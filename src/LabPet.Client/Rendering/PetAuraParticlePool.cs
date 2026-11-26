using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LabPet.Client.Rendering;

/// <summary>오라 생성 시 한 번 할당하는 고정 크기 풀. 정적 메시를 모든 입자가 공유한다.</summary>
internal static class PetAuraParticlePool
{
    internal static (int Triangles, MusicParticleHandle[] Particles) BuildTheme(
        Model3DGroup parent, string auraId, Color accent)
    {
        var mesh = auraId switch
        {
            "starlight" => PetAuraMeshes.Star,
            "cherry_blossom" => PetAuraMeshes.Petal,
            "milky_way" => PetAuraMeshes.Star,
            _ => null
        };
        if (mesh is null) return (0, []);
        var color = auraId switch
        {
            "starlight" => Color.FromRgb(255, 232, 171),
            "cherry_blossom" => Color.FromRgb(255, 183, 205),
            _ => Color.FromRgb(202, 192, 255)
        };
        var firstChild = parent.Children.Count;
        var pool = Build(parent, mesh, color, auraId);
        var triangles = pool.Triangles;
        if (auraId == "milky_way")
        {
            // 풀 생성 시에만 별 두 개를 공유 초승달 메시로 교체한다.
            for (var i = 0; i < pool.Particles.Length; i += 4)
            {
                var model = (GeometryModel3D)parent.Children[firstChild + i];
                model.Geometry = PetAuraMeshes.Crescent;
                triangles += (PetAuraMeshes.Crescent.TriangleIndices.Count
                    - mesh.TriangleIndices.Count) / 3;
            }
        }
        return (triangles, pool.Particles);
    }

    internal static (int Triangles, MusicParticleHandle[] Particles) BuildSoftGlow(
        Model3DGroup parent, string kind, Color color)
    {
        var particles = new MusicParticleHandle[8];
        for (var i = 0; i < particles.Length; i++)
        {
            var brush = PetAuraGlow.CreateBrush(color);
            var material = new EmissiveMaterial(brush);
            var scale = new ScaleTransform3D(0, 0, 0);
            var rotation = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
            var translation = new TranslateTransform3D();
            var transform = new Transform3DGroup();
            transform.Children.Add(scale);
            transform.Children.Add(new RotateTransform3D(rotation));
            transform.Children.Add(translation);
            parent.Children.Add(new GeometryModel3D(PetAuraGlow.Quad, material)
            {
                BackMaterial = material,
                Transform = transform
            });
            particles[i] = new MusicParticleHandle
            {
                Scale = scale, Rotation = rotation, Translate = translation,
                SoftGlowBrush = brush, BaseColor = color, ParticleKind = kind
            };
        }
        return (particles.Length * 2, particles);
    }

    internal static (int Triangles, MusicParticleHandle[] Particles) Build(
        Model3DGroup parent, MeshGeometry3D mesh, Color color, string kind, int count = 8)
    {
        count = Math.Clamp(count, 6, 12);
        if (!mesh.IsFrozen)
        {
            mesh = mesh.CloneCurrentValue();
            mesh.Freeze();
        }
        var particles = new MusicParticleHandle[count];
        for (var i = 0; i < count; i++)
        {
            var tint = new SolidColorBrush(Color.FromArgb(0, color.R, color.G, color.B));
            var glow = new SolidColorBrush(Color.FromArgb(0, color.R, color.G, color.B));
            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(tint));
            material.Children.Add(new EmissiveMaterial(glow));
            var scale = new ScaleTransform3D(0, 0, 0);
            var rotation = new AxisAngleRotation3D(new Vector3D(0.2, 0.7, 1), i * 47);
            var translation = new TranslateTransform3D();
            var transform = new Transform3DGroup();
            transform.Children.Add(scale);
            transform.Children.Add(new RotateTransform3D(rotation));
            transform.Children.Add(translation);
            parent.Children.Add(new GeometryModel3D(mesh, material)
            {
                BackMaterial = material,
                Transform = transform
            });
            particles[i] = new MusicParticleHandle
            {
                Scale = scale,
                Rotation = rotation,
                Translate = translation,
                TintBrush = tint,
                EmissiveBrush = glow,
                BaseColor = color,
                ParticleKind = kind
            };
        }
        return (mesh.TriangleIndices.Count / 3 * count, particles);
    }
}
