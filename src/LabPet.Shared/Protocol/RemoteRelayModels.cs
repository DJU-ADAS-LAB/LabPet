using System.Security.Cryptography;
using System.Text;
using LabPet.Shared.Models;

namespace LabPet.Shared.Protocol;

/// <summary>
/// GCP SignalR 서버 및 하이브리드 클라이언트가 공유하는 경로·허브 메서드 이름 상수.
/// </summary>
public static class SocialHubConstants
{
    public const string HubPath = "/hubs/social";
    public const string HealthPath = "/health";
    public const string AuthChallengePath = "/api/auth/challenge";
    public const string AuthTokenPath = "/api/auth/token";

    // Server Hub 메서드
    public const string RegisterPresenceMethod = "RegisterPresenceAsync";
    public const string UpdatePresenceMethod = "UpdatePresenceAsync";
    public const string SyncKnownFriendsMethod = "SyncKnownFriendsAsync";
    public const string RelayControlPacketMethod = "RelayControlPacketAsync";
    public const string RelayMusicFrameMethod = "RelayMusicFrameAsync";

    // Client 콜백 메서드
    public const string DeliverControlPacketClientMethod = "DeliverControlPacket";
    public const string ReceiveMusicFrameClientMethod = "ReceiveMusicFrame";
    public const string ReceiveFriendsSnapshotClientMethod = "ReceiveFriendsSnapshot";
    public const string ReceiveFriendPresenceChangedClientMethod = "ReceiveFriendPresenceChanged";
    public const string ReceiveFriendOfflineClientMethod = "ReceiveFriendOffline";
}

/// <summary>
/// 기기 ECDSA 공개키 소유를 증명하기 위한 챌린지 요청.
/// </summary>
public sealed class AuthChallengeRequest
{
    public string PetId { get; set; } = string.Empty;
    public string DevicePublicKeyBase64 { get; set; } = string.Empty;
}

/// <summary>
/// 서버가 발급하는 1회용 논스 챌린지 응답.
/// </summary>
public sealed class AuthChallengeResponse
{
    public string Nonce { get; set; } = string.Empty;
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

/// <summary>
/// 챌린지 서명을 포함한 Bearer 액세스 토큰 발급 요청.
/// </summary>
public sealed class AuthTokenRequest
{
    public string PetId { get; set; } = string.Empty;
    public string FriendCode { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string PetName { get; set; } = string.Empty;
    public string DevicePublicKeyBase64 { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public DateTimeOffset IssuedAtUtc { get; set; }
    public string SignatureBase64 { get; set; } = string.Empty;
}

/// <summary>
/// 발급된 단기 Bearer 액세스 토큰 응답.
/// </summary>
public sealed class AuthTokenResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string VerifiedPetId { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

/// <summary>
/// 클라이언트 → 서버 실시간 온라인 상태 등록·갱신 페이로드.
/// </summary>
public sealed class RemotePresenceUpdate
{
    public string FriendCode { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string PetName { get; set; } = string.Empty;
    public PetAppearance Appearance { get; set; } = new();
    public int Level { get; set; } = 1;
    public bool IsMusicActive { get; set; }
    public bool IsSleeping { get; set; }
    public string AppVersion { get; set; } = "0.1.0";
    public int ProtocolVersion { get; set; } = ProtocolConstants.CurrentProtocolVersion;
    public List<string> KnownFriendPetIds { get; set; } = [];
}

/// <summary>
/// 서버 → 클라이언트 상호 승인된 온라인 친구 프레즌스 DTO.
/// </summary>
public sealed class RemoteFriendStateDto
{
    public string PetId { get; set; } = string.Empty;
    public string FriendCode { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string PetName { get; set; } = string.Empty;
    public PetAppearance Appearance { get; set; } = new();
    public int Level { get; set; } = 1;
    public bool IsMusicActive { get; set; }
    public bool IsSleeping { get; set; }
    public string AppVersion { get; set; } = "0.1.0";
    public int ProtocolVersion { get; set; } = ProtocolConstants.CurrentProtocolVersion;
}

/// <summary>
/// SignalR 제어 패킷 E2EE 봉투 중계 요청.
/// </summary>
public sealed record RelayControlEnvelopeRequest(string TargetPetId, ControlPacketEnvelope Envelope);

/// <summary>
/// SignalR 서버가 수신자 클라이언트에게 전달하는 제어 패킷 봉투.
/// </summary>
public sealed record RelayControlEnvelopeDelivery(string SenderPetId, ControlPacketEnvelope Envelope);

/// <summary>
/// SignalR 제어 패킷 중계 결과(수신자 오프라인 시 Delivered=false).
/// </summary>
public sealed record RelayDeliveryResult(bool Delivered, string? ErrorCode = null, ControlPacketEnvelope? AckEnvelope = null);

/// <summary>
/// 기기 ECDSA P-256 챌린지-응답 인증 서명/검증 유틸리티.
/// </summary>
public static class RemoteAuthSigner
{
    public static bool IsValidDevicePublicKey(string? publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64) || publicKeyBase64.Length > 256)
        {
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            var bytes = Convert.FromBase64String(publicKeyBase64);
            ecdsa.ImportSubjectPublicKeyInfo(bytes, out var consumed);
            return consumed == bytes.Length
                && ecdsa.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    public static string ComputeKeyThumbprint(string publicKeyBase64)
    {
        var bytes = Convert.FromBase64String(publicKeyBase64);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16];
    }

    public static string BuildChallengeCanonicalString(
        string nonce,
        string petId,
        string devicePublicKeyBase64,
        DateTimeOffset issuedAtUtc)
        => $"LabPet-Auth-v1|{nonce}|{petId}|{devicePublicKeyBase64}|{issuedAtUtc.ToUnixTimeMilliseconds()}";

    public static string SignChallenge(ECDsa privateKey, string canonicalData)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        var signature = privateKey.SignData(
            Encoding.UTF8.GetBytes(canonicalData),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(signature);
    }

    public static bool VerifyChallengeSignature(string publicKeyBase64, string canonicalData, string signatureBase64)
    {
        if (!IsValidDevicePublicKey(publicKeyBase64) || string.IsNullOrWhiteSpace(signatureBase64))
        {
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            var sigBytes = Convert.FromBase64String(signatureBase64);
            return ecdsa.VerifyData(
                Encoding.UTF8.GetBytes(canonicalData),
                sigBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}
