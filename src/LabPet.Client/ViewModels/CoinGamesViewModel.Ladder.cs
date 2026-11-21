using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Games;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;

namespace LabPet.Client.ViewModels;

public sealed partial class CoinGamesViewModel
{
    // ══════════════════════════════════════════════════════════════════════
    // 4. 연구실 공동 사다리타기 (초대 응답 -> 참가자 확정 -> 위치 선택 -> 공정 사다리 -> 도착 공개)
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task OpenLadderRoomAsync()
    {
        if (IsLadderRoomOpen || IsLadderAnimating)
        {
            return;
        }

        if (OnlineFriends.Count == 0)
        {
            LadderStatusText = "초대할 온라인 친구가 없습니다. 혼자 바로 돌리기를 이용해 주세요.";
            return;
        }

        if (!LabLadderEngine.TryParsePrizeInput(LadderPrizesText, out var parsedPrizes, out var prizeError))
        {
            LadderStatusText = prizeError;
            return;
        }

        if (!LadderFeeOptions.Contains(LadderEntryFee))
        {
            return;
        }

        var invitees = OnlineFriends.Take(LabLadderEngine.MaxParticipants - 1).ToList();
        var initialSlots = Math.Clamp(1 + invitees.Count, LabLadderEngine.MinParticipants, LabLadderEngine.MaxParticipants);
        var initialPrizes = LabLadderEngine.NormalizePrizesForCount(parsedPrizes, initialSlots);
        var wager = LadderEntryFee;

        if (wager > 0 && initialPrizes.Count(LabLadderEngine.IsJackpot) > 1)
        {
            LadderStatusText = "유료 사다리에서는 잭팟을 한 칸까지만 지정할 수 있습니다.";
            return;
        }

        if (wager > 0)
        {
            var (ok, msg) = _progression.TrySpendCoins(_profile, wager);
            if (!ok)
            {
                LadderStatusText = msg;
                return;
            }
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var myName = string.IsNullOrWhiteSpace(_profile.OwnerName) ? _profile.PetName : _profile.OwnerName;

        _ladderPendingInviteeIds.Clear();
        _ladderPendingInvitees.Clear();
        _ladderJoinedFriends.Clear();
        _ladderInitialPrizes.Clear();
        _ladderInitialPrizes.AddRange(initialPrizes);
        _ladderHostFriend = null;
        _ladderActiveWager = wager;
        _ladderPaidCoins = wager;
        _ladderStarting = false;

        foreach (var choice in invitees)
        {
            _ladderPendingInviteeIds.Add(choice.Friend.PetId);
            _ladderPendingInvitees[choice.Friend.PetId] = choice.Friend;
        }

        // 조기 수락(accept) 패킷이 도착해도 유실되지 않도록 초대 전송 전에 _ladderRoom을 먼저 초기화한다.
        _ladderRoom = new LadderRoomStateDto
        {
            SessionId = sessionId,
            HostPetId = _profile.PetId,
            Phase = LadderRoomPhase.WaitingInvites,
            WagerCoins = wager,
            SlotCount = initialSlots,
            PendingInviteCount = invitees.Count,
            Prizes = initialPrizes.ToList(),
            Members =
            [
                new LadderMemberStateDto
                {
                    PetId = _profile.PetId,
                    DisplayName = myName,
                    SelectedSlot = null
                }
            ]
        };

        IsLadderHost = true;
        IsLadderRoomOpen = true;
        LadderResultLines.Clear();
        LadderBoardPrizesSummaryText = $"공개 상품: {LabLadderEngine.FormatPrizeSummary(initialPrizes)}";
        RefreshLadderLobbyUi();

        if (wager > 0)
        {
            await PersistProfileAsync();
        }

        if (_ladderRoom?.SessionId != sessionId)
        {
            return;
        }

        var initialRoomJson = JsonSerializer.Serialize(_ladderRoom, ProtocolJson.PacketOptions);
        var sendTasks = invitees.Select(async choice =>
        {
            var sent = await _social.SendGameEventAsync(choice.Friend, new GameEventPayload
            {
                GameType = LabLadderEngine.GameTypeId,
                SessionId = sessionId,
                Action = "invite",
                WagerCoins = wager,
                Data = initialRoomJson
            });
            return (choice.Friend.PetId, sent);
        }).ToList();

        var results = await Task.WhenAll(sendTasks);
        if (_ladderRoom is null || _ladderRoom.SessionId != sessionId)
        {
            return;
        }

        var sentCount = 0;
        foreach (var (petId, sent) in results)
        {
            if (sent)
            {
                sentCount++;
            }
            else
            {
                _ladderPendingInviteeIds.Remove(petId);
                _ladderPendingInvitees.Remove(petId);
            }
        }

        if (sentCount == 0 && _ladderJoinedFriends.Count == 0)
        {
            var refunded = await RefundLadderEntryFeeIfPaidAsync();
            CloseLadderRoomState(refunded > 0
                ? "온라인 친구에게 초대를 전송하지 못해 참가 코인을 반환했습니다."
                : "온라인 친구에게 초대를 전송하지 못했습니다.");
            return;
        }

        _ladderRoom.PendingInviteCount = _ladderPendingInviteeIds.Count;
        RecalculateHostSlotCountAndPrizes();
        RefreshLadderLobbyUi();
        await BroadcastLadderStateOrAbortAsync();
        if (_ladderRoom?.SessionId != sessionId)
        {
            return;
        }

        if (_ladderRoom.PendingInviteCount > 0)
        {
            StartLadderInviteTimeout(sessionId);
            LadderStatusText = $"친구 {sentCount}명에게 초대 발송! 응답 완료 후 '참가자 확정'을 눌러 주세요.";
        }
        else
        {
            LadderStatusText = "모든 초대에 대한 응답이 완료되었습니다. '참가자 확정'을 눌러 주세요.";
        }
    }

    [RelayCommand]
    private async Task ConfirmLadderParticipantsAsync()
    {
        if (!IsLadderRoomOpen || !IsLadderHost || _ladderStarting || _ladderRoom is null)
        {
            return;
        }

        if (!LabLadderEngine.CanConfirmParticipants(_ladderRoom))
        {
            LadderStatusText = _ladderRoom.PendingInviteCount > 0
                ? $"응답 대기 중인 친구({_ladderRoom.PendingInviteCount}명)가 모두 응답해야 참가자를 확정할 수 있습니다."
                : "최소 2명 이상 참가해야 명단을 확정할 수 있습니다.";
            return;
        }

        CancelLadderInviteTimeout();
        if (!LabLadderEngine.TryConfirmParticipants(_ladderRoom, _ladderInitialPrizes))
        {
            return;
        }

        LadderBoardPrizesSummaryText = $"공개 상품: {LabLadderEngine.FormatPrizeSummary(_ladderRoom.Prizes)}";
        RefreshLadderLobbyUi();
        LadderStatusText = "참가자가 확정되었습니다! 각자 원하는 시작 번호를 골라 주세요.";
        await BroadcastLadderStateOrAbortAsync();
    }

    [RelayCommand]
    private async Task PickLadderSlotAsync(int slotIndex)
    {
        if (!IsLadderRoomOpen || _ladderStarting || _ladderRoom is null)
        {
            return;
        }

        if (_ladderRoom.Phase != LadderRoomPhase.PickingSlots)
        {
            LadderStatusText = "방장이 참가자를 확정한 뒤에 시작 위치를 선택할 수 있습니다.";
            return;
        }

        if (IsLadderHost)
        {
            if (!LabLadderEngine.TryPickSlot(_ladderRoom, _profile.PetId, slotIndex))
            {
                LadderStatusText = $"{slotIndex + 1}번 자리는 이미 다른 참가자가 선택했습니다.";
                return;
            }

            RefreshLadderLobbyUi();
            LadderStatusText = LabLadderEngine.CanStartGame(_ladderRoom)
                ? "모든 참가자가 위치를 선택했습니다! '게임 시작'을 눌러 사다리를 생성하세요."
                : $"{slotIndex + 1}번 자리를 선택했습니다. 다른 참가자의 위치 선택을 기다리는 중...";
            await BroadcastLadderStateOrAbortAsync();
        }
        else if (_ladderHostFriend is not null)
        {
            var sessionId = _ladderRoom.SessionId;
            var sent = await _social.SendGameEventAsync(_ladderHostFriend, new GameEventPayload
            {
                GameType = LabLadderEngine.GameTypeId,
                SessionId = sessionId,
                Action = "pick",
                WagerCoins = _ladderActiveWager,
                Data = slotIndex.ToString()
            });

            if (!sent && _ladderRoom?.SessionId == sessionId)
            {
                var refunded = await RefundLadderEntryFeeIfPaidAsync();
                var msg = refunded > 0
                    ? "방장과의 연결이 끊겨 사다리방을 취소하고 참가 코인을 반환했습니다."
                    : "방장과의 연결이 끊겨 사다리방이 취소되었습니다.";
                CloseLadderRoomState(msg);
                GameNotification?.Invoke(this, msg);
            }
        }
    }

    [RelayCommand]
    private async Task StartLadderRoomAsync()
    {
        if (!IsLadderRoomOpen || !IsLadderHost || _ladderStarting || _ladderRoom is null)
        {
            return;
        }

        if (!LabLadderEngine.CanStartGame(_ladderRoom))
        {
            LadderStatusText = _ladderRoom.Phase != LadderRoomPhase.PickingSlots
                ? "먼저 참가자 명단을 확정해 주세요."
                : "모든 참가자가 중복 없이 시작 위치를 선택해야 시작할 수 있습니다.";
            return;
        }

        var sessionId = _ladderRoom.SessionId;
        var orderedMembers = _ladderRoom.Members
            .OrderBy(m => m.SelectedSlot!.Value)
            .ToList();

        _ladderStarting = true;
        var stateJson = JsonSerializer.Serialize(_ladderRoom, ProtocolJson.PacketOptions);
        var stateSendTasks = _ladderJoinedFriends.Values.ToArray().Select(friend => _social.SendGameEventAsync(friend, new GameEventPayload
        {
            GameType = LabLadderEngine.GameTypeId,
            SessionId = sessionId,
            Action = "state",
            WagerCoins = _ladderRoom.WagerCoins,
            Data = stateJson
        })).ToList();

        var stateResults = await Task.WhenAll(stateSendTasks);
        if (_ladderRoom is null || _ladderRoom.SessionId != sessionId)
        {
            _ladderStarting = false;
            return;
        }

        if (stateResults.Any(sent => !sent))
        {
            await AbortLadderRoomAsHostAsync("일부 참가자와 연결이 끊겨 사다리 게임을 취소했습니다.");
            return;
        }

        var board = LabLadderEngine.GenerateRandomizedBoard(
            orderedMembers.Select(m => m.DisplayName).ToList(),
            _ladderRoom.Prizes);
        var startDto = LabLadderEngine.CreateStartDto(_ladderRoom, board);
        var startJson = JsonSerializer.Serialize(startDto, ProtocolJson.PacketOptions);

        var startSendTasks = _ladderJoinedFriends.Values.ToArray().Select(friend => _social.SendGameEventAsync(friend, new GameEventPayload
        {
            GameType = LabLadderEngine.GameTypeId,
            SessionId = sessionId,
            Action = "start",
            WagerCoins = startDto.WagerCoins,
            Data = startJson
        })).ToList();

        var startResults = await Task.WhenAll(startSendTasks);
        if (_ladderRoom is null || _ladderRoom.SessionId != sessionId)
        {
            _ladderStarting = false;
            return;
        }

        if (startResults.Any(sent => !sent))
        {
            await AbortLadderRoomAsHostAsync("일부 참가자에게 사다리판을 전송하지 못해 게임을 취소했습니다.");
            return;
        }

        var abortRefundCoins = _ladderPaidCoins;
        _ladderPaidCoins = 0;
        _ladderStarting = false;
        await ApplyStartedMultiplayerLadderAsync(startDto, abortRefundCoins);
    }

    [RelayCommand]
    private async Task CancelLadderRoomAsync()
    {
        if (!IsLadderRoomOpen || _ladderStarting || _ladderRoom is null)
        {
            return;
        }

        if (IsLadderHost)
        {
            await AbortLadderRoomAsHostAsync("사다리방을 취소했습니다.");
            return;
        }

        var sessionId = _ladderRoom.SessionId;
        var wager = _ladderActiveWager;
        var host = _ladderHostFriend;
        var refunded = await RefundLadderEntryFeeIfPaidAsync();
        CloseLadderRoomState(refunded > 0
            ? "사다리방을 나가 참가 코인을 반환했습니다."
            : "사다리방에서 나갔습니다.");

        if (host is not null)
        {
            _ = await _social.SendGameEventAsync(host, new GameEventPayload
            {
                GameType = LabLadderEngine.GameTypeId,
                SessionId = sessionId,
                Action = "decline",
                WagerCoins = wager
            });
        }
    }

    private async Task HandleLadderEventAsync(string senderPetId, string senderName, FriendEntry friend, GameEventPayload p)
    {
        if (_ladderRoom is null)
        {
            return;
        }

        if (IsLadderHost)
        {
            switch (p.Action)
            {
                case "accept" when _ladderRoom.Phase == LadderRoomPhase.WaitingInvites && _ladderPendingInviteeIds.Remove(senderPetId):
                {
                    _ladderPendingInvitees.Remove(senderPetId);
                    _ladderJoinedFriends[senderPetId] = friend;
                    var shortName = string.IsNullOrWhiteSpace(friend.OwnerName) ? friend.PetName : friend.OwnerName;
                    if (!_ladderRoom.Members.Any(m => string.Equals(m.PetId, senderPetId, StringComparison.OrdinalIgnoreCase)))
                    {
                        _ladderRoom.Members.Add(new LadderMemberStateDto
                        {
                            PetId = senderPetId,
                            DisplayName = shortName,
                            SelectedSlot = null
                        });
                    }

                    _ladderRoom.PendingInviteCount = _ladderPendingInviteeIds.Count;
                    RecalculateHostSlotCountAndPrizes();
                    RefreshLadderLobbyUi();
                    if (_ladderRoom.PendingInviteCount == 0)
                    {
                        CancelLadderInviteTimeout();
                        LadderStatusText = "모든 응답이 완료되었습니다. '참가자 확정' 버튼을 눌러 주세요.";
                    }

                    await BroadcastLadderStateOrAbortAsync();
                    break;
                }

                case "decline" or "cancel" when _ladderPendingInviteeIds.Contains(senderPetId) || _ladderJoinedFriends.ContainsKey(senderPetId):
                {
                    var wasJoinedInPicking = _ladderRoom.Phase == LadderRoomPhase.PickingSlots && _ladderJoinedFriends.ContainsKey(senderPetId);
                    _ladderPendingInviteeIds.Remove(senderPetId);
                    _ladderPendingInvitees.Remove(senderPetId);
                    _ladderJoinedFriends.Remove(senderPetId);
                    _ladderRoom.Members.RemoveAll(m => string.Equals(m.PetId, senderPetId, StringComparison.OrdinalIgnoreCase));
                    _ladderRoom.PendingInviteCount = _ladderPendingInviteeIds.Count;

                    if (wasJoinedInPicking)
                    {
                        await AbortLadderRoomAsHostAsync($"{senderName}(이)가 사다리방을 이탈하여 게임을 취소했습니다.");
                        return;
                    }

                    if (_ladderRoom.Members.Count + _ladderRoom.PendingInviteCount < LabLadderEngine.MinParticipants)
                    {
                        await AbortLadderRoomAsHostAsync("참가 가능한 친구가 부족하여 사다리방이 종료되었습니다.");
                        return;
                    }

                    RecalculateHostSlotCountAndPrizes();
                    RefreshLadderLobbyUi();
                    if (_ladderRoom.PendingInviteCount == 0)
                    {
                        CancelLadderInviteTimeout();
                        LadderStatusText = "응답이 완료되었습니다. '참가자 확정' 버튼을 눌러 주세요.";
                    }

                    await BroadcastLadderStateOrAbortAsync();
                    break;
                }

                case "pick" when _ladderRoom.Phase == LadderRoomPhase.PickingSlots && _ladderJoinedFriends.ContainsKey(senderPetId):
                {
                    if (int.TryParse(p.Data, out var slotIdx) && LabLadderEngine.TryPickSlot(_ladderRoom, senderPetId, slotIdx))
                    {
                        RefreshLadderLobbyUi();
                        if (LabLadderEngine.CanStartGame(_ladderRoom))
                        {
                            LadderStatusText = "모든 참가자가 위치를 선택했습니다! '게임 시작'을 눌러 주세요.";
                        }

                        await BroadcastLadderStateOrAbortAsync();
                    }

                    break;
                }
            }
        }
        else if (string.Equals(_ladderHostFriend?.PetId, senderPetId, StringComparison.OrdinalIgnoreCase))
        {
            switch (p.Action)
            {
                case "state":
                {
                    if (TryReadLadderPayload<LadderRoomStateDto>(p.Data, out var state) && state is not null
                        && LabLadderEngine.IsValidRoom(state, p.SessionId, senderPetId, _ladderActiveWager)
                        && LabLadderEngine.IsValidRoomTransition(_ladderRoom, _ladderInitialPrizes, state, _profile.PetId))
                    {
                        _ladderRoom = state;
                        LadderBoardPrizesSummaryText = $"공개 상품: {LabLadderEngine.FormatPrizeSummary(state.Prizes)}";
                        RefreshLadderLobbyUi();
                        if (state.Phase == LadderRoomPhase.PickingSlots)
                        {
                            var myMember = state.Members.FirstOrDefault(m => string.Equals(m.PetId, _profile.PetId, StringComparison.OrdinalIgnoreCase));
                            LadderStatusText = LabLadderEngine.CanStartGame(state)
                                ? "모두 위치를 골랐습니다! 방장이 게임을 시작할 때까지 기다리는 중..."
                                : myMember?.SelectedSlot is int mySlot
                                    ? $"{mySlot + 1}번 자리를 선택했습니다. 다른 친구들의 선택을 기다리는 중..."
                                    : "참가자가 확정되었습니다! 원하는 시작 번호를 골라 주세요.";
                        }
                    }

                    break;
                }

                case "start":
                {
                    if (TryReadLadderPayload<LadderStartDto>(p.Data, out var startDto) && startDto is not null)
                    {
                        await ApplyStartedMultiplayerLadderAsync(startDto);
                    }

                    break;
                }

                case "cancel":
                {
                    var refunded = await RefundLadderEntryFeeIfPaidAsync();
                    var cancelMsg = refunded > 0
                        ? $"{senderName}(이)가 사다리방을 취소하여 참가 코인을 반환했습니다."
                        : $"{senderName}(이)가 사다리방을 취소했습니다.";
                    CloseLadderRoomState(cancelMsg);
                    GameNotification?.Invoke(this, cancelMsg);
                    break;
                }
            }
        }
    }

    private void StartLadderInviteTimeout(string sessionId)
    {
        CancelLadderInviteTimeout();
        var cts = new CancellationTokenSource();
        _ladderInviteTimeoutCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(InviteWaitTimeoutSeconds), cts.Token);
                if (!cts.Token.IsCancellationRequested)
                {
                    RunOnUi(() => _ = ExpirePendingLadderInvitesAsync(sessionId));
                }
            }
            catch (TaskCanceledException)
            {
            }
        }, cts.Token);
    }

    private void CancelLadderInviteTimeout()
    {
        _ladderInviteTimeoutCts?.Cancel();
        _ladderInviteTimeoutCts?.Dispose();
        _ladderInviteTimeoutCts = null;
    }

    private async Task ExpirePendingLadderInvitesAsync(string sessionId)
    {
        if (!IsLadderRoomOpen || !IsLadderHost || _ladderStarting || _ladderRoom is null
            || _ladderRoom.SessionId != sessionId || _ladderRoom.Phase != LadderRoomPhase.WaitingInvites
            || _ladderPendingInvitees.Count == 0)
        {
            return;
        }

        var timedOut = _ladderPendingInvitees.Values.ToArray();
        var wager = _ladderActiveWager;
        _ladderPendingInvitees.Clear();
        _ladderPendingInviteeIds.Clear();
        _ladderRoom.PendingInviteCount = 0;

        foreach (var friend in timedOut)
        {
            _ = await _social.SendGameEventAsync(friend, new GameEventPayload
            {
                GameType = LabLadderEngine.GameTypeId,
                SessionId = sessionId,
                Action = "cancel",
                WagerCoins = wager
            });
        }

        if (_ladderRoom is null || _ladderRoom.SessionId != sessionId)
        {
            return;
        }

        if (_ladderRoom.Members.Count < LabLadderEngine.MinParticipants)
        {
            await AbortLadderRoomAsHostAsync($"수락 대기 시간({InviteWaitTimeoutSeconds}초)이 지나 참가 인원이 부족하여 방을 종료했습니다.");
            return;
        }

        RecalculateHostSlotCountAndPrizes();
        RefreshLadderLobbyUi();
        LadderStatusText = "응답 대기 시간이 종료되었습니다. 현재 입장 인원으로 '참가자 확정'을 눌러 주세요.";
        await BroadcastLadderStateOrAbortAsync();
    }

    private async Task BroadcastLadderStateOrAbortAsync()
    {
        if (_ladderRoom is not { } room)
        {
            return;
        }

        var sessionId = room.SessionId;
        var json = JsonSerializer.Serialize(room, ProtocolJson.PacketOptions);
        var targets = _ladderJoinedFriends.Values.ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        var results = await Task.WhenAll(targets.Select(friend => _social.SendGameEventAsync(friend, new GameEventPayload
        {
            GameType = LabLadderEngine.GameTypeId,
            SessionId = sessionId,
            Action = "state",
            WagerCoins = room.WagerCoins,
            Data = json
        })));

        if (_ladderRoom is null || _ladderRoom.SessionId != sessionId)
        {
            return;
        }

        if (results.Any(sent => !sent))
        {
            await AbortLadderRoomAsHostAsync("참가자와의 연결이 끊겨 사다리방을 취소했습니다.");
        }
    }

    private async Task AbortLadderRoomAsHostAsync(string statusMessage)
    {
        var sessionId = _ladderRoom?.SessionId ?? string.Empty;
        var wager = _ladderActiveWager;
        var notifyTargets = _ladderJoinedFriends.Values
            .Concat(_ladderPendingInvitees.Values)
            .GroupBy(f => f.PetId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        CancelLadderInviteTimeout();
        var refunded = await RefundLadderEntryFeeIfPaidAsync();
        var finalMessage = refunded > 0 && !statusMessage.Contains("반환", StringComparison.Ordinal)
            ? $"{statusMessage} (참가 {refunded} 코인 반환)"
            : statusMessage;

        CloseLadderRoomState(finalMessage);

        if (!string.IsNullOrEmpty(sessionId))
        {
            foreach (var friend in notifyTargets)
            {
                _ = await _social.SendGameEventAsync(friend, new GameEventPayload
                {
                    GameType = LabLadderEngine.GameTypeId,
                    SessionId = sessionId,
                    Action = "cancel",
                    WagerCoins = wager
                });
            }
        }
    }

    private async Task AbortActiveLadderAnimationAsync(string reasonMessage)
    {
        var run = _pendingLadderRun;
        if (run is null || run.IsCompleted)
        {
            return;
        }

        run.IsCompleted = true;
        _pendingLadderRun = null;
        IsLadderAnimating = false;
        LadderAnimationCancelled?.Invoke(this, EventArgs.Empty);

        if (run.AbortRefundCoins > 0)
        {
            var refund = run.AbortRefundCoins;
            run.AbortRefundCoins = 0;
            _progression.AddCoins(_profile, refund);
            await PersistProfileAsync();
            var fullMsg = $"{reasonMessage} ({refund} 코인 반환)";
            LadderStatusText = fullMsg;
            GameNotification?.Invoke(this, fullMsg);
        }
        else
        {
            LadderStatusText = reasonMessage;
            GameNotification?.Invoke(this, reasonMessage);
        }

        LadderResultLines.Clear();
    }

    private async Task<int> RefundLadderEntryFeeIfPaidAsync()
    {
        var refund = _ladderPaidCoins;
        if (refund <= 0)
        {
            return 0;
        }

        _ladderPaidCoins = 0;
        _progression.AddCoins(_profile, refund);
        await PersistProfileAsync();
        return refund;
    }

    private static bool TryReadLadderPayload<T>(string? json, out T? payload) where T : class
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 32_768)
        {
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<T>(json, ProtocolJson.PacketOptions);
            return payload is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void RecalculateHostSlotCountAndPrizes()
    {
        if (_ladderRoom is null)
        {
            return;
        }

        var desired = Math.Clamp(_ladderRoom.Members.Count + _ladderRoom.PendingInviteCount, LabLadderEngine.MinParticipants, LabLadderEngine.MaxParticipants);
        var maxPicked = _ladderRoom.Members.Where(m => m.SelectedSlot.HasValue).Select(m => m.SelectedSlot!.Value + 1).DefaultIfEmpty(0).Max();
        _ladderRoom.SlotCount = Math.Max(desired, maxPicked);
        _ladderRoom.Prizes = LabLadderEngine.NormalizePrizesForCount(_ladderInitialPrizes, _ladderRoom.SlotCount);
        LadderBoardPrizesSummaryText = $"공개 상품: {LabLadderEngine.FormatPrizeSummary(_ladderRoom.Prizes)}";
    }

    private async Task ApplyStartedMultiplayerLadderAsync(LadderStartDto startDto, int overrideRefundCoins = -1)
    {
        if (_startedLadderSessions.Contains(startDto.SessionId))
        {
            return;
        }

        if (_ladderRoom is not { } room
            || !LabLadderEngine.TryValidateStart(startDto, room, _profile.PetId, out var board, out var traces)
            || board is null)
        {
            _ladderStarting = false;
            var host = _ladderHostFriend;
            var sessionId = _ladderRoom?.SessionId ?? startDto.SessionId;
            var wager = _ladderActiveWager;
            var refunded = await RefundLadderEntryFeeIfPaidAsync();
            var invalidMsg = refunded > 0
                ? "사다리판 검증에 실패하여 게임을 취소하고 참가 코인을 반환했습니다."
                : "사다리판 검증에 실패하여 게임이 취소되었습니다.";
            CloseLadderRoomState(invalidMsg);
            if (!IsLadderHost && host is not null && !string.IsNullOrEmpty(sessionId))
            {
                _ = await _social.SendGameEventAsync(host, new GameEventPayload
                {
                    GameType = LabLadderEngine.GameTypeId,
                    SessionId = sessionId,
                    Action = "decline",
                    WagerCoins = wager
                });
            }

            return;
        }

        if (_startedLadderSessions.Count >= RecentInvitationCapacity)
        {
            _startedLadderSessions.Clear();
        }
        _startedLadderSessions.Add(startDto.SessionId);

        var refundCoins = overrideRefundCoins >= 0 ? overrideRefundCoins : _ladderPaidCoins;
        _ladderPaidCoins = 0;
        var hostFriend = _ladderHostFriend;
        var myCol = startDto.ParticipantPetIds.FindIndex(id => string.Equals(id, _profile.PetId, StringComparison.OrdinalIgnoreCase));

        var configuredPrizesSummary = LabLadderEngine.FormatPrizeSummary(room.Prizes);
        CloseLadderRoomStateInternal();
        BeginLadderReveal(
            startDto.SessionId,
            board,
            traces,
            myCol,
            startDto.WagerCoins,
            isMultiplayer: true,
            hostFriend,
            refundCoins,
            configuredPrizesSummary);
    }

    private void BeginLadderReveal(
        string sessionId,
        LadderBoard board,
        IReadOnlyList<LadderTraceResult> traces,
        int myStartColumn,
        int wagerCoins,
        bool isMultiplayer,
        FriendEntry? hostFriend,
        int abortRefundCoins,
        string? configuredPrizesSummary = null)
    {
        _lastLadderBoard = board;
        _lastLadderResults = [];
        _pendingLadderRun = new PendingLadderRun
        {
            SessionId = sessionId,
            Board = board,
            Traces = traces,
            MyStartColumn = myStartColumn,
            WagerCoins = wagerCoins,
            IsMultiplayer = isMultiplayer,
            HostFriend = hostFriend,
            AbortRefundCoins = abortRefundCoins,
            RevealedColumns = new bool[traces.Count]
        };

        LadderBoardPrizesSummaryText = string.IsNullOrWhiteSpace(configuredPrizesSummary)
            ? $"공개 상품: {LabLadderEngine.FormatPrizeSummary(board.Prizes)}"
            : $"공개 상품: {configuredPrizesSummary}";
        LadderResultLines.Clear();
        foreach (var r in traces)
        {
            LadderResultLines.Add($"{r.Participant}  →  ?");
        }

        LadderStatusText = "🪜 사다리 내려가는 중... (하단 도착 시 결과가 공개됩니다)";
        IsLadderAnimating = true;
        OnPropertyChanged(nameof(ActiveLadderBoard));

        if (isMultiplayer && LadderBoardGenerated is null)
        {
            SelectedGameIndex = 3;
            OpenGameTabRequested?.Invoke(this, EventArgs.Empty);
        }

        LadderBoardGenerated?.Invoke(this, board);
    }

    public bool RevealLadderParticipantResult(int startColumn)
    {
        if (_pendingLadderRun is not { IsCompleted: false } run
            || startColumn < 0
            || startColumn >= run.Traces.Count)
        {
            return false;
        }

        if (run.RevealedColumns[startColumn])
        {
            return false;
        }

        run.RevealedColumns[startColumn] = true;
        var result = run.Traces[startColumn];
        if (startColumn < LadderResultLines.Count)
        {
            LadderResultLines[startColumn] = $"{result.Participant}  →  {result.Prize}";
        }

        LadderStatusText = $"🪜 {result.Participant} 도착 → {result.Prize}";
        return true;
    }

    public async Task CompleteLadderRevealAsync()
    {
        var run = _pendingLadderRun;
        if (run is null || run.IsCompleted)
        {
            return;
        }

        run.IsCompleted = true;
        run.AbortRefundCoins = 0;

        for (var i = 0; i < run.Traces.Count; i++)
        {
            if (!run.RevealedColumns[i])
            {
                run.RevealedColumns[i] = true;
                var r = run.Traces[i];
                if (i < LadderResultLines.Count)
                {
                    LadderResultLines[i] = $"{r.Participant}  →  {r.Prize}";
                }
            }
        }

        _lastLadderResults = run.Traces;
        IsLadderAnimating = false;

        var myResult = run.MyStartColumn >= 0 && run.MyStartColumn < run.Traces.Count
            ? run.Traces[run.MyStartColumn]
            : null;

        if (run.WagerCoins > 0 && myResult is not null && LabLadderEngine.IsJackpot(myResult.Prize))
        {
            var settledNow = _settledLadderSessions.Add(run.SessionId);
            if (_settledLadderSessions.Count > RecentInvitationCapacity)
            {
                _settledLadderSessions.Clear();
                _settledLadderSessions.Add(run.SessionId);
            }

            if (settledNow)
            {
                var jackpot = run.WagerCoins * run.Traces.Count;
                _progression.AddCoins(_profile, jackpot);
                await PersistProfileAsync();

                var prefix = run.IsMultiplayer ? "🎉 동시 사다리 당첨!" : "🎉 사다리 결과:";
                LadderStatusText = $"{prefix} {myResult.Participant} → {myResult.Prize} (+{jackpot} 코인)";
                PetEmotionTriggered?.Invoke(this, true);
                if (run.IsMultiplayer)
                {
                    GameNotification?.Invoke(this, $"🪜 사다리 잭팟 당첨! (+{jackpot} 코인)");
                }
            }

            return;
        }

        if (myResult is not null)
        {
            if (run.IsMultiplayer)
            {
                var modeLabel = run.WagerCoins > 0 ? "동시 사다리 결과" : "동시 사다리(무료) 결과";
                LadderStatusText = $"🪜 {modeLabel}: {myResult.Participant} → {myResult.Prize}";
                GameNotification?.Invoke(this, $"🪜 사다리 결과: {myResult.Prize}");
            }
            else
            {
                LadderStatusText = $"🪜 사다리 완료! ({run.Traces.Count}명 참여)";
            }
        }
        else
        {
            LadderStatusText = $"🪜 사다리 완료! ({run.Traces.Count}명 참여)";
        }
    }

    private void RefreshLadderLobbyUi()
    {
        LadderSlots.Clear();
        if (_ladderRoom is null)
        {
            IsLadderWaitingInvites = false;
            IsLadderPickingPhase = false;
            CanConfirmLadderParticipants = false;
            ShowConfirmLadderButton = false;
            CanStartLadderGame = false;
            ShowStartLadderButton = false;
            LadderLobbySummaryText = string.Empty;
            LadderRoomPrizesText = string.Empty;
            LadderRoomMembersText = string.Empty;
            return;
        }

        var isPicking = _ladderRoom.Phase == LadderRoomPhase.PickingSlots;
        var joinedCount = _ladderRoom.Members.Count;
        var pickedCount = _ladderRoom.Members.Count(m => m.SelectedSlot.HasValue);

        for (var col = 0; col < _ladderRoom.SlotCount; col++)
        {
            var occupant = _ladderRoom.Members.FirstOrDefault(m => m.SelectedSlot == col);
            var isMine = occupant is not null && string.Equals(occupant.PetId, _profile.PetId, StringComparison.OrdinalIgnoreCase);
            LadderSlots.Add(new LadderSlotOption(
                col,
                $"{col + 1}번",
                !isPicking
                    ? "확정 대기"
                    : (occupant is null ? "빈 자리" : (isMine ? $"{occupant.DisplayName} (나)" : occupant.DisplayName)),
                occupant is not null,
                isMine,
                isPicking));
        }

        var feeText = _ladderRoom.WagerCoins > 0 ? $"{_ladderRoom.WagerCoins}코인" : "무료 (0코인)";
        LadderRoomPrizesText = $"참가비: {feeText} · 상품({ _ladderRoom.SlotCount }칸): {LabLadderEngine.FormatPrizeSummary(_ladderRoom.Prizes)}";
        LadderRoomMembersText = "참가자: " + string.Join(", ", _ladderRoom.Members.Select(m =>
            string.Equals(m.PetId, _profile.PetId, StringComparison.OrdinalIgnoreCase)
                ? $"{m.DisplayName}(나)"
                : m.DisplayName));

        IsLadderWaitingInvites = !isPicking;
        IsLadderPickingPhase = isPicking;
        CanConfirmLadderParticipants = IsLadderHost && LabLadderEngine.CanConfirmParticipants(_ladderRoom);
        ShowConfirmLadderButton = IsLadderHost && !isPicking;
        CanStartLadderGame = IsLadderHost && LabLadderEngine.CanStartGame(_ladderRoom);
        ShowStartLadderButton = IsLadderHost && isPicking;

        if (!isPicking)
        {
            var pendingText = _ladderRoom.PendingInviteCount > 0 ? $" · 응답 대기 {_ladderRoom.PendingInviteCount}명" : " · 응답 완료";
            LadderLobbySummaryText = $"1단계 초대 응답 ({joinedCount}명 입장{pendingText})";
        }
        else
        {
            LadderLobbySummaryText = $"2단계 위치 선택 ({pickedCount}/{joinedCount}명 완료)";
        }
    }

    private void CloseLadderRoomState(string statusMessage)
    {
        CloseLadderRoomStateInternal();
        LadderStatusText = statusMessage;
    }

    private void CloseLadderRoomStateInternal()
    {
        CancelLadderInviteTimeout();
        _ladderRoom = null;
        _ladderHostFriend = null;
        _ladderInitialPrizes.Clear();
        _ladderPendingInviteeIds.Clear();
        _ladderPendingInvitees.Clear();
        _ladderJoinedFriends.Clear();
        _ladderActiveWager = 0;
        _ladderPaidCoins = 0;
        _ladderStarting = false;
        IsLadderRoomOpen = false;
        IsLadderHost = false;
        IsLadderWaitingInvites = false;
        IsLadderPickingPhase = false;
        CanConfirmLadderParticipants = false;
        ShowConfirmLadderButton = false;
        CanStartLadderGame = false;
        ShowStartLadderButton = false;
        LadderLobbySummaryText = string.Empty;
        LadderRoomPrizesText = string.Empty;
        LadderRoomMembersText = string.Empty;
        LadderSlots.Clear();
    }

    [RelayCommand]
    private void LoadOnlineFriendsToLadder()
    {
        var names = new List<string>
        {
            string.IsNullOrWhiteSpace(_profile.OwnerName) ? _profile.PetName : _profile.OwnerName
        };

        foreach (var f in OnlineFriends.Take(LabLadderEngine.MaxParticipants - 1))
        {
            var shortName = string.IsNullOrWhiteSpace(f.Friend.OwnerName) ? f.Friend.PetName : f.Friend.OwnerName;
            if (!names.Contains(shortName, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(shortName);
            }
        }

        if (names.Count < 2)
        {
            names.Add("친구1");
            names.Add("친구2");
        }

        LadderParticipantsText = string.Join(", ", names);
        ApplyCoffeePreset();
    }

    [RelayCommand]
    private void ApplyCoffeePreset()
    {
        var count = Math.Clamp(ParseTokens(LadderParticipantsText).Count, LabLadderEngine.MinParticipants, LabLadderEngine.MaxParticipants);
        LadderPrizesText = count <= 2
            ? "☕ 커피 쏘기 x1, 통과 x1"
            : $"☕ 커피 쏘기 x1, 통과 x{count - 1}";
    }

    [RelayCommand]
    private void ApplyCoinJackpotPreset()
    {
        var count = Math.Clamp(ParseTokens(LadderParticipantsText).Count, LabLadderEngine.MinParticipants, LabLadderEngine.MaxParticipants);
        LadderPrizesText = count <= 2
            ? "💰 코인 잭팟! x1, 꽝 x1"
            : $"💰 코인 잭팟! x1, 꽝 x{count - 1}";
        if (LadderEntryFee == 0)
        {
            LadderEntryFee = 30;
        }
    }

    [RelayCommand]
    private async Task RunLadderAsync()
    {
        if (IsLadderRoomOpen || IsLadderAnimating)
        {
            return;
        }

        var participants = ParseTokens(LadderParticipantsText);
        if (participants.Count < LabLadderEngine.MinParticipants || participants.Count > LabLadderEngine.MaxParticipants)
        {
            LadderStatusText = $"참여자는 {LabLadderEngine.MinParticipants}~{LabLadderEngine.MaxParticipants}명으로 입력해 주세요. (쉼표 구분)";
            return;
        }

        if (!LabLadderEngine.TryParsePrizeInput(LadderPrizesText, out var parsedPrizes, out var prizeError))
        {
            LadderStatusText = prizeError;
            return;
        }

        if (!LadderFeeOptions.Contains(LadderEntryFee))
        {
            return;
        }

        var prizes = LabLadderEngine.NormalizePrizesForCount(parsedPrizes, participants.Count);
        if (LadderEntryFee > 0 && prizes.Count(LabLadderEngine.IsJackpot) > 1)
        {
            LadderStatusText = "유료 사다리에서는 잭팟을 한 칸까지만 지정할 수 있습니다.";
            return;
        }

        if (LadderEntryFee > 0)
        {
            var (ok, msg) = _progression.TrySpendCoins(_profile, LadderEntryFee);
            if (!ok)
            {
                LadderStatusText = msg;
                return;
            }

            await PersistProfileAsync();
        }

        var configuredPrizesSummary = LabLadderEngine.FormatPrizeSummary(prizes);
        var board = LabLadderEngine.GenerateRandomizedBoard(participants, prizes);
        var traces = LabLadderEngine.TraceAll(board);
        BeginLadderReveal(
            Guid.NewGuid().ToString("N"),
            board,
            traces,
            myStartColumn: 0,
            LadderEntryFee,
            isMultiplayer: false,
            hostFriend: null,
            abortRefundCoins: 0,
            configuredPrizesSummary);
    }

    [RelayCommand]
    private async Task ShareLadderResultToFriendsAsync()
    {
        if (IsLadderAnimating)
        {
            LadderStatusText = "사다리 결과 공개가 끝난 뒤 공유할 수 있습니다.";
            return;
        }

        if (_lastLadderResults.Count == 0)
        {
            LadderStatusText = "먼저 사다리를 실행해 주세요.";
            return;
        }

        if (OnlineFriends.Count == 0)
        {
            LadderStatusText = "결과를 공유할 온라인 친구가 없습니다.";
            return;
        }

        var summary = "🪜사다리: " + string.Join(" / ", _lastLadderResults.Select(r => $"{r.Participant}→{r.Prize}"));
        if (summary.Length > ProtocolConstants.MaxBubbleMessageLength)
        {
            summary = summary[..(ProtocolConstants.MaxBubbleMessageLength - 1)] + "…";
        }

        var sentCount = 0;
        foreach (var f in OnlineFriends)
        {
            if (await _social.SendBubbleMessageAsync(f.Friend, summary))
            {
                sentCount++;
            }
        }

        LadderStatusText = sentCount > 0
            ? $"📢 온라인 친구 {sentCount}명에게 사다리 결과를 공유했습니다!"
            : "친구들에게 결과를 전송하지 못했습니다.";
    }

    private static List<string> ParseTokens(string? raw)
        => (raw ?? string.Empty)
            .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
}
