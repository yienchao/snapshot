using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OfficeOpenXml;
using ViewTracker.Views;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ImportProgramCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiApp = commandData.Application;
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            try
            {
                // Set EPPlus license context
                ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

                // 1. Select Excel file
                var openFileDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select Space Program Excel File",
                    Filter = "Excel Files (*.xlsx)|*.xlsx|All Files (*.*)|*.*",
                    CheckFileExists = true
                };

                if (openFileDialog.ShowDialog() != true)
                    return Result.Cancelled;

                string excelPath = openFileDialog.FileName;

                // 2. Read Excel and get column headers
                List<string> columnHeaders;
                List<Dictionary<string, object>> excelData;

                using (var package = new ExcelPackage(new FileInfo(excelPath)))
                {
                    var worksheet = package.Workbook.Worksheets.FirstOrDefault();
                    if (worksheet == null)
                    {
                        TaskDialog.Show("Error", "No worksheets found in Excel file.");
                        return Result.Failed;
                    }

                    // Read headers from first row
                    columnHeaders = new List<string>();
                    int colCount = worksheet.Dimension?.Columns ?? 0;

                    for (int col = 1; col <= colCount; col++)
                    {
                        var header = worksheet.Cells[1, col].Value?.ToString();
                        if (!string.IsNullOrWhiteSpace(header))
                            columnHeaders.Add(header);
                    }

                    if (!columnHeaders.Any())
                    {
                        TaskDialog.Show("Error", "No column headers found in Excel file.");
                        return Result.Failed;
                    }

                    // Read data rows
                    excelData = new List<Dictionary<string, object>>();
                    int rowCount = worksheet.Dimension?.Rows ?? 0;

                    for (int row = 2; row <= rowCount; row++) // Start from row 2 (skip headers)
                    {
                        var rowData = new Dictionary<string, object>();
                        bool hasData = false;

                        for (int col = 1; col <= columnHeaders.Count; col++)
                        {
                            var cell = worksheet.Cells[row, col];

                            // ALWAYS use formatted text - what the user sees in Excel
                            // This avoids any issues with formula calculation or locale handling
                            if (!string.IsNullOrWhiteSpace(cell.Text))
                            {
                                rowData[columnHeaders[col - 1]] = cell.Text;
                                hasData = true;
                                System.Diagnostics.Debug.WriteLine($"Excel Read Row {row} Col '{columnHeaders[col - 1]}': Text='{cell.Text}', Value={cell.Value}, Formula='{cell.Formula}'");
                            }
                            else if (cell.Value != null)
                            {
                                // Fallback to raw value only if no text display
                                rowData[columnHeaders[col - 1]] = cell.Value.ToString();
                                hasData = true;
                                System.Diagnostics.Debug.WriteLine($"Excel Read Row {row} Col '{columnHeaders[col - 1]}': (using Value) {cell.Value}, Formula='{cell.Formula}'");
                            }
                        }

                        if (hasData)
                            excelData.Add(rowData);
                    }

                    if (!excelData.Any())
                    {
                        TaskDialog.Show("Error", "No data rows found in Excel file.");
                        return Result.Failed;
                    }
                }

                // 3. Get available filled region parameters
                var filledRegionTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>()
                    .ToList();

                // Get shared parameters from the project that are applicable to Filled Regions
                var availableParameters = new List<string>();

                // Check existing filled regions for their parameters
                var existingFilledRegions = new FilteredElementCollector(doc)
                    .OfClass(typeof(FilledRegion))
                    .Cast<FilledRegion>()
                    .ToList();

                if (existingFilledRegions.Any())
                {
                    var sampleRegion = existingFilledRegions.First();
                    foreach (Parameter param in sampleRegion.Parameters)
                    {
                        if (!param.IsReadOnly &&
                            param.Definition is Definition def &&
                            param.StorageType != StorageType.ElementId)
                        {
                            // Only include shared/project parameters, not built-in ones
                            // Exclude Area (it's calculated from boundary, not settable)
                            var paramName = def.Name;
                            if ((def is ExternalDefinition || param.IsShared) &&
                                !paramName.Equals("Area", StringComparison.OrdinalIgnoreCase))
                            {
                                availableParameters.Add(paramName);
                            }
                        }
                    }
                }

                // Get shared parameters from project that could be added to filled regions
                // Note: This requires access to the shared parameter file, which we might not have
                // So we rely on parameters already in use on existing filled regions

                if (!availableParameters.Any())
                {
                    // No filled region parameters found - show warning
                    var result = TaskDialog.Show("No Parameters Found",
                        "No shared parameters found on filled regions in this project.\n\n" +
                        "Filled regions need shared parameters to store space program data (Name, Number, Area, etc.).\n\n" +
                        "Options:\n" +
                        "1. Create shared parameters and add them to Filled Regions category\n" +
                        "2. Continue without parameter mapping (filled regions will have no data)\n\n" +
                        "Continue anyway?",
                        TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No);

                    if (result != TaskDialogResult.Yes)
                        return Result.Cancelled;
                }

                availableParameters = availableParameters.Distinct().OrderBy(p => p).ToList();

                // 4. Show mapping window
                var mappingWindow = new ExcelMappingWindow(columnHeaders, availableParameters);
                if (mappingWindow.ShowDialog() != true || !mappingWindow.WasImported)
                    return Result.Cancelled;

                var mappings = mappingWindow.FinalMappings;
                var groupByColumn = mappingWindow.GroupByColumn;
                bool excelIsSquareFeet = mappingWindow.IsSquareFeet;

                // 5. Get or create "space_layout" filled region type
                FilledRegionType spaceLayoutType = filledRegionTypes.FirstOrDefault(t => t.Name == "space_layout");

                if (spaceLayoutType == null)
                {
                    // Create new filled region type
                    using (Transaction trans = new Transaction(doc, "Create space_layout Type"))
                    {
                        trans.Start();

                        // Duplicate an existing type or create from scratch
                        if (filledRegionTypes.Any())
                        {
                            spaceLayoutType = filledRegionTypes.First().Duplicate("space_layout") as FilledRegionType;
                        }
                        else
                        {
                            TaskDialog.Show("Error", "No filled region types found in project. Please create at least one filled region type first.");
                            return Result.Failed;
                        }

                        // Set to light gray color
                        var fillPatternElement = new FilteredElementCollector(doc)
                            .OfClass(typeof(FillPatternElement))
                            .Cast<FillPatternElement>()
                            .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill);

                        if (fillPatternElement != null)
                        {
                            spaceLayoutType.ForegroundPatternId = fillPatternElement.Id;
                            spaceLayoutType.ForegroundPatternColor = new Color(200, 200, 200); // Light gray
                        }

                        trans.Commit();
                    }
                }

                // 6. Select view to place filled regions
                var view = doc.ActiveView;
                if (!(view is ViewPlan))
                {
                    TaskDialog.Show("Error", "Please open a floor plan view before importing program.");
                    return Result.Failed;
                }

                // 7. Determine which mapped parameter contains area data (ask user or use first numeric column)
                string areaSourceColumn = null;
                var numericColumns = excelData.First().Where(kvp =>
                {
                    try
                    {
                        ParseNumericValue(kvp.Value);
                        return true;
                    }
                    catch { return false; }
                }).Select(kvp => kvp.Key).ToList();

                // Simple dialog to ask which column has area
                if (numericColumns.Count > 1)
                {
                    // Multiple numeric columns - ask user which one is area
                    var td = new TaskDialog("Select Area Column");
                    td.MainInstruction = "Which column contains the area values?";
                    td.MainContent = "This will be used to calculate filled region dimensions.\nThe value will also be stored in the mapped parameter.";

                    foreach (var col in numericColumns)
                    {
                        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1 + numericColumns.IndexOf(col), col);
                    }

                    var result = td.Show();
                    int selectedIndex = (int)result - (int)TaskDialogCommandLinkId.CommandLink1;
                    if (selectedIndex >= 0 && selectedIndex < numericColumns.Count)
                    {
                        areaSourceColumn = numericColumns[selectedIndex];
                    }
                }
                else if (numericColumns.Any())
                {
                    areaSourceColumn = numericColumns.First();
                }

                if (string.IsNullOrEmpty(areaSourceColumn))
                {
                    TaskDialog.Show("Error", "No numeric column found for area calculation.");
                    return Result.Failed;
                }

                // 8. Calculate grid layout
                var layoutData = CalculateGridLayout(excelData, mappings, groupByColumn, areaSourceColumn, excelIsSquareFeet);

                // 9. Create filled regions (optimized with batch processing)
                int createdCount = 0;
                using (Transaction trans = new Transaction(doc, "Import Space Program"))
                {
                    trans.Start();

                    // Pre-build curves list for all items (outside loop)
                    var allCurvesData = new List<(List<CurveLoop> curves, Dictionary<string, object> data)>();
                    foreach (var item in layoutData)
                    {
                        var curveLoop = new CurveLoop();
                        XYZ p1 = new XYZ(item.X, item.Y, 0);
                        XYZ p2 = new XYZ(item.X + item.Width, item.Y, 0);
                        XYZ p3 = new XYZ(item.X + item.Width, item.Y + item.Height, 0);
                        XYZ p4 = new XYZ(item.X, item.Y + item.Height, 0);

                        curveLoop.Append(Line.CreateBound(p1, p2));
                        curveLoop.Append(Line.CreateBound(p2, p3));
                        curveLoop.Append(Line.CreateBound(p3, p4));
                        curveLoop.Append(Line.CreateBound(p4, p1));

                        allCurvesData.Add((new List<CurveLoop> { curveLoop }, item.Data));
                    }

                    // Create all filled regions first
                    var filledRegions = new List<FilledRegion>();
                    foreach (var curveData in allCurvesData)
                    {
                        try
                        {
                            FilledRegion fr = FilledRegion.Create(doc, spaceLayoutType.Id, view.Id, curveData.curves);
                            filledRegions.Add(fr);
                            createdCount++;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to create filled region: {ex.Message}");
                            filledRegions.Add(null); // Maintain index alignment
                        }
                    }

                    // Set parameters on all created filled regions
                    for (int i = 0; i < filledRegions.Count; i++)
                    {
                        var filledRegion = filledRegions[i];
                        if (filledRegion == null) continue;

                        var data = allCurvesData[i].data;

                        foreach (var mapping in mappings)
                        {
                            if (data.ContainsKey(mapping.Key))
                            {
                                try
                                {
                                    var param = filledRegion.LookupParameter(mapping.Value);
                                    if (param != null && !param.IsReadOnly)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"Setting parameter loop: Excel column '{mapping.Key}' → Revit param '{mapping.Value}', rawValue='{data[mapping.Key]}'");
                                        SetParameterValue(param, data[mapping.Key], excelIsSquareFeet);
                                    }
                                }
                                catch { }
                            }
                        }
                    }

                    trans.Commit();
                }

                TaskDialog.Show("Success",
                    $"Imported {createdCount} space(s) from Excel.\n\n" +
                    $"Type: space_layout\n" +
                    $"View: {view.Name}\n\n" +
                    $"Note: Area values from Excel are interpreted in project units.\n" +
                    $"Dimensions are calculated automatically (square root of area).\n\n" +
                    $"Adjust layout as needed, then use 'Convert to Rooms' when ready.");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to import program:\n\n{ex.Message}");
                return Result.Failed;
            }
        }

        private List<LayoutItem> CalculateGridLayout(List<Dictionary<string, object>> excelData, Dictionary<string, string> mappings, string groupByColumn, string areaSourceColumn, bool isSquareFeet)
        {
            var layoutItems = new List<LayoutItem>();

            // Calculate dimensions for each space
            foreach (var row in excelData)
            {
                double area = 100; // Default area if not found

                if (!string.IsNullOrEmpty(areaSourceColumn) && row.ContainsKey(areaSourceColumn))
                {
                    try
                    {
                        var rawValue = row[areaSourceColumn];
                        area = ParseNumericValue(rawValue);
                        System.Diagnostics.Debug.WriteLine($"CalculateGridLayout: areaSourceColumn='{areaSourceColumn}', rawValue='{rawValue}', parsed area={area}, isSquareFeet={isSquareFeet}");
                    }
                    catch { }
                }

                // Validate area value (reject absurdly large values)
                if (area > 1000000) // 1 million sq units is already huge
                {
                    System.Diagnostics.Debug.WriteLine($"Warning: Area value {area} is too large, defaulting to 100");
                    area = 100;
                }

                // Calculate side dimension in display units
                double side = Math.Sqrt(area);

                // Convert dimension to feet if project uses different units
                if (!isSquareFeet && side > 0)
                {
                    // Assume meters if not square feet (most common alternative)
                    // 1 m = 3.28084 ft
                    side = side * 3.28084;
                }

                // Ensure reasonable size
                if (side < 0.1)
                    side = 10.0; // Default to 10 feet if too small
                else if (side > 1000)
                    side = 10.0; // Default to 10 feet if absurdly large

                // Get group value if grouping is enabled
                string groupValue = null;
                if (!string.IsNullOrEmpty(groupByColumn) && row.ContainsKey(groupByColumn))
                {
                    groupValue = row[groupByColumn]?.ToString() ?? "Ungrouped";
                }

                layoutItems.Add(new LayoutItem
                {
                    Width = side,
                    Height = side,
                    Data = row,
                    Group = groupValue
                });
            }

            // Layout with grouping
            double padding = 5; // 5 feet/meters gap between spaces
            double groupPadding = 15; // Extra padding between groups
            double currentY = 0;
            double currentX = 0;

            if (!string.IsNullOrEmpty(groupByColumn))
            {
                // Group items by the grouping column
                var groups = layoutItems.GroupBy(item => item.Group).ToList();

                foreach (var group in groups)
                {
                    var groupItems = group.ToList();

                    // Calculate optimal grid for this group (as square as possible)
                    int itemCount = groupItems.Count;
                    int columns = (int)Math.Ceiling(Math.Sqrt(itemCount));
                    int rows = (int)Math.Ceiling((double)itemCount / columns);

                    // Find max width and height in this group for uniform grid
                    double maxWidth = groupItems.Max(i => i.Width);
                    double maxHeight = groupItems.Max(i => i.Height);

                    // Layout this group in a grid
                    double groupStartY = currentY;
                    currentX = 0;
                    int index = 0;

                    foreach (var item in groupItems)
                    {
                        int col = index % columns;
                        int row = index / columns;

                        item.X = currentX + col * (maxWidth + padding);
                        item.Y = groupStartY + row * (maxHeight + padding);

                        index++;
                    }

                    // Move to next group position
                    currentY = groupStartY + rows * (maxHeight + padding) + groupPadding;
                }
            }
            else
            {
                // No grouping - create one optimal grid for all items
                int itemCount = layoutItems.Count;
                int columns = (int)Math.Ceiling(Math.Sqrt(itemCount));
                int rows = (int)Math.Ceiling((double)itemCount / columns);

                // Find max dimensions for uniform grid
                double maxWidth = layoutItems.Max(i => i.Width);
                double maxHeight = layoutItems.Max(i => i.Height);

                int index = 0;
                foreach (var item in layoutItems)
                {
                    int col = index % columns;
                    int row = index / columns;

                    item.X = col * (maxWidth + padding);
                    item.Y = row * (maxHeight + padding);

                    index++;
                }
            }

            return layoutItems;
        }

        private double ParseNumericValue(object value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));

            string strValue = value.ToString().Trim();

            // Try parsing with invariant culture (uses "." as decimal)
            if (double.TryParse(strValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double result1))
                return result1;

            // Try parsing with current culture (might use "," as decimal)
            if (double.TryParse(strValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out double result2))
                return result2;

            // Try replacing comma with period and parse again
            strValue = strValue.Replace(',', '.');
            if (double.TryParse(strValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double result3))
                return result3;

            throw new FormatException($"Cannot parse numeric value: {value}");
        }

        private void SetParameterValue(Parameter param, object value, bool excelIsSquareFeet)
        {
            if (value == null) return;

            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        param.Set(value.ToString());
                        break;
                    case StorageType.Double:
                        double parsedValue = ParseNumericValue(value);

                        // For area parameters: convert Excel value to Revit internal units (square feet)
                        // Revit ALWAYS stores area in square feet internally, regardless of display units
                        var paramType = param.Definition.GetDataType();
                        if (paramType == SpecTypeId.Area)
                        {
                            // If Excel is in square meters, convert to square feet for storage
                            if (!excelIsSquareFeet)
                            {
                                parsedValue = UnitUtils.ConvertToInternalUnits(parsedValue, UnitTypeId.SquareMeters);
                                System.Diagnostics.Debug.WriteLine($"Area param '{param.Definition.Name}': {value} m² → {parsedValue} ft² (internal)");
                            }
                            else
                            {
                                // Excel is in square feet, which is already internal units
                                parsedValue = UnitUtils.ConvertToInternalUnits(parsedValue, UnitTypeId.SquareFeet);
                                System.Diagnostics.Debug.WriteLine($"Area param '{param.Definition.Name}': {value} ft² → {parsedValue} ft² (internal)");
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"Non-area param '{param.Definition.Name}' = {parsedValue}");
                        }

                        param.Set(parsedValue);
                        break;
                    case StorageType.Integer:
                        double parsedInt = ParseNumericValue(value);
                        param.Set(Convert.ToInt32(parsedInt));
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting parameter {param.Definition.Name}: {ex.Message}");
            }
        }

        private class LayoutItem
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public Dictionary<string, object> Data { get; set; }
            public string Group { get; set; }
        }
    }
}
