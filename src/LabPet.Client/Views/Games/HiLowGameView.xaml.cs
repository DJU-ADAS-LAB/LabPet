using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views.Games;

public partial class HiLowGameView : UserControl
{
    private CoinGamesViewModel? _boundVm;

    public HiLowGameView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.HiLowCardChanged -= OnHiLowCardChanged;
        }

        _boundVm = e.NewValue as CoinGamesViewModel;
        if (IsLoaded && _boundVm is not null)
        {
            _boundVm.HiLowCardChanged += OnHiLowCardChanged;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.HiLowCardChanged -= OnHiLowCardChanged;
            _boundVm.HiLowCardChanged += OnHiLowCardChanged;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.HiLowCardChanged -= OnHiLowCardChanged;
        }
    }

    private void OnHiLowCardChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        var flip = new DoubleAnimation(0.76, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fade = new DoubleAnimation(0.45, 1.0, TimeSpan.FromMilliseconds(160));
        CardFlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, flip);
        PlayingCardBorder.BeginAnimation(OpacityProperty, fade);

        var petPulse = new DoubleAnimation(1.12, 1.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        PetReactionScale.BeginAnimation(ScaleTransform.ScaleXProperty, petPulse);
        PetReactionScale.BeginAnimation(ScaleTransform.ScaleYProperty, petPulse);
    }
}
