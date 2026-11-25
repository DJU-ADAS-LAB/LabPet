using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabPet.Client.Navigation;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Client.Updates;
using LabPet.Client.Views;
using LabPet.Shared.Models;
using LabPet.Shared.Transport;

namespace LabPet.Client.ViewModels;

/// <summary>
/// 펫 마우스 오버레이 팝업에 표시되는 미니멀 주변/친구 항목.
/// </summary>
public sealed partial class QuickOverlayEntry : ObservableObject
{
    private static readonly Brush OnlineDotBrush = CreateFrozenBrush("#FF34D399");

    public string PetId { get; init; } = string.Empty;
    [ObservableProperty] private string _displayTitle = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanFollowMusic), nameof(MusicTooltip), nameof(MusicButtonIcon))]
    private bool _isMusicActive;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowsFocusIndicator), nameof(ShowsSleepIndicator), nameof(ShowsOnlineDot), nameof(StatusTooltip))]
    private bool _isSleeping;
    [ObservableProperty, NotifyPropertyChangedFor(
        nameof(ShowsFocusIndicator),
        nameof(ShowsSleepIndicator),
        nameof(ShowsOnlineDot),
        nameof(StatusTooltip),
        nameof(IsInteractionBlocked),
        nameof(CanInteract),
        nameof(CanFollowMusic),
        nameof(CanToggleVisit),
        nameof(MessageTooltip),
        nameof(PetTooltip),
        nameof(MusicTooltip),
        nameof(VisitTooltip))]
    private bool _isFocusing;
    [ObservableProperty, NotifyPropertyChangedFor(
        nameof(IsInteractionBlocked),
        nameof(CanInteract),
        nameof(CanFollowMusic),
        nameof(CanToggleVisit),
        nameof(MessageTooltip),
        nameof(PetTooltip),
        nameof(MusicTooltip),
        nameof(VisitTooltip))]
    private bool _isLocalFocusing;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanFollowMusic), nameof(MusicTooltip), nameof(MusicButtonIcon))]
    private bool _isFollowingMusic;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanToggleVisit), nameof(VisitTooltip))]
    private bool _isVisitingDesktop;
    [ObservableProperty] private bool _isComposingMessage;

    // 상태 표시 우선순위: 1) 집중 중(✏️) > 2) 수면(zzz) > 3) 온라인 점
    public bool ShowsFocusIndicator => IsFocusing;
    public bool ShowsSleepIndicator => !IsFocusing && IsSleeping;
    public bool ShowsOnlineDot => !IsFocusing && !IsSleeping;

    public bool IsInteractionBlocked => IsFocusing || IsLocalFocusing;
    public bool CanInteract => !IsInteractionBlocked;
    public bool CanFollowMusic => IsFollowingMusic || (!IsInteractionBlocked && IsMusicActive);
    public bool CanToggleVisit => IsVisitingDesktop || !IsInteractionBlocked;
    public Brush StatusDotBrush => OnlineDotBrush;
    public string StatusTooltip => IsFocusing
        ? FriendOperationErrorCodes.PomodoroFocusMessage
        : IsSleeping ? "자는 중" : "온라인";
    public string MessageTooltip => IsInteractionBlocked
        ? FriendOperationErrorCodes.PomodoroFocusMessage
        : "빠른 메시지";
    public string PetTooltip => IsInteractionBlocked
        ? FriendOperationErrorCodes.PomodoroFocusMessage
        : "인사하기";
    public string VisitTooltip => IsVisitingDesktop
        ? "바탕화면에서 돌려보내기"
        : IsInteractionBlocked
            ? FriendOperationErrorCodes.PomodoroFocusMessage
            : "내 바탕화면에 데려오기 / 돌려보내기";
    public FriendEntry? Friend { get; set; }
    public SnowballAction? Snowball { get; init; }
    public PeerSnapshot? Peer { get; set; }

    public string MusicButtonIcon => IsFollowingMusic ? "⏹" : "🎵";

    public string MusicTooltip => IsFollowingMusic
        ? "노래 따라하기 중지"
        : IsInteractionBlocked
            ? FriendOperationErrorCodes.PomodoroFocusMessage
            : IsMusicActive ? "노래 따라하기 시작" : "음악 재생 중이 아님";

    private static Brush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// 개별 표시 시간을 가진 말풍선. 같은 문구가 반복되어도 별개 항목으로 유지한다.
/// </summary>
public class BubbleLine(string text)
{
    public string Text { get; } = text;
}

public sealed class FriendMessageBubble(string text, string senderName, ImageSource portrait) : BubbleLine(text)
{
    public string SenderName { get; } = senderName;
    public ImageSource Portrait { get; } = portrait;
}

