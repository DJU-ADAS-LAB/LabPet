using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Updates;

namespace LabPet.Client.ViewModels;

/// <summary>
/// LabPet에서 사용 중인 오픈소스 패키지 메타데이터.
/// </summary>
public sealed record OpenSourcePackageInfo(
    string Name,
    string Version,
    string License,
    string Purpose);

/// <summary>
/// 통합 창 정보(About) 페이지 ViewModel.
/// 버전 정보, Velopack 업데이트 확인·적용, 저장소 링크, 오픈소스 라이선스를 제공한다.
/// </summary>
public sealed partial class AboutPageViewModel : ObservableObject, IDisposable
{
    private const string DefaultUpdateStatus = "최신 업데이트를 확인하려면 아래 버튼을 눌러 주세요.";
    private readonly PetWindowViewModel _petVm;
    private readonly UpdateService _updateService;
    private readonly UpdateSourceOptions _updateOptions;
    private readonly Dispatcher? _dispatcher = Dispatcher.FromThread(Thread.CurrentThread);

    private bool _isActive;
    private bool _disposed;

    [ObservableProperty]
    private string _currentVersionDisplay = string.Empty;

    [ObservableProperty]
    private string _updateStatusText = DefaultUpdateStatus;

    [ObservableProperty]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private bool _isUpdateReady;

    [ObservableProperty]
    private bool _hasUpdateOffer;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private bool _canInstallUpdate;

    [ObservableProperty]
    private string _pendingVersionDisplay = string.Empty;

    [ObservableProperty]
    private string _linkFeedbackMessage = string.Empty;

    public string RepositoryUrl => _updateOptions.GitHubRepoUrl;
    public string ReleasesUrl => $"{_updateOptions.GitHubRepoUrl.TrimEnd('/')}/releases";

    public IReadOnlyList<OpenSourcePackageInfo> OpenSourcePackages { get; } =
    [
        new("CommunityToolkit.Mvvm", "8.4.0", "MIT", "MVVM ObservableObject · RelayCommand 소스 생성"),
        new("H.NotifyIcon.Wpf", "2.3.0", "MIT", "Windows 시스템 트레이 아이콘 및 컨텍스트 메뉴"),
        new("HelixToolkit.Wpf", "3.1.2", "MIT", "순수 WPF Viewport3D 보조 렌더링 유틸리티"),
        new("Microsoft.AspNetCore.SignalR.Client", "10.0.15", "MIT", "원격 친구 릴레이(GCP SignalR) 클라이언트 통신"),
        new("Microsoft.Extensions.DependencyInjection", "10.0.15", "MIT", "서비스 · ViewModel 의존성 주입 컨테이너"),
        new("NAudio", "2.2.1", "MIT", "WASAPI 시스템 오디오 루프백 캡처 및 FFT 분석"),
        new("SharpGLTF.Core", "1.0.7", "MIT", "Blender GLB 3D 캐릭터 스켈레톤 · 애니메이션 로드"),
        new("Velopack", "1.2.161", "MIT", "차분 자동 업데이트 및 패키지 설치 수명주기")
    ];

    public AboutPageViewModel(
        PetWindowViewModel petVm,
        UpdateService updateService,
        UpdateSourceOptions? updateOptions = null)
    {
        _petVm = petVm;
        _updateService = updateService;
        _updateOptions = updateOptions ?? new UpdateSourceOptions();
        SyncUpdateState();
    }

    public void Activate()
    {
        if (_disposed)
        {
            return;
        }

        if (!_isActive)
        {
            _isActive = true;
            _updateService.StateChanged += OnUpdateStateChanged;
        }

        SyncUpdateState();
    }

    public void Deactivate()
    {
        if (!_isActive)
        {
            return;
        }

        _isActive = false;
        _updateService.StateChanged -= OnUpdateStateChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Deactivate();
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (IsCheckingUpdates || _disposed)
        {
            return;
        }

        IsCheckingUpdates = true;
        UpdateStatusText = "업데이트를 확인하는 중입니다...";
        try
        {
            var message = _updateService.State.IsBusy ? null : await _updateService.CheckForUpdatesAsync();
            SyncUpdateState();
            if (message is not null)
            {
                UpdateStatusText = message;
            }
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private Task InstallUpdateAsync() => _updateService.InstallUpdateAsync();

    [RelayCommand]
    private void OpenRepository() => TryOpenExternalUrl(RepositoryUrl);

    [RelayCommand]
    private void OpenReleases() => TryOpenExternalUrl(ReleasesUrl);

    private void TryOpenExternalUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            LinkFeedbackMessage = "허용되지 않은 URL 형식입니다.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            LinkFeedbackMessage = string.Empty;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            LinkFeedbackMessage = $"브라우저를 열지 못했습니다: {ex.Message}";
        }
    }

    private void SyncUpdateState()
    {
        var rawVersion = (_petVm.CurrentVersionText ?? string.Empty).Trim();
        if (rawVersion.StartsWith("현재 버전 ", StringComparison.Ordinal))
        {
            rawVersion = rawVersion["현재 버전 ".Length..].Trim();
        }

        CurrentVersionDisplay = string.IsNullOrEmpty(rawVersion)
            ? "v0.0.0"
            : rawVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? rawVersion
                : $"v{rawVersion}";

        var state = _updateService.State;
        var wasShowingOffer = HasUpdateOffer;
        HasUpdateOffer = state.Version is not null;
        IsUpdateReady = state.Phase == UpdatePhase.Ready;
        CanInstallUpdate = state.CanInstall;
        PendingVersionDisplay = state.Version is { } version ? $"v{version}" : string.Empty;
        if (state.Message is { } message)
        {
            UpdateStatusText = message;
        }
        else if (wasShowingOffer)
        {
            UpdateStatusText = DefaultUpdateStatus;
        }
    }

    private void OnUpdateStateChanged(object? sender, EventArgs e)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            SyncUpdateState();
        }
        else if (!_dispatcher.HasShutdownStarted)
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (_isActive)
                {
                    SyncUpdateState();
                }
            });
        }
    }
}
