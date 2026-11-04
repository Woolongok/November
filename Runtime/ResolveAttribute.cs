using System;

namespace November.DI
{
    /// <summary>Where <see cref="ResolveAttribute"/> looks for the component.</summary>
    public enum ComponentSource
    {
        /// <summary>The same GameObject (<c>GetComponent</c>).</summary>
        Self,
        /// <summary>This GameObject or any child, inactive ones included (<c>GetComponentInChildren</c>).</summary>
        Children,
        /// <summary>This GameObject or any parent, inactive ones included (<c>GetComponentInParent</c>).</summary>
        Parent
    }

    /// <summary>
    /// Fills a Component- or interface-typed field from the GameObject hierarchy when the
    /// object is injected (<c>[Resolve]</c> = "find it in the hierarchy", <c>[Inject]</c> =
    /// "get it from the container").
    ///
    /// By default a required miss clears the field to null (so a stale Inspector reference
    /// cannot hide a missing component) and logs a warning.
    /// Use <see cref="Optional"/> for components that may legitimately be absent: a miss is then
    /// silent and the field is left untouched.
    ///
    /// Fields whose type is neither a Component nor an interface are reported once, when the
    /// type is first inspected, and skipped.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class ResolveAttribute : Attribute
    {
        /// <summary>Where to search. Default: <see cref="ComponentSource.Self"/>.</summary>
        public ComponentSource Source { get; set; } = ComponentSource.Self;

        /// <summary>When true a miss neither warns nor clears the field.</summary>
        public bool Optional { get; set; }
    }
}
