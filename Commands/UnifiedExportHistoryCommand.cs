using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using OfficeOpenXml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class UnifiedExportHistoryCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Route to appropriate export based on selected entity type
                IExternalCommand targetCommand = TrackerContext.CurrentEntityType switch
                {
                    TrackerContext.EntityType.Room => new RoomExportHistoryCommand(),
                    TrackerContext.EntityType.Door => new DoorExportHistoryCommand(),
                    TrackerContext.EntityType.Element => new ElementExportHistoryCommand(),
                    _ => new RoomExportHistoryCommand()
                };

                return targetCommand.Execute(commandData, ref message, elements);
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Error", $"Failed to execute export history command:\n{ex.Message}");
                return Result.Failed;
            }
        }
    }

    // Room Export History Command
    [Transaction(TransactionMode.ReadOnly)]
    public class RoomExportHistoryCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument.Document;

            // Validate projectID
            var projectIdStr = doc.ProjectInformation.LookupParameter("projectID")?.AsString();
            if (!Guid.TryParse(projectIdStr, out Guid projectId))
            {
                Autodesk.Revit.UI.TaskDialog.Show("Error", "This file does not have a valid projectID parameter.");
                return Result.Failed;
            }

            // Get all room snapshots for this project
            var supabaseService = new SupabaseService();
            List<RoomSnapshot> allSnapshots = new List<RoomSnapshot>();

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await supabaseService.InitializeAsync();

                    // Get all versions
                    var versions = await supabaseService.GetAllVersionNamesAsync(projectId);

                    // Get all snapshots for all versions
                    foreach (var version in versions)
                    {
                        var snapshots = await supabaseService.GetRoomsByVersionAsync(version, projectId);
                        allSnapshots.AddRange(snapshots);
                    }
                }).Wait();
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Error", $"Failed to load room snapshots:\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            if (!allSnapshots.Any())
            {
                Autodesk.Revit.UI.TaskDialog.Show("No Data", "No room snapshot history found for this project.");
                return Result.Cancelled;
            }

            // Ask user for save location
            var saveDialog = new SaveFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx|CSV Files (*.csv)|*.csv",
                DefaultExt = "xlsx",
                FileName = $"RoomHistory_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (saveDialog.ShowDialog() != true)
                return Result.Cancelled;

            {

                string filePath = saveDialog.FileName;
                string extension = Path.GetExtension(filePath).ToLower();

                try
                {
                    if (extension == ".xlsx")
                        ExportToExcel(allSnapshots, filePath);
                    else
                        ExportToCsv(allSnapshots, filePath);

                    Autodesk.Revit.UI.TaskDialog.Show("Success", $"Room history exported successfully to:\n{filePath}\n\nTotal records: {allSnapshots.Count}");
                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Error", $"Failed to export:\n{ex.Message}");
                    return Result.Failed;
                }
            }
        }

        private void ExportToExcel(List<RoomSnapshot> snapshots, string filePath)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            using (var package = new ExcelPackage())
            {
                var worksheet = package.Workbook.Worksheets.Add("Room History");

                // Headers
                int col = 1;
                worksheet.Cells[1, col++].Value = "Track ID";
                worksheet.Cells[1, col++].Value = "Version Name";
                worksheet.Cells[1, col++].Value = "Snapshot Date";
                worksheet.Cells[1, col++].Value = "Created By";
                worksheet.Cells[1, col++].Value = "Is Official";
                worksheet.Cells[1, col++].Value = "Room Number";
                worksheet.Cells[1, col++].Value = "Room Name";
                worksheet.Cells[1, col++].Value = "Level";
                worksheet.Cells[1, col++].Value = "Area";
                worksheet.Cells[1, col++].Value = "Perimeter";
                worksheet.Cells[1, col++].Value = "Volume";
                worksheet.Cells[1, col++].Value = "Unbound Height";
                worksheet.Cells[1, col++].Value = "Occupancy";
                worksheet.Cells[1, col++].Value = "Department";
                worksheet.Cells[1, col++].Value = "Phase";
                worksheet.Cells[1, col++].Value = "Base Finish";
                worksheet.Cells[1, col++].Value = "Ceiling Finish";
                worksheet.Cells[1, col++].Value = "Wall Finish";
                worksheet.Cells[1, col++].Value = "Floor Finish";
                worksheet.Cells[1, col++].Value = "Comments";
                worksheet.Cells[1, col++].Value = "Occupant";

                // Collect all custom parameter names
                var allParamNames = new HashSet<string>();
                foreach (var snapshot in snapshots)
                {
                    if (snapshot.AllParameters != null)
                    {
                        foreach (var key in snapshot.AllParameters.Keys)
                            allParamNames.Add(key);
                    }
                }
                var sortedParamNames = allParamNames.OrderBy(x => x).ToList();
                foreach (var paramName in sortedParamNames)
                {
                    worksheet.Cells[1, col++].Value = paramName;
                }

                // Data rows (sorted by TrackID and SnapshotDate)
                int row = 2;
                foreach (var snapshot in snapshots.OrderBy(s => s.TrackId).ThenBy(s => s.SnapshotDate))
                {
                    col = 1;
                    worksheet.Cells[row, col++].Value = snapshot.TrackId;
                    worksheet.Cells[row, col++].Value = snapshot.VersionName;
                    worksheet.Cells[row, col++].Value = snapshot.SnapshotDate?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                    worksheet.Cells[row, col++].Value = snapshot.CreatedBy;
                    worksheet.Cells[row, col++].Value = snapshot.IsOfficial ? "Yes" : "No";
                    worksheet.Cells[row, col++].Value = snapshot.RoomNumber;
                    worksheet.Cells[row, col++].Value = snapshot.RoomName;
                    worksheet.Cells[row, col++].Value = snapshot.Level;
                    worksheet.Cells[row, col++].Value = snapshot.Area;
                    worksheet.Cells[row, col++].Value = snapshot.Perimeter;
                    worksheet.Cells[row, col++].Value = snapshot.Volume;
                    worksheet.Cells[row, col++].Value = snapshot.UnboundHeight;
                    worksheet.Cells[row, col++].Value = snapshot.Occupancy;
                    worksheet.Cells[row, col++].Value = snapshot.Department;
                    worksheet.Cells[row, col++].Value = snapshot.Phase;
                    worksheet.Cells[row, col++].Value = snapshot.BaseFinish;
                    worksheet.Cells[row, col++].Value = snapshot.CeilingFinish;
                    worksheet.Cells[row, col++].Value = snapshot.WallFinish;
                    worksheet.Cells[row, col++].Value = snapshot.FloorFinish;
                    worksheet.Cells[row, col++].Value = snapshot.Comments;
                    worksheet.Cells[row, col++].Value = snapshot.Occupant;

                    // Add custom parameters
                    foreach (var paramName in sortedParamNames)
                    {
                        var value = snapshot.AllParameters?.ContainsKey(paramName) == true
                            ? snapshot.AllParameters[paramName]?.ToString()
                            : "";
                        worksheet.Cells[row, col++].Value = value;
                    }

                    row++;
                }

                // Format header
                using (var range = worksheet.Cells[1, 1, 1, col - 1])
                {
                    range.Style.Font.Bold = true;
                    range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                    range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
                }

                worksheet.Cells.AutoFitColumns();
                package.SaveAs(new FileInfo(filePath));
            }
        }

        private void ExportToCsv(List<RoomSnapshot> snapshots, string filePath)
        {
            using (var writer = new StreamWriter(filePath))
            {
                // Collect all custom parameter names
                var allParamNames = new HashSet<string>();
                foreach (var snapshot in snapshots)
                {
                    if (snapshot.AllParameters != null)
                    {
                        foreach (var key in snapshot.AllParameters.Keys)
                            allParamNames.Add(key);
                    }
                }
                var sortedParamNames = allParamNames.OrderBy(x => x).ToList();

                // Write header
                var headers = new List<string>
                {
                    "Track ID", "Version Name", "Snapshot Date", "Created By", "Is Official",
                    "Room Number", "Room Name", "Level", "Area", "Perimeter", "Volume",
                    "Unbound Height", "Occupancy", "Department", "Phase",
                    "Base Finish", "Ceiling Finish", "Wall Finish", "Floor Finish",
                    "Comments", "Occupant"
                };
                headers.AddRange(sortedParamNames);
                writer.WriteLine(string.Join(",", headers.Select(h => $"\"{h}\"")));

                // Write data (sorted by TrackID and SnapshotDate)
                foreach (var snapshot in snapshots.OrderBy(s => s.TrackId).ThenBy(s => s.SnapshotDate))
                {
                    var values = new List<string>
                    {
                        CsvEscape(snapshot.TrackId),
                        CsvEscape(snapshot.VersionName),
                        CsvEscape(snapshot.SnapshotDate?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                        CsvEscape(snapshot.CreatedBy),
                        CsvEscape(snapshot.IsOfficial ? "Yes" : "No"),
                        CsvEscape(snapshot.RoomNumber),
                        CsvEscape(snapshot.RoomName),
                        CsvEscape(snapshot.Level),
                        CsvEscape(snapshot.Area?.ToString()),
                        CsvEscape(snapshot.Perimeter?.ToString()),
                        CsvEscape(snapshot.Volume?.ToString()),
                        CsvEscape(snapshot.UnboundHeight?.ToString()),
                        CsvEscape(snapshot.Occupancy),
                        CsvEscape(snapshot.Department),
                        CsvEscape(snapshot.Phase),
                        CsvEscape(snapshot.BaseFinish),
                        CsvEscape(snapshot.CeilingFinish),
                        CsvEscape(snapshot.WallFinish),
                        CsvEscape(snapshot.FloorFinish),
                        CsvEscape(snapshot.Comments),
                        CsvEscape(snapshot.Occupant)
                    };

                    // Add custom parameters
                    foreach (var paramName in sortedParamNames)
                    {
                        var value = snapshot.AllParameters?.ContainsKey(paramName) == true
                            ? snapshot.AllParameters[paramName]?.ToString()
                            : "";
                        values.Add(CsvEscape(value));
                    }

                    writer.WriteLine(string.Join(",", values));
                }
            }
        }

        private string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "\"\"";
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
    }

    // Door Export History Command
    [Transaction(TransactionMode.ReadOnly)]
    public class DoorExportHistoryCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument.Document;

            var projectIdStr = doc.ProjectInformation.LookupParameter("projectID")?.AsString();
            if (!Guid.TryParse(projectIdStr, out Guid projectId))
            {
                Autodesk.Revit.UI.TaskDialog.Show("Error", "This file does not have a valid projectID parameter.");
                return Result.Failed;
            }

            var supabaseService = new SupabaseService();
            List<DoorSnapshot> allSnapshots = new List<DoorSnapshot>();

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await supabaseService.InitializeAsync();
                    var versions = await supabaseService.GetAllDoorVersionNamesAsync(projectId);
                    foreach (var version in versions)
                    {
                        var snapshots = await supabaseService.GetDoorsByVersionAsync(version, projectId);
                        allSnapshots.AddRange(snapshots);
                    }
                }).Wait();
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Error", $"Failed to load door snapshots:\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            if (!allSnapshots.Any())
            {
                Autodesk.Revit.UI.TaskDialog.Show("No Data", "No door snapshot history found for this project.");
                return Result.Cancelled;
            }

            var saveDialog = new SaveFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx|CSV Files (*.csv)|*.csv",
                DefaultExt = "xlsx",
                FileName = $"DoorHistory_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (saveDialog.ShowDialog() != true)
                return Result.Cancelled;

            {

                string filePath = saveDialog.FileName;
                string extension = Path.GetExtension(filePath).ToLower();

                try
                {
                    if (extension == ".xlsx")
                        ExportToExcel(allSnapshots, filePath);
                    else
                        ExportToCsv(allSnapshots, filePath);

                    Autodesk.Revit.UI.TaskDialog.Show("Success", $"Door history exported successfully to:\n{filePath}\n\nTotal records: {allSnapshots.Count}");
                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Error", $"Failed to export:\n{ex.Message}");
                    return Result.Failed;
                }
            }
        }

        private void ExportToExcel(List<DoorSnapshot> snapshots, string filePath)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            using (var package = new ExcelPackage())
            {
                var worksheet = package.Workbook.Worksheets.Add("Door History");

                int col = 1;
                worksheet.Cells[1, col++].Value = "Track ID";
                worksheet.Cells[1, col++].Value = "Version Name";
                worksheet.Cells[1, col++].Value = "Snapshot Date";
                worksheet.Cells[1, col++].Value = "Created By";
                worksheet.Cells[1, col++].Value = "Is Official";
                worksheet.Cells[1, col++].Value = "Family Name";
                worksheet.Cells[1, col++].Value = "Type Name";
                worksheet.Cells[1, col++].Value = "Mark";
                worksheet.Cells[1, col++].Value = "Level";
                worksheet.Cells[1, col++].Value = "Fire Rating";
                worksheet.Cells[1, col++].Value = "Door Width";
                worksheet.Cells[1, col++].Value = "Door Height";
                worksheet.Cells[1, col++].Value = "Phase Created";
                worksheet.Cells[1, col++].Value = "Phase Demolished";
                worksheet.Cells[1, col++].Value = "Comments";

                var allParamNames = new HashSet<string>();
                foreach (var snapshot in snapshots)
                {
                    if (snapshot.AllParameters != null)
                    {
                        foreach (var key in snapshot.AllParameters.Keys)
                            allParamNames.Add(key);
                    }
                }
                var sortedParamNames = allParamNames.OrderBy(x => x).ToList();
                foreach (var paramName in sortedParamNames)
                {
                    worksheet.Cells[1, col++].Value = paramName;
                }

                int row = 2;
                foreach (var snapshot in snapshots.OrderBy(s => s.TrackId).ThenBy(s => s.SnapshotDate))
                {
                    col = 1;
                    worksheet.Cells[row, col++].Value = snapshot.TrackId;
                    worksheet.Cells[row, col++].Value = snapshot.VersionName;
                    worksheet.Cells[row, col++].Value = snapshot.SnapshotDate?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                    worksheet.Cells[row, col++].Value = snapshot.CreatedBy;
                    worksheet.Cells[row, col++].Value = snapshot.IsOfficial ? "Yes" : "No";
                    worksheet.Cells[row, col++].Value = snapshot.FamilyName;
                    worksheet.Cells[row, col++].Value = snapshot.TypeName;
                    worksheet.Cells[row, col++].Value = snapshot.Mark;
                    worksheet.Cells[row, col++].Value = snapshot.Level;
                    worksheet.Cells[row, col++].Value = snapshot.FireRating;
                    worksheet.Cells[row, col++].Value = snapshot.DoorWidth;
                    worksheet.Cells[row, col++].Value = snapshot.DoorHeight;
                    worksheet.Cells[row, col++].Value = snapshot.PhaseCreated;
                    worksheet.Cells[row, col++].Value = snapshot.PhaseDemolished;
                    worksheet.Cells[row, col++].Value = snapshot.Comments;

                    foreach (var paramName in sortedParamNames)
                    {
                        var value = snapshot.AllParameters?.ContainsKey(paramName) == true
                            ? snapshot.AllParameters[paramName]?.ToString()
                            : "";
                        worksheet.Cells[row, col++].Value = value;
                    }

                    row++;
                }

                using (var range = worksheet.Cells[1, 1, 1, col - 1])
                {
                    range.Style.Font.Bold = true;
                    range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                    range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
                }

                worksheet.Cells.AutoFitColumns();
                package.SaveAs(new FileInfo(filePath));
            }
        }

        private void ExportToCsv(List<DoorSnapshot> snapshots, string filePath)
        {
            using (var writer = new StreamWriter(filePath))
            {
                var allParamNames = new HashSet<string>();
                foreach (var snapshot in snapshots)
                {
                    if (snapshot.AllParameters != null)
                    {
                        foreach (var key in snapshot.AllParameters.Keys)
                            allParamNames.Add(key);
                    }
                }
                var sortedParamNames = allParamNames.OrderBy(x => x).ToList();

                var headers = new List<string>
                {
                    "Track ID", "Version Name", "Snapshot Date", "Created By", "Is Official",
                    "Family Name", "Type Name", "Mark", "Level", "Fire Rating",
                    "Door Width", "Door Height", "Phase Created", "Phase Demolished", "Comments"
                };
                headers.AddRange(sortedParamNames);
                writer.WriteLine(string.Join(",", headers.Select(h => $"\"{h}\"")));

                foreach (var snapshot in snapshots.OrderBy(s => s.TrackId).ThenBy(s => s.SnapshotDate))
                {
                    var values = new List<string>
                    {
                        CsvEscape(snapshot.TrackId),
                        CsvEscape(snapshot.VersionName),
                        CsvEscape(snapshot.SnapshotDate?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                        CsvEscape(snapshot.CreatedBy),
                        CsvEscape(snapshot.IsOfficial ? "Yes" : "No"),
                        CsvEscape(snapshot.FamilyName),
                        CsvEscape(snapshot.TypeName),
                        CsvEscape(snapshot.Mark),
                        CsvEscape(snapshot.Level),
                        CsvEscape(snapshot.FireRating),
                        CsvEscape(snapshot.DoorWidth?.ToString()),
                        CsvEscape(snapshot.DoorHeight?.ToString()),
                        CsvEscape(snapshot.PhaseCreated),
                        CsvEscape(snapshot.PhaseDemolished),
                        CsvEscape(snapshot.Comments)
                    };

                    foreach (var paramName in sortedParamNames)
                    {
                        var value = snapshot.AllParameters?.ContainsKey(paramName) == true
                            ? snapshot.AllParameters[paramName]?.ToString()
                            : "";
                        values.Add(CsvEscape(value));
                    }

                    writer.WriteLine(string.Join(",", values));
                }
            }
        }

        private string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "\"\"";
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
    }

    // Element Export History Command
    [Transaction(TransactionMode.ReadOnly)]
    public class ElementExportHistoryCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument.Document;

            var projectIdStr = doc.ProjectInformation.LookupParameter("projectID")?.AsString();
            if (!Guid.TryParse(projectIdStr, out Guid projectId))
            {
                Autodesk.Revit.UI.TaskDialog.Show("Error", "This file does not have a valid projectID parameter.");
                return Result.Failed;
            }

            var supabaseService = new SupabaseService();
            List<ElementSnapshot> allSnapshots = new List<ElementSnapshot>();

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await supabaseService.InitializeAsync();
                    var versions = await supabaseService.GetAllElementVersionNamesAsync(projectId);
                    foreach (var version in versions)
                    {
                        var snapshots = await supabaseService.GetElementsByVersionAsync(version, projectId);
                        allSnapshots.AddRange(snapshots);
                    }
                }).Wait();
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Error", $"Failed to load element snapshots:\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            if (!allSnapshots.Any())
            {
                Autodesk.Revit.UI.TaskDialog.Show("No Data", "No element snapshot history found for this project.");
                return Result.Cancelled;
            }

            var saveDialog = new SaveFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx|CSV Files (*.csv)|*.csv",
                DefaultExt = "xlsx",
                FileName = $"ElementHistory_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (saveDialog.ShowDialog() != true)
                return Result.Cancelled;

            {

                string filePath = saveDialog.FileName;
                string extension = Path.GetExtension(filePath).ToLower();

                try
                {
                    if (extension == ".xlsx")
                        ExportToExcel(allSnapshots, filePath);
                    else
                        ExportToCsv(allSnapshots, filePath);

                    Autodesk.Revit.UI.TaskDialog.Show("Success", $"Element history exported successfully to:\n{filePath}\n\nTotal records: {allSnapshots.Count}");
                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Error", $"Failed to export:\n{ex.Message}");
                    return Result.Failed;
                }
            }
        }

        private void ExportToExcel(List<ElementSnapshot> snapshots, string filePath)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            using (var package = new ExcelPackage())
            {
                var worksheet = package.Workbook.Worksheets.Add("Element History");

                int col = 1;
                worksheet.Cells[1, col++].Value = "Track ID";
                worksheet.Cells[1, col++].Value = "Version Name";
                worksheet.Cells[1, col++].Value = "Snapshot Date";
                worksheet.Cells[1, col++].Value = "Created By";
                worksheet.Cells[1, col++].Value = "Is Official";
                worksheet.Cells[1, col++].Value = "Category";
                worksheet.Cells[1, col++].Value = "Family Name";
                worksheet.Cells[1, col++].Value = "Type Name";
                worksheet.Cells[1, col++].Value = "Mark";
                worksheet.Cells[1, col++].Value = "Level";
                worksheet.Cells[1, col++].Value = "Phase Created";
                worksheet.Cells[1, col++].Value = "Phase Demolished";
                worksheet.Cells[1, col++].Value = "Comments";

                var allParamNames = new HashSet<string>();
                foreach (var snapshot in snapshots)
                {
                    if (snapshot.AllParameters != null)
                    {
                        foreach (var key in snapshot.AllParameters.Keys)
                            allParamNames.Add(key);
                    }
                }
                var sortedParamNames = allParamNames.OrderBy(x => x).ToList();
                foreach (var paramName in sortedParamNames)
                {
                    worksheet.Cells[1, col++].Value = paramName;
                }

                int row = 2;
                foreach (var snapshot in snapshots.OrderBy(s => s.TrackId).ThenBy(s => s.SnapshotDate))
                {
                    col = 1;
                    worksheet.Cells[row, col++].Value = snapshot.TrackId;
                    worksheet.Cells[row, col++].Value = snapshot.VersionName;
                    worksheet.Cells[row, col++].Value = snapshot.SnapshotDate?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                    worksheet.Cells[row, col++].Value = snapshot.CreatedBy;
                    worksheet.Cells[row, col++].Value = snapshot.IsOfficial ? "Yes" : "No";
                    worksheet.Cells[row, col++].Value = snapshot.Category;
                    worksheet.Cells[row, col++].Value = snapshot.FamilyName;
                    worksheet.Cells[row, col++].Value = snapshot.TypeName;
                    worksheet.Cells[row, col++].Value = snapshot.Mark;
                    worksheet.Cells[row, col++].Value = snapshot.Level;
                    worksheet.Cells[row, col++].Value = snapshot.PhaseCreated;
                    worksheet.Cells[row, col++].Value = snapshot.PhaseDemolished;
                    worksheet.Cells[row, col++].Value = snapshot.Comments;

                    foreach (var paramName in sortedParamNames)
                    {
                        var value = snapshot.AllParameters?.ContainsKey(paramName) == true
                            ? snapshot.AllParameters[paramName]?.ToString()
                            : "";
                        worksheet.Cells[row, col++].Value = value;
                    }

                    row++;
                }

                using (var range = worksheet.Cells[1, 1, 1, col - 1])
                {
                    range.Style.Font.Bold = true;
                    range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                    range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
                }

                worksheet.Cells.AutoFitColumns();
                package.SaveAs(new FileInfo(filePath));
            }
        }

        private void ExportToCsv(List<ElementSnapshot> snapshots, string filePath)
        {
            using (var writer = new StreamWriter(filePath))
            {
                var allParamNames = new HashSet<string>();
                foreach (var snapshot in snapshots)
                {
                    if (snapshot.AllParameters != null)
                    {
                        foreach (var key in snapshot.AllParameters.Keys)
                            allParamNames.Add(key);
                    }
                }
                var sortedParamNames = allParamNames.OrderBy(x => x).ToList();

                var headers = new List<string>
                {
                    "Track ID", "Version Name", "Snapshot Date", "Created By", "Is Official",
                    "Category", "Family Name", "Type Name", "Mark", "Level",
                    "Phase Created", "Phase Demolished", "Comments"
                };
                headers.AddRange(sortedParamNames);
                writer.WriteLine(string.Join(",", headers.Select(h => $"\"{h}\"")));

                foreach (var snapshot in snapshots.OrderBy(s => s.TrackId).ThenBy(s => s.SnapshotDate))
                {
                    var values = new List<string>
                    {
                        CsvEscape(snapshot.TrackId),
                        CsvEscape(snapshot.VersionName),
                        CsvEscape(snapshot.SnapshotDate?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                        CsvEscape(snapshot.CreatedBy),
                        CsvEscape(snapshot.IsOfficial ? "Yes" : "No"),
                        CsvEscape(snapshot.Category),
                        CsvEscape(snapshot.FamilyName),
                        CsvEscape(snapshot.TypeName),
                        CsvEscape(snapshot.Mark),
                        CsvEscape(snapshot.Level),
                        CsvEscape(snapshot.PhaseCreated),
                        CsvEscape(snapshot.PhaseDemolished),
                        CsvEscape(snapshot.Comments)
                    };

                    foreach (var paramName in sortedParamNames)
                    {
                        var value = snapshot.AllParameters?.ContainsKey(paramName) == true
                            ? snapshot.AllParameters[paramName]?.ToString()
                            : "";
                        values.Add(CsvEscape(value));
                    }

                    writer.WriteLine(string.Join(",", values));
                }
            }
        }

        private string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "\"\"";
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
    }
}
