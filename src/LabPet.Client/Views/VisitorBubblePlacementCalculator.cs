using System.Windows;

namespace LabPet.Client.Views;

/// <summary>
/// 방문 펫별 팝업 위치를 물리 픽셀 단위로 계산한다.
/// </summary>
internal static class VisitorBubblePlacementCalculator
{
    private const double ComposerGapDip = 5;
    private const double BodyRasterInsetPixels = 1;
    private const double TailLengthDip = 9;
    private const double TailStrokeInsetDip = 1;
    private const double TailHalfWidthDip = 5;
    private const double CardCornerInsetDip = 12;
    internal const double BubbleShadowInsetDip = 8;
    internal const double TailStrokeMarginDip = 2;
    internal const double MaxBubbleSurfacePhysicalExtent = 4096;

    internal readonly record struct BubblePlacement(Rect Body, Point Tip, bool IsBelowHead, double Scale);
    internal readonly record struct TailPlacement(Point BaseLeft, Point Tip, Point BaseRight);

    internal static Rect CalculateComposerPlacement(Rect nameplate, Size popup, DpiScale dpi)
    {
        dpi = NormalizeDpi(dpi);
        var safeLeft = !nameplate.IsEmpty && double.IsFinite(nameplate.Left) ? nameplate.Left : 0;
        var safeWidth = !nameplate.IsEmpty && double.IsFinite(nameplate.Width) ? Math.Max(0, nameplate.Width) : 0;
        var safeBottom = !nameplate.IsEmpty && double.IsFinite(nameplate.Bottom) ? nameplate.Bottom : 0;
        var safePopupWidth = double.IsFinite(popup.Width) ? Math.Max(0, popup.Width) : 0;
        var safePopupHeight = double.IsFinite(popup.Height) ? Math.Max(0, popup.Height) : 0;

        return new(
            new Point(
                safeLeft + (safeWidth - safePopupWidth) / 2,
                safeBottom + ComposerGapDip * dpi.DpiScaleY),
            new Size(safePopupWidth, safePopupHeight));
    }

    internal static BubblePlacement CalculateBubblePlacement(Point head, Size body, Rect workArea, DpiScale dpi)
    {
        var fallbackTip = new Point(
            double.IsFinite(head.X) ? head.X : 0,
            double.IsFinite(head.Y) ? head.Y : 0);

        if (workArea.IsEmpty
            || !double.IsFinite(workArea.Left) || !double.IsFinite(workArea.Top)
            || !double.IsFinite(workArea.Width) || !double.IsFinite(workArea.Height)
            || !double.IsFinite(workArea.Right) || !double.IsFinite(workArea.Bottom)
            || workArea.Width <= 0 || workArea.Height <= 0
            || !double.IsFinite(head.X) || !double.IsFinite(head.Y)
            || !double.IsFinite(body.Width) || !double.IsFinite(body.Height)
            || body.Width <= 0 || body.Height <= 0)
        {
            return new BubblePlacement(Rect.Empty, fallbackTip, IsBelowHead: false, Scale: 0);
        }

        // WPF의 레이아웃과 화면 좌표를 픽셀 단위로 반올림해도 카드 본문은 작업영역 안에 둔다.
        var bodyArea = workArea;
        bodyArea.Inflate(-Math.Min(BodyRasterInsetPixels, workArea.Width / 4),
            -Math.Min(BodyRasterInsetPixels, workArea.Height / 4));
        dpi = NormalizeDpi(dpi);
        var insetX = Math.Min(TailStrokeInsetDip * dpi.DpiScaleX, bodyArea.Width / 2);
        var insetY = Math.Min(TailStrokeInsetDip * dpi.DpiScaleY, bodyArea.Height / 2);
        var tip = new Point(
            SafeClamp(head.X, bodyArea.Left + insetX, bodyArea.Right - insetX),
            SafeClamp(head.Y, bodyArea.Top + insetY, bodyArea.Bottom - insetY));
        var tailLength = Math.Min(TailLengthDip * dpi.DpiScaleY, bodyArea.Height / 4);
        var above = tip.Y - tailLength - bodyArea.Top;
        var below = bodyArea.Bottom - tip.Y - tailLength;
        var widthScale = SafeClamp(bodyArea.Width / body.Width, 0, 1);
        var isBelow = body.Height * widthScale > above && below > above;
        var availableHeight = Math.Max(0, isBelow ? below : above);

        // 공간이 어느 쪽에도 충분하지 않으면 전체 스택을 한 비율로 맞춰 가까운 카드를 가리지 않는다.
        var rawScale = Math.Min(widthScale, availableHeight / body.Height);
        var scale = double.IsFinite(rawScale) ? SafeClamp(rawScale, 0, 1) : 0;
        var size = new Size(Math.Max(0, body.Width * scale), Math.Max(0, body.Height * scale));
        var x = ClampStart(tip.X - size.Width / 2, bodyArea.Left, bodyArea.Right, size.Width);
        var y = isBelow ? tip.Y + tailLength : tip.Y - tailLength - size.Height;
        if (!double.IsFinite(x) || !double.IsFinite(y)
            || !double.IsFinite(size.Width) || !double.IsFinite(size.Height)
            || !double.IsFinite(x + size.Width) || !double.IsFinite(y + size.Height))
        {
            return new BubblePlacement(Rect.Empty, fallbackTip, isBelow, Scale: 0);
        }

        // 본문 배치용 제한 좌표와 실제 머리를 가리키는 꼬리 끝점을 분리한다.
        // 꼬리 끝점은 작업영역 안으로 제한하지 않는다.
        return new BubblePlacement(new Rect(new Point(x, y), size), head, isBelow, scale);
    }

