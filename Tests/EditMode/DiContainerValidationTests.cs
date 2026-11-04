using System;
using System.Collections.Generic;
using NUnit.Framework;
using November.DI;

namespace November.DI.Tests
{
    /// <summary>
    /// Validate() must agree with the runtime resolution rules. These tests focus on the cases
    /// where the dependency graph is intentionally indirect: transient hops, deferred wrappers,
    /// implicit collections, aliases and child-scope overrides.
    /// </summary>
    internal class DiContainerValidationTests
    {
        private DiContainer _container;

        // ── Test subjects ───────────────────────────────────────────

        private interface IActiveSession { }
        private interface ISession { }
        private interface IMissingValidation { }

        private sealed class Session : ISession, IActiveSession { }
        private sealed class Repository
        {
            public Repository(ISession session) { }
        }

        private sealed class AppManager
        {
            public AppManager(Repository repository) { }
        }

        private sealed class FuncManager
        {
            public FuncManager(Func<ISession> factory) { }
        }

        private sealed class LazyManager
        {
            public LazyManager(Lazy<ISession> lazy) { }
        }

        private sealed class CollectionManager
        {
            public CollectionManager(IEnumerable<ISession> sessions) { }
        }

        private sealed class AliasManager
        {
            public AliasManager(IActiveSession session) { }
        }

        private sealed class FuncMissingManager
        {
            public FuncMissingManager(Func<IMissingValidation> factory) { }
        }

        private interface IClock { }
        private sealed class RealClock : IClock { }
        private sealed class MockClock : IClock { }

        private sealed class RootClockUser
        {
            public RootClockUser(IClock clock) { }
        }

        private sealed class ChildClockUser
        {
            public ChildClockUser(IClock clock) { }
        }

        private sealed class RootTransientNeedsSession
        {
            public RootTransientNeedsSession(ISession session) { }
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

        private static InvalidOperationException ValidationFailure(DiContainer container)
            => Assert.Throws<InvalidOperationException>(() => container.Validate());

        private static void AssertContains(string message, string expected)
            => Assert.IsTrue(message.Contains(expected), "Expected '" + expected + "' in: " + message);

        // ── Captive dependencies through indirect edges ─────────────

        [Test]
        public void Validate_SingletonThroughTransientToScoped_ReportsTheCaptivePath()
        {
            _container.Register<ISession, Session>(Lifetime.Scoped);
            _container.Register<Repository>(Lifetime.Transient);
            _container.Register<AppManager>(Lifetime.Singleton);

            var ex = ValidationFailure(_container);

            AssertContains(ex.Message, "Captive dependency");
            AssertContains(ex.Message, "AppManager -> Repository -> Session");
        }

        [Test]
        public void Validate_SingletonFunctionOfScoped_ReportsTheCaptiveDependency()
        {
            _container.Register<ISession, Session>(Lifetime.Scoped);
            _container.Register<FuncManager>(Lifetime.Singleton);

            var ex = ValidationFailure(_container);

            AssertContains(ex.Message, "Captive dependency");
            AssertContains(ex.Message, "Session");
        }

        [Test]
        public void Validate_SingletonLazyOfScoped_ReportsTheCaptiveDependency()
        {
            _container.Register<ISession, Session>(Lifetime.Scoped);
            _container.Register<LazyManager>(Lifetime.Singleton);

            var ex = ValidationFailure(_container);

            AssertContains(ex.Message, "Captive dependency");
            AssertContains(ex.Message, "Session");
        }

        [Test]
        public void Validate_SingletonCollectionOfScoped_ReportsTheCaptiveDependency()
        {
            _container.Add<ISession, Session>(Lifetime.Scoped);
            _container.Register<CollectionManager>(Lifetime.Singleton);

            var ex = ValidationFailure(_container);

            AssertContains(ex.Message, "Captive dependency");
            AssertContains(ex.Message, "Session");
        }

