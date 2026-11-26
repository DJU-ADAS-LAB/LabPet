using System.Runtime.ExceptionServices;
using System.Windows.Media.Media3D;
using LabPet.Client.Rendering;
using LabPet.Shared.Models;
using Xunit;

namespace LabPet.Tests;

[Collection("WpfUi")]
public sealed class MeshFactoryTests
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
    public void AuraModels_AreDecorativeButBodyAndAccessoryRemainInteractive(string auraId)
    {
        RunOnSta(() =>
        {
            var fallback = new ProceduralPetMeshFactory();
            IPetMeshFactory[] factories = [fallback, new BlenderAssetPetMeshFactory(fallback)];
            foreach (var factory in factories)
            foreach (var species in PetCustomizationCatalog.Species)
            {
                var rig = factory.BuildPetRig(new PetAppearance
                {
                    SpeciesId = species.Id, HeadAccessoryId = "crown", AuraEffectId = auraId
                }, PetRigEffects.None);
                var aura = rig.RootModel.Children[rig.RootModel.Children.Count - 1];
                var auraModels = GeometryModels(aura).ToArray();
                Assert.NotEmpty(auraModels);
                Assert.All(auraModels, model => Assert.True(PetEffectHitTest.IsDecorative(model)));
                var body = rig.RootModel.Children.OfType<Model3DGroup>()
                    .Single(g => ReferenceEquals(g.Transform, rig.BodyTransform));
                Assert.All(GeometryModels(body), model => Assert.False(PetEffectHitTest.IsDecorative(model)));
            }
        });
    }

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

    [Theory]
    [InlineData("sprout")]
    [InlineData("ribbon")]
    public void FallbackAccessory_AnimatesAroundSocketWithoutReplacingMesh(string accessoryId)
    {
        RunOnSta(() =>
        {
            foreach (var species in PetCustomizationCatalog.Species)
            {
                var rig = new ProceduralPetMeshFactory().BuildPetRig(new PetAppearance
                {
                    SpeciesId = species.Id, HeadAccessoryId = accessoryId
                }, PetRigEffects.None);
                Assert.Equal(accessoryId, rig.HeadAccessoryId);
                Assert.NotNull(rig.AccessoryRotation);
                var body = rig.RootModel.Children.OfType<Model3DGroup>()
                    .Single(g => ReferenceEquals(g.Transform, rig.BodyTransform));
                var head = body.Children.OfType<Model3DGroup>().Single(g =>
                    g.Children.OfType<Model3DGroup>().Any(a => a.Transform is RotateTransform3D r
                        && ReferenceEquals(r.Rotation, rig.AccessoryRotation)));
                var accessory = head.Children.OfType<Model3DGroup>().Single(a =>
                    a.Transform is RotateTransform3D r && ReferenceEquals(r.Rotation, rig.AccessoryRotation));
                var transform = Assert.IsType<RotateTransform3D>(accessory.Transform);
                var socket = new Point3D(transform.CenterX, transform.CenterY, transform.CenterZ);
                var meshes = GeometryModels(accessory).Select(m => m.Geometry).ToArray();
                var animator = new PetAnimationController();
                animator.AttachRig(rig, "#FF9EB5");
                var moved = false;
                for (var frame = 0; frame < 180; frame++)
                {
                    animator.UpdateAudioSnapshot(0.7f, 0.5f, 0.4f, frame % 30 == 0);
                    animator.UpdateFrame(1d / 60, 0.6, -0.2);
                    Assert.InRange(rig.AccessoryRotation.Angle, -6, 6);
                    Assert.Equal(socket, transform.Transform(socket));
                    moved |= Math.Abs(rig.AccessoryRotation.Angle) > 0.01;
                }
                Assert.True(moved);
                Assert.Equal(meshes, GeometryModels(accessory).Select(m => m.Geometry).ToArray());
            }
        });
    }

    [Fact]
    public void FallbackSanta_OnlyPomSwingsAndKeepsItsAttachmentFixed()
    {
        RunOnSta(() =>
        {
            foreach (var species in PetCustomizationCatalog.Species)
            {
                var rig = new ProceduralPetMeshFactory().BuildPetRig(new PetAppearance
                {
                    SpeciesId = species.Id, HeadAccessoryId = "santa_hat"
                }, PetRigEffects.None);
                Assert.NotNull(rig.AccessoryRotation);
                var body = rig.RootModel.Children.OfType<Model3DGroup>()
                    .Single(g => ReferenceEquals(g.Transform, rig.BodyTransform));
                var head = body.Children.OfType<Model3DGroup>().Single(g =>
                    g.Children.OfType<Model3DGroup>().Any(a =>
                        a.Children.OfType<Model3DGroup>().Any(p => p.Transform is RotateTransform3D r
                            && ReferenceEquals(r.Rotation, rig.AccessoryRotation))));
                var hat = head.Children.OfType<Model3DGroup>().Single(a =>
                    a.Children.OfType<Model3DGroup>().Any(p => p.Transform is RotateTransform3D r
                        && ReferenceEquals(r.Rotation, rig.AccessoryRotation)));
                Assert.True(hat.Transform.Value.IsIdentity);
                var pom = Assert.Single(hat.Children.OfType<Model3DGroup>());
                Assert.Single(GeometryModels(pom));
                var fixedParts = hat.Children.OfType<GeometryModel3D>().ToArray();
                Assert.Equal(2, fixedParts.Length);
                var bounds = fixedParts.Select(p => p.Bounds).ToArray();
                var rotation = Assert.IsType<RotateTransform3D>(pom.Transform);
                var anchor = new Point3D(rotation.CenterX, rotation.CenterY, rotation.CenterZ);
                var animator = new PetAnimationController();
                animator.AttachRig(rig, "#FF9EB5");
                var moved = false;
                for (var frame = 0; frame < 180; frame++)
                {
                    animator.UpdateFrame(1d / 60, 0.6, -0.2);
                    Assert.InRange(rig.AccessoryRotation.Angle, -6, 6);
                    Assert.True((rotation.Transform(anchor) - anchor).Length < 1e-10);
                    moved |= Math.Abs(rig.AccessoryRotation.Angle) > 0.01;
                }
                Assert.True(moved);
                Assert.Equal(bounds, fixedParts.Select(p => p.Bounds).ToArray());
            }
        });
    }

    [Fact]
    public void FallbackHeadset_LedRespondsToAudioWithoutReplacingGeometry()
    {
        RunOnSta(() =>
        {
            foreach (var species in PetCustomizationCatalog.Species)
            {
                var rig = new ProceduralPetMeshFactory().BuildPetRig(new PetAppearance
                {
                    SpeciesId = species.Id, HeadAccessoryId = "headset"
                }, PetRigEffects.None);
                var brush = rig.AccessoryEmissiveBrush;
                Assert.NotNull(brush);
                Assert.False(brush.IsFrozen);
                var led = Assert.Single(GeometryModels(rig.RootModel), m =>
                    m.Material is EmissiveMaterial e
                    && ReferenceEquals(e.Brush, brush));
                var geometry = led.Geometry;
                Assert.True(geometry.IsFrozen);
                var animator = new PetAnimationController();
                animator.AttachRig(rig, "#FF9EB5");
                for (var frame = 0; frame < 120; frame++)
                {
                    animator.UpdateAudioSnapshot(0, 0, 0, false);
                    animator.UpdateFrame(1d / 60, 0, 0);
                }
                var quiet = brush.Opacity;
                for (var frame = 0; frame < 120; frame++)
                {
                    animator.UpdateAudioSnapshot(0.8f, 0.8f, 0.5f, frame % 30 == 0);
                    animator.UpdateFrame(1d / 60, 0, 0);
                    Assert.InRange(brush.Opacity, 0, 1);
                }
                Assert.True(brush.Opacity > quiet);
                Assert.Same(geometry, led.Geometry);
                Assert.Same(brush, rig.AccessoryEmissiveBrush);
            }
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
            // 오라는 미리보기에도 유지되며 그룹 회전 대신 독립 풀로 갱신한다.
            Assert.Null(preview.AuraRotation);
            Assert.Equal("halo_ring", preview.AuraEffectId);
            Assert.InRange(preview.AuraParticles.Count, 6, 12);
            Assert.Equal(full.AuraParticles.Count, preview.AuraParticles.Count);
            Assert.Equal(visitor.AuraParticles.Count, preview.AuraParticles.Count);
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
    public void UpdateColors_MatchesFreshAuraWithoutReplacingGeometryOrResettingFade(string auraId)
    {
        RunOnSta(() =>
        {
            foreach (var factory in new IPetMeshFactory[]
            {
                new ProceduralPetMeshFactory(),
                new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory())
            })
            {
                var appearance = new PetAppearance { AuraEffectId = auraId, AccentColorHex = "#FF3388" };
                var rig = factory.BuildPetRig(appearance, PetRigEffects.None);
                Assert.NotEmpty(rig.AuraParticles);
                var animator = new PetAnimationController();
                animator.AttachRig(rig, appearance.AccentColorHex);
                animator.UpdateFrame(1d / 60, 0, 0);
                var particles = rig.AuraParticles;
                var models = GeometryModels(rig.RootModel).ToArray();
                var alphas = particles.Select(p => (p.TintBrush?.Color.A, p.EmissiveBrush?.Color.A)).ToArray();
                appearance.AccentColorHex = "#2288EE";
                rig.UpdateColors(appearance.BodyColorHex, appearance.AccentColorHex);
                var fresh = factory.BuildPetRig(appearance, PetRigEffects.None);
                Assert.Same(particles, rig.AuraParticles);
                for (var i = 0; i < particles.Count; i++)
                {
                    Assert.Equal(fresh.AuraParticles[i].BaseColor, particles[i].BaseColor);
                    Assert.Equal(alphas[i], (particles[i].TintBrush?.Color.A, particles[i].EmissiveBrush?.Color.A));
                }
                var updatedModels = GeometryModels(rig.RootModel).ToArray();
                Assert.Equal(models.Length, updatedModels.Length);
                for (var i = 0; i < models.Length; i++)
                {
                    Assert.Same(models[i], updatedModels[i]);
                    Assert.Same(models[i].Geometry, updatedModels[i].Geometry);
                }
            }
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
