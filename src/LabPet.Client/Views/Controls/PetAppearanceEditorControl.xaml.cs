using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using LabPet.Client.Rendering;
using LabPet.Client.ViewModels;
using LabPet.Shared.Models;

namespace LabPet.Client.Views.Controls;

public partial class PetAppearanceEditorControl : UserControl
{
    private static readonly TimeSpan ActivePreviewInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan IdlePreviewInterval = TimeSpan.FromMilliseconds(33);

    private readonly PetAnimationController _previewAnimator = new();
    private readonly AxisAngleRotation3D _turntableYaw = new(new Vector3D(0, 1, 0), 0);
    private readonly AxisAngleRotation3D _turntablePitch = new(new Vector3D(1, 0, 0), 8);
    private readonly DispatcherTimer _renderTimer;
    private readonly Stopwatch _frameClock = Stopwatch.StartNew();

    private OnboardingViewModel? _viewModel;
    private IPetMeshFactory? _meshFactory;
    private PetRigHandle? _currentRig;

    private bool _isRotating;
    private Point _lastDragPoint;
    private double _lastTimestamp;
    private double _rhythmBeatTimer;
    private double _typingPreviewTimer;
    private bool _isDisposed;

    internal bool IsPreviewTimerRunning => _renderTimer.IsEnabled;

