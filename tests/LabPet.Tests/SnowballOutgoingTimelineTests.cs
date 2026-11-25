using System.Windows;
using LabPet.Client.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace LabPet.Tests;

public sealed class SnowballOutgoingTimelineTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1, 1.0, false)]
    [InlineData(3, 1.5, false)]
    [InlineData(6, 2.0, false)]
    [InlineData(10, 2.5, true)]
    public void SuccessfulRequestsLaunchInOrderWithAdaptiveTiming(int count, double speed, bool startsInBurst)
    {
        var run = new Run();
        var targets = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        var expected = targets.Select(run.Send).ToArray();
        run.Step(0.01);
        Assert.Equal(speed, run.Timeline.CurrentSpeed);
        Assert.Equal(startsInBurst, run.Timeline.IsBurstMode);
        run.Drain();
        Assert.Equal(expected, run.Launches.Select(item => item.Request.EventId));
        Assert.Equal(SnowballThrowPose.ReleaseSeconds / speed, run.Launches[0].At, precision: 8);
        Assert.Equal(targets, run.Launches.Select(item => item.Request.TargetPetId));
        Assert.Equal(1, run.LaughStarts);
        if (startsInBurst)
            Assert.InRange(run.Launches[1].At - run.Launches[0].At, 0.10, 0.28);
        Assert.Equal(1, run.Timeline.CurrentSpeed);
        Assert.False(run.Timeline.IsBurstMode);
        run.Report(output);
    }

    [Fact]
    public void FailedOrOutOfOrderCompletionDoesNotDuplicateOrReorderSuccessfulRequests()
    {
        var run = new Run();
        var first = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var last = Guid.NewGuid();
        var target = Guid.NewGuid();
        Assert.True(run.Queue.TryReserve(first));
        Assert.True(run.Queue.TryReserve(failed));
        Assert.True(run.Queue.TryReserve(last));
        Assert.True(run.Queue.Complete(last, target, 0));
        Assert.False(run.Queue.Complete(last, target, 0));
        Assert.False(run.Queue.TryReserve(last));
        Assert.Equal(0, run.Queue.Count);
        Assert.True(run.Queue.Complete(first, target, 0));
        Assert.Equal(1, run.Queue.Count);
        run.Queue.Release(failed);
        run.Drain();
        Assert.Equal(new[] { first, last }, run.Launches.Select(item => item.Request.EventId));
    }

    [Fact]
    public void SustainedOverloadPreservesAcceptedTargetsAndRejectsNewRequestsAtCapacity()
    {
        var run = new Run();
        var left = Guid.NewGuid();
        var right = Guid.NewGuid();
        run.Directions[left] = new(-1, 0);
        run.Directions[right] = new(1, 0);
        var expected = new List<Guid>();
        for (var i = 0; i < 40; i++)
        {
            // 연속으로 몰리는 동일 방향 요청과 반대편 요청 모두 유지한다.
            for (var j = 0; j < 4; j++) expected.Add(run.Send(i % 3 == 0 ? left : right));
            run.Step(0.08);
        }
        while (run.Queue.Count < SnowballOutgoingQueue.Capacity) expected.Add(run.Send(left));
        Assert.False(run.Queue.TryReserve(Guid.NewGuid()));
        run.Drain();
        Assert.True(run.ObservedBurst);
        Assert.Equal(expected, run.Launches.Select(item => item.Request.EventId));
        var adjacent = run.Launches.Zip(run.Launches.Skip(1)).ToArray();
        Assert.Contains(adjacent, pair => pair.First.Request.TargetPetId == pair.Second.Request.TargetPetId
            && pair.Second.At - pair.First.At < 0.28);
        Assert.All(adjacent.Where(pair => pair.First.Request.TargetPetId != pair.Second.Request.TargetPetId),
            pair => Assert.True(pair.Second.At - pair.First.At >= 0.28));
        Assert.Equal(SnowballOutgoingQueue.Capacity, run.MaximumPending);
        Assert.Equal(1, run.LaughStarts);
        run.Report(output);
    }

    [Fact]
    public void BlockingPausesCurrentShotAndNewShotCanInterruptTheFinalLaugh()
    {
        var run = new Run();
        var target = Guid.NewGuid();
        var first = run.Send(target);
        run.Step(0.32);
        for (var i = 0; i < 4; i++) run.Send(target);
        run.Step(1, blocked: true);
        Assert.Empty(run.Launches);
        Assert.Null(run.LastPose);
        Assert.Equal(1, run.Timeline.CurrentSpeed);
        run.Step(0.34);
        Assert.Empty(run.Launches);
        run.Step(0.02);
        Assert.Equal(first, Assert.Single(run.Launches).Request.EventId);
        while (!run.IsLaughing) run.Step(0.02);
        Assert.Equal(5, run.Launches.Count);
        var next = run.Send(target);
        run.Step(0.02);
        Assert.False(run.IsLaughing);
        run.Drain();
        Assert.Equal(next, run.Launches[^1].Request.EventId);
        Assert.Equal(2, run.LaughStarts);
    }

    [Fact]
    public void HitDefersFinalLaughWithoutConsumingItOrBlockingAnotherThrow()
    {
        var run = new Run();
        var target = Guid.NewGuid();
        var first = run.Send(target);
        run.Step(1.4, deferLaugh: true);
        Assert.Equal(first, Assert.Single(run.Launches).Request.EventId);
        Assert.Equal(0, run.LaughStarts);
        Assert.True(run.Timeline.HasWork);
        var next = run.Send(target);
        run.Step(1.4, deferLaugh: true);
        Assert.Equal(next, run.Launches[^1].Request.EventId);
        Assert.Equal(2, run.Launches.Count);
        Assert.Equal(0, run.LaughStarts);
        run.Drain();
        Assert.Equal(1, run.LaughStarts);
    }

    private sealed class Run
    {
        private sealed class Clock : TimeProvider
        {
            internal double Seconds;
            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
            public override long GetTimestamp() => (long)(Seconds * TimestampFrequency);
        }

        private readonly Clock _clock = new();
        private double _poseTime;
        internal SnowballOutgoingQueue Queue { get; }
        internal SnowballOutgoingTimeline Timeline { get; }
        internal Dictionary<Guid, Vector> Directions { get; } = [];
        internal List<(SnowballOutgoingRequest Request, double At, double Wait)> Launches { get; } = [];
        internal int MaximumPending { get; private set; }
        internal int LaughStarts { get; private set; }
        internal bool IsLaughing { get; private set; }
        internal bool ObservedBurst { get; private set; }
        internal SnowballThrowPose? LastPose { get; private set; }

        internal Run()
        {
            Queue = new(_clock);
            Timeline = new(Queue);
        }

        internal Guid Send(Guid target)
        {
            var id = Guid.NewGuid();
            Assert.True(Queue.TryReserve(id));
            Assert.True(Queue.Complete(id, target, _clock.GetTimestamp()));
            MaximumPending = Math.Max(MaximumPending, Queue.Count);
            return id;
        }

        internal void Step(double seconds, bool blocked = false, bool deferLaugh = false)
        {
            _poseTime = _clock.Seconds;
            _clock.Seconds += seconds;
            Timeline.Advance(seconds, blocked, request => Directions.GetValueOrDefault(request.TargetPetId, new Vector(1, 0)),
                (_, pose, laughing, elapsed) =>
                {
                    _poseTime += elapsed;
                    LastPose = pose;
                    if (laughing && !IsLaughing) LaughStarts++;
                    IsLaughing = laughing;
                    ObservedBurst |= Timeline.IsBurstMode;
                }, request => Launches.Add((request, _poseTime, _poseTime - (double)request.QueuedAt / _clock.TimestampFrequency)),
                deferLaugh);
        }

        internal void Drain()
        {
            double[] steps = [0.016, 0.043, 0.011];
            for (var frame = 0; Timeline.HasWork && frame < 12_000; frame++) Step(steps[frame % steps.Length]);
            Assert.False(Timeline.HasWork);
        }

        internal void Report(ITestOutputHelper logger) =>
            logger.WriteLine($"발사 {Launches.Count}, 중복 {Launches.Count - Launches.Select(x => x.Request.EventId).Distinct().Count()}, 최대 대기열 {MaximumPending}, 최대 대기 {Launches.Max(x => x.Wait):F3}초");
    }
}
