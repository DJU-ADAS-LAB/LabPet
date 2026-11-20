using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using LabPet.Shared.Protocol;

namespace LabPet.Client.SystemIntegration;

/// <summary>
/// LabPet LAN 방화벽 규칙의 등록 및 보안 강화 상태.
/// </summary>
public enum FirewallRuleStatus
{
    NotRegistered,
    RegisteredLegacy,
    RegisteredHardened
}

/// <summary>
/// LAN P2P 소셜용 Windows 방화벽 인바운드 규칙(UDP 45678~45679, TCP 45680)을
/// 로컬 서브넷(LocalSubnet) 및 LabPet 실행 파일 제한 조건과 함께 UAC 권한 상승(runas)으로
/// 원클릭 등록/제거하는 서비스.
/// </summary>
public static class FirewallRegistrationService
{
    public const string UdpRuleName = "LabPet LAN UDP";
    public const string TcpRuleName = "LabPet LAN TCP";

    private static readonly char[] UnsafeCmdPathChars = ['"', '&', '|', '<', '>', '%', '^', '\r', '\n'];

    /// <summary>
    /// 현재 머신에 LabPet 방화벽 규칙(UDP·TCP 모두)이 등록되어 있는지 확인한다 (비관리자 권한으로도 조회 가능).
    /// </summary>
    public static bool AreRulesRegistered() => GetRuleStatus() != FirewallRuleStatus.NotRegistered;

    /// <summary>
    /// UDP·TCP 방화벽 규칙의 존재 여부와 보안 강화 조건(LocalSubnet, LabPet 실행 파일 제한) 적용 상태를 반환한다.
    /// </summary>
    public static FirewallRuleStatus GetRuleStatus(string? expectedExecutablePath = null)
    {
        if (!TryQueryRuleOutput(UdpRuleName, out var udpOutput) ||
            !TryQueryRuleOutput(TcpRuleName, out var tcpOutput))
        {
            return FirewallRuleStatus.NotRegistered;
        }

        var targetPath = expectedExecutablePath ?? Environment.ProcessPath;
        return IsHardenedRuleOutput(udpOutput, targetPath) && IsHardenedRuleOutput(tcpOutput, targetPath)
            ? FirewallRuleStatus.RegisteredHardened
            : FirewallRuleStatus.RegisteredLegacy;
    }

    /// <summary>
    /// netsh verbose 출력이 로컬 서브넷 제한과 실행 파일 경로 제한을 모두 만족하는지 검증한다.
    /// </summary>
    public static bool IsHardenedRuleOutput(string netshVerboseOutput, string? expectedExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(netshVerboseOutput))
        {
            return false;
        }

        var fields = ParseNetshVerboseFields(netshVerboseOutput);
        if (!TryGetField(fields, out var remoteIp, "RemoteIP", "원격 IP"))
        {
            return false;
        }

        var isLocalSubnet =
            remoteIp.Contains("LocalSubnet", StringComparison.OrdinalIgnoreCase) ||
            remoteIp.Contains("로컬 서브넷", StringComparison.OrdinalIgnoreCase);

        if (!isLocalSubnet)
        {
            return false;
        }

        var normalizedExpected = NormalizeExecutablePath(expectedExecutablePath);
        if (normalizedExpected is null)
        {
            return true;
        }

        if (!TryGetField(fields, out var programValue, "Program", "프로그램"))
        {
            return false;
        }

