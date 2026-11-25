using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    [InlineData(51, 50_001, true)]
    [InlineData(50, 50_001, false)]
    [InlineData(51, 50_000, false)]
    [InlineData(50, 50_000, false)]
    public async Task LegacyProgressReset_RequiresBothThresholdsAndRunsOnce(int level, long coins, bool reset)
    {
        var profile = new PetProfile { PetName = "이전 모찌", IsOnboardingCompleted = true };
        profile.SoloProgress.Level = level;
        profile.SoloProgress.Coins = coins;
        profile.SoloProgress.PendingGameWagers.Add("보류 세션", 10);
        profile.SoloProgress.PendingSoloLadderSession = new() { SessionId = "솔로 세션", BetCoins = 10 };
        profile.SoloProgress.TotalKeystrokes = 123;
        var path = Path.Combine(_tempDir, "profile.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(profile, ProtocolJson.StorageOptions));

        await using var store = new JsonAppDataStore(new StateMigrator(), _tempDir);
        var migrated = await store.LoadProfileAsync();
        Assert.Equal(reset ? 1 : level, migrated.SoloProgress.Level);
        Assert.Equal(reset ? 0 : coins, migrated.SoloProgress.Coins);
        Assert.Equal(reset ? 0 : 1, migrated.SoloProgress.PendingGameWagers.Count);
        Assert.Equal(reset, migrated.SoloProgress.PendingSoloLadderSession is null);
        Assert.Equal(123, migrated.SoloProgress.TotalKeystrokes);
        Assert.Equal(profile.PetId, migrated.PetId);
        Assert.Equal(profile.PetName, migrated.PetName);

        migrated.SoloProgress.Level = 60;
        migrated.SoloProgress.Coins = 200_000;
        await store.SaveProfileAsync(migrated);
        await using var restarted = new JsonAppDataStore(new StateMigrator(), _tempDir);
        var saved = await restarted.LoadProfileAsync();
        Assert.Equal(60, saved.SoloProgress.Level);
        Assert.Equal(200_000, saved.SoloProgress.Coins);
    }

    [Fact]
    public void LegacyResetDoesNotApplyWhenAnInstallationSkipsTheResetRelease()
    {
        var progress = new SoloProgress { Level = 80, Coins = 300_000 };
        Assert.True(JsonAppDataStore.ShouldResetLegacyProgress(progress, new Version(0, 3, 15, 0)));
        Assert.False(JsonAppDataStore.ShouldResetLegacyProgress(progress, new Version(0, 3, 16, 0)));
    }

    [Fact]
    public async Task ProtectedProgress_IgnoresPlaintextEditsAndAnOuterSchemaDowngrade()
    {
        await using var store = new JsonAppDataStore(new StateMigrator(), _tempDir);
        var profile = new PetProfile();
        profile.SoloProgress.Level = 25;
        profile.SoloProgress.Coins = 600;
        await store.SaveProfileAsync(profile);

        var path = Path.Combine(_tempDir, "profile.json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.False(root.ContainsKey("soloProgress"));
        Assert.True(root.ContainsKey("protectedProgressV1"));
        root["schemaVersion"] = 1;
        root["soloProgress"] = JsonNode.Parse("""{"level":99,"coins":999999}""");
        root["petName"] = "수정한 이름";
        await File.WriteAllTextAsync(path, root.ToJsonString());

        var loaded = await store.LoadProfileAsync();
        Assert.Equal(25, loaded.SoloProgress.Level);
        Assert.Equal(600, loaded.SoloProgress.Coins);
        Assert.Equal("수정한 이름", loaded.PetName);
    }

    [Fact]
    public async Task ProtectedProgress_RecoversValidatedBackupAndRejectsFlatJsonRollback()
    {
        var path = Path.Combine(_tempDir, "profile.json");
        await using var store = new JsonAppDataStore(new StateMigrator(), _tempDir);
        var profile = new PetProfile();
        profile.SoloProgress.Coins = 15;
        await store.SaveProfileAsync(profile);
        profile.SoloProgress.Coins = 5;
        await store.SaveProfileAsync(profile);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        root["protectedProgressV1"] = "AQID";
        await File.WriteAllTextAsync(path, root.ToJsonString());
        Assert.Equal(15, (await store.LoadProfileAsync()).SoloProgress.Coins);
        Assert.Single(Directory.GetFiles(_tempDir, "profile.json.rejected-*"));

        var legacy = JsonSerializer.Serialize(profile, ProtocolJson.StorageOptions);
        await File.WriteAllTextAsync(path, legacy);
        await File.WriteAllTextAsync(path + ".bak", legacy);
        await Assert.ThrowsAsync<ProfileProtectionException>(() => store.LoadProfileAsync());
        Assert.Equal(legacy, await File.ReadAllTextAsync(path));
        Assert.Equal(legacy, await File.ReadAllTextAsync(path + ".bak"));
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

    [Fact]
    public async Task SaveFriends_ProtectsPairSecretWithDpapiAndMigratesLegacyPlaintext()
    {
        var store = new JsonAppDataStore(new StateMigrator(), _tempDir);
        var plainSecret = PacketSigner.GeneratePairSecretBase64();
        var friendsStore = new FriendsStore
        {
            Friends =
            [
                new FriendEntry
                {
                    PetId = "friend-dpapi-001",
                    PetName = "보안모찌",
                    PairSecretBase64 = plainSecret
                }
            ]
        };

        // 1. 저장 시 디스크에는 DPAPI 암호문만 기록되고, 메모리 원본은 평문 유지
        await store.SaveFriendsAsync(friendsStore);
        Assert.Equal(plainSecret, friendsStore.Friends[0].PairSecretBase64);

        var friendsPath = Path.Combine(_tempDir, "friends.json");
        var rawDiskJson = await File.ReadAllTextAsync(friendsPath);
        Assert.DoesNotContain(plainSecret, rawDiskJson);
        Assert.Contains(LocalSecretProtector.ProtectedPrefix, rawDiskJson);

        // 2. 재로드 시 원래 평문 비밀키 복원
        var loaded = await store.LoadFriendsAsync();
        Assert.Single(loaded.Friends);
        Assert.Equal(plainSecret, loaded.Friends[0].PairSecretBase64);

        // 3. 구버전 평문 friends.json 로드 시 자동 DPAPI 마이그레이션
        var legacySecret = PacketSigner.GeneratePairSecretBase64();
        var legacyJson = $$"""
        {
          "schemaVersion": 1,
          "friends": [
            {
              "petId": "friend-legacy-002",
              "petName": "레거시냥이",
              "pairSecretBase64": "{{legacySecret}}"
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(friendsPath, legacyJson);

        var migrated = await store.LoadFriendsAsync();
        Assert.Single(migrated.Friends);
        Assert.Equal(legacySecret, migrated.Friends[0].PairSecretBase64);

        var migratedDiskJson = await File.ReadAllTextAsync(friendsPath);
        Assert.DoesNotContain(legacySecret, migratedDiskJson);
        Assert.Contains(LocalSecretProtector.ProtectedPrefix, migratedDiskJson);
        var backupPath = friendsPath + ".bak";
        Assert.Contains(LocalSecretProtector.ProtectedPrefix, await File.ReadAllTextAsync(backupPath));
        Assert.DoesNotContain(legacySecret, await File.ReadAllTextAsync(backupPath));

        // 본 파일이 이미 암호화되어 있어도 기존 평문 백업을 별도로 보호한다.
        await File.WriteAllTextAsync(backupPath, legacyJson);
        await store.LoadFriendsAsync();
        Assert.DoesNotContain(legacySecret, await File.ReadAllTextAsync(backupPath));
        Assert.Contains(LocalSecretProtector.ProtectedPrefix, await File.ReadAllTextAsync(backupPath));
    }

    [Fact]
    public async Task SaveFriends_PreservesUnreadableProtectedBlob()
    {
        var store = new JsonAppDataStore(new StateMigrator(), _tempDir);
        var protectedBlob = LocalSecretProtector.ProtectedPrefix + "AQID";
        await store.SaveFriendsAsync(new FriendsStore
        {
            Friends = [new FriendEntry { PetId = "friend", PairSecretBase64 = protectedBlob }]
        });
        var loaded = await store.LoadFriendsAsync();
        Assert.Equal(1, loaded.UnreadableProtectedKeys);
        Assert.Equal(protectedBlob, Assert.Single(loaded.Friends).PairSecretBase64);
        await store.SaveFriendsAsync(loaded);
        Assert.Equal(protectedBlob, Assert.Single((await store.LoadFriendsAsync()).Friends).PairSecretBase64);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
