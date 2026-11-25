using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Diagnostics;
using LabPet.Client.Storage;
using LabPet.Client.Views;
using LabPet.Shared.Models;

namespace LabPet.Client.ViewModels;

/// <summary>
/// 통합 창 설정 페이지 ViewModel.
/// 일반·표시·데이터 저장소 설정을 실제 서비스와 양방향 동기화한다.
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject, IDisposable
{
    private readonly PetWindowViewModel _petVm;
    private readonly IAppDataStore _store;
    private readonly CoinGamesViewModel? _games;
    private readonly DispatcherTimer _displaySaveDebounceTimer;

    private bool _isSyncingFromPet;
    private bool _hasPendingDisplaySave;
    private bool _isActive;
    private bool _disposed;
    private bool _isRepairingGrowth;

    [ObservableProperty]
    private bool _isStartupAutoRunEnabled;

    [ObservableProperty]
    private double _petScale = PetProfile.DefaultPetScale;

    [ObservableProperty]
    private double _petOpacity = 1.0;

    // 클릭 투과 집중 모드 UI 제거에 따라 관련 상태 주석 처리
    // [ObservableProperty]
    // private bool _isClickThroughEnabled;

    [ObservableProperty]
    private string _dataDirectoryPath = string.Empty;

    [ObservableProperty]
    private string _generalStatusMessage = string.Empty;

    [ObservableProperty]
    private string _dataStatusMessage = string.Empty;

    [ObservableProperty]
    private string _growthRecoveryStatusMessage = string.Empty;

    public string PetScalePercentText => $"{Math.Round(PetScale * 100)}%";
    public string PetOpacityPercentText => $"{Math.Round(PetOpacity * 100)}%";
    public bool RequiresGrowthRecovery => _petVm.CurrentProfile.RequiresGrowthRecovery;

    public SettingsPageViewModel(
        PetWindowViewModel petVm,
        IAppDataStore store,
        CoinGamesViewModel? games = null)
    {
        _petVm = petVm;
        _store = store;
        _games = games;

        _displaySaveDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _displaySaveDebounceTimer.Tick += async (_, _) => await FlushPendingDisplaySaveAsync();

        SyncFromPetVm();
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
        }

        SyncFromPetVm();
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

    // 클릭 투과 집중 모드 UI 제거에 따라 관련 핸들러 주석 처리
    // partial void OnIsClickThroughEnabledChanged(bool value)
    // {
    //     if (_isSyncingFromPet)
    //     {
    //         return;
    //     }
    //
    //     _petVm.IsClickThroughEnabled = value;
    //     GeneralStatusMessage = value
    //         ? "클릭 투과 집중 모드가 켜졌습니다. (트레이 또는 이 화면에서 해제 가능)"
    //         : "클릭 투과 집중 모드를 해제했습니다.";
    // }

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
    private Task RetryGrowthRecoveryAsync() => RepairGrowthAsync(reset: false);

    internal Task ResetConfirmedGrowthAsync() => RepairGrowthAsync(reset: true);

    private async Task RepairGrowthAsync(bool reset)
    {
        if (_isRepairingGrowth || !RequiresGrowthRecovery) return;
        _isRepairingGrowth = true;
        var profile = _petVm.CurrentProfile;
        try
        {
            var recovered = reset ? await _store.ResetUnrecoverableGrowthAsync(profile)
                : await _store.RetryGrowthRecoveryAsync(profile);
            if (!recovered)
            {
                GrowthRecoveryStatusMessage = "읽을 수 있는 성장 백업을 찾지 못했습니다. 이 Windows 계정에서 만든 백업을 준비한 뒤 다시 시도해 주세요.";
                return;
            }

            _petVm.ApplyProfile(profile);
            _games?.BindProfile(profile);
            _games?.Economy.NotifyExternalStateChanged(profile, "growth-recovery");
            DataStatusMessage = "성장 데이터를 사용할 수 있게 됐습니다.";
            _petVm.ShowBubble(DataStatusMessage, 3200);
            OnPropertyChanged(nameof(RequiresGrowthRecovery));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLogWriter.Write("Settings.GrowthRecovery", ex);
            GrowthRecoveryStatusMessage = "성장 데이터를 복구하거나 저장하지 못했습니다. 기존 파일을 확인한 뒤 다시 시도해 주세요.";
        }
        finally
        {
            _isRepairingGrowth = false;
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
        OnPropertyChanged(nameof(RequiresGrowthRecovery));
        _isSyncingFromPet = true;
        try
        {
            IsStartupAutoRunEnabled = _petVm.IsStartupAutoRunEnabled;
            PetScale = PetProfile.NormalizePetScale(_petVm.PetScale);
            PetOpacity = PetDisplayLayoutHelper.NormalizeOpacity(_petVm.PetOpacity);
            // IsClickThroughEnabled = _petVm.IsClickThroughEnabled;
            OnPropertyChanged(nameof(PetScalePercentText));
            OnPropertyChanged(nameof(PetOpacityPercentText));
        }
        finally
        {
            _isSyncingFromPet = false;
        }
    }

    private void OnPetVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PetWindowViewModel.IsStartupAutoRunEnabled)
            or nameof(PetWindowViewModel.PetScale)
            or nameof(PetWindowViewModel.PetOpacity))
            // or nameof(PetWindowViewModel.IsClickThroughEnabled))
        {
            SyncFromPetVm();
        }
    }

    private void OnPetProfileUpdated(object? sender, PetProfile e) => SyncFromPetVm();
}
