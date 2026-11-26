using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using LabPet.Client.Audio;
using LabPet.Client.Diagnostics;
using LabPet.Client.Interop;
using LabPet.Client.Rendering;
using LabPet.Client.Social;
using LabPet.Client.ViewModels;
using LabPet.Shared.Models;
using LabPet.Shared.Transport;

namespace LabPet.Client.Views;

/// <summary>
/// 내 바탕화면 펫 옆에 친구의 3D 펫을 함께 띄워주는 경량 방문 창.
/// </summary>
public partial class VisitorPetWindow : Window, IMessageBubbleTarget, ISnowballTarget
{
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(45);
    internal const int DefaultChatMessageDurationMs = 8000;
    private const double DragThresholdPixels = 5.0;
    private const double LayoutDiffEpsilon = 0.01;
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;

    private readonly record struct AppliedBubbleLayout(
        Rect RootBounds,
        Point BodyOrigin,
        double ScaleX,
        double ScaleY,
        double SurfaceWidth,
        double SurfaceHeight,
        Point TailBaseLeft,
        Point TailTip,
        Point TailBaseRight,
        Brush? TailFill,
        Brush? TailStroke,
        double Opacity)
    {
        public bool IsCloseTo(AppliedBubbleLayout other)
            => Math.Abs(RootBounds.Left - other.RootBounds.Left) < LayoutDiffEpsilon
               && Math.Abs(RootBounds.Top - other.RootBounds.Top) < LayoutDiffEpsilon
               && Math.Abs(RootBounds.Width - other.RootBounds.Width) < LayoutDiffEpsilon
               && Math.Abs(RootBounds.Height - other.RootBounds.Height) < LayoutDiffEpsilon
               && Math.Abs(BodyOrigin.X - other.BodyOrigin.X) < LayoutDiffEpsilon
               && Math.Abs(BodyOrigin.Y - other.BodyOrigin.Y) < LayoutDiffEpsilon
               && Math.Abs(ScaleX - other.ScaleX) < 0.0005
               && Math.Abs(ScaleY - other.ScaleY) < 0.0005
               && Math.Abs(SurfaceWidth - other.SurfaceWidth) < LayoutDiffEpsilon
               && Math.Abs(SurfaceHeight - other.SurfaceHeight) < LayoutDiffEpsilon
               && Math.Abs(TailBaseLeft.X - other.TailBaseLeft.X) < LayoutDiffEpsilon
               && Math.Abs(TailBaseLeft.Y - other.TailBaseLeft.Y) < LayoutDiffEpsilon
               && Math.Abs(TailTip.X - other.TailTip.X) < LayoutDiffEpsilon
               && Math.Abs(TailTip.Y - other.TailTip.Y) < LayoutDiffEpsilon
               && Math.Abs(TailBaseRight.X - other.TailBaseRight.X) < LayoutDiffEpsilon
               && Math.Abs(TailBaseRight.Y - other.TailBaseRight.Y) < LayoutDiffEpsilon
               && ReferenceEquals(TailFill, other.TailFill)
               && ReferenceEquals(TailStroke, other.TailStroke)
               && Math.Abs(Opacity - other.Opacity) < 0.001;
    }

    private readonly IPetMeshFactory _meshFactory;
    private readonly IAudioAnalyzer _audioAnalyzer;
    private readonly SocialCoordinatorService _social;
    private readonly PetAnimationController _animator = new();
    private readonly BubbleMessageStack _chatBubbleStack;
    private readonly TimeProvider _bubbleClock;
    private readonly VisitorPopupOrder _popupOrder;
    private readonly DataTemplate _chatCardTemplate;
    private readonly Action<VisitorPetWindow>? _beforeChatComposerOpen;

    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _dismissHideTimer;
    private readonly DispatcherTimer _composerCloseTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrameSeconds;
    private CancellationTokenSource? _statusBubbleCts;
    private bool _hasStatusBubble;
    private bool _isClosingOrClosed;
    private Rect _currentBubblePhysicalBounds = Rect.Empty;
    private Rect _currentComposerPhysicalBounds = Rect.Empty;
    private bool _isActivatingComposer;
    private bool _popupLayoutQueued;
    private bool _composerNeedsPlacement;
    private bool _composerPositionQueued;
    private bool _bubblePositionQueued;
    private bool _chatItemsChanged;
    private bool _bubbleBelowHead;
    private bool _isOpeningBubblePopup;
    private AppliedBubbleLayout? _lastAppliedBubbleLayout;
    private HwndSource? _windowHwndSource;
    private DispatcherOperation? _pendingPopupLayoutOperation;
    private DispatcherOperation? _pendingBubblePositionOperation;
    private DispatcherOperation? _pendingComposerPositionOperation;
    private int _bubbleLayoutApplyCount;
    private long _lastReceivedOrder;
    private bool _wasComposerOpenOnMouseDown;
    private bool _isSendingComposerMessage;
    private int _composerDraftVersion;

    private FriendEntry _friend;
    private string _appliedAppearanceSignature = string.Empty;
    private readonly SnowballInteractionService? _snowballs;
    private PetRigHandle? _currentRig;
    private bool _snowballSceneOwnsPose;
    private Point3D _nameplateAnchor;
    private double _petOpacity = PetDisplayLayoutHelper.MaxPetOpacity;

    private bool _isPointerPressed;
    private bool _isDraggingWindow;
    private Point _pressScreenPoint;
    private Point _lastScreenPoint;

    public string FriendPetId => _friend.PetId;
    public bool IsAvailableForMessages => !_isClosingOrClosed && IsVisible;
    internal Rect CurrentBubblePhysicalBounds => VisitorBubblePopup.IsOpen ? _currentBubblePhysicalBounds : Rect.Empty;
    internal Rect CurrentComposerPhysicalBounds => VisitorChatComposerPopup.IsOpen ? _currentComposerPhysicalBounds : Rect.Empty;
    internal bool IsChatComposerOpen => VisitorChatComposerPopup.IsOpen;
    internal string ChatComposerInputText
    {
        get => VisitorChatInput.Text;
        set => VisitorChatInput.Text = value;
    }
    internal BubbleMessageStack ChatBubbleStack => _chatBubbleStack;
    internal bool HasStatusBubble => _hasStatusBubble;
    internal string StatusBubbleText => VisitorStatusBubbleText.Text;
    internal double NameplateOffsetY => VisitorNameplateOffset.Y;
    internal Size AppliedViewportSize => new(VisitorViewport.Width, VisitorViewport.Height);
    internal double AppliedViewportOpacity => VisitorViewport.Opacity;
    internal double AppliedNameplateOpacity => VisitorNameplate.Opacity;
    internal double AppliedBubbleOpacity => VisitorBubbleSurface.Opacity;
    internal bool ShowsSleepingPose => _animator.IsSleeping;
    internal string? CrashLogDirectory { get; set; }
    internal Action? BeforeLayoutBubbleForTest { get; set; }
    internal int BubbleLayoutApplyCount => _bubbleLayoutApplyCount;

