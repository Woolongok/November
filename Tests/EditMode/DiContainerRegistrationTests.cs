using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using November.DI;
using UnityEngine;
using UnityEngine.TestTools;

namespace November.DI.Tests
{
    /// <summary>
    /// Registration contract: duplicates, Replace / Add, aliases, instances, implicit Func / Lazy /
    /// collection types, input validation, and TryResolve semantics.
    /// </summary>
    internal class DiContainerRegistrationTests
    {
        private DiContainer _container;
        private GameObject _gameObject;

        // ── Test subjects ───────────────────────────────────────────

        private interface IDep { }
        private sealed class DepImpl : IDep { }
        private sealed class OtherDepImpl : IDep { }

        private interface IService { }
        private sealed class ServiceImpl : IService { }
        private sealed class OtherServiceImpl : IService { }

        private interface IA { }
        private interface IB { }

        private sealed class Both : IA, IB, IDisposable
        {
            public int InjectCalls;
            public int DisposeCount;

            [Inject] public void Init(IDep dep) => InjectCalls++;
            public void Dispose() => DisposeCount++;
        }

        private struct StructImpl : IService { }

        private sealed class Marker : MonoBehaviour { }

        private sealed class NeedsFunc
        {
            public Func<IService> Factory { get; }
            public NeedsFunc(Func<IService> factory = null) => Factory = factory;
        }

        // ── Lifecycle ───────────────────────────────────────────────

        [SetUp]
        public void SetUp()
        {
            _container = new DiContainer();
            _gameObject = new GameObject(nameof(DiContainerRegistrationTests));
        }

        [TearDown]
        public void TearDown()
        {
            _container?.Dispose();
            _container = null;
            if (_gameObject != null) UnityEngine.Object.DestroyImmediate(_gameObject);
            _gameObject = null;
        }

        // ── Duplicates, Replace, Add ────────────────────────────────

        [Test]
        public void Register_Twice_Throws_AndKeepsTheFirstBinding()
        {
            _container.Register<IService, ServiceImpl>();

            Assert.Throws<InvalidOperationException>(() => _container.Register<IService, OtherServiceImpl>());

            Assert.IsInstanceOf<ServiceImpl>(_container.Resolve<IService>());
        }

        [Test]
        public void RegisterInstance_AndRegister_AreSymmetric_BothThrowOnADuplicate()
        {
            _container.RegisterInstance<IDep>(new DepImpl());
            Assert.Throws<InvalidOperationException>(() => _container.Register<IDep, OtherDepImpl>());

            _container.Register<IService, ServiceImpl>();
            Assert.Throws<InvalidOperationException>(() => _container.RegisterInstance<IService>(new ServiceImpl()));
        }

        [Test]
        public void Replace_SwapsTheBinding()
        {
            _container.Register<IService, ServiceImpl>();

            _container.Replace<IService, OtherServiceImpl>();

            Assert.IsInstanceOf<OtherServiceImpl>(_container.Resolve<IService>());
            Assert.AreEqual(1, _container.ResolveAll<IService>().Count());
        }

        [Test]
        public void ReplaceInstance_AndReplaceFactory_SwapTheBinding()
        {
            var first = new DepImpl();
            var second = new OtherDepImpl();
            _container.RegisterInstance<IDep>(first);

            _container.ReplaceInstance<IDep>(second);
            Assert.AreSame(second, _container.Resolve<IDep>());

            _container.ReplaceFactory<IDep>(p => first);
            Assert.AreSame(first, _container.Resolve<IDep>());
        }

        [Test]
        public void LegacyPolicy_ReplacesWithAWarning()
        {
            using var container = new DiContainer(options: new DiContainerOptions
            {
                DuplicateRegistrations = DuplicateRegistrationPolicy.ReplaceWithWarning
            });
            container.Register<IService, ServiceImpl>();
            LogAssert.Expect(LogType.Warning, new Regex("Replacing registration"));

            container.Register<IService, OtherServiceImpl>();

            Assert.IsInstanceOf<OtherServiceImpl>(container.Resolve<IService>());
        }

        [Test]
        public void Add_AppendsBindings_ResolveReturnsTheLast_ResolveAllReturnsAll()
        {
            _container.Add<IService, ServiceImpl>();
            _container.Add<IService, OtherServiceImpl>();

            Assert.IsInstanceOf<OtherServiceImpl>(_container.Resolve<IService>());

            var all = _container.ResolveAll<IService>().ToArray();
            Assert.AreEqual(2, all.Length);
            Assert.IsInstanceOf<ServiceImpl>(all[0]);
            Assert.IsInstanceOf<OtherServiceImpl>(all[1]);
        }

