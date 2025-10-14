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
                // Route to appropriate command based on selected entity type
                IExternalCommand targetCommand = TrackerContext.CurrentEntityType switch
                {
                    TrackerContext.EntityType.Room => new RoomCompareTwoVersionsCommand(),
                    TrackerContext.EntityType.Door => new DoorCompareTwoVersionsCommand(),
                    TrackerContext.EntityType.Element => new ElementCompareTwoVersionsCommand(),
                    _ => new RoomCompareTwoVersionsCommand()
                };

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
