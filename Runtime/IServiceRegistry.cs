using System;

namespace November.DI
{
    /// <summary>
    /// Abstraction for registering services with the DI container.
    /// Segregated from resolution and injection — follows the Interface Segregation Principle.
    ///
    /// Registration rules (per container; a child scope may always shadow its parent):
    /// - <c>Register*</c>  — single binding. Registering a service type twice in the same
    ///   container throws (see <see cref="DuplicateRegistrationPolicy"/> for the legacy
    ///   "replace with a warning" behaviour).
    /// - <c>Replace*</c>   — deliberately swaps the binding(s) in this container.
    /// - <c>Add</c>        — appends another binding. <c>Resolve</c> returns the last one added,
    ///   <c>ResolveAll</c> returns all of them.
    /// Generic shorthands (<c>Register&lt;T&gt;()</c>, <c>RegisterSingleton&lt;…&gt;()</c>,
    /// <c>Add&lt;…&gt;()</c>, <c>Replace&lt;…&gt;()</c>, <c>RegisterAlias&lt;…&gt;()</c>) live in
    /// <see cref="ServiceRegistryExtensions"/>.
    /// </summary>
    public interface IServiceRegistry
    {
        /// <summary>Registers a service with its implementation type and lifetime.</summary>
        void Register<TService, TImpl>(Lifetime lifetime = Lifetime.Transient)
            where TImpl : class, TService;

        /// <summary>Registers a service type with its implementation type and lifetime (non-generic).</summary>
        void Register(Type serviceType, Type implType, Lifetime lifetime = Lifetime.Transient);

        /// <summary>
        /// Registers an existing instance as the service. The container does not inject into it
        /// and does not dispose it. Registering over a live instance throws; registering over
        /// an instance whose Unity object has been destroyed replaces it.
        /// </summary>
        void RegisterInstance<TService>(TService instance);

        /// <summary>Non-generic <see cref="RegisterInstance{TService}"/>.</summary>
        void RegisterInstance(Type serviceType, object instance);

        /// <summary>
        /// Registers a factory delegate that creates the service on demand. The factory
        /// receives an <see cref="IDiProvider"/> to resolve its own dependencies. The result is
        /// injected ([Inject] members) unless the container already injected that very object,
        /// and disposable results are owned (disposed) by the scope that built them.
        /// To expose one existing registration under another service type use
        /// <see cref="RegisterAlias"/> instead of a forwarding factory.
        /// </summary>
        void RegisterFactory<TService>(
            Func<IDiProvider, TService> factory,
            Lifetime lifetime = Lifetime.Transient);

        /// <summary>Non-generic <see cref="RegisterFactory{TService}"/>.</summary>
        void RegisterFactory(Type serviceType, Func<IDiProvider, object> factory, Lifetime lifetime = Lifetime.Transient);

        /// <summary>
        /// Makes <paramref name="aliasType"/> resolve to whatever <paramref name="targetType"/>
        /// resolves to: the same instance, with no second injection and no second disposal.
        /// </summary>
        void RegisterAlias(Type aliasType, Type targetType);

        /// <summary>Appends a binding (multi-binding). See the interface remarks.</summary>
        void Add(Type serviceType, Type implType, Lifetime lifetime = Lifetime.Transient);

        /// <summary>Replaces this container's binding(s) for a service type.</summary>
        void Replace(Type serviceType, Type implType, Lifetime lifetime = Lifetime.Transient);

        /// <summary>Replaces this container's binding(s) with an existing instance.</summary>
        void ReplaceInstance(Type serviceType, object instance);

        /// <summary>Replaces this container's binding(s) with a factory.</summary>
        void ReplaceFactory(Type serviceType, Func<IDiProvider, object> factory, Lifetime lifetime = Lifetime.Transient);
    }
}
