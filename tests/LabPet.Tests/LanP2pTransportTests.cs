using System.Net;
using System.Net.NetworkInformation;
using LabPet.Client.Network;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using Xunit;

namespace LabPet.Tests;

public sealed class LanP2pTransportTests
{
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
        var requestTcs = new TaskCompletionSource<(string Owner, string Pet)>();
        host.FriendRequestReceived += (_, e) => requestTcs.TrySetResult((e.RequesterOwnerName, e.RequesterName));

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
        var (reqOwner, reqPet) = await requestTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("민수", reqOwner);
        Assert.Equal("게스트냥이", reqPet);

        // 2. 호스트 친구 수락 -> 비밀키 교환
        var acceptTcs = new TaskCompletionSource<string>();
        guest.FriendAccepted += (_, e) => acceptTcs.TrySetResult(e.PairSecretBase64);

        var pairSecret = PacketSigner.GeneratePairSecretBase64();
        var acceptSent = await host.RespondFriendRequestAsync(
            guestProfile.PetId, "127.0.0.1", guest.BoundTcpPort, accept: true, pairSecret);
        Assert.True(acceptSent);
        Assert.Equal(pairSecret, await acceptTcs.Task.WaitAsync(TimeSpan.FromSeconds(3)));

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
}
