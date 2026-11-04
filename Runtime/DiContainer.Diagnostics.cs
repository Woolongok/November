using System;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace November.DI
{
    /// <summary>
    /// Diagnostics partition: the resolution stack (cycle detection and error paths) and construction of
    /// every error the container throws. Errors are thrown once, with the full path, instead of being
    /// logged at each level on the way up.
    /// </summary>
    public sealed partial class DiContainer
    {
        /// <summary>
        /// Key under which the resolution path is attached to exceptions that pass through the container
        /// (<c>ex.Data[DiContainer.ResolutionPathKey]</c>), so a constructor that throws deep in the graph
        /// stays diagnosable without the container logging anything or wrapping the exception.
        /// </summary>
        public const string ResolutionPathKey = "November.DI.ResolutionPath";

        // ── Resolution stack ────────────────────────────────────────────────

        /// <summary>Pushes a frame for the registration; throws if this exact registration is already being built by this scope.</summary>
        private int EnterFrame(ServiceDescriptor descriptor)
        {
            var stack = _state.Stack;
            for (int i = 0; i < stack.Count; i++)
            {
                var frame = stack[i];
                if (ReferenceEquals(frame.Descriptor, descriptor) && ReferenceEquals(frame.Scope, this))
                    throw Circular(descriptor);
            }

            int mark = stack.Count;
            stack.Add(new ResolutionFrame(descriptor, this, descriptor.ServiceType));
            return mark;
        }

        private void LeaveFrame(int mark)
        {
            var stack = _state.Stack;
            if (stack.Count > mark) stack.RemoveRange(mark, stack.Count - mark);
        }

        private void SetMember(object member)
        {
            var stack = _state.Stack;
            int top = stack.Count - 1;
            if (top >= 0) stack[top] = stack[top].WithMember(member);
        }

        private string BuildPath(Type next = null)
        {
            var stack = _state.Stack;
            var sb = new StringBuilder(64);
            for (int i = 0; i < stack.Count; i++)
            {
                if (i > 0) sb.Append(" -> ");
                var frame = stack[i];
                sb.Append(PrettyName(frame.Type));
                var d = frame.Descriptor;
                if (d != null && d.Kind == DescriptorKind.Type && d.ImplementationType != d.ServiceType)
                    sb.Append(" (").Append(PrettyName(d.ImplementationType)).Append(')');
            }

            if (next != null)
            {
                if (sb.Length > 0) sb.Append(" -> ");
                sb.Append(PrettyName(next));
            }
            return sb.ToString();
        }

        private static string DescribeMember(object member)
        {
            if (member is ParameterInfo parameter)
            {
                var owner = parameter.Member;
                string where = owner is ConstructorInfo ? "constructor" : "method '" + owner.Name + "'";
                return $"parameter '{parameter.Name}' of {where} on {PrettyName(owner.DeclaringType)}";
            }
            if (member is FieldInfo field)
                return $"field '{field.Name}' on {PrettyName(field.DeclaringType)}";
            return null;
        }

        /// <summary>Attaches the current resolution path to an exception, once (the innermost level wins).</summary>
        private void Annotate(Exception ex)
        {
            try
            {
                if (!ex.Data.Contains(ResolutionPathKey)) ex.Data[ResolutionPathKey] = BuildPath();
            }
            catch
            {
                // Some exception types expose read-only Data; the path is a convenience, never worth masking the error.
            }
        }

        // ── Errors ──────────────────────────────────────────────────────────

        private Exception NotRegistered(Type missing, Type via = null)
        {
            var sb = new StringBuilder(224);
            if (!missing.IsAbstract && !missing.IsInterface)
                sb.Append("No registration found for concrete type '").Append(missing.FullName)
                  .Append("'. Register it via Register<TService, TImpl>(), RegisterInstance<T>() or RegisterFactory<T>() (concrete types are never auto-created).");
            else
                sb.Append("No registration found for '").Append(missing.FullName)
                  .Append("'. Ensure it is registered before resolving.");

            if (via != null) sb.Append(" It is needed by ").Append(PrettyName(via)).Append('.');

            var stack = _state.Stack;
            if (stack.Count > 0)
            {
                var member = DescribeMember(stack[stack.Count - 1].Member);
                if (member != null) sb.Append(" Required by ").Append(member).Append('.');
                sb.Append(" Resolution path: ").Append(BuildPath(missing)).Append('.');
            }

            var error = new InvalidOperationException(sb.ToString());
            try { error.Data[ResolutionPathKey] = BuildPath(missing); }
            catch { /* see Annotate: diagnostics must never mask the original error */ }
            return error;
        }

        private Exception Circular(ServiceDescriptor descriptor)
            => new InvalidOperationException(
                $"Circular dependency detected while resolving '{PrettyName(descriptor.ServiceType)}'. " +
                $"Path: {BuildPath(descriptor.ServiceType)}. " +
                "Use Lazy<T> or Func<T> to break the cycle.");

        private Exception ScopedFromRoot(ServiceDescriptor scoped)
        {
            ServiceDescriptor consumer = null;
            var stack = _state.Stack;
            for (int i = stack.Count - 1; i >= 0 && consumer == null; i--)
                consumer = stack[i].Descriptor;

            string what = consumer != null && consumer.Lifetime == Lifetime.Singleton
                ? $"Captive dependency: singleton '{PrettyName(consumer.ServiceType)}' depends on scoped service " +
                  $"'{PrettyName(scoped.ServiceType)}', which the root container would keep alive forever."
                : $"Scoped service '{PrettyName(scoped.ServiceType)}' cannot be resolved from the root container " +
                  "(it would behave like a singleton). Resolve it from a scope created with CreateScope().";

            return new InvalidOperationException(
                $"[DiContainer] {what} Path: {BuildPath(scoped.ServiceType)}. " +
                "(Turn this check off with DiContainerOptions.ValidateScopes = false.)");
        }

        private static Exception OwnerDisposed(ServiceDescriptor singleton)
            => new ObjectDisposedException(nameof(DiContainer),
                $"Cannot resolve singleton '{PrettyName(singleton.ServiceType)}': the container that owns it has been disposed " +
                "(a parent scope?). Dispose child scopes before their parent, or register the service in the child scope.");

        // ── Once-only warnings ──────────────────────────────────────────────

        private void WarnOptionalFallback(ParameterInfo parameter)
        {
            if (!_state.FirstOptionalFallback(parameter)) return;
            Debug.LogError(
                $"[DiContainer] No registration for optional parameter '{parameter.Name}' ({PrettyName(parameter.ParameterType)}) " +
                $"on '{PrettyName(parameter.Member.DeclaringType)}' — falling back to its default value. " +
                "Register it or make the dependency required.");
        }

        private void WarnIfRootTransientDisposable(object instance)
        {
            if (!(instance is IDisposable) && !(instance is IAsyncDisposable)) return;

            // A disposable built as a dependency of a singleton is held by that singleton: not a leak.
            var stack = _state.Stack;
            for (int i = 0; i < stack.Count; i++)
            {
                var d = stack[i].Descriptor;
                if (d != null && d.Kind != DescriptorKind.Alias && d.Lifetime == Lifetime.Singleton) return;
            }

            var type = instance.GetType();
            if (!_state.FirstTransientDisposable(type)) return;
            Debug.LogWarning(
                $"[DiContainer] Transient disposable '{PrettyName(type)}' was resolved from the root container, which keeps it " +
                "until the root itself is disposed. Resolve it through a short-lived scope (CreateScope) so it is released with " +
                "that scope. (DiContainerOptions.WarnOnRootTransientDisposables)");
        }
    }
}
