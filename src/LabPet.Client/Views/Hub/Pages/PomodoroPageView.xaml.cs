using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using LabPet.Client.Navigation;
using LabPet.Client.Solo;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views.Hub.Pages;

public partial class PomodoroPageView : UserControl, IHubPageLifecycle, IDisposable
{
    private static readonly TimeSpan DialTransitionDuration = TimeSpan.FromMilliseconds(220);
    private static readonly DependencyProperty DisplayedDialRatioProperty = DependencyProperty.Register(
        "DisplayedDialRatio", typeof(double), typeof(PomodoroPageView),
        new PropertyMetadata(0.0, (control, args) => ((PomodoroPageView)control).DrawDial((double)args.NewValue)));

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
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        IsVisibleChanged += OnDialVisibilityChanged;
        UpdateDialDisplay(animate: false);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoloHubViewModel.PomodoroDialProgressRatio))
        {
            UpdateDialDisplay(animate: !_isDraggingDial && _viewModel.CanConfigurePomodoro && IsLoaded && IsVisible);
        }
        else if (e.PropertyName == nameof(SoloHubViewModel.CanConfigurePomodoro)
                 && !_viewModel.CanConfigurePomodoro && HasAnimatedProperties)
        {
            UpdateDialDisplay(animate: false);
        }
    }

    private void OnDialVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        => UpdateDialDisplay(animate: false);

    private void UpdateDialDisplay(bool animate)
    {
        var target = _viewModel.PomodoroDialProgressRatio;
        var current = (double)GetValue(DisplayedDialRatioProperty);
        if (animate && current != target)
        {
            // 연타하면 직전 목표 대신 그 순간 화면에 그려진 비율부터 다시 이어 간다.
            BeginAnimation(DisplayedDialRatioProperty, new DoubleAnimation(current, target, DialTransitionDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            return;
        }

        BeginAnimation(DisplayedDialRatioProperty, null);
        SetValue(DisplayedDialRatioProperty, target);
    }

    private void DrawDial(double ratio)
    {
        const double center = SoloHubViewModel.PomodoroDialCenter;
        const double radius = SoloHubViewModel.PomodoroDialRadius;
        CircularDialArcPath.Data = PomodoroCircularDialMath.BuildArcGeometry(center, center, radius, ratio);
        var knobCenter = PomodoroCircularDialMath.ComputeKnobCenter(center, center, radius, ratio);
        Canvas.SetLeft(CircularDialKnob, knobCenter.X - CircularDialKnob.Width / 2);
        Canvas.SetTop(CircularDialKnob, knobCenter.Y - CircularDialKnob.Height / 2);
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
        SetDialFromPointer(nextMinutes);
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
        SetDialFromPointer(PomodoroCircularDialMath.PointToFocusMinutes(pos, center, previousMinutes: null));
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

        SetDialFromPointer(PomodoroCircularDialMath.PointToFocusMinutes(
            pos, center, previousMinutes: _viewModel.PomodoroFocusMinutes));
    }

    private void SetDialFromPointer(int minutes)
    {
        _viewModel.PomodoroFocusMinutes = minutes;
        UpdateDialDisplay(animate: false);
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
        UpdateDialDisplay(animate: false);
    }

    public void OnDeactivated()
    {
        // 창을 닫거나 다른 페이지로 이동해도 PomodoroTimerService 백그라운드 타이머는 유지한다.
        UpdateDialDisplay(animate: false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        IsVisibleChanged -= OnDialVisibilityChanged;
        BeginAnimation(DisplayedDialRatioProperty, null);
        _viewModel.Dispose();
    }
}
