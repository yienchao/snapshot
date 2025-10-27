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
            var supabaseService = new SupabaseService();
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

            // Check if version already exists (reuse supabaseService from trackID check)
            bool versionExists = false;
            ElementSnapshot existingVersion = null;

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await supabaseService.InitializeAsync();
                    versionExists = await supabaseService.ElementVersionExistsAsync(versionName, fileName);
                    if (versionExists)
                    {
                        existingVersion = await supabaseService.GetElementVersionInfoAsync(versionName, fileName);
                    }
                }).Wait();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to check existing versions:\n{SanitizeErrorMessage(ex)}");
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
                var allParams = GetInstanceParameters(element);
                var typeParams = GetTypeParameters(element);

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
                    AllParameters = allParams,
                    TypeParameters = typeParams
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
                    $"Captured {snapshots.Count} element(s) to database.\n\nCategories:\n{statsMessage}\n\nVersion: {versionName} ({typeLabel})\nCreated by: {currentUser}\nDate: {now.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to upload snapshots:\n\n{SanitizeErrorMessage(ex)}");
                return Result.Failed;
            }

            return Result.Succeeded;
        }

        private Dictionary<string, object> GetInstanceParameters(FamilyInstance element)
        {
            var parameters = new Dictionary<string, object>();

            // Built-in parameters that are stored in dedicated columns - exclude from JSON
            // Using BuiltInParameter enum IDs for language-independence (same approach as rooms/doors)
            var excludedBuiltInParams = new HashSet<BuiltInParameter>
            {
                BuiltInParameter.ALL_MODEL_FAMILY_NAME,          // family_name column
                BuiltInParameter.ALL_MODEL_TYPE_NAME,            // type_name column
                BuiltInParameter.ALL_MODEL_MARK,                 // mark column
                BuiltInParameter.FAMILY_LEVEL_PARAM,             // level column
                BuiltInParameter.PHASE_CREATED,                  // phase_created column
                BuiltInParameter.PHASE_DEMOLISHED,               // phase_demolished column
                BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,    // comments column
                BuiltInParameter.EDITED_BY                       // System metadata (changes automatically)
            };

            // Shared/string parameters to exclude (by name, for non-built-in parameters)
            var excludedSharedParams = new HashSet<string>
            {
                // NOTE: Variantes is now included - we track it for comparison but won't restore it (read-only)
            };

            // Use GetOrderedParameters to get only user-visible INSTANCE parameters
            var orderedParams = element.GetOrderedParameters();
            foreach (Parameter param in orderedParams)
            {
                string paramName = param.Definition.Name;

                // Skip ALL IFC-related parameters (any parameter containing "IFC" - catches all languages)
                if (paramName.Contains("IFC", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip TYPE parameters - only capture INSTANCE parameters
                // Type parameters belong to the ElementType, not the instance
                if (param.Element is ElementType)
                    continue;

                // Skip built-in parameters that are already in dedicated columns
                if (param.Definition is InternalDefinition internalDef)
                {
                    var builtInParam = internalDef.BuiltInParameter;
                    if (builtInParam != BuiltInParameter.INVALID && excludedBuiltInParams.Contains(builtInParam))
                        continue;
                }

                // Skip shared parameters that are in exclusion list
                if (excludedSharedParams.Contains(paramName))
                    continue;

                // Skip IFC-related parameters (auto-generated)
                if (paramName.StartsWith("IFC", StringComparison.OrdinalIgnoreCase) ||
                    paramName.StartsWith("Ifc", StringComparison.Ordinal))
                    continue;

                // Skip if this parameter name already exists
                if (parameters.ContainsKey(paramName))
                    continue;

                AddParameterValue(param, parameters);
            }

            // Add location information
            // BUGFIX: Wrap in ParameterValue objects for type-safe storage
            var location = element.Location;
            if (location is LocationPoint locationPoint)
            {
                var point = locationPoint.Point;
                parameters["location_x"] = new Models.ParameterValue { StorageType = "Double", RawValue = point.X, DisplayValue = point.X.ToString(), IsTypeParameter = false };
                parameters["location_y"] = new Models.ParameterValue { StorageType = "Double", RawValue = point.Y, DisplayValue = point.Y.ToString(), IsTypeParameter = false };
                parameters["location_z"] = new Models.ParameterValue { StorageType = "Double", RawValue = point.Z, DisplayValue = point.Z.ToString(), IsTypeParameter = false };
                parameters["rotation"] = new Models.ParameterValue { StorageType = "Double", RawValue = locationPoint.Rotation, DisplayValue = locationPoint.Rotation.ToString(), IsTypeParameter = false };
            }
            else if (location is LocationCurve locationCurve)
            {
                var curve = locationCurve.Curve;
                var startPoint = curve.GetEndPoint(0);
                var endPoint = curve.GetEndPoint(1);
                parameters["location_start_x"] = new Models.ParameterValue { StorageType = "Double", RawValue = startPoint.X, DisplayValue = startPoint.X.ToString(), IsTypeParameter = false };
                parameters["location_start_y"] = new Models.ParameterValue { StorageType = "Double", RawValue = startPoint.Y, DisplayValue = startPoint.Y.ToString(), IsTypeParameter = false };
                parameters["location_start_z"] = new Models.ParameterValue { StorageType = "Double", RawValue = startPoint.Z, DisplayValue = startPoint.Z.ToString(), IsTypeParameter = false };
                parameters["location_end_x"] = new Models.ParameterValue { StorageType = "Double", RawValue = endPoint.X, DisplayValue = endPoint.X.ToString(), IsTypeParameter = false };
                parameters["location_end_y"] = new Models.ParameterValue { StorageType = "Double", RawValue = endPoint.Y, DisplayValue = endPoint.Y.ToString(), IsTypeParameter = false };
                parameters["location_end_z"] = new Models.ParameterValue { StorageType = "Double", RawValue = endPoint.Z, DisplayValue = endPoint.Z.ToString(), IsTypeParameter = false };
            }

            // Add facing and hand orientation (important for flip detection)
            if (element.FacingOrientation != null)
            {
                parameters["facing_x"] = new Models.ParameterValue { StorageType = "Double", RawValue = element.FacingOrientation.X, DisplayValue = element.FacingOrientation.X.ToString("F6"), IsTypeParameter = false };
                parameters["facing_y"] = new Models.ParameterValue { StorageType = "Double", RawValue = element.FacingOrientation.Y, DisplayValue = element.FacingOrientation.Y.ToString("F6"), IsTypeParameter = false };
                parameters["facing_z"] = new Models.ParameterValue { StorageType = "Double", RawValue = element.FacingOrientation.Z, DisplayValue = element.FacingOrientation.Z.ToString("F6"), IsTypeParameter = false };
            }
            if (element.HandOrientation != null)
            {
                parameters["hand_x"] = new Models.ParameterValue { StorageType = "Double", RawValue = element.HandOrientation.X, DisplayValue = element.HandOrientation.X.ToString("F6"), IsTypeParameter = false };
                parameters["hand_y"] = new Models.ParameterValue { StorageType = "Double", RawValue = element.HandOrientation.Y, DisplayValue = element.HandOrientation.Y.ToString("F6"), IsTypeParameter = false };
                parameters["hand_z"] = new Models.ParameterValue { StorageType = "Double", RawValue = element.HandOrientation.Z, DisplayValue = element.HandOrientation.Z.ToString("F6"), IsTypeParameter = false };
            }

            // Add host information if hosted
            if (element.Host != null)
            {
                parameters["host_id"] = element.Host.Id.Value.ToString();
                parameters["host_category"] = element.Host.Category?.Name;
            }

            return parameters;
        }

        private Dictionary<string, object> GetTypeParameters(FamilyInstance element)
        {
            var parameters = new Dictionary<string, object>();

            if (element.Symbol == null)
                return parameters;

            // Built-in parameters that are stored in dedicated columns - exclude from JSON
            var excludedBuiltInParams = new HashSet<BuiltInParameter>
            {
                BuiltInParameter.ALL_MODEL_FAMILY_NAME,          // family_name column
                BuiltInParameter.ALL_MODEL_TYPE_NAME,            // type_name column
                BuiltInParameter.EDITED_BY                       // System metadata (changes automatically)
            };

            // Use GetOrderedParameters to get only user-visible TYPE parameters
            var orderedParams = element.Symbol.GetOrderedParameters();
            foreach (Parameter param in orderedParams)
            {
                string paramName = param.Definition.Name;

                // Skip built-in parameters that are already in dedicated columns
                if (param.Definition is InternalDefinition internalDef)
                {
                    var builtInParam = internalDef.BuiltInParameter;
                    if (builtInParam != BuiltInParameter.INVALID && excludedBuiltInParams.Contains(builtInParam))
                        continue;
                }

                // Skip IFC-related parameters (auto-generated)
                if (paramName.StartsWith("IFC", StringComparison.OrdinalIgnoreCase) ||
                    paramName.StartsWith("Ifc", StringComparison.Ordinal) ||
                    paramName.Contains("IFC", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip if this parameter name already exists
                if (parameters.ContainsKey(paramName))
                    continue;

                AddParameterValue(param, parameters);
            }

            return parameters;
        }

        private void AddParameterValue(Parameter param, Dictionary<string, object> parameters)
        {
            string paramName = param.Definition.Name;

            // NEW: Use ParameterValue class for type-safe storage
            var paramValue = Models.ParameterValue.FromRevitParameter(param);
            if (paramValue != null)
            {
                parameters[paramName] = paramValue;
            }
        }

        private string SanitizeErrorMessage(Exception ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;

            // Remove URLs and hostnames to avoid exposing credentials
            message = System.Text.RegularExpressions.Regex.Replace(message,
                @"https?://[^\s\)]+",
                "[server connection]");

            // Remove anything that looks like a Supabase URL pattern
            message = System.Text.RegularExpressions.Regex.Replace(message,
                @"\([a-z0-9]+\.supabase\.co[^\)]*\)",
                "(connection failed)");

            return message;
        }
    }
}
