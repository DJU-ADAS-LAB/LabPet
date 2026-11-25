using System.Windows;
using LabPet.Client.Rendering;
using Xunit;

namespace LabPet.Tests;

public sealed class SnowballSpawnGeometryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void EntirePerimeterKeepsHalfAreaAndFullyHides(double dpi)
    {
        var monitor = new Rect(-1920 * dpi, -200 * dpi, 1920 * dpi, 1080 * dpi);
        var pet = new Size(214 * dpi, 224 * dpi);
        var edges = new HashSet<Point>();
        for (var i = 0; i <= 6000; i++)
        {
            Assert.True(SnowballSpawnGeometry.TryCreate(monitor, pet, i / 6000d, out var g));
            edges.Add(g.Edge);
            Assert.InRange(g.Inward.Length, 0.999999, 1.000001);
            Assert.True(g.Edge.X == monitor.Left || g.Edge.X == monitor.Right
                || g.Edge.Y == monitor.Top || g.Edge.Y == monitor.Bottom);
            var clipped = Rect.Intersect(monitor, g.VisibleBounds);
            Assert.InRange(clipped.Width * clipped.Height / (pet.Width * pet.Height), 0.499999, 0.500001);
            Assert.True(Rect.Intersect(monitor, g.HiddenBounds).IsEmpty);
            Assert.InRange((g.BoundsAt(1).TopLeft - g.VisibleBounds.TopLeft).Length, 0, 0.000001);
            Assert.Equal(g.HiddenBounds, g.BoundsAt(double.NaN));
            Assert.Equal(pet, g.BoundsAt(0.5).Size);
        }
        Assert.Equal(6000, edges.Count);
    }

    [Fact]
    public void CornersAndEdgeLengthsAreNotFixedSlots()
    {
        var monitor = new Rect(0, 0, 1920, 1080);
        var distances = new[] { 0d, 1920d, 3000d, 4920d, 6000d };
        var corners = new[] { monitor.TopLeft, monitor.TopRight, monitor.BottomRight, monitor.BottomLeft, monitor.TopLeft };
        for (var i = 0; i < distances.Length; i++)
        {
            Assert.True(SnowballSpawnGeometry.TryCreate(monitor, new Size(214, 224), distances[i] / 6000, out var g));
            Assert.Equal(corners[i], g.Edge);
            Assert.InRange(Math.Abs(g.Inward.X), 0.707106, 0.707108);
            Assert.InRange(Math.Abs(g.Inward.Y), 0.707106, 0.707108);
        }
        Assert.False(SnowballSpawnGeometry.TryCreate(Rect.Empty, new Size(10, 10), 0, out _));
        foreach (var unit in new[] { -1d, 1.1, double.NaN, double.PositiveInfinity })
            Assert.False(SnowballSpawnGeometry.TryCreate(monitor, new Size(10, 10), unit, out _));
        Assert.False(SnowballSpawnGeometry.TryCreate(monitor, new Size(0, 10), 0, out _));
    }
}
