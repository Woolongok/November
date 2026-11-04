using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using November.DI;
using UnityEngine;
using UnityEngine.TestTools;

namespace November.DI.Tests
{
    /// <summary>
    /// Construction and injection details: member order, constructor selection, optional parameters,
    /// error reporting, validation, async disposal and the allocation-free hot path.
    /// </summary>
    internal class DiContainerInjectionTests
    {
        private DiContainer _container;

        // ── Test subjects ───────────────────────────────────────────

        private interface IDep { }
        private sealed class DepImpl : IDep { }

        private interface IService { }
        private sealed class ServiceImpl : IService { }

        private sealed class CountingService : IService
        {
            public static int Instances;
            public CountingService() => Instances++;
        }

        private sealed class Ordered
        {
            public static List<string> Log = new List<string>();
            [Inject] public IDep Field;
            public bool FieldWasSetWhenMethodRan;

            public Ordered() => Log.Add("ctor");

            [Inject]
            public void Method(IDep dep)
            {
                Log.Add("method");
                FieldWasSetWhenMethodRan = Field != null;
            }
        }

        private class OrderBase
        {
            [Inject] public void InitBase() => Ordered.Log.Add("base");
        }

        private sealed class OrderDerived : OrderBase
        {
            [Inject] public void InitDerived() => Ordered.Log.Add("derived");
        }

        private class FieldBase
        {
            [Inject] private IDep _baseDep;
            public IDep BaseDep => _baseDep;
        }

        private sealed class FieldDerived : FieldBase
        {
            [Inject] public IService Service;
        }

        private class VirtualBase
        {
            public static int Runs;
            [Inject] public virtual void Init(IDep dep) => Runs++;
        }

        private sealed class VirtualDerived : VirtualBase
        {
            [Inject] public override void Init(IDep dep) => Runs++;
        }

        private sealed class AutoProperty
        {
            [field: Inject] public IDep Dep { get; private set; }
        }

        private sealed class TwoInjectConstructors
        {
            [Inject] public TwoInjectConstructors() { }
            [Inject] public TwoInjectConstructors(IDep dep) { }
        }

        private sealed class TwoPublicConstructors
        {
            public TwoPublicConstructors() { }
            public TwoPublicConstructors(IDep dep) { }
        }

        private sealed class MarkedConstructor
        {
            public IDep Dep;
            public MarkedConstructor() { }
            [Inject] public MarkedConstructor(IDep dep) => Dep = dep;
        }

        private sealed class PrivateInjectConstructor
        {
            public IDep Dep;
            [Inject] private PrivateInjectConstructor(IDep dep) => Dep = dep;
        }

        private sealed class NoAccessibleConstructor
        {
            private NoAccessibleConstructor() { }
        }

        private sealed class OptionalConcrete
        {
            public int Count;
            public OptionalConcrete(int count = 7) => Count = count;
        }

        private sealed class OptionalService
        {
            public IService Service;
            public OptionalService(IService service = null) => Service = service;
        }

        private sealed class MethodDefaults
        {
            public IDep Dep;
            public int Count;

            [Inject]
            public void Init(IDep dep, int count = 5)
            {
                Dep = dep;
                Count = count;
            }
        }

        private sealed class Level1 { public Level1(Level2 level2) { } }
        private sealed class Level2 { public Level2(IDep dep) { } }

        private sealed class CustomException : Exception
        {
            public CustomException() : base("custom") { }
        }

        private sealed class ThrowsInConstructor
        {
            public ThrowsInConstructor() => throw new CustomException();
        }

        private sealed class FailingInject : IDisposable
        {
            public static int Disposed;
            [Inject] public void Init(IDep dep) => throw new InvalidOperationException("inject failed");
            public void Dispose() => Disposed++;
        }

        private sealed class UnrelatedService
        {
        }

        private sealed class AsyncOnlyFailingInject : IAsyncDisposable
        {
            public int Disposed;
            [Inject] public void Init(IMissing missing) => throw new InvalidOperationException("inject failed");
            public ValueTask DisposeAsync() { Disposed++; return default; }
        }

        private interface IExternalFactoryService { }
        private interface IFactoryResultService { }

