using System;
using System.Collections.Generic;
using UnityEngine;

namespace November.DI
{

    /// <summary>
    /// DiContainer registration partition: Register / RegisterInstance /
    /// RegisterFactory plus descriptor bookkeeping.
    /// Same type via partial — no behaviour change.
    /// </summary>
    public sealed partial class DiContainer : IDiScope, IServiceProvider, IScopeFactory
    {
        public void Register<TService, TImpl>(Lifetime lifetime = Lifetime.Transient)
            where TImpl : class, TService
        {
            var implType = typeof(TImpl);
            if (implType.IsAbstract || implType.IsInterface)
                throw new ArgumentException(
                    $"Implementation type '{implType.FullName}' must be a concrete, non-abstract type.");
            AssertNotUnityComponent(implType);

            AddDescriptor(typeof(TService), new ServiceDescriptor(implType, lifetime));
        }

        public void Register(Type serviceType, Type implType, Lifetime lifetime = Lifetime.Transient)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (implType == null) throw new ArgumentNullException(nameof(implType));
            if (implType.IsAbstract || implType.IsInterface)
                throw new ArgumentException(
                    $"Implementation type '{implType.FullName}' must be a concrete, non-abstract type.",
                    nameof(implType));
            if (!serviceType.IsAssignableFrom(implType))
                throw new ArgumentException(
                    $"'{implType.FullName}' does not implement or extend '{serviceType.FullName}'.");
            AssertNotUnityComponent(implType);

            AddDescriptor(serviceType, new ServiceDescriptor(implType, lifetime));
        }

        public void RegisterInstance<TService>(TService instance)
        {
            if (instance == null || (instance is UnityEngine.Object unityObject && unityObject == null))
                throw new ArgumentNullException(nameof(instance), "Cannot register a null or destroyed Unity object.");
            var type = typeof(TService);

            if (_registry.TryGetValue(type, out var existingDescriptors))
            {
                object cachedInstance = null;
                if (existingDescriptors.Count > 0 && existingDescriptors[^1].IsExternalInstance)
                {
                    if (!_singletons.TryGetValue(type, out cachedInstance))
                        cachedInstance = existingDescriptors[^1].Factory?.Invoke(this);
                }
                bool cachedInstanceWasDestroyed = cachedInstance is UnityEngine.Object cachedUnityObject
                    && cachedUnityObject == null;
                if (!cachedInstanceWasDestroyed)
                    throw new InvalidOperationException(
                        $"[DiContainer] A registration for '{type.FullName}' already exists. " +
                        "Call RegisterInstance only once per type per container.");

                // Scene-owned Unity objects can be destroyed while a long-lived
                // scope still holds their external registration. Permit the owner
                // to replace that dead reference with its live scene instance.
                _registry.Remove(type);
                _singletons.Remove(type);
            }

            if (_registry.ContainsKey(type))
                throw new InvalidOperationException(
                    $"[DiContainer] A registration for '{type.FullName}' already exists. " +
                    "Call RegisterInstance only once per type per container.");

            var descriptor = new ServiceDescriptor(
                type,
                Lifetime.Singleton,
                factory: _ => (object)instance,
                isExternalInstance: true);

            _singletons[type] = instance;
            AddDescriptor(type, descriptor);
        }

        public void RegisterFactory<TService>(
            Func<IServiceProvider, TService> factory,
            Lifetime lifetime = Lifetime.Transient)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            AddDescriptor(typeof(TService),
                new ServiceDescriptor(typeof(TService), lifetime, c => factory(c)));
        }

        private void AddDescriptor(Type serviceType, ServiceDescriptor descriptor)
        {
            if (_registry.TryGetValue(serviceType, out var list))
            {
                // Register/RegisterFactory: REPLACING an existing descriptor is deliberate —
                // it keeps stale descriptors from accumulating across scene reloads.
                // (RegisterInstance is intentionally different: it THROWS on duplicates,
                // because the same external instance registered twice is always a wiring bug.)
                // Single-bind contract: after Clear()+Add only one descriptor remains, so
                // ResolveAll returns the parent chain + this one entry (never two local
                // bindings). Warn so an accidental double-Register is visible in
                // Editor AND builds (Warning level: observable without failing runs).
                if (list.Count > 0)
                {
                    Debug.LogWarning($"[DiContainer] Replacing registration for '{serviceType.FullName}' (previous descriptor discarded). This is expected across scene reloads; if unexpected, check for a duplicate Register call.");
                    // The previous singleton/scoped value belongs to the replaced
                    // descriptor; retaining it could return a destroyed scene object
                    // after the new binding has been installed.
                    _singletons.Remove(serviceType);
                    _scoped.Remove(serviceType);
                }
                list.Clear();
                list.Add(descriptor);
            }
            else
            {
                list = new List<ServiceDescriptor>(1);
                list.Add(descriptor);
                _registry[serviceType] = list;
            }
        }

        private static void AssertNotUnityComponent(Type implType)
        {
            if (typeof(Component).IsAssignableFrom(implType))
                throw new ArgumentException(
                    $"'{implType.FullName}' derives from UnityEngine.Component and cannot be " +
                    "constructor-injected via Register(...). " +
                    "Use RegisterInstance(...) or InstantiatePrefab/InjectGameObjectHierarchy.");
        }
    }

}
