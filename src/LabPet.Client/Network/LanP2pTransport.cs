using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;

namespace LabPet.Client.Network;

/// <summary>
/// 외부 서버 없이 로컬 네트워크(LAN)만으로 동작하는 서버리스 P2P 소셜 전송 계층.
/// - UDP 45678: 물리 LAN 서브넷 브로드캐스트 자동 탐색 (3초 송신 / 10초 오프라인 판정)
/// - TCP 45680: 친구 신청/수락, HMAC-SHA256 인증 메시지/반응/쓰다듬기, 노래 따라하기 구독 제어
/// - UDP 45679: 구독 중인 인증된 친구 IP 목록에만 30Hz 경량 오디오 프레임 유니캐스트
/// </summary>
public sealed class LanP2pTransport : ISocialTransport
{
    private sealed record MusicSubscriber(string PetId, IPEndPoint UdpEndPoint, string PairSecretBase64, DateTimeOffset SubscribedAtUtc);

    private readonly int _udpDiscoveryPort;
    private readonly int _udpMusicPort;
    private readonly int _tcpControlPort;

    private readonly ConcurrentDictionary<string, PeerSnapshot> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FriendEntry> _friendsByPetId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MusicSubscriber> _musicSubscribers = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _stateLock = new();
    private CancellationTokenSource? _cts;
    private UdpClient? _discoveryUdp;
    private UdpClient? _musicUdp;
    private TcpListener? _tcpListener;

    private string _localPetId = string.Empty;
    private LocalPresenceState _localPresence = new("모찌", new PetAppearance(), 1, false, "0.1.0");
    private int _boundTcpPort;

    public string TransportName => "LAN-P2P";
    public bool IsRunning { get; private set; }

    public event EventHandler<IReadOnlyList<PeerSnapshot>>? PeersChanged;
    public event EventHandler<FriendRequestReceivedEventArgs>? FriendRequestReceived;
    public event EventHandler<FriendAcceptedEventArgs>? FriendAccepted;
    public event EventHandler<FriendRejectedEventArgs>? FriendRejected;
    public event EventHandler<BubbleMessageReceivedEventArgs>? BubbleMessageReceived;
    public event EventHandler<QuickReactionReceivedEventArgs>? QuickReactionReceived;
    public event EventHandler<PetInteractionReceivedEventArgs>? PetInteractionReceived;
    public event EventHandler<MusicFrameReceivedEventArgs>? MusicFrameReceived;
    public event EventHandler<MusicFollowStateChangedEventArgs>? MusicFollowStateChanged;
    public event EventHandler<TransportErrorEventArgs>? TransportError;

    public LanP2pTransport(
        int udpDiscoveryPort = ProtocolConstants.UdpDiscoveryPort,
        int udpMusicPort = ProtocolConstants.UdpMusicFramePort,
        int tcpControlPort = ProtocolConstants.TcpControlPort)
    {
        _udpDiscoveryPort = udpDiscoveryPort;
        _udpMusicPort = udpMusicPort;
        _tcpControlPort = tcpControlPort;
    }

    public int BoundTcpPort => _boundTcpPort;

    public Task StartAsync(PetProfile localProfile, IReadOnlyList<FriendEntry> friends, CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (IsRunning)
            {
                return Task.CompletedTask;
            }

            _localPetId = localProfile.PetId;
            _localPresence = new LocalPresenceState(
                localProfile.PetName,
                localProfile.Appearance.Clone(),
                localProfile.SoloProgress.Level,
                IsMusicActive: false,
                AppVersion: typeof(LanP2pTransport).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");

            UpdateKnownFriends(friends);
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // 1. TCP 제어 리스너 시작 (기본 45680, 충돌 시 동적 포트 폴백)
            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, _tcpControlPort);
                _tcpListener.Start();
                _boundTcpPort = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;
            }
            catch (SocketException)
            {
                _tcpListener = new TcpListener(IPAddress.Any, 0);
                _tcpListener.Start();
                _boundTcpPort = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;
            }

