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
                TaskDialog.Show(Localization.Common.Error, Localization.Get("Validation.NoProjectID"));
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
                TaskDialog.Show(Localization.Get("RoomSnapshot.NoRooms"), Localization.Get("RoomSnapshot.NoRoomsMessage"));
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

                TaskDialog.Show(Localization.Get("RoomSnapshot.DuplicateTrackIDs"),
                    $"{Localization.Get("Validation.DuplicateTrackIDs")}\n\n{duplicates}\n\n{Localization.Get("Validation.FixBeforeSnapshot")}");
                return Result.Failed;
            }

            // 4. Get version name and type
            var supabaseService = new SupabaseService();
            var versionDialog = new TaskDialog(Localization.Get("RoomSnapshot.Title"));
            versionDialog.MainInstruction = Localization.Get("Version.SelectSnapshotType");
            versionDialog.MainContent = Localization.Get("Version.OfficialDescription");
            versionDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, Localization.Get("Version.DraftVersion"), Localization.Get("Version.DraftDescription"));
            versionDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, Localization.Get("Version.OfficialVersion"), Localization.Get("Version.OfficialDescription2"));
            versionDialog.CommonButtons = TaskDialogCommonButtons.Cancel;

            var result = versionDialog.Show();
            if (result == TaskDialogResult.Cancel)
                return Result.Cancelled;

            bool isOfficial = (result == TaskDialogResult.CommandLink2);

            // Get version name
            string defaultName = isOfficial ? $"official_{DateTime.Now:yyyyMMdd}" : $"draft_{DateTime.Now:yyyyMMdd}";
            string versionName = Microsoft.VisualBasic.Interaction.InputBox(
                isOfficial ? Localization.Get("RoomSnapshot.EnterOfficialName") : Localization.Get("RoomSnapshot.EnterDraftName"),
                Localization.Get("RoomSnapshot.VersionTitle"),
                defaultName,
                -1, -1);

            if (string.IsNullOrWhiteSpace(versionName))
                return Result.Cancelled;

            // Validate version name (alphanumeric, underscore, dash only)
            if (!System.Text.RegularExpressions.Regex.IsMatch(versionName, @"^[a-zA-Z0-9_-]+$"))
            {
                TaskDialog.Show(Localization.Get("Version.InvalidVersionName"), Localization.Get("Validation.InvalidVersionName"));
                return Result.Failed;
            }

            // Check if version already exists (reuse supabaseService from trackID check)
            bool versionExists = false;
            RoomSnapshot existingVersion = null;

            try
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await supabaseService.InitializeAsync();
                    versionExists = await supabaseService.VersionExistsAsync(versionName, fileName);
                    if (versionExists)
                    {
                        existingVersion = await supabaseService.GetVersionInfoAsync(versionName, fileName);
                    }
                }).Wait();
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Localization.Common.Error, $"{Localization.Get("RoomSnapshot.FailedCheckVersions")}\n{SanitizeErrorMessage(ex)}");
                return Result.Failed;
            }

            if (versionExists && existingVersion != null)
            {
                string existingType = existingVersion.IsOfficial ? Localization.Get("Type.Official") : Localization.Get("Type.Draft");
                TaskDialog.Show(Localization.Get("Version.AlreadyExists"),
                    string.Format(Localization.Get("Version.ExistsMessage"),
                        versionName, existingType, existingVersion.CreatedBy, existingVersion.SnapshotDate));
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

                string typeLabel = isOfficial ? Localization.Get("Type.Official") : Localization.Get("Type.Draft");
                TaskDialog.Show(Localization.Common.Success,
                    string.Format(Localization.Get("RoomSnapshot.SuccessMessage"),
                        snapshots.Count, versionName, typeLabel, currentUser, now.ToLocalTime()));
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Localization.Common.Error, $"{Localization.Get("RoomSnapshot.FailedUpload")}\n\n{SanitizeErrorMessage(ex)}");
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
                BuiltInParameter.IFC_EXPORT_ELEMENT_AS,  // IFC export parameter (has formatting issues)
                BuiltInParameter.EDITED_BY              // System metadata (changes automatically)
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

                // NEW: Use ParameterValue class for type-safe storage
                var paramValue = Models.ParameterValue.FromRevitParameter(param);
                if (paramValue != null)
                {
                    parameters[paramName] = paramValue;
                }
            }

            return parameters;
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