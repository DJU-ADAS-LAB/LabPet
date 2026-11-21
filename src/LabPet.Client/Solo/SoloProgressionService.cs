using LabPet.Shared.Models;

namespace LabPet.Client.Solo;

/// <summary>
/// 활동량(키보드/마우스/PC 가동 시간), 뽀모도로 완료 보상, 레벨업 및 코인 게임 정산을 담당하는 순수 도메인 서비스.
/// </summary>
public sealed class SoloProgressionService
{
    public const int KeystrokesPerReward = 40;
    public const int KeystrokeRewardXp = 5;
    public const int KeystrokeRewardCoins = 2;
    public const int MouseClicksPerReward = 25;
    public const int MouseClickRewardXp = 5;
    public const int MouseClickRewardCoins = 2;
    public const int UptimeRewardXpPerMinute = 4;
    public const int UptimeRewardCoinsPerMinute = 1;
    public const int PomodoroRewardXp = 100;
    public const int PomodoroRewardCoins = 50;

    /// <summary>현재 레벨에서 다음 레벨로 올라가기 위해 필요한 XP.</summary>
    public static long GetRequiredXpForLevel(int level)
        => 100L + Math.Max(0, level - 1) * 50L;

    public (bool LeveledUp, int NewLevel) RecordKeystrokes(PetProfile profile, long deltaKeys)
    {
        if (deltaKeys <= 0)
        {
            return (false, profile.SoloProgress.Level);
        }

        var prevBuckets = profile.SoloProgress.TotalKeystrokes / KeystrokesPerReward;
        profile.SoloProgress.TotalKeystrokes += deltaKeys;
        var newBuckets = profile.SoloProgress.TotalKeystrokes / KeystrokesPerReward;
        var earnedBuckets = newBuckets - prevBuckets;

        if (earnedBuckets <= 0)
        {
            return (false, profile.SoloProgress.Level);
        }

        return GrantRewards(profile, xp: earnedBuckets * KeystrokeRewardXp, coins: earnedBuckets * KeystrokeRewardCoins);
    }

    public (bool LeveledUp, int NewLevel) RecordMouseClicks(PetProfile profile, long deltaClicks)
    {
        if (deltaClicks <= 0)
        {
            return (false, profile.SoloProgress.Level);
        }

        var prevBuckets = profile.SoloProgress.TotalMouseClicks / MouseClicksPerReward;
        profile.SoloProgress.TotalMouseClicks += deltaClicks;
        var newBuckets = profile.SoloProgress.TotalMouseClicks / MouseClicksPerReward;
        var earnedBuckets = newBuckets - prevBuckets;

        if (earnedBuckets <= 0)
        {
            return (false, profile.SoloProgress.Level);
        }

        return GrantRewards(profile, xp: earnedBuckets * MouseClickRewardXp, coins: earnedBuckets * MouseClickRewardCoins);
    }

    public (bool LeveledUp, int NewLevel) RecordUptimeMinutes(PetProfile profile, int minutes)
    {
        if (minutes <= 0)
        {
            return (false, profile.SoloProgress.Level);
        }

        profile.SoloProgress.TotalUptimeMinutes += minutes;
        return GrantRewards(profile, xp: minutes * (long)UptimeRewardXpPerMinute, coins: minutes * (long)UptimeRewardCoinsPerMinute);
    }

    public (bool LeveledUp, int NewLevel) RecordPomodoroCompleted(PetProfile profile)
    {
        profile.SoloProgress.CompletedPomodoros++;
        return GrantRewards(profile, PomodoroRewardXp, PomodoroRewardCoins);
    }

    public (bool LeveledUp, int NewLevel) GrantRewards(PetProfile profile, long xp, long coins)
    {
        var progress = profile.SoloProgress;
        lock (progress)
        {
            if (coins > 0)
            {
                progress.Coins += coins;
            }

            var leveledUp = false;
            if (xp > 0)
            {
                progress.Xp += xp;
                while (progress.Xp >= GetRequiredXpForLevel(progress.Level))
                {
                    progress.Xp -= GetRequiredXpForLevel(progress.Level);
                    progress.Level++;
                    leveledUp = true;
                }
            }

            return (leveledUp, progress.Level);
        }
    }

    /// <summary>코인 게임 배팅/참가비를 잔액에서 차감한다. 잔액이 부족하면 false를 반환한다.</summary>
    public (bool Success, string Message) TrySpendCoins(PetProfile profile, long amount)
    {
        if (amount < 0)
        {
            return (false, "잘못된 코인 금액입니다.");
        }

        if (amount == 0)
        {
            return (true, string.Empty);
        }

        var progress = profile.SoloProgress;
        lock (progress)
        {
            if (progress.Coins < amount)
            {
                return (false, $"코인이 부족합니다. (필요: {amount}, 보유: {progress.Coins})");
            }

            progress.Coins -= amount;
            return (true, string.Empty);
        }
    }

    /// <summary>코인 게임 승리·정산·반환 코인을 잔액에 가산한다.</summary>
    public void AddCoins(PetProfile profile, long amount)
    {
        if (amount <= 0)
        {
            return;
        }

        var progress = profile.SoloProgress;
        lock (progress)
        {
            progress.Coins += amount;
        }
    }
}
