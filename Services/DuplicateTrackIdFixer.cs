using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ViewTracker.Services
{
    /// <summary>
    /// Handles detection and fixing of duplicate trackIDs using smart matching logic
    /// </summary>
    public class DuplicateTrackIdFixer
    {
        private readonly SupabaseService _supabaseService;
        private readonly Guid _projectId;

        public DuplicateTrackIdFixer(SupabaseService supabaseService, Guid projectId)
        {
            _supabaseService = supabaseService;
            _projectId = projectId;
        }

        /// <summary>
        /// Detects duplicate trackIDs and suggests which to keep and which to regenerate
        /// </summary>
        public async Task<List<DuplicateTrackIdGroup>> DetectDuplicatesAsync(List<Room> rooms)
        {
            var duplicateGroups = new List<DuplicateTrackIdGroup>();

            // Group rooms by trackID
            var grouped = rooms
                .GroupBy(r => r.LookupParameter("trackID")?.AsString())
                .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1);

            foreach (var group in grouped)
            {
                var trackId = group.Key;
                var duplicateGroup = new DuplicateTrackIdGroup
                {
                    TrackId = trackId,
                    Rooms = new List<DuplicateTrackIdRoom>()
                };

                // Get latest snapshot from database for this trackID
                RoomSnapshot latestSnapshot = null;
                try
                {
                    await _supabaseService.InitializeAsync();
                    latestSnapshot = await _supabaseService.GetLatestRoomSnapshotByTrackIdAsync(trackId, _projectId);
                }
                catch
                {
                    // No snapshot found or database error - will use fallback logic
                }

                Room originalRoom = null;
                string matchReason = "";

                if (latestSnapshot != null)
                {
                    // Try to match by room number first (most reliable)
                    originalRoom = group.FirstOrDefault(r =>
                        !string.IsNullOrWhiteSpace(r.Number) &&
                        r.Number.Trim().Equals(latestSnapshot.RoomNumber?.Trim(), StringComparison.OrdinalIgnoreCase));

                    if (originalRoom != null)
                    {
                        matchReason = "Matches snapshot room number";
                    }
                    else
                    {
                        // REFACTORED: Try to match by room name from AllParameters JSON
                        string snapshotRoomName = "";
                        if (latestSnapshot.AllParameters != null)
                        {
                            foreach (var key in new[] { "Nom", "Name", "Nombre" })
                            {
                                if (latestSnapshot.AllParameters.TryGetValue(key, out object nameValue))
                                {
                                    var paramVal = ViewTracker.Models.ParameterValue.FromJsonObject(nameValue);
                                    snapshotRoomName = paramVal?.DisplayValue ?? "";
                                    break;
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(snapshotRoomName))
                        {
                            originalRoom = group.FirstOrDefault(r =>
                                !string.IsNullOrWhiteSpace(r.LookupParameter("Name")?.AsString()) &&
                                r.LookupParameter("Name").AsString().Trim().Equals(snapshotRoomName.Trim(), StringComparison.OrdinalIgnoreCase));

                            if (originalRoom != null)
                            {
                                matchReason = "Matches snapshot room name";
                            }
                        }
                    }
                }

                // Fallback: use lowest ElementId (oldest room in model)
                if (originalRoom == null)
                {
                    originalRoom = group.OrderBy(r => r.Id.Value).First();
                    matchReason = latestSnapshot != null
                        ? "No match found, kept lowest ID"
                        : "No snapshot history, kept lowest ID";
                }

                // Build the duplicate group with suggestions
                foreach (var room in group)
                {
                    bool isOriginal = room.Id == originalRoom.Id;

                    duplicateGroup.Rooms.Add(new DuplicateTrackIdRoom
                    {
                        Room = room,
                        RoomNumber = room.Number,
                        RoomName = room.LookupParameter("Name")?.AsString() ?? "",
                        Level = room.Level?.Name ?? "N/A",
                        ElementId = room.Id.Value,
                        IsOriginal = isOriginal,
                        MatchReason = isOriginal ? matchReason : "",
                        SuggestedAction = isOriginal ? "Keep" : "Regenerate"
                    });
                }

                duplicateGroups.Add(duplicateGroup);
            }

            return duplicateGroups;
        }

        /// <summary>
        /// Generates a new unique trackID for room type
        /// Finds the highest existing number and returns the next available sequential number
        /// </summary>
        public string GenerateNewTrackId(List<Room> allRooms)
        {
            var existingIds = allRooms
                .Select(r => r.LookupParameter("trackID")?.AsString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();

            // Extract numeric portions and find max
            int maxNumber = 0;
            foreach (var id in existingIds)
            {
                if (id.StartsWith("ROOM-", StringComparison.OrdinalIgnoreCase) && id.Length >= 9)
                {
                    string numericPart = id.Substring(5, 4);
                    if (int.TryParse(numericPart, out int num))
                    {
                        maxNumber = Math.Max(maxNumber, num);
                    }
                }
            }

            // Find next available number (in case of gaps)
            int nextNumber = maxNumber + 1;
            string newId = $"ROOM-{nextNumber:D4}";

            // Safety check: ensure it's truly unique
            var existingSet = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);
            while (existingSet.Contains(newId))
            {
                nextNumber++;
                newId = $"ROOM-{nextNumber:D4}";
            }

            return newId;
        }

        /// <summary>
        /// Generates a new unique trackID for door type
        /// Finds the highest existing number and returns the next available sequential number
        /// </summary>
        public string GenerateNewDoorTrackId(List<FamilyInstance> allDoors)
        {
            var existingIds = allDoors
                .Select(d => d.LookupParameter("trackID")?.AsString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();

            // Extract numeric portions and find max
            int maxNumber = 0;
            foreach (var id in existingIds)
            {
                if (id.StartsWith("DOOR-", StringComparison.OrdinalIgnoreCase) && id.Length >= 9)
                {
                    string numericPart = id.Substring(5, 4);
                    if (int.TryParse(numericPart, out int num))
                    {
                        maxNumber = Math.Max(maxNumber, num);
                    }
                }
            }

            // Find next available number (in case of gaps)
            int nextNumber = maxNumber + 1;
            string newId = $"DOOR-{nextNumber:D4}";

            // Safety check: ensure it's truly unique
            var existingSet = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);
            while (existingSet.Contains(newId))
            {
                nextNumber++;
                newId = $"DOOR-{nextNumber:D4}";
            }

            return newId;
        }

        /// <summary>
        /// Generates a new unique trackID for element type
        /// Finds the highest existing number and returns the next available sequential number
        /// </summary>
        public string GenerateNewElementTrackId(List<FamilyInstance> allElements)
        {
            var existingIds = allElements
                .Select(e => e.LookupParameter("trackID")?.AsString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();

            // Extract numeric portions and find max
            int maxNumber = 0;
            foreach (var id in existingIds)
            {
                if (id.StartsWith("ELEM-", StringComparison.OrdinalIgnoreCase) && id.Length >= 9)
                {
                    string numericPart = id.Substring(5, 4);
                    if (int.TryParse(numericPart, out int num))
                    {
                        maxNumber = Math.Max(maxNumber, num);
                    }
                }
            }

            // Find next available number (in case of gaps)
            int nextNumber = maxNumber + 1;
            string newId = $"ELEM-{nextNumber:D4}";

            // Safety check: ensure it's truly unique
            var existingSet = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);
            while (existingSet.Contains(newId))
            {
                nextNumber++;
                newId = $"ELEM-{nextNumber:D4}";
            }

            return newId;
        }
    }

    /// <summary>
    /// Represents a group of rooms sharing the same trackID
    /// </summary>
    public class DuplicateTrackIdGroup
    {
        public string TrackId { get; set; }
        public List<DuplicateTrackIdRoom> Rooms { get; set; }
    }

    /// <summary>
    /// Represents a room within a duplicate trackID group
    /// </summary>
    public class DuplicateTrackIdRoom
    {
        public Room Room { get; set; }
        public string RoomNumber { get; set; }
        public string RoomName { get; set; }
        public string Level { get; set; }
        public long ElementId { get; set; }
        public bool IsOriginal { get; set; }
        public string MatchReason { get; set; }
        public string SuggestedAction { get; set; }
        public string NewTrackId { get; set; }  // Set when action is "Regenerate"
    }
}
