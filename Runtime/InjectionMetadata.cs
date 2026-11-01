using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace November.DI
{

    /// <summary>
    /// Public cross-assembly query over the internal <see cref="InjectionMetadata"/>
    /// cache. Scene contexts (Game.Gameplay assembly) use this to skip
    /// non-participating MonoBehaviours during scene-wide injection; the cache
    /// itself stays internal so reflection internals never leak across assemblies.
    /// </summary>
    public static class InjectionQuery
    {
        /// <summary>O(1) check whether a type needs any injection — see
        /// <see cref="InjectionMetadata.TypeNeedsAnyInjection"/>.</summary>
        public static bool TypeNeedsAnyInjection(Type type) =>
            InjectionMetadata.TypeNeedsAnyInjection(type);
    }

    /// <summary>
    /// Pre-computed metadata cache for injection points across all loaded types.
    /// Populated at static initialization time to eliminate per-scene reflection overhead.
    ///
    /// Efficiency: instead of scanning every MonoBehaviour's fields/methods at scene load
    /// (the O(n*m) problem), we discover injection points once per type at class init
    /// and only iterate MonoBehaviours that actually HAVE injection points.
    ///
    /// SOLID: Single Responsibility — this class handles ONLY injection metadata caching.
    /// </summary>
    internal static class InjectionMetadata
    {
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> _injectFields = new();
        private static readonly ConcurrentDictionary<Type, MethodInfo[]> _injectMethods = new();
        private static readonly ConcurrentDictionary<Type, bool> _hasInjectPoint = new();
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> _resolveFields = new();
        private static readonly ConcurrentDictionary<Type, bool> _hasResolvePoint = new();
        private static readonly ConcurrentDictionary<Type, ConstructorInfo> _constructorCache = new();
        private static readonly ConcurrentDictionary<Type, Type[]> _constructorParamCache = new();
        private static readonly ConcurrentDictionary<Type, Func<object[], object>> _constructorDelegateCache = new();

        /// Global set of types that need DI container injection ([Inject] fields/methods).
        /// Pre-computed so scene scanning can skip non-participating types instantly.
        /// Guarded by lock (HashSet is not thread-safe; GetOrAdd delegates may run
        /// concurrently during background priming).
        /// </summary>
        private static readonly HashSet<Type> _typesNeedingContainerInjection = new();
        private static readonly object _injectionSetLock = new();

        /// <summary>
        /// Global set of types that need component resolution ([Resolve] fields).
        /// </summary>
        private static readonly HashSet<Type> _typesNeedingComponentResolution = new();

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Initialize()
        {
            // Domain reload may be disabled (Editor "Enter Play Mode Options") or the
            // runtime may re-enter play mode with statics still populated from the
            // previous session — start from a clean slate so no stale metadata (or
            // stale "needs injection" flags) leaks in.
            ClearCache();

            // Pre-populate caches by scanning only assemblies that can actually
            // carry our DI attributes: the assembly that defines [Inject]/[Resolve]
            // plus every assembly referencing it (game, editor, tests). Unity
            // packages, plugin and framework assemblies are skipped entirely —
            // GetTypes() over them costs startup time with zero possible injection
            // points (they cannot reference our attribute).
            var diAssembly = typeof(InjectAttribute).Assembly;
            string diAssemblyName = diAssembly.GetName().Name;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                if (!CanUseDiAttributes(assembly, diAssembly, diAssemblyName)) continue;

                Type[] types;
                // GetTypes (not GetExportedTypes): exported types are PUBLIC only —
                // internal/nested MonoBehaviours with [Inject] must be primed too,
                // otherwise the scene scanners below silently skip them.
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex)
                {
                    // Partially-loadable assembly: keep the types that did load so a
                    // single broken type cannot disable DI for the whole assembly.
                    // Logged once per assembly; TypeNeedsAnyInjection lazily primes
                    // any type the startup scan missed.
                    UnityEngine.Debug.LogWarning($"[InjectionMetadata] '{assembly.GetName().Name}' partially loadable: {ex.Types?.Length ?? 0} entries, {ex.LoaderExceptions?.Length ?? 0} loader errors. Continuing with loadable types.");
                    types = Array.FindAll(ex.Types, t => t != null);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[InjectionMetadata] Skipping assembly '{assembly.GetName().Name}': {ex.GetType().Name}: {ex}");
                    continue;
                }

                foreach (var type in types)
                {
                    if (type.IsAbstract || type.IsInterface) continue;
                    PrimeInjectionCache(type);
                }
            }
        }

        private static bool CanUseDiAttributes(Assembly assembly, Assembly diAssembly, string diAssemblyName)
        {
            if (assembly == diAssembly) return true;

            AssemblyName[] referenced = assembly.GetReferencedAssemblies();
            for (int i = 0; i < referenced.Length; i++)
                if (referenced[i].Name == diAssemblyName)
                    return true;

            return false;
        }

        /// Pre-computes and caches injection metadata for a given type.
        /// Thread-safe: ConcurrentDictionaries for metadata, locked HashSets
        /// for the type sets (GetOrAdd delegates may run concurrently).
        /// </summary>
        public static void PrimeInjectionCache(Type type)
        {
            // ── [Inject] fields and methods ──────────────────────────────
            _hasInjectPoint.GetOrAdd(type, t =>
            {
                var fields = new List<FieldInfo>();
                var methods = new List<MethodInfo>();
                var hierarchy = new Stack<Type>();

                for (var current = t; current != null && current != typeof(object); current = current.BaseType)
                    hierarchy.Push(current);

                while (hierarchy.Count > 0)
                {
                    var current = hierarchy.Pop();
                    var declaredFields = current.GetFields(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    foreach (var f in declaredFields)
                    {
                        if (f.IsDefined(typeof(InjectAttribute), inherit: false))
                            fields.Add(f);
                    }

                    var declaredMethods = current.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    foreach (var m in declaredMethods)
                    {
                        if (m.IsDefined(typeof(InjectAttribute), inherit: false))
                            methods.Add(m);
                    }
                }

                _injectFields.TryAdd(t, fields.ToArray());
                _injectMethods.TryAdd(t, methods.ToArray());

                bool hasInject = fields.Count > 0 || methods.Count > 0;
                if (hasInject) lock (_injectionSetLock) { _typesNeedingContainerInjection.Add(t); }
                return hasInject;
            });

            // ── [Resolve] fields ─────────────────────────────────────────
            _hasResolvePoint.GetOrAdd(type, t =>
            {
                var fields = new List<FieldInfo>();
                var hierarchy = new Stack<Type>();

                for (var current = t; current != null && current != typeof(object); current = current.BaseType)
                    hierarchy.Push(current);

                while (hierarchy.Count > 0)
                {
                    var current = hierarchy.Pop();
                    var declaredFields = current.GetFields(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    foreach (var f in declaredFields)
                    {
                        if (f.IsDefined(typeof(ResolveAttribute), inherit: false))
                            fields.Add(f);
                    }
                }

                _resolveFields.TryAdd(t, fields.ToArray());

                bool hasResolve = fields.Count > 0;
                if (hasResolve) lock (_injectionSetLock) { _typesNeedingComponentResolution.Add(t); }
                return hasResolve;
            });
        }

        // ── [Inject] accessors ──────────────────────────────────────────

        public static bool TypeNeedsContainerInjection(Type type)
        {
            lock (_injectionSetLock) { if (_typesNeedingContainerInjection.Contains(type)) return true; }
            // Fallback: prime on demand if not pre-populated
            if (!_hasInjectPoint.ContainsKey(type)) PrimeInjectionCache(type);
            return _hasInjectPoint.TryGetValue(type, out var v) && v;
        }

        public static FieldInfo[] GetInjectFields(Type type)
        {
            if (!_injectFields.TryGetValue(type, out var fields))
            {
                PrimeInjectionCache(type);
                fields = _injectFields.TryGetValue(type, out var f) ? f : Array.Empty<FieldInfo>();
            }
            return fields;
        }

        public static MethodInfo[] GetInjectMethods(Type type)
        {
            if (!_injectMethods.TryGetValue(type, out var methods))
            {
                PrimeInjectionCache(type);
                methods = _injectMethods.TryGetValue(type, out var m) ? m : Array.Empty<MethodInfo>();
            }
            return methods;
        }

        // ── [Resolve] accessors ─────────────────────────────────────────

        public static bool TypeNeedsComponentResolution(Type type)
        {
            lock (_injectionSetLock) { if (_typesNeedingComponentResolution.Contains(type)) return true; }
            if (!_hasResolvePoint.ContainsKey(type)) PrimeInjectionCache(type);
            return _hasResolvePoint.TryGetValue(type, out var v) && v;
        }

        /// <summary>
        /// Fast check: does this type need ANY form of injection?
        /// Used by scene scanning to skip irrelevant MonoBehaviours instantly — O(1) HashSet lookup.
        /// Primes on demand: Initialize() may have missed the type (e.g. an internal
        /// type when the startup scan ran under different assembly state), and this
        /// method is the ONLY gate for scene-wide injection — a false negative here
        /// would silently skip the component's [Inject]/[Resolve] fields forever.
        /// </summary>
        public static bool TypeNeedsAnyInjection(Type type)
        {
            lock (_injectionSetLock)
            {
                if (_typesNeedingContainerInjection.Contains(type) ||
                    _typesNeedingComponentResolution.Contains(type))
                    return true;
            }

            // Lazy prime — mirrors TypeNeedsContainerInjection's fallback.
            if (!_hasInjectPoint.ContainsKey(type) || !_hasResolvePoint.ContainsKey(type))
                PrimeInjectionCache(type);

            return (_hasInjectPoint.TryGetValue(type, out var inject) && inject) ||
                   (_hasResolvePoint.TryGetValue(type, out var resolve) && resolve);
        }

        public static FieldInfo[] GetResolveFields(Type type)
        {
            if (!_resolveFields.TryGetValue(type, out var fields))
            {
                PrimeInjectionCache(type);
                fields = _resolveFields.TryGetValue(type, out var f) ? f : Array.Empty<FieldInfo>();
            }
            return fields;
        }

        // ── Constructor cache (for DiContainer) ─────────────────────────

        public static ConstructorInfo GetConstructor(Type type)
        {
            return _constructorCache.GetOrAdd(type, t =>
            {
                var allCtors = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var publicCtors = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

                var ctor = allCtors.FirstOrDefault(c => c.IsDefined(typeof(InjectAttribute), inherit: false))
                        ?? (publicCtors.Length == 1 ? publicCtors[0] : null);

                if (ctor != null) return ctor;

                throw publicCtors.Length > 1
                    ? new InvalidOperationException(
                        $"Type '{t.FullName}' has {publicCtors.Length} public constructors. " +
                        "Annotate the intended one with [Inject] to remove ambiguity.")
                    : new InvalidOperationException(
                        $"Type '{t.FullName}' has no accessible constructors. " +
                        "Add a public constructor or annotate one with [Inject].");
            });
        }

        public static Type[] GetConstructorParamTypes(Type type)
        {
            return _constructorParamCache.GetOrAdd(type, t =>
            {
                var ctor = GetConstructor(t);
                return ctor.GetParameters().Select(p => p.ParameterType).ToArray();
            });
        }

        public static Func<object[], object> GetConstructorDelegate(Type type)
        {
            // Uses direct ctor.Invoke instead of Expression trees — AOT-safe for IL2CPP/WebGL.
            return _constructorDelegateCache.GetOrAdd(type, _ => args => GetConstructor(type).Invoke(args));
        }

        /// <summary>
        /// Clears all cached metadata. Called on domain reload in Editor and on subsystem registration.
        /// </summary>
        public static void ClearCache()
        {
            _injectFields.Clear();
            _injectMethods.Clear();
            _hasInjectPoint.Clear();
            _resolveFields.Clear();
            _hasResolvePoint.Clear();
            _constructorCache.Clear();
            _constructorParamCache.Clear();
            _constructorDelegateCache.Clear();
            lock (_injectionSetLock)
            {
                _typesNeedingContainerInjection.Clear();
                _typesNeedingComponentResolution.Clear();
            }
        }
    }
}
