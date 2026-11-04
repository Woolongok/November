using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using UnityEngine;
using Object = UnityEngine.Object;

namespace November.DI
{
    /// <summary>
    /// Injection partition: construction, [Inject] / [Resolve] member injection, prefab instantiation
    /// and child scopes. Order for a built object: constructor, then [Inject] fields, then [Inject]
    /// methods (base classes first), then [Resolve] fields.
    /// </summary>
    public sealed partial class DiContainer
    {
        // Objects the container has already injected AND cached (singleton / scoped). A factory that hands
        // one of them back (a forwarding factory) must not get a second injection. Weak: costs no lifetime.
        private static readonly ConditionalWeakTable<object, object> s_injected = new ConditionalWeakTable<object, object>();
        private static readonly object s_injectedMarker = new object();

        // Reusable buffers for hierarchy injection. Main thread only; a stack because injection can nest.
        private static readonly Stack<List<MonoBehaviour>> s_buffers = new Stack<List<MonoBehaviour>>();

        // ── Construction ────────────────────────────────────────────────────

        /// <summary>Builds and injects one instance for a registration, as this scope (see <see cref="ResolveDescriptor"/>).</summary>
        private object Construct(ServiceDescriptor descriptor)
        {
            int mark = EnterFrame(descriptor);
            try
            {
                var instance = descriptor.Kind == DescriptorKind.Factory
                    ? InvokeFactory(descriptor)
                    : CreateInstance(descriptor.ImplementationType);

                var plan = InjectionMetadata.GetPlan(instance.GetType());
                bool alreadyInjected = descriptor.Kind == DescriptorKind.Factory && s_injected.TryGetValue(instance, out _);
                if (plan.NeedsAnyInjection && !alreadyInjected)
                {
                    try { InjectCore(instance, plan); }
                    catch
                    {
                        DisposeQuietly(instance);
                        throw;
                    }
                }

                return instance;
            }
            catch (Exception ex)
            {
                Annotate(ex);
                throw;
            }
            finally
            {
                LeaveFrame(mark);
            }
        }

        private object InvokeFactory(ServiceDescriptor descriptor)
        {
            var result = descriptor.Factory(this);
            if (result == null || IsDestroyedUnityObject(result))
                throw new InvalidOperationException(
                    $"[DiContainer] The factory registered for '{PrettyName(descriptor.ServiceType)}' returned null.");
            return result;
        }

        private object CreateInstance(Type implementationType)
        {
            var plan = InjectionMetadata.GetPlan(implementationType);
            var constructor = plan.Constructor; // throws a clear error for none / ambiguous
            var parameters = plan.ConstructorParameters;

            object[] args = parameters.Length == 0 ? Array.Empty<object>() : new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                args[i] = ResolveParameter(parameters[i]);

            try { return constructor.Invoke(args); }
            catch (TargetInvocationException ex) { throw Unwrap(ex); }
        }

        /// <summary>Constructor and [Inject]-method parameters: resolved, or defaulted when optional and unregistered.</summary>
        private object ResolveParameter(ParameterInfo parameter)
        {
            var type = parameter.ParameterType;

            if (parameter.HasDefaultValue && !CanResolve(type))
            {
                // An unregistered SERVICE (interface / abstract) quietly becoming null hides a mis-wire, so it
                // is loud. Concrete and value-type defaults (int, string, Transform parent = null) are normal.
                if (type.IsInterface || type.IsAbstract) WarnOptionalFallback(parameter);
                return parameter.DefaultValue;
            }

            SetMember(parameter);
            return ResolveInternal(type);
        }

        /// <summary>Reflection wraps whatever a constructor or [Inject] method throws; surface the real exception, stack intact.</summary>
        private static Exception Unwrap(TargetInvocationException ex)
        {
            if (ex.InnerException == null) return ex;
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            return ex.InnerException; // unreachable
        }

        private static void DisposeQuietly(object instance)
        {
            if (!(instance is IDisposable disposable)) return;
            try { disposable.Dispose(); }
            catch { /* the injection failure being thrown is the error that matters */ }
        }

        // ── Injection ───────────────────────────────────────────────────────

        public void InjectInto(object instance)
        {
            ThrowIfDisposed();
            if (instance == null)
                throw new ArgumentNullException(nameof(instance),
                    "InjectInto(null) hides a caller bug (usually a factory that returned null). Fix the caller instead of injecting nothing.");
            MainThread.Assert(nameof(InjectInto));

            var type = instance.GetType();
            var plan = InjectionMetadata.GetPlan(type);
            if (!plan.NeedsAnyInjection) return;

            var stack = _state.Stack;
            int mark = stack.Count;
            stack.Add(new ResolutionFrame(null, this, type));
            try { InjectCore(instance, plan); }
            catch (Exception ex)
            {
                Annotate(ex);
                throw;
            }
            finally
            {
                LeaveFrame(mark);
            }
        }

