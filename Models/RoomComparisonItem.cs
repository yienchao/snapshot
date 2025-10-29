using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ViewTracker.Models
{
    /// <summary>
    /// Represents a room comparison between current model and snapshot
    /// Used in the unified Compare & Restore window
    /// </summary>
    public class RoomComparisonItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _restorePlacement;

        /// <summary>
        /// The room in the current model (null if deleted)
        /// </summary>
        public Room CurrentRoom { get; set; }

        /// <summary>
        /// The snapshot data for this room
        /// </summary>
        public RoomSnapshot Snapshot { get; set; }

        /// <summary>
        /// Track ID (primary key for matching)
        /// </summary>
        public string TrackId { get; set; }

        /// <summary>
        /// Room number for display
        /// </summary>
        public string RoomNumber { get; set; }

        /// <summary>
        /// Room name for display
        /// </summary>
        public string RoomName { get; set; }

        /// <summary>
        /// Level name for display
        /// </summary>
        public string LevelName { get; set; }

        /// <summary>
        /// Area for display
        /// </summary>
        public string AreaDisplay { get; set; }

        /// <summary>
        /// Status of this room (Modified, New, Deleted, Unplaced, Unchanged)
        /// </summary>
        public RoomStatus Status { get; set; }

        /// <summary>
        /// List of changed parameters
        /// </summary>
        public ObservableCollection<ParameterChange> ChangedParameters { get; set; } = new ObservableCollection<ParameterChange>();

        /// <summary>
        /// Whether this room is selected for restoration
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        /// <summary>
        /// Whether to restore placement for this room (only relevant for Deleted/Unplaced)
        /// </summary>
        public bool RestorePlacement
        {
            get => _restorePlacement;
            set
            {
                if (_restorePlacement != value)
                {
                    _restorePlacement = value;
                    OnPropertyChanged(nameof(RestorePlacement));
                }
            }
        }

        /// <summary>
        /// Whether the room was placed in the snapshot
        /// </summary>
        public bool WasPlacedInSnapshot { get; set; }

        /// <summary>
        /// Whether the room is currently placed in the model
        /// </summary>
        public bool IsPlacedNow { get; set; }

        /// <summary>
        /// Snapshot location (if room was placed)
        /// </summary>
        public XYZ SnapshotLocation { get; set; }

        /// <summary>
        /// Display text for placement location
        /// </summary>
        public string PlacementLocationDisplay
        {
            get
            {
                if (SnapshotLocation != null)
                {
                    return $"({SnapshotLocation.X:F2}, {SnapshotLocation.Y:F2})";
                }
                return "";
            }
        }

        /// <summary>
        /// Status text for display (e.g., "Modified (3 parameters)", "Deleted", "Unplaced")
        /// </summary>
        public string StatusDisplay
        {
            get
            {
                switch (Status)
                {
                    case RoomStatus.Modified:
                        return $"Modified ({ChangedParameters.Count} parameter{(ChangedParameters.Count != 1 ? "s" : "")})";
                    case RoomStatus.Deleted:
                        return "Deleted (not in current model)";
                    case RoomStatus.Unplaced:
                        return $"Unplaced ({ChangedParameters.Count} parameter{(ChangedParameters.Count != 1 ? "s" : "")} changed)";
                    case RoomStatus.New:
                        return "New (not in snapshot)";
                    case RoomStatus.Unchanged:
                        return "Unchanged";
                    default:
                        return Status.ToString();
                }
            }
        }

        /// <summary>
        /// Display name for the room list
        /// </summary>
        public string DisplayName => $"{RoomNumber} - {RoomName}";

        /// <summary>
        /// Full display info with level and area
        /// </summary>
        public string FullDisplayInfo
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(LevelName))
                    parts.Add($"Level: {LevelName}");
                if (!string.IsNullOrEmpty(AreaDisplay))
                    parts.Add($"Area: {AreaDisplay}");
                return parts.Count > 0 ? string.Join(" | ", parts) : "";
            }
        }

        /// <summary>
        /// Text for placement restore checkbox
        /// </summary>
        public string PlacementCheckboxText
        {
            get
            {
                if (Status == RoomStatus.Deleted)
                    return $"Try to place at original location {PlacementLocationDisplay}";
                else if (Status == RoomStatus.Unplaced)
                    return $"Restore placement at {PlacementLocationDisplay}";
                return "";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
