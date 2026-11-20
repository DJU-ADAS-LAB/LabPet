using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Games;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;

namespace LabPet.Client.ViewModels;

public sealed record OnlineFriendChoice(FriendEntry Friend, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record LadderSlotOption(
    int ColumnIndex,
    string HeaderText,
    string OccupantText,
    bool IsOccupied,
    bool IsMine)
{
    public bool CanPick => !IsOccupied || IsMine;
}

public sealed record IncomingGameInvite(
    string SenderPetId,
    string SenderName,
    FriendEntry Friend,
    string GameType,
    string SessionId,
    int WagerCoins,
    string Data = "")
{
    public string GameDisplayName => GameType switch
    {
        RpsGameEngine.GameTypeId => "가위바위보",
        TypingSprintEngine.GameTypeId => "타이핑 대결",
        LabLadderEngine.GameTypeId => "사다리타기",
        _ => "코인 게임"
    };

    public string SummaryText => WagerCoins > 0
        ? $"{SenderName}의 {GameDisplayName} 초대! ({WagerCoins} 코인)"
        : $"{SenderName}의 {GameDisplayName} 초대!";
}

/// <summary>
/// 4종 코인 게임(코인 하이로우, LAN 가위바위보 배틀, LAN 타이핑 스프린트, 연구실 공동 사다리타기) 통합 ViewModel.
/// </summary>
public sealed partial class CoinGamesViewModel : ObservableObject
{
    public const int InviteWaitTimeoutSeconds = 20;

    private readonly IAppDataStore _store;
    private readonly SoloProgressionService _progression;
    private readonly SocialCoordinatorService _social;

    private readonly CoinHiLowGame _hiLow = new();
    private readonly DispatcherTimer _typingTimer;
    private CancellationTokenSource? _rpsInviteTimeoutCts;
    private CancellationTokenSource? _typingInviteTimeoutCts;

    private PetProfile _profile = new();

    public event EventHandler<PetProfile>? ProfileChanged;
    public event EventHandler<IncomingGameInvite?>? IncomingInviteChanged;
    public event EventHandler<string>? GameNotification;
    public event EventHandler<bool>? PetEmotionTriggered; // true: 승리/기쁨, false: 패배/어지러움
    public event EventHandler? OpenGameTabRequested;
    public event EventHandler<LadderBoard>? LadderBoardGenerated;

    public IReadOnlyList<int> WagerOptions { get; } = [10, 30, 50, 100];
    public IReadOnlyList<int> LadderFeeOptions { get; } = [0, 10, 30, 50];

    public ObservableCollection<OnlineFriendChoice> OnlineFriends { get; } = [];
    public ObservableCollection<string> LadderResultLines { get; } = [];
    public ObservableCollection<LadderSlotOption> LadderSlots { get; } = [];

    /// <summary>선택된 코인 게임 서브 탭 인덱스 (0: 하이로우, 1: 가위바위보, 2: 타이핑 대결, 3: 연구실 사다리).</summary>
    [ObservableProperty] private int _selectedGameIndex;

    // ── 수신된 게임 초대 ──────────────────────────────────────────────────
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIncomingInvite), nameof(IncomingInviteText))]
    private IncomingGameInvite? _incomingInvite;

    public bool HasIncomingInvite => IncomingInvite is not null;
    public string IncomingInviteText => IncomingInvite?.SummaryText ?? string.Empty;

    partial void OnIncomingInviteChanged(IncomingGameInvite? value)
        => IncomingInviteChanged?.Invoke(this, value);

    // ── 1. 코인 하이로우 (솔로) ───────────────────────────────────────────
    [ObservableProperty] private int _hiLowWager = 30;
    [ObservableProperty] private bool _isHiLowActive;
    [ObservableProperty] private string _hiLowCardDisplay = "?";
    [ObservableProperty] private string _hiLowStreakText = "0연승";
    [ObservableProperty] private string _hiLowMultiplierText = "×1.0";
    [ObservableProperty] private string _hiLowPotentialPayoutText = "0 코인";
    [ObservableProperty] private string _hiLowStatusMessage = "배팅 코인을 고르고 시작을 눌러 주세요. (카드 범위: 1 ~ 13)";

    public bool CanCashOutHiLow => IsHiLowActive && _hiLow.Streak > 0;

    // ── 2. 친구 가위바위보 (LAN P2P) ──────────────────────────────────────
    [ObservableProperty] private OnlineFriendChoice? _selectedRpsFriend;
    [ObservableProperty] private int _rpsWager = 30;
    [ObservableProperty] private string _rpsStatusText = "온라인 친구를 선택해 가위바위보 코인 배틀을 신청해 보세요!";
    [ObservableProperty] private string _rpsMyMoveText = "?";
    [ObservableProperty] private string _rpsOpponentMoveText = "?";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RpsInviteButtonText))]
    private bool _isRpsWaitingResponse;
    [ObservableProperty] private bool _isRpsInMatch;
    [ObservableProperty] private bool _canChooseRpsMove;

    public string RpsInviteButtonText => IsRpsWaitingResponse ? "도전 취소" : "도전 보내기";

    private string? _rpsSessionId;
    private FriendEntry? _rpsOpponent;
    private int _rpsActiveWager;
    private RpsMove? _rpsMyMove;
    private RpsMove? _rpsOpponentMove;

    // ── 3. 친구 타이핑 스프린트 (LAN P2P) ─────────────────────────────────
    [ObservableProperty] private OnlineFriendChoice? _selectedTypingFriend;
    [ObservableProperty] private int _typingWager = 30;
    [ObservableProperty] private string _typingStatusText = "온라인 친구와 10초 타이핑 스프린트 대결을 펼쳐 보세요!";
    [ObservableProperty] private string _typingPromptText = "대결 시작 시 문장이 표시됩니다.";
    [ObservableProperty] private string _typingInputText = string.Empty;
    [ObservableProperty] private int _typingMyScore;
    [ObservableProperty] private int _typingOpponentScore;
    [ObservableProperty] private string _typingTimeLeftText = "10.0초";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TypingInviteButtonText))]
    private bool _isTypingWaitingResponse;
    [ObservableProperty] private bool _isTypingRunning;

    public string TypingInviteButtonText => IsTypingWaitingResponse ? "신청 취소" : "대결 신청";

    private string? _typingSessionId;
    private FriendEntry? _typingOpponent;
    private int _typingActiveWager;
    private bool _isTypingSoloPractice;
    private double _typingRemainingSeconds;
    private int _typingCompletedScore;
    private bool _typingMyFinished;
    private bool _typingOpponentFinished;
    private int _typingOpponentFinalScore;
    private DateTime _lastTypingProgressSentUtc = DateTime.MinValue;

    // ── 4. 연구실 공동 사다리타기 ─────────────────────────────────────────
    [ObservableProperty] private string _ladderParticipantsText = "나, 친구1, 친구2";
    [ObservableProperty] private string _ladderPrizesText = "커피 쏘기, 통과, 통과";
    [ObservableProperty] private int _ladderEntryFee;
    [ObservableProperty] private string _ladderStatusText = "참여자와 결과를 입력하거나, 친구 초대 방을 열어 위치를 고른 뒤 동시에 시작하세요!";
    [ObservableProperty] private bool _isLadderRoomOpen;
    [ObservableProperty] private bool _isLadderHost;
    [ObservableProperty] private bool _canForceStartLadderRoom;
    [ObservableProperty] private string _ladderLobbySummaryText = string.Empty;

    private LadderBoard? _lastLadderBoard;
    private IReadOnlyList<LadderTraceResult> _lastLadderResults = [];
    private LadderRoomStateDto? _ladderRoom;
    private FriendEntry? _ladderHostFriend;
    private readonly HashSet<string> _ladderPendingInviteeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FriendEntry> _ladderJoinedFriends = new(StringComparer.OrdinalIgnoreCase);
    private int _ladderActiveWager;

    public bool HasOnlineFriends => OnlineFriends.Count > 0;

    public CoinGamesViewModel(
        IAppDataStore store,
        SoloProgressionService progression,
        SocialCoordinatorService social)
    {
        _store = store;
        _progression = progression;
        _social = social;

        _typingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _typingTimer.Tick += OnTypingTimerTick;

        _social.StateChanged += (_, _) => RunOnUi(RefreshOnlineFriends);
        _social.GameEventReceived += (_, e) => RunOnUi(() => HandleGameEvent(e));
    }

    public void BindProfile(PetProfile profile)
    {
        _profile = profile;
        RefreshOnlineFriends();
        if (string.IsNullOrWhiteSpace(LadderParticipantsText) || LadderParticipantsText == "나, 친구1, 친구2")
        {
            LoadOnlineFriendsToLadder();
        }
    }

    public void RefreshOnlineFriends()
    {
        var prevRpsId = SelectedRpsFriend?.Friend.PetId;
        var prevTypingId = SelectedTypingFriend?.Friend.PetId;

        var peers = _social.DiscoveredPeers;
        var list = new List<OnlineFriendChoice>();
        foreach (var f in _social.Friends)
        {
            var live = peers.FirstOrDefault(p => string.Equals(p.PetId, f.PetId, StringComparison.OrdinalIgnoreCase));
            if (live is null || live.RequiresUpdate)
            {
                continue;
            }

            var name = SocialCoordinatorService.FormatDisplayName(
                string.IsNullOrWhiteSpace(live.OwnerName) ? f.OwnerName : live.OwnerName,
                string.IsNullOrWhiteSpace(live.PetName) ? f.PetName : live.PetName);
            list.Add(new OnlineFriendChoice(f, name));
        }

        OnlineFriends.Clear();
        foreach (var item in list)
        {
            OnlineFriends.Add(item);
        }

        SelectedRpsFriend = OnlineFriends.FirstOrDefault(x => string.Equals(x.Friend.PetId, prevRpsId, StringComparison.OrdinalIgnoreCase))
            ?? OnlineFriends.FirstOrDefault();
        SelectedTypingFriend = OnlineFriends.FirstOrDefault(x => string.Equals(x.Friend.PetId, prevTypingId, StringComparison.OrdinalIgnoreCase))
            ?? OnlineFriends.FirstOrDefault();

        OnPropertyChanged(nameof(HasOnlineFriends));
    }

    // ══════════════════════════════════════════════════════════════════════
    // 1. 코인 하이로우 (솔로)
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task StartHiLowAsync()
    {
        if (IsHiLowActive)
        {
            return;
        }

        var (ok, msg) = _progression.TrySpendCoins(_profile, HiLowWager);
        if (!ok)
        {
            HiLowStatusMessage = msg;
            return;
        }

        await PersistProfileAsync();

        var card = _hiLow.StartRound(HiLowWager);
        IsHiLowActive = true;
        UpdateHiLowDisplay(card, $"첫 카드는 {CoinHiLowGame.FormatCardLabel(card)}! 다음 카드가 높을까요, 낮을까요?");
    }

    [RelayCommand]
    private Task GuessHigherAsync() => ExecuteHiLowGuessAsync(guessHigher: true);

    [RelayCommand]
    private Task GuessLowerAsync() => ExecuteHiLowGuessAsync(guessHigher: false);

    private async Task ExecuteHiLowGuessAsync(bool guessHigher)
    {
        if (!IsHiLowActive)
        {
            return;
        }

        var result = _hiLow.Guess(guessHigher);
        switch (result.Outcome)
        {
            case HiLowGuessResult.Correct:
                UpdateHiLowDisplay(result.NextCard,
                    $"정답! {CoinHiLowGame.FormatCardLabel(result.PreviousCard)} → {CoinHiLowGame.FormatCardLabel(result.NextCard)} ({result.Streak}연승, 현재 {result.PotentialPayout} 코인)");
                PetEmotionTriggered?.Invoke(this, true);
                break;

            case HiLowGuessResult.Push:
                UpdateHiLowDisplay(result.NextCard,
                    $"같은 숫자({CoinHiLowGame.FormatCardLabel(result.NextCard)})! 무승부로 계속 진행합니다.");
                break;

            case HiLowGuessResult.Wrong:
                IsHiLowActive = false;
                UpdateHiLowDisplay(result.NextCard,
                    $"아쉽네요! {CoinHiLowGame.FormatCardLabel(result.PreviousCard)} → {CoinHiLowGame.FormatCardLabel(result.NextCard)}. 배팅 {HiLowWager} 코인을 잃었습니다.");
                PetEmotionTriggered?.Invoke(this, false);
                await PersistProfileAsync();
                break;
        }
    }

    [RelayCommand]
    private async Task CashOutHiLowAsync()
    {
        if (!CanCashOutHiLow)
        {
            return;
        }

        var payout = _hiLow.CashOut();
        IsHiLowActive = false;
        _progression.AddCoins(_profile, payout);
        await PersistProfileAsync();

        HiLowPotentialPayoutText = "0 코인";
        OnPropertyChanged(nameof(CanCashOutHiLow));
        HiLowStatusMessage = $"🎉 정산 완료! +{payout} 코인을 획득했습니다!";
        PetEmotionTriggered?.Invoke(this, true);
        GameNotification?.Invoke(this, $"하이로우 정산 +{payout} 코인!");
    }

    private void UpdateHiLowDisplay(int card, string message)
    {
        HiLowCardDisplay = CoinHiLowGame.FormatCardLabel(card);
        HiLowStreakText = $"{_hiLow.Streak}연승";
        HiLowMultiplierText = $"×{_hiLow.CurrentMultiplier:0.0}";
        HiLowPotentialPayoutText = $"{_hiLow.PotentialPayout} 코인";
        HiLowStatusMessage = message;
        OnPropertyChanged(nameof(CanCashOutHiLow));
    }

    // ══════════════════════════════════════════════════════════════════════
    // 2. 친구 가위바위보 배틀 (LAN P2P)
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task SendRpsInviteAsync()
    {
        if (IsRpsInMatch)
        {
            return;
        }

        if (IsRpsWaitingResponse)
        {
            await CancelRpsInviteInternalAsync(isTimeout: false);
            return;
        }

        if (SelectedRpsFriend is null)
        {
            RpsStatusText = "대결할 온라인 친구를 먼저 선택해 주세요.";
            return;
        }

        if (_profile.SoloProgress.Coins < RpsWager)
        {
            RpsStatusText = $"코인이 부족합니다. (필요: {RpsWager}, 보유: {_profile.SoloProgress.Coins})";
            return;
        }

        var target = SelectedRpsFriend;
        var sessionId = Guid.NewGuid().ToString("N")[..10];

        // 상대방이 초대를 받자마자 즉시 수락해도 유실되지 않도록 세션 상태를 먼저 등록한다.
        _rpsSessionId = sessionId;
        _rpsOpponent = target.Friend;
        _rpsActiveWager = RpsWager;
        _rpsMyMove = null;
        _rpsOpponentMove = null;
        IsRpsWaitingResponse = true;
        RpsMyMoveText = "?";
        RpsOpponentMoveText = "?";
        RpsStatusText = $"{target.DisplayName}의 수락을 기다리는 중... ({RpsWager} 코인 · {InviteWaitTimeoutSeconds}초 제한)";

        var ok = await _social.SendGameEventAsync(target.Friend, new GameEventPayload
        {
            GameType = RpsGameEngine.GameTypeId,
            SessionId = sessionId,
            Action = "invite",
            WagerCoins = RpsWager
        });

        if (!ok)
        {
            if (_rpsSessionId == sessionId)
            {
                _rpsSessionId = null;
                _rpsOpponent = null;
                IsRpsWaitingResponse = false;
                RpsStatusText = $"{target.DisplayName}에게 도전을 보내지 못했습니다. (연결/방화벽 확인)";
            }

            return;
        }

        StartRpsInviteTimeout(sessionId);
    }

    private void StartRpsInviteTimeout(string sessionId)
    {
        CancelRpsInviteTimeout();
        var cts = new CancellationTokenSource();
        _rpsInviteTimeoutCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(InviteWaitTimeoutSeconds), cts.Token);
                if (!cts.Token.IsCancellationRequested)
                {
                    RunOnUi(() =>
                    {
                        if (IsRpsWaitingResponse && _rpsSessionId == sessionId)
                        {
                            _ = CancelRpsInviteInternalAsync(isTimeout: true);
                        }
                    });
                }
            }
            catch (TaskCanceledException)
            {
            }
        }, cts.Token);
    }

    private void CancelRpsInviteTimeout()
    {
        _rpsInviteTimeoutCts?.Cancel();
        _rpsInviteTimeoutCts?.Dispose();
        _rpsInviteTimeoutCts = null;
    }

    private async Task CancelRpsInviteInternalAsync(bool isTimeout)
    {
        CancelRpsInviteTimeout();
        var sessionId = _rpsSessionId;
        var opponent = _rpsOpponent;
        var wager = _rpsActiveWager;

        _rpsSessionId = null;
        _rpsOpponent = null;
        IsRpsWaitingResponse = false;
        RpsStatusText = isTimeout
            ? $"수락 대기 시간({InviteWaitTimeoutSeconds}초)이 초과되어 도전이 자동 취소되었습니다."
            : "가위바위보 도전을 취소했습니다.";

        if (opponent is not null && sessionId is not null)
        {
            _ = await _social.SendGameEventAsync(opponent, new GameEventPayload
            {
                GameType = RpsGameEngine.GameTypeId,
                SessionId = sessionId,
                Action = "cancel",
                WagerCoins = wager
            });
        }
    }

    [RelayCommand]
    private async Task ChooseRpsMoveAsync(string? moveToken)
    {
        if (!IsRpsInMatch || !CanChooseRpsMove || _rpsOpponent is null || _rpsSessionId is null)
        {
            return;
        }

        if (!RpsGameEngine.TryParseToken(moveToken, out var move))
        {
            return;
        }

        _rpsMyMove = move;
        CanChooseRpsMove = false;
        RpsMyMoveText = RpsGameEngine.FormatKorean(move);
        RpsStatusText = "선택 완료! 상대의 선택을 기다리는 중...";

        await _social.SendGameEventAsync(_rpsOpponent, new GameEventPayload
        {
            GameType = RpsGameEngine.GameTypeId,
            SessionId = _rpsSessionId,
            Action = "move",
            WagerCoins = _rpsActiveWager,
            Data = RpsGameEngine.ToToken(move)
        });

        await TryResolveRpsMatchAsync();
    }

    private async Task TryResolveRpsMatchAsync()
    {
        if (!IsRpsInMatch || _rpsMyMove is null || _rpsOpponentMove is null)
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

    // ══════════════════════════════════════════════════════════════════════
    // 3. 친구 타이핑 스프린트 대결 (LAN P2P + 10초 솔로 연습)
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task SendTypingInviteAsync()
    {
        if (IsTypingRunning)
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
        var sessionId = Guid.NewGuid().ToString("N")[..10];

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
        if (IsTypingRunning || IsTypingWaitingResponse)
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
        _typingMyFinished = false;
        _typingOpponentFinished = false;
        _typingOpponentFinalScore = 0;
        _typingRemainingSeconds = TypingSprintEngine.SprintDurationSeconds;

        TypingMyScore = 0;
        TypingOpponentScore = 0;
        TypingInputText = string.Empty;
        TypingPromptText = TypingSprintEngine.PickPrompt();
        TypingTimeLeftText = $"{_typingRemainingSeconds:0.0}초";
        IsTypingWaitingResponse = false;
        IsTypingRunning = true;
        TypingStatusText = isSoloPractice
            ? "10초 연습 시작! 아래 문장을 빠르게 입력하고 Enter를 누르세요."
            : $"🔥 10초 타이핑 대결 시작! ({wager} 코인) 빠르게 입력하고 Enter!";

        _typingTimer.Start();
    }

    partial void OnTypingInputTextChanged(string value)
    {
        if (!IsTypingRunning)
        {
            return;
        }

        // 정확히 일치하면 Enter 없이도 즉시 다음 문장으로 넘어가 쾌적한 속도감 제공
        var completed = TypingSprintEngine.EvaluateCompletedPrompt(TypingPromptText, value);
        if (completed > 0)
        {
            _typingCompletedScore += completed;
            TypingMyScore = _typingCompletedScore;
            TypingPromptText = TypingSprintEngine.PickPrompt(exclude: TypingPromptText);
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
                    Data = TypingMyScore.ToString()
                });
            }

            await TryResolveTypingMatchAsync();
        }
    }

    private async Task TryResolveTypingMatchAsync()
    {
        if (!_typingMyFinished || !_typingOpponentFinished)
        {
            return;
        }

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

    // ══════════════════════════════════════════════════════════════════════
    // 공용 초대 수락 / 거절 (펫 말풍선 또는 게임 탭 배너)
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    public async Task AcceptIncomingInviteAsync()
    {
        var invite = IncomingInvite;
        if (invite is null)
        {
            return;
        }

        var (ok, msg) = _progression.TrySpendCoins(_profile, invite.WagerCoins);
        if (!ok)
        {
            GameNotification?.Invoke(this, $"도전 수락 실패: {msg}");
            await DeclineIncomingInviteAsync();
            return;
        }

        IncomingInvite = null;
        await PersistProfileAsync();

        var sent = await _social.SendGameEventAsync(invite.Friend, new GameEventPayload
        {
            GameType = invite.GameType,
            SessionId = invite.SessionId,
            Action = "accept",
            WagerCoins = invite.WagerCoins
        });

        if (!sent)
        {
            _progression.AddCoins(_profile, invite.WagerCoins);
            await PersistProfileAsync();
            GameNotification?.Invoke(this, "상대와 연결이 끊겨 배팅 코인을 반환했습니다.");
            return;
        }

        if (invite.GameType == RpsGameEngine.GameTypeId)
        {
            _rpsSessionId = invite.SessionId;
            _rpsOpponent = invite.Friend;
            _rpsActiveWager = invite.WagerCoins;
            _rpsMyMove = null;
            _rpsOpponentMove = null;
            IsRpsWaitingResponse = false;
            IsRpsInMatch = true;
            CanChooseRpsMove = true;
            RpsMyMoveText = "?";
            RpsOpponentMoveText = "?";
            RpsStatusText = $"{invite.SenderName}와 대결 시작! ({invite.WagerCoins} 코인) 아래에서 선택하세요!";
            SelectedGameIndex = 1;
            OpenGameTabRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (invite.GameType == TypingSprintEngine.GameTypeId)
        {
            SelectedGameIndex = 2;
            OpenGameTabRequested?.Invoke(this, EventArgs.Empty);
            BeginTypingSprint(isSoloPractice: false, invite.Friend, invite.SessionId, invite.WagerCoins);
        }
        else if (invite.GameType == LabLadderEngine.GameTypeId)
        {
            _ladderHostFriend = invite.Friend;
            _ladderActiveWager = invite.WagerCoins;

            LadderRoomStateDto? initialRoom = null;
            if (!string.IsNullOrWhiteSpace(invite.Data))
            {
                try
                {
                    initialRoom = JsonSerializer.Deserialize<LadderRoomStateDto>(invite.Data, ProtocolJson.PacketOptions);
                }
                catch (JsonException)
                {
                }
            }

            _ladderRoom = initialRoom ?? new LadderRoomStateDto
            {
                SessionId = invite.SessionId,
                HostPetId = invite.SenderPetId,
                WagerCoins = invite.WagerCoins,
                SlotCount = LabLadderEngine.MinParticipants
            };
            IsLadderHost = false;
            IsLadderRoomOpen = true;
            CanForceStartLadderRoom = false;
            RefreshLadderLobbyUi();
            LadderLobbySummaryText = $"{invite.SenderName}의 사다리방에 입장했습니다! 시작 위치를 고르세요.";
            LadderStatusText = "다른 친구들이 들어와 위치를 고르면 동시에 시작됩니다.";
            SelectedGameIndex = 3;
            OpenGameTabRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    public async Task DeclineIncomingInviteAsync()
    {
        var invite = IncomingInvite;
        if (invite is null)
        {
            return;
        }

        IncomingInvite = null;
        await _social.SendGameEventAsync(invite.Friend, new GameEventPayload
        {
            GameType = invite.GameType,
            SessionId = invite.SessionId,
            Action = "decline",
            WagerCoins = invite.WagerCoins
        });
    }

    private async void HandleGameEvent(GameEventReceivedEventArgs e)
    {
        var p = e.Payload;
        var friend = _social.Friends.FirstOrDefault(f => string.Equals(f.PetId, e.SenderPetId, StringComparison.OrdinalIgnoreCase));
        if (friend is null)
        {
            return;
        }

        var senderName = _social.GetFriendDisplayName(e.SenderPetId);

        if (p.Action == "invite")
        {
            var invite = new IncomingGameInvite(
                e.SenderPetId,
                senderName,
                friend,
                p.GameType,
                p.SessionId,
                Math.Max(0, p.WagerCoins),
                p.Data ?? string.Empty);
            IncomingInvite = invite;
            return;
        }

        if (p.Action == "cancel" && IncomingInvite is not null &&
            string.Equals(IncomingInvite.SessionId, p.SessionId, StringComparison.Ordinal))
        {
            IncomingInvite = null;
            GameNotification?.Invoke(this, $"{senderName}(이)가 게임 초대를 취소했습니다.");
            return;
        }

        if (p.GameType == RpsGameEngine.GameTypeId && string.Equals(p.SessionId, _rpsSessionId, StringComparison.Ordinal))
        {
            switch (p.Action)
            {
                case "accept":
                    CancelRpsInviteTimeout();
                    var (ok, msg) = _progression.TrySpendCoins(_profile, _rpsActiveWager);
                    if (!ok)
                    {
                        IsRpsWaitingResponse = false;
                        RpsStatusText = msg;
                        return;
                    }

                    await PersistProfileAsync();
                    IsRpsWaitingResponse = false;
                    IsRpsInMatch = true;
                    CanChooseRpsMove = true;
                    RpsStatusText = $"{senderName} 수락! ({_rpsActiveWager} 코인) 바위 · 가위 · 보 중 선택하세요!";
                    break;

                case "decline":
                    CancelRpsInviteTimeout();
                    IsRpsWaitingResponse = false;
                    IsRpsInMatch = false;
                    CanChooseRpsMove = false;
                    RpsStatusText = $"{senderName}(이)가 가위바위보 도전을 거절했어요.";
                    break;

                case "move":
                    if (RpsGameEngine.TryParseToken(p.Data, out var oppMove))
                    {
                        _rpsOpponentMove = oppMove;
                        if (IsRpsInMatch && _rpsMyMove is null)
                        {
                            RpsStatusText = $"{senderName}(이)가 선택을 마쳤습니다! 어서 골라주세요!";
                        }

                        await TryResolveRpsMatchAsync();
                    }

                    break;
            }
        }
        else if (p.GameType == TypingSprintEngine.GameTypeId && string.Equals(p.SessionId, _typingSessionId, StringComparison.Ordinal))
        {
            switch (p.Action)
            {
                case "accept":
                    CancelTypingInviteTimeout();
                    var (ok, msg) = _progression.TrySpendCoins(_profile, _typingActiveWager);
                    if (!ok)
                    {
                        IsTypingWaitingResponse = false;
                        TypingStatusText = msg;
                        return;
                    }

                    await PersistProfileAsync();
                    BeginTypingSprint(isSoloPractice: false, friend, _typingSessionId, _typingActiveWager);
                    break;

                case "decline":
                    CancelTypingInviteTimeout();
                    IsTypingWaitingResponse = false;
                    TypingStatusText = $"{senderName}(이)가 타이핑 대결 도전을 거절했어요.";
                    break;

                case "progress":
                    if (int.TryParse(p.Data, out var liveScore))
                    {
                        TypingOpponentScore = liveScore;
                    }

                    break;

                case "finish":
                    if (int.TryParse(p.Data, out var finalScore))
                    {
                        _typingOpponentFinalScore = finalScore;
                        _typingOpponentFinished = true;
                        TypingOpponentScore = finalScore;
                        await TryResolveTypingMatchAsync();
                    }

                    break;
            }
        }
        else if (p.GameType == LabLadderEngine.GameTypeId && _ladderRoom is not null &&
                 string.Equals(p.SessionId, _ladderRoom.SessionId, StringComparison.Ordinal))
        {
            await HandleLadderEventAsync(e.SenderPetId, senderName, friend, p);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // 4. 연구실 공동 사다리타기 (LAN 대기실 · 위치 선택 · 동시 시작 + 솔로)
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task OpenLadderRoomAsync()
    {
        if (IsLadderRoomOpen)
        {
            return;
        }

        if (OnlineFriends.Count == 0)
        {
            LadderStatusText = "초대할 온라인 친구가 없습니다. 혼자 바로 돌리기를 이용해 주세요.";
            return;
        }

        if (LadderEntryFee > 0)
        {
            var (ok, msg) = _progression.TrySpendCoins(_profile, LadderEntryFee);
            if (!ok)
            {
                LadderStatusText = msg;
                return;
            }

            await PersistProfileAsync();
        }

        var invitees = OnlineFriends.Take(LabLadderEngine.MaxParticipants - 1).ToList();
        var sessionId = Guid.NewGuid().ToString("N")[..10];
        var myName = string.IsNullOrWhiteSpace(_profile.OwnerName) ? _profile.PetName : _profile.OwnerName;
        var prizes = ParseTokens(LadderPrizesText);

        _ladderPendingInviteeIds.Clear();
        _ladderJoinedFriends.Clear();
        _ladderHostFriend = null;
        _ladderActiveWager = LadderEntryFee;

        foreach (var choice in invitees)
        {
            _ladderPendingInviteeIds.Add(choice.Friend.PetId);
        }

        var initialSlots = Math.Clamp(1 + invitees.Count, LabLadderEngine.MinParticipants, LabLadderEngine.MaxParticipants);
        while (prizes.Count < initialSlots)
        {
            prizes.Add("통과");
        }

        // 조기 수락(accept) 패킷이 도착해도 유실되지 않도록 초대 전송 전에 _ladderRoom을 먼저 초기화한다.
        _ladderRoom = new LadderRoomStateDto
        {
            SessionId = sessionId,
            HostPetId = _profile.PetId,
            WagerCoins = LadderEntryFee,
            SlotCount = initialSlots,
            PendingInviteCount = invitees.Count,
            Prizes = prizes.Take(initialSlots).ToList(),
            Members =
            [
                new LadderMemberStateDto
                {
                    PetId = _profile.PetId,
                    DisplayName = myName,
                    SelectedSlot = null
                }
            ]
        };

        IsLadderHost = true;
        IsLadderRoomOpen = true;
        LadderResultLines.Clear();
        RefreshLadderLobbyUi();

        var initialRoomJson = JsonSerializer.Serialize(_ladderRoom, ProtocolJson.PacketOptions);
        var sendTasks = invitees.Select(async choice =>
        {
            var sent = await _social.SendGameEventAsync(choice.Friend, new GameEventPayload
            {
                GameType = LabLadderEngine.GameTypeId,
                SessionId = sessionId,
                Action = "invite",
                WagerCoins = LadderEntryFee,
                Data = initialRoomJson
            });
            return (choice.Friend.PetId, sent);
        }).ToList();

        var results = await Task.WhenAll(sendTasks);
        if (_ladderRoom is null || _ladderRoom.SessionId != sessionId)
        {
            return;
        }

        var sentCount = 0;
        foreach (var (petId, sent) in results)
        {
            if (sent)
            {
                sentCount++;
            }
            else
            {
                _ladderPendingInviteeIds.Remove(petId);
            }
        }

        if (sentCount == 0 && _ladderJoinedFriends.Count == 0)
        {
            if (LadderEntryFee > 0)
            {
                _progression.AddCoins(_profile, LadderEntryFee);
                await PersistProfileAsync();
            }

            CloseLadderRoomState("온라인 친구에게 초대를 전송하지 못했습니다.");
            return;
        }

        _ladderRoom.PendingInviteCount = _ladderPendingInviteeIds.Count;
        RecalculateHostSlotCount();
        RefreshLadderLobbyUi();
        await BroadcastLadderStateAsync();
        LadderStatusText = $"친구 {sentCount}명에게 초대 발송! 원하는 시작 번호를 골라 주세요.";
    }

    [RelayCommand]
    private async Task PickLadderSlotAsync(int slotIndex)
    {
        if (!IsLadderRoomOpen || _ladderRoom is null)
        {
            return;
        }

        if (IsLadderHost)
        {
            if (!LabLadderEngine.TryPickSlot(_ladderRoom, _profile.PetId, slotIndex))
            {
                LadderStatusText = $"{slotIndex + 1}번 자리는 이미 다른 친구가 선택했습니다.";
                return;
            }

            RefreshLadderLobbyUi();
            await BroadcastLadderStateAsync();

            if (LabLadderEngine.IsReadyToAutoStart(_ladderRoom))
            {
                await StartMultiplayerLadderAsync();
            }
        }
        else if (_ladderHostFriend is not null)
        {
            await _social.SendGameEventAsync(_ladderHostFriend, new GameEventPayload
            {
                GameType = LabLadderEngine.GameTypeId,
                SessionId = _ladderRoom.SessionId,
                Action = "pick",
                WagerCoins = _ladderActiveWager,
                Data = slotIndex.ToString()
            });
        }
    }

    [RelayCommand]
    private async Task ForceStartLadderRoomAsync()
    {
        if (!IsLadderRoomOpen || !IsLadderHost || _ladderRoom is null)
        {
            return;
        }

        if (!LabLadderEngine.AreJoinedMembersReady(_ladderRoom))
        {
            LadderStatusText = "입장한 참가자(최소 2명)가 모두 위치를 선택해야 시작할 수 있습니다.";
            return;
        }

        await StartMultiplayerLadderAsync();
    }

    [RelayCommand]
    private async Task CancelLadderRoomAsync()
    {
        if (!IsLadderRoomOpen || _ladderRoom is null)
        {
            return;
        }

        var sessionId = _ladderRoom.SessionId;
        if (IsLadderHost)
        {
            foreach (var friend in _ladderJoinedFriends.Values)
            {
                _ = await _social.SendGameEventAsync(friend, new GameEventPayload
                {
                    GameType = LabLadderEngine.GameTypeId,
                    SessionId = sessionId,
                    Action = "cancel",
                    WagerCoins = _ladderActiveWager
                });
            }
        }
        else if (_ladderHostFriend is not null)
        {
            _ = await _social.SendGameEventAsync(_ladderHostFriend, new GameEventPayload
            {
                GameType = LabLadderEngine.GameTypeId,
                SessionId = sessionId,
                Action = "decline",
                WagerCoins = _ladderActiveWager
            });
        }

        if (_ladderActiveWager > 0)
        {
            _progression.AddCoins(_profile, _ladderActiveWager);
            await PersistProfileAsync();
        }

        CloseLadderRoomState("사다리방을 취소했습니다.");
    }

    private async Task HandleLadderEventAsync(string senderPetId, string senderName, FriendEntry friend, GameEventPayload p)
    {
        if (_ladderRoom is null)
        {
            return;
        }

        if (IsLadderHost)
        {
            switch (p.Action)
            {
                case "accept":
                {
                    _ladderPendingInviteeIds.Remove(senderPetId);
                    _ladderJoinedFriends[senderPetId] = friend;
                    var shortName = string.IsNullOrWhiteSpace(friend.OwnerName) ? friend.PetName : friend.OwnerName;
                    if (!_ladderRoom.Members.Any(m => string.Equals(m.PetId, senderPetId, StringComparison.OrdinalIgnoreCase)))
                    {
                        _ladderRoom.Members.Add(new LadderMemberStateDto
                        {
                            PetId = senderPetId,
                            DisplayName = shortName,
                            SelectedSlot = null
                        });
                    }

                    _ladderRoom.PendingInviteCount = _ladderPendingInviteeIds.Count;
                    RecalculateHostSlotCount();
                    RefreshLadderLobbyUi();
                    await BroadcastLadderStateAsync();
                    break;
                }

                case "decline":
                {
                    _ladderPendingInviteeIds.Remove(senderPetId);
                    _ladderJoinedFriends.Remove(senderPetId);
                    _ladderRoom.Members.RemoveAll(m => string.Equals(m.PetId, senderPetId, StringComparison.OrdinalIgnoreCase));
                    _ladderRoom.PendingInviteCount = _ladderPendingInviteeIds.Count;

                    if (_ladderRoom.Members.Count < LabLadderEngine.MinParticipants && _ladderRoom.PendingInviteCount == 0)
                    {
                        if (_ladderActiveWager > 0)
                        {
                            _progression.AddCoins(_profile, _ladderActiveWager);
                            await PersistProfileAsync();
                        }

                        CloseLadderRoomState("참가할 친구가 없어 사다리방이 종료되었습니다.");
                        return;
                    }

                    RecalculateHostSlotCount();
                    RefreshLadderLobbyUi();
                    await BroadcastLadderStateAsync();

                    if (LabLadderEngine.IsReadyToAutoStart(_ladderRoom))
                    {
                        await StartMultiplayerLadderAsync();
                    }

                    break;
                }

                case "pick":
                {
                    if (int.TryParse(p.Data, out var slotIdx) && LabLadderEngine.TryPickSlot(_ladderRoom, senderPetId, slotIdx))
                    {
                        RefreshLadderLobbyUi();
                        await BroadcastLadderStateAsync();

                        if (LabLadderEngine.IsReadyToAutoStart(_ladderRoom))
                        {
                            await StartMultiplayerLadderAsync();
                        }
                    }

                    break;
                }
            }
        }
        else
        {
            switch (p.Action)
            {
                case "state":
                {
                    var state = JsonSerializer.Deserialize<LadderRoomStateDto>(p.Data, ProtocolJson.PacketOptions);
                    if (state is not null)
                    {
                        _ladderRoom = state;
                        RefreshLadderLobbyUi();
                    }

                    break;
                }

                case "start":
                {
                    var startDto = JsonSerializer.Deserialize<LadderStartDto>(p.Data, ProtocolJson.PacketOptions);
                    if (startDto is not null)
                    {
                        await ApplyStartedMultiplayerLadderAsync(startDto);
                    }

                    break;
                }

                case "cancel":
                {
                    if (_ladderActiveWager > 0)
                    {
                        _progression.AddCoins(_profile, _ladderActiveWager);
                        await PersistProfileAsync();
                    }

                    CloseLadderRoomState($"{senderName}(이)가 사다리방을 취소했습니다.");
                    break;
                }
            }
        }
    }

    private void RecalculateHostSlotCount()
    {
        if (_ladderRoom is null)
        {
            return;
        }

        var desired = Math.Clamp(_ladderRoom.Members.Count + _ladderRoom.PendingInviteCount, LabLadderEngine.MinParticipants, LabLadderEngine.MaxParticipants);
        var maxPicked = _ladderRoom.Members.Where(m => m.SelectedSlot.HasValue).Select(m => m.SelectedSlot!.Value + 1).DefaultIfEmpty(0).Max();
        _ladderRoom.SlotCount = Math.Max(desired, maxPicked);
    }

    private async Task BroadcastLadderStateAsync()
    {
        if (_ladderRoom is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(_ladderRoom, ProtocolJson.PacketOptions);
        foreach (var friend in _ladderJoinedFriends.Values)
        {
            _ = await _social.SendGameEventAsync(friend, new GameEventPayload
            {
                GameType = LabLadderEngine.GameTypeId,
                SessionId = _ladderRoom.SessionId,
                Action = "state",
                WagerCoins = _ladderRoom.WagerCoins,
                Data = json
            });
        }
    }

    private async Task StartMultiplayerLadderAsync()
    {
        if (_ladderRoom is null)
        {
            return;
        }

        var orderedMembers = _ladderRoom.Members
            .Where(m => m.SelectedSlot.HasValue)
            .OrderBy(m => m.SelectedSlot!.Value)
            .ToList();

        if (orderedMembers.Count < LabLadderEngine.MinParticipants)
        {
            return;
        }

        var rawPrizes = ParseTokens(LadderPrizesText);
        while (rawPrizes.Count < orderedMembers.Count)
        {
            rawPrizes.Add("통과");
        }

        var shuffledPrizes = rawPrizes.Take(orderedMembers.Count).OrderBy(_ => Random.Shared.Next()).ToList();
        var names = orderedMembers.Select(m => m.DisplayName).ToList();
        var board = LabLadderEngine.GenerateBoard(names, shuffledPrizes);

        var startDto = new LadderStartDto
        {
            SessionId = _ladderRoom.SessionId,
            WagerCoins = _ladderRoom.WagerCoins,
            Levels = board.Levels,
            ParticipantPetIds = orderedMembers.Select(m => m.PetId).ToList(),
            Participants = board.Participants.ToList(),
            Prizes = board.Prizes.ToList(),
            Rungs = board.Rungs.ToList()
        };

        var json = JsonSerializer.Serialize(startDto, ProtocolJson.PacketOptions);
        foreach (var friend in _ladderJoinedFriends.Values)
        {
            _ = await _social.SendGameEventAsync(friend, new GameEventPayload
            {
                GameType = LabLadderEngine.GameTypeId,
                SessionId = _ladderRoom.SessionId,
                Action = "start",
                WagerCoins = _ladderRoom.WagerCoins,
                Data = json
            });
        }

        await ApplyStartedMultiplayerLadderAsync(startDto);
    }

    private async Task ApplyStartedMultiplayerLadderAsync(LadderStartDto startDto)
    {
        var board = new LadderBoard(startDto.Participants, startDto.Prizes, startDto.Levels, startDto.Rungs);
        var results = LabLadderEngine.TraceAll(board);

        _lastLadderBoard = board;
        _lastLadderResults = results;

        LadderResultLines.Clear();
        foreach (var r in results)
        {
            LadderResultLines.Add($"{r.Participant}  →  {r.Prize}");
        }

        // 내 결과 확인 및 코인 잭팟 정산
        var myCol = startDto.ParticipantPetIds.FindIndex(id => string.Equals(id, _profile.PetId, StringComparison.OrdinalIgnoreCase));
        var myResult = myCol >= 0 && myCol < results.Count ? results[myCol] : null;
        string statusMsg;

        if (myResult is not null && startDto.WagerCoins > 0 && myResult.Prize.Contains("잭팟", StringComparison.OrdinalIgnoreCase))
        {
            var jackpot = startDto.WagerCoins * results.Count;
            _progression.AddCoins(_profile, jackpot);
            await PersistProfileAsync();
            statusMsg = $"🎉 동시 사다리 당첨! {myResult.Participant} → {myResult.Prize} (+{jackpot} 코인)";
            PetEmotionTriggered?.Invoke(this, true);
            GameNotification?.Invoke(this, $"🪜 사다리 잭팟 당첨! (+{jackpot} 코인)");
        }
        else if (myResult is not null)
        {
            statusMsg = $"🪜 동시 사다리 결과: {myResult.Participant} → {myResult.Prize}";
            GameNotification?.Invoke(this, $"🪜 사다리 결과: {myResult.Prize}");
        }
        else
        {
            statusMsg = $"🪜 동시 사다리 완료! ({results.Count}명 참여)";
        }

        CloseLadderRoomState(statusMsg);
        LadderBoardGenerated?.Invoke(this, board);
    }

    private void RefreshLadderLobbyUi()
    {
        LadderSlots.Clear();
        if (_ladderRoom is null)
        {
            CanForceStartLadderRoom = false;
            LadderLobbySummaryText = string.Empty;
            return;
        }

        var pickedCount = _ladderRoom.Members.Count(m => m.SelectedSlot.HasValue);
        var joinedCount = _ladderRoom.Members.Count;
        var pendingText = _ladderRoom.PendingInviteCount > 0 ? $" · 응답 대기 {_ladderRoom.PendingInviteCount}명" : string.Empty;
        LadderLobbySummaryText = $"참가 {joinedCount}명 · 위치 선택 {pickedCount}/{joinedCount}{pendingText} (전원 선택 시 자동 시작)";
        CanForceStartLadderRoom = IsLadderHost && LabLadderEngine.AreJoinedMembersReady(_ladderRoom);

        for (var col = 0; col < _ladderRoom.SlotCount; col++)
        {
            var occupant = _ladderRoom.Members.FirstOrDefault(m => m.SelectedSlot == col);
            var isMine = occupant is not null && string.Equals(occupant.PetId, _profile.PetId, StringComparison.OrdinalIgnoreCase);
            LadderSlots.Add(new LadderSlotOption(
                col,
                $"{col + 1}번",
                occupant is null ? "빈 자리" : (isMine ? $"{occupant.DisplayName} (나)" : occupant.DisplayName),
                occupant is not null,
                isMine));
        }
    }

    private void CloseLadderRoomState(string statusMessage)
    {
        _ladderRoom = null;
        _ladderHostFriend = null;
        _ladderPendingInviteeIds.Clear();
        _ladderJoinedFriends.Clear();
        _ladderActiveWager = 0;
        IsLadderRoomOpen = false;
        IsLadderHost = false;
        CanForceStartLadderRoom = false;
        LadderLobbySummaryText = string.Empty;
        LadderSlots.Clear();
        LadderStatusText = statusMessage;
    }

    [RelayCommand]
    private void LoadOnlineFriendsToLadder()
    {
        var names = new List<string>
        {
            string.IsNullOrWhiteSpace(_profile.OwnerName) ? _profile.PetName : _profile.OwnerName
        };

        foreach (var f in OnlineFriends.Take(LabLadderEngine.MaxParticipants - 1))
        {
            var shortName = string.IsNullOrWhiteSpace(f.Friend.OwnerName) ? f.Friend.PetName : f.Friend.OwnerName;
            if (!names.Contains(shortName, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(shortName);
            }
        }

        if (names.Count < 2)
        {
            names.Add("친구1");
            names.Add("친구2");
        }

        LadderParticipantsText = string.Join(", ", names);
        ApplyCoffeePreset();
    }

    [RelayCommand]
    private void ApplyCoffeePreset()
    {
        var count = Math.Clamp(ParseTokens(LadderParticipantsText).Count, LabLadderEngine.MinParticipants, LabLadderEngine.MaxParticipants);
        var prizes = new List<string> { "☕ 커피 쏘기" };
        for (var i = 1; i < count; i++)
        {
            prizes.Add("통과");
        }

        LadderPrizesText = string.Join(", ", prizes);
    }

    [RelayCommand]
    private void ApplyCoinJackpotPreset()
    {
        var count = Math.Clamp(ParseTokens(LadderParticipantsText).Count, LabLadderEngine.MinParticipants, LabLadderEngine.MaxParticipants);
        var prizes = new List<string> { "💰 코인 잭팟!" };
        for (var i = 1; i < count; i++)
        {
            prizes.Add("꽝");
        }

        LadderPrizesText = string.Join(", ", prizes);
        if (LadderEntryFee == 0)
        {
            LadderEntryFee = 30;
        }
    }

    [RelayCommand]
    private async Task RunLadderAsync()
    {
        var participants = ParseTokens(LadderParticipantsText);
        if (participants.Count < LabLadderEngine.MinParticipants || participants.Count > LabLadderEngine.MaxParticipants)
        {
            LadderStatusText = $"참여자는 {LabLadderEngine.MinParticipants}~{LabLadderEngine.MaxParticipants}명으로 입력해 주세요. (쉼표 구분)";
            return;
        }

        var prizes = ParseTokens(LadderPrizesText);
        while (prizes.Count < participants.Count)
        {
            prizes.Add("통과");
        }

        // 선택적 코인 참가비 차감
        if (LadderEntryFee > 0)
        {
            var (ok, msg) = _progression.TrySpendCoins(_profile, LadderEntryFee);
            if (!ok)
            {
                LadderStatusText = msg;
                return;
            }
        }

        // 결과 순서를 매 실행마다 셔플해 공정성 확보
        var shuffledPrizes = prizes.Take(participants.Count).OrderBy(_ => Random.Shared.Next()).ToList();
        var board = LabLadderEngine.GenerateBoard(participants, shuffledPrizes);
        var results = LabLadderEngine.TraceAll(board);

        _lastLadderBoard = board;
        _lastLadderResults = results;

        LadderResultLines.Clear();
        foreach (var r in results)
        {
            LadderResultLines.Add($"{r.Participant}  →  {r.Prize}");
        }

        // 첫 번째 참여자(나)가 잭팟 당첨 시 참가비 × 인원수만큼 코인 지급
        if (LadderEntryFee > 0 && results.Count > 0 && results[0].Prize.Contains("잭팟", StringComparison.OrdinalIgnoreCase))
        {
            var jackpot = LadderEntryFee * participants.Count;
            _progression.AddCoins(_profile, jackpot);
            LadderStatusText = $"🎉 사다리 결과: {results[0].Participant} → {results[0].Prize} (+{jackpot} 코인 획득!)";
            PetEmotionTriggered?.Invoke(this, true);
        }
        else
        {
            LadderStatusText = $"🪜 사다리 완료! ({participants.Count}명 참여)";
        }

        await PersistProfileAsync();
        LadderBoardGenerated?.Invoke(this, board);
    }

    [RelayCommand]
    private async Task ShareLadderResultToFriendsAsync()
    {
        if (_lastLadderResults.Count == 0)
        {
            LadderStatusText = "먼저 사다리를 실행해 주세요.";
            return;
        }

        if (OnlineFriends.Count == 0)
        {
            LadderStatusText = "결과를 공유할 온라인 친구가 없습니다.";
            return;
        }

        var summary = "🪜사다리: " + string.Join(" / ", _lastLadderResults.Select(r => $"{r.Participant}→{r.Prize}"));
        if (summary.Length > ProtocolConstants.MaxBubbleMessageLength)
        {
            summary = summary[..(ProtocolConstants.MaxBubbleMessageLength - 1)] + "…";
        }

        var sentCount = 0;
        foreach (var f in OnlineFriends)
        {
            if (await _social.SendBubbleMessageAsync(f.Friend, summary))
            {
                sentCount++;
            }
        }

        LadderStatusText = sentCount > 0
            ? $"📢 온라인 친구 {sentCount}명에게 사다리 결과를 공유했습니다!"
            : "친구들에게 결과를 전송하지 못했습니다.";
    }

    private static List<string> ParseTokens(string? raw)
        => (raw ?? string.Empty)
            .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

    private async Task PersistProfileAsync()
    {
        await _store.SaveProfileAsync(_profile);
        ProfileChanged?.Invoke(this, _profile);
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
