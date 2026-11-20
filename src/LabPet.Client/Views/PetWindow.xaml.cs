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
using Microsoft.Win32;

namespace LabPet.Client.Views;

public partial class PetWindow : Window
{
    private const double DragThresholdPixels = 5.0;
    private const double IdleAnimationIntervalSeconds = 0.030;
    private const double BaseWindowWidth = 214.0;
    private const double DefaultWindowHeight = 274.0;
    private const double LegacyMessageRowHeight = 78.0;
    private const double NameplateGap = 4.0;
    private const double BaseNameplateHeight = 24.0;
    private const double FriendOverlayGap = 4.0;
    private const double FriendOverlayTopInset = 12.0;
    private const double SpeechBubbleBottomOverlap = 8.0;

    /// <summary>
    /// 타이핑 미니 키보드(Y=-0.80, Z=1.04)와 바닥 회전 링 오라(Y=-0.78, Z=1.24)의 앞쪽 하단 기준점.
    /// 네임태그가 캐릭터 발바닥뿐 아니라 키보드·오라 효과도 가리지 않도록 함께 비교한다.
    /// </summary>
    private static readonly Point3D KeyboardAuraAnchor = new(0, -0.81, 1.12);

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

    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _activityFlushTimer;
    private readonly DispatcherTimer _uptimeTimer;
    private readonly DispatcherTimer _overlayCloseTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrameSeconds;
    private double _lastMusicPublishSeconds;
    private bool _lastReportedMusicActive;
    private Point3D _nameplateAnchor;
    private FrameworkElement? _messageEditorToClose;

    // 마우스 클릭(<=5px) vs 창 드래그(>5px) vs 빠른 좌우 흔들기 판정 상태
    private bool _isPointerPressed;
    private bool _isDraggingWindow;
    private Point _pressScreenPoint;
    private Point _lastScreenPoint;
    private int _lastHorizontalDirection;
    private int _shakeReversalCount;
    private double _shakeWindowStartSeconds;
    private bool _dizzyTriggeredDuringDrag;

    public PetWindow(
        PetWindowViewModel viewModel,
        IPetMeshFactory meshFactory,
        PetAnimationController animator,
        IAudioAnalyzer audioAnalyzer,
        GlobalActivityHookService activityHook,
        SoloProgressionService progression,
        PomodoroTimerService pomodoro,
        SocialCoordinatorService social)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _meshFactory = meshFactory;
        _portraitRenderer = new PetPortraitRenderer(meshFactory);
        _animator = animator;
        _audioAnalyzer = audioAnalyzer;
        _activityHook = activityHook;
        _progression = progression;
        _pomodoro = pomodoro;
        _social = social;
        DataContext = _viewModel;

        TrayIcon.Icon = CreateTrayIcon();
        LightsVisual.Content = _meshFactory.CreateStudioLights();
        FriendOverlayPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            GetFriendOverlayPlacements(popupSize, targetSize, VisualTreeHelper.GetDpi(PetLayout),
                FriendOverlayCard.Margin, messageRowHeight: 0);
        SpeechBubblePopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            GetSpeechBubblePlacements(popupSize, targetSize, VisualTreeHelper.GetDpi(PetLayout));

        Loaded += (_, _) =>
        {
            KeepPetInMonitorWorkArea();
            RepositionSpeechBubblePopup();
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        LocationChanged += (_, _) => RepositionSpeechBubblePopup();
        SizeChanged += (_, e) =>
        {
            FriendOverlayPopup.PlacementRectangle = new Rect(e.NewSize);
            RepositionSpeechBubblePopup();
        };
        PetViewport.SizeChanged += (_, _) =>
        {
            UpdateNameplatePosition();
            RepositionSpeechBubblePopup();
        };

        _viewModel.ProfileUpdated += OnProfileUpdated;
        _viewModel.PetScaleChanged += OnPetScaleChanged;
        _viewModel.ClickThroughChanged += OnClickThroughChanged;
        _viewModel.ResetPositionRequested += OnResetPositionRequested;
        _viewModel.ExitRequested += OnExitRequested;
        _viewModel.FriendRequestResponded += OnFriendRequestResponded;
        _activityHook.KeyTyped += OnGlobalKeyTyped;
        _pomodoro.FocusCompleted += OnPomodoroFocusCompleted;
        _pomodoro.BreakCompleted += OnPomodoroBreakCompleted;
        _social.StateChanged += OnSocialStateChanged;
        _social.FriendRequestArrived += OnSocialFriendRequestArrived;
        _social.PairingCodeReady += OnPairingCodeReady;
        _social.FriendMessageReceived += OnFriendMessageReceived;
        _social.SocialNotification += OnSocialNotification;
        _social.PetReactionTriggered += OnSocialPetReactionTriggered;

        _overlayCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(360)
        };
        _overlayCloseTimer.Tick += OnOverlayCloseTick;

