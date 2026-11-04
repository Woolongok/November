using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEngine.Scripting;

namespace November.DI
{
    /// <summary>
    /// Func&lt;T&gt; / Lazy&lt;T&gt; support.
    ///
    /// AOT / IL2CPP: the closed wrappers are created with MakeGenericMethod, which is safe for reference-type
    /// arguments (shared generic code) but needs an explicit instantiation for value types that never appear as a
    /// generic argument in compiled code. Services are almost always reference types; if you do resolve
    /// Func&lt;SomeStruct&gt;, reference that exact type once in your own code. The wrappers carry [Preserve] and
    /// the package ships a link.xml so stripping cannot remove them.
    ///
    /// The generic wrapper is turned into a typed delegate once per T, so each resolve is a delegate call:
    /// no MethodInfo.Invoke and no object[] per call.
    /// </summary>
    public sealed partial class DiContainer
    {
        // The container is main-thread-only, so ordinary dictionaries avoid concurrent-collection
        // overhead and keep every static cache under the same threading contract.
        private static readonly Dictionary<Type, Func<DiContainer, object>> s_funcCreators =
            new Dictionary<Type, Func<DiContainer, object>>();

        private static readonly Dictionary<Type, Func<DiContainer, object>> s_lazyCreators =
            new Dictionary<Type, Func<DiContainer, object>>();

        private static readonly Func<Type, Func<DiContainer, object>> s_buildFuncCreator = BuildFuncCreator;
        private static readonly Func<Type, Func<DiContainer, object>> s_buildLazyCreator = BuildLazyCreator;

        private object CreateFunc(Type returnType)
        {
            if (!s_funcCreators.TryGetValue(returnType, out var creator))
            {
                creator = s_buildFuncCreator(returnType);
                s_funcCreators.Add(returnType, creator);
            }
            return creator(this);
        }

        private object CreateLazy(Type valueType)
        {
            if (!s_lazyCreators.TryGetValue(valueType, out var creator))
            {
                creator = s_buildLazyCreator(valueType);
                s_lazyCreators.Add(valueType, creator);
            }
            return creator(this);
        }

        private static Func<DiContainer, object> BuildFuncCreator(Type returnType)
            => BuildCreator(nameof(MakeFunc), returnType);

        private static Func<DiContainer, object> BuildLazyCreator(Type valueType)
            => BuildCreator(nameof(MakeLazy), valueType);

        private static Func<DiContainer, object> BuildCreator(string methodName, Type argument)
        {
            var method = typeof(DiContainer).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            return (Func<DiContainer, object>)method
                .MakeGenericMethod(argument)
                .CreateDelegate(typeof(Func<DiContainer, object>));
        }

        // Each invocation is a fresh resolution through the public API (disposed / thread guards included).
        [Preserve]
        private static object MakeFunc<T>(DiContainer container)
            => new Func<T>(() => (T)container.Resolve(typeof(T)));

        // PublicationOnly: a failed .Value is not cached, so it can succeed on a later call (for example after
        // the missing service has been registered). The default mode would rethrow the first exception forever.
        [Preserve]
        private static object MakeLazy<T>(DiContainer container)
            => new Lazy<T>(() => (T)container.Resolve(typeof(T)), LazyThreadSafetyMode.PublicationOnly);

        /// <summary>Called when the domain is reset with "Enter Play Mode Options" (statics survive play sessions).</summary>
        internal static void ClearStaticCaches()
        {
            s_funcCreators.Clear();
            s_lazyCreators.Clear();
        }
    }
}
