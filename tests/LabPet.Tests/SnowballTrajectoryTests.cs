using System.Windows;
using LabPet.Client.Rendering;
using Xunit;

namespace LabPet.Tests;

public sealed class SnowballTrajectoryTests
{
    [Theory]
    [InlineData(0.5)]
    [InlineData(0.7)]
    [InlineData(0.9)]
    public void StationaryTargetFollowsParabolaAndCompletesOnce(double duration)
    {
        var flight = new SnowballTrajectory(new Point(-300, 200), new Point(500, 400), duration, 120);
        Assert.False(flight.Advance(duration / 2));
        Assert.Equal(100, flight.Position.X, 8);
        Assert.Equal(180, flight.Position.Y, 8);
        Assert.True(flight.Advance(duration));
        Assert.Equal(new Point(500, 400), flight.Position);
        Assert.True(flight.IsComplete);
        Assert.False(flight.Advance(1));
        Assert.False(flight.TryRetarget(default));
    }

    [Fact]
    public void RetargetPreservesPositionVelocityAndArrivalTime()
    {
        var flight = new SnowballTrajectory(default, new Point(500, 200), 0.7, 100);
        flight.Advance(0.2);
        var position = flight.Position;
        var velocity = flight.Velocity;
        var remaining = flight.RemainingSeconds;
        var target = new Point(550, 220);
        Assert.True(flight.TryRetarget(target));
        Assert.Equal(position, flight.Position);
        Assert.Equal(velocity, flight.Velocity);
        Assert.Equal(remaining, flight.RemainingSeconds, 10);
        const double dt = 0.000001;
        Assert.False(flight.Advance(dt));
        Assert.InRange(((flight.Position - position) / dt - velocity).Length, 0, 0.1);
        Assert.True(flight.Advance(remaining));
        Assert.Equal(target, flight.Position);
    }

    [Fact]
    public void MovingTargetUpdatesDoNotExtendFlightOrAffectOtherSnowballs()
    {
        var flight = new SnowballTrajectory(default, new Point(500, 100), 0.7, 80);
        var independent = new SnowballTrajectory(default, new Point(-500, 100), 0.7, 80);
        var completions = 0;
        for (var i = 1; i <= 80; i++)
        {
            flight.TryRetarget(new Point(500 + i, 100));
            if (flight.Advance(0.01)) completions++;
            Assert.True(double.IsFinite(flight.Position.X));
            Assert.True(double.IsFinite(flight.Position.Y));
            Assert.True(double.IsFinite(flight.Velocity.Length));
        }
        Assert.Equal(1, completions);
        Assert.True(flight.IsComplete);
        Assert.Equal(default(Point), independent.Position);
        Assert.Equal(0.7, independent.RemainingSeconds);
    }

    [Fact]
    public void InvalidUpdatesLeaveFlightUnchanged()
    {
        var flight = new SnowballTrajectory(default, new Point(500, 100), 0.7, 80);
        foreach (var delta in new[] { double.NaN, double.PositiveInfinity, -1, 0 })
            Assert.False(flight.Advance(delta));
        Assert.False(flight.TryRetarget(new Point(double.NaN, 0)));
        Assert.False(flight.TryRetarget(new Point(0, double.PositiveInfinity)));
        Assert.Equal(default(Point), flight.Position);
        Assert.Equal(0.7, flight.RemainingSeconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnowballTrajectory(default, default, double.NaN, 80));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnowballTrajectory(default, default, 0.7, -1));
        flight.Advance(0.65);
        Assert.False(flight.TryRetarget(new Point(600, 200)));
        Assert.True(flight.Advance(0.1));
        Assert.Equal(new Point(500, 100), flight.Position);
    }
}
