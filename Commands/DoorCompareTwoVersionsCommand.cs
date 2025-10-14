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
    public class DoorCompareTwoVersionsCommand : IExternalCommand
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

            // 4. Get snapshot doors for both versions
            List<DoorSnapshot> snapshotDoors1 = new List<DoorSnapshot>();
            List<DoorSnapshot> snapshotDoors2 = new List<DoorSnapshot>();

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    snapshotDoors1 = await supabaseService.GetDoorsByVersionAsync(version1, projectId);
                    snapshotDoors2 = await supabaseService.GetDoorsByVersionAsync(version2, projectId);
                }).Wait();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to load snapshots:\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            // 5. Compare the two snapshots
            var comparison = CompareTwoSnapshots(snapshotDoors1, snapshotDoors2);

            // 6. Show results
            ShowComparisonResults(comparison, version1, version2, commandData.Application);

            return Result.Succeeded;
        }

        private DoorComparisonResult CompareTwoSnapshots(
            List<DoorSnapshot> snapshot1,
            List<DoorSnapshot> snapshot2)
        {
            var result = new DoorComparisonResult();
            var snapshot1Dict = snapshot1.ToDictionary(s => s.TrackId, s => s);
            var snapshot2Dict = snapshot2.ToDictionary(s => s.TrackId, s => s);

            // Find new doors (in snapshot2, not in snapshot1)
            foreach (var door in snapshot2)
            {
                if (!snapshot1Dict.ContainsKey(door.TrackId))
                {
                    result.NewDoors.Add(new DoorChange
                    {
                        TrackId = door.TrackId,
                        Mark = door.Mark,
                        FamilyName = door.FamilyName,
                        TypeName = door.TypeName,
                        ChangeType = "New"
                    });
                }
            }

            // Find deleted doors (in snapshot1, not in snapshot2)
            foreach (var door in snapshot1)
            {
                if (!snapshot2Dict.ContainsKey(door.TrackId))
                {
                    result.DeletedDoors.Add(new DoorChange
                    {
                        TrackId = door.TrackId,
                        Mark = door.Mark,
                        FamilyName = door.FamilyName,
                        TypeName = door.TypeName,
                        ChangeType = "Deleted"
                    });
                }
            }

            // Find modified doors (in both snapshots but with different values)
            foreach (var door2 in snapshot2)
            {
                if (snapshot1Dict.TryGetValue(door2.TrackId, out var door1))
                {
                    var changes = GetParameterChanges(door1, door2);
                    if (changes.Any())
                    {
                        result.ModifiedDoors.Add(new DoorChange
                        {
                            TrackId = door2.TrackId,
                            Mark = door2.Mark,
                            FamilyName = door2.FamilyName,
                            TypeName = door2.TypeName,
                            ChangeType = "Modified",
                            Changes = changes
                        });
                    }
                }
            }

            return result;
        }

        private List<string> GetParameterChanges(DoorSnapshot snapshot1, DoorSnapshot snapshot2)
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

                    // For doubles, use tolerance comparison
                    if (param2.Value is double snapDouble && value1 is double snap1Double)
                    {
                        isDifferent = Math.Abs(snapDouble - snap1Double) > 0.001;
                    }
                    // Handle long/int comparison
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
                    // Parameter was added in version 2
                    changes.Add($"{param2.Key}: (new) → '{param2.Value}'");
                }
            }

            // Check for removed parameters (in version 1 but not in version 2)
            foreach (var param1 in params1)
            {
                if (!params2.ContainsKey(param1.Key))
                {
                    changes.Add($"{param1.Key}: '{param1.Value}' → (removed)");
                }
            }

            return changes;
        }

        private Dictionary<string, object> BuildSnapshotParams(DoorSnapshot snapshot)
        {
            var parameters = new Dictionary<string, object>();

            // Add from AllParameters JSON
            if (snapshot.AllParameters != null)
            {
                foreach (var kvp in snapshot.AllParameters)
                {
                    parameters[kvp.Key] = kvp.Value;
                }
            }

            // Add from dedicated columns
            if (!string.IsNullOrEmpty(snapshot.FamilyName))
                parameters["Family"] = snapshot.FamilyName;
            if (!string.IsNullOrEmpty(snapshot.TypeName))
                parameters["Type"] = snapshot.TypeName;
            if (!string.IsNullOrEmpty(snapshot.Mark))
                parameters["Mark"] = snapshot.Mark;
            if (!string.IsNullOrEmpty(snapshot.Level))
                parameters["Level"] = snapshot.Level;
            if (!string.IsNullOrEmpty(snapshot.FireRating))
                parameters["Fire Rating"] = snapshot.FireRating;
            if (snapshot.DoorWidth.HasValue)
                parameters["Width"] = snapshot.DoorWidth.Value;
            if (snapshot.DoorHeight.HasValue)
                parameters["Height"] = snapshot.DoorHeight.Value;
            if (!string.IsNullOrEmpty(snapshot.PhaseCreated))
                parameters["Phase Created"] = snapshot.PhaseCreated;
            if (!string.IsNullOrEmpty(snapshot.PhaseDemolished))
                parameters["Phase Demolished"] = snapshot.PhaseDemolished;
            if (!string.IsNullOrEmpty(snapshot.Comments))
                parameters["Comments"] = snapshot.Comments;

            return parameters;
        }

        private void ShowComparisonResults(DoorComparisonResult result, string version1, string version2, UIApplication uiApp)
        {
            int totalChanges = result.NewDoors.Count + result.DeletedDoors.Count + result.ModifiedDoors.Count;

            if (totalChanges == 0)
            {
                TaskDialog.Show("No Changes", $"No changes detected between '{version1}' and '{version2}'.");
                return;
            }

            // Build ViewModel (reusing room view model)
            var viewModel = new ComparisonResultViewModel
            {
                VersionName = $"{version1} → {version2}",
                VersionInfo = $"Door Comparison: {version1} (baseline) → {version2} (comparison) | Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                NewRoomsCount = result.NewDoors.Count,
                ModifiedRoomsCount = result.ModifiedDoors.Count,
                DeletedRoomsCount = result.DeletedDoors.Count
            };

            // Convert to display models
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

            viewModel.AllResults = new System.Collections.ObjectModel.ObservableCollection<RoomChangeDisplay>(displayItems);
            viewModel.FilteredResults = new System.Collections.ObjectModel.ObservableCollection<RoomChangeDisplay>(displayItems);

            // Show WPF window
            var window = new ComparisonResultWindow(viewModel);
            window.ShowDialog();
        }
    }
}
