using System.Collections.Concurrent;
using System.Globalization;
using LabPet.Shared.Models;

namespace LabPet.Client.Solo;

/// <summary>소셜 보상 종류.</summary>
public enum SocialRewardKind
{
    FriendMessage,
    FriendGreeting,
    QuickReaction
}

/// <summary>레벨업 실패 사유.</summary>
public enum LevelUpFailureReason
{
    None,
    MaxLevelReached,
    InsufficientCoins
}

/// <summary>수동 레벨업 시도 결과.</summary>
public readonly record struct LevelUpResult(
    bool Succeeded,
    int OldLevel,
    int NewLevel,
    long CostCoins,
    long RemainingCoins,
    long ShortfallCoins,
    LevelUpFailureReason FailureReason);

/// <summary>활동 보상 적립 결과.</summary>
public readonly record struct ActivityRewardResult(
    long CoinsEarned,
    long CurrentCoins,
    int CurrentLevel);

/// <summary>소셜 보상 적립 결과.</summary>
public readonly record struct SocialCoinRewardResult(
    long CoinsEarned,
    int DailyCoinsEarned,
    int DailyCoinCap,
    bool IsDailyCapReached,
    bool IsCooldownBlocked);

/// <summary>경제 상태 변경 알림.</summary>
public readonly record struct EconomyChangedEventArgs(
    string Reason,
    long CurrentCoins,
    int CurrentLevel,
    int DailySocialCoinsEarned);

/// <summary>수동 레벨업 완료 알림.</summary>
public readonly record struct LevelUpCompletedEventArgs(
    int OldLevel,
    int NewLevel,
    long SpentCoins,
    long RemainingCoins);

/// <summary>
/// LabPet 코인 경제, 수동 레벨업(Lv.1~99), 게임 판돈 정산 및 어뷰징 방지 카운터를 총괄하는 단일 서비스.
/// </summary>
public sealed class PetEconomyService
{
    public const int MinLevel = SoloProgress.MinLevel;
    public const int MaxLevel = SoloProgress.MaxLevel;
    public const long LevelUpBaseCost = 100L;
    public const long LevelUpCostStep = 50L;

    public const int KeystrokesPerReward = 200;
    public const int KeystrokeRewardCoins = 1;
    public const int MouseClicksPerReward = 100;
    public const int MouseClickRewardCoins = 1;
    public const int UptimeMinutesPerReward = 10;
    public const int UptimeRewardCoins = 1;

    public const int PomodoroBaseMinutes = 5;
    public const int PomodoroCoinsPer5Minutes = 2;

    public const int FriendMessageRewardCoins = 1;
    public const int FriendGreetingRewardCoins = 1;
    public const int QuickReactionRewardCoins = 0;
    public const int DailySocialCoinCap = 10;
    public static readonly TimeSpan DefaultSocialRewardCooldown = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _socialRewardCooldown;
    private readonly ConcurrentDictionary<(string FriendPetId, SocialRewardKind Kind), DateTimeOffset> _lastSocialRewardByFriend = new();

    public event EventHandler<EconomyChangedEventArgs>? EconomyStateChanged;
    public event EventHandler<LevelUpCompletedEventArgs>? LevelUpCompleted;

    public PetEconomyService(TimeProvider? timeProvider = null, TimeSpan? socialRewardCooldown = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _socialRewardCooldown = socialRewardCooldown ?? DefaultSocialRewardCooldown;
    }

    public static int NormalizeLevel(int level)
        => Math.Clamp(level, MinLevel, MaxLevel);

    /// <summary>
    /// 현재 레벨에서 다음 레벨로 올리는 데 필요한 코인 비용을 반환한다. 만렙(Lv.99)이면 0을 반환한다.
    /// </summary>
    public static long GetLevelUpCost(int currentLevel)
    {
        var safeLevel = NormalizeLevel(currentLevel);
        if (safeLevel >= MaxLevel)
        {
            return 0L;
        }

        return LevelUpBaseCost + (safeLevel - 1L) * LevelUpCostStep;
    }

