using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using ViewTracker.Models;

namespace ViewTracker.Views
{
    public partial class ExcelMappingWindow : Window
    {
        public class MappingRow : INotifyPropertyChanged
        {
            private string _targetParameter;

            public string SourceColumn { get; set; }
            public ObservableCollection<string> AvailableParameters { get; set; }
            public string TargetParameter
            {
                get => _targetParameter;
                set
                {
                    _targetParameter = value;
                    OnPropertyChanged(nameof(TargetParameter));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            protected void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        private List<MappingRow> _mappingRows;
        private List<string> _excelColumns;
        public Dictionary<string, string> FinalMappings { get; private set; }
        public string GroupByColumn { get; private set; }
        public string ColorByColumn { get; private set; }
        public bool IsSquareFeet { get; private set; } = false;
        public bool WasImported { get; private set; } = false;

        public ExcelMappingWindow(List<string> excelColumns, List<string> availableParameters)
        {
            InitializeComponent();

            _excelColumns = excelColumns;
            _mappingRows = new List<MappingRow>();

            // Add "(Skip)" option
            var parametersWithSkip = new List<string> { "(Skip)" };
            parametersWithSkip.AddRange(availableParameters);

            foreach (var column in excelColumns)
            {
                var row = new MappingRow
                {
                    SourceColumn = column,
                    AvailableParameters = new ObservableCollection<string>(parametersWithSkip),
                    TargetParameter = SuggestParameter(column, parametersWithSkip)
                };
                _mappingRows.Add(row);
            }

            MappingItemsControl.ItemsSource = _mappingRows;

            // Populate GroupBy dropdown with Excel columns
            foreach (var column in excelColumns)
            {
                GroupByComboBox.Items.Add(new ComboBoxItem { Content = column });
            }

            // Populate ColorBy dropdown with Excel columns
            foreach (var column in excelColumns)
            {
                ColorByComboBox.Items.Add(new ComboBoxItem { Content = column });
            }
        }

        private string SuggestParameter(string excelColumn, List<string> availableParameters)
        {
            // Smart suggestions based on common names
            var columnLower = excelColumn.ToLower().Trim();

            // Direct matches
            if (availableParameters.Any(p => p.Equals(excelColumn, StringComparison.OrdinalIgnoreCase)))
                return availableParameters.First(p => p.Equals(excelColumn, StringComparison.OrdinalIgnoreCase));

            // Common variations
            var suggestions = new Dictionary<string, string[]>
            {
                { "name", new[] { "Name", "Room Name", "Space Name" } },
                { "number", new[] { "Number", "Room Number", "Space Number" } },
                { "area", new[] { "Area", "Target Area", "Program Area" } },
                { "department", new[] { "Department", "Room Department" } },
                { "occupancy", new[] { "Occupancy", "Room Occupancy" } },
                { "level", new[] { "Level", "Room Level" } },
                { "comments", new[] { "Comments", "Notes", "Description" } }
            };

            foreach (var kvp in suggestions)
            {
                if (columnLower.Contains(kvp.Key))
                {
                    foreach (var variant in kvp.Value)
                    {
                        var match = availableParameters.FirstOrDefault(p => p.Equals(variant, StringComparison.OrdinalIgnoreCase));
                        if (match != null)
                            return match;
                    }
                }
            }

            return "(Skip)";
        }

        private void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var presetName = Microsoft.VisualBasic.Interaction.InputBox(
                    "Enter a name for this mapping preset:",
                    "Save Mapping Preset",
                    $"Mapping_{DateTime.Now:yyyyMMdd}",
                    -1, -1);

                if (string.IsNullOrWhiteSpace(presetName))
                    return;

                var preset = new MappingPreset
                {
                    Name = presetName,
                    Mappings = _mappingRows.Select(r => new ParameterMapping
                    {
                        SourceColumn = r.SourceColumn,
                        TargetParameter = r.TargetParameter
                    }).ToList()
                };

                // Save to AppData
                string appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Snapshot", "MappingPresets");

                Directory.CreateDirectory(appDataPath);

                string presetPath = Path.Combine(appDataPath, $"{presetName}.json");
                string json = JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(presetPath, json);

                PresetStatusText.Text = $"Preset '{presetName}' saved";
                MessageBox.Show($"Mapping preset saved:\n{presetPath}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save preset:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadPreset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Snapshot", "MappingPresets");

                if (!Directory.Exists(appDataPath) || !Directory.GetFiles(appDataPath, "*.json").Any())
                {
                    MessageBox.Show("No saved presets found.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var openFileDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Load Mapping Preset",
                    Filter = "JSON Files (*.json)|*.json",
                    InitialDirectory = appDataPath
                };

                if (openFileDialog.ShowDialog() != true)
                    return;

                string json = File.ReadAllText(openFileDialog.FileName);
                var preset = JsonSerializer.Deserialize<MappingPreset>(json);

                if (preset?.Mappings == null)
                {
                    MessageBox.Show("Invalid preset file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Apply preset mappings
                foreach (var mapping in preset.Mappings)
                {
                    var row = _mappingRows.FirstOrDefault(r => r.SourceColumn == mapping.SourceColumn);
                    if (row != null && row.AvailableParameters.Contains(mapping.TargetParameter))
                    {
                        row.TargetParameter = mapping.TargetParameter;
                    }
                }

                PresetStatusText.Text = $"Preset '{preset.Name}' loaded";
                MessageBox.Show($"Mapping preset loaded:\n{preset.Name}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load preset:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            // Build final mappings (exclude skipped columns)
            FinalMappings = new Dictionary<string, string>();

            foreach (var row in _mappingRows)
            {
                if (row.TargetParameter != "(Skip)" && !string.IsNullOrWhiteSpace(row.TargetParameter))
                {
                    FinalMappings[row.SourceColumn] = row.TargetParameter;
                }
            }

            if (!FinalMappings.Any())
            {
                MessageBox.Show("Please map at least one column to a parameter.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Get grouping column
            if (GroupByComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                var content = selectedItem.Content?.ToString();
                if (content != "(No grouping)")
                {
                    GroupByColumn = content;
                }
            }

            // Get color-by column
            if (ColorByComboBox.SelectedItem is ComboBoxItem selectedColorItem)
            {
                var content = selectedColorItem.Content?.ToString();
                if (content != "(No colors - default gray)")
                {
                    ColorByColumn = content;
                }
            }

            // Get area units
            if (AreaUnitsComboBox.SelectedItem is ComboBoxItem selectedUnits)
            {
                var tag = selectedUnits.Tag?.ToString();
                IsSquareFeet = (tag == "SquareFeet");
            }

            WasImported = true;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            WasImported = false;
            DialogResult = false;
            Close();
        }
    }
}
