using System.Windows.Media.Media3D;

namespace LabPet.Client.Rendering;

/// <summary>머리 소켓 로컬 좌표의 장착 보정. 회전 단위는 도(degree).</summary>
public sealed class PetAccessoryPlacement
{
    public PetVector3 Offset { get; set; } = new(0, 0, 0);
    public PetVector3 Rotation { get; set; } = new(0, 0, 0);
    public PetVector3 Scale { get; set; } = new(1, 1, 1);

    internal Transform3D CreateTransform()
    {
        static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
        static double Size(double value) => double.IsFinite(value) && value > 0 ? Math.Clamp(value, 0.1, 4) : 1;
        var scale = Scale ?? new PetVector3(1, 1, 1);
        var rotation = Rotation ?? new PetVector3(0, 0, 0);
        var offset = Offset ?? new PetVector3(0, 0, 0);
        var transform = new Transform3DGroup();
        transform.Children.Add(new ScaleTransform3D(Size(scale.X), Size(scale.Y), Size(scale.Z)));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), Finite(rotation.X, 0) % 360)));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), Finite(rotation.Y, 0) % 360)));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), Finite(rotation.Z, 0) % 360)));
        transform.Children.Add(new TranslateTransform3D(Finite(offset.X, 0), Finite(offset.Y, 0), Finite(offset.Z, 0)));
        transform.Freeze();
        return transform;
    }
}
