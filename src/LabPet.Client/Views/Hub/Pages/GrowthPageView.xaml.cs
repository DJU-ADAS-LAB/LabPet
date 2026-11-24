using System.Windows.Controls;
using System.Windows.Threading;
using LabPet.Client.Navigation;
using LabPet.Client.ViewModels;
using LabPet.Shared.Models;

namespace LabPet.Client.Views.Hub.Pages;

public partial class GrowthPageView : UserControl, IHubPageLifecycle, IDisposable
{
    private readonly SoloHubViewModel _viewModel;
    private readonly PetWindowViewModel _petVm;
    private readonly DispatcherTimer _refreshTimer;
    private PetProfile? _boundProfile;
    private bool _isActive;
    private bool _disposed;

    internal bool IsRefreshTimerRunning => _refreshTimer.IsEnabled;

    public GrowthPageView(SoloHubViewModel viewModel, PetWindowViewModel petVm)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _petVm = petVm;
        DataContext = _viewModel;

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _refreshTimer.Tick += (_, _) =>
        {
            SyncProfileBindingIfNeeded();
            _viewModel.RefreshAudioUi();
            _viewModel.RefreshProgressUi();
        };

        _petVm.ProfileUpdated += OnProfileUpdated;
        _petVm.ProfileProgressUpdated += OnProfileUpdated;
        SyncProfileBindingIfNeeded();
    }

    public void OnActivated(NavigationRequest request)
    {
        if (_disposed)
        {
            return;
        }

        _isActive = true;
        SyncProfileBindingIfNeeded();
        _viewModel.RefreshAudioUi();
        _viewModel.RefreshProgressUi();

        if (!_refreshTimer.IsEnabled)
        {
            _refreshTimer.Start();
        }
    }

    public void OnDeactivated()
    {
        _isActive = false;
        if (_refreshTimer.IsEnabled)
        {
            _refreshTimer.Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        OnDeactivated();
        _petVm.ProfileUpdated -= OnProfileUpdated;
        _petVm.ProfileProgressUpdated -= OnProfileUpdated;
        _viewModel.Dispose();
    }

    private void OnProfileUpdated(object? sender, PetProfile profile)
    {
        SyncProfileBindingIfNeeded();
        if (_isActive)
        {
            _viewModel.RefreshProgressUi();
        }
    }

    private void SyncProfileBindingIfNeeded()
    {
        if (!ReferenceEquals(_boundProfile, _petVm.CurrentProfile))
        {
            _boundProfile = _petVm.CurrentProfile;
            _viewModel.BindProfile(_boundProfile);
        }
    }
}
