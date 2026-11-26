using System.IO;
using LabPet.Client.Storage;
using LabPet.Client.SystemIntegration;
using LabPet.Client.Updates;
using LabPet.Shared.Models;
using Velopack.Sources;
using Xunit;

namespace LabPet.Tests;

public sealed class UpgradeAndUpdateTests
{
    [Fact]
    public void UpdateSourceOptions_DefaultsPreserveOfficialStaticFeedAndRepoUrl()
    {
        var defaults = new UpdateSourceOptions();

        Assert.Equal(UpdateProviderKind.SimpleWeb, defaults.Provider);
        Assert.Equal("https://github.com/DJU-ADAS-LAB/LabPet", defaults.GitHubRepoUrl);
        Assert.Equal("https://github.com/DJU-ADAS-LAB/LabPet/releases/latest/download", defaults.SimpleWebFeedUrl);
        Assert.IsType<SimpleWebSource>(defaults.CreateUpdateSource());
    }

    [Fact]
    public void UpdateSourceOptions_SwitchesBetweenGitHubAndSimpleWebSource()
    {
        var ghOptions = new UpdateSourceOptions
        {
            Provider = UpdateProviderKind.GitHub,
            GitHubRepoUrl = "https://github.com/example/labpet"
        };
        Assert.IsType<GithubSource>(ghOptions.CreateUpdateSource());

        var gcpBucketOptions = new UpdateSourceOptions
        {
            Provider = UpdateProviderKind.SimpleWeb,
            SimpleWebFeedUrl = "https://storage.googleapis.com/labpet-releases/win-x64"
        };
        Assert.IsType<SimpleWebSource>(gcpBucketOptions.CreateUpdateSource());
    }

    [Fact]
    public async Task AppDataStore_UsesRoamingAppDataOutsideBaseDirectoryAndPreservesAcrossReinstantiation()
    {
        // 기본 생성자가 빌드 구성별 저장 경로를 사용하고 설치 폴더와 분리되는지 확인한다.
        var defaultStore = new JsonAppDataStore(new StateMigrator(), customDataDirectory: null);
        var expectedRoaming = AppDataPaths.ResolveDefaultDataDirectory();

        Assert.Equal(expectedRoaming, defaultStore.DataDirectory);
        Assert.False(
            defaultStore.DataDirectory.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase),
            "사용자 데이터 경로는 설치 폴더(AppContext.BaseDirectory) 내부일 수 없습니다.");

