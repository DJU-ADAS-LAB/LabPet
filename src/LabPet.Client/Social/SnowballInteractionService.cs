using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Diagnostics;
using LabPet.Client.Rendering;
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
    internal event EventHandler? OutgoingReady;
    internal SnowballOutgoingQueue Outgoing { get; }
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
        Outgoing = new SnowballOutgoingQueue(clock);
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

    internal async Task<TransportSendResult> SendDetailedAsync(FriendEntry friend, Guid? requestId = null)
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
            || GetStatus(friend.PetId) != SnowballPeerStatus.Available
            || !Guid.TryParse(friend.PetId, out var targetId) || targetId == Guid.Empty)
            return TransportSendResult.Fail();
        var id = requestId ?? Guid.NewGuid();
        if (id == Guid.Empty) return TransportSendResult.Fail();
        if (!Outgoing.TryReserve(id))
        {
            InteractionRejected?.Invoke(this, "눈덩이 투척이 너무 많이 대기 중입니다. 잠시 후 다시 시도해 주세요.");
            return TransportSendResult.Fail();
        }
        var sentAt = Timestamp;
        _lastSend[friend.PetId] = sentAt;
        // 전송 접수부터 대상별 1초를 계산하고 응답과 독립적으로 만료를 알린다.
        _ = RefreshAfterCooldownAsync(friend.PetId, sentAt);
        if (_actions.TryGetValue(friend.PetId, out var action)) action.Refresh();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        attempt.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var result = await _transport.SendSnowballDetailedAsync(friend, id, attempt.Token);
            if (result.IsPomodoroFocusBlocked)
            {
                InteractionRejected?.Invoke(this, FriendOperationErrorCodes.PomodoroFocusMessage);
            }

            if (!_disposed && result.Succeeded) Outgoing.Complete(id, targetId, sentAt);
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
        finally
        {
            Outgoing.Release(id);
            if (!_disposed && Outgoing.Count > 0)
            {
                try { OutgoingReady?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex) { CrashLogWriter.Write("SnowballInteraction.Outgoing", ex); }
            }
        }
    }

    private async Task RefreshAfterCooldownAsync(string petId, long sentAt)
    {
        try
        {
            // UI별 타이머 대신 서비스의 단조 증가 시계로 실제 만료를 확인한다.
            while (!_disposed && _lastSend.TryGetValue(petId, out var latest) && latest == sentAt)
            {
                var remaining = GetCooldownRemaining(petId);
                if (remaining <= TimeSpan.Zero)
                {
                    if (_actions.TryGetValue(petId, out var action)) action.Refresh();
                    return;
                }
                // 타이머 해상도로 일찍 깨어나도 남은 시간을 재확인한다.
                var delay = TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(remaining.TotalMilliseconds)));
                await Task.Delay(delay, _clock, _stopping);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception ex) { CrashLogWriter.Write("SnowballInteraction.Cooldown", ex); }
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
        Outgoing.Clear();
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
    // 네트워크 대기 상태를 저장하지 않고 대상별 공유 쿨다운만 확인한다.
    private bool _waiting => _service.GetCooldownRemaining(_friend.PetId) > TimeSpan.Zero;
    private Guid _latestRequestId;
    private bool _failed;
    private bool _rejectedByFocus;

    internal SnowballAction(SnowballInteractionService service, FriendEntry friend)
    {
        _service = service;
        _friend = friend;
        // 중복 전송은 서비스의 대상별 쿨다운으로 제한하고 응답 대기는 차단하지 않는다.
        Command = new AsyncRelayCommand(ExecuteAsync, CanSend, AsyncRelayCommandOptions.AllowConcurrentExecutions);
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
        var requestId = Guid.NewGuid();
        _latestRequestId = requestId;
        _failed = false;
        _rejectedByFocus = false;
        Refresh();
        try
        {
            var result = await _service.SendDetailedAsync(friend, requestId);
            // 이전 요청이 늦게 끝나더라도 최신 요청의 표시 상태는 바꾸지 않는다.
            if (_latestRequestId != requestId) return;
            _failed = !result.Succeeded;
            _rejectedByFocus = result.IsPomodoroFocusBlocked;
        }
        finally
        {
            if (_latestRequestId == requestId) Refresh();
        }
    }
}
