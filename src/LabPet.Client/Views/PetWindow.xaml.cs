using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using LabPet.Client.Audio;
using LabPet.Client.Interop;
using LabPet.Client.Rendering;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.ViewModels;
using LabPet.Shared.Models;
using LabPet.Shared.Transport;

namespace LabPet.Client.Views;

public partial class PetWindow : Window, IMessageBubbleTarget
{
    private static readonly TimeSpan ActiveFrameInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan IdleFrameInterval = TimeSpan.FromMilliseconds(40);
    public static readonly TimeSpan SleepIdleThreshold = TimeSpan.FromMinutes(5);
    private const double DragThresholdPixels = 5.0;
    private const double FriendOverlayGap = 4.0;
    private const double FriendOverlayTopInset = 12.0;
    private const double SpeechBubbleBottomOverlap = 8.0;

    /// <summary>
    /// 친구 메시지·소셜 알림 말풍선 줄의 표시 시간(ms). 새 메시지가 와도 기존 줄은 이 시간 동안 유지된다.
    /// </summary>
    private const int SocialNotificationBubbleDurationMs = 8000;

    private readonly PetWindowViewModel _viewModel;
    private readonly IPetMeshFactory _meshFactory;
    private readonly PetPortraitRenderer _portraitRenderer;
    private readonly PetAnimationController _animator;
    private readonly IAudioAnalyzer _audioAnalyzer;
    private readonly GlobalActivityHookService _activityHook;
    private readonly SoloProgressionService _progression;
    private readonly PomodoroTimerService _pomodoro;
    private readonly SocialCoordinatorService _social;
    private readonly ISystemActivityStateProvider _activityState;

    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _activityFlushTimer;
    private readonly DispatcherTimer _uptimeTimer;
    private readonly DispatcherTimer _overlayCloseTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrameSeconds;
    private double _lastMusicPublishSeconds;
    private double _lastIdleCheckSeconds = -1.0;
    private bool _lastReportedMusicActive;
    private bool _isLocalSleeping;
    private Point3D _nameplateAnchor;
    private FrameworkElement? _messageEditorToClose;
    private PetRigHandle? _currentRig;

    // 마우스 클릭(<=5px) vs 창 드래그(>5px) vs 빠른 좌우 흔들기 판정 상태
    private bool _isPointerPressed;
    private bool _isDraggingWindow;
    private Point _pressScreenPoint;
    private Point _lastScreenPoint;
    private int _lastHorizontalDirection;
    private int _shakeReversalCount;
    private double _shakeWindowStartSeconds;
    private bool _dizzyTriggeredDuringDrag;
    private bool _isClosingOrClosed;

    public bool IsAvailableForMessages => !_isClosingOrClosed && IsVisible;

    public PetWindow(
        PetWindowViewModel viewModel,
        IPetMeshFactory meshFactory,
        PetPortraitRenderer portraitRenderer,
        PetAnimationController animator,
        IAudioAnalyzer audioAnalyzer,
        GlobalActivityHookService activityHook,
        SoloProgressionService progression,
        PomodoroTimerService pomodoro,
        SocialCoordinatorService social,
        ISystemActivityStateProvider? activityState = null)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _meshFactory = meshFactory;
        _portraitRenderer = portraitRenderer;
        _animator = animator;
        _audioAnalyzer = audioAnalyzer;
        _activityHook = activityHook;
        _progression = progression;
        _pomodoro = pomodoro;
        _social = social;
        _activityState = activityState ?? new SystemActivityStateMonitor(hookSystemEvents: false);
        DataContext = _viewModel;

        TrayIcon.Icon = CreateTrayIcon();
        TrayIcon.ContextMenu = PetContextMenuFactory.Create(_viewModel, includeStartupAutoRun: true);
        PetViewport.ContextMenu = PetContextMenuFactory.Create(_viewModel, includeStartupAutoRun: false);
        LightsVisual.Content = _meshFactory.CreateStudioLights();
        FriendOverlayPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(PetLayout);
            Rect? targetBoundsPhysical = null;
            if (PresentationSource.FromVisual(PetLayout) is not null)
            {
                var anchorOffset = FriendOverlayPopup.PlacementRectangle.IsEmpty
                    ? new Point(0, 0)
                    : FriendOverlayPopup.PlacementRectangle.TopLeft;
                var topLeftPhysical = PetLayout.PointToScreen(anchorOffset);
                targetBoundsPhysical = new Rect(topLeftPhysical, targetSize);
            }

            Rect? workAreaPhysical = Win32WindowHelper.TryGetNearestWorkArea(this, out var area)
                ? area
                : null;

