using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using LabPet.Shared.Models;

namespace LabPet.Client.Rendering;

public sealed class PetPortraitRenderer(IPetMeshFactory meshFactory)
{
    private const int PixelSize = 72;
    private const int MaxCachedPortraits = 8;
    private readonly Dictionary<PortraitAppearance, BitmapSource> _cache = [];
    private readonly LinkedList<PortraitAppearance> _lruOrder = [];

    public void ClearCache()
    {
        _cache.Clear();
        _lruOrder.Clear();
    }

    public BitmapSource GetPortrait(PetAppearance appearance)
    {
        var key = new PortraitAppearance(appearance.SpeciesId, appearance.BodyColorHex, appearance.AccentColorHex,
            appearance.ExpressionId, appearance.HeadAccessoryId, appearance.AuraEffectId);
        if (_cache.TryGetValue(key, out var portrait))
        {
            _lruOrder.Remove(key);
            _lruOrder.AddLast(key);
            return portrait;
        }

        // 작은 프로필은 애니메이션 없이 한 번 렌더링해 여러 말풍선에서 비트맵을 공유한다.
        var model = meshFactory.BuildPetRig(appearance, PetRigEffects.None).RootModel;
        var bounds = model.Bounds;
        var center = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        var viewport = new Viewport3D
        {
            Camera = new OrthographicCamera(
                center + new Vector3D(0, 0.4, 5),
                new Vector3D(0, -0.4, -5),
                new Vector3D(0, 1, 0),
                Math.Max(bounds.SizeX, bounds.SizeY + bounds.SizeZ * 0.08) * 1.08)
        };
        viewport.Children.Add(new ModelVisual3D { Content = meshFactory.CreateStudioLights() });
        viewport.Children.Add(new ModelVisual3D { Content = model });
        viewport.Measure(new Size(PixelSize, PixelSize));
        viewport.Arrange(new Rect(0, 0, PixelSize, PixelSize));

        var bitmap = new RenderTargetBitmap(PixelSize, PixelSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(viewport);
        bitmap.Freeze();

        if (_cache.Count >= MaxCachedPortraits && _lruOrder.First is { } oldest)
        {
            _lruOrder.RemoveFirst();
            _cache.Remove(oldest.Value);
        }

        _cache.Add(key, bitmap);
        _lruOrder.AddLast(key);
        return bitmap;
    }

    private readonly record struct PortraitAppearance(string Species, string Body, string Accent,
        string Expression, string Accessory, string Aura);
}
