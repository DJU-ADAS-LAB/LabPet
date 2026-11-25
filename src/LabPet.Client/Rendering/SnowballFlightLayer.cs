using System.Windows;
using System.Windows.Controls;

namespace LabPet.Client.Rendering;

/// <summary>한 공용 오버레이의 비행·입자 소유자. UI 스레드에서 호출하며 자체 갱신 루프는 만들지 않는다.</summary>
internal sealed class SnowballFlightLayer : IDisposable
{
    private sealed record Flight(Guid SenderId, SnowballFlightVisual Visual);
    private readonly Canvas _scene;
    private readonly Dictionary<Guid, Flight> _flights = [];
    private bool _disposed;
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
            || _flights.ContainsKey(eventId) || _flights.Count >= 24
            || !Valid(startDip) || !Valid(targetDip)
            || !double.IsFinite(durationSeconds) || durationSeconds < 0.5 || durationSeconds > 0.9
            || !double.IsFinite(arcHeightDip) || arcHeightDip < 0 || arcHeightDip > 10000) return false;
        var visual = new SnowballFlightVisual(startDip, targetDip, durationSeconds, arcHeightDip);
        _scene.Children.Add(visual);
        _flights.Add(eventId, new Flight(senderId, visual));
        return true;
    }

    /// <summary>명중 이벤트를 한 번 반환한다. 호출자가 피격 반응과 발신자 시간표에 통지한다.</summary>
    internal IReadOnlyList<Guid> Advance(double deltaSeconds, Point targetDip)
    {
        _scene.Dispatcher.VerifyAccess();
        if (_disposed || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0)
            return Array.Empty<Guid>();
        List<Guid>? impacts = null;
        foreach (var pair in _flights.ToArray())
        {
            var visual = pair.Value.Visual;
            if (Valid(targetDip)) visual.TryRetarget(targetDip);
            if (visual.Advance(deltaSeconds)) (impacts ??= []).Add(pair.Key);
            if (visual.IsComplete) Remove(pair.Key);
        }
        return impacts is null ? Array.Empty<Guid>() : impacts;
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
        _scene.Children.Remove(flight.Visual);
        flight.Visual.Dispose();
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
