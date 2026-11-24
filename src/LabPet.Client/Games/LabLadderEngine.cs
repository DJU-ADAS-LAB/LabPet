using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LabPet.Client.Games;

public enum LadderRoomPhase
{
    WaitingInvites = 0,
    PickingSlots = 1,
    Running = 2
}

/// <summary>단체 사다리 대기실 참가자 9종 상태.</summary>
public enum LadderParticipantStatus
{
    Host = 0,
    Sending = 1,
    WaitingResponse = 2,
    Joined = 3,
    Declined = 4,
    InsufficientCoins = 5,
    TimedOut = 6,
    Disconnected = 7,
    Cancelled = 8
}

/// <summary>사다리 상품 빠른 프리셋.</summary>
public enum LadderPrizePreset
{
    CoffeeTreat = 0,
    LuckyDraw = 1,
    CoinJackpot = 2
}

public sealed record LadderRung(int Level, int LeftColumn);

public sealed record LadderPoint(double Column, double Level);

public sealed record LadderTraceResult(
    int StartColumn,
    int EndColumn,
    string Participant,
    string Prize,
    IReadOnlyList<LadderPoint> Path);

public sealed record LadderBoard(
    IReadOnlyList<string> Participants,
    IReadOnlyList<string> Prizes,
    int Levels,
    IReadOnlyList<LadderRung> Rungs);

/// <summary>
/// 연구실 공동 사다리타기 엔진.
/// 인접한 가로줄 충돌 없이 사다리 판을 생성하고, 각 참여자의 이동 경로와 최종 결과를 계산한다.
/// 또한 LAN 멀티플레이어 대기실에서 초대 응답 -> 참가자 확정 -> 중복 없는 위치 선택 -> 불변 사다리판 검증 단계를 담당한다.
/// </summary>
public static partial class LabLadderEngine
{
    public const string GameTypeId = "ladder";
    public const int MinParticipants = 2;
    public const int MaxParticipants = 8;
    public const int DefaultLevels = 9;

    public const int InviteResponseTimeoutSeconds = 20;
    public const int HostConfirmTimeoutSeconds = 60;
    public const int SlotSelectionTimeoutSeconds = 30;
    public const int ReconnectGraceTimeoutSeconds = 10;
    public const int MaxPrizeLabelLength = 24;

    public static string GetStatusDisplayText(LadderParticipantStatus status) => status switch
    {
        LadderParticipantStatus.Host => "방장",
        LadderParticipantStatus.Sending => "전송 중",
        LadderParticipantStatus.WaitingResponse => "수락 대기",
        LadderParticipantStatus.Joined => "참가 완료",
        LadderParticipantStatus.Declined => "거절",
        LadderParticipantStatus.InsufficientCoins => "코인 부족",
        LadderParticipantStatus.TimedOut => "시간 초과",
        LadderParticipantStatus.Disconnected => "연결 끊김",
        LadderParticipantStatus.Cancelled => "초대 취소",
        _ => "알 수 없음"
    };

    public static List<LadderPrizeItemDto> BuildPresetPrizeItems(LadderPrizePreset preset, int participantCount)
    {
        var count = Math.Clamp(participantCount, MinParticipants, MaxParticipants);
        var rest = Math.Max(1, count - 1);
        return preset switch
        {
            LadderPrizePreset.CoffeeTreat =>
            [
                new LadderPrizeItemDto { Label = "커피 쏘기", Quantity = 1 },
                new LadderPrizeItemDto { Label = "통과", Quantity = rest }
            ],
            LadderPrizePreset.LuckyDraw =>
            [
                new LadderPrizeItemDto { Label = "당첨", Quantity = 1 },
                new LadderPrizeItemDto { Label = "꽝", Quantity = rest }
            ],
            LadderPrizePreset.CoinJackpot =>
            [
                new LadderPrizeItemDto { Label = "코인 잭팟", Quantity = 1 },
                new LadderPrizeItemDto { Label = "꽝", Quantity = rest }
            ],
            _ =>
            [
                new LadderPrizeItemDto { Label = "당첨", Quantity = 1 },
                new LadderPrizeItemDto { Label = "통과", Quantity = rest }
            ]
        };
    }

