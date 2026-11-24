using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Games;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;

namespace LabPet.Client.ViewModels;

public sealed partial class CoinGamesViewModel
{
    private static readonly TimeSpan RpsRoundTimeout = TimeSpan.FromSeconds(45);
    private string? _rpsNonce;
    private string? _rpsOpponentCommitment;
    private bool _rpsCommitDelivered;
    private bool _rpsSendingReveal;
    private bool _rpsRevealDelivered;

    private void BeginRpsRound(string sessionId)
    {
        _rpsMyMove = null;
        _rpsOpponentMove = null;
        _rpsNonce = null;
        _rpsOpponentCommitment = null;
        _rpsCommitDelivered = false;
        _rpsSendingReveal = false;
        _rpsRevealDelivered = false;
        IsRpsWaitingResponse = false;
        IsRpsInMatch = true;
        CanChooseRpsMove = true;
        RpsPhase = RpsPhase.Choosing;
        RpsMySelectedMove = null;
        RpsRevealedOpponentMove = null;
        RpsOpponentCommitted = false;
        RpsLastOutcome = null;
        RpsSettlementText = string.Empty;
        RpsCurrentMatchWager = _rpsActiveWager;
        RpsOpponentDisplayName = _rpsOpponent is null ? "상대 친구" : _social.GetFriendDisplayName(_rpsOpponent.PetId);
        RpsOpponentPortrait = _rpsOpponent is null ? null : TryGetPortrait(ResolveFriendAppearance(_rpsOpponent.PetId, _rpsOpponent));
        RpsMyMoveText = "?";
        RpsOpponentMoveText = "?";
        _ = WaitForRpsDeadlineAsync(sessionId);
    }

    private async Task WaitForRpsDeadlineAsync(string sessionId)
    {
        await Task.Delay(RpsRoundTimeout);
        if (IsRpsInMatch && _rpsSessionId == sessionId)
        {
            await RefundRpsRoundAsync("가위바위보 확인 시간이 지나 배팅 코인을 반환했습니다.");
        }
    }

    private async Task RefundRpsRoundAsync(string message)
    {
        if (!IsRpsInMatch)
        {
            return;
        }

        var session = _rpsSessionId;
        IsRpsInMatch = false;
        CanChooseRpsMove = false;
        _rpsSessionId = null;
        RpsPhase = RpsPhase.Setup;
        RpsMySelectedMove = null;
        RpsRevealedOpponentMove = null;
        RpsOpponentCommitted = false;
        RpsLastOutcome = null;
        RpsSettlementText = string.Empty;
        RpsStatusText = message;
        await SettleMultiplayerWagerAsync(session, _rpsActiveWager);
    }

    [RelayCommand]
    private async Task ChooseRpsMoveAsync(string? moveToken)
    {
        if (!IsRpsInMatch || !CanChooseRpsMove || _rpsOpponent is not { } opponent
            || _rpsSessionId is not { } sessionId || !RpsGameEngine.TryParseToken(moveToken, out var move))
        {
            return;
        }

        _rpsMyMove = move;
        _rpsNonce = RpsGameEngine.CreateNonce();
        CanChooseRpsMove = false;
        RpsMySelectedMove = move;
        RpsPhase = RpsPhase.WaitingOpponent;
        RpsMyMoveText = RpsGameEngine.FormatKorean(move);
        RpsStatusText = RpsOpponentCommitted
            ? "선택 완료! 서로의 패를 검증하는 중..."
            : "선택 완료! 상대가 패를 고를 때까지 기다리는 중...";

        var commitment = RpsGameEngine.Commit(sessionId, _profile.PetId, move, _rpsNonce);
        var delivered = await SendRpsRoundEventAsync(opponent, sessionId, "commit", commitment);
        if (IsRpsInMatch && _rpsSessionId == sessionId)
        {
            _rpsCommitDelivered = delivered;
            await TrySendRpsRevealAsync();
        }
    }

    [RelayCommand]
    private void ResetRpsToSetup()
    {
        if (IsRpsInMatch || IsRpsWaitingResponse)
        {
            return;
        }

        RpsPhase = RpsPhase.Setup;
        RpsMySelectedMove = null;
        RpsRevealedOpponentMove = null;
        RpsOpponentCommitted = false;
        RpsLastOutcome = null;
        RpsSettlementText = string.Empty;
        RpsMyMoveText = "?";
        RpsOpponentMoveText = "?";
        RpsStatusText = "온라인 친구를 선택해 가위바위보 코인 배틀을 신청해 보세요!";
    }

    private async Task<bool> SendRpsRoundEventAsync(FriendEntry opponent, string sessionId, string action, string data)
    {
        var payload = new GameEventPayload
        {
            GameType = RpsGameEngine.GameTypeId,
            SessionId = sessionId,
            Action = action,
            WagerCoins = _rpsActiveWager,
            Data = data
        };
        return await _social.SendGameEventAsync(opponent, payload)
            || (IsRpsInMatch && _rpsSessionId == sessionId && await _social.SendGameEventAsync(opponent, payload));
    }

