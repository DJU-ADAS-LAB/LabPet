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
                cancellationToken).ConfigureAwait(false);

            store.Friends ??= [];

            if (needsSave)
            {
                await WriteAtomicCoreAsync(path, store, cancellationToken).ConfigureAwait(false);
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
            var path = Path.Combine(DataDirectory, FriendsFileName);
            await WriteAtomicCoreAsync(path, friendsStore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private static async Task<(T Result, bool NeedsSave)> LoadWithFallbackAsync<T>(
        string filePath,
        Func<JsonObject, bool> migrateAction,
        Func<T> defaultFactory,
        CancellationToken cancellationToken) where T : class
    {
        var backupPath = filePath + ".bak";

        if (File.Exists(filePath))
        {
            var primary = await TryReadAndMigrateAsync<T>(filePath, migrateAction, cancellationToken).ConfigureAwait(false);
            if (primary.Success && primary.Value is not null)
            {
                return (primary.Value, primary.Migrated);
            }
        }

        // 본 파일이 없거나 손상된 경우 직전 정상 백업(.bak)에서 복구 시도
        if (File.Exists(backupPath))
        {
            var backup = await TryReadAndMigrateAsync<T>(backupPath, migrateAction, cancellationToken).ConfigureAwait(false);
            if (backup.Success && backup.Value is not null)
            {
                return (backup.Value, true);
            }
        }

        // 최초 실행이거나 복구 불가 시 신규 생성
        return (defaultFactory(), true);
    }

    private static async Task<(bool Success, T? Value, bool Migrated)> TryReadAndMigrateAsync<T>(
        string path,
        Func<JsonObject, bool> migrateAction,
        CancellationToken cancellationToken) where T : class
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
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return (false, null, false);
        }
    }

    private async Task WriteAtomicCoreAsync<T>(string targetPath, T payload, CancellationToken cancellationToken)
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
        if (File.Exists(targetPath))
        {
            File.Copy(targetPath, backupPath, overwrite: true);
        }

        // 3. 임시 파일을 본 파일로 원자적 교체
        File.Move(tempPath, targetPath, overwrite: true);
    }
}
