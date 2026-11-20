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
        IsRpsInMatch = false;
        CanChooseRpsMove = false;
        _rpsSessionId = null;
        _progression.AddCoins(_profile, _rpsActiveWager);
        RpsStatusText = message;
        await PersistProfileAsync();
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
        RpsMyMoveText = RpsGameEngine.FormatKorean(move);
        RpsStatusText = "선택 완료! 상대의 선택을 기다리는 중...";
        var commitment = RpsGameEngine.Commit(sessionId, _profile.PetId, move, _rpsNonce);
        var delivered = await SendRpsRoundEventAsync(opponent, sessionId, "commit", commitment);
        if (IsRpsInMatch && _rpsSessionId == sessionId)
        {
            _rpsCommitDelivered = delivered;
            await TrySendRpsRevealAsync();
        }
    }

    private async Task<bool> SendRpsRoundEventAsync(FriendEntry opponent, string sessionId, string action, string data)
    {
        var payload = new GameEventPayload
        {
            GameType = RpsGameEngine.GameTypeId, SessionId = sessionId,
            Action = action, WagerCoins = _rpsActiveWager, Data = data
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
        if (payload.Action == "commit" && _rpsOpponentCommitment is null && RpsGameEngine.IsCommitment(payload.Data))
        {
            _rpsOpponentCommitment = payload.Data;
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
        var oppName = _rpsOpponent is null ? "상대" : _social.GetFriendDisplayName(_rpsOpponent.PetId);

        RpsOpponentMoveText = RpsGameEngine.FormatKorean(opp);
        IsRpsInMatch = false;
        CanChooseRpsMove = false;
        _rpsSessionId = null;

        switch (outcome)
        {
            case RpsOutcome.Win:
                var winPrize = _rpsActiveWager * 2;
                _progression.AddCoins(_profile, winPrize);
                await PersistProfileAsync();
                RpsStatusText = $"🎉 승리! {RpsGameEngine.FormatKorean(mine)} vs {RpsGameEngine.FormatKorean(opp)} (+{winPrize} 코인 획득)";
                PetEmotionTriggered?.Invoke(this, true);
                GameNotification?.Invoke(this, $"{oppName}에게 가위바위보 승리! (+{winPrize} 코인)");
                break;

            case RpsOutcome.Draw:
                _progression.AddCoins(_profile, _rpsActiveWager);
                await PersistProfileAsync();
                RpsStatusText = $"🤝 무승부! {RpsGameEngine.FormatKorean(mine)} vs {RpsGameEngine.FormatKorean(opp)} (배팅 {_rpsActiveWager} 코인 반환)";
                GameNotification?.Invoke(this, $"{oppName}와 가위바위보 무승부!");
                break;

            case RpsOutcome.Lose:
                RpsStatusText = $"😢 패배! {RpsGameEngine.FormatKorean(mine)} vs {RpsGameEngine.FormatKorean(opp)} (-{_rpsActiveWager} 코인)";
                PetEmotionTriggered?.Invoke(this, false);
                GameNotification?.Invoke(this, $"{oppName}에게 가위바위보 패배...");
                break;
        }
    }

}