        var normalizedActual = NormalizeExecutablePath(programValue);
        return normalizedActual is not null &&
               string.Equals(normalizedActual, normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// UAC 권한 상승으로 실행할 netsh 방화벽 규칙 등록 명령 문자열을 생성한다.
    /// 기존 동일 이름 규칙을 삭제한 뒤 LocalSubnet 및 현재 실행 파일 제한이 적용된 UDP/TCP 규칙을 등록한다.
    /// </summary>
    public static string BuildNetshRegisterCommand(string? executablePath)
    {
        var normalizedPath = NormalizeExecutablePath(executablePath);
        var programArg = normalizedPath is not null ? $" program=\"{normalizedPath}\"" : string.Empty;

        return
            $"netsh advfirewall firewall delete rule name=\"{UdpRuleName}\" >nul 2>&1 & " +
            $"netsh advfirewall firewall delete rule name=\"{TcpRuleName}\" >nul 2>&1 & " +
            $"netsh advfirewall firewall add rule name=\"{UdpRuleName}\" dir=in action=allow protocol=UDP localport={ProtocolConstants.UdpDiscoveryPort},{ProtocolConstants.UdpMusicFramePort} profile=any remoteip=localsubnet{programArg} & " +
            $"netsh advfirewall firewall add rule name=\"{TcpRuleName}\" dir=in action=allow protocol=TCP localport={ProtocolConstants.TcpControlPort} profile=any remoteip=localsubnet{programArg}";
    }

    /// <summary>
    /// 관리자 권한 PowerShell에서 직접 실행할 수 있는 방화벽 규칙 등록 명령 문자열을 생성한다.
    /// </summary>
    public static string BuildPowerShellRegisterCommand(string? executablePath)
    {
        var normalizedPath = NormalizeExecutablePath(executablePath);
        var programArg = normalizedPath is not null
            ? $" -Program '{normalizedPath.Replace("'", "''")}'"
            : string.Empty;

        return
            $"Remove-NetFirewallRule -DisplayName '{UdpRuleName}','{TcpRuleName}' -ErrorAction SilentlyContinue; " +
            $"New-NetFirewallRule -DisplayName '{UdpRuleName}' -Direction Inbound -Profile Any -RemoteAddress LocalSubnet{programArg} -Protocol UDP -LocalPort {ProtocolConstants.UdpDiscoveryPort},{ProtocolConstants.UdpMusicFramePort} -Action Allow; " +
            $"New-NetFirewallRule -DisplayName '{TcpRuleName}' -Direction Inbound -Profile Any -RemoteAddress LocalSubnet{programArg} -Protocol TCP -LocalPort {ProtocolConstants.TcpControlPort} -Action Allow";
    }

    /// <summary>
    /// UAC 권한 상승을 요청해 LabPet LAN UDP/TCP 인바운드 규칙(profile=any, remoteip=localsubnet, program 제한)을 등록한다.
    /// 사용자가 UAC를 취소하거나 실패해도 예외를 던지지 않고 false를 반환한다.
    /// </summary>
    public static bool TryRegisterRulesElevated()
    {
        try
        {
            var cmd = BuildNetshRegisterCommand(Environment.ProcessPath);

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
            if (!TryQueryRuleOutput(UdpRuleName, out _) && !TryQueryRuleOutput(TcpRuleName, out _))
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

    private static bool TryQueryRuleOutput(string ruleName, out string output)
    {
        output = string.Empty;
        try
        {
            var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{ruleName}\" verbose")
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

            output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var trimmed = executablePath.Trim();
        if (trimmed.IndexOfAny(UnsafeCmdPathChars) >= 0)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ParseNetshVerboseFields(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;
        var currentValue = new StringBuilder();

        void FlushCurrent()
        {
            if (currentKey is not null)
            {
                result[currentKey] = currentValue.ToString().Trim();
            }
        }

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0 ||
                trimmed.StartsWith("---", StringComparison.Ordinal) ||
                string.Equals(trimmed, "Ok.", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "확인.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TrySplitFieldLine(rawLine, out var key, out var value))
            {
                FlushCurrent();
                currentKey = key;
                currentValue.Clear();
                currentValue.Append(value);
            }
            else if (currentKey is not null)
            {
                // netsh verbose 출력은 긴 Program 경로를 80컬럼 부근에서 줄바꿈하므로 직전 필드에 이어붙인다.
                currentValue.Append(trimmed);
            }
        }

        FlushCurrent();
        return result;
    }

    private static bool TrySplitFieldLine(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var colonIndex = line.IndexOf(':');
        if (colonIndex <= 0)
        {
            return false;
        }

        var candidateKey = line[..colonIndex].Trim();
        if (candidateKey.Length == 0 || candidateKey.Contains('\\') || candidateKey.Contains('/'))
        {
            return false;
        }

        // 드라이브 문자(예: C:\...)로 시작하는 줄바꿈 연속 경로는 필드로 취급하지 않는다.
        if (candidateKey.Length == 1 && char.IsLetter(candidateKey[0]) &&
            colonIndex + 1 < line.Length && (line[colonIndex + 1] == '\\' || line[colonIndex + 1] == '/'))
        {
            return false;
        }

        key = candidateKey;
        value = line[(colonIndex + 1)..].Trim();
        return true;
    }

    private static bool TryGetField(Dictionary<string, string> fields, out string value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var found))
            {
                value = found;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
