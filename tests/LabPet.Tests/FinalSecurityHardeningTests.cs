using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LabPet.Client.Games;
using LabPet.Client.Network;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Client.SystemIntegration;
using LabPet.Client.ViewModels;
using LabPet.Server;
using LabPet.Server.Options;
using LabPet.Server.Security;
using LabPet.Server.Services;
using LabPet.Server.Storage;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LabPet.Tests;

public sealed class FinalSecurityHardeningTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "LabPet_FinalSec_" + Guid.NewGuid().ToString("N"));
    private const string StrongTestSecret = "final-security-hardening-strong-test-secret-2026!";

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WindowsSecurity_ManifestAsInvoker_FirewallAbsolutePaths_AndChristmasTreeNativeOnly()
    {
        var regPsi = FirewallRegistrationService.BuildRegisterProcessStartInfo(@"C:\Apps\LabPet\LabPet.Client.exe");
        Assert.True(Path.IsPathFullyQualified(regPsi.FileName));
        Assert.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", regPsi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FirewallRegistrationService.ResolveSystemDirectory(), regPsi.WorkingDirectory);
        Assert.Contains("-NoProfile -NonInteractive -EncodedCommand ", regPsi.Arguments, StringComparison.Ordinal);

        var remPsi = FirewallRegistrationService.BuildRemoveProcessStartInfo();
        Assert.True(Path.IsPathFullyQualified(remPsi.FileName));
        Assert.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", remPsi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd.exe", remPsi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FirewallRegistrationService.ResolveSystemDirectory(), remPsi.WorkingDirectory);
        Assert.Contains("-NoProfile -NonInteractive -EncodedCommand ", remPsi.Arguments, StringComparison.Ordinal);

        var queryPsi = FirewallRegistrationService.BuildQueryProcessStartInfo(FirewallRegistrationService.UdpRuleName);
        Assert.True(Path.IsPathFullyQualified(queryPsi.FileName));
        Assert.EndsWith(@"\netsh.exe", queryPsi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FirewallRegistrationService.ResolveSystemDirectory(), queryPsi.WorkingDirectory);
        Assert.False(queryPsi.UseShellExecute);
        Assert.Empty(queryPsi.Arguments);
        Assert.Equal(
            ["advfirewall", "firewall", "show", "rule", $"name={FirewallRegistrationService.UdpRuleName}", "verbose"],
            queryPsi.ArgumentList.ToArray());

        // PowerShell 크리스마스트리 폴백 심볼 제거 및 네이티브 상대경로 차단 검증
        var xtreeType = typeof(TerminalChristmasTreeService);
        Assert.Null(xtreeType.GetMethod("BuildScriptContent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        Assert.Null(xtreeType.GetMethod("EnsureScriptFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.Null(xtreeType.GetMethod("BuildLaunchStartInfo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));

        Assert.Throws<ArgumentException>(() =>
            TerminalChristmasTreeService.BuildNativeLaunchStartInfo(@"relative\LabPet.Client.exe", "모찌", null));

        if (FindRepoRoot() is { } repoRoot)
        {
            var manifest = File.ReadAllText(Path.Combine(repoRoot, "src", "LabPet.Client", "app.manifest"));
            Assert.Contains("<requestedExecutionLevel level=\"asInvoker\" uiAccess=\"false\" />", manifest, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GcpAuthSecurity_EnforcesStrongSecret_RejectsQueryToken_BlocksConfigHttpDowngrade_AndMinimizesHealth()
    {
        // 1. 운영 비밀키 강도 검증
        foreach (var weakSecret in new[]
        {
            "",
            "short-secret",
            new string('a', 40),
            "changeme-0123456789-abcdefghijklmnop"
        })
        {
            Assert.Throws<InvalidOperationException>(() => new ServerTokenService(new ServerOptions
            {
                RequireConfiguredSecret = true,
                ServerSecret = weakSecret
            }));
        }

        _ = new ServerTokenService(new ServerOptions
        {
            RequireConfiguredSecret = true,
            ServerSecret = StrongTestSecret
        });

        // 2. server.json 역직렬화로 AllowInsecureHttp 활성화 불가 확인
        var cfgDir = Path.Combine(_tempRoot, "conn_cfg");
        Directory.CreateDirectory(cfgDir);
        await File.WriteAllTextAsync(
            Path.Combine(cfgDir, ServerConnectionOptions.ConfigFileName),
            """{"enabled":true,"serverUrl":"http://remote.example.com","allowInsecureHttp":true}""");
        var loadedOpts = ServerConnectionOptions.Load(cfgDir, envServerUrl: null, bundledDefaultServerUrl: null);
        Assert.False(loadedOpts.AllowInsecureHttp);
        Assert.False(loadedOpts.IsConfigured);

        // 3. 토큰 마스킹 검증
        var sanitized = GcpSignalRTransport.SanitizeSensitiveText(
            "Error Authorization: Bearer header.payload.sig and ?access_token=top_secret_token_123");
        Assert.DoesNotContain("header.payload.sig", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("top_secret_token_123", sanitized, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", sanitized, StringComparison.Ordinal);

        // 4. 쿼리스트링 access_token 차단 및 /health 최소 응답 검증
        var port = GetFreeTcpPort();
        var serverUrl = $"http://127.0.0.1:{port}";
        await using var app = LabPetServerHost.BuildApplication(new ServerOptions
        {
            ListenUrl = serverUrl,
            ServerSecret = StrongTestSecret,
            RequireConfiguredSecret = true,
            DataDirectory = Path.Combine(_tempRoot, "auth_srv")
        });
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(serverUrl) };
            var healthRes = await http.GetAsync(SocialHubConstants.HealthPath);
            Assert.Equal(HttpStatusCode.OK, healthRes.StatusCode);
            var healthJson = await healthRes.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(healthJson);
            Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("bootId").GetString()));
            Assert.False(doc.RootElement.TryGetProperty("onlinePets", out _));
            Assert.False(doc.RootElement.TryGetProperty("activeConnections", out _));
            Assert.False(doc.RootElement.TryGetProperty("overloadLevel", out _));
            Assert.False(doc.RootElement.TryGetProperty("service", out _));
            Assert.False(doc.RootElement.TryGetProperty("utcNow", out _));

            var token = await IssueTokenAsync(http, Guid.NewGuid().ToString("D"));

            // access_token 쿼리만 전달 -> 401
            var queryRes = await http.PostAsync($"{SocialHubConstants.HubPath}/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}", null);
            Assert.Equal(HttpStatusCode.Unauthorized, queryRes.StatusCode);

            // Authorization Bearer 헤더 -> 200
            using var bearerReq = new HttpRequestMessage(HttpMethod.Post, $"{SocialHubConstants.HubPath}/negotiate?negotiateVersion=1");
            bearerReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var headerRes = await http.SendAsync(bearerReq);
            Assert.Equal(HttpStatusCode.OK, headerRes.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task GcpDtoValidation_RejectsInvalidLengthsRangesCollectionsAndNonFiniteValues()
    {
        var port = GetFreeTcpPort();
        var serverUrl = $"http://127.0.0.1:{port}";
        await using var app = LabPetServerHost.BuildApplication(new ServerOptions
        {
            ListenUrl = serverUrl,
            ServerSecret = StrongTestSecret,
            RequireConfiguredSecret = true,
            DataDirectory = Path.Combine(_tempRoot, "dto_srv"),
            MaxDeclaredFriendsPerPet = 10
        });
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(serverUrl) };
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

            // 1. 공백 포함 PetId / 초과 길이 챌린지 거부
            var badCh = await http.PostAsJsonAsync(SocialHubConstants.AuthChallengePath, new AuthChallengeRequest
            {
                PetId = "bad pet id",
                DevicePublicKeyBase64 = pub
            });
            Assert.Equal(HttpStatusCode.BadRequest, badCh.StatusCode);

            // 2. 초대형 Nonce 토큰 요청 거부
            var badTok = await http.PostAsJsonAsync(SocialHubConstants.AuthTokenPath, new AuthTokenRequest
            {
                PetId = "valid-pet-1",
                DevicePublicKeyBase64 = pub,
                Nonce = new string('n', 200),
                SignatureBase64 = "AAAA"
            });
            Assert.Equal(HttpStatusCode.BadRequest, badTok.StatusCode);

            // 3. Hub 메서드 DTO 범위/컬렉션/제어패킷 검증
            var petId = Guid.NewGuid().ToString("D");
            var (hub, _) = await ConnectRawHubAsync(serverUrl, new PetProfile { PetId = petId }, new DeviceIdentityStore(Path.Combine(_tempRoot, "dto_id")));
            try
            {
                await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync(SocialHubConstants.RegisterPresenceMethod, new RemotePresenceUpdate
                {
                    PetName = "정상",
                    Level = 0,
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion
                }));

                await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync(SocialHubConstants.RegisterPresenceMethod, new RemotePresenceUpdate
                {
                    PetName = new string('긴', 40),
                    Level = 1,
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion
                }));

                var hugeFriends = Enumerable.Range(0, 40).Select(i => $"friend-{i}").ToList();
                await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync(SocialHubConstants.RegisterPresenceMethod, new RemotePresenceUpdate
                {
                    PetName = "정상",
                    Level = 1,
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                    KnownFriendPetIds = hugeFriends
                }));

                await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync(SocialHubConstants.SyncKnownFriendsMethod, new List<string> { "bad id" }));

                var badRelay = await hub.InvokeAsync<RelayDeliveryResult>(
                    SocialHubConstants.RelayControlPacketMethod,
                    new RelayControlEnvelopeRequest(Guid.NewGuid().ToString("D"), new ControlPacketEnvelope
                    {
                        Header = new PacketHeader
                        {
                            SenderPetId = petId,
                            AppVersion = new string('1', 30),
                            PacketType = PacketType.BubbleMessage
                        },
                        PayloadJson = "{}"
                    }));
                Assert.False(badRelay.Delivered);
                Assert.Equal(FriendOperationErrorCodes.InvalidRequest, badRelay.ErrorCode);

                // 범위 밖 음수/초과 유한 음악 프레임 -> 서버가 예외 없이 폐기
                await hub.SendAsync(SocialHubConstants.RelayMusicFrameMethod, new MusicFramePacket
                {
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                    SenderPetId = petId,
                    TargetPetId = Guid.NewGuid().ToString("D"),
                    SubscriptionId = Guid.NewGuid().ToString("N"),
                    Bass = -1.0f,
                    Mid = 15.0f,
                    Treble = 0.5f,
                    SignatureBase64 = "AAAA"
                });

                // 비유한값(NaN, +/-Infinity) 및 경계값 직접 DTO 검증 차단 확인
                Assert.False(RemoteDtoValidator.ValidateMusicFramePacket(new MusicFramePacket
                {
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                    SenderPetId = petId,
                    TargetPetId = Guid.NewGuid().ToString("D"),
                    SubscriptionId = Guid.NewGuid().ToString("N"),
                    Bass = float.NaN,
                    Mid = 0.5f,
                    Treble = 0.5f,
                    SignatureBase64 = "AAAA"
                }, petId));
                Assert.False(RemoteDtoValidator.ValidateMusicFramePacket(new MusicFramePacket
                {
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                    SenderPetId = petId,
                    TargetPetId = Guid.NewGuid().ToString("D"),
                    SubscriptionId = Guid.NewGuid().ToString("N"),
                    Bass = float.PositiveInfinity,
                    Mid = 0.5f,
                    Treble = 0.5f,
                    SignatureBase64 = "AAAA"
                }, petId));
                Assert.False(RemoteDtoValidator.ValidateMusicFramePacket(new MusicFramePacket
                {
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                    SenderPetId = petId,
                    TargetPetId = Guid.NewGuid().ToString("D"),
                    SubscriptionId = Guid.NewGuid().ToString("N"),
                    Bass = 0.5f,
                    Mid = float.NegativeInfinity,
                    Treble = -0.01f,
                    SignatureBase64 = "AAAA"
                }, petId));
                Assert.True(RemoteDtoValidator.ValidateMusicFramePacket(new MusicFramePacket
                {
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                    SenderPetId = petId,
                    TargetPetId = Guid.NewGuid().ToString("D"),
                    SubscriptionId = Guid.NewGuid().ToString("N"),
                    Bass = 0.5f,
                    Mid = 1.0f,
                    Treble = 10.0f,
                    SignatureBase64 = "AAAA"
                }, petId));
            }
            finally
            {
                await hub.DisposeAsync();
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task FriendRemoval_RevokesServerAndSubscriptions_SyncsOfflineDeletion_AndBlocksOldListRestore()
    {
        var serverDataDir = Path.Combine(_tempRoot, "fr_srv");
        var port = GetFreeTcpPort();
        var serverUrl = $"http://127.0.0.1:{port}";

        ServerOptions CreateServerOptions() => new()
        {
            ListenUrl = serverUrl,
            ServerSecret = StrongTestSecret,
            RequireConfiguredSecret = true,
            DataDirectory = serverDataDir
        };

        var app = LabPetServerHost.BuildApplication(CreateServerOptions());
        await app.StartAsync();
        try
        {
            var connOpts = new ServerConnectionOptions { Enabled = true, ServerUrl = serverUrl };
            var dirA = Path.Combine(_tempRoot, "social_a");
            var dirB = Path.Combine(_tempRoot, "social_b");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            var profileA = new PetProfile { OwnerName = "주인A", PetName = "모찌A" };
            var profileB = new PetProfile { OwnerName = "주인B", PetName = "초코B" };

            var storeA = new JsonAppDataStore(new StateMigrator(), dirA);
            var storeB = new JsonAppDataStore(new StateMigrator(), dirB);
            await storeA.SaveProfileAsync(profileA);
            await storeB.SaveProfileAsync(profileB);

            var idStoreA = new DeviceIdentityStore(dirA);
            var idStoreB = new DeviceIdentityStore(dirB);
            var gcpA = new GcpSignalRTransport(connOpts, idStoreA);
            var gcpB = new GcpSignalRTransport(connOpts, idStoreB);

            var socialA = new SocialCoordinatorService(gcpA, storeA, new SoloProgressionService());
            var socialB = new SocialCoordinatorService(gcpB, storeB, new SoloProgressionService());

            var reqTcs = new TaskCompletionSource<PendingFriendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            socialB.FriendRequestArrived += (_, req) => reqTcs.TrySetResult(req);

            await socialA.StartAsync(profileA);
            await socialB.StartAsync(profileB);

            try
            {
                await WaitUntilAsync(() =>
                    socialA.IsRemoteConnected && socialB.IsRemoteConnected
                    && socialA.DiscoveredPeers.Any(p => p.PetId == profileB.PetId),
                    TimeSpan.FromSeconds(10));

                var peerBOnA = socialA.DiscoveredPeers.First(p => p.PetId == profileB.PetId);
                Assert.True((await socialA.SendFriendRequestDetailedAsync(peerBOnA)).Succeeded);
                var incomingReq = await reqTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True((await socialB.RespondFriendRequestDetailedAsync(incomingReq, accept: true)).Succeeded);

                await WaitUntilAsync(() => socialA.Friends.Any(f => f.PetId == profileB.PetId), TimeSpan.FromSeconds(5));
                await socialA.UpdateLocalPresenceAsync(profileA, isMusicActive: true);

                // 선택적 확인 코드 일치 검증 (원클릭 수락 후 동일 코드)
                var codeA = socialA.TryGetFriendSafetyCode(profileB.PetId);
                var codeB = socialB.TryGetFriendSafetyCode(profileA.PetId);
                Assert.NotNull(codeA);
                Assert.Matches(@"^\d{3}-\d{3}$", codeA);
                Assert.Equal(codeA, codeB);

                // 음악 구독 활성화
                var friendAOnB = Assert.Single(socialB.Friends);
                Assert.True((await socialB.ToggleMusicFollowDetailedAsync(friendAOnB)).Succeeded);

                var registry = app.Services.GetRequiredService<IdentityRegistryStore>();
                var presence = app.Services.GetRequiredService<ConnectionPresenceManager>();
                Assert.True(registry.AreMutualFriends(profileA.PetId, profileB.PetId));
                Assert.True(presence.IsMusicFollower(profileA.PetId, profileB.PetId));

                // 1. 온라인 친구 삭제 -> 서버 승인/선언/음악구독 해제 & 과거 목록 재동기화 복원 차단
                Assert.True(await socialB.RemoveFriendAsync(profileA.PetId));
                Assert.False(registry.AreMutualFriends(profileA.PetId, profileB.PetId));
                Assert.False(presence.IsMusicFollower(profileA.PetId, profileB.PetId));

                // 과거 친구 목록을 다시 Sync 해도 삭제된 관계가 자동 복원되지 않음
                var (rawHubA, _) = await ConnectRawHubAsync(serverUrl, profileA, idStoreA);
                try
                {
                    await rawHubA.InvokeAsync(SocialHubConstants.SyncKnownFriendsMethod, new[] { profileB.PetId });
                    Assert.False(registry.AreMutualFriends(profileA.PetId, profileB.PetId));
                }
                finally
                {
                    await rawHubA.DisposeAsync();
                }

                // A 측에서 삭제 전 잔여 친구 엔트리로 메시지 전송 시도 -> 서버가 NOT_MUTUAL_FRIEND로 차단
                var sendBlocked = await socialA.SendBubbleMessageDetailedAsync(Assert.Single(socialA.Friends), "차단확인");
                Assert.False(sendBlocked.Succeeded);

                // A도 로컬에서 B를 정리한 뒤 신규 상호 수락 핸드셰이크를 수행하면 의도적 재등록 가능
                await socialA.RemoveFriendAsync(profileB.PetId);
                await WaitUntilAsync(() => socialA.DiscoveredPeers.Any(p => p.PetId == profileB.PetId && !p.IsFriend), TimeSpan.FromSeconds(5));

                var reqTcs2 = new TaskCompletionSource<PendingFriendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
                socialB.FriendRequestArrived += (_, req) => reqTcs2.TrySetResult(req);
                var peerBAgain = socialA.DiscoveredPeers.First(p => p.PetId == profileB.PetId);
                Assert.True((await socialA.SendFriendRequestDetailedAsync(peerBAgain)).Succeeded);
                var incomingReq2 = await reqTcs2.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True((await socialB.RespondFriendRequestDetailedAsync(incomingReq2, accept: true)).Succeeded);
                await WaitUntilAsync(() => socialA.Friends.Any(f => f.PetId == profileB.PetId), TimeSpan.FromSeconds(5));
                Assert.True(registry.AreMutualFriends(profileA.PetId, profileB.PetId));
            }
            finally
            {
                await socialA.DisposeAsync();
                await socialB.DisposeAsync();
            }

            // 2. 오프라인 친구 삭제 후 재시작·재접속 시 서버에 삭제 자동 반영
            await app.StopAsync();
            await app.DisposeAsync();
            app = null;

            var unreachableUrl = $"http://127.0.0.1:{GetFreeTcpPort()}";
            await using (var offlineGcpA = new GcpSignalRTransport(new ServerConnectionOptions { Enabled = true, ServerUrl = unreachableUrl }, idStoreA))
            await using (var offlineSocialA = new SocialCoordinatorService(offlineGcpA, storeA, new SoloProgressionService()))
            {
                await offlineSocialA.StartAsync(profileA);
                Assert.True(await offlineSocialA.RemoveFriendAsync(profileB.PetId));
                var savedOffline = await storeA.LoadFriendsAsync();
                Assert.Empty(savedOffline.Friends);
                Assert.Contains(profileB.PetId, savedOffline.RemovedFriendPetIds);
            }

            app = LabPetServerHost.BuildApplication(CreateServerOptions());
            await app.StartAsync();
            var restartedRegistry = app.Services.GetRequiredService<IdentityRegistryStore>();
            Assert.True(restartedRegistry.AreMutualFriends(profileA.PetId, profileB.PetId));

            await using var reconnectedGcpA = new GcpSignalRTransport(connOpts, idStoreA);
            await using var reconnectedSocialA = new SocialCoordinatorService(reconnectedGcpA, storeA, new SoloProgressionService());
            await reconnectedSocialA.StartAsync(profileA);

            await WaitUntilAsync(
                () => !restartedRegistry.AreMutualFriends(profileA.PetId, profileB.PetId),
                TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (app is not null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }
    }

    [Fact]
    public void LanDiscoveryHello_MasksMusicSleepAndFocusStatesForUnauthenticatedPeers()
    {
        var observer = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
        observer.HandleDiscoveryHello(new DiscoveryHelloPacket
        {
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            AppVersion = "0.3.17",
            PetId = "stranger-1",
            OwnerName = "외부",
            PetName = "비친구",
            IsMusicActive = true,
            IsSleeping = true,
            IsFocusing = true,
            TcpPort = 45680
        }, "192.0.2.99", CancellationToken.None);

        var peer = Assert.Single(observer.GetDiscoveredPeers());
        Assert.False(peer.IsFriend);
        Assert.False(peer.IsMusicActive);
        Assert.False(peer.IsSleeping);
        Assert.False(peer.IsFocusing);
    }

    [Fact]
    public async Task EconomyAndGame_AllowsValidHighMultiWins_BlocksFailedOrTamperedSettlement_AndPreventsPrimaryRollback()
    {
        // 1. JsonAppDataStore 구버전 profile.json 덮어쓰기 롤백 차단 검증
        var rollbackDir = Path.Combine(_tempRoot, "rollback_store");
        var store = new JsonAppDataStore(new StateMigrator(), rollbackDir);
        var p = await store.LoadProfileAsync();
        var economy = new PetEconomyService();
        economy.AddCoins(p, 100);
        await store.SaveProfileAsync(p);
        var rev1Bytes = await File.ReadAllBytesAsync(Path.Combine(rollbackDir, "profile.json"));

        economy.AddCoins(p, 350);
        await store.SaveProfileAsync(p);
        await File.WriteAllBytesAsync(Path.Combine(rollbackDir, "profile.json"), rev1Bytes);

        var reloaded = await store.LoadProfileAsync();
        Assert.Equal(450L, reloaded.SoloProgress.Coins);

        // 2. 멀티플레이 정산·정산 실패 롤백·비정상 성장 차단 검증
        await WpfTestHost.RunAsync(async () =>
        {
            var gameDir = Path.Combine(_tempRoot, "game_store");
            var toggleStore = new ToggleFailProfileStore(new JsonAppDataStore(new StateMigrator(), gameDir));
            var transport = new NoopSocialTransport();
            var social = new SocialCoordinatorService(transport, toggleStore, new SoloProgressionService());
            var friend = new FriendEntry
            {
                PetId = Guid.NewGuid().ToString("D"),
                OwnerName = "친구",
                PetName = "상대",
                PairSecretBase64 = PacketSigner.GeneratePairSecretBase64()
            };
            await toggleStore.SaveFriendsAsync(new FriendsStore { Friends = [friend] });
            var profile = await toggleStore.LoadProfileAsync();
            new PetEconomyService().AddCoins(profile, 1_000);
            await toggleStore.SaveProfileAsync(profile);
            await social.StartAsync(profile);

            try
            {
                using var games = new CoinGamesViewModel(toggleStore, new SoloProgressionService(), social);
                games.BindProfile(profile);
                var notifications = new List<string>();
                games.GameNotification += (_, msg) => notifications.Add(msg);

                // A. 정상 다수 승리 (+350 net x 16회 = +5,600 > UnexplainedCoinsLimit 5,000) -> 오탐 초기화 없음
                for (var round = 0; round < 16; round++)
                {
                    await RunIncomingJackpotLadderAsync(games, profile, friend, Guid.NewGuid().ToString("N"), participants: 8, wager: 50);
                }

                Assert.Equal(6_600L, profile.SoloProgress.Coins);
                Assert.Equal(0L, profile.SoloProgress.GrowthAudit?.DetectionCount ?? 0);
                Assert.False(profile.RequiresGrowthRecovery);

                // B. 저장 실패 시 정산 실패 표시 및 코인/예치금 롤백
                notifications.Clear();
                var ioFailSession = Guid.NewGuid().ToString("N");
                await RunIncomingJackpotLadderAsync(
                    games, profile, friend, ioFailSession, participants: 2, wager: 50,
                    beforeComplete: () => toggleStore.FailNextSaveProfile = true);

                Assert.Equal("정산 실패", games.LadderResultHeadline);
                Assert.DoesNotContain("획득", games.LadderResultSettlementText, StringComparison.Ordinal);
                Assert.Equal(["게임 변경을 저장하지 못했습니다."], notifications);
                Assert.Equal(6_550L, profile.SoloProgress.Coins);
                Assert.True(profile.SoloProgress.PendingGameWagers.ContainsKey(ioFailSession));
                toggleStore.FailNextSaveProfile = false;

                // C. 게임 중 비정상 성장 변조 시 정산 차단 및 성공 문구 미표시
                notifications.Clear();
                var tamperSession = Guid.NewGuid().ToString("N");
                await RunIncomingJackpotLadderAsync(
                    games, profile, friend, tamperSession, participants: 2, wager: 50,
                    beforeComplete: () => profile.SoloProgress.Coins = 999_999);

                Assert.Equal("정산 실패", games.LadderResultHeadline);
                Assert.DoesNotContain("획득", games.LadderResultSettlementText, StringComparison.Ordinal);
                Assert.Empty(notifications);
                Assert.Equal(0L, profile.SoloProgress.Coins);
                Assert.Empty(profile.SoloProgress.PendingGameWagers);
            }
            finally
            {
                await social.DisposeAsync();
            }
        });
    }

    [Fact]
    public void WorkflowSecurity_PinsExternalActionsToShaAndProtectsReleaseArtifacts()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            return;
        }

        var verifyYml = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "verify.yml"));
        var releaseYml = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "release.yml"));

        var shaUsesPattern = new Regex(@"uses:\s*[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-/]+@([0-9a-f]{40})\b");
        var allUsesPattern = new Regex(@"uses:\s*\S+");

        foreach (var yml in new[] { verifyYml, releaseYml })
        {
            var allUses = allUsesPattern.Matches(yml);
            Assert.NotEmpty(allUses);
            Assert.Equal(allUses.Count, shaUsesPattern.Matches(yml).Count);
        }

        Assert.Contains("permissions:\n  contents: read", verifyYml.Replace("\r\n", "\n"), StringComparison.Ordinal);
        var normalizedRelease = releaseYml.Replace("\r\n", "\n");
        Assert.Contains("permissions:\n  contents: read", normalizedRelease, StringComparison.Ordinal);
        Assert.Contains("release:\n    runs-on: windows-latest\n    permissions:\n      contents: write", normalizedRelease, StringComparison.Ordinal);
        Assert.DoesNotContain("--token \"${{ github.token }}\"", normalizedRelease, StringComparison.Ordinal);
        Assert.Contains("$env:GITHUB_TOKEN", normalizedRelease, StringComparison.Ordinal);
        Assert.Contains("LABPET_SERVER_URL은 https://", normalizedRelease, StringComparison.Ordinal);
        Assert.Contains("device_identity.json", normalizedRelease, StringComparison.Ordinal);
    }

    private static async Task RunIncomingJackpotLadderAsync(
        CoinGamesViewModel games,
        PetProfile myProfile,
        FriendEntry hostFriend,
        string sessionId,
        int participants,
        int wager,
        Action? beforeComplete = null)
    {
        var members = new List<LadderMemberStateDto>();
        for (var i = 0; i < participants; i++)
        {
            members.Add(new LadderMemberStateDto
            {
                PetId = i == 1 ? myProfile.PetId : (i == 0 ? hostFriend.PetId : $"npc-{i}"),
                DisplayName = i == 1 ? "나" : $"참가{i}",
                SelectedSlot = i,
                AgreedPrizeRevision = 1
            });
        }

        var prizes = Enumerable.Repeat("통과", participants).ToList();
        prizes[0] = "통과";
        prizes[1] = "잭팟";

        var waitingRoom = new LadderRoomStateDto
        {
            SessionId = sessionId,
            HostPetId = hostFriend.PetId,
            Phase = LadderRoomPhase.WaitingInvites,
            WagerCoins = wager,
            SlotCount = participants,
            PendingInviteCount = participants - 1,
            PrizeRevision = 1,
            Prizes = prizes,
            Members =
            [
                new LadderMemberStateDto
                {
                    PetId = hostFriend.PetId,
                    DisplayName = "참가0",
                    SelectedSlot = null,
                    AgreedPrizeRevision = 1
                }
            ]
        };

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        Task SendAsync(string action, object data) => games.HandleGameEventAsync(new GameEventReceivedEventArgs(
            hostFriend.PetId,
            new GameEventPayload
            {
                GameType = LabLadderEngine.GameTypeId,
                SessionId = sessionId,
                Action = action,
                WagerCoins = wager,
                Data = JsonSerializer.Serialize(data, ProtocolJson.PacketOptions),
                InviteExpiresAtUtc = action is "offer" or "ready" or "begin" ? deadline
                    : action == "prepare-start" ? DateTimeOffset.UtcNow.AddSeconds(8) : null
            }));

        await SendAsync("offer", waitingRoom);
        var acceptTask = games.AcceptIncomingInviteAsync();
        await SendAsync("ready", waitingRoom);
        await SendAsync("begin", waitingRoom);
        await acceptTask;

        var pickingRoom = new LadderRoomStateDto
        {
            SessionId = sessionId,
            HostPetId = hostFriend.PetId,
            Phase = LadderRoomPhase.PickingSlots,
            WagerCoins = wager,
            SlotCount = participants,
            Prizes = prizes,
            Members = members
        };
        await SendAsync("state", pickingRoom);

        var board = new LadderBoard(
            members.Select(m => m.DisplayName).ToList(),
            prizes,
            LabLadderEngine.DefaultLevels,
            []);
        var start = LabLadderEngine.CreateStartDto(pickingRoom, board);
        await SendAsync("prepare-start", start);
        await SendAsync("start", start);

        beforeComplete?.Invoke();
        await games.CompleteLadderRevealAsync();
    }

    private sealed class ToggleFailProfileStore(IAppDataStore inner) : IAppDataStore
    {
        public bool FailNextSaveProfile { get; set; }
        public string DataDirectory => inner.DataDirectory;
        public Task<PetProfile> LoadProfileAsync(CancellationToken cancellationToken = default) => inner.LoadProfileAsync(cancellationToken);
        public Task<FriendsStore> LoadFriendsAsync(CancellationToken cancellationToken = default) => inner.LoadFriendsAsync(cancellationToken);
        public Task SaveFriendsAsync(FriendsStore friendsStore, CancellationToken cancellationToken = default) => inner.SaveFriendsAsync(friendsStore, cancellationToken);
        public Task SaveProfileAsync(PetProfile profile, CancellationToken cancellationToken = default)
            => FailNextSaveProfile ? throw new IOException("테스트 저장 실패") : inner.SaveProfileAsync(profile, cancellationToken);
    }

    private sealed class NoopSocialTransport : ISocialTransport
    {
        public string TransportName => "Noop";
        public bool IsRunning => true;

        public event EventHandler<IReadOnlyList<PeerSnapshot>>? PeersChanged;
        public event EventHandler<FriendRequestReceivedEventArgs>? FriendRequestReceived;
        public event EventHandler<FriendAcceptedEventArgs>? FriendAccepted;
        public event EventHandler<FriendRejectedEventArgs>? FriendRejected;
        public event EventHandler<BubbleMessageReceivedEventArgs>? BubbleMessageReceived;
        public event EventHandler<QuickReactionReceivedEventArgs>? QuickReactionReceived;
        public event EventHandler<PetInteractionReceivedEventArgs>? PetInteractionReceived;
        public event EventHandler<MusicFrameReceivedEventArgs>? MusicFrameReceived;
        public event EventHandler<MusicFollowStateChangedEventArgs>? MusicFollowStateChanged;
        public event EventHandler<GameEventReceivedEventArgs>? GameEventReceived;
        public event EventHandler<TransportErrorEventArgs>? TransportError;

        public Task StartAsync(PetProfile localProfile, IReadOnlyList<FriendEntry> friends, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateLocalPresenceAsync(LocalPresenceState presence, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void UpdateKnownFriends(IReadOnlyList<FriendEntry> friends) { }
        public IReadOnlyList<PeerSnapshot> GetDiscoveredPeers() => [];
        public Task<bool> SendFriendRequestAsync(PeerSnapshot target, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<FriendOperationResult> SendFriendRequestDetailedAsync(PeerSnapshot target, CancellationToken cancellationToken = default)
            => Task.FromResult(FriendOperationResult.Fail(FriendOperationStage.Validation, "NOOP", false));
        public Task<bool> RespondFriendRequestAsync(string requestId, bool accept, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<FriendOperationResult> RespondFriendRequestDetailedAsync(string requestId, bool accept, CancellationToken cancellationToken = default)
            => Task.FromResult(FriendOperationResult.Fail(FriendOperationStage.Validation, "NOOP", false));
        public Task<bool> SendBubbleMessageAsync(FriendEntry friend, string message, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> SendQuickReactionAsync(FriendEntry friend, string reactionId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> SendPetInteractionAsync(FriendEntry friend, string interactionType, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> SetMusicFollowAsync(FriendEntry hostFriend, bool enable, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> SendGameEventAsync(FriendEntry friend, GameEventPayload payload, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public ValueTask PublishMusicFrameAsync(MusicFrameData frame, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync()
        {
            _ = (PeersChanged, FriendRequestReceived, FriendAccepted, FriendRejected, BubbleMessageReceived,
                QuickReactionReceived, PetInteractionReceived, MusicFrameReceived, MusicFollowStateChanged,
                GameEventReceived, TransportError);
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<string> IssueTokenAsync(HttpClient http, string petId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var chRes = await http.PostAsJsonAsync(SocialHubConstants.AuthChallengePath, new AuthChallengeRequest
        {
            PetId = petId,
            DevicePublicKeyBase64 = pub
        });
        chRes.EnsureSuccessStatusCode();
        var ch = (await chRes.Content.ReadFromJsonAsync<AuthChallengeResponse>())!;
        var canonical = RemoteAuthSigner.BuildChallengeCanonicalString(ch.Nonce, petId, pub, ch.IssuedAtUtc);

        var tokRes = await http.PostAsJsonAsync(SocialHubConstants.AuthTokenPath, new AuthTokenRequest
        {
            PetId = petId,
            DevicePublicKeyBase64 = pub,
            Nonce = ch.Nonce,
            IssuedAtUtc = ch.IssuedAtUtc,
            SignatureBase64 = RemoteAuthSigner.SignChallenge(key, canonical)
        });
        tokRes.EnsureSuccessStatusCode();
        var tok = (await tokRes.Content.ReadFromJsonAsync<AuthTokenResponse>())!;
        return tok.AccessToken;
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

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LabPet.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
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
