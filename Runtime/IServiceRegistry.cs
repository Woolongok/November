using System;

namespace November.DI
{

    /// <summary>
    /// Abstraction for registering services with the DI container.
    /// Segregated from resolution and injection — follows the Interface Segregation Principle.
    /// </summary>
    public interface IServiceRegistry
    {
        /// <summary>
        /// Registers a service with its implementation type and lifetime.
        /// </summary>
        void Register<TService, TImpl>(Lifetime lifetime = Lifetime.Transient)
            where TImpl : class, TService;

        /// <summary>
        /// Registers a service type with its implementation type and lifetime (non-generic).
        /// </summary>
        void Register(Type serviceType, Type implType, Lifetime lifetime = Lifetime.Transient);

        /// <summary>
        /// Registers an existing instance as a singleton service.
        /// </summary>
        void RegisterInstance<TService>(TService instance);

        /// <summary>
        /// Registers a factory delegate that creates the service on demand.
        /// The factory receives an IServiceProvider (resolve + inject) for creating
        /// the instance, without depending on the concrete DiContainer.
        /// </summary>
        void RegisterFactory<TService>(
            Func<IServiceProvider, TService> factory,
            Lifetime lifetime = Lifetime.Transient);
    }
}
