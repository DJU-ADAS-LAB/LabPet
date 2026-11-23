using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using LabPet.Client.Audio;
using LabPet.Client.Interop;
using LabPet.Client.Rendering;
using LabPet.Client.Social;
using LabPet.Shared.Models;

namespace LabPet.Client.Views;

/// <summary>
/// 내 바탕화면 펫 옆에 친구의 3D 펫을 함께 띄워주는 경량 방문 창.
/// </summary>
public partial class VisitorPetWindow : Window
{
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(45);
    private const double DragThresholdPixels = 5.0;
    private const double BaseWindowWidth = 176.0;
    private const double BaseViewportHeight = 186.0;
    private const double BaseBubbleHeight = 34.0;
    private const double BaseNameplateHeight = 24.0;

    private readonly IPetMeshFactory _meshFactory;
    private readonly IAudioAnalyzer _audioAnalyzer;
    private readonly SocialCoordinatorService _social;
    private readonly PetAnimationController _animator = new();

    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _dismissHideTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrameSeconds;
    private CancellationTokenSource? _bubbleCts;

    private FriendEntry _friend;
    private string _appliedAppearanceSignature = string.Empty;
    private PetRigHandle? _currentRig;

    private bool _isPointerPressed;
    private bool _isDraggingWindow;
    private Point _pressScreenPoint;
    private Point _lastScreenPoint;

    public string FriendPetId => _friend.PetId;

    public VisitorPetWindow(
        FriendEntry friend,
        PetAppearance initialAppearance,
        IPetMeshFactory meshFactory,
        IAudioAnalyzer audioAnalyzer,
        SocialCoordinatorService social)
    {
        InitializeComponent();

        _friend = friend;
        _meshFactory = meshFactory;
        _audioAnalyzer = audioAnalyzer;
        _social = social;

        LightsVisual.Content = _meshFactory.CreateStudioLights();
        ApplyFriendState(friend, initialAppearance);
        var initialPeer = _social.DiscoveredPeers.FirstOrDefault(p => string.Equals(p.PetId, friend.PetId, StringComparison.OrdinalIgnoreCase));
        ApplyFriendSleepState(initialPeer?.IsSleeping ?? false);

        SourceInitialized += (_, _) => Win32WindowHelper.ApplyToolWindowStyle(this);
        _social.StateChanged += OnSocialStateChanged;

        _frameTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = IdleInterval
        };
        _frameTimer.Tick += OnFrameTick;
        _frameTimer.Start();

        _dismissHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _dismissHideTimer.Tick += OnDismissHideTick;

