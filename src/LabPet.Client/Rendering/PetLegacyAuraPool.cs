using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Geometry;

namespace LabPet.Client.Rendering;

/// <summary>외부 에셋 누락 시 사용하는 공유 음표·육각 눈결정 메시.</summary>
internal static class PetLegacyAuraPool
{
    private static readonly System.Windows.Media.Media3D.MeshGeometry3D Note = Create(false);
    private static readonly System.Windows.Media.Media3D.MeshGeometry3D Snow = Create(true);

    private static System.Windows.Media.Media3D.MeshGeometry3D Create(bool snow)
    {
        var mesh = new MeshBuilder(true, false);
        if (snow)
        {
            for (var arm = 0; arm < 6; arm++)
            {
                var angle = arm * MathF.PI / 3;
                var axis = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0);
                var tangent = new Vector3(-axis.Y, axis.X, 0);
                // 작은 결정의 윤곽은 유지하고 가는 가지의 단면만 삼각형으로 단순화한다.
                mesh.AddCylinder(Vector3.Zero, axis * 0.16f, 0.009f, 3);
                // 폴백은 팔마다 한 쌍의 곁가지를 사용한다. 상세형은 Blender 에셋으로 제공한다.
                for (var side = -1; side <= 1; side += 2)
                {
                    var origin = axis * 0.085f;
                    mesh.AddCylinder(origin, origin + axis * 0.034f + tangent * (side * 0.027f), 0.006f, 3);
                }
            }
        }
        else
        {
            mesh.AddEllipsoid(new Vector3(-0.03f, -0.05f, 0), 0.088f, 0.068f, 0.048f, 12, 8);
            mesh.AddCylinder(new Vector3(0.042f, -0.04f, 0), new Vector3(0.042f, 0.18f, 0), 0.019f, 8);
            mesh.AddTube(new[] { new Vector3(0.042f, 0.175f, 0), new Vector3(0.105f, 0.155f, 0.01f), new Vector3(0.148f, 0.105f, 0.015f) }, 0.030f, 8, false);
        }
        return new ProceduralPetMeshFactory().GetSharedMesh(mesh.ToMesh());
    }

    internal static (int Triangles, MusicParticleHandle[] Particles) Build(Model3DGroup parent, string id, Color accent)
        => PetAuraParticlePool.Build(parent, id == "snowflakes" ? Snow : Note,
            id == "snowflakes" ? Color.FromRgb(224, 244, 255) : ProceduralPetMeshFactory.Lighten(accent, 0.35), id);
}
