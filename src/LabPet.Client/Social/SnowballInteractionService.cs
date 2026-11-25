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
    private static readonly TimeSpan SendCooldown = TimeSpan.FromSeconds(1);
    private readonly Dictionary<string, long> _lastSend = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISocialTransport _transport;
    private readonly SocialCoordinatorService? _social;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, SnowballAction> _actions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, ReceivedWindow> _received = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopping;
    private readonly TimeProvider _clock;
    private int _refreshQueued;
    private volatile bool _disposed;

    internal event EventHandler<SnowballReceivedEventArgs>? Received;
    public event EventHandler<string>? InteractionRejected;

    public SnowballInteractionService(ISocialTransport transport, SocialCoordinatorService? social = null)
        : this(transport, Dispatcher.CurrentDispatcher, TimeProvider.System, social)
    {
    }

    internal SnowballInteractionService(ISocialTransport transport, Dispatcher dispatcher, TimeProvider clock, SocialCoordinatorService? social = null)
    {
        _transport = transport;
        _social = social;
        _dispatcher = dispatcher;
        _clock = clock;
        _stopping = _stop.Token;
        _transport.PeersChanged += OnPeersChanged;
        _transport.SnowballReceived += OnReceived;
        if (_social is not null)
        {
            _social.StateChanged += OnSocialStateChanged;
        }
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

    internal bool IsBlockedByPomodoroFocus(string petId)
    {
        if (_disposed)
        {
            return false;
        }

        if (_social?.IsLocalFocusing == true)
        {
            return true;
        }

        return _transport.GetDiscoveredPeers().Any(p =>
            string.Equals(p.PetId, petId, StringComparison.OrdinalIgnoreCase) && p.IsFocusing);
    }

    internal async Task<bool> SendAsync(FriendEntry friend)
        => (await SendDetailedAsync(friend)).Succeeded;

    internal async Task<TransportSendResult> SendDetailedAsync(FriendEntry friend)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || string.IsNullOrWhiteSpace(friend.PairSecretBase64))
            return TransportSendResult.Fail();
        if (IsBlockedByPomodoroFocus(friend.PetId))
        {
            InteractionRejected?.Invoke(this, FriendOperationErrorCodes.PomodoroFocusMessage);
            return TransportSendResult.Fail(FriendOperationErrorCodes.PomodoroFocusBlocked);
        }
        if (GetCooldownRemaining(friend.PetId) > TimeSpan.Zero
            || GetStatus(friend.PetId) != SnowballPeerStatus.Available)
            return TransportSendResult.Fail();
        _lastSend[friend.PetId] = Timestamp;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        attempt.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var result = await _transport.SendSnowballDetailedAsync(friend, Guid.NewGuid(), attempt.Token);
            if (result.IsPomodoroFocusBlocked)
            {
                InteractionRejected?.Invoke(this, FriendOperationErrorCodes.PomodoroFocusMessage);
            }

            return result;
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested)
        {
            return TransportSendResult.Fail();
        }
        catch (Exception ex)
        {
            CrashLogWriter.Write("SnowballInteraction.Send", ex);
            return TransportSendResult.Fail();
        }
    }

    private long Timestamp => _clock.GetTimestamp();

    internal TimeSpan GetCooldownRemaining(string petId)
    {
        _dispatcher.VerifyAccess();
        if (!_lastSend.TryGetValue(petId, out var previous)) return TimeSpan.Zero;
        var remaining = SendCooldown - _clock.GetElapsedTime(previous, Timestamp);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void OnPeersChanged(object? sender, IReadOnlyList<PeerSnapshot> peers)
        => QueueRefreshActions();

    private void OnSocialStateChanged(object? sender, EventArgs e)
        => QueueRefreshActions();

    private void QueueRefreshActions()
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

    [System.Reflection.Obfuscation(Exclude = false)]
    private void OnReceived(object? sender, SnowballReceivedEventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _social?.IsLocalFocusing == true
            || !Guid.TryParse(e.SenderPetId, out var senderId)
            || senderId == Guid.Empty || e.EventId == Guid.Empty) return;

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _social?.IsLocalFocusing == true
                || (_clock.GetUtcNow() - e.TimestampUtc).Duration() > TimeSpan.FromSeconds(10))
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
        if (_social is not null)
        {
            _social.StateChanged -= OnSocialStateChanged;
        }
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
    private readonly SnowballInteractionService _service;
    private FriendEntry _friend;
    private bool _waiting;
    private bool _failed;
    private bool _rejectedByFocus;

    internal SnowballAction(SnowballInteractionService service, FriendEntry friend)
    {
        _service = service;
        _friend = friend;
        Command = new AsyncRelayCommand(ExecuteAsync, CanSend);
    }

    public IAsyncRelayCommand Command { get; }
    public string Icon => _waiting ? "1초" : _failed ? "!" : "❄";
    public string MenuText => _service.IsBlockedByPomodoroFocus(_friend.PetId)
        ? $"❄  눈던지기 · {FriendOperationErrorCodes.PomodoroFocusMessage}"
        : _waiting ? "❄  눈던지기 · 대기" : _failed ? "❄  눈던지기 · 전송 실패" : "❄  눈던지기";
    public string Tooltip => _service.IsBlockedByPomodoroFocus(_friend.PetId) || _rejectedByFocus
        ? FriendOperationErrorCodes.PomodoroFocusMessage
        : _service.GetStatus(_friend.PetId) switch
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
        if (!_service.IsBlockedByPomodoroFocus(_friend.PetId) && !_waiting)
        {
            _rejectedByFocus = false;
        }

        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(MenuText));
        OnPropertyChanged(nameof(Tooltip));
        Command.NotifyCanExecuteChanged();
    }

    private bool CanSend() => !_waiting && !string.IsNullOrWhiteSpace(_friend.PairSecretBase64)
        && !_service.IsBlockedByPomodoroFocus(_friend.PetId)
        && _service.GetStatus(_friend.PetId) == SnowballPeerStatus.Available
        && _service.GetCooldownRemaining(_friend.PetId) == TimeSpan.Zero;

    [System.Reflection.Obfuscation(Exclude = false)]
    private async Task ExecuteAsync()
    {
        if (!CanSend()) return;
        var friend = _friend;
        _waiting = true;
        _failed = false;
        _rejectedByFocus = false;
        Refresh();
        try
        {
            var result = await _service.SendDetailedAsync(friend);
            _failed = !result.Succeeded;
            _rejectedByFocus = result.IsPomodoroFocusBlocked;
            var remaining = _service.GetCooldownRemaining(friend.PetId);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        }
        finally
        {
            _waiting = false;
            Refresh();
        }
    }
}
