using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace November.DI
{
    /// <summary>
    /// One link of the resolution path: a registration being built (or, with a null descriptor, an
    /// object being injected) by a particular scope.
    /// </summary>
    internal readonly struct ResolutionFrame
    {
        public readonly ServiceDescriptor Descriptor;
        public readonly DiContainer Scope;
        public readonly Type Type;
        public readonly object Member; // ParameterInfo or FieldInfo currently being resolved for this frame

        public ResolutionFrame(ServiceDescriptor descriptor, DiContainer scope, Type type, object member = null)
        {
            Descriptor = descriptor;
            Scope = scope;
            Type = type;
            Member = member;
        }

        public ResolutionFrame WithMember(object member) => new ResolutionFrame(Descriptor, Scope, Type, member);
    }

    /// <summary>
    /// State shared by a root container and every scope created from it: the resolution stack (cycle
    /// detection and error paths), once-only warning sets, the miss counter and the hidden inactive
    /// holder used to inject prefabs before their Awake runs. Allocated once per root, not per scope.
    /// </summary>
    internal sealed class ResolutionState
    {
        public readonly List<ResolutionFrame> Stack = new List<ResolutionFrame>(16);
        public int MissCount;

        private HashSet<Type> _warnedMisses;
        private HashSet<Type> _warnedTransientDisposables;
        private HashSet<object> _warnedOptionalParameters;
        private GameObject _inactiveHolder;

        public bool FirstMiss(Type type) => (_warnedMisses ?? (_warnedMisses = new HashSet<Type>())).Add(type);

        public bool FirstTransientDisposable(Type type)
            => (_warnedTransientDisposables ?? (_warnedTransientDisposables = new HashSet<Type>())).Add(type);

        public bool FirstOptionalFallback(object parameter)
            => (_warnedOptionalParameters ?? (_warnedOptionalParameters = new HashSet<object>())).Add(parameter);

        /// <summary>
        /// A hidden, inactive GameObject. Prefabs are instantiated under it so Awake/OnEnable are
        /// deferred until injection is done and the instance is re-parented. Re-created if Unity
        /// destroyed it (scene unload).
        /// </summary>
        public GameObject GetInactiveHolder()
        {
            if (_inactiveHolder != null && _inactiveHolder.scene != SceneManager.GetActiveScene()) ReleaseHolder();

            if (_inactiveHolder == null)
            {
                var holder = new GameObject("[November.DI] Inactive Holder");
                // HideInHierarchy only: DontSave flags can keep an object alive across scene loads,
                // which is unsafe for a holder that must stay in the active scene.
                holder.hideFlags = HideFlags.HideInHierarchy;
                holder.SetActive(false);
                _inactiveHolder = holder;
            }
            return _inactiveHolder;
        }

        /// <summary>
        /// Edit mode only: nothing disposes the holder between calls there, and a hidden object that is left
        /// in a scene is saved with it. Skipped while a clone is still under it (a nested call).
        /// </summary>
        public void ReleaseHolderIfEmpty()
        {
            if (_inactiveHolder != null && _inactiveHolder.transform.childCount == 0) ReleaseHolder();
        }

        public void ReleaseHolder()
        {
            if (_inactiveHolder != null) DiContainer.DestroyObject(_inactiveHolder);
            _inactiveHolder = null;
        }
    }
}
