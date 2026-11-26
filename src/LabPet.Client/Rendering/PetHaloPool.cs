using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LabPet.Client.Rendering;

/// <summary>두 겹의 링을 구성하는 고정 원호 8개. 메시와 발광 텍스처는 공유한다.</summary>
internal static class PetHaloPool
{
    private static readonly MeshGeometry3D Arc = CreateArc();

    private static MeshGeometry3D CreateArc()
    {
        var mesh = new MeshGeometry3D();
        const int segments = 20;
        for (var i = 0; i <= segments; i++)
        {
            var u = i / (double)segments;
            var angle = u * Math.PI / 2;
            for (var edge = 0; edge < 2; edge++)
            {
                var radius = 1.0 + edge * 0.055;
                mesh.Positions.Add(new Point3D(radius * Math.Cos(angle), 0, radius * Math.Sin(angle)));
                mesh.Normals.Add(new Vector3D(0, 1, 0));
                mesh.TextureCoordinates.Add(new Point(u, edge));
            }
            if (i == segments) continue;
            var n = i * 2;
            mesh.TriangleIndices.Add(n); mesh.TriangleIndices.Add(n + 2); mesh.TriangleIndices.Add(n + 1);
            mesh.TriangleIndices.Add(n + 1); mesh.TriangleIndices.Add(n + 2); mesh.TriangleIndices.Add(n + 3);
        }
        mesh.Freeze();
        return mesh;
    }

    internal static (int Triangles, MusicParticleHandle[] Particles) Build(Model3DGroup parent, Color color)
    {
        var first = parent.Children.Count;
        var pool = PetAuraParticlePool.BuildSoftGlow(parent, "halo_ring", color);
        for (var i = 0; i < pool.Particles.Length; i++)
        {
            var model = (GeometryModel3D)parent.Children[first + i];
            model.Geometry = Arc;
            pool.Particles[i].Rotation.Axis = new Vector3D(0, 1, 0);
        }
        return (Arc.TriangleIndices.Count / 3 * pool.Particles.Length, pool.Particles);
    }
}
