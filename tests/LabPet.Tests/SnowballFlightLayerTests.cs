using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using LabPet.Client.Rendering;
using Xunit;

namespace LabPet.Tests;

public sealed class SnowballFlightLayerTests
{
    [Fact]
    public void ConcurrentFlightsImpactOnceAndOnlyOwnedVisualsAreRemoved() => RunSta(() =>
    {
        var scene = new Canvas();
        var unrelated = new Border();
        scene.Children.Add(unrelated);
        using var layer = new SnowballFlightLayer(scene);
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
            Assert.True(layer.TryLaunch(Guid.NewGuid(), id, new Point(10, 10), new Point(80, 80), 0.6, 20));
        Assert.Equal(6, layer.Count);
        Assert.Empty(layer.Advance(0.2, new Point(90, 70)));
        var impacts = layer.Advance(0.4, new Point(90, 70));
        Assert.Equal(ids.OrderBy(x => x), impacts.OrderBy(x => x));
        Assert.Equal(6, layer.Count);
        Assert.Empty(layer.Advance(1, new Point(90, 70)));
        Assert.Equal(0, layer.Count);
        Assert.Same(unrelated, Assert.Single(scene.Children.Cast<UIElement>()));
    });

    [Fact]
    public void SenderCancellationDoesNotCancelOtherSendersAndDisposeIsFinal() => RunSta(() =>
    {
        var scene = new Canvas();
        using var layer = new SnowballFlightLayer(scene);
        var sender = Guid.NewGuid();
        var cancelled = Guid.NewGuid();
        var remaining = Guid.NewGuid();
        Assert.True(layer.TryLaunch(sender, cancelled, new Point(10, 10), new Point(80, 80), 0.6, 20));
        Assert.False(layer.TryLaunch(sender, cancelled, new Point(10, 10), new Point(80, 80), 0.6, 20));
        Assert.True(layer.TryLaunch(Guid.NewGuid(), remaining, new Point(10, 10), new Point(80, 80), 0.6, 20));
        layer.CancelSender(sender);
        layer.CancelSender(sender);
        Assert.Equal(1, layer.Count);
        Assert.Equal(remaining, Assert.Single(layer.Advance(0.7, new Point(80, 80))));
        layer.Dispose();
        layer.Dispose();
        Assert.Empty(scene.Children);
        Assert.Empty(layer.Advance(1, new Point(80, 80)));
        Assert.False(layer.TryLaunch(sender, Guid.NewGuid(), new Point(10, 10), new Point(80, 80), 0.6, 20));
    });

    [Fact]
    public void OutgoingFlightsTrackSeparateTargetsAndCrossClippedSurfacesWhileIncomingIsIndependent() => RunSta(() =>
    {
        var primary = new Canvas { ClipToBounds = true };
        var secondary = new Canvas { ClipToBounds = true };
        using var layer = new SnowballFlightLayer(primary);
        SnowballFlightPlacement? Place(Point point) => point.X < 0
            ? new(primary, new((point.X + 200) / 1.25, point.Y / 1.25))
            : new(secondary, new(point.X / 1.75, point.Y / 1.75));
        Point? moving = new Point(120, 80);
        Point? disappearing = new Point(-150, 90);
        var hits = new List<string>();
        var incoming = Guid.NewGuid();
        Assert.True(layer.TryLaunch(Guid.NewGuid(), incoming, new(10, 20), new(80, 100), 0.6, 20));
        Assert.True(layer.TryLaunchPhysical(Guid.NewGuid(), new(-100, 100), moving.Value, 0.6, 20,
            Place, () => moving, () => hits.Add("이동"), null));
        Assert.True(layer.TryLaunchPhysical(Guid.NewGuid(), new(-100, 100), disappearing.Value, 0.6, 20,
            Place, () => disappearing, () => hits.Add("사라짐"), null));
        var escaped = false;
        Assert.True(layer.TryLaunchPhysical(Guid.NewGuid(), new(-100, 100), new(-240, 90), 0.6, 20,
            point => new(primary, new(point.X + 200, point.Y)), null, () => hits.Add("화면 밖"),
            point => escaped |= point.X < -208));
        layer.AdvancePhysical(0.2);
        moving = new Point(175, 110);
        disappearing = null;
        layer.AdvancePhysical(0.2);
        var migrated = Assert.Single(secondary.Children.OfType<SnowballFlightVisual>());
        layer.AdvancePhysical(0.2);
        Assert.Equal(moving, migrated.Position);
        Assert.Equal(new[] { "이동" }, hits);
        Assert.True(escaped);
        Assert.Equal(incoming, Assert.Single(layer.Advance(0.6, new(100, 110))));
        layer.AdvancePhysical(1);
        layer.Advance(1, new(100, 110));
        Assert.Empty(primary.Children);
        Assert.Empty(secondary.Children);
        Assert.Equal(0, layer.Count);
    });

    [Fact]
    public void DisappearingTargetRedirectsFromCurrentPositionAndReleasesHoldOnce() => RunSta(() =>
    {
        var scene = new Canvas();
        using var layer = new SnowballFlightLayer(scene);
        Point? target = new Point(-10, 90);
        var monitor = new Rect(-460, 0, 460, 250);
        var hand = new Point(-250, 90);
        var last = target.Value;
        var released = 0;
        var hit = 0;
        Assert.True(layer.TryLaunchPhysical(Guid.NewGuid(), hand, last, 0.6, 12,
            point => new(scene, new(point.X - monitor.Left, point.Y)), () => target, () => hit++,
            point => point.X > monitor.Right + 8,
            () => (SnowballSpawnGeometry.BeyondMonitor(monitor, hand, last, 20), 0.4), () => released++));
        var ball = Assert.Single(scene.Children.OfType<SnowballFlightVisual>());
        layer.AdvancePhysical(0.26);
        var before = ball.Position;
        target = null;
        layer.AdvancePhysical(0.01);
        Assert.InRange((ball.Position - before).Length, 0.01, 15);
        Assert.Equal(0, released);
        layer.AdvancePhysical(0.4);
        Assert.True(ball.Position.X > monitor.Right + 8);
        Assert.Equal(0, hit);
        Assert.Equal(1, released);
        Assert.Equal(0, layer.Count);
        Assert.Empty(scene.Children);
    });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Flight layer test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
