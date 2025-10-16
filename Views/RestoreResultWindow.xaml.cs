using System.Windows;

namespace ViewTracker.Views
{
    public partial class RestoreResultWindow : Window
    {
        public RestoreResultWindow(RestoreResult result, string versionName)
        {
            InitializeComponent();

            VersionText.Text = $"Successfully restored from snapshot: {versionName}";

            UpdatedRoomsText.Text = $"✅ {result.UpdatedRooms} room(s) updated";

            if (result.CreatedRooms > 0)
            {
                CreatedRoomsText.Text = $"✅ {result.CreatedRooms} unplaced room(s) created";
                UnplacedRoomsGroup.Visibility = System.Windows.Visibility.Visible;
                UnplacedRoomsGrid.ItemsSource = result.UnplacedRoomInfo;
            }
            else
            {
                CreatedRoomsText.Text = "✅ No deleted rooms";
            }

            BackupText.Text = "✅ Backup snapshot created: " + $"Backup_Before_Restore_{System.DateTime.Now:yyyyMMdd}";
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
