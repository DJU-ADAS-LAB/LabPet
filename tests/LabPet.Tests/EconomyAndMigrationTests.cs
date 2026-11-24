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

public sealed class EconomyAndMigrationTests : IDisposable
{
    private readonly string _tempDir;

    public EconomyAndMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LabPet_Economy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyV1ProfileAndFriends_MigrateOncePreservingCoinsStatsAndClampingIntimacy()
    {
        var store = new JsonAppDataStore(new StateMigrator(), _tempDir);
        var profileJson = """
        {
          "schemaVersion": 1,
          "petName": "구름모찌",
          "ownerName": "테스터",
          "soloProgress": {
            "level": 37,
            "xp": 185,
            "coins": 2450,
            "totalKeystrokes": 12345,
            "totalMouseClicks": 6789,
            "totalUptimeMinutes": 600,
            "completedPomodoros": 14
          },
          "appearance": {
            "speciesId": "fluffy_cloud",
            "expressionId": "happy"
          }
        }
        """;
        var friendsJson = """
        {
          "friends": [
            { "petId": "f-max", "ownerName": "철수", "petName": "냥이", "intimacy": 15000 },
            { "petId": "f-neg", "ownerName": "영희", "petName": "몽이", "intimacy": -25 }
          ]
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "profile.json"), profileJson);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "friends.json"), friendsJson);

        var migrated = await store.LoadProfileAsync();
        Assert.Equal(2, migrated.SchemaVersion);
        Assert.Equal(1, migrated.SoloProgress.Level);
        Assert.Equal(0, migrated.SoloProgress.Xp);
        // 기존 보유 코인과 누적 활동량·외형은 보존되고 소급 지급은 없다
        Assert.Equal(2450, migrated.SoloProgress.Coins);
        Assert.Equal(12345, migrated.SoloProgress.TotalKeystrokes);
        Assert.Equal(6789, migrated.SoloProgress.TotalMouseClicks);
        Assert.Equal(600, migrated.SoloProgress.TotalUptimeMinutes);
        Assert.Equal(14, migrated.SoloProgress.CompletedPomodoros);
        Assert.Equal(0, migrated.SoloProgress.PendingKeystrokesForReward);
        Assert.Equal(0, migrated.SoloProgress.PendingMouseClicksForReward);
        Assert.Equal(0, migrated.SoloProgress.PendingUptimeMinutesForReward);
        Assert.Equal("구름모찌", migrated.PetName);
        Assert.Equal(PetSpeciesIds.FluffyCloud, migrated.Appearance.SpeciesId);

        var migratedFriends = await store.LoadFriendsAsync();
        Assert.Equal(9999, migratedFriends.Friends.First(f => f.PetId == "f-max").Intimacy);
        Assert.Equal(0, migratedFriends.Friends.First(f => f.PetId == "f-neg").Intimacy);

        // v2 마이그레이션 완료 후 수동 레벨업을 진행해 재시작해도 레벨이 초기화되지 않아야 한다
        var economy = new PetEconomyService();
        Assert.True(economy.TryLevelUp(migrated, out _));
        Assert.True(economy.TryLevelUp(migrated, out _));
        Assert.Equal(3, migrated.SoloProgress.Level);
        await store.SaveProfileAsync(migrated);

        var storeRestarted = new JsonAppDataStore(new StateMigrator(), _tempDir);
        var reloaded = await storeRestarted.LoadProfileAsync();
        Assert.Equal(3, reloaded.SoloProgress.Level);
        Assert.Equal(2200, reloaded.SoloProgress.Coins);
    }

    [Fact]
    public async Task ManualLevelUp_FollowsFormulaCapsAt99AndIsConcurrencySafe()
    {
        var economy = new PetEconomyService();
        Assert.Equal(100, PetEconomyService.GetLevelUpCost(1));
        Assert.Equal(150, PetEconomyService.GetLevelUpCost(2));
        Assert.Equal(100 + (98 - 1) * 50, PetEconomyService.GetLevelUpCost(98));
        Assert.Equal(0, PetEconomyService.GetLevelUpCost(99));

        var profile = new PetProfile { SoloProgress = { Level = 1, Coins = 240 } };
        Assert.True(economy.TryLevelUp(profile, out var first));
        Assert.Equal(2, first.NewLevel);
        Assert.Equal(100, first.CostCoins);
        Assert.Equal(140, profile.SoloProgress.Coins);
        Assert.Equal(0, profile.SoloProgress.Xp);

        // Lv.2 -> Lv.3 필요 코인 150 부족 시 실패하고 잔액 불변
        Assert.False(economy.TryLevelUp(profile, out var failed));
        Assert.Equal(LevelUpFailureReason.InsufficientCoins, failed.FailureReason);
        Assert.Equal(10, failed.ShortfallCoins);
        Assert.Equal(140, profile.SoloProgress.Coins);
        Assert.Equal(2, profile.SoloProgress.Level);

        // Lv.99 상한 도달 시 코인 차감 금지
        profile.SoloProgress.Level = 99;
        profile.SoloProgress.Coins = 500;
        Assert.False(economy.TryLevelUp(profile, out var maxFailed));
        Assert.Equal(LevelUpFailureReason.MaxLevelReached, maxFailed.FailureReason);
        Assert.Equal(500, profile.SoloProgress.Coins);
        Assert.Equal(99, profile.SoloProgress.Level);

        // 동시 레벨업 호출 시 잔액 범위 내에서만 성공 (총 150코인으로 1->2(100) 1회만 성공)
        var raceProfile = new PetProfile { SoloProgress = { Level = 1, Coins = 150 } };
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_idx => Task.Run(() => economy.TryLevelUp(raceProfile, out _))));
        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(2, raceProfile.SoloProgress.Level);
        Assert.Equal(50, raceProfile.SoloProgress.Coins);
    }

    [Fact]
    public async Task EconomyRates_PomodoroAndDailySocialCapPersistAcrossRestart()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));
        var economy = new PetEconomyService(clock, TimeSpan.FromSeconds(15));
        var store = new JsonAppDataStore(new StateMigrator(), _tempDir);
        var profile = new PetProfile { SoloProgress = { Coins = 0 } };

        // 키 199타 -> 0코인, +1타 -> +1코인
        Assert.Equal(0, economy.RecordKeystrokes(profile, 199).CoinsEarned);
        Assert.Equal(1, economy.RecordKeystrokes(profile, 1).CoinsEarned);

        // 클릭 99회 -> 0코인, +1회 -> +1코인
        Assert.Equal(0, economy.RecordMouseClicks(profile, 99).CoinsEarned);
        Assert.Equal(1, economy.RecordMouseClicks(profile, 1).CoinsEarned);

        // 활성 시간 9분 -> 0코인, +1분 -> +1코인
        Assert.Equal(0, economy.RecordActiveUptimeMinutes(profile, 9).CoinsEarned);
        Assert.Equal(1, economy.RecordActiveUptimeMinutes(profile, 1).CoinsEarned);

        // 뽀모도로 5/15/25/50분 지급량 (2/6/10/20)
        Assert.Equal(2, economy.RecordPomodoroCompleted(profile, 5).CoinsEarned);
        Assert.Equal(6, economy.RecordPomodoroCompleted(profile, 15).CoinsEarned);
        Assert.Equal(10, economy.RecordPomodoroCompleted(profile, 25).CoinsEarned);
        Assert.Equal(20, economy.RecordPomodoroCompleted(profile, 50).CoinsEarned);
        Assert.Equal(41, profile.SoloProgress.Coins);

        // 이모지 반응 0코인
        Assert.Equal(0, economy.TryGrantSocialCoinReward(profile, "f1", SocialRewardKind.QuickReaction).CoinsEarned);

        // 메시지 1코인 + 동일 친구 15초 쿨다운 + 일일 합산 상한 10코인 + 재시작 후 상한 유지
        Assert.Equal(1, economy.TryGrantSocialCoinReward(profile, "f1", SocialRewardKind.FriendMessage).CoinsEarned);
        var cdBlocked = economy.TryGrantSocialCoinReward(profile, "f1", SocialRewardKind.FriendMessage);
        Assert.True(cdBlocked.IsCooldownBlocked);
        Assert.Equal(0, cdBlocked.CoinsEarned);

        clock.Advance(TimeSpan.FromSeconds(15));
        for (var i = 2; i <= 11; i++)
        {
            var res = economy.TryGrantSocialCoinReward(profile, $"f{i}", SocialRewardKind.FriendGreeting);
            Assert.Equal(i <= 10 ? 1 : 0, res.CoinsEarned);
        }

        Assert.Equal(10, profile.SoloProgress.DailySocialRewardCoinsEarned);
        await store.SaveProfileAsync(profile);

        // 프로그램 재시작 시 일일 상한이 초기화되지 않음
        var reloaded = await store.LoadProfileAsync();
        var economyAfterRestart = new PetEconomyService(clock, TimeSpan.FromSeconds(15));
        var afterRestartRes = economyAfterRestart.TryGrantSocialCoinReward(reloaded, "new-friend", SocialRewardKind.FriendMessage);
        Assert.True(afterRestartRes.IsDailyCapReached);
        Assert.Equal(0, afterRestartRes.CoinsEarned);

        // 다음 날로 넘어가면 일일 한도가 새로 리셋됨
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, economyAfterRestart.TryGrantSocialCoinReward(reloaded, "new-friend", SocialRewardKind.FriendMessage).CoinsEarned);
    }

    [Fact]
    public void AntiAbuseFilter_And_SystemActivityMonitor_BlockMacrosHeldKeysLockSleepAndIdle()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));
        var filter = new ActivityAntiAbuseFilter(clock);

        // 소프트웨어 주입 입력(매크로) 차단
        Assert.False(filter.TryAcceptKeyDown(65, ActivityAntiAbuseFilter.LlkfInjectedFlag));
        Assert.False(filter.TryAcceptKeyDown(65, ActivityAntiAbuseFilter.LlkfLowerIlInjectedFlag));
        Assert.False(filter.TryAcceptMouseClick(ActivityAntiAbuseFilter.LlmhfInjectedFlag));
        Assert.False(filter.TryAcceptMouseClick(ActivityAntiAbuseFilter.LlmhfLowerIlInjectedFlag));

        // 키 꾹 누름(릴리즈 전 반복 WM_KEYDOWN) 차단, KeyUp 이후 허용
        Assert.True(filter.TryAcceptKeyDown(65, 0));
        Assert.False(filter.TryAcceptKeyDown(65, 0));
        filter.NotifyKeyUp(65);
        Assert.True(filter.TryAcceptKeyDown(65, 0));

        // 40ms 미만 연속 오토클릭 차단, 50ms 경과 후 허용
        Assert.True(filter.TryAcceptMouseClick(0));
        clock.Advance(TimeSpan.FromMilliseconds(20));
        Assert.False(filter.TryAcceptMouseClick(0));
        clock.Advance(TimeSpan.FromMilliseconds(30));
        Assert.True(filter.TryAcceptMouseClick(0));

        // OS 유휴 60초 이상, 잠금, 절전 상태에서 업타임 보상 차단
        var idle = TimeSpan.FromSeconds(59);
        using var monitor = new SystemActivityStateMonitor(
            idleProbe: (out TimeSpan measured) =>
            {
                measured = idle;
                return true;
            },
            hookSystemEvents: false);

        Assert.True(monitor.IsUserActiveForUptime(out _));
        idle = TimeSpan.FromSeconds(60);
        Assert.False(monitor.IsUserActiveForUptime(out _));

        idle = TimeSpan.FromSeconds(5);
        Assert.True(monitor.IsUserActiveForUptime(out _));
        monitor.SetSessionLockedForTest(true);
        Assert.False(monitor.IsUserActiveForUptime(out _));
        monitor.SetSessionLockedForTest(false);
        Assert.True(monitor.IsUserActiveForUptime(out _));

        monitor.SetSystemSuspendedForTest(true);
        Assert.False(monitor.IsUserActiveForUptime(out _));
        monitor.SetSystemSuspendedForTest(false);
        Assert.True(monitor.IsUserActiveForUptime(out _));
    }

    [Fact]
    public async Task Intimacy_Clamps0To9999_DisplaysMaxAndAllowsInteractionEvenWhenMaxed()
    {
        var store = new JsonAppDataStore(new StateMigrator(), _tempDir);
        await using var hostTransport = new LanP2pTransport(0, 0, 0);
        await using var guestTransport = new LanP2pTransport(0, 0, 0);

        var economy = new PetEconomyService();
        var hostSocial = new SocialCoordinatorService(hostTransport, store, new SoloProgressionService(economy));
        var guestStore = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempDir, "guest"));
        await using var guestSocial = new SocialCoordinatorService(guestTransport, guestStore, new SoloProgressionService());

        var secret = PacketSigner.GeneratePairSecretBase64();
        var hostProfile = new PetProfile { OwnerName = "방장", PetName = "모찌" };
        var guestProfile = new PetProfile { OwnerName = "친구", PetName = "구름이" };
        await store.SaveProfileAsync(hostProfile);
        await guestStore.SaveProfileAsync(guestProfile);
        await hostTransport.StartAsync(hostProfile, []);
        await guestTransport.StartAsync(guestProfile, []);

        var guestFriend = new FriendEntry
        {
            PetId = guestProfile.PetId,
            OwnerName = "친구",
            PetName = "구름이",
            LastKnownIp = "127.0.0.1",
            LastKnownTcpPort = guestTransport.BoundTcpPort,
            PairSecretBase64 = secret,
            Intimacy = 9998
        };
        var hostFriend = new FriendEntry
        {
            PetId = hostProfile.PetId,
            OwnerName = "방장",
            PetName = "모찌",
            LastKnownIp = "127.0.0.1",
            LastKnownTcpPort = hostTransport.BoundTcpPort,
            PairSecretBase64 = secret,
            Intimacy = 1
        };

        await store.SaveFriendsAsync(new FriendsStore { Friends = [guestFriend] });
        await guestStore.SaveFriendsAsync(new FriendsStore { Friends = [hostFriend] });
        await hostSocial.StartAsync(hostProfile);
        await guestSocial.StartAsync(guestProfile);
        hostTransport.UpdateKnownFriends([guestFriend]);
        guestTransport.UpdateKnownFriends([hostFriend]);

        // 일일 소셜 코인 상한 도달 상태에서도 친밀도 9998 -> 9999 증가 및 초과 방지 + 상호작용 정상 동작
        hostProfile.SoloProgress.DailySocialRewardDateLocal = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        hostProfile.SoloProgress.DailySocialRewardCoinsEarned = PetEconomyService.DailySocialCoinCap;

        Assert.True(await hostSocial.SendBubbleMessageAsync(guestFriend, "친밀도 맥스 도달"));
        Assert.Equal(9999, Assert.Single(hostSocial.Friends).Intimacy);
        Assert.True(await hostSocial.SendBubbleMessageAsync(guestFriend, "맥스 이후에도 대화 가능"));
        Assert.Equal(9999, Assert.Single(hostSocial.Friends).Intimacy);
        Assert.Equal(0, hostProfile.SoloProgress.Coins);

        var maxVm = new FriendListItemViewModel(Assert.Single(hostSocial.Friends), null, false, false);
        Assert.True(maxVm.IsIntimacyMax);
        Assert.Equal("친밀도 MAX", maxVm.IntimacyText);
        Assert.Contains("9,999 / 9,999", maxVm.IntimacyToolTip);
    }

    private sealed class FakeTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        public void Advance(TimeSpan delta) => _utcNow += delta;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
