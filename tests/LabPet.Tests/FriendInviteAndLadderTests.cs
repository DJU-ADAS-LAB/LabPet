using System.IO;
using System.Net;
using LabPet.Client.Games;
using LabPet.Client.Network;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Client.ViewModels;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using Xunit;

namespace LabPet.Tests;

public sealed class FriendInviteAndLadderTests : IDisposable
{
    private readonly string _rootDir;

    public FriendInviteAndLadderTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "LabPet_LadderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDir))
        {
            Directory.Delete(_rootDir, recursive: true);
        }
    }

    [Fact]
    public void FriendInvitePicker_SearchMultiSingleAndOfflineDeselectWithoutFallback()
    {
        var alice = new FriendEntry { PetId = "pet-alice", OwnerName = "앨리스", PetName = "치비냥" };
        var bob = new FriendEntry { PetId = "pet-bob", OwnerName = "밥", PetName = "모찌" };
        var carol = new FriendEntry { PetId = "pet-carol", OwnerName = "캐롤", PetName = "우사기" };

        // 1. 1:1 단일 선택 모드: 최초 미선택, 이름/펫명 검색, 오프라인 시 타인 자동 선택 없음
        var single = new FriendInvitePickerViewModel(FriendInviteSelectionMode.Single);
        single.UpdateCandidates([
            new FriendInviteCandidateInput(alice, new PeerSnapshot { PetId = alice.PetId }, null, true),
            new FriendInviteCandidateInput(bob, new PeerSnapshot { PetId = bob.PetId }, null, true)
        ]);
        Assert.Equal(0, single.SelectedCount);

        single.SearchQuery = "치비";
        var filtered = Assert.Single(single.FilteredCandidates);
        Assert.Equal(alice.PetId, filtered.PetId);
        single.ToggleCandidateCommand.Execute(filtered);
        Assert.Equal(alice.PetId, single.GetFirstSelectedEntry()?.PetId);

        // 목록 갱신(검색어 유지/해제 및 순서 변경) 시 PetId 기준으로 선택 유지
        single.SearchQuery = string.Empty;
        single.UpdateCandidates([
            new FriendInviteCandidateInput(bob, new PeerSnapshot { PetId = bob.PetId }, null, true),
            new FriendInviteCandidateInput(alice, new PeerSnapshot { PetId = alice.PetId }, null, true)
        ]);
        Assert.Equal(alice.PetId, single.GetFirstSelectedEntry()?.PetId);

        // 선택된 alice가 오프라인으로 빠지면 bob이 자동 선택되지 않아야 한다
        single.UpdateCandidates([
            new FriendInviteCandidateInput(bob, new PeerSnapshot { PetId = bob.PetId }, null, true)
        ]);
        Assert.Equal(0, single.SelectedCount);
        Assert.Null(single.GetFirstSelectedEntry());

        // 2. 단체 다중 선택 모드: 전체 선택·해제 및 선택 수 추적
        var multi = new FriendInvitePickerViewModel(FriendInviteSelectionMode.Multi, maxSelectable: 7);
        multi.UpdateCandidates([
            new FriendInviteCandidateInput(alice, new PeerSnapshot { PetId = alice.PetId }, null, true),
            new FriendInviteCandidateInput(bob, new PeerSnapshot { PetId = bob.PetId }, null, true),
            new FriendInviteCandidateInput(carol, new PeerSnapshot { PetId = carol.PetId }, null, true)
        ]);
        Assert.Equal(0, multi.SelectedCount);
        multi.SelectAllCommand.Execute(null);
        Assert.Equal(3, multi.SelectedCount);
        multi.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, multi.SelectedCount);
    }

    [Fact]
    public void SoloLadderEngine_MaintainsFourSlotsFixedPoolAndExact875ReturnRate()
    {
        Assert.Equal(4, SoloLadderEngine.SlotCount);
        Assert.Equal([10, 30, 50, 100], SoloLadderEngine.BetOptions);

        // 24가지(4!) 균등 순열 전체의 평균 반환 배율은 정확히 0.875(87.5%)여야 한다
        var pool = SoloLadderEngine.FixedOutcomes.Select(x => x.MultiplierPercent).ToArray();
        Assert.Equal([0, 0, 100, 250], pool.Order().ToArray());
        var expectedRatio = pool.Average(p => p / 100.0);
        Assert.Equal(0.875, expectedRatio, precision: 6);

        // 4,000회 무작위 생성 + 단일 경로 추적 시 모든 세션이 4칸 규칙과 유효 정산을 만족한다
        long totalBet = 0;
        long totalPayout = 0;
        for (var i = 0; i < 4000; i++)
        {
            var slot = i % SoloLadderEngine.SlotCount;
            var session = SoloLadderEngine.CreateSession(100, slot);
            Assert.True(SoloLadderEngine.IsValidSession(session));
            var (endSlot, path) = SoloLadderEngine.TraceSinglePath(session.Bridges, slot);
            Assert.InRange(endSlot, 0, 3);
            Assert.Equal(slot, (int)path[0].Column);
            Assert.Equal(endSlot, (int)path[^1].Column);
            totalBet += session.BetCoins;
            totalPayout += SoloLadderEngine.CalculatePayout(session.BetCoins, session.SlotMultipliersPercent[endSlot]);
        }

        var empiricalReturn = (double)totalPayout / totalBet;
        Assert.InRange(empiricalReturn, 0.80, 0.95);
    }

    [Fact]
    public async Task SoloLadderSession_RestoresAfterRestartWithoutRerollOrDoubleDeduct()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var store = new JsonAppDataStore(new StateMigrator(), _rootDir);
            await using var lan = new LanP2pTransport(0, 0, 0);
            var progression = new SoloProgressionService();
            var social = new SocialCoordinatorService(lan, store, progression);
            var profile = new PetProfile { OwnerName = "유저", PetName = "모찌", SoloProgress = { Coins = 200 } };
            await store.SaveProfileAsync(profile);

            using var vm1 = new CoinGamesViewModel(store, progression, social);
            vm1.BindProfile(profile);
            vm1.SoloLadderBetCoins = 50;
            vm1.SelectSoloLadderSlotCommand.Execute(0);
            await vm1.StartSoloLadderCommand.ExecuteAsync(null);

            Assert.Equal(150, profile.SoloProgress.Coins);
            var originalSession = Assert.IsType<SoloLadderPersistedSession>(vm1.ActiveSoloLadderSession);
            var (expectedEndSlot, _) = SoloLadderEngine.TraceSinglePath(originalSession.Bridges, originalSession.SelectedSlot);
            var expectedPayout = SoloLadderEngine.CalculatePayout(50, originalSession.SlotMultipliersPercent[expectedEndSlot]);

            // 앱 재시작 시뮬레이션: 새 Store/ViewModel이 기존 세션을 리롤·재차감 없이 복원하고 1회만 정산한다
            var storeReloaded = new JsonAppDataStore(new StateMigrator(), _rootDir);
            var loadedProfile = await storeReloaded.LoadProfileAsync();
            Assert.Equal(150, loadedProfile.SoloProgress.Coins);

            using var vm2 = new CoinGamesViewModel(storeReloaded, new SoloProgressionService(), social);
            vm2.BindProfile(loadedProfile);
            Assert.Equal(SoloLadderPhase.Running, vm2.SoloLadderPhase);
            Assert.Equal(originalSession.SessionId, vm2.ActiveSoloLadderSession?.SessionId);
            Assert.Equal(originalSession.SlotMultipliersPercent, vm2.ActiveSoloLadderSession?.SlotMultipliersPercent);

            await vm2.CompleteSoloLadderTraceAsync();
            await vm2.CompleteSoloLadderTraceAsync();
            Assert.Equal(150 + expectedPayout, (await storeReloaded.LoadProfileAsync()).SoloProgress.Coins);
            Assert.Null((await storeReloaded.LoadProfileAsync()).SoloProgress.PendingSoloLadderSession);
            await social.DisposeAsync();
        });
    }

    [Fact]
    public async Task InsufficientCoinInvite_ShowsShortfallUnlocksWhenCoinsAddedAndNeverExposesNumericBalance()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var hostLan = new LanP2pTransport(0, 0, 0);
            await using var guestLan = new LanP2pTransport(0, 0, 0);
            var hostStore = new JsonAppDataStore(new StateMigrator(), Path.Combine(_rootDir, "insufficient_host"));
            var guestStore = new JsonAppDataStore(new StateMigrator(), Path.Combine(_rootDir, "insufficient_guest"));

            var progression = new SoloProgressionService();
            await using var hostSocial = new SocialCoordinatorService(hostLan, hostStore, progression);
            await using var guestSocial = new SocialCoordinatorService(guestLan, guestStore, new SoloProgressionService());

            var secret = PacketSigner.GeneratePairSecretBase64();
            var hostProfile = new PetProfile { OwnerName = "방장", PetName = "모찌", SoloProgress = { Coins = 200 } };
            var guestProfile = new PetProfile { OwnerName = "친구", PetName = "몽실", SoloProgress = { Coins = 12 } };
            await hostStore.SaveProfileAsync(hostProfile);
            await guestStore.SaveProfileAsync(guestProfile);
            await hostLan.StartAsync(hostProfile, []);
            await guestLan.StartAsync(guestProfile, []);

            var guestEntry = new FriendEntry { PetId = guestProfile.PetId, OwnerName = "친구", PetName = "몽실", LastKnownIp = "127.0.0.1", LastKnownTcpPort = guestLan.BoundTcpPort, PairSecretBase64 = secret };
            var hostEntry = new FriendEntry { PetId = hostProfile.PetId, OwnerName = "방장", PetName = "모찌", LastKnownIp = "127.0.0.1", LastKnownTcpPort = hostLan.BoundTcpPort, PairSecretBase64 = secret };
            await hostStore.SaveFriendsAsync(new FriendsStore { Friends = [guestEntry] });
            await guestStore.SaveFriendsAsync(new FriendsStore { Friends = [hostEntry] });
            await hostSocial.StartAsync(hostProfile);
            await guestSocial.StartAsync(guestProfile);
            hostLan.UpdateKnownFriends([guestEntry]);
            guestLan.UpdateKnownFriends([hostEntry]);
            hostLan.HandleDiscoveryHello(new DiscoveryHelloPacket { PetId = guestProfile.PetId, OwnerName = "친구", PetName = "몽실", TcpPort = guestLan.BoundTcpPort, AppVersion = "0.2.0" }, IPAddress.Loopback.ToString(), CancellationToken.None);
            guestLan.HandleDiscoveryHello(new DiscoveryHelloPacket { PetId = hostProfile.PetId, OwnerName = "방장", PetName = "모찌", TcpPort = hostLan.BoundTcpPort, AppVersion = "0.2.0" }, IPAddress.Loopback.ToString(), CancellationToken.None);

            var observedReasons = new List<string?>();
            hostLan.GameEventReceived += (_, e) => observedReasons.Add(e.Payload.InviteReason);

            using var hostGames = new CoinGamesViewModel(hostStore, progression, hostSocial);
            using var guestGames = new CoinGamesViewModel(guestStore, progression, guestSocial);
            hostGames.BindProfile(hostProfile);
            guestGames.BindProfile(guestProfile);

            await UntilAsync(() => hostGames.OnlineFriends.Count == 1);
            hostGames.SelectedRpsFriend = hostGames.OnlineFriends.Single();
            hostGames.RpsWager = 30;
            await hostGames.SendRpsInviteCommand.ExecuteAsync(null);
            await UntilAsync(() => guestGames.HasIncomingInvite);

            // 부족한 코인 상세 정보 표시 + 수락 비활성화 + 숫자 잔액 "12" 미노출
            Assert.True(guestGames.ShowIncomingInviteCoinDetails);
            Assert.Equal(30, guestGames.IncomingInviteRequiredCoins);
            Assert.Equal(12, guestGames.IncomingInviteCurrentCoins);
            Assert.Equal(18, guestGames.IncomingInviteShortfallCoins);
            Assert.False(guestGames.CanAcceptIncomingInvite);
            Assert.DoesNotContain("12", hostGames.RpsWaitState.StatusDetailText);

            // 초대 대기 중 코인을 획득하면 즉시 수락 활성화 & 실제 매치 진입 가능
            progression.AddCoins(guestProfile, 25);
            await UntilAsync(() => guestGames.CanAcceptIncomingInvite);
            Assert.True(guestGames.CanAcceptIncomingInvite);

            await guestGames.AcceptIncomingInviteCommand.ExecuteAsync(null);
            await UntilAsync(() => hostGames.RpsPhase == RpsPhase.Choosing && guestGames.RpsPhase == RpsPhase.Choosing);
            Assert.Equal(7, guestProfile.SoloProgress.Coins);
            Assert.All(observedReasons, r => Assert.DoesNotContain("12", r ?? string.Empty));
        });
    }

    [Fact]
    public async Task GroupLadder_LobbyNineStates_IndividualCancel_ReadjustAgree_AndTimersWorkCorrectly()
    {
        // 타이머 및 상태 라벨 상수 확인
        Assert.Equal(20, LabLadderEngine.InviteResponseTimeoutSeconds);
        Assert.Equal(60, LabLadderEngine.HostConfirmTimeoutSeconds);
        Assert.Equal(30, LabLadderEngine.SlotSelectionTimeoutSeconds);
        Assert.Equal(10, LabLadderEngine.ReconnectGraceTimeoutSeconds);
        Assert.Equal(9, Enum.GetValues<LadderParticipantStatus>().Length);

        await WpfTestHost.RunAsync(async () =>
        {
            await using var hostLan = new LanP2pTransport(0, 0, 0);
            await using var g1Lan = new LanP2pTransport(0, 0, 0);
            await using var g2Lan = new LanP2pTransport(0, 0, 0);

            var hStore = new JsonAppDataStore(new StateMigrator(), Path.Combine(_rootDir, "gl_h"));
            var g1Store = new JsonAppDataStore(new StateMigrator(), Path.Combine(_rootDir, "gl_g1"));
            var g2Store = new JsonAppDataStore(new StateMigrator(), Path.Combine(_rootDir, "gl_g2"));

            await using var hSocial = new SocialCoordinatorService(hostLan, hStore, new SoloProgressionService());
            await using var g1Social = new SocialCoordinatorService(g1Lan, g1Store, new SoloProgressionService());
            await using var g2Social = new SocialCoordinatorService(g2Lan, g2Store, new SoloProgressionService());

            var secret1 = PacketSigner.GeneratePairSecretBase64();
            var secret2 = PacketSigner.GeneratePairSecretBase64();
            var hp = new PetProfile { OwnerName = "방장", PetName = "모찌", SoloProgress = { Coins = 100 } };
            var g1p = new PetProfile { OwnerName = "친구1", PetName = "냐옹", SoloProgress = { Coins = 100 } };
            var g2p = new PetProfile { OwnerName = "친구2", PetName = "몽이", SoloProgress = { Coins = 0 } };
            await hStore.SaveProfileAsync(hp);
            await g1Store.SaveProfileAsync(g1p);
            await g2Store.SaveProfileAsync(g2p);
            await hostLan.StartAsync(hp, []);
            await g1Lan.StartAsync(g1p, []);
            await g2Lan.StartAsync(g2p, []);

            var f1 = new FriendEntry { PetId = g1p.PetId, OwnerName = "친구1", PetName = "냐옹", LastKnownIp = "127.0.0.1", LastKnownTcpPort = g1Lan.BoundTcpPort, PairSecretBase64 = secret1 };
            var f2 = new FriendEntry { PetId = g2p.PetId, OwnerName = "친구2", PetName = "몽이", LastKnownIp = "127.0.0.1", LastKnownTcpPort = g2Lan.BoundTcpPort, PairSecretBase64 = secret2 };
            var hFor1 = new FriendEntry { PetId = hp.PetId, OwnerName = "방장", PetName = "모찌", LastKnownIp = "127.0.0.1", LastKnownTcpPort = hostLan.BoundTcpPort, PairSecretBase64 = secret1 };
            var hFor2 = new FriendEntry { PetId = hp.PetId, OwnerName = "방장", PetName = "모찌", LastKnownIp = "127.0.0.1", LastKnownTcpPort = hostLan.BoundTcpPort, PairSecretBase64 = secret2 };

            await hStore.SaveFriendsAsync(new FriendsStore { Friends = [f1, f2] });
            await g1Store.SaveFriendsAsync(new FriendsStore { Friends = [hFor1] });
            await g2Store.SaveFriendsAsync(new FriendsStore { Friends = [hFor2] });
            await hSocial.StartAsync(hp);
            await g1Social.StartAsync(g1p);
            await g2Social.StartAsync(g2p);
            hostLan.UpdateKnownFriends([f1, f2]);
            g1Lan.UpdateKnownFriends([hFor1]);
            g2Lan.UpdateKnownFriends([hFor2]);

            void DiscoverAll()
            {
                hostLan.HandleDiscoveryHello(new DiscoveryHelloPacket { PetId = g1p.PetId, OwnerName = "친구1", PetName = "냐옹", TcpPort = g1Lan.BoundTcpPort, AppVersion = "0.2.0" }, "127.0.0.1", CancellationToken.None);
                hostLan.HandleDiscoveryHello(new DiscoveryHelloPacket { PetId = g2p.PetId, OwnerName = "친구2", PetName = "몽이", TcpPort = g2Lan.BoundTcpPort, AppVersion = "0.2.0" }, "127.0.0.1", CancellationToken.None);
                g1Lan.HandleDiscoveryHello(new DiscoveryHelloPacket { PetId = hp.PetId, OwnerName = "방장", PetName = "모찌", TcpPort = hostLan.BoundTcpPort, AppVersion = "0.2.0" }, "127.0.0.1", CancellationToken.None);
                g2Lan.HandleDiscoveryHello(new DiscoveryHelloPacket { PetId = hp.PetId, OwnerName = "방장", PetName = "모찌", TcpPort = hostLan.BoundTcpPort, AppVersion = "0.2.0" }, "127.0.0.1", CancellationToken.None);
            }
            DiscoverAll();

            using var hVm = new CoinGamesViewModel(hStore, new SoloProgressionService(), hSocial);
            using var g1Vm = new CoinGamesViewModel(g1Store, new SoloProgressionService(), g1Social);
            using var g2Vm = new CoinGamesViewModel(g2Store, new SoloProgressionService(), g2Social);
            hVm.BindProfile(hp);
            g1Vm.BindProfile(g1p);
            g2Vm.BindProfile(g2p);

            await UntilAsync(() => hVm.OnlineFriends.Count == 2);
            hVm.LadderFriendPicker.SelectAllCommand.Execute(null);
            hVm.ApplyCoffeePresetCommand.Execute(null);
            hVm.LadderEntryFee = 10;
            await hVm.OpenLadderRoomCommand.ExecuteAsync(null);
            await UntilAsync(() => g1Vm.HasIncomingInvite && g2Vm.HasIncomingInvite);

            // 친구2 코인 부족 상태 반영 + 방장 개별 초대 취소
            await UntilAsync(() => hVm.LadderParticipantCards.Any(c => c.PetId == g2p.PetId && c.Status == LadderParticipantStatus.InsufficientCoins));
            await hVm.CancelSingleLadderInviteCommand.ExecuteAsync(g2p.PetId);
            await UntilAsync(() => !g2Vm.HasIncomingInvite && hVm.LadderParticipantCards.Any(c => c.PetId == g2p.PetId && c.Status == LadderParticipantStatus.Cancelled));

            // 친구1 수락 -> 전체 인원 2명 vs 기존 상품 3개 불일치 -> 상품 재조정 및 게스트 동의 후 확정 가능
            await g1Vm.AcceptIncomingInviteCommand.ExecuteAsync(null);
            await UntilAsync(() => hVm.RequiresHostLobbyPrizeAdjustment);
            Assert.False(hVm.CanConfirmLadderParticipants);

            hVm.ApplyCoffeePresetCommand.Execute(null);
            await hVm.ApplyLobbyPrizeAdjustmentCommand.ExecuteAsync(null);
            await UntilAsync(() => g1Vm.RequiresGuestPrizeAgreement);
            Assert.False(hVm.CanConfirmLadderParticipants);

            await g1Vm.AgreeAdjustedPrizesCommand.ExecuteAsync(null);
            await UntilAsync(() => hVm.CanConfirmLadderParticipants);
            await hVm.ConfirmLadderParticipantsCommand.ExecuteAsync(null);
            await UntilAsync(() => hVm.IsLadderPickingPhase && g1Vm.IsLadderPickingPhase);

            // 위치 선택 후 게임 시작 -> Running 이후 취소 시도는 완전히 차단되어야 함
            await hVm.PickLadderSlotCommand.ExecuteAsync(0);
            await g1Vm.PickLadderSlotCommand.ExecuteAsync(1);
            await UntilAsync(() => hVm.CanStartLadderGame);
            await hVm.StartLadderRoomCommand.ExecuteAsync(null);
            Assert.True(hVm.IsLadderAnimating && g1Vm.IsLadderAnimating);
            Assert.False(hVm.CanCancelLadderRoom);
            Assert.False(g1Vm.CanCancelLadderRoom);
            await hVm.CancelLadderRoomCommand.ExecuteAsync(null);
            Assert.True(hVm.IsLadderAnimating);

            await Task.WhenAll(hVm.CompleteLadderRevealAsync(), g1Vm.CompleteLadderRevealAsync());
            Assert.Equal(LadderStage.Result, hVm.LadderStage);

            // 위치 선택 시간(30초 정책 단축 테스트) 초과 시 자동 취소 및 1회 환불
            hVm.ResetLadderToSetupCommand.Execute(null);
            g1Vm.ResetLadderToSetupCommand.Execute(null);
            hVm.SlotSelectionTimeoutOverrideSeconds = 0.25;
            hVm.LadderFriendPicker.SetSelection(g1p.PetId, true, raiseEvent: true);
            hVm.LadderFriendPicker.SetSelection(g2p.PetId, false, raiseEvent: true);
            hVm.ApplyCoffeePresetCommand.Execute(null);
            hVm.LadderEntryFee = 10;
            var hostCoinsBeforeTimeout = hp.SoloProgress.Coins;
            await hVm.OpenLadderRoomCommand.ExecuteAsync(null);
            await UntilAsync(() => g1Vm.HasIncomingInvite);
            await g1Vm.AcceptIncomingInviteCommand.ExecuteAsync(null);
            await UntilAsync(() => hVm.CanConfirmLadderParticipants);
            await hVm.ConfirmLadderParticipantsCommand.ExecuteAsync(null);
            await UntilAsync(() => !hVm.IsLadderRoomOpen && !g1Vm.IsLadderRoomOpen);
            Assert.Equal(hostCoinsBeforeTimeout, (await hStore.LoadProfileAsync()).SoloProgress.Coins);
        });
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }
}
