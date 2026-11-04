using System;
using System.Collections;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using November.DI;
using UnityEngine;
using UnityEngine.TestTools;

namespace November.DI.Tests
{
    /// <summary>
    /// Cross-scope ownership and disposal: a forwarded parent singleton must not become owned by a
    /// child, caller-owned instances must survive forwarding factories, replacement keeps deferred
    /// disposal, and every DisposeAsync caller awaits the same cleanup task.
    /// </summary>
    internal class DiContainerOwnershipTests
    {
        private DiContainer _container;

        // ── Test subjects ───────────────────────────────────────────

        private interface IShared { }
        private interface IExternal { }
        private interface IExternalAlias { }

        private sealed class SharedDisposable : IShared, IDisposable
        {
            public int DisposeCount;
            public void Dispose() => DisposeCount++;
        }

        private sealed class ExternalDisposable : IExternal, IExternalAlias, IDisposable
        {
            public int DisposeCount;
            public void Dispose() => DisposeCount++;
        }

        private sealed class GatedAsyncDisposable : IAsyncDisposable
        {
            public int DisposeCount;
            public readonly TaskCompletionSource<bool> Gate =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public async ValueTask DisposeAsync()
            {
                DisposeCount++;
                await Gate.Task;
            }
        }

        // ── Lifecycle ───────────────────────────────────────────────

        [SetUp]
        public void SetUp() => _container = new DiContainer();

        [TearDown]
        public void TearDown()
        {
            _container?.Dispose();
            _container = null;
        }

        // ── Parent-owned objects forwarded through a child ─────────

        [Test]
        public void ParentSingletonForwardedThroughAChildFactory_IsDisposedOnlyByTheParent()
        {
            _container.Register<SharedDisposable>(Lifetime.Singleton);
            var shared = _container.Resolve<SharedDisposable>();

            using (var scope = _container.CreateScope())
            {
                scope.RegisterFactory<IShared>(p => p.Resolve<SharedDisposable>(), Lifetime.Singleton);

                Assert.AreSame(shared, scope.Resolve<IShared>());
            }

            Assert.AreEqual(0, shared.DisposeCount, "the child must not dispose a parent-owned singleton");

            _container.Dispose();
            Assert.AreEqual(1, shared.DisposeCount);
        }

        [Test]
        public void ChildFactoryThatCreatesTheParentSingleton_DoesNotBecomeItsOwner()
        {
            _container.Register<SharedDisposable>(Lifetime.Singleton);

            SharedDisposable shared;
            using (var scope = _container.CreateScope())
            {
                scope.RegisterFactory<IShared>(p => p.Resolve<SharedDisposable>(), Lifetime.Singleton);
                shared = (SharedDisposable)scope.Resolve<IShared>();
            }

            Assert.AreEqual(0, shared.DisposeCount);

            _container.Dispose();
            Assert.AreEqual(1, shared.DisposeCount);
        }

        [Test]
        public void TwoScopesForwardingTheSameParentSingleton_DisposeItOnce()
        {
            _container.Register<SharedDisposable>(Lifetime.Singleton);
            var shared = _container.Resolve<SharedDisposable>();

            using (var first = _container.CreateScope())
            using (var second = _container.CreateScope())
            {
                first.RegisterFactory<IShared>(p => p.Resolve<SharedDisposable>(), Lifetime.Singleton);
                second.RegisterFactory<IShared>(p => p.Resolve<SharedDisposable>(), Lifetime.Singleton);

                Assert.AreSame(shared, first.Resolve<IShared>());
                Assert.AreSame(shared, second.Resolve<IShared>());
            }

            Assert.AreEqual(0, shared.DisposeCount);

            _container.Dispose();
            Assert.AreEqual(1, shared.DisposeCount);
        }

        [Test]
        public void CachedBorrowedSingleton_IsNotServedAfterItsOwnerIsDisposed()
        {
            _container.Register<SharedDisposable>(Lifetime.Singleton);

            using (var scope = _container.CreateScope())
            {
                scope.RegisterFactory<IShared>(p => p.Resolve<SharedDisposable>(), Lifetime.Singleton);
                Assert.IsNotNull(scope.Resolve<IShared>());

                _container.Dispose();

                Assert.Throws<ObjectDisposedException>(() => scope.Resolve<IShared>(),
                    "the child must not keep returning an object its owner already disposed");
            }
        }

        [Test]
        public void RegisteredInstanceForwardedThroughAFactory_IsNeverDisposed()
        {
            var external = new ExternalDisposable();
            _container.RegisterInstance<IExternal>(external);

            using (var scope = _container.CreateScope())
            {
                scope.RegisterFactory<IExternalAlias>(
                    p => (IExternalAlias)p.Resolve<IExternal>(), Lifetime.Singleton);

                Assert.AreSame(external, scope.Resolve<IExternalAlias>());
            }

            Assert.AreEqual(0, external.DisposeCount, "a child factory must not claim a RegisterInstance object");

            _container.Dispose();
            Assert.AreEqual(0, external.DisposeCount, "RegisterInstance is caller-owned");
        }

        // ── Replacement keeps deferred disposal ────────────────────

        [Test]
        public void ReplacingADisposableSingleton_DisposesBothInstancesOnceAtTeardown()
        {
            _container.Register<SharedDisposable>(Lifetime.Singleton);
            var first = _container.Resolve<SharedDisposable>();

            _container.Replace(typeof(SharedDisposable), typeof(SharedDisposable), Lifetime.Singleton);
            var second = _container.Resolve<SharedDisposable>();

            Assert.AreNotSame(first, second);

            _container.Dispose();

            Assert.AreEqual(1, first.DisposeCount);
            Assert.AreEqual(1, second.DisposeCount);
        }

        // ── Async disposal is one shared operation ─────────────────

        [UnityTest]
        public IEnumerator DisposeAsync_CalledTwice_AwaitsTheSameCleanup()
        {
            _container.Register<GatedAsyncDisposable>(Lifetime.Singleton);
            var item = _container.Resolve<GatedAsyncDisposable>();

            var first = _container.DisposeAsync().AsTask();
            var second = _container.DisposeAsync().AsTask();

            Assert.AreSame(first, second, "every caller must await the same disposal operation");
            Assert.IsFalse(first.IsCompleted);
            Assert.IsFalse(second.IsCompleted, "a second DisposeAsync must not return before cleanup finishes");

            item.Gate.SetResult(true);
            yield return WaitFor(Task.WhenAll(first, second));

            Assert.AreEqual(1, item.DisposeCount);
        }

        [UnityTest]
        public IEnumerator Dispose_WhileAsyncDisposalIsRunning_LogsAnError_AndDoesNotDisposeTwice()
        {
            _container.Register<GatedAsyncDisposable>(Lifetime.Singleton);
            var item = _container.Resolve<GatedAsyncDisposable>();

            var pending = _container.DisposeAsync().AsTask();
            Assert.IsFalse(pending.IsCompleted);

            using (var logs = new LogCounter())
            {
                LogAssert.Expect(LogType.Error, new Regex("DisposeAsync"));

                _container.Dispose();

                Assert.AreEqual(1, logs.Count(LogType.Error, "DisposeAsync"), logs.ToString());
            }

            item.Gate.SetResult(true);
            yield return WaitFor(pending);

            Assert.AreEqual(1, item.DisposeCount);
        }

        private static IEnumerator WaitFor(Task task)
        {
            for (int i = 0; i < 250 && !task.IsCompleted; i++)
            {
                Thread.Sleep(1);
                yield return null;
            }

            Assert.IsTrue(task.IsCompleted, "timed out waiting for disposal");
        }
    }
}