        // 업그레이드 전후 시뮬레이션: 서로 다른 스토어 인스턴스(새 바이너리)가 동일 디렉터리에서 프로필/친구 100% 복원
        var isolatedDir = Path.Combine(Path.GetTempPath(), "LabPet_Upgrade_" + Guid.NewGuid().ToString("N"));
        try
        {
            var beforeUpgradeStore = new JsonAppDataStore(new StateMigrator(), isolatedDir);
            var profile = new PetProfile
            {
                PetName = "업그레이드모찌",
                IsOnboardingCompleted = true,
                SoloProgress = new SoloProgress { Level = 4, Coins = 250, CompletedPomodoros = 3 }
            };
            var friends = new FriendsStore
            {
                Friends =
                [
                    new FriendEntry
                    {
                        PetId = "friend-uuid-001",
                        PetName = "단짝냥이",
                        PairSecretBase64 = "c2VjcmV0",
                        Intimacy = 12
                    }
                ]
            };

            await beforeUpgradeStore.SaveProfileAsync(profile);
            await beforeUpgradeStore.SaveFriendsAsync(friends);

            // 업그레이드 후 새 인스턴스에서 로드
            var afterUpgradeStore = new JsonAppDataStore(new StateMigrator(), isolatedDir);
            var loadedProfile = await afterUpgradeStore.LoadProfileAsync();
            var loadedFriends = await afterUpgradeStore.LoadFriendsAsync();

            Assert.Equal(profile.PetId, loadedProfile.PetId);
            Assert.Equal("업그레이드모찌", loadedProfile.PetName);
            Assert.Equal(4, loadedProfile.SoloProgress.Level);
            Assert.Equal(250, loadedProfile.SoloProgress.Coins);
            Assert.Single(loadedFriends.Friends);
            Assert.Equal("단짝냥이", loadedFriends.Friends[0].PetName);
            Assert.Equal(12, loadedFriends.Friends[0].Intimacy);
        }
        finally
        {
            if (Directory.Exists(isolatedDir))
            {
                Directory.Delete(isolatedDir, recursive: true);
            }
        }
    }

    [Fact]
    public void FirewallRegistration_BuildsRestrictedPowerShellCommandAndRejectsMissingPath()
    {
        const string exePath = @"C:\Users\TestUser\AppData\Local\LabPet\current\LabPet.Client.exe";

        var ps = FirewallRegistrationService.BuildPowerShellRegisterCommand(exePath);
        Assert.Contains("$new | Remove-NetFirewallRule -ErrorAction SilentlyContinue", ps);
        Assert.Contains("if ($old.Count -gt 0) { $old | Remove-NetFirewallRule }", ps);
        Assert.Contains("-RemoteAddress LocalSubnet -Program 'C:\\Users\\TestUser\\AppData\\Local\\LabPet\\current\\LabPet.Client.exe' -Protocol UDP -LocalPort 45678,45679", ps);
        Assert.Contains("-RemoteAddress LocalSubnet -Program 'C:\\Users\\TestUser\\AppData\\Local\\LabPet\\current\\LabPet.Client.exe' -Protocol TCP -LocalPort 45680", ps);

        // 작은따옴표가 포함된 경로는 PowerShell 문자열 이스케이프('') 처리
        var quotePath = @"C:\Users\O'Brien\LabPet\LabPet.Client.exe";
        var psWithQuote = FirewallRegistrationService.BuildPowerShellRegisterCommand(quotePath);
        Assert.Contains("-Program 'C:\\Users\\O''Brien\\LabPet\\LabPet.Client.exe'", psWithQuote);

        Assert.Throws<ArgumentException>(() => FirewallRegistrationService.BuildPowerShellRegisterCommand(null));
        Assert.Throws<ArgumentException>(() => FirewallRegistrationService.BuildPowerShellRegisterCommand("relative.exe"));
    }

    [Fact]
    public void FirewallRegistration_DetectsHardenedAndLegacyNetshOutput()
    {
        const string expectedPath = @"C:\Users\TestUser\AppData\Local\LabPet\current\LabPet.Client.exe";

        // 영문 netsh verbose 출력 + 긴 Program 경로 줄바꿈 케이스
        const string hardenedEnglishOutput = """
            Rule Name:                            LabPet LAN TCP
            ----------------------------------------------------------------------
            Enabled:                              Yes
            Direction:                            In
            Profiles:                             Domain,Private,Public
            LocalIP:                              Any
            RemoteIP:                             LocalSubnet
            Protocol:                             TCP
            LocalPort:                            45680
            Program:                              c:\users\testuser\appdata\local\labpet\c
            urrent\labpet.client.exe
            Action:                               Allow
            Ok.
            """;

        // 한글 netsh verbose 출력 케이스
        const string hardenedKoreanOutput = """
            규칙 이름:                            LabPet LAN TCP
            ----------------------------------------------------------------------
            사용:                                 예
            방향:                                 인바운드
            프로토콜:                             TCP
            로컬 포트:                            45680
            원격 IP:                              로컬 서브넷
            프로그램:                             C:\Users\TestUser\AppData\Local\LabPet\current\LabPet.Client.exe
            작업:                                 허용
            확인.
            """;

        // 구버전(RemoteIP: Any, Program 미지정) 출력 케이스
        const string legacyOutput = """
            Rule Name:                            LabPet LAN TCP
            ----------------------------------------------------------------------
            LocalIP:                              Any
            RemoteIP:                             Any
            Protocol:                             TCP
            LocalPort:                            45680
            Action:                               Allow
            Ok.
            """;

        // 다른 실행 파일 경로가 등록된 케이스
        const string mismatchedProgramOutput = """
            Rule Name:                            LabPet LAN TCP
            ----------------------------------------------------------------------
            RemoteIP:                             LocalSubnet
            Program:                              C:\OtherApp\Other.exe
            Action:                               Allow
            Ok.
            """;

        Assert.True(FirewallRegistrationService.IsHardenedRuleOutput(hardenedEnglishOutput, expectedPath, FirewallRegistrationService.TcpRuleName));
        Assert.True(FirewallRegistrationService.IsHardenedRuleOutput(hardenedKoreanOutput, expectedPath, FirewallRegistrationService.TcpRuleName));
        Assert.False(FirewallRegistrationService.IsHardenedRuleOutput(legacyOutput, expectedPath, FirewallRegistrationService.TcpRuleName));
        Assert.False(FirewallRegistrationService.IsHardenedRuleOutput(mismatchedProgramOutput, expectedPath, FirewallRegistrationService.TcpRuleName));
        Assert.False(FirewallRegistrationService.IsHardenedRuleOutput(hardenedEnglishOutput.Replace("45680", "45681"), expectedPath, FirewallRegistrationService.TcpRuleName));
        Assert.False(FirewallRegistrationService.IsHardenedRuleOutput(hardenedEnglishOutput.Replace("LocalSubnet", "LocalSubnet,Any"), expectedPath, FirewallRegistrationService.TcpRuleName));
        Assert.False(FirewallRegistrationService.IsHardenedRuleOutput(hardenedEnglishOutput + "\n" + hardenedEnglishOutput, expectedPath, FirewallRegistrationService.TcpRuleName));
    }
}
