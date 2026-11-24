using System.Windows;
using LabPet.Client.Views;
using Xunit;

namespace LabPet.Tests;

public sealed class PetOverlayPlacementTests
{
    [Theory]
    [InlineData(180, 220, 1.0)]
    [InlineData(214, 274, 1.25)]
    [InlineData(321, 411, 2.0)]
    public void FriendOverlayCandidates_KeepTheCardMarginOutsideThePetWindow(
        double windowWidth, double windowHeight, double dpiScale)
    {
        var dpi = new DpiScale(dpiScale, dpiScale);
        var cardMargin = new Thickness(8);
        var cardSize = new Size(262 * dpiScale, 142 * dpiScale);
        var windowSize = new Size(windowWidth * dpiScale, windowHeight * dpiScale);
        var windowBounds = new Rect(windowSize);
        var popupSize = new Size(
            cardSize.Width + (cardMargin.Left + cardMargin.Right) * dpiScale,
            cardSize.Height + (cardMargin.Top + cardMargin.Bottom) * dpiScale);

        var candidates = PetWindow.GetFriendOverlayPlacements(cardSize, windowSize, dpi, cardMargin, messageRowHeight: 78);

        Assert.Equal(4, candidates.Length);
        Assert.All(candidates, placement => Assert.False(windowBounds.IntersectsWith(new Rect(placement.Point, popupSize))));
        Assert.True(candidates[0].Point.X + popupSize.Width < windowBounds.Left);
        Assert.True(candidates[1].Point.X > windowBounds.Right);
    }

    [Fact]
    public void FriendOverlayCandidates_BottomEdgeExpandsUpwardInPlaceWithoutJumpingAbovePet()
    {
        var dpi = new DpiScale(1.0, 1.0);
        var cardMargin = new Thickness(8);
        var expandedCardSize = new Size(262, 270);
        var targetBounds = new Rect(1710, 810, 180, 220);
        var workArea = new Rect(0, 0, 1920, 1040);
        var popupSize = new Size(
            expandedCardSize.Width + cardMargin.Left + cardMargin.Right,
            expandedCardSize.Height + cardMargin.Top + cardMargin.Bottom);

        var candidates = PetWindow.GetFriendOverlayPlacements(
            expandedCardSize,
            targetBounds.Size,
            dpi,
            cardMargin,
            messageRowHeight: 0,
            targetBounds,
            workArea);

        Assert.Equal(4, candidates.Length);
        // 펫 위/아래가 아니라 기존 좌측 배치를 유지한 채 위쪽 가용 공간으로 확장되어야 한다.
        Assert.True(candidates[0].Point.X + popupSize.Width < 0);
        Assert.True(candidates[0].Point.Y < 0);

        var primaryRect = new Rect(
            targetBounds.Left + candidates[0].Point.X,
            targetBounds.Top + candidates[0].Point.Y,
            popupSize.Width,
            popupSize.Height);
        Assert.True(workArea.Contains(primaryRect), $"작업 영역 이탈: {primaryRect} vs {workArea}");
        Assert.False(targetBounds.IntersectsWith(primaryRect));
    }

    [Fact]
    public void FriendOverlayCandidates_TopAndLeftEdgesExpandIntoAvailableSpaceInPlace()
    {
        var dpi = new DpiScale(1.0, 1.0);
        var cardMargin = new Thickness(8);
        var expandedCardSize = new Size(262, 260);
        var workArea = new Rect(0, 0, 1920, 1040);
        var popupSize = new Size(
            expandedCardSize.Width + cardMargin.Left + cardMargin.Right,
            expandedCardSize.Height + cardMargin.Top + cardMargin.Bottom);

        // 1) 상단 가장자리: 아래쪽으로 가용 공간이 있으므로 상단이 잘리지 않고 아래로 확장된다.
        var topPetBounds = new Rect(1700, 4, 180, 220);
        var topCandidates = PetWindow.GetFriendOverlayPlacements(
            expandedCardSize, topPetBounds.Size, dpi, cardMargin, messageRowHeight: 0, topPetBounds, workArea);
        var topPrimaryRect = new Rect(
            topPetBounds.Left + topCandidates[0].Point.X,
            topPetBounds.Top + topCandidates[0].Point.Y,
            popupSize.Width,
            popupSize.Height);
        Assert.True(topCandidates[0].Point.X < 0);
        Assert.True(workArea.Contains(topPrimaryRect));

        // 2) 좌하단 가장자리: 왼쪽 공간이 없으면 우측을 1순위로 유지하고 위로 확장된다.
        var bottomLeftPetBounds = new Rect(10, 810, 180, 220);
        var leftEdgeCandidates = PetWindow.GetFriendOverlayPlacements(
            expandedCardSize, bottomLeftPetBounds.Size, dpi, cardMargin, messageRowHeight: 0, bottomLeftPetBounds, workArea);
        var leftEdgePrimaryRect = new Rect(
            bottomLeftPetBounds.Left + leftEdgeCandidates[0].Point.X,
            bottomLeftPetBounds.Top + leftEdgeCandidates[0].Point.Y,
            popupSize.Width,
            popupSize.Height);
        Assert.True(leftEdgeCandidates[0].Point.X > bottomLeftPetBounds.Width);
        Assert.True(workArea.Contains(leftEdgePrimaryRect));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void PetViewport_PreservesAspectAndContainsTopAccessoryAndTypingKeyboardAcrossScales(double scale)
    {
        var viewportSize = PetWindow.ComputeViewportSize(scale);
        var aspectRatio = viewportSize.Width / viewportSize.Height;

        // 50~150% 어느 배율에서도 기본 종횡비(214:224 ≈ 0.955)에서 벗어나지 않는다.
        Assert.InRange(aspectRatio, 0.93, 0.98);

        // WPF PerspectiveCamera (Horizontal FOV=36°, Position=(0, 0.30, 6.65), LookDirection=(0, -0.02, -1)) 투영 범위 검증
        const double camY = 0.30;
        const double camZ = 6.65;
        const double slopeY = -0.02;
        var tanHalfFovH = Math.Tan(36.0 * Math.PI / 360.0);

        // Z=0 평면: 산타 모자/왕관 + 점프(+2.05) ~ 바닥 그림자(-0.96)
        var centerAtBody = camY + slopeY * camZ;
        var halfVertAtBody = camZ * tanHalfFovH / aspectRatio;
        Assert.True(centerAtBody + halfVertAtBody > 2.05);
        Assert.True(centerAtBody - halfVertAtBody < -0.96);

        // Z=1.04 앞쪽 평면: 타이핑 미니 키보드 최하단(-0.98)
        var distToKeyboard = camZ - 1.04;
        var centerAtKeyboard = camY + slopeY * distToKeyboard;
        var halfVertAtKeyboard = distToKeyboard * tanHalfFovH / aspectRatio;
        Assert.True(centerAtKeyboard - halfVertAtKeyboard < -0.98);
    }
}