/// <summary>
/// 바탕화면 투명 펫 창 ViewModel.
/// </summary>
public sealed partial class PetWindowViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly Brush PomodoroOverlayFocusBrush = CreateFrozenBrush("#FFFF5D8F");
    private static readonly Brush PomodoroOverlayBreakBrush = CreateFrozenBrush("#FF10B981");
    private const double PomodoroOverlayCenter = 10.0;
    private const double PomodoroOverlayRadius = 7.8;

    private readonly IAppDataStore _store;
    private readonly SocialCoordinatorService _social;
    private readonly PomodoroTimerService? _pomodoro;
    private readonly UpdateService? _updates;
    private readonly Dispatcher? _updateDispatcher;
    private readonly BubbleMessageStack _bubbleStack = new(MaxBubbleLines);
    private string? _dismissedUpdateVersion;

    public event EventHandler<NavigationRequest>? HubNavigationRequested;
    public event EventHandler? LaunchChristmasTreeRequested;
    public event EventHandler<bool>? StartupAutoRunToggled;
    public event EventHandler? ResetPositionRequested;
    public event EventHandler? ExitRequested;
    // 클릭 투과 집중 모드 UI 제거에 따라 관련 이벤트 주석 처리
    // public event EventHandler<bool>? ClickThroughChanged;
    public event EventHandler<PetProfile>? ProfileUpdated;
    public event EventHandler<PetProfile>? ProfileProgressUpdated;
    public event EventHandler<double>? PetScaleChanged;
    public event EventHandler<double>? PetOpacityChanged;
    public event EventHandler<(FriendEntry Friend, PetAppearance Appearance)>? ToggleVisitFriendRequested;
    internal event EventHandler? VisitorStateChanged;

    private Func<string, bool>? _isVisitorActiveChecker;

    internal void AttachVisitorChecker(Func<string, bool> checker) => _isVisitorActiveChecker = checker;
    internal bool IsVisitorActive(string petId) => _isVisitorActiveChecker?.Invoke(petId) ?? false;
    internal void RequestToggleVisitFriend(FriendEntry friend, PetAppearance appearance)
        => ToggleVisitFriendRequested?.Invoke(this, (friend, appearance));
    internal void NotifyVisitorStateChanged() => VisitorStateChanged?.Invoke(this, EventArgs.Empty);

    public PetProfile CurrentProfile { get; private set; } = new();

    public ObservableCollection<QuickOverlayEntry> QuickOverlayEntries { get; } = [];

    [ObservableProperty] private string _petName = "모찌";
    [ObservableProperty] private bool _isSleeping;
    // 클릭 투과 집중 모드 UI 제거에 따라 관련 상태 주석 처리
    // [ObservableProperty] private bool _isClickThroughEnabled;
    [ObservableProperty] private bool _isStartupAutoRunEnabled;
    [ObservableProperty] private bool _showPomodoroFeatureBadge = true;
    [ObservableProperty] private double _pomodoroOverlayProgressRatio = 25.0 / PomodoroTimerService.MaximumFocusMinutes;
    [ObservableProperty] private Geometry _pomodoroOverlayArcGeometry = PomodoroCircularDialMath.BuildArcGeometry(
        PomodoroOverlayCenter, PomodoroOverlayCenter, PomodoroOverlayRadius, 25.0 / PomodoroTimerService.MaximumFocusMinutes);
    [ObservableProperty] private Brush _pomodoroOverlayArcBrush = PomodoroOverlayFocusBrush;
    [ObservableProperty] private string _pomodoroOverlayTooltip = "뽀모도로 타이머 열기 (설정 25분)";
    [ObservableProperty] private bool _hasUpdateOffer;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private bool _canInstallUpdate;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAnyBubble))]
    private bool _isUpdateBubbleVisible;
    [ObservableProperty] private string _updateBubbleText = string.Empty;
    [ObservableProperty] private string _updateMenuHeader = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAnyBubble))]
    private bool _isBubbleVisible;

    /// <summary>
    /// 말풍선에 동시에 쌓아 보여 줄 최대 메시지 수. 초과 시 가장 오래된 메시지부터 밀려난다.
    /// </summary>
    public const int MaxBubbleLines = 5;

    /// <summary>
    /// 말풍선에 쌓여 표시 중인 메시지 줄 목록(오래된 순 → 최신 순).
    /// 각 줄은 자신의 표시 시간이 끝나면 개별적으로 사라진다.
    /// </summary>
    public ObservableCollection<BubbleLine> BubbleLines => _bubbleStack.Items;
    private readonly Queue<PendingFriendRequest> _pendingFriendRequestQueue = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingFriendRequest), nameof(PendingFriendRequestText), nameof(HasAnyBubble), nameof(CanClickFriendRequestAction))]
    private PendingFriendRequest? _pendingFriendRequest;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanClickFriendRequestAction), nameof(AcceptButtonText))]
    private bool _isRespondingFriendRequest;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingFriendRequestStatus))]
    private string _pendingFriendRequestStatusText = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AcceptButtonText))]
    private bool _canRetryPendingFriendRequest;
    [ObservableProperty] private string _trayTooltip = "LabPet";
    [ObservableProperty] private double _petScale = PetProfile.DefaultPetScale;
    [ObservableProperty] private double _petOpacity = 1.0;

    public string PetScalePercentText => $"{Math.Round(PetScale * 100)}%";
    public string PetOpacityPercentText => $"{Math.Round(PetOpacity * 100)}%";

    // 미니멀 오버레이 빠른 메시지 입력 상태
    [ObservableProperty] private QuickOverlayEntry? _quickMessageTarget;
    [ObservableProperty] private string _quickMessageText = string.Empty;

    public bool HasPendingFriendRequest => PendingFriendRequest is not null;
    public bool HasPendingFriendRequestStatus => !string.IsNullOrWhiteSpace(PendingFriendRequestStatusText);
    public bool CanClickFriendRequestAction => PendingFriendRequest is not null && !IsRespondingFriendRequest;
    public string AcceptButtonText => IsRespondingFriendRequest
        ? "처리 중..."
        : (CanRetryPendingFriendRequest ? "다시 시도" : "수락");
    public bool HasAnyBubble => IsUpdateBubbleVisible || HasPendingFriendRequest || IsBubbleVisible;
    public bool HasNoQuickOverlayEntries => QuickOverlayEntries.Count == 0;
    public string QuickOverlayHeaderText => $"온라인 친구 ({QuickOverlayEntries.Count})";
    private int _quickMessageDraftRevision;

    public string PendingFriendRequestText => PendingFriendRequest is null
        ? string.Empty
        : $"{PendingFriendRequest.DisplayName} 친구 신청!";

    public string CurrentVersionText { get; } = BuildCurrentVersionText();

    private static string BuildCurrentVersionText()
    {
        var asm = typeof(PetWindowViewModel).Assembly;
        var infoAttr = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(infoAttr))
        {
            var clean = infoAttr.Split('+')[0];
            return $"현재 버전 v{clean}";
        }

        var ver = asm.GetName().Version?.ToString(3) ?? "0.1.0";
        return $"현재 버전 v{ver}";
    }

    private readonly object _appearanceSaveLock = new();
    private readonly AsyncOperationLifetime _profileWork = new();
    private readonly Lazy<Task> _shutdownProfileSaves;
    private CancellationTokenSource? _appearanceSaveCts;
    private bool _appearanceSavePending;
    private bool _suppressStartupToggleEvent;
    private bool _suppressSliderSideEffects;

    private readonly SnowballInteractionService? _snowballs;

    public PetWindowViewModel(
        IAppDataStore store,
        SocialCoordinatorService social,
        SnowballInteractionService? snowballs = null,
        PomodoroTimerService? pomodoro = null,
        UpdateService? updates = null)
    {
        _store = store;
        _snowballs = snowballs;
        _pomodoro = pomodoro;
        _updates = updates;
        if (_updates is not null)
        {
            _updateDispatcher = Dispatcher.FromThread(Thread.CurrentThread);
            _updates.StateChanged += OnUpdateStateChanged;
            SyncUpdateState();
        }
        _shutdownProfileSaves = new Lazy<Task>(ShutdownProfileSavesAsync);
        _social = social;
        _bubbleStack.Changed += (_, _) => IsBubbleVisible = _bubbleStack.HasItems;
        if (_pomodoro is not null)
        {
            _pomodoro.Tick += OnPomodoroTick;
            _pomodoro.FocusStateChanged += OnPomodoroFocusStateChanged;
            RefreshPomodoroOverlayState();
        }
    }

    private void OnPomodoroTick(object? sender, EventArgs e) => RefreshPomodoroOverlayState();
    private void OnPomodoroFocusStateChanged(object? sender, bool isFocusing) => RefreshPomodoroOverlayState();

    public void RefreshPomodoroOverlayState()
    {
        if (_pomodoro is null)
        {
            return;
        }

        var ratio = PomodoroCircularDialMath.ComputeDisplayProgressRatio(_pomodoro);
        PomodoroOverlayProgressRatio = ratio;
        PomodoroOverlayArcBrush = _pomodoro.CurrentPhase == PomodoroPhase.Break
            ? PomodoroOverlayBreakBrush
            : PomodoroOverlayFocusBrush;
        PomodoroOverlayArcGeometry = PomodoroCircularDialMath.BuildArcGeometry(
            PomodoroOverlayCenter, PomodoroOverlayCenter, PomodoroOverlayRadius, ratio);

        var rem = _pomodoro.Remaining;
        var timeText = $"{(int)rem.TotalMinutes:00}:{rem.Seconds:00}";
        PomodoroOverlayTooltip = !_pomodoro.IsDurationLocked
            ? $"뽀모도로 타이머 열기 (설정 {_pomodoro.FocusMinutes}분)"
            : _pomodoro.CurrentPhase == PomodoroPhase.Focus
                ? (_pomodoro.IsRunning
                    ? $"뽀모도로 집중 중 · {timeText} 남음 (클릭하여 열기)"
                    : $"뽀모도로 일시정지 · {timeText} 남음 (클릭하여 열기)")
                : (_pomodoro.IsRunning
                    ? $"뽀모도로 휴식 중 · {timeText} 남음 (클릭하여 열기)"
                    : $"뽀모도로 휴식 대기 · {timeText} (클릭하여 열기)");
    }

    public void MarkPomodoroFeatureBadgeSeen()
    {
        if (CurrentProfile.HasSeenPomodoroFeatureBadge)
        {
            if (ShowPomodoroFeatureBadge)
            {
                ShowPomodoroFeatureBadge = false;
            }

            return;
        }

        CurrentProfile.HasSeenPomodoroFeatureBadge = true;
        ShowPomodoroFeatureBadge = false;
        _ = SaveProfileTrackedAsync();
    }

    private static Brush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public void ApplyProfile(PetProfile profile)
    {
        CurrentProfile = profile;
        PetName = profile.PetName;

        _suppressSliderSideEffects = true;
        try
        {
            PetScale = PetProfile.NormalizePetScale(profile.PetScale);
            PetOpacity = PetDisplayLayoutHelper.NormalizeOpacity(
                profile.PetOpacity <= 0 ? PetDisplayLayoutHelper.MaxPetOpacity : profile.PetOpacity);
        }
        finally
        {
            _suppressSliderSideEffects = false;
        }

        TrayTooltip = $"{profile.PetName} · Lv.{profile.SoloProgress.Level}";
        ShowPomodoroFeatureBadge = !profile.HasSeenPomodoroFeatureBadge;
        RefreshPomodoroOverlayState();
        ProfileUpdated?.Invoke(this, profile);
        PetScaleChanged?.Invoke(this, PetScale);
        PetOpacityChanged?.Invoke(this, PetOpacity);
    }

    partial void OnPetScaleChanged(double value)
    {
        OnPropertyChanged(nameof(PetScalePercentText));
        if (_suppressSliderSideEffects) return;

        var clamped = PetProfile.NormalizePetScale(value);
        CurrentProfile.PetScale = clamped;
        PetScaleChanged?.Invoke(this, clamped);
        ScheduleDebouncedProfileSave();
    }

    partial void OnPetOpacityChanged(double value)
    {
        OnPropertyChanged(nameof(PetOpacityPercentText));
        if (_suppressSliderSideEffects) return;

        var clamped = PetDisplayLayoutHelper.NormalizeOpacity(value);
        CurrentProfile.PetOpacity = clamped;
        PetOpacityChanged?.Invoke(this, clamped);
        ScheduleDebouncedProfileSave();
    }

    private void ScheduleDebouncedProfileSave()
    {
        lock (_appearanceSaveLock)
        {
            var operation = _profileWork.TryBegin();
            if (operation is null)
            {
                return;
            }

            _appearanceSaveCts?.Cancel();
            _appearanceSaveCts?.Dispose();
            _appearanceSaveCts = new CancellationTokenSource();
            _appearanceSavePending = true;
            _ = SaveAppearanceAfterDebounceAsync(CurrentProfile, operation, _appearanceSaveCts.Token);
        }
    }

    private async Task SaveAppearanceAfterDebounceAsync(PetProfile profile, IDisposable operation, CancellationToken token)
    {
        using (operation)
        {
            try
            {
                await Task.Delay(350, token).ConfigureAwait(false);
                lock (_appearanceSaveLock)
                {
                    token.ThrowIfCancellationRequested();
                    _appearanceSavePending = false;
                }
                await _store.SaveProfileAsync(profile).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceError("방문 창 외형 저장 실패: {0}", ex);
            }
        }
    }

    public ValueTask DisposeAsync() => new(_shutdownProfileSaves.Value);

    private async Task ShutdownProfileSavesAsync()
    {
        if (_updates is not null)
        {
            _updates.StateChanged -= OnUpdateStateChanged;
        }

        if (_pomodoro is not null)
        {
            _pomodoro.Tick -= OnPomodoroTick;
            _pomodoro.FocusStateChanged -= OnPomodoroFocusStateChanged;
        }

        Task drain;
        PetProfile? unsavedProfile;
        lock (_appearanceSaveLock)
        {
            drain = _profileWork.DrainAsync();
            unsavedProfile = _appearanceSavePending ? CurrentProfile : null;
            _appearanceSaveCts?.Cancel();
            _appearanceSaveCts?.Dispose();
            _appearanceSaveCts = null;
        }

        await drain.ConfigureAwait(false);
        if (unsavedProfile is not null)
        {
            await _store.SaveProfileAsync(unsavedProfile).ConfigureAwait(false);
        }
    }

    private async Task SaveProfileTrackedAsync()
    {
        using var operation = _profileWork.TryBegin();
        if (operation is not null)
        {
            await _store.SaveProfileAsync(CurrentProfile).ConfigureAwait(false);
        }
    }

    public void RefreshQuickOverlayEntries()
    {
        var peers = _social.DiscoveredPeers;
        var friends = _social.Friends;
        var followingId = _social.FollowingHostPetId;
        var isLocalFocusing = _social.IsLocalFocusing;

        var items = new List<QuickOverlayEntry>();

        foreach (var f in friends)
        {
            var live = peers.FirstOrDefault(p => string.Equals(p.PetId, f.PetId, StringComparison.OrdinalIgnoreCase));
            if (live is null || live.RequiresUpdate)
            {
                continue;
            }

            items.Add(new QuickOverlayEntry
            {
                PetId = f.PetId,
                DisplayTitle = SocialCoordinatorService.FormatDisplayName(
                    string.IsNullOrWhiteSpace(live.OwnerName) ? f.OwnerName : live.OwnerName,
                    string.IsNullOrWhiteSpace(live.PetName) ? f.PetName : live.PetName),
                IsMusicActive = live.IsMusicActive,
                IsSleeping = live.IsSleeping,
                IsFocusing = live.IsFocusing,
                IsLocalFocusing = isLocalFocusing,
                IsFollowingMusic = string.Equals(followingId, f.PetId, StringComparison.OrdinalIgnoreCase),
                IsVisitingDesktop = IsVisitorActive(f.PetId),
                Friend = f,
                Snowball = _snowballs?.ForFriend(f),
                Peer = live
            });
        }
        // 친구 상태가 바뀌어도 같은 행과 입력 컨트롤을 유지한다.
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var existing = QuickOverlayEntries.Skip(index)
                .FirstOrDefault(entry => string.Equals(entry.PetId, item.PetId, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                QuickOverlayEntries.Insert(index, item);
                continue;
            }

            existing.DisplayTitle = item.DisplayTitle;
            existing.IsMusicActive = item.IsMusicActive;
            existing.IsSleeping = item.IsSleeping;
            existing.IsFocusing = item.IsFocusing;
            existing.IsLocalFocusing = item.IsLocalFocusing;
            existing.IsFollowingMusic = item.IsFollowingMusic;
            existing.IsVisitingDesktop = item.IsVisitingDesktop;
            existing.Friend = item.Friend;
            existing.Peer = item.Peer;
            var oldIndex = QuickOverlayEntries.IndexOf(existing);
            if (oldIndex != index)
            {
                QuickOverlayEntries.Move(oldIndex, index);
            }
        }

        while (QuickOverlayEntries.Count > items.Count)
        {
            QuickOverlayEntries.RemoveAt(QuickOverlayEntries.Count - 1);
        }

        // 연결이 끊기거나 집중 모드로 차단된 친구는 빠른 메시지 입력을 닫는다.
        if (QuickMessageTarget is { } target
            && (!QuickOverlayEntries.Contains(target) || target.IsInteractionBlocked))
        {
            QuickMessageTarget = null;
        }

        OnPropertyChanged(nameof(HasNoQuickOverlayEntries));
        OnPropertyChanged(nameof(QuickOverlayHeaderText));
    }

    public async Task SaveWindowPositionAsync(double left, double top)
    {
        CurrentProfile.LastWindowLeft = left;
        CurrentProfile.LastWindowTop = top;
        await SaveProfileTrackedAsync();
    }

    /// <summary>
    /// 펫 말풍선에 메시지 한 줄을 추가한다. 새 메시지가 와도 기존 줄을 즉시 지우지 않고
    /// 최대 <see cref="MaxBubbleLines"/>개까지 쌓으며, 각 메시지는 자신의 표시 시간이 지나면 사라진다.
    /// UI 스레드에서 호출해야 한다.
    /// </summary>
    public void ShowBubble(string message, int durationMs = 2600)
        => ShowBubble(new BubbleLine(message), durationMs);

    public void ShowBubble(BubbleLine line, int durationMs)
    {
        _bubbleStack.Add(line, durationMs);
    }

    public void DisposeBubbles() => _bubbleStack.Dispose();

    // 클릭 투과 집중 모드 UI 제거에 따라 관련 핸들러 주석 처리
    // partial void OnIsClickThroughEnabledChanged(bool value)
    // {
    //     ClickThroughChanged?.Invoke(this, value);
    //     ShowBubble(value ? "클릭 투과 집중 모드 ON (트레이에서 해제)" : "클릭 투과 집중 모드 OFF", 2400);
    // }

    partial void OnPendingFriendRequestChanged(PendingFriendRequest? value)
    {
        OnPropertyChanged(nameof(HasPendingFriendRequest));
        OnPropertyChanged(nameof(PendingFriendRequestText));
    }

    partial void OnQuickMessageTargetChanged(QuickOverlayEntry? oldValue, QuickOverlayEntry? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsComposingMessage = false;
        }

        QuickMessageText = string.Empty;
        if (newValue is not null)
        {
            newValue.IsComposingMessage = true;
        }
    }

    partial void OnQuickMessageTextChanged(string value) => _quickMessageDraftRevision++;

    partial void OnIsStartupAutoRunEnabledChanged(bool value)
    {
        if (_suppressStartupToggleEvent)
        {
            return;
        }

        StartupAutoRunToggled?.Invoke(this, value);
    }

    public void InitializeStartupState(bool isEnabled)
    {
        _suppressStartupToggleEvent = true;
        try
        {
            IsStartupAutoRunEnabled = isEnabled;
        }
        finally
        {
            _suppressStartupToggleEvent = false;
        }
    }

    private void OnUpdateStateChanged(object? sender, EventArgs e)
    {
        if (_updateDispatcher is null || _updateDispatcher.CheckAccess())
        {
            SyncUpdateState();
        }
        else if (!_updateDispatcher.HasShutdownStarted)
        {
            _updateDispatcher.BeginInvoke(() => SyncUpdateState());
        }
    }

    private void SyncUpdateState()
    {
        if (_updates is null)
        {
            return;
        }

        var state = _updates.State;
        var version = state.Version;
        CanInstallUpdate = state.CanInstall;
        HasUpdateOffer = state.CanInstall;
        UpdateMenuHeader = state.Phase == UpdatePhase.Ready
            ? $"새 버전 v{version} 준비됨 · 재시작하여 적용"
            : $"새 버전 v{version} 업데이트";
        UpdateBubbleText = state.Message ?? string.Empty;
        IsUpdateBubbleVisible = version is not null && version != _dismissedUpdateVersion;
    }

    [RelayCommand]
    private void DismissUpdateBubble()
    {
        _dismissedUpdateVersion = _updates?.State.Version;
        IsUpdateBubbleVisible = false;
    }

    public async Task ApplyDisplaySettingsAsync(double scale, double opacity, bool persist = true)
    {
        var clampedScale = PetProfile.NormalizePetScale(scale);
        var clampedOpacity = PetDisplayLayoutHelper.NormalizeOpacity(opacity);

        PetScale = clampedScale;
        PetOpacity = clampedOpacity;
        CurrentProfile.PetScale = clampedScale;
        CurrentProfile.PetOpacity = clampedOpacity;
        PetScaleChanged?.Invoke(this, clampedScale);
        PetOpacityChanged?.Invoke(this, clampedOpacity);

        if (persist)
        {
            await SaveProfileTrackedAsync();
            ProfileUpdated?.Invoke(this, CurrentProfile);
        }
    }

    [RelayCommand]
    private void OpenQuickMessage(QuickOverlayEntry? entry)
    {
        if (entry?.Friend is null)
        {
            return;
        }

        if (entry.IsInteractionBlocked || _social.IsInteractionBlocked(entry.Friend.PetId))
        {
            QuickMessageTarget = null;
            ShowBubble(FriendOperationErrorCodes.PomodoroFocusMessage, 2400);
            return;
        }

        QuickMessageTarget = string.Equals(QuickMessageTarget?.PetId, entry.PetId, StringComparison.OrdinalIgnoreCase)
            ? null
            : entry;
    }

    [RelayCommand]
    private async Task SendQuickMessageAsync()
    {
        if (QuickMessageTarget?.Friend is not { } friend)
        {
            return;
        }

        if (_social.IsInteractionBlocked(friend.PetId))
        {
            QuickMessageTarget = null;
            ShowBubble(FriendOperationErrorCodes.PomodoroFocusMessage, 2400);
            return;
        }

        var trimmed = (QuickMessageText ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var revision = _quickMessageDraftRevision;
        var result = await _social.SendBubbleMessageDetailedAsync(friend, trimmed);
        if (result.Succeeded)
        {
            if (revision == _quickMessageDraftRevision)
            {
                QuickMessageText = string.Empty;
            }
        }
        else
        {
            ShowBubble(
                result.IsPomodoroFocusBlocked
                    ? FriendOperationErrorCodes.PomodoroFocusMessage
                    : "메시지 전송 실패",
                2200);
        }
    }

    [RelayCommand]
    private async Task QuickPetFriendAsync(QuickOverlayEntry? entry)
    {
        if (entry?.Friend is not { } friend)
        {
            return;
        }

        var result = await _social.SendPetInteractionAsync(friend);
        if (result == PetInteractionSendResult.PomodoroFocusBlocked)
        {
            ShowBubble(FriendOperationErrorCodes.PomodoroFocusMessage, 2400);
        }
        else if (result == PetInteractionSendResult.Unavailable)
        {
            ShowBubble("인사하기 전송 실패", 2000);
        }
    }

    [RelayCommand]
    private async Task QuickToggleMusicFollowAsync(QuickOverlayEntry? entry)
    {
        if (entry?.Friend is null)
        {
            return;
        }

        if (!entry.IsFollowingMusic && (entry.IsInteractionBlocked || _social.IsInteractionBlocked(entry.Friend.PetId)))
        {
            ShowBubble(FriendOperationErrorCodes.PomodoroFocusMessage, 2400);
            return;
        }

        if (!entry.CanFollowMusic)
        {
            return;
        }

        var result = await _social.ToggleMusicFollowDetailedAsync(entry.Friend);
        if (result.Succeeded)
        {
            RefreshQuickOverlayEntries();
        }
        else
        {
            ShowBubble(
                result.IsPomodoroFocusBlocked
                    ? FriendOperationErrorCodes.PomodoroFocusMessage
                    : "노래 따라하기 요청 실패",
                2000);
        }
    }

    [RelayCommand]
    private void QuickToggleVisitFriend(QuickOverlayEntry? entry)
    {
        if (entry?.Friend is null)
        {
            return;
        }

        if (!IsVisitorActive(entry.Friend.PetId) && (entry.IsInteractionBlocked || _social.IsInteractionBlocked(entry.Friend.PetId)))
        {
            ShowBubble(FriendOperationErrorCodes.PomodoroFocusMessage, 2400);
            return;
        }

        var appearance = (entry.Peer?.Appearance ?? entry.Friend.CachedAppearance ?? new PetAppearance()).Clone();
        ToggleVisitFriendRequested?.Invoke(this, (entry.Friend, appearance));
    }

    public void EnqueueFriendRequest(PendingFriendRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RequestId))
        {
            return;
        }

        if (_social.Friends.Any(f => string.Equals(f.PetId, request.RequesterPetId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (PendingFriendRequest is not null)
        {
            if (string.Equals(PendingFriendRequest.RequestId, request.RequestId, StringComparison.Ordinal)
                || string.Equals(PendingFriendRequest.RequesterPetId, request.RequesterPetId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_pendingFriendRequestQueue.Any(q =>
                string.Equals(q.RequestId, request.RequestId, StringComparison.Ordinal)
                || string.Equals(q.RequesterPetId, request.RequesterPetId, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _pendingFriendRequestQueue.Enqueue(request);
            return;
        }

        PendingFriendRequest = request;
        PendingFriendRequestStatusText = string.Empty;
        CanRetryPendingFriendRequest = false;
    }

    private void MoveToNextPendingFriendRequest()
    {
        var currentFriends = _social.Friends;
        while (_pendingFriendRequestQueue.Count > 0)
        {
            var next = _pendingFriendRequestQueue.Dequeue();
            if (next.IsExpired)
            {
                continue;
            }

            if (currentFriends.Any(f => string.Equals(f.PetId, next.RequesterPetId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            PendingFriendRequest = next;
            PendingFriendRequestStatusText = string.Empty;
            CanRetryPendingFriendRequest = false;
            return;
        }

        PendingFriendRequest = null;
        PendingFriendRequestStatusText = string.Empty;
        CanRetryPendingFriendRequest = false;
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (_updates is null)
        {
            return;
        }

        _dismissedUpdateVersion = null;
        var message = _updates.State.IsBusy ? null : await _updates.CheckForUpdatesAsync();
        SyncUpdateState();
        if (message is not null)
        {
            ShowBubble(message, 3600);
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private Task InstallUpdateAsync() => _updates?.InstallUpdateAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private void OpenHub()
        => HubNavigationRequested?.Invoke(this, new NavigationRequest(HubPage.Home));

    [RelayCommand]
    private void OpenSettings()
        => HubNavigationRequested?.Invoke(this, new NavigationRequest(HubPage.Settings));

    [RelayCommand]
    private void OpenGrowth()
        => HubNavigationRequested?.Invoke(this, new NavigationRequest(HubPage.Growth));

    [RelayCommand]
    private void OpenPomodoro()
    {
        MarkPomodoroFeatureBadgeSeen();
        HubNavigationRequested?.Invoke(this, new NavigationRequest(HubPage.Pomodoro));
    }

    [RelayCommand]
    private void OpenGames()
        => HubNavigationRequested?.Invoke(this, new NavigationRequest(HubPage.Games));

    [RelayCommand]
    private void LaunchChristmasTree() => LaunchChristmasTreeRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenSocial()
        => HubNavigationRequested?.Invoke(this, new NavigationRequest(HubPage.Friends));

    [RelayCommand]
    private async Task AcceptFriendRequestAsync()
    {
        var req = PendingFriendRequest;
        if (req is null || IsRespondingFriendRequest)
        {
            return;
        }

        IsRespondingFriendRequest = true;
        CanRetryPendingFriendRequest = false;
        PendingFriendRequestStatusText = "수락 처리 중...";

        FriendOperationResult result;
        try
        {
            result = await _social.RespondFriendRequestDetailedAsync(req, accept: true);
        }
        finally
        {
            IsRespondingFriendRequest = false;
        }

        if (!ReferenceEquals(PendingFriendRequest, req))
        {
            return;
        }

        if (result.Succeeded)
        {
            MoveToNextPendingFriendRequest();
            return;
        }

        var errorText = SocialCoordinatorService.FormatFriendOperationFailure(result, isAccept: true, req.DisplayName);
        if (result.IsTransient)
        {
            CanRetryPendingFriendRequest = true;
            PendingFriendRequestStatusText = "수락 실패 · 다시 시도 가능";
            ShowBubble(errorText, 4500);
            return;
        }

        // 만료되었거나 알 수 없는 신청, 이미 친구, 확정적 인증 오류는 재수락하지 않고 요청을 정리한 뒤 안내한다.
        MoveToNextPendingFriendRequest();
        ShowBubble(errorText, 4500);
    }

    [RelayCommand]
    private async Task RejectFriendRequestAsync()
    {
        var req = PendingFriendRequest;
        if (req is null || IsRespondingFriendRequest)
        {
            return;
        }

        IsRespondingFriendRequest = true;
        PendingFriendRequestStatusText = "거절 처리 중...";
        try
        {
            var result = await _social.RespondFriendRequestDetailedAsync(req, accept: false);
            if (!result.Succeeded && !FriendOperationErrorCodes.IsExpiredOrUnknown(result.ErrorCode))
            {
                ShowBubble(SocialCoordinatorService.FormatFriendOperationFailure(result, isAccept: false, req.DisplayName), 3200);
            }
        }
        finally
        {
            IsRespondingFriendRequest = false;
            if (ReferenceEquals(PendingFriendRequest, req))
            {
                MoveToNextPendingFriendRequest();
            }
        }
    }

    public async Task SaveCurrentProfileAsync()
    {
        await SaveProfileTrackedAsync();
        TrayTooltip = $"{CurrentProfile.PetName} · Lv.{CurrentProfile.SoloProgress.Level}";
        ProfileProgressUpdated?.Invoke(this, CurrentProfile);
    }

    // 클릭 투과 집중 모드 UI 제거에 따라 관련 커맨드 주석 처리
    // [RelayCommand]
    // private void ToggleClickThrough() => IsClickThroughEnabled = !IsClickThroughEnabled;

    [RelayCommand]
    private void OpenCustomization()
        => HubNavigationRequested?.Invoke(this, new NavigationRequest(HubPage.Appearance));

    [RelayCommand]
    private void ResetPosition() => ResetPositionRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ExitApplication() => ExitRequested?.Invoke(this, EventArgs.Empty);
}
