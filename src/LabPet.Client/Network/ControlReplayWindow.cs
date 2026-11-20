using LabPet.Shared.Protocol;

namespace LabPet.Client.Network;

internal sealed class ControlReplayWindow
{
    internal static readonly TimeSpan TimestampTolerance = TimeSpan.FromMinutes(2);
    internal const int Capacity = 1024;
    private readonly Dictionary<string, Entry> _requests = new(StringComparer.Ordinal);

    private sealed record Entry(string Fingerprint, DateTimeOffset ExpiresAt)
    {
        public AckPayload? Result { get; set; }
    }

    public static bool IsFresh(DateTimeOffset timestamp, DateTimeOffset now)
        => timestamp >= now - TimestampTolerance && timestamp <= now + TimestampTolerance;

    public AckPayload Execute(PacketHeader header, string fingerprint, DateTimeOffset now, Func<AckPayload> handler)
    {
        lock (_requests)
        {
            foreach (var expired in _requests.Where(entry => entry.Value.ExpiresAt < now).ToArray())
            {
                _requests.Remove(expired.Key);
            }

            if (_requests.TryGetValue(header.MessageId, out var previous))
            {
                return previous.Fingerprint == fingerprint && previous.Result is not null
                    ? previous.Result
                    : Reject(header.MessageId, "DUPLICATE_MESSAGE");
            }
            if (!IsFresh(header.TimestampUtc, now) || _requests.Count >= Capacity)
            {
                return Reject(header.MessageId, "REPLAY_WINDOW_REJECTED");
            }

            var entry = new Entry(fingerprint, header.TimestampUtc + TimestampTolerance);
            _requests.Add(header.MessageId, entry);
            // 처리 중 예외가 나도 같은 요청을 다시 실행하지 않는다.
            entry.Result = Reject(header.MessageId, "PROCESSING_FAILED");
            entry.Result = handler();
            return entry.Result;
        }
    }

    private static AckPayload Reject(string id, string reason) =>
        new() { AckMessageId = id, Accepted = false, ErrorCode = reason };
}
