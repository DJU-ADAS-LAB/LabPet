using System;
using System.IO;
using System.Numerics;
using LabPet.Client.Rendering.Terminal3D;
using Xunit;

namespace LabPet.Tests;

public sealed class Terminal3DCoreTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DepthTest_NearestSurfaceWinsRegardlessOfSubmissionOrder(bool nearFirst)
    {
        var surface = new AsciiDepthSurface();
        surface.Resize(80, 24);
        if (nearFirst)
        {
            Assert.True(surface.TryWrite(4, 5, 2f, 0xFF0000, 0.8f));
            Assert.False(surface.TryWrite(4, 5, 8f, 0x0000FF, 0.2f));
        }
        else
        {
            Assert.True(surface.TryWrite(4, 5, 8f, 0x0000FF, 0.2f));
            Assert.True(surface.TryWrite(4, 5, 2f, 0xFF0000, 0.8f));
        }
        var index = 5 * surface.Columns + 4;
        Assert.Equal(2f, surface.Depths[index]);
        Assert.Equal(0xFF0000, surface.Colors[index]);
        Assert.Equal(AsciiDepthSurface.MapDensity(0.8f), surface.Glyphs[index]);
        Assert.True(AsciiDepthSurface.IsPrintableAscii(surface.Glyphs[index]));
        Assert.Equal(1, surface.OccupiedCells);

        surface.Clear();
        Assert.Equal(0, surface.OccupiedCells);
        Assert.True(float.IsPositiveInfinity(surface.Depths[index]));
        Assert.Equal(' ', surface.Glyphs[index]);
        Assert.Equal(0, surface.Colors[index]);
    }

    [Fact]
    public void Surface_UsesSingleColumnAsciiAndBoundsAllocation()
    {
        var surface = new AsciiDepthSurface();
        Assert.True(surface.Resize(164, 43));
        Assert.Equal(164, surface.Columns);
        Assert.Equal(43, surface.Rows);
        Assert.False(surface.Resize(164, 43));

        surface.Resize(int.MaxValue, int.MaxValue);
        Assert.Equal(AsciiDepthSurface.MaximumColumns, surface.Columns);
        Assert.Equal(AsciiDepthSurface.MaximumRows, surface.Rows);

        Assert.False(surface.TryWrite(-1, 0, 1f, 0, 1f));
        Assert.False(surface.TryWrite(0, 0, float.NaN, 0, 1f));
        Assert.False(surface.TryWrite(0, 0, float.PositiveInfinity, 0, 1f));
        Assert.False(surface.TryWrite(0, 0, 0f, 0, 1f));
        Assert.False(surface.TryWriteGlyph(0, 0, 1f, 0xFFFFFF, '가'));
    }

    [Fact]
    public void BlendGlow_RespectsDepthOcclusionAndAddsColor()
    {
        var surface = new AsciiDepthSurface();
        surface.Resize(20, 10);
        Assert.True(surface.TryWriteGlyph(5, 5, 4f, 0x203040, '#'));
        Assert.False(surface.TryBlendGlow(5, 5, 6f, 0x808080, '*'));
        Assert.Equal(0x203040, surface.Colors[5 * surface.Columns + 5]);

        Assert.True(surface.TryBlendGlow(5, 5, 3.5f, 0x504030, '*'));
        Assert.Equal(0x707070, surface.Colors[5 * surface.Columns + 5]);
        Assert.Equal('*', surface.Glyphs[5 * surface.Columns + 5]);
    }

    [Fact]
    public void Projector_ClipsBehindCameraAndWritesAsciiShade()
    {
        var surface = new AsciiDepthSurface();
        surface.Resize(160, 48);
        var projector = new TerminalPointProjector(surface);
        projector.Configure(0f, 1.4f, 0.5f);
        Assert.False(projector.Plot(new Vector3(0, 3, 20), Vector3.UnitY, 0xFFFFFF));
        Assert.False(projector.Plot(new Vector3(float.NaN, 0, 0), Vector3.UnitY, 0xFFFFFF));
        Assert.True(projector.Plot(new Vector3(0, 2.55f, 0), Vector3.UnitZ, 0xFFFFFF, specularStrength: 0.9f, shininess: 24f));
        Assert.Equal(1, surface.OccupiedCells);
    }

    [Fact]
    public void AnsiFrameWriter_EmitsDiffOnlyWhenCellsChange()
    {
        var surface = new AsciiDepthSurface();
        surface.Resize(16, 4);
        var writer = new AsciiAnsiFrameWriter();

        surface.TryWriteGlyph(2, 1, 3f, 0x11AA22, '#');
        using var firstOut = new StringWriter();
        var firstChanged = writer.WriteFrame(firstOut, surface, left: 0, top: 1);
        Assert.True(firstChanged > 0);
        Assert.Contains("\u001b[38;2;17;170;34m#", firstOut.ToString(), StringComparison.Ordinal);

        using var secondOut = new StringWriter();
        var secondChanged = writer.WriteFrame(secondOut, surface, left: 0, top: 1);
        Assert.Equal(0, secondChanged);
        Assert.Equal(string.Empty, secondOut.ToString());
    }
}