    public static List<string> ExpandStructuredPrizes(IEnumerable<LadderPrizeItemDto> items)
    {
        var expanded = new List<string>();
        foreach (var item in items)
        {
            var label = (item.Label ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(label) || item.Quantity <= 0)
            {
                continue;
            }

            var qty = Math.Clamp(item.Quantity, 1, MaxParticipants);
            for (var i = 0; i < qty && expanded.Count < MaxParticipants; i++)
            {
                expanded.Add(label);
            }
        }

        return expanded;
    }

    public static string FormatStructuredPrizeInputText(IEnumerable<LadderPrizeItemDto> items)
    {
        var parts = new List<string>();
        foreach (var item in items)
        {
            var label = (item.Label ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(label) || item.Quantity <= 0)
            {
                continue;
            }

            parts.Add($"{label} x{Math.Clamp(item.Quantity, 1, MaxParticipants)}");
        }

        return string.Join(", ", parts);
    }

    public static List<LadderPrizeItemDto> ParsePrizeItemsFromText(string? raw)
    {
        var items = new List<LadderPrizeItemDto>();
        var tokens = (raw ?? string.Empty)
            .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            var label = token;
            var qty = 1;
            var match = PrizeQuantityRegex().Match(token);
            if (match.Success)
            {
                label = match.Groups["name"].Value.Trim();
                if (!int.TryParse(match.Groups["count"].Value, out qty))
                {
                    qty = 1;
                }
            }

            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            qty = Math.Clamp(qty, 1, MaxParticipants);
            var existing = items.FirstOrDefault(x => string.Equals(x.Label, label, StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.Quantity = Math.Clamp(existing.Quantity + qty, 1, MaxParticipants);
            }
            else
            {
                items.Add(new LadderPrizeItemDto
                {
                    Label = label.Length > MaxPrizeLabelLength ? label[..MaxPrizeLabelLength] : label,
                    Quantity = qty
                });
            }
        }

        return items;
    }

    public static bool TryValidateStructuredPrizes(
        IReadOnlyList<LadderPrizeItemDto> items,
        int requiredSlotCount,
        bool isPaidWager,
        out List<string> expandedPrizes,
        out string error)
    {
        expandedPrizes = [];
        error = string.Empty;

        if (items.Count == 0)
        {
            error = "상품을 1개 이상 추가해 주세요.";
            return false;
        }

        var totalQuantity = 0;
        foreach (var item in items)
        {
            var label = (item.Label ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(label))
            {
                error = "비어 있는 상품 이름을 입력하거나 삭제해 주세요.";
                expandedPrizes.Clear();
                return false;
            }

            if (label.Length > MaxPrizeLabelLength)
            {
                error = $"상품 이름은 최대 {MaxPrizeLabelLength}자까지 입력할 수 있습니다.";
                expandedPrizes.Clear();
                return false;
            }

            if (item.Quantity < 1 || item.Quantity > MaxParticipants)
            {
                error = $"상품 수량은 1~{MaxParticipants}개 사이여야 합니다.";
                expandedPrizes.Clear();
                return false;
            }

            totalQuantity += item.Quantity;
            if (totalQuantity > MaxParticipants)
            {
                error = $"총 상품 수량은 최대 {MaxParticipants}개까지 설정할 수 있습니다.";
                expandedPrizes.Clear();
                return false;
            }

            for (var i = 0; i < item.Quantity; i++)
            {
                expandedPrizes.Add(label);
            }
        }

        if (totalQuantity != requiredSlotCount)
        {
            error = $"총 상품 수량({totalQuantity}개)이 참가 인원({requiredSlotCount}명)과 일치해야 합니다.";
            expandedPrizes.Clear();
            return false;
        }

        if (isPaidWager && expandedPrizes.Count(IsJackpot) > 1)
        {
            error = "유료 사다리에서는 잭팟을 한 칸까지만 지정할 수 있습니다.";
            expandedPrizes.Clear();
            return false;
        }

        return true;
    }

    [GeneratedRegex(@"^(?<name>.+?)\s*[xX×*]\s*(?<count>\d+)$")]
    private static partial Regex PrizeQuantityRegex();

