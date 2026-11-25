using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LabPet.Client.Navigation;
using LabPet.Client.Solo;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views.Hub.Pages;

public partial class PomodoroPageView : UserControl, IHubPageLifecycle, IDisposable
{
    private readonly SoloHubViewModel _viewModel;
    private readonly PomodoroTimerService _pomodoro;
    private readonly PetWindowViewModel _petVm;
    private bool _isDraggingDial;
    private bool _disposed;

    public PomodoroPageView(SoloHubViewModel viewModel, PomodoroTimerService pomodoro, PetWindowViewModel petVm)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _pomodoro = pomodoro;
        _petVm = petVm;
        DataContext = _viewModel;
        _viewModel.BindProfile(petVm.CurrentProfile);
    }

    internal bool ApplyDialPointerForTest(Point pointOnDial, bool usePreviousMinutes = false)
    {
        if (!_viewModel.CanConfigurePomodoro)
        {
            return false;
        }

        var center = new Point(SoloHubViewModel.PomodoroDialCenter, SoloHubViewModel.PomodoroDialCenter);
        var nextMinutes = PomodoroCircularDialMath.PointToFocusMinutes(
            pointOnDial,
            center,
            usePreviousMinutes ? _viewModel.PomodoroFocusMinutes : null);
        _viewModel.PomodoroFocusMinutes = nextMinutes;
        return true;
    }

    private void CircularDialHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.CanConfigurePomodoro)
        {
            return;
        }

        var pos = e.GetPosition(CircularDialHost);
        var center = new Point(CircularDialHost.ActualWidth * 0.5, CircularDialHost.ActualHeight * 0.5);
        if (center.X <= 0 || center.Y <= 0)
        {
            center = new Point(SoloHubViewModel.PomodoroDialCenter, SoloHubViewModel.PomodoroDialCenter);
        }

        var distance = Math.Sqrt(Math.Pow(pos.X - center.X, 2) + Math.Pow(pos.Y - center.Y, 2));
        // 중앙 텍스트 영역(반지름 58px 이내) 클릭은 무시하고 링 영역 드래그·클릭만 받는다.
        if (distance < 58.0)
        {
            return;
        }

        _isDraggingDial = true;
        CircularDialHost.CaptureMouse();
        _viewModel.PomodoroFocusMinutes = PomodoroCircularDialMath.PointToFocusMinutes(pos, center, previousMinutes: null);
        e.Handled = true;
    }

    private void CircularDialHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingDial || !_viewModel.CanConfigurePomodoro)
        {
            return;
        }

        var pos = e.GetPosition(CircularDialHost);
        var center = new Point(CircularDialHost.ActualWidth * 0.5, CircularDialHost.ActualHeight * 0.5);
        if (center.X <= 0 || center.Y <= 0)
        {
            center = new Point(SoloHubViewModel.PomodoroDialCenter, SoloHubViewModel.PomodoroDialCenter);
        }

        _viewModel.PomodoroFocusMinutes = PomodoroCircularDialMath.PointToFocusMinutes(
            pos, center, previousMinutes: _viewModel.PomodoroFocusMinutes);
    }

    private void CircularDialHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingDial)
        {
            return;
        }

        _isDraggingDial = false;
        if (CircularDialHost.IsMouseCaptured)
        {
            CircularDialHost.ReleaseMouseCapture();
        }

        e.Handled = true;
    }

    private void CircularDialHost_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _isDraggingDial = false;
    }

    private void FocusMinutesTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = string.IsNullOrEmpty(e.Text) || e.Text.Any(c => !char.IsDigit(c));
    }

    private void FocusMinutesTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.CommitPomodoroFocusMinutesInput();
            e.Handled = true;
        }
    }

    private void FocusMinutesTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _viewModel.CommitPomodoroFocusMinutesInput();
    }

    public void OnActivated(NavigationRequest request)
    {
        if (_disposed)
        {
            return;
        }

        _petVm.MarkPomodoroFeatureBadgeSeen();
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
