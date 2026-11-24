using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Games;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;

namespace LabPet.Client.ViewModels;

public sealed partial class CoinGamesViewModel
{
    private static readonly string[] LadderTrackAccentHexes =
    [
        "#FFFF4D82",
        "#FF4F46E5",
        "#FF10B981",
        "#FFF59E0B",
        "#FF0EA5E9",
        "#FFA855F7",
        "#FFEF4444",
        "#FF14B8A6"
    ];

    // ══════════════════════════════════════════════════════════════════════
    // 4. 연구실 공동 사다리타기 (구조화 상품 · 9상태 대기실 · 4종 타이머)
    // ══════════════════════════════════════════════════════════════════════

    private void InitializeLadderPrizeRows()
    {
        var items = LabLadderEngine.ParsePrizeItemsFromText(LadderPrizesText);
        if (items.Count == 0)
        {
            items = LabLadderEngine.BuildPresetPrizeItems(LadderPrizePreset.CoffeeTreat, 2);
        }

        ReplaceLadderPrizeRows(items, syncText: true);
    }

    private void LoadPrizeRowsFromText(string? rawText)
    {
        var items = LabLadderEngine.ParsePrizeItemsFromText(rawText);
        if (items.Count == 0)
        {
            return;
        }

        ReplaceLadderPrizeRows(items, syncText: false);
    }

    private void ReplaceLadderPrizeRows(IEnumerable<LadderPrizeItemDto> items, bool syncText)
    {
        foreach (var existing in LadderPrizeRows)
        {
            existing.PropertyChanged -= OnLadderPrizeRowChanged;
        }

        LadderPrizeRows.Clear();
        foreach (var dto in items)
        {
            var row = new LadderPrizeRowViewModel
            {
                Label = dto.Label,
                Quantity = Math.Clamp(dto.Quantity, 1, LabLadderEngine.MaxParticipants)
            };
            row.PropertyChanged += OnLadderPrizeRowChanged;
            LadderPrizeRows.Add(row);
        }

        if (syncText)
        {
            SyncLadderPrizesTextFromRows();
        }

        NotifyLadderPrizeAllocationChanged();
    }

