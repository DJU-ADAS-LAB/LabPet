using System;
using System.Runtime.InteropServices;

namespace LabPet.Client.SystemIntegration;

/// <summary>콘솔 모드·코드페이지·글꼴의 원래 값을 보관하고 종료 시 복원한다.</summary>
internal sealed class ChristmasConsoleState : IDisposable
{
    private readonly IntPtr _input = LabPetConsoleNative.GetStdHandle(-10);
    private readonly IntPtr _output = LabPetConsoleNative.GetStdHandle(-11);
    private readonly uint _inputMode;
    private readonly uint _outputMode;
    private readonly bool _hasInputMode;
    private readonly bool _hasOutputMode;
    private readonly uint _inputCodePage;
    private readonly uint _outputCodePage;
    private LabPetConsoleNative.CONSOLE_FONT_INFOEX _font;
    private readonly bool _hasFont;
    private bool _disposed;

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleCP();
    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetCurrentConsoleFontEx(IntPtr output, bool maximumWindow,
        ref LabPetConsoleNative.CONSOLE_FONT_INFOEX font);

    public ChristmasConsoleState()
    {
        _hasInputMode = LabPetConsoleNative.GetConsoleMode(_input, out _inputMode);
        _hasOutputMode = LabPetConsoleNative.GetConsoleMode(_output, out _outputMode);
        _inputCodePage = GetConsoleCP();
        _outputCodePage = GetConsoleOutputCP();
        _font = CreateFontInfo();
        _hasFont = GetCurrentConsoleFontEx(_output, false, ref _font);
    }

    private static LabPetConsoleNative.CONSOLE_FONT_INFOEX CreateFontInfo() => new()
    {
        cbSize = (uint)Marshal.SizeOf<LabPetConsoleNative.CONSOLE_FONT_INFOEX>(),
        FaceName = string.Empty
    };

    /// <summary>콘솔 호스트가 제공하는 단일 문자 셀 치수로 1열 ASCII 종횡비(가로/세로)를 계산한다.</summary>
    public bool TryGetGlyphAspect(out float aspect)
    {
        var current = CreateFontInfo();
        aspect = 0.5f;
        if (!GetCurrentConsoleFontEx(_output, false, ref current)
            || current.dwFontSizeX <= 0 || current.dwFontSizeY <= 0) return false;
        aspect = (float)current.dwFontSizeX / current.dwFontSizeY;
        return float.IsFinite(aspect) && aspect >= 0.25f && aspect <= 2.0f;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 복원 항목 하나의 실패가 나머지 항목 복원을 막지 않도록 각각 시도한다.
        if (_hasFont)
            LabPetConsoleNative.SetCurrentConsoleFontEx(_output, false, ref _font);
        if (_inputCodePage != 0) LabPetConsoleNative.SetConsoleCP(_inputCodePage);
        if (_outputCodePage != 0) LabPetConsoleNative.SetConsoleOutputCP(_outputCodePage);
        if (_hasInputMode) LabPetConsoleNative.SetConsoleMode(_input, _inputMode);
        if (_hasOutputMode) LabPetConsoleNative.SetConsoleMode(_output, _outputMode);
    }
}
