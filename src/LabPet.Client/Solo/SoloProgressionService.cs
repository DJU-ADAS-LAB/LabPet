using LabPet.Shared.Models;

namespace LabPet.Client.Solo;

/// <summary>
/// 활동량(키 입력·마우스 클릭·함께한 시간·뽀모도로)을 <see cref="PetEconomyService"/>에 연결하는 개인 성장 서비스.
/// </summary>
public sealed class SoloProgressionService
{
    public const int KeystrokesPerReward = PetEconomyService.KeystrokesPerReward;
    public const int KeystrokeRewardCoins = PetEconomyService.KeystrokeRewardCoins;

    public const int MouseClicksPerReward = PetEconomyService.MouseClicksPerReward;
    public const int MouseClickRewardCoins = PetEconomyService.MouseClickRewardCoins;

    public const int UptimeMinutesPerReward = PetEconomyService.UptimeMinutesPerReward;
    public const int UptimeRewardCoins = PetEconomyService.UptimeRewardCoins;

    public const int PomodoroBaseMinutes = PetEconomyService.PomodoroBaseMinutes;
    public const int PomodoroCoinsPer5Minutes = PetEconomyService.PomodoroCoinsPer5Minutes;

    public PetEconomyService Economy { get; }

    public SoloProgressionService(PetEconomyService? economy = null)
    {
        Economy = economy ?? new PetEconomyService();
    }

    public static int GetPomodoroRewardCoins(int focusMinutes)
        => PetEconomyService.GetPomodoroRewardCoins(focusMinutes);

    public static long GetLevelUpCost(int level)
        => PetEconomyService.GetLevelUpCost(level);

    public (bool LeveledUp, int NewLevel) RecordKeystrokes(PetProfile profile, long deltaKeys)
    {
        var delta = deltaKeys <= 0 ? 0 : (int)Math.Min(deltaKeys, int.MaxValue);
        var result = Economy.RecordKeystrokes(profile, delta);
        return (false, result.CurrentLevel);
    }

    public ActivityRewardResult RecordKeystrokesDetailed(PetProfile profile, long deltaKeys)
    {
        var delta = deltaKeys <= 0 ? 0 : (int)Math.Min(deltaKeys, int.MaxValue);
        return Economy.RecordKeystrokes(profile, delta);
    }

    public (bool LeveledUp, int NewLevel) RecordMouseClicks(PetProfile profile, long deltaClicks)
    {
        var delta = deltaClicks <= 0 ? 0 : (int)Math.Min(deltaClicks, int.MaxValue);
        var result = Economy.RecordMouseClicks(profile, delta);
        return (false, result.CurrentLevel);
    }

    public ActivityRewardResult RecordMouseClicksDetailed(PetProfile profile, long deltaClicks)
    {
        var delta = deltaClicks <= 0 ? 0 : (int)Math.Min(deltaClicks, int.MaxValue);
        return Economy.RecordMouseClicks(profile, delta);
    }

    public (bool LeveledUp, int NewLevel) RecordUptimeMinutes(PetProfile profile, int minutes)
    {
        var result = Economy.RecordActiveUptimeMinutes(profile, minutes);
        return (false, result.CurrentLevel);
    }

    public ActivityRewardResult RecordUptimeMinutesDetailed(PetProfile profile, int minutes)
        => Economy.RecordActiveUptimeMinutes(profile, minutes);

    public (bool LeveledUp, int NewLevel) RecordPomodoroCompleted(PetProfile profile, int focusMinutes = 25)
    {
        var result = Economy.RecordPomodoroCompleted(profile, focusMinutes);
        return (false, result.CurrentLevel);
    }

    public ActivityRewardResult RecordPomodoroCompletedDetailed(PetProfile profile, int focusMinutes = 25)
        => Economy.RecordPomodoroCompleted(profile, focusMinutes);

    public bool TryLevelUp(PetProfile profile, out LevelUpResult result)
        => Economy.TryLevelUp(profile, out result);

    public (bool Success, string Message) TrySpendCoins(PetProfile profile, long amount)
    {
        if (amount <= 0)
        {
            return (false, "유효하지 않은 코인 금액입니다.");
        }

        if (Economy.TrySpendCoins(profile, amount))
        {
            return (true, string.Empty);
        }

        long currentCoins;
        lock (profile.SoloProgress)
        {
            currentCoins = profile.SoloProgress.Coins;
        }

        return (false, $"코인이 부족합니다. (필요: {Formatting.CompactNumberFormatter.FormatExact(amount)}, 보유: {Formatting.CompactNumberFormatter.FormatExact(currentCoins)})");
    }

    public void AddCoins(PetProfile profile, long amount, string reason = "reward")
        => Economy.AddCoins(profile, amount, reason);
}
