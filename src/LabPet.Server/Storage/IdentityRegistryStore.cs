using System.Text;
using System.Text.Json;
using LabPet.Server.Options;
using LabPet.Shared.Protocol;

namespace LabPet.Server.Storage;

public sealed class PetIdentityRecord
{
    public string PetId { get; set; } = string.Empty;
    public string DevicePublicKeyBase64 { get; set; } = string.Empty;
    public string KeyThumbprint { get; set; } = string.Empty;
    public string FriendCode { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string PetName { get; set; } = string.Empty;
    public DateTimeOffset RegisteredAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastAuthenticatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class RegistryDocument
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, PetIdentityRecord> Identities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ApprovedFriendships { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> DeclaredFriends { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 기기 공개키 ↔ PetId 바인딩 및 승인된 친구 관계만을 보관하는 최소 영속 저장소.
/// 채팅 메시지, 빠른 반응, 게임 페이로드, 오프라인 쪽지는 절대 저장하지 않는다.
/// </summary>
public sealed class IdentityRegistryStore
{
    private const string FileName = "registry.json";

    private readonly string _filePath;
    private readonly int _maxDeclaredFriendsPerPet;
    private readonly ILogger<IdentityRegistryStore>? _logger;
    private readonly object _lock = new();
    private readonly RegistryDocument _doc;

    public IdentityRegistryStore(ServerOptions options, ILogger<IdentityRegistryStore>? logger = null)
    {
        _logger = logger;
        var dataDir = string.IsNullOrWhiteSpace(options.DataDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : options.DataDirectory;
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, FileName);
        _maxDeclaredFriendsPerPet = Math.Clamp(options.MaxDeclaredFriendsPerPet, 10, 1000);
        _doc = LoadOrCreate(_filePath);
    }

    public bool TryAuthenticateOrRegister(
        string petId,
        string devicePublicKeyBase64,
        string friendCode,
        string ownerName,
        string petName,
        out PetIdentityRecord? record,
        out string errorCode)
    {
        record = null;
        errorCode = string.Empty;

        if (string.IsNullOrWhiteSpace(petId) || petId.Length > 64 || !RemoteAuthSigner.IsValidDevicePublicKey(devicePublicKeyBase64))
        {
            errorCode = "INVALID_IDENTITY";
            return false;
        }

        var normalizedPetId = petId.Trim();
        var normalizedFriendCode = NormalizeFriendCode(friendCode);
        var thumbprint = RemoteAuthSigner.ComputeKeyThumbprint(devicePublicKeyBase64);
        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            if (_doc.Identities.TryGetValue(normalizedPetId, out var existing))
            {
                if (!string.Equals(existing.DevicePublicKeyBase64, devicePublicKeyBase64, StringComparison.Ordinal))
                {
                    errorCode = "PUBLIC_KEY_MISMATCH";
                    return false;
                }

                if (!string.IsNullOrEmpty(normalizedFriendCode))
                {
                    existing.FriendCode = normalizedFriendCode;
                }

                if (!string.IsNullOrWhiteSpace(ownerName))
                {
                    existing.OwnerName = TrimDisplay(ownerName, 32);
                }

                if (!string.IsNullOrWhiteSpace(petName))
                {
                    existing.PetName = TrimDisplay(petName, 32);
                }

                existing.LastAuthenticatedAtUtc = now;
                _doc.UpdatedAtUtc = now;
                SaveAtomicLocked();
                record = CloneRecord(existing);
                return true;
            }

            var created = new PetIdentityRecord
            {
                PetId = normalizedPetId,
                DevicePublicKeyBase64 = devicePublicKeyBase64,
                KeyThumbprint = thumbprint,
                FriendCode = normalizedFriendCode,
                OwnerName = TrimDisplay(ownerName, 32),
                PetName = TrimDisplay(petName, 32),
                RegisteredAtUtc = now,
                LastAuthenticatedAtUtc = now
            };
            _doc.Identities[normalizedPetId] = created;
            _doc.UpdatedAtUtc = now;
            SaveAtomicLocked();
            record = CloneRecord(created);
            return true;
        }
    }

    public void UpdateIdentityProfile(string petId, string friendCode, string ownerName, string petName)
    {
        var normalizedFriendCode = NormalizeFriendCode(friendCode);
        lock (_lock)
        {
            if (!_doc.Identities.TryGetValue(petId, out var rec))
            {
                return;
            }

            var changed = false;
            if (!string.IsNullOrEmpty(normalizedFriendCode) && !string.Equals(rec.FriendCode, normalizedFriendCode, StringComparison.Ordinal))
            {
                rec.FriendCode = normalizedFriendCode;
                changed = true;
            }

            var cleanOwner = TrimDisplay(ownerName, 32);
            if (!string.IsNullOrEmpty(cleanOwner) && !string.Equals(rec.OwnerName, cleanOwner, StringComparison.Ordinal))
            {
                rec.OwnerName = cleanOwner;
                changed = true;
            }

            var cleanPet = TrimDisplay(petName, 32);
            if (!string.IsNullOrEmpty(cleanPet) && !string.Equals(rec.PetName, cleanPet, StringComparison.Ordinal))
            {
                rec.PetName = cleanPet;
                changed = true;
            }

            if (changed)
            {
                _doc.UpdatedAtUtc = DateTimeOffset.UtcNow;
                SaveAtomicLocked();
            }
        }
    }

    public bool ApproveFriendship(string petA, string petB)
    {
        if (string.IsNullOrWhiteSpace(petA) || string.IsNullOrWhiteSpace(petB)
            || string.Equals(petA, petB, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var key = BuildPairKey(petA, petB);
        lock (_lock)
        {
            var changed = _doc.ApprovedFriendships.Add(key);
            changed |= AddDeclaredLocked(petA, petB);
            changed |= AddDeclaredLocked(petB, petA);
            if (!changed)
            {
                _logger?.LogInformation("친구 관계 승인 확인(이미 등록됨): Stage=FriendshipRegistry, TotalPairs={TotalPairs}", _doc.ApprovedFriendships.Count);
                return true;
            }

            _doc.UpdatedAtUtc = DateTimeOffset.UtcNow;
            var saved = SaveAtomicLocked();
            if (saved)
            {
                _logger?.LogInformation("친구 관계 승인 저장 완료: Stage=FriendshipRegistry, TotalPairs={TotalPairs}", _doc.ApprovedFriendships.Count);
            }
            else
            {
                _logger?.LogWarning("친구 관계 승인 저장 실패(메모리 상태만 반영): Stage=FriendshipRegistry, ErrorCode=REGISTRY_SAVE_FAILED, TotalPairs={TotalPairs}", _doc.ApprovedFriendships.Count);
            }

            return saved;
        }
    }

    public void SyncDeclaredFriends(string petId, IReadOnlyCollection<string> friendPetIds)
    {
        if (string.IsNullOrWhiteSpace(petId))
        {
            return;
        }

        var normalized = friendPetIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 64 && !string.Equals(id, petId, StringComparison.OrdinalIgnoreCase))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(_maxDeclaredFriendsPerPet)
            .ToList();

        lock (_lock)
        {
            if (_doc.DeclaredFriends.TryGetValue(petId, out var existing)
                && existing.Count == normalized.Count
                && new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase).SetEquals(normalized))
            {
                return;
            }

            _doc.DeclaredFriends[petId] = normalized;
            _doc.UpdatedAtUtc = DateTimeOffset.UtcNow;
            SaveAtomicLocked();
        }
    }

    public bool AreMutualFriends(string petA, string petB)
    {
        if (string.IsNullOrWhiteSpace(petA) || string.IsNullOrWhiteSpace(petB)
            || string.Equals(petA, petB, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var key = BuildPairKey(petA, petB);
        lock (_lock)
        {
            if (_doc.ApprovedFriendships.Contains(key))
            {
                return true;
            }

            var aHasB = _doc.DeclaredFriends.TryGetValue(petA, out var aFriends)
                && aFriends.Any(id => string.Equals(id, petB, StringComparison.OrdinalIgnoreCase));
            var bHasA = _doc.DeclaredFriends.TryGetValue(petB, out var bFriends)
                && bFriends.Any(id => string.Equals(id, petA, StringComparison.OrdinalIgnoreCase));
            return aHasB && bHasA;
        }
    }

    public IReadOnlyList<string> GetPotentialMutualFriendIds(string petId)
    {
        lock (_lock)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in _doc.ApprovedFriendships)
            {
                var sep = key.IndexOf('|');
                if (sep <= 0) continue;
                var a = key[..sep];
                var b = key[(sep + 1)..];
                if (string.Equals(a, petId, StringComparison.OrdinalIgnoreCase)) result.Add(b);
                else if (string.Equals(b, petId, StringComparison.OrdinalIgnoreCase)) result.Add(a);
            }

            if (_doc.DeclaredFriends.TryGetValue(petId, out var declared))
            {
                foreach (var otherId in declared)
                {
                    if (_doc.DeclaredFriends.TryGetValue(otherId, out var otherDeclared)
                        && otherDeclared.Any(id => string.Equals(id, petId, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Add(otherId);
                    }
                }
            }

            return result.ToList();
        }
    }

    private bool AddDeclaredLocked(string ownerId, string friendId)
    {
        if (!_doc.DeclaredFriends.TryGetValue(ownerId, out var list))
        {
            list = [];
            _doc.DeclaredFriends[ownerId] = list;
        }

        if (list.Any(x => string.Equals(x, friendId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (list.Count >= _maxDeclaredFriendsPerPet)
        {
            return false;
        }

        list.Add(friendId);
        return true;
    }

    private static string BuildPairKey(string petA, string petB)
    {
        var a = petA.Trim().ToLowerInvariant();
        var b = petB.Trim().ToLowerInvariant();
        return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
    }

    private static string NormalizeFriendCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        var trimmed = code.Trim().ToUpperInvariant();
        return trimmed.Length > 32 ? trimmed[..32] : trimmed;
    }

    private static string TrimDisplay(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = value.Trim();
        return cleaned.Length <= maxLen ? cleaned : cleaned[..maxLen];
    }

    private static PetIdentityRecord CloneRecord(PetIdentityRecord src) => new()
    {
        PetId = src.PetId,
        DevicePublicKeyBase64 = src.DevicePublicKeyBase64,
        KeyThumbprint = src.KeyThumbprint,
        FriendCode = src.FriendCode,
        OwnerName = src.OwnerName,
        PetName = src.PetName,
        RegisteredAtUtc = src.RegisteredAtUtc,
        LastAuthenticatedAtUtc = src.LastAuthenticatedAtUtc
    };

    private static RegistryDocument LoadOrCreate(string path)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(candidate, Encoding.UTF8);
                var doc = JsonSerializer.Deserialize<RegistryDocument>(json, ProtocolJson.StorageOptions);
                if (doc is not null)
                {
                    doc.Identities ??= new(StringComparer.OrdinalIgnoreCase);
                    doc.ApprovedFriendships ??= new(StringComparer.OrdinalIgnoreCase);
                    doc.DeclaredFriends ??= new(StringComparer.OrdinalIgnoreCase);
                    return doc;
                }
            }
            catch
            {
                // 손상된 본 파일 시 .bak 폴백
            }
        }

        return new RegistryDocument();
    }

    private bool SaveAtomicLocked()
    {
        var tmpPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        var bakPath = _filePath + ".bak";
        try
        {
            var json = JsonSerializer.Serialize(_doc, ProtocolJson.StorageOptions);
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(_filePath))
            {
                File.Copy(_filePath, bakPath, overwrite: true);
            }

            File.Move(tmpPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "저장소 원자적 저장 실패: File={FileName}, ErrorCode=REGISTRY_SAVE_FAILED", FileName);
            return false;
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { }
            }
        }
    }
}
