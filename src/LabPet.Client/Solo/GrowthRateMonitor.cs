using LabPet.Shared.Models;

namespace LabPet.Client.Solo;

/// <summary>정상 경제 처리로 설명되지 않는 최근 1시간 증가를 감시한다.</summary>
internal static class GrowthRateMonitor
{
    internal const long UnexplainedCoinsLimit = 5_000;
    internal const int UnexplainedLevelsLimit = 5;

    // 호출자는 GrowthSync를 잠근다. 시계가 뒤로 가도 기존 감시 기록을 지우지 않는다.
    internal static bool Observe(PetProfile profile, DateTimeOffset utcNow)
    {
        if (profile.RequiresGrowthRecovery) return false;
        var p = profile.SoloProgress;
        if (p.GrowthAudit is null)
        {
            p.GrowthAudit = new GrowthRateAudit
            {
                ExpectedLevel = p.Level, ExpectedCoins = p.Coins, LastObservedUtc = utcNow
            };
            return false;
        }
        var audit = p.GrowthAudit;
        var now = utcNow < audit.LastObservedUtc ? audit.LastObservedUtc : utcNow;
        audit.Samples ??= [];
        audit.Samples.RemoveAll(s => s.MinuteUtc < now.AddHours(-1).AddMinutes(-1));
        var coinDelta = Math.Clamp((decimal)p.Coins - audit.ExpectedCoins, 0, UnexplainedCoinsLimit + 1);
        var levelDelta = Math.Clamp((long)p.Level - audit.ExpectedLevel, 0, UnexplainedLevelsLimit + 1);
        if (coinDelta > 0 || levelDelta > 0)
        {
            var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);
            var sample = audit.Samples.LastOrDefault(s => s.MinuteUtc == minute);
            if (sample is null)
            {
                sample = new GrowthRateSample { MinuteUtc = minute };
                audit.Samples.Add(sample);
            }
            sample.Coins = (long)Math.Min(UnexplainedCoinsLimit + 1, (decimal)sample.Coins + coinDelta);
            sample.Levels = (int)Math.Min(UnexplainedLevelsLimit + 1, (long)sample.Levels + levelDelta);
        }
        audit.LastObservedUtc = now;
        var coins = audit.Samples.Sum(s => (decimal)s.Coins);
        var levels = audit.Samples.Sum(s => (long)s.Levels);
        var detected = coins > UnexplainedCoinsLimit || levels > UnexplainedLevelsLimit;
        if (detected)
        {
            audit.DetectionCount = audit.DetectionCount == long.MaxValue ? long.MaxValue : audit.DetectionCount + 1;
            audit.LastDetectionUtc = now;
            audit.LastDetectionReason = $"unexplained-hourly: coins={coins}, levels={levels}";
            p.Level = SoloProgress.MinLevel;
            p.Coins = 0;
            p.Xp = 0;
            p.PendingGameWagers = [];
            p.PendingSoloLadderSession = null;
            p.PendingKeystrokesForReward = 0;
            p.PendingMouseClicksForReward = 0;
            p.PendingUptimeMinutesForReward = 0;
            audit.Samples.Clear();
        }
        audit.ExpectedCoins = p.Coins;
        audit.ExpectedLevel = p.Level;
        return detected;
    }

    // 정상 보상/소비 전에 변조를 먼저 검사하고, 그 작업의 결과만 승인한다.
    internal static IDisposable BeginEconomyChange(PetProfile profile, DateTimeOffset now)
    {
        Observe(profile, now);
        return new ApprovedChange(profile);
    }

    private sealed class ApprovedChange(PetProfile profile) : IDisposable
    {
        public void Dispose()
        {
            var p = profile.SoloProgress;
            if (p.GrowthAudit is not { } audit) return;
            audit.ExpectedLevel = p.Level;
            audit.ExpectedCoins = p.Coins;
        }
    }
}
