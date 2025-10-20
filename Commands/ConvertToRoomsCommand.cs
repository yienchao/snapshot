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
                        if (paramName.Contains("IFC") ||
                            paramName.Contains("prédéfini d'IFC") ||
                            paramName == "Exporter au format IFC" ||
                            paramName == "Export to IFC" ||
                            paramName == "Exporter au format IFC sous" ||
                            paramName == "Export to IFC as" ||
                            paramName == "Type prédéfini d'IFC")
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

                // Built-in parameters
                roomParameters.Add("Name");
                roomParameters.Add("Number");
                roomParameters.Add("Comments");
                roomParameters.Add("Department");
                roomParameters.Add("Occupancy");
                roomParameters.Add("Base Finish");
                roomParameters.Add("Ceiling Finish");
                roomParameters.Add("Wall Finish");
                roomParameters.Add("Floor Finish");

                // Get shared parameters from project
                var existingRooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .FirstOrDefault();

                if (existingRooms != null)
                {
                    foreach (Parameter param in existingRooms.Parameters)
                    {
                        if (!param.IsReadOnly && param.Definition is Definition def)
                        {
                            var paramName = def.Name;
                            if (!roomParameters.Contains(paramName) &&
                                !paramName.StartsWith("Phase") &&
                                !paramName.StartsWith("Level") &&
                                param.StorageType != StorageType.ElementId &&
                                // Skip IFC parameters
                                !paramName.Contains("IFC") &&
                                !paramName.Contains("prédéfini d'IFC") &&
                                paramName != "Exporter au format IFC" &&
                                paramName != "Export to IFC" &&
                                paramName != "Exporter au format IFC sous" &&
                                paramName != "Export to IFC as" &&
                                paramName != "Type prédéfini d'IFC")
                            {
                                roomParameters.Add(paramName);
                            }
                        }
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

                // 6. Convert filled regions to rooms
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

                            // Add trackID if requested
                            if (addTrackID)
                            {
                                var trackIdParam = newRoom.LookupParameter("trackID");
                                if (trackIdParam != null && !trackIdParam.IsReadOnly)
                                {
                                    trackIdParam.Set(Guid.NewGuid().ToString());
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
