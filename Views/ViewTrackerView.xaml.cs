using ViewTracker.ViewModels;

namespace ViewTracker.Views;

public sealed partial class ViewTrackerView
{
    public ViewTrackerView(ViewTrackerViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}