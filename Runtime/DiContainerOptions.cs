namespace November.DI
{
    /// <summary>What a second <c>Register</c> for the same service type does in one container.</summary>
    public enum DuplicateRegistrationPolicy
    {
        /// <summary>Throw <see cref="System.InvalidOperationException"/>. Use <c>Replace</c> to override on purpose.</summary>
        Throw,

        /// <summary>
        /// Legacy behaviour: drop a previous type, factory, or alias binding and log a warning.
        /// Duplicate instance registrations still throw; use <c>ReplaceInstance</c> to replace one.
        /// </summary>
        ReplaceWithWarning
    }

    /// <summary>
    /// Behaviour switches for a container chain. The options object is copied when the root
    /// container is created and shared by every scope created from it.
    /// </summary>
    public sealed class DiContainerOptions
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private const bool DevelopmentDefault = true;
#else
        private const bool DevelopmentDefault = false;
#endif

        /// <summary>
        /// Rejects scoped services resolved from the root container and singletons that capture a
        /// scoped dependency (both silently behave like singletons otherwise). On by default in the
        /// Editor and development builds, off in release builds.
        /// </summary>
        public bool ValidateScopes { get; set; } = DevelopmentDefault;

        /// <summary>
        /// Warns (once per type) when a transient <c>IDisposable</c> is resolved directly from the
        /// root container: the root tracks it until the root itself is disposed, so per-action
        /// disposables resolved there accumulate. Resolve them through a short-lived scope.
        /// </summary>
        public bool WarnOnRootTransientDisposables { get; set; } = DevelopmentDefault;

        /// <summary>Logs a warning the first time <c>TryResolve&lt;T&gt;</c> misses for a type (per root container).</summary>
        public bool WarnOnTryResolveMiss { get; set; } = true;

        /// <summary>Default: <see cref="DuplicateRegistrationPolicy.Throw"/>.</summary>
        public DuplicateRegistrationPolicy DuplicateRegistrations { get; set; } = DuplicateRegistrationPolicy.Throw;

        internal DiContainerOptions Clone() => (DiContainerOptions)MemberwiseClone();
    }
}
