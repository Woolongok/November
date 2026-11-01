using System;
using UnityEngine;

namespace November.DI
{

    /// <summary>
    /// DiContainer disposal partition: scope teardown and guards.
    /// Same type via partial — no behaviour change.
    /// </summary>
    public sealed partial class DiContainer : IDiScope, IServiceProvider, IScopeFactory
    {
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Dispose tracked disposables in reverse order
            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                try { _disposables[i].Dispose(); }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[DiContainer] Exception disposing '{_disposables[i].GetType().Name}':\n{ex}");
                }
            }
            _disposables.Clear();

            _singletons.Clear();
            _scoped.Clear();
            _resolvingLocal.Clear();
            _constructingLocal.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DiContainer),
                    "This DiContainer has been disposed. Create a new scope to continue.");
        }

        private void AssertMainThread(string context)
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != s_mainThreadId)
                throw new InvalidOperationException(
                    $"[DiContainer] '{context}' must be called from the Unity main thread. " +
                    $"Current thread: {System.Threading.Thread.CurrentThread.ManagedThreadId}, " +
                    $"main thread: {s_mainThreadId}.");
        }
    }

}
