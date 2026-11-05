using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace November.DI.Tests
{
    internal interface IEditDep { }
    internal sealed class EditDep : IEditDep { }

    /// <summary>
    /// Remembers which object it sat under while it was being injected (the container's hidden holder) and
    /// lets a test run code at that moment.
    /// </summary>
    internal sealed class EditHolderProbe : MonoBehaviour
    {
        public static Action<EditHolderProbe> WhileInjecting;

        public GameObject HolderSeen;

        [Inject]
        private void Init(IEditDep dep)
        {
            HolderSeen = transform.parent != null ? transform.parent.gameObject : null;
            WhileInjecting?.Invoke(this);
        }
    }

    /// <summary>
    /// In edit mode nothing disposes the container's hidden holder between calls and a hidden object left in
    /// a scene is saved with it, so InstantiatePrefab removes the holder again before it returns.
    /// (The PlayMode tests cover what happens in play mode, where the holder lives as long as the container.)
    /// </summary>
    internal class PrefabEditModeTests
    {
        private readonly List<GameObject> _cleanup = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            Application.isPlaying = false;
        }

        [TearDown]
        public void TearDown()
        {
            Application.isPlaying = true;
            EditHolderProbe.WhileInjecting = null;
            foreach (var go in _cleanup) if (go != null) Object.DestroyImmediate(go);
            _cleanup.Clear();
        }

        private EditHolderProbe NewTemplate()
        {
            var template = new GameObject("template");
            _cleanup.Add(template);
            return template.AddComponent<EditHolderProbe>();
        }

        private static DiContainer NewContainer()
        {
            var container = new DiContainer();
            container.Register<IEditDep, EditDep>(Lifetime.Singleton);
            return container;
        }

        [Test]
        public void InstantiatePrefab_InEditMode_LeavesNoHiddenHolderBehind()
        {
            Assert.IsFalse(Application.isPlaying, "this is an EditMode test");
            var template = NewTemplate();

            using (var container = NewContainer())
            {
                var instance = container.InstantiatePrefab(template);
                _cleanup.Add(instance.gameObject);

                Assert.IsFalse(ReferenceEquals(instance.HolderSeen, null), "the clone sat under a holder while it was injected");
                Assert.IsTrue(instance.HolderSeen == null, "and the holder is gone again: a hidden object left in the scene would be saved with it");
                Assert.IsNull(instance.transform.parent);
            }
        }

        [Test]
        public void InstantiatePrefab_InEditMode_ANestedCall_DoesNotDestroyTheOuterClonesHolder()
        {
            var template = NewTemplate();
            int injected = 0;

            using (var container = NewContainer())
            {
                EditHolderProbe inner = null;
                EditHolderProbe.WhileInjecting = probe =>
                {
                    if (injected++ > 0) return; // only the outer clone spawns the nested one
                    inner = container.InstantiatePrefab(template); // the holder still has the outer clone under it
                    _cleanup.Add(inner.gameObject);
                };

                var outer = container.InstantiatePrefab(template);
                _cleanup.Add(outer.gameObject);

                Assert.IsNotNull(inner, "the nested call ran");
                Assert.IsTrue(outer != null, "the outer clone survived the nested call");
                Assert.IsTrue(inner != null);
                Assert.IsNull(outer.transform.parent, "the outer clone was still re-parented afterwards");
                Assert.IsTrue(outer.HolderSeen == null, "and the holder is gone once the outermost call is done");
            }
        }
    }
}
