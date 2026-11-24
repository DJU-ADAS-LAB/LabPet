using System.Windows.Controls;
using LabPet.Client.Navigation;
using LabPet.Client.ViewModels;
using LabPet.Client.Views.Games;

namespace LabPet.Client.Views.Hub.Pages;

public partial class GamesPageView : UserControl, IHubPageLifecycle
{
    private readonly CoinGamesViewModel _gamesVm;
    private readonly PetWindowViewModel _petVm;

    internal GamesHubView GamesHub => GamesHubControl;

    public GamesPageView(CoinGamesViewModel gamesVm, PetWindowViewModel petVm)
    {
        InitializeComponent();
        _gamesVm = gamesVm;
        _petVm = petVm;
        DataContext = _gamesVm;
    }

    public void OnActivated(NavigationRequest request)
    {
        _gamesVm.BindProfile(_petVm.CurrentProfile);
        if (request.TargetGameScreen is { } targetScreen)
        {
            _gamesVm.ActiveGameScreen = targetScreen;
        }
    }

    public void OnDeactivated()
    {
        // 다른 페이지 이동·최소화·창 닫기 시 진행 중인 게임 세션(CoinGamesViewModel)은 초기화하지 않는다.
    }
}
