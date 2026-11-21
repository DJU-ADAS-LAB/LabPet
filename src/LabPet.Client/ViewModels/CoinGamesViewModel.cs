using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Games;
using LabPet.Client.Rendering;
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

public sealed record LadderFeeChoice(int Coins, string Label)
{
    public override string ToString() => Label;
}

public sealed record LadderSlotOption(
    int ColumnIndex,
    string HeaderText,
    string OccupantText,
    bool IsOccupied,
    bool IsMine,
    bool IsSelectionEnabled = true,
    ImageSource? OccupantPortrait = null)
{
    public bool CanPick => IsSelectionEnabled && !IsOccupied;
}

public sealed record IncomingGameInvite(
    string SenderPetId,
    string SenderName,
    FriendEntry Friend,
    string GameType,
    string SessionId,
    int WagerCoins,
    string Data = "",
    string PrizeSummary = "",
    PetAppearance? SenderAppearance = null,
    DateTimeOffset ReceivedAtUtc = default,
    DateTimeOffset ExpiresAtUtc = default)
{
    public string GameDisplayName => GameType switch
    {
        RpsGameEngine.GameTypeId => "가위바위보",
        TypingSprintEngine.GameTypeId => "타이핑 대결",
        LabLadderEngine.GameTypeId => "연구실 사다리",
        _ => "코인 게임"
    };

    public string CostBadgeText => WagerCoins > 0 ? $"{WagerCoins} 코인" : "무료 (0코인)";

    public string SummaryText
    {
        get
        {
            if (GameType == LabLadderEngine.GameTypeId)
            {
                var feeText = WagerCoins > 0 ? $"{WagerCoins} 코인" : "무료 (0코인)";
                return string.IsNullOrWhiteSpace(PrizeSummary)
                    ? $"{SenderName}의 {GameDisplayName} 초대 ({feeText})"
                    : $"{SenderName}의 {GameDisplayName} 초대 ({feeText} · 상품: {PrizeSummary})";
            }

            return WagerCoins > 0
                ? $"{SenderName}의 {GameDisplayName} 초대 ({WagerCoins} 코인)"
                : $"{SenderName}의 {GameDisplayName} 초대";
        }
    }
}

/// <summary>
/// 4종 코인 게임(하이로우, 가위바위보, 타이핑 대결, 연구실 사다리) 통합 ViewModel.
/// </summary>
public sealed partial class CoinGamesViewModel : ObservableObject, IDisposable
{
    public const int InviteWaitTimeoutSeconds = 20;

    private readonly IAppDataStore _store;
    private readonly SoloProgressionService _progression;
    private readonly SocialCoordinatorService _social;
    private readonly PetPortraitRenderer? _portraitRenderer;

    private readonly CoinHiLowGame _hiLow = new();
    private readonly DispatcherTimer _typingTimer;
    private CancellationTokenSource? _rpsInviteTimeoutCts;
    private CancellationTokenSource? _typingInviteTimeoutCts;
    private CancellationTokenSource? _typingCountdownCts;
    private CancellationTokenSource? _incomingInviteCountdownCts;

    private PetProfile _profile = new();
    private bool _disposed;

    public event EventHandler<PetProfile>? ProfileChanged;
    public event EventHandler<IncomingGameInvite?>? IncomingInviteChanged;
    public event EventHandler<string>? GameNotification;
    public event EventHandler<bool>? PetEmotionTriggered; // true: 승리/기쁨, false: 패배/아쉬움
    public event EventHandler? OpenGameTabRequested;
    public event EventHandler<GameScreen>? OpenGameScreenRequested;
    public event EventHandler<LadderBoard>? LadderBoardGenerated;
    public event EventHandler? LadderAnimationCancelled;
    public event EventHandler? HiLowCardChanged;

    internal double InviteExpireSeconds { get; set; } = InviteWaitTimeoutSeconds;
    internal double TypingCountdownDurationSeconds { get; set; } = 3.0;
    internal double TypingSprintDurationSeconds { get; set; } = TypingSprintEngine.SprintDurationSeconds;

    public IReadOnlyList<int> WagerOptions { get; } = [10, 30, 50, 100];
    public IReadOnlyList<int> LadderFeeOptions { get; } = [0, 10, 30, 50];
    public IReadOnlyList<LadderFeeChoice> LadderFeeChoices { get; } =
    [
        new(0, "0코인 (무료)"),
        new(10, "10코인"),
        new(30, "30코인"),
        new(50, "50코인")
    ];

    public IReadOnlyList<GameCardDescriptor> GameCards { get; } =
    [
        new(GameScreen.HiLow, "하이로우", "다음 카드 숫자가 높을지 낮을지 맞혀 연속 배수를 노립니다.", "혼자 플레이", "코인 사용", "IconCardsGeometry"),
        new(GameScreen.Rps, "친구 가위바위보", "온라인 친구와 1:1 공정한 심리전을 겨루어 코인을 획득합니다.", "1:1 대전", "코인 사용", "IconHandScissorsGeometry"),
        new(GameScreen.Typing, "타이핑 대결", "10초 동안 제시된 문장을 빠르고 정확하게 입력해 대결합니다.", "혼자 / 1:1", "코인 / 연습", "IconKeyboardGeometry"),
        new(GameScreen.Ladder, "연구실 사다리", "참가자 확정과 위치 선택 후 투명하게 결과를 공개하는 사다리 추첨입니다.", "혼자 / 단체", "무료 / 코인", "IconLadderGeometry")
    ];

    public ObservableCollection<OnlineFriendChoice> OnlineFriends { get; } = [];
    public ObservableCollection<string> LadderResultLines { get; } = [];
    public ObservableCollection<LadderSlotOption> LadderSlots { get; } = [];
    public ObservableCollection<LadderFriendInviteOption> LadderInviteCandidates { get; } = [];
    public ObservableCollection<LadderParticipantCardItem> LadderParticipantCards { get; } = [];
    public ObservableCollection<LadderParticipantCardItem> LadderResultCards { get; } = [];

