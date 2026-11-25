using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using LabPet.Client.Views;
using LabPet.Shared.Models;

namespace LabPet.Client.Rendering;

/// <summary>임시 펫의 독립 리그와 뷰포트. 창·타이머 없이 소유자의 공용 루프에서 갱신한다.</summary>
internal sealed class SnowballPetVisual : Grid, IDisposable
{
    private readonly PetRigHandle _rig;
    private readonly ModelVisual3D _model;
    private readonly Viewport3D _viewport;
    private bool _disposed;
    internal PetAnimationController Animator { get; } = new();

    internal SnowballPetVisual(IPetMeshFactory factory, PetAppearance appearance, double scale)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(appearance);
        Dispatcher.VerifyAccess();
        IsHitTestVisible = false;
        Focusable = false;
        var size = PetDisplayLayoutHelper.ComputeViewportSize(scale);
        Width = size.Width;
        Height = size.Height;
        _rig = factory.BuildPetRig(appearance, PetRigEffects.None);
        var bounds = _rig.RootModel.Bounds;
        var extent = Math.Max(bounds.SizeX, bounds.SizeY + bounds.SizeZ * 0.08);
        if (bounds.IsEmpty || !double.IsFinite(extent) || extent <= 0)
            throw new InvalidOperationException("Invalid snowball pet model bounds.");
        var center = new Point3D(bounds.X + bounds.SizeX / 2,
            bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        _viewport = new Viewport3D
        {
            IsHitTestVisible = false,
            Camera = new OrthographicCamera(center + new Vector3D(0, 0.4, 5),
                new Vector3D(0, -0.4, -5), new Vector3D(0, 1, 0),
                extent * 1.18 * Math.Max(1, Width / Height))
        };
        _model = new ModelVisual3D { Content = _rig.RootModel };
        _viewport.Children.Add(new ModelVisual3D { Content = factory.CreateStudioLights() });
        _viewport.Children.Add(_model);
        Children.Add(_viewport);
        Animator.AttachRig(_rig, appearance.AccentColorHex);
    }

    /// <summary>관절 갱신 후 손 위치를 이 시각 요소의 로컬 DIP로 반환한다.</summary>
    internal bool TryGetHandDip(out Point point)
    {
        Dispatcher.VerifyAccess();
        point = default;
        if (_disposed || !_rig.TryGetRightHandPosition(out var hand)) return false;
        return PetDisplayLayoutHelper.TryProjectPointToViewport(_viewport, _model, hand, out point);
    }

