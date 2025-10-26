using Microsoft.Win32;
using OfficeOpenXml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfGrid = System.Windows.Controls.Grid;
using WpfLine = System.Windows.Shapes.Line;
using WpfEllipse = System.Windows.Shapes.Ellipse;

namespace ViewTracker.Views
{
    public partial class HistoryTimelineWindow : Window
    {
        private List<TimelineEntry> _timeline;
        private string _trackId;
        private string _elementName;
        private string _elementType; // "Room", "Door", or "Element"

        public class TimelineEntry
        {
            public string VersionName { get; set; }
            public DateTime SnapshotDate { get; set; }
            public string CreatedBy { get; set; }
            public bool IsOfficial { get; set; }
            public string ElementInfo { get; set; } // e.g., "Room 101 - Office"
            public List<string> Changes { get; set; }
            public int ChangeCount { get; set; }
            public object SnapshotData { get; set; } // Full snapshot for export
        }

        public HistoryTimelineWindow(string trackId, string elementName, string elementType, List<TimelineEntry> timeline)
        {
            InitializeComponent();
            _trackId = trackId;
            _elementName = elementName;
            _elementType = elementType;
            _timeline = timeline;

            InitializeTimeline();
        }

        private void InitializeTimeline()
        {
            // Set header
            TitleText.Text = $"{_elementType} History Timeline";
            SubtitleText.Text = $"{_elementName} | Track ID: {_trackId}";
            SummaryText.Text = $"Total snapshots: {_timeline.Count}";

            // Build visual timeline (newest first)
            var reversedTimeline = _timeline.AsEnumerable().Reverse().ToList();

            for (int i = 0; i < reversedTimeline.Count; i++)
            {
                var entry = reversedTimeline[i];
                bool isFirst = (i == 0);
                bool isLast = (i == reversedTimeline.Count - 1);

                AddTimelineNode(entry, isFirst, isLast);
            }
        }

