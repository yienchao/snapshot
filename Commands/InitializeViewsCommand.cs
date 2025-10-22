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

            var projectIdStr = document.ProjectInformation.LookupParameter("projectID")?.AsString();
            Guid projectId = Guid.Empty;
            if (!Guid.TryParse(projectIdStr, out projectId))
            {
                TaskDialog.Show(
                    "ViewTracker",
                    "Missing or invalid 'projectID' parameter!\n\n" +
                    "Please assign a valid projectID to the 'projectID' Project Information parameter " +
                    "before using ViewTracker batch initialize."
                );
                return;
            }

            try
            {
                var views = GetAllTrackedViews(document);
                var fileName = System.IO.Path.GetFileNameWithoutExtension(document.PathName);
                if (string.IsNullOrEmpty(fileName))
                    fileName = document.Title;

                // Run sync and wait for completion to show real result
                TaskDialog.Show("ViewTracker", $"Initializing {views.Count} views...\nPlease wait...");

                Task.Run(async () =>
                {
                    try
                    {
                        await OptimizedInitializeAndCleanViewsInDatabase(document, views, fileName, projectId);
                        System.Diagnostics.Debug.WriteLine($"✓ Successfully initialized {views.Count} views to Supabase");

                        // Show success
                        System.Windows.MessageBox.Show(
                            $"Successfully synchronized {views.Count} views to Supabase!",
                            "Success",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Information);
                    }
                    catch (Exception asyncEx)
                    {
                        // Log detailed error for debugging
                        var errorLog = $"\n========== SUPABASE SYNC ERROR ==========\n" +
                                      $"Time: {DateTime.Now}\n" +
                                      $"Error: {asyncEx.Message}\n" +
                                      $"Type: {asyncEx.GetType().Name}\n" +
                                      $"Stack Trace:\n{asyncEx.StackTrace}\n";

                        if (asyncEx.InnerException != null)
                        {
                            errorLog += $"Inner Exception: {asyncEx.InnerException.Message}\n";
                        }

                        errorLog += $"=========================================\n";

                        System.Diagnostics.Debug.WriteLine(errorLog);

                        // Write to log file
                        try
                        {
                            var logPath = System.IO.Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                "Snapshot", "error.log");
                            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath));
                            System.IO.File.AppendAllText(logPath, errorLog);
                        }
                        catch { /* Ignore if can't write log */ }

                        // Show error dialog to user
                        System.Windows.MessageBox.Show(
                            $"Failed to sync views to Supabase:\n\n" +
                            $"{asyncEx.Message}\n\n" +
                            $"Full error log saved to:\n%APPDATA%\\Snapshot\\error.log",
                            "Initialization Error",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Error);
                    }
                });
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
                .Where(v =>
                    !v.IsTemplate &&
                    trackedTypes.Contains(v.ViewType) &&
                    !(v is ViewSchedule vs && vs.Definition.CategoryId == new ElementId(BuiltInCategory.OST_Revisions))
                )
                .ToList();
        }

        private async Task OptimizedInitializeAndCleanViewsInDatabase(Document document, List<View> views, string fileName, Guid projectId)
        {
            try
            {
                var supabaseService = new SupabaseService();
                await supabaseService.InitializeAsync();

                var allViewports = new FilteredElementCollector(document)
                    .OfClass(typeof(Viewport))
                    .Cast<Viewport>()
                    .ToList();

                var allScheduleInstances = new FilteredElementCollector(document)
                    .OfClass(typeof(ScheduleSheetInstance))
                    .Cast<ScheduleSheetInstance>()
                    .ToList();

                var allSheets = new FilteredElementCollector(document)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
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

                        if (view.ViewType == ViewType.Schedule)
                        {
                            var sheetNumbers = new List<string>();
                            foreach (var sInst in allScheduleInstances)
                            {
                                Element scheduleElem = document.GetElement(sInst.ScheduleId);
                                bool isRevisionSchedule = scheduleElem is ViewSchedule vs &&
                                    vs.Definition.CategoryId == new ElementId(BuiltInCategory.OST_Revisions);

                                // Use .Value property, version-proof as int
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
                    }

                    WorksharingTooltipInfo info = null;
                    try { info = WorksharingUtils.GetWorksharingTooltipInfo(view.Document, view.Id); } catch { }
                    string creatorName = info?.Creator ?? "";
                    string lastChangedBy = info?.LastChangedBy ?? "";

                    var currentDateTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

                    var record = new ViewActivationRecord
                    {
                        ViewUniqueId = view.UniqueId,
                        FileName = fileName,
                        ViewId = view.Id.Value.ToString(),
                        ViewName = viewName,
                        ViewType = viewType,
                        LastViewer = null,
                        LastActivationDate = null,
                        ActivationCount = 0,
                        LastInitialization = currentDateTime,
                        CreatorName = creatorName,
                        LastChangedBy = lastChangedBy,
                        SheetNumber = sheetNumber,
                        ViewNumber = viewNumber,
                        ProjectId = projectId
                    };

                    allRecords.Add(record);
                    currentUniqueIds.Add(view.UniqueId);
                }

                await supabaseService.BulkUpsertInitViewsPreserveAsync(allRecords, fileName);

                var existingUniqueIds = await supabaseService.GetExistingViewUniqueIdsAsync(fileName);
                var orphanIds = existingUniqueIds.Except(currentUniqueIds).ToList();

                if (orphanIds.Any())
                {
                    await supabaseService.BulkDeleteOrphanedRecordsAsync(orphanIds, fileName);
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
    }
}
