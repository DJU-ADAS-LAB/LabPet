namespace LabPet.Client.Games;

public enum HiLowGuessResult
{
    Correct,
    Wrong,
    Push
}

public sealed record HiLowStepResult(
    HiLowGuessResult Outcome,
    int PreviousCard,
    int NextCard,
    int Streak,
    double Multiplier,
    long PotentialPayout);

/// <summary>
/// 솔로 코인 하이로우(Up &amp; Down) 게임 엔진.
/// 카드(1~13)의 높고 낮음을 연속으로 맞출수록 배수가 올라가며, 언제든 중간 정산할 수 있다.
/// </summary>
public sealed class CoinHiLowGame
{
    public const int MinCard = 1;
    public const int MaxCard = 13;

    private static readonly double[] Multipliers =
    [
        1.0, // 0연승 (시작 직후)
        1.4, // 1연승
        2.0, // 2연승
        2.8, // 3연승
        4.0, // 4연승
        5.6, // 5연승
        8.0  // 6연승 이상
    ];

    private readonly Random _rng;

    public bool IsRoundActive { get; private set; }
    public int WagerCoins { get; private set; }
    public int CurrentCard { get; private set; }
    public int Streak { get; private set; }

    public double CurrentMultiplier => Multipliers[Math.Min(Streak, Multipliers.Length - 1)];
    public long PotentialPayout => IsRoundActive ? (long)Math.Round(WagerCoins * CurrentMultiplier) : 0;

    public CoinHiLowGame(Random? rng = null)
    {
        _rng = rng ?? Random.Shared;
    }

    public int StartRound(int wagerCoins)
    {
        if (wagerCoins <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wagerCoins), "배팅 코인은 1 이상이어야 합니다.");
        }

        WagerCoins = wagerCoins;
        Streak = 0;
        CurrentCard = _rng.Next(MinCard, MaxCard + 1);
        IsRoundActive = true;
        return CurrentCard;
    }

    public HiLowStepResult Guess(bool guessHigher)
    {
        if (!IsRoundActive)
        {
            throw new InvalidOperationException("진행 중인 하이로우 라운드가 없습니다.");
        }

        var prev = CurrentCard;
        var next = _rng.Next(MinCard, MaxCard + 1);
        CurrentCard = next;

        if (next == prev)
        {
            return new HiLowStepResult(HiLowGuessResult.Push, prev, next, Streak, CurrentMultiplier, PotentialPayout);
        }

        var isCorrect = guessHigher ? next > prev : next < prev;
        if (isCorrect)
        {
            Streak++;
            return new HiLowStepResult(HiLowGuessResult.Correct, prev, next, Streak, CurrentMultiplier, PotentialPayout);
        }

        IsRoundActive = false;
        return new HiLowStepResult(HiLowGuessResult.Wrong, prev, next, Streak, 0.0, 0);
    }

    public long CashOut()
    {
        if (!IsRoundActive)
        {
            return 0;
        }

        var payout = PotentialPayout;
        IsRoundActive = false;
        return payout;
    }

    public static string FormatCardLabel(int card) => card switch
    {
        1 => "A (1)",
        11 => "J (11)",
        12 => "Q (12)",
        13 => "K (13)",
        _ => card.ToString()
    };
}