    // ── 공용 게임 내비게이션 및 보유 코인 ───────────────────────────────
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsOnGameDashboard), nameof(IsInActiveGameScreen), nameof(CurrentGameTitle))]
    private GameScreen _activeGameScreen = GameScreen.Dashboard;

    /// <summary>레거시 호환용 선택 게임 인덱스 (0: 하이로우, 1: 가위바위보, 2: 타이핑 대결, 3: 연구실 사다리).</summary>
    [ObservableProperty] private int _selectedGameIndex;

    public bool IsOnGameDashboard => ActiveGameScreen == GameScreen.Dashboard;
    public bool IsInActiveGameScreen => ActiveGameScreen != GameScreen.Dashboard;
    public bool CanNavigateBackFromGame => !IsTypingCountdown && !IsTypingRunning;
    public long CurrentCoins => _profile.SoloProgress.Coins;

    public string CurrentGameTitle => ActiveGameScreen switch
    {
        GameScreen.HiLow => "하이로우",
        GameScreen.Rps => "친구 가위바위보",
        GameScreen.Typing => "타이핑 대결",
        GameScreen.Ladder => "연구실 사다리",
        _ => "미니게임"
    };

    public ImageSource? MyPetPortrait => TryGetPortrait(_profile.Appearance);
    public string MyDisplayName => SocialCoordinatorService.FormatDisplayName(_profile.OwnerName, _profile.PetName);

    public bool IsHiLowInProgress => IsHiLowActive;
    public bool IsRpsInProgress => IsRpsWaitingResponse || IsRpsInMatch;
    public bool IsTypingInProgress => IsTypingWaitingResponse || IsTypingCountdown || IsTypingRunning || _typingSessionId is not null;
    public bool IsLadderInProgress => IsLadderRoomOpen || IsLadderAnimating;

    internal bool IsAnyGameBusy => IsHiLowActive
        || IsRpsWaitingResponse || IsRpsInMatch
        || IsTypingWaitingResponse || IsTypingCountdown || IsTypingRunning || _typingSessionId is not null
        || IsLadderRoomOpen || IsLadderAnimating;

    partial void OnActiveGameScreenChanged(GameScreen value)
    {
        if (value != GameScreen.Dashboard)
        {
            var idx = value switch
            {
                GameScreen.HiLow => 0,
                GameScreen.Rps => 1,
                GameScreen.Typing => 2,
                GameScreen.Ladder => 3,
                _ => SelectedGameIndex
            };
            if (SelectedGameIndex != idx)
            {
                SelectedGameIndex = idx;
            }
        }
    }

    [RelayCommand]
    private void OpenGameScreen(GameScreen screen)
    {
        if (screen == GameScreen.Dashboard)
        {
            NavigateToDashboard();
            return;
        }

        ActiveGameScreen = screen;
    }

    [RelayCommand]
    private void NavigateToDashboard()
    {
        if (!CanNavigateBackFromGame)
        {
            return;
        }

        ActiveGameScreen = GameScreen.Dashboard;
    }

    // ── 수신된 게임 초대 및 상태 머신 ────────────────────────────────────
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIncomingInvite), nameof(IncomingInviteText), nameof(CanRespondToIncomingInvite))]
    private IncomingGameInvite? _incomingInvite;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIncomingInvite), nameof(CanRespondToIncomingInvite), nameof(IsAcceptingIncomingInvite))]
    private GameInviteStatus _incomingInviteStatus = GameInviteStatus.None;

    [ObservableProperty] private int _incomingInviteRemainingSeconds;
    [ObservableProperty] private double _incomingInviteProgressRatio = 1.0;
    [ObservableProperty] private ImageSource? _incomingInvitePortrait;
    [ObservableProperty] private string _incomingInviteFeedbackMessage = string.Empty;

    public bool HasIncomingInvite => IncomingInvite is not null
        && IncomingInviteStatus is GameInviteStatus.Pending or GameInviteStatus.Accepting;

    public bool IsAcceptingIncomingInvite => IncomingInviteStatus == GameInviteStatus.Accepting;
    public bool CanRespondToIncomingInvite => IncomingInvite is not null && IncomingInviteStatus == GameInviteStatus.Pending;
    public string IncomingInviteText => IncomingInvite?.SummaryText ?? string.Empty;

    partial void OnIncomingInviteChanged(IncomingGameInvite? value)
        => IncomingInviteChanged?.Invoke(this, value);

    // ── 1. 코인 하이로우 (솔로) ───────────────────────────────────────────
    [ObservableProperty] private int _hiLowWager = 30;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsHiLowInProgress))]
    private bool _isHiLowActive;
    [ObservableProperty] private HiLowPhase _hiLowPhase = HiLowPhase.Idle;
    [ObservableProperty] private HiLowResultKind _hiLowResultKind = HiLowResultKind.None;
    [ObservableProperty] private int _hiLowCurrentCardValue;
    [ObservableProperty] private int _hiLowPreviousCardValue;
    [ObservableProperty] private string _hiLowCardRankText = "?";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HiLowIsRedSuit))]
    private int _hiLowCardSuitIndex;
    [ObservableProperty] private string _hiLowLastFeedbackTag = string.Empty;
    [ObservableProperty] private long _hiLowLastPayoutAmount;
    [ObservableProperty] private int _hiLowActiveWager;
    [ObservableProperty] private string _hiLowCardDisplay = "?";
    [ObservableProperty] private string _hiLowStreakText = "0연승";
    [ObservableProperty] private string _hiLowMultiplierText = "×1.0";
    [ObservableProperty] private string _hiLowPotentialPayoutText = "0 코인";
    [ObservableProperty] private string _hiLowStatusMessage = "배팅 코인을 고르고 시작을 눌러 주세요. (카드 범위: 1 ~ 13)";

    public bool HiLowIsRedSuit => HiLowCardSuitIndex is 1 or 2;
    public bool CanCashOutHiLow => IsHiLowActive && _hiLow.Streak > 0;

    // ── 2. 친구 가위바위보 (LAN P2P) ──────────────────────────────────────
    [ObservableProperty] private OnlineFriendChoice? _selectedRpsFriend;
    [ObservableProperty] private int _rpsWager = 30;
    [ObservableProperty] private string _rpsStatusText = "온라인 친구를 선택해 가위바위보 코인 배틀을 신청해 보세요!";
    [ObservableProperty] private string _rpsMyMoveText = "?";
    [ObservableProperty] private string _rpsOpponentMoveText = "?";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RpsInviteButtonText), nameof(IsRpsInProgress))]
    private bool _isRpsWaitingResponse;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsRpsInProgress))]
    private bool _isRpsInMatch;
    [ObservableProperty] private bool _canChooseRpsMove;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsRpsSetupOrWaiting), nameof(IsRpsArenaVisible))]
    private RpsPhase _rpsPhase = RpsPhase.Setup;
    [ObservableProperty] private RpsMove? _rpsMySelectedMove;
    [ObservableProperty] private RpsMove? _rpsRevealedOpponentMove;
    [ObservableProperty] private bool _rpsOpponentCommitted;
    [ObservableProperty] private RpsOutcome? _rpsLastOutcome;
    [ObservableProperty] private string _rpsOpponentDisplayName = "상대 친구";
    [ObservableProperty] private ImageSource? _rpsOpponentPortrait;
    [ObservableProperty] private string _rpsSettlementText = string.Empty;
    [ObservableProperty] private int _rpsCurrentMatchWager;

    public bool IsRpsSetupOrWaiting => RpsPhase is RpsPhase.Setup or RpsPhase.WaitingInviteResponse;
    public bool IsRpsArenaVisible => RpsPhase is RpsPhase.Choosing or RpsPhase.WaitingOpponent or RpsPhase.Result;
    public string RpsInviteButtonText => IsRpsWaitingResponse ? "도전 취소" : "도전 보내기";

    private string? _rpsSessionId;
    private FriendEntry? _rpsOpponent;
    private int _rpsActiveWager;
    private RpsMove? _rpsMyMove;
    private RpsMove? _rpsOpponentMove;

    // ── 3. 친구 타이핑 스프린트 (LAN P2P) ─────────────────────────────────
    [ObservableProperty] private OnlineFriendChoice? _selectedTypingFriend;
    [ObservableProperty] private int _typingWager = 30;
    [ObservableProperty] private string _typingStatusText = "온라인 친구와 10초 타이핑 대결을 펼쳐 보세요!";
    [ObservableProperty] private string _typingPromptText = "대결이 시작되면 문장이 표시됩니다.";
    [ObservableProperty] private string _typingInputText = string.Empty;
    [ObservableProperty] private int _typingMyScore;
    [ObservableProperty] private int _typingOpponentScore;
    [ObservableProperty] private string _typingTimeLeftText = "10.0초";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TypingInviteButtonText), nameof(IsTypingInProgress))]
    private bool _isTypingWaitingResponse;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsTypingInProgress), nameof(CanNavigateBackFromGame))]
    private bool _isTypingRunning;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsTypingCountdown), nameof(IsTypingSetupOrWaiting), nameof(IsTypingArenaVisible), nameof(IsTypingInProgress), nameof(CanNavigateBackFromGame))]
    private TypingPhase _typingPhase = TypingPhase.Setup;
    [ObservableProperty] private int _typingCountdownValue;
    [ObservableProperty] private bool _isTypingSoloMode;
    [ObservableProperty] private double _typingTimeProgress = 1.0;
    [ObservableProperty] private int _typingCompletedCount;
    [ObservableProperty] private int _typingCurrentMatchWager;
    [ObservableProperty] private string _typingOpponentDisplayName = "상대 친구";
    [ObservableProperty] private ImageSource? _typingOpponentPortrait;
    [ObservableProperty] private string _typingResultHeadline = string.Empty;
    [ObservableProperty] private string _typingSettlementText = string.Empty;

    public bool IsTypingCountdown => TypingPhase == TypingPhase.Countdown;
    public bool IsTypingSetupOrWaiting => TypingPhase is TypingPhase.Setup or TypingPhase.WaitingInviteResponse;
    public bool IsTypingArenaVisible => TypingPhase is TypingPhase.Countdown or TypingPhase.Running or TypingPhase.WaitingOpponent or TypingPhase.Result;
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
    [ObservableProperty] private string _ladderPrizesText = "커피 쏘기 x1, 통과 x2";
    [ObservableProperty] private int _ladderEntryFee;
    [ObservableProperty] private string _ladderStatusText = "상품과 참가비를 설정한 뒤 혼자 돌리거나 친구 초대 방을 여세요!";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditLadderSetup), nameof(CanOpenLadderRoom), nameof(IsLadderInProgress))]
    private bool _isLadderRoomOpen;
    [ObservableProperty] private bool _isLadderHost;
    [ObservableProperty] private bool _isLadderWaitingInvites;
    [ObservableProperty] private bool _isLadderPickingPhase;
    [ObservableProperty] private bool _canConfirmLadderParticipants;
    [ObservableProperty] private bool _showConfirmLadderButton;
    [ObservableProperty] private bool _canStartLadderGame;
    [ObservableProperty] private bool _showStartLadderButton;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditLadderSetup), nameof(CanOpenLadderRoom), nameof(CanShareLadderResult), nameof(IsLadderInProgress))]
    private bool _isLadderAnimating;
    [ObservableProperty] private LadderStage _ladderStage = LadderStage.Setup;
    [ObservableProperty] private bool _isLadderMultiplayerMode = true;
    [ObservableProperty] private string _ladderLobbySummaryText = string.Empty;
    [ObservableProperty] private string _ladderRoomPrizesText = string.Empty;
    [ObservableProperty] private string _ladderRoomMembersText = string.Empty;
    [ObservableProperty] private string _ladderBoardPrizesSummaryText = string.Empty;
    [ObservableProperty] private string _ladderResultHeadline = string.Empty;
    [ObservableProperty] private string _ladderResultSettlementText = string.Empty;
    [ObservableProperty] private string _ladderMyResultPrizeText = string.Empty;

    public bool CanEditLadderSetup => !IsLadderRoomOpen && !IsLadderAnimating;
    public bool CanOpenLadderRoom => HasOnlineFriends && SelectedLadderInviteeCount > 0 && !IsLadderRoomOpen && !IsLadderAnimating;
    public bool CanShareLadderResult => !IsLadderAnimating && _lastLadderResults.Count > 0;
    public int SelectedLadderInviteeCount => LadderInviteCandidates.Count(c => c.IsSelected);
    public LadderBoard? ActiveLadderBoard => _pendingLadderRun?.Board ?? _lastLadderBoard;
    public double ActiveLadderAnimationProgress => _pendingLadderRun?.AnimationProgress ?? 0.0;
    public IReadOnlyList<bool> ActiveLadderRevealedColumns => _pendingLadderRun?.RevealedColumns ?? [];
    public IReadOnlyList<ImageSource?> ActiveLadderParticipantPortraits => _pendingLadderRun?.ParticipantPortraits ?? [];

    private LadderBoard? _lastLadderBoard;
    private IReadOnlyList<LadderTraceResult> _lastLadderResults = [];
    private LadderRoomStateDto? _ladderRoom;
    private FriendEntry? _ladderHostFriend;
    private readonly List<string> _ladderInitialPrizes = [];
    private readonly HashSet<string> _ladderPendingInviteeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FriendEntry> _ladderPendingInvitees = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FriendEntry> _ladderJoinedFriends = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _startedLadderSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _settledLadderSessions = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _ladderInviteTimeoutCts;
    private int _ladderActiveWager;
    private int _ladderPaidCoins;
    private bool _ladderStarting;
    private PendingLadderRun? _pendingLadderRun;
    private readonly Dictionary<(string Sender, string Session), DateTimeOffset> _seenInvitations = [];
    private readonly HashSet<string> _canceledInviteSessions = new(StringComparer.OrdinalIgnoreCase);
    private const int RecentInvitationCapacity = 256;

    public bool HasOnlineFriends => OnlineFriends.Count > 0;

    private sealed class PendingLadderRun
    {
        public required string SessionId { get; init; }
        public required LadderBoard Board { get; init; }
        public required IReadOnlyList<LadderTraceResult> Traces { get; init; }
        public required IReadOnlyList<string> ParticipantPetIds { get; init; }
        public required IReadOnlyList<ImageSource?> ParticipantPortraits { get; init; }
        public required int MyStartColumn { get; init; }
        public required int WagerCoins { get; init; }
        public required bool IsMultiplayer { get; init; }
        public FriendEntry? HostFriend { get; init; }
        public required int AbortRefundCoins { get; set; }
        public required bool[] RevealedColumns { get; init; }
        public double AnimationProgress { get; set; }
        public bool IsCompleted { get; set; }
    }

    public CoinGamesViewModel(
        IAppDataStore store,
        SoloProgressionService progression,
        SocialCoordinatorService social,
        PetPortraitRenderer? portraitRenderer = null)
    {
        _store = store;
        _progression = progression;
        _social = social;
        _portraitRenderer = portraitRenderer;

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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _social.StateChanged -= OnSocialStateChanged;
        _social.GameEventReceived -= OnSocialGameEvent;
        _typingTimer.Stop();
        _typingTimer.Tick -= OnTypingTimerTick;
        CancelRpsInviteTimeout();
        CancelTypingInviteTimeout();
        CancelTypingCountdown();
        CancelLadderInviteTimeout();
        CancelIncomingInviteCountdown();
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

        NotifyCoinsChanged();
        OnPropertyChanged(nameof(MyPetPortrait));
        OnPropertyChanged(nameof(MyDisplayName));
    }

    public void RefreshOnlineFriends()
    {
        var prevRpsId = SelectedRpsFriend?.Friend.PetId;
        var prevTypingId = SelectedTypingFriend?.Friend.PetId;
        var previousSelection = LadderInviteCandidates.ToDictionary(c => c.Friend.PetId, c => c.IsSelected, StringComparer.OrdinalIgnoreCase);

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

        foreach (var oldCandidate in LadderInviteCandidates)
        {
            oldCandidate.PropertyChanged -= OnLadderInviteCandidatePropertyChanged;
        }

        LadderInviteCandidates.Clear();
        foreach (var item in list)
        {
            var selected = previousSelection.TryGetValue(item.Friend.PetId, out var prev) ? prev : true;
            var appearance = ResolveFriendAppearance(item.Friend.PetId, item.Friend);
            var candidate = new LadderFriendInviteOption(item.Friend, item.DisplayName, TryGetPortrait(appearance), selected);
            candidate.PropertyChanged += OnLadderInviteCandidatePropertyChanged;
            LadderInviteCandidates.Add(candidate);
        }

        SelectedRpsFriend = OnlineFriends.FirstOrDefault(x => string.Equals(x.Friend.PetId, prevRpsId, StringComparison.OrdinalIgnoreCase))
            ?? OnlineFriends.FirstOrDefault();
        SelectedTypingFriend = OnlineFriends.FirstOrDefault(x => string.Equals(x.Friend.PetId, prevTypingId, StringComparison.OrdinalIgnoreCase))
            ?? OnlineFriends.FirstOrDefault();

        OnPropertyChanged(nameof(HasOnlineFriends));
        OnPropertyChanged(nameof(SelectedLadderInviteeCount));
        OnPropertyChanged(nameof(CanOpenLadderRoom));
    }

    private void OnLadderInviteCandidatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LadderFriendInviteOption.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedLadderInviteeCount));
            OnPropertyChanged(nameof(CanOpenLadderRoom));
        }
    }

    private ImageSource? TryGetPortrait(PetAppearance? appearance)
    {
        if (_portraitRenderer is null || appearance is null)
        {
            return null;
        }

        if (Application.Current?.Dispatcher is { } appDispatcher)
        {
            if (!appDispatcher.CheckAccess())
            {
                return null;
            }
        }
        else if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            return null;
        }

        return _portraitRenderer.GetPortrait(appearance);
    }

    private PetAppearance? ResolveFriendAppearance(string petId, FriendEntry? fallbackEntry = null)
    {
        var live = _social.DiscoveredPeers.FirstOrDefault(p => string.Equals(p.PetId, petId, StringComparison.OrdinalIgnoreCase));
        if (live?.Appearance is { } liveApp)
        {
            return liveApp;
        }

        if (fallbackEntry?.CachedAppearance is { } cachedApp)
        {
            return cachedApp;
        }

        return _social.Friends.FirstOrDefault(f => string.Equals(f.PetId, petId, StringComparison.OrdinalIgnoreCase))?.CachedAppearance;
    }

    private void NotifyCoinsChanged()
        => OnPropertyChanged(nameof(CurrentCoins));

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

        NotifyCoinsChanged();
        await PersistProfileAsync();

        var card = _hiLow.StartRound(HiLowWager);
        HiLowActiveWager = HiLowWager;
        HiLowPreviousCardValue = 0;
        HiLowLastPayoutAmount = 0;
        HiLowResultKind = HiLowResultKind.None;
        HiLowLastFeedbackTag = "진행 중";
        HiLowPhase = HiLowPhase.Playing;
        IsHiLowActive = true;
        UpdateHiLowDisplay(card, $"첫 카드는 {CoinHiLowGame.FormatCardLabel(card)}! 다음 카드가 높을지 낮을지 선택하세요.");
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
                HiLowPreviousCardValue = result.PreviousCard;
                HiLowLastFeedbackTag = $"{result.Streak}연승 성공";
                UpdateHiLowDisplay(result.NextCard,
                    $"정답! {CoinHiLowGame.FormatCardLabel(result.PreviousCard)} → {CoinHiLowGame.FormatCardLabel(result.NextCard)} ({result.Streak}연승 · 예상 {result.PotentialPayout} 코인)");
                PetEmotionTriggered?.Invoke(this, true);
                break;

            case HiLowGuessResult.Push:
                HiLowPreviousCardValue = result.PreviousCard;
                HiLowLastFeedbackTag = "무승부";
                UpdateHiLowDisplay(result.NextCard,
                    $"같은 숫자({CoinHiLowGame.FormatCardLabel(result.NextCard)})! 무승부로 계속 진행합니다.");
                break;

            case HiLowGuessResult.Wrong:
                HiLowPreviousCardValue = result.PreviousCard;
                IsHiLowActive = false;
                HiLowLastPayoutAmount = 0;
                HiLowLastFeedbackTag = "아쉬운 실패";
                HiLowResultKind = HiLowResultKind.Lost;
                HiLowPhase = HiLowPhase.Result;
                UpdateHiLowDisplay(result.NextCard,
                    $"{CoinHiLowGame.FormatCardLabel(result.PreviousCard)} → {CoinHiLowGame.FormatCardLabel(result.NextCard)} · 배팅 {HiLowActiveWager} 코인을 잃었습니다.");
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
        NotifyCoinsChanged();
        await PersistProfileAsync();

        HiLowLastPayoutAmount = payout;
        HiLowLastFeedbackTag = "정산 완료";
        HiLowResultKind = HiLowResultKind.CashedOut;
        HiLowPhase = HiLowPhase.Result;
        HiLowPotentialPayoutText = "0 코인";
        OnPropertyChanged(nameof(CanCashOutHiLow));
        HiLowStatusMessage = $"정산 완료! +{payout} 코인을 획득했습니다.";
        PetEmotionTriggered?.Invoke(this, true);
        GameNotification?.Invoke(this, $"하이로우 정산 +{payout} 코인!");
    }

    [RelayCommand]
    private void ResetHiLowToSetup()
    {
        if (IsHiLowActive)
        {
            return;
        }

        HiLowPhase = HiLowPhase.Idle;
        HiLowResultKind = HiLowResultKind.None;
        HiLowCurrentCardValue = 0;
        HiLowPreviousCardValue = 0;
        HiLowCardRankText = "?";
        HiLowCardDisplay = "?";
        HiLowStreakText = "0연승";
        HiLowMultiplierText = "×1.0";
        HiLowPotentialPayoutText = "0 코인";
        HiLowLastFeedbackTag = string.Empty;
        HiLowLastPayoutAmount = 0;
        HiLowStatusMessage = "배팅 코인을 고르고 시작을 눌러 주세요. (카드 범위: 1 ~ 13)";
    }

    private void UpdateHiLowDisplay(int card, string message)
    {
        HiLowCurrentCardValue = card;
        HiLowCardRankText = card switch
        {
            1 => "A",
            11 => "J",
            12 => "Q",
            13 => "K",
            _ => card.ToString()
        };
        HiLowCardSuitIndex = (card + _hiLow.Streak) % 4;
        HiLowCardDisplay = CoinHiLowGame.FormatCardLabel(card);
        HiLowStreakText = $"{_hiLow.Streak}연승";
        HiLowMultiplierText = $"×{_hiLow.CurrentMultiplier:0.0}";
        HiLowPotentialPayoutText = $"{_hiLow.PotentialPayout} 코인";
        HiLowStatusMessage = message;
        OnPropertyChanged(nameof(CanCashOutHiLow));
        HiLowCardChanged?.Invoke(this, EventArgs.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════
    // 2. 친구 가위바위보 배틀 초대 송수신
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
        RpsPhase = RpsPhase.WaitingInviteResponse;
        RpsOpponentDisplayName = target.DisplayName;
        RpsOpponentPortrait = TryGetPortrait(ResolveFriendAppearance(target.Friend.PetId, target.Friend));
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
                RpsPhase = RpsPhase.Setup;
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
        RpsPhase = RpsPhase.Setup;
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
    // 공용 초대 수락 / 거절 / 만료 상태 머신
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    public async Task AcceptIncomingInviteAsync()
    {
        var invite = IncomingInvite;
        if (invite is null || IncomingInviteStatus != GameInviteStatus.Pending)
        {
            return;
        }

        if (_canceledInviteSessions.Contains(invite.SessionId) || DateTimeOffset.UtcNow > invite.ExpiresAtUtc && invite.ExpiresAtUtc != default)
        {
            CancelIncomingInviteCountdown();
            var expired = invite.ExpiresAtUtc != default && DateTimeOffset.UtcNow > invite.ExpiresAtUtc;
            IncomingInviteStatus = expired ? GameInviteStatus.Expired : GameInviteStatus.None;
            IncomingInvite = null;
            IncomingInviteFeedbackMessage = expired ? "수락 제한 시간이 만료되었습니다." : "이미 취소된 초대입니다.";
            return;
        }

        if ((invite.GameType == RpsGameEngine.GameTypeId && (IsRpsInMatch || IsRpsWaitingResponse))
            || (invite.GameType == TypingSprintEngine.GameTypeId && (IsTypingRunning || IsTypingCountdown || IsTypingWaitingResponse || _typingSessionId is not null))
            || (invite.GameType == LabLadderEngine.GameTypeId && (IsLadderRoomOpen || IsLadderAnimating)))
        {
            IncomingInviteFeedbackMessage = "이미 진행 중인 게임이 있어 초대를 수락할 수 없습니다.";
            await DeclineIncomingInviteAsync();
            return;
        }

        LadderRoomStateDto? initialRoom = null;
        if (invite.GameType == LabLadderEngine.GameTypeId
            && (!TryReadLadderPayload(invite.Data, out initialRoom) || initialRoom is null
                || initialRoom.Phase != LadderRoomPhase.WaitingInvites
                || !LabLadderEngine.IsValidRoom(initialRoom, invite.SessionId, invite.SenderPetId, invite.WagerCoins)))
        {
            IncomingInviteFeedbackMessage = "유효하지 않은 사다리 초대 정보입니다.";
            await DeclineIncomingInviteAsync();
            return;
        }

        // 중복 클릭 및 만료 타이머 차단
        CancelIncomingInviteCountdown();
        IncomingInviteStatus = GameInviteStatus.Accepting;
        IncomingInviteFeedbackMessage = string.Empty;

        if (invite.WagerCoins > 0)
        {
            var (ok, msg) = _progression.TrySpendCoins(_profile, invite.WagerCoins);
            if (!ok)
            {
                IncomingInviteFeedbackMessage = $"도전 수락 실패: {msg}";
                GameNotification?.Invoke(this, $"도전 수락 실패: {msg}");
                IncomingInviteStatus = GameInviteStatus.Declined;
                IncomingInvite = null;
                _ = await _social.SendGameEventAsync(invite.Friend, new GameEventPayload
                {
                    GameType = invite.GameType,
                    SessionId = invite.SessionId,
                    Action = "decline",
                    WagerCoins = invite.WagerCoins
                });
                return;
            }

            NotifyCoinsChanged();
        }

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
            _ladderPaidCoins = invite.WagerCoins;
            _ladderInitialPrizes.Clear();
            _ladderInitialPrizes.AddRange(initialRoom.Prizes);
            _ladderRoom = initialRoom;
            IsLadderHost = false;
            IsLadderRoomOpen = true;
            IsLadderMultiplayerMode = true;
            LadderStage = LadderStage.Lobby;
            LadderResultLines.Clear();
            LadderResultCards.Clear();
            RefreshLadderLobbyUi();
        }

        if (invite.WagerCoins > 0)
        {
            await PersistProfileAsync();
        }

        var sent = await _social.SendGameEventAsync(invite.Friend, new GameEventPayload
        {
            GameType = invite.GameType,
            SessionId = invite.SessionId,
            Action = "accept",
            WagerCoins = invite.WagerCoins
        });

        var canceledMidFlight = _canceledInviteSessions.Contains(invite.SessionId);
        if (!sent || canceledMidFlight)
        {
            await RollbackFailedOrCanceledInviteAcceptAsync(invite, canceledMidFlight);
            return;
        }

        IncomingInviteStatus = GameInviteStatus.Accepted;
        IncomingInvite = null;

        GameScreen targetScreen;
        if (invite.GameType == RpsGameEngine.GameTypeId)
        {
            if (!IsRpsInMatch || _rpsSessionId != invite.SessionId)
            {
                return;
            }

            CanChooseRpsMove = true;
            RpsPhase = RpsPhase.Choosing;
            RpsStatusText = $"{invite.SenderName}와 대결 시작! ({invite.WagerCoins} 코인) 아래에서 선택하세요!";
            targetScreen = GameScreen.Rps;
        }
        else if (invite.GameType == TypingSprintEngine.GameTypeId)
        {
            targetScreen = GameScreen.Typing;
        }
        else
        {
            if (_ladderRoom?.SessionId != invite.SessionId)
            {
                return;
            }

            RefreshLadderLobbyUi();
            if (_ladderRoom.Phase == LadderRoomPhase.WaitingInvites)
            {
                LadderStatusText = $"{invite.SenderName}의 사다리방에 입장했습니다. 방장이 참가자를 확정한 뒤 위치를 선택할 수 있습니다.";
            }

            targetScreen = GameScreen.Ladder;
        }

        ActiveGameScreen = targetScreen;
        OpenGameScreenRequested?.Invoke(this, targetScreen);
        OpenGameTabRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task RollbackFailedOrCanceledInviteAcceptAsync(IncomingGameInvite invite, bool canceledMidFlight)
    {
        var failMessage = canceledMidFlight
            ? "상대가 초대를 이미 취소해 참가를 되돌렸습니다."
            : "상대와 연결이 끊겨 초대를 수락하지 못했습니다.";

        if (invite.GameType == RpsGameEngine.GameTypeId && _rpsSessionId == invite.SessionId)
        {
            await RefundRpsRoundAsync(failMessage);
        }
        else if (invite.GameType == TypingSprintEngine.GameTypeId && _typingSessionId == invite.SessionId)
        {
            await RefundTypingRoundAsync(failMessage);
        }
        else if (invite.GameType == LabLadderEngine.GameTypeId && _ladderRoom?.SessionId == invite.SessionId)
        {
            var refunded = await RefundLadderEntryFeeIfPaidAsync();
            var ladderMsg = refunded > 0 ? $"{failMessage} (참가 코인 반환)" : failMessage;
            CloseLadderRoomState(ladderMsg);
        }

        IncomingInviteStatus = GameInviteStatus.None;
        IncomingInvite = null;
        IncomingInviteFeedbackMessage = failMessage;
        GameNotification?.Invoke(this, failMessage);
    }

    [RelayCommand]
    public async Task DeclineIncomingInviteAsync()
    {
        var invite = IncomingInvite;
        if (invite is null || IncomingInviteStatus != GameInviteStatus.Pending)
        {
            return;
        }

        CancelIncomingInviteCountdown();
        IncomingInviteStatus = GameInviteStatus.Declined;
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
            if (_canceledInviteSessions.Contains(p.SessionId))
            {
                return;
            }

            LadderRoomStateDto? ladderInviteRoom = null;
            var payloadValid = p.GameType switch
            {
                RpsGameEngine.GameTypeId => WagerOptions.Contains(p.WagerCoins),
                TypingSprintEngine.GameTypeId => WagerOptions.Contains(p.WagerCoins),
                LabLadderEngine.GameTypeId => LadderFeeOptions.Contains(p.WagerCoins)
                    && TryReadLadderPayload(p.Data, out ladderInviteRoom) && ladderInviteRoom is not null
                    && ladderInviteRoom.Phase == LadderRoomPhase.WaitingInvites
                    && LabLadderEngine.IsValidRoom(ladderInviteRoom, p.SessionId, e.SenderPetId, p.WagerCoins),
                _ => false
            };
            if (!payloadValid || !TryRecordInvite(e.SenderPetId, p.SessionId))
            {
                return;
            }

            // 다른 게임이 실행 중이거나 대기 중인 초대가 이미 있으면 현재 게임을 방해하지 않고 조용히 거절한다.
            if (IsAnyGameBusy || HasIncomingInvite)
            {
                _ = await _social.SendGameEventAsync(friend, new GameEventPayload
                {
                    GameType = p.GameType,
                    SessionId = p.SessionId,
                    Action = "decline",
                    WagerCoins = p.WagerCoins
                });
                return;
            }

            var prizeSummary = ladderInviteRoom is not null
                ? LabLadderEngine.FormatPrizeSummary(ladderInviteRoom.Prizes)
                : string.Empty;

            var totalSeconds = Math.Max(0.2, InviteExpireSeconds);
            var now = DateTimeOffset.UtcNow;
            var expiresAt = now.AddSeconds(totalSeconds);
            var senderAppearance = ResolveFriendAppearance(e.SenderPetId, friend);

            var invite = new IncomingGameInvite(
                e.SenderPetId,
                senderName,
                friend,
                p.GameType,
                p.SessionId,
                p.WagerCoins,
                p.Data ?? string.Empty,
                prizeSummary,
                senderAppearance,
                now,
                expiresAt);

            IncomingInvitePortrait = TryGetPortrait(senderAppearance);
            IncomingInviteFeedbackMessage = string.Empty;
            IncomingInviteRemainingSeconds = Math.Max(1, (int)Math.Ceiling(totalSeconds));
            IncomingInviteProgressRatio = 1.0;
            IncomingInviteStatus = GameInviteStatus.Pending;
            IncomingInvite = invite;
            StartIncomingInviteCountdown(invite);
            return;
        }

        if (p.Action == "cancel")
        {
            RecordCanceledInviteSession(p.SessionId);
            if (IncomingInvite is not null &&
                IncomingInvite.SessionId == p.SessionId && IncomingInvite.GameType == p.GameType &&
                string.Equals(IncomingInvite.SenderPetId, e.SenderPetId, StringComparison.OrdinalIgnoreCase))
            {
                var wasAccepting = IncomingInviteStatus == GameInviteStatus.Accepting;
                CancelIncomingInviteCountdown();
                IncomingInviteStatus = GameInviteStatus.None;
                IncomingInvite = null;
                if (!wasAccepting)
                {
                    IncomingInviteFeedbackMessage = $"{senderName}(이)가 게임 초대를 취소했습니다.";
                    GameNotification?.Invoke(this, $"{senderName}(이)가 게임 초대를 취소했습니다.");
                }

                return;
            }
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
                        RpsPhase = RpsPhase.Setup;
                        RpsStatusText = msg;
                        return;
                    }

                    NotifyCoinsChanged();
                    BeginRpsRound(p.SessionId);
                    RpsStatusText = $"{senderName} 수락! ({_rpsActiveWager} 코인) 가위 · 바위 · 보 중 선택하세요!";
                    await PersistProfileAsync();
                    break;

                case "decline" when IsRpsWaitingResponse:
                    CancelRpsInviteTimeout();
                    IsRpsWaitingResponse = false;
                    IsRpsInMatch = false;
                    CanChooseRpsMove = false;
                    _rpsSessionId = null;
                    RpsPhase = RpsPhase.Setup;
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
                        _typingSessionId = null;
                        TypingPhase = TypingPhase.Setup;
                        TypingStatusText = msg;
                        return;
                    }

                    NotifyCoinsChanged();
                    BeginTypingSprint(isSoloPractice: false, friend, _typingSessionId, _typingActiveWager);
                    await PersistProfileAsync();
                    break;

                case "decline" when IsTypingWaitingResponse:
                    CancelTypingInviteTimeout();
                    IsTypingWaitingResponse = false;
                    _typingSessionId = null;
                    TypingPhase = TypingPhase.Setup;
                    TypingStatusText = $"{senderName}(이)가 타이핑 대결 도전을 거절했어요.";
                    break;

                case "progress" when !IsTypingWaitingResponse && !_typingOpponentFinished:
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
        else if (p.GameType == LabLadderEngine.GameTypeId)
        {
            if (p.Action == "cancel"
                && _pendingLadderRun is { IsCompleted: false, IsMultiplayer: true } activeRun
                && activeRun.SessionId == p.SessionId
                && p.WagerCoins == activeRun.WagerCoins
                && string.Equals(activeRun.HostFriend?.PetId, e.SenderPetId, StringComparison.OrdinalIgnoreCase))
            {
                await AbortActiveLadderAnimationAsync($"{senderName}(이)가 사다리 시작 중 연결 오류로 게임을 취소했습니다.");
            }
            else if (IsLadderHost && p.Action == "accept"
                && _ladderRoom is not null && p.SessionId == _ladderRoom.SessionId
                && _ladderRoom.Phase != LadderRoomPhase.WaitingInvites)
            {
                _ = await _social.SendGameEventAsync(friend, new GameEventPayload
                {
                    GameType = LabLadderEngine.GameTypeId,
                    SessionId = p.SessionId,
                    Action = "cancel",
                    WagerCoins = _ladderActiveWager
                });
            }
            else if (_ladderRoom is not null && p.SessionId == _ladderRoom.SessionId && p.WagerCoins == _ladderActiveWager
                && (!_ladderStarting || p.Action is "decline" or "cancel"))
            {
                await HandleLadderEventAsync(e.SenderPetId, senderName, friend, p);
            }
        }
    }

    private void StartIncomingInviteCountdown(IncomingGameInvite invite)
    {
        CancelIncomingInviteCountdown();
        var cts = new CancellationTokenSource();
        _incomingInviteCountdownCts = cts;
        var totalSeconds = Math.Max(0.2, (invite.ExpiresAtUtc - invite.ReceivedAtUtc).TotalSeconds);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var delayMs = totalSeconds < 1.5 ? 50 : 250;
                    await Task.Delay(delayMs, cts.Token);
                    if (cts.Token.IsCancellationRequested)
                    {
                        break;
                    }

                    var remaining = (invite.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds;
                    if (remaining <= 0)
                    {
                        RunOnUi(() => _ = ExpireIncomingInviteAsync(invite.SessionId));
                        break;
                    }

                    var sec = Math.Max(1, (int)Math.Ceiling(remaining));
                    var ratio = Math.Clamp(remaining / totalSeconds, 0.0, 1.0);
                    RunOnUi(() =>
                    {
                        if (IncomingInvite?.SessionId == invite.SessionId && IncomingInviteStatus == GameInviteStatus.Pending)
                        {
                            if (IncomingInviteRemainingSeconds != sec)
                            {
                                IncomingInviteRemainingSeconds = sec;
                            }

                            IncomingInviteProgressRatio = ratio;
                        }
                    });
                }
            }
            catch (TaskCanceledException)
            {
            }
        }, cts.Token);
    }

    private void CancelIncomingInviteCountdown()
    {
        _incomingInviteCountdownCts?.Cancel();
        _incomingInviteCountdownCts?.Dispose();
        _incomingInviteCountdownCts = null;
    }

    private async Task ExpireIncomingInviteAsync(string sessionId)
    {
        if (IncomingInvite is not { } invite
            || invite.SessionId != sessionId
            || IncomingInviteStatus != GameInviteStatus.Pending)
        {
            return;
        }

        CancelIncomingInviteCountdown();
        IncomingInviteRemainingSeconds = 0;
        IncomingInviteProgressRatio = 0.0;
        IncomingInviteStatus = GameInviteStatus.Expired;
        IncomingInvite = null;
        IncomingInviteFeedbackMessage = $"{invite.SenderName}의 초대가 시간 초과로 만료되었습니다.";

        _ = await _social.SendGameEventAsync(invite.Friend, new GameEventPayload
        {
            GameType = invite.GameType,
            SessionId = invite.SessionId,
            Action = "decline",
            WagerCoins = invite.WagerCoins
        });
    }

    private void RecordCanceledInviteSession(string sessionId)
    {
        if (_canceledInviteSessions.Count >= RecentInvitationCapacity)
        {
            _canceledInviteSessions.Clear();
        }

        _canceledInviteSessions.Add(sessionId);
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
            NotifyCoinsChanged();
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
