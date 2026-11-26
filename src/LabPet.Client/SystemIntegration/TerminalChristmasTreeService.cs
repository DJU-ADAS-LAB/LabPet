using System.Diagnostics;
using System.IO;
using System.Text;
using LabPet.Client.Storage;

namespace LabPet.Client.SystemIntegration;

/// <summary>
/// 외부 PowerShell이나 임시 .ps1 스크립트 없이 LabPet.Client.exe 내장 C# 3D ASCII 콘솔 모드(--christmas-tree)를 실행한다.
/// </summary>
public sealed class TerminalChristmasTreeService
{
    public const string NativeCliFlag = "--christmas-tree";
    public const string DefaultPetName = "LabPet";
    public const int MaxPetNameLength = 32;
    public const int DefaultWindowColumns = 164;
    public const int DefaultWindowRows = 48;
    public const short DefaultFontPixelHeight = 11;

    public TerminalChristmasTreeService()
    {
    }

    public TerminalChristmasTreeService(IAppDataStore? _)
    {
    }

    internal TerminalChristmasTreeService(string _)
    {
    }

    /// <summary>
    /// 펫 이름에서 제어문자(개행·탭·ANSI ESC 등)를 제거하고 길이를 제한해 터미널 출력/인자 전달에 안전한 문자열로 정제한다.
    /// </summary>
    public static string SanitizePetName(string? petName)
    {
        if (string.IsNullOrWhiteSpace(petName))
        {
            return DefaultPetName;
        }

        var sb = new StringBuilder(Math.Min(petName.Length, MaxPetNameLength));
        foreach (var ch in petName)
        {
            if (char.IsControl(ch))
            {
                continue;
            }

            sb.Append(ch);
        }

        var cleaned = sb.ToString().Trim();
        if (cleaned.Length == 0)
        {
            return DefaultPetName;
        }

        return cleaned.Length <= MaxPetNameLength
            ? cleaned
            : cleaned[..MaxPetNameLength].TrimEnd();
    }

