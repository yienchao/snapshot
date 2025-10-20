using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using ViewTracker.Views;

namespace ViewTracker.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class SyncRealAreaCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiApp = commandData.Application;
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            // Get selected filled regions or all filled regions in active view
            var selectedIds = uiDoc.Selection.GetElementIds();

            FilteredElementCollector collector;
            bool useSelection = false;

            if (selectedIds.Count > 0)
            {
                // Check if selection contains filled regions
                var selectedFilledRegions = selectedIds
                    .Select(id => doc.GetElement(id))
                    .OfType<FilledRegion>()
                    .ToList();

                if (selectedFilledRegions.Any())
                {
                    useSelection = true;
                    collector = new FilteredElementCollector(doc, selectedIds);
                }
                else
                {
                    // No filled regions in selection, process active view
                    collector = new FilteredElementCollector(doc, doc.ActiveView.Id);
                }
            }
            else
            {
                // No selection, process active view only
                collector = new FilteredElementCollector(doc, doc.ActiveView.Id);
            }

            var filledRegions = collector
                .OfClass(typeof(FilledRegion))
                .Cast<FilledRegion>()
                .ToList();

            if (!filledRegions.Any())
            {
                TaskDialog.Show(Localization.Common.Error,
                    Localization.Get("SyncArea.NoFilledRegions"));
                return Result.Cancelled;
            }

            // Collect all available parameters from filled regions
            var availableParameters = new HashSet<string>();
            foreach (var region in filledRegions)
            {
                foreach (Parameter param in region.Parameters)
                {
                    if (!param.IsReadOnly && param.StorageType == StorageType.Double)
                    {
                        availableParameters.Add(param.Definition.Name);
                    }
                }
            }

            // Show window to select parameter
            var syncWindow = new SyncAreaWindow(availableParameters.ToList(), "Superficie_Nette_Reel");
            var dialogResult = syncWindow.ShowDialog();

            if (dialogResult != true)
                return Result.Cancelled;

            string targetParamName = syncWindow.SelectedParameter;

            if (string.IsNullOrWhiteSpace(targetParamName))
                return Result.Cancelled;

            // Filter filled regions that have the target parameter
            var regionsToSync = filledRegions
                .Where(fr => fr.LookupParameter(targetParamName) != null)
                .ToList();

            if (!regionsToSync.Any())
            {
                TaskDialog.Show(Localization.Common.Error,
                    string.Format(Localization.Get("SyncArea.NoRegionsWithParam"),
                        targetParamName));
                return Result.Cancelled;
            }

            // Sync the values
            int successCount = 0;
            int errorCount = 0;

            using (Transaction trans = new Transaction(doc, "Sync Real Area"))
            {
                trans.Start();

                foreach (var region in regionsToSync)
                {
                    try
                    {
                        var targetParam = region.LookupParameter(targetParamName);

                        if (targetParam == null)
                            continue;

                        // Check if target parameter is read-only
                        if (targetParam.IsReadOnly)
                        {
                            errorCount++;
                            continue;
                        }

                        // Get the real calculated area from Revit (Area property)
                        double realArea = region.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED).AsDouble();

                        // Set to target parameter
                        targetParam.Set(realArea);
                        successCount++;
                    }
                    catch
                    {
                        errorCount++;
                    }
                }

                trans.Commit();
            }

            // Show results
            string resultMessage = string.Format(
                Localization.Get("SyncArea.SuccessMessage"),
                successCount,
                targetParamName);

            if (errorCount > 0)
            {
                resultMessage += $"\n\n{string.Format(Localization.Get("SyncArea.ErrorCount"), errorCount)}";
            }

            if (useSelection)
            {
                resultMessage += $"\n\n{Localization.Get("SyncArea.FromSelection")}";
            }
            else
            {
                resultMessage += $"\n\n{Localization.Get("SyncArea.ActiveView")}";
            }

            TaskDialog.Show(Localization.Common.Success, resultMessage);

            return Result.Succeeded;
        }
    }
}
