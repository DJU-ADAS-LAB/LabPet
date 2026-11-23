using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
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
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LabPet.Tests;

public sealed class FriendPairingReliabilityTests : IAsyncLifetime
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "LabPet_PairRel_" + Guid.NewGuid().ToString("N"));
    private readonly int _serverPort = GetFreeTcpPort();
    private readonly List<LogRecord> _serverLogs = [];
    private readonly object _serverLogLock = new();
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
            ServerSecret = "pair-reliability-test-fixed-secret-2026-abcdef!",
            RequireConfiguredSecret = true,
            DataDirectory = ServerDataDir
        };
        _server = LabPetServerHost.BuildApplication(
            opts,
            configureLogging: logging => logging.AddProvider(new CaptureLoggerProvider(_serverLogs, _serverLogLock)));
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
    public async Task LanPairing_SavesSameKeyOnBothSidesAndRelaysEncryptedMessages()
    {
        var storeA = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "lan_a"));
        var storeB = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "lan_b"));
        await using var lanA = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
        await using var lanB = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
        await using var socialA = new SocialCoordinatorService(lanA, storeA, new SoloProgressionService());
        await using var socialB = new SocialCoordinatorService(lanB, storeB, new SoloProgressionService());

        var profileA = new PetProfile { OwnerName = "영희", PetName = "모찌A" };
        var profileB = new PetProfile { OwnerName = "철수", PetName = "냥이B" };
        await socialA.StartAsync(profileA);
        await socialB.StartAsync(profileB);

        var arrivedTcs = new TaskCompletionSource<PendingFriendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptedOnATcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        socialB.FriendRequestArrived += (_, req) => arrivedTcs.TrySetResult(req);
        socialA.SocialNotification += (_, msg) =>
        {
            if (msg.Contains("친구 신청을 수락했어요", StringComparison.Ordinal))
            {
                acceptedOnATcs.TrySetResult();
            }
        };

        // 1. ACK 타임아웃: LAN TCP 응답 지연 시 DELIVERY_FAILED_OR_TIMEOUT 반환 및 임시 키 정리
        using var stalledTcp = new TcpListener(IPAddress.Loopback, 0);
        stalledTcp.Start();
        var stalledPort = ((IPEndPoint)stalledTcp.LocalEndpoint).Port;
        using var stallHoldCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var acceptHoldTask = Task.Run(async () =>
        {
            using var client = await stalledTcp.AcceptTcpClientAsync(stallHoldCts.Token);
            try { await Task.Delay(Timeout.Infinite, stallHoldCts.Token); } catch (OperationCanceledException) { }
        });

        var lanTimeoutRes = await socialA.SendFriendRequestDetailedAsync(new PeerSnapshot
        {
            PetId = profileB.PetId,
            OwnerName = profileB.OwnerName,
            PetName = profileB.PetName,
            IpAddress = IPAddress.Loopback.ToString(),
            TcpPort = stalledPort,
            TransportKind = PeerTransportKind.Lan,
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion
        });
        Assert.False(lanTimeoutRes.Succeeded);
        Assert.Equal(FriendOperationErrorCodes.DeliveryFailedOrTimeout, lanTimeoutRes.ErrorCode);
        Assert.True(lanTimeoutRes.IsTransient);
        Assert.False(lanA.HasPendingOutgoingRequest(profileB.PetId));
        stallHoldCts.Cancel();
        try { await acceptHoldTask; } catch { }

        // 2. 정상 LAN 친구 신청 및 수락 + 양측 저장 + E2EE 메시지 송수신
        var targetB = new PeerSnapshot
        {
            PetId = profileB.PetId,
            OwnerName = profileB.OwnerName,
            PetName = profileB.PetName,
            IpAddress = IPAddress.Loopback.ToString(),
            TcpPort = lanB.BoundTcpPort,
            TransportKind = PeerTransportKind.Lan,
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion
        };

        var sendResult = await socialA.SendFriendRequestDetailedAsync(targetB);
        Assert.True(sendResult.Succeeded);
        Assert.Equal(FriendOperationStage.Completed, sendResult.Stage);
        Assert.Equal(PeerTransportKind.Lan, sendResult.Route);

        var incoming = await arrivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var acceptResult = await socialB.RespondFriendRequestDetailedAsync(incoming, accept: true);
        Assert.True(acceptResult.Succeeded);
        Assert.Equal(FriendOperationStage.Completed, acceptResult.Stage);
        Assert.Equal(PeerTransportKind.Lan, acceptResult.Route);

        await acceptedOnATcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await socialA.LastRemoteAcceptPersistTask.WaitAsync(TimeSpan.FromSeconds(5));

        var savedFriendsA = (await storeA.LoadFriendsAsync()).Friends;
        var savedFriendsB = (await storeB.LoadFriendsAsync()).Friends;
        var entryBOnA = Assert.Single(savedFriendsA);
        var entryAOnB = Assert.Single(savedFriendsB);
        Assert.Equal(entryBOnA.PairSecretBase64, entryAOnB.PairSecretBase64);

        var msgOnBTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var msgOnATcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        socialB.FriendMessageReceived += (_, m) => msgOnBTcs.TrySetResult(m.Text);
        socialA.FriendMessageReceived += (_, m) => msgOnATcs.TrySetResult(m.Text);

        Assert.True(await socialA.SendBubbleMessageAsync(entryBOnA, "LAN_암호화_인사"));
        Assert.True(await socialB.SendBubbleMessageAsync(entryAOnB, "LAN_암호화_답장"));
        Assert.Equal("LAN_암호화_인사", await msgOnBTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("LAN_암호화_답장", await msgOnATcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task GcpHybridDifferentNetworks_RegistersServerAndLocalFriendsAndSyncsEncryptedChat()
    {
        var storeA = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "gcp_a"));
        var storeB = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "gcp_b"));
        var opts = new ServerConnectionOptions { Enabled = true, ServerUrl = ServerUrl };
        await using var lanA = new LanP2pTransport(0, 0, 0);
        await using var lanB = new LanP2pTransport(0, 0, 0);
        await using var gcpA = new GcpSignalRTransport(opts, new DeviceIdentityStore(Path.Combine(_tempRoot, "gcp_id_a")));
        await using var gcpB = new GcpSignalRTransport(opts, new DeviceIdentityStore(Path.Combine(_tempRoot, "gcp_id_b")));
        await using var routerA = new HybridTransportRouter(lanA, gcpA);
        await using var routerB = new HybridTransportRouter(lanB, gcpB);
        await using var socialA = new SocialCoordinatorService(routerA, storeA, new SoloProgressionService());
        await using var socialB = new SocialCoordinatorService(routerB, storeB, new SoloProgressionService());

        var profileA = new PetProfile { OwnerName = "원격A", PetName = "하치", FriendCode = "LP-RMT1-0001" };
        var profileB = new PetProfile { OwnerName = "원격B", PetName = "우사기", FriendCode = "LP-RMT2-0002" };
        await socialA.StartAsync(profileA);
        await socialB.StartAsync(profileB);

        // LAN 브로드캐스트 없이 다른 네트워크로 GCP를 통해 피어 탐색
        await WaitUntilAsync(() =>
            socialA.DiscoveredPeers.Any(p => p.PetId == profileB.PetId && p.TransportKind == PeerTransportKind.GcpRelay),
            TimeSpan.FromSeconds(5));
        var peerB = socialA.DiscoveredPeers.Single(p => p.PetId == profileB.PetId);

        var reqTcs = new TaskCompletionSource<PendingFriendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptedATcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        socialB.FriendRequestArrived += (_, r) => reqTcs.TrySetResult(r);
        socialA.SocialNotification += (_, msg) =>
        {
            if (msg.Contains("친구 신청을 수락했어요", StringComparison.Ordinal))
            {
                acceptedATcs.TrySetResult();
            }
        };

        var sendRes = await socialA.SendFriendRequestDetailedAsync(peerB);
        Assert.True(sendRes.Succeeded);
        Assert.Equal(PeerTransportKind.GcpRelay, sendRes.Route);

        var incoming = await reqTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PeerTransportKind.GcpRelay, incoming.TransportKind);

        var acceptRes = await socialB.RespondFriendRequestDetailedAsync(incoming, accept: true);
        Assert.True(acceptRes.Succeeded);
        Assert.Equal(PeerTransportKind.GcpRelay, acceptRes.Route);

        await acceptedATcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await socialA.LastRemoteAcceptPersistTask.WaitAsync(TimeSpan.FromSeconds(5));

        var registry = _server!.Services.GetRequiredService<IdentityRegistryStore>();
        Assert.True(registry.AreMutualFriends(profileA.PetId, profileB.PetId));

        var reloadedA = Assert.Single((await storeA.LoadFriendsAsync()).Friends);
        var reloadedB = Assert.Single((await storeB.LoadFriendsAsync()).Friends);
        Assert.Equal(reloadedA.PairSecretBase64, reloadedB.PairSecretBase64);

        var rxB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rxA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        socialB.FriendMessageReceived += (_, m) => rxB.TrySetResult(m.Text);
        socialA.FriendMessageReceived += (_, m) => rxA.TrySetResult(m.Text);

        Assert.True(await socialA.SendBubbleMessageAsync(reloadedA, "다른네트워크_GCP_메시지"));
        Assert.True(await socialB.SendBubbleMessageAsync(reloadedB, "다른네트워크_GCP_응답"));
        Assert.Equal("다른네트워크_GCP_메시지", await rxB.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("다른네트워크_GCP_응답", await rxA.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AckLossAndTimeout_AllowsIdempotentServerRetryWithSameKeyAndCleansUpOfflineTemporaryKeys()
    {
        var opts = new ServerConnectionOptions { Enabled = true, ServerUrl = ServerUrl };
        var storeA = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "ack_a"));
        var storeB = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "ack_b"));
        await using var gcpA = new GcpSignalRTransport(opts, new DeviceIdentityStore(Path.Combine(_tempRoot, "ack_id_a")));
        await using var gcpB = new GcpSignalRTransport(opts, new DeviceIdentityStore(Path.Combine(_tempRoot, "ack_id_b")));
        await using var socialA = new SocialCoordinatorService(gcpA, storeA, new SoloProgressionService());
        await using var socialB = new SocialCoordinatorService(gcpB, storeB, new SoloProgressionService());

        var profileA = new PetProfile { OwnerName = "신청A", PetName = "모찌" };
        var profileB = new PetProfile { OwnerName = "수락B", PetName = "치이" };

        // 1. 대상 B 오프라인 시 RECIPIENT_OFFLINE 반환 및 임시 ECDH 키 즉시 정리 확인
        await socialA.StartAsync(profileA);
        var offlineRes = await socialA.SendFriendRequestDetailedAsync(new PeerSnapshot
        {
            PetId = profileB.PetId,
            OwnerName = profileB.OwnerName,
            PetName = profileB.PetName,
            TransportKind = PeerTransportKind.GcpRelay,
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion
        });
        Assert.False(offlineRes.Succeeded);
        Assert.Equal(FriendOperationErrorCodes.RecipientOffline, offlineRes.ErrorCode);
        Assert.True(offlineRes.IsTransient);
        Assert.False(gcpA.HasPendingOutgoingRequest(profileB.PetId));

        // 2. B 접속 후 정상 신청 → 첫 FriendAccept 응답 지연으로 B 측 Invoke 타임아웃(ACK 유실 재현)
        await socialB.StartAsync(profileB);
        await WaitUntilAsync(() => gcpA.GetDiscoveredPeers().Any(p => p.PetId == profileB.PetId), TimeSpan.FromSeconds(5));

        var reqTcs = new TaskCompletionSource<PendingFriendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        socialB.FriendRequestArrived += (_, req) => reqTcs.TrySetResult(req);

        var sent = await socialA.SendFriendRequestDetailedAsync(gcpA.GetDiscoveredPeers().Single(p => p.PetId == profileB.PetId));
        Assert.True(sent.Succeeded);
        var incoming = await reqTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var aAcceptCount = 0;
        var aAcceptedDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gcpA.FriendAccepted += (_, _) =>
        {
            Interlocked.Increment(ref aAcceptCount);
            Thread.Sleep(450); // 서버는 정상 ACK(4s 내) 수령 후 MarkApproved 완료, B(200ms 타임아웃)는 ACK 유실로 타임아웃됨
            aAcceptedDone.TrySetResult();
        };

        gcpB.RelayInvokeTimeout = TimeSpan.FromMilliseconds(200);
        var timedOutAccept = await socialB.RespondFriendRequestDetailedAsync(incoming, accept: true);
        Assert.False(timedOutAccept.Succeeded);
        Assert.Equal(FriendOperationErrorCodes.DeliveryFailedOrTimeout, timedOutAccept.ErrorCode);
        Assert.True(timedOutAccept.IsTransient);
        Assert.Empty(socialB.Friends);

        await aAcceptedDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await socialA.LastRemoteAcceptPersistTask.WaitAsync(TimeSpan.FromSeconds(5));
        var registry = _server!.Services.GetRequiredService<IdentityRegistryStore>();
        await WaitUntilAsync(() => registry.AreMutualFriends(profileA.PetId, profileB.PetId), TimeSpan.FromSeconds(5));

        // 3. 타임아웃 해제 후 수락자 B가 동일 RequestId로 재시도 → 서버 MarkApproved 멱등 재시도로 동일 키 등록
        gcpB.RelayInvokeTimeout = TimeSpan.FromSeconds(10);
        var retryAccept = await socialB.RespondFriendRequestDetailedAsync(incoming, accept: true);
        Assert.True(retryAccept.Succeeded);
        Assert.Equal(1, Volatile.Read(ref aAcceptCount));

        var aFriend = Assert.Single((await storeA.LoadFriendsAsync()).Friends);
        var bFriend = Assert.Single((await storeB.LoadFriendsAsync()).Friends);
        Assert.Equal(aFriend.PairSecretBase64, bFriend.PairSecretBase64);

        var rxOnA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        socialA.FriendMessageReceived += (_, m) => rxOnA.TrySetResult(m.Text);
        Assert.True(await socialB.SendBubbleMessageAsync(bFriend, "ACK유실복구_암호화메시지"));
        Assert.Equal("ACK유실복구_암호화메시지", await rxOnA.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ExpiredRequest_ReturnsExpiredCodeDoesNotRequeueAndAllowsReRequest()
    {
        var opts = new ServerConnectionOptions { Enabled = true, ServerUrl = ServerUrl };
        var storeA = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "exp_a"));
        var storeB = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "exp_b"));
        await using var gcpA = new GcpSignalRTransport(opts, new DeviceIdentityStore(Path.Combine(_tempRoot, "exp_id_a")));
        await using var gcpB = new GcpSignalRTransport(opts, new DeviceIdentityStore(Path.Combine(_tempRoot, "exp_id_b")));
        await using var socialA = new SocialCoordinatorService(gcpA, storeA, new SoloProgressionService());
        await using var socialB = new SocialCoordinatorService(gcpB, storeB, new SoloProgressionService());

        gcpA.PairingLifetime = TimeSpan.FromMilliseconds(160);
        gcpB.PairingLifetime = TimeSpan.FromMilliseconds(160);

        var profileA = new PetProfile { OwnerName = "신청자", PetName = "하나" };
        var profileB = new PetProfile { OwnerName = "수락자", PetName = "두리" };
        await socialA.StartAsync(profileA);
        await socialB.StartAsync(profileB);
        await WaitUntilAsync(() => gcpA.GetDiscoveredPeers().Any(p => p.PetId == profileB.PetId), TimeSpan.FromSeconds(5));

        var reqTcs = new TaskCompletionSource<PendingFriendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        socialB.FriendRequestArrived += (_, req) => reqTcs.TrySetResult(req);

        Assert.True((await socialA.SendFriendRequestDetailedAsync(gcpA.GetDiscoveredPeers().Single(p => p.PetId == profileB.PetId))).Succeeded);
        var incoming = await reqTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var vmB = new PetWindowViewModel(storeB, socialB);
        vmB.EnqueueFriendRequest(incoming);
        Assert.NotNull(vmB.PendingFriendRequest);

        await Task.Delay(260);

        // 만료된 신청을 UI에서 수락 클릭하면 만료 안내와 함께 요청이 복구되지 않고 제거되어야 한다.
        await vmB.AcceptFriendRequestCommand.ExecuteAsync(null);
        Assert.Null(vmB.PendingFriendRequest);
        Assert.Contains(vmB.BubbleLines, l => l.Text.Contains("만료", StringComparison.Ordinal));
        Assert.Empty(socialB.Friends);

        // 만료 후 재신청 및 수락 정상 완료 검증
        gcpA.PairingLifetime = TimeSpan.FromMinutes(3);
        gcpB.PairingLifetime = TimeSpan.FromMinutes(3);
        var req2Tcs = new TaskCompletionSource<PendingFriendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        void SecondReqHandler(object? _, PendingFriendRequest r) => req2Tcs.TrySetResult(r);
        socialB.FriendRequestArrived += SecondReqHandler;

        var resendRes = await socialA.SendFriendRequestDetailedAsync(gcpA.GetDiscoveredPeers().Single(p => p.PetId == profileB.PetId));
        Assert.True(resendRes.Succeeded);
        var incoming2 = await req2Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vmB.EnqueueFriendRequest(incoming2);
        await vmB.AcceptFriendRequestCommand.ExecuteAsync(null);

        await socialA.LastRemoteAcceptPersistTask.WaitAsync(TimeSpan.FromSeconds(5));
        var fA = Assert.Single((await storeA.LoadFriendsAsync()).Friends);
        var fB = Assert.Single((await storeB.LoadFriendsAsync()).Friends);
        Assert.Equal(fA.PairSecretBase64, fB.PairSecretBase64);
    }

    [Fact]
    public async Task DuplicateSendAndConcurrentAcceptClicks_PreventDuplicatesAcrossLanAndGcp()
    {
        var storeA = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "dup_a"));
        var storeB = new PausingFriendsStore(new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "dup_b")));
        var opts = new ServerConnectionOptions { Enabled = true, ServerUrl = ServerUrl };
        await using var lanA = new LanP2pTransport(0, 0, 0);
        await using var lanB = new LanP2pTransport(0, 0, 0);
        await using var gcpA = new GcpSignalRTransport(opts, new DeviceIdentityStore(Path.Combine(_tempRoot, "dup_id_a")));
        await using var gcpB = new GcpSignalRTransport(opts, new DeviceIdentityStore(Path.Combine(_tempRoot, "dup_id_b")));
        await using var routerA = new HybridTransportRouter(lanA, gcpA);
        await using var routerB = new HybridTransportRouter(lanB, gcpB);
        await using var socialA = new SocialCoordinatorService(routerA, storeA, new SoloProgressionService());
        await using var socialB = new SocialCoordinatorService(routerB, storeB, new SoloProgressionService());

        var profileA = new PetProfile { OwnerName = "중복A", PetName = "모찌" };
        var profileB = new PetProfile { OwnerName = "중복B", PetName = "강아지" };
        await socialA.StartAsync(profileA);
        await socialB.StartAsync(profileB);

        var reqTcs = new TaskCompletionSource<PendingFriendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        socialB.FriendRequestArrived += (_, r) => reqTcs.TrySetResult(r);

        var lanTargetB = new PeerSnapshot
        {
            PetId = profileB.PetId,
            OwnerName = profileB.OwnerName,
            PetName = profileB.PetName,
            IpAddress = IPAddress.Loopback.ToString(),
            TcpPort = lanB.BoundTcpPort,
            TransportKind = PeerTransportKind.Lan,
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion
        };

        var firstSend = await socialA.SendFriendRequestDetailedAsync(lanTargetB);
        Assert.True(firstSend.Succeeded);

        // 동일 상대에게 LAN 및 GCP 교차 중복 신청 시 모두 차단
        var dupLan = await socialA.SendFriendRequestDetailedAsync(lanTargetB);
        Assert.False(dupLan.Succeeded);
        Assert.Equal(FriendOperationErrorCodes.DuplicatePendingRequest, dupLan.ErrorCode);

        var gcpTargetB = new PeerSnapshot
        {
            PetId = profileB.PetId,
            OwnerName = profileB.OwnerName,
            PetName = profileB.PetName,
            TransportKind = PeerTransportKind.GcpRelay,
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion
        };
        var dupGcp = await socialA.SendFriendRequestDetailedAsync(gcpTargetB);
        Assert.False(dupGcp.Succeeded);
        Assert.Equal(FriendOperationErrorCodes.DuplicatePendingRequest, dupGcp.ErrorCode);

        // 수락 버튼 연속/동시 클릭 차단 검증 (저장 중 3회 연속 실행해도 1회만 등록)
        var incoming = await reqTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var vmB = new PetWindowViewModel(storeB, socialB);
        vmB.EnqueueFriendRequest(incoming);

        var saveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storeB.BeforeSaveFriends = () =>
        {
            saveGate.TrySetResult();
            return resumeGate.Task;
        };

        var acceptNotifications = 0;
        socialB.SocialNotification += (_, msg) =>
        {
            if (msg.Contains("친구 신청을 수락했어요", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref acceptNotifications);
            }
        };

        var click1 = vmB.AcceptFriendRequestCommand.ExecuteAsync(null);
        await saveGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vmB.IsRespondingFriendRequest);
        Assert.False(vmB.CanClickFriendRequestAction);

        var click2 = vmB.AcceptFriendRequestCommand.ExecuteAsync(null);
        var click3 = vmB.AcceptFriendRequestCommand.ExecuteAsync(null);
        resumeGate.TrySetResult();
        await Task.WhenAll(click1, click2, click3);

        await socialA.LastRemoteAcceptPersistTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(socialB.Friends);
        Assert.Single((await storeB.LoadFriendsAsync()).Friends);
        Assert.Equal(1, Volatile.Read(ref acceptNotifications));
    }

    [Fact]
    public async Task ServerRestart_RejectsUnknownPreRestartRequestAndRecoversConnection()
    {
        var opts = new ServerConnectionOptions { Enabled = true, ServerUrl = ServerUrl };
        var storeA = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "rst_a"));
        var storeB = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "rst_b"));
        await using var gcpA = new GcpSignalRTransport(opts, new DeviceIdentityStore(Path.Combine(_tempRoot, "rst_id_a")));
        await using var gcpB = new GcpSignalRTransport(opts, new DeviceIdentityStore(Path.Combine(_tempRoot, "rst_id_b")));
        await using var socialA = new SocialCoordinatorService(gcpA, storeA, new SoloProgressionService());
        await using var socialB = new SocialCoordinatorService(gcpB, storeB, new SoloProgressionService());

        var profileA = new PetProfile { OwnerName = "재시작A", PetName = "구름" };
        var profileB = new PetProfile { OwnerName = "재시작B", PetName = "별" };
        await socialA.StartAsync(profileA);
        await socialB.StartAsync(profileB);
        await WaitUntilAsync(() => gcpA.GetDiscoveredPeers().Any(p => p.PetId == profileB.PetId), TimeSpan.FromSeconds(5));

        var reqTcs = new TaskCompletionSource<PendingFriendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        socialB.FriendRequestArrived += (_, req) => reqTcs.TrySetResult(req);
        Assert.True((await socialA.SendFriendRequestDetailedAsync(gcpA.GetDiscoveredPeers().Single(p => p.PetId == profileB.PetId))).Succeeded);
        var preRestartReq = await reqTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await StopServerAsync();
        await WaitUntilAsync(() => !gcpA.IsRemoteConnected && !gcpB.IsRemoteConnected, TimeSpan.FromSeconds(5));

        await StartServerAsync();
        await WaitUntilAsync(() => gcpA.IsRemoteConnected && gcpB.IsRemoteConnected, TimeSpan.FromSeconds(25));

        // 서버 재시작으로 인메모리 pendingRequest가 사라진 요청 수락 시 PAIRING_REQUEST_UNKNOWN 명확 반환
        var unknownRes = await socialB.RespondFriendRequestDetailedAsync(preRestartReq, accept: true);
        Assert.False(unknownRes.Succeeded);
        Assert.Equal(FriendOperationErrorCodes.PairingRequestUnknown, unknownRes.ErrorCode);

        // 재연결 후 새 친구 신청·수락 및 암호화 메시지 정상 동작
        var req2Tcs = new TaskCompletionSource<PendingFriendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        socialB.FriendRequestArrived += (_, req) => req2Tcs.TrySetResult(req);
        await WaitUntilAsync(() => gcpA.GetDiscoveredPeers().Any(p => p.PetId == profileB.PetId), TimeSpan.FromSeconds(5));

        Assert.True((await socialA.SendFriendRequestDetailedAsync(gcpA.GetDiscoveredPeers().Single(p => p.PetId == profileB.PetId))).Succeeded);
        var postRestartReq = await req2Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var okRes = await socialB.RespondFriendRequestDetailedAsync(postRestartReq, accept: true);
        Assert.True(okRes.Succeeded);
        await socialA.LastRemoteAcceptPersistTask.WaitAsync(TimeSpan.FromSeconds(5));

        var friendA = Assert.Single((await storeA.LoadFriendsAsync()).Friends);
        var friendB = Assert.Single((await storeB.LoadFriendsAsync()).Friends);
        Assert.Equal(friendA.PairSecretBase64, friendB.PairSecretBase64);
    }

    [Fact]
    public async Task LocalSaveFailure_RollsBackMemoryPreventsSuccessNoticeAndRecoversOnRetry()
    {
        var dirA = Path.Combine(_tempRoot, "savefail_a");
        var dirB = Path.Combine(_tempRoot, "savefail_b");
        var storeA = new JsonAppDataStore(new StateMigrator(), dirA);
        var storeB = new JsonAppDataStore(new StateMigrator(), dirB);
        await using var lanA = new LanP2pTransport(0, 0, 0);
        await using var lanB = new LanP2pTransport(0, 0, 0);
        await using var socialA = new SocialCoordinatorService(lanA, storeA, new SoloProgressionService());
        await using var socialB = new SocialCoordinatorService(lanB, storeB, new SoloProgressionService());

        var profileA = new PetProfile { OwnerName = "저장A", PetName = "코코" };
        var profileB = new PetProfile { OwnerName = "저장B", PetName = "보리" };
        await socialA.StartAsync(profileA);
        await socialB.StartAsync(profileB);

        var reqTcs = new TaskCompletionSource<PendingFriendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        socialB.FriendRequestArrived += (_, r) => reqTcs.TrySetResult(r);

        Assert.True((await socialA.SendFriendRequestDetailedAsync(new PeerSnapshot
        {
            PetId = profileB.PetId,
            OwnerName = profileB.OwnerName,
            PetName = profileB.PetName,
            IpAddress = IPAddress.Loopback.ToString(),
            TcpPort = lanB.BoundTcpPort,
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion
        })).Succeeded);
        var incoming = await reqTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var vmB = new PetWindowViewModel(storeB, socialB);
        vmB.EnqueueFriendRequest(incoming);

        var successNoticeCount = 0;
        socialB.SocialNotification += (_, msg) =>
        {
            if (msg.Contains("친구 신청을 수락했어요", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref successNoticeCount);
            }
        };

        // 1. 수락자 측 friends.json.tmp 점유로 저장 실패 유발
        Directory.CreateDirectory(dirB);
        using (File.Open(Path.Combine(dirB, "friends.json.tmp"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await vmB.AcceptFriendRequestCommand.ExecuteAsync(null);
        }

        Assert.Equal(0, Volatile.Read(ref successNoticeCount));
        Assert.Empty(socialB.Friends);
        Assert.NotNull(vmB.PendingFriendRequest);
        Assert.True(vmB.CanRetryPendingFriendRequest);
        Assert.Equal("다시 시도", vmB.AcceptButtonText);

        // 2. 파일 잠금 해제 후 재시도 → 네트워크 재송신 없이 동일 키로 로컬 저장 성공
        await vmB.AcceptFriendRequestCommand.ExecuteAsync(null);
        Assert.Equal(1, Volatile.Read(ref successNoticeCount));
        Assert.Null(vmB.PendingFriendRequest);
        await socialA.LastRemoteAcceptPersistTask.WaitAsync(TimeSpan.FromSeconds(5));

        var fA = Assert.Single((await storeA.LoadFriendsAsync()).Friends);
        var fB = Assert.Single((await storeB.LoadFriendsAsync()).Friends);
        Assert.Equal(fA.PairSecretBase64, fB.PairSecretBase64);

        // 3. 신청자 측 저장 실패 시에도 메모리 롤백 및 성공 알림 미발행 검증
        var dirC = Path.Combine(_tempRoot, "savefail_c");
        var storeC = new JsonAppDataStore(new StateMigrator(), dirC);
        await using var lanC = new LanP2pTransport(0, 0, 0);
        await using var socialC = new SocialCoordinatorService(lanC, storeC, new SoloProgressionService());
        var profileC = new PetProfile { OwnerName = "저장C", PetName = "초코" };
        await socialC.StartAsync(profileC);

        var reqCTcs = new TaskCompletionSource<PendingFriendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        socialB.FriendRequestArrived += (_, r) => reqCTcs.TrySetResult(r);
        var cSuccessNotice = 0;
        var cFailNotice = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        socialC.SocialNotification += (_, msg) =>
        {
            if (msg.Contains("친구 신청을 수락했어요", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref cSuccessNotice);
            }
            else if (msg.Contains("저장에 실패", StringComparison.Ordinal))
            {
                cFailNotice.TrySetResult(msg);
            }
        };

        Assert.True((await socialC.SendFriendRequestDetailedAsync(new PeerSnapshot
        {
            PetId = profileB.PetId,
            OwnerName = profileB.OwnerName,
            PetName = profileB.PetName,
            IpAddress = IPAddress.Loopback.ToString(),
            TcpPort = lanB.BoundTcpPort,
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion
        })).Succeeded);
        var incomingFromC = await reqCTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Directory.CreateDirectory(dirC);
        using (File.Open(Path.Combine(dirC, "friends.json.tmp"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True((await socialB.RespondFriendRequestDetailedAsync(incomingFromC, accept: true)).Succeeded);
            var errMsg = await cFailNotice.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("저장에 실패", errMsg);
        }

        await socialC.LastRemoteAcceptPersistTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, Volatile.Read(ref cSuccessNotice));
        Assert.Empty(socialC.Friends);
    }

    [Fact]
    public async Task HybridRouter_AutoFallsBackToGcpOnTransientLanFailureAndSkipsFallbackOnDeterministicRejection()
    {
        var opts = new ServerConnectionOptions { Enabled = true, ServerUrl = ServerUrl };
        var storeA = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "hyb_a"));
        var storeB = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "hyb_b"));
        await using var lanA = new LanP2pTransport(0, 0, 0);
        await using var lanB = new LanP2pTransport(0, 0, 0);
        await using var gcpA = new GcpSignalRTransport(opts, new DeviceIdentityStore(Path.Combine(_tempRoot, "hyb_id_a")));
        await using var gcpB = new GcpSignalRTransport(opts, new DeviceIdentityStore(Path.Combine(_tempRoot, "hyb_id_b")));
        await using var routerA = new HybridTransportRouter(lanA, gcpA);
        await using var routerB = new HybridTransportRouter(lanB, gcpB);
        await using var socialA = new SocialCoordinatorService(routerA, storeA, new SoloProgressionService());
        await using var socialB = new SocialCoordinatorService(routerB, storeB, new SoloProgressionService());

        var profileA = new PetProfile { OwnerName = "하이브리드A", PetName = "모찌" };
        var profileB = new PetProfile { OwnerName = "하이브리드B", PetName = "토끼" };
        await socialA.StartAsync(profileA);
        await socialB.StartAsync(profileB);
        await WaitUntilAsync(() => gcpA.IsRemoteConnected && gcpB.IsRemoteConnected, TimeSpan.FromSeconds(5));

        var reqTcs = new TaskCompletionSource<PendingFriendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        socialB.FriendRequestArrived += (_, r) => reqTcs.TrySetResult(r);

        // 1. 닫힌 LAN 포트로 신청 시도 → 일시적 LAN 실패 시 HybridTransportRouter가 GCP로 자동 전환
        var closedLanPort = GetFreeTcpPort();
        var res = await socialA.SendFriendRequestDetailedAsync(new PeerSnapshot
        {
            PetId = profileB.PetId,
            OwnerName = profileB.OwnerName,
            PetName = profileB.PetName,
            IpAddress = IPAddress.Loopback.ToString(),
            TcpPort = closedLanPort,
            TransportKind = PeerTransportKind.Lan,
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion
        });
        Assert.True(res.Succeeded);
        Assert.Equal(PeerTransportKind.GcpRelay, res.Route);

        var incoming = await reqTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await socialB.RespondFriendRequestDetailedAsync(incoming, accept: true)).Succeeded);
        await socialA.LastRemoteAcceptPersistTask.WaitAsync(TimeSpan.FromSeconds(5));

        // 2. 이미 친구 상태에서는 확정적 거부(ALREADY_FRIENDS)를 반환하고 GCP로 불필요하게 폴백하지 않음
        var serverLogsBefore = _serverLogs.Count;
        var dupRes = await routerA.SendFriendRequestDetailedAsync(new PeerSnapshot
        {
            PetId = profileB.PetId,
            IpAddress = IPAddress.Loopback.ToString(),
            TcpPort = lanB.BoundTcpPort,
            TransportKind = PeerTransportKind.Lan,
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion
        });
        Assert.False(dupRes.Succeeded);
        Assert.Equal(FriendOperationErrorCodes.AlreadyFriends, dupRes.ErrorCode);
        Assert.False(dupRes.IsTransient);
        Assert.Equal(PeerTransportKind.Lan, dupRes.Route);
        Assert.Equal(serverLogsBefore, _serverLogs.Count);
    }

    [Fact]
    public async Task FailureCodes_PropagateToUiAndLogsWithoutExposingSecrets()
    {
        var traceMessages = new ConcurrentQueue<string>();
        using var traceListener = new CaptureTraceListener(traceMessages);
        Trace.Listeners.Add(traceListener);

        try
        {
            var opts = new ServerConnectionOptions { Enabled = true, ServerUrl = ServerUrl };
            var storeA = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "log_a"));
            await using var gcpA = new GcpSignalRTransport(opts, new DeviceIdentityStore(Path.Combine(_tempRoot, "log_id_a")));
            await using var socialA = new SocialCoordinatorService(gcpA, storeA, new SoloProgressionService());

            var profileA = new PetProfile { OwnerName = "로그A", PetName = "모찌" };
            await socialA.StartAsync(profileA);
            var vm = new SocialViewModel(socialA);

            // 1. RECIPIENT_OFFLINE 코드가 StatusBanner 및 다시 신청 UI 상태까지 전달
            var offlinePeer = new PeerListItemViewModel(new PeerSnapshot
            {
                PetId = Guid.NewGuid().ToString("D"),
                OwnerName = "오프라인주인",
                PetName = "잠든펫",
                TransportKind = PeerTransportKind.GcpRelay,
                ProtocolVersion = ProtocolConstants.CurrentProtocolVersion
            });
            await vm.SendFriendRequestCommand.ExecuteAsync(offlinePeer);
            Assert.Contains("오프라인", vm.StatusBanner);
            Assert.Equal("다시 신청", offlinePeer.FriendRequestButtonText);

            var petVm = new PetWindowViewModel(storeA, socialA);
            var overlayEntry = new QuickOverlayEntry
            {
                PetId = offlinePeer.Snapshot.PetId,
                DisplayTitle = offlinePeer.DisplayTitle,
                Peer = offlinePeer.Snapshot
            };
            await petVm.QuickSendFriendRequestCommand.ExecuteAsync(overlayEntry);
            Assert.False(overlayEntry.IsFriendRequestPending);
            Assert.True(overlayEntry.CanSendFriendRequest);
            Assert.Contains(petVm.BubbleLines, l => l.Text.Contains("오프라인", StringComparison.Ordinal));

            // 2. AUTH_FAILED (PUBLIC_KEY_MISMATCH) 전파 검증
            var attackerStore = new DeviceIdentityStore(Path.Combine(_tempRoot, "log_id_attacker"));
            await using var attacker = new GcpSignalRTransport(opts, attackerStore);
            await using var socialAttacker = new SocialCoordinatorService(attacker, storeA, new SoloProgressionService());
            var vmAttacker = new SocialViewModel(socialAttacker);
            var authErrorTcs = new TaskCompletionSource<TransportErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            socialAttacker.TransportErrorReported += (_, e) =>
            {
                if (e.Operation == "GcpAuth")
                {
                    authErrorTcs.TrySetResult(e);
                }
            };

            // 동일 PetId, 다른 기기 키로 접속 → 서버 403 PUBLIC_KEY_MISMATCH → TransportErrorReported + StatusBanner
            await attacker.StartAsync(new PetProfile { PetId = profileA.PetId, PetName = "사칭펫" }, []);
            var authErr = await authErrorTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(FriendOperationErrorCodes.PublicKeyMismatch, authErr.ErrorCode);
            await WaitUntilAsync(
                () => vmAttacker.StatusBanner.Contains(FriendOperationErrorCodes.PublicKeyMismatch, StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            // 3. PAIRING_REQUEST_UNKNOWN 전파 검증
            var unknownRes = await socialA.RespondFriendRequestDetailedAsync(
                new PendingFriendRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("D"), "없는주인", "없는펫", new PetAppearance(), 1, "GCP", 45680, PeerTransportKind.GcpRelay),
                accept: true);
            Assert.Equal(FriendOperationErrorCodes.PairingRequestUnknown, unknownRes.ErrorCode);

            // 4. RATE_LIMITED 전파 검증 (전용 저한도 서버 포트 구성)
            var rateLimitPort = GetFreeTcpPort();
            var rateLimitOpts = new ServerOptions
            {
                ListenUrl = $"http://127.0.0.1:{rateLimitPort}",
                ServerSecret = "rate-limit-test-fixed-secret-2026-abcdef!",
                RequireConfiguredSecret = true,
                DataDirectory = Path.Combine(_tempRoot, "rl_server"),
                ControlRateLimitPerSecond = 5
            };
            Directory.CreateDirectory(rateLimitOpts.DataDirectory);
            await using var rlServer = LabPetServerHost.BuildApplication(rateLimitOpts);
            await rlServer.StartAsync();
            try
            {
                var rlClientOpts = new ServerConnectionOptions { Enabled = true, ServerUrl = rateLimitOpts.ListenUrl };
                await using var gcpRl = new GcpSignalRTransport(rlClientOpts, new DeviceIdentityStore(Path.Combine(_tempRoot, "log_id_rl")));
                await using var socialRl = new SocialCoordinatorService(gcpRl, storeA, new SoloProgressionService());
                await socialRl.StartAsync(new PetProfile { OwnerName = "속도제한", PetName = "테스트" });

                FriendOperationResult? rateLimitedResult = null;
                var dummyTarget = new PeerSnapshot
                {
                    PetId = Guid.NewGuid().ToString("D"),
                    OwnerName = "대상",
                    PetName = "펫",
                    TransportKind = PeerTransportKind.GcpRelay,
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion
                };
                for (var i = 0; i < 12; i++)
                {
                    var res = await socialRl.SendFriendRequestDetailedAsync(dummyTarget);
                    if (res.ErrorCode == FriendOperationErrorCodes.RateLimited)
                    {
                        rateLimitedResult = res;
                        break;
                    }
                }

                Assert.NotNull(rateLimitedResult);
                Assert.Equal(FriendOperationErrorCodes.RateLimited, rateLimitedResult!.ErrorCode);
                Assert.Contains("빠릅니다", SocialCoordinatorService.FormatFriendOperationFailure(rateLimitedResult, isAccept: false));
            }
            finally
            {
                await rlServer.StopAsync();
            }

            // 5. 서버·클라이언트 로그 점검: 오류 코드는 존재하되, 비밀값/토큰/개인키는 전혀 기록되지 않음
            var combinedServerLogs = string.Join('\n', SnapshotServerLogs());
            var combinedTraceLogs = string.Join('\n', traceMessages);
            Assert.Contains("RECIPIENT_OFFLINE", combinedServerLogs);
            Assert.Contains("RECIPIENT_OFFLINE", combinedTraceLogs);
            Assert.Contains("PUBLIC_KEY_MISMATCH", combinedTraceLogs);

            Assert.DoesNotContain("PairSecretBase64", combinedServerLogs, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AccessToken", combinedServerLogs, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PrivateKey", combinedServerLogs, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Bearer ", combinedTraceLogs, StringComparison.OrdinalIgnoreCase);

            vm.Detach();
            vmAttacker.Detach();
        }
        finally
        {
            Trace.Listeners.Remove(traceListener);
        }
    }

    private List<string> SnapshotServerLogs()
    {
        lock (_serverLogLock)
        {
            return _serverLogs.Select(x => $"{x.Category}:{x.Message}").ToList();
        }
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

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class PausingFriendsStore(IAppDataStore inner) : IAppDataStore
    {
        public Func<Task>? BeforeSaveFriends { get; set; }
        public string DataDirectory => inner.DataDirectory;

        public Task<PetProfile> LoadProfileAsync(CancellationToken cancellationToken = default)
            => inner.LoadProfileAsync(cancellationToken);

        public Task SaveProfileAsync(PetProfile profile, CancellationToken cancellationToken = default)
            => inner.SaveProfileAsync(profile, cancellationToken);

        public Task<FriendsStore> LoadFriendsAsync(CancellationToken cancellationToken = default)
            => inner.LoadFriendsAsync(cancellationToken);

        public async Task SaveFriendsAsync(FriendsStore friendsStore, CancellationToken cancellationToken = default)
        {
            if (BeforeSaveFriends is { } beforeSave)
            {
                await beforeSave();
            }

            await inner.SaveFriendsAsync(friendsStore, cancellationToken);
        }
    }

    private sealed record LogRecord(string Category, LogLevel Level, string Message);

    private sealed class CaptureLoggerProvider(List<LogRecord> target, object sync) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, target, sync);
        public void Dispose() { }

        private sealed class CaptureLogger(string category, List<LogRecord> target, object sync) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (sync)
                {
                    target.Add(new LogRecord(category, logLevel, formatter(state, exception)));
                }
            }
        }
    }

    private sealed class CaptureTraceListener(ConcurrentQueue<string> messages) : TraceListener
    {
        public override void Write(string? message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                messages.Enqueue(message);
            }
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                messages.Enqueue(message);
            }
        }
    }
}
