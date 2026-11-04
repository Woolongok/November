using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace November.DI
{
    /// <summary>
    /// Disposal partition: Dispose / DisposeAsync and disposal tracking.
    ///
    /// A scope disposes, in reverse creation order, every IDisposable / IAsyncDisposable it built and
    /// owns: its singletons, its scoped services, and the transient disposables resolved THROUGH it.
    /// Instances registered with RegisterInstance are never disposed. Ownership is centralized in
    /// <see cref="ObjectOwnership"/>, so the same object is never disposed twice when several
    /// registrations or scopes hand it out. Parents do not cascade to child scopes.
    ///
    /// DisposeAsync is idempotent and all callers await the same completion task. Synchronous
    /// Dispose cannot wait for an asynchronous cleanup that has not completed; it logs an error
    /// naming the type instead of pretending the work finished.
    /// </summary>
    public sealed partial class DiContainer
    {
        public void Dispose()
        {
            MainThread.Assert(nameof(Dispose));

            if (_disposed)
            {
                if (_disposeTask != null && !_disposeTask.IsCompleted)
                    Debug.LogError(
                        "[DiContainer] Dispose() was called while DisposeAsync() is still running. " +
                        "Await DisposeAsync() to guarantee that asynchronous cleanup has finished.");
                return;
            }

            _disposed = true;
            var items = _disposables;
            if (items != null)
            {
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    var item = items[i];
                    if (!ObjectOwnership.ShouldDispose(this, item)) continue;

                    try { DisposeSynchronously(item); }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[DiContainer] Exception disposing '{item.GetType().Name}':\n{ex}");
                    }
                }
            }

            Release();
            _disposeTask = Task.CompletedTask;
        }

        /// <summary>
        /// Like <see cref="Dispose"/>, but awaits <see cref="IAsyncDisposable"/> instances (preferred over
        /// IDisposable when a type has both). Continues on the caller's context, so Unity-thread disposal stays on the main thread.
        /// Every call made while disposal is running awaits the same completion task.
        /// </summary>
        public ValueTask DisposeAsync()
        {
            MainThread.Assert(nameof(DisposeAsync));

            if (_disposeTask != null) return new ValueTask(_disposeTask);
            if (_disposed) return default;

            _disposed = true;
            _disposeTask = DisposeAsyncCore();
            return new ValueTask(_disposeTask);
        }

        private async Task DisposeAsyncCore()
        {
            try
            {
                var items = _disposables;
                if (items != null)
                {
                    for (int i = items.Count - 1; i >= 0; i--)
                    {
                        var item = items[i];
                        if (!ObjectOwnership.ShouldDispose(this, item)) continue;

                        try
                        {
                            if (item is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                            else if (item is IDisposable disposable) disposable.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[DiContainer] Exception disposing '{item.GetType().Name}':\n{ex}");
                        }
                    }
                }
            }
            finally
            {
                Release();
            }
        }

        private static void DisposeSynchronously(object item)
        {
            if (item is IDisposable disposable)
            {
                disposable.Dispose();
                return;
            }

            if (item is IAsyncDisposable asyncDisposable)
            {
                // Never block the main thread waiting on a task: only accept an async disposal that finished at once.
                var pending = asyncDisposable.DisposeAsync();
                if (pending.IsCompleted) pending.GetAwaiter().GetResult();
                else
                    Debug.LogError(
                        $"[DiContainer] '{item.GetType().Name}' implements only IAsyncDisposable and did not finish synchronously. " +
                        "Dispose its scope with 'await scope.DisposeAsync()' instead of Dispose().");
            }
        }

        private void Release()
        {
            if (_disposables != null) _disposables.Clear();
            if (_instances != null) _instances.Clear();

            // The inactive prefab holder belongs to the whole chain: the root releases it. Registrations stay
            // readable so live children can still resolve inherited scoped and transient services.
            if (_parent == null) _state.ReleaseHolder();
        }

        /// <summary>Records a disposable for teardown; ownership is claimed once for the whole process.</summary>
        private void Track(object instance)
        {
            if (!(instance is IDisposable) && !(instance is IAsyncDisposable)) return;

            // A factory may return an object another scope already owns (or a caller-owned instance
            // resolved through RegisterInstance). Never take ownership of somebody else's object.
            if (!ObjectOwnership.TryClaim(instance, this, out _)) return;

            if (_disposables == null) _disposables = new List<object>();
            _disposables.Add(instance);
        }

        /// <summary>An instance registration transfers ownership back to the caller, even if this scope tracked it before.</summary>
        private void ForgetTrackedInstance(object instance)
        {
            if (_disposables == null) return;

            for (int i = _disposables.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_disposables[i], instance))
                    _disposables.RemoveAt(i);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DiContainer),
                    "This DiContainer has been disposed. Create a new scope to continue.");
        }
    }
}
