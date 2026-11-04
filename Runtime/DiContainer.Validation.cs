using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace November.DI
{
    /// <summary>
    /// Validation partition: an eager, side-effect-free walk of the registered constructor graph
    /// (<see cref="Validate"/>) and a readable dump of what is registered (<see cref="DescribeRegistrations"/>).
    /// Meant for editor tooling, startup checks in development builds and tests.
    ///
    /// The walk mirrors resolution:
    /// - a singleton is constructed from the registrations of the container that owns it;
    /// - scoped and transient services are constructed from the resolving scope;
    /// - aliases are graph edges, not lifetime boundaries;
    /// - Func&lt;T&gt; / Lazy&lt;T&gt; targets are validated as deferred edges (they cannot create an
    ///   eager cycle, but they can still capture a scoped service or need a missing registration);
    /// - collections expand exactly like ResolveAll, ancestors first;
    /// - completed work is tracked by descriptor + resolution scope + capturing singleton, so the
    ///   same descriptor is not assumed healthy in a different scope context.
    ///
    /// Factories are opaque: their delegate body cannot be inspected. A factory registration is
    /// still checked for its own lifetime and is called at runtime like any other registration.
    /// </summary>
    public sealed partial class DiContainer
    {
        /// <summary>
        /// Whether a dependency edge is resolved immediately during construction or only when the
        /// returned Func / Lazy is invoked. Deferred edges still have to be valid; they just do not
        /// participate in eager cycle detection.
        /// </summary>
        private enum DependencyKind
        {
            Eager,
            Deferred
        }

        /// <summary>
        /// The context in which a descriptor's graph is validated. Two visits of the same descriptor
        /// are only equivalent when the resolution scope and the singleton whose lifetime captures
        /// the result are the same.
        /// </summary>
        private readonly struct ValidationKey : IEquatable<ValidationKey>
        {
            public readonly ServiceDescriptor Descriptor;
            public readonly DiContainer Scope;
            public readonly ServiceDescriptor CapturingSingleton;

            public ValidationKey(ServiceDescriptor descriptor, DiContainer scope, ServiceDescriptor capturingSingleton)
            {
                Descriptor = descriptor;
                Scope = scope;
                CapturingSingleton = capturingSingleton;
            }

            public bool Equals(ValidationKey other)
                => ReferenceEquals(Descriptor, other.Descriptor)
                   && ReferenceEquals(Scope, other.Scope)
                   && ReferenceEquals(CapturingSingleton, other.CapturingSingleton);

            public override bool Equals(object obj) => obj is ValidationKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = RuntimeHelpers.GetHashCode(Descriptor);
                    hash = (hash * 397) ^ RuntimeHelpers.GetHashCode(Scope);
                    hash = (hash * 397) ^ (CapturingSingleton == null ? 0 : RuntimeHelpers.GetHashCode(CapturingSingleton));
                    return hash;
                }
            }
        }

        private readonly struct ValidationFrame
        {
            public readonly ValidationKey Key;
            public readonly ServiceDescriptor Descriptor;

            public ValidationFrame(ValidationKey key, ServiceDescriptor descriptor)
            {
                Key = key;
                Descriptor = descriptor;
            }
        }

        /// <summary>Per-pass state: eager path, in-progress/completed keys, and the diagnostics collected so far.</summary>
        private sealed class ValidationState
        {
            // Swapped for an empty set at a deferred edge: the outer eager chain cannot be completed
            // through a Func / Lazy, so it must not be reported as an eager cycle.
            public HashSet<ValidationKey> Active = new HashSet<ValidationKey>();
            public readonly HashSet<ValidationKey> InProgress = new HashSet<ValidationKey>();
            public readonly HashSet<ValidationKey> Completed = new HashSet<ValidationKey>();
            public readonly List<ValidationFrame> Path = new List<ValidationFrame>(16);
            public readonly List<string> Errors = new List<string>();
            public readonly HashSet<string> Reported = new HashSet<string>();
        }

        /// <summary>The construction view for one descriptor: who resolves it and which singleton (if any) keeps it alive.</summary>
        private sealed class ValidationContext
        {
            public readonly DiContainer ResolutionScope;
            public readonly ServiceDescriptor CapturingSingleton;
            public readonly ValidationState State;

            public ValidationContext(DiContainer resolutionScope, ServiceDescriptor capturingSingleton, ValidationState state)
            {
                ResolutionScope = resolutionScope;
                CapturingSingleton = capturingSingleton;
                State = state;
            }
        }

        /// <summary>
        /// Checks every registration visible from this scope (its own and its ancestors') without constructing
        /// anything and throws one <see cref="InvalidOperationException"/> listing every problem:
        /// unregistered dependencies (constructor, [Inject] field, [Inject] method), ambiguous constructors,
        /// dependency cycles, singletons that capture scoped services, scoped services resolved from the root,
        /// dead instances and dangling aliases. Factories are opaque and are not analysed. Func&lt;T&gt; /
        /// Lazy&lt;T&gt; edges are deferred: their targets are validated, but they are not eager cycles.
        /// </summary>
        public void Validate()
        {
            ThrowIfDisposed();
            MainThread.Assert(nameof(Validate));

            var errors = CollectValidationErrors();
            if (errors.Count == 0) return;

            var sb = new StringBuilder("[DiContainer] Validation found ").Append(errors.Count).Append(" problem(s):");
            for (int i = 0; i < errors.Count; i++) sb.Append("\n - ").Append(errors[i]);
            throw new InvalidOperationException(sb.ToString());
        }

        /// <summary>Registrations by scope level (root first), sorted, one per line.</summary>
        public string DescribeRegistrations()
        {
            ThrowIfDisposed();
            MainThread.Assert(nameof(DescribeRegistrations));

            var chain = new List<DiContainer>();
            for (var scope = this; scope != null; scope = scope._parent) chain.Add(scope);
            chain.Reverse();

            var sb = new StringBuilder();
            for (int level = 0; level < chain.Count; level++)
            {
                var scope = chain[level];
                sb.Append(level == 0 ? "[root]" : "[scope " + level + "]");
                if (scope == this) sb.Append(" (this)");
                if (scope._disposed) sb.Append(" (disposed)");
                sb.Append('\n');

                foreach (var type in SortedKeys(scope))
                    foreach (var descriptor in scope._registry[type])
                        sb.Append("  ").Append(PrettyName(type)).Append(" -> ").Append(descriptor.Describe()).Append('\n');
            }
            return sb.ToString();
        }

        internal List<string> CollectValidationErrors()
        {
            var state = new ValidationState();

            // Every registration visible from this scope is checked as if it were resolved here. A
            // singleton switches to its owner's view as soon as ValidateDescriptor enters it.
            for (var scope = this; scope != null; scope = scope._parent)
                foreach (var type in SortedKeys(scope))
                    foreach (var descriptor in scope._registry[type])
                        ValidateDescriptor(descriptor, scope, DependencyKind.Eager,
                            new ValidationContext(this, null, state));

            return state.Errors;
        }

        private static List<Type> SortedKeys(DiContainer scope)
        {
            var keys = new List<Type>();
            if (scope._registry != null) keys.AddRange(scope._registry.Keys);
            keys.Sort((a, b) => string.CompareOrdinal(PrettyName(a), PrettyName(b)));
            return keys;
        }

        private void ValidateDescriptor(ServiceDescriptor descriptor, DiContainer owner, DependencyKind kind, ValidationContext context)
        {
            var state = context.State;

            // A singleton is built by the container that owns its registration, from that container's
            // view; everything else is built by the scope that is resolving it right now.
            bool buildsSingleton = descriptor.Lifetime == Lifetime.Singleton
                                   && (descriptor.Kind == DescriptorKind.Type || descriptor.Kind == DescriptorKind.Factory);
            var constructionScope = buildsSingleton ? owner : context.ResolutionScope;
            var capturingSingleton = buildsSingleton ? descriptor : context.CapturingSingleton;
            var key = new ValidationKey(descriptor, constructionScope, capturingSingleton);

            // Deferred edges start a fresh eager path. A cycle that only exists by invoking the
            // deferred value later is not an eager construction cycle.
            HashSet<ValidationKey> savedActive = null;
            if (kind == DependencyKind.Deferred)
            {
                savedActive = state.Active;
                state.Active = new HashSet<ValidationKey>();
            }

            try
            {
                if (state.Active.Contains(key))
                {
                    ReportCircular(key, descriptor, state);
                    return;
                }

                // InProgress catches a re-entry through a deferred edge (LazyA -> Func<LazyB> ->
                // LazyB -> LazyA). That graph is valid: no eager path completes the loop.
                if (state.InProgress.Contains(key) || state.Completed.Contains(key)) return;

                state.InProgress.Add(key);
                state.Active.Add(key);

                var frame = new ValidationFrame(key, descriptor);
                state.Path.Add(frame);
                try
                {
                    // The construction view can change at a singleton boundary; keep it for the
                    // whole subtree so transient hops cannot hide a captive scoped dependency.
                    var inner = new ValidationContext(constructionScope, capturingSingleton, state);
                    CheckScopedLifetime(descriptor, constructionScope, capturingSingleton, state);

                    switch (descriptor.Kind)
                    {
                        case DescriptorKind.Instance:
                            if (descriptor.IsDestroyedInstance)
                                Report($"The registered instance for '{PrettyName(descriptor.ServiceType)}' was destroyed.",
                                    state);
                            break;

                        case DescriptorKind.Alias:
                            ValidateAlias(descriptor, inner);
                            break;

                        case DescriptorKind.Type:
                            ValidateType(descriptor, inner);
                            break;

                        // Factory: the delegate is opaque. Its own lifetime was checked above.
                    }
                }
                finally
                {
                    state.Path.RemoveAt(state.Path.Count - 1);
                    state.Active.Remove(key);
                    state.InProgress.Remove(key);
                    state.Completed.Add(key);
                }
            }
            finally
            {
                if (savedActive != null) state.Active = savedActive;
            }
        }

        private void CheckScopedLifetime(ServiceDescriptor descriptor, DiContainer constructionScope,
                                         ServiceDescriptor capturingSingleton, ValidationState state)
        {
            if (descriptor.Lifetime != Lifetime.Scoped) return;
            if (descriptor.Kind != DescriptorKind.Type && descriptor.Kind != DescriptorKind.Factory) return;

            // Scope validation is a root-container rule: a child scope gives a scoped service exactly
            // the lifetime of the objects it resolves. A child-owned singleton and a child scoped
            // service live and die together, so that combination is valid at runtime and here.
            if (constructionScope._parent != null || !constructionScope._options.ValidateScopes) return;

            if (capturingSingleton != null)
                Report(
                    $"Captive dependency: singleton '{PrettyName(capturingSingleton.ServiceType)}' captures scoped " +
                    $"'{PrettyName(descriptor.ServiceType)}'. Path: {FormatPath(null, state)}.",
                    state);
            else
                Report(
                    $"Scoped service '{PrettyName(descriptor.ServiceType)}' cannot be resolved from the root container " +
                    $"(it would behave like a singleton). Path: {FormatPath(null, state)}. " +
                    "Create a scope with CreateScope() or turn this check off with DiContainerOptions.ValidateScopes = false.",
                    state);
        }

        private void ValidateAlias(ServiceDescriptor alias, ValidationContext context)
        {
            var target = alias.ImplementationType;

            // An alias is an edge in the current graph: it must not reset the resolution scope or
            // the singleton that is capturing the result.
            if (context.ResolutionScope.FindDescriptor(target, out var targetDescriptor, out var targetOwner))
            {
                ValidateDescriptor(targetDescriptor, targetOwner, DependencyKind.Eager, context);
                return;
            }

            if (TryValidateImplicit(target, context, alias.ServiceType, "alias target")) return;

            Report($"Alias '{PrettyName(alias.ServiceType)}' points at '{PrettyName(target)}', which is not registered.", context.State);
        }

        private void ValidateType(ServiceDescriptor descriptor, ValidationContext context)
        {
            TypePlan plan;
            try
            {
                plan = InjectionMetadata.GetPlan(descriptor.ImplementationType);
                _ = plan.Constructor; // surfaces "ambiguous" / "no accessible constructor"
            }
            catch (InvalidOperationException ex)
            {
                Report(ex.Message, context.State);
                return;
            }

            var constructorParameters = plan.ConstructorParameters;
            for (int i = 0; i < constructorParameters.Length; i++)
            {
                var p = constructorParameters[i];
                ValidateDependency(context, descriptor.ServiceType, p.ParameterType, p.HasDefaultValue,
                    "constructor parameter '" + p.Name + "'", DependencyKind.Eager);
            }

            foreach (var field in plan.InjectFields)
                ValidateDependency(context, descriptor.ServiceType, field.FieldType, false,
                    "[Inject] field '" + field.Name + "'", DependencyKind.Eager);

            foreach (var method in plan.InjectMethods)
                foreach (var p in method.Parameters)
                    ValidateDependency(context, descriptor.ServiceType, p.ParameterType, p.HasDefaultValue,
                        "parameter '" + p.Name + "' of [Inject] method '" + method.Method.Name + "'", DependencyKind.Eager);
        }

        private void ValidateDependency(ValidationContext context, Type consumerType, Type dependency, bool optional,
                                        string what, DependencyKind kind)
        {
            var scope = context.ResolutionScope;

            // Mirrors ResolveParameter: an optional parameter falls back to its default when the
            // whole dependency type cannot be resolved, including Func / Lazy over a missing service.
            if (optional && !scope.CanResolve(dependency)) return;

            // An explicit registration always wins, including an explicit Func<T> / Lazy<T> /
            // collection registration. It is resolved eagerly as the dependency value.
            if (scope.FindDescriptor(dependency, out var found, out var foundOwner))
            {
                ValidateDescriptor(found, foundOwner, kind, context);
                return;
            }

            if (TryValidateImplicit(dependency, context, consumerType, what)) return;

            Report($"'{PrettyName(consumerType)}' ({what}) needs '{PrettyName(dependency)}', which is not registered.", context.State);
        }

        /// <summary>
        /// Validates the implicit shapes ResolveImplicit supports. Returns false when the type is not
        /// an implicit wrapper, so the caller can report it as an ordinary missing registration.
        /// </summary>
        private bool TryValidateImplicit(Type type, ValidationContext context, Type consumerType, string what)
        {
            if (type.IsArray)
            {
                var element = type.GetElementType();
                if (type.GetArrayRank() != 1 || element.IsValueType) return false;
                ValidateCollection(element, context);
                return true;
            }

            if (!type.IsGenericType) return false;

            var definition = type.GetGenericTypeDefinition();
            var argument = type.GetGenericArguments()[0];

            if (definition == typeof(Func<>) || definition == typeof(Lazy<>))
            {
                // The target may be missing even though the wrapper itself is requested. The
                // wrapper is created immediately for an eager edge, but the target is resolved
                // later; validate it as a deferred edge.
                ValidateDependency(context, consumerType, argument, optional: false, what, DependencyKind.Deferred);
                return true;
            }

            if (!IsCollectionDefinition(definition) || argument.IsValueType) return false;

            ValidateCollection(argument, context);
            return true;
        }

        /// <summary>Expands an implicit collection exactly like ResolveAll: ancestor registrations first.</summary>
        private void ValidateCollection(Type elementType, ValidationContext context)
        {
            var chain = new List<DiContainer>();
            for (var scope = context.ResolutionScope; scope != null; scope = scope._parent) chain.Add(scope);
            chain.Reverse();

            for (int level = 0; level < chain.Count; level++)
            {
                var scope = chain[level];
                if (scope._registry == null || !scope._registry.TryGetValue(elementType, out var bindings)) continue;

                for (int i = 0; i < bindings.Count; i++)
                    ValidateDescriptor(bindings[i], scope, DependencyKind.Eager, context);
            }
        }

        private void ReportCircular(ValidationKey key, ServiceDescriptor descriptor, ValidationState state)
        {
            int start = -1;
            for (int i = 0; i < state.Path.Count; i++)
            {
                if (state.Path[i].Key.Equals(key))
                {
                    start = i;
                    break;
                }
            }

            var sb = new StringBuilder("Circular dependency: ");
            if (start >= 0)
            {
                for (int i = start; i < state.Path.Count; i++)
                    sb.Append(PrettyName(state.Path[i].Descriptor.ServiceType)).Append(" -> ");
            }
            sb.Append(PrettyName(descriptor.ServiceType));
            Report(sb.ToString(), state);
        }

        private static string FormatPath(Type last, ValidationState state)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < state.Path.Count; i++)
            {
                if (i > 0) sb.Append(" -> ");
                sb.Append(PrettyName(state.Path[i].Descriptor.ServiceType));
            }
            if (last != null)
            {
                if (sb.Length > 0) sb.Append(" -> ");
                sb.Append(PrettyName(last));
            }
            return sb.ToString();
        }

        private static void Report(string message, ValidationState state)
        {
            if (state.Reported.Add(message)) state.Errors.Add(message);
        }
    }
}