        private sealed class ExternalFailingResult : IExternalFactoryService, IFactoryResultService, IDisposable
        {
            public int Disposed;
            [Inject] public IMissing Missing;
            public void Dispose() => Disposed++;
        }

        private sealed class FieldCycleA { [Inject] public FieldCycleB B; }
        private sealed class FieldCycleB { [Inject] public FieldCycleA A; }

        private interface IMissing { }
        private sealed class NeedsMissing { public NeedsMissing(IMissing missing) { } }

        private sealed class InjectIntoNeedsMissing
        {
            [Inject] public IMissing Missing;
        }
        private sealed class CycleA { public CycleA(CycleB b) { } }
        private sealed class CycleB { public CycleB(CycleA a) { } }
        private sealed class LazyA { public LazyA(Func<LazyB> b) { } }
        private sealed class LazyB { public LazyB(LazyA a) { } }
        private sealed class CaptiveSingleton { public CaptiveSingleton(IDep dep) { } }
        private sealed class NeedsDep { public NeedsDep(IDep dep) { } }

        private sealed class AsyncOnly : IAsyncDisposable
        {
            public int Count;
            public ValueTask DisposeAsync() { Count++; return default; }
        }

        private sealed class SyncAndAsync : IDisposable, IAsyncDisposable
        {
            public int Sync;
            public int Async;
            public void Dispose() => Sync++;
            public ValueTask DisposeAsync() { Async++; return default; }
        }

        private static List<string> DisposalEvents;

        private sealed class RecordingDisposable : IDisposable
        {
            public void Dispose() => DisposalEvents.Add("recording");
        }

        private sealed class ThrowingDisposable : IDisposable
        {
            public void Dispose()
            {
                DisposalEvents.Add("throwing");
                throw new InvalidOperationException("deliberate dispose failure");
            }
        }

