using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Games;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;

namespace LabPet.Client.ViewModels;

public sealed partial class CoinGamesViewModel
{
    // ══════════════════════════════════════════════════════════════════════
    // 3. 친구 타이핑 스프린트 대결 (LAN P2P + 10초 솔로 연습)
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task SendTypingInviteAsync()
    {
        if (IsTypingRunning || (_typingSessionId is not null && !IsTypingWaitingResponse))
        {
            return;
        }

        if (IsTypingWaitingResponse)
        {
            await CancelTypingInviteInternalAsync(isTimeout: false);
            return;
        }

        if (SelectedTypingFriend is null)
        {
            TypingStatusText = "대결할 온라인 친구를 먼저 선택해 주세요.";
            return;
        }

        if (_profile.SoloProgress.Coins < TypingWager)
        {
            TypingStatusText = $"코인이 부족합니다. (필요: {TypingWager}, 보유: {_profile.SoloProgress.Coins})";
            return;
        }

        var target = SelectedTypingFriend;
        var sessionId = Guid.NewGuid().ToString("N");

        // 상대방이 초대를 받자마자 즉시 수락해도 유실되지 않도록 세션 상태를 먼저 등록한다.
        _typingSessionId = sessionId;
        _typingOpponent = target.Friend;
        _typingActiveWager = TypingWager;
        IsTypingWaitingResponse = true;
        TypingStatusText = $"{target.DisplayName}의 수락을 기다리는 중... ({TypingWager} 코인 · {InviteWaitTimeoutSeconds}초 제한)";

        var ok = await _social.SendGameEventAsync(target.Friend, new GameEventPayload
        {
            GameType = TypingSprintEngine.GameTypeId,
            SessionId = sessionId,
            Action = "invite",
            WagerCoins = TypingWager
        });

        if (!ok)
        {
            if (_typingSessionId == sessionId)
            {
                _typingSessionId = null;
                _typingOpponent = null;
                IsTypingWaitingResponse = false;
                TypingStatusText = $"{target.DisplayName}에게 도전을 보내지 못했습니다. (연결/방화벽 확인)";
            }

            return;
        }

        StartTypingInviteTimeout(sessionId);
    }

