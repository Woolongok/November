using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using November.DI;
using UnityEngine;

namespace November.DI.Tests
{
    /// <summary>
    /// Ownership semantics: which scope builds, caches and disposes what. Covers inherited transient and
    /// scoped services, ResolveAll / collection injection, disposed containers, scope validation,
    /// replaced registrations and disposal order.
    /// </summary>
    internal class DiContainerLifetimeTests
    {
        private DiContainer _container;

        // ── Test subjects ───────────────────────────────────────────

        private interface IDep { }
        private sealed class DepImpl : IDep { }
        private sealed class OtherDepImpl : IDep { }

        private sealed class NeedsDep
        {
            public IDep Dep { get; }
            public NeedsDep(IDep dep) => Dep = dep;
        }

        private interface IService { }
        private sealed class ServiceImpl : IService { }
        private sealed class OtherServiceImpl : IService { }

        private sealed class DisposableService : IService, IDisposable
        {
            public int DisposeCount;
            public void Dispose() => DisposeCount++;
        }

        private sealed class ExternalDisposable : IDep, IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        private sealed class HoldsDisposable
        {
            public DisposableService Held { get; }
            public HoldsDisposable(DisposableService held) => Held = held;
        }

        private sealed class Recording : IDisposable
        {
            private readonly string _name;
            private readonly List<string> _log;
            public Recording(string name, List<string> log) { _name = name; _log = log; }
            public void Dispose() => _log.Add(_name);
        }

        private sealed class NeedsCollections
        {
            public IEnumerable<IService> Enumerable { get; }
            public IReadOnlyList<IService> List { get; }
            public IService[] Array { get; }

