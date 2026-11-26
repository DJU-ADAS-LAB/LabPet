using System;
using System.IO;
using System.Text;

namespace LabPet.Client.Rendering.Terminal3D;

/// <summary>1열 ASCII 셀의 ANSI True Color 차분 출력 및 단일 버퍼 HUD 조합기.</summary>
internal sealed class AsciiAnsiFrameWriter
{
    private readonly StringBuilder _output = new(65536);
    private char[] _previousGlyphs = Array.Empty<char>();
    private int[] _previousColors = Array.Empty<int>();
    private int _columns;
    private int _rows;
    private int _left;
    private int _top;
    private bool _valid;
    private bool _surfaceChanged;

    public void Invalidate() => _valid = false;

    public void BeginFrame()
    {
        _output.Clear();
        _surfaceChanged = false;
    }

    public int AppendSurfaceDiff(AsciiDepthSurface surface, int left = 0, int top = 1, bool forceFullRedraw = false)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (left < 0 || top < 0) throw new ArgumentOutOfRangeException(nameof(left));

        var glyphs = surface.Glyphs;
        var colors = surface.Colors;
        if (_columns != surface.Columns || _rows != surface.Rows)
        {
            _columns = surface.Columns;
            _rows = surface.Rows;
            _previousGlyphs = new char[glyphs.Length];
            _previousColors = new int[colors.Length];
            _valid = false;
        }
        if (_left != left || _top != top) _valid = false;
        _left = left;
        _top = top;

        var full = forceFullRedraw || !_valid;
        var changed = 0;
        var foreground = -1;
        for (var row = 0; row < _rows; row++)
        {
            var contiguous = false;
            var rowBase = row * _columns;
            for (var column = 0; column < _columns; column++)
            {
                var index = rowBase + column;
                var glyph = glyphs[index];
                var color = colors[index];
                if (!full && glyph == _previousGlyphs[index] && color == _previousColors[index])
                {
                    contiguous = false;
                    continue;
                }
                if (changed == 0) _output.Append("\u001b[0m\u001b[48;2;0;0;0m");
                if (!contiguous)
                {
                    _output.Append("\u001b[").Append(top + row + 1).Append(';')
                        .Append(left + column + 1).Append('H');
                }
                if (glyph == ' ')
                {
                    _output.Append(' ');
                }
                else
                {
                    if (foreground != color)
                    {
                        _output.Append("\u001b[38;2;").Append((color >> 16) & 255).Append(';')
                            .Append((color >> 8) & 255).Append(';').Append(color & 255).Append('m');
                        foreground = color;
                    }
                    _output.Append(glyph);
                }
                changed++;
                contiguous = true;
            }
        }
        if (changed > 0)
        {
            _output.Append("\u001b[0m");
            _surfaceChanged = true;
        }
        return changed;
    }

    public void AppendHudLine(int row, int consoleWidth, ReadOnlySpan<char> text, int rgb = 0xDCE6F5, bool alignRight = false)
    {
        var available = Math.Max(0, consoleWidth - 1);
        var used = 0;
        var length = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var cellWidth = IsWideDisplayChar(ch) ? 2 : 1;
            if (used + cellWidth > available) break;
            used += cellWidth;
            length++;
        }

        _output.Append("\u001b[").Append(row + 1).Append(";1H\u001b[0m\u001b[48;2;0;0;0m\u001b[38;2;")
            .Append((rgb >> 16) & 255).Append(';').Append((rgb >> 8) & 255).Append(';').Append(rgb & 255)
            .Append("m\u001b[2K");
        if (alignRight && available > used)
        {
            _output.Append(' ', available - used);
        }
        _output.Append(text[..length]);
    }

    public void FlushTo(TextWriter writer, AsciiDepthSurface surface)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(surface);
        if (_output.Length == 0) return;

        try
        {
            foreach (var chunk in _output.GetChunks()) writer.Write(chunk.Span);
            writer.Flush();
            if (_surfaceChanged || !_valid)
            {
                surface.Glyphs.CopyTo(_previousGlyphs);
                surface.Colors.CopyTo(_previousColors);
                _valid = true;
            }
        }
        catch
        {
            _valid = false;
            throw;
        }
    }

    public int WriteFrame(TextWriter writer, AsciiDepthSurface surface,
        int left = 0, int top = 1, bool forceFullRedraw = false)
    {
        BeginFrame();
        var changed = AppendSurfaceDiff(surface, left, top, forceFullRedraw);
        if (changed > 0) FlushTo(writer, surface);
        return changed;
    }

    private static bool IsWideDisplayChar(char ch)
        => (ch >= '\uAC00' && ch <= '\uD7A3')
           || (ch >= '\u1100' && ch <= '\u11FF')
           || (ch >= '\u3130' && ch <= '\u318F')
           || (ch >= '\uFF01' && ch <= '\uFF60');
}
