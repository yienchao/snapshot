using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class UnifiedHistoryCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Route to appropriate command based on selected entity type
                IExternalCommand targetCommand = TrackerContext.CurrentEntityType switch
                {
                    TrackerContext.EntityType.Room => new RoomHistoryCommand(),
                    TrackerContext.EntityType.Door => new DoorHistoryCommand(),
                    TrackerContext.EntityType.Element => new ElementHistoryCommand(),
                    _ => new RoomHistoryCommand()
                };

                return targetCommand.Execute(commandData, ref message, elements);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to execute history command:\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}
