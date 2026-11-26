using System.Diagnostics;
using System.IO;
using LabPet.Client.Storage;
using LabPet.Client.SystemIntegration;
using Xunit;

namespace LabPet.Tests;

public sealed class TerminalChristmasTreeTests
{
    [Fact]
    public void EnsureScriptFile_WritesUtf8BomPowerShell5CompatibleScriptOutsideBaseDirectory()
    {
        var defaultService = new TerminalChristmasTreeService(new JsonAppDataStore(new StateMigrator(), customDataDirectory: null));
        var expectedRoamingScript = Path.Combine(
            AppDataPaths.ResolveDefaultDataDirectory(),
            TerminalChristmasTreeService.ScriptsDirectoryName,
            TerminalChristmasTreeService.ScriptFileName);

        Assert.Equal(expectedRoamingScript, defaultService.ScriptFilePath);
        Assert.False(
            defaultService.ScriptFilePath.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase),
            "생성되는 스크립트 경로는 설치 폴더(AppContext.BaseDirectory) 내부일 수 없습니다.");

        var tempDir = Path.Combine(Path.GetTempPath(), "LabPet_Christmas_" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new TerminalChristmasTreeService(tempDir);
            var scriptPath = service.EnsureScriptFile();

            Assert.Equal(
                Path.Combine(tempDir, TerminalChristmasTreeService.ScriptsDirectoryName, TerminalChristmasTreeService.ScriptFileName),
                scriptPath);
            Assert.True(File.Exists(scriptPath));

            var rawBytes = File.ReadAllBytes(scriptPath);
            Assert.True(rawBytes.Length > 3);
            Assert.Equal(0xEF, rawBytes[0]);
            Assert.Equal(0xBB, rawBytes[1]);
            Assert.Equal(0xBF, rawBytes[2]);

            var text = File.ReadAllText(scriptPath);
            Assert.DoesNotContain("`e", text, StringComparison.Ordinal);
            Assert.DoesNotContain("`u{", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Ctrl+휠", text, StringComparison.Ordinal);
            Assert.Equal(164, TerminalChristmasTreeService.DefaultWindowColumns);
            Assert.Equal(48, TerminalChristmasTreeService.DefaultWindowRows);
            Assert.Equal((short)11, TerminalChristmasTreeService.DefaultFontPixelHeight);
            Assert.Contains("WriteByte(27)", text, StringComparison.Ordinal);
            Assert.Contains("KeyAvailable", text, StringComparison.Ordinal);
            Assert.Contains("finally", text, StringComparison.Ordinal);
            Assert.Contains("$PetName", text, StringComparison.Ordinal);
            Assert.Contains("Merry Christmas", text, StringComparison.Ordinal);
            Assert.Contains("LabPetChristmasEngine", text, StringComparison.Ordinal);
            Assert.Contains("2580", text, StringComparison.Ordinal);
            Assert.Contains("ToneMapAces", text, StringComparison.Ordinal);
            Assert.Contains("_pondMask", text, StringComparison.Ordinal);
            Assert.Contains("SetConsoleOutputCP", text, StringComparison.Ordinal);
            Assert.Contains("SetCurrentConsoleFontEx", text, StringComparison.Ordinal);
            Assert.Contains("TryDisableQuickEditMode", text, StringComparison.Ordinal);
            Assert.Contains("TryConfigureHighDensityWindow(164, 48, 11)", text, StringComparison.Ordinal);
            Assert.Contains("DrawKeyGuideBar", text, StringComparison.Ordinal);
            Assert.Contains(" SPACE ", text, StringComparison.Ordinal);
            Assert.Contains(" Q / ESC ", text, StringComparison.Ordinal);
            Assert.Contains("RunLoop", text, StringComparison.Ordinal);
            Assert.Contains("RenderFrameBytes", text, StringComparison.Ordinal);
            Assert.Contains("1049h", text, StringComparison.Ordinal);
            Assert.Contains("1049l", text, StringComparison.Ordinal);
            Assert.Contains("Spacebar", text, StringComparison.Ordinal);
            Assert.Contains("ConsoleKey.F", text, StringComparison.Ordinal);
            Assert.Contains("ConsoleKey.S", text, StringComparison.Ordinal);

            // 동일 내용 재호출 시에도 예외 없이 동일 경로 반환
            var secondCallPath = service.EnsureScriptFile();
            Assert.Equal(scriptPath, secondCallPath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void BuildLaunchStartInfo_UsesConsoleHostWhenAvailableAndFallsBackToPowerShell()
    {
        const string scriptPath = @"C:\Users\Test User\AppData\Roaming\LabPet\scripts\christmas_tree.ps1";
        const string conhostPath = @"C:\Windows\System32\conhost.exe";

        var conhostInfo = TerminalChristmasTreeService.BuildLaunchStartInfo(scriptPath, "모찌\r\n", conhostPath);
        Assert.Equal(conhostPath, conhostInfo.FileName);
        Assert.True(conhostInfo.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Normal, conhostInfo.WindowStyle);
        Assert.Equal(
            $"powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -PetName \"모찌\"",
            conhostInfo.Arguments);

        var psInfo = TerminalChristmasTreeService.BuildLaunchStartInfo(scriptPath, "초코", consoleHostPath: null);
        Assert.Equal("powershell.exe", psInfo.FileName);
        Assert.True(psInfo.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Normal, psInfo.WindowStyle);
        Assert.Equal(
            $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -PetName \"초코\"",
            psInfo.Arguments);
    }

    [Theory]
    [InlineData(@"C:\My Folder\scripts\", @"""C:\My Folder\scripts\\""")]
    [InlineData("""이름 "모찌" \펫""", @"""이름 \""모찌\"" \펫""")]
    [InlineData("", @"""""")]
    public void QuoteCommandLineArgument_EscapesQuotesAndTrailingBackslashes(string raw, string expected)
    {
        Assert.Equal(expected, TerminalChristmasTreeService.QuoteCommandLineArgument(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(@"C:\Windows\System32\conhost.exe")]
    public void BuildNativeLaunchStartInfo_PreservesArgumentsWithoutShell(string? consoleHostPath)
    {
        const string clientExe = @"C:\사용자 이름\LabPet & (트리) %PATH%!\LabPet.Client.exe";
        const string petName = "모찌 & | < > ^ %PATH% ! \"펫\"";
        var info = TerminalChristmasTreeService.BuildNativeLaunchStartInfo(clientExe, petName + "\r\n", consoleHostPath);
        Assert.Equal(consoleHostPath ?? clientExe, info.FileName);
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
