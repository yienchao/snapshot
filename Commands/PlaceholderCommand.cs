using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class PlaceholderCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            TaskDialog.Show("Coming Soon", "Room tracking features are not yet implemented.");
            return Result.Succeeded;
        }
    }
}