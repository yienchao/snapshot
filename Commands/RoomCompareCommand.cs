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

            // 2. Get all versions from Supabase
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

            // 3. Let user select version using WPF dropdown window
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

            // 4. Get snapshot rooms from Supabase
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

            // 5. Get current rooms from Revit - check for pre-selection first
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

            // 6. Filter snapshot to only include rooms we're comparing
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

            // 7. Compare
            ComparisonResult comparison = CompareRooms(currentRooms, filteredSnapshotRooms, doc);

            // 8. Show results
            ShowComparisonResults(comparison, selectedVersion, commandData.Application);

            return Result.Succeeded;
        }

        private ComparisonResult CompareRooms(List<Room> currentRooms, List<RoomSnapshot> snapshotRooms, Document doc)
        {
            var result = new ComparisonResult();
            var snapshotDict = snapshotRooms.ToDictionary(s => s.TrackId, s => s);
            var currentDict = currentRooms.ToDictionary(r => r.LookupParameter("trackID").AsString(), r => r);

            // Find new rooms (in current, not in snapshot)
            foreach (var room in currentRooms)
            {
                var trackId = room.LookupParameter("trackID").AsString();
                if (!snapshotDict.ContainsKey(trackId))
                {
                    result.NewRooms.Add(new RoomChange
                    {
                        TrackId = trackId,
                        RoomNumber = room.Number,
                        RoomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                        ChangeType = "New"
                    });
                }
            }

            // Find deleted rooms (in snapshot, not in current)
            foreach (var snapshot in snapshotRooms)
            {
                if (!currentDict.ContainsKey(snapshot.TrackId))
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

            // Find modified rooms
            foreach (var room in currentRooms)
            {
                var trackId = room.LookupParameter("trackID").AsString();
                if (snapshotDict.TryGetValue(trackId, out var snapshot))
                {
                    var changes = GetParameterChanges(room, snapshot, doc);
                    if (changes.Any())
                    {
                        result.ModifiedRooms.Add(new RoomChange
                        {
                            TrackId = trackId,
                            RoomNumber = room.Number,
                            RoomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                            ChangeType = "Modified",
                            Changes = changes
                        });
                    }
                }
            }

            return result;
        }

        private List<string> GetParameterChanges(Room currentRoom, RoomSnapshot snapshot, Document doc)
        {
            var changes = new List<string>();

            // Get all current room parameters
            var currentParams = new Dictionary<string, object>();
            var currentParamsDisplay = new Dictionary<string, string>(); // For display with units

            foreach (Parameter param in currentRoom.Parameters)
            {
                string paramName = param.Definition.Name;
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
                        // Only add non-empty strings
                        var stringValue = param.AsString();
                        if (!string.IsNullOrEmpty(stringValue))
                        {
                            paramValue = stringValue;
                            displayValue = stringValue;
                            shouldAdd = true;
                        }
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

                    // Try to format snapshot value using current parameter's units
                    var param = currentRoom.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Definition.Name == kvp.Key);
                    if (param != null)
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
                ["level"] = (snapshot.Level, !string.IsNullOrEmpty(snapshot.Level)),

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

            // Now map these to actual parameter names found in the current room
            foreach (Parameter param in currentRoom.Parameters)
            {
                var paramName = param.Definition.Name;
                string columnKey = null;

                // Determine which snapshot column this parameter corresponds to
                // Support both French and English names
                switch (paramName)
                {
                    case "Numéro":
                    case "Number":
                        columnKey = "room_number";
                        break;
                    case "Nom":
                    case "Name":
                        columnKey = "room_name";
                        break;
                    case "Niveau":
                    case "Level":
                        columnKey = "level";
                        break;
                    case "Surface":
                    case "Area":
                        columnKey = "area";
                        break;
                    case "Périmètre":
                    case "Perimeter":
                        columnKey = "perimeter";
                        break;
                    case "Volume":
                        columnKey = "volume";
                        break;
                    case "Hauteur non liée":
                    case "Unbounded Height":
                        columnKey = "unbound_height";
                        break;
                    case "Occupation":
                    case "Occupancy":
                        columnKey = "occupancy";
                        break;
                    case "Service":
                    case "Department":
                        columnKey = "department";
                        break;
                    case "Phase":
                        columnKey = "phase";
                        break;
                    case "Finition de la base":
                    case "Base Finish":
                        columnKey = "base_finish";
                        break;
                    case "Finition du plafond":
                    case "Ceiling Finish":
                        columnKey = "ceiling_finish";
                        break;
                    case "Finition du mur":
                    case "Wall Finish":
                        columnKey = "wall_finish";
                        break;
                    case "Finition du sol":
                    case "Floor Finish":
                        columnKey = "floor_finish";
                        break;
                    case "Commentaires":
                    case "Comments":
                        columnKey = "comments";
                        break;
                    case "Occupant":
                        columnKey = "occupant";
                        break;
                }

                // If this parameter maps to a snapshot column and the snapshot has a value, add it
                if (columnKey != null && paramMapping.TryGetValue(columnKey, out var snapshotData) && snapshotData.hasValue)
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

            // Compare: check if values changed
            foreach (var snapshotParam in snapshotParams)
            {
                if (currentParams.TryGetValue(snapshotParam.Key, out var currentValue))
                {
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
                        var snapStr = snapshotParam.Value?.ToString() ?? "";
                        var currStr = currentValue?.ToString() ?? "";
                        isDifferent = (snapStr != currStr);
                    }

                    if (isDifferent)
                    {
                        // Use formatted display values for output
                        var snapDisplay = snapshotParamsDisplay.ContainsKey(snapshotParam.Key)
                            ? snapshotParamsDisplay[snapshotParam.Key]
                            : snapshotParam.Value?.ToString() ?? "";
                        var currDisplay = currentParamsDisplay.ContainsKey(snapshotParam.Key)
                            ? currentParamsDisplay[snapshotParam.Key]
                            : currentValue?.ToString() ?? "";

                        changes.Add($"{snapshotParam.Key}: '{snapDisplay}' → '{currDisplay}'");
                    }
                }
                else
                {
                    // Parameter was removed from current room
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
            int totalChanges = result.NewRooms.Count + result.DeletedRooms.Count + result.ModifiedRooms.Count;

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
                DeletedRoomsCount = result.DeletedRooms.Count
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
