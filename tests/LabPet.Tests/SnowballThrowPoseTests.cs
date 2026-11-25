using LabPet.Client.Rendering;
using Xunit;

namespace LabPet.Tests;

public sealed class SnowballThrowPoseTests
{
    [Fact]
    public void InvalidOrFinishedTimeReturnsNeutralPose()
    {
        foreach (var time in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, SnowballThrowPose.DurationSeconds, 100 })
            Assert.Equal(default(SnowballThrowPose), SnowballThrowPose.Evaluate(time));
    }

    [Fact]
    public void PoseRemainsFiniteAndContinuousAtPhaseBoundaries()
    {
        for (var i = 0; i <= 1000; i++)
        {
            var pose = SnowballThrowPose.Evaluate(i * SnowballThrowPose.DurationSeconds / 1000);
            Assert.InRange(pose.ArmPitch, -48, 60);
            Assert.InRange(pose.ArmRoll, -24, 12);
            Assert.InRange(pose.Weight, 0, 1);
        }
        foreach (var boundary in new[] { 0.0, SnowballThrowPose.PrepareSeconds, SnowballThrowPose.PrepareSeconds + SnowballThrowPose.SwingSeconds, SnowballThrowPose.DurationSeconds })
        {
            var before = SnowballThrowPose.Evaluate(boundary - 0.000001);
            var after = SnowballThrowPose.Evaluate(boundary + 0.000001);
            Assert.InRange(Math.Abs(before.ArmPitch - after.ArmPitch), 0, 0.001);
            Assert.InRange(Math.Abs(before.ArmRoll - after.ArmRoll), 0, 0.001);
            Assert.InRange(Math.Abs(before.Weight - after.Weight), 0, 0.001);
        }
    }

    [Fact]
    public void AdvancingTimeReleasesOnceEvenWhenFrameSkipsSwing()
    {
        var times = new[] { 0.0, 0.2, 0.4, 0.8, 1.1 };
        var releases = 0;
        for (var i = 1; i < times.Length; i++)
            if (SnowballThrowPose.CrossedRelease(times[i - 1], times[i])) releases++;
        Assert.Equal(1, releases);
        var release = SnowballThrowPose.ReleaseSeconds;
        Assert.True(SnowballThrowPose.CrossedRelease(0, release));
        Assert.False(SnowballThrowPose.CrossedRelease(release, release + 0.1));
        Assert.False(SnowballThrowPose.CrossedRelease(double.NaN, release));
        Assert.False(SnowballThrowPose.CrossedRelease(0, double.PositiveInfinity));
        Assert.False(SnowballThrowPose.CrossedRelease(0.8, 0.2));
    }
}
