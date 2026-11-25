using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabPet.Client.Network;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Client.SystemIntegration;
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
    [InlineData(51, 50_001, true, true, false)]
    [InlineData(50, 50_001, false, true, false)]
    [InlineData(51, 50_000, true, false, false)]
    [InlineData(50, 50_000, false, false, false)]
    [InlineData(49, 4_000, false, false, false)]
    [InlineData(51, 50_001, true, true, true)]
    [InlineData(49, 50_001, false, true, true)]
    [InlineData(51, 50_000, true, false, true)]
    [InlineData(50, 50_000, false, false, true)]
    public async Task GrowthReset_HasIndependentThresholdsAndDurableReceipt(int level, long coins,
        bool resetLevel, bool resetCoins, bool protectedBefore)
    {
        var profile = new PetProfile { PetName = "이전 모찌", IsOnboardingCompleted = true };
        profile.SoloProgress.Level = level;
        profile.SoloProgress.Coins = coins;
        profile.SoloProgress.PendingGameWagers.Add("보류 세션", 10);
        profile.SoloProgress.PendingSoloLadderSession = new() { SessionId = "솔로 세션", BetCoins = 10 };
        profile.SoloProgress.PendingKeystrokesForReward = 40;
        profile.SoloProgress.TotalKeystrokes = 123;
        var path = Path.Combine(_tempDir, "profile.json");
        if (protectedBefore)
        {
            await using var old = new JsonAppDataStore(new StateMigrator(), _tempDir, new Version(0, 3, 15, 0));
            await old.SaveProfileAsync(profile);
        }
        else
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(profile, ProtocolJson.StorageOptions));
        }
        var original = await File.ReadAllTextAsync(path);
        var markerPath = Path.Combine(_tempDir, "profile.protection-v1");
        var oldMarker = protectedBefore ? await File.ReadAllTextAsync(markerPath) : null;

        await using var store = new JsonAppDataStore(new StateMigrator(), _tempDir);
        var migrated = await store.LoadProfileAsync();
        Assert.Equal("0.3.16", migrated.GrowthResetReceipt);
        Assert.False(migrated.RequiresGrowthRecovery);
        Assert.Equal(resetLevel ? 1 : level, migrated.SoloProgress.Level);
        Assert.Equal(resetCoins ? 0 : coins, migrated.SoloProgress.Coins);
        Assert.Equal(resetCoins ? 0 : 1, migrated.SoloProgress.PendingGameWagers.Count);
        Assert.Equal(resetCoins, migrated.SoloProgress.PendingSoloLadderSession is null);
        Assert.Equal(resetCoins ? 0 : 40, migrated.SoloProgress.PendingKeystrokesForReward);
        Assert.Equal(123, migrated.SoloProgress.TotalKeystrokes);
        Assert.Equal(profile.PetId, migrated.PetId);
        Assert.Equal(profile.PetName, migrated.PetName);
        var primary = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.DoesNotContain(primary, entry => entry.Key.Contains("receipt", StringComparison.OrdinalIgnoreCase));

        if (oldMarker is not null)
        {
            // 원본 교체 뒤 백업과 마커를 갱신하기 전에 종료된 상태를 재현한다.
            await File.WriteAllTextAsync(path + ".bak", original);
            await File.WriteAllTextAsync(markerPath, oldMarker);
            var interrupted = await store.LoadProfileAsync();
            Assert.Equal("0.3.16", interrupted.GrowthResetReceipt);
            Assert.Equal(migrated.SoloProgress.Coins, interrupted.SoloProgress.Coins);
            Assert.NotEqual(oldMarker, await File.ReadAllTextAsync(markerPath));
        }

        if (resetCoins)
        {
            var economy = new PetEconomyService();
            Assert.False(economy.RefundWager(migrated, "보류 세션", out _));
            Assert.False(economy.SettleWager(migrated, "보류 세션", 20));
        }

        migrated.SoloProgress.Level = 60;
        migrated.SoloProgress.Coins = 200_000;
        await store.SaveProfileAsync(migrated);
        await using var restarted = new JsonAppDataStore(new StateMigrator(), _tempDir);
        var saved = await restarted.LoadProfileAsync();
        Assert.Equal(60, saved.SoloProgress.Level);
        Assert.Equal(200_000, saved.SoloProgress.Coins);

        // 검사 전의 원본·백업을 수동 복원해도 별도로 유지한 DPAPI 기록이 우선한다.
        await File.WriteAllTextAsync(path, original);
        await File.WriteAllTextAsync(path + ".bak", original);
        var afterRestore = await restarted.LoadProfileAsync();
        Assert.Equal("0.3.16", afterRestore.GrowthResetReceipt);
        Assert.Equal(60, afterRestore.SoloProgress.Level);
        Assert.Equal(200_000, afterRestore.SoloProgress.Coins);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetDoesNotRunWhenAnInstallationSkipsTheRelease(bool protectedBefore)
    {
        var profile = new PetProfile { SoloProgress = new() { Level = 80, Coins = 300_000 } };
        if (protectedBefore)
        {
            await using var old = new JsonAppDataStore(new StateMigrator(), _tempDir, new Version(0, 3, 15, 0));
            await old.SaveProfileAsync(profile);
            Assert.Null((await old.LoadProfileAsync()).GrowthResetReceipt);
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(_tempDir, "profile.json"),
                JsonSerializer.Serialize(profile, ProtocolJson.StorageOptions));
        }
        await using var later = new JsonAppDataStore(new StateMigrator(), _tempDir, new Version(0, 3, 17, 0));
        var loaded = await later.LoadProfileAsync();
        Assert.Null(loaded.GrowthResetReceipt);
        Assert.Equal(80, loaded.SoloProgress.Level);
        Assert.Equal(300_000, loaded.SoloProgress.Coins);
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
        var markerPath = Path.Combine(_tempDir, "profile.protection-v1");
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

        // profile.json과 .bak이 모두 평문(변조 코인 포함)으로 덮어쓰여도 봉인 스냅샷이 있으면 평문을 무시하고 진짜 값을 복구한다.
        profile.SoloProgress.Coins = 999_999;
        var tamperedLegacy = JsonSerializer.Serialize(profile, ProtocolJson.StorageOptions);
        await File.WriteAllTextAsync(path, tamperedLegacy);
        await File.WriteAllTextAsync(path + ".bak", tamperedLegacy);
        var recoveredFromVault = await store.LoadProfileAsync();
        Assert.Equal(15, recoveredFromVault.SoloProgress.Coins);
        var resealedPrimary = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var resealedBackup = JsonNode.Parse(await File.ReadAllTextAsync(path + ".bak"))!.AsObject();
        Assert.True(resealedPrimary.ContainsKey("protectedProgressV1"));
        Assert.True(resealedBackup.ContainsKey("protectedProgressV1"));

        // 봉인 스냅샷 없이 평문으로 바뀐 성장 값은 읽지 않고 기존 파일은 보존한다.
        await File.WriteAllTextAsync(markerPath, "true");
        await File.WriteAllTextAsync(path, tamperedLegacy);
        await File.WriteAllTextAsync(path + ".bak", tamperedLegacy);
        var limited = await store.LoadProfileAsync();
        Assert.True(limited.RequiresGrowthRecovery);
        Assert.Equal(profile.PetId, limited.PetId);
        Assert.Equal(tamperedLegacy, await File.ReadAllTextAsync(path));
        Assert.Equal(tamperedLegacy, await File.ReadAllTextAsync(path + ".bak"));
    }

    [Fact]
    public async Task ProtectedProgress_UpgradesLegacyBooleanMarkerAndRecoversAfterOldClientPlaintextOverwrite()
    {
        var path = Path.Combine(_tempDir, "profile.json");
        var markerPath = Path.Combine(_tempDir, "profile.protection-v1");
        await using var store = new JsonAppDataStore(new StateMigrator(), _tempDir);

        var profile = new PetProfile { PetName = "지존모찌", IsOnboardingCompleted = true, PetScale = 0.6 };
        profile.SoloProgress.Level = 4;
        profile.SoloProgress.Coins = 373;
        await store.SaveProfileAsync(profile);

        // v0.3.15 기존 배포 클라이언트의 4바이트 boolean 마커(true) 상태를 재현한다.
        await File.WriteAllTextAsync(markerPath, "true");

        var upgraded = await store.LoadProfileAsync();
        Assert.Equal(4, upgraded.SoloProgress.Level);
        Assert.Equal(373, upgraded.SoloProgress.Coins);
        var markerNode = JsonNode.Parse(await File.ReadAllTextAsync(markerPath))!.AsObject();
        Assert.True(markerNode.ContainsKey("protectedProgressV1"));

        // 구버전 클라이언트(<= v0.3.14)가 같은 폴더에서 실행되어 profile.json과 .bak을 모두 평문(Lv.1 / 4코인)으로 덮어쓴 상황을 재현한다.
        var downgraded = new PetProfile
        {
            PetId = profile.PetId,
            FriendCode = profile.FriendCode,
            PetName = "지존모찌",
            IsOnboardingCompleted = true,
            PetScale = 0.85
        };
        downgraded.SoloProgress.Level = 1;
        downgraded.SoloProgress.Coins = 4;
        var plaintextJson = JsonSerializer.Serialize(downgraded, ProtocolJson.StorageOptions);
        await File.WriteAllTextAsync(path, plaintextJson);
        await File.WriteAllTextAsync(path + ".bak", plaintextJson);

        // 재실행 시 오류 없이 봉인 스냅샷에서 Lv.4 / 373코인을 복원하고 외형/비보호 설정은 유지한다.
        var recovered = await store.LoadProfileAsync();
        Assert.Equal(4, recovered.SoloProgress.Level);
        Assert.Equal(373, recovered.SoloProgress.Coins);
        Assert.Equal(0.85, recovered.PetScale);
        Assert.Single(Directory.GetFiles(_tempDir, "profile.json.rejected-*"));
    }

    [Fact]
    public async Task ProtectedProgress_RejectsSealedVaultWhenPetIdDoesNotMatch()
    {
        var path = Path.Combine(_tempDir, "profile.json");
        await using var store = new JsonAppDataStore(new StateMigrator(), _tempDir);

        var original = new PetProfile { PetName = "원래펫" };
        original.SoloProgress.Level = 7;
        original.SoloProgress.Coins = 500;
        await store.SaveProfileAsync(original);

        var anotherPet = new PetProfile { PetId = Guid.NewGuid().ToString("D"), PetName = "다른펫" };
        anotherPet.SoloProgress.Coins = 1;
        var foreignJson = JsonSerializer.Serialize(anotherPet, ProtocolJson.StorageOptions);
        await File.WriteAllTextAsync(path, foreignJson);
        await File.WriteAllTextAsync(path + ".bak", foreignJson);

        var limited = await store.LoadProfileAsync();
        Assert.True(limited.RequiresGrowthRecovery);
        Assert.Equal(anotherPet.PetId, limited.PetId);
        Assert.Equal(foreignJson, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DamagedGrowthStartsInRecoveryAndRestoresFromBackupWithoutLosingNewSettings()
    {
        var path = Path.Combine(_tempDir, "profile.json");
        var backup = path + ".bak";
        var marker = Path.Combine(_tempDir, "profile.protection-v1");
        await using var store = new JsonAppDataStore(new StateMigrator(), _tempDir);
        var profile = new PetProfile { PetName = "복구모찌", IsOnboardingCompleted = true, PetOpacity = 0.8 };
        profile.SoloProgress.Level = 21;
        profile.SoloProgress.Coins = 732;
        await store.SaveProfileAsync(profile);
        var valid = await File.ReadAllTextAsync(path);
        var root = JsonNode.Parse(valid)!.AsObject();
        // 실제 DPAPI 봉인을 훼손해 형식 파싱을 통과한 뒤 복호화가 실패하게 한다.
        var ciphertext = Convert.FromBase64String(root["protectedProgressV1"]!.GetValue<string>());
        ciphertext[^8] ^= 1;
        root["protectedProgressV1"] = Convert.ToBase64String(ciphertext);
        root["requiresGrowthRecovery"] = false;
        root["growthResetReceipt"] = "0.3.16";
        root["soloProgress"] = JsonNode.Parse("""{"level":99,"coins":999999}""");
        var invalidPrimary = root.ToJsonString();
        const string invalidBackup = "{손상된 백업";
        await File.WriteAllTextAsync(path, invalidPrimary);
        await File.WriteAllTextAsync(backup, invalidBackup);
        await File.WriteAllTextAsync(marker, "true");

        var loaded = await store.LoadProfileAsync();
        Assert.True(loaded.RequiresGrowthRecovery);
        Assert.Equal(profile.PetId, loaded.PetId);
        Assert.Equal("복구모찌", loaded.PetName);
        Assert.Null(loaded.GrowthResetReceipt);
        Assert.False(await store.RetryGrowthRecoveryAsync(loaded));
        loaded.PetOpacity = 0.45;
        loaded.Appearance.HeadAccessoryId = "headset";
        await store.SaveProfileAsync(loaded);
        Assert.Equal(invalidPrimary, await File.ReadAllTextAsync(path));
        Assert.Equal(invalidBackup, await File.ReadAllTextAsync(backup));
        Assert.Equal("true", await File.ReadAllTextAsync(marker));
        var sidecar = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_tempDir, "profile.settings-recovery.json")))!.AsObject();
        Assert.False(sidecar.ContainsKey("soloProgress"));
        Assert.False(sidecar.ContainsKey("protectedProgressV1"));
        var reopened = await store.LoadProfileAsync();
        Assert.True(reopened.RequiresGrowthRecovery);
        Assert.Equal(0.45, reopened.PetOpacity);

        await File.WriteAllTextAsync(backup, valid);
        Assert.True(await store.RetryGrowthRecoveryAsync(reopened));
        Assert.False(reopened.RequiresGrowthRecovery);
        Assert.Equal(21, reopened.SoloProgress.Level);
        Assert.Equal(732, reopened.SoloProgress.Coins);
        Assert.Equal("headset", reopened.Appearance.HeadAccessoryId);
        Assert.Equal(0.45, (await store.LoadProfileAsync()).PetOpacity);
        var preserved = Assert.Single(Directory.GetFiles(_tempDir, "profile.json.rejected-*"));
        Assert.Equal(invalidPrimary, await File.ReadAllTextAsync(preserved));
    }

    [Fact]
    public async Task ConfirmedGrowthResetPreservesDamagedFilesAndProfileIdentity()
    {
        var path = Path.Combine(_tempDir, "profile.json");
        var backup = path + ".bak";
        const string original = """{"schemaVersion":2,"petId":"existing-id","petName":"복구펫","protectedProgressV1":"not-decryptable"}""";
        const string damaged = "{손상된 백업";
        await File.WriteAllTextAsync(path, original);
        await File.WriteAllTextAsync(backup, damaged);
        await using var store = new JsonAppDataStore(new StateMigrator(), _tempDir);
        var limited = await store.LoadProfileAsync();
        Assert.True(limited.RequiresGrowthRecovery);
        Assert.False(await store.RetryGrowthRecoveryAsync(limited));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.True(await store.ResetUnrecoverableGrowthAsync(limited));
        Assert.False(limited.RequiresGrowthRecovery);
        Assert.Equal("existing-id", limited.PetId);
        Assert.Equal("복구펫", limited.PetName);
        Assert.Equal(0, limited.SoloProgress.Coins);
        Assert.Equal(1, limited.SoloProgress.Level);
        Assert.Equal(original, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_tempDir, "profile.json.rejected-*"))));
        Assert.Equal(damaged, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_tempDir, "profile.json.bak.rejected-*"))));
        Assert.False(await store.ResetUnrecoverableGrowthAsync(limited));
    }

    [Fact]
    public async Task LoadProfile_RetriesTransientSharingViolationAndSucceeds()
    {
        var path = Path.Combine(_tempDir, "profile.json");
        await using var store = new JsonAppDataStore(new StateMigrator(), _tempDir);
        var profile = new PetProfile { PetName = "잠금테스트" };
        profile.SoloProgress.Coins = 77;
        await store.SaveProfileAsync(profile);

        using var lockedStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var releaseTask = Task.Run(async () =>
        {
            await Task.Delay(90);
            lockedStream.Dispose();
        });

        var loaded = await store.LoadProfileAsync();
        await releaseTask;

        Assert.Equal("잠금테스트", loaded.PetName);
        Assert.Equal(77, loaded.SoloProgress.Coins);
    }

    [Fact]
    public void StartupRegistration_PreservesInstalledEntryWhenNonInstalledClientRuns()
    {
        const string installedExe = @"C:\Users\Test\AppData\Local\LabPet\current\LabPet.Client.exe";
        const string portableExe = @"C:\Users\Test\Downloads\LabPet-Portable\LabPet.Client.exe";
        const string existingCommand = $"\"{installedExe}\" --autostart";

        Assert.Equal(installedExe, StartupRegistrationService.ExtractExecutablePath(existingCommand));
        Assert.True(StartupRegistrationService.ShouldPreserveInstalledRegistration(
            existingCommand,
            portableExe,
            p => string.Equals(p, installedExe, StringComparison.OrdinalIgnoreCase)));
        Assert.False(StartupRegistrationService.ShouldPreserveInstalledRegistration(
            existingCommand,
            installedExe,
            p => string.Equals(p, installedExe, StringComparison.OrdinalIgnoreCase)));
        Assert.False(StartupRegistrationService.ShouldPreserveInstalledRegistration(
            $"\"{portableExe}\" --autostart",
            portableExe,
            p => string.Equals(p, installedExe, StringComparison.OrdinalIgnoreCase)));
        Assert.False(StartupRegistrationService.ShouldPreserveInstalledRegistration(
            null,
            portableExe,
            p => string.Equals(p, installedExe, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void AppDataPaths_IsolatesDebugBuildsFromProductionDirectoryAndMutex()
    {
        var fakeAppData = Path.Combine(_tempDir, "AppData");
        var dataDir = AppDataPaths.ResolveDefaultDataDirectory(fakeAppData);
        var logDir = AppDataPaths.ResolveDefaultLogDirectory(fakeAppData);

#if DEBUG
        Assert.Equal("LabPet-Dev", AppDataPaths.DefaultDirectoryName);
        Assert.Equal(Path.Combine(fakeAppData, "LabPet-Dev"), dataDir);
        Assert.Equal(Path.Combine(fakeAppData, "LabPet-Dev", "logs"), logDir);
        Assert.Equal("Local\\LabPet_SingleInstance_Mutex_Dev", AppDataPaths.SingleInstanceMutexName);
#else
        Assert.Equal("LabPet", AppDataPaths.DefaultDirectoryName);
        Assert.Equal(Path.Combine(fakeAppData, "LabPet"), dataDir);
        Assert.Equal(Path.Combine(fakeAppData, "LabPet", "logs"), logDir);
        Assert.Equal("Local\\LabPet_SingleInstance_Mutex", AppDataPaths.SingleInstanceMutexName);
#endif
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
