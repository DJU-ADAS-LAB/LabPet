using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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

namespace LabPet.Client.Views;

public partial class PetWindow : Window
{
    private const double DragThresholdPixels = 5.0;
    private const double BaseWindowWidth = 214.0;
    private const double BaseWindowHeight = 274.0;
    private const double BaseCameraDistanceZ = 5.95;

    private readonly PetWindowViewModel _viewModel;
    private readonly IPetMeshFactory _meshFactory;
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
    private bool _isMouseOverPet;
    private bool _isMouseOverOverlay;

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
        _animator = animator;
        _audioAnalyzer = audioAnalyzer;
        _activityHook = activityHook;
        _progression = progression;
        _pomodoro = pomodoro;
        _social = social;
        DataContext = _viewModel;

        TrayIcon.Icon = CreateTrayIcon();
        LightsVisual.Content = _meshFactory.CreateStudioLights();

        _viewModel.ProfileUpdated += OnProfileUpdated;
        _viewModel.PetScaleChanged += OnPetScaleChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ClickThroughChanged += OnClickThroughChanged;
        _viewModel.ResetPositionRequested += OnResetPositionRequested;
        _viewModel.ExitRequested += OnExitRequested;
        _viewModel.FriendRequestResponded += OnFriendRequestResponded;
        _activityHook.KeyTyped += OnGlobalKeyTyped;
        _pomodoro.FocusCompleted += OnPomodoroFocusCompleted;
        _pomodoro.BreakCompleted += OnPomodoroBreakCompleted;
        _social.StateChanged += OnSocialStateChanged;
        _social.FriendRequestArrived += OnSocialFriendRequestArrived;
        _social.SocialNotification += OnSocialNotification;
        _social.PetReactionTriggered += OnSocialPetReactionTriggered;

