using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Nice3point.Revit.Toolkit.External;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class ExportCsvCommand : ExternalCommand
    {
        public override void Execute()
        {
            var doc = UiDocument.Document;

            var projectIdStr = doc.ProjectInformation.LookupParameter("projectID")?.AsString();
            if (!Guid.TryParse(projectIdStr, out Guid projectId))
            {
                TaskDialog.Show("ViewTracker", "Project parameter 'projectID' is missing or not a valid GUID.");
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export ViewTracker CSV",
                Filter = "CSV files (*.csv)|*.csv",
                FileName = $"ViewTracker_{projectId}.csv"
            };
            if (dlg.ShowDialog() != true) return;
            string path = dlg.FileName;

            Task.Run(async () =>
            {
                try
                {
                    var svc = new SupabaseService();
                    await svc.InitializeAsync();

                    var rows = await svc.GetViewActivationsByProjectAsync(projectId);
                    await WriteCsvAsync(path, rows);

                    TaskDialog.Show("ViewTracker", $"Exported {rows.Count} rows to:\n{path}");
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("ViewTracker - Export Error", ex.Message);
                }
            });
        }

        private static async Task WriteCsvAsync(string path, List<ViewActivationRecord> rows)
        {
            string[] headers = new[]
            {
                "project_id","file_name","view_unique_id","view_id","view_name","view_type",
                "sheet_number","view_number","creator_name","last_changed_by",
                "last_viewer","last_activation_date","last_initialization","activation_count"
            };

            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", headers.Select(Escape)));

            foreach (var r in rows)
            {
                var values = new string[]
                {
                    r.ProjectId.ToString(),
                    r.FileName,
                    r.ViewUniqueId,
                    r.ViewId,
                    r.ViewName,
                    r.ViewType,
                    r.SheetNumber,
                    r.ViewNumber,
                    r.CreatorName,
                    r.LastChangedBy,
                    r.LastViewer,
                    r.LastActivationDate,
                    r.LastInitialization,
                    r.ActivationCount.ToString()
                }.Select(Escape);
                sb.AppendLine(string.Join(",", values));
            }

            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
                await writer.WriteAsync(sb.ToString());
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var needsQuotes = s.Contains(",") || s.Contains("\"") || s.Contains("\n") || s.Contains("\r");
            s = s.Replace("\"", "\"\"");
            return needsQuotes ? $"\"{s}\"" : s;
        }
    }
}
