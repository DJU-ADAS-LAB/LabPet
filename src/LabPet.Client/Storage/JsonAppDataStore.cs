using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabPet.Client.Diagnostics;
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
    private const string RecoverySettingsFileName = "profile.settings-recovery.json";
    private const string ProtectedProgressProperty = "protectedProgressV1";
    private const string ProtectionVaultFlagProperty = "protected";
    private const int MigrationResetLevel = 50;
    private const long MigrationResetCoins = 50_000;
    private const int FileIoMaxAttempts = 5;
    private const int ErrorSharingViolationHResult = unchecked((int)0x80070020);
    private const int ErrorLockViolationHResult = unchecked((int)0x80070021);

    internal const string MigrationResetRelease = "0.3.17";
    private static readonly byte[] ProgressEntropy = Encoding.UTF8.GetBytes("LabPet.ProfileProgress.v1");

    private sealed record ProtectedProgress(string PetId, SoloProgress Progress, string? ResetReceipt = null);
    private sealed record ProfileRead(PetProfile? Profile, bool RequiresSave, bool HasProtection, JsonObject? Document,
        string? FailureReason = null, PetProfile? Shell = null);
    private sealed record ProtectionVaultState(bool Exists, bool HasSealedBlob, ProtectedProgress? Progress,
        string? FailureReason = null, string? PetId = null);

    private readonly AsyncOperationLifetime _operations = new();
    private readonly IStateMigrator _migrator;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly Version? _releaseVersion;

    public string DataDirectory { get; }
    internal event EventHandler<PetProfile>? GrowthRecoveryEntered;

    internal JsonAppDataStore(IStateMigrator migrator, string? customDataDirectory)
        : this(migrator, customDataDirectory, typeof(JsonAppDataStore).Assembly.GetName().Version)
    {
    }

    public JsonAppDataStore(IStateMigrator migrator)
        : this(migrator, null, typeof(JsonAppDataStore).Assembly.GetName().Version)
    {
        // 외부 스크립트가 앱 DLL을 저장·재암호화 도구로 사용하는 것을 거부한다.
        // 동일 사용자에 의한 DLL 패치나 비공개 리플렉션까지 막는 보안 경계는 아니다.
        if (!ReferenceEquals(System.Reflection.Assembly.GetEntryAssembly(), typeof(JsonAppDataStore).Assembly))
            throw new UnauthorizedAccessException("프로필 저장소는 LabPet 앱에서만 생성할 수 있습니다.");
    }

    internal JsonAppDataStore(IStateMigrator migrator, string? customDataDirectory, Version? releaseVersion)
    {
        _migrator = migrator;
        _releaseVersion = releaseVersion;
        DataDirectory = string.IsNullOrWhiteSpace(customDataDirectory)
            ? AppDataPaths.ResolveDefaultDataDirectory()
            : customDataDirectory;
    }

    public async Task<PetProfile> LoadProfileAsync(CancellationToken cancellationToken = default)
    {
        using var operation = _operations.TryBegin() ?? throw new ObjectDisposedException(nameof(JsonAppDataStore));
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadProfileCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task<PetProfile> LoadProfileCoreAsync(CancellationToken ct, PetProfile? recoverySession = null)
    {
        var path = Path.Combine(DataDirectory, ProfileFileName);
        var markerPath = Path.Combine(DataDirectory, ProfileProtectionMarker);
        var settingsPath = Path.Combine(DataDirectory, RecoverySettingsFileName);
        var vault = await ReadProtectionVaultAsync(markerPath, ct).ConfigureAwait(false);
        var protectionRequired = vault.Exists || File.Exists(settingsPath) || recoverySession is not null;
        var primary = await ReadProfileAsync(path, protectionRequired, ct, captureShell: true).ConfigureAwait(false);
        // 원본을 사용할 수 있으면 백업 읽기 실패가 앱 시작을 막지 않는다.
        var backup = await ReadProfileAsync(path + ".bak", protectionRequired || primary.HasProtection, ct,
            ignoreTransientReadError: true, captureShell: true).ConfigureAwait(false);
        var settings = await ReadProfileShellAsync(settingsPath, ct).ConfigureAwait(false);
        var expectedId = new[] { recoverySession?.PetId, primary.Shell?.PetId, vault.PetId, backup.Shell?.PetId, settings?.PetId }
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        var source = primary is { Profile: not null, HasProtection: true } && SamePet(primary.Profile.PetId, expectedId)
            ? primary : backup is { Profile: not null, HasProtection: true } && SamePet(backup.Profile.PetId, expectedId)
            ? backup : null;
        var protectedBackup = vault.Progress is { } sealedProgress && SamePet(sealedProgress.PetId, expectedId)
            ? sealedProgress : null;

        // 검사 전 백업을 다시 넣어도 같은 펫의 검사 완료 스냅샷을 우선 사용한다.
        var useVault = protectedBackup is not null && (source is null
            || (source.Profile!.GrowthResetReceipt != MigrationResetRelease && protectedBackup.ResetReceipt == MigrationResetRelease));
        if (!useVault && source?.Profile?.GrowthResetReceipt != MigrationResetRelease
            && backup.Profile is { GrowthResetReceipt: MigrationResetRelease } checkedBackup
            && SamePet(checkedBackup.PetId, expectedId))
            source = backup;

        var recoverySettings = MatchingShell(settings, expectedId);
        var shell = recoverySession ?? recoverySettings ?? MatchingShell(primary.Shell, expectedId)
            ?? MatchingShell(backup.Shell, expectedId);
        var hasProtectedHistory = protectionRequired || primary.HasProtection || backup.HasProtection;
        if (source is null && !useVault && !hasProtectedHistory)
        {
            if (primary.Profile is not null) source = primary;
            else if (backup.Profile is not null) source = backup;
        }
        var isNewProfile = source is null && !useVault && !hasProtectedHistory
            && !File.Exists(path) && !File.Exists(path + ".bak");
        if (source is null && !useVault && !isNewProfile)
        {
            if (recoverySession is not null) return recoverySession;
            return EnterGrowthRecovery(shell ?? CreateRecoveryShell(expectedId),
                BuildProtectionFailureDetails(primary, backup, vault));
        }

        var profile = useVault || source?.HasProtection == true
            ? shell is not null ? CopyProfileShell(shell) : CreateRecoveryShell(expectedId)
            : source?.Profile ?? new PetProfile();
        if (useVault)
        {
            profile.SoloProgress = protectedBackup!.Progress;
            profile.GrowthResetReceipt = protectedBackup.ResetReceipt;
        }
        else if (source?.HasProtection == true)
        {
            profile.SoloProgress = source.Profile!.SoloProgress;
            profile.GrowthResetReceipt = source.Profile.GrowthResetReceipt;
        }
        var requiresSave = NormalizeProfileShell(profile) | NormalizeProfileInvariants(profile);
        var checkedThisRelease = ApplyGrowthReset(profile, _releaseVersion);
        lock (profile.GrowthSync)
        {
            var needsAudit = profile.SoloProgress.GrowthAudit is null;
            var detected = LabPet.Client.Solo.GrowthRateMonitor.Observe(profile, DateTimeOffset.UtcNow);
            requiresSave |= needsAudit || detected;
            checkedThisRelease |= detected;
        }
        var restoring = useVault || !ReferenceEquals(source, primary) || recoverySession is not null || recoverySettings is not null;
        try
        {
            if (requiresSave || checkedThisRelease || restoring || source?.RequiresSave == true || source?.HasProtection != true || !vault.Exists)
            {
                if (restoring && primary.Profile is null)
                    await ArchiveRejectedProfileAsync(path, ct).ConfigureAwait(false);
                await SaveProtectedProfileAsync(path, profile, vault.Exists, restoring, ct,
                    refreshBackupFromCurrent: useVault || checkedThisRelease || recoverySession is not null).ConfigureAwait(false);
            }
            else if (protectedBackup is null
                     || (profile.GrowthResetReceipt == MigrationResetRelease && protectedBackup.ResetReceipt != MigrationResetRelease))
            {
                await WriteProtectionVaultAsync(markerPath, profile, ct).ConfigureAwait(false);
            }
            if (recoverySettings is not null)
                RemoveRecoverySettings(settingsPath);
            return profile;
        }
        catch (Exception ex) when (ex is ProfileProtectionException || (restoring && ex is IOException or UnauthorizedAccessException))
        {
            if (recoverySession is not null) return recoverySession;
            return EnterGrowthRecovery(profile, ex.Message);
        }
    }

    public async Task SaveProfileAsync(PetProfile profile, CancellationToken cancellationToken = default)
    {
        using var operation = _operations.TryBegin() ?? throw new ObjectDisposedException(nameof(JsonAppDataStore));
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NormalizeProfileShell(profile);
            profile.SchemaVersion = ProtocolConstants.CurrentStorageSchemaVersion;
            profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
            if (profile.RequiresGrowthRecovery)
            {
                await SaveRecoverySettingsAsync(profile, cancellationToken).ConfigureAwait(false);
                return;
            }

            lock (profile.GrowthSync)
            {
                LabPet.Client.Solo.GrowthRateMonitor.Observe(profile, DateTimeOffset.UtcNow);
                NormalizeProfileInvariants(profile);
            }
            var path = Path.Combine(DataDirectory, ProfileFileName);
            var markerExists = File.Exists(Path.Combine(DataDirectory, ProfileProtectionMarker));
            try
            {
                await SaveProtectedProfileAsync(path, profile, markerExists, recovering: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ProfileProtectionException ex)
            {
                EnterGrowthRecovery(profile, ex.Message);
                GrowthRecoveryEntered?.Invoke(this, profile);
                await SaveRecoverySettingsAsync(profile, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public Task<bool> RetryGrowthRecoveryAsync(PetProfile profile, CancellationToken cancellationToken = default) =>
        RepairGrowthAsync(profile, reset: false, cancellationToken);

    public Task<bool> ResetUnrecoverableGrowthAsync(PetProfile profile, CancellationToken cancellationToken = default) =>
        RepairGrowthAsync(profile, reset: true, cancellationToken);

    private async Task<bool> RepairGrowthAsync(PetProfile profile, bool reset, CancellationToken ct)
    {
        using var operation = _operations.TryBegin() ?? throw new ObjectDisposedException(nameof(JsonAppDataStore));
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!profile.RequiresGrowthRecovery) return false;
            var recovered = await LoadProfileCoreAsync(ct, profile).ConfigureAwait(false);
            if (recovered.RequiresGrowthRecovery)
            {
                if (!reset) return false;
                recovered = CopyProfileShell(profile);
                recovered.GrowthResetReceipt = MigrationResetRelease;
                var path = Path.Combine(DataDirectory, ProfileFileName);
                await ArchiveRejectedProfileAsync(path, ct).ConfigureAwait(false);
                await SaveProtectedProfileAsync(path, recovered, markerExists: true, recovering: true, ct,
                    refreshBackupFromCurrent: true).ConfigureAwait(false);
                RemoveRecoverySettings(Path.Combine(DataDirectory, RecoverySettingsFileName));
            }

            lock (profile.GrowthSync)
            {
                profile.SoloProgress = recovered.SoloProgress;
                profile.GrowthResetReceipt = recovered.GrowthResetReceipt;
                profile.RequiresGrowthRecovery = false;
            }
            return true;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    internal static bool ApplyGrowthReset(PetProfile profile, Version? release)
    {
        if (release is null || release.Build < 0 || release.ToString(3) != MigrationResetRelease
            || profile.GrowthResetReceipt == MigrationResetRelease)
            return false;

        var progress = profile.SoloProgress;
        if (progress.Level > MigrationResetLevel || progress.Coins > MigrationResetCoins)
        {
            progress.Level = SoloProgress.MinLevel;
            progress.Xp = 0;
            progress.Coins = 0;
            progress.PendingGameWagers = [];
            progress.PendingSoloLadderSession = null;
            progress.PendingKeystrokesForReward = 0;
            progress.PendingMouseClicksForReward = 0;
            progress.PendingUptimeMinutesForReward = 0;
        }
        profile.GrowthResetReceipt = MigrationResetRelease;
        return true;
    }

    private static bool SamePet(string? id, string? expectedId) =>
        !string.IsNullOrWhiteSpace(id) && string.Equals(id, expectedId, StringComparison.OrdinalIgnoreCase);

    private static PetProfile? MatchingShell(PetProfile? shell, string? id) =>
        SamePet(shell?.PetId, id) ? shell : null;

    private static PetProfile CreateRecoveryShell(string? petId)
    {
        var shell = new PetProfile { IsOnboardingCompleted = true };
        if (!string.IsNullOrWhiteSpace(petId)) shell.PetId = petId;
        return shell;
    }

    private static PetProfile EnterGrowthRecovery(PetProfile profile, string reason)
    {
        lock (profile.GrowthSync)
        {
            profile.RequiresGrowthRecovery = true;
            profile.SoloProgress = new SoloProgress();
            profile.GrowthResetReceipt = null;
        }
        NormalizeProfileShell(profile);
        CrashLogWriter.Write("Storage.GrowthRecovery", new ProfileProtectionException(reason));
        return profile;
    }

    private static bool NormalizeProfileShell(PetProfile profile)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(profile.PetId))
        {
            profile.PetId = Guid.NewGuid().ToString("D");
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(profile.FriendCode))
        {
            profile.FriendCode = PetProfile.GenerateFriendCode();
            changed = true;
        }
        profile.Appearance ??= new PetAppearance();
        profile.SoloProgress ??= new SoloProgress();
        profile.SyncMetadata ??= new SyncMetadata();
        return changed;
    }

    private static JsonObject CreateProfileShellDocument(PetProfile profile)
    {
        var document = JsonSerializer.SerializeToNode(profile, ProtocolJson.StorageOptions)!.AsObject();
        document.Remove("soloProgress");
        return document;
    }

    private static PetProfile CopyProfileShell(PetProfile profile) =>
        CreateProfileShellDocument(profile).Deserialize<PetProfile>(ProtocolJson.StorageOptions)!;

    private async Task SaveRecoverySettingsAsync(PetProfile profile, CancellationToken ct)
    {
        var shell = CreateProfileShellDocument(profile);
        await WriteAtomicCoreAsync(Path.Combine(DataDirectory, RecoverySettingsFileName), shell, ct, keepBackup: false)
            .ConfigureAwait(false);
    }

    private static void RemoveRecoverySettings(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLogWriter.Write("Storage.RemoveRecoverySettings", ex);
        }
    }

    private async Task<PetProfile?> ReadProfileShellAsync(string path, CancellationToken ct)
    {
        var (_, profile, _) = await TryReadAndMigrateAsync<PetProfile>(path, root =>
        {
            RemoveGrowthFields(root);
            return _migrator.MigrateProfile(root);
        }, ct).ConfigureAwait(false);
        return profile;
    }

    private static void RemoveGrowthFields(JsonObject root)
    {
        foreach (var name in root.Select(pair => pair.Key).Where(name =>
            name.Equals("soloProgress", StringComparison.OrdinalIgnoreCase)
            || name.Equals(ProtectedProgressProperty, StringComparison.OrdinalIgnoreCase)).ToArray())
            root.Remove(name);
    }

    [System.Reflection.Obfuscation(Exclude = false)]
    private async Task<ProfileRead> ReadProfileAsync(string path, bool requireProtection, CancellationToken ct,
        bool captureDocument = false, bool ignoreTransientReadError = false, bool captureShell = false)
    {
        var hasProtection = false;
        JsonObject? document = null;
        PetProfile? shell = null;
        string? resetReceipt = null;
        string? failureReason = null;
        var (ok, profile, migrated) = await TryReadAndMigrateAsync<PetProfile>(path, root =>
        {
            hasProtection = root.TryGetPropertyValue(ProtectedProgressProperty, out var encrypted);
            if (captureShell)
            {
                try
                {
                    var shellJson = (JsonObject)root.DeepClone();
                    RemoveGrowthFields(shellJson);
                    var knownId = (shellJson["petId"] as JsonValue)?.TryGetValue<string>(out var suppliedId) == true
                        ? suppliedId : string.Empty;
                    _migrator.MigrateProfile(shellJson);
                    shell = shellJson.Deserialize<PetProfile>(ProtocolJson.StorageOptions);
                    if (shell is not null) shell.PetId = knownId;
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                {
                    // 성장 데이터와 무관한 필드도 해석할 수 없으면 다른 프로필 셸을 시도한다.
                }
            }
            if (!hasProtection)
            {
                if (requireProtection)
                {
                    failureReason = "보호된 성장 상태 필드(protectedProgressV1)가 없습니다.";
                    throw new JsonException(failureReason);
                }
                return _migrator.MigrateProfile(root);
            }

            try
            {
                if (encrypted is not JsonValue value || !value.TryGetValue<string>(out var blob)
                    || string.IsNullOrEmpty(blob))
                {
                    failureReason = "보호된 성장 상태가 비어 있습니다.";
                    throw new JsonException(failureReason);
                }

                var progress = UnprotectProgressBlob(blob);
                if (progress?.Progress is null || string.IsNullOrEmpty(progress.PetId)
                    || root["petId"] is not JsonValue petId || !petId.TryGetValue<string>(out var id)
                    || !string.Equals(progress.PetId, id, StringComparison.OrdinalIgnoreCase))
                {
                    failureReason = "보호된 성장 상태의 펫 ID가 일치하지 않습니다.";
                    throw new JsonException(failureReason);
                }

                if (captureDocument) document = (JsonObject)root.DeepClone();
                resetReceipt = progress.ResetReceipt;

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
                failureReason = $"보호된 성장 상태를 복호화·검증할 수 없습니다({ex.Message}).";
                throw new JsonException(failureReason, ex);
            }
        }, ct, requireReadableFile: !ignoreTransientReadError).ConfigureAwait(false);
        if (profile is not null) profile.GrowthResetReceipt = resetReceipt;
        return new ProfileRead(ok ? profile : null, migrated, hasProtection, ok ? document : null, ok ? null : failureReason, shell);
    }

    [System.Reflection.Obfuscation(Exclude = false)]
    private async Task SaveProtectedProfileAsync(string path, PetProfile profile, bool markerExists,
        bool recovering, CancellationToken ct)
        => await SaveProtectedProfileAsync(path, profile, markerExists, recovering, ct, refreshBackupFromCurrent: false)
            .ConfigureAwait(false);

    [System.Reflection.Obfuscation(Exclude = false)]
    private async Task SaveProtectedProfileAsync(string path, PetProfile profile, bool markerExists,
        bool recovering, CancellationToken ct, bool refreshBackupFromCurrent)
    {
        JsonObject document;
        JsonObject vaultDocument;
        lock (profile.GrowthSync)
        {
            document = CreateProfileShellDocument(profile);
            var protectedBlob = ProtectProgressBlob(profile.PetId, profile.SoloProgress, profile.GrowthResetReceipt);
            document[ProtectedProgressProperty] = protectedBlob;
            vaultDocument = CreateProtectionVaultDocument(profile.PetId, protectedBlob);
        }

        // 원본과 성장 상태는 한 스냅샷에서 함께 교체한다. 손상된 원본은 검증된 백업을 덮어쓰지 않는다.
        await WriteAtomicCoreAsync(path, document, ct, BackupValidatedProfileAsync,
            keepBackup: markerExists && !recovering && !refreshBackupFromCurrent).ConfigureAwait(false);

        await WriteAtomicCoreAsync(Path.Combine(DataDirectory, ProfileProtectionMarker), vaultDocument, ct, keepBackup: false)
            .ConfigureAwait(false);

        if (!markerExists || refreshBackupFromCurrent)
        {
            await ArchiveIfInvalidBackupAsync(path + ".bak", ct).ConfigureAwait(false);
            await WriteAtomicCoreAsync(path + ".bak", document, ct, keepBackup: false).ConfigureAwait(false);
        }
    }

    [System.Reflection.Obfuscation(Exclude = false)]
    private static string ProtectProgressBlob(string petId, SoloProgress soloProgress, string? resetReceipt)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new ProtectedProgress(petId, soloProgress, resetReceipt),
            ProtocolJson.StorageOptions);
        try
        {
            return Convert.ToBase64String(
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

    [System.Reflection.Obfuscation(Exclude = false)]
    private static ProtectedProgress? UnprotectProgressBlob(string blob)
    {
        var decoded = LocalSecretProtector.UnprotectBytes(Convert.FromBase64String(blob), ProgressEntropy);
        try
        {
            using var document = JsonDocument.Parse(decoded);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("progress", out var growth) || growth.ValueKind != JsonValueKind.Object
                || !growth.TryGetProperty("level", out var level) || level.ValueKind != JsonValueKind.Number || !level.TryGetInt32(out _)
                || !growth.TryGetProperty("coins", out var coins) || coins.ValueKind != JsonValueKind.Number || !coins.TryGetInt64(out _))
            {
                throw new JsonException("봉인 스냅샷에 레벨 또는 코인 필드가 없습니다.");
            }
            return root.Deserialize<ProtectedProgress>(ProtocolJson.StorageOptions);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static JsonObject CreateProtectionVaultDocument(string petId, string protectedBlob) => new()
    {
        [ProtectionVaultFlagProperty] = true,
        ["petId"] = petId,
        [ProtectedProgressProperty] = protectedBlob
    };

    [System.Reflection.Obfuscation(Exclude = false)]
    private async Task WriteProtectionVaultAsync(string markerPath, PetProfile profile, CancellationToken ct)
    {
        JsonObject vaultDocument;
        lock (profile.GrowthSync)
        {
            var protectedBlob = ProtectProgressBlob(profile.PetId, profile.SoloProgress, profile.GrowthResetReceipt);
            vaultDocument = CreateProtectionVaultDocument(profile.PetId, protectedBlob);
        }

        await WriteAtomicCoreAsync(markerPath, vaultDocument, ct, keepBackup: false).ConfigureAwait(false);
    }

    [System.Reflection.Obfuscation(Exclude = false)]
    private async Task<ProtectionVaultState> ReadProtectionVaultAsync(string markerPath, CancellationToken ct)
    {
        if (!File.Exists(markerPath))
        {
            return new ProtectionVaultState(Exists: false, HasSealedBlob: false, Progress: null);
        }

        string? savedPetId = null;
        try
        {
            var text = await ReadAllTextWithRetryAsync(markerPath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new ProtectionVaultState(Exists: true, HasSealedBlob: false, Progress: null,
                    FailureReason: "보호 마커 파일이 비어 있습니다.");
            }

            var node = JsonNode.Parse(text);
            if (node is JsonValue boolValue && boolValue.TryGetValue<bool>(out _))
            {
                return new ProtectionVaultState(Exists: true, HasSealedBlob: false, Progress: null,
                    FailureReason: "구버전 보호 마커(true)에는 봉인 스냅샷이 포함되어 있지 않습니다.");
            }

            if (node is not JsonObject obj)
            {
                return new ProtectionVaultState(Exists: true, HasSealedBlob: false, Progress: null,
                    FailureReason: "보호 마커 형식을 해석할 수 없습니다.");
            }
            if (obj["petId"] is JsonValue idValue && idValue.TryGetValue<string>(out var knownId))
                savedPetId = knownId;

            if (!obj.TryGetPropertyValue(ProtectedProgressProperty, out var encrypted)
                || encrypted is not JsonValue blobValue
                || !blobValue.TryGetValue<string>(out var blob)
                || string.IsNullOrWhiteSpace(blob))
            {
                return new ProtectionVaultState(Exists: true, HasSealedBlob: false, Progress: null,
                    FailureReason: "보호 마커에 봉인된 성장 상태가 없습니다.", PetId: savedPetId);
            }

            var progress = UnprotectProgressBlob(blob);
            if (progress?.Progress is null || string.IsNullOrWhiteSpace(progress.PetId))
            {
                return new ProtectionVaultState(Exists: true, HasSealedBlob: true, Progress: null,
                    FailureReason: "봉인 스냅샷의 성장 상태가 비어 있습니다.", PetId: savedPetId);
            }

            if (obj["petId"] is JsonValue petIdNode
                && petIdNode.TryGetValue<string>(out var expectedPetId)
                && !string.IsNullOrWhiteSpace(expectedPetId)
                && !string.Equals(expectedPetId, progress.PetId, StringComparison.OrdinalIgnoreCase))
            {
                return new ProtectionVaultState(Exists: true, HasSealedBlob: true, Progress: null,
                    FailureReason: "봉인 스냅샷의 펫 ID가 일치하지 않습니다.", PetId: savedPetId);
            }

            return new ProtectionVaultState(Exists: true, HasSealedBlob: true, Progress: progress, PetId: progress.PetId);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return new ProtectionVaultState(Exists: true, HasSealedBlob: true, Progress: null,
                FailureReason: $"봉인 스냅샷을 검증할 수 없습니다({ex.Message}).", PetId: savedPetId);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ProtectionVaultState(Exists: false, HasSealedBlob: false, Progress: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ProtectionVaultState(Exists: true, HasSealedBlob: false, Progress: null, FailureReason: ex.Message, PetId: savedPetId);
        }
    }

    private static string BuildProtectionFailureDetails(ProfileRead primary, ProfileRead backup, ProtectionVaultState vault)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(primary.FailureReason))
            details.Add($"profile.json={primary.FailureReason}");
        if (!string.IsNullOrWhiteSpace(backup.FailureReason))
            details.Add($"profile.json.bak={backup.FailureReason}");
        if (!string.IsNullOrWhiteSpace(vault.FailureReason))
            details.Add($"profile.protection-v1={vault.FailureReason}");
        return details.Count > 0
            ? $"로컬 성장 데이터의 보호 상태를 확인할 수 없습니다. ({string.Join(" / ", details)})"
            : "로컬 성장 데이터의 보호 상태를 확인할 수 없습니다.";
    }

    [System.Reflection.Obfuscation(Exclude = false)]
    private async Task BackupValidatedProfileAsync(string path, string backupPath, CancellationToken ct)
    {
        if (!File.Exists(path)) return;

        var previous = await ReadProfileAsync(path, requireProtection: true, ct, captureDocument: true)
            .ConfigureAwait(false);
        if (previous.Document is not null)
        {
            await ArchiveIfInvalidBackupAsync(backupPath, ct).ConfigureAwait(false);
            await WriteAtomicCoreAsync(backupPath, previous.Document, ct, keepBackup: false).ConfigureAwait(false);
        }
        else
            await ArchiveRejectedProfileAsync(path, ct).ConfigureAwait(false);
    }

    private async Task ArchiveIfInvalidBackupAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return;
        var backup = await ReadProfileAsync(path, requireProtection: true, ct).ConfigureAwait(false);
        if (backup.Profile is null) await ArchiveRejectedProfileAsync(path, ct).ConfigureAwait(false);
    }

    private static Task ArchiveRejectedProfileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return Task.CompletedTask;
        var archive = path + ".rejected-" + Guid.NewGuid().ToString("N");
        return ExecuteFileActionWithRetryAsync(() => File.Copy(path, archive), ct);
    }

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
            var jsonText = await ReadAllTextWithRetryAsync(path, cancellationToken).ConfigureAwait(false);
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

    private static async Task<string> ReadAllTextWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var fs = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    4096,
                    useAsync: true);
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (attempt < FileIoMaxAttempts && IsTransientSharingException(ex))
            {
                await Task.Delay(attempt * 40, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ExecuteFileActionWithRetryAsync(Action action, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException ex) when (attempt < FileIoMaxAttempts && IsTransientSharingException(ex))
            {
                await Task.Delay(attempt * 40, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientSharingException(IOException ex)
        => ex.HResult is ErrorSharingViolationHResult or ErrorLockViolationHResult;

    private async Task WriteAtomicCoreAsync<T>(string targetPath, T payload, CancellationToken cancellationToken,
        Func<string, string, CancellationToken, Task>? writeBackup = null, bool keepBackup = true)
    {
        Directory.CreateDirectory(DataDirectory);

        var tempPath = targetPath + ".tmp";
        var backupPath = targetPath + ".bak";
        var json = JsonSerializer.Serialize(payload, ProtocolJson.StorageOptions);

        // 1. 임시 파일에 UTF-8 기록 후 디스크 플러시
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                await using var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
                break;
            }
            catch (IOException ex) when (attempt < FileIoMaxAttempts && IsTransientSharingException(ex))
            {
                await Task.Delay(attempt * 40, cancellationToken).ConfigureAwait(false);
            }
        }

        // 2. 기존 정상 파일이 존재하면 직전 백업(.bak) 1개 갱신
        if (keepBackup && writeBackup is not null)
        {
            await writeBackup(targetPath, backupPath, cancellationToken).ConfigureAwait(false);
        }
        else if (keepBackup && File.Exists(targetPath))
        {
            await ExecuteFileActionWithRetryAsync(() => File.Copy(targetPath, backupPath, overwrite: true), cancellationToken)
                .ConfigureAwait(false);
        }

        // 3. 임시 파일을 본 파일로 원자적 교체
        await ExecuteFileActionWithRetryAsync(() => File.Move(tempPath, targetPath, overwrite: true), cancellationToken)
            .ConfigureAwait(false);
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

internal sealed class ProfileProtectionException : IOException
{
    public ProfileProtectionException(Exception? inner = null)
        : base("로컬 성장 데이터의 보호 상태를 확인할 수 없습니다.", inner)
    {
    }

    public ProfileProtectionException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
