using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;

namespace LabPet.Client.Storage;

/// <summary>
/// %AppData%\LabPet\ 디렉터리에 프로필/친구 JSON을 원자적(Atomic Write + .bak 백업)으로 저장·로드한다.
/// 설치 디렉터리(AppContext.BaseDirectory)에는 어떠한 사용자 데이터도 기록하지 않는다.
/// </summary>
public sealed class JsonAppDataStore : IAppDataStore, IAsyncDisposable
{
    private const string ProfileFileName = "profile.json";
    private const string FriendsFileName = "friends.json";
    private const string ProfileProtectionMarker = "profile.protection-v1";
    private const string ProtectedProgressProperty = "protectedProgressV1";
    private const int MigrationResetLevel = 50;
    private const long MigrationResetCoins = 50_000;

    private static readonly Version MigrationResetRelease = new(0, 3, 15, 0);
    private static readonly byte[] ProgressEntropy = Encoding.UTF8.GetBytes("LabPet.ProfileProgress.v1");

    private sealed record ProtectedProgress(string PetId, SoloProgress Progress);
    private sealed record ProfileRead(PetProfile? Profile, bool RequiresSave, bool HasProtection, JsonObject? Document);

    private readonly AsyncOperationLifetime _operations = new();
    private readonly IStateMigrator _migrator;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public string DataDirectory { get; }