    public static bool TryParsePrizeInput(string? raw, out List<string> prizes, out string error)
    {
        prizes = [];
        error = string.Empty;

        var tokens = (raw ?? string.Empty)
            .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var itemName = token;
            var quantity = 1;
            var match = PrizeQuantityRegex().Match(token);
            if (match.Success)
            {
                itemName = match.Groups["name"].Value.Trim();
                if (!int.TryParse(match.Groups["count"].Value, out quantity) || quantity < 1 || quantity > MaxParticipants)
                {
                    error = $"상품 수량은 1~{MaxParticipants}개 사이로 입력해 주세요. ({token})";
                    prizes.Clear();
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(itemName))
            {
                error = $"유효하지 않은 상품 형식입니다. ({token})";
                prizes.Clear();
                return false;
            }

            if (prizes.Count + quantity > MaxParticipants)
            {
                error = $"총 상품 수량은 최대 {MaxParticipants}개까지 입력할 수 있습니다.";
                prizes.Clear();
                return false;
            }

            for (var i = 0; i < quantity; i++)
            {
                prizes.Add(itemName);
            }
        }

        if (prizes.Count == 0)
        {
            error = "상품을 1개 이상 입력해 주세요. (예: 커피 x1, 통과 x2)";
            return false;
        }

        return true;
    }

    public static List<string> NormalizePrizesForCount(IReadOnlyList<string> configuredPrizes, int targetCount)
    {
        var count = Math.Clamp(targetCount, MinParticipants, MaxParticipants);
        var normalized = configuredPrizes
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToList();

        while (normalized.Count < count)
        {
            normalized.Add("통과");
        }

        while (normalized.Count > count)
        {
            var fillerIdx = normalized.FindLastIndex(IsFillerPrize);
            normalized.RemoveAt(fillerIdx >= 0 ? fillerIdx : normalized.Count - 1);
        }

        return normalized;
    }

