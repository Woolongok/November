using System;
using System.Collections.Generic;
using System.Reflection;

namespace November.DI
{

    /// <summary>
    /// DiContainer factory partition: Func&lt;T&gt; / Lazy&lt;T&gt; open-generic support.
    /// Same type via partial — no behaviour change.
    /// </summary>
    public sealed partial class DiContainer : IDiScope, IServiceProvider, IScopeFactory
    {
        // ── AOT / IL2CPP WARNING ───────────────────────────────────────
        // MakeGenericMethod with a runtime-only Type is NOT safe under full-AOT
        // backends (IL2CPP, WebGL). If Resolve(typeof(Func<T>)) is called for a T
        // that never otherwise appears as a generic argument in compiled code,
        // it throws ExecutionEngineException at runtime. Mitigation: reference
        // typeof(Func<YourType>) / typeof(Lazy<YourType>) in an AotGenericHints.cs file.
        private static readonly Dictionary<Type, MethodInfo> _factoryWrapperCache = new();
        private static readonly Dictionary<Type, MethodInfo> _lazyWrapperCache = new();
        private static readonly object _factoryCacheLock = new();

        private static object CreateTypedFactory(Type returnType, Func<object> inner)
        {
            MethodInfo method;
            lock (_factoryCacheLock)
            {
                if (!_factoryWrapperCache.TryGetValue(returnType, out method))
                {
                    method = typeof(DiContainer)
                        .GetMethod(nameof(WrapFactory), BindingFlags.NonPublic | BindingFlags.Static)!
                        .MakeGenericMethod(returnType);
                    _factoryWrapperCache[returnType] = method;
                }
            }

            return method.Invoke(null, new object[] { inner });
        }

        internal static Func<T> WrapFactory<T>(Func<object> inner) => () => (T)inner();

        private object CreateTypedLazy(Type innerType)
        {
            MethodInfo method;
            lock (_factoryCacheLock)
            {
                if (!_lazyWrapperCache.TryGetValue(innerType, out method))
                {
                    method = typeof(DiContainer)
                        .GetMethod(nameof(WrapLazyInstance), BindingFlags.NonPublic | BindingFlags.Instance)!
                        .MakeGenericMethod(innerType);
                    _lazyWrapperCache[innerType] = method;
                }
            }

            return method.Invoke(this, null);
        }

        internal Lazy<T> WrapLazyInstance<T>() => new(() => (T)Resolve(typeof(T)));
    }

}
