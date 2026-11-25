using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using LabPet.Client.Interop;
using LabPet.Client.Views;
using Xunit;

namespace LabPet.Tests;

public sealed class SnowballOverlayWindowTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    [InlineData(0.707, 0.707)]
    [InlineData(-0.707, 0.707)]
    [InlineData(0.707, -0.707)]
    [InlineData(-0.707, -0.707)]
    public void PeekChangesProjectedHandAndCancellationRestoresIt(double x, double y) => RunSta(() =>
    {
        var factory = new LabPet.Client.Rendering.ProceduralPetMeshFactory();
        var appearance = new LabPet.Shared.Models.PetAppearance();
        using var pet = new LabPet.Client.Rendering.SnowballPetVisual(factory, appearance, 1);
        using var baseline = new LabPet.Client.Rendering.SnowballPetVisual(factory, appearance, 1);
        foreach (var visual in new[] { pet, baseline })
        {
            visual.Measure(new Size(visual.Width, visual.Height));
            visual.Arrange(new Rect(0, 0, visual.Width, visual.Height));
        }
        pet.Animator.SetSnowballPeek(x, y, 1);
        pet.Advance(0.01, 0, 0);
        baseline.Advance(0.01, 0, 0);
        Assert.True(pet.TryGetHandDip(out var hand));
        Assert.True(baseline.TryGetHandDip(out var neutral));
        Assert.True(double.IsFinite(hand.X) && double.IsFinite(hand.Y));
        Assert.True((hand - neutral).Length > 0.01);
        pet.Animator.CancelSnowballInteraction();
        pet.Advance(0.01, 0, 0);
        baseline.Advance(0.01, 0, 0);
        Assert.True(pet.TryGetHandDip(out hand));
        Assert.True(baseline.TryGetHandDip(out neutral));
        Assert.InRange((hand - neutral).Length, 0, 0.0001);
    });

    private sealed class SpawnRandom(params double[] values) : Random
    {
        internal int Calls { get; private set; }
        public override double NextDouble() => values[Math.Min(Calls++, values.Length - 1)];
    }

    [Fact]
    public void SpawnSelectionRejectsOverlapAndTerminatesWhenCrowded()
    {
        var monitor = new Rect(-1920, -200, 1920, 1080);
        var pet = new Size(160, 180);
        Assert.True(LabPet.Client.Rendering.SnowballSpawnGeometry.TryCreate(monitor, pet, 0.1, out var first));
        var random = new SpawnRandom(0.1, 0.6);
        Assert.True(LabPet.Client.Rendering.SnowballSpawnGeometry.TrySelect(
            monitor, pet, new[] { first.VisibleBounds }, random, out var selected));
        Assert.Equal(2, random.Calls);
        Assert.False(first.VisibleBounds.IntersectsWith(selected.VisibleBounds));
        random = new SpawnRandom(0.1);
        Assert.True(LabPet.Client.Rendering.SnowballSpawnGeometry.TrySelect(
            monitor, pet, new[] { monitor }, random, out selected));
        Assert.Equal(48, random.Calls);
        Assert.True(double.IsFinite(selected.Inward.X));
        random = new SpawnRandom(0.1);
        Assert.False(LabPet.Client.Rendering.SnowballSpawnGeometry.TrySelect(
            monitor, new Size(0, 10), Array.Empty<Rect>(), random, out _));
        Assert.Equal(0, random.Calls);
    }

    [Fact]
    public void SpawnSelectionPreservesPerimeterLengthDistributionAndContinuousCoordinates()
    {
        var monitor = new Rect(-2000, -300, 1800, 900);
        var random = new Random(7321);
        var counts = new int[4];
        var positions = new HashSet<Point>();
        for (var i = 0; i < 12000; i++)
        {
            Assert.True(LabPet.Client.Rendering.SnowballSpawnGeometry.TrySelect(
                monitor, new Size(100, 120), Array.Empty<Rect>(), random, out var spawn));
            positions.Add(spawn.Edge);
            var side = spawn.Edge.Y == monitor.Top ? 0 : spawn.Edge.X == monitor.Right ? 1
                : spawn.Edge.Y == monitor.Bottom ? 2 : 3;
            counts[side]++;
        }
        Assert.Equal(12000, positions.Count);
        Assert.InRange(counts[0], 3700, 4300);
        Assert.InRange(counts[2], 3700, 4300);
        Assert.InRange(counts[1], 1700, 2300);
        Assert.InRange(counts[3], 1700, 2300);
    }

    [Fact]
    public void ShownWindowClipsPhysicalBoundsConvertsCoordinatesAndReusesHandle() => RunSta(() =>
    {
        using var overlay = new SnowballOverlayWindow();
        var handle = new WindowInteropHelper(overlay).EnsureHandle();
        Assert.True(Win32WindowHelper.TryGetNearestMonitorBounds(overlay, out var monitor));
        var requested = new Rect(monitor.Left - 40, monitor.Top - 30, 240, 180);
        Assert.True(overlay.TryShowWithinMonitor(monitor, requested));
        overlay.UpdateLayout();
        Assert.True(Win32WindowHelper.TryGetWindowPhysicalBounds(overlay, out var actual));
        Assert.Equal(Rect.Intersect(monitor, requested), actual);
        Assert.True(monitor.Contains(actual));
        var physical = new Point(actual.Left + 25, actual.Top + 35);
        Assert.True(overlay.TryScreenPhysicalToSceneDip(physical, out var local));
        var roundTrip = overlay.Scene.PointToScreen(local);
        Assert.InRange((roundTrip - physical).Length, 0, 0.01);
        Assert.False(overlay.IsActive);
        Assert.False(overlay.ShowInTaskbar);
        Assert.False(overlay.ShowActivated);
        Assert.True(overlay.Scene.ClipToBounds);
        var moved = new Rect(monitor.Left + 10, monitor.Top + 10, 100, 90);
        Assert.True(overlay.TryShowWithinMonitor(monitor, moved));
        Assert.Equal(handle, new WindowInteropHelper(overlay).Handle);
        Assert.True(Win32WindowHelper.TryGetWindowPhysicalBounds(overlay, out actual));
        Assert.Equal(moved, actual);
        overlay.Scene.Children.Add(new Border());
        var flights = overlay.Flights;
        Assert.Same(flights, overlay.Flights);
        Assert.True(flights.TryLaunch(Guid.NewGuid(), Guid.NewGuid(),
            new Point(10, 10), new Point(70, 60), 0.6, 20));
        Assert.Equal(1, flights.Count);
        overlay.Dispose();
        Assert.Equal(0, flights.Count);
        Assert.Empty(flights.Advance(1, new Point(70, 60)));
        Assert.False(flights.TryLaunch(Guid.NewGuid(), Guid.NewGuid(),
            new Point(10, 10), new Point(70, 60), 0.6, 20));
        Assert.Throws<ObjectDisposedException>(() => { _ = overlay.Flights; });
        Assert.False(Win32WindowHelper.IsValidWindowHandle(handle));
        Assert.Empty(overlay.Scene.Children);
        Assert.Null(overlay.Content);
        Assert.False(overlay.TryScreenPhysicalToSceneDip(physical, out _));
        Assert.False(overlay.TryShowWithinMonitor(monitor, moved));
        overlay.Dispose();
    });

    [Fact]
    public void InvalidOrDisjointBoundsDoNotCreateHandle() => RunSta(() =>
    {
        using var overlay = new SnowballOverlayWindow();
        var monitor = new Rect(-1920, -200, 1920, 1080);
        Assert.False(overlay.TryShowWithinMonitor(Rect.Empty, monitor));
        Assert.False(overlay.TryShowWithinMonitor(monitor, new Rect(100, 100, 20, 20)));
        Assert.False(overlay.TryShowWithinMonitor(monitor, new Rect(-100, 0, 0.2, 0.2)));
        Assert.False(overlay.TryShowWithinMonitor(monitor, new Rect(double.NaN, 0, 10, 10)));
        Assert.False(overlay.TryScreenPhysicalToSceneDip(default, out _));
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(overlay).Handle);
    });

    [Fact]
    public void ImpactParticlesRenderFadeAndFinishIndependently() => RunSta(() =>
    {
        var first = new LabPet.Client.Rendering.SnowballImpactVisual();
        var second = new LabPet.Client.Rendering.SnowballImpactVisual();
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1d, 0d })
            first.Advance(invalid);
        Assert.False(first.IsComplete);
        Assert.False(first.IsHitTestVisible);
        Assert.False(first.Focusable);
        first.Advance(0.15);
        var visible = AlphaSum(first);
        Assert.True(visible > 0);
        first.Advance(0.35);
        Assert.InRange(AlphaSum(first), 1L, visible - 1);
        first.Advance(1);
        Assert.True(first.IsComplete);
        Assert.Equal(0L, AlphaSum(first));
        first.Advance(1);
        Assert.False(second.IsComplete);
        Assert.True(AlphaSum(second) > 0);
    });

    [Fact]
    public void FlightRetargetsThenImpactsOnceAndFinishesParticles() => RunSta(() =>
    {
        using var flight = new LabPet.Client.Rendering.SnowballFlightVisual(
            new Point(20, 50), new Point(70, 50), 0.6, 20);
        Assert.True(AlphaSum(flight) > 0);
        Assert.False(flight.Advance(0.2));
        var before = flight.Position;
        Assert.True(flight.TryRetarget(new Point(60, 60)));
        Assert.Equal(before, flight.Position);
        Assert.False(flight.Advance(double.NaN));
        Assert.True(flight.Advance(0.45));
        Assert.Equal(new Point(60, 60), flight.Position);
        Assert.IsType<LabPet.Client.Rendering.SnowballImpactVisual>(Assert.Single(flight.Children.Cast<UIElement>()));
        Assert.True(AlphaSum(flight) > 0);
        Assert.False(flight.TryRetarget(new Point(30, 30)));
        Assert.False(flight.Advance(0.6));
        Assert.True(flight.IsComplete);
        Assert.Equal(0L, AlphaSum(flight));
        Assert.False(flight.Advance(1));
    });

    [Fact]
    public void CancelledFlightDoesNotImpactOrAffectOtherFlights() => RunSta(() =>
    {
        using var first = new LabPet.Client.Rendering.SnowballFlightVisual(
            new Point(20, 50), new Point(70, 50), 0.5, 10);
        using var second = new LabPet.Client.Rendering.SnowballFlightVisual(
            new Point(20, 50), new Point(70, 50), 0.5, 10);
        first.Dispose();
        first.Dispose();
        Assert.True(first.IsComplete);
        Assert.Empty(first.Children);
        Assert.False(first.Advance(1));
        Assert.False(first.TryRetarget(new Point(60, 60)));
        Assert.False(second.IsComplete);
        Assert.True(second.Advance(2));
        Assert.True(second.IsComplete);
        Assert.False(second.Advance(2));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PetVisualProjectsMovingHandReleasesOnceAndDisposesIndependently(bool external) => RunSta(() =>
    {
        var procedural = new LabPet.Client.Rendering.ProceduralPetMeshFactory();
        LabPet.Client.Rendering.IPetMeshFactory factory = external
            ? new LabPet.Client.Rendering.BlenderAssetPetMeshFactory(procedural) : procedural;
        var appearance = new LabPet.Shared.Models.PetAppearance
        {
            SpeciesId = LabPet.Shared.Models.PetSpeciesIds.MochiSpirit
        };
        using var first = new LabPet.Client.Rendering.SnowballPetVisual(factory, appearance, 1);
        using var second = new LabPet.Client.Rendering.SnowballPetVisual(factory, appearance, 1);
        foreach (var visual in new[] { first, second })
        {
            visual.Measure(new Size(visual.Width, visual.Height));
            visual.Arrange(new Rect(0, 0, visual.Width, visual.Height));
            visual.UpdateLayout();
        }
        Assert.True(first.TryGetHandDip(out var initial));
        Assert.True(second.TryGetHandDip(out var untouched));
        Assert.Equal(initial, untouched);
        Assert.True(first.Animator.TryStartSnowballThrow());
        var releases = 0;
        var moved = false;
        for (var i = 0; i < 60; i++)
        {
            if (first.Advance(1.0 / 60, 0, 0)) releases++;
            Assert.True(first.TryGetHandDip(out var hand));
            Assert.True(double.IsFinite(hand.X) && double.IsFinite(hand.Y));
            moved |= (hand - initial).Length > 0.1;
        }
        Assert.True(moved);
        Assert.Equal(1, releases);
        Assert.True(second.TryGetHandDip(out var after));
        Assert.Equal(untouched, after);
        first.Dispose();
        first.Dispose();
        Assert.Empty(first.Children);
        Assert.False(first.TryGetHandDip(out _));
        Assert.False(first.Advance(1, 0, 0));
        Assert.False(first.Animator.TryStartSnowballThrow());
        Assert.True(second.Animator.TryStartSnowballThrow());
        Assert.True(second.TryGetHandDip(out _));
    });

    [Fact]
    public void PetPlacementAndPhysicalHandFollowShownOverlay() => RunSta(() =>
    {
        using var overlay = new SnowballOverlayWindow();
        using var pet = new LabPet.Client.Rendering.SnowballPetVisual(
            new LabPet.Client.Rendering.ProceduralPetMeshFactory(),
            new LabPet.Shared.Models.PetAppearance(), 1);
        Assert.False(pet.TryGetHandPhysical(out _));
        Assert.False(pet.TryPlace(overlay, new Rect(0, 0, 214, 224)));
        new WindowInteropHelper(overlay).EnsureHandle();
        Assert.True(Win32WindowHelper.TryGetNearestMonitorBounds(overlay, out var monitor));
        var region = new Rect(monitor.Left + 10, monitor.Top + 10, 500, 400);
        Assert.True(overlay.TryShowWithinMonitor(monitor, region));
        overlay.Scene.Children.Add(pet);
        var bounds = new Rect(region.Left + 20, region.Top + 20, 214, 224);
        Assert.True(pet.TryPlace(overlay, bounds));
        overlay.UpdateLayout();
        Assert.InRange((pet.PointToScreen(default) - bounds.TopLeft).Length, 0, 0.01);
        Assert.True(pet.TryGetHandDip(out var local));
        Assert.True(pet.TryGetHandPhysical(out var hand));
        Assert.InRange((pet.PointToScreen(local) - hand).Length, 0, 0.01);
        bounds.Offset(31, 17);
        Assert.True(pet.TryPlace(overlay, bounds));
        overlay.UpdateLayout();
        Assert.True(pet.TryGetHandPhysical(out var moved));
        Assert.InRange((moved - hand - new Vector(31, 17)).Length, 0, 0.01);
        Assert.False(pet.TryPlace(overlay, Rect.Empty));
        Assert.True(pet.TryGetHandPhysical(out var unchanged));
        Assert.Equal(moved, unchanged);
        pet.Dispose();
        Assert.False(pet.TryGetHandPhysical(out _));
        Assert.False(pet.TryPlace(overlay, bounds));
    });

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    public void TemporaryActorReusesVisualAndReleasesEveryEventFromHand(int count) => RunSta(() =>
    {
        using var overlay = new SnowballOverlayWindow();
        new WindowInteropHelper(overlay).EnsureHandle();
        Assert.True(Win32WindowHelper.TryGetNearestMonitorBounds(overlay, out var monitor));
        Assert.True(overlay.TryShowWithinMonitor(monitor, monitor));
        using var pet = new LabPet.Client.Rendering.SnowballPetVisual(
            new LabPet.Client.Rendering.ProceduralPetMeshFactory(),
            new LabPet.Shared.Models.PetAppearance(), 1);
        Assert.True(LabPet.Client.Rendering.SnowballSpawnGeometry.TryCreate(
            monitor, new Size(214, 224), 0.13, out var spawn));
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        using var actor = new LabPet.Client.Rendering.SnowballTemporaryActor(overlay, pet, spawn, ids[0]);
        var launches = new List<Guid>();
        var impacts = new List<Guid>();
        var senderId = Guid.NewGuid();
        var handle = new WindowInteropHelper(overlay).Handle;
        for (var tick = 0; tick < (count + 5) * 100; tick++)
        {
            if (tick > 0 && tick % 100 == 0 && tick / 100 < count)
                Assert.True(actor.TryEnqueue(ids[tick / 100]));
            var target = new Point(monitor.Left + monitor.Width / 2 + 20 * Math.Sin(tick * 0.01),
                monitor.Top + monitor.Height / 2);
            foreach (var id in overlay.AdvanceFlightsPhysical(0.01, target))
            {
                Assert.DoesNotContain(id, impacts);
                impacts.Add(id);
                actor.NotifyImpact(id);
            }
            actor.Advance(0.01, 0.2, 0, (id, hand) =>
            {
                Assert.True(pet.TryGetHandPhysical(out var actual));
                Assert.Equal(actual, hand);
                Assert.True(double.IsFinite(hand.X) && double.IsFinite(hand.Y));
                Assert.True(overlay.TryLaunchPhysical(senderId, id, hand, target, 0.6, 80));
                Assert.True(overlay.TryScreenPhysicalToSceneDip(hand, out var handDip));
                var flight = overlay.Scene.Children.OfType<LabPet.Client.Rendering.SnowballFlightVisual>().Last();
                Assert.InRange((flight.Position - handDip).Length, 0, 0.01);
                launches.Add(id);
            });
            Assert.Equal(spawn.VisibleBounds, actor.OccupiedBounds);
            Assert.Equal(handle, new WindowInteropHelper(overlay).Handle);
            if (tick >= 40 && tick < (count - 1) * 100)
            {
                Assert.Same(pet, Assert.Single(overlay.Scene.Children.OfType<LabPet.Client.Rendering.SnowballPetVisual>()));
                Assert.InRange((pet.PointToScreen(default) - spawn.VisibleBounds.TopLeft).Length, 0, 0.01);
            }
        }
        Assert.Equal(ids, launches);
        Assert.Equal(ids, impacts);
        Assert.Equal(0, overlay.Flights.Count);
        Assert.Equal(LabPet.Client.Rendering.SnowballActorPhase.Complete, actor.Phase);
        Assert.Empty(overlay.Scene.Children);
        Assert.False(pet.TryGetHandPhysical(out _));
        Assert.False(actor.TryEnqueue(Guid.NewGuid()));
        actor.Dispose();
        actor.Advance(1, 0, 0, (_, _) => Assert.Fail("Late release after disposal."));
    });

    [Fact]
    public void HeldSnowballTracksHandAndDisappearsAtReleaseAndDisposal() => RunSta(() =>
    {
        using var overlay = new SnowballOverlayWindow();
        new WindowInteropHelper(overlay).EnsureHandle();
        Assert.True(Win32WindowHelper.TryGetNearestMonitorBounds(overlay, out var monitor));
        Assert.True(overlay.TryShowWithinMonitor(monitor, monitor));
        using var pet = new LabPet.Client.Rendering.SnowballPetVisual(
            new LabPet.Client.Rendering.ProceduralPetMeshFactory(),
            new LabPet.Shared.Models.PetAppearance(), 1);
        Assert.True(LabPet.Client.Rendering.SnowballSpawnGeometry.TryCreate(
            monitor, new Size(214, 224), 0.13, out var spawn));
        using var actor = new LabPet.Client.Rendering.SnowballTemporaryActor(overlay, pet, spawn, Guid.NewGuid());
        var heldFrames = 0;
        var launches = 0;
        System.Windows.Shapes.Ellipse? ball = null;
        for (var i = 0; i < 140; i++)
        {
            actor.Advance(0.01, 0.2, 0, (_, _) =>
            {
                launches++;
                Assert.Null(pet.HeldSnowballPosition);
                Assert.NotNull(ball);
                Assert.Equal(Visibility.Collapsed, ball.Visibility);
            });
            if (pet.HeldSnowballPosition is not { } held) continue;
            heldFrames++;
            Assert.True(pet.TryGetHandDip(out var hand));
            Assert.Equal(hand, held);
            var layer = Assert.Single(pet.Children.OfType<Canvas>());
            var current = Assert.Single(layer.Children.OfType<System.Windows.Shapes.Ellipse>());
            if (ball is not null) Assert.Same(ball, current);
            ball = current;
            Assert.Equal(Visibility.Visible, ball.Visibility);
            Assert.Equal(hand.X, Canvas.GetLeft(ball) + ball.Width / 2, 6);
            Assert.Equal(hand.Y, Canvas.GetTop(ball) + ball.Height / 2, 6);
            Assert.False(layer.IsHitTestVisible);
        }
        Assert.True(heldFrames > 10);
        Assert.Equal(1, launches);
        pet.SetSnowballHeld(true);
        Assert.NotNull(pet.HeldSnowballPosition);
        pet.Dispose();
        Assert.Null(pet.HeldSnowballPosition);
        Assert.Empty(pet.Children);
        pet.SetSnowballHeld(true);
        Assert.Null(pet.HeldSnowballPosition);
    });

    [Fact]
    public void SceneSelectsSixSpawnsWithoutMovingExistingActorsAndReleasesSpace() => RunSta(() =>
    {
        using var overlay = new SnowballOverlayWindow();
        using var scene = new LabPet.Client.Rendering.SnowballSceneController(overlay);
        Assert.False(scene.TrySelectSpawn(new Rect(0, 0, 1000, 800), new Size(100, 120), out _));
        new WindowInteropHelper(overlay).EnsureHandle();
        Assert.True(Win32WindowHelper.TryGetNearestMonitorBounds(overlay, out var monitor));
        Assert.True(overlay.TryShowWithinMonitor(monitor, monitor));
        var actors = new List<LabPet.Client.Rendering.SnowballTemporaryActor>();
        var bounds = new List<Rect>();
        var senders = new List<Guid>();
        var random = new Random(191);
        for (var i = 0; i < 6; i++)
        {
            Assert.True(scene.TrySelectSpawn(monitor, new Size(100, 120), out var spawn, random));
            foreach (var previous in bounds)
                Assert.False(previous.IntersectsWith(spawn.VisibleBounds));
            var pet = new LabPet.Client.Rendering.SnowballPetVisual(
                new LabPet.Client.Rendering.ProceduralPetMeshFactory(),
                new LabPet.Shared.Models.PetAppearance(), 1);
            var actor = new LabPet.Client.Rendering.SnowballTemporaryActor(overlay, pet, spawn, Guid.NewGuid());
            var sender = Guid.NewGuid();
            Assert.True(scene.TryAdd(sender, actor));
            actors.Add(actor);
            bounds.Add(spawn.VisibleBounds);
            senders.Add(sender);
            for (var j = 0; j <= i; j++) Assert.Equal(bounds[j], actors[j].OccupiedBounds);
        }
        Assert.False(scene.TrySelectSpawn(monitor, new Size(100, 120), out _));
        scene.CancelSender(senders[0]);
        Assert.True(scene.TrySelectSpawn(monitor, new Size(100, 120), out _));
        scene.Dispose();
        Assert.False(scene.TrySelectSpawn(monitor, new Size(100, 120), out _));
        Assert.Empty(overlay.Scene.Children);
    });

    [Theory]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    public void SceneControllerRoutesParallelImpactsAndCleansUp(int count, bool disposeInFlight) => RunSta(() =>
    {
        using var overlay = new SnowballOverlayWindow();
        new WindowInteropHelper(overlay).EnsureHandle();
        Assert.True(Win32WindowHelper.TryGetNearestMonitorBounds(overlay, out var monitor));
        Assert.True(overlay.TryShowWithinMonitor(monitor, monitor));
        using var scene = new LabPet.Client.Rendering.SnowballSceneController(overlay);
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var pet = new LabPet.Client.Rendering.SnowballPetVisual(
                new LabPet.Client.Rendering.ProceduralPetMeshFactory(),
                new LabPet.Shared.Models.PetAppearance(), 1);
            Assert.True(LabPet.Client.Rendering.SnowballSpawnGeometry.TryCreate(
                monitor, new Size(214, 224), (i + 0.3) / count, out var spawn));
            var id = Guid.NewGuid();
            ids.Add(id);
            var actor = new LabPet.Client.Rendering.SnowballTemporaryActor(overlay, pet, spawn, id);
            var sender = Guid.NewGuid();
            Assert.True(scene.TryAdd(sender, actor));
            Assert.False(scene.TryAdd(sender, actor));
        }
        Assert.Equal(count, scene.ActorCount);
        var impacts = new List<Guid>();
        for (var tick = 0; tick < 250; tick++)
        {
            var target = new Point(monitor.Left + monitor.Width / 2 + 20 * Math.Sin(tick * 0.02),
                monitor.Top + monitor.Height / 2);
            scene.Advance(0.02, target, id =>
            {
                Assert.DoesNotContain(id, impacts);
                impacts.Add(id);
            });
            if (disposeInFlight && overlay.Flights.Count > 0)
            {
                Assert.Equal(count, overlay.Flights.Count);
                scene.Dispose();
                scene.Dispose();
                Assert.Empty(overlay.Scene.Children);
                Assert.Equal(0, overlay.Flights.Count);
                scene.Advance(1, target, _ => Assert.Fail("Impact after disposal."));
                break;
            }
        }
        Assert.Equal(disposeInFlight ? 0 : count, impacts.Count);
        if (!disposeInFlight) Assert.True(ids.ToHashSet().SetEquals(impacts));
        Assert.Equal(0, scene.ActorCount);
        Assert.Equal(0, overlay.Flights.Count);
        Assert.Empty(overlay.Scene.Children);
        overlay.Dispose();
        scene.Dispose();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceneControllerRejectsForeignActorAndHandlesImpactShutdown(bool closeWindow) => RunSta(() =>
    {
        using var overlay = new SnowballOverlayWindow();
        new WindowInteropHelper(overlay).EnsureHandle();
        Assert.True(Win32WindowHelper.TryGetNearestMonitorBounds(overlay, out var monitor));
        Assert.True(overlay.TryShowWithinMonitor(monitor, monitor));
        using var foreignOverlay = new SnowballOverlayWindow();
        using var foreignScene = new LabPet.Client.Rendering.SnowballSceneController(foreignOverlay);
        using var scene = new LabPet.Client.Rendering.SnowballSceneController(overlay);
        using var pet = new LabPet.Client.Rendering.SnowballPetVisual(
            new LabPet.Client.Rendering.ProceduralPetMeshFactory(), new LabPet.Shared.Models.PetAppearance(), 1);
        Assert.True(LabPet.Client.Rendering.SnowballSpawnGeometry.TryCreate(
            monitor, new Size(214, 224), 0.2, out var spawn));
        var eventId = Guid.NewGuid();
        using var actor = new LabPet.Client.Rendering.SnowballTemporaryActor(overlay, pet, spawn, eventId);
        var sender = Guid.NewGuid();
        Assert.False(foreignScene.TryAdd(sender, actor));
        Assert.True(scene.TryAdd(sender, actor));
        var hits = new List<Guid>();
        var target = new Point(monitor.Left + monitor.Width / 2, monitor.Top + monitor.Height / 2);
        for (var i = 0; i < 200; i++)
            scene.Advance(0.02, target, id =>
            {
                hits.Add(id);
                if (closeWindow) overlay.Dispose();
                else scene.Dispose();
            });
        Assert.Equal(new[] { eventId }, hits);
        Assert.Equal(0, scene.ActorCount);
        Assert.Empty(overlay.Scene.Children);
        Assert.False(scene.TryEnqueue(sender, Guid.NewGuid()));
        if (!closeWindow) Assert.Equal(0, overlay.Flights.Count);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceneLoopRendersAndStopsWhenIdleOrClosed(bool closeOnImpact) => RunSta(() =>
    {
        using var overlay = new SnowballOverlayWindow();
        new WindowInteropHelper(overlay).EnsureHandle();
        Assert.True(Win32WindowHelper.TryGetNearestMonitorBounds(overlay, out var monitor));
        Assert.True(overlay.TryShowWithinMonitor(monitor, monitor));
        using var scene = new LabPet.Client.Rendering.SnowballSceneController(overlay);
        var hits = new List<Guid>();
        var target = new Point(monitor.Left + monitor.Width / 2, monitor.Top + monitor.Height / 2);
        using var loop = new LabPet.Client.Rendering.SnowballSceneLoop(overlay, scene, () => target, id =>
        {
            hits.Add(id);
            if (closeOnImpact) overlay.Dispose();
        });
        loop.Wake();
        Assert.False(loop.IsRunning);
        Assert.False(scene.HasWork);
        using var pet = new LabPet.Client.Rendering.SnowballPetVisual(
            new LabPet.Client.Rendering.ProceduralPetMeshFactory(), new LabPet.Shared.Models.PetAppearance(), 1);
        Assert.True(LabPet.Client.Rendering.SnowballSpawnGeometry.TryCreate(
            monitor, new Size(214, 224), 0.2, out var spawn));
        var eventId = Guid.NewGuid();
        using var actor = new LabPet.Client.Rendering.SnowballTemporaryActor(overlay, pet, spawn, eventId);
        Assert.True(scene.TryAdd(Guid.NewGuid(), actor));
        loop.Wake();
        loop.Wake();
        Assert.True(loop.IsRunning);
        var frame = new System.Windows.Threading.DispatcherFrame();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var guard = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(20)
        };
        guard.Tick += (_, _) =>
        {
            if (!loop.IsRunning || elapsed.Elapsed > TimeSpan.FromSeconds(8)) frame.Continue = false;
        };
        guard.Start();
        try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
        finally { guard.Stop(); }
        Assert.False(loop.IsRunning);
        Assert.Equal(new[] { eventId }, hits);
        Assert.False(scene.HasWork);
        Assert.False(overlay.HasActiveFlights);
        Assert.Empty(overlay.Scene.Children);
        loop.Dispose();
        loop.Dispose();
        loop.Wake();
        Assert.False(loop.IsRunning);
    });

    [Fact]
    public void SceneLoopRejectsForeignAndDuplicateBindingsAndAllowsReplacement() => RunSta(() =>
    {
        using var overlay = new SnowballOverlayWindow();
        using var foreign = new SnowballOverlayWindow();
        using var scene = new LabPet.Client.Rendering.SnowballSceneController(overlay);
        Assert.Throws<InvalidOperationException>(() =>
            new LabPet.Client.Rendering.SnowballSceneLoop(foreign, scene, () => null, _ => { }));
        using var first = new LabPet.Client.Rendering.SnowballSceneLoop(overlay, scene, () => null, _ => { });
        Assert.Throws<InvalidOperationException>(() =>
            new LabPet.Client.Rendering.SnowballSceneLoop(overlay, scene, () => null, _ => { }));
        first.Dispose();
        using var replacement = new LabPet.Client.Rendering.SnowballSceneLoop(overlay, scene, () => null, _ => { });
        first.Dispose();
        Assert.Throws<InvalidOperationException>(() =>
            new LabPet.Client.Rendering.SnowballSceneLoop(overlay, scene, () => null, _ => { }));
        scene.Dispose();
        replacement.Wake();
        Assert.False(replacement.IsRunning);
        Assert.Throws<InvalidOperationException>(() =>
            new LabPet.Client.Rendering.SnowballSceneLoop(overlay, scene, () => null, _ => { }));
    });

    [Fact]
    public void PendingActorsAreCreatedOnlyWhenSlotsOpenAndKeepFifoOrder() => RunSta(() =>
    {
        using var overlay = new SnowballOverlayWindow();
        new WindowInteropHelper(overlay).EnsureHandle();
        Assert.True(Win32WindowHelper.TryGetNearestMonitorBounds(overlay, out var monitor));
        Assert.True(overlay.TryShowWithinMonitor(monitor, monitor));
        var clock = new PendingTestClock();
        using var scene = new LabPet.Client.Rendering.SnowballSceneController(overlay, 6, clock);
        var senders = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        var events = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        var created = new List<Guid>();
        for (var i = 0; i < 8; i++)
            Assert.True(scene.TryReceive(senders[i], events[i], id =>
            {
                created.Add(id);
                return CreatePendingTestActor(overlay, monitor, id);
            }));
        Assert.Equal(events.Take(6), created);
        Assert.Equal(6, scene.ActorCount);
        Assert.Equal(2, scene.PendingCount);
        Assert.Equal(6, overlay.Scene.Children.OfType<LabPet.Client.Rendering.SnowballPetVisual>().Count());
        Assert.False(scene.TryReceive(senders[6], events[6], _ => throw new Exception("Duplicate factory")));
        Assert.False(scene.TryEnqueue(senders[0], Guid.NewGuid()));
        scene.CancelSender(senders[0]);
        var hits = new List<Guid>();
        var target = new Point(monitor.Left + monitor.Width / 2, monitor.Top + monitor.Height / 2);
        scene.Advance(0.01, target, hits.Add);
        Assert.Equal(events.Take(7), created);
        Assert.Equal(1, scene.PendingCount);
        for (var tick = 0; tick < 400; tick++)
        {
            scene.Advance(0.02, target, hits.Add);
            Assert.InRange(scene.ActorCount, 0, 6);
        }
        Assert.Equal(events, created);
        Assert.Equal(7, hits.Count);
        Assert.True(events.Skip(1).ToHashSet().SetEquals(hits));
        Assert.False(scene.HasWork);
        Assert.Empty(overlay.Scene.Children);
    });

    [Fact]
    public void PendingQueueIsBoundedExpiresAtDeadlineAndClearsOnShutdown() => RunSta(() =>
    {
        using var overlay = new SnowballOverlayWindow();
        new WindowInteropHelper(overlay).EnsureHandle();
        Assert.True(Win32WindowHelper.TryGetNearestMonitorBounds(overlay, out var monitor));
        Assert.True(overlay.TryShowWithinMonitor(monitor, monitor));
        var clock = new PendingTestClock();
        using var scene = new LabPet.Client.Rendering.SnowballSceneController(overlay, 1, clock);
        var active = Guid.NewGuid();
        Assert.True(scene.TryReceive(active, Guid.NewGuid(), id => CreatePendingTestActor(overlay, monitor, id)));
        var called = 0;
        LabPet.Client.Rendering.SnowballTemporaryActor Unexpected(Guid id)
        {
            called++;
            throw new InvalidOperationException("Expired or cancelled factory must not run.");
        }
        var pending = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var sender in pending) Assert.True(scene.TryReceive(sender, Guid.NewGuid(), Unexpected));
        Assert.Equal(12, scene.PendingCount);
        Assert.False(scene.TryReceive(Guid.NewGuid(), Guid.NewGuid(), Unexpected));
        scene.CancelSender(pending[0]);
        Assert.Equal(11, scene.PendingCount);
        clock.Timestamp = 3999;
        var target = new Point(monitor.Left + monitor.Width / 2, monitor.Top + monitor.Height / 2);
        scene.Advance(0.01, target, _ => { });
        Assert.Equal(11, scene.PendingCount);
        clock.Timestamp = 4000;
        scene.Advance(0.01, target, _ => { });
        Assert.Equal(0, scene.PendingCount);
        Assert.Equal(0, called);
        Assert.True(scene.TryReceive(Guid.NewGuid(), Guid.NewGuid(), Unexpected));
        overlay.Dispose();
        Assert.Equal(0, scene.PendingCount);
        Assert.Equal(0, scene.ActorCount);
        Assert.False(scene.HasWork);
        Assert.Equal(0, called);
        Assert.False(scene.TryReceive(Guid.NewGuid(), Guid.NewGuid(), Unexpected));
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void PendingFactoryFailureCancellationExpiryAndShutdownDoNotReportSuccess(int mode) => RunSta(() =>
    {
        using var overlay = new SnowballOverlayWindow();
        new WindowInteropHelper(overlay).EnsureHandle();
        Assert.True(Win32WindowHelper.TryGetNearestMonitorBounds(overlay, out var monitor));
        Assert.True(overlay.TryShowWithinMonitor(monitor, monitor));
        var clock = new PendingTestClock();
        using var scene = new LabPet.Client.Rendering.SnowballSceneController(overlay, 1, clock);
        var sender = Guid.NewGuid();
        var reentered = false;
        LabPet.Client.Rendering.SnowballTemporaryActor? created = null;
        var accepted = scene.TryReceive(sender, Guid.NewGuid(), id =>
        {
            reentered = scene.TryReceive(Guid.NewGuid(), Guid.NewGuid(), _ =>
                throw new InvalidOperationException("Reentrant factory"));
            if (mode == 0) throw new InvalidOperationException("Factory failure");
            if (mode == 5) return null!;
            created = CreatePendingTestActor(overlay, monitor, id);
            if (mode == 1) scene.CancelSender(sender);
            if (mode == 2) clock.Timestamp = 4000;
            if (mode == 3) overlay.Dispose();
            if (mode == 4) scene.Dispose();
            return created;
        });
        Assert.False(accepted);
        Assert.False(reentered);
        Assert.Equal(0, scene.ActorCount);
        Assert.Equal(0, scene.PendingCount);
        Assert.False(scene.HasWork);
        Assert.Empty(overlay.Scene.Children);
        if (created is not null)
            Assert.Equal(LabPet.Client.Rendering.SnowballActorPhase.Complete, created.Phase);
        if (mode != 3 && mode != 4)
        {
            Assert.True(scene.TryReceive(sender, Guid.NewGuid(), id => CreatePendingTestActor(overlay, monitor, id)));
            Assert.Equal(1, scene.ActorCount);
        }
    });

    [Fact]
    public void PendingSenderGetsSlotWithoutCancellingAcceptedFollowupOrFlights() => RunSta(() =>
    {
        using var overlay = new SnowballOverlayWindow();
        new WindowInteropHelper(overlay).EnsureHandle();
        Assert.True(Win32WindowHelper.TryGetNearestMonitorBounds(overlay, out var monitor));
        Assert.True(overlay.TryShowWithinMonitor(monitor, monitor));
        var clock = new PendingTestClock();
        using var scene = new LabPet.Client.Rendering.SnowballSceneController(overlay, 1, clock);
        var active = Guid.NewGuid();
        var waiting = Guid.NewGuid();
        var first = Guid.NewGuid();
        var followup = Guid.NewGuid();
        var queued = Guid.NewGuid();
        var created = new List<Guid>();
        LabPet.Client.Rendering.SnowballTemporaryActor Create(Guid id)
        {
            created.Add(id);
            return CreatePendingTestActor(overlay, monitor, id);
        }
        Assert.True(scene.TryReceive(active, first, Create));
        var target = new Point(monitor.Left + monitor.Width / 2, monitor.Top + monitor.Height / 2);
        var hits = new List<Guid>();
        for (var tick = 0; tick < 100; tick++) scene.Advance(0.01, target, hits.Add);
        Assert.True(overlay.Flights.Count > 0);
        Assert.True(scene.TryReceive(active, followup, Create));
        Assert.True(scene.TryReceive(waiting, queued, Create));
        Assert.Equal(new[] { first }, created);
        for (var tick = 0; tick < 700; tick++)
        {
            if (scene.PendingCount > 0 && tick % 100 == 0)
            {
                Assert.False(scene.TryReceive(active, Guid.NewGuid(), Create));
                Assert.False(scene.TryEnqueue(active, Guid.NewGuid()));
            }
            scene.Advance(0.01, target, hits.Add);
            Assert.InRange(scene.ActorCount, 0, 1);
        }
        Assert.Equal(new[] { first, queued }, created);
        Assert.Equal(new[] { first, followup, queued }, hits);
        Assert.Equal(0, scene.PendingCount);
        Assert.False(scene.HasWork);
        Assert.Empty(overlay.Scene.Children);
    });

    private sealed class PendingTestClock : TimeProvider
    {
        internal long Timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Timestamp;
    }

    private static LabPet.Client.Rendering.SnowballTemporaryActor CreatePendingTestActor(
        SnowballOverlayWindow overlay, Rect monitor, Guid eventId)
    {
        Assert.True(LabPet.Client.Rendering.SnowballSpawnGeometry.TryCreate(
            monitor, new Size(214, 224), 0.2, out var spawn));
        var pet = new LabPet.Client.Rendering.SnowballPetVisual(
            new LabPet.Client.Rendering.ProceduralPetMeshFactory(), new LabPet.Shared.Models.PetAppearance(), 1);
        try { return new LabPet.Client.Rendering.SnowballTemporaryActor(overlay, pet, spawn, eventId); }
        catch { pet.Dispose(); throw; }
    }

    private static long AlphaSum(FrameworkElement element)
    {
        element.Measure(new Size(100, 100));
        element.Arrange(new Rect(0, 0, 100, 100));
        element.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            100, 100, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[100 * 100 * 4];
        bitmap.CopyPixels(pixels, 100 * 4, 0);
        long sum = 0;
        for (var i = 3; i < pixels.Length; i += 4) sum += pixels[i];
        return sum;
    }

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
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Snowball overlay test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
