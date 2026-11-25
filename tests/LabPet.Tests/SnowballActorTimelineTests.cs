using LabPet.Client.Rendering;
using Xunit;

namespace LabPet.Tests;

public sealed class SnowballActorTimelineTests
{
    [Theory]
    [InlineData(3, false)]
    [InlineData(10, false)]
    [InlineData(10, true)]
    public void OneSecondSequenceLaunchesEveryEventWithoutRepeatedEntrance(int count, bool visitor)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        var actor = new SnowballActorTimeline(ids[0], visitor);
        var launched = new List<Guid>();
        var flights = new List<(Guid Id, int Due)>();
        for (var tick = 0; tick < (count + 5) * 100; tick++)
        {
            if (tick > 0 && tick % 100 == 0 && tick / 100 < count)
                Assert.True(actor.TryEnqueue(ids[tick / 100]));
            foreach (var flight in flights.Where(f => f.Due <= tick).ToArray())
            {
                actor.NotifyImpact(flight.Id);
                flights.Remove(flight);
            }
            actor.Advance(0.01, id =>
            {
                launched.Add(id);
                flights.Add((id, tick + 60));
            });
            if (tick >= 40 && tick < (count - 1) * 100)
                Assert.DoesNotContain(actor.Phase, new[] { SnowballActorPhase.Entering, SnowballActorPhase.Exiting, SnowballActorPhase.Laughing });
            Assert.InRange(actor.PendingCount, 0, 1);
        }
        Assert.Equal(ids, launched);
        Assert.Equal(0, actor.InFlightCount);
        Assert.Equal(SnowballActorPhase.Complete, actor.Phase);
        Assert.Equal(visitor ? 1d : 0d, actor.VisibilityProgress);
    }

    [Fact]
    public void QueueIsBoundedAndDuplicateEventsNeverLaunchTwice()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var actor = new SnowballActorTimeline(first, false);
        Assert.False(actor.TryEnqueue(first));
        Assert.False(actor.TryEnqueue(Guid.Empty));
        Assert.True(actor.TryEnqueue(second));
        Assert.False(actor.TryEnqueue(Guid.NewGuid()));
        var launches = new List<Guid>();
        actor.Advance(1.9, launches.Add);
        Assert.Equal(new[] { first, second }, launches);
        Assert.Equal(2, actor.InFlightCount);
        actor.NotifyImpact(first);
        actor.NotifyImpact(first);
        actor.NotifyImpact(Guid.NewGuid());
        Assert.Equal(1, actor.InFlightCount);
        actor.Advance(5, launches.Add);
        Assert.Equal(SnowballActorPhase.WaitingForImpact, actor.Phase);
        Assert.False(actor.TryEnqueue(second));
        actor.NotifyImpact(second);
        actor.Advance(2, launches.Add);
        Assert.Equal(SnowballActorPhase.Complete, actor.Phase);
        Assert.Equal(2, launches.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExitCancellationPreservesVisibilityAndHandlesExpiredPending(bool expire)
    {
        var first = Guid.NewGuid();
        var next = Guid.NewGuid();
        var actor = new SnowballActorTimeline(first, false);
        var launches = new List<Guid>();
        actor.Advance(1.3, launches.Add);
        actor.NotifyImpact(first);
        actor.Advance(1.1, launches.Add);
        Assert.Equal(SnowballActorPhase.Exiting, actor.Phase);
        var visible = actor.VisibilityProgress;
        Assert.True(actor.TryEnqueue(next));
        Assert.Equal(visible, actor.VisibilityProgress, 10);
        actor.Advance(expire ? 3 : 1.3, launches.Add);
        if (expire)
        {
            Assert.Single(launches);
            Assert.Equal(SnowballActorPhase.Complete, actor.Phase);
        }
        else
        {
            Assert.Equal(new[] { first, next }, launches);
            Assert.Equal(SnowballActorPhase.WaitingForImpact, actor.Phase);
        }
    }

    [Theory]
    [InlineData(false, 0.2)]
    [InlineData(false, 1.0)]
    [InlineData(false, 1.3)]
    [InlineData(true, 0.1)]
    [InlineData(true, 0.6)]
    [InlineData(true, 1.3)]
    public void CancelClearsPendingAndFlightsAndRejectsLateEvents(bool visitor, double elapsed)
    {
        var first = Guid.NewGuid();
        var actor = new SnowballActorTimeline(first, visitor);
        var other = new SnowballActorTimeline(Guid.NewGuid(), false);
        var launches = new List<Guid>();
        actor.Advance(elapsed, launches.Add);
        Assert.True(actor.TryEnqueue(Guid.NewGuid()));
        var launchedBeforeCancel = launches.Count;

        actor.Cancel();
        actor.Cancel();
        actor.NotifyImpact(first);
        Assert.False(actor.TryEnqueue(Guid.NewGuid()));
        actor.Advance(10, launches.Add);

        Assert.Equal(SnowballActorPhase.Complete, actor.Phase);
        Assert.Equal(0, actor.PendingCount);
        Assert.Equal(0, actor.InFlightCount);
        Assert.Equal(0d, actor.ThrowElapsed);
        Assert.Equal(0d, actor.LaughProgress);
        Assert.Equal(visitor ? 1d : 0d, actor.VisibilityProgress);
        Assert.Equal(launchedBeforeCancel, launches.Count);
        Assert.Equal(SnowballActorPhase.Entering, other.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VisualUpdatesReachReleaseBeforeLaunchAndConsumeRemainingTime(bool visitor)
    {
        var id = Guid.NewGuid();
        var actor = new SnowballActorTimeline(id, visitor);
        var visualTime = 0d;
        var throwTime = 0d;
        var launches = 0;
        var lastPhase = SnowballActorPhase.Complete;
        var lastEnd = -1d;
        actor.Advance(1.7, launched =>
        {
            Assert.Equal(id, launched);
            Assert.Equal(SnowballActorPhase.Throwing, lastPhase);
            Assert.Equal(SnowballThrowPose.ReleaseSeconds, lastEnd, 10);
            Assert.Equal(SnowballThrowPose.ReleaseSeconds, throwTime, 10);
            Assert.Equal((visitor ? 0 : 0.4) + SnowballThrowPose.ReleaseSeconds, visualTime, 10);
            launches++;
        }, (phase, before, step) =>
        {
            Assert.True(step > 0);
            visualTime += step;
            if (phase == SnowballActorPhase.Throwing) throwTime += step;
            lastPhase = phase;
            lastEnd = before + step;
        });
        Assert.Equal(1, launches);
        Assert.Equal(1.7, visualTime, 10);
        Assert.Equal(SnowballThrowPose.DurationSeconds, throwTime, 10);
        Assert.Equal(SnowballActorPhase.WaitingForImpact, lastPhase);
        actor.Advance(0.2, _ => Assert.Fail("Duplicate release."),
            (phase, before, step) => visualTime += step);
        Assert.Equal(1.9, visualTime, 10);
    }

    [Fact]
    public void ActorsAndInvalidTimeUpdatesAreIndependent()
    {
        var actor = new SnowballActorTimeline(Guid.NewGuid(), false);
        var other = new SnowballActorTimeline(Guid.NewGuid(), false);
        foreach (var delta in new[] { double.NaN, double.PositiveInfinity, -1d, 0d })
            actor.Advance(delta, _ => Assert.Fail("Invalid time launched a snowball."));
        Assert.Equal(0, actor.VisibilityProgress);
        actor.Advance(1.3, _ => { });
        Assert.Equal(1, actor.InFlightCount);
        Assert.Equal(0, other.InFlightCount);
        Assert.Equal(SnowballActorPhase.Entering, other.Phase);
        Assert.Equal(0, other.VisibilityProgress);
    }
}
