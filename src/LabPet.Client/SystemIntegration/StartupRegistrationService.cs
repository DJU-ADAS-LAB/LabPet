using Microsoft.Win32;

namespace LabPet.Client.SystemIntegration;

/// <summary>
/// Windows 시작 프로그램 자동 실행(HKCU\Software\Microsoft\Windows\CurrentVersion\Run) 관리 서비스.
/// Velopack 설치 환경에서는 고정 current 경로(%LocalAppData%\LabPet\current\LabPet.Client.exe)가 등록되어
/// 업데이트 이후에도 자동 실행이 끊어지지 않는다.
/// </summary>
public sealed class StartupRegistrationService
{
    private const string RunRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryValueName = "LabPet";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKeyPath, writable: false);
            var existing = key?.GetValue(AppRegistryValueName) as string;
            return !string.IsNullOrWhiteSpace(existing);
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

                key.SetValue(AppRegistryValueName, $"\"{exePath}\"");
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
