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
