using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace November.DI.Tests
{
    internal sealed class SourceMarker : MonoBehaviour
    {
    }

    internal sealed class ChildrenTarget : MonoBehaviour
    {
        [Resolve(Source = ComponentSource.Children)] public SourceMarker Marker;
    }

    internal sealed class ParentTarget : MonoBehaviour
    {
        [Resolve(Source = ComponentSource.Parent)] public SourceMarker Marker;
    }

    internal sealed class OptionalTarget : MonoBehaviour
    {
        [Resolve(Optional = true)] public SourceMarker Marker;
    }

    internal sealed class NotAComponentTarget : MonoBehaviour
    {
        [Resolve] public string Name;
    }

    /// <summary>
    /// [Resolve] options: search source (self / children / parents), Optional, and the once-only report for
    /// fields that can never be resolved.
    /// </summary>
    internal class ComponentResolverSourceTests
    {
        private GameObject _root;

        [SetUp]
        public void SetUp()
        {
            InjectionMetadata.ClearCache(); // plan-time warnings fire once per type: make that deterministic
            _root = new GameObject(nameof(ComponentResolverSourceTests));
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            _root = null;
        }

        [Test]
        public void Children_FindsAComponentOnAChild()
        {
            var child = new GameObject("child");
            child.transform.SetParent(_root.transform, false);
            var marker = child.AddComponent<SourceMarker>();
            var target = _root.AddComponent<ChildrenTarget>();

            ComponentResolver.Resolve(target);

            Assert.AreSame(marker, target.Marker);
        }

        [Test]
        public void Children_IncludesInactiveChildren()
        {
            var child = new GameObject("child");
            child.transform.SetParent(_root.transform, false);
            var marker = child.AddComponent<SourceMarker>();
            child.SetActive(false);
            var target = _root.AddComponent<ChildrenTarget>();

            ComponentResolver.Resolve(target);

            Assert.AreSame(marker, target.Marker, "prefabs are injected while inactive, so inactive objects must be searched");
        }

        [Test]
        public void Parent_FindsAComponentOnAParent()
        {
            var parent = new GameObject("parent");
            var marker = parent.AddComponent<SourceMarker>();
            _root.transform.SetParent(parent.transform, false);
            var target = _root.AddComponent<ParentTarget>();
            try
            {
                ComponentResolver.Resolve(target);

                Assert.AreSame(marker, target.Marker);
            }
            finally
            {
                _root.transform.SetParent(null, false);
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void Optional_MissingComponent_IsSilent_AndKeepsTheAssignedValue()
        {
            var other = new GameObject("other");
            try
            {
                var assigned = other.AddComponent<SourceMarker>();
                var target = _root.AddComponent<OptionalTarget>();
                target.Marker = assigned;

                using (var logs = new LogCounter())
                {
                    ComponentResolver.Resolve(target);

                    Assert.AreSame(assigned, target.Marker, "an optional miss must not wipe an Inspector reference");
                    Assert.AreEqual(0, logs.Total, logs.ToString());
                }
            }
            finally
            {
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void Optional_PresentComponent_IsStillAssigned()
        {
            var marker = _root.AddComponent<SourceMarker>();
            var target = _root.AddComponent<OptionalTarget>();

            ComponentResolver.Resolve(target);

            Assert.AreSame(marker, target.Marker);
        }

        [Test]
        public void Children_Miss_WarnsAndClears_NamingTheSearch()
        {
            var target = _root.AddComponent<ChildrenTarget>();
            LogAssert.Expect(LogType.Warning, new Regex("no 'SourceMarker' found on '.*' or its children"));

            ComponentResolver.Resolve(target);

            Assert.IsNull(target.Marker);
        }

        [Test]
        public void FieldThatIsNotAComponent_IsReportedOnce_WhenTheTypeIsFirstInspected()
        {
            var target = _root.AddComponent<NotAComponentTarget>();

            using (var logs = new LogCounter())
            {
                ComponentResolver.Resolve(target);
                ComponentResolver.Resolve(target); // the plan is cached: no second report

                Assert.AreEqual(1, logs.Count(LogType.Warning, "'String' is not a Unity Component or an interface"), logs.ToString());
                Assert.AreEqual(1, logs.Total, "reported once, and nothing else is logged: " + logs);
            }
        }
    }
}
