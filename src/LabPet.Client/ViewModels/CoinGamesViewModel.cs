using System.Collections.ObjectModel;
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
public sealed partial class CoinGamesViewModel : ObservableObject, IDisposable
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
    private readonly List<string> _typingCompletedPrompts = [];
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
    private bool _ladderStarting;
    private readonly Dictionary<(string Sender, string Session), DateTimeOffset> _seenInvitations = [];
    private const int RecentInvitationCapacity = 256;

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

        _social.StateChanged += OnSocialStateChanged;
        _social.GameEventReceived += OnSocialGameEvent;
    }

    private void OnSocialStateChanged(object? sender, EventArgs e) => RunOnUi(RefreshOnlineFriends);

    private void OnSocialGameEvent(object? sender, GameEventReceivedEventArgs e)
        => RunOnUi(() => _ = HandleGameEventAsync(e));

    public void Dispose()
    {
        _social.StateChanged -= OnSocialStateChanged;
        _social.GameEventReceived -= OnSocialGameEvent;
        _typingTimer.Stop();
        _typingTimer.Tick -= OnTypingTimerTick;
        CancelRpsInviteTimeout();
        CancelTypingInviteTimeout();
        _rpsSessionId = null;
        _typingSessionId = null;
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
        var sessionId = Guid.NewGuid().ToString("N");

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

    // ══════════════════════════════════════════════════════════════════════
    // 공용 초대 수락 / 거절 (펫 말풍선 또는 게임 탭 배너)
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    public async Task AcceptIncomingInviteAsync()
    {
        var invite = IncomingInvite;
        if (invite is null ||
            (invite.GameType == RpsGameEngine.GameTypeId && (IsRpsInMatch || IsRpsWaitingResponse)) ||
            (invite.GameType == TypingSprintEngine.GameTypeId && (IsTypingRunning || IsTypingWaitingResponse || _typingSessionId is not null)) ||
            (invite.GameType == LabLadderEngine.GameTypeId && IsLadderRoomOpen))
        {
            return;
        }
        LadderRoomStateDto? initialRoom = null;
        if (invite.GameType == LabLadderEngine.GameTypeId
            && (!TryReadLadderPayload(invite.Data, out initialRoom) || initialRoom is null
                || !LabLadderEngine.IsValidRoom(initialRoom, invite.SessionId, invite.SenderPetId, invite.WagerCoins)))
        {
            await DeclineIncomingInviteAsync();
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
        if (invite.GameType == RpsGameEngine.GameTypeId)
        {
            _rpsSessionId = invite.SessionId;
            _rpsOpponent = invite.Friend;
            _rpsActiveWager = invite.WagerCoins;
            BeginRpsRound(invite.SessionId);
            CanChooseRpsMove = false;
        }
        else if (invite.GameType == TypingSprintEngine.GameTypeId)
        {
            BeginTypingSprint(isSoloPractice: false, invite.Friend, invite.SessionId, invite.WagerCoins);
        }
        else if (initialRoom is not null)
        {
            _ladderHostFriend = invite.Friend;
            _ladderActiveWager = invite.WagerCoins;
            _ladderRoom = initialRoom;
            IsLadderHost = false;
            IsLadderRoomOpen = true;
        }
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
            if (invite.GameType == RpsGameEngine.GameTypeId && _rpsSessionId == invite.SessionId)
            {
                await RefundRpsRoundAsync("상대와 연결이 끊겨 배팅 코인을 반환했습니다.");
                return;
            }
            if (invite.GameType == RpsGameEngine.GameTypeId)
            {
                return;
            }
            if (invite.GameType == TypingSprintEngine.GameTypeId)
            {
                if (_typingSessionId == invite.SessionId)
                {
                    await RefundTypingRoundAsync("상대와 연결이 끊겨 배팅 코인을 반환했습니다.");
                }
                return;
            }
            if (_ladderRoom?.SessionId != invite.SessionId)
            {
                return;
            }
            CloseLadderRoomState("상대와 연결이 끊겨 배팅 코인을 반환했습니다.");
            _progression.AddCoins(_profile, invite.WagerCoins);
            await PersistProfileAsync();
            GameNotification?.Invoke(this, "상대와 연결이 끊겨 배팅 코인을 반환했습니다.");
            return;
        }

        if (invite.GameType == RpsGameEngine.GameTypeId)
        {
            if (!IsRpsInMatch || _rpsSessionId != invite.SessionId)
            {
                return;
            }
            CanChooseRpsMove = true;
            RpsStatusText = $"{invite.SenderName}와 대결 시작! ({invite.WagerCoins} 코인) 아래에서 선택하세요!";
            SelectedGameIndex = 1;
            OpenGameTabRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (invite.GameType == TypingSprintEngine.GameTypeId)
        {
            SelectedGameIndex = 2;
            OpenGameTabRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (invite.GameType == LabLadderEngine.GameTypeId && _ladderRoom?.SessionId == invite.SessionId)
        {
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

    internal async Task HandleGameEventAsync(GameEventReceivedEventArgs e)
    {
        var p = e.Payload;
        if (!Guid.TryParseExact(p.SessionId, "N", out _))
        {
            return;
        }
        var friend = _social.Friends.FirstOrDefault(f => string.Equals(f.PetId, e.SenderPetId, StringComparison.OrdinalIgnoreCase));
        if (friend is null)
        {
            return;
        }

        var senderName = _social.GetFriendDisplayName(e.SenderPetId);

        if (p.Action == "invite")
        {
            var valid = p.GameType switch
            {
                RpsGameEngine.GameTypeId => !IsRpsInMatch && !IsRpsWaitingResponse && WagerOptions.Contains(p.WagerCoins),
                TypingSprintEngine.GameTypeId => !IsTypingRunning && !IsTypingWaitingResponse
                    && _typingSessionId is null && WagerOptions.Contains(p.WagerCoins),
                LabLadderEngine.GameTypeId => !IsLadderRoomOpen && LadderFeeOptions.Contains(p.WagerCoins),
                _ => false
            };
            if (!valid || IncomingInvite is not null || !TryRecordInvite(e.SenderPetId, p.SessionId))
            {
                return;
            }
            var invite = new IncomingGameInvite(
                e.SenderPetId,
                senderName,
                friend,
                p.GameType,
                p.SessionId,
                p.WagerCoins,
                p.Data ?? string.Empty);
            IncomingInvite = invite;
            return;
        }

        if (p.Action == "cancel" && IncomingInvite is not null &&
            IncomingInvite.SessionId == p.SessionId && IncomingInvite.GameType == p.GameType &&
            string.Equals(IncomingInvite.SenderPetId, e.SenderPetId, StringComparison.OrdinalIgnoreCase))
        {
            IncomingInvite = null;
            GameNotification?.Invoke(this, $"{senderName}(이)가 게임 초대를 취소했습니다.");
            return;
        }

        if (p.GameType == RpsGameEngine.GameTypeId && p.SessionId == _rpsSessionId && p.WagerCoins == _rpsActiveWager
            && string.Equals(_rpsOpponent?.PetId, e.SenderPetId, StringComparison.OrdinalIgnoreCase))
        {
            switch (p.Action)
            {
                case "accept" when IsRpsWaitingResponse:
                    CancelRpsInviteTimeout();
                    IsRpsWaitingResponse = false;
                    var (ok, msg) = _progression.TrySpendCoins(_profile, _rpsActiveWager);
                    if (!ok)
                    {
                        IsRpsWaitingResponse = false;
                        RpsStatusText = msg;
                        return;
                    }

                    BeginRpsRound(p.SessionId);
                    RpsStatusText = $"{senderName} 수락! ({_rpsActiveWager} 코인) 바위 · 가위 · 보 중 선택하세요!";
                    await PersistProfileAsync();
                    break;

                case "decline" when IsRpsWaitingResponse:
                    CancelRpsInviteTimeout();
                    IsRpsWaitingResponse = false;
                    IsRpsInMatch = false;
                    CanChooseRpsMove = false;
                    RpsStatusText = $"{senderName}(이)가 가위바위보 도전을 거절했어요.";
                    break;

                case "commit":
                case "reveal":
                case "cancel":
                    await ReceiveRpsRoundEventAsync(e.SenderPetId, p);
                    break;
            }
        }
        else if (p.GameType == TypingSprintEngine.GameTypeId && p.SessionId == _typingSessionId
            && p.WagerCoins == _typingActiveWager
            && string.Equals(_typingOpponent?.PetId, e.SenderPetId, StringComparison.OrdinalIgnoreCase))
        {
            switch (p.Action)
            {
                case "accept" when IsTypingWaitingResponse:
                    CancelTypingInviteTimeout();
                    IsTypingWaitingResponse = false;
                    var (ok, msg) = _progression.TrySpendCoins(_profile, _typingActiveWager);
                    if (!ok)
                    {
                        IsTypingWaitingResponse = false;
                        _typingSessionId = null;
                        TypingStatusText = msg;
                        return;
                    }

                    BeginTypingSprint(isSoloPractice: false, friend, _typingSessionId, _typingActiveWager);
                    await PersistProfileAsync();
                    break;

                case "decline" when IsTypingWaitingResponse:
                    CancelTypingInviteTimeout();
                    IsTypingWaitingResponse = false;
                    _typingSessionId = null;
                    TypingStatusText = $"{senderName}(이)가 타이핑 대결 도전을 거절했어요.";
                    break;

                case "progress" when IsTypingRunning && !_typingOpponentFinished:
                    if (int.TryParse(p.Data, out var liveScore) && liveScore >= 0 && liveScore <= TypingSprintEngine.MaxMatchScore)
                    {
                        TypingOpponentScore = liveScore;
                    }

                    break;

                case "finish" when !IsTypingWaitingResponse && !_typingOpponentFinished:
                    if (TypingSprintEngine.TryScoreTranscript(p.SessionId, p.Data, out var finalScore))
                    {
                        _typingOpponentFinalScore = finalScore;
                        _typingOpponentFinished = true;
                        TypingOpponentScore = finalScore;
                        await TryResolveTypingMatchAsync();
                    }

                    break;
            }
        }
        else if (p.GameType == LabLadderEngine.GameTypeId && !_ladderStarting && _ladderRoom is not null
            && p.SessionId == _ladderRoom.SessionId && p.WagerCoins == _ladderActiveWager)
        {
            await HandleLadderEventAsync(e.SenderPetId, senderName, friend, p);
        }
    }

    private bool TryRecordInvite(string senderId, string sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in _seenInvitations.Where(item => now >= item.Value).Select(item => item.Key).ToArray())
        {
            _seenInvitations.Remove(expired);
        }
        return _seenInvitations.Count < RecentInvitationCapacity &&
            _seenInvitations.TryAdd((senderId.ToUpperInvariant(), sessionId), now.AddMinutes(5));
    }

    private async Task PersistProfileAsync()
    {
        try
        {
            await _store.SaveProfileAsync(_profile);
            ProfileChanged?.Invoke(this, _profile);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError("게임 코인 저장 실패: {0}", ex);
            GameNotification?.Invoke(this, "게임 변경을 저장하지 못했습니다.");
        }
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
