using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Games;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;

namespace LabPet.Client.ViewModels;

public sealed partial class CoinGamesViewModel
{
    // ══════════════════════════════════════════════════════════════════════
    // 4. 연구실 공동 사다리타기 (LAN 대기실 · 위치 선택 · 동시 시작 + 솔로)
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task OpenLadderRoomAsync()
    {
        if (IsLadderRoomOpen)
        {
            return;
        }

        if (OnlineFriends.Count == 0)
        {
            LadderStatusText = "초대할 온라인 친구가 없습니다. 혼자 바로 돌리기를 이용해 주세요.";
            return;
        }

        var prizes = ParseTokens(LadderPrizesText);
        if (prizes.Count(LabLadderEngine.IsJackpot) > 1 || !LadderFeeOptions.Contains(LadderEntryFee))
        {
            LadderStatusText = "공동 사다리에서는 잭팟을 한 칸까지만 지정할 수 있습니다.";
            return;
        }
        var wager = LadderEntryFee;
        if (wager > 0)
        {
            var (ok, msg) = _progression.TrySpendCoins(_profile, wager);
            if (!ok)
            {
                LadderStatusText = msg;
                return;
            }

        }

        var invitees = OnlineFriends.Take(LabLadderEngine.MaxParticipants - 1).ToList();
        var sessionId = Guid.NewGuid().ToString("N");
        var myName = string.IsNullOrWhiteSpace(_profile.OwnerName) ? _profile.PetName : _profile.OwnerName;

        _ladderPendingInviteeIds.Clear();
        _ladderJoinedFriends.Clear();
        _ladderHostFriend = null;
        _ladderActiveWager = wager;

        foreach (var choice in invitees)
        {
            _ladderPendingInviteeIds.Add(choice.Friend.PetId);
        }

        var initialSlots = Math.Clamp(1 + invitees.Count, LabLadderEngine.MinParticipants, LabLadderEngine.MaxParticipants);
        while (prizes.Count < initialSlots)
        {
            prizes.Add("통과");
        }

        // 조기 수락(accept) 패킷이 도착해도 유실되지 않도록 초대 전송 전에 _ladderRoom을 먼저 초기화한다.
        _ladderRoom = new LadderRoomStateDto
        {
            SessionId = sessionId,
            HostPetId = _profile.PetId,
            WagerCoins = wager,
            SlotCount = initialSlots,
            PendingInviteCount = invitees.Count,
            Prizes = prizes.Take(initialSlots).ToList(),
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
            }
        }

        if (sentCount == 0 && _ladderJoinedFriends.Count == 0)
        {
            if (wager > 0)
            {
                _progression.AddCoins(_profile, wager);
                await PersistProfileAsync();
            }

            CloseLadderRoomState("온라인 친구에게 초대를 전송하지 못했습니다.");
            return;
        }

