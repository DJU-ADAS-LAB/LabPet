using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views;

public partial class SoloHubWindow : Window
{
    private readonly SoloHubViewModel _viewModel;
    private readonly DispatcherTimer _uiRefreshTimer;

    public SoloHubWindow(SoloHubViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _uiRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _uiRefreshTimer.Tick += (_, _) =>
        {
            _viewModel.RefreshAudioUi();
            _viewModel.RefreshProgressUi();
        };
        _uiRefreshTimer.Start();

        Closed += (_, _) => _uiRefreshTimer.Stop();
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
