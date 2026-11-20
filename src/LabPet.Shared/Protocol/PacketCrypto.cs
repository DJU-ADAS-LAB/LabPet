using System.Security.Cryptography;
using System.Text;

namespace LabPet.Shared.Protocol;

/// <summary>
/// 친구 전용 TCP 제어 패킷 페이로드의 AES-256-GCM 인증 암호화(AEAD) 유틸리티.
/// 공유 비밀키(PairSecretBase64)에서 HKDF-SHA256으로 암호화 전용 키를 도메인 분리 파생하고,
/// 패킷 헤더 메타데이터를 AAD(Associated Authenticated Data)로 바인딩한다.
/// </summary>
public static class PacketCrypto
{
    private static readonly byte[] AesKeyDerivationInfo = Encoding.UTF8.GetBytes("LabPet-v2-AES256GCM-ControlPayload");

    /// <summary>
    /// 친구 전용 제어 패킷의 평문 JSON 페이로드를 AES-256-GCM으로 암호화한다.
    /// </summary>
    public static (string CiphertextBase64, string NonceBase64, string AuthTagBase64) EncryptControlPayload(
        PacketHeader header,
        string plaintextPayloadJson,
        string pairSecretBase64)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (string.IsNullOrWhiteSpace(pairSecretBase64))
        {
            throw new ArgumentException("공유 비밀키가 비어 있습니다.", nameof(pairSecretBase64));
        }

        var ikm = Convert.FromBase64String(pairSecretBase64);
        var key = new byte[ProtocolConstants.AesGcmKeyByteLength];
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, key, salt: ReadOnlySpan<byte>.Empty, info: AesKeyDerivationInfo);

            var nonce = new byte[ProtocolConstants.AesGcmNonceByteLength];
            RandomNumberGenerator.Fill(nonce);

            var plaintextBytes = Encoding.UTF8.GetBytes(plaintextPayloadJson);
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[ProtocolConstants.AesGcmTagByteLength];
            var aad = BuildControlAadBytes(header);

            using var aes = new AesGcm(key, ProtocolConstants.AesGcmTagByteLength);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, aad);

            return (
                Convert.ToBase64String(ciphertext),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ikm);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// 암호화된 제어 패킷 봉투를 복호화해 평문 JSON 페이로드를 복원한다.
    /// 헤더 변조, 태그 불일치, 잘못된 키인 경우 false를 반환한다.
    /// </summary>
    public static bool TryDecryptControlPayload(
        ControlPacketEnvelope envelope,
        string pairSecretBase64,
        out string plaintextPayloadJson)
    {
        plaintextPayloadJson = string.Empty;

        if (envelope?.Header is null ||
            !envelope.IsEncrypted ||
            string.IsNullOrWhiteSpace(pairSecretBase64) ||
            string.IsNullOrWhiteSpace(envelope.NonceBase64) ||
            string.IsNullOrWhiteSpace(envelope.AuthTagBase64))
        {
            return false;
        }

        byte[]? ikm = null;
        var key = new byte[ProtocolConstants.AesGcmKeyByteLength];
        try
        {
            ikm = Convert.FromBase64String(pairSecretBase64);
            var nonce = Convert.FromBase64String(envelope.NonceBase64);
            var tag = Convert.FromBase64String(envelope.AuthTagBase64);
            var ciphertext = Convert.FromBase64String(envelope.PayloadJson ?? string.Empty);

            if (nonce.Length != ProtocolConstants.AesGcmNonceByteLength ||
                tag.Length != ProtocolConstants.AesGcmTagByteLength)
            {
                return false;
            }

            HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, key, salt: ReadOnlySpan<byte>.Empty, info: AesKeyDerivationInfo);

            var plaintext = new byte[ciphertext.Length];
            var aad = BuildControlAadBytes(envelope.Header);

            using var aes = new AesGcm(key, ProtocolConstants.AesGcmTagByteLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);

            plaintextPayloadJson = Encoding.UTF8.GetString(plaintext);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (ikm is not null)
            {
                CryptographicOperations.ZeroMemory(ikm);
            }

            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>암호화된 제어 패킷 봉투의 HMAC 서명 대상 페이로드 문자열을 조합한다.</summary>
    public static string BuildEncryptedPayloadSignatureInput(ControlPacketEnvelope envelope)
        => $"{envelope.NonceBase64}.{envelope.PayloadJson}.{envelope.AuthTagBase64}";

    private static byte[] BuildControlAadBytes(PacketHeader header)
        => Encoding.UTF8.GetBytes(
            $"{header.ProtocolVersion}|{(int)header.PacketType}|{header.SenderPetId}|{header.MessageId}|{header.TimestampUtc.ToUnixTimeMilliseconds()}");
}
