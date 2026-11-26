using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using LabPet.Client.Network;
using LabPet.Client.Storage;
using LabPet.Server;
using LabPet.Server.Hubs;
using LabPet.Server.Options;
using LabPet.Server.Services;
using LabPet.Server.Storage;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LabPet.Tests;

public sealed class GcpDosProtectionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "LabPet_GcpDos_" + Guid.NewGuid().ToString("N"));
    private const string TestSecret = "unit-test-dos-protection-fixed-secret-2026!!";

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task HttpAuthRateLimiting_LimitsExcessiveBurst_WhileAllowingSharedLabIpClients()
    {
        var port = GetFreeTcpPort();
        var dataDir = Path.Combine(_tempRoot, "http_rl");
        var serverUrl = $"http://127.0.0.1:{port}";

        var opts = new ServerOptions
        {
            ListenUrl = serverUrl,
            ServerSecret = TestSecret,
            RequireConfiguredSecret = true,
            DataDirectory = dataDir,
            AuthChallengePerIpPerMinute = 5,
            AuthChallengeGlobalPerMinute = 30,
            AuthChallengeMaxConcurrency = 8
        };

        await using var app = LabPetServerHost.BuildApplication(opts);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(serverUrl) };

            // 연구실 공유 IP(127.0.0.1)에서 여러 정상 클라이언트가 5회 내 요청 시 모두 통과
            for (var i = 0; i < 5; i++)
            {
                using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var pub = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
                var res = await http.PostAsJsonAsync(SocialHubConstants.AuthChallengePath, new AuthChallengeRequest
                {
                    PetId = $"lab-user-{i}",
                    DevicePublicKeyBase64 = pub
                });
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            }

            // 상한 초과 시 429 TooManyRequests + RATE_LIMITED 반환
            using var extraKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var extraPub = Convert.ToBase64String(extraKey.ExportSubjectPublicKeyInfo());
            var blocked = await http.PostAsJsonAsync(SocialHubConstants.AuthChallengePath, new AuthChallengeRequest
            {
                PetId = "lab-user-extra",
                DevicePublicKeyBase64 = extraPub
            });

            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            var body = await blocked.Content.ReadAsStringAsync();
            Assert.Contains("RATE_LIMITED", body, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task ProxyAndHttpsPolicy_IgnoresUntrustedForwardedHeaders_EnforcesHttps_AndBlocksInsecureClientRemoteUrl()
    {
        // 1. 클라이언트 ServerConnectionOptions: 비루프백 평문 http 거부, 루프백·https 허용
        var insecureRemote = new ServerConnectionOptions { Enabled = true, ServerUrl = "http://remote.example.com" };
        Assert.False(insecureRemote.TryGetBaseUri(out _));

        var explicitInsecure = new ServerConnectionOptions { Enabled = true, ServerUrl = "http://remote.example.com", AllowInsecureHttp = true };
        Assert.True(explicitInsecure.TryGetBaseUri(out var allowedInsecureUri));
        Assert.Equal("http", allowedInsecureUri!.Scheme);

        var loopbackHttp = new ServerConnectionOptions { Enabled = true, ServerUrl = "http://127.0.0.1:5000" };
        Assert.True(loopbackHttp.TryGetBaseUri(out _));

        var remoteHttps = new ServerConnectionOptions { Enabled = true, ServerUrl = "https://remote.example.com" };
        Assert.True(remoteHttps.TryGetBaseUri(out _));

        // 2. 비신뢰 프록시 목록(127.0.0.1 미포함) -> 위조 X-Forwarded-Proto: https 무시, 426 응답 (루프백 /health만 200 허용)
        var untrustedPort = GetFreeTcpPort();
        var untrustedUrl = $"http://127.0.0.1:{untrustedPort}";
        await using (var untrustedApp = LabPetServerHost.BuildApplication(new ServerOptions
        {
            ListenUrl = untrustedUrl,
            ServerSecret = TestSecret,
            RequireConfiguredSecret = true,
            DataDirectory = Path.Combine(_tempRoot, "untrusted_proxy"),
            EnforceHttps = true,
            TrustedProxies = ["198.51.100.10"]
        }))
        {
            await untrustedApp.StartAsync();
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(untrustedUrl) };
                using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var pub = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

                using var spoofedReq = new HttpRequestMessage(HttpMethod.Post, SocialHubConstants.AuthChallengePath)
                {
                    Content = JsonContent.Create(new AuthChallengeRequest { PetId = "spoof-1", DevicePublicKeyBase64 = pub })
                };
                spoofedReq.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
                spoofedReq.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.99");

                var res = await http.SendAsync(spoofedReq);
                Assert.Equal(HttpStatusCode.UpgradeRequired, res.StatusCode);

                var healthRes = await http.GetAsync(SocialHubConstants.HealthPath);
                Assert.Equal(HttpStatusCode.OK, healthRes.StatusCode);
            }
            finally
            {
                await untrustedApp.StopAsync();
            }
        }

        // 3. 신뢰 프록시(127.0.0.1, 로컬 Caddy) -> 헤더 없으면 426, X-Forwarded-Proto: https 있으면 200 OK
        var trustedPort = GetFreeTcpPort();
        var trustedUrl = $"http://127.0.0.1:{trustedPort}";
        await using (var trustedApp = LabPetServerHost.BuildApplication(new ServerOptions
        {
            ListenUrl = trustedUrl,
            ServerSecret = TestSecret,
            RequireConfiguredSecret = true,
            DataDirectory = Path.Combine(_tempRoot, "trusted_proxy"),
            EnforceHttps = true,
            TrustedProxies = ["127.0.0.1"]
        }))
        {
            await trustedApp.StartAsync();
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(trustedUrl) };
                using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var pub = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

                var plainRes = await http.PostAsJsonAsync(SocialHubConstants.AuthChallengePath,
                    new AuthChallengeRequest { PetId = "proxy-nohdr", DevicePublicKeyBase64 = pub });
                Assert.Equal(HttpStatusCode.UpgradeRequired, plainRes.StatusCode);

                using var proxyReq = new HttpRequestMessage(HttpMethod.Post, SocialHubConstants.AuthChallengePath)
                {
                    Content = JsonContent.Create(new AuthChallengeRequest { PetId = "proxy-ok", DevicePublicKeyBase64 = pub })
                };
                proxyReq.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
                var okRes = await http.SendAsync(proxyReq);
                Assert.Equal(HttpStatusCode.OK, okRes.StatusCode);
            }
            finally
            {
                await trustedApp.StopAsync();
            }
        }
    }

    [Fact]
    public async Task HubFilterFeatureLimitsStorageCapsAndBucketCleanup_EnforceResourceBounds()
    {
        // 1. RateLimiterAndDedupService TTL 만료 키 회수 검증
        var start = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(start);
        var limiter = new RateLimiterAndDedupService(new ServerOptions
        {
            MaxRateLimitTrackedKeys = 64,
            MessageDedupWindowSeconds = 30
        }, clock);

        for (var i = 0; i < 10; i++)
        {
            Assert.True(limiter.AllowControlPacket($"pet-{i}"));
            Assert.True(limiter.AllowControlFeature($"pet-{i}", PacketType.BubbleMessage));
            Assert.True(limiter.TryMarkMessageSeen($"pet-{i}", $"msg-{i}"));
        }
        Assert.True(limiter.TrackedBucketCount >= 20);

        clock.Advance(TimeSpan.FromMinutes(3));
        limiter.PruneExpired(clock.GetUtcNow());
        Assert.Equal(0, limiter.TrackedBucketCount);

        // 2. IdentityRegistryStore 등록 ID 및 승인 친구 수 상한 검증 (최소 Clamp = 10)
        var storeOpts = new ServerOptions
        {
            DataDirectory = Path.Combine(_tempRoot, "reg_cap"),
            MaxRegisteredIdentities = 10,
            MaxApprovedFriendships = 10
        };
        var store = new IdentityRegistryStore(storeOpts);
        var registeredIds = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            var ok = store.TryAuthenticateOrRegister($"id-{i}", pub, $"LP-CAP-{i:0000}", "주인", $"펫{i}", out _, out _);
            Assert.True(ok);
            registeredIds.Add($"id-{i}");
        }

        using (var extraKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var pub = Convert.ToBase64String(extraKey.ExportSubjectPublicKeyInfo());
            var extraOk = store.TryAuthenticateOrRegister("id-overflow", pub, "LP-CAP-9999", "초과", "오버", out _, out var err);
            Assert.False(extraOk);
            Assert.Equal("REGISTRY_CAPACITY_REACHED", err);
        }

        var friendshipsCreated = 0;
        for (var i = 0; i < registeredIds.Count; i++)
        {
            for (var j = i + 1; j < registeredIds.Count; j++)
            {
                var approved = store.ApproveFriendship(registeredIds[i], registeredIds[j]);
                if (friendshipsCreated < 10)
                {
                    Assert.True(approved);
                    friendshipsCreated++;
                }
                else
                {
                    Assert.False(approved);
                }
            }
        }
        Assert.Equal(10, store.ApprovedFriendshipCount);

        // 3. SignalR Hub Filter (SyncKnownFriendsPerMinute=2) 및 기능별 메시지 제한(MessagePer10Seconds=2) E2E 검증
        var port = GetFreeTcpPort();
        var serverUrl = $"http://127.0.0.1:{port}";
        await using var app = LabPetServerHost.BuildApplication(new ServerOptions
        {
            ListenUrl = serverUrl,
            ServerSecret = TestSecret,
            RequireConfiguredSecret = true,
            DataDirectory = Path.Combine(_tempRoot, "hub_limits"),
            SyncKnownFriendsPerMinute = 2,
            MessagePer10Seconds = 2,
            ControlRateLimitPerSecond = 20
        });
        await app.StartAsync();
        try
        {
            var connOpts = new ServerConnectionOptions { Enabled = true, ServerUrl = serverUrl };
            var profileA = new PetProfile { OwnerName = "송신A", PetName = "모찌A" };
            var profileB = new PetProfile { OwnerName = "수신B", PetName = "초코B" };
            var secret = PacketSigner.GeneratePairSecretBase64();
            var friendB = new FriendEntry { PetId = profileB.PetId, OwnerName = "수신B", PetName = "초코B", PairSecretBase64 = secret };
            var friendA = new FriendEntry { PetId = profileA.PetId, OwnerName = "송신A", PetName = "모찌A", PairSecretBase64 = secret };

            await using var clientA = new GcpSignalRTransport(connOpts, new DeviceIdentityStore(Path.Combine(_tempRoot, "hl_a")));
            await using var clientB = new GcpSignalRTransport(connOpts, new DeviceIdentityStore(Path.Combine(_tempRoot, "hl_b")));
            await clientA.StartAsync(profileA, [friendB]);
            await clientB.StartAsync(profileB, [friendA]);

            // 기능별 제한: MessagePer10Seconds=2 -> 처음 2건 성공, 3번째 RateLimited 실패
            Assert.True((await clientA.SendBubbleMessageDetailedAsync(friendB, "정상1")).Succeeded);
            Assert.True((await clientA.SendBubbleMessageDetailedAsync(friendB, "정상2")).Succeeded);
            var blockedMsg = await clientA.SendBubbleMessageDetailedAsync(friendB, "초과3");
            Assert.False(blockedMsg.Succeeded);
            Assert.Equal(FriendOperationErrorCodes.RateLimited, blockedMsg.ErrorCode);

            // Hub Filter: SyncKnownFriendsPerMinute=2 -> 2회 허용, 3번째 호출 시 RATE_LIMITED HubException
            var (rawHub, _) = await ConnectRawHubAsync(serverUrl, profileA, new DeviceIdentityStore(Path.Combine(_tempRoot, "hl_a")));
            try
            {
                await rawHub.InvokeAsync(SocialHubConstants.SyncKnownFriendsMethod, new[] { profileB.PetId });
                await rawHub.InvokeAsync(SocialHubConstants.SyncKnownFriendsMethod, new[] { profileB.PetId });
                var hubEx = await Assert.ThrowsAsync<HubException>(() =>
                    rawHub.InvokeAsync(SocialHubConstants.SyncKnownFriendsMethod, new[] { profileB.PetId }));
                Assert.Contains(FriendOperationErrorCodes.RateLimited, hubEx.Message, StringComparison.Ordinal);
            }
            finally
            {
                await rawHub.DisposeAsync();
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task GcpMusicRelay_RoutesToSingleTargetOnly_DropsStale_AndCoalescesSlowQueue()
    {
        // 1. RemoteMusicRelayDispatcher: 오래된 시퀀스 폐기 및 1-슬롯 병합 검증
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentSequences = new List<uint>();

        var dispatcher = new RemoteMusicRelayDispatcher(
            new ServerOptions { MaxRateLimitTrackedKeys = 64, MessageDedupWindowSeconds = 60 },
            async (_, packet, _) =>
            {
                lock (sentSequences)
                {
                    sentSequences.Add(packet.Sequence);
                }

                if (packet.Sequence == 1)
                {
                    firstEntered.TrySetResult();
                    await gate.Task.ConfigureAwait(false);
                }
            },
            clock);

        Assert.True(dispatcher.TryAcceptSequence("host", "subB", "sub-1", 10));
        Assert.False(dispatcher.TryAcceptSequence("host", "subB", "sub-1", 10));
        Assert.False(dispatcher.TryAcceptSequence("host", "subB", "sub-1", 9));
        Assert.Equal(2, dispatcher.DroppedStaleFrames);

        Assert.True(dispatcher.TryEnqueue("conn-slow", new MusicFramePacket { SenderPetId = "host", TargetPetId = "subB", Sequence = 1 }));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // 1번 송신 중 2~5번 연속 적재 -> 1개 대기 슬롯만 유지하므로 2, 3, 4번은 최신 5번으로 덮어쓰기됨 (3건 병합 폐기)
        for (uint seq = 2; seq <= 5; seq++)
        {
            Assert.True(dispatcher.TryEnqueue("conn-slow", new MusicFramePacket { SenderPetId = "host", TargetPetId = "subB", Sequence = seq }));
        }

        Assert.Equal(3, dispatcher.DroppedQueuedFrames);
        gate.TrySetResult();
        await WaitUntilAsync(() => { lock (sentSequences) return sentSequences.Count >= 2; }, TimeSpan.FromSeconds(3));
        await Task.Delay(50);

        lock (sentSequences)
        {
            Assert.Equal([1u, 5u], sentSequences);
        }

        // 2. E2E Hub: 2명의 구독자(B, C) 중 TargetPetId=B 패킷은 B에게만 전달 (중복 브로드캐스트 제거)
        var port = GetFreeTcpPort();
        var serverUrl = $"http://127.0.0.1:{port}";
        await using var app = LabPetServerHost.BuildApplication(new ServerOptions
        {
            ListenUrl = serverUrl,
            ServerSecret = TestSecret,
            RequireConfiguredSecret = true,
            DataDirectory = Path.Combine(_tempRoot, "music_single_target"),
            MusicFramePerSubscriberPerSecond = 20,
            MusicFramePerSenderPerSecond = 50,
            MusicFrameGlobalPerSecond = 200
        });
        await app.StartAsync();
        try
        {
            var connOpts = new ServerConnectionOptions { Enabled = true, ServerUrl = serverUrl };
            var profileA = new PetProfile { OwnerName = "호스트A", PetName = "디제이" };
            var profileB = new PetProfile { OwnerName = "구독자B", PetName = "냥이B" };
            var profileC = new PetProfile { OwnerName = "구독자C", PetName = "멍이C" };
            var secretAB = PacketSigner.GeneratePairSecretBase64();
            var secretAC = PacketSigner.GeneratePairSecretBase64();

            var storeA = new DeviceIdentityStore(Path.Combine(_tempRoot, "ms_a"));
            var storeB = new DeviceIdentityStore(Path.Combine(_tempRoot, "ms_b"));
            var storeC = new DeviceIdentityStore(Path.Combine(_tempRoot, "ms_c"));

            var friendBOnA = new FriendEntry { PetId = profileB.PetId, PetName = profileB.PetName, PairSecretBase64 = secretAB };
            var friendCOnA = new FriendEntry { PetId = profileC.PetId, PetName = profileC.PetName, PairSecretBase64 = secretAC };
            var friendAOnB = new FriendEntry { PetId = profileA.PetId, PetName = profileA.PetName, PairSecretBase64 = secretAB };
            var friendAOnC = new FriendEntry { PetId = profileA.PetId, PetName = profileA.PetName, PairSecretBase64 = secretAC };

            await using var clientA = new GcpSignalRTransport(connOpts, storeA);
            await using var clientB = new GcpSignalRTransport(connOpts, storeB);
            await using var clientC = new GcpSignalRTransport(connOpts, storeC);

            await clientA.StartAsync(profileA, [friendBOnA, friendCOnA]);
            await clientB.StartAsync(profileB, [friendAOnB]);
            await clientC.StartAsync(profileC, [friendAOnC]);

            var bFrames = 0;
            var cFrames = 0;
            clientB.MusicFrameReceived += (_, _) => Interlocked.Increment(ref bFrames);
            clientC.MusicFrameReceived += (_, _) => Interlocked.Increment(ref cFrames);

            Assert.True(await clientB.SetMusicFollowAsync(friendAOnB, enable: true));
            Assert.True(await clientC.SetMusicFollowAsync(friendAOnC, enable: true));

            // raw HubConnection으로 C의 2번째 연결을 열어 타겟 B 전용 프레임 유출 여부 직접 감시
            var (rawHubA, _) = await ConnectRawHubAsync(serverUrl, profileA, storeA);
            var (rawHubC, _) = await ConnectRawHubAsync(serverUrl, profileC, storeC);
            var rawCReceivedTargetB = 0;
            rawHubC.On<MusicFramePacket>(SocialHubConstants.ReceiveMusicFrameClientMethod, p =>
            {
                if (string.Equals(p.TargetPetId, profileB.PetId, StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref rawCReceivedTargetB);
                }
            });
            try
            {
                // 정상 Publish -> B와 C 각각 자기 서명 패킷 수신
                for (uint seq = 1; seq <= 3; seq++)
                {
                    await clientA.PublishMusicFrameAsync(new MusicFrameData(0.7f, 0.4f, 0.3f, true, seq));
                    await Task.Delay(75);
                }

                await WaitUntilAsync(() => Volatile.Read(ref bFrames) >= 2 && Volatile.Read(ref cFrames) >= 2, TimeSpan.FromSeconds(5));

                // TargetPetId=B 전용 패킷을 직접 중계 요청 -> B에만 전달되고 C(구독 중)에는 0건
                var targetedForB = new MusicFramePacket
                {
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                    SenderPetId = profileA.PetId,
                    TargetPetId = profileB.PetId,
                    SubscriptionId = string.Empty,
                    Bass = 0.9f,
                    Mid = 0.5f,
                    Treble = 0.2f,
                    IsBeat = true,
                    Sequence = 999
                };
                targetedForB.SignatureBase64 = PacketSigner.ComputeSignature(PacketSigner.BuildMusicFrameCanonicalString(targetedForB), secretAB);
                await rawHubA.InvokeAsync(nameof(SocialHub.RelayMusicFrameAsync), targetedForB);

                await Task.Delay(150);
                Assert.Equal(0, Volatile.Read(ref rawCReceivedTargetB));
            }
            finally
            {
                await rawHubA.DisposeAsync();
                await rawHubC.DisposeAsync();
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task ServerOverloadProtection_ShedsStagesInOrderAndRecoversStepwiseWithHysteresis()
    {
        var opts = new ServerOptions
        {
            DataDirectory = Path.Combine(_tempRoot, "overload_unit"),
            OverloadShedMusicConnectionRatio = 0.75,
            OverloadShedDiscoveryConnectionRatio = 0.85,
            OverloadShedNewConnectionsRatio = 0.95,
            OverloadRecoveryHysteresis = 0.10,
            OverloadRecoveryHoldSeconds = 10
        };

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 26, 13, 0, 0, TimeSpan.Zero));
        var registry = new IdentityRegistryStore(opts);
        var presence = new ConnectionPresenceManager(opts);
        var throttledLog = new ThrottledLogService(opts, clock);
        var overload = new ServerOverloadProtectionService(opts, presence, throttledLog, NullLogger<ServerOverloadProtectionService>.Instance, clock);

        Assert.Equal(ServerOverloadLevel.Normal, overload.CurrentLevel);
        Assert.True(overload.AllowMusicRelay && overload.AllowPublicDiscovery && overload.AllowNewConnections);

        // 1단계: 음악 제한
        overload.SetTestMetricsOverride(connectionRatio: 0.78, memoryMb: 50);
        Assert.Equal(ServerOverloadLevel.ShedMusic, overload.EvaluateNow());
        Assert.False(overload.AllowMusicRelay);
        Assert.True(overload.AllowPublicDiscovery);
        Assert.True(overload.AllowNewConnections);

        // 2단계: 비친구 탐색 제한
        overload.SetTestMetricsOverride(connectionRatio: 0.88, memoryMb: 50);
        Assert.Equal(ServerOverloadLevel.ShedDiscovery, overload.EvaluateNow());
        Assert.False(overload.AllowPublicDiscovery);
        Assert.True(overload.AllowNewConnections);

        // 3단계: 신규 연결 제한
        overload.SetTestMetricsOverride(connectionRatio: 0.97, memoryMb: 50);
        Assert.Equal(ServerOverloadLevel.ShedNewConnections, overload.EvaluateNow());
        Assert.False(overload.AllowNewConnections);

        // 부하 해소 직후에는 즉시 복구되지 않고 홀드 타이머 경과마다 1단계씩 단계 복구
        overload.SetTestMetricsOverride(connectionRatio: 0.30, memoryMb: 50);
        Assert.Equal(ServerOverloadLevel.ShedNewConnections, overload.EvaluateNow());

        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(ServerOverloadLevel.ShedDiscovery, overload.EvaluateNow());

        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(ServerOverloadLevel.ShedMusic, overload.EvaluateNow());

        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(ServerOverloadLevel.Normal, overload.EvaluateNow());
        Assert.True(overload.AllowMusicRelay && overload.AllowPublicDiscovery && overload.AllowNewConnections);

        // Kestrel 통합 서버에서 과부하 3단계 진입 시 /health 표시 및 /api/auth/challenge 503 확인
        var port = GetFreeTcpPort();
        var serverUrl = $"http://127.0.0.1:{port}";
        await using var app = LabPetServerHost.BuildApplication(new ServerOptions
        {
            ListenUrl = serverUrl,
            ServerSecret = TestSecret,
            RequireConfiguredSecret = true,
            DataDirectory = Path.Combine(_tempRoot, "overload_http"),
            OverloadShedNewConnectionsRatio = 0.90
        });
        await app.StartAsync();
        try
        {
            var svc = app.Services.GetRequiredService<ServerOverloadProtectionService>();
            svc.SetTestMetricsOverride(connectionRatio: 0.95, memoryMb: 50);
            Assert.Equal(ServerOverloadLevel.ShedNewConnections, svc.EvaluateNow());

            using var http = new HttpClient { BaseAddress = new Uri(serverUrl) };
            var healthJson = await http.GetStringAsync(SocialHubConstants.HealthPath);
            using var doc = JsonDocument.Parse(healthJson);
            Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
            Assert.False(doc.RootElement.TryGetProperty("overloadLevel", out _));
            Assert.Equal(ServerOverloadLevel.ShedNewConnections, svc.CurrentLevel);

            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var challengeRes = await http.PostAsJsonAsync(SocialHubConstants.AuthChallengePath, new AuthChallengeRequest
            {
                PetId = "overloaded-user",
                DevicePublicKeyBase64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo())
            });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, challengeRes.StatusCode);
            Assert.Contains("SERVER_OVERLOADED", await challengeRes.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task HybridRouter_KeepsLanDiscoveryMessagingAndMusicWorking_WhenGcpIsUnavailable()
    {
        // 연결 불가능한 로컬 미사용 포트를 GCP 서버로 지정해도 LAN 기능은 100% 독립 동작해야 함
        var unreachableGcpUrl = $"http://127.0.0.1:{GetFreeTcpPort()}";
        var serverOpts = new ServerConnectionOptions { Enabled = true, ServerUrl = unreachableGcpUrl };

        var lanDiscA = GetFreeUdpPort();
        var lanMusicA = GetFreeUdpPort();
        var lanDiscB = GetFreeUdpPort();
        var lanMusicB = GetFreeUdpPort();

        await using var lanA = new LanP2pTransport(lanDiscA, lanMusicA, tcpControlPort: 0);
        await using var lanB = new LanP2pTransport(lanDiscB, lanMusicB, tcpControlPort: 0);
        await using var gcpA = new GcpSignalRTransport(serverOpts, new DeviceIdentityStore(Path.Combine(_tempRoot, "hyb_a")));
        await using var gcpB = new GcpSignalRTransport(serverOpts, new DeviceIdentityStore(Path.Combine(_tempRoot, "hyb_b")));
        await using var routerA = new HybridTransportRouter(lanA, gcpA);
        await using var routerB = new HybridTransportRouter(lanB, gcpB);

        var secret = PacketSigner.GeneratePairSecretBase64();
        var profileA = new PetProfile { OwnerName = "로컬A", PetName = "모찌" };
        var profileB = new PetProfile { OwnerName = "로컬B", PetName = "초코" };
        var friendBOnA = new FriendEntry { PetId = profileB.PetId, OwnerName = "로컬B", PetName = "초코", PairSecretBase64 = secret };
        var friendAOnB = new FriendEntry { PetId = profileA.PetId, OwnerName = "로컬A", PetName = "모찌", PairSecretBase64 = secret };

        await routerA.StartAsync(profileA, [friendBOnA]);
        await routerB.StartAsync(profileB, [friendAOnB]);

        Assert.False(routerA.IsRemoteConnected);
        Assert.False(routerB.IsRemoteConnected);

        lanA.HandleDiscoveryHello(new DiscoveryHelloPacket
        {
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            AppVersion = "0.3.17",
            PetId = profileB.PetId,
            OwnerName = profileB.OwnerName,
            PetName = profileB.PetName,
            TcpPort = lanB.BoundTcpPort
        }, IPAddress.Loopback.ToString(), CancellationToken.None);

        await WaitUntilAsync(
            () => routerA.GetDiscoveredPeers().Any(p => p.PetId == profileB.PetId && p.IsFriend && p.TransportKind == PeerTransportKind.Lan),
            TimeSpan.FromSeconds(5));

        var receivedMessages = new List<string>();
        var musicCount = 0;
        routerB.BubbleMessageReceived += (_, e) => receivedMessages.Add(e.Text);
        routerA.MusicFrameReceived += (_, _) => Interlocked.Increment(ref musicCount);

        Assert.True(await routerA.SendBubbleMessageAsync(friendBOnA, "GCP장애중LAN정상"));
        Assert.Contains("GCP장애중LAN정상", receivedMessages);

        // Reverse HELLO로 B에서도 A의 LAN TCP 포트 확정 후 음악 구독
        lanB.HandleDiscoveryHello(new DiscoveryHelloPacket
        {
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            AppVersion = "0.3.17",
            PetId = profileA.PetId,
            OwnerName = profileA.OwnerName,
            PetName = profileA.PetName,
            TcpPort = lanA.BoundTcpPort
        }, IPAddress.Loopback.ToString(), CancellationToken.None);

        await WaitUntilAsync(
            () => routerB.GetDiscoveredPeers().Any(p => p.PetId == profileA.PetId && p.IsFriend),
            TimeSpan.FromSeconds(5));

        Assert.True(await routerA.SetMusicFollowAsync(friendBOnA, enable: true));
        for (uint seq = 1; seq <= 8; seq++)
        {
            await routerB.PublishMusicFrameAsync(new MusicFrameData(0.6f, 0.5f, 0.4f, true, seq));
            await Task.Delay(33);
        }

        await WaitUntilAsync(() => Volatile.Read(ref musicCount) >= 5, TimeSpan.FromSeconds(5));
    }

    private static async Task<(HubConnection Hub, string Token)> ConnectRawHubAsync(
        string serverUrl,
        PetProfile profile,
        DeviceIdentityStore identityStore)
    {
        var (pub, key) = identityStore.GetOrCreateKeyPair(profile.PetId);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(serverUrl) };
            var chRes = await http.PostAsJsonAsync(SocialHubConstants.AuthChallengePath, new AuthChallengeRequest
            {
                PetId = profile.PetId,
                DevicePublicKeyBase64 = pub
            });
            chRes.EnsureSuccessStatusCode();
            var ch = (await chRes.Content.ReadFromJsonAsync<AuthChallengeResponse>())!;
            var canonical = RemoteAuthSigner.BuildChallengeCanonicalString(ch.Nonce, profile.PetId, pub, ch.IssuedAtUtc);

            var tokRes = await http.PostAsJsonAsync(SocialHubConstants.AuthTokenPath, new AuthTokenRequest
            {
                PetId = profile.PetId,
                DevicePublicKeyBase64 = pub,
                Nonce = ch.Nonce,
                IssuedAtUtc = ch.IssuedAtUtc,
                SignatureBase64 = RemoteAuthSigner.SignChallenge(key, canonical),
                FriendCode = profile.FriendCode,
                OwnerName = profile.OwnerName,
                PetName = profile.PetName
            });
            tokRes.EnsureSuccessStatusCode();
            var tok = (await tokRes.Content.ReadFromJsonAsync<AuthTokenResponse>())!;

            var hub = new HubConnectionBuilder()
                .WithUrl(new Uri(new Uri(serverUrl), SocialHubConstants.HubPath.TrimStart('/')), o =>
                    o.AccessTokenProvider = () => Task.FromResult<string?>(tok.AccessToken))
                .AddJsonProtocol(p =>
                {
                    p.PayloadSerializerOptions.PropertyNamingPolicy = ProtocolJson.PacketOptions.PropertyNamingPolicy;
                    p.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
                })
                .Build();
            await hub.StartAsync();
            return (hub, tok.AccessToken);
        }
        finally
        {
            key.Dispose();
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset startUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = startUtc;

        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _utcNow.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
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
