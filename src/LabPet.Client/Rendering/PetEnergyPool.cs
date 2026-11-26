using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LabPet.Client.Rendering;

/// <summary>고정 원호 메시와 공유 발광 텍스처를 사용하는 에너지 파동 풀.</summary>
internal static class PetEnergyPool
{
    private static readonly MeshGeometry3D Arc = CreateArc();

    private static MeshGeometry3D CreateArc()
    {
        var mesh = new MeshGeometry3D();
        const int segments = 16;
        for (var i = 0; i <= segments; i++)
        {
            var u = i / (double)segments;
            var angle = (u - 0.5) * Math.PI * 0.60;
            for (var edge = 0; edge < 2; edge++)
            {
                var radius = 1.04 + edge * 0.10;
                mesh.Positions.Add(new Point3D(radius * Math.Cos(angle),
                    0.025 * Math.Sin(angle * 5), radius * Math.Sin(angle)));
                mesh.TextureCoordinates.Add(new Point(u, edge));
                mesh.Normals.Add(new Vector3D(0, 1, 0));
            }
            if (i == segments) continue;
            var n = i * 2;
            mesh.TriangleIndices.Add(n); mesh.TriangleIndices.Add(n + 1); mesh.TriangleIndices.Add(n + 2);
            mesh.TriangleIndices.Add(n + 1); mesh.TriangleIndices.Add(n + 3); mesh.TriangleIndices.Add(n + 2);
        }
        mesh.Freeze();
        return mesh;
    }

    internal static (int Triangles, MusicParticleHandle[] Particles) Build(Model3DGroup parent)
    {
        var firstChild = parent.Children.Count;
        var pool = PetAuraParticlePool.BuildSoftGlow(parent, "electric", Color.FromRgb(139, 204, 255));
        for (var i = 0; i < pool.Particles.Length; i++)
        {
            var model = (GeometryModel3D)parent.Children[firstChild + i];
            model.Geometry = Arc;
            pool.Particles[i].Rotation.Axis = new Vector3D(0, 1, 0);
        }
        return (Arc.TriangleIndices.Count / 3 * pool.Particles.Length, pool.Particles);
    }
}
