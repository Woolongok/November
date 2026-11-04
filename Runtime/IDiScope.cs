using System;

namespace November.DI
{
    /// <summary>
    /// A self-contained DI scope: registration, resolution, injection, child scopes and
    /// disposal. Created by <see cref="IScopeFactory.CreateScope"/>.
    ///
    /// A scope inherits parent registrations and can define its own. Disposing it releases
    /// everything it created and tracked: its singletons, its scoped services and the
    /// transient disposables that were resolved through it.
    ///
    /// - Dispose() / DisposeAsync() releases only this scope's resources.
    /// - Parents do not track child scopes; each scope must be disposed independently.
    /// - After disposal every resolve, inject, register and CreateScope call throws
    ///   <see cref="ObjectDisposedException"/>.
    /// </summary>
    public interface IDiScope : IServiceRegistry, IDiProvider, IScopeFactory, IDisposable, IAsyncDisposable
    {
    }
}
