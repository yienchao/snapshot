using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Nice3point.Revit.Toolkit.External;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class InitializeViewsCommand : ExternalCommand
    {
        public override void Execute()
        {
            var document = UiDocument.Document;

            if (!IsViewTrackingEnabled(document))
            {
                TaskDialog.Show("ViewTracker", "ViewTracker parameter must be set to Yes to initialize views.");
                return;
            }

            try
            {
                var views = GetAllTrackedViews(document);
                var fileName = System.IO.Path.GetFileNameWithoutExtension(document.PathName);
                if (string.IsNullOrEmpty(fileName))
                    fileName = document.Title;

                Task.Run(async () => await InitializeAndCleanViewsInDatabase(views, fileName));
                TaskDialog.Show("ViewTracker", $"Updating {views.Count} tracked views and cleaning orphaned Supabase records...");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to initialize views: {ex.Message}");
            }
        }

        private List<View> GetAllTrackedViews(Document document)
        {
            var trackedTypes = new HashSet<ViewType>
            {
                ViewType.FloorPlan,
                ViewType.CeilingPlan,
                ViewType.Elevation,
                ViewType.Section,
                ViewType.ThreeD,
                ViewType.Legend,
                ViewType.Detail,
                ViewType.Schedule,
                ViewType.DrawingSheet
            };

            return new FilteredElementCollector(document)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && trackedTypes.Contains(v.ViewType))
                .ToList();
        }

        private async Task InitializeAndCleanViewsInDatabase(List<View> views, string fileName)
        {
            try
            {
                var supabaseService = new SupabaseService();
                await supabaseService.InitializeAsync();

                var currentUniqueIds = new HashSet<string>();
                foreach (var view in views)
                {
                    string viewName;
                    string viewType;

                    if (view is ViewSheet sheet)
                    {
                        viewName = $"{sheet.SheetNumber}_{sheet.Name}";
                        viewType = "Sheet";
                    }
                    else
                    {
                        viewName = view.Name;
                        viewType = GetViewType(view);
                    }

                    await supabaseService.InitializeOrUpdateViewAsync(
                        fileName,
                        view.UniqueId,
                        view.Id.IntegerValue,
                        viewName,
                        viewType,
                        Environment.UserName // This gets passed as lastViewer now
                    );

                    currentUniqueIds.Add(view.UniqueId);
                }

                var allRecords = await supabaseService.GetViewActivationsByFileNameAsync(fileName);
                var orphanRecords = allRecords.Where(r => !currentUniqueIds.Contains(r.ViewUniqueId)).ToList();

                foreach (var orphan in orphanRecords)
                {
                    await supabaseService.DeleteViewActivationByUniqueIdAsync(orphan.ViewUniqueId);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error initializing/cleaning views: {ex.Message}");
            }
        }

        private string GetViewType(View view)
        {
            switch (view.ViewType)
            {
                case ViewType.FloorPlan: return "Floor Plan";
                case ViewType.CeilingPlan: return "Ceiling Plan";
                case ViewType.Elevation: return "Elevation";
                case ViewType.Section: return "Section";
                case ViewType.ThreeD: return "3D";
                case ViewType.Legend: return "Legend";
                case ViewType.Detail: return "Detail";
                case ViewType.Schedule: return "Schedule";
                case ViewType.DrawingSheet: return "Sheet";
                default: return view.ViewType.ToString();
            }
        }

        private bool IsViewTrackingEnabled(Document document)
        {
            try
            {
                var projectInfo = document.ProjectInformation;
                var parameter = projectInfo.LookupParameter("ViewTracker");

                if (parameter == null)
                    return false;

                return parameter.AsInteger() == 1;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking ViewTracker parameter: {ex.Message}");
                return false;
            }
        }
    }
}