    private void OnLadderPrizeRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LadderPrizeRowViewModel.Label) or nameof(LadderPrizeRowViewModel.Quantity))
        {
            SyncLadderPrizesTextFromRows();
            NotifyLadderPrizeAllocationChanged();
        }
    }

    private void SyncLadderPrizesTextFromRows()
    {
        var dtos = GetPrizeRowDtos();
        var formatted = LabLadderEngine.FormatStructuredPrizeInputText(dtos);
        if (string.Equals(LadderPrizesText, formatted, StringComparison.Ordinal))
        {
            return;
        }

        _syncingLadderPrizes = true;
        try
        {
            LadderPrizesText = formatted;
        }
        finally
        {
            _syncingLadderPrizes = false;
        }
    }

    private List<LadderPrizeItemDto> GetPrizeRowDtos()
        => LadderPrizeRows.Select(r => new LadderPrizeItemDto
        {
            Label = r.Label?.Trim() ?? string.Empty,
            Quantity = Math.Clamp(r.Quantity, 1, LabLadderEngine.MaxParticipants)
        }).ToList();

    private void NotifyLadderPrizeAllocationChanged()
    {
        OnPropertyChanged(nameof(LadderTargetSlotCount));
        OnPropertyChanged(nameof(LadderConfiguredSlotCount));
        OnPropertyChanged(nameof(IsLadderPrizeAllocationMatched));
        OnPropertyChanged(nameof(LadderPrizeAllocationRatio));
        OnPropertyChanged(nameof(LadderPrizeAllocationText));
        OnPropertyChanged(nameof(CanOpenLadderRoom));
    }

    [RelayCommand]
    private void AddLadderPrizeRow()
    {
        if (LadderPrizeRows.Count >= LabLadderEngine.MaxParticipants
            || LadderConfiguredSlotCount >= LabLadderEngine.MaxParticipants)
        {
            LadderStatusText = $"상품 칸 수 합계는 최대 {LabLadderEngine.MaxParticipants}개까지 추가할 수 있습니다.";
            return;
        }

        var row = new LadderPrizeRowViewModel
        {
            Label = "새 상품",
            Quantity = 1
        };
        row.PropertyChanged += OnLadderPrizeRowChanged;
        LadderPrizeRows.Add(row);
        SyncLadderPrizesTextFromRows();
        NotifyLadderPrizeAllocationChanged();
    }

    [RelayCommand]
    private void RemoveLadderPrizeRow(LadderPrizeRowViewModel? row)
    {
        if (row is null || LadderPrizeRows.Count <= 1)
        {
            return;
        }

        row.PropertyChanged -= OnLadderPrizeRowChanged;
        LadderPrizeRows.Remove(row);
        SyncLadderPrizesTextFromRows();
        NotifyLadderPrizeAllocationChanged();
    }

    [RelayCommand]
    private void IncrementPrizeRowQuantity(LadderPrizeRowViewModel? row)
    {
        if (row is null || LadderConfiguredSlotCount >= LabLadderEngine.MaxParticipants)
        {
            return;
        }

        row.Quantity = Math.Min(LabLadderEngine.MaxParticipants, row.Quantity + 1);
    }

    [RelayCommand]
    private void DecrementPrizeRowQuantity(LadderPrizeRowViewModel? row)
    {
        if (row is null || row.Quantity <= 1)
        {
            return;
        }

        row.Quantity--;
    }

    [RelayCommand]
    private void ApplyCoffeePreset()
        => ApplyLadderPreset(LadderPrizePreset.CoffeeTreat, setPaidFeeIfZero: false);

    [RelayCommand]
    private void ApplyLuckyDrawPreset()
        => ApplyLadderPreset(LadderPrizePreset.LuckyDraw, setPaidFeeIfZero: false);

    [RelayCommand]
    private void ApplyCoinJackpotPreset()
        => ApplyLadderPreset(LadderPrizePreset.CoinJackpot, setPaidFeeIfZero: true);

    private void ApplyLadderPreset(LadderPrizePreset preset, bool setPaidFeeIfZero)
    {
        var count = IsLadderRoomOpen && _ladderRoom is not null
            ? Math.Clamp(_ladderRoom.Members.Count, LabLadderEngine.MinParticipants, LabLadderEngine.MaxParticipants)
            : LadderTargetSlotCount;
        var presetItems = LabLadderEngine.BuildPresetPrizeItems(preset, count);
        ReplaceLadderPrizeRows(presetItems, syncText: true);
        if (setPaidFeeIfZero && !IsLadderRoomOpen && LadderEntryFee == 0)
        {
            LadderEntryFee = 30;
        }
    }

    partial void OnLadderInviteeRegisteredAck(string friendPetId, bool coinsShort)
    {
        if (!IsLadderHost || _ladderRoom is null || _ladderRoom.Phase != LadderRoomPhase.WaitingInvites)
        {
            return;
        }

        if (_ladderInviteeTrackers.TryGetValue(friendPetId, out var tracker)
            && tracker.Status is LadderParticipantStatus.Sending or LadderParticipantStatus.WaitingResponse or LadderParticipantStatus.InsufficientCoins)
        {
            var nextStatus = coinsShort
                ? LadderParticipantStatus.InsufficientCoins
                : LadderParticipantStatus.WaitingResponse;
            if (tracker.Status != nextStatus)
            {
                tracker.Status = nextStatus;
                SyncInviteesIntoRoom();
                RefreshLadderLobbyUi();
                _ = BroadcastLadderStateOrAbortAsync();
            }
        }
    }

    private async Task HandleLadderPendingInviteEndedAsync(
        string friendPetId,
        GameInviteStatus status,
        string reason,
        string message)
    {
        if (!IsLadderHost || _ladderRoom is null || !_ladderPendingInviteeIds.Remove(friendPetId))
        {
            return;
        }

        _ladderPendingInvitees.Remove(friendPetId);
        if (_ladderInviteeTrackers.TryGetValue(friendPetId, out var tracker))
        {
            var isExpiredOrUnregistered = status == GameInviteStatus.Expired || reason is "expired" or "unregistered";
            tracker.Status = (status, reason) switch
            {
                (_, "coins") => LadderParticipantStatus.InsufficientCoins,
                _ when tracker.Status == LadderParticipantStatus.InsufficientCoins && isExpiredOrUnregistered => LadderParticipantStatus.InsufficientCoins,
                (GameInviteStatus.Expired, _) or (_, "expired") => LadderParticipantStatus.TimedOut,
                (GameInviteStatus.Canceled, _) or (_, "canceled") => LadderParticipantStatus.Cancelled,
                _ => LadderParticipantStatus.Declined
            };
        }

        _ladderRoom.PendingInviteCount = _ladderPendingInviteeIds.Count;
        var maxPossible = _ladderRoom.Members.Count + _ladderRoom.PendingInviteCount;
        if (maxPossible < LabLadderEngine.MinParticipants)
        {
            await AbortLadderRoomAsHostAsync("최소 참가 인원(2명)을 충족하지 못해 사다리방을 취소했습니다.");
            return;
        }

        if (_ladderRoom.PendingInviteCount == 0 && _ladderRoom.HostConfirmDeadlineUtc is null)
        {
            _ladderRoom.HostConfirmDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(HostConfirmTimeoutOverrideSeconds);
        }

        SyncInviteesIntoRoom();
        RefreshLadderLobbyUi();
        if (_ladderRoom.PendingInviteCount == 0)
        {
            LadderStatusText = RequiresHostLobbyPrizeAdjustment
                ? $"참가 인원이 {_ladderRoom.Members.Count}명으로 변경되었습니다. 상품 수량을 {_ladderRoom.Members.Count}칸에 맞게 재조정해 주세요."
                : "모든 초대 응답이 완료되었습니다. '참가자 확정' 버튼을 눌러 주세요.";
        }
        else
        {
            LadderStatusText = $"{_social.GetFriendDisplayName(friendPetId)} · {message}";
        }

        await BroadcastLadderStateOrAbortAsync();
    }

    [RelayCommand]
    private async Task OpenLadderRoomAsync()
    {
        if (IsLadderRoomOpen || IsLadderAnimating)
        {
            return;
        }

        if (IsAnyGameBusy || HasIncomingInvite)
        {
            LadderStatusText = DescribeInviteReason("busy");
            return;
        }

        if (OnlineFriends.Count == 0)
        {
            LadderStatusText = "초대할 온라인 친구가 없습니다.";
            return;
        }

        var selectedEntries = LadderFriendPicker.GetSelectedEntries();
        if (selectedEntries.Count == 0)
        {
            selectedEntries = LadderInviteCandidates
                .Where(c => c.IsSelected)
                .Select(c => c.Friend)
                .ToList();
        }

        var invitees = selectedEntries
            .Take(LabLadderEngine.MaxParticipants - 1)
            .Select(f => new OnlineFriendChoice(f, _social.GetFriendDisplayName(f.PetId)))
            .ToList();

        if (invitees.Count == 0)
        {
            LadderStatusText = "초대할 친구를 1명 이상 선택해 주세요.";
            return;
        }

        if (!LadderFeeOptions.Contains(LadderEntryFee))
        {
            return;
        }

        var initialSlots = Math.Clamp(1 + invitees.Count, LabLadderEngine.MinParticipants, LabLadderEngine.MaxParticipants);
        var wager = LadderEntryFee;
        var prizeDtos = GetPrizeRowDtos();
        if (!LabLadderEngine.TryValidateStructuredPrizes(prizeDtos, initialSlots, wager > 0, out var initialPrizes, out var prizeError))
        {
            LadderStatusText = prizeError;
            return;
        }

        var sessionId = Guid.NewGuid().ToString("N");
        if (wager > 0)
        {
            if (!TryReserveMultiplayerWager(sessionId, wager))
            {
                LadderStatusText = DescribeInviteReason("coins");
                return;
            }

            NotifyCoinsChanged();
        }

        var myName = string.IsNullOrWhiteSpace(_profile.OwnerName) ? _profile.PetName : _profile.OwnerName;
        var inviteDeadline = NewInviteDeadline();

        _ladderPendingInviteeIds.Clear();
        _ladderPendingInvitees.Clear();
        _ladderJoinedFriends.Clear();
        _ladderInviteeTrackers.Clear();
        _ladderInitialPrizes.Clear();
        _ladderInitialPrizes.AddRange(initialPrizes);
        _ladderHostFriend = null;
        _ladderActiveWager = wager;
        _ladderPaidCoins = wager;
        _ladderStarting = false;

        _ladderInviteeTrackers[_profile.PetId] = new LadderInviteeTracker
        {
            PetId = _profile.PetId,
            DisplayName = myName,
            Friend = null,
            Status = LadderParticipantStatus.Host
        };

        foreach (var choice in invitees)
        {
            _ladderPendingInviteeIds.Add(choice.Friend.PetId);
            _ladderPendingInvitees[choice.Friend.PetId] = choice.Friend;
            _ladderInviteeTrackers[choice.Friend.PetId] = new LadderInviteeTracker
            {
                PetId = choice.Friend.PetId,
                DisplayName = choice.DisplayName,
                Friend = choice.Friend,
                Status = LadderParticipantStatus.Sending
            };
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
            PrizeRevision = 1,
            RequiresPrizeAgreement = false,
            Prizes = initialPrizes.ToList(),
            PrizeItems = prizeDtos,
            InviteDeadlineUtc = inviteDeadline,
            Members =
            [
                new LadderMemberStateDto
                {
                    PetId = _profile.PetId,
                    DisplayName = myName,
                    SelectedSlot = null,
                    AgreedPrizeRevision = 1
                }
            ]
        };
        SyncInviteesIntoRoom();

        IsLadderHost = true;
        IsLadderRoomOpen = true;
        IsLadderMultiplayerMode = true;
        LadderStage = LadderStage.Lobby;
        LadderResultLines.Clear();
        LadderResultCards.Clear();
        LadderBoardPrizesSummaryText = $"공개 상품: {LabLadderEngine.FormatPrizeSummary(initialPrizes)}";
        RefreshLadderLobbyUi();

        if (wager > 0 && !await PersistProfileAsync())
        {
            await AbortLadderRoomAsHostAsync(DescribeInviteReason("storage"));
            return;
        }

        if (_ladderRoom?.SessionId != sessionId)
        {
            return;
        }

        _ = WatchLadderConnectionsAsync(sessionId);
        var initialRoomJson = JsonSerializer.Serialize(_ladderRoom, ProtocolJson.PacketOptions);
        LadderStatusText = "친구에게 사다리 초대를 보내는 중입니다.";
        await Task.WhenAll(invitees.Select(choice => SendTrackedOfferAsync(
            choice.Friend, LabLadderEngine.GameTypeId, sessionId, wager, inviteDeadline, initialRoomJson)));
    }

    [RelayCommand]
    private async Task CancelSingleLadderInviteAsync(string? friendPetId)
    {
        if (!IsLadderHost || !IsLadderRoomOpen || _ladderStarting || _ladderRoom is null
            || _ladderRoom.Phase != LadderRoomPhase.WaitingInvites
            || string.IsNullOrWhiteSpace(friendPetId))
        {
            return;
        }

        if (!_ladderPendingInvitees.TryGetValue(friendPetId, out var targetFriend))
        {
            return;
        }

        await CancelTrackedInviteAsync(_ladderRoom.SessionId, targetFriend);
    }

    [RelayCommand]
    private async Task ApplyLobbyPrizeAdjustmentAsync()
    {
        if (!IsLadderHost || !IsLadderRoomOpen || _ladderStarting || _ladderRoom is null
            || _ladderRoom.Phase != LadderRoomPhase.WaitingInvites
            || _ladderRoom.PendingInviteCount != 0)
        {
            return;
        }

        var requiredCount = _ladderRoom.Members.Count;
        if (requiredCount < LabLadderEngine.MinParticipants)
        {
            LadderStatusText = "최소 2명 이상 참가해야 상품을 확정할 수 있습니다.";
            return;
        }

        var itemDtos = GetPrizeRowDtos();
        if (!LabLadderEngine.TryValidateStructuredPrizes(itemDtos, requiredCount, _ladderRoom.WagerCoins > 0, out var adjustedPrizes, out var error))
        {
            LadderStatusText = error;
            return;
        }

        _ladderRoom.SlotCount = requiredCount;
        _ladderRoom.PrizeItems = itemDtos;
        _ladderRoom.Prizes = adjustedPrizes;
        _ladderRoom.PrizeRevision++;
        _ladderRoom.RequiresPrizeAgreement = true;
        foreach (var m in _ladderRoom.Members)
        {
            m.AgreedPrizeRevision = string.Equals(m.PetId, _ladderRoom.HostPetId, StringComparison.OrdinalIgnoreCase)
                ? _ladderRoom.PrizeRevision
                : 0;
        }

        _ladderInitialPrizes.Clear();
        _ladderInitialPrizes.AddRange(adjustedPrizes);
        LadderBoardPrizesSummaryText = $"공개 상품: {LabLadderEngine.FormatPrizeSummary(adjustedPrizes)}";
        RefreshLadderLobbyUi();
        LadderStatusText = "상품을 재조정했습니다. 모든 참가자의 동의를 기다리는 중입니다.";
        await BroadcastLadderStateOrAbortAsync();
    }

    [RelayCommand]
    private async Task AgreeAdjustedPrizesAsync()
    {
        if (IsLadderHost || !IsLadderRoomOpen || _ladderStarting || _ladderRoom is null || _ladderHostFriend is null)
        {
            return;
        }

        var rev = _ladderRoom.PrizeRevision;
        var me = _ladderRoom.Members.FirstOrDefault(m => string.Equals(m.PetId, _profile.PetId, StringComparison.OrdinalIgnoreCase));
        if (me is not null)
        {
            me.AgreedPrizeRevision = rev;
        }

        RefreshLadderLobbyUi();
        LadderStatusText = "변경된 상품에 동의했습니다. 방장이 참가자를 확정할 때까지 기다려 주세요.";

        var sent = await _social.SendGameEventAsync(_ladderHostFriend, new GameEventPayload
        {
            GameType = LabLadderEngine.GameTypeId,
            SessionId = _ladderRoom.SessionId,
            Action = "agree-prizes",
            WagerCoins = _ladderActiveWager,
            Data = rev.ToString(CultureInfo.InvariantCulture)
        });

        if (!sent && _ladderRoom is not null)
        {
            var refunded = await RefundLadderEntryFeeIfPaidAsync();
            CloseLadderRoomState(refunded > 0
                ? "방장과의 연결이 끊겨 참가 코인을 반환했습니다."
                : "방장과의 연결이 끊겨 사다리방이 종료되었습니다.");
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
            if (_ladderRoom.PendingInviteCount > 0)
            {
                LadderStatusText = $"응답 대기 중인 친구({_ladderRoom.PendingInviteCount}명)가 모두 응답해야 참가자를 확정할 수 있습니다.";
            }
            else if (_ladderRoom.Members.Count < LabLadderEngine.MinParticipants)
            {
                LadderStatusText = "최소 2명 이상 참가해야 명단을 확정할 수 있습니다.";
            }
            else if (_ladderRoom.Prizes.Count != _ladderRoom.Members.Count)
            {
                LadderStatusText = $"참가 인원({_ladderRoom.Members.Count}명)에 맞게 상품 수량을 재조정한 뒤 적용해 주세요.";
            }
            else
            {
                LadderStatusText = "모든 참가자가 변경된 상품에 동의한 뒤에 확정할 수 있습니다.";
            }

            return;
        }

        if (!LabLadderEngine.TryConfirmParticipants(_ladderRoom, _ladderRoom.Prizes))
        {
            return;
        }

        _ladderRoom.SlotSelectionDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(SlotSelectionTimeoutOverrideSeconds);
        SyncInviteesIntoRoom();
        LadderBoardPrizesSummaryText = $"공개 상품: {LabLadderEngine.FormatPrizeSummary(_ladderRoom.Prizes)}";
        RefreshLadderLobbyUi();
        LadderStatusText = $"참가자가 확정되었습니다! {LabLadderEngine.SlotSelectionTimeoutSeconds}초 내에 시작 번호를 골라 주세요.";
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
        OnPropertyChanged(nameof(CanCancelLadderRoom));
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
            OnPropertyChanged(nameof(CanCancelLadderRoom));
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
        var targets = _ladderJoinedFriends.Values.ToArray();
        var readiness = new LadderStartReadiness(sessionId, targets, "ready-start");
        _ladderStartReadiness = readiness;
        LadderStatusText = "참가자별 사다리판 확인을 기다리는 중입니다.";
        if (!await WaitForLadderStartRepliesAsync(readiness, startDto.WagerCoins, startJson, "prepare-start"))
        {
            if (_ladderRoom?.SessionId == sessionId)
                await AbortLadderRoomAsHostAsync("일부 참가자의 준비를 확인하지 못해 사다리 게임을 취소했습니다.");
            return;
        }
        if (_ladderRoom?.SessionId != sessionId)
            return;

        var startReplies = new LadderStartReadiness(sessionId, targets, "started-start");
        _ladderStartReadiness = startReplies;
        var started = await WaitForLadderStartRepliesAsync(startReplies, startDto.WagerCoins, startJson, "start");
        if (_ladderRoom is null || _ladderRoom.SessionId != sessionId)
        {
            _ladderStarting = false;
            OnPropertyChanged(nameof(CanCancelLadderRoom));
            return;
        }

        if (!started)
        {
            await AbortLadderRoomAsHostAsync("일부 참가자에게 사다리판을 전송하지 못해 게임을 취소했습니다.");
            return;
        }

        var abortRefundCoins = _ladderPaidCoins;
        _ladderPaidCoins = 0;
        _ladderStarting = false;
        OnPropertyChanged(nameof(CanCancelLadderRoom));
        await ApplyStartedMultiplayerLadderAsync(startDto, abortRefundCoins);
    }

    private sealed class LadderStartReadiness(string session, IReadOnlyList<FriendEntry> targets, string replyAction)
    {
        public string Session { get; } = session;
        public IReadOnlyList<FriendEntry> Targets { get; } = targets;
        public string ReplyAction { get; } = replyAction;
        public DateTimeOffset Deadline { get; } = DateTimeOffset.UtcNow.AddSeconds(8);
        public ConcurrentDictionary<string, byte> Ready { get; } = new(StringComparer.OrdinalIgnoreCase);
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private LadderStartReadiness? _ladderStartReadiness;
    private string? _ladderPreparedStart;
    private string? _ladderAppliedStart;

    internal double HostConfirmTimeoutOverrideSeconds { get; set; } = LabLadderEngine.HostConfirmTimeoutSeconds;
    internal double SlotSelectionTimeoutOverrideSeconds { get; set; } = LabLadderEngine.SlotSelectionTimeoutSeconds;
    internal double ReconnectGraceTimeoutOverrideSeconds { get; set; } = LabLadderEngine.ReconnectGraceTimeoutSeconds;

    private async Task<bool> WaitForLadderStartRepliesAsync(LadderStartReadiness readiness, int wager, string start, string action)
    {
        var payload = new GameEventPayload
        {
            GameType = LabLadderEngine.GameTypeId, SessionId = readiness.Session, Action = action,
            WagerCoins = wager, Data = start, InviteExpiresAtUtc = readiness.Deadline
        };
        while (_ladderStartReadiness == readiness && DateTimeOffset.UtcNow < readiness.Deadline)
        {
            if (readiness.Completion.Task.IsCompleted)
                return await readiness.Completion.Task;

            var attempts = readiness.Targets.Where(friend => !readiness.Ready.ContainsKey(friend.PetId))
                .Select(friend => _social.SendGameEventAsync(friend, payload)).ToArray();
            if (attempts.Length == 0)
                return true;
            var batch = Task.WhenAll(attempts);
            if (await Task.WhenAny(batch, readiness.Completion.Task) == readiness.Completion.Task)
                return await readiness.Completion.Task;
            await batch;
            await Task.WhenAny(Task.Delay(InviteRetryInterval), readiness.Completion.Task);
        }
        return false;
    }

    private void ReceiveLadderReadiness(string sender, GameEventPayload packet)
    {
        if (_ladderStartReadiness is not { } readiness || readiness.Session != packet.SessionId
            || readiness.ReplyAction != packet.Action
            || readiness.Deadline != packet.InviteExpiresAtUtc
            || !readiness.Targets.Any(friend => string.Equals(friend.PetId, sender, StringComparison.OrdinalIgnoreCase)))
            return;
        if (packet.Data != "ready")
            readiness.Completion.TrySetResult(false);
        else if (readiness.Ready.TryAdd(sender, 0) && readiness.Ready.Count == readiness.Targets.Count)
            readiness.Completion.TrySetResult(true);
    }

    private void ReceiveLadderPrepare(FriendEntry host, GameEventPayload packet)
    {
        if (_ladderRoom is not { } room || packet.InviteExpiresAtUtc is not { } deadline
            || deadline <= DateTimeOffset.UtcNow || deadline > DateTimeOffset.UtcNow.AddSeconds(12))
            return;
        var ready = room.WagerCoins == _ladderPaidCoins
            && TryReadLadderPayload<LadderStartDto>(packet.Data, out var start) && start is not null
            && LabLadderEngine.TryValidateStart(start, room, _profile.PetId, out _, out _);
        if (ready && _ladderPreparedStart is null)
        {
            _ladderPreparedStart = packet.Data;
            OnPropertyChanged(nameof(CanCancelLadderRoom));
            _ = WaitForLadderStartDecisionAsync(room.SessionId, deadline);
        }

        _ = _social.SendGameEventAsync(host, new GameEventPayload
        {
            GameType = LabLadderEngine.GameTypeId, SessionId = room.SessionId, Action = "ready-start",
            WagerCoins = room.WagerCoins, Data = ready && _ladderPreparedStart == packet.Data ? "ready" : "rejected",
            InviteExpiresAtUtc = deadline
        });
    }

    private async Task WaitForLadderStartDecisionAsync(string session, DateTimeOffset deadline)
    {
        var delay = deadline - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay);
        RunOnUi(() =>
        {
            if (!_disposed && !IsLadderHost && _ladderRoom?.SessionId == session && _ladderPreparedStart is not null)
                _ = AbortGuestUnstartedLadderAsync(session);
        });
    }

    private async Task AbortGuestUnstartedLadderAsync(string session)
    {
        if (_disposed || IsLadderHost || _ladderRoom?.SessionId != session || _ladderPreparedStart is null
            || IsLadderAnimating || _ladderRoom.Phase == LadderRoomPhase.Running)
        {
            return;
        }

        _ladderPreparedStart = null;
        OnPropertyChanged(nameof(CanCancelLadderRoom));
        await CancelLadderRoomAsync();
    }

    private async Task WatchLadderConnectionsAsync(string session)
    {
        DateTimeOffset? guestDisconnectedSince = null;
        try
        {
            while (!_disposed && _ladderRoom?.SessionId == session)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), _inviteToken);
                if (_ladderRoom is not { } room || room.SessionId != session)
                    return;

                var now = DateTimeOffset.UtcNow;
                var activeDeadline = room.Phase switch
                {
                    LadderRoomPhase.WaitingInvites when room.PendingInviteCount > 0 => room.InviteDeadlineUtc,
                    LadderRoomPhase.WaitingInvites => room.HostConfirmDeadlineUtc,
                    LadderRoomPhase.PickingSlots => room.SlotSelectionDeadlineUtc,
                    _ => null
                };
                if (activeDeadline is { } deadlineVal)
                {
                    var sec = Math.Max(0, (int)Math.Ceiling((deadlineVal - now).TotalSeconds));
                    LadderLobbyRemainingText = $"남은 시간 {sec}초";
                }

                if (IsLadderHost)
                {
                    if (!_ladderStarting
                        && room.Phase == LadderRoomPhase.WaitingInvites
                        && room.PendingInviteCount == 0
                        && room.HostConfirmDeadlineUtc is { } hc
                        && now >= hc)
                    {
                        await AbortLadderRoomAsHostAsync($"참가자 확정 제한 시간({LabLadderEngine.HostConfirmTimeoutSeconds}초)이 초과되어 사다리방을 취소했습니다.");
                        return;
                    }

                    if (!_ladderStarting
                        && room.Phase == LadderRoomPhase.PickingSlots
                        && room.SlotSelectionDeadlineUtc is { } sc
                        && now >= sc)
                    {
                        await AbortLadderRoomAsHostAsync($"시작 위치 선택 제한 시간({LabLadderEngine.SlotSelectionTimeoutSeconds}초)이 초과되어 사다리방을 취소했습니다.");
                        return;
                    }

                    var disconnectChanged = false;
                    foreach (var friend in _ladderJoinedFriends.Values.ToArray())
                    {
                        if (!_ladderInviteeTrackers.TryGetValue(friend.PetId, out var tracker))
                        {
                            continue;
                        }

                        if (IsOnlineInviteTarget(friend.PetId))
                        {
                            if (tracker.DisconnectedSinceUtc is not null || tracker.Status == LadderParticipantStatus.Disconnected)
                            {
                                tracker.DisconnectedSinceUtc = null;
                                tracker.Status = LadderParticipantStatus.Joined;
                                disconnectChanged = true;
                            }
                        }
                        else
                        {
                            tracker.DisconnectedSinceUtc ??= now;
                            if (tracker.Status != LadderParticipantStatus.Disconnected)
                            {
                                tracker.Status = LadderParticipantStatus.Disconnected;
                                disconnectChanged = true;
                            }

                            if (now - tracker.DisconnectedSinceUtc.Value >= TimeSpan.FromSeconds(ReconnectGraceTimeoutOverrideSeconds))
                            {
                                await AbortLadderRoomAsHostAsync($"{tracker.DisplayName}와의 연결이 복구되지 않아 사다리방을 종료했습니다.");
                                return;
                            }
                        }
                    }

                    if (disconnectChanged)
                    {
                        SyncInviteesIntoRoom();
                        RefreshLadderLobbyUi();
                        await BroadcastLadderStateOrAbortAsync();
                    }
                }
                else if (_ladderHostFriend is { } host)
                {
                    if (IsOnlineInviteTarget(host.PetId))
                    {
                        guestDisconnectedSince = null;
                    }
                    else
                    {
                        guestDisconnectedSince ??= now;
                        if (now - guestDisconnectedSince.Value >= TimeSpan.FromSeconds(ReconnectGraceTimeoutOverrideSeconds))
                        {
                            var refunded = await RefundLadderEntryFeeIfPaidAsync();
                            if (_ladderRoom?.SessionId == session)
                                CloseLadderRoomState(refunded > 0
                                    ? "방장과의 연결이 복구되지 않아 참가 코인을 반환했습니다."
                                    : "방장과의 연결이 복구되지 않아 사다리방을 종료했습니다.");
                            return;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private async Task CancelLadderRoomAsync()
    {
        if (!CanCancelLadderRoom || _ladderRoom is null)
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
            if (_inviteExchanges.TryGetValue(InviteKey(host.PetId, sessionId), out var exchange))
            {
                exchange.Reason = "canceled";
                await SendInvitePacketAsync(exchange, Cancel);
                return;
            }
            _ = await _social.SendGameEventAsync(host, new GameEventPayload
            {
                GameType = LabLadderEngine.GameTypeId,
                SessionId = sessionId,
                Action = "decline",
                WagerCoins = wager
            });
        }
    }

    [RelayCommand]
    private void ResetLadderToSetup()
    {
        if (IsLadderRoomOpen || IsLadderAnimating)
        {
            return;
        }

        LadderStage = LadderStage.Setup;
        LadderResultLines.Clear();
        LadderResultCards.Clear();
        LadderResultHeadline = string.Empty;
        LadderResultSettlementText = string.Empty;
        LadderMyResultPrizeText = string.Empty;
        LadderStatusText = "상품과 참가비를 설정한 뒤 혼자 돌리거나 친구 초대 방을 여세요!";
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
                case "ready-start" or "started-start":
                    ReceiveLadderReadiness(senderPetId, p);
                    break;

                case "accept" when _ladderRoom.Phase == LadderRoomPhase.WaitingInvites && _ladderPendingInviteeIds.Remove(senderPetId):
                {
                    _ladderPendingInvitees.Remove(senderPetId);
                    _ladderJoinedFriends[senderPetId] = friend;
                    var shortName = string.IsNullOrWhiteSpace(friend.OwnerName) ? friend.PetName : friend.OwnerName;
                    if (_ladderInviteeTrackers.TryGetValue(senderPetId, out var tracker))
                    {
                        tracker.Status = LadderParticipantStatus.Joined;
                        tracker.DisconnectedSinceUtc = null;
                    }
                    else
                    {
                        _ladderInviteeTrackers[senderPetId] = new LadderInviteeTracker
                        {
                            PetId = senderPetId,
                            DisplayName = shortName,
                            Friend = friend,
                            Status = LadderParticipantStatus.Joined
                        };
                    }

                    if (!_ladderRoom.Members.Any(m => string.Equals(m.PetId, senderPetId, StringComparison.OrdinalIgnoreCase)))
                    {
                        _ladderRoom.Members.Add(new LadderMemberStateDto
                        {
                            PetId = senderPetId,
                            DisplayName = shortName,
                            SelectedSlot = null,
                            AgreedPrizeRevision = _ladderRoom.PrizeRevision
                        });
                    }

                    _ladderRoom.PendingInviteCount = _ladderPendingInviteeIds.Count;
                    if (_ladderRoom.PendingInviteCount == 0 && _ladderRoom.HostConfirmDeadlineUtc is null)
                    {
                        _ladderRoom.HostConfirmDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(HostConfirmTimeoutOverrideSeconds);
                    }

                    SyncInviteesIntoRoom();
                    RefreshLadderLobbyUi();
                    if (_ladderRoom.PendingInviteCount == 0)
                    {
                        LadderStatusText = RequiresHostLobbyPrizeAdjustment
                            ? $"참가 인원이 {_ladderRoom.Members.Count}명으로 확정되었습니다. 상품 수량을 재조정한 뒤 동의를 요청해 주세요."
                            : "모든 응답이 완료되었습니다. '참가자 확정' 버튼을 눌러 주세요.";
                    }

                    await BroadcastLadderStateOrAbortAsync();
                    break;
                }

                case "decline" or "cancel" when !_ladderStarting && !IsLadderAnimating && _ladderRoom.Phase != LadderRoomPhase.Running
                    && (_ladderPendingInviteeIds.Contains(senderPetId) || _ladderJoinedFriends.ContainsKey(senderPetId)):
                {
                    var wasJoinedInPicking = _ladderRoom.Phase == LadderRoomPhase.PickingSlots && _ladderJoinedFriends.ContainsKey(senderPetId);
                    _ladderPendingInviteeIds.Remove(senderPetId);
                    _ladderPendingInvitees.Remove(senderPetId);
                    _ladderJoinedFriends.Remove(senderPetId);
                    _ladderRoom.Members.RemoveAll(m => string.Equals(m.PetId, senderPetId, StringComparison.OrdinalIgnoreCase));
                    if (_ladderInviteeTrackers.TryGetValue(senderPetId, out var tracker))
                    {
                        tracker.Status = LadderParticipantStatus.Declined;
                        tracker.DisconnectedSinceUtc = null;
                    }

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

                    if (_ladderRoom.PendingInviteCount == 0 && _ladderRoom.HostConfirmDeadlineUtc is null)
                    {
                        _ladderRoom.HostConfirmDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(HostConfirmTimeoutOverrideSeconds);
                    }

                    SyncInviteesIntoRoom();
                    RefreshLadderLobbyUi();
                    if (_ladderRoom.PendingInviteCount == 0)
                    {
                        LadderStatusText = RequiresHostLobbyPrizeAdjustment
                            ? $"참가 인원이 {_ladderRoom.Members.Count}명으로 변경되었습니다. 상품 수량을 재조정한 뒤 동의를 요청해 주세요."
                            : "응답이 완료되었습니다. '참가자 확정' 버튼을 눌러 주세요.";
                    }

                    await BroadcastLadderStateOrAbortAsync();
                    break;
                }

                case "agree-prizes" when _ladderRoom.Phase == LadderRoomPhase.WaitingInvites && _ladderJoinedFriends.ContainsKey(senderPetId):
                {
                    if (int.TryParse(p.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var agreedRev)
                        && agreedRev == _ladderRoom.PrizeRevision)
                    {
                        var member = _ladderRoom.Members.FirstOrDefault(m => string.Equals(m.PetId, senderPetId, StringComparison.OrdinalIgnoreCase));
                        if (member is not null)
                        {
                            member.AgreedPrizeRevision = agreedRev;
                            RefreshLadderLobbyUi();
                            LadderStatusText = LabLadderEngine.AreAllGuestsAgreedOnPrizes(_ladderRoom)
                                ? "모든 참가자가 변경된 상품에 동의했습니다! '참가자 확정' 버튼을 눌러 주세요."
                                : $"{senderName}(이)가 변경된 상품에 동의했습니다.";
                            await BroadcastLadderStateOrAbortAsync();
                        }
                    }

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
                case "prepare-start":
                    ReceiveLadderPrepare(friend, p);
                    break;

                case "state":
                {
                    if (TryReadLadderPayload<LadderRoomStateDto>(p.Data, out var state) && state is not null
                        && LabLadderEngine.IsValidRoom(state, p.SessionId, senderPetId, _ladderActiveWager)
                        && LabLadderEngine.IsValidRoomTransition(_ladderRoom, _ladderInitialPrizes, state, _profile.PetId))
                    {
                        _ladderRoom = state;
                        LadderBoardPrizesSummaryText = $"공개 상품: {LabLadderEngine.FormatPrizeSummary(state.Prizes)}";
                        RefreshLadderLobbyUi();
                        if (RequiresGuestPrizeAgreement)
                        {
                            LadderStatusText = "방장이 참가 인원 변동에 맞춰 상품을 재조정했습니다. 확인 후 동의해 주세요.";
                        }
                        else if (state.Phase == LadderRoomPhase.PickingSlots)
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

                case "start" when _ladderPreparedStart == p.Data:
                {
                    if (TryReadLadderPayload<LadderStartDto>(p.Data, out var startDto) && startDto is not null)
                    {
                        await ApplyStartedMultiplayerLadderAsync(startDto);
                        if (_pendingLadderRun?.SessionId == p.SessionId)
                        {
                            _ladderAppliedStart = p.Data;
                            ReplyToAppliedLadderStart(friend, p);
                        }
                    }

                    break;
                }

                case "cancel" when !IsLadderAnimating && _ladderRoom.Phase != LadderRoomPhase.Running:
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

    private void ReplyToAppliedLadderStart(FriendEntry host, GameEventPayload packet)
    {
        if (packet.InviteExpiresAtUtc is not { } deadline)
            return;
        _ = _social.SendGameEventAsync(host, new GameEventPayload
        {
            GameType = LabLadderEngine.GameTypeId, SessionId = packet.SessionId, Action = "started-start",
            WagerCoins = packet.WagerCoins, Data = "ready", InviteExpiresAtUtc = deadline
        });
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

        var markedDisconnected = false;
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < targets.Length; i++)
        {
            if (results[i])
            {
                continue;
            }

            if (_ladderInviteeTrackers.TryGetValue(targets[i].PetId, out var tracker))
            {
                tracker.DisconnectedSinceUtc ??= now;
                if (tracker.Status != LadderParticipantStatus.Disconnected)
                {
                    tracker.Status = LadderParticipantStatus.Disconnected;
                    markedDisconnected = true;
                }
            }
        }

        if (markedDisconnected)
        {
            SyncInviteesIntoRoom();
            RefreshLadderLobbyUi();
            LadderStatusText = $"일부 참가자의 응답이 지연되고 있습니다. ({LabLadderEngine.ReconnectGraceTimeoutSeconds}초 내 재연결 대기)";
        }
    }

    private async Task AbortLadderRoomAsHostAsync(string statusMessage)
    {
        var sessionId = _ladderRoom?.SessionId ?? string.Empty;
        if (!string.IsNullOrEmpty(sessionId))
        {
            if (_abortedLadderSessions.Count >= RecentInvitationCapacity)
            {
                _abortedLadderSessions.Clear();
            }

            if (!_abortedLadderSessions.Add(sessionId))
            {
                return;
            }
        }

        var wager = _ladderActiveWager;
        var notifyTargets = _ladderJoinedFriends.Values
            .Concat(_ladderPendingInvitees.Values)
            .GroupBy(f => f.PetId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        var refunded = _ladderPaidCoins;
        var finalMessage = refunded > 0 && !statusMessage.Contains("반환", StringComparison.Ordinal)
            ? $"{statusMessage} (참가 {refunded} 코인 반환)"
            : statusMessage;

        CloseLadderRoomState(finalMessage);
        if (refunded > 0)
            await SettleMultiplayerWagerAsync(sessionId, refunded);

        if (!string.IsNullOrEmpty(sessionId))
        {
            foreach (var friend in notifyTargets)
            {
                if (_inviteExchanges.TryGetValue(InviteKey(friend.PetId, sessionId), out var exchange))
                {
                    exchange.Status = GameInviteStatus.Canceled;
                    exchange.Reason = "canceled";
                    exchange.TerminalPacket = Cancel;
                    _ = SendTerminalInvitePacketAsync(exchange, Cancel);
                    continue;
                }

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
        LadderStage = LadderStage.Setup;
        LadderAnimationCancelled?.Invoke(this, EventArgs.Empty);

        if (run.AbortRefundCoins > 0)
        {
            var refund = run.AbortRefundCoins;
            run.AbortRefundCoins = 0;
            await SettleMultiplayerWagerAsync(run.SessionId, refund);
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
        LadderResultCards.Clear();
    }

    private async Task<int> RefundLadderEntryFeeIfPaidAsync()
    {
        var refund = _ladderPaidCoins;
        if (refund <= 0)
        {
            return 0;
        }

        _ladderPaidCoins = 0;
        await SettleMultiplayerWagerAsync(_ladderRoom?.SessionId, refund);
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

    private void SyncInviteesIntoRoom()
    {
        if (_ladderRoom is null)
        {
            return;
        }

        _ladderRoom.Invitees = _ladderInviteeTrackers.Values
            .Select(t => new LadderInviteeStateDto
            {
                PetId = t.PetId,
                DisplayName = t.DisplayName,
                Status = t.Status
            })
            .ToList();
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
        var participantIds = startDto.ParticipantPetIds.ToList();
        var participantPortraits = participantIds.Select(ResolveMemberPortrait).ToList();
        var myCol = participantIds.FindIndex(id => string.Equals(id, _profile.PetId, StringComparison.OrdinalIgnoreCase));

        var configuredPrizesSummary = LabLadderEngine.FormatPrizeSummary(room.Prizes);
        CloseLadderRoomStateInternal(resetStageToSetup: false);
        BeginLadderReveal(
            startDto.SessionId,
            board,
            traces,
            participantIds,
            participantPortraits,
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
        IReadOnlyList<string> participantPetIds,
        IReadOnlyList<ImageSource?> participantPortraits,
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
            ParticipantPetIds = participantPetIds,
            ParticipantPortraits = participantPortraits,
            MyStartColumn = myStartColumn,
            WagerCoins = wagerCoins,
            IsMultiplayer = isMultiplayer,
            HostFriend = hostFriend,
            AbortRefundCoins = abortRefundCoins,
            RevealedColumns = new bool[traces.Count],
            AnimationProgress = 0.0
        };

        LadderBoardPrizesSummaryText = string.IsNullOrWhiteSpace(configuredPrizesSummary)
            ? $"공개 상품: {LabLadderEngine.FormatPrizeSummary(board.Prizes)}"
            : $"공개 상품: {configuredPrizesSummary}";
        LadderResultLines.Clear();
        LadderResultCards.Clear();
        foreach (var r in traces)
        {
            LadderResultLines.Add($"{r.Participant}  →  ?");
        }

        LadderStatusText = "사다리 내려가는 중... (하단 도착 시 결과가 공개됩니다)";
        IsLadderAnimating = true;
        LadderStage = LadderStage.Running;
        OnPropertyChanged(nameof(ActiveLadderBoard));
        OnPropertyChanged(nameof(ActiveLadderAnimationProgress));
        OnPropertyChanged(nameof(ActiveLadderRevealedColumns));
        OnPropertyChanged(nameof(ActiveLadderParticipantPortraits));

        if (isMultiplayer && LadderBoardGenerated is null)
        {
            SelectedGameIndex = 3;
        }

        LadderBoardGenerated?.Invoke(this, board);
    }

    /// <summary>
    /// 창을 닫았다가 다시 열 때도 이어서 재생할 수 있도록 현재 사다리 진행 비율만 보존한다.
    /// 창 종료만으로 결과를 강제 공개하거나 코인을 정산하지 않는다.
    /// </summary>
    public void SaveLadderAnimationProgress(double progress)
    {
        if (_pendingLadderRun is { IsCompleted: false } run)
        {
            run.AnimationProgress = Math.Clamp(progress, 0.0, 0.999);
            OnPropertyChanged(nameof(ActiveLadderAnimationProgress));
            OnPropertyChanged(nameof(ActiveLadderRevealedColumns));
        }
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

        LadderStatusText = $"{result.Participant} 도착 → {result.Prize}";
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
        run.AnimationProgress = 1.0;
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
        BuildLadderResultCards(run);

        var myResult = run.MyStartColumn >= 0 && run.MyStartColumn < run.Traces.Count
            ? run.Traces[run.MyStartColumn]
            : null;
        LadderMyResultPrizeText = myResult?.Prize ?? string.Empty;

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
                if (run.IsMultiplayer)
                    await SettleMultiplayerWagerAsync(run.SessionId, jackpot);
                else
                {
                    _progression.AddCoins(_profile, jackpot);
                    NotifyCoinsChanged();
                    await PersistProfileAsync();
                }

                var prefix = run.IsMultiplayer ? "동시 사다리 당첨!" : "사다리 잭팟!";
                LadderResultHeadline = prefix;
                LadderResultSettlementText = $"+{jackpot} 코인 획득 (참가 {run.WagerCoins} 코인 × {run.Traces.Count}명)";
                LadderStatusText = $"{prefix} {myResult.Participant} → {myResult.Prize} (+{jackpot} 코인)";
                LadderStage = LadderStage.Result;
                PetEmotionTriggered?.Invoke(this, true);
                if (run.IsMultiplayer)
                {
                    GameNotification?.Invoke(this, $"사다리 잭팟 당첨! (+{jackpot} 코인)");
                }

                return;
            }
        }

        if (run.IsMultiplayer && run.WagerCoins > 0)
            await SettleMultiplayerWagerAsync(run.SessionId, 0);

        if (myResult is not null)
        {
            if (LabLadderEngine.IsLosingPrize(myResult.Prize))
            {
                PetEmotionTriggered?.Invoke(this, false);
            }
            else if (run.WagerCoins == 0 && LabLadderEngine.IsJackpot(myResult.Prize))
            {
                PetEmotionTriggered?.Invoke(this, true);
            }

            if (run.IsMultiplayer)
            {
                var modeLabel = run.WagerCoins > 0 ? "동시 사다리 결과" : "동시 사다리(무료) 결과";
                LadderResultHeadline = $"{myResult.Participant}의 결과: {myResult.Prize}";
                LadderResultSettlementText = run.WagerCoins > 0
                    ? $"참가비 {run.WagerCoins} 코인 정산 완료"
                    : "무료 모드 · 코인 변동 없음";
                LadderStatusText = $"{modeLabel}: {myResult.Participant} → {myResult.Prize}";
                GameNotification?.Invoke(this, $"사다리 결과: {myResult.Prize}");
            }
            else
            {
                LadderResultHeadline = "사다리 추첨 완료!";
                LadderResultSettlementText = run.WagerCoins > 0
                    ? $"참가비 {run.WagerCoins} 코인 정산 완료"
                    : "무료 모드 · 코인 변동 없음";
                LadderStatusText = $"사다리 완료! ({run.Traces.Count}명 참여)";
            }
        }
        else
        {
            LadderResultHeadline = "사다리 추첨 완료!";
            LadderResultSettlementText = run.WagerCoins > 0 ? $"참가비 {run.WagerCoins} 코인" : "무료 모드 · 코인 변동 없음";
            LadderStatusText = $"사다리 완료! ({run.Traces.Count}명 참여)";
        }

        LadderStage = LadderStage.Result;
    }

    private void BuildLadderResultCards(PendingLadderRun run)
    {
        LadderResultCards.Clear();
        for (var i = 0; i < run.Traces.Count; i++)
        {
            var trace = run.Traces[i];
            var petId = i < run.ParticipantPetIds.Count ? run.ParticipantPetIds[i] : string.Empty;
            var portrait = i < run.ParticipantPortraits.Count ? run.ParticipantPortraits[i] : null;
            var isMe = i == run.MyStartColumn && (run.IsMultiplayer || !string.IsNullOrEmpty(petId));
            var badge = isMe ? "내 결과" : $"출발 {i + 1}번";
            LadderResultCards.Add(new LadderParticipantCardItem(
                petId,
                trace.Participant,
                portrait,
                IsHost: false,
                IsMe: isMe,
                IsPending: false,
                StatusBadge: badge,
                SelectedSlot: trace.StartColumn,
                PrizeText: trace.Prize,
                AccentColorHex: LadderTrackAccentHexes[i % LadderTrackAccentHexes.Length]));
        }
    }

    private ImageSource? ResolveMemberPortrait(string petId)
    {
        if (string.IsNullOrWhiteSpace(petId))
        {
            return null;
        }

        if (string.Equals(petId, _profile.PetId, StringComparison.OrdinalIgnoreCase))
        {
            return MyPetPortrait;
        }

        return TryGetPortrait(ResolveFriendAppearance(petId));
    }

    private static (string TextHex, string BgHex) GetStatusBadgePalette(LadderParticipantStatus status) => status switch
    {
        LadderParticipantStatus.Host => ("#FFBE123C", "#FFFFF1F2"),
        LadderParticipantStatus.Sending => ("#FF1D4ED8", "#FFEFF6FF"),
        LadderParticipantStatus.WaitingResponse => ("#FF92400E", "#FFFEF3C7"),
        LadderParticipantStatus.Joined => ("#FF15803D", "#FFECFDF5"),
        LadderParticipantStatus.Declined => ("#FF6B7280", "#FFF3F4F6"),
        LadderParticipantStatus.InsufficientCoins => ("#FFB91C1C", "#FFFEF2F2"),
        LadderParticipantStatus.TimedOut => ("#FFB45309", "#FFFFFBEB"),
        LadderParticipantStatus.Disconnected => ("#FF7C2D12", "#FFFFEDD5"),
        LadderParticipantStatus.Cancelled => ("#FF64748B", "#FFF1F5F9"),
        _ => ("#FF374151", "#FFF3F4F6")
    };

    private void RefreshLadderLobbyUi()
    {
        LadderSlots.Clear();
        LadderParticipantCards.Clear();

        if (_ladderRoom is null)
        {
            IsLadderWaitingInvites = false;
            IsLadderPickingPhase = false;
            CanConfirmLadderParticipants = false;
            ShowConfirmLadderButton = false;
            CanStartLadderGame = false;
            ShowStartLadderButton = false;
            RequiresHostLobbyPrizeAdjustment = false;
            RequiresGuestPrizeAgreement = false;
            LadderPrizeAgreementBannerText = string.Empty;
            LadderInvitedTotalText = "전체 초대 0명";
            LadderJoinedCountText = "현재 입장 0명";
            LadderLobbyRemainingText = string.Empty;
            LadderLobbySummaryText = string.Empty;
            LadderRoomPrizesText = string.Empty;
            LadderRoomMembersText = string.Empty;
            OnPropertyChanged(nameof(CanCancelLadderRoom));
            return;
        }

        var isPicking = _ladderRoom.Phase == LadderRoomPhase.PickingSlots;
        var joinedCount = _ladderRoom.Members.Count;
        var pickedCount = _ladderRoom.Members.Count(m => m.SelectedSlot.HasValue);

        for (var col = 0; col < _ladderRoom.SlotCount; col++)
        {
            var occupant = _ladderRoom.Members.FirstOrDefault(m => m.SelectedSlot == col);
            var isMine = occupant is not null && string.Equals(occupant.PetId, _profile.PetId, StringComparison.OrdinalIgnoreCase);
            var occupantPortrait = occupant is null ? null : ResolveMemberPortrait(occupant.PetId);
            LadderSlots.Add(new LadderSlotOption(
                col,
                $"{col + 1}번",
                !isPicking
                    ? "확정 대기"
                    : (occupant is null ? "빈 자리" : (isMine ? $"{occupant.DisplayName} (나)" : occupant.DisplayName)),
                occupant is not null,
                isMine,
                isPicking,
                occupantPortrait));
        }

        if (!isPicking && _ladderRoom.Invitees.Count > 0)
        {
            for (var i = 0; i < _ladderRoom.Invitees.Count; i++)
            {
                var invitee = _ladderRoom.Invitees[i];
                var isHost = string.Equals(invitee.PetId, _ladderRoom.HostPetId, StringComparison.OrdinalIgnoreCase);
                var isMe = string.Equals(invitee.PetId, _profile.PetId, StringComparison.OrdinalIgnoreCase);
                var member = _ladderRoom.Members.FirstOrDefault(m => string.Equals(m.PetId, invitee.PetId, StringComparison.OrdinalIgnoreCase));
                var status = isHost ? LadderParticipantStatus.Host : invitee.Status;
                var awaitingAgreement = !isHost && status == LadderParticipantStatus.Joined && _ladderRoom.RequiresPrizeAgreement
                    && member is not null && member.AgreedPrizeRevision != _ladderRoom.PrizeRevision;
                var badge = awaitingAgreement
                    ? "상품 동의 대기"
                    : LabLadderEngine.GetStatusDisplayText(status);
                var (textHex, bgHex) = awaitingAgreement
                    ? ("#FFB45309", "#FFFFFBEB")
                    : GetStatusBadgePalette(status);
                var isPending = status is LadderParticipantStatus.Sending or LadderParticipantStatus.WaitingResponse or LadderParticipantStatus.InsufficientCoins;
                var canCancelInvite = IsLadderHost && !isPicking && !isHost
                    && status is LadderParticipantStatus.Sending or LadderParticipantStatus.WaitingResponse or LadderParticipantStatus.InsufficientCoins;

                LadderParticipantCards.Add(new LadderParticipantCardItem(
                    invitee.PetId,
                    invitee.DisplayName,
                    ResolveMemberPortrait(invitee.PetId),
                    isHost,
                    isMe,
                    isPending,
                    badge,
                    member?.SelectedSlot,
                    string.Empty,
                    LadderTrackAccentHexes[i % LadderTrackAccentHexes.Length],
                    status,
                    canCancelInvite,
                    textHex,
                    bgHex));
            }
        }
        else
        {
            for (var i = 0; i < _ladderRoom.Members.Count; i++)
            {
                var member = _ladderRoom.Members[i];
                var isHost = string.Equals(member.PetId, _ladderRoom.HostPetId, StringComparison.OrdinalIgnoreCase);
                var isMe = string.Equals(member.PetId, _profile.PetId, StringComparison.OrdinalIgnoreCase);
                var status = isHost ? LadderParticipantStatus.Host : LadderParticipantStatus.Joined;
                var badge = !isPicking
                    ? LabLadderEngine.GetStatusDisplayText(status)
                    : (member.SelectedSlot is int s ? $"{s + 1}번 선택" : "위치 선택 중");
                var (textHex, bgHex) = !isPicking
                    ? GetStatusBadgePalette(status)
                    : (member.SelectedSlot.HasValue ? ("#FFBE123C", "#FFFFF1F2") : ("#FF92400E", "#FFFEF3C7"));

                LadderParticipantCards.Add(new LadderParticipantCardItem(
                    member.PetId,
                    member.DisplayName,
                    ResolveMemberPortrait(member.PetId),
                    isHost,
                    isMe,
                    IsPending: false,
                    StatusBadge: badge,
                    SelectedSlot: member.SelectedSlot,
                    PrizeText: string.Empty,
                    AccentColorHex: LadderTrackAccentHexes[i % LadderTrackAccentHexes.Length],
                    Status: status,
                    CanCancelInvite: false,
                    StatusTextColorHex: textHex,
                    StatusBgColorHex: bgHex));
            }

            if (IsLadderHost && !isPicking)
            {
                foreach (var pendingFriend in _ladderPendingInvitees.Values)
                {
                    var name = _social.GetFriendDisplayName(pendingFriend.PetId);
                    var (textHex, bgHex) = GetStatusBadgePalette(LadderParticipantStatus.WaitingResponse);
                    LadderParticipantCards.Add(new LadderParticipantCardItem(
                        pendingFriend.PetId,
                        name,
                        TryGetPortrait(ResolveFriendAppearance(pendingFriend.PetId, pendingFriend)),
                        IsHost: false,
                        IsMe: false,
                        IsPending: true,
                        StatusBadge: LabLadderEngine.GetStatusDisplayText(LadderParticipantStatus.WaitingResponse),
                        SelectedSlot: null,
                        PrizeText: string.Empty,
                        AccentColorHex: "#FF9CA3AF",
                        Status: LadderParticipantStatus.WaitingResponse,
                        CanCancelInvite: true,
                        StatusTextColorHex: textHex,
                        StatusBgColorHex: bgHex));
                }
            }
        }

        var invitedCount = _ladderRoom.Invitees.Count > 0
            ? Math.Max(0, _ladderRoom.Invitees.Count - 1)
            : Math.Max(0, joinedCount - 1 + _ladderRoom.PendingInviteCount);
        LadderInvitedTotalText = $"전체 초대 {invitedCount}명";
        LadderJoinedCountText = $"현재 입장 {joinedCount}명";

        var now = DateTimeOffset.UtcNow;
        var activeDeadline = _ladderRoom.Phase switch
        {
            LadderRoomPhase.WaitingInvites when _ladderRoom.PendingInviteCount > 0 => _ladderRoom.InviteDeadlineUtc,
            LadderRoomPhase.WaitingInvites => _ladderRoom.HostConfirmDeadlineUtc,
            LadderRoomPhase.PickingSlots => _ladderRoom.SlotSelectionDeadlineUtc,
            _ => null
        };
        if (activeDeadline is { } deadlineVal)
        {
            var remainingSec = Math.Max(0, (int)Math.Ceiling((deadlineVal - now).TotalSeconds));
            LadderLobbyRemainingText = $"남은 시간 {remainingSec}초";
        }
        else
        {
            LadderLobbyRemainingText = string.Empty;
        }

        var myMember = _ladderRoom.Members.FirstOrDefault(m => string.Equals(m.PetId, _profile.PetId, StringComparison.OrdinalIgnoreCase));
        var feeText = _ladderRoom.WagerCoins > 0 ? $"{_ladderRoom.WagerCoins}코인" : "무료 (0코인)";
        LadderRoomPrizesText = $"참가비: {feeText} · 설정 상품: {LabLadderEngine.FormatPrizeSummary(_ladderRoom.Prizes)}";
        LadderRoomMembersText = "참가자: " + string.Join(", ", _ladderRoom.Members.Select(m =>
            string.Equals(m.PetId, _profile.PetId, StringComparison.OrdinalIgnoreCase)
                ? $"{m.DisplayName}(나)"
                : m.DisplayName));

        RequiresHostLobbyPrizeAdjustment = IsLadderHost
            && !isPicking
            && _ladderRoom.PendingInviteCount == 0
            && _ladderRoom.Members.Count >= LabLadderEngine.MinParticipants
            && _ladderRoom.Prizes.Count != _ladderRoom.Members.Count;

        RequiresGuestPrizeAgreement = !IsLadderHost
            && !isPicking
            && _ladderRoom.RequiresPrizeAgreement
            && myMember is not null
            && myMember.AgreedPrizeRevision != _ladderRoom.PrizeRevision;

        LadderPrizeAgreementBannerText = RequiresGuestPrizeAgreement
            ? $"방장이 참가 인원({_ladderRoom.Members.Count}명)에 맞춰 상품을 [{LabLadderEngine.FormatPrizeSummary(_ladderRoom.Prizes)}](으)로 재조정했습니다. 확인 후 동의해 주세요."
            : string.Empty;

        IsLadderWaitingInvites = !isPicking;
        IsLadderPickingPhase = isPicking;
        CanConfirmLadderParticipants = IsLadderHost && LabLadderEngine.CanConfirmParticipants(_ladderRoom);
        ShowConfirmLadderButton = IsLadderHost && !isPicking;
        CanStartLadderGame = IsLadderHost && LabLadderEngine.CanStartGame(_ladderRoom);
        ShowStartLadderButton = IsLadderHost && isPicking;
        LadderStage = isPicking ? LadderStage.Picking : LadderStage.Lobby;

        if (!isPicking)
        {
            var pendingText = _ladderRoom.PendingInviteCount > 0 ? $" · 응답 대기 {_ladderRoom.PendingInviteCount}명" : " · 전원 응답 완료";
            LadderLobbySummaryText = $"참가자 대기 ({joinedCount}명 입장{pendingText})";
        }
        else
        {
            LadderLobbySummaryText = $"시작 위치 선택 ({pickedCount}/{joinedCount}명 완료)";
        }

        OnPropertyChanged(nameof(CanCancelLadderRoom));
        NotifyLadderPrizeAllocationChanged();
    }

    private void CloseLadderRoomState(string statusMessage, bool resetStageToSetup = true)
    {
        CloseLadderRoomStateInternal(resetStageToSetup);
        LadderStatusText = statusMessage;
    }

    private void CloseLadderRoomStateInternal(bool resetStageToSetup = true)
    {
        _ladderStartReadiness?.Completion.TrySetResult(false);
        _ladderStartReadiness = null;
        _ladderPreparedStart = null;
        _ladderAppliedStart = null;
        _ladderRoom = null;
        _ladderHostFriend = null;
        _ladderInitialPrizes.Clear();
        _ladderPendingInviteeIds.Clear();
        _ladderPendingInvitees.Clear();
        _ladderJoinedFriends.Clear();
        _ladderInviteeTrackers.Clear();
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
        RequiresHostLobbyPrizeAdjustment = false;
        RequiresGuestPrizeAgreement = false;
        LadderPrizeAgreementBannerText = string.Empty;
        LadderInvitedTotalText = "전체 초대 0명";
        LadderJoinedCountText = "현재 입장 0명";
        LadderLobbyRemainingText = string.Empty;
        LadderLobbySummaryText = string.Empty;
        LadderRoomPrizesText = string.Empty;
        LadderRoomMembersText = string.Empty;
        LadderSlots.Clear();
        LadderParticipantCards.Clear();
        OnPropertyChanged(nameof(CanCancelLadderRoom));
        OnPropertyChanged(nameof(IsLadderInProgress));
        OnPropertyChanged(nameof(CanSwitchLadderMode));
        if (resetStageToSetup && !IsLadderAnimating)
        {
            LadderStage = LadderStage.Setup;
        }
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

        if (IsAnyGameBusy || HasIncomingInvite)
        {
            LadderStatusText = DescribeInviteReason("busy");
            return;
        }

        if (OnlineFriends.Count == 0)
        {
            LadderStatusText = "결과를 공유할 온라인 친구가 없습니다.";
            return;
        }

        var summary = "사다리 결과: " + string.Join(" / ", _lastLadderResults.Select(r => $"{r.Participant}→{r.Prize}"));
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
            ? $"온라인 친구 {sentCount}명에게 사다리 결과를 공유했습니다."
            : "친구들에게 결과를 전송하지 못했습니다.";
    }
}
