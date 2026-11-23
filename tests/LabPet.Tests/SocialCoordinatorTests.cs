using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using LabPet.Client.Network;
using LabPet.Client.Games;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Client.ViewModels;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;
using Xunit;

namespace LabPet.Tests;

public sealed class SocialCoordinatorTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "LabPet_Social_" + Guid.NewGuid().ToString("N"));
    private readonly LanP2pTransport _hostTransport = new(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
    private readonly LanP2pTransport _guestTransport = new(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
    private readonly PetProfile _hostProfile = new();
    private readonly JsonAppDataStore _store;
    private readonly PausingStore _coordinationStore;
    private readonly SocialCoordinatorService _host;
    private FriendEntry _hostAsFriend = null!;

    public SocialCoordinatorTests()
    {
        _store = new JsonAppDataStore(new StateMigrator(), _dataDirectory);
        _coordinationStore = new PausingStore(_store);
        _host = new SocialCoordinatorService(_hostTransport, _coordinationStore, new SoloProgressionService());
    }

    public async Task InitializeAsync()
    {
        var guestProfile = new PetProfile
        {
            OwnerName = "친구",
            PetName = "게스트",
            Appearance = new PetAppearance { SpeciesId = PetSpeciesIds.FluffyPuppy, BodyColorHex = "#C4905E" }
        };
        var sharedSecret = PacketSigner.GeneratePairSecretBase64();
        await _guestTransport.StartAsync(guestProfile, []);
        await _store.SaveFriendsAsync(new FriendsStore
        {
            Friends =
            [
                new FriendEntry
                {
                    PetId = guestProfile.PetId,
                    OwnerName = guestProfile.OwnerName,
                    PetName = guestProfile.PetName,
                    CachedAppearance = guestProfile.Appearance.Clone(),
                    LastKnownIp = IPAddress.Loopback.ToString(),
                    LastKnownTcpPort = _guestTransport.BoundTcpPort,
                    PairSecretBase64 = sharedSecret,
                    Intimacy = 1
                }
            ]
        });
        await _host.StartAsync(_hostProfile);
        _hostAsFriend = new FriendEntry
        {
            PetId = _hostProfile.PetId,
            LastKnownIp = IPAddress.Loopback.ToString(),
            LastKnownTcpPort = _hostTransport.BoundTcpPort,
            PairSecretBase64 = sharedSecret
        };
        _guestTransport.UpdateKnownFriends([_hostAsFriend]);
    }

    [Fact]
    public async Task LadderSettlement_UsesTheAgreedWagerAndPaysOnlyOnce()
    {
        using var games = new CoinGamesViewModel(_store, new SoloProgressionService(), _host);
        _hostProfile.SoloProgress.Coins = 100;
        games.BindProfile(_hostProfile);
        var friend = Assert.Single(_host.Friends);
        var session = Guid.NewGuid().ToString("N");

        var initialRoom = new LadderRoomStateDto
        {
            SessionId = session,
            HostPetId = friend.PetId,
            Phase = LadderRoomPhase.WaitingInvites,
            WagerCoins = 10,
            SlotCount = 2,
            PendingInviteCount = 0,
            Prizes = ["통과", "잭팟"],
            Members =
            [
                new LadderMemberStateDto { PetId = friend.PetId, DisplayName = "친구", SelectedSlot = null }
            ]
        };

        Task Receive(string action, int wager, object data) => games.HandleGameEventAsync(new GameEventReceivedEventArgs(
            friend.PetId, new GameEventPayload
            {
                GameType = LabLadderEngine.GameTypeId, SessionId = session, Action = action, WagerCoins = wager,
                Data = JsonSerializer.Serialize(data, ProtocolJson.PacketOptions)
            }));

        var notifications = new List<string>();
        games.GameNotification += (_, msg) => notifications.Add(msg);

        await Receive("invite", 10, initialRoom);
        await games.AcceptIncomingInviteAsync();
        Assert.Equal(90, _hostProfile.SoloProgress.Coins);

        var pickingRoom = new LadderRoomStateDto
        {
            SessionId = session,
            HostPetId = friend.PetId,
            Phase = LadderRoomPhase.PickingSlots,
            WagerCoins = 10,
            SlotCount = 2,
            PendingInviteCount = 0,
            Prizes = ["통과", "잭팟"],
            Members =
            [
                new LadderMemberStateDto { PetId = friend.PetId, DisplayName = "친구", SelectedSlot = 0 },
                new LadderMemberStateDto { PetId = _hostProfile.PetId, DisplayName = "나", SelectedSlot = 1 }
            ]
        };
        await Receive("state", 10, pickingRoom);

        var board = new LadderBoard(["친구", "나"], ["잭팟", "통과"], LabLadderEngine.DefaultLevels, [new LadderRung(1, 0)]);
        var start = LabLadderEngine.CreateStartDto(pickingRoom, board);

        // 합의되지 않은 배팅 금액의 start는 차단
        await Receive("start", 50, start);
        Assert.Equal(90, _hostProfile.SoloProgress.Coins);

        // 유효한 start 수신 시에는 하단 도착 전까지 결과가 숨겨지고 정산되지 않는다
        await Receive("start", 10, start);
        Assert.True(games.IsLadderAnimating);
        Assert.All(games.LadderResultLines, line => Assert.EndsWith("→  ?", line));
        Assert.DoesNotContain("잭팟", games.LadderStatusText, StringComparison.Ordinal);
        Assert.Empty(notifications);
        Assert.Equal(90, _hostProfile.SoloProgress.Coins);

        // 중복 start는 무시된다
        await Receive("start", 10, start);

        // 경로 도착 후 결과 공개 및 1회 정산
        Assert.True(games.RevealLadderParticipantResult(0));
        Assert.True(games.RevealLadderParticipantResult(1));
        await games.CompleteLadderRevealAsync();
        await games.CompleteLadderRevealAsync();

        Assert.False(games.IsLadderAnimating);
        Assert.Single(notifications);
        Assert.Equal(110, (await _store.LoadProfileAsync()).SoloProgress.Coins);
    }

    [Fact]
    public async Task LadderMultiplayer_FreeMode_RunsAllPhasesFairlyHidesUntilArrivalAndLeavesCoinsUnchanged()
    {
        await using var harness = await CreateLadderPairAsync("free", hostCoins: 120, guestCoins: 75);
        var hostGames = harness.HostGames;
        var guestGames = harness.GuestGames;

        var guestNotifications = new List<string>();
        guestGames.GameNotification += (_, msg) => guestNotifications.Add(msg);

        var guestInvited = WaitUntilPropertyAsync(guestGames, nameof(CoinGamesViewModel.HasIncomingInvite), () => guestGames.HasIncomingInvite);
        hostGames.LadderEntryFee = 0;
        hostGames.LadderPrizesText = "☕ 커피 x1, 통과 x1";
        await hostGames.OpenLadderRoomCommand.ExecuteAsync(null);
        await guestInvited.WaitAsync(TimeSpan.FromSeconds(5));

        // 1. 초대 시 무료 모드 및 전체 상품 공개 확인
        Assert.NotNull(guestGames.IncomingInvite);
        Assert.Equal(0, guestGames.IncomingInvite!.WagerCoins);
        Assert.Contains("무료 (0코인)", guestGames.IncomingInviteText);
        Assert.Contains("☕ 커피 ×1, 통과 ×1", guestGames.IncomingInviteText);

        var hostJoined = WaitUntilPropertyAsync(hostGames, nameof(CoinGamesViewModel.CanConfirmLadderParticipants),
            () => hostGames.CanConfirmLadderParticipants);
        await guestGames.AcceptIncomingInviteCommand.ExecuteAsync(null);
        await hostJoined.WaitAsync(TimeSpan.FromSeconds(5));

        // 2. 확정 전에는 시작 위치를 선택할 수 없다
        await hostGames.PickLadderSlotCommand.ExecuteAsync(0);
        await guestGames.PickLadderSlotCommand.ExecuteAsync(1);
        Assert.False(hostGames.IsLadderPickingPhase);
        Assert.False(guestGames.IsLadderPickingPhase);
        Assert.All(hostGames.LadderSlots, s => Assert.False(s.CanPick));

        // 3. 방장이 참가자 확정 -> 양쪽 모두 2단계 위치 선택 진입
        var guestPicking = WaitUntilPropertyAsync(guestGames, nameof(CoinGamesViewModel.IsLadderPickingPhase),
            () => guestGames.IsLadderPickingPhase);
        await hostGames.ConfirmLadderParticipantsCommand.ExecuteAsync(null);
        await guestPicking.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(hostGames.IsLadderPickingPhase);

        // 4. 중복 선택 차단 및 전원 선택 완료 전 게임 시작 불가
        var guestSawHostSlot = WaitUntilPropertyAsync(guestGames, nameof(CoinGamesViewModel.LadderLobbySummaryText),
            () => guestGames.LadderSlots.Any(s => s.ColumnIndex == 0 && s.IsOccupied));
        await hostGames.PickLadderSlotCommand.ExecuteAsync(0);
        await guestSawHostSlot.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(guestGames.LadderSlots[0].CanPick);
        Assert.False(hostGames.CanStartLadderGame);

        // 게스트가 이미 차지한 0번 선택 시도해도 무시되며, 1번 선택 후 시작 가능해짐
        await guestGames.PickLadderSlotCommand.ExecuteAsync(0);
        Assert.False(hostGames.CanStartLadderGame);

        var hostReady = WaitUntilPropertyAsync(hostGames, nameof(CoinGamesViewModel.CanStartLadderGame),
            () => hostGames.CanStartLadderGame);
        await guestGames.PickLadderSlotCommand.ExecuteAsync(1);
        await hostReady.WaitAsync(TimeSpan.FromSeconds(5));

        // 5. 방장 게임 시작 -> 양쪽 동일한 사다리판 수신, 도착 전에는 결과 비노출
        var guestBoardTcs = new TaskCompletionSource<LadderBoard>(TaskCreationOptions.RunContinuationsAsynchronously);
        guestGames.LadderBoardGenerated += (_, board) => guestBoardTcs.TrySetResult(board);
        LadderBoard? hostBoard = null;
        hostGames.LadderBoardGenerated += (_, board) => hostBoard = board;

        await hostGames.StartLadderRoomCommand.ExecuteAsync(null);
        var guestBoard = await guestBoardTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(hostBoard);
        Assert.Equal(hostBoard!.Participants, guestBoard.Participants);
        Assert.Equal(hostBoard.Prizes, guestBoard.Prizes);
        Assert.Equal(hostBoard.Rungs, guestBoard.Rungs);

        Assert.True(hostGames.IsLadderAnimating);
        Assert.True(guestGames.IsLadderAnimating);
        Assert.All(hostGames.LadderResultLines, line => Assert.EndsWith("→  ?", line));
        Assert.All(guestGames.LadderResultLines, line => Assert.EndsWith("→  ?", line));
        Assert.DoesNotContain("☕ 커피", hostGames.LadderStatusText);
        Assert.DoesNotContain("☕ 커피", guestGames.LadderStatusText);
        Assert.Empty(guestNotifications);
        Assert.Contains("☕ 커피 ×1, 통과 ×1", guestGames.LadderBoardPrizesSummaryText);

        // 6. 개별 도착 시 해당 결과만 공개되고 무료 모드 코인은 변하지 않는다
        var expectedTraces = LabLadderEngine.TraceAll(guestBoard);
        Assert.True(guestGames.RevealLadderParticipantResult(0));
        Assert.Contains(expectedTraces[0].Prize, guestGames.LadderResultLines[0]);
        Assert.EndsWith("→  ?", guestGames.LadderResultLines[1]);

        Assert.True(hostGames.RevealLadderParticipantResult(0));
        Assert.True(hostGames.RevealLadderParticipantResult(1));
        Assert.True(guestGames.RevealLadderParticipantResult(1));
        await hostGames.CompleteLadderRevealAsync();
        await guestGames.CompleteLadderRevealAsync();

        Assert.False(hostGames.IsLadderAnimating);
        Assert.False(guestGames.IsLadderAnimating);
        Assert.Contains(expectedTraces[1].Prize, guestGames.LadderResultLines[1]);
        Assert.Single(guestNotifications);
        Assert.Equal(120, harness.HostProfile.SoloProgress.Coins);
        Assert.Equal(75, harness.GuestProfile.SoloProgress.Coins);
    }

    [Fact]
    public async Task LadderMultiplayer_LeaveOrDisconnect_CancelsGameAndRefundsPaidCoinsOnce()
    {
        await using var harness = await CreateLadderPairAsync("refund", hostCoins: 100, guestCoins: 100);
        var hostGames = harness.HostGames;
        var guestGames = harness.GuestGames;

        // 시나리오 1: 유료(30코인) 확정 후 게스트 이탈 -> 양쪽 방 취소 및 각 1회 환불 (100코인 복구)
        var invited = WaitUntilPropertyAsync(guestGames, nameof(CoinGamesViewModel.HasIncomingInvite), () => guestGames.HasIncomingInvite);
        hostGames.LadderEntryFee = 30;
        hostGames.LadderPrizesText = "💰 잭팟! x1, 통과 x1";
        await hostGames.OpenLadderRoomCommand.ExecuteAsync(null);
        await invited.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(70, harness.HostProfile.SoloProgress.Coins);

        var joined = WaitUntilPropertyAsync(hostGames, nameof(CoinGamesViewModel.CanConfirmLadderParticipants), () => hostGames.CanConfirmLadderParticipants);
        await guestGames.AcceptIncomingInviteCommand.ExecuteAsync(null);
        await joined.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(70, harness.GuestProfile.SoloProgress.Coins);

        var picking = WaitUntilPropertyAsync(guestGames, nameof(CoinGamesViewModel.IsLadderPickingPhase), () => guestGames.IsLadderPickingPhase);
        await hostGames.ConfirmLadderParticipantsCommand.ExecuteAsync(null);
        await picking.WaitAsync(TimeSpan.FromSeconds(5));

        var hostClosed = WaitUntilPropertyAsync(hostGames, nameof(CoinGamesViewModel.IsLadderRoomOpen), () => !hostGames.IsLadderRoomOpen);
        await guestGames.CancelLadderRoomCommand.ExecuteAsync(null);
        await hostClosed.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(100, harness.GuestProfile.SoloProgress.Coins);
        Assert.Equal(100, (await harness.HostStore.LoadProfileAsync()).SoloProgress.Coins);

        // 시나리오 2: 위치 선택 후 상대 연결이 끊긴 상태에서 시작 시도 -> 안전 취소 및 방장 1회 환불
        var invited2 = WaitUntilPropertyAsync(guestGames, nameof(CoinGamesViewModel.HasIncomingInvite), () => guestGames.HasIncomingInvite);
        await hostGames.OpenLadderRoomCommand.ExecuteAsync(null);
        await invited2.WaitAsync(TimeSpan.FromSeconds(5));

        var joined2 = WaitUntilPropertyAsync(hostGames, nameof(CoinGamesViewModel.CanConfirmLadderParticipants), () => hostGames.CanConfirmLadderParticipants);
        await guestGames.AcceptIncomingInviteCommand.ExecuteAsync(null);
        await joined2.WaitAsync(TimeSpan.FromSeconds(5));

        var picking2 = WaitUntilPropertyAsync(guestGames, nameof(CoinGamesViewModel.IsLadderPickingPhase), () => guestGames.IsLadderPickingPhase);
        await hostGames.ConfirmLadderParticipantsCommand.ExecuteAsync(null);
        await picking2.WaitAsync(TimeSpan.FromSeconds(5));

        var ready2 = WaitUntilPropertyAsync(hostGames, nameof(CoinGamesViewModel.CanStartLadderGame), () => hostGames.CanStartLadderGame);
        await hostGames.PickLadderSlotCommand.ExecuteAsync(0);
        await guestGames.PickLadderSlotCommand.ExecuteAsync(1);
        await ready2.WaitAsync(TimeSpan.FromSeconds(5));

        await harness.GuestTransport.DisposeAsync();
        await hostGames.StartLadderRoomCommand.ExecuteAsync(null);

        Assert.False(hostGames.IsLadderRoomOpen);
        Assert.False(hostGames.IsLadderAnimating);
        Assert.Equal(100, (await harness.HostStore.LoadProfileAsync()).SoloProgress.Coins);
    }

    private sealed class LadderPairHarness : IAsyncDisposable
    {
        public required JsonAppDataStore HostStore { get; init; }
        public required JsonAppDataStore GuestStore { get; init; }
        public required LanP2pTransport HostTransport { get; init; }
        public required LanP2pTransport GuestTransport { get; init; }
        public required SocialCoordinatorService HostSocial { get; init; }
        public required SocialCoordinatorService GuestSocial { get; init; }
        public required PetProfile HostProfile { get; init; }
        public required PetProfile GuestProfile { get; init; }
        public required CoinGamesViewModel HostGames { get; init; }
        public required CoinGamesViewModel GuestGames { get; init; }

        public async ValueTask DisposeAsync()
        {
            HostGames.Dispose();
            GuestGames.Dispose();
            await HostSocial.DisposeAsync();
            await GuestSocial.DisposeAsync();
            await HostTransport.DisposeAsync();
            await GuestTransport.DisposeAsync();
        }
    }

    private async Task<LadderPairHarness> CreateLadderPairAsync(string dirSuffix, int hostCoins, int guestCoins)
    {
        var hostStore = new JsonAppDataStore(new StateMigrator(), Path.Combine(_dataDirectory, $"host_{dirSuffix}"));
        var guestStore = new JsonAppDataStore(new StateMigrator(), Path.Combine(_dataDirectory, $"guest_{dirSuffix}"));
        var hostTransport = new LanP2pTransport(0, 0, 0);
        var guestTransport = new LanP2pTransport(0, 0, 0);
        var hostSocial = new SocialCoordinatorService(hostTransport, hostStore, new SoloProgressionService());
        var guestSocial = new SocialCoordinatorService(guestTransport, guestStore, new SoloProgressionService());

        var secret = PacketSigner.GeneratePairSecretBase64();
        var hostProfile = new PetProfile { OwnerName = "방장", PetName = "모찌", SoloProgress = { Coins = hostCoins } };
        var guestProfile = new PetProfile { OwnerName = "친구", PetName = "몽실", SoloProgress = { Coins = guestCoins } };

        await hostStore.SaveProfileAsync(hostProfile);
        await guestStore.SaveProfileAsync(guestProfile);
        await hostTransport.StartAsync(hostProfile, []);
        await guestTransport.StartAsync(guestProfile, []);

        var guestAsFriend = new FriendEntry
        {
            PetId = guestProfile.PetId,
            OwnerName = guestProfile.OwnerName,
            PetName = guestProfile.PetName,
            LastKnownIp = IPAddress.Loopback.ToString(),
            LastKnownTcpPort = guestTransport.BoundTcpPort,
            PairSecretBase64 = secret
        };
        var hostAsFriend = new FriendEntry
        {
            PetId = hostProfile.PetId,
            OwnerName = hostProfile.OwnerName,
            PetName = hostProfile.PetName,
            LastKnownIp = IPAddress.Loopback.ToString(),
            LastKnownTcpPort = hostTransport.BoundTcpPort,
            PairSecretBase64 = secret
        };

        await hostStore.SaveFriendsAsync(new FriendsStore { Friends = [guestAsFriend] });
        await guestStore.SaveFriendsAsync(new FriendsStore { Friends = [hostAsFriend] });
        await hostSocial.StartAsync(hostProfile);
        await guestSocial.StartAsync(guestProfile);
        hostTransport.UpdateKnownFriends([guestAsFriend]);
        guestTransport.UpdateKnownFriends([hostAsFriend]);

        var peerDiscovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hostSocial.StateChanged += (_, _) =>
        {
            if (hostSocial.DiscoveredPeers.Any(p => p.PetId == guestProfile.PetId))
            {
                peerDiscovered.TrySetResult();
            }
        };
        hostTransport.HandleDiscoveryHello(new DiscoveryHelloPacket
        {
            PetId = guestProfile.PetId,
            OwnerName = guestProfile.OwnerName,
            PetName = guestProfile.PetName,
            TcpPort = guestTransport.BoundTcpPort,
            AppVersion = "0.2.0"
        }, IPAddress.Loopback.ToString(), CancellationToken.None);
        await peerDiscovered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var hostGames = new CoinGamesViewModel(hostStore, new SoloProgressionService(), hostSocial);
        var guestGames = new CoinGamesViewModel(guestStore, new SoloProgressionService(), guestSocial);
        hostGames.BindProfile(hostProfile);
        guestGames.BindProfile(guestProfile);

        return new LadderPairHarness
        {
            HostStore = hostStore,
            GuestStore = guestStore,
            HostTransport = hostTransport,
            GuestTransport = guestTransport,
            HostSocial = hostSocial,
            GuestSocial = guestSocial,
            HostProfile = hostProfile,
            GuestProfile = guestProfile,
            HostGames = hostGames,
            GuestGames = guestGames
        };
    }

    [Fact]
    public async Task LadderSolo_FreeAndPaid_HideResultsUntilArrivalAndSettleOnce()
    {
        var soloStore = new JsonAppDataStore(new StateMigrator(), Path.Combine(_dataDirectory, "solo"));
        var soloProfile = new PetProfile { OwnerName = "솔로", PetName = "모찌", SoloProgress = { Coins = 100 } };
        await soloStore.SaveProfileAsync(soloProfile);

        using var games = new CoinGamesViewModel(soloStore, new SoloProgressionService(), _host);
        games.BindProfile(soloProfile);

        // 무료(0코인) 솔로 사다리: 도착 전 숨김, 완료 후 코인 변동 없음
        games.LadderParticipantsText = "나, 친구";
        games.LadderPrizesText = "커피 x1, 통과 x1";
        games.LadderEntryFee = 0;
        await games.RunLadderCommand.ExecuteAsync(null);

        Assert.True(games.IsLadderAnimating);
        Assert.All(games.LadderResultLines, line => Assert.EndsWith("→  ?", line));
        Assert.Contains("커피 ×1, 통과 ×1", games.LadderBoardPrizesSummaryText);
        Assert.DoesNotContain("커피", games.LadderStatusText);
        Assert.Equal(100, soloProfile.SoloProgress.Coins);

        Assert.True(games.RevealLadderParticipantResult(0));
        Assert.DoesNotContain("→  ?", games.LadderResultLines[0]);
        Assert.EndsWith("→  ?", games.LadderResultLines[1]);

        await games.CompleteLadderRevealAsync();
        Assert.False(games.IsLadderAnimating);
        Assert.Equal(100, soloProfile.SoloProgress.Coins);

        // 유료(30코인) 솔로 잭팟: 시작 시 30코인 차감(70), 도착 전 지급 없음, 완료 시 잭팟 결과에 따라 1회 정산
        games.LadderPrizesText = "💰 잭팟! x1, 꽝 x1";
        games.LadderEntryFee = 30;
        await games.RunLadderCommand.ExecuteAsync(null);
        Assert.Equal(70, soloProfile.SoloProgress.Coins);
        Assert.All(games.LadderResultLines, line => Assert.EndsWith("→  ?", line));

        var board = Assert.IsType<LadderBoard>(games.ActiveLadderBoard);
        var myWon = LabLadderEngine.IsJackpot(LabLadderEngine.TracePath(board, 0).Prize);
        await games.CompleteLadderRevealAsync();
        await games.CompleteLadderRevealAsync();

        var expectedCoins = myWon ? 70 + 30 * 2 : 70;
        Assert.Equal(expectedCoins, soloProfile.SoloProgress.Coins);
    }

    private static Task WaitUntilPropertyAsync(CoinGamesViewModel vm, string propertyName, Func<bool> condition)
    {
        if (condition())
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if ((string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == propertyName) && condition())
            {
                vm.PropertyChanged -= Handler;
                tcs.TrySetResult();
            }
        }

        vm.PropertyChanged += Handler;
        if (condition())
        {
            vm.PropertyChanged -= Handler;
            tcs.TrySetResult();
        }

        return tcs.Task;
    }

    [Fact]
    public async Task FriendRegistration_AcceptAloneSavesTheSameKeyOnBothPeers()
    {
        var requesterTransport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
        var requesterStore = new JsonAppDataStore(new StateMigrator(), Path.Combine(_dataDirectory, "requester"));
        await using var requester = new SocialCoordinatorService(requesterTransport, requesterStore, new SoloProgressionService());
        var requesterProfile = new PetProfile { PetName = "신청자" };
        await requester.StartAsync(requesterProfile);

        var arrived = new TaskCompletionSource<PendingFriendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requesterAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.FriendRequestArrived += (_, request) => arrived.TrySetResult(request);
        _host.SocialNotification += (_, message) =>
        {
            if (message.Contains("친구 신청을 수락했어요", StringComparison.Ordinal))
            {
                hostAccepted.TrySetResult();
            }
        };
        requester.SocialNotification += (_, message) =>
        {
            if (message.Contains("친구 신청을 수락했어요", StringComparison.Ordinal))
            {
                requesterAccepted.TrySetResult();
            }
        };
        Assert.True(await requester.SendFriendRequestAsync(new PeerSnapshot
        {
            PetId = _hostProfile.PetId, IpAddress = IPAddress.Loopback.ToString(), TcpPort = _hostTransport.BoundTcpPort
        }));
        var pending = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(requester.Friends);
        Assert.DoesNotContain(_host.Friends, friend => friend.PetId == requesterProfile.PetId);

        Assert.True(await _host.RespondFriendRequestAsync(pending, accept: true));
        await Task.WhenAll(requesterAccepted.Task, hostAccepted.Task).WaitAsync(TimeSpan.FromSeconds(5));

        var hostEntry = Assert.Single((await requesterStore.LoadFriendsAsync()).Friends);
        var requesterEntry = Assert.Single((await _store.LoadFriendsAsync()).Friends,
            friend => friend.PetId == requesterProfile.PetId);
        Assert.Equal(_hostProfile.PetId, hostEntry.PetId);
        Assert.Equal(ProtocolConstants.PairSecretByteLength, Convert.FromBase64String(hostEntry.PairSecretBase64).Length);
        Assert.Equal(hostEntry.PairSecretBase64, requesterEntry.PairSecretBase64);
        Assert.True(await requester.SendBubbleMessageAsync(hostEntry, "등록 직후 메시지"));
    }

    [Fact]
    public async Task RegistrationAndConcurrentMessages_PersistAllChangesAndKeepEarlierFriendSnapshotStable()
    {
        const int messagesPerDirection = 8;
        var beforeList = _host.Friends;
        var before = Assert.Single(beforeList);
        before.CachedAppearance.ExpressionId = "expr_happy";
        Assert.NotEqual(before.CachedAppearance.ExpressionId, Assert.Single(_host.Friends).CachedAppearance.ExpressionId);

        await using var newPeer = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
        var newPeerProfile = new PetProfile { PetName = "새 친구" };
        await newPeer.StartAsync(newPeerProfile, []);
        var registration = new TaskCompletionSource<LabPet.Shared.Transport.FriendRequestReceivedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        newPeer.FriendRequestReceived += (_, e) => registration.TrySetResult(e);
        var newPeerKey = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        newPeer.FriendAccepted += (_, e) => newPeerKey.TrySetResult(e.PairSecretBase64);
        Assert.True(await _host.SendFriendRequestAsync(new PeerSnapshot
        {
            PetId = newPeerProfile.PetId,
            IpAddress = IPAddress.Loopback.ToString(),
            TcpPort = newPeer.BoundTcpPort
        }));

        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var friendAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedCount = 0;
        _host.FriendMessageReceived += (_, message) =>
        {
            if (message.Text.Contains("수신 메시지", StringComparison.Ordinal)
                && Interlocked.Increment(ref receivedCount) == messagesPerDirection + 1)
            {
                allReceived.TrySetResult();
            }
        };

        _host.SocialNotification += (_, message) =>
        {
            if (message.Contains("친구 신청을 수락했어요", StringComparison.Ordinal))
            {
                friendAccepted.TrySetResult();
            }
        };

        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _coordinationStore.BeforeSaveFriendsAsync = () =>
        {
            saveStarted.TrySetResult();
            return resumeSave.Task;
        };
        var sendTasks = Enumerable.Range(0, messagesPerDirection).SelectMany(index => new[]
        {
            _host.SendBubbleMessageAsync(before, $"호스트 메시지 {index}"),
            _guestTransport.SendBubbleMessageAsync(_hostAsFriend, $"게스트 수신 메시지 {index}")
        }).ToArray();

        try
        {
            await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var request = await registration.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await newPeer.RespondFriendRequestAsync(request.RequestId, accept: true));

            // 앞선 저장이 대기 중이어도 새 친구의 첫 메시지는 바로 인증된다.
            var hostAsNewFriend = _hostAsFriend.Clone();
            hostAsNewFriend.PairSecretBase64 = await newPeerKey.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await newPeer.SendBubbleMessageAsync(hostAsNewFriend, "새 친구 수신 메시지"));
        }
        finally
        {
            resumeSave.TrySetResult();
        }

        var results = await Task.WhenAll(sendTasks).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.All(results, result => Assert.True(result));
        await Task.WhenAll(allReceived.Task, friendAccepted.Task).WaitAsync(TimeSpan.FromSeconds(15));

        var guestIntimacy = 1 + 2 * messagesPerDirection;
        var totalMessages = 2 * messagesPerDirection + 1;
        Assert.Single(beforeList);
        Assert.Equal(1, before.Intimacy);
        Assert.Collection(_host.Friends,
            guest => Assert.Equal(guestIntimacy, guest.Intimacy),
            friend => Assert.Equal(newPeerProfile.PetId, friend.PetId));
        Assert.Collection((await _store.LoadFriendsAsync()).Friends,
            guest => Assert.Equal(guestIntimacy, guest.Intimacy),
            friend =>
            {
                Assert.Equal(newPeerProfile.PetId, friend.PetId);
                Assert.Equal(2, friend.Intimacy);
            });
        var savedProfile = await _store.LoadProfileAsync();
        Assert.Equal(totalMessages * 2, savedProfile.SoloProgress.Coins);
        Assert.Equal(totalMessages * 3, savedProfile.SoloProgress.Xp);
    }

    [Fact]
    public async Task SaveFailure_NotifiesAndNextMessagePersistsPendingRewards()
    {
        var notifications = new List<string>();
        _host.SocialNotification += (_, message) => notifications.Add(message);
        var guest = Assert.Single(_host.Friends);
        var friendsFile = Path.Combine(_dataDirectory, "friends.json");

        using (File.Open(friendsFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(await _host.SendBubbleMessageAsync(guest, "첫 메시지"));
        }

        Assert.Single(notifications);
        Assert.Contains("저장하지 못했어요", notifications[0]);
        Assert.True(await _host.SendBubbleMessageAsync(guest, "다음 메시지"));
        Assert.Equal(3, Assert.Single((await _store.LoadFriendsAsync()).Friends).Intimacy);
        Assert.Equal(4, (await _store.LoadProfileAsync()).SoloProgress.Coins);
    }

    [Fact]
    public async Task QuickMessage_SuccessKeepsSameFriendEditorOpenForAnotherMessage()
    {
        var viewModel = new PetWindowViewModel(_store, _host);
        var friend = Assert.Single(_host.Friends);
        var row = new QuickOverlayEntry { PetId = friend.PetId, DisplayTitle = friend.PetName, IsFriend = true, Friend = friend };

        viewModel.OpenQuickMessageCommand.Execute(row);
        viewModel.QuickMessageText = "첫 메시지";
        await viewModel.SendQuickMessageCommand.ExecuteAsync(null);

        Assert.Same(row, viewModel.QuickMessageTarget);
        Assert.True(row.IsComposingMessage);
        Assert.Empty(viewModel.QuickMessageText);
        Assert.Empty(viewModel.BubbleLines);

        viewModel.QuickMessageText = "두 번째 메시지";
        await viewModel.SendQuickMessageCommand.ExecuteAsync(null);
        Assert.Same(row, viewModel.QuickMessageTarget);
        Assert.Empty(viewModel.BubbleLines);
        Assert.Equal(3, Assert.Single((await _store.LoadFriendsAsync()).Friends).Intimacy);
    }

    [Fact]
    public async Task SocialMessage_SuccessClearsTheFailureStatusWithoutASentNotice()
    {
        var viewModel = new SocialViewModel(_host);
        try
        {
            var friend = Assert.Single(_host.Friends);
            var defaultStatus = viewModel.StatusBanner;
            viewModel.SelectedFriend = new FriendListItemViewModel(friend,
                new PeerSnapshot { PetId = friend.PetId }, isFollowingMusic: false, isVisitingDesktop: false);
            viewModel.StatusBanner = "메시지 전송에 실패했습니다.";
            viewModel.MessageText = "다시 전송한 메시지";
            await viewModel.SendBubbleMessageCommand.ExecuteAsync(null);

            Assert.Empty(viewModel.MessageText);
            Assert.Equal(defaultStatus, viewModel.StatusBanner);
            Assert.Equal(2, Assert.Single((await _store.LoadFriendsAsync()).Friends).Intimacy);
        }
        finally
        {
            viewModel.Detach();
        }
    }

    [Fact]
    public async Task ReceivedFriendMessage_CarriesOriginalTextAndIndependentFriendAppearance()
    {
        var received = new TaskCompletionSource<ReceivedFriendMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationCount = 0;
        _host.SocialNotification += (_, _) => Interlocked.Increment(ref notificationCount);
        _host.FriendMessageReceived += (_, message) => received.TrySetResult(message);

        const string text = "안녕: 캐릭터 메시지";
        Assert.True(await _guestTransport.SendBubbleMessageAsync(_hostAsFriend, text));
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(text, message.Text);
        Assert.Equal("친구 (게스트)", message.SenderName);
        Assert.False(string.IsNullOrWhiteSpace(message.SenderPetId));
        Assert.False(string.IsNullOrWhiteSpace(message.MessageId));
        Assert.Equal(PetSpeciesIds.FluffyPuppy, message.Appearance.SpeciesId);
        Assert.Equal("#C4905E", message.Appearance.BodyColorHex);
        message.Appearance.SpeciesId = PetSpeciesIds.ChibiCat;
        Assert.Equal(PetSpeciesIds.FluffyPuppy, Assert.Single(_host.Friends).CachedAppearance.SpeciesId);
        Assert.Equal(0, Volatile.Read(ref notificationCount));
    }

    [Fact]
    public async Task BubbleLines_DisplayTheLatestFiveMessagesInOrder()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                dispatcher.Invoke(() =>
                {
                    var viewModel = new PetWindowViewModel(_store, _host);
                    var portrait = new DrawingImage();
                    for (var index = 1; index <= 6; index++)
                    {
                        viewModel.ShowBubble(new FriendMessageBubble($"받은 메시지 {index}", "친구", portrait), durationMs: 100);
                    }

                    Assert.True(viewModel.IsBubbleVisible);
                    Assert.Equal(Enumerable.Range(2, 5).Select(index => $"받은 메시지 {index}"),
                        viewModel.BubbleLines.Select(line => line.Text));
                    Assert.All(viewModel.BubbleLines, line => Assert.Same(portrait, Assert.IsType<FriendMessageBubble>(line).Portrait));

                    var expiryFrame = new DispatcherFrame();
                    viewModel.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(PetWindowViewModel.IsBubbleVisible) && !viewModel.IsBubbleVisible)
                        {
                            expiryFrame.Continue = false;
                        }
                    };
                    Dispatcher.PushFrame(expiryFrame);
                    Assert.Empty(viewModel.BubbleLines);
                });
                completed.SetResult();
            }
            catch (Exception exception)
            {
                completed.SetException(exception);
            }
            finally
            {
                dispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task QuickMessage_LeavesTextTypedDuringAnInFlightSend()
    {
        var viewModel = new PetWindowViewModel(_store, _host);
        var friend = Assert.Single(_host.Friends);
        var row = new QuickOverlayEntry { PetId = friend.PetId, IsFriend = true, Friend = friend };
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _coordinationStore.BeforeSaveFriendsAsync = () =>
        {
            saveStarted.TrySetResult();
            return resumeSave.Task;
        };

        viewModel.OpenQuickMessageCommand.Execute(row);
        viewModel.QuickMessageText = "전송 시작";
        var send = viewModel.SendQuickMessageCommand.ExecuteAsync(null);
        try
        {
            await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            viewModel.QuickMessageText = "전송 중 작성한 다음 메시지";
        }
        finally
        {
            resumeSave.TrySetResult();
        }

        await send;
        Assert.Equal("전송 중 작성한 다음 메시지", viewModel.QuickMessageText);
        Assert.True(row.IsComposingMessage);
        Assert.Same(row, viewModel.QuickMessageTarget);
    }

    [Fact]
    public async Task LocalSleepState_SyncsPeerSnapshotAndShowsZzzInFriendAndOverlayStatus()
    {
        var guestFriend = Assert.Single(_host.Friends);
        var sleepObservedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wakeObservedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStateChanged(object? sender, EventArgs args)
        {
            var live = _host.DiscoveredPeers.FirstOrDefault(p =>
                string.Equals(p.PetId, guestFriend.PetId, StringComparison.OrdinalIgnoreCase));
            if (live is null)
            {
                return;
            }

            if (live.IsSleeping)
            {
                sleepObservedTcs.TrySetResult(true);
            }
            else if (sleepObservedTcs.Task.IsCompleted)
            {
                wakeObservedTcs.TrySetResult(true);
            }
        }

        _host.StateChanged += OnStateChanged;
        try
        {
            // 1. 게스트가 수면 상태(IsSleeping=true)로 전환 후 첫 탐색→PresenceSync 검증
            await _guestTransport.UpdateLocalPresenceAsync(new LocalPresenceState(
                guestFriend.OwnerName,
                guestFriend.PetName,
                guestFriend.CachedAppearance.Clone(),
                Level: 1,
                IsMusicActive: false,
                AppVersion: "0.2.2",
                IsSleeping: true));

            _hostTransport.HandleDiscoveryHello(
                new DiscoveryHelloPacket
                {
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                    AppVersion = "0.2.2",
                    PetId = guestFriend.PetId,
                    OwnerName = guestFriend.OwnerName,
                    PetName = guestFriend.PetName,
                    Appearance = guestFriend.CachedAppearance.Clone(),
                    IsSleeping = true,
                    TcpPort = _guestTransport.BoundTcpPort
                },
                IPAddress.Loopback.ToString(),
                CancellationToken.None);

            await sleepObservedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var sleepingPeer = Assert.Single(_host.DiscoveredPeers);
            Assert.True(sleepingPeer.IsSleeping);

            var friendVm = new FriendListItemViewModel(guestFriend, sleepingPeer, isFollowingMusic: false, isVisitingDesktop: false);
            Assert.True(friendVm.IsSleeping);
            Assert.Contains("zzz", friendVm.StatusText, StringComparison.Ordinal);

            var peerVm = new PeerListItemViewModel(sleepingPeer);
            Assert.True(peerVm.IsSleeping);

            var petVm = new PetWindowViewModel(_store, _host);
            petVm.RefreshQuickOverlayEntries();
            var overlayRow = Assert.Single(petVm.QuickOverlayEntries);
            Assert.True(overlayRow.IsSleeping);
            Assert.True(overlayRow.HasSleepBadge);

            // 2. 게스트 입력 재개로 수면 해제 -> 즉시 PresenceSync 푸시로 온라인·비수면 복귀
            await _guestTransport.UpdateLocalPresenceAsync(new LocalPresenceState(
                guestFriend.OwnerName,
                guestFriend.PetName,
                guestFriend.CachedAppearance.Clone(),
                Level: 1,
                IsMusicActive: false,
                AppVersion: "0.2.2",
                IsSleeping: false));

            await wakeObservedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var awakePeer = Assert.Single(_host.DiscoveredPeers);
            Assert.False(awakePeer.IsSleeping);

            var awakeFriendVm = new FriendListItemViewModel(guestFriend, awakePeer, isFollowingMusic: false, isVisitingDesktop: false);
            Assert.False(awakeFriendVm.IsSleeping);
            Assert.Equal("온라인", awakeFriendVm.StatusText);

            petVm.RefreshQuickOverlayEntries();
            var awakeOverlayRow = Assert.Single(petVm.QuickOverlayEntries);
            Assert.False(awakeOverlayRow.IsSleeping);
            Assert.False(awakeOverlayRow.HasSleepBadge);
        }
        finally
        {
            _host.StateChanged -= OnStateChanged;
        }
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _hostTransport.DisposeAsync();
        await _guestTransport.DisposeAsync();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    private sealed class PausingStore(IAppDataStore inner) : IAppDataStore
    {
        public Func<Task>? BeforeSaveFriendsAsync { get; set; }
        public string DataDirectory => inner.DataDirectory;

        public Task<PetProfile> LoadProfileAsync(CancellationToken cancellationToken = default)
            => inner.LoadProfileAsync(cancellationToken);

        public Task SaveProfileAsync(PetProfile profile, CancellationToken cancellationToken = default)
            => inner.SaveProfileAsync(profile, cancellationToken);

        public Task<FriendsStore> LoadFriendsAsync(CancellationToken cancellationToken = default)
            => inner.LoadFriendsAsync(cancellationToken);

        public async Task SaveFriendsAsync(FriendsStore friendsStore, CancellationToken cancellationToken = default)
        {
            if (BeforeSaveFriendsAsync is { } beforeSave)
            {
                await beforeSave();
            }

            await inner.SaveFriendsAsync(friendsStore, cancellationToken);
        }
    }
}
