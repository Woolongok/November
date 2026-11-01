using System;
using UnityEngine;

namespace November.DI
{

    /// <summary>
    /// Resolves [Resolve]-annotated fields on Unity Components via GetComponent.
    ///
    /// SOLID: Single Responsibility — this class ONLY handles GetComponent-based
    /// field resolution, which is a fundamentally different concern from DI
    /// container injection.
    /// [Resolve] = "find this component on my GameObject"
    /// [Inject]  = "resolve this service from the DI container"
    ///
    /// Efficiency: uses InjectionMetadata for pre-computed, cached reflection data.
    ///
    /// NOTE: raw Debug logging is intentional here (same as DiContainer): this
    /// runs inside container injection, before ILogService itself may be bound —
    /// routing through ILogService would recurse into the thing being built.
    /// </summary>
    public static class ComponentResolver
    {
        /// <summary>
        /// Populates fields marked with [Resolve] on the given component.
        /// Uses GetComponent() internally.
        /// </summary>
        public static void Resolve(Component component)
        {
            if (component == null) throw new ArgumentNullException(nameof(component), "[ComponentResolver] Resolve(null) — caller bug.");

            var type = component.GetType();

            if (!InjectionMetadata.TypeNeedsComponentResolution(type))
                return;

            foreach (var field in InjectionMetadata.GetResolveFields(type))
            {
                if (!typeof(Component).IsAssignableFrom(field.FieldType) && !field.FieldType.IsInterface)
                {
                    Debug.LogWarning(
                        $"[ComponentResolver] [Resolve] '{field.Name}' on '{type.Name}' - " +
                        $"'{field.FieldType.Name}' is not a Unity Component. Field skipped.");
                    continue;
                }

                var resolved = component.GetComponent(field.FieldType);
                if (resolved == null)
                {
                    // Fail LOUD: clear the stale Inspector value so a missing
                    // component can't hide behind a pre-assigned reference.
                    // (Warning, not Error: Error-level logs without a
                    // LogAssert.Expect fail EditMode runs that intentionally
                    // probe partial hierarchies.)
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
                        $"no '{field.FieldType.Name}' found on '{component.gameObject.name}'. Field cleared to null.",
                        component);
                    continue;
                }

                try { field.SetValue(component, resolved); }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[ComponentResolver] [Resolve] '{field.Name}' on '{type.Name}' - " +
                        $"failed to assign '{field.FieldType.Name}': {ex.GetType().Name}.",
                        component);
                }
            }
        }

        /// <summary>
        /// Extension method to easily resolve components inside Awake() or Start().
        /// </summary>
        public static void ResolveComponents(this Component component)
        {
            Resolve(component);
        }
    }

}
