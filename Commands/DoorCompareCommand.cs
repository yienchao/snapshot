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
                    var (allChanges, instanceChanges, typeChanges) = GetParameterChanges(door, snapshot, doc);
                    if (allChanges.Any())
                    {
                        result.ModifiedDoors.Add(new DoorChange
                        {
                            TrackId = trackId,
                            Mark = door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                            FamilyName = door.Symbol?.Family?.Name,
                            TypeName = door.Symbol?.Name,
                            ChangeType = "Modified",
                            Changes = allChanges,
                            InstanceParameterChanges = instanceChanges,
                            TypeParameterChanges = typeChanges
                        });
                    }
                }
            }

            return result;
        }

        private (List<string> allChanges, List<string> instanceChanges, List<string> typeChanges) GetParameterChanges(FamilyInstance currentDoor, DoorSnapshot snapshot, Document doc)
        {
            var changes = new List<string>();
            var instanceChanges = new List<string>();
            var typeChanges = new List<string>();

            // Get type parameter names for categorization
            var typeParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (snapshot.TypeParameters != null)
            {
                foreach (var key in snapshot.TypeParameters.Keys)
                {
                    typeParameterNames.Add(key);
                }
            }

            // Get all current door parameters (both instance and type, user-visible only)
            var currentParams = new Dictionary<string, object>();
            var currentParamsDisplay = new Dictionary<string, string>();

            // Get ONLY instance parameters using GetOrderedParameters
            // Get ONLY instance parameters (not type parameters)
            // We'll add type parameters later only if snapshot has them
            var orderedParams = currentDoor.GetOrderedParameters();
            foreach (Parameter param in orderedParams)
            {
                // Skip type parameters - only collect instance parameters here
                if (param.Element is ElementType)
                    continue;

                AddParameterToDict(param, currentParams, currentParamsDisplay);
            }

            // Also collect type parameters from current door (for comparison with snapshot type parameters)
            var currentTypeParams = new Dictionary<string, object>();
            var currentTypeParamsDisplay = new Dictionary<string, string>();
            if (currentDoor.Symbol != null)
            {
                var orderedTypeParams = currentDoor.Symbol.GetOrderedParameters();
                foreach (Parameter param in orderedTypeParams)
                {
                    AddParameterToDict(param, currentTypeParams, currentTypeParamsDisplay);
                }
            }

            // Add location information (same as in snapshot)
            var location = currentDoor.Location;
            if (location is LocationPoint locationPoint)
            {
                var point = locationPoint.Point;
                currentParams["location_x"] = point.X;
                currentParamsDisplay["location_x"] = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, point.X, false);
                currentParams["location_y"] = point.Y;
                currentParamsDisplay["location_y"] = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, point.Y, false);
                currentParams["location_z"] = point.Z;
                currentParamsDisplay["location_z"] = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, point.Z, false);
                currentParams["rotation"] = locationPoint.Rotation;
                currentParamsDisplay["rotation"] = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Angle, locationPoint.Rotation, false);
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
                "Mark", "Marque", "Identifiant",  // Mark parameter (various languages)
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
                // Check if this is an old snapshot (doesn't have type_parameters column)
                // NULL means old snapshot, empty dictionary means new snapshot with no type parameters
                bool isOldSnapshot = snapshot.TypeParameters == null;

                foreach (var kvp in snapshot.AllParameters)
                {
                    // Skip IFC-related parameters from old snapshots (for backward compatibility)
                    if (kvp.Key.Contains("IFC", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Skip parameters that should be in dedicated columns
                    if (excludedFromJson.Contains(kvp.Key))
                        continue;

                    // Skip host_id and host_category ONLY from old snapshots (to avoid false positives)
                    // Keep location, rotation, facing, hand for quality control comparison
                    if (isOldSnapshot &&
                        (kvp.Key.Equals("host_id", StringComparison.OrdinalIgnoreCase) ||
                        kvp.Key.Equals("host_category", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    snapshotParams[kvp.Key] = kvp.Value;

                    // Format the display value properly
                    // For doubles, we need to get the formatted value from the current parameter
                    string displayValue;
                    if (kvp.Value is double doubleVal)
                    {
                        // Special handling for location/rotation parameters (they're in feet/radians)
                        if (kvp.Key.StartsWith("location_") || kvp.Key == "rotation" ||
                            kvp.Key.StartsWith("facing_") || kvp.Key.StartsWith("hand_"))
                        {
                            // For location: convert feet to project units (mm, inches, etc.)
                            if (kvp.Key.StartsWith("location_"))
                            {
                                displayValue = UnitFormatUtils.Format(
                                    currentDoor.Document.GetUnits(),
                                    SpecTypeId.Length,
                                    doubleVal,
                                    false);
                            }
                            // For rotation: show in degrees or project angle units
                            else if (kvp.Key == "rotation")
                            {
                                displayValue = UnitFormatUtils.Format(
                                    currentDoor.Document.GetUnits(),
                                    SpecTypeId.Angle,
                                    doubleVal,
                                    false);
                            }
                            // For facing/hand orientation: just show the vector component
                            else
                            {
                                displayValue = doubleVal.ToString("F6");
                            }
                        }
                        else
                        {
                            // Try to get the current parameter to format it properly
                            var currentParam = currentDoor.LookupParameter(kvp.Key);
                            if (currentParam != null && currentParam.StorageType == StorageType.Double)
                            {
                                // Format with units, then strip the unit label
                                string formatted = UnitFormatUtils.Format(
                                    currentDoor.Document.GetUnits(),
                                    currentParam.Definition.GetDataType(),
                                    doubleVal,
                                    false);
                                // Strip unit label (e.g., "2438.4 mm" → "2438.4")
                                displayValue = formatted?.Split(' ')[0]?.Replace(",", ".") ?? doubleVal.ToString("F2");
                            }
                            else
                            {
                                // Fallback: just show the number
                                displayValue = doubleVal.ToString("F2");
                            }
                        }
                    }
                    else
                    {
                        displayValue = kvp.Value?.ToString() ?? "";
                    }

                    snapshotParamsDisplay[kvp.Key] = displayValue;
                }
            }

            // Add from TypeParameters JSON (type parameters) for comparison
            // ONLY if snapshot has type parameters (for backward compatibility with old snapshots)
            if (snapshot.TypeParameters != null && snapshot.TypeParameters.Any())
            {
                // Add snapshot type parameters
                foreach (var kvp in snapshot.TypeParameters)
                {
                    snapshotParams[kvp.Key] = kvp.Value;

                    // For display, format type parameters
                    string displayValue;
                    if (kvp.Value is double doubleVal)
                    {
                        // Try to get the parameter from the door's type to format it properly
                        Parameter currentParam = null;
                        if (currentDoor.Symbol != null)
                        {
                            currentParam = currentDoor.Symbol.LookupParameter(kvp.Key);
                        }

                        if (currentParam != null && currentParam.StorageType == StorageType.Double)
                        {
                            string formatted = UnitFormatUtils.Format(
                                currentDoor.Document.GetUnits(),
                                currentParam.Definition.GetDataType(),
                                doubleVal,
                                false);
                            displayValue = formatted?.Split(' ')[0]?.Replace(",", ".") ?? doubleVal.ToString("F2");
                        }
                        else
                        {
                            displayValue = doubleVal.ToString("F2");
                        }
                    }
                    else
                    {
                        displayValue = kvp.Value?.ToString() ?? "";
                    }

                    snapshotParamsDisplay[kvp.Key] = displayValue;
                }

                // Also add current type parameters to currentParams for comparison
                foreach (var kvp in currentTypeParams)
                {
                    currentParams[kvp.Key] = kvp.Value;
                }
                foreach (var kvp in currentTypeParamsDisplay)
                {
                    currentParamsDisplay[kvp.Key] = kvp.Value;
                }
            }

            // Add dedicated column values from snapshot to snapshotParams for comparison
            // These are not in AllParameters JSON, but we still want to compare them
            // IMPORTANT: Use the actual parameter name from the current door (language-independent)
            // not hardcoded English names, to avoid false "(new)" and "(removed)" changes

            // Mark parameter - include even if empty
            var markParam = currentDoor.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            if (markParam != null)
            {
                string markParamName = markParam.Definition.Name;
                snapshotParams[markParamName] = snapshot.Mark ?? "";
                snapshotParamsDisplay[markParamName] = snapshot.Mark ?? "";
            }

            // Level parameter - only add if not empty (ElementId parameters are skipped when empty in AddParameterToDict)
            var levelParam = currentDoor.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
            if (levelParam != null && !string.IsNullOrEmpty(snapshot.Level))
            {
                string levelParamName = levelParam.Definition.Name;
                snapshotParams[levelParamName] = snapshot.Level;
                snapshotParamsDisplay[levelParamName] = snapshot.Level;
            }

            // Fire Rating parameter - include even if empty (string parameter, not ElementId)
            var fireRatingParam = currentDoor.get_Parameter(BuiltInParameter.DOOR_FIRE_RATING);
            if (fireRatingParam != null)
            {
                string fireRatingParamName = fireRatingParam.Definition.Name;
                snapshotParams[fireRatingParamName] = snapshot.FireRating ?? "";
                snapshotParamsDisplay[fireRatingParamName] = snapshot.FireRating ?? "";
            }

            // Comments parameter - include even if empty (string parameter, not ElementId)
            var commentsParam = currentDoor.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (commentsParam != null)
            {
                string commentsParamName = commentsParam.Definition.Name;
                snapshotParams[commentsParamName] = snapshot.Comments ?? "";
                snapshotParamsDisplay[commentsParamName] = snapshot.Comments ?? "";
            }

            // Phase Created parameter - only add if not empty (ElementId parameters are skipped when empty in AddParameterToDict)
            var phaseCreatedParam = currentDoor.get_Parameter(BuiltInParameter.PHASE_CREATED);
            if (phaseCreatedParam != null && !string.IsNullOrEmpty(snapshot.PhaseCreated))
            {
                string phaseCreatedParamName = phaseCreatedParam.Definition.Name;
                snapshotParams[phaseCreatedParamName] = snapshot.PhaseCreated;
                snapshotParamsDisplay[phaseCreatedParamName] = snapshot.PhaseCreated;
            }

            // Phase Demolished parameter - only add if not empty (ElementId parameters are skipped when empty in AddParameterToDict)
            var phaseDemolishedParam = currentDoor.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
            if (phaseDemolishedParam != null && !string.IsNullOrEmpty(snapshot.PhaseDemolished))
            {
                string phaseDemolishedParamName = phaseDemolishedParam.Definition.Name;
                snapshotParams[phaseDemolishedParamName] = snapshot.PhaseDemolished;
                snapshotParamsDisplay[phaseDemolishedParamName] = snapshot.PhaseDemolished;
            }

            // Note: Family and Type are NOT in AllParameters, but we need to compare them
            // to detect when a door changes to a different type

            // Compare Family
            string currentFamily = currentDoor.Symbol?.Family?.Name ?? "";
            string snapshotFamily = snapshot.FamilyName ?? "";
            if (currentFamily != snapshotFamily)
            {
                changes.Add($"Family: '{snapshotFamily}' → '{currentFamily}'");
            }

            // Compare Type
            string currentType = currentDoor.Symbol?.Name ?? "";
            string snapshotType = snapshot.TypeName ?? "";
            if (currentType != snapshotType)
            {
                changes.Add($"Type: '{snapshotType}' → '{currentType}'");
            }

            // Note: Width and Height are type parameters, not instance parameters

            // Compare parameters (including location, rotation, facing, hand for quality control)
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

                        string changeText = $"{snapshotParam.Key}: '{snapDisplay}' → '{currDisplay}'";
                        changes.Add(changeText);

                        // Categorize as instance or type parameter
                        if (typeParameterNames.Contains(snapshotParam.Key))
                            typeChanges.Add(changeText);
                        else
                            instanceChanges.Add(changeText);
                    }
                }
                else
                {
                    var snapDisplay = snapshotParamsDisplay[snapshotParam.Key];
                    string changeText = $"{snapshotParam.Key}: '{snapDisplay}' → (removed)";
                    changes.Add(changeText);

                    // Categorize as instance or type parameter
                    if (typeParameterNames.Contains(snapshotParam.Key))
                        typeChanges.Add(changeText);
                    else
                        instanceChanges.Add(changeText);
                }
            }

            // Check for new parameters (including type parameters from TypeParameters column)
            foreach (var currentParam in currentParams)
            {
                if (!snapshotParams.ContainsKey(currentParam.Key))
                {
                    var currDisplay = currentParamsDisplay[currentParam.Key];
                    string changeText = $"{currentParam.Key}: (new) → '{currDisplay}'";
                    changes.Add(changeText);

                    // Categorize as instance or type parameter
                    if (typeParameterNames.Contains(currentParam.Key))
                        typeChanges.Add(changeText);
                    else
                        instanceChanges.Add(changeText);
                }
            }

            return (changes, instanceChanges, typeChanges);
        }

        private void AddParameterToDict(Parameter param, Dictionary<string, object> values, Dictionary<string, string> display)
        {
            string paramName = param.Definition.Name;

            // Exclude IFC-related parameters from comparison (they're auto-generated)
            if (paramName.StartsWith("IFC", StringComparison.OrdinalIgnoreCase) ||
                paramName.StartsWith("Ifc", StringComparison.Ordinal) ||
                paramName.Contains("IFC", StringComparison.OrdinalIgnoreCase))
                return;

            // DO NOT exclude parameters with dedicated columns - they should still be compared!
            // We want to compare ALL instance parameters that GetOrderedParameters() returns

            object paramValue = null;
            string displayValue = null;
            bool shouldAdd = false;

            switch (param.StorageType)
            {
                case StorageType.Double:
                    paramValue = param.AsDouble();
                    // Use AsValueString for display, keep full value (don't truncate)
                    displayValue = param.AsValueString() ?? paramValue.ToString();
                    shouldAdd = true;
                    break;
                case StorageType.Integer:
                    // Use AsValueString to get enum text (e.g., "Par type" instead of "0")
                    var intValueString = param.AsValueString();
                    if (!string.IsNullOrEmpty(intValueString))
                    {
                        paramValue = intValueString; // Store the display text for comparison
                        displayValue = intValueString;
                        shouldAdd = true;
                    }
                    else
                    {
                        paramValue = param.AsInteger();
                        displayValue = paramValue.ToString();
                        shouldAdd = true;
                    }
                    break;
                case StorageType.String:
                    // Include all string parameters, even empty ones (to match snapshot behavior)
                    var stringValue = param.AsString();
                    paramValue = (stringValue ?? "").Trim(); // Trim whitespace for accurate comparison
                    displayValue = (stringValue ?? "").Trim();
                    shouldAdd = true;
                    break;
                case StorageType.ElementId:
                    var valueString = param.AsValueString();
                    if (!string.IsNullOrEmpty(valueString))
                    {
                        paramValue = valueString.Trim(); // Trim whitespace for accurate comparison
                        displayValue = valueString.Trim();
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
            // Handle numeric comparisons with tolerance
            if (snapValue is double snapDouble && currentValue is double currDouble)
                return Math.Abs(snapDouble - currDouble) > 0.001;

            // Handle mixed integer/long comparisons
            if (snapValue is long snapLong && currentValue is int currInt)
                return snapLong != currInt;

            if (snapValue is int snapInt && currentValue is long currLong)
                return snapInt != currLong;

            // String comparison: trim whitespace and normalize
            var snapStr = snapValue?.ToString()?.Trim() ?? "";
            var currStr = currentValue?.ToString()?.Trim() ?? "";

            // Return true if different (changed)
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
                    Changes = door.Changes,
                    InstanceParameterChanges = door.InstanceParameterChanges,
                    TypeParameterChanges = door.TypeParameterChanges
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
        public List<string> InstanceParameterChanges { get; set; } = new List<string>();
        public List<string> TypeParameterChanges { get; set; } = new List<string>();
    }
}
