using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ViewTracker.Models;
using ViewTracker.Views;

namespace ViewTracker.Helpers
{
    /// <summary>
    /// Unified helper for displaying comparison results for all entity types
    /// </summary>
    public static class ComparisonHelper
    {
        public static void ShowComparisonResults(
            ComparisonResult<EntityChange> result,
            string versionName,
            string entityTypeLabel,
            bool hasUnplacedCategory = false)
        {
            if (result.TotalChanges == 0)
            {
                TaskDialog.Show("No Changes", $"No changes detected compared to version '{versionName}'.");
                return;
            }

            // Build ViewModel
            var viewModel = new ComparisonResultViewModel
            {
                VersionName = versionName,
                VersionInfo = $"{entityTypeLabel} Comparison | Version: {versionName} | Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                EntityTypeLabel = entityTypeLabel,
                NewRoomsCount = result.NewEntities.Count,
                ModifiedRoomsCount = result.ModifiedEntities.Count,
                DeletedRoomsCount = result.DeletedEntities.Count,
                UnplacedRoomsCount = hasUnplacedCategory ? result.UnplacedEntities.Count : 0
            };

            // Convert to display models
            var displayItems = new List<RoomChangeDisplay>();

            // New entities
            foreach (var entity in result.NewEntities)
            {
                displayItems.Add(new RoomChangeDisplay
                {
                    ChangeType = "New",
                    TrackId = entity.TrackId,
                    RoomNumber = entity.Identifier1,
                    RoomName = entity.Identifier2,
                    Changes = new List<string>()
                });
            }

            // Modified entities
            foreach (var entity in result.ModifiedEntities)
            {
                displayItems.Add(new RoomChangeDisplay
                {
                    ChangeType = "Modified",
                    TrackId = entity.TrackId,
                    RoomNumber = entity.Identifier1,
                    RoomName = entity.Identifier2,
                    Changes = entity.Changes,
                    InstanceParameterChanges = entity.InstanceParameterChanges,
                    TypeParameterChanges = entity.TypeParameterChanges
                });
            }

            // Deleted entities
            foreach (var entity in result.DeletedEntities)
            {
                displayItems.Add(new RoomChangeDisplay
                {
                    ChangeType = "Deleted",
                    TrackId = entity.TrackId,
                    RoomNumber = entity.Identifier1,
                    RoomName = entity.Identifier2,
                    Changes = new List<string>()
                });
            }

            // Unplaced entities (only for rooms)
            if (hasUnplacedCategory)
            {
                foreach (var entity in result.UnplacedEntities)
                {
                    displayItems.Add(new RoomChangeDisplay
                    {
                        ChangeType = "Unplaced",
                        TrackId = entity.TrackId,
                        RoomNumber = entity.Identifier1,
                        RoomName = entity.Identifier2,
                        Changes = entity.Changes
                    });
                }
            }

            viewModel.AllResults = new ObservableCollection<RoomChangeDisplay>(displayItems);
            viewModel.FilteredResults = new ObservableCollection<RoomChangeDisplay>(displayItems);

            // Show WPF window
            var window = new ComparisonResultWindow(viewModel);
            window.ShowDialog();
        }
    }
}
