namespace ViewTracker.Models
{
    /// <summary>
    /// Represents a change in a single parameter between current model and snapshot
    /// </summary>
    public class ParameterChange
    {
        /// <summary>
        /// Display name of the parameter
        /// </summary>
        public string ParameterName { get; set; }

        /// <summary>
        /// Current value in the model (formatted for display)
        /// </summary>
        public string CurrentValue { get; set; }

        /// <summary>
        /// Value in the snapshot (formatted for display)
        /// </summary>
        public string SnapshotValue { get; set; }

        /// <summary>
        /// Current parameter value object (for comparison logic)
        /// </summary>
        public ParameterValue CurrentParamValue { get; set; }

        /// <summary>
        /// Snapshot parameter value object (for comparison logic)
        /// </summary>
        public ParameterValue SnapshotParamValue { get; set; }

        /// <summary>
        /// Whether this parameter is read-only (Area, Perimeter, Volume, etc.)
        /// Show in UI but don't restore
        /// </summary>
        public bool IsReadOnly { get; set; }

        /// <summary>
        /// Formatted string for display: "ParameterName: CurrentValue → SnapshotValue"
        /// </summary>
        public string DisplayText => $"{ParameterName}: {CurrentValue} → {SnapshotValue}";

        /// <summary>
        /// Display text with read-only suffix if applicable
        /// </summary>
        public string DisplayTextWithReadOnly => IsReadOnly ? $"{DisplayText} (read-only)" : DisplayText;
    }
}