    /// <summary>
    /// 집중 시간(분) 기준 뽀모도로 완료 시 지급할 코인을 계산한다(5분 = 2코인, 15분 = 6코인, 25분 = 10코인, 50분 = 20코인).
    /// </summary>
    public static int GetPomodoroRewardCoins(int focusMinutes)
    {
        if (focusMinutes < PomodoroBaseMinutes)
        {
            return 0;
        }

        return (focusMinutes / PomodoroBaseMinutes) * PomodoroCoinsPer5Minutes;
    }

    public static void NormalizeProgress(PetProfile profile)
    {
        var p = profile.SoloProgress;
        p.Level = NormalizeLevel(p.Level);
        p.Xp = 0;
        p.Coins = Math.Max(0, p.Coins);
        p.PendingKeystrokesForReward = Math.Max(0, p.PendingKeystrokesForReward);
        p.PendingMouseClicksForReward = Math.Max(0, p.PendingMouseClicksForReward);
        p.PendingUptimeMinutesForReward = Math.Max(0, p.PendingUptimeMinutesForReward);
        p.DailySocialRewardCoinsEarned = Math.Clamp(p.DailySocialRewardCoinsEarned, 0, DailySocialCoinCap);
    }

    public int GetDailySocialCoinsEarned(PetProfile profile)
    {
        lock (profile.SoloProgress)
        {
            EnsureDailySocialBucket(profile.SoloProgress);
            return profile.SoloProgress.DailySocialRewardCoinsEarned;
        }
    }

    public bool CanLevelUp(PetProfile profile, out long costCoins, out long shortfallCoins)
    {
        lock (profile.SoloProgress)
        {
            NormalizeProgress(profile);
            var level = profile.SoloProgress.Level;
            if (level >= MaxLevel)
            {
                costCoins = 0;
                shortfallCoins = 0;
                return false;
            }

            costCoins = GetLevelUpCost(level);
            var current = profile.SoloProgress.Coins;
            if (current >= costCoins)
            {
                shortfallCoins = 0;
                return true;
            }

            shortfallCoins = costCoins - current;
            return false;
        }
    }

    public bool TryLevelUp(PetProfile profile, out LevelUpResult result)
    {
        long currentCoins;
        int currentLevel;
        int dailySocial;

        lock (profile.SoloProgress)
        {
            NormalizeProgress(profile);
            var p = profile.SoloProgress;
            EnsureDailySocialBucket(p);

            if (p.Level >= MaxLevel)
            {
                result = new LevelUpResult(false, p.Level, p.Level, 0, p.Coins, 0, LevelUpFailureReason.MaxLevelReached);
                return false;
            }

            var cost = GetLevelUpCost(p.Level);
            if (p.Coins < cost)
            {
                result = new LevelUpResult(false, p.Level, p.Level, cost, p.Coins, cost - p.Coins, LevelUpFailureReason.InsufficientCoins);
                return false;
            }

            var oldLevel = p.Level;
            p.Coins -= cost;
            p.Level = oldLevel + 1;
            p.Xp = 0;

            currentCoins = p.Coins;
            currentLevel = p.Level;
            dailySocial = p.DailySocialRewardCoinsEarned;

            result = new LevelUpResult(true, oldLevel, currentLevel, cost, currentCoins, 0, LevelUpFailureReason.None);
        }

        LevelUpCompleted?.Invoke(this, new LevelUpCompletedEventArgs(result.OldLevel, result.NewLevel, result.CostCoins, result.RemainingCoins));
        EconomyStateChanged?.Invoke(this, new EconomyChangedEventArgs("level-up", currentCoins, currentLevel, dailySocial));
        return true;
    }

    public ActivityRewardResult RecordKeystrokes(PetProfile profile, int deltaKeys)
    {
        long coinsEarned = 0;
        long currentCoins;
        int currentLevel;
        int dailySocial;

        lock (profile.SoloProgress)
        {
            NormalizeProgress(profile);
            var p = profile.SoloProgress;
            EnsureDailySocialBucket(p);

            if (deltaKeys > 0)
            {
                p.TotalKeystrokes += deltaKeys;
                var pending = (long)p.PendingKeystrokesForReward + deltaKeys;
                var buckets = pending / KeystrokesPerReward;
                p.PendingKeystrokesForReward = (int)(pending % KeystrokesPerReward);
                if (buckets > 0)
                {
                    coinsEarned = buckets * KeystrokeRewardCoins;
                    p.Coins += coinsEarned;
                }
            }

            currentCoins = p.Coins;
            currentLevel = p.Level;
            dailySocial = p.DailySocialRewardCoinsEarned;
        }

        if (coinsEarned > 0)
        {
            EconomyStateChanged?.Invoke(this, new EconomyChangedEventArgs("keystrokes", currentCoins, currentLevel, dailySocial));
        }

        return new ActivityRewardResult(coinsEarned, currentCoins, currentLevel);
    }

