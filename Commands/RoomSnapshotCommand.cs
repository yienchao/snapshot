using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualBasic;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class RoomSnapshotCommand : IExternalCommand
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

            // 2. Get all rooms with trackID parameter (including unplaced)
            var allRooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .ToList();

            var roomsWithTrackId = allRooms
                .Where(r => r.LookupParameter("trackID") != null && 
                           !string.IsNullOrWhiteSpace(r.LookupParameter("trackID").AsString()))
                .ToList();

            if (!roomsWithTrackId.Any())
            {
                TaskDialog.Show("No Rooms", "No rooms found with trackID parameter.");
                return Result.Cancelled;
            }

            // 3. Check for duplicate trackIDs in current file
            var trackIdGroups = roomsWithTrackId
                .GroupBy(r => r.LookupParameter("trackID").AsString())
                .Where(g => g.Count() > 1)
                .ToList();

            if (trackIdGroups.Any())
            {
                var duplicates = string.Join("\n", trackIdGroups.Select(g => 
                    $"trackID '{g.Key}': {string.Join(", ", g.Select(r => $"Room {r.Number}"))}"));
                
                TaskDialog.Show("Duplicate trackIDs", 
                    $"Found duplicate trackIDs in this file:\n\n{duplicates}\n\nPlease fix before creating snapshot.");
                return Result.Failed;
            }

            // 4. Get version name and type
            var versionDialog = new TaskDialog("Create Room Snapshot");
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
                "Room Snapshot Version",
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
            RoomSnapshot existingVersion = null;

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await supabaseService.InitializeAsync();
                    versionExists = await supabaseService.VersionExistsAsync(versionName);
                    if (versionExists)
                    {
                        existingVersion = await supabaseService.GetVersionInfoAsync(versionName);
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
            var snapshots = new List<RoomSnapshot>();
            var now = DateTime.UtcNow;
            string currentUser = Environment.UserName;

            foreach (var room in roomsWithTrackId)
            {
                var trackId = room.LookupParameter("trackID").AsString();
                var allParams = GetAllParameters(room);

                // Capture room position (for restoring unplaced rooms)
                double? posX = null, posY = null, posZ = null;
                if (room.Location is LocationPoint locationPoint)
                {
                    var point = locationPoint.Point;
                    posX = point.X;
                    posY = point.Y;
                    posZ = point.Z;
                }

                var snapshot = new RoomSnapshot
                {
                    TrackId = trackId,
                    VersionName = versionName,
                    ProjectId = projectId,
                    FileName = fileName,
                    SnapshotDate = now,
                    CreatedBy = currentUser,
                    IsOfficial = isOfficial,
                    RoomNumber = room.Number,
                    RoomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                    Level = doc.GetElement(room.LevelId)?.Name,
                    Area = room.Area,
                    Perimeter = room.Perimeter,
                    Volume = room.Volume,
                    UnboundHeight = room.UnboundedHeight,
                    Occupancy = room.get_Parameter(BuiltInParameter.ROOM_OCCUPANCY)?.AsString(),
                    Department = room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString(),
                    Phase = room.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsValueString(),
                    PositionX = posX,
                    PositionY = posY,
                    PositionZ = posZ,
                    BaseFinish = room.get_Parameter(BuiltInParameter.ROOM_FINISH_BASE)?.AsString(),
                    CeilingFinish = room.get_Parameter(BuiltInParameter.ROOM_FINISH_CEILING)?.AsString(),
                    WallFinish = room.get_Parameter(BuiltInParameter.ROOM_FINISH_WALL)?.AsString(),
                    FloorFinish = room.get_Parameter(BuiltInParameter.ROOM_FINISH_FLOOR)?.AsString(),
                    Comments = room.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? room.LookupParameter("Comments")?.AsString() ?? room.LookupParameter("Commentaires")?.AsString(),
                    Occupant = room.LookupParameter("Occupant")?.AsString(),
                    AllParameters = allParams
                };

                snapshots.Add(snapshot);
            }

            // 6. Upload to Supabase (reuse existing service)
            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await supabaseService.BulkUpsertRoomSnapshotsAsync(snapshots);
                }).Wait();

                string typeLabel = isOfficial ? "Official" : "Draft";
                TaskDialog.Show("Success",
                    $"Captured {snapshots.Count} room(s) to database.\n\nVersion: {versionName} ({typeLabel})\nCreated by: {currentUser}\nDate: {now.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to upload snapshots:\n\n{ex.InnerException?.Message ?? ex.Message}");
                return Result.Failed;
            }

            return Result.Succeeded;
        }

        private Dictionary<string, object> GetAllParameters(Room room)
        {
            var parameters = new Dictionary<string, object>();

            // Built-in parameters that are stored in dedicated columns - exclude from JSON
            // Using BuiltInParameter enum IDs for language-independence
            var excludedBuiltInParams = new HashSet<BuiltInParameter>
            {
                BuiltInParameter.ROOM_NUMBER,           // room_number column
                BuiltInParameter.ROOM_NAME,             // room_name column
                BuiltInParameter.ROOM_LEVEL_ID,         // level column
                BuiltInParameter.ROOM_AREA,             // area column
                BuiltInParameter.ROOM_PERIMETER,        // perimeter column
                BuiltInParameter.ROOM_VOLUME,           // volume column
                BuiltInParameter.ROOM_UPPER_LEVEL,      // unbound_height column
                BuiltInParameter.ROOM_UPPER_OFFSET,     // upper limit (not useful for comparison, causes false positives)
                BuiltInParameter.ROOM_LOWER_OFFSET,     // lower limit (not useful for comparison, causes false positives)
                BuiltInParameter.ROOM_COMPUTATION_HEIGHT, // computation height (not useful for comparison)
                BuiltInParameter.ROOM_OCCUPANCY,        // occupancy column
                BuiltInParameter.ROOM_DEPARTMENT,       // department column
                BuiltInParameter.ROOM_PHASE,            // phase column
                BuiltInParameter.ROOM_FINISH_BASE,      // base_finish column
                BuiltInParameter.ROOM_FINISH_CEILING,   // ceiling_finish column
                BuiltInParameter.ROOM_FINISH_WALL,      // wall_finish column
                BuiltInParameter.ROOM_FINISH_FLOOR,     // floor_finish column
                BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,  // comments column
                BuiltInParameter.IFC_EXPORT_ELEMENT_AS  // IFC export parameter (has formatting issues)
            };

            // Shared/string parameters to exclude (by name, for language-independence)
            var excludedSharedParams = new HashSet<string>
            {
                "Occupant",  // occupant column (shared parameter)
                "Exporter au format IFC",  // IFC export (French)
                "Export to IFC",  // IFC export (English)
                "IFC Export"  // IFC export (alternative English)
            };

            // Use GetOrderedParameters to get only user-visible parameters
            var orderedParams = room.GetOrderedParameters();
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

                // Skip shared parameters that are already in dedicated columns
                if (excludedSharedParams.Contains(paramName))
                    continue;

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
                        // Only add string parameters if they have a value (non-empty)
                        // This reduces database storage size significantly
                        var stringValue = param.AsString();
                        if (!string.IsNullOrEmpty(stringValue))
                        {
                            paramValue = stringValue;
                            shouldAdd = true;
                        }
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

            return parameters;
        }
    }
}