using System.Windows.Controls;
using LabPet.Client.Navigation;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views.Hub.Pages;

public partial class HomePageView : UserControl, IHubPageLifecycle, IDisposable
{
    private readonly HomePageViewModel _viewModel;
    private bool _disposed;

    public HomePageView(HomePageViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    public void OnActivated(NavigationRequest request)
    {
        if (!_disposed)
        {
            _viewModel.Activate();
        }
    }

    public void OnDeactivated() => _viewModel.Deactivate();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.Dispose();
    }
}
