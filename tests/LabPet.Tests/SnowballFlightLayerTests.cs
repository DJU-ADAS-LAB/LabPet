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
