using System;
using System.Collections.Generic;
using System.Text;

namespace November.DI
{
    /// <summary>
    /// Validation partition: an eager, side-effect-free walk of the registered constructor graph
    /// (<see cref="Validate"/>) and a readable dump of what is registered (<see cref="DescribeRegistrations"/>).
    /// Meant for editor tooling, startup checks in development builds and tests.
    /// </summary>
    public sealed partial class DiContainer
    {
        /// <summary>
        /// Checks every registration visible from this scope (its own and its ancestors') without constructing
        /// anything and throws one <see cref="InvalidOperationException"/> listing every problem:
        /// unregistered dependencies (constructor, [Inject] field, [Inject] method), ambiguous constructors,
        /// dependency cycles, singletons that capture scoped services, dead instances and dangling aliases.
        /// Factories are opaque and are not analysed. Func&lt;T&gt; / Lazy&lt;T&gt; edges are lazy and not cycles.
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
            var errors = new List<string>();
            var reported = new HashSet<string>();
            var done = new HashSet<ServiceDescriptor>();
            var active = new List<ServiceDescriptor>();

            for (var scope = this; scope != null; scope = scope._parent)
                foreach (var type in SortedKeys(scope))
                    foreach (var descriptor in scope._registry[type])
                        ValidateDescriptor(descriptor, scope, done, active, errors, reported);

            return errors;
        }

        private static List<Type> SortedKeys(DiContainer scope)
        {
            var keys = new List<Type>();
            if (scope._registry != null) keys.AddRange(scope._registry.Keys);
            keys.Sort((a, b) => string.CompareOrdinal(PrettyName(a), PrettyName(b)));
            return keys;
        }

        private void ValidateDescriptor(ServiceDescriptor descriptor, DiContainer owner, HashSet<ServiceDescriptor> done,
                                        List<ServiceDescriptor> active, List<string> errors, HashSet<string> reported)
        {
            if (done.Contains(descriptor)) return;

            int cycleStart = active.IndexOf(descriptor);
            if (cycleStart >= 0)
            {
                var sb = new StringBuilder("Circular dependency: ");
                for (int i = cycleStart; i < active.Count; i++)
                    sb.Append(PrettyName(active[i].ServiceType)).Append(" -> ");
                sb.Append(PrettyName(descriptor.ServiceType));
                Report(sb.ToString(), errors, reported);
                return;
            }

            active.Add(descriptor);
            try
            {
                switch (descriptor.Kind)
                {
                    case DescriptorKind.Instance:
                        if (descriptor.IsDestroyedInstance)
                            Report($"The registered instance for '{PrettyName(descriptor.ServiceType)}' was destroyed.", errors, reported);
                        break;

                    case DescriptorKind.Alias:
                        ValidateAlias(descriptor, done, active, errors, reported);
                        break;

                    case DescriptorKind.Type:
                        ValidateType(descriptor, owner, done, active, errors, reported);
                        break;
                }
            }
            finally
            {
                active.RemoveAt(active.Count - 1);
                done.Add(descriptor);
            }
        }

        private void ValidateAlias(ServiceDescriptor alias, HashSet<ServiceDescriptor> done, List<ServiceDescriptor> active,
                                   List<string> errors, HashSet<string> reported)
        {
            var target = alias.ImplementationType;
            if (FindDescriptor(target, out var targetDescriptor, out var targetOwner))
                ValidateDescriptor(targetDescriptor, targetOwner, done, active, errors, reported);
            else if (!CanResolve(target))
                Report($"Alias '{PrettyName(alias.ServiceType)}' points at '{PrettyName(target)}', which is not registered.", errors, reported);
        }

        private void ValidateType(ServiceDescriptor descriptor, DiContainer owner, HashSet<ServiceDescriptor> done,
                                  List<ServiceDescriptor> active, List<string> errors, HashSet<string> reported)
        {
            TypePlan plan;
            try
            {
                plan = InjectionMetadata.GetPlan(descriptor.ImplementationType);
                _ = plan.Constructor; // surfaces "ambiguous" / "no accessible constructor"
            }
            catch (InvalidOperationException ex)
            {
                Report(ex.Message, errors, reported);
                return;
            }

            // Singletons are built from their owner's view of the registrations; everything else from the resolving scope.
            var view = descriptor.Lifetime == Lifetime.Singleton ? owner : this;
            var consumer = descriptor;

            var constructorParameters = plan.ConstructorParameters;
            for (int i = 0; i < constructorParameters.Length; i++)
            {
                var p = constructorParameters[i];
                ValidateDependency(consumer, owner, view, p.ParameterType, p.HasDefaultValue, "constructor parameter '" + p.Name + "'", done, active, errors, reported);
            }

            foreach (var field in plan.InjectFields)
                ValidateDependency(consumer, owner, view, field.FieldType, false, "[Inject] field '" + field.Name + "'", done, active, errors, reported);

            foreach (var method in plan.InjectMethods)
                foreach (var p in method.Parameters)
                    ValidateDependency(consumer, owner, view, p.ParameterType, p.HasDefaultValue,
                        "parameter '" + p.Name + "' of [Inject] method '" + method.Method.Name + "'", done, active, errors, reported);
        }

        private void ValidateDependency(ServiceDescriptor consumer, DiContainer owner, DiContainer view, Type dependency, bool optional,
                                        string what, HashSet<ServiceDescriptor> done, List<ServiceDescriptor> active,
                                        List<string> errors, HashSet<string> reported)
        {
            var effective = dependency;
            bool lazy = false;
            if (dependency.IsGenericType)
            {
                var definition = dependency.GetGenericTypeDefinition();
                if (definition == typeof(Func<>) || definition == typeof(Lazy<>))
                {
                    effective = dependency.GetGenericArguments()[0];
                    lazy = true;
                }
            }

            if (view.FindDescriptor(effective, out var found, out var foundOwner))
            {
                // A singleton owned by the root lives forever; a scoped service it holds would be the root's copy.
                if (!lazy && consumer.Lifetime == Lifetime.Singleton && owner._parent == null && found.Lifetime == Lifetime.Scoped
                    && found.Kind != DescriptorKind.Instance && found.Kind != DescriptorKind.Alias)
                    Report($"Captive dependency: singleton '{PrettyName(consumer.ServiceType)}' ({what}) depends on scoped '{PrettyName(found.ServiceType)}'.", errors, reported);

                if (!lazy) ValidateDescriptor(found, foundOwner, done, active, errors, reported);
                return;
            }

            if (optional || view.CanResolve(dependency)) return;

            Report($"'{PrettyName(consumer.ServiceType)}' ({what}) needs '{PrettyName(dependency)}', which is not registered.", errors, reported);
        }

        private static void Report(string message, List<string> errors, HashSet<string> reported)
        {
            if (reported.Add(message)) errors.Add(message);
        }
    }
}