    /// <summary>모니터 물리 픽셀 경계를 오버레이 로컬 DIP로 변환한다. 기존 방문 펫에는 사용하지 않는다.</summary>
    internal bool TryPlace(SnowballOverlayWindow overlay, Rect boundsPhysical)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(overlay);
        if (_disposed || !overlay.Dispatcher.CheckAccess() || !ReferenceEquals(Parent, overlay.Scene)
            || boundsPhysical.IsEmpty || boundsPhysical.Width <= 0 || boundsPhysical.Height <= 0
            || !overlay.TryScreenPhysicalToSceneDip(boundsPhysical.TopLeft, out var topLeft)
            || !overlay.TryScreenPhysicalToSceneDip(boundsPhysical.BottomRight, out var bottomRight)) return false;
        var width = bottomRight.X - topLeft.X;
        var height = bottomRight.Y - topLeft.Y;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0
            || width > 10000 || height > 10000) return false;
        Width = width;
        Height = height;
        Canvas.SetLeft(this, topLeft.X);
        Canvas.SetTop(this, topLeft.Y);
        return true;
    }

    internal bool TryGetHandPhysical(out Point point)
    {
        Dispatcher.VerifyAccess();
        point = default;
        if (_disposed || PresentationSource.FromVisual(this) is null
            || !TryGetHandDip(out var local)) return false;
        var physical = _viewport.PointToScreen(local);
        if (!double.IsFinite(physical.X) || !double.IsFinite(physical.Y)) return false;
        point = physical;
        return true;
    }

    internal bool TryGetVisibleBodyPhysical(Rect monitor, out Point point)
    {
        point = default;
        var body = _rig.BodyBounds;
        if (_disposed || body.IsEmpty || PresentationSource.FromVisual(_viewport) is null) return false;
        // 투영된 몸통 사각형과 실제 클리핑 영역이 겹치는 부분을 조준한다.
        var projected = Rect.Empty;
        for (var corner = 0; corner < 8; corner++)
        {
            var model = new Point3D(
                body.X + ((corner & 1) == 0 ? 0 : body.SizeX),
                body.Y + ((corner & 2) == 0 ? 0 : body.SizeY),
                body.Z + ((corner & 4) == 0 ? 0 : body.SizeZ));
            model = _rig.BodyTransform?.Transform(model) ?? model;
            if (!PetDisplayLayoutHelper.TryProjectPointToViewport(_viewport, _model, model, out var local)) return false;
            projected.Union(_viewport.PointToScreen(local));
        }
        var visible = Rect.Intersect(projected, monitor);
        if (visible.IsEmpty || visible.Width < 7 || visible.Height < 7
            || visible.Width * visible.Height < projected.Width * projected.Height * 0.18) return false;
        point = visible.TopLeft + new Vector(visible.Width / 2, visible.Height / 2);
        return true;
    }

    private Canvas? _heldLayer;
    private System.Windows.Shapes.Ellipse? _heldSnowball;
    internal Point? HeldSnowballPosition { get; private set; }

    /// <summary>관절 투영 좌표를 따라가는 경량 눈덩이. 창·타이머 없이 공용 루프에서 갱신한다.</summary>
    internal void SetSnowballHeld(bool held)
    {
        Dispatcher.VerifyAccess();
        HeldSnowballPosition = null;
        if (_disposed || !held || !TryGetHandDip(out var hand))
        {
            if (_heldSnowball is not null) _heldSnowball.Visibility = Visibility.Collapsed;
            return;
        }
        if (_heldSnowball is null)
        {
            _heldLayer = new Canvas { IsHitTestVisible = false, Focusable = false };
            _heldSnowball = new System.Windows.Shapes.Ellipse
            {
                IsHitTestVisible = false,
                Focusable = false,
                Fill = new System.Windows.Media.RadialGradientBrush(
                    System.Windows.Media.Colors.White, System.Windows.Media.Color.FromRgb(192, 219, 238)),
                Stroke = System.Windows.Media.Brushes.White,
                StrokeThickness = 0.6
            };
            _heldLayer.Children.Add(_heldSnowball);
            Children.Add(_heldLayer);
        }
        var diameter = Math.Clamp(Width / 214 * 14, 4, 40);
        _heldSnowball.Width = _heldSnowball.Height = diameter;
        Canvas.SetLeft(_heldSnowball, hand.X - diameter / 2);
        Canvas.SetTop(_heldSnowball, hand.Y - diameter / 2);
        _heldSnowball.Visibility = Visibility.Visible;
        HeldSnowballPosition = hand;
    }

    internal bool Advance(double seconds, double lookX, double lookY)
    {
        Dispatcher.VerifyAccess();
        if (_disposed || !double.IsFinite(seconds) || seconds <= 0
            || !double.IsFinite(lookX) || !double.IsFinite(lookY)) return false;
        Animator.UpdateFrame(seconds, lookX, lookY);
        return Animator.SnowballReleasedThisFrame;
    }

    public void Dispose()
    {
        Dispatcher.VerifyAccess();
        if (_disposed) return;
        SetSnowballHeld(false);
        _heldLayer?.Children.Clear();
        _heldSnowball = null;
        _heldLayer = null;
        _disposed = true;
        Animator.CancelSnowballInteraction();
        Animator.DetachRig();
        _model.Content = null;
        _viewport.Children.Clear();
        Children.Clear();
    }
}
