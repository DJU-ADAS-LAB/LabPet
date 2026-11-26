using System.Security.Cryptography;
using LabPet.Server.Options;
using LabPet.Shared.Protocol;

namespace LabPet.Server.Security;

/// <summary>
/// 메모리 기반 1회용 논스 챌린지 저장소 (전체·IP별 상한, TTL, 재사용 불가).
/// </summary>
public sealed class AuthChallengeStore(ServerOptions options)
{
    private sealed record ChallengeEntry(
        string PetId,
        string PublicKeyBase64,
        string? RemoteIp,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc);

    private readonly object _sync = new();
    private readonly Dictionary<string, ChallengeEntry> _challenges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _ipCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxPending = Math.Clamp(options.MaxPendingChallenges, 32, 32_768);
    private readonly int _maxPendingPerIp = Math.Clamp(options.MaxPendingChallengesPerIp, 4, 2048);
    private readonly TimeSpan _lifetime = TimeSpan.FromSeconds(Math.Clamp(options.ChallengeLifetimeSeconds, 15, 300));

    public AuthChallengeResponse? IssueChallenge(string petId, string devicePublicKeyBase64, string? remoteIp = null)
    {
        if (string.IsNullOrWhiteSpace(petId) || petId.Length > 64
            || !RemoteAuthSigner.IsValidDevicePublicKey(devicePublicKeyBase64))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var normalizedIp = string.IsNullOrWhiteSpace(remoteIp) ? null : remoteIp.Trim();

        lock (_sync)
        {
            if (_challenges.Count >= _maxPending
                || (normalizedIp is not null && _ipCounts.TryGetValue(normalizedIp, out var ipCount) && ipCount >= _maxPendingPerIp))
            {
                PruneExpiredLocked(now);
            }

            if (_challenges.Count >= _maxPending)
            {
                return null;
            }

            if (normalizedIp is not null && _ipCounts.TryGetValue(normalizedIp, out var afterPruneIpCount) && afterPruneIpCount >= _maxPendingPerIp)
            {
                return null;
            }

            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var expires = now + _lifetime;
            _challenges[nonce] = new ChallengeEntry(petId.Trim(), devicePublicKeyBase64, normalizedIp, now, expires);
            if (normalizedIp is not null)
            {
                _ipCounts[normalizedIp] = _ipCounts.GetValueOrDefault(normalizedIp) + 1;
            }

            return new AuthChallengeResponse
            {
                Nonce = nonce,
                IssuedAtUtc = now,
                ExpiresAtUtc = expires
            };
        }
    }

    public bool TryConsumeChallenge(
        string nonce,
        string petId,
        string devicePublicKeyBase64,
        out DateTimeOffset issuedAtUtc)
    {
        issuedAtUtc = default;
        if (string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(petId))
        {
            return false;
        }

        ChallengeEntry? entry;
        lock (_sync)
        {
            if (!_challenges.Remove(nonce, out entry))
            {
                return false;
            }

            DecrementIpCountLocked(entry.RemoteIp);
        }

        if (entry.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            return false;
        }

        if (!string.Equals(entry.PetId, petId.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(entry.PublicKeyBase64, devicePublicKeyBase64, StringComparison.Ordinal))
        {
            return false;
        }

        issuedAtUtc = entry.IssuedAtUtc;
        return true;
    }

    public void PruneExpired(DateTimeOffset now)
    {
        lock (_sync)
        {
            PruneExpiredLocked(now);
        }
    }

    private void PruneExpiredLocked(DateTimeOffset now)
    {
        foreach (var kv in _challenges.Where(kv => kv.Value.ExpiresAtUtc < now).ToArray())
        {
            if (_challenges.Remove(kv.Key, out var removed))
            {
                DecrementIpCountLocked(removed.RemoteIp);
            }
        }
    }

    private void DecrementIpCountLocked(string? remoteIp)
    {
        if (remoteIp is null || !_ipCounts.TryGetValue(remoteIp, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            _ipCounts.Remove(remoteIp);
        }
        else
        {
            _ipCounts[remoteIp] = count - 1;
        }
    }
}
