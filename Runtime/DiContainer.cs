using System;
using System.Collections.Generic;

namespace November.DI
{

    /// <summary>
    /// Dependency Injection container implementing all DI interfaces.
    ///
    /// SOLID compliance:
    ///   S — Single Responsibility via interface segregation (each interface = one concern).
    ///   I — Interface Segregation: IServiceRegistry / IServiceResolver / IInjector / IScopeFactory.
    ///   D — Dependency Inversion: consumers depend on abstractions, not this concrete class.
    ///
    /// Efficiency:
    ///   - No thread-safety overhead (Unity is single-threaded on the main thread).
    ///   - All reflection metadata cached via InjectionMetadata (pre-populated at class init).
    ///   - Plain Dictionary lookups instead of ConcurrentDictionary.
    ///   - No ThreadLocal allocations — shared HashSet with try/finally.
    ///   - Constructor delegate cached per type (AOT-safe ctor.Invoke for IL2CPP).
    ///
    /// File layout (same partial type — zero behaviour change vs the old god file):
    ///   DiContainer.cs — fields, scope chain, construction (this file).
    ///   DiContainer.Registration.cs — Register / RegisterInstance / RegisterFactory.
    ///   DiContainer.Resolution.cs — Resolve / TryResolve / ResolveAll / dispatch.
    ///   DiContainer.Injection.cs — creation, [Inject]/[Resolve], prefab, scopes.
    ///   DiContainer.Factories.cs — Func&lt;T&gt; / Lazy&lt;T&gt; support.
    ///   DiContainer.Disposal.cs — teardown and guards.
    /// </summary>
    public sealed partial class DiContainer : IDiScope, IServiceProvider, IScopeFactory
    {
        private readonly DiContainer _parent;

        // ── Registries: plain Dictionary, no thread-safety overhead ──────
        private readonly Dictionary<Type, List<ServiceDescriptor>> _registry = new();
        private readonly Dictionary<Type, object> _singletons = new();
        private readonly Dictionary<Type, object> _scoped = new();

        // ── Disposal tracking ───────────────────────────────────────────
        private readonly List<IDisposable> _disposables = new();
        private bool _disposed;

        // ── Cycle detection: shared HashSet across parent/child chain ───
        // A dependency cycle can span scopes (e.g. a global singleton needing a
        // scene-scoped service that in turn needs the singleton back). The whole
        // parent/child chain shares one set via Root, so cross-scope cycles are
        // detected instead of recursing until stack overflow.
        private DiContainer Root => _parent?.Root ?? this;
        private readonly HashSet<Type> _resolvingLocal = new();
        private readonly HashSet<Type> _constructingLocal = new();
        private HashSet<Type> Resolving => Root._resolvingLocal;
        private HashSet<Type> Constructing => Root._constructingLocal;

        private static int s_mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;

#if UNITY_2019_1_OR_NEWER
        // Re-capture on every domain/scene load: static initializers run on
        // whichever thread first touches the type (test host, background
        // loader), which would invert IsMainThread and all AssertMainThread
        // guards. SubsystemRegistration runs on the main thread.
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RecaptureMainThreadId() =>
            s_mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
#endif

        /// <summary>True on the Unity main thread. The container is intentionally
        /// single-threaded (plain Dictionaries, shared Resolve/Construct sets);
        /// background-thread resolve is unsupported by design.</summary>
        public static bool IsMainThread => System.Threading.Thread.CurrentThread.ManagedThreadId == s_mainThreadId;

        // ── Construction ────────────────────────────────────────────────

        public DiContainer(DiContainer parent = null)
        {
            _parent = parent;
        }

        /// <summary>
        /// Factory method that ensures the container is created on the main Unity thread.
        /// </summary>
        public static DiContainer CreateOnMainThread(DiContainer parent = null)
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != s_mainThreadId)
                throw new InvalidOperationException(
                    "[DiContainer] CreateOnMainThread() must be called from the main thread.");
            return new DiContainer(parent);
        }
    }
}
