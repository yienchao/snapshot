using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using ViewTracker.Views;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class ElementCompareTwoVersionsCommand : IExternalCommand
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

            if (versionSnapshots.GroupBy(v => v.VersionName).Count() < 2)
            {
                TaskDialog.Show("Insufficient Versions", "You need at least 2 different versions to compare. Only 1 version found.");
                return Result.Cancelled;
            }

            // 3. Let user select TWO versions
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

            // Select first version (older/baseline)
            var selectionWindow1 = new VersionSelectionWindow(versionInfos);
            selectionWindow1.Title = "Select First Version (Baseline)";
            var dialogResult1 = selectionWindow1.ShowDialog();

            if (dialogResult1 != true)
                return Result.Cancelled;

            string version1 = selectionWindow1.SelectedVersionName;
            if (string.IsNullOrWhiteSpace(version1))
                return Result.Cancelled;

            // Select second version (newer/comparison)
            var selectionWindow2 = new VersionSelectionWindow(versionInfos);
            selectionWindow2.Title = "Select Second Version (To Compare)";
            var dialogResult2 = selectionWindow2.ShowDialog();

            if (dialogResult2 != true)
                return Result.Cancelled;

            string version2 = selectionWindow2.SelectedVersionName;
            if (string.IsNullOrWhiteSpace(version2))
                return Result.Cancelled;

            if (version1 == version2)
            {
                TaskDialog.Show("Same Version", "You selected the same version twice. Please select two different versions.");
                return Result.Cancelled;
            }

            // 4. Get snapshot elements for both versions
            List<ElementSnapshot> snapshotElements1 = new List<ElementSnapshot>();
            List<ElementSnapshot> snapshotElements2 = new List<ElementSnapshot>();

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    snapshotElements1 = await supabaseService.GetElementsByVersionAsync(version1, projectId);
                    snapshotElements2 = await supabaseService.GetElementsByVersionAsync(version2, projectId);
                }).Wait();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to load snapshots:\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            // 5. Compare the two snapshots
            var comparison = CompareTwoSnapshots(snapshotElements1, snapshotElements2);

            // 6. Show results
            ShowComparisonResults(comparison, version1, version2, commandData.Application);

            return Result.Succeeded;
        }

        private ElementComparisonResult CompareTwoSnapshots(
            List<ElementSnapshot> snapshot1,
            List<ElementSnapshot> snapshot2)
        {
            var result = new ElementComparisonResult();
            var snapshot1Dict = snapshot1.ToDictionary(s => s.TrackId, s => s);
            var snapshot2Dict = snapshot2.ToDictionary(s => s.TrackId, s => s);

            // Find new elements (in snapshot2, not in snapshot1)
            foreach (var element in snapshot2)
            {
                if (!snapshot1Dict.ContainsKey(element.TrackId))
                {
                    result.NewElements.Add(new ElementChange
                    {
                        TrackId = element.TrackId,
                        Category = element.Category,
                        Mark = element.Mark,
                        FamilyName = element.FamilyName,
                        TypeName = element.TypeName,
                        ChangeType = "New"
                    });
                }
            }

            // Find deleted elements (in snapshot1, not in snapshot2)
            foreach (var element in snapshot1)
            {
                if (!snapshot2Dict.ContainsKey(element.TrackId))
                {
                    result.DeletedElements.Add(new ElementChange
                    {
                        TrackId = element.TrackId,
                        Category = element.Category,
                        Mark = element.Mark,
                        FamilyName = element.FamilyName,
                        TypeName = element.TypeName,
                        ChangeType = "Deleted"
                    });
                }
            }

            // Find modified elements (in both snapshots but with different values)
            foreach (var element2 in snapshot2)
            {
                if (snapshot1Dict.TryGetValue(element2.TrackId, out var element1))
                {
                    var changes = GetParameterChanges(element1, element2);
                    if (changes.Any())
                    {
                        result.ModifiedElements.Add(new ElementChange
                        {
                            TrackId = element2.TrackId,
                            Category = element2.Category,
                            Mark = element2.Mark,
                            FamilyName = element2.FamilyName,
                            TypeName = element2.TypeName,
                            ChangeType = "Modified",
                            Changes = changes
                        });
                    }
                }
            }

            return result;
        }

        private List<string> GetParameterChanges(ElementSnapshot snapshot1, ElementSnapshot snapshot2)
        {
            var changes = new List<string>();

            // Build dictionaries for both snapshots
            var params1 = BuildSnapshotParams(snapshot1);
            var params2 = BuildSnapshotParams(snapshot2);

            // Compare parameters
            foreach (var param2 in params2)
            {
                if (params1.TryGetValue(param2.Key, out var value1))
                {
                    bool isDifferent = false;

                    if (param2.Value is double snapDouble && value1 is double snap1Double)
                    {
                        isDifferent = Math.Abs(snapDouble - snap1Double) > 0.001;
                    }
                    else if (param2.Value is long snapLong && value1 is int snap1Int)
                    {
                        isDifferent = (snapLong != snap1Int);
                    }
                    else if (param2.Value is int snapInt && value1 is long snap1Long)
                    {
                        isDifferent = (snapInt != snap1Long);
                    }
                    else
                    {
                        var snap2Str = param2.Value?.ToString() ?? "";
                        var snap1Str = value1?.ToString() ?? "";
                        isDifferent = (snap2Str != snap1Str);
                    }

                    if (isDifferent)
                    {
                        changes.Add($"{param2.Key}: '{value1}' → '{param2.Value}'");
                    }
                }
                else
                {
                    changes.Add($"{param2.Key}: (new) → '{param2.Value}'");
                }
            }

            // Check for removed parameters
            foreach (var param1 in params1)
            {
                if (!params2.ContainsKey(param1.Key))
                {
                    changes.Add($"{param1.Key}: '{param1.Value}' → (removed)");
                }
            }

            return changes;
        }

        private Dictionary<string, object> BuildSnapshotParams(ElementSnapshot snapshot)
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
            if (!string.IsNullOrEmpty(snapshot.Category))
                parameters["Category"] = snapshot.Category;
            if (!string.IsNullOrEmpty(snapshot.FamilyName))
                parameters["Family"] = snapshot.FamilyName;
            if (!string.IsNullOrEmpty(snapshot.TypeName))
                parameters["Type"] = snapshot.TypeName;
            if (!string.IsNullOrEmpty(snapshot.Mark))
                parameters["Mark"] = snapshot.Mark;
            if (!string.IsNullOrEmpty(snapshot.Level))
                parameters["Level"] = snapshot.Level;
            if (!string.IsNullOrEmpty(snapshot.PhaseCreated))
                parameters["Phase Created"] = snapshot.PhaseCreated;
            if (!string.IsNullOrEmpty(snapshot.PhaseDemolished))
                parameters["Phase Demolished"] = snapshot.PhaseDemolished;
            if (!string.IsNullOrEmpty(snapshot.Comments))
                parameters["Comments"] = snapshot.Comments;

            return parameters;
        }

        private void ShowComparisonResults(ElementComparisonResult result, string version1, string version2, UIApplication uiApp)
        {
            int totalChanges = result.NewElements.Count + result.DeletedElements.Count + result.ModifiedElements.Count;

            if (totalChanges == 0)
            {
                TaskDialog.Show("No Changes", $"No changes detected between '{version1}' and '{version2}'.");
                return;
            }

            // Build ViewModel
            var viewModel = new ComparisonResultViewModel
            {
                VersionName = $"{version1} → {version2}",
                VersionInfo = $"Element Comparison: {version1} (baseline) → {version2} (comparison) | Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                NewRoomsCount = result.NewElements.Count,
                ModifiedRoomsCount = result.ModifiedElements.Count,
                DeletedRoomsCount = result.DeletedElements.Count
            };

            // Convert to display models
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

            viewModel.AllResults = new System.Collections.ObjectModel.ObservableCollection<RoomChangeDisplay>(displayItems);
            viewModel.FilteredResults = new System.Collections.ObjectModel.ObservableCollection<RoomChangeDisplay>(displayItems);

            // Show WPF window
            var window = new ComparisonResultWindow(viewModel);
            window.ShowDialog();
        }
    }
}
