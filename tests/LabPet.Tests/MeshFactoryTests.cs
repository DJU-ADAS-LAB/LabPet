using System.Runtime.ExceptionServices;
using System.Windows.Media.Media3D;
using LabPet.Client.Rendering;
using LabPet.Shared.Models;
using Xunit;

namespace LabPet.Tests;

public sealed class MeshFactoryTests
{
    [Theory]
    [InlineData(PetSpeciesIds.MochiSpirit, "default", "none", "none")]
    [InlineData(PetSpeciesIds.ChibiCat, "happy", "headset", "halo_ring")]
    [InlineData(PetSpeciesIds.FluffyCloud, "sparkle", "santa_hat", "music_notes")]
    [InlineData(PetSpeciesIds.FluffyPuppy, "default", "crown", "snowflakes")]
    [InlineData(PetSpeciesIds.FluffyPuppy, "sleepy", "headset", "halo_ring")]
    [InlineData(PetSpeciesIds.Hachiware, "default", "sprout", "music_notes")]
    [InlineData(PetSpeciesIds.Chiikawa, "happy", "ribbon", "halo_ring")]
    [InlineData(PetSpeciesIds.Usagi, "sparkle", "crown", "snowflakes")]
    public void BuildPetRig_ProducesValidLowToMidPolyMeshOnStaThread(
        string speciesId,
        string expressionId,
        string accessoryId,
        string auraId)
    {
        RunOnSta(() =>
        {
            var rig = new ProceduralPetMeshFactory().BuildPetRig(new PetAppearance
            {
                SpeciesId = speciesId,
                ExpressionId = expressionId,
                HeadAccessoryId = accessoryId,
                AuraEffectId = auraId
            });

            Assert.NotEmpty(rig.RootModel.Children);
            Assert.InRange(rig.TotalTriangleCount, 500, 15000);
        });
    }

    [Fact]
    public void BuildPetRig_CreatesOnlyRequestedKeyboardAndParticleGeometry()
    {
        RunOnSta(() =>
        {
            var factory = new ProceduralPetMeshFactory();
            var appearance = new PetAppearance { AuraEffectId = "halo_ring" };
            var full = factory.BuildPetRig(appearance);
            var visitor = factory.BuildPetRig(appearance, PetRigEffects.MusicParticles);
            var preview = factory.BuildPetRig(appearance, PetRigEffects.None);

            Assert.NotNull(full.TypingKeyboardScale);
            Assert.Equal(6, full.MusicParticles.Count);
            Assert.Null(visitor.TypingKeyboardScale);
            Assert.Equal(6, visitor.MusicParticles.Count);
            Assert.Null(preview.TypingKeyboardScale);
            Assert.Empty(preview.MusicParticles);
            Assert.NotNull(preview.AuraRotation);
            Assert.Equal(7, full.RootModel.Children.Count - preview.RootModel.Children.Count);
            Assert.Equal(6, visitor.RootModel.Children.Count - preview.RootModel.Children.Count);
            Assert.True(preview.TotalTriangleCount < visitor.TotalTriangleCount);
            Assert.True(visitor.TotalTriangleCount < full.TotalTriangleCount);
        });
    }

    [Fact]
    public void BuildTypingKeyboard_PlacesTheSpacebarBetweenThePetAndTheOtherKeys()
    {
        RunOnSta(() =>
        {
            var rig = new ProceduralPetMeshFactory().BuildPetRig(new PetAppearance(), PetRigEffects.TypingKeyboard);
            var keyboard = Assert.Single(rig.RootModel.Children.OfType<Model3DGroup>(),
                group => ReferenceEquals(group.Transform, rig.TypingKeyboardScale));
            var parts = GeometryModels(keyboard).Select(part => part.Bounds).ToArray();
            Assert.Equal(3, parts.Length);
            var deck = parts[0];
            var keys = parts[1];
            var spacebar = parts[2];

            Assert.True(spacebar.Z > deck.Z);
            Assert.True(spacebar.Z + spacebar.SizeZ < keys.Z);
            Assert.True(keys.Z + keys.SizeZ < deck.Z + deck.SizeZ);
        });
    }

    [Fact]
    public void BuildPetRig_SharesFrozenGeometryAcrossColorsAndKeepsAnimationIndependent()
    {
        RunOnSta(() =>
        {
            var factory = new ProceduralPetMeshFactory();
            var appearance = new PetAppearance { SpeciesId = PetSpeciesIds.FluffyPuppy, HeadAccessoryId = "crown", AuraEffectId = "snowflakes" };
            var first = factory.BuildPetRig(appearance);
            var recoloredAppearance = appearance.Clone();
            recoloredAppearance.BodyColorHex = "#F2C27D";
            recoloredAppearance.AccentColorHex = "#FFAAC7";
            var recolored = factory.BuildPetRig(recoloredAppearance);

            var firstModels = GeometryModels(first.RootModel).ToArray();
            var recoloredModels = GeometryModels(recolored.RootModel).ToArray();
            Assert.Equal(firstModels.Length, recoloredModels.Length);
            foreach (var (model, recoloredModel) in firstModels.Zip(recoloredModels))
            {
                Assert.True(model.Geometry.IsFrozen);
                Assert.Same(model.Geometry, recoloredModel.Geometry);
                Assert.NotSame(model, recoloredModel);
                Assert.NotSame(model.Material, recoloredModel.Material);
            }

            first.BodyTranslate.OffsetX = 1;
            first.MusicParticles[0].Scale.ScaleX = 1;
            Assert.Equal(0, recolored.BodyTranslate.OffsetX);
            Assert.Equal(0, recolored.MusicParticles[0].Scale.ScaleX);

            recoloredAppearance.ExpressionId = "sleepy";
            var sleepy = factory.BuildPetRig(recoloredAppearance);
            var firstGeometry = firstModels.Select(model => model.Geometry).ToHashSet();
            Assert.Contains(GeometryModels(sleepy.RootModel), model => !firstGeometry.Contains(model.Geometry));
        });
    }

    private static IEnumerable<GeometryModel3D> GeometryModels(Model3D model)
    {
        if (model is GeometryModel3D geometry)
        {
            yield return geometry;
        }
        else if (model is Model3DGroup group)
        {
            foreach (var child in group.Children.SelectMany(GeometryModels))
            {
                yield return child;
            }
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? threadException = null;
        var staThread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { threadException = ex; }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();
        if (threadException is not null)
        {
            ExceptionDispatchInfo.Capture(threadException).Throw();
        }
    }
}
