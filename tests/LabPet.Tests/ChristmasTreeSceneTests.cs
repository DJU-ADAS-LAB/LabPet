using System;
using LabPet.Client.Rendering.Terminal3D;
using LabPet.Client.SystemIntegration;
using Xunit;

namespace LabPet.Tests;

public sealed class ChristmasTreeSceneTests
{
    [Fact]
    public void Timeline_UsesElapsedTimeAndDoesNotRepeatIntroduction()
    {
        var singleStep = new ChristmasTreeScene();
        var manySteps = new ChristmasTreeScene();
        singleStep.Update(10);
        for (var i = 0; i < 600; i++) manySteps.Update(1.0 / 60);
        Assert.Equal(10, singleStep.ElapsedSeconds, 8);
        Assert.InRange(Math.Abs(singleStep.RotationRadians - manySteps.RotationRadians), 0, 0.0001);
        singleStep.Update(120);
        Assert.Equal(130, singleStep.ElapsedSeconds);
        singleStep.RestartIntroduction();
        Assert.Equal(0, singleStep.ElapsedSeconds);
        Assert.Equal(0, singleStep.RotationRadians);
    }

    [Fact]
    public void Pause_StopsOnlyRotationAndPreservesTimeline()
    {
        var scene = new ChristmasTreeScene();
        scene.Update(6);
        var rotation = scene.RotationRadians;
        scene.ToggleRotation();
        scene.Update(3);
        Assert.Equal(rotation, scene.RotationRadians);
        Assert.Equal(9, scene.ElapsedSeconds);
        scene.ToggleRotation();
        scene.Update(1);
        Assert.NotEqual(rotation, scene.RotationRadians);
        Assert.Throws<ArgumentOutOfRangeException>(() => scene.Update(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => scene.Update(-1));
    }

    [Fact]
    public void Geometry_HasAllMaterialsAndFiniteUnitNormals()
    {
        var geometry = new ChristmasTreeGeometry();
        var found = new bool[Enum.GetValues<ChristmasMaterial>().Length];
        foreach (ref readonly var sample in geometry.Samples)
        {
            found[(int)sample.Material] = true;
            Assert.True(float.IsFinite(sample.Position.X));
            Assert.True(float.IsFinite(sample.Position.Y));
            Assert.True(float.IsFinite(sample.Position.Z));
            Assert.InRange(sample.Normal.Length(), 0.999f, 1.001f);
        }
        Assert.All(found, present => Assert.True(present));
    }

    [Fact]
    public void Render_ProducesPrintableAsciiSceneAcrossIntroductionMilestonesAndClearsOnRestart()
    {
        var scene = new ChristmasTreeScene();
        var particles = new ChristmasParticleField();
        var surface = new AsciiDepthSurface();
        surface.Resize(164, 43);
        var projector = new TerminalPointProjector(surface);

        // 0초: 빈 장면
        scene.Render(surface, projector, 0.5f);
        Assert.Equal(0, scene.SubmittedSamples);
        Assert.Equal(0, surface.OccupiedCells);

        // 0.8초: 지면만 등장
        scene.Update(0.8);
        scene.Render(surface, projector, 0.5f);
        var groundSamples = scene.SubmittedSamples;
        Assert.True(groundSamples > 0);
        Assert.Equal(0f, scene.RotationRadians);

        // 2.5초: 트리 성장 중
        scene.Update(1.7);
        scene.Render(surface, projector, 0.5f);
        Assert.True(scene.SubmittedSamples > groundSamples);

        // 6.0초: 별 점등 및 완성 장면 + 눈/불꽃/배경 별
        scene.Update(3.5);
        particles.TriggerFirework(5.2);
        scene.Render(surface, projector, 0.5f);
        particles.Render(projector, scene.ElapsedSeconds, scene.Zoom, 0.5f);

        Assert.True(scene.SubmittedSamples > 30_000);
        Assert.True(surface.OccupiedCells > 800);
        Assert.True(particles.ActiveSparks > 0);
        Assert.NotEqual(0f, scene.RotationRadians);

        foreach (var glyph in surface.Glyphs)
            Assert.True(AsciiDepthSurface.IsPrintableAscii(glyph));

        // 4개 조명 모드 순환 및 재시작 확인
        for (var i = 0; i < ChristmasTreeScene.LightModeCount; i++)
        {
            scene.CycleLighting();
            Assert.False(string.IsNullOrWhiteSpace(scene.LightModeLabel));
        }

        scene.RestartIntroduction();
        scene.Render(surface, projector, 0.5f);
        Assert.Equal(0, scene.SubmittedSamples);
        Assert.Equal(0, surface.OccupiedCells);
    }

    [Fact]
    public void FormatElapsedSeconds_ProducesTwoDecimalPlaces()
    {
        Span<char> buffer = stackalloc char[16];
        var len = ChristmasTreeConsoleRunner.FormatElapsedSeconds(12.3456, buffer);
        Assert.Equal("12.35s", buffer[..len].ToString());
    }
}
