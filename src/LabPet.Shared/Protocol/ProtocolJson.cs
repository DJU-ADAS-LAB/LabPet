using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabPet.Shared.Protocol;

/// <summary>
/// 마이너 업데이트 간 하위/상위 호환성을 유지하기 위한 공용 JSON 직렬화 옵션.
/// </summary>
public static class ProtocolJson
{
    /// <summary>
    /// 네트워크 패킷 직렬화 옵션 (알 수 없는 필드 무시, camelCase, 대소문자 구분 없음).
    /// </summary>
    public static JsonSerializerOptions PacketOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// 로컬 AppData JSON 파일 저장 옵션 (가독성 있는 들여쓰기 + 미지원 필드 무시).
    /// </summary>
    public static JsonSerializerOptions StorageOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