        [Test]
        public void Validate_SingletonThroughAliasToScoped_ReportsTheCaptiveDependency()
        {
            _container.Register<ISession, Session>(Lifetime.Scoped);
            _container.RegisterAlias(typeof(IActiveSession), typeof(ISession));
            _container.Register<AliasManager>(Lifetime.Singleton);

            var ex = ValidationFailure(_container);

            AssertContains(ex.Message, "Captive dependency");
            AssertContains(ex.Message, "AliasManager -> IActiveSession -> ISession -> Session");
        }

        [Test]
        public void Validate_SingletonFunctionOfMissingService_ReportsTheMissingTarget()
        {
            _container.Register<FuncMissingManager>(Lifetime.Singleton);

            var ex = ValidationFailure(_container);

            AssertContains(ex.Message, "IMissingValidation");
            AssertContains(ex.Message, "not registered");
        }

        // ── Deferred edges are still cycles only when they are eager ─

        [Test]
        public void Validate_LazyCircularGraph_IsNotReportedAsAnEagerCycle()
        {
            _container.Register<LazyA>();
            _container.Register<LazyB>();

            Assert.DoesNotThrow(() => _container.Validate());
        }

        private sealed class LazyA
        {
            public LazyA(Func<LazyB> b) { }
        }

        private sealed class LazyB
        {
            public LazyB(LazyA a) { }
        }

        // ── Scope-aware validation ──────────────────────────────────

        [Test]
        public void Validate_RootSingletonUsesTheRootView_NotAChildScopedOverride()
        {
            _container.Register<IClock, RealClock>(Lifetime.Singleton);
            _container.Register<RootClockUser>(Lifetime.Singleton);

            using (var scope = _container.CreateScope())
            {
                scope.Register<IClock, MockClock>(Lifetime.Scoped);

                // The root singleton is built by the root, so the child override is irrelevant.
                Assert.DoesNotThrow(() => ((DiContainer)scope).Validate());
            }
        }

        [Test]
        public void Validate_ChildSingletonCapturingChildScoped_MatchesRuntimeResolution()
        {
            _container.Register<IClock, RealClock>(Lifetime.Singleton);

            using (var scope = _container.CreateScope())
            {
                scope.Register<IClock, MockClock>(Lifetime.Scoped);
                scope.Register<ChildClockUser>(Lifetime.Singleton);

                // Both live in the child and are disposed together, so this is not a captive root singleton.
                Assert.DoesNotThrow(() => ((DiContainer)scope).Validate());
                Assert.IsNotNull(scope.Resolve<ChildClockUser>());
            }
        }

        [Test]
        public void Validate_TransientResolvedFromTheRootNeedingScoped_ReportsTheRootError()
        {
            _container.Register<ISession, Session>(Lifetime.Scoped);
            _container.Register<RootTransientNeedsSession>(Lifetime.Transient);

            var ex = ValidationFailure(_container);

            AssertContains(ex.Message, "root container");
        }

        [Test]
        public void Validate_ChildTransientNeedingARootScopedService_IsHealthy()
        {
            _container.Register<ISession, Session>(Lifetime.Scoped);
            _container.Register<RootTransientNeedsSession>(Lifetime.Transient);

            using (var scope = _container.CreateScope())
            {
                Assert.DoesNotThrow(() => ((DiContainer)scope).Validate());
                Assert.IsNotNull(scope.Resolve<RootTransientNeedsSession>());
            }
        }

        // ── Explicit registrations win over implicit wrappers ──────

        [Test]
        public void Validate_ExplicitFuncRegistration_IsNotUnwrappedAndTraversed()
        {
            _container.Register<ISession, Session>(Lifetime.Scoped);
            _container.RegisterInstance<Func<ISession>>(() => null);
            _container.Register<FuncManager>(Lifetime.Singleton);

            // Runtime resolves the explicit Func registration; no scoped service is captured.
            Assert.DoesNotThrow(() => _container.Validate());
        }
    }
}
