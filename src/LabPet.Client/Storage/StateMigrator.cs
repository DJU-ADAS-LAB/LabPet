using System.Text.Json.Nodes;
using LabPet.Shared.Protocol;

namespace LabPet.Client.Storage;

/// <summary>
/// schemaVersion 기반 순차 마이그레이터.
/// 향후 스키마 버전이 올라갈 때(v1 -> v2 -> ...) 단계별 변환을 이곳에 추가한다.
/// </summary>
public sealed class StateMigrator : IStateMigrator
{
    public bool MigrateProfile(JsonObject rootNode)
    {
        var version = ReadSchemaVersion(rootNode);
        var migrated = false;

        // schemaVersion이 누락된 초기/손상 파일은 v1 기준 기본 구조로 승격
        if (version < 1)
        {
            if (rootNode["petId"] is null || string.IsNullOrWhiteSpace(rootNode["petId"]?.GetValue<string>()))
            {
                rootNode["petId"] = Guid.NewGuid().ToString("D");
            }

            if (rootNode["appearance"] is null)
            {
                rootNode["appearance"] = new JsonObject();
            }

            if (rootNode["soloProgress"] is null)
            {
                rootNode["soloProgress"] = new JsonObject();
            }

            if (rootNode["syncMetadata"] is null)
            {
                rootNode["syncMetadata"] = new JsonObject();
            }

            version = 1;
            migrated = true;
        }

        if (version < 2)
        {
            MigrateProfileToV2(rootNode);
            version = 2;
            migrated = true;
        }

        if (version != ProtocolConstants.CurrentStorageSchemaVersion && version < ProtocolConstants.CurrentStorageSchemaVersion)
        {
            version = ProtocolConstants.CurrentStorageSchemaVersion;
            migrated = true;
        }

        if (migrated)
        {
            rootNode["schemaVersion"] = version;
        }

        return migrated;
    }

    public bool MigrateFriends(JsonObject rootNode)
    {
        var version = ReadSchemaVersion(rootNode);
        var migrated = false;

        if (version < 1)
        {
            if (rootNode["friends"] is null)
            {
                rootNode["friends"] = new JsonArray();
            }

            version = 1;
            migrated = true;
        }

        if (version < 2)
        {
            version = 2;
            migrated = true;
        }

        if (NormalizeFriendsEntries(rootNode))
        {
            migrated = true;
        }

        if (version != ProtocolConstants.CurrentStorageSchemaVersion && version < ProtocolConstants.CurrentStorageSchemaVersion)
        {
            version = ProtocolConstants.CurrentStorageSchemaVersion;
            migrated = true;
        }

        if (migrated)
        {
            rootNode["schemaVersion"] = version;
        }

        return migrated;
    }

    private static void MigrateProfileToV2(JsonObject rootNode)
    {
        if (!TryGetPropertyIgnoreCase(rootNode, "soloProgress", out var soloNode) || soloNode is not JsonObject soloProgress)
        {
            soloProgress = new JsonObject();
            rootNode["soloProgress"] = soloProgress;
        }

        // v1 -> v2 1회성 마이그레이션: 레벨 1, XP 0으로 초기화하고 코인/누적 통계/아이템은 보존
        SetNormalizedProperty(soloProgress, "level", 1);
        SetNormalizedProperty(soloProgress, "xp", 0L);

        if (TryGetPropertyIgnoreCase(soloProgress, "coins", out var coinsNode)
            && coinsNode is JsonValue coinsVal
            && coinsVal.TryGetValue<long>(out var coins)
            && coins < 0)
        {
            SetNormalizedProperty(soloProgress, "coins", 0L);
        }

        // 기존 누적 활동량에 대한 소급 코인 지급이 발생하지 않도록 보상용 잔여 카운터는 0으로 시작
        SetNormalizedProperty(soloProgress, "pendingKeystrokesForReward", 0);
        SetNormalizedProperty(soloProgress, "pendingMouseClicksForReward", 0);
        SetNormalizedProperty(soloProgress, "pendingUptimeMinutesForReward", 0);
        SetNormalizedProperty(soloProgress, "dailySocialRewardCoinsEarned", 0);
    }

    private static bool NormalizeFriendsEntries(JsonObject rootNode)
    {
        if (!TryGetPropertyIgnoreCase(rootNode, "friends", out var friendsNode) || friendsNode is not JsonArray friendsArray)
        {
            return false;
        }

        var changed = false;
        foreach (var item in friendsArray)
        {
            if (item is not JsonObject friendObj)
            {
                continue;
            }

            if (TryGetPropertyIgnoreCase(friendObj, "intimacy", out var intimacyNode, out var actualKey)
                && intimacyNode is JsonValue intimacyValue
                && intimacyValue.TryGetValue<int>(out var rawIntimacy))
            {
                var clamped = Math.Clamp(rawIntimacy, 0, 9999);
                if (clamped != rawIntimacy)
                {
                    friendObj[actualKey!] = clamped;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private static bool TryGetPropertyIgnoreCase(JsonObject obj, string camelName, out JsonNode? node)
        => TryGetPropertyIgnoreCase(obj, camelName, out node, out _);

    private static bool TryGetPropertyIgnoreCase(JsonObject obj, string camelName, out JsonNode? node, out string? actualKey)
    {
        foreach (var kvp in obj)
        {
            if (string.Equals(kvp.Key, camelName, StringComparison.OrdinalIgnoreCase))
            {
                actualKey = kvp.Key;
                node = kvp.Value;
                return true;
            }
        }

        actualKey = null;
        node = null;
        return false;
    }

    private static void SetNormalizedProperty(JsonObject obj, string camelName, JsonNode value)
    {
        if (TryGetPropertyIgnoreCase(obj, camelName, out _, out var existingKey)
            && !string.Equals(existingKey, camelName, StringComparison.Ordinal))
        {
            obj.Remove(existingKey!);
        }

        obj[camelName] = value;
    }

    private static int ReadSchemaVersion(JsonObject rootNode)
    {
        if (rootNode.TryGetPropertyValue("schemaVersion", out var node) && node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }
}
