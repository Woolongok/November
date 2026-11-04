using System;

namespace November.DI
{
    /// <summary>How a registration produces its service.</summary>
    internal enum DescriptorKind
    {
        /// <summary>Constructor injection of <see cref="ServiceDescriptor.ImplementationType"/>.</summary>
        Type,
        /// <summary>A user delegate.</summary>
        Factory,
        /// <summary>An existing object owned by someone else.</summary>
        Instance,
        /// <summary>Forwards to another registration's result.</summary>
        Alias
    }

    /// <summary>
    /// One registration. Immutable, and used as the cache key for singleton and scoped instances,
    /// so replacing a registration can never leave a stale instance reachable through the old key.
    /// </summary>
    internal sealed class ServiceDescriptor
    {
        public DescriptorKind Kind { get; }
        public Type ServiceType { get; }
        public Lifetime Lifetime { get; }

        /// <summary>Concrete type for <see cref="DescriptorKind.Type"/>; the target service type for
        /// <see cref="DescriptorKind.Alias"/>; null otherwise.</summary>
        public Type ImplementationType { get; }

        public Func<IDiProvider, object> Factory { get; }
        public object Instance { get; }

        private ServiceDescriptor(DescriptorKind kind, Type serviceType, Lifetime lifetime,
                                  Type implementationType, Func<IDiProvider, object> factory, object instance)
        {
            Kind = kind;
            ServiceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
            Lifetime = lifetime;
            ImplementationType = implementationType;
            Factory = factory;
            Instance = instance;
        }

        public static ServiceDescriptor ForType(Type serviceType, Type implementationType, Lifetime lifetime)
            => new ServiceDescriptor(DescriptorKind.Type, serviceType, lifetime,
                implementationType ?? throw new ArgumentNullException(nameof(implementationType)), null, null);

        public static ServiceDescriptor ForFactory(Type serviceType, Func<IDiProvider, object> factory, Lifetime lifetime)
            => new ServiceDescriptor(DescriptorKind.Factory, serviceType, lifetime, null,
                factory ?? throw new ArgumentNullException(nameof(factory)), null);

        public static ServiceDescriptor ForInstance(Type serviceType, object instance)
            => new ServiceDescriptor(DescriptorKind.Instance, serviceType, Lifetime.Singleton, null, null,
                instance ?? throw new ArgumentNullException(nameof(instance)));

        public static ServiceDescriptor ForAlias(Type aliasType, Type targetType)
            => new ServiceDescriptor(DescriptorKind.Alias, aliasType, Lifetime.Transient,
                targetType ?? throw new ArgumentNullException(nameof(targetType)), null, null);

        /// <summary>An instance registration whose Unity object has been destroyed.</summary>
        public bool IsDestroyedInstance => Kind == DescriptorKind.Instance && Instance is UnityEngine.Object o && o == null;

        public string Describe()
        {
            switch (Kind)
            {
                case DescriptorKind.Type: return DiContainer.PrettyName(ImplementationType) + " (" + Lifetime + ")";
                case DescriptorKind.Factory: return "factory (" + Lifetime + ")";
                case DescriptorKind.Instance: return "instance of " + DiContainer.PrettyName(Instance.GetType());
                default: return "alias of " + DiContainer.PrettyName(ImplementationType);
            }
        }

        public override string ToString() => DiContainer.PrettyName(ServiceType) + " -> " + Describe();
    }
}
