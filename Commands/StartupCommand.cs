using Autodesk.Revit.Attributes;
using Nice3point.Revit.Toolkit.External;
using ViewTracker.ViewModels;
using ViewTracker.Views;

namespace ViewTracker.Commands;

/// <summary>
///     External command entry point
/// </summary>
[UsedImplicitly]
[Transaction(TransactionMode.Manual)]
public class StartupCommand : ExternalCommand
{
    public override void Execute()
    {
        var viewModel = new ViewTrackerViewModel();
        var view = new ViewTrackerView(viewModel);
        view.ShowDialog();
    }
}