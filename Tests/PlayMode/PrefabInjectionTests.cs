using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace November.DI.Tests.PlayMode
{
    internal interface IPrefabDep { }
    internal interface IUnregisteredPrefabDep { }
    internal sealed class PrefabDep : IPrefabDep { }
    internal sealed class OtherPrefabDep : IPrefabDep { }

    internal sealed class ProbeChild : MonoBehaviour
    {
        [Inject] public IPrefabDep Dep;
    }

    internal sealed class ParentLookupMarker : MonoBehaviour
    {
    }

    internal sealed class ParentLookupProbe : MonoBehaviour
    {
        [Resolve(Source = ComponentSource.Parent)] public ParentLookupMarker Marker;
    }

    /// <summary>Records what it can see at the moment Unity calls Awake.</summary>
    internal sealed class AwakeProbe : MonoBehaviour
    {
        [Inject] public IPrefabDep Dep;
        [Resolve(Source = ComponentSource.Children)] public ProbeChild Child;

        public int AwakeCount;
        public IPrefabDep DepSeenInAwake;
        public ProbeChild ChildSeenInAwake;
        public IPrefabDep ChildDepSeenInAwake;

        private void Awake()
        {
            AwakeCount++;
            DepSeenInAwake = Dep;
            ChildSeenInAwake = Child;
            ChildDepSeenInAwake = Child != null ? Child.Dep : null;
        }
    }

    internal sealed class FailingProbe : MonoBehaviour
    {
        public static readonly List<FailingProbe> Created = new List<FailingProbe>();
        [Inject] public IUnregisteredPrefabDep Dep; // deliberately never registered: injection must fail

        public FailingProbe() => Created.Add(this);
    }

    internal sealed class CountedProbe : MonoBehaviour
    {
        public static int Constructed;
        [Inject] public IPrefabDep Dep;

        public CountedProbe() => Constructed++;
    }

    internal sealed class PrefabSettings : ScriptableObject
    {
        [Inject] public IPrefabDep Dep;
    }

    internal sealed class FailingSettings : ScriptableObject
    {
        public static readonly List<FailingSettings> Created = new List<FailingSettings>();
        [Inject] public IUnregisteredPrefabDep Dep; // deliberately never registered: injection must fail

        public FailingSettings() => Created.Add(this);
    }

    /// <summary>
    /// InstantiatePrefab must inject BEFORE Awake / OnEnable run. Awake only runs in play mode, so these
    /// belong in a PlayMode assembly: in EditMode a plain MonoBehaviour never receives Awake and the test
    /// would pass without proving anything.
    ///
    /// "Prefabs" are built as objects under an inactive parent: that is how an asset behaves (components
    /// added to it never run Awake) and Instantiate(original) of such an object yields an active root.
    /// </summary>
    internal class PrefabInjectionTests
    {
        private DiContainer _container;
        private GameObject _templates;
        private readonly List<GameObject> _spawned = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _container = new DiContainer();
            _container.Register<IPrefabDep, PrefabDep>(Lifetime.Singleton);
            _templates = new GameObject("templates");
            _templates.SetActive(false);
            FailingProbe.Created.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned) if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
            if (_templates != null) Object.DestroyImmediate(_templates);
            _templates = null;
            _container?.Dispose();
            _container = null;
        }

        private GameObject NewTemplate(string name)
        {
            var template = new GameObject(name);
            template.transform.SetParent(_templates.transform, false);
            return template;
        }

        private AwakeProbe NewProbeTemplate()
        {
            var template = NewTemplate("probe");
            var probe = template.AddComponent<AwakeProbe>();
            var child = new GameObject("child");
            child.transform.SetParent(template.transform, false);
            child.AddComponent<ProbeChild>();
            return probe;
        }

        private T Track<T>(T instance) where T : Component
        {
            _spawned.Add(instance.gameObject);
            return instance;
        }

        [Test]
        public void InstantiatePrefab_InjectsTheWholeHierarchy_BeforeAwakeRuns()
        {
            var instance = Track(_container.InstantiatePrefab(NewProbeTemplate()));

            Assert.AreEqual(1, instance.AwakeCount, "the instance is active, so Awake ran exactly once");
            Assert.IsNotNull(instance.DepSeenInAwake, "[Inject] fields must be set before Awake");
            Assert.IsNotNull(instance.ChildSeenInAwake, "[Resolve] fields must be set before Awake");
            Assert.IsNotNull(instance.ChildDepSeenInAwake, "children are injected before the root's Awake");
            Assert.IsNull(instance.transform.parent);
            Assert.IsTrue(instance.gameObject.activeInHierarchy);
        }

        [Test]
        public void InstantiatePrefab_ResolveParent_FindsAComponentOnAnInactivePrefabAncestor()
        {
            var template = NewTemplate("parent lookup");
            template.AddComponent<ParentLookupMarker>();
            var child = new GameObject("child");
            child.transform.SetParent(template.transform, false);
            child.AddComponent<ParentLookupProbe>();

            var clone = _container.InstantiatePrefab(template);
            _spawned.Add(clone);
            var probe = clone.GetComponentInChildren<ParentLookupProbe>(true);

            Assert.IsNotNull(probe.Marker, "the prefab root is inactive in the hierarchy while injection runs");
        }

        [UnityTest]
        public IEnumerator DisposingRoot_ReleasesTheHiddenPrefabHolder()
        {
            var clone = _container.InstantiatePrefab(NewProbeTemplate());
            _spawned.Add(clone.gameObject);

            var state = typeof(DiContainer)
                .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(_container);
            var holder = (GameObject)typeof(ResolutionState)
                .GetField("_inactiveHolder", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(state);

            Assert.IsNotNull(holder, "the first prefab clone creates the hidden holder");

            _container.Dispose();
            yield return null;

            Assert.IsTrue(holder == null, "disposing the root must destroy the hidden holder");
        }

        [Test]
        public void InstantiatePrefab_UnderAnActiveParent_ReparentsAndActivates()
        {
            var parent = new GameObject("parent");
            _spawned.Add(parent);

            var instance = Track(_container.InstantiatePrefab(NewProbeTemplate(), parent.transform));

            Assert.AreSame(parent.transform, instance.transform.parent);
            Assert.AreEqual(1, instance.AwakeCount);
            Assert.IsNotNull(instance.DepSeenInAwake);
        }

        [Test]
        public void InstantiatePrefab_UnderAnInactiveParent_DefersAwake_UntilTheParentIsActivated()
        {
            var parent = new GameObject("inactive parent");
            parent.SetActive(false);
            _spawned.Add(parent);

            var instance = Track(_container.InstantiatePrefab(NewProbeTemplate(), parent.transform));

            Assert.AreEqual(0, instance.AwakeCount, "Unity defers Awake while the parent is inactive");
            Assert.IsNotNull(instance.Dep, "but the instance is already injected");

            parent.SetActive(true);

            Assert.AreEqual(1, instance.AwakeCount);
            Assert.IsNotNull(instance.DepSeenInAwake);
        }

        [Test]
        public void InstantiatePrefab_KeepsAnInactivePrefabRootInactive()
        {
            var probe = NewProbeTemplate();
            probe.gameObject.SetActive(false);

            var instance = Track(_container.InstantiatePrefab(probe));

            Assert.IsFalse(instance.gameObject.activeSelf);
            Assert.AreEqual(0, instance.AwakeCount);
            Assert.IsNotNull(instance.Dep);
        }

        [Test]
        public void InstantiatePrefab_OfAGameObject_ReturnsTheInjectedRoot()
        {
            var template = NewProbeTemplate().gameObject;

            var root = _container.InstantiatePrefab(template);
            _spawned.Add(root);

            var probe = root.GetComponentsInChildren<AwakeProbe>(true)[0];
            Assert.IsNotNull(probe.DepSeenInAwake);
        }

        [Test]
        public void InstantiatePrefab_FromAScope_UsesThatScopesRegistrations()
        {
            using (var scope = _container.CreateScope())
            {
                scope.Register<IPrefabDep, OtherPrefabDep>(Lifetime.Singleton);

                var instance = Track(scope.InstantiatePrefab(NewProbeTemplate()));

                Assert.IsInstanceOf<OtherPrefabDep>(instance.DepSeenInAwake);
            }
        }

        [UnityTest]
        public IEnumerator InstantiatePrefab_WhenInjectionFails_RethrowsAndDestroysTheInstance()
        {
            var template = NewTemplate("failing");
            var failing = template.AddComponent<FailingProbe>();
            FailingProbe.Created.Clear();

            Assert.Throws<InvalidOperationException>(() => _container.InstantiatePrefab(failing));

            yield return null; // Destroy is deferred to the end of the frame in play mode

            Assert.AreEqual(1, FailingProbe.Created.Count);
            Assert.IsTrue(FailingProbe.Created[0] == null, "the half-injected instance must not be left behind");
        }

        [Test]
        public void InjectGameObjectHierarchy_InjectsAnExistingHierarchy_IncludingInactiveChildren()
        {
            var root = new GameObject("scene root");
            _spawned.Add(root);
            var child = new GameObject("inactive child");
            child.transform.SetParent(root.transform, false);
            var probe = child.AddComponent<ProbeChild>();
            child.SetActive(false);

            _container.InjectGameObjectHierarchy(root);

            Assert.IsNotNull(probe.Dep);
        }

        [Test]
        public void InstantiatePrefab_OfAScriptableObject_ClonesAndInjectsTheClone()
        {
            var asset = ScriptableObject.CreateInstance<PrefabSettings>();
            PrefabSettings clone = null;
            try
            {
                clone = _container.InstantiatePrefab(asset);

                Assert.AreNotSame(asset, clone);
                Assert.IsNotNull(clone.Dep);
                Assert.IsNull(asset.Dep, "the source asset is left untouched");
            }
            finally
            {
                Object.DestroyImmediate(asset);
                if (clone != null) Object.DestroyImmediate(clone);
            }
        }

        [UnityTest]
        public IEnumerator InstantiatePrefab_OfAScriptableObject_WhenInjectionFails_DestroysTheClone()
        {
            FailingSettings.Created.Clear();
            var asset = ScriptableObject.CreateInstance<FailingSettings>();
            try
            {
                Assert.Throws<InvalidOperationException>(() => _container.InstantiatePrefab(asset));

                yield return null; // Destroy is deferred to the end of the frame in play mode

                Assert.AreEqual(2, FailingSettings.Created.Count, "the asset and its clone");
                Assert.IsTrue(FailingSettings.Created[1] == null, "the half-injected clone must not be left behind");
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void InstantiatePrefab_NullPrefab_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => _container.InstantiatePrefab<AwakeProbe>(null));
        }

        [Test]
        public void InstantiatePrefab_AfterDispose_Throws_WithoutCreatingAnything()
        {
            var probe = NewTemplate("counted").AddComponent<CountedProbe>();
            int before = CountedProbe.Constructed;
            _container.Dispose();

            Assert.Throws<ObjectDisposedException>(() => _container.InstantiatePrefab(probe));
            Assert.Throws<ObjectDisposedException>(() => _container.InjectGameObjectHierarchy(probe.gameObject));

            Assert.AreEqual(before, CountedProbe.Constructed, "a disposed container must not instantiate (and then destroy) anything");
        }
    }
}
