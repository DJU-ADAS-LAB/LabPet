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
}