        SourceInitialized += (_, _) =>
        {
            Win32WindowHelper.ApplyToolWindowStyle(this);
            Win32WindowHelper.SetClickThrough(this, _viewModel.IsClickThroughEnabled);
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

        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _frameTimer.Stop();
            _activityFlushTimer.Stop();
            _uptimeTimer.Stop();
            _overlayCloseTimer.Stop();
            _audioAnalyzer.Stop();
            _activityHook.Stop();
            TrayIcon.Dispose();
            _viewModel.ProfileUpdated -= OnProfileUpdated;
            _viewModel.PetScaleChanged -= OnPetScaleChanged;
            _viewModel.ClickThroughChanged -= OnClickThroughChanged;
            _viewModel.ResetPositionRequested -= OnResetPositionRequested;
            _viewModel.ExitRequested -= OnExitRequested;
            _viewModel.FriendRequestResponded -= OnFriendRequestResponded;
            _activityHook.KeyTyped -= OnGlobalKeyTyped;
            _pomodoro.FocusCompleted -= OnPomodoroFocusCompleted;
            _pomodoro.BreakCompleted -= OnPomodoroBreakCompleted;
            _social.StateChanged -= OnSocialStateChanged;
            _social.FriendRequestArrived -= OnSocialFriendRequestArrived;
            _social.PairingCodeReady -= OnPairingCodeReady;
            _social.FriendMessageReceived -= OnFriendMessageReceived;
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
        var rig = _meshFactory.BuildPetRig(profile.Appearance);
        PetVisual.Content = rig.RootModel;
        _nameplateAnchor = rig.NameplateAnchor;
        UpdateNameplatePosition();
        _animator.AttachRig(rig, profile.Appearance.AccentColorHex);
        _ = _social.UpdateLocalPresenceAsync(profile, _lastReportedMusicActive);
    }

