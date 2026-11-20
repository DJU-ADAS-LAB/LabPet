using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using LabPet.Client.Network;
using LabPet.Client.Games;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Client.ViewModels;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;
using Xunit;

namespace LabPet.Tests;

public sealed class SocialCoordinatorTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "LabPet_Social_" + Guid.NewGuid().ToString("N"));
    private readonly LanP2pTransport _hostTransport = new(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
    private readonly LanP2pTransport _guestTransport = new(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
    private readonly PetProfile _hostProfile = new();
    private readonly JsonAppDataStore _store;
    private readonly PausingStore _coordinationStore;
    private readonly SocialCoordinatorService _host;
    private FriendEntry _hostAsFriend = null!;

    public SocialCoordinatorTests()
    {
        _store = new JsonAppDataStore(new StateMigrator(), _dataDirectory);
        _coordinationStore = new PausingStore(_store);
        _host = new SocialCoordinatorService(_hostTransport, _coordinationStore, new SoloProgressionService());
    }

    public async Task InitializeAsync()
    {
        var guestProfile = new PetProfile
        {
            OwnerName = "친구",
            PetName = "게스트",
            Appearance = new PetAppearance { SpeciesId = PetSpeciesIds.FluffyPuppy, BodyColorHex = "#C4905E" }
        };
        var sharedSecret = PacketSigner.GeneratePairSecretBase64();
        await _guestTransport.StartAsync(guestProfile, []);
        await _store.SaveFriendsAsync(new FriendsStore
        {
            Friends =
            [
                new FriendEntry
                {
                    PetId = guestProfile.PetId,
                    OwnerName = guestProfile.OwnerName,
                    PetName = guestProfile.PetName,
                    CachedAppearance = guestProfile.Appearance.Clone(),
                    LastKnownIp = IPAddress.Loopback.ToString(),
                    LastKnownTcpPort = _guestTransport.BoundTcpPort,
                    PairSecretBase64 = sharedSecret,
                    Intimacy = 1
                }
            ]
        });
        await _host.StartAsync(_hostProfile);
        _hostAsFriend = new FriendEntry
        {
            PetId = _hostProfile.PetId,
            LastKnownIp = IPAddress.Loopback.ToString(),
            LastKnownTcpPort = _hostTransport.BoundTcpPort,
            PairSecretBase64 = sharedSecret
        };
        _guestTransport.UpdateKnownFriends([_hostAsFriend]);
    }

    [Fact]
    public async Task LadderSettlement_UsesTheAgreedWagerAndPaysOnlyOnce()
    {
        using var games = new CoinGamesViewModel(_store, new SoloProgressionService(), _host);
        _hostProfile.SoloProgress.Coins = 100;
        games.BindProfile(_hostProfile);
        var friend = Assert.Single(_host.Friends);
        var session = Guid.NewGuid().ToString("N");
        var room = new LadderRoomStateDto
        {
            SessionId = session, HostPetId = friend.PetId, WagerCoins = 10, SlotCount = 2,
            Prizes = ["통과", "잭팟"],
            Members =
            [
                new LadderMemberStateDto { PetId = friend.PetId, DisplayName = "친구", SelectedSlot = 0 },
                new LadderMemberStateDto { PetId = _hostProfile.PetId, DisplayName = "나", SelectedSlot = 1 }
            ]
        };
        Task Receive(string action, int wager, object data) => games.HandleGameEventAsync(new GameEventReceivedEventArgs(
            friend.PetId, new GameEventPayload
            {
                GameType = LabLadderEngine.GameTypeId, SessionId = session, Action = action, WagerCoins = wager,
                Data = JsonSerializer.Serialize(data, ProtocolJson.PacketOptions)
            }));
        await Receive("invite", 10, room);
        await games.AcceptIncomingInviteAsync();
        Assert.Equal(90, _hostProfile.SoloProgress.Coins);

        var start = new LadderStartDto
        {
            SessionId = session, WagerCoins = 10, Participants = ["친구", "나"],
            ParticipantPetIds = [friend.PetId, _hostProfile.PetId],
            Prizes = ["잭팟", "통과"], Rungs = [new LadderRung(1, 0)]
        };
        await Receive("start", 50, start);
        Assert.Equal(90, _hostProfile.SoloProgress.Coins);
        await Receive("start", 10, start);
        await Receive("start", 10, start);
        Assert.Equal(110, (await _store.LoadProfileAsync()).SoloProgress.Coins);
    }

    [Fact]
    public async Task RegistrationAndConcurrentMessages_PersistAllChangesAndKeepEarlierFriendSnapshotStable()
    {
        const int messagesPerDirection = 8;
        var beforeList = _host.Friends;
        var before = Assert.Single(beforeList);
        before.CachedAppearance.ExpressionId = "expr_happy";
        Assert.NotEqual(before.CachedAppearance.ExpressionId, Assert.Single(_host.Friends).CachedAppearance.ExpressionId);

        await using var newPeer = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
        var newPeerProfile = new PetProfile { PetName = "새 친구" };
        await newPeer.StartAsync(newPeerProfile, []);
        var registration = new TaskCompletionSource<LabPet.Shared.Transport.FriendRequestReceivedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        newPeer.FriendRequestReceived += (_, e) => registration.TrySetResult(e);
        var code = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.PairingCodeReady += (_, value) => code.TrySetResult(value);
        var newPeerKey = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        newPeer.FriendAccepted += (_, e) => newPeerKey.TrySetResult(e.PairSecretBase64);
        Assert.True(await _host.SendFriendRequestAsync(new PeerSnapshot
        {
            PetId = newPeerProfile.PetId,
            IpAddress = IPAddress.Loopback.ToString(),
            TcpPort = newPeer.BoundTcpPort
        }));

        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var friendAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedCount = 0;
        _host.FriendMessageReceived += (_, message) =>
        {
            if (message.Text.Contains("수신 메시지", StringComparison.Ordinal)
                && Interlocked.Increment(ref receivedCount) == messagesPerDirection + 1)
            {
                allReceived.TrySetResult();
            }
        };

        _host.SocialNotification += (_, message) =>
        {
            if (message.Contains("친구 신청을 수락했어요", StringComparison.Ordinal))
            {
                friendAccepted.TrySetResult();
            }
        };

        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _coordinationStore.BeforeSaveFriendsAsync = () =>
        {
            saveStarted.TrySetResult();
            return resumeSave.Task;
        };
        var sendTasks = Enumerable.Range(0, messagesPerDirection).SelectMany(index => new[]
        {
            _host.SendBubbleMessageAsync(before, $"호스트 메시지 {index}"),
            _guestTransport.SendBubbleMessageAsync(_hostAsFriend, $"게스트 수신 메시지 {index}")
        }).ToArray();

        try
        {
            await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var request = await registration.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await newPeer.RespondFriendRequestAsync(
                request.RequestId, accept: true, await code.Task.WaitAsync(TimeSpan.FromSeconds(5))));

            // 앞선 저장이 대기 중이어도 새 친구의 첫 메시지는 바로 인증된다.
            var hostAsNewFriend = _hostAsFriend.Clone();
            hostAsNewFriend.PairSecretBase64 = await newPeerKey.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await newPeer.SendBubbleMessageAsync(hostAsNewFriend, "새 친구 수신 메시지"));
        }
        finally
        {
            resumeSave.TrySetResult();
        }

        var results = await Task.WhenAll(sendTasks).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.All(results, result => Assert.True(result));
        await Task.WhenAll(allReceived.Task, friendAccepted.Task).WaitAsync(TimeSpan.FromSeconds(15));

        var guestIntimacy = 1 + 2 * messagesPerDirection;
        var totalMessages = 2 * messagesPerDirection + 1;
        Assert.Single(beforeList);
        Assert.Equal(1, before.Intimacy);
        Assert.Collection(_host.Friends,
            guest => Assert.Equal(guestIntimacy, guest.Intimacy),
            friend => Assert.Equal(newPeerProfile.PetId, friend.PetId));
        Assert.Collection((await _store.LoadFriendsAsync()).Friends,
            guest => Assert.Equal(guestIntimacy, guest.Intimacy),
            friend =>
            {
                Assert.Equal(newPeerProfile.PetId, friend.PetId);
                Assert.Equal(2, friend.Intimacy);
            });
        var savedProfile = await _store.LoadProfileAsync();
        Assert.Equal(totalMessages * 2, savedProfile.SoloProgress.Coins);
        Assert.Equal(totalMessages * 3, savedProfile.SoloProgress.Xp);
    }

    [Fact]
    public async Task SaveFailure_NotifiesAndNextMessagePersistsPendingRewards()
    {
        var notifications = new List<string>();
        _host.SocialNotification += (_, message) => notifications.Add(message);
        var guest = Assert.Single(_host.Friends);
        var friendsFile = Path.Combine(_dataDirectory, "friends.json");

        using (File.Open(friendsFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(await _host.SendBubbleMessageAsync(guest, "첫 메시지"));
        }

        Assert.Single(notifications);
        Assert.Contains("저장하지 못했어요", notifications[0]);
        Assert.True(await _host.SendBubbleMessageAsync(guest, "다음 메시지"));
        Assert.Equal(3, Assert.Single((await _store.LoadFriendsAsync()).Friends).Intimacy);
        Assert.Equal(4, (await _store.LoadProfileAsync()).SoloProgress.Coins);
    }

    [Fact]
    public async Task QuickMessage_SuccessKeepsSameFriendEditorOpenForAnotherMessage()
    {
        var viewModel = new PetWindowViewModel(_store, _host);
        var friend = Assert.Single(_host.Friends);
        var row = new QuickOverlayEntry { PetId = friend.PetId, DisplayTitle = friend.PetName, IsFriend = true, Friend = friend };

        viewModel.OpenQuickMessageCommand.Execute(row);
        viewModel.QuickMessageText = "첫 메시지";
        await viewModel.SendQuickMessageCommand.ExecuteAsync(null);

        Assert.Same(row, viewModel.QuickMessageTarget);
        Assert.True(row.IsComposingMessage);
        Assert.Empty(viewModel.QuickMessageText);
        Assert.Empty(viewModel.BubbleLines);

        viewModel.QuickMessageText = "두 번째 메시지";
        await viewModel.SendQuickMessageCommand.ExecuteAsync(null);
        Assert.Same(row, viewModel.QuickMessageTarget);
        Assert.Empty(viewModel.BubbleLines);
        Assert.Equal(3, Assert.Single((await _store.LoadFriendsAsync()).Friends).Intimacy);
    }

    [Fact]
    public async Task SocialMessage_SuccessClearsTheFailureStatusWithoutASentNotice()
    {
        var viewModel = new SocialViewModel(_host);
        try
        {
            var friend = Assert.Single(_host.Friends);
            var defaultStatus = viewModel.StatusBanner;
            viewModel.SelectedFriend = new FriendListItemViewModel(friend,
                new PeerSnapshot { PetId = friend.PetId }, isFollowingMusic: false, isVisitingDesktop: false);
            viewModel.StatusBanner = "메시지 전송에 실패했습니다.";
            viewModel.MessageText = "다시 전송한 메시지";
            await viewModel.SendBubbleMessageCommand.ExecuteAsync(null);

            Assert.Empty(viewModel.MessageText);
            Assert.Equal(defaultStatus, viewModel.StatusBanner);
            Assert.Equal(2, Assert.Single((await _store.LoadFriendsAsync()).Friends).Intimacy);
        }
        finally
        {
            viewModel.Detach();
        }
    }

    [Fact]
    public async Task ReceivedFriendMessage_CarriesOriginalTextAndIndependentFriendAppearance()
    {
        var received = new TaskCompletionSource<ReceivedFriendMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationCount = 0;
        _host.SocialNotification += (_, _) => Interlocked.Increment(ref notificationCount);
        _host.FriendMessageReceived += (_, message) => received.TrySetResult(message);

        const string text = "안녕: 캐릭터 메시지";
        Assert.True(await _guestTransport.SendBubbleMessageAsync(_hostAsFriend, text));
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(text, message.Text);
        Assert.Equal("친구 (게스트)", message.SenderName);
        Assert.Equal(PetSpeciesIds.FluffyPuppy, message.Appearance.SpeciesId);
        Assert.Equal("#C4905E", message.Appearance.BodyColorHex);
        message.Appearance.SpeciesId = PetSpeciesIds.ChibiCat;
        Assert.Equal(PetSpeciesIds.FluffyPuppy, Assert.Single(_host.Friends).CachedAppearance.SpeciesId);
        Assert.Equal(0, Volatile.Read(ref notificationCount));
    }

    [Fact]
    public async Task BubbleLines_DisplayTheLatestFiveMessagesInOrder()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                dispatcher.Invoke(() =>
                {
                    var viewModel = new PetWindowViewModel(_store, _host);
                    var portrait = new DrawingImage();
                    for (var index = 1; index <= 6; index++)
                    {
                        viewModel.ShowBubble(new FriendMessageBubble($"받은 메시지 {index}", "친구", portrait), durationMs: 100);
                    }

                    Assert.True(viewModel.IsBubbleVisible);
                    Assert.Equal(Enumerable.Range(2, 5).Select(index => $"받은 메시지 {index}"),
                        viewModel.BubbleLines.Select(line => line.Text));
                    Assert.All(viewModel.BubbleLines, line => Assert.Same(portrait, Assert.IsType<FriendMessageBubble>(line).Portrait));

                    var expiryFrame = new DispatcherFrame();
                    viewModel.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(PetWindowViewModel.IsBubbleVisible) && !viewModel.IsBubbleVisible)
                        {
                            expiryFrame.Continue = false;
                        }
                    };
                    Dispatcher.PushFrame(expiryFrame);
                    Assert.Empty(viewModel.BubbleLines);
                });
                completed.SetResult();
            }
            catch (Exception exception)
            {
                completed.SetException(exception);
            }
            finally
            {
                dispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task QuickMessage_LeavesTextTypedDuringAnInFlightSend()
    {
        var viewModel = new PetWindowViewModel(_store, _host);
        var friend = Assert.Single(_host.Friends);
        var row = new QuickOverlayEntry { PetId = friend.PetId, IsFriend = true, Friend = friend };
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _coordinationStore.BeforeSaveFriendsAsync = () =>
        {
            saveStarted.TrySetResult();
            return resumeSave.Task;
        };

        viewModel.OpenQuickMessageCommand.Execute(row);
        viewModel.QuickMessageText = "전송 시작";
        var send = viewModel.SendQuickMessageCommand.ExecuteAsync(null);
        try
        {
            await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            viewModel.QuickMessageText = "전송 중 작성한 다음 메시지";
        }
        finally
        {
            resumeSave.TrySetResult();
        }

        await send;
        Assert.Equal("전송 중 작성한 다음 메시지", viewModel.QuickMessageText);
        Assert.True(row.IsComposingMessage);
        Assert.Same(row, viewModel.QuickMessageTarget);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _hostTransport.DisposeAsync();
        await _guestTransport.DisposeAsync();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    private sealed class PausingStore(IAppDataStore inner) : IAppDataStore
    {
        public Func<Task>? BeforeSaveFriendsAsync { get; set; }
        public string DataDirectory => inner.DataDirectory;

        public Task<PetProfile> LoadProfileAsync(CancellationToken cancellationToken = default)
            => inner.LoadProfileAsync(cancellationToken);

        public Task SaveProfileAsync(PetProfile profile, CancellationToken cancellationToken = default)
            => inner.SaveProfileAsync(profile, cancellationToken);

        public Task<FriendsStore> LoadFriendsAsync(CancellationToken cancellationToken = default)
            => inner.LoadFriendsAsync(cancellationToken);

        public async Task SaveFriendsAsync(FriendsStore friendsStore, CancellationToken cancellationToken = default)
        {
            if (BeforeSaveFriendsAsync is { } beforeSave)
            {
                await beforeSave();
            }

            await inner.SaveFriendsAsync(friendsStore, cancellationToken);
        }
    }
}
