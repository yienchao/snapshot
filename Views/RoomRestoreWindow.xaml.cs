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

        // PERFORMANCE: Flag to suppress updates during bulk checkbox operations
        private bool _suppressUpdates = false;

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

            // REFACTORED: All parameters now come from AllParameters JSON
            // Only RoomNumber stays as separate UI option (since it's in dedicated column for indexing)
            allParameters.Add("Room Number|RoomNumber");

            // Add ALL parameters from AllParameters JSON (excluding read-only ones)
            if (sampleSnapshot.AllParameters != null)
            {
                foreach (var param in sampleSnapshot.AllParameters.Keys)
                {
                    // Skip IFC parameters
                    if (param.ToLower().Contains("ifc"))
                        continue;

                    if (!excludedParamNames.Contains(param))
                    {
                        // Use actual Revit parameter name for both display and key
                        allParameters.Add($"{param}|{param}");
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

                    // Skip IFC parameters (they're not useful for room restore)
                    if (paramName.ToLower().Contains("ifc"))
                        continue;

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

                    // Skip ROOM_NUMBER (it's handled separately above)
                    if (param.Definition is InternalDefinition internalDef2)
                    {
                        var builtInParam2 = internalDef2.BuiltInParameter;
                        if (builtInParam2 == BuiltInParameter.ROOM_NUMBER)
                            continue;
                    }

                    // Add parameter if not already in list
                    // Use actual Revit parameter name for both display and key
                    string paramKey = $"{paramName}|{paramName}";
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
                    // PERFORMANCE: Skip updates during bulk operations
                    if (_suppressUpdates) return;

                    UpdateStatus();
                    UpdateDeletedRoomsParameterPreview();
                    UpdateRoomListParameterPreview();
                };
                checkbox.Unchecked += (s, e) =>
                {
                    // PERFORMANCE: Skip updates during bulk operations
                    if (_suppressUpdates) return;

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
                .ToDictionary(r => r.LookupParameter("trackID").AsString()?.Trim() ?? "", r => r, StringComparer.OrdinalIgnoreCase);

            // Find unplaced rooms: exist in current model, exist in snapshot as placed, but are now unplaced
            var unplacedItems = new List<DeletedRoomItem>();
            foreach (var snapshot in _selectedVersionSnapshots)
            {
                // Skip snapshots with invalid trackIDs
                if (string.IsNullOrWhiteSpace(snapshot.TrackId))
                    continue;

                // Check if room exists in current model
                if (!currentRoomsDict.TryGetValue(snapshot.TrackId?.Trim(), out Room currentRoom))
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

                    // REFACTORED: Get room name from AllParameters JSON
                    string roomName = "";
                    if (snapshot.AllParameters != null)
                    {
                        foreach (var key in new[] { "Nom", "Name", "Nombre" })
                        {
                            if (snapshot.AllParameters.TryGetValue(key, out object nameValue))
                            {
                                var paramVal = Models.ParameterValue.FromJsonObject(nameValue);
                                roomName = paramVal?.DisplayValue ?? "";
                                break;
                            }
                        }
                    }

                    var item = new DeletedRoomItem
                    {
                        Snapshot = snapshot,
                        RoomDisplayName = $"{snapshot.RoomNumber} - {roomName}",
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
                    .ToDictionary(r => r.LookupParameter("trackID").AsString()?.Trim() ?? "", r => r, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Room>(StringComparer.OrdinalIgnoreCase);

            var currentTrackIds = currentRoomsDict.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var snapshotTrackIds = _selectedVersionSnapshots
                .Select(s => s.TrackId?.Trim())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Calculate counts based on scope selection
            var existingCount = currentTrackIds.Intersect(snapshotTrackIds).Count();
            var deletedCount = snapshotTrackIds.Except(currentTrackIds).Count();

            // Count unplaced rooms (exist in model but currently unplaced, were placed in snapshot)
            var unplacedCount = _selectedVersionSnapshots.Count(s =>
            {
                if (string.IsNullOrWhiteSpace(s.TrackId))
                    return false;

                if (!currentRoomsDict.TryGetValue(s.TrackId?.Trim(), out Room currentRoom))
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
                    .Where(s => !string.IsNullOrWhiteSpace(s.TrackId) &&
                               !currentTrackIds.Contains(s.TrackId?.Trim()))
                    .ToList();
            }
            else if (UnplacedRoomsRadio?.IsChecked == true)
            {
                relevantSnapshots = _selectedVersionSnapshots
                    .Where(s =>
                    {
                        if (string.IsNullOrWhiteSpace(s.TrackId))
                            return false;

                        if (!currentRoomsDict.TryGetValue(s.TrackId?.Trim(), out Room currentRoom))
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
                    .Where(s => !string.IsNullOrWhiteSpace(s.TrackId) &&
                               currentTrackIds.Contains(s.TrackId?.Trim()))
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
            // PERFORMANCE: Disable updates during bulk operation
            _suppressUpdates = true;
            try
            {
                foreach (var child in ParameterCheckboxPanel.Children)
                {
                    if (child is CheckBox checkbox)
                    {
                        checkbox.IsChecked = true;
                    }
                }
            }
            finally
            {
                _suppressUpdates = false;
                // Update once at the end instead of 50+ times
                UpdateStatus();
                UpdateDeletedRoomsParameterPreview();
                UpdateRoomListParameterPreview();
            }
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            // PERFORMANCE: Disable updates during bulk operation
            _suppressUpdates = true;
            try
            {
                foreach (var child in ParameterCheckboxPanel.Children)
                {
                    if (child is CheckBox checkbox)
                    {
                        checkbox.IsChecked = false;
                    }
                }
            }
            finally
            {
                _suppressUpdates = false;
                // Update once at the end instead of 50+ times
                UpdateStatus();
                UpdateDeletedRoomsParameterPreview();
                UpdateRoomListParameterPreview();
            }
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
                .ToDictionary(r => r.LookupParameter("trackID").AsString()?.Trim() ?? "", r => r, StringComparer.OrdinalIgnoreCase);

            var currentTrackIds = currentRoomsDict.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Filter based on scope selection
            if (DeletedRoomsRadio?.IsChecked == true)
            {
                // Deleted rooms only: snapshots that DON'T exist in current model
                return _selectedVersionSnapshots
                    .Where(s => !string.IsNullOrWhiteSpace(s.TrackId) &&
                               !currentTrackIds.Contains(s.TrackId?.Trim()))
                    .ToList();
            }
            else if (UnplacedRoomsRadio?.IsChecked == true)
            {
                // Unplaced rooms only: snapshots for rooms that exist but are currently unplaced
                return _selectedVersionSnapshots
                    .Where(s =>
                    {
                        // Skip snapshots with invalid trackIDs
                        if (string.IsNullOrWhiteSpace(s.TrackId))
                            return false;

                        // Room must exist in current model
                        if (!currentRoomsDict.TryGetValue(s.TrackId?.Trim(), out Room currentRoom))
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
                    .Where(s => !string.IsNullOrWhiteSpace(s.TrackId) &&
                               currentTrackIds.Contains(s.TrackId?.Trim()))
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

                // REFACTORED: Only set dedicated columns for indexing/calculated values
                RoomNumber = room.Number,
                Level = room.Level?.Name,
                Area = room.Area,
                Perimeter = room.Perimeter,
                Volume = room.Volume,
                UnboundHeight = room.UnboundedHeight,

                // All other parameters will be in AllParameters
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
                    .Where(item => !string.IsNullOrWhiteSpace(item.Snapshot.TrackId))
                    .ToDictionary(item => item.Snapshot.TrackId?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);
            }
            else if (isAllOrPreselectedScope && _roomRestoreItems != null && _roomRestoreItems.Any())
            {
                // For All/Pre-selected scopes, get selected rooms from RoomListGroupBox
                selectedRoomTrackIds = _roomRestoreItems
                    .Where(item => item.IsSelected)
                    .Select(item => item.TrackId?.Trim())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // If no rooms selected, show warning
                if (!selectedRoomTrackIds.Any())
                {
                    throw new Exception("No rooms selected for restore. Please select at least one room from the list.");
                }
            }

            // Performance optimization: Use existing _currentRooms instead of re-querying
            var allRoomsDict = _currentRooms
                .Where(r =>
                {
                    var trackIdParam = r.LookupParameter("trackID");
                    return trackIdParam != null && !string.IsNullOrWhiteSpace(trackIdParam.AsString());
                })
                .ToDictionary(r => r.LookupParameter("trackID").AsString()?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);

            using (Transaction trans = new Transaction(_doc, "Restore Rooms from Snapshot"))
            {
                try
                {
                    trans.Start();

                    foreach (var snapshot in snapshotsToRestore)
                {
                    // Skip snapshots with invalid trackIDs
                    if (string.IsNullOrWhiteSpace(snapshot.TrackId))
                        continue;

                    string trackId = snapshot.TrackId?.Trim();

                    // Determine snapshot placement state
                    bool snapshotWasPlaced = snapshot.PositionX.HasValue && snapshot.PositionY.HasValue;

                    // Check if room exists in current model
                    bool roomExists = allRoomsDict.TryGetValue(trackId, out Room existingRoom);

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
                                if (selectedRoomsFromPanel != null && !selectedRoomsFromPanel.ContainsKey(trackId))
                                {
                                    continue; // Skip unselected rooms
                                }

                                // Get user preference for placement
                                bool shouldPlaceAtLocation = selectedRoomsFromPanel != null &&
                                                            selectedRoomsFromPanel.TryGetValue(trackId, out var item) &&
                                                            item.PlaceAtLocation;

                                // Strategy: Delete the unplaced room and recreate it
                                _doc.Delete(existingRoom.Id);
                                allRoomsDict.Remove(trackId);

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
                                            RoomName = GetRoomNameFromSnapshot(snapshot),
                                            TrackId = snapshot.TrackId
                                        });
                                    }

                                    // Add to dictionary to prevent duplicates
                                    allRoomsDict[trackId] = newRoom;
                                }
                            }
                            // else: Skip rooms that don't match unplaced criteria
                        }
                        else
                        {
                            // All Rooms or Selected Rooms scope: Just update parameters
                            // But only if this room is selected in the UI
                            if (isAllOrPreselectedScope && selectedRoomTrackIds != null && !selectedRoomTrackIds.Contains(trackId))
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
                            if (selectedRoomsFromPanel != null && !selectedRoomsFromPanel.ContainsKey(trackId))
                            {
                                continue; // Skip unselected rooms
                            }

                            // Get user preference for placement
                            bool shouldPlaceAtLocation = selectedRoomsFromPanel != null &&
                                                        selectedRoomsFromPanel.TryGetValue(trackId, out var item) &&
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
                                        RoomName = GetRoomNameFromSnapshot(snapshot),
                                        TrackId = snapshot.TrackId
                                    });
                                }

                                // Add to dictionary to prevent duplicates
                                allRoomsDict[trackId] = newRoom;
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
            // REFACTORED: All parameters now come from AllParameters JSON
            foreach (var paramKey in selectedParams)
            {
                try
                {
                    if (paramKey == "RoomNumber")
                    {
                        // RoomNumber is still in dedicated column
                        room.Number = snapshot.RoomNumber ?? "";
                    }
                    else if (snapshot.AllParameters != null && snapshot.AllParameters.TryGetValue(paramKey, out object value))
                    {
                        // Get parameter value from AllParameters JSON
                        SetParameterFromObject(room, paramKey, value);
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
            var param = room.LookupParameter(paramName);
            if (param == null || param.IsReadOnly)
                return;

            // Allow null/empty values for string parameters to clear them
            // For numeric parameters, skip if value is null

            object actualValue = value; // Declare outside try for error reporting
            try
            {
                // NEW: Handle ParameterValue objects from new snapshot format
                if (value is ViewTracker.Models.ParameterValue paramValue)
                {
                    // Extract the raw value based on storage type
                    actualValue = paramValue.RawValue;
                }
                else if (value is Newtonsoft.Json.Linq.JObject jObj)
                {
                    // Handle JSON deserialization - convert JObject to ParameterValue
                    var pv = ViewTracker.Models.ParameterValue.FromJsonObject(jObj);
                    if (pv != null)
                    {
                        actualValue = pv.RawValue;
                    }
                }

                switch (param.StorageType)
                {
                    case StorageType.String:
                        // Allow empty strings to be restored (can clear parameters)
                        param.Set(actualValue?.ToString() ?? "");
                        break;

                    case StorageType.Integer:
                        // Handle null (unset/gray state for booleans/integers)
                        if (actualValue == null)
                        {
                            // NOTE: Revit API doesn't allow clearing integer parameters back to "unset/gray" state
                            // Best we can do is set to 0 (unchecked for booleans, empty for integers)
                            param.Set(0);
                            break;
                        }
                        else if (actualValue is long longVal)
                        {
                            param.Set((int)longVal);
                        }
                        else if (actualValue is int intVal)
                        {
                            param.Set(intVal);
                        }
                        else if (int.TryParse(actualValue?.ToString(), out int parsedInt))
                        {
                            param.Set(parsedInt);
                        }
                        break;

                    case StorageType.Double:
                        // Skip if value is null (cannot set null for numeric parameters)
                        if (actualValue == null)
                            break;

                        if (actualValue is double doubleVal)
                            param.Set(doubleVal);
                        else if (actualValue is float floatVal)
                            param.Set(floatVal);
                        else if (double.TryParse(actualValue?.ToString().Replace(',', '.'),
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double parsedDouble))
                            param.Set(parsedDouble);
                        break;

                    case StorageType.ElementId:
                        // ElementId parameters (key schedules, references, etc.)
                        // Handle clearing to "none" (-1 or empty)
                        if (actualValue == null ||
                            actualValue.ToString() == "" ||
                            actualValue.ToString() == "-1")
                        {
                            // Set to InvalidElementId (clears the reference)
                            param.Set(ElementId.InvalidElementId);
                        }
                        else if (actualValue is long longId)
                        {
                            // Use long constructor (Revit 2024+)
                            param.Set(new ElementId(longId));
                        }
                        else if (actualValue is int intId)
                        {
                            // Convert int to long
                            param.Set(new ElementId((long)intId));
                        }
                        else if (long.TryParse(actualValue?.ToString(), out long parsedId))
                        {
                            param.Set(new ElementId(parsedId));
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
            // BUGFIX Issue #1: Allow empty strings to be restored (can clear parameters)
            // Don't return early for empty values - user may want to clear a parameter

            foreach (var paramName in parameterNames)
            {
                var param = room.LookupParameter(paramName);
                if (param != null && !param.IsReadOnly)
                {
                    param.Set(value ?? "");
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

                // REFACTORED: Get phase from AllParameters JSON
                Phase targetPhase = null;
                if (snapshot.AllParameters != null && snapshot.AllParameters.TryGetValue("Phase", out object phaseValue))
                {
                    var phaseParamValue = Models.ParameterValue.FromJsonObject(phaseValue);
                    if (phaseParamValue != null && phaseParamValue.RawValue != null)
                    {
                        long phaseId = Convert.ToInt64(phaseParamValue.RawValue);
                        var phaseElement = _doc.GetElement(new ElementId(phaseId));
                        if (phaseElement is Phase phase)
                        {
                            targetPhase = phase;
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
                    .Select(r => r.LookupParameter("trackID")?.AsString()?.Trim())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Find deleted rooms (in snapshot but not in current model)
            // Filter out snapshots with null/empty/whitespace trackIDs
            var deletedSnapshots = _selectedVersionSnapshots
                .Where(s => !string.IsNullOrWhiteSpace(s.TrackId) &&
                           !currentTrackIds.Contains(s.TrackId?.Trim()))
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
                    RoomDisplayName = $"{snapshot.RoomNumber} - {GetRoomNameFromSnapshot(snapshot)}",
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
                            // REFACTORED: paramKey is now the actual Revit parameter name
                            item.ParameterPreview.Add(new ParameterPreviewItem
                            {
                                Name = paramKey == "RoomNumber" ? "Room Number" : paramKey,
                                Value = paramValue
                            });
                        }
                    }
                }
            }
        }

        // REFACTORED: Helper to get room name from AllParameters JSON
        private string GetRoomNameFromSnapshot(RoomSnapshot snapshot)
        {
            if (snapshot.AllParameters == null)
                return "";

            foreach (var key in new[] { "Nom", "Name", "Nombre" })
            {
                if (snapshot.AllParameters.TryGetValue(key, out object nameValue))
                {
                    var paramVal = Models.ParameterValue.FromJsonObject(nameValue);
                    return paramVal?.DisplayValue ?? "";
                }
            }
            return "";
        }

        private string GetCurrentParameterValueFromRoom(Room room, string paramKey)
        {
            // REFACTORED: Get current value from actual Room
            if (paramKey == "RoomNumber")
            {
                return room.Number;
            }
            else
            {
                // Look up parameter by name (actual Revit parameter name)
                Parameter param = room.LookupParameter(paramKey);
                if (param != null && param.HasValue)
                {
                    var paramValue = Models.ParameterValue.FromRevitParameter(param);
                    return paramValue?.DisplayValue ?? "";
                }
            }
            return null;
        }

        private string GetParameterValueFromSnapshot(RoomSnapshot snapshot, string paramKey)
        {
            // REFACTORED: All parameters now come from AllParameters JSON
            // Only RoomNumber stays in dedicated column for indexing
            if (paramKey == "RoomNumber")
            {
                return snapshot.RoomNumber;
            }
            else if (snapshot.AllParameters != null && snapshot.AllParameters.TryGetValue(paramKey, out object value))
            {
                // Get parameter value from JSON using actual Revit parameter name
                var paramValue = Models.ParameterValue.FromJsonObject(value);
                return paramValue?.DisplayValue ?? "";
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
            var snapshotMap = _selectedVersionSnapshots
                .Where(s => !string.IsNullOrWhiteSpace(s.TrackId))
                .ToDictionary(s => s.TrackId?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);

            // Get selected parameters
            var selectedParams = GetSelectedParameters();

            // Get current room trackIDs
            var currentRoomsDict = _currentRooms
                .Where(r => r.LookupParameter("trackID") != null)
                .ToDictionary(r => r.LookupParameter("trackID").AsString()?.Trim() ?? "", r => r, StringComparer.OrdinalIgnoreCase);

            // Create restore items for each room
            foreach (var room in _currentRooms)
            {
                var trackIdParam = room.LookupParameter("trackID");
                if (trackIdParam == null || string.IsNullOrWhiteSpace(trackIdParam.AsString()))
                    continue;

                string trackId = trackIdParam.AsString()?.Trim();
                if (string.IsNullOrWhiteSpace(trackId) || !snapshotMap.ContainsKey(trackId))
                    continue;

                var snapshot = snapshotMap[trackId];

                // Get room info
                string roomDisplayName = $"{snapshot.RoomNumber} - {GetRoomNameFromSnapshot(snapshot)}";
                string roomInfo = $"Level: {snapshot.Level ?? "N/A"} | Area: {(snapshot.Area.HasValue ? $"{snapshot.Area.Value * 0.09290304:F3} m²" : "N/A")}";

                // Get parameter preview
                var parameterPreview = new ObservableCollection<RoomParameterPreview>();

                if (selectedParams.Any())
                {
                    foreach (var paramKey in selectedParams)
                    {
                        string newValue = GetParameterValueFromSnapshot(snapshot, paramKey);
                        string displayName = paramKey.StartsWith("AllParam_")
                            ? paramKey.Substring("AllParam_".Length)
                            : GetParameterDisplayName(paramKey);

                        // Get CURRENT value from room
                        string currentValue = "(empty)";
                        var currentParam = room.LookupParameter(displayName);
                        if (currentParam != null)
                        {
                            var currentParamValue = Models.ParameterValue.FromRevitParameter(currentParam);
                            if (currentParamValue != null)
                            {
                                currentValue = currentParamValue.DisplayValue ?? currentParamValue.RawValue?.ToString() ?? "(empty)";
                            }
                        }

                        parameterPreview.Add(new RoomParameterPreview
                        {
                            Name = displayName,
                            CurrentValue = currentValue,
                            NewValue = newValue ?? "(empty)",
                            IsDifferent = currentValue != (newValue ?? "(empty)"),
                            Value = newValue  // Backwards compatibility
                        });
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
                    // REFACTORED: paramKey is now the actual Revit parameter name
                    string displayName = paramKey == "RoomNumber" ? "Room Number" : paramKey;

                    // Use ParameterValue.IsEqualTo() to compare current vs snapshot
                    Models.ParameterValue currentParamValue = null;
                    Models.ParameterValue snapshotParamValue = null;

                    // Get current parameter value
                    if (paramKey == "RoomNumber")
                    {
                        currentParamValue = new Models.ParameterValue { StorageType = "String", RawValue = item.Room.Number ?? "", DisplayValue = item.Room.Number ?? "" };
                    }
                    else
                    {
                        var param = item.Room.LookupParameter(paramKey);
                        if (param != null)
                            currentParamValue = Models.ParameterValue.FromRevitParameter(param);
                    }

                    // Get snapshot parameter value
                    if (paramKey == "RoomNumber")
                    {
                        string storedValue = snapshot.RoomNumber;
                        if (!string.IsNullOrEmpty(storedValue))
                            snapshotParamValue = new Models.ParameterValue { StorageType = "String", RawValue = storedValue, DisplayValue = storedValue };
                    }
                    else if (snapshot.AllParameters != null && snapshot.AllParameters.TryGetValue(paramKey, out object value))
                    {
                        snapshotParamValue = Models.ParameterValue.FromJsonObject(value);
                    }

                    // Skip if snapshot value is null/empty
                    if (snapshotParamValue == null)
                        continue;

                    // Show ALL selected parameters, not just changed ones
                    string currentDisplay = currentParamValue?.DisplayValue ?? "(empty)";
                    string snapshotDisplay = snapshotParamValue?.DisplayValue ?? "(empty)";
                    bool isDifferent = currentParamValue == null || !snapshotParamValue.IsEqualTo(currentParamValue);

                    item.ParameterPreview.Add(new RoomParameterPreview
                    {
                        Name = displayName,
                        CurrentValue = currentDisplay,
                        NewValue = snapshotDisplay,
                        IsDifferent = isDifferent,
                        Value = $"{currentDisplay} → {snapshotDisplay}"  // Backwards compatibility
                    });
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
        public string CurrentValue { get; set; }  // Current value in the model (will be replaced)
        public string NewValue { get; set; }      // New value from snapshot
        public bool IsDifferent { get; set; }     // True if CurrentValue != NewValue

        // For backwards compatibility (if only showing snapshot value)
        public string Value { get; set; }

        // For conditional styling in XAML
        public System.Windows.Media.Brush NewValueColor => IsDifferent ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Black;
        public System.Windows.FontWeight NewValueWeight => IsDifferent ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal;
    }
}
