using Velopack.Sources;

namespace LabPet.Client.Updates;

public enum UpdateProviderKind
{
    /// <summary>GitHub Releases 기반 자동 업데이트 (기본값).</summary>
    GitHub = 0,

    /// <summary>GCP Cloud Storage 정적 버킷 등 HTTP 정적 호스팅 기반 업데이트.</summary>
    SimpleWeb = 1
}

/// <summary>
/// 업데이트 소스 설정. Provider를 SimpleWeb으로 바꾸면 GCP Cloud Storage 등으로 무중단 전환 가능하다.
/// </summary>
public sealed class UpdateSourceOptions
{
    public UpdateProviderKind Provider { get; set; } = UpdateProviderKind.GitHub;

    /// <summary>GitHub 저장소 URL (예: https://github.com/OWNER/labpet).</summary>
    public string GitHubRepoUrl { get; set; } = "https://github.com/DJU-ADAS-LAB/LabPet";

    /// <summary>비공개 저장소 접근 토큰 (공개 저장소는 null).</summary>
    public string? GitHubAccessToken { get; set; }

    /// <summary>프리릴리스 포함 여부.</summary>
    public bool IncludePrerelease { get; set; }

    /// <summary>SimpleWebSource용 정적 버킷 기본 URL (예: https://storage.googleapis.com/labpet-releases/win-x64).</summary>
    public string SimpleWebFeedUrl { get; set; } = string.Empty;

    public IUpdateSource CreateUpdateSource()
    {
        return Provider switch
        {
            UpdateProviderKind.SimpleWeb when !string.IsNullOrWhiteSpace(SimpleWebFeedUrl)
                => new SimpleWebSource(SimpleWebFeedUrl),
            _ => new GithubSource(GitHubRepoUrl, GitHubAccessToken, IncludePrerelease)
        };
    }
}
