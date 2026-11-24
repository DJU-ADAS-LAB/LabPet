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

    internal static Rect CalculateComposerPlacement(Rect nameplate, Size popup, Rect workArea, DpiScale dpi)
    {
        var gapX = ComposerGapDip * dpi.DpiScaleX;
        var gapY = ComposerGapDip * dpi.DpiScaleY;
        var x = ClampStart(nameplate.Left + (nameplate.Width - popup.Width) / 2,
            workArea.Left, workArea.Right, popup.Width);
        var below = new Rect(new Point(x, nameplate.Bottom + gapY), popup);
        if (workArea.Contains(below))
        {
            return below;
        }

        var sideY = ClampStart(nameplate.Bottom - popup.Height, workArea.Top, workArea.Bottom, popup.Height);
        var left = new Rect(new Point(nameplate.Left - gapX - popup.Width, sideY), popup);
        var right = new Rect(new Point(nameplate.Right + gapX, sideY), popup);
        var preferRight = workArea.Right - nameplate.Right >= nameplate.Left - workArea.Left;
        var nearSide = preferRight ? right : left;
        var farSide = preferRight ? left : right;
        if (workArea.Contains(nearSide))
        {
            return nearSide;
        }
        if (workArea.Contains(farSide))
        {
            return farSide;
        }

        var aboveY = ClampStart(nameplate.Top - gapY - popup.Height, workArea.Top, workArea.Bottom, popup.Height);
        return new Rect(new Point(x, aboveY), popup);
    }

    internal static BubblePlacement CalculateBubblePlacement(Point head, Size body, Rect workArea, DpiScale dpi)
    {
        var insetX = Math.Min(TailStrokeInsetDip * dpi.DpiScaleX, workArea.Width / 2);
        var insetY = Math.Min(TailStrokeInsetDip * dpi.DpiScaleY, workArea.Height / 2);
        var tip = new Point(
            Math.Clamp(head.X, workArea.Left + insetX, workArea.Right - insetX),
            Math.Clamp(head.Y, workArea.Top + insetY, workArea.Bottom - insetY));
        var tailLength = Math.Min(TailLengthDip * dpi.DpiScaleY, workArea.Height / 4);
        var above = tip.Y - tailLength - workArea.Top;
        var below = workArea.Bottom - tip.Y - tailLength;
        var widthScale = Math.Min(1, workArea.Width / body.Width);
        var isBelow = body.Height * widthScale > above && below > above;

        // 공간이 어느 쪽에도 충분하지 않으면 전체 스택을 한 비율로 맞춰 가까운 카드를 가리지 않는다.
        var scale = Math.Min(widthScale, (isBelow ? below : above) / body.Height);
        var size = new Size(body.Width * scale, body.Height * scale);
        var x = ClampStart(tip.X - size.Width / 2, workArea.Left, workArea.Right, size.Width);
        var y = isBelow ? tip.Y + tailLength : tip.Y - tailLength - size.Height;
        return new BubblePlacement(new Rect(new Point(x, y), size), tip, isBelow, scale);
    }

    internal static TailPlacement CalculateTailPlacement(BubblePlacement placement, Rect nearestCard, DpiScale dpi)
    {
        var inset = Math.Min(CardCornerInsetDip * dpi.DpiScaleX * placement.Scale, nearestCard.Width / 4);
        var halfWidth = Math.Min(TailHalfWidthDip * dpi.DpiScaleX * placement.Scale, nearestCard.Width / 4);
        var center = Math.Clamp(placement.Tip.X, nearestCard.Left + inset + halfWidth,
            nearestCard.Right - inset - halfWidth);
        var overlap = TailStrokeInsetDip * dpi.DpiScaleY * placement.Scale;
        var y = placement.IsBelowHead ? nearestCard.Top + overlap : nearestCard.Bottom - overlap;
        return new TailPlacement(new Point(center - halfWidth, y), placement.Tip, new Point(center + halfWidth, y));
    }

    private static double ClampStart(double start, double min, double max, double length)
        => Math.Clamp(start, min, Math.Max(min, max - length));
}
