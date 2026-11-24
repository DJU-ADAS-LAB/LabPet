using System.Windows;

namespace LabPet.Client.Views;

/// <summary>
/// 방문 펫별 팝업 위치를 물리 픽셀 단위로 계산한다.
/// </summary>
internal static class VisitorBubblePlacementCalculator
{
    private const double ComposerGapDip = 5;
    private const double TailLengthDip = 9;
    private const double TailStrokeInsetDip = 1;
    private const double TailHalfWidthDip = 5;
    private const double CardCornerInsetDip = 12;

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
            || workArea.Width <= 0 || workArea.Height <= 0
            || !double.IsFinite(head.X) || !double.IsFinite(head.Y)
            || !double.IsFinite(body.Width) || !double.IsFinite(body.Height)
            || body.Width <= 0 || body.Height <= 0)
        {
            return new BubblePlacement(Rect.Empty, fallbackTip, IsBelowHead: false, Scale: 0);
        }

        dpi = NormalizeDpi(dpi);
        var insetX = Math.Min(TailStrokeInsetDip * dpi.DpiScaleX, workArea.Width / 2);
        var insetY = Math.Min(TailStrokeInsetDip * dpi.DpiScaleY, workArea.Height / 2);
        var tip = new Point(
            SafeClamp(head.X, workArea.Left + insetX, workArea.Right - insetX),
            SafeClamp(head.Y, workArea.Top + insetY, workArea.Bottom - insetY));
        var tailLength = Math.Min(TailLengthDip * dpi.DpiScaleY, workArea.Height / 4);
        var above = tip.Y - tailLength - workArea.Top;
        var below = workArea.Bottom - tip.Y - tailLength;
        var widthScale = SafeClamp(workArea.Width / body.Width, 0, 1);
        var isBelow = body.Height * widthScale > above && below > above;
        var availableHeight = Math.Max(0, isBelow ? below : above);

        // 공간이 어느 쪽에도 충분하지 않으면 전체 스택을 한 비율로 맞춰 가까운 카드를 가리지 않는다.
        var rawScale = Math.Min(widthScale, availableHeight / body.Height);
        var scale = double.IsFinite(rawScale) ? SafeClamp(rawScale, 0, 1) : 0;
        var size = new Size(Math.Max(0, body.Width * scale), Math.Max(0, body.Height * scale));
        var x = ClampStart(tip.X - size.Width / 2, workArea.Left, workArea.Right, size.Width);
        var y = isBelow ? tip.Y + tailLength : tip.Y - tailLength - size.Height;
        if (!double.IsFinite(x) || !double.IsFinite(y)
            || !double.IsFinite(size.Width) || !double.IsFinite(size.Height))
        {
            return new BubblePlacement(Rect.Empty, tip, isBelow, Scale: 0);
        }

        return new BubblePlacement(new Rect(new Point(x, y), size), tip, isBelow, scale);
    }

    internal static TailPlacement CalculateTailPlacement(BubblePlacement placement, Rect nearestCard, DpiScale dpi)
    {
        var tip = new Point(
            double.IsFinite(placement.Tip.X) ? placement.Tip.X : 0,
            double.IsFinite(placement.Tip.Y) ? placement.Tip.Y : 0);

        if (nearestCard.IsEmpty
            || !double.IsFinite(nearestCard.Left) || !double.IsFinite(nearestCard.Top)
            || !double.IsFinite(nearestCard.Width) || !double.IsFinite(nearestCard.Height)
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

    internal static double SafeClamp(double value, double min, double max)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            return 0;
        }

        if (min > max)
        {
            return (min + max) / 2.0;
        }

        if (!double.IsFinite(value))
        {
            return min;
        }

        return Math.Clamp(value, min, max);
    }

    private static DpiScale NormalizeDpi(DpiScale dpi)
        => new(
            double.IsFinite(dpi.DpiScaleX) && dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0,
            double.IsFinite(dpi.DpiScaleY) && dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0);

    private static double ClampStart(double start, double min, double max, double length)
        => SafeClamp(start, min, Math.Max(min, max - length));
}
