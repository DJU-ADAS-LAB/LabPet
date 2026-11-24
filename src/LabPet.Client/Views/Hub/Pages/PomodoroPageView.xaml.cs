using System.Windows.Controls;
using LabPet.Client.Navigation;
using LabPet.Client.Solo;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views.Hub.Pages;

public partial class PomodoroPageView : UserControl, IHubPageLifecycle, IDisposable
{
    private readonly SoloHubViewModel _viewModel;
    private readonly PomodoroTimerService _pomodoro;
    private bool _disposed;

    public PomodoroPageView(SoloHubViewModel viewModel, PomodoroTimerService pomodoro, PetWindowViewModel petVm)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _pomodoro = pomodoro;
        DataContext = _viewModel;
        _viewModel.BindProfile(petVm.CurrentProfile);
    }

    public void OnActivated(NavigationRequest request)
    {
        if (_disposed)
        {
            return;
        }

        if (request.Action == HubNavigationAction.StartPomodoroFocus && !_pomodoro.IsRunning)
        {
            _pomodoro.StartOrPause();
        }

        _viewModel.RefreshPomodoroUi();
    }

    public void OnDeactivated()
    {
        // 창을 닫거나 다른 페이지로 이동해도 PomodoroTimerService 백그라운드 타이머는 유지한다.
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
