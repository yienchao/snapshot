using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;
using ViewTracker.Views;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ConvertToRoomsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiApp = commandData.Application;
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            try
            {
                // 1. Prompt user to select filled regions
                var selection = uiDoc.Selection;
                var selectedIds = selection.GetElementIds();

                List<FilledRegion> filledRegions;

                if (selectedIds.Any())
                {
                    // Use pre-selected elements
                    filledRegions = selectedIds
                        .Select(id => doc.GetElement(id))
                        .OfType<FilledRegion>()
                        .ToList();
                }
                else
                {
                    // Prompt for selection
                    try
                    {
                        var selectionFilter = new FilledRegionSelectionFilter();
                        var selectedRefs = selection.PickObjects(ObjectType.Element, selectionFilter, "Select filled regions to convert to rooms");
                        filledRegions = selectedRefs
                            .Select(r => doc.GetElement(r.ElementId))
                            .OfType<FilledRegion>()
                            .ToList();
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        return Result.Cancelled;
                    }
                }

                if (!filledRegions.Any())
                {
                    TaskDialog.Show("No Selection", "No filled regions selected. Please select filled regions to convert.");
                    return Result.Cancelled;
                }

                // 2. Get parameters from filled regions
                var sampleRegion = filledRegions.First();
                var filledRegionParams = new List<string>();

                foreach (Parameter param in sampleRegion.Parameters)
                {
                    if (!param.IsReadOnly && param.Definition is Definition def && param.StorageType != StorageType.ElementId)
                    {
                        var paramName = def.Name;

                        // Skip IFC parameters (they're not useful for room mapping)
                        if (paramName.ToLower().Contains("ifc"))
                            continue;

                        filledRegionParams.Add(paramName);
                    }
                }

                if (!filledRegionParams.Any())
                {
                    var result = TaskDialog.Show("No Parameters",
                        "No parameters found on filled regions. Create rooms without parameter mapping?",
                        TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No);

                    if (result != TaskDialogResult.Yes)
                        return Result.Cancelled;
                }

                // 3. Get available room parameters
                var roomParameters = new List<string>();

                // Get parameters from existing room (or create temporary one to get parameter names)
                var existingRoom = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .FirstOrDefault();

                Room tempRoom = null;
                bool createdTempRoom = false;

                if (existingRoom == null)
                {
                    // Create temporary room to get parameter names
                    using (Transaction tempTrans = new Transaction(doc, "Get Room Parameters"))
                    {
                        tempTrans.Start();
                        var tempPhase = doc.GetElement(doc.ActiveView.get_Parameter(BuiltInParameter.VIEW_PHASE).AsElementId()) as Phase;
                        if (tempPhase != null)
                        {
                            tempRoom = doc.Create.NewRoom(tempPhase);
                            createdTempRoom = true;
                        }
                        tempTrans.Commit();
                    }
                    existingRoom = tempRoom;
                }

                if (existingRoom != null)
                {
                    // Collect built-in writable parameters from the room
                    var builtInParamIds = new[]
                    {
                        BuiltInParameter.ROOM_NAME,
                        BuiltInParameter.ROOM_NUMBER,
                        BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
                        BuiltInParameter.ROOM_DEPARTMENT,
                        BuiltInParameter.ROOM_OCCUPANCY,
                        BuiltInParameter.ROOM_FINISH_BASE,
                        BuiltInParameter.ROOM_FINISH_CEILING,
                        BuiltInParameter.ROOM_FINISH_WALL,
                        BuiltInParameter.ROOM_FINISH_FLOOR
                    };

                    foreach (var paramId in builtInParamIds)
                    {
                        var param = existingRoom.get_Parameter(paramId);
                        if (param != null && !param.IsReadOnly && param.Definition is Definition def)
                        {
                            roomParameters.Add(def.Name);
                        }
                    }

                    // Add shared/project parameters and other built-in writable parameters
                    foreach (Parameter param in existingRoom.Parameters)
                    {
                        if (!param.IsReadOnly &&
                            param.Definition is Definition def &&
                            param.StorageType != StorageType.ElementId)
                        {
                            var paramName = def.Name;

                            // Skip IFC parameters and already-added parameters
                            if (!roomParameters.Contains(paramName) &&
                                !paramName.ToLower().Contains("ifc"))
                            {
                                roomParameters.Add(paramName);
                            }
                        }
                    }
                }

                // Delete temporary room if created
                if (createdTempRoom && tempRoom != null)
                {
                    using (Transaction tempTrans = new Transaction(doc, "Delete Temp Room"))
                    {
                        tempTrans.Start();
                        doc.Delete(tempRoom.Id);
                        tempTrans.Commit();
                    }
                }

                roomParameters = roomParameters.Distinct().OrderBy(p => p).ToList();

                // 4. Show mapping window
                var mappingWindow = new FilledRegionToRoomMappingWindow(filledRegionParams, roomParameters, filledRegions.Count);
                if (mappingWindow.ShowDialog() != true || !mappingWindow.WasConverted)
                    return Result.Cancelled;

                var mappings = mappingWindow.FinalMappings;
                bool placeAtCentroid = mappingWindow.PlaceAtCentroid;
                bool deleteFilledRegions = mappingWindow.DeleteFilledRegions;
                bool addTrackID = mappingWindow.AddTrackID;

                // Check if projectID is valid when trackID is being added
                if (addTrackID)
                {
                    var projectInfo = doc.ProjectInformation;
                    var projectIdParam = projectInfo.LookupParameter("projectID");

                    if (projectIdParam == null || string.IsNullOrWhiteSpace(projectIdParam.AsString()))
                    {
                        TaskDialog.Show("Error",
                            "Convert to Rooms with trackID requires a valid projectID.\n\n" +
                            "Please set the projectID parameter in Project Information before converting.\n\n" +
                            "This ensures rooms will have proper tracking IDs for snapshots and restore.");
                        return Result.Failed;
                    }
                }

                // 5. Get active phase
                var activePhase = doc.GetElement(doc.ActiveView.get_Parameter(BuiltInParameter.VIEW_PHASE).AsElementId()) as Phase;
                if (activePhase == null)
                {
                    TaskDialog.Show("Error", "Could not determine active phase from current view.");
                    return Result.Failed;
                }

                // 6. Get highest existing trackID number (for R-0001 format)
                int nextTrackIdNumber = 1;
                if (addTrackID)
                {
                    var allRooms = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType()
                        .Cast<Room>()
                        .ToList();

                    foreach (var room in allRooms)
                    {
                        var trackIdParam = room.LookupParameter("trackID");
                        if (trackIdParam != null)
                        {
                            var trackId = trackIdParam.AsString();
                            if (!string.IsNullOrEmpty(trackId) && trackId.StartsWith("R-"))
                            {
                                var numberPart = trackId.Substring(2);
                                if (int.TryParse(numberPart, out int number) && number >= nextTrackIdNumber)
                                {
                                    nextTrackIdNumber = number + 1;
                                }
                            }
                        }
                    }
                }

                // 7. Convert filled regions to rooms
                int createdCount = 0;
                int failedCount = 0;
                var elementsToDelete = new List<ElementId>();

                using (Transaction trans = new Transaction(doc, "Convert Filled Regions to Rooms"))
                {
                    trans.Start();

                    foreach (var filledRegion in filledRegions)
                    {
                        try
                        {
                            Room newRoom = null;

                            if (placeAtCentroid)
                            {
                                // Try to place room at centroid
                                var centroid = GetFilledRegionCentroid(filledRegion);
                                if (centroid != null)
                                {
                                    var view = doc.GetElement(filledRegion.OwnerViewId) as View;
                                    var level = doc.GetElement(view.GenLevel.Id) as Level;

                                    if (level != null)
                                    {
                                        try
                                        {
                                            newRoom = doc.Create.NewRoom(level, new UV(centroid.X, centroid.Y));

                                            // Set phase
                                            var phaseParam = newRoom.get_Parameter(BuiltInParameter.ROOM_PHASE);
                                            if (phaseParam != null && !phaseParam.IsReadOnly)
                                            {
                                                phaseParam.Set(activePhase.Id);
                                            }
                                        }
                                        catch
                                        {
                                            // If placement fails, create unplaced room
                                            newRoom = null;
                                        }
                                    }
                                }
                            }

                            // If placement failed or not requested, create unplaced room
                            if (newRoom == null)
                            {
                                newRoom = doc.Create.NewRoom(activePhase);

                                // Try to set level from view
                                var view = doc.GetElement(filledRegion.OwnerViewId) as View;
                                if (view?.GenLevel != null)
                                {
                                    var levelParam = newRoom.get_Parameter(BuiltInParameter.ROOM_LEVEL_ID);
                                    if (levelParam != null && !levelParam.IsReadOnly)
                                    {
                                        levelParam.Set(view.GenLevel.Id);
                                    }
                                }
                            }

                            // Map parameters
                            foreach (var mapping in mappings)
                            {
                                try
                                {
                                    var sourceParam = filledRegion.LookupParameter(mapping.Key);
                                    var targetParam = newRoom.LookupParameter(mapping.Value);

                                    if (sourceParam != null && targetParam != null && !targetParam.IsReadOnly)
                                    {
                                        CopyParameterValue(sourceParam, targetParam);
                                    }
                                }
                                catch { }
                            }

                            // Add trackID if requested (R-0001 format)
                            if (addTrackID)
                            {
                                var trackIdParam = newRoom.LookupParameter("trackID");
                                if (trackIdParam != null && !trackIdParam.IsReadOnly)
                                {
                                    trackIdParam.Set($"R-{nextTrackIdNumber:D4}");
                                    nextTrackIdNumber++;
                                }
                            }

                            createdCount++;

                            if (deleteFilledRegions)
                            {
                                elementsToDelete.Add(filledRegion.Id);
                            }
                        }
                        catch (Exception ex)
                        {
                            failedCount++;
                            System.Diagnostics.Debug.WriteLine($"Failed to convert filled region: {ex.Message}");
                        }
                    }

                    // Delete filled regions if requested
                    if (elementsToDelete.Any())
                    {
                        doc.Delete(elementsToDelete);
                    }

                    trans.Commit();
                }

                // 7. Show results
                string resultMessage = $"Successfully converted {createdCount} filled region(s) to rooms.";
                if (failedCount > 0)
                {
                    resultMessage += $"\n\n{failedCount} failed (no valid room boundary at location).";
                }
                if (deleteFilledRegions)
                {
                    resultMessage += $"\n\nDeleted {elementsToDelete.Count} filled region(s).";
                }
                if (addTrackID)
                {
                    resultMessage += "\n\ntrackID parameter added to new rooms.";
                }

                TaskDialog.Show("Conversion Complete", resultMessage);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to convert filled regions:\n\n{ex.Message}");
                return Result.Failed;
            }
        }

        private XYZ GetFilledRegionCentroid(FilledRegion filledRegion)
        {
            try
            {
                var boundaries = filledRegion.GetBoundaries();
                if (boundaries.Any())
                {
                    var firstLoop = boundaries[0];
                    double sumX = 0, sumY = 0;
                    int count = 0;

                    foreach (Curve curve in firstLoop)
                    {
                        sumX += curve.GetEndPoint(0).X;
                        sumY += curve.GetEndPoint(0).Y;
                        count++;
                    }

                    if (count > 0)
                    {
                        return new XYZ(sumX / count, sumY / count, 0);
                    }
                }
            }
            catch { }

            return null;
        }

        private void CopyParameterValue(Parameter source, Parameter target)
        {
            if (source.StorageType != target.StorageType)
                return;

            switch (source.StorageType)
            {
                case StorageType.String:
                    var strValue = source.AsString();
                    if (!string.IsNullOrEmpty(strValue))
                        target.Set(strValue);
                    break;
                case StorageType.Double:
                    target.Set(source.AsDouble());
                    break;
                case StorageType.Integer:
                    target.Set(source.AsInteger());
                    break;
            }
        }

        private class FilledRegionSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                return elem is FilledRegion;
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false;
            }
        }
    }
}