        [Test]
        public void ChildScope_MayShadowAParentBinding_WithoutReplaceOrAdd()
        {
            _container.Register<IService, ServiceImpl>();

            using (var scope = _container.CreateScope())
            {
                scope.Register<IService, OtherServiceImpl>();

                Assert.IsInstanceOf<OtherServiceImpl>(scope.Resolve<IService>());
                Assert.IsInstanceOf<ServiceImpl>(_container.Resolve<IService>());
            }
        }

        // ── Destroyed Unity instances ───────────────────────────────

        [Test]
        public void RegisterInstance_ReplacesAnInstanceWhoseUnityObjectWasDestroyed()
        {
            var dead = _gameObject.AddComponent<Marker>();
            _container.RegisterInstance<Marker>(dead);
            UnityEngine.Object.DestroyImmediate(dead);

            var live = _gameObject.AddComponent<Marker>();
            _container.RegisterInstance<Marker>(live);

            Assert.AreSame(live, _container.Resolve<Marker>());
        }

        [Test]
        public void Resolve_OfADestroyedRegisteredInstance_Throws_InsteadOfReturningTheDeadReference()
        {
            var marker = _gameObject.AddComponent<Marker>();
            _container.RegisterInstance<Marker>(marker);
            UnityEngine.Object.DestroyImmediate(marker);

            Assert.Throws<InvalidOperationException>(() => _container.Resolve<Marker>());
        }

        [Test]
        public void DestroyedSingleton_IsEvictedAndRebuilt()
        {
            _container.RegisterFactory<Marker>(p => _gameObject.AddComponent<Marker>(), Lifetime.Singleton);
            var first = _container.Resolve<Marker>();
            UnityEngine.Object.DestroyImmediate(first);

            var second = _container.Resolve<Marker>();

            Assert.AreNotSame(first, second);
            Assert.IsTrue(second != null);
        }

        // ── Shorthands and non-generic overloads ────────────────────

        [Test]
        public void SelfBinding_AndLifetimeShorthands_Register()
        {
            _container.Register<DepImpl>();
            _container.RegisterSingleton<IService, ServiceImpl>();

            Assert.IsNotNull(_container.Resolve<DepImpl>());
            Assert.AreSame(_container.Resolve<IService>(), _container.Resolve<IService>());
        }

        [Test]
        public void NonGenericInstanceAndFactoryOverloads_Register()
        {
            var instance = new DepImpl();
            _container.RegisterInstance(typeof(IDep), instance);
            _container.RegisterFactory(typeof(IService), p => new ServiceImpl());

            Assert.AreSame(instance, _container.Resolve<IDep>());
            Assert.IsInstanceOf<ServiceImpl>(_container.Resolve<IService>());
        }

        [Test]
        public void HasRegistration_GenericOverload_Works()
        {
            _container.Register<IService, ServiceImpl>();

            Assert.IsTrue(_container.HasRegistration<IService>());
            Assert.IsFalse(_container.HasRegistration<IDep>());
        }

        // ── Input validation ────────────────────────────────────────

        [Test]
        public void Register_StructImplementation_Throws()
        {
            Assert.Throws<ArgumentException>(() => _container.Register(typeof(IService), typeof(StructImpl)));
        }

        [Test]
        public void Register_ScriptableObjectImplementation_Throws()
        {
            Assert.Throws<ArgumentException>(() => _container.Register<object, ScriptableObject>());
        }

        [Test]
        public void RegisterInstance_OfTheWrongType_Throws()
        {
            Assert.Throws<ArgumentException>(() => _container.RegisterInstance(typeof(IService), new DepImpl()));
        }