        private void AddTimelineNode(TimelineEntry entry, bool isFirst, bool isLast)
        {
            var nodeContainer = new WpfGrid();
            nodeContainer.Margin = new Thickness(0, 0, 0, 20);

            // Define columns
            nodeContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); // Date
            nodeContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });  // Timeline
            nodeContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Content

            // Date column
            var datePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            var dateText = new TextBlock
            {
                Text = entry.SnapshotDate.ToLocalTime().ToString("yyyy-MM-dd"),
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.DimGray
            };
            var timeText = new TextBlock
            {
                Text = entry.SnapshotDate.ToLocalTime().ToString("HH:mm"),
                FontSize = 11,
                Foreground = Brushes.Gray
            };
            datePanel.Children.Add(dateText);
            datePanel.Children.Add(timeText);
            WpfGrid.SetColumn(datePanel, 0);
            nodeContainer.Children.Add(datePanel);

            // Timeline column (vertical line + node)
            var timelineCanvas = new Canvas { Width = 30 };

            // Vertical line
            if (!isLast)
            {
                var line = new WpfLine
                {
                    X1 = 15,
                    Y1 = 20,
                    X2 = 15,
                    Y2 = 150, // Extended to next node
                    Stroke = (SolidColorBrush)FindResource("TimelineBrush"),
                    StrokeThickness = 2
                };
                timelineCanvas.Children.Add(line);
            }

            // Node (circle)
            var nodeColor = entry.IsOfficial ? (SolidColorBrush)FindResource("OfficialBrush") : (SolidColorBrush)FindResource("DraftBrush");
            var node = new WpfEllipse
            {
                Fill = nodeColor,
                Style = (Style)FindResource("TimelineNodeStyle")
            };
            Canvas.SetLeft(node, 7); // Center it on the line (15 - 8)
            Canvas.SetTop(node, 4);
            timelineCanvas.Children.Add(node);

            // Make node clickable
            node.MouseLeftButtonDown += (s, e) => OnNodeClicked(entry);

            WpfGrid.SetColumn(timelineCanvas, 1);
            nodeContainer.Children.Add(timelineCanvas);

            // Content column (version card)
            var card = CreateVersionCard(entry);
            WpfGrid.SetColumn(card, 2);
            nodeContainer.Children.Add(card);

            // Add to timeline panel
            TimelinePanel.Children.Add(nodeContainer);
        }

        private Border CreateVersionCard(TimelineEntry entry)
        {
            var card = new Border
            {
                Style = (Style)FindResource("VersionCardStyle")
            };

            var cardContent = new StackPanel();

            // Header row (version name + badge)
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

            var versionText = new TextBlock
            {
                Text = entry.VersionName,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerPanel.Children.Add(versionText);

            // Official/Draft badge
            var badge = new Border
            {
                Background = entry.IsOfficial ? (SolidColorBrush)FindResource("OfficialBrush") : (SolidColorBrush)FindResource("DraftBrush"),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(10, 0, 0, 0)
            };
            var badgeText = new TextBlock
            {
                Text = entry.IsOfficial ? "OFFICIAL" : "DRAFT",
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeights.Bold
            };
            badge.Child = badgeText;
            headerPanel.Children.Add(badge);

            cardContent.Children.Add(headerPanel);

            // Creator info
            var creatorText = new TextBlock
            {
                Text = $"By: {entry.CreatedBy ?? "Unknown"}",
                FontSize = 11,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 8)
            };
            cardContent.Children.Add(creatorText);

            // Element info (if available)
            if (!string.IsNullOrEmpty(entry.ElementInfo))
            {
                var elementText = new TextBlock
                {
                    Text = entry.ElementInfo,
                    FontSize = 11,
                    Foreground = Brushes.DimGray,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                cardContent.Children.Add(elementText);
            }

            // Changes
            if (entry.ChangeCount > 0)
            {
                var changesHeader = new TextBlock
                {
                    Text = $"Changes: {entry.ChangeCount} parameter(s)",
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11,
                    Margin = new Thickness(0, 8, 0, 4)
                };
                cardContent.Children.Add(changesHeader);

                // Show first 3 changes
                var changesToShow = entry.Changes.Take(3).ToList();
                foreach (var change in changesToShow)
                {
                    var changeText = new TextBlock
                    {
                        Text = $"• {change}",
                        FontSize = 11,
                        Foreground = Brushes.DimGray,
                        Margin = new Thickness(10, 2, 0, 2),
                        TextWrapping = TextWrapping.Wrap
                    };
                    cardContent.Children.Add(changeText);
                }

                // "... and X more" if there are more changes
                if (entry.Changes.Count > 3)
                {
                    var moreText = new TextBlock
                    {
                        Text = $"... and {entry.Changes.Count - 3} more",
                        FontSize = 11,
                        Foreground = Brushes.Gray,
                        FontStyle = FontStyles.Italic,
                        Margin = new Thickness(10, 2, 0, 2)
                    };
                    cardContent.Children.Add(moreText);
                }
            }
            else
            {
                var initialText = new TextBlock
                {
                    Text = entry.Changes.FirstOrDefault() ?? "No changes",
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                cardContent.Children.Add(initialText);
            }

            card.Child = cardContent;

            // Make card clickable
            card.MouseLeftButtonDown += (s, e) => OnNodeClicked(entry);

            return card;
        }

        private void OnNodeClicked(TimelineEntry entry)
        {
            // Show detailed information in a dialog
            var details = $"Version: {entry.VersionName}\n" +
                         $"Type: {(entry.IsOfficial ? "Official" : "Draft")}\n" +
                         $"Date: {entry.SnapshotDate.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
                         $"Created by: {entry.CreatedBy ?? "Unknown"}\n" +
                         $"Element: {entry.ElementInfo}\n\n" +
                         $"Changes ({entry.ChangeCount}):\n" +
                         string.Join("\n", entry.Changes.Select(c => $"  • {c}"));

            MessageBox.Show(details, "Snapshot Details", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var saveDialog = new SaveFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx|CSV Files (*.csv)|*.csv",
                DefaultExt = "xlsx",
                FileName = $"{_elementType}History_{_trackId}_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (saveDialog.ShowDialog() != true)
                return;

            string filePath = saveDialog.FileName;
            string extension = System.IO.Path.GetExtension(filePath).ToLower();

            try
            {
                if (extension == ".xlsx")
                    ExportToExcel(filePath);
                else
                    ExportToCsv(filePath);

                MessageBox.Show($"History exported successfully to:\n{filePath}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportToExcel(string filePath)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            using (var package = new ExcelPackage())
            {
                var worksheet = package.Workbook.Worksheets.Add($"{_elementType} History");

                // Title
                worksheet.Cells[1, 1].Value = $"History for {_elementType}: {_elementName}";
                worksheet.Cells[1, 1].Style.Font.Bold = true;
                worksheet.Cells[1, 1].Style.Font.Size = 14;
                worksheet.Cells[2, 1].Value = $"Track ID: {_trackId}";

                // Headers
                int headerRow = 4;
                worksheet.Cells[headerRow, 1].Value = "Snapshot Date";
                worksheet.Cells[headerRow, 2].Value = "Version Name";
                worksheet.Cells[headerRow, 3].Value = "Created By";
                worksheet.Cells[headerRow, 4].Value = "Type";
                worksheet.Cells[headerRow, 5].Value = "Element Info";
                worksheet.Cells[headerRow, 6].Value = "Changes Count";
                worksheet.Cells[headerRow, 7].Value = "Change Details";

                // Data (newest first)
                int row = headerRow + 1;
                foreach (var entry in _timeline.AsEnumerable().Reverse())
                {
                    worksheet.Cells[row, 1].Value = entry.SnapshotDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                    worksheet.Cells[row, 2].Value = entry.VersionName;
                    worksheet.Cells[row, 3].Value = entry.CreatedBy ?? "Unknown";
                    worksheet.Cells[row, 4].Value = entry.IsOfficial ? "Official" : "Draft";
                    worksheet.Cells[row, 5].Value = entry.ElementInfo;
                    worksheet.Cells[row, 6].Value = entry.ChangeCount;
                    worksheet.Cells[row, 7].Value = string.Join("; ", entry.Changes);
                    row++;
                }

                // Format header
                using (var range = worksheet.Cells[headerRow, 1, headerRow, 7])
                {
                    range.Style.Font.Bold = true;
                    range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                    range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
                }

                worksheet.Cells.AutoFitColumns();
                package.SaveAs(new FileInfo(filePath));
            }
        }

        private void ExportToCsv(string filePath)
        {
            using (var writer = new StreamWriter(filePath))
            {
                // Title
                writer.WriteLine($"\"History for {_elementType}: {_elementName}\"");
                writer.WriteLine($"\"Track ID: {_trackId}\"");
                writer.WriteLine();

                // Header
                writer.WriteLine("\"Snapshot Date\",\"Version Name\",\"Created By\",\"Type\",\"Element Info\",\"Changes Count\",\"Change Details\"");

                // Data (newest first)
                foreach (var entry in _timeline.AsEnumerable().Reverse())
                {
                    var dateStr = entry.SnapshotDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                    var typeStr = entry.IsOfficial ? "Official" : "Draft";
                    var changeDetails = string.Join("; ", entry.Changes).Replace("\"", "\"\"");

                    writer.WriteLine($"\"{dateStr}\",\"{entry.VersionName}\",\"{entry.CreatedBy ?? "Unknown"}\",\"{typeStr}\",\"{entry.ElementInfo}\",{entry.ChangeCount},\"{changeDetails}\"");
                }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
