using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using LabPet.Client.Rendering;
using LabPet.Client.ViewModels;
using LabPet.Shared.Models;

namespace LabPet.Client.Views;

public partial class SocialWindow : Window
{
    private readonly SocialViewModel _viewModel;
    private readonly IPetMeshFactory _meshFactory;
    private readonly PetAnimationController _previewAnimator = new();

    private readonly AxisAngleRotation3D _turntableYaw = new(new Vector3D(0, 1, 0), 0);
    private readonly AxisAngleRotation3D _turntablePitch = new(new Vector3D(1, 0, 0), 6);
    private readonly DispatcherTimer _previewTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _lastTimestamp;
    private bool _isRotating;
    private Point _lastDragPoint;
    private string _renderedSignature = string.Empty;

    public SocialWindow(SocialViewModel viewModel, IPetMeshFactory meshFactory)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _meshFactory = meshFactory;
        DataContext = _viewModel;

        FriendPreviewLights.Content = _meshFactory.CreateStudioLights();
        var transformGroup = new Transform3DGroup();
        transformGroup.Children.Add(new RotateTransform3D(_turntableYaw));
        transformGroup.Children.Add(new RotateTransform3D(_turntablePitch));
        FriendPreviewPet.Transform = transformGroup;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _previewTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(20)
        };
        _previewTimer.Tick += OnPreviewTick;
        _previewTimer.Start();

        Closed += (_, _) =>
        {
            _previewTimer.Stop();
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Detach();
        };

        UpdateFriendPreviewMesh();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SocialViewModel.SelectedFriend))
        {
            UpdateFriendPreviewMesh();
        }
    }

    private void UpdateFriendPreviewMesh()
    {
        var selected = _viewModel.SelectedFriend;
        if (selected is null)
        {
            FriendPreviewPet.Content = null;
            _renderedSignature = string.Empty;
            return;
        }

        var appearance = selected.EffectiveAppearance;
        var sig = $"{selected.Entry.PetId}|{appearance.SpeciesId}|{appearance.BodyColorHex}|{appearance.AccentColorHex}|{appearance.ExpressionId}|{appearance.HeadAccessoryId}|{appearance.AuraEffectId}";
        if (sig == _renderedSignature)
        {
            return;
        }

        _renderedSignature = sig;
        var rig = _meshFactory.BuildPetRig(appearance);
        FriendPreviewPet.Content = rig.RootModel;
        _previewAnimator.AttachRig(rig, appearance.AccentColorHex);
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _lastTimestamp;
        _lastTimestamp = now;

        if (_viewModel.SelectedFriend is null)
        {
            return;
        }

        if (!_isRotating)
        {
            _turntableYaw.Angle = (_turntableYaw.Angle + dt * 22.0) % 360.0;
        }

        _previewAnimator.UpdateFrame(dt, targetLookX: 0, targetLookY: 0);
    }

    private void FriendPreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedFriend is null)
        {
            return;
        }

        _isRotating = true;
        _lastDragPoint = e.GetPosition(this);
        ((UIElement)sender).CaptureMouse();
    }

    private void FriendPreview_MouseMove(object sender, MouseEventArgs e)
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
        _turntablePitch.Angle = Math.Clamp(_turntablePitch.Angle + dy * 0.45, -30.0, 40.0);
    }

    private void FriendPreview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isRotating)
        {
            return;
        }

        _isRotating = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
