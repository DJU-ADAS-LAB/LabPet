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
public sealed class JsonAppDataStore : IAppDataStore
{
    private const string ProfileFileName = "profile.json";
    private const string FriendsFileName = "friends.json";

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
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(DataDirectory, ProfileFileName);
            var (profile, needsSave) = await LoadWithFallbackAsync(
                path,
                _migrator.MigrateProfile,
                () => new PetProfile(),
                cancellationToken).ConfigureAwait(false);

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

            if (needsSave)
            {
                await WriteAtomicCoreAsync(path, profile, cancellationToken).ConfigureAwait(false);
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
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            profile.SchemaVersion = ProtocolConstants.CurrentStorageSchemaVersion;
            profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
            NormalizeProfileInvariants(profile);
            var path = Path.Combine(DataDirectory, ProfileFileName);
            await WriteAtomicCoreAsync(path, profile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<FriendsStore> LoadFriendsAsync(CancellationToken cancellationToken = default)
    {
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
        string json;
        if (payload is PetProfile { SoloProgress: { } progress })
        {
            // 소셜 보상·상점 구매와 같은 잠금에서 성장 상태를 직렬화한다.
            lock (progress)
            {
                json = JsonSerializer.Serialize(payload, ProtocolJson.StorageOptions);
            }
        }
        else
        {
            json = JsonSerializer.Serialize(payload, ProtocolJson.StorageOptions);
        }

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
