using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.IO;
using OfficeOpenXml;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class DownloadTemplateCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Set EPPlus license context
                ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

                // Prompt user for save location
                var saveFileDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save Space Program Template",
                    Filter = "Excel Files (*.xlsx)|*.xlsx",
                    FileName = "SpaceProgram_Template.xlsx",
                    DefaultExt = "xlsx"
                };

                if (saveFileDialog.ShowDialog() != true)
                    return Result.Cancelled;

                string filePath = saveFileDialog.FileName;

                // Create Excel file with sample data
                using (var package = new ExcelPackage())
                {
                    var worksheet = package.Workbook.Worksheets.Add("Space Program");

                    // Headers
                    worksheet.Cells[1, 1].Value = "Name";
                    worksheet.Cells[1, 2].Value = "Number";
                    worksheet.Cells[1, 3].Value = "Area";
                    worksheet.Cells[1, 4].Value = "Department";
                    worksheet.Cells[1, 5].Value = "Occupancy";
                    worksheet.Cells[1, 6].Value = "Level";
                    worksheet.Cells[1, 7].Value = "Comments";

                    // Format headers
                    using (var range = worksheet.Cells[1, 1, 1, 7])
                    {
                        range.Style.Font.Bold = true;
                        range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                        range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightBlue);
                    }

                    // Sample data
                    worksheet.Cells[2, 1].Value = "Office";
                    worksheet.Cells[2, 2].Value = "101";
                    worksheet.Cells[2, 3].Value = 150;
                    worksheet.Cells[2, 4].Value = "Admin";
                    worksheet.Cells[2, 5].Value = "2";
                    worksheet.Cells[2, 6].Value = "Level 1";
                    worksheet.Cells[2, 7].Value = "Corner office";

                    worksheet.Cells[3, 1].Value = "Meeting Room";
                    worksheet.Cells[3, 2].Value = "102";
                    worksheet.Cells[3, 3].Value = 200;
                    worksheet.Cells[3, 4].Value = "Admin";
                    worksheet.Cells[3, 5].Value = "8";
                    worksheet.Cells[3, 6].Value = "Level 1";
                    worksheet.Cells[3, 7].Value = "Video conferencing";

                    worksheet.Cells[4, 1].Value = "Open Office";
                    worksheet.Cells[4, 2].Value = "103";
                    worksheet.Cells[4, 3].Value = 500;
                    worksheet.Cells[4, 4].Value = "Workspace";
                    worksheet.Cells[4, 5].Value = "20";
                    worksheet.Cells[4, 6].Value = "Level 1";
                    worksheet.Cells[4, 7].Value = "Hot desking";

                    worksheet.Cells[5, 1].Value = "Break Room";
                    worksheet.Cells[5, 2].Value = "104";
                    worksheet.Cells[5, 3].Value = 120;
                    worksheet.Cells[5, 4].Value = "Support";
                    worksheet.Cells[5, 5].Value = "10";
                    worksheet.Cells[5, 6].Value = "Level 1";
                    worksheet.Cells[5, 7].Value = "Kitchenette";

                    worksheet.Cells[6, 1].Value = "Storage";
                    worksheet.Cells[6, 2].Value = "105";
                    worksheet.Cells[6, 3].Value = 80;
                    worksheet.Cells[6, 4].Value = "Support";
                    worksheet.Cells[6, 5].Value = "0";
                    worksheet.Cells[6, 6].Value = "Level 1";
                    worksheet.Cells[6, 7].Value = "";

                    // Auto-fit columns
                    worksheet.Cells.AutoFitColumns();

                    // Add instructions in a separate sheet
                    var instructionsSheet = package.Workbook.Worksheets.Add("Instructions");
                    instructionsSheet.Cells[1, 1].Value = "Space Program Template - Instructions";
                    instructionsSheet.Cells[1, 1].Style.Font.Bold = true;
                    instructionsSheet.Cells[1, 1].Style.Font.Size = 14;

                    instructionsSheet.Cells[3, 1].Value = "1. Fill in the 'Space Program' sheet with your room data";
                    instructionsSheet.Cells[4, 1].Value = "2. You can add custom columns for additional parameters";
                    instructionsSheet.Cells[5, 1].Value = "3. Use 'Import Program' command in Revit to import this file";
                    instructionsSheet.Cells[6, 1].Value = "4. Map Excel columns to Filled Region parameters";
                    instructionsSheet.Cells[7, 1].Value = "5. Filled regions will be auto-placed in a grid layout";
                    instructionsSheet.Cells[8, 1].Value = "6. Adjust layout manually in Revit as needed";
                    instructionsSheet.Cells[9, 1].Value = "7. Use 'Convert to Rooms' when ready to create real rooms";

                    instructionsSheet.Cells[11, 1].Value = "Standard Columns:";
                    instructionsSheet.Cells[11, 1].Style.Font.Bold = true;
                    instructionsSheet.Cells[12, 1].Value = "• Name: Room name";
                    instructionsSheet.Cells[13, 1].Value = "• Number: Room number";
                    instructionsSheet.Cells[14, 1].Value = "• Area: Target area in square feet or square meters";
                    instructionsSheet.Cells[15, 1].Value = "• Department: Room department/category";
                    instructionsSheet.Cells[16, 1].Value = "• Occupancy: Number of occupants";
                    instructionsSheet.Cells[17, 1].Value = "• Level: Target level name";
                    instructionsSheet.Cells[18, 1].Value = "• Comments: Additional notes";

                    instructionsSheet.Cells.AutoFitColumns();

                    // Save file
                    package.SaveAs(new FileInfo(filePath));
                }

                TaskDialog.Show("Success", $"Template saved successfully:\n\n{filePath}\n\nYou can now customize this template and use 'Import Program' to import it.");

                // Open the file
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true
                    });
                }
                catch { }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to create template:\n\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}
