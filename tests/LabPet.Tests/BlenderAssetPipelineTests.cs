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

[Collection("WpfUi")]
public sealed class BlenderAssetPipelineTests
{
    [Fact]
    public void Joey_AnimatesAllEmbeddedClipsWithTexturedGeometry()
    {
        RunOnSta(() =>
        {
            var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory());
            var rig = factory.BuildPetRig(new PetAppearance { SpeciesId = PetSpeciesIds.Joey });
            var skin = Assert.IsType<PetSkinInstance>(rig.Skin);
            var models = GeometryModels(rig.RootModel).ToArray();
            Assert.Contains(models, m => m.Material is DiffuseMaterial { Brush: ImageBrush });
            foreach (var clip in new[] { "idle", "walk", "run", "dance", "jump", "sit", "sleep", "happy", "eat", "wave", "talk" })
            {
                skin.Update(rig, 0, 0, 0, 0, 0, -1, 0, clip);
                var start = models.SelectMany(m => ((MeshGeometry3D)m.Geometry).Positions).ToArray();
                skin.Update(rig, 0.27f, 0, 0, 0, 0, -1, 0, clip);
                var movement = models.SelectMany(m => ((MeshGeometry3D)m.Geometry).Positions)
                    .Zip(start, (a, b) => (a - b).Length).ToArray();
                Assert.All(movement, d => Assert.InRange(d, 0, 2));
                Assert.True(movement.Max() > 0.001, clip);
            }
        });
    }

    [Fact]
    public void Joey_TransitionsFromTheDisplayedPoseWhenInterrupted()
    {
        RunOnSta(() =>
        {
            const float frame = 1f / 60;
            var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory());
            var rig = factory.BuildPetRig(new PetAppearance { SpeciesId = PetSpeciesIds.Joey });
            var skin = Assert.IsType<PetSkinInstance>(rig.Skin);
            var models = GeometryModels(rig.RootModel).ToArray();

            void Update(string clip, float? progress = null) => skin.Update(rig, frame, 0, 0, 0, 0, -1, 0, clip, progress);
            Point3D[] Snapshot() => models.SelectMany(m => ((MeshGeometry3D)m.Geometry).Positions).ToArray();
            double Movement(Point3D[] previous) => Snapshot().Zip(previous, (a, b) => (a - b).Length).Max();

            Update("idle");
            var idle = Snapshot();
            Update("sleep");
            Assert.True(skin.IsAuthoredTransitioning);
            Assert.InRange(Movement(idle), 0, 0.00001);
            for (var i = 0; i < 5; i++) Update("sleep");
            Assert.True(skin.IsAuthoredTransitioning);
            Assert.True(Movement(idle) > 0.01);

            var fallingAsleep = Snapshot();
            Update("wave", 0.05f);
            Assert.InRange(Movement(fallingAsleep), 0, 0.00001);
            for (var i = 1; i <= 8; i++) Update("wave", i / 16f);
            var waving = Snapshot();
            Update("wave", 0.05f);
            Assert.InRange(Movement(waving), 0, 0.00001);
            for (var i = 1; i <= 20; i++) Update("wave", i / 20f);
            Assert.False(skin.IsAuthoredTransitioning);
            Assert.True(Movement(waving) > 0.01);

            var finished = Snapshot();
            Update("wave", 1);
            Assert.InRange(Movement(finished), 0, 0.00001);
            Update("idle");
            Assert.InRange(Movement(finished), 0, 0.00001);
            for (var i = 0; i < 20; i++) Update("idle");
            Assert.False(skin.IsAuthoredTransitioning);
            Assert.True(Movement(finished) > 0.001);
        });
    }

    [Fact]
    public void Joey_ControllerKeepsWaveSleepAndWakeTransitionsContinuous()
    {
        RunOnSta(() =>
        {
            const double frame = 1.0 / 60;
            var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory());
            var appearance = new PetAppearance { SpeciesId = PetSpeciesIds.Joey };
            var rig = factory.BuildPetRig(appearance);
            var controller = new PetAnimationController();
            controller.AttachRig(rig, appearance.AccentColorHex);
            var models = GeometryModels(rig.RootModel).ToArray();
            Point3D[] Snapshot() => models.SelectMany(m => ((MeshGeometry3D)m.Geometry).Positions).ToArray();
            double Advance()
            {
                var previous = Snapshot();
                controller.UpdateFrame(frame, 0, 0);
                return Snapshot().Zip(previous, (a, b) => (a - b).Length).Max();
            }

            Advance();
            controller.TriggerPetReaction();
            for (var i = 0; i < 52; i++) Advance();
            for (var i = 0; i < 20; i++) Assert.InRange(Advance(), 0, 0.10);

            controller.SetSleeping(true);
            var sleepEntry = Enumerable.Range(0, 20).Select(_ => Advance()).ToArray();
            Assert.All(sleepEntry, displacement => Assert.InRange(displacement, 0, 0.10));
            Assert.True(sleepEntry.Max() > 0.01);

            for (var i = 0; i < 60; i++) Advance();
            controller.SetSleeping(false);
            var wake = Enumerable.Range(0, 80).Select(_ => Advance()).ToArray();
            Assert.All(wake, displacement => Assert.InRange(displacement, 0, 0.12));
            Assert.True(wake.Max() > 0.01);
            Assert.False(controller.IsWakingUp);
            controller.DetachRig();
        });
    }

    [Theory]
    [InlineData(PetSpeciesIds.MochiSpirit)]
    [InlineData(PetSpeciesIds.ChibiCat)]
    [InlineData(PetSpeciesIds.FluffyCloud)]
    [InlineData(PetSpeciesIds.FluffyPuppy)]
    [InlineData(PetSpeciesIds.Hachiware)]
    [InlineData(PetSpeciesIds.Chiikawa)]
    [InlineData(PetSpeciesIds.Usagi)]
    public void WeightedSkin_BendsFiniteVerticesAndKeepsOtherPetInBindPose(string species)
    {
        RunOnSta(() =>
        {
            var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory());
            var appearance = new PetAppearance { SpeciesId = species };
            var rig = factory.BuildPetRig(appearance);
            var second = factory.BuildPetRig(appearance);
            var asset = Assert.IsType<PetSkinInstance>(rig.Skin).Asset;
            Assert.Same(asset, second.Skin!.Asset);
            Assert.All(asset.Bones.Select((b, i) => (b, i)), v => Assert.InRange(v.b.Parent, -1, v.i - 1));
            foreach (var left in asset.Bones.Where(b => b.Name.StartsWith("Left", StringComparison.Ordinal)))
            {
                var right = Assert.Single(asset.Bones, b => b.Name == "Right" + left.Name[4..]);
                Assert.Equal(-left.Bind.Translation.X, right.Bind.Translation.X, 4);
                Assert.Equal(left.Bind.Translation.Y, right.Bind.Translation.Y, 4);
                Assert.Equal(left.End.Z, right.End.Z, 4);
            }
            Assert.Contains(asset.Surfaces.Values, s => s.Weights.Any(w => w.X > 0.05 && w.Y > 0.05));
            var models = GeometryModels(rig.RootModel).ToArray();
            var original = models.Select(m => m.Geometry).ToArray();
            var controller = new PetAnimationController();
            controller.AttachRig(rig, appearance.AccentColorHex);
            for (int i = 0; i < 60; i++)
            {
                controller.UpdateAudioSnapshot(0.8f, 0.5f, 0.7f, i % 20 == 0);
                controller.UpdateFrame(1.0 / 60, 0.5, 0.2);
            }
            var displacement = models.SelectMany((m, i) =>
                ((MeshGeometry3D)m.Geometry).Positions.Zip(((MeshGeometry3D)original[i]).Positions,
                    (a, b) => (a - b).Length)).ToArray();
            Assert.All(displacement, d => Assert.InRange(d, 0, 1.2));
            Assert.True(displacement.Max() > 0.01);
            Assert.All(GeometryModels(second.RootModel), m => Assert.True(m.Geometry.IsFrozen));
            appearance.ExpressionId = "happy";
            Assert.True(rig.TryUpdateAppearance(appearance));
            Assert.Same(asset, rig.Skin!.Asset);
        });
    }

    [Theory]
    [InlineData(PetSpeciesIds.MochiSpirit)]
    [InlineData(PetSpeciesIds.ChibiCat)]
    [InlineData(PetSpeciesIds.FluffyCloud)]
    [InlineData(PetSpeciesIds.FluffyPuppy)]
    [InlineData(PetSpeciesIds.Hachiware)]
    [InlineData(PetSpeciesIds.Chiikawa)]
    [InlineData(PetSpeciesIds.Usagi)]
    public void UnifiedTypingKeyboard_AttachesSingleDeskKeyboardAndAnimatesTowardIt(string species)
    {
        RunOnSta(() =>
        {
            var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory());
            var appearance = new PetAppearance { SpeciesId = species };
            var rig = factory.BuildPetRig(appearance, PetRigEffects.TypingKeyboard);

            var keyboard = Assert.Single(
                rig.RootModel.Children.OfType<Model3DGroup>(),
                group => ReferenceEquals(group.Transform, rig.TypingKeyboardScale));
            var parts = GeometryModels(keyboard).Select(part => part.Bounds).ToArray();
            Assert.Equal(3, parts.Length);
            var deck = parts[0];
            var keys = parts[1];
            var spacebar = parts[2];
            Assert.InRange(deck.X + deck.SizeX * 0.5, -0.01, 0.01);
            Assert.True(deck.SizeX > 1.0);
            Assert.True(spacebar.Z > deck.Z);
            Assert.True(spacebar.Z + spacebar.SizeZ < keys.Z);

            var keyboardModels = GeometryModels(keyboard).ToHashSet();
            var characterModels = GeometryModels(rig.RootModel).Where(m => !keyboardModels.Contains(m)).ToArray();
            var restPositions = characterModels
                .Select(m => ((MeshGeometry3D)m.Geometry).Positions.ToArray())
                .ToArray();

            var controller = new PetAnimationController();
            controller.AttachRig(rig, appearance.AccentColorHex);

            controller.TriggerTypingPawTap();
            for (int i = 0; i < 12; i++)
            {
                controller.UpdateFrame(1.0 / 60, 0, 0);
            }

            Assert.NotNull(rig.TypingKeyboardScale);
            Assert.True(rig.TypingKeyboardScale!.ScaleX > 0.5);
            Assert.True(rig.BodyTranslate.OffsetZ > 0.05);
            Assert.True(rig.BodyPitch.Angle > 2.5);

            var leftTapPositions = characterModels
                .Select(m => ((MeshGeometry3D)m.Geometry).Positions.ToArray())
                .ToArray();

            controller.TriggerTypingPawTap();
            for (int i = 0; i < 8; i++)
            {
                controller.UpdateFrame(1.0 / 60, 0, 0);
            }

            var rightTapPositions = characterModels
                .Select(m => ((MeshGeometry3D)m.Geometry).Positions.ToArray())
                .ToArray();

            var maxForwardDeltaZ = 0.0;
            var maxLeftRightDelta = 0.0;
            for (int i = 0; i < characterModels.Length; i++)
            {
                for (int v = 0; v < restPositions[i].Length; v++)
                {
                    var rest = restPositions[i][v];
                    var left = leftTapPositions[i][v];
                    var right = rightTapPositions[i][v];
                    Assert.True(double.IsFinite(left.X) && double.IsFinite(left.Y) && double.IsFinite(left.Z));
                    Assert.True(double.IsFinite(right.X) && double.IsFinite(right.Y) && double.IsFinite(right.Z));
                    Assert.InRange((left - rest).Length, 0, 1.2);
                    Assert.InRange((right - rest).Length, 0, 1.2);
                    maxForwardDeltaZ = Math.Max(maxForwardDeltaZ, Math.Max(left.Z - rest.Z, right.Z - rest.Z));
                    maxLeftRightDelta = Math.Max(maxLeftRightDelta, (right - left).Length);
                }
            }

            Assert.True(maxForwardDeltaZ > 0.02);
            Assert.True(maxLeftRightDelta > 0.01);
        });
    }

    [Theory]
    [InlineData(PetSpeciesIds.MochiSpirit)]
    [InlineData(PetSpeciesIds.ChibiCat)]
    [InlineData(PetSpeciesIds.FluffyCloud)]
    [InlineData(PetSpeciesIds.FluffyPuppy)]
    [InlineData(PetSpeciesIds.Hachiware)]
    [InlineData(PetSpeciesIds.Chiikawa)]
    [InlineData(PetSpeciesIds.Usagi)]
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

    [Fact]
    public void CorruptGlb_UsesShippedObjBeforeProceduralFallback()
    {
        RunOnSta(() =>
        {
            var root = Directory.CreateTempSubdirectory();
            try
            {
                var relative = Path.Combine("Characters", PetSpeciesIds.Usagi);
                var dir = Directory.CreateDirectory(Path.Combine(root.FullName, relative));
                var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", relative);
                File.Copy(Path.Combine(assets, "model.obj"), Path.Combine(dir.FullName, "model.obj"));
                File.Copy(Path.Combine(assets, "rig.json"), Path.Combine(dir.FullName, "rig.json"));
                File.WriteAllBytes(Path.Combine(dir.FullName, "model.glb"), "glTF"u8.ToArray());
                var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory(), root.FullName);
                var rig = factory.BuildPetRig(new PetAppearance { SpeciesId = PetSpeciesIds.Usagi });
                Assert.True(rig.IsExternalAsset);
                Assert.Null(rig.Skin);
            }
            finally { root.Delete(true); }
        });
    }

    [Fact]
    public void AnimatedMeshes_ReuseBuffersAndReleaseRigAfterDetach()
    {
        RunOnSta(() =>
        {
            var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory());
            var animator = new PetAnimationController();
            var weak = Exercise(factory, animator);
            for (int i = 0; i < 4 && weak.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Assert.False(weak.IsAlive);
            GC.KeepAlive(factory);
            GC.KeepAlive(animator);
        });

        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference Exercise(BlenderAssetPetMeshFactory factory, PetAnimationController animator)
        {
            var appearance = new PetAppearance { SpeciesId = PetSpeciesIds.FluffyPuppy };
            var rig = factory.BuildPetRig(appearance, PetRigEffects.MusicParticles);
            animator.AttachRig(rig, appearance.AccentColorHex);
            animator.UpdateFrame(1d/60, 0, 0);
            var active = GeometryModels(rig.RootModel).Where(m => !m.Geometry.IsFrozen).ToArray();
            Assert.NotEmpty(active);
            var buffers = active.Select(m => ((MeshGeometry3D)m.Geometry).Positions).ToArray();
            for (int i = 0; i < 3600; i++)
            {
                if (i % 600 == 0) animator.SetSleeping(!animator.IsSleeping);
                if (i % 14 == 0) animator.TriggerTypingPawTap();
                animator.UpdateAudioSnapshot(0.5f, 0.35f, 0.6f, i % 30 == 0);
                animator.UpdateFrame(1d/60, Math.Sin(i * 0.02), 0.25);
            }
            for (int i = 0; i < active.Length; i++)
                Assert.Same(buffers[i], ((MeshGeometry3D)active[i].Geometry).Positions);
            animator.DetachRig();
            return new WeakReference(rig.Skin!);
        }
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
        WpfTestHost.Run(() =>
        {
            var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory());
            var renderer = new PetPortraitRenderer(factory);

            var baseApp = new PetAppearance { SpeciesId = PetSpeciesIds.ChibiCat };
            var p0 = renderer.GetPortrait(baseApp);
            Assert.Equal(72, p0.PixelWidth);
            Assert.Equal(72, p0.PixelHeight);
            Assert.True(p0.IsFrozen);
            Assert.Same(p0, renderer.GetPortrait(baseApp.Clone()));

            // 캐시 상한(32개) 초과 추가 시 LRU 캐시에서 가장 오래된 항목이 정리되어 재요청 시 새로 생성됨
            for (var i = 1; i <= PetPortraitRenderer.MaxCachedPortraits + 1; i++)
            {
                var alt = baseApp.Clone();
                var r = (i * 37) % 256;
                var g = (i * 59) % 256;
                var b = (i * 83) % 256;
                alt.BodyColorHex = $"#{r:X2}{g:X2}{b:X2}";
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