    internal VisitorPetWindow(
        FriendEntry friend,
        PetAppearance initialAppearance,
        IPetMeshFactory meshFactory,
        IAudioAnalyzer audioAnalyzer,
        SocialCoordinatorService social,
        VisitorPopupOrder? popupOrder = null,
        Action<VisitorPetWindow>? beforeChatComposerOpen = null,
        SnowballInteractionService? snowballs = null,
        TimeProvider? bubbleClock = null)
    {
        InitializeComponent();

        _snowballs = snowballs;
        _bubbleClock = bubbleClock ?? TimeProvider.System;
        _chatBubbleStack = new BubbleMessageStack(PetWindowViewModel.MaxBubbleLines, _bubbleClock);
        _friend = friend;
        _meshFactory = meshFactory;
        _audioAnalyzer = audioAnalyzer;
        _social = social;
        _popupOrder = popupOrder ?? new VisitorPopupOrder();
        _chatCardTemplate = (DataTemplate)FindResource("VisitorChatCardTemplate");
        _beforeChatComposerOpen = beforeChatComposerOpen;

        _chatBubbleStack.Changed += OnChatBubbleStackChanged;
        VisitorBubblePopup.CustomPopupPlacementCallback = GetVisitorBubblePlacements;
        VisitorChatComposerPopup.CustomPopupPlacementCallback = GetVisitorChatComposerPlacements;

        VisitorBubblePopup.Opened += OnBubblePopupOpened;
        VisitorChatComposerPopup.Opened += OnComposerPopupOpened;

        InitializeInteractionOverlay();
        LightsVisual.Content = _meshFactory.CreateStudioLights();
        ApplyFriendState(friend, initialAppearance);
        var initialPeer = _social.DiscoveredPeers.FirstOrDefault(p => string.Equals(p.PetId, friend.PetId, StringComparison.OrdinalIgnoreCase));
        ApplyFriendSleepState(initialPeer?.IsSleeping ?? true);

        SourceInitialized += OnWindowSourceInitialized;
        Loaded += OnWindowLoaded;
        LocationChanged += OnWindowLocationChanged;
        DpiChanged += OnWindowDpiChanged;
        VisitorViewport.SizeChanged += OnVisitorViewportSizeChanged;
        VisitorNameplate.SizeChanged += OnVisitorNameplateSizeChanged;
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
        _composerCloseTimer = new DispatcherTimer { Interval = QuickMessageInputBehavior.DismissDelay };
        _composerCloseTimer.Tick += OnComposerCloseTick;

        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    private void OnBubblePopupOpened(object? sender, EventArgs e)
    {
        if (_isClosingOrClosed)
        {
            return;
        }

        try
        {
            Win32WindowHelper.SetPopupClickThrough(VisitorBubbleSurface);
            ApplyBubbleScreenPosition();
            _popupOrder.UpdateBubble(VisitorBubblePopup, _lastReceivedOrder);
            if (!_isOpeningBubblePopup
                && PresentationSource.FromVisual(VisitorBubbleSurface) is not null
                && PresentationSource.FromVisual(VisitorViewport) is not null
                && !VisualTreeHelper.GetDpi(VisitorBubbleSurface).Equals(VisualTreeHelper.GetDpi(VisitorViewport)))
            {
                QueuePopupLayout();
            }
        }
        catch (Exception ex)
        {
            HandleBubbleLayoutFailure("VisitorBubblePopup.Opened", ex);
        }
    }

    private void OnComposerPopupOpened(object? sender, EventArgs e)
    {
        if (_isClosingOrClosed)
        {
            return;
        }

        try
        {
            ApplyComposerScreenPosition();
            _popupOrder.OpenComposer(VisitorChatComposerPopup);
            QueuePopupLayout(includeComposer: true);
        }
        catch (Exception ex)
        {
            CrashLogWriter.Write("VisitorChatComposerPopup.Opened", ex, BuildBubbleFailureContext(), CrashLogDirectory);
        }
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        Win32WindowHelper.ApplyToolWindowStyle(this);
        _windowHwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowHwndSource?.AddHook(HandleDisplaySettingsChange);
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e) => UpdatePetOverlayLayout();
    private void OnWindowLocationChanged(object? sender, EventArgs e) => QueuePopupLayout(includeComposer: true);
    private void OnWindowDpiChanged(object sender, DpiChangedEventArgs e) => UpdatePetOverlayLayout();
    private void OnVisitorViewportSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePetOverlayLayout();
    private void OnVisitorNameplateSizeChanged(object sender, SizeChangedEventArgs e) => QueuePopupLayout(includeComposer: true);

    private void OnComposerCloseTick(object? sender, EventArgs e)
    {
        _composerCloseTimer.Stop();
        if (!_isClosingOrClosed && !_isActivatingComposer && !QuickMessageInputBehavior.IsActive(VisitorChatComposerCard)
            && !VisitorViewport.IsMouseOver && !VisitorViewport.IsMouseCaptureWithin)
        {
            CloseChatComposer();
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _isClosingOrClosed = true;
        CancelPendingPopupOperations();
        CloseChatComposer();
        CloseBubblePopup();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _isClosingOrClosed = true;
        CancelPendingPopupOperations();
        _frameTimer.Stop();
        _frameTimer.Tick -= OnFrameTick;
        _dismissHideTimer.Stop();
        _dismissHideTimer.Tick -= OnDismissHideTick;
        _composerCloseTimer.Stop();
        _composerCloseTimer.Tick -= OnComposerCloseTick;

        SourceInitialized -= OnWindowSourceInitialized;
        Loaded -= OnWindowLoaded;
        LocationChanged -= OnWindowLocationChanged;
        DpiChanged -= OnWindowDpiChanged;
        Closing -= OnWindowClosing;
        Closed -= OnWindowClosed;
        VisitorViewport.SizeChanged -= OnVisitorViewportSizeChanged;
        VisitorNameplate.SizeChanged -= OnVisitorNameplateSizeChanged;
        VisitorBubblePopup.Opened -= OnBubblePopupOpened;
        VisitorChatComposerPopup.Opened -= OnComposerPopupOpened;
        VisitorBubblePopup.CustomPopupPlacementCallback = null;
        VisitorChatComposerPopup.CustomPopupPlacementCallback = null;

        if (_windowHwndSource is { IsDisposed: false } hwndSource)
        {
            hwndSource.RemoveHook(HandleDisplaySettingsChange);
        }

        _windowHwndSource = null;

        _social.StateChanged -= OnSocialStateChanged;
        _chatBubbleStack.Changed -= OnChatBubbleStackChanged;
        _chatBubbleStack.Dispose();
        CancelAndDisposeStatusBubbleCts();
        _animator.DetachRig();
        VisitorPetVisual.Content = null;
        LightsVisual.Content = null;
        _currentRig = null;
    }

    private void CancelPendingPopupOperations()
    {
        _pendingPopupLayoutOperation?.Abort();
        _pendingPopupLayoutOperation = null;
        _popupLayoutQueued = false;
        _composerNeedsPlacement = false;

        _pendingBubblePositionOperation?.Abort();
        _pendingBubblePositionOperation = null;
        _bubblePositionQueued = false;

        _pendingComposerPositionOperation?.Abort();
        _pendingComposerPositionOperation = null;
        _composerPositionQueued = false;
    }

    /// <summary>
    /// 내 펫과 동일한 크기 배율·창 크기·투명도·네임텍 위치를 방문 친구 펫에도 적용한다.
    /// </summary>
    public void ApplyDisplaySettings(double scale, double opacity)
    {
        var metrics = PetDisplayLayoutHelper.ApplyScaleAndSize(
            this,
            VisitorViewport,
            scale,
            opacity,
            VisitorNameplateRow.Height.Value,
            horizontalAnchorRatio: 0.5);
        _petOpacity = metrics.NormalizedOpacity;

        VisitorViewport.Opacity = _petOpacity;
        VisitorSleepBadge.Opacity = _petOpacity;
        VisitorNameplate.Opacity = _petOpacity;
        VisitorBubbleSurface.Opacity = _petOpacity;
        VisitorChatComposerCard.Opacity = _petOpacity;
        if (DismissButton.IsHitTestVisible)
        {
            DismissButton.BeginAnimation(OpacityProperty,
                new DoubleAnimation(_petOpacity, TimeSpan.FromMilliseconds(80)));
        }

        UpdatePetOverlayLayout();
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
        ScheduleComposerClose();
    }

    private void OnDismissHideTick(object? sender, EventArgs e)
    {
        _dismissHideTimer.Stop();
        if (!VisitorViewport.IsMouseOver && !DismissButton.IsMouseOver
            && _interactionStrip?.IsMouseOver != true)
        {
            SetDismissButtonVisible(false);
        }
    }

    private void SetDismissButtonVisible(bool visible)
    {
        SetInteractionOverlayVisible(visible);
        if (DismissButton.IsHitTestVisible != visible)
        {
            DismissButton.IsHitTestVisible = visible;
            DismissButton.BeginAnimation(OpacityProperty,
                new DoubleAnimation(visible ? _petOpacity : 0.0, TimeSpan.FromMilliseconds(visible ? 160 : 200)));
        }
    }

    private void UpdateNameplatePosition()
    {
        if (PetDisplayLayoutHelper.TryComputeNameplateOffsetY(VisitorViewport, VisitorPetVisual, _nameplateAnchor, out var offsetDip))
        {
            VisitorNameplateOffset.Y = offsetDip;
        }
    }

    private void UpdateDismissButtonPosition()
    {
        var bounds = _currentRig?.BodyBounds ?? Rect3D.Empty;
        if (bounds.IsEmpty || VisitorViewport.ActualWidth < DismissButton.Width
            || VisitorViewport.ActualHeight < DismissButton.Height)
        {
            return;
        }

        var anchor = new Point3D(
            bounds.X + bounds.SizeX * 0.88,
            bounds.Y + bounds.SizeY * 0.86,
            bounds.Z + bounds.SizeZ * 0.70);
        if (VisitorPetVisual.TransformToAncestor(VisitorViewport).TryTransform(anchor, out var position))
        {
            var left = Math.Clamp(position.X + 10, 4, Math.Max(4, VisitorViewport.ActualWidth - DismissButton.Width - 4));
            var stripHeight = DismissButton.Height * 3 + 10;
            var top = Math.Clamp(position.Y - DismissButton.Height, 0,
                Math.Max(0, VisitorViewport.ActualHeight - stripHeight));
            Canvas.SetLeft(DismissButton, left);
            Canvas.SetTop(DismissButton, top);
            PositionInteractionOverlay(left, top);
        }
    }

    public void PlaceBeside(Window hostWindow)
    {
        var workArea = GetLogicalWorkAreaFor(hostWindow);
        var leftCandidate = hostWindow.Left - Width + 14;
        Left = leftCandidate >= workArea.Left
            ? leftCandidate
            : Math.Min(workArea.Right - Width, hostWindow.Left + hostWindow.Width - 14);

        var hostHeight = hostWindow.ActualHeight > 0
            ? hostWindow.ActualHeight
            : (double.IsFinite(hostWindow.Height) && hostWindow.Height > 0 ? hostWindow.Height : Height);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - Height);
        Top = Math.Clamp(hostWindow.Top + hostHeight - Height, workArea.Top, maxTop);
    }

    private Rect GetLogicalWorkAreaFor(Window hostWindow)
    {
        if (Win32WindowHelper.TryGetNearestWorkArea(hostWindow, out var physicalArea))
        {
            var source = PresentationSource.FromVisual(hostWindow) ?? PresentationSource.FromVisual(this);
            var dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            if (dpiX > 0 && dpiY > 0)
            {
                return new Rect(
                    physicalArea.Left / dpiX,
                    physicalArea.Top / dpiY,
                    physicalArea.Width / dpiX,
                    physicalArea.Height / dpiY);
            }
        }

        return SystemParameters.WorkArea;
    }

    public void ApplyFriendState(FriendEntry friend, PetAppearance appearance)
    {
        SnowballMenuItem.DataContext = _snowballs?.ForFriend(friend);
        _friend = friend;
        VisitorNameText.Text = SocialCoordinatorService.FormatDisplayName(friend.OwnerName, friend.PetName);

        var sig = $"{appearance.SpeciesId}|{appearance.BodyColorHex}|{appearance.AccentColorHex}|{appearance.ExpressionId}|{appearance.HeadAccessoryId}|{appearance.AuraEffectId}";
        if (sig == _appliedAppearanceSignature)
        {
            return;
        }

        _appliedAppearanceSignature = sig;
        if (_currentRig is not null &&
            _currentRig.TryUpdateAppearance(appearance))
        {
            _animator.UpdateAccentColor(appearance.AccentColorHex);
            return;
        }

        var rig = _meshFactory.BuildPetRig(appearance, PetRigEffects.MusicParticles);
        _currentRig = rig;
        VisitorPetVisual.Content = rig.RootModel;
        _nameplateAnchor = rig.NameplateAnchor;
        UpdatePetOverlayLayout();
        _animator.AttachRig(rig, appearance.AccentColorHex);
    }

    private void ApplyFriendSleepState(bool isSleeping)
    {
        _animator.SetSleeping(isSleeping);
        UpdateSleepBadgeVisibility();
    }

    private void UpdateSleepBadgeVisibility()
        => VisitorSleepBadge.Visibility = _animator.IsSleeping && !_isPointerPressed ? Visibility.Visible : Visibility.Collapsed;
    public bool TryShowFriendMessage(ReceivedFriendMessage message)
        => ShowChatMessage(message.Text, DefaultChatMessageDurationMs);

    public bool TryShowFriendStatus(string text, int durationMs, bool animate)
    {
        if (!IsAvailableForMessages)
        {
            return false;
        }

        ShowVisitorBubble(text, durationMs);
        if (animate)
        {
            _animator.TriggerPetReaction();
        }
        return true;
    }

    internal bool ShowChatMessage(string text, int durationMs = DefaultChatMessageDurationMs)
    {
        if (_isClosingOrClosed || !IsVisible || Dispatcher.HasShutdownStarted)
        {
            return false;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowChatMessage(text, durationMs));
            return true;
        }

        if (!_chatBubbleStack.Add(new BubbleLine(text ?? string.Empty), durationMs))
        {
            return false;
        }

        _lastReceivedOrder = _popupOrder.NextReceivedOrder();
        _popupOrder.UpdateBubble(VisitorBubblePopup, _lastReceivedOrder);
        _animator.TriggerPetReaction();
        return true;
    }