            return GetFriendOverlayPlacements(
                popupSize,
                targetSize,
                dpi,
                FriendOverlayCard.Margin,
                messageRowHeight: 0,
                targetBoundsPhysical,
                workAreaPhysical);
        };
        SpeechBubblePopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            GetSpeechBubblePlacements(popupSize, targetSize, VisualTreeHelper.GetDpi(PetLayout));

        SpeechBubblePopup.Opened += (_, _) => SpeechBubbleLayoutChanged?.Invoke(this, EventArgs.Empty);
        SpeechBubblePopup.Closed += (_, _) => SpeechBubbleLayoutChanged?.Invoke(this, EventArgs.Empty);

        Loaded += (_, _) =>
        {
            UpdateNameplatePosition();
            RepositionSpeechBubblePopup();
        };
        LocationChanged += (_, _) =>
        {
            UpdateNameplatePosition();
            RepositionFriendOverlayPopup();
            RepositionSpeechBubblePopup();
        };
        SizeChanged += (_, e) =>
        {
            FriendOverlayPopup.PlacementRectangle = new Rect(e.NewSize);
            RepositionFriendOverlayPopup();
            RepositionSpeechBubblePopup();
        };
        PetViewport.SizeChanged += (_, _) =>
        {
            UpdateNameplatePosition();
            RepositionSpeechBubblePopup();
        };

        _viewModel.ProfileUpdated += OnProfileUpdated;
        _viewModel.PetScaleChanged += OnPetScaleChanged;
        // 클릭 투과 집중 모드 UI 제거에 따라 관련 구독 주석 처리
        // _viewModel.ClickThroughChanged += OnClickThroughChanged;
        _viewModel.ResetPositionRequested += OnResetPositionRequested;
        _viewModel.ExitRequested += OnExitRequested;
        _activityHook.KeyTyped += OnGlobalKeyTyped;
        _pomodoro.FocusStateChanged += OnPomodoroFocusStateChanged;
        _pomodoro.FocusCompletedWithDuration += OnPomodoroFocusCompleted;
        _pomodoro.BreakCompleted += OnPomodoroBreakCompleted;
        _progression.Economy.LevelUpCompleted += OnEconomyLevelUpCompleted;
        if (_activityState is SystemActivityStateMonitor activityMonitor)
        {
            activityMonitor.ActivityClockResetRequested += OnActivityClockResetRequested;
        }
        _social.StateChanged += OnSocialStateChanged;
        _social.FriendRequestArrived += OnSocialFriendRequestArrived;
        _social.SocialNotification += OnSocialNotification;
        _social.PetReactionTriggered += OnSocialPetReactionTriggered;

        _overlayCloseTimer = new DispatcherTimer
        {
            Interval = QuickMessageInputBehavior.DismissDelay
        };
        _overlayCloseTimer.Tick += OnOverlayCloseTick;

        SourceInitialized += (_, _) =>
        {
            Win32WindowHelper.ApplyToolWindowStyle(this);
            // 클릭 투과 집중 모드 UI 제거에 따라 관련 호출 주석 처리
            // Win32WindowHelper.SetClickThrough(this, _viewModel.IsClickThroughEnabled);
        };

        _audioAnalyzer.Start();
        _activityHook.Start();

        _frameTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _frameTimer.Tick += OnFrameTick;
        _frameTimer.Start();

        _activityFlushTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _activityFlushTimer.Tick += OnActivityFlushTick;
        _activityFlushTimer.Start();

        _uptimeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _uptimeTimer.Tick += OnUptimeTick;
        _uptimeTimer.Start();

        Closing += (_, _) => _isClosingOrClosed = true;

        Closed += (_, _) =>
        {
            _isClosingOrClosed = true;
            _frameTimer.Stop();
            _activityFlushTimer.Stop();
            _uptimeTimer.Stop();
            _overlayCloseTimer.Stop();
            _audioAnalyzer.Stop();
            _activityHook.Stop();
            TrayIcon.Dispose();
            _animator.DetachRig();
            PetVisual.Content = null;
            LightsVisual.Content = null;
            _portraitRenderer.ClearCache();
            _currentRig = null;
            _viewModel.DisposeBubbles();
            _viewModel.ProfileUpdated -= OnProfileUpdated;
            _viewModel.PetScaleChanged -= OnPetScaleChanged;
            // 클릭 투과 집중 모드 UI 제거에 따라 관련 구독 해제 주석 처리
            // _viewModel.ClickThroughChanged -= OnClickThroughChanged;
            _viewModel.ResetPositionRequested -= OnResetPositionRequested;
            _viewModel.ExitRequested -= OnExitRequested;
            _activityHook.KeyTyped -= OnGlobalKeyTyped;
            _pomodoro.FocusStateChanged -= OnPomodoroFocusStateChanged;
            _pomodoro.FocusCompletedWithDuration -= OnPomodoroFocusCompleted;
            _pomodoro.BreakCompleted -= OnPomodoroBreakCompleted;
            _progression.Economy.LevelUpCompleted -= OnEconomyLevelUpCompleted;
            if (_activityState is SystemActivityStateMonitor activityMonitor)
            {
                activityMonitor.ActivityClockResetRequested -= OnActivityClockResetRequested;
            }
            _social.StateChanged -= OnSocialStateChanged;
            _social.FriendRequestArrived -= OnSocialFriendRequestArrived;
            _social.SocialNotification -= OnSocialNotification;
            _social.PetReactionTriggered -= OnSocialPetReactionTriggered;
        };
    }

    public void InitializeWithProfile(PetProfile profile)
    {
        _viewModel.ApplyProfile(profile);
        RestoreOrPlaceDefaultPosition(profile);
        _ = _social.StartAsync(profile);
        _viewModel.ShowBubble("안녕!", 2600);
    }

    private void OnProfileUpdated(object? sender, PetProfile profile)
    {
        if (_currentRig is not null &&
            _currentRig.TryUpdateAppearance(profile.Appearance))
        {
            _animator.UpdateAccentColor(profile.Appearance.AccentColorHex);
        }
        else
        {
            var rig = _meshFactory.BuildPetRig(profile.Appearance);
            _currentRig = rig;
            PetVisual.Content = rig.RootModel;
            _nameplateAnchor = rig.NameplateAnchor;
            UpdateNameplatePosition();
            _animator.AttachRig(rig, profile.Appearance.AccentColorHex);
        }

        _ = _social.UpdateLocalPresenceAsync(profile, _lastReportedMusicActive, _isLocalSleeping, _pomodoro.IsFocusActive);
    }

    internal static Size ComputeViewportSize(double scale)
        => PetDisplayLayoutHelper.ComputeViewportSize(scale);

    private void OnPetScaleChanged(object? sender, double scale)
    {
        // 슬라이더를 끌 때는 가운데를 유지하고, 메뉴에서 변경할 때는 우하단을 유지한다.
        var isDraggingSlider = IsLoaded && (FriendOverlayCard.IsMouseCaptureWithin || PetControls.IsMouseCaptureWithin);
        PetDisplayLayoutHelper.ApplyScaleAndSize(
            this,
            PetViewport,
            scale,
            _viewModel.PetOpacity,
            PetNameplateRow.Height.Value,
            horizontalAnchorRatio: isDraggingSlider ? 0.5 : 1.0);
        UpdateNameplatePosition();
        RepositionSpeechBubblePopup();
    }

    internal event EventHandler? SpeechBubbleLayoutChanged;

    internal Point? GetInviteCardHead()
    {
        if (!IsAvailableForMessages || PresentationSource.FromVisual(PetViewport) is null
            || _currentRig is not { } rig || rig.BodyBounds.IsEmpty) return null;
        var bounds = rig.BodyBounds;
        var top = new Point3D(0, bounds.Y + bounds.SizeY, bounds.Z + bounds.SizeZ * 0.65);
        if (!PetViewport.IsAncestorOf(PetVisual)
            || !PetVisual.TransformToAncestor(PetViewport).TryTransform(top, out var local)
            || !double.IsFinite(local.X) || !double.IsFinite(local.Y)) return null;
        var physical = PetViewport.PointToScreen(local);
        return double.IsFinite(physical.X) && double.IsFinite(physical.Y) ? physical : null;
    }

    internal Rect GetInviteCardObstacle() =>
        SpeechBubblePopup.IsOpen && Win32WindowHelper.TryGetVisualWindowPhysicalBounds(SpeechBubbleContent, out var bounds)
            ? bounds : Rect.Empty;

    private static CustomPopupPlacement[] GetSpeechBubblePlacements(Size popupSize, Size targetSize, DpiScale dpi)
    {
        var centerX = (targetSize.Width - popupSize.Width) / 2.0;
        var overlapY = SpeechBubbleBottomOverlap * dpi.DpiScaleY;
        return
        [
            new(new Point(centerX, -popupSize.Height + overlapY), PopupPrimaryAxis.Horizontal),
            new(new Point(centerX, targetSize.Height), PopupPrimaryAxis.Horizontal)
        ];
    }

    private void SpeechBubbleContent_SizeChanged(object sender, SizeChangedEventArgs e)
        => RepositionSpeechBubblePopup();

    private void RepositionSpeechBubblePopup()
    {
        if (!SpeechBubblePopup.IsOpen)
        {
            return;
        }

        // WPF Popup은 PlacementTarget이 이동하거나 내부 크기가 변할 때 offset을 미세 갱신해야 즉시 재배치된다.
        var offset = SpeechBubblePopup.HorizontalOffset;
        SpeechBubblePopup.HorizontalOffset = offset + 0.01;
        SpeechBubblePopup.HorizontalOffset = offset;
        SpeechBubbleLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateNameplatePosition()
    {
        if (PetDisplayLayoutHelper.TryComputeNameplateOffsetY(PetViewport, PetVisual, _nameplateAnchor, out var offsetDip))
        {
            PetNameplateOffset.Y = offsetDip;
        }
    }

    private void OnSocialStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(_viewModel.RefreshQuickOverlayEntries);
    }

    private void PetViewport_MouseEnter(object sender, MouseEventArgs e)
    {
        // 클릭 투과 집중 모드 UI 제거에 따라 _viewModel.IsClickThroughEnabled 분기 주석 처리
        // if (_viewModel.IsClickThroughEnabled || _isDraggingWindow)
        if (_isDraggingWindow)
        {
            return;
        }

        CancelOverlayCloseUnlessEditorLeft();
        _viewModel.RefreshPomodoroOverlayState();
        _viewModel.RefreshQuickOverlayEntries();
        FriendOverlayPopup.IsOpen = true;
        SetPetControlsVisible(true);
    }

    internal static CustomPopupPlacement[] GetFriendOverlayPlacements(
        Size popupSize, Size targetSize, DpiScale dpi, Thickness cardMargin, double messageRowHeight)
        => GetFriendOverlayPlacements(
            popupSize,
            targetSize,
            dpi,
            cardMargin,
            messageRowHeight,
            targetBoundsPhysical: null,
            workAreaPhysical: null);

    internal static CustomPopupPlacement[] GetFriendOverlayPlacements(
        Size popupSize,
        Size targetSize,
        DpiScale dpi,
        Thickness cardMargin,
        double messageRowHeight,
        Rect? targetBoundsPhysical,
        Rect? workAreaPhysical)
    {
        // WPF는 카드의 여백을 뺀 크기를 전달한다. 그림자 여백까지 포함해 창 바깥에 배치한다.
        var popupWidth = popupSize.Width + (cardMargin.Left + cardMargin.Right) * dpi.DpiScaleX;
        var popupHeight = popupSize.Height + (cardMargin.Top + cardMargin.Bottom) * dpi.DpiScaleY;
        var gapX = FriendOverlayGap * dpi.DpiScaleX;
        var gapY = FriendOverlayGap * dpi.DpiScaleY;
        var preferredTop = (messageRowHeight + FriendOverlayTopInset) * dpi.DpiScaleY;
        var centerX = (targetSize.Width - popupWidth) / 2;
        var leftRelX = -popupWidth - gapX;
        var rightRelX = targetSize.Width + gapX;

        if (targetBoundsPhysical is { IsEmpty: false } targetBounds
            && workAreaPhysical is { IsEmpty: false } workArea)
        {
            // 채팅 입력창·슬라이더 등으로 높이가 변해도 펫 위로 이동하지 않고 현재 좌/우 방향을 유지한 채 위·아래 가용 공간으로 확장한다.
            var desiredAbsTop = targetBounds.Top + preferredTop;
            var maxAbsTop = Math.Max(workArea.Top, workArea.Bottom - popupHeight);
            var clampedAbsTop = Math.Clamp(desiredAbsTop, workArea.Top, maxAbsTop);
            var sideTop = clampedAbsTop - targetBounds.Top;

            var leftAbsX = targetBounds.Left + leftRelX;
            var rightAbsX = targetBounds.Left + rightRelX;
            var leftSpaceMargin = leftAbsX - workArea.Left;
            var rightSpaceMargin = workArea.Right - (rightAbsX + popupWidth);
            var preferLeft = leftSpaceMargin >= 0 || leftSpaceMargin >= rightSpaceMargin;

            var desiredAbsCenterX = targetBounds.Left + centerX;
            var maxAbsCenterX = Math.Max(workArea.Left, workArea.Right - popupWidth);
            var clampedCenterRelX = Math.Clamp(desiredAbsCenterX, workArea.Left, maxAbsCenterX) - targetBounds.Left;

            return
            [
                new(new Point(preferLeft ? leftRelX : rightRelX, sideTop), PopupPrimaryAxis.Vertical),
                new(new Point(preferLeft ? rightRelX : leftRelX, sideTop), PopupPrimaryAxis.Vertical),
                new(new Point(clampedCenterRelX, -popupHeight - gapY), PopupPrimaryAxis.Horizontal),
                new(new Point(clampedCenterRelX, targetSize.Height + gapY), PopupPrimaryAxis.Horizontal)
            ];
        }

        var fallbackSideTop = Math.Min(preferredTop, Math.Max(0, targetSize.Height - popupHeight));

        return
        [
            new(new Point(leftRelX, fallbackSideTop), PopupPrimaryAxis.Vertical),
            new(new Point(rightRelX, fallbackSideTop), PopupPrimaryAxis.Vertical),
            new(new Point(centerX, -popupHeight - gapY), PopupPrimaryAxis.Horizontal),
            new(new Point(centerX, targetSize.Height + gapY), PopupPrimaryAxis.Horizontal)
        ];
    }

    private void FriendOverlayCard_SizeChanged(object sender, SizeChangedEventArgs e)
        => RepositionFriendOverlayPopup();

    private void RepositionFriendOverlayPopup()
    {
        if (_isClosingOrClosed || !FriendOverlayPopup.IsOpen)
        {
            return;
        }

        var offset = FriendOverlayPopup.HorizontalOffset;
        FriendOverlayPopup.HorizontalOffset = offset + 0.01;
        FriendOverlayPopup.HorizontalOffset = offset;
    }

    private void OverlaySurface_MouseEnter(object sender, MouseEventArgs e) => CancelOverlayCloseUnlessEditorLeft();

    private void OverlaySurface_MouseLeave(object sender, MouseEventArgs e) => ScheduleOverlayClose();

    private void ScheduleOverlayClose()
    {
        _overlayCloseTimer.Stop();
        _overlayCloseTimer.Start();
    }

    private void CancelOverlayCloseUnlessEditorLeft()
    {
        if (_messageEditorToClose is null)
        {
            _overlayCloseTimer.Stop();
        }
    }

    private void OnOverlayCloseTick(object? sender, EventArgs e)
    {
        _overlayCloseTimer.Stop();

        // 전송 입력창에 키보드 커서가 살아있는 동안에는 마우스가 밖으로 나가도 오버레이를 닫지 않는다.
        if (FriendOverlayCard.IsKeyboardFocusWithin)
        {
            return;
        }

        if (!PetViewport.IsMouseOver && !QuickMessageInputBehavior.IsActive(FriendOverlayCard)
            && !QuickMessageInputBehavior.IsActive(PetControls))
        {
            ClosePetOverlays();
            return;
        }

        var editor = _messageEditorToClose;
        _messageEditorToClose = null;
        if (editor is { DataContext: QuickOverlayEntry target } && !QuickMessageInputBehavior.IsActive(editor)
            && ReferenceEquals(_viewModel.QuickMessageTarget, target))
        {
            _viewModel.QuickMessageTarget = null;
        }
    }

    private void SetPetControlsVisible(bool visible)
    {
        if (PetControls.IsHitTestVisible != visible)
        {
            PetControls.IsHitTestVisible = visible;
            PetControls.BeginAnimation(OpacityProperty,
                new DoubleAnimation(visible ? 1.0 : 0.0, TimeSpan.FromMilliseconds(visible ? 180 : 250)));
        }
    }

    private void ClosePetOverlays()
    {
        _viewModel.QuickMessageTarget = null;
        _messageEditorToClose = null;
        FriendOverlayPopup.IsOpen = false;
        SetPetControlsVisible(false);
        _overlayCloseTimer.Stop();
    }

    private void QuickMessageEditor_MouseEnter(object sender, MouseEventArgs e)
    {
        _messageEditorToClose = null;
        _overlayCloseTimer.Stop();
    }

    private void QuickMessageEditor_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { IsMouseOver: false, IsKeyboardFocusWithin: false, DataContext: QuickOverlayEntry target } editor
            && ReferenceEquals(_viewModel.QuickMessageTarget, target))
        {
            _messageEditorToClose = editor;
            ScheduleOverlayClose();
        }
    }

    private void QuickMessageInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 입력창에서 포커스가 빠졌을 때 마우스가 이미 오버레이 바깥에 있다면 자연스럽게 닫는다.
        if (!FriendOverlayCard.IsKeyboardFocusWithin && !FriendOverlayCard.IsMouseOver && !PetViewport.IsMouseOver && !PetControls.IsMouseOver)
        {
            ScheduleOverlayClose();
        }
    }

    private void QuickMessageInput_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } input)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (FriendOverlayPopup.IsOpen && input.IsVisible)
                {
                    QuickMessageInputBehavior.Focus(input);
                }
            });
        }
    }

    private void QuickMessageInput_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        => QuickMessageInputBehavior.Focus((TextBox)sender);

    private void QuickMessageInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.SendQuickMessageCommand.CanExecute(null))
        {
            _viewModel.SendQuickMessageCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.QuickMessageTarget = null;
            e.Handled = true;
        }
    }

    // 클릭 투과 집중 모드 UI 제거에 따라 관련 핸들러 주석 처리
    // private void OnClickThroughChanged(object? sender, bool enable)
    // {
    //     Win32WindowHelper.SetClickThrough(this, enable);
    //     if (enable)
    //     {
    //         if (_isPointerPressed)
    //         {
    //             EndPetGrab();
    //             PetViewport.ReleaseMouseCapture();
    //         }
    //         ClosePetOverlays();
    //     }
    // }

    private void OnResetPositionRequested(object? sender, EventArgs e)
    {
        PlaceAtDefaultBottomRight();
        SaveRestingPosition();
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void TrayIcon_TrayLeftMouseDown(object sender, RoutedEventArgs e)
    {
        // 트레이 좌클릭 시 펫 창을 활성화하고 인사 말풍선 표시
        if (_pomodoro.IsFocusActive)
        {
            _viewModel.ShowBubble(FriendOperationErrorCodes.PomodoroFocusMessage, 2400);
        }
        // 클릭 투과 집중 모드 UI 제거에 따라 관련 분기 주석 처리
        // else if (_viewModel.IsClickThroughEnabled)
        // {
        //     _viewModel.ShowBubble("집중 모드 ON · 트레이 우클릭으로 해제", 2400);
        // }
        else
        {
            _animator.TriggerPetReaction();
            _viewModel.ShowBubble($"{_viewModel.PetName} 여기 있어!", 2000);
        }
    }

    private void OnGlobalKeyTyped()
    {
        // 백그라운드 훅 스레드에서 스레드 안전하게 앞발 두드리기 요청만 증가
        _animator.TriggerTypingPawTap();
    }

    private async void OnActivityFlushTick(object? sender, EventArgs e)
    {
        var (keys, clicks) = _activityHook.DrainPendingCounts();
        if (keys == 0 && clicks == 0)
        {
            return;
        }

        _progression.RecordKeystrokes(_viewModel.CurrentProfile, keys);
        _progression.RecordMouseClicks(_viewModel.CurrentProfile, clicks);

        await _viewModel.SaveCurrentProfileAsync();
    }

    private async void OnUptimeTick(object? sender, EventArgs e)
    {
        if (!_activityState.IsUserActiveForUptime(out _))
        {
            return;
        }

        _progression.RecordUptimeMinutes(_viewModel.CurrentProfile, 1);
        await _viewModel.SaveCurrentProfileAsync();
    }

    private void OnPomodoroFocusStateChanged(object? sender, bool isFocusing)
    {
        if (isFocusing)
        {
            _viewModel.QuickMessageTarget = null;
        }

        _ = _social.UpdateLocalPresenceAsync(_viewModel.CurrentProfile, _lastReportedMusicActive, _isLocalSleeping, isFocusing);
        _viewModel.RefreshQuickOverlayEntries();
    }

    private async void OnPomodoroFocusCompleted(object? sender, int focusMinutes)
    {
        var reward = _progression.RecordPomodoroCompletedDetailed(_viewModel.CurrentProfile, focusMinutes);
        _animator.TriggerPetReaction();
        _animator.StartBreakSnack();
        _viewModel.ShowBubble(
            reward.CoinsEarned > 0
                ? $"뽀모도로 완료! +{reward.CoinsEarned} 코인 획득!"
                : "뽀모도로 완료!",
            4000);
        await _viewModel.SaveCurrentProfileAsync();
    }

    private void OnEconomyLevelUpCompleted(object? sender, LevelUpCompletedEventArgs e)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            _animator.TriggerPetReaction();
            _viewModel.ShowBubble($"레벨 업! Lv.{e.NewLevel} 달성!", 3200);
            await _viewModel.SaveCurrentProfileAsync();
            _ = _social.UpdateLocalPresenceAsync(_viewModel.CurrentProfile, _lastReportedMusicActive, _isLocalSleeping, _pomodoro.IsFocusActive);
        });
    }

    private void OnActivityClockResetRequested(object? sender, EventArgs e)
    {
        _activityHook.ResetAntiAbuseState();
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosingOrClosed)
            {
                return;
            }

            _uptimeTimer.Stop();
            _uptimeTimer.Start();
        });
    }

    private void OnPomodoroBreakCompleted(object? sender, EventArgs e)
    {
        _viewModel.ShowBubble("휴식 끝! 다시 집중해 볼까요?", 3000);
    }

    private void OnSocialFriendRequestArrived(object? sender, PendingFriendRequest request)
    {
        Dispatcher.BeginInvoke(() => _viewModel.EnqueueFriendRequest(request));
    }

    public bool TryShowFriendMessage(ReceivedFriendMessage message)
    {
        if (_isClosingOrClosed || !IsVisible || Dispatcher.HasShutdownStarted)
        {
            return false;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => TryShowFriendMessage(message));
            return true;
        }

        var portrait = _portraitRenderer.GetPortrait(message.Appearance);
        _viewModel.ShowBubble(
            new FriendMessageBubble(message.Text, message.SenderName, portrait),
            SocialNotificationBubbleDurationMs);
        return true;
    }

    private void OnSocialNotification(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() => _viewModel.ShowBubble(message, SocialNotificationBubbleDurationMs));
    }

    public bool TryShowFriendStatus(string text, int durationMs, bool animate)
    {
        if (!IsAvailableForMessages)
        {
            return false;
        }

        _viewModel.ShowBubble(text, durationMs);
        if (animate)
        {
            _animator.TriggerPetReaction();
        }
        return true;
    }

    /// <summary>UI 스레드 전용. 현재 몸통 중심을 화면 물리 픽셀로 반환한다.</summary>
    internal double SnowballPetScale => _viewModel.PetScale;
    private bool _snowballSceneOwnsPose;
    internal bool IsSnowballThrowBlocked => _isDraggingWindow || _animator.IsSnowballThrowBlocked;
    internal bool IsReactingToSnowball => _animator.IsReactingToSnowball;

    internal bool TryGetSnowballTargetPhysical(out Point targetPhysical)
    {
        targetPhysical = default;
        return Dispatcher.CheckAccess() && !Dispatcher.HasShutdownStarted && IsAvailableForMessages
            && PetDisplayLayoutHelper.TryGetSnowballBodyPhysical(PetViewport, PetVisual, _currentRig, out targetPhysical);
    }

    internal bool TryGetSnowballHandPhysical(out Point hand) =>
        PetDisplayLayoutHelper.TryGetSnowballHandPhysical(PetViewport, PetVisual, _currentRig, out hand);

    internal void AdvanceOutgoingSnowballPose(Vector? direction, SnowballThrowPose? pose, bool laughing, double seconds)
    {
        _snowballSceneOwnsPose = true;
        _animator.SetOutgoingSnowballPose(pose);
        _animator.SetSnowballLaughing(laughing);
        if (direction is { } aim)
        {
            _animator.UpdateFrame(seconds, aim.X, aim.Y, _viewModel.IsBubbleVisible);
            return;
        }
        ComputeNormalizedLookDirection(out var lookX, out var lookY);
        _animator.UpdateFrame(seconds, lookX, lookY, _viewModel.IsBubbleVisible);
    }

    internal void ReleaseOutgoingSnowballPose()
    {
        if (!_snowballSceneOwnsPose) return;
        _snowballSceneOwnsPose = false;
        _animator.CancelSnowballInteraction(preserveHit: true);
    }

    /// <summary>공용 눈덩이 연출 관리자가 명중 시 UI 스레드에서 호출한다.</summary>
    internal bool TryTriggerSnowballHit()
    {
        // 숨김·종료·리그 해제 이후의 늦은 명중은 재생하지 않는다.
        if (!TryGetSnowballTargetPhysical(out _)) return false;
        _animator.TriggerSnowballHit();
        // 유휴 갱신 중에도 짧은 피격 반응을 부드럽게 재생한다.
        if (_frameTimer.Interval != ActiveFrameInterval)
            _frameTimer.Interval = ActiveFrameInterval;
        return true;
    }

    private void OnSocialPetReactionTriggered(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => _animator.TriggerPetReaction());
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;

        // 5분 이상 키보드/마우스 입력이 없으면 수면(zzz) 상태로 전환, 입력 재개 시 깜짝 기상
        var idleCheckInterval = _isLocalSleeping ? 0.1 : 0.5;
        if (_lastIdleCheckSeconds < 0 || now - _lastIdleCheckSeconds >= idleCheckInterval)
        {
            _lastIdleCheckSeconds = now;
            if (Win32WindowHelper.TryGetSystemIdleDuration(out var idleDuration))
            {
                var shouldSleep = idleDuration >= SleepIdleThreshold;
                if (shouldSleep != _isLocalSleeping)
                {
                    ApplyLocalSleepState(shouldSleep);
                }
            }
        }

        // 로컬 오디오 스냅샷 조회
        var localAudio = _audioAnalyzer.GetCurrentSnapshot();

        // 음악 활성 상태 변경 시 LAN HELLO 프레즌스 갱신
        if (localAudio.IsMusicActive != _lastReportedMusicActive)
        {
            _lastReportedMusicActive = localAudio.IsMusicActive;
            _ = _social.UpdateLocalPresenceAsync(_viewModel.CurrentProfile, localAudio.IsMusicActive, _isLocalSleeping, _pomodoro.IsFocusActive);
        }

        // 호스트로서 구독 중인 친구들에게 30Hz(약 33ms 간격) 경량 오디오 프레임 송출
        if (now - _lastMusicPublishSeconds >= 0.033)
        {
            _lastMusicPublishSeconds = now;
            _ = _social.PublishHostMusicFrameAsync(localAudio.Bass, localAudio.Mid, localAudio.Treble, localAudio.IsBeat);
        }

        // 팔로워 모드 수신 프레임 우선 확인 (내 음악 우선 옵션 포함)
        var isFollowing = _social.TryGetFollowedMusicFrame(localAudio.IsMusicActive, out var followed);
        if (isFollowing)
        {
            _animator.UpdateAudioSnapshot(followed.Bass, followed.Mid, followed.Treble, followed.IsBeat);
        }
        else
        {
            _animator.UpdateAudioSnapshot(localAudio.Bass, localAudio.Mid, localAudio.Treble, localAudio.IsBeat);
        }

        var isActive = IsVisible && (localAudio.IsMusicActive || isFollowing || PetViewport.IsMouseOver || _animator.HasInteractiveMotion);
        var desiredInterval = isActive ? ActiveFrameInterval : IdleFrameInterval;
        if (_frameTimer.Interval != desiredInterval)
        {
            _frameTimer.Interval = desiredInterval;
        }

        var dt = now - _lastFrameSeconds;
        if (!IsVisible)
        {
            return;
        }

        _lastFrameSeconds = now;
        if (_snowballSceneOwnsPose) return;
        ComputeNormalizedLookDirection(out var lookX, out var lookY);
        _animator.UpdateFrame(dt, lookX, lookY, _viewModel.IsBubbleVisible);
    }

    private void ApplyLocalSleepState(bool isSleeping)
    {
        _isLocalSleeping = isSleeping;
        _viewModel.IsSleeping = isSleeping;
        _animator.SetSleeping(isSleeping);
        _ = _social.UpdateLocalPresenceAsync(_viewModel.CurrentProfile, _lastReportedMusicActive, isSleeping, _pomodoro.IsFocusActive);
    }

    private void ComputeNormalizedLookDirection(out double lookX, out double lookY)
    {
        if (PresentationSource.FromVisual(PetViewport) is null
            || !Win32WindowHelper.TryGetGlobalCursorPosition(out var physicalCursor))
        {
            lookX = 0;
            lookY = 0;
            return;
        }

        var cursor = PetViewport.PointFromScreen(physicalCursor);
        lookX = Math.Clamp((cursor.X - PetViewport.ActualWidth * 0.5) / 420.0, -1.0, 1.0);
        lookY = Math.Clamp((cursor.Y - PetViewport.ActualHeight * 0.5) / 360.0, -1.0, 1.0);
    }

    private void PetViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 3D 히트 테스트로 실제 펫 메시 위를 눌렀는지 확인
        if (!HitTestPetMesh(e.GetPosition(PetViewport)) || !PetViewport.CaptureMouse())
        {
            return;
        }

        _isPointerPressed = true;
        _isDraggingWindow = false;
        _dizzyTriggeredDuringDrag = false;
        _lastHorizontalDirection = 0;
        _shakeReversalCount = 0;
        _shakeWindowStartSeconds = _clock.Elapsed.TotalSeconds;

        _pressScreenPoint = PointToScreen(e.GetPosition(this));
        _lastScreenPoint = _pressScreenPoint;

        _animator.SetGrabbed(true);
        e.Handled = true;
    }

    private void PetViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPointerPressed || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentScreen = PointToScreen(e.GetPosition(this));
        var totalDx = currentScreen.X - _pressScreenPoint.X;
        var totalDy = currentScreen.Y - _pressScreenPoint.Y;
        var distance = Math.Sqrt(totalDx * totalDx + totalDy * totalDy);

        if (!_isDraggingWindow && distance > DragThresholdPixels)
        {
            _isDraggingWindow = true;
            ClosePetOverlays();
        }

        if (_isDraggingWindow)
        {
            var source = PresentationSource.FromVisual(this);
            var dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            var stepDx = currentScreen.X - _lastScreenPoint.X;
            var stepDy = currentScreen.Y - _lastScreenPoint.Y;

            Left += stepDx / dpiX;
            Top += stepDy / dpiY;

            DetectRapidShake(stepDx);
        }

        _lastScreenPoint = currentScreen;
    }

    private void PetViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPointerPressed)
        {
            return;
        }

        var wasDragging = _isDraggingWindow;
        EndPetGrab();
        PetViewport.ReleaseMouseCapture();
        if (!wasDragging)
        {
            if (_pomodoro.IsFocusActive)
            {
                _viewModel.ShowBubble(FriendOperationErrorCodes.PomodoroFocusMessage, 2000);
            }
            else if (_social.TryReservePetInteraction())
            {
                // 이동 거리 5px 이하: 쓰다듬기 3D 반응
                _animator.TriggerPetReaction();
                _viewModel.ShowBubble($"{_viewModel.PetName}(이)가 기분 좋아해요!", 2000);
            }
        }

        e.Handled = true;
    }

    private void PetViewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isPointerPressed && !PetViewport.IsMouseCaptured)
        {
            EndPetGrab();
        }
    }

    private void EndPetGrab()
    {
        var wasDragging = _isDraggingWindow;
        _isPointerPressed = false;
        _isDraggingWindow = false;
        _animator.SetGrabbed(false);
        if (wasDragging)
        {
            SaveRestingPosition();
        }
    }

    private void DetectRapidShake(double stepDxPixels)
    {
        if (Math.Abs(stepDxPixels) < 8.0)
        {
            return;
        }

        var now = _clock.Elapsed.TotalSeconds;
        if (now - _shakeWindowStartSeconds > 0.55)
        {
            _shakeWindowStartSeconds = now;
            _shakeReversalCount = 0;
        }

        var direction = Math.Sign(stepDxPixels);
        if (_lastHorizontalDirection != 0 && direction != _lastHorizontalDirection)
        {
            _shakeReversalCount++;
            if (_shakeReversalCount >= 3 && !_dizzyTriggeredDuringDrag && !_pomodoro.IsFocusActive)
            {
                _dizzyTriggeredDuringDrag = true;
                _animator.TriggerDizzyReaction();
                _viewModel.ShowBubble("으악, 어지러워요!", 2200);
            }
        }

        _lastHorizontalDirection = direction;
    }

    private bool HitTestPetMesh(Point viewportPoint)
    {
        var hitDetected = false;
        VisualTreeHelper.HitTest(
            PetViewport,
            null,
            result =>
            {
                if (result is RayMeshGeometry3DHitTestResult)
                {
                    hitDetected = true;
                    return HitTestResultBehavior.Stop;
                }

                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(viewportPoint));

        return hitDetected;
    }

    private void RestoreOrPlaceDefaultPosition(PetProfile profile)
    {
        if (profile.LastWindowLeft is double savedLeft && double.IsFinite(savedLeft)
            && profile.LastWindowTop is double savedTop && double.IsFinite(savedTop))
        {
            Left = savedLeft;
            Top = savedTop;
            return;
        }

        PlaceAtDefaultBottomRight();
    }

    private void PlaceAtDefaultBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        var viewportHeight = double.IsFinite(PetViewport.Height) && PetViewport.Height > 0
            ? PetViewport.Height
            : PetDisplayLayoutHelper.BaseViewportHeight;
        Left = workArea.Right - Width - 28;
        Top = workArea.Bottom - viewportHeight - 8;
    }

    private void SaveRestingPosition()
        => _ = _viewModel.SaveWindowPositionAsync(Left, Top);

    private static System.Drawing.Icon CreateTrayIcon()
    {
        var resourceUri = new Uri("pack://application:,,,/LabPet.Client;component/Assets/app.ico", UriKind.Absolute);
        var streamInfo = Application.GetResourceStream(resourceUri);
        if (streamInfo is not null)
        {
            using var s = streamInfo.Stream;
            return new System.Drawing.Icon(s);
        }

        return System.Drawing.SystemIcons.Application;
    }
}
