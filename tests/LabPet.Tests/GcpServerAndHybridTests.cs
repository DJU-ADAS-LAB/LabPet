using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using LabPet.Client.Network;
using LabPet.Client.Social;
using LabPet.Client.Storage;
using LabPet.Server;
using LabPet.Server.Options;
using LabPet.Server.Storage;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LabPet.Tests;

public sealed class GcpServerAndHybridTests : IAsyncLifetime
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "LabPet_GcpTest_" + Guid.NewGuid().ToString("N"));
    private readonly int _serverPort = GetFreeTcpPort();
    private WebApplication? _server;

    private string ServerDataDir => Path.Combine(_tempRoot, "server");
    private string ServerUrl => $"http://127.0.0.1:{_serverPort}";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(ServerDataDir);
        await StartServerAsync();
    }

    public async Task DisposeAsync()
    {
        await StopServerAsync();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private async Task StartServerAsync()
    {
        var opts = new ServerOptions
        {
            ListenUrl = ServerUrl,
            ServerSecret = "unit-test-fixed-secret-for-reconnect-consistency-2026!",
            RequireConfiguredSecret = true,
            DataDirectory = ServerDataDir
        };
        _server = LabPetServerHost.BuildApplication(opts);
        await _server.StartAsync();
    }

    private async Task StopServerAsync()
    {
        if (_server is not null)
        {
            await _server.StopAsync();
            await _server.DisposeAsync();
            _server = null;
        }
    }

    [Fact]
    public async Task HealthAndDeviceAuth_EnforcesSingleUseNonceAndRejectsPetIdImpersonation()
    {
        using var http = new HttpClient { BaseAddress = new Uri(ServerUrl) };

        // 1. /health 확인
        var healthRes = await http.GetAsync(SocialHubConstants.HealthPath);
        Assert.Equal(HttpStatusCode.OK, healthRes.StatusCode);
        var healthJson = await healthRes.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", healthJson, StringComparison.Ordinal);

        // 2. ECDSA 기기 인증 성공 및 토큰 발급
        var idDirA = Path.Combine(_tempRoot, "devA");
        var storeA = new DeviceIdentityStore(idDirA);
        var petId = Guid.NewGuid().ToString("D");
        var (pubA, keyA) = storeA.GetOrCreateKeyPair(petId);

        var chRes = await http.PostAsJsonAsync(SocialHubConstants.AuthChallengePath, new AuthChallengeRequest
        {
            PetId = petId,
            DevicePublicKeyBase64 = pubA
        });
        chRes.EnsureSuccessStatusCode();
        var ch = (await chRes.Content.ReadFromJsonAsync<AuthChallengeResponse>())!;
        var sigA = RemoteAuthSigner.SignChallenge(
            keyA,
            RemoteAuthSigner.BuildChallengeCanonicalString(ch.Nonce, petId, pubA, ch.IssuedAtUtc));

        var tokenReq = new AuthTokenRequest
        {
            PetId = petId,
            FriendCode = "LP-AAAA-1111",
            OwnerName = "앨리스",
            PetName = "모찌",
            DevicePublicKeyBase64 = pubA,
            Nonce = ch.Nonce,
            IssuedAtUtc = ch.IssuedAtUtc,
            SignatureBase64 = sigA
        };
        var tokRes1 = await http.PostAsJsonAsync(SocialHubConstants.AuthTokenPath, tokenReq);
        Assert.Equal(HttpStatusCode.OK, tokRes1.StatusCode);
        var authPayload = await tokRes1.Content.ReadFromJsonAsync<AuthTokenResponse>();
        Assert.NotNull(authPayload);
        Assert.Equal(petId, authPayload!.VerifiedPetId);

        // 3. 동일 Nonce 재사용 -> 401 차단
        var tokReplay = await http.PostAsJsonAsync(SocialHubConstants.AuthTokenPath, tokenReq);
        Assert.Equal(HttpStatusCode.Unauthorized, tokReplay.StatusCode);

        // 4. 다른 기기 공개키로 동일 PetId 사칭 -> 403 Forbidden 차단
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var attackerPub = Convert.ToBase64String(attackerKey.ExportSubjectPublicKeyInfo());
        var ch2 = await (await http.PostAsJsonAsync(SocialHubConstants.AuthChallengePath, new AuthChallengeRequest
        {
            PetId = petId,
            DevicePublicKeyBase64 = attackerPub
        })).Content.ReadFromJsonAsync<AuthChallengeResponse>();
        var sigAttacker = RemoteAuthSigner.SignChallenge(
            attackerKey,
            RemoteAuthSigner.BuildChallengeCanonicalString(ch2!.Nonce, petId, attackerPub, ch2.IssuedAtUtc));
        var impersonateRes = await http.PostAsJsonAsync(SocialHubConstants.AuthTokenPath, new AuthTokenRequest
        {
            PetId = petId,
            DevicePublicKeyBase64 = attackerPub,
            Nonce = ch2.Nonce,
            IssuedAtUtc = ch2.IssuedAtUtc,
            SignatureBase64 = sigAttacker
        });
        Assert.Equal(HttpStatusCode.Forbidden, impersonateRes.StatusCode);
        keyA.Dispose();
    }

    [Fact]
    public async Task GcpRelay_EndToEnd_PairingMessagingGamesMusicThrottleNoStorageAndAuthorization()
    {
        var serverOpts = new ServerConnectionOptions { Enabled = true, ServerUrl = ServerUrl };
        var idStoreA = new DeviceIdentityStore(Path.Combine(_tempRoot, "cA"));
        var idStoreB = new DeviceIdentityStore(Path.Combine(_tempRoot, "cB"));
        var idStoreC = new DeviceIdentityStore(Path.Combine(_tempRoot, "cC"));

        await using var clientA = new GcpSignalRTransport(serverOpts, idStoreA);
        await using var clientB = new GcpSignalRTransport(serverOpts, idStoreB);
        await using var clientC = new GcpSignalRTransport(serverOpts, idStoreC);

        var profileA = new PetProfile { OwnerName = "승우", PetName = "모찌A", FriendCode = "LP-1111-AAAA" };
        var profileB = new PetProfile { OwnerName = "민수", PetName = "냥이B", FriendCode = "LP-2222-BBBB" };
        var profileC = new PetProfile { OwnerName = "제3자", PetName = "구름C", FriendCode = "LP-3333-CCCC" };

        await clientA.StartAsync(profileA, []);
        await clientB.StartAsync(profileB, []);
        await clientC.StartAsync(profileC, []);

        Assert.True(clientA.IsRemoteConnected);
        Assert.True(clientB.IsRemoteConnected);
        Assert.True(clientC.IsRemoteConnected);

        // 1. 비인가 사용자 C -> B 미친구 상태 메시지/게임 이벤트 중계 차단
        var attackerRes = await clientC.SendBubbleMessageAsync(new FriendEntry { PetId = profileB.PetId, PairSecretBase64 = PacketSigner.GeneratePairSecretBase64() }, "해킹");
        Assert.False(attackerRes);

        // 2. 기존 '주변 펫' 방식 원격 탐색 및 프라이버시 마스킹 확인 (비친구에게는 음악/수면/친구코드 비공개)
        await clientB.UpdateLocalPresenceAsync(new LocalPresenceState(
            profileB.OwnerName,
            profileB.PetName,
            profileB.Appearance.Clone(),
            profileB.SoloProgress.Level,
            IsMusicActive: true,
            AppVersion: "0.2.2",
            IsSleeping: true,
            FriendCode: profileB.FriendCode));
        await WaitUntilAsync(() => clientA.GetDiscoveredPeers().Any(p => p.PetId == profileB.PetId), TimeSpan.FromSeconds(5));

        var peerBOnA = clientA.GetDiscoveredPeers().Single(p => p.PetId == profileB.PetId);
        Assert.False(peerBOnA.IsFriend);
        Assert.Equal(PeerTransportKind.GcpRelay, peerBOnA.TransportKind);
        Assert.Equal("GCP", peerBOnA.IpAddress);
        Assert.False(peerBOnA.IsMusicActive);
        Assert.False(peerBOnA.IsSleeping);
        Assert.Equal(string.Empty, peerBOnA.FriendCode);

        // C도 온라인 목록에서 비친구로 보이지만 IsFriend=false이고 상호 친구가 아니므로 메시지·게임 중계 불가
        await WaitUntilAsync(() => clientC.GetDiscoveredPeers().Any(p => p.PetId == profileB.PetId), TimeSpan.FromSeconds(5));
        Assert.All(clientC.GetDiscoveredPeers(), p => Assert.False(p.IsFriend));

        // 3. 기존 주변 펫 탐색 스냅샷으로 인터넷 친구 신청/수락(P-256 ECDH 키 합의)
        var reqTcs = new TaskCompletionSource<FriendRequestReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientB.FriendRequestReceived += (_, e) => reqTcs.TrySetResult(e);

        var accATcs = new TaskCompletionSource<FriendAcceptedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accBTcs = new TaskCompletionSource<FriendAcceptedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientA.FriendAccepted += (_, e) => accATcs.TrySetResult(e);
        clientB.FriendAccepted += (_, e) => accBTcs.TrySetResult(e);

        var sentReq = await clientA.SendFriendRequestAsync(peerBOnA);
        Assert.True(sentReq);

        var incomingReq = await reqTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await clientB.RespondFriendRequestAsync(incomingReq.RequestId, accept: true));

        var acceptedOnA = await accATcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var acceptedOnB = await accBTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(acceptedOnA.PairSecretBase64, acceptedOnB.PairSecretBase64);

        var friendBOnA = new FriendEntry { PetId = profileB.PetId, PetName = profileB.PetName, PairSecretBase64 = acceptedOnA.PairSecretBase64 };
        var friendAOnB = new FriendEntry { PetId = profileA.PetId, PetName = profileA.PetName, PairSecretBase64 = acceptedOnB.PairSecretBase64 };
        clientA.UpdateKnownFriends([friendBOnA]);
        clientB.UpdateKnownFriends([friendAOnB]);

        // 상호 친구 승격 후에는 B의 음악/수면 상태가 A에게 동기화됨 (비친구 C에게는 여전히 미노출)
        await WaitUntilAsync(() => clientA.GetDiscoveredPeers().Any(p => p.PetId == profileB.PetId && p.IsFriend && p.IsMusicActive && p.IsSleeping), TimeSpan.FromSeconds(5));
        var hiddenFromC = clientC.GetDiscoveredPeers().Single(p => p.PetId == profileB.PetId);
        Assert.False(hiddenFromC.IsMusicActive);
        Assert.False(hiddenFromC.IsSleeping);

        // 4. 양방향 E2EE 말풍선, 빠른 반응, 쓰다듬기, 게임 이벤트 중계
        const string secretMessage = "인터넷_종단간암호화_비밀채팅_메시지_9911";
        var msgOnBTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientB.BubbleMessageReceived += (_, e) => msgOnBTcs.TrySetResult(e.Text);
        Assert.True(await clientA.SendBubbleMessageAsync(friendBOnA, secretMessage));
        Assert.Equal(secretMessage, await msgOnBTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        var reactOnATcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientA.QuickReactionReceived += (_, e) => reactOnATcs.TrySetResult(e.ReactionId);
        Assert.True(await clientB.SendQuickReactionAsync(friendAOnB, "♥"));
        Assert.Equal("♥", await reactOnATcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        var petOnATcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientA.PetInteractionReceived += (_, e) => petOnATcs.TrySetResult(e.InteractionType);
        Assert.True(await clientB.SendPetInteractionAsync(friendAOnB, "pet"));
        Assert.Equal("pet", await petOnATcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        var gameOnBTcs = new TaskCompletionSource<GameEventPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientB.GameEventReceived += (_, e) => gameOnBTcs.TrySetResult(e.Payload);
        Assert.True(await clientA.SendGameEventAsync(friendBOnA, new GameEventPayload
        {
            GameType = "rps",
            SessionId = "gcp-sess-1",
            Action = "invite",
            WagerCoins = 25
        }));
        var rxGame = await gameOnBTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("rps", rxGame.GameType);
        Assert.Equal(25, rxGame.WagerCoins);

        // 5. 원격 노래 따라하기 (구독자 있을 때만 중계 + 스로틀링 검증)
        var receivedMusic = new List<uint>();
        clientA.MusicFrameReceived += (_, e) => { lock (receivedMusic) { receivedMusic.Add(e.Frame.Sequence); } };
        // 구독 전에는 전송 안 됨
        await clientB.PublishMusicFrameAsync(new MusicFrameData(0.7f, 0.5f, 0.4f, true, Sequence: 1));
        await Task.Delay(120);
        Assert.Empty(receivedMusic);

        Assert.True(await clientA.SetMusicFollowAsync(friendBOnA, enable: true));
        for (uint seq = 10; seq <= 25; seq++)
        {
            await clientB.PublishMusicFrameAsync(new MusicFrameData(0.8f, 0.4f, 0.3f, true, seq));
            await Task.Delay(25);
        }

        await Task.Delay(180);
        lock (receivedMusic)
        {
            // 스로틀링(>=66ms) 덕분에 16연속 호출 중 일부만 전송되어야 함 (>0 이면서 <16)
            Assert.InRange(receivedMusic.Count, 1, 10);
        }

        // 6. 비암호화/발신자 사칭 차단 (직접 Hub 호출 검증)
        var attackerStore = new DeviceIdentityStore(Path.Combine(_tempRoot, "cC"));
        var (_, keyC) = attackerStore.GetOrCreateKeyPair(profileC.PetId);
        using var http = new HttpClient { BaseAddress = new Uri(ServerUrl) };
        var authChallenge = await (await http.PostAsJsonAsync(SocialHubConstants.AuthChallengePath, new AuthChallengeRequest
        {
            PetId = profileC.PetId,
            DevicePublicKeyBase64 = Convert.ToBase64String(keyC.ExportSubjectPublicKeyInfo())
        })).Content.ReadFromJsonAsync<AuthChallengeResponse>();
        var pubC = Convert.ToBase64String(keyC.ExportSubjectPublicKeyInfo());
        var tokenRes = await (await http.PostAsJsonAsync(SocialHubConstants.AuthTokenPath, new AuthTokenRequest
        {
            PetId = profileC.PetId,
            DevicePublicKeyBase64 = pubC,
            Nonce = authChallenge!.Nonce,
            IssuedAtUtc = authChallenge.IssuedAtUtc,
            SignatureBase64 = RemoteAuthSigner.SignChallenge(
                keyC, RemoteAuthSigner.BuildChallengeCanonicalString(authChallenge.Nonce, profileC.PetId, pubC, authChallenge.IssuedAtUtc))
        })).Content.ReadFromJsonAsync<AuthTokenResponse>();
        keyC.Dispose();

        await using var rawHub = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(ServerUrl), SocialHubConstants.HubPath.TrimStart('/')), o =>
                o.AccessTokenProvider = () => Task.FromResult<string?>(tokenRes!.AccessToken))
            .AddJsonProtocol(p =>
            {
                p.PayloadSerializerOptions.PropertyNamingPolicy = ProtocolJson.PacketOptions.PropertyNamingPolicy;
                p.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
            })
            .Build();
        await rawHub.StartAsync();

        // 타인(A) 사칭 시도 -> SENDER_MISMATCH
        var spoofResult = await rawHub.InvokeAsync<RelayDeliveryResult>(
            SocialHubConstants.RelayControlPacketMethod,
            new RelayControlEnvelopeRequest(profileB.PetId, new ControlPacketEnvelope
            {
                Header = new PacketHeader
                {
                    SenderPetId = profileA.PetId,
                    PacketType = PacketType.BubbleMessage
                },
                PayloadJson = """{"text":"위조"}""",
                IsEncrypted = true
            }));
        Assert.False(spoofResult.Delivered);
        Assert.Equal("SENDER_MISMATCH", spoofResult.ErrorCode);

        // 비친구 대상 비암호화/미승인 시도 -> NOT_MUTUAL_FRIEND
        var unauthResult = await rawHub.InvokeAsync<RelayDeliveryResult>(
            SocialHubConstants.RelayControlPacketMethod,
            new RelayControlEnvelopeRequest(profileB.PetId, new ControlPacketEnvelope
            {
                Header = new PacketHeader
                {
                    SenderPetId = profileC.PetId,
                    PacketType = PacketType.BubbleMessage
                },
                PayloadJson = """{"text":"미승인평문"}""",
                IsEncrypted = false
            }));
        Assert.False(unauthResult.Delivered);
        Assert.Equal("NOT_MUTUAL_FRIEND", unauthResult.ErrorCode);

        // 7. 메시지 비저장 검증: B 오프라인 상태에서 A가 전송 -> 실패 반환 + 재접속 후에도 지연 전달 없음 + 디스크에 메시지 0건
        await clientB.StopAsync();
        await Task.Delay(120);
        const string offlineSecretMessage = "오프라인_절대저장금지_문구_7744";
        var offlineOk = await clientA.SendBubbleMessageAsync(friendBOnA, offlineSecretMessage);
        Assert.False(offlineOk);

        var missedMsgReceived = false;
        clientB.BubbleMessageReceived += (_, e) =>
        {
            if (e.Text == offlineSecretMessage) missedMsgReceived = true;
        };
        await clientB.StartAsync(profileB, [friendAOnB]);
        await Task.Delay(250);
        Assert.False(missedMsgReceived);

        var registryJson = File.ReadAllText(Path.Combine(ServerDataDir, "registry.json"));
        Assert.DoesNotContain(secretMessage, registryJson, StringComparison.Ordinal);
        Assert.DoesNotContain(offlineSecretMessage, registryJson, StringComparison.Ordinal);
        Assert.DoesNotContain("text", registryJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HybridRouter_PrefersLanFallsBackToGcpDeduplicatesAndHandlesServerRestart()
    {
        var serverOpts = new ServerConnectionOptions { Enabled = true, ServerUrl = ServerUrl };
        var idStoreA = new DeviceIdentityStore(Path.Combine(_tempRoot, "hA"));
        var idStoreB = new DeviceIdentityStore(Path.Combine(_tempRoot, "hB"));

        await using var lanA = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
        await using var lanB = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
        await using var gcpA = new GcpSignalRTransport(serverOpts, idStoreA);
        await using var gcpB = new GcpSignalRTransport(serverOpts, idStoreB);
        await using var routerA = new HybridTransportRouter(lanA, gcpA);
        await using var routerB = new HybridTransportRouter(lanB, gcpB);

        var secret = PacketSigner.GeneratePairSecretBase64();
        var profileA = new PetProfile { OwnerName = "호스트A", PetName = "모찌", FriendCode = "LP-AAA1-1111" };
        var profileB = new PetProfile { OwnerName = "게스트B", PetName = "초코", FriendCode = "LP-BBB2-2222" };

        var friendB = new FriendEntry
        {
            PetId = profileB.PetId,
            OwnerName = profileB.OwnerName,
            PetName = profileB.PetName,
            PairSecretBase64 = secret
        };
        var friendA = new FriendEntry
        {
            PetId = profileA.PetId,
            OwnerName = profileA.OwnerName,
            PetName = profileA.PetName,
            PairSecretBase64 = secret
        };

        await routerA.StartAsync(profileA, [friendB]);
        await routerB.StartAsync(profileB, [friendA]);

        var gcpMessagesOnB = new List<string>();
        var lanMessagesOnB = new List<string>();
        var routerMessagesOnB = new List<string>();
        var routerGamesOnB = new List<string>();

        lanB.BubbleMessageReceived += (_, e) => lanMessagesOnB.Add(e.Text);
        gcpB.BubbleMessageReceived += (_, e) => gcpMessagesOnB.Add(e.Text);
        routerB.BubbleMessageReceived += (_, e) => routerMessagesOnB.Add(e.Text);
        routerB.GameEventReceived += (_, e) => routerGamesOnB.Add(e.Payload.Action);

        // Wait briefly for mutual friend sync to reflect online presence on GCP
        await WaitUntilAsync(() => routerA.GetDiscoveredPeers().Any(p => p.PetId == profileB.PetId && p.IsFriend), TimeSpan.FromSeconds(5));

        // 1. LAN에 없는 외부 친구 -> GCP로 라우팅
        Assert.True(await routerA.SendBubbleMessageAsync(friendB, "외부GCP메시지"));
        Assert.Single(gcpMessagesOnB);
        Assert.Empty(lanMessagesOnB);
        Assert.Single(routerMessagesOnB);

        // 2. LAN에서도 동일 친구 탐색됨 -> HybridRouter는 LAN P2P를 우선 사용 (GCP 미사용)
        lanA.HandleDiscoveryHello(new DiscoveryHelloPacket
        {
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            AppVersion = "0.3.15",
            PetId = profileB.PetId,
            OwnerName = profileB.OwnerName,
            PetName = profileB.PetName,
            TcpPort = lanB.BoundTcpPort
        }, IPAddress.Loopback.ToString(), CancellationToken.None);

        await WaitUntilAsync(() => lanA.GetDiscoveredPeers().Any(p => p.PetId == profileB.PetId && p.IsFriend), TimeSpan.FromSeconds(5));

        // LAN 대표 1건만 병합 노출됨
        var mergedPeer = Assert.Single(routerA.GetDiscoveredPeers());
        Assert.Equal(PeerTransportKind.Lan, mergedPeer.TransportKind);

        Assert.True(await routerA.SendBubbleMessageAsync(friendB, "같은LAN우선메시지"));
        Assert.Single(lanMessagesOnB);
        Assert.Single(gcpMessagesOnB); // GCP 추가 호출 없음
        Assert.Equal(2, routerMessagesOnB.Count);

        // 3. 이중 경로 동일 이벤트 중복 수신 방지 검증 (LAN + GCP 동시 전달)
        var dupPayload = new GameEventPayload { GameType = "rps", SessionId = "dup-s1", Action = "move", WagerCoins = 10, Data = "rock" };
        Assert.True(await lanA.SendGameEventAsync(friendB, dupPayload));
        Assert.True(await gcpA.SendGameEventAsync(friendB, dupPayload));
        Assert.Single(routerGamesOnB);

        // 4. B의 LAN 소켓이 닫혀 LAN 전송 실패 -> routerA가 자동으로 GCP 폴백 전환
        await lanB.StopAsync();
        Assert.True(await routerA.SendBubbleMessageAsync(friendB, "LAN실패후자동GCP폴백"));
        Assert.Equal(2, gcpMessagesOnB.Count);
        Assert.Contains("LAN실패후자동GCP폴백", routerMessagesOnB);

        await WpfTestHost.RunAsync(async () =>
        {
            using var sendSnowball = new SnowballInteractionService(routerA);
            var relayed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnRelayed(object? sender, SnowballReceivedEventArgs e) => relayed.TrySetResult(e.EventId);
            gcpB.SnowballReceived += OnRelayed;
            try
            {
                Assert.True(await sendSnowball.SendAsync(friendB));
                Assert.True(sendSnowball.Outgoing.TryTake(out var request));
                Assert.Equal(await relayed.Task.WaitAsync(TimeSpan.FromSeconds(4)), request.EventId);
                Assert.False(sendSnowball.Outgoing.TryTake(out _));
            }
            finally
            {
                gcpB.SnowballReceived -= OnRelayed;
            }
        });

        // 5. 서버 중단 중에는 LAN 유지, 서버 재시작 후 자동 재연결 및 송수신 복구 검증
        await lanB.StartAsync(profileB, [friendA]);
        lanA.HandleDiscoveryHello(new DiscoveryHelloPacket
        {
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            AppVersion = "0.2.2",
            PetId = profileB.PetId,
            OwnerName = profileB.OwnerName,
            PetName = profileB.PetName,
            TcpPort = lanB.BoundTcpPort
        }, IPAddress.Loopback.ToString(), CancellationToken.None);
        await WaitUntilAsync(() =>
            lanA.GetDiscoveredPeers().Any(p => p.PetId == profileB.PetId && p.TcpPort == lanB.BoundTcpPort),
            TimeSpan.FromSeconds(5));

        await StopServerAsync();
        await WaitUntilAsync(() => !routerA.IsRemoteConnected, TimeSpan.FromSeconds(5));

        // 서버 중단 중에도 기존 LAN은 계속 정상 동작
        Assert.True(await routerA.SendBubbleMessageAsync(friendB, "서버중단중LAN생존"));
        Assert.Equal(2, lanMessagesOnB.Count);

        await StartServerAsync();
        await WaitUntilAsync(() => routerA.IsRemoteConnected && routerB.IsRemoteConnected, TimeSpan.FromSeconds(25));

        Assert.True(await gcpA.SendBubbleMessageAsync(friendB, "재연결성공메시지"));
        Assert.Contains("재연결성공메시지", gcpMessagesOnB);
    }

    [Fact]
    public async Task GcpServer_ConcurrentConnectionsBurstAndCleanDisconnect_ReleasesResources()
    {
        const int clientCount = 12;
        var serverOpts = new ServerConnectionOptions { Enabled = true, ServerUrl = ServerUrl };
        var clients = new List<(GcpSignalRTransport Transport, PetProfile Profile)>(clientCount);

        try
        {
            for (var i = 0; i < clientCount; i++)
            {
                var store = new DeviceIdentityStore(Path.Combine(_tempRoot, $"load_{i}"));
                var transport = new GcpSignalRTransport(serverOpts, store);
                var profile = new PetProfile
                {
                    OwnerName = $"부하_{i}",
                    PetName = $"펫_{i}",
                    FriendCode = $"LP-LOAD-{i:0000}"
                };
                clients.Add((transport, profile));
            }

            await Task.WhenAll(clients.Select(c => c.Transport.StartAsync(c.Profile, [])));
            Assert.All(clients, c => Assert.True(c.Transport.IsRemoteConnected));

            // 짝끼리 친구 등록 후 동시 다발 E2EE 메시지 중계 (6쌍 x 5건 = 30건)
            var receivedTotal = 0;
            var relayTasks = new List<Task<bool>>();
            var friendPairs = new List<(string SenderPetId, GcpSignalRTransport Sender, FriendEntry TargetFriend)>();
            for (var i = 0; i < clientCount; i += 2)
            {
                var secret = PacketSigner.GeneratePairSecretBase64();
                var a = clients[i];
                var b = clients[i + 1];
                var fb = new FriendEntry { PetId = b.Profile.PetId, PetName = b.Profile.PetName, PairSecretBase64 = secret };
                var fa = new FriendEntry { PetId = a.Profile.PetId, PetName = a.Profile.PetName, PairSecretBase64 = secret };

                b.Transport.BubbleMessageReceived += (_, _) => Interlocked.Increment(ref receivedTotal);
                a.Transport.UpdateKnownFriends([fb]);
                b.Transport.UpdateKnownFriends([fa]);
                friendPairs.Add((a.Profile.PetId, a.Transport, fb));
            }

            await WaitUntilAsync(
                () => clients.Where((_, idx) => idx % 2 == 0).All(c => c.Transport.GetDiscoveredPeers().Count(p => p.IsFriend) == 1),
                TimeSpan.FromSeconds(5));

            // 로컬 친구 목록이 갱신된 뒤에도 서버의 양방향 친구 동기화는 진행 중일 수 있다.
            var registry = _server!.Services.GetRequiredService<IdentityRegistryStore>();
            await WaitUntilAsync(
                () => friendPairs.All(pair => registry.AreMutualFriends(pair.SenderPetId, pair.TargetFriend.PetId)),
                TimeSpan.FromSeconds(5));

            foreach (var (_, sender, targetFriend) in friendPairs)
            {
                for (var m = 0; m < 5; m++)
                {
                    relayTasks.Add(sender.SendBubbleMessageAsync(targetFriend, $"부하테스트_{m}"));
                }
            }

            var results = await Task.WhenAll(relayTasks);
            Assert.All(results, Assert.True);
            Assert.Equal(friendPairs.Count * 5, Volatile.Read(ref receivedTotal));
        }
        finally
        {
            foreach (var (transport, _) in clients)
            {
                await transport.DisposeAsync();
            }
        }

        // 모든 연결 종료 후 활성 연결과 온라인 펫 수가 0으로 깔끔히 회수되는지 확인 및 공개 /health 최소 응답 검증
        var presence = _server!.Services.GetRequiredService<LabPet.Server.Services.ConnectionPresenceManager>();
        await WaitUntilAsync(() =>
            presence.OnlinePetCount == 0 && presence.ActiveConnectionCount == 0,
            TimeSpan.FromSeconds(5));

        using var http = new HttpClient { BaseAddress = new Uri(ServerUrl) };
        var healthJson = await http.GetStringAsync(SocialHubConstants.HealthPath);
        using var doc = JsonDocument.Parse(healthJson);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.False(doc.RootElement.TryGetProperty("onlinePets", out _));
        Assert.False(doc.RootElement.TryGetProperty("activeConnections", out _));
        Assert.False(doc.RootElement.TryGetProperty("overloadLevel", out _));
    }

    [Fact]
    public void ServerConnectionOptions_Load_RespectsPriorityAndExplicitDisable()
    {
        var optsDir = Path.Combine(_tempRoot, "conn_opts");
        Directory.CreateDirectory(optsDir);
        var configPath = Path.Combine(optsDir, ServerConnectionOptions.ConfigFileName);

        // 1. 모두 미설정 -> 원격 비활성
        var none = ServerConnectionOptions.Load(optsDir, envServerUrl: null, bundledDefaultServerUrl: null);
        Assert.False(none.IsConfigured);

        // 2. 릴리스 번들 기본 URL만 존재 -> 설정 파일 없이도 자동 활성화
        var bundledOnly = ServerConnectionOptions.Load(optsDir, envServerUrl: null, bundledDefaultServerUrl: "https://bundled.example.com");
        Assert.True(bundledOnly.IsConfigured);
        Assert.Equal("https://bundled.example.com", bundledOnly.ServerUrl);

        // 3. 손상된 server.json + 번들 기본값 -> 번들 기본값으로 폴백
        File.WriteAllText(configPath, "{ invalid json");
        var corruptedFallback = ServerConnectionOptions.Load(optsDir, envServerUrl: null, bundledDefaultServerUrl: "https://bundled.example.com");
        Assert.True(corruptedFallback.IsConfigured);
        Assert.Equal("https://bundled.example.com", corruptedFallback.ServerUrl);

        // 4. 사용자가 server.json에 enabled=false 명시 -> 번들 기본값이 있어도 비활성 유지
        File.WriteAllText(configPath, """{"enabled":false,"serverUrl":"https://custom.example.com"}""");
        var explicitDisabled = ServerConnectionOptions.Load(optsDir, envServerUrl: null, bundledDefaultServerUrl: "https://bundled.example.com");
        Assert.False(explicitDisabled.IsConfigured);

        // 5. server.json 커스텀 URL -> 번들 기본값보다 우선
        File.WriteAllText(configPath, """{"enabled":true,"serverUrl":"https://custom.example.com"}""");
        var fileOverridesBundled = ServerConnectionOptions.Load(optsDir, envServerUrl: null, bundledDefaultServerUrl: "https://bundled.example.com");
        Assert.True(fileOverridesBundled.IsConfigured);
        Assert.Equal("https://custom.example.com", fileOverridesBundled.ServerUrl);

        // 6. 환경변수 -> server.json 및 번들 기본값 모두보다 최우선
        var envOverridesAll = ServerConnectionOptions.Load(optsDir, envServerUrl: "https://env.example.com", bundledDefaultServerUrl: "https://bundled.example.com");
        Assert.True(envOverridesAll.IsConfigured);
        Assert.Equal("https://env.example.com", envOverridesAll.ServerUrl);
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

            await Task.Delay(30);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("지정 시간 내에 기대 상태에 도달하지 못했습니다.");
            }

            await Task.Delay(30);
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
