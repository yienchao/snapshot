using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
        private ObservableCollection<RoomRestoreItem> _roomRestoreItems = new ObservableCollection<RoomRestoreItem>();

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
                PopulateRoomList();
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
            // If there are no current rooms, we'll rely solely on snapshot parameters
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
            // Note: If no current rooms exist, parameters are still populated from snapshots above

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

                checkbox.Checked += (s, e) =>
                {
                    UpdateStatus();
                    UpdateDeletedRoomsParameterPreview();
                    UpdateRoomListParameterPreview();
                };
                checkbox.Unchecked += (s, e) =>
                {
                    UpdateStatus();
                    UpdateDeletedRoomsParameterPreview();
                    UpdateRoomListParameterPreview();
                };

                ParameterCheckboxPanel.Children.Add(checkbox);
            }
        }

        private void ScopeRadio_Changed(object sender, RoutedEventArgs e)
        {
            // Show/hide the third panel based on scope selection
            if (DeletedRoomsGroupBox != null && RoomListGroupBox != null)
            {
                if (DeletedRoomsRadio?.IsChecked == true)
                {
                    // Show deleted rooms panel
                    RoomListGroupBox.Visibility = System.Windows.Visibility.Collapsed;
                    DeletedRoomsGroupBox.Header = "Deleted Rooms to Recreate";
                    DeletedRoomsGroupBox.Visibility = System.Windows.Visibility.Visible;
                    PopulateDeletedRoomsList();
                }
                else if (UnplacedRoomsRadio?.IsChecked == true)
                {
                    // Show unplaced rooms panel
                    RoomListGroupBox.Visibility = System.Windows.Visibility.Collapsed;
                    DeletedRoomsGroupBox.Header = "Unplaced Rooms to Restore";
                    DeletedRoomsGroupBox.Visibility = System.Windows.Visibility.Visible;
                    PopulateUnplacedRoomsList();
                }
                else
                {
                    // Show normal room list panel for All/Pre-selected scopes
                    DeletedRoomsGroupBox.Visibility = System.Windows.Visibility.Collapsed;
                    RoomListGroupBox.Visibility = System.Windows.Visibility.Visible;
                    PopulateRoomList();
                }
            }

            UpdateStatus();
        }

        private void PopulateUnplacedRoomsList()
        {
            if (_selectedVersionSnapshots == null || !_selectedVersionSnapshots.Any())
                return;
            if (_currentRooms == null || !_currentRooms.Any())
                return;

            // Get current room trackIDs mapped to rooms
            var currentRoomsDict = _currentRooms
                .Where(r => r.LookupParameter("trackID") != null)
                .ToDictionary(r => r.LookupParameter("trackID").AsString(), r => r);

            // Find unplaced rooms: exist in current model, exist in snapshot as placed, but are now unplaced
            var unplacedItems = new List<DeletedRoomItem>();
            foreach (var snapshot in _selectedVersionSnapshots)
            {
                // Check if room exists in current model
                if (!currentRoomsDict.TryGetValue(snapshot.TrackId, out Room currentRoom))
                    continue;

                // Check if room is currently unplaced (Location == null)
                bool roomIsUnplaced = currentRoom.Location == null;

                // Check if snapshot shows it was placed (has position data and area > 0)
                bool snapshotWasPlaced = snapshot.Area.HasValue && snapshot.Area.Value > 0 &&
                                        snapshot.PositionX.HasValue && snapshot.PositionY.HasValue;

                // If room is unplaced now but was placed in snapshot, add to list
                if (roomIsUnplaced && snapshotWasPlaced)
                {
                    bool hasValidCoordinates = snapshot.PositionX.HasValue && snapshot.PositionY.HasValue &&
                                              (snapshot.PositionX.Value != 0 || snapshot.PositionY.Value != 0);

                    var item = new DeletedRoomItem
                    {
                        Snapshot = snapshot,
                        RoomDisplayName = $"{snapshot.RoomNumber} - {snapshot.RoomName}",
                        LevelAreaDisplay = $"Level: {snapshot.Level ?? "N/A"} | Area was: {(snapshot.Area.HasValue ? $"{snapshot.Area.Value * 0.09290304:F3} m²" : "N/A")}",
                        IsSelected = true,
                        HasValidCoordinates = hasValidCoordinates,
                        PlaceAtLocation = hasValidCoordinates,
                        CreateUnplaced = !hasValidCoordinates,
                        GroupName = $"Room_{snapshot.TrackId}",
                        ParameterPreview = new System.Collections.ObjectModel.ObservableCollection<ParameterPreviewItem>()
                    };
                    unplacedItems.Add(item);
                }
            }

            DeletedRoomsItemsControl.ItemsSource = unplacedItems;
            UpdateDeletedRoomsParameterPreview();
        }

        private void UpdateStatus()
        {
            if (StatusText == null || _selectedVersionSnapshots == null)
            {
                if (StatusText != null)
                    StatusText.Text = "Select a version to see status information.";
                return;
            }

            // Get current room trackIDs mapped to rooms
            var currentRoomsDict = (_currentRooms != null && _currentRooms.Any())
                ? _currentRooms
                    .Where(r => r.LookupParameter("trackID") != null)
                    .ToDictionary(r => r.LookupParameter("trackID").AsString(), r => r)
                : new Dictionary<string, Room>();

            var currentTrackIds = currentRoomsDict.Keys.ToHashSet();
            var snapshotTrackIds = _selectedVersionSnapshots.Select(s => s.TrackId).ToHashSet();

            // Calculate counts based on scope selection
            var existingCount = currentTrackIds.Intersect(snapshotTrackIds).Count();
            var deletedCount = snapshotTrackIds.Except(currentTrackIds).Count();

            // Count unplaced rooms (exist in model but currently unplaced, were placed in snapshot)
            var unplacedCount = _selectedVersionSnapshots.Count(s =>
            {
                if (!currentRoomsDict.TryGetValue(s.TrackId, out Room currentRoom))
                    return false;

                bool roomIsUnplaced = currentRoom.Location == null;
                bool snapshotWasPlaced = s.PositionX.HasValue && s.PositionY.HasValue;

                return roomIsUnplaced && snapshotWasPlaced;
            });

            // Filter based on selected scope
            List<RoomSnapshot> relevantSnapshots = null;
            if (DeletedRoomsRadio?.IsChecked == true)
            {
                relevantSnapshots = _selectedVersionSnapshots
                    .Where(s => !currentTrackIds.Contains(s.TrackId))
                    .ToList();
            }
            else if (UnplacedRoomsRadio?.IsChecked == true)
            {
                relevantSnapshots = _selectedVersionSnapshots
                    .Where(s =>
                    {
                        if (!currentRoomsDict.TryGetValue(s.TrackId, out Room currentRoom))
                            return false;

                        bool roomIsUnplaced = currentRoom.Location == null;
                        bool snapshotWasPlaced = s.PositionX.HasValue && s.PositionY.HasValue;

                        return roomIsUnplaced && snapshotWasPlaced;
                    })
                    .ToList();
            }
            else if (SelectedRoomsRadio?.IsChecked == true && _hasPreSelection)
            {
                relevantSnapshots = _selectedVersionSnapshots
                    .Where(s => currentTrackIds.Contains(s.TrackId))
                    .ToList();
            }
            else
            {
                relevantSnapshots = _selectedVersionSnapshots;
            }

            var sb = new StringBuilder();

            // Show different status based on scope
            if (DeletedRoomsRadio?.IsChecked == true)
            {
                sb.AppendLine($"• {deletedCount} deleted rooms will be recreated");
                sb.AppendLine($"  (Rooms in snapshot but not in current model)");
            }
            else if (UnplacedRoomsRadio?.IsChecked == true)
            {
                sb.AppendLine($"• {unplacedCount} unplaced rooms will have placement restored");
                sb.AppendLine($"  (Rooms that exist but are unplaced, were placed in snapshot)");
            }
            else if (SelectedRoomsRadio?.IsChecked == true && _hasPreSelection)
            {
                sb.AppendLine($"• {relevantSnapshots.Count} pre-selected rooms will be updated");
            }
            else
            {
                sb.AppendLine($"• {existingCount} existing rooms will be updated");
                if (deletedCount > 0)
                {
                    sb.AppendLine($"• {deletedCount} deleted rooms will be ignored");
                    sb.AppendLine($"  (Use 'Deleted rooms only' scope to recreate them)");
                }
                if (unplacedCount > 0)
                {
                    sb.AppendLine($"• {unplacedCount} unplaced rooms will be ignored");
                    sb.AppendLine($"  (Use 'Unplaced rooms only' scope to restore placement)");
                }
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

            // Filter snapshots to only those matching pre-selected rooms (if applicable)
            var snapshotsToRestore = GetRelevantSnapshots();

            // Show preview window
            var previewWindow = new RestorePreviewWindow(
                _doc,
                _currentRooms,
                snapshotsToRestore,
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
                // Filter snapshots to only those matching pre-selected rooms (if applicable)
                var snapshotsToRestore = GetRelevantSnapshots();
                PerformRestore(selectedParams, snapshotsToRestore);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Restore failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Gets the relevant snapshots based on user scope selection.
        /// </summary>
        private List<RoomSnapshot> GetRelevantSnapshots()
        {
            if (_selectedVersionSnapshots == null)
                return _selectedVersionSnapshots;

            // Get current room trackIDs mapped to rooms for filtering
            var currentRoomsDict = _currentRooms
                .Where(r => r.LookupParameter("trackID") != null)
                .ToDictionary(r => r.LookupParameter("trackID").AsString(), r => r);

            var currentTrackIds = currentRoomsDict.Keys.ToHashSet();

            // Filter based on scope selection
            if (DeletedRoomsRadio?.IsChecked == true)
            {
                // Deleted rooms only: snapshots that DON'T exist in current model
                return _selectedVersionSnapshots
                    .Where(s => !currentTrackIds.Contains(s.TrackId))
                    .ToList();
            }
            else if (UnplacedRoomsRadio?.IsChecked == true)
            {
                // Unplaced rooms only: snapshots for rooms that exist but are currently unplaced
                return _selectedVersionSnapshots
                    .Where(s =>
                    {
                        // Room must exist in current model
                        if (!currentRoomsDict.TryGetValue(s.TrackId, out Room currentRoom))
                            return false;

                        // Room must be currently unplaced
                        bool roomIsUnplaced = currentRoom.Location == null;

                        // Snapshot must show it was placed
                        bool snapshotWasPlaced = s.PositionX.HasValue && s.PositionY.HasValue;

                        return roomIsUnplaced && snapshotWasPlaced;
                    })
                    .ToList();
            }
            else if (SelectedRoomsRadio?.IsChecked == true && _hasPreSelection)
            {
                // Pre-selected rooms only: snapshots matching pre-selected rooms
                return _selectedVersionSnapshots
                    .Where(s => currentTrackIds.Contains(s.TrackId))
                    .ToList();
            }
            else
            {
                // All rooms: return all snapshots
                return _selectedVersionSnapshots;
            }
        }

        private void PerformRestore(List<string> selectedParams, List<RoomSnapshot> snapshotsToRestore)
        {
            string versionName = (VersionComboBox.SelectedItem as VersionInfo).VersionName;
            bool createBackup = ChkCreateBackup.IsChecked == true;

            // Only recreate deleted rooms if "Deleted Rooms Only" scope is selected
            bool recreateDeleted = (DeletedRoomsRadio?.IsChecked == true);
            bool restorePlacement = false; // Placement is controlled per-room in the third panel

            // Step 1: Create backup snapshot if requested
            if (createBackup)
            {
                CreateBackupSnapshot();
            }

            // Step 2: Perform restore (using filtered snapshots)
            var restoreResult = RestoreRoomsFromSnapshot(selectedParams, recreateDeleted, restorePlacement, snapshotsToRestore);

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

        private RestoreResult RestoreRoomsFromSnapshot(List<string> selectedParams, bool recreateDeleted, bool restorePlacement, List<RoomSnapshot> snapshotsToRestore)
        {
            var result = new RestoreResult();

            // Get selected rooms from third panel UI
            Dictionary<string, DeletedRoomItem> selectedRoomsFromPanel = null;
            HashSet<string> selectedRoomTrackIds = null;
            bool isUnplacedScope = UnplacedRoomsRadio?.IsChecked == true;
            bool isDeletedScope = DeletedRoomsRadio?.IsChecked == true;
            bool isAllOrPreselectedScope = AllRoomsRadio?.IsChecked == true || SelectedRoomsRadio?.IsChecked == true;

            if ((isDeletedScope || isUnplacedScope) && DeletedRoomsItemsControl.ItemsSource is List<DeletedRoomItem> panelItems)
            {
                selectedRoomsFromPanel = panelItems
                    .Where(item => item.IsSelected)
                    .ToDictionary(item => item.Snapshot.TrackId);
            }
            else if (isAllOrPreselectedScope && _roomRestoreItems != null && _roomRestoreItems.Any())
            {
                // For All/Pre-selected scopes, get selected rooms from RoomListGroupBox
                selectedRoomTrackIds = _roomRestoreItems
                    .Where(item => item.IsSelected)
                    .Select(item => item.TrackId)
                    .ToHashSet();

                // If no rooms selected, show warning
                if (!selectedRoomTrackIds.Any())
                {
                    throw new Exception("No rooms selected for restore. Please select at least one room from the list.");
                }
            }

            // Performance optimization: Build complete room dictionary ONCE before loop
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
                try
                {
                    trans.Start();

                    foreach (var snapshot in snapshotsToRestore)
                {
                    // Determine snapshot placement state
                    bool snapshotWasPlaced = snapshot.PositionX.HasValue && snapshot.PositionY.HasValue;

                    // Check if room exists in current model
                    bool roomExists = allRoomsDict.TryGetValue(snapshot.TrackId, out Room existingRoom);

                    if (roomExists)
                    {
                        // Determine current room placement state
                        bool roomIsPlaced = existingRoom.Location != null;

                        // === SCENARIO MATRIX FOR EXISTING ROOMS ===

                        if (isUnplacedScope)
                        {
                            // Unplaced scope: Only process rooms that are currently unplaced but were placed in snapshot
                            if (!roomIsPlaced && snapshotWasPlaced)
                            {
                                // Check if this room was selected by user in the panel
                                if (selectedRoomsFromPanel != null && !selectedRoomsFromPanel.ContainsKey(snapshot.TrackId))
                                {
                                    continue; // Skip unselected rooms
                                }

                                // Get user preference for placement
                                bool shouldPlaceAtLocation = selectedRoomsFromPanel != null &&
                                                            selectedRoomsFromPanel.TryGetValue(snapshot.TrackId, out var item) &&
                                                            item.PlaceAtLocation;

                                // Strategy: Delete the unplaced room and recreate it
                                _doc.Delete(existingRoom.Id);
                                allRoomsDict.Remove(snapshot.TrackId);

                                // Recreate room with placement option
                                var newRoom = CreateRoomWithPlacement(snapshot, selectedParams, shouldPlaceAtLocation);
                                if (newRoom != null)
                                {
                                    result.CreatedRooms++;

                                    // If created as unplaced, add to warning list
                                    if (newRoom.Location == null)
                                    {
                                        result.UnplacedRoomInfo.Add(new UnplacedRoomInfo
                                        {
                                            RoomNumber = snapshot.RoomNumber,
                                            RoomName = snapshot.RoomName,
                                            TrackId = snapshot.TrackId
                                        });
                                    }

                                    // Add to dictionary to prevent duplicates
                                    allRoomsDict[snapshot.TrackId] = newRoom;
                                }
                            }
                            // else: Skip rooms that don't match unplaced criteria
                        }
                        else
                        {
                            // All Rooms or Selected Rooms scope: Just update parameters
                            // But only if this room is selected in the UI
                            if (isAllOrPreselectedScope && selectedRoomTrackIds != null && !selectedRoomTrackIds.Contains(snapshot.TrackId))
                            {
                                continue; // Skip unselected rooms
                            }

                            // We don't care about placement state - just sync parameters
                            UpdateRoomParameters(existingRoom, snapshot, selectedParams);
                            result.UpdatedRooms++;
                        }
                    }
                    else
                    {
                        // Room doesn't exist in current model (deleted room)

                        if (isDeletedScope)
                        {
                            // Deleted scope: Recreate deleted rooms

                            // Check if this deleted room was selected by user
                            if (selectedRoomsFromPanel != null && !selectedRoomsFromPanel.ContainsKey(snapshot.TrackId))
                            {
                                continue; // Skip unselected rooms
                            }

                            // Get user preference for placement
                            bool shouldPlaceAtLocation = selectedRoomsFromPanel != null &&
                                                        selectedRoomsFromPanel.TryGetValue(snapshot.TrackId, out var item) &&
                                                        item.PlaceAtLocation;

                            // Create room with placement option
                            var newRoom = CreateRoomWithPlacement(snapshot, selectedParams, shouldPlaceAtLocation);
                            if (newRoom != null)
                            {
                                result.CreatedRooms++;

                                // If created as unplaced, add to warning list
                                if (newRoom.Location == null)
                                {
                                    result.UnplacedRoomInfo.Add(new UnplacedRoomInfo
                                    {
                                        RoomNumber = snapshot.RoomNumber,
                                        RoomName = snapshot.RoomName,
                                        TrackId = snapshot.TrackId
                                    });
                                }

                                // Add to dictionary to prevent duplicates
                                allRoomsDict[snapshot.TrackId] = newRoom;
                            }
                        }
                        // else: Room doesn't exist and we're not in deleted scope - skip it
                    }
                }

                    trans.Commit();
                }
                catch (Exception ex)
                {
                    // Rollback transaction if it was started but not committed
                    if (trans.HasStarted() && !trans.HasEnded())
                    {
                        trans.RollBack();
                    }
                    throw new Exception($"Room restore failed and was rolled back: {ex.Message}", ex);
                }
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

        private Room CreateRoomWithPlacement(RoomSnapshot snapshot, List<string> selectedParams, bool restorePlacement)
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

                Room newRoom = null;

                // Create room at original position if requested and position data exists
                if (restorePlacement && snapshot.PositionX.HasValue && snapshot.PositionY.HasValue)
                {
                    // Create a UV point at the original position
                    var position = new XYZ(snapshot.PositionX.Value, snapshot.PositionY.Value,
                                          snapshot.PositionZ ?? targetLevel.Elevation);

                    // Try to create room at the specified position
                    // Note: This will only work if there's a valid room boundary at that location
                    try
                    {
                        newRoom = _doc.Create.NewRoom(targetLevel, new UV(position.X, position.Y));

                        // Set phase after creation
                        var phaseParam = newRoom.get_Parameter(BuiltInParameter.ROOM_PHASE);
                        if (phaseParam != null && !phaseParam.IsReadOnly)
                        {
                            phaseParam.Set(targetPhase.Id);
                        }
                    }
                    catch
                    {
                        // If placement fails (no valid boundary), fall back to unplaced room
                        newRoom = null;
                    }
                }

                // If placement was not requested or failed, create unplaced room
                if (newRoom == null)
                {
                    // Create unplaced room (exists only in schedule, not placed in model)
                    newRoom = _doc.Create.NewRoom(targetPhase);

                    // Set the level manually for unplaced rooms
                    var levelParam = newRoom.get_Parameter(BuiltInParameter.ROOM_LEVEL_ID);
                    if (levelParam != null && !levelParam.IsReadOnly)
                    {
                        levelParam.Set(targetLevel.Id);
                    }
                }

                // Set trackID first
                var trackIdParam = newRoom.LookupParameter("trackID");
                if (trackIdParam != null)
                {
                    trackIdParam.Set(snapshot.TrackId);
                }

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
            // Only show detailed window if there are unplaced rooms that need attention
            if (result.UnplacedRoomInfo != null && result.UnplacedRoomInfo.Any())
            {
                var resultWindow = new RestoreResultWindow(result, versionName);
                resultWindow.ShowDialog();
            }
            else
            {
                // Simple success message for normal cases
                string message = $"✓ Restore Complete\n\n" +
                                $"• {result.UpdatedRooms} room(s) updated\n" +
                                $"• {result.CreatedRooms} room(s) created\n\n" +
                                $"Restored from snapshot: {versionName}";

                TaskDialog.Show("Restore Complete", message, TaskDialogCommonButtons.Ok);
            }
        }

        private void SelectAllDeleted_Click(object sender, RoutedEventArgs e)
        {
            if (DeletedRoomsItemsControl.ItemsSource is List<DeletedRoomItem> items)
            {
                foreach (var item in items)
                {
                    item.IsSelected = true;
                }
                DeletedRoomsItemsControl.Items.Refresh();
            }
        }

        private void SelectNoneDeleted_Click(object sender, RoutedEventArgs e)
        {
            if (DeletedRoomsItemsControl.ItemsSource is List<DeletedRoomItem> items)
            {
                foreach (var item in items)
                {
                    item.IsSelected = false;
                }
                DeletedRoomsItemsControl.Items.Refresh();
            }
        }

        private void PlacedAll_Click(object sender, RoutedEventArgs e)
        {
            if (DeletedRoomsItemsControl.ItemsSource is List<DeletedRoomItem> items)
            {
                foreach (var item in items)
                {
                    if (item.HasValidCoordinates)
                    {
                        item.PlaceAtLocation = true;
                        item.CreateUnplaced = false;
                    }
                }
                DeletedRoomsItemsControl.Items.Refresh();
            }
        }

        private void UnplacedAll_Click(object sender, RoutedEventArgs e)
        {
            if (DeletedRoomsItemsControl.ItemsSource is List<DeletedRoomItem> items)
            {
                foreach (var item in items)
                {
                    item.PlaceAtLocation = false;
                    item.CreateUnplaced = true;
                }
                DeletedRoomsItemsControl.Items.Refresh();
            }
        }

        private void PopulateDeletedRoomsList()
        {
            if (_selectedVersionSnapshots == null || !_selectedVersionSnapshots.Any())
                return;

            // Get current room trackIDs (empty set if no current rooms)
            var currentTrackIds = (_currentRooms != null && _currentRooms.Any())
                ? _currentRooms
                    .Select(r => r.LookupParameter("trackID")?.AsString())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet()
                : new HashSet<string>();

            // Find deleted rooms (in snapshot but not in current model)
            var deletedSnapshots = _selectedVersionSnapshots
                .Where(s => !currentTrackIds.Contains(s.TrackId))
                .ToList();

            // Create DeletedRoomItem objects
            var deletedItems = new List<DeletedRoomItem>();
            foreach (var snapshot in deletedSnapshots)
            {
                bool hasValidCoordinates = snapshot.PositionX.HasValue && snapshot.PositionY.HasValue &&
                                          (snapshot.PositionX.Value != 0 || snapshot.PositionY.Value != 0);

                var item = new DeletedRoomItem
                {
                    Snapshot = snapshot,
                    RoomDisplayName = $"{snapshot.RoomNumber} - {snapshot.RoomName}",
                    LevelAreaDisplay = $"Level: {snapshot.Level ?? "N/A"} | Area: {(snapshot.Area.HasValue ? $"{snapshot.Area.Value * 0.09290304:F3} m²" : "N/A")}",
                    IsSelected = true,
                    HasValidCoordinates = hasValidCoordinates,
                    PlaceAtLocation = hasValidCoordinates,
                    CreateUnplaced = !hasValidCoordinates,
                    GroupName = $"Room_{snapshot.TrackId}",
                    ParameterPreview = new System.Collections.ObjectModel.ObservableCollection<ParameterPreviewItem>()
                };
                deletedItems.Add(item);
            }

            DeletedRoomsItemsControl.ItemsSource = deletedItems;
            UpdateDeletedRoomsParameterPreview();
        }

        private void UpdateDeletedRoomsParameterPreview()
        {
            if (DeletedRoomsItemsControl.ItemsSource is List<DeletedRoomItem> items)
            {
                var selectedParams = GetSelectedParameters();

                foreach (var item in items)
                {
                    item.ParameterPreview.Clear();

                    foreach (var paramKey in selectedParams)
                    {
                        string paramValue = GetParameterValueFromSnapshot(item.Snapshot, paramKey);
                        if (!string.IsNullOrEmpty(paramValue))
                        {
                            string displayName = paramKey.StartsWith("AllParam_")
                                ? paramKey.Substring("AllParam_".Length)
                                : GetParameterDisplayName(paramKey);

                            item.ParameterPreview.Add(new ParameterPreviewItem
                            {
                                Name = displayName,
                                Value = paramValue
                            });
                        }
                    }
                }
            }
        }

        private string GetParameterValueFromSnapshot(RoomSnapshot snapshot, string paramKey)
        {
            if (paramKey.StartsWith("AllParam_"))
            {
                string actualParamName = paramKey.Substring("AllParam_".Length);
                if (snapshot.AllParameters != null && snapshot.AllParameters.TryGetValue(actualParamName, out object value))
                {
                    return value?.ToString() ?? "";
                }
            }
            else
            {
                switch (paramKey)
                {
                    case "RoomNumber": return snapshot.RoomNumber;
                    case "RoomName": return snapshot.RoomName;
                    case "Department": return snapshot.Department;
                    case "Occupancy": return snapshot.Occupancy;
                    case "BaseFinish": return snapshot.BaseFinish;
                    case "CeilingFinish": return snapshot.CeilingFinish;
                    case "WallFinish": return snapshot.WallFinish;
                    case "FloorFinish": return snapshot.FloorFinish;
                    case "Comments": return snapshot.Comments;
                    case "Occupant": return snapshot.Occupant;
                    case "Phase": return snapshot.Phase;
                }
            }
            return "";
        }

        private string GetParameterDisplayName(string paramKey)
        {
            switch (paramKey)
            {
                case "RoomNumber": return "Room Number";
                case "RoomName": return "Room Name";
                case "Department": return "Department";
                case "Occupancy": return "Occupancy";
                case "BaseFinish": return "Base Finish";
                case "CeilingFinish": return "Ceiling Finish";
                case "WallFinish": return "Wall Finish";
                case "FloorFinish": return "Floor Finish";
                case "Comments": return "Comments";
                case "Occupant": return "Occupant";
                case "Phase": return "Phase";
                default: return paramKey;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void SelectAllRooms_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _roomRestoreItems)
            {
                item.IsSelected = true;
            }
        }

        private void SelectNoneRooms_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _roomRestoreItems)
            {
                item.IsSelected = false;
            }
        }

        private void PopulateRoomList()
        {
            _roomRestoreItems.Clear();

            if (_selectedVersionSnapshots == null || !_currentRooms.Any())
                return;

            // Build trackID to snapshot mapping
            var snapshotMap = _selectedVersionSnapshots.ToDictionary(s => s.TrackId);

            // Get selected parameters
            var selectedParams = GetSelectedParameters();

            // Get current room trackIDs
            var currentRoomsDict = _currentRooms
                .Where(r => r.LookupParameter("trackID") != null)
                .ToDictionary(r => r.LookupParameter("trackID").AsString(), r => r);

            // Create restore items for each room
            foreach (var room in _currentRooms)
            {
                var trackIdParam = room.LookupParameter("trackID");
                if (trackIdParam == null || string.IsNullOrWhiteSpace(trackIdParam.AsString()))
                    continue;

                string trackId = trackIdParam.AsString();
                if (!snapshotMap.ContainsKey(trackId))
                    continue;

                var snapshot = snapshotMap[trackId];

                // Get room info
                string roomDisplayName = $"{snapshot.RoomNumber} - {snapshot.RoomName}";
                string roomInfo = $"Level: {snapshot.Level ?? "N/A"} | Area: {(snapshot.Area.HasValue ? $"{snapshot.Area.Value * 0.09290304:F3} m²" : "N/A")}";

                // Get parameter preview
                var parameterPreview = new ObservableCollection<RoomParameterPreview>();

                if (selectedParams.Any())
                {
                    foreach (var paramKey in selectedParams)
                    {
                        string paramValue = GetParameterValueFromSnapshot(snapshot, paramKey);
                        if (!string.IsNullOrEmpty(paramValue))
                        {
                            string displayName = paramKey.StartsWith("AllParam_")
                                ? paramKey.Substring("AllParam_".Length)
                                : GetParameterDisplayName(paramKey);

                            parameterPreview.Add(new RoomParameterPreview
                            {
                                Name = displayName,
                                Value = paramValue
                            });
                        }
                    }
                }

                var restoreItem = new RoomRestoreItem
                {
                    Room = room,
                    TrackId = trackId,
                    RoomDisplayName = roomDisplayName,
                    RoomInfo = roomInfo,
                    ParameterPreview = parameterPreview,
                    IsSelected = true
                };

                _roomRestoreItems.Add(restoreItem);
            }

            RoomsItemsControl.ItemsSource = _roomRestoreItems;
        }

        private void UpdateRoomListParameterPreview()
        {
            if (_roomRestoreItems == null || !_roomRestoreItems.Any())
                return;

            var selectedParams = GetSelectedParameters();

            // Build snapshot map
            var snapshotMap = _selectedVersionSnapshots?.ToDictionary(s => s.TrackId) ?? new Dictionary<string, RoomSnapshot>();

            foreach (var item in _roomRestoreItems)
            {
                item.ParameterPreview.Clear();

                if (!snapshotMap.ContainsKey(item.TrackId))
                    continue;

                var snapshot = snapshotMap[item.TrackId];

                foreach (var paramKey in selectedParams)
                {
                    string paramValue = GetParameterValueFromSnapshot(snapshot, paramKey);
                    if (!string.IsNullOrEmpty(paramValue))
                    {
                        string displayName = paramKey.StartsWith("AllParam_")
                            ? paramKey.Substring("AllParam_".Length)
                            : GetParameterDisplayName(paramKey);

                        item.ParameterPreview.Add(new RoomParameterPreview
                        {
                            Name = displayName,
                            Value = paramValue
                        });
                    }
                }
            }
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

    public class DeletedRoomItem : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _placeAtLocation;
        private bool _createUnplaced;

        public RoomSnapshot Snapshot { get; set; }
        public string RoomDisplayName { get; set; }
        public string LevelAreaDisplay { get; set; }
        public string GroupName { get; set; }
        public bool HasValidCoordinates { get; set; }

        public System.Collections.ObjectModel.ObservableCollection<ParameterPreviewItem> ParameterPreview { get; set; }

        public System.Windows.Visibility HasParametersVisibility => (ParameterPreview != null && ParameterPreview.Count > 0)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        public System.Windows.Visibility HasNoParameters => (ParameterPreview == null || ParameterPreview.Count == 0)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }

        public bool PlaceAtLocation
        {
            get => _placeAtLocation;
            set
            {
                _placeAtLocation = value;
                if (value) _createUnplaced = false;
                OnPropertyChanged(nameof(PlaceAtLocation));
                OnPropertyChanged(nameof(CreateUnplaced));
            }
        }

        public bool CreateUnplaced
        {
            get => _createUnplaced;
            set
            {
                _createUnplaced = value;
                if (value) _placeAtLocation = false;
                OnPropertyChanged(nameof(CreateUnplaced));
                OnPropertyChanged(nameof(PlaceAtLocation));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }
    }

    public class ParameterPreviewItem
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }

    // Data model for room restore items (for All/Pre-selected scopes)
    public class RoomRestoreItem : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected;

        public Room Room { get; set; }
        public string TrackId { get; set; }
        public string RoomDisplayName { get; set; }
        public string RoomInfo { get; set; }
        public ObservableCollection<RoomParameterPreview> ParameterPreview { get; set; } = new ObservableCollection<RoomParameterPreview>();

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }

        public System.Windows.Visibility HasParametersVisibility => (ParameterPreview != null && ParameterPreview.Count > 0)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        public System.Windows.Visibility HasNoParameters => (ParameterPreview == null || ParameterPreview.Count == 0)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }
    }

    public class RoomParameterPreview
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }
}
