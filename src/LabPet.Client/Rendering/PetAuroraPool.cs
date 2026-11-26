using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LabPet.Client.Rendering;

/// <summary>고정 곡면 띠와 공유 발광 텍스처로 구성하는 오로라 풀.</summary>
internal static class PetAuroraPool
{
    private static readonly MeshGeometry3D Ribbon = CreateRibbon();

    private static MeshGeometry3D CreateRibbon()
    {
        var mesh = new MeshGeometry3D();
        const int segments = 24;
        for (var i = 0; i <= segments; i++)
        {
            var u = i / (double)segments;
            var x = (u - 0.5) * 0.65;
            var wave = 0.075 * Math.Sin(u * Math.PI * 2);
            var depth = -0.045 * Math.Cos(u * Math.PI * 2);
            for (var edge = 0; edge < 2; edge++)
            {
                mesh.Positions.Add(new Point3D(x, wave + (edge - 0.5) * 0.48, depth));
                mesh.TextureCoordinates.Add(new Point(u, edge));
                mesh.Normals.Add(new Vector3D(0, 0, 1));
            }
            if (i == segments) continue;
            var n = i * 2;
            mesh.TriangleIndices.Add(n); mesh.TriangleIndices.Add(n + 2); mesh.TriangleIndices.Add(n + 1);
            mesh.TriangleIndices.Add(n + 1); mesh.TriangleIndices.Add(n + 2); mesh.TriangleIndices.Add(n + 3);
        }
        mesh.Freeze();
        return mesh;
    }

    internal static (int Triangles, MusicParticleHandle[] Particles) Build(Model3DGroup parent)
    {
        var particles = new MusicParticleHandle[8];
        for (var i = 0; i < particles.Length; i++)
        {
            var color = (i % 3) switch
            {
                0 => Color.FromRgb(125, 239, 205),
                1 => Color.FromRgb(137, 199, 255),
                _ => Color.FromRgb(198, 161, 245)
            };
            var brush = PetAuraGlow.CreateBrush(color);
            var material = new EmissiveMaterial(brush);
            var scale = new ScaleTransform3D(0, 0, 0);
            var rotation = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
            var translation = new TranslateTransform3D();
            var transform = new Transform3DGroup();
            transform.Children.Add(scale);
            transform.Children.Add(new RotateTransform3D(rotation));
            transform.Children.Add(translation);
            parent.Children.Add(new GeometryModel3D(Ribbon, material)
            {
                BackMaterial = material, Transform = transform
            });
            particles[i] = new MusicParticleHandle
            {
                Scale = scale, Rotation = rotation, Translate = translation,
                SoftGlowBrush = brush, BaseColor = color, ParticleKind = "aurora"
            };
        }
        return (Ribbon.TriangleIndices.Count / 3 * particles.Length, particles);
    }
}
