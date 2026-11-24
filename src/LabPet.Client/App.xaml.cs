using System.Windows;
using LabPet.Client.Audio;
using LabPet.Client.Navigation;
using LabPet.Client.Network;
using LabPet.Client.Rendering;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Client.SystemIntegration;
using LabPet.Client.Updates;
using LabPet.Client.ViewModels;
using LabPet.Client.Views;
using LabPet.Client.Views.Hub;
using LabPet.Client.Views.Hub.Pages;
using LabPet.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace LabPet.Client;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Local\\LabPet_SingleInstance_Mutex";

    private Mutex? _singleInstanceMutex;
    private ServiceProvider? _serviceProvider;
    private SocialCoordinatorService? _social;
    private FriendMessageRouter? _messageRouter;
    private PetWindow? _petWindow;
    private GameInviteCardWindow? _inviteCardWindow;
    private readonly Dictionary<string, VisitorPetWindow> _visitorWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly VisitorPopupOrder _visitorPopupOrder = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            singleInstanceMutex.Dispose();
            MessageBox.Show(
                "LabPet이 이미 실행 중입니다. 시스템 트레이 아이콘을 확인해 주세요.",
                "LabPet",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _singleInstanceMutex = singleInstanceMutex;
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        var store = _serviceProvider.GetRequiredService<IAppDataStore>();
        var profile = await store.LoadProfileAsync();

        // 최초 실행 시 온보딩 마법사 진행
        if (!profile.IsOnboardingCompleted)
        {
            var onboardingVm = _serviceProvider.GetRequiredService<OnboardingViewModel>();
            onboardingVm.LoadFromProfile(profile, isEditMode: false);

            var onboardingWindow = new OnboardingWindow(
                onboardingVm,
                _serviceProvider.GetRequiredService<IPetMeshFactory>());

            var accepted = onboardingWindow.ShowDialog();
            if (accepted != true || onboardingWindow.SavedProfile is null)
            {
                Shutdown();
                return;
            }

            profile = onboardingWindow.SavedProfile;
        }

        var startupService = _serviceProvider.GetRequiredService<StartupRegistrationService>();
        var christmasTreeService = _serviceProvider.GetRequiredService<TerminalChristmasTreeService>();
        var updateService = _serviceProvider.GetRequiredService<UpdateService>();

        var isWindowsAutoStart = e.Args.Any(a =>
            string.Equals(a, StartupRegistrationService.AutoStartArgument, StringComparison.OrdinalIgnoreCase));

        // 최초 실행(AutoStartEnabled == null) 시 Windows 시작 프로그램 자동 실행을 기본값(true)으로 등록
        if (profile.AutoStartEnabled is null)
        {
            startupService.SetEnabled(true);
            profile.AutoStartEnabled = true;
            await store.SaveProfileAsync(profile);
        }
        else if (profile.AutoStartEnabled == true)
        {
            // 기존 등록 PC도 --autostart 인자가 포함되도록 레지스트리 값 최신화
            startupService.SetEnabled(true);
        }

        var petVm = _serviceProvider.GetRequiredService<PetWindowViewModel>();
        petVm.InitializeStartupState(startupService.IsEnabled());
        petVm.AttachVisitorChecker(petId => _visitorWindows.ContainsKey(petId));
        petVm.HubNavigationRequested += (_, request) => _ = NavigateHubAsync(request);
        petVm.LaunchChristmasTreeRequested += (_, _) =>
        {
            var (ok, message) = christmasTreeService.Launch(petVm.CurrentProfile.PetName);
            petVm.ShowBubble(message, ok ? 3000 : 2600);
        };
        petVm.ToggleVisitFriendRequested += (_, args) => ToggleVisitorPetWindow(args.Friend, args.Appearance);
        petVm.PetScaleChanged += (_, scale) => SyncVisitorDisplaySettings(scale, petVm.PetOpacity);
        petVm.PetOpacityChanged += (_, opacity) => SyncVisitorDisplaySettings(petVm.PetScale, opacity);
        petVm.StartupAutoRunToggled += async (_, enabled) =>
        {
            var ok = startupService.SetEnabled(enabled);
            if (ok)
            {
                petVm.CurrentProfile.AutoStartEnabled = enabled;
                await store.SaveProfileAsync(petVm.CurrentProfile);
            }
            else
            {
                petVm.InitializeStartupState(startupService.IsEnabled());
            }

            petVm.ShowBubble(ok
                ? (enabled ? "Windows 시작 시 자동 실행 등록됨" : "Windows 자동 실행 해제됨")
                : "시작 프로그램 설정을 변경하지 못했습니다.", 2400);
        };
        petVm.ApplyUpdateRestartRequested += (_, _) => updateService.ApplyUpdatesAndRestart();

        updateService.UpdateReady += (_, version) => Dispatcher.BeginInvoke(() => petVm.NotifyUpdateReady(version));
        updateService.StatusMessage += (_, msg) => Dispatcher.BeginInvoke(() => petVm.ShowBubble(msg, 3600));
        updateService.StartPeriodicChecks(silentAutoApplyOnStartup: isWindowsAutoStart);

        // LAN 피어 중 더 높은 앱 버전이 감지되면 5분 주기를 기다리지 않고 즉시 업데이트 확인
        var social = _serviceProvider.GetRequiredService<SocialCoordinatorService>();
        _social = social;
        social.NewerAppVersionDetected += (_, _) => updateService.TriggerImmediateCheckFromPeerHint();

        // 코인 게임 허브(초대 카드 · 말풍선 알림 · 수락 시 해당 게임 화면 직행)
        var gamesVm = _serviceProvider.GetRequiredService<CoinGamesViewModel>();
        gamesVm.BindProfile(profile);
        var hubNavigation = _serviceProvider.GetRequiredService<IHubNavigationService>();
        hubNavigation.HubVisibilityChanged += (_, _) => Dispatcher.BeginInvoke(() => SyncInviteCardWindow(gamesVm));
        var animator = _serviceProvider.GetRequiredService<PetAnimationController>();
        gamesVm.IncomingInviteChanged += (_, _) => Dispatcher.BeginInvoke(() => SyncInviteCardWindow(gamesVm));
        gamesVm.GameNotification += (_, msg) => Dispatcher.BeginInvoke(() => petVm.ShowBubble(msg, 3400));
        gamesVm.PetEmotionTriggered += (_, isHappy) => Dispatcher.BeginInvoke(() => animator.TriggerGameReaction(isHappy));
        gamesVm.OpenGameScreenRequested += (_, screen) => Dispatcher.BeginInvoke(() =>
            _ = NavigateHubAsync(new NavigationRequest(
                HubPage.Games,
                TargetGameScreen: screen,
                BypassUnsavedPrompt: true)));
        gamesVm.ProfileChanged += (_, updated) => Dispatcher.BeginInvoke(() => petVm.ApplyProfile(updated));

        _petWindow = _serviceProvider.GetRequiredService<PetWindow>();
        MainWindow = _petWindow;
        _messageRouter = new FriendMessageRouter(
            petId => _visitorWindows.TryGetValue(petId, out var visitor) ? visitor : null,
            () => _petWindow);
        social.FriendMessageReceived += OnSocialFriendMessageReceived;
        social.FriendStatusNotification += OnSocialFriendStatusNotification;
        _petWindow.LocationChanged += (_, _) => RepositionInviteCard();
        _petWindow.SizeChanged += (_, _) => RepositionInviteCard();
        _petWindow.InitializeWithProfile(profile);
        _petWindow.Show();
    }

    private void OnSocialFriendMessageReceived(object? sender, ReceivedFriendMessage message)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Dispatcher.BeginInvoke(() => _messageRouter?.Route(message));
    }

    private void OnSocialFriendStatusNotification(object? sender, FriendStatusNotice notice)
    {
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            Dispatcher.BeginInvoke(() => _messageRouter?.RouteStatus(notice));
        }
    }

    private Task<bool> NavigateHubAsync(NavigationRequest request)
    {
        if (_serviceProvider is null || _petWindow is null)
        {
            return Task.FromResult(false);
        }

        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.InvokeAsync(() => NavigateHubAsync(request)).Task.Unwrap();
        }

        var navigation = _serviceProvider.GetRequiredService<IHubNavigationService>();
        return navigation.NavigateAsync(request);
    }

    private void SyncInviteCardWindow(CoinGamesViewModel gamesVm)
    {
        if (_petWindow is null)
        {
            return;
        }

        var hubVisible = _serviceProvider?.GetService<IHubNavigationService>()?.IsHubVisibleAndNotMinimized == true;
        if (!gamesVm.HasIncomingInvite || hubVisible)
        {
            CloseInviteCardWindow();
            return;
        }

        if (_inviteCardWindow is null)
        {
            _inviteCardWindow = new GameInviteCardWindow(gamesVm)
            {
                Owner = _petWindow
            };
            _inviteCardWindow.Closed += (_, _) => _inviteCardWindow = null;
            _inviteCardWindow.Show();
            _inviteCardWindow.PositionNearPet(_petWindow);
        }
        else
        {
            if (!_inviteCardWindow.IsVisible)
            {
                _inviteCardWindow.Show();
            }

            _inviteCardWindow.PositionNearPet(_petWindow);
            _inviteCardWindow.ReplayEntrance();
        }
    }

    private void RepositionInviteCard()
    {
        if (_petWindow is not null && _inviteCardWindow is { IsVisible: true })
        {
            _inviteCardWindow.PositionNearPet(_petWindow);
        }
    }

    private void CloseInviteCardWindow()
    {
        if (_inviteCardWindow is not null)
        {
            var card = _inviteCardWindow;
            _inviteCardWindow = null;
            card.Close();
        }
    }

    private void ToggleVisitorPetWindow(LabPet.Shared.Models.FriendEntry friend, LabPet.Shared.Models.PetAppearance appearance)
    {
        if (_serviceProvider is null || _petWindow is null)
        {
            return;
        }

        var petVm = _serviceProvider.GetRequiredService<PetWindowViewModel>();
        if (_visitorWindows.TryGetValue(friend.PetId, out var existing))
        {
            RemoveVisitorWindow(friend.PetId);
            existing.Close();
            petVm.ShowBubble($"{SocialCoordinatorService.FormatDisplayName(friend.OwnerName, friend.PetName)}(을)를 돌려보냈어요.", 2000);
            return;
        }

        var visitor = new VisitorPetWindow(
            friend,
            appearance,
            _serviceProvider.GetRequiredService<IPetMeshFactory>(),
            _serviceProvider.GetRequiredService<IAudioAnalyzer>(),
            _serviceProvider.GetRequiredService<SocialCoordinatorService>(),
            _visitorPopupOrder,
            target => CloseOtherVisitorChatComposers(_visitorWindows.Values, target));

        visitor.ApplyDisplaySettings(petVm.PetScale, petVm.PetOpacity);
        visitor.PlaceBeside(_petWindow);
        visitor.Closing += (_, _) => RemoveVisitorWindow(friend.PetId);
        visitor.Closed += (_, _) => RemoveVisitorWindow(friend.PetId);

        _visitorWindows[friend.PetId] = visitor;
        visitor.Show();
        visitor.ShowVisitorBubble("놀러 왔어!", 2200);
        petVm.NotifyVisitorStateChanged();
    }

    internal static void CloseOtherVisitorChatComposers(IEnumerable<VisitorPetWindow> visitors, VisitorPetWindow target)
    {
        foreach (var visitor in visitors)
        {
            if (!ReferenceEquals(visitor, target) && visitor.IsChatComposerOpen)
            {
                visitor.CloseChatComposer();
            }
        }
    }

    private void RemoveVisitorWindow(string petId)
    {
        if (!_visitorWindows.Remove(petId))
        {
            return;
        }

        _serviceProvider?.GetRequiredService<PetWindowViewModel>().NotifyVisitorStateChanged();
    }

    private void SyncVisitorDisplaySettings(double scale, double opacity)
    {
        foreach (var visitor in _visitorWindows.Values)
        {
            visitor.ApplyDisplaySettings(scale, opacity);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_social is not null)
        {
            _social.FriendMessageReceived -= OnSocialFriendMessageReceived;
            _social.FriendStatusNotification -= OnSocialFriendStatusNotification;
            _social = null;
        }

        _messageRouter = null;

        CloseInviteCardWindow();
        _serviceProvider?.GetService<IHubNavigationService>()?.CloseIfOpen();

        foreach (var visitor in _visitorWindows.Values.ToList())
        {
            visitor.Close();
        }

        _visitorWindows.Clear();

        if (_serviceProvider is not null)
        {
            // IAsyncDisposable 서비스(SocialCoordinatorService, LanP2pTransport) 안전 정리
            Task.Run(async () => await _serviceProvider.DisposeAsync().ConfigureAwait(false))
                .GetAwaiter()
                .GetResult();
            _serviceProvider = null;
        }

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IStateMigrator, StateMigrator>();
        services.AddSingleton<IAppDataStore, JsonAppDataStore>();
        services.AddSingleton<HubWindowStateStore>();
        services.AddSingleton<ProceduralPetMeshFactory>();
        services.AddSingleton<IPetMeshFactory, BlenderAssetPetMeshFactory>();
        services.AddSingleton<PetPortraitRenderer>();
        services.AddSingleton<PetAnimationController>();
        services.AddSingleton<IAudioAnalyzer, LoopbackAudioAnalyzer>();
        services.AddSingleton<PetEconomyService>();
        services.AddSingleton<SystemActivityStateMonitor>();
        services.AddSingleton<ISystemActivityStateProvider>(sp => sp.GetRequiredService<SystemActivityStateMonitor>());
        services.AddSingleton<SoloProgressionService>();
        services.AddSingleton<GlobalActivityHookService>();
        services.AddSingleton<PomodoroTimerService>();

        // 통신 계층은 ISocialTransport 인터페이스로만 등록 (LAN P2P 우선 + GCP SignalR 하이브리드 라우터)
        services.AddSingleton(sp => ServerConnectionOptions.Load(sp.GetRequiredService<IAppDataStore>()));
        services.AddSingleton<DeviceIdentityStore>();
        services.AddSingleton<LanP2pTransport>();
        services.AddSingleton<GcpSignalRTransport>();
        services.AddSingleton<ISocialTransport>(sp => new HybridTransportRouter(
            sp.GetRequiredService<LanP2pTransport>(),
            sp.GetRequiredService<GcpSignalRTransport>()));
        services.AddSingleton<SocialCoordinatorService>();

        // 배포·자동 업데이트 및 Windows 시작 프로그램·터미널 연동
        services.AddSingleton<StartupRegistrationService>();
        services.AddSingleton<TerminalChristmasTreeService>();
        services.AddSingleton(new UpdateSourceOptions());
        services.AddSingleton<UpdateService>();

        services.AddSingleton<PetWindowViewModel>();
        services.AddSingleton<CoinGamesViewModel>();
        services.AddSingleton<IHubNavigationService>(sp => new HubNavigationService(() => sp.GetRequiredService<HubWindow>()));
        services.AddTransient<HubViewModel>();
        services.AddTransient<HomePageViewModel>();
        services.AddTransient<SeasonalPageViewModel>();
        services.AddTransient<SettingsPageViewModel>();
        services.AddTransient<AboutPageViewModel>();
        services.AddTransient<HomePageView>();
        services.AddTransient<GrowthPageView>();
        services.AddTransient<PomodoroPageView>();
        services.AddTransient<GamesPageView>();
        services.AddTransient<FriendsPageView>();
        services.AddTransient<AppearancePageView>();
        services.AddTransient<SeasonalPageView>();
        services.AddTransient<SettingsPageView>();
        services.AddTransient<AboutPageView>();
        services.AddTransient<Func<HubPage, System.Windows.Controls.UserControl>>(sp => page => page switch
        {
            HubPage.Home => sp.GetRequiredService<HomePageView>(),
            HubPage.Appearance => sp.GetRequiredService<AppearancePageView>(),
            HubPage.Friends => sp.GetRequiredService<FriendsPageView>(),
            HubPage.Growth => sp.GetRequiredService<GrowthPageView>(),
            HubPage.Pomodoro => sp.GetRequiredService<PomodoroPageView>(),
            HubPage.Games => sp.GetRequiredService<GamesPageView>(),
            HubPage.Seasonal => sp.GetRequiredService<SeasonalPageView>(),
            HubPage.Settings => sp.GetRequiredService<SettingsPageView>(),
            HubPage.About => sp.GetRequiredService<AboutPageView>(),
            _ => throw new ArgumentOutOfRangeException(nameof(page), page, null)
        });
        services.AddTransient<HubWindow>();
        services.AddTransient<OnboardingViewModel>();
        services.AddTransient<SoloHubViewModel>();
        services.AddTransient<SocialViewModel>();
        services.AddSingleton<PetWindow>();
    }
}
