using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Games;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;

namespace LabPet.Client.ViewModels;

public sealed partial class CoinGamesViewModel
{
    // ══════════════════════════════════════════════════════════════════════
    // 3. 친구 타이핑 스프린트 대결 (3초 카운트다운 -> 10초 대결 -> 결과 비교)
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SendTypingInviteAsync()
    {
        if (IsTypingRunning || IsTypingCountdown || (_typingSessionId is not null && !IsTypingWaitingResponse))
        {
            return;
        }

        if (IsTypingWaitingResponse)
        {
            if (_typingSessionId is { } session && _typingOpponent is { } opponent)
                await CancelTrackedInviteAsync(session, opponent);
            return;
        }

        if (IsAnyGameBusy || HasIncomingInvite)
        {
            TypingStatusText = DescribeInviteReason("busy");
            return;
        }

        if (SelectedTypingFriend is null)
        {
            TypingStatusText = _typingInviteTargetId is null ? "대결할 온라인 친구를 먼저 선택해 주세요." : DescribeInviteReason("offline");
            return;
        }

        if (_profile.SoloProgress.Coins < TypingWager)
        {
            TypingStatusText = $"코인이 부족합니다. (필요: {TypingWager}, 보유: {_profile.SoloProgress.Coins})";
            return;
        }

        var target = SelectedTypingFriend;
        if (!IsOnlineInviteTarget(target.Friend.PetId))
        {
            TypingStatusText = DescribeInviteReason("offline");
            return;
        }
        var sessionId = Guid.NewGuid().ToString("N");

        // 상대방이 초대를 받자마자 즉시 수락해도 유실되지 않도록 세션 상태를 먼저 등록한다.
        _typingSessionId = sessionId;
        _typingOpponent = target.Friend;
        _typingActiveWager = TypingWager;
        IsTypingWaitingResponse = true;
        TypingPhase = TypingPhase.WaitingInviteResponse;
        TypingOpponentDisplayName = target.DisplayName;
        TypingOpponentPortrait = TryGetPortrait(ResolveFriendAppearance(target.Friend.PetId, target.Friend));
        TypingStatusText = $"{target.DisplayName}에게 초대를 보내는 중입니다.";

        var deadline = NewInviteDeadline();
        StartOutgoingWaitCountdown(
            TypingWaitState,
            "타이핑 대결",
            _typingActiveWager,
            target.DisplayName,
            TypingOpponentPortrait,
            $"{target.DisplayName}에게 초대를 보내는 중입니다.",
            sessionId,
            deadline);

        await SendTrackedOfferAsync(target.Friend, TypingSprintEngine.GameTypeId, sessionId, _typingActiveWager, deadline);
    }

    private void CancelTypingCountdown()
    {
        _typingCountdownCts?.Cancel();
        _typingCountdownCts?.Dispose();
        _typingCountdownCts = null;
    }

    [RelayCommand]
    private void StartSoloTypingPractice()
    {
        if (IsAcceptingIncomingInvite || IsAnyGameBusy)
        {
            return;
        }

        BeginTypingSprint(isSoloPractice: true, opponent: null, sessionId: null, wager: 0);
    }

    [RelayCommand]
    private void ResetTypingToSetup()
    {
        if (IsTypingRunning || IsTypingCountdown || IsTypingWaitingResponse || _typingSessionId is not null)
        {
            return;
        }

        TypingPhase = TypingPhase.Setup;
        TypingMyScore = 0;
        TypingOpponentScore = 0;
        TypingCompletedCount = 0;
        TypingInputText = string.Empty;
        TypingPromptText = "대결이 시작되면 문장이 표시됩니다.";
        TypingTimeLeftText = $"{TypingSprintDurationSeconds:0.0}초";
        TypingTimeProgress = 1.0;
        TypingResultHeadline = string.Empty;
        TypingSettlementText = string.Empty;
        TypingStatusText = "온라인 친구와 10초 타이핑 대결을 펼쳐 보세요!";
    }

    private void BeginTypingSprint(bool isSoloPractice, FriendEntry? opponent, string? sessionId, int wager)
    {
        CancelTypingCountdown();
        _typingTimer.Stop();

        _isTypingSoloPractice = isSoloPractice;
        _typingOpponent = opponent;
        _typingSessionId = sessionId;
        _typingActiveWager = wager;
        _typingCompletedScore = 0;
        _typingCompletedPrompts.Clear();
        _typingMyFinished = false;
        _typingOpponentFinished = false;
        _typingOpponentFinalScore = 0;
        _typingRemainingSeconds = TypingSprintDurationSeconds;

        IsTypingSoloMode = isSoloPractice;
        TypingCurrentMatchWager = wager;
        TypingOpponentDisplayName = opponent is null ? "혼자 연습" : _social.GetFriendDisplayName(opponent.PetId);
        TypingOpponentPortrait = opponent is null ? null : TryGetPortrait(ResolveFriendAppearance(opponent.PetId, opponent));
        TypingCompletedCount = 0;
        TypingMyScore = 0;
        TypingOpponentScore = 0;
        TypingInputText = string.Empty;
        TypingTimeLeftText = $"{_typingRemainingSeconds:0.0}초";
        TypingTimeProgress = 1.0;
        TypingResultHeadline = string.Empty;
        TypingSettlementText = string.Empty;
        IsTypingWaitingResponse = false;
        IsTypingRunning = false;

        if (sessionId is not null)
        {
            _ = WaitForTypingDeadlineAsync(sessionId);
        }

        if (TypingCountdownDurationSeconds > 0)
        {
            StartTypingCountdown(sessionId, TypingCountdownDurationSeconds);
        }
        else
        {
            StartTypingSprintActivePhase(sessionId);
        }
    }

