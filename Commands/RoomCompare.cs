using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
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
            List<string> versions = new List<string>();

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await supabaseService.InitializeAsync();
                    versions = await supabaseService.GetAllVersionNamesAsync(projectId);
                }).Wait();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to load versions:\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            if (!versions.Any())
            {
                TaskDialog.Show("No Versions", "No snapshots found in Supabase. Create a snapshot first.");
                return Result.Cancelled;
            }

            // 3. Let user select version
            var versionDialog = new TaskDialog("Select Version to Compare");
            versionDialog.MainInstruction = "Compare current rooms with which version?";
            versionDialog.MainContent = $"Found {versions.Count} version(s):\n\n" + string.Join("\n", versions.Take(10));
            
            if (versions.Count > 10)
                versionDialog.FooterText = $"...and {versions.Count - 10} more. Showing first 10.";

            // Use InputBox for version selection (TaskDialog limitation)
            string selectedVersion = Microsoft.VisualBasic.Interaction.InputBox(
                $"Enter version name to compare:\n\nAvailable versions:\n{string.Join("\n", versions.Take(20))}",
                "Select Version",
                versions.FirstOrDefault(),
                -1, -1);

            if (string.IsNullOrWhiteSpace(selectedVersion))
                return Result.Cancelled;

            if (!versions.Contains(selectedVersion))
            {
                TaskDialog.Show("Invalid Version", $"Version '{selectedVersion}' not found.");
                return Result.Failed;
            }

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

            // 5. Get current rooms from Revit
            var currentRooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.LookupParameter("trackID") != null &&
                           !string.IsNullOrWhiteSpace(r.LookupParameter("trackID").AsString()))
                .ToList();

            // 6. Compare
            ComparisonResult comparison;
            try
            {
                comparison = CompareRooms(currentRooms, snapshotRooms, doc);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Comparison Error", $"Error during comparison:\n{ex.Message}\n\nStack:\n{ex.StackTrace}");
                return Result.Failed;
            }

            // 7. Show results
            try
            {
                ShowComparisonResults(comparison, selectedVersion, commandData.Application);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Display Error", $"Error showing results:\n{ex.Message}\n\nStack:\n{ex.StackTrace}");
                return Result.Failed;
            }

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
            var currentParams = GetAllParameters(currentRoom, doc);

            // Check if snapshot has parameters
            if (snapshot.AllParameters == null)
                return changes;

            foreach (var snapshotParam in snapshot.AllParameters)
            {
                if (currentParams.TryGetValue(snapshotParam.Key, out var currentValue))
                {
                    var snapshotValue = snapshotParam.Value?.ToString() ?? "";
                    var currentValueStr = currentValue?.ToString() ?? "";
                    
                    if (snapshotValue != currentValueStr)
                    {
                        changes.Add($"{snapshotParam.Key}: '{snapshotValue}' → '{currentValueStr}'");
                    }
                }
                else
                {
                    // Parameter removed
                    changes.Add($"{snapshotParam.Key}: '{snapshotParam.Value}' → (removed)");
                }
            }

            // Check for new parameters
            foreach (var currentParam in currentParams)
            {
                if (!snapshot.AllParameters.ContainsKey(currentParam.Key))
                {
                    changes.Add($"{currentParam.Key}: (new) → '{currentParam.Value}'");
                }
            }

            return changes;
        }

        private Dictionary<string, object> GetAllParameters(Room room, Document doc)
        {
            var parameters = new Dictionary<string, object>();

            foreach (Parameter param in room.Parameters)
            {
                if (param.HasValue)
                {
                    string paramName = param.Definition.Name;
                    object paramValue = GetParameterValue(param);
                    
                    if (paramValue != null)
                    {
                        parameters[paramName] = paramValue;
                    }
                }
            }

            return parameters;
        }

        private object GetParameterValue(Parameter param)
        {
            switch (param.StorageType)
            {
                case StorageType.Double:
                    return param.AsDouble();
                case StorageType.Integer:
                    return param.AsInteger();
                case StorageType.String:
                    return param.AsString();
                case StorageType.ElementId:
                    return param.AsElementId().IntegerValue;
                default:
                    return null;
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