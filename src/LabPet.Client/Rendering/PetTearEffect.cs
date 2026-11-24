using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LabPet.Client.Rendering;

internal sealed class PetTearEffect
{
    private readonly Point3D _eyeCenter;
    private readonly ScaleTransform3D[] _scales = [new(0, 0, 0), new(0, 0, 0)];
    private readonly TranslateTransform3D[] _positions = [new(), new()];

    public PetTearEffect(Model3DGroup head, Point3D eyeCenter, MeshGeometry3D mesh)
    {
        _eyeCenter = eyeCenter;
        var material = ProceduralPetMeshFactory.CreateClayMaterial(Color.FromRgb(114, 196, 255), null, 25);
        material.Freeze();
        for (var i = 0; i < _scales.Length; i++)
        {
            var transform = new Transform3DGroup();
            transform.Children.Add(_scales[i]);
            transform.Children.Add(_positions[i]);
            ProceduralPetMeshFactory.AddGeometry(head, mesh, material, transform);
        }
    }

    public bool IsVisible => _scales[0].ScaleX > 0 || _scales[1].ScaleX > 0;

    public void Update(double elapsed, double weight)
    {
        for (var i = 0; i < _scales.Length; i++)
        {
            var fall = (elapsed * 2.2 + i * 0.48) % 1.0;
            var size = Math.Sin(fall * Math.PI) * weight;
            _scales[i].ScaleX = _scales[i].ScaleY = _scales[i].ScaleZ = size;
            _positions[i].OffsetX = _eyeCenter.X + (i == 0 ? -1 : 1) * (0.28 + fall * 0.08);
            _positions[i].OffsetY = _eyeCenter.Y - 0.06 - fall * 0.39;
            _positions[i].OffsetZ = _eyeCenter.Z + 0.16;
        }
    }
}