    public void ShowVisitorBubble(string message, int durationMs = 2200)
    {
        if (_isClosingOrClosed || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowVisitorBubble(message, durationMs));
            return;
        }

        _ = ShowVisitorBubbleCoreAsync(message, durationMs);
    }

    private async Task ShowVisitorBubbleCoreAsync(string message, int durationMs)
    {
        CancelAndDisposeStatusBubbleCts();
        var cts = new CancellationTokenSource();
        _statusBubbleCts = cts;
        var token = cts.Token;

        VisitorStatusBubbleText.Text = message ?? string.Empty;
        _hasStatusBubble = true;
        QueuePopupLayout();

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, durationMs)), _bubbleClock, token);
            if (!token.IsCancellationRequested && !_isClosingOrClosed && ReferenceEquals(_statusBubbleCts, cts))
            {
                _hasStatusBubble = false;
                QueuePopupLayout();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CancelAndDisposeStatusBubbleCts()
    {
        var previous = Interlocked.Exchange(ref _statusBubbleCts, null);
        if (previous is null)
        {
            return;
        }

        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        previous.Dispose();
    }

    private void OnChatBubbleStackChanged(object? sender, EventArgs e)
    {
        if (_isClosingOrClosed || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnChatBubbleStackChanged(sender, e));
            return;
        }

        _chatItemsChanged = true;
        QueuePopupLayout();
    }

    private void UpdatePetOverlayLayout()
    {
        UpdateNameplatePosition();
        UpdateDismissButtonPosition();
        QueuePopupLayout(includeComposer: true);
    }

    private IntPtr HandleDisplaySettingsChange(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is WmDisplayChange or WmSettingChange)
        {
            QueuePopupLayout(includeComposer: true);
        }

        return IntPtr.Zero;
    }

    private void QueuePopupLayout(bool includeComposer = false)
    {
        if (_isClosingOrClosed || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => QueuePopupLayout(includeComposer));
            return;
        }

        if (!IsLoaded)
        {
            return;
        }

        _composerNeedsPlacement |= includeComposer;
        if (_popupLayoutQueued)
        {
            return;
        }

        _popupLayoutQueued = true;
        _pendingPopupLayoutOperation = Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _pendingPopupLayoutOperation = null;
            _popupLayoutQueued = false;
            if (_isClosingOrClosed || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            if (_composerNeedsPlacement && VisitorChatComposerPopup.IsOpen)
            {
                try
                {
                    UpdateComposerDpi();
                    RepositionPopup(VisitorChatComposerPopup);
                }
                catch (Exception ex)
                {
                    CrashLogWriter.Write(
                        "VisitorPetWindow.RepositionComposerPopup",
                        ex,
                        BuildBubbleFailureContext(),
                        CrashLogDirectory);
                }
            }

            _composerNeedsPlacement = false;
            try
            {
                BeforeLayoutBubbleForTest?.Invoke();
                LayoutBubblePopup();
            }
            catch (Exception ex)
            {
                HandleBubbleLayoutFailure("VisitorPetWindow.LayoutBubblePopup", ex);
            }
        });
    }

    private void CloseBubblePopup()
    {
        _pendingBubblePositionOperation?.Abort();
        _pendingBubblePositionOperation = null;
        _bubblePositionQueued = false;

        if (VisitorBubblePopup.IsOpen)
        {
            VisitorBubblePopup.IsOpen = false;
        }

        _popupOrder.RemoveBubble(VisitorBubblePopup);
        VisitorChatCards.Children.Clear();
        VisitorChatCards.Visibility = Visibility.Collapsed;
        VisitorStatusBubble.Visibility = Visibility.Collapsed;
        VisitorBubbleTail.Data = null;
        VisitorBubbleSurface.Width = 0;
        VisitorBubbleSurface.Height = 0;
        VisitorBubbleSurface.Opacity = _petOpacity;
        _currentBubblePhysicalBounds = Rect.Empty;
        _lastAppliedBubbleLayout = null;
        _lastReceivedOrder = 0;
        _chatItemsChanged = true;
    }

    private void HandleBubbleLayoutFailure(string source, Exception exception)
    {
        CrashLogWriter.Write(source, exception, BuildBubbleFailureContext(), CrashLogDirectory);
        try
        {
            CloseBubblePopup();
        }
        catch (Exception closeEx)
        {
            CrashLogWriter.Write($"{source}.CloseFallback", closeEx, null, CrashLogDirectory);
        }
    }

    private string BuildBubbleFailureContext()
        => $"IsLoaded={IsLoaded}, IsClosingOrClosed={_isClosingOrClosed}, PopupOpen={VisitorBubblePopup.IsOpen}, ComposerOpen={VisitorChatComposerPopup.IsOpen}, HasStatus={_hasStatusBubble}, StackCount={_chatBubbleStack.SnapshotItems().Count}, VisualCount={VisitorChatCards.Children.Count}, BelowHead={_bubbleBelowHead}, Bounds={_currentBubblePhysicalBounds}";

    private void LayoutBubblePopup()
    {
        if (_isClosingOrClosed || !IsLoaded || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        var chatItems = _chatBubbleStack.SnapshotItems();
        var hasChat = chatItems.Count > 0;
        if (!hasChat && !_hasStatusBubble)
        {
            CloseBubblePopup();
            return;
        }

        if (PresentationSource.FromVisual(VisitorViewport) is null
            || !TryGetPopupHeadPhysical(out var head))
        {
            CloseBubblePopup();
            return;
        }

        var ownerDpi = NormalizeDpi(VisualTreeHelper.GetDpi(VisitorViewport));
        var workArea = GetPopupWorkArea(ownerDpi);
        if (!IsFinitePositive(workArea))
        {
            CloseBubblePopup();
            return;
        }

        VisitorChatCards.Visibility = hasChat ? Visibility.Visible : Visibility.Collapsed;
        VisitorStatusBubble.Visibility = _hasStatusBubble ? Visibility.Visible : Visibility.Collapsed;
        ArrangeBubbleItems(_bubbleBelowHead, chatItems);
        if (hasChat && VisitorChatCards.Children.Count == 0)
        {
            CloseBubblePopup();
            return;
        }

        // 닫힌 PopupRoot가 하위 트리의 IsLayoutSuspended=true를 유지하므로 먼저 열어 Measure가 건너뛰어지지 않게 한다.
        if (!VisitorBubblePopup.IsOpen)
        {
            VisitorBubbleSurface.Opacity = 0;
            VisitorBubbleSurface.Width = 0;
            VisitorBubbleSurface.Height = 0;
            _isOpeningBubblePopup = true;
            try
            {
                VisitorBubblePopup.IsOpen = true;
            }
            finally
            {
                _isOpeningBubblePopup = false;
            }
        }

        var popupDpi = PresentationSource.FromVisual(VisitorBubbleSurface) is null
            ? ownerDpi
            : NormalizeDpi(VisualTreeHelper.GetDpi(VisitorBubbleSurface));

        var natural = MeasureBubbleContent();
        if (!IsFinitePositive(natural))
        {
            CloseBubblePopup();
            return;
        }

        var bodySize = new Size(natural.Width * ownerDpi.DpiScaleX, natural.Height * ownerDpi.DpiScaleY);
        var placement = VisitorBubblePlacementCalculator.CalculateBubblePlacement(head, bodySize, workArea, ownerDpi);
        if (!IsFinitePositive(placement.Body) || !double.IsFinite(placement.Scale) || placement.Scale <= 0)
        {
            CloseBubblePopup();
            return;
        }

        if (placement.IsBelowHead != _bubbleBelowHead)
        {
            ArrangeBubbleItems(placement.IsBelowHead, chatItems);
            if (hasChat && VisitorChatCards.Children.Count == 0)
            {
                CloseBubblePopup();
                return;
            }

            natural = MeasureBubbleContent();
            if (!IsFinitePositive(natural))
            {
                CloseBubblePopup();
                return;
            }

            bodySize = new Size(natural.Width * ownerDpi.DpiScaleX, natural.Height * ownerDpi.DpiScaleY);
            placement = VisitorBubblePlacementCalculator.CalculateBubblePlacement(head, bodySize, workArea, ownerDpi);
            if (!IsFinitePositive(placement.Body) || !double.IsFinite(placement.Scale) || placement.Scale <= 0)
            {
                CloseBubblePopup();
                return;
            }
        }

        var card = hasChat
            ? VisitorChatCards.Children.OfType<Border>().ElementAtOrDefault(
                placement.IsBelowHead ? 0 : VisitorChatCards.Children.Count - 1)
            : VisitorStatusBubble;
        if (card is null)
        {
            CloseBubblePopup();
            return;
        }

        AlignBubbleCards(placement, natural);
        if (!TryGetNearestCardBounds(placement, natural, card, out var cardBounds))
        {
            CloseBubblePopup();
            return;
        }

        var tail = VisitorBubblePlacementCalculator.CalculateTailPlacement(placement, cardBounds, ownerDpi);

        // 말풍선 본체(작업영역 안쪽 그림자 여백 포함)와 작업영역 밖까지 뻗는 꼬리 전체를 한 HWND에 담는다.
        var rootBounds = VisitorBubblePlacementCalculator.CalculateBubbleSurfaceBounds(
            placement, tail, workArea, ownerDpi);
        if (rootBounds.IsEmpty || !IsFinitePositive(rootBounds))
        {
            CloseBubblePopup();
            return;
        }

        Point ToPopup(Point point) => new(
            (point.X - rootBounds.Left) / popupDpi.DpiScaleX,
            (point.Y - rootBounds.Top) / popupDpi.DpiScaleY);

        var bodyOrigin = ToPopup(placement.Body.TopLeft);
        var scaleX = placement.Scale * ownerDpi.DpiScaleX / popupDpi.DpiScaleX;
        var scaleY = placement.Scale * ownerDpi.DpiScaleY / popupDpi.DpiScaleY;
        var surfaceWidth = rootBounds.Width / popupDpi.DpiScaleX;
        var surfaceHeight = rootBounds.Height / popupDpi.DpiScaleY;
        var tailBaseLeft = ToPopup(tail.BaseLeft);
        var tailTip = ToPopup(tail.Tip);
        var tailBaseRight = ToPopup(tail.BaseRight);
        if (!IsFinitePoint(bodyOrigin)
            || !double.IsFinite(scaleX) || scaleX <= 0
            || !double.IsFinite(scaleY) || scaleY <= 0
            || !double.IsFinite(surfaceWidth) || surfaceWidth <= 0
            || !double.IsFinite(surfaceHeight) || surfaceHeight <= 0
            || !IsFinitePoint(tailBaseLeft) || !IsFinitePoint(tailTip) || !IsFinitePoint(tailBaseRight))
        {
            CloseBubblePopup();
            return;
        }

        var nextLayout = new AppliedBubbleLayout(
            rootBounds,
            bodyOrigin,
            scaleX,
            scaleY,
            surfaceWidth,
            surfaceHeight,
            tailBaseLeft,
            tailTip,
            tailBaseRight,
            card.Background,
            card.BorderBrush,
            _petOpacity);

        // 직전과 동일한 배치 결과이면 도형 재생성과 팝업 왕복 재배치를 생략한다.
        if (_lastAppliedBubbleLayout is { } previous && previous.IsCloseTo(nextLayout) && VisitorBubblePopup.IsOpen)
        {
            ApplyBubbleScreenPosition();
            return;
        }

        Canvas.SetLeft(VisitorBubbleContent, bodyOrigin.X);
        Canvas.SetTop(VisitorBubbleContent, bodyOrigin.Y);
        VisitorBubbleScale.ScaleX = scaleX;
        VisitorBubbleScale.ScaleY = scaleY;
        VisitorBubbleSurface.Width = surfaceWidth;
        VisitorBubbleSurface.Height = surfaceHeight;

        var figure = new PathFigure { StartPoint = tailBaseLeft };
        figure.Segments.Add(new LineSegment(tailTip, true));
        figure.Segments.Add(new LineSegment(tailBaseRight, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        VisitorBubbleTail.Fill = card.Background;
        VisitorBubbleTail.Stroke = card.BorderBrush;
        VisitorBubbleTail.Data = geometry;

        VisitorBubbleSurface.Opacity = _petOpacity;
        _currentBubblePhysicalBounds = rootBounds;
        _lastAppliedBubbleLayout = nextLayout;
        _bubbleLayoutApplyCount++;
        RepositionPopup(VisitorBubblePopup);
        ApplyBubbleScreenPosition();
    }

    private Size MeasureBubbleContent()
    {
        if (VisitorStatusBubble.Visibility == Visibility.Visible)
        {
            VisitorStatusBubbleText.InvalidateMeasure();
            VisitorStatusBubble.InvalidateMeasure();
        }

        if (VisitorChatCards.Visibility == Visibility.Visible)
        {
            foreach (var card in VisitorChatCards.Children.OfType<Border>())
            {
                card.Child?.InvalidateMeasure();
                card.InvalidateMeasure();
            }

            VisitorChatCards.InvalidateMeasure();
        }

        VisitorBubbleContent.InvalidateMeasure();
        VisitorBubbleContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return VisitorBubbleContent.DesiredSize;
    }

    private void ArrangeBubbleItems(bool belowHead, IReadOnlyList<BubbleLine> chatItems)
    {
        if (_chatItemsChanged || _bubbleBelowHead != belowHead || VisitorChatCards.Children.Count != chatItems.Count)
        {
            // 항목 컨테이너 생성 시점을 기다리지 않고 현재 카드들을 바로 실측할 수 있게 유지한다.
            var previousCards = VisitorChatCards.Children.OfType<Border>().ToArray();
            VisitorChatCards.Children.Clear();
            var lines = belowHead ? chatItems.Reverse() : chatItems;
            foreach (var line in lines)
            {
                var card = previousCards.FirstOrDefault(existing => ReferenceEquals(existing.DataContext, line))
                    ?? (Border)_chatCardTemplate.LoadContent();
                card.DataContext = line;
                if (card.Child is TextBlock block)
                {
                    block.Text = line.Text;
                }

                VisitorChatCards.Children.Add(card);
            }
            _chatItemsChanged = false;
        }

        _bubbleBelowHead = belowHead;
        DockPanel.SetDock(VisitorStatusBubble, belowHead ? Dock.Bottom : Dock.Top);
    }

    private void AlignBubbleCards(VisitorBubblePlacementCalculator.BubblePlacement placement, Size naturalBody)
    {
        if (!IsFinitePositive(naturalBody) || !IsFinitePositive(placement.Body)
            || !double.IsFinite(placement.Tip.X))
        {
            return;
        }

        // 가장 긴 카드 때문에 가로 보정이 걸려도, 짧은 카드는 머리에 가까운 가로 위치를 유지한다.
        var anchorX = (placement.Tip.X - placement.Body.Left) * naturalBody.Width / placement.Body.Width;
        if (!double.IsFinite(anchorX))
        {
            return;
        }

        foreach (var card in VisitorChatCards.Children.OfType<Border>().Append(VisitorStatusBubble))
        {
            if (card.Visibility != Visibility.Collapsed)
            {
                var cardWidth = double.IsFinite(card.DesiredSize.Width) ? Math.Max(0, card.DesiredSize.Width) : 0;
                var centeredX = Math.Max(0, naturalBody.Width - cardWidth) / 2;
                var x = VisitorBubblePlacementCalculator.SafeClamp(anchorX - cardWidth / 2, 0, 2 * centeredX);
                if (card.RenderTransform is not TranslateTransform offset || offset.IsFrozen)
                {
                    offset = new TranslateTransform();
                    card.RenderTransform = offset;
                }

                offset.X = x - centeredX;
            }
        }
    }

    private static bool TryGetNearestCardBounds(
        VisitorBubblePlacementCalculator.BubblePlacement placement, Size naturalBody, Border card, out Rect cardBounds)
    {
        cardBounds = Rect.Empty;
        var body = placement.Body;
        if (!IsFinitePositive(body) || !IsFinitePositive(naturalBody) || !IsFinitePositive(card.DesiredSize))
        {
            return false;
        }

        var scaleX = body.Width / naturalBody.Width;
        var scaleY = body.Height / naturalBody.Height;
        if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY) || scaleX <= 0 || scaleY <= 0)
        {
            return false;
        }

        var margin = card.Margin;
        var rawWidth = card.DesiredSize.Width - margin.Left - margin.Right;
        var rawHeight = card.DesiredSize.Height - margin.Top - margin.Bottom;
        if (!double.IsFinite(rawWidth) || !double.IsFinite(rawHeight) || rawWidth <= 0 || rawHeight <= 0)
        {
            return false;
        }

        var width = rawWidth * scaleX;
        var height = rawHeight * scaleY;
        var offsetX = card.RenderTransform is TranslateTransform transform && double.IsFinite(transform.X)
            ? transform.X
            : 0.0;
        var y = placement.IsBelowHead
            ? body.Top + margin.Top * scaleY
            : body.Bottom - margin.Bottom * scaleY - height;
        var x = body.Left + (body.Width - width) / 2 + offsetX * scaleX;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)
            || width <= 0 || height <= 0)
        {
            return false;
        }

        cardBounds = new Rect(x, y, width, height);
        return true;
    }

    private static Rect GetNearestCardBounds(
        VisitorBubblePlacementCalculator.BubblePlacement placement, Size naturalBody, Border card)
        => TryGetNearestCardBounds(placement, naturalBody, card, out var cardBounds) ? cardBounds : Rect.Empty;

    private static void RepositionPopup(Popup popup)
    {
        if (!popup.IsOpen)
        {
            return;
        }

        // WPF는 PlacementTarget이 이동해도 열린 팝업을 자동으로 따라 옮기지 않는다.
        var offset = popup.HorizontalOffset;
        popup.HorizontalOffset = double.IsFinite(offset) ? offset + 0.01 : 0.01;
        popup.HorizontalOffset = double.IsFinite(offset) ? offset : 0.0;
    }

    private void UpdateComposerDpi()
    {
        if (PresentationSource.FromVisual(VisitorViewport) is null
            || PresentationSource.FromVisual(VisitorChatComposerCard) is null)
        {
            return;
        }

        var ownerDpi = NormalizeDpi(VisualTreeHelper.GetDpi(VisitorViewport));
        var popupDpi = NormalizeDpi(VisualTreeHelper.GetDpi(VisitorChatComposerCard));
        VisitorChatComposerScale.ScaleX = ownerDpi.DpiScaleX / popupDpi.DpiScaleX;
        VisitorChatComposerScale.ScaleY = ownerDpi.DpiScaleY / popupDpi.DpiScaleY;
    }

    private CustomPopupPlacement[] GetVisitorChatComposerPlacements(Size popupSize, Size targetSize, Point offset)
    {
        try
        {
            if (PresentationSource.FromVisual(VisitorNameplate) is null
                || PresentationSource.FromVisual(VisitorViewport) is null)
            {
                return [];
            }

            var dpi = NormalizeDpi(VisualTreeHelper.GetDpi(VisitorViewport));
            var nameplateWidth = double.IsFinite(VisitorNameplate.ActualWidth) && VisitorNameplate.ActualWidth >= 0
                ? VisitorNameplate.ActualWidth
                : 0;
            var nameplateHeight = double.IsFinite(VisitorNameplate.ActualHeight) && VisitorNameplate.ActualHeight >= 0
                ? VisitorNameplate.ActualHeight
                : 0;
            var topLeft = VisitorNameplate.PointToScreen(new Point());
            var bottomRight = VisitorNameplate.PointToScreen(new Point(nameplateWidth, nameplateHeight));
            if (!IsFinitePoint(topLeft) || !IsFinitePoint(bottomRight))
            {
                return [];
            }

            var width = Math.Max(0, bottomRight.X - topLeft.X);
            var height = Math.Max(0, bottomRight.Y - topLeft.Y);
            var nameplate = new Rect(topLeft, new Size(width, height));
            _currentComposerPhysicalBounds = VisitorBubblePlacementCalculator.CalculateComposerPlacement(
                nameplate, popupSize, dpi);

            // WPF가 화면 경계에 맞춰 팝업을 밀어낸 뒤에도, 실제 창은 이름표 아래에 둔다.
            if (!_composerPositionQueued && !_isClosingOrClosed && !Dispatcher.HasShutdownStarted)
            {
                _composerPositionQueued = true;
                _pendingComposerPositionOperation = Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                {
                    _pendingComposerPositionOperation = null;
                    _composerPositionQueued = false;
                    ApplyComposerScreenPosition();
                });
            }

            return RelativePopupPlacement(_currentComposerPhysicalBounds.TopLeft);
        }
        catch (Exception ex)
        {
            CrashLogWriter.Write("VisitorPetWindow.GetVisitorChatComposerPlacements", ex, BuildBubbleFailureContext(), CrashLogDirectory);
            return [];
        }
    }

    private void ApplyComposerScreenPosition()
    {
        try
        {
            if (!_isClosingOrClosed && VisitorChatComposerPopup.IsOpen && !_currentComposerPhysicalBounds.IsEmpty)
            {
                Win32WindowHelper.MovePopupPhysical(VisitorChatComposerCard, _currentComposerPhysicalBounds.TopLeft);
            }
        }
        catch (Exception ex)
        {
            CrashLogWriter.Write("VisitorPetWindow.ApplyComposerScreenPosition", ex, BuildBubbleFailureContext(), CrashLogDirectory);
        }
    }

    private void ApplyBubbleScreenPosition()
    {
        try
        {
            if (!_isClosingOrClosed && VisitorBubblePopup.IsOpen && IsFinitePositive(_currentBubblePhysicalBounds))
            {
                Win32WindowHelper.MovePopupPhysical(VisitorBubbleSurface, _currentBubblePhysicalBounds.TopLeft);
            }
        }
        catch (Exception ex)
        {
            CrashLogWriter.Write("VisitorPetWindow.ApplyBubbleScreenPosition", ex, BuildBubbleFailureContext(), CrashLogDirectory);
        }
    }

    private CustomPopupPlacement[] GetVisitorBubblePlacements(Size popupSize, Size targetSize, Point offset)
    {
        if (_currentBubblePhysicalBounds.IsEmpty)
        {
            return [];
        }

        // 꼬리가 작업영역 밖으로 뻗어 WPF가 팝업 창을 모니터 안으로 밀어낸 뒤에도 실제 물리 좌표에 고정한다.
        if (!_bubblePositionQueued && !_isClosingOrClosed && !Dispatcher.HasShutdownStarted)
        {
            _bubblePositionQueued = true;
            _pendingBubblePositionOperation = Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                _pendingBubblePositionOperation = null;
                _bubblePositionQueued = false;
                ApplyBubbleScreenPosition();
            });
        }

        return RelativePopupPlacement(_currentBubblePhysicalBounds.TopLeft);
    }

    private CustomPopupPlacement[] RelativePopupPlacement(Point physicalOrigin)
    {
        if (!IsFinitePoint(physicalOrigin) || PresentationSource.FromVisual(VisitorViewport) is null)
        {
            return [];
        }

        var origin = VisitorViewport.PointToScreen(new Point());
        if (!IsFinitePoint(origin))
        {
            return [];
        }

        return [new CustomPopupPlacement(physicalOrigin - (Vector)origin, PopupPrimaryAxis.None)];
    }

    internal Point GetPopupHeadPhysical()
        => TryGetPopupHeadPhysical(out var head) ? head : default;

    private bool TryGetPopupHeadPhysical(out Point head)
    {
        head = default;
        if (PresentationSource.FromVisual(VisitorViewport) is null)
        {
            return false;
        }

        var viewportWidth = double.IsFinite(VisitorViewport.ActualWidth) && VisitorViewport.ActualWidth >= 0
            ? VisitorViewport.ActualWidth
            : (double.IsFinite(VisitorViewport.Width) && VisitorViewport.Width >= 0 ? VisitorViewport.Width : 0);
        var viewportHeight = double.IsFinite(VisitorViewport.ActualHeight) && VisitorViewport.ActualHeight >= 0
            ? VisitorViewport.ActualHeight
            : (double.IsFinite(VisitorViewport.Height) && VisitorViewport.Height >= 0 ? VisitorViewport.Height : 0);
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return false;
        }

        var bounds = _currentRig?.BodyBounds ?? Rect3D.Empty;
        var inViewport = new Point(viewportWidth / 2, 0);
        if (!bounds.IsEmpty && VisitorViewport.IsAncestorOf(VisitorPetVisual))
        {
            // 움직이는 뼈대 대신 기본 모델의 머리를 투영해 말풍선이 애니메이션에 흔들리지 않게 한다.
            var headAnchor = new Point3D(0, bounds.Y + bounds.SizeY, bounds.Z + bounds.SizeZ * 0.65);
            if (VisitorPetVisual.TransformToAncestor(VisitorViewport).TryTransform(headAnchor, out var projected)
                && IsFinitePoint(projected))
            {
                inViewport = projected;
            }
        }

        head = VisitorViewport.PointToScreen(new Point(
            VisitorBubblePlacementCalculator.SafeClamp(inViewport.X, 0, viewportWidth),
            VisitorBubblePlacementCalculator.SafeClamp(inViewport.Y, 0, viewportHeight)));
        return IsFinitePoint(head);
    }

    private Rect GetPopupWorkArea(DpiScale dpi)
        => Win32WindowHelper.TryGetNearestWorkArea(this, out var area) ? area : GetScaledPrimaryWorkArea(dpi);

    private static Rect GetScaledPrimaryWorkArea(DpiScale dpi)
    {
        dpi = NormalizeDpi(dpi);
        var wa = SystemParameters.WorkArea;
        if (wa.IsEmpty || !double.IsFinite(wa.Width) || !double.IsFinite(wa.Height) || wa.Width <= 0 || wa.Height <= 0)
        {
            return Rect.Empty;
        }

        return new Rect(
            wa.Left * dpi.DpiScaleX,
            wa.Top * dpi.DpiScaleY,
            wa.Width * dpi.DpiScaleX,
            wa.Height * dpi.DpiScaleY);
    }

    private static DpiScale NormalizeDpi(DpiScale dpi)
        => new(
            double.IsFinite(dpi.DpiScaleX) && dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0,
            double.IsFinite(dpi.DpiScaleY) && dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0);

    private static bool IsFinitePositive(Size size)
        => !size.IsEmpty && double.IsFinite(size.Width) && double.IsFinite(size.Height)
            && size.Width > 0 && size.Height > 0;

    private static bool IsFinitePositive(Rect rect)
        => !rect.IsEmpty
            && double.IsFinite(rect.Left) && double.IsFinite(rect.Top)
            && double.IsFinite(rect.Width) && double.IsFinite(rect.Height)
            && rect.Width > 0 && rect.Height > 0;

    private static bool IsFinitePoint(Point point)
        => double.IsFinite(point.X) && double.IsFinite(point.Y);

    private void OnSocialStateChanged(object? sender, EventArgs e)
    {
        if (_isClosingOrClosed || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosingOrClosed)
            {
                return;
            }

            var latestFriend = _social.Friends.FirstOrDefault(f => string.Equals(f.PetId, _friend.PetId, StringComparison.OrdinalIgnoreCase));
            var livePeer = _social.DiscoveredPeers.FirstOrDefault(p => string.Equals(p.PetId, _friend.PetId, StringComparison.OrdinalIgnoreCase));

            if (latestFriend is not null)
            {
                var appearance = livePeer?.Appearance ?? latestFriend.CachedAppearance;
                ApplyFriendState(latestFriend, appearance);
            }

            ApplyFriendSleepState(livePeer?.IsSleeping ?? true);
            if (_social.IsInteractionBlocked(_friend.PetId))
            {
                CloseChatComposer();
            }
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
        if (!_snowballSceneOwnsPose)
            _animator.UpdateFrame(dt, lookX, lookY, VisitorBubblePopup.IsOpen);
    }

    internal bool TryUseSnowballPose()
    {
        Dispatcher.VerifyAccess();
        if (_snowballSceneOwnsPose || !IsAvailableForMessages || _currentRig is null) return false;
        _snowballSceneOwnsPose = true;
        return true;
    }

    internal bool IsSnowballThrowBlocked => _animator.IsSnowballThrowBlocked;

    internal bool TryGetSnowballTargetPhysical(out Point target)
    {
        target = default;
        return IsAvailableForMessages
            && PetDisplayLayoutHelper.TryGetSnowballBodyPhysical(VisitorViewport, VisitorPetVisual, _currentRig, out target);
    }

    internal void TriggerSnowballHit()
    {
        if (IsAvailableForMessages) _animator.TriggerSnowballHit();
    }

    bool ISnowballTarget.TryGetSnowballTargetPhysical(out Point target) => TryGetSnowballTargetPhysical(out target);
    void ISnowballTarget.TriggerSnowballHit() => TriggerSnowballHit();

    internal bool AdvanceSnowballPose(SnowballActorPhase phase, double? startThrowFrom, double seconds, double lookX, double lookY)
    {
        Dispatcher.VerifyAccess();
        if (!_snowballSceneOwnsPose || !IsAvailableForMessages || _currentRig is null
            || (startThrowFrom is { } elapsed && !_animator.TryStartSnowballThrow(elapsed))) return false;
        _animator.SetSnowballLaughing(phase == SnowballActorPhase.Laughing);
        _animator.UpdateFrame(seconds, lookX, lookY, VisitorBubblePopup.IsOpen);
        return true;
    }

    internal bool TryGetSnowballHandPhysical(out Point hand)
    {
        Dispatcher.VerifyAccess();
        hand = default;
        return IsAvailableForMessages
            && PetDisplayLayoutHelper.TryGetSnowballHandPhysical(VisitorViewport, VisitorPetVisual, _currentRig, out hand);
    }

    internal void ReleaseSnowballPose()
    {
        Dispatcher.VerifyAccess();
        if (!_snowballSceneOwnsPose) return;
        _snowballSceneOwnsPose = false;
        _animator.CancelSnowballInteraction(preserveHit: true);
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
        if (!HitTestVisitorMesh(e.GetPosition(VisitorViewport)) || !VisitorViewport.CaptureMouse())
        {
            return;
        }

        _wasComposerOpenOnMouseDown = VisitorChatComposerPopup.IsOpen;
        _isPointerPressed = true;
        _isDraggingWindow = false;
        _pressScreenPoint = PointToScreen(e.GetPosition(this));
        _lastScreenPoint = _pressScreenPoint;
        _animator.SetGrabbed(true);
        UpdateSleepBadgeVisibility();
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

    private void VisitorViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPointerPressed)
        {
            return;
        }

        var wasDragging = _isDraggingWindow;
        var wasComposerOpen = _wasComposerOpenOnMouseDown;
        EndVisitorGrab();
        VisitorViewport.ReleaseMouseCapture();
        if (!wasDragging)
        {
            HandleVisitorClick(wasComposerOpen);
        }

        e.Handled = true;
    }

    private void VisitorViewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isPointerPressed && !VisitorViewport.IsMouseCaptured)
        {
            EndVisitorGrab();
        }
    }

    private void EndVisitorGrab()
    {
        _isPointerPressed = false;
        _isDraggingWindow = false;
        _wasComposerOpenOnMouseDown = false;
        _animator.SetGrabbed(false);
        UpdateSleepBadgeVisibility();
        ScheduleComposerClose();
    }

    internal void HandleVisitorClick(bool wasComposerOpen = false)
    {
        if (wasComposerOpen || VisitorChatComposerPopup.IsOpen)
        {
            CloseChatComposer();
        }
        else
        {
            OpenChatComposer();
        }
    }

    internal void OpenChatComposer()
    {
        if (_isClosingOrClosed || Dispatcher.HasShutdownStarted || VisitorChatComposerPopup.IsOpen)
        {
            return;
        }

        if (_social.IsInteractionBlocked(_friend.PetId))
        {
            ShowVisitorBubble(FriendOperationErrorCodes.PomodoroFocusMessage, 2200);
            return;
        }

        _beforeChatComposerOpen?.Invoke(this);
        VisitorChatInput.ToolTip = $"{SocialCoordinatorService.FormatDisplayName(_friend.OwnerName, _friend.PetName)}에게 메시지";
        _composerCloseTimer.Stop();
        _isActivatingComposer = true;
        VisitorChatComposerScale.ScaleX = 1;
        VisitorChatComposerScale.ScaleY = 1;
        VisitorChatComposerPopup.IsOpen = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_isClosingOrClosed || !VisitorChatComposerPopup.IsOpen)
            {
                return;
            }

            QuickMessageInputBehavior.Focus(VisitorChatInput);
            VisitorChatInput.CaretIndex = VisitorChatInput.Text.Length;
            _isActivatingComposer = false;
        });
    }

    internal void CloseChatComposer()
    {
        _isActivatingComposer = false;
        _composerCloseTimer.Stop();
        if (!VisitorChatComposerPopup.IsOpen && _currentComposerPhysicalBounds.IsEmpty)
        {
            return;
        }

        VisitorChatComposerPopup.IsOpen = false;
        _popupOrder.CloseComposer(VisitorChatComposerPopup);
        _currentComposerPhysicalBounds = Rect.Empty;
    }

    private void ScheduleComposerClose()
    {
        if (!_isClosingOrClosed && VisitorChatComposerPopup.IsOpen)
        {
            _composerCloseTimer.Stop();
            _composerCloseTimer.Start();
        }
    }

    private void VisitorChatInput_TextChanged(object sender, TextChangedEventArgs e)
        => _composerDraftVersion++;

    private void VisitorChatInput_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        => QuickMessageInputBehavior.Focus(VisitorChatInput);

    private void VisitorChatComposerCard_MouseEnter(object sender, MouseEventArgs e)
        => _composerCloseTimer.Stop();

    private void VisitorChatComposerCard_MouseLeave(object sender, MouseEventArgs e)
        => ScheduleComposerClose();

    private void VisitorChatComposerCard_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => ScheduleComposerClose();

    private void VisitorChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = SendVisitorChatMessageAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseChatComposer();
            e.Handled = true;
        }
    }

    private void SendVisitorChatButton_Click(object sender, RoutedEventArgs e)
    {
        QuickMessageInputBehavior.Focus(VisitorChatInput);
        _ = SendVisitorChatMessageAsync();
    }

    private void CloseVisitorChatButton_Click(object sender, RoutedEventArgs e)
        => CloseChatComposer();

    internal async Task<bool> SendVisitorChatMessageAsync()
    {
        var text = VisitorChatInput.Text.Trim();
        if (string.IsNullOrEmpty(text) || _isClosingOrClosed || _isSendingComposerMessage)
        {
            return false;
        }

        if (_social.IsInteractionBlocked(_friend.PetId))
        {
            CloseChatComposer();
            ShowVisitorBubble(FriendOperationErrorCodes.PomodoroFocusMessage, 2200);
            return false;
        }

        var revision = _composerDraftVersion;
        _isSendingComposerMessage = true;
        try
        {
            var result = await _social.SendBubbleMessageDetailedAsync(_friend, text);
            if (_isClosingOrClosed)
            {
                return result.Succeeded;
            }

            if (!result.Succeeded)
            {
                ShowVisitorBubble(
                    result.IsPomodoroFocusBlocked
                        ? FriendOperationErrorCodes.PomodoroFocusMessage
                        : "메시지 전송 실패",
                    2200);
                return false;
            }

            if (revision == _composerDraftVersion)
            {
                VisitorChatInput.Clear();
            }

            _animator.TriggerPetReaction();
            return true;
        }
        finally
        {
            _isSendingComposerMessage = false;
        }
    }

    private bool HitTestVisitorMesh(Point viewportPoint)
    {
        var hit = false;
        VisualTreeHelper.HitTest(
            VisitorViewport,
            null,
            result =>
            {
                if (result is RayMeshGeometry3DHitTestResult meshHit &&
                    !PetEffectHitTest.IsDecorative(meshHit.ModelHit))
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
        var result = await _social.SendPetInteractionAsync(_friend);
        if (result == PetInteractionSendResult.PomodoroFocusBlocked)
        {
            ShowVisitorBubble(FriendOperationErrorCodes.PomodoroFocusMessage, 2200);
        }
        else if (result == PetInteractionSendResult.Unavailable)
        {
            ShowVisitorBubble("인사하기를 보내지 못했어요", 1800);
        }
    }

    private void VisitorContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var isFollowing = string.Equals(_social.FollowingHostPetId, _friend.PetId, StringComparison.OrdinalIgnoreCase);
        var livePeer = _social.DiscoveredPeers.FirstOrDefault(p => string.Equals(p.PetId, _friend.PetId, StringComparison.OrdinalIgnoreCase));
        var isMusicActive = livePeer?.IsMusicActive ?? false;
        var isFocusBlocked = _social.IsInteractionBlocked(_friend.PetId);

        PetFriendMenuItem.IsEnabled = !isFocusBlocked && livePeer is not null && !livePeer.RequiresUpdate;
        PetFriendMenuItem.ToolTip = isFocusBlocked ? FriendOperationErrorCodes.PomodoroFocusMessage : null;

        if (isFollowing)
        {
            MusicFollowMenuItem.Header = "⏹  노래 따라하기 중지";
            MusicFollowMenuItem.ToolTip = null;
            MusicFollowMenuItem.IsEnabled = true;
        }
        else if (isFocusBlocked)
        {
            MusicFollowMenuItem.Header = $"✏️  {FriendOperationErrorCodes.PomodoroFocusMessage}";
            MusicFollowMenuItem.ToolTip = FriendOperationErrorCodes.PomodoroFocusMessage;
            MusicFollowMenuItem.IsEnabled = false;
        }
        else if (livePeer is null)
        {
            MusicFollowMenuItem.Header = "🎵  오프라인";
            MusicFollowMenuItem.ToolTip = null;
            MusicFollowMenuItem.IsEnabled = false;
        }
        else if (isMusicActive)
        {
            MusicFollowMenuItem.Header = "🎵  노래 따라하기 시작";
            MusicFollowMenuItem.ToolTip = null;
            MusicFollowMenuItem.IsEnabled = true;
        }
        else
        {
            MusicFollowMenuItem.Header = "🎵  음악 재생 중이 아님";
            MusicFollowMenuItem.ToolTip = null;
            MusicFollowMenuItem.IsEnabled = false;
        }
    }

    private async void ToggleMusicFollowMenu_Click(object sender, RoutedEventArgs e)
    {
        var wasFollowing = string.Equals(_social.FollowingHostPetId, _friend.PetId, StringComparison.OrdinalIgnoreCase);
        var result = await _social.ToggleMusicFollowDetailedAsync(_friend);
        if (result.Succeeded)
        {
            ShowVisitorBubble(wasFollowing ? "노래 따라하기 중지" : "노래 따라하기 시작!", 2000);
        }
        else
        {
            ShowVisitorBubble(
                result.IsPomodoroFocusBlocked
                    ? FriendOperationErrorCodes.PomodoroFocusMessage
                    : "현재 음악 재생 중이 아니에요",
                2200);
        }
    }

    private void DismissMenu_Click(object sender, RoutedEventArgs e) => Close();
}
