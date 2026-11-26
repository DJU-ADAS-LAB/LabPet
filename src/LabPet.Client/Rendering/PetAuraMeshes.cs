using System.Windows;
using System.Windows.Media.Media3D;

namespace LabPet.Client.Rendering;

/// <summary>모든 펫이 공유하는 작은 오라 메시. 생성 후 변경하지 않는다.</summary>
internal static class PetAuraMeshes
{
    internal static readonly MeshGeometry3D Star = CreateStar();
    internal static readonly MeshGeometry3D Petal = CreatePetal();
    internal static readonly MeshGeometry3D Crescent = CreateCrescent();

    private static void Triangle(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c)
    {
        var index = mesh.Positions.Count;
        var normal = Vector3D.CrossProduct(b - a, c - a);
        if (normal.LengthSquared < 1e-16) return;
        normal.Normalize();
        mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c);
        for (var i = 0; i < 3; i++)
        {
            mesh.Normals.Add(normal);
            mesh.TriangleIndices.Add(index + i);
        }
    }

    private static MeshGeometry3D CreateStar()
    {
        var mesh = new MeshGeometry3D();
        for (var i = 0; i < 10; i++)
        {
            Point3D Edge(int n)
            {
                var angle = n * Math.PI / 5 + Math.PI / 2;
                var radius = n % 2 == 0 ? 0.14 : 0.062;
                return new Point3D(Math.Cos(angle) * radius, Math.Sin(angle) * radius, 0);
            }
            Triangle(mesh, new Point3D(0, 0, 0.027), Edge(i), Edge(i + 1));
            Triangle(mesh, new Point3D(0, 0, -0.027), Edge(i + 1), Edge(i));
        }
        mesh.Freeze();
        return mesh;
    }

    private static MeshGeometry3D CreatePetal()
    {
        var mesh = new MeshGeometry3D();
        Point3D Surface(int row, int col)
        {
            var u = row / 12d;
            var v = col / 6d * 2 - 1;
            var width = 0.085 * Math.Pow(Math.Sin(Math.PI * u), 0.7);
            var notch = 0.018 * Math.Exp(-v * v * 24) * Math.Pow(u, 6);
            return new Point3D(width * v, u * 0.23 - 0.115 - notch,
                0.026 * Math.Sin(Math.PI * u) + 0.018 * v * v + 0.012 * v * u);
        }
        for (var row = 0; row < 12; row++)
        for (var col = 0; col < 6; col++)
        {
            var a = Surface(row, col); var b = Surface(row + 1, col);
            var c = Surface(row + 1, col + 1); var d = Surface(row, col + 1);
            Triangle(mesh, a, b, c); Triangle(mesh, a, c, d);
        }
        mesh.Freeze();
        return mesh;
    }

    private static MeshGeometry3D CreateCrescent()
    {
        var mesh = new MeshGeometry3D();
        Point3D Edge(int i, bool inner)
        {
            var angle = -Math.PI / 2 + Math.PI * i / 24;
            return new Point3D((inner ? 0.055 : 0.14) * Math.Cos(angle),
                0.14 * Math.Sin(angle), 0);
        }
        for (var i = 0; i < 24; i++)
        {
            Triangle(mesh, Edge(i, false), Edge(i + 1, false), Edge(i + 1, true));
            Triangle(mesh, Edge(i, false), Edge(i + 1, true), Edge(i, true));
        }
        mesh.Freeze();
        return mesh;
    }
}
