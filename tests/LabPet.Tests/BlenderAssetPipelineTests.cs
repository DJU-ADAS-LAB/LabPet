using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using LabPet.Client.Rendering;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using Xunit;

namespace LabPet.Tests;

public sealed class BlenderAssetPipelineTests
{
    [Theory]
    [InlineData(PetSpeciesIds.MochiSpirit)]
    [InlineData(PetSpeciesIds.ChibiCat)]
    [InlineData(PetSpeciesIds.FluffyCloud)]
    [InlineData(PetSpeciesIds.FluffyPuppy)]
    public void BuildPetRig_LoadsShippedBlenderAssetsForAllFourSpecies(string speciesId)
    {
        RunOnSta(() =>
        {
            var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory());
            var rig = factory.BuildPetRig(new PetAppearance
            {
                SpeciesId = speciesId,
                ExpressionId = "default",
                HeadAccessoryId = "headset",
                AuraEffectId = "halo_ring"
            });

            Assert.True(rig.IsExternalAsset);
            Assert.NotEmpty(rig.RootModel.Children);
            Assert.InRange(rig.TotalTriangleCount, 1200, 12000);
            Assert.Equal(BlenderAssetPetMeshFactory.MaxPooledParticles, rig.MusicParticles.Count);
        });
    }

    [Theory]
    [InlineData("missing_dir")]
    [InlineData("corrupt_rig_json")]
    [InlineData("corrupt_obj")]
    [InlineData("missing_core_part")]
    public void BuildPetRig_FallsBackToProceduralFactoryWhenAssetsMissingOrInvalid(string failureMode)
    {
        RunOnSta(() =>
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "LabPet_BlenderFallback_" + Guid.NewGuid().ToString("N"));
            try
            {
                if (failureMode != "missing_dir")
                {
                    var catDir = Path.Combine(tempRoot, "Characters", PetSpeciesIds.ChibiCat);
                    Directory.CreateDirectory(catDir);

                    var rigPath = Path.Combine(catDir, "rig.json");
                    var objPath = Path.Combine(catDir, "model.obj");

                    File.WriteAllText(rigPath, failureMode == "corrupt_rig_json"
                        ? "{ invalid json"
                        : """{"coordinateSystem":"WPF_Y_UP_Z_FRONT","unitScale":1.0}""");

                    File.WriteAllText(objPath, failureMode switch
                    {
                        "corrupt_obj" => "v not_a_number 0 0\nf 1 2 3\n",
                        "missing_core_part" => "o OnlyAccessory\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n",
                        _ => "v 0 0 0\n"
                    });
                }

                var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory(), tempRoot);
                var rig = factory.BuildPetRig(new PetAppearance { SpeciesId = PetSpeciesIds.ChibiCat });

                Assert.False(rig.IsExternalAsset);
                Assert.NotEmpty(rig.RootModel.Children);
                Assert.True(rig.TotalTriangleCount > 500);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        });
    }

    [Fact]
    public void BuildPetRig_SupportsAllCustomizationsAndReusesSharedFrozenGeometry()
    {
        RunOnSta(() =>
        {
            var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory());

            foreach (var expr in new[] { "default", "happy", "sleepy", "sparkle" })
            {
                foreach (var acc in new[] { "none", "sprout", "ribbon", "headset", "santa_hat", "crown" })
                {
                    var rig = factory.BuildPetRig(new PetAppearance
                    {
                        SpeciesId = PetSpeciesIds.ChibiCat,
                        ExpressionId = expr,
                        HeadAccessoryId = acc,
                        AuraEffectId = acc == "crown" ? "snowflakes" : "music_notes"
                    }, PetRigEffects.None);

                    Assert.True(rig.IsExternalAsset);
                    Assert.NotEmpty(rig.RootModel.Children);
                }
            }

            // 동일 외형 구조의 두 펫은 중간에 GC가 발생하더라도 동결된 기하 데이터를 공유하되 색상·애니메이션은 독립적이어야 한다.
            var first = factory.BuildPetRig(new PetAppearance
            {
                SpeciesId = PetSpeciesIds.FluffyPuppy,
                BodyColorHex = "#F2C27D",
                AccentColorHex = "#FF9EB5",
                HeadAccessoryId = "ribbon",
                AuraEffectId = "halo_ring"
            });

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var second = factory.BuildPetRig(new PetAppearance
            {
                SpeciesId = PetSpeciesIds.FluffyPuppy,
                BodyColorHex = "#E6F4FF",
                AccentColorHex = "#7CC6FE",
                HeadAccessoryId = "ribbon",
                AuraEffectId = "halo_ring"
            });

            var firstModels = GeometryModels(first.RootModel).ToArray();
            var secondModels = GeometryModels(second.RootModel).ToArray();
            Assert.Equal(firstModels.Length, secondModels.Length);
            Assert.Equal(first.AppearanceStructureSignature, second.AppearanceStructureSignature);

            for (var i = 0; i < firstModels.Length; i++)
            {
                Assert.True(firstModels[i].Geometry.IsFrozen);
                Assert.Same(firstModels[i].Geometry, secondModels[i].Geometry);
            }

            // 메시 재생성 없이 재질 브러시만으로 색상 즉시 변경 (다른 펫에 영향 없음)
            first.UpdateColors("#E9F7E8", "#6EE7B7");
            Assert.Equal(Color.FromRgb(0xE9, 0xF7, 0xE8), first.BodyDiffuseBrush!.Color);
            Assert.Equal(Color.FromRgb(0x6E, 0xE7, 0xB7), first.AccentDiffuseBrush!.Color);
            Assert.Equal(Color.FromRgb(0xE6, 0xF4, 0xFF), second.BodyDiffuseBrush!.Color);
            Assert.Equal(Color.FromRgb(0x7C, 0xC6, 0xFE), second.AccentDiffuseBrush!.Color);

            first.BodyTranslate.OffsetY = 0.22;
            Assert.Equal(0.0, second.BodyTranslate.OffsetY);
        });
    }

    [Fact]
    public void ObjMeshLoader_ReleasesUnusedCachedAssetsWhenNoLongerReferenced()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"LabPet_WeakObj_{Guid.NewGuid():N}.obj");
        try
        {
            File.WriteAllText(tempFile, """
                o Body
                usemtl body
                v 0 0 0
                v 1 0 0
                v 0 1 0
                f 1 2 3
                """);

            var loader = new ObjMeshLoader();
            var weakRef = LoadAndDrop(loader, tempFile);

            for (var i = 0; i < 4 && weakRef.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.False(weakRef.IsAlive);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference LoadAndDrop(ObjMeshLoader loader, string path)
        {
            Assert.True(loader.TryLoad(path, out var asset));
            Assert.NotNull(asset);
            return new WeakReference(asset);
        }
    }

    [Fact]
    public void PortraitRenderer_Caches72PxBitmapsWithBoundedLruCap()
    {
        RunOnSta(() =>
        {
            var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory());
            var renderer = new PetPortraitRenderer(factory);

            var baseApp = new PetAppearance { SpeciesId = PetSpeciesIds.ChibiCat };
            var p0 = renderer.GetPortrait(baseApp);
            Assert.Equal(72, p0.PixelWidth);
            Assert.Equal(72, p0.PixelHeight);
            Assert.True(p0.IsFrozen);
            Assert.Same(p0, renderer.GetPortrait(baseApp.Clone()));

            // 8개 초과 추가 시 LRU 캐시에서 가장 오래된 항목이 정리되어 10번째 삽입 후 재요청 시 새로 생성됨
            for (var i = 1; i <= 9; i++)
            {
                var alt = baseApp.Clone();
                alt.BodyColorHex = $"#FFE{i:D1}CC";
                var bmp = renderer.GetPortrait(alt);
                Assert.Equal(72, bmp.PixelWidth);
            }

            var rebuiltP0 = renderer.GetPortrait(baseApp);
            Assert.NotSame(p0, rebuiltP0);

            renderer.ClearCache();
        });
    }

    [Fact]
    public void PetAppearance_DeserializesLegacyJsonWithSafeDefaults()
    {
        const string legacyJson = """
        {
          "speciesId": "fluffy_cloud",
          "bodyColorHex": "#E6F4FF"
        }
        """;

        var loaded = JsonSerializer.Deserialize<PetAppearance>(legacyJson, ProtocolJson.StorageOptions);
        Assert.NotNull(loaded);
        Assert.Equal(PetSpeciesIds.FluffyCloud, loaded!.SpeciesId);
        Assert.Equal("#E6F4FF", loaded.BodyColorHex);
        Assert.Equal("default", loaded.ExpressionId);
        Assert.Equal("none", loaded.HeadAccessoryId);
        Assert.Equal("none", loaded.AuraEffectId);
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
