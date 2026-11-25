using System.Windows;

namespace LabPet.Client.Rendering;

/// <summary>모든 입력·출력은 같은 모니터의 물리 픽셀 좌표다. 실제 클리핑은 오버레이가 담당한다.</summary>
internal readonly record struct SnowballSpawnGeometry(Point Edge, Vector Inward, Rect VisibleBounds, Rect HiddenBounds)
{
    internal Rect BoundsAt(double visibility)
    {
        var t = double.IsFinite(visibility) ? Math.Clamp(visibility, 0, 1) : 0;
        return new Rect(HiddenBounds.TopLeft + (VisibleBounds.TopLeft - HiddenBounds.TopLeft) * t,
            VisibleBounds.Size);
    }

    /// <summary>unit을 균등 추출하면 가장자리 길이에 비례한다. 0과 1은 같은 꼭짓점이다.</summary>
    internal static bool TryCreate(Rect monitor, Size pet, double unit, out SnowballSpawnGeometry result)
    {
        result = default;
        if (monitor.IsEmpty || pet.IsEmpty || !Finite(monitor.Left) || !Finite(monitor.Top)
            || !Finite(monitor.Right) || !Finite(monitor.Bottom)
            || !Finite(pet.Width) || !Finite(pet.Height)
            || monitor.Width <= 0 || monitor.Height <= 0 || pet.Width <= 0 || pet.Height <= 0
            || pet.Width > monitor.Width || pet.Height > monitor.Height
            || !double.IsFinite(unit) || unit < 0 || unit > 1) return false;
        var w = monitor.Width;
        var h = monitor.Height;
        var edge = PerimeterPoint(monitor, unit);
        // 인접 모서리 반경에서는 두 안쪽 법선을 혼합해 대각선으로 진입한다.
        var nx = Math.Max(0, 1 - (edge.X - monitor.Left) / (pet.Width / 2))
            - Math.Max(0, 1 - (monitor.Right - edge.X) / (pet.Width / 2));
        var ny = Math.Max(0, 1 - (edge.Y - monitor.Top) / (pet.Height / 2))
            - Math.Max(0, 1 - (monitor.Bottom - edge.Y) / (pet.Height / 2));
        var inward = new Vector(nx, ny);
        inward.Normalize();
        var visible = new Rect(edge.X - pet.Width / 2, edge.Y - pet.Height / 2, pet.Width, pet.Height);
        // 모서리에서도 사각형 면적의 절반을 화면 안에 둔다.
        if (nx != 0 && ny != 0)
        {
            var clipped = Rect.Intersect(monitor, visible);
            var a = clipped.Width / pet.Width;
            var b = clipped.Height / pet.Height;
            // (a+s)(b+s)=0.5. 모서리에서 멀어질수록 보정량이 연속적으로 0이 된다.
            var shift = Math.Max(0, (Math.Sqrt((a - b) * (a - b) + 2) - a - b) / 2);
            visible.Offset(Math.Sign(nx) * pet.Width * shift, Math.Sign(ny) * pet.Height * shift);
        }
        var hidden = visible;
        hidden.Offset(-inward.X * pet.Width * 2, -inward.Y * pet.Height * 2);
        result = new(edge, inward, visible, hidden);
        return true;
    }

    /// <summary>연속적인 둘레 좌표를 최대 48회 추출한다. 빈 영역을 우선하고 포화 시 겹침 면적을 최소화한다.</summary>
    internal static bool TrySelect(Rect monitor, Size pet, IReadOnlyList<Rect> occupied,
        Random random, out SnowballSpawnGeometry result)
    {
        ArgumentNullException.ThrowIfNull(occupied);
        ArgumentNullException.ThrowIfNull(random);
        result = default;
        // 잘못된 크기는 난수 추출 전에 거부한다.
        if (!TryCreate(monitor, pet, 0, out _)) return false;
        var bestOverlap = double.PositiveInfinity;
        for (var attempt = 0; attempt < 48; attempt++)
        {
            if (!TryCreate(monitor, pet, random.NextDouble(), out var candidate)) return false;
            var visible = Rect.Intersect(monitor, candidate.VisibleBounds);
            var overlap = 0.0;
            foreach (var bounds in occupied)
            {
                if (bounds.IsEmpty || !Finite(bounds.Left) || !Finite(bounds.Top)
                    || !Finite(bounds.Right) || !Finite(bounds.Bottom)) continue;
                var intersection = Rect.Intersect(visible, bounds);
                if (!intersection.IsEmpty) overlap += intersection.Width * intersection.Height;
            }
            if (overlap < bestOverlap)
            {
                bestOverlap = overlap;
                result = candidate;
            }
            if (overlap == 0) return true;
        }
        return double.IsFinite(bestOverlap);
    }

    private static bool Finite(double value) => double.IsFinite(value) && Math.Abs(value) <= 10000000;

    internal static Point PerimeterPoint(Rect monitor, double unit)
    {
        var w = monitor.Width;
        var h = monitor.Height;
        var distance = (unit == 1 ? 0 : unit) * (2 * (w + h));
        if (distance < w) return new(monitor.Left + distance, monitor.Top);
        if ((distance -= w) < h) return new(monitor.Right, monitor.Top + distance);
        if ((distance -= h) < w) return new(monitor.Right - distance, monitor.Bottom);
        return new(monitor.Left, monitor.Bottom - (distance - w));
    }

    internal static Point BeyondMonitor(Rect monitor, Point origin, Point last, double margin)
    {
        var travel = last - origin;
        if (travel.LengthSquared < 1) travel = new(1, 0);
        var dx = travel.X > 0 ? monitor.Right - last.X : last.X - monitor.Left;
        var dy = travel.Y > 0 ? monitor.Bottom - last.Y : last.Y - monitor.Top;
        var tx = travel.X == 0 ? double.PositiveInfinity : dx / Math.Abs(travel.X);
        var ty = travel.Y == 0 ? double.PositiveInfinity : dy / Math.Abs(travel.Y);
        var edge = last + travel * Math.Max(0, Math.Min(tx, ty));
        return tx <= ty
            ? edge + new Vector(Math.Sign(travel.X) * margin, 0)
            : edge + new Vector(0, Math.Sign(travel.Y) * margin);
    }
}
