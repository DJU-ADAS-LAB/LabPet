using System;

namespace LabPet.Client.Rendering.Terminal3D;

/// <summary>1콘솔 열을 1픽셀 셀로 사용하는 재사용 가능한 ASCII 문자·RGB 색상·깊이(Z-Buffer) 버퍼.</summary>
internal sealed class AsciiDepthSurface
{
    public const int MaximumColumns = 320;
    public const int MaximumRows = 120;

    // 고정폭 글꼴(Consolas/Cascadia/Lucida) 기준 획 밀도가 단조 증가하는 기본 7비트 ASCII 램프.
    private const string DensityRamp = ".,':;-~=+!*ox#%&$@";
    // 재질별 질감을 살리는 전용 7비트 ASCII 밀도 램프
    private const string FoliageRamp = ".,:^v+=*xAMW#@";
    private const string GroundRamp = " .,:;-~^+*";
    private const string SolidRamp = ".,:-=+*H#&$@";

    private char[] _glyphs = Array.Empty<char>();
    private int[] _colors = Array.Empty<int>();
    private float[] _depths = Array.Empty<float>();

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int OccupiedCells { get; private set; }
    public ReadOnlySpan<char> Glyphs => _glyphs;
    public ReadOnlySpan<int> Colors => _colors;
    public ReadOnlySpan<float> Depths => _depths;

    public bool Resize(int consoleColumns, int sceneRows)
    {
        var columns = Math.Clamp(consoleColumns, 1, MaximumColumns);
        var rows = Math.Clamp(sceneRows, 1, MaximumRows);
        if (columns == Columns && rows == Rows) return false;
        Columns = columns;
        Rows = rows;
        var count = columns * rows;
        _glyphs = new char[count];
        _colors = new int[count];
        _depths = new float[count];
        Clear();
        return true;
    }

    public void Clear()
    {
        Array.Fill(_glyphs, ' ');
        Array.Clear(_colors);
        Array.Fill(_depths, float.PositiveInfinity);
        OccupiedCells = 0;
    }

    public static char MapDensity(float brightness) => MapRamp(DensityRamp, brightness);

    public static char MapMaterialDensity(int style, float brightness) => style switch
    {
        1 => MapRamp(FoliageRamp, brightness),
        2 => MapRamp(GroundRamp, brightness),
        3 => MapRamp(SolidRamp, brightness),
        _ => MapRamp(DensityRamp, brightness)
    };

    private static char MapRamp(string ramp, float brightness)
    {
        if (!float.IsFinite(brightness)) brightness = 0;
        var index = (int)MathF.Round(Math.Clamp(brightness, 0f, 1f) * (ramp.Length - 1));
        return ramp[index];
    }

    public static bool IsPrintableAscii(char glyph) => glyph >= 0x20 && glyph <= 0x7E;

    /// <summary>양의 카메라 깊이를 비교해 밝기 밀도에 맞는 ASCII 문자를 기록한다.</summary>
    public bool TryWrite(int column, int row, float depth, int rgb, float brightness)
        => TryWriteGlyph(column, row, depth, rgb, MapDensity(brightness));

    /// <summary>재질별 전용 ASCII 밀도 램프를 사용해 깊이 테스트와 함께 기록한다.</summary>
    public bool TryWriteStyled(int column, int row, float depth, int rgb, float brightness, int glyphStyle)
        => TryWriteGlyph(column, row, depth, rgb, MapMaterialDensity(glyphStyle, brightness));

    /// <summary>명시적인 7비트 ASCII 문자를 깊이 테스트와 함께 기록한다.</summary>
    public bool TryWriteGlyph(int column, int row, float depth, int rgb, char glyph)
    {
        if ((uint)column >= (uint)Columns || (uint)row >= (uint)Rows
            || !float.IsFinite(depth) || depth <= 0
            || !IsPrintableAscii(glyph)) return false;
        var index = row * Columns + column;
        if (depth >= _depths[index]) return false;
        if (float.IsPositiveInfinity(_depths[index])) OccupiedCells++;
        _depths[index] = depth;
        _glyphs[index] = glyph;
        _colors[index] = rgb & 0xFFFFFF;
        return true;
    }

    /// <summary>
    /// 불투명 표면을 가리지 않는 범위에서 별빛 후광·불꽃 잔광 같은 가산 발광 셀을 기록한다.
    /// 더 가까운 표면이 이미 기록되어 있으면 건너뛴다.
    /// </summary>
    public bool TryBlendGlow(int column, int row, float depth, int rgb, char glyph)
    {
        if ((uint)column >= (uint)Columns || (uint)row >= (uint)Rows
            || !float.IsFinite(depth) || depth <= 0
            || !IsPrintableAscii(glyph)) return false;
        var index = row * Columns + column;
        var existingDepth = _depths[index];
        if (float.IsPositiveInfinity(existingDepth))
        {
            OccupiedCells++;
            _depths[index] = depth;
            _glyphs[index] = glyph;
            _colors[index] = rgb & 0xFFFFFF;
            return true;
        }
        if (depth > existingDepth + 0.05f) return false;
        var existing = _colors[index];
        var r = Math.Min(255, ((existing >> 16) & 255) + ((rgb >> 16) & 255));
        var g = Math.Min(255, ((existing >> 8) & 255) + ((rgb >> 8) & 255));
        var b = Math.Min(255, (existing & 255) + (rgb & 255));
        _colors[index] = (r << 16) | (g << 8) | b;
        if (depth < existingDepth)
        {
            _depths[index] = depth;
            _glyphs[index] = glyph;
        }
        return true;
    }
}
