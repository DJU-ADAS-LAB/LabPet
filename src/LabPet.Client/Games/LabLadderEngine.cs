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
/// </summary>
public static class LabLadderEngine
{
    public const int MinParticipants = 2;
    public const int MaxParticipants = 8;
    public const int DefaultLevels = 9;

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
}
