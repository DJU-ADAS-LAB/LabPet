using System.Windows;
using System.Windows.Controls;
using LabPet.Client.Navigation;
using LabPet.Client.Rendering;
using LabPet.Client.ViewModels;
using LabPet.Shared.Models;

namespace LabPet.Client.Views.Hub.Pages;

public partial class AppearancePageView : UserControl, IHubPageLifecycle, IHubUnsavedChangesGuard, IDisposable
{
    private readonly OnboardingViewModel _viewModel;
    private readonly PetWindowViewModel _petVm;
    private readonly IPetMeshFactory _meshFactory;
    private bool _isInitialized;
    private bool _disposed;

    internal Func<bool, Task<MessageBoxResult>>? ConfirmUnsavedPromptOverride { get; set; }
    internal OnboardingViewModel EditorViewModel => _viewModel;
    internal bool IsPreviewTimerRunning => EditorControl.IsPreviewTimerRunning;

    public bool HasUnsavedChanges => !_disposed && _viewModel.HasUnsavedChanges;

    public AppearancePageView(
        OnboardingViewModel viewModel,
        PetWindowViewModel petVm,
        IPetMeshFactory meshFactory)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _petVm = petVm;
        _meshFactory = meshFactory;

        _viewModel.AttachLatestProfileProvider(() => _petVm.CurrentProfile);
        _viewModel.LoadFromProfile(_petVm.CurrentProfile, isEditMode: true);
        _viewModel.ProfileSaved += OnProfileSaved;

        EditorControl.Initialize(_viewModel, _meshFactory, startActive: false);
        _isInitialized = true;
    }

    public void OnActivated(NavigationRequest request)
    {
        if (_disposed)
        {
            return;
        }

        if (!_viewModel.HasUnsavedChanges)
        {
            _viewModel.LoadFromProfile(_petVm.CurrentProfile, isEditMode: true);
        }

        if (_isInitialized)
        {
            EditorControl.SetPreviewActive(true);
        }
    }

    public void OnDeactivated()
    {
        if (_disposed)
        {
            return;
        }

        EditorControl.SetPreviewActive(false);
    }

    public async Task<bool> ConfirmLeaveAsync(bool isClosingWindow)
    {
        if (!HasUnsavedChanges)
        {
            return true;
        }

        var decision = ConfirmUnsavedPromptOverride is not null
            ? await ConfirmUnsavedPromptOverride(isClosingWindow)
            : PromptWithMessageBox(isClosingWindow);

        switch (decision)
        {
            case MessageBoxResult.Yes:
                await _viewModel.SaveCommand.ExecuteAsync(null);
                return !_viewModel.HasUnsavedChanges;

            case MessageBoxResult.No:
                _viewModel.CancelChanges();
                return true;

            default:
                return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.ProfileSaved -= OnProfileSaved;
        EditorControl.DisposePreview();
    }

    private void OnProfileSaved(object? sender, PetProfile savedProfile)
    {
        _petVm.ApplyProfile(savedProfile);
        _petVm.ShowBubble("새 외형이 적용됐어요!", 2200);
    }

    private static MessageBoxResult PromptWithMessageBox(bool isClosingWindow)
    {
        var prompt = isClosingWindow
            ? "저장하지 않은 외형 변경사항이 있습니다.\n저장한 뒤 통합 관리 창을 닫을까요?"
            : "저장하지 않은 외형 변경사항이 있습니다.\n변경 내용을 저장하고 이동할까요?";

        return MessageBox.Show(
            prompt,
            "LabPet",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
    }
}