        _ladderRoom.PendingInviteCount = _ladderPendingInviteeIds.Count;
        RecalculateHostSlotCount();
        RefreshLadderLobbyUi();
        await BroadcastLadderStateAsync();
        LadderStatusText = $"친구 {sentCount}명에게 초대 발송! 원하는 시작 번호를 골라 주세요.";
    }

    [RelayCommand]
    private async Task PickLadderSlotAsync(int slotIndex)
    {
        if (!IsLadderRoomOpen || _ladderRoom is null)
        {
            return;
        }

        if (IsLadderHost)
        {
            if (!LabLadderEngine.TryPickSlot(_ladderRoom, _profile.PetId, slotIndex))
            {
                LadderStatusText = $"{slotIndex + 1}번 자리는 이미 다른 친구가 선택했습니다.";
                return;
            }

            RefreshLadderLobbyUi();
            await BroadcastLadderStateAsync();

            if (_ladderRoom is { } currentRoom && LabLadderEngine.IsReadyToAutoStart(currentRoom))
            {
                await StartMultiplayerLadderAsync();
            }
        }
        else if (_ladderHostFriend is not null)
        {
            await _social.SendGameEventAsync(_ladderHostFriend, new GameEventPayload
            {
                GameType = LabLadderEngine.GameTypeId,
                SessionId = _ladderRoom.SessionId,
                Action = "pick",
                WagerCoins = _ladderActiveWager,
                Data = slotIndex.ToString()
            });
        }
    }

    [RelayCommand]
    private async Task ForceStartLadderRoomAsync()
    {
        if (!IsLadderRoomOpen || !IsLadderHost || _ladderRoom is null)
        {
            return;
        }

        if (!LabLadderEngine.AreJoinedMembersReady(_ladderRoom))
        {
            LadderStatusText = "입장한 참가자(최소 2명)가 모두 위치를 선택해야 시작할 수 있습니다.";
            return;
        }

        await StartMultiplayerLadderAsync();
    }

    [RelayCommand]
    private async Task CancelLadderRoomAsync()
    {
        if (!IsLadderRoomOpen || _ladderStarting || _ladderRoom is null)
        {
            return;
        }

        var sessionId = _ladderRoom.SessionId;
        var wager = _ladderActiveWager;
        var isHost = IsLadderHost;
        var host = _ladderHostFriend;
        var joined = _ladderJoinedFriends.Values.ToArray();
        CloseLadderRoomState("사다리방을 취소했습니다.");
        if (wager > 0)
        {
            _progression.AddCoins(_profile, wager);
            await PersistProfileAsync();
        }
        if (isHost)
        {
            foreach (var friend in joined)
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
        else if (host is not null)
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
                case "accept" when _ladderPendingInviteeIds.Remove(senderPetId):
                {
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
                    RecalculateHostSlotCount();
                    RefreshLadderLobbyUi();
                    await BroadcastLadderStateAsync();
                    break;
                }

                case "decline" when _ladderPendingInviteeIds.Contains(senderPetId) || _ladderJoinedFriends.ContainsKey(senderPetId):
                {
                    _ladderPendingInviteeIds.Remove(senderPetId);
                    _ladderJoinedFriends.Remove(senderPetId);
                    _ladderRoom.Members.RemoveAll(m => string.Equals(m.PetId, senderPetId, StringComparison.OrdinalIgnoreCase));
                    _ladderRoom.PendingInviteCount = _ladderPendingInviteeIds.Count;

                    if (_ladderRoom.Members.Count < LabLadderEngine.MinParticipants && _ladderRoom.PendingInviteCount == 0)
                    {
                        var wager = _ladderActiveWager;
                        CloseLadderRoomState("참가할 친구가 없어 사다리방이 종료되었습니다.");
                        if (wager > 0)
                        {
                            _progression.AddCoins(_profile, wager);
                            await PersistProfileAsync();
                        }

                        return;
                    }

                    RecalculateHostSlotCount();
                    RefreshLadderLobbyUi();
                    await BroadcastLadderStateAsync();

                    if (_ladderRoom is { } currentRoom && LabLadderEngine.IsReadyToAutoStart(currentRoom))
                    {
                        await StartMultiplayerLadderAsync();
                    }

                    break;
                }

                case "pick" when _ladderJoinedFriends.ContainsKey(senderPetId):
                {
                    if (int.TryParse(p.Data, out var slotIdx) && LabLadderEngine.TryPickSlot(_ladderRoom, senderPetId, slotIdx))
                    {
                        RefreshLadderLobbyUi();
                        await BroadcastLadderStateAsync();

                        if (_ladderRoom is { } currentRoom && LabLadderEngine.IsReadyToAutoStart(currentRoom))
                        {
                            await StartMultiplayerLadderAsync();
                        }
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
                        && state.Prizes.SequenceEqual(_ladderRoom.Prizes, StringComparer.Ordinal))
                    {
                        _ladderRoom = state;
                        RefreshLadderLobbyUi();
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
                    var wager = _ladderActiveWager;
                    CloseLadderRoomState($"{senderName}(이)가 사다리방을 취소했습니다.");
                    if (wager > 0)
                    {
                        _progression.AddCoins(_profile, wager);
                        await PersistProfileAsync();
                    }

                    break;
                }
            }
        }
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

    private void RecalculateHostSlotCount()
    {
        if (_ladderRoom is null)
        {
            return;
        }

        var desired = Math.Clamp(_ladderRoom.Members.Count + _ladderRoom.PendingInviteCount, LabLadderEngine.MinParticipants, LabLadderEngine.MaxParticipants);
        var maxPicked = _ladderRoom.Members.Where(m => m.SelectedSlot.HasValue).Select(m => m.SelectedSlot!.Value + 1).DefaultIfEmpty(0).Max();
        _ladderRoom.SlotCount = Math.Max(desired, maxPicked);
    }

    private async Task BroadcastLadderStateAsync()
    {
        if (_ladderRoom is not { } room)
        {
            return;
        }

        var json = JsonSerializer.Serialize(room, ProtocolJson.PacketOptions);
        foreach (var friend in _ladderJoinedFriends.Values.ToArray())
        {
            _ = await _social.SendGameEventAsync(friend, new GameEventPayload
            {
                GameType = LabLadderEngine.GameTypeId,
                SessionId = room.SessionId,
                Action = "state",
                WagerCoins = room.WagerCoins,
                Data = json
            });
        }
    }

    private async Task StartMultiplayerLadderAsync()
    {
        if (_ladderStarting || _ladderRoom is null)
        {
            return;
        }

        var orderedMembers = _ladderRoom.Members
            .Where(m => m.SelectedSlot.HasValue)
            .OrderBy(m => m.SelectedSlot!.Value)
            .ToList();

        if (orderedMembers.Count < LabLadderEngine.MinParticipants)
        {
            return;
        }

        var shuffledPrizes = _ladderRoom.Prizes.Take(orderedMembers.Count).OrderBy(_ => Random.Shared.Next()).ToList();
        var names = orderedMembers.Select(m => m.DisplayName).ToList();
        var board = LabLadderEngine.GenerateBoard(names, shuffledPrizes);

        var startDto = new LadderStartDto
        {
            SessionId = _ladderRoom.SessionId,
            WagerCoins = _ladderRoom.WagerCoins,
            Levels = board.Levels,
            ParticipantPetIds = orderedMembers.Select(m => m.PetId).ToList(),
            Participants = board.Participants.ToList(),
            Prizes = board.Prizes.ToList(),
            Rungs = board.Rungs.ToList()
        };

        var json = JsonSerializer.Serialize(startDto, ProtocolJson.PacketOptions);
        var stateJson = JsonSerializer.Serialize(_ladderRoom, ProtocolJson.PacketOptions);
        _ladderStarting = true;
        foreach (var friend in _ladderJoinedFriends.Values.ToArray())
        {
            _ = await _social.SendGameEventAsync(friend, new GameEventPayload
            {
                GameType = LabLadderEngine.GameTypeId, SessionId = startDto.SessionId, Action = "state",
                WagerCoins = startDto.WagerCoins, Data = stateJson
            });
            _ = await _social.SendGameEventAsync(friend, new GameEventPayload
            {
                GameType = LabLadderEngine.GameTypeId,
                SessionId = startDto.SessionId,
                Action = "start",
                WagerCoins = startDto.WagerCoins,
                Data = json
            });
        }

        await ApplyStartedMultiplayerLadderAsync(startDto);
    }

    private async Task ApplyStartedMultiplayerLadderAsync(LadderStartDto startDto)
    {
        if (_ladderRoom is not { } room || !LabLadderEngine.IsValidStart(startDto, room, _profile.PetId))
        {
            _ladderStarting = false;
            return;
        }
        CloseLadderRoomState("사다리 결과를 계산했습니다.");
        var board = new LadderBoard(startDto.Participants, startDto.Prizes, startDto.Levels, startDto.Rungs);
        var results = LabLadderEngine.TraceAll(board);

        _lastLadderBoard = board;
        _lastLadderResults = results;

        LadderResultLines.Clear();
        foreach (var r in results)
        {
            LadderResultLines.Add($"{r.Participant}  →  {r.Prize}");
        }

        // 내 결과 확인 및 코인 잭팟 정산
        var myCol = startDto.ParticipantPetIds.FindIndex(id => string.Equals(id, _profile.PetId, StringComparison.OrdinalIgnoreCase));
        var myResult = myCol >= 0 && myCol < results.Count ? results[myCol] : null;
        string statusMsg;

        if (myResult is not null && startDto.WagerCoins > 0 && myResult.Prize.Contains("잭팟", StringComparison.OrdinalIgnoreCase))
        {
            var jackpot = startDto.WagerCoins * results.Count;
            _progression.AddCoins(_profile, jackpot);
            await PersistProfileAsync();
            statusMsg = $"🎉 동시 사다리 당첨! {myResult.Participant} → {myResult.Prize} (+{jackpot} 코인)";
            PetEmotionTriggered?.Invoke(this, true);
            GameNotification?.Invoke(this, $"🪜 사다리 잭팟 당첨! (+{jackpot} 코인)");
        }
        else if (myResult is not null)
        {
            statusMsg = $"🪜 동시 사다리 결과: {myResult.Participant} → {myResult.Prize}";
            GameNotification?.Invoke(this, $"🪜 사다리 결과: {myResult.Prize}");
        }
        else
        {
            statusMsg = $"🪜 동시 사다리 완료! ({results.Count}명 참여)";
        }

        LadderStatusText = statusMsg;
        LadderBoardGenerated?.Invoke(this, board);
    }

    private void RefreshLadderLobbyUi()
    {
        LadderSlots.Clear();
        if (_ladderRoom is null)
        {
            CanForceStartLadderRoom = false;
            LadderLobbySummaryText = string.Empty;
            return;
        }

        var pickedCount = _ladderRoom.Members.Count(m => m.SelectedSlot.HasValue);
        var joinedCount = _ladderRoom.Members.Count;
        var pendingText = _ladderRoom.PendingInviteCount > 0 ? $" · 응답 대기 {_ladderRoom.PendingInviteCount}명" : string.Empty;
        LadderLobbySummaryText = $"참가 {joinedCount}명 · 위치 선택 {pickedCount}/{joinedCount}{pendingText} (전원 선택 시 자동 시작)";
        CanForceStartLadderRoom = IsLadderHost && LabLadderEngine.AreJoinedMembersReady(_ladderRoom);

        for (var col = 0; col < _ladderRoom.SlotCount; col++)
        {
            var occupant = _ladderRoom.Members.FirstOrDefault(m => m.SelectedSlot == col);
            var isMine = occupant is not null && string.Equals(occupant.PetId, _profile.PetId, StringComparison.OrdinalIgnoreCase);
            LadderSlots.Add(new LadderSlotOption(
                col,
                $"{col + 1}번",
                occupant is null ? "빈 자리" : (isMine ? $"{occupant.DisplayName} (나)" : occupant.DisplayName),
                occupant is not null,
                isMine));
        }
    }

    private void CloseLadderRoomState(string statusMessage)
    {
        _ladderRoom = null;
        _ladderHostFriend = null;
        _ladderPendingInviteeIds.Clear();
        _ladderJoinedFriends.Clear();
        _ladderActiveWager = 0;
        _ladderStarting = false;
        IsLadderRoomOpen = false;
        IsLadderHost = false;
        CanForceStartLadderRoom = false;
        LadderLobbySummaryText = string.Empty;
        LadderSlots.Clear();
        LadderStatusText = statusMessage;
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
        var prizes = new List<string> { "☕ 커피 쏘기" };
        for (var i = 1; i < count; i++)
        {
            prizes.Add("통과");
        }

        LadderPrizesText = string.Join(", ", prizes);
    }

    [RelayCommand]
    private void ApplyCoinJackpotPreset()
    {
        var count = Math.Clamp(ParseTokens(LadderParticipantsText).Count, LabLadderEngine.MinParticipants, LabLadderEngine.MaxParticipants);
        var prizes = new List<string> { "💰 코인 잭팟!" };
        for (var i = 1; i < count; i++)
        {
            prizes.Add("꽝");
        }

        LadderPrizesText = string.Join(", ", prizes);
        if (LadderEntryFee == 0)
        {
            LadderEntryFee = 30;
        }
    }

    [RelayCommand]
    private async Task RunLadderAsync()
    {
        var participants = ParseTokens(LadderParticipantsText);
        if (participants.Count < LabLadderEngine.MinParticipants || participants.Count > LabLadderEngine.MaxParticipants)
        {
            LadderStatusText = $"참여자는 {LabLadderEngine.MinParticipants}~{LabLadderEngine.MaxParticipants}명으로 입력해 주세요. (쉼표 구분)";
            return;
        }

        var prizes = ParseTokens(LadderPrizesText);
        while (prizes.Count < participants.Count)
        {
            prizes.Add("통과");
        }

        // 선택적 코인 참가비 차감
        if (LadderEntryFee > 0)
        {
            var (ok, msg) = _progression.TrySpendCoins(_profile, LadderEntryFee);
            if (!ok)
            {
                LadderStatusText = msg;
                return;
            }
        }

        // 결과 순서를 매 실행마다 셔플해 공정성 확보
        var shuffledPrizes = prizes.Take(participants.Count).OrderBy(_ => Random.Shared.Next()).ToList();
        var board = LabLadderEngine.GenerateBoard(participants, shuffledPrizes);
        var results = LabLadderEngine.TraceAll(board);

        _lastLadderBoard = board;
        _lastLadderResults = results;

        LadderResultLines.Clear();
        foreach (var r in results)
        {
            LadderResultLines.Add($"{r.Participant}  →  {r.Prize}");
        }

        // 첫 번째 참여자(나)가 잭팟 당첨 시 참가비 × 인원수만큼 코인 지급
        if (LadderEntryFee > 0 && results.Count > 0 && results[0].Prize.Contains("잭팟", StringComparison.OrdinalIgnoreCase))
        {
            var jackpot = LadderEntryFee * participants.Count;
            _progression.AddCoins(_profile, jackpot);
            LadderStatusText = $"🎉 사다리 결과: {results[0].Participant} → {results[0].Prize} (+{jackpot} 코인 획득!)";
            PetEmotionTriggered?.Invoke(this, true);
        }
        else
        {
            LadderStatusText = $"🪜 사다리 완료! ({participants.Count}명 참여)";
        }

        await PersistProfileAsync();
        LadderBoardGenerated?.Invoke(this, board);
    }

    [RelayCommand]
    private async Task ShareLadderResultToFriendsAsync()
    {
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
