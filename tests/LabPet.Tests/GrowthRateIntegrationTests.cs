using System;
using System.IO;
using System.Threading.Tasks;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Shared.Models;
using Xunit;

namespace LabPet.Tests;

public sealed class GrowthRateIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TamperedCoins_CannotBeHiddenBySpendingOrReserving(bool reserve)
    {
        var p = new PetProfile();
        var economy = new PetEconomyService();
        economy.AddCoins(p, 100);
        p.SoloProgress.Coins = 10000;
        var succeeded = reserve ? economy.TryReserveWager(p, "session", 9000)
            : economy.TrySpendCoins(p, 9000);
        Assert.False(succeeded);
        Assert.Equal(0L, p.SoloProgress.Coins);
        Assert.Equal(1L, p.SoloProgress.GrowthAudit!.DetectionCount);
    }

    [Fact]
    public async Task SaveDetectsTampering_AndRestartPreservesResetAndAudit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LabPet-growth-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var store = new JsonAppDataStore(new StateMigrator(), directory))
            {
                var p = await store.LoadProfileAsync();
                new PetEconomyService().AddCoins(p, 100);
                await store.SaveProfileAsync(p);
                p.SoloProgress.Level = 99;
                p.SoloProgress.Coins = 1000000000;
                await store.SaveProfileAsync(p);
                Assert.Equal(1, p.SoloProgress.Level);
                Assert.Equal(0L, p.SoloProgress.Coins);
            }
            await using var restarted = new JsonAppDataStore(new StateMigrator(), directory);
            var loaded = await restarted.LoadProfileAsync();
            Assert.Equal(1, loaded.SoloProgress.Level);
            Assert.Equal(0L, loaded.SoloProgress.Coins);
            Assert.Equal(1L, loaded.SoloProgress.GrowthAudit!.DetectionCount);
            Assert.NotNull(loaded.SoloProgress.GrowthAudit.LastDetectionReason);
            Assert.False(loaded.RequiresGrowthRecovery);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
