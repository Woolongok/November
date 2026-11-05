using System;
using UnityEngine.Scripting;

namespace November.DI
{

    /// <summary>
    /// Marks a constructor, field, or method for dependency injection.
    ///
    /// ═══ Contract ═══════════════════════════════════════════════════════════
    /// [Inject] fields are ALWAYS required. If the dependency cannot be resolved,
    /// injection fails and the object construction is aborted — no [Inject] field
    /// is optional or best-effort. A null [Inject] field would cause a
    /// NullReferenceException later, far from the actual misconfiguration. Fail
    /// fast, fail at construction time.
    ///
    /// There is no "optional injection" pattern. If a dependency may legitimately
    /// be absent, use a separate configuration path (null-object pattern, separate
    /// prefab variant, or manual assignment in Awake/Start).
    /// ════════════════════════════════════════════════════════════════════════
    ///
    /// The attribute derives from <see cref="PreserveAttribute"/>, so managed code stripping
    /// (IL2CPP, Medium / High stripping level) keeps every constructor, field and method that
    /// carries it, together with its declaring type. The container reaches those members only
    /// through reflection, which the linker cannot see.
    /// </summary>
    [AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Field | AttributeTargets.Method)]
    public sealed class InjectAttribute : PreserveAttribute { }
}
