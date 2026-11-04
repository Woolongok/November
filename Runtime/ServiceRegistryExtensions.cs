using System;

namespace November.DI
{
    /// <summary>Generic shorthands over <see cref="IServiceRegistry"/> / <see cref="IServiceResolver"/>.</summary>
    public static class ServiceRegistryExtensions
    {
        // ── Self-binding: Register<Foo>() == Register<Foo, Foo>() ──────────────
        public static void Register<TImpl>(this IServiceRegistry registry, Lifetime lifetime = Lifetime.Transient)
            where TImpl : class
            => registry.Register(typeof(TImpl), typeof(TImpl), lifetime);

        // ── Lifetime-named shorthands ─────────────────────────────────────────
        public static void RegisterSingleton<TService, TImpl>(this IServiceRegistry registry) where TImpl : class, TService
            => registry.Register(typeof(TService), typeof(TImpl), Lifetime.Singleton);

        public static void RegisterScoped<TService, TImpl>(this IServiceRegistry registry) where TImpl : class, TService
            => registry.Register(typeof(TService), typeof(TImpl), Lifetime.Scoped);

        public static void RegisterTransient<TService, TImpl>(this IServiceRegistry registry) where TImpl : class, TService
            => registry.Register(typeof(TService), typeof(TImpl), Lifetime.Transient);

        public static void RegisterSingleton<TImpl>(this IServiceRegistry registry) where TImpl : class
            => registry.Register(typeof(TImpl), typeof(TImpl), Lifetime.Singleton);

        public static void RegisterScoped<TImpl>(this IServiceRegistry registry) where TImpl : class
            => registry.Register(typeof(TImpl), typeof(TImpl), Lifetime.Scoped);

        public static void RegisterTransient<TImpl>(this IServiceRegistry registry) where TImpl : class
            => registry.Register(typeof(TImpl), typeof(TImpl), Lifetime.Transient);

        // ── Aliases: one instance exposed under several service types ─────────
        public static void RegisterAlias<TAlias, TTarget>(this IServiceRegistry registry) where TTarget : TAlias
            => registry.RegisterAlias(typeof(TAlias), typeof(TTarget));

        // ── Multi-binding and explicit replacement ────────────────────────────
        public static void Add<TService, TImpl>(this IServiceRegistry registry, Lifetime lifetime = Lifetime.Transient)
            where TImpl : class, TService
            => registry.Add(typeof(TService), typeof(TImpl), lifetime);

        public static void Replace<TService, TImpl>(this IServiceRegistry registry, Lifetime lifetime = Lifetime.Transient)
            where TImpl : class, TService
            => registry.Replace(typeof(TService), typeof(TImpl), lifetime);

        public static void ReplaceInstance<TService>(this IServiceRegistry registry, TService instance)
            => registry.ReplaceInstance(typeof(TService), instance);

        public static void ReplaceFactory<TService>(
            this IServiceRegistry registry,
            Func<IDiProvider, TService> factory,
            Lifetime lifetime = Lifetime.Transient)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            registry.ReplaceFactory(typeof(TService), p => factory(p), lifetime);
        }

        // ── Resolver convenience ──────────────────────────────────────────────
        public static bool HasRegistration<TService>(this IServiceResolver resolver)
            => resolver.HasRegistration(typeof(TService));
    }
}
