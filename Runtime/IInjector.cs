using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace November.DI
{

    /// <summary>
    /// Abstraction for injecting dependencies into existing objects and instantiating prefabs.
    /// Segregated from registration/resolution — follows the Interface Segregation Principle.
    /// </summary>
    public interface IInjector
    {
        /// <summary>
        /// Injects dependencies into an existing object instance (field/method injection via [Inject]).
        /// </summary>
        void InjectInto(object instance);

        /// <summary>
        /// Instantiates a prefab and injects its whole hierarchy BEFORE Awake / OnEnable run. The clone is
        /// placed where <c>Object.Instantiate</c> would put it: under <paramref name="parent"/>, or in the
        /// active scene when there is none. If injection fails the whole clone is destroyed and the exception
        /// is rethrown. A <c>ScriptableObject</c> is cloned and injected.
        /// </summary>
        T InstantiatePrefab<T>(T prefab, Transform parent = null) where T : Object;

        /// <summary>
        /// Injects dependencies into all MonoBehaviours in a GameObject hierarchy (inactive ones included).
        /// Objects that already ran Awake cannot be helped retroactively.
        /// </summary>
        void InjectGameObjectHierarchy(Object instance);
    }
}
