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
    public class RoomCompareTwoVersionsCommand : IExternalCommand
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

            // 4. Get snapshot rooms for both versions
            List<RoomSnapshot> snapshotRooms1 = new List<RoomSnapshot>();
            List<RoomSnapshot> snapshotRooms2 = new List<RoomSnapshot>();

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    snapshotRooms1 = await supabaseService.GetRoomsByVersionAsync(version1, projectId);
                    snapshotRooms2 = await supabaseService.GetRoomsByVersionAsync(version2, projectId);
                }).Wait();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to load snapshots:\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            // 5. Compare the two snapshots
            var comparison = CompareTwoSnapshots(snapshotRooms1, snapshotRooms2, doc);

            // 6. Show results
            ShowComparisonResults(comparison, version1, version2, commandData.Application);

            return Result.Succeeded;
        }

        private ComparisonResult CompareTwoSnapshots(
            List<RoomSnapshot> snapshot1,
            List<RoomSnapshot> snapshot2,
            Document doc)
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

            // Find modified rooms (in both snapshots but with different values)
            foreach (var room2 in snapshot2)
            {
                if (snapshot1Dict.TryGetValue(room2.TrackId, out var room1))
                {
                    var changes = GetParameterChanges(room1, room2, doc);
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

            return result;
        }

        private List<string> GetParameterChanges(RoomSnapshot snapshot1, RoomSnapshot snapshot2, Document doc)
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

        private Dictionary<string, object> BuildSnapshotParams(RoomSnapshot snapshot)
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

            // Add from dedicated columns (French names as keys - adjust if needed)
            if (!string.IsNullOrEmpty(snapshot.RoomNumber))
                parameters["Numéro"] = snapshot.RoomNumber;
            if (!string.IsNullOrEmpty(snapshot.RoomName))
                parameters["Nom"] = snapshot.RoomName;
            if (!string.IsNullOrEmpty(snapshot.Level))
                parameters["Niveau"] = snapshot.Level;

            if (snapshot.Area.HasValue)
                parameters["Surface"] = snapshot.Area.Value;
            if (snapshot.Perimeter.HasValue)
                parameters["Périmètre"] = snapshot.Perimeter.Value;
            if (snapshot.Volume.HasValue)
                parameters["Volume"] = snapshot.Volume.Value;
            if (snapshot.UnboundHeight.HasValue)
                parameters["Hauteur non liée"] = snapshot.UnboundHeight.Value;

            if (!string.IsNullOrEmpty(snapshot.Occupancy))
                parameters["Occupation"] = snapshot.Occupancy;
            if (!string.IsNullOrEmpty(snapshot.Department))
                parameters["Service"] = snapshot.Department;
            if (!string.IsNullOrEmpty(snapshot.Phase))
                parameters["Phase"] = snapshot.Phase;

            if (!string.IsNullOrEmpty(snapshot.BaseFinish))
                parameters["Finition de la base"] = snapshot.BaseFinish;
            if (!string.IsNullOrEmpty(snapshot.CeilingFinish))
                parameters["Finition du plafond"] = snapshot.CeilingFinish;
            if (!string.IsNullOrEmpty(snapshot.WallFinish))
                parameters["Finition du mur"] = snapshot.WallFinish;
            if (!string.IsNullOrEmpty(snapshot.FloorFinish))
                parameters["Finition du sol"] = snapshot.FloorFinish;

            if (!string.IsNullOrEmpty(snapshot.Comments))
                parameters["Commentaires"] = snapshot.Comments;
            if (!string.IsNullOrEmpty(snapshot.Occupant))
                parameters["Occupant"] = snapshot.Occupant;

            return parameters;
        }

        private void ShowComparisonResults(ComparisonResult result, string version1, string version2, UIApplication uiApp)
        {
            int totalChanges = result.NewRooms.Count + result.DeletedRooms.Count + result.ModifiedRooms.Count;

            if (totalChanges == 0)
            {
                TaskDialog.Show("No Changes", $"No changes detected between '{version1}' and '{version2}'.");
                return;
            }

            // Build ViewModel
            var viewModel = new ComparisonResultViewModel
            {
                VersionName = $"{version1} → {version2}",
                VersionInfo = $"Comparing: {version1} (baseline) → {version2} (comparison) | Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
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

            viewModel.AllResults = new System.Collections.ObjectModel.ObservableCollection<RoomChangeDisplay>(displayItems);
            viewModel.FilteredResults = new System.Collections.ObjectModel.ObservableCollection<RoomChangeDisplay>(displayItems);

            // Show WPF window
            var window = new ComparisonResultWindow(viewModel);
            window.ShowDialog();
        }
    }
}
