using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LabPet.Client.Audio;
using LabPet.Client.Games;
using LabPet.Client.Interop;
using LabPet.Client.Network;
using LabPet.Client.Rendering;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Client.ViewModels;
using LabPet.Client.Views;
using LabPet.Client.Views.Games;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;
using Xunit;

namespace LabPet.Tests;

internal static class WpfTestHost
{
    private static readonly object InitLock = new();
    private static Dispatcher? _dispatcher;

    private static Dispatcher EnsureDispatcher()
    {
        lock (InitLock)
        {
            if (_dispatcher is not null)
            {
                return _dispatcher;
            }

            if (Application.Current is { Dispatcher: { } existing })
            {
                EnsureThemeResources(Application.Current);
                _dispatcher = existing;
                return _dispatcher;
            }

            using var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                var app = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                EnsureThemeResources(app);
                _dispatcher = app.Dispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            return _dispatcher!;
        }
    }

    private static void EnsureThemeResources(Application app)
    {
        if (app.Resources.MergedDictionaries.Count >= 2)
        {
            return;
        }

        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/LabPet.Client;component/Themes/LabPetTheme.xaml", UriKind.Absolute)
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/LabPet.Client;component/Themes/GameUiResources.xaml", UriKind.Absolute)
        });
    }

    public static void Run(Action action)
        => EnsureDispatcher().Invoke(action);

    public static Task RunAsync(Func<Task> func)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EnsureDispatcher().BeginInvoke(async () =>
        {
            try
            {
                await func();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    public static Task FlushAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EnsureDispatcher().BeginInvoke(DispatcherPriority.ApplicationIdle, () => tcs.SetResult());
        return tcs.Task;
    }
}

public sealed class GameUiUxAndInviteTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "LabPet_GameUx_" + Guid.NewGuid().ToString("N"));
    private LanP2pTransport _hostTransport = null!;
    private LanP2pTransport _guestTransport = null!;
    private JsonAppDataStore _hostStore = null!;
    private JsonAppDataStore _guestStore = null!;
    private SocialCoordinatorService _hostSocial = null!;
    private SocialCoordinatorService _guestSocial = null!;
    private PetProfile _hostProfile = null!;
    private PetProfile _guestProfile = null!;
    private FriendEntry _guestAsFriend = null!;
    private FriendEntry _hostAsFriend = null!;

    public async Task InitializeAsync()
    {
        _hostStore = new JsonAppDataStore(new StateMigrator(), Path.Combine(_dataDir, "host"));
        _guestStore = new JsonAppDataStore(new StateMigrator(), Path.Combine(_dataDir, "guest"));
        _hostTransport = new LanP2pTransport(0, 0, 0);
        _guestTransport = new LanP2pTransport(0, 0, 0);
        _hostSocial = new SocialCoordinatorService(_hostTransport, _hostStore, new SoloProgressionService());
        _guestSocial = new SocialCoordinatorService(_guestTransport, _guestStore, new SoloProgressionService());

        var secret = PacketSigner.GeneratePairSecretBase64();
        _hostProfile = new PetProfile { OwnerName = "방장", PetName = "모찌", SoloProgress = { Coins = 200 } };
        _guestProfile = new PetProfile
        {
            OwnerName = "친구",
            PetName = "몽실",
            SoloProgress = { Coins = 200 },
            Appearance = new PetAppearance { SpeciesId = PetSpeciesIds.ChibiCat, BodyColorHex = "#FFD8C8" }
        };

        await _hostStore.SaveProfileAsync(_hostProfile);
        await _guestStore.SaveProfileAsync(_guestProfile);
        await _hostTransport.StartAsync(_hostProfile, []);
        await _guestTransport.StartAsync(_guestProfile, []);

        _guestAsFriend = new FriendEntry
        {
            PetId = _guestProfile.PetId,
            OwnerName = _guestProfile.OwnerName,
            PetName = _guestProfile.PetName,
            CachedAppearance = _guestProfile.Appearance.Clone(),
            LastKnownIp = IPAddress.Loopback.ToString(),
            LastKnownTcpPort = _guestTransport.BoundTcpPort,
            PairSecretBase64 = secret
        };
        _hostAsFriend = new FriendEntry
        {
            PetId = _hostProfile.PetId,
            OwnerName = _hostProfile.OwnerName,
            PetName = _hostProfile.PetName,
            CachedAppearance = _hostProfile.Appearance.Clone(),
            LastKnownIp = IPAddress.Loopback.ToString(),
            LastKnownTcpPort = _hostTransport.BoundTcpPort,
            PairSecretBase64 = secret
        };

        await _hostStore.SaveFriendsAsync(new FriendsStore { Friends = [_guestAsFriend] });
        await _guestStore.SaveFriendsAsync(new FriendsStore { Friends = [_hostAsFriend] });
        await _hostSocial.StartAsync(_hostProfile);
        await _guestSocial.StartAsync(_guestProfile);
        _hostTransport.UpdateKnownFriends([_guestAsFriend]);
        _guestTransport.UpdateKnownFriends([_hostAsFriend]);

        var discovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _hostSocial.StateChanged += (_, _) =>
        {
            if (_hostSocial.DiscoveredPeers.Any(p => p.PetId == _guestProfile.PetId))
            {
                discovered.TrySetResult();
            }
        };
        _hostTransport.HandleDiscoveryHello(new DiscoveryHelloPacket
        {
            PetId = _guestProfile.PetId,
            OwnerName = _guestProfile.OwnerName,
            PetName = _guestProfile.PetName,
            TcpPort = _guestTransport.BoundTcpPort,
            Appearance = _guestProfile.Appearance.Clone(),
            AppVersion = "0.2.0"
        }, IPAddress.Loopback.ToString(), CancellationToken.None);
        await discovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async Task DisposeAsync()
    {
        await _hostSocial.DisposeAsync();
        await _guestSocial.DisposeAsync();
        await _hostTransport.DisposeAsync();
        await _guestTransport.DisposeAsync();

        for (var attempt = 0; attempt < 5 && Directory.Exists(_dataDir); attempt++)
        {
            try
            {
                Directory.Delete(_dataDir, recursive: true);
                break;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(50);
            }
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(2.0)]
    public void InviteCardPlacement_StaysInsideMultiDpiAndSecondaryMonitorWorkArea(double dpiScale)
    {
        var workArea = new Rect(-1920 * dpiScale, 0, 1920 * dpiScale, 1040 * dpiScale);
        var cardSize = new Size(316 * dpiScale, 220 * dpiScale);
        var gap = 12 * dpiScale;

        // 펫이 오른쪽 하단에 있을 때는 왼쪽에 배치된다
        var petBottomRight = new Rect(-250 * dpiScale, 760 * dpiScale, 214 * dpiScale, 234 * dpiScale);
        var leftPos = GameInviteCardWindow.CalculatePlacement(petBottomRight, cardSize, workArea, gap);
        var leftRect = new Rect(leftPos, cardSize);
        Assert.True(workArea.Contains(leftRect));
        Assert.False(petBottomRight.IntersectsWith(leftRect));
        Assert.True(leftRect.Right <= petBottomRight.Left);

        // 펫이 보조 모니터 왼쪽 상단 구석에 있을 때는 오른쪽/화면 안쪽으로 보정된다
        var petTopLeft = new Rect(-1915 * dpiScale, 6 * dpiScale, 214 * dpiScale, 234 * dpiScale);
        var clampedPos = GameInviteCardWindow.CalculatePlacement(petTopLeft, cardSize, workArea, gap);
        var clampedRect = new Rect(clampedPos, cardSize);
        Assert.True(workArea.Contains(clampedRect));
        Assert.False(petTopLeft.IntersectsWith(clampedRect));
    }

    [Fact]
    public async Task InviteCardWindow_DoesNotStealFocus_AllowsClickEvenDuringClickThroughMode()
    {
        await WpfTestHost.RunAsync(() =>
        {
            var portraitRenderer = new PetPortraitRenderer(new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory()));
            using var guestGames = new CoinGamesViewModel(_guestStore, new SoloProgressionService(), _guestSocial, portraitRenderer);
            guestGames.BindProfile(_guestProfile);

            var cardWindow = new GameInviteCardWindow(guestGames);
            try
            {
                Assert.False(cardWindow.ShowActivated);
                Assert.False(cardWindow.Focusable);
                cardWindow.Left = -20000;
                cardWindow.Top = -20000;
                cardWindow.Show();

                var hwnd = new WindowInteropHelper(cardWindow).Handle;
                Assert.NotEqual(IntPtr.Zero, hwnd);
                var exStyle = GetWindowLongPtr(hwnd, -20);
                const int wsExNoActivate = 0x08000000;
                const int wsExToolWindow = 0x00000080;
                const int wsExTransparent = 0x00000020;

                // 포커스를 빼앗지 않고 Alt+Tab 숨김이며, 클릭 투과(WS_EX_TRANSPARENT)는 적용되지 않아 클릭 가능
                Assert.NotEqual(0, exStyle & wsExNoActivate);
                Assert.NotEqual(0, exStyle & wsExToolWindow);
                Assert.Equal(0, exStyle & wsExTransparent);
            }
            finally
            {
                cardWindow.Close();
                portraitRenderer.ClearCache();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task InviteStateMachine_TracksPendingCancelExpireAndPreventsDuplicateAccept()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var portraitRenderer = new PetPortraitRenderer(new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory()));
            using var hostGames = new CoinGamesViewModel(_hostStore, new SoloProgressionService(), _hostSocial, portraitRenderer);
            using var guestGames = new CoinGamesViewModel(_guestStore, new SoloProgressionService(), _guestSocial, portraitRenderer);
            hostGames.BindProfile(_hostProfile);
            guestGames.BindProfile(_guestProfile);

            // 1. 짧은 만료(0.25s) 설정 -> 만료 시 Expired 상태 전이 및 방장 측 대기 자동 종료
            guestGames.InviteExpireSeconds = 0.25;
            var guestInvited = WaitUntilAsync(guestGames, () => guestGames.HasIncomingInvite);
            hostGames.RpsWager = 30;
            hostGames.SelectedRpsFriend = hostGames.OnlineFriends.Single();
            await hostGames.SendRpsInviteCommand.ExecuteAsync(null);
            await guestInvited.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(GameInviteStatus.Pending, guestGames.IncomingInviteStatus);
            Assert.NotNull(guestGames.IncomingInvitePortrait);

            var hostCanceledByExpire = WaitUntilAsync(hostGames, () => !hostGames.IsRpsWaitingResponse);
            await hostCanceledByExpire.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(GameInviteStatus.Expired, guestGames.IncomingInviteStatus);
            Assert.False(guestGames.HasIncomingInvite);

            // 2. 방장이 먼저 초대를 취소한 뒤 수락을 시도해도 코인이 차감되거나 입장되지 않는다
            guestGames.InviteExpireSeconds = 20;
            var guestInvited2 = WaitUntilAsync(guestGames, () => guestGames.HasIncomingInvite);
            await hostGames.SendRpsInviteCommand.ExecuteAsync(null);
            await guestInvited2.WaitAsync(TimeSpan.FromSeconds(5));

            var guestClearedByCancel = WaitUntilAsync(guestGames, () => !guestGames.HasIncomingInvite);
            await hostGames.SendRpsInviteCommand.ExecuteAsync(null);
            await guestClearedByCancel.WaitAsync(TimeSpan.FromSeconds(5));
            await guestGames.AcceptIncomingInviteCommand.ExecuteAsync(null);
            Assert.Equal(200, _guestProfile.SoloProgress.Coins);
            Assert.False(guestGames.IsRpsInMatch);

            // 3. 정상 수락 시 중복 실행해도 1회만 차감되고 즉시 RPS 화면으로 직행한다
            GameScreen? openedScreen = null;
            guestGames.OpenGameScreenRequested += (_, s) => openedScreen = s;
            var guestInvited3 = WaitUntilAsync(guestGames, () => guestGames.HasIncomingInvite);
            await hostGames.SendRpsInviteCommand.ExecuteAsync(null);
            await guestInvited3.WaitAsync(TimeSpan.FromSeconds(5));

            var hostInRps = WaitUntilAsync(hostGames, () => hostGames.RpsPhase == RpsPhase.Choosing);
            await Task.WhenAll(
                guestGames.AcceptIncomingInviteCommand.ExecuteAsync(null),
                guestGames.AcceptIncomingInviteCommand.ExecuteAsync(null));
            await hostInRps.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(170, _guestProfile.SoloProgress.Coins);
            Assert.Equal(GameInviteStatus.Accepted, guestGames.IncomingInviteStatus);
            Assert.Equal(GameScreen.Rps, guestGames.ActiveGameScreen);
            Assert.Equal(GameScreen.Rps, openedScreen);
            Assert.Equal(RpsPhase.Choosing, guestGames.RpsPhase);

            // 4. 진행 중 게임 도중 다른 초대가 와도 현재 게임을 방해하지 않고 조용히 거절한다
            var unrelatedSession = Guid.NewGuid().ToString("N");
            await guestGames.HandleGameEventAsync(new GameEventReceivedEventArgs(
                _hostProfile.PetId,
                new GameEventPayload
                {
                    GameType = TypingSprintEngine.GameTypeId,
                    SessionId = unrelatedSession,
                    Action = "invite",
                    WagerCoins = 10
                }));
            Assert.False(guestGames.HasIncomingInvite);
            Assert.Equal(GameScreen.Rps, guestGames.ActiveGameScreen);
            Assert.Equal(RpsPhase.Choosing, guestGames.RpsPhase);

            portraitRenderer.ClearCache();
        });
    }

    [Fact]
    public async Task DirectGameNavigationAndStateSeparation_WorkForHiLowRpsTypingAndLadder()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var hostGames = new CoinGamesViewModel(_hostStore, new SoloProgressionService(), _hostSocial);
            using var guestGames = new CoinGamesViewModel(_guestStore, new SoloProgressionService(), _guestSocial);
            hostGames.BindProfile(_hostProfile);
            guestGames.BindProfile(_guestProfile);

            // 1. 하이로우: Idle -> Playing -> Result -> Idle
            Assert.Equal(HiLowPhase.Idle, hostGames.HiLowPhase);
            await hostGames.StartHiLowCommand.ExecuteAsync(null);
            Assert.Equal(HiLowPhase.Playing, hostGames.HiLowPhase);
            Assert.True(hostGames.IsHiLowActive);
            Assert.Equal(HiLowResultKind.None, hostGames.HiLowResultKind);
            hostGames.ResetHiLowToSetupCommand.Execute(null);
            Assert.Equal(HiLowPhase.Playing, hostGames.HiLowPhase); // 진행 중 초기화 불가

            // 1연승 도달 시 정산하거나 실패 시 Result 단계로 전환된다
            for (var guard = 0; guard < 12 && hostGames.IsHiLowActive; guard++)
            {
                if (hostGames.CanCashOutHiLow)
                {
                    await hostGames.CashOutHiLowCommand.ExecuteAsync(null);
                    break;
                }

                if (hostGames.HiLowCurrentCardValue <= 7)
                {
                    await hostGames.GuessHigherCommand.ExecuteAsync(null);
                }
                else
                {
                    await hostGames.GuessLowerCommand.ExecuteAsync(null);
                }
            }

            Assert.Equal(HiLowPhase.Result, hostGames.HiLowPhase);
            hostGames.ResetHiLowToSetupCommand.Execute(null);
            Assert.Equal(HiLowPhase.Idle, hostGames.HiLowPhase);

            // 2. 가위바위보: 상대 commit 수신 시에도 상대 패는 Result까지 비공개
            var guestInvitedRps = WaitUntilAsync(guestGames, () => guestGames.HasIncomingInvite);
            hostGames.SelectedRpsFriend = hostGames.OnlineFriends.Single();
            hostGames.RpsWager = 10;
            await hostGames.SendRpsInviteCommand.ExecuteAsync(null);
            await guestInvitedRps.WaitAsync(TimeSpan.FromSeconds(5));
            await guestGames.AcceptIncomingInviteCommand.ExecuteAsync(null);

            var hostInRps = WaitUntilAsync(hostGames, () => hostGames.RpsPhase == RpsPhase.Choosing);
            await hostInRps.WaitAsync(TimeSpan.FromSeconds(5));

            var guestCommitted = WaitUntilAsync(guestGames, () => guestGames.RpsOpponentCommitted);
            await hostGames.ChooseRpsMoveCommand.ExecuteAsync("rock");
            await guestCommitted.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(guestGames.RpsOpponentCommitted);
            Assert.Null(guestGames.RpsRevealedOpponentMove);
            Assert.Equal("?", guestGames.RpsOpponentMoveText);
            Assert.Equal(RpsPhase.Choosing, guestGames.RpsPhase);

            var bothResolved = Task.WhenAll(
                WaitUntilAsync(hostGames, () => hostGames.RpsPhase == RpsPhase.Result),
                WaitUntilAsync(guestGames, () => guestGames.RpsPhase == RpsPhase.Result));
            await guestGames.ChooseRpsMoveCommand.ExecuteAsync("scissors");
            await bothResolved.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(RpsMove.Scissors, hostGames.RpsRevealedOpponentMove);
            Assert.Equal(RpsMove.Rock, guestGames.RpsRevealedOpponentMove);
            Assert.Equal(RpsOutcome.Win, hostGames.RpsLastOutcome);
            Assert.Equal(RpsOutcome.Lose, guestGames.RpsLastOutcome);

            // 3. 타이핑: 3초 카운트다운(테스트 단축) -> 10초 실행 -> 자동 문장 전환 -> Result
            guestGames.TypingCountdownDurationSeconds = 0.15;
            guestGames.TypingSprintDurationSeconds = 0.35;
            var coinsBeforePractice = _guestProfile.SoloProgress.Coins;
            guestGames.StartSoloTypingPracticeCommand.Execute(null);
            Assert.Equal(TypingPhase.Countdown, guestGames.TypingPhase);
            Assert.False(guestGames.IsTypingRunning);

            await WaitUntilAsync(guestGames, () => guestGames.TypingPhase == TypingPhase.Running).WaitAsync(TimeSpan.FromSeconds(5));
            var firstPrompt = guestGames.TypingPromptText;
            guestGames.TypingInputText = firstPrompt;
            Assert.Equal(1, guestGames.TypingCompletedCount);
            Assert.NotEqual(firstPrompt, guestGames.TypingPromptText);

            await WaitUntilAsync(guestGames, () => guestGames.TypingPhase == TypingPhase.Result).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(coinsBeforePractice, _guestProfile.SoloProgress.Coins);
        });
    }

    [Fact]
    public async Task Ladder_EightPlayersResizeAndCloseReopen_PreservesAnimationWithoutPrematureSettle()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var portraitRenderer = new PetPortraitRenderer(new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory()));
            using var hostGames = new CoinGamesViewModel(_hostStore, new SoloProgressionService(), _hostSocial, portraitRenderer);
            hostGames.BindProfile(_hostProfile);
            hostGames.LadderEntryFee = 0;
            hostGames.LadderParticipantsText = "승민, 대현, 민수, 지영, 현우, 서연, 도윤, 하은";
            hostGames.LadderPrizesText = "커피 x1, 간식 x1, 통과 x6";
            await hostGames.RunLadderCommand.ExecuteAsync(null);

            Assert.True(hostGames.IsLadderAnimating);
            Assert.Equal(LadderStage.Running, hostGames.LadderStage);

            // 사다리 뷰를 오프스크린 창에 마운트해 실제 IsVisible 상태에서 8명 반응형 리사이즈·창 닫기 후 재오픈 복원을 검증한다
            hostGames.ActiveGameScreen = GameScreen.Ladder;
            var hostWin = new Window
            {
                Left = -20000,
                Top = -20000,
                Width = 760,
                Height = 520,
                ShowActivated = false,
                Content = new GamesHubView { DataContext = hostGames }
            };
            hostWin.Show();
            await WpfTestHost.FlushAsync();

            hostWin.Width = 1120;
            hostWin.Height = 720;
            hostWin.UpdateLayout();
            await Task.Delay(140);
            await WpfTestHost.FlushAsync();

            // 창을 닫아도 완료 전에는 절대 강제 공개·정산하지 않고 진행률만 자동 보존한다
            hostGames.RevealLadderParticipantResult(0);
            hostWin.Close();
            await WpfTestHost.FlushAsync();

            Assert.True(hostGames.IsLadderAnimating);
            Assert.Equal(LadderStage.Running, hostGames.LadderStage);
            Assert.InRange(hostGames.ActiveLadderAnimationProgress, 0.01, 0.99);
            Assert.EndsWith("→  ?", hostGames.LadderResultLines[1]);

            hostGames.SaveLadderAnimationProgress(0.45);
            Assert.Equal(0.45, hostGames.ActiveLadderAnimationProgress, precision: 3);

            // 새 창으로 재오픈 시 상태 복원 후 완료 시 결과 단계로 1회 정산된다
            var reopenedWin = new Window
            {
                Left = -20000,
                Top = -20000,
                Width = 900,
                Height = 620,
                ShowActivated = false,
                Content = new GamesHubView { DataContext = hostGames }
            };
            reopenedWin.Show();
            await WpfTestHost.FlushAsync();

            await hostGames.CompleteLadderRevealAsync();
            Assert.False(hostGames.IsLadderAnimating);
            Assert.Equal(LadderStage.Result, hostGames.LadderStage);
            Assert.Equal(8, hostGames.LadderResultCards.Count);
            Assert.Contains("무료 모드", hostGames.LadderResultSettlementText);

            reopenedWin.Close();
            portraitRenderer.ClearCache();
        });
    }

    [Fact]
    public void EnumEqualsConverter_ConvertBack_SupportsEnumIntAndBool()
    {
        var converter = new EnumEqualsConverter();
        Assert.Equal(SoloHubSection.Games, converter.ConvertBack(true, typeof(SoloHubSection), "Games", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(30, converter.ConvertBack(true, typeof(int), "30", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(false, converter.ConvertBack(true, typeof(bool), "False", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Same(System.Windows.Data.Binding.DoNothing, converter.ConvertBack(false, typeof(int), "30", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task InviteAccept_EntersTypingAndLadderDirectly_WithoutDisturbingCurrentHubViewOnArrival()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var hostGames = new CoinGamesViewModel(_hostStore, new SoloProgressionService(), _hostSocial);
            using var guestGames = new CoinGamesViewModel(_guestStore, new SoloProgressionService(), _guestSocial);
            hostGames.BindProfile(_hostProfile);
            guestGames.BindProfile(_guestProfile);

            var guestHub = new SoloHubViewModel(new PomodoroTimerService(), new TestAudioAnalyzer(), guestGames);
            guestHub.BindProfile(_guestProfile);
            guestHub.SelectSectionCommand.Execute(SoloHubSection.Pomodoro);

            // 1. 타이핑 초대 도착 시 현재 뽀모도로 화면 유지, 수락 시 대시보드 없이 Countdown 진입
            hostGames.TypingCountdownDurationSeconds = 0.25;
            hostGames.TypingSprintDurationSeconds = 0.2;
            guestGames.TypingCountdownDurationSeconds = 0.25;
            guestGames.TypingSprintDurationSeconds = 0.2;
            hostGames.SelectedTypingFriend = hostGames.OnlineFriends.Single();
            hostGames.TypingWager = 10;

            var invitedTyping = WaitUntilAsync(guestGames, () => guestGames.HasIncomingInvite);
            await hostGames.SendTypingInviteCommand.ExecuteAsync(null);
            await invitedTyping.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(SoloHubSection.Pomodoro, guestHub.ActiveSection);
            Assert.Equal(GameScreen.Dashboard, guestGames.ActiveGameScreen);

            GameScreen? requestedScreen = null;
            guestGames.OpenGameScreenRequested += (_, s) => requestedScreen = s;
            await guestGames.AcceptIncomingInviteCommand.ExecuteAsync(null);

            Assert.Equal(GameScreen.Typing, guestGames.ActiveGameScreen);
            Assert.Equal(GameScreen.Typing, requestedScreen);
            Assert.Equal(TypingPhase.Countdown, guestGames.TypingPhase);

            await WaitUntilAsync(guestGames, () => guestGames.TypingPhase == TypingPhase.Result).WaitAsync(TimeSpan.FromSeconds(5));

            // 2. 사다리 초대 수락 시 대시보드 없이 Lobby 진입
            guestGames.OpenGameScreenCommand.Execute(GameScreen.Dashboard);
            hostGames.LadderEntryFee = 0;
            hostGames.LadderPrizesText = "커피 x1, 통과 x1";
            var invitedLadder = WaitUntilAsync(guestGames, () => guestGames.HasIncomingInvite);
            await hostGames.OpenLadderRoomCommand.ExecuteAsync(null);
            await invitedLadder.WaitAsync(TimeSpan.FromSeconds(5));

            await guestGames.AcceptIncomingInviteCommand.ExecuteAsync(null);
            Assert.Equal(GameScreen.Ladder, guestGames.ActiveGameScreen);
            Assert.Equal(GameScreen.Ladder, requestedScreen);
            Assert.Equal(LadderStage.Lobby, guestGames.LadderStage);

            await guestGames.CancelLadderRoomCommand.ExecuteAsync(null);
            guestHub.Dispose();
        });
    }

    [Fact]
    public async Task FriendReconnectAndRepeatedGameViewSwitching_WorkCleanlyAndReleaseMemory()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var portraitRenderer = new PetPortraitRenderer(new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory()));
            using var hostGames = new CoinGamesViewModel(_hostStore, new SoloProgressionService(), _hostSocial, portraitRenderer);
            hostGames.BindProfile(_hostProfile);

            // 인증된 친구가 이름을 바꾸어 재연결(PresenceSync)하면 온라인 친구·사다리 후보가 자동 갱신된다
            var candidate = Assert.Single(hostGames.LadderInviteCandidates);
            Assert.True(candidate.IsSelected);
            _guestProfile.PetName = "몽실-재접속";
            await _guestSocial.UpdateLocalPresenceAsync(_guestProfile, isMusicActive: false);
            var updatedVisible = WaitUntilAsync(hostGames,
                () => hostGames.OnlineFriends.Any(f => f.DisplayName.Contains("몽실-재접속", StringComparison.Ordinal)));
            _hostTransport.HandleDiscoveryHello(new DiscoveryHelloPacket
            {
                PetId = _guestProfile.PetId,
                OwnerName = _guestProfile.OwnerName,
                PetName = "몽실-재접속",
                TcpPort = _guestTransport.BoundTcpPort,
                Appearance = _guestProfile.Appearance.Clone(),
                AppVersion = "0.2.0"
            }, IPAddress.Loopback.ToString(), CancellationToken.None);
            await updatedVisible.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("몽실-재접속", Assert.Single(hostGames.OnlineFriends).DisplayName);
            Assert.Contains("몽실-재접속", Assert.Single(hostGames.LadderInviteCandidates).DisplayName);

            // SoloHubWindow 생성 -> 탭/4종 게임 화면 반복 전환 & 리사이즈 -> 닫기 및 약참조 회수 검증
            WeakReference windowRef = OpenExerciseAndCloseSoloHub(
                _hostProfile,
                new PomodoroTimerService(),
                new TestAudioAnalyzer(),
                hostGames);

            await WpfTestHost.FlushAsync();
            for (var i = 0; i < 6 && windowRef.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.False(windowRef.IsAlive);
            portraitRenderer.ClearCache();
        });

        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference OpenExerciseAndCloseSoloHub(
            PetProfile profile,
            PomodoroTimerService pomodoroSvc,
            IAudioAnalyzer analyzer,
            CoinGamesViewModel gamesVm)
        {
            var hubVm = new SoloHubViewModel(pomodoroSvc, analyzer, gamesVm);
            hubVm.BindProfile(profile);
            var win = new SoloHubWindow(hubVm)
            {
                Left = -20000,
                Top = -20000
            };
            win.Show();
            win.Width = 780;
            win.Height = 580;
            win.UpdateLayout();

            hubVm.SelectSectionCommand.Execute(SoloHubSection.Games);
            for (var round = 0; round < 3; round++)
            {
                foreach (var screen in new[] { GameScreen.HiLow, GameScreen.Rps, GameScreen.Typing, GameScreen.Ladder, GameScreen.Dashboard })
                {
                    gamesVm.ActiveGameScreen = screen;
                    win.UpdateLayout();
                }
            }

            win.Width = 1040;
            win.Height = 720;
            win.UpdateLayout();
            win.Close();
            return new WeakReference(hubVm);
        }
    }

    [Fact]
    public async Task SoloHubWindow_SectionSwitching_HidesGamesHubWhenGrowthOrPomodoroSelected()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var gamesVm = new CoinGamesViewModel(_hostStore, new SoloProgressionService(), _hostSocial);
            using var hubVm = new SoloHubViewModel(new PomodoroTimerService(), new TestAudioAnalyzer(), gamesVm);
            hubVm.BindProfile(_hostProfile);

            var win = new SoloHubWindow(hubVm)
            {
                Left = -20000,
                Top = -20000
            };
            try
            {
                win.Show();
                win.UpdateLayout();
                await WpfTestHost.FlushAsync();

                Assert.Equal(SoloHubSection.GrowthAudio, hubVm.ActiveSection);
                Assert.Equal(Visibility.Collapsed, win.GamesSectionContainer.Visibility);
                Assert.False(win.GamesHubControl.IsVisible);

                hubVm.SelectSectionCommand.Execute(SoloHubSection.Pomodoro);
                win.UpdateLayout();
                await WpfTestHost.FlushAsync();
                Assert.Equal(Visibility.Collapsed, win.GamesSectionContainer.Visibility);
                Assert.False(win.GamesHubControl.IsVisible);

                hubVm.SelectSectionCommand.Execute(SoloHubSection.Games);
                gamesVm.ActiveGameScreen = GameScreen.HiLow;
                win.UpdateLayout();
                await WpfTestHost.FlushAsync();
                Assert.Equal(Visibility.Visible, win.GamesSectionContainer.Visibility);
                Assert.True(win.GamesHubControl.IsVisible);

                hubVm.SelectSectionCommand.Execute(SoloHubSection.GrowthAudio);
                win.UpdateLayout();
                await WpfTestHost.FlushAsync();
                Assert.Equal(Visibility.Collapsed, win.GamesSectionContainer.Visibility);
                Assert.False(win.GamesHubControl.IsVisible);
            }
            finally
            {
                win.Close();
            }
        });
    }

    [Fact]
    public async Task Ladder_DirectFriendSelection_OnlyInvitesCheckedFriends()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var hostGames = new CoinGamesViewModel(_hostStore, new SoloProgressionService(), _hostSocial);
            hostGames.BindProfile(_hostProfile);

            var candidate = Assert.Single(hostGames.LadderInviteCandidates);
            candidate.IsSelected = false;
            Assert.False(hostGames.CanOpenLadderRoom);

            await hostGames.OpenLadderRoomCommand.ExecuteAsync(null);
            Assert.False(hostGames.IsLadderRoomOpen);
            Assert.Equal(LadderStage.Setup, hostGames.LadderStage);
            Assert.Contains("1명 이상", hostGames.LadderStatusText);
        });
    }

    [Fact]
    public void SoloHubViewModel_PomodoroAndGrowthWorkAndDisposeReleasesWithoutLeaks()
    {
        var pomodoro = new PomodoroTimerService();
        var audio = new TestAudioAnalyzer();
        using var games = new CoinGamesViewModel(_hostStore, new SoloProgressionService(), _hostSocial);

        var weakRef = CreateAndDisposeHub(_hostProfile, pomodoro, audio, games);
        for (var i = 0; i < 4 && weakRef.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(weakRef.IsAlive);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference CreateAndDisposeHub(
            PetProfile profile,
            PomodoroTimerService pomodoroSvc,
            IAudioAnalyzer analyzer,
            CoinGamesViewModel gamesVm)
        {
            var hub = new SoloHubViewModel(pomodoroSvc, analyzer, gamesVm);
            hub.BindProfile(profile);
            Assert.Equal(6, hub.CoinEarningGuides.Count);
            Assert.Contains(hub.CoinEarningGuides, item => item.ConditionText.Contains("40타") && item.RewardBadge == "+2 코인");
            hub.SetPomodoroPresetCommand.Execute("15");
            Assert.Equal("15:00", hub.PomodoroTimeText);
            hub.SelectSectionCommand.Execute(SoloHubSection.Games);
            Assert.Equal(SoloHubSection.Games, hub.ActiveSection);
            hub.RefreshAudioUi();
            hub.Dispose();
            return new WeakReference(hub);
        }
    }

    private sealed class TestAudioAnalyzer : IAudioAnalyzer
    {
        public bool IsAvailable => true;
        public string ActiveDeviceName => "테스트 오디오";
        public void Start() { }
        public void Stop() { }
        public AudioFrameSnapshot GetCurrentSnapshot() => new(0.2f, 0.3f, 0.1f, false, false, true, ActiveDeviceName);
        public void Dispose() { }
    }

    private static Task WaitUntilAsync(CoinGamesViewModel vm, Func<bool> condition)
    {
        if (condition())
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, System.ComponentModel.PropertyChangedEventArgs _)
        {
            if (condition())
            {
                vm.PropertyChanged -= Handler;
                tcs.TrySetResult();
            }
        }

        vm.PropertyChanged += Handler;
        if (condition())
        {
            vm.PropertyChanged -= Handler;
            tcs.TrySetResult();
        }

        return tcs.Task;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLongPtr(IntPtr hWnd, int nIndex);
}
