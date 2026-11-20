using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using LabPet.Client.Rendering;
using LabPet.Client.ViewModels;
using LabPet.Shared.Models;

namespace LabPet.Client.Views;

public partial class OnboardingWindow : Window
{
    private readonly OnboardingViewModel _viewModel;
    private readonly IPetMeshFactory _meshFactory;
    private readonly PetAnimationController _previewAnimator = new();

    private readonly AxisAngleRotation3D _turntableYaw = new(new Vector3D(0, 1, 0), 0);
    private readonly AxisAngleRotation3D _turntablePitch = new(new Vector3D(1, 0, 0), 8);
    private readonly DispatcherTimer _renderTimer;
    private readonly Stopwatch _frameClock = Stopwatch.StartNew();

    private bool _isRotating;
    private Point _lastDragPoint;
    private double _lastTimestamp;
    private double _rhythmBeatTimer;

    public PetProfile? SavedProfile { get; private set; }

    public OnboardingWindow(OnboardingViewModel viewModel, IPetMeshFactory meshFactory)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _meshFactory = meshFactory;
        DataContext = _viewModel;

        PreviewLightsVisual.Content = _meshFactory.CreateStudioLights();

        var turntableGroup = new Transform3DGroup();
        turntableGroup.Children.Add(new RotateTransform3D(_turntableYaw));
        turntableGroup.Children.Add(new RotateTransform3D(_turntablePitch));
        PreviewPetVisual.Transform = turntableGroup;

        _viewModel.AppearancePreviewChanged += OnAppearancePreviewChanged;
        _viewModel.ProfileSaved += OnProfileSaved;

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();

        Closed += (_, _) =>
        {
            _renderTimer.Stop();
            _viewModel.AppearancePreviewChanged -= OnAppearancePreviewChanged;
            _viewModel.ProfileSaved -= OnProfileSaved;
        };

        RebuildPreviewMesh(_viewModel.BuildCurrentAppearance());
    }

    private void OnAppearancePreviewChanged(object? sender, PetAppearance appearance)
    {
        RebuildPreviewMesh(appearance);
    }

    private void OnProfileSaved(object? sender, PetProfile profile)
    {
        SavedProfile = profile;
        DialogResult = true;
        Close();
    }

    private void RebuildPreviewMesh(PetAppearance appearance)
    {
        var rig = _meshFactory.BuildPetRig(appearance);
        PreviewPetVisual.Content = rig.RootModel;
        _previewAnimator.AttachRig(rig, appearance.AccentColorHex);
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        var now = _frameClock.Elapsed.TotalSeconds;
        var dt = now - _lastTimestamp;
        _lastTimestamp = now;

        if (!_isRotating && _viewModel.AutoRotatePreview)
        {
            _turntableYaw.Angle = (_turntableYaw.Angle + dt * 28.0) % 360.0;
        }

        // 리듬 댄스 미리보기 토글 시 120 BPM 박자 펄스 주입
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

        _previewAnimator.UpdateFrame(dt, targetLookX: 0, targetLookY: 0);
    }

    private void PreviewPetMotion_Click(object sender, RoutedEventArgs e)
        => _previewAnimator.TriggerPetReaction();

    private void PreviewDizzyMotion_Click(object sender, RoutedEventArgs e)
        => _previewAnimator.TriggerDizzyReaction();

    private void PreviewPawTapMotion_Click(object sender, RoutedEventArgs e)
        => _previewAnimator.TriggerTypingPawTap();

    private void StageThemeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (PreviewStageBorder is null)
        {
            return;
        }

        var isLight = LightStageToggle.IsChecked == true;
        if (isLight)
        {
            // 밝은 무대 모드에서도 캐릭터 외곽선이 구분되도록 차분한 라벤더-블루 그레이 톤 사용
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

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

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
