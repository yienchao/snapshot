using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System.Threading.Tasks;

namespace ViewTracker
{
    public class Application : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            application.ViewActivated += OnViewActivated;
            CreateRibbonButton(application);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ViewActivated -= OnViewActivated;
            return Result.Succeeded;
        }

        private void CreateRibbonButton(UIControlledApplication application)
        {
            try
            {
                string tabName = "ViewTracker";
                try { application.CreateRibbonTab(tabName); } catch { }
                var ribbonPanel = application.CreateRibbonPanel(tabName, tabName);
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

        // Event handler for view activation
        private void OnViewActivated(object sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
        {
            try
            {
                var currentView = e.CurrentActiveView;
                var document = e.Document;
                if (currentView == null || document == null)
                    return;

                var projectIdStr = document.ProjectInformation.LookupParameter("projectID")?.AsString();
                Guid projectId = Guid.Empty;
                if (!Guid.TryParse(projectIdStr, out projectId))
                    return;

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
                try { info = WorksharingUtils.GetWorksharingTooltipInfo(document, currentView.Id); } catch { }
                string creatorName = info?.Creator ?? "";
                string lastChangedBy = info?.LastChangedBy ?? "";

                // Send activation data to Supabase (increment count!)
                Task.Run(async () =>
                {
                    var supabaseService = new SupabaseService();
                    await supabaseService.InitializeAsync();

                    int previousCount = await supabaseService.GetActivationCountAsync(currentView.UniqueId);
                    int activationCount = previousCount + 1;

                    var record = new ViewActivationRecord
                    {
                        ViewUniqueId = currentView.UniqueId,
                        FileName = fileName,
                        ViewId = currentView.Id.Value.ToString(),
                        ViewName = viewName,
                        ViewType = viewType,
                        LastViewer = Environment.UserName,
                        LastActivationDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        ActivationCount = activationCount,
                        LastInitialization = null,
                        CreatorName = creatorName,
                        LastChangedBy = lastChangedBy,
                        SheetNumber = sheetNumber,
                        ViewNumber = viewNumber,
                        ProjectId = projectId
                    };

                    await supabaseService.UpsertViewActivationAsync(record);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in ViewActivated event: {ex.Message}");
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
    }
}