    /// <summary>
    /// Windows 시스템 디렉터리(System32)의 conhost.exe 절대경로를 반환한다. 없으면 null을 반환한다.
    /// </summary>
    public static string? ResolveConsoleHostPath()
    {
        var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrWhiteSpace(systemDir))
        {
            var conhostPath = Path.Combine(systemDir, "conhost.exe");
            if (File.Exists(conhostPath))
            {
                return conhostPath;
            }
        }

        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(winDir))
        {
            var fallbackPath = Path.Combine(winDir, "System32", "conhost.exe");
            if (File.Exists(fallbackPath))
            {
                return fallbackPath;
            }
        }

        return null;
    }

    /// <summary>
    /// 외부 PowerShell이나 .ps1 스크립트 없이 LabPet.Client.exe 내장 C# 엔진(--christmas-tree)을 실행하는 ProcessStartInfo를 생성한다.
    /// 시스템 conhost.exe 또는 실행 파일 절대경로를 직접 시작하고 작업 디렉터리와 ArgumentList를 명시하여 셸 해석을 차단한다.
    /// </summary>
    public static ProcessStartInfo BuildNativeLaunchStartInfo(
        string clientExePath,
        string? petName,
        string? consoleHostPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientExePath);
        if (!Path.IsPathFullyQualified(clientExePath))
        {
            throw new ArgumentException("실행 파일 경로는 절대경로여야 합니다.", nameof(clientExePath));
        }

        var fullClientPath = Path.GetFullPath(clientExePath);
        var workingDirectory = Path.GetDirectoryName(fullClientPath);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        }

        var useConsoleHost = !string.IsNullOrWhiteSpace(consoleHostPath);
        string launchFile;
        if (useConsoleHost)
        {
            if (!Path.IsPathFullyQualified(consoleHostPath!))
            {
                throw new ArgumentException("콘솔 호스트 경로는 절대경로여야 합니다.", nameof(consoleHostPath));
            }

            launchFile = Path.GetFullPath(consoleHostPath!);
        }
        else
        {
            launchFile = fullClientPath;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = launchFile,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };
        if (useConsoleHost)
        {
            startInfo.ArgumentList.Add(fullClientPath);
        }

        startInfo.ArgumentList.Add(NativeCliFlag);
        startInfo.ArgumentList.Add(SanitizePetName(petName));
        return startInfo;
    }

    /// <summary>
    /// 현재 프로세스의 LabPet.Client.exe 실행 파일 경로를 확인한다. (dotnet test 호스트 등에서는 null 반환)
    /// </summary>
    public static string? ResolveCurrentClientExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && File.Exists(processPath)
            && string.Equals(Path.GetFileName(processPath), "LabPet.Client.exe", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, "LabPet.Client.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// LabPet.Client.exe --christmas-tree [PetName] 진입점에서 호출되어 PowerShell 없이 내장 C# 크리스마스 트리 엔진을 즉시 실행한다.
    /// </summary>
    public static int RunNativeConsoleMode(IReadOnlyList<string> args)
    {
        try
        {
            using var instance = new ChristmasTreeInstance();
            if (!instance.IsOwner)
            {
                if (instance.TryActivateExisting())
                {
                    ChristmasTreeStartup.SignalReady();
                    return 0;
                }

                ChristmasTreeStartup.SignalFailure();
                return 1;
            }

            var result = RunNativeConsoleModeCore(args, instance);
            if (result != 0)
            {
                ChristmasTreeStartup.SignalFailure();
            }

            return result;
        }
        catch (Exception ex)
        {
            try { ChristmasTreeStartup.SignalFailure(); } catch { }
            try { Console.Error.WriteLine("크리스마스트리 초기화 실패: " + ex.Message); } catch { }
            return 1;
        }
    }

    private static int RunNativeConsoleModeCore(IReadOnlyList<string> args, ChristmasTreeInstance instance)
    {
        string? rawName = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], NativeCliFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                {
                    rawName = args[i + 1];
                }

                break;
            }
        }

        var safeName = SanitizePetName(rawName);
        if (!LabPetConsoleNative.EnsureConsoleAttachedOrAllocated())
        {
            return 1;
        }

        using var consoleState = new ChristmasConsoleState();
        try
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.OutputEncoding = utf8NoBom;
            Console.InputEncoding = utf8NoBom;
        }
        catch
        {
        }

        try
        {
            Console.Title = "LabPet - Merry Christmas!";
        }
        catch
        {
        }

        var vtEnabled = LabPetConsoleNative.TryEnableVirtualTerminal();
        LabPetConsoleNative.TryDisableQuickEditMode();
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")))
        {
            LabPetConsoleNative.TryConfigureHighDensityWindow(
                DefaultWindowColumns,
                DefaultWindowRows,
                DefaultFontPixelHeight);
        }

        try
        {
            if (vtEnabled)
            {
                // 1열 ASCII 고정폭 셀 기본 비율(가로/세로 ≈ 0.5)을 폴백으로 사용하고, 호스트 측정값이 있으면 우선 적용한다.
                var cellAspect = consoleState.TryGetGlyphAspect(out var measuredAspect)
                    ? measuredAspect
                    : 0.5f;
                new ChristmasTreeConsoleRunner().Run(safeName, cellAspect,
                    initialized: () =>
                    {
                        instance.MarkReady();
                        ChristmasTreeStartup.SignalReady();
                    });
            }
            else
            {
                // ANSI 미지원 환경에서는 기존 비 ANSI 출력 경로를 보존하고 시작 완료를 알린다.
                Console.WriteLine("ANSI 색상을 지원하지 않아 호환 모드로 실행합니다.");
                instance.MarkReady();
                ChristmasTreeStartup.SignalReady();
                var engine = new LabPetChristmasEngine();
                engine.RunLoop(safeName, vtEnabled: false);
            }

            return 0;
        }
        catch (Exception ex)
        {
            // 크리스마스트리 전용 자식 프로세스의 실패를 종료 코드로 전달한다.
            try { Console.Error.WriteLine("크리스마스트리 실행 실패: " + ex.Message); } catch { }
            return 1;
        }
    }

    /// <summary>
    /// 네이티브 콘솔 창 모드(conhost.exe → 직접 실행 순)로 내장 C# 3D ASCII 크리스마스 트리를 실행한다.
    /// </summary>
    public (bool Success, string Message) Launch(string? petName)
    {
        try
        {
            // 사용자 요청을 받은 본체에서 직접 활성화한다. 자식의 전경 전환 권한에 의존하지 않는다.
            // 트리가 없으면 이 확인용 잠금을 해제한 뒤 자식을 시작한다.
            using var existing = new ChristmasTreeInstance();
            if (!existing.IsOwner)
            {
                return existing.TryActivateExisting()
                    ? (true, "기존 크리스마스트리 창을 활성화했어요!")
                    : (false, "크리스마스트리가 이미 실행 중이지만 창 활성화를 확인하지 못했습니다. 새 창은 만들지 않았습니다.");
            }
        }
        catch
        {
            return (false, "크리스마스트리 실행 상태를 확인하지 못했습니다. 중복 실행을 방지하기 위해 새 창은 만들지 않았습니다.");
        }

        var conhostPath = ResolveConsoleHostPath();
        var clientExePath = ResolveCurrentClientExecutablePath();
        if (string.IsNullOrWhiteSpace(clientExePath))
        {
            return (false, "크리스마스트리 실행 파일(LabPet.Client.exe) 경로를 확인할 수 없습니다.");
        }

        if (!string.IsNullOrWhiteSpace(conhostPath))
        {
            try
            {
                var nativeConhostInfo = BuildNativeLaunchStartInfo(clientExePath, petName, conhostPath);
                var conhostResult = ChristmasTreeStartup.StartAndWait(nativeConhostInfo);
                if (conhostResult.Success || !conhostResult.CanFallback)
                {
                    return (conhostResult.Success, conhostResult.Message);
                }
            }
            catch
            {
                // conhost 래퍼 실패 시 직접 네이티브 실행으로 전환
            }
        }

        try
        {
            var nativeDirectInfo = BuildNativeLaunchStartInfo(clientExePath, petName, consoleHostPath: null);
            var directResult = ChristmasTreeStartup.StartAndWait(nativeDirectInfo);
            return (directResult.Success, directResult.Message);
        }
        catch
        {
            return (false, "크리스마스 트리 창을 실행하지 못했습니다.");
        }
    }
}
