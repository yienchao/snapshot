namespace ViewTracker.Models
{
    /// <summary>
    /// Status of a room when comparing current model against a snapshot
    /// </summary>
    public enum RoomStatus
    {
        /// <summary>
        /// Room exists in both current model and snapshot, and has parameter changes
        /// </summary>
        Modified,

        /// <summary>
        /// Room exists in both current model and snapshot, but has no changes
        /// </summary>
        Unchanged,

        /// <summary>
        /// Room exists in current model but not in snapshot (newly created)
        /// </summary>
        New,

        /// <summary>
        /// Room exists in snapshot but not in current model (deleted)
        /// </summary>
        Deleted,

        /// <summary>
        /// Room exists in both, but is currently unplaced (was placed in snapshot)
        /// </summary>
        Unplaced
    }
}
