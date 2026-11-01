using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace November.DI.Tests
{
    internal sealed class ResolveMarker : MonoBehaviour
    {
    }

    internal sealed class ResolveTarget : MonoBehaviour
    {
        [Resolve] public ResolveMarker Marker;
    }

    /// <summary>
    /// Covers the Unity-specific half of injection: [Resolve] fields are filled
    /// from the same GameObject via GetComponent, missing components are cleared
    /// loudly, and DiContainer.InjectInto runs component resolution for Components.
    /// </summary>
    internal class ComponentResolverTests
    {
        private GameObject _gameObject;

        [SetUp]
        public void SetUp()
        {
            _gameObject = new GameObject(nameof(ComponentResolverTests));
        }

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null) Object.DestroyImmediate(_gameObject);
            _gameObject = null;
        }

        [Test]
        public void Resolve_AssignsComponentFromSameGameObject()
        {
            var marker = _gameObject.AddComponent<ResolveMarker>();
            var target = _gameObject.AddComponent<ResolveTarget>();

            ComponentResolver.Resolve(target);

            Assert.AreSame(marker, target.Marker);
        }

        [Test]
        public void Resolve_MissingComponent_ClearsFieldAndWarns()
        {
            var other = new GameObject("other");
            try
            {
                var target = _gameObject.AddComponent<ResolveTarget>();
                target.Marker = other.AddComponent<ResolveMarker>();

                LogAssert.Expect(LogType.Warning, new Regex("no 'ResolveMarker' found"));
                ComponentResolver.Resolve(target);

                Assert.IsNull(target.Marker);
            }
            finally
            {
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void InjectInto_OnComponent_ResolvesResolveFields()
        {
            using var container = new DiContainer();
            var marker = _gameObject.AddComponent<ResolveMarker>();
            var target = _gameObject.AddComponent<ResolveTarget>();

            container.InjectInto(target);

            Assert.AreSame(marker, target.Marker);
        }
    }
}
