using System.Windows;
using System.Windows.Input;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views;

public partial class SocialWindow : Window
{
    public SocialWindow(SocialViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Detach();
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
