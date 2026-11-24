using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using LabPet.Client.Audio;
using LabPet.Client.Interop;
using LabPet.Client.Rendering;
using LabPet.Client.Social;
using LabPet.Client.ViewModels;
using LabPet.Shared.Models;

namespace LabPet.Client.Views;

/// <summary>
/// 내 바탕화면 펫 옆에 친구의 3D 펫을 함께 띄워주는 경량 방문 창.
/// </summary>
public partial class VisitorPetWindow : Window, IMessageBubbleTarget
{
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(45);
    internal const int DefaultChatMessageDurationMs = 8000;
    private const double DragThresholdPixels = 5.0;
    private const double SpeechBubbleBottomOverlap = 8.0;
    private const double ChatComposerBottomOverlap = 4.0;
    private const double BubbleCollisionGap = 8.0;

    private readonly IPetMeshFactory _meshFactory;
    private readonly IAudioAnalyzer _audioAnalyzer;
    private readonly SocialCoordinatorService _social;
    private readonly PetAnimationController _animator = new();
    private readonly BubbleMessageStack _chatBubbleStack = new(PetWindowViewModel.MaxBubbleLines);
    private readonly Func<string, IReadOnlyList<Rect>>? _occupiedBubbleBoundsProvider;
    private readonly Action? _onBubbleLayoutChanged;
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
    private bool _tailAlignmentQueued;
    private bool _wasComposerOpenOnMouseDown;
    private bool _isSendingComposerMessage;
    private int _composerDraftVersion;

    private FriendEntry _friend;
    private string _appliedAppearanceSignature = string.Empty;
    private PetRigHandle? _currentRig;
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
    internal double AppliedBubbleOpacity => VisitorBubbleContent.Opacity;
    internal bool ShowsSleepingPose => _animator.IsSleeping;

