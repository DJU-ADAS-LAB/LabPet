using System.Windows.Controls;
using LabPet.Client.Navigation;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views.Hub.Pages;

public partial class SeasonalPageView : UserControl, IHubPageLifecycle
{
    public SeasonalPageView(SeasonalPageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    public void OnActivated(NavigationRequest request)
    {
    }

    public void OnDeactivated()
    {
    }
}