    internal static TailPlacement CalculateTailPlacement(BubblePlacement placement, Rect nearestCard, DpiScale dpi)
    {
        var tip = new Point(
            double.IsFinite(placement.Tip.X) ? placement.Tip.X : 0,
            double.IsFinite(placement.Tip.Y) ? placement.Tip.Y : 0);

        if (nearestCard.IsEmpty
            || !double.IsFinite(nearestCard.Left) || !double.IsFinite(nearestCard.Top)
            || !double.IsFinite(nearestCard.Width) || !double.IsFinite(nearestCard.Height)
            || !double.IsFinite(nearestCard.Right) || !double.IsFinite(nearestCard.Bottom)
            || nearestCard.Width <= 0 || nearestCard.Height <= 0)
        {
            return new TailPlacement(tip, tip, tip);
        }

        dpi = NormalizeDpi(dpi);
        var scale = double.IsFinite(placement.Scale) && placement.Scale >= 0 ? placement.Scale : 0;
        var inset = Math.Min(CardCornerInsetDip * dpi.DpiScaleX * scale, nearestCard.Width / 4);
        var halfWidth = Math.Min(TailHalfWidthDip * dpi.DpiScaleX * scale, nearestCard.Width / 4);
        var center = SafeClamp(tip.X, nearestCard.Left + inset + halfWidth,
            nearestCard.Right - inset - halfWidth);
        var overlap = Math.Min(TailStrokeInsetDip * dpi.DpiScaleY * scale, nearestCard.Height / 2);
        var y = placement.IsBelowHead ? nearestCard.Top + overlap : nearestCard.Bottom - overlap;
        return new TailPlacement(new Point(center - halfWidth, y), tip, new Point(center + halfWidth, y));
    }

