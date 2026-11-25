using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using LabPet.Client.Interop;
using LabPet.Client.Rendering;
using LabPet.Shared.Models;
using Xunit;

namespace LabPet.Tests;

[Collection("WpfUi")]
public sealed class PetAnimationAndEffectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snowball_CancelStopsLateReleaseAndPreservesSleep(bool sleeping)
    {
        RunOnSta(() =>
        {
            var rig = new ProceduralPetMeshFactory().BuildPetRig(
                new PetAppearance { SpeciesId = PetSpeciesIds.MochiSpirit }, PetRigEffects.None);
            var controller = new PetAnimationController();
            controller.SetSleeping(sleeping);
            controller.AttachRig(rig, "#FF9EB5");
            controller.SetSnowballLaughing(true);
            for (var i = 0; i < 20; i++) controller.UpdateFrame(0.01, 0, 0);
            Assert.Equal("happy", rig.ReactionExpression);
            Assert.True(controller.TryStartSnowballThrow());
            controller.TriggerSnowballHit();
            controller.CancelSnowballInteraction();
            controller.CancelSnowballInteraction();
            Assert.Null(rig.ReactionExpression);
            Assert.False(controller.IsThrowingSnowball);
            Assert.False(controller.IsReactingToSnowball);
            Assert.False(controller.IsLaughingAfterSnowball);
            for (var i = 0; i < 60; i++)
            {
                controller.UpdateFrame(1.0 / 60, 0, 0);
                Assert.False(controller.SnowballReleasedThisFrame);
            }
            Assert.Equal(sleeping, controller.IsSleeping);
            Assert.True(controller.TryStartSnowballThrow());
            controller.CancelSnowballInteraction();
            if (!sleeping)
            {
                controller.TriggerGameReaction(true);
                controller.CancelSnowballInteraction();
                Assert.Equal("happy", rig.ReactionExpression);
                Assert.True(controller.HasInteractiveMotion);
            }
            controller.DetachRig();
            controller.CancelSnowballInteraction();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snowball_HitsExtendWithoutRestartAndPreserveUnderlyingState(bool sleeping)
    {
        RunOnSta(() =>
        {
            var factory = new ProceduralPetMeshFactory();
            var appearance = new PetAppearance { SpeciesId = PetSpeciesIds.MochiSpirit };
            var rig = factory.BuildPetRig(appearance, PetRigEffects.None);
            var referenceRig = factory.BuildPetRig(appearance, PetRigEffects.None);
            var controller = new PetAnimationController();
            var reference = new PetAnimationController();
            controller.SetSleeping(sleeping);
            reference.SetSleeping(sleeping);
            controller.AttachRig(rig, "#FF9EB5");
            reference.AttachRig(referenceRig, "#FF9EB5");
            controller.TriggerSnowballHit();
            reference.TriggerSnowballHit();
            for (var i = 0; i < 6; i++)
            {
                controller.UpdateFrame(1.0 / 60, 0, 0);
                reference.UpdateFrame(1.0 / 60, 0, 0);
            }
            controller.TriggerSnowballHit();
            controller.UpdateFrame(1.0 / 60, 0, 0);
            reference.UpdateFrame(1.0 / 60, 0, 0);
            Assert.Equal(referenceRig.BodyPitch.Angle, rig.BodyPitch.Angle, 8);
            Assert.Equal(referenceRig.HeadRoll.Angle, rig.HeadRoll.Angle, 8);
            Assert.True(controller.HasInteractiveMotion);
            for (var i = 0; i < 17; i++)
            {
                controller.UpdateFrame(1.0 / 60, 0, 0);
                reference.UpdateFrame(1.0 / 60, 0, 0);
            }
            Assert.False(reference.IsReactingToSnowball);
            Assert.True(controller.IsReactingToSnowball);
            for (var i = 0; i < 100; i++) controller.TriggerSnowballHit();
            for (var i = 0; i < 42; i++) controller.UpdateFrame(1.0 / 60, 0, 0);
            Assert.False(controller.IsReactingToSnowball);
            Assert.Equal(sleeping, controller.IsSleeping);
            Assert.InRange(rig.BodyScale.ScaleY, 0.80, 1.24);
            if (sleeping) Assert.InRange(rig.EyeBlinkScale!.ScaleY, 0, 0.1);
            controller.TriggerSnowballHit();
            controller.AttachRig(rig, "#FF9EB5");
            Assert.False(controller.IsReactingToSnowball);
            controller.TriggerSnowballHit();
            controller.DetachRig();
            Assert.False(controller.IsReactingToSnowball);
            controller.TriggerSnowballHit();
            Assert.False(controller.IsReactingToSnowball);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snowball_RepeatedThrowsReleaseOnceAndPreserveSleep(bool sleeping)
    {
        RunOnSta(() =>
        {
            var rig = new ProceduralPetMeshFactory().BuildPetRig(
                new PetAppearance { SpeciesId = PetSpeciesIds.MochiSpirit }, PetRigEffects.None);
            var controller = new PetAnimationController();
            controller.SetSleeping(sleeping);
            controller.AttachRig(rig, "#FF9EB5");
            for (var attempt = 0; attempt < 10; attempt++)
            {
                Assert.True(controller.TryStartSnowballThrow());
                Assert.True(controller.HasInteractiveMotion);
                var releases = 0;
                for (var frame = 0; frame < 60; frame++)
                {
                    if (frame < 40) Assert.False(controller.TryStartSnowballThrow());
                    controller.UpdateFrame(1.0 / 60, 0, 0);
                    if (controller.SnowballReleasedThisFrame) releases++;
                    Assert.True(double.IsFinite(rig.RightArmPitch!.Angle));
                    Assert.InRange(rig.RightArmPitch.Angle, -65, 65);
                }
                Assert.Equal(1, releases);
                Assert.False(controller.IsThrowingSnowball);
                Assert.Equal(sleeping, controller.IsSleeping);
            }
            Assert.True(controller.TryStartSnowballThrow());
            // 발사 신호는 한 프레임만 유지되므로 실제 발생 프레임에서 멈춘다.
            var releaseFrame = 0;
            while (!controller.SnowballReleasedThisFrame && releaseFrame < 60)
            {
                controller.UpdateFrame(1.0 / 60, 0, 0);
                releaseFrame++;
            }
            Assert.True(controller.SnowballReleasedThisFrame);
            Assert.InRange(releaseFrame / 60.0, SnowballThrowPose.ReleaseSeconds,
                SnowballThrowPose.ReleaseSeconds + 1.0 / 60);
            controller.AttachRig(rig, "#FF9EB5");
            Assert.False(controller.IsThrowingSnowball);
            Assert.False(controller.SnowballReleasedThisFrame);
            Assert.True(controller.TryStartSnowballThrow());
            controller.DetachRig();
            Assert.False(controller.IsThrowingSnowball);
            Assert.False(controller.SnowballReleasedThisFrame);
            Assert.False(controller.TryStartSnowballThrow());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snowball_LaughBlendsOutAndClearsOnRigReplacement(bool sleeping)
    {
        RunOnSta(() =>
        {
            var rig = new ProceduralPetMeshFactory().BuildPetRig(
                new PetAppearance { SpeciesId = PetSpeciesIds.MochiSpirit }, PetRigEffects.None);
            var controller = new PetAnimationController();
            controller.SetSleeping(sleeping);
            controller.AttachRig(rig, "#FF9EB5");
            controller.SetSnowballLaughing(true);
            Assert.True(controller.HasInteractiveMotion);
            for (var i = 0; i < 20; i++) controller.UpdateFrame(0.01, 0, 0);
            Assert.InRange(rig.EyeBlinkScale!.ScaleY, 0.06, 0.08);
            Assert.Equal(sleeping, controller.IsSleeping);
            controller.SetSnowballLaughing(false);
            controller.UpdateFrame(0.01, 0, 0);
            Assert.True(controller.IsLaughingAfterSnowball);
            for (var i = 0; i < 20; i++) controller.UpdateFrame(0.01, 0, 0);
            Assert.False(controller.IsLaughingAfterSnowball);
            Assert.Equal(sleeping, controller.IsSleeping);
            if (!sleeping) Assert.InRange(rig.EyeBlinkScale.ScaleY, 0.99, 1.01);
            controller.SetSnowballLaughing(true);
            controller.UpdateFrame(0.01, 0, 0);
            controller.AttachRig(rig, "#FF9EB5");
            Assert.False(controller.IsLaughingAfterSnowball);
            controller.SetSnowballLaughing(true);
            controller.DetachRig();
            Assert.False(controller.IsLaughingAfterSnowball);
            controller.SetSnowballLaughing(true);
            Assert.False(controller.IsLaughingAfterSnowball);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snowball_LaughExpressionRestoresAppearanceAndYieldsToGame(bool external)
    {
        RunOnSta(() =>
        {
            var procedural = new ProceduralPetMeshFactory();
            IPetMeshFactory factory = external ? new BlenderAssetPetMeshFactory(procedural) : procedural;
            var appearance = new PetAppearance { SpeciesId = PetSpeciesIds.MochiSpirit, ExpressionId = "sparkle" };
            var rig = factory.BuildPetRig(appearance, PetRigEffects.None);
            Assert.Equal(external, rig.IsExternalAsset);
            Assert.NotNull(rig.SelectExpression);
            var controller = new PetAnimationController();
            controller.AttachRig(rig, appearance.AccentColorHex);
            void Laugh()
            {
                controller.SetSnowballLaughing(true);
                for (var i = 0; i < 20; i++) controller.UpdateFrame(0.01, 0, 0);
                Assert.Equal("happy", rig.ReactionExpression);
            }
            Laugh();
            appearance.ExpressionId = "default";
            Assert.True(rig.TryUpdateAppearance(appearance));
            Assert.Equal("happy", rig.ReactionExpression);
            controller.SetSnowballLaughing(false);
            controller.UpdateFrame(0.01, 0, 0);
            Assert.Null(rig.ReactionExpression);
            Assert.Equal(factory.BuildPetRig(appearance, PetRigEffects.None).TotalTriangleCount, rig.TotalTriangleCount);
            Laugh();
            Assert.True(controller.TryStartSnowballThrow());
            controller.SetSnowballLaughing(false);
            controller.UpdateFrame(0.01, 0, 0);
            Assert.Null(rig.ReactionExpression);
            for (var i = 0; i < 100; i++) controller.UpdateFrame(0.01, 0, 0);
            Laugh();
            controller.SetGrabbed(true);
            controller.UpdateFrame(0.01, 0, 0);
            Assert.Null(rig.ReactionExpression);
            controller.SetGrabbed(false);
            for (var i = 0; i < 30; i++) controller.UpdateFrame(0.01, 0, 0);
            foreach (var won in new[] { true, false })
            {
                Laugh();
                controller.TriggerGameReaction(won);
                controller.SetSnowballLaughing(false);
                for (var i = 0; i < 20; i++) controller.UpdateFrame(0.01, 0, 0);
                Assert.Equal(won ? "happy" : "sleepy", rig.ReactionExpression);
                for (var i = 0; i < 220; i++) controller.UpdateFrame(0.01, 0, 0);
                Assert.Null(rig.ReactionExpression);
            }
            Laugh();
            controller.AttachRig(rig, appearance.AccentColorHex);
            Assert.Null(rig.ReactionExpression);
            Laugh();
            controller.DetachRig();
            Assert.Null(rig.ReactionExpression);
        });
    }

    [Fact]
    public void MotionClips_ProduceDistinctBehaviorsAcrossSpecies()
    {
        var mochiPose = PetMotionClips.EvaluateClip(
            PetMotionClipKind.Bounce, SpeciesMotionProfile.Mochi,
            dancePhase: 1.2, tailPhase: 1.0, elapsedSeconds: 2.0, bass: 0.85, mid: 0.4, treble: 0.3, beat: 0.8);

        var catPose = PetMotionClips.EvaluateClip(
            PetMotionClipKind.PawDance, SpeciesMotionProfile.Cat,
            dancePhase: 1.1, tailPhase: 1.0, elapsedSeconds: 2.0, bass: 0.5, mid: 0.6, treble: 0.8, beat: 0.5);

        var puppyPose = PetMotionClips.EvaluateClip(
            PetMotionClipKind.Groove, SpeciesMotionProfile.Puppy,
            dancePhase: 1.2, tailPhase: 1.0, elapsedSeconds: 2.0, bass: 0.6, mid: 0.7, treble: 0.7, beat: 0.5);

        var cloudPose = PetMotionClips.EvaluateClip(
            PetMotionClipKind.Idle, SpeciesMotionProfile.Cloud,
            dancePhase: 0.0, tailPhase: 0.0, elapsedSeconds: 0.8, bass: 0, mid: 0, treble: 0, beat: 0);

        var catIdle = PetMotionClips.EvaluateClip(
            PetMotionClipKind.Idle, SpeciesMotionProfile.Cat,
            dancePhase: 0.0, tailPhase: 0.0, elapsedSeconds: 0.8, bass: 0, mid: 0, treble: 0, beat: 0);

        // 모찌는 강한 Squash & Stretch, 고양이는 앞발/꼬리 춤, 강아지는 처진 귀 바깥 펄럭임(음수 왼쪽 귀 오프셋), 솜구름은 큰 부유
        Assert.NotEqual(1.0, mochiPose.BodyScaleY);
        Assert.True(Math.Abs(catPose.LeftPawY) + Math.Abs(catPose.RightPawY) > 0.05);
        Assert.True(Math.Abs(catPose.TailAngle) > 0.1);
        Assert.True(puppyPose.LeftEarOffset < 0 && puppyPose.RightEarOffset > 0);
        Assert.True(Math.Abs(cloudPose.BodyOffsetY) > Math.Abs(catIdle.BodyOffsetY));
    }

    [Fact]
    public void AnimationController_BlendsClipsSmoothlyGuardsBeatSpamAndStaysBounded()
    {
        RunOnSta(() =>
        {
            var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory());
            var rig = factory.BuildPetRig(new PetAppearance
            {
                SpeciesId = PetSpeciesIds.ChibiCat,
                HeadAccessoryId = "crown",
                AuraEffectId = "halo_ring"
            });

            var controller = new PetAnimationController();
            controller.AttachRig(rig, "#FF9EB5");
            Assert.Equal(PetMotionClipKind.Idle, controller.CurrentMotionClip);

            // 음악 입력 시작 -> 댄스 클립으로 부드럽게 진입, 다양한 dt에서 유한 값 및 안전 클램프 유지
            var activeClipsObserved = new HashSet<PetMotionClipKind>();
            var dtCycle = new[] { 0.016, 0.033, 0.025, 0.045 };
            for (var i = 0; i < 240; i++)
            {
                // 매 프레임 beat=true 연속 주입 시에도 최소 유지 시간(1.5s) 덕분에 과도한 클립 난사 억제
                controller.UpdateAudioSnapshot(
                    bass: 0.75f,
                    mid: (i % 60 < 30) ? 0.55f : 0.82f,
                    treble: (i % 40 < 20) ? 0.85f : 0.35f,
                    isBeat: true);

                controller.UpdateFrame(dtCycle[i % dtCycle.Length], 0.3, -0.2);
                activeClipsObserved.Add(controller.CurrentMotionClip);

                Assert.True(double.IsFinite(rig.BodyScale.ScaleY));
                Assert.InRange(rig.BodyScale.ScaleY, 0.80, 1.24);
                Assert.InRange(rig.BodyTranslate.OffsetY, -0.15, 0.45);
                Assert.InRange(rig.BodyRoll.Angle, -26.0, 26.0);
            }

            Assert.DoesNotContain(PetMotionClipKind.Idle, activeClipsObserved.Skip(1));
            Assert.True(activeClipsObserved.Count >= 2);

            // 음악 종료 -> Idle 모션으로 부드럽게 복귀
            for (var i = 0; i < 120; i++)
            {
                controller.UpdateAudioSnapshot(0f, 0f, 0f, isBeat: false);
                controller.UpdateFrame(0.033, 0, 0);
            }

            Assert.Equal(PetMotionClipKind.Idle, controller.CurrentMotionClip);
        });
    }

    [Fact]
    public void Grab_AlternatesPawKicksAndReturnsSleepingPetToRestOnRelease()
    {
        RunOnSta(() =>
        {
            var rig = new ProceduralPetMeshFactory().BuildPetRig(new PetAppearance { SpeciesId = PetSpeciesIds.FluffyPuppy }, PetRigEffects.None);
            var controller = new PetAnimationController();
            controller.SetSleeping(true);
            controller.AttachRig(rig, "#FF9EB5");

            controller.SetGrabbed(true);
            var leftLead = 0.0;
            var rightLead = 0.0;
            for (var i = 0; i < 40; i++)
            {
                controller.UpdateFrame(0.016, 0, 0);
                var pawDifference = rig.LeftPawTranslate!.OffsetY - rig.RightPawTranslate!.OffsetY;
                leftLead = Math.Max(leftLead, pawDifference);
                rightLead = Math.Max(rightLead, -pawDifference);
            }

            Assert.True(leftLead > 0.10 && rightLead > 0.10);
            Assert.True(controller.IsSleeping);
            Assert.False(controller.IsWakingUp);

            controller.SetGrabbed(false);
            for (var i = 0; i < 100; i++)
            {
                controller.UpdateFrame(0.033, 0, 0);
            }

            Assert.False(controller.HasInteractiveMotion);
            Assert.InRange(rig.EyeBlinkScale!.ScaleY, 0, 0.1);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GameReaction_SmilesOrCriesAndRestoresTheLatestSavedExpression(bool useExternalAssets)
    {
        RunOnSta(() =>
        {
            var procedural = new ProceduralPetMeshFactory();
            IPetMeshFactory factory = useExternalAssets ? new BlenderAssetPetMeshFactory(procedural) : procedural;
            var appearance = new PetAppearance { SpeciesId = PetSpeciesIds.MochiSpirit, ExpressionId = "sparkle" };
            var rig = factory.BuildPetRig(appearance, PetRigEffects.None);
            Assert.Equal(useExternalAssets, rig.IsExternalAsset);
            var controller = new PetAnimationController();
            controller.AttachRig(rig, appearance.AccentColorHex);

            controller.TriggerGameReaction(won: true);
            for (var i = 0; i < 10; i++)
                controller.UpdateFrame(0.033, 0, 0);
            Assert.Equal("happy", rig.ReactionExpression);
            Assert.True(rig.BodyTranslate.OffsetY > 0.16);
            Assert.False(controller.IsCrying);

            controller.TriggerGameReaction(won: false);
            for (var i = 0; i < 10; i++)
                controller.UpdateFrame(0.033, 0, 0);
            Assert.True(controller.IsCrying);
            Assert.True(rig.HeadPitch.Angle > 7);

            appearance.ExpressionId = "default";
            Assert.True(rig.TryUpdateAppearance(appearance));
            Assert.Equal("sleepy", rig.ReactionExpression);
            for (var i = 0; i < 70; i++)
                controller.UpdateFrame(0.033, 0, 0);

            Assert.Null(rig.ReactionExpression);
            Assert.False(controller.IsCrying);
            Assert.False(controller.HasInteractiveMotion);
            Assert.Equal(factory.BuildPetRig(appearance, PetRigEffects.None).TotalTriangleCount, rig.TotalTriangleCount);
        });
    }

    [Fact]
    public void MusicParticles_EnforcePoolLimitReactionBurstAndCleanFadeOut()
    {
        RunOnSta(() =>
        {
            var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory());
            var rig = factory.BuildPetRig(new PetAppearance
            {
                SpeciesId = PetSpeciesIds.MochiSpirit,
                AuraEffectId = "music_notes"
            });

            Assert.InRange(rig.MusicParticles.Count, 1, BlenderAssetPetMeshFactory.MaxPooledParticles);

            var controller = new PetAnimationController();
            controller.AttachRig(rig, "#7CC6FE");

            // 쓰다듬기 반응 트리거 -> 별 파티클 버스트 활성화 및 투명도 페이드 인 확인
            controller.TriggerPetReaction();
            controller.UpdateFrame(0.05, 0, 0);
            controller.UpdateFrame(0.05, 0, 0);

            Assert.Contains(rig.MusicParticles, p => p.Scale.ScaleX > 0.05 && p.TintBrush is { Color.A: > 0 });

            // 수명(0.95s) 이후 모든 파티클이 완전히 숨겨져(Scale=0, Alpha=0) 검은 테두리/잔상 없음
            for (var i = 0; i < 50; i++)
            {
                controller.UpdateFrame(0.033, 0, 0);
            }

            foreach (var p in rig.MusicParticles)
            {
                Assert.Equal(0.0, p.Scale.ScaleX);
                Assert.Equal((byte)0, p.TintBrush!.Color.A);
            }

            // 색상 업데이트만으로 파티클 기본 색상이 새 포인트 컬러(#6EE7B7 -> G가 가장 큼)로 동기화됨
            rig.UpdateColors("#FFF5F7", "#6EE7B7");
            controller.UpdateAccentColor("#6EE7B7");
            controller.TriggerPetReaction();
            controller.UpdateFrame(0.05, 0, 0);
            Assert.Contains(rig.MusicParticles, p =>
                p.Scale.ScaleX > 0.05 &&
                p.TintBrush is { Color.A: > 0 } b &&
                b.Color.G > b.Color.R);
        });
    }

    [Fact]
    public void RigDetach_AllowsGarbageCollectionAcrossRepeatedBuilds()
    {
        RunOnSta(() =>
        {
            var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory());
            var controller = new PetAnimationController();

            var weakRigs = BuildAttachDetachCycle(factory, controller);

            for (var i = 0; i < 4; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            // 이전 사이클에서 분리된 리그들이 GC로 정상 회수됨
            Assert.All(weakRigs, w => Assert.False(w.IsAlive));
        });

        [MethodImpl(MethodImplOptions.NoInlining)]
        static List<WeakReference> BuildAttachDetachCycle(IPetMeshFactory factory, PetAnimationController controller)
        {
            var refs = new List<WeakReference>();
            for (var i = 0; i < 8; i++)
            {
                var rig = factory.BuildPetRig(new PetAppearance
                {
                    SpeciesId = PetSpeciesIds.All[i % PetSpeciesIds.All.Count],
                    HeadAccessoryId = "crown",
                    AuraEffectId = "snowflakes"
                });
                controller.AttachRig(rig, "#FF9EB5");
                controller.UpdateFrame(0.016, 0.2, 0.1);
                refs.Add(new WeakReference(rig));
            }

            controller.DetachRig();
            return refs;
        }
    }

    [Fact]
    public void SleepAndWakeUpStartle_ClosesEyesBreathesSlowlyAndJumpsOnWake()
    {
        RunOnSta(() =>
        {
            var factory = new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory());
            var rig = factory.BuildPetRig(new PetAppearance
            {
                SpeciesId = PetSpeciesIds.MochiSpirit,
                AuraEffectId = "starlight"
            });

            var controller = new PetAnimationController();
            controller.AttachRig(rig, "#FF9EB5");

            // 5분 무입력 수면 진입 -> 눈 감김 유지 및 고개 숙인 호흡 자세 확인
            controller.SetSleeping(true);
            Assert.True(controller.IsSleeping);

            for (var i = 0; i < 45; i++)
            {
                controller.UpdateFrame(0.033, targetLookX: 0.9, targetLookY: -0.7);
            }

            Assert.NotNull(rig.EyeBlinkScale);
            Assert.InRange(rig.EyeBlinkScale!.ScaleY, 0.05, 0.18);
            Assert.True(rig.HeadPitch.Angle > 8.0);
            Assert.InRange(rig.BodyScale.ScaleY, 0.80, 1.24);
            Assert.InRange(rig.BodyTranslate.OffsetY, -0.15, 0.45);

            // 사용자 입력 재개 -> 수면 해제 + 깜짝 놀라며 깨는 점프/눈 뜨기/파티클 버스트
            controller.SetSleeping(false);
            Assert.False(controller.IsSleeping);
            Assert.True(controller.IsWakingUp);

            var maxJumpOffsetY = double.MinValue;
            for (var i = 0; i < 5; i++)
            {
                controller.UpdateFrame(0.025, 0, 0);
                maxJumpOffsetY = Math.Max(maxJumpOffsetY, rig.BodyTranslate.OffsetY);
            }

            Assert.Equal(1.0, rig.EyeBlinkScale.ScaleY);
            Assert.True(maxJumpOffsetY > 0.12);
            Assert.Contains(rig.MusicParticles, p => p.Scale.ScaleX > 0.05 && p.TintBrush is { Color.A: > 0 });

            // 약 1.2초 후 깜짝 기상 모션이 정상 종료되고 안정 범위로 안착
            for (var i = 0; i < 40; i++)
            {
                controller.UpdateFrame(0.033, 0, 0);
            }

            Assert.False(controller.IsWakingUp);
            Assert.InRange(rig.BodyScale.ScaleY, 0.80, 1.24);
            Assert.InRange(rig.BodyTranslate.OffsetY, -0.15, 0.45);
        });
    }

    [Fact]
    public void Win32IdleDuration_HandlesNormalAndTickCountWrapAround()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            Win32WindowHelper.ComputeIdleDurationFromTickDiff(350_000u, 50_000u));

        // 32-bit Environment.TickCount 49.7일 wrap-around 경계에서도 5분(300,000ms) 경과를 정확히 계산
        const uint beforeWrap = uint.MaxValue - 199_999u;
        const uint afterWrap = 100_000u;
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            Win32WindowHelper.ComputeIdleDurationFromTickDiff(afterWrap, beforeWrap));
    }

    [Fact]
    public Task SleepZzzOverlay_AnimatesOnlyWhileItsHostIsVisible()
    {
        return WpfTestHost.RunAsync(async () =>
        {
            var overlay = new LabPet.Client.Views.SleepZzzOverlay();
            var host = new Grid { Children = { overlay } };
            var window = new Window { Content = host, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                var letters = overlay.Children.Cast<UIElement>().Select(Assert.IsType<TextBlock>).ToArray();
                Assert.Equal(new[] { "z", "Z", "Z" }, letters.Select(letter => letter.Text));
                Assert.Equal(new[] { 13d, 16d, 19d }, letters.Select(letter => letter.FontSize));
                Assert.Null(overlay.Background);
                Assert.All(letters, letter => Assert.Null(letter.Background));
                Assert.False(overlay.IsAnimating);
                window.Show();
                await WpfTestHost.FlushAsync();
                Assert.True(overlay.IsAnimating);

                var timer = System.Diagnostics.Stopwatch.StartNew();
                var maxOpacity = 0d;
                var minY = 0d;
                var sawVisibleRise = false;
                while (timer.Elapsed < TimeSpan.FromSeconds(3))
                {
                    var opacity = letters[0].Opacity;
                    var y = letters[0].RenderTransform.Value.OffsetY;
                    maxOpacity = Math.Max(maxOpacity, opacity);
                    minY = Math.Min(minY, y);
                    sawVisibleRise = opacity >= 0.5 && y < 0;
                    if (sawVisibleRise) break;
                    await Task.Delay(16);
                }
                Assert.True(sawVisibleRise, $"진행한 수면 글자 값 (투명도 {maxOpacity:F2}, 상승 {minY:F2})");

                host.Visibility = Visibility.Collapsed;
                Assert.False(overlay.IsAnimating);
                Assert.False(overlay.IsVisible);
                host.Visibility = Visibility.Visible;
                Assert.True(overlay.IsAnimating);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public Task MiniSleepZzzIndicator_StartsAndStopsWithFixedSlotSize()
    {
        return WpfTestHost.RunAsync(async () =>
        {
            var indicator = new LabPet.Client.Views.MiniSleepZzzIndicator { Visibility = Visibility.Collapsed };
            Assert.Equal(20.0, indicator.Width);
            Assert.Equal(16.0, indicator.Height);
            Assert.Empty(indicator.Children.OfType<Border>());
            Assert.Equal(new[] { "z", "z", "z" }, indicator.Children.OfType<TextBlock>().Select(letter => letter.Text));
            var window = new Window { Content = indicator, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                await WpfTestHost.FlushAsync();
                Assert.False(indicator.IsAnimating);

                indicator.Visibility = Visibility.Visible;
                await WpfTestHost.FlushAsync();
                Assert.True(indicator.IsAnimating);

                indicator.Visibility = Visibility.Collapsed;
                Assert.False(indicator.IsAnimating);
            }
            finally
            {
                window.Close();
            }
        });
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
