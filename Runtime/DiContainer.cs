using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace November.DI
{
    /// <summary>
    /// Dependency injection container. Single-threaded by design: use it from the Unity main
    /// thread only (checked in the Editor and development builds).
    ///
    /// Ownership model
    /// - A registration is a <see cref="ServiceDescriptor"/>; instance caches are keyed by descriptor,
    ///   never by service type, so a replaced or shadowed registration cannot serve a stale instance.
    /// - Singleton: built by the container that owns the registration, from that container's view
    ///   of the registrations; cached and disposed there.
    /// - Scoped:    built, cached and disposed by the scope that RESOLVES it, even when the
    ///   registration lives on an ancestor.
    /// - Transient: built by the resolving scope; a disposable one is tracked by that scope.
    /// - RegisterInstance: owned by the caller; never injected, never disposed.
    /// - RegisterFactory: the result is owned by the container that built it, unless the object
    ///   already belongs to another container or was registered as an instance. A factory that
    ///   forwards a parent singleton therefore cannot transfer ownership to a child scope.
    ///
    /// File layout (one partial type):
    ///   DiContainer.cs              — fields, construction.
    ///   DiContainer.Registration.cs — Register / Add / Replace / RegisterInstance / RegisterFactory / RegisterAlias.
    ///   DiContainer.Resolution.cs   — Resolve / TryResolve / ResolveAll and lifetime dispatch.
    ///   DiContainer.Injection.cs    — construction, [Inject] / [Resolve] injection, prefabs, scopes.
    ///   DiContainer.Factories.cs    — Func&lt;T&gt; / Lazy&lt;T&gt; support.
    ///   DiContainer.Disposal.cs     — Dispose / DisposeAsync and disposal tracking.
    ///   DiContainer.Validation.cs   — Validate() and DescribeRegistrations().
    ///   DiContainer.Diagnostics.cs  — resolution paths and error construction.
    /// </summary>
    public sealed partial class DiContainer : IDiScope
    {
        private readonly DiContainer _parent;
        private readonly ResolutionState _state;       // shared by the whole parent/child chain
        private readonly DiContainerOptions _options;  // shared by the chain unless a scope overrides it

        // Allocated on first use: a scope that only resolves costs a single object.
        private Dictionary<Type, List<ServiceDescriptor>> _registry;
        private Dictionary<ServiceDescriptor, object> _instances;
        private List<object> _disposables;
        private bool _disposed;
        private Task _disposeTask;

        /// <summary>
        /// Creates a root container, or a scope when <paramref name="parent"/> is given
        /// (prefer <see cref="CreateScope"/>). <paramref name="options"/> is copied; scopes inherit
        /// their parent's options unless they pass their own.
        /// </summary>
        public DiContainer(DiContainer parent = null, DiContainerOptions options = null)
        {
            MainThread.Assert(nameof(DiContainer));

            if (parent != null)
            {
                parent.ThrowIfDisposed();
                _state = parent._state;
                _options = options != null ? options.Clone() : parent._options;
            }
            else
            {
                _state = new ResolutionState();
                _options = (options ?? new DiContainerOptions()).Clone();
            }

            _parent = parent;
        }

        /// <summary>
        /// Creates a container and, unlike a plain constructor call, enforces the main-thread
        /// contract in every build (not only in the Editor and development builds).
        /// </summary>
        public static DiContainer CreateOnMainThread(DiContainer parent = null, DiContainerOptions options = null)
        {
            MainThread.Require("CreateOnMainThread()");
            return new DiContainer(parent, options);
        }

        /// <summary>True on the Unity main thread.</summary>
        public static bool IsMainThread => MainThread.IsMain;

        /// <summary>True once <see cref="Dispose"/> or <see cref="DisposeAsync"/> has been called.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>Number of <c>TryResolve</c> misses seen by this container chain (diagnostics and tests).</summary>
        internal int TryResolveMissCount => _state.MissCount;

        /// <summary>Readable name for a type, generics included (<c>Func&lt;IFoo&gt;</c>, not <c>Func`1</c>).</summary>
        internal static string PrettyName(Type type)
        {
            if (type == null) return "null";
            if (type.IsArray) return PrettyName(type.GetElementType()) + "[]";
            if (!type.IsGenericType) return type.Name;

            var name = type.Name;
            int tick = name.IndexOf('`');
            if (tick > 0) name = name.Substring(0, tick);

            var arguments = type.GetGenericArguments();
            var sb = new StringBuilder(name).Append('<');
            for (int i = 0; i < arguments.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(PrettyName(arguments[i]));
            }
            return sb.Append('>').ToString();
        }

        /// <summary>A destroyed UnityEngine.Object compares equal to null; plain objects never do.</summary>
        private static bool IsDestroyedUnityObject(object instance)
            => instance is UnityEngine.Object unityObject && unityObject == null;
    }
}
