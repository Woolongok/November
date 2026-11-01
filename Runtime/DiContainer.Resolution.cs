using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using UnityEngine;

namespace November.DI
{

    /// <summary>
    /// DiContainer resolution partition: Resolve / TryResolve / ResolveAll /
    /// descriptor dispatch with cycle detection.
    /// Same type via partial — no behaviour change.
    /// </summary>
    public sealed partial class DiContainer : IDiScope, IServiceProvider, IScopeFactory
    {
        // Cache for compiled List<T> factory per element type — zero-reflection per call.
        private static readonly Dictionary<Type, Func<System.Collections.IList>> _listFactoryCache = new();
        private static readonly object _listFactoryLock = new();

        public TService Resolve<TService>() => (TService)Resolve(typeof(TService));

        public object Resolve(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            ThrowIfDisposed();
            AssertMainThread($"Resolve({type.Name})");

            // Handle Func<T> and Lazy<T> open generics
            if (type.IsGenericType)
            {
                var def = type.GetGenericTypeDefinition();

                if (def == typeof(Func<>))
                {
                    var returnType = type.GetGenericArguments()[0];
                    return CreateTypedFactory(returnType, () => Resolve(returnType));
                }

                if (def == typeof(Lazy<>))
                    return CreateTypedLazy(type.GetGenericArguments()[0]);
            }

            if (!Resolving.Add(type))
                throw new InvalidOperationException(
                    $"Circular dependency detected while resolving '{type.FullName}'. " +
                    $"Chain: {string.Join(" -> ", Resolving.Select(t => t.Name))} -> {type.Name}. " +
                    "Use Lazy<T> or Func<T> to break the cycle.");

            try { return ResolveCore(type); }
            finally { Resolving.Remove(type); }
        }

        // Warn-once bookkeeping: TryResolve is the optional-service probe, so a
        // miss can be legitimate (and a caller may probe in a loop) — logging on
        // every call would spam the console. Static for app lifetime; tests can
        // reset via ClearResolveFailureHistory so a first-test mis-wire does not
        // silence later tests.
        private static readonly HashSet<Type> _resolveFailureWarned = new HashSet<Type>();
        private static readonly object _resolveFailureLock = new();

        /// <summary>Test hook: re-arm TryResolve warnings (static warn-once set).</summary>
        public static void ClearResolveFailureHistory() { lock (_resolveFailureLock) { _resolveFailureWarned.Clear(); } }

        /// <summary>Total TryResolve misses (warn-once still applies to the log, but
        /// the count keeps every mis-wire observable instead of silent after first).</summary>
        public static int ResolveFailureCount => _resolveFailureCount;
        private static int _resolveFailureCount;

        public bool TryResolve<TService>(out TService result)
        {
            try
            {
                result = Resolve<TService>();
                return true;
            }
            catch (InvalidOperationException ex)
            {
                // Fail-loud for real construction bugs: only a missing
                // registration is an optional-miss. If a registration exists
                // but resolution threw (circular dep, singleton ctor failure,
                // factory exception), rethrow so the mis-wire surfaces instead
                // of reading as "optional miss" with default(null).
                if (HasRegistration(typeof(TService)))
                    throw;
                System.Threading.Interlocked.Increment(ref _resolveFailureCount);
                bool first;
                lock (_resolveFailureLock) { first = _resolveFailureWarned.Add(typeof(TService)); }
                if (first)
                    Debug.LogWarning($"[DiContainer] TryResolve<{typeof(TService).Name}> failed:\n{ex}");
                else
                    Debug.Log($"[DiContainer] TryResolve<{typeof(TService).Name}> missed again (total {ResolveFailureCount}).");
                result = default;
                return false;
            }
        }

        public IEnumerable<TService> ResolveAll<TService>(bool includeParents = true)
            => ResolveAll(typeof(TService), includeParents).Cast<TService>();

        public bool HasRegistration(Type type)
        {
            if (_registry.ContainsKey(type)) return true;
            return _parent != null && _parent.HasRegistration(type);
        }

        private IEnumerable ResolveAll(Type elementType, bool includeParents)
        {
            Func<System.Collections.IList> factory;
            lock (_listFactoryLock)
            {
                if (!_listFactoryCache.TryGetValue(elementType, out factory))
                {
                    var listType = typeof(List<>).MakeGenericType(elementType);
                    var ctor = listType.GetConstructor(Type.EmptyTypes);
                    // Expression.Compile avoids ctor.Invoke reflection overhead entirely.
                    var lambda = Expression.Lambda<Func<System.Collections.IList>>(
                        Expression.New(ctor));
                    factory = lambda.Compile();
                    _listFactoryCache[elementType] = factory;
                }
            }

            var resultList = factory();

            if (includeParents && _parent != null)
                foreach (var item in _parent.ResolveAll(elementType, includeParents: true))
                    resultList.Add(item);

            if (_registry.TryGetValue(elementType, out var descriptors))
            {
                foreach (var d in descriptors)
                    resultList.Add(ResolveDescriptor(elementType, d));
            }

            return resultList;
        }

