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
    public class ElementRestoreCommand : IExternalCommand
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

            // 3. Get current elements with trackID
            var selectedIds = uiDoc.Selection.GetElementIds();
            List<Element> currentElements;
            bool hasPreSelection = false;

            if (selectedIds.Any())
            {
                // Use pre-selected elements
                currentElements = selectedIds
                    .Select(id => doc.GetElement(id))
                    .Where(e => e != null && e is FamilyInstance)
                    .Where(e => e.LookupParameter("trackID") != null &&
                               !string.IsNullOrWhiteSpace(e.LookupParameter("trackID").AsString()))
                    .ToList();

                if (currentElements.Any())
                {
                    hasPreSelection = true;
                }
                else
                {
                    // Fall back to all elements if selection is invalid
                    currentElements = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilyInstance))
                        .Cast<Element>()
                        .Where(e => e.LookupParameter("trackID") != null &&
                                   !string.IsNullOrWhiteSpace(e.LookupParameter("trackID").AsString()))
                        .ToList();
                }
            }
            else
            {
                // No selection - use all elements with trackID
                currentElements = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<Element>()
                    .Where(e => e.LookupParameter("trackID") != null &&
                               !string.IsNullOrWhiteSpace(e.LookupParameter("trackID").AsString()))
                    .ToList();
            }

            if (!currentElements.Any())
            {
                TaskDialog.Show("No Elements", "No elements with trackID parameter found in the model.\n\n" +
                    "Note: Deleted elements cannot be recreated. Only existing elements can have their parameters restored.");
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
                currentElements.Count,
                hasPreSelection,
                doc,
                supabaseService,
                projectId,
                currentElements,
                "Element"
            );

            var result = restoreWindow.ShowDialog();

            return result == true ? Result.Succeeded : Result.Cancelled;
        }
    }
}
