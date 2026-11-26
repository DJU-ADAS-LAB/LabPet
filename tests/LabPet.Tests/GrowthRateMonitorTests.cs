using System;
using System.Text.Json;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Shared.Models;
using Xunit;

namespace LabPet.Tests;

public sealed class GrowthRateMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(50, 50000, false)]
    [InlineData(51, 49999, true)]
    [InlineData(50, 50001, true)]
    [InlineData(51, 50001, true)]
    public void Migration_RequiresEitherThreshold_AndRunsOnce(int level, long coins, bool reset)
    {
        var p = new PetProfile();
        p.SoloProgress.Level = level;
        p.SoloProgress.Coins = coins;
        Assert.True(JsonAppDataStore.ApplyGrowthReset(p, new Version(0, 3, 17)));
        Assert.Equal(reset ? 1 : level, p.SoloProgress.Level);
        Assert.Equal(reset ? 0L : coins, p.SoloProgress.Coins);
        p.SoloProgress.Level = 60;
        p.SoloProgress.Coins = 60000;
        Assert.False(JsonAppDataStore.ApplyGrowthReset(p, new Version(0, 3, 17)));
        Assert.Equal(60000L, p.SoloProgress.Coins);
    }

    [Fact]
    public void UnexplainedCoins_AccumulateAcrossSerialization_AndReset()
    {
        var p = new PetProfile();
        GrowthRateMonitor.Observe(p, Now);
        p.SoloProgress.Coins = 3000;
        Assert.False(GrowthRateMonitor.Observe(p, Now.AddMinutes(1)));
        p.SoloProgress = JsonSerializer.Deserialize<SoloProgress>(JsonSerializer.Serialize(p.SoloProgress))!;
        p.SoloProgress.Coins += 2001;
        Assert.True(GrowthRateMonitor.Observe(p, Now.AddMinutes(2)));
        Assert.Equal(0L, p.SoloProgress.Coins);
        Assert.Equal(1, p.SoloProgress.Level);
        Assert.Equal(1L, p.SoloProgress.GrowthAudit!.DetectionCount);
    }

    [Fact]
    public void UnexplainedLevels_ResetWithoutCoinIncrease()
    {
        var p = new PetProfile();
        GrowthRateMonitor.Observe(p, Now);
        p.SoloProgress.Level = 7;
        Assert.True(GrowthRateMonitor.Observe(p, Now));
        Assert.Equal(1, p.SoloProgress.Level);
    }

    [Fact]
    public void ApprovedRewardAndPaidLevelUps_DoNotTriggerReset()
    {
        var p = new PetProfile();
        var economy = new PetEconomyService();
        economy.AddCoins(p, 100000);
        for (var i = 0; i < 10; i++) Assert.True(economy.TryLevelUp(p, out _));
        Assert.False(GrowthRateMonitor.Observe(p, DateTimeOffset.UtcNow));
        Assert.Equal(11, p.SoloProgress.Level);
        Assert.Equal(0L, p.SoloProgress.GrowthAudit!.DetectionCount);
    }

    [Fact]
    public void ExpiredSamples_DoNotAccumulate_ButClockRollbackDoes()
    {
        var p = new PetProfile();
        GrowthRateMonitor.Observe(p, Now);
        p.SoloProgress.Coins = 3000;
        GrowthRateMonitor.Observe(p, Now);
        p.SoloProgress.Coins += 3000;
        Assert.False(GrowthRateMonitor.Observe(p, Now.AddHours(2)));
        p.SoloProgress.Coins += 2001;
        Assert.True(GrowthRateMonitor.Observe(p, Now));
    }
}