    public ActivityRewardResult RecordMouseClicks(PetProfile profile, int deltaClicks)
    {
        long coinsEarned = 0;
        long currentCoins;
        int currentLevel;
        int dailySocial;

        lock (profile.SoloProgress)
        {
            NormalizeProgress(profile);
            var p = profile.SoloProgress;
            EnsureDailySocialBucket(p);

            if (deltaClicks > 0)
            {
                p.TotalMouseClicks += deltaClicks;
                var pending = (long)p.PendingMouseClicksForReward + deltaClicks;
                var buckets = pending / MouseClicksPerReward;
                p.PendingMouseClicksForReward = (int)(pending % MouseClicksPerReward);
                if (buckets > 0)
                {
                    coinsEarned = buckets * MouseClickRewardCoins;
                    p.Coins += coinsEarned;
                }
            }

            currentCoins = p.Coins;
            currentLevel = p.Level;
            dailySocial = p.DailySocialRewardCoinsEarned;
        }

        if (coinsEarned > 0)
        {
            EconomyStateChanged?.Invoke(this, new EconomyChangedEventArgs("mouse-clicks", currentCoins, currentLevel, dailySocial));
        }

        return new ActivityRewardResult(coinsEarned, currentCoins, currentLevel);
    }

    public ActivityRewardResult RecordActiveUptimeMinutes(PetProfile profile, int minutes)
    {
        long coinsEarned = 0;
        long currentCoins;
        int currentLevel;
        int dailySocial;

        lock (profile.SoloProgress)
        {
            NormalizeProgress(profile);
            var p = profile.SoloProgress;
            EnsureDailySocialBucket(p);

            if (minutes > 0)
            {
                p.TotalUptimeMinutes += minutes;
                var pending = (long)p.PendingUptimeMinutesForReward + minutes;
                var buckets = pending / UptimeMinutesPerReward;
                p.PendingUptimeMinutesForReward = (int)(pending % UptimeMinutesPerReward);
                if (buckets > 0)
                {
                    coinsEarned = buckets * UptimeRewardCoins;
                    p.Coins += coinsEarned;
                }
            }

            currentCoins = p.Coins;
            currentLevel = p.Level;
            dailySocial = p.DailySocialRewardCoinsEarned;
        }

        if (coinsEarned > 0)
        {
            EconomyStateChanged?.Invoke(this, new EconomyChangedEventArgs("uptime", currentCoins, currentLevel, dailySocial));
        }

        return new ActivityRewardResult(coinsEarned, currentCoins, currentLevel);
    }

    public ActivityRewardResult RecordPomodoroCompleted(PetProfile profile, int focusMinutes)
    {
        var coinsEarned = (long)GetPomodoroRewardCoins(focusMinutes);
        long currentCoins;
        int currentLevel;
        int dailySocial;

        lock (profile.SoloProgress)
        {
            NormalizeProgress(profile);
            var p = profile.SoloProgress;
            EnsureDailySocialBucket(p);

            p.CompletedPomodoros++;
            if (coinsEarned > 0)
            {
                p.Coins += coinsEarned;
            }

            currentCoins = p.Coins;
            currentLevel = p.Level;
            dailySocial = p.DailySocialRewardCoinsEarned;
        }

        EconomyStateChanged?.Invoke(this, new EconomyChangedEventArgs("pomodoro", currentCoins, currentLevel, dailySocial));
        return new ActivityRewardResult(coinsEarned, currentCoins, currentLevel);
    }

