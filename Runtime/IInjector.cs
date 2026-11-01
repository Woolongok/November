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
        /// Instantiates a prefab and injects dependencies into its hierarchy.
        /// </summary>
        T InstantiatePrefab<T>(T prefab, Transform parent = null) where T : Object;

        /// <summary>
        /// Injects dependencies into all MonoBehaviours in a GameObject hierarchy.
        /// </summary>
        void InjectGameObjectHierarchy(Object instance);
    }
}
