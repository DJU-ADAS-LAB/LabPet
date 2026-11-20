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
