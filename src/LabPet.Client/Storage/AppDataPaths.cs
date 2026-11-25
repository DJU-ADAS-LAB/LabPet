using System.IO;

namespace LabPet.Client.Storage;

/// <summary>
/// 빌드 구성(Debug 개발 빌드 vs Release 배포 빌드)에 따라 로컬 데이터·로그 디렉터리와
/// 단일 인스턴스 뮤텍스 이름을 일관되게 결정한다.
/// Debug 빌드에서는 설치판(%AppData%\LabPet)과 분리된 %AppData%\LabPet-Dev를 사용해
/// 로컬 테스트 중 설치판 프로필이나 보호 마커를 덮어쓰지 않도록 차단한다.
/// </summary>
internal static class AppDataPaths
{
#if DEBUG
    internal const string DefaultDirectoryName = "LabPet-Dev";
    internal const string SingleInstanceMutexName = "Local\\LabPet_SingleInstance_Mutex_Dev";
#else
    internal const string DefaultDirectoryName = "LabPet";
    internal const string SingleInstanceMutexName = "Local\\LabPet_SingleInstance_Mutex";
#endif

    internal static string ResolveDefaultDataDirectory(string? appDataRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(appDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : appDataRoot;
        return Path.Combine(root, DefaultDirectoryName);
    }

    internal static string ResolveDefaultLogDirectory(string? appDataRoot = null)
        => Path.Combine(ResolveDefaultDataDirectory(appDataRoot), "logs");
}
