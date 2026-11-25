using System.Windows;
using LabPet.Client.Views;
using Xunit;

namespace LabPet.Tests;

public sealed class VisitorTailTargetTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void BodyStaysInsideButTailReachesActualHeadOutsideWorkArea(double scale)
    {
        var dpi = new DpiScale(scale, scale);
        foreach (var origin in new[] { new Point(0, 0), new Point(-1920, -300) })
        {
            var work = new Rect(origin, new Size(1920, 1040));
            foreach (var x in new[] { work.Left - 40, work.Left, work.Right, work.Right + 40 })
            foreach (var y in new[] { work.Top - 40, work.Top, work.Bottom, work.Bottom + 40 })
            {
                var head = new Point(x, y);
                var placement = VisitorBubblePlacementCalculator.CalculateBubblePlacement(
                    head, new Size(280 * scale, 160 * scale), work, dpi);
                Assert.False(placement.Body.IsEmpty);
                Assert.True(work.Contains(placement.Body));
                Assert.Equal(head, placement.Tip);
                Assert.InRange(placement.Scale, 0.000001, 1);
                var tail = VisitorBubblePlacementCalculator.CalculateTailPlacement(placement, placement.Body, dpi);
                Assert.Equal(head, tail.Tip);
                Assert.True(placement.Body.Contains(tail.BaseLeft));
                Assert.True(placement.Body.Contains(tail.BaseRight));
                Assert.True(double.IsFinite(tail.Tip.X) && double.IsFinite(tail.Tip.Y));
            }
        }
    }

    [Fact]
    public void InvalidHeadProducesEmptyBodyAndFiniteFallback()
    {
        var placement = VisitorBubblePlacementCalculator.CalculateBubblePlacement(
            new Point(double.NaN, double.PositiveInfinity), new Size(280, 160),
            new Rect(0, 0, 1920, 1040), new DpiScale(1, 1));
        Assert.True(placement.Body.IsEmpty);
        Assert.Equal(0, placement.Scale);
        Assert.True(double.IsFinite(placement.Tip.X) && double.IsFinite(placement.Tip.Y));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void SurfaceBounds_IncludeUnclippedTailOutsideWorkAreaAcrossDpisAndOrigins(double scale)
    {
        var dpi = new DpiScale(scale, scale);
        foreach (var origin in new[] { new Point(0, 0), new Point(-1920, -300) })
        {
            var work = new Rect(origin, new Size(1920, 1040));
            var outsideHeads = new[]
            {
                new Point(work.Left - 60 * scale, work.Top + 400),
                new Point(work.Right + 60 * scale, work.Top + 400),
                new Point(work.Left + 500, work.Top - 50 * scale),
                new Point(work.Left + 500, work.Bottom + 50 * scale),
                new Point(work.Left - 50 * scale, work.Top - 45 * scale),
                new Point(work.Right + 50 * scale, work.Bottom + 45 * scale)
            };

            foreach (var head in outsideHeads)
            {
                var placement = VisitorBubblePlacementCalculator.CalculateBubblePlacement(
                    head, new Size(240 * scale, 120 * scale), work, dpi);
                Assert.False(placement.Body.IsEmpty);
                Assert.True(work.Contains(placement.Body));
                Assert.Equal(head, placement.Tip);

                var tail = VisitorBubblePlacementCalculator.CalculateTailPlacement(placement, placement.Body, dpi);
                var surface = VisitorBubblePlacementCalculator.CalculateBubbleSurfaceBounds(placement, tail, work, dpi);

                Assert.False(surface.IsEmpty);
                Assert.True(surface.Contains(placement.Body));
                Assert.True(surface.Contains(tail.BaseLeft));
                Assert.True(surface.Contains(tail.BaseRight));
                Assert.True(surface.Contains(tail.Tip));
                Assert.False(work.Contains(surface));

                // 팝업 로컬 DIP 변환 결과가 표면 영역(0..Width/dpi, 0..Height/dpi) 안에서 잘리지 않아야 한다.
                var localTip = new Point((tail.Tip.X - surface.Left) / scale, (tail.Tip.Y - surface.Top) / scale);
                Assert.InRange(localTip.X, 0, surface.Width / scale);
                Assert.InRange(localTip.Y, 0, surface.Height / scale);
            }
        }
    }

    [Fact]
    public void ExtremeAndOverflowInputs_DoNotThrowOrAllocateOversizedSurface()
    {
        var work = new Rect(-1920, -300, 1920, 1040);
        var dpis = new[]
        {
            new DpiScale(1.0, 1.0),
            new DpiScale(2.0, 2.0),
            new DpiScale(0, 0),
            new DpiScale(-1.5, double.NaN),
            new DpiScale(double.PositiveInfinity, 1.25)
        };
        var extremeHeads = new[]
        {
            new Point(1e12, -1e12),
            new Point(-1e12, 1e12),
            new Point(double.MaxValue, double.MinValue),
            new Point(double.NaN, double.NegativeInfinity)
        };

        foreach (var dpi in dpis)
        {
            foreach (var head in extremeHeads)
            {
                var placement = VisitorBubblePlacementCalculator.CalculateBubblePlacement(head, new Size(220, 100), work, dpi);
                Assert.True(double.IsFinite(placement.Tip.X) && double.IsFinite(placement.Tip.Y));
                Assert.True(double.IsFinite(placement.Scale) && placement.Scale >= 0);

                var tail = VisitorBubblePlacementCalculator.CalculateTailPlacement(placement, placement.Body, dpi);
                var surface = VisitorBubblePlacementCalculator.CalculateBubbleSurfaceBounds(placement, tail, work, dpi);
                if (!surface.IsEmpty)
                {
                    Assert.True(double.IsFinite(surface.Left) && double.IsFinite(surface.Top));
                    Assert.True(double.IsFinite(surface.Width) && double.IsFinite(surface.Height));
                    Assert.InRange(surface.Width, 1, VisitorBubblePlacementCalculator.MaxBubbleSurfacePhysicalExtent + 64);
                    Assert.InRange(surface.Height, 1, VisitorBubblePlacementCalculator.MaxBubbleSurfacePhysicalExtent + 64);
                }
            }
        }

        // 산술 오버플로 경계에서의 SafeClamp 방어 확인
        var clamped = VisitorBubblePlacementCalculator.SafeClamp(0, double.MaxValue, double.MaxValue / 2);
        Assert.True(double.IsFinite(clamped));
    }
}
