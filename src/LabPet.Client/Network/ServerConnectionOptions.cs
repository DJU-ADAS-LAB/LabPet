using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LabPet.Client.Storage;
using LabPet.Shared.Protocol;

namespace LabPet.Client.Network;

/// <summary>
/// GCP SignalR 서버 접속 설정 (`LABPET_SERVER_URL` 환경변수 → `%AppData%\LabPet\server.json` → 릴리스 빌드 기본값 `LabPetDefaultServerUrl`).
/// 모든 경로에 설정이 없거나 `server.json`에 `enabled: false`가 지정되면 원격 서버 연결을 비활성화하고 기존 LAN P2P만 사용한다.
/// </summary>
public sealed class ServerConnectionOptions
{
    public const string ConfigFileName = "server.json";
    public const string ServerUrlEnvironmentVariable = "LABPET_SERVER_URL";
    public const string BundledServerUrlMetadataKey = "LabPetDefaultServerUrl";

    public bool Enabled { get; set; }
    public string ServerUrl { get; set; } = string.Empty;

    public bool IsConfigured => Enabled && TryGetBaseUri(out _);

    public bool TryGetBaseUri(out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(ServerUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    public static string? GetBundledDefaultServerUrl()
        => typeof(ServerConnectionOptions).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attr => string.Equals(attr.Key, BundledServerUrlMetadataKey, StringComparison.Ordinal))
            ?.Value
            ?.Trim();

    public static ServerConnectionOptions Load(IAppDataStore store)
        => Load(
            store.DataDirectory,
            Environment.GetEnvironmentVariable(ServerUrlEnvironmentVariable),
            GetBundledDefaultServerUrl());

    internal static ServerConnectionOptions Load(
        string dataDirectory,
        string? envServerUrl = null,
        string? bundledDefaultServerUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(envServerUrl))
        {
            return new ServerConnectionOptions
            {
                Enabled = true,
                ServerUrl = envServerUrl.Trim()
            };
        }

        var path = Path.Combine(dataDirectory, ConfigFileName);
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var parsed = JsonSerializer.Deserialize<ServerConnectionOptions>(json, ProtocolJson.StorageOptions);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch
            {
                // 손상된 설정 파일은 무시하고 아래 기본값으로 폴백
            }
        }

        if (!string.IsNullOrWhiteSpace(bundledDefaultServerUrl))
        {
            return new ServerConnectionOptions
            {
                Enabled = true,
                ServerUrl = bundledDefaultServerUrl.Trim()
            };
        }

        return new ServerConnectionOptions { Enabled = false };
    }
}
