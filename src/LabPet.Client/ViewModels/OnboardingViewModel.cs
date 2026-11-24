using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Rendering;
using LabPet.Client.Storage;
using LabPet.Client.Views;
using LabPet.Shared.Models;

namespace LabPet.Client.ViewModels;

/// <summary>
/// 최초 실행 온보딩 마법사 및 통합 창 외형 편집 페이지 ViewModel.
/// 편집 중에는 임시 상태만 수정하며 저장 시 최신 라이브 프로필의 성장·친구 데이터를 보존한 채 외형/이름/크기/투명도만 반영한다.
/// </summary>
public sealed partial class OnboardingViewModel : ObservableObject
{
    private readonly record struct AppearanceDraftSnapshot(
        string OwnerName,
        string PetName,
        string SpeciesId,
        string BodyColorHex,
        string AccentColorHex,
        string ExpressionId,
        string HeadAccessoryId,
        string AuraEffectId,
        double PetScale,
        double PetOpacity);

    private readonly IAppDataStore _store;
    private PetProfile _profile = new();
    private AppearanceDraftSnapshot _savedSnapshot;
    private Func<PetProfile>? _latestProfileProvider;
    private bool _isLoadingProfile;

    public event EventHandler<PetAppearance>? AppearancePreviewChanged;
    public event EventHandler<PetProfile>? ProfileSaved;

    public IReadOnlyList<CustomizationOption> SpeciesOptions => PetCustomizationCatalog.Species;
    public IReadOnlyList<CustomizationOption> BodyColorOptions => PetCustomizationCatalog.BodyColors;
    public IReadOnlyList<CustomizationOption> AccentColorOptions => PetCustomizationCatalog.AccentColors;
    public IReadOnlyList<CustomizationOption> ExpressionOptions => PetCustomizationCatalog.Expressions;
    public IReadOnlyList<CustomizationOption> HeadAccessoryOptions => PetCustomizationCatalog.HeadAccessories;
    public IReadOnlyList<CustomizationOption> AuraEffectOptions => PetCustomizationCatalog.AuraEffects;

    [ObservableProperty]
    private string _windowTitle = "LabPet · 나만의 3D 펫 만들기";

    [ObservableProperty]
    private string _submitButtonText = "LabPet 시작하기";

    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    private string _ownerName = string.Empty;

    [ObservableProperty]
    private string _petName = "모찌";

    [ObservableProperty]
    private string _selectedSpeciesId = PetSpeciesIds.MochiSpirit;

    [ObservableProperty]
    private string _selectedBodyColorHex = "#FFF5F7";

    [ObservableProperty]
    private string _selectedAccentColorHex = "#FF9EB5";

    [ObservableProperty]
    private string _selectedExpressionId = "default";

    [ObservableProperty]
    private string _selectedHeadAccessoryId = "none";

    [ObservableProperty]
    private string _selectedAuraEffectId = "none";

    [ObservableProperty]
    private double _petScale = PetProfile.DefaultPetScale;

    [ObservableProperty]
    private double _petOpacity = 1.0;

    public string PetScalePercentText => $"{Math.Round(PetScale * 100)}%";
    public string PetOpacityPercentText => $"{Math.Round(PetOpacity * 100)}%";

    [ObservableProperty]
    private bool _autoRotatePreview = true;

    [ObservableProperty]
    private bool _isRhythmPreviewEnabled;

    [ObservableProperty]
    private bool _isTypingPreviewEnabled;

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public OnboardingViewModel(IAppDataStore store)
    {
        _store = store;
        _savedSnapshot = CaptureCurrentDraft();
    }

    public void AttachLatestProfileProvider(Func<PetProfile> latestProfileProvider)
        => _latestProfileProvider = latestProfileProvider;

    public void LoadFromProfile(PetProfile profile, bool isEditMode)
    {
        ArgumentNullException.ThrowIfNull(profile);

        _isLoadingProfile = true;
        try
        {
            _profile = profile;
            IsEditMode = isEditMode;
            WindowTitle = isEditMode ? "LabPet · 외형 및 이름 변경" : "LabPet · 나만의 3D 펫 만들기";
            SubmitButtonText = isEditMode ? "변경 내용 저장" : "LabPet 시작하기";

            OwnerName = string.IsNullOrWhiteSpace(profile.OwnerName) ? Environment.UserName : profile.OwnerName;
            PetName = string.IsNullOrWhiteSpace(profile.PetName) ? "모찌" : profile.PetName;
            SelectedSpeciesId = profile.Appearance.SpeciesId;
            SelectedBodyColorHex = profile.Appearance.BodyColorHex;
            SelectedAccentColorHex = profile.Appearance.AccentColorHex;
            SelectedExpressionId = profile.Appearance.ExpressionId;
            SelectedHeadAccessoryId = profile.Appearance.HeadAccessoryId;
            SelectedAuraEffectId = profile.Appearance.AuraEffectId;
            PetScale = PetProfile.NormalizePetScale(profile.PetScale);
            PetOpacity = PetDisplayLayoutHelper.NormalizeOpacity(
                profile.PetOpacity <= 0 ? PetDisplayLayoutHelper.MaxPetOpacity : profile.PetOpacity);
            ValidationMessage = string.Empty;
            StatusMessage = string.Empty;

            _savedSnapshot = CaptureCurrentDraft();
            HasUnsavedChanges = false;
        }
        finally
        {
            _isLoadingProfile = false;
        }

        NotifyPreviewChanged();
    }

