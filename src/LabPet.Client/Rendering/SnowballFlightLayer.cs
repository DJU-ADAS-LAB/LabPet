using System.Windows;
using System.Windows.Controls;
using LabPet.Client.Diagnostics;

namespace LabPet.Client.Rendering;

/// <summary>한 공용 오버레이의 비행·입자 소유자. UI 스레드에서 호출하며 자체 갱신 루프는 만들지 않는다.</summary>
internal sealed class SnowballFlightLayer : IDisposable
{
    private sealed record Flight(Guid SenderId, SnowballFlightVisual Visual,
        Func<Point?>? ResolvePhysicalTarget = null, Action? OnPhysicalImpact = null,
        Func<(Point Destination, double Seconds)>? EscapeWhenLost = null)
    {
        internal Action? EndTravel;
        internal bool RedirectedOutside;
    }
    private readonly Canvas _scene;
    private readonly Dictionary<Guid, Flight> _flights = [];
    private bool _disposed;
    private int _incomingCount;
    internal int Count => _flights.Count;

    internal SnowballFlightLayer(Canvas scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        scene.Dispatcher.VerifyAccess();
        _scene = scene;
    }

    internal bool TryLaunch(Guid senderId, Guid eventId, Point startDip, Point targetDip,
        double durationSeconds, double arcHeightDip)
    {
        _scene.Dispatcher.VerifyAccess();
        if (_disposed || senderId == Guid.Empty || eventId == Guid.Empty
            || _flights.ContainsKey(eventId) || _incomingCount >= 24
            || !Valid(startDip) || !Valid(targetDip)
            || !double.IsFinite(durationSeconds) || durationSeconds < 0.5 || durationSeconds > 0.9
            || !double.IsFinite(arcHeightDip) || arcHeightDip < 0 || arcHeightDip > 10000) return false;
        var visual = new SnowballFlightVisual(startDip, targetDip, durationSeconds, arcHeightDip);
        _scene.Children.Add(visual);
        _flights.Add(eventId, new Flight(senderId, visual));
        _incomingCount++;
        return true;
    }

    internal bool TryLaunchPhysical(Guid eventId, Point start, Point target, double duration, double height,
        Func<Point, SnowballFlightPlacement?> place,
        Func<Point?>? resolveTarget, Action? onImpact, Func<Point, bool>? escaped,
        Func<(Point Destination, double Seconds)>? escapeWhenLost = null, Action? endTravel = null)
    {
        _scene.Dispatcher.VerifyAccess();
        if (_disposed || eventId == Guid.Empty || _flights.ContainsKey(eventId)
            || !Valid(start) || !Valid(target)) return false;
        var visual = new SnowballFlightVisual(start, target, duration, height, place, escaped);
        _flights.Add(eventId, new(Guid.Empty, visual, resolveTarget, onImpact, escapeWhenLost) { EndTravel = endTravel });
        return true;
    }

    /// <summary>명중 이벤트를 한 번 반환한다. 호출자가 피격 반응과 발신자 시간표에 통지한다.</summary>
    internal IReadOnlyList<Guid> Advance(double deltaSeconds, Point targetDip)
    {
        _scene.Dispatcher.VerifyAccess();
        if (_disposed || _incomingCount == 0 || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0)
            return Array.Empty<Guid>();
        List<Guid>? impacts = null;
        foreach (var pair in _flights.ToArray())
        {
            var flight = pair.Value;
            var visual = flight.Visual;
            if (visual.IsPhysical) continue;
            if (Valid(targetDip)) visual.TryRetarget(targetDip);
            if (visual.Advance(deltaSeconds)) (impacts ??= []).Add(pair.Key);
            if (visual.IsComplete) Remove(pair.Key);
        }
        return impacts is null ? Array.Empty<Guid>() : impacts;
    }

    internal void AdvancePhysical(double seconds)
    {
        _scene.Dispatcher.VerifyAccess();
        if (_disposed || _flights.Count == 0 || !double.IsFinite(seconds) || seconds <= 0) return;
        foreach (var pair in _flights.ToArray())
        {
            var flight = pair.Value;
            if (!flight.Visual.IsPhysical) continue;
            try
            {
                var target = flight.Visual.IsFlying && !flight.RedirectedOutside ? flight.ResolvePhysicalTarget?.Invoke() : null;
                if (target is { } point && Valid(point))
                {
                    flight.Visual.TrackPhysicalTarget(point);
                }
                else if (flight.Visual.IsFlying && !flight.RedirectedOutside && flight.EscapeWhenLost is { } escape)
                {
                    var (destination, duration) = escape();
                    flight.Visual.RedirectOutside(destination, duration);
                    flight.RedirectedOutside = true;
                }
                if (flight.Visual.Advance(seconds, target is { } valid && Valid(valid)))
                    flight.OnPhysicalImpact?.Invoke();
                if (!flight.Visual.IsFlying) FinishTravel(flight);
                if (flight.Visual.IsComplete) Remove(pair.Key);
            }
            catch (Exception ex)
            {
                CrashLogWriter.Write("SnowballFlight.Outgoing", ex);
                Remove(pair.Key);
            }
        }
    }

    internal void CancelSender(Guid senderId)
    {
        _scene.Dispatcher.VerifyAccess();
        foreach (var pair in _flights.ToArray())
            if (pair.Value.SenderId == senderId) Remove(pair.Key);
    }

    private void Remove(Guid eventId)
    {
        if (!_flights.Remove(eventId, out var flight)) return;
        if (!flight.Visual.IsPhysical) _incomingCount--;
        FinishTravel(flight);
        _scene.Children.Remove(flight.Visual);
        flight.Visual.Dispose();
    }

    private static void FinishTravel(Flight flight)
    {
        var end = flight.EndTravel;
        flight.EndTravel = null;
        end?.Invoke();
    }

    public void Dispose()
    {
        _scene.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        foreach (var id in _flights.Keys.ToArray()) Remove(id);
    }

    private static bool Valid(Point point) => double.IsFinite(point.X) && double.IsFinite(point.Y)
        && Math.Abs(point.X) <= 10000000 && Math.Abs(point.Y) <= 10000000;
}
