using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LabPet.Client.Audio;
using LabPet.Client.Games;
using LabPet.Client.Interop;
using LabPet.Client.Navigation;
using LabPet.Client.Network;
using LabPet.Client.Rendering;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Client.ViewModels;
using LabPet.Client.Views;
using LabPet.Client.Views.Games;
using LabPet.Client.Views.Hub;
using LabPet.Client.Views.Hub.Pages;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;
using Xunit;

namespace LabPet.Tests;

internal static class WpfTestHost
{
    private static readonly object InitLock = new();
    private static readonly SemaphoreSlim RunGate = new(1, 1);
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
        string[] requiredUris =
        [
            "pack://application:,,,/LabPet.Client;component/Themes/LabPetTheme.xaml",
            "pack://application:,,,/LabPet.Client;component/Themes/GameUiResources.xaml",
            "pack://application:,,,/LabPet.Client;component/Themes/HubDesignSystem.xaml"
        ];

        foreach (var uriString in requiredUris)
        {
            var uri = new Uri(uriString, UriKind.Absolute);
            if (!app.Resources.MergedDictionaries.Any(d => d.Source == uri))
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
            }
        }
    }

    public static void Run(Action action)
        => EnsureDispatcher().Invoke(action);

    public static async Task RunAsync(Func<Task> func)
    {
        await RunGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = EnsureDispatcher().BeginInvoke(async () =>
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
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            RunGate.Release();
        }
    }

    public static Task FlushAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EnsureDispatcher().BeginInvoke(DispatcherPriority.ApplicationIdle, () => tcs.SetResult());
        return tcs.Task;
    }
}

[CollectionDefinition("WpfUi", DisableParallelization = true)]
public sealed class WpfUiTestCollection
{
}

