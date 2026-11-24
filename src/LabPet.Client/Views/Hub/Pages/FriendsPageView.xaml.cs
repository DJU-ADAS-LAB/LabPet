using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using LabPet.Client.Navigation;
using LabPet.Client.Rendering;
using LabPet.Client.ViewModels;
using LabPet.Shared.Models;

namespace LabPet.Client.Views.Hub.Pages;

public partial class FriendsPageView : UserControl, IHubPageLifecycle, IDisposable
{
    private readonly SocialViewModel _viewModel;
    private readonly PetWindowViewModel _petVm;
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
    private string _renderedFriendPetId = string.Empty;
    private PetRigHandle? _currentRig;
    private bool _previewUpdateQueued;
    private bool _isActive;
    private bool _disposed;

    internal bool IsPreviewTimerRunning => _previewTimer.IsEnabled;

    public FriendsPageView(SocialViewModel viewModel, PetWindowViewModel petVm, IPetMeshFactory meshFactory)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _petVm = petVm;
        _meshFactory = meshFactory;
        DataContext = _viewModel;

        _viewModel.AttachVisitorStateChecker(_petVm.IsVisitorActive);
        _viewModel.ToggleVisitDesktopRequested += OnToggleVisitDesktopRequested;
        _petVm.VisitorStateChanged += OnVisitorStateChanged;

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
    }

    public void OnActivated(NavigationRequest request)
    {
        if (_disposed)
        {
            return;
        }

        _isActive = true;
        _viewModel.RefreshLists();

        if (!string.IsNullOrWhiteSpace(request.SelectedFriendPetId))
        {
            FriendsTabRadio.IsChecked = true;
            var match = _viewModel.Friends.FirstOrDefault(f =>
                string.Equals(f.Entry.PetId, request.SelectedFriendPetId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                _viewModel.SelectedFriend = match;
            }
        }
        else if (string.Equals(request.SubSection, "Nearby", StringComparison.OrdinalIgnoreCase))
        {
            NearbyTabRadio.IsChecked = true;
        }
        else if (string.Equals(request.SubSection, "Friends", StringComparison.OrdinalIgnoreCase))
        {
            FriendsTabRadio.IsChecked = true;
        }

        SyncSubTabVisibility();
    }

    public void OnDeactivated()
    {
        _isActive = false;
        StopPreviewTimer();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        OnDeactivated();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.ToggleVisitDesktopRequested -= OnToggleVisitDesktopRequested;
        _petVm.VisitorStateChanged -= OnVisitorStateChanged;
        _viewModel.Detach();
        ClearFriendPreviewMesh();
        FriendPreviewLights.Content = null;
    }

    private void OnToggleVisitDesktopRequested(object? sender, (FriendEntry Friend, PetAppearance Appearance) args)
        => _petVm.RequestToggleVisitFriend(args.Friend, args.Appearance);

    private void OnVisitorStateChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            _viewModel.RefreshLists();
        }
    }

    private void SubTabRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        SyncSubTabVisibility();
    }

    private void SyncSubTabVisibility()
    {
        if (FriendsSectionPanel is null || NearbySectionPanel is null)
        {
            return;
        }

        var showFriends = FriendsTabRadio.IsChecked == true;
        FriendsSectionPanel.Visibility = showFriends ? Visibility.Visible : Visibility.Collapsed;
        NearbySectionPanel.Visibility = showFriends ? Visibility.Collapsed : Visibility.Visible;

        if (_isActive && showFriends)
        {
            if (FriendPreviewLights.Content is null)
            {
                FriendPreviewLights.Content = _meshFactory.CreateStudioLights();
            }

            UpdateFriendPreviewMesh();
            StartPreviewTimer();
        }
        else
        {
            StopPreviewTimer();
        }
    }

    private void StartPreviewTimer()
    {
        if (!_previewTimer.IsEnabled)
        {
            _lastTimestamp = _clock.Elapsed.TotalSeconds;
            _previewTimer.Start();
        }
    }

    private void StopPreviewTimer()
    {
        if (_previewTimer.IsEnabled)
        {
            _previewTimer.Stop();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SocialViewModel.SelectedFriend) && !_previewUpdateQueued)
        {
            _previewUpdateQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
            {
                _previewUpdateQueued = false;
                if (_isActive && FriendsTabRadio.IsChecked == true)
                {
                    UpdateFriendPreviewMesh();
                }
            });
        }
    }

    private void UpdateFriendPreviewMesh()
    {
        var selected = _viewModel.SelectedFriend;
        if (selected is null)
        {
            ClearFriendPreviewMesh();
            return;
        }

        var appearance = selected.EffectiveAppearance;
        var sig = $"{selected.Entry.PetId}|{appearance.SpeciesId}|{appearance.BodyColorHex}|{appearance.AccentColorHex}|{appearance.ExpressionId}|{appearance.HeadAccessoryId}|{appearance.AuraEffectId}";
        if (sig == _renderedSignature && _currentRig is not null)
        {
            return;
        }

        _renderedSignature = sig;
        if (_currentRig is not null &&
            string.Equals(_renderedFriendPetId, selected.Entry.PetId, StringComparison.OrdinalIgnoreCase) &&
            _currentRig.TryUpdateAppearance(appearance))
        {
            _previewAnimator.UpdateAccentColor(appearance.AccentColorHex);
            return;
        }

        _renderedFriendPetId = selected.Entry.PetId;
        var rig = _meshFactory.BuildPetRig(appearance, PetRigEffects.None);
        _currentRig = rig;
        FriendPreviewPet.Content = rig.RootModel;
        _previewAnimator.AttachRig(rig, appearance.AccentColorHex);
        _previewAnimator.SetSleeping(selected.IsSleeping);
    }

    private void ClearFriendPreviewMesh()
    {
        FriendPreviewPet.Content = null;
        _renderedSignature = string.Empty;
        _renderedFriendPetId = string.Empty;
        _currentRig = null;
        _previewAnimator.DetachRig();
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

        _previewAnimator.SetSleeping(_viewModel.SelectedFriend.IsSleeping);

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
}
