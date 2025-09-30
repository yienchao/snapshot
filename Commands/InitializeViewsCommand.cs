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

                Task.Run(async () => await OptimizedInitializeAndCleanViewsInDatabase(document, views, fileName));
                TaskDialog.Show("ViewTracker", $"Started batch processing of {views.Count} views. Check Supabase for progress.");
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

        private async Task OptimizedInitializeAndCleanViewsInDatabase(Document document, List<View> views, string fileName)
        {
            try
            {
                var supabaseService = new SupabaseService();
                await supabaseService.InitializeAsync();

                var allViewports = new FilteredElementCollector(document)
                    .OfClass(typeof(Viewport))
                    .Cast<Viewport>()
                    .ToList();

                var allRecords = new List<ViewActivationRecord>();
                var currentUniqueIds = new HashSet<string>();

                foreach (var view in views)
                {
                    string viewName, viewType, sheetNumber = null, viewNumber = null;

                    if (view is ViewSheet sheet)
                    {
                        viewName = $"{sheet.SheetNumber}_{sheet.Name}";
                        viewType = "Sheet";
                        sheetNumber = sheet.SheetNumber;
                    }
                    else
                    {
                        viewName = view.Name;
                        viewType = GetViewType(view);

                        var viewport = allViewports.FirstOrDefault(vp => vp.ViewId == view.Id);
                        if (viewport != null)
                        {
                            var parentSheet = document.GetElement(viewport.SheetId) as ViewSheet;
                            if (parentSheet != null)
                            {
                                sheetNumber = parentSheet.SheetNumber;
                                viewNumber = viewport.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.AsString();
                            }
                        }
                    }

                    WorksharingTooltipInfo info = null;
                    try { info = WorksharingUtils.GetWorksharingTooltipInfo(view.Document, view.Id); } catch { }
                    string creatorName = info?.Creator ?? "";
                    string lastChangedBy = info?.LastChangedBy ?? "";

                    var currentDateTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                    // These three fields must be preserved from existing DB data in service:
                    var record = new ViewActivationRecord
                    {
                        ViewUniqueId = view.UniqueId,
                        FileName = fileName,
                        ViewId = view.Id.Value.ToString(),
                        ViewName = viewName,
                        ViewType = viewType,
                        LastViewer = null,              // will be filled from DB if present
                        LastActivationDate = null,      // will be filled from DB if present
                        ActivationCount = 0,            // will be filled from DB if present
                        LastInitialization = currentDateTime,
                        CreatorName = creatorName,
                        LastChangedBy = lastChangedBy,
                        SheetNumber = sheetNumber,
                        ViewNumber = viewNumber
                    };

                    allRecords.Add(record);
                    currentUniqueIds.Add(view.UniqueId);
                }

                // Use the upsert that preserves user fields
                await supabaseService.BulkUpsertInitViewsPreserveAsync(allRecords, fileName);

                // Clean up orphaned records
                var existingUniqueIds = await supabaseService.GetExistingViewUniqueIdsAsync(fileName);
                var orphanIds = existingUniqueIds.Except(currentUniqueIds).ToList();

                if (orphanIds.Any())
                {
                    await supabaseService.BulkDeleteOrphanedRecordsAsync(orphanIds);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in optimized initialization: {ex.Message}");
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
            catch
            {
                return false;
            }
        }
    }
}