            // 2. UDP 탐색 소켓 (45678, ReuseAddress + Broadcast)
            _discoveryUdp = CreateReusableUdpClient(_udpDiscoveryPort, enableBroadcast: true);

            // 3. UDP 오디오 프레임 소켓 (45679, ReuseAddress)
            _musicUdp = CreateReusableUdpClient(_udpMusicPort, enableBroadcast: false);

            IsRunning = true;
            var ct = _cts.Token;
            _ = Task.Run(() => DiscoveryBroadcastLoopAsync(ct), ct);
            _ = Task.Run(() => DiscoveryReceiveLoopAsync(ct), ct);
            _ = Task.Run(() => MusicFrameReceiveLoopAsync(ct), ct);
            _ = Task.Run(() => TcpAcceptLoopAsync(ct), ct);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (!IsRunning)
            {
                return Task.CompletedTask;
            }

            IsRunning = false;
            try { _cts?.Cancel(); } catch { }
            try { _tcpListener?.Stop(); } catch { }
            try { _discoveryUdp?.Dispose(); } catch { }
            try { _musicUdp?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }

            _tcpListener = null;
            _discoveryUdp = null;
            _musicUdp = null;
            _cts = null;
            _musicSubscribers.Clear();
        }

        return Task.CompletedTask;
    }

    public Task UpdateLocalPresenceAsync(LocalPresenceState presence, CancellationToken cancellationToken = default)
    {
        _localPresence = presence;
        return Task.CompletedTask;
    }

    public void UpdateKnownFriends(IReadOnlyList<FriendEntry> friends)
    {
        _friendsByPetId.Clear();
        foreach (var f in friends)
        {
            if (!string.IsNullOrWhiteSpace(f.PetId))
            {
                _friendsByPetId[f.PetId] = f;
            }
        }

        // 기존 피어 스냅샷의 친구 여부 배지 동기화
        var changed = false;
        foreach (var peer in _peers.Values)
        {
            var isFriend = _friendsByPetId.ContainsKey(peer.PetId);
            if (peer.IsFriend != isFriend)
            {
                peer.IsFriend = isFriend;
                changed = true;
            }
        }

        if (changed)
        {
            RaisePeersChanged();
        }
    }

    public IReadOnlyList<PeerSnapshot> GetDiscoveredPeers()
        => _peers.Values.OrderByDescending(p => p.IsFriend).ThenBy(p => p.PetName, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<bool> SendFriendRequestAsync(PeerSnapshot target, CancellationToken cancellationToken = default)
    {
        if (target.RequiresUpdate || string.IsNullOrWhiteSpace(target.IpAddress))
        {
            return false;
        }

        var presence = _localPresence;
        var payload = new FriendRequestPayload
        {
            RequesterName = presence.PetName,
            Appearance = presence.Appearance.Clone(),
            Level = presence.Level,
            TcpPort = _boundTcpPort
        };

        var ack = await SendControlPacketAsync(
            target.IpAddress,
            target.TcpPort,
            PacketType.FriendRequest,
            payload,
            pairSecretBase64: null,
            cancellationToken).ConfigureAwait(false);

        return ack is { Accepted: true };
    }

    public async Task<bool> RespondFriendRequestAsync(
        string requesterPetId,
        string requesterIp,
        int requesterTcpPort,
        bool accept,
        string? pairSecretBase64,
        CancellationToken cancellationToken = default)
    {
        if (accept)
        {
            if (string.IsNullOrWhiteSpace(pairSecretBase64))
            {
                return false;
            }

            var presence = _localPresence;
            var payload = new FriendAcceptPayload
            {
                AccepterName = presence.PetName,
                Appearance = presence.Appearance.Clone(),
                PairSecretBase64 = pairSecretBase64,
                TcpPort = _boundTcpPort
            };

            var ack = await SendControlPacketAsync(
                requesterIp,
                requesterTcpPort,
                PacketType.FriendAccept,
                payload,
                pairSecretBase64: null,
                cancellationToken).ConfigureAwait(false);

            return ack is { Accepted: true };
        }
        else
        {
            var ack = await SendControlPacketAsync(
                requesterIp,
                requesterTcpPort,
                PacketType.FriendReject,
                new FriendRejectPayload { Reason = "rejected_by_user" },
                pairSecretBase64: null,
                cancellationToken).ConfigureAwait(false);

            return ack is { Accepted: true };
        }
    }

    public async Task<bool> SendBubbleMessageAsync(FriendEntry friend, string message, CancellationToken cancellationToken = default)
    {
        var trimmed = (message ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.Length > ProtocolConstants.MaxBubbleMessageLength)
        {
            trimmed = trimmed[..ProtocolConstants.MaxBubbleMessageLength];
        }

        if (!TryResolveFriendEndpoint(friend, out var ip, out var port))
        {
            return false;
        }

        var ack = await SendControlPacketAsync(
            ip,
            port,
            PacketType.BubbleMessage,
            new BubbleMessagePayload { Text = trimmed },
            friend.PairSecretBase64,
            cancellationToken).ConfigureAwait(false);

        return ack is { Accepted: true };
    }

    public async Task<bool> SendQuickReactionAsync(FriendEntry friend, string reactionId, CancellationToken cancellationToken = default)
    {
        if (!TryResolveFriendEndpoint(friend, out var ip, out var port))
        {
            return false;
        }

        var ack = await SendControlPacketAsync(
            ip,
            port,
            PacketType.QuickReaction,
            new QuickReactionPayload { ReactionId = reactionId },
            friend.PairSecretBase64,
            cancellationToken).ConfigureAwait(false);

        return ack is { Accepted: true };
    }

    public async Task<bool> SendPetInteractionAsync(FriendEntry friend, string interactionType, CancellationToken cancellationToken = default)
    {
        if (!TryResolveFriendEndpoint(friend, out var ip, out var port))
        {
            return false;
        }

        var ack = await SendControlPacketAsync(
            ip,
            port,
            PacketType.PetInteraction,
            new PetInteractionPayload { InteractionType = interactionType },
            friend.PairSecretBase64,
            cancellationToken).ConfigureAwait(false);

        return ack is { Accepted: true };
    }

    public async Task<bool> SetMusicFollowAsync(FriendEntry hostFriend, bool enable, CancellationToken cancellationToken = default)
    {
        if (!TryResolveFriendEndpoint(hostFriend, out var ip, out var port))
        {
            return false;
        }

        var ack = await SendControlPacketAsync(
            ip,
            port,
            enable ? PacketType.MusicFollowOn : PacketType.MusicFollowOff,
            new MusicFollowPayload { ListenerUdpPort = _udpMusicPort },
            hostFriend.PairSecretBase64,
            cancellationToken).ConfigureAwait(false);

        return ack is { Accepted: true };
    }

    public async ValueTask PublishMusicFrameAsync(MusicFrameData frame, CancellationToken cancellationToken = default)
    {
        var udp = _musicUdp;
        if (!IsRunning || udp is null || _musicSubscribers.IsEmpty)
        {
            return;
        }

        foreach (var sub in _musicSubscribers.Values)
        {
            try
            {
                var packet = new MusicFramePacket
                {
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                    SenderPetId = _localPetId,
                    Sequence = frame.Sequence,
                    Bass = frame.Bass,
                    Mid = frame.Mid,
                    Treble = frame.Treble,
                    IsBeat = frame.IsBeat
                };

                var canonical = PacketSigner.BuildMusicFrameCanonicalString(packet);
                packet.SignatureBase64 = PacketSigner.ComputeSignature(canonical, sub.PairSecretBase64);

                var bytes = JsonSerializer.SerializeToUtf8Bytes(packet, ProtocolJson.PacketOptions);
                await udp.SendAsync(bytes, sub.UdpEndPoint, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 개별 구독자 UDP 송신 오류는 다른 구독자에 영향을 주지 않음
            }
        }
    }

    private async Task DiscoveryBroadcastLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(ProtocolConstants.DiscoveryIntervalSeconds));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SendHelloBroadcastOnceAsync(ct).ConfigureAwait(false);
                PruneStalePeers();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                TransportError?.Invoke(this, new TransportErrorEventArgs("DiscoveryBroadcast", ex.Message, ex));
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendHelloBroadcastOnceAsync(CancellationToken ct)
    {
        var udp = _discoveryUdp;
        if (udp is null)
        {
            return;
        }

        var presence = _localPresence;
        var hello = new DiscoveryHelloPacket
        {
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            AppVersion = presence.AppVersion,
            PetId = _localPetId,
            PetName = presence.PetName,
            Appearance = presence.Appearance.Clone(),
            Level = presence.Level,
            IsMusicActive = presence.IsMusicActive,
            TcpPort = _boundTcpPort,
            TimestampUtc = DateTimeOffset.UtcNow
        };

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(hello, ProtocolJson.PacketOptions);
        var broadcastTargets = LanNetworkHelper.GetPhysicalBroadcastAddresses();

        foreach (var broadcastIp in broadcastTargets)
        {
            try
            {
                await udp.SendAsync(payloadBytes, new IPEndPoint(broadcastIp, _udpDiscoveryPort), ct).ConfigureAwait(false);
            }
            catch
            {
                // 특정 인터페이스 송신 실패 시 나머지 인터페이스 계속 진행
            }
        }
    }

    private async Task DiscoveryReceiveLoopAsync(CancellationToken ct)
    {
        var udp = _discoveryUdp;
        if (udp is null)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                var hello = JsonSerializer.Deserialize<DiscoveryHelloPacket>(result.Buffer, ProtocolJson.PacketOptions);
                if (hello is null || string.IsNullOrWhiteSpace(hello.PetId))
                {
                    continue;
                }

                if (string.Equals(hello.PetId, _localPetId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var compat = ProtocolConstants.EvaluateCompatibility(hello.ProtocolVersion);
                var remoteIp = result.RemoteEndPoint.Address.ToString();
                var isFriend = _friendsByPetId.TryGetValue(hello.PetId, out var friendEntry);

                // DHCP IP 변경 시 영구 petId 기준으로 최신 IP/포트 자동 갱신
                if (isFriend && friendEntry is not null)
                {
                    friendEntry.LastKnownIp = remoteIp;
                    friendEntry.LastKnownTcpPort = hello.TcpPort;
                    friendEntry.LastSeenAtUtc = DateTimeOffset.UtcNow;
                }

                var snapshot = new PeerSnapshot
                {
                    PetId = hello.PetId,
                    PetName = hello.PetName,
                    Appearance = hello.Appearance ?? new PetAppearance(),
                    Level = hello.Level,
                    IsMusicActive = hello.IsMusicActive,
                    IpAddress = remoteIp,
                    TcpPort = hello.TcpPort,
                    ProtocolVersion = hello.ProtocolVersion,
                    AppVersion = hello.AppVersion,
                    Compatibility = compat,
                    IsFriend = isFriend,
                    LastSeenUtc = DateTimeOffset.UtcNow
                };

                _peers[hello.PetId] = snapshot;
                RaisePeersChanged();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                // 잘못된 UDP 패킷은 무시
            }
        }
    }

    private async Task MusicFrameReceiveLoopAsync(CancellationToken ct)
    {
        var udp = _musicUdp;
        if (udp is null)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                var packet = JsonSerializer.Deserialize<MusicFramePacket>(result.Buffer, ProtocolJson.PacketOptions);
                if (packet is null || string.IsNullOrWhiteSpace(packet.SenderPetId))
                {
                    continue;
                }

                if (ProtocolConstants.EvaluateCompatibility(packet.ProtocolVersion) != ProtocolCompatibility.Compatible)
                {
                    continue;
                }

                // 인증된 친구 패킷만 HMAC 검증 후 허용
                if (!_friendsByPetId.TryGetValue(packet.SenderPetId, out var friend))
                {
                    continue;
                }

                var canonical = PacketSigner.BuildMusicFrameCanonicalString(packet);
                if (!PacketSigner.VerifySignature(canonical, packet.SignatureBase64, friend.PairSecretBase64))
                {
                    continue;
                }

                MusicFrameReceived?.Invoke(this, new MusicFrameReceivedEventArgs(
                    packet.SenderPetId,
                    new MusicFrameData(packet.Bass, packet.Mid, packet.Treble, packet.IsBeat, packet.Sequence)));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                // 잘못된 오디오 프레임 무시
            }
        }
    }

    private async Task TcpAcceptLoopAsync(CancellationToken ct)
    {
        var listener = _tcpListener;
        if (listener is null)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleIncomingTcpClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
        }
    }

    private async Task HandleIncomingTcpClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 4000;
                client.SendTimeout = 4000;

                var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? string.Empty;
                await using var stream = client.GetStream();

                var envelope = await ReadFramedJsonAsync<ControlPacketEnvelope>(stream, ct).ConfigureAwait(false);
                if (envelope?.Header is null)
                {
                    return;
                }

                var ack = ProcessIncomingControlEnvelope(envelope, remoteIp);
                var ackEnvelope = new ControlPacketEnvelope
                {
                    Header = new PacketHeader
                    {
                        ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                        AppVersion = _localPresence.AppVersion,
                        PacketType = PacketType.Ack,
                        SenderPetId = _localPetId
                    },
                    PayloadJson = JsonSerializer.Serialize(ack, ProtocolJson.PacketOptions)
                };

                await WriteFramedJsonAsync(stream, ackEnvelope, ct).ConfigureAwait(false);
            }
            catch
            {
                // 비정상 TCP 연결 종료 무시
            }
        }
    }

    private AckPayload ProcessIncomingControlEnvelope(ControlPacketEnvelope envelope, string remoteIp)
    {
        var header = envelope.Header;

        // 1. 프로토콜 버전 호환성 차단
        var compat = ProtocolConstants.EvaluateCompatibility(header.ProtocolVersion);
        if (compat != ProtocolCompatibility.Compatible)
        {
            return new AckPayload
            {
                AckMessageId = header.MessageId,
                Accepted = false,
                ErrorCode = "PROTOCOL_MISMATCH"
            };
        }

        // 2. 친구 전용 패킷은 HMAC-SHA256 서명 검증 필수
        var requiresFriendHmac = header.PacketType is
            PacketType.BubbleMessage or
            PacketType.QuickReaction or
            PacketType.PetInteraction or
            PacketType.MusicFollowOn or
            PacketType.MusicFollowOff;

        FriendEntry? friend = null;
        if (requiresFriendHmac)
        {
            if (!_friendsByPetId.TryGetValue(header.SenderPetId, out friend))
            {
                return new AckPayload { AckMessageId = header.MessageId, Accepted = false, ErrorCode = "NOT_FRIEND" };
            }

            var canonical = PacketSigner.BuildControlCanonicalString(header, envelope.PayloadJson);
            if (!PacketSigner.VerifySignature(canonical, header.SignatureBase64, friend.PairSecretBase64))
            {
                return new AckPayload { AckMessageId = header.MessageId, Accepted = false, ErrorCode = "INVALID_SIGNATURE" };
            }

            friend.LastKnownIp = remoteIp;
            friend.LastSeenAtUtc = DateTimeOffset.UtcNow;
        }

        // 3. 패킷 유형별 이벤트 디스패치
        switch (header.PacketType)
        {
            case PacketType.FriendRequest:
            {
                var p = JsonSerializer.Deserialize<FriendRequestPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
                if (p is null) return RejectAck(header.MessageId, "INVALID_PAYLOAD");

                FriendRequestReceived?.Invoke(this, new FriendRequestReceivedEventArgs(
                    header.SenderPetId, p.RequesterName, p.Appearance ?? new PetAppearance(), p.Level, remoteIp, p.TcpPort));
                break;
            }

            case PacketType.FriendAccept:
            {
                var p = JsonSerializer.Deserialize<FriendAcceptPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
                if (p is null || string.IsNullOrWhiteSpace(p.PairSecretBase64)) return RejectAck(header.MessageId, "INVALID_PAYLOAD");

                FriendAccepted?.Invoke(this, new FriendAcceptedEventArgs(
                    header.SenderPetId, p.AccepterName, p.Appearance ?? new PetAppearance(), p.PairSecretBase64, remoteIp, p.TcpPort));
                break;
            }

            case PacketType.FriendReject:
            {
                var p = JsonSerializer.Deserialize<FriendRejectPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
                FriendRejected?.Invoke(this, new FriendRejectedEventArgs(header.SenderPetId, p?.Reason));
                break;
            }

            case PacketType.BubbleMessage:
            {
                var p = JsonSerializer.Deserialize<BubbleMessagePayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
                if (p is null || string.IsNullOrWhiteSpace(p.Text)) return RejectAck(header.MessageId, "INVALID_PAYLOAD");

                var safeText = p.Text.Length > ProtocolConstants.MaxBubbleMessageLength
                    ? p.Text[..ProtocolConstants.MaxBubbleMessageLength]
                    : p.Text;
                BubbleMessageReceived?.Invoke(this, new BubbleMessageReceivedEventArgs(
                    header.SenderPetId, header.MessageId, safeText, header.TimestampUtc));
                break;
            }

            case PacketType.QuickReaction:
            {
                var p = JsonSerializer.Deserialize<QuickReactionPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
                if (p is null) return RejectAck(header.MessageId, "INVALID_PAYLOAD");

                QuickReactionReceived?.Invoke(this, new QuickReactionReceivedEventArgs(header.SenderPetId, p.ReactionId));
                break;
            }

            case PacketType.PetInteraction:
            {
                var p = JsonSerializer.Deserialize<PetInteractionPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
                PetInteractionReceived?.Invoke(this, new PetInteractionReceivedEventArgs(
                    header.SenderPetId, p?.InteractionType ?? "pet"));
                break;
            }

            case PacketType.MusicFollowOn:
            {
                var p = JsonSerializer.Deserialize<MusicFollowPayload>(envelope.PayloadJson, ProtocolJson.PacketOptions);
                var udpPort = p?.ListenerUdpPort ?? _udpMusicPort;
                if (IPAddress.TryParse(remoteIp, out var parsedIp) && friend is not null)
                {
                    _musicSubscribers[header.SenderPetId] = new MusicSubscriber(
                        header.SenderPetId,
                        new IPEndPoint(parsedIp, udpPort),
                        friend.PairSecretBase64,
                        DateTimeOffset.UtcNow);
                    MusicFollowStateChanged?.Invoke(this, new MusicFollowStateChangedEventArgs(header.SenderPetId, true));
                }

                break;
            }

            case PacketType.MusicFollowOff:
            {
                if (_musicSubscribers.TryRemove(header.SenderPetId, out _))
                {
                    MusicFollowStateChanged?.Invoke(this, new MusicFollowStateChangedEventArgs(header.SenderPetId, false));
                }

                break;
            }
        }

        return new AckPayload { AckMessageId = header.MessageId, Accepted = true };
    }

    private async Task<AckPayload?> SendControlPacketAsync<TPayload>(
        string remoteIp,
        int remotePort,
        PacketType packetType,
        TPayload payload,
        string? pairSecretBase64,
        CancellationToken ct)
    {
        try
        {
            var payloadJson = JsonSerializer.Serialize(payload, ProtocolJson.PacketOptions);
            var header = new PacketHeader
            {
                ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                AppVersion = _localPresence.AppVersion,
                PacketType = packetType,
                SenderPetId = _localPetId,
                MessageId = Guid.NewGuid().ToString("N"),
                TimestampUtc = DateTimeOffset.UtcNow
            };

            if (!string.IsNullOrWhiteSpace(pairSecretBase64))
            {
                var canonical = PacketSigner.BuildControlCanonicalString(header, payloadJson);
                header.SignatureBase64 = PacketSigner.ComputeSignature(canonical, pairSecretBase64);
            }

            var envelope = new ControlPacketEnvelope
            {
                Header = header,
                PayloadJson = payloadJson
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            using var client = new TcpClient();
            await client.ConnectAsync(remoteIp, remotePort, timeoutCts.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();

            await WriteFramedJsonAsync(stream, envelope, timeoutCts.Token).ConfigureAwait(false);
            var ackEnvelope = await ReadFramedJsonAsync<ControlPacketEnvelope>(stream, timeoutCts.Token).ConfigureAwait(false);
            if (ackEnvelope is null || ackEnvelope.Header.PacketType != PacketType.Ack)
            {
                return null;
            }

            return JsonSerializer.Deserialize<AckPayload>(ackEnvelope.PayloadJson, ProtocolJson.PacketOptions);
        }
        catch (Exception ex)
        {
            TransportError?.Invoke(this, new TransportErrorEventArgs($"Send:{packetType}", ex.Message, ex));
            return null;
        }
    }

    private bool TryResolveFriendEndpoint(FriendEntry friend, out string ip, out int port)
    {
        // 최신 탐색 캐시가 있으면 우선 사용 (DHCP IP 변경 자동 반영)
        if (_peers.TryGetValue(friend.PetId, out var livePeer) && !livePeer.RequiresUpdate && !string.IsNullOrWhiteSpace(livePeer.IpAddress))
        {
            ip = livePeer.IpAddress;
            port = livePeer.TcpPort;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(friend.LastKnownIp))
        {
            ip = friend.LastKnownIp;
            port = friend.LastKnownTcpPort > 0 ? friend.LastKnownTcpPort : _tcpControlPort;
            return true;
        }

        ip = string.Empty;
        port = 0;
        return false;
    }

    private void PruneStalePeers()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(ProtocolConstants.PeerOfflineTimeoutSeconds);
        var removed = false;

        foreach (var kv in _peers)
        {
            if (kv.Value.LastSeenUtc < cutoff)
            {
                if (_peers.TryRemove(kv.Key, out _))
                {
                    _musicSubscribers.TryRemove(kv.Key, out _);
                    removed = true;
                }
            }
        }

        if (removed)
        {
            RaisePeersChanged();
        }
    }

    private void RaisePeersChanged()
    {
        PeersChanged?.Invoke(this, GetDiscoveredPeers());
    }

    private static AckPayload RejectAck(string messageId, string errorCode) => new()
    {
        AckMessageId = messageId,
        Accepted = false,
        ErrorCode = errorCode
    };

    private static UdpClient CreateReusableUdpClient(int port, bool enableBroadcast)
    {
        var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.ExclusiveAddressUse = false;
        udp.EnableBroadcast = enableBroadcast;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        return udp;
    }

    private static async Task WriteFramedJsonAsync<T>(NetworkStream stream, T value, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, ProtocolJson.PacketOptions);
        var lengthPrefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, body.Length);
        await stream.WriteAsync(lengthPrefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<T?> ReadFramedJsonAsync<T>(NetworkStream stream, CancellationToken ct)
    {
        var lengthPrefix = new byte[4];
        if (!await ReadExactAsync(stream, lengthPrefix, ct).ConfigureAwait(false))
        {
            return default;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(lengthPrefix);
        if (length <= 0 || length > 65536)
        {
            return default;
        }

        var body = new byte[length];
        if (!await ReadExactAsync(stream, body, ct).ConfigureAwait(false))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(body, ProtocolJson.PacketOptions);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
