using System.Security.Cryptography;
using LabPet.Shared.Models;

namespace LabPet.Client.Games;

/// <summary>
/// 솔로 전용 4칸 사다리 미니게임 엔진.
/// 고정 보상 구성(꽝 50% 0배 / 본전 25% 1배 / 당첨 25% 2.5배, 이론 반환율 87.5%)을 균등 무작위 순열로 배치하고,
/// 선택한 단일 시작 위치의 경로만을 공정하게 추적한다.
/// </summary>
public static class SoloLadderEngine
{
    public const int SlotCount = 4;
    public const int Levels = 9;
    public const double TheoreticalReturnRate = 0.875;

    public static IReadOnlyList<int> BetOptions { get; } = [10, 30, 50, 100];

    public sealed record SoloOutcomeSpec(string Key, string Label, int MultiplierPercent);

    private static readonly IReadOnlyList<SoloOutcomeSpec> FixedOutcomePool =
    [
        new("miss", "꽝", 0),
        new("miss", "꽝", 0),
        new("refund", "본전", 100),
        new("jackpot", "당첨", 250)
    ];

    public static IReadOnlyList<SoloOutcomeSpec> FixedOutcomes => FixedOutcomePool;

    public static SoloLadderPersistedSession CreateSession(
        int betCoins,
        int selectedSlot,
        Func<int, int>? nextInt = null)
    {
        if (!BetOptions.Contains(betCoins))
        {
            throw new ArgumentOutOfRangeException(nameof(betCoins), "허용되지 않는 참가 코인 금액입니다.");
        }

        if (selectedSlot is < 0 or >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedSlot), "시작 위치는 0~3 사이여야 합니다.");
        }

        var shuffled = LabLadderEngine.ShuffleFisherYates(FixedOutcomePool, nextInt);
        var bridges = GenerateBridges(nextInt);

        return new SoloLadderPersistedSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            BetCoins = betCoins,
            SelectedSlot = selectedSlot,
            SlotMultipliersPercent = shuffled.Select(o => o.MultiplierPercent).ToArray(),
            SlotOutcomeKeys = shuffled.Select(o => o.Key).ToArray(),
            Bridges = bridges,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public static List<SoloLadderBridgeRecord> GenerateBridges(Func<int, int>? nextInt = null)
    {
        bool ShouldPlace() => nextInt is null ? RandomNumberGenerator.GetInt32(100) < 48 : nextInt(100) < 48;
        int NextLevel() => nextInt is null ? RandomNumberGenerator.GetInt32(1, Levels) : 1 + nextInt(Levels - 1);

        var pickedLevels = new List<(int Level, int LeftCol)>();

        for (var level = 1; level < Levels; level++)
        {
            var prevHad = false;
            for (var col = 0; col < SlotCount - 1; col++)
            {
                if (prevHad)
                {
                    prevHad = false;
                    continue;
                }

                if (ShouldPlace())
                {
                    pickedLevels.Add((level, col));
                    prevHad = true;
                }
            }
        }

        // 모든 인접 열 쌍 사이에 최소 1개의 가로줄을 보장해 각 슬롯의 결과가 자연스럽게 섞이도록 한다.
        for (var col = 0; col < SlotCount - 1; col++)
        {
            if (pickedLevels.Any(x => x.LeftCol == col))
            {
                continue;
            }

            for (var attempt = 0; attempt < 24; attempt++)
            {
                var level = NextLevel();
                var conflict = pickedLevels.Any(x => x.Level == level && Math.Abs(x.LeftCol - col) <= 1);
                if (!conflict)
                {
                    pickedLevels.Add((level, col));
                    break;
                }
            }
        }

        return pickedLevels
            .OrderBy(x => x.Level)
            .ThenBy(x => x.LeftCol)
            .Select(x => new SoloLadderBridgeRecord
            {
                LeftColumn = x.LeftCol,
                NormalizedY = x.Level / (double)(Levels + 1)
            })
            .ToList();
    }

    public static (int EndSlot, IReadOnlyList<LadderPoint> Path) TraceSinglePath(
        IReadOnlyList<SoloLadderBridgeRecord> bridges,
        int startSlot)
    {
        if (startSlot is < 0 or >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(startSlot));
        }

        var col = startSlot;
        var path = new List<LadderPoint> { new(col, 0.0) };

        foreach (var bridge in bridges.OrderBy(b => b.NormalizedY))
        {
            if (bridge.LeftColumn == col)
            {
                path.Add(new LadderPoint(col, bridge.NormalizedY));
                col++;
                path.Add(new LadderPoint(col, bridge.NormalizedY));
            }
            else if (bridge.LeftColumn == col - 1)
            {
                path.Add(new LadderPoint(col, bridge.NormalizedY));
                col--;
                path.Add(new LadderPoint(col, bridge.NormalizedY));
            }
        }

        path.Add(new LadderPoint(col, 1.0));
        return (col, path);
    }

    public static long CalculatePayout(int betCoins, int multiplierPercent)
        => betCoins <= 0 || multiplierPercent <= 0
            ? 0L
            : (long)betCoins * multiplierPercent / 100L;

    public static string GetOutcomeDisplayLabel(string outcomeKey, int multiplierPercent)
        => outcomeKey switch
        {
            "jackpot" => $"당첨 ({multiplierPercent / 100.0:0.#}배)",
            "refund" => $"본전 ({multiplierPercent / 100.0:0.#}배)",
            _ => "꽝 (0배)"
        };

    public static string GetOutcomeShortTitle(string outcomeKey)
        => outcomeKey switch
        {
            "jackpot" => "당첨",
            "refund" => "본전",
            _ => "꽝"
        };

    public static bool IsValidSession(SoloLadderPersistedSession? session)
    {
        if (session is null
            || string.IsNullOrWhiteSpace(session.SessionId)
            || !BetOptions.Contains(session.BetCoins)
            || session.SelectedSlot is < 0 or >= SlotCount
            || session.SlotMultipliersPercent is not { Length: SlotCount } mults
            || session.SlotOutcomeKeys is not { Length: SlotCount } keys
            || session.Bridges is null)
        {
            return false;
        }

        var expectedMults = new[] { 0, 0, 100, 250 };
        if (!mults.Order().SequenceEqual(expectedMults))
        {
            return false;
        }

        for (var i = 0; i < SlotCount; i++)
        {
            var expectedKey = mults[i] switch
            {
                250 => "jackpot",
                100 => "refund",
                _ => "miss"
            };
            if (!string.Equals(keys[i], expectedKey, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return session.Bridges.All(b => b is not null
            && b.LeftColumn is >= 0 and < SlotCount - 1
            && b.NormalizedY > 0.0
            && b.NormalizedY < 1.0);
    }
}
