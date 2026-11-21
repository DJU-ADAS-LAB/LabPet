using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LabPet.Client.Interop;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views;

public partial class SoloHubWindow : Window
{
    private readonly SoloHubViewModel _viewModel;
    private readonly DispatcherTimer _growthRefreshTimer;
    private Rect _restoreBounds;
    private bool _isCustomMaximized;

    public SoloHubWindow(SoloHubViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        // 성장 & 오디오 섹션이 실제로 보일 때만 저부하(250ms)로 갱신한다.
        _growthRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _growthRefreshTimer.Tick += (_, _) =>
        {
            _viewModel.RefreshAudioUi();
            _viewModel.RefreshProgressUi();
        };

        _viewModel.PropertyChanged += OnHubPropertyChanged;
        StateChanged += (_, _) => SyncGrowthRefreshTimer();

        Loaded += (_, _) => SyncGrowthRefreshTimer();

        Closed += (_, _) =>
        {
            _growthRefreshTimer.Stop();
            _viewModel.PropertyChanged -= OnHubPropertyChanged;
            _viewModel.Dispose();
        };
    }

    private void OnHubPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SoloHubViewModel.ActiveSection) or nameof(SoloHubViewModel.SelectedMainTabIndex))
        {
            SyncGrowthRefreshTimer();
        }
    }

    private void SyncGrowthRefreshTimer()
    {
        var shouldRun = IsLoaded
            && WindowState != WindowState.Minimized
            && _viewModel.IsGrowthSectionActive;

        if (shouldRun && !_growthRefreshTimer.IsEnabled)
        {
            _viewModel.RefreshAudioUi();
            _viewModel.RefreshProgressUi();
            _growthRefreshTimer.Start();
        }
        else if (!shouldRun && _growthRefreshTimer.IsEnabled)
        {
            _growthRefreshTimer.Stop();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleCustomMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed && !_isCustomMaximized)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
        => ToggleCustomMaximize();

    private void ToggleCustomMaximize()
    {
        if (_isCustomMaximized)
        {
            _isCustomMaximized = false;
            OuterShellBorder.Margin = new Thickness(12);
            OuterShellBorder.CornerRadius = new CornerRadius(22);
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
            return;
        }

        _restoreBounds = new Rect(Left, Top, Width, Height);
        _isCustomMaximized = true;
        OuterShellBorder.Margin = new Thickness(0);
        OuterShellBorder.CornerRadius = new CornerRadius(0);

        if (Win32WindowHelper.TryGetNearestWorkArea(this, out var workArea))
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = workArea.Left / dpi.DpiScaleX;
            Top = workArea.Top / dpi.DpiScaleY;
            Width = workArea.Width / dpi.DpiScaleX;
            Height = workArea.Height / dpi.DpiScaleY;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left;
            Top = wa.Top;
            Width = wa.Width;
            Height = wa.Height;
        }
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_isCustomMaximized || sender is not Thumb { Tag: string direction })
        {
            return;
        }

        var nextLeft = Left;
        var nextTop = Top;
        var nextWidth = Width;
        var nextHeight = Height;

        if (direction.Contains("Right", StringComparison.Ordinal))
        {
            nextWidth = Math.Max(MinWidth, nextWidth + e.HorizontalChange);
        }

        if (direction.Contains("Bottom", StringComparison.Ordinal))
        {
            nextHeight = Math.Max(MinHeight, nextHeight + e.VerticalChange);
        }

        if (direction.Contains("Left", StringComparison.Ordinal))
        {
            var candidateWidth = Math.Max(MinWidth, nextWidth - e.HorizontalChange);
            nextLeft += nextWidth - candidateWidth;
            nextWidth = candidateWidth;
        }

        if (direction.Contains("Top", StringComparison.Ordinal))
        {
            var candidateHeight = Math.Max(MinHeight, nextHeight - e.VerticalChange);
            nextTop += nextHeight - candidateHeight;
            nextHeight = candidateHeight;
        }

        Left = nextLeft;
        Top = nextTop;
        Width = nextWidth;
        Height = nextHeight;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
