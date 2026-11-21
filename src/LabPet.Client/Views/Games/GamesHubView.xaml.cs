using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views.Games;

public partial class GamesHubView : UserControl
{
    private CoinGamesViewModel? _boundVm;
    private GameScreen _mountedScreen = GameScreen.Dashboard;

    public GamesHubView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _boundVm = e.NewValue as CoinGamesViewModel;
        if (IsLoaded && IsVisible && _boundVm is not null)
        {
            _boundVm.PropertyChanged += OnViewModelPropertyChanged;
            SyncMountedScreen(_boundVm.ActiveGameScreen, animate: false);
        }
        else
        {
            ClearMountedScreen();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
            _boundVm.PropertyChanged += OnViewModelPropertyChanged;
            if (IsVisible)
            {
                SyncMountedScreen(_boundVm.ActiveGameScreen, animate: false);
            }
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (IsVisible && _boundVm is not null)
        {
            SyncMountedScreen(_boundVm.ActiveGameScreen, animate: false);
        }
        else
        {
            ClearMountedScreen();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        ClearMountedScreen();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_boundVm is not null && IsLoaded && IsVisible && e.PropertyName == nameof(CoinGamesViewModel.ActiveGameScreen))
        {
            SyncMountedScreen(_boundVm.ActiveGameScreen, animate: true);
        }
    }

    private void SyncMountedScreen(GameScreen targetScreen, bool animate)
    {
        if (targetScreen == _mountedScreen && (targetScreen == GameScreen.Dashboard || ActiveGameHost.Content is not null))
        {
            return;
        }

        _mountedScreen = targetScreen;
        UserControl? view = targetScreen switch
        {
            GameScreen.HiLow => new HiLowGameView { DataContext = _boundVm },
            GameScreen.Rps => new RpsGameView { DataContext = _boundVm },
            GameScreen.Typing => new TypingGameView { DataContext = _boundVm },
            GameScreen.Ladder => new LadderGameView { DataContext = _boundVm },
            _ => null
        };

        ActiveGameHost.Content = view;
        if (view is not null && animate)
        {
            AnimateHostEntrance();
        }
    }

    private void ClearMountedScreen()
    {
        _mountedScreen = GameScreen.Dashboard;
        ActiveGameHost.Content = null;
    }

    private void AnimateHostEntrance()
    {
        var fade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slide = new DoubleAnimation(8.0, 0.0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ActiveGameHost.BeginAnimation(OpacityProperty, fade);
        HostSlideTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
    }
}
