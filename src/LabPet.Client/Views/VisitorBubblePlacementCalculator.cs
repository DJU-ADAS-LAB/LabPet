using System.Windows;

namespace LabPet.Client.Views;

/// <summary>
/// 방문 펫 상단 말풍선의 화면 작업 영역 경계 보정 및 다중 방문 펫 간 충돌 회피 배치를 계산한다.
/// 모든 좌표와 크기는 물리 픽셀 기준을 사용한다.
/// </summary>
internal static class VisitorBubblePlacementCalculator
{
    internal static Point CalculatePlacement(
        Rect targetPhysicalBounds,
        Size popupPhysicalSize,
        Rect workAreaPhysical,
        IReadOnlyList<Rect>? occupiedBubbleBounds = null,
        double overlapYPhysical = 8.0,
        double gapPhysical = 8.0,
        bool allowAboveObstacles = true)
    {
        var centerX = targetPhysicalBounds.Left + (targetPhysicalBounds.Width - popupPhysicalSize.Width) * 0.5;
        var aboveY = targetPhysicalBounds.Top - popupPhysicalSize.Height + overlapYPhysical;
        var preferredRaw = new Point(centerX, aboveY);

        if (popupPhysicalSize.Width <= 0 || popupPhysicalSize.Height <= 0 || workAreaPhysical.IsEmpty)
        {
            return preferredRaw;
        }

        var preferredClamped = ClampToWorkArea(preferredRaw, popupPhysicalSize, workAreaPhysical);
        var belowY = targetPhysicalBounds.Bottom + gapPhysical;
        var sideY = targetPhysicalBounds.Top + Math.Max(0.0, (targetPhysicalBounds.Height - popupPhysicalSize.Height) * 0.25);

        var candidates = new List<Point>
        {
            preferredRaw
        };

        if (occupiedBubbleBounds is { Count: > 0 })
        {
            foreach (var other in occupiedBubbleBounds)
            {
                if (other.IsEmpty)
                {
                    continue;
                }

                // 기존 말풍선 좌측/우측/상단 회피 후보 추가
                candidates.Add(new Point(other.Left - popupPhysicalSize.Width - gapPhysical, aboveY));
                candidates.Add(new Point(other.Right + gapPhysical, aboveY));
                if (allowAboveObstacles)
                {
                    candidates.Add(new Point(centerX, other.Top - popupPhysicalSize.Height - gapPhysical));
                }
            }
        }

        // 상단 공간이 부족하거나 인접 말풍선과 겹칠 때 좌·우·하단 대체 위치 추가
        candidates.Add(new Point(targetPhysicalBounds.Left - popupPhysicalSize.Width - gapPhysical, sideY));
        candidates.Add(new Point(targetPhysicalBounds.Right + gapPhysical, sideY));
        candidates.Add(new Point(centerX, belowY));

        var allowedSelfOverlap = Math.Max(0.0, popupPhysicalSize.Width * overlapYPhysical);
        var bestPoint = preferredClamped;
        var bestScore = double.MaxValue;

        foreach (var rawCandidate in candidates)
        {
            var clamped = ClampToWorkArea(rawCandidate, popupPhysicalSize, workAreaPhysical);
            var candidateRect = new Rect(clamped, popupPhysicalSize);

            var overlapWithOthers = ComputeTotalIntersectionArea(candidateRect, occupiedBubbleBounds);
            var selfOverlap = ComputeIntersectionArea(candidateRect, targetPhysicalBounds);
            var excessSelfOverlap = Math.Max(0.0, selfOverlap - allowedSelfOverlap);
            var dx = clamped.X - preferredClamped.X;
            var dy = clamped.Y - preferredClamped.Y;
            var distanceFromPreferred = Math.Sqrt(dx * dx + dy * dy);

            var score = (overlapWithOthers * 1_000_000.0)
                + (excessSelfOverlap * 100.0)
                + (distanceFromPreferred * 0.01);

            if (score < bestScore)
            {
                bestScore = score;
                bestPoint = clamped;
            }
        }

        return bestPoint;
    }