    public PetAppearanceEditorControl()
    {
        InitializeComponent();

        var turntableGroup = new Transform3DGroup();
        turntableGroup.Children.Add(new RotateTransform3D(_turntableYaw));
        turntableGroup.Children.Add(new RotateTransform3D(_turntablePitch));
        PreviewPetVisual.Transform = turntableGroup;

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = ActivePreviewInterval
        };
        _renderTimer.Tick += OnRenderTick;
    }

    public void Initialize(OnboardingViewModel viewModel, IPetMeshFactory meshFactory, bool startActive)
    {
        if (_viewModel is not null)
        {
            _viewModel.AppearancePreviewChanged -= OnAppearancePreviewChanged;
        }

        _viewModel = viewModel;
        _meshFactory = meshFactory;
        DataContext = _viewModel;

        if (PreviewLightsVisual.Content is null)
        {
            PreviewLightsVisual.Content = _meshFactory.CreateStudioLights();
        }

        _viewModel.AppearancePreviewChanged += OnAppearancePreviewChanged;
        RebuildPreviewMesh(_viewModel.BuildCurrentAppearance());
        SetPreviewActive(startActive);
    }

    public void SetPreviewActive(bool active)
    {
        if (_isDisposed)
        {
            return;
        }

        if (active)
        {
            if (PreviewLightsVisual.Content is null && _meshFactory is not null)
            {
                PreviewLightsVisual.Content = _meshFactory.CreateStudioLights();
            }

            if (_currentRig is null && _viewModel is not null)
            {
                RebuildPreviewMesh(_viewModel.BuildCurrentAppearance());
            }

            if (!_renderTimer.IsEnabled)
            {
                _lastTimestamp = _frameClock.Elapsed.TotalSeconds;
                _renderTimer.Start();
            }
        }
        else if (_renderTimer.IsEnabled)
        {
            _renderTimer.Stop();
        }
    }

    public void DisposePreview()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _renderTimer.Stop();
        if (_viewModel is not null)
        {
            _viewModel.AppearancePreviewChanged -= OnAppearancePreviewChanged;
            _viewModel = null;
        }

        _previewAnimator.DetachRig();
        PreviewPetVisual.Content = null;
        PreviewLightsVisual.Content = null;
        _currentRig = null;
    }

    private void OnAppearancePreviewChanged(object? sender, PetAppearance appearance)
        => RebuildPreviewMesh(appearance);

    private void RebuildPreviewMesh(PetAppearance appearance)
    {
        if (_meshFactory is null || _isDisposed)
        {
            return;
        }

        if (_currentRig is not null && _currentRig.TryUpdateAppearance(appearance))
        {
            _previewAnimator.UpdateAccentColor(appearance.AccentColorHex);
            return;
        }

        var rig = _meshFactory.BuildPetRig(appearance);
        _currentRig = rig;
        PreviewPetVisual.Content = rig.RootModel;
        _previewAnimator.AttachRig(rig, appearance.AccentColorHex);
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var isActive = _isRotating || _viewModel.AutoRotatePreview || _viewModel.IsRhythmPreviewEnabled
                       || _viewModel.IsTypingPreviewEnabled || _previewAnimator.HasInteractiveMotion;
        var desiredInterval = isActive ? ActivePreviewInterval : IdlePreviewInterval;
        if (_renderTimer.Interval != desiredInterval)
        {
            _renderTimer.Interval = desiredInterval;
        }

        var now = _frameClock.Elapsed.TotalSeconds;
        var dt = now - _lastTimestamp;
        _lastTimestamp = now;

        if (!_isRotating && _viewModel.AutoRotatePreview)
        {
            _turntableYaw.Angle = (_turntableYaw.Angle + dt * 28.0) % 360.0;
        }

        if (_viewModel.IsRhythmPreviewEnabled)
        {
            _rhythmBeatTimer += dt;
            var isBeat = false;
            if (_rhythmBeatTimer >= 0.5)
            {
                _rhythmBeatTimer -= 0.5;
                isBeat = true;
            }

            var phase = _rhythmBeatTimer / 0.5;
            var bass = (float)Math.Clamp(0.28 + Math.Exp(-phase * 4.5) * 0.62, 0.0, 1.0);
            var mid = (float)Math.Clamp(0.25 + Math.Abs(Math.Sin(now * 6.0)) * 0.45, 0.0, 1.0);
            var treble = (float)Math.Clamp(0.20 + Math.Abs(Math.Cos(now * 9.0)) * 0.45, 0.0, 1.0);
            _previewAnimator.UpdateAudioSnapshot(bass, mid, treble, isBeat);
        }
        else
        {
            _rhythmBeatTimer = 0;
            _previewAnimator.UpdateAudioSnapshot(0f, 0f, 0f, isBeat: false);
        }

        if (_viewModel.IsTypingPreviewEnabled)
        {
            _typingPreviewTimer += dt;
            if (_typingPreviewTimer >= 0.13)
            {
                _typingPreviewTimer -= 0.13;
                _previewAnimator.TriggerTypingPawTap();
            }
        }
        else
        {
            _typingPreviewTimer = 0;
        }

        _previewAnimator.UpdateFrame(dt, targetLookX: 0, targetLookY: 0);
    }

    private void PreviewPetMotion_Click(object sender, RoutedEventArgs e)
        => _previewAnimator.TriggerPetReaction();

    private void PreviewDizzyMotion_Click(object sender, RoutedEventArgs e)
        => _previewAnimator.TriggerDizzyReaction();

    private void StageThemeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (PreviewStageBorder is null)
        {
            return;
        }

        if (LightStageToggle.IsChecked == true)
        {
            PreviewStageBorder.Background = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.38),
                Center = new Point(0.5, 0.48),
                RadiusX = 0.72,
                RadiusY = 0.72,
                GradientStops =
                [
                    new GradientStop(Color.FromRgb(220, 226, 240), 0.0),
                    new GradientStop(Color.FromRgb(196, 205, 224), 1.0)
                ]
            };
            PreviewStageBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(180, 190, 212));
        }
        else
        {
            PreviewStageBorder.Background = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.38),
                Center = new Point(0.5, 0.48),
                RadiusX = 0.72,
                RadiusY = 0.72,
                GradientStops =
                [
                    new GradientStop(Color.FromRgb(52, 61, 90), 0.0),
                    new GradientStop(Color.FromRgb(34, 40, 60), 0.62),
                    new GradientStop(Color.FromRgb(23, 27, 42), 1.0)
                ]
            };
            PreviewStageBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(54, 62, 89));
        }
    }

    private void PreviewSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isRotating = true;
        _lastDragPoint = e.GetPosition(this);
        ((UIElement)sender).CaptureMouse();
    }

    private void PreviewSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isRotating)
        {
            return;
        }

        var current = e.GetPosition(this);
        var dx = current.X - _lastDragPoint.X;
        var dy = current.Y - _lastDragPoint.Y;
        _lastDragPoint = current;

        _turntableYaw.Angle = (_turntableYaw.Angle + dx * 0.65) % 360.0;
        _turntablePitch.Angle = Math.Clamp(_turntablePitch.Angle + dy * 0.45, -35.0, 45.0);
    }

    private void PreviewSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isRotating)
        {
            return;
        }

        _isRotating = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }
}
