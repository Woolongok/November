using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using November.DI;
using UnityEngine;
using UnityEngine.TestTools;

namespace November.DI.Tests
{
    /// <summary>
    /// Core contract of the hand-rolled DI container (DiContainer):
    /// lifetimes (transient/singleton/scoped), registration validation,
    /// fail-fast resolution, constructor + field/method injection, Func/Lazy
    /// generics, scope inheritance/override, disposal ordering, and cycle
    /// detection that throws instead of overflowing the stack.
    /// </summary>
    internal class DiContainerTests
    {
        private DiContainer _container;

        // ── Test subjects ───────────────────────────────────────────

        private interface IService { }

        private sealed class ServiceImpl : IService { public ServiceImpl() { } }

        private sealed class CountingService : IService
        {
            public static int Instances;
            public CountingService() => Instances++;
        }

        private interface IDep { }
        private sealed class DepImpl : IDep { public DepImpl() { } }

        /// <summary>Constructor-parameter injection.</summary>
        private sealed class NeedsDep
        {
            public IDep Dep { get; }
            public NeedsDep(IDep dep) => Dep = dep;
        }

        /// <summary>[Inject] field + method injection.</summary>
        private sealed class InjectTarget
        {
            [Inject] public IDep FieldDep;
            [Inject] public IDep MethodDep;
            public bool MethodRan;

            [Inject]
            public void Configure(IDep dep)
            {
                MethodRan = true;
                MethodDep = dep;
            }
        }

        private sealed class CycleA { public CycleA(CycleB b) { } }
        private sealed class CycleB { public CycleB(CycleA a) { } }

        private sealed class DisposableA : IService, IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        private sealed class DisposableB : IService, IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        private sealed class ScopedDisposable : IService, IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        private sealed class DependingService : IService
        {
            public IDep Dep { get; }
            public DependingService(IDep dep) => Dep = dep;
        }

        private sealed class ScopeOnlyService : IService
        {
            public ScopeOnlyService() { }
        }

        // ── Lifecycle ───────────────────────────────────────────────

        [SetUp]
        public void SetUp()
        {
            CountingService.Instances = 0;
            _container = new DiContainer();
        }

        [TearDown]
        public void TearDown()
        {
            _container?.Dispose();
            _container = null;
        }

        // ── Lifetimes ───────────────────────────────────────────────

        [Test]
        public void Resolve_Transient_ReturnsNewInstanceEachTime()
        {
            _container.Register<IService, ServiceImpl>(Lifetime.Transient);

            var first = _container.Resolve<IService>();
            var second = _container.Resolve<IService>();

            Assert.AreNotSame(first, second);
        }

        [Test]
        public void Resolve_Singleton_ReturnsSameInstance()
        {
            _container.Register<IService, ServiceImpl>(Lifetime.Singleton);

            var first = _container.Resolve<IService>();
            var second = _container.Resolve<IService>();

            Assert.AreSame(first, second);
        }

        [Test]
        public void Resolve_Scoped_IsSharedWithinScope_UniqueAcrossScopes()
        {
            _container.Register<IService, ServiceImpl>(Lifetime.Scoped);

            using (var scopeA = _container.CreateScope())
            using (var scopeB = _container.CreateScope())
            {
                Assert.AreSame(scopeA.Resolve<IService>(), scopeA.Resolve<IService>());
                Assert.AreSame(scopeB.Resolve<IService>(), scopeB.Resolve<IService>());
                Assert.AreNotSame(scopeA.Resolve<IService>(), scopeB.Resolve<IService>());
            }
        }

        [Test]
        public void RegisterInstance_AlwaysReturnsTheSameInstance()
        {
            var instance = new ServiceImpl();
            _container.RegisterInstance<IService>(instance);

            Assert.AreSame(instance, _container.Resolve<IService>());
        }

        [Test]
        public void RegisterInstance_DuplicateRegistration_Throws()
        {
            _container.RegisterInstance<IService>(new ServiceImpl());

            Assert.Throws<InvalidOperationException>(
                () => _container.RegisterInstance<IService>(new ServiceImpl()));
        }

        // ── Registration validation ─────────────────────────────────

