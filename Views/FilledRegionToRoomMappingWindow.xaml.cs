using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using ViewTracker.Models;

namespace ViewTracker.Views
{
    public partial class FilledRegionToRoomMappingWindow : Window
    {
        public class MappingRow : INotifyPropertyChanged
        {
            private string _targetParameter;

            public string SourceParameter { get; set; }
            public ObservableCollection<string> AvailableRoomParameters { get; set; }
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
        public Dictionary<string, string> FinalMappings { get; private set; }
        public bool PlaceAtCentroid { get; private set; }
        public bool DeleteFilledRegions { get; private set; }
        public bool AddTrackID { get; private set; }
        public bool WasConverted { get; private set; } = false;

        public FilledRegionToRoomMappingWindow(List<string> filledRegionParams, List<string> roomParameters, int selectedCount)
        {
            InitializeComponent();

            InfoText.Text = $"{selectedCount} filled region(s) selected for conversion.";

            _mappingRows = new List<MappingRow>();

            // Add "(Skip)" option
            var roomParamsWithSkip = new List<string> { "(Skip)" };
            roomParamsWithSkip.AddRange(roomParameters);

            foreach (var param in filledRegionParams)
            {
                var row = new MappingRow
                {
                    SourceParameter = param,
                    AvailableRoomParameters = new ObservableCollection<string>(roomParamsWithSkip),
                    TargetParameter = SuggestRoomParameter(param, roomParamsWithSkip)
                };
                _mappingRows.Add(row);
            }

            MappingItemsControl.ItemsSource = _mappingRows;
        }

        private string SuggestRoomParameter(string filledRegionParam, List<string> roomParameters)
        {
            var paramLower = filledRegionParam.ToLower().Trim();

            // Direct match (case insensitive)
            var directMatch = roomParameters.FirstOrDefault(p => p.Equals(filledRegionParam, StringComparison.OrdinalIgnoreCase));
            if (directMatch != null && directMatch != "(Skip)")
                return directMatch;

            // Remove common prefixes
            var cleanedParam = paramLower
                .Replace("region_", "")
                .Replace("space_", "")
                .Replace("room_", "")
                .Replace("fr_", "");

            // Try to match cleaned name
            var cleanedMatch = roomParameters.FirstOrDefault(p =>
                p.ToLower().Replace("room_", "").Replace("space_", "") == cleanedParam);
            if (cleanedMatch != null && cleanedMatch != "(Skip)")
                return cleanedMatch;

            // Smart suggestions for common parameters
            var suggestions = new Dictionary<string, string[]>
            {
                { "name", new[] { "Name", "Room Name" } },
                { "number", new[] { "Number", "Room Number" } },
                { "area", new[] { "Area" } },
                { "department", new[] { "Department", "Room Department" } },
                { "occupancy", new[] { "Occupancy", "Room Occupancy" } },
                { "level", new[] { "Level" } },
                { "comments", new[] { "Comments" } },
                { "phase", new[] { "Phase" } }
            };

            foreach (var kvp in suggestions)
            {
                if (cleanedParam.Contains(kvp.Key))
                {
                    foreach (var variant in kvp.Value)
                    {
                        var match = roomParameters.FirstOrDefault(p => p.Equals(variant, StringComparison.OrdinalIgnoreCase));
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
                    $"FR_to_Room_{DateTime.Now:yyyyMMdd}",
                    -1, -1);

                if (string.IsNullOrWhiteSpace(presetName))
                    return;

                var preset = new MappingPreset
                {
                    Name = presetName,
                    Mappings = _mappingRows.Select(r => new ParameterMapping
                    {
                        SourceColumn = r.SourceParameter,
                        TargetParameter = r.TargetParameter
                    }).ToList()
                };

                string appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "dataTracker", "MappingPresets");

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
                    "dataTracker", "MappingPresets");

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

                foreach (var mapping in preset.Mappings)
                {
                    var row = _mappingRows.FirstOrDefault(r => r.SourceParameter == mapping.SourceColumn);
                    if (row != null && row.AvailableRoomParameters.Contains(mapping.TargetParameter))
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

        private void Convert_Click(object sender, RoutedEventArgs e)
        {
            FinalMappings = new Dictionary<string, string>();

            foreach (var row in _mappingRows)
            {
                if (row.TargetParameter != "(Skip)" && !string.IsNullOrWhiteSpace(row.TargetParameter))
                {
                    FinalMappings[row.SourceParameter] = row.TargetParameter;
                }
            }

            if (!FinalMappings.Any())
            {
                var result = MessageBox.Show(
                    "No parameters are mapped. Rooms will be created without parameter data.\n\nContinue anyway?",
                    "Warning",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return;
            }

            PlaceAtCentroid = ChkPlaceAtCentroid.IsChecked == true;
            DeleteFilledRegions = ChkDeleteFilledRegions.IsChecked == true;
            AddTrackID = ChkAddTrackID.IsChecked == true;

            WasConverted = true;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            WasConverted = false;
            DialogResult = false;
            Close();
        }
    }
}
