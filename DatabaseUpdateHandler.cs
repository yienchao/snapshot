using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;

namespace ViewTracker
{
    public class DatabaseUpdateHandler
    {
        private readonly SupabaseService _supabaseService;

        public DatabaseUpdateHandler()
        {
            _supabaseService = new SupabaseService();
        }

        public DatabaseUpdateHandler(SupabaseService supabaseService)
        {
            _supabaseService = supabaseService;
        }

        public async Task HandleViewActivationAsync(ViewActivationRecord record)
        {
            try
            {
                await _supabaseService.UpsertViewActivationAsync(record);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in DatabaseUpdateHandler: {ex.Message}");
            }
        }

        public async Task HandleViewActivationAsync(View view, Document document, string fileName, string lastViewer, Guid projectId)
        {
            try
            {
                string viewName = view.Name;
                string viewType = GetViewType(view);
                string sheetNumber = null;
                string viewNumber = null;

                if (view is ViewSheet sheet)
                {
                    viewName = $"{sheet.SheetNumber}_{sheet.Name}";
                    viewType = "Sheet";
                    sheetNumber = sheet.SheetNumber;
                }
                else if (view.ViewType == ViewType.Schedule)
                {
                    var allScheduleInstances = new FilteredElementCollector(document)
                        .OfClass(typeof(ScheduleSheetInstance))
                        .Cast<ScheduleSheetInstance>()
                        .ToList();

                    var sheetNumbers = new List<string>();
                    foreach (var sInst in allScheduleInstances)
                    {
                        Element scheduleElem = document.GetElement(sInst.ScheduleId);
                        bool isRevisionSchedule = scheduleElem is ViewSchedule vs &&
                            vs.Definition.CategoryId == new ElementId(BuiltInCategory.OST_Revisions);

                        // Use .Value property for ElementId comparison
                        if ((sInst.ScheduleId.Value == view.Id.Value) && !isRevisionSchedule)
                        {
                            var parentSheet = document.GetElement(sInst.OwnerViewId) as ViewSheet;
                            if (parentSheet != null && !string.IsNullOrEmpty(parentSheet.SheetNumber))
                                sheetNumbers.Add(parentSheet.SheetNumber);
                        }
                    }
                    sheetNumber = sheetNumbers.Count > 0 ? string.Join(",", sheetNumbers) : null;
                }
                else if (view.ViewType == ViewType.Legend)
                {
                    var allSheets = new FilteredElementCollector(document)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .ToList();

                    var sheetNumbers = new List<string>();
                    foreach (var legendSheet in allSheets)
                    {
                        var placedViews = legendSheet.GetAllPlacedViews();
                        if (placedViews.Contains(view.Id))
                        {
                            if (!string.IsNullOrEmpty(legendSheet.SheetNumber))
                                sheetNumbers.Add(legendSheet.SheetNumber);
                        }
                    }
                    sheetNumber = sheetNumbers.Count > 0 ? string.Join(",", sheetNumbers) : null;
                }
                else
                {
                    var viewports = new FilteredElementCollector(document)
                        .OfClass(typeof(Viewport))
                        .Cast<Viewport>()
                        .Where(vp => vp.ViewId == view.Id)
                        .ToList();

                    if (viewports.Any())
                    {
                        var viewport = viewports.First();
                        var parentSheet = document.GetElement(viewport.SheetId) as ViewSheet;
                        if (parentSheet != null)
                        {
                            sheetNumber = parentSheet.SheetNumber;
                            viewNumber = viewport.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.AsString();
                        }
                    }
                }

                WorksharingTooltipInfo info = null;
                try { info = WorksharingUtils.GetWorksharingTooltipInfo(document, view.Id); } catch { }
                string creatorName = info?.Creator ?? "";
                string lastChangedBy = info?.LastChangedBy ?? "";

                var record = new ViewActivationRecord
                {
                    ViewUniqueId = view.UniqueId,
                    FileName = fileName,
                    ViewId = view.Id.Value.ToString(),
                    ViewName = viewName,
                    ViewType = viewType,
                    LastViewer = lastViewer,
                    LastActivationDate = null,
                    ActivationCount = 0,
                    LastInitialization = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    CreatorName = creatorName,
                    LastChangedBy = lastChangedBy,
                    SheetNumber = sheetNumber,
                    ViewNumber = viewNumber,
                    ProjectId = projectId
                };

                await HandleViewActivationAsync(record);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in DatabaseUpdateHandler: {ex.Message}");
            }
        }

        private string GetViewType(View view)
        {
            try
            {
                switch (view.ViewType)
                {
                    case ViewType.FloorPlan: return "Floor Plan";
                    case ViewType.CeilingPlan: return "Ceiling Plan";
                    case ViewType.Elevation: return "Elevation";
                    case ViewType.Section: return "Section";
                    case ViewType.ThreeD: return "3D";
                    case ViewType.DrawingSheet: return "Sheet";
                    case ViewType.Schedule: return "Schedule";
                    case ViewType.DraftingView: return "Drafting";
                    case ViewType.Legend: return "Legend";
                    case ViewType.AreaPlan: return "Area Plan";
                    case ViewType.Detail: return "Detail";
                    case ViewType.Rendering: return "Rendering";
                    case ViewType.Walkthrough: return "Walkthrough";
                    default: return view.ViewType.ToString();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting view type: {ex.Message}");
                return "Unknown";
            }
        }
    }
}
