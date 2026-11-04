using System;
using System.Collections.Generic;
using UnityEngine;

namespace November.DI
{
    /// <summary>
    /// Registration partition: Register / Add / Replace for types, instances, factories and aliases.
    /// </summary>
    public sealed partial class DiContainer
    {
        private enum RegistrationMode
        {
            /// <summary>Single binding: a second one in the same container is a duplicate.</summary>
            Register,
            /// <summary>Swap every existing binding for the type.</summary>
            Replace,
            /// <summary>Append (multi-binding).</summary>
            Append
        }

        // ── Types ───────────────────────────────────────────────────────────
        public void Register<TService, TImpl>(Lifetime lifetime = Lifetime.Transient)
            where TImpl : class, TService
            => RegisterType(typeof(TService), typeof(TImpl), lifetime, RegistrationMode.Register);

        public void Register(Type serviceType, Type implType, Lifetime lifetime = Lifetime.Transient)
            => RegisterType(serviceType, implType, lifetime, RegistrationMode.Register);

        public void Add(Type serviceType, Type implType, Lifetime lifetime = Lifetime.Transient)
            => RegisterType(serviceType, implType, lifetime, RegistrationMode.Append);

        public void Replace(Type serviceType, Type implType, Lifetime lifetime = Lifetime.Transient)
            => RegisterType(serviceType, implType, lifetime, RegistrationMode.Replace);

        // ── Instances ───────────────────────────────────────────────────────
        public void RegisterInstance<TService>(TService instance)
            => RegisterInstanceCore(typeof(TService), instance, RegistrationMode.Register);

        public void RegisterInstance(Type serviceType, object instance)
            => RegisterInstanceCore(serviceType, instance, RegistrationMode.Register);

        public void ReplaceInstance(Type serviceType, object instance)
            => RegisterInstanceCore(serviceType, instance, RegistrationMode.Replace);

        // ── Factories ───────────────────────────────────────────────────────
        public void RegisterFactory<TService>(Func<IDiProvider, TService> factory, Lifetime lifetime = Lifetime.Transient)
        {
            ThrowIfDisposed();
            MainThread.Assert(nameof(RegisterFactory));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            RegisterFactoryCore(typeof(TService), p => factory(p), lifetime, RegistrationMode.Register);
        }

        public void RegisterFactory(Type serviceType, Func<IDiProvider, object> factory, Lifetime lifetime = Lifetime.Transient)
            => RegisterFactoryCore(serviceType, factory, lifetime, RegistrationMode.Register);

        public void ReplaceFactory(Type serviceType, Func<IDiProvider, object> factory, Lifetime lifetime = Lifetime.Transient)
            => RegisterFactoryCore(serviceType, factory, lifetime, RegistrationMode.Replace);

        // ── Aliases ─────────────────────────────────────────────────────────
        public void RegisterAlias(Type aliasType, Type targetType)
        {
            ThrowIfDisposed();
            MainThread.Assert(nameof(RegisterAlias));
            if (aliasType == null) throw new ArgumentNullException(nameof(aliasType));
            if (targetType == null) throw new ArgumentNullException(nameof(targetType));
            ValidateServiceType(aliasType, nameof(aliasType));
            ValidateServiceType(targetType, nameof(targetType));
            if (aliasType == targetType)
                throw new ArgumentException($"An alias cannot point at itself ('{aliasType.FullName}').", nameof(targetType));

            // Reject a chain that already loops. Aliases whose target is registered later stay
            // legal: the walk stops at the first registration that is not itself an alias.
            ValidateAliasChain(aliasType, targetType);

            AddDescriptor(aliasType, ServiceDescriptor.ForAlias(aliasType, targetType), RegistrationMode.Register);
        }

        // ── Shared implementation ───────────────────────────────────────────
        private void RegisterType(Type serviceType, Type implType, Lifetime lifetime, RegistrationMode mode)
        {
            ThrowIfDisposed();
            MainThread.Assert(nameof(Register));
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (implType == null) throw new ArgumentNullException(nameof(implType));
            ValidateServiceType(serviceType, nameof(serviceType));
            ValidateLifetime(lifetime);

            if (implType.IsAbstract || implType.IsInterface)
                throw new ArgumentException(
                    $"Implementation type '{implType.FullName}' must be a concrete, non-abstract type.",
                    nameof(implType));
            if (implType.IsValueType || implType.ContainsGenericParameters || IsUnsupportedMetadataType(implType))
                throw new ArgumentException(
                    $"Cannot register '{serviceType.FullName}' -> '{implType.FullName}': implementations must be closed classes " +
                    "(structs, open generic types, pointers and by-ref types are not supported; register the closed type).",
                    nameof(implType));
            if (implType.IsArray || typeof(Delegate).IsAssignableFrom(implType))
                throw new ArgumentException(
                    $"'{implType.FullName}' cannot be constructed by the container. Register a concrete class, an instance, " +
                    "or a factory instead.",
                    nameof(implType));
            if (!serviceType.IsAssignableFrom(implType))
                throw new ArgumentException(
                    $"'{implType.FullName}' does not implement or extend '{serviceType.FullName}'.");
            AssertNotUnityObject(implType);

            AddDescriptor(serviceType, ServiceDescriptor.ForType(serviceType, implType, lifetime), mode);
        }