            public NeedsCollections(IEnumerable<IService> enumerable, IReadOnlyList<IService> list, IService[] array)
            {
                Enumerable = enumerable;
                List = list;
                Array = array;
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

        // ── Inherited transient: built by the resolving scope ───────

        [Test]
        public void InheritedTransient_IsBuiltByTheResolvingScope_NotByTheAncestor()
        {
            _container.Register<IDep, DepImpl>(Lifetime.Scoped);
            _container.Register<NeedsDep>(Lifetime.Transient);

            using (var scopeA = _container.CreateScope())
            using (var scopeB = _container.CreateScope())
            {
                var fromA = scopeA.Resolve<NeedsDep>();
                var fromB = scopeB.Resolve<NeedsDep>();

                Assert.AreSame(scopeA.Resolve<IDep>(), fromA.Dep, "must see scope A's scoped dependency");
                Assert.AreSame(scopeB.Resolve<IDep>(), fromB.Dep, "must see scope B's scoped dependency");
                Assert.AreNotSame(fromA.Dep, fromB.Dep);
            }
        }

        [Test]
        public void InheritedTransientDisposable_IsReleasedWithTheScope_NotKeptByTheRoot()
        {
            _container.Register<DisposableService>(Lifetime.Transient);

            DisposableService resolved;
            using (var scope = _container.CreateScope())
                resolved = scope.Resolve<DisposableService>();

            Assert.AreEqual(1, resolved.DisposeCount, "disposed when the scope that resolved it ended");

            _container.Dispose();
            Assert.AreEqual(1, resolved.DisposeCount, "the root must not dispose it again");
        }

        [Test]
        public void Singleton_IsBuiltFromItsOwnersRegistrations_AndSharedAcrossScopes()
        {
            _container.Register<IDep, DepImpl>(Lifetime.Transient);
            _container.Register<NeedsDep>(Lifetime.Singleton);

            using (var scopeA = _container.CreateScope())
            using (var scopeB = _container.CreateScope())
            {
                scopeA.Register<IDep, OtherDepImpl>(Lifetime.Transient); // shadows in scope A only

                var singleton = scopeA.Resolve<NeedsDep>();

                Assert.IsInstanceOf<DepImpl>(singleton.Dep, "a singleton is built from its owner's view");
                Assert.AreSame(singleton, scopeB.Resolve<NeedsDep>());
                Assert.AreSame(singleton, _container.Resolve<NeedsDep>());
            }
        }

        // ── ResolveAll and collection injection ─────────────────────

        [Test]
        public void ResolveAll_BuildsScopedBindingsInTheResolvingScope()
        {
            _container.Register<IService, ServiceImpl>(Lifetime.Scoped);

            using (var scopeA = _container.CreateScope())
            using (var scopeB = _container.CreateScope())
            {
                var viaResolve = scopeA.Resolve<IService>();
                var viaResolveAll = scopeA.ResolveAll<IService>().Single();

                Assert.AreSame(viaResolve, viaResolveAll);
                Assert.AreNotSame(viaResolveAll, scopeB.ResolveAll<IService>().Single());
            }
        }

        [Test]
        public void CollectionParameters_ReceiveEveryBinding_InRegistrationOrder()
        {
            _container.Add<IService, ServiceImpl>(Lifetime.Singleton);
            _container.Add<IService, OtherServiceImpl>(Lifetime.Singleton);
            _container.Register<NeedsCollections>();

            var resolved = _container.Resolve<NeedsCollections>();

            Assert.AreEqual(2, resolved.Enumerable.Count());
            Assert.AreEqual(2, resolved.List.Count);
            Assert.AreEqual(2, resolved.Array.Length);
            Assert.IsInstanceOf<ServiceImpl>(resolved.Array[0]);
            Assert.IsInstanceOf<OtherServiceImpl>(resolved.Array[1]);
        }

        // ── Disposed containers ─────────────────────────────────────

        [Test]
        public void AfterDispose_EveryEntryPoint_ThrowsObjectDisposedException()
        {
            _container.Register<IService, ServiceImpl>();
            _container.Dispose();

            Assert.Throws<ObjectDisposedException>(() => _container.Resolve<IService>());
            Assert.Throws<ObjectDisposedException>(() => _container.Resolve(typeof(IService)));
            Assert.Throws<ObjectDisposedException>(() => _container.TryResolve<IService>(out _));
            Assert.Throws<ObjectDisposedException>(() => _container.ResolveAll<IService>());
            Assert.Throws<ObjectDisposedException>(() => _container.HasRegistration(typeof(IService)));
            Assert.Throws<ObjectDisposedException>(() => _container.InjectInto(new object()));
            Assert.Throws<ObjectDisposedException>(() => _container.InjectInto(null));
            Assert.Throws<ObjectDisposedException>(() => _container.InstantiatePrefab<GameObject>(null));
            Assert.Throws<ObjectDisposedException>(() => _container.InjectGameObjectHierarchy(null));
            Assert.Throws<ObjectDisposedException>(() => _container.DescribeRegistrations());
            Assert.Throws<ObjectDisposedException>(() => _container.CreateScope());
            Assert.Throws<ObjectDisposedException>(() => _container.Register<IDep, DepImpl>());
            Assert.Throws<ObjectDisposedException>(() => _container.RegisterInstance<IDep>(new DepImpl()));
            Assert.Throws<ObjectDisposedException>(() => _container.Validate());
        }

        [Test]
        public void ChildOfADisposedParent_GetsAnAccurateError_ForAnInheritedSingleton()
        {
            _container.Register<IService, ServiceImpl>(Lifetime.Singleton);
            _container.Register<IDep, DepImpl>(Lifetime.Scoped);
            _container.Register<NeedsDep>(Lifetime.Transient);

            var child = _container.CreateScope();
            _container.Dispose();

            var ex = Assert.Throws<ObjectDisposedException>(() => child.Resolve<IService>());
            Assert.IsTrue(ex.Message.Contains("owns it"), "should blame the owning parent, not the child: " + ex.Message);

            // Scoped and transient services are built by the child itself and keep working.
            Assert.IsNotNull(child.Resolve<NeedsDep>());
            child.Dispose();
        }

        // ── Scope validation ────────────────────────────────────────

        [Test]
        public void ScopedResolvedFromTheRoot_Throws_WhenScopeValidationIsOn()
        {
            _container.Register<IService, ServiceImpl>(Lifetime.Scoped);

            var ex = Assert.Throws<InvalidOperationException>(() => _container.Resolve<IService>());
            Assert.IsTrue(ex.Message.Contains("root container"), ex.Message);
        }

        [Test]
        public void ScopedResolvedFromTheRoot_ActsLikeASingleton_WhenScopeValidationIsOff()
        {
            using var container = new DiContainer(options: new DiContainerOptions { ValidateScopes = false });
            container.Register<IService, ServiceImpl>(Lifetime.Scoped);

            Assert.AreSame(container.Resolve<IService>(), container.Resolve<IService>());
        }

        [Test]
        public void SingletonCapturingAScopedService_IsReportedAsACaptiveDependency()
        {
            _container.Register<IDep, DepImpl>(Lifetime.Scoped);
            _container.Register<NeedsDep>(Lifetime.Singleton);

            using (var scope = _container.CreateScope())
            {
                var ex = Assert.Throws<InvalidOperationException>(() => scope.Resolve<NeedsDep>());
                Assert.IsTrue(ex.Message.Contains("Captive"), ex.Message);
            }
        }

        // ── Replaced and shadowed registrations ─────────────────────

        [Test]
        public void LocalScopedOverride_AfterAnInheritedScopedResolve_IsNotShadowedByTheCache()
        {
            _container.Register<IDep, DepImpl>(Lifetime.Scoped);

            using (var scope = _container.CreateScope())
            {
                Assert.IsInstanceOf<DepImpl>(scope.Resolve<IDep>());

                scope.Register<IDep, OtherDepImpl>(Lifetime.Scoped);

                Assert.IsInstanceOf<OtherDepImpl>(scope.Resolve<IDep>());
            }
        }

        [Test]
        public void Replace_DropsTheCachedSingletonOfTheOldBinding()
        {
            _container.Register<IDep, DepImpl>(Lifetime.Singleton);
            Assert.IsInstanceOf<DepImpl>(_container.Resolve<IDep>());

            _container.Replace<IDep, OtherDepImpl>(Lifetime.Singleton);

            Assert.IsInstanceOf<OtherDepImpl>(_container.Resolve<IDep>());
        }

        // ── Transient disposables resolved from the root ────────────

        private static DiContainer NewContainerWithRootWarning(bool warn)
            => new DiContainer(options: new DiContainerOptions { WarnOnRootTransientDisposables = warn });

        [Test]
        public void RootTransientDisposable_WarnsOncePerType_WithTheScopeAdvice()
        {
            using (var container = NewContainerWithRootWarning(true))
            using (var logs = new LogCounter())
            {
                container.Register<DisposableService>(Lifetime.Transient);

                container.Resolve<DisposableService>();
                container.Resolve<DisposableService>();

                Assert.AreEqual(1, logs.Count(LogType.Warning, "CreateScope"), logs.ToString());
                Assert.AreEqual(1, logs.Total, "one warning for the type, nothing else");
            }
        }

        [Test]
        public void TransientDisposableResolvedThroughAScope_DoesNotWarn()
        {
            using (var container = NewContainerWithRootWarning(true))
            using (var logs = new LogCounter())
            {
                container.Register<DisposableService>(Lifetime.Transient);

                using (var scope = container.CreateScope())
                    scope.Resolve<DisposableService>();

                Assert.AreEqual(0, logs.Total, logs.ToString());
            }
        }

        [Test]
        public void RootTransientDisposable_HeldByASingleton_DoesNotWarn()
        {
            using (var container = NewContainerWithRootWarning(true))
            using (var logs = new LogCounter())
            {
                container.Register<DisposableService>(Lifetime.Transient);
                container.Register<HoldsDisposable>(Lifetime.Singleton);

                container.Resolve<HoldsDisposable>();

                Assert.AreEqual(0, logs.Total, "the singleton owns it for as long as the root lives: not a leak");
            }
        }

        [Test]
        public void RootTransientDisposableWarning_CanBeSwitchedOff()
        {
            using (var container = NewContainerWithRootWarning(false))
            using (var logs = new LogCounter())
            {
                container.Register<DisposableService>(Lifetime.Transient);

                container.Resolve<DisposableService>();

                Assert.AreEqual(0, logs.Total);
            }
        }

        // ── Disposal order and ownership ────────────────────────────

        [Test]
        public void Dispose_RunsInReverseCreationOrder()
        {
            var order = new List<string>();
            _container.RegisterFactory<Recording>(p => new Recording("first", order), Lifetime.Singleton);
            _container.RegisterFactory<IDisposable>(p => new Recording("second", order), Lifetime.Singleton);

            _container.Resolve<Recording>();
            _container.Resolve<IDisposable>();
            _container.Dispose();

            CollectionAssert.AreEqual(new[] { "second", "first" }, order);
        }

        [Test]
        public void Dispose_NeverDisposesRegisteredInstances()
        {
            var external = new ExternalDisposable();
            _container.RegisterInstance<IDep>(external);
            _container.Resolve<IDep>();

            _container.Dispose();

            Assert.IsFalse(external.Disposed, "RegisterInstance hands over a value the caller still owns.");
        }
    }
}
