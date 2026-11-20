using System.IO;
using System.Net;
using LabPet.Client.Network;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
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
        var guestProfile = new PetProfile { PetName = "게스트" };
        var sharedSecret = PacketSigner.GeneratePairSecretBase64();
        await _guestTransport.StartAsync(guestProfile, []);
        await _store.SaveFriendsAsync(new FriendsStore
        {
            Friends =
            [
                new FriendEntry
                {
                    PetId = guestProfile.PetId,
                    PetName = guestProfile.PetName,
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
        Assert.True(await _host.SendFriendRequestAsync(new PeerSnapshot
        {
            PetId = newPeerProfile.PetId,
            IpAddress = IPAddress.Loopback.ToString(),
            TcpPort = newPeer.BoundTcpPort
        }));

        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var friendAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedCount = 0;
        _host.SocialNotification += (_, message) =>
        {
            if (message.Contains("수신 메시지", StringComparison.Ordinal)
                && Interlocked.Increment(ref receivedCount) == messagesPerDirection + 1)
            {
                allReceived.TrySetResult();
            }

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
            var newSecret = PacketSigner.GeneratePairSecretBase64();
            Assert.True(await newPeer.RespondFriendRequestAsync(
                _hostProfile.PetId, IPAddress.Loopback.ToString(), _hostTransport.BoundTcpPort,
                accept: true, newSecret));

            // 앞선 저장이 대기 중이어도 새 친구의 첫 메시지는 바로 인증된다.
            var hostAsNewFriend = _hostAsFriend.Clone();
            hostAsNewFriend.PairSecretBase64 = newSecret;
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