        private void RegisterInstanceCore(Type serviceType, object instance, RegistrationMode mode)
        {
            ThrowIfDisposed();
            MainThread.Assert(nameof(RegisterInstance));
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            ValidateServiceType(serviceType, nameof(serviceType));
            if (instance == null || IsDestroyedUnityObject(instance))
                throw new ArgumentNullException(nameof(instance), "Cannot register a null or destroyed Unity object.");
            if (!serviceType.IsInstanceOfType(instance))
                throw new ArgumentException(
                    $"Instance of '{instance.GetType().FullName}' is not assignable to '{serviceType.FullName}'.",
                    nameof(instance));

            AddDescriptor(serviceType, ServiceDescriptor.ForInstance(serviceType, instance), mode);

            // Caller-owned: a factory that forwards this object later must not claim it, and this
            // container must not dispose it even if it was previously tracked by the same scope.
            // Both steps happen only after AddDescriptor succeeded, so a duplicate registration
            // cannot silently transfer ownership of an object the container already owns.
            ObjectOwnership.MarkExternal(instance);
            ForgetTrackedInstance(instance);
        }

        private void RegisterFactoryCore(Type serviceType, Func<IDiProvider, object> factory, Lifetime lifetime, RegistrationMode mode)
        {
            ThrowIfDisposed();
            MainThread.Assert(nameof(RegisterFactory));
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            ValidateServiceType(serviceType, nameof(serviceType));
            ValidateLifetime(lifetime);

            AddDescriptor(serviceType, ServiceDescriptor.ForFactory(serviceType, factory, lifetime), mode);
        }

        private static void ValidateServiceType(Type serviceType, string parameterName)
        {
            if (IsUnsupportedMetadataType(serviceType))
                throw new ArgumentException(
                    $"'{serviceType}' cannot be used as a service type. Service types must be closed, non-void, " +
                    "non-pointer and non-by-ref types; open generic definitions are not supported.",
                    parameterName);
        }

        private static bool IsUnsupportedMetadataType(Type type)
            => type == typeof(void) || type.IsByRef || type.IsPointer || type.IsByRefLike || type.ContainsGenericParameters;

        private static void ValidateLifetime(Lifetime lifetime)
        {
            if (lifetime != Lifetime.Transient && lifetime != Lifetime.Singleton && lifetime != Lifetime.Scoped)
                throw new ArgumentOutOfRangeException(
                    nameof(lifetime), lifetime,
                    $"Lifetime value {(int)lifetime} is not defined. Use Transient, Singleton or Scoped.");
        }

        /// <summary>
        /// Walks the registrations the new alias would forward through. A loop through existing
        /// aliases is rejected now instead of surfacing as a circular dependency on first resolve.
        /// </summary>
        private void ValidateAliasChain(Type aliasType, Type targetType)
        {
            var visited = new HashSet<Type>();
            var current = targetType;
            while (current != null && visited.Add(current))
            {
                if (current == aliasType)
                    throw new ArgumentException(
                        $"Alias '{aliasType.FullName}' -> '{targetType.FullName}' would create a circular alias chain.",
                        nameof(targetType));

                if (!FindDescriptor(current, out var descriptor, out _) || descriptor.Kind != DescriptorKind.Alias)
                    return;

                current = descriptor.ImplementationType;
            }
        }

        private void AddDescriptor(Type serviceType, ServiceDescriptor descriptor, RegistrationMode mode)
        {
            if (_registry == null) _registry = new Dictionary<Type, List<ServiceDescriptor>>();

            if (!_registry.TryGetValue(serviceType, out var list) || list.Count == 0 || mode == RegistrationMode.Append)
            {
                if (list == null) _registry[serviceType] = list = new List<ServiceDescriptor>(1);
                list.Add(descriptor);
                return;
            }

            // An owner may re-register a scene object whose previous registration died with its scene.
            bool replacesDeadInstance = list.Count == 1 && list[0].IsDestroyedInstance;

            if (mode == RegistrationMode.Register && !replacesDeadInstance)
            {
                // Legacy behaviour only for type/factory/alias bindings: a live external instance is
                // never replaced silently.
                bool legacyReplace = _options.DuplicateRegistrations == DuplicateRegistrationPolicy.ReplaceWithWarning
                                     && descriptor.Kind != DescriptorKind.Instance;
                if (!legacyReplace) throw Duplicate(serviceType);

                Debug.LogWarning(
                    $"[DiContainer] Replacing registration for '{serviceType.FullName}' (previous binding discarded). " +
                    "Use Replace(...) when this is intended.");
            }

            // Replace / dead instance / legacy replace: swap every binding and drop their cached instances.
            for (int i = 0; i < list.Count; i++)
                if (_instances != null) _instances.Remove(list[i]);
            list.Clear();
            list.Add(descriptor);
        }

        private static InvalidOperationException Duplicate(Type serviceType)
            => new InvalidOperationException(
                $"[DiContainer] A registration for '{serviceType.FullName}' already exists in this container. " +
                "Use Replace(...) to override it on purpose, Add(...) to register several implementations, " +
                "or register it in a child scope to shadow it.");

        private static void AssertNotUnityObject(Type implType)
        {
            if (typeof(UnityEngine.Object).IsAssignableFrom(implType))
                throw new ArgumentException(
                    $"'{implType.FullName}' derives from UnityEngine.Object (Component, ScriptableObject, ...) and cannot be " +
                    "created with a constructor. Use RegisterInstance(...), a RegisterFactory(...) that calls " +
                    "AddComponent / ScriptableObject.CreateInstance, or InstantiatePrefab.");
        }
    }
}
