using LabPet.Client.Formatting;
using Xunit;

namespace LabPet.Tests;

public sealed class CompactNumberFormatterTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1_000, "1K")]
    [InlineData(1_250, "1.25K")]
    [InlineData(12_500, "12.5K")]
    [InlineData(125_000, "125K")]
    [InlineData(999_999, "1M")]
    [InlineData(1_000_000, "1M")]
    [InlineData(1_250_000, "1.25M")]
    [InlineData(999_999_999, "1B")]
    [InlineData(1_000_000_000, "1B")]
    [InlineData(999_999_999_999, "1T")]
    [InlineData(1_000_000_000_000, "1T")]
    [InlineData(-1_250, "-1.25K")]
    public void Format_MatchesRoundingPromotionAndTrimmingRules(long value, string expected)
    {
        Assert.Equal(expected, CompactNumberFormatter.Format(value));
    }

    [Fact]
    public void FormatExact_IncludesThousandsSeparators()
    {
        Assert.Equal("1,250,000", CompactNumberFormatter.FormatExact(1_250_000));
        Assert.Equal("0", CompactNumberFormatter.FormatExact(0));
    }

    [Theory]
    [InlineData(45, "45분", "45분")]
    [InlineData(90, "1시간 30분", "1시간 30분 (90분)")]
    [InlineData(75_000, "1.25K시간", "1,250시간 0분 (75,000분)")]
    public void FormatUptimeDuration_PreservesUnitAndExactTooltip(long minutes, string expectedCompact, string expectedTooltip)
    {
        Assert.Equal(expectedCompact, CompactNumberFormatter.FormatUptimeDuration(minutes));
        Assert.Equal(expectedTooltip, CompactNumberFormatter.FormatExactUptimeTooltip(minutes));
    }
}