        _overlayCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(360)
        };
        _overlayCloseTimer.Tick += (_, _) =>
        {
            _overlayCloseTimer.Stop();
            if (!_isMouseOverPet && !_isMouseOverOverlay && !QuickMessageInput.IsKeyboardFocusWithin)
            {
                FriendOverlayPopup.IsOpen = false;
                _viewModel.QuickMessageTarget = null;
            }
        };

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
            _frameTimer.Stop();
            _activityFlushTimer.Stop();
            _uptimeTimer.Stop();
            _overlayCloseTimer.Stop();
            _audioAnalyzer.Stop();
            _activityHook.Stop();
            TrayIcon.Dispose();
            _viewModel.ProfileUpdated -= OnProfileUpdated;
            _viewModel.PetScaleChanged -= OnPetScaleChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ClickThroughChanged -= OnClickThroughChanged;
            _viewModel.ResetPositionRequested -= OnResetPositionRequested;
            _viewModel.ExitRequested -= OnExitRequested;
            _viewModel.FriendRequestResponded -= OnFriendRequestResponded;
            _activityHook.KeyTyped -= OnGlobalKeyTyped;
            _pomodoro.FocusCompleted -= OnPomodoroFocusCompleted;
            _pomodoro.BreakCompleted -= OnPomodoroBreakCompleted;
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
        var rig = _meshFactory.BuildPetRig(profile.Appearance);
        PetVisual.Content = rig.RootModel;
        _animator.AttachRig(rig, profile.Appearance.AccentColorHex);
        _ = _social.UpdateLocalPresenceAsync(profile, _lastReportedMusicActive);
    }

    private void OnPetScaleChanged(object? sender, double scale)
    {
        var clamped = Math.Clamp(scale, 0.65, 1.45);
        var prevRight = Left + Width;
        var prevBottom = Top + Height;

        Width = Math.Max(180.0, Math.Round(BaseWindowWidth * clamped));
        Height = Math.Max(220.0, Math.Round(BaseWindowHeight * clamped));
        PetCamera.Position = new Point3D(0, 0.34, BaseCameraDistanceZ / Math.Sqrt(clamped));

        // 크기 변경 시 우하단 기준점이 자연스럽게 유지되도록 보정
        if (IsLoaded)
        {
            Left = prevRight - Width;
            Top = prevBottom - Height;
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

        _isMouseOverPet = true;
        _overlayCloseTimer.Stop();
        _viewModel.RefreshQuickOverlayEntries();
        FriendOverlayPopup.IsOpen = true;
    }

    private void PetViewport_MouseLeave(object sender, MouseEventArgs e)
    {
        _isMouseOverPet = false;
        _overlayCloseTimer.Stop();
        _overlayCloseTimer.Start();
    }

    private void FriendOverlayCard_MouseEnter(object sender, MouseEventArgs e)
    {
        _isMouseOverOverlay = true;
        _overlayCloseTimer.Stop();
    }

    private void FriendOverlayCard_MouseLeave(object sender, MouseEventArgs e)
    {
        _isMouseOverOverlay = false;
        _overlayCloseTimer.Stop();
        _overlayCloseTimer.Start();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PetWindowViewModel.QuickMessageTarget) && _viewModel.QuickMessageTarget is not null)
        {
            FocusQuickMessageInput();
        }
    }

    private void FocusQuickMessageInput()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!FriendOverlayPopup.IsOpen || _viewModel.QuickMessageTarget is null)
            {
                return;
            }

            Win32WindowHelper.ActivatePopupForKeyboardInput(QuickMessageInput);
            QuickMessageInput.Focus();
            Keyboard.Focus(QuickMessageInput);
        });
    }

    private void QuickMessageInput_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        Win32WindowHelper.ActivatePopupForKeyboardInput(QuickMessageInput);
        QuickMessageInput.Focus();
        Keyboard.Focus(QuickMessageInput);
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
            FriendOverlayPopup.IsOpen = false;
        }
    }

    private void OnResetPositionRequested(object? sender, EventArgs e)
    {
        PlaceAtDefaultBottomRight();
        _ = _viewModel.SaveWindowPositionAsync(Left, Top);
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

    private void OnSocialNotification(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() => _viewModel.ShowBubble(message, 3400));
    }

    private void OnSocialPetReactionTriggered(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => _animator.TriggerPetReaction());
    }

    private async void OnFriendRequestResponded(object? sender, (PendingFriendRequest Request, bool Accept) args)
    {
        var ok = await _social.RespondFriendRequestAsync(args.Request, args.Accept);
        if (args.Accept && !ok)
        {
            // 상대 PC 방화벽 등으로 수락 전송이 실패한 경우 재시도할 수 있도록 요청 복원
            _viewModel.PendingFriendRequest = args.Request;
        }
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _lastFrameSeconds;
        _lastFrameSeconds = now;

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
        if (_social.TryGetFollowedMusicFrame(localAudio.IsMusicActive, out var followed))
        {
            _animator.UpdateAudioSnapshot(followed.Bass, followed.Mid, followed.Treble, followed.IsBeat);
        }
        else
        {
            _animator.UpdateAudioSnapshot(localAudio.Bass, localAudio.Mid, localAudio.Treble, localAudio.IsBeat);
        }

        ComputeNormalizedLookDirection(out var lookX, out var lookY);
        _animator.UpdateFrame(dt, lookX, lookY);
    }

    private void ComputeNormalizedLookDirection(out double lookX, out double lookY)
    {
        if (!Win32WindowHelper.TryGetGlobalCursorPosition(out var physicalCursor))
        {
            lookX = 0;
            lookY = 0;
            return;
        }

        var source = PresentationSource.FromVisual(this);
        var dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var cursorDipX = physicalCursor.X / dpiX;
        var cursorDipY = physicalCursor.Y / dpiY;

        var petCenterX = Left + Width * 0.5;
        var petCenterY = Top + Height * 0.58;

        lookX = Math.Clamp((cursorDipX - petCenterX) / 420.0, -1.0, 1.0);
        lookY = Math.Clamp((cursorDipY - petCenterY) / 360.0, -1.0, 1.0);
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
            FriendOverlayPopup.IsOpen = false;
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
            _ = _viewModel.SaveWindowPositionAsync(Left, Top);
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
        var workArea = SystemParameters.WorkArea;
        if (profile.LastWindowLeft is double savedLeft && profile.LastWindowTop is double savedTop)
        {
            var clampedLeft = Math.Clamp(savedLeft, workArea.Left - 40, workArea.Right - Width + 40);
            var clampedTop = Math.Clamp(savedTop, workArea.Top - 40, workArea.Bottom - Height + 40);
            Left = clampedLeft;
            Top = clampedTop;
            return;
        }

        PlaceAtDefaultBottomRight();
    }

    private void PlaceAtDefaultBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 28;
        Top = workArea.Bottom - Height - 18;
    }

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
