using System;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace November.DI
{

    /// <summary>
    /// DiContainer injection partition: instance creation, [Inject]/[Resolve]
    /// member injection, prefab instantiation and scope factory.
    /// Same type via partial — no behaviour change.
    /// </summary>
    public sealed partial class DiContainer : IDiScope, IServiceProvider, IScopeFactory
    {
        private object CreateAndTrack(Type type, Func<DiContainer, object> factory,
                                      Lifetime lifetime, bool externalInstance)
        {
            if (!Constructing.Add(type))
                throw new InvalidOperationException(
                    $"Circular field/method injection dependency detected for '{type.FullName}'. " +
                    "Use Lazy<T> or Func<T> to break the cycle.");

            try
            {
                var instance = factory != null ? factory(this) : CreateInstance(type);

                try { InjectInto(instance); }
                catch
                {
                    if (instance is IDisposable d) d.Dispose();
                    throw;
                }

                if (!externalInstance && instance is IDisposable disposable)
                {
                    // Scope ownership (MS-DI semantics): only Singleton/Scoped
                    // lifetimes are rooted here. Transient disposables resolved
                    // from a long-lived root accumulate until Dispose — prefer
                    // a short-lived scope for per-enemy/per-action disposables.
                    _disposables.Add(disposable);
                }

                return instance;
            }
            finally
            {
                Constructing.Remove(type);
            }
        }

        private object CreateInstance(Type type)
        {
            var ctor = InjectionMetadata.GetConstructor(type);
            var paramTypes = InjectionMetadata.GetConstructorParamTypes(type);
            var parameters = ctor.GetParameters();
            var args = new object[paramTypes.Length];

            for (int i = 0; i < paramTypes.Length; i++)
            {
                if (parameters[i].HasDefaultValue && !HasRegistration(paramTypes[i]))
                {
                    // LOUD fallback: an unregistered SERVICE (interface/abstract)
                    // silently becoming null hides a mis-wire. Concrete/value
                    // defaults (int, string, Transform parent = null) are
                    // legitimate and stay silent.
                    var pt = paramTypes[i];
                    if (pt.IsInterface || pt.IsAbstract)
                        Debug.LogError($"[DiContainer] No registration for optional ctor param '{parameters[i].Name}' ({pt.Name}) on '{type.Name}' — falling back to default. Register it or make the dependency required.");
                    args[i] = parameters[i].DefaultValue;
                }
                else args[i] = Resolve(paramTypes[i]);
            }

            return InjectionMetadata.GetConstructorDelegate(type)(args);
        }

        public void InjectInto(object instance)
        {
            if (instance == null)
                throw new System.ArgumentNullException(nameof(instance),
                    "InjectInto(null) hides a caller bug (usually a factory that returned null). Fix the caller instead of injecting nothing.");

            var type = instance.GetType();

            // ── [Inject] field injection ────────────────────────────────
            if (InjectionMetadata.TypeNeedsContainerInjection(type))
            {
                foreach (var field in InjectionMetadata.GetInjectFields(type))
                {
                    try
                    {
                        field.SetValue(instance, Resolve(field.FieldType));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError(
                            $"[DiContainer] Failed to inject field '{field.Name}' " +
                            $"on '{type.Name}':\n{ex}");
                        throw;
                    }
                }

                foreach (var method in InjectionMetadata.GetInjectMethods(type))
                {
                    try
                    {
                        var args = method.GetParameters()
                                         .Select(p => Resolve(p.ParameterType))
                                         .ToArray();
                        method.Invoke(instance, args);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError(
                            $"[DiContainer] Exception invoking [Inject] method " +
                            $"'{method.Name}' on '{type.Name}':\n{ex}");
                        throw;
                    }
                }
            }

            // ── [Resolve] component resolution (only for Components) ────
            if (instance is Component component &&
                InjectionMetadata.TypeNeedsComponentResolution(type))
            {
                AssertMainThread("[InjectInto] component resolution");
                ComponentResolver.Resolve(component);
            }
        }

        public T InstantiatePrefab<T>(T prefab, Transform parent = null) where T : Object
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            ThrowIfDisposed();
            AssertMainThread(nameof(InstantiatePrefab));

            var instance = Object.Instantiate(prefab, parent);
            InjectGameObjectHierarchy(instance);
            return instance;
        }

        public void InjectGameObjectHierarchy(Object instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            AssertMainThread(nameof(InjectGameObjectHierarchy));

            var go = instance switch
            {
                GameObject g => g,
                Component c => c.gameObject,
                _ => null
            };
            if (go == null) return;

            foreach (var mono in go.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
                InjectInto(mono);
        }

        public IDiScope CreateScope()
        {
            ThrowIfDisposed();
            AssertMainThread(nameof(CreateScope));
            return new DiContainer(this);
        }
    }

}