        private object ResolveCore(Type type)
        {
            // IEnumerable<T> → resolve all
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return ResolveAll(type.GetGenericArguments()[0], includeParents: true);

            // Direct registration lookup (last registered wins for single-resolve)
            if (_registry.TryGetValue(type, out var descriptors) && descriptors.Count > 0)
                return ResolveDescriptor(type, descriptors[^1]);

            // Parent delegation with cycle-detection safety.
            // Scoped is the exception: the descriptor may live on an ancestor,
            // but the instance is created, cached and tracked HERE, so sibling
            // scopes stay independent and parent disposal does not cascade into
            // them. Singleton/Transient keep delegating (shared / caller-owned).
            if (_parent != null && _parent.HasRegistration(type))
            {
                var ancestorDescriptor = _parent.FindNearestDescriptor(type);
                if (ancestorDescriptor != null && ancestorDescriptor.Lifetime == Lifetime.Scoped)
                    return ResolveDescriptor(type, ancestorDescriptor);
                return ResolveFromParent(type);
            }

            // No registration found — fail fast instead of silently auto-creating.
            if (!type.IsAbstract && !type.IsInterface)
            {
                if (_parent != null)
                    return ResolveFromParent(type);

                throw new InvalidOperationException(
                    $"No registration found for concrete type '{type.FullName}'. " +
                    $"Register it via Register<TService, TImpl>(), RegisterInstance<T>(), or RegisterFactory<T>(). " +
                    $"(Chain: {string.Join(" -> ", Resolving.Select(t => t.Name))})");
            }

            if (_parent != null)
                return ResolveFromParent(type);

            throw new InvalidOperationException(
                $"No registration found for '{type.FullName}'. " +
                $"Ensure it is registered before resolving. " +
                $"(Chain: {string.Join(" -> ", Resolving.Select(t => t.Name))})");
        }

        private object ResolveFromParent(Type type)
        {
            Resolving.Remove(type);
            try { return _parent.Resolve(type); }
            finally { Resolving.Add(type); }
        }

        /// <summary>Nearest registration for <paramref name="type"/> up the scope
        /// chain (own scope first). Used to honor Scoped lifetime locally even
        /// when the descriptor lives on an ancestor.</summary>
        private ServiceDescriptor FindNearestDescriptor(Type type)
        {
            if (_registry.TryGetValue(type, out var descriptors) && descriptors.Count > 0)
                return descriptors[^1];
            return _parent?.FindNearestDescriptor(type);
        }

        private object ResolveDescriptor(Type serviceType, ServiceDescriptor descriptor)
        {
            switch (descriptor.Lifetime)
            {
                case Lifetime.Singleton:
                    {
                        // Descriptor is always resolved from its owning container,
                        // so `this` is the correct scope for singleton caching.
                        if (_singletons.TryGetValue(serviceType, out var existing))
                        {
                            // Scene MonoBehaviours registered as instances die
                            // with their scene: a destroyed Unity object must
                            // never be handed out as a live singleton. External
                            // instances cannot be recreated (factory returns the
                            // same dead ref) — fail loud so the owner re-registers.
                            if (existing is UnityEngine.Object unityObj && unityObj == null)
                            {
                                _singletons.Remove(serviceType);
                                if (descriptor.IsExternalInstance)
                                    throw new InvalidOperationException(
                                        $"[DiContainer] Registered instance for '{serviceType.FullName}' was destroyed (scene unload?). Re-register the live instance — the dead reference is evicted, not returned.");
                            }
                            else return existing;
                        }

                        try
                        {
                            var instance = CreateAndTrack(
                                descriptor.ImplementationType,
                                descriptor.Factory,
                                Lifetime.Singleton,
                                descriptor.IsExternalInstance);

                            _singletons[serviceType] = instance;
                            return instance;
                        }
                        catch
                        {
                            _singletons.Remove(serviceType);
                            Debug.LogError(
                                $"[DiContainer] Singleton construction failed for {serviceType.FullName}. Cache cleared.");
                            throw;
                        }
                    }

                case Lifetime.Scoped:
                    {
                        if (_scoped.TryGetValue(serviceType, out var existing))
                        {
                            if (existing is UnityEngine.Object unityObj && unityObj == null)
                            {
                                _scoped.Remove(serviceType);
                                if (descriptor.IsExternalInstance)
                                    throw new InvalidOperationException(
                                        $"[DiContainer] Registered scoped instance for '{serviceType.FullName}' was destroyed. Re-register the live instance.");
                            }
                            else return existing;
                        }

                        try
                        {
                            var instance = CreateAndTrack(
                                descriptor.ImplementationType,
                                descriptor.Factory,
                                Lifetime.Scoped,
                                descriptor.IsExternalInstance);

                            _scoped[serviceType] = instance;
                            return instance;
                        }
                        catch
                        {
                            _scoped.Remove(serviceType);
                            throw;
                        }
                    }

                default: // Transient
                    return CreateAndTrack(
                        descriptor.ImplementationType,
                        descriptor.Factory,
                        Lifetime.Transient,
                        externalInstance: false);
            }
        }
    }

}