[Collection("WpfUi")]
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
    public async Task InviteCardPresenter_KeepsOneCardWithHubOpenAndStopsQueuedUpdatesOnPetClose()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var petVm = new PetWindowViewModel(_hostStore, _hostSocial);
            petVm.ApplyProfile(_hostProfile);
            using var games = new CoinGamesViewModel(_hostStore, new SoloProgressionService(), _hostSocial);
            games.BindProfile(_hostProfile);
            using var sender = new CoinGamesViewModel(_guestStore, new SoloProgressionService(), _guestSocial);
            sender.BindProfile(_guestProfile);
            var portraits = new PetPortraitRenderer(new ProceduralPetMeshFactory());
            using var audio = new TestAudioAnalyzer();
            using var hook = new GlobalActivityHookService();
            var pet = new PetWindow(petVm, new ProceduralPetMeshFactory(), portraits, new PetAnimationController(),
                audio, hook, new SoloProgressionService(), new PomodoroTimerService(), _hostSocial)
            {
                Left = 420, Top = 380, ShowActivated = false
            };
            petVm.ApplyProfile(_hostProfile);
            using var presenter = new GameInviteCardPresenter(games, pet, pet.GetInviteCardObstacle, pet.GetInviteCardHead);
            pet.SpeechBubbleLayoutChanged += (_, _) => presenter.RequestUpdate();
            HubWindow? hub = null;
            HubNavigationService? navigation = null;
            navigation = new HubNavigationService(() => hub = new HubWindow(
                new HubViewModel(navigation!, petVm, games, portraits),
                _ => new GamesPageView(games, petVm), new HubWindowStateStore(_hostStore.DataDirectory)));
            pet.Show();
            try
            {
                await navigation.NavigateAsync(new NavigationRequest(HubPage.Games));
                games.IncomingInviteStatus = GameInviteStatus.Pending;
                games.IncomingInviteRemainingSeconds = 19;
                var invite = new IncomingGameInvite(
                    _guestProfile.PetId, "초대 발신자", _guestAsFriend, RpsGameEngine.GameTypeId,
                    Guid.NewGuid().ToString("N"), 10);
                games.IncomingInvite = invite;
                await WpfTestHost.FlushAsync();

                var card = Assert.Single(pet.OwnedWindows.OfType<GameInviteCardWindow>());
                Assert.True(card.IsVisible);
                Assert.Same(games, card.DataContext);
                var banner = Assert.IsType<Border>(hub!.FindName("GameInviteBanner"));
                Assert.Equal(Visibility.Visible, banner.Visibility);
                petVm.ShowBubble("두 창이 열릴 때도 유지하는 말풍선", 12_000);
                await WpfTestHost.FlushAsync();
                var obstacle = pet.GetInviteCardObstacle();
                Assert.False(obstacle.IsEmpty);
                Assert.True(Win32WindowHelper.TryGetWindowPhysicalBounds(card, out var relocated));
                Assert.False(obstacle.IntersectsWith(relocated));

                hub.WindowState = WindowState.Minimized;
                await WpfTestHost.FlushAsync();
                hub.WindowState = WindowState.Normal;
                games.IncomingInviteStatus = GameInviteStatus.Accepting;
                await WpfTestHost.FlushAsync();
                Assert.Same(card, Assert.Single(pet.OwnedWindows.OfType<GameInviteCardWindow>()));
                Assert.False(Assert.IsType<Button>(hub.FindName("GameInviteBannerAccept")).IsEnabled);
                var acceptCard = Assert.Single(FindButtons(card), button => button.Command == games.AcceptIncomingInviteCommand);
                Assert.False(acceptCard.IsEnabled);

                games.IncomingInviteStatus = GameInviteStatus.Expired;
                games.IncomingInvite = null;
                await WpfTestHost.FlushAsync();
                Assert.Empty(pet.OwnedWindows.OfType<GameInviteCardWindow>());
                Assert.Equal(Visibility.Collapsed, banner.Visibility);

                sender.SelectedRpsFriend = sender.OnlineFriends.Single();
                sender.RpsWager = 30;
                await SendInviteAsync();
                var decline = Assert.Single(FindButtons(hub), button => button.Command == games.DeclineIncomingInviteCommand);
                var declined = WaitUntilAsync(sender, () => !sender.IsRpsWaitingResponse);
                decline.Command.Execute(null);
                await declined.WaitAsync(TimeSpan.FromSeconds(5));
                await WpfTestHost.FlushAsync();
                Assert.Empty(pet.OwnedWindows.OfType<GameInviteCardWindow>());
                Assert.Equal(Visibility.Collapsed, banner.Visibility);
                Assert.Equal(200, _hostProfile.SoloProgress.Coins);

                await SendInviteAsync();
                var activeCard = Assert.Single(pet.OwnedWindows.OfType<GameInviteCardWindow>());
                var accept = Assert.Single(FindButtons(activeCard), button => button.Command == games.AcceptIncomingInviteCommand);
                var accepted = WaitUntilAsync(games, () => games.IncomingInviteStatus == GameInviteStatus.Accepted);
                accept.Command.Execute(null);
                await accepted.WaitAsync(TimeSpan.FromSeconds(5));
                await WpfTestHost.FlushAsync();
                Assert.Empty(pet.OwnedWindows.OfType<GameInviteCardWindow>());
                Assert.Equal(Visibility.Collapsed, banner.Visibility);
                Assert.Equal(170, _hostProfile.SoloProgress.Coins);

                games.IncomingInviteStatus = GameInviteStatus.Pending;
                games.IncomingInvite = invite;
                presenter.RequestUpdate();
                pet.Close();
                await WpfTestHost.FlushAsync();
                Assert.Empty(pet.OwnedWindows.OfType<GameInviteCardWindow>());
            }
            finally
            {
                if (pet.IsVisible) pet.Close();
                navigation.CloseIfOpen();
            }

            async Task SendInviteAsync()
            {
                var received = WaitUntilAsync(games, () => games.HasIncomingInvite);
                await sender.SendRpsInviteCommand.ExecuteAsync(null);
                await received.WaitAsync(TimeSpan.FromSeconds(5));
                await WpfTestHost.FlushAsync();
                Assert.Equal(Visibility.Visible, Assert.IsType<Border>(hub!.FindName("GameInviteBanner")).Visibility);
            }
        });

        static IEnumerable<Button> FindButtons(DependencyObject element)
        {
            if (element is Button button) yield return button;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            {
                foreach (var child in FindButtons(VisualTreeHelper.GetChild(element, i))) yield return child;
            }
        }
    }

    [Fact]
    public void InviteCardPlacement_PrefersAbovePetAndStepsAsideForBubble()
    {
        var work = new Rect(0, 0, 1920, 1080);
        var pet = new Rect(800, 440, 220, 200);
        var size = new Size(320, 220);
        var top = GameInviteCardWindow.CalculatePlacement(pet, size, work);
        Assert.True(top.Y + size.Height < pet.Top);
        var bubble = new Rect(top, size);
        var side = GameInviteCardWindow.CalculatePlacement(pet, size, work, obstacle: bubble);
        Assert.False(new Rect(side, size).IntersectsWith(bubble));
        Assert.True(side.X + size.Width < pet.Left);
    }

    [Fact]
    public async Task SnowballButtons_ShareOneCommandAndCooldownAcrossAllThreeViews()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            _hostTransport.HandleDiscoveryHello(new DiscoveryHelloPacket
            {
                PetId = _guestProfile.PetId, PetName = _guestProfile.PetName,
                TcpPort = _guestTransport.BoundTcpPort, AppVersion = "0.3.13"
            }, IPAddress.Loopback.ToString(), CancellationToken.None);
            using var snowballs = new SnowballInteractionService(_hostTransport);
            using var receiver = new SnowballInteractionService(_guestTransport);
            var received = new List<Guid>();
            receiver.Received += (_, e) => received.Add(e.EventId);
            var factory = new ProceduralPetMeshFactory();
            var vm = new PetWindowViewModel(_hostStore, _hostSocial, snowballs);
            using var audio = new TestAudioAnalyzer();
            using var hook = new GlobalActivityHookService();
            var pet = new PetWindow(vm, factory, new PetPortraitRenderer(factory), new PetAnimationController(),
                audio, hook, new SoloProgressionService(), new PomodoroTimerService(), _hostSocial)
            {
                Left = 400, Top = 350, ShowActivated = false
            };
            var visit = new VisitorPetWindow(_guestAsFriend, _guestAsFriend.CachedAppearance, factory, audio, _hostSocial,
                snowballs: snowballs);
            using var page = new FriendsPageView(new SocialViewModel(_hostSocial, snowballs), vm, factory);
            var hub = new Window { Content = page, Width = 840, Height = 580, ShowActivated = false };
            vm.ApplyProfile(_hostProfile);
            vm.RefreshQuickOverlayEntries();
            pet.Show();
            hub.Show();
            visit.Show();
            try
            {
                var popup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(pet.FindName("FriendOverlayPopup"));
                popup.IsOpen = true;
                await WpfTestHost.FlushAsync();
                var quickButton = Assert.Single(Descendants<Button>(popup.Child),
                    button => AutomationName(button) == "눈던지기");
                var hubButton = Assert.IsType<Button>(page.FindName("SnowballQuickReactionButton"));
                var visitorMenu = Assert.IsType<Viewport3D>(visit.FindName("VisitorViewport")).ContextMenu;
                var visitButton = Assert.Single(visitorMenu.Items.OfType<MenuItem>(),
                    item => AutomationName(item) == "눈던지기");
                var state = Assert.Single(vm.QuickOverlayEntries).Snowball!;
                Assert.Same(state.Command, quickButton.Command);
                Assert.Same(state.Command, hubButton.Command);
                Assert.Same(state.Command, visitButton.Command);
                Assert.Equal(26, quickButton.ActualWidth);
                Assert.Equal(24, quickButton.ActualHeight);

                foreach (var command in new[] { quickButton.Command, hubButton.Command, visitButton.Command })
                {
                    Assert.True(command.CanExecute(null));
                    var send = state.Command.ExecuteAsync(null);
                    Assert.False(quickButton.Command.CanExecute(null));
                    Assert.False(hubButton.Command.CanExecute(null));
                    Assert.False(visitButton.Command.CanExecute(null));
                    Assert.Equal("1초", state.Icon);
                    await send.WaitAsync(TimeSpan.FromSeconds(4));
                    await WpfTestHost.FlushAsync();
                    Assert.Equal("❄", state.Icon);
                }
                Assert.Equal(3, received.Distinct().Count());
            }
            finally
            {
                visit.Close();
                hub.Close();
                pet.Close();
            }
        });

        static string AutomationName(DependencyObject element) =>
            System.Windows.Automation.AutomationProperties.GetName(element);

        static IEnumerable<T> Descendants<T>(DependencyObject element) where T : DependencyObject
        {
            if (element is T match) yield return match;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(element, i))) yield return child;
        }
    }

    [Fact]
    public async Task SnowballDesktopScene_UsesTemporaryOrLiveVisitorRigAndCleansUpWhenTargetHides()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var factory = new ProceduralPetMeshFactory();
            var vm = new PetWindowViewModel(_hostStore, _hostSocial);
            using var audio = new TestAudioAnalyzer();
            using var hook = new GlobalActivityHookService();
            var animator = new PetAnimationController();
            var pet = new PetWindow(vm, factory, new PetPortraitRenderer(factory), animator, audio,
                hook, new SoloProgressionService(), new PomodoroTimerService(), _hostSocial)
            {
                Left = 400, Top = 350, ShowActivated = false
            };
            var visit = new VisitorPetWindow(_guestAsFriend, _guestAsFriend.CachedAppearance, factory, audio, _hostSocial)
            {
                Left = 650, Top = 350, ShowActivated = false
            };
            using var scene = new SnowballDesktopScene(pet, factory,
                id => id == _guestProfile.PetId && visit.IsVisible ? visit : null);
            vm.ApplyProfile(_hostProfile);
            pet.Show();

            SnowballOverlayWindow GetOverlay() => Assert.Single(Application.Current.Windows.OfType<SnowballOverlayWindow>());
            try
            {
                var sender = Guid.Parse(_guestProfile.PetId);
                await WpfTestHost.FlushAsync();
                Assert.True(scene.TryReceive(sender, Guid.NewGuid(), _guestAsFriend.CachedAppearance));
                var overlay = GetOverlay();
                Assert.Single(overlay.Scene.Children.OfType<SnowballPetVisual>());
                await WaitForHitAsync();
                pet.Hide();
                Assert.False(overlay.IsVisible);
                Assert.Empty(Application.Current.Windows.OfType<SnowballOverlayWindow>());

                pet.Show();
                visit.Show();
                var recovery = System.Diagnostics.Stopwatch.StartNew();
                while (animator.IsReactingToSnowball && recovery.Elapsed < TimeSpan.FromSeconds(2))
                    await Task.Delay(15);
                Assert.False(animator.IsReactingToSnowball);
                visit.OpenChatComposer();
                visit.ChatComposerInputText = "보내지 않은 메시지";
                Assert.True(visit.ShowChatMessage("투척 중 유지할 말풍선", 12_000));
                await WpfTestHost.FlushAsync();
                var position = new Point(visit.Left, visit.Top);
                Assert.True(scene.TryReceive(sender, Guid.NewGuid(), _guestAsFriend.CachedAppearance));
                Assert.Empty(GetOverlay().Scene.Children.OfType<SnowballPetVisual>());
                await WaitForHitAsync();
                Assert.Equal(position, new Point(visit.Left, visit.Top));
                Assert.True(visit.IsChatComposerOpen);
                Assert.Equal("보내지 않은 메시지", visit.ChatComposerInputText);
                Assert.False(visit.CurrentBubblePhysicalBounds.IsEmpty);
                scene.RemoveVisitor(_guestProfile.PetId);
                pet.Hide();
                Assert.Empty(Application.Current.Windows.OfType<SnowballOverlayWindow>());
            }
            finally
            {
                visit.Close();
                pet.Close();
            }

            async Task WaitForHitAsync()
            {
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (!animator.IsReactingToSnowball && deadline.Elapsed < TimeSpan.FromSeconds(5))
                    await Task.Delay(15);
                Assert.True(animator.IsReactingToSnowball);
            }
        });
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
    public async Task InviteBannerAndDesktopCard_KeepLongContentAndActionsWithinTheLayout()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var petVm = new PetWindowViewModel(_hostStore, _hostSocial);
            petVm.ApplyProfile(_hostProfile);
            using var games = new CoinGamesViewModel(_hostStore, new SoloProgressionService(), _hostSocial);
            games.BindProfile(_hostProfile);
            var portraits = new PetPortraitRenderer(new ProceduralPetMeshFactory());
            var sender = "길이가 긴 친구 이름과 펫 이름을 모두 보여 주는 테스트";
            var prizes = string.Join(", ", Enumerable.Repeat("사무실에서 함께 쓰는 간식 선택권 ×1", 6));
            games.IncomingInviteStatus = GameInviteStatus.Pending;
            games.IncomingInviteRemainingSeconds = 19;
            games.IncomingInvite = new IncomingGameInvite(
                _guestProfile.PetId, sender, _guestAsFriend, LabLadderEngine.GameTypeId,
                Guid.NewGuid().ToString("N"), 30, PrizeSummary: prizes);

            HubWindow? hub = null;
            GamesPageView? gamesPage = null;
            HubNavigationService? navigation = null;
            navigation = new HubNavigationService(() =>
            {
                var shell = new HubViewModel(navigation!, petVm, games, portraits) { IsSidebarCollapsed = false };
                return hub = new HubWindow(shell, _ => gamesPage = new GamesPageView(games, petVm), new HubWindowStateStore(_hostStore.DataDirectory));
            });
            var card = new GameInviteCardWindow(games) { Left = -20000, Top = -20000 };
            try
            {
                await navigation.NavigateAsync(new NavigationRequest(HubPage.Games));
                hub!.Left = hub.Top = -20000;
                hub.Width = hub.MinWidth;
                hub.Height = hub.MinHeight;
                hub.UpdateLayout();
                card.Show();
                card.UpdateLayout();
                await WpfTestHost.FlushAsync();

                var banner = Assert.IsType<Border>(hub.FindName("GameInviteBanner"));
                var bannerSummary = Assert.IsType<TextBlock>(hub.FindName("GameInviteBannerSummary"));
                var bannerAccept = Assert.IsType<Button>(hub.FindName("GameInviteBannerAccept"));
                Assert.Contains(prizes, bannerSummary.Text);
                Assert.True(bannerSummary.ActualHeight > bannerSummary.FontSize * 2);
                var summaryBounds = AssertWithin(banner, bannerSummary);
                var actionBounds = AssertWithin(banner, bannerAccept);
                Assert.True(summaryBounds.Right < actionBounds.Left);
                var dashboard = Assert.IsType<GamesHubView>(gamesPage!.GamesHub);
                var dashboardScroll = Assert.IsType<ScrollViewer>(dashboard.FindName("DashboardCardsScroll"));
                Assert.True(dashboardScroll.ScrollableHeight > 0);
                AssertWithin(dashboardScroll, Assert.IsType<TextBlock>(dashboard.FindName("RpsDashboardTitle")));
                dashboardScroll.ScrollToBottom();
                hub.UpdateLayout();
                AssertWithin(dashboardScroll, Assert.IsType<TextBlock>(dashboard.FindName("LadderDashboardTitle")));

                var cardRoot = Assert.IsType<Border>(card.FindName("InviteCardRoot"));
                var cardSender = Assert.IsType<TextBlock>(card.FindName("InviteSenderName"));
                var cardPrizes = Assert.IsType<TextBlock>(card.FindName("InvitePrizeSummary"));
                Assert.Equal(sender, cardSender.Text);
                Assert.Contains(prizes, new System.Windows.Documents.TextRange(cardPrizes.ContentStart, cardPrizes.ContentEnd).Text);
                Assert.True(cardSender.ActualHeight > cardSender.FontSize * 2);
                Assert.True(cardPrizes.ActualHeight > cardPrizes.FontSize * 2);
                AssertWithin(cardRoot, cardPrizes);
                AssertWithin(cardRoot, Assert.IsType<Button>(card.FindName("InviteAccept")));
            }
            finally
            {
                card.Close();
                hub?.Close();
                portraits.ClearCache();
            }
        });
    }

    [Fact]
    public async Task CompactGameViews_KeepInviteActionsAndGestureEmojiReachable()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            using var games = new CoinGamesViewModel(_hostStore, new SoloProgressionService(), _hostSocial);
            games.BindProfile(_hostProfile);
            var wait = new OneToOneInviteWaitView
            {
                DataContext = new OneToOneInviteWaitState
                {
                    GameTitle = "타이핑 대결",
                    OpponentDisplayName = "아주 긴 상대방 펫 이름과 사용자 이름",
                    StatusDetailText = string.Concat(Enumerable.Repeat("상대가 초대를 등록하고 준비할 때까지 기다리는 중입니다. ", 4))
                }
            };
            var rps = new RpsGameView { DataContext = games };
            var window = new Window { Left = -20000, Top = -20000, Width = 548, Height = 330, Content = wait, ShowActivated = false };
            try
            {
                window.Show();
                window.UpdateLayout();
                var waitScroll = Assert.IsType<ScrollViewer>(wait.FindName("InviteWaitScroll"));
                Assert.True(waitScroll.ScrollableHeight > 0);
                waitScroll.ScrollToBottom();
                window.UpdateLayout();
                AssertWithin(waitScroll, Assert.IsType<Button>(wait.FindName("CancelInviteWait")));

                games.RpsPhase = RpsPhase.Choosing;
                games.CanChooseRpsMove = true;
                games.RpsMySelectedMove = RpsMove.Rock;
                window.Content = rps;
                window.UpdateLayout();
                await WpfTestHost.FlushAsync();
                var arenaScroll = Assert.IsType<ScrollViewer>(rps.FindName("RpsArenaScroll"));
                Assert.True(arenaScroll.ScrollableHeight > 0);
                arenaScroll.ScrollToBottom();
                window.UpdateLayout();

                foreach (var (name, emoji) in new[] { ("ScissorsChoiceEmoji", "✌️"), ("RockChoiceEmoji", "✊"), ("PaperChoiceEmoji", "✋") })
                {
                    var glyph = Assert.IsType<TextBlock>(rps.FindName(name));
                    Assert.Equal(emoji, glyph.Text);
                    AssertWithin(arenaScroll, glyph);
                }

                var myMove = Assert.IsType<TextBlock>(rps.FindName("RpsMyMoveEmoji"));
                var opponent = Assert.IsType<TextBlock>(rps.FindName("RpsOpponentMoveEmoji"));
                Assert.Equal("✊", myMove.Text);
                Assert.False(opponent.IsVisible);
                games.RpsRevealedOpponentMove = RpsMove.Paper;
                games.RpsPhase = RpsPhase.Result;
                arenaScroll.ScrollToTop();
                window.UpdateLayout();
                Assert.Equal("✋", opponent.Text);
                AssertWithin(arenaScroll, myMove);
                AssertWithin(arenaScroll, opponent);

                games.LadderStage = LadderStage.Lobby;
                games.LadderRoomPrizesText = string.Concat(Enumerable.Repeat("사무실에서 쓰는 간식 선택권 ×1, ", 6));
                games.LadderStatusText = "모든 응답이 끝났습니다. 참가자를 확정하면 위치를 선택할 수 있습니다.";
                games.ShowConfirmLadderButton = true;
                var ladder = new LadderGameView { DataContext = games };
                window.Content = ladder;
                window.UpdateLayout();
                await WpfTestHost.FlushAsync();
                var lobbyPrizes = Assert.IsType<TextBlock>(ladder.FindName("LobbyPrizeText"));
                Assert.True(lobbyPrizes.ActualHeight > lobbyPrizes.FontSize * 2);
                AssertWithin(ladder, lobbyPrizes);
                AssertWithin(ladder, Assert.IsType<Button>(ladder.FindName("LobbyConfirmButton")));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static Rect AssertWithin(FrameworkElement container, FrameworkElement element)
    {
        Assert.True(element.IsVisible);
        Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0);
        var bounds = element.TransformToAncestor(container).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Left >= -1 && bounds.Top >= -1
            && bounds.Right <= container.ActualWidth + 1 && bounds.Bottom <= container.ActualHeight + 1,
            $"요소 {element.Name} 배치 {bounds}, 상위 영역 {container.RenderSize}");
        return bounds;
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
            hostGames.InviteExpireSeconds = 0.5;
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
            hostGames.InviteExpireSeconds = 20;
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
                    Action = "offer",
                    WagerCoins = 10,
                    InviteExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(20)
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
    public async Task SoloLadderResizeAndCloseReopen_PreservesAnimationWithoutPrematureSettle()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var portraitRenderer = new PetPortraitRenderer(new BlenderAssetPetMeshFactory(new ProceduralPetMeshFactory()));
            using var hostGames = new CoinGamesViewModel(_hostStore, new SoloProgressionService(), _hostSocial, portraitRenderer);
            hostGames.BindProfile(_hostProfile);
            hostGames.IsLadderMultiplayerMode = false;
            hostGames.SelectSoloLadderSlotCommand.Execute(1);
            hostGames.SoloLadderBetCoins = 30;
            await hostGames.StartSoloLadderCommand.ExecuteAsync(null);

            Assert.True(hostGames.IsSoloLadderRunning);
            Assert.Equal(SoloLadderPhase.Running, hostGames.SoloLadderPhase);
            Assert.Equal(170, _hostProfile.SoloProgress.Coins);
            Assert.NotNull(_hostProfile.SoloProgress.PendingSoloLadderSession);

            // 솔로 사다리 뷰를 오프스크린 창에 마운트해 실제 IsVisible 상태에서 반응형 리사이즈·창 닫기 후 재오픈 복원을 검증한다
            hostGames.ActiveGameScreen = GameScreen.Ladder;
            var hostWin = new Window
            {
                Left = -20000,
                Top = -20000,
                Width = 840,
                Height = 580,
                ShowActivated = false,
                Content = new GamesHubView { DataContext = hostGames }
            };
            hostWin.Show();
            await WpfTestHost.FlushAsync();

            hostWin.Width = 1120;
            hostWin.Height = 720;
            hostWin.UpdateLayout();
            await Task.Delay(120);
            await WpfTestHost.FlushAsync();

            // 창을 닫아도 완료 전에는 절대 강제 공개·정산하지 않고 진행률만 자동 보존한다
            hostWin.Close();
            await WpfTestHost.FlushAsync();

            Assert.True(hostGames.IsSoloLadderRunning);
            Assert.Equal(SoloLadderPhase.Running, hostGames.SoloLadderPhase);
            Assert.InRange(hostGames.ActiveSoloLadderAnimationProgress, 0.01, 0.99);
            Assert.Equal(170, _hostProfile.SoloProgress.Coins);

            hostGames.SaveSoloLadderAnimationProgress(0.45);
            Assert.Equal(0.45, hostGames.ActiveSoloLadderAnimationProgress, precision: 3);

            // 새 ViewModel + 창으로 재오픈 시 기존 세션이 리롤 없이 복원되고 완료 시 1회만 정산된다
            using var reopenedGames = new CoinGamesViewModel(_hostStore, new SoloProgressionService(), _hostSocial, portraitRenderer);
            reopenedGames.BindProfile(await _hostStore.LoadProfileAsync());
            reopenedGames.ActiveGameScreen = GameScreen.Ladder;
            Assert.Equal(SoloLadderPhase.Running, reopenedGames.SoloLadderPhase);
            Assert.Equal(170, _hostProfile.SoloProgress.Coins);

            var reopenedWin = new Window
            {
                Left = -20000,
                Top = -20000,
                Width = 900,
                Height = 620,
                ShowActivated = false,
                Content = new GamesHubView { DataContext = reopenedGames }
            };
            reopenedWin.Show();
            await WpfTestHost.FlushAsync();

            await reopenedGames.CompleteSoloLadderTraceAsync();
            await reopenedGames.CompleteSoloLadderTraceAsync();
            Assert.False(reopenedGames.IsSoloLadderRunning);
            Assert.Equal(SoloLadderPhase.Result, reopenedGames.SoloLadderPhase);
            Assert.Null((await _hostStore.LoadProfileAsync()).SoloProgress.PendingSoloLadderSession);

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

            await WaitUntilAsync(hostGames, () => hostGames.TypingPhase == TypingPhase.Result).WaitAsync(TimeSpan.FromSeconds(5));

            // 2. 사다리 초대 수락 시 대시보드 없이 Lobby 진입
            guestGames.OpenGameScreenCommand.Execute(GameScreen.Dashboard);
            hostGames.LadderFriendPicker.SelectAllCommand.Execute(null);
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

            // 초기에는 아무도 선택되지 않으며, 선택한 친구는 이름 변경 재연결 후에도 PetId 기준으로 선택이 유지된다.
            var candidate = Assert.Single(hostGames.LadderInviteCandidates);
            Assert.False(candidate.IsSelected);
            hostGames.LadderFriendPicker.SetSelection(_guestProfile.PetId, true, raiseEvent: true);
            Assert.True(Assert.Single(hostGames.LadderInviteCandidates).IsSelected);
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

            // HubWindow 생성 -> 탭/4종 게임 화면 반복 전환 & 리사이즈 -> 닫기 및 약참조 회수 검증
            WeakReference windowRef = OpenExerciseAndCloseHub(
                _hostStore,
                _hostSocial,
                _hostProfile,
                new PomodoroTimerService(),
                new TestAudioAnalyzer(),
                hostGames,
                portraitRenderer);

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
        static WeakReference OpenExerciseAndCloseHub(
            IAppDataStore store,
            SocialCoordinatorService social,
            PetProfile profile,
            PomodoroTimerService pomodoroSvc,
            IAudioAnalyzer analyzer,
            CoinGamesViewModel gamesVm,
            PetPortraitRenderer portraitRenderer)
        {
            var petVm = new PetWindowViewModel(store, social);
            petVm.ApplyProfile(profile);
            var soloVm = new SoloHubViewModel(pomodoroSvc, analyzer, gamesVm);
            soloVm.BindProfile(profile);

            HubWindow? createdWindow = null;
            HubNavigationService? navigation = null;
            navigation = new HubNavigationService(() =>
            {
                var shellVm = new HubViewModel(navigation!, petVm, gamesVm, portraitRenderer);
                createdWindow = new HubWindow(
                    shellVm,
                    page => page switch
                    {
                        HubPage.Growth => new GrowthPageView(soloVm, petVm),
                        HubPage.Pomodoro => new PomodoroPageView(soloVm, pomodoroSvc, petVm),
                        HubPage.Games => new GamesPageView(gamesVm, petVm),
                        _ => throw new ArgumentOutOfRangeException(nameof(page))
                    },
                    new HubWindowStateStore(store.DataDirectory))
                {
                    Left = -20000,
                    Top = -20000
                };
                return createdWindow;
            });

            _ = navigation.NavigateAsync(new NavigationRequest(HubPage.Growth)).GetAwaiter().GetResult();
            var win = createdWindow!;
            win.Width = 840;
            win.Height = 580;
            win.UpdateLayout();

            _ = navigation.NavigateAsync(new NavigationRequest(HubPage.Games)).GetAwaiter().GetResult();
            for (var round = 0; round < 3; round++)
            {
                foreach (var screen in new[] { GameScreen.HiLow, GameScreen.Rps, GameScreen.Typing, GameScreen.Ladder, GameScreen.Dashboard })
                {
                    gamesVm.ActiveGameScreen = screen;
                    win.UpdateLayout();
                }
            }

            win.Width = 1080;
            win.Height = 720;
            win.UpdateLayout();
            var shellRef = new WeakReference(win.ShellViewModel);
            win.Close();
            return shellRef;
        }
    }

    [Fact]
    public async Task HubWindow_PageSwitching_HidesGamesHubWhenGrowthOrPomodoroSelected()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var petVm = new PetWindowViewModel(_hostStore, _hostSocial);
            petVm.ApplyProfile(_hostProfile);
            using var gamesVm = new CoinGamesViewModel(_hostStore, new SoloProgressionService(), _hostSocial);
            using var soloVm = new SoloHubViewModel(new PomodoroTimerService(), new TestAudioAnalyzer(), gamesVm);
            soloVm.BindProfile(_hostProfile);
            var portraitRenderer = new PetPortraitRenderer(new ProceduralPetMeshFactory());

            GamesPageView? gamesPage = null;
            HubWindow? win = null;
            HubNavigationService? navigation = null;
            navigation = new HubNavigationService(() =>
            {
                var shellVm = new HubViewModel(navigation!, petVm, gamesVm, portraitRenderer);
                win = new HubWindow(
                    shellVm,
                    page => page switch
                    {
                        HubPage.Growth => new GrowthPageView(soloVm, petVm),
                        HubPage.Pomodoro => new PomodoroPageView(soloVm, new PomodoroTimerService(), petVm),
                        HubPage.Games => gamesPage ??= new GamesPageView(gamesVm, petVm),
                        _ => throw new ArgumentOutOfRangeException(nameof(page))
                    },
                    new HubWindowStateStore(_hostStore.DataDirectory))
                {
                    Left = -20000,
                    Top = -20000
                };
                return win;
            });

            try
            {
                await navigation.NavigateAsync(new NavigationRequest(HubPage.Growth));
                win!.Left = -20000;
                win.Top = -20000;
                win.UpdateLayout();
                await WpfTestHost.FlushAsync();

                Assert.Equal(HubPage.Growth, navigation.CurrentPage);
                Assert.Null(gamesPage);

                await navigation.NavigateAsync(new NavigationRequest(HubPage.Pomodoro));
                win.UpdateLayout();
                await WpfTestHost.FlushAsync();
                Assert.Equal(HubPage.Pomodoro, navigation.CurrentPage);
                Assert.Null(gamesPage);

                await navigation.NavigateAsync(new NavigationRequest(HubPage.Games, TargetGameScreen: GameScreen.HiLow));
                win.UpdateLayout();
                await WpfTestHost.FlushAsync();
                Assert.NotNull(gamesPage);
                Assert.True(gamesPage!.GamesHub.IsVisible);

                await navigation.NavigateAsync(new NavigationRequest(HubPage.Growth));
                win.UpdateLayout();
                await WpfTestHost.FlushAsync();
                Assert.False(gamesPage.GamesHub.IsVisible);
            }
            finally
            {
                win?.Close();
                portraitRenderer.ClearCache();
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
            Assert.Contains(hub.CoinEarningGuides, item => item.ConditionText.Contains("200타") && item.RewardBadge == "+1 코인");
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
