using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class UnifiedCompareCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Route to appropriate command based on selected entity type
                IExternalCommand targetCommand = TrackerContext.CurrentEntityType switch
                {
                    TrackerContext.EntityType.Room => new RoomCompareCommand(),
                    TrackerContext.EntityType.Door => new DoorCompareCommand(),
                    TrackerContext.EntityType.Element => new ElementCompareCommand(),
                    _ => new RoomCompareCommand()
                };

                return targetCommand.Execute(commandData, ref message, elements);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to execute compare command:\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}
