using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Rendering;
using LabPet.Client.Storage;
using LabPet.Shared.Models;

namespace LabPet.Client.ViewModels;

/// <summary>
/// 최초 실행 온보딩 마법사 및 외형·이름 편집 창 ViewModel.
/// </summary>
public sealed partial class OnboardingViewModel : ObservableObject
{
    private readonly IAppDataStore _store;
    private PetProfile _profile = new();

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
    private double _petScale = 1.0;

    public string PetScalePercentText => $"{Math.Round(PetScale * 100)}%";

    [ObservableProperty]
    private bool _autoRotatePreview = true;

    [ObservableProperty]
    private bool _isRhythmPreviewEnabled;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    public OnboardingViewModel(IAppDataStore store)
    {
        _store = store;
    }

    public void LoadFromProfile(PetProfile profile, bool isEditMode)
    {
        _profile = profile;
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
        PetScale = Math.Clamp(profile.PetScale <= 0 ? 1.0 : profile.PetScale, 0.65, 1.45);
        ValidationMessage = string.Empty;

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

    partial void OnSelectedSpeciesIdChanged(string value) => NotifyPreviewChanged();
    partial void OnSelectedBodyColorHexChanged(string value) => NotifyPreviewChanged();
    partial void OnSelectedAccentColorHexChanged(string value) => NotifyPreviewChanged();
    partial void OnSelectedExpressionIdChanged(string value) => NotifyPreviewChanged();
    partial void OnSelectedHeadAccessoryIdChanged(string value) => NotifyPreviewChanged();
    partial void OnSelectedAuraEffectIdChanged(string value) => NotifyPreviewChanged();
    partial void OnPetScaleChanged(double value) => OnPropertyChanged(nameof(PetScalePercentText));

    private void NotifyPreviewChanged()
    {
        AppearancePreviewChanged?.Invoke(this, BuildCurrentAppearance());
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
        _profile.OwnerName = trimmedOwner;
        _profile.PetName = trimmedPet;
        _profile.PetScale = Math.Clamp(PetScale, 0.65, 1.45);
        _profile.Appearance = BuildCurrentAppearance();
        _profile.IsOnboardingCompleted = true;

        await _store.SaveProfileAsync(_profile);
        ProfileSaved?.Invoke(this, _profile);
    }
}
