using System;
using System.Threading.Tasks;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Nice3point.Revit.Toolkit.External;

namespace ViewTracker
{
    public class Application : ExternalApplication
    {
        private SupabaseService _supabaseService;

        public override void OnStartup()
        {
            try
            {
                _supabaseService = new SupabaseService();
                Task.Run(async () => await _supabaseService.InitializeAsync());
                UiApplication.ViewActivated += OnViewActivated;
                CreateRibbonButton();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnStartup: {ex.Message}");
            }
        }

        private void CreateRibbonButton()
        {
            try
            {
                var uiApp = UiApplication;
                try { uiApp.CreateRibbonTab("ViewTracker"); } catch { }

                var ribbonPanel = uiApp.CreateRibbonPanel("ViewTracker", "ViewTracker");
                var buttonData = new PushButtonData(
                    "InitializeViews",
                    "Initialize\nViews",
                    typeof(Application).Assembly.Location,
                    "ViewTracker.Commands.InitializeViewsCommand"
                );

                buttonData.ToolTip = "Initialize all views in the project database";
                ribbonPanel.AddItem(buttonData);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating ribbon button: {ex.Message}");
            }
        }

        public override void OnShutdown()
        {
            try
            {
                UiApplication.ViewActivated -= OnViewActivated;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnShutdown: {ex.Message}");
            }
        }

        private void OnViewActivated(object sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
        {
            try
            {
                var currentView = e.CurrentActiveView;
                var document = e.Document;

                if (currentView == null || document == null)
                    return;

                if (!IsViewTrackingEnabled(document))
                {
                    return;
                }

                var fileName = System.IO.Path.GetFileNameWithoutExtension(document.PathName);
                if (string.IsNullOrEmpty(fileName))
                    fileName = document.Title;

                string viewName = currentView.Name;
                string viewType = GetViewType(currentView);

                string sheetNumber = null, viewNumber = null;

                if (currentView is ViewSheet sheet)
                {
                    viewName = $"{sheet.SheetNumber}_{sheet.Name}";
                    viewType = "Sheet";
                    sheetNumber = sheet.SheetNumber;
                }
                else
                {
                    // Find viewport placement (if any)
                    var viewports = new FilteredElementCollector(document)
                        .OfClass(typeof(Viewport))
                        .Cast<Viewport>()
                        .Where(vp => vp.ViewId == currentView.Id)
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
                try
                {
                    info = WorksharingUtils.GetWorksharingTooltipInfo(document, currentView.Id);
                }
                catch
                {
                }
                string creatorName = info?.Creator ?? "";
                string lastChangedBy = info?.LastChangedBy ?? "";

                Task.Run(async () =>
                {
                    try
                    {
                        await _supabaseService.UpsertViewActivationAsync(
                            fileName,
                            currentView.UniqueId,
                            currentView.Id.Value.ToString(),
                            viewName,
                            viewType,
                            Environment.UserName,
                            creatorName,
                            lastChangedBy,
                            sheetNumber,
                            viewNumber
                        );
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error sending to Supabase: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in ViewActivated event: {ex.Message}");
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
