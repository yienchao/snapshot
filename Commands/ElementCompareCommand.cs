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
                    var changes = GetParameterChanges(element, snapshot, doc);
                    if (changes.Any())
                    {
                        result.ModifiedElements.Add(new ElementChange
                        {
                            TrackId = trackId,
                            Category = element.Category?.Name,
                            Mark = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                            FamilyName = element.Symbol?.Family?.Name,
                            TypeName = element.Symbol?.Name,
                            ChangeType = "Modified",
                            Changes = changes
                        });
                    }
                }
            }

            return result;
        }

        private List<string> GetParameterChanges(FamilyInstance currentElement, ElementSnapshot snapshot, Document doc)
        {
            var changes = new List<string>();

            // Get all current element parameters (both instance and type, user-visible only)
            var currentParams = new Dictionary<string, object>();
            var currentParamsDisplay = new Dictionary<string, string>();

            // Get instance parameters using GetOrderedParameters
            var orderedInstanceParams = currentElement.GetOrderedParameters();
            foreach (Parameter param in orderedInstanceParams)
            {
                AddParameterToDict(param, currentParams, currentParamsDisplay);
            }

            // Get type parameters using GetOrderedParameters
            if (currentElement.Symbol != null)
            {
                var orderedTypeParams = currentElement.Symbol.GetOrderedParameters();
                foreach (Parameter param in orderedTypeParams)
                {
                    if (!currentParams.ContainsKey(param.Definition.Name))
                    {
                        AddParameterToDict(param, currentParams, currentParamsDisplay);
                    }
                }
            }

            // Add location information (same as in snapshot)
            var location = currentElement.Location;
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
                "Category", "Catégorie",
                "Family", "Famille",
                "Type",
                "Mark", "Marque",
                "Level", "Niveau",
                "Phase Created", "Phase de création",
                "Phase Demolished", "Phase de démolition",
                "Comments", "Commentaires"
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
            // These parameters (Category, Family, Type, Mark, Level, Comments, etc.) are excluded from both
            // current element parameters and snapshot parameters to avoid false change detection

            // Compare Family and Type to detect type changes
            string currentFamily = currentElement.Symbol?.Family?.Name ?? "";
            string snapshotFamily = snapshot.FamilyName ?? "";
            if (currentFamily != snapshotFamily)
            {
                changes.Add($"Family: '{snapshotFamily}' → '{currentFamily}'");
            }

            string currentType = currentElement.Symbol?.Name ?? "";
            string snapshotType = snapshot.TypeName ?? "";
            if (currentType != snapshotType)
            {
                changes.Add($"Type: '{snapshotType}' → '{currentType}'");
            }

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
                "Category", "Catégorie",
                "Family", "Famille",
                "Type",
                "Mark", "Marque",
                "Level", "Niveau",
                "Phase Created", "Phase de création",
                "Phase Demolished", "Phase de démolition",
                "Comments", "Commentaires"
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
                    // Include ALL string parameters, even empty ones
                    // Users may want to compare empty values or detect when values are cleared
                    var stringValue = param.AsString();
                    paramValue = stringValue ?? "";
                    displayValue = stringValue ?? "";
                    shouldAdd = true;
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
                    Changes = element.Changes
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
    }
}
