using System;

namespace November.DI
{

    /// <summary>
    /// Abstraction for creating child scopes from a DI container.
    /// Enables scoped lifetime management without depending on the concrete container.
    /// Follows the Dependency Inversion and Interface Segregation Principles.
    ///
    /// ═══ Scope Lifecycle ═══════════════════════════════════════════
    /// - Each call to CreateScope creates an independent child scope.
    /// - Child scopes inherit all parent registrations and can add/override their own.
    /// - Disposing a scope releases only its own scoped and singleton instances.
    /// - ⚠️  Parent containers do NOT track child scopes. Disposing a parent does
    ///   NOT cascade disposal to its children. Each scope must be disposed independently.
    ///   This is intentional — scopes are standalone lifetime boundaries (e.g. one per
    ///   scene or one per entity), and the parent should not outlive them.
    /// ════════════════════════════════════════════════════════════════
    /// </summary>
    public interface IScopeFactory : IDisposable
    {
        /// <summary>
        /// Creates a child scope that inherits parent registrations.
        /// The returned IDiScope supports registration, resolution, and injection
        /// within the scope boundary.
        /// Throws ObjectDisposedException if the parent container has been disposed.
        /// </summary>
        IDiScope CreateScope();
    }
}
