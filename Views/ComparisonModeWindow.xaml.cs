using System.Windows;

namespace ViewTracker.Views
{
    public partial class ComparisonModeWindow : Window
    {
        public ComparisonMode SelectedMode { get; private set; }

        public ComparisonModeWindow()
        {
            InitializeComponent();
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentVsSnapshotRadio.IsChecked == true)
            {
                SelectedMode = ComparisonMode.CurrentVsSnapshot;
            }
            else if (SnapshotVsSnapshotRadio.IsChecked == true)
            {
                SelectedMode = ComparisonMode.SnapshotVsSnapshot;
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    public enum ComparisonMode
    {
        CurrentVsSnapshot,
        SnapshotVsSnapshot
    }
}
