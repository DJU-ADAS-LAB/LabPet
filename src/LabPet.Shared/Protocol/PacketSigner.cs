using System.Security.Cryptography;
using System.Text;

namespace LabPet.Shared.Protocol;

/// <summary>
/// 친구 전용 패킷(TCP 제어 패킷 및 UDP 오디오 프레임)의 HMAC-SHA256 서명 생성 및 검증 유틸리티.
/// </summary>
public static class PacketSigner
{
    /// <summary>친구 수락 시 발급할 32바이트 난수 공유 비밀키(Base64)를 생성한다.</summary>
    public static string GeneratePairSecretBase64()
    {
        Span<byte> secret = stackalloc byte[ProtocolConstants.PairSecretByteLength];
        RandomNumberGenerator.Fill(secret);
        return Convert.ToBase64String(secret);
    }

    /// <summary>TCP 제어 패킷의 정규화된 서명 문자열을 생성한다.</summary>
    public static string BuildControlCanonicalString(PacketHeader header, string payloadJson)
        => $"{header.ProtocolVersion}|{(int)header.PacketType}|{header.SenderPetId}|{header.MessageId}|{header.TimestampUtc.ToUnixTimeMilliseconds()}|{payloadJson}";

    /// <summary>UDP 오디오 프레임의 정규화된 서명 문자열을 생성한다.</summary>
    public static string BuildMusicFrameCanonicalString(MusicFramePacket packet)
        => $"{packet.ProtocolVersion}|{packet.SenderPetId}|{packet.Sequence}|{packet.Bass:F3}|{packet.Mid:F3}|{packet.Treble:F3}|{(packet.IsBeat ? 1 : 0)}";

    /// <summary>공유 비밀키로 HMAC-SHA256 서명(Base64)을 계산한다.</summary>
    public static string ComputeSignature(string canonicalData, string pairSecretBase64)
    {
        var keyBytes = Convert.FromBase64String(pairSecretBase64);
        var dataBytes = Encoding.UTF8.GetBytes(canonicalData);
        var hash = HMACSHA256.HashData(keyBytes, dataBytes);
        return Convert.ToBase64String(hash);
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
