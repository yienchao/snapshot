using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class UnifiedCompareTwoVersionsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Only rooms support snapshot-vs-snapshot comparison
                // Doors and elements use current-vs-snapshot comparison only
                if (TrackerContext.CurrentEntityType == TrackerContext.EntityType.Door ||
                    TrackerContext.CurrentEntityType == TrackerContext.EntityType.Element)
                {
                    var entityLabel = TrackerContext.CurrentEntityType == TrackerContext.EntityType.Door ? "Doors" : "Elements";
                    TaskDialog.Show("Feature Not Available",
                        $"Snapshot-vs-snapshot comparison is only available for Rooms.\n\n" +
                        $"{entityLabel} only support current model vs snapshot comparison.\n" +
                        $"Please use the regular Compare command instead.");
                    return Result.Cancelled;
                }

                // Route to room comparison command
                IExternalCommand targetCommand = new RoomCompareTwoVersionsCommand();
                return targetCommand.Execute(commandData, ref message, elements);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to execute compare versions command:\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}
