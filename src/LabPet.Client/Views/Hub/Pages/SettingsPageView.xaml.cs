using System.Windows.Controls;
using System.Windows.Threading;
using LabPet.Client.Navigation;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views.Hub.Pages;

public partial class SettingsPageView : UserControl, IHubPageLifecycle, IDisposable
{
    private readonly SettingsPageViewModel _viewModel;
    private bool _disposed;

    public SettingsPageView(SettingsPageViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    public void OnActivated(NavigationRequest request)
    {
        if (_disposed)
        {
            return;
        }

        _viewModel.Activate();

        if (string.Equals(request.SubSection, "Network", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => NetworkCard.BringIntoView());
        }
    }

    public void OnDeactivated()
    {
        _viewModel.Deactivate();
    }

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
