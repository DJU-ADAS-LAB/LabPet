using System.ComponentModel;
using System.Diagnostics;
using LabPet.Shared.Protocol;

namespace LabPet.Client.SystemIntegration;

/// <summary>
/// LAN P2P 소셜용 Windows 방화벽 인바운드 규칙(UDP 45678~45679, TCP 45680)을
/// UAC 권한 상승(runas)으로 원클릭 등록/제거하는 서비스.
/// </summary>
public static class FirewallRegistrationService
{
    public const string UdpRuleName = "LabPet LAN UDP";
    public const string TcpRuleName = "LabPet LAN TCP";

    /// <summary>
    /// 현재 머신에 LabPet 방화벽 규칙이 이미 등록되어 있는지 확인한다 (비관리자 권한으로도 조회 가능).
    /// </summary>
    public static bool AreRulesRegistered()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{TcpRuleName}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            proc.WaitForExit(2000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// UAC 권한 상승을 요청해 LabPet LAN UDP/TCP 인바운드 규칙(profile=any)을 등록한다.
    /// 사용자가 UAC를 취소하거나 실패해도 예외를 던지지 않고 false를 반환한다.
    /// </summary>
    public static bool TryRegisterRulesElevated()
    {
        try
        {
            // 중복 규칙을 방지하기 위해 기존 동일 이름 규칙 삭제 후 새로 추가
            var cmd =
                $"netsh advfirewall firewall delete rule name=\"{UdpRuleName}\" >nul 2>&1 & " +
                $"netsh advfirewall firewall delete rule name=\"{TcpRuleName}\" >nul 2>&1 & " +
                $"netsh advfirewall firewall add rule name=\"{UdpRuleName}\" dir=in action=allow protocol=UDP localport={ProtocolConstants.UdpDiscoveryPort},{ProtocolConstants.UdpMusicFramePort} profile=any & " +
                $"netsh advfirewall firewall add rule name=\"{TcpRuleName}\" dir=in action=allow protocol=TCP localport={ProtocolConstants.TcpControlPort} profile=any";

            var psi = new ProcessStartInfo("cmd.exe", $"/c {cmd}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            proc.WaitForExit(15000);
            return proc.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // 사용자가 UAC 권한 상승을 취소함
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 앱 제거 시 기존 방화벽 규칙 정리를 시도한다 (실패 시 조용히 무시).
    /// </summary>
    public static void TryRemoveRulesSilently()
    {
        try
        {
            if (!AreRulesRegistered())
            {
                return;
            }

            var cmd =
                $"netsh advfirewall firewall delete rule name=\"{UdpRuleName}\" >nul 2>&1 & " +
                $"netsh advfirewall firewall delete rule name=\"{TcpRuleName}\" >nul 2>&1";

            var psi = new ProcessStartInfo("cmd.exe", $"/c {cmd}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(8000);
        }
        catch
        {
            // 제거 과정은 방해하지 않음
        }
    }
}
