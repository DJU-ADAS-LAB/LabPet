using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Diagnostics;
using LabPet.Shared.Models;
using LabPet.Shared.Transport;

namespace LabPet.Client.Social;

/// <summary>세 발신 위치의 대상별 쿨다운과 인증 전송을 공유한다.</summary>
public sealed class SnowballInteractionService : IDisposable
{
    private readonly ISocialTransport _transport;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, SnowballAction> _actions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, ReceivedWindow> _received = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopping;
    private readonly TimeProvider _clock;
    private int _refreshQueued;
    private volatile bool _disposed;

    internal event EventHandler<SnowballReceivedEventArgs>? Received;

    public SnowballInteractionService(ISocialTransport transport)
        : this(transport, Dispatcher.CurrentDispatcher, TimeProvider.System)
    {
    }

    internal SnowballInteractionService(ISocialTransport transport, Dispatcher dispatcher, TimeProvider clock)
    {
        _transport = transport;
        _dispatcher = dispatcher;
        _clock = clock;
        _stopping = _stop.Token;
        _transport.PeersChanged += OnPeersChanged;
        _transport.SnowballReceived += OnReceived;
    }

    public SnowballAction ForFriend(FriendEntry friend)
    {
        _dispatcher.VerifyAccess();
        if (!_actions.TryGetValue(friend.PetId, out var action))
            _actions.Add(friend.PetId, action = new SnowballAction(this, friend));
        action.UpdateFriend(friend);
        return action;
    }

    internal SnowballPeerStatus GetStatus(string petId) => _disposed
        ? SnowballPeerStatus.Offline : _transport.GetSnowballPeerStatus(petId);

    internal async Task<bool> SendAsync(FriendEntry friend)
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return false;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        attempt.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            return await _transport.SendSnowballAsync(friend, Guid.NewGuid(), attempt.Token);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            CrashLogWriter.Write("SnowballInteraction.Send", ex);
            return false;
        }
    }

    internal long Timestamp => _clock.GetTimestamp();
    internal TimeSpan ElapsedSince(long timestamp) => _clock.GetElapsedTime(timestamp, _clock.GetTimestamp());

    private void OnPeersChanged(object? sender, IReadOnlyList<PeerSnapshot> peers)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _refreshQueued, 1) != 0)
            return;
        _dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (_disposed) return;
            foreach (var action in _actions.Values) action.Refresh();
        });
    }

    private void OnReceived(object? sender, SnowballReceivedEventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || !Guid.TryParse(e.SenderPetId, out var senderId)
            || senderId == Guid.Empty || e.EventId == Guid.Empty) return;

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || (_clock.GetUtcNow() - e.TimestampUtc).Duration() > TimeSpan.FromSeconds(10))
                return;
            if (!_received.TryGetValue(senderId, out var window))
                _received.Add(senderId, window = new ReceivedWindow(Timestamp));
            if (window.TryAdmit(e.EventId, Timestamp, _clock))
                Received?.Invoke(this, e);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.PeersChanged -= OnPeersChanged;
        _transport.SnowballReceived -= OnReceived;
        _stop.Cancel();
        _stop.Dispose();
    }

    // 1초마다 한 건을 보충한다. 최대 두 건의 버스트를 허용해 경로 전환의 지터를 흡수한다.
    private sealed class ReceivedWindow(long last)
    {
        private double _tokens = 2;
        private long _last = last;
        private readonly HashSet<Guid> _seen = [];
        private readonly Queue<(Guid EventId, long ReceivedAt)> _order = [];

        internal bool TryAdmit(Guid eventId, long now, TimeProvider clock)
        {
            while (_order.TryPeek(out var entry) && clock.GetElapsedTime(entry.ReceivedAt, now) > TimeSpan.FromSeconds(20))
                _seen.Remove(_order.Dequeue().EventId);
            if (_order.Count >= 64 || !_seen.Add(eventId)) return false;
            _order.Enqueue((eventId, now));
            _tokens = Math.Min(2, _tokens + clock.GetElapsedTime(_last, now).TotalSeconds);
            _last = now;
            if (_tokens < 1) return false;
            _tokens--;
            return true;
        }
    }
}

public sealed class SnowballAction : ObservableObject
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(1);
    private readonly SnowballInteractionService _service;
    private FriendEntry _friend;
    private long? _lastAttempt;
    private bool _waiting;
    private bool _failed;

    internal SnowballAction(SnowballInteractionService service, FriendEntry friend)
    {
        _service = service;
        _friend = friend;
        Command = new AsyncRelayCommand(ExecuteAsync, CanSend);
    }

    public IAsyncRelayCommand Command { get; }
    public string Icon => _waiting ? "1초" : _failed ? "!" : "❄";
    public string MenuText => _waiting ? "❄  눈던지기 · 대기" : _failed ? "❄  눈던지기 · 전송 실패" : "❄  눈던지기";
    public string Tooltip => _service.GetStatus(_friend.PetId) switch
    {
        SnowballPeerStatus.Offline => "눈던지기 · 친구가 오프라인입니다.",
        SnowballPeerStatus.Unsupported => "눈던지기 · 상대 앱 업데이트가 필요합니다.",
        _ when _waiting => "눈던지기 · 잠시 후 다시 보낼 수 있습니다.",
        _ when _failed => "눈던지기 · 전송에 실패했습니다.",
        _ => "눈던지기"
    };

    internal void UpdateFriend(FriendEntry friend)
    {
        _friend = friend;
        Refresh();
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(MenuText));
        OnPropertyChanged(nameof(Tooltip));
        Command.NotifyCanExecuteChanged();
    }

    private bool CanSend() => !_waiting && !string.IsNullOrWhiteSpace(_friend.PairSecretBase64)
        && _service.GetStatus(_friend.PetId) == SnowballPeerStatus.Available
        && (_lastAttempt is not { } sent || _service.ElapsedSince(sent) >= Cooldown);

    private async Task ExecuteAsync()
    {
        if (!CanSend()) return;
        var attemptedAt = _service.Timestamp;
        _lastAttempt = attemptedAt;
        _waiting = true;
        _failed = false;
        Refresh();
        try
        {
            _failed = !await _service.SendAsync(_friend);
            var remaining = Cooldown - _service.ElapsedSince(attemptedAt);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        }
        finally
        {
            _waiting = false;
            Refresh();
        }
    }
}
