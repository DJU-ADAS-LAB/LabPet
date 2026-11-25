using System.Windows;
using System.Windows.Media;

namespace LabPet.Client.Solo;

/// <summary>
/// 뽀모도로 원형 다이얼 슬라이더(1~60분) 및 배경 오버레이 원형 인디케이터용 각도·호(Arc) 계산 유틸리티.
/// 12시 방향을 0°로 하여 시계 방향으로 0~360°를 매핑한다.
/// </summary>
public static class PomodoroCircularDialMath
{
    public const double FullCircleDegrees = 360.0;

    /// <summary>
    /// 뽀모도로 타이머의 현재 원형 진행 비율(0.0~1.0)을 반환한다.
    /// 시작 전에는 설정된 집중 시간(분/60), 시작 후에는 현재 단계의 남은 시간 비율을 반환한다.
    /// </summary>
    public static double ComputeDisplayProgressRatio(PomodoroTimerService pomodoro)
    {
        if (!pomodoro.IsDurationLocked)
        {
            return Math.Clamp(
                (double)pomodoro.FocusMinutes / PomodoroTimerService.MaximumFocusMinutes,
                0.0,
                1.0);
        }

        var totalSeconds = Math.Max(
            1.0,
            (pomodoro.CurrentPhase == PomodoroPhase.Focus ? pomodoro.FocusMinutes : pomodoro.BreakMinutes) * 60.0);
        return Math.Clamp(pomodoro.Remaining.TotalSeconds / totalSeconds, 0.0, 1.0);
    }

    /// <summary>
    /// 원형 다이얼 중심 기준 포인터 좌표를 1~60분으로 변환한다.
    /// 드래그 중 12시 경계를 넘을 때 60↔1로 급격히 튀지 않도록 이전 분 값을 기준으로 경계 클램핑한다.
    /// </summary>
    public static int PointToFocusMinutes(Point pointer, Point center, int? previousMinutes = null)
    {
        var dx = pointer.X - center.X;
        var dy = pointer.Y - center.Y;
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001))
        {
            return Math.Clamp(
                previousMinutes ?? 25,
                PomodoroTimerService.MinimumFocusMinutes,
                PomodoroTimerService.MaximumFocusMinutes);
        }

        // 12시(0, -1) = 0°, 3시(+1, 0) = 90°, 6시(0, +1) = 180°, 9시(-1, 0) = 270°
        var angleDeg = (Math.Atan2(dx, -dy) * 180.0 / Math.PI + FullCircleDegrees) % FullCircleDegrees;
        var raw = (int)Math.Round(angleDeg / (FullCircleDegrees / PomodoroTimerService.MaximumFocusMinutes));
        var candidate = raw == 0
            ? (angleDeg >= 357.0 ? PomodoroTimerService.MaximumFocusMinutes : PomodoroTimerService.MinimumFocusMinutes)
            : Math.Clamp(raw, PomodoroTimerService.MinimumFocusMinutes, PomodoroTimerService.MaximumFocusMinutes);

        if (previousMinutes is { } prev)
        {
            if (prev >= 45 && candidate <= 15)
            {
                return PomodoroTimerService.MaximumFocusMinutes;
            }

            if (prev <= 15 && candidate >= 45)
            {
                return PomodoroTimerService.MinimumFocusMinutes;
            }
        }

        return candidate;
    }

    /// <summary>12시 방향부터 시계 방향으로 ratio(0.0~1.0)만큼 채워진 원호 Geometry를 생성한다.</summary>
    public static Geometry BuildArcGeometry(double centerX, double centerY, double radius, double ratio)
    {
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(ratio) || ratio <= 0.0001)
        {
            return Geometry.Empty;
        }

        var clamped = Math.Clamp(ratio, 0.0, 1.0);
        var start = new Point(centerX, centerY - radius);
        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };

        if (clamped >= 0.9999)
        {
            var bottom = new Point(centerX, centerY + radius);
            var size = new Size(radius, radius);
            figure.Segments.Add(new ArcSegment(bottom, size, 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true));
            figure.Segments.Add(new ArcSegment(start, size, 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true));
        }
        else
        {
            var angleRad = clamped * 2.0 * Math.PI;
            var end = new Point(
                centerX + radius * Math.Sin(angleRad),
                centerY - radius * Math.Cos(angleRad));
            figure.Segments.Add(new ArcSegment(
                end,
                new Size(radius, radius),
                0,
                isLargeArc: clamped > 0.5,
                SweepDirection.Clockwise,
                isStroked: true));
        }

        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>원호 끝점(노브 중심) 좌표를 반환한다.</summary>
    public static Point ComputeKnobCenter(double centerX, double centerY, double radius, double ratio)
    {
        var clamped = Math.Clamp(double.IsFinite(ratio) ? ratio : 0.0, 0.0, 1.0);
        var angleRad = clamped * 2.0 * Math.PI;
        return new Point(
            centerX + radius * Math.Sin(angleRad),
            centerY - radius * Math.Cos(angleRad));
    }
}
