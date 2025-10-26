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

            var selectionWindow = new VersionSelectionWindow(versionInfos,
                null, // use default title
                "Choose a snapshot version to compare with current doors:");
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
            var comparison = CompareDoors(currentDoors, filteredSnapshotDoors, doc);

            // 8. Show results using unified helper
            Helpers.ComparisonHelper.ShowComparisonResults(comparison, selectedVersion, "DOORS");

            return Result.Succeeded;
        }

        private Models.ComparisonResult<Models.EntityChange> CompareDoors(List<FamilyInstance> currentDoors, List<DoorSnapshot> snapshotDoors, Document doc)
        {
            var result = new Models.ComparisonResult<Models.EntityChange>();
            var snapshotDict = snapshotDoors.ToDictionary(s => s.TrackId, s => s);
            var currentDict = currentDoors.ToDictionary(d => d.LookupParameter("trackID").AsString(), d => d);

            // Find new doors (in current, not in snapshot)
            foreach (var door in currentDoors)
            {
                var trackId = door.LookupParameter("trackID").AsString();
                if (!snapshotDict.ContainsKey(trackId))
                {
                    result.NewEntities.Add(new Models.EntityChange
                    {
                        TrackId = trackId,
                        Identifier1 = door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                        Identifier2 = $"{door.Symbol?.Family?.Name}: {door.Symbol?.Name}",
                        ChangeType = "New"
                    });
                }
            }

            // Find deleted doors (in snapshot, not in current)
            foreach (var snapshot in snapshotDoors)
            {
                if (!currentDict.ContainsKey(snapshot.TrackId))
                {
                    result.DeletedEntities.Add(new Models.EntityChange
                    {
                        TrackId = snapshot.TrackId,
                        Identifier1 = snapshot.Mark,
                        Identifier2 = $"{snapshot.FamilyName}: {snapshot.TypeName}",
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
                        result.ModifiedEntities.Add(new Models.EntityChange
                        {
                            TrackId = trackId,
                            Identifier1 = door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                            Identifier2 = $"{door.Symbol?.Family?.Name}: {door.Symbol?.Name}",
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

        // OPTIMIZATION: Cache for GetOrderedParameters to avoid redundant API calls
        private Dictionary<ElementId, IList<Parameter>> _instanceParamCache = new Dictionary<ElementId, IList<Parameter>>();
        private Dictionary<ElementId, IList<Parameter>> _typeParamCache = new Dictionary<ElementId, IList<Parameter>>();

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

            // Get ONLY instance parameters using GetOrderedParameters (with caching)
            // Get ONLY instance parameters (not type parameters)
            // We'll add type parameters later only if snapshot has them
            if (!_instanceParamCache.TryGetValue(currentDoor.Id, out var orderedParams))
            {
                orderedParams = currentDoor.GetOrderedParameters();
                _instanceParamCache[currentDoor.Id] = orderedParams;
            }

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
                // Use cache for type parameters too
                if (!_typeParamCache.TryGetValue(currentDoor.Symbol.Id, out var orderedTypeParams))
                {
                    orderedTypeParams = currentDoor.Symbol.GetOrderedParameters();
                    _typeParamCache[currentDoor.Symbol.Id] = orderedTypeParams;
                }

                foreach (Parameter param in orderedTypeParams)
                {
                    AddParameterToDict(param, currentTypeParams, currentTypeParamsDisplay);
                }
            }

            // Add location information (same as in snapshot)
            // BUGFIX: Wrap in ParameterValue objects to match snapshot format
            var location = currentDoor.Location;
            if (location is LocationPoint locationPoint)
            {
                var point = locationPoint.Point;

                var locXValue = new Models.ParameterValue { StorageType = "Double", RawValue = point.X, DisplayValue = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, point.X, false), IsTypeParameter = false };
                currentParams["location_x"] = locXValue;
                currentParamsDisplay["location_x"] = locXValue.DisplayValue;

                var locYValue = new Models.ParameterValue { StorageType = "Double", RawValue = point.Y, DisplayValue = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, point.Y, false), IsTypeParameter = false };
                currentParams["location_y"] = locYValue;
                currentParamsDisplay["location_y"] = locYValue.DisplayValue;

                var locZValue = new Models.ParameterValue { StorageType = "Double", RawValue = point.Z, DisplayValue = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, point.Z, false), IsTypeParameter = false };
                currentParams["location_z"] = locZValue;
                currentParamsDisplay["location_z"] = locZValue.DisplayValue;

                var rotValue = new Models.ParameterValue { StorageType = "Double", RawValue = locationPoint.Rotation, DisplayValue = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Angle, locationPoint.Rotation, false), IsTypeParameter = false };
                currentParams["rotation"] = rotValue;
                currentParamsDisplay["rotation"] = rotValue.DisplayValue;
            }

            // Add facing and hand orientation (important for flip detection)
            if (currentDoor.FacingOrientation != null)
            {
                var facingXValue = new Models.ParameterValue { StorageType = "Double", RawValue = currentDoor.FacingOrientation.X, DisplayValue = currentDoor.FacingOrientation.X.ToString("F6"), IsTypeParameter = false };
                currentParams["facing_x"] = facingXValue;
                currentParamsDisplay["facing_x"] = facingXValue.DisplayValue;

                var facingYValue = new Models.ParameterValue { StorageType = "Double", RawValue = currentDoor.FacingOrientation.Y, DisplayValue = currentDoor.FacingOrientation.Y.ToString("F6"), IsTypeParameter = false };
                currentParams["facing_y"] = facingYValue;
                currentParamsDisplay["facing_y"] = facingYValue.DisplayValue;

                var facingZValue = new Models.ParameterValue { StorageType = "Double", RawValue = currentDoor.FacingOrientation.Z, DisplayValue = currentDoor.FacingOrientation.Z.ToString("F6"), IsTypeParameter = false };
                currentParams["facing_z"] = facingZValue;
                currentParamsDisplay["facing_z"] = facingZValue.DisplayValue;
            }
            if (currentDoor.HandOrientation != null)
            {
                var handXValue = new Models.ParameterValue { StorageType = "Double", RawValue = currentDoor.HandOrientation.X, DisplayValue = currentDoor.HandOrientation.X.ToString("F6"), IsTypeParameter = false };
                currentParams["hand_x"] = handXValue;
                currentParamsDisplay["hand_x"] = handXValue.DisplayValue;

                var handYValue = new Models.ParameterValue { StorageType = "Double", RawValue = currentDoor.HandOrientation.Y, DisplayValue = currentDoor.HandOrientation.Y.ToString("F6"), IsTypeParameter = false };
                currentParams["hand_y"] = handYValue;
                currentParamsDisplay["hand_y"] = handYValue.DisplayValue;

                var handZValue = new Models.ParameterValue { StorageType = "Double", RawValue = currentDoor.HandOrientation.Z, DisplayValue = currentDoor.HandOrientation.Z.ToString("F6"), IsTypeParameter = false };
                currentParams["hand_z"] = handZValue;
                currentParamsDisplay["hand_z"] = handZValue.DisplayValue;
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
                foreach (var kvp in snapshot.AllParameters)
                {
                    // Skip IFC-related parameters
                    if (kvp.Key.Contains("IFC", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Skip parameters that should be in dedicated columns
                    if (excludedFromJson.Contains(kvp.Key))
                        continue;

                    // Convert JSON objects to ParameterValue objects
                    var paramValue = Models.ParameterValue.FromJsonObject(kvp.Value);
                    if (paramValue == null)
                    {
                        // This should never happen with new snapshots - log error
                        System.Diagnostics.Debug.WriteLine($"ERROR: Failed to convert parameter '{kvp.Key}' to ParameterValue. Snapshot may be corrupted.");
                        continue;
                    }

                    snapshotParams[kvp.Key] = paramValue;

                    // Format display value based on parameter type
                    string formattedDisplay = paramValue.DisplayValue;
                    if (paramValue.StorageType == "Double")
                    {
                        var snapDoubleVal = Convert.ToDouble(paramValue.RawValue);

                        // Special formatting for location/rotation parameters
                        if (kvp.Key.StartsWith("location_"))
                        {
                            // Format as length with project units (e.g., "22.5 m")
                            formattedDisplay = UnitFormatUtils.Format(
                                currentDoor.Document.GetUnits(),
                                SpecTypeId.Length,
                                snapDoubleVal,
                                false);
                        }
                        else if (kvp.Key == "rotation")
                        {
                            // Format as angle with project units (e.g., "90°")
                            formattedDisplay = UnitFormatUtils.Format(
                                currentDoor.Document.GetUnits(),
                                SpecTypeId.Angle,
                                snapDoubleVal,
                                false);
                        }
                        else if (kvp.Key.StartsWith("facing_") || kvp.Key.StartsWith("hand_"))
                        {
                            // For orientation vectors, use 2 decimal places
                            formattedDisplay = snapDoubleVal.ToString("F2");
                        }
                    }

                    snapshotParamsDisplay[kvp.Key] = formattedDisplay;
                }
            }

            // Add from TypeParameters JSON (type parameters) for comparison
            if (snapshot.TypeParameters != null && snapshot.TypeParameters.Any())
            {
                // Add snapshot type parameters
                foreach (var kvp in snapshot.TypeParameters)
                {
                    // Convert JSON objects to ParameterValue objects
                    var paramValue = Models.ParameterValue.FromJsonObject(kvp.Value);
                    if (paramValue == null)
                    {
                        // This should never happen with new snapshots - log error
                        System.Diagnostics.Debug.WriteLine($"ERROR: Failed to convert type parameter '{kvp.Key}' to ParameterValue. Snapshot may be corrupted.");
                        continue;
                    }

                    snapshotParams[kvp.Key] = paramValue;
                    snapshotParamsDisplay[kvp.Key] = paramValue.DisplayValue;
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
            // BUGFIX: Wrap dedicated column values in ParameterValue objects to match current format

            // Mark parameter - include even if empty
            var markParam = currentDoor.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            if (markParam != null)
            {
                string markParamName = markParam.Definition.Name;
                var markValue = new Models.ParameterValue
                {
                    StorageType = StorageType.String.ToString(),
                    RawValue = snapshot.Mark ?? "",
                    DisplayValue = snapshot.Mark ?? "",
                    IsTypeParameter = false
                };
                snapshotParams[markParamName] = markValue;
                snapshotParamsDisplay[markParamName] = markValue.DisplayValue;
            }

            // Level parameter - only add if not empty (ElementId parameters are skipped when empty in AddParameterToDict)
            var levelParam = currentDoor.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
            if (levelParam != null && !string.IsNullOrEmpty(snapshot.Level))
            {
                string levelParamName = levelParam.Definition.Name;
                var levelValue = new Models.ParameterValue
                {
                    StorageType = StorageType.ElementId.ToString(),
                    RawValue = snapshot.Level,
                    DisplayValue = snapshot.Level,
                    IsTypeParameter = false
                };
                snapshotParams[levelParamName] = levelValue;
                snapshotParamsDisplay[levelParamName] = levelValue.DisplayValue;
            }

            // Fire Rating parameter - include even if empty (string parameter, not ElementId)
            var fireRatingParam = currentDoor.get_Parameter(BuiltInParameter.DOOR_FIRE_RATING);
            if (fireRatingParam != null)
            {
                string fireRatingParamName = fireRatingParam.Definition.Name;
                var fireRatingValue = new Models.ParameterValue
                {
                    StorageType = StorageType.String.ToString(),
                    RawValue = snapshot.FireRating ?? "",
                    DisplayValue = snapshot.FireRating ?? "",
                    IsTypeParameter = false
                };
                snapshotParams[fireRatingParamName] = fireRatingValue;
                snapshotParamsDisplay[fireRatingParamName] = fireRatingValue.DisplayValue;
            }

            // Comments parameter - include even if empty (string parameter, not ElementId)
            var commentsParam = currentDoor.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (commentsParam != null)
            {
                string commentsParamName = commentsParam.Definition.Name;
                var commentsValue = new Models.ParameterValue
                {
                    StorageType = StorageType.String.ToString(),
                    RawValue = snapshot.Comments ?? "",
                    DisplayValue = snapshot.Comments ?? "",
                    IsTypeParameter = false
                };
                snapshotParams[commentsParamName] = commentsValue;
                snapshotParamsDisplay[commentsParamName] = commentsValue.DisplayValue;
            }

            // Phase Created parameter - only add if not empty (ElementId parameters are skipped when empty in AddParameterToDict)
            var phaseCreatedParam = currentDoor.get_Parameter(BuiltInParameter.PHASE_CREATED);
            if (phaseCreatedParam != null && !string.IsNullOrEmpty(snapshot.PhaseCreated))
            {
                string phaseCreatedParamName = phaseCreatedParam.Definition.Name;
                var phaseCreatedValue = new Models.ParameterValue
                {
                    StorageType = StorageType.ElementId.ToString(),
                    RawValue = snapshot.PhaseCreated,
                    DisplayValue = snapshot.PhaseCreated,
                    IsTypeParameter = false
                };
                snapshotParams[phaseCreatedParamName] = phaseCreatedValue;
                snapshotParamsDisplay[phaseCreatedParamName] = phaseCreatedValue.DisplayValue;
            }

            // Phase Demolished parameter - only add if not empty (ElementId parameters are skipped when empty in AddParameterToDict)
            var phaseDemolishedParam = currentDoor.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
            if (phaseDemolishedParam != null && !string.IsNullOrEmpty(snapshot.PhaseDemolished))
            {
                string phaseDemolishedParamName = phaseDemolishedParam.Definition.Name;
                var phaseDemolishedValue = new Models.ParameterValue
                {
                    StorageType = StorageType.ElementId.ToString(),
                    RawValue = snapshot.PhaseDemolished,
                    DisplayValue = snapshot.PhaseDemolished,
                    IsTypeParameter = false
                };
                snapshotParams[phaseDemolishedParamName] = phaseDemolishedValue;
                snapshotParamsDisplay[phaseDemolishedParamName] = phaseDemolishedValue.DisplayValue;
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
                    // BUGFIX: Safe dictionary access to prevent KeyNotFoundException
                    var snapDisplay = snapshotParamsDisplay.ContainsKey(snapshotParam.Key)
                        ? snapshotParamsDisplay[snapshotParam.Key]
                        : snapshotParam.Value?.ToString() ?? "";
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
                    // BUGFIX: Skip EDITED_BY parameter (excluded from snapshots, covers all languages)
                    var paramNameLower = currentParam.Key.ToLower();
                    if (paramNameLower.Contains("modifié par") ||
                        paramNameLower.Contains("edited by") ||
                        paramNameLower.Contains("modified by"))
                        continue;

                    // BUGFIX: Safe dictionary access to prevent KeyNotFoundException
                    var currDisplay = currentParamsDisplay.ContainsKey(currentParam.Key)
                        ? currentParamsDisplay[currentParam.Key]
                        : currentParam.Value?.ToString() ?? "";
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

            // NEW: Use ParameterValue class for type-safe storage and comparison
            var paramValue = Models.ParameterValue.FromRevitParameter(param);
            if (paramValue != null)
            {
                values[paramName] = paramValue;
                display[paramName] = paramValue.DisplayValue;
            }
        }

        private bool CompareValues(object snapValue, object currentValue)
        {
            // NEW: Type-safe comparison using ParameterValue objects
            if (snapValue is Models.ParameterValue snapParam && currentValue is Models.ParameterValue currParam)
            {
                return !snapParam.IsEqualTo(currParam);
            }

            // If we get here, something is wrong - log warning but don't show dialog
            System.Diagnostics.Debug.WriteLine($"WARNING: Non-ParameterValue objects! Snap: {snapValue?.GetType().Name}, Curr: {currentValue?.GetType().Name}");
            return true; // Treat as different
        }
    }
}
