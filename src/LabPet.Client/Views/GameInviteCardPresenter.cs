using System.Windows;
using System.Windows.Threading;
using LabPet.Client.Diagnostics;
using LabPet.Client.ViewModels;

namespace LabPet.Client.Views;

/// <summary>수신 초대 상태와 펫 창에 맞춰 독립 카드를 표시한다.</summary>
internal sealed class GameInviteCardPresenter : IDisposable
{
    private readonly CoinGamesViewModel _games;
    private readonly Window _pet;
    private readonly Func<Rect> _getObstacle;
    private readonly Func<Point?>? _getHead;
    private GameInviteCardWindow? _card;
    private DispatcherOperation? _pending;
    private bool _disposed;

    internal GameInviteCardPresenter(CoinGamesViewModel games, Window pet, Func<Rect> getObstacle, Func<Point?>? getHead = null)
    {
        _games = games;
        _pet = pet;
        _getObstacle = getObstacle;
        _getHead = getHead;

        _games.IncomingInviteChanged += OnInviteChanged;
        _pet.LocationChanged += OnLocationChanged;
        _pet.SizeChanged += OnSizeChanged;
        _pet.DpiChanged += OnDpiChanged;
        _pet.IsVisibleChanged += OnVisibilityChanged;
        _pet.Closing += OnPetClosing;
        RequestUpdate();
    }

    internal void RequestUpdate()
    {
        _pet.Dispatcher.VerifyAccess();
        if (_disposed || _pending is not null || _pet.Dispatcher.HasShutdownStarted) return;
        _pending = _pet.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Update);
    }

    private void Update()
    {
        _pending = null;
        if (_disposed || _pet.Dispatcher.HasShutdownStarted) return;
        if (!_games.HasIncomingInvite || !_pet.IsVisible)
        {
            CloseCard();
            return;
        }

        try
        {
            if (_card is null)
            {
                var card = new GameInviteCardWindow(_games) { Owner = _pet };
                _card = card;
                card.CardSizeChanged += OnLocationChanged;
                card.Closed += OnCardClosed;
                card.Show();
            }
            _card?.PositionNearPet(_pet, _getObstacle(), _getHead?.Invoke());
        }
        catch (InvalidOperationException ex)
        {
            CrashLogWriter.Write("GameInviteCardPresenter.Update", ex);
            if (_card is { IsVisible: false }) CloseCard();
        }
    }

    private void OnInviteChanged(object? sender, IncomingGameInvite? e) => RequestUpdate();
    private void OnLocationChanged(object? sender, EventArgs e) => RequestUpdate();
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => RequestUpdate();
    private void OnDpiChanged(object sender, DpiChangedEventArgs e) => RequestUpdate();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => RequestUpdate();
    private void OnPetClosing(object? sender, System.ComponentModel.CancelEventArgs e) => Dispose();

    private void OnCardClosed(object? sender, EventArgs e)
    {
        if (sender is not GameInviteCardWindow card) return;
        card.CardSizeChanged -= OnLocationChanged;
        card.Closed -= OnCardClosed;
        if (ReferenceEquals(_card, card)) _card = null;
    }

    private void CloseCard()
    {
        if (_card is not { } card) return;
        OnCardClosed(card, EventArgs.Empty);
        try
        {
            card.Close();
        }
        catch (InvalidOperationException ex)
        {
            CrashLogWriter.Write("GameInviteCardPresenter.Close", ex);
        }
    }

    public void Dispose()
    {
        _pet.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _pending?.Abort();
        _pending = null;
        _games.IncomingInviteChanged -= OnInviteChanged;
        _pet.LocationChanged -= OnLocationChanged;
        _pet.SizeChanged -= OnSizeChanged;
        _pet.DpiChanged -= OnDpiChanged;
        _pet.IsVisibleChanged -= OnVisibilityChanged;
        _pet.Closing -= OnPetClosing;
        CloseCard();
    }
}
