using System.Text.Json.Nodes;

namespace LabPet.Client.Storage;

/// <summary>
/// 저장된 JSON 문서의 schemaVersion을 현재 앱 스키마 버전으로 안전하게 승격하는 마이그레이터.
/// </summary>
public interface IStateMigrator
{
    /// <summary>
    /// 프로필 JSON 노드를 현재 스키마 버전으로 마이그레이션한다.
    /// 변경이 발생했으면 true를 반환한다.
    /// </summary>
    bool MigrateProfile(JsonObject rootNode);

    /// <summary>
    /// 친구 목록 JSON 노드를 현재 스키마 버전으로 마이그레이션한다.
    /// 변경이 발생했으면 true를 반환한다.
    /// </summary>
    bool MigrateFriends(JsonObject rootNode);
}