        [Test]
        public void Register_AbstractImplementation_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => _container.Register<IService, IService>());
        }

        [Test]
        public void Register_NonAssignablePair_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => _container.Register(typeof(IService), typeof(DepImpl)));
        }

        [Test]
        public void Register_UnityComponentImplementation_Throws()
        {
            Assert.Throws<ArgumentException>(() => _container.Register<object, Transform>());
        }

        // ── Resolution ──────────────────────────────────────────────

        [Test]
        public void Resolve_UnregisteredInterface_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => _container.Resolve<IService>());
        }

        [Test]
        public void Resolve_UnregisteredConcreteType_Throws_NoAutoCreation()
        {
            Assert.Throws<InvalidOperationException>(() => _container.Resolve<ServiceImpl>());
        }

        [Test]
        public void TryResolve_Unregistered_ReturnsFalse()
        {
            LogAssert.Expect(LogType.Warning, new Regex("TryResolve"));
            Assert.IsFalse(_container.TryResolve<IService>(out var result));
            Assert.IsNull(result);
        }

        [Test]
        public void TryResolve_CircularDependency_Rethrows_InsteadOfMaskingAsMiss()
        {
            _container.Register<CycleA, CycleA>();
            _container.Register<CycleB, CycleB>();

            Assert.Throws<InvalidOperationException>(() => _container.TryResolve<CycleA>(out _));
        }

        [Test]
        public void TryResolve_SingletonConstructionFailure_Rethrows_InsteadOfMaskingAsMiss()
        {
            _container.RegisterFactory<IService>(c => throw new InvalidOperationException("boom"), Lifetime.Singleton);

            // The container no longer logs on the way up: the exception itself carries the diagnosis.
            Assert.Throws<InvalidOperationException>(() => _container.TryResolve<IService>(out _));
        }

        [Test]
        public void Resolve_ConstructorParameters_AreResolved()
        {
            _container.Register<IDep, DepImpl>();
            _container.Register<NeedsDep, NeedsDep>();

            var resolved = _container.Resolve<NeedsDep>();

            Assert.IsInstanceOf<DepImpl>(resolved.Dep);
        }

        [Test]
        public void Resolve_CircularConstructorDependency_Throws_NoStackOverflow()
        {
            _container.Register<CycleA, CycleA>();
            _container.Register<CycleB, CycleB>();

            Assert.Throws<InvalidOperationException>(() => _container.Resolve<CycleA>());
        }

        [Test]
        public void HasRegistration_IsVisibleThroughParentChain()
        {
            _container.Register<IService, ServiceImpl>();
            using (var scope = _container.CreateScope())
            {
                Assert.IsTrue(scope.HasRegistration(typeof(IService)));
                Assert.IsFalse(scope.HasRegistration(typeof(IDep)));
            }
            Assert.IsTrue(_container.HasRegistration(typeof(IService)));
        }

        [Test]
        public void ResolveAll_CombinesParentAndScopeRegistrations()
        {
            _container.Register<IService, ServiceImpl>(Lifetime.Singleton);
            using (var scope = _container.CreateScope())
            {
                scope.Register<IService, CountingService>(Lifetime.Singleton);

                var all = new List<IService>(scope.ResolveAll<IService>());

                Assert.AreEqual(2, all.Count);
            }
        }

        [Test]
        public void ResolveAll_IncludesAncestorsFirst_AndCanExcludeThem()
        {
            _container.Add<IService, ServiceImpl>();
            _container.Add<IService, CountingService>();

            using (var scope = _container.CreateScope())
            {
                scope.Add<IService, ScopeOnlyService>();

                var all = new List<IService>(scope.ResolveAll<IService>());
                var local = new List<IService>(scope.ResolveAll<IService>(includeParents: false));

                Assert.AreEqual(3, all.Count);
                Assert.IsInstanceOf<ServiceImpl>(all[0]);
                Assert.IsInstanceOf<CountingService>(all[1]);
                Assert.IsInstanceOf<ScopeOnlyService>(all[2]);

                Assert.AreEqual(1, local.Count);
                Assert.IsInstanceOf<ScopeOnlyService>(local[0]);
            }
        }

        [Test]
        public void PrettyName_FormatsGenericTypesWithoutMetadataArity()
        {
            Assert.AreEqual("Func<IService>", DiContainer.PrettyName(typeof(Func<IService>)));
        }

        // ── Factories ───────────────────────────────────────────────

        [Test]
        public void RegisterFactory_ReceivesProvider_AndCanResolveDependencies()
        {
            _container.Register<IDep, DepImpl>();
            _container.RegisterFactory<IService>(provider => new DependingService(provider.Resolve<IDep>()));

            var resolved = _container.Resolve<IService>();

            Assert.IsInstanceOf<DepImpl>(((DependingService)resolved).Dep);
        }

        [Test]
        public void Resolve_FuncGeneric_ProducesNewInstancePerInvocation()
        {
            _container.Register<IService, CountingService>(Lifetime.Transient);

            var factory = _container.Resolve<Func<IService>>();

            factory.Invoke();
            factory.Invoke();

            Assert.AreEqual(2, CountingService.Instances);
        }

        [Test]
        public void Resolve_LazyGeneric_DefersConstructionUntilValue()
        {
            _container.Register<IService, CountingService>(Lifetime.Transient);

            var lazy = _container.Resolve<Lazy<IService>>();
            Assert.AreEqual(0, CountingService.Instances, "Lazy must not construct on resolve.");

            GC.KeepAlive(lazy.Value);

            Assert.AreEqual(1, CountingService.Instances);
        }

        // ── Injection ───────────────────────────────────────────────

        [Test]
        public void InjectInto_FillsInjectFields_AndInvokesInjectMethods()
        {
            // Singleton so the field and the method both receive the SAME instance.
            _container.Register<IDep, DepImpl>(Lifetime.Singleton);
            var target = new InjectTarget();

            _container.InjectInto(target);

            Assert.IsInstanceOf<DepImpl>(target.FieldDep);
            Assert.IsTrue(target.MethodRan);
            Assert.AreSame(target.FieldDep, target.MethodDep);
        }

        [Test]
        public void CreatedServices_ReceiveFieldInjection()
        {
            _container.Register<IDep, DepImpl>();
            _container.Register<InjectTarget, InjectTarget>();

            var resolved = _container.Resolve<InjectTarget>();

            Assert.IsInstanceOf<DepImpl>(resolved.FieldDep);
        }

        // ── Scope inheritance / override ────────────────────────────

        [Test]
        public void Scope_InheritsParentRegistrations_AndSupportsOverride()
        {
            var parentService = new ServiceImpl();
            _container.RegisterInstance<IService>(parentService);

            using (var scope = _container.CreateScope())
            {
                Assert.AreSame(parentService, scope.Resolve<IService>());

                scope.Register<IService, ServiceImpl>(Lifetime.Transient);
                Assert.AreNotSame(parentService, scope.Resolve<IService>());
                Assert.AreSame(parentService, _container.Resolve<IService>(),
                    "Scope override must not leak into the parent.");
            }
        }

        // ── Disposal ────────────────────────────────────────────────

        [Test]
        public void Dispose_ReleasesCreatedDisposables()
        {
            _container.Register<DisposableA, DisposableA>(Lifetime.Transient);
            _container.Register<DisposableB, DisposableB>(Lifetime.Transient);

            var a = _container.Resolve<DisposableA>();
            var b = _container.Resolve<DisposableB>();

            _container.Dispose();

            Assert.IsTrue(a.Disposed);
            Assert.IsTrue(b.Disposed);
        }

        [Test]
        public void Resolve_AfterDispose_ThrowsObjectDisposedException()
        {
            _container.Register<IService, ServiceImpl>();
            _container.Dispose();

            Assert.Throws<ObjectDisposedException>(() => _container.Resolve<IService>());
        }

        [Test]
        public void Dispose_ParentDoesNotCascadeToChildScope()
        {
            _container.Register<ScopedDisposable, ScopedDisposable>(Lifetime.Scoped);
            var child = _container.CreateScope();
            var childService = child.Resolve<ScopedDisposable>();

            _container.Dispose();

            Assert.IsFalse(childService.Disposed,
                "Parent disposal must not cascade into child scopes (documented lifecycle).");
            child.Dispose();
            Assert.IsTrue(childService.Disposed,
                "Child's own disposal must release its tracked instances.");
        }
    }
}
