using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OfficeOpenXml;

namespace ViewTracker.Views
{
    public partial class ComparisonResultWindow : Window
    {
        private ComparisonResultViewModel _viewModel;

        public ComparisonResultWindow(ComparisonResultViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_viewModel == null) return;
            _viewModel.FilterText = SearchBox.Text;
        }

        private void ChangeTypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_viewModel == null || ChangeTypeFilter.SelectedItem == null) return;

            if (ChangeTypeFilter.SelectedItem is ComboBoxItem item)
            {
                _viewModel.SelectedFilter = item.Content.ToString();
            }
        }

        private void ExportCSV_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var filename = $"RoomComparison_{_viewModel.VersionName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                var filepath = Path.Combine(desktop, filename);

                using (var writer = new StreamWriter(filepath, false, Encoding.UTF8))
                {
                    // Header
                    writer.WriteLine("Change Type,Track ID,Room Number,Room Name,Parameter Name,Old Value,New Value");

                    // Write all changes with detailed breakdown
                    foreach (var room in _viewModel.AllResults)
                    {
                        if (room.ChangeType == "New")
                        {
                            writer.WriteLine($"New,{room.TrackId},{room.RoomNumber},\"{room.RoomName}\",,,");
                        }
                        else if (room.ChangeType == "Deleted")
                        {
                            writer.WriteLine($"Deleted,{room.TrackId},{room.RoomNumber},\"{room.RoomName}\",,,");
                        }
                        else if (room.ChangeType == "Modified")
                        {
                            if (room.Changes.Any())
                            {
                                foreach (var change in room.Changes)
                                {
                                    // Parse "ParamName: 'oldValue' → 'newValue'"
                                    var parts = ParseChange(change);
                                    writer.WriteLine($"Modified,{room.TrackId},{room.RoomNumber},\"{room.RoomName}\",\"{parts.ParamName}\",\"{parts.OldValue}\",\"{parts.NewValue}\"");
                                }
                            }
                            else
                            {
                                writer.WriteLine($"Modified,{room.TrackId},{room.RoomNumber},\"{room.RoomName}\",,,");
                            }
                        }
                    }
                }

                MessageBox.Show($"Comparison exported to:\n{filepath}", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filepath}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export CSV:\n{ex.Message}", "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private (string ParamName, string OldValue, string NewValue) ParseChange(string change)
        {
            // Parse "ParamName: 'oldValue' → 'newValue'"
            try
            {
                var colonIndex = change.IndexOf(':');
                var arrowIndex = change.IndexOf('→');

                if (colonIndex > 0 && arrowIndex > colonIndex)
                {
                    var paramName = change.Substring(0, colonIndex).Trim();
                    var oldPart = change.Substring(colonIndex + 1, arrowIndex - colonIndex - 1).Trim().Trim('\'');
                    var newPart = change.Substring(arrowIndex + 1).Trim().Trim('\'');
                    return (paramName, oldPart, newPart);
                }
            }
            catch { }

            return (change, "", "");
        }

        private void CopyToClipboard_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Room Comparison Results - {_viewModel.VersionName}");
                sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();
                sb.AppendLine($"New Rooms: {_viewModel.NewRoomsCount}");
                sb.AppendLine($"Modified Rooms: {_viewModel.ModifiedRoomsCount}");
                sb.AppendLine($"Deleted Rooms: {_viewModel.DeletedRoomsCount}");
                sb.AppendLine();
                sb.AppendLine("Details:");
                sb.AppendLine("========");

                foreach (var room in _viewModel.FilteredResults)
                {
                    sb.AppendLine($"\n[{room.ChangeType}] {room.TrackId} - {room.RoomNumber} - {room.RoomName}");
                    if (room.Changes.Any())
                    {
                        foreach (var change in room.Changes)
                        {
                            sb.AppendLine($"  • {change}");
                        }
                    }
                }

                Clipboard.SetText(sb.ToString());
                MessageBox.Show("Results copied to clipboard!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy to clipboard:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportExcel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var saveDialog = new SaveFileDialog
                {
                    Filter = "Excel Files (*.xlsx)|*.xlsx",
                    DefaultExt = "xlsx",
                    FileName = $"RoomComparison_{_viewModel.VersionName}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
                };

                if (saveDialog.ShowDialog() != true)
                    return;

                ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;

                using (var package = new ExcelPackage())
                {
                    var worksheet = package.Workbook.Worksheets.Add("Comparison Results");

                    // Header
                    int col = 1;
                    worksheet.Cells[1, col++].Value = "Change Type";
                    worksheet.Cells[1, col++].Value = "Track ID";
                    worksheet.Cells[1, col++].Value = "Room Number";
                    worksheet.Cells[1, col++].Value = "Room Name";
                    worksheet.Cells[1, col++].Value = "Parameter Name";
                    worksheet.Cells[1, col++].Value = "Old Value";
                    worksheet.Cells[1, col++].Value = "New Value";

                    // Data
                    int row = 2;
                    foreach (var room in _viewModel.AllResults)
                    {
                        if (room.ChangeType == "New")
                        {
                            worksheet.Cells[row, 1].Value = "New";
                            worksheet.Cells[row, 2].Value = room.TrackId;
                            worksheet.Cells[row, 3].Value = room.RoomNumber;
                            worksheet.Cells[row, 4].Value = room.RoomName;
                            row++;
                        }
                        else if (room.ChangeType == "Deleted")
                        {
                            worksheet.Cells[row, 1].Value = "Deleted";
                            worksheet.Cells[row, 2].Value = room.TrackId;
                            worksheet.Cells[row, 3].Value = room.RoomNumber;
                            worksheet.Cells[row, 4].Value = room.RoomName;
                            row++;
                        }
                        else if (room.ChangeType == "Modified")
                        {
                            if (room.Changes.Any())
                            {
                                foreach (var change in room.Changes)
                                {
                                    var parts = ParseChange(change);
                                    worksheet.Cells[row, 1].Value = "Modified";
                                    worksheet.Cells[row, 2].Value = room.TrackId;
                                    worksheet.Cells[row, 3].Value = room.RoomNumber;
                                    worksheet.Cells[row, 4].Value = room.RoomName;
                                    worksheet.Cells[row, 5].Value = parts.ParamName;
                                    worksheet.Cells[row, 6].Value = parts.OldValue;
                                    worksheet.Cells[row, 7].Value = parts.NewValue;
                                    row++;
                                }
                            }
                            else
                            {
                                worksheet.Cells[row, 1].Value = "Modified";
                                worksheet.Cells[row, 2].Value = room.TrackId;
                                worksheet.Cells[row, 3].Value = room.RoomNumber;
                                worksheet.Cells[row, 4].Value = room.RoomName;
                                row++;
                            }
                        }
                    }

                    // Format header
                    using (var range = worksheet.Cells[1, 1, 1, 7])
                    {
                        range.Style.Font.Bold = true;
                        range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                        range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
                    }

                    worksheet.Cells.AutoFitColumns();
                    package.SaveAs(new FileInfo(saveDialog.FileName));
                }

                MessageBox.Show($"Comparison exported to:\n{saveDialog.FileName}", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{saveDialog.FileName}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export Excel:\n{ex.Message}", "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }

    // ViewModel
    public class ComparisonResultViewModel : INotifyPropertyChanged
    {
        private ObservableCollection<RoomChangeDisplay> _filteredResults;
        private string _filterText = "";
        private string _selectedFilter = "All Changes";

        public string VersionName { get; set; }
        public string VersionInfo { get; set; }
        public string EntityTypeLabel { get; set; } = "ROOMS"; // Can be "ROOMS", "DOORS", or "ELEMENTS"
        public int NewRoomsCount { get; set; }
        public int ModifiedRoomsCount { get; set; }
        public int DeletedRoomsCount { get; set; }
        public ObservableCollection<RoomChangeDisplay> AllResults { get; set; }

        public ObservableCollection<RoomChangeDisplay> FilteredResults
        {
            get => _filteredResults;
            set
            {
                _filteredResults = value;
                OnPropertyChanged(nameof(FilteredResults));
            }
        }

        public string FilterText
        {
            get => _filterText;
            set
            {
                _filterText = value;
                ApplyFilters();
            }
        }

        public string SelectedFilter
        {
            get => _selectedFilter;
            set
            {
                _selectedFilter = value;
                ApplyFilters();
            }
        }

        public ComparisonResultViewModel()
        {
            AllResults = new ObservableCollection<RoomChangeDisplay>();
            FilteredResults = new ObservableCollection<RoomChangeDisplay>();
        }

        private void ApplyFilters()
        {
            var filtered = AllResults.AsEnumerable();

            // Filter by change type
            if (SelectedFilter == "New Only")
                filtered = filtered.Where(r => r.ChangeType == "New");
            else if (SelectedFilter == "Modified Only")
                filtered = filtered.Where(r => r.ChangeType == "Modified");
            else if (SelectedFilter == "Deleted Only")
                filtered = filtered.Where(r => r.ChangeType == "Deleted");

            // Filter by search text
            if (!string.IsNullOrWhiteSpace(FilterText))
            {
                var searchLower = FilterText.ToLower();
                filtered = filtered.Where(r =>
                    r.TrackId?.ToLower().Contains(searchLower) == true ||
                    r.RoomNumber?.ToLower().Contains(searchLower) == true ||
                    r.RoomName?.ToLower().Contains(searchLower) == true ||
                    r.ChangesSummary?.ToLower().Contains(searchLower) == true);
            }

            FilteredResults = new ObservableCollection<RoomChangeDisplay>(filtered);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // Display model
    public class RoomChangeDisplay
    {
        public string ChangeType { get; set; }
        public string TrackId { get; set; }
        public string RoomNumber { get; set; }
        public string RoomName { get; set; }
        public int ChangesCount => Changes?.Count ?? 0;
        public string ChangesSummary => Changes?.Count > 0 ? $"{Changes.Count} parameter(s) changed" : "No parameter changes";
        public List<string> Changes { get; set; } = new List<string>();
    }
}