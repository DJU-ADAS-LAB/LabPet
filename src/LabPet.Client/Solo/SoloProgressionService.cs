using LabPet.Client.Rendering;
using LabPet.Shared.Models;

namespace LabPet.Client.Solo;

/// <summary>
/// 활동량(키보드/마우스/PC 가동 시간), 뽀모도로 완료 보상, 레벨업 및 상점 구매를 담당하는 순수 도메인 서비스.
/// </summary>
public sealed class SoloProgressionService
{
    public const int KeystrokesPerReward = 40;
    public const int MouseClicksPerReward = 25;
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

        return GrantRewards(profile, xp: earnedBuckets * 5, coins: earnedBuckets * 2);
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

        return GrantRewards(profile, xp: earnedBuckets * 5, coins: earnedBuckets * 2);
    }

    public (bool LeveledUp, int NewLevel) RecordUptimeMinutes(PetProfile profile, int minutes)
    {
        if (minutes <= 0)
        {
            return (false, profile.SoloProgress.Level);
        }

        profile.SoloProgress.TotalUptimeMinutes += minutes;
        return GrantRewards(profile, xp: minutes * 4L, coins: minutes * 1L);
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

    public (bool Success, string Message) TryPurchaseItem(PetProfile profile, CustomizationOption option)
    {
        if (option.CoinPrice <= 0 || string.IsNullOrWhiteSpace(option.UnlockItemId))
        {
            return (false, "이미 기본 제공되는 아이템입니다.");
        }

        var progress = profile.SoloProgress;
        lock (progress)
        {
            if (progress.UnlockedItemIds.Contains(option.UnlockItemId, StringComparer.OrdinalIgnoreCase))
            {
                return (false, "이미 해금된 아이템입니다.");
            }

            if (progress.Coins < option.CoinPrice)
            {
                return (false, $"코인이 부족합니다. (필요: {option.CoinPrice}, 보유: {progress.Coins})");
            }

            progress.Coins -= option.CoinPrice;
            progress.UnlockedItemIds.Add(option.UnlockItemId);
            return (true, $"'{option.DisplayName}' 해금 완료!");
        }
    }
}
