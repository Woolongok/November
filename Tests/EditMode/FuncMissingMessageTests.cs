using System;
using NUnit.Framework;
using November.DI;

namespace November.DI.Tests
{
    internal interface IMissing { }

    internal class FuncMissingMessageTests
    {
        private DiContainer _container;

        [SetUp]
        public void SetUp() => _container = new DiContainer();

        [TearDown]
        public void TearDown() => _container?.Dispose();

        [Test]
        public void FuncOfAnUnregisteredService_NamesTheWrapper_InTheError()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => _container.Resolve<Func<IMissing>>());
            Assert.IsTrue(ex.Message.Contains("needed by Func<IMissing>"), ex.Message);
        }
    }
}