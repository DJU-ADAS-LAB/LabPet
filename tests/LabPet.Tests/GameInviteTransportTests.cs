using System.IO;
using System.Net;
using System.Net.Sockets;
using LabPet.Client.Network;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Client.ViewModels;
using LabPet.Server;
using LabPet.Server.Options;
using LabPet.Server.Storage;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LabPet.Tests;

[Collection("WpfUi")]
public sealed class GameInviteTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InviteLifecycle_UsesApplicationRepliesAndSettlesOnce_AcrossLanAndGcp(bool useGcp)
    {
        var root = Path.Combine(Path.GetTempPath(), "LabPet_InviteTransport_" + Guid.NewGuid().ToString("N"));
        WebApplication? server = null;
        try
        {
            string? url = null;
            if (useGcp)
            {
                using var reservation = new TcpListener(IPAddress.Loopback, 0);
                reservation.Start();
                url = $"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}";
                reservation.Stop();
                server = LabPetServerHost.BuildApplication(new ServerOptions
                {
                    ListenUrl = url, DataDirectory = Path.Combine(root, "server"),
                    ServerSecret = "game-invite-loopback-integration-test"
                });
                await server.StartAsync();
            }

            var host = NewProfile("방장");
            var guest = NewProfile("게스트");
            var hostStore = NewStore(Path.Combine(root, "host"));
            var guestStore = NewStore(Path.Combine(root, "guest"));
            var hostLan = NewLan();
            var guestLan = NewLan();
            var options = new ServerConnectionOptions { Enabled = useGcp, ServerUrl = url ?? string.Empty };
            var hostTransport = Wrap(hostLan, options, Path.Combine(root, "host-identity"));
            var guestTransport = Wrap(guestLan, options, Path.Combine(root, "guest-identity"));
            await using var hostSocial = new SocialCoordinatorService(hostTransport, hostStore, new SoloProgressionService());
            await using var guestSocial = new SocialCoordinatorService(guestTransport, guestStore, new SoloProgressionService());

            if (!useGcp)
            {
                await hostLan.StartAsync(host, []);
                await guestLan.StartAsync(guest, []);
            }
            var secret = PacketSigner.GeneratePairSecretBase64();
            var hostFriend = NewFriend(host, useGcp ? 0 : hostLan.BoundTcpPort, secret);
            var guestFriend = NewFriend(guest, useGcp ? 0 : guestLan.BoundTcpPort, secret);
            var alternate = NewFriend(NewProfile("다른 친구"), 0, secret);
            await hostStore.SaveFriendsAsync(new FriendsStore { Friends = [guestFriend, alternate] });
            await guestStore.SaveFriendsAsync(new FriendsStore { Friends = [hostFriend] });
            await hostSocial.StartAsync(host);
            await guestSocial.StartAsync(guest);
            if (!useGcp)
            {
                hostLan.UpdateKnownFriends([guestFriend, alternate]);
                guestLan.UpdateKnownFriends([hostFriend]);
                Discover(hostLan, guestFriend);
                Discover(guestLan, hostFriend);
            }
            else
            {
                var registry = server!.Services.GetRequiredService<IdentityRegistryStore>();
                await UntilAsync(() => registry.AreMutualFriends(host.PetId, guest.PetId));
            }
            await UntilAsync(() => hostSocial.DiscoveredPeers.Any(p => p.PetId == guest.PetId)
                && guestSocial.DiscoveredPeers.Any(p => p.PetId == host.PetId));
            var expectedRoute = useGcp ? PeerTransportKind.GcpRelay : PeerTransportKind.Lan;
            Assert.Contains(hostSocial.DiscoveredPeers, p => p.PetId == guest.PetId && p.TransportKind == expectedRoute);

            await WpfTestHost.RunAsync(async () =>
            {
                using var a = new CoinGamesViewModel(hostStore, new SoloProgressionService(), hostSocial);
                using var b = new CoinGamesViewModel(guestStore, new SoloProgressionService(), guestSocial);
                a.BindProfile(host);
                b.BindProfile(guest);
                a.RpsWager = b.RpsWager = 30;

                // 최초에는 아무 친구도 자동 선택되지 않으며, 선택 후 오프라인 전환 시 타인을 임의 선택하지 않는다.
                Assert.Null(a.SelectedRpsFriend);
                Assert.Null(a.SelectedTypingFriend);
                Assert.Equal(0, a.SelectedLadderInviteeCount);

                a.SelectedRpsFriend = a.OnlineFriends.First(f => f.Friend.PetId == guest.PetId);
                a.InviteExpireSeconds = 0.8;
                hostTransport.Filter = (p, send) => p.Action == "offer" ? Task.FromResult(true) : send();
                await a.SendRpsInviteCommand.ExecuteAsync(null);
                Assert.True(a.IsRpsWaitingResponse);
                Assert.DoesNotContain("표시되었습니다", a.RpsStatusText);
                await UntilAsync(() => !a.IsRpsWaitingResponse);
                Assert.Contains("등록 응답", a.RpsStatusText);
                Assert.False(b.HasIncomingInvite);

                hostTransport.Filter = (p, send) => p.Action == "offer" ? Task.FromResult(false) : send();
                await a.SendRpsInviteCommand.ExecuteAsync(null);
                await UntilAsync(() => !a.IsRpsWaitingResponse);
                Assert.Contains("전달하지 못했습니다", a.RpsStatusText);
                hostTransport.Filter = null;

                hostTransport.OverridePeers = [new PeerSnapshot { PetId = alternate.PetId, PetName = alternate.PetName, IsFriend = true }];
                a.RefreshOnlineFriends();
                Assert.Single(a.OnlineFriends);
                Assert.Null(a.SelectedRpsFriend);
                Assert.Null(a.SelectedTypingFriend);
                await a.SendRpsInviteCommand.ExecuteAsync(null);
                Assert.Contains("선택해 주세요", a.RpsStatusText);
                hostTransport.OverridePeers = null;
                a.RefreshOnlineFriends();
                Assert.Null(a.SelectedRpsFriend);
                a.SelectedRpsFriend = a.OnlineFriends.First(f => f.Friend.PetId == guest.PetId);

                a.InviteExpireSeconds = 1;
                DateTimeOffset? sentDeadline = null;
                hostTransport.Filter = (p, send) =>
                {
                    if (p.Action == "offer")
                        sentDeadline = p.InviteExpiresAtUtc;
                    return send();
                };
                await a.SendRpsInviteCommand.ExecuteAsync(null);
                await UntilAsync(() => b.HasIncomingInvite);
                Assert.Equal(sentDeadline, b.IncomingInvite?.ExpiresAtUtc);
                await UntilAsync(() => b.IncomingInviteStatus == GameInviteStatus.Expired && !a.IsRpsWaitingResponse);
                Assert.Contains("만료", a.RpsStatusText);
                hostTransport.Filter = null;
                a.InviteExpireSeconds = b.InviteExpireSeconds = 8;

                var releaseDelayedOffer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var canceledOfferRejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                hostTransport.Filter = async (p, send) =>
                {
                    if (p.Action == "offer")
                        await releaseDelayedOffer.Task;
                    var delivered = await send();
                    if (p.Action == "cancel" && delivered)
                        cancelDelivered.TrySetResult();
                    return delivered;
                };
                guestTransport.Filter = (p, send) =>
                {
                    if (p.Action == "decline" && p.InviteReason == "canceled")
                        canceledOfferRejected.TrySetResult();
                    return send();
                };
                var delayedOffer = a.SendRpsInviteCommand.ExecuteAsync(null);
                Assert.True(a.IsRpsWaitingResponse);
                await a.SendRpsInviteCommand.ExecuteAsync(null);
                await UntilAsync(() => !a.IsRpsWaitingResponse);
                await cancelDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                releaseDelayedOffer.TrySetResult();
                await delayedOffer;
                await canceledOfferRejected.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(b.HasIncomingInvite);
                hostTransport.Filter = guestTransport.Filter = null;

                await a.SendRpsInviteCommand.ExecuteAsync(null);
                await UntilAsync(() => b.HasIncomingInvite);
                await b.DeclineIncomingInviteAsync();
                await UntilAsync(() => !a.IsRpsWaitingResponse);
                Assert.Contains("거절", a.RpsStatusText);

                await a.SendRpsInviteCommand.ExecuteAsync(null);
                await UntilAsync(() => b.HasIncomingInvite);
                await a.SendRpsInviteCommand.ExecuteAsync(null);
                await UntilAsync(() => b.IncomingInviteStatus == GameInviteStatus.Canceled);
                Assert.Equal(100, guest.SoloProgress.Coins);

                var simultaneous = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                async Task<bool> DelayOffer(GameEventPayload p, Func<Task<bool>> send)
                {
                    if (p.Action == "offer")
                        await simultaneous.Task;
                    return await send();
                }
                hostTransport.Filter = guestTransport.Filter = DelayOffer;
                b.SelectedRpsFriend = b.OnlineFriends.First(f => f.Friend.PetId == host.PetId);
                var outgoingA = a.SendRpsInviteCommand.ExecuteAsync(null);
                var outgoingB = b.SendRpsInviteCommand.ExecuteAsync(null);
                simultaneous.SetResult();
                await Task.WhenAll(outgoingA, outgoingB);
                await UntilAsync(() => !a.IsRpsWaitingResponse && !b.IsRpsWaitingResponse);
                Assert.Contains("진행 중", a.RpsStatusText);
                Assert.Contains("진행 중", b.RpsStatusText);
                Assert.False(a.HasIncomingInvite || b.HasIncomingInvite);
                hostTransport.Filter = guestTransport.Filter = null;

                guest.SoloProgress.Coins = 0;
                await a.SendRpsInviteCommand.ExecuteAsync(null);
                await UntilAsync(() => b.HasIncomingInvite && a.RpsWaitState.OpponentResponseStatusText.Contains("코인 부족", StringComparison.Ordinal));
                Assert.True(b.HasIncomingInviteInsufficientCoins);
                Assert.False(b.CanAcceptIncomingInvite);
                await b.AcceptIncomingInviteAsync();
                Assert.True(b.HasIncomingInvite);
                await b.DeclineIncomingInviteAsync();
                await UntilAsync(() => !a.IsRpsWaitingResponse);
                Assert.Contains("코인", a.RpsStatusText);
                Assert.False(b.HasIncomingInvite);
                Assert.Equal(100, host.SoloProgress.Coins);
                guest.SoloProgress.Coins = 100;

                await a.SendRpsInviteCommand.ExecuteAsync(null);
                await UntilAsync(() => b.HasIncomingInvite);
                host.SoloProgress.Coins = 0;
                await b.AcceptIncomingInviteAsync();
                await UntilAsync(() => !a.IsRpsWaitingResponse);
                Assert.Contains("코인", a.RpsStatusText);
                Assert.Contains("코인", b.IncomingInviteFeedbackMessage);
                Assert.Equal(100, (await guestStore.LoadProfileAsync()).SoloProgress.Coins);
                Assert.Empty(guest.SoloProgress.PendingGameWagers);
                host.SoloProgress.Coins = 100;

                var droppedOfferAck = false;
                var droppedRegistered = false;
                var droppedAccept = false;
                var droppedBegun = false;
                hostTransport.Filter = async (p, send) =>
                {
                    var delivered = await send();
                    if (p.Action == "offer" && !droppedOfferAck && delivered)
                    {
                        droppedOfferAck = true;
                        return false;
                    }
                    return delivered;
                };
                guestTransport.Filter = (p, send) =>
                {
                    if (p.Action == "registered" && !droppedRegistered)
                    {
                        droppedRegistered = true;
                        return Task.FromResult(false);
                    }
                    if (p.Action == "accept" && !droppedAccept)
                    {
                        droppedAccept = true;
                        return Task.FromResult(false);
                    }
                    if (p.Action == "begun" && !droppedBegun)
                    {
                        droppedBegun = true;
                        return Task.FromResult(false);
                    }
                    return send();
                };
                await a.SendRpsInviteCommand.ExecuteAsync(null);
                await UntilAsync(() => b.HasIncomingInvite && a.RpsStatusText.Contains("표시되었습니다", StringComparison.Ordinal));
                await Task.WhenAll(b.AcceptIncomingInviteAsync(), b.AcceptIncomingInviteAsync());
                await UntilAsync(() => a.IsRpsInMatch && b.IsRpsInMatch);
                Assert.True(droppedOfferAck && droppedRegistered && droppedAccept && droppedBegun);
                Assert.Equal(GameScreen.Rps, b.ActiveGameScreen);
                Assert.Equal(70, host.SoloProgress.Coins);
                Assert.Equal(70, guest.SoloProgress.Coins);
                await Task.WhenAll(a.ChooseRpsMoveCommand.ExecuteAsync("rock"), b.ChooseRpsMoveCommand.ExecuteAsync("rock"));
                await UntilAsync(() => a.RpsPhase == RpsPhase.Result && b.RpsPhase == RpsPhase.Result);
                Assert.Equal(100, host.SoloProgress.Coins);
                Assert.Equal(100, guest.SoloProgress.Coins);
                Assert.Empty(host.SoloProgress.PendingGameWagers);
                Assert.Empty(guest.SoloProgress.PendingGameWagers);
                hostTransport.Filter = guestTransport.Filter = null;

                a.SelectedTypingFriend = a.OnlineFriends.First(f => f.Friend.PetId == guest.PetId);
                a.TypingWager = 10;
                a.TypingCountdownDurationSeconds = b.TypingCountdownDurationSeconds = 0.3;
                a.TypingSprintDurationSeconds = b.TypingSprintDurationSeconds = 0.3;
                await a.SendTypingInviteCommand.ExecuteAsync(null);
                await UntilAsync(() => b.HasIncomingInvite);
                await guestTransport.StopAsync();
                var typingAccept = b.AcceptIncomingInviteAsync();
                await UntilAsync(() => b.IsAcceptingIncomingInvite);
                Assert.False(a.IsTypingRunning || b.IsTypingRunning);
                await guestTransport.StartAsync(guest, [hostFriend]);
                if (!useGcp)
                {
                    guestFriend.LastKnownTcpPort = guestLan.BoundTcpPort;
                    Discover(hostLan, guestFriend);
                    Discover(guestLan, hostFriend);
                }
                await typingAccept;
                Assert.Equal(GameScreen.Typing, b.ActiveGameScreen);
                await UntilAsync(() => a.TypingPhase == TypingPhase.Result && b.TypingPhase == TypingPhase.Result);
                Assert.Equal(100, host.SoloProgress.Coins);
                Assert.Equal(100, guest.SoloProgress.Coins);

                a.LadderFriendPicker.SelectAllCommand.Execute(null);
                a.LadderEntryFee = 0;
                a.LadderPrizesText = "커피 x1, 통과 x1";
                await a.OpenLadderRoomCommand.ExecuteAsync(null);
                await UntilAsync(() => b.HasIncomingInvite);
                await b.AcceptIncomingInviteAsync();
                await UntilAsync(() => a.CanConfirmLadderParticipants);
                Assert.Equal(GameScreen.Ladder, b.ActiveGameScreen);
                await a.ConfirmLadderParticipantsCommand.ExecuteAsync(null);
                await UntilAsync(() => b.IsLadderPickingPhase);
                await a.PickLadderSlotCommand.ExecuteAsync(0);
                await b.PickLadderSlotCommand.ExecuteAsync(1);
                await UntilAsync(() => a.CanStartLadderGame);
                var missedBoardConfirmation = false;
                guestTransport.Filter = (p, send) =>
                {
                    if (p.Action == "started-start" && !missedBoardConfirmation)
                    {
                        missedBoardConfirmation = true;
                        return Task.FromResult(false);
                    }
                    return send();
                };
                await a.StartLadderRoomCommand.ExecuteAsync(null);
                Assert.True(missedBoardConfirmation);
                Assert.True(a.IsLadderAnimating && b.IsLadderAnimating);
                await Task.WhenAll(a.CompleteLadderRevealAsync(), b.CompleteLadderRevealAsync());
                guestTransport.Filter = null;
                Assert.Equal(100, host.SoloProgress.Coins);
                Assert.Equal(100, guest.SoloProgress.Coins);

                await a.OpenLadderRoomCommand.ExecuteAsync(null);
                await UntilAsync(() => b.HasIncomingInvite);
                await b.AcceptIncomingInviteAsync();
                await UntilAsync(() => a.CanConfirmLadderParticipants);
                await a.CancelLadderRoomCommand.ExecuteAsync(null);
                await UntilAsync(() => !b.IsLadderRoomOpen);
                Assert.Equal(100, host.SoloProgress.Coins);
                Assert.Equal(100, guest.SoloProgress.Coins);
            }).WaitAsync(TimeSpan.FromSeconds(35));
        }
        finally
        {
            if (server is not null)
                await server.DisposeAsync();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InterruptedWager_IsRecoveredOnceWhenTheProfileIsReopened()
    {
        var root = Path.Combine(Path.GetTempPath(), "LabPet_InviteRecovery_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = NewStore(root);
            var saved = NewProfile("복구");
            saved.SoloProgress.Coins = 70;
            saved.SoloProgress.PendingGameWagers[Guid.NewGuid().ToString("N")] = 30;
            await store.SaveProfileAsync(saved);
            await using var social = new SocialCoordinatorService(NewLan(), store, new SoloProgressionService());
            using var games = new CoinGamesViewModel(store, new SoloProgressionService(), social);
            var profile = await store.LoadProfileAsync();
            var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            games.ProfileChanged += (_, _) => persisted.TrySetResult();
            games.BindProfile(profile);
            games.BindProfile(profile);
            await persisted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(profile.SoloProgress.PendingGameWagers);
            Assert.Equal(100, (await store.LoadProfileAsync()).SoloProgress.Coins);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static PetProfile NewProfile(string name) => new() { PetName = name, SoloProgress = new() { Coins = 100 } };
    private static JsonAppDataStore NewStore(string path) => new(new StateMigrator(), path);
    private static LanP2pTransport NewLan() => new(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
    private static FilteredTransport Wrap(LanP2pTransport lan, ServerConnectionOptions options, string identity)
        => new(new HybridTransportRouter(lan, new GcpSignalRTransport(options, new DeviceIdentityStore(identity))));

    private static FriendEntry NewFriend(PetProfile profile, int port, string secret) => new()
    {
        PetId = profile.PetId, PetName = profile.PetName, PairSecretBase64 = secret,
        LastKnownIp = port > 0 ? IPAddress.Loopback.ToString() : string.Empty, LastKnownTcpPort = port
    };

    private static void Discover(LanP2pTransport lan, FriendEntry friend)
        => lan.HandleDiscoveryHello(new DiscoveryHelloPacket { PetId = friend.PetId, TcpPort = friend.LastKnownTcpPort },
            IPAddress.Loopback.ToString(), CancellationToken.None);

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private sealed class FilteredTransport(ISocialTransport inner) : ISocialTransport
    {
        public Func<GameEventPayload, Func<Task<bool>>, Task<bool>>? Filter { get; set; }
        public IReadOnlyList<PeerSnapshot>? OverridePeers { get; set; }
        public string TransportName => inner.TransportName;
        public bool IsRunning => inner.IsRunning;
        public bool IsRemoteConnected => inner.IsRemoteConnected;
        public string ConnectionSummary => inner.ConnectionSummary;
        public event EventHandler<IReadOnlyList<PeerSnapshot>>? PeersChanged { add => inner.PeersChanged += value; remove => inner.PeersChanged -= value; }
        public event EventHandler<FriendRequestReceivedEventArgs>? FriendRequestReceived { add => inner.FriendRequestReceived += value; remove => inner.FriendRequestReceived -= value; }
        public event EventHandler<FriendAcceptedEventArgs>? FriendAccepted { add => inner.FriendAccepted += value; remove => inner.FriendAccepted -= value; }
        public event EventHandler<FriendRejectedEventArgs>? FriendRejected { add => inner.FriendRejected += value; remove => inner.FriendRejected -= value; }
        public event EventHandler<BubbleMessageReceivedEventArgs>? BubbleMessageReceived { add => inner.BubbleMessageReceived += value; remove => inner.BubbleMessageReceived -= value; }
        public event EventHandler<QuickReactionReceivedEventArgs>? QuickReactionReceived { add => inner.QuickReactionReceived += value; remove => inner.QuickReactionReceived -= value; }
        public event EventHandler<PetInteractionReceivedEventArgs>? PetInteractionReceived { add => inner.PetInteractionReceived += value; remove => inner.PetInteractionReceived -= value; }
        public event EventHandler<MusicFrameReceivedEventArgs>? MusicFrameReceived { add => inner.MusicFrameReceived += value; remove => inner.MusicFrameReceived -= value; }
        public event EventHandler<MusicFollowStateChangedEventArgs>? MusicFollowStateChanged { add => inner.MusicFollowStateChanged += value; remove => inner.MusicFollowStateChanged -= value; }
        public event EventHandler<GameEventReceivedEventArgs>? GameEventReceived { add => inner.GameEventReceived += value; remove => inner.GameEventReceived -= value; }
        public event EventHandler<TransportErrorEventArgs>? TransportError { add => inner.TransportError += value; remove => inner.TransportError -= value; }
        public Task StartAsync(PetProfile p, IReadOnlyList<FriendEntry> f, CancellationToken ct = default) => inner.StartAsync(p, f, ct);
        public Task StopAsync(CancellationToken ct = default) => inner.StopAsync(ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public Task UpdateLocalPresenceAsync(LocalPresenceState p, CancellationToken ct = default) => inner.UpdateLocalPresenceAsync(p, ct);
        public void UpdateKnownFriends(IReadOnlyList<FriendEntry> friends) => inner.UpdateKnownFriends(friends);
        public IReadOnlyList<PeerSnapshot> GetDiscoveredPeers() => OverridePeers ?? inner.GetDiscoveredPeers();
        public Task<bool> SendFriendRequestAsync(PeerSnapshot p, CancellationToken ct = default) => inner.SendFriendRequestAsync(p, ct);
        public Task<FriendOperationResult> SendFriendRequestDetailedAsync(PeerSnapshot p, CancellationToken ct = default) => inner.SendFriendRequestDetailedAsync(p, ct);
        public Task<bool> RespondFriendRequestAsync(string id, bool accept, CancellationToken ct = default) => inner.RespondFriendRequestAsync(id, accept, ct);
        public Task<FriendOperationResult> RespondFriendRequestDetailedAsync(string id, bool accept, CancellationToken ct = default) => inner.RespondFriendRequestDetailedAsync(id, accept, ct);
        public Task<bool> SendBubbleMessageAsync(FriendEntry f, string text, CancellationToken ct = default) => inner.SendBubbleMessageAsync(f, text, ct);
        public Task<bool> SendQuickReactionAsync(FriendEntry f, string id, CancellationToken ct = default) => inner.SendQuickReactionAsync(f, id, ct);
        public Task<bool> SendPetInteractionAsync(FriendEntry f, string type, CancellationToken ct = default) => inner.SendPetInteractionAsync(f, type, ct);
        public Task<bool> SetMusicFollowAsync(FriendEntry f, bool enable, CancellationToken ct = default) => inner.SetMusicFollowAsync(f, enable, ct);
        public Task<bool> SendGameEventAsync(FriendEntry f, GameEventPayload p, CancellationToken ct = default)
            => Filter?.Invoke(p, () => inner.SendGameEventAsync(f, p, ct)) ?? inner.SendGameEventAsync(f, p, ct);
        public ValueTask PublishMusicFrameAsync(MusicFrameData data, CancellationToken ct = default) => inner.PublishMusicFrameAsync(data, ct);
    }
}