    public static string FormatPrizeSummary(IReadOnlyList<string> prizes)
    {
        if (prizes.Count == 0)
        {
            return string.Empty;
        }

        var ordered = new List<(string Name, int Count)>();
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in prizes)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var name = raw.Trim();
            if (indexByName.TryGetValue(name, out var idx))
            {
                ordered[idx] = (name, ordered[idx].Count + 1);
            }
            else
            {
                indexByName[name] = ordered.Count;
                ordered.Add((name, 1));
            }
        }

        return string.Join(", ", ordered.Select(x => $"{x.Name} ×{x.Count}"));
    }

    public static List<T> ShuffleFisherYates<T>(IReadOnlyList<T> items, Func<int, int>? nextInt = null)
    {
        var list = new List<T>(items);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = nextInt is null ? RandomNumberGenerator.GetInt32(i + 1) : nextInt(i + 1);
            if (j < 0 || j > i)
            {
                throw new ArgumentOutOfRangeException(nameof(nextInt), "셔플 인덱스가 허용 범위를 벗어났습니다.");
            }

            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    public static LadderBoard GenerateRandomizedBoard(
        IReadOnlyList<string> participants,
        IReadOnlyList<string> prizes,
        int levels = DefaultLevels)
    {
        var count = Math.Clamp(participants.Count, MinParticipants, MaxParticipants);
        var normalizedPrizes = NormalizePrizesForCount(prizes, count);
        var shuffledPrizes = ShuffleFisherYates(normalizedPrizes);
        return GenerateBoard(participants, shuffledPrizes, levels, rng: null);
    }

    public static LadderBoard GenerateBoard(
        IReadOnlyList<string> participants,
        IReadOnlyList<string> prizes,
        int levels = DefaultLevels,
        Random? rng = null)
    {
        var count = Math.Clamp(participants.Count, MinParticipants, MaxParticipants);
        if (prizes.Count < count)
        {
            throw new ArgumentException("결과 항목 수는 참여자 수 이상이어야 합니다.", nameof(prizes));
        }

        var cleanParticipants = participants.Take(count).Select((p, idx) => string.IsNullOrWhiteSpace(p) ? $"참여자 {idx + 1}" : p.Trim()).ToList();
        var cleanPrizes = prizes.Take(count).Select((p, idx) => string.IsNullOrWhiteSpace(p) ? $"항목 {idx + 1}" : p.Trim()).ToList();

        var rungs = new List<LadderRung>();
        var usableLevels = Math.Max(4, levels);

        bool ShouldPlaceRung() => rng is null ? RandomNumberGenerator.GetInt32(100) < 45 : rng.NextDouble() < 0.45;
        int NextLevel() => rng is null ? RandomNumberGenerator.GetInt32(1, usableLevels) : rng.Next(1, usableLevels);

        for (var level = 1; level < usableLevels; level++)
        {
            var prevHadRung = false;
            for (var col = 0; col < count - 1; col++)
            {
                if (prevHadRung)
                {
                    prevHadRung = false;
                    continue;
                }

                // 약 45% 확률로 가로줄 배치
                if (ShouldPlaceRung())
                {
                    rungs.Add(new LadderRung(level, col));
                    prevHadRung = true;
                }
            }
        }

        // 모든 인접 열 사이에 최소 1개 이상 가로줄이 있도록 보장
        for (var col = 0; col < count - 1; col++)
        {
            if (rungs.Any(x => x.LeftColumn == col))
            {
                continue;
            }

            for (var attempt = 0; attempt < 20; attempt++)
            {
                var level = NextLevel();
                var conflict = rungs.Any(x => x.Level == level && Math.Abs(x.LeftColumn - col) <= 1);
                if (!conflict)
                {
                    rungs.Add(new LadderRung(level, col));
                    break;
                }
            }
        }

        return new LadderBoard(cleanParticipants, cleanPrizes, usableLevels, rungs);
    }

    public static LadderTraceResult TracePath(LadderBoard board, int startColumn)
    {
        if (startColumn < 0 || startColumn >= board.Participants.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(startColumn));
        }

        var col = startColumn;
        var path = new List<LadderPoint> { new(col, 0) };

        for (var level = 1; level < board.Levels; level++)
        {
            var rightRung = board.Rungs.FirstOrDefault(r => r.Level == level && r.LeftColumn == col);
            var leftRung = board.Rungs.FirstOrDefault(r => r.Level == level && r.LeftColumn == col - 1);

            if (rightRung is not null)
            {
                path.Add(new LadderPoint(col, level));
                col++;
                path.Add(new LadderPoint(col, level));
            }
            else if (leftRung is not null)
            {
                path.Add(new LadderPoint(col, level));
                col--;
                path.Add(new LadderPoint(col, level));
            }
        }

        path.Add(new LadderPoint(col, board.Levels));

        return new LadderTraceResult(
            startColumn,
            col,
            board.Participants[startColumn],
            board.Prizes[col],
            path);
    }

    public static IReadOnlyList<LadderTraceResult> TraceAll(LadderBoard board)
    {
        var list = new List<LadderTraceResult>(board.Participants.Count);
        for (var i = 0; i < board.Participants.Count; i++)
        {
            list.Add(TracePath(board, i));
        }

        return list;
    }

    public static LadderStartDto CreateStartDto(LadderRoomStateDto room, LadderBoard board)
    {
        var orderedMembers = room.Members
            .Where(m => m.SelectedSlot.HasValue)
            .OrderBy(m => m.SelectedSlot!.Value)
            .ToList();

        var traces = TraceAll(board);
        var outcomes = new List<LadderOutcomeDto>(orderedMembers.Count);
        for (var i = 0; i < orderedMembers.Count; i++)
        {
            var trace = traces[i];
            outcomes.Add(new LadderOutcomeDto
            {
                StartColumn = trace.StartColumn,
                EndColumn = trace.EndColumn,
                ParticipantPetId = orderedMembers[i].PetId,
                Prize = trace.Prize
            });
        }

        return new LadderStartDto
        {
            SessionId = room.SessionId,
            WagerCoins = room.WagerCoins,
            Levels = board.Levels,
            ParticipantPetIds = orderedMembers.Select(m => m.PetId).ToList(),
            Participants = board.Participants.ToList(),
            Prizes = board.Prizes.ToList(),
            Rungs = board.Rungs.ToList(),
            Outcomes = outcomes
        };
    }

    public static bool CanConfirmParticipants(LadderRoomStateDto room)
        => room.Phase == LadderRoomPhase.WaitingInvites
            && room.PendingInviteCount == 0
            && room.Members.Count is >= MinParticipants and <= MaxParticipants
            && room.Prizes.Count == room.Members.Count
            && (!room.RequiresPrizeAgreement || AreAllGuestsAgreedOnPrizes(room));

    public static bool AreAllGuestsAgreedOnPrizes(LadderRoomStateDto room)
        => room.Members
            .Where(m => !string.Equals(m.PetId, room.HostPetId, StringComparison.OrdinalIgnoreCase))
            .All(m => m.AgreedPrizeRevision == room.PrizeRevision);

    public static bool TryConfirmParticipants(LadderRoomStateDto room, IReadOnlyList<string> configuredPrizes)
    {
        if (room.Phase != LadderRoomPhase.WaitingInvites
            || room.PendingInviteCount != 0
            || room.Members.Count is < MinParticipants or > MaxParticipants)
        {
            return false;
        }

        var normalized = NormalizePrizesForCount(configuredPrizes, room.Members.Count);
        room.Phase = LadderRoomPhase.PickingSlots;
        room.SlotCount = room.Members.Count;
        room.Prizes = normalized;
        foreach (var member in room.Members)
        {
            member.SelectedSlot = null;
        }

        return true;
    }

    /// <summary>
    /// LAN 사다리 대기실에서 참가자가 원하는 시작 열(slotIndex)을 선점하거나 빈 자리로 이동한다.
    /// 참가자 확정(PickingSlots) 전이거나 이미 다른 참가자가 차지한 자리는 선택할 수 없다.
    /// </summary>
    public static bool TryPickSlot(LadderRoomStateDto room, string petId, int slotIndex)
    {
        if (room.Phase != LadderRoomPhase.PickingSlots || slotIndex < 0 || slotIndex >= room.SlotCount)
        {
            return false;
        }

        var member = room.Members.FirstOrDefault(m => string.Equals(m.PetId, petId, StringComparison.OrdinalIgnoreCase));
        if (member is null)
        {
            return false;
        }

        var occupiedByOther = room.Members.Any(m =>
            !string.Equals(m.PetId, petId, StringComparison.OrdinalIgnoreCase) &&
            m.SelectedSlot == slotIndex);
        if (occupiedByOther)
        {
            return false;
        }

        member.SelectedSlot = slotIndex;
        return true;
    }

    /// <summary>
    /// 참가자가 확정되었고 모든 확정 참가자(최소 2명)가 서로 다른 시작 위치를 선택했을 때만 true를 반환한다.
    /// </summary>
    public static bool CanStartGame(LadderRoomStateDto room)
    {
        if (room.Phase != LadderRoomPhase.PickingSlots
            || room.PendingInviteCount != 0
            || room.Members.Count < MinParticipants
            || room.Members.Count > MaxParticipants
            || room.Members.Count != room.SlotCount
            || (room.RequiresPrizeAgreement && !AreAllGuestsAgreedOnPrizes(room)))
        {
            return false;
        }

        var slots = new HashSet<int>();
        foreach (var m in room.Members)
        {
            if (m.SelectedSlot is not int s || s < 0 || s >= room.SlotCount || !slots.Add(s))
            {
                return false;
            }
        }

        return slots.Count == room.SlotCount;
    }

    internal static bool IsValidRoom(LadderRoomStateDto room, string sessionId, string hostId, int wager)
    {
        if (room.SessionId != sessionId
            || !string.Equals(room.HostPetId, hostId, StringComparison.OrdinalIgnoreCase)
            || wager < 0
            || room.WagerCoins != wager
            || room.SlotCount is < MinParticipants or > MaxParticipants
            || room.Members is not { Count: >= 1 and <= MaxParticipants }
            || room.PendingInviteCount is < 0 or >= MaxParticipants
            || room.Prizes is null
            || room.Prizes.Count != room.SlotCount
            || room.Prizes.Any(string.IsNullOrWhiteSpace)
            || (wager > 0 && room.Prizes.Count(IsJackpot) > 1)
            || !room.Members.All(member => member is not null
                && !string.IsNullOrWhiteSpace(member.PetId)
                && (member.SelectedSlot is null || (member.SelectedSlot >= 0 && member.SelectedSlot < room.SlotCount))))
        {
            return false;
        }

        var uniqueIds = room.Members.Select(member => member.PetId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (uniqueIds.Count != room.Members.Count || !uniqueIds.Contains(hostId))
        {
            return false;
        }

        var slots = room.Members.Where(member => member.SelectedSlot.HasValue).Select(member => member.SelectedSlot!.Value).ToArray();
        if (slots.Distinct().Count() != slots.Length)
        {
            return false;
        }

        return room.Phase switch
        {
            LadderRoomPhase.WaitingInvites => slots.Length == 0,
            LadderRoomPhase.PickingSlots => room.PendingInviteCount == 0 && room.Members.Count == room.SlotCount,
            _ => false
        };
    }

    internal static bool IsValidRoomTransition(
        LadderRoomStateDto currentRoom,
        IReadOnlyList<string> initialPrizes,
        LadderRoomStateDto nextRoom,
        string localPetId)
    {
        if (!nextRoom.Members.Any(m => string.Equals(m.PetId, localPetId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return (currentRoom.Phase, nextRoom.Phase) switch
        {
            (LadderRoomPhase.WaitingInvites, LadderRoomPhase.WaitingInvites) =>
                nextRoom.PrizeRevision >= currentRoom.PrizeRevision
                && (nextRoom.PrizeRevision > currentRoom.PrizeRevision
                    || nextRoom.Prizes.SequenceEqual(currentRoom.Prizes, StringComparer.Ordinal)
                    || (nextRoom.PrizeRevision == 1
                        && nextRoom.Prizes.SequenceEqual(NormalizePrizesForCount(initialPrizes, nextRoom.SlotCount), StringComparer.Ordinal))),

            (LadderRoomPhase.WaitingInvites, LadderRoomPhase.PickingSlots) =>
                nextRoom.PrizeRevision >= currentRoom.PrizeRevision
                && (!nextRoom.RequiresPrizeAgreement || AreAllGuestsAgreedOnPrizes(nextRoom))
                && (nextRoom.PrizeRevision > 1
                    ? nextRoom.Prizes.SequenceEqual(currentRoom.Prizes, StringComparer.Ordinal)
                    : nextRoom.Prizes.SequenceEqual(NormalizePrizesForCount(initialPrizes, nextRoom.SlotCount), StringComparer.Ordinal)),

            (LadderRoomPhase.PickingSlots, LadderRoomPhase.PickingSlots) =>
                nextRoom.SlotCount == currentRoom.SlotCount
                && nextRoom.PrizeRevision == currentRoom.PrizeRevision
                && nextRoom.Prizes.SequenceEqual(currentRoom.Prizes, StringComparer.Ordinal)
                && currentRoom.Members.Select(m => m.PetId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(nextRoom.Members.Select(m => m.PetId)),

            _ => false
        };
    }

    internal static bool IsValidStart(LadderStartDto start, LadderRoomStateDto room, string localPetId)
        => TryValidateStart(start, room, localPetId, out _, out _);

    public static bool TryValidateStart(
        LadderStartDto start,
        LadderRoomStateDto room,
        string localPetId,
        out LadderBoard? board,
        out IReadOnlyList<LadderTraceResult> traces)
    {
        board = null;
        traces = [];

        if (!CanStartGame(room)
            || start.SessionId != room.SessionId
            || start.WagerCoins != room.WagerCoins
            || start.ParticipantPetIds is not { Count: >= MinParticipants and <= MaxParticipants } ids
            || ids.Count != room.SlotCount
            || start.Participants?.Count != ids.Count
            || start.Prizes?.Count != ids.Count
            || start.Participants.Any(string.IsNullOrWhiteSpace)
            || start.Prizes.Any(string.IsNullOrWhiteSpace)
            || start.Levels != DefaultLevels
            || start.Rungs is null
            || start.Rungs.Count > DefaultLevels * MaxParticipants
            || !ids.Contains(localPetId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var orderedMembers = room.Members
            .OrderBy(member => member.SelectedSlot!.Value)
            .ToList();
        if (!ids.SequenceEqual(orderedMembers.Select(m => m.PetId), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = 0; i < orderedMembers.Count; i++)
        {
            var expectedName = string.IsNullOrWhiteSpace(orderedMembers[i].DisplayName)
                ? $"참여자 {i + 1}"
                : orderedMembers[i].DisplayName.Trim();
            if (!string.Equals(start.Participants[i].Trim(), expectedName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!start.Prizes.Order(StringComparer.Ordinal)
            .SequenceEqual(room.Prizes.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return false;
        }

        var rungs = new HashSet<(int Level, int Column)>();
        var validRungs = start.Rungs.All(rung => rung is not null
            && rung.Level > 0 && rung.Level < start.Levels && rung.LeftColumn >= 0 && rung.LeftColumn < ids.Count - 1
            && !rungs.Contains((rung.Level, rung.LeftColumn - 1)) && !rungs.Contains((rung.Level, rung.LeftColumn + 1))
            && rungs.Add((rung.Level, rung.LeftColumn)));
        if (!validRungs)
        {
            return false;
        }

        var candidateBoard = new LadderBoard(
            start.Participants.Select(p => p.Trim()).ToList(),
            start.Prizes.Select(p => p.Trim()).ToList(),
            start.Levels,
            start.Rungs.ToList());

        var computedTraces = TraceAll(candidateBoard);
        if (computedTraces.Count != ids.Count
            || computedTraces.Select(t => t.EndColumn).Distinct().Count() != ids.Count)
        {
            return false;
        }

        if (start.Outcomes is not { Count: > 0 } outcomes || outcomes.Count != ids.Count)
        {
            return false;
        }

        for (var i = 0; i < computedTraces.Count; i++)
        {
            var expected = computedTraces[i];
            var actual = outcomes[i];
            if (actual is null
                || actual.StartColumn != expected.StartColumn
                || actual.EndColumn != expected.EndColumn
                || !string.Equals(actual.ParticipantPetId, ids[i], StringComparison.OrdinalIgnoreCase)
                || !string.Equals(actual.Prize, expected.Prize, StringComparison.Ordinal))
            {
                return false;
            }
        }

        board = candidateBoard;
        traces = computedTraces;
        return true;
    }

    internal static bool IsJackpot(string prize) => prize.Contains("잭팟", StringComparison.OrdinalIgnoreCase);

    internal static bool IsLosingPrize(string prize) => string.Equals(prize.Trim(), "꽝", StringComparison.Ordinal);

    private static bool IsFillerPrize(string prize)
        => string.Equals(prize.Trim(), "통과", StringComparison.Ordinal)
            || IsLosingPrize(prize);
}

public sealed class LadderMemberStateDto
{
    public string PetId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int? SelectedSlot { get; set; }
    public int AgreedPrizeRevision { get; set; }
}

public sealed class LadderPrizeItemDto
{
    public string Label { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
}

public sealed class LadderInviteeStateDto
{
    public string PetId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public LadderParticipantStatus Status { get; set; } = LadderParticipantStatus.Sending;
}

public sealed class LadderRoomStateDto
{
    public string SessionId { get; set; } = string.Empty;
    public string HostPetId { get; set; } = string.Empty;
    public LadderRoomPhase Phase { get; set; } = LadderRoomPhase.WaitingInvites;
    public int WagerCoins { get; set; }
    public int SlotCount { get; set; }
    public int PendingInviteCount { get; set; }
    public int PrizeRevision { get; set; } = 1;
    public bool RequiresPrizeAgreement { get; set; }
    public List<string> Prizes { get; set; } = [];
    public List<LadderPrizeItemDto> PrizeItems { get; set; } = [];
    public List<LadderInviteeStateDto> Invitees { get; set; } = [];
    public List<LadderMemberStateDto> Members { get; set; } = [];
    public DateTimeOffset? InviteDeadlineUtc { get; set; }
    public DateTimeOffset? HostConfirmDeadlineUtc { get; set; }
    public DateTimeOffset? SlotSelectionDeadlineUtc { get; set; }
}

public sealed class LadderOutcomeDto
{
    public int StartColumn { get; set; }
    public int EndColumn { get; set; }
    public string ParticipantPetId { get; set; } = string.Empty;
    public string Prize { get; set; } = string.Empty;
}

public sealed class LadderStartDto
{
    public string SessionId { get; set; } = string.Empty;
    public int WagerCoins { get; set; }
    public int Levels { get; set; } = LabLadderEngine.DefaultLevels;
    public List<string> ParticipantPetIds { get; set; } = [];
    public List<string> Participants { get; set; } = [];
    public List<string> Prizes { get; set; } = [];
    public List<LadderRung> Rungs { get; set; } = [];
    public List<LadderOutcomeDto> Outcomes { get; set; } = [];
}