    private void OnPetScaleChanged(object? sender, double scale)
    {
        var clamped = PetProfile.NormalizePetScale(scale);
        var previousWidth = Width;
        var previousHeight = Height;

        // 뷰포트 너비로 펫 배율을 맞추고, 창은 이름표와 조절바가 들어갈 너비를 확보한다.
        PetViewport.Width = Math.Round(BaseWindowWidth * clamped);
        Width = Math.Max(180.0, PetViewport.Width);
        var viewportHeight = Math.Max(220.0, Math.Round(DefaultWindowHeight * clamped))
            - LegacyMessageRowHeight - BaseNameplateHeight;
        PetViewport.Height = viewportHeight;
        Height = viewportHeight + PetNameplateRow.Height.Value;
        UpdateNameplatePosition();
        RepositionSpeechBubblePopup();

        // 슬라이더를 끌 때는 조절바 가운데를 유지하고, 메뉴에서 변경할 때는 우하단을 유지한다.
        if (IsLoaded)
        {
            Left += (previousWidth - Width) * (PetControls.IsMouseCaptureWithin ? 0.5 : 1.0);
            Top += previousHeight - Height;
        }
    }

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
    }

    private void UpdateNameplatePosition()
    {
        if (PetViewport.ActualHeight > 0 && PetVisual.Content is not null)
        {
            var transform = PetVisual.TransformToAncestor(PetViewport);
            if (transform.TryTransform(_nameplateAnchor, out var bodyAnchor))
            {
                var lowestY = bodyAnchor.Y;
                if (transform.TryTransform(KeyboardAuraAnchor, out var effectAnchor))
                {
                    lowestY = Math.Max(lowestY, effectAnchor.Y);
                }

                PetNameplateOffset.Y = Math.Min(0, lowestY + NameplateGap - PetViewport.ActualHeight);
            }
        }
    }

    private void OnSocialStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(_viewModel.RefreshQuickOverlayEntries);
    }

    private void PetViewport_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_viewModel.IsClickThroughEnabled || _isDraggingWindow)
        {
            return;
        }

        CancelOverlayCloseUnlessEditorLeft();
        _viewModel.RefreshQuickOverlayEntries();
        FriendOverlayPopup.IsOpen = true;
        SetPetControlsVisible(true);
    }

    internal static CustomPopupPlacement[] GetFriendOverlayPlacements(
        Size popupSize, Size targetSize, DpiScale dpi, Thickness cardMargin, double messageRowHeight)
    {
        // WPF는 카드의 여백을 뺀 크기를 전달한다. 그림자 여백까지 포함해 창 바깥에 배치한다.
        var popupWidth = popupSize.Width + (cardMargin.Left + cardMargin.Right) * dpi.DpiScaleX;
        var popupHeight = popupSize.Height + (cardMargin.Top + cardMargin.Bottom) * dpi.DpiScaleY;
        var gapX = FriendOverlayGap * dpi.DpiScaleX;
        var gapY = FriendOverlayGap * dpi.DpiScaleY;
        var preferredTop = (messageRowHeight + FriendOverlayTopInset) * dpi.DpiScaleY;
        var sideTop = Math.Min(preferredTop, Math.Max(0, targetSize.Height - popupHeight));
        var centerX = (targetSize.Width - popupWidth) / 2;

        return
        [
            new(new Point(-popupWidth - gapX, sideTop), PopupPrimaryAxis.Vertical),
            new(new Point(targetSize.Width + gapX, sideTop), PopupPrimaryAxis.Vertical),
            new(new Point(centerX, -popupHeight - gapY), PopupPrimaryAxis.Horizontal),
            new(new Point(centerX, targetSize.Height + gapY), PopupPrimaryAxis.Horizontal)
        ];
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

        if (!PetViewport.IsMouseOver && !FriendOverlayCard.IsMouseOver && !PetControls.IsMouseOver
            && !FriendOverlayCard.IsMouseCaptureWithin && !PetControls.IsMouseCaptureWithin)
        {
            ClosePetOverlays();
            return;
        }

        var editor = _messageEditorToClose;
        _messageEditorToClose = null;
        if (editor is { IsMouseOver: false, IsMouseCaptureWithin: false, IsKeyboardFocusWithin: false, DataContext: QuickOverlayEntry target }
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
                    FocusQuickMessageInput(input);
                }
            });
        }
    }

    private void QuickMessageInput_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        => FocusQuickMessageInput((TextBox)sender);

    private static void FocusQuickMessageInput(TextBox input)
    {
        Win32WindowHelper.ActivatePopupForKeyboardInput(input);
        input.Focus();
        Keyboard.Focus(input);
    }

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

    private void OnClickThroughChanged(object? sender, bool enable)
    {
        Win32WindowHelper.SetClickThrough(this, enable);
        if (enable)
        {
            ClosePetOverlays();
        }
    }

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
        if (_viewModel.IsClickThroughEnabled)
        {
            _viewModel.ShowBubble("집중 모드 ON · 트레이 우클릭으로 해제", 2400);
        }
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

        var (keyLevelUp, _) = _progression.RecordKeystrokes(_viewModel.CurrentProfile, keys);
        var (clickLevelUp, newLevel) = _progression.RecordMouseClicks(_viewModel.CurrentProfile, clicks);

        if (keyLevelUp || clickLevelUp)
        {
            _animator.TriggerPetReaction();
            _viewModel.ShowBubble($"레벨 업! Lv.{newLevel} 달성!", 3200);
        }

        await _viewModel.SaveCurrentProfileAsync();
    }

    private async void OnUptimeTick(object? sender, EventArgs e)
    {
        var (leveledUp, newLevel) = _progression.RecordUptimeMinutes(_viewModel.CurrentProfile, 1);
        if (leveledUp)
        {
            _animator.TriggerPetReaction();
            _viewModel.ShowBubble($"레벨 업! Lv.{newLevel} 달성!", 3200);
        }

        await _viewModel.SaveCurrentProfileAsync();
    }

    private async void OnPomodoroFocusCompleted(object? sender, EventArgs e)
    {
        var (leveledUp, newLevel) = _progression.RecordPomodoroCompleted(_viewModel.CurrentProfile);
        _animator.TriggerPetReaction();
        _viewModel.ShowBubble(
            leveledUp
                ? $"뽀모도로 완료! +50 코인 & Lv.{newLevel} 달성!"
                : "뽀모도로 완료! +100 XP · +50 코인 획득!",
            4000);
        await _viewModel.SaveCurrentProfileAsync();
    }

    private void OnPomodoroBreakCompleted(object? sender, EventArgs e)
    {
        _viewModel.ShowBubble("휴식 끝! 다시 집중해 볼까요?", 3000);
    }

    private void OnSocialFriendRequestArrived(object? sender, PendingFriendRequest request)
    {
        Dispatcher.BeginInvoke(() => _viewModel.PendingFriendRequest = request);
    }

    private void OnPairingCodeReady(object? sender, string code)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (IsLoaded)
            {
                new PairingCodeWindow(code) { Owner = this }.ShowDialog();
            }
        });
    }

    private void OnFriendMessageReceived(object? sender, ReceivedFriendMessage message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var portrait = _portraitRenderer.GetPortrait(message.Appearance);
            _viewModel.ShowBubble(new FriendMessageBubble(message.Text, message.SenderName, portrait),
                SocialNotificationBubbleDurationMs);
        });
    }

    private void OnSocialNotification(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() => _viewModel.ShowBubble(message, SocialNotificationBubbleDurationMs));
    }

    private void OnSocialPetReactionTriggered(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => _animator.TriggerPetReaction());
    }

    private async void OnFriendRequestResponded(object? sender, (PendingFriendRequest Request, bool Accept) args)
    {
        string? code = null;
        if (args.Accept)
        {
            var dialog = new PairingCodeWindow { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                _viewModel.PendingFriendRequest ??= args.Request;
                return;
            }
            code = dialog.VerificationCode;
        }
        var ok = await _social.RespondFriendRequestAsync(args.Request, args.Accept, code);
        if (args.Accept && !ok)
        {
            _viewModel.PendingFriendRequest ??= args.Request;
        }
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;

        // 로컬 오디오 스냅샷 조회
        var localAudio = _audioAnalyzer.GetCurrentSnapshot();

        // 음악 활성 상태 변경 시 LAN HELLO 프레즌스 갱신
        if (localAudio.IsMusicActive != _lastReportedMusicActive)
        {
            _lastReportedMusicActive = localAudio.IsMusicActive;
            _ = _social.UpdateLocalPresenceAsync(_viewModel.CurrentProfile, localAudio.IsMusicActive);
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

        var dt = now - _lastFrameSeconds;
        if (!IsVisible || (!localAudio.IsMusicActive && !isFollowing && !PetViewport.IsMouseOver
            && !_animator.HasInteractiveMotion && dt < IdleAnimationIntervalSeconds))
        {
            return;
        }
        _lastFrameSeconds = now;
        ComputeNormalizedLookDirection(out var lookX, out var lookY);
        _animator.UpdateFrame(dt, lookX, lookY);
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
        if (!HitTestPetMesh(e.GetPosition(PetViewport)))
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

        PetViewport.CaptureMouse();
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

        _isPointerPressed = false;
        PetViewport.ReleaseMouseCapture();

        if (_isDraggingWindow)
        {
            _isDraggingWindow = false;
            SaveRestingPosition();
        }
        else
        {
            // 이동 거리 5px 이하: 쓰다듬기 3D 반응
            _animator.TriggerPetReaction();
            _viewModel.ShowBubble($"{_viewModel.PetName}(이)가 기분 좋아해요!", 2000);
        }

        e.Handled = true;
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
            if (_shakeReversalCount >= 3 && !_dizzyTriggeredDuringDrag)
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

    private void KeepPetInMonitorWorkArea()
    {
        if (!Win32WindowHelper.TryGetNearestWorkArea(this, out var workArea))
        {
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = PetLayout.PointToScreen(new Point());
        var bounds = new Rect(origin, new Size(ActualWidth * dpi.DpiScaleX, ActualHeight * dpi.DpiScaleY));
        var visibleOrigin = ClampWindowOrigin(bounds, workArea);
        Left += (visibleOrigin.X - origin.X) / dpi.DpiScaleX;
        Top += (visibleOrigin.Y - origin.Y) / dpi.DpiScaleY;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (IsLoaded && IsVisible)
            {
                KeepPetInMonitorWorkArea();
            }
        });
    }

    internal static Point ClampWindowOrigin(Rect window, Rect workArea)
        => new(
            Math.Clamp(window.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - window.Width)),
            Math.Clamp(window.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - window.Height)));

    private void PlaceAtDefaultBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 28;
        Top = workArea.Bottom - Height - 18;
    }

    private void SaveRestingPosition()
        => _ = _viewModel.SaveWindowPositionAsync(Left, Top);

    private static System.Drawing.Icon CreateTrayIcon()
    {
        var resourceUri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
        var streamInfo = Application.GetResourceStream(resourceUri);
        if (streamInfo is not null)
        {
            using var s = streamInfo.Stream;
            return new System.Drawing.Icon(s);
        }

        return System.Drawing.SystemIcons.Application;
    }
}
