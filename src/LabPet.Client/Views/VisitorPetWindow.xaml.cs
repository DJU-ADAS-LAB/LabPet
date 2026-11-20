using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
    private const double DragThresholdPixels = 5.0;

    private readonly IPetMeshFactory _meshFactory;
    private readonly IAudioAnalyzer _audioAnalyzer;
    private readonly SocialCoordinatorService _social;
    private readonly PetAnimationController _animator = new();

    private readonly DispatcherTimer _frameTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrameSeconds;
    private CancellationTokenSource? _bubbleCts;

    private FriendEntry _friend;
    private string _appliedAppearanceSignature = string.Empty;

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

        SourceInitialized += (_, _) => Win32WindowHelper.ApplyToolWindowStyle(this);
        _social.StateChanged += OnSocialStateChanged;

        _frameTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _frameTimer.Tick += OnFrameTick;
        _frameTimer.Start();

        Closed += (_, _) =>
        {
            _frameTimer.Stop();
            _social.StateChanged -= OnSocialStateChanged;
            _bubbleCts?.Cancel();
            _bubbleCts?.Dispose();
        };
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
        var rig = _meshFactory.BuildPetRig(appearance);
        VisitorPetVisual.Content = rig.RootModel;
        _animator.AttachRig(rig, appearance.AccentColorHex);
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
        });
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _lastFrameSeconds;
        _lastFrameSeconds = now;

        // 로컬 음악 또는 노래 따라하기 프레임에 맞춰 내 펫과 함께 춤춤
        var localAudio = _audioAnalyzer.GetCurrentSnapshot();
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

    private async void ToggleMusicFollowMenu_Click(object sender, RoutedEventArgs e)
    {
        var ok = await _social.ToggleMusicFollowAsync(_friend);
        ShowVisitorBubble(ok ? "노래 따라하기 전환!" : "현재 음악 재생 중이 아니에요", 2000);
    }

    private void DismissMenu_Click(object sender, RoutedEventArgs e) => Close();
}
