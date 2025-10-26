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
    public class DoorSnapshotCommand : IExternalCommand
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

            // 2. Get all door instances with trackID parameter
            var allDoors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .ToList();

            var doorsWithTrackId = allDoors
                .Where(d => d.LookupParameter("trackID") != null &&
                           !string.IsNullOrWhiteSpace(d.LookupParameter("trackID").AsString()))
                .ToList();

            if (!doorsWithTrackId.Any())
            {
                TaskDialog.Show("No Doors", "No doors found with trackID parameter.");
                return Result.Cancelled;
            }

            // 3. Check for duplicate trackIDs in current file
            var trackIdGroups = doorsWithTrackId
                .GroupBy(d => d.LookupParameter("trackID").AsString())
                .Where(g => g.Count() > 1)
                .ToList();

            if (trackIdGroups.Any())
            {
                var duplicates = string.Join("\n", trackIdGroups.Select(g =>
                    $"trackID '{g.Key}': {string.Join(", ", g.Select(d => $"Mark {d.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString()}"))}"));

                TaskDialog.Show("Duplicate trackIDs",
                    $"Found duplicate trackIDs in this file:\n\n{duplicates}\n\nPlease fix before creating snapshot.");
                return Result.Failed;
            }

            // 4. Get version name and type
            var supabaseService = new SupabaseService();
            var versionDialog = new TaskDialog("Create Door Snapshot");
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
                "Door Snapshot Version",
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
            DoorSnapshot existingVersion = null;

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await supabaseService.InitializeAsync();
                    versionExists = await supabaseService.DoorVersionExistsAsync(versionName, fileName);
                    if (versionExists)
                    {
                        existingVersion = await supabaseService.GetDoorVersionInfoAsync(versionName, fileName);
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
            var snapshots = new List<DoorSnapshot>();
            var now = DateTime.UtcNow;
            string currentUser = Environment.UserName;

            foreach (var door in doorsWithTrackId)
            {
                var trackId = door.LookupParameter("trackID").AsString();
                var allParams = GetInstanceParameters(door);
                var typeParams = GetTypeParameters(door);

                var snapshot = new DoorSnapshot
                {
                    TrackId = trackId,
                    VersionName = versionName,
                    ProjectId = projectId,
                    FileName = fileName,
                    SnapshotDate = now,
                    CreatedBy = currentUser,
                    IsOfficial = isOfficial,
                    FamilyName = door.Symbol?.Family?.Name,
                    TypeName = door.Symbol?.Name,
                    Mark = door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                    Level = doc.GetElement(door.LevelId)?.Name,
                    FireRating = door.get_Parameter(BuiltInParameter.DOOR_FIRE_RATING)?.AsString(),
                    PhaseCreated = door.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsValueString(),
                    PhaseDemolished = door.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.AsValueString(),
                    Comments = door.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(),
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
                    await supabaseService.BulkUpsertDoorSnapshotsAsync(snapshots);
                }).Wait();

                string typeLabel = isOfficial ? "Official" : "Draft";
                TaskDialog.Show("Success",
                    $"Captured {snapshots.Count} door(s) to database.\n\nVersion: {versionName} ({typeLabel})\nCreated by: {currentUser}\nDate: {now.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to upload snapshots:\n\n{SanitizeErrorMessage(ex)}");
                return Result.Failed;
            }

            return Result.Succeeded;
        }

        private Dictionary<string, object> GetInstanceParameters(FamilyInstance door)
        {
            var parameters = new Dictionary<string, object>();

            // Built-in parameters that are stored in dedicated columns - exclude from JSON
            // Using BuiltInParameter enum IDs for language-independence (same approach as rooms)
            var excludedBuiltInParams = new HashSet<BuiltInParameter>
            {
                BuiltInParameter.ALL_MODEL_FAMILY_NAME,          // family_name column
                BuiltInParameter.ALL_MODEL_TYPE_NAME,            // type_name column
                BuiltInParameter.ALL_MODEL_MARK,                 // mark column
                BuiltInParameter.FAMILY_LEVEL_PARAM,             // level column
                BuiltInParameter.DOOR_FIRE_RATING,               // fire_rating column
                BuiltInParameter.DOOR_WIDTH,                     // door_width column (from type)
                BuiltInParameter.DOOR_HEIGHT,                    // door_height column (from type)
                BuiltInParameter.PHASE_CREATED,                  // phase_created column
                BuiltInParameter.PHASE_DEMOLISHED,               // phase_demolished column
                BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,    // comments column
                BuiltInParameter.EDITED_BY                       // System metadata (changes automatically)
            };

            // Shared/string parameters to exclude (by name, for non-built-in parameters)
            var excludedSharedParams = new HashSet<string>
            {
                "From Room", "De la pièce",     // From Room
                "To Room", "À la pièce"          // To Room
            };

            // Use GetOrderedParameters to get only user-visible parameters
            var orderedParams = door.GetOrderedParameters();
            foreach (Parameter param in orderedParams)
            {
                string paramName = param.Definition.Name;

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

                // Skip shared parameters that are already in dedicated columns
                if (excludedSharedParams.Contains(paramName))
                    continue;

                // Skip if this parameter name already exists (prefer instance over type)
                if (parameters.ContainsKey(paramName))
                    continue;

                AddParameterValue(param, parameters);
            }

            // Add location information
            var location = door.Location;
            if (location is LocationPoint locationPoint)
            {
                var point = locationPoint.Point;
                parameters["location_x"] = point.X;
                parameters["location_y"] = point.Y;
                parameters["location_z"] = point.Z;
                parameters["rotation"] = locationPoint.Rotation;
            }

            // Add facing and hand orientation (important for flip detection)
            if (door.FacingOrientation != null)
            {
                parameters["facing_x"] = door.FacingOrientation.X;
                parameters["facing_y"] = door.FacingOrientation.Y;
                parameters["facing_z"] = door.FacingOrientation.Z;
            }
            if (door.HandOrientation != null)
            {
                parameters["hand_x"] = door.HandOrientation.X;
                parameters["hand_y"] = door.HandOrientation.Y;
                parameters["hand_z"] = door.HandOrientation.Z;
            }

            return parameters;
        }

        private Dictionary<string, object> GetTypeParameters(FamilyInstance door)
        {
            var parameters = new Dictionary<string, object>();

            if (door.Symbol == null)
                return parameters;

            // Built-in parameters that are stored in dedicated columns - exclude from JSON
            var excludedBuiltInParams = new HashSet<BuiltInParameter>
            {
                BuiltInParameter.ALL_MODEL_FAMILY_NAME,          // family_name column
                BuiltInParameter.ALL_MODEL_TYPE_NAME,            // type_name column
                BuiltInParameter.EDITED_BY                       // System metadata (changes automatically)
            };

            // Use GetOrderedParameters to get only user-visible TYPE parameters
            var orderedParams = door.Symbol.GetOrderedParameters();
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
            object paramValue = null;
            bool shouldAdd = false;

            switch (param.StorageType)
            {
                case StorageType.Double:
                    // Always add double values, even if 0
                    paramValue = param.AsDouble();
                    shouldAdd = true;
                    break;
                case StorageType.Integer:
                    // Use AsValueString() to get display text for enums (e.g., "Par type" instead of "0")
                    var intValueString = param.AsValueString();
                    if (!string.IsNullOrEmpty(intValueString))
                    {
                        paramValue = intValueString;
                        shouldAdd = true;
                    }
                    else
                    {
                        // Fallback to integer if no display string available
                        paramValue = param.AsInteger();
                        shouldAdd = true;
                    }
                    break;
                case StorageType.String:
                    // Save ALL string parameters, even empty ones
                    // Users may want to restore empty values or set values from empty
                    var stringValue = param.AsString();
                    paramValue = stringValue ?? "";  // Use empty string if null
                    shouldAdd = true;
                    break;
                case StorageType.ElementId:
                    // Use AsValueString() to get the display value instead of the ID
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