    private async Task TrySendRpsRevealAsync()
    {
        if (!IsRpsInMatch || !_rpsCommitDelivered || _rpsSendingReveal || _rpsRevealDelivered
            || _rpsOpponentCommitment is null || _rpsMyMove is not { } move || _rpsNonce is null
            || _rpsOpponent is not { } opponent || _rpsSessionId is not { } sessionId)
        {
            return;
        }

        _rpsSendingReveal = true;
        var delivered = await SendRpsRoundEventAsync(opponent, sessionId, "reveal", $"{RpsGameEngine.ToToken(move)}:{_rpsNonce}");
        if (IsRpsInMatch && _rpsSessionId == sessionId)
        {
            _rpsSendingReveal = false;
            _rpsRevealDelivered = delivered;
            await TryResolveRpsMatchAsync();
        }
    }

    private async Task ReceiveRpsRoundEventAsync(string senderPetId, GameEventPayload payload)
    {
        if (!IsRpsInMatch)
        {
            return;
        }

        // 상대의 커밋 해시만 수신 기록하고, 실제 패는 양측 선택·공개 완료 시까지 절대 노출하지 않는다.
        if (payload.Action == "commit" && _rpsOpponentCommitment is null && RpsGameEngine.IsCommitment(payload.Data))
        {
            _rpsOpponentCommitment = payload.Data;
            RpsOpponentCommitted = true;
            if (RpsPhase == RpsPhase.Choosing)
            {
                RpsStatusText = "상대가 선택을 마쳤습니다! 가위 · 바위 · 보 중 선택하세요.";
            }

            await TrySendRpsRevealAsync();
        }
        else if (payload.Action == "reveal" && _rpsMyMove is not null && _rpsOpponentMove is null
            && _rpsOpponentCommitment is { } commitment
            && RpsGameEngine.TryValidateReveal(payload.SessionId, senderPetId, commitment, payload.Data, out var move))
        {
            _rpsOpponentMove = move;
            await TryResolveRpsMatchAsync();
        }
        else if (payload.Action == "cancel")
        {
            await RefundRpsRoundAsync("상대가 가위바위보를 종료해 배팅 코인을 반환했습니다.");
        }
    }

    private async Task TryResolveRpsMatchAsync()
    {
        if (!IsRpsInMatch || !_rpsRevealDelivered || _rpsMyMove is null || _rpsOpponentMove is null)
        {
            return;
        }

        var mine = _rpsMyMove.Value;
        var opp = _rpsOpponentMove.Value;
        var outcome = RpsGameEngine.Evaluate(mine, opp);
        var session = _rpsSessionId;
        var oppName = _rpsOpponent is null ? "상대" : _social.GetFriendDisplayName(_rpsOpponent.PetId);

        RpsOpponentMoveText = RpsGameEngine.FormatKorean(opp);
        RpsRevealedOpponentMove = opp;
        RpsLastOutcome = outcome;
        RpsPhase = RpsPhase.Result;
        IsRpsInMatch = false;
        CanChooseRpsMove = false;
        _rpsSessionId = null;

        switch (outcome)
        {
            case RpsOutcome.Win:
                var winPrize = _rpsActiveWager * 2;
                await SettleMultiplayerWagerAsync(session, winPrize);
                RpsSettlementText = $"+{winPrize} 코인 획득 (참가 {_rpsActiveWager} 코인)";
                RpsStatusText = $"승리! {RpsGameEngine.FormatKorean(mine)} vs {RpsGameEngine.FormatKorean(opp)} (+{winPrize} 코인 획득)";
                PetEmotionTriggered?.Invoke(this, true);
                GameNotification?.Invoke(this, $"{oppName}에게 가위바위보 승리! (+{winPrize} 코인)");
                break;

            case RpsOutcome.Draw:
                await SettleMultiplayerWagerAsync(session, _rpsActiveWager);
                RpsSettlementText = $"배팅 {_rpsActiveWager} 코인 반환 (무승부)";
                RpsStatusText = $"무승부! {RpsGameEngine.FormatKorean(mine)} vs {RpsGameEngine.FormatKorean(opp)} (배팅 {_rpsActiveWager} 코인 반환)";
                GameNotification?.Invoke(this, $"{oppName}와 가위바위보 무승부!");
                break;

            case RpsOutcome.Lose:
                await SettleMultiplayerWagerAsync(session, 0);
                RpsSettlementText = $"-{_rpsActiveWager} 코인 차감 (상대 승리)";
                RpsStatusText = $"패배 · {RpsGameEngine.FormatKorean(mine)} vs {RpsGameEngine.FormatKorean(opp)} (-{_rpsActiveWager} 코인)";
                PetEmotionTriggered?.Invoke(this, false);
                GameNotification?.Invoke(this, $"{oppName}에게 가위바위보 패배...");
                break;
        }
    }
}
