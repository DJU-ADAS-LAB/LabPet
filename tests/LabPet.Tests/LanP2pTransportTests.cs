using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using LabPet.Client.Network;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using Xunit;

namespace LabPet.Tests;

public sealed class LanP2pTransportTests
{
    [Fact]
    public async Task DiscoveryCandidate_RequiresAuthenticationBeforeReplacingFriendPresence()
    {
        await using var transport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
        var friend = new FriendEntry
        {
            PetId = "friend", PetName = "기존 이름", LastKnownIp = "192.0.2.5",
            CachedAppearance = new PetAppearance { SpeciesId = PetSpeciesIds.ChibiCat }
        };
        transport.UpdateKnownFriends([friend]);
        var candidate = new DiscoveryHelloPacket { PetId = friend.PetId, PetName = "새 이름", TcpPort = 12345 };
        transport.HandleDiscoveryHello(candidate, "192.0.2.6", new CancellationToken(canceled: true));

        Assert.Empty(transport.GetDiscoveredPeers());
        Assert.Equal("192.0.2.5", friend.LastKnownIp);
        Assert.Equal(ProtocolConstants.TcpControlPort, friend.LastKnownTcpPort);
        Assert.Equal("기존 이름", friend.PetName);
        Assert.Equal(PetSpeciesIds.ChibiCat, friend.CachedAppearance.SpeciesId);
    }

    [Fact]
    public async Task TcpListener_UsesConfiguredPortAndTimesOutIdleConnection()
    {
        await using var listener = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
        await listener.StartAsync(new PetProfile(), []);
        Assert.True(listener.IsRunning);

        await using var conflicting = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: listener.BoundTcpPort);
        var error = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        conflicting.TransportError += (_, args) => error.TrySetResult(args.Operation);
        await conflicting.StartAsync(new PetProfile(), []);
        Assert.False(conflicting.IsRunning);
        Assert.Equal(0, conflicting.BoundTcpPort);
        Assert.Equal("Listen", await error.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        using var idle = new TcpClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await idle.ConnectAsync(IPAddress.Loopback, listener.BoundTcpPort, deadline.Token);
        var buffer = new byte[1];
        Assert.Equal(0, await idle.GetStream().ReadAsync(buffer, deadline.Token));
    }

    [Fact]
    public void LanNetworkHelper_FiltersVirtualAdaptersAndComputesSubnetBroadcast()
    {
        Assert.True(LanNetworkHelper.IsPhysicalLanAdapter(
            NetworkInterfaceType.Wireless80211, OperationalStatus.Up, "Wi-Fi", "Intel Wi-Fi 6E"));
        Assert.True(LanNetworkHelper.IsPhysicalLanAdapter(
            NetworkInterfaceType.Ethernet, OperationalStatus.Up, "Ethernet", "Realtek PCIe GbE"));

        Assert.False(LanNetworkHelper.IsPhysicalLanAdapter(
            NetworkInterfaceType.Ethernet, OperationalStatus.Up, "vEthernet (WSL)", "Hyper-V Virtual Ethernet"));
        Assert.False(LanNetworkHelper.IsPhysicalLanAdapter(
            NetworkInterfaceType.Ethernet, OperationalStatus.Up, "DockerNAT", "Docker Desktop"));
        Assert.False(LanNetworkHelper.IsPhysicalLanAdapter(
            NetworkInterfaceType.Ethernet, OperationalStatus.Up, "WireGuard Tunnel", "Wintun Userspace Tunnel"));

        var broadcast = LanNetworkHelper.ComputeSubnetBroadcastAddress(
            IPAddress.Parse("192.168.10.42"),
            IPAddress.Parse("255.255.255.0"));
        Assert.Equal(IPAddress.Parse("192.168.10.255"), broadcast);
    }

