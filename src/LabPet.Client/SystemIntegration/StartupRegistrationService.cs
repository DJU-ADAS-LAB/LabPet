using System.IO;
using Microsoft.Win32;

namespace LabPet.Client.SystemIntegration;

/// <summary>
/// Windows 시작 프로그램 자동 실행(HKCU\Software\Microsoft\Windows\CurrentVersion\Run) 관리 서비스.
/// Velopack 설치 환경에서는 고정 current 경로(%LocalAppData%\LabPet\current\LabPet.Client.exe)가 등록되어
/// 업데이트 이후에도 자동 실행이 끊어지지 않는다.
/// </summary>
public sealed class StartupRegistrationService
{
    public const string AutoStartArgument = "--autostart";

    private const string RunRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryValueName = "LabPet";

    public bool IsEnabled()
    {
        return !string.IsNullOrWhiteSpace(GetRegisteredCommand());
    }

    public string? GetRegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKeyPath, writable: false);
            return key?.GetValue(AppRegistryValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 앱 시작 시 자동 실행 레지스트리를 최신화한다.
    /// 이미 Velopack 설치본이 등록되어 있는 상태에서 포터블·로컬 개발 빌드가 실행된 경우에는
    /// 기존 설치본 자동 실행 경로를 덮어쓰지 않고 보존한다.
    /// </summary>
    public bool RefreshForStartup(string? currentExePath = null)
    {
        var resolvedCurrent = string.IsNullOrWhiteSpace(currentExePath)
            ? Environment.ProcessPath
            : currentExePath;
        if (ShouldPreserveInstalledRegistration(GetRegisteredCommand(), resolvedCurrent))
        {
            return false;
        }

        return SetEnabled(true);
    }

    internal static bool ShouldPreserveInstalledRegistration(
        string? existingCommand,
        string? currentExePath,
        Func<string, bool>? isInstalledExe = null)
    {
        var existingExe = ExtractExecutablePath(existingCommand);
        if (string.IsNullOrWhiteSpace(existingExe) || string.IsNullOrWhiteSpace(currentExePath))
        {
            return false;
        }

        var checker = isInstalledExe ?? IsVelopackInstalledExecutable;
        return checker(existingExe) && !checker(currentExePath);
    }

    internal static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);
            return closing > 1 ? trimmed[1..closing].Trim() : null;
        }

        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space].Trim() : trimmed;
    }

    internal static bool IsVelopackInstalledExecutable(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        try
        {
            if (!File.Exists(exePath))
            {
                return false;
            }

            var currentDir = Path.GetDirectoryName(Path.GetFullPath(exePath));
            var rootDir = currentDir is null ? null : Directory.GetParent(currentDir)?.FullName;
            return !string.IsNullOrWhiteSpace(rootDir)
                && File.Exists(Path.Combine(rootDir, "Update.exe"));
        }
        catch
        {
            return false;
        }
    }

    public bool SetEnabled(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunRegistryKeyPath, writable: true);

            if (key is null)
            {
                return false;
            }

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return false;
                }

                // Windows 부팅 자동 실행 시 무인 백그라운드 업데이트를 구분하기 위해 --autostart 인자 포함
                key.SetValue(AppRegistryValueName, $"\"{exePath}\" {AutoStartArgument}");
            }
            else
            {
                key.DeleteValue(AppRegistryValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