    /// <summary>
    /// 말풍선 본문(작업영역 안쪽 그림자 여백 포함)과 작업영역 밖까지 뻗는 꼬리 전체를 담는 팝업 물리 경계를 계산한다.
    /// 꼬리 영역은 작업영역으로 잘라내지 않으며, 극단적 비정상 좌표로 인한 초대형 백버퍼 할당만 상한으로 방어한다.
    /// </summary>
    internal static Rect CalculateBubbleSurfaceBounds(
        BubblePlacement placement, TailPlacement tail, Rect workArea, DpiScale dpi)
    {
        if (placement.Body.IsEmpty || workArea.IsEmpty
            || !IsFinitePositiveRect(placement.Body) || !IsFinitePositiveRect(workArea))
        {
            return Rect.Empty;
        }

        dpi = NormalizeDpi(dpi);
        var shadowX = BubbleShadowInsetDip * dpi.DpiScaleX;
        var shadowY = BubbleShadowInsetDip * dpi.DpiScaleY;
        var bodyWithShadow = placement.Body;
        bodyWithShadow.Inflate(shadowX, shadowY);
        var bodySurface = Rect.Intersect(workArea, bodyWithShadow);
        if (!IsFinitePositiveRect(bodySurface))
        {
            bodySurface = placement.Body;
        }

        var combined = bodySurface;
        if (IsFinitePoint(tail.BaseLeft) && IsFinitePoint(tail.Tip) && IsFinitePoint(tail.BaseRight))
        {
            var tailLeft = Math.Min(tail.BaseLeft.X, Math.Min(tail.Tip.X, tail.BaseRight.X));
            var tailTop = Math.Min(tail.BaseLeft.Y, Math.Min(tail.Tip.Y, tail.BaseRight.Y));
            var tailRight = Math.Max(tail.BaseLeft.X, Math.Max(tail.Tip.X, tail.BaseRight.X));
            var tailBottom = Math.Max(tail.BaseLeft.Y, Math.Max(tail.Tip.Y, tail.BaseRight.Y));
            var strokeX = TailStrokeMarginDip * dpi.DpiScaleX;
            var strokeY = TailStrokeMarginDip * dpi.DpiScaleY;
            var left = tailLeft - strokeX;
            var top = tailTop - strokeY;
            var right = tailRight + strokeX;
            var bottom = tailBottom + strokeY;
            if (double.IsFinite(left) && double.IsFinite(top) && double.IsFinite(right) && double.IsFinite(bottom)
                && double.IsFinite(right - left) && double.IsFinite(bottom - top))
            {
                combined.Union(new Rect(new Point(left, top), new Point(right, bottom)));
            }
        }

        var snappedLeft = Math.Floor(combined.Left);
        var snappedTop = Math.Floor(combined.Top);
        var snappedRight = Math.Ceiling(combined.Right);
        var snappedBottom = Math.Ceiling(combined.Bottom);
        if (!double.IsFinite(snappedLeft) || !double.IsFinite(snappedTop)
            || !double.IsFinite(snappedRight) || !double.IsFinite(snappedBottom))
        {
            return SnapRect(bodySurface);
        }

        // 비정상적으로 먼 좌표로 인해 Layered Window가 초대형 비트맵을 할당하지 않도록 본문 기준 최대 표면 크기만 제한한다.
        var maxExtent = Math.Max(MaxBubbleSurfacePhysicalExtent, Math.Max(bodySurface.Width, bodySurface.Height) + 64);
        if (snappedRight - snappedLeft > maxExtent)
        {
            var bodyFloorLeft = Math.Floor(bodySurface.Left);
            var bodyCeilRight = Math.Ceiling(bodySurface.Right);
            if (snappedLeft < bodyFloorLeft)
            {
                snappedLeft = Math.Max(snappedLeft, bodyCeilRight - maxExtent);
            }

            if (snappedRight - snappedLeft > maxExtent)
            {
                snappedRight = snappedLeft + maxExtent;
            }
        }

        if (snappedBottom - snappedTop > maxExtent)
        {
            var bodyFloorTop = Math.Floor(bodySurface.Top);
            var bodyCeilBottom = Math.Ceiling(bodySurface.Bottom);
            if (snappedTop < bodyFloorTop)
            {
                snappedTop = Math.Max(snappedTop, bodyCeilBottom - maxExtent);
            }

            if (snappedBottom - snappedTop > maxExtent)
            {
                snappedBottom = snappedTop + maxExtent;
            }
        }

        var width = snappedRight - snappedLeft;
        var height = snappedBottom - snappedTop;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0
            || snappedLeft < int.MinValue + 1024 || snappedRight > int.MaxValue - 1024
            || snappedTop < int.MinValue + 1024 || snappedBottom > int.MaxValue - 1024)
        {
            return SnapRect(bodySurface);
        }

        return new Rect(snappedLeft, snappedTop, width, height);
    }

    internal static double SafeClamp(double value, double min, double max)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            return 0;
        }

        if (min > max)
        {
            var midpoint = min + (max - min) / 2.0;
            return double.IsFinite(midpoint) ? midpoint : 0;
        }

        if (!double.IsFinite(value))
        {
            return min;
        }

        return Math.Clamp(value, min, max);
    }

    private static Rect SnapRect(Rect rect)
    {
        if (!IsFinitePositiveRect(rect))
        {
            return Rect.Empty;
        }

        var left = Math.Floor(rect.Left);
        var top = Math.Floor(rect.Top);
        var right = Math.Ceiling(rect.Right);
        var bottom = Math.Ceiling(rect.Bottom);
        var width = right - left;
        var height = bottom - top;
        return double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0
            ? new Rect(left, top, width, height)
            : Rect.Empty;
    }

    private static bool IsFinitePoint(Point point)
        => double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static bool IsFinitePositiveRect(Rect rect)
        => !rect.IsEmpty
            && double.IsFinite(rect.Left) && double.IsFinite(rect.Top)
            && double.IsFinite(rect.Width) && double.IsFinite(rect.Height)
            && double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom)
            && rect.Width > 0 && rect.Height > 0;

    private static DpiScale NormalizeDpi(DpiScale dpi)
        => new(
            double.IsFinite(dpi.DpiScaleX) && dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0,
            double.IsFinite(dpi.DpiScaleY) && dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0);

    private static double ClampStart(double start, double min, double max, double length)
        => SafeClamp(start, min, Math.Max(min, max - length));
}
