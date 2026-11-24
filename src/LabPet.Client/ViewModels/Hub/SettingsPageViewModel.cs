using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Social;
using LabPet.Client.Storage;
using LabPet.Client.SystemIntegration;
using LabPet.Client.Views;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;

namespace LabPet.Client.ViewModels;

/// <summary>
/// 통합 창 설정 페이지 ViewModel.
/// 일반·표시·네트워크 및 방화벽·데이터 저장소 설정을 실제 서비스와 양방향 동기화한다.
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject, IDisposable
{
    private readonly PetWindowViewModel _petVm;
    private readonly SocialCoordinatorService _social;
    private readonly IAppDataStore _store;
    private readonly DispatcherTimer _displaySaveDebounceTimer;

    private bool _isSyncingFromPet;
    private bool _hasPendingDisplaySave;
    private bool _isActive;
    private bool _disposed;

    [ObservableProperty]
    private bool _isStartupAutoRunEnabled;

    [ObservableProperty]
    private double _petScale = PetProfile.DefaultPetScale;

    [ObservableProperty]
    private double _petOpacity = 1.0;

    [ObservableProperty]
    private bool _isClickThroughEnabled;

    [ObservableProperty]
    private string _connectionSummary = string.Empty;

    [ObservableProperty]
    private string _friendCodeText = string.Empty;

    [ObservableProperty]
    private FirewallRuleStatus _firewallStatus;

    [ObservableProperty]
    private string _firewallStatusBadgeText = string.Empty;

    [ObservableProperty]
    private string _firewallStatusDescription = string.Empty;

    [ObservableProperty]
    private bool _isFirewallHardened;

    [ObservableProperty]
    private string _dataDirectoryPath = string.Empty;

    [ObservableProperty]
    private string _generalStatusMessage = string.Empty;

    [ObservableProperty]
    private string _networkStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _networkStatusIsError;

    [ObservableProperty]
    private string _dataStatusMessage = string.Empty;

    public string PetScalePercentText => $"{Math.Round(PetScale * 100)}%";
    public string PetOpacityPercentText => $"{Math.Round(PetOpacity * 100)}%";

    public SettingsPageViewModel(
        PetWindowViewModel petVm,
        SocialCoordinatorService social,
        IAppDataStore store)
    {
        _petVm = petVm;
        _social = social;
        _store = store;

        _displaySaveDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _displaySaveDebounceTimer.Tick += async (_, _) => await FlushPendingDisplaySaveAsync();

        SyncFromPetVm();
        RefreshNetworkAndFirewallStatus();
        DataDirectoryPath = _store.DataDirectory;
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
            _petVm.PropertyChanged += OnPetVmPropertyChanged;
            _petVm.ProfileUpdated += OnPetProfileUpdated;
            _social.StateChanged += OnSocialStateChanged;
        }

        SyncFromPetVm();
        RefreshNetworkAndFirewallStatus();
    }

    public void Deactivate()
    {
        FlushDisplaySaveSynchronouslyOnDeactivate();

        if (!_isActive)
        {
            return;
        }

        _isActive = false;
        _petVm.PropertyChanged -= OnPetVmPropertyChanged;
        _petVm.ProfileUpdated -= OnPetProfileUpdated;
        _social.StateChanged -= OnSocialStateChanged;
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

    partial void OnIsStartupAutoRunEnabledChanged(bool value)
    {
        if (_isSyncingFromPet)
        {
            return;
        }

        _petVm.IsStartupAutoRunEnabled = value;
        if (_petVm.IsStartupAutoRunEnabled == value)
        {
            GeneralStatusMessage = value
                ? "Windows 로그인 시 LabPet을 자동으로 시작합니다."
                : "Windows 자동 시작을 해제했습니다.";
        }
        else
        {
            // 레지스트리 등록 실패 등으로 되돌려진 경우 UI를 다시 맞춘다.
            SyncFromPetVm();
            GeneralStatusMessage = "Windows 자동 시작 설정을 변경하지 못했습니다.";
        }
    }

    partial void OnPetScaleChanged(double value)
    {
        OnPropertyChanged(nameof(PetScalePercentText));
        if (_isSyncingFromPet)
        {
            return;
        }

        ApplyLiveDisplayAndScheduleSave();
    }

    partial void OnPetOpacityChanged(double value)
    {
        OnPropertyChanged(nameof(PetOpacityPercentText));
        if (_isSyncingFromPet)
        {
            return;
        }

        ApplyLiveDisplayAndScheduleSave();
    }

    partial void OnIsClickThroughEnabledChanged(bool value)
    {
        if (_isSyncingFromPet)
        {
            return;
        }

        _petVm.IsClickThroughEnabled = value;
        GeneralStatusMessage = value
            ? "클릭 투과 집중 모드가 켜졌습니다. (트레이 또는 이 화면에서 해제 가능)"
            : "클릭 투과 집중 모드를 해제했습니다.";
    }

    [RelayCommand]
    private async Task ApplyScalePresetAsync(string? scaleText)
    {
        if (!double.TryParse(scaleText, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return;
        }

        _displaySaveDebounceTimer.Stop();
        _hasPendingDisplaySave = false;

        _isSyncingFromPet = true;
        try
        {
            PetScale = PetProfile.NormalizePetScale(parsed);
            OnPropertyChanged(nameof(PetScalePercentText));
        }
        finally
        {
            _isSyncingFromPet = false;
        }

        await _petVm.ApplyDisplaySettingsAsync(PetScale, PetOpacity, persist: true);
        _petVm.ShowBubble($"펫 크기 {PetScalePercentText}", 1600);
    }

    [RelayCommand]
    private void ResetPetPosition()
    {
        _petVm.ResetPositionCommand.Execute(null);
        GeneralStatusMessage = "바탕화면 펫 위치를 기본 위치로 이동했습니다.";
    }

    [RelayCommand]
    private void RefreshFirewallStatus()
    {
        RefreshNetworkAndFirewallStatus();
        NetworkStatusIsError = false;
        NetworkStatusMessage = "방화벽 및 네트워크 연결 상태를 새로고침했습니다.";
    }

    [RelayCommand]
    private void RegisterFirewallRules()
    {
        if (FirewallRegistrationService.TryRegisterRulesElevated(out var error))
        {
            RefreshNetworkAndFirewallStatus();
            NetworkStatusIsError = false;
            NetworkStatusMessage = "로컬 서브넷 전용 Windows 방화벽 규칙 등록을 확인했습니다.";
        }
        else
        {
            RefreshNetworkAndFirewallStatus();
            NetworkStatusIsError = true;
            NetworkStatusMessage = error;
        }
    }

    [RelayCommand]
    private void CopyFirewallCommand()
    {
        try
        {
            var script = FirewallRegistrationService.BuildPowerShellRegisterCommand(Environment.ProcessPath);
            Clipboard.SetText(script);
            NetworkStatusIsError = false;
            NetworkStatusMessage = "PowerShell 관리자 등록 명령을 클립보드에 복사했습니다.";
        }
        catch (Exception ex) when (ex is ArgumentException or COMException)
        {
            NetworkStatusIsError = true;
            NetworkStatusMessage = $"명령 복사에 실패했습니다: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_store.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", _store.DataDirectory)
            {
                UseShellExecute = true
            });
            DataStatusMessage = "데이터 저장 폴더를 열었습니다.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            DataStatusMessage = $"폴더를 열지 못했습니다: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CopyDataPath()
    {
        try
        {
            Clipboard.SetText(_store.DataDirectory);
            DataStatusMessage = "데이터 저장 경로를 클립보드에 복사했습니다.";
        }
        catch (COMException ex)
        {
            DataStatusMessage = $"경로 복사에 실패했습니다: {ex.Message}";
        }
    }

    private void ApplyLiveDisplayAndScheduleSave()
    {
        _hasPendingDisplaySave = true;
        _ = _petVm.ApplyDisplaySettingsAsync(PetScale, PetOpacity, persist: false);
        _displaySaveDebounceTimer.Stop();
        _displaySaveDebounceTimer.Start();
    }

    private async Task FlushPendingDisplaySaveAsync()
    {
        _displaySaveDebounceTimer.Stop();
        if (!_hasPendingDisplaySave)
        {
            return;
        }

        _hasPendingDisplaySave = false;
        await _petVm.ApplyDisplaySettingsAsync(PetScale, PetOpacity, persist: true);
    }

    private void FlushDisplaySaveSynchronouslyOnDeactivate()
    {
        _displaySaveDebounceTimer.Stop();
        if (!_hasPendingDisplaySave)
        {
            return;
        }

        _hasPendingDisplaySave = false;
        _ = _petVm.ApplyDisplaySettingsAsync(PetScale, PetOpacity, persist: true);
    }

    private void SyncFromPetVm()
    {
        _isSyncingFromPet = true;
        try
        {
            IsStartupAutoRunEnabled = _petVm.IsStartupAutoRunEnabled;
            PetScale = PetProfile.NormalizePetScale(_petVm.PetScale);
            PetOpacity = PetDisplayLayoutHelper.NormalizeOpacity(_petVm.PetOpacity);
            IsClickThroughEnabled = _petVm.IsClickThroughEnabled;
            FriendCodeText = _petVm.CurrentProfile.FriendCode;
            OnPropertyChanged(nameof(PetScalePercentText));
            OnPropertyChanged(nameof(PetOpacityPercentText));
        }
        finally
        {
            _isSyncingFromPet = false;
        }
    }

    private void RefreshNetworkAndFirewallStatus()
    {
        ConnectionSummary = _social.ConnectionSummary;
        FriendCodeText = _petVm.CurrentProfile.FriendCode;
        FirewallStatus = FirewallRegistrationService.GetRuleStatus();
        IsFirewallHardened = FirewallStatus == FirewallRuleStatus.RegisteredHardened;

        (FirewallStatusBadgeText, FirewallStatusDescription) = FirewallStatus switch
        {
            FirewallRuleStatus.RegisteredHardened => (
                "규칙 등록됨 (로컬 서브넷 전용)",
                $"현재 LabPet 실행 파일에 대해 LocalSubnet 범위의 UDP({ProtocolConstants.UdpDiscoveryPort},{ProtocolConstants.UdpMusicFramePort}) / TCP({ProtocolConstants.TcpControlPort}) 허용 규칙이 확인되었습니다."),
            FirewallRuleStatus.RegisteredLegacy => (
                "기존 규칙 감지 (강화 권장)",
                "구형 또는 넓은 범위의 LabPet 방화벽 규칙이 감지되었습니다. 아래 버튼으로 로컬 서브넷 전용 규칙으로 교체하는 것을 권장합니다."),
            _ => (
                "미등록",
                "같은 연구실·공유기(LAN) 친구 검색이 막힌다면 아래 버튼으로 로컬 서브넷 전용 방화벽 예외 규칙을 등록해 주세요.")
        };
    }

    private void OnPetVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PetWindowViewModel.IsStartupAutoRunEnabled)
            or nameof(PetWindowViewModel.PetScale)
            or nameof(PetWindowViewModel.PetOpacity)
            or nameof(PetWindowViewModel.IsClickThroughEnabled))
        {
            SyncFromPetVm();
        }
    }

    private void OnPetProfileUpdated(object? sender, PetProfile e) => SyncFromPetVm();

    private void OnSocialStateChanged(object? sender, EventArgs e)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(() => ConnectionSummary = _social.ConnectionSummary);
        }
        else
        {
            ConnectionSummary = _social.ConnectionSummary;
        }
    }
}
