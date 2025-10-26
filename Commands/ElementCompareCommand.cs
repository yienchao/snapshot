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

            var selectionWindow = new VersionSelectionWindow(versionInfos);
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
            ElementComparisonResult comparison = CompareElements(currentElements, filteredSnapshotElements, doc);

            // 8. Show results
            ShowComparisonResults(comparison, selectedVersion, commandData.Application);

            return Result.Succeeded;
        }

        private ElementComparisonResult CompareElements(List<FamilyInstance> currentElements, List<ElementSnapshot> snapshotElements, Document doc)
        {
            var result = new ElementComparisonResult();
            var snapshotDict = snapshotElements.ToDictionary(s => s.TrackId, s => s);
            var currentDict = currentElements.ToDictionary(e => e.LookupParameter("trackID").AsString(), e => e);

            // Find new elements (in current, not in snapshot)
            foreach (var element in currentElements)
            {
                var trackId = element.LookupParameter("trackID").AsString();
                if (!snapshotDict.ContainsKey(trackId))
                {
                    result.NewElements.Add(new ElementChange
                    {
                        TrackId = trackId,
                        Category = element.Category?.Name,
                        Mark = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                        FamilyName = element.Symbol?.Family?.Name,
                        TypeName = element.Symbol?.Name,
                        ChangeType = "New"
                    });
                }
            }

            // Find deleted elements (in snapshot, not in current)
            foreach (var snapshot in snapshotElements)
            {
                if (!currentDict.ContainsKey(snapshot.TrackId))
                {
                    result.DeletedElements.Add(new ElementChange
                    {
                        TrackId = snapshot.TrackId,
                        Category = snapshot.Category,
                        Mark = snapshot.Mark,
                        FamilyName = snapshot.FamilyName,
                        TypeName = snapshot.TypeName,
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
                        result.ModifiedElements.Add(new ElementChange
                        {
                            TrackId = trackId,
                            Category = element.Category?.Name,
                            Mark = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                            FamilyName = element.Symbol?.Family?.Name,
                            TypeName = element.Symbol?.Name,
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

            // Get ONLY instance parameters using GetOrderedParameters
            var orderedParams = currentElement.GetOrderedParameters();
            foreach (Parameter param in orderedParams)
            {
                // Skip type parameters - only collect instance parameters here
                if (param.Element is ElementType)
                    continue;

                AddParameterToDict(param, currentParams, currentParamsDisplay);
            }

            // Also collect type parameters from current element (for comparison with snapshot type parameters)
            var currentTypeParams = new Dictionary<string, object>();
            var currentTypeParamsDisplay = new Dictionary<string, string>();
            if (currentElement.Symbol != null)
            {
                var orderedTypeParams = currentElement.Symbol.GetOrderedParameters();
                foreach (Parameter param in orderedTypeParams)
                {
                    AddParameterToDict(param, currentTypeParams, currentTypeParamsDisplay);
                }
            }

            // Add location information (same as in snapshot)
            var location = currentElement.Location;
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
            else if (location is LocationCurve locationCurve)
            {
                var curve = locationCurve.Curve;
                var startPoint = curve.GetEndPoint(0);
                var endPoint = curve.GetEndPoint(1);
                currentParams["location_start_x"] = startPoint.X;
                currentParamsDisplay["location_start_x"] = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, startPoint.X, false);
                currentParams["location_start_y"] = startPoint.Y;
                currentParamsDisplay["location_start_y"] = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, startPoint.Y, false);
                currentParams["location_start_z"] = startPoint.Z;
                currentParamsDisplay["location_start_z"] = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, startPoint.Z, false);
                currentParams["location_end_x"] = endPoint.X;
                currentParamsDisplay["location_end_x"] = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, endPoint.X, false);
                currentParams["location_end_y"] = endPoint.Y;
                currentParamsDisplay["location_end_y"] = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, endPoint.Y, false);
                currentParams["location_end_z"] = endPoint.Z;
                currentParamsDisplay["location_end_z"] = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, endPoint.Z, false);
            }

            // Add facing and hand orientation (important for flip detection)
            if (currentElement.FacingOrientation != null)
            {
                currentParams["facing_x"] = currentElement.FacingOrientation.X;
                currentParamsDisplay["facing_x"] = currentElement.FacingOrientation.X.ToString("F6");
                currentParams["facing_y"] = currentElement.FacingOrientation.Y;
                currentParamsDisplay["facing_y"] = currentElement.FacingOrientation.Y.ToString("F6");
                currentParams["facing_z"] = currentElement.FacingOrientation.Z;
                currentParamsDisplay["facing_z"] = currentElement.FacingOrientation.Z.ToString("F6");
            }
            if (currentElement.HandOrientation != null)
            {
                currentParams["hand_x"] = currentElement.HandOrientation.X;
                currentParamsDisplay["hand_x"] = currentElement.HandOrientation.X.ToString("F6");
                currentParams["hand_y"] = currentElement.HandOrientation.Y;
                currentParamsDisplay["hand_y"] = currentElement.HandOrientation.Y.ToString("F6");
                currentParams["hand_z"] = currentElement.HandOrientation.Z;
                currentParamsDisplay["hand_z"] = currentElement.HandOrientation.Z.ToString("F6");
            }

            // Build snapshot parameters dictionary
            var snapshotParams = new Dictionary<string, object>();
            var snapshotParamsDisplay = new Dictionary<string, string>();

            // Parameters that should NOT be in all_parameters (they're in dedicated columns)
            var excludedFromJson = new HashSet<string>
            {
                "Mark", "Marque", "Identifiant",  // Mark parameter (various languages)
                "Level", "Niveau",
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

                    // Skip location/rotation from ALL snapshots (creates false positives when elements move)
                    // But KEEP facing and hand orientation - they're important for fixture orientation (like doors)
                    if (kvp.Key.Equals("host_id", StringComparison.OrdinalIgnoreCase) ||
                        kvp.Key.Equals("host_category", StringComparison.OrdinalIgnoreCase) ||
                        kvp.Key.StartsWith("location_", StringComparison.OrdinalIgnoreCase) ||
                        kvp.Key.Equals("rotation", StringComparison.OrdinalIgnoreCase))
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
                                    currentElement.Document.GetUnits(),
                                    SpecTypeId.Length,
                                    doubleVal,
                                    false);
                            }
                            // For rotation: show in degrees or project angle units
                            else if (kvp.Key == "rotation")
                            {
                                displayValue = UnitFormatUtils.Format(
                                    currentElement.Document.GetUnits(),
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
                            var currentParam = currentElement.LookupParameter(kvp.Key);
                            if (currentParam != null && currentParam.StorageType == StorageType.Double)
                            {
                                // Format with units, then strip the unit label
                                string formatted = UnitFormatUtils.Format(
                                    currentElement.Document.GetUnits(),
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
                        // Try to get the parameter from the element's type to format it properly
                        Parameter currentParam = null;
                        if (currentElement.Symbol != null)
                        {
                            currentParam = currentElement.Symbol.LookupParameter(kvp.Key);
                        }

                        if (currentParam != null && currentParam.StorageType == StorageType.Double)
                        {
                            string formatted = UnitFormatUtils.Format(
                                currentElement.Document.GetUnits(),
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
            // IMPORTANT: Use the actual parameter name from the current element (language-independent)
            // not hardcoded English names, to avoid false "(new)" and "(removed)" changes

            // Mark parameter - include even if empty
            var markParam = currentElement.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            if (markParam != null)
            {
                string markParamName = markParam.Definition.Name;
                snapshotParams[markParamName] = snapshot.Mark ?? "";
                snapshotParamsDisplay[markParamName] = snapshot.Mark ?? "";
            }

            // Level parameter - only add if not empty (ElementId parameters are skipped when empty in AddParameterToDict)
            var levelParam = currentElement.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
            if (levelParam != null && !string.IsNullOrEmpty(snapshot.Level))
            {
                string levelParamName = levelParam.Definition.Name;
                snapshotParams[levelParamName] = snapshot.Level;
                snapshotParamsDisplay[levelParamName] = snapshot.Level;
            }

            // Comments parameter - include even if empty
            var commentsParam = currentElement.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (commentsParam != null)
            {
                string commentsParamName = commentsParam.Definition.Name;
                snapshotParams[commentsParamName] = snapshot.Comments ?? "";
                snapshotParamsDisplay[commentsParamName] = snapshot.Comments ?? "";
            }

            // Phase Created parameter - only add if not empty (ElementId parameters are skipped when empty in AddParameterToDict)
            var phaseCreatedParam = currentElement.get_Parameter(BuiltInParameter.PHASE_CREATED);
            if (phaseCreatedParam != null && !string.IsNullOrEmpty(snapshot.PhaseCreated))
            {
                string phaseCreatedParamName = phaseCreatedParam.Definition.Name;
                snapshotParams[phaseCreatedParamName] = snapshot.PhaseCreated;
                snapshotParamsDisplay[phaseCreatedParamName] = snapshot.PhaseCreated;
            }

            // Phase Demolished parameter - only add if not empty (ElementId parameters are skipped when empty in AddParameterToDict)
            var phaseDemolishedParam = currentElement.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
            if (phaseDemolishedParam != null && !string.IsNullOrEmpty(snapshot.PhaseDemolished))
            {
                string phaseDemolishedParamName = phaseDemolishedParam.Definition.Name;
                snapshotParams[phaseDemolishedParamName] = snapshot.PhaseDemolished;
                snapshotParamsDisplay[phaseDemolishedParamName] = snapshot.PhaseDemolished;
            }

            // Note: Family and Type are NOT in AllParameters, but we need to compare them
            // to detect when an element changes to a different type

            // Compare Family
            string currentFamily = currentElement.Symbol?.Family?.Name ?? "";
            string snapshotFamily = snapshot.FamilyName ?? "";
            if (currentFamily != snapshotFamily)
            {
                string change = $"Family: '{snapshotFamily}' → '{currentFamily}'";
                changes.Add(change);
                instanceChanges.Add(change); // Categorize family changes as instance changes
            }

            // Compare Type
            string currentType = currentElement.Symbol?.Name ?? "";
            string snapshotType = snapshot.TypeName ?? "";
            if (currentType != snapshotType)
            {
                string change = $"Type: '{snapshotType}' → '{currentType}'";
                changes.Add(change);
                typeChanges.Add(change); // Categorize type changes as type changes
            }

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
                    // Skip location/rotation parameters - they're filtered from snapshots so would show as "new"
                    // But DON'T skip facing and hand - we want to track those changes
                    if (currentParam.Key.Equals("host_id", StringComparison.OrdinalIgnoreCase) ||
                        currentParam.Key.Equals("host_category", StringComparison.OrdinalIgnoreCase) ||
                        currentParam.Key.StartsWith("location_", StringComparison.OrdinalIgnoreCase) ||
                        currentParam.Key.Equals("rotation", StringComparison.OrdinalIgnoreCase))
                        continue;

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

        // Helper method to convert parameter values from internal units to display units (no unit symbols)
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

        private void ShowComparisonResults(ElementComparisonResult result, string versionName, UIApplication uiApp)
        {
            int totalChanges = result.NewElements.Count + result.DeletedElements.Count + result.ModifiedElements.Count;

            if (totalChanges == 0)
            {
                TaskDialog.Show("No Changes", $"No changes detected compared to version '{versionName}'.");
                return;
            }

            // Build ViewModel (reusing room view model structure)
            var viewModel = new ComparisonResultViewModel
            {
                VersionName = versionName,
                VersionInfo = $"Element Comparison | Version: {versionName} | Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                EntityTypeLabel = "ELEMENTS",
                NewRoomsCount = result.NewElements.Count,
                ModifiedRoomsCount = result.ModifiedElements.Count,
                DeletedRoomsCount = result.DeletedElements.Count
            };

            // Convert to display models (reusing room display structure)
            var displayItems = new List<RoomChangeDisplay>();

            foreach (var element in result.NewElements)
            {
                displayItems.Add(new RoomChangeDisplay
                {
                    ChangeType = "New",
                    TrackId = element.TrackId,
                    RoomNumber = $"{element.Category}: {element.Mark}",
                    RoomName = $"{element.FamilyName}: {element.TypeName}",
                    Changes = new List<string>()
                });
            }

            foreach (var element in result.ModifiedElements)
            {
                displayItems.Add(new RoomChangeDisplay
                {
                    ChangeType = "Modified",
                    TrackId = element.TrackId,
                    RoomNumber = $"{element.Category}: {element.Mark}",
                    RoomName = $"{element.FamilyName}: {element.TypeName}",
                    Changes = element.Changes,
                    InstanceParameterChanges = element.InstanceParameterChanges,
                    TypeParameterChanges = element.TypeParameterChanges
                });
            }

            foreach (var element in result.DeletedElements)
            {
                displayItems.Add(new RoomChangeDisplay
                {
                    ChangeType = "Deleted",
                    TrackId = element.TrackId,
                    RoomNumber = $"{element.Category}: {element.Mark}",
                    RoomName = $"{element.FamilyName}: {element.TypeName}",
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
    public class ElementComparisonResult
    {
        public List<ElementChange> NewElements { get; set; } = new List<ElementChange>();
        public List<ElementChange> DeletedElements { get; set; } = new List<ElementChange>();
        public List<ElementChange> ModifiedElements { get; set; } = new List<ElementChange>();
    }

    public class ElementChange
    {
        public string TrackId { get; set; }
        public string Category { get; set; }
        public string Mark { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public string ChangeType { get; set; }
        public List<string> Changes { get; set; } = new List<string>();
        public List<string> InstanceParameterChanges { get; set; } = new List<string>();
        public List<string> TypeParameterChanges { get; set; } = new List<string>();
    }
}
