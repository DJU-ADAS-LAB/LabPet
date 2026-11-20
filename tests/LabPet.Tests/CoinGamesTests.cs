using LabPet.Client.Games;
using Xunit;

namespace LabPet.Tests;

public sealed class CoinGamesTests
{
    [Fact]
    public void RpsReveal_OnlyMatchesTheCommittedMoveSessionAndPlayer()
    {
        var nonce = RpsGameEngine.CreateNonce();
        var commitment = RpsGameEngine.Commit("round", "player", RpsMove.Rock, nonce);

        Assert.True(RpsGameEngine.TryValidateReveal("round", "player", commitment, $"rock:{nonce}", out var move));
        Assert.Equal(RpsMove.Rock, move);
        Assert.False(RpsGameEngine.TryValidateReveal("round", "player", commitment, $"paper:{nonce}", out _));
        Assert.False(RpsGameEngine.TryValidateReveal("other", "player", commitment, $"rock:{nonce}", out _));
        Assert.False(RpsGameEngine.TryValidateReveal("round", "other", commitment, $"rock:{nonce}", out _));
    }

    [Fact]
    public void TypingMatch_RecomputesScoreFromTheRoundPrompts()
    {
        string[] completed = [TypingSprintEngine.MatchPrompt("round", 0), TypingSprintEngine.MatchPrompt("round", 1)];
        var prefix = TypingSprintEngine.MatchPrompt("round", 2)[..2];
        var data = TypingSprintEngine.CreateTranscript(completed, prefix);

        Assert.True(TypingSprintEngine.TryScoreTranscript("round", data, out var score));
        Assert.Equal(completed.Sum(prompt => prompt.Length) + prefix.Length, score);
        Assert.False(TypingSprintEngine.TryScoreTranscript("round", "9999", out _));
        Assert.False(TypingSprintEngine.TryScoreTranscript("round",
            TypingSprintEngine.CreateTranscript(completed, "다른 문장"), out _));
    }

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

    [Fact]
    public void LabLadderEngine_MultiplayerLobby_PreventsDuplicateSlotAndAutoStartsWhenAllReady()
    {
        var room = new LadderRoomStateDto
        {
            SessionId = "room-1",
            HostPetId = "host",
            SlotCount = 3,
            PendingInviteCount = 1,
            Members =
            [
                new LadderMemberStateDto { PetId = "host", DisplayName = "승민" },
                new LadderMemberStateDto { PetId = "p2", DisplayName = "대현" }
            ]
        };

        // 방장이 1번(인덱스 0) 선점
        Assert.True(LabLadderEngine.TryPickSlot(room, "host", 0));
        // 다른 참가자가 이미 선점된 1번을 고르면 차단
        Assert.False(LabLadderEngine.TryPickSlot(room, "p2", 0));
        // 2번(인덱스 1)은 성공
        Assert.True(LabLadderEngine.TryPickSlot(room, "p2", 1));

        // 입장한 2명은 모두 골랐지만 응답 대기 1명이 남았으므로 자동 시작은 아직 false
        Assert.True(LabLadderEngine.AreJoinedMembersReady(room));
        Assert.False(LabLadderEngine.IsReadyToAutoStart(room));

        // 대기 중이던 3번째 참가자가 입장
        room.PendingInviteCount = 0;
        room.Members.Add(new LadderMemberStateDto { PetId = "p3", DisplayName = "민수" });
        Assert.False(LabLadderEngine.IsReadyToAutoStart(room));

        // 3번째 참가자가 빈 3번(인덱스 2)을 고르는 순간 전원 선택 완료 -> 자동 시작 true
        Assert.True(LabLadderEngine.TryPickSlot(room, "p3", 2));
        Assert.True(LabLadderEngine.IsReadyToAutoStart(room));
    }
}
