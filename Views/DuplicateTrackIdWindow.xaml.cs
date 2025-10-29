using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ViewTracker.Services;

namespace ViewTracker.Views
{
    public partial class DuplicateTrackIdWindow : Window
    {
        public List<DuplicateTrackIdGroup> DuplicateGroups { get; private set; }
        public bool UserApproved { get; private set; }

        public DuplicateTrackIdWindow(List<DuplicateTrackIdGroup> duplicateGroups)
        {
            InitializeComponent();
            DuplicateGroups = duplicateGroups;
            UserApproved = false;

            // Set summary
            int totalDuplicates = duplicateGroups.Sum(g => g.Rooms.Count);
            int totalGroups = duplicateGroups.Count;
            SummaryText.Text = $"Found {totalDuplicates} rooms in {totalGroups} duplicate groups";

            // Bind data
            DuplicateGroupsList.ItemsSource = duplicateGroups;
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            UserApproved = true;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            UserApproved = false;
            DialogResult = false;
            Close();
        }
    }
}
