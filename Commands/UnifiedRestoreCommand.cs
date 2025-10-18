using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class UnifiedRestoreCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Route to appropriate command based on selected entity type
                IExternalCommand targetCommand = TrackerContext.CurrentEntityType switch
                {
                    TrackerContext.EntityType.Room => new RoomRestoreCommand(),
                    TrackerContext.EntityType.Door => new DoorRestoreCommand(),
                    TrackerContext.EntityType.Element => new ElementRestoreCommand(),
                    _ => new RoomRestoreCommand()
                };

                return targetCommand.Execute(commandData, ref message, elements);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to execute restore command:\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}
