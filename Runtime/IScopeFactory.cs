namespace November.DI
{
    /// <summary>
    /// Abstraction for creating child scopes from a DI container. Consumers that only
    /// need to open a scope depend on this and do not get a <c>Dispose</c> method they
    /// must not call (the scope that is returned is the thing to dispose).
    ///
    /// Scope lifecycle:
    /// - Each call creates an independent child scope that inherits parent registrations
    ///   and may add or override its own.
    /// - Disposing a scope releases only what that scope owns.
    /// - Parents do NOT track children: disposing a parent does not cascade. Dispose child
    ///   scopes before their parent; a singleton owned by a disposed parent can no longer
    ///   be resolved from a still-live child.
    /// </summary>
    public interface IScopeFactory
    {
        /// <summary>
        /// Creates a child scope that inherits parent registrations.
        /// Throws <see cref="System.ObjectDisposedException"/> if the parent is disposed.
        /// </summary>
        IDiScope CreateScope();
    }
}