        [Test]
        public void RegisterInstance_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => _container.RegisterInstance<IService>(null));
        }

        // ── Implicit Func / Lazy / collections ──────────────────────

        [Test]
        public void FuncAndLazy_OfAnUnregisteredService_AreNotResolvable()
        {
            Assert.IsFalse(_container.HasRegistration(typeof(Func<IService>)));
            Assert.IsFalse(_container.HasRegistration(typeof(Lazy<IService>)));
            Assert.Throws<InvalidOperationException>(() => _container.Resolve<Func<IService>>());
            Assert.Throws<InvalidOperationException>(() => _container.Resolve<Lazy<IService>>());

            LogAssert.Expect(LogType.Warning, new Regex("TryResolve"));
            Assert.IsFalse(_container.TryResolve<Func<IService>>(out _));
        }

        [Test]
        public void FuncAndLazy_OfARegisteredService_AreResolvable()
        {
            _container.Register<IService, ServiceImpl>();

            Assert.IsTrue(_container.HasRegistration(typeof(Func<IService>)));
            Assert.IsTrue(_container.HasRegistration(typeof(Lazy<IService>)));
        }

        [Test]
        public void CollectionTypes_AreAlwaysResolvable_AndEmptyWhenNothingIsRegistered()
        {
            Assert.IsTrue(_container.HasRegistration(typeof(System.Collections.Generic.IEnumerable<IService>)));
            Assert.IsTrue(_container.HasRegistration(typeof(IService[])));
            Assert.AreEqual(0, _container.Resolve<IService[]>().Length);
            Assert.AreEqual(0, _container.Resolve<System.Collections.Generic.IReadOnlyList<IService>>().Count);
        }

        [Test]
        public void OptionalParameter_OfAResolvableFuncType_IsResolved_NotDefaulted()
        {
            _container.Register<IService, ServiceImpl>();
            _container.Register<NeedsFunc>();

            var resolved = _container.Resolve<NeedsFunc>();

            Assert.IsNotNull(resolved.Factory);
            Assert.IsInstanceOf<ServiceImpl>(resolved.Factory());
        }

        [Test]
        public void ARegisteredFunc_WinsOverTheImplicitOne()
        {
            Func<IService> custom = () => new OtherServiceImpl();
            _container.RegisterInstance<Func<IService>>(custom);

            Assert.AreSame(custom, _container.Resolve<Func<IService>>());
        }

        // ── Aliases (one instance, several service types) ───────────

        [Test]
        public void Alias_ForwardsToTheSameInstance_InjectedOnce_DisposedOnce()
        {
            _container.Register<IDep, DepImpl>();
            _container.Register<Both>(Lifetime.Singleton);
            _container.RegisterAlias<IA, Both>();
            _container.RegisterAlias<IB, Both>();

            var asA = _container.Resolve<IA>();
            var asB = _container.Resolve<IB>();
            var both = _container.Resolve<Both>();

            Assert.AreSame(asA, asB);
            Assert.AreSame(asA, both);
            Assert.AreEqual(1, both.InjectCalls);

            _container.Dispose();
            Assert.AreEqual(1, both.DisposeCount);
        }

        [Test]
        public void ForwardingFactory_DoesNotInjectOrDisposeTheSameObjectTwice()
        {
            _container.Register<IDep, DepImpl>();
            _container.Register<IA, Both>(Lifetime.Singleton);
            _container.RegisterFactory<IB>(p => (IB)p.Resolve<IA>(), Lifetime.Singleton);

            var both = (Both)_container.Resolve<IA>();
            var viaFactory = _container.Resolve<IB>();

            Assert.AreSame(both, viaFactory);
            Assert.AreEqual(1, both.InjectCalls, "already-injected objects are not injected again");

            _container.Dispose();
            Assert.AreEqual(1, both.DisposeCount, "one object is disposed once, however many registrations hand it out");
        }

        [Test]
        public void AliasCycle_IsReportedAsACircularDependency()
        {
            _container.RegisterAlias(typeof(IA), typeof(IB));
            _container.RegisterAlias(typeof(IB), typeof(IA));

            var ex = Assert.Throws<InvalidOperationException>(() => _container.Resolve<IA>());
            Assert.IsTrue(ex.Message.Contains("Circular"), ex.Message);
        }

        [Test]
        public void RegisterAlias_ToItself_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => _container.RegisterAlias(typeof(IA), typeof(IA)));

            Assert.IsTrue(ex.Message.Contains("itself"), ex.Message);
        }

        [Test]
        public void Alias_ResolvingToAnObjectOfTheWrongType_IsADiagnosedError_NotACastException()
        {
            _container.Register<IService, ServiceImpl>();
            _container.RegisterAlias(typeof(IA), typeof(IService)); // ServiceImpl does not implement IA

            var ex = Assert.Throws<InvalidOperationException>(() => _container.Resolve<IA>());

            Assert.IsTrue(ex.Message.Contains("is not a 'IA'"), ex.Message);
        }

        // ── Collections that cannot be implicit ─────────────────────

        [Test]
        public void CollectionsOfValueTypes_AreNotImplicitlyResolvable()
        {
            Assert.IsFalse(_container.HasRegistration<int[]>());
            Assert.IsFalse(_container.HasRegistration<IEnumerable<int>>());
            Assert.Throws<InvalidOperationException>(() => _container.Resolve<int[]>());
            Assert.Throws<InvalidOperationException>(() => _container.Resolve<IEnumerable<int>>());
        }

        // ── Options ─────────────────────────────────────────────────

        [Test]
        public void Scopes_InheritTheRootOptions()
        {
            var options = new DiContainerOptions
            {
                DuplicateRegistrations = DuplicateRegistrationPolicy.ReplaceWithWarning,
                WarnOnTryResolveMiss = false,
            };

            using (var root = new DiContainer(options: options))
            using (var scope = root.CreateScope())
            using (var logs = new LogCounter())
            {
                scope.Register<IService, ServiceImpl>();
                scope.Register<IService, OtherServiceImpl>(); // the root's legacy policy: replace with a warning, no exception

                Assert.IsFalse(scope.TryResolve<IDep>(out _)); // the root switched the miss warning off

                Assert.AreEqual(1, logs.Count(LogType.Warning, "Replacing registration"), logs.ToString());
                Assert.AreEqual(0, logs.Count(LogType.Warning, "TryResolve"), logs.ToString());
                Assert.IsInstanceOf<OtherServiceImpl>(scope.Resolve<IService>());
            }
        }

        [Test]
        public void ConstructingAScopeDirectly_WithItsOwnOptions_OverridesTheParents()
        {
            var legacy = new DiContainerOptions { DuplicateRegistrations = DuplicateRegistrationPolicy.ReplaceWithWarning };

            using (var scope = new DiContainer(_container, legacy))
            using (var logs = new LogCounter())
            {
                scope.Register<IService, ServiceImpl>();
                scope.Register<IService, OtherServiceImpl>();

                Assert.AreEqual(1, logs.Count(LogType.Warning, "Replacing registration"), logs.ToString());
            }

            // The root kept its own (strict) policy.
            _container.Register<IService, ServiceImpl>();
            Assert.Throws<InvalidOperationException>(() => _container.Register<IService, OtherServiceImpl>());
        }

        [Test]
        public void Options_AreCopied_SoChangingThemAfterConstructionHasNoEffect()
        {
            var options = new DiContainerOptions { DuplicateRegistrations = DuplicateRegistrationPolicy.Throw };

            using (var container = new DiContainer(options: options))
            {
                options.DuplicateRegistrations = DuplicateRegistrationPolicy.ReplaceWithWarning;

                container.Register<IService, ServiceImpl>();
                Assert.Throws<InvalidOperationException>(() => container.Register<IService, OtherServiceImpl>());
            }
        }

        // ── TryResolve ──────────────────────────────────────────────

        [Test]
        public void TryResolve_OnADisposedContainer_Throws_EvenForAnUnregisteredType()
        {
            _container.Dispose();

            Assert.Throws<ObjectDisposedException>(() => _container.TryResolve<IService>(out _));
        }

        [Test]
        public void TryResolve_FromABackgroundThread_Throws_InsteadOfReturningFalse()
        {
            var caught = RunOnBackgroundThread(() => _container.TryResolve<IService>(out _));

            Assert.IsNotNull(caught);
            Assert.IsInstanceOf<InvalidOperationException>(caught);
            Assert.IsTrue(caught.Message.Contains("main thread"), caught.Message);
        }

        [Test]
        public void TryResolve_Miss_WarnsOncePerTypePerContainer_ButCountsEveryMiss()
        {
            using (var logs = new LogCounter())
            {
                Assert.IsFalse(_container.TryResolve<IService>(out _));
                Assert.IsFalse(_container.TryResolve<IService>(out _));
                Assert.IsFalse(_container.TryResolve<IDep>(out _)); // a different type warns again

                Assert.AreEqual(3, _container.TryResolveMissCount);
                Assert.AreEqual(2, logs.Count(LogType.Warning, "TryResolve"), "once per type, not once per miss: " + logs);
            }
        }

        [Test]
        public void TryResolve_Warning_IsNotSharedAcrossContainers()
        {
            // A static warn-once set would stay silent for the second container (and make this test order-dependent).
            LogAssert.Expect(LogType.Warning, new Regex("TryResolve"));
            LogAssert.Expect(LogType.Warning, new Regex("TryResolve"));

            using (var first = new DiContainer())
                Assert.IsFalse(first.TryResolve<IService>(out _));
            using (var second = new DiContainer())
                Assert.IsFalse(second.TryResolve<IService>(out _));
        }

        [Test]
        public void TryResolve_Warning_CanBeSwitchedOff()
        {
            using (var container = new DiContainer(options: new DiContainerOptions { WarnOnTryResolveMiss = false }))
            using (var logs = new LogCounter())
            {
                Assert.IsFalse(container.TryResolve<IService>(out _));

                Assert.AreEqual(0, logs.Total, logs.ToString());
                Assert.AreEqual(1, container.TryResolveMissCount, "the miss is still counted");
            }
        }

        // ── Creation and thread contract ────────────────────────────

        [Test]
        public void CreateOnMainThread_ReturnsAWorkingContainer()
        {
            using var container = DiContainer.CreateOnMainThread();
            container.Register<IService, ServiceImpl>();

            Assert.IsNotNull(container.Resolve<IService>());
        }

        [Test]
        public void Constructor_FromABackgroundThread_Throws_InEditorAndDevelopmentBuilds()
        {
            var caught = RunOnBackgroundThread(() => new DiContainer().Dispose());

            Assert.IsInstanceOf<InvalidOperationException>(caught);
        }

        [Test]
        public void Resolve_FromABackgroundThread_Throws_InEditorAndDevelopmentBuilds()
        {
            _container.Register<IService, ServiceImpl>();

            var caught = RunOnBackgroundThread(() => _container.Resolve<IService>());

            Assert.IsInstanceOf<InvalidOperationException>(caught);
            Assert.IsTrue(caught.Message.Contains("main thread"), caught.Message);
        }

        [Test]
        public void EveryResolvingEntryPoint_RejectsABackgroundThread()
        {
            _container.Register<IService, ServiceImpl>();

            var entryPoints = new (string Name, Action Call)[]
            {
                ("Resolve(Type)", () => _container.Resolve(typeof(IService))),
                ("ResolveAll", () => _container.ResolveAll<IService>()),
                ("InjectInto", () => _container.InjectInto(new object())),
                ("CreateScope", () => _container.CreateScope()),
            };

            foreach (var entryPoint in entryPoints)
            {
                var caught = RunOnBackgroundThread(entryPoint.Call);

                Assert.IsInstanceOf<InvalidOperationException>(caught, entryPoint.Name);
                Assert.IsTrue(caught.Message.Contains("main thread"), entryPoint.Name + ": " + caught.Message);
            }
        }

        [Test]
        public void RegistrationAndDiagnosticEntryPoints_RejectABackgroundThread()
        {
            _container.Register<IService, ServiceImpl>();

            var entryPoints = new (string Name, Action Call)[]
            {
                ("Register", () => _container.Register<IDep, DepImpl>()),
                ("RegisterFactory", () => _container.RegisterFactory<IService>(_ => new ServiceImpl())),
                ("HasRegistration", () => _container.HasRegistration(typeof(IService))),
                ("Validate", () => _container.Validate()),
                ("DescribeRegistrations", () => _container.DescribeRegistrations()),
                ("ComponentResolver", () => ComponentResolver.Resolve(null)),
                ("InjectionQuery", () => InjectionQuery.TypeNeedsAnyInjection(typeof(ServiceImpl))),
                ("Dispose", () => _container.Dispose()),
            };

            foreach (var entryPoint in entryPoints)
            {
                var caught = RunOnBackgroundThread(entryPoint.Call);

                Assert.IsInstanceOf<InvalidOperationException>(caught, entryPoint.Name);
                Assert.IsTrue(caught.Message.Contains("main thread"), entryPoint.Name + ": " + caught.Message);
            }
        }

        [Test]
        public void IsMainThread_IsTrueOnTheTestThread_AndFalseElsewhere()
        {
            bool onWorker = true;
            RunOnBackgroundThread(() => onWorker = DiContainer.IsMainThread);

            Assert.IsTrue(DiContainer.IsMainThread);
            Assert.IsFalse(onWorker);
        }

        [Test]
        public void Scopes_AreCreatedThroughTheSegregatedInterfaces()
        {
            IScopeFactory factory = _container;
            IDiScope scope = factory.CreateScope();
            IDiScope grandChild = scope.CreateScope(); // a scope can open a child without a cast

            Assert.IsNotNull(grandChild);
            grandChild.Dispose();
            scope.Dispose();
        }

        private static Exception RunOnBackgroundThread(Action action)
        {
            Exception caught = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { caught = ex; }
            });
            thread.Start();
            thread.Join();
            return caught;
        }
    }
}
