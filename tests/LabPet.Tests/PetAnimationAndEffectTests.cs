using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using LabPet.Client.Interop;
using LabPet.Client.Rendering;
using LabPet.Shared.Models;
using Xunit;

namespace LabPet.Tests;

[Collection("WpfUi")]
public sealed class PetAnimationAndEffectTests
{
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
    public void SleepZzzOverlay_StartsAndStopsFloatingAnimationWithVisibility()
    {
        RunOnSta(() =>
        {
            var overlay = new LabPet.Client.Views.SleepZzzOverlay
            {
                Visibility = System.Windows.Visibility.Collapsed
            };

            Assert.Equal(3, overlay.BubbleCount);
            Assert.False(overlay.IsAnimating);

            overlay.Visibility = System.Windows.Visibility.Visible;
            Assert.True(overlay.IsAnimating);

            overlay.FlipHorizontal = true;
            Assert.True(overlay.IsAnimating);

            overlay.Visibility = System.Windows.Visibility.Collapsed;
            Assert.False(overlay.IsAnimating);
        });
    }

    [Fact]
    public void MiniSleepZzzIndicator_StartsAndStopsDiagonalAnimationWithFixedSlotSize()
    {
        RunOnSta(() =>
        {
            var indicator = new LabPet.Client.Views.MiniSleepZzzIndicator
            {
                Visibility = System.Windows.Visibility.Collapsed
            };

            Assert.Equal(14.0, indicator.Width);
            Assert.Equal(14.0, indicator.Height);
            Assert.Equal(3, indicator.LetterCount);
            Assert.False(indicator.IsAnimating);

            indicator.Visibility = System.Windows.Visibility.Visible;
            Assert.True(indicator.IsAnimating);

            indicator.Visibility = System.Windows.Visibility.Collapsed;
            Assert.False(indicator.IsAnimating);
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
