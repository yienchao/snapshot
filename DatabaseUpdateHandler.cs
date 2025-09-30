using System;
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

        public async Task HandleViewActivationAsync(string fileName, string viewUniqueId, int viewElementId, string viewName, string viewType, string lastViewer)
        {
            try
            {
                await _supabaseService.UpsertViewActivationAsync(
                    fileName,
                    viewUniqueId,
                    viewElementId,
                    viewName,
                    viewType,
                    lastViewer);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in DatabaseUpdateHandler: {ex.Message}");
            }
        }

        public async Task HandleViewActivationAsync(View view, Document document, string fileName, string lastViewer)
        {
            try
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

                await _supabaseService.UpsertViewActivationAsync(
                    fileName,
                    view.UniqueId,
                    view.Id.IntegerValue,
                    viewName,
                    viewType,
                    lastViewer
                );
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