        private void InjectCore(object instance, TypePlan plan)
        {
            var fields = plan.InjectFields;
            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                SetMember(field);
                field.SetValue(instance, ResolveInternal(field.FieldType));
            }

            var methods = plan.InjectMethods;
            for (int i = 0; i < methods.Length; i++)
            {
                var parameters = methods[i].Parameters;
                object[] args = parameters.Length == 0 ? Array.Empty<object>() : new object[parameters.Length];
                for (int p = 0; p < parameters.Length; p++)
                    args[p] = ResolveParameter(parameters[p]);

                try { methods[i].Method.Invoke(instance, args); }
                catch (TargetInvocationException ex) { throw Unwrap(ex); }
            }

            if (plan.NeedsComponentResolution && instance is Component component)
                ComponentResolver.Resolve(component);
        }

        private static void MarkInjected(object instance)
        {
            if (!InjectionMetadata.GetPlan(instance.GetType()).NeedsAnyInjection) return;
            if (!s_injected.TryGetValue(instance, out _)) s_injected.Add(instance, s_injectedMarker);
        }

        // ── Prefabs and hierarchies ─────────────────────────────────────────

        /// <summary>
        /// Instantiates a prefab and injects its hierarchy BEFORE Awake / OnEnable run: the clone is created
        /// under a hidden inactive holder (which defers both), injected, then re-parented, which activates it
        /// if its own active state and the new parent allow. Awake therefore sees every [Inject] and [Resolve] field.
        /// </summary>
        public T InstantiatePrefab<T>(T prefab, Transform parent = null) where T : Object
        {
            ThrowIfDisposed();
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            MainThread.Assert(nameof(InstantiatePrefab));

            // ScriptableObjects and other assets have no hierarchy: nothing to defer and no parent to apply.
            if (!(prefab is GameObject) && !(prefab is Component))
            {
                T asset = Object.Instantiate(prefab);
                try { InjectInto(asset); }
                catch
                {
                    DestroyObject(asset);
                    throw;
                }
                return asset;
            }

            var holder = _state.GetInactiveHolder();
            T instance = Object.Instantiate(prefab, holder.transform);
            var root = GameObjectOf(instance);

            try { InjectHierarchyCore(root); }
            catch
            {
                DestroyObject(instance);
                throw;
            }

            root.transform.SetParent(parent, false);
            return instance;
        }

        /// <summary>
        /// Injects every MonoBehaviour under a GameObject (inactive ones included). Objects that already ran
        /// Awake cannot be helped retroactively: use <see cref="InstantiatePrefab{T}"/> for new objects.
        /// </summary>
        public void InjectGameObjectHierarchy(Object instance)
        {
            ThrowIfDisposed();
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            MainThread.Assert(nameof(InjectGameObjectHierarchy));

            var root = GameObjectOf(instance);
            if (root != null) InjectHierarchyCore(root);
        }

        private void InjectHierarchyCore(GameObject root)
        {
            var buffer = s_buffers.Count > 0 ? s_buffers.Pop() : new List<MonoBehaviour>(16);
            try
            {
                root.GetComponentsInChildren(true, buffer);

                int missingScripts = 0;
                for (int i = 0; i < buffer.Count; i++)
                {
                    var behaviour = buffer[i];
                    if (behaviour == null) { missingScripts++; continue; } // a "Missing (Mono Script)" slot
                    InjectInto(behaviour);
                }

                if (missingScripts > 0)
                    Debug.LogWarning(
                        $"[DiContainer] '{root.name}' has {missingScripts} MonoBehaviour slot(s) with a missing script; they were skipped.",
                        root);
            }
            finally
            {
                buffer.Clear();
                s_buffers.Push(buffer);
            }
        }

        private static GameObject GameObjectOf(Object instance)
        {
            if (instance is GameObject gameObject) return gameObject;
            if (instance is Component component) return component.gameObject;
            return null;
        }

        /// <summary>Destroy in play mode, DestroyImmediate in edit mode (Destroy is not allowed there).</summary>
        internal static void DestroyObject(Object instance)
        {
            if (instance == null) return;
            if (Application.isPlaying) Object.Destroy(instance);
            else Object.DestroyImmediate(instance);
        }

        // ── Scopes ──────────────────────────────────────────────────────────

        public IDiScope CreateScope()
        {
            ThrowIfDisposed();
            MainThread.Assert(nameof(CreateScope));
            return new DiContainer(this);
        }
    }
}
