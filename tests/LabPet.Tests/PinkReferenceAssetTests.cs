using System.Runtime.ExceptionServices;
using System.Windows.Media.Media3D;
using LabPet.Client.Rendering;
using LabPet.Shared.Models;
using Xunit;

namespace LabPet.Tests;

[Collection("WpfUi")]
public sealed class PinkReferenceAssetTests
{
    [Fact]
    public void ShippedKaniAsset_LoadsWithoutFallbackSupportsExpressionsAndSmoothTransitions()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory());
                var appearance = new PetAppearance
                {
                    SpeciesId = PetSpeciesIds.PinkReference,
                    BodyColorHex = "#FFF5F7",
                    AccentColorHex = "#FF9EB5",
                    ExpressionId = "default"
                };

                var rig = factory.BuildPetRig(appearance, PetRigEffects.None);
                Assert.True(rig.IsExternalAsset);
                var skin = Assert.IsType<PetSkinInstance>(rig.Skin);
                Assert.True(skin.Asset.Bones.Length >= 10);
                Assert.InRange(rig.TotalTriangleCount, 1800, 9000);

                var catalogEntry = Assert.Single(PetCustomizationCatalog.Species, o => o.Id == PetSpeciesIds.PinkReference);
                Assert.Equal("카니", catalogEntry.DisplayName);

                // 4종 표정 전환이 메시 재생성 없이 동작해야 한다.
                foreach (var expr in new[] { "happy", "sleepy", "sparkle", "default" })
                {
                    appearance.ExpressionId = expr;
                    Assert.True(rig.TryUpdateAppearance(appearance));
                    CheckFinite(rig.RootModel);
                }

                var controller = new PetAnimationController();
                controller.AttachRig(rig, appearance.AccentColorHex);

                Point3D[] Snapshot() => WorldPoints(rig.RootModel, Matrix3D.Identity).ToArray();

                double Advance()
                {
                    var before = Snapshot();
                    controller.UpdateFrame(1.0 / 60, 0, 0);
                    var after = Snapshot();
                    Assert.Equal(before.Length, after.Length);
                    return Enumerable.Range(0, after.Length)
                        .Max(i => (after[i] - before[i]).Length);
                }

                for (var i = 0; i < 60; i++)
                {
                    Advance();
                }

                void Measure(string name, Action trigger, int frames, bool checkWakeBoundaries = false)
                {
                    trigger();
                    var maximum = Enumerable.Range(0, frames).Select(_ => Advance()).Max();
                    Assert.True(double.IsFinite(maximum), $"{name}: non-finite movement");
                    if (!name.Contains("wake", StringComparison.Ordinal))
                    {
                        Assert.True(maximum <= 0.12, $"{name}: peak={maximum:F6}");
                    }

                    if (checkWakeBoundaries)
                    {
                        foreach (var boundary in new[] { 0.0, 0.95 * 0.28, 0.95 * 0.72, 0.94, 0.95 })
                        {
                            controller.SetSleeping(true);
                            for (var i = 0; i < 120; i++)
                            {
                                Advance();
                            }

                            var beforeTrigger = Snapshot();
                            controller.SetSleeping(false);
                            Assert.True(Snapshot().Zip(beforeTrigger, (a, b) => (a - b).Length).Max() < 0.000001);

                            var remaining = Math.Max(0, boundary - 0.001);
                            while (remaining > 0.0000001)
                            {
                                var dt = Math.Min(0.001, remaining);
                                controller.UpdateFrame(dt, 0, 0);
                                remaining -= dt;
                            }

                            var beforeBoundary = Snapshot();
                            controller.UpdateFrame(0.002, 0, 0);
                            var delta = Snapshot().Zip(beforeBoundary, (a, b) => (a - b).Length).Max();
                            Assert.True(double.IsFinite(delta) && delta < 0.04, $"Wake boundary {boundary:F3}s: {delta:F6} over 2ms");
                        }

                        for (var i = 0; i < 90; i++)
                        {
                            Advance();
                        }
                    }
                }

                Measure("idle_to_wave_to_idle", controller.TriggerPetReaction, 150);
                Measure("idle_to_sleep", () => controller.SetSleeping(true), 150);
                Measure("sleep_to_wake_to_idle", () => controller.SetSleeping(false), 150, checkWakeBoundaries: true);
                Measure("sleep_interrupted_by_wake", () =>
                {
                    controller.SetSleeping(true);
                    for (var i = 0; i < 5; i++)
                    {
                        Advance();
                    }

                    controller.SetSleeping(false);
                }, 120);

                CheckFinite(rig.RootModel);
                controller.DetachRig();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static IEnumerable<Point3D> WorldPoints(Model3D model, Matrix3D parent)
    {
        var world = model.Transform?.Value ?? Matrix3D.Identity;
        world.Append(parent);
        if (model is GeometryModel3D { Geometry: MeshGeometry3D mesh })
        {
            foreach (var point in mesh.Positions)
            {
                yield return world.Transform(point);
            }
        }

        if (model is Model3DGroup group)
        {
            foreach (var child in group.Children)
            {
                foreach (var point in WorldPoints(child, world))
                {
                    yield return point;
                }
            }
        }
    }

    private static void CheckFinite(Model3D model)
    {
        if (model is Model3DGroup group)
        {
            foreach (var child in group.Children)
            {
                CheckFinite(child);
            }
        }

        if (model is GeometryModel3D { Geometry: MeshGeometry3D mesh })
        {
            Assert.All(mesh.Positions, p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z)));
        }
    }
}
