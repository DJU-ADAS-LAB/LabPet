using System.Text.Json;
using LabPet.Shared.Protocol;
using Xunit;

namespace LabPet.Tests;

public sealed class SnowballProtocolTests
{
    [Fact]
    public void GreetingPreservesLegacyWirePayload()
    {
        var json = JsonSerializer.Serialize(new PetInteractionPayload(), ProtocolJson.PacketOptions);
        Assert.Equal("{\"interactionType\":\"pet\"}", json);
        var payload = JsonSerializer.Deserialize<PetInteractionPayload>(json, ProtocolJson.PacketOptions)!;
        Assert.Equal(PetInteractionPayload.PetType, payload.InteractionType);
        Assert.Null(payload.EventId);
    }

    [Fact]
    public void SnowballPreservesLogicalEventIdAcrossSerialization()
    {
        var eventId = Guid.NewGuid().ToString("N");
        var payload = new PetInteractionPayload
        {
            InteractionType = PetInteractionPayload.SnowballType,
            EventId = eventId
        };
        for (var route = 0; route < 2; route++)
        {
            var json = JsonSerializer.Serialize(payload, ProtocolJson.PacketOptions);
            payload = JsonSerializer.Deserialize<PetInteractionPayload>(json, ProtocolJson.PacketOptions)!;
            Assert.Equal("snowball", payload.InteractionType);
            Assert.Equal(eventId, payload.EventId);
        }
    }

    [Fact]
    public void UnknownInteractionIsNotConvertedIntoGreeting()
    {
        var payload = JsonSerializer.Deserialize<PetInteractionPayload>(
            "{\"interactionType\":\"future-event\",\"futureField\":true}", ProtocolJson.PacketOptions)!;
        Assert.Equal("future-event", payload.InteractionType);
        Assert.Null(payload.EventId);
    }
}
