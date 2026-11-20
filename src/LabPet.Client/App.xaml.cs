using System.Windows;
using LabPet.Client.Audio;
using LabPet.Client.Network;
using LabPet.Client.Rendering;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Client.SystemIntegration;
using LabPet.Client.Updates;
using LabPet.Client.ViewModels;
using LabPet.Client.Views;
using LabPet.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace LabPet.Client;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Local\\LabPet_SingleInstance_Mutex";

    private Mutex? _singleInstanceMutex;
    private ServiceProvider? _serviceProvider;
    private PetWindow? _petWindow;
    private SoloHubWindow? _soloHubWindow;
    private SocialWindow? _socialWindow;
    private EnvironmentGuideWindow? _guideWindow;
    private readonly Dictionary<string, VisitorPetWindow> _visitorWindows = new(StringComparer.OrdinalIgnoreCase);

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
        petVm.OpenCustomizationRequested += OnOpenCustomizationRequested;
        petVm.OpenSoloHubRequested += OnOpenSoloHubRequested;
        petVm.OpenSocialRequested += OnOpenSocialRequested;
        petVm.OpenEnvironmentGuideRequested += OnOpenEnvironmentGuideRequested;
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

            petVm.ShowBubble(ok
                ? (enabled ? "Windows 시작 시 자동 실행 등록됨" : "Windows 자동 실행 해제됨")
                : "시작 프로그램 설정을 변경하지 못했습니다.", 2400);
        };
        petVm.CheckUpdatesRequested += async (_, _) =>
        {
            petVm.ShowBubble("새 버전을 확인 중입니다...", 2000);
            await updateService.CheckAndDownloadUpdatesAsync(isManualCheck: true);
        };
        petVm.ApplyUpdateRestartRequested += (_, _) => updateService.ApplyUpdatesAndRestart();

        updateService.UpdateReady += (_, version) => Dispatcher.BeginInvoke(() => petVm.NotifyUpdateReady(version));
        updateService.StatusMessage += (_, msg) => Dispatcher.BeginInvoke(() => petVm.ShowBubble(msg, 3600));
        updateService.StartPeriodicChecks(silentAutoApplyOnStartup: isWindowsAutoStart);

        // LAN 피어 중 더 높은 앱 버전이 감지되면 5분 주기를 기다리지 않고 즉시 업데이트 확인
        var social = _serviceProvider.GetRequiredService<SocialCoordinatorService>();
        social.NewerAppVersionDetected += (_, _) => updateService.TriggerImmediateCheckFromPeerHint();

        // 코인 게임 허브(LAN 가위바위보·타이핑 도전 수신 및 펫 반응 연결)
        var gamesVm = _serviceProvider.GetRequiredService<CoinGamesViewModel>();
        gamesVm.BindProfile(profile);
        var animator = _serviceProvider.GetRequiredService<PetAnimationController>();
        gamesVm.IncomingInviteArrived += (_, invite) => Dispatcher.BeginInvoke(() => petVm.PendingGameInvite = invite);
        gamesVm.GameNotification += (_, msg) => Dispatcher.BeginInvoke(() => petVm.ShowBubble(msg, 3400));
        gamesVm.PetEmotionTriggered += (_, isHappy) => Dispatcher.BeginInvoke(() =>
        {
            if (isHappy) animator.TriggerPetReaction();
            else animator.TriggerDizzyReaction();
        });
        gamesVm.OpenGameTabRequested += (_, _) => Dispatcher.BeginInvoke(() => OpenSoloHub(selectGamesTab: true));
        gamesVm.ProfileChanged += (_, updated) => Dispatcher.BeginInvoke(() => petVm.ApplyProfile(updated));
        petVm.GameInviteResponded += async (_, accept) =>
        {
            if (accept) await gamesVm.AcceptIncomingInviteAsync();
            else await gamesVm.DeclineIncomingInviteAsync();
        };

        _petWindow = _serviceProvider.GetRequiredService<PetWindow>();
        MainWindow = _petWindow;
        _petWindow.InitializeWithProfile(profile);
        _petWindow.Show();
    }

    private void OnOpenCustomizationRequested(object? sender, EventArgs e)
    {
        if (_serviceProvider is null || _petWindow is null)
        {
            return;
        }

        var petVm = _serviceProvider.GetRequiredService<PetWindowViewModel>();
        var onboardingVm = _serviceProvider.GetRequiredService<OnboardingViewModel>();
        onboardingVm.LoadFromProfile(petVm.CurrentProfile, isEditMode: true);

        var editWindow = new OnboardingWindow(
            onboardingVm,
            _serviceProvider.GetRequiredService<IPetMeshFactory>())
        {
            Owner = _petWindow
        };

        if (editWindow.ShowDialog() == true && editWindow.SavedProfile is not null)
        {
            petVm.ApplyProfile(editWindow.SavedProfile);
            petVm.ShowBubble("새 외형이 적용됐어요!", 2200);
        }
    }

    private void OnOpenSoloHubRequested(object? sender, EventArgs e)
        => OpenSoloHub(selectGamesTab: false);

    private void OpenSoloHub(bool selectGamesTab)
    {
        if (_serviceProvider is null || _petWindow is null)
        {
            return;
        }

        if (_soloHubWindow is not null)
        {
            if (selectGamesTab && _soloHubWindow.DataContext is SoloHubViewModel existingVm)
            {
                existingVm.SelectGamesTab();
            }

            _soloHubWindow.Activate();
            return;
        }

        var petVm = _serviceProvider.GetRequiredService<PetWindowViewModel>();
        var hubVm = _serviceProvider.GetRequiredService<SoloHubViewModel>();
        hubVm.BindProfile(petVm.CurrentProfile);
        if (selectGamesTab)
        {
            hubVm.SelectGamesTab();
        }

        hubVm.ProfileChanged += (_, updated) => petVm.ApplyProfile(updated);

        _soloHubWindow = new SoloHubWindow(hubVm)
        {
            Owner = _petWindow
        };
        _soloHubWindow.Closed += (_, _) => _soloHubWindow = null;
        _soloHubWindow.Show();
    }

    private void OnOpenSocialRequested(object? sender, EventArgs e)
    {
        if (_serviceProvider is null || _petWindow is null)
        {
            return;
        }

        if (_socialWindow is not null)
        {
            _socialWindow.Activate();
            return;
        }

        var socialVm = _serviceProvider.GetRequiredService<SocialViewModel>();
        socialVm.AttachVisitorStateChecker(petId => _visitorWindows.ContainsKey(petId));
        socialVm.ToggleVisitDesktopRequested += (_, args) => ToggleVisitorPetWindow(args.Friend, args.Appearance);

        _socialWindow = new SocialWindow(
            socialVm,
            _serviceProvider.GetRequiredService<IPetMeshFactory>())
        {
            Owner = _petWindow
        };
        _socialWindow.Closed += (_, _) => _socialWindow = null;
        _socialWindow.Show();
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
            existing.Close();
            _visitorWindows.Remove(friend.PetId);
            petVm.ShowBubble($"{SocialCoordinatorService.FormatDisplayName(friend.OwnerName, friend.PetName)}(을)를 돌려보냈어요.", 2000);
            return;
        }

        var visitor = new VisitorPetWindow(
            friend,
            appearance,
            _serviceProvider.GetRequiredService<IPetMeshFactory>(),
            _serviceProvider.GetRequiredService<IAudioAnalyzer>(),
            _serviceProvider.GetRequiredService<SocialCoordinatorService>());

        visitor.ApplyDisplaySettings(petVm.PetScale, petVm.PetOpacity);
        visitor.PlaceBeside(_petWindow);
        visitor.Closed += (_, _) =>
        {
            _visitorWindows.Remove(friend.PetId);
            if (_socialWindow?.DataContext is SocialViewModel vm)
            {
                vm.RefreshLists();
            }
        };

        _visitorWindows[friend.PetId] = visitor;
        visitor.Show();
        visitor.ShowVisitorBubble("놀러 왔어!", 2200);
    }

    private void SyncVisitorDisplaySettings(double scale, double opacity)
    {
        foreach (var visitor in _visitorWindows.Values)
        {
            visitor.ApplyDisplaySettings(scale, opacity);
        }
    }

    private void OnOpenEnvironmentGuideRequested(object? sender, EventArgs e)
    {
        if (_serviceProvider is null || _petWindow is null)
        {
            return;
        }

        if (_guideWindow is not null)
        {
            _guideWindow.Activate();
            return;
        }

        _guideWindow = new EnvironmentGuideWindow(_serviceProvider.GetRequiredService<IAppDataStore>())
        {
            Owner = _petWindow
        };
        _guideWindow.Closed += (_, _) => _guideWindow = null;
        _guideWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
        services.AddSingleton<IPetMeshFactory, ProceduralPetMeshFactory>();
        services.AddSingleton<PetAnimationController>();
        services.AddSingleton<IAudioAnalyzer, LoopbackAudioAnalyzer>();
        services.AddSingleton<SoloProgressionService>();
        services.AddSingleton<GlobalActivityHookService>();
        services.AddSingleton<PomodoroTimerService>();

        // 통신 계층은 ISocialTransport 인터페이스로만 등록 (향후 GcpSignalRTransport/HybridTransportRouter로 교체 가능)
        services.AddSingleton<ISocialTransport>(_ => new LanP2pTransport());
        services.AddSingleton<SocialCoordinatorService>();

        // 배포·자동 업데이트 및 Windows 시작 프로그램 통합
        services.AddSingleton<StartupRegistrationService>();
        services.AddSingleton(new UpdateSourceOptions());
        services.AddSingleton<UpdateService>();

        services.AddSingleton<PetWindowViewModel>();
        services.AddSingleton<CoinGamesViewModel>();
        services.AddTransient<OnboardingViewModel>();
        services.AddTransient<SoloHubViewModel>();
        services.AddTransient<SocialViewModel>();
        services.AddSingleton<PetWindow>();
    }
}