    public SocialCoinRewardResult TryGrantSocialCoinReward(PetProfile profile, string friendPetId, SocialRewardKind kind)
    {
        var baseReward = kind switch
        {
            SocialRewardKind.FriendMessage => FriendMessageRewardCoins,
            SocialRewardKind.FriendGreeting => FriendGreetingRewardCoins,
            _ => QuickReactionRewardCoins
        };

        long coinsEarned = 0;
        long currentCoins;
        int currentLevel;
        int dailySocial;
        var capReached = false;
        var cooldownBlocked = false;
        var nowUtc = _timeProvider.GetUtcNow();

        lock (profile.SoloProgress)
        {
            NormalizeProgress(profile);
            var p = profile.SoloProgress;
            EnsureDailySocialBucket(p);

            if (baseReward <= 0)
            {
                return new SocialCoinRewardResult(0, p.DailySocialRewardCoinsEarned, DailySocialCoinCap, p.DailySocialRewardCoinsEarned >= DailySocialCoinCap, false);
            }

            var remainingCap = DailySocialCoinCap - p.DailySocialRewardCoinsEarned;
            if (remainingCap <= 0)
            {
                capReached = true;
            }
            else if (!string.IsNullOrWhiteSpace(friendPetId) && _socialRewardCooldown > TimeSpan.Zero)
            {
                var key = (friendPetId.ToUpperInvariant(), kind);
                if (_lastSocialRewardByFriend.TryGetValue(key, out var lastAt) && nowUtc - lastAt < _socialRewardCooldown)
                {
                    cooldownBlocked = true;
                }
                else
                {
                    var granted = Math.Min(baseReward, remainingCap);
                    p.DailySocialRewardCoinsEarned += granted;
                    p.Coins += granted;
                    coinsEarned = granted;
                    _lastSocialRewardByFriend[key] = nowUtc;
                }
            }
            else
            {
                var granted = Math.Min(baseReward, remainingCap);
                p.DailySocialRewardCoinsEarned += granted;
                p.Coins += granted;
                coinsEarned = granted;
            }

            currentCoins = p.Coins;
            currentLevel = p.Level;
            dailySocial = p.DailySocialRewardCoinsEarned;
            capReached |= dailySocial >= DailySocialCoinCap;
        }

        if (coinsEarned > 0)
        {
            EconomyStateChanged?.Invoke(this, new EconomyChangedEventArgs("social", currentCoins, currentLevel, dailySocial));
        }

        return new SocialCoinRewardResult(coinsEarned, dailySocial, DailySocialCoinCap, capReached, cooldownBlocked);
    }

    public bool TrySpendCoins(PetProfile profile, long amount, string reason = "spend")
    {
        if (amount <= 0)
        {
            return false;
        }

        long currentCoins;
        int currentLevel;
        int dailySocial;

        lock (profile.SoloProgress)
        {
            NormalizeProgress(profile);
            var p = profile.SoloProgress;
            EnsureDailySocialBucket(p);
            if (p.Coins < amount)
            {
                return false;
            }

            p.Coins -= amount;
            currentCoins = p.Coins;
            currentLevel = p.Level;
            dailySocial = p.DailySocialRewardCoinsEarned;
        }

        EconomyStateChanged?.Invoke(this, new EconomyChangedEventArgs(reason, currentCoins, currentLevel, dailySocial));
        return true;
    }

    public void AddCoins(PetProfile profile, long amount, string reason = "reward")
    {
        if (amount <= 0)
        {
            return;
        }

        long currentCoins;
        int currentLevel;
        int dailySocial;

        lock (profile.SoloProgress)
        {
            NormalizeProgress(profile);
            var p = profile.SoloProgress;
            EnsureDailySocialBucket(p);
            p.Coins += amount;
            currentCoins = p.Coins;
            currentLevel = p.Level;
            dailySocial = p.DailySocialRewardCoinsEarned;
        }

        EconomyStateChanged?.Invoke(this, new EconomyChangedEventArgs(reason, currentCoins, currentLevel, dailySocial));
    }

