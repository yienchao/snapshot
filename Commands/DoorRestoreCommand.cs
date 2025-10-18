using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using ViewTracker.Views;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class DoorRestoreCommand : IExternalCommand
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

            // 3. Get current doors with trackID
            var selectedIds = uiDoc.Selection.GetElementIds();
            List<Element> currentDoors;
            bool hasPreSelection = false;

            if (selectedIds.Any())
            {
                // Use pre-selected doors
                currentDoors = selectedIds
                    .Select(id => doc.GetElement(id))
                    .Where(e => e != null && e.Category?.Id.Value == (long)BuiltInCategory.OST_Doors)
                    .Where(e => e.LookupParameter("trackID") != null &&
                               !string.IsNullOrWhiteSpace(e.LookupParameter("trackID").AsString()))
                    .ToList();

                if (currentDoors.Any())
                {
                    hasPreSelection = true;
                }
                else
                {
                    // Fall back to all doors if selection is invalid
                    currentDoors = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Doors)
                        .WhereElementIsNotElementType()
                        .Cast<Element>()
                        .Where(e => e.LookupParameter("trackID") != null &&
                                   !string.IsNullOrWhiteSpace(e.LookupParameter("trackID").AsString()))
                        .ToList();
                }
            }
            else
            {
                // No selection - use all doors
                currentDoors = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .Cast<Element>()
                    .Where(e => e.LookupParameter("trackID") != null &&
                               !string.IsNullOrWhiteSpace(e.LookupParameter("trackID").AsString()))
                    .ToList();
            }

            if (!currentDoors.Any())
            {
                TaskDialog.Show("No Doors", "No doors with trackID parameter found in the model.\n\n" +
                    "Note: Deleted doors cannot be recreated. Only existing doors can have their parameters restored.");
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
            var restoreWindow = new ParameterRestoreWindow(
                versionInfos,
                currentDoors.Count,
                hasPreSelection,
                doc,
                supabaseService,
                projectId,
                currentDoors,
                "Door"
            );

            var result = restoreWindow.ShowDialog();

            return result == true ? Result.Succeeded : Result.Cancelled;
        }
    }
}
