using System;
using System.Diagnostics;
using System.Threading;

namespace November.DI
{
    /// <summary>
    /// Main-thread bookkeeping for the single-threaded container.
    ///
    /// The id is captured when the type is first touched (which may be a worker thread in a test
    /// host) and RE-captured on the main thread when the Editor loads the domain and when the player
    /// starts, so the first thread to touch the container never decides which thread is "main".
    /// The checks compile away outside the Editor and development builds, so release resolves pay nothing.
    /// </summary>
    internal static class MainThread
    {
        private static int s_mainThreadId = Thread.CurrentThread.ManagedThreadId;

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Capture() => s_mainThreadId = Thread.CurrentThread.ManagedThreadId;

        public static bool IsMain => Thread.CurrentThread.ManagedThreadId == s_mainThreadId;

        /// <summary>Editor / development builds only. The message is built only when the check fails.</summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Assert(string context)
        {
            if (!IsMain) throw Wrong(context);
        }

        /// <summary>Same as <see cref="Assert(string)"/> for resolve calls; formats the type name only on failure.</summary>
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void Assert(Type resolving)
        {
            if (!IsMain) throw Wrong("Resolve(" + resolving.Name + ")");
        }

        /// <summary>Unconditional check (used by factory methods whose contract is "main thread only").</summary>
        public static void Require(string context)
        {
            if (!IsMain) throw Wrong(context);
        }

        private static InvalidOperationException Wrong(string context)
            => new InvalidOperationException(
                $"[DiContainer] '{context}' must be called from the Unity main thread. " +
                $"Current thread: {Thread.CurrentThread.ManagedThreadId}, main thread: {s_mainThreadId}.");
    }
}
