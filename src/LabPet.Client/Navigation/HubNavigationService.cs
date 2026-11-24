using System.Windows;
using LabPet.Client.Views.Hub;

namespace LabPet.Client.Navigation;

/// <summary>
/// 단일 HubWindow 인스턴스를 관리하고 사이드바·트레이·우클릭·외부 이벤트 내비게이션을 단일화하는 서비스.
/// </summary>
public sealed class HubNavigationService : IHubNavigationService
{
    private readonly Func<HubWindow> _windowFactory;
    private readonly SemaphoreSlim _navigationGate = new(1, 1);

    private HubWindow? _window;

    public HubPage CurrentPage { get; private set; } = HubPage.Home;
    public NavigationRequest CurrentRequest { get; private set; } = new(HubPage.Home);
    public bool IsHubOpen => _window is not null;
    public bool IsHubVisibleAndNotMinimized => _window is { IsVisible: true, WindowState: not WindowState.Minimized };

    public event EventHandler<NavigationRequest>? Navigated;
    public event EventHandler? HubVisibilityChanged;

    public HubNavigationService(Func<HubWindow> windowFactory)
    {
        _windowFactory = windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));
    }

    public async Task<bool> NavigateAsync(NavigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _navigationGate.WaitAsync();
        try
        {
            var isNewWindow = _window is null;
            var window = _window;

            if (window is not null)
            {
                if (!request.BypassUnsavedPrompt && CurrentPage != request.Page)
                {
                    var canLeave = await window.ConfirmLeaveCurrentPageAsync(isClosingWindow: false);
                    if (!canLeave)
                    {
                        window.SyncSidebarSelection(CurrentPage);
                        return false;
                    }
                }

                window.RestoreIfMinimizedForNavigation();

                if (!window.IsVisible)
                {
                    window.Show();
                }

                CurrentPage = request.Page;
                CurrentRequest = request;
                window.ApplyNavigationRequest(request);
                Navigated?.Invoke(this, request);
                window.Activate();
                HubVisibilityChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }

            window = _windowFactory();
            _window = window;
            window.StateChanged += OnHubWindowStateChanged;
            window.IsVisibleChanged += OnHubWindowIsVisibleChanged;
            window.Closed += OnHubWindowClosed;

            CurrentPage = request.Page;
            CurrentRequest = request;
            window.ApplyNavigationRequest(request);
            Navigated?.Invoke(this, request);

            window.Show();
            if (isNewWindow && window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
            HubVisibilityChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            _navigationGate.Release();
        }
    }

    public void CloseIfOpen()
    {
        var window = _window;
        if (window is null)
        {
            return;
        }

        window.ForceCloseWithoutPrompt();
    }

    private void OnHubWindowStateChanged(object? sender, EventArgs e)
        => HubVisibilityChanged?.Invoke(this, EventArgs.Empty);

    private void OnHubWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => HubVisibilityChanged?.Invoke(this, EventArgs.Empty);

    private void OnHubWindowClosed(object? sender, EventArgs e)
    {
        if (sender is HubWindow window)
        {
            window.StateChanged -= OnHubWindowStateChanged;
            window.IsVisibleChanged -= OnHubWindowIsVisibleChanged;
            window.Closed -= OnHubWindowClosed;
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }
        }

        HubVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }
}
