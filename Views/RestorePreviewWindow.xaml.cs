using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ViewTracker.Views
{
    public partial class RestorePreviewWindow : Window
    {
        private List<PreviewChange> _allChanges;

        public RestorePreviewWindow(
            Document doc,
            List<Room> currentRooms,
            List<RoomSnapshot> snapshots,
            List<string> selectedParams,
            string versionName)
        {
            InitializeComponent();

            try
            {
                if (VersionText != null)
                    VersionText.Text = $"Version: {versionName} | Parameters: {selectedParams.Count} selected";

                // Build preview changes
                _allChanges = BuildPreviewChanges(doc, currentRooms, snapshots, selectedParams);

                // Apply initial filter
                if (_allChanges != null)
                    ApplyFilter();

                // Update summary
                UpdateSummary();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error building preview: {ex.Message}", "Preview Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private List<PreviewChange> BuildPreviewChanges(
            Document doc,
            List<Room> currentRooms,
            List<RoomSnapshot> snapshots,
            List<string> selectedParams)
        {
            var changes = new List<PreviewChange>();
            var currentRoomDict = currentRooms.ToDictionary(r => r.LookupParameter("trackID").AsString());

            foreach (var snapshot in snapshots)
            {
                if (currentRoomDict.TryGetValue(snapshot.TrackId, out Room room))
                {
                    // Room exists - compare parameters
                    foreach (var paramName in selectedParams)
                    {
                        var change = CreatePreviewChange(room, snapshot, paramName);
                        if (change != null)
                            changes.Add(change);
                    }
                }
                else
                {
                    // Room deleted - will be created
                    // REFACTORED: Get room name from AllParameters JSON
                    string roomName = "";
                    if (snapshot.AllParameters != null && snapshot.AllParameters.TryGetValue("Nom", out object nameValue))
                    {
                        var paramVal = Models.ParameterValue.FromJsonObject(nameValue);
                        roomName = paramVal?.DisplayValue ?? "";
                    }

                    changes.Add(new PreviewChange
                    {
                        RoomIdentifier = snapshot.RoomNumber ?? snapshot.TrackId,
                        ParameterName = "Room",
                        CurrentValue = "(deleted)",
                        SnapshotValue = $"{snapshot.RoomNumber} - {roomName}",
                        ChangeStatus = "New Room",
                        IsChanged = true
                    });
                }
            }

            return changes;
        }

        private PreviewChange CreatePreviewChange(Room room, RoomSnapshot snapshot, string paramName)
        {
            try
            {
                string currentValue = "";
                string snapshotValue = "";
                string displayParamName = "";

                // Skip read-only parameters that can't be restored
                // (Area, Perimeter, Volume, Level are placement-dependent)
                var readOnlyParams = new HashSet<string>
                {
                    "Area", "Surface",
                    "Perimeter", "Périmètre",
                    "Volume",
                    "Level", "Niveau",
                    "Unbounded Height", "Hauteur non liée",
                    "Upper Limit", "Limite supérieure",
                    "Limit Offset", "Décalage limite",
                    "Base Offset", "Décalage inférieur",
                    "Computation Height", "Hauteur de calcul"
                };

                // REFACTORED: All parameters now come from AllParameters JSON except RoomNumber
                if (paramName == "RoomNumber")
                {
                    currentValue = room.Number;
                    snapshotValue = snapshot.RoomNumber;
                    displayParamName = "Room Number";
                }
                else
                {
                    // Skip if this is a read-only parameter
                    if (readOnlyParams.Contains(paramName))
                        return null;

                    displayParamName = paramName;

                    // Get current value from room
                    var param = room.LookupParameter(paramName);
                    if (param != null)
                    {
                        var currentParamValue = Models.ParameterValue.FromRevitParameter(param);
                        currentValue = currentParamValue?.DisplayValue ?? "";
                    }

                    // Get snapshot value from AllParameters JSON
                    if (snapshot.AllParameters != null && snapshot.AllParameters.TryGetValue(paramName, out object value))
                    {
                        var snapshotParamValue = Models.ParameterValue.FromJsonObject(value);
                        snapshotValue = snapshotParamValue?.DisplayValue ?? "";
                    }
                }

                bool isChanged = (currentValue ?? "") != (snapshotValue ?? "");

                return new PreviewChange
                {
                    RoomIdentifier = room.Number,
                    ParameterName = displayParamName,
                    CurrentValue = currentValue ?? "(empty)",
                    SnapshotValue = snapshotValue ?? "(empty)",
                    ChangeStatus = isChanged ? "Changed" : "Unchanged",
                    IsChanged = isChanged
                };
            }
            catch (System.Exception)
            {
                // If any error occurs, skip this parameter
                return null;
            }
        }

        private void ApplyFilter()
        {
            if (_allChanges == null || ChangesGrid == null)
                return;

            IEnumerable<PreviewChange> filtered = _allChanges;

            if (ModifiedOnlyRadio?.IsChecked == true)
            {
                filtered = _allChanges.Where(c => c.IsChanged);
            }
            else if (UnchangedOnlyRadio?.IsChecked == true)
            {
                filtered = _allChanges.Where(c => !c.IsChanged);
            }

            ChangesGrid.ItemsSource = filtered.ToList();
        }

        private void UpdateSummary()
        {
            if (_allChanges == null || SummaryText == null)
                return;

            var changedCount = _allChanges.Count(c => c.IsChanged && c.ChangeStatus != "New Room");
            var unchangedCount = _allChanges.Count(c => !c.IsChanged);
            var newRoomsCount = _allChanges.Count(c => c.ChangeStatus == "New Room");

            var totalRooms = _allChanges.Select(c => c.RoomIdentifier).Distinct().Count();

            SummaryText.Text = $"• {totalRooms} room(s) will be affected\n" +
                               $"• {changedCount} parameter(s) will change\n" +
                               $"• {unchangedCount} parameter(s) unchanged\n" +
                               (newRoomsCount > 0 ? $"• {newRoomsCount} unplaced room(s) will be created" : "");
        }

        private void FilterRadio_Changed(object sender, RoutedEventArgs e)
        {
            ApplyFilter();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Formats a snapshot's raw parameter value to match the display format of the current parameter
        /// Uses the same logic as RoomCompareCommand.FormatParameterValue
        /// </summary>
        private string FormatSnapshotValue(Parameter currentParam, object snapshotValue)
        {
            if (snapshotValue == null)
                return "";

            // If parameter doesn't exist in current room, just return the raw value as string
            if (currentParam == null)
                return snapshotValue.ToString();

            try
            {
                switch (currentParam.StorageType)
                {
                    case StorageType.Double:
                        // Convert value to double (handles int, long, double from JSON)
                        double doubleValue = 0;

                        if (snapshotValue is double d)
                            doubleValue = d;
                        else if (snapshotValue is float f)
                            doubleValue = f;
                        else if (snapshotValue is int i)
                            doubleValue = i;
                        else if (snapshotValue is long l)
                            doubleValue = l;
                        else if (double.TryParse(snapshotValue.ToString(), out double parsed))
                            doubleValue = parsed;
                        else
                            return snapshotValue.ToString();

                        // Get the conversion factor from the current parameter
                        // by comparing its raw value (internal units) to display value (file units)
                        var currentRawValue = currentParam.AsDouble();
                        var currentDisplayString = currentParam.AsValueString();

                        if (!string.IsNullOrEmpty(currentDisplayString) && Math.Abs(currentRawValue) > 0.0001)
                        {
                            // Parse the numeric part from the display string
                            // Handle formats like "32.8", "32.8 m", "32,8", etc.
                            string numericPart = currentDisplayString.Split(' ')[0].Replace(",", ".");

                            if (double.TryParse(numericPart, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out double currentDisplayValue))
                            {
                                // Calculate conversion factor (display units / internal units)
                                double conversionFactor = currentDisplayValue / currentRawValue;

                                // Apply the same conversion to the snapshot value
                                double convertedValue = doubleValue * conversionFactor;

                                // Format with appropriate precision (remove trailing zeros and decimal point if integer)
                                string formatted = convertedValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                                // Remove trailing zeros and decimal point if not needed
                                formatted = formatted.TrimEnd('0').TrimEnd('.');
                                return formatted;
                            }
                        }

                        // If we can't determine conversion, return as-is with precision
                        return doubleValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');

                    case StorageType.Integer:
                        // Integer parameters should be stored as display text in newer snapshots (e.g., "Par type")
                        // but older snapshots might have numeric values. Handle both cases:

                        // If snapshot value is a number, convert it to display text
                        if (int.TryParse(snapshotValue.ToString(), out int intVal))
                        {
                            // Get the display text for this integer value
                            // We can't directly convert without setting the parameter, so return formatted value
                            return intVal.ToString();
                        }

                        // Otherwise it's already text - extract first word to match current value format
                        // (e.g., "Par type" → "Par" to match how current value is extracted)
                        return snapshotValue.ToString().Split(' ')[0];

                    case StorageType.String:
                        return snapshotValue.ToString();

                    case StorageType.ElementId:
                        return snapshotValue.ToString();
                }
            }
            catch
            {
                // If formatting fails, return raw value
                return snapshotValue.ToString();
            }

            return snapshotValue.ToString();
        }
    }

    public class PreviewChange
    {
        public string RoomIdentifier { get; set; }
        public string ParameterName { get; set; }
        public string CurrentValue { get; set; }
        public string SnapshotValue { get; set; }
        public string ChangeStatus { get; set; }
        public bool IsChanged { get; set; }
    }
}
