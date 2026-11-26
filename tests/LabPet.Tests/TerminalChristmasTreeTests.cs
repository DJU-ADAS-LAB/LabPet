using System.Diagnostics;
using System.IO;
using LabPet.Client.Storage;
using LabPet.Client.SystemIntegration;
using Xunit;

namespace LabPet.Tests;

public sealed class TerminalChristmasTreeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(@"C:\Windows\System32\conhost.exe")]
    public void BuildNativeLaunchStartInfo_PreservesArgumentsSetsWorkingDirWithoutShell(string? consoleHostPath)
    {
        const string clientExe = @"C:\사용자 이름\LabPet & (트리) %PATH%!\LabPet.Client.exe";
        const string petName = "모찌 & | < > ^ %PATH% ! \"펫\"";
        var info = TerminalChristmasTreeService.BuildNativeLaunchStartInfo(clientExe, petName + "\r\n", consoleHostPath);
        Assert.Equal(consoleHostPath ?? clientExe, info.FileName);
        Assert.Equal(Path.GetDirectoryName(clientExe), info.WorkingDirectory);
        Assert.False(info.UseShellExecute);
        Assert.False(info.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Normal, info.WindowStyle);
        Assert.Empty(info.Arguments);
        var expected = consoleHostPath is null
            ? new[] { TerminalChristmasTreeService.NativeCliFlag, petName }
            : new[] { clientExe, TerminalChristmasTreeService.NativeCliFlag, petName };
        Assert.Equal(expected, info.ArgumentList.ToArray());
    }

    [Fact]
    public void BuildNativeLaunchStartInfo_RejectsRelativePathsAndResolvesSystemConhost()
    {
        Assert.Throws<ArgumentException>(() =>
            TerminalChristmasTreeService.BuildNativeLaunchStartInfo("LabPet.Client.exe", "모찌", null));
        Assert.Throws<ArgumentException>(() =>
            TerminalChristmasTreeService.BuildNativeLaunchStartInfo(@"C:\Apps\LabPet.Client.exe", "모찌", "conhost.exe"));

        var conhost = TerminalChristmasTreeService.ResolveConsoleHostPath();
        if (conhost is not null)
        {
            Assert.True(Path.IsPathFullyQualified(conhost));
            Assert.EndsWith("conhost.exe", conhost, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NativeChristmasEngine_RendersHighDensityFrameWithoutPowerShell()
    {
        var engine = new LabPetChristmasEngine();
        var bytesWritten = engine.RenderFrameBytes(
            TerminalChristmasTreeService.DefaultWindowColumns,
            TerminalChristmasTreeService.DefaultWindowRows,
            "똥개",
            frame: 0,
            lightMode: 0,
            snowLevel: 1,
            vtEnabled: true,
            forceFullRedraw: true);

        Assert.True(bytesWritten > 50_000);
        var renderedPlain = engine.RenderFrame(
            TerminalChristmasTreeService.DefaultWindowColumns,
            TerminalChristmasTreeService.DefaultWindowRows,
            "똥개",
            frame: 1,
            lightMode: 0,
            snowLevel: 1,
            vtEnabled: false,
            forceFullRedraw: true);
        Assert.Contains("Merry Christmas, 똥개!", renderedPlain, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "LabPet")]
    [InlineData("", "LabPet")]
    [InlineData("   \r\n\t\u001b  ", "LabPet")]
    [InlineData("  모찌\r\n\u001b[31m펫  ", "모찌[31m펫")]
    public void SanitizePetName_RemovesControlCharactersAndAppliesFallback(string? input, string expected)
    {
        Assert.Equal(expected, TerminalChristmasTreeService.SanitizePetName(input));
    }

    [Fact]
    public void SanitizePetName_ClampsToMaxLength()
    {
        var longName = new string('가', TerminalChristmasTreeService.MaxPetNameLength + 10);
        var sanitized = TerminalChristmasTreeService.SanitizePetName(longName);

        Assert.Equal(TerminalChristmasTreeService.MaxPetNameLength, sanitized.Length);
    }
}
