using System.IO;
using LabPet.Client.Storage;
using LabPet.Client.Updates;
using LabPet.Shared.Models;
using Velopack.Sources;
using Xunit;

namespace LabPet.Tests;

public sealed class UpgradeAndUpdateTests
{
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
        // 기본 생성자 경로가 설치 폴더(AppContext.BaseDirectory)와 완전히 분리된 %AppData%\LabPet인지 검증
        var defaultStore = new JsonAppDataStore(new StateMigrator());
        var expectedRoaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LabPet");

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
}
