using System;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;

namespace ViewTracker
{
    public class Application : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            // Initialize localization based on Revit language
            string revitLanguage = application.ControlledApplication.Language.ToString();
            Localization.Initialize(revitLanguage);

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
                string tabName = "Snapshot";
                try { application.CreateRibbonTab(tabName); } catch { }

                // ===== VIEW ANALYTICS PANEL =====
                var viewPanel = application.CreateRibbonPanel(tabName, Localization.Get("Panel.ViewAnalytics"));

                // Initialize Views
                var initializeBtn = new PushButtonData(
                    "InitializeViews",
                    Localization.Get("Ribbon.InitializeViews"),
                    typeof(Application).Assembly.Location,
                    "ViewTracker.Commands.InitializeViewsCommand"
                );
                initializeBtn.ToolTip = Localization.Get("Ribbon.InitializeViewsTooltip");
                viewPanel.AddItem(initializeBtn);

                // Export CSV
                var exportBtn = new PushButtonData(
                    "ExportCsv",
                    Localization.Get("Ribbon.ExportCSV"),
                    typeof(Application).Assembly.Location,
                    "ViewTracker.Commands.ExportCsvCommand"
                );
                exportBtn.ToolTip = Localization.Get("Ribbon.ExportCSVTooltip");
                viewPanel.AddItem(exportBtn);

                // ===== PROGRAM PANEL =====
                var programPanel = application.CreateRibbonPanel(tabName, Localization.Get("Panel.Program"));

                // Template
                var downloadTemplateBtn = new PushButtonData(
                    "Template",
                    Localization.Get("Ribbon.Template"),
                    typeof(Application).Assembly.Location,
                    "ViewTracker.Commands.DownloadTemplateCommand"
                );
                downloadTemplateBtn.ToolTip = Localization.Get("Ribbon.TemplateTooltip");
                programPanel.AddItem(downloadTemplateBtn);

                // Import Program
                var importProgramBtn = new PushButtonData(
                    "ImportProgram",
                    Localization.Get("Ribbon.ImportProgram"),
                    typeof(Application).Assembly.Location,
                    "ViewTracker.Commands.ImportProgramCommand"
                );
                importProgramBtn.ToolTip = Localization.Get("Ribbon.ImportProgramTooltip");
                programPanel.AddItem(importProgramBtn);

                // Sync Real Area
                var syncAreaBtn = new PushButtonData(
                    "SyncRealArea",
                    Localization.Get("Ribbon.SyncRealArea"),
                    typeof(Application).Assembly.Location,
                    "ViewTracker.Commands.SyncRealAreaCommand"
                );
                syncAreaBtn.ToolTip = Localization.Get("Ribbon.SyncRealAreaTooltip");
                programPanel.AddItem(syncAreaBtn);

                // Convert to Rooms
                var convertToRoomsBtn = new PushButtonData(
                    "ConvertToRooms",
                    Localization.Get("Ribbon.ConvertToRooms"),
                    typeof(Application).Assembly.Location,
                    "ViewTracker.Commands.ConvertToRoomsCommand"
                );
                convertToRoomsBtn.ToolTip = Localization.Get("Ribbon.ConvertToRoomsTooltip");
                programPanel.AddItem(convertToRoomsBtn);

                // ===== VERSIONS PANEL =====
                var trackerPanel = application.CreateRibbonPanel(tabName, Localization.Get("Panel.Versions"));

                // Entity Type ComboBox
                var comboBoxData = new ComboBoxData("EntityTypeCombo");
                var comboBox = trackerPanel.AddItem(comboBoxData) as ComboBox;
                comboBox.ToolTip = "Select entity type to track (Rooms, Doors, or Elements)";
                comboBox.LongDescription = "Choose which type of elements you want to snapshot, compare, or view history for.";

                var roomItemData = new ComboBoxMemberData("Room", Localization.Get("EntityType.Rooms"));
                roomItemData.GroupName = "Entity Type";
                var roomItem = comboBox.AddItem(roomItemData);

                var doorItemData = new ComboBoxMemberData("Door", Localization.Get("EntityType.Doors"));
                doorItemData.GroupName = "Entity Type";
                var doorItem = comboBox.AddItem(doorItemData);

                var elementItemData = new ComboBoxMemberData("Element", Localization.Get("EntityType.Elements"));
                elementItemData.GroupName = "Entity Type";
                var elementItem = comboBox.AddItem(elementItemData);

                comboBox.CurrentChanged += (sender, e) =>
                {
                    var selectedComboBox = sender as ComboBox;
                    if (selectedComboBox?.Current != null)
                    {
                        TrackerContext.CurrentEntityType = selectedComboBox.Current.Name switch
                        {
                            "Door" => TrackerContext.EntityType.Door,
                            "Element" => TrackerContext.EntityType.Element,
                            _ => TrackerContext.EntityType.Room
                        };
                    }
                };

                // Set default to Room
                comboBox.Current = roomItem;
                TrackerContext.CurrentEntityType = TrackerContext.EntityType.Room;

