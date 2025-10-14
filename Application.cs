using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;

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
                string tabName = "Data Tracker";
                try { application.CreateRibbonTab(tabName); } catch { }

                // ===== VIEW TRACKER PANEL =====
                var viewPanel = application.CreateRibbonPanel(tabName, "View Tracker");

                // Initialize Views
                var initializeBtn = new PushButtonData(
                    "InitializeViews",
                    "Initialize\nViews",
                    typeof(Application).Assembly.Location,
                    "ViewTracker.Commands.InitializeViewsCommand"
                );
                initializeBtn.ToolTip = "Initialize all views in the project database";
                viewPanel.AddItem(initializeBtn);

                // Export CSV
                var exportBtn = new PushButtonData(
                    "ExportCsv",
                    "Export Views\nCSV",
                    typeof(Application).Assembly.Location,
                    "ViewTracker.Commands.ExportCsvCommand"
                );
                exportBtn.ToolTip = "Export Supabase view_activations for this project's projectID to CSV";
                viewPanel.AddItem(exportBtn);

                // ===== UNIFIED TRACKER PANEL =====
                var trackerPanel = application.CreateRibbonPanel(tabName, "Tracker");

                // Entity Type ComboBox
                var comboBoxData = new ComboBoxData("EntityTypeCombo");
                var comboBox = trackerPanel.AddItem(comboBoxData) as ComboBox;
                comboBox.ToolTip = "Select entity type to track (Rooms, Doors, or Elements)";
                comboBox.LongDescription = "Choose which type of elements you want to snapshot, compare, or view history for.";

                var roomItemData = new ComboBoxMemberData("Room", "Rooms");
                roomItemData.GroupName = "Entity Type";
                var roomItem = comboBox.AddItem(roomItemData);

                var doorItemData = new ComboBoxMemberData("Door", "Doors");
                doorItemData.GroupName = "Entity Type";
                var doorItem = comboBox.AddItem(doorItemData);

                var elementItemData = new ComboBoxMemberData("Element", "Elements");
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

                // Snapshot button
                var snapshotBtn = new PushButtonData(
                    "UnifiedSnapshot",
                    "📷\nSnapshot",
                    typeof(Application).Assembly.Location,
                    "ViewTracker.Commands.UnifiedSnapshotCommand"
                );
                snapshotBtn.ToolTip = "Create snapshot with trackID (draft or official)";
                snapshotBtn.LongDescription = "Captures current data to Supabase for the selected entity type. You'll choose draft or official when creating.";
                trackerPanel.AddItem(snapshotBtn);

                trackerPanel.AddSeparator();

                // Compare split button
                var compareBtn = new PushButtonData(
                    "UnifiedCompare",
                    "⇄\nCompare to\nSnapshot",
                    typeof(Application).Assembly.Location,
                    "ViewTracker.Commands.UnifiedCompareCommand"
                );
                compareBtn.ToolTip = "Compare current state with a snapshot version";

                var compareTwoBtn = new PushButtonData(
                    "UnifiedCompareTwoVersions",
                    "Compare\nSnapshots",
                    typeof(Application).Assembly.Location,
                    "ViewTracker.Commands.UnifiedCompareTwoVersionsCommand"
                );
                compareTwoBtn.ToolTip = "Compare two snapshot versions with each other";

                var compareSplit = trackerPanel.AddItem(new SplitButtonData("UnifiedCompareSplit", "⇄ Compare")) as SplitButton;
                compareSplit.AddPushButton(compareBtn);
                compareSplit.AddPushButton(compareTwoBtn);

                trackerPanel.AddSeparator();

                // History button
                var historyBtn = new PushButtonData(
                    "UnifiedHistory",
                    "⟲\nHistory",
                    typeof(Application).Assembly.Location,
                    "ViewTracker.Commands.UnifiedHistoryCommand"
                );
                historyBtn.ToolTip = "View history of a selected element across all versions";
                trackerPanel.AddItem(historyBtn);

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