    public JsonAppDataStore(IStateMigrator migrator, string? customDataDirectory = null)
    {
        _migrator = migrator;
        DataDirectory = string.IsNullOrWhiteSpace(customDataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LabPet")
            : customDataDirectory;
    }

    public async Task<PetProfile> LoadProfileAsync(CancellationToken cancellationToken = default)
    {
        using var operation = _operations.TryBegin() ?? throw new ObjectDisposedException(nameof(JsonAppDataStore));
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(DataDirectory, ProfileFileName);
            var markerExists = File.Exists(Path.Combine(DataDirectory, ProfileProtectionMarker));
            var primary = await ReadProfileAsync(path, markerExists, cancellationToken).ConfigureAwait(false);
            var chosen = primary;
            if (primary is not { Profile: not null, HasProtection: true })
            {
                var backup = await ReadProfileAsync(path + ".bak", markerExists || primary.HasProtection, cancellationToken)
                    .ConfigureAwait(false);
                if (backup is { Profile: not null, HasProtection: true }
                    || (!markerExists && !primary.HasProtection && primary.Profile is null && backup.Profile is not null))
                    chosen = backup;
                else if (markerExists || primary.HasProtection || backup.HasProtection)
                    throw new ProfileProtectionException();
            }

            var profile = chosen.Profile ?? new PetProfile();
            var isLegacyImport = !chosen.HasProtection;
            var needsSave = chosen.RequiresSave || !ReferenceEquals(chosen, primary) || isLegacyImport || !markerExists;

            // 필수 식별자 방어적 보정
            if (string.IsNullOrWhiteSpace(profile.PetId))
            {
                profile.PetId = Guid.NewGuid().ToString("D");
                needsSave = true;
            }

            if (string.IsNullOrWhiteSpace(profile.FriendCode))
            {
                profile.FriendCode = PetProfile.GenerateFriendCode();
                needsSave = true;
            }

            profile.Appearance ??= new PetAppearance();
            profile.SoloProgress ??= new SoloProgress();
            profile.SyncMetadata ??= new SyncMetadata();
            if (NormalizeProfileInvariants(profile))
            {
                needsSave = true;
            }

            // 새 보호 스냅샷을 만드는 최초 이전에서만 두 조건을 함께 검사한다.
            if (isLegacyImport && ShouldResetLegacyProgress(profile.SoloProgress, typeof(JsonAppDataStore).Assembly.GetName().Version))
            {
                profile.SoloProgress.Level = SoloProgress.MinLevel;
                profile.SoloProgress.Coins = 0;
                profile.SoloProgress.PendingGameWagers = [];
                profile.SoloProgress.PendingSoloLadderSession = null;
            }

            if (needsSave)
            {
                var recovering = !ReferenceEquals(chosen, primary);
                if (recovering && chosen.HasProtection && File.Exists(path))
                    PreserveRejectedProfile(path);
                await SaveProtectedProfileAsync(path, profile, markerExists, recovering, cancellationToken)
                    .ConfigureAwait(false);
            }

            return profile;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task SaveProfileAsync(PetProfile profile, CancellationToken cancellationToken = default)
    {
        using var operation = _operations.TryBegin() ?? throw new ObjectDisposedException(nameof(JsonAppDataStore));
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            profile.SchemaVersion = ProtocolConstants.CurrentStorageSchemaVersion;
            profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
            NormalizeProfileInvariants(profile);
            var path = Path.Combine(DataDirectory, ProfileFileName);
            var markerExists = File.Exists(Path.Combine(DataDirectory, ProfileProtectionMarker));
            await SaveProtectedProfileAsync(path, profile, markerExists, recovering: false, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    internal static bool ShouldResetLegacyProgress(SoloProgress progress, Version? release) =>
        release == MigrationResetRelease && progress.Level > MigrationResetLevel && progress.Coins > MigrationResetCoins;

    [System.Reflection.Obfuscation(Exclude = false)]
    private async Task<ProfileRead> ReadProfileAsync(string path, bool requireProtection, CancellationToken ct,
        bool captureDocument = false)
    {
        var hasProtection = false;
        JsonObject? document = null;
        var (ok, profile, migrated) = await TryReadAndMigrateAsync<PetProfile>(path, root =>
        {
            hasProtection = root.TryGetPropertyValue(ProtectedProgressProperty, out var encrypted);
            if (!hasProtection)
            {
                if (requireProtection) throw new JsonException("보호된 성장 상태가 필요합니다.");
                return _migrator.MigrateProfile(root);
            }

            try
            {
                if (encrypted is not JsonValue value || !value.TryGetValue<string>(out var blob)
                    || string.IsNullOrEmpty(blob))
                    throw new JsonException("보호된 성장 상태가 비어 있습니다.");
                var decoded = LocalSecretProtector.UnprotectBytes(Convert.FromBase64String(blob), ProgressEntropy);
                ProtectedProgress? progress;
                try
                {
                    progress = JsonSerializer.Deserialize<ProtectedProgress>(decoded, ProtocolJson.StorageOptions);
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(decoded);
                }
                if (progress?.Progress is null || string.IsNullOrEmpty(progress.PetId)
                    || root["petId"] is not JsonValue petId || !petId.TryGetValue<string>(out var id)
                    || !string.Equals(progress.PetId, id, StringComparison.OrdinalIgnoreCase))
                    throw new JsonException("보호된 성장 상태의 펫 ID가 일치하지 않습니다.");

                if (captureDocument) document = (JsonObject)root.DeepClone();

                // 평문이나 외부 스키마 번호의 수정이 보호된 성장 상태에 영향을 주지 않게 한다.
                foreach (var name in root.Select(pair => pair.Key)
                    .Where(name => name.Equals("soloProgress", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("petId", StringComparison.OrdinalIgnoreCase)).ToArray())
                    root.Remove(name);
                root["petId"] = progress.PetId;
                var changed = _migrator.MigrateProfile(root);
                root["soloProgress"] = JsonSerializer.SerializeToNode(progress.Progress, ProtocolJson.StorageOptions);
                return changed;
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                throw new JsonException("보호된 성장 상태를 검증할 수 없습니다.", ex);
            }
        }, ct, requireReadableFile: true).ConfigureAwait(false);
        return new ProfileRead(ok ? profile : null, migrated, hasProtection, ok ? document : null);
    }

    [System.Reflection.Obfuscation(Exclude = false)]
    private async Task SaveProtectedProfileAsync(string path, PetProfile profile, bool markerExists,
        bool recovering, CancellationToken ct)
    {
        JsonObject document;
        lock (profile.SoloProgress)
        {
            document = JsonSerializer.SerializeToNode(profile, ProtocolJson.StorageOptions)!.AsObject();
            document.Remove("soloProgress");
            var plain = JsonSerializer.SerializeToUtf8Bytes(new ProtectedProgress(profile.PetId, profile.SoloProgress),
                ProtocolJson.StorageOptions);
            try
            {
                document[ProtectedProgressProperty] = Convert.ToBase64String(
                    LocalSecretProtector.ProtectBytes(plain, ProgressEntropy, "LabPet Profile Progress"));
            }
            catch (InvalidOperationException ex)
            {
                throw new ProfileProtectionException(ex);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
            }
        }

        // 원본과 성장 상태는 한 스냅샷에서 함께 교체한다. 손상된 원본은 검증된 백업을 덮어쓰지 않는다.
        await WriteAtomicCoreAsync(path, document, ct, BackupValidatedProfileAsync,
            keepBackup: markerExists && !recovering).ConfigureAwait(false);

        if (!markerExists)
        {
            // 프로필을 먼저 커밋하므로 이전 중 종료돼도 평문을 다시 적용하지 않는다.
            await WriteAtomicCoreAsync(path + ".bak", document, ct, keepBackup: false).ConfigureAwait(false);
            await WriteAtomicCoreAsync(Path.Combine(DataDirectory, ProfileProtectionMarker), true, ct, keepBackup: false)
                .ConfigureAwait(false);
        }
    }

    [System.Reflection.Obfuscation(Exclude = false)]
    private async Task BackupValidatedProfileAsync(string path, string backupPath, CancellationToken ct)
    {
        if (!File.Exists(path)) return;

        var previous = await ReadProfileAsync(path, requireProtection: true, ct, captureDocument: true)
            .ConfigureAwait(false);
        if (previous.Document is not null)
            await WriteAtomicCoreAsync(backupPath, previous.Document, ct, keepBackup: false).ConfigureAwait(false);
        else
            PreserveRejectedProfile(path);
    }

    private static void PreserveRejectedProfile(string path) =>
        File.Copy(path, path + ".rejected-" + Guid.NewGuid().ToString("N"));

    public async Task<FriendsStore> LoadFriendsAsync(CancellationToken cancellationToken = default)
    {
        using var operation = _operations.TryBegin() ?? throw new ObjectDisposedException(nameof(JsonAppDataStore));
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(DataDirectory, FriendsFileName);
            var (store, needsSave) = await LoadWithFallbackAsync(
                path,
                _migrator.MigrateFriends,
                () => new FriendsStore(),
                cancellationToken, preserveUnreadableFiles: true).ConfigureAwait(false);

            store.Friends ??= [];
            await ProtectExistingFriendBackupAsync(path + ".bak", cancellationToken).ConfigureAwait(false);

            // friends.json에 저장된 DPAPI 비밀키 복호화 및 기존 평문 비밀키 자동 마이그레이션
            foreach (var friend in store.Friends)
            {
                var clampedIntimacy = FriendEntry.NormalizeIntimacy(friend.Intimacy);
                if (clampedIntimacy != friend.Intimacy)
                {
                    friend.Intimacy = clampedIntimacy;
                    needsSave = true;
                }

                if (LocalSecretProtector.TryUnprotect(friend.PairSecretBase64, out var plainSecret, out var wasPlaintext))
                {
                    friend.PairSecretBase64 = plainSecret;
                    if (wasPlaintext && !string.IsNullOrWhiteSpace(plainSecret))
                    {
                        needsSave = true;
                    }
                }
                else
                {
                    // 원래 Windows 계정에서 복구할 수 있도록 읽을 수 없는 암호문을 보존한다.
                    store.UnreadableProtectedKeys++;
                }
            }

            if (needsSave)
            {
                await WriteAtomicFriendsAsync(path, store, cancellationToken).ConfigureAwait(false);
            }

            return store;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task SaveFriendsAsync(FriendsStore friendsStore, CancellationToken cancellationToken = default)
    {
        using var operation = _operations.TryBegin() ?? throw new ObjectDisposedException(nameof(JsonAppDataStore));
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            friendsStore.SchemaVersion = ProtocolConstants.CurrentStorageSchemaVersion;
            friendsStore.UpdatedAtUtc = DateTimeOffset.UtcNow;
            foreach (var friend in friendsStore.Friends)
            {
                friend.Intimacy = FriendEntry.NormalizeIntimacy(friend.Intimacy);
            }
            var path = Path.Combine(DataDirectory, FriendsFileName);
            await WriteAtomicFriendsAsync(path, friendsStore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// 메모리 내 FriendEntry 원본의 평문 PairSecretBase64는 유지하면서,
    /// 디스크 직렬화용 복사본에만 Windows DPAPI 암호화를 적용해 원자적 저장한다.
    /// </summary>
    private Task WriteAtomicFriendsAsync(string path, FriendsStore friendsStore, CancellationToken cancellationToken)
        => WriteAtomicCoreAsync(path, ProtectedFriendCopy(friendsStore), cancellationToken, BackupFriendsAsync);

    private static FriendsStore ProtectedFriendCopy(FriendsStore friendsStore)
    {
        return new FriendsStore
        {
            SchemaVersion = friendsStore.SchemaVersion,
            UpdatedAtUtc = friendsStore.UpdatedAtUtc,
            Friends = (friendsStore.Friends ?? []).Select(friend =>
            {
                var clone = friend.Clone();
                try
                {
                    clone.PairSecretBase64 = LocalSecretProtector.Protect(clone.PairSecretBase64);
                }
                catch (InvalidOperationException ex)
                {
                    throw new IOException("친구 키를 보호하지 못했습니다. 기존 파일을 보존합니다.", ex);
                }
                return clone;
            }).ToList()
        };

    }

    private async Task BackupFriendsAsync(string primaryPath, string backupPath, CancellationToken ct)
    {
        var original = await TryReadAndMigrateAsync<FriendsStore>(primaryPath, _migrator.MigrateFriends, ct)
            .ConfigureAwait(false);
        if (original is { Success: true, Value: { } previous })
        {
            await WriteAtomicCoreAsync(backupPath, ProtectedFriendCopy(previous), ct, keepBackup: false)
                .ConfigureAwait(false);
        }
        else
        {
            // 손상된 본 파일로 정상 백업을 덮어쓰지 않는다.
            await ProtectExistingFriendBackupAsync(backupPath, ct).ConfigureAwait(false);
        }
    }

    private async Task ProtectExistingFriendBackupAsync(string backupPath, CancellationToken ct)
    {
        var backup = await TryReadAndMigrateAsync<FriendsStore>(backupPath, _migrator.MigrateFriends, ct, requireReadableFile: true)
            .ConfigureAwait(false);
        if (backup.Value?.Friends is { } friends && friends.Any(friend =>
            !string.IsNullOrWhiteSpace(friend.PairSecretBase64) && !LocalSecretProtector.IsProtected(friend.PairSecretBase64)))
        {
            await WriteAtomicCoreAsync(backupPath, ProtectedFriendCopy(backup.Value), ct, keepBackup: false)
                .ConfigureAwait(false);
        }
    }

    private static async Task<(T Result, bool NeedsSave)> LoadWithFallbackAsync<T>(
        string filePath,
        Func<JsonObject, bool> migrateAction,
        Func<T> defaultFactory,
        CancellationToken cancellationToken, bool preserveUnreadableFiles = false) where T : class
    {
        var backupPath = filePath + ".bak";
        var primaryExists = File.Exists(filePath);
        var backupExists = File.Exists(backupPath);

        if (primaryExists)
        {
            var primary = await TryReadAndMigrateAsync<T>(filePath, migrateAction, cancellationToken).ConfigureAwait(false);
            if (primary.Success && primary.Value is not null)
            {
                return (primary.Value, primary.Migrated);
            }
        }

        // 본 파일이 없거나 손상된 경우 직전 정상 백업(.bak)에서 복구 시도
        if (backupExists)
        {
            var backup = await TryReadAndMigrateAsync<T>(backupPath, migrateAction, cancellationToken).ConfigureAwait(false);
            if (backup.Success && backup.Value is not null)
            {
                return (backup.Value, true);
            }
        }

        if (preserveUnreadableFiles && (primaryExists || backupExists))
        {
            throw new IOException("친구 목록과 백업을 읽을 수 없습니다. 기존 파일을 보존합니다.");
        }

        // 최초 실행이거나 프로필을 복구할 수 없을 때 신규 생성
        return (defaultFactory(), true);
    }

    private static async Task<(bool Success, T? Value, bool Migrated)> TryReadAndMigrateAsync<T>(
        string path,
        Func<JsonObject, bool> migrateAction,
        CancellationToken cancellationToken, bool requireReadableFile = false) where T : class
    {
        try
        {
            var jsonText = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(jsonText))
            {
                return (false, null, false);
            }

            var parsedNode = JsonNode.Parse(jsonText);
            if (parsedNode is not JsonObject rootObject)
            {
                return (false, null, false);
            }

            var migrated = migrateAction(rootObject);
            var deserialized = rootObject.Deserialize<T>(ProtocolJson.StorageOptions);
            return deserialized is not null
                ? (true, deserialized, migrated)
                : (false, null, false);
        }
        catch (Exception ex) when (ex is JsonException or FileNotFoundException or DirectoryNotFoundException
            || (!requireReadableFile && ex is (IOException or UnauthorizedAccessException)))
        {
            return (false, null, false);
        }
    }

    private async Task WriteAtomicCoreAsync<T>(string targetPath, T payload, CancellationToken cancellationToken,
        Func<string, string, CancellationToken, Task>? writeBackup = null, bool keepBackup = true)
    {
        Directory.CreateDirectory(DataDirectory);

        var tempPath = targetPath + ".tmp";
        var backupPath = targetPath + ".bak";
        var json = JsonSerializer.Serialize(payload, ProtocolJson.StorageOptions);

        // 1. 임시 파일에 UTF-8 기록 후 디스크 플러시
        await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        await using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            fs.Flush(flushToDisk: true);
        }

        // 2. 기존 정상 파일이 존재하면 직전 백업(.bak) 1개 갱신
        if (keepBackup && writeBackup is not null)
        {
            await writeBackup(targetPath, backupPath, cancellationToken).ConfigureAwait(false);
        }
        else if (keepBackup && File.Exists(targetPath))
        {
            File.Copy(targetPath, backupPath, overwrite: true);
        }

        // 3. 임시 파일을 본 파일로 원자적 교체
        File.Move(tempPath, targetPath, overwrite: true);
    }

    public ValueTask DisposeAsync() => new(_operations.DrainAsync());

    private static bool NormalizeProfileInvariants(PetProfile profile)
    {
        var changed = false;
        var p = profile.SoloProgress;
        var clampedLevel = Math.Clamp(p.Level, SoloProgress.MinLevel, SoloProgress.MaxLevel);
        if (p.Level != clampedLevel)
        {
            p.Level = clampedLevel;
            changed = true;
        }

        if (p.Xp != 0)
        {
            p.Xp = 0;
            changed = true;
        }

        if (p.Coins < 0)
        {
            p.Coins = 0;
            changed = true;
        }

        if (p.DailySocialRewardCoinsEarned is < 0 or > 10)
        {
            p.DailySocialRewardCoinsEarned = Math.Clamp(p.DailySocialRewardCoinsEarned, 0, 10);
            changed = true;
        }

        return changed;
    }
}

internal sealed class ProfileProtectionException(Exception? inner = null)
    : IOException("로컬 성장 데이터의 보호 상태를 확인할 수 없습니다.", inner);
