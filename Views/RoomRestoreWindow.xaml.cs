using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace ViewTracker.Views
{
    public partial class RoomRestoreWindow : Window
    {
        private List<VersionInfo> _versions;
        private int _totalRoomCount;
        private bool _hasPreSelection;
        private Document _doc;
        private SupabaseService _supabase;
        private Guid _projectId;
        private List<Room> _currentRooms;
        private List<RoomSnapshot> _selectedVersionSnapshots;

        public RoomRestoreWindow(
            List<VersionInfo> versions,
            int totalRoomCount,
            bool hasPreSelection,
            Document doc,
            SupabaseService supabase,
            Guid projectId,
            List<Room> currentRooms)
        {
            InitializeComponent();

            _versions = versions;
            _totalRoomCount = totalRoomCount;
            _hasPreSelection = hasPreSelection;
            _doc = doc;
            _supabase = supabase;
            _projectId = projectId;
            _currentRooms = currentRooms;

            // Populate version dropdown
            VersionComboBox.ItemsSource = _versions;
            if (_versions.Any())
            {
                VersionComboBox.SelectedIndex = 0;
            }

            // Set scope UI
            if (_hasPreSelection)
            {
                SelectedRoomsRadio.IsChecked = true;
                AllRoomsRadio.IsEnabled = true;
                ScopeInfoText.Text = $"{_totalRoomCount} rooms pre-selected";
            }
            else
            {
                AllRoomsRadio.IsChecked = true;
                SelectedRoomsRadio.IsEnabled = false;
                ScopeInfoText.Text = $"{_totalRoomCount} rooms with trackID found";
            }

            UpdateStatus();
        }

        private void VersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VersionComboBox.SelectedItem is VersionInfo version)
            {
                var typeLabel = version.IsOfficial ? "OFFICIAL" : "draft";
                var dateStr = version.SnapshotDate?.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                VersionInfoText.Text = $"Created: {dateStr} by {version.CreatedBy ?? "Unknown"} ({typeLabel})";

                // Load snapshots for this version
                LoadSnapshotsForVersion(version.VersionName);
            }
        }

        private void LoadSnapshotsForVersion(string versionName)
        {
            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await _supabase.InitializeAsync();
                    _selectedVersionSnapshots = await _supabase.GetRoomsByVersionAsync(versionName, _projectId);
                }).Wait();

                PopulateParameterCheckboxes();
                UpdateStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load snapshot data:\n{ex.InnerException?.Message ?? ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PopulateParameterCheckboxes()
        {
            ParameterCheckboxPanel.Children.Clear();

            if (_selectedVersionSnapshots == null || !_selectedVersionSnapshots.Any())
                return;

            // Collect all unique parameter names from BOTH snapshots AND current rooms
            var allParameters = new HashSet<string>();

            // Get first snapshot to examine parameters
            var sampleSnapshot = _selectedVersionSnapshots.First();

            // Parameters to exclude from restore (read-only, placement-dependent, or system parameters)
            // Using both BuiltInParameter enum check and string name fallback for comprehensive filtering
            var excludedBuiltInParams = new HashSet<BuiltInParameter>
            {
                // Placement-dependent (read-only, calculated from room geometry)
                BuiltInParameter.ROOM_AREA,
                BuiltInParameter.ROOM_PERIMETER,
                BuiltInParameter.ROOM_VOLUME,
                BuiltInParameter.ROOM_UPPER_LEVEL,
                BuiltInParameter.ROOM_UPPER_OFFSET,
                BuiltInParameter.ROOM_LOWER_OFFSET,
                BuiltInParameter.ROOM_COMPUTATION_HEIGHT,
                BuiltInParameter.ROOM_LEVEL_ID,

                // System metadata (read-only, managed by Revit)
                BuiltInParameter.EDITED_BY,

                // Design/organizational (should not be restored)
                BuiltInParameter.DESIGN_OPTION_ID,
                BuiltInParameter.DESIGN_OPTION_PARAM,
                BuiltInParameter.ELEM_PARTITION_PARAM  // Workset
            };

            // String-based exclusions for shared parameters or when BuiltInParameter is not available
            var excludedParamNames = new HashSet<string>();

            // Add dedicated column parameters - ALWAYS add them, even if empty in snapshot
            // (they might have values in current model that user wants to clear)
            allParameters.Add("Room Number|RoomNumber");
            allParameters.Add("Room Name|RoomName");
            allParameters.Add("Department|Department");
            allParameters.Add("Occupancy|Occupancy");
            allParameters.Add("Comments|Comments");
            allParameters.Add("Base Finish|BaseFinish");
            allParameters.Add("Ceiling Finish|CeilingFinish");
            allParameters.Add("Wall Finish|WallFinish");
            allParameters.Add("Floor Finish|FloorFinish");
            allParameters.Add("Occupant|Occupant");
            allParameters.Add("Phase|Phase");

            // Add parameters from AllParameters JSON (excluding read-only ones)
            if (sampleSnapshot.AllParameters != null)
            {
                foreach (var param in sampleSnapshot.AllParameters.Keys)
                {
                    if (!excludedParamNames.Contains(param))
                    {
                        allParameters.Add($"{param}|AllParam_{param}");
                    }
                }
            }

            // ALSO add parameters from current rooms that might not be in snapshot
            // (e.g., shared parameters that currently have empty values)
            if (_currentRooms != null && _currentRooms.Any())
            {
                var sampleRoom = _currentRooms.First();
                foreach (Parameter param in sampleRoom.GetOrderedParameters())
                {
                    string paramName = param.Definition.Name;

                    // Skip built-in parameters using BuiltInParameter enum (language-independent)
                    if (param.Definition is InternalDefinition internalDef)
                    {
                        var builtInParam = internalDef.BuiltInParameter;
                        if (builtInParam != BuiltInParameter.INVALID && excludedBuiltInParams.Contains(builtInParam))
                            continue;
                    }

                    // Skip shared parameters by name
                    if (excludedParamNames.Contains(paramName))
                        continue;

                    // Skip built-in parameters that are already in dedicated columns (Number, Name)
                    if (param.Definition is InternalDefinition internalDef2)
                    {
                        var builtInParam2 = internalDef2.BuiltInParameter;
                        if (builtInParam2 == BuiltInParameter.ROOM_NUMBER ||
                            builtInParam2 == BuiltInParameter.ROOM_NAME)
                            continue;
                    }

                    // Add parameter if not already in list
                    string paramKey = $"{paramName}|AllParam_{paramName}";
                    if (!allParameters.Any(p => p.StartsWith(paramName + "|")))
                    {
                        allParameters.Add(paramKey);
                    }
                }
            }

            // Create checkboxes grouped by category
            var sortedParams = allParameters.OrderBy(p => p.Split('|')[0]).ToList();

            foreach (var paramData in sortedParams)
            {
                var parts = paramData.Split('|');
                var displayName = parts[0];
                var paramKey = parts[1];

                var checkbox = new CheckBox
                {
                    Content = displayName,
                    Tag = paramKey,
                    IsChecked = true, // Default to checked
                    Margin = new Thickness(5, 3, 5, 3),
                    FontSize = 13
                };

                checkbox.Checked += (s, e) => UpdateStatus();
                checkbox.Unchecked += (s, e) => UpdateStatus();

                ParameterCheckboxPanel.Children.Add(checkbox);
            }
        }

        private void ScopeRadio_Changed(object sender, RoutedEventArgs e)
        {
            UpdateStatus();
        }

        private void RecreateDeleted_Changed(object sender, RoutedEventArgs e)
        {
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (StatusText == null || _selectedVersionSnapshots == null)
            {
                if (StatusText != null)
                    StatusText.Text = "Select a version to see status information.";
                return;
            }

            var currentTrackIds = _currentRooms.Select(r => r.LookupParameter("trackID").AsString()).ToHashSet();
            var snapshotTrackIds = _selectedVersionSnapshots.Select(s => s.TrackId).ToHashSet();

            var existingCount = currentTrackIds.Intersect(snapshotTrackIds).Count();
            var deletedCount = snapshotTrackIds.Except(currentTrackIds).Count();

            var sb = new StringBuilder();
            sb.AppendLine($"• {existingCount} existing rooms will be updated");

            // Only show deleted rooms message if the checkbox is checked
            if (deletedCount > 0 && ChkRecreateDeleted != null && ChkRecreateDeleted.IsChecked == true)
            {
                sb.AppendLine($"• {deletedCount} deleted rooms will be created (unplaced)");
            }
            else if (deletedCount > 0)
            {
                sb.AppendLine($"• {deletedCount} deleted rooms will be ignored");
            }

            var selectedParamsCount = GetSelectedParameters().Count;
            sb.AppendLine($"• {selectedParamsCount} parameter(s) selected for restore");

            StatusText.Text = sb.ToString();
        }

        private List<string> GetSelectedParameters()
        {
            var selected = new List<string>();

            foreach (var child in ParameterCheckboxPanel.Children)
            {
                if (child is CheckBox checkbox && checkbox.IsChecked == true)
                {
                    selected.Add(checkbox.Tag.ToString());
                }
            }

            return selected;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var child in ParameterCheckboxPanel.Children)
            {
                if (child is CheckBox checkbox)
                {
                    checkbox.IsChecked = true;
                }
            }
            UpdateStatus();
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var child in ParameterCheckboxPanel.Children)
            {
                if (child is CheckBox checkbox)
                {
                    checkbox.IsChecked = false;
                }
            }
            UpdateStatus();
        }

        private void Preview_Click(object sender, RoutedEventArgs e)
        {
            if (VersionComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a version first.", "No Version Selected",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedParams = GetSelectedParameters();
            if (!selectedParams.Any())
            {
                MessageBox.Show("Please select at least one parameter to restore.", "No Parameters Selected",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Show preview window
            var previewWindow = new RestorePreviewWindow(
                _doc,
                _currentRooms,
                _selectedVersionSnapshots,
                selectedParams,
                (VersionComboBox.SelectedItem as VersionInfo).VersionName
            );
            previewWindow.ShowDialog();
        }

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (VersionComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a version first.", "No Version Selected",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedParams = GetSelectedParameters();
            if (!selectedParams.Any())
            {
                MessageBox.Show("Please select at least one parameter to restore.", "No Parameters Selected",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                "Are you sure you want to restore the selected parameters?\n\n" +
                "This will modify existing rooms and create unplaced rooms for deleted ones.\n\n" +
                (ChkCreateBackup.IsChecked == true ? "A backup snapshot will be created first." : ""),
                "Confirm Restore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                PerformRestore(selectedParams);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Restore failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PerformRestore(List<string> selectedParams)
        {
            string versionName = (VersionComboBox.SelectedItem as VersionInfo).VersionName;
            bool createBackup = ChkCreateBackup.IsChecked == true;
            bool recreateDeleted = ChkRecreateDeleted.IsChecked == true;

            // Step 1: Create backup snapshot if requested
            if (createBackup)
            {
                CreateBackupSnapshot();
            }

            // Step 2: Perform restore
            var restoreResult = RestoreRoomsFromSnapshot(selectedParams, recreateDeleted);

            // Step 3: Show result
            ShowRestoreResult(restoreResult, versionName);

            DialogResult = true;
            Close();
        }

        private void CreateBackupSnapshot()
        {
            string backupVersionName = $"Backup_Before_Restore_{DateTime.Now:yyyyMMdd_HHmmss}";
            string currentUser = Environment.UserName;

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    // Create room snapshots for current state
                    var roomSnapshots = new List<RoomSnapshot>();

                    foreach (var room in _currentRooms)
                    {
                        var snapshot = CreateRoomSnapshot(room, backupVersionName, currentUser, false);
                        roomSnapshots.Add(snapshot);
                    }

                    // Save to Supabase
                    await _supabase.InitializeAsync();
                    await _supabase.BulkUpsertRoomSnapshotsAsync(roomSnapshots);

                }).Wait();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to create backup snapshot: {ex.Message}", ex);
            }
        }

        private RoomSnapshot CreateRoomSnapshot(Room room, string versionName, string createdBy, bool isOfficial)
        {
            var projectIdStr = _doc.ProjectInformation.LookupParameter("projectID")?.AsString();
            Guid.TryParse(projectIdStr, out Guid projectId);

            var snapshot = new RoomSnapshot
            {
                TrackId = room.LookupParameter("trackID").AsString(),
                VersionName = versionName,
                ProjectId = projectId,
                FileName = _doc.Title,
                SnapshotDate = DateTime.UtcNow,
                CreatedBy = createdBy,
                IsOfficial = isOfficial,
                RoomNumber = room.Number,
                RoomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                Level = room.Level?.Name,
                Area = room.Area,
                Perimeter = room.Perimeter,
                Volume = room.Volume,
                UnboundHeight = room.UnboundedHeight,
                Occupancy = room.LookupParameter("Occupancy")?.AsString() ?? room.LookupParameter("Occupation")?.AsString(),
                Department = room.LookupParameter("Department")?.AsString() ?? room.LookupParameter("Service")?.AsString(),
                Phase = room.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsValueString(),
                BaseFinish = room.LookupParameter("Base Finish")?.AsString() ?? room.LookupParameter("Finition de la base")?.AsString(),
                CeilingFinish = room.LookupParameter("Ceiling Finish")?.AsString() ?? room.LookupParameter("Finition du plafond")?.AsString(),
                WallFinish = room.LookupParameter("Wall Finish")?.AsString() ?? room.LookupParameter("Finition du mur")?.AsString(),
                FloorFinish = room.LookupParameter("Floor Finish")?.AsString() ?? room.LookupParameter("Finition du sol")?.AsString(),
                Comments = room.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(),
                Occupant = room.LookupParameter("Occupant")?.AsString(),
                AllParameters = new Dictionary<string, object>()
            };

            return snapshot;
        }

        private RestoreResult RestoreRoomsFromSnapshot(List<string> selectedParams, bool recreateDeleted)
        {
            var result = new RestoreResult();

            // Performance optimization: Build complete room dictionary ONCE before loop
            // This includes ALL rooms in the document (not just _currentRooms)
            var allRoomsDict = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r =>
                {
                    var trackIdParam = r.LookupParameter("trackID");
                    return trackIdParam != null && !string.IsNullOrWhiteSpace(trackIdParam.AsString());
                })
                .ToDictionary(r => r.LookupParameter("trackID").AsString());

            using (Transaction trans = new Transaction(_doc, "Restore Rooms from Snapshot"))
            {
                trans.Start();

                foreach (var snapshot in _selectedVersionSnapshots)
                {
                    // Check if room exists anywhere in document (O(1) dictionary lookup)
                    if (allRoomsDict.TryGetValue(snapshot.TrackId, out Room existingRoom))
                    {
                        // Room exists - update parameters
                        UpdateRoomParameters(existingRoom, snapshot, selectedParams);
                        result.UpdatedRooms++;
                    }
                    else if (recreateDeleted)
                    {
                        // Room doesn't exist - create unplaced room if checkbox is checked
                        var newRoom = CreateUnplacedRoomWithLevelAndPhase(snapshot, selectedParams);
                        if (newRoom != null)
                        {
                            result.CreatedRooms++;
                            result.UnplacedRoomInfo.Add(new UnplacedRoomInfo
                            {
                                RoomNumber = snapshot.RoomNumber,
                                RoomName = snapshot.RoomName,
                                TrackId = snapshot.TrackId
                            });

                            // Add newly created room to dictionary to prevent duplicates in same transaction
                            allRoomsDict[snapshot.TrackId] = newRoom;
                        }
                    }
                    // else: room doesn't exist and recreateDeleted is false, skip it
                }

                trans.Commit();
            }

            return result;
        }

        private void UpdateRoomParameters(Room room, RoomSnapshot snapshot, List<string> selectedParams)
        {
            foreach (var paramKey in selectedParams)
            {
                try
                {
                    // Check if this is a parameter from AllParameters JSON
                    if (paramKey.StartsWith("AllParam_"))
                    {
                        // Extract the actual parameter name
                        string actualParamName = paramKey.Substring("AllParam_".Length);

                        // Get value from AllParameters dictionary
                        if (snapshot.AllParameters != null && snapshot.AllParameters.TryGetValue(actualParamName, out object value))
                        {
                            SetParameterFromObject(room, actualParamName, value);
                        }
                    }
                    else
                    {
                        // Handle dedicated column parameters
                        switch (paramKey)
                        {
                            case "RoomNumber":
                                if (!string.IsNullOrEmpty(snapshot.RoomNumber))
                                    room.Number = snapshot.RoomNumber;
                                break;

                            case "RoomName":
                                var nameParam = room.get_Parameter(BuiltInParameter.ROOM_NAME);
                                if (nameParam != null && !nameParam.IsReadOnly && !string.IsNullOrEmpty(snapshot.RoomName))
                                    nameParam.Set(snapshot.RoomName);
                                break;

                            case "Department":
                                SetParameterValue(room, new[] { "Department", "Service" }, snapshot.Department);
                                break;

                            case "Occupancy":
                                SetParameterValue(room, new[] { "Occupancy", "Occupation" }, snapshot.Occupancy);
                                break;

                            case "BaseFinish":
                                SetParameterValue(room, new[] { "Base Finish", "Finition de la base" }, snapshot.BaseFinish);
                                break;

                            case "CeilingFinish":
                                SetParameterValue(room, new[] { "Ceiling Finish", "Finition du plafond" }, snapshot.CeilingFinish);
                                break;

                            case "WallFinish":
                                SetParameterValue(room, new[] { "Wall Finish", "Finition du mur" }, snapshot.WallFinish);
                                break;

                            case "FloorFinish":
                                SetParameterValue(room, new[] { "Floor Finish", "Finition du sol" }, snapshot.FloorFinish);
                                break;

                            case "Comments":
                                var commentsParam = room.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                                if (commentsParam != null && !commentsParam.IsReadOnly && !string.IsNullOrEmpty(snapshot.Comments))
                                    commentsParam.Set(snapshot.Comments);
                                break;

                            case "Occupant":
                                SetParameterValue(room, new[] { "Occupant" }, snapshot.Occupant);
                                break;

                            case "Phase":
                                // Phase might be read-only, skip if so
                                var phaseParam = room.get_Parameter(BuiltInParameter.ROOM_PHASE);
                                if (phaseParam != null && !phaseParam.IsReadOnly && !string.IsNullOrEmpty(snapshot.Phase))
                                {
                                    // Would need to match phase name to ElementId - complex, skip for now
                                }
                                break;
                        }
                    }
                }
                catch
                {
                    // Skip parameters that can't be set
                }
            }
        }

        private void SetParameterFromObject(Room room, string paramName, object value)
        {
            if (value == null)
                return;

            var param = room.LookupParameter(paramName);
            if (param == null || param.IsReadOnly)
                return;

            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        param.Set(value.ToString());
                        break;

                    case StorageType.Integer:
                        if (value is long longVal)
                            param.Set((int)longVal);
                        else if (value is int intVal)
                            param.Set(intVal);
                        else if (int.TryParse(value.ToString(), out int parsedInt))
                            param.Set(parsedInt);
                        break;

                    case StorageType.Double:
                        if (value is double doubleVal)
                            param.Set(doubleVal);
                        else if (value is float floatVal)
                            param.Set(floatVal);
                        else if (double.TryParse(value.ToString(), out double parsedDouble))
                            param.Set(parsedDouble);
                        break;

                    case StorageType.ElementId:
                        // For ElementId parameters (materials, etc.), the snapshot stores display text (name)
                        // We need to find the element by name and set its ID
                        if (value == null)
                            break;

                        string elementName = value.ToString();
                        if (string.IsNullOrEmpty(elementName))
                            break;

                        // Try to find material by name (most common ElementId parameter for rooms)
                        var material = new FilteredElementCollector(_doc)
                            .OfClass(typeof(Material))
                            .Cast<Material>()
                            .FirstOrDefault(m => m.Name == elementName);

                        if (material != null)
                        {
                            param.Set(material.Id);
                        }
                        // If not found, try to find any element with that name
                        else
                        {
                            var element = new FilteredElementCollector(_doc)
                                .WhereElementIsNotElementType()
                                .FirstOrDefault(e => e.Name == elementName);

                            if (element != null)
                            {
                                param.Set(element.Id);
                            }
                        }
                        break;
                }
            }
            catch
            {
                // Skip if unable to set
            }
        }

        private void SetParameterValue(Room room, string[] parameterNames, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            foreach (var paramName in parameterNames)
            {
                var param = room.LookupParameter(paramName);
                if (param != null && !param.IsReadOnly)
                {
                    param.Set(value);
                    return;
                }
            }
        }

        private Room CreateUnplacedRoomWithLevelAndPhase(RoomSnapshot snapshot, List<string> selectedParams)
        {
            try
            {
                // Find the level from snapshot by name
                Level targetLevel = null;
                if (!string.IsNullOrEmpty(snapshot.Level))
                {
                    targetLevel = new FilteredElementCollector(_doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .FirstOrDefault(l => l.Name == snapshot.Level);
                }

                // Fallback to first level if snapshot level not found
                if (targetLevel == null)
                {
                    targetLevel = new FilteredElementCollector(_doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .OrderBy(l => l.Elevation)
                        .FirstOrDefault();
                }

                if (targetLevel == null)
                    return null;

                // Get phase for room creation
                Phase targetPhase = null;
                if (!string.IsNullOrEmpty(snapshot.Phase))
                {
                    var phases = _doc.Phases;
                    foreach (Phase phase in phases)
                    {
                        if (phase.Name == snapshot.Phase)
                        {
                            targetPhase = phase;
                            break;
                        }
                    }
                }

                // Fallback to last phase if snapshot phase not found
                if (targetPhase == null)
                {
                    targetPhase = _doc.Phases.get_Item(_doc.Phases.Size - 1);
                }

                // Create unplaced room (exists only in schedule, not placed in model)
                // Using NewRoom(phase) creates an unplaced room
                Room newRoom = _doc.Create.NewRoom(targetPhase);

                // Set the level manually
                var levelParam = newRoom.get_Parameter(BuiltInParameter.ROOM_LEVEL_ID);
                if (levelParam != null && !levelParam.IsReadOnly)
                {
                    levelParam.Set(targetLevel.Id);
                }

                // Set trackID first
                var trackIdParam = newRoom.LookupParameter("trackID");
                if (trackIdParam != null)
                {
                    trackIdParam.Set(snapshot.TrackId);
                }

                // Phase was already set during room creation (NewRoom(targetPhase))

                // Update parameters based on selection
                UpdateRoomParameters(newRoom, snapshot, selectedParams);

                return newRoom;
            }
            catch
            {
                return null;
            }
        }

        private void ShowRestoreResult(RestoreResult result, string versionName)
        {
            var resultWindow = new RestoreResultWindow(result, versionName);
            resultWindow.ShowDialog();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    public class RestoreResult
    {
        public int UpdatedRooms { get; set; }
        public int CreatedRooms { get; set; }
        public List<UnplacedRoomInfo> UnplacedRoomInfo { get; set; } = new List<UnplacedRoomInfo>();
    }

    public class UnplacedRoomInfo
    {
        public string RoomNumber { get; set; }
        public string RoomName { get; set; }
        public string TrackId { get; set; }
    }
}
