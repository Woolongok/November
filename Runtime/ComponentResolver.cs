using System;
using UnityEngine;

namespace November.DI
{
    /// <summary>
    /// Fills [Resolve]-annotated fields on Unity Components from the GameObject hierarchy.
    ///
    /// [Resolve] = "find this component in my hierarchy" (see <see cref="ResolveAttribute"/>),
    /// [Inject]  = "get this service from the DI container". Different concerns, kept separate.
    ///
    /// Raw Debug logging is intentional: this runs inside container injection, before any logging
    /// service may be registered, and routing through one would recurse into the thing being built.
    /// </summary>
    public static class ComponentResolver
    {
        /// <summary>Populates the [Resolve] fields of <paramref name="component"/> (see <see cref="ResolveAttribute"/>).</summary>
        public static void Resolve(Component component)
        {
            MainThread.Assert(nameof(ComponentResolver.Resolve));
            if (component == null)
                throw new ArgumentNullException(nameof(component), "[ComponentResolver] Resolve(null) — caller bug.");

            var type = component.GetType();
            var fields = InjectionMetadata.GetPlan(type).ResolveFields;
            for (int i = 0; i < fields.Length; i++)
                ResolveField(component, type, fields[i]);
        }

        /// <summary>Extension form for use inside Awake() or Start().</summary>
        public static void ResolveComponents(this Component component) => Resolve(component);

        private static void ResolveField(Component component, Type type, in ResolveFieldPlan plan)
        {
            var field = plan.Field;
            var found = Find(component, field.FieldType, plan.Source);

            if (found == null)
            {
                if (plan.Optional) return; // may legitimately be absent: no warning, keep whatever is there

                // Fail LOUD: clear a pre-assigned value so a missing component cannot hide behind a
                // stale Inspector reference. Warning, not Error: Error-level logs without a
                // LogAssert.Expect fail EditMode runs that intentionally probe partial hierarchies.
                try { field.SetValue(component, null); }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[ComponentResolver] [Resolve] '{field.Name}' on '{type.Name}' - " +
                        $"failed to clear stale value: {ex.GetType().Name}.",
                        component);
                }

                Debug.LogWarning(
                    $"[ComponentResolver] [Resolve] '{field.Name}' on '{type.Name}' - " +
                    $"no '{field.FieldType.Name}' found on '{component.gameObject.name}'" +
                    $"{Describe(plan.Source)}. Field cleared to null.",
                    component);
                return;
            }

            try { field.SetValue(component, found); }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[ComponentResolver] [Resolve] '{field.Name}' on '{type.Name}' - " +
                    $"failed to assign '{field.FieldType.Name}': {ex.GetType().Name}.",
                    component);
            }
        }

        // Inactive objects are included for Children/Parent: prefabs are injected while still inactive.
        private static Component Find(Component component, Type type, ComponentSource source)
        {
            switch (source)
            {
                case ComponentSource.Children: return component.GetComponentInChildren(type, true);
                case ComponentSource.Parent: return component.GetComponentInParent(type, true);
                default: return component.GetComponent(type);
            }
        }

        private static string Describe(ComponentSource source)
        {
            switch (source)
            {
                case ComponentSource.Children: return " or its children";
                case ComponentSource.Parent: return " or its parents";
                default: return string.Empty;
            }
        }
    }
}