    private void StartTypingInviteTimeout(string sessionId)
    {
        CancelTypingInviteTimeout();
        var cts = new CancellationTokenSource();
        _typingInviteTimeoutCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(InviteWaitTimeoutSeconds), cts.Token);
                if (!cts.Token.IsCancellationRequested)
                {
                    RunOnUi(() =>
                    {
                        if (IsTypingWaitingResponse && _typingSessionId == sessionId)
                        {
                            _ = CancelTypingInviteInternalAsync(isTimeout: true);
                        }
                    });
                }
            }
            catch (TaskCanceledException)
            {
            }
        }, cts.Token);
    }

    private void CancelTypingInviteTimeout()
    {
        _typingInviteTimeoutCts?.Cancel();
        _typingInviteTimeoutCts?.Dispose();
        _typingInviteTimeoutCts = null;
    }

    private async Task CancelTypingInviteInternalAsync(bool isTimeout)
    {
        CancelTypingInviteTimeout();
        var sessionId = _typingSessionId;
        var opponent = _typingOpponent;
        var wager = _typingActiveWager;

        _typingSessionId = null;
        _typingOpponent = null;
        IsTypingWaitingResponse = false;
        TypingStatusText = isTimeout
            ? $"수락 대기 시간({InviteWaitTimeoutSeconds}초)이 초과되어 신청이 자동 취소되었습니다."
            : "타이핑 대결 신청을 취소했습니다.";

        if (opponent is not null && sessionId is not null)
        {
            _ = await _social.SendGameEventAsync(opponent, new GameEventPayload
            {
                GameType = TypingSprintEngine.GameTypeId,
                SessionId = sessionId,
                Action = "cancel",
                WagerCoins = wager
            });
        }
    }

    [RelayCommand]
    private void StartSoloTypingPractice()
    {
        if (IsTypingRunning || IsTypingWaitingResponse || _typingSessionId is not null)
        {
            return;
        }

        BeginTypingSprint(isSoloPractice: true, opponent: null, sessionId: null, wager: 0);
    }

    private void BeginTypingSprint(bool isSoloPractice, FriendEntry? opponent, string? sessionId, int wager)
    {
        _isTypingSoloPractice = isSoloPractice;
        _typingOpponent = opponent;
        _typingSessionId = sessionId;
        _typingActiveWager = wager;
        _typingCompletedScore = 0;
        _typingCompletedPrompts.Clear();
        _typingMyFinished = false;
        _typingOpponentFinished = false;
        _typingOpponentFinalScore = 0;
        _typingRemainingSeconds = TypingSprintEngine.SprintDurationSeconds;

        TypingMyScore = 0;
        TypingOpponentScore = 0;
        TypingInputText = string.Empty;
        TypingPromptText = sessionId is null ? TypingSprintEngine.PickPrompt() : TypingSprintEngine.MatchPrompt(sessionId, 0);
        TypingTimeLeftText = $"{_typingRemainingSeconds:0.0}초";
        IsTypingWaitingResponse = false;
        IsTypingRunning = true;
        TypingStatusText = isSoloPractice
            ? "10초 연습 시작! 아래 문장을 빠르게 입력하고 Enter를 누르세요."
            : $"🔥 10초 타이핑 대결 시작! ({wager} 코인) 빠르게 입력하고 Enter!";

        _typingTimer.Start();
        if (sessionId is not null)
        {
            _ = WaitForTypingDeadlineAsync(sessionId);
        }
    }

    private async Task WaitForTypingDeadlineAsync(string sessionId)
    {
        await Task.Delay(TimeSpan.FromSeconds(TypingSprintEngine.SprintDurationSeconds + 30));
        if (_typingSessionId == sessionId)
        {
            await RefundTypingRoundAsync("상대 결과를 기다리는 시간이 지나 배팅 코인을 반환했습니다.");
        }
    }

    private async Task RefundTypingRoundAsync(string message)
    {
        _typingSessionId = null;
        _typingTimer.Stop();
        IsTypingRunning = false;
        _progression.AddCoins(_profile, _typingActiveWager);
        TypingStatusText = message;
        await PersistProfileAsync();
    }

    partial void OnTypingInputTextChanged(string value)
    {
        if (!IsTypingRunning)
        {
            return;
        }

        // 정확히 일치하면 Enter 없이도 즉시 다음 문장으로 넘어가 쾌적한 속도감 제공
        var completed = TypingSprintEngine.EvaluateCompletedPrompt(TypingPromptText, value);
        if (completed > 0 && (_typingSessionId is null || _typingCompletedPrompts.Count < TypingSprintEngine.MaxCompletedPrompts))
        {
            _typingCompletedScore += completed;
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

        if (_typingRemainingSeconds <= 0.001)
        {
            _typingTimer.Stop();
            IsTypingRunning = false;
            _typingMyFinished = true;

            if (_isTypingSoloPractice)
            {
                TypingStatusText = $"연습 종료! 최종 점수: {TypingMyScore}점";
                PetEmotionTriggered?.Invoke(this, true);
                return;
            }

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
        _typingSessionId = null;

        var oppName = _typingOpponent is null ? "상대" : _social.GetFriendDisplayName(_typingOpponent.PetId);
        TypingOpponentScore = _typingOpponentFinalScore;

        if (TypingMyScore > _typingOpponentFinalScore)
        {
            var prize = _typingActiveWager * 2;
            _progression.AddCoins(_profile, prize);
            await PersistProfileAsync();
            TypingStatusText = $"🎉 승리! 나 {TypingMyScore}점 vs {oppName} {_typingOpponentFinalScore}점 (+{prize} 코인)";
            PetEmotionTriggered?.Invoke(this, true);
            GameNotification?.Invoke(this, $"{oppName}에게 타이핑 대결 승리! (+{prize} 코인)");
        }
        else if (TypingMyScore == _typingOpponentFinalScore)
        {
            _progression.AddCoins(_profile, _typingActiveWager);
            await PersistProfileAsync();
            TypingStatusText = $"🤝 무승부! {TypingMyScore}점 동점 (배팅 {_typingActiveWager} 코인 반환)";
            GameNotification?.Invoke(this, $"{oppName}와 타이핑 대결 무승부!");
        }
        else
        {
            TypingStatusText = $"😢 패배! 나 {TypingMyScore}점 vs {oppName} {_typingOpponentFinalScore}점 (-{_typingActiveWager} 코인)";
            PetEmotionTriggered?.Invoke(this, false);
            GameNotification?.Invoke(this, $"{oppName}에게 타이핑 대결 패배...");
        }
    }
}
