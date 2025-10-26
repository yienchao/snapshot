using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using ViewTracker.Views;
using System.Collections.ObjectModel;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class RoomCompareCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument.Document;

            // 1. Validate projectID
            var projectIdStr = doc.ProjectInformation.LookupParameter("projectID")?.AsString();
            if (!Guid.TryParse(projectIdStr, out Guid projectId))
            {
                TaskDialog.Show("Error", "This file does not have a valid projectID parameter.");
                return Result.Failed;
            }

            // 2. Show mode selection window
            var modeWindow = new ComparisonModeWindow();
            var modeResult = modeWindow.ShowDialog();

            if (modeResult != true)
                return Result.Cancelled;

            // Branch based on selected mode
            if (modeWindow.SelectedMode == ComparisonMode.CurrentVsSnapshot)
            {
                return ExecuteCurrentVsSnapshot(commandData, projectId);
            }
            else // SnapshotVsSnapshot
            {
                return ExecuteSnapshotVsSnapshot(commandData, projectId);
            }
        }

        private Result ExecuteCurrentVsSnapshot(ExternalCommandData commandData, Guid projectId)
        {
            var doc = commandData.Application.ActiveUIDocument.Document;

            // 1. Get all versions from Supabase
            var supabaseService = new SupabaseService();
            List<RoomSnapshot> versionSnapshots = new List<RoomSnapshot>();

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await supabaseService.InitializeAsync();
                    versionSnapshots = await supabaseService.GetAllVersionsWithInfoAsync(projectId);
                }).Wait();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to load versions:\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            if (!versionSnapshots.Any())
            {
                TaskDialog.Show("No Versions", "No snapshots found in Supabase. Create a snapshot first.");
                return Result.Cancelled;
            }

            // 2. Let user select version using WPF dropdown window
            var versionInfos = versionSnapshots
                .GroupBy(v => v.VersionName)
                .Select(g => new VersionInfo
                {
                    VersionName = g.Key,
                    SnapshotDate = g.First().SnapshotDate,
                    CreatedBy = g.First().CreatedBy,
                    IsOfficial = g.First().IsOfficial
                })
                .OrderByDescending(v => v.SnapshotDate)
                .ToList();

            var selectionWindow = new VersionSelectionWindow(versionInfos);
            var dialogResult = selectionWindow.ShowDialog();

            if (dialogResult != true)
                return Result.Cancelled;

            string selectedVersion = selectionWindow.SelectedVersionName;
            if (string.IsNullOrWhiteSpace(selectedVersion))
                return Result.Cancelled;

            // 3. Get snapshot rooms from Supabase
            List<RoomSnapshot> snapshotRooms = new List<RoomSnapshot>();
            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    snapshotRooms = await supabaseService.GetRoomsByVersionAsync(selectedVersion, projectId);
                }).Wait();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to load snapshot:\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            // 4. Get current rooms from Revit - check for pre-selection first
            var uiDoc = commandData.Application.ActiveUIDocument;
            var selectedIds = uiDoc.Selection.GetElementIds();

            List<Room> currentRooms;

            // Check if user has pre-selected rooms
            if (selectedIds.Any())
            {
                // Use only selected rooms that have trackID
                currentRooms = selectedIds
                    .Select(id => doc.GetElement(id))
                    .OfType<Room>()
                    .Where(r => r.LookupParameter("trackID") != null &&
                               !string.IsNullOrWhiteSpace(r.LookupParameter("trackID").AsString()))
                    .ToList();

                if (!currentRooms.Any())
                {
                    TaskDialog.Show("No Valid Rooms Selected",
                        "None of the selected elements are rooms with trackID.\n\n" +
                        "Please select rooms with trackID parameter, or run without selection to compare all rooms.");
                    return Result.Cancelled;
                }

                // Inform user about selection
                TaskDialog.Show("Using Selection",
                    $"Comparing {currentRooms.Count} pre-selected room(s) against version '{selectedVersion}'.");
            }
            else
            {
                // No selection - use all rooms with trackID
                currentRooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r.LookupParameter("trackID") != null &&
                               !string.IsNullOrWhiteSpace(r.LookupParameter("trackID").AsString()))
                    .ToList();

                if (!currentRooms.Any())
                {
                    TaskDialog.Show("No Rooms Found", "No rooms with trackID parameter found in the model.");
                    return Result.Cancelled;
                }
            }

            // 5. Filter snapshot to only include rooms we're comparing
            // If user pre-selected rooms, only compare against those trackIDs in the snapshot
            List<RoomSnapshot> filteredSnapshotRooms;
            if (selectedIds.Any())
            {
                // Get trackIDs of selected rooms
                var selectedTrackIds = currentRooms.Select(r => r.LookupParameter("trackID").AsString()).ToHashSet();

                // Only include snapshot rooms that match selected trackIDs
                filteredSnapshotRooms = snapshotRooms.Where(s => selectedTrackIds.Contains(s.TrackId)).ToList();
            }
            else
            {
                // No selection - use all snapshot rooms
                filteredSnapshotRooms = snapshotRooms;
            }

            // 6. Compare
            ComparisonResult comparison = CompareRooms(currentRooms, filteredSnapshotRooms, doc);

            // 7. Show results
            ShowComparisonResults(comparison, $"Current vs {selectedVersion}", commandData.Application);

            return Result.Succeeded;
        }

        private Result ExecuteSnapshotVsSnapshot(ExternalCommandData commandData, Guid projectId)
        {
            var doc = commandData.Application.ActiveUIDocument.Document;

            // 1. Get all versions from Supabase
            var supabaseService = new SupabaseService();
            List<RoomSnapshot> versionSnapshots = new List<RoomSnapshot>();

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await supabaseService.InitializeAsync();
                    versionSnapshots = await supabaseService.GetAllVersionsWithInfoAsync(projectId);
                }).Wait();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to load versions:\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            if (!versionSnapshots.Any())
            {
                TaskDialog.Show("No Versions", "No snapshots found in Supabase. Create a snapshot first.");
                return Result.Cancelled;
            }

            // 2. Build version list
            var versionInfos = versionSnapshots
                .GroupBy(v => v.VersionName)
                .Select(g => new VersionInfo
                {
                    VersionName = g.Key,
                    SnapshotDate = g.First().SnapshotDate,
                    CreatedBy = g.First().CreatedBy,
                    IsOfficial = g.First().IsOfficial
                })
                .OrderByDescending(v => v.SnapshotDate)
                .ToList();

            if (versionInfos.Count < 2)
            {
                TaskDialog.Show("Not Enough Versions", "You need at least 2 snapshot versions to compare snapshots.");
                return Result.Cancelled;
            }

            // 3. Let user select first version (BEFORE - baseline/older state)
            var selection1Window = new VersionSelectionWindow(versionInfos,
                "Select BEFORE Version (Baseline/Older)",
                "Choose the baseline/older snapshot version:");
            var result1 = selection1Window.ShowDialog();

            if (result1 != true)
                return Result.Cancelled;

            string version1 = selection1Window.SelectedVersionName;
            if (string.IsNullOrWhiteSpace(version1))
                return Result.Cancelled;

            // 4. Let user select second version (AFTER - what changed)
            var selection2Window = new VersionSelectionWindow(versionInfos,
                "Select AFTER Version (What Changed)",
                "Choose the newer snapshot version to see what changed:");
            var result2 = selection2Window.ShowDialog();

            if (result2 != true)
                return Result.Cancelled;

            string version2 = selection2Window.SelectedVersionName;
            if (string.IsNullOrWhiteSpace(version2))
                return Result.Cancelled;

            if (version1 == version2)
            {
                TaskDialog.Show("Same Version", "You selected the same version twice. Please select two different versions.");
                return Result.Cancelled;
            }

            // 5. Load both snapshots
            List<RoomSnapshot> snapshot1 = new List<RoomSnapshot>();
            List<RoomSnapshot> snapshot2 = new List<RoomSnapshot>();

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    snapshot1 = await supabaseService.GetRoomsByVersionAsync(version1, projectId);
                    snapshot2 = await supabaseService.GetRoomsByVersionAsync(version2, projectId);
                }).Wait();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to load snapshots:\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            // 6. Compare snapshots
            ComparisonResult comparison = CompareSnapshots(snapshot1, snapshot2, doc);

            // 7. Show results with clear BEFORE → AFTER labeling
            ShowComparisonResults(comparison, $"{version1} (BEFORE) → {version2} (AFTER)", commandData.Application);

            return Result.Succeeded;
        }

        private ComparisonResult CompareSnapshots(List<RoomSnapshot> snapshot1, List<RoomSnapshot> snapshot2, Document doc)
        {
            var result = new ComparisonResult();

            // Use GroupBy to handle duplicate trackIDs safely (take first occurrence)
            var snapshot1Dict = snapshot1
                .Where(s => !string.IsNullOrWhiteSpace(s.TrackId))
                .GroupBy(s => s.TrackId.Trim())
                .ToDictionary(g => g.Key, g => g.First());

            var snapshot2Dict = snapshot2
                .Where(s => !string.IsNullOrWhiteSpace(s.TrackId))
                .GroupBy(s => s.TrackId.Trim())
                .ToDictionary(g => g.Key, g => g.First());

            // Find new rooms (in snapshot2, not in snapshot1)
            foreach (var room in snapshot2)
            {
                if (string.IsNullOrWhiteSpace(room.TrackId)) continue;
                var trackIdNormalized = room.TrackId.Trim();

                if (!snapshot1Dict.ContainsKey(trackIdNormalized))
                {
                    result.NewRooms.Add(new RoomChange
                    {
                        TrackId = room.TrackId,
                        RoomNumber = room.RoomNumber,
                        RoomName = room.RoomName,
                        ChangeType = "New"
                    });
                }
            }

            // Find deleted rooms (in snapshot1, not in snapshot2)
            foreach (var room in snapshot1)
            {
                if (string.IsNullOrWhiteSpace(room.TrackId)) continue;
                var trackIdNormalized = room.TrackId.Trim();

                if (!snapshot2Dict.ContainsKey(trackIdNormalized))
                {
                    result.DeletedRooms.Add(new RoomChange
                    {
                        TrackId = room.TrackId,
                        RoomNumber = room.RoomNumber,
                        RoomName = room.RoomName,
                        ChangeType = "Deleted"
                    });
                }
            }

            // Find modified rooms and unplaced rooms
            foreach (var room2 in snapshot2)
            {
                if (string.IsNullOrWhiteSpace(room2.TrackId)) continue;
                var trackIdNormalized = room2.TrackId.Trim();

                if (snapshot1Dict.TryGetValue(trackIdNormalized, out var room1))
                {
                    // Check if room became unplaced (was placed in snapshot1, now unplaced in snapshot2)
                    bool wasPlaced = room1.Area.HasValue && room1.Area.Value > 0.001;
                    bool isNowUnplaced = !room2.Area.HasValue || room2.Area.Value <= 0.001;

                    if (wasPlaced && isNowUnplaced)
                    {
                        // Room was deleted from plan but still in schedule
                        // Convert area from internal units (sq ft) to square meters
                        double areaInM2 = room1.Area.Value * 0.09290304; // 1 sq ft = 0.09290304 m²
                        result.UnplacedRooms.Add(new RoomChange
                        {
                            TrackId = room2.TrackId,
                            RoomNumber = room2.RoomNumber,
                            RoomName = room2.RoomName,
                            ChangeType = "Unplaced",
                            Changes = new List<string> { $"Room deleted from plan (Area was {areaInM2:F3} m², now unplaced)" }
                        });
                    }
                    else
                    {
                        // Check for parameter changes
                        var changes = GetSnapshotChanges(room1, room2);
                        if (changes.Any())
                        {
                            result.ModifiedRooms.Add(new RoomChange
                            {
                                TrackId = room2.TrackId,
                                RoomNumber = room2.RoomNumber,
                                RoomName = room2.RoomName,
                                ChangeType = "Modified",
                                Changes = changes
                            });
                        }
                    }
                }
            }

            return result;
        }

        private List<string> GetSnapshotChanges(RoomSnapshot snapshot1, RoomSnapshot snapshot2)
        {
            var changes = new List<string>();

            // Build parameter dictionaries from both snapshots
            var params1 = BuildSnapshotParams(snapshot1);
            var params2 = BuildSnapshotParams(snapshot2);

            // Find all changed parameters
            foreach (var param2 in params2)
            {
                if (params1.TryGetValue(param2.Key, out var value1))
                {
                    bool isDifferent = false;

                    if (param2.Value is double double2 && value1 is double double1)
                    {
                        isDifferent = Math.Abs(double2 - double1) > 0.001;
                    }
                    else if (param2.Value is long long2 && value1 is int int1)
                    {
                        isDifferent = (long2 != int1);
                    }
                    else if (param2.Value is int int2 && value1 is long long1)
                    {
                        isDifferent = (int2 != long1);
                    }
                    else
                    {
                        var str1 = value1?.ToString() ?? "";
                        var str2 = param2.Value?.ToString() ?? "";
                        isDifferent = (str1 != str2);
                    }

                    if (isDifferent)
                    {
                        changes.Add($"{param2.Key}: '{value1}' → '{param2.Value}'");
                    }
                }
                else
                {
                    changes.Add($"{param2.Key}: (new) '{param2.Value}'");
                }
            }

            // Find removed parameters
            foreach (var param1 in params1)
            {
                if (!params2.ContainsKey(param1.Key))
                {
                    changes.Add($"{param1.Key}: '{param1.Value}' → (removed)");
                }
            }

            return changes;
        }

        private Dictionary<string, object> BuildSnapshotParams(RoomSnapshot snapshot)
        {
            var parameters = new Dictionary<string, object>();

            if (snapshot.AllParameters != null)
            {
                foreach (var kvp in snapshot.AllParameters)
                {
                    parameters[kvp.Key] = kvp.Value;
                }
            }

            // Add dedicated columns
            if (!string.IsNullOrEmpty(snapshot.RoomNumber))
                parameters["Room Number"] = snapshot.RoomNumber;
            if (!string.IsNullOrEmpty(snapshot.RoomName))
                parameters["Room Name"] = snapshot.RoomName;
            // Skip Level - it's read-only and causes false positives for unplaced rooms
            // if (!string.IsNullOrEmpty(snapshot.Level))
            //     parameters["Level"] = snapshot.Level;
            if (snapshot.Area.HasValue)
                parameters["Area"] = snapshot.Area.Value;
            if (snapshot.Perimeter.HasValue)
                parameters["Perimeter"] = snapshot.Perimeter.Value;
            if (snapshot.Volume.HasValue)
                parameters["Volume"] = snapshot.Volume.Value;
            if (snapshot.UnboundHeight.HasValue)
                parameters["Unbounded Height"] = snapshot.UnboundHeight.Value;
            if (!string.IsNullOrEmpty(snapshot.Occupancy))
                parameters["Occupancy"] = snapshot.Occupancy;
            if (!string.IsNullOrEmpty(snapshot.Department))
                parameters["Department"] = snapshot.Department;
            if (!string.IsNullOrEmpty(snapshot.Phase))
                parameters["Phase"] = snapshot.Phase;
            if (!string.IsNullOrEmpty(snapshot.BaseFinish))
                parameters["Base Finish"] = snapshot.BaseFinish;
            if (!string.IsNullOrEmpty(snapshot.CeilingFinish))
                parameters["Ceiling Finish"] = snapshot.CeilingFinish;
            if (!string.IsNullOrEmpty(snapshot.WallFinish))
                parameters["Wall Finish"] = snapshot.WallFinish;
            if (!string.IsNullOrEmpty(snapshot.FloorFinish))
                parameters["Floor Finish"] = snapshot.FloorFinish;
            if (!string.IsNullOrEmpty(snapshot.Comments))
                parameters["Comments"] = snapshot.Comments;
            if (!string.IsNullOrEmpty(snapshot.Occupant))
                parameters["Occupant"] = snapshot.Occupant;

            return parameters;
        }

        private ComparisonResult CompareRooms(List<Room> currentRooms, List<RoomSnapshot> snapshotRooms, Document doc)
        {
            var result = new ComparisonResult();

            // Clear parameter cache at the start of each comparison
            _parameterCache.Clear();

            // Cache trackID lookups to avoid redundant parameter access (3x performance improvement)
            var roomTrackIdCache = new Dictionary<Room, string>();
            foreach (var room in currentRooms)
            {
                var trackIdParam = room.LookupParameter("trackID");
                if (trackIdParam != null && !string.IsNullOrWhiteSpace(trackIdParam.AsString()))
                {
                    roomTrackIdCache[room] = trackIdParam.AsString().Trim();
                }
            }

            // Check for duplicate trackIDs in current rooms and warn user
            var currentTrackIdGroups = roomTrackIdCache
                .GroupBy(kvp => kvp.Value)
                .Where(g => g.Count() > 1)
                .ToList();

            if (currentTrackIdGroups.Any())
            {
                string duplicates = string.Join("\n", currentTrackIdGroups.Select(g =>
                    $"trackID '{g.Key}' appears {g.Count()} times in rooms: {string.Join(", ", g.Select(kvp => kvp.Key.Number))}"));

                TaskDialog.Show("Duplicate trackIDs Detected",
                    $"WARNING: Found duplicate trackIDs in current model. Using first occurrence for comparison.\n\n{duplicates}");
            }

            // Check for duplicate trackIDs in snapshot and warn user
            var snapshotTrackIdGroups = snapshotRooms
                .Where(s => !string.IsNullOrWhiteSpace(s.TrackId))
                .GroupBy(s => s.TrackId.Trim())
                .Where(g => g.Count() > 1)
                .ToList();

            if (snapshotTrackIdGroups.Any())
            {
                string duplicates = string.Join("\n", snapshotTrackIdGroups.Select(g =>
                    $"trackID '{g.Key}' appears {g.Count()} times in snapshot rooms: {string.Join(", ", g.Select(r => r.RoomNumber))}"));

                TaskDialog.Show("Duplicate trackIDs in Snapshot",
                    $"WARNING: Found duplicate trackIDs in snapshot. Using first occurrence for comparison.\n\n{duplicates}");
            }

            // Build dictionaries with normalized trackIDs (trim whitespace)
            // Use GroupBy to handle potential duplicates, taking the first occurrence
            var snapshotDict = snapshotRooms
                .Where(s => !string.IsNullOrWhiteSpace(s.TrackId))
                .GroupBy(s => s.TrackId.Trim())
                .ToDictionary(g => g.Key, g => g.First());

            // Use cached trackIDs instead of repeated LookupParameter calls
            var currentDict = roomTrackIdCache
                .GroupBy(kvp => kvp.Value)
                .ToDictionary(g => g.Key, g => g.First().Key);

            // Find new rooms (in current, not in snapshot) - use cached trackIDs
            foreach (var kvp in roomTrackIdCache)
            {
                var room = kvp.Key;
                var trackIdNormalized = kvp.Value;

                if (!snapshotDict.ContainsKey(trackIdNormalized))
                {
                    result.NewRooms.Add(new RoomChange
                    {
                        TrackId = trackIdNormalized,
                        RoomNumber = room.Number,
                        RoomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                        ChangeType = "New"
                    });
                }
            }

            // Find deleted rooms (in snapshot, not in current)
            foreach (var snapshot in snapshotRooms)
            {
                if (string.IsNullOrWhiteSpace(snapshot.TrackId)) continue;

                var trackIdNormalized = snapshot.TrackId.Trim();
                if (!currentDict.ContainsKey(trackIdNormalized))
                {
                    result.DeletedRooms.Add(new RoomChange
                    {
                        TrackId = snapshot.TrackId,
                        RoomNumber = snapshot.RoomNumber,
                        RoomName = snapshot.RoomName,
                        ChangeType = "Deleted"
                    });
                }
            }

            // Find modified rooms and unplaced rooms - use cached trackIDs
            foreach (var kvp in roomTrackIdCache)
            {
                var room = kvp.Key;
                var trackIdNormalized = kvp.Value;

                if (snapshotDict.TryGetValue(trackIdNormalized, out var snapshot))
                {
                    // Check if room became unplaced (was placed in snapshot, now unplaced in current)
                    bool wasPlaced = snapshot.Area.HasValue && snapshot.Area.Value > 0.001;
                    bool isNowUnplaced = room.Location == null;

                    if (wasPlaced && isNowUnplaced)
                    {
                        // Room was deleted from plan but still in schedule
                        // Convert area from internal units (sq ft) to square meters
                        double areaInM2 = snapshot.Area.Value * 0.09290304; // 1 sq ft = 0.09290304 m²
                        result.UnplacedRooms.Add(new RoomChange
                        {
                            TrackId = trackIdNormalized,
                            RoomNumber = room.Number,
                            RoomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                            ChangeType = "Unplaced",
                            Changes = new List<string> { $"Room deleted from plan (Area was {areaInM2:F3} m², now unplaced)" }
                        });
                    }
                    else
                    {
                        // Check for parameter changes
                        var (allChanges, instanceChanges, typeChanges) = GetParameterChanges(room, snapshot, doc);
                        if (allChanges.Any())
                        {
                            result.ModifiedRooms.Add(new RoomChange
                            {
                                TrackId = trackIdNormalized,
                                RoomNumber = room.Number,
                                RoomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                                ChangeType = "Modified",
                                Changes = allChanges,
                                InstanceParameterChanges = instanceChanges,
                                TypeParameterChanges = typeChanges
                            });
                        }
                    }
                }
            }

            return result;
        }

        // Cache for GetOrderedParameters to avoid redundant API calls
        private Dictionary<ElementId, IList<Parameter>> _parameterCache = new Dictionary<ElementId, IList<Parameter>>();

        private (List<string> allChanges, List<string> instanceChanges, List<string> typeChanges) GetParameterChanges(Room currentRoom, RoomSnapshot snapshot, Document doc)
        {
            var changes = new List<string>();
            var instanceChanges = new List<string>(); // For rooms, all parameters are instance parameters
            var typeChanges = new List<string>(); // Will be empty for rooms

            // Get all current room parameters (user-visible only)
            var currentParams = new Dictionary<string, object>();
            var currentParamsDisplay = new Dictionary<string, string>(); // For display with units

            // Use cached parameters to avoid redundant GetOrderedParameters calls
            if (!_parameterCache.TryGetValue(currentRoom.Id, out var orderedParams))
            {
                orderedParams = currentRoom.GetOrderedParameters();
                _parameterCache[currentRoom.Id] = orderedParams;
            }

            // Create a dictionary of parameters by name for fast lookups (avoid repeated FirstOrDefault calls)
            // OPTIMIZATION: Single loop to build cache AND process parameters (was two separate loops)
            var paramByName = new Dictionary<string, Parameter>();
            var builtInParamCache = new Dictionary<string, BuiltInParameter>();

            foreach (Parameter param in orderedParams)
            {
                string paramName = param.Definition.Name;

                // Build lookup dictionaries (skip duplicates)
                if (!paramByName.ContainsKey(paramName))
                {
                    paramByName[paramName] = param;

                    // Cache BuiltInParameter enum for fast lookup (avoid repeated "is InternalDefinition" checks)
                    if (param.Definition is InternalDefinition internalDefForCache)
                    {
                        builtInParamCache[paramName] = internalDefForCache.BuiltInParameter;
                    }
                }

                // Skip only system metadata and IFC parameters in comparison
                // Use cached BuiltInParameter to avoid redundant casting
                if (builtInParamCache.TryGetValue(paramName, out var builtInParam))
                {
                    // Skip only these system parameters
                    if (builtInParam == BuiltInParameter.EDITED_BY ||
                        builtInParam == BuiltInParameter.IFC_EXPORT_ELEMENT_AS)  // IFC export (has formatting issues)
                        continue;
                }

                // Also skip by parameter name (covers all languages and parameters not in cache)
                if (paramName == "Exporter au format IFC" || paramName == "Export to IFC" || paramName == "IFC Export")
                    continue;

                // Skip EDITED_BY parameter (covers all languages)
                if (paramName == "Modifié par" || paramName == "Edited by" || paramName == "Modified by")
                    continue;

                // Skip placement-dependent parameters that cause false positives
                if (paramName == "Limite supérieure" || paramName == "Upper Limit" ||
                    paramName == "Décalage limite" || paramName == "Base Offset" || paramName == "Limit Offset" ||
                    paramName == "Hauteur de calcul" || paramName == "Computation Height" ||
                    paramName == "Niveau" || paramName == "Level")
                    continue;

                // NEW: Use ParameterValue class for type-safe storage and comparison
                var paramValue = Models.ParameterValue.FromRevitParameter(param);
                if (paramValue != null)
                {
                    currentParams[paramName] = paramValue;
                    currentParamsDisplay[paramName] = paramValue.DisplayValue;
                }
            }

            // Build snapshot parameters dictionary from BOTH dedicated columns AND AllParameters JSON
            var snapshotParams = new Dictionary<string, object>();
            var snapshotParamsDisplay = new Dictionary<string, string>(); // For formatted display

            // First, add from AllParameters JSON (these are the "extra" parameters not in dedicated columns)
            if (snapshot.AllParameters != null)
            {
                foreach (var kvp in snapshot.AllParameters)
                {
                    // Skip placement-dependent parameters that cause false positives (for backwards compatibility with old snapshots)
                    if (kvp.Key == "Limite supérieure" || kvp.Key == "Upper Limit" ||
                        kvp.Key == "Décalage limite" || kvp.Key == "Base Offset" || kvp.Key == "Limit Offset" ||
                        kvp.Key == "Hauteur de calcul" || kvp.Key == "Computation Height" ||
                        kvp.Key == "Niveau" || kvp.Key == "Level")
                        continue;

                    // Skip IFC parameters
                    if (kvp.Key == "Exporter au format IFC" || kvp.Key == "Export to IFC" || kvp.Key == "IFC Export" ||
                        kvp.Key.Contains("IFC") || kvp.Key.Contains("prédéfini d'IFC"))
                        continue;

                    // Skip EDITED_BY parameter (covers all languages)
                    if (kvp.Key == "Modifié par" || kvp.Key == "Edited by" || kvp.Key == "Modified by")
                        continue;

                    // Convert JSON objects to ParameterValue objects
                    var paramValue = Models.ParameterValue.FromJsonObject(kvp.Value);
                    if (paramValue == null)
                    {
                        // This should never happen with new snapshots - log error and skip
                        System.Diagnostics.Debug.WriteLine($"ERROR: Failed to convert parameter '{kvp.Key}' to ParameterValue. Snapshot may be corrupted.");
                        continue;
                    }

                    snapshotParams[kvp.Key] = paramValue;
                    snapshotParamsDisplay[kvp.Key] = paramValue.DisplayValue;
                }
            }

            // Then add from dedicated columns (these were EXCLUDED from AllParameters during snapshot creation)
            // We need to map the actual parameter names that exist in the current room
            // Create a reverse lookup: find which current parameter corresponds to which snapshot column

            var paramMapping = new Dictionary<string, (object value, bool hasValue)>
            {
                // Room identification
                ["room_number"] = (snapshot.RoomNumber, !string.IsNullOrEmpty(snapshot.RoomNumber)),
                ["room_name"] = (snapshot.RoomName, !string.IsNullOrEmpty(snapshot.RoomName)),
                // Skip Level - it's read-only and causes false positives for unplaced rooms
                // ["level"] = (snapshot.Level, !string.IsNullOrEmpty(snapshot.Level)),

                // Measurements
                ["area"] = (snapshot.Area, snapshot.Area.HasValue),
                ["perimeter"] = (snapshot.Perimeter, snapshot.Perimeter.HasValue),
                ["volume"] = (snapshot.Volume, snapshot.Volume.HasValue),
                ["unbound_height"] = (snapshot.UnboundHeight, snapshot.UnboundHeight.HasValue),

                // Categories
                ["occupancy"] = (snapshot.Occupancy, !string.IsNullOrEmpty(snapshot.Occupancy)),
                ["department"] = (snapshot.Department, !string.IsNullOrEmpty(snapshot.Department)),
                ["phase"] = (snapshot.Phase, !string.IsNullOrEmpty(snapshot.Phase)),

                // Finishes
                ["base_finish"] = (snapshot.BaseFinish, !string.IsNullOrEmpty(snapshot.BaseFinish)),
                ["ceiling_finish"] = (snapshot.CeilingFinish, !string.IsNullOrEmpty(snapshot.CeilingFinish)),
                ["wall_finish"] = (snapshot.WallFinish, !string.IsNullOrEmpty(snapshot.WallFinish)),
                ["floor_finish"] = (snapshot.FloorFinish, !string.IsNullOrEmpty(snapshot.FloorFinish)),

                // Other
                ["comments"] = (snapshot.Comments, !string.IsNullOrEmpty(snapshot.Comments)),
                ["occupant"] = (snapshot.Occupant, !string.IsNullOrEmpty(snapshot.Occupant))
            };

            // Now map these to actual parameter names found in the current room (user-visible only)
            // Use BuiltInParameter enum for language-independent mapping
            foreach (Parameter param in orderedParams)
            {
                var paramName = param.Definition.Name;
                string columnKey = null;

                // For built-in parameters, use BuiltInParameter enum (language-independent)
                if (param.Definition is InternalDefinition internalDef)
                {
                    var builtInParam = internalDef.BuiltInParameter;
                    switch (builtInParam)
                    {
                        case BuiltInParameter.ROOM_NUMBER:
                            columnKey = "room_number";
                            break;
                        case BuiltInParameter.ROOM_NAME:
                            columnKey = "room_name";
                            break;
                        case BuiltInParameter.ROOM_LEVEL_ID:
                            // Skip level - it's read-only and causes false positives
                            continue;
                        case BuiltInParameter.ROOM_AREA:
                            columnKey = "area";
                            break;
                        case BuiltInParameter.ROOM_PERIMETER:
                            columnKey = "perimeter";
                            break;
                        case BuiltInParameter.ROOM_VOLUME:
                            columnKey = "volume";
                            break;
                        case BuiltInParameter.ROOM_UPPER_LEVEL:
                            columnKey = "unbound_height";
                            break;
                        case BuiltInParameter.ROOM_UPPER_OFFSET:
                            // Skip upper offset (Limite supérieure) - not in dedicated columns
                            continue;
                        case BuiltInParameter.ROOM_LOWER_OFFSET:
                            // Skip lower offset - not in dedicated columns
                            continue;
                        case BuiltInParameter.ROOM_COMPUTATION_HEIGHT:
                            // Skip computation height - not in dedicated columns
                            continue;
                        case BuiltInParameter.ROOM_OCCUPANCY:
                            columnKey = "occupancy";
                            break;
                        case BuiltInParameter.ROOM_DEPARTMENT:
                            columnKey = "department";
                            break;
                        case BuiltInParameter.ROOM_PHASE:
                            columnKey = "phase";
                            break;
                        case BuiltInParameter.ROOM_FINISH_BASE:
                            columnKey = "base_finish";
                            break;
                        case BuiltInParameter.ROOM_FINISH_CEILING:
                            columnKey = "ceiling_finish";
                            break;
                        case BuiltInParameter.ROOM_FINISH_WALL:
                            columnKey = "wall_finish";
                            break;
                        case BuiltInParameter.ROOM_FINISH_FLOOR:
                            columnKey = "floor_finish";
                            break;
                        case BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS:
                            columnKey = "comments";
                            break;
                    }
                }
                else
                {
                    // For shared parameters, check by name
                    if (paramName == "Occupant")
                        columnKey = "occupant";
                }

                // Skip IFC parameters by name (covers all languages, in case they weren't caught by BuiltInParameter check)
                if (paramName == "Exporter au format IFC" || paramName == "Export to IFC" || paramName == "IFC Export" ||
                    paramName.Contains("IFC") || paramName.Contains("prédéfini d'IFC"))
                    continue;

                // Skip placement-dependent parameters that cause false positives (for backwards compatibility with old snapshots)
                if (paramName == "Limite supérieure" || paramName == "Upper Limit" ||
                    paramName == "Décalage limite" || paramName == "Base Offset" || paramName == "Limit Offset" ||
                    paramName == "Hauteur de calcul" || paramName == "Computation Height" ||
                    paramName == "Niveau" || paramName == "Level")
                    continue;

                // If this parameter maps to a snapshot column, check if snapshot has value OR if parameter exists in current room
                // This ensures we compare even when snapshot OR current value is empty
                if (columnKey != null && paramMapping.TryGetValue(columnKey, out var snapshotData))
                {
                    // Always add the parameter to comparison if either:
                    // 1. Snapshot has a value
                    // 2. Current room has this parameter (even if empty)
                    if (snapshotData.hasValue || param != null)
                    {
                        // BUGFIX: Wrap dedicated column values in ParameterValue objects to match current format
                        // Create a ParameterValue from the raw dedicated column value
                        var paramValue = new Models.ParameterValue
                        {
                            StorageType = param.StorageType.ToString(),
                            IsTypeParameter = false
                        };

                        switch (param.StorageType)
                        {
                            case StorageType.String:
                                var stringVal = snapshotData.value as string ?? "";
                                paramValue.RawValue = stringVal;
                                paramValue.DisplayValue = stringVal;
                                break;
                            case StorageType.Double:
                                var doubleVal = Convert.ToDouble(snapshotData.value);
                                paramValue.RawValue = doubleVal;
                                // BUGFIX: Format the SNAPSHOT value, not the current value!
                                // Use UnitFormatUtils to format the snapshot value with current document units
                                try
                                {
                                    paramValue.DisplayValue = UnitFormatUtils.Format(
                                        doc.GetUnits(),
                                        param.Definition.GetDataType(),
                                        doubleVal,
                                        false);
                                }
                                catch
                                {
                                    paramValue.DisplayValue = doubleVal.ToString();
                                }
                                break;
                            case StorageType.Integer:
                                var intVal = Convert.ToInt32(snapshotData.value);
                                paramValue.RawValue = intVal;
                                // For integers, we can't format the snapshot value, so just show the number
                                paramValue.DisplayValue = intVal.ToString();
                                break;
                            case StorageType.ElementId:
                                var elemVal = snapshotData.value as string ?? "";
                                paramValue.RawValue = elemVal;
                                paramValue.DisplayValue = elemVal;
                                break;
                        }

                        snapshotParams[paramName] = paramValue;
                        snapshotParamsDisplay[paramName] = paramValue.DisplayValue;
                    }
                }
            }

            // Compare: check if values changed
            // For each snapshot parameter, look it up in current room directly (not just in currentParams dictionary)
            foreach (var snapshotParam in snapshotParams)
            {
                // Try to get value from currentParams dictionary first
                object currentValue = null;
                string currentDisplay = "";
                bool paramExistsInCurrent = false;

                if (currentParams.TryGetValue(snapshotParam.Key, out var dictValue))
                {
                    // Found in dictionary (non-empty) - should be a ParameterValue object
                    currentValue = dictValue;
                    currentDisplay = currentParamsDisplay.ContainsKey(snapshotParam.Key)
                        ? currentParamsDisplay[snapshotParam.Key]
                        : dictValue?.ToString() ?? "";
                    paramExistsInCurrent = true;
                }
                else
                {
                    // Not in dictionary - check if parameter exists in room but is empty
                    // PROFESSIONAL FIX: Create ParameterValue object for empty parameters too
                    if (paramByName.TryGetValue(snapshotParam.Key, out var param))
                    {
                        paramExistsInCurrent = true;

                        // Create ParameterValue object for the empty/null parameter
                        var paramValue = Models.ParameterValue.FromRevitParameter(param);
                        if (paramValue != null)
                        {
                            currentValue = paramValue;
                            currentDisplay = paramValue.DisplayValue;
                        }
                        else
                        {
                            // Fallback if FromRevitParameter fails
                            currentValue = null;
                            currentDisplay = "";
                        }
                    }
                }

                if (paramExistsInCurrent)
                {
                    // BUGFIX: Use standardized CompareValues method
                    bool isDifferent = CompareValues(snapshotParam.Value, currentValue);

                    if (isDifferent)
                    {
                        // Use formatted display values for output
                        var snapDisplay = snapshotParamsDisplay.ContainsKey(snapshotParam.Key)
                            ? snapshotParamsDisplay[snapshotParam.Key]
                            : snapshotParam.Value?.ToString() ?? "";

                        string changeText = $"{snapshotParam.Key}: '{snapDisplay}' → '{currentDisplay}'";
                        changes.Add(changeText);
                        instanceChanges.Add(changeText); // All room parameters are instance parameters
                    }
                }
                else
                {
                    // Parameter doesn't exist in current room at all
                    var snapDisplay = snapshotParamsDisplay.ContainsKey(snapshotParam.Key)
                        ? snapshotParamsDisplay[snapshotParam.Key]
                        : snapshotParam.Value?.ToString() ?? "";
                    string changeText = $"{snapshotParam.Key}: '{snapDisplay}' → (removed)";
                    changes.Add(changeText);
                    instanceChanges.Add(changeText); // All room parameters are instance parameters
                }
            }

            // Check for new parameters (exist in current but not in snapshot)
            foreach (var currentParam in currentParams)
            {
                if (!snapshotParams.ContainsKey(currentParam.Key))
                {
                    // BUGFIX: Skip EDITED_BY parameter (excluded from snapshots, covers all languages)
                    var paramNameLower = currentParam.Key.ToLower();
                    if (paramNameLower.Contains("modifié par") ||
                        paramNameLower.Contains("edited by") ||
                        paramNameLower.Contains("modified by"))
                        continue;

                    var currDisplay = currentParamsDisplay.ContainsKey(currentParam.Key)
                        ? currentParamsDisplay[currentParam.Key]
                        : currentParam.Value?.ToString() ?? "";

                    // BUGFIX: Skip empty/zero parameters - they're not really "new", they just weren't captured in snapshot
                    // Handle both English and French decimal separators
                    if (string.IsNullOrWhiteSpace(currDisplay))
                        continue;

                    // Check for numeric zeros (handles "0", "0.0", "0.00", "0,0", "0,00")
                    if (double.TryParse(currDisplay.Replace(',', '.'), out double numValue) && Math.Abs(numValue) < 0.0001)
                        continue;

                    string changeText = $"{currentParam.Key}: (new) → '{currDisplay}'";
                    changes.Add(changeText);
                    instanceChanges.Add(changeText); // All room parameters are instance parameters
                }
            }

            return (changes, instanceChanges, typeChanges);
        }

        // NEW: Type-safe comparison using ParameterValue objects
        private bool CompareValues(object snapValue, object currentValue)
        {
            // Both must be ParameterValue objects
            if (snapValue is Models.ParameterValue snapParam && currentValue is Models.ParameterValue currParam)
            {
                return !snapParam.IsEqualTo(currParam); // Return true if DIFFERENT
            }

            // If we get here, something is wrong - log and treat as different
            System.Diagnostics.Debug.WriteLine($"WARNING: CompareValues received non-ParameterValue objects. Snapshot type: {snapValue?.GetType().Name}, Current type: {currentValue?.GetType().Name}");
            return true; // Treat as different to be safe
        }

        // Helper method to convert parameter values to file's display units (no unit symbols)
        private string FormatParameterValue(Parameter param, object value, Document doc)
        {
            if (value == null)
                return "";

            try
            {
                // For numeric values, convert from internal units to display units
                if (param.StorageType == StorageType.Double)
                {
                    // Convert value to double (handles int, long, double from JSON)
                    double doubleValue = 0;

                    if (value is double d)
                        doubleValue = d;
                    else if (value is float f)
                        doubleValue = f;
                    else if (value is int i)
                        doubleValue = i;
                    else if (value is long l)
                        doubleValue = l;
                    else if (double.TryParse(value.ToString(), out double parsed))
                        doubleValue = parsed;
                    else
                        return value.ToString();

                    // Convert from internal units to display units using document's unit settings
                    try
                    {
                        var spec = param.Definition.GetDataType();
                        var formatOptions = doc.GetUnits().GetFormatOptions(spec);
                        var displayUnitType = formatOptions.GetUnitTypeId();
                        double convertedValue = UnitUtils.ConvertFromInternalUnits(doubleValue, displayUnitType);

                        // Format with 2 decimal places (no unit label)
                        return convertedValue.ToString("0.##");
                    }
                    catch
                    {
                        // Fallback: If UnitUtils fails, return the raw value
                        return doubleValue.ToString("F2");
                    }
                }

                return value.ToString();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FormatParameterValue failed: {ex.Message}, param: {param?.Definition?.Name}");
                return value.ToString();
            }
        }

        private void ShowComparisonResults(ComparisonResult result, string versionName, UIApplication uiApp)
        {
            int totalChanges = result.NewRooms.Count + result.DeletedRooms.Count + result.ModifiedRooms.Count + result.UnplacedRooms.Count;

            if (totalChanges == 0)
            {
                TaskDialog.Show("No Changes", $"No changes detected compared to version '{versionName}'.");
                return;
            }

            // Build ViewModel
            var viewModel = new ComparisonResultViewModel
            {
                VersionName = versionName,
                VersionInfo = $"Comparing current state with version: {versionName} | Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                NewRoomsCount = result.NewRooms.Count,
                ModifiedRoomsCount = result.ModifiedRooms.Count,
                DeletedRoomsCount = result.DeletedRooms.Count,
                UnplacedRoomsCount = result.UnplacedRooms.Count
            };

            // Convert to display models
            var displayItems = new List<RoomChangeDisplay>();

            foreach (var room in result.NewRooms)
            {
                displayItems.Add(new RoomChangeDisplay
                {
                    ChangeType = "New",
                    TrackId = room.TrackId,
                    RoomNumber = room.RoomNumber,
                    RoomName = room.RoomName,
                    Changes = new List<string>()
                });
            }

            foreach (var room in result.ModifiedRooms)
            {
                displayItems.Add(new RoomChangeDisplay
                {
                    ChangeType = "Modified",
                    TrackId = room.TrackId,
                    RoomNumber = room.RoomNumber,
                    RoomName = room.RoomName,
                    Changes = room.Changes,
                    InstanceParameterChanges = room.InstanceParameterChanges,
                    TypeParameterChanges = room.TypeParameterChanges
                });
            }

            foreach (var room in result.DeletedRooms)
            {
                displayItems.Add(new RoomChangeDisplay
                {
                    ChangeType = "Deleted",
                    TrackId = room.TrackId,
                    RoomNumber = room.RoomNumber,
                    RoomName = room.RoomName,
                    Changes = new List<string>()
                });
            }

            foreach (var room in result.UnplacedRooms)
            {
                displayItems.Add(new RoomChangeDisplay
                {
                    ChangeType = "Unplaced",
                    TrackId = room.TrackId,
                    RoomNumber = room.RoomNumber,
                    RoomName = room.RoomName,
                    Changes = room.Changes
                });
            }

            viewModel.AllResults = new ObservableCollection<RoomChangeDisplay>(displayItems);
            viewModel.FilteredResults = new ObservableCollection<RoomChangeDisplay>(displayItems);

            // Show WPF window
            var window = new ComparisonResultWindow(viewModel);
            window.ShowDialog();
        }
    }

    // Helper classes
    public class ComparisonResult
    {
        public List<RoomChange> NewRooms { get; set; } = new List<RoomChange>();
        public List<RoomChange> DeletedRooms { get; set; } = new List<RoomChange>();
        public List<RoomChange> ModifiedRooms { get; set; } = new List<RoomChange>();
        public List<RoomChange> UnplacedRooms { get; set; } = new List<RoomChange>();
    }

    public class RoomChange
    {
        public string TrackId { get; set; }
        public string RoomNumber { get; set; }
        public string RoomName { get; set; }
        public string ChangeType { get; set; }
        public List<string> Changes { get; set; } = new List<string>();
        public List<string> InstanceParameterChanges { get; set; } = new List<string>();
        public List<string> TypeParameterChanges { get; set; } = new List<string>();
    }
}