    private void StartTypingCountdown(string? sessionId, double totalSeconds)
    {
        var steps = totalSeconds >= 1.5 ? Math.Max(1, (int)Math.Ceiling(totalSeconds)) : 1;
        var stepDelay = TimeSpan.FromSeconds(totalSeconds / steps);

        TypingCountdownValue = steps;
        TypingPhase = TypingPhase.Countdown;
        TypingPromptText = "잠시 후 문장이 공개됩니다...";
        TypingStatusText = $"{steps}초 뒤 시작됩니다. 키보드 입력을 준비하세요.";

        var cts = new CancellationTokenSource();
        _typingCountdownCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                for (var remaining = steps; remaining >= 1; remaining--)
                {
                    if (cts.Token.IsCancellationRequested)
                    {
                        return;
                    }

                    var currentStep = remaining;
                    RunOnUi(() =>
                    {
                        if (TypingPhase == TypingPhase.Countdown && _typingSessionId == sessionId)
                        {
                            TypingCountdownValue = currentStep;
                            TypingStatusText = $"{currentStep}초 뒤 시작됩니다. 키보드 입력을 준비하세요.";
                        }
                    });

                    await Task.Delay(stepDelay, cts.Token);
                }

                if (!cts.Token.IsCancellationRequested)
                {
                    RunOnUi(() =>
                    {
                        if (TypingPhase == TypingPhase.Countdown && _typingSessionId == sessionId)
                        {
                            StartTypingSprintActivePhase(sessionId);
                        }
                    });
                }
            }
            catch (TaskCanceledException)
            {
            }
        }, cts.Token);
    }

    private void StartTypingSprintActivePhase(string? sessionId)
    {
        if (sessionId is not null && _typingSessionId != sessionId)
        {
            return;
        }

        TypingPromptText = sessionId is null
            ? TypingSprintEngine.PickPrompt()
            : TypingSprintEngine.MatchPrompt(sessionId, 0);
        TypingPhase = TypingPhase.Running;
        IsTypingRunning = true;
        TypingStatusText = "문장을 정확히 입력하면 자동으로 다음 문장으로 넘어갑니다.";
        _typingTimer.Start();
    }

    private async Task WaitForTypingDeadlineAsync(string sessionId)
    {
        var waitSeconds = Math.Max(0, TypingCountdownDurationSeconds) + TypingSprintDurationSeconds + 30;
        if (!await WaitForGameTimerAsync(TimeSpan.FromSeconds(waitSeconds)) || _disposed) return;
        if (_typingSessionId == sessionId)
        {
            await RefundTypingRoundAsync("상대 결과를 기다리는 시간이 지나 배팅 코인을 반환했습니다.");
        }
    }

    private async Task RefundTypingRoundAsync(string message)
    {
        var session = _typingSessionId;
        CancelTypingCountdown();
        _typingSessionId = null;
        _typingTimer.Stop();
        IsTypingRunning = false;
        TypingPhase = TypingPhase.Setup;
        TypingStatusText = message;
        await SettleMultiplayerWagerAsync(session, _typingActiveWager);
    }

    partial void OnTypingInputTextChanged(string value)
    {
        if (!IsTypingRunning)
        {
            return;
        }

        // 정확히 일치하면 Enter 없이도 즉시 다음 문장으로 넘어간다.
        var completed = TypingSprintEngine.EvaluateCompletedPrompt(TypingPromptText, value);
        if (completed > 0 && (_typingSessionId is null || _typingCompletedPrompts.Count < TypingSprintEngine.MaxCompletedPrompts))
        {
            _typingCompletedScore += completed;
            TypingCompletedCount++;
            TypingMyScore = _typingCompletedScore;
            if (_typingSessionId is { } sessionId)
            {
                _typingCompletedPrompts.Add(TypingPromptText);
                TypingPromptText = TypingSprintEngine.MatchPrompt(sessionId, _typingCompletedPrompts.Count);
            }
            else
            {
                TypingPromptText = TypingSprintEngine.PickPrompt(exclude: TypingPromptText);
            }

            TypingInputText = string.Empty;
            SendTypingProgressIfNeeded(force: true);
            return;
        }

        var partial = TypingSprintEngine.CountMatchedPrefixChars(TypingPromptText, value);
        TypingMyScore = _typingCompletedScore + partial;
        SendTypingProgressIfNeeded(force: false);
    }

    private void SendTypingProgressIfNeeded(bool force)
    {
        if (_isTypingSoloPractice || _typingOpponent is null || _typingSessionId is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force && (now - _lastTypingProgressSentUtc).TotalMilliseconds < 250)
        {
            return;
        }

        _lastTypingProgressSentUtc = now;
        _ = _social.SendGameEventAsync(_typingOpponent, new GameEventPayload
        {
            GameType = TypingSprintEngine.GameTypeId,
            SessionId = _typingSessionId,
            Action = "progress",
            WagerCoins = _typingActiveWager,
            Data = TypingMyScore.ToString()
        });
    }

    private async void OnTypingTimerTick(object? sender, EventArgs e)
    {
        if (!IsTypingRunning)
        {
            _typingTimer.Stop();
            return;
        }

        _typingRemainingSeconds = Math.Max(0, _typingRemainingSeconds - 0.1);
        TypingTimeLeftText = $"{_typingRemainingSeconds:0.0}초";
        TypingTimeProgress = TypingSprintDurationSeconds > 0
            ? Math.Clamp(_typingRemainingSeconds / TypingSprintDurationSeconds, 0.0, 1.0)
            : 0.0;

        if (_typingRemainingSeconds <= 0.001)
        {
            _typingTimer.Stop();
            IsTypingRunning = false;
            _typingMyFinished = true;

            if (_isTypingSoloPractice)
            {
                TypingPhase = TypingPhase.Result;
                TypingResultHeadline = "연습 완료!";
                TypingSettlementText = $"완료 문장 {TypingCompletedCount}개 · 총 {TypingMyScore}점";
                TypingStatusText = $"연습 종료! 최종 점수: {TypingMyScore}점";
                PetEmotionTriggered?.Invoke(this, true);
                return;
            }

            TypingPhase = TypingPhase.WaitingOpponent;
            TypingStatusText = $"종료! 내 점수 {TypingMyScore}점 · 상대 결과 집계 중...";
            if (_typingOpponent is not null && _typingSessionId is not null)
            {
                await _social.SendGameEventAsync(_typingOpponent, new GameEventPayload
                {
                    GameType = TypingSprintEngine.GameTypeId,
                    SessionId = _typingSessionId,
                    Action = "finish",
                    WagerCoins = _typingActiveWager,
                    Data = TypingSprintEngine.CreateTranscript(_typingCompletedPrompts,
                        TypingPromptText[..TypingSprintEngine.CountMatchedPrefixChars(TypingPromptText, TypingInputText)])
                });
            }

            await TryResolveTypingMatchAsync();
        }
    }

    private async Task TryResolveTypingMatchAsync()
    {
        if (_typingSessionId is null || !_typingMyFinished || !_typingOpponentFinished)
        {
            return;
        }

        // 저장을 기다리는 동안 나중에 수신된 완료 이벤트가 다시 정산하지 않도록 세션을 먼저 닫는다.
        var session = _typingSessionId;
        _typingSessionId = null;

        var oppName = _typingOpponent is null ? "상대" : _social.GetFriendDisplayName(_typingOpponent.PetId);
        TypingOpponentScore = _typingOpponentFinalScore;
        TypingPhase = TypingPhase.Result;

        if (TypingMyScore > _typingOpponentFinalScore)
        {
            var prize = _typingActiveWager * 2;
            await SettleMultiplayerWagerAsync(session, prize);
            TypingResultHeadline = "승리!";
            TypingSettlementText = $"+{prize} 코인 획득 (참가 {_typingActiveWager} 코인)";
            TypingStatusText = $"승리! 나 {TypingMyScore}점 vs {oppName} {_typingOpponentFinalScore}점 (+{prize} 코인)";
            PetEmotionTriggered?.Invoke(this, true);
            GameNotification?.Invoke(this, $"{oppName}에게 타이핑 대결 승리! (+{prize} 코인)");
        }
        else if (TypingMyScore == _typingOpponentFinalScore)
        {
            await SettleMultiplayerWagerAsync(session, _typingActiveWager);
            TypingResultHeadline = "무승부";
            TypingSettlementText = $"배팅 {_typingActiveWager} 코인 반환";
            TypingStatusText = $"무승부 · {TypingMyScore}점 동점 (배팅 {_typingActiveWager} 코인 반환)";
            GameNotification?.Invoke(this, $"{oppName}와 타이핑 대결 무승부!");
        }
        else
        {
            await SettleMultiplayerWagerAsync(session, 0);
            TypingResultHeadline = "아쉬운 패배";
            TypingSettlementText = $"-{_typingActiveWager} 코인 차감";
            TypingStatusText = $"패배 · 나 {TypingMyScore}점 vs {oppName} {_typingOpponentFinalScore}점 (-{_typingActiveWager} 코인)";
            PetEmotionTriggered?.Invoke(this, false);
            GameNotification?.Invoke(this, $"{oppName}에게 타이핑 대결 패배...");
        }
    }
}
