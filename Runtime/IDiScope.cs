using System;

namespace November.DI
{

    /// <summary>
    /// A self-contained DI scope that supports registration, resolution, and injection.
    /// Created by IScopeFactory.CreateScope().
    ///
    /// A scope inherits parent registrations and can define its own. Disposing the scope
    /// releases all scoped and singleton instances created within it.
    ///
    /// ═══ Scope Lifecycle ═══════════════════════════════════════════
    /// - Dispose() releases only this scope's resources (its scoped and singleton instances).
    /// - Parent containers do NOT track child scopes. Disposing a parent does NOT cascade
    ///   disposal to its children. Each scope must be disposed independently.
    /// - After disposal, the scope's IServiceResolver and IInjector throw ObjectDisposedException.
    /// ════════════════════════════════════════════════════════════════
    ///
    /// SOLID: follows Interface Segregation by composing smaller interfaces:
    ///   IServiceRegistry — register services within this scope
    ///   IServiceProvider  — resolve services and inject dependencies
    ///   IDisposable       — clean up scope resources
    /// </summary>
    public interface IDiScope : IServiceRegistry, IServiceProvider, IDisposable
    {
    }
}
