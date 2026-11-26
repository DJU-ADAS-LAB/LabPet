using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace LabPet.Client.Rendering;

/// <summary>로컬에서 한 번 생성한 발광 텍스처와 사각형 메시를 공유한다.</summary>
internal static class PetAuraGlow
{
    private static readonly Dictionary<Color, BitmapSource> Textures = new();
    internal static readonly MeshGeometry3D Quad = CreateQuad();

    private static MeshGeometry3D CreateQuad()
    {
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection
            {
                new(-0.22, -0.22, 0), new(0.22, -0.22, 0),
                new(0.22, 0.22, 0), new(-0.22, 0.22, 0)
            },
            TextureCoordinates = new PointCollection
            {
                new(0, 1), new(1, 1), new(1, 0), new(0, 0)
            },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
            Normals = new Vector3DCollection
            {
                new(0, 0, 1), new(0, 0, 1), new(0, 0, 1), new(0, 0, 1)
            }
        };
        mesh.Freeze();
        return mesh;
    }

    internal static ImageBrush CreateBrush(Color color)
    {
        BitmapSource texture;
        lock (Textures)
        {
            if (!Textures.TryGetValue(color, out texture!))
            {
                const int size = 64;
                var pixels = new byte[size * size * 4];
                for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var dx = (x + 0.5 - size / 2d) / (size / 2d);
                    var dy = (y + 0.5 - size / 2d) / (size / 2d);
                    var radius = Math.Sqrt(dx * dx + dy * dy);
                    var edge = Math.Clamp(1 - radius, 0, 1);
                    var alpha = (byte)(210 * edge * edge * edge);
                    var offset = (y * size + x) * 4;
                    pixels[offset] = (byte)(color.B * alpha / 255);
                    pixels[offset + 1] = (byte)(color.G * alpha / 255);
                    pixels[offset + 2] = (byte)(color.R * alpha / 255);
                    pixels[offset + 3] = alpha;
                }
                texture = BitmapSource.Create(size, size, 96, 96,
                    PixelFormats.Pbgra32, null, pixels, size * 4);
                texture.Freeze();
                Textures.Add(color, texture);
            }
        }
        // 투명도만 입자별로 변경하므로 브러시는 공유하거나 Freeze하지 않는다.
        return new ImageBrush(texture) { Opacity = 0, Stretch = Stretch.Fill };
    }
}
