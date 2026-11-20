using System.Text.Json;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using Xunit;

namespace LabPet.Tests;

public sealed class ProtocolAndSignerTests
{
    [Fact]
    public void EvaluateCompatibility_ReturnsExpectedState()
    {
        Assert.Equal(
            ProtocolCompatibility.Compatible,
            ProtocolConstants.EvaluateCompatibility(ProtocolConstants.CurrentProtocolVersion));

        Assert.Equal(
            ProtocolCompatibility.PeerOutdated,
            ProtocolConstants.EvaluateCompatibility(ProtocolConstants.MinSupportedProtocolVersion - 1));

        Assert.Equal(
            ProtocolCompatibility.LocalOutdated,
            ProtocolConstants.EvaluateCompatibility(ProtocolConstants.CurrentProtocolVersion + 1));
    }

    [Fact]
    public void PacketSigner_ControlPacket_ValidatesAndRejectsTampering()
    {
        var secret = PacketSigner.GeneratePairSecretBase64();
        Assert.Equal(ProtocolConstants.PairSecretByteLength, Convert.FromBase64String(secret).Length);

        var header = new PacketHeader
        {
            PacketType = PacketType.BubbleMessage,
            SenderPetId = Guid.NewGuid().ToString("D")
        };
        const string payload = """{"text":"안녕 모찌!"}""";

        var canonical = PacketSigner.BuildControlCanonicalString(header, payload);
        var signature = PacketSigner.ComputeSignature(canonical, secret);

        Assert.True(PacketSigner.VerifySignature(canonical, signature, secret));

        var tamperedCanonical = PacketSigner.BuildControlCanonicalString(header, """{"text":"변조됨"}""");
        Assert.False(PacketSigner.VerifySignature(tamperedCanonical, signature, secret));

        var otherSecret = PacketSigner.GeneratePairSecretBase64();
        Assert.False(PacketSigner.VerifySignature(canonical, signature, otherSecret));
    }

    [Fact]
    public void PacketCrypto_EncryptsDecryptsAndRejectsTampering()
    {
        var secret = PacketSigner.GeneratePairSecretBase64();
        var header = new PacketHeader
        {
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            PacketType = PacketType.BubbleMessage,
            SenderPetId = Guid.NewGuid().ToString("D")
        };
        const string plaintext = """{"text":"비밀 말풍선 테스트 1234"}""";

        var (cipher1, nonce1, tag1) = PacketCrypto.EncryptControlPayload(header, plaintext, secret);
        var (cipher2, nonce2, _) = PacketCrypto.EncryptControlPayload(header, plaintext, secret);

        Assert.DoesNotContain("비밀 말풍선", cipher1);
        Assert.NotEqual(nonce1, nonce2);
        Assert.NotEqual(cipher1, cipher2);

        var envelope = new ControlPacketEnvelope
        {
            Header = header,
            PayloadJson = cipher1,
            IsEncrypted = true,
            NonceBase64 = nonce1,
            AuthTagBase64 = tag1
        };

        Assert.True(PacketCrypto.TryDecryptControlPayload(envelope, secret, out var decrypted));
        Assert.Equal(plaintext, decrypted);

        // 다른 키로 복호화 시도 -> 실패
        var wrongSecret = PacketSigner.GeneratePairSecretBase64();
        Assert.False(PacketCrypto.TryDecryptControlPayload(envelope, wrongSecret, out _));

        // 헤더(AAD) 변조 -> 실패
        var tamperedHeaderEnvelope = new ControlPacketEnvelope
        {
            Header = new PacketHeader
            {
                ProtocolVersion = header.ProtocolVersion,
                PacketType = header.PacketType,
                SenderPetId = Guid.NewGuid().ToString("D"),
                MessageId = header.MessageId,
                TimestampUtc = header.TimestampUtc
            },
            PayloadJson = cipher1,
            IsEncrypted = true,
            NonceBase64 = nonce1,
            AuthTagBase64 = tag1
        };
        Assert.False(PacketCrypto.TryDecryptControlPayload(tamperedHeaderEnvelope, secret, out _));

        // 암호문 1비트 변조 -> 실패
        var cipherBytes = Convert.FromBase64String(cipher1);
        cipherBytes[0] ^= 0xFF;
        var tamperedCipherEnvelope = new ControlPacketEnvelope
        {
            Header = header,
            PayloadJson = Convert.ToBase64String(cipherBytes),
            IsEncrypted = true,
            NonceBase64 = nonce1,
            AuthTagBase64 = tag1
        };
        Assert.False(PacketCrypto.TryDecryptControlPayload(tamperedCipherEnvelope, secret, out _));
    }

    [Fact]
    public void ProtocolJson_IgnoresUnknownFutureFieldsAndAppliesDefaults()
    {
        const string futureJson = """
        {
          "schemaVersion": 1,
          "petId": "11111111-2222-3333-4444-555555555555",
          "petName": "미래모찌",
          "futureUnknownField": "should-be-ignored",
          "appearance": {
            "speciesId": "chibi_cat",
            "futureShaderFlag": true
          }
        }
        """;

        var profile = JsonSerializer.Deserialize<PetProfile>(futureJson, ProtocolJson.StorageOptions);

        Assert.NotNull(profile);
        Assert.Equal("미래모찌", profile!.PetName);
        Assert.Equal(PetSpeciesIds.ChibiCat, profile.Appearance.SpeciesId);
        // 누락된 필드는 기본값으로 안전 폴백
        Assert.Equal("default", profile.Appearance.ExpressionId);
        Assert.Equal("none", profile.Appearance.HeadAccessoryId);
    }
}
