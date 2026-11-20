namespace LabPet.Client.Games;

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
/// 또한 LAN 멀티플레이어 대기실에서 참가자별 시작 위치 선점 및 전원 선택 완료 판정을 담당한다.
/// </summary>
public static class LabLadderEngine
{
    public const string GameTypeId = "ladder";
    public const int MinParticipants = 2;
    public const int MaxParticipants = 8;
    public const int DefaultLevels = 9;

    internal static bool IsValidRoom(LadderRoomStateDto room, string sessionId, string hostId, int wager)
    {
        if (room.SessionId != sessionId || !string.Equals(room.HostPetId, hostId, StringComparison.OrdinalIgnoreCase)
            || room.WagerCoins != wager || room.SlotCount is < MinParticipants or > MaxParticipants
            || room.Members is not { Count: >= 1 and <= MaxParticipants }
            || room.PendingInviteCount is < 0 or >= MaxParticipants
            || room.Prizes is not { Count: >= MinParticipants and <= MaxParticipants }
            || room.Prizes.Any(string.IsNullOrWhiteSpace)
            || room.Prizes.Count(IsJackpot) > 1
            || !room.Members.All(member => member is not null && !string.IsNullOrWhiteSpace(member.PetId)
                && (member.SelectedSlot is null || member.SelectedSlot >= 0 && member.SelectedSlot < room.SlotCount)))
        {
            return false;
        }
        var uniqueIds = room.Members.Select(member => member.PetId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var slots = room.Members.Where(member => member.SelectedSlot.HasValue).Select(member => member.SelectedSlot).ToArray();
        return uniqueIds.Count == room.Members.Count && uniqueIds.Contains(hostId)
            && slots.Distinct().Count() == slots.Length;
    }

    internal static bool IsValidStart(LadderStartDto start, LadderRoomStateDto room, string localPetId)
    {
        if (start.SessionId != room.SessionId || start.WagerCoins != room.WagerCoins
            || start.ParticipantPetIds is not { Count: >= MinParticipants and <= MaxParticipants } ids
            || start.Participants?.Count != ids.Count || start.Prizes?.Count != ids.Count
            || start.Participants.Any(string.IsNullOrWhiteSpace) || start.Prizes.Any(string.IsNullOrWhiteSpace)
            || start.Levels != DefaultLevels || start.Rungs is null || start.Rungs.Count > DefaultLevels * MaxParticipants)
        {
            return false;
        }
        var expected = room.Members.Where(member => member.SelectedSlot.HasValue)
            .OrderBy(member => member.SelectedSlot).Select(member => member.PetId);
        if (!ids.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase)
            || !ids.Contains(localPetId, StringComparer.OrdinalIgnoreCase)
            || !start.Prizes.Order(StringComparer.Ordinal)
                .SequenceEqual(room.Prizes.Take(ids.Count).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return false;
        }
        var rungs = new HashSet<(int Level, int Column)>();
        return start.Rungs.All(rung => rung is not null
            && rung.Level > 0 && rung.Level < start.Levels && rung.LeftColumn >= 0 && rung.LeftColumn < ids.Count - 1
            && !rungs.Contains((rung.Level, rung.LeftColumn - 1)) && !rungs.Contains((rung.Level, rung.LeftColumn + 1))
            && rungs.Add((rung.Level, rung.LeftColumn)));
    }

    internal static bool IsJackpot(string prize) => prize.Contains("잭팟", StringComparison.OrdinalIgnoreCase);

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

        var r = rng ?? Random.Shared;
        var rungs = new List<LadderRung>();
        var usableLevels = Math.Max(4, levels);

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
                if (r.NextDouble() < 0.45)
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
                var level = r.Next(1, usableLevels);
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

    /// <summary>
    /// LAN 사다리 대기실에서 참가자가 원하는 시작 열(slotIndex)을 선점하거나 이동한다.
    /// 이미 다른 참가자가 차지한 자리는 선택할 수 없다.
    /// </summary>
    public static bool TryPickSlot(LadderRoomStateDto room, string petId, int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= room.SlotCount)
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
    /// 방에 들어온 모든 참가자(최소 2명)가 서로 다른 시작 위치를 선택했고,
    /// 대기 중인 초대자가 없는지(자동 시작 가능 여부) 판정한다.
    /// </summary>
    public static bool IsReadyToAutoStart(LadderRoomStateDto room)
    {
        if (room.PendingInviteCount > 0 || room.Members.Count < MinParticipants)
        {
            return false;
        }

        return AreJoinedMembersReady(room);
    }

    /// <summary>
    /// 현재 입장한 모든 참가자(최소 2명)가 시작 위치를 선택했는지 판정한다.
    /// </summary>
    public static bool AreJoinedMembersReady(LadderRoomStateDto room)
    {
        if (room.Members.Count < MinParticipants)
        {
            return false;
        }

        return room.Members.All(m => m.SelectedSlot is int s && s >= 0 && s < room.SlotCount);
    }
}

public sealed class LadderMemberStateDto
{
    public string PetId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int? SelectedSlot { get; set; }
}

public sealed class LadderRoomStateDto
{
    public string SessionId { get; set; } = string.Empty;
    public string HostPetId { get; set; } = string.Empty;
    public int WagerCoins { get; set; }
    public int SlotCount { get; set; }
    public int PendingInviteCount { get; set; }
    public List<string> Prizes { get; set; } = [];
    public List<LadderMemberStateDto> Members { get; set; } = [];
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
}