    internal static Point CalculateStackedBubblePlacement(
        Rect petPhysicalBounds,
        Rect composerPhysicalBounds,
        Size bubblePopupPhysicalSize,
        Rect workAreaPhysical,
        IReadOnlyList<Rect>? otherOccupiedBounds = null,
        double baseOverlapYPhysical = 8.0,
        double gapPhysical = 8.0)
    {
        if (composerPhysicalBounds.IsEmpty)
        {
            return CalculatePlacement(
                petPhysicalBounds,
                bubblePopupPhysicalSize,
                workAreaPhysical,
                otherOccupiedBounds,
                baseOverlapYPhysical,
                gapPhysical);
        }

        var centerX = petPhysicalBounds.Left + (petPhysicalBounds.Width - bubblePopupPhysicalSize.Width) * 0.5;
        var composerIsAbovePet = composerPhysicalBounds.Top < petPhysicalBounds.Top;
        var preferredTopY = composerIsAbovePet
            ? composerPhysicalBounds.Top - bubblePopupPhysicalSize.Height - gapPhysical
            : petPhysicalBounds.Top - bubblePopupPhysicalSize.Height + baseOverlapYPhysical;
        var preferredRaw = new Point(centerX, preferredTopY);

        if (bubblePopupPhysicalSize.Width <= 0 || bubblePopupPhysicalSize.Height <= 0 || workAreaPhysical.IsEmpty)
        {
            return preferredRaw;
        }

        List<Rect> occupiedWithComposer = otherOccupiedBounds is { Count: > 0 }
            ? [.. otherOccupiedBounds, composerPhysicalBounds]
            : [composerPhysicalBounds];

        var preferredClamped = ClampToWorkArea(preferredRaw, bubblePopupPhysicalSize, workAreaPhysical);
        var belowY = Math.Max(petPhysicalBounds.Bottom, composerPhysicalBounds.Bottom) + gapPhysical;
        var sideY = petPhysicalBounds.Top + Math.Max(0.0, (petPhysicalBounds.Height - bubblePopupPhysicalSize.Height) * 0.25);
        var combinedLeft = Math.Min(petPhysicalBounds.Left, composerPhysicalBounds.Left);
        var combinedRight = Math.Max(petPhysicalBounds.Right, composerPhysicalBounds.Right);

        var candidates = new List<Point>
        {
            preferredRaw,
            new(combinedLeft - bubblePopupPhysicalSize.Width - gapPhysical, composerPhysicalBounds.Top),
            new(combinedRight + gapPhysical, composerPhysicalBounds.Top),
            new(combinedLeft - bubblePopupPhysicalSize.Width - gapPhysical, sideY),
            new(combinedRight + gapPhysical, sideY),
            new(centerX, belowY),
            new(centerX, composerPhysicalBounds.Bottom + gapPhysical)
        };

        foreach (var other in occupiedWithComposer)
        {
            if (other.IsEmpty)
            {
                continue;
            }

            candidates.Add(new Point(other.Left - bubblePopupPhysicalSize.Width - gapPhysical, preferredTopY));
            candidates.Add(new Point(other.Right + gapPhysical, preferredTopY));
            candidates.Add(new Point(other.Left - bubblePopupPhysicalSize.Width - gapPhysical, other.Top));
            candidates.Add(new Point(other.Right + gapPhysical, other.Top));
            candidates.Add(new Point(centerX, other.Top - bubblePopupPhysicalSize.Height - gapPhysical));
        }

        var bestPoint = preferredClamped;
        var bestScore = double.MaxValue;

        foreach (var rawCandidate in candidates)
        {
            var clamped = ClampToWorkArea(rawCandidate, bubblePopupPhysicalSize, workAreaPhysical);
            var candidateRect = new Rect(clamped, bubblePopupPhysicalSize);

            var overlapWithOccupied = ComputeTotalIntersectionArea(candidateRect, occupiedWithComposer);
            var selfOverlap = ComputeIntersectionArea(candidateRect, petPhysicalBounds);
            var dx = clamped.X - preferredClamped.X;
            var dy = clamped.Y - preferredClamped.Y;
            var distanceFromPreferred = Math.Sqrt(dx * dx + dy * dy);

            var score = (overlapWithOccupied * 1_000_000.0)
                + (selfOverlap * 100.0)
                + (distanceFromPreferred * 0.01);

            if (score < bestScore)
            {
                bestScore = score;
                bestPoint = clamped;
            }
        }

        return bestPoint;
    }

    internal static Point ToRelativePopupOffset(Point chosenScreenTopLeft, Rect targetPhysicalBounds)
        => new(chosenScreenTopLeft.X - targetPhysicalBounds.Left, chosenScreenTopLeft.Y - targetPhysicalBounds.Top);

    internal static double CalculateTailOffset(
        double targetX, double originalTipX, double cardLeft, double cardWidth, double tailWidth)
    {
        const double cornerInset = 4.0;
        var inset = Math.Min(cardWidth / 2, tailWidth / 2 + cornerInset);
        return Math.Clamp(targetX, cardLeft + inset, cardLeft + cardWidth - inset) - originalTipX;
    }

    internal static Point ClampToWorkArea(Point topLeft, Size size, Rect workArea)
    {
        var maxLeft = workArea.Right - size.Width;
        var maxTop = workArea.Bottom - size.Height;

        var clampedX = maxLeft < workArea.Left
            ? workArea.Left
            : Math.Clamp(topLeft.X, workArea.Left, maxLeft);
        var clampedY = maxTop < workArea.Top
            ? workArea.Top
            : Math.Clamp(topLeft.Y, workArea.Top, maxTop);

        return new Point(clampedX, clampedY);
    }

    private static double ComputeTotalIntersectionArea(Rect rect, IReadOnlyList<Rect>? others)
    {
        if (others is null || others.Count == 0)
        {
            return 0.0;
        }

        var sum = 0.0;
        for (var i = 0; i < others.Count; i++)
        {
            sum += ComputeIntersectionArea(rect, others[i]);
        }

        return sum;
    }

    private static double ComputeIntersectionArea(Rect a, Rect b)
    {
        if (a.IsEmpty || b.IsEmpty)
        {
            return 0.0;
        }

        var left = Math.Max(a.Left, b.Left);
        var top = Math.Max(a.Top, b.Top);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);

        if (right <= left || bottom <= top)
        {
            return 0.0;
        }

        return (right - left) * (bottom - top);
    }
}
