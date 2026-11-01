using System;
using System.Collections.Generic;

namespace November.DI
{

    /// <summary>
    /// Abstraction for resolving services from the DI container.
    /// Segregated from registration — follows the Interface Segregation Principle.
    /// </summary>
    public interface IServiceResolver
    {
        /// <summary>
        /// Resolves a service of the specified type.
        /// </summary>
        TService Resolve<TService>();

        /// <summary>
        /// Resolves a service by type (non-generic).
        /// </summary>
        object Resolve(Type type);

        /// <summary>
        /// Attempts to resolve a service without throwing on failure.
        /// </summary>
        bool TryResolve<TService>(out TService result);

        /// <summary>
        /// Resolves all registered implementations of a service type.
        /// </summary>
        IEnumerable<TService> ResolveAll<TService>(bool includeParents = true);

        /// <summary>
        /// Checks whether a registration exists for the given type (in this container or parent).
        /// </summary>
        bool HasRegistration(Type type);
    }
}
