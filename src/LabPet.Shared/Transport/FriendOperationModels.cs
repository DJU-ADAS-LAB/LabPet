using LabPet.Shared.Models;

namespace LabPet.Shared.Transport;

/// <summary>
/// 친구 신청·수락 처리의 단계별 진행 위치.
/// </summary>
public enum FriendOperationStage
{
    Validation = 0,
    Sending = 1,
    RemoteReceipt = 2,
    AckVerification = 3,
    LocalPersistence = 4,
    Completed = 5
}

/// <summary>
/// LAN P2P, GCP SignalR 서버·클라이언트 및 로컬 저장소가 공유하는 표준 친구 신청/수락 오류 코드.
/// </summary>
public static class FriendOperationErrorCodes
{
    public const string AlreadyFriends = "ALREADY_FRIENDS";
    public const string DuplicatePendingRequest = "DUPLICATE_PENDING_REQUEST";
    public const string RequestInProgress = "REQUEST_IN_PROGRESS";
    public const string PendingCapacityFull = "PENDING_CAPACITY_FULL";
    public const string ProtocolMismatch = "PROTOCOL_MISMATCH";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string InvalidPairingRequest = "INVALID_PAIRING_REQUEST";
    public const string InvalidPairingEnvelope = "INVALID_PAIRING_ENVELOPE";
    public const string PairingRequestRejected = "PAIRING_REQUEST_REJECTED";
    public const string PairingRequestUnknown = "PAIRING_REQUEST_UNKNOWN";
    public const string PairingRequestExpired = "PAIRING_REQUEST_EXPIRED";
    public const string PairingAlreadyCompleted = "PAIRING_ALREADY_COMPLETED";
    public const string PairingConfirmationFailed = "PAIRING_CONFIRMATION_FAILED";
    public const string InvalidAckSignature = "INVALID_ACK_SIGNATURE";
    public const string RecipientOffline = "RECIPIENT_OFFLINE";
    public const string DeliveryFailedOrTimeout = "DELIVERY_FAILED_OR_TIMEOUT";
    public const string RateLimited = "RATE_LIMITED";
    public const string StaleTimestamp = "STALE_TIMESTAMP";
    public const string DuplicateMessage = "DUPLICATE_MESSAGE";
    public const string SenderMismatch = "SENDER_MISMATCH";
    public const string NetworkError = "NETWORK_ERROR";
    public const string GcpNotConnected = "GCP_NOT_CONNECTED";
    public const string AuthFailed = "AUTH_FAILED";
    public const string PublicKeyMismatch = "PUBLIC_KEY_MISMATCH";
    public const string InvalidSignature = "INVALID_SIGNATURE";
    public const string InvalidOrExpiredChallenge = "INVALID_OR_EXPIRED_CHALLENGE";
    public const string LocalStorageFailed = "LOCAL_STORAGE_FAILED";
    public const string Cancelled = "CANCELLED";
    public const string PomodoroFocusBlocked = "POMODORO_FOCUS";
    public const string PomodoroFocusMessage = "뽀모도로 타이머로 집중 중!";

    public static bool IsTransient(string? code) => code switch
    {
        RecipientOffline => true,
        DeliveryFailedOrTimeout => true,
        RateLimited => true,
        NetworkError => true,
        GcpNotConnected => true,
        LocalStorageFailed => true,
        InvalidOrExpiredChallenge => true,
        _ => false
    };

    public static bool IsExpiredOrUnknown(string? code) => code switch
    {
        PairingRequestExpired => true,
        PairingRequestUnknown => true,
        _ => false
    };

    public static bool IsAuthError(string? code) => code switch
    {
        AuthFailed => true,
        PublicKeyMismatch => true,
        InvalidSignature => true,
        InvalidOrExpiredChallenge => true,
        SenderMismatch => true,
        InvalidAckSignature => true,
        PairingConfirmationFailed => true,
        _ => false
    };
}

/// <summary>
/// 친구 신청·수락의 단계별 결과, 오류 코드, 일시적 실패 여부 및 사용 경로를 전달하는 표준 결과 레코드.
/// </summary>
public sealed record FriendOperationResult(
    bool Succeeded,
    FriendOperationStage Stage,
    string? ErrorCode = null,
    bool IsTransient = false,
    PeerTransportKind? Route = null,
    string? RequestId = null,
    FriendAcceptedEventArgs? AcceptedFriend = null,
    string? Detail = null)
{
    public static FriendOperationResult Ok(
        FriendOperationStage stage,
        PeerTransportKind? route = null,
        string? requestId = null,
        FriendAcceptedEventArgs? acceptedFriend = null,
        string? detail = null)
        => new(true, stage, null, false, route, requestId, acceptedFriend, detail);

    public static FriendOperationResult Fail(
        FriendOperationStage stage,
        string errorCode,
        bool isTransient,
        PeerTransportKind? route = null,
        string? requestId = null,
        string? detail = null)
        => new(false, stage, errorCode, isTransient, route, requestId, null, detail);
}

/// <summary>
/// 일반 제어 패킷(메시지·반응·인사·눈덩이·노래 따라하기·게임 이벤트)의 전송 결과와 거절 오류 코드.
/// </summary>
public readonly record struct TransportSendResult(bool Succeeded, string? ErrorCode = null)
{
    public static TransportSendResult Ok => new(true);
    public bool IsPomodoroFocusBlocked => string.Equals(ErrorCode, FriendOperationErrorCodes.PomodoroFocusBlocked, StringComparison.Ordinal);

    public static TransportSendResult Fail(string? errorCode = null) => new(false, errorCode);
}
