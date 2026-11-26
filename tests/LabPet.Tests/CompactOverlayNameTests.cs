using System.Windows;
using System.Windows.Controls;
using LabPet.Client.ViewModels;
using LabPet.Client.Views;
using LabPet.Shared.Models;
using Xunit;

namespace LabPet.Tests;

[Collection("WpfUi")]
public sealed class CompactOverlayNameTests
{
    [Theory]
    [InlineData("친구", "몽실", 100, true)]
    [InlineData("테스트3", "모찌", 160, true)]
    [InlineData("테스트4", "구름", 160, true)]
    [InlineData("연구실친구", "몽실고양이", 85, true)]
    [InlineData("아주아주아주아주긴사용자이름", "매우매우매우긴펫이름", 85, true)]
    public async Task Name_UsesAvailableWidthAndKeepsBothNamesInTooltip(
        string owner, string pet, double width, bool twoLines)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var entry = new QuickOverlayEntry
            {
                DisplayTitle = $"{owner} ({pet})",
                Friend = new FriendEntry { OwnerName = owner, PetName = pet }
            };
            var text = new TextBlock { DataContext = entry, FontSize = 11, Width = width };
            CompactOverlayName.Attach(text);
            text.Measure(new Size(width, double.PositiveInfinity));
            text.Arrange(new Rect(0, 0, width, text.DesiredSize.Height));
            await WpfTestHost.FlushAsync();
            text.Measure(new Size(width, double.PositiveInfinity));
            text.Arrange(new Rect(0, 0, width, text.DesiredSize.Height));
            Assert.True(twoLines == text.Text.Contains('\n'),
                $"ExpectedTwoLines={twoLines}; Width={text.ActualWidth}; Height={text.ActualHeight}; Text={System.Text.Json.JsonSerializer.Serialize(text.Text)}; Tooltip={text.ToolTip}");
            Assert.Contains(owner, text.Text);
            Assert.Contains(pet, text.Text);
            Assert.Equal($"{owner} ({pet})", text.ToolTip);
            Assert.Equal(width, text.ActualWidth);
            Assert.InRange(text.ActualHeight, 1, 24);

            // 같은 컨트롤을 재사용해 이름이 갱신돼도 원본 툴팁과 너비를 유지한다.
            entry.Friend = new FriendEntry { OwnerName = "새친구", PetName = "새펫" };
            entry.DisplayTitle = "새친구 (새펫)";
            await WpfTestHost.FlushAsync();
            Assert.Equal("새친구 (새펫)", text.ToolTip);
            Assert.Equal(width, text.ActualWidth);
        });
    }
}
