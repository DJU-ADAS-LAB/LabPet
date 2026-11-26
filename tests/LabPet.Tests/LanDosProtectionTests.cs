using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Threading;
using LabPet.Client.Network;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;
using Xunit;

namespace LabPet.Tests;

public sealed class LanDosProtectionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "LabPet_LanDos_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task UdpAndTcp_DropOversizedPayloadsAndUdpRateExcessBeforeDeserialization()
    {
        var discPort = GetFreeUdpPort();
        var musicPort = GetFreeUdpPort();
        var opts = new LanDosProtectionOptions
        {
            MaxDiscoveryUdpBytes = 512,
            MaxMusicUdpBytes = 256,
            MaxControlTcpBytes = 1024,
            DiscoveryPerIpPerSecond = 3,
            DiscoveryGlobalPerSecond = 20,
            MusicPerIpPerSecond = 4,
            MusicGlobalPerSecond = 40
        };

        await using var transport = new LanP2pTransport(discPort, musicPort, tcpControlPort: 0, dosOptions: opts);
        await transport.StartAsync(new PetProfile { OwnerName = "방어", PetName = "수신" }, []);
        Assert.True(transport.IsRunning);

        using var senderUdp = new UdpClient();

        // 1. 역직렬화 전 초대형 UDP 탐색·음악 패킷 폐기
        var oversizedDisc = Encoding.UTF8.GetBytes(new string('D', 700));
        var oversizedMusic = Encoding.UTF8.GetBytes(new string('M', 400));
        await senderUdp.SendAsync(oversizedDisc, oversizedDisc.Length, new IPEndPoint(IPAddress.Loopback, discPort));
        await senderUdp.SendAsync(oversizedMusic, oversizedMusic.Length, new IPEndPoint(IPAddress.Loopback, musicPort));

        await WaitUntilAsync(
            () => transport.DroppedOversizedDiscoveryPackets >= 1 && transport.DroppedOversizedMusicPackets >= 1,
            TimeSpan.FromSeconds(5));

        // 2. UDP IP별 빈도 제한 초과 폐기 (유효 크기 이하 무효 JSON으로 빈도 카운터만 소진)
        var smallBytes = "{}"u8.ToArray();
        for (var i = 0; i < 10; i++)
        {
            await senderUdp.SendAsync(smallBytes, smallBytes.Length, new IPEndPoint(IPAddress.Loopback, discPort));
            await senderUdp.SendAsync(smallBytes, smallBytes.Length, new IPEndPoint(IPAddress.Loopback, musicPort));
        }

        await WaitUntilAsync(
            () => transport.DroppedRateLimitedDiscoveryPackets >= 1 && transport.DroppedRateLimitedMusicPackets >= 1,
            TimeSpan.FromSeconds(5));

        // 3. TCP 길이 프리픽스가 MaxControlTcpBytes 초과이면 본문 할당 전 즉시 연결 종료
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, transport.BoundTcpPort);
        var stream = tcp.GetStream();
        var lenHeader = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenHeader, opts.MaxControlTcpBytes + 1);
        await stream.WriteAsync(lenHeader);
        await stream.FlushAsync();

        var probe = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var read = await stream.ReadAsync(probe, cts.Token);
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task TcpControl_LimitsUnauthenticatedFlood_WhilePreservingFriendReservedSlots()
    {
        var opts = new LanDosProtectionOptions
        {
            MaxTotalControlConnections = 6,
            ReservedFriendControlConnections = 4,
            MaxConcurrentUnauthenticatedTcpPerIp = 2,
            MaxConcurrentTcpPerIp = 4,
            TcpRequestsPerIpPerSecond = 30,
            TcpRequestsGlobalPerSecond = 80
        };

        var secret = PacketSigner.GeneratePairSecretBase64();
        var receiverProfile = new PetProfile { OwnerName = "수신자", PetName = "모찌" };
        var senderProfile = new PetProfile { OwnerName = "친구발신", PetName = "코코" };

        await using var receiver = new LanP2pTransport(0, 0, tcpControlPort: 0, dosOptions: opts);
        await using var friendSender = new LanP2pTransport(0, 0, tcpControlPort: 0);

        var friendEntryOnReceiver = new FriendEntry
        {
            PetId = senderProfile.PetId,
            OwnerName = senderProfile.OwnerName,
            PetName = senderProfile.PetName,
            PairSecretBase64 = secret,
            LastKnownIp = IPAddress.Loopback.ToString(),
            LastKnownTcpPort = 45680
        };

        var receivedMessages = new List<string>();
        receiver.BubbleMessageReceived += (_, e) => receivedMessages.Add(e.Text);

        await receiver.StartAsync(receiverProfile, [friendEntryOnReceiver]);
        var targetFriendOnSender = new FriendEntry
        {
            PetId = receiverProfile.PetId,
            OwnerName = receiverProfile.OwnerName,
            PetName = receiverProfile.PetName,
            PairSecretBase64 = secret,
            LastKnownIp = IPAddress.Loopback.ToString(),
            LastKnownTcpPort = receiver.BoundTcpPort
        };
        await friendSender.StartAsync(senderProfile, [targetFriendOnSender]);

        var heldSockets = new List<TcpClient>();
        try
        {
            // 127.0.0.2(비친구 IP)로 비친구 슬롯 2개(MaxTotal 6 - Reserved 4 = 2) 포화
            for (var i = 0; i < 2; i++)
            {
                var unauth = new TcpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
                await unauth.ConnectAsync(IPAddress.Loopback, receiver.BoundTcpPort);
                heldSockets.Add(unauth);
            }

            await WaitUntilAsync(() => receiver.ActiveTcpConnectionCount == 2, TimeSpan.FromSeconds(5));

            // 같은 비친구 IP(127.0.0.2) 3번째 연결 및 다른 비친구 IP(127.0.0.3) 연결 모두 차단
            foreach (var extraIp in new[] { "127.0.0.2", "127.0.0.3" })
            {
                using var rejected = new TcpClient(new IPEndPoint(IPAddress.Parse(extraIp), 0));
                await rejected.ConnectAsync(IPAddress.Loopback, receiver.BoundTcpPort);
                var buf = new byte[1];
                using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var n = await rejected.GetStream().ReadAsync(buf, readCts.Token);
                Assert.Equal(0, n);
            }

            Assert.True(receiver.DroppedTcpConnectionsByLimit >= 2);

            // 비친구 슬롯이 꽉 찬 상태에서도 친구(127.0.0.1)는 친구 전용 예약 슬롯으로 정상 전달됨
            var res = await friendSender.SendBubbleMessageDetailedAsync(targetFriendOnSender, "예약슬롯정상수신");
            Assert.True(res.Succeeded);
            Assert.Contains("예약슬롯정상수신", receivedMessages);
        }
        finally
        {
            foreach (var sock in heldSockets)
            {
                sock.Dispose();
            }
        }
    }

    [Fact]
    public async Task DiscoveryCache_CapsUnauthenticatedPeersExpiresTtl_AndThrottlesDuplicateUiEvents()
    {
        var opts = new LanDosProtectionOptions
        {
            MaxUnauthenticatedPeers = 4,
            UnauthenticatedPeerTtlSeconds = 1,
            PeersChangedMinIntervalMs = 200
        };

        await using var friendNode = new LanP2pTransport(0, 0, tcpControlPort: 0);
        await using var observer = new LanP2pTransport(0, 0, tcpControlPort: 0, dosOptions: opts);

        var secret = PacketSigner.GeneratePairSecretBase64();
        var friendProfile = new PetProfile { OwnerName = "친구", PetName = "흰둥이" };
        var observerProfile = new PetProfile { OwnerName = "관찰자", PetName = "모찌" };

        var friendEntry = new FriendEntry
        {
            PetId = friendProfile.PetId,
            OwnerName = friendProfile.OwnerName,
            PetName = friendProfile.PetName,
            PairSecretBase64 = secret
        };
        var reverseFriend = new FriendEntry
        {
            PetId = observerProfile.PetId,
            OwnerName = observerProfile.OwnerName,
            PetName = observerProfile.PetName,
            PairSecretBase64 = secret,
            LastKnownIp = IPAddress.Loopback.ToString()
        };

        var peersChangedCount = 0;
        observer.PeersChanged += (_, _) => Interlocked.Increment(ref peersChangedCount);

        await friendNode.StartAsync(friendProfile, [reverseFriend]);
        await observer.StartAsync(observerProfile, [friendEntry]);

        // 친구 온라인 등록 (PresenceSync TCP 검증 완료 후 친구로 등록됨)
        observer.HandleDiscoveryHello(new DiscoveryHelloPacket
        {
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            AppVersion = "0.3.17",
            PetId = friendProfile.PetId,
            OwnerName = friendProfile.OwnerName,
            PetName = friendProfile.PetName,
            TcpPort = friendNode.BoundTcpPort
        }, IPAddress.Loopback.ToString(), CancellationToken.None);

        await WaitUntilAsync(
            () => observer.GetDiscoveredPeers().Any(p => p.PetId == friendProfile.PetId && p.IsFriend),
            TimeSpan.FromSeconds(5));

        var countAfterFriend = Volatile.Read(ref peersChangedCount);

        // 미인증 탐색 피어 7개 유입 -> 상한 4개 유지
        for (var i = 0; i < 7; i++)
        {
            observer.HandleDiscoveryHello(new DiscoveryHelloPacket
            {
                ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                AppVersion = "0.3.17",
                PetId = $"unauth-{i}",
                OwnerName = $"낯선{i}",
                PetName = $"펫{i}",
                TcpPort = 46000 + i
            }, "192.0.2.50", CancellationToken.None);
        }

        var snapshotAfterFlood = observer.GetDiscoveredPeers();
        Assert.Equal(4, snapshotAfterFlood.Count(p => !p.IsFriend));
        Assert.Contains(snapshotAfterFlood, p => p.PetId == friendProfile.PetId && p.IsFriend);

        // 디바운스 안정화 대기 후 동일 내용 HELLO 10회 재수신 -> UI 갱신 이벤트 재발행 없음
        await Task.Delay(260);
        var countBeforeDuplicates = Volatile.Read(ref peersChangedCount);
        Assert.True(countBeforeDuplicates - countAfterFriend <= 3);

        for (var i = 0; i < 10; i++)
        {
            observer.HandleDiscoveryHello(new DiscoveryHelloPacket
            {
                ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                AppVersion = "0.3.17",
                PetId = "unauth-0",
                OwnerName = "낯선0",
                PetName = "펫0",
                TcpPort = 46000
            }, "192.0.2.50", CancellationToken.None);
        }

        await Task.Delay(240);
        Assert.Equal(countBeforeDuplicates, Volatile.Read(ref peersChangedCount));

        // 1.25초 경과 후 TTL 만료 정리 -> 미인증 피어는 제거되고 친구(10초 TTL)는 유지
        await Task.Delay(1050);
        var pruned = observer.PruneStalePeers();
        Assert.Equal(4, pruned);

        var remaining = observer.GetDiscoveredPeers();
        var onlyFriend = Assert.Single(remaining);
        Assert.Equal(friendProfile.PetId, onlyFriend.PetId);
        Assert.True(onlyFriend.IsFriend);
    }

    [Fact]
    public async Task FeatureLimits_ThrottleControlSpam_WhileNormal30HzLanMusicSyncContinues()
    {
        var hostDisc = GetFreeUdpPort();
        var hostMusic = GetFreeUdpPort();
        var guestDisc = GetFreeUdpPort();
        var guestMusic = GetFreeUdpPort();

        var hostOpts = new LanDosProtectionOptions
        {
            MessagePerSenderPer10Seconds = 3,
            GameEventPerSenderPer10Seconds = 4
        };
        var guestOpts = new LanDosProtectionOptions
        {
            MusicPerFriendPerSecond = 45,
            MusicPerIpPerSecond = 120,
            MusicGlobalPerSecond = 400
        };

        var secret = PacketSigner.GeneratePairSecretBase64();
        var hostProfile = new PetProfile { OwnerName = "호스트", PetName = "리듬모찌" };
        var guestProfile = new PetProfile { OwnerName = "게스트", PetName = "댄스냥" };

        await using var host = new LanP2pTransport(hostDisc, hostMusic, tcpControlPort: 0, dosOptions: hostOpts);
        await using var guest = new LanP2pTransport(guestDisc, guestMusic, tcpControlPort: 0, dosOptions: guestOpts);

        await host.StartAsync(hostProfile, []);
        await guest.StartAsync(guestProfile, []);

        var hostEntryOnGuest = new FriendEntry
        {
            PetId = hostProfile.PetId,
            OwnerName = hostProfile.OwnerName,
            PetName = hostProfile.PetName,
            PairSecretBase64 = secret,
            LastKnownIp = IPAddress.Loopback.ToString(),
            LastKnownTcpPort = host.BoundTcpPort
        };
        var guestEntryOnHost = new FriendEntry
        {
            PetId = guestProfile.PetId,
            OwnerName = guestProfile.OwnerName,
            PetName = guestProfile.PetName,
            PairSecretBase64 = secret,
            LastKnownIp = IPAddress.Loopback.ToString(),
            LastKnownTcpPort = guest.BoundTcpPort
        };

        host.UpdateKnownFriends([guestEntryOnHost]);
        guest.UpdateKnownFriends([hostEntryOnGuest]);

        // 1. 말풍선·게임 이벤트 기능별 빈도 제한 검증
        var messageResults = new List<TransportSendResult>();
        for (var i = 0; i < 6; i++)
        {
            messageResults.Add(await guest.SendBubbleMessageDetailedAsync(hostEntryOnGuest, $"메시지_{i}"));
        }

        Assert.Equal(3, messageResults.Count(r => r.Succeeded));
        Assert.Equal(3, messageResults.Count(r => !r.Succeeded && r.ErrorCode == FriendOperationErrorCodes.RateLimited));

        var gameResults = new List<TransportSendResult>();
        for (var i = 0; i < 6; i++)
        {
            gameResults.Add(await guest.SendGameEventDetailedAsync(hostEntryOnGuest, new GameEventPayload
            {
                GameType = "rps",
                SessionId = "s1",
                Action = $"act_{i}",
                WagerCoins = 10
            }));
        }

        Assert.Equal(4, gameResults.Count(r => r.Succeeded));
        Assert.Equal(2, gameResults.Count(r => !r.Succeeded && r.ErrorCode == FriendOperationErrorCodes.RateLimited));

        // 2. 정상 30Hz LAN 오디오 동기화 무손실/비차단 동작 확인
        var musicCount = 0;
        guest.MusicFrameReceived += (_, _) => Interlocked.Increment(ref musicCount);

        var followOk = await guest.SetMusicFollowAsync(hostEntryOnGuest, enable: true);
        Assert.True(followOk);

        for (uint seq = 1; seq <= 15; seq++)
        {
            await host.PublishMusicFrameAsync(new MusicFrameData(0.75f, 0.50f, 0.30f, seq % 2 == 0, seq));
            await Task.Delay(33);
        }

        await WaitUntilAsync(() => Volatile.Read(ref musicCount) >= 12, TimeSpan.FromSeconds(5));
        Assert.Equal(0, guest.DroppedRateLimitedMusicPackets);
    }

    [Fact]
    public async Task SnowballService_RejectsStrangerDuplicateStaleAndBurstBeforeDispatcherEnqueue()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            Directory.CreateDirectory(_tempRoot);
            var store = new JsonAppDataStore(new StateMigrator(), _tempRoot);
            var friendPetId = Guid.NewGuid().ToString("D");
            var strangerPetId = Guid.NewGuid().ToString("D");
            var friendA = new FriendEntry
            {
                PetId = friendPetId,
                OwnerName = "친구",
                PetName = "모찌",
                PairSecretBase64 = PacketSigner.GeneratePairSecretBase64()
            };
            await store.SaveFriendsAsync(new FriendsStore { Friends = [friendA] });

            var transport = new StubSocialTransport();
            var progression = new SoloProgressionService();
            await using var social = new SocialCoordinatorService(transport, store, progression);
            await social.StartAsync(new PetProfile { OwnerName = "나", PetName = "내펫" });

            var now = DateTimeOffset.UtcNow;
            using var service = new SnowballInteractionService(transport, Dispatcher.CurrentDispatcher, TimeProvider.System, social);

            var delivered = new List<Guid>();
            service.Received += (_, e) => delivered.Add(e.EventId);

            // 1. 미등록 발신자 -> Dispatcher 적재 전 차단
            transport.RaiseSnowball(new SnowballReceivedEventArgs(strangerPetId, Guid.NewGuid(), now));

            // 2. 정상 친구 이벤트 1건 -> 통과
            var validId = Guid.NewGuid();
            transport.RaiseSnowball(new SnowballReceivedEventArgs(friendPetId, validId, now));

            // 3. 동일 EventId 중복 -> Dispatcher 적재 전 차단
            transport.RaiseSnowball(new SnowballReceivedEventArgs(friendPetId, validId, now));

            // 4. 오래된 타임스탬프(15초 전) -> Dispatcher 적재 전 차단
            transport.RaiseSnowball(new SnowballReceivedEventArgs(friendPetId, Guid.NewGuid(), now.AddSeconds(-15)));

            // 5. 단기 폭주 5건 -> 버스트 허용 1건만 추가 통과, 나머지 4건은 Dispatcher 전 차단
            for (var i = 0; i < 5; i++)
            {
                transport.RaiseSnowball(new SnowballReceivedEventArgs(friendPetId, Guid.NewGuid(), now));
            }

            // Dispatcher 큐 플러시
            await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);

            Assert.Equal(2, delivered.Count);
            Assert.Equal(7, service.RejectedBeforeDispatcherCount);
        });
    }

    private sealed class StubSocialTransport : ISocialTransport
    {
        public string TransportName => "Stub";
        public bool IsRunning => true;

        public event EventHandler<IReadOnlyList<PeerSnapshot>>? PeersChanged;
        public event EventHandler<FriendRequestReceivedEventArgs>? FriendRequestReceived;
        public event EventHandler<FriendAcceptedEventArgs>? FriendAccepted;
        public event EventHandler<FriendRejectedEventArgs>? FriendRejected;
        public event EventHandler<BubbleMessageReceivedEventArgs>? BubbleMessageReceived;
        public event EventHandler<QuickReactionReceivedEventArgs>? QuickReactionReceived;
        public event EventHandler<PetInteractionReceivedEventArgs>? PetInteractionReceived;
        public event EventHandler<SnowballReceivedEventArgs>? SnowballReceived;
        public event EventHandler<MusicFrameReceivedEventArgs>? MusicFrameReceived;
        public event EventHandler<MusicFollowStateChangedEventArgs>? MusicFollowStateChanged;
        public event EventHandler<GameEventReceivedEventArgs>? GameEventReceived;
        public event EventHandler<TransportErrorEventArgs>? TransportError;

        public void RaiseSnowball(SnowballReceivedEventArgs args) => SnowballReceived?.Invoke(this, args);

        public Task StartAsync(PetProfile localProfile, IReadOnlyList<FriendEntry> friends, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateLocalPresenceAsync(LocalPresenceState presence, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void UpdateKnownFriends(IReadOnlyList<FriendEntry> friends) { }
        public IReadOnlyList<PeerSnapshot> GetDiscoveredPeers() => [];
        public Task<bool> SendFriendRequestAsync(PeerSnapshot target, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<FriendOperationResult> SendFriendRequestDetailedAsync(PeerSnapshot target, CancellationToken cancellationToken = default)
            => Task.FromResult(FriendOperationResult.Fail(FriendOperationStage.Validation, "STUB", isTransient: false, detail: "지원 안 함"));
        public Task<bool> RespondFriendRequestAsync(string requestId, bool accept, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<FriendOperationResult> RespondFriendRequestDetailedAsync(string requestId, bool accept, CancellationToken cancellationToken = default)
            => Task.FromResult(FriendOperationResult.Fail(FriendOperationStage.Validation, "STUB", isTransient: false, detail: "지원 안 함"));
        public Task<bool> SendBubbleMessageAsync(FriendEntry friend, string message, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> SendQuickReactionAsync(FriendEntry friend, string reactionId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> SendPetInteractionAsync(FriendEntry friend, string interactionType, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public SnowballPeerStatus GetSnowballPeerStatus(string petId) => SnowballPeerStatus.Available;
        public Task<bool> SetMusicFollowAsync(FriendEntry hostFriend, bool enable, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> SendGameEventAsync(FriendEntry friend, GameEventPayload payload, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public ValueTask PublishMusicFrameAsync(MusicFrameData frame, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync()
        {
            _ = (PeersChanged, FriendRequestReceived, FriendAccepted, FriendRejected, BubbleMessageReceived,
                QuickReactionReceived, PetInteractionReceived, MusicFrameReceived, MusicFollowStateChanged,
                GameEventReceived, TransportError);
            return ValueTask.CompletedTask;
        }
    }

    private static int GetFreeUdpPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("지정 시간 내에 기대 상태에 도달하지 못했습니다.");
            }

            await Task.Delay(25);
        }
    }
}