    public PetAppearance BuildCurrentAppearance() => new()
    {
        SpeciesId = SelectedSpeciesId,
        BodyColorHex = SelectedBodyColorHex,
        AccentColorHex = SelectedAccentColorHex,
        ExpressionId = SelectedExpressionId,
        HeadAccessoryId = SelectedHeadAccessoryId,
        AuraEffectId = SelectedAuraEffectId
    };

    partial void OnOwnerNameChanged(string value) => UpdateDirtyState();
    partial void OnPetNameChanged(string value) => UpdateDirtyState();

    partial void OnSelectedSpeciesIdChanged(string value)
    {
        if (!_isLoadingProfile)
        {
            var speciesOpt = SpeciesOptions.FirstOrDefault(s => string.Equals(s.Id, value, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(speciesOpt?.PreviewHex) &&
                BodyColorOptions.Any(c => string.Equals(c.Id, speciesOpt.PreviewHex, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedBodyColorHex = speciesOpt.PreviewHex;
            }
        }

        UpdateDirtyState();
        NotifyPreviewChanged();
    }

    partial void OnSelectedBodyColorHexChanged(string value)
    {
        UpdateDirtyState();
        NotifyPreviewChanged();
    }

    partial void OnSelectedAccentColorHexChanged(string value)
    {
        UpdateDirtyState();
        NotifyPreviewChanged();
    }

    partial void OnSelectedExpressionIdChanged(string value)
    {
        UpdateDirtyState();
        NotifyPreviewChanged();
    }

    partial void OnSelectedHeadAccessoryIdChanged(string value)
    {
        UpdateDirtyState();
        NotifyPreviewChanged();
    }

    partial void OnSelectedAuraEffectIdChanged(string value)
    {
        UpdateDirtyState();
        NotifyPreviewChanged();
    }

    partial void OnPetScaleChanged(double value)
    {
        OnPropertyChanged(nameof(PetScalePercentText));
        UpdateDirtyState();
    }

    partial void OnPetOpacityChanged(double value)
    {
        OnPropertyChanged(nameof(PetOpacityPercentText));
        UpdateDirtyState();
    }

    private void NotifyPreviewChanged()
        => AppearancePreviewChanged?.Invoke(this, BuildCurrentAppearance());

    private void UpdateDirtyState()
    {
        if (_isLoadingProfile)
        {
            return;
        }

        HasUnsavedChanges = CaptureCurrentDraft() != _savedSnapshot;
        if (HasUnsavedChanges && !string.IsNullOrEmpty(StatusMessage))
        {
            StatusMessage = string.Empty;
        }
    }

    private AppearanceDraftSnapshot CaptureCurrentDraft() => new(
        (OwnerName ?? string.Empty).Trim(),
        (PetName ?? string.Empty).Trim(),
        SelectedSpeciesId,
        SelectedBodyColorHex,
        SelectedAccentColorHex,
        SelectedExpressionId,
        SelectedHeadAccessoryId,
        SelectedAuraEffectId,
        PetProfile.NormalizePetScale(PetScale),
        PetDisplayLayoutHelper.NormalizeOpacity(PetOpacity <= 0 ? PetDisplayLayoutHelper.MaxPetOpacity : PetOpacity));

    [RelayCommand]
    public void CancelChanges()
    {
        _isLoadingProfile = true;
        try
        {
            OwnerName = _savedSnapshot.OwnerName;
            PetName = _savedSnapshot.PetName;
            SelectedSpeciesId = _savedSnapshot.SpeciesId;
            SelectedBodyColorHex = _savedSnapshot.BodyColorHex;
            SelectedAccentColorHex = _savedSnapshot.AccentColorHex;
            SelectedExpressionId = _savedSnapshot.ExpressionId;
            SelectedHeadAccessoryId = _savedSnapshot.HeadAccessoryId;
            SelectedAuraEffectId = _savedSnapshot.AuraEffectId;
            PetScale = _savedSnapshot.PetScale;
            PetOpacity = _savedSnapshot.PetOpacity;
            ValidationMessage = string.Empty;
            StatusMessage = "변경사항을 취소하고 기존 외형으로 되돌렸습니다.";
            HasUnsavedChanges = false;
        }
        finally
        {
            _isLoadingProfile = false;
        }

        NotifyPreviewChanged();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var trimmedOwner = (OwnerName ?? string.Empty).Trim();
        if (trimmedOwner.Length is < 1 or > 16)
        {
            ValidationMessage = "내 이름은 1~16자 사이로 입력해 주세요.";
            return;
        }

        var trimmedPet = (PetName ?? string.Empty).Trim();
        if (trimmedPet.Length is < 1 or > 16)
        {
            ValidationMessage = "펫 이름은 1~16자 사이로 입력해 주세요.";
            return;
        }

        ValidationMessage = string.Empty;

        // 편집 도중 쌓인 최신 XP·코인·활동량·친구 관계 등을 덮어쓰지 않도록 최신 라이브 프로필을 기준으로 외형만 반영한다.
        var target = _latestProfileProvider?.Invoke() ?? _profile;
        target.OwnerName = trimmedOwner;
        target.PetName = trimmedPet;
        target.PetScale = PetProfile.NormalizePetScale(PetScale);
        target.PetOpacity = PetDisplayLayoutHelper.NormalizeOpacity(PetOpacity <= 0 ? PetDisplayLayoutHelper.MaxPetOpacity : PetOpacity);
        target.Appearance = BuildCurrentAppearance();
        target.IsOnboardingCompleted = true;

        _profile = target;
        await _store.SaveProfileAsync(target);

        _savedSnapshot = CaptureCurrentDraft();
        HasUnsavedChanges = false;
        StatusMessage = IsEditMode ? "변경사항을 저장해 바탕화면 펫에 반영했습니다." : string.Empty;
        ProfileSaved?.Invoke(this, target);
    }
}
