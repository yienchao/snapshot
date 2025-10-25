using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class SetupIDsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uiApp = commandData.Application;
                var doc = uiApp.ActiveUIDocument.Document;

                // Validate projectID
                var projectIdStr = doc.ProjectInformation.LookupParameter("projectID")?.AsString();
                if (!Guid.TryParse(projectIdStr, out Guid projectId))
                {
                    TaskDialog.Show("Error", "This file does not have a valid projectID parameter.\n\nPlease add a 'projectID' project parameter with a valid GUID.");
                    return Result.Failed;
                }

                // Show placeholder window
                var window = new Views.SetupIDsWindow(doc, projectId);
                window.ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to open Setup IDs window:\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}