        Closed += (_, _) =>
        {
            _frameTimer.Stop();
            _dismissHideTimer.Stop();
            _social.StateChanged -= OnSocialStateChanged;
            _animator.DetachRig();
            VisitorPetVisual.Content = null;
            LightsVisual.Content = null;
            _currentRig = null;
            _bubbleCts?.Cancel();
            _bubbleCts?.Dispose();
        };
    }

    /// <summary>
    /// 내 펫과 동일한 크기 배율과 투명도를 방문 친구 펫에도 적용한다.
    /// </summary>
    public void ApplyDisplaySettings(double scale, double opacity)
    {
        var clampedScale = PetProfile.NormalizePetScale(scale);
        var clampedOpacity = Math.Clamp(opacity, 0.25, 1.0);

        var prevWidth = Width;
        var prevHeight = Height;

        VisitorViewport.Width = Math.Round(BaseWindowWidth * clampedScale);
        Width = Math.Max(140.0, VisitorViewport.Width);

        var viewportHeight = Math.Max(96.0, Math.Round(BaseViewportHeight * clampedScale));
        VisitorViewport.Height = viewportHeight;
        Height = BaseBubbleHeight + viewportHeight + BaseNameplateHeight;

        VisitorViewport.Opacity = clampedOpacity;
        VisitorSleepBadge.Opacity = clampedOpacity;
        VisitorNameplate.Opacity = clampedOpacity;

        if (IsLoaded)
        {
            Left += (prevWidth - Width) * 0.5;
            Top += prevHeight - Height;
        }
    }

    private void VisitorSurface_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_isDraggingWindow)
        {
            return;
        }

        _dismissHideTimer.Stop();
        SetDismissButtonVisible(true);
    }

    private void VisitorSurface_MouseLeave(object sender, MouseEventArgs e)
    {
        _dismissHideTimer.Stop();
        _dismissHideTimer.Start();
    }

    private void OnDismissHideTick(object? sender, EventArgs e)
    {
        _dismissHideTimer.Stop();
        if (!VisitorViewport.IsMouseOver && !DismissButton.IsMouseOver)
        {
            SetDismissButtonVisible(false);
        }
    }

    private void SetDismissButtonVisible(bool visible)
    {
        if (DismissButton.IsHitTestVisible != visible)
        {
            DismissButton.IsHitTestVisible = visible;
            DismissButton.BeginAnimation(OpacityProperty,
                new DoubleAnimation(visible ? 1.0 : 0.0, TimeSpan.FromMilliseconds(visible ? 160 : 200)));
        }
    }

    public void PlaceBeside(Window hostWindow)
    {
        var workArea = SystemParameters.WorkArea;
        var leftCandidate = hostWindow.Left - Width + 14;
        Left = leftCandidate >= workArea.Left
            ? leftCandidate
            : Math.Min(workArea.Right - Width, hostWindow.Left + hostWindow.Width - 14);
        Top = Math.Clamp(hostWindow.Top + hostWindow.ActualHeight - Height, workArea.Top, workArea.Bottom - Height);
    }

    public void ApplyFriendState(FriendEntry friend, PetAppearance appearance)
    {
        _friend = friend;
        VisitorNameText.Text = SocialCoordinatorService.FormatDisplayName(friend.OwnerName, friend.PetName);

        var sig = $"{appearance.SpeciesId}|{appearance.BodyColorHex}|{appearance.AccentColorHex}|{appearance.ExpressionId}|{appearance.HeadAccessoryId}|{appearance.AuraEffectId}";
        if (sig == _appliedAppearanceSignature)
        {
            return;
        }

        _appliedAppearanceSignature = sig;
        var structureSig = PetRigHandle.BuildStructureSignature(appearance);
        if (_currentRig is not null &&
            string.Equals(_currentRig.AppearanceStructureSignature, structureSig, StringComparison.Ordinal))
        {
            _currentRig.UpdateColors(appearance.BodyColorHex, appearance.AccentColorHex);
            _animator.UpdateAccentColor(appearance.AccentColorHex);
            return;
        }

        var rig = _meshFactory.BuildPetRig(appearance, PetRigEffects.MusicParticles);
        _currentRig = rig;
        VisitorPetVisual.Content = rig.RootModel;
        _animator.AttachRig(rig, appearance.AccentColorHex);
    }

    private void ApplyFriendSleepState(bool isSleeping)
    {
        _animator.SetSleeping(isSleeping);
        VisitorSleepBadge.Visibility = isSleeping ? Visibility.Visible : Visibility.Collapsed;
    }

    public async void ShowVisitorBubble(string message, int durationMs = 2200)
    {
        _bubbleCts?.Cancel();
        _bubbleCts?.Dispose();
        _bubbleCts = new CancellationTokenSource();
        var token = _bubbleCts.Token;

        VisitorBubbleText.Text = message;
        VisitorBubble.Visibility = Visibility.Visible;

        try
        {
            await Task.Delay(durationMs, token);
            if (!token.IsCancellationRequested)
            {
                VisitorBubble.Visibility = Visibility.Collapsed;
            }
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void OnSocialStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var latestFriend = _social.Friends.FirstOrDefault(f => string.Equals(f.PetId, _friend.PetId, StringComparison.OrdinalIgnoreCase));
            var livePeer = _social.DiscoveredPeers.FirstOrDefault(p => string.Equals(p.PetId, _friend.PetId, StringComparison.OrdinalIgnoreCase));

            if (latestFriend is not null)
            {
                var appearance = livePeer?.Appearance ?? latestFriend.CachedAppearance;
                ApplyFriendState(latestFriend, appearance);
            }

            ApplyFriendSleepState(livePeer?.IsSleeping ?? false);
        });
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _lastFrameSeconds;
        _lastFrameSeconds = now;

        // 로컬 음악 또는 노래 따라하기 프레임에 맞춰 내 펫과 함께 춤춤
        var localAudio = _audioAnalyzer.GetCurrentSnapshot();
        var isFollowing = _social.TryGetFollowedMusicFrame(localAudio.IsMusicActive, out var followed);
        var interval = localAudio.IsMusicActive || isFollowing || VisitorViewport.IsMouseOver || _animator.HasInteractiveMotion
            ? ActiveInterval : IdleInterval;
        if (_frameTimer.Interval != interval)
        {
            _frameTimer.Interval = interval;
        }
        if (isFollowing)
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
        if (PresentationSource.FromVisual(VisitorViewport) is null
            || !Win32WindowHelper.TryGetGlobalCursorPosition(out var physicalCursor))
        {
            lookX = 0;
            lookY = 0;
            return;
        }

        var cursor = VisitorViewport.PointFromScreen(physicalCursor);
        lookX = Math.Clamp((cursor.X - VisitorViewport.ActualWidth * 0.5) / 420.0, -1.0, 1.0);
        lookY = Math.Clamp((cursor.Y - VisitorViewport.ActualHeight * 0.5) / 360.0, -1.0, 1.0);
    }

    private void VisitorViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!HitTestVisitorMesh(e.GetPosition(VisitorViewport)))
        {
            return;
        }

        _isPointerPressed = true;
        _isDraggingWindow = false;
        _pressScreenPoint = PointToScreen(e.GetPosition(this));
        _lastScreenPoint = _pressScreenPoint;
        VisitorViewport.CaptureMouse();
        e.Handled = true;
    }

    private void VisitorViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPointerPressed || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentScreen = PointToScreen(e.GetPosition(this));
        var dx = currentScreen.X - _pressScreenPoint.X;
        var dy = currentScreen.Y - _pressScreenPoint.Y;
        if (!_isDraggingWindow && Math.Sqrt(dx * dx + dy * dy) > DragThresholdPixels)
        {
            _isDraggingWindow = true;
        }

        if (_isDraggingWindow)
        {
            var source = PresentationSource.FromVisual(this);
            var dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            Left += (currentScreen.X - _lastScreenPoint.X) / dpiX;
            Top += (currentScreen.Y - _lastScreenPoint.Y) / dpiY;
        }

        _lastScreenPoint = currentScreen;
    }

    private async void VisitorViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPointerPressed)
        {
            return;
        }

        _isPointerPressed = false;
        VisitorViewport.ReleaseMouseCapture();

        if (_isDraggingWindow)
        {
            _isDraggingWindow = false;
        }
        else
        {
            _animator.TriggerPetReaction();
            var ok = await _social.SendPetInteractionAsync(_friend);
            ShowVisitorBubble(ok ? "쓰다듬기 전송!" : "오프라인 상태예요", 1800);
        }

        e.Handled = true;
    }

    private bool HitTestVisitorMesh(Point viewportPoint)
    {
        var hit = false;
        VisualTreeHelper.HitTest(
            VisitorViewport,
            null,
            result =>
            {
                if (result is RayMeshGeometry3DHitTestResult)
                {
                    hit = true;
                    return HitTestResultBehavior.Stop;
                }

                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(viewportPoint));
        return hit;
    }

    private async void PetFriendMenu_Click(object sender, RoutedEventArgs e)
    {
        _animator.TriggerPetReaction();
        var ok = await _social.SendPetInteractionAsync(_friend);
        ShowVisitorBubble(ok ? "쓰다듬기 전송!" : "오프라인 상태예요", 1800);
    }

    private void VisitorContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var isFollowing = string.Equals(_social.FollowingHostPetId, _friend.PetId, StringComparison.OrdinalIgnoreCase);
        var livePeer = _social.DiscoveredPeers.FirstOrDefault(p => string.Equals(p.PetId, _friend.PetId, StringComparison.OrdinalIgnoreCase));
        var isMusicActive = livePeer?.IsMusicActive ?? false;

        if (isFollowing)
        {
            MusicFollowMenuItem.Header = "⏹  노래 따라하기 중지";
            MusicFollowMenuItem.IsEnabled = true;
        }
        else if (livePeer is null)
        {
            MusicFollowMenuItem.Header = "🎵  오프라인";
            MusicFollowMenuItem.IsEnabled = false;
        }
        else if (isMusicActive)
        {
            MusicFollowMenuItem.Header = "🎵  노래 따라하기 시작";
            MusicFollowMenuItem.IsEnabled = true;
        }
        else
        {
            MusicFollowMenuItem.Header = "🎵  음악 재생 중이 아님";
            MusicFollowMenuItem.IsEnabled = false;
        }
    }

    private async void ToggleMusicFollowMenu_Click(object sender, RoutedEventArgs e)
    {
        var wasFollowing = string.Equals(_social.FollowingHostPetId, _friend.PetId, StringComparison.OrdinalIgnoreCase);
        var ok = await _social.ToggleMusicFollowAsync(_friend);
        if (ok)
        {
            ShowVisitorBubble(wasFollowing ? "노래 따라하기 중지" : "노래 따라하기 시작!", 2000);
        }
        else
        {
            ShowVisitorBubble("현재 음악 재생 중이 아니에요", 2000);
        }
    }

    private void DismissMenu_Click(object sender, RoutedEventArgs e) => Close();
}