        private sealed class RecordingAsyncDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                DisposalEvents.Add("recording-async");
                return default;
            }
        }

        private sealed class ThrowingAsyncDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                DisposalEvents.Add("throwing-async");
                throw new InvalidOperationException("deliberate async dispose failure");
            }
        }

        // ── Lifecycle ───────────────────────────────────────────────

        [SetUp]
        public void SetUp()
        {
            Ordered.Log = new List<string>();
            VirtualBase.Runs = 0;
            FailingInject.Disposed = 0;
            CountingService.Instances = 0;
            DisposalEvents = new List<string>();
            _container = new DiContainer();
        }

        [TearDown]
        public void TearDown()
        {
            _container?.Dispose();
            _container = null;
        }

        // ── Member order ────────────────────────────────────────────

        [Test]
        public void Construction_RunsConstructor_ThenFields_ThenMethods()
        {
            _container.Register<IDep, DepImpl>();
            _container.Register<Ordered>();

            var resolved = _container.Resolve<Ordered>();

            CollectionAssert.AreEqual(new[] { "ctor", "method" }, Ordered.Log);
            Assert.IsNotNull(resolved.Field);
            Assert.IsTrue(resolved.FieldWasSetWhenMethodRan, "fields are injected before [Inject] methods run");
        }

        [Test]
        public void InjectInto_Null_ThrowsArgumentNullException()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => _container.InjectInto(null));

            Assert.AreEqual("instance", ex.ParamName);
        }

        [Test]
        public void InjectInto_FieldFailure_ReportsAResolutionPath()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => _container.InjectInto(new InjectIntoNeedsMissing()));

            Assert.IsTrue(ex.Message.Contains("field 'Missing'"), ex.Message);
            Assert.IsTrue(ex.Message.Contains("Resolution path: InjectIntoNeedsMissing -> IMissing"), ex.Message);
            Assert.IsTrue(((string)ex.Data[DiContainer.ResolutionPathKey]).Contains("InjectIntoNeedsMissing -> IMissing"));
        }

        [Test]
        public void InjectMethods_RunBaseClassFirst()
        {
            _container.InjectInto(new OrderDerived());

            CollectionAssert.AreEqual(new[] { "base", "derived" }, Ordered.Log);
        }

        [Test]
        public void PrivateBaseClassFields_AreInjected()
        {
            _container.Register<IDep, DepImpl>();
            _container.Register<IService, ServiceImpl>();
            var target = new FieldDerived();

            _container.InjectInto(target);

            Assert.IsNotNull(target.BaseDep);
            Assert.IsNotNull(target.Service);
        }

        [Test]
        public void OverriddenInjectMethod_RunsOnce()
        {
            _container.Register<IDep, DepImpl>();

            _container.InjectInto(new VirtualDerived());

            Assert.AreEqual(1, VirtualBase.Runs);
        }

        [Test]
        public void InjectOnAnAutoPropertyBackingField_Works()
        {
            _container.Register<IDep, DepImpl>();
            var target = new AutoProperty();

            _container.InjectInto(target);

            Assert.IsNotNull(target.Dep);
        }

        // ── Constructor selection ───────────────────────────────────

        [Test]
        public void TwoInjectConstructors_AreRejected()
        {
            _container.Register<IDep, DepImpl>();
            _container.Register<TwoInjectConstructors>();

            var ex = Assert.Throws<InvalidOperationException>(() => _container.Resolve<TwoInjectConstructors>());
            Assert.IsTrue(ex.Message.Contains("marked [Inject]"), ex.Message);
        }

        [Test]
        public void TwoPublicConstructors_WithoutInject_AreRejected()
        {
            _container.Register<TwoPublicConstructors>();

            var ex = Assert.Throws<InvalidOperationException>(() => _container.Resolve<TwoPublicConstructors>());
            Assert.IsTrue(ex.Message.Contains("Annotate"), ex.Message);
        }

        [Test]
        public void InjectMarkedConstructor_IsChosen_EvenWithSeveralPublicOnes()
        {
            _container.Register<IDep, DepImpl>();
            _container.Register<MarkedConstructor>();

            Assert.IsNotNull(_container.Resolve<MarkedConstructor>().Dep);
        }

        [Test]
        public void PrivateInjectConstructor_IsUsed()
        {
            _container.Register<IDep, DepImpl>();
            _container.Register<PrivateInjectConstructor>();

            Assert.IsNotNull(_container.Resolve<PrivateInjectConstructor>().Dep);
        }

        [Test]
        public void TypeWithoutAnAccessibleConstructor_ThrowsAClearError()
        {
            _container.Register<NoAccessibleConstructor>();

            var ex = Assert.Throws<InvalidOperationException>(() => _container.Resolve<NoAccessibleConstructor>());
            Assert.IsTrue(ex.Message.Contains("no accessible constructors"), ex.Message);
        }

        // ── Optional parameters ─────────────────────────────────────

        [Test]
        public void OptionalConcreteParameter_UsesItsDefault_Silently()
        {
            _container.Register<OptionalConcrete>();

            using (var logs = new LogCounter())
            {
                Assert.AreEqual(7, _container.Resolve<OptionalConcrete>().Count);

                Assert.AreEqual(0, logs.Total, logs.ToString());
            }
        }

        [Test]
        public void OptionalServiceParameter_WhenUnregistered_FallsBackLoudly()
        {
            _container.Register<OptionalService>();
            LogAssert.Expect(LogType.Error, new Regex("optional parameter 'service'"));

            Assert.IsNull(_container.Resolve<OptionalService>().Service);
        }

        [Test]
        public void OptionalServiceParameter_WhenRegistered_IsResolved()
        {
            _container.Register<IService, ServiceImpl>();
            _container.Register<OptionalService>();

            Assert.IsNotNull(_container.Resolve<OptionalService>().Service);
        }

        [Test]
        public void InjectMethod_HonoursParameterDefaults_LikeConstructors()
        {
            _container.Register<IDep, DepImpl>();
            var target = new MethodDefaults();

            _container.InjectInto(target);

            Assert.IsNotNull(target.Dep);
            Assert.AreEqual(5, target.Count);
        }

        // ── Factories ───────────────────────────────────────────────

        [Test]
        public void FactoryResults_AreInjected()
        {
            _container.Register<IDep, DepImpl>();
            _container.RegisterFactory<Ordered>(p => new Ordered());

            Assert.IsNotNull(_container.Resolve<Ordered>().Field);
        }

        [Test]
        public void FactoryReturningNull_Throws()
        {
            _container.RegisterFactory<IService>(p => null);

            var ex = Assert.Throws<InvalidOperationException>(() => _container.Resolve<IService>());
            Assert.IsTrue(ex.Message.Contains("returned null"), ex.Message);
        }

        [Test]
        public void FactoryReturningAnIncompatibleType_ThrowsImmediately()
        {
            _container.RegisterFactory(typeof(IService), p => new UnrelatedService());

            var ex = Assert.Throws<InvalidOperationException>(() => _container.Resolve<IService>());

            Assert.IsTrue(ex.Message.Contains("not assignable"), ex.Message);
            Assert.IsTrue(ex.Message.Contains("UnrelatedService"), ex.Message);
        }

        [Test]
        public void FailedFactoryInjection_DisposesTheHalfBuiltResult()
        {
            _container.Register<IDep, DepImpl>();
            _container.RegisterFactory<FailingInject>(p => new FailingInject());

            var ex = Assert.Throws<InvalidOperationException>(() => _container.Resolve<FailingInject>());

            Assert.AreEqual("inject failed", ex.Message);
            Assert.AreEqual(1, FailingInject.Disposed);
        }

        [Test]
        public void FailedFactoryInjection_DisposesAnAsyncOnlyResult_WhenCleanupCompletesSynchronously()
        {
            AsyncOnlyFailingInject created = null;
            _container.RegisterFactory<AsyncOnlyFailingInject>(p =>
            {
                created = new AsyncOnlyFailingInject();
                return created;
            });

            var ex = Assert.Throws<InvalidOperationException>(() =>
                _container.Resolve(typeof(AsyncOnlyFailingInject)));

            Assert.IsNotNull(created);
            Assert.AreEqual(1, created.Disposed, "the async-only factory result must still be cleaned up");
            Assert.IsTrue(ex.Message.Contains("IMissing"), ex.Message);
        }

        [Test]
        public void FailedFactoryInjection_DoesNotCleanUpAnExternallyOwnedResult()
        {
            var external = new ExternalFailingResult();
            _container.RegisterInstance<IExternalFactoryService>(external);
            _container.RegisterFactory<IFactoryResultService>(
                p => (IFactoryResultService)p.Resolve<IExternalFactoryService>());

            Assert.Throws<InvalidOperationException>(() => _container.Resolve<IFactoryResultService>());

            Assert.AreEqual(0, external.Disposed, "RegisterInstance objects stay caller-owned even when a factory forwards them");
        }

        [Test]
        public void Lazy_RetriesAfterAFailedValue()
        {
            int calls = 0;
            _container.RegisterFactory<IService>(p =>
            {
                if (calls++ == 0) throw new InvalidOperationException("first call fails");
                return new ServiceImpl();
            });
            var lazy = _container.Resolve<Lazy<IService>>();

            Assert.Throws<InvalidOperationException>(() => GC.KeepAlive(lazy.Value));
            Assert.IsNotNull(lazy.Value, "a failed Lazy must not cache the exception forever");
        }

        // ── Errors: thrown once, with the path ──────────────────────

        [Test]
        public void MissingRegistration_TwoLevelsDown_ThrowsOnce_WithTheFullPath_AndLogsNothing()
        {
            _container.Register<Level1>();
            _container.Register<Level2>();

            using (var logs = new LogCounter())
            {
                var ex = Assert.Throws<InvalidOperationException>(() => _container.Resolve<Level1>());

                Assert.IsTrue(ex.Message.Contains("Level1 -> Level2 -> IDep"), ex.Message);
                Assert.IsTrue(ex.Message.Contains("parameter 'dep'"), ex.Message);
                Assert.AreEqual(0, logs.Total, "the old container logged an error at every level it unwound through: " + logs);
            }
        }

        [Test]
        public void ConstructorExceptions_AreNotWrappedInTargetInvocationException()
        {
            _container.Register<ThrowsInConstructor>();

            var ex = Assert.Throws<CustomException>(() => _container.Resolve<ThrowsInConstructor>());

            Assert.IsInstanceOf<string>(ex.Data[DiContainer.ResolutionPathKey]);
            Assert.IsTrue(((string)ex.Data[DiContainer.ResolutionPathKey]).Contains("ThrowsInConstructor"));
        }

        [Test]
        public void FailingInjection_DisposesTheHalfBuiltInstance_AndRethrowsTheOriginal()
        {
            _container.Register<IDep, DepImpl>();
            _container.Register<FailingInject>();

            var ex = Assert.Throws<InvalidOperationException>(() => _container.Resolve<FailingInject>());

            Assert.AreEqual("inject failed", ex.Message);
            Assert.AreEqual(1, FailingInject.Disposed);
        }

        [Test]
        public void CycleThroughInjectFields_IsDetected()
        {
            _container.Register<FieldCycleA>();
            _container.Register<FieldCycleB>();

            var ex = Assert.Throws<InvalidOperationException>(() => _container.Resolve<FieldCycleA>());
            Assert.IsTrue(ex.Message.Contains("Circular"), ex.Message);
        }

        // ── Validate() and DescribeRegistrations() ──────────────────

        [Test]
        public void Validate_ReportsEveryProblem_InOneException()
        {
            _container.Register<NeedsMissing>();
            _container.Register<CycleA>();
            _container.Register<CycleB>();
            _container.Register<TwoPublicConstructors>();
            _container.RegisterAlias(typeof(IService), typeof(ServiceImpl));

            var ex = Assert.Throws<InvalidOperationException>(() => _container.Validate());

            Assert.IsTrue(ex.Message.Contains("'IMissing'"), ex.Message);
            Assert.IsTrue(ex.Message.Contains("Circular dependency"), ex.Message);
            Assert.IsTrue(ex.Message.Contains("public constructors"), ex.Message);
            Assert.IsTrue(ex.Message.Contains("Alias 'IService'"), ex.Message);
        }

        [Test]
        public void Validate_PassesForAHealthyGraph_IncludingLazyEdges()
        {
            _container.Register<IDep, DepImpl>();
            _container.Register<NeedsDep>();
            _container.Register<LazyA>(); // LazyA -> Func<LazyB> -> LazyB -> LazyA is lazy, not a cycle
            _container.Register<LazyB>();

            Assert.DoesNotThrow(() => _container.Validate());
        }

        [Test]
        public void Validate_FlagsASingletonThatCapturesAScopedService()
        {
            _container.Register<IDep, DepImpl>(Lifetime.Scoped);
            _container.Register<CaptiveSingleton>(Lifetime.Singleton);

            var ex = Assert.Throws<InvalidOperationException>(() => _container.Validate());
            Assert.IsTrue(ex.Message.Contains("Captive"), ex.Message);
        }

        [Test]
        public void Validate_ConstructsNothing()
        {
            _container.Register<IService, CountingService>(Lifetime.Singleton);

            _container.Validate();

            Assert.AreEqual(0, CountingService.Instances);
        }

        [Test]
        public void DescribeRegistrations_ListsEveryScopeLevel()
        {
            _container.Register<IService, ServiceImpl>(Lifetime.Singleton);
            using (var scope = _container.CreateScope())
            {
                scope.Register<IDep, DepImpl>();

                var dump = ((DiContainer)scope).DescribeRegistrations();

                Assert.IsTrue(dump.Contains("[root]"), dump);
                Assert.IsTrue(dump.Contains("[scope 1] (this)"), dump);
                Assert.IsTrue(dump.Contains("IService -> ServiceImpl (Singleton)"), dump);
                Assert.IsTrue(dump.Contains("IDep -> DepImpl (Transient)"), dump);
            }
        }

        // ── Async disposal ──────────────────────────────────────────

        [Test]
        public void Dispose_AcceptsAnAsyncDisposable_ThatCompletesSynchronously()
        {
            _container.Register<AsyncOnly>(Lifetime.Singleton);
            var resolved = _container.Resolve<AsyncOnly>();

            _container.Dispose();

            Assert.AreEqual(1, resolved.Count);
        }

        [Test]
        public void DisposeAsync_PrefersIAsyncDisposable_AndStillDisposesPlainDisposables()
        {
            _container.Register<SyncAndAsync>(Lifetime.Singleton);
            _container.Register<DisposableOnly>(Lifetime.Singleton);
            var both = _container.Resolve<SyncAndAsync>();
            var plain = _container.Resolve<DisposableOnly>();

            _container.DisposeAsync().GetAwaiter().GetResult();

            Assert.AreEqual(0, both.Sync);
            Assert.AreEqual(1, both.Async);
            Assert.AreEqual(1, plain.Count);
            Assert.Throws<ObjectDisposedException>(() => _container.Resolve<SyncAndAsync>());
        }

        private sealed class DisposableOnly : IDisposable
        {
            public int Count;
            public void Dispose() => Count++;
        }

        [Test]
        public void Dispose_ContinuesAfterAThrowingDisposable_AndLogsOneError()
        {
            _container.Register<RecordingDisposable>(Lifetime.Singleton);
            _container.Register<ThrowingDisposable>(Lifetime.Singleton);
            _container.Resolve<RecordingDisposable>();
            _container.Resolve<ThrowingDisposable>();

            using (var logs = new LogCounter())
            {
                LogAssert.Expect(LogType.Error, new Regex("Exception disposing 'ThrowingDisposable'"));
                Assert.DoesNotThrow(() => _container.Dispose());

                Assert.AreEqual(2, DisposalEvents.Count);
                Assert.AreEqual("throwing", DisposalEvents[0]);
                Assert.AreEqual("recording", DisposalEvents[1]);
                Assert.AreEqual(1, logs.Count(LogType.Error, "Exception disposing 'ThrowingDisposable'"), logs.ToString());
            }
        }

        [Test]
        public void DisposeAsync_ContinuesAfterAThrowingAsyncDisposable_AndLogsOneError()
        {
            _container.Register<RecordingAsyncDisposable>(Lifetime.Singleton);
            _container.Register<ThrowingAsyncDisposable>(Lifetime.Singleton);
            _container.Resolve<RecordingAsyncDisposable>();
            _container.Resolve<ThrowingAsyncDisposable>();

            using (var logs = new LogCounter())
            {
                LogAssert.Expect(LogType.Error, new Regex("Exception disposing 'ThrowingAsyncDisposable'"));
                _container.DisposeAsync().GetAwaiter().GetResult();

                Assert.AreEqual(2, DisposalEvents.Count);
                Assert.AreEqual("throwing-async", DisposalEvents[0]);
                Assert.AreEqual("recording-async", DisposalEvents[1]);
                Assert.AreEqual(1, logs.Count(LogType.Error, "Exception disposing 'ThrowingAsyncDisposable'"), logs.ToString());
            }
        }

        // ── Metadata and the hot path ───────────────────────────────

        [Test]
        public void InjectionQuery_ReportsInjectionPoints()
        {
            Assert.IsTrue(InjectionQuery.TypeNeedsAnyInjection(typeof(Ordered)));
            Assert.IsFalse(InjectionQuery.TypeNeedsAnyInjection(typeof(DepImpl)));
            Assert.IsFalse(InjectionQuery.TypeNeedsAnyInjection(typeof(string)));
        }

        [Test]
        public void ClearingTheMetadataCache_DoesNotBreakResolution()
        {
            _container.Register<IDep, DepImpl>();
            _container.Register<Ordered>();
            _container.Resolve<Ordered>();

            InjectionMetadata.ClearCache();

            Assert.IsNotNull(_container.Resolve<Ordered>().Field);
        }

        [Test]
        public void Resolve_OfACachedSingleton_DoesNotAllocate()
        {
            _container.Register<IService, ServiceImpl>(Lifetime.Singleton);
            for (int i = 0; i < 10; i++) _container.Resolve<IService>();

            long Allocated()
            {
                try { return GC.GetAllocatedBytesForCurrentThread(); }
                catch (NotSupportedException) { return -1; }
            }

            long probeBefore = Allocated();
            var junk = new byte[64];
            long probeAfter = Allocated();
            GC.KeepAlive(junk);
            if (probeBefore < 0 || probeAfter - probeBefore < 64)
            {
                Assert.Pass("This runtime does not expose a per-thread allocation counter.");
                return;
            }

            long before = Allocated();
            for (int i = 0; i < 100; i++) _container.Resolve<IService>();
            long after = Allocated();

            Assert.AreEqual(0L, after - before, "a cached Resolve must not allocate (no message formatting, no closures)");
        }
    }
}
