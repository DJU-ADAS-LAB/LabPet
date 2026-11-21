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
    public void LabLadderEngine_PrizeParsingNormalizationAndSummary_SupportKindAndQuantity()
    {
        Assert.True(LabLadderEngine.TryParsePrizeInput("☕ 커피 x1, 과자 ×2, 통과 *2", out var prizes, out var error));
        Assert.Equal(string.Empty, error);
        Assert.Equal(["☕ 커피", "과자", "과자", "통과", "통과"], prizes);
        Assert.Equal("☕ 커피 ×1, 과자 ×2, 통과 ×2", LabLadderEngine.FormatPrizeSummary(prizes));

        // 2명 확정 시 더미("통과")가 먼저 줄어들고 실제 상품은 보존된다
        var shrunk = LabLadderEngine.NormalizePrizesForCount(prizes, 2);
        Assert.Equal(["☕ 커피", "과자"], shrunk);

        // 부족한 인원 칸은 "통과"로 채워진다
        var expanded = LabLadderEngine.NormalizePrizesForCount(["☕ 커피"], 3);
        Assert.Equal(["☕ 커피", "통과", "통과"], expanded);

        Assert.False(LabLadderEngine.TryParsePrizeInput("커피 x0", out _, out _));
        Assert.False(LabLadderEngine.TryParsePrizeInput("커피 x9", out _, out _));
        Assert.False(LabLadderEngine.TryParsePrizeInput("   ", out _, out _));
    }

    [Fact]
    public void LabLadderEngine_FisherYatesShuffle_PreservesMultisetAndPermutesCleanly()
    {
        var items = new[] { "A", "B", "C", "D" };
        // 항상 index 0을 고르는 순환 시프트 검증
        var rotated = LabLadderEngine.ShuffleFisherYates(items, _ => 0);
        Assert.Equal(["B", "C", "D", "A"], rotated);

        // 암호학적 난수 기반 보드 생성 시 다중집합 보존 및 서로 다른 배치 생성 가능
        var baseline = new[] { "당첨", "과자", "음료", "통과" };
        var seenOrders = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 24; i++)
        {
            var board = LabLadderEngine.GenerateRandomizedBoard(["P1", "P2", "P3", "P4"], baseline);
            Assert.Equal(baseline.Order(StringComparer.Ordinal), board.Prizes.Order(StringComparer.Ordinal));
            seenOrders.Add(string.Join("|", board.Prizes));
        }

        Assert.True(seenOrders.Count >= 2, "암호학적 무작위 셔플이 다양한 상품 배치를 만들어야 합니다.");
    }

    [Fact]
    public void LabLadderEngine_PhaseTransitions_BlockPickBeforeConfirmPreventDuplicatesAndRequireAllSlots()
    {
        var room = new LadderRoomStateDto
        {
            SessionId = "room-1",
            HostPetId = "host",
            Phase = LadderRoomPhase.WaitingInvites,
            WagerCoins = 0,
            SlotCount = 3,
            PendingInviteCount = 1,
            Prizes = ["커피", "통과", "통과"],
            Members =
            [
                new LadderMemberStateDto { PetId = "host", DisplayName = "승민" },
                new LadderMemberStateDto { PetId = "p2", DisplayName = "대현" }
            ]
        };

        // 참가자 확정 전(WaitingInvites)에는 위치를 선택할 수 없다
        Assert.False(LabLadderEngine.TryPickSlot(room, "host", 0));
        // 응답 대기 인원이 있으면 확정할 수 없다
        Assert.False(LabLadderEngine.CanConfirmParticipants(room));
        Assert.False(LabLadderEngine.TryConfirmParticipants(room, room.Prizes));

        // 3번째 초대자 수락으로 응답 완료
        room.PendingInviteCount = 0;
        room.Members.Add(new LadderMemberStateDto { PetId = "p3", DisplayName = "민수" });
        Assert.True(LabLadderEngine.CanConfirmParticipants(room));
        Assert.True(LabLadderEngine.TryConfirmParticipants(room, room.Prizes));
        Assert.Equal(LadderRoomPhase.PickingSlots, room.Phase);

        // 확정 후 위치 선택 가능 및 중복 선택 차단
        Assert.True(LabLadderEngine.TryPickSlot(room, "host", 0));
        Assert.False(LabLadderEngine.TryPickSlot(room, "p2", 0));
        Assert.True(LabLadderEngine.TryPickSlot(room, "p2", 1));

        // 3번째 참가자가 아직 고르지 않았으므로 시작 불가
        Assert.False(LabLadderEngine.CanStartGame(room));

        // 전원 중복 없이 선택 완료 -> 시작 가능
        Assert.True(LabLadderEngine.TryPickSlot(room, "p3", 2));
        Assert.True(LabLadderEngine.CanStartGame(room));
    }

    [Fact]
    public void LabLadderEngine_StartValidation_VerifiesConsistencyRungsAndImmutablePrizes()
    {
        var room = new LadderRoomStateDto
        {
            SessionId = "room-2",
            HostPetId = "host",
            Phase = LadderRoomPhase.PickingSlots,
            WagerCoins = 0,
            SlotCount = 3,
            PendingInviteCount = 0,
            Prizes = ["커피", "과자", "통과"],
            Members =
            [
                new LadderMemberStateDto { PetId = "host", DisplayName = "승민", SelectedSlot = 1 },
                new LadderMemberStateDto { PetId = "p2", DisplayName = "대현", SelectedSlot = 0 },
                new LadderMemberStateDto { PetId = "p3", DisplayName = "민수", SelectedSlot = 2 }
            ]
        };

        var ordered = room.Members.OrderBy(m => m.SelectedSlot!.Value).ToList();
        var board = LabLadderEngine.GenerateBoard(ordered.Select(m => m.DisplayName).ToList(), ["과자", "통과", "커피"], rng: new Random(11));
        var start = LabLadderEngine.CreateStartDto(room, board);

        Assert.True(LabLadderEngine.TryValidateStart(start, room, "p2", out var verifiedBoard, out var traces));
        Assert.NotNull(verifiedBoard);
        Assert.Equal(3, traces.Count);
        Assert.Equal(["대현", "승민", "민수"], verifiedBoard!.Participants);

        // 1) Outcomes 변조 시 검증 실패
        var tamperedOutcomes = LabLadderEngine.CreateStartDto(room, board);
        tamperedOutcomes.Outcomes[0].Prize = "잭팟";
        Assert.False(LabLadderEngine.TryValidateStart(tamperedOutcomes, room, "p2", out _, out _));

        // 2) 상품 목록 변조 시 검증 실패
        var tamperedPrizes = LabLadderEngine.CreateStartDto(room, board);
        tamperedPrizes.Prizes = ["아이패드", "통과", "통과"];
        Assert.False(LabLadderEngine.TryValidateStart(tamperedPrizes, room, "p2", out _, out _));

        // 3) 인접 가로줄 충돌 사다리판 차단
        var badRungStart = LabLadderEngine.CreateStartDto(room, board);
        badRungStart.Rungs = [new LadderRung(1, 0), new LadderRung(1, 1)];
        Assert.False(LabLadderEngine.TryValidateStart(badRungStart, room, "p2", out _, out _));

        // 4) 단계 역행 전이 차단
        var revertedRoom = new LadderRoomStateDto
        {
            SessionId = room.SessionId,
            HostPetId = room.HostPetId,
            Phase = LadderRoomPhase.WaitingInvites,
            WagerCoins = room.WagerCoins,
            SlotCount = 3,
            Prizes = room.Prizes.ToList(),
            Members = room.Members
        };
        Assert.False(LabLadderEngine.IsValidRoomTransition(room, room.Prizes, revertedRoom, "p2"));
    }
}
