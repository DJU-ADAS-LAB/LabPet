using LabPet.Client.Games;
using Xunit;

namespace LabPet.Tests;

public sealed class CoinGamesTests
{
    [Fact]
    public void CoinHiLowGame_TracksStreakMultiplierAndCashOut()
    {
        // 고정 시드 대신 결정적 동작 검증을 위해 여러 카드 시퀀스 테스트
        var game = new CoinHiLowGame(new Random(42));
        var first = game.StartRound(50);
        Assert.InRange(first, CoinHiLowGame.MinCard, CoinHiLowGame.MaxCard);
        Assert.True(game.IsRoundActive);
        Assert.Equal(50, game.PotentialPayout);

        // 첫 카드가 중간 이하면 높다, 크면 낮다 선택
        var step = game.Guess(guessHigher: first <= 7);
        if (step.Outcome == HiLowGuessResult.Correct)
        {
            Assert.Equal(1, game.Streak);
            Assert.True(game.PotentialPayout > 50);
            var payout = game.CashOut();
            Assert.Equal(step.PotentialPayout, payout);
            Assert.False(game.IsRoundActive);
        }
        else if (step.Outcome == HiLowGuessResult.Wrong)
        {
            Assert.False(game.IsRoundActive);
            Assert.Equal(0, game.PotentialPayout);
        }
        else
        {
            Assert.True(game.IsRoundActive);
            Assert.Equal(0, game.Streak);
        }
    }

    [Theory]
    [InlineData(RpsMove.Rock, RpsMove.Scissors, RpsOutcome.Win)]
    [InlineData(RpsMove.Scissors, RpsMove.Paper, RpsOutcome.Win)]
    [InlineData(RpsMove.Paper, RpsMove.Rock, RpsOutcome.Win)]
    [InlineData(RpsMove.Rock, RpsMove.Rock, RpsOutcome.Draw)]
    [InlineData(RpsMove.Scissors, RpsMove.Rock, RpsOutcome.Lose)]
    public void RpsGameEngine_EvaluatesOutcomesAndRoundTripsTokens(RpsMove mine, RpsMove opponent, RpsOutcome expected)
    {
        Assert.Equal(expected, RpsGameEngine.Evaluate(mine, opponent));

        var token = RpsGameEngine.ToToken(mine);
        Assert.True(RpsGameEngine.TryParseToken(token, out var parsed));
        Assert.Equal(mine, parsed);
    }

    [Fact]
    public void TypingSprintEngine_ScoresCompletedAndPrefixMatches()
    {
        Assert.Equal(19, TypingSprintEngine.EvaluateCompletedPrompt("labpet coffee break", "LabPet Coffee Break"));
        Assert.Equal(0, TypingSprintEngine.EvaluateCompletedPrompt("labpet coffee break", "labpet coffee"));
        Assert.Equal(6, TypingSprintEngine.CountMatchedPrefixChars("labpet coffee break", "labpet"));
        Assert.Equal(3, TypingSprintEngine.CountMatchedPrefixChars("labpet", "labxxx"));
    }

    [Fact]
    public void LabLadderEngine_GeneratesValidBijectionWithoutAdjacentRungCollisions()
    {
        var participants = new[] { "승민", "대현", "민수", "지영" };
        var prizes = new[] { "커피 쏘기", "통과", "통과", "간식 당번" };

        var board = LabLadderEngine.GenerateBoard(participants, prizes, levels: 9, rng: new Random(7));

        // 같은 층에서 인접한 두 열에 가로줄이 동시에 놓이면 안 된다
        foreach (var group in board.Rungs.GroupBy(r => r.Level))
        {
            var cols = group.Select(r => r.LeftColumn).OrderBy(c => c).ToList();
            for (var i = 1; i < cols.Count; i++)
            {
                Assert.True(cols[i] - cols[i - 1] >= 2, "인접한 가로줄 충돌 발생");
            }
        }

        // 사다리는 반드시 1:1 대응(모든 결과 열이 중복 없이 정확히 한 번씩 매핑)이어야 한다
        var traces = LabLadderEngine.TraceAll(board);
        Assert.Equal(participants.Length, traces.Count);
        Assert.Equal(participants.Length, traces.Select(t => t.EndColumn).Distinct().Count());
    }
}