    [Fact]
    public async Task LanP2pTransport_EndToEnd_FriendHandshake_HmacMessage_And_MusicFollow()
    {
        // 테스트 격리용 임시 포트 (충돌 방지)
        await using var host = new LanP2pTransport(udpDiscoveryPort: 46678, udpMusicPort: 46679, tcpControlPort: 0);
        await using var guest = new LanP2pTransport(udpDiscoveryPort: 46688, udpMusicPort: 46689, tcpControlPort: 0);

        var hostProfile = new PetProfile { OwnerName = "승우", PetName = "호스트모찌" };
        var guestProfile = new PetProfile { OwnerName = "민수", PetName = "게스트냥이" };

        await host.StartAsync(hostProfile, []);
        await guest.StartAsync(guestProfile, []);

        // 1. 친구 신청 -> 호스트 수신 확인 (주인 이름 + 펫 이름 모두 전달)
        var requestTcs = new TaskCompletionSource<LabPet.Shared.Transport.FriendRequestReceivedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        host.FriendRequestReceived += (_, e) => requestTcs.TrySetResult(e);

        var hostPeerSnapshot = new PeerSnapshot
        {
            PetId = hostProfile.PetId,
            OwnerName = hostProfile.OwnerName,
            PetName = hostProfile.PetName,
            IpAddress = "127.0.0.1",
            TcpPort = host.BoundTcpPort,
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion
        };

        var reqSent = await guest.SendFriendRequestAsync(hostPeerSnapshot);
        Assert.True(reqSent);
        var request = await requestTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("민수", request.RequesterOwnerName);
        Assert.Equal("게스트냥이", request.RequesterName);

        // 2. 호스트 친구 수락 -> 비밀키 교환
        var acceptTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostAcceptTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        guest.FriendAccepted += (_, e) => acceptTcs.TrySetResult(e.PairSecretBase64);
        host.FriendAccepted += (_, e) => hostAcceptTcs.TrySetResult(e.PairSecretBase64);

        Assert.False(await host.RespondFriendRequestAsync(Guid.NewGuid().ToString("N"), true));
        Assert.False(acceptTcs.Task.IsCompleted);
        Assert.False(hostAcceptTcs.Task.IsCompleted);
        var acceptSent = await host.RespondFriendRequestAsync(request.RequestId, accept: true);
        Assert.True(acceptSent);
        var pairSecret = await acceptTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(pairSecret, await hostAcceptTcs.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(await host.RespondFriendRequestAsync(request.RequestId, true));

        // 양쪽 친구 테이블 등록
        var guestAsFriend = new FriendEntry
        {
            PetId = guestProfile.PetId,
            PetName = guestProfile.PetName,
            LastKnownIp = "127.0.0.1",
            LastKnownTcpPort = guest.BoundTcpPort,
            PairSecretBase64 = pairSecret
        };
        var hostAsFriend = new FriendEntry
        {
            PetId = hostProfile.PetId,
            PetName = hostProfile.PetName,
            LastKnownIp = "127.0.0.1",
            LastKnownTcpPort = host.BoundTcpPort,
            PairSecretBase64 = pairSecret
        };
        host.UpdateKnownFriends([guestAsFriend]);
        guest.UpdateKnownFriends([hostAsFriend]);

        // 3. HMAC 서명 말풍선 메시지 송수신 확인
        var msgTcs = new TaskCompletionSource<string>();
        host.BubbleMessageReceived += (_, e) => msgTcs.TrySetResult(e.Text);

        var msgOk = await guest.SendBubbleMessageAsync(hostAsFriend, "안녕 호스트!");
        Assert.True(msgOk);
        Assert.Equal("안녕 호스트!", await msgTcs.Task.WaitAsync(TimeSpan.FromSeconds(3)));

        // 4. 잘못된 비밀키로 위조한 메시지는 차단되어야 함
        var fakeFriend = new FriendEntry
        {
            PetId = hostProfile.PetId,
            PetName = hostProfile.PetName,
            LastKnownIp = "127.0.0.1",
            LastKnownTcpPort = host.BoundTcpPort,
            PairSecretBase64 = PacketSigner.GeneratePairSecretBase64()
        };
        var forgedOk = await guest.SendBubbleMessageAsync(fakeFriend, "위조 메시지");
        Assert.False(forgedOk);

        // 5. 노래 따라하기 구독 -> 호스트 30Hz UDP 오디오 프레임 유니캐스트 수신 확인
        var musicTcs = new TaskCompletionSource<MusicFrameData>();
        guest.MusicFrameReceived += (_, e) => musicTcs.TrySetResult(e.Frame);

        var followOk = await guest.SetMusicFollowAsync(hostAsFriend, enable: true);
        Assert.True(followOk);

        await host.PublishMusicFrameAsync(new MusicFrameData(0.82f, 0.45f, 0.30f, IsBeat: true, Sequence: 7));
        var receivedFrame = await musicTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(7u, receivedFrame.Sequence);
        Assert.True(receivedFrame.IsBeat);
        Assert.InRange(receivedFrame.Bass, 0.80f, 0.84f);

        // 6. 친구 전용 HMAC 서명 코인 게임 이벤트 송수신 확인
        var gameTcs = new TaskCompletionSource<GameEventPayload>();
        host.GameEventReceived += (_, e) => gameTcs.TrySetResult(e.Payload);

        var gameOk = await guest.SendGameEventAsync(hostAsFriend, new GameEventPayload
        {
            GameType = "rps",
            SessionId = "test-sess-1",
            Action = "invite",
            WagerCoins = 30
        });
        Assert.True(gameOk);
        var receivedGame = await gameTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("rps", receivedGame.GameType);
        Assert.Equal("invite", receivedGame.Action);
        Assert.Equal(30, receivedGame.WagerCoins);
    }

    [Fact]
    public async Task LanP2pTransport_RejectsUnencryptedFriendControlPacket()
    {
        await using var host = new LanP2pTransport(udpDiscoveryPort: 46778, udpMusicPort: 46779, tcpControlPort: 0);
        var hostProfile = new PetProfile { OwnerName = "호스트", PetName = "모찌" };
        var guestPetId = Guid.NewGuid().ToString("D");
        var pairSecret = PacketSigner.GeneratePairSecretBase64();

        await host.StartAsync(hostProfile,
        [
            new FriendEntry
            {
                PetId = guestPetId,
                PetName = "게스트",
                LastKnownIp = "127.0.0.1",
                PairSecretBase64 = pairSecret
            }
        ]);

        var plainPayload = """{"text":"평문 전송 시도"}""";
        var header = new PacketHeader
        {
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            PacketType = PacketType.BubbleMessage,
            SenderPetId = guestPetId
        };
        header.SignatureBase64 = PacketSigner.ComputeSignature(
            PacketSigner.BuildControlCanonicalString(header, plainPayload),
            pairSecret);

        var unencryptedEnvelope = new ControlPacketEnvelope
        {
            Header = header,
            PayloadJson = plainPayload,
            IsEncrypted = false
        };

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", host.BoundTcpPort);
        await using var stream = client.GetStream();

        var body = JsonSerializer.SerializeToUtf8Bytes(unencryptedEnvelope, ProtocolJson.PacketOptions);
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, body.Length);
        await stream.WriteAsync(prefix);
        await stream.WriteAsync(body);
        await stream.FlushAsync();

        var ackLenBytes = new byte[4];
        await stream.ReadExactlyAsync(ackLenBytes);
        var ackLen = BinaryPrimitives.ReadInt32BigEndian(ackLenBytes);
        var ackBody = new byte[ackLen];
        await stream.ReadExactlyAsync(ackBody);

        var ackEnvelope = JsonSerializer.Deserialize<ControlPacketEnvelope>(ackBody, ProtocolJson.PacketOptions);
        Assert.NotNull(ackEnvelope);
        var ack = JsonSerializer.Deserialize<AckPayload>(ackEnvelope!.PayloadJson, ProtocolJson.PacketOptions);
        Assert.NotNull(ack);
        Assert.False(ack!.Accepted);
        Assert.Equal("ENCRYPTION_REQUIRED", ack.ErrorCode);
    }
}
