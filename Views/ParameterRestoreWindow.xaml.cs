using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ViewTracker.Views
{
    public partial class ParameterRestoreWindow : Window
    {
        private List<VersionInfo> _versions;
        private int _totalElementCount;
        private bool _hasPreSelection;
        private Document _doc;
        private SupabaseService _supabase;
        private Guid _projectId;
        private List<Element> _currentElements;
        private List<dynamic> _selectedVersionSnapshots; // Can be DoorSnapshot or ElementSnapshot
        private string _entityType; // "Door" or "Element"
        private Dictionary<string, CheckBox> _instanceParameterCheckboxes = new Dictionary<string, CheckBox>();
        private ObservableCollection<ElementRestoreItem> _elementRestoreItems = new ObservableCollection<ElementRestoreItem>();

        public ParameterRestoreWindow(
            List<VersionInfo> versions,
            int totalElementCount,
            bool hasPreSelection,
            Document doc,
            SupabaseService supabase,
            Guid projectId,
            List<Element> currentElements,
            string entityType)
        {
            InitializeComponent();

            _versions = versions;
            _totalElementCount = totalElementCount;
            _hasPreSelection = hasPreSelection;
            _doc = doc;
            _supabase = supabase;
            _projectId = projectId;
            _currentElements = currentElements;
            _entityType = entityType;

            // Update header based on entity type
            HeaderTitle.Text = $"Restore {_entityType} Parameters from Snapshot";

            // Populate version dropdown
            VersionComboBox.ItemsSource = _versions;
            if (_versions.Any())
            {
                VersionComboBox.SelectedIndex = 0;
            }

            // Set scope UI
            if (_hasPreSelection)
            {
                SelectedElementsRadio.IsChecked = true;
                AllElementsRadio.IsEnabled = true;
                ScopeInfoText.Text = $"{_totalElementCount} {_entityType.ToLower()}(s) pre-selected";
            }
            else
            {
                AllElementsRadio.IsChecked = true;
                SelectedElementsRadio.IsEnabled = false;
                ScopeInfoText.Text = $"{_totalElementCount} {_entityType.ToLower()}(s) with trackID found";
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

                    if (_entityType == "Door")
                    {
                        _selectedVersionSnapshots = (await _supabase.GetDoorsByVersionAsync(versionName, _projectId))
                            .Cast<dynamic>().ToList();
                    }
                    else // Element
                    {
                        _selectedVersionSnapshots = (await _supabase.GetElementsByVersionAsync(versionName, _projectId))
                            .Cast<dynamic>().ToList();
                    }
                }).Wait();

                PopulateParameterCheckboxes();
                PopulateElementList();
                UpdateStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load snapshot data:\n{ex.InnerException?.Message ?? ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ParameterCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            // Refresh element list when parameters are selected/deselected
            // OPTIMIZATION: Only update the preview for existing items instead of rebuilding entire list
            UpdateElementPreviews();
        }

        private void UpdateElementPreviews()
        {
            // Get selected parameters
            var selectedParams = GetSelectedParameters();

            // Update preview for each existing restore item without recreating the entire list
            foreach (var restoreItem in _elementRestoreItems)
            {
                UpdateElementPreview(restoreItem, selectedParams);
            }
        }

        private void UpdateElementPreview(ElementRestoreItem restoreItem, List<string> selectedParams)
        {
            var element = restoreItem.Element;
            var trackId = restoreItem.TrackId;

            // Get snapshot
            dynamic snapshot = null;
            if (_selectedVersionSnapshots != null)
            {
                snapshot = _selectedVersionSnapshots.FirstOrDefault(s => s.TrackId == trackId);
            }

            if (snapshot == null)
                return;

            // Rebuild parameter preview based on selected parameters
            var parameterPreview = new List<ElementParameterPreview>();

            // Get available parameters from snapshot
            Dictionary<string, object> allParameters = null;
            try
            {
                if (_entityType == "Door")
                {
                    allParameters = ((DoorSnapshot)snapshot).AllParameters;
                }
                else
                {
                    allParameters = ((ElementSnapshot)snapshot).AllParameters;
                }
            }
            catch { }

            if (allParameters != null && selectedParams.Any())
            {
                var nonRestorableParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "facing_x", "facing_y", "facing_z",
                    "hand_x", "hand_y", "hand_z",
                    "locationx", "locationy", "locationz",
                    "location_x", "location_y", "location_z",
                    "facingx", "facingy", "facingz",
                    "handx", "handy", "handz"
                };

                foreach (var paramName in selectedParams)
                {
                    if (nonRestorableParams.Contains(paramName))
                        continue;

                    if (allParameters.ContainsKey(paramName))
                    {
                        string displayValue = "(empty)";
                        var value = allParameters[paramName];

                        if (value != null)
                        {
                            // Extract from ParameterValue
                            if (value is ViewTracker.Models.ParameterValue pv)
                            {
                                displayValue = pv.DisplayValue ?? pv.RawValue?.ToString() ?? "(empty)";
                            }
                            else if (value is Newtonsoft.Json.Linq.JObject jObj)
                            {
                                var paramVal = ViewTracker.Models.ParameterValue.FromJsonObject(jObj);
                                if (paramVal != null)
                                {
                                    displayValue = paramVal.DisplayValue ?? paramVal.RawValue?.ToString() ?? "(empty)";
                                }
                            }
                            else
                            {
                                displayValue = value.ToString();
                            }
                        }

                        parameterPreview.Add(new ElementParameterPreview
                        {
                            Name = paramName,
                            Value = displayValue
                        });
                    }
                }
            }

            // If no parameters selected, check for orientation differences (this was already calculated)
            // Don't recalculate - just check if we need to show orientation status
            if (!selectedParams.Any() && (_entityType == "Door" || _entityType == "Element"))
            {
                // Check if there's cached orientation info from the initial population
                // For now, we'll add a simple check - this could be optimized further
                if (element is FamilyInstance familyInst && allParameters != null)
                {
                    try
                    {
                        XYZ snapshotFacing = GetOrientationFromSnapshot(allParameters, "facing");
                        XYZ snapshotHand = GetOrientationFromSnapshot(allParameters, "hand");

                        if (snapshotFacing != null && snapshotHand != null &&
                            familyInst.FacingOrientation != null && familyInst.HandOrientation != null)
                        {
                            double facingDot = familyInst.FacingOrientation.DotProduct(snapshotFacing);
                            double handDot = familyInst.HandOrientation.DotProduct(snapshotHand);

                            if (facingDot < 0.0)
                            {
                                parameterPreview.Add(new ElementParameterPreview
                                {
                                    Name = "⚠️ Facing orientation",
                                    Value = "Needs flip"
                                });
                            }
                            if (handDot < 0.0)
                            {
                                parameterPreview.Add(new ElementParameterPreview
                                {
                                    Name = "⚠️ Hand orientation",
                                    Value = "Needs flip"
                                });
                            }
                        }
                    }
                    catch { }
                }
            }

            // Update the restore item's preview
            restoreItem.ParameterPreview = parameterPreview;
        }

        private void PopulateParameterCheckboxes()
        {
            InstanceParameterPanel.Children.Clear();
            _instanceParameterCheckboxes.Clear();

            if (_selectedVersionSnapshots == null || !_selectedVersionSnapshots.Any())
            {
                var noDataText = new TextBlock
                {
                    Text = "No parameters available",
                    FontStyle = FontStyles.Italic,
                    Foreground = System.Windows.Media.Brushes.Gray
                };
                InstanceParameterPanel.Children.Add(noDataText);
                return;
            }

            // Get all unique parameters from snapshots (both AllParameters JSON and dedicated columns)
            var allSnapshotParams = new HashSet<string>();

            // Get a sample snapshot to determine which dedicated columns are available
            var sampleSnapshot = _selectedVersionSnapshots.FirstOrDefault();
            if (sampleSnapshot != null)
            {
                // Add dedicated column parameters using BuiltInParameter to get localized names
                // We need to get the actual parameter names from the current element
                var firstElement = _currentElements.FirstOrDefault();
                if (firstElement != null)
                {
                    // Add all dedicated column parameters (even if empty in snapshot)
                    // This allows users to restore these parameters regardless of their values

                    // Mark
                    var markParam = firstElement.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                    if (markParam != null)
                        allSnapshotParams.Add(markParam.Definition.Name);

                    // Level
                    var levelParam = firstElement.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                    if (levelParam != null)
                        allSnapshotParams.Add(levelParam.Definition.Name);

                    // Comments
                    var commentsParam = firstElement.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (commentsParam != null)
                        allSnapshotParams.Add(commentsParam.Definition.Name);

                    // Phase Created
                    var phaseCreatedParam = firstElement.get_Parameter(BuiltInParameter.PHASE_CREATED);
                    if (phaseCreatedParam != null)
                        allSnapshotParams.Add(phaseCreatedParam.Definition.Name);

                    // Phase Demolished
                    var phaseDemolishedParam = firstElement.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                    if (phaseDemolishedParam != null)
                        allSnapshotParams.Add(phaseDemolishedParam.Definition.Name);

                    // Fire Rating (doors only)
                    if (_entityType == "Door")
                    {
                        var fireRatingParam = firstElement.get_Parameter(BuiltInParameter.DOOR_FIRE_RATING);
                        if (fireRatingParam != null)
                            allSnapshotParams.Add(fireRatingParam.Definition.Name);
                    }
                }
            }

            // Add parameters from AllParameters JSON
            foreach (var snapshot in _selectedVersionSnapshots)
            {
                if (snapshot.AllParameters != null)
                {
                    foreach (var paramName in ((Dictionary<string, object>)snapshot.AllParameters).Keys)
                    {
                        allSnapshotParams.Add(paramName);
                    }
                }
            }

            // Get a sample element to analyze its parameters
            var sampleElement = _currentElements.FirstOrDefault();
            if (sampleElement == null)
            {
                var noElementText = new TextBlock
                {
                    Text = "No elements found in current model",
                    FontStyle = FontStyles.Italic,
                    Foreground = System.Windows.Media.Brushes.Gray
                };
                InstanceParameterPanel.Children.Add(noElementText);
                return;
            }

            var restorableParams = new List<string>();

            // Parameters that are NOT restorable (geometric properties, not real parameters)
            var nonRestorableParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "facing_x", "facing_y", "facing_z",      // FacingOrientation (read-only property)
                "hand_x", "hand_y", "hand_z",            // HandOrientation (read-only property)
                "locationx", "locationy", "locationz",   // Location (requires different API)
                "facingx", "facingy", "facingz",         // Alternative naming
                "handx", "handy", "handz"                // Alternative naming
            };

            // Use GetOrderedParameters() to get only USER-VISIBLE parameters in proper order
            // This is the correct Revit API method for getting visible parameters
            var orderedParams = sampleElement.GetOrderedParameters();

            // Filter to get only INSTANCE parameters (not type parameters)
            // Key: If param.Element is ElementType, it's a TYPE parameter
            foreach (Parameter param in orderedParams)
            {
                if (param == null || param.Definition == null)
                    continue;

                string paramName = param.Definition.Name;

                // Only process parameters that exist in our snapshots
                if (!allSnapshotParams.Contains(paramName))
                    continue;

                // Skip non-restorable geometric properties
                if (nonRestorableParams.Contains(paramName))
                    continue;

                // Skip IFC-related parameters (they should not be restored)
                if (paramName.ToLower().Contains("ifc"))
                    continue;

                // Note: We don't check param.IsReadOnly here (like rooms doesn't)
                // We'll check it later during actual restore and skip if needed
                // This allows parameters to appear in the list even if temporarily read-only

                // Check if this is a TYPE parameter by checking if param.Element is ElementType
                // If param.Element is ElementType → Type parameter (skip it)
                // If param.Element is null or NOT ElementType → Instance parameter (include it)
                if (param.Element is ElementType)
                {
                    // This is a type parameter - skip it
                    continue;
                }

                // This is an instance parameter (param.Element is null or not ElementType)
                // Add it to the restorable list
                restorableParams.Add(paramName);
            }

            // Create checkboxes for restorable instance parameters only
            if (restorableParams.Any())
            {
                foreach (var paramName in restorableParams.OrderBy(p => p))
                {
                    var checkbox = new CheckBox
                    {
                        Content = paramName,
                        IsChecked = true,
                        Margin = new Thickness(5, 3, 5, 3),
                        FontSize = 13
                    };
                    checkbox.Checked += ParameterCheckbox_Changed;
                    checkbox.Unchecked += ParameterCheckbox_Changed;
                    _instanceParameterCheckboxes[paramName] = checkbox;
                    InstanceParameterPanel.Children.Add(checkbox);
                }
            }
            else
            {
                var noInstanceText = new TextBlock
                {
                    Text = "No restorable instance parameters found",
                    FontStyle = FontStyles.Italic,
                    Foreground = System.Windows.Media.Brushes.Gray
                };
                InstanceParameterPanel.Children.Add(noInstanceText);
            }
        }

        private void ScopeRadio_Changed(object sender, RoutedEventArgs e)
        {
            // When switching to "All elements", fetch all elements from document
            if (AllElementsRadio?.IsChecked == true && _hasPreSelection)
            {
                // Fetch all elements with trackID from document
                try
                {
                    if (_entityType == "Door")
                    {
                        _currentElements = new FilteredElementCollector(_doc)
                            .OfCategory(BuiltInCategory.OST_Doors)
                            .WhereElementIsNotElementType()
                            .Cast<Element>()
                            .Where(e => e.LookupParameter("trackID") != null &&
                                       !string.IsNullOrWhiteSpace(e.LookupParameter("trackID").AsString()))
                            .ToList();
                    }
                    else // Element
                    {
                        _currentElements = new FilteredElementCollector(_doc)
                            .WhereElementIsNotElementType()
                            .Cast<Element>()
                            .Where(e => e.Category != null &&
                                       e.LookupParameter("trackID") != null &&
                                       !string.IsNullOrWhiteSpace(e.LookupParameter("trackID").AsString()))
                            .ToList();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error fetching all elements: {ex.Message}");
                }
            }

            // Reload the element list based on the new scope selection
            if (_selectedVersionSnapshots != null && _selectedVersionSnapshots.Any())
            {
                PopulateElementList();
            }
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            // Guard against being called before InitializeComponent completes
            if (StatusText == null) return;

            if (_selectedVersionSnapshots == null || !_currentElements.Any())
            {
                StatusText.Text = "Select a version to see restore options";
                return;
            }

            int matchCount = 0;
            int noMatchCount = 0;

            // Count how many snapshots have matching elements in current model
            var currentTrackIds = _currentElements
                .Select(e => e.LookupParameter("trackID")?.AsString())
                .Where(tid => !string.IsNullOrWhiteSpace(tid))
                .ToHashSet();

            foreach (var snapshot in _selectedVersionSnapshots)
            {
                string trackId = snapshot.TrackId;
                if (currentTrackIds.Contains(trackId))
                    matchCount++;
                else
                    noMatchCount++;
            }

            bool useAllElements = AllElementsRadio?.IsChecked == true;
            int targetCount = useAllElements ? matchCount : _totalElementCount;

            StatusText.Text = $"Ready to restore {targetCount} {_entityType.ToLower()}(s)\n\n" +
                              $"• {matchCount} {_entityType.ToLower()}(s) found in current model\n" +
                              $"• {noMatchCount} {_entityType.ToLower()}(s) from snapshot not found (will be skipped)";
        }

        private void SelectAllInstance_Click(object sender, RoutedEventArgs e)
        {
            foreach (var checkbox in _instanceParameterCheckboxes.Values)
            {
                checkbox.IsChecked = true;
            }
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var checkbox in _instanceParameterCheckboxes.Values)
            {
                checkbox.IsChecked = false;
            }
        }

        private void Preview_Click(object sender, RoutedEventArgs e)
        {
            var selectedParams = GetSelectedParameters();

            // For doors/elements, allow preview even with no parameters (for orientation info)
            if (!selectedParams.Any() && _entityType != "Door" && _entityType != "Element")
            {
                MessageBox.Show("Please select at least one instance parameter to restore.",
                    "No Parameters Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // TODO: Implement preview window
            MessageBox.Show($"Preview functionality will show changes for:\n\n" +
                          $"Parameters: {string.Join(", ", selectedParams.Take(10))}" +
                          $"{(selectedParams.Count > 10 ? $"\n...and {selectedParams.Count - 10} more" : "")}",
                          "Preview", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            var selectedParams = GetSelectedParameters();

            // For doors/elements, allow restore even with no parameters (for orientation restore)
            // For other entity types, require at least one parameter
            if (!selectedParams.Any() && _entityType != "Door" && _entityType != "Element")
            {
                MessageBox.Show("Please select at least one instance parameter to restore.",
                    "No Parameters Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (VersionComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a version to restore from.",
                    "No Version Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Build confirmation message
            string message = selectedParams.Count > 0
                ? $"Are you sure you want to restore {selectedParams.Count} parameter(s) for existing {_entityType.ToLower()}(s)?\n\n" +
                  $"This will update parameters based on the selected snapshot version."
                : $"Are you sure you want to restore orientation for existing {_entityType.ToLower()}(s)?\n\n" +
                  $"This will flip {_entityType.ToLower()}(s) back to their snapshot orientation (no parameters will be changed).";

            if (_entityType == "Door" || _entityType == "Element")
            {
                message += "\n\nOrientation (facing/hand) will also be restored automatically.";
            }

            if (ChkCreateBackup.IsChecked == true)
            {
                message += "\n\nA backup snapshot will be created first.";
            }

            var result = MessageBox.Show(message, "Confirm Restore", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            // Perform the restore
            PerformRestore(selectedParams);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private List<string> GetSelectedParameters()
        {
            return _instanceParameterCheckboxes
                .Where(kvp => kvp.Value.IsChecked == true)
                .Select(kvp => kvp.Key)
                .ToList();
        }

        private void PerformRestore(List<string> selectedParams)
        {
            try
            {
                // Get only selected elements from the element list
                var selectedElementItems = _elementRestoreItems.Where(item => item.IsSelected).ToList();
                if (!selectedElementItems.Any())
                {
                    MessageBox.Show("No elements selected for restore. Please select at least one element from the list.",
                        "No Elements Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var elementsToRestore = selectedElementItems.Select(item => item.Element).ToList();

                // Create backup if requested
                if (ChkCreateBackup.IsChecked == true)
                {
                    try
                    {
                        string backupVersionName = $"backup_{DateTime.Now:yyyyMMdd_HHmmss}";
                        string currentUser = Environment.UserName;
                        var now = DateTime.UtcNow;
                        string fileName = System.IO.Path.GetFileNameWithoutExtension(_doc.PathName);
                        if (string.IsNullOrEmpty(fileName))
                            fileName = _doc.Title;

                        // Create backup snapshots of current state
                        var backupSnapshots = new List<dynamic>();

                        foreach (var element in elementsToRestore)
                        {
                            var trackIdParam = element.LookupParameter("trackID");
                            if (trackIdParam == null || string.IsNullOrWhiteSpace(trackIdParam.AsString()))
                                continue;

                            string trackId = trackIdParam.AsString();

                            if (_entityType == "Door")
                            {
                                var door = element as FamilyInstance;
                                if (door == null) continue;

                                var allParams = GetAllParametersForBackup(door);
                                var snapshot = new DoorSnapshot
                                {
                                    TrackId = trackId,
                                    VersionName = backupVersionName,
                                    ProjectId = _projectId,
                                    FileName = fileName,
                                    SnapshotDate = now,
                                    CreatedBy = currentUser,
                                    IsOfficial = false,
                                    FamilyName = door.Symbol?.Family?.Name ?? "Unknown",
                                    TypeName = door.Symbol?.Name ?? "Unknown",
                                    Mark = door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "",
                                    Level = door.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)?.AsValueString() ?? "",
                                    Comments = door.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "",
                                    PhaseCreated = door.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsValueString() ?? "",
                                    PhaseDemolished = door.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.AsValueString() ?? "",
                                    FireRating = door.get_Parameter(BuiltInParameter.DOOR_FIRE_RATING)?.AsString() ?? "",
                                    AllParameters = allParams
                                };
                                backupSnapshots.Add(snapshot);
                            }
                            else // Element
                            {
                                var allParams = GetAllParametersForBackup(element);
                                var snapshot = new ElementSnapshot
                                {
                                    TrackId = trackId,
                                    VersionName = backupVersionName,
                                    ProjectId = _projectId,
                                    FileName = fileName,
                                    SnapshotDate = now,
                                    CreatedBy = currentUser,
                                    IsOfficial = false,
                                    Category = element.Category?.Name ?? "Unknown",
                                    FamilyName = (element as FamilyInstance)?.Symbol?.Family?.Name ?? "Unknown",
                                    TypeName = (element as FamilyInstance)?.Symbol?.Name ?? element.GetTypeId()?.ToString() ?? "Unknown",
                                    Mark = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "",
                                    Level = element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)?.AsValueString() ?? "",
                                    Comments = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "",
                                    PhaseCreated = element.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsValueString() ?? "",
                                    PhaseDemolished = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.AsValueString() ?? "",
                                    AllParameters = allParams
                                };
                                backupSnapshots.Add(snapshot);
                            }
                        }

                        if (backupSnapshots.Any())
                        {
                            // Save backup to Supabase
                            System.Threading.Tasks.Task.Run(async () =>
                            {
                                await _supabase.InitializeAsync();
                                if (_entityType == "Door")
                                {
                                    await _supabase.BulkUpsertDoorSnapshotsAsync(backupSnapshots.Cast<DoorSnapshot>().ToList());
                                }
                                else
                                {
                                    await _supabase.BulkUpsertElementSnapshotsAsync(backupSnapshots.Cast<ElementSnapshot>().ToList());
                                }
                            }).Wait();

                            MessageBox.Show($"Backup snapshot '{backupVersionName}' created successfully with {backupSnapshots.Count} {_entityType.ToLower()}(s).",
                                          "Backup Created", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                    catch (Exception backupEx)
                    {
                        MessageBox.Show($"Warning: Failed to create backup snapshot:\n{backupEx.InnerException?.Message ?? backupEx.Message}\n\nRestore will continue without backup.",
                                      "Backup Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                // Build trackID to snapshot mapping
                var snapshotMap = new Dictionary<string, dynamic>();
                foreach (var snapshot in _selectedVersionSnapshots)
                {
                    snapshotMap[snapshot.TrackId] = snapshot;
                }

                int updatedCount = 0;
                int skippedCount = 0;
                var errors = new List<string>();

                using (var transaction = new Transaction(_doc, $"Restore {_entityType} Parameters"))
                {
                    transaction.Start();

                    foreach (var element in elementsToRestore)
                    {
                        var trackIdParam = element.LookupParameter("trackID");
                        if (trackIdParam == null || string.IsNullOrWhiteSpace(trackIdParam.AsString()))
                        {
                            skippedCount++;
                            continue;
                        }

                        string trackId = trackIdParam.AsString();
                        if (!snapshotMap.ContainsKey(trackId))
                        {
                            skippedCount++;
                            continue;
                        }

                        dynamic snapshot = snapshotMap[trackId];

                        // Build a unified parameters dictionary from both AllParameters JSON and dedicated columns
                        var parametersToRestore = new Dictionary<string, object>();

                        // First, add parameters from AllParameters JSON
                        Dictionary<string, object> allParameters = null;
                        try
                        {
                            if (_entityType == "Door")
                            {
                                allParameters = ((DoorSnapshot)snapshot).AllParameters;
                            }
                            else // Element
                            {
                                allParameters = ((ElementSnapshot)snapshot).AllParameters;
                            }

                            if (allParameters != null)
                            {
                                foreach (var kvp in allParameters)
                                {
                                    parametersToRestore[kvp.Key] = kvp.Value;
                                }
                            }
                        }
                        catch { }

                        // Then, add dedicated column parameters using BuiltInParameter to get localized names
                        // Include even if empty (matches comparison logic)
                        // Mark
                        var markParam = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                        if (markParam != null)
                            parametersToRestore[markParam.Definition.Name] = snapshot.Mark ?? "";

                        // Level
                        var levelParam = element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                        if (levelParam != null)
                            parametersToRestore[levelParam.Definition.Name] = snapshot.Level ?? "";

                        // Comments
                        var commentsParam = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                        if (commentsParam != null)
                            parametersToRestore[commentsParam.Definition.Name] = snapshot.Comments ?? "";

                        // Phase Created
                        var phaseCreatedParam = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
                        if (phaseCreatedParam != null)
                            parametersToRestore[phaseCreatedParam.Definition.Name] = snapshot.PhaseCreated ?? "";

                        // Phase Demolished
                        var phaseDemolishedParam = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                        if (phaseDemolishedParam != null)
                            parametersToRestore[phaseDemolishedParam.Definition.Name] = snapshot.PhaseDemolished ?? "";

                        // Fire Rating (doors only)
                        if (_entityType == "Door")
                        {
                            var fireRatingParam = element.get_Parameter(BuiltInParameter.DOOR_FIRE_RATING);
                            if (fireRatingParam != null)
                                parametersToRestore[fireRatingParam.Definition.Name] = snapshot.FireRating ?? "";
                        }

                        if (!parametersToRestore.Any())
                        {
                            skippedCount++;
                            continue;
                        }

                        // Restore selected parameters
                        bool elementHadChanges = false;
                        foreach (var paramName in selectedParams)
                        {
                            if (!parametersToRestore.ContainsKey(paramName))
                            {
                                errors.Add($"Element {element.Id}: Parameter '{paramName}' not found in snapshot");
                                continue;
                            }

                            try
                            {
                                Parameter param = null;

                                // Special handling for certain parameters - use get_Parameter instead of LookupParameter
                                // Check if this is one of the special parameters by comparing with the parameter name

                                // Level parameter
                                var levelParameterCheck = element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                                if (levelParameterCheck != null && levelParameterCheck.Definition.Name == paramName)
                                {
                                    param = levelParameterCheck;
                                }
                                // Phase Created parameter
                                else if (element.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Definition.Name == paramName)
                                {
                                    param = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
                                }
                                // Phase Demolished parameter
                                else if (element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.Definition.Name == paramName)
                                {
                                    param = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                                }
                                else
                                {
                                    // Regular parameter - use LookupParameter
                                    param = element.LookupParameter(paramName);
                                }

                                if (param == null)
                                {
                                    errors.Add($"Element {element.Id}: Parameter '{paramName}' does not exist on element");
                                    continue;
                                }

                                // Check if parameter is read-only (like rooms does)
                                if (param.IsReadOnly)
                                {
                                    errors.Add($"Element {element.Id}: Parameter '{paramName}' is read-only");
                                    continue;
                                }

                                // Try to set the value
                                SetParameterValue(param, parametersToRestore[paramName]);
                                elementHadChanges = true;
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"Element {element.Id}: {paramName}: {ex.Message}");
                            }
                        }

                        // After restoring parameters, check if orientation needs to be restored (doors/elements only)
                        if (element is FamilyInstance familyInstance &&
                            (_entityType == "Door" || _entityType == "Element"))
                        {
                            try
                            {
                                bool orientationChanged = RestoreOrientation(familyInstance, allParameters, errors);
                                if (orientationChanged)
                                    elementHadChanges = true;
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"Element {element.Id}: Orientation restore failed: {ex.Message}");
                            }
                        }

                        if (elementHadChanges)
                            updatedCount++;
                    }

                    transaction.Commit();
                }

                // Show results
                string resultMessage = $"Successfully restored parameters for {updatedCount} {_entityType.ToLower()}(s).";
                if (skippedCount > 0)
                {
                    resultMessage += $"\n\n{skippedCount} {_entityType.ToLower()}(s) skipped (no matching snapshot or no trackID).";
                }
                if (errors.Any())
                {
                    resultMessage += $"\n\nSome parameters had errors:\n{string.Join("\n", errors.Take(5))}";
                    if (errors.Count > 5)
                        resultMessage += $"\n...and {errors.Count - 5} more";
                }

                MessageBox.Show(resultMessage, "Restore Complete", MessageBoxButton.OK, MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to restore parameters:\n\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SetParameterValue(Parameter param, object value)
        {
            if (value == null) return;

            // Special handling for ElementId parameters that store names as strings
            if (param.Definition is InternalDefinition internalDef)
            {
                // Level parameter - find Level by name and use ElementId
                if (internalDef.BuiltInParameter == BuiltInParameter.FAMILY_LEVEL_PARAM)
                {
                    string levelName = value.ToString();
                    var level = new FilteredElementCollector(_doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .FirstOrDefault(l => l.Name == levelName);

                    if (level != null)
                    {
                        param.Set(level.Id);
                    }
                    return;
                }

                // Phase Created parameter - find Phase by name and use ElementId
                if (internalDef.BuiltInParameter == BuiltInParameter.PHASE_CREATED)
                {
                    string phaseName = value.ToString();
                    if (!string.IsNullOrEmpty(phaseName))
                    {
                        var phase = new FilteredElementCollector(_doc)
                            .OfClass(typeof(Phase))
                            .Cast<Phase>()
                            .FirstOrDefault(p => p.Name == phaseName);

                        if (phase != null)
                        {
                            param.Set(phase.Id);
                        }
                    }
                    return;
                }

                // Phase Demolished parameter - find Phase by name and use ElementId
                if (internalDef.BuiltInParameter == BuiltInParameter.PHASE_DEMOLISHED)
                {
                    string phaseName = value.ToString();

                    // Check if the phase name represents "None" (which means no demolition phase)
                    // In different locales this might be "Aucune", "Aucun(e)", "None", etc.
                    if (string.IsNullOrEmpty(phaseName) ||
                        phaseName.Equals("None", StringComparison.OrdinalIgnoreCase) ||
                        phaseName.Equals("Aucune", StringComparison.OrdinalIgnoreCase) ||
                        phaseName.Equals("Aucun(e)", StringComparison.OrdinalIgnoreCase))
                    {
                        // Set to ElementId.InvalidElementId to represent "None"
                        param.Set(ElementId.InvalidElementId);
                    }
                    else
                    {
                        // Find the phase by name
                        var phase = new FilteredElementCollector(_doc)
                            .OfClass(typeof(Phase))
                            .Cast<Phase>()
                            .FirstOrDefault(p => p.Name == phaseName);

                        if (phase != null)
                        {
                            param.Set(phase.Id);
                        }
                        else
                        {
                            // Phase not found - this is an error condition
                            throw new Exception($"Phase '{phaseName}' not found in project");
                        }
                    }
                    return;
                }
            }

            // Extract value from ParameterValue object (professional architecture)
            object actualValue = value;
            if (value is ViewTracker.Models.ParameterValue paramValue)
            {
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

            // Set parameter based on storage type - clean, type-safe logic
            switch (param.StorageType)
            {
                case StorageType.String:
                    param.Set(actualValue?.ToString() ?? "");
                    break;

                case StorageType.Double:
                    if (actualValue is double doubleValue)
                    {
                        param.Set(doubleValue);
                    }
                    else
                    {
                        throw new Exception($"Expected Double value but got {actualValue?.GetType().Name}. Snapshot may be corrupted.");
                    }
                    break;

                case StorageType.Integer:
                    if (actualValue is int intValue)
                    {
                        param.Set(intValue);
                    }
                    else if (actualValue is long longValue)
                    {
                        // Handle JSON deserialization that might use long for integers
                        param.Set((int)longValue);
                    }
                    else
                    {
                        throw new Exception($"Expected Integer value but got {actualValue?.GetType().Name}. Snapshot may be corrupted.");
                    }
                    break;

                case StorageType.ElementId:
                    // ElementId parameters store display names as strings - skip restore
                    // These are auto-generated (like Level, Phase) and handled separately above
                    break;
            }
        }

        private void SelectAllElements_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _elementRestoreItems)
            {
                item.IsSelected = true;
            }
        }

        private void SelectNoneElements_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _elementRestoreItems)
            {
                item.IsSelected = false;
            }
        }

        private void PopulateElementList()
        {
            _elementRestoreItems.Clear();

            if (_selectedVersionSnapshots == null || !_currentElements.Any())
                return;

            // Build trackID to snapshot mapping
            var snapshotMap = new Dictionary<string, dynamic>();
            foreach (var snapshot in _selectedVersionSnapshots)
            {
                snapshotMap[snapshot.TrackId] = snapshot;
            }

            // Get selected parameters
            var selectedParams = GetSelectedParameters();

            // Create restore items for each element
            foreach (var element in _currentElements)
            {
                var trackIdParam = element.LookupParameter("trackID");
                if (trackIdParam == null || string.IsNullOrWhiteSpace(trackIdParam.AsString()))
                    continue;

                string trackId = trackIdParam.AsString();
                if (!snapshotMap.ContainsKey(trackId))
                    continue;

                dynamic snapshot = snapshotMap[trackId];

                // Get element info and current type
                string elementName = "";
                string elementInfo = "";
                string currentTypeName = "";
                string snapshotTypeName = "";

                if (_entityType == "Door")
                {
                    var doorSnapshot = (DoorSnapshot)snapshot;
                    var door = element as FamilyInstance;
                    elementName = $"{doorSnapshot.Mark ?? "No Mark"}";
                    currentTypeName = door?.Symbol?.Name ?? "Unknown";
                    snapshotTypeName = doorSnapshot.TypeName ?? "Unknown";

                    // Show type mismatch indicator if types differ
                    string typeInfo = currentTypeName == snapshotTypeName
                        ? $"{doorSnapshot.FamilyName}: {doorSnapshot.TypeName}"
                        : $"{doorSnapshot.FamilyName}: {currentTypeName} ⚠️ (was: {snapshotTypeName})";
                    elementInfo = $"{typeInfo} | Level: {doorSnapshot.Level}";
                }
                else // Element
                {
                    var elemSnapshot = (ElementSnapshot)snapshot;
                    var familyInstance = element as FamilyInstance;
                    elementName = $"{elemSnapshot.Mark ?? "No Mark"}";
                    currentTypeName = familyInstance?.Symbol?.Name ?? element.GetTypeId()?.ToString() ?? "Unknown";
                    snapshotTypeName = elemSnapshot.TypeName ?? "Unknown";

                    // Show type mismatch indicator if types differ
                    string typeInfo = currentTypeName == snapshotTypeName
                        ? $"{elemSnapshot.FamilyName}: {elemSnapshot.TypeName}"
                        : $"{elemSnapshot.FamilyName}: {currentTypeName} ⚠️ (was: {snapshotTypeName})";
                    elementInfo = $"{elemSnapshot.Category} | {typeInfo}";
                }

                // Get parameter preview - build unified dictionary from AllParameters JSON and dedicated columns
                var parameterPreview = new List<ElementParameterPreview>();
                var parametersAvailable = new Dictionary<string, object>();

                // First, add parameters from AllParameters JSON
                Dictionary<string, object> allParameters = null;
                try
                {
                    if (_entityType == "Door")
                    {
                        allParameters = ((DoorSnapshot)snapshot).AllParameters;
                    }
                    else
                    {
                        allParameters = ((ElementSnapshot)snapshot).AllParameters;
                    }

                    if (allParameters != null)
                    {
                        foreach (var kvp in allParameters)
                        {
                            parametersAvailable[kvp.Key] = kvp.Value;
                        }
                    }
                }
                catch { }

                // Then, add dedicated column parameters using BuiltInParameter to get localized names
                // Include even if empty (matches comparison logic)
                // Mark
                var markParam = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                if (markParam != null)
                    parametersAvailable[markParam.Definition.Name] = snapshot.Mark ?? "";

                // Level
                var levelParam = element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                if (levelParam != null)
                    parametersAvailable[levelParam.Definition.Name] = snapshot.Level ?? "";

                // Comments
                var commentsParam = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (commentsParam != null)
                    parametersAvailable[commentsParam.Definition.Name] = snapshot.Comments ?? "";

                // Phase Created
                var phaseCreatedParam = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
                if (phaseCreatedParam != null)
                    parametersAvailable[phaseCreatedParam.Definition.Name] = snapshot.PhaseCreated ?? "";

                // Phase Demolished
                var phaseDemolishedParam = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                if (phaseDemolishedParam != null)
                    parametersAvailable[phaseDemolishedParam.Definition.Name] = snapshot.PhaseDemolished ?? "";

                // Fire Rating (doors only)
                if (_entityType == "Door")
                {
                    var fireRatingParam = element.get_Parameter(BuiltInParameter.DOOR_FIRE_RATING);
                    if (fireRatingParam != null)
                        parametersAvailable[fireRatingParam.Definition.Name] = snapshot.FireRating ?? "";
                }

                // Filter out location/orientation parameters from preview too
                var nonRestorableParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "facing_x", "facing_y", "facing_z",
                    "hand_x", "hand_y", "hand_z",
                    "locationx", "locationy", "locationz",
                    "location_x", "location_y", "location_z",
                    "facingx", "facingy", "facingz",
                    "handx", "handy", "handz"
                };

                if (parametersAvailable.Any() && selectedParams.Any())
                {
                    foreach (var paramName in selectedParams)
                    {
                        // Skip non-restorable parameters
                        if (nonRestorableParams.Contains(paramName))
                            continue;

                        if (parametersAvailable.ContainsKey(paramName))
                        {
                            // Format the value properly for display
                            string displayValue = "(empty)";
                            var value = parametersAvailable[paramName];

                            if (value != null)
                            {
                                // For double values, convert from internal units (feet) to display units (mm, etc.)
                                if (value is double doubleVal)
                                {
                                    var param = element.LookupParameter(paramName);
                                    if (param != null && param.StorageType == StorageType.Double)
                                    {
                                        try
                                        {
                                            // Convert from internal units (feet) to display units (mm, etc.)
                                            var spec = param.Definition.GetDataType();
                                            var formatOptions = _doc.GetUnits().GetFormatOptions(spec);
                                            var displayUnitType = formatOptions.GetUnitTypeId();
                                            double convertedValue = UnitUtils.ConvertFromInternalUnits(doubleVal, displayUnitType);
                                            displayValue = convertedValue.ToString("0.##");
                                        }
                                        catch
                                        {
                                            // Fallback to simple ToString if formatting fails
                                            displayValue = doubleVal.ToString("F2");
                                        }
                                    }
                                    else
                                    {
                                        displayValue = doubleVal.ToString("F2");
                                    }
                                }
                                else
                                {
                                    displayValue = value.ToString();
                                }
                            }

                            parameterPreview.Add(new ElementParameterPreview
                            {
                                Name = paramName,
                                Value = displayValue
                            });
                        }
                    }
                }

                // Check for type parameter differences
                var typeParameterWarnings = new List<string>();
                Dictionary<string, object> snapshotTypeParams = null;

                try
                {
                    if (_entityType == "Door")
                    {
                        snapshotTypeParams = ((DoorSnapshot)snapshot).TypeParameters;
                    }
                    else
                    {
                        snapshotTypeParams = ((ElementSnapshot)snapshot).TypeParameters;
                    }
                }
                catch { }

                if (snapshotTypeParams != null && snapshotTypeParams.Any())
                {
                    // Get current type parameters
                    var currentTypeParams = new Dictionary<string, object>();
                    var familyInstance = element as FamilyInstance;
                    if (familyInstance?.Symbol != null)
                    {
                        foreach (Parameter param in familyInstance.Symbol.GetOrderedParameters())
                        {
                            if (param == null || param.Definition == null)
                                continue;

                            string paramName = param.Definition.Name;

                            // Skip IFC parameters
                            if (paramName.Contains("IFC", StringComparison.OrdinalIgnoreCase))
                                continue;

                            // Get current value
                            object currentValue = null;
                            switch (param.StorageType)
                            {
                                case StorageType.Double:
                                    currentValue = param.AsDouble();
                                    break;
                                case StorageType.Integer:
                                    currentValue = param.AsInteger();
                                    break;
                                case StorageType.String:
                                    currentValue = param.AsString() ?? "";
                                    break;
                                case StorageType.ElementId:
                                    currentValue = param.AsValueString() ?? "";
                                    break;
                            }

                            if (currentValue != null)
                            {
                                currentTypeParams[paramName] = currentValue;
                            }
                        }
                    }

                    // Compare type parameters
                    foreach (var kvp in snapshotTypeParams)
                    {
                        if (currentTypeParams.TryGetValue(kvp.Key, out var currentValue))
                        {
                            // Extract snapshot value from ParameterValue object
                            object snapshotValue = null;
                            try
                            {
                                if (kvp.Value is Models.ParameterValue pv)
                                {
                                    snapshotValue = pv.RawValue;
                                }
                                else if (kvp.Value is Newtonsoft.Json.Linq.JObject jObj)
                                {
                                    var paramVal = Models.ParameterValue.FromJsonObject(jObj);
                                    if (paramVal != null)
                                        snapshotValue = paramVal.RawValue;
                                }
                                else
                                {
                                    snapshotValue = kvp.Value;
                                }
                            }
                            catch
                            {
                                snapshotValue = kvp.Value;
                            }

                            if (snapshotValue == null)
                                continue;

                            // Compare values
                            bool isDifferent = false;
                            if (snapshotValue is double snapDouble && currentValue is double currDouble)
                            {
                                isDifferent = Math.Abs(snapDouble - currDouble) > 0.0001;
                            }
                            else if (snapshotValue is long snapLong && currentValue is int currInt)
                            {
                                // JSON deserializes integers as long, compare as long
                                isDifferent = snapLong != currInt;
                            }
                            else if (snapshotValue is int snapInt && currentValue is int currInt2)
                            {
                                isDifferent = snapInt != currInt2;
                            }
                            else
                            {
                                // Handle ElementId special case: -1 (invalid) equals empty string
                                string snapStr = snapshotValue?.ToString() ?? "";
                                string currStr = currentValue?.ToString() ?? "";

                                // Normalize: -1 and empty string both mean "no value"
                                if ((snapStr == "-1" || snapStr == "") && (currStr == "-1" || currStr == ""))
                                {
                                    isDifferent = false;
                                }
                                else
                                {
                                    isDifferent = !Equals(snapStr, currStr);
                                }
                            }

                            if (isDifferent)
                            {
                                typeParameterWarnings.Add($"  {kvp.Key}: '{snapshotValue}' → '{currentValue}'");
                            }
                        }
                    }
                }

                // Check for orientation differences (doors/elements only)
                bool needsOrientationRestore = false;
                if ((_entityType == "Door" || _entityType == "Element") && element is FamilyInstance familyInst)
                {
                    try
                    {
                        // Get snapshot orientation
                        XYZ snapshotFacing = GetOrientationFromSnapshot(allParameters, "facing");
                        XYZ snapshotHand = GetOrientationFromSnapshot(allParameters, "hand");

                        if (snapshotFacing != null && snapshotHand != null &&
                            familyInst.FacingOrientation != null && familyInst.HandOrientation != null)
                        {
                            double facingDot = familyInst.FacingOrientation.DotProduct(snapshotFacing);
                            double handDot = familyInst.HandOrientation.DotProduct(snapshotHand);

                            needsOrientationRestore = facingDot < 0.0 || handDot < 0.0;

                            // Add orientation info to preview if no parameters selected
                            if (!selectedParams.Any() && needsOrientationRestore)
                            {
                                if (facingDot < 0.0)
                                {
                                    parameterPreview.Add(new ElementParameterPreview
                                    {
                                        Name = "⚠️ Facing orientation",
                                        Value = "Needs flip"
                                    });
                                }
                                if (handDot < 0.0)
                                {
                                    parameterPreview.Add(new ElementParameterPreview
                                    {
                                        Name = "⚠️ Hand orientation",
                                        Value = "Needs flip"
                                    });
                                }
                            }
                        }
                    }
                    catch { }
                }

                var restoreItem = new ElementRestoreItem
                {
                    Element = element,
                    TrackId = trackId,
                    ElementDisplayName = elementName,
                    ElementInfo = elementInfo,
                    ParameterPreview = parameterPreview,
                    IsSelected = true,
                    CurrentTypeName = currentTypeName,
                    SnapshotTypeName = snapshotTypeName,
                    HasTypeMismatch = currentTypeName != snapshotTypeName,
                    TypeParameterWarnings = typeParameterWarnings
                };

                _elementRestoreItems.Add(restoreItem);
            }

            ElementsItemsControl.ItemsSource = _elementRestoreItems;
        }

        private Dictionary<string, object> GetAllParametersForBackup(Element element)
        {
            var result = new Dictionary<string, object>();

            // Parameters that are stored in dedicated columns - exclude from AllParameters JSON
            var excludedBuiltInParams = new HashSet<BuiltInParameter>
            {
                BuiltInParameter.ALL_MODEL_MARK,
                BuiltInParameter.FAMILY_LEVEL_PARAM,
                BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
                BuiltInParameter.PHASE_CREATED,
                BuiltInParameter.PHASE_DEMOLISHED,
                BuiltInParameter.DOOR_FIRE_RATING  // For doors only
            };

            foreach (Parameter param in element.GetOrderedParameters())
            {
                if (param == null || param.Definition == null)
                    continue;

                string paramName = param.Definition.Name;

                // Skip parameters that are in dedicated columns
                if (param.Definition is InternalDefinition internalDef &&
                    excludedBuiltInParams.Contains(internalDef.BuiltInParameter))
                    continue;

                // Skip IFC parameters
                if (paramName.ToLower().Contains("ifc"))
                    continue;

                // Skip TYPE parameters (only capture instance parameters)
                if (param.Element is ElementType)
                    continue;

                // Capture parameter value
                object paramValue = null;
                bool shouldAdd = false;

                switch (param.StorageType)
                {
                    case StorageType.Double:
                        paramValue = param.AsDouble();
                        shouldAdd = true;
                        break;
                    case StorageType.Integer:
                        paramValue = param.AsInteger();
                        shouldAdd = true;
                        break;
                    case StorageType.String:
                        // Save ALL string parameters, even empty ones
                        var stringValue = param.AsString();
                        paramValue = stringValue ?? "";
                        shouldAdd = true;
                        break;
                    case StorageType.ElementId:
                        var valueString = param.AsValueString();
                        if (!string.IsNullOrEmpty(valueString))
                        {
                            paramValue = valueString;
                            shouldAdd = true;
                        }
                        break;
                }

                if (shouldAdd)
                {
                    result[paramName] = paramValue;
                }
            }

            // For doors, add location/rotation/facing/hand information
            if (element is FamilyInstance door && _entityType == "Door")
            {
                try
                {
                    var location = door.Location as LocationPoint;
                    if (location != null)
                    {
                        var point = location.Point;
                        result["location_x"] = point.X;
                        result["location_y"] = point.Y;
                        result["location_z"] = point.Z;
                        result["rotation"] = location.Rotation * (180.0 / Math.PI); // Convert to degrees

                        var facingOrientation = door.FacingOrientation;
                        result["facing_x"] = facingOrientation.X;
                        result["facing_y"] = facingOrientation.Y;
                        result["facing_z"] = facingOrientation.Z;

                        var handOrientation = door.HandOrientation;
                        result["hand_x"] = handOrientation.X;
                        result["hand_y"] = handOrientation.Y;
                        result["hand_z"] = handOrientation.Z;
                    }
                }
                catch { }
            }

            return result;
        }

        /// <summary>
        /// Restores orientation (facing and hand) for doors and elements by comparing snapshot orientation with current orientation
        /// Returns true if any flips were performed
        /// </summary>
        private bool RestoreOrientation(FamilyInstance element, Dictionary<string, object> snapshotParameters, List<string> errors)
        {
            if (snapshotParameters == null)
            {
                System.Diagnostics.Debug.WriteLine($"RestoreOrientation: snapshotParameters is null for element {element.Id}");
                return false;
            }

            if (element.FacingOrientation == null || element.HandOrientation == null)
            {
                System.Diagnostics.Debug.WriteLine($"RestoreOrientation: Element {element.Id} has null orientation");
                return false;
            }

            bool orientationChanged = false;

            try
            {
                // Extract snapshot orientation vectors from AllParameters JSON
                XYZ snapshotFacing = GetOrientationFromSnapshot(snapshotParameters, "facing");
                XYZ snapshotHand = GetOrientationFromSnapshot(snapshotParameters, "hand");

                if (snapshotFacing == null || snapshotHand == null)
                {
                    System.Diagnostics.Debug.WriteLine($"RestoreOrientation: Could not extract orientation from snapshot for element {element.Id}");
                    System.Diagnostics.Debug.WriteLine($"  snapshotFacing: {snapshotFacing}, snapshotHand: {snapshotHand}");
                    System.Diagnostics.Debug.WriteLine($"  Available keys: {string.Join(", ", snapshotParameters.Keys)}");
                    return false; // No orientation data in snapshot
                }

                // Get current orientation
                XYZ currentFacing = element.FacingOrientation;
                XYZ currentHand = element.HandOrientation;

                // Check if facing orientation needs to be flipped
                // Use dot product to determine if orientations are opposite (dot product near -1) or same (near 1)
                double facingDot = currentFacing.DotProduct(snapshotFacing);
                bool needsFacingFlip = facingDot < 0.0; // If dot product is negative, vectors point in opposite directions

                // Check if hand orientation needs to be flipped
                double handDot = currentHand.DotProduct(snapshotHand);
                bool needsHandFlip = handDot < 0.0;

                System.Diagnostics.Debug.WriteLine($"RestoreOrientation: Element {element.Id}");
                System.Diagnostics.Debug.WriteLine($"  Facing dot: {facingDot:F3}, needs flip: {needsFacingFlip}");
                System.Diagnostics.Debug.WriteLine($"  Hand dot: {handDot:F3}, needs flip: {needsHandFlip}");

                // Perform flips if needed
                if (needsFacingFlip)
                {
                    System.Diagnostics.Debug.WriteLine($"  Flipping facing for element {element.Id}");
                    element.flipFacing();
                    orientationChanged = true;
                }

                if (needsHandFlip)
                {
                    System.Diagnostics.Debug.WriteLine($"  Flipping hand for element {element.Id}");
                    element.flipHand();
                    orientationChanged = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RestoreOrientation: Exception for element {element.Id}: {ex.Message}");
                errors.Add($"Element {element.Id}: Failed to restore orientation: {ex.Message}");
            }

            return orientationChanged;
        }

        /// <summary>
        /// Extracts orientation vector (facing or hand) from snapshot AllParameters JSON
        /// </summary>
        private XYZ GetOrientationFromSnapshot(Dictionary<string, object> snapshotParameters, string prefix)
        {
            try
            {
                string xKey = $"{prefix}_x";
                string yKey = $"{prefix}_y";
                string zKey = $"{prefix}_z";

                if (!snapshotParameters.ContainsKey(xKey) ||
                    !snapshotParameters.ContainsKey(yKey) ||
                    !snapshotParameters.ContainsKey(zKey))
                {
                    return null;
                }

                // Extract values from ParameterValue objects
                double x = GetDoubleFromParameterValue(snapshotParameters[xKey]);
                double y = GetDoubleFromParameterValue(snapshotParameters[yKey]);
                double z = GetDoubleFromParameterValue(snapshotParameters[zKey]);

                return new XYZ(x, y, z);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extracts double value from ParameterValue object or JSON
        /// </summary>
        private double GetDoubleFromParameterValue(object paramValue)
        {
            if (paramValue == null)
                return 0.0;

            // Try direct cast first (if it's already a ParameterValue object)
            if (paramValue is Models.ParameterValue pv)
            {
                return Convert.ToDouble(pv.RawValue);
            }

            // Try JObject (if it's JSON)
            if (paramValue is Newtonsoft.Json.Linq.JObject jObj)
            {
                var pValue = Models.ParameterValue.FromJsonObject(jObj);
                if (pValue != null)
                {
                    return Convert.ToDouble(pValue.RawValue);
                }
            }

            // Fallback: try direct conversion
            return Convert.ToDouble(paramValue);
        }
    }

    // Data model for element restore items
    public class ElementRestoreItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private List<ElementParameterPreview> _parameterPreview = new List<ElementParameterPreview>();

        public Element Element { get; set; }
        public string TrackId { get; set; }
        public string ElementDisplayName { get; set; }
        public string ElementInfo { get; set; }

        public List<ElementParameterPreview> ParameterPreview
        {
            get => _parameterPreview;
            set
            {
                _parameterPreview = value;
                OnPropertyChanged(nameof(ParameterPreview));
                OnPropertyChanged(nameof(HasParametersVisibility));
                OnPropertyChanged(nameof(HasNoParameters));
            }
        }

        public string CurrentTypeName { get; set; }
        public string SnapshotTypeName { get; set; }
        public bool HasTypeMismatch { get; set; }
        public List<string> TypeParameterWarnings { get; set; } = new List<string>();

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }

        public string HasParametersVisibility => ParameterPreview.Any() ? "Visible" : "Collapsed";
        public string HasNoParameters => !ParameterPreview.Any() ? "Visible" : "Collapsed";
        public string TypeMismatchVisibility => HasTypeMismatch ? "Visible" : "Collapsed";
        public string TypeMismatchText => $"⚠️ Type changed: {SnapshotTypeName} → {CurrentTypeName}";
        public string TypeParameterWarningsVisibility => TypeParameterWarnings.Any() ? "Visible" : "Collapsed";
        public string TypeParameterWarningsText => TypeParameterWarnings.Any()
            ? $"⚠️ Type parameter differences (will NOT be restored):\n{string.Join("\n", TypeParameterWarnings)}"
            : "";

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ElementParameterPreview
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }
}