                trackerPanel.AddSeparator();

                // Create regular buttons with 16x16 icons
                var snapshotBtn = new PushButtonData(
                    "UnifiedSnapshot",
                    Localization.Get("Ribbon.Snapshot"),
                    typeof(Application).Assembly.Location,
                    "ViewTracker.Commands.UnifiedSnapshotCommand"
                );
                snapshotBtn.ToolTip = Localization.Get("Ribbon.SnapshotTooltip");
                try
                {
                    string assemblyPath = typeof(Application).Assembly.Location;
                    string iconPath16 = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(assemblyPath), "Resources", "Icons", "Snapshot16.png");
                    string iconPath32 = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(assemblyPath), "Resources", "Icons", "Snapshot32.png");
                    if (System.IO.File.Exists(iconPath16))
                    {
                        snapshotBtn.Image = new BitmapImage(new Uri(iconPath16, UriKind.Absolute));
                    }
                    if (System.IO.File.Exists(iconPath32))
                    {
                        snapshotBtn.LargeImage = new BitmapImage(new Uri(iconPath32, UriKind.Absolute));
                    }
                }
                catch { }
                trackerPanel.AddItem(snapshotBtn);

                var compareBtn = new PushButtonData(
                    "UnifiedCompare",
                    Localization.Get("Ribbon.Compare"),
                    typeof(Application).Assembly.Location,
                    "ViewTracker.Commands.UnifiedCompareCommand"
                );
                compareBtn.ToolTip = Localization.Get("Ribbon.CompareTooltip");
                try
                {
                    string assemblyPath = typeof(Application).Assembly.Location;
                    string iconPath16 = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(assemblyPath), "Resources", "Icons", "Compare16.png");
                    string iconPath32 = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(assemblyPath), "Resources", "Icons", "Compare32.png");
                    if (System.IO.File.Exists(iconPath16))
                    {
                        compareBtn.Image = new BitmapImage(new Uri(iconPath16, UriKind.Absolute));
                    }
                    if (System.IO.File.Exists(iconPath32))
                    {
                        compareBtn.LargeImage = new BitmapImage(new Uri(iconPath32, UriKind.Absolute));
                    }
                }
                catch { }
                trackerPanel.AddItem(compareBtn);

                var historyBtn = new PushButtonData(
                    "UnifiedHistory",
                    Localization.Get("Ribbon.History"),
                    typeof(Application).Assembly.Location,
                    "ViewTracker.Commands.UnifiedHistoryCommand"
                );
                historyBtn.ToolTip = Localization.Get("Ribbon.HistoryTooltip");
                try
                {
                    string assemblyPath = typeof(Application).Assembly.Location;
                    string iconPath16 = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(assemblyPath), "Resources", "Icons", "History16.png");
                    string iconPath32 = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(assemblyPath), "Resources", "Icons", "History32.png");
                    if (System.IO.File.Exists(iconPath16))
                    {
                        historyBtn.Image = new BitmapImage(new Uri(iconPath16, UriKind.Absolute));
                    }
                    if (System.IO.File.Exists(iconPath32))
                    {
                        historyBtn.LargeImage = new BitmapImage(new Uri(iconPath32, UriKind.Absolute));
                    }
                }
                catch { }
                trackerPanel.AddItem(historyBtn);

                trackerPanel.AddSeparator();

                // Unified Restore button (context-aware: Rooms/Doors/Elements)
                var restoreBtn = new PushButtonData(
                    "UnifiedRestore",
                    Localization.Get("Ribbon.Restore"),
                    typeof(Application).Assembly.Location,
                    "ViewTracker.Commands.UnifiedRestoreCommand"
                );
                restoreBtn.ToolTip = Localization.Get("Ribbon.RestoreTooltip");
                restoreBtn.LongDescription = "Rooms: Restore all parameters and recreate deleted rooms\n" +
                                            "Doors/Elements: Restore instance parameters only (existing elements)";
                try
                {
                    string assemblyPath = typeof(Application).Assembly.Location;
                    string iconPath16 = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(assemblyPath), "Resources", "Icons", "restore16.png");
                    string iconPath32 = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(assemblyPath), "Resources", "Icons", "restore32.png");
                    if (System.IO.File.Exists(iconPath16))
                    {
                        restoreBtn.Image = new BitmapImage(new Uri(iconPath16, UriKind.Absolute));
                    }
                    if (System.IO.File.Exists(iconPath32))
                    {
                        restoreBtn.LargeImage = new BitmapImage(new Uri(iconPath32, UriKind.Absolute));
                    }
                }
                catch { }
                trackerPanel.AddItem(restoreBtn);

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating ribbon button: {ex.Message}");
            }
        }

        // Existing ViewActivated handler (unchanged)
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

                // TEMPORARILY DISABLED: Supabase view tracking to improve performance
                // Re-enable when Supabase connection is stable
                /*
                System.Threading.Tasks.Task.Run(async () =>
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
                */
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
