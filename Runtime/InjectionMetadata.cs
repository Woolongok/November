using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace November.DI
{
    /// <summary>
    /// Public cross-assembly query over the internal injection metadata. Scene contexts in other
    /// assemblies use it to skip MonoBehaviours that have no injection points; the metadata types
    /// themselves stay internal so reflection details never leak across assemblies.
    /// </summary>
    public static class InjectionQuery
    {
        /// <summary>True when the type has any [Inject] field/method or [Resolve] field. O(1) after the first call per type.</summary>
        public static bool TypeNeedsAnyInjection(Type type)
        {
            MainThread.Assert(nameof(TypeNeedsAnyInjection));
            if (type == null) throw new ArgumentNullException(nameof(type));
            return InjectionMetadata.GetPlan(type).NeedsAnyInjection;
        }
    }

    internal readonly struct InjectMethodPlan
    {
        public readonly MethodInfo Method;
        public readonly ParameterInfo[] Parameters;

        public InjectMethodPlan(MethodInfo method, ParameterInfo[] parameters)
        {
            Method = method;
            Parameters = parameters;
        }
    }

    internal readonly struct ResolveFieldPlan
    {
        public readonly FieldInfo Field;
        public readonly ComponentSource Source;
        public readonly bool Optional;

        public ResolveFieldPlan(FieldInfo field, ComponentSource source, bool optional)
        {
            Field = field;
            Source = source;
            Optional = optional;
        }
    }

    /// <summary>
    /// Everything the container needs to construct and inject one type, computed once by reflection
    /// and then reused: the constructor and its parameters, [Inject] fields and methods (with their
    /// parameter arrays) and [Resolve] fields. Immutable after construction.
    /// </summary>
    internal sealed class TypePlan
    {
        public readonly Type Type;
        public readonly FieldInfo[] InjectFields;
        public readonly InjectMethodPlan[] InjectMethods;
        public readonly ResolveFieldPlan[] ResolveFields;
        public readonly bool NeedsContainerInjection;
        public readonly bool NeedsComponentResolution;
        public readonly bool NeedsAnyInjection;

        // Constructor selection is lazy and published as one immutable object: most injection targets
        // (MonoBehaviours) are never constructed by the container, and a type with an ambiguous
        // constructor must only fail when somebody actually tries to construct it.
        private sealed class ConstructorChoice
        {
            public readonly ConstructorInfo Constructor;
            public readonly ParameterInfo[] Parameters;
            public readonly string Error;

            public ConstructorChoice(ConstructorInfo constructor, ParameterInfo[] parameters, string error)
            {
                Constructor = constructor;
                Parameters = parameters;
                Error = error;
            }
        }

        private ConstructorChoice _choice;

        public TypePlan(Type type, FieldInfo[] injectFields, InjectMethodPlan[] injectMethods, ResolveFieldPlan[] resolveFields)
        {
            Type = type;
            InjectFields = injectFields;
            InjectMethods = injectMethods;
            ResolveFields = resolveFields;
            NeedsContainerInjection = injectFields.Length > 0 || injectMethods.Length > 0;
            NeedsComponentResolution = resolveFields.Length > 0;
            NeedsAnyInjection = NeedsContainerInjection || NeedsComponentResolution;
        }

        /// <summary>The constructor to call. Throws <see cref="InvalidOperationException"/> when none, or more than one, is eligible.</summary>
        public ConstructorInfo Constructor => Choice().Constructor;

        /// <summary>Parameters of <see cref="Constructor"/>; the array is shared, do not modify it.</summary>
        public ParameterInfo[] ConstructorParameters => Choice().Parameters;

        private ConstructorChoice Choice()
        {
            var choice = _choice ?? (_choice = SelectConstructor(Type));
            if (choice.Error != null) throw new InvalidOperationException(choice.Error);
            return choice;
        }

        private static ConstructorChoice SelectConstructor(Type type)
        {
            var all = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            ConstructorInfo marked = null, onlyPublic = null;
            int markedCount = 0, publicCount = 0;
            foreach (var ctor in all)
            {
                if (ctor.IsDefined(typeof(InjectAttribute), false)) { markedCount++; if (marked == null) marked = ctor; }
                if (ctor.IsPublic) { publicCount++; if (onlyPublic == null) onlyPublic = ctor; }
            }

            if (markedCount > 1)
                return Fail($"Type '{type.FullName}' has {markedCount} constructors marked [Inject]. Mark exactly one.");

            var chosen = markedCount == 1 ? marked : (publicCount == 1 ? onlyPublic : null);
            if (chosen != null)
                return new ConstructorChoice(chosen, chosen.GetParameters(), null);

            return Fail(publicCount > 1
                ? $"Type '{type.FullName}' has {publicCount} public constructors. Annotate the intended one with [Inject] to remove ambiguity."
                : $"Type '{type.FullName}' has no accessible constructors. Add a public constructor or annotate one with [Inject].");
        }

        private static ConstructorChoice Fail(string message) => new ConstructorChoice(null, null, message);
    }

    /// <summary>
    /// Lazily built, per-type injection plans. Nothing is scanned at startup: a type is inspected the
    /// first time it is constructed or injected, and its hierarchy walk stops at engine and framework
    /// base types (they cannot carry our attributes).
    ///
    /// Threading: plans are cached in a regular dictionary. Like the container, metadata queries
    /// and cache resets must run on the Unity main thread.
    /// </summary>
    internal static class InjectionMetadata
    {
        private const BindingFlags DeclaredMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        private const BindingFlags StaticDeclaredMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static readonly Dictionary<Type, TypePlan> s_plans = new Dictionary<Type, TypePlan>();

        public static TypePlan GetPlan(Type type)
        {
            if (s_plans.TryGetValue(type, out var plan)) return plan;

            plan = Build(type);
            s_plans.Add(type, plan);
            return plan;
        }

        // Domain reload may be disabled ("Enter Play Mode Options"): start every play session clean.
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => ClearCache();

        /// <summary>Clears every cached plan and the container's static reflection caches.</summary>
        public static void ClearCache()
        {
            MainThread.Assert(nameof(ClearCache));
            s_plans.Clear();
            DiContainer.ClearStaticCaches();
        }

        private static TypePlan Build(Type type)
        {
            var hierarchy = new List<Type>(4);
            for (var t = type; t != null && !IsEngineOrFrameworkType(t); t = t.BaseType)
                hierarchy.Add(t);
            hierarchy.Reverse(); // base classes first

            List<FieldInfo> injectFields = null;
            List<InjectMethodPlan> injectMethods = null;
            List<ResolveFieldPlan> resolveFields = null;
            HashSet<MethodInfo> seenDefinitions = null;

            for (int h = 0; h < hierarchy.Count; h++)
            {
                var current = hierarchy[h];
                RejectUnsupportedMembers(current);

                var fields = current.GetFields(DeclaredMembers);
                SortMembers(fields);
                for (int i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    if (field.IsDefined(typeof(InjectAttribute), false))
                        (injectFields ?? (injectFields = new List<FieldInfo>())).Add(field);

                    if (field.IsDefined(typeof(ResolveAttribute), false) && TryPlanResolveField(field, type, out var resolvePlan))
                        (resolveFields ?? (resolveFields = new List<ResolveFieldPlan>())).Add(resolvePlan);
                }

                var methods = current.GetMethods(DeclaredMembers);
                SortMembers(methods);
                for (int i = 0; i < methods.Length; i++)
                {
                    var method = methods[i];
                    if (!method.IsDefined(typeof(InjectAttribute), false)) continue;
                    if (method.IsGenericMethodDefinition)
                        throw new InvalidOperationException(
                            $"[DiContainer] [Inject] method '{method.Name}' on '{current.FullName}' is generic. " +
                            "Generic [Inject] methods are not supported; use a non-generic method, or inject the " +
                            "dependency through a field or constructor.");

                    // A virtual method and its overrides share one base definition. Invoking the
                    // base MethodInfo already dispatches to the override, so run the body once.
                    if (!(seenDefinitions ?? (seenDefinitions = new HashSet<MethodInfo>())).Add(method.GetBaseDefinition()))
                        continue;

                    (injectMethods ?? (injectMethods = new List<InjectMethodPlan>())).Add(
                        new InjectMethodPlan(method, method.GetParameters()));
                }
            }

            return new TypePlan(
                type,
                injectFields != null ? injectFields.ToArray() : Array.Empty<FieldInfo>(),
                injectMethods != null ? injectMethods.ToArray() : Array.Empty<InjectMethodPlan>(),
                resolveFields != null ? resolveFields.ToArray() : Array.Empty<ResolveFieldPlan>());
        }

        /// <summary>
        /// Reflection returns members in no guaranteed order, so the plan fixes it to declaration
        /// order (metadata token, then name) to make field and method injection repeatable.
        /// </summary>
        private static void SortMembers<T>(T[] members) where T : MemberInfo
            => Array.Sort(members, CompareMembers);

        private static int CompareMembers(MemberInfo left, MemberInfo right)
        {
            int token = MetadataTokenOf(left).CompareTo(MetadataTokenOf(right));
            return token != 0 ? token : string.CompareOrdinal(left.Name, right.Name);
        }

        private static int MetadataTokenOf(MemberInfo member)
        {
            try { return member.MetadataToken; }
            catch (InvalidOperationException) { return 0; } // metadata emitted dynamically; fall back to the name
        }

        /// <summary>
        /// Static and generic injection members cannot work: the version with BindingFlags.Instance
        /// silently skipped them, which hid a broken configuration until much later. Fail when the
        /// plan is built instead.
        /// </summary>
        private static void RejectUnsupportedMembers(Type current)
        {
            var fields = current.GetFields(StaticDeclaredMembers);
            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (field.IsDefined(typeof(InjectAttribute), false))
                    throw InvalidMember(current, "[Inject] field", field.Name,
                        "is static. Static fields cannot be injected; remove [Inject] or make the field an instance member.");
                if (field.IsDefined(typeof(ResolveAttribute), false))
                    throw InvalidMember(current, "[Resolve] field", field.Name,
                        "is static. Static fields cannot be resolved from a GameObject hierarchy; remove [Resolve] or make the field an instance member.");
            }

            var methods = current.GetMethods(StaticDeclaredMembers);
            for (int i = 0; i < methods.Length; i++)
                if (methods[i].IsDefined(typeof(InjectAttribute), false))
                    throw InvalidMember(current, "[Inject] method", methods[i].Name,
                        "is static. Static methods cannot be injected; remove [Inject] or make the method an instance member.");

            var constructors = current.GetConstructors(StaticDeclaredMembers);
            for (int i = 0; i < constructors.Length; i++)
                if (constructors[i].IsDefined(typeof(InjectAttribute), false))
                    throw InvalidMember(current, "[Inject] static constructor", "cctor",
                        "cannot be injected; [Inject] constructors must be instance constructors.");
        }

        private static InvalidOperationException InvalidMember(Type owner, string kind, string member, string reason)
            => new InvalidOperationException($"[DiContainer] {kind} '{member}' on '{owner.FullName}' {reason}");

        private static bool TryPlanResolveField(FieldInfo field, Type owner, out ResolveFieldPlan plan)
        {
            var fieldType = field.FieldType;
            if (!typeof(Component).IsAssignableFrom(fieldType) && !fieldType.IsInterface)
            {
                // Reported once per type, when the plan is built, instead of on every injection.
                Debug.LogWarning(
                    $"[ComponentResolver] [Resolve] '{field.Name}' on '{owner.Name}' - " +
                    $"'{fieldType.Name}' is not a Unity Component or an interface. Field skipped.");
                plan = default;
                return false;
            }

            var attribute = (ResolveAttribute)Attribute.GetCustomAttribute(field, typeof(ResolveAttribute), false);
            plan = new ResolveFieldPlan(field, attribute.Source, attribute.Optional);
            return true;
        }

        private static bool IsEngineOrFrameworkType(Type type)
        {
            if (type == typeof(object)) return true;
            var ns = type.Namespace;
            if (ns == null) return false;
            return ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)
                || ns == "UnityEngine" || ns.StartsWith("UnityEngine.", StringComparison.Ordinal);
        }
    }
}
