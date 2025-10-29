using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System;
using System.Collections.Generic;
using System.Linq;
using ViewTracker.Models;
using ParameterValue = ViewTracker.Models.ParameterValue;

namespace ViewTracker.Services
{
    /// <summary>
    /// Service for comparing rooms between current model and snapshots
    /// Uses solid ParameterValue.IsEqualTo() logic
    /// </summary>
    public class RoomComparisonService
    {
        private readonly Document _doc;

        // Read-only parameter names that should be shown but not restored
        private static readonly HashSet<string> ReadOnlyParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Area", "Perimeter", "Volume", "Unbounded Height",
            "Surface", "Périmètre", "Hauteur non liée"
        };

        public RoomComparisonService(Document doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        }

        /// <summary>
        /// Compare rooms in current model against snapshot
        /// Returns list of comparison items (excludes Unchanged rooms)
        /// </summary>
        public List<RoomComparisonItem> CompareRoomsAgainstSnapshot(
            List<Room> currentRooms,
            List<RoomSnapshot> snapshotRooms)
        {
            var results = new List<RoomComparisonItem>();

            // Build dictionaries for fast lookup (handle duplicates by using first occurrence)
            var currentRoomsDict = currentRooms
                .Where(r => r.LookupParameter("trackID") != null &&
                           !string.IsNullOrWhiteSpace(r.LookupParameter("trackID").AsString()))
                .GroupBy(r => r.LookupParameter("trackID").AsString().Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var snapshotDict = snapshotRooms
                .Where(s => !string.IsNullOrWhiteSpace(s.TrackId))
                .GroupBy(s => s.TrackId.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var processedTrackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Process rooms that exist in BOTH current and snapshot
            foreach (var kvp in currentRoomsDict)
            {
                string trackId = kvp.Key;
                var currentRoom = kvp.Value;

                if (snapshotDict.TryGetValue(trackId, out var snapshot))
                {
                    processedTrackIds.Add(trackId);

                    // Compare parameters
                    var changedParams = CompareParameters(currentRoom, snapshot);

                    // Determine status
                    bool isPlacedNow = currentRoom.Location != null;
                    bool wasPlacedInSnapshot = snapshot.PositionX.HasValue && snapshot.PositionY.HasValue;

                    RoomStatus status;
                    if (!isPlacedNow && wasPlacedInSnapshot)
                    {
                        // Room is unplaced but was placed in snapshot
                        status = RoomStatus.Unplaced;
                    }
                    else if (changedParams.Any())
                    {
                        // Room has changes
                        status = RoomStatus.Modified;
                    }
                    else
                    {
                        // No changes
                        status = RoomStatus.Unchanged;
                    }

                    // Skip unchanged rooms (don't show in comparison)
                    if (status == RoomStatus.Unchanged)
                        continue;

                    // Create comparison item
                    var item = new RoomComparisonItem
                    {
                        CurrentRoom = currentRoom,
                        Snapshot = snapshot,
                        TrackId = trackId,
                        RoomNumber = currentRoom.Number,
                        RoomName = currentRoom.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "",
                        LevelName = currentRoom.Level?.Name ?? "",
                        AreaDisplay = FormatArea(currentRoom.Area),
                        Status = status,
                        IsPlacedNow = isPlacedNow,
                        WasPlacedInSnapshot = wasPlacedInSnapshot,
                        SnapshotLocation = wasPlacedInSnapshot
                            ? new XYZ(snapshot.PositionX.Value, snapshot.PositionY.Value, snapshot.PositionZ ?? 0)
                            : null,
                        IsSelected = true, // Default: select all rooms with changes
                        RestorePlacement = !isPlacedNow && wasPlacedInSnapshot // Default: checked for unplaced rooms
                    };

                    foreach (var param in changedParams)
                    {
                        item.ChangedParameters.Add(param);
                    }

                    results.Add(item);
                }
                else
                {
                    // Room exists in current but NOT in snapshot = NEW
                    processedTrackIds.Add(trackId);

                    var item = new RoomComparisonItem
                    {
                        CurrentRoom = currentRoom,
                        Snapshot = null,
                        TrackId = trackId,
                        RoomNumber = currentRoom.Number,
                        RoomName = currentRoom.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "",
                        LevelName = currentRoom.Level?.Name ?? "",
                        AreaDisplay = FormatArea(currentRoom.Area),
                        Status = RoomStatus.New,
                        IsPlacedNow = currentRoom.Location != null,
                        WasPlacedInSnapshot = false,
                        IsSelected = false, // Default: don't select new rooms
                        RestorePlacement = false
                    };

                    results.Add(item);
                }
            }

            // Process rooms that exist in snapshot but NOT in current = DELETED
            foreach (var kvp in snapshotDict)
            {
                string trackId = kvp.Key;
                var snapshot = kvp.Value;

                if (processedTrackIds.Contains(trackId))
                    continue; // Already processed

                bool wasPlaced = snapshot.PositionX.HasValue && snapshot.PositionY.HasValue;

                // REFACTORED: Get RoomName from AllParameters JSON
                string roomName = "";
                if (snapshot.AllParameters != null)
                {
                    foreach (var key in new[] { "Nom", "Name", "Nombre" })
                    {
                        if (snapshot.AllParameters.TryGetValue(key, out object nameValue))
                        {
                            var paramVal = ParameterValue.FromJsonObject(nameValue);
                            roomName = paramVal?.DisplayValue ?? "";
                            break;
                        }
                    }
                }

                var item = new RoomComparisonItem
                {
                    CurrentRoom = null,
                    Snapshot = snapshot,
                    TrackId = trackId,
                    RoomNumber = snapshot.RoomNumber ?? "",
                    RoomName = roomName,
                    LevelName = snapshot.Level ?? "",
                    AreaDisplay = FormatArea(snapshot.Area),
                    Status = RoomStatus.Deleted,
                    IsPlacedNow = false,
                    WasPlacedInSnapshot = wasPlaced,
                    SnapshotLocation = wasPlaced
                        ? new XYZ(snapshot.PositionX.Value, snapshot.PositionY.Value, snapshot.PositionZ ?? 0)
                        : null,
                    IsSelected = true, // Default: select deleted rooms for recreation
                    RestorePlacement = false // Default: create as unplaced (user can opt-in)
                };

                results.Add(item);
            }

            return results;
        }

        /// <summary>
        /// Compare parameters between current room and snapshot
        /// Returns list of changed parameters (SOLID LOGIC using ParameterValue.IsEqualTo)
        /// </summary>
        private List<ParameterChange> CompareParameters(Room currentRoom, RoomSnapshot snapshot)
        {
            var changes = new List<ParameterChange>();

            // List of built-in parameters to compare
            // REFACTORED: Built-in parameters are now in AllParameters JSON
            // The AllParameters comparison loop below handles all of them

            // Compare Room Number separately (stored directly as string, not BuiltInParameter)
            var currentRoomNumber = currentRoom.Number ?? "";
            var snapshotRoomNumber = snapshot.RoomNumber ?? "";
            if (!string.Equals(currentRoomNumber, snapshotRoomNumber, StringComparison.Ordinal))
            {
                changes.Add(new ParameterChange
                {
                    ParameterName = "Room Number",
                    CurrentValue = currentRoomNumber,
                    SnapshotValue = snapshotRoomNumber,
                    CurrentParamValue = new ParameterValue { StorageType = "String", RawValue = currentRoomNumber, DisplayValue = currentRoomNumber },
                    SnapshotParamValue = new ParameterValue { StorageType = "String", RawValue = snapshotRoomNumber, DisplayValue = snapshotRoomNumber },
                    IsReadOnly = false
                });
            }

            // REFACTORED: Occupant is now in AllParameters JSON, no need for special handling here

            // Compare AllParameters (custom parameters stored in JSON)
            if (snapshot.AllParameters != null)
            {
                foreach (var kvp in snapshot.AllParameters)
                {
                    string paramName = kvp.Key;
                    var currentParam = currentRoom.LookupParameter(paramName);

                    if (currentParam == null)
                        continue;

                    var currentValue = ParameterValue.FromRevitParameter(currentParam);
                    var snapshotValue = ParameterValue.FromJsonObject(kvp.Value);

                    if (snapshotValue == null)
                        continue;

                    // SOLID COMPARISON: Use ParameterValue.IsEqualTo()
                    bool hasChanged = !snapshotValue.IsEqualTo(currentValue);

                    if (hasChanged)
                    {
                        changes.Add(new ParameterChange
                        {
                            ParameterName = paramName,
                            CurrentValue = currentValue.DisplayValue ?? "",
                            SnapshotValue = snapshotValue.DisplayValue ?? "",
                            CurrentParamValue = currentValue,
                            SnapshotParamValue = snapshotValue,
                            IsReadOnly = ReadOnlyParameters.Contains(paramName)
                        });
                    }
                }
            }

            return changes;
        }

        // REFACTORED: GetSnapshotParameterValue method removed - no longer needed
        // All parameters are now in AllParameters JSON and handled by the comparison loop above

        /// <summary>
        /// Format area for display
        /// </summary>
        private string FormatArea(double? area)
        {
            if (!area.HasValue)
                return "";
            return $"{area.Value:F2} m²";
        }
    }
}
