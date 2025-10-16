using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualBasic;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class ElementSnapshotCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiApp = commandData.Application;
            var doc = uiApp.ActiveUIDocument.Document;

            // 1. Validate projectID
            var projectIdStr = doc.ProjectInformation.LookupParameter("projectID")?.AsString();
            if (!Guid.TryParse(projectIdStr, out Guid projectId))
            {
                TaskDialog.Show("Error", "This file does not have a valid projectID parameter.");
                return Result.Failed;
            }

            var fileName = System.IO.Path.GetFileNameWithoutExtension(doc.PathName);
            if (string.IsNullOrEmpty(fileName))
                fileName = doc.Title;

            // 2. Get all family instances with trackID parameter (excluding doors)
            var allInstances = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Category != null && fi.Category.Id.Value != (int)BuiltInCategory.OST_Doors)
                .ToList();

            var instancesWithTrackId = allInstances
                .Where(e => e.LookupParameter("trackID") != null &&
                           !string.IsNullOrWhiteSpace(e.LookupParameter("trackID").AsString()))
                .ToList();

            if (!instancesWithTrackId.Any())
            {
                TaskDialog.Show("No Elements", "No elements found with trackID parameter.\n\nMake sure you've added the 'trackID' shared parameter to the categories you want to track.");
                return Result.Cancelled;
            }

            // 3. Check for duplicate trackIDs in current file
            var trackIdGroups = instancesWithTrackId
                .GroupBy(e => e.LookupParameter("trackID").AsString())
                .Where(g => g.Count() > 1)
                .ToList();

            if (trackIdGroups.Any())
            {
                var duplicates = string.Join("\n", trackIdGroups.Select(g =>
                    $"trackID '{g.Key}': {string.Join(", ", g.Select(e => $"{e.Category?.Name} Mark {e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString()}"))}"));

                TaskDialog.Show("Duplicate trackIDs",
                    $"Found duplicate trackIDs in this file:\n\n{duplicates}\n\nPlease fix before creating snapshot.");
                return Result.Failed;
            }

            // 4. Get version name and type
            var versionDialog = new TaskDialog("Create Element Snapshot");
            versionDialog.MainInstruction = "Select snapshot type:";
            versionDialog.MainContent = "Official versions should be created by BIM Manager for milestones.\nDraft versions are for work-in-progress tracking.";
            versionDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Draft Version", "For testing and work-in-progress");
            versionDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Official Version", "For milestones and deliverables (BIM Manager)");
            versionDialog.CommonButtons = TaskDialogCommonButtons.Cancel;

            var result = versionDialog.Show();
            if (result == TaskDialogResult.Cancel)
                return Result.Cancelled;

            bool isOfficial = (result == TaskDialogResult.CommandLink2);

            // Get version name
            string defaultName = isOfficial ? $"official_{DateTime.Now:yyyyMMdd}" : $"draft_{DateTime.Now:yyyyMMdd}";
            string versionName = Microsoft.VisualBasic.Interaction.InputBox(
                isOfficial ? "Enter official version name:\n(e.g., permit_set, design_v2)" : "Enter draft version name:\n(e.g., wip_jan15, test_v1)",
                "Element Snapshot Version",
                defaultName,
                -1, -1);

            if (string.IsNullOrWhiteSpace(versionName))
                return Result.Cancelled;

            // Validate version name (alphanumeric, underscore, dash only)
            if (!System.Text.RegularExpressions.Regex.IsMatch(versionName, @"^[a-zA-Z0-9_-]+$"))
            {
                TaskDialog.Show("Invalid Version Name", "Version name must contain only letters, numbers, underscores, and dashes.");
                return Result.Failed;
            }

            // Check if version already exists
            var supabaseService = new SupabaseService();
            bool versionExists = false;
            ElementSnapshot existingVersion = null;

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await supabaseService.InitializeAsync();
                    versionExists = await supabaseService.ElementVersionExistsAsync(versionName);
                    if (versionExists)
                    {
                        existingVersion = await supabaseService.GetElementVersionInfoAsync(versionName);
                    }
                }).Wait();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to check existing versions:\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            if (versionExists && existingVersion != null)
            {
                string existingType = existingVersion.IsOfficial ? "Official" : "Draft";
                TaskDialog.Show("Version Already Exists",
                    $"Version '{versionName}' already exists:\n\n" +
                    $"Type: {existingType}\n" +
                    $"Created by: {existingVersion.CreatedBy}\n" +
                    $"Date: {existingVersion.SnapshotDate:yyyy-MM-dd HH:mm}\n\n" +
                    $"Please choose a different version name.");
                return Result.Failed;
            }

            // 5. Create snapshots
            var snapshots = new List<ElementSnapshot>();
            var now = DateTime.UtcNow;
            string currentUser = Environment.UserName;

            // Show progress for large datasets
            var categoryStats = instancesWithTrackId.GroupBy(e => e.Category?.Name ?? "Unknown").ToDictionary(g => g.Key, g => g.Count());
            var statsMessage = string.Join("\n", categoryStats.Select(kvp => $"  {kvp.Key}: {kvp.Value}"));

            foreach (var element in instancesWithTrackId)
            {
                var trackId = element.LookupParameter("trackID").AsString();
                var allParams = GetAllParameters(element);

                var snapshot = new ElementSnapshot
                {
                    TrackId = trackId,
                    VersionName = versionName,
                    ProjectId = projectId,
                    FileName = fileName,
                    SnapshotDate = now,
                    CreatedBy = currentUser,
                    IsOfficial = isOfficial,
                    Category = element.Category?.Name,
                    FamilyName = element.Symbol?.Family?.Name,
                    TypeName = element.Symbol?.Name,
                    Mark = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                    Level = doc.GetElement(element.LevelId)?.Name,
                    PhaseCreated = element.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsValueString(),
                    PhaseDemolished = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.AsValueString(),
                    Comments = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(),
                    AllParameters = allParams
                };

                snapshots.Add(snapshot);
            }

            // 6. Upload to Supabase
            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await supabaseService.BulkUpsertElementSnapshotsAsync(snapshots);
                }).Wait();

                string typeLabel = isOfficial ? "Official" : "Draft";
                TaskDialog.Show("Success",
                    $"Captured {snapshots.Count} element(s) to Supabase.\n\nCategories:\n{statsMessage}\n\nVersion: {versionName} ({typeLabel})\nCreated by: {currentUser}\nDate: {now:yyyy-MM-dd HH:mm:ss} UTC");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to upload snapshots:\n\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            return Result.Succeeded;
        }

        private Dictionary<string, object> GetAllParameters(FamilyInstance element)
        {
            var parameters = new Dictionary<string, object>();

            // Parameters that are stored in dedicated columns - exclude from JSON
            var excludedParams = new HashSet<string>
            {
                "Category", "Catégorie",
                "Family", "Famille",
                "Type",
                "Mark", "Marque",
                "Level", "Niveau",
                "Phase Created", "Phase de création",
                "Phase Demolished", "Phase de démolition",
                "Comments", "Commentaires"
            };

            // Capture instance parameters using GetOrderedParameters (user-visible only)
            var orderedInstanceParams = element.GetOrderedParameters();
            foreach (Parameter param in orderedInstanceParams)
            {
                AddParameterToDictionary(param, parameters, excludedParams);
            }

            // Capture type parameters using GetOrderedParameters (user-visible only)
            if (element.Symbol != null)
            {
                var orderedTypeParams = element.Symbol.GetOrderedParameters();
                foreach (Parameter param in orderedTypeParams)
                {
                    AddParameterToDictionary(param, parameters, excludedParams);
                }
            }

            // Add location information
            var location = element.Location;
            if (location is LocationPoint locationPoint)
            {
                var point = locationPoint.Point;
                parameters["location_x"] = point.X;
                parameters["location_y"] = point.Y;
                parameters["location_z"] = point.Z;
                parameters["rotation"] = locationPoint.Rotation;
            }
            else if (location is LocationCurve locationCurve)
            {
                var curve = locationCurve.Curve;
                var startPoint = curve.GetEndPoint(0);
                var endPoint = curve.GetEndPoint(1);
                parameters["location_start_x"] = startPoint.X;
                parameters["location_start_y"] = startPoint.Y;
                parameters["location_start_z"] = startPoint.Z;
                parameters["location_end_x"] = endPoint.X;
                parameters["location_end_y"] = endPoint.Y;
                parameters["location_end_z"] = endPoint.Z;
            }

            // Add facing and hand orientation (important for flip detection)
            if (element.FacingOrientation != null)
            {
                parameters["facing_x"] = element.FacingOrientation.X;
                parameters["facing_y"] = element.FacingOrientation.Y;
                parameters["facing_z"] = element.FacingOrientation.Z;
            }
            if (element.HandOrientation != null)
            {
                parameters["hand_x"] = element.HandOrientation.X;
                parameters["hand_y"] = element.HandOrientation.Y;
                parameters["hand_z"] = element.HandOrientation.Z;
            }

            // Add host information if hosted
            if (element.Host != null)
            {
                parameters["host_id"] = element.Host.Id.Value.ToString();
                parameters["host_category"] = element.Host.Category?.Name;
            }

            return parameters;
        }

        private void AddParameterToDictionary(Parameter param, Dictionary<string, object> parameters, HashSet<string> excludedParams)
        {
            string paramName = param.Definition.Name;

            // Skip parameters that are already in dedicated columns
            if (excludedParams.Contains(paramName))
                return;

            // Skip if this parameter name already exists (prefer instance over type)
            if (parameters.ContainsKey(paramName))
                return;

            object paramValue = null;
            bool shouldAdd = false;

            switch (param.StorageType)
            {
                case StorageType.Double:
                    paramValue = param.AsDouble();
                    shouldAdd = true;
                    break;
                case StorageType.Integer:
                    paramValue = param.AsInteger();
                    shouldAdd = true;
                    break;
                case StorageType.String:
                    var stringValue = param.AsString();
                    if (!string.IsNullOrEmpty(stringValue))
                    {
                        paramValue = stringValue;
                        shouldAdd = true;
                    }
                    break;
                case StorageType.ElementId:
                    var valueString = param.AsValueString();
                    if (!string.IsNullOrEmpty(valueString))
                    {
                        paramValue = valueString;
                        shouldAdd = true;
                    }
                    else if (param.AsElementId().Value != -1)
                    {
                        paramValue = param.AsElementId().Value.ToString();
                        shouldAdd = true;
                    }
                    break;
            }

            if (shouldAdd)
            {
                parameters[paramName] = paramValue;
            }
        }
    }
}
