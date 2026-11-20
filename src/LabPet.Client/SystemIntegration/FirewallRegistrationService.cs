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
        return IsHardenedRuleOutput(udpOutput, targetPath, UdpRuleName) && IsHardenedRuleOutput(tcpOutput, targetPath, TcpRuleName)
            ? FirewallRuleStatus.RegisteredHardened
            : FirewallRuleStatus.RegisteredLegacy;
    }

    /// <summary>
    /// netsh verbose 출력이 로컬 서브넷 제한과 실행 파일 경로 제한을 모두 만족하는지 검증한다.
    /// </summary>
    public static bool IsHardenedRuleOutput(string netshVerboseOutput, string? expectedExecutablePath, string expectedRuleName)
    {
        var normalizedExpected = NormalizeExecutablePath(expectedExecutablePath);
        if (string.IsNullOrWhiteSpace(netshVerboseOutput) || normalizedExpected is null)
        {
            return false;
        }

        var definitions = netshVerboseOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Count(line => TrySplitFieldLine(line, out var key, out _) &&
                key is "Rule Name" or "규칙 이름");
        if (definitions != 1)
        {
            return false;
        }

        var fields = ParseNetshVerboseFields(netshVerboseOutput);
        if (!TryGetField(fields, out var name, "Rule Name", "규칙 이름") || name != expectedRuleName ||
            !TryGetField(fields, out var enabled, "Enabled", "사용", "사용 여부") || !EqualsAny(enabled, "Yes", "예") ||
            !TryGetField(fields, out var direction, "Direction", "방향") || !EqualsAny(direction, "In", "Inbound", "인바운드") ||
            !TryGetField(fields, out var action, "Action", "작업", "동작") || !EqualsAny(action, "Allow", "허용") ||
            !TryGetField(fields, out var remoteIp, "RemoteIP", "원격 IP") || !EqualsAny(remoteIp, "LocalSubnet", "로컬 서브넷") ||
            !TryGetField(fields, out var protocol, "Protocol", "프로토콜") ||
            !TryGetField(fields, out var ports, "LocalPort", "로컬 포트") ||
            !TryGetField(fields, out var programValue, "Program", "프로그램"))
        {
            return false;
        }

        var normalizedActual = NormalizeExecutablePath(programValue);
        var usesExpectedPorts = expectedRuleName switch
        {
            UdpRuleName => EqualsAny(protocol, "UDP")
                && (ports == $"{ProtocolConstants.UdpDiscoveryPort}-{ProtocolConstants.UdpMusicFramePort}"
                    || ports.Split(',', StringSplitOptions.TrimEntries).Order().SequenceEqual(
                        new[] { ProtocolConstants.UdpDiscoveryPort.ToString(), ProtocolConstants.UdpMusicFramePort.ToString() })),
            TcpRuleName => EqualsAny(protocol, "TCP") && ports == ProtocolConstants.TcpControlPort.ToString(),
            _ => false
        };
        return usesExpectedPorts && normalizedActual is not null &&
               string.Equals(normalizedActual, normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildPowerShellRegisterCommand(string? executablePath)
    {
        var normalizedPath = NormalizeExecutablePath(executablePath)
            ?? throw new ArgumentException("LabPet 실행 파일 경로를 확인할 수 없습니다.", nameof(executablePath));
        var program = normalizedPath.Replace("'", "''");
        // 두 새 규칙이 모두 생성되기 전에는 기존 규칙을 보존한다.
        return
            "$ErrorActionPreference = 'Stop'; " +
            $"$old = @(Get-NetFirewallRule | Where-Object {{ $_.DisplayName -in @('{UdpRuleName}', '{TcpRuleName}') }}); " +
            "$new = @(); try { " +
            $"$new += New-NetFirewallRule -DisplayName '{UdpRuleName}' -Direction Inbound -Profile Any -RemoteAddress LocalSubnet -Program '{program}' -Protocol UDP -LocalPort {ProtocolConstants.UdpDiscoveryPort},{ProtocolConstants.UdpMusicFramePort} -Action Allow; " +
            $"$new += New-NetFirewallRule -DisplayName '{TcpRuleName}' -Direction Inbound -Profile Any -RemoteAddress LocalSubnet -Program '{program}' -Protocol TCP -LocalPort {ProtocolConstants.TcpControlPort} -Action Allow; " +
            "} catch { $new | Remove-NetFirewallRule -ErrorAction SilentlyContinue; throw }; " +
            "if ($old.Count -gt 0) { $old | Remove-NetFirewallRule }";
    }

    public static bool TryRegisterRulesElevated() => TryRegisterRulesElevated(out _);

    public static bool TryRegisterRulesElevated(out string error)
    {
        const string failed = "Windows 방화벽에서 두 규칙을 모두 확인하지 못했습니다.";
        try
        {
            var script = BuildPowerShellRegisterCommand(Environment.ProcessPath);
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encoded}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var process = Process.Start(psi);
            if (process is null || !process.WaitForExit(15000))
            {
                error = "방화벽 등록 작업의 완료를 확인하지 못했습니다.";
                return false;
            }
            if (process.ExitCode != 0 || GetRuleStatus() != FirewallRuleStatus.RegisteredHardened)
            {
                error = failed;
                return false;
            }
            error = string.Empty;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Win32Exception)
        {
            error = "방화벽 명령을 시작하지 못했습니다. 관리자 권한 허용 여부를 확인해 주세요.";
            return false;
        }
        catch (InvalidOperationException)
        {
            error = failed;
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
        if (trimmed.IndexOfAny(['"', '\r', '\n']) >= 0 || !Path.IsPathFullyQualified(trimmed))
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

    private static bool EqualsAny(string value, params string[] choices)
        => choices.Contains(value, StringComparer.OrdinalIgnoreCase);
}
