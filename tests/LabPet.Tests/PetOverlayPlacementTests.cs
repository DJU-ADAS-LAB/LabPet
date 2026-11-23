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
