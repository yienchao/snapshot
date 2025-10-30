using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ViewTracker.Views
{
    public partial class SetupIDsWindow : Window
    {
        private readonly Document _doc;
        private readonly Guid _projectId;

        public SetupIDsWindow(Document doc, Guid projectId)
        {
            InitializeComponent();
            _doc = doc;
            _projectId = projectId;
        }

        // ===== TAB 1: GENERATE IDs =====

        private void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // TODO: Implement scanning logic
                // This is a placeholder that counts elements

                int roomsTotal = 0, roomsWithId = 0;
                int doorsTotal = 0, doorsWithId = 0;
                int elementsTotal = 0, elementsWithId = 0;

                // Count rooms
                if (RoomsCheckBox.IsChecked == true)
                {
                    var rooms = new FilteredElementCollector(_doc)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType()
                        .Cast<Room>()
                        .ToList();

                    roomsTotal = rooms.Count;
                    roomsWithId = rooms.Count(r => r.LookupParameter("trackID") != null &&
                                                    !string.IsNullOrWhiteSpace(r.LookupParameter("trackID").AsString()));
                }

                // Count doors
                if (DoorsCheckBox.IsChecked == true)
                {
                    var doors = new FilteredElementCollector(_doc)
                        .OfCategory(BuiltInCategory.OST_Doors)
                        .WhereElementIsNotElementType()
                        .Cast<FamilyInstance>()
                        .ToList();

                    doorsTotal = doors.Count;
                    doorsWithId = doors.Count(d => d.LookupParameter("trackID") != null &&
                                                    !string.IsNullOrWhiteSpace(d.LookupParameter("trackID").AsString()));
                }

                // Count elements (only those whose category has trackID parameter)
                if (ElementsCheckBox.IsChecked == true)
                {
                    var elements = new FilteredElementCollector(_doc)
                        .OfClass(typeof(FamilyInstance))
                        .Cast<FamilyInstance>()
                        .Where(fi => fi.Category != null &&
                                     fi.Category.Id.Value != (int)BuiltInCategory.OST_Doors &&
                                     fi.LookupParameter("trackID") != null)  // Only categories with trackID parameter
                        .ToList();

                    elementsTotal = elements.Count;
                    elementsWithId = elements.Count(e => !string.IsNullOrWhiteSpace(e.LookupParameter("trackID").AsString()));
                }

                // Display results
                string status = "Scan Results:\n\n";
                if (RoomsCheckBox.IsChecked == true)
                    status += $"Rooms: {roomsWithId}/{roomsTotal} have track IDs ({roomsTotal - roomsWithId} missing)\n";
                if (DoorsCheckBox.IsChecked == true)
                    status += $"Doors: {doorsWithId}/{doorsTotal} have track IDs ({doorsTotal - doorsWithId} missing)\n";
                if (ElementsCheckBox.IsChecked == true)
                    status += $"Elements: {elementsWithId}/{elementsTotal} have track IDs ({elementsTotal - elementsWithId} missing)\n";

                int totalMissing = (roomsTotal - roomsWithId) + (doorsTotal - doorsWithId) + (elementsTotal - elementsWithId);
                status += $"\nTotal elements needing IDs: {totalMissing}";

                GenerateStatusText.Text = status;
                GenerateButton.IsEnabled = totalMissing > 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error scanning project:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void GenerateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Confirm action
                var result = MessageBox.Show(
                    "This will generate unique track IDs for all elements that don't have one.\n\n" +
                    "Elements with existing track IDs will be skipped.\n\n" +
                    "Do you want to continue?",
                    "Confirm Generate IDs",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                    return;

                // Disable UI during operation
                GenerateButton.IsEnabled = false;
                ScanButton.IsEnabled = false;
                GenerateStatusText.Text = "Querying database for existing track IDs...";

                // Step 1: Query database for all existing trackIDs
                var supabaseService = new SupabaseService();
                await supabaseService.InitializeAsync();
                var existingTrackIds = await supabaseService.GetAllExistingTrackIDsAsync(_projectId);

                GenerateStatusText.Text = $"Found {existingTrackIds.Count} existing track IDs in database.\n\nScanning current model...";

                // Step 1b: ALSO scan current model for existing trackIDs (important for un-snapshotted IDs!)
                var allElementsInModel = new List<Element>();

                // Collect all rooms
                var allRooms = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>();
                allElementsInModel.AddRange(allRooms);

                // Collect all doors
                var allDoors = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>();
                allElementsInModel.AddRange(allDoors);

                // Collect all other elements (only those whose category has trackID parameter)
                var allElements = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(fi => fi.Category != null &&
                                 fi.Category.Id.Value != (int)BuiltInCategory.OST_Doors &&
                                 fi.LookupParameter("trackID") != null);  // Only categories with trackID parameter
                allElementsInModel.AddRange(allElements);

                // Add all trackIDs from current model to the set
                int modelTrackIdCount = 0;
                foreach (var element in allElementsInModel)
                {
                    var param = element.LookupParameter("trackID");
                    if (param != null && !string.IsNullOrWhiteSpace(param.AsString()))
                    {
                        existingTrackIds.Add(param.AsString());
                        modelTrackIdCount++;
                    }
                }

                GenerateStatusText.Text = $"Found {existingTrackIds.Count} existing track IDs ({modelTrackIdCount} in current model, {existingTrackIds.Count - modelTrackIdCount} in database).\n\nAnalyzing...";

                // Step 2: Parse existing IDs to find highest number per category
                var categoryCounters = new Dictionary<string, int>();
                foreach (var trackId in existingTrackIds)
                {
                    // Parse format: "CATEGORY-0001"
                    var parts = trackId.Split('-');
                    if (parts.Length == 2 && int.TryParse(parts[1], out int number))
                    {
                        string category = parts[0].ToUpper();
                        if (!categoryCounters.ContainsKey(category) || categoryCounters[category] < number)
                        {
                            categoryCounters[category] = number;
                        }
                    }
                }

                // Step 3: Collect elements needing IDs
                var elementsToUpdate = new List<(Element element, string category)>();

                if (RoomsCheckBox.IsChecked == true)
                {
                    var rooms = new FilteredElementCollector(_doc)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType()
                        .Cast<Room>()
                        .Where(r => r.LookupParameter("trackID") == null ||
                                    string.IsNullOrWhiteSpace(r.LookupParameter("trackID").AsString()))
                        .ToList();

                    foreach (var room in rooms)
                        elementsToUpdate.Add((room, "ROOM"));
                }

                if (DoorsCheckBox.IsChecked == true)
                {
                    var doors = new FilteredElementCollector(_doc)
                        .OfCategory(BuiltInCategory.OST_Doors)
                        .WhereElementIsNotElementType()
                        .Cast<FamilyInstance>()
                        .Where(d => d.LookupParameter("trackID") == null ||
                                    string.IsNullOrWhiteSpace(d.LookupParameter("trackID").AsString()))
                        .ToList();

                    foreach (var door in doors)
                        elementsToUpdate.Add((door, "DOOR"));
                }

                if (ElementsCheckBox.IsChecked == true)
                {
                    var elements = new FilteredElementCollector(_doc)
                        .OfClass(typeof(FamilyInstance))
                        .Cast<FamilyInstance>()
                        .Where(fi => fi.Category != null &&
                                     fi.Category.Id.Value != (int)BuiltInCategory.OST_Doors &&
                                     fi.LookupParameter("trackID") != null &&  // Only categories with trackID parameter
                                     string.IsNullOrWhiteSpace(fi.LookupParameter("trackID").AsString()))  // But missing value
                        .ToList();

                    foreach (var element in elements)
                    {
                        // Use category name as prefix
                        string categoryName = element.Category?.Name?.ToUpper().Replace(" ", "") ?? "ELEMENT";
                        elementsToUpdate.Add((element, categoryName));
                    }
                }

                if (elementsToUpdate.Count == 0)
                {
                    GenerateStatusText.Text = "No elements need track IDs!";
                    GenerateButton.IsEnabled = false;
                    ScanButton.IsEnabled = true;
                    return;
                }

                GenerateStatusText.Text = $"Assigning track IDs to {elementsToUpdate.Count} elements...";

                // Step 4: Assign IDs in a transaction
                using (Transaction trans = new Transaction(_doc, "Generate Track IDs"))
                {
                    trans.Start();

                    int assigned = 0;
                    var assignmentLog = new System.Text.StringBuilder();

                    foreach (var (element, category) in elementsToUpdate)
                    {
                        // Get next number for this category
                        if (!categoryCounters.ContainsKey(category))
                            categoryCounters[category] = 0;

                        categoryCounters[category]++;
                        string newTrackId = $"{category}-{categoryCounters[category]:D4}";

                        // Ensure uniqueness (in case of manual IDs in model that weren't in database)
                        while (existingTrackIds.Contains(newTrackId))
                        {
                            categoryCounters[category]++;
                            newTrackId = $"{category}-{categoryCounters[category]:D4}";
                        }

                        // Assign the ID
                        var param = element.LookupParameter("trackID");
                        if (param != null && !param.IsReadOnly)
                        {
                            param.Set(newTrackId);
                            existingTrackIds.Add(newTrackId); // Add to set to prevent duplicates
                            assigned++;
                            assignmentLog.AppendLine($"  {GetElementDisplayName(element)}: {newTrackId}");
                        }
                    }

                    trans.Commit();

                    // Show results
                    GenerateStatusText.Text = $"✓ Successfully assigned {assigned} track IDs!\n\n" +
                                             $"Assignments:\n{assignmentLog}";

                    MessageBox.Show(
                        $"Successfully assigned {assigned} track IDs!\n\n" +
                        $"You can now create snapshots with these elements.",
                        "Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    // Re-run scan to update stats
                    ScanButton_Click(sender, e);
                }
            }
            catch (Exception ex)
            {
                GenerateStatusText.Text = $"Error: {ex.Message}";
                MessageBox.Show($"Error generating track IDs:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ScanButton.IsEnabled = true;
            }
        }

        // ===== TAB 2: AUDIT =====

        private void RunAuditButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // TODO: Implement full audit logic
                // This is a placeholder

                // Count elements
                var rooms = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .ToList();

                var doors = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                var elements = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(fi => fi.Category != null &&
                                 fi.Category.Id.Value != (int)BuiltInCategory.OST_Doors &&
                                 fi.LookupParameter("trackID") != null)  // Only categories with trackID parameter
                    .ToList();

                int roomsWithId = rooms.Count(r => r.LookupParameter("trackID") != null &&
                                                !string.IsNullOrWhiteSpace(r.LookupParameter("trackID").AsString()));
                int doorsWithId = doors.Count(d => d.LookupParameter("trackID") != null &&
                                                !string.IsNullOrWhiteSpace(d.LookupParameter("trackID").AsString()));
                int elementsWithId = elements.Count(e => !string.IsNullOrWhiteSpace(e.LookupParameter("trackID").AsString()));

                double roomsPct = rooms.Count > 0 ? (roomsWithId * 100.0 / rooms.Count) : 0;
                double doorsPct = doors.Count > 0 ? (doorsWithId * 100.0 / doors.Count) : 0;
                double elementsPct = elements.Count > 0 ? (elementsWithId * 100.0 / elements.Count) : 0;

                string stats = "Coverage Statistics:\n\n";
                stats += $"Rooms:    {roomsWithId,4}/{rooms.Count,-4} tracked ({roomsPct:F1}%)\n";
                stats += $"Doors:    {doorsWithId,4}/{doors.Count,-4} tracked ({doorsPct:F1}%)\n";
                stats += $"Elements: {elementsWithId,4}/{elements.Count,-4} tracked ({elementsPct:F1}%)\n";

                AuditStatsText.Text = stats;

                string issues = "Issues:\n";
                if (roomsWithId < rooms.Count)
                    issues += $"• {rooms.Count - roomsWithId} rooms missing track IDs\n";
                if (doorsWithId < doors.Count)
                    issues += $"• {doors.Count - doorsWithId} doors missing track IDs\n";
                if (elementsWithId < elements.Count)
                    issues += $"• {elements.Count - elementsWithId} elements missing track IDs\n";

                if (issues == "Issues:\n")
                    issues += "• None found - all elements have track IDs!";

                AuditIssuesText.Text = issues;
                ExportReportButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error running audit:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ===== TAB 3: VALIDATE =====

        private void CheckDuplicatesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // TODO: Implement duplicate checking with database validation
                // This is a placeholder that only checks current model

                var allElements = new List<Element>();

                // Collect all elements with trackID
                var rooms = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r.LookupParameter("trackID") != null &&
                                !string.IsNullOrWhiteSpace(r.LookupParameter("trackID").AsString()));
                allElements.AddRange(rooms);

                var doors = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Where(d => d.LookupParameter("trackID") != null &&
                                !string.IsNullOrWhiteSpace(d.LookupParameter("trackID").AsString()));
                allElements.AddRange(doors);

                var elements = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(fi => fi.Category != null &&
                                 fi.Category.Id.Value != (int)BuiltInCategory.OST_Doors &&
                                 fi.LookupParameter("trackID") != null &&
                                 !string.IsNullOrWhiteSpace(fi.LookupParameter("trackID").AsString()));
                allElements.AddRange(elements);

                // Find duplicates
                var duplicateGroups = allElements
                    .GroupBy(e => e.LookupParameter("trackID").AsString())
                    .Where(g => g.Count() > 1)
                    .ToList();

                DuplicatesListBox.Items.Clear();

                if (duplicateGroups.Any())
                {
                    ValidateResultsText.Text = $"⚠️ Found {duplicateGroups.Count} duplicate track IDs in the model!";
                    ValidateResultsText.Foreground = System.Windows.Media.Brushes.Red;

                    foreach (var group in duplicateGroups)
                    {
                        string elementNames = string.Join(", ", group.Select(e => GetElementDisplayName(e)));
                        DuplicatesListBox.Items.Add($"trackID '{group.Key}': {elementNames}");
                    }

                    SelectDuplicatesButton.IsEnabled = true;
                    ZoomToDuplicatesButton.IsEnabled = true;
                    FixDuplicatesButton.IsEnabled = true;
                }
                else
                {
                    ValidateResultsText.Text = "✓ No duplicate track IDs found in current model!";
                    ValidateResultsText.Foreground = System.Windows.Media.Brushes.Green;
                    DuplicatesListBox.Items.Add("No duplicates found");
                    SelectDuplicatesButton.IsEnabled = false;
                    ZoomToDuplicatesButton.IsEnabled = false;
                    FixDuplicatesButton.IsEnabled = false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error checking duplicates:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void FixDuplicatesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Disable buttons during processing
                FixDuplicatesButton.IsEnabled = false;
                CheckDuplicatesButton.IsEnabled = false;

                ValidateResultsText.Text = "Fixing duplicate trackIDs...";

                // Collect ALL elements with trackID (rooms, doors, elements)
                var allElements = new List<Element>();

                var rooms = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r.LookupParameter("trackID") != null &&
                                !string.IsNullOrWhiteSpace(r.LookupParameter("trackID").AsString()));
                allElements.AddRange(rooms);

                var doors = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Where(d => d.LookupParameter("trackID") != null &&
                                !string.IsNullOrWhiteSpace(d.LookupParameter("trackID").AsString()));
                allElements.AddRange(doors);

                var elements = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(fi => fi.Category != null &&
                                 fi.Category.Id.Value != (int)BuiltInCategory.OST_Doors &&
                                 fi.LookupParameter("trackID") != null &&
                                 !string.IsNullOrWhiteSpace(fi.LookupParameter("trackID").AsString()));
                allElements.AddRange(elements);

                // Find duplicate groups
                var duplicateGroups = allElements
                    .GroupBy(e => e.LookupParameter("trackID").AsString())
                    .Where(g => g.Count() > 1)
                    .ToList();

                if (!duplicateGroups.Any())
                {
                    ValidateResultsText.Text = "✓ No duplicate track IDs found!";
                    ValidateResultsText.Foreground = System.Windows.Media.Brushes.Green;
                    CheckDuplicatesButton.IsEnabled = true;
                    FixDuplicatesButton.IsEnabled = true;
                    return;
                }

                // Confirm with user
                var result = MessageBox.Show(
                    $"Found {duplicateGroups.Count} duplicate trackID groups.\n\n" +
                    $"This will assign new unique trackIDs to {duplicateGroups.Sum(g => g.Count() - 1)} elements.\n\n" +
                    $"The first element in each group will keep its original trackID.\n\n" +
                    $"Continue?",
                    "Fix Duplicates",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    ValidateResultsText.Text = "⚠️ Fix cancelled by user";
                    ValidateResultsText.Foreground = System.Windows.Media.Brushes.Orange;
                    CheckDuplicatesButton.IsEnabled = true;
                    FixDuplicatesButton.IsEnabled = true;
                    return;
                }

                // Get all existing trackIDs to avoid collisions
                var existingTrackIds = new HashSet<string>(
                    allElements.Select(e => e.LookupParameter("trackID").AsString()),
                    StringComparer.OrdinalIgnoreCase);

                // Apply the fix in transaction
                using (var trans = new Transaction(_doc, "Fix Duplicate TrackIDs"))
                {
                    trans.Start();

                    int fixedCount = 0;
                    foreach (var group in duplicateGroups)
                    {
                        // Skip the first element (keep original trackID)
                        foreach (var element in group.Skip(1))
                        {
                            var trackIdParam = element.LookupParameter("trackID");
                            if (trackIdParam != null && !trackIdParam.IsReadOnly)
                            {
                                // Generate new unique trackID based on element type
                                string prefix = "";
                                if (element is Room)
                                    prefix = "ROOM";
                                else if (element.Category.Id.Value == (int)BuiltInCategory.OST_Doors)
                                    prefix = "DOOR";
                                else
                                    prefix = "ELEM";

                                // Find next available number
                                int counter = 1;
                                string newTrackId;
                                do
                                {
                                    newTrackId = $"{prefix}-{counter:D4}";
                                    counter++;
                                } while (existingTrackIds.Contains(newTrackId));

                                trackIdParam.Set(newTrackId);
                                existingTrackIds.Add(newTrackId);
                                fixedCount++;
                            }
                        }
                    }

                    trans.Commit();

                    ValidateResultsText.Text = $"✓ Successfully fixed {fixedCount} duplicate track IDs!";
                    ValidateResultsText.Foreground = System.Windows.Media.Brushes.Green;

                    MessageBox.Show(
                        $"Successfully fixed {fixedCount} duplicate track IDs!\n\n" +
                        $"The duplicates have been assigned new unique IDs.",
                        "Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    // Re-run check to update UI
                    CheckDuplicatesButton_Click(sender, e);
                }
            }
            catch (Exception ex)
            {
                ValidateResultsText.Text = $"❌ Error: {ex.Message}";
                ValidateResultsText.Foreground = System.Windows.Media.Brushes.Red;
                MessageBox.Show($"Error fixing duplicates:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                CheckDuplicatesButton.IsEnabled = true;
                FixDuplicatesButton.IsEnabled = true;
            }
        }

        private string GetElementDisplayName(Element element)
        {
            if (element is Room room)
                return $"Room {room.Number}";
            else if (element is FamilyInstance fi)
            {
                var mark = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                return $"{fi.Category?.Name} {mark}";
            }
            return $"Element {element.Id}";
        }
    }
}
