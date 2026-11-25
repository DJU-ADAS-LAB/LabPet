using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LabPet.Client.Audio;
using LabPet.Client.Navigation;
using LabPet.Client.Network;
using LabPet.Client.Rendering;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Client.SystemIntegration;
using LabPet.Client.Updates;
using LabPet.Client.ViewModels;
using LabPet.Client.Views;
using LabPet.Client.Views.Hub;
using LabPet.Client.Views.Hub.Pages;
using LabPet.Shared.Models;
using Xunit;

namespace LabPet.Tests;

[Collection("WpfUi")]
public sealed class HubNavigationAndLifecycleTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "LabPet_HubTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // 임시 폴더 정리 예외 무시
        }
    }

    [Fact]
    public async Task PetWindowViewModel_NavigationCommands_RaiseExpectedRequests()
    {
        var store = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempDir, "cmds"));
        await using var transport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
        await using var social = new SocialCoordinatorService(transport, store, new SoloProgressionService());
        var petVm = new PetWindowViewModel(store, social);

        var received = new List<NavigationRequest>();
        petVm.HubNavigationRequested += (_, req) => received.Add(req);

        petVm.OpenHubCommand.Execute(null);
        petVm.OpenCustomizationCommand.Execute(null);
        petVm.OpenSocialCommand.Execute(null);
        petVm.OpenGrowthCommand.Execute(null);
        petVm.OpenPomodoroCommand.Execute(null);
        petVm.OpenGamesCommand.Execute(null);
        petVm.OpenSettingsCommand.Execute(null);

        Assert.Equal(
        [
            new NavigationRequest(HubPage.Home),
            new NavigationRequest(HubPage.Appearance),
            new NavigationRequest(HubPage.Friends),
            new NavigationRequest(HubPage.Growth),
            new NavigationRequest(HubPage.Pomodoro),
            new NavigationRequest(HubPage.Games),
            new NavigationRequest(HubPage.Settings)
        ], received);
    }

    [Fact]
    public async Task PetContextMenuFactory_Create_IncludesExpectedSharedAndTrayOnlyItems()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var store = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempDir, "menu"));
            await using var transport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
            await using var social = new SocialCoordinatorService(transport, store, new SoloProgressionService());
            var petVm = new PetWindowViewModel(store, social);

            var petMenu = PetContextMenuFactory.Create(petVm, includeStartupAutoRun: false);
            var trayMenu = PetContextMenuFactory.Create(petVm, includeStartupAutoRun: true);

            static List<string> ExtractHeaders(ContextMenu menu)
                => menu.Items.OfType<MenuItem>()
                    .Select(item => item.Header switch
                    {
                        string s => s,
                        StackPanel sp when sp.Children.Count > 0 && sp.Children[0] is TextBlock tb => tb.Text,
                        _ => string.Empty
                    })
                    .ToList();

            var petHeaders = ExtractHeaders(petMenu);
            var trayHeaders = ExtractHeaders(trayMenu);

            Assert.DoesNotContain(petHeaders, h => h.Contains("자동 실행", StringComparison.Ordinal));
            Assert.Contains(trayHeaders, h => h.Contains("자동 실행", StringComparison.Ordinal));
            Assert.DoesNotContain(petHeaders, h => h.Contains("크기 조절", StringComparison.Ordinal));
            Assert.DoesNotContain(trayHeaders, h => h.Contains("크기 조절", StringComparison.Ordinal));
            Assert.DoesNotContain(petHeaders, h => h.Contains("방화벽", StringComparison.Ordinal));
            Assert.DoesNotContain(trayHeaders, h => h.Contains("방화벽", StringComparison.Ordinal));
            Assert.DoesNotContain(petHeaders, h => h.Contains("클릭 투과", StringComparison.Ordinal));
            Assert.DoesNotContain(trayHeaders, h => h.Contains("클릭 투과", StringComparison.Ordinal));

            foreach (var required in new[]
            {
                "LabPet 열기", "외형", "친구", "성장", "뽀모도로", "코인 게임",
                "크리스마스 트리", "기본 위치로 복귀",
                "설정", "업데이트 확인", "LabPet 종료"
            })
            {
                Assert.Contains(petHeaders, h => h.Contains(required, StringComparison.Ordinal));
                Assert.Contains(trayHeaders, h => h.Contains(required, StringComparison.Ordinal));
            }

            Assert.Equal(Visibility.Collapsed, petMenu.Items.OfType<MenuItem>().First().Visibility);
        });
    }

    [Fact]
    public async Task UpdateMenu_DoesNotOpenHub_AndSharesInstallStateWithAbout()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var finishDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var backend = new TestUpdateBackend { Download = ct => finishDownload.Task.WaitAsync(ct) };
            await using var h = await CreateTestHarnessAsync("update", backend);
            var pet = h.PetVm;
            var menu = PetContextMenuFactory.Create(pet, includeStartupAutoRun: false);
            var tray = PetContextMenuFactory.Create(pet, includeStartupAutoRun: true);
            Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Command == pet.CheckUpdatesCommand);
            Assert.Single(tray.Items.OfType<MenuItem>(), item => item.Command == pet.CheckUpdatesCommand);
            pet.HubNavigationRequested += (_, _) => throw new InvalidOperationException("업데이트 확인은 창을 열 수 없습니다.");

            await pet.CheckUpdatesCommand.ExecuteAsync(null);
            await WpfTestHost.FlushAsync();
            Assert.False(h.Navigation.IsHubOpen);
            Assert.True(pet.IsUpdateBubbleVisible);
            Assert.Contains("v0.4.0", pet.UpdateBubbleText);
            Assert.Equal(Visibility.Visible, menu.Items.OfType<MenuItem>().First().Visibility);
            Assert.Equal(0, backend.Downloads);

            pet.DismissUpdateBubbleCommand.Execute(null);
            Assert.False(pet.IsUpdateBubbleVisible);

            Assert.True(await h.Navigation.NavigateAsync(new NavigationRequest(HubPage.About)));
            var host = Assert.IsType<ContentControl>(h.CurrentWindow!.FindName("PageHost"));
            var view = Assert.IsType<AboutPageView>(host.Content);
            var about = Assert.IsType<AboutPageViewModel>(view.DataContext);
            Assert.True(about.HasUpdateOffer);
            await about.CheckUpdatesCommand.ExecuteAsync(null);
            Assert.False(pet.IsUpdateBubbleVisible);
            Assert.Equal(0, backend.Downloads);

            await pet.CheckUpdatesCommand.ExecuteAsync(null);
            Assert.True(pet.IsUpdateBubbleVisible);
            Assert.True(h.Navigation.IsHubOpen);

            var install = about.InstallUpdateCommand.ExecuteAsync(null);
            Assert.Contains("다운로드 중", about.UpdateStatusText);
            Assert.Contains("다운로드 중", pet.UpdateBubbleText);
            Assert.False(pet.InstallUpdateCommand.CanExecute(null));
            Assert.False(about.InstallUpdateCommand.CanExecute(null));

            finishDownload.SetResult();
            await install;
            await WpfTestHost.FlushAsync();
            Assert.Contains("재시작", pet.UpdateBubbleText);
            Assert.Contains("재시작", about.UpdateStatusText);
            Assert.Equal(1, backend.Downloads);
            Assert.Equal(1, backend.ApplyAttempts);
        });
    }

    [Fact]
    public async Task UpdateMenu_LatestAndPortableUseTextBubbleWithoutOpeningHub()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var backend = new TestUpdateBackend { Available = null };
            await using var h = await CreateTestHarnessAsync("update_status", backend);
            var pet = h.PetVm;

            await pet.CheckUpdatesCommand.ExecuteAsync(null);
            Assert.Contains(pet.BubbleLines, message => message.Text.Contains("최신 버전"));
            Assert.False(pet.IsUpdateBubbleVisible);

            backend.IsInstalled = false;
            await pet.CheckUpdatesCommand.ExecuteAsync(null);
            Assert.Contains(pet.BubbleLines, message => message.Text.Contains("포터블"));
            Assert.False(h.Navigation.IsHubOpen);
            Assert.Equal(0, backend.Downloads);
        });
    }

    [Fact]
    public async Task HubNavigationService_SingleInstance_RestoresMinimized_SyncsSidebar_AndLazyLoadsPages()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var h = await CreateTestHarnessAsync("nav");
            try
            {
                Assert.False(h.Navigation.IsHubOpen);

                Assert.True(await h.Navigation.NavigateAsync(new NavigationRequest(HubPage.Home)));
                Assert.True(h.Navigation.IsHubOpen);
                Assert.NotNull(h.CurrentWindow);
                var firstWindow = h.CurrentWindow!;
                firstWindow.UpdateLayout();
                await WpfTestHost.FlushAsync();

                Assert.Single(firstWindow.CreatedPages);
                Assert.Contains(HubPage.Home, firstWindow.CreatedPages);
                Assert.True(firstWindow.ShellViewModel.PrimaryNavItems.Single(x => x.Page == HubPage.Home).IsSelected);

                Assert.True(await h.Navigation.NavigateAsync(new NavigationRequest(HubPage.Pomodoro)));
                await WpfTestHost.FlushAsync();
                Assert.Same(firstWindow, h.CurrentWindow);
                Assert.Equal(2, firstWindow.CreatedPages.Count);
                Assert.Contains(HubPage.Pomodoro, firstWindow.CreatedPages);
                Assert.True(firstWindow.ShellViewModel.PrimaryNavItems.Single(x => x.Page == HubPage.Pomodoro).IsSelected);
                Assert.False(firstWindow.ShellViewModel.PrimaryNavItems.Single(x => x.Page == HubPage.Home).IsSelected);

                // 최소화 상태에서 설정 페이지로 이동하면 자동 복원 및 Footer 사이드바 선택 동기화
                firstWindow.WindowState = WindowState.Minimized;
                await WpfTestHost.FlushAsync();
                Assert.False(h.Navigation.IsHubVisibleAndNotMinimized);

                Assert.True(await h.Navigation.NavigateAsync(new NavigationRequest(HubPage.Settings)));
                await WpfTestHost.FlushAsync();

                Assert.Same(firstWindow, h.CurrentWindow);
                Assert.Equal(WindowState.Normal, firstWindow.WindowState);
                Assert.True(h.Navigation.IsHubVisibleAndNotMinimized);
                Assert.True(firstWindow.ShellViewModel.FooterNavItems.Single(x => x.Page == HubPage.Settings).IsSelected);
                Assert.All(firstWindow.ShellViewModel.PrimaryNavItems, item => Assert.False(item.IsSelected));
            }
            finally
            {
                h.CloseAll();
            }
        });
    }

    [Fact]
    public async Task HubPages_TimersPauseOnDeactivateAndMinimize_AndResumeOnRestore()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var h = await CreateTestHarnessAsync("timers");
            try
            {
                await h.Navigation.NavigateAsync(new NavigationRequest(HubPage.Home));
                h.CurrentWindow!.UpdateLayout();
                await WpfTestHost.FlushAsync();

                var homeView = Assert.IsType<HomePageView>(h.CurrentWindow.ActivePageView);
                Assert.True(homeView.IsRefreshTimerRunning);

                // 성장 페이지로 전환 -> 홈 타이머 정지, 성장 타이머 시작
                await h.Navigation.NavigateAsync(new NavigationRequest(HubPage.Growth));
                h.CurrentWindow.UpdateLayout();
                await WpfTestHost.FlushAsync();
                var growthView = Assert.IsType<GrowthPageView>(h.CurrentWindow.ActivePageView);
                Assert.False(homeView.IsRefreshTimerRunning);
                Assert.True(growthView.IsRefreshTimerRunning);

                // 외형 편집 -> 프리뷰 타이머 시작, 최소화 -> 정지, 복원 -> 재개
                await h.Navigation.NavigateAsync(new NavigationRequest(HubPage.Appearance));
                h.CurrentWindow.UpdateLayout();
                await WpfTestHost.FlushAsync();
                var appearanceView = Assert.IsType<AppearancePageView>(h.CurrentWindow.ActivePageView);
                Assert.False(growthView.IsRefreshTimerRunning);
                Assert.True(appearanceView.IsPreviewTimerRunning);

                h.CurrentWindow.WindowState = WindowState.Minimized;
                await WpfTestHost.FlushAsync();
                Assert.False(appearanceView.IsPreviewTimerRunning);

                h.CurrentWindow.WindowState = WindowState.Normal;
                await WpfTestHost.FlushAsync();
                Assert.True(appearanceView.IsPreviewTimerRunning);

                // 친구 페이지 -> 턴테이블 프리뷰 타이머, 주변 펫 탭으로 전환 시 정지, 친구 탭 복귀 시 재개
                await h.Navigation.NavigateAsync(new NavigationRequest(HubPage.Friends, SubSection: "Friends"));
                h.CurrentWindow.UpdateLayout();
                await WpfTestHost.FlushAsync();
                var friendsView = Assert.IsType<FriendsPageView>(h.CurrentWindow.ActivePageView);
                Assert.False(appearanceView.IsPreviewTimerRunning);
                Assert.True(friendsView.IsPreviewTimerRunning);

                await h.Navigation.NavigateAsync(new NavigationRequest(HubPage.Friends, SubSection: "Nearby"));
                h.CurrentWindow.UpdateLayout();
                await WpfTestHost.FlushAsync();
                Assert.False(friendsView.IsPreviewTimerRunning);

                await h.Navigation.NavigateAsync(new NavigationRequest(HubPage.Friends, SubSection: "Friends"));
                h.CurrentWindow.UpdateLayout();
                await WpfTestHost.FlushAsync();
                Assert.True(friendsView.IsPreviewTimerRunning);

                h.CurrentWindow.WindowState = WindowState.Minimized;
                await WpfTestHost.FlushAsync();
                Assert.False(friendsView.IsPreviewTimerRunning);
            }
            finally
            {
                h.CloseAll();
            }
        });
    }

    [Fact]
    public async Task HubWindow_Close_PreservesPomodoroAndGameSessionsAcrossReopen()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var h = await CreateTestHarnessAsync("persist-session");
            try
            {
                await h.Navigation.NavigateAsync(new NavigationRequest(HubPage.Pomodoro, Action: HubNavigationAction.StartPomodoroFocus));
                await WpfTestHost.FlushAsync();
                Assert.True(h.Pomodoro.IsRunning);

                await h.Navigation.NavigateAsync(new NavigationRequest(HubPage.Games, TargetGameScreen: GameScreen.HiLow));
                await WpfTestHost.FlushAsync();
                Assert.Equal(GameScreen.HiLow, h.Games.ActiveGameScreen);

                h.Navigation.CloseIfOpen();
                await WpfTestHost.FlushAsync();

                Assert.False(h.Navigation.IsHubOpen);
                Assert.True(h.Pomodoro.IsRunning);
                Assert.Equal(GameScreen.HiLow, h.Games.ActiveGameScreen);

                // 다시 열어도 뽀모도로·게임 세션 상태가 그대로 유지된다.
                await h.Navigation.NavigateAsync(new NavigationRequest(HubPage.Games));
                await WpfTestHost.FlushAsync();
                Assert.True(h.Pomodoro.IsRunning);
                Assert.Equal(GameScreen.HiLow, h.Games.ActiveGameScreen);
            }
            finally
            {
                h.CloseAll();
            }
        });
    }

    [Fact]
    public async Task AppearancePageView_UnsavedChangesGuard_CancelBypassAndLiveProgressionSave()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var h = await CreateTestHarnessAsync("appearance");
            try
            {
                await h.Navigation.NavigateAsync(new NavigationRequest(HubPage.Appearance));
                await WpfTestHost.FlushAsync();

                var appearanceView = Assert.IsType<AppearancePageView>(h.CurrentWindow!.ActivePageView);
                appearanceView.EditorViewModel.PetName = "새이름모찌";
                appearanceView.EditorViewModel.PetScale = 1.25;
                appearanceView.EditorViewModel.PetOpacity = 0.80;
                Assert.True(appearanceView.HasUnsavedChanges);

                // 1. 일반 탭 이동 시 취소 응답 -> 이동 차단 및 사이드바 선택 유지
                var promptCount = 0;
                appearanceView.ConfirmUnsavedPromptOverride = _ =>
                {
                    promptCount++;
                    return Task.FromResult(MessageBoxResult.Cancel);
                };

                var moved = await h.Navigation.NavigateAsync(new NavigationRequest(HubPage.Friends));
                Assert.False(moved);
                Assert.Equal(1, promptCount);
                Assert.Equal(HubPage.Appearance, h.Navigation.CurrentPage);
                Assert.True(h.CurrentWindow.ShellViewModel.PrimaryNavItems.Single(x => x.Page == HubPage.Appearance).IsSelected);

                // 2. 초대 수락(BypassUnsavedPrompt: true) -> 프롬프트 없이 게임 직행 및 임시 초안 유지
                var bypassed = await h.Navigation.NavigateAsync(new NavigationRequest(
                    HubPage.Games,
                    TargetGameScreen: GameScreen.Rps,
                    BypassUnsavedPrompt: true));
                Assert.True(bypassed);
                Assert.Equal(1, promptCount);
                Assert.Equal(HubPage.Games, h.Navigation.CurrentPage);
                Assert.Equal(GameScreen.Rps, h.Games.ActiveGameScreen);

                await h.Navigation.NavigateAsync(new NavigationRequest(HubPage.Appearance));
                Assert.Equal("새이름모찌", appearanceView.EditorViewModel.PetName);
                Assert.True(appearanceView.HasUnsavedChanges);

                // 3. 편집 중 획득한 라이브 코인·레벨은 저장 시 덮어쓰지 않고 보존되어야 함
                h.PetVm.CurrentProfile.SoloProgress.Coins += 500;
                h.PetVm.CurrentProfile.SoloProgress.Level = 5;
                var expectedCoins = h.PetVm.CurrentProfile.SoloProgress.Coins;
                var expectedLevel = h.PetVm.CurrentProfile.SoloProgress.Level;

                await appearanceView.EditorViewModel.SaveCommand.ExecuteAsync(null);
                Assert.False(appearanceView.HasUnsavedChanges);
                Assert.Equal("새이름모찌", h.PetVm.CurrentProfile.PetName);
                Assert.Equal(1.25, h.PetVm.CurrentProfile.PetScale, 2);
                Assert.Equal(0.80, h.PetVm.CurrentProfile.PetOpacity, 2);
                Assert.Equal(expectedCoins, h.PetVm.CurrentProfile.SoloProgress.Coins);
                Assert.Equal(expectedLevel, h.PetVm.CurrentProfile.SoloProgress.Level);
                Assert.Equal(0, h.PetVm.CurrentProfile.SoloProgress.Xp);

                // 4. 변경 후 취소 실행 시 저장된 값으로 원복
                appearanceView.EditorViewModel.PetName = "취소될이름";
                Assert.True(appearanceView.HasUnsavedChanges);
                appearanceView.EditorViewModel.CancelChangesCommand.Execute(null);
                Assert.False(appearanceView.HasUnsavedChanges);
                Assert.Equal("새이름모찌", appearanceView.EditorViewModel.PetName);
            }
            finally
            {
                h.CloseAll();
            }
        });
    }

    [Fact]
    public void HubWindowStateStore_AtomicSave_BackupRecovery_AndVirtualScreenNormalization()
    {
        var storeDir = Path.Combine(_tempDir, "state-store");
        var stateStore = new HubWindowStateStore(storeDir);
        var virtualScreen = new Rect(0, 0, 1920, 1080);

        stateStore.Save(new HubWindowState
        {
            Left = 140,
            Top = 90,
            Width = 1220,
            Height = 780,
            IsMaximized = false,
            IsSidebarCollapsed = true
        }, virtualScreen);
        stateStore.Save(new HubWindowState
        {
            Left = 180,
            Top = 120,
            Width = 1100,
            Height = 740,
            IsMaximized = true,
            IsSidebarCollapsed = false
        }, virtualScreen);

        Assert.True(File.Exists(stateStore.FilePath));
        Assert.True(File.Exists(stateStore.FilePath + ".bak"));

        // 본 파일 손상 시 .bak에서 직전 정상 상태 복구
        File.WriteAllText(stateStore.FilePath, "{corrupt-json");
        var recovered = stateStore.Load(virtualScreen);
        Assert.Equal(1220, recovered.Width);
        Assert.Equal(780, recovered.Height);
        Assert.True(recovered.IsSidebarCollapsed);

        // 화면 밖 좌표 및 최소 크기 미만 값 보정
        var outOfBounds = new HubWindowState
        {
            Left = 9000,
            Top = -400,
            Width = 300,
            Height = 200
        }.Normalize(virtualScreen);

        Assert.Equal(HubWindowState.DefaultWidth, outOfBounds.Width);
        Assert.Equal(HubWindowState.DefaultHeight, outOfBounds.Height);
        Assert.InRange(outOfBounds.Left!.Value, -outOfBounds.Width + 120, 1920 - 120);
        Assert.InRange(outOfBounds.Top!.Value, 0, 1080 - 80);
    }

    [Fact]
    public async Task HubWindow_VisitAllNinePagesAndClose_ReleasesMemory_AndTracksVisibilityEvents()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            await using var h = await CreateTestHarnessAsync("gc-all");
            try
            {
                var visibilityStates = new List<bool>();
                h.Navigation.HubVisibilityChanged += (_, _) => visibilityStates.Add(h.Navigation.IsHubVisibleAndNotMinimized);

                var (firstWinRef, firstShellRef, pageRefs) = await VisitAllPagesAndCloseAsync(h);
                await FlushSystemIdleAsync();

                // WPF 단일 Dispatcher 세션의 직전 활성 Window 캐시 슬롯을 비우기 위해 2회차 열기·닫기 수행
                await OpenSinglePageAndCloseAsync(h);
                await FlushSystemIdleAsync();

                for (var i = 0; i < 6 && (firstWinRef.IsAlive || firstShellRef.IsAlive || pageRefs.Any(p => p.Ref.IsAlive)); i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }

                var alivePages = pageRefs.Where(p => p.Ref.IsAlive).Select(p => p.Page.ToString()).ToList();
                Assert.True(alivePages.Count == 0, $"해제되지 않은 페이지: {string.Join(", ", alivePages)}");
                Assert.False(firstShellRef.IsAlive, "1회차 HubViewModel이 GC 회수되지 않았습니다.");
                Assert.False(firstWinRef.IsAlive, "1회차 HubWindow가 GC 회수되지 않았습니다.");
                Assert.NotEmpty(visibilityStates);
                Assert.False(h.Navigation.IsHubOpen);
                Assert.False(h.Navigation.IsHubVisibleAndNotMinimized);
            }
            finally
            {
                h.CloseAll();
            }
        });

        [MethodImpl(MethodImplOptions.NoInlining)]
        static async Task<(WeakReference WindowRef, WeakReference ShellVmRef, List<(HubPage Page, WeakReference Ref)> PageRefs)> VisitAllPagesAndCloseAsync(TestHarness h)
        {
            var pageRefs = new List<(HubPage Page, WeakReference Ref)>();
            foreach (var page in Enum.GetValues<HubPage>())
            {
                Assert.True(await h.Navigation.NavigateAsync(new NavigationRequest(page)));
                h.CurrentWindow!.UpdateLayout();
                await WpfTestHost.FlushAsync();
                pageRefs.Add((page, new WeakReference(h.CurrentWindow.ActivePageView)));
            }

            Assert.Equal(9, h.CurrentWindow!.CreatedPages.Count);
            Assert.True(h.Navigation.IsHubVisibleAndNotMinimized);

            var winRef = new WeakReference(h.CurrentWindow);
            var shellRef = new WeakReference(h.CurrentWindow.ShellViewModel);
            h.Navigation.CloseIfOpen();
            h.CurrentWindow = null;
            await WpfTestHost.FlushAsync();
            return (winRef, shellRef, pageRefs);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static async Task OpenSinglePageAndCloseAsync(TestHarness h)
        {
            Assert.True(await h.Navigation.NavigateAsync(new NavigationRequest(HubPage.Home)));
            h.CurrentWindow!.UpdateLayout();
            await WpfTestHost.FlushAsync();
            h.Navigation.CloseIfOpen();
            h.CurrentWindow = null;
            await WpfTestHost.FlushAsync();
        }

        static Task FlushSystemIdleAsync()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, () => tcs.SetResult());
            return tcs.Task;
        }
    }

    private async Task<TestHarness> CreateTestHarnessAsync(string name, TestUpdateBackend? updateBackend = null)
    {
        var dir = Path.Combine(_tempDir, name);
        var store = new JsonAppDataStore(new StateMigrator(), dir);
        var profile = new PetProfile
        {
            OwnerName = "테스터",
            PetName = "모찌",
            PetScale = 1.0,
            PetOpacity = 1.0,
            SoloProgress = { Level = 2, Xp = 40, Coins = 300 }
        };
        await store.SaveProfileAsync(profile);

        var transport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
        var progression = new SoloProgressionService();
        var social = new SocialCoordinatorService(transport, store, progression);
        await social.StartAsync(profile);

        var updateService = updateBackend is null ? new UpdateService() : new UpdateService(updateBackend);
        var petVm = new PetWindowViewModel(store, social, updates: updateService);
        petVm.ApplyProfile(profile);

        var pomodoro = new PomodoroTimerService();
        var analyzer = new StubAudioAnalyzer();
        var games = new CoinGamesViewModel(store, progression, social);
        games.BindProfile(profile);

        var meshFactory = new ProceduralPetMeshFactory();
        var portraitRenderer = new PetPortraitRenderer(meshFactory);
        var christmasTree = new TerminalChristmasTreeService(store);
        var stateStore = new HubWindowStateStore(store);

        var harness = new TestHarness(store, transport, social, petVm, pomodoro, analyzer, games, portraitRenderer, updateService);

        HubNavigationService? navigation = null;
        navigation = new HubNavigationService(() =>
        {
            var shellVm = new HubViewModel(navigation!, petVm, games, portraitRenderer);
            var window = new HubWindow(
                shellVm,
                page => page switch
                {
                    HubPage.Home => new HomePageView(new HomePageViewModel(navigation!, petVm, social, pomodoro, games, portraitRenderer)),
                    HubPage.Appearance => new AppearancePageView(new OnboardingViewModel(store), petVm, meshFactory),
                    HubPage.Friends => new FriendsPageView(new SocialViewModel(social), petVm, meshFactory),
                    HubPage.Growth => new GrowthPageView(new SoloHubViewModel(pomodoro, analyzer, games), petVm),
                    HubPage.Pomodoro => new PomodoroPageView(new SoloHubViewModel(pomodoro, analyzer, games), pomodoro, petVm),
                    HubPage.Games => new GamesPageView(games, petVm),
                    HubPage.Seasonal => new SeasonalPageView(new SeasonalPageViewModel(petVm, christmasTree)),
                    HubPage.Settings => new SettingsPageView(new SettingsPageViewModel(petVm, store)),
                    HubPage.About => new AboutPageView(new AboutPageViewModel(petVm, updateService, new UpdateSourceOptions())),
                    _ => throw new ArgumentOutOfRangeException(nameof(page))
                },
                stateStore)
            {
                Left = -20000,
                Top = -20000
            };

            harness.CurrentWindow = window;
            return window;
        });

        harness.Navigation = navigation;
        return harness;
    }

    private sealed class TestHarness(
        IAppDataStore store,
        LanP2pTransport transport,
        SocialCoordinatorService social,
        PetWindowViewModel petVm,
        PomodoroTimerService pomodoro,
        StubAudioAnalyzer analyzer,
        CoinGamesViewModel games,
        PetPortraitRenderer portraitRenderer,
        UpdateService updateService) : IAsyncDisposable
    {
        public IAppDataStore Store { get; } = store;
        public SocialCoordinatorService Social { get; } = social;
        public PetWindowViewModel PetVm { get; } = petVm;
        public PomodoroTimerService Pomodoro { get; } = pomodoro;
        public CoinGamesViewModel Games { get; } = games;
        public HubNavigationService Navigation { get; set; } = null!;
        public HubWindow? CurrentWindow { get; set; }

        public void CloseAll()
        {
            Navigation.CloseIfOpen();
            if (CurrentWindow is { IsVisible: true })
            {
                CurrentWindow.ForceCloseWithoutPrompt();
            }
            CurrentWindow = null;
        }

        public async ValueTask DisposeAsync()
        {
            CloseAll();
            Games.Dispose();
            Pomodoro.Reset();
            analyzer.Dispose();
            portraitRenderer.ClearCache();
            PetVm.DisposeBubbles();
            await PetVm.DisposeAsync();
            await updateService.DisposeAsync();
            await Social.DisposeAsync();
            await transport.DisposeAsync();
        }
    }

    private sealed class StubAudioAnalyzer : IAudioAnalyzer
    {
        public bool IsAvailable => true;
        public string ActiveDeviceName => "테스트 오디오";
        public void Start() { }
        public void Stop() { }
        public AudioFrameSnapshot GetCurrentSnapshot() => new(0.15f, 0.25f, 0.1f, false, false, true, ActiveDeviceName);
        public void Dispose() { }
    }
}
