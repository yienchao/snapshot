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
    public class ElementCompareCommand : IExternalCommand
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
            List<ElementSnapshot> versionSnapshots = new List<ElementSnapshot>();

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await supabaseService.InitializeAsync();
                    versionSnapshots = await supabaseService.GetAllElementVersionsWithInfoAsync(projectId);
                }).Wait();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to load versions:\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            if (!versionSnapshots.Any())
            {
                TaskDialog.Show("No Versions", "No element snapshots found in Supabase. Create a snapshot first.");
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
                "Choose a snapshot version to compare with current elements:");
            var dialogResult = selectionWindow.ShowDialog();

            if (dialogResult != true)
                return Result.Cancelled;

            string selectedVersion = selectionWindow.SelectedVersionName;
            if (string.IsNullOrWhiteSpace(selectedVersion))
                return Result.Cancelled;

            // 4. Get snapshot elements from Supabase
            List<ElementSnapshot> snapshotElements = new List<ElementSnapshot>();
            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    snapshotElements = await supabaseService.GetElementsByVersionAsync(selectedVersion, projectId);
                }).Wait();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to load snapshot:\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            // 5. Get current elements from Revit - check for pre-selection first
            var uiDoc = commandData.Application.ActiveUIDocument;
            var selectedIds = uiDoc.Selection.GetElementIds();

            List<FamilyInstance> currentElements;

            // Check if user has pre-selected elements
            if (selectedIds.Any())
            {
                // Use only selected elements (excluding doors) that have trackID
                currentElements = selectedIds
                    .Select(id => doc.GetElement(id))
                    .OfType<FamilyInstance>()
                    .Where(e => e.Category != null && e.Category.Id.Value != (int)BuiltInCategory.OST_Doors &&
                               e.LookupParameter("trackID") != null &&
                               !string.IsNullOrWhiteSpace(e.LookupParameter("trackID").AsString()))
                    .ToList();

                if (!currentElements.Any())
                {
                    TaskDialog.Show("No Valid Elements Selected",
                        "None of the selected elements have trackID.\n\n" +
                        "Please select elements with trackID parameter, or run without selection to compare all elements.");
                    return Result.Cancelled;
                }

                // Inform user about selection
                TaskDialog.Show("Using Selection",
                    $"Comparing {currentElements.Count} pre-selected element(s) against version '{selectedVersion}'.");
            }
            else
            {
                // No selection - use all elements with trackID
                currentElements = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(e => e.Category != null && e.Category.Id.Value != (int)BuiltInCategory.OST_Doors &&
                               e.LookupParameter("trackID") != null &&
                               !string.IsNullOrWhiteSpace(e.LookupParameter("trackID").AsString()))
                    .ToList();

                if (!currentElements.Any())
                {
                    TaskDialog.Show("No Elements Found", "No elements with trackID parameter found in the model.");
                    return Result.Cancelled;
                }
            }

            // 6. Filter snapshot to only include elements we're comparing
            List<ElementSnapshot> filteredSnapshotElements;
            if (selectedIds.Any())
            {
                // Get trackIDs of selected elements
                var selectedTrackIds = currentElements.Select(e => e.LookupParameter("trackID").AsString()).ToHashSet();

                // Only include snapshot elements that match selected trackIDs
                filteredSnapshotElements = snapshotElements.Where(s => selectedTrackIds.Contains(s.TrackId)).ToList();
            }
            else
            {
                // No selection - use all snapshot elements
                filteredSnapshotElements = snapshotElements;
            }

            // 7. Compare
            var comparison = CompareElements(currentElements, filteredSnapshotElements, doc);

            // 8. Show results using unified helper
            Helpers.ComparisonHelper.ShowComparisonResults(comparison, selectedVersion, "ELEMENTS");

            return Result.Succeeded;
        }

        private Models.ComparisonResult<Models.EntityChange> CompareElements(List<FamilyInstance> currentElements, List<ElementSnapshot> snapshotElements, Document doc)
        {
            var result = new Models.ComparisonResult<Models.EntityChange>();
            var snapshotDict = snapshotElements.ToDictionary(s => s.TrackId, s => s);
            var currentDict = currentElements.ToDictionary(e => e.LookupParameter("trackID").AsString(), e => e);

            // Find new elements (in current, not in snapshot)
            foreach (var element in currentElements)
            {
                var trackId = element.LookupParameter("trackID").AsString();
                if (!snapshotDict.ContainsKey(trackId))
                {
                    result.NewEntities.Add(new Models.EntityChange
                    {
                        TrackId = trackId,
                        Identifier1 = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                        Identifier2 = $"{element.Symbol?.Family?.Name}: {element.Symbol?.Name}",
                        ChangeType = "New"
                    });
                }
            }

            // Find deleted elements (in snapshot, not in current)
            foreach (var snapshot in snapshotElements)
            {
                if (!currentDict.ContainsKey(snapshot.TrackId))
                {
                    // REFACTORED: Get Family/Type from JSON
                    string familyName = GetElementParameterValue(snapshot, new[] { "Famille", "Family" });
                    string typeName = GetElementParameterValue(snapshot, new[] { "Type" });

                    result.DeletedEntities.Add(new Models.EntityChange
                    {
                        TrackId = snapshot.TrackId,
                        Identifier1 = snapshot.Mark,
                        Identifier2 = $"{familyName}: {typeName}",
                        ChangeType = "Deleted"
                    });
                }
            }

            // Find modified elements
            foreach (var element in currentElements)
            {
                var trackId = element.LookupParameter("trackID").AsString();
                if (snapshotDict.TryGetValue(trackId, out var snapshot))
                {
                    var (allChanges, instanceChanges, typeChanges) = GetParameterChanges(element, snapshot, doc);
                    if (allChanges.Any())
                    {
                        result.ModifiedEntities.Add(new Models.EntityChange
                        {
                            TrackId = trackId,
                            Identifier1 = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                            Identifier2 = $"{element.Symbol?.Family?.Name}: {element.Symbol?.Name}",
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

        private (List<string> allChanges, List<string> instanceChanges, List<string> typeChanges) GetParameterChanges(FamilyInstance currentElement, ElementSnapshot snapshot, Document doc)
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

            // Get all current element parameters (user-visible only)
            var currentParams = new Dictionary<string, object>();
            var currentParamsDisplay = new Dictionary<string, string>();

            // Get ONLY instance parameters using GetOrderedParameters (with caching)
            if (!_instanceParamCache.TryGetValue(currentElement.Id, out var orderedParams))
            {
                orderedParams = currentElement.GetOrderedParameters();
                _instanceParamCache[currentElement.Id] = orderedParams;
            }

            foreach (Parameter param in orderedParams)
            {
                // Skip type parameters - only collect instance parameters here
                // Check if the parameter belongs to this element instance (not its Symbol/Type)
                if (param.Element.Id != currentElement.Id)
                    continue;

                AddParameterToDict(param, currentParams, currentParamsDisplay);
            }

            // Also collect type parameters from current element (for comparison with snapshot type parameters)
            var currentTypeParams = new Dictionary<string, object>();
            var currentTypeParamsDisplay = new Dictionary<string, string>();
            if (currentElement.Symbol != null)
            {
                // Use cache for type parameters too
                if (!_typeParamCache.TryGetValue(currentElement.Symbol.Id, out var orderedTypeParams))
                {
                    orderedTypeParams = currentElement.Symbol.GetOrderedParameters();
                    _typeParamCache[currentElement.Symbol.Id] = orderedTypeParams;
                }

                foreach (Parameter param in orderedTypeParams)
                {
                    AddParameterToDict(param, currentTypeParams, currentTypeParamsDisplay);
                }
            }

            // Add location information (same as in snapshot)
            // BUGFIX: Wrap in ParameterValue objects to match snapshot format
            var location = currentElement.Location;
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
            else if (location is LocationCurve locationCurve)
            {
                var curve = locationCurve.Curve;
                var startPoint = curve.GetEndPoint(0);
                var endPoint = curve.GetEndPoint(1);

                var startXValue = new Models.ParameterValue { StorageType = "Double", RawValue = startPoint.X, DisplayValue = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, startPoint.X, false), IsTypeParameter = false };
                currentParams["location_start_x"] = startXValue;
                currentParamsDisplay["location_start_x"] = startXValue.DisplayValue;

                var startYValue = new Models.ParameterValue { StorageType = "Double", RawValue = startPoint.Y, DisplayValue = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, startPoint.Y, false), IsTypeParameter = false };
                currentParams["location_start_y"] = startYValue;
                currentParamsDisplay["location_start_y"] = startYValue.DisplayValue;

                var startZValue = new Models.ParameterValue { StorageType = "Double", RawValue = startPoint.Z, DisplayValue = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, startPoint.Z, false), IsTypeParameter = false };
                currentParams["location_start_z"] = startZValue;
                currentParamsDisplay["location_start_z"] = startZValue.DisplayValue;

                var endXValue = new Models.ParameterValue { StorageType = "Double", RawValue = endPoint.X, DisplayValue = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, endPoint.X, false), IsTypeParameter = false };
                currentParams["location_end_x"] = endXValue;
                currentParamsDisplay["location_end_x"] = endXValue.DisplayValue;

                var endYValue = new Models.ParameterValue { StorageType = "Double", RawValue = endPoint.Y, DisplayValue = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, endPoint.Y, false), IsTypeParameter = false };
                currentParams["location_end_y"] = endYValue;
                currentParamsDisplay["location_end_y"] = endYValue.DisplayValue;

                var endZValue = new Models.ParameterValue { StorageType = "Double", RawValue = endPoint.Z, DisplayValue = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, endPoint.Z, false), IsTypeParameter = false };
                currentParams["location_end_z"] = endZValue;
                currentParamsDisplay["location_end_z"] = endZValue.DisplayValue;
            }

            // Add facing and hand orientation (important for flip detection)
            if (currentElement.FacingOrientation != null)
            {
                var facingXValue = new Models.ParameterValue { StorageType = "Double", RawValue = currentElement.FacingOrientation.X, DisplayValue = currentElement.FacingOrientation.X.ToString("F6"), IsTypeParameter = false };
                currentParams["facing_x"] = facingXValue;
                currentParamsDisplay["facing_x"] = facingXValue.DisplayValue;

                var facingYValue = new Models.ParameterValue { StorageType = "Double", RawValue = currentElement.FacingOrientation.Y, DisplayValue = currentElement.FacingOrientation.Y.ToString("F6"), IsTypeParameter = false };
                currentParams["facing_y"] = facingYValue;
                currentParamsDisplay["facing_y"] = facingYValue.DisplayValue;

                var facingZValue = new Models.ParameterValue { StorageType = "Double", RawValue = currentElement.FacingOrientation.Z, DisplayValue = currentElement.FacingOrientation.Z.ToString("F6"), IsTypeParameter = false };
                currentParams["facing_z"] = facingZValue;
                currentParamsDisplay["facing_z"] = facingZValue.DisplayValue;
            }
            if (currentElement.HandOrientation != null)
            {
                var handXValue = new Models.ParameterValue { StorageType = "Double", RawValue = currentElement.HandOrientation.X, DisplayValue = currentElement.HandOrientation.X.ToString("F6"), IsTypeParameter = false };
                currentParams["hand_x"] = handXValue;
                currentParamsDisplay["hand_x"] = handXValue.DisplayValue;

                var handYValue = new Models.ParameterValue { StorageType = "Double", RawValue = currentElement.HandOrientation.Y, DisplayValue = currentElement.HandOrientation.Y.ToString("F6"), IsTypeParameter = false };
                currentParams["hand_y"] = handYValue;
                currentParamsDisplay["hand_y"] = handYValue.DisplayValue;

                var handZValue = new Models.ParameterValue { StorageType = "Double", RawValue = currentElement.HandOrientation.Z, DisplayValue = currentElement.HandOrientation.Z.ToString("F6"), IsTypeParameter = false };
                currentParams["hand_z"] = handZValue;
                currentParamsDisplay["hand_z"] = handZValue.DisplayValue;
            }

            // Add Category from current element (Category is NOT a parameter, it's a property)
            // Mark and Level are now captured via GetOrderedParameters (they're in AllParameters JSON)
            var currentCategoryValue = new Models.ParameterValue
            {
                StorageType = StorageType.String.ToString(),
                RawValue = currentElement.Category?.Name ?? "",
                DisplayValue = currentElement.Category?.Name ?? "",
                IsTypeParameter = false
            };
            currentParams["Category"] = currentCategoryValue;
            currentParamsDisplay["Category"] = currentCategoryValue.DisplayValue;

            // Build snapshot parameters dictionary
            var snapshotParams = new Dictionary<string, object>();
            var snapshotParamsDisplay = new Dictionary<string, string>();

            // Mark and Level are now in AllParameters JSON, only Category needs special handling
            var excludedFromJson = new HashSet<string>
            {
                "Category", "Catégorie"  // Category is NOT a parameter, added manually above
            };

            // Add from AllParameters JSON
            if (snapshot.AllParameters != null)
            {
                foreach (var kvp in snapshot.AllParameters)
                {
                    // Skip IFC-related parameters
                    if (kvp.Key.Contains("IFC", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Skip Category (not a parameter)
                    if (excludedFromJson.Contains(kvp.Key))
                        continue;

                    // Skip location/rotation from ALL snapshots (creates false positives when elements move)
                    // But KEEP facing and hand orientation - they're important for fixture orientation (like doors)
                    if (kvp.Key.Equals("host_id", StringComparison.OrdinalIgnoreCase) ||
                        kvp.Key.Equals("host_category", StringComparison.OrdinalIgnoreCase) ||
                        kvp.Key.StartsWith("location_", StringComparison.OrdinalIgnoreCase) ||
                        kvp.Key.Equals("rotation", StringComparison.OrdinalIgnoreCase))
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
                    snapshotParamsDisplay[kvp.Key] = paramValue.DisplayValue;
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

            // Add Category from snapshot (Category is NOT a parameter, stored as dedicated column)
            // Mark and Level are now in AllParameters JSON from snapshot
            if (!string.IsNullOrEmpty(snapshot.Category))
            {
                var categoryValue = new Models.ParameterValue
                {
                    StorageType = StorageType.String.ToString(),
                    RawValue = snapshot.Category,
                    DisplayValue = snapshot.Category,
                    IsTypeParameter = false
                };
                snapshotParams["Category"] = categoryValue;
                snapshotParamsDisplay["Category"] = categoryValue.DisplayValue;
            }

            // REFACTORED: Family and Type are now in AllParameters/TypeParameters JSON (no longer dedicated columns)
            // They will be compared automatically as part of parameter comparison above

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
                    // Skip location/rotation parameters - they're filtered from snapshots so would show as "new"
                    // But DON'T skip facing and hand - we want to track those changes
                    if (currentParam.Key.Equals("host_id", StringComparison.OrdinalIgnoreCase) ||
                        currentParam.Key.Equals("host_category", StringComparison.OrdinalIgnoreCase) ||
                        currentParam.Key.StartsWith("location_", StringComparison.OrdinalIgnoreCase) ||
                        currentParam.Key.Equals("rotation", StringComparison.OrdinalIgnoreCase))
                        continue;

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

            // Mark and Level are now included in AllParameters JSON for comparison
            // NOTE: Variantes is now included in comparison (but won't be restorable - read-only)

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

            // If we get here, something is wrong
            System.Diagnostics.Debug.WriteLine($"WARNING: CompareValues received non-ParameterValue objects");
            return true; // Treat as different
        }

        // Helper method to convert parameter values from internal units to display units (no unit symbols)
        private string GetElementParameterValue(ElementSnapshot snapshot, string[] possibleKeys)
        {
            // Try AllParameters first
            if (snapshot.AllParameters != null)
            {
                foreach (var key in possibleKeys)
                {
                    if (snapshot.AllParameters.TryGetValue(key, out object value))
                    {
                        var paramVal = Models.ParameterValue.FromJsonObject(value);
                        return paramVal?.DisplayValue ?? "";
                    }
                }
            }

            // Try TypeParameters
            if (snapshot.TypeParameters != null)
            {
                foreach (var key in possibleKeys)
                {
                    if (snapshot.TypeParameters.TryGetValue(key, out object value))
                    {
                        var paramVal = Models.ParameterValue.FromJsonObject(value);
                        return paramVal?.DisplayValue ?? "";
                    }
                }
            }

            return "";
        }

    }
}
