using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class ElementHistoryCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc = uiDoc.Document;

            // 1. Validate projectID
            var projectIdStr = doc.ProjectInformation.LookupParameter("projectID")?.AsString();
            if (!Guid.TryParse(projectIdStr, out Guid projectId))
            {
                TaskDialog.Show("Error", "This file does not have a valid projectID parameter.");
                return Result.Failed;
            }

            // 2. Check if user selected an element
            var selectedIds = uiDoc.Selection.GetElementIds();
            FamilyInstance selectedElement = null;

            if (selectedIds.Count == 1)
            {
                var element = doc.GetElement(selectedIds.First());
                if (element is FamilyInstance fi && fi.Category != null &&
                    fi.Category.Id.Value != (int)BuiltInCategory.OST_Doors)
                {
                    selectedElement = fi;
                }
            }

            if (selectedElement == null || selectedElement.LookupParameter("trackID") == null ||
                string.IsNullOrWhiteSpace(selectedElement.LookupParameter("trackID").AsString()))
            {
                TaskDialog.Show("No Element Selected",
                    "Please select exactly one element (not a door) with a trackID parameter to view its history.");
                return Result.Cancelled;
            }

            string trackId = selectedElement.LookupParameter("trackID").AsString();
            string category = selectedElement.Category?.Name;
            string mark = selectedElement.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
            string familyName = selectedElement.Symbol?.Family?.Name;
            string typeName = selectedElement.Symbol?.Name;

            // 3. Get all snapshots for this element across all versions
            var supabaseService = new SupabaseService();
            List<ElementSnapshot> elementHistory = new List<ElementSnapshot>();

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await supabaseService.InitializeAsync();
                    elementHistory = await supabaseService.GetElementHistoryAsync(trackId, projectId);
                }).Wait();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to load element history:\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            if (!elementHistory.Any())
            {
                TaskDialog.Show("No History",
                    $"No snapshot history found for element:\n\nTrack ID: {trackId}\nCategory: {category}\nMark: {mark}\nFamily: {familyName}\nType: {typeName}");
                return Result.Cancelled;
            }

            // 4. Build history timeline
            var timeline = BuildTimeline(elementHistory, doc);

            // 5. Display history
            ShowHistory(trackId, category, mark, familyName, typeName, timeline);

            return Result.Succeeded;
        }

        private List<HistoryEntry> BuildTimeline(List<ElementSnapshot> snapshots, Document doc)
        {
            var timeline = new List<HistoryEntry>();

            // Sort by date (oldest first)
            var sortedSnapshots = snapshots.OrderBy(s => s.SnapshotDate).ToList();

            ElementSnapshot lastAddedSnapshot = null;

            for (int i = 0; i < sortedSnapshots.Count; i++)
            {
                var snapshot = sortedSnapshots[i];
                var entry = new HistoryEntry
                {
                    VersionName = snapshot.VersionName,
                    SnapshotDate = snapshot.SnapshotDate ?? DateTime.MinValue,
                    CreatedBy = snapshot.CreatedBy,
                    IsOfficial = snapshot.IsOfficial,
                    Category = snapshot.Category,
                    Mark = snapshot.Mark,
                    FamilyName = snapshot.FamilyName,
                    TypeName = snapshot.TypeName,
                    Level = snapshot.Level
                };

                // Compare with previous version to find changes
                if (lastAddedSnapshot != null)
                {
                    entry.Changes = GetChangesSincePrevious(lastAddedSnapshot, snapshot, doc);
                    entry.ChangeCount = entry.Changes.Count;

                    // Skip this entry if there are no changes
                    if (entry.ChangeCount == 0)
                        continue;
                }
                else
                {
                    // First snapshot - always show
                    entry.Changes = new List<string> { "Initial snapshot" };
                    entry.ChangeCount = 0;
                }

                timeline.Add(entry);
                lastAddedSnapshot = snapshot;
            }

            return timeline;
        }

        private List<string> GetChangesSincePrevious(ElementSnapshot previous, ElementSnapshot current, Document doc)
        {
            var changes = new List<string>();

            // Build parameter dictionaries
            var prevParams = BuildSnapshotParams(previous);
            var currParams = BuildSnapshotParams(current);

            // Find all changed parameters
            foreach (var currParam in currParams)
            {
                if (prevParams.TryGetValue(currParam.Key, out var prevValue))
                {
                    bool isDifferent = false;

                    if (currParam.Value is double currDouble && prevValue is double prevDouble)
                    {
                        isDifferent = Math.Abs(currDouble - prevDouble) > 0.001;
                    }
                    else
                    {
                        var currStr = currParam.Value?.ToString() ?? "";
                        var prevStr = prevValue?.ToString() ?? "";
                        isDifferent = (currStr != prevStr);
                    }

                    if (isDifferent)
                    {
                        changes.Add($"{currParam.Key}: {prevValue} → {currParam.Value}");
                    }
                }
                else
                {
                    changes.Add($"{currParam.Key}: (new) {currParam.Value}");
                }
            }

            // Find removed parameters
            foreach (var prevParam in prevParams)
            {
                if (!currParams.ContainsKey(prevParam.Key))
                {
                    changes.Add($"{prevParam.Key}: {prevParam.Value} → (removed)");
                }
            }

            return changes;
        }

        private Dictionary<string, object> BuildSnapshotParams(ElementSnapshot snapshot)
        {
            var parameters = new Dictionary<string, object>();

            if (snapshot.AllParameters != null)
            {
                foreach (var kvp in snapshot.AllParameters)
                {
                    parameters[kvp.Key] = kvp.Value;
                }
            }

            // Add dedicated columns
            if (!string.IsNullOrEmpty(snapshot.Category))
                parameters["Category"] = snapshot.Category;
            if (!string.IsNullOrEmpty(snapshot.FamilyName))
                parameters["Family"] = snapshot.FamilyName;
            if (!string.IsNullOrEmpty(snapshot.TypeName))
                parameters["Type"] = snapshot.TypeName;
            if (!string.IsNullOrEmpty(snapshot.Mark))
                parameters["Mark"] = snapshot.Mark;
            if (!string.IsNullOrEmpty(snapshot.Level))
                parameters["Level"] = snapshot.Level;
            if (!string.IsNullOrEmpty(snapshot.PhaseCreated))
                parameters["Phase Created"] = snapshot.PhaseCreated;
            if (!string.IsNullOrEmpty(snapshot.PhaseDemolished))
                parameters["Phase Demolished"] = snapshot.PhaseDemolished;
            if (!string.IsNullOrEmpty(snapshot.Comments))
                parameters["Comments"] = snapshot.Comments;

            return parameters;
        }

        private void ShowHistory(string trackId, string category, string mark, string familyName, string typeName, List<HistoryEntry> timeline)
        {
            var dialog = new TaskDialog("Element Change History");
            dialog.MainInstruction = $"History for {category}: {mark}";
            dialog.MainContent = $"Track ID: {trackId}\nFamily: {familyName}\nType: {typeName}\nTotal snapshots: {timeline.Count}\n\n";

            // Build timeline text (newest first)
            var timelineText = "TIMELINE (newest to oldest):\n";
            timelineText += new string('-', 60) + "\n\n";

            // Reverse the timeline to show most recent first
            foreach (var entry in timeline.AsEnumerable().Reverse())
            {
                var typeLabel = entry.IsOfficial ? "OFFICIAL" : "draft";
                var dateStr = entry.SnapshotDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

                timelineText += $"📅 {dateStr} | {entry.VersionName} ({typeLabel})\n";
                timelineText += $"   By: {entry.CreatedBy ?? "Unknown"}\n";

                if (entry.ChangeCount > 0)
                {
                    timelineText += $"   Changes: {entry.ChangeCount} parameter(s) changed\n";
                    foreach (var change in entry.Changes.Take(3)) // Show first 3 changes
                    {
                        timelineText += $"      • {change}\n";
                    }
                    if (entry.Changes.Count > 3)
                    {
                        timelineText += $"      ... and {entry.Changes.Count - 3} more\n";
                    }
                }
                else
                {
                    timelineText += $"   {entry.Changes.FirstOrDefault()}\n";
                }

                timelineText += "\n";
            }

            dialog.MainContent += timelineText;

            dialog.CommonButtons = TaskDialogCommonButtons.Ok;
            dialog.Show();
        }

        private class HistoryEntry
        {
            public string VersionName { get; set; }
            public DateTime SnapshotDate { get; set; }
            public string CreatedBy { get; set; }
            public bool IsOfficial { get; set; }
            public string Category { get; set; }
            public string Mark { get; set; }
            public string FamilyName { get; set; }
            public string TypeName { get; set; }
            public string Level { get; set; }
            public List<string> Changes { get; set; }
            public int ChangeCount { get; set; }
        }
    }
}
