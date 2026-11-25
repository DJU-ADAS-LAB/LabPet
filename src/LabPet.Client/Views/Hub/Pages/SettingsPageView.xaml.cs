using System.Windows;
using System.Windows.Controls;
using LabPet.Client.Navigation;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views.Hub.Pages;

public partial class SettingsPageView : UserControl, IHubPageLifecycle, IDisposable
{
    private readonly SettingsPageViewModel _viewModel;
    private bool _disposed;

    public SettingsPageView(SettingsPageViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    public void OnActivated(NavigationRequest request)
    {
        if (!_disposed)
        {
            _viewModel.Activate();
        }
    }

    public void OnDeactivated()
    {
        _viewModel.Deactivate();
    }

    private async void OnResetGrowthClicked(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.RequiresGrowthRecovery || Window.GetWindow(this) is not { } owner) return;
        var confirm = MessageBox.Show(owner,
            "복구할 수 있는 백업이 없으면 코인과 레벨을 초기화합니다. 손상된 파일의 사본은 보존합니다. 계속할까요?",
            "성장 데이터 초기화", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm == MessageBoxResult.Yes) await _viewModel.ResetConfirmedGrowthAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.Dispose();
    }
}
