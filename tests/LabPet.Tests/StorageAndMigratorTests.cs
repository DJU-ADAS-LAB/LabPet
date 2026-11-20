using System.IO;
using LabPet.Client.Network;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Client.ViewModels;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using Xunit;

namespace LabPet.Tests;

public sealed class StorageAndMigratorTests : IDisposable
{
    private readonly string _tempDir;

    public StorageAndMigratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LabPet_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task SaveAndLoadProfile_CreatesBackupAndRecoversWhenPrimaryCorrupted()
    {
        var store = new JsonAppDataStore(new StateMigrator(), _tempDir);

        var profile = new PetProfile
        {
            PetName = "구름이",
            IsOnboardingCompleted = true,
            Appearance = new PetAppearance
            {
                SpeciesId = PetSpeciesIds.FluffyCloud,
                HeadAccessoryId = "headset"
            }
        };

        // 1차 저장 -> profile.json 생성
        await store.SaveProfileAsync(profile);

        // 2차 저장 -> 직전 정상 파일이 profile.json.bak 으로 백업됨
        profile.SoloProgress.Coins = 120;
        await store.SaveProfileAsync(profile);

        var primaryPath = Path.Combine(_tempDir, "profile.json");
        var backupPath = Path.Combine(_tempDir, "profile.json.bak");
        Assert.True(File.Exists(primaryPath));
        Assert.True(File.Exists(backupPath));
        Assert.False(File.Exists(primaryPath + ".tmp"));

        // 본 파일을 강제로 손상시킴
        await File.WriteAllTextAsync(primaryPath, "{ invalid json !!!");

        // 로드 시 .bak에서 자동 복구되어야 함
        var recovered = await store.LoadProfileAsync();
        Assert.Equal("구름이", recovered.PetName);
        Assert.Equal(PetSpeciesIds.FluffyCloud, recovered.Appearance.SpeciesId);
        Assert.Equal("headset", recovered.Appearance.HeadAccessoryId);
    }

    [Fact]
    public async Task LoadProfile_MigratesLegacyFileWithoutSchemaVersion()
    {
        var legacyPath = Path.Combine(_tempDir, "profile.json");
        await File.WriteAllTextAsync(legacyPath, """{"petName":"레거시모찌"}""");

        var store = new JsonAppDataStore(new StateMigrator(), _tempDir);
        var loaded = await store.LoadProfileAsync();

        Assert.Equal(ProtocolConstants.CurrentStorageSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("레거시모찌", loaded.PetName);
        Assert.False(string.IsNullOrWhiteSpace(loaded.PetId));
        Assert.False(string.IsNullOrWhiteSpace(loaded.FriendCode));
    }

    [Theory]
    [InlineData(0.5, "50%")]
    [InlineData(1.5, "150%")]
    public async Task SavePetScale_PreservesLimitsInBothViewModels(double scale, string label)
    {
        var store = new JsonAppDataStore(new StateMigrator(), _tempDir);
        var appearanceEditor = new OnboardingViewModel(store);
        appearanceEditor.LoadFromProfile(new PetProfile { OwnerName = "하늘", PetName = "솜구름" }, isEditMode: true);
        appearanceEditor.PetScale = scale;
        await appearanceEditor.SaveCommand.ExecuteAsync(null);

        var restored = await store.LoadProfileAsync();
        await using var transport = new LanP2pTransport();
        await using var social = new SocialCoordinatorService(transport, store, new SoloProgressionService());
        var desktop = new PetWindowViewModel(store, social);
        desktop.ApplyProfile(restored);
        appearanceEditor.LoadFromProfile(restored, isEditMode: true);

        Assert.Equal(scale, restored.PetScale);
        Assert.Equal(scale, desktop.PetScale);
        Assert.Equal(label, desktop.PetScalePercentText);
        Assert.Equal(scale, appearanceEditor.PetScale);
        Assert.Equal(label, appearanceEditor.PetScalePercentText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
