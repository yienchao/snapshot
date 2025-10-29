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

            // 3. Simple duplicate check with warning (no auto-fix - use Setup Track IDs window for fixing)
            var duplicateTrackIds = roomsWithTrackId
                .GroupBy(r => r.LookupParameter("trackID").AsString())
                .Where(g => g.Count() > 1)
                .ToList();

            if (duplicateTrackIds.Any())
            {
                var duplicateCount = duplicateTrackIds.Count;
                var td = new TaskDialog("Duplicate TrackIDs Detected")
                {
                    MainInstruction = $"Found {duplicateCount} duplicate track ID(s) in the model",
                    MainContent = "Please use the 'Setup Track IDs' tool (Validate tab) to fix duplicates before creating a snapshot.\n\n" +
                                  "Using first occurrence for now.",
                    CommonButtons = TaskDialogCommonButtons.Ok
                };
                td.Show();
            }

            // Initialize Supabase service for version checks and upload
            var supabaseService = new SupabaseService();

            // 4. Get version name and type
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

                // REFACTORED: Only populate dedicated columns for indexing and calculated values
                var snapshot = new RoomSnapshot
                {
                    TrackId = trackId,
                    VersionName = versionName,
                    ProjectId = projectId,
                    FileName = fileName,
                    SnapshotDate = now,
                    CreatedBy = currentUser,
                    IsOfficial = isOfficial,

                    // Dedicated columns for indexing/queries
                    RoomNumber = room.Number,
                    Level = doc.GetElement(room.LevelId)?.Name,

                    // Position data for recreating deleted/unplaced rooms
                    PositionX = posX,
                    PositionY = posY,
                    PositionZ = posZ,

                    // Read-only calculated values for display/reporting
                    Area = room.Area,
                    Perimeter = room.Perimeter,
                    Volume = room.Volume,
                    UnboundHeight = room.UnboundedHeight,

                    // ALL user-editable parameters in JSON (single source of truth)
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

            // REFACTORED: Exclude ONLY parameters that are:
            // 1. In dedicated columns for indexing (room_number, level)
            // 2. Read-only calculated values (area, perimeter, volume, unbound_height)
            // 3. System parameters not useful for comparison (offsets, computation height, IFC, EDITED_BY)
            // Include room_number, level, area, perimeter in AllParameters for comparison
            // They will be marked as non-restorable in the restore window
            var excludedBuiltInParams = new HashSet<BuiltInParameter>
            {
                BuiltInParameter.ROOM_UPPER_OFFSET,     // upper limit (not useful)
                BuiltInParameter.ROOM_LOWER_OFFSET,     // lower limit (not useful)
                BuiltInParameter.ROOM_COMPUTATION_HEIGHT, // not useful
                BuiltInParameter.IFC_EXPORT_ELEMENT_AS,  // system parameter
                BuiltInParameter.EDITED_BY              // system parameter
            };

            // REFACTORED: All custom shared parameters now go in all_parameters JSON
            // Only exclude IFC parameters (caught by "IFC" string check below)
            var excludedSharedParams = new HashSet<string>
            {
                "Exporter au format IFC",  // IFC export (French)
                "Export to IFC",  // IFC export (English)
                "IFC Export"  // IFC export (alternative English)
            };

            // Use GetOrderedParameters to get only user-visible parameters
            var orderedParams = room.GetOrderedParameters();
            foreach (Parameter param in orderedParams)
            {
                string paramName = param.Definition.Name;

                // Skip ALL IFC-related parameters (any parameter containing "IFC" - catches all languages)
                if (paramName.Contains("IFC", StringComparison.OrdinalIgnoreCase))
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