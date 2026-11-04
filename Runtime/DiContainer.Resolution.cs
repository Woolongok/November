using System;
using System.Collections.Generic;
using UnityEngine;

namespace November.DI
{
    /// <summary>
    /// Resolution partition: Resolve / TryResolve / ResolveAll, registry lookup and lifetime dispatch.
    /// </summary>
    public sealed partial class DiContainer
    {
        public TService Resolve<TService>() => (TService)Resolve(typeof(TService));

        public object Resolve(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            ThrowIfDisposed();
            MainThread.Assert(type);
            return ResolveInternal(type);
        }

        public bool TryResolve<TService>(out TService result)
        {
            ThrowIfDisposed();
            var type = typeof(TService);
            MainThread.Assert(type);

            // false means exactly one thing: nothing is registered. Everything else (disposed container,
            // wrong thread, a throwing constructor or factory, a dependency cycle) propagates untouched.
            if (!CanResolve(type))
            {
                RecordMiss(type);
                result = default;
                return false;
            }

            result = (TService)ResolveInternal(type);
            return true;
        }

        public IEnumerable<TService> ResolveAll<TService>(bool includeParents = true)
        {
            ThrowIfDisposed();
            MainThread.Assert(typeof(TService));
            return (TService[])ResolveAllCore(typeof(TService), includeParents);
        }

        public bool HasRegistration(Type type)
        {
            ThrowIfDisposed();
            MainThread.Assert(nameof(HasRegistration));
            if (type == null) throw new ArgumentNullException(nameof(type));
            return CanResolve(type);
        }

        private void RecordMiss(Type type)
        {
            _state.MissCount++;
            if (_options.WarnOnTryResolveMiss && _state.FirstMiss(type))
                Debug.LogWarning(
                    $"[DiContainer] TryResolve<{PrettyName(type)}> found no registration and returned false. " +
                    "Logged once per type; use HasRegistration to probe silently, or fix the wiring if it is required.");
        }

        // ── Lookup ──────────────────────────────────────────────────────────

        /// <summary>Nearest registration for the type, own scope first; the last binding in a scope wins.</summary>
        private bool FindDescriptor(Type type, out ServiceDescriptor descriptor, out DiContainer owner)
        {
            for (var scope = this; scope != null; scope = scope._parent)
            {
                if (scope._registry != null && scope._registry.TryGetValue(type, out var list) && list.Count > 0)
                {
                    descriptor = list[list.Count - 1];
                    owner = scope;
                    return true;
                }
            }

            descriptor = null;
            owner = null;
            return false;
        }

        private static bool IsCollectionDefinition(Type definition)
            => definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>) ||
               definition == typeof(IReadOnlyCollection<>) || definition == typeof(IList<>) ||
               definition == typeof(ICollection<>);

        /// <summary>Registered, or an implicit wrapper over something that is.</summary>
        private bool CanResolve(Type type)
        {
            if (FindDescriptor(type, out _, out _)) return true;

            if (type.IsArray)
                return type.GetArrayRank() == 1 && !type.GetElementType().IsValueType;

            if (!type.IsGenericType) return false;

            var definition = type.GetGenericTypeDefinition();
            var argument = type.GetGenericArguments()[0];
            if (definition == typeof(Func<>) || definition == typeof(Lazy<>)) return CanResolve(argument);
            return IsCollectionDefinition(definition) && !argument.IsValueType;
        }

        // ── Dispatch ────────────────────────────────────────────────────────

        private object ResolveInternal(Type type)
        {
            if (FindDescriptor(type, out var descriptor, out var owner))
                return ResolveDescriptor(descriptor, owner);
            return ResolveImplicit(type);
        }

        /// <summary>Func&lt;T&gt;, Lazy&lt;T&gt;, IEnumerable&lt;T&gt;-style collections and T[] when no registration matched.</summary>
        private object ResolveImplicit(Type type)
        {
            if (type.IsArray)
            {
                var element = type.GetElementType();
                if (type.GetArrayRank() == 1 && !element.IsValueType)
                    return ResolveAllCore(element, includeParents: true);
            }
            else if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                var argument = type.GetGenericArguments()[0];

                if (definition == typeof(Func<>)) { RequireRegistered(argument, type); return CreateFunc(argument); }
                if (definition == typeof(Lazy<>)) { RequireRegistered(argument, type); return CreateLazy(argument); }
                if (IsCollectionDefinition(definition) && !argument.IsValueType)
                    return ResolveAllCore(argument, includeParents: true);
            }

            throw NotRegistered(type);
        }

        private void RequireRegistered(Type argument, Type wrapper)
        {
            if (!CanResolve(argument)) throw NotRegistered(argument, wrapper);
        }

        /// <summary>
        /// Runs on the RESOLVING scope (<c>this</c>); <paramref name="owner"/> is the scope that holds the
        /// registration. Only singletons are built by the owner.
        /// </summary>
        private object ResolveDescriptor(ServiceDescriptor descriptor, DiContainer owner)
        {
            switch (descriptor.Kind)
            {
                case DescriptorKind.Instance: return GetRegisteredInstance(descriptor);
                case DescriptorKind.Alias: return ResolveAlias(descriptor);
            }

            if (descriptor.Lifetime == Lifetime.Singleton)
            {
                if (owner._disposed) throw OwnerDisposed(descriptor);
                return owner.GetOrCreateCached(descriptor);
            }

            if (descriptor.Lifetime == Lifetime.Scoped)
            {
                if (_parent == null && _options.ValidateScopes) throw ScopedFromRoot(descriptor);
                return GetOrCreateCached(descriptor);
            }

            return CreateTransient(descriptor);
        }

        /// <summary>Singleton cache of the owner, or scoped cache of the resolving scope; constructs on a miss.</summary>
        private object GetOrCreateCached(ServiceDescriptor descriptor)
        {
            if (_instances != null && _instances.TryGetValue(descriptor, out var existing))
            {
                // Scene objects die with their scene: never hand out a destroyed Unity object.
                if (!IsDestroyedUnityObject(existing)) return existing;
                _instances.Remove(descriptor);
            }

            var instance = Construct(descriptor);

            if (_instances == null) _instances = new Dictionary<ServiceDescriptor, object>();
            _instances[descriptor] = instance;
            MarkInjected(instance);
            Track(instance);
            return instance;
        }

        private object CreateTransient(ServiceDescriptor descriptor)
        {
            var instance = Construct(descriptor);

            if (_parent == null && _options.WarnOnRootTransientDisposables)
                WarnIfRootTransientDisposable(instance);

            Track(instance);
            return instance;
        }

        private object GetRegisteredInstance(ServiceDescriptor descriptor)
        {
            if (descriptor.IsDestroyedInstance)
                throw new InvalidOperationException(
                    $"[DiContainer] Registered instance for '{descriptor.ServiceType.FullName}' was destroyed (scene unload?). " +
                    "Register the live instance again (RegisterInstance replaces a destroyed one); the dead reference is never returned.");
            return descriptor.Instance;
        }

        /// <summary>Forwards to the target's resolution: same instance, no second injection or disposal.</summary>
        private object ResolveAlias(ServiceDescriptor descriptor)
        {
            int mark = EnterFrame(descriptor);
            try
            {
                var result = ResolveInternal(descriptor.ImplementationType);
                if (!descriptor.ServiceType.IsInstanceOfType(result))
                    throw new InvalidOperationException(
                        $"[DiContainer] Alias '{PrettyName(descriptor.ServiceType)}' -> '{PrettyName(descriptor.ImplementationType)}' " +
                        $"resolved to a '{result.GetType().Name}', which is not a '{PrettyName(descriptor.ServiceType)}'.");
                return result;
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

        // ── Collections ─────────────────────────────────────────────────────

        /// <summary>
        /// Every binding of <paramref name="elementType"/>, ancestors first, built by THIS scope — so scoped and
        /// transient entries are exactly what <c>Resolve</c> would have produced. Returns a typed array, which
        /// satisfies IEnumerable&lt;T&gt;, IReadOnlyList&lt;T&gt; and T[] without any generic instantiation.
        /// </summary>
        private Array ResolveAllCore(Type elementType, bool includeParents)
        {
            int count = 0;
            for (var scope = this; scope != null; scope = includeParents ? scope._parent : null)
                if (scope._registry != null && scope._registry.TryGetValue(elementType, out var bindings))
                    count += bindings.Count;

            var result = Array.CreateInstance(elementType, count);
            if (count == 0) return result;

            int index = 0;
            CollectInto(this, elementType, includeParents, result, ref index);
            return result;
        }

        private void CollectInto(DiContainer resolver, Type elementType, bool includeParents, Array target, ref int index)
        {
            if (includeParents && _parent != null)
                _parent.CollectInto(resolver, elementType, true, target, ref index);

            if (_registry == null || !_registry.TryGetValue(elementType, out var bindings)) return;
            for (int i = 0; i < bindings.Count; i++)
                target.SetValue(resolver.ResolveDescriptor(bindings[i], this), index++);
        }
    }
}
