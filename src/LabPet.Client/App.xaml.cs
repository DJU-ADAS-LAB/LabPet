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
    private bool _isUpdatePromptOpen;
    private string? _lastPromptedUpdateVersion;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "LabPet이 이미 실행 중입니다. 시스템 트레이 아이콘을 확인해 주세요.",
                "LabPet",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

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

        // 최초 실행(AutoStartEnabled == null) 시 Windows 시작 프로그램 자동 실행을 기본값(true)으로 등록
        if (profile.AutoStartEnabled is null)
        {
            startupService.SetEnabled(true);
            profile.AutoStartEnabled = true;
            await store.SaveProfileAsync(profile);
        }
        else if (profile.AutoStartEnabled == true && !startupService.IsEnabled())
        {
            startupService.SetEnabled(true);
        }

        var petVm = _serviceProvider.GetRequiredService<PetWindowViewModel>();
        petVm.InitializeStartupState(startupService.IsEnabled());
        petVm.OpenCustomizationRequested += OnOpenCustomizationRequested;
        petVm.OpenSoloHubRequested += OnOpenSoloHubRequested;
        petVm.OpenSocialRequested += OnOpenSocialRequested;
        petVm.OpenEnvironmentGuideRequested += OnOpenEnvironmentGuideRequested;
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
            // 수동 확인 시에는 이미 안내했던 버전이라도 다시 물어보도록 초기화
            _lastPromptedUpdateVersion = null;
            petVm.ShowBubble("새 버전을 확인 중입니다...", 2000);
            await updateService.CheckAndDownloadUpdatesAsync(isManualCheck: true);
        };
        petVm.ApplyUpdateRestartRequested += (_, _) => updateService.ApplyUpdatesAndRestart();

        updateService.UpdateReady += (_, version) => Dispatcher.BeginInvoke(() =>
        {
            petVm.NotifyUpdateReady(version);
            ShowUpdatePromptIfNeeded(version, updateService);
        });
        updateService.StatusMessage += (_, msg) => Dispatcher.BeginInvoke(() => petVm.ShowBubble(msg, 3600));
        updateService.StartPeriodicChecks();

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
    {
        if (_serviceProvider is null || _petWindow is null)
        {
            return;
        }

        if (_soloHubWindow is not null)
        {
            _soloHubWindow.Activate();
            return;
        }

        var petVm = _serviceProvider.GetRequiredService<PetWindowViewModel>();
        var hubVm = _serviceProvider.GetRequiredService<SoloHubViewModel>();
        hubVm.BindProfile(petVm.CurrentProfile);
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
        _socialWindow = new SocialWindow(socialVm)
        {
            Owner = _petWindow
        };
        _socialWindow.Closed += (_, _) => _socialWindow = null;
        _socialWindow.Show();
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

    private void ShowUpdatePromptIfNeeded(string version, UpdateService updateService)
    {
        if (_isUpdatePromptOpen || string.Equals(_lastPromptedUpdateVersion, version, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _isUpdatePromptOpen = true;
        _lastPromptedUpdateVersion = version;
        try
        {
            var prompt = new UpdatePromptWindow(version);
            if (_petWindow is not null && _petWindow.IsVisible)
            {
                prompt.Owner = _petWindow;
            }

            if (prompt.ShowDialog() == true)
            {
                updateService.ApplyUpdatesAndRestart();
            }
        }
        finally
        {
            _isUpdatePromptOpen = false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
        services.AddTransient<OnboardingViewModel>();
        services.AddTransient<SoloHubViewModel>();
        services.AddTransient<SocialViewModel>();
        services.AddSingleton<PetWindow>();
    }
}
