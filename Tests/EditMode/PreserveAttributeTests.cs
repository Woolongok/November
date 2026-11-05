using NUnit.Framework;
using UnityEngine.Scripting;

namespace November.DI.Tests
{
    internal class PreserveAttributeTests
    {
        [Test]
        public void InjectAndResolveAttributes_DeriveFromPreserveAttribute()
        {
            Assert.IsTrue(typeof(PreserveAttribute).IsAssignableFrom(typeof(InjectAttribute)));
            Assert.IsTrue(typeof(PreserveAttribute).IsAssignableFrom(typeof(ResolveAttribute)));
        }
    }
}