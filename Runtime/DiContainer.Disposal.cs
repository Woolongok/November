using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine;

namespace November.DI
{
    /// <summary>
    /// Disposal partition: Dispose / DisposeAsync and disposal tracking.
    ///
    /// A scope disposes, in reverse creation order, every IDisposable / IAsyncDisposable it built and tracked:
    /// its singletons, its scoped services, and the transient disposables resolved THROUGH it. Instances
    /// registered with RegisterInstance are never disposed. The same object is never disposed twice, even if
    /// two registrations hand it out. Parents do not cascade to child scopes.
    /// </summary>
    public sealed partial class DiContainer
    {
        public void Dispose()
        {
            MainThread.Assert(nameof(Dispose));
            if (_disposed) return;
            _disposed = true;

            var items = _disposables;
            if (items != null)
            {
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    var item = items[i];
                    try { DisposeSynchronously(item); }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[DiContainer] Exception disposing '{item.GetType().Name}':\n{ex}");
                    }
                }
            }

            Release();
        }

        /// <summary>
        /// Like <see cref="Dispose"/>, but awaits <see cref="IAsyncDisposable"/> instances (preferred over
        /// IDisposable when a type has both). Continues on the caller's context, so Unity-thread disposal stays on the main thread.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            MainThread.Assert(nameof(DisposeAsync));
            if (_disposed) return;
            _disposed = true;

            var items = _disposables;
            if (items != null)
            {
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    var item = items[i];
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

            Release();
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
            if (_tracked != null) _tracked.Clear();
            if (_instances != null) _instances.Clear();

            // The inactive prefab holder belongs to the whole chain: the root releases it. Registrations stay
            // readable so live children can still resolve inherited scoped and transient services.
            if (_parent == null) _state.ReleaseHolder();
        }

        /// <summary>Records a disposable for teardown; the same object is recorded once.</summary>
        private void Track(object instance)
        {
            if (!(instance is IDisposable) && !(instance is IAsyncDisposable)) return;

            if (_tracked == null) _tracked = new HashSet<object>(ReferenceComparer.Instance);
            if (!_tracked.Add(instance)) return;

            if (_disposables == null) _disposables = new List<object>();
            _disposables.Add(instance);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DiContainer),
                    "This DiContainer has been disposed. Create a new scope to continue.");
        }

        /// <summary>Identity comparison: UnityEngine.Object overrides Equals / GetHashCode and must not be used as a key.</summary>
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);
            int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
