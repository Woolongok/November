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
        /// <summary>Resolves a service of the specified type. Throws if it is not registered.</summary>
        TService Resolve<TService>();

        /// <summary>Resolves a service by type (non-generic). Throws if it is not registered.</summary>
        object Resolve(Type type);

        /// <summary>
        /// Resolves a service if it is registered. Returns <c>false</c> ONLY when there is no
        /// registration; every other failure (disposed container, wrong thread, a throwing
        /// constructor or factory, a dependency cycle) propagates as an exception.
        /// </summary>
        bool TryResolve<TService>(out TService result);

        /// <summary>
        /// Resolves every registration of a service type: ancestors first (when
        /// <paramref name="includeParents"/> is true), then this scope, each in registration
        /// order. Scoped and transient entries are built by the scope this is called on.
        /// </summary>
        IEnumerable<TService> ResolveAll<TService>(bool includeParents = true);

        /// <summary>
        /// True when <paramref name="type"/> can be resolved from this scope: it has a
        /// registration here or in an ancestor, or it is an implicit wrapper over something
        /// resolvable (<c>Func&lt;T&gt;</c> / <c>Lazy&lt;T&gt;</c> need a registration for T;
        /// <c>IEnumerable&lt;T&gt;</c>, <c>IReadOnlyList&lt;T&gt;</c> and <c>T[]</c> are always
        /// resolvable and are empty when nothing is registered).
        /// </summary>
        bool HasRegistration(Type type);
    }
}
