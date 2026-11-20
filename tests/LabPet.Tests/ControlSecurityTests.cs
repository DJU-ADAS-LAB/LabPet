using System.Text.Json;
using LabPet.Client.Network;
using LabPet.Shared.Protocol;
using Xunit;

namespace LabPet.Tests;

public sealed class ControlSecurityTests
{
    [Fact]
    public void ReplayWindow_ExecutesAnAuthenticatedOperationOnceAndRejectsStaleRequests()
    {
        var window = new ControlReplayWindow();
        var now = DateTimeOffset.UtcNow;
        var header = new PacketHeader { TimestampUtc = now };
        var callbacks = 0;
        AckPayload Apply() => new() { AckMessageId = header.MessageId, Accepted = ++callbacks == 1 };

        Assert.True(window.Execute(header, "내용", now, Apply).Accepted);
        Assert.True(window.Execute(header, "내용", now.AddSeconds(1), Apply).Accepted);
        Assert.False(window.Execute(header, "다른 내용", now, Apply).Accepted);
        Assert.False(window.Execute(header, "내용", now + ControlReplayWindow.TimestampTolerance + TimeSpan.FromTicks(1), Apply).Accepted);
        Assert.Equal(1, callbacks);
    }

    [Fact]
    public void AckValidation_RequiresBoundRequestAndPeerAndKey()
    {
        var key = PacketSigner.GeneratePairSecretBase64();
        var requests = new PacketHeader();
        var ackHeader = new PacketHeader { SenderPetId = "friend", PacketType = PacketType.Ack };
        var payload = JsonSerializer.Serialize(new AckPayload { AckMessageId = requests.MessageId }, ProtocolJson.PacketOptions);
        ackHeader.SignatureBase64 = PacketSigner.ComputeSignature(PacketSigner.BuildControlCanonicalString(ackHeader, payload), key);
        var ack = new ControlPacketEnvelope { Header = ackHeader, PayloadJson = payload };

        Assert.True(LanP2pTransport.ReadVerifiedAck(ack, requests.MessageId, "friend", key)?.Accepted);
        Assert.Null(LanP2pTransport.ReadVerifiedAck(ack, new PacketHeader().MessageId, "friend", key));
        Assert.Null(LanP2pTransport.ReadVerifiedAck(ack, requests.MessageId, "other", key));
        Assert.Null(LanP2pTransport.ReadVerifiedAck(ack, requests.MessageId, "friend", PacketSigner.GeneratePairSecretBase64()));
    }

    [Fact]
    public void MusicWindow_RejectsOldFramesAndFramesFromOtherSubscription()
    {
        var window = new LanP2pTransport.MusicSequenceWindow("current");
        Assert.True(window.Accept(new MusicFramePacket { SubscriptionId = "current", Sequence = uint.MaxValue }));
        Assert.True(window.Accept(new MusicFramePacket { SubscriptionId = "current", Sequence = 0 }));
        Assert.False(window.Accept(new MusicFramePacket { SubscriptionId = "current", Sequence = 0 }));
        Assert.False(window.Accept(new MusicFramePacket { SubscriptionId = "current", Sequence = uint.MaxValue }));
        Assert.False(window.Accept(new MusicFramePacket { SubscriptionId = "old", Sequence = 1 }));
        Assert.True(window.Accept(new MusicFramePacket { SubscriptionId = "current", Sequence = 1 }));
    }
}
