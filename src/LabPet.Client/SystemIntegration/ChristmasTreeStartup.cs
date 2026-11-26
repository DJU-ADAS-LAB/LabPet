using System;
using System.Diagnostics;
using System.Threading;

namespace LabPet.Client.SystemIntegration;

/// <summary>자식의 첫 프레임 출력 완료 신호를 기다린다. 생존 시간은 성공 기준이 아니다.</summary>
internal static class ChristmasTreeStartup
{
    private const string SignalVariable = "LABPET_CHRISTMAS_STARTUP";

    public static (bool Success, bool CanFallback, string Message) StartAndWait(ProcessStartInfo startInfo)
    {
        var signal = "Local\\LabPet.Christmas.Startup." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, signal + ".Ready");
        using var failed = new EventWaitHandle(false, EventResetMode.ManualReset, signal + ".Failed");
        startInfo.Environment[SignalVariable] = signal;
        using var process = Process.Start(startInfo);
        if (process is null) return (false, true, "크리스마스트리 프로세스를 시작하지 못했습니다.");
        try
        {
            var timer = Stopwatch.StartNew();
            WaitHandle[] signals = { ready, failed };
            while (timer.Elapsed.TotalSeconds < 10)
            {
                var result = WaitHandle.WaitAny(signals, 25);
                if (result == 0)
                    return (true, false, "3D ASCII 크리스마스트리를 띄웠어요! (Space: 회전 | L: 조명 | F: 불꽃 | Q: 종료)");
                if (result == 1)
                    return (false, true, "크리스마스트리 콘솔 또는 렌더러 초기화에 실패했습니다.");
                if (process.HasExited)
                    return (false, true, "초기화 완료 전에 크리스마스트리 프로세스가 종료됐습니다.");
            }
        }
        catch (Exception)
        {
            // 시작된 프로세스의 상태 조회 실패를 새 실행의 근거로 사용하지 않는다.
            return (false, false, "실행된 크리스마스트리의 초기화 상태를 확인하지 못했습니다. 중복 실행은 시도하지 않았습니다.");
        }
        return (false, false, "크리스마스트리 초기화 완료를 아직 확인하지 못했습니다. 중복 실행은 시도하지 않았습니다.");
    }

    public static void SignalReady() => Signal(".Ready");
    public static void SignalFailure() => Signal(".Failed");

    private static void Signal(string suffix)
    {
        var name = Environment.GetEnvironmentVariable(SignalVariable);
        const string prefix = "Local\\LabPet.Christmas.Startup.";
        if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[prefix.Length..], "N", out _)) return;
        try
        {
            using var handle = EventWaitHandle.OpenExisting(name + suffix);
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }
    }
}
