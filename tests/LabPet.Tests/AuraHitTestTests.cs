using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using LabPet.Client.Rendering;
using LabPet.Shared.Models;
using Xunit;

namespace LabPet.Tests;

[Collection("WpfUi")]
public sealed class AuraHitTestTests
{
    [Theory]
    [InlineData("halo_ring")]
    [InlineData("music_notes")]
    [InlineData("snowflakes")]
    [InlineData("starlight")]
    [InlineData("aurora")]
    [InlineData("fireflies")]
    [InlineData("cherry_blossom")]
    [InlineData("milky_way")]
    [InlineData("electric")]
    public void DesktopCamera_FilteredHitMapMatchesAuraRemovedScene(string auraId)
    {
        WpfTestHost.Run(() =>
        {
            var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory());
            var excluded = 0;
            foreach (var species in PetCustomizationCatalog.Species)
            {
                var appearance = new PetAppearance
                {
                    SpeciesId = species.Id, HeadAccessoryId = "crown", AuraEffectId = auraId
                };
                var rig = factory.BuildPetRig(appearance, PetRigEffects.None);
                var animator = new PetAnimationController();
                animator.AttachRig(rig, appearance.AccentColorHex);
                var view = new Viewport3D
                {
                    Camera = new PerspectiveCamera(new Point3D(0, 0.30, 6.65),
                        new Vector3D(0, -0.02, -1), new Vector3D(0, 1, 0), 36)
                };
                view.Children.Add(new ModelVisual3D { Content = rig.RootModel });
                view.Measure(new Size(214, 224));
                view.Arrange(new Rect(0, 0, 214, 224));
                view.UpdateLayout();
                for (var frame = 0; frame < 180; frame++)
                {
                    animator.UpdateAudioSnapshot(0.65f, 0.5f, 0.4f, frame % 30 == 0);
                    animator.UpdateFrame(1d / 60, 0.4, -0.1);
                    if (frame % 60 != 59) continue;
                    var raw = Map(view, false);
                    var filtered = Map(view, true);
                    var aura = rig.RootModel.Children[rig.RootModel.Children.Count - 1];
                    rig.RootModel.Children.Remove(aura);
                    try
                    {
                        var withoutAura = Map(view, false);
                        Assert.Contains(true, withoutAura);
                        Assert.Equal(withoutAura, filtered);
                        excluded += raw.Zip(filtered, (a, b) => a && !b ? 1 : 0).Sum();
                    }
                    finally { rig.RootModel.Children.Add(aura); }
                }
                animator.DetachRig();
            }
            Assert.True(excluded > 0, $"오라 전용 히트 표본이 없습니다: {auraId}");
        });
    }

    private static bool[] Map(Viewport3D view, bool filter)
    {
        var hits = new List<bool>();
        for (var y = 1; y < 224; y += 3)
        for (var x = 1; x < 214; x += 3)
        {
            var hit = false;
            VisualTreeHelper.HitTest(view, null, result =>
            {
                if (result is RayMeshGeometry3DHitTestResult mesh &&
                    (!filter || !PetEffectHitTest.IsDecorative(mesh.ModelHit)))
                {
                    hit = true;
                    return HitTestResultBehavior.Stop;
                }
                return HitTestResultBehavior.Continue;
            }, new PointHitTestParameters(new Point(x, y)));
            hits.Add(hit);
        }
        return hits.ToArray();
    }
}
