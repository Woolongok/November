using System.Runtime.CompilerServices;

namespace November.DI
{
    /// <summary>
    /// Records who is allowed to dispose an object. The table is process-wide and keyed by
    /// reference identity, so forwarding a singleton through a child factory, registering the same
    /// object twice, or registering an externally created object cannot make two scopes believe
    /// they own it.
    ///
    /// The keys are weak: the table never keeps a service alive by itself. Values hold the owning
    /// container so a cached borrowed reference can notice that its real owner has been disposed.
    /// As with the container itself, this state is only touched from the Unity main thread.
    /// </summary>
    internal sealed class OwnershipRecord
    {
        /// <summary>The container that disposes the object; null for caller-owned instances.</summary>
        public readonly DiContainer Owner;

        public OwnershipRecord(DiContainer owner) => Owner = owner;

        public bool IsExternal => Owner == null;
    }

    internal static class ObjectOwnership
    {
        private static readonly ConditionalWeakTable<object, OwnershipRecord> s_records =
            new ConditionalWeakTable<object, OwnershipRecord>();

        /// <summary>Marks an object as caller-owned. RegisterInstance and ReplaceInstance use this.</summary>
        public static void MarkExternal(object instance)
        {
            if (instance == null) return;

            // Replace any previous claim: registering an instance explicitly transfers ownership
            // back to the caller.
            s_records.Remove(instance);
            s_records.Add(instance, new OwnershipRecord(null));
        }

        /// <summary>
        /// Claims an object for <paramref name="owner"/> when it has no owner yet. Returns false when
        /// another container owns it, when it is caller-owned, or when this container already owns it.
        /// </summary>
        public static bool TryClaim(object instance, DiContainer owner, out OwnershipRecord record)
        {
            if (s_records.TryGetValue(instance, out record)) return false;

            record = new OwnershipRecord(owner);
            s_records.Add(instance, record);
            return true;
        }

        public static bool TryGet(object instance, out OwnershipRecord record)
            => s_records.TryGetValue(instance, out record);

        public static bool IsExternallyOwned(object instance)
            => s_records.TryGetValue(instance, out var record) && record.IsExternal;

        /// <summary>
        /// True when the object is owned by someone else (external or another container). Such an
        /// object must not be disposed or cleaned up by <paramref name="container"/>.
        /// </summary>
        public static bool IsOwnedByAnother(DiContainer container, object instance)
            => s_records.TryGetValue(instance, out var record)
               && (record.IsExternal || !ReferenceEquals(record.Owner, container));

        /// <summary>True when the cached object's real owner has already been disposed.</summary>
        public static bool IsOwnedByDisposedContainer(object instance)
            => s_records.TryGetValue(instance, out var record)
               && !ReferenceEquals(record.Owner, null)
               && record.Owner.IsDisposed;

        /// <summary>The container about to dispose an object may do so only if it still owns it.</summary>
        public static bool ShouldDispose(DiContainer container, object instance)
            => !s_records.TryGetValue(instance, out var record) || ReferenceEquals(record.Owner, container);
    }
}