    public bool TryReserveWager(PetProfile profile, string sessionId, int wagerCoins)
    {
        if (wagerCoins < 0)
        {
            return false;
        }

        if (wagerCoins == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        long currentCoins;
        int currentLevel;
        int dailySocial;

        lock (profile.SoloProgress)
        {
            NormalizeProgress(profile);
            var p = profile.SoloProgress;
            EnsureDailySocialBucket(p);

            if (p.PendingGameWagers.ContainsKey(sessionId))
            {
                return false;
            }

            if (p.Coins < wagerCoins)
            {
                return false;
            }

            p.Coins -= wagerCoins;
            p.PendingGameWagers[sessionId] = wagerCoins;
            currentCoins = p.Coins;
            currentLevel = p.Level;
            dailySocial = p.DailySocialRewardCoinsEarned;
        }

        EconomyStateChanged?.Invoke(this, new EconomyChangedEventArgs("wager-reserve", currentCoins, currentLevel, dailySocial));
        return true;
    }

    public bool SettleWager(PetProfile profile, string sessionId, long payoutCoins)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        long currentCoins;
        int currentLevel;
        int dailySocial;

        lock (profile.SoloProgress)
        {
            NormalizeProgress(profile);
            var p = profile.SoloProgress;
            EnsureDailySocialBucket(p);

            if (!p.PendingGameWagers.Remove(sessionId))
            {
                return false;
            }

            if (payoutCoins > 0)
            {
                p.Coins += payoutCoins;
            }

            currentCoins = p.Coins;
            currentLevel = p.Level;
            dailySocial = p.DailySocialRewardCoinsEarned;
        }

        EconomyStateChanged?.Invoke(this, new EconomyChangedEventArgs("wager-settle", currentCoins, currentLevel, dailySocial));
        return true;
    }

    public bool RefundWager(PetProfile profile, string sessionId, out int refundedCoins)
    {
        refundedCoins = 0;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        long currentCoins;
        int currentLevel;
        int dailySocial;

        lock (profile.SoloProgress)
        {
            NormalizeProgress(profile);
            var p = profile.SoloProgress;
            EnsureDailySocialBucket(p);

            if (!p.PendingGameWagers.Remove(sessionId, out refundedCoins))
            {
                return false;
            }

            if (refundedCoins > 0)
            {
                p.Coins += refundedCoins;
            }

            currentCoins = p.Coins;
            currentLevel = p.Level;
            dailySocial = p.DailySocialRewardCoinsEarned;
        }

        EconomyStateChanged?.Invoke(this, new EconomyChangedEventArgs("wager-refund", currentCoins, currentLevel, dailySocial));
        return true;
    }

    public static bool CanAfford(PetProfile profile, long requiredCoins)
    {
        if (requiredCoins <= 0)
        {
            return true;
        }

        lock (profile.SoloProgress)
        {
            return profile.SoloProgress.Coins >= requiredCoins;
        }
    }

    public static long GetShortfall(PetProfile profile, long requiredCoins)
    {
        if (requiredCoins <= 0)
        {
            return 0;
        }

        lock (profile.SoloProgress)
        {
            return Math.Max(0L, requiredCoins - profile.SoloProgress.Coins);
        }
    }

    public void NotifyExternalStateChanged(PetProfile profile, string reason = "sync")
    {
        long currentCoins;
        int currentLevel;
        int dailySocial;

        lock (profile.SoloProgress)
        {
            NormalizeProgress(profile);
            EnsureDailySocialBucket(profile.SoloProgress);
            currentCoins = profile.SoloProgress.Coins;
            currentLevel = profile.SoloProgress.Level;
            dailySocial = profile.SoloProgress.DailySocialRewardCoinsEarned;
        }

        EconomyStateChanged?.Invoke(this, new EconomyChangedEventArgs(reason, currentCoins, currentLevel, dailySocial));
    }

    private void EnsureDailySocialBucket(SoloProgress progress)
    {
        var todayLocal = _timeProvider.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.Equals(progress.DailySocialRewardDateLocal, todayLocal, StringComparison.Ordinal))
        {
            progress.DailySocialRewardDateLocal = todayLocal;
            progress.DailySocialRewardCoinsEarned = 0;
        }
        else if (progress.DailySocialRewardCoinsEarned < 0 || progress.DailySocialRewardCoinsEarned > DailySocialCoinCap)
        {
            progress.DailySocialRewardCoinsEarned = Math.Clamp(progress.DailySocialRewardCoinsEarned, 0, DailySocialCoinCap);
        }
    }
}
