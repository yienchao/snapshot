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
            var snapshot1Dict = snapshot1.ToDictionary(s => s.TrackId, s => s);
            var snapshot2Dict = snapshot2.ToDictionary(s => s.TrackId, s => s);

            // Find new rooms (in snapshot2, not in snapshot1)
            foreach (var room in snapshot2)
            {
                if (!snapshot1Dict.ContainsKey(room.TrackId))
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
                if (!snapshot2Dict.ContainsKey(room.TrackId))
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
                if (snapshot1Dict.TryGetValue(room2.TrackId, out var room1))
                {
                    // Check if room became unplaced (was placed in snapshot1, now unplaced in snapshot2)
                    bool wasPlaced = room1.Area.HasValue && room1.Area.Value > 0.001;
                    bool isNowUnplaced = !room2.Area.HasValue || room2.Area.Value <= 0.001;

                    if (wasPlaced && isNowUnplaced)
                    {
                        // Room was deleted from plan but still in schedule
                        result.UnplacedRooms.Add(new RoomChange
                        {
                            TrackId = room2.TrackId,
                            RoomNumber = room2.RoomNumber,
                            RoomName = room2.RoomName,
                            ChangeType = "Unplaced",
                            Changes = new List<string> { $"Room deleted from plan (Area was {room1.Area:F2}, now unplaced)" }
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
                        result.UnplacedRooms.Add(new RoomChange
                        {
                            TrackId = trackIdNormalized,
                            RoomNumber = room.Number,
                            RoomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                            ChangeType = "Unplaced",
                            Changes = new List<string> { $"Room deleted from plan (Area was {snapshot.Area:F2}, now unplaced)" }
                        });
                    }
                    else
                    {
                        // Check for parameter changes
                        var changes = GetParameterChanges(room, snapshot, doc);
                        if (changes.Any())
                        {
                            result.ModifiedRooms.Add(new RoomChange
                            {
                                TrackId = trackIdNormalized,
                                RoomNumber = room.Number,
                                RoomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                                ChangeType = "Modified",
                                Changes = changes
                            });
                        }
                    }
                }
            }

            return result;
        }

        // Cache for GetOrderedParameters to avoid redundant API calls
        private Dictionary<ElementId, IList<Parameter>> _parameterCache = new Dictionary<ElementId, IList<Parameter>>();

        private List<string> GetParameterChanges(Room currentRoom, RoomSnapshot snapshot, Document doc)
        {
            var changes = new List<string>();

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
            var paramByName = new Dictionary<string, Parameter>();
            var builtInParamCache = new Dictionary<string, BuiltInParameter>();
            foreach (Parameter p in orderedParams)
            {
                var paramName = p.Definition.Name;
                if (!paramByName.ContainsKey(paramName))
                {
                    paramByName[paramName] = p;

                    // Cache BuiltInParameter enum for fast lookup (avoid repeated "is InternalDefinition" checks)
                    if (p.Definition is InternalDefinition internalDef)
                    {
                        builtInParamCache[paramName] = internalDef.BuiltInParameter;
                    }
                }
            }
            foreach (Parameter param in orderedParams)
            {
                string paramName = param.Definition.Name;

                // Skip placement-dependent and system parameters that cause false positives
                // Use BuiltInParameter for language-independent exclusion
                if (param.Definition is InternalDefinition internalDef)
                {
                    var builtInParam = internalDef.BuiltInParameter;

                    // Skip these read-only placement-dependent parameters
                    if (builtInParam == BuiltInParameter.ROOM_LEVEL_ID ||
                        builtInParam == BuiltInParameter.ROOM_AREA ||
                        builtInParam == BuiltInParameter.ROOM_PERIMETER ||
                        builtInParam == BuiltInParameter.ROOM_VOLUME ||
                        builtInParam == BuiltInParameter.ROOM_UPPER_LEVEL ||
                        builtInParam == BuiltInParameter.ROOM_UPPER_OFFSET ||
                        builtInParam == BuiltInParameter.ROOM_LOWER_OFFSET ||
                        builtInParam == BuiltInParameter.ROOM_COMPUTATION_HEIGHT ||
                        builtInParam == BuiltInParameter.EDITED_BY ||
                        builtInParam == BuiltInParameter.IFC_EXPORT_ELEMENT_AS)  // IFC export (has formatting issues)
                        continue;
                }

                // Also skip by parameter name for IFC export (covers all languages)
                if (paramName == "Exporter au format IFC" || paramName == "Export to IFC" || paramName == "IFC Export")
                    continue;

                object paramValue = null;
                string displayValue = null;
                bool shouldAdd = false;

                switch (param.StorageType)
                {
                    case StorageType.Double:
                        // Store raw value for comparison, formatted value for display
                        paramValue = param.AsDouble();
                        var valueString = param.AsValueString();
                        if (!string.IsNullOrEmpty(valueString))
                        {
                            // Extract numeric part only (remove unit symbols)
                            displayValue = valueString.Split(' ')[0].Replace(",", ".");
                        }
                        else
                        {
                            displayValue = paramValue.ToString();
                        }
                        shouldAdd = true;
                        break;
                    case StorageType.Integer:
                        // Always add integer values, even if 0
                        paramValue = param.AsInteger();
                        var intValueString = param.AsValueString();
                        if (!string.IsNullOrEmpty(intValueString))
                        {
                            displayValue = intValueString.Split(' ')[0].Replace(",", ".");
                        }
                        else
                        {
                            displayValue = paramValue.ToString();
                        }
                        shouldAdd = true;
                        break;
                    case StorageType.String:
                        // Always add string parameters, even if empty (matches snapshot capture behavior)
                        // This ensures we detect changes from empty→value and value→empty
                        var stringValue = param.AsString();
                        paramValue = stringValue ?? "";
                        displayValue = stringValue ?? "";
                        shouldAdd = true;
                        break;
                    case StorageType.ElementId:
                        // Use AsValueString() to get the display value instead of the ID
                        var elementIdValueString = param.AsValueString();
                        if (!string.IsNullOrEmpty(elementIdValueString))
                        {
                            paramValue = elementIdValueString;
                            displayValue = elementIdValueString;
                            shouldAdd = true;
                        }
                        else if (param.AsElementId().Value != -1)
                        {
                            paramValue = param.AsElementId().Value.ToString();
                            displayValue = paramValue.ToString();
                            shouldAdd = true;
                        }
                        break;
                }

                if (shouldAdd)
                {
                    currentParams[paramName] = paramValue;
                    currentParamsDisplay[paramName] = displayValue;
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
                    snapshotParams[kvp.Key] = kvp.Value;

                    // Try to format snapshot value using current parameter's units (use cached dictionary for fast lookup)
                    if (paramByName.TryGetValue(kvp.Key, out var param))
                    {
                        try
                        {
                            // Format using the parameter's units (handles all numeric types)
                            snapshotParamsDisplay[kvp.Key] = FormatParameterValue(param, kvp.Value, doc);
                        }
                        catch
                        {
                            snapshotParamsDisplay[kvp.Key] = kvp.Value?.ToString() ?? "";
                        }
                    }
                    else
                    {
                        snapshotParamsDisplay[kvp.Key] = kvp.Value?.ToString() ?? "";
                    }
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
                            // Skip area - placement-dependent
                            continue;
                        case BuiltInParameter.ROOM_PERIMETER:
                            // Skip perimeter - placement-dependent
                            continue;
                        case BuiltInParameter.ROOM_VOLUME:
                            // Skip volume - placement-dependent
                            continue;
                        case BuiltInParameter.ROOM_UPPER_LEVEL:
                            // Skip upper level - placement-dependent
                            continue;
                        case BuiltInParameter.ROOM_UPPER_OFFSET:
                            // Skip upper offset (Limite supérieure) - placement-dependent
                            continue;
                        case BuiltInParameter.ROOM_LOWER_OFFSET:
                            // Skip lower offset - placement-dependent
                            continue;
                        case BuiltInParameter.ROOM_COMPUTATION_HEIGHT:
                            // Skip computation height - placement-dependent
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

                // If this parameter maps to a snapshot column, check if snapshot has value OR if parameter exists in current room
                // This ensures we compare even when snapshot OR current value is empty
                if (columnKey != null && paramMapping.TryGetValue(columnKey, out var snapshotData))
                {
                    // Always add the parameter to comparison if either:
                    // 1. Snapshot has a value
                    // 2. Current room has this parameter (even if empty)
                    if (snapshotData.hasValue || param != null)
                    {
                        snapshotParams[paramName] = snapshotData.value;
                        // Format display value using the current parameter's formatting
                        try
                        {
                            var formatted = FormatParameterValue(param, snapshotData.value, doc);
                            snapshotParamsDisplay[paramName] = formatted;
                            System.Diagnostics.Debug.WriteLine($"Formatted {paramName}: raw={snapshotData.value}, formatted={formatted}");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to format {paramName}: {ex.Message}");
                            snapshotParamsDisplay[paramName] = snapshotData.value?.ToString() ?? "";
                        }
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
                    // Found in dictionary (non-empty)
                    currentValue = dictValue;
                    currentDisplay = currentParamsDisplay.ContainsKey(snapshotParam.Key)
                        ? currentParamsDisplay[snapshotParam.Key]
                        : currentValue?.ToString() ?? "";
                    paramExistsInCurrent = true;
                }
                else
                {
                    // Not in dictionary - check if parameter exists in room but is empty (use cached dictionary for fast lookup)
                    if (paramByName.TryGetValue(snapshotParam.Key, out var param))
                    {
                        // Parameter exists but value is empty/null
                        paramExistsInCurrent = true;
                        switch (param.StorageType)
                        {
                            case StorageType.String:
                                currentValue = param.AsString() ?? "";
                                currentDisplay = currentValue.ToString();
                                break;
                            case StorageType.Integer:
                                currentValue = param.AsInteger();
                                currentDisplay = param.AsValueString()?.Split(' ')[0]?.Replace(",", ".") ?? currentValue.ToString();
                                break;
                            case StorageType.Double:
                                currentValue = param.AsDouble();
                                currentDisplay = param.AsValueString()?.Split(' ')[0]?.Replace(",", ".") ?? currentValue.ToString();
                                break;
                            case StorageType.ElementId:
                                currentValue = param.AsValueString() ?? "";
                                currentDisplay = currentValue.ToString();
                                break;
                        }
                    }
                }

                if (paramExistsInCurrent)
                {
                    // Compare values
                    bool isDifferent = false;

                    // For doubles, use tolerance comparison
                    if (snapshotParam.Value is double snapDouble && currentValue is double currDouble)
                    {
                        isDifferent = Math.Abs(snapDouble - currDouble) > 0.001;
                    }
                    // Handle long/int comparison (JSON deserialization might convert to long)
                    else if (snapshotParam.Value is long snapLong && currentValue is int currInt)
                    {
                        isDifferent = (snapLong != currInt);
                    }
                    else if (snapshotParam.Value is int snapInt && currentValue is long currLong)
                    {
                        isDifferent = (snapInt != currLong);
                    }
                    else
                    {
                        // For string comparison, use the formatted display values
                        var snapDisplay = snapshotParamsDisplay.ContainsKey(snapshotParam.Key)
                            ? snapshotParamsDisplay[snapshotParam.Key]
                            : snapshotParam.Value?.ToString() ?? "";

                        isDifferent = (snapDisplay != currentDisplay);
                    }

                    if (isDifferent)
                    {
                        // Use formatted display values for output
                        var snapDisplay = snapshotParamsDisplay.ContainsKey(snapshotParam.Key)
                            ? snapshotParamsDisplay[snapshotParam.Key]
                            : snapshotParam.Value?.ToString() ?? "";

                        changes.Add($"{snapshotParam.Key}: '{snapDisplay}' → '{currentDisplay}'");
                    }
                }
                else
                {
                    // Parameter doesn't exist in current room at all
                    var snapDisplay = snapshotParamsDisplay.ContainsKey(snapshotParam.Key)
                        ? snapshotParamsDisplay[snapshotParam.Key]
                        : snapshotParam.Value?.ToString() ?? "";
                    changes.Add($"{snapshotParam.Key}: '{snapDisplay}' → (removed)");
                }
            }

            // Check for new parameters (exist in current but not in snapshot)
            foreach (var currentParam in currentParams)
            {
                if (!snapshotParams.ContainsKey(currentParam.Key))
                {
                    var currDisplay = currentParamsDisplay.ContainsKey(currentParam.Key)
                        ? currentParamsDisplay[currentParam.Key]
                        : currentParam.Value?.ToString() ?? "";
                    changes.Add($"{currentParam.Key}: (new) → '{currDisplay}'");
                }
            }

            return changes;
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

                    // Get the conversion factor from the current parameter
                    // by comparing its raw value (internal units) to display value (file units)
                    var currentRawValue = param.AsDouble();
                    var currentDisplayString = param.AsValueString();

                    if (!string.IsNullOrEmpty(currentDisplayString) && Math.Abs(currentRawValue) > 0.0001)
                    {
                        // Parse the numeric part from the display string
                        // Handle formats like "32.8", "32.8 m", "32,8", etc.
                        string numericPart = currentDisplayString.Split(' ')[0].Replace(",", ".");

                        if (double.TryParse(numericPart, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double currentDisplayValue))
                        {
                            // Calculate conversion factor (display units / internal units)
                            double conversionFactor = currentDisplayValue / currentRawValue;

                            // Apply the same conversion to the snapshot value
                            double convertedValue = doubleValue * conversionFactor;

                            // Format with appropriate precision
                            return convertedValue.ToString("F2");
                        }
                    }

                    // If we can't determine conversion, return as-is with precision
                    return doubleValue.ToString("F2");
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
                    Changes = room.Changes
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
    }
}
