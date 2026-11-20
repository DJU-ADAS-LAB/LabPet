using LabPet.Shared.Models;
using LabPet.Shared.Protocol;

namespace LabPet.Shared.Transport;

/// <summary>
/// 소셜 통신 계층 추상화.
/// UI와 ViewModel은 이 인터페이스만 의존하여 LAN P2P(<c>LanP2pTransport</c>)와
/// 향후 GCP SignalR(<c>GcpSignalRTransport</c>) 또는 <c>HybridTransportRouter</c>를 무중단 교체할 수 있다.
/// </summary>
public interface ISocialTransport : IAsyncDisposable
{
    /// <summary>전송 계층 식별 이름 (예: "LAN-P2P", "GCP-SignalR", "Hybrid").</summary>
    string TransportName { get; }

    /// <summary>전송 계층 실행 여부.</summary>
    bool IsRunning { get; }

    event EventHandler<IReadOnlyList<PeerSnapshot>>? PeersChanged;
    event EventHandler<FriendRequestReceivedEventArgs>? FriendRequestReceived;
    event EventHandler<FriendAcceptedEventArgs>? FriendAccepted;
    event EventHandler<FriendRejectedEventArgs>? FriendRejected;
    event EventHandler<BubbleMessageReceivedEventArgs>? BubbleMessageReceived;
    event EventHandler<QuickReactionReceivedEventArgs>? QuickReactionReceived;
    event EventHandler<PetInteractionReceivedEventArgs>? PetInteractionReceived;
    event EventHandler<MusicFrameReceivedEventArgs>? MusicFrameReceived;
    event EventHandler<MusicFollowStateChangedEventArgs>? MusicFollowStateChanged;
    event EventHandler<GameEventReceivedEventArgs>? GameEventReceived;
    event EventHandler<TransportErrorEventArgs>? TransportError;

    Task StartAsync(PetProfile localProfile, IReadOnlyList<FriendEntry> friends, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>로컬 펫 외형/레벨/음악 활성 상태를 전송 계층에 반영한다.</summary>
    Task UpdateLocalPresenceAsync(LocalPresenceState presence, CancellationToken cancellationToken = default);

    /// <summary>친구 목록 변경 시 전송 계층의 인증 캐시를 갱신한다.</summary>
    void UpdateKnownFriends(IReadOnlyList<FriendEntry> friends);

    /// <summary>현재 관측된 온라인 피어 목록 스냅샷을 반환한다.</summary>
    IReadOnlyList<PeerSnapshot> GetDiscoveredPeers();

    Task<bool> SendFriendRequestAsync(PeerSnapshot target, CancellationToken cancellationToken = default);

    Task<bool> RespondFriendRequestAsync(
        string requestId,
        bool accept,
        CancellationToken cancellationToken = default);

    Task<bool> SendBubbleMessageAsync(FriendEntry friend, string message, CancellationToken cancellationToken = default);
    Task<bool> SendQuickReactionAsync(FriendEntry friend, string reactionId, CancellationToken cancellationToken = default);
    Task<bool> SendPetInteractionAsync(FriendEntry friend, string interactionType, CancellationToken cancellationToken = default);
    Task<bool> SetMusicFollowAsync(FriendEntry hostFriend, bool enable, CancellationToken cancellationToken = default);
    Task<bool> SendGameEventAsync(FriendEntry friend, GameEventPayload payload, CancellationToken cancellationToken = default);

    /// <summary>구독 중인 팔로워들에게만 30Hz 경량 오디오 프레임을 전송한다.</summary>
    ValueTask PublishMusicFrameAsync(MusicFrameData frame, CancellationToken cancellationToken = default);
}

public sealed class FriendRequestReceivedEventArgs(
    string requestId,
    string requesterPetId,
    string requesterOwnerName,
    string requesterName,
    PetAppearance appearance,
    int level,
    string remoteIp,
    int remoteTcpPort) : EventArgs
{
    public string RequestId { get; } = requestId;
    public string RequesterPetId { get; } = requesterPetId;
    public string RequesterOwnerName { get; } = requesterOwnerName;
    public string RequesterName { get; } = requesterName;
    public PetAppearance Appearance { get; } = appearance;
    public int Level { get; } = level;
    public string RemoteIp { get; } = remoteIp;
    public int RemoteTcpPort { get; } = remoteTcpPort;
}

public sealed class FriendAcceptedEventArgs(
    string accepterPetId,
    string accepterOwnerName,
    string accepterName,
    PetAppearance appearance,
    string pairSecretBase64,
    string remoteIp,
    int remoteTcpPort) : EventArgs
{
    public string AccepterPetId { get; } = accepterPetId;
    public string AccepterOwnerName { get; } = accepterOwnerName;
    public string AccepterName { get; } = accepterName;
    public PetAppearance Appearance { get; } = appearance;
    public string PairSecretBase64 { get; } = pairSecretBase64;
    public string RemoteIp { get; } = remoteIp;
    public int RemoteTcpPort { get; } = remoteTcpPort;
}

public sealed class FriendRejectedEventArgs(string responderPetId, string? reason) : EventArgs
{
    public string ResponderPetId { get; } = responderPetId;
    public string? Reason { get; } = reason;
}

public sealed class BubbleMessageReceivedEventArgs(
    string senderPetId,
    string messageId,
    string text,
    DateTimeOffset timestampUtc) : EventArgs
{
    public string SenderPetId { get; } = senderPetId;
    public string MessageId { get; } = messageId;
    public string Text { get; } = text;
    public DateTimeOffset TimestampUtc { get; } = timestampUtc;
}

public sealed class QuickReactionReceivedEventArgs(string senderPetId, string reactionId) : EventArgs
{
    public string SenderPetId { get; } = senderPetId;
    public string ReactionId { get; } = reactionId;
}

public sealed class PetInteractionReceivedEventArgs(string senderPetId, string interactionType) : EventArgs
{
    public string SenderPetId { get; } = senderPetId;
    public string InteractionType { get; } = interactionType;
}

public sealed class MusicFrameReceivedEventArgs(string hostPetId, MusicFrameData frame) : EventArgs
{
    public string HostPetId { get; } = hostPetId;
    public MusicFrameData Frame { get; } = frame;
}

public sealed class MusicFollowStateChangedEventArgs(string peerPetId, bool isFollowing) : EventArgs
{
    public string PeerPetId { get; } = peerPetId;
    public bool IsFollowing { get; } = isFollowing;
}

public sealed class GameEventReceivedEventArgs(string senderPetId, GameEventPayload payload) : EventArgs
{
    public string SenderPetId { get; } = senderPetId;
    public GameEventPayload Payload { get; } = payload;
}

public sealed class TransportErrorEventArgs(string operation, string message, Exception? exception = null) : EventArgs
{
    public string Operation { get; } = operation;
    public string Message { get; } = message;
    public Exception? Exception { get; } = exception;
}
