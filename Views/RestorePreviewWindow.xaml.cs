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
                    changes.Add(new PreviewChange
                    {
                        RoomIdentifier = snapshot.RoomNumber ?? snapshot.TrackId,
                        ParameterName = "Room",
                        CurrentValue = "(deleted)",
                        SnapshotValue = $"{snapshot.RoomNumber} - {snapshot.RoomName}",
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

                switch (paramName)
                {
                case "RoomNumber":
                    currentValue = room.Number;
                    snapshotValue = snapshot.RoomNumber;
                    displayParamName = "Room Number";
                    break;

                case "RoomName":
                    currentValue = room.get_Parameter(Autodesk.Revit.DB.BuiltInParameter.ROOM_NAME)?.AsString();
                    snapshotValue = snapshot.RoomName;
                    displayParamName = "Room Name";
                    break;

                case "Department":
                    currentValue = room.LookupParameter("Department")?.AsString() ?? room.LookupParameter("Service")?.AsString();
                    snapshotValue = snapshot.Department;
                    displayParamName = "Department";
                    break;

                case "Occupancy":
                    currentValue = room.LookupParameter("Occupancy")?.AsString() ?? room.LookupParameter("Occupation")?.AsString();
                    snapshotValue = snapshot.Occupancy;
                    displayParamName = "Occupancy";
                    break;

                case "BaseFinish":
                    currentValue = room.LookupParameter("Base Finish")?.AsString() ?? room.LookupParameter("Finition de la base")?.AsString();
                    snapshotValue = snapshot.BaseFinish;
                    displayParamName = "Base Finish";
                    break;

                case "CeilingFinish":
                    currentValue = room.LookupParameter("Ceiling Finish")?.AsString() ?? room.LookupParameter("Finition du plafond")?.AsString();
                    snapshotValue = snapshot.CeilingFinish;
                    displayParamName = "Ceiling Finish";
                    break;

                case "WallFinish":
                    currentValue = room.LookupParameter("Wall Finish")?.AsString() ?? room.LookupParameter("Finition du mur")?.AsString();
                    snapshotValue = snapshot.WallFinish;
                    displayParamName = "Wall Finish";
                    break;

                case "FloorFinish":
                    currentValue = room.LookupParameter("Floor Finish")?.AsString() ?? room.LookupParameter("Finition du sol")?.AsString();
                    snapshotValue = snapshot.FloorFinish;
                    displayParamName = "Floor Finish";
                    break;

                case "Comments":
                    currentValue = room.get_Parameter(Autodesk.Revit.DB.BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                    snapshotValue = snapshot.Comments;
                    displayParamName = "Comments";
                    break;

                case "Occupant":
                    currentValue = room.LookupParameter("Occupant")?.AsString();
                    snapshotValue = snapshot.Occupant;
                    displayParamName = "Occupant";
                    break;

                default:
                    // Check if this is a parameter from AllParameters JSON
                    if (paramName.StartsWith("AllParam_"))
                    {
                        string actualParamName = paramName.Substring("AllParam_".Length);
                        displayParamName = actualParamName;

                        var param = room.LookupParameter(actualParamName);
                        if (param != null)
                        {
                            currentValue = param.AsValueString() ?? param.AsString() ?? "";
                        }

                        if (snapshot.AllParameters != null && snapshot.AllParameters.TryGetValue(actualParamName, out object value))
                        {
                            snapshotValue = value?.ToString() ?? "";
                        }
                    }
                    else
                    {
                        return null;
                    }
                    break;
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
