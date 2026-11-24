using System.Windows;
using System.Windows.Input;
using LabPet.Client.Rendering;
using LabPet.Client.ViewModels;
using LabPet.Shared.Models;

namespace LabPet.Client.Views;

public partial class OnboardingWindow : Window
{
    private readonly OnboardingViewModel _viewModel;

    public PetProfile? SavedProfile { get; private set; }

    public OnboardingWindow(OnboardingViewModel viewModel, IPetMeshFactory meshFactory)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.ProfileSaved += OnProfileSaved;
        EditorControl.Initialize(_viewModel, meshFactory, startActive: true);

        Closed += (_, _) =>
        {
            _viewModel.ProfileSaved -= OnProfileSaved;
            EditorControl.DisposePreview();
        };
    }

    private void OnProfileSaved(object? sender, PetProfile profile)
    {
        SavedProfile = profile;
        DialogResult = true;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
