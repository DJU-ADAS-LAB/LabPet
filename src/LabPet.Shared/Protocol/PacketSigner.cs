using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LabPet.Shared.Protocol;

/// <summary>
/// 친구 전용 패킷(TCP 제어 패킷 및 UDP 오디오 프레임)의 HMAC-SHA256 서명 생성 및 검증 유틸리티.
/// </summary>
public static class PacketSigner
{
    /// <summary>32바이트 난수 키(Base64)를 생성한다. 신규 친구 키는 ECDH로 합의한다.</summary>
    public static string GeneratePairSecretBase64()
    {
        Span<byte> secret = stackalloc byte[ProtocolConstants.PairSecretByteLength];
        RandomNumberGenerator.Fill(secret);
        return Convert.ToBase64String(secret);
    }

    /// <summary>TCP 제어 패킷의 정규화된 서명 문자열을 생성한다.</summary>
    public static string BuildControlCanonicalString(PacketHeader header, string payloadJson)
        => JsonSerializer.Serialize(new object?[]
        {
            header.ProtocolVersion, (int)header.PacketType, header.SenderPetId, header.MessageId,
            header.TimestampUtc.ToUnixTimeMilliseconds(), header.AppVersion, payloadJson
        });

    /// <summary>UDP 오디오 프레임의 정규화된 서명 문자열을 생성한다.</summary>
    public static string BuildMusicFrameCanonicalString(MusicFramePacket packet)
        => FormattableString.Invariant(
            $"{packet.ProtocolVersion}|{packet.SenderPetId}|{packet.SubscriptionId}|{packet.Sequence}|{packet.Bass:R}|{packet.Mid:R}|{packet.Treble:R}|{(packet.IsBeat ? 1 : 0)}");

    /// <summary>공유 비밀키로 HMAC-SHA256 서명(Base64)을 계산한다.</summary>
    public static string ComputeSignature(string canonicalData, string pairSecretBase64)
    {
        var keyBytes = Convert.FromBase64String(pairSecretBase64);
        try
        {
            var hash = HMACSHA256.HashData(keyBytes, Encoding.UTF8.GetBytes(canonicalData));
            return Convert.ToBase64String(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    /// <summary>상수 시간 비교로 HMAC-SHA256 서명을 검증한다.</summary>
    public static bool VerifySignature(string canonicalData, string? signatureBase64, string pairSecretBase64)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64) || string.IsNullOrWhiteSpace(pairSecretBase64))
        {
            return false;
        }

        try
        {
            var expected = Convert.FromBase64String(ComputeSignature(canonicalData, pairSecretBase64));
            var actual = Convert.FromBase64String(signatureBase64);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
