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
