using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using ViewTracker.Views;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class RoomRestoreCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument.Document;
            var uiDoc = commandData.Application.ActiveUIDocument;

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

            // 3. Get current rooms
            var selectedIds = uiDoc.Selection.GetElementIds();
            List<Room> currentRooms;
            bool hasPreSelection = false;

            if (selectedIds.Any())
            {
                // Use pre-selected rooms
                currentRooms = selectedIds
                    .Select(id => doc.GetElement(id))
                    .OfType<Room>()
                    .Where(r => r.LookupParameter("trackID") != null &&
                               !string.IsNullOrWhiteSpace(r.LookupParameter("trackID").AsString()))
                    .ToList();

                if (currentRooms.Any())
                {
                    hasPreSelection = true;
                }
                else
                {
                    // Fall back to all rooms if selection is invalid
                    currentRooms = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType()
                        .Cast<Room>()
                        .Where(r => r.LookupParameter("trackID") != null &&
                                   !string.IsNullOrWhiteSpace(r.LookupParameter("trackID").AsString()))
                        .ToList();
                }
            }
            else
            {
                // No selection - use all rooms
                currentRooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r.LookupParameter("trackID") != null &&
                               !string.IsNullOrWhiteSpace(r.LookupParameter("trackID").AsString()))
                    .ToList();
            }

            if (!currentRooms.Any())
            {
                TaskDialog.Show("No Rooms", "No rooms with trackID parameter found in the model.");
                return Result.Cancelled;
            }

            // 4. Prepare version list
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

            // 5. Show restore window
            var restoreWindow = new RoomRestoreWindow(
                versionInfos,
                currentRooms.Count,
                hasPreSelection,
                doc,
                supabaseService,
                projectId,
                currentRooms
            );

            var result = restoreWindow.ShowDialog();

            return result == true ? Result.Succeeded : Result.Cancelled;
        }
    }
}
