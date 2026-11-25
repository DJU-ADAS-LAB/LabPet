using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using LabPet.Client.Games;
using LabPet.Client.Solo;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;

namespace LabPet.Client.ViewModels;

public sealed partial class CoinGamesViewModel
{
    private const string Offer = "offer";
    private const string Registered = "registered";
    private const string Accept = "accept";
    private const string Ready = "ready";
    private const string Confirm = "confirm";
    private const string Begin = "begin";
    private const string Begun = "begun";
    private const string Decline = "decline";
    private const string Cancel = "cancel";
    private static readonly TimeSpan InviteRetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DeliveryGrace = TimeSpan.FromSeconds(4);
    private readonly ConcurrentDictionary<(string Peer, string Session), InviteExchange> _inviteExchanges = new();
    private readonly SemaphoreSlim _inviteGate = new(1, 1);
    private readonly CancellationTokenSource _inviteLifetime = new();
    private readonly CancellationToken _inviteToken;
    private string? _wagerRecoveryPetId;

    private sealed class InviteExchange(FriendEntry friend, string game, string session, int wager,
        DateTimeOffset deadline, string data, bool outgoing)
    {
        public FriendEntry Friend { get; } = friend;
        public string Game { get; } = game;
        public string Session { get; } = session;
        public int Wager { get; } = wager;
        public DateTimeOffset Deadline { get; } = deadline;
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public string Data { get; } = data;
        public bool Outgoing { get; } = outgoing;
        public GameInviteStatus Status { get; set; } = GameInviteStatus.Sending;
        public string? Reason { get; set; }
        public string? TerminalPacket { get; set; }
        public int ReservedCoins { get; set; }
        public bool TransportAcknowledged { get; set; }
        public bool OfferCompleted { get; set; }
        public bool TerminalDelivered { get; set; }
        public bool Sending { get; set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static (string, string) InviteKey(string peer, string session) => (peer.ToUpperInvariant(), session);

    private DateTimeOffset NewInviteDeadline()
        => DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(InviteExpireSeconds, 0.2, InviteWaitTimeoutSeconds));

    private static bool IsInviteLive(GameInviteStatus status) => status is GameInviteStatus.Sending
        or GameInviteStatus.Pending or GameInviteStatus.Accepting or GameInviteStatus.Ready or GameInviteStatus.Starting;

    private bool IsOnlineInviteTarget(string petId) => _social.DiscoveredPeers.Any(peer =>
        !peer.RequiresUpdate && string.Equals(peer.PetId, petId, StringComparison.OrdinalIgnoreCase));

    private void SetInviteMessage(string game, string message)
    {
        switch (game)
        {
            case RpsGameEngine.GameTypeId: RpsStatusText = message; break;
            case TypingSprintEngine.GameTypeId: TypingStatusText = message; break;
            case LabLadderEngine.GameTypeId: LadderStatusText = message; break;
        }
    }

    private static string DescribeInviteReason(string? reason) => reason switch
    {
        "offline" => "상대가 오프라인입니다.",
        "busy" => "이미 진행 중인 게임이나 초대가 있습니다.",
        "coins" => "참가에 필요한 코인이 부족합니다.",
        "expired" => "초대 시간이 만료되었습니다.",
        "unregistered" => "상대의 초대 등록 응답을 받지 못했습니다.",
        "delivery" => "네트워크 연결 문제로 초대를 전달하지 못했습니다.",
        "connection" => "연결이 끊겨 게임 시작을 확인하지 못했습니다.",
        "storage" => "참가 코인을 저장하지 못했습니다.",
        "invalid" => "유효하지 않은 게임 초대입니다.",
        "upgrade" => "친구와 게임을 하려면 양쪽 앱을 업데이트해야 합니다.",
        "declined" => "상대가 게임 초대를 거절했습니다.",
        "pomodoro_focus" => FriendOperationErrorCodes.PomodoroFocusMessage,
        _ => "게임 초대가 취소되었습니다."
    };

    private void PruneInviteExchanges()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, exchange) in _inviteExchanges)
        {
            if (!IsInviteLive(exchange.Status) && now - exchange.Deadline > TimeSpan.FromMinutes(2))
                _inviteExchanges.TryRemove(key, out _);
        }
    }

    private async Task SendTrackedOfferAsync(FriendEntry friend, string game, string session, int wager,
        DateTimeOffset deadline, string data = "")
    {
        PruneInviteExchanges();
        var exchange = new InviteExchange(friend, game, session, wager, deadline, data, outgoing: true);
        _inviteExchanges[InviteKey(friend.PetId, session)] = exchange;
        _ = WatchInviteAsync(exchange, _inviteToken);
        await SendInviteRetryAsync(exchange, Offer);
    }

    private async Task SendInvitePacketAsync(InviteExchange exchange, string action)
    {
        try
        {
            var result = await _social.SendGameEventDetailedAsync(exchange.Friend, CreateInvitePacket(exchange, action));
            if (result.IsPomodoroFocusBlocked)
            {
                exchange.TerminalDelivered = true;
                if (exchange.Outgoing && IsInviteLive(exchange.Status))
                {
                    RunOnUi(() => _ = EndInviteForPomodoroFocusAsync(exchange));
                }

                return;
            }

            var delivered = result.Succeeded;
            if (action == Offer && delivered)
                exchange.TransportAcknowledged = true;
            if (action == exchange.TerminalPacket && delivered)
                exchange.TerminalDelivered = true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("게임 초대 전송 실패 ({0}): {1}", action, ex.Message);
        }
    }

    private async Task EndInviteForPomodoroFocusAsync(InviteExchange exchange)
    {
        await _inviteGate.WaitAsync();
        try
        {
            if (_disposed || !IsInviteLive(exchange.Status))
            {
                return;
            }

            await EndInviteAsync(exchange, GameInviteStatus.Declined, "pomodoro_focus", terminalPacket: null);
        }
        finally
        {
            _inviteGate.Release();
        }
    }

    private async Task CancelPendingInvitesForPomodoroFocusAsync()
    {
        await _inviteGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            foreach (var exchange in _inviteExchanges.Values.Where(e => IsInviteLive(e.Status)).ToArray())
            {
                await EndInviteAsync(
                    exchange,
                    exchange.Outgoing ? GameInviteStatus.Canceled : GameInviteStatus.Declined,
                    "pomodoro_focus",
                    exchange.Outgoing ? Cancel : Decline);
            }
        }
        finally
        {
            _inviteGate.Release();
        }
    }

    private static GameEventPayload CreateInvitePacket(InviteExchange exchange, string action) => new()
    {
        GameType = exchange.Game,
        SessionId = exchange.Session,
        Action = action,
        WagerCoins = exchange.Wager,
        Data = action == Offer ? exchange.Data : string.Empty,
        InviteExpiresAtUtc = exchange.Deadline,
        InviteReason = exchange.Reason
    };

    private async Task WatchInviteAsync(InviteExchange exchange, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var remaining = exchange.Deadline - DateTimeOffset.UtcNow;
                var delay = remaining > TimeSpan.Zero && remaining < InviteRetryInterval ? remaining : InviteRetryInterval;
                await Task.Delay(delay, token).ConfigureAwait(false);
                if (!IsInviteLive(exchange.Status))
                    return;
                RunOnUi(() => _ = AdvanceInviteAsync(exchange));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AdvanceInviteAsync(InviteExchange exchange)
    {
        await _inviteGate.WaitAsync();
        try
        {
            if (_disposed || !IsInviteLive(exchange.Status))
                return;

            var now = DateTimeOffset.UtcNow;
            if (exchange.Outgoing && exchange.Status == GameInviteStatus.Sending
                && now - exchange.Created >= DeliveryGrace && exchange.OfferCompleted && !exchange.TransportAcknowledged)
            {
                await EndInviteAsync(exchange, GameInviteStatus.Failed,
                    IsOnlineInviteTarget(exchange.Friend.PetId) ? "delivery" : "offline", Cancel);
                return;
            }

            if (now >= exchange.Deadline)
            {
                var reason = exchange.Status switch
                {
                    GameInviteStatus.Sending when exchange.TransportAcknowledged => "unregistered",
                    GameInviteStatus.Sending => IsOnlineInviteTarget(exchange.Friend.PetId) ? "delivery" : "offline",
                    GameInviteStatus.Accepting or GameInviteStatus.Ready or GameInviteStatus.Starting => "connection",
                    _ when !exchange.Outgoing && exchange.Wager > 0 && !PetEconomyService.CanAfford(_profile, exchange.Wager) => "coins",
                    _ => "expired"
                };
                var expireStatus = !exchange.Outgoing || reason == "expired"
                    ? GameInviteStatus.Expired
                    : GameInviteStatus.Failed;
                await EndInviteAsync(exchange, expireStatus, reason, exchange.Outgoing ? Cancel : Decline);
                return;
            }

            var action = (exchange.Outgoing, exchange.Status) switch
            {
                (true, GameInviteStatus.Sending) => Offer,
                (true, GameInviteStatus.Ready) => Ready,
                (true, GameInviteStatus.Starting) => Begin,
                (false, GameInviteStatus.Accepting) => Accept,
                (false, GameInviteStatus.Ready) => Confirm,
                _ => null
            };
            if (action is not null && !exchange.Sending)
                _ = SendInviteRetryAsync(exchange, action);
        }
        finally
        {
            _inviteGate.Release();
        }
    }

    private async Task SendInviteRetryAsync(InviteExchange exchange, string action)
    {
        exchange.Sending = true;
        try
        {
            await SendInvitePacketAsync(exchange, action);
        }
        finally
        {
            if (action == Offer)
                exchange.OfferCompleted = true;
            exchange.Sending = false;
        }
    }

    private async Task<string?> ReserveInviteCoinsAsync(InviteExchange exchange, bool alreadyPaid = false)
    {
        if (exchange.Wager == 0 || alreadyPaid)
            return null;
        if (!TryReserveMultiplayerWager(exchange.Session, exchange.Wager))
            return "coins";

        exchange.ReservedCoins = exchange.Wager;
        NotifyCoinsChanged();
        if (await PersistProfileAsync())
            return null;

        await ReleaseInviteCoinsAsync(exchange);
        return "storage";
    }

    private async Task ReleaseInviteCoinsAsync(InviteExchange exchange)
    {
        var refund = exchange.ReservedCoins;
        exchange.ReservedCoins = 0;
        if (refund == 0)
            return;
        await SettleMultiplayerWagerAsync(exchange.Session, refund);
    }

    [System.Reflection.Obfuscation(Exclude = false)]
    private bool TryReserveMultiplayerWager(string session, int wager)
    {
        if (wager == 0)
            return true;
        if (string.IsNullOrWhiteSpace(session) || !WagerOptions.Contains(wager))
            return false;
        var progress = _profile.SoloProgress;
        lock (progress)
        {
            if (progress.PendingGameWagers.ContainsKey(session) || progress.Coins < wager)
                return false;
            progress.Coins -= wager;
            progress.PendingGameWagers.Add(session, wager);
            return true;
        }
    }

    [System.Reflection.Obfuscation(Exclude = false)]
    private async Task SettleMultiplayerWagerAsync(string? session, int payout)
    {
        if (session is null)
            return;
        var progress = _profile.SoloProgress;
        lock (progress)
        {
            if (!progress.PendingGameWagers.TryGetValue(session, out var reserved)
                || reserved <= 0 || payout < 0 || payout > (long)reserved * LabLadderEngine.MaxParticipants
                || progress.Coins > long.MaxValue - payout)
                return;
            progress.PendingGameWagers.Remove(session);
            progress.Coins += payout;
        }
        NotifyCoinsChanged();
        await PersistProfileAsync();
    }

    [System.Reflection.Obfuscation(Exclude = false)]
    private void RecoverInterruptedGameWagers(PetProfile profile)
    {
        if (string.Equals(_wagerRecoveryPetId, profile.PetId, StringComparison.Ordinal))
            return;
        _wagerRecoveryPetId = profile.PetId;
        var progress = profile.SoloProgress;
        lock (progress)
        {
            progress.PendingGameWagers ??= [];
            if (progress.PendingGameWagers.Count == 0)
                return;
            var refund = progress.PendingGameWagers.Values.Where(WagerOptions.Contains).Sum(static wager => (long)wager);
            progress.Coins += refund;
            progress.PendingGameWagers.Clear();
        }
        _ = PersistProfileAsync();
    }

    private async Task EndInviteAsync(InviteExchange exchange, GameInviteStatus status, string reason, string? terminalPacket)
    {
        if (!IsInviteLive(exchange.Status))
            return;

        exchange.Status = status;
        exchange.Reason = reason;
        exchange.TerminalPacket = terminalPacket;
        var message = (status, reason, terminalPacket) switch
        {
            (_, "pomodoro_focus", _) => FriendOperationErrorCodes.PomodoroFocusMessage,
            (GameInviteStatus.Declined, _, Decline) => "게임 초대를 거절했습니다.",
            (GameInviteStatus.Expired, _, Decline) => "초대 시간이 만료되었습니다.",
            (_, "canceled", Cancel) => "게임 초대를 취소했습니다.",
            (_, "coins", null) => "상대의 참가 코인이 부족합니다.",
            _ => DescribeInviteReason(reason)
        };
        if (terminalPacket is not null)
            _ = SendTerminalInvitePacketAsync(exchange, terminalPacket);

        await ReleaseInviteCoinsAsync(exchange);
        if (!exchange.Outgoing && IncomingInvite is { } current && current.SessionId == exchange.Session
            && string.Equals(current.SenderPetId, exchange.Friend.PetId, StringComparison.OrdinalIgnoreCase))
        {
            CancelIncomingInviteCountdown();
            IncomingInviteFeedbackMessage = message;
            IncomingInviteRemainingSeconds = 0;
            IncomingInviteProgressRatio = 0;
            IncomingInviteStatus = status;
            IncomingInvite = null;
            if (reason != "declined")
                GameNotification?.Invoke(this, message);
        }
        if (exchange.Outgoing)
            await CloseOutgoingInviteUiAsync(exchange, message);
        exchange.Completion.TrySetResult();
    }

    private async Task SendTerminalInvitePacketAsync(InviteExchange exchange, string action)
    {
        var end = DateTimeOffset.UtcNow + DeliveryGrace;
        try
        {
            do
            {
                await SendInvitePacketAsync(exchange, action);
                if (exchange.TerminalDelivered || _disposed)
                    return;
                await Task.Delay(InviteRetryInterval, _inviteToken);
            }
            while (DateTimeOffset.UtcNow < end);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CloseOutgoingInviteUiAsync(InviteExchange exchange, string message)
    {
        if (exchange.Game == RpsGameEngine.GameTypeId && IsRpsWaitingResponse && _rpsSessionId == exchange.Session)
        {
            CancelOutgoingWaitCountdown();
            _rpsSessionId = null;
            _rpsOpponent = null;
            IsRpsWaitingResponse = false;
            RpsPhase = RpsPhase.Setup;
            RpsStatusText = message;
            if (exchange.Reason == "pomodoro_focus")
                GameNotification?.Invoke(this, message);
        }
        else if (exchange.Game == TypingSprintEngine.GameTypeId && IsTypingWaitingResponse && _typingSessionId == exchange.Session)
        {
            CancelOutgoingWaitCountdown();
            _typingSessionId = null;
            _typingOpponent = null;
            IsTypingWaitingResponse = false;
            TypingPhase = TypingPhase.Setup;
            TypingStatusText = message;
            if (exchange.Reason == "pomodoro_focus")
                GameNotification?.Invoke(this, message);
        }
        else if (exchange.Game == LabLadderEngine.GameTypeId && IsLadderHost && _ladderRoom?.SessionId == exchange.Session
            && _ladderPendingInviteeIds.Contains(exchange.Friend.PetId))
        {
            await HandleLadderPendingInviteEndedAsync(exchange.Friend.PetId, exchange.Status, exchange.Reason ?? string.Empty, message);
            if (exchange.Reason == "pomodoro_focus")
                GameNotification?.Invoke(this, message);
        }
    }

    private async Task<bool> HandleInviteHandshakeAsync(FriendEntry friend, string name, GameEventPayload packet)
    {
        if (packet.Action == "invite")
        {
            _ = _social.SendGameEventAsync(friend, new GameEventPayload
            {
                GameType = packet.GameType, SessionId = packet.SessionId, Action = Decline,
                WagerCoins = packet.WagerCoins, InviteReason = "upgrade"
            });
            return true;
        }
        if (packet.Action is not (Offer or Registered or Accept or Ready or Confirm or Begin or Begun or Decline or Cancel))
            return false;

        await _inviteGate.WaitAsync();
        try
        {
            if (_disposed)
                return true;
            var key = InviteKey(friend.PetId, packet.SessionId);
            if (packet.Action == Offer)
            {
                if (_social.IsLocalFocusing)
                {
                    return true;
                }

                RegisterIncomingOffer(friend, name, packet, key);
                return true;
            }

            if (!_inviteExchanges.TryGetValue(key, out var exchange))
            {
                if (packet.Action == Cancel && packet.InviteExpiresAtUtc is { } deadline
                    && deadline > DateTimeOffset.UtcNow && deadline <= DateTimeOffset.UtcNow.AddSeconds(InviteWaitTimeoutSeconds + 5))
                {
                    PruneInviteExchanges();
                    if (_inviteExchanges.Count < RecentInvitationCapacity)
                        _inviteExchanges.TryAdd(key, new InviteExchange(friend, packet.GameType, packet.SessionId,
                            packet.WagerCoins, deadline, string.Empty, outgoing: false)
                        { Status = GameInviteStatus.Canceled, Reason = "canceled", TerminalPacket = Decline });
                }
                return packet.InviteExpiresAtUtc is not null || packet.Action is not (Decline or Cancel);
            }

            if (exchange.Game != packet.GameType || exchange.Wager != packet.WagerCoins)
                return true;
            if (packet.InviteExpiresAtUtc != exchange.Deadline)
                return packet.InviteExpiresAtUtc is not null || packet.Action is not (Decline or Cancel);

            if (!IsInviteLive(exchange.Status))
            {
                if (exchange.Status == GameInviteStatus.Accepted && !exchange.Outgoing && packet.Action == Begin)
                    _ = SendInvitePacketAsync(exchange, Begun);
                else if (exchange.Status == GameInviteStatus.Accepted && packet.Action == Cancel)
                    return false;
                else if (exchange.TerminalPacket is { } terminal && packet.Action is Accept or Ready or Confirm or Begin)
                    _ = SendInvitePacketAsync(exchange, terminal);
                return true;
            }

            if (packet.Action is Decline or Cancel)
            {
                var reason = packet.InviteReason ?? (packet.Action == Decline ? "declined" : "canceled");
                await EndInviteAsync(exchange, reason == "expired" ? GameInviteStatus.Expired
                    : packet.Action == Decline ? GameInviteStatus.Declined : GameInviteStatus.Canceled, reason, null);
                return true;
            }

            if (DateTimeOffset.UtcNow >= exchange.Deadline)
            {
                await EndInviteAsync(exchange, GameInviteStatus.Expired, "expired", exchange.Outgoing ? Cancel : Decline);
                return true;
            }

            await HandleLiveInvitePacketAsync(exchange, name, packet.Action, packet.InviteReason);
            return true;
        }
        finally
        {
            _inviteGate.Release();
        }
    }

    private void RegisterIncomingOffer(FriendEntry friend, string name, GameEventPayload packet, (string, string) key)
    {
        if (_inviteExchanges.TryGetValue(key, out var existing))
        {
            if (existing.Game == packet.GameType && existing.Wager == packet.WagerCoins
                && existing.Deadline == packet.InviteExpiresAtUtc && !existing.Outgoing)
            {
                var reply = existing.TerminalPacket ?? (existing.Status == GameInviteStatus.Accepted ? Begun : Registered);
                _ = SendInvitePacketAsync(existing, reply);
            }
            return;
        }
        if (packet.InviteExpiresAtUtc is not { } deadline)
            return;
        var now = DateTimeOffset.UtcNow;
        var ladder = ReadLadderOffer(packet, friend.PetId);
        var valid = packet.GameType switch
        {
            RpsGameEngine.GameTypeId or TypingSprintEngine.GameTypeId => WagerOptions.Contains(packet.WagerCoins),
            LabLadderEngine.GameTypeId => LadderFeeOptions.Contains(packet.WagerCoins) && ladder is not null,
            _ => false
        };
        var rejection = !valid || deadline > now.AddSeconds(InviteWaitTimeoutSeconds + 5) ? "invalid"
            : deadline <= now ? "expired"
            : IsAnyGameBusy || HasIncomingInvite ? "busy"
            : null;

        PruneInviteExchanges();
        var exchange = new InviteExchange(friend, packet.GameType, packet.SessionId, packet.WagerCoins,
            deadline, packet.Data ?? string.Empty, outgoing: false);
        if (rejection is not null || _inviteExchanges.Count >= RecentInvitationCapacity)
        {
            exchange.Status = GameInviteStatus.Declined;
            exchange.Reason = rejection ?? "busy";
            exchange.TerminalPacket = Decline;
            if (rejection != "invalid" && _inviteExchanges.Count < RecentInvitationCapacity)
                _inviteExchanges.TryAdd(key, exchange);
            _ = SendInvitePacketAsync(exchange, Decline);
            return;
        }

        // 코인이 부족해도 초대를 즉시 거절하지 않고 UI에 표시하며, 상대에게는 코인 부족 사유만 전달한다.
        if (packet.WagerCoins > 0 && !PetEconomyService.CanAfford(_profile, packet.WagerCoins))
        {
            exchange.Reason = "coins";
        }

        exchange.Status = GameInviteStatus.Pending;
        _inviteExchanges[key] = exchange;
        var appearance = ResolveFriendAppearance(friend.PetId, friend);
        var invite = new IncomingGameInvite(friend.PetId, name, friend, packet.GameType, packet.SessionId,
            packet.WagerCoins, exchange.Data, ladder is null ? string.Empty : LabLadderEngine.FormatPrizeSummary(ladder.Prizes),
            appearance, now, deadline);
        IncomingInvitePortrait = TryGetPortrait(appearance);
        IncomingInviteFeedbackMessage = string.Empty;
        IncomingInviteRemainingSeconds = Math.Max(1, (int)Math.Ceiling((deadline - now).TotalSeconds));
        IncomingInviteProgressRatio = 1;
        IncomingInviteStatus = GameInviteStatus.Pending;
        IncomingInvite = invite;
        OnEconomyCoinsUpdated();
        StartIncomingInviteCountdown(invite);
        _ = WatchInviteAsync(exchange, _inviteToken);
        _ = SendInvitePacketAsync(exchange, Registered);
    }

    private void UpdateOutgoingWaitUiFromAck(InviteExchange exchange, string responseStatus, string detail, bool coinsShort)
    {
        if (exchange.Game == RpsGameEngine.GameTypeId && _rpsSessionId == exchange.Session)
        {
            RpsWaitState.OpponentResponseStatusText = responseStatus;
            RpsWaitState.StatusDetailText = detail;
        }
        else if (exchange.Game == TypingSprintEngine.GameTypeId && _typingSessionId == exchange.Session)
        {
            TypingWaitState.OpponentResponseStatusText = responseStatus;
            TypingWaitState.StatusDetailText = detail;
        }
        else if (exchange.Game == LabLadderEngine.GameTypeId && _ladderRoom?.SessionId == exchange.Session)
        {
            OnLadderInviteeRegisteredAck(exchange.Friend.PetId, coinsShort);
        }
    }

    partial void OnLadderInviteeRegisteredAck(string friendPetId, bool coinsShort);

    private static LadderRoomStateDto? ReadLadderOffer(GameEventPayload packet, string sender)
        => packet.GameType == LabLadderEngine.GameTypeId
           && TryReadLadderPayload<LadderRoomStateDto>(packet.Data, out var room) && room is not null
           && room.Phase == LadderRoomPhase.WaitingInvites
           && LabLadderEngine.IsValidRoom(room, packet.SessionId, sender, packet.WagerCoins) ? room : null;

    private async Task HandleLiveInvitePacketAsync(InviteExchange exchange, string name, string action, string? inviteReason)
    {
        if (exchange.Outgoing)
        {
            if (exchange.Game == LabLadderEngine.GameTypeId
                && (!IsLadderHost || _ladderRoom?.SessionId != exchange.Session || _ladderPaidCoins != exchange.Wager))
            {
                await EndInviteAsync(exchange, GameInviteStatus.Canceled, "canceled", Cancel);
                return;
            }
            if (action == Registered && exchange.Status == GameInviteStatus.Sending)
            {
                exchange.Status = GameInviteStatus.Pending;
                var coinsShort = string.Equals(inviteReason, "coins", StringComparison.OrdinalIgnoreCase);
                var waitMessage = coinsShort
                    ? $"{name}에게 초대가 표시되었으나 현재 참가 코인이 부족합니다."
                    : $"{name}에게 초대가 표시되었습니다. 수락을 기다리는 중입니다.";
                SetInviteMessage(exchange.Game, waitMessage);
                UpdateOutgoingWaitUiFromAck(exchange, coinsShort ? "코인 부족 (대기 중)" : "수락 대기 중", waitMessage, coinsShort);
            }
            else if (action == Accept && exchange.Status is GameInviteStatus.Sending or GameInviteStatus.Pending or GameInviteStatus.Ready)
            {
                UpdateOutgoingWaitUiFromAck(exchange, "수락 · 시작 확인 중", $"{name}와 게임 시작을 확인하는 중입니다.", false);
                if (exchange.Status != GameInviteStatus.Ready)
                {
                    var failure = await ReserveInviteCoinsAsync(exchange, alreadyPaid: exchange.Game == LabLadderEngine.GameTypeId);
                    if (failure is not null || DateTimeOffset.UtcNow >= exchange.Deadline)
                    {
                        await EndInviteAsync(exchange, failure is null ? GameInviteStatus.Expired : GameInviteStatus.Failed,
                            failure ?? "expired", Cancel);
                        return;
                    }
                    exchange.Status = GameInviteStatus.Ready;
                    SetInviteMessage(exchange.Game, $"{name}와 게임 시작을 확인하는 중입니다.");
                }
                _ = SendInvitePacketAsync(exchange, Ready);
            }
            else if (action == Confirm && exchange.Status is GameInviteStatus.Ready or GameInviteStatus.Starting)
            {
                exchange.Status = GameInviteStatus.Starting;
                _ = SendInvitePacketAsync(exchange, Begin);
            }
            else if (action == Begun && exchange.Status == GameInviteStatus.Starting)
            {
                CancelOutgoingWaitCountdown();
                exchange.Status = GameInviteStatus.Accepted;
                exchange.ReservedCoins = 0;
                if (exchange.Game == LabLadderEngine.GameTypeId)
                    await HandleLadderEventAsync(exchange.Friend.PetId, name, exchange.Friend, CreateInvitePacket(exchange, Accept));
                else
                    BeginAcceptedMatch(exchange, name);
            }
        }
        else if (action == Ready && exchange.Status is GameInviteStatus.Accepting or GameInviteStatus.Ready)
        {
            exchange.Status = GameInviteStatus.Ready;
            IncomingInviteStatus = GameInviteStatus.Ready;
            _ = SendInvitePacketAsync(exchange, Confirm);
        }
        else if (action == Begin && exchange.Status == GameInviteStatus.Ready)
        {
            exchange.Status = GameInviteStatus.Accepted;
            exchange.ReservedCoins = 0;
            CancelIncomingInviteCountdown();
            IncomingInviteStatus = GameInviteStatus.Accepted;
            IncomingInvite = null;
            var screen = BeginAcceptedMatch(exchange, name);
            ActiveGameScreen = screen;
            OpenGameScreenRequested?.Invoke(this, screen);
            OpenGameTabRequested?.Invoke(this, EventArgs.Empty);
            _ = SendInvitePacketAsync(exchange, Begun);
            exchange.Completion.TrySetResult();
            if (screen == GameScreen.Ladder)
                _ = WaitForLadderAdmissionAsync(exchange);
        }
    }

    private async Task WaitForLadderAdmissionAsync(InviteExchange exchange)
    {
        var delay = exchange.Deadline - DateTimeOffset.UtcNow + InviteRetryInterval;
        if (delay > TimeSpan.Zero && !await WaitForGameTimerAsync(delay)) return;

        RunOnUi(() =>
        {
            if (!_disposed && !IsLadderHost && _ladderRoom?.SessionId == exchange.Session
                && !_ladderRoom.Members.Any(member => string.Equals(member.PetId, _profile.PetId, StringComparison.OrdinalIgnoreCase)))
                _ = CancelLadderRoomAsync();
        });
    }

    private GameScreen BeginAcceptedMatch(InviteExchange exchange, string name)
    {
        switch (exchange.Game)
        {
            case RpsGameEngine.GameTypeId:
                _rpsSessionId = exchange.Session;
                _rpsOpponent = exchange.Friend;
                _rpsActiveWager = exchange.Wager;
                BeginRpsRound(exchange.Session);
                RpsStatusText = $"{name}와 대결 시작! ({exchange.Wager} 코인) 아래에서 선택하세요!";
                return GameScreen.Rps;
            case TypingSprintEngine.GameTypeId:
                BeginTypingSprint(false, exchange.Friend, exchange.Session, exchange.Wager);
                return GameScreen.Typing;
            default:
                var room = JsonSerializer.Deserialize<LadderRoomStateDto>(exchange.Data, ProtocolJson.PacketOptions)!;
                _ladderHostFriend = exchange.Friend;
                _ladderActiveWager = exchange.Wager;
                _ladderPaidCoins = exchange.Wager;
                _ladderInitialPrizes.Clear();
                _ladderInitialPrizes.AddRange(room.Prizes);
                _ladderRoom = room;
                LadderBoardPrizesSummaryText = $"공개 상품: {LabLadderEngine.FormatPrizeSummary(room.Prizes)}";
                IsLadderHost = false;
                IsLadderRoomOpen = true;
                IsLadderMultiplayerMode = true;
                LadderStage = LadderStage.Lobby;
                LadderResultLines.Clear();
                LadderResultCards.Clear();
                RefreshLadderLobbyUi();
                LadderStatusText = $"{name}의 사다리방에 입장했습니다. 참가자가 확정되면 위치를 선택할 수 있습니다.";
                _ = WatchLadderConnectionsAsync(room.SessionId);
                return GameScreen.Ladder;
        }
    }

    private async Task CancelTrackedInviteAsync(string session, FriendEntry friend)
    {
        await _inviteGate.WaitAsync();
        try
        {
            if (_inviteExchanges.TryGetValue(InviteKey(friend.PetId, session), out var exchange) && exchange.Outgoing)
                await EndInviteAsync(exchange, GameInviteStatus.Canceled, "canceled", Cancel);
        }
        finally
        {
            _inviteGate.Release();
        }
    }

    private async Task ObserveStartedMatchAsync(FriendEntry friend, string name, GameEventPayload packet)
    {
        if (packet.Action is not ("commit" or "progress" or "finish")
            || !_inviteExchanges.TryGetValue(InviteKey(friend.PetId, packet.SessionId), out var exchange)
            || !exchange.Outgoing || exchange.Status != GameInviteStatus.Starting
            || exchange.Game != packet.GameType || exchange.Wager != packet.WagerCoins)
            return;

        // 시작 확인이 유실되어도 상대가 시작 뒤 보낸 정상 진행 패킷을 버리지 않는다.
        await HandleInviteHandshakeAsync(friend, name, CreateInvitePacket(exchange, Begun));
    }
}
