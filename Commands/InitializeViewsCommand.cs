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
            
            // Check if ViewTracker parameter is enabled
            if (!IsViewTrackingEnabled(document))
            {
                TaskDialog.Show("ViewTracker", "ViewTracker parameter must be set to Yes to initialize views.");
                return;
            }

            try
            {
                var views = GetAllNonTemplateViews(document);
                var fileName = System.IO.Path.GetFileNameWithoutExtension(document.PathName);
                if (string.IsNullOrEmpty(fileName))
                    fileName = document.Title;

                Task.Run(async () => await InitializeViewsInDatabase(views, fileName));
                
                TaskDialog.Show("ViewTracker", $"Initializing {views.Count} views in database...");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to initialize views: {ex.Message}");
            }
        }

        private List<View> GetAllNonTemplateViews(Document document)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .ToList();
        }

        private async Task InitializeViewsInDatabase(List<View> views, string fileName)
        {
            try
            {
                var supabaseService = new SupabaseService();
                await supabaseService.InitializeAsync();

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

                    // CHANGED: Use InitializeViewAsync instead of UpsertViewActivationAsync
                    await supabaseService.InitializeViewAsync(
                        fileName,
                        view.UniqueId,
                        view.Id.ToString(),
                        viewName,
                        viewType,
                        Environment.UserName
                    );
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error initializing views: {ex.Message}");
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
