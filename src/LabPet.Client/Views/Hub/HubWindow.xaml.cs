using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LabPet.Client.Interop;
using LabPet.Client.Navigation;
using LabPet.Client.Storage;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views.Hub;

public partial class HubWindow : Window
{
    private readonly HubViewModel _viewModel;
    private readonly Func<HubPage, UserControl> _pageFactory;
    private readonly HubWindowStateStore _stateStore;
    private readonly Dictionary<HubPage, UserControl> _createdPages = [];

    private UserControl? _activePageView;
    private NavigationRequest _lastRequest = new(HubPage.Home);
    private Rect _restoreBounds = new(0, 0, HubWindowState.DefaultWidth, HubWindowState.DefaultHeight);
    private bool _isCustomMaximized;
    private bool _forceCloseWithoutPrompt;
    private bool _isConfirmingClose;
    private bool _isCurrentViewActivated;
    private bool _suppressRestoreActivation;

    internal IReadOnlyCollection<HubPage> CreatedPages => _createdPages.Keys;
    internal UserControl? ActivePageView => _activePageView;
    internal HubViewModel ShellViewModel => _viewModel;

    public HubWindow(
        HubViewModel viewModel,
        Func<HubPage, UserControl> pageFactory,
        HubWindowStateStore stateStore)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _pageFactory = pageFactory;
        _stateStore = stateStore;
        DataContext = _viewModel;

        ApplySavedWindowState(_stateStore.Load());

        Loaded += OnWindowLoaded;
        StateChanged += OnWindowStateChanged;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    internal void RestoreIfMinimizedForNavigation()
    {
        if (WindowState != WindowState.Minimized)
        {
            return;
        }

        _suppressRestoreActivation = true;
        try
        {
            WindowState = WindowState.Normal;
        }
        finally
        {
            _suppressRestoreActivation = false;
        }
    }

    internal void ApplyNavigationRequest(NavigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var targetPage = request.Page;
        if (!_createdPages.TryGetValue(targetPage, out var nextView))
        {
            nextView = _pageFactory(targetPage);
            _createdPages[targetPage] = nextView;
        }

        if (!ReferenceEquals(_activePageView, nextView))
        {
            if (_isCurrentViewActivated)
            {
                (_activePageView as IHubPageLifecycle)?.OnDeactivated();
                _isCurrentViewActivated = false;
            }

            _activePageView = nextView;
            PageHost.Content = nextView;
        }

        _lastRequest = request;
        _viewModel.SyncSelectedPage(targetPage);

        if (WindowState != WindowState.Minimized)
        {
            (nextView as IHubPageLifecycle)?.OnActivated(request);
            _isCurrentViewActivated = true;
        }
    }

    internal void SyncSidebarSelection(HubPage page) => _viewModel.SyncSelectedPage(page);

    internal async Task<bool> ConfirmLeaveCurrentPageAsync(bool isClosingWindow)
    {
        if (_activePageView is IHubUnsavedChangesGuard { HasUnsavedChanges: true } guard)
        {
            return await guard.ConfirmLeaveAsync(isClosingWindow);
        }

        return true;
    }

    internal void ForceCloseWithoutPrompt()
    {
        _forceCloseWithoutPrompt = true;
        Close();
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_isCustomMaximized)
        {
            ApplyCustomMaximizeLayout(saveRestoreBounds: false);
        }

        if (WindowState != WindowState.Minimized && !_isCurrentViewActivated && _activePageView is IHubPageLifecycle lifecycle)
        {
            lifecycle.OnActivated(_lastRequest);
            _isCurrentViewActivated = true;
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            if (_isCurrentViewActivated)
            {
                (_activePageView as IHubPageLifecycle)?.OnDeactivated();
                _isCurrentViewActivated = false;
            }
        }
        else if (!_suppressRestoreActivation && !_isCurrentViewActivated && _activePageView is IHubPageLifecycle lifecycle)
        {
            lifecycle.OnActivated(_lastRequest);
            _isCurrentViewActivated = true;
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_forceCloseWithoutPrompt && _activePageView is IHubUnsavedChangesGuard { HasUnsavedChanges: true } guard)
        {
            e.Cancel = true;
            if (!_isConfirmingClose)
            {
                _ = ConfirmAndCloseAsync(guard);
            }

            return;
        }

        SaveCurrentWindowStateSafely();
    }

    private async Task ConfirmAndCloseAsync(IHubUnsavedChangesGuard guard)
    {
        _isConfirmingClose = true;
        try
        {
            var canClose = await guard.ConfirmLeaveAsync(isClosingWindow: true);
            if (canClose)
            {
                _forceCloseWithoutPrompt = true;
                Close();
            }
        }
        finally
        {
            _isConfirmingClose = false;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Loaded -= OnWindowLoaded;
        StateChanged -= OnWindowStateChanged;
        Closing -= OnWindowClosing;
        Closed -= OnWindowClosed;

        (_activePageView as IHubPageLifecycle)?.OnDeactivated();
        _activePageView = null;
        PageHost.Content = null;

        foreach (var page in _createdPages.Values)
        {
            if (page is IHubPageLifecycle lifecycle)
            {
                lifecycle.OnDeactivated();
            }

            if (page is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _createdPages.Clear();
        _viewModel.Dispose();
    }

    private void ApplySavedWindowState(HubWindowState state)
    {
        Width = state.Width;
        Height = state.Height;
        _viewModel.IsSidebarCollapsed = state.IsSidebarCollapsed;

        if (state.Left is double left && state.Top is double top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
            _restoreBounds = new Rect(left, top, state.Width, state.Height);
        }
        else
        {
            _restoreBounds = new Rect(0, 0, state.Width, state.Height);
        }

        _isCustomMaximized = state.IsMaximized;
    }

    private void SaveCurrentWindowStateSafely()
    {
        try
        {
            var normalBounds = _isCustomMaximized
                ? _restoreBounds
                : new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);

            _stateStore.Save(new HubWindowState
            {
                Left = double.IsFinite(normalBounds.Left) ? normalBounds.Left : null,
                Top = double.IsFinite(normalBounds.Top) ? normalBounds.Top : null,
                Width = double.IsFinite(normalBounds.Width) && normalBounds.Width >= MinWidth ? normalBounds.Width : HubWindowState.DefaultWidth,
                Height = double.IsFinite(normalBounds.Height) && normalBounds.Height >= MinHeight ? normalBounds.Height : HubWindowState.DefaultHeight,
                IsMaximized = _isCustomMaximized,
                IsSidebarCollapsed = _viewModel.IsSidebarCollapsed
            });
        }
        catch
        {
            // 레이아웃 저장 실패로 앱 종료가 막히지 않도록 한다.
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
            OuterShellBorder.Margin = new Thickness(10);
            OuterShellBorder.CornerRadius = new CornerRadius(18);
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
            return;
        }

        ApplyCustomMaximizeLayout(saveRestoreBounds: true);
    }

    private void ApplyCustomMaximizeLayout(bool saveRestoreBounds)
    {
        if (saveRestoreBounds)
        {
            _restoreBounds = new Rect(Left, Top, Width, Height);
        }

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
