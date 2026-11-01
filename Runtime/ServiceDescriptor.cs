using System;

namespace November.DI
{

    /// <summary>
    /// Describes a single service registration within the container.
    /// Internal — consumers interact via IServiceRegistry / IServiceResolver.
    /// </summary>
    internal sealed class ServiceDescriptor
    {
        public Type ImplementationType { get; }
        public Lifetime Lifetime { get; }
        public Func<DiContainer, object> Factory { get; }
        public bool IsExternalInstance { get; }

        public ServiceDescriptor(
            Type implementationType,
            Lifetime lifetime,
            Func<DiContainer, object> factory = null,
            bool isExternalInstance = false)
        {
            ImplementationType = implementationType ?? throw new ArgumentNullException(nameof(implementationType));
            Lifetime = lifetime;
            Factory = factory;
            IsExternalInstance = isExternalInstance;
        }
    }

}
