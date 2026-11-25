using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views.Games;

public partial class TypingGameView : UserControl
{
    private static readonly SolidColorBrush MatchedBrush = CreateFrozenBrush(Color.FromRgb(16, 185, 129));
    private static readonly SolidColorBrush MismatchBrush = CreateFrozenBrush(Color.FromRgb(239, 68, 68));
    private static readonly SolidColorBrush MismatchBgBrush = CreateFrozenBrush(Color.FromRgb(254, 226, 226));
    private static readonly SolidColorBrush PendingBrush = CreateFrozenBrush(Color.FromRgb(107, 114, 128));

    private CoinGamesViewModel? _boundVm;

    public TypingGameView()
    {
        InitializeComponent();
        DataObject.AddPastingHandler(TypingInputBox, (_, args) =>
        {
            if (_boundVm is { IsCompetitiveTypingRound: true }) args.CancelCommand();
        });
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _boundVm = e.NewValue as CoinGamesViewModel;
        if (IsLoaded && _boundVm is not null)
        {
            _boundVm.PropertyChanged += OnViewModelPropertyChanged;
            RefreshPromptHighlighting();
            FocusTypingInputIfRunning();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
            _boundVm.PropertyChanged += OnViewModelPropertyChanged;
            RefreshPromptHighlighting();
            FocusTypingInputIfRunning();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_boundVm is null || !IsLoaded)
        {
            return;
        }

        if (e.PropertyName is nameof(CoinGamesViewModel.TypingPromptText) or nameof(CoinGamesViewModel.TypingInputText))
        {
            RefreshPromptHighlighting();
        }
        else if (e.PropertyName == nameof(CoinGamesViewModel.TypingPhase) || e.PropertyName == nameof(CoinGamesViewModel.IsTypingRunning))
        {
            RefreshPromptHighlighting();
            FocusTypingInputIfRunning();
        }
        else if (e.PropertyName == nameof(CoinGamesViewModel.TypingCountdownValue))
        {
            AnimateCountdownPulse();
        }
    }

    private void FocusTypingInputIfRunning()
    {
        if (_boundVm is null || !_boundVm.IsTypingRunning || !IsVisible)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsLoaded
                && _boundVm?.IsTypingRunning == true
                && Window.GetWindow(this)?.IsActive == true
                && !TypingInputBox.IsKeyboardFocusWithin)
            {
                TypingInputBox.Focus();
                Keyboard.Focus(TypingInputBox);
                TypingInputBox.CaretIndex = TypingInputBox.Text?.Length ?? 0;
            }
        });
    }

    private void RefreshPromptHighlighting()
    {
        PromptHighlightTextBlock.Inlines.Clear();
        if (_boundVm is null)
        {
            return;
        }

        var prompt = _boundVm.TypingPromptText ?? string.Empty;
        var typed = _boundVm.TypingInputText ?? string.Empty;

        if (string.IsNullOrEmpty(prompt))
        {
            return;
        }

        var compareLen = Math.Min(prompt.Length, typed.Length);
        var matchedPrefix = 0;
        while (matchedPrefix < compareLen && char.ToLowerInvariant(prompt[matchedPrefix]) == char.ToLowerInvariant(typed[matchedPrefix]))
        {
            matchedPrefix++;
        }

        if (matchedPrefix > 0)
        {
            PromptHighlightTextBlock.Inlines.Add(new Run(prompt[..matchedPrefix])
            {
                Foreground = MatchedBrush,
                FontWeight = FontWeights.Bold
            });
        }

        var wrongLen = Math.Clamp(typed.Length - matchedPrefix, 0, Math.Max(0, prompt.Length - matchedPrefix));
        if (wrongLen > 0)
        {
            PromptHighlightTextBlock.Inlines.Add(new Run(prompt.Substring(matchedPrefix, wrongLen))
            {
                Foreground = MismatchBrush,
                Background = MismatchBgBrush,
                FontWeight = FontWeights.Bold
            });
        }

        var remainingStart = matchedPrefix + wrongLen;
        if (remainingStart < prompt.Length)
        {
            PromptHighlightTextBlock.Inlines.Add(new Run(prompt[remainingStart..])
            {
                Foreground = PendingBrush
            });
        }
    }

    private void AnimateCountdownPulse()
    {
        if (_boundVm?.TypingPhase != TypingPhase.Countdown)
        {
            return;
        }

        var pop = new DoubleAnimation(1.18, 1.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        CountdownScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
