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
    public bool IsFreeGame => WagerCoins <= 0;

    public string GameDisplayName => GameType switch
    {
        RpsGameEngine.GameTypeId => "가위바위보",
        TypingSprintEngine.GameTypeId => "타이핑 대결",
        LabLadderEngine.GameTypeId => "연구실 사다리",
        _ => "코인 게임"
    };

    public string CostBadgeText => WagerCoins > 0
        ? $"{LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(WagerCoins)} 코인"
        : "무료 (0코인)";

    public string SummaryText
    {
        get
        {
            if (GameType == LabLadderEngine.GameTypeId)
            {
                var feeText = WagerCoins > 0
                    ? $"{LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(WagerCoins)} 코인"
                    : "무료 (0코인)";
                return string.IsNullOrWhiteSpace(PrizeSummary)
                    ? $"{SenderName}의 {GameDisplayName} 초대 ({feeText})"
                    : $"{SenderName}의 {GameDisplayName} 초대 ({feeText} · 상품: {PrizeSummary})";
            }

            return WagerCoins > 0
                ? $"{SenderName}의 {GameDisplayName} 초대 ({LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(WagerCoins)} 코인)"
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
    private string? _rpsInviteTargetId;
    private string? _typingInviteTargetId;
    private bool _refreshingOnlineFriends;
    private readonly Dictionary<string, bool> _ladderInviteSelection = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _typingCountdownCts;
    private CancellationTokenSource? _incomingInviteCountdownCts;
    private CancellationTokenSource? _outgoingWaitCountdownCts;

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
    public PetEconomyService Economy => _progression.Economy;
    public long CurrentCoins => _profile.SoloProgress.Coins;
    public string CurrentCoinsCompactText => LabPet.Client.Formatting.CompactNumberFormatter.Format(CurrentCoins);
    public string CurrentCoinsExactTooltip => $"보유 코인: {LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(CurrentCoins)} 코인";

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
    public bool IsSoloLadderRunning => SoloLadderPhase == SoloLadderPhase.Running;
    public bool IsLadderInProgress => IsLadderRoomOpen || IsLadderAnimating || IsSoloLadderRunning;
    public bool CanSwitchLadderMode => !IsLadderInProgress;

    internal bool IsAnyGameBusy => IsHiLowActive
        || IsRpsWaitingResponse || IsRpsInMatch
        || IsTypingWaitingResponse || IsTypingCountdown || IsTypingRunning || _typingSessionId is not null
        || IsLadderRoomOpen || IsLadderAnimating || IsSoloLadderRunning;

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
    [ObservableProperty, NotifyPropertyChangedFor(
        nameof(HasIncomingInvite),
        nameof(IncomingInviteText),
        nameof(CanRespondToIncomingInvite),
        nameof(CanAcceptIncomingInvite),
        nameof(ShowIncomingInviteCoinDetails),
        nameof(IncomingInviteRequiredCoins),
        nameof(IncomingInviteShortfallCoins),
        nameof(HasIncomingInviteInsufficientCoins),
        nameof(IncomingInviteRequiredCoinsText),
        nameof(IncomingInviteCurrentCoinsText),
        nameof(IncomingInviteShortfallText),
        nameof(IncomingInviteEligibilityText))]
    private IncomingGameInvite? _incomingInvite;

    [ObservableProperty, NotifyPropertyChangedFor(
        nameof(HasIncomingInvite),
        nameof(CanRespondToIncomingInvite),
        nameof(CanAcceptIncomingInvite),
        nameof(IsAcceptingIncomingInvite))]
    private GameInviteStatus _incomingInviteStatus = GameInviteStatus.None;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IncomingInviteRemainingTimeText))]
    private int _incomingInviteRemainingSeconds;
    [ObservableProperty] private double _incomingInviteProgressRatio = 1.0;
    [ObservableProperty] private ImageSource? _incomingInvitePortrait;
    [ObservableProperty] private string _incomingInviteFeedbackMessage = string.Empty;

    public bool HasIncomingInvite => IncomingInvite is not null
        && IncomingInviteStatus is GameInviteStatus.Pending or GameInviteStatus.Accepting or GameInviteStatus.Ready;

    public bool IsAcceptingIncomingInvite => IncomingInviteStatus is GameInviteStatus.Accepting or GameInviteStatus.Ready;
    public bool CanRespondToIncomingInvite => IncomingInvite is not null && IncomingInviteStatus == GameInviteStatus.Pending;
    public string IncomingInviteText => IncomingInvite?.SummaryText ?? string.Empty;

    public bool ShowIncomingInviteCoinDetails => IncomingInvite is { WagerCoins: > 0 };
    public int IncomingInviteRequiredCoins => IncomingInvite?.WagerCoins ?? 0;
    public long IncomingInviteCurrentCoins => CurrentCoins;
    public long IncomingInviteShortfallCoins => IncomingInvite is { WagerCoins: > 0 } invite
        ? Math.Max(0L, invite.WagerCoins - CurrentCoins)
        : 0L;
    public bool HasIncomingInviteInsufficientCoins => IncomingInvite is { WagerCoins: > 0 } invite
        && CurrentCoins < invite.WagerCoins;
    public bool CanAcceptIncomingInvite => CanRespondToIncomingInvite && !HasIncomingInviteInsufficientCoins;

    public string IncomingInviteRequiredCoinsText => $"{LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(IncomingInviteRequiredCoins)} 코인";
    public string IncomingInviteCurrentCoinsText => $"{LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(CurrentCoins)} 코인";
    public string IncomingInviteShortfallText => $"{LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(IncomingInviteShortfallCoins)} 코인";
    public string IncomingInviteRemainingTimeText => $"{Math.Max(0, IncomingInviteRemainingSeconds)}초";
    public string IncomingInviteEligibilityText => IncomingInvite is not { } invite
        ? string.Empty
        : invite.WagerCoins <= 0
            ? "참가 가능 (무료 게임)"
            : HasIncomingInviteInsufficientCoins
                ? $"참가 불가 · {LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(IncomingInviteShortfallCoins)} 코인 부족"
                : "참가 가능";

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
    public FriendInvitePickerViewModel RpsFriendPicker { get; } = new(FriendInviteSelectionMode.Single);
    public OneToOneInviteWaitState RpsWaitState { get; } = new() { GameTitle = "가위바위보" };

    [ObservableProperty] private OnlineFriendChoice? _selectedRpsFriend;
    [ObservableProperty] private int _rpsWager = 30;
    [ObservableProperty] private string _rpsStatusText = "온라인 친구를 선택해 가위바위보 코인 배틀을 신청해 보세요!";
    [ObservableProperty] private string _rpsMyMoveText = "?";
    [ObservableProperty] private string _rpsOpponentMoveText = "?";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RpsInviteButtonText), nameof(IsRpsInProgress), nameof(IsRpsSetupVisible), nameof(IsRpsWaitViewVisible))]
    private bool _isRpsWaitingResponse;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsRpsInProgress))]
    private bool _isRpsInMatch;
    [ObservableProperty] private bool _canChooseRpsMove;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsRpsSetupOrWaiting), nameof(IsRpsSetupVisible), nameof(IsRpsWaitViewVisible), nameof(IsRpsArenaVisible))]
    private RpsPhase _rpsPhase = RpsPhase.Setup;
    [ObservableProperty] private RpsMove? _rpsMySelectedMove;
    [ObservableProperty] private RpsMove? _rpsRevealedOpponentMove;
    [ObservableProperty] private bool _rpsOpponentCommitted;
    [ObservableProperty] private RpsOutcome? _rpsLastOutcome;
    [ObservableProperty] private string _rpsOpponentDisplayName = "상대 친구";
    [ObservableProperty] private ImageSource? _rpsOpponentPortrait;
    [ObservableProperty] private string _rpsSettlementText = string.Empty;
    [ObservableProperty] private int _rpsCurrentMatchWager;

    public bool IsRpsSetupVisible => RpsPhase == RpsPhase.Setup;
    public bool IsRpsWaitViewVisible => RpsPhase == RpsPhase.WaitingInviteResponse;
    public bool IsRpsSetupOrWaiting => RpsPhase is RpsPhase.Setup or RpsPhase.WaitingInviteResponse;
    public bool IsRpsArenaVisible => RpsPhase is RpsPhase.Choosing or RpsPhase.WaitingOpponent or RpsPhase.Result;
    public string RpsInviteButtonText => IsRpsWaitingResponse ? "도전 취소" : "도전 보내기";

    private string? _rpsSessionId;
    private FriendEntry? _rpsOpponent;
    private int _rpsActiveWager;
    private RpsMove? _rpsMyMove;
    private RpsMove? _rpsOpponentMove;

    // ── 3. 친구 타이핑 스프린트 (LAN P2P) ─────────────────────────────────
    public FriendInvitePickerViewModel TypingFriendPicker { get; } = new(FriendInviteSelectionMode.Single);
    public FriendInvitePickerViewModel LadderFriendPicker { get; } = new(FriendInviteSelectionMode.Multi, LabLadderEngine.MaxParticipants - 1);
    public OneToOneInviteWaitState TypingWaitState { get; } = new() { GameTitle = "타이핑 대결" };

    [ObservableProperty] private OnlineFriendChoice? _selectedTypingFriend;
    [ObservableProperty] private int _typingWager = 30;
    [ObservableProperty] private string _typingStatusText = "온라인 친구와 10초 타이핑 대결을 펼쳐 보세요!";
    [ObservableProperty] private string _typingPromptText = "대결이 시작되면 문장이 표시됩니다.";
    [ObservableProperty] private string _typingInputText = string.Empty;
    [ObservableProperty] private int _typingMyScore;
    [ObservableProperty] private int _typingOpponentScore;
    [ObservableProperty] private string _typingTimeLeftText = "10.0초";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TypingInviteButtonText), nameof(IsTypingInProgress), nameof(IsTypingSetupVisible), nameof(IsTypingWaitViewVisible))]
    private bool _isTypingWaitingResponse;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsTypingInProgress), nameof(CanNavigateBackFromGame))]
    private bool _isTypingRunning;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsTypingCountdown), nameof(IsTypingSetupOrWaiting), nameof(IsTypingSetupVisible), nameof(IsTypingWaitViewVisible), nameof(IsTypingArenaVisible), nameof(IsTypingInProgress), nameof(CanNavigateBackFromGame))]
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
    public bool IsTypingSetupVisible => TypingPhase == TypingPhase.Setup;
    public bool IsTypingWaitViewVisible => TypingPhase == TypingPhase.WaitingInviteResponse;
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
    public ObservableCollection<LadderPrizeRowViewModel> LadderPrizeRows { get; } = [];

    [ObservableProperty] private string _ladderPrizesText = "커피 쏘기 x1, 통과 x1";
    [ObservableProperty] private int _ladderEntryFee;
    [ObservableProperty] private string _ladderStatusText = "상품과 참가비를 설정한 뒤 친구 초대 방을 여세요!";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditLadderSetup), nameof(CanOpenLadderRoom), nameof(CanCancelLadderRoom), nameof(IsLadderInProgress), nameof(CanSwitchLadderMode), nameof(CanStartSoloLadder), nameof(LadderTargetSlotCount), nameof(LadderPrizeAllocationText), nameof(IsLadderPrizeAllocationMatched), nameof(LadderPrizeAllocationRatio))]
    private bool _isLadderRoomOpen;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RequiresHostLobbyPrizeAdjustment))]
    private bool _isLadderHost;
    [ObservableProperty] private bool _isLadderWaitingInvites;
    [ObservableProperty] private bool _isLadderPickingPhase;
    [ObservableProperty] private bool _canConfirmLadderParticipants;
    [ObservableProperty] private bool _showConfirmLadderButton;
    [ObservableProperty] private bool _canStartLadderGame;
    [ObservableProperty] private bool _showStartLadderButton;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditLadderSetup), nameof(CanOpenLadderRoom), nameof(CanCancelLadderRoom), nameof(CanShareLadderResult), nameof(IsLadderInProgress), nameof(CanSwitchLadderMode), nameof(CanStartSoloLadder))]
    private bool _isLadderAnimating;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanCancelLadderRoom))]
    private LadderStage _ladderStage = LadderStage.Setup;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LadderTargetSlotCount), nameof(LadderPrizeAllocationText), nameof(IsLadderPrizeAllocationMatched), nameof(LadderPrizeAllocationRatio))]
    private bool _isLadderMultiplayerMode = true;
    [ObservableProperty] private string _ladderLobbySummaryText = string.Empty;
    [ObservableProperty] private string _ladderInvitedTotalText = "전체 초대 0명";
    [ObservableProperty] private string _ladderJoinedCountText = "현재 입장 1명";
    [ObservableProperty] private string _ladderLobbyRemainingText = "남은 시간 20초";
    [ObservableProperty] private string _ladderRoomPrizesText = string.Empty;
    [ObservableProperty] private string _ladderRoomMembersText = string.Empty;
    [ObservableProperty] private string _ladderBoardPrizesSummaryText = string.Empty;
    [ObservableProperty] private string _ladderResultHeadline = string.Empty;
    [ObservableProperty] private string _ladderResultSettlementText = string.Empty;
    [ObservableProperty] private string _ladderMyResultPrizeText = string.Empty;
    [ObservableProperty] private bool _requiresHostLobbyPrizeAdjustment;
    [ObservableProperty] private bool _requiresGuestPrizeAgreement;
    [ObservableProperty] private string _ladderPrizeAgreementBannerText = string.Empty;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSoloLadderRunning), nameof(IsLadderInProgress), nameof(CanSwitchLadderMode), nameof(CanStartSoloLadder), nameof(IsSoloLadderIdle), nameof(IsSoloLadderResult))]
    private SoloLadderPhase _soloLadderPhase = SoloLadderPhase.Idle;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStartSoloLadder), nameof(SoloLadderMaxPayoutText))]
    private int _soloLadderBetCoins = 10;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStartSoloLadder))]
    private int _soloLadderSelectedSlot = -1;

    public bool IsSoloLadderIdle => SoloLadderPhase == SoloLadderPhase.Idle;
    public bool IsSoloLadderResult => SoloLadderPhase == SoloLadderPhase.Result;
    public bool CanStartSoloLadder =>
        !IsAnyGameBusy && !IsAcceptingIncomingInvite
        && SoloLadderPhase == SoloLadderPhase.Idle
        && SoloLadderSelectedSlot is >= 0 and < SoloLadderEngine.SlotCount
        && PetEconomyService.CanAfford(_profile, SoloLadderBetCoins);
    public string SoloLadderMaxPayoutText =>
        $"{LabPet.Client.Formatting.CompactNumberFormatter.FormatExact((long)SoloLadderBetCoins * 250L / 100L)} 코인";

    public bool CanEditLadderSetup => !IsLadderRoomOpen && !IsLadderAnimating;
    public bool CanOpenLadderRoom => HasOnlineFriends && SelectedLadderInviteeCount > 0 && IsLadderPrizeAllocationMatched && !IsLadderRoomOpen && !IsLadderAnimating;
    public bool CanCancelLadderRoom => IsLadderRoomOpen && !IsLadderAnimating && !_ladderStarting && _ladderPreparedStart is null && _ladderRoom?.Phase != LadderRoomPhase.Running;
    public bool CanShareLadderResult => !IsLadderAnimating && _lastLadderResults.Count > 0;
    public int SelectedLadderInviteeCount => LadderFriendPicker.SelectedCount;

    public int LadderTargetSlotCount
    {
        get
        {
            if (IsLadderRoomOpen && _ladderRoom is not null)
            {
                return _ladderRoom.PendingInviteCount == 0
                    ? Math.Clamp(_ladderRoom.Members.Count, LabLadderEngine.MinParticipants, LabLadderEngine.MaxParticipants)
                    : Math.Clamp(_ladderRoom.Members.Count + _ladderRoom.PendingInviteCount, LabLadderEngine.MinParticipants, LabLadderEngine.MaxParticipants);
            }

            return Math.Clamp(1 + Math.Max(1, SelectedLadderInviteeCount), LabLadderEngine.MinParticipants, LabLadderEngine.MaxParticipants);
        }
    }

    public int LadderConfiguredSlotCount => LadderPrizeRows.Sum(r => Math.Max(0, r.Quantity));
    public bool IsLadderPrizeAllocationMatched => LadderConfiguredSlotCount == LadderTargetSlotCount;
    public double LadderPrizeAllocationRatio => LadderTargetSlotCount <= 0
        ? 0.0
        : Math.Clamp((double)LadderConfiguredSlotCount / LadderTargetSlotCount, 0.0, 1.0);
    public string LadderPrizeAllocationText => IsLadderPrizeAllocationMatched
        ? $"배정 완료 ({LadderConfiguredSlotCount} / {LadderTargetSlotCount}칸)"
        : $"배정 {LadderConfiguredSlotCount} / {LadderTargetSlotCount}칸 (수량을 맞춰 주세요)";

    public LadderBoard? ActiveLadderBoard => _pendingLadderRun?.Board ?? _lastLadderBoard;
    public double ActiveLadderAnimationProgress => _pendingLadderRun?.AnimationProgress ?? 0.0;
    public IReadOnlyList<bool> ActiveLadderRevealedColumns => _pendingLadderRun?.RevealedColumns ?? [];
    public IReadOnlyList<ImageSource?> ActiveLadderParticipantPortraits => _pendingLadderRun?.ParticipantPortraits ?? [];

    private sealed class LadderInviteeTracker
    {
        public required string PetId { get; init; }
        public required string DisplayName { get; init; }
        public FriendEntry? Friend { get; init; }
        public LadderParticipantStatus Status { get; set; }
        public DateTimeOffset? DisconnectedSinceUtc { get; set; }
    }

    private LadderBoard? _lastLadderBoard;
    private IReadOnlyList<LadderTraceResult> _lastLadderResults = [];
    private LadderRoomStateDto? _ladderRoom;
    private FriendEntry? _ladderHostFriend;
    private readonly List<string> _ladderInitialPrizes = [];
    private readonly HashSet<string> _ladderPendingInviteeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FriendEntry> _ladderPendingInvitees = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FriendEntry> _ladderJoinedFriends = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LadderInviteeTracker> _ladderInviteeTrackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _abortedLadderSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _startedLadderSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _settledLadderSessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncingLadderPrizes;
    private int _ladderActiveWager;
    private int _ladderPaidCoins;
    private bool _ladderStarting;
    private PendingLadderRun? _pendingLadderRun;
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
        _inviteToken = _inviteLifetime.Token;

        _typingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _typingTimer.Tick += OnTypingTimerTick;

        _social.StateChanged += OnSocialStateChanged;
        _social.GameEventReceived += OnSocialGameEvent;
        _progression.Economy.EconomyStateChanged += OnEconomyStateChanged;
        RpsFriendPicker.SelectionChanged += OnRpsFriendPickerSelectionChanged;
        TypingFriendPicker.SelectionChanged += OnTypingFriendPickerSelectionChanged;
        LadderFriendPicker.SelectionChanged += OnLadderFriendPickerSelectionChanged;
        RpsWaitState.CancelCommand = CancelRpsOutgoingInviteCommand;
        TypingWaitState.CancelCommand = CancelTypingOutgoingInviteCommand;
        InitializeLadderPrizeRows();
    }

    partial void OnLadderPrizesTextChanged(string value)
    {
        if (_syncingLadderPrizes)
        {
            return;
        }

        LoadPrizeRowsFromText(value);
    }

    partial void OnLadderEntryFeeChanged(int value)
    {
        NotifyLadderPrizeAllocationChanged();
    }

    private void OnRpsFriendPickerSelectionChanged(object? sender, EventArgs e)
    {
        if (_refreshingOnlineFriends)
        {
            return;
        }

        _rpsInviteTargetId = RpsFriendPicker.GetFirstSelectedEntry()?.PetId;
        _refreshingOnlineFriends = true;
        try
        {
            SelectedRpsFriend = FindOnlineInviteChoice(_rpsInviteTargetId);
        }
        finally
        {
            _refreshingOnlineFriends = false;
        }
    }

    private void OnTypingFriendPickerSelectionChanged(object? sender, EventArgs e)
    {
        if (_refreshingOnlineFriends)
        {
            return;
        }

        _typingInviteTargetId = TypingFriendPicker.GetFirstSelectedEntry()?.PetId;
        _refreshingOnlineFriends = true;
        try
        {
            SelectedTypingFriend = FindOnlineInviteChoice(_typingInviteTargetId);
        }
        finally
        {
            _refreshingOnlineFriends = false;
        }
    }

    private void OnLadderFriendPickerSelectionChanged(object? sender, EventArgs e)
    {
        if (_refreshingOnlineFriends)
        {
            return;
        }

        var selectedIds = new HashSet<string>(LadderFriendPicker.GetSelectedPetIds(), StringComparer.OrdinalIgnoreCase);
        _refreshingOnlineFriends = true;
        try
        {
            foreach (var option in LadderInviteCandidates)
            {
                var shouldSelect = selectedIds.Contains(option.Friend.PetId);
                _ladderInviteSelection[option.Friend.PetId] = shouldSelect;
                option.IsSelected = shouldSelect;
            }
        }
        finally
        {
            _refreshingOnlineFriends = false;
        }

        OnPropertyChanged(nameof(SelectedLadderInviteeCount));
        OnPropertyChanged(nameof(CanOpenLadderRoom));
        NotifyLadderPrizeAllocationChanged();
    }

    private void OnSocialStateChanged(object? sender, EventArgs e) => RunOnUi(RefreshOnlineFriends);

    private void OnSocialGameEvent(object? sender, GameEventReceivedEventArgs e)
        => RunOnUi(() =>
        {
            if (!_disposed)
            {
                _ = HandleGameEventAsync(e);
            }
        });

    private void OnEconomyStateChanged(object? sender, EconomyChangedEventArgs e)
        => RunOnUi(NotifyCoinsChanged);

    private readonly AsyncOperationLifetime _profileSaves = new();
    private readonly AsyncOperationLifetime _gameTimers = new();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var profileSaves = _profileSaves.DrainAsync();
        var gameTimers = _gameTimers.DrainAsync();
        _social.StateChanged -= OnSocialStateChanged;
        _social.GameEventReceived -= OnSocialGameEvent;
        _progression.Economy.EconomyStateChanged -= OnEconomyStateChanged;
        RpsFriendPicker.SelectionChanged -= OnRpsFriendPickerSelectionChanged;
        TypingFriendPicker.SelectionChanged -= OnTypingFriendPickerSelectionChanged;
        LadderFriendPicker.SelectionChanged -= OnLadderFriendPickerSelectionChanged;
        CancelOutgoingWaitCountdown();
        _typingTimer.Stop();
        _typingTimer.Tick -= OnTypingTimerTick;
        _ladderStartReadiness?.Completion.TrySetResult(false);
        _inviteLifetime.Cancel();
        _inviteLifetime.Dispose();
        foreach (var exchange in _inviteExchanges.Values)
            exchange.Completion.TrySetResult();
        CancelTypingCountdown();
        CancelIncomingInviteCountdown();
        _rpsSessionId = null;
        _typingSessionId = null;
        Task.WhenAll(profileSaves, gameTimers).GetAwaiter().GetResult();
    }

    public void BindProfile(PetProfile profile)
    {
        _profile = profile;
        RecoverInterruptedGameWagers(profile);
        RefreshOnlineFriends();
        if (LadderPrizeRows.Count == 0)
        {
            InitializeLadderPrizeRows();
        }

        NotifyCoinsChanged();
        OnPropertyChanged(nameof(MyPetPortrait));
        OnPropertyChanged(nameof(MyDisplayName));
        RestorePendingSoloLadderSessionIfAny();
    }

    partial void RestorePendingSoloLadderSessionIfAny();

    public void RefreshOnlineFriends()
    {
        var peers = _social.DiscoveredPeers;
        var list = new List<OnlineFriendChoice>();
        var pickerInputs = new List<FriendInviteCandidateInput>();
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
            var appearance = ResolveFriendAppearance(f.PetId, f);
            var effectiveEntry = new FriendEntry
            {
                PetId = f.PetId,
                OwnerName = string.IsNullOrWhiteSpace(live.OwnerName) ? f.OwnerName : live.OwnerName,
                PetName = string.IsNullOrWhiteSpace(live.PetName) ? f.PetName : live.PetName,
                LastKnownIp = f.LastKnownIp,
                LastKnownTcpPort = f.LastKnownTcpPort,
                PairSecretBase64 = f.PairSecretBase64,
                CachedAppearance = appearance ?? f.CachedAppearance,
                Intimacy = f.Intimacy,
                PairedAtUtc = f.PairedAtUtc,
                LastSeenAtUtc = f.LastSeenAtUtc
            };
            pickerInputs.Add(new FriendInviteCandidateInput(
                effectiveEntry,
                live,
                TryGetPortrait(appearance),
                IsOnline: true));
        }

        _refreshingOnlineFriends = true;
        try
        {
            for (var i = 0; i < list.Count; i++)
            {
                var item = list[i];
                var current = OnlineFriends.Skip(i).FirstOrDefault(choice => SameFriend(choice.Friend, item.Friend));
                if (current is null)
                {
                    OnlineFriends.Insert(i, item);
                    continue;
                }

                var oldIndex = OnlineFriends.IndexOf(current);
                if (oldIndex != i)
                    OnlineFriends.Move(oldIndex, i);
                if (current.DisplayName != item.DisplayName)
                    OnlineFriends[i] = item;
            }

            while (OnlineFriends.Count > list.Count)
                OnlineFriends.RemoveAt(OnlineFriends.Count - 1);

            RpsFriendPicker.UpdateCandidates(pickerInputs);
            TypingFriendPicker.UpdateCandidates(pickerInputs);
            LadderFriendPicker.UpdateCandidates(pickerInputs);

            // 최초에는 아무도 선택되지 않으며, 오프라인이 된 친구는 선택 해제하되 타인을 자동 선택하지 않는다.
            _rpsInviteTargetId = RpsFriendPicker.GetFirstSelectedEntry()?.PetId;
            _typingInviteTargetId = TypingFriendPicker.GetFirstSelectedEntry()?.PetId;
            SelectedRpsFriend = FindOnlineInviteChoice(_rpsInviteTargetId);
            SelectedTypingFriend = FindOnlineInviteChoice(_typingInviteTargetId);

            foreach (var oldCandidate in LadderInviteCandidates)
                oldCandidate.PropertyChanged -= OnLadderInviteCandidatePropertyChanged;
            LadderInviteCandidates.Clear();
            var selectedLadderIds = new HashSet<string>(LadderFriendPicker.GetSelectedPetIds(), StringComparer.OrdinalIgnoreCase);
            foreach (var item in list)
            {
                var selected = selectedLadderIds.Contains(item.Friend.PetId);
                _ladderInviteSelection[item.Friend.PetId] = selected;
                var candidate = new LadderFriendInviteOption(
                    item.Friend,
                    item.DisplayName,
                    TryGetPortrait(ResolveFriendAppearance(item.Friend.PetId, item.Friend)),
                    selected);
                candidate.PropertyChanged += OnLadderInviteCandidatePropertyChanged;
                LadderInviteCandidates.Add(candidate);
            }
        }
        finally
        {
            _refreshingOnlineFriends = false;
        }

        OnPropertyChanged(nameof(HasOnlineFriends));
        OnPropertyChanged(nameof(SelectedLadderInviteeCount));
        OnPropertyChanged(nameof(CanOpenLadderRoom));
        NotifyLadderPrizeAllocationChanged();
    }

    private static bool SameFriend(FriendEntry a, FriendEntry b)
        => string.Equals(a.PetId, b.PetId, StringComparison.OrdinalIgnoreCase);

    private OnlineFriendChoice? FindOnlineInviteChoice(string? petId)
        => string.IsNullOrWhiteSpace(petId)
            ? null
            : OnlineFriends.FirstOrDefault(choice => string.Equals(choice.Friend.PetId, petId, StringComparison.OrdinalIgnoreCase));

    partial void OnSelectedRpsFriendChanged(OnlineFriendChoice? value)
    {
        if (_refreshingOnlineFriends)
        {
            return;
        }

        _rpsInviteTargetId = value?.Friend.PetId;
        RpsFriendPicker.SetSelectedPetId(_rpsInviteTargetId, raiseEvent: false);
    }

    partial void OnSelectedTypingFriendChanged(OnlineFriendChoice? value)
    {
        if (_refreshingOnlineFriends)
        {
            return;
        }

        _typingInviteTargetId = value?.Friend.PetId;
        TypingFriendPicker.SetSelectedPetId(_typingInviteTargetId, raiseEvent: false);
    }

    private void OnLadderInviteCandidatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_refreshingOnlineFriends)
        {
            return;
        }

        if (e.PropertyName == nameof(LadderFriendInviteOption.IsSelected) && sender is LadderFriendInviteOption candidate)
        {
            _ladderInviteSelection[candidate.Friend.PetId] = candidate.IsSelected;
            LadderFriendPicker.SetSelection(candidate.Friend.PetId, candidate.IsSelected, raiseEvent: false);
            OnPropertyChanged(nameof(SelectedLadderInviteeCount));
            OnPropertyChanged(nameof(CanOpenLadderRoom));
            NotifyLadderPrizeAllocationChanged();
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
    {
        OnPropertyChanged(nameof(CurrentCoins));
        OnPropertyChanged(nameof(CurrentCoinsCompactText));
        OnPropertyChanged(nameof(CurrentCoinsExactTooltip));
        OnEconomyCoinsUpdated();
    }

    private void OnEconomyCoinsUpdated()
    {
        OnPropertyChanged(nameof(IncomingInviteCurrentCoins));
        OnPropertyChanged(nameof(IncomingInviteShortfallCoins));
        OnPropertyChanged(nameof(HasIncomingInviteInsufficientCoins));
        OnPropertyChanged(nameof(CanAcceptIncomingInvite));
        OnPropertyChanged(nameof(IncomingInviteCurrentCoinsText));
        OnPropertyChanged(nameof(IncomingInviteShortfallText));
        OnPropertyChanged(nameof(IncomingInviteEligibilityText));
        OnPropertyChanged(nameof(CanStartSoloLadder));
    }

    // ══════════════════════════════════════════════════════════════════════
    // 1. 코인 하이로우 (솔로)
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task StartHiLowAsync()
    {
        if (IsAcceptingIncomingInvite || IsAnyGameBusy)
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

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SendRpsInviteAsync()
    {
        if (IsRpsInMatch)
        {
            return;
        }

        if (IsRpsWaitingResponse)
        {
            if (_rpsSessionId is { } session && _rpsOpponent is { } opponent)
                await CancelTrackedInviteAsync(session, opponent);
            return;
        }

        if (IsAnyGameBusy || HasIncomingInvite)
        {
            RpsStatusText = DescribeInviteReason("busy");
            return;
        }

        if (SelectedRpsFriend is null)
        {
            RpsStatusText = _rpsInviteTargetId is null ? "대결할 온라인 친구를 먼저 선택해 주세요." : DescribeInviteReason("offline");
            return;
        }

        if (_profile.SoloProgress.Coins < RpsWager)
        {
            RpsStatusText = $"코인이 부족합니다. (필요: {RpsWager}, 보유: {_profile.SoloProgress.Coins})";
            return;
        }

        var target = SelectedRpsFriend;
        if (!IsOnlineInviteTarget(target.Friend.PetId))
        {
            RpsStatusText = DescribeInviteReason("offline");
            return;
        }
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
        RpsStatusText = $"{target.DisplayName}에게 초대를 보내는 중입니다.";

        var deadline = NewInviteDeadline();
        StartOutgoingWaitCountdown(
            RpsWaitState,
            "가위바위보",
            _rpsActiveWager,
            target.DisplayName,
            RpsOpponentPortrait,
            $"{target.DisplayName}에게 초대를 보내는 중입니다.",
            sessionId,
            deadline);

        await SendTrackedOfferAsync(target.Friend, RpsGameEngine.GameTypeId, sessionId, _rpsActiveWager, deadline);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CancelRpsOutgoingInviteAsync()
    {
        if (IsRpsWaitingResponse && _rpsSessionId is { } session && _rpsOpponent is { } opponent)
        {
            await CancelTrackedInviteAsync(session, opponent);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CancelTypingOutgoingInviteAsync()
    {
        if (IsTypingWaitingResponse && _typingSessionId is { } session && _typingOpponent is { } opponent)
        {
            await CancelTrackedInviteAsync(session, opponent);
        }
    }

    private void StartOutgoingWaitCountdown(
        OneToOneInviteWaitState state,
        string gameTitle,
        int wagerCoins,
        string opponentDisplayName,
        ImageSource? opponentPortrait,
        string detailText,
        string sessionId,
        DateTimeOffset deadline)
    {
        CancelOutgoingWaitCountdown();
        var now = DateTimeOffset.UtcNow;
        var totalSeconds = Math.Max(0.2, (deadline - now).TotalSeconds);

        state.GameTitle = gameTitle;
        state.WagerCoins = wagerCoins;
        state.MyPetName = MyDisplayName;
        state.MyPetPortrait = MyPetPortrait;
        state.MyReadyStatusText = "준비 완료";
        state.OpponentDisplayName = opponentDisplayName;
        state.OpponentPortrait = opponentPortrait;
        state.OpponentResponseStatusText = "전송 중";
        state.StatusDetailText = detailText;
        state.RemainingSeconds = Math.Max(1, (int)Math.Ceiling(totalSeconds));
        state.ProgressRatio = 1.0;
        state.CanCancel = true;

        var cts = new CancellationTokenSource();
        _outgoingWaitCountdownCts = cts;
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

                    var remaining = (deadline - DateTimeOffset.UtcNow).TotalSeconds;
                    if (remaining <= 0)
                    {
                        break;
                    }

                    var sec = Math.Max(1, (int)Math.Ceiling(remaining));
                    var ratio = Math.Clamp(remaining / totalSeconds, 0.0, 1.0);
                    RunOnUi(() =>
                    {
                        if ((_rpsSessionId == sessionId && IsRpsWaitingResponse)
                            || (_typingSessionId == sessionId && IsTypingWaitingResponse))
                        {
                            state.RemainingSeconds = sec;
                            state.ProgressRatio = ratio;
                        }
                    });
                }
            }
            catch (TaskCanceledException)
            {
            }
        }, cts.Token);
    }

    private void CancelOutgoingWaitCountdown()
    {
        _outgoingWaitCountdownCts?.Cancel();
        _outgoingWaitCountdownCts?.Dispose();
        _outgoingWaitCountdownCts = null;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 공용 초대 수락 / 거절 / 만료 상태 머신
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    public async Task AcceptIncomingInviteAsync()
    {
        InviteExchange? exchange = null;
        await _inviteGate.WaitAsync();
        try
        {
            if (IncomingInvite is not { } invite || IncomingInviteStatus != GameInviteStatus.Pending
                || !_inviteExchanges.TryGetValue(InviteKey(invite.SenderPetId, invite.SessionId), out exchange)
                || exchange.Status != GameInviteStatus.Pending)
                return;

            if (DateTimeOffset.UtcNow >= exchange.Deadline)
            {
                await EndInviteAsync(exchange, GameInviteStatus.Expired, "expired", Decline);
                return;
            }
            if (IsAnyGameBusy)
            {
                await EndInviteAsync(exchange, GameInviteStatus.Declined, "busy", Decline);
                return;
            }
            if (exchange.Wager > 0 && !PetEconomyService.CanAfford(_profile, exchange.Wager))
            {
                IncomingInviteFeedbackMessage = $"코인이 {LabPet.Client.Formatting.CompactNumberFormatter.FormatExact(PetEconomyService.GetShortfall(_profile, exchange.Wager))} 코인 부족해 아직 수락할 수 없습니다.";
                OnEconomyCoinsUpdated();
                return;
            }

            exchange.Reason = string.Empty;
            exchange.Status = GameInviteStatus.Accepting;
            IncomingInviteFeedbackMessage = string.Empty;
            IncomingInviteStatus = GameInviteStatus.Accepting;
            var failure = await ReserveInviteCoinsAsync(exchange);
            if (failure is not null || DateTimeOffset.UtcNow >= exchange.Deadline)
            {
                await EndInviteAsync(exchange, failure is null ? GameInviteStatus.Expired : GameInviteStatus.Failed,
                    failure ?? "expired", Decline);
                return;
            }
            _ = SendInvitePacketAsync(exchange, Accept);
        }
        finally
        {
            _inviteGate.Release();
        }

        if (exchange is not null)
            await exchange.Completion.Task;
    }

    [RelayCommand]
    public async Task DeclineIncomingInviteAsync()
    {
        await _inviteGate.WaitAsync();
        try
        {
            if (IncomingInvite is { } invite && IncomingInviteStatus == GameInviteStatus.Pending
                && _inviteExchanges.TryGetValue(InviteKey(invite.SenderPetId, invite.SessionId), out var exchange))
            {
                var declineReason = exchange.Wager > 0 && !PetEconomyService.CanAfford(_profile, exchange.Wager)
                    ? "coins"
                    : "declined";
                await EndInviteAsync(exchange, GameInviteStatus.Declined, declineReason, Decline);
            }
        }
        finally
        {
            _inviteGate.Release();
        }
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

        if (await HandleInviteHandshakeAsync(friend, senderName, p))
            return;
        await ObserveStartedMatchAsync(friend, senderName, p);

        if (p.GameType == RpsGameEngine.GameTypeId && p.SessionId == _rpsSessionId && p.WagerCoins == _rpsActiveWager
            && string.Equals(_rpsOpponent?.PetId, e.SenderPetId, StringComparison.OrdinalIgnoreCase))
        {
            switch (p.Action)
            {
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
                case "cancel" when !IsTypingWaitingResponse:
                    await RefundTypingRoundAsync("상대가 타이핑 대결을 취소해 배팅 코인을 반환했습니다.");
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
            if (p.Action == "start" && p.InviteExpiresAtUtc is not null && _ladderAppliedStart == p.Data
                && _pendingLadderRun is { IsMultiplayer: true } startedRun && startedRun.SessionId == p.SessionId
                && p.WagerCoins == startedRun.WagerCoins
                && string.Equals(startedRun.HostFriend?.PetId, e.SenderPetId, StringComparison.OrdinalIgnoreCase))
            {
                ReplyToAppliedLadderStart(friend, p);
            }
            else if (p.Action == "cancel"
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
            else if (_ladderRoom is not null && p.SessionId == _ladderRoom.SessionId && p.WagerCoins == _ladderActiveWager)
            {
                if ((_ladderStarting || IsLadderAnimating || _ladderRoom.Phase == LadderRoomPhase.Running)
                    && p.Action is not ("ready-start" or "started-start" or "start"))
                {
                    return;
                }

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
                        break;
                    }

                    var sec = Math.Max(1, (int)Math.Ceiling(remaining));
                    var ratio = Math.Clamp(remaining / totalSeconds, 0.0, 1.0);
                    RunOnUi(() =>
                    {
                        if (IncomingInvite?.SessionId == invite.SessionId && HasIncomingInvite)
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

    private async Task<bool> PersistProfileAsync()
    {
        try
        {
            if (!await SaveGameProfileAsync(_profile))
            {
                return false;
            }

            if (_disposed)
            {
                return true;
            }

            NotifyCoinsChanged();
            ProfileChanged?.Invoke(this, _profile);
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError("게임 코인 저장 실패: {0}", ex);
            GameNotification?.Invoke(this, "게임 변경을 저장하지 못했습니다.");
            return false;
        }
    }

    private async Task<bool> WaitForGameTimerAsync(TimeSpan duration)
    {
        using var operation = _gameTimers.TryBegin();
        if (operation is null)
        {
            return false;
        }
        try
        {
            await Task.Delay(duration, _inviteToken).ConfigureAwait(false);
            return !_inviteToken.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (_inviteToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> SaveGameProfileAsync(PetProfile profile)
    {
        using var operation = _profileSaves.TryBegin();
        if (operation is null)
        {
            return false;
        }

        // 종료 스레드가 저장을 기다려도 UI Dispatcher 재개에 의존하지 않는다.
        await Task.Run(() => _store.SaveProfileAsync(profile)).ConfigureAwait(false);
        return true;
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
