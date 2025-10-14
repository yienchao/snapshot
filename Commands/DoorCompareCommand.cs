using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using ViewTracker.Views;
using System.Collections.ObjectModel;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class DoorCompareCommand : IExternalCommand
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
            List<DoorSnapshot> versionSnapshots = new List<DoorSnapshot>();

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await supabaseService.InitializeAsync();
                    versionSnapshots = await supabaseService.GetAllDoorVersionsWithInfoAsync(projectId);
                }).Wait();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to load versions:\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            if (!versionSnapshots.Any())
            {
                TaskDialog.Show("No Versions", "No door snapshots found in Supabase. Create a snapshot first.");
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

            // 4. Get snapshot doors from Supabase
            List<DoorSnapshot> snapshotDoors = new List<DoorSnapshot>();
            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    snapshotDoors = await supabaseService.GetDoorsByVersionAsync(selectedVersion, projectId);
                }).Wait();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to load snapshot:\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            // 5. Get current doors from Revit - check for pre-selection first
            var uiDoc = commandData.Application.ActiveUIDocument;
            var selectedIds = uiDoc.Selection.GetElementIds();

            List<FamilyInstance> currentDoors;

            // Check if user has pre-selected doors
            if (selectedIds.Any())
            {
                // Use only selected doors that have trackID
                currentDoors = selectedIds
                    .Select(id => doc.GetElement(id))
                    .OfType<FamilyInstance>()
                    .Where(d => d.Category?.Id.Value == (int)BuiltInCategory.OST_Doors &&
                               d.LookupParameter("trackID") != null &&
                               !string.IsNullOrWhiteSpace(d.LookupParameter("trackID").AsString()))
                    .ToList();

                if (!currentDoors.Any())
                {
                    TaskDialog.Show("No Valid Doors Selected",
                        "None of the selected elements are doors with trackID.\n\n" +
                        "Please select doors with trackID parameter, or run without selection to compare all doors.");
                    return Result.Cancelled;
                }

                // Inform user about selection
                TaskDialog.Show("Using Selection",
                    $"Comparing {currentDoors.Count} pre-selected door(s) against version '{selectedVersion}'.");
            }
            else
            {
                // No selection - use all doors with trackID
                currentDoors = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Where(d => d.LookupParameter("trackID") != null &&
                               !string.IsNullOrWhiteSpace(d.LookupParameter("trackID").AsString()))
                    .ToList();

                if (!currentDoors.Any())
                {
                    TaskDialog.Show("No Doors Found", "No doors with trackID parameter found in the model.");
                    return Result.Cancelled;
                }
            }

            // 6. Filter snapshot to only include doors we're comparing
            List<DoorSnapshot> filteredSnapshotDoors;
            if (selectedIds.Any())
            {
                // Get trackIDs of selected doors
                var selectedTrackIds = currentDoors.Select(d => d.LookupParameter("trackID").AsString()).ToHashSet();

                // Only include snapshot doors that match selected trackIDs
                filteredSnapshotDoors = snapshotDoors.Where(s => selectedTrackIds.Contains(s.TrackId)).ToList();
            }
            else
            {
                // No selection - use all snapshot doors
                filteredSnapshotDoors = snapshotDoors;
            }

            // 7. Compare
            DoorComparisonResult comparison = CompareDoors(currentDoors, filteredSnapshotDoors, doc);

            // 8. Show results
            ShowComparisonResults(comparison, selectedVersion, commandData.Application);

            return Result.Succeeded;
        }

        private DoorComparisonResult CompareDoors(List<FamilyInstance> currentDoors, List<DoorSnapshot> snapshotDoors, Document doc)
        {
            var result = new DoorComparisonResult();
            var snapshotDict = snapshotDoors.ToDictionary(s => s.TrackId, s => s);
            var currentDict = currentDoors.ToDictionary(d => d.LookupParameter("trackID").AsString(), d => d);

            // Find new doors (in current, not in snapshot)
            foreach (var door in currentDoors)
            {
                var trackId = door.LookupParameter("trackID").AsString();
                if (!snapshotDict.ContainsKey(trackId))
                {
                    result.NewDoors.Add(new DoorChange
                    {
                        TrackId = trackId,
                        Mark = door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                        FamilyName = door.Symbol?.Family?.Name,
                        TypeName = door.Symbol?.Name,
                        ChangeType = "New"
                    });
                }
            }

            // Find deleted doors (in snapshot, not in current)
            foreach (var snapshot in snapshotDoors)
            {
                if (!currentDict.ContainsKey(snapshot.TrackId))
                {
                    result.DeletedDoors.Add(new DoorChange
                    {
                        TrackId = snapshot.TrackId,
                        Mark = snapshot.Mark,
                        FamilyName = snapshot.FamilyName,
                        TypeName = snapshot.TypeName,
                        ChangeType = "Deleted"
                    });
                }
            }

            // Find modified doors
            foreach (var door in currentDoors)
            {
                var trackId = door.LookupParameter("trackID").AsString();
                if (snapshotDict.TryGetValue(trackId, out var snapshot))
                {
                    var changes = GetParameterChanges(door, snapshot, doc);
                    if (changes.Any())
                    {
                        result.ModifiedDoors.Add(new DoorChange
                        {
                            TrackId = trackId,
                            Mark = door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                            FamilyName = door.Symbol?.Family?.Name,
                            TypeName = door.Symbol?.Name,
                            ChangeType = "Modified",
                            Changes = changes
                        });
                    }
                }
            }

            return result;
        }

        private List<string> GetParameterChanges(FamilyInstance currentDoor, DoorSnapshot snapshot, Document doc)
        {
            var changes = new List<string>();

            // Get all current door parameters (both instance and type)
            var currentParams = new Dictionary<string, object>();
            var currentParamsDisplay = new Dictionary<string, string>();

            // Get instance parameters
            foreach (Parameter param in currentDoor.Parameters)
            {
                AddParameterToDict(param, currentParams, currentParamsDisplay);
            }

            // Get type parameters
            if (currentDoor.Symbol != null)
            {
                foreach (Parameter param in currentDoor.Symbol.Parameters)
                {
                    if (!currentParams.ContainsKey(param.Definition.Name))
                    {
                        AddParameterToDict(param, currentParams, currentParamsDisplay);
                    }
                }
            }

            // Add location information (same as in snapshot)
            var location = currentDoor.Location;
            if (location is LocationPoint locationPoint)
            {
                var point = locationPoint.Point;
                currentParams["location_x"] = point.X;
                currentParamsDisplay["location_x"] = point.X.ToString("F6");
                currentParams["location_y"] = point.Y;
                currentParamsDisplay["location_y"] = point.Y.ToString("F6");
                currentParams["location_z"] = point.Z;
                currentParamsDisplay["location_z"] = point.Z.ToString("F6");
                currentParams["rotation"] = locationPoint.Rotation;
                currentParamsDisplay["rotation"] = locationPoint.Rotation.ToString("F6");
            }

            // Add facing and hand orientation (important for flip detection)
            if (currentDoor.FacingOrientation != null)
            {
                currentParams["facing_x"] = currentDoor.FacingOrientation.X;
                currentParamsDisplay["facing_x"] = currentDoor.FacingOrientation.X.ToString("F6");
                currentParams["facing_y"] = currentDoor.FacingOrientation.Y;
                currentParamsDisplay["facing_y"] = currentDoor.FacingOrientation.Y.ToString("F6");
                currentParams["facing_z"] = currentDoor.FacingOrientation.Z;
                currentParamsDisplay["facing_z"] = currentDoor.FacingOrientation.Z.ToString("F6");
            }
            if (currentDoor.HandOrientation != null)
            {
                currentParams["hand_x"] = currentDoor.HandOrientation.X;
                currentParamsDisplay["hand_x"] = currentDoor.HandOrientation.X.ToString("F6");
                currentParams["hand_y"] = currentDoor.HandOrientation.Y;
                currentParamsDisplay["hand_y"] = currentDoor.HandOrientation.Y.ToString("F6");
                currentParams["hand_z"] = currentDoor.HandOrientation.Z;
                currentParamsDisplay["hand_z"] = currentDoor.HandOrientation.Z.ToString("F6");
            }

            // Build snapshot parameters dictionary
            var snapshotParams = new Dictionary<string, object>();
            var snapshotParamsDisplay = new Dictionary<string, string>();

            // Parameters that should NOT be in all_parameters (they're in dedicated columns)
            var excludedFromJson = new HashSet<string>
            {
                "Mark", "Marque",
                "Level", "Niveau",
                "Fire Rating", "Cote de résistance au feu",
                "Width", "Largeur",
                "Height", "Hauteur",
                "Phase Created", "Phase de création",
                "Phase Demolished", "Phase de démolition",
                "Comments", "Commentaires",
                "Family", "Famille",
                "Type"
            };

            // Add from AllParameters JSON (but skip parameters that should be in dedicated columns)
            if (snapshot.AllParameters != null)
            {
                foreach (var kvp in snapshot.AllParameters)
                {
                    // Skip parameters that should be in dedicated columns
                    if (!excludedFromJson.Contains(kvp.Key))
                    {
                        snapshotParams[kvp.Key] = kvp.Value;
                        snapshotParamsDisplay[kvp.Key] = kvp.Value?.ToString() ?? "";
                    }
                }
            }

            // DO NOT add dedicated columns back for comparison
            // These parameters (Family, Type, Mark, Level, Width, Height, etc.) are excluded from both
            // current door parameters and snapshot parameters to avoid false change detection

            // Compare parameters
            foreach (var snapshotParam in snapshotParams)
            {
                if (currentParams.TryGetValue(snapshotParam.Key, out var currentValue))
                {
                    bool isDifferent = CompareValues(snapshotParam.Value, currentValue);

                    if (isDifferent)
                    {
                        var snapDisplay = snapshotParamsDisplay[snapshotParam.Key];
                        var currDisplay = currentParamsDisplay.ContainsKey(snapshotParam.Key)
                            ? currentParamsDisplay[snapshotParam.Key]
                            : currentValue?.ToString() ?? "";

                        changes.Add($"{snapshotParam.Key}: '{snapDisplay}' → '{currDisplay}'");
                    }
                }
                else
                {
                    var snapDisplay = snapshotParamsDisplay[snapshotParam.Key];
                    changes.Add($"{snapshotParam.Key}: '{snapDisplay}' → (removed)");
                }
            }

            // Check for new parameters
            foreach (var currentParam in currentParams)
            {
                if (!snapshotParams.ContainsKey(currentParam.Key))
                {
                    var currDisplay = currentParamsDisplay[currentParam.Key];
                    changes.Add($"{currentParam.Key}: (new) → '{currDisplay}'");
                }
            }

            return changes;
        }

        private void AddParameterToDict(Parameter param, Dictionary<string, object> values, Dictionary<string, string> display)
        {
            string paramName = param.Definition.Name;

            // Parameters that are stored in dedicated columns - exclude from comparison
            var excludedParams = new HashSet<string>
            {
                "Mark", "Marque",
                "Level", "Niveau",
                "Fire Rating", "Cote de résistance au feu",
                "Width", "Largeur",
                "Height", "Hauteur",
                "Phase Created", "Phase de création",
                "Phase Demolished", "Phase de démolition",
                "Comments", "Commentaires",
                "Family", "Famille",
                "Type"
            };

            // Skip parameters that are already in dedicated columns
            if (excludedParams.Contains(paramName))
                return;

            object paramValue = null;
            string displayValue = null;
            bool shouldAdd = false;

            switch (param.StorageType)
            {
                case StorageType.Double:
                    paramValue = param.AsDouble();
                    displayValue = param.AsValueString()?.Split(' ')[0]?.Replace(",", ".") ?? paramValue.ToString();
                    shouldAdd = true;
                    break;
                case StorageType.Integer:
                    paramValue = param.AsInteger();
                    displayValue = param.AsValueString()?.Split(' ')[0]?.Replace(",", ".") ?? paramValue.ToString();
                    shouldAdd = true;
                    break;
                case StorageType.String:
                    var stringValue = param.AsString();
                    if (!string.IsNullOrEmpty(stringValue))
                    {
                        paramValue = stringValue;
                        displayValue = stringValue;
                        shouldAdd = true;
                    }
                    break;
                case StorageType.ElementId:
                    var valueString = param.AsValueString();
                    if (!string.IsNullOrEmpty(valueString))
                    {
                        paramValue = valueString;
                        displayValue = valueString;
                        shouldAdd = true;
                    }
                    break;
            }

            if (shouldAdd)
            {
                values[paramName] = paramValue;
                display[paramName] = displayValue;
            }
        }

        private bool CompareValues(object snapValue, object currentValue)
        {
            if (snapValue is double snapDouble && currentValue is double currDouble)
                return Math.Abs(snapDouble - currDouble) > 0.001;

            if (snapValue is long snapLong && currentValue is int currInt)
                return snapLong != currInt;

            if (snapValue is int snapInt && currentValue is long currLong)
                return snapInt != currLong;

            var snapStr = snapValue?.ToString() ?? "";
            var currStr = currentValue?.ToString() ?? "";
            return snapStr != currStr;
        }

        private void ShowComparisonResults(DoorComparisonResult result, string versionName, UIApplication uiApp)
        {
            int totalChanges = result.NewDoors.Count + result.DeletedDoors.Count + result.ModifiedDoors.Count;

            if (totalChanges == 0)
            {
                TaskDialog.Show("No Changes", $"No changes detected compared to version '{versionName}'.");
                return;
            }

            // Build ViewModel (reusing room view model structure)
            var viewModel = new ComparisonResultViewModel
            {
                VersionName = versionName,
                VersionInfo = $"Door Comparison | Version: {versionName} | Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                EntityTypeLabel = "DOORS",
                NewRoomsCount = result.NewDoors.Count,
                ModifiedRoomsCount = result.ModifiedDoors.Count,
                DeletedRoomsCount = result.DeletedDoors.Count
            };

            // Convert to display models (reusing room display structure)
            var displayItems = new List<RoomChangeDisplay>();

            foreach (var door in result.NewDoors)
            {
                displayItems.Add(new RoomChangeDisplay
                {
                    ChangeType = "New",
                    TrackId = door.TrackId,
                    RoomNumber = door.Mark,
                    RoomName = $"{door.FamilyName}: {door.TypeName}",
                    Changes = new List<string>()
                });
            }

            foreach (var door in result.ModifiedDoors)
            {
                displayItems.Add(new RoomChangeDisplay
                {
                    ChangeType = "Modified",
                    TrackId = door.TrackId,
                    RoomNumber = door.Mark,
                    RoomName = $"{door.FamilyName}: {door.TypeName}",
                    Changes = door.Changes
                });
            }

            foreach (var door in result.DeletedDoors)
            {
                displayItems.Add(new RoomChangeDisplay
                {
                    ChangeType = "Deleted",
                    TrackId = door.TrackId,
                    RoomNumber = door.Mark,
                    RoomName = $"{door.FamilyName}: {door.TypeName}",
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
    public class DoorComparisonResult
    {
        public List<DoorChange> NewDoors { get; set; } = new List<DoorChange>();
        public List<DoorChange> DeletedDoors { get; set; } = new List<DoorChange>();
        public List<DoorChange> ModifiedDoors { get; set; } = new List<DoorChange>();
    }

    public class DoorChange
    {
        public string TrackId { get; set; }
        public string Mark { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public string ChangeType { get; set; }
        public List<string> Changes { get; set; } = new List<string>();
    }
}
