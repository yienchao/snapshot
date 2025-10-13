using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace ViewTracker.Views
{
    public partial class VersionSelectionWindow : Window
    {
        public VersionSelectionViewModel ViewModel { get; set; }

        public VersionSelectionWindow(List<VersionInfo> versions)
        {
            InitializeComponent();
            ViewModel = new VersionSelectionViewModel(versions);
            DataContext = ViewModel;

            // Auto-select first version
            if (ViewModel.Versions.Any())
            {
                ViewModel.SelectedVersion = ViewModel.Versions.First();
            }
        }

        public string SelectedVersionName => ViewModel.SelectedVersion?.VersionName;

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedVersion == null)
            {
                MessageBox.Show("Please select a version.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    public class VersionSelectionViewModel : INotifyPropertyChanged
    {
        private VersionInfo _selectedVersion;

        public List<VersionInfo> Versions { get; set; }

        public VersionInfo SelectedVersion
        {
            get => _selectedVersion;
            set
            {
                _selectedVersion = value;
                OnPropertyChanged(nameof(SelectedVersion));
            }
        }

        public VersionSelectionViewModel(List<VersionInfo> versions)
        {
            Versions = versions;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class VersionInfo
    {
        public string VersionName { get; set; }
        public string DisplayName => $"{VersionName}";
        public DateTime? SnapshotDate { get; set; }
        public string CreatedBy { get; set; }
        public bool IsOfficial { get; set; }
    }
}