    public VisitorPetWindow(
        FriendEntry friend,
        PetAppearance initialAppearance,
        IPetMeshFactory meshFactory,
        IAudioAnalyzer audioAnalyzer,
        SocialCoordinatorService social,
        Func<string, IReadOnlyList<Rect>>? occupiedBubbleBoundsProvider = null,
        Action? onBubbleLayoutChanged = null,
        Action<VisitorPetWindow>? beforeChatComposerOpen = null)
    {
        InitializeComponent();

        _friend = friend;
        _meshFactory = meshFactory;
        _audioAnalyzer = audioAnalyzer;
        _social = social;
        _occupiedBubbleBoundsProvider = occupiedBubbleBoundsProvider;
        _onBubbleLayoutChanged = onBubbleLayoutChanged;
        _beforeChatComposerOpen = beforeChatComposerOpen;

        VisitorChatItemsControl.ItemsSource = _chatBubbleStack.Items;
        _chatBubbleStack.Changed += OnChatBubbleStackChanged;
        VisitorBubblePopup.CustomPopupPlacementCallback = GetVisitorBubblePlacements;
        VisitorChatComposerPopup.CustomPopupPlacementCallback = GetVisitorChatComposerPlacements;

        LightsVisual.Content = _meshFactory.CreateStudioLights();
        ApplyFriendState(friend, initialAppearance);
        var initialPeer = _social.DiscoveredPeers.FirstOrDefault(p => string.Equals(p.PetId, friend.PetId, StringComparison.OrdinalIgnoreCase));
        ApplyFriendSleepState(initialPeer?.IsSleeping ?? true);

        SourceInitialized += (_, _) => Win32WindowHelper.ApplyToolWindowStyle(this);
        Loaded += (_, _) =>
        {
            UpdateNameplatePosition();
            UpdateDismissButtonPosition();
            RepositionChatComposerPopup();
            RepositionBubblePopup();
        };
        LocationChanged += (_, _) =>
        {
            UpdateNameplatePosition();
            RepositionChatComposerPopup();
            RepositionBubblePopup();
            _onBubbleLayoutChanged?.Invoke();
        };
        SizeChanged += (_, _) =>
        {
            RepositionChatComposerPopup();
            RepositionBubblePopup();
            _onBubbleLayoutChanged?.Invoke();
        };
        VisitorViewport.SizeChanged += (_, _) =>
        {
            UpdateNameplatePosition();
            UpdateDismissButtonPosition();
            RepositionChatComposerPopup();
            RepositionBubblePopup();
            _onBubbleLayoutChanged?.Invoke();
        };
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
        _composerCloseTimer.Tick += (_, _) =>
        {
            _composerCloseTimer.Stop();
            if (!_isActivatingComposer && !QuickMessageInputBehavior.IsActive(VisitorChatComposerCard)
                && !VisitorViewport.IsMouseOver && !VisitorViewport.IsMouseCaptureWithin)
            {
                CloseChatComposer();
            }
        };

        Closing += (_, _) =>
        {
            _isClosingOrClosed = true;
            _currentBubblePhysicalBounds = Rect.Empty;
            _currentComposerPhysicalBounds = Rect.Empty;
            VisitorChatComposerPopup.IsOpen = false;
            VisitorBubblePopup.IsOpen = false;
        };

        Closed += (_, _) =>
        {
            _isClosingOrClosed = true;
            _frameTimer.Stop();
            _dismissHideTimer.Stop();
            _composerCloseTimer.Stop();
            _social.StateChanged -= OnSocialStateChanged;
            _chatBubbleStack.Changed -= OnChatBubbleStackChanged;
            _chatBubbleStack.Dispose();
            CancelAndDisposeStatusBubbleCts();
            _currentBubblePhysicalBounds = Rect.Empty;
            _currentComposerPhysicalBounds = Rect.Empty;
            VisitorChatComposerPopup.IsOpen = false;
            VisitorBubblePopup.IsOpen = false;
            _animator.DetachRig();
            VisitorPetVisual.Content = null;
            LightsVisual.Content = null;
            _currentRig = null;
        };
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
        VisitorBubbleContent.Opacity = _petOpacity;
        VisitorChatComposerCard.Opacity = _petOpacity;
        if (DismissButton.IsHitTestVisible)
        {
            DismissButton.BeginAnimation(OpacityProperty,
                new DoubleAnimation(_petOpacity, TimeSpan.FromMilliseconds(80)));
        }

        UpdateNameplatePosition();

        if (IsLoaded)
        {
            RepositionChatComposerPopup();
            RepositionBubblePopup();
            _onBubbleLayoutChanged?.Invoke();
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
        ScheduleComposerClose();
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
            Canvas.SetLeft(DismissButton, Math.Clamp(position.X, 0, VisitorViewport.ActualWidth - DismissButton.Width));
            Canvas.SetTop(DismissButton, Math.Clamp(position.Y - DismissButton.Height, 0, VisitorViewport.ActualHeight - DismissButton.Height));
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
        UpdateNameplatePosition();
        if (IsLoaded)
        {
            UpdateDismissButtonPosition();
        }
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

        if (!_chatBubbleStack.Add(new BubbleLine(text), durationMs))
        {
            return false;
        }

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

        VisitorStatusBubbleText.Text = message;
        _hasStatusBubble = true;
        UpdateBubbleVisualState();

        try
        {
            await Task.Delay(Math.Max(1, durationMs), token);
            if (!token.IsCancellationRequested && !_isClosingOrClosed && ReferenceEquals(_statusBubbleCts, cts))
            {
                _hasStatusBubble = false;
                UpdateBubbleVisualState();
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
        if (_isClosingOrClosed)
        {
            return;
        }

        UpdateBubbleVisualState();
    }

    private void UpdateBubbleVisualState()
    {
        if (_isClosingOrClosed)
        {
            VisitorBubblePopup.IsOpen = false;
            _currentBubblePhysicalBounds = Rect.Empty;
            return;
        }

        var hasChat = _chatBubbleStack.HasItems;
        VisitorChatItemsControl.Visibility = hasChat ? Visibility.Visible : Visibility.Collapsed;
        VisitorStatusBubble.Visibility = _hasStatusBubble ? Visibility.Visible : Visibility.Collapsed;

        var shouldOpen = hasChat || _hasStatusBubble;
        VisitorBubblePopup.IsOpen = shouldOpen;
        if (!shouldOpen)
        {
            _currentBubblePhysicalBounds = Rect.Empty;
        }
        else
        {
            RepositionBubblePopup();
        }

        _onBubbleLayoutChanged?.Invoke();
    }

    private void VisitorBubbleContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RepositionBubblePopup();
        _onBubbleLayoutChanged?.Invoke();
    }

    private void VisitorChatComposerCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RepositionChatComposerPopup();
        RepositionBubblePopup();
        _onBubbleLayoutChanged?.Invoke();
    }

    public void RepositionChatComposerPopup()
    {
        if (_isClosingOrClosed || !VisitorChatComposerPopup.IsOpen)
        {
            return;
        }

        var offset = VisitorChatComposerPopup.HorizontalOffset;
        VisitorChatComposerPopup.HorizontalOffset = offset + 0.01;
        VisitorChatComposerPopup.HorizontalOffset = offset;
    }

    public void RepositionBubblePopup()
    {
        if (_isClosingOrClosed || !VisitorBubblePopup.IsOpen)
        {
            return;
        }

        var offset = VisitorBubblePopup.HorizontalOffset;
        VisitorBubblePopup.HorizontalOffset = offset + 0.01;
        VisitorBubblePopup.HorizontalOffset = offset;

        if (!_tailAlignmentQueued)
        {
            _tailAlignmentQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                _tailAlignmentQueued = false;
                AlignBubbleTail();
            });
        }
    }

    private void AlignBubbleTail()
    {
        if (!VisitorBubblePopup.IsOpen || !VisitorBubbleTail.IsVisible
            || PresentationSource.FromVisual(VisitorBubbleTail) is null
            || PresentationSource.FromVisual(VisitorViewport) is null)
        {
            return;
        }

        FrameworkElement bottomCard = VisitorStatusBubble;
        if (!_hasStatusBubble)
        {
            if (VisitorChatItemsControl.ItemContainerGenerator.ContainerFromIndex(_chatBubbleStack.Items.Count - 1)
                    is not FrameworkElement container || VisualTreeHelper.GetChildrenCount(container) == 0
                || VisualTreeHelper.GetChild(container, 0) is not FrameworkElement card)
            {
                return;
            }
            bottomCard = card;
        }

        // 팝업이 화면 경계에서 이동해도 꼬리는 카드에 붙인 채 실제 펫 중심을 향하게 한다.
        var petCenter = VisitorViewport.PointToScreen(new Point(VisitorViewport.ActualWidth / 2, 0));
        var targetX = VisitorBubbleContent.PointFromScreen(petCenter).X;
        var cardLeft = bottomCard.TranslatePoint(new Point(), VisitorBubbleContent).X;
        var tailBounds = VisitorBubbleTail.RenderedGeometry.Bounds;
        if (tailBounds.IsEmpty)
        {
            return;
        }
        var tip = VisitorBubbleTail.TranslatePoint(new Point(tailBounds.Left + tailBounds.Width / 2, 0), VisitorBubbleContent);
        VisitorBubbleTailOffset.X = VisitorBubblePlacementCalculator.CalculateTailOffset(
            targetX, tip.X - VisitorBubbleTailOffset.X, cardLeft, bottomCard.ActualWidth, VisitorBubbleTail.ActualWidth);
    }

    private CustomPopupPlacement[] GetVisitorChatComposerPlacements(Size popupSize, Size targetSize, Point offset)
    {
        if (PresentationSource.FromVisual(VisitorViewport) is null)
        {
            return
            [
                new CustomPopupPlacement(
                    new Point((targetSize.Width - popupSize.Width) * 0.5, -popupSize.Height + ChatComposerBottomOverlap),
                    PopupPrimaryAxis.Horizontal)
            ];
        }

        var dpi = VisualTreeHelper.GetDpi(VisitorViewport);
        var targetTopLeftPhysical = VisitorViewport.PointToScreen(new Point(0, 0));
        var targetBoundsPhysical = new Rect(targetTopLeftPhysical, targetSize);
        var workAreaPhysical = Win32WindowHelper.TryGetNearestWorkArea(this, out var area)
            ? area
            : GetScaledPrimaryWorkArea(dpi);

        var occupied = _occupiedBubbleBoundsProvider?.Invoke(FriendPetId) ?? [];
        var petBoundsPhysical = GetPopupPetBounds(targetBoundsPhysical);
        var chosen = VisitorBubblePlacementCalculator.CalculatePlacement(
            petBoundsPhysical,
            popupSize,
            workAreaPhysical,
            occupied,
            ChatComposerBottomOverlap * dpi.DpiScaleY,
            BubbleCollisionGap * dpi.DpiScaleX,
            allowAboveObstacles: false);

        _currentComposerPhysicalBounds = new Rect(chosen, popupSize);

        var relativeOffset = VisitorBubblePlacementCalculator.ToRelativePopupOffset(chosen, targetBoundsPhysical);
        return [new CustomPopupPlacement(relativeOffset, PopupPrimaryAxis.Horizontal)];
    }

    private CustomPopupPlacement[] GetVisitorBubblePlacements(Size popupSize, Size targetSize, Point offset)
    {
        if (PresentationSource.FromVisual(VisitorViewport) is null)
        {
            return
            [
                new CustomPopupPlacement(
                    new Point((targetSize.Width - popupSize.Width) * 0.5, -popupSize.Height + SpeechBubbleBottomOverlap),
                    PopupPrimaryAxis.Horizontal)
            ];
        }

        var dpi = VisualTreeHelper.GetDpi(VisitorViewport);
        var targetTopLeftPhysical = VisitorViewport.PointToScreen(new Point(0, 0));
        var targetBoundsPhysical = new Rect(targetTopLeftPhysical, targetSize);
        var workAreaPhysical = Win32WindowHelper.TryGetNearestWorkArea(this, out var area)
            ? area
            : GetScaledPrimaryWorkArea(dpi);

        var occupied = _occupiedBubbleBoundsProvider?.Invoke(FriendPetId) ?? [];
        var composerBounds = CurrentComposerPhysicalBounds;
        var petBoundsPhysical = GetPopupPetBounds(targetBoundsPhysical);
        var chosen = VisitorBubblePlacementCalculator.CalculateStackedBubblePlacement(
            petBoundsPhysical,
            composerBounds,
            popupSize,
            workAreaPhysical,
            occupied,
            SpeechBubbleBottomOverlap * dpi.DpiScaleY,
            BubbleCollisionGap * dpi.DpiScaleX);

        _currentBubblePhysicalBounds = new Rect(chosen, popupSize);
        var targetCenterX = targetBoundsPhysical.Left + targetBoundsPhysical.Width / 2;
        VisitorBubbleTail.Visibility = chosen.Y < petBoundsPhysical.Top && chosen.X <= targetCenterX
            && targetCenterX <= chosen.X + popupSize.Width
            && (composerBounds.IsEmpty || chosen.Y + popupSize.Height <= composerBounds.Top + 2.0)
            ? Visibility.Visible
            : Visibility.Collapsed;

        var relativeOffset = VisitorBubblePlacementCalculator.ToRelativePopupOffset(chosen, targetBoundsPhysical);
        return [new CustomPopupPlacement(relativeOffset, PopupPrimaryAxis.Horizontal)];
    }

    private Rect GetPopupPetBounds(Rect viewportPhysicalBounds)
    {
        var bounds = _currentRig?.BodyBounds ?? Rect3D.Empty;
        if (bounds.IsEmpty)
        {
            return viewportPhysicalBounds;
        }

        // 투명한 뷰포트 여백 대신 캐릭터 머리 바로 위를 팝업 기준점으로 사용한다.
        var head = new Point3D(0, bounds.Y + bounds.SizeY, bounds.Z + bounds.SizeZ * 0.65);
        if (!VisitorPetVisual.TransformToAncestor(VisitorViewport).TryTransform(head, out var headInViewport))
        {
            return viewportPhysicalBounds;
        }

        var headY = VisitorViewport.PointToScreen(headInViewport).Y;
        headY = Math.Clamp(headY, viewportPhysicalBounds.Top, viewportPhysicalBounds.Bottom);
        return new Rect(viewportPhysicalBounds.Left, headY, viewportPhysicalBounds.Width, viewportPhysicalBounds.Bottom - headY);
    }

    private static Rect GetScaledPrimaryWorkArea(DpiScale dpi)
    {
        var wa = SystemParameters.WorkArea;
        return new Rect(
            wa.Left * dpi.DpiScaleX,
            wa.Top * dpi.DpiScaleY,
            wa.Width * dpi.DpiScaleX,
            wa.Height * dpi.DpiScaleY);
    }

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

        _beforeChatComposerOpen?.Invoke(this);
        VisitorChatInput.ToolTip = $"{SocialCoordinatorService.FormatDisplayName(_friend.OwnerName, _friend.PetName)}에게 메시지";
        _composerCloseTimer.Stop();
        _isActivatingComposer = true;
        VisitorChatComposerPopup.IsOpen = true;
        RepositionChatComposerPopup();
        RepositionBubblePopup();
        _onBubbleLayoutChanged?.Invoke();

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_isClosingOrClosed || !VisitorChatComposerPopup.IsOpen)
            {
                return;
            }

            QuickMessageInputBehavior.Focus(VisitorChatInput);
            VisitorChatInput.CaretIndex = VisitorChatInput.Text.Length;
            _isActivatingComposer = false;
            RepositionChatComposerPopup();
            RepositionBubblePopup();
            _onBubbleLayoutChanged?.Invoke();
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
        _currentComposerPhysicalBounds = Rect.Empty;
        RepositionBubblePopup();
        _onBubbleLayoutChanged?.Invoke();
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

        var revision = _composerDraftVersion;
        _isSendingComposerMessage = true;
        try
        {
            var sent = await _social.SendBubbleMessageAsync(_friend, text);
            if (_isClosingOrClosed)
            {
                return sent;
            }

            if (!sent)
            {
                ShowVisitorBubble("메시지 전송 실패", 2200);
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
        if (await _social.SendPetInteractionAsync(_friend) == PetInteractionSendResult.Unavailable)
        {
            ShowVisitorBubble("쓰다듬기를 보내지 못했어요", 1800);
        }
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
