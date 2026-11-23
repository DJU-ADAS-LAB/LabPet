using System.Security.Cryptography;
using System.Text.Json;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using Xunit;

namespace LabPet.Tests;

public sealed class ProtocolAndSignerTests
{
    [Fact]
    public void PairingKeyAgreement_BindsTheRequestPeerRolesAndTranscript()
    {
        using var requester = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var acceptor = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var first = Convert.ToBase64String(requester.ExportSubjectPublicKeyInfo());
        var second = Convert.ToBase64String(acceptor.ExportSubjectPublicKeyInfo());
        var requestId = Guid.NewGuid().ToString("N");
        var firstKeys = PacketCrypto.DerivePairingKeys(requester, second, requestId, "a", "b", first, second, "request");
        var secondKeys = PacketCrypto.DerivePairingKeys(acceptor, first, requestId, "a", "b", first, second, "request");
        Assert.NotNull(firstKeys);
        Assert.Equal(firstKeys, secondKeys);
        Assert.NotEqual(firstKeys.Value.ConfirmationKey, firstKeys.Value.FriendKey);
        Assert.NotEqual(firstKeys, PacketCrypto.DerivePairingKeys(acceptor, first, requestId, "a", "b", first, second, "different"));
        Assert.NotEqual(firstKeys, PacketCrypto.DerivePairingKeys(acceptor, first, requestId, "a", "c", first, second, "request"));
        Assert.NotEqual(firstKeys, PacketCrypto.DerivePairingKeys(acceptor, first, Guid.NewGuid().ToString("N"), "a", "b", first, second, "request"));
        Assert.Null(PacketCrypto.DerivePairingKeys(requester, "not-a-key", requestId, "a", "b", first, second, "request"));
        var serialized = JsonSerializer.Serialize(new FriendAcceptPayload { RequestId = requestId, PublicKey = second }, ProtocolJson.PacketOptions);
        Assert.DoesNotContain(firstKeys.Value.ConfirmationKey, serialized);
        Assert.DoesNotContain(firstKeys.Value.FriendKey, serialized);
    }

    [Fact]
    public void EvaluateCompatibility_ReturnsExpectedState()
    {
        Assert.Equal(
            ProtocolCompatibility.Compatible,
            ProtocolConstants.EvaluateCompatibility(ProtocolConstants.CurrentProtocolVersion));

        Assert.Equal(
            ProtocolCompatibility.PeerOutdated,
            ProtocolConstants.EvaluateCompatibility(ProtocolConstants.MinSupportedProtocolVersion - 1));
        Assert.Equal(ProtocolCompatibility.PeerOutdated, ProtocolConstants.EvaluateCompatibility(1));

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

        var nonceBytes = Convert.FromBase64String(nonce1);
        nonceBytes[0] ^= 1;
        envelope.NonceBase64 = Convert.ToBase64String(nonceBytes);
        Assert.False(PacketCrypto.TryDecryptControlPayload(envelope, secret, out _));
        envelope.NonceBase64 = nonce1;
        var tagBytes = Convert.FromBase64String(tag1);
        tagBytes[0] ^= 1;
        envelope.AuthTagBase64 = Convert.ToBase64String(tagBytes);
        Assert.False(PacketCrypto.TryDecryptControlPayload(envelope, secret, out _));
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

    [Fact]
    public void PresencePayloads_SerializeIsSleepingAndDefaultFalseForLegacyJson()
    {
        var hello = new DiscoveryHelloPacket
        {
            PetId = "pet-sleep-1",
            PetName = "모찌",
            IsSleeping = true
        };
        var helloJson = JsonSerializer.Serialize(hello, ProtocolJson.PacketOptions);
        var roundTripHello = JsonSerializer.Deserialize<DiscoveryHelloPacket>(helloJson, ProtocolJson.PacketOptions);
        Assert.NotNull(roundTripHello);
        Assert.True(roundTripHello!.IsSleeping);

        var presence = new FriendPresencePayload
        {
            OwnerName = "승우",
            PetName = "모찌",
            IsSleeping = true,
            TcpPort = ProtocolConstants.TcpControlPort,
            AppVersion = "0.2.2"
        };
        var presenceJson = JsonSerializer.Serialize(presence, ProtocolJson.PacketOptions);
        var roundTripPresence = JsonSerializer.Deserialize<FriendPresencePayload>(presenceJson, ProtocolJson.PacketOptions);
        Assert.NotNull(roundTripPresence);
        Assert.True(roundTripPresence!.IsSleeping);

        // 구버전 피어(IsSleeping 필드 미포함 JSON)는 기본값 false로 역직렬화되어 호환 유지
        const string legacyHelloJson = """{"protocolVersion":3,"petId":"legacy-1","petName":"구버전","tcpPort":45680}""";
        const string legacyPresenceJson = """{"ownerName":"민수","petName":"냥이","tcpPort":45680,"appVersion":"0.2.1"}""";
        var parsedLegacyHello = JsonSerializer.Deserialize<DiscoveryHelloPacket>(legacyHelloJson, ProtocolJson.PacketOptions);
        var parsedLegacyPresence = JsonSerializer.Deserialize<FriendPresencePayload>(legacyPresenceJson, ProtocolJson.PacketOptions);
        Assert.NotNull(parsedLegacyHello);
        Assert.NotNull(parsedLegacyPresence);
        Assert.False(parsedLegacyHello!.IsSleeping);
        Assert.False(parsedLegacyPresence!.IsSleeping);
    }
}
