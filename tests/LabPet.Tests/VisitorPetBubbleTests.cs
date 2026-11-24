using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using BubblePath = System.Windows.Shapes.Path;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LabPet.Client;
using LabPet.Client.Audio;
using LabPet.Client.Diagnostics;
using LabPet.Client.Interop;
using LabPet.Client.Network;
using LabPet.Client.Rendering;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Client.ViewModels;
using LabPet.Client.Views;
using LabPet.Server;
using LabPet.Server.Options;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace LabPet.Tests;

[Collection("WpfUi")]
public sealed class VisitorPetBubbleTests
{
    private sealed class StubMessageTarget(bool initiallyAvailable = true, bool acceptMessages = true) : IMessageBubbleTarget
    {
        public bool IsAvailableForMessages { get; set; } = initiallyAvailable;
        public bool AcceptMessages { get; set; } = acceptMessages;
        public List<ReceivedFriendMessage> ReceivedMessages { get; } = [];
        public List<string> ReceivedStatuses { get; } = [];

        public bool TryShowFriendMessage(ReceivedFriendMessage message)
        {
            if (!IsAvailableForMessages || !AcceptMessages)
            {
                return false;
            }

            ReceivedMessages.Add(message);
            return true;
        }

        public bool TryShowFriendStatus(string text, int durationMs, bool animate)
        {
            ReceivedStatuses.Add(text);
            return true;
        }
    }

    private sealed class NullAudioAnalyzer : IAudioAnalyzer
    {
        public void Start() { }
        public void Stop() { }
        public AudioFrameSnapshot GetCurrentSnapshot() => default;
        public void Dispose() { }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return _utcNow;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (_lock)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan delta)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);

            List<(ManualTimer Timer, TimerCallback Callback, object? State)> dueTimers = [];
            lock (_lock)
            {
                _utcNow = _utcNow.Add(delta);
                foreach (var timer in _timers)
                {
                    if (!timer.IsDisposed && timer.IsDue(_utcNow))
                    {
                        dueTimers.Add((timer, timer.Callback, timer.State));
                        timer.RescheduleOrDisable(_utcNow);
                    }
                }
            }

            foreach (var (_, callback, state) in dueTimers)
            {
                callback(state);
            }
        }

        private void RemoveTimer(ManualTimer timer)
        {
            lock (_lock)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private DateTimeOffset? _nextDueUtc;
            private TimeSpan _period;

            public TimerCallback Callback { get; }
            public object? State { get; }
            public bool IsDisposed { get; private set; }

            public ManualTimer(
                ManualTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _owner = owner;
                Callback = callback;
                State = state;
                ApplySchedule(_owner.GetUtcNow(), dueTime, period);
            }

            public bool IsDue(DateTimeOffset nowUtc)
                => _nextDueUtc is { } due && nowUtc >= due;

            public void RescheduleOrDisable(DateTimeOffset nowUtc)
            {
                _nextDueUtc = _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan
                    ? nowUtc.Add(_period)
                    : null;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_owner._lock)
                {
                    if (IsDisposed)
                    {
                        return false;
                    }

                    ApplySchedule(_owner.GetUtcNow(), dueTime, period);
                    return true;
                }
            }

            private void ApplySchedule(DateTimeOffset nowUtc, TimeSpan dueTime, TimeSpan period)
            {
                _period = period;
                _nextDueUtc = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : nowUtc.Add(dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
            }

            public void Dispose()
            {
                lock (_owner._lock)
                {
                    if (IsDisposed)
                    {
                        return;
                    }

                    IsDisposed = true;
                    _nextDueUtc = null;
                }

                _owner.RemoveTimer(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static ReceivedFriendMessage CreateMessage(string senderPetId, string text, string? messageId = null)
        => new(senderPetId, $"친구-{senderPetId}", text, new PetAppearance(), messageId ?? Guid.NewGuid().ToString("N"));

    [Fact]
    public void Route_WithoutVisitorWindow_DisplaysOnHostPet()
    {
        var host = new StubMessageTarget();
        var router = new FriendMessageRouter(_ => null, () => host);

        var outcome = router.Route(CreateMessage("friend-a", "안녕 내 펫!"));

        Assert.Equal(FriendMessageRouteOutcome.Host, outcome);
        Assert.Single(host.ReceivedMessages);
        Assert.Equal("안녕 내 펫!", host.ReceivedMessages[0].Text);
        Assert.Equal("friend-a", host.ReceivedMessages[0].SenderPetId);
    }

    [Fact]
    public void Route_WithMatchingVisitorWindow_DisplaysOnlyOnVisitorPet()
    {
        var host = new StubMessageTarget();
        var visitorA = new StubMessageTarget();
        var router = new FriendMessageRouter(
            petId => string.Equals(petId, "friend-a", StringComparison.OrdinalIgnoreCase) ? visitorA : null,
            () => host);

        var outcome = router.Route(CreateMessage("friend-a", "방문 펫에만 떠야 해"));

        Assert.Equal(FriendMessageRouteOutcome.Visitor, outcome);
        Assert.Single(visitorA.ReceivedMessages);
        Assert.Equal("방문 펫에만 떠야 해", visitorA.ReceivedMessages[0].Text);
        Assert.Empty(host.ReceivedMessages);
    }

    [Fact]
    public void Route_WithMultipleVisitors_SeparatesMessagesBySenderPetId()
    {
        var host = new StubMessageTarget();
        var visitorA = new StubMessageTarget();
        var visitorB = new StubMessageTarget();
        var visitors = new Dictionary<string, IMessageBubbleTarget>(StringComparer.OrdinalIgnoreCase)
        {
            ["friend-a"] = visitorA,
            ["friend-b"] = visitorB
        };

        var router = new FriendMessageRouter(
            petId => visitors.TryGetValue(petId, out var target) ? target : null,
            () => host);

        Assert.Equal(FriendMessageRouteOutcome.Visitor, router.Route(CreateMessage("friend-a", "A가 보냄")));
        Assert.Equal(FriendMessageRouteOutcome.Visitor, router.Route(CreateMessage("friend-b", "B가 보냄")));
        Assert.Equal(FriendMessageRouteOutcome.Host, router.Route(CreateMessage("friend-c", "방문 안 온 C가 보냄")));

        Assert.Single(visitorA.ReceivedMessages);
        Assert.Equal("A가 보냄", visitorA.ReceivedMessages[0].Text);
        Assert.Single(visitorB.ReceivedMessages);
        Assert.Equal("B가 보냄", visitorB.ReceivedMessages[0].Text);
        Assert.Single(host.ReceivedMessages);
        Assert.Equal("방문 안 온 C가 보냄", host.ReceivedMessages[0].Text);
    }

    [Fact]
    public void Route_SameMessageId_DoesNotDuplicateAcrossHostOrVisitor()
    {
        var host = new StubMessageTarget();
        var visitorA = new StubMessageTarget();
        var timeProvider = new ManualTimeProvider();
        var router = new FriendMessageRouter(
            petId => string.Equals(petId, "friend-a", StringComparison.OrdinalIgnoreCase) ? visitorA : null,
            () => host,
            timeProvider: timeProvider);

        var first = CreateMessage("friend-a", "중복 방지 테스트", "msg-same-01");
        var second = CreateMessage("friend-a", "중복 방지 테스트", "msg-same-01");

        Assert.Equal(FriendMessageRouteOutcome.Visitor, router.Route(first));
        Assert.Equal(FriendMessageRouteOutcome.DuplicateIgnored, router.Route(second));
        Assert.Single(visitorA.ReceivedMessages);
        Assert.Empty(host.ReceivedMessages);
    }

    [Fact]
    public void BubbleMessageStack_EnforcesMaxFiveAndEvictsOldestFirst()
    {
        var time = new ManualTimeProvider();
        using var stack = new BubbleMessageStack(5, time);

        for (var i = 1; i <= 7; i++)
        {
            Assert.True(stack.Add(new BubbleLine($"메시지 {i}"), 5000));
        }

        Assert.Equal(["메시지 3", "메시지 4", "메시지 5", "메시지 6", "메시지 7"], stack.Items.Select(x => x.Text));
        Assert.Equal(5, stack.ActiveTimerCount);

        stack.Dispose();
        Assert.Empty(stack.Items);
        Assert.Equal(0, stack.ActiveTimerCount);
        Assert.False(stack.Add(new BubbleLine("해제 후 무시"), 5000));
    }

    [Fact]
    public async Task BubbleMessageStack_ExpiresItemsIndependentlyWithoutResettingOlderTimers()
    {
        var time = new ManualTimeProvider();
        using var stack = new BubbleMessageStack(5, time);

        stack.Add(new BubbleLine("첫 번째(5초)"), 5000);
        time.Advance(TimeSpan.FromSeconds(3));
        stack.Add(new BubbleLine("두 번째(5초)"), 5000);

        Assert.Equal(["첫 번째(5초)", "두 번째(5초)"], stack.Items.Select(x => x.Text));

        // 2초 추가 경과(총 5초): 첫 번째만 만료되고 두 번째(남은 3초)는 유지되어야 한다.
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => stack.Items.Count == 1);

        Assert.Equal(["두 번째(5초)"], stack.Items.Select(x => x.Text));
        Assert.Equal(1, stack.ActiveTimerCount);

        // 3초 추가 경과(두 번째 기준 5초): 두 번째도 만료된다.
        time.Advance(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => stack.Items.Count == 0);

        Assert.Empty(stack.Items);
        Assert.Equal(0, stack.ActiveTimerCount);
    }

    [Theory]
    [InlineData(1.0, 0, 0)]
    [InlineData(1.5, -1920, 80)]
    [InlineData(2.0, 240, -1600)]
    public void VisitorBubblePlacement_KeepsCardsInsideEachCornerAndPointsAtTheHead(
        double scale, int monitorLeft, int monitorTop)
    {
        var dpi = new DpiScale(scale, scale);
        var area = new Rect(monitorLeft, monitorTop, 1440, 900);
        var bodySize = new Size(220 * scale, 128 * scale);
        foreach (var x in new[] { area.Left + 4 * scale, area.Right - 4 * scale })
        {
            foreach (var y in new[] { area.Top + 4 * scale, area.Bottom - 4 * scale })
            {
                var head = new Point(x, y);
                var placement = VisitorBubblePlacementCalculator.CalculateBubblePlacement(head, bodySize, area, dpi);
                Assert.True(area.Contains(placement.Body));
                Assert.Equal(head, placement.Tip);
                Assert.Equal(y < area.Top + area.Height / 2, placement.IsBelowHead);
                Assert.Equal(1, placement.Scale);

                var nearestCard = new Rect(placement.Body.Left + 40 * scale,
                    placement.IsBelowHead ? placement.Body.Top : placement.Body.Bottom - 28 * scale,
                    140 * scale, 28 * scale);
                var tail = VisitorBubblePlacementCalculator.CalculateTailPlacement(placement, nearestCard, dpi);
                Assert.Equal(head, tail.Tip);
                Assert.True(nearestCard.Contains(tail.BaseLeft));
                Assert.True(nearestCard.Contains(tail.BaseRight));
                Assert.True(placement.IsBelowHead ? tail.Tip.Y < tail.BaseLeft.Y : tail.Tip.Y > tail.BaseLeft.Y);
                Assert.NotEqual((tail.BaseLeft.X + tail.BaseRight.X) / 2, tail.Tip.X);
            }
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void VisitorComposerPlacement_StaysCenteredFiveDipBelowAtMonitorEdges(double scale)
    {
        var dpi = new DpiScale(scale, scale);
        var area = new Rect(-1920, -400, 1920, 1080);
        var size = new Size(234 * scale, 46 * scale);
        var nameplate = new Rect(area.Left + 1, area.Bottom - 20 * scale - 1, 90 * scale, 20 * scale);

        foreach (var x in new[] { area.Left + 1, area.Right - nameplate.Width - 1 })
        {
            nameplate.X = x;
            var below = VisitorBubblePlacementCalculator.CalculateComposerPlacement(nameplate, size, dpi);
            Assert.Equal(5 * scale, below.Top - nameplate.Bottom);
            Assert.Equal(nameplate.Left + nameplate.Width / 2, below.Left + below.Width / 2);
            Assert.True(below.Bottom > area.Bottom);
            Assert.True(below.Left < area.Left || below.Right > area.Right);
        }
    }

    [Fact]
    public void VisitorBubblePlacement_FitsTheWholeStackWhenNeitherSideHasEnoughRoom()
    {
        var dpi = new DpiScale(2, 2);
        var area = new Rect(-480, 0, 480, 420);
        var head = new Point(-10, 240);
        var placement = VisitorBubblePlacementCalculator.CalculateBubblePlacement(head, new Size(440, 450), area, dpi);
        Assert.True(area.Contains(placement.Body));
        Assert.False(placement.IsBelowHead);
        Assert.True(placement.Body.Bottom < head.Y);
        Assert.InRange(placement.Scale, 0.49, 0.50);
    }

    [Fact]
    public async Task VisitorPetWindow_SwitchesSingleComposerAndRoutesStatusToTheMatchingPet()
    {
        var transport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
        var store = new JsonAppDataStore(new StateMigrator(), Path.Combine(Path.GetTempPath(), "LabPet_VisitorSwitch_" + Guid.NewGuid().ToString("N")));
        await using var social = new SocialCoordinatorService(transport, store, new SoloProgressionService());
        using var audio = new NullAudioAnalyzer();
        var meshFactory = new ProceduralPetMeshFactory();

        await WpfTestHost.RunAsync(async () =>
        {
            var windows = new Dictionary<string, VisitorPetWindow>();
            void CloseOtherComposers(VisitorPetWindow target) => App.CloseOtherVisitorChatComposers(windows.Values, target);
            VisitorPetWindow ShowVisitor(string petId, double left)
            {
                var friend = new FriendEntry { PetId = petId };
                var visitor = new VisitorPetWindow(friend, friend.CachedAppearance, meshFactory, audio, social,
                    beforeChatComposerOpen: CloseOtherComposers)
                {
                    ShowActivated = false,
                    Left = left,
                    Top = 400
                };
                windows.Add(petId, visitor);
                visitor.Show();
                return visitor;
            }

            try
            {
                var first = ShowVisitor("first", 310);
                var second = ShowVisitor("second", 485);
                var host = new StubMessageTarget();
                var router = new FriendMessageRouter(petId => windows.GetValueOrDefault(petId), () => host);
                await FlushUiAsync();

                first.HandleVisitorClick();
                await FlushUiAsync();
                Assert.True(first.IsChatComposerOpen);
                first.ChatComposerInputText = "보내기 전 문장";

                second.HandleVisitorClick();
                await FlushUiAsync();
                Assert.False(first.IsChatComposerOpen);
                Assert.True(first.CurrentComposerPhysicalBounds.IsEmpty);
                Assert.True(second.IsChatComposerOpen);
                var composer = Assert.IsType<Border>(second.FindName("VisitorChatComposerCard"));
                var viewport = Assert.IsType<Viewport3D>(second.FindName("VisitorViewport"));
                Assert.True(composer.PointToScreen(new Point(0, composer.ActualHeight)).Y > viewport.PointToScreen(new Point()).Y);

                Assert.Equal(FriendMessageRouteOutcome.Visitor, router.RouteStatus(new("second", "인사 전송!", 5000)));
                await FlushUiAsync();
                Assert.False(first.HasStatusBubble);
                Assert.Equal("인사 전송!", second.StatusBubbleText);
                Assert.Empty(host.ReceivedStatuses);
                Assert.True(second.CurrentBubblePhysicalBounds.Bottom < second.CurrentComposerPhysicalBounds.Top);
                var status = Assert.IsType<Border>(second.FindName("VisitorStatusBubble"));
                Assert.True(status.IsVisible);
                Assert.True(status.PointToScreen(new Point(0, status.ActualHeight)).Y < composer.PointToScreen(new Point()).Y);

                first.HandleVisitorClick();
                await FlushUiAsync();
                Assert.True(first.IsChatComposerOpen);
                Assert.False(second.IsChatComposerOpen);
                Assert.Equal("보내기 전 문장", first.ChatComposerInputText);
                Assert.Equal(FriendMessageRouteOutcome.Host, router.RouteStatus(new("absent", "인사했어요!", 2000)));
                Assert.Equal("인사했어요!", Assert.Single(host.ReceivedStatuses));
            }
            finally
            {
                foreach (var visitor in windows.Values)
                {
                    visitor.Close();
                }
            }
        }).WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task VisitorPetWindow_UsesSleepPose_AlignsBubbleTail_AndKeepsComposerAfterSend()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "LabPet_VisitorComposerTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var hostProfile = new PetProfile { OwnerName = "호스트", PetName = "내펫" };
            var peerProfile = new PetProfile { OwnerName = "보미", PetName = "구름이" };
            var sharedSecret = PacketSigner.GeneratePairSecretBase64();

            await using var peerTransport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
            await using var hostTransport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
            await peerTransport.StartAsync(peerProfile, []);

            var friend = new FriendEntry
            {
                PetId = peerProfile.PetId,
                OwnerName = peerProfile.OwnerName,
                PetName = peerProfile.PetName,
                CachedAppearance = new PetAppearance(),
                LastKnownIp = IPAddress.Loopback.ToString(),
                LastKnownTcpPort = peerTransport.BoundTcpPort,
                PairSecretBase64 = sharedSecret
            };

            var store = new JsonAppDataStore(new StateMigrator(), tempDir);
            await store.SaveFriendsAsync(new FriendsStore { Friends = [friend] });
            await using var social = new SocialCoordinatorService(hostTransport, store, new SoloProgressionService());
            await social.StartAsync(hostProfile);

            peerTransport.UpdateKnownFriends(
            [
                new FriendEntry
                {
                    PetId = hostProfile.PetId,
                    OwnerName = hostProfile.OwnerName,
                    PetName = hostProfile.PetName,
                    LastKnownIp = IPAddress.Loopback.ToString(),
                    LastKnownTcpPort = hostTransport.BoundTcpPort,
                    PairSecretBase64 = sharedSecret
                }
            ]);

            var receivedBubbleTcs = new TaskCompletionSource<BubbleMessageReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondBubbleTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var petInteractionCount = 0;
            peerTransport.BubbleMessageReceived += (_, e) =>
            {
                if (!receivedBubbleTcs.TrySetResult(e))
                {
                    secondBubbleTcs.TrySetResult(e.Text);
                }
            };
            peerTransport.PetInteractionReceived += (_, _) => Interlocked.Increment(ref petInteractionCount);

            var meshFactory = new ProceduralPetMeshFactory();
            using var audio = new NullAudioAnalyzer();

            await WpfTestHost.RunAsync(async () =>
            {
                VisitorPetWindow? window = null;
                try
                {
                    window = new VisitorPetWindow(
                        friend,
                        friend.CachedAppearance,
                        meshFactory,
                        audio,
                        social)
                    {
                        ShowActivated = false,
                        Left = 420,
                        Top = 380
                    };

                    window.Show();
                    await FlushUiAsync();
                    Assert.True(window.ShowsSleepingPose);

                    var viewport = Assert.IsType<Viewport3D>(window.FindName("VisitorViewport"));
                    var sleepBadge = Assert.IsType<Viewbox>(window.FindName("VisitorSleepBadge"));
                    var sleepOverlay = Assert.IsType<SleepZzzOverlay>(Assert.IsType<Grid>(sleepBadge.Child).Children[0]);
                    Assert.Equal(HorizontalAlignment.Right, sleepOverlay.HorizontalAlignment);
                    Assert.True(sleepOverlay.IsAnimating);

                    hostTransport.HandleDiscoveryHello(new DiscoveryHelloPacket
                    {
                        PetId = peerProfile.PetId, TcpPort = peerTransport.BoundTcpPort
                    }, IPAddress.Loopback.ToString(), CancellationToken.None);
                    await WaitUntilAsync(() => !window.ShowsSleepingPose);
                    Assert.False(sleepOverlay.IsAnimating);

                    Assert.True(window.TryShowFriendMessage(CreateMessage(friend.PetId, "정렬 확인")));
                    await FlushUiAsync();
                    AssertBubbleHeadAlignment(window);

                    // 클릭 시 쓰다듬기 대신 채팅 입력창이 열려야 한다.
                    window.HandleVisitorClick();
                    await FlushUiAsync();
                    Assert.True(window.IsChatComposerOpen);
                    Assert.False(window.CurrentComposerPhysicalBounds.IsEmpty);
                    Assert.Equal(0, Volatile.Read(ref petInteractionCount));
                    var composerBounds = GetScreenBounds((Border)window.FindName("VisitorChatComposerCard"));
                    var nameplateBounds = GetScreenBounds((Border)window.FindName("VisitorNameplate"));
                    var dpi = VisualTreeHelper.GetDpi(viewport);
                    Assert.InRange((composerBounds.Top - nameplateBounds.Bottom) / dpi.DpiScaleY, 4, 6);

                    // 채팅 입력창이 열린 상태에서 수신 메시지가 와도 입력창이 유지되고 영역이 겹치지 않는다.
                    Assert.True(window.TryShowFriendMessage(CreateMessage(friend.PetId, "지금 답장하는 중이야?")));
                    await FlushUiAsync();

                    Assert.True(window.IsChatComposerOpen);
                    Assert.False(window.CurrentBubblePhysicalBounds.IsEmpty);
                    Assert.Equal(composerBounds, GetScreenBounds((Border)window.FindName("VisitorChatComposerCard")));
                    Assert.Equal(nameplateBounds, GetScreenBounds((Border)window.FindName("VisitorNameplate")));
                    Assert.False(
                        window.CurrentBubblePhysicalBounds.IntersectsWith(window.CurrentComposerPhysicalBounds),
                        $"방문 펫 말풍선과 채팅 입력창이 겹침: {window.CurrentBubblePhysicalBounds} vs {window.CurrentComposerPhysicalBounds}");

                    // 성공하면 발송한 문장만 비우고 같은 입력창에서 다시 보낼 수 있다.
                    window.ChatComposerInputText = "방문 펫에서 바로 보낸 메시지!";
                    var sendButton = Assert.IsType<Button>(window.FindName("SendVisitorChatButton"));
                    sendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                    var delivered = await receivedBubbleTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await WaitUntilAsync(() => window.ChatComposerInputText.Length == 0);
                    Assert.Equal("방문 펫에서 바로 보낸 메시지!", delivered.Text);
                    Assert.Equal(0, Volatile.Read(ref petInteractionCount));
                    Assert.True(window.IsChatComposerOpen);
                    Assert.Empty(window.ChatComposerInputText);

                    window.ChatComposerInputText = "이어 보내는 메시지";
                    var input = (TextBox)window.FindName("VisitorChatInput");
                    var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input),
                        Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
                    input.RaiseEvent(key);
                    Assert.True(key.Handled);
                    Assert.Equal("이어 보내는 메시지", await secondBubbleTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                    await WaitUntilAsync(() => window.ChatComposerInputText.Length == 0);
                    Assert.True(window.IsChatComposerOpen);

                    window.HandleVisitorClick();
                    Assert.False(window.IsChatComposerOpen);
                }
                finally
                {
                    if (window is { IsLoaded: true })
                    {
                        window.Close();
                    }
                }
            }).WaitAsync(TimeSpan.FromSeconds(15));

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
    public void Route_WhenVisitorClosingOrClosed_FallsBackToHostWithoutException()
    {
        var host = new StubMessageTarget();
        var closingVisitor = new StubMessageTarget(initiallyAvailable: false, acceptMessages: false);
        var router = new FriendMessageRouter(
            petId => string.Equals(petId, "friend-a", StringComparison.OrdinalIgnoreCase) ? closingVisitor : null,
            () => host);

        var outcome = router.Route(CreateMessage("friend-a", "닫히는 순간 도착"));

        Assert.Equal(FriendMessageRouteOutcome.Host, outcome);
        Assert.Empty(closingVisitor.ReceivedMessages);
        Assert.Single(host.ReceivedMessages);

        // 이미 IsAvailableForMessages가 true였더라도 TryShowFriendMessage에서 false를 반환하는 레이스 상황
        var raceVisitor = new StubMessageTarget(initiallyAvailable: true, acceptMessages: false);
        var raceRouter = new FriendMessageRouter(_ => raceVisitor, () => host);
        var raceOutcome = raceRouter.Route(CreateMessage("friend-a", "TryShow false 레이스"));

        Assert.Equal(FriendMessageRouteOutcome.Host, raceOutcome);
        Assert.Equal(2, host.ReceivedMessages.Count);
    }

    [Fact]
    public async Task VisitorPetWindow_LongAndBurstMessages_KeepMaxFiveWrapAndDoNotMoveWindowOrOverwriteStatus()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "LabPet_VisitorWinTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var store = new JsonAppDataStore(new StateMigrator(), tempDir);
            var transport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
            var social = new SocialCoordinatorService(transport, store, new SoloProgressionService());
            var meshFactory = new ProceduralPetMeshFactory();
            using var audio = new NullAudioAnalyzer();

            var friend = new FriendEntry
            {
                PetId = "friend-ui-01",
                OwnerName = "보미",
                PetName = "구름이",
                CachedAppearance = new PetAppearance()
            };

            await WpfTestHost.RunAsync(async () =>
            {
                VisitorPetWindow? window = null;
                try
                {
                    window = new VisitorPetWindow(
                        friend,
                        friend.CachedAppearance,
                        meshFactory,
                        audio,
                        social)
                    {
                        ShowActivated = false,
                        Left = 320,
                        Top = 360
                    };

                    var hostTarget = new StubMessageTarget();
                    VisitorPetWindow? trackedVisitor = window;
                    var router = new FriendMessageRouter(
                        petId => trackedVisitor is not null && string.Equals(petId, friend.PetId, StringComparison.OrdinalIgnoreCase)
                            ? trackedVisitor
                            : null,
                        () => hostTarget);

                    window.Show();
                    await FlushUiAsync();

                    var initialTop = window.Top;
                    var initialHeight = window.Height;

                    // 시스템 알림 먼저 표시
                    window.ShowVisitorBubble("인사 전송!", 5000);
                    Assert.True(window.HasStatusBubble);
                    Assert.Equal("인사 전송!", window.StatusBubbleText);

                    // 긴 메시지 포함 7연속 채팅 전송 → 최대 5개 유지 + 시스템 알림은 덮어쓰지 않음 + 창 높이/위치 불변
                    var longText = "이것은 아주 긴 친구 메시지입니다. 방문 펫 말풍선에서 자동 줄바꿈되고 창이 흔들리지 않아야 합니다.";
                    for (var i = 1; i <= 6; i++)
                    {
                        Assert.Equal(FriendMessageRouteOutcome.Visitor, router.Route(CreateMessage(friend.PetId, $"채팅 {i}")));
                    }

                    Assert.Equal(FriendMessageRouteOutcome.Visitor, router.Route(CreateMessage(friend.PetId, longText)));
                    await FlushUiAsync();

                    Assert.Equal(5, window.ChatBubbleStack.Items.Count);
                    Assert.Equal(5, ((StackPanel)window.FindName("VisitorChatCards")).Children.Count);
                    Assert.Equal(["채팅 3", "채팅 4", "채팅 5", "채팅 6", longText],
                        window.ChatBubbleStack.Items.Select(x => x.Text));
                    Assert.True(window.HasStatusBubble);
                    Assert.Equal("인사 전송!", window.StatusBubbleText);
                    Assert.Equal(initialTop, window.Top);
                    Assert.Equal(initialHeight, window.Height);
                    Assert.False(window.CurrentBubblePhysicalBounds.IsEmpty);

                    // 50% -> 150% 크기 변경 후에도 말풍선 스택 및 창 가용성 유지
                    window.ApplyDisplaySettings(0.5, 0.8);
                    window.ApplyDisplaySettings(1.5, 1.0);
                    await FlushUiAsync();
                    Assert.Equal(5, window.ChatBubbleStack.Items.Count);

                    // 창 닫기 직후 타이머/스택 해제 + 직후 도착 메시지는 호스트로 폴백
                    window.Close();
                    await FlushUiAsync();

                    Assert.False(window.IsAvailableForMessages);
                    Assert.Equal(0, window.ChatBubbleStack.ActiveTimerCount);
                    Assert.Empty(window.ChatBubbleStack.Items);

                    var postCloseOutcome = router.Route(CreateMessage(friend.PetId, "종료 직후 도착"));
                    Assert.Equal(FriendMessageRouteOutcome.Host, postCloseOutcome);
                    Assert.Single(hostTarget.ReceivedMessages);
                    Assert.Equal("종료 직후 도착", hostTarget.ReceivedMessages[0].Text);

                    trackedVisitor = null;
                    window = null;
                }
                finally
                {
                    if (window is { IsLoaded: true })
                    {
                        window.Close();
                    }
                }
            }).WaitAsync(TimeSpan.FromSeconds(15));

            await social.DisposeAsync();
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
    public async Task EndToEnd_LanAndGcpMessages_RouteToMatchingVisitorPetBySenderPetId()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "LabPet_MsgRouteE2E_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        WebApplication? server = null;

        try
        {
            // 1) LAN P2P 경로에서 SenderPetId 포함 ReceivedFriendMessage → 방문 펫 라우팅 검증
            var profileHostLan = new PetProfile { OwnerName = "호스트", PetName = "내펫" };
            var profileLanFriend = new PetProfile { OwnerName = "랜친구", PetName = "랜멍" };
            var sharedLanSecret = PacketSigner.GeneratePairSecretBase64();

            var storeHostLan = new JsonAppDataStore(new StateMigrator(), Path.Combine(tempRoot, "lan-host"));
            var lanHostTransport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
            var lanFriendTransport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);

            await lanFriendTransport.StartAsync(profileLanFriend, []);
            await storeHostLan.SaveFriendsAsync(new FriendsStore
            {
                Friends =
                [
                    new FriendEntry
                    {
                        PetId = profileLanFriend.PetId,
                        OwnerName = profileLanFriend.OwnerName,
                        PetName = profileLanFriend.PetName,
                        LastKnownIp = IPAddress.Loopback.ToString(),
                        LastKnownTcpPort = lanFriendTransport.BoundTcpPort,
                        PairSecretBase64 = sharedLanSecret
                    }
                ]
            });

            await using var lanHostSocial = new SocialCoordinatorService(lanHostTransport, storeHostLan, new SoloProgressionService());
            await lanHostSocial.StartAsync(profileHostLan);
            lanFriendTransport.UpdateKnownFriends(
            [
                new FriendEntry
                {
                    PetId = profileHostLan.PetId,
                    LastKnownIp = IPAddress.Loopback.ToString(),
                    LastKnownTcpPort = lanHostTransport.BoundTcpPort,
                    PairSecretBase64 = sharedLanSecret
                }
            ]);

            var hostBubbleTarget = new StubMessageTarget();
            var lanVisitorTarget = new StubMessageTarget();
            var gcpVisitorTarget = new StubMessageTarget();
            var visitors = new Dictionary<string, IMessageBubbleTarget>(StringComparer.OrdinalIgnoreCase)
            {
                [profileLanFriend.PetId] = lanVisitorTarget
            };
            var router = new FriendMessageRouter(
                petId => visitors.TryGetValue(petId, out var t) ? t : null,
                () => hostBubbleTarget);

            var lanRoutedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lanHostSocial.FriendMessageReceived += (_, msg) =>
            {
                router.Route(msg);
                lanRoutedTcs.TrySetResult();
            };

            Assert.True(await lanFriendTransport.SendBubbleMessageAsync(
                new FriendEntry
                {
                    PetId = profileHostLan.PetId,
                    LastKnownIp = IPAddress.Loopback.ToString(),
                    LastKnownTcpPort = lanHostTransport.BoundTcpPort,
                    PairSecretBase64 = sharedLanSecret
                },
                "LAN 방문 펫 메시지"));

            await lanRoutedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(lanVisitorTarget.ReceivedMessages);
            Assert.Equal("LAN 방문 펫 메시지", lanVisitorTarget.ReceivedMessages[0].Text);
            Assert.Equal(profileLanFriend.PetId, lanVisitorTarget.ReceivedMessages[0].SenderPetId);
            Assert.Empty(hostBubbleTarget.ReceivedMessages);

            await lanFriendTransport.DisposeAsync();

            // 2) GCP SignalR 중계 경로에서 SenderPetId 포함 ReceivedFriendMessage → 방문 펫 라우팅 검증
            var serverPort = GetFreeTcpPort();
            var serverUrl = $"http://127.0.0.1:{serverPort}";
            server = LabPetServerHost.BuildApplication(new ServerOptions
            {
                ListenUrl = serverUrl,
                ServerSecret = "visitor-bubble-e2e-server-secret-value-2026!",
                RequireConfiguredSecret = true,
                DataDirectory = Path.Combine(tempRoot, "server")
            });
            await server.StartAsync();

            var connOpts = new ServerConnectionOptions { Enabled = true, ServerUrl = serverUrl };
            var gcpHostTransport = new GcpSignalRTransport(connOpts, new DeviceIdentityStore(Path.Combine(tempRoot, "gcp-host-id")));
            var gcpFriendTransport = new GcpSignalRTransport(connOpts, new DeviceIdentityStore(Path.Combine(tempRoot, "gcp-friend-id")));

            var profileHostGcp = new PetProfile { OwnerName = "클라우드호스트", PetName = "내펫2" };
            var profileFriendGcp = new PetProfile { OwnerName = "클라우드친구", PetName = "구름냥" };
            var gcpStoreHost = new JsonAppDataStore(new StateMigrator(), Path.Combine(tempRoot, "gcp-host-data"));
            await using var gcpHostSocial = new SocialCoordinatorService(gcpHostTransport, gcpStoreHost, new SoloProgressionService());

            await gcpHostSocial.StartAsync(profileHostGcp);
            await gcpFriendTransport.StartAsync(profileFriendGcp, []);
            await WaitUntilAsync(() => gcpFriendTransport.GetDiscoveredPeers().Any(p => p.PetId == profileHostGcp.PetId));

            var incomingReqTcs = new TaskCompletionSource<PendingFriendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            gcpHostSocial.FriendRequestArrived += (_, req) => incomingReqTcs.TrySetResult(req);
            var friendAcceptedOnPeerTcs = new TaskCompletionSource<FriendAcceptedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            gcpFriendTransport.FriendAccepted += (_, e) => friendAcceptedOnPeerTcs.TrySetResult(e);

            var hostPeer = Assert.Single(gcpFriendTransport.GetDiscoveredPeers(), p => p.PetId == profileHostGcp.PetId);
            Assert.True(await gcpFriendTransport.SendFriendRequestAsync(hostPeer));
            var incomingReq = await incomingReqTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var acceptResult = await gcpHostSocial.RespondFriendRequestDetailedAsync(incomingReq, accept: true);
            Assert.True(acceptResult.Succeeded);

            await WaitUntilAsync(() => gcpHostSocial.Friends.Any(f => f.PetId == profileFriendGcp.PetId));
            var friendAccepted = await friendAcceptedOnPeerTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var hostSideFriend = Assert.Single(gcpHostSocial.Friends, f => f.PetId == profileFriendGcp.PetId);
            var friendSideHost = new FriendEntry
            {
                PetId = profileHostGcp.PetId,
                OwnerName = profileHostGcp.OwnerName,
                PetName = profileHostGcp.PetName,
                PairSecretBase64 = friendAccepted.PairSecretBase64
            };
            Assert.Equal(hostSideFriend.PairSecretBase64, friendSideHost.PairSecretBase64);
            gcpFriendTransport.UpdateKnownFriends([friendSideHost]);

            visitors[profileFriendGcp.PetId] = gcpVisitorTarget;
            var gcpRoutedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gcpHostSocial.FriendMessageReceived += (_, msg) =>
            {
                router.Route(msg);
                gcpRoutedTcs.TrySetResult();
            };

            Assert.True(await gcpFriendTransport.SendBubbleMessageAsync(friendSideHost, "GCP 방문 펫 메시지"));
            await gcpRoutedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Single(gcpVisitorTarget.ReceivedMessages);
            Assert.Equal("GCP 방문 펫 메시지", gcpVisitorTarget.ReceivedMessages[0].Text);
            Assert.Equal(profileFriendGcp.PetId, gcpVisitorTarget.ReceivedMessages[0].SenderPetId);
            Assert.Empty(hostBubbleTarget.ReceivedMessages);

            await gcpFriendTransport.DisposeAsync();
        }
        finally
        {
            if (server is not null)
            {
                await server.StopAsync();
                await server.DisposeAsync();
            }

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task VisitorPetWindow_DisplaySettingsAndOverlayControls_StayAlignedAcrossScales()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "LabPet_VisitorLayoutTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var store = new JsonAppDataStore(new StateMigrator(), tempDir);
            var transport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
            var social = new SocialCoordinatorService(transport, store, new SoloProgressionService());
            var meshFactory = new ProceduralPetMeshFactory();
            using var audio = new NullAudioAnalyzer();

            var friend = new FriendEntry
            {
                PetId = "friend-layout-01",
                OwnerName = "지수",
                PetName = "모찌",
                CachedAppearance = new PetAppearance()
            };

            await WpfTestHost.RunAsync(async () =>
            {
                VisitorPetWindow? window = null;
                try
                {
                    window = new VisitorPetWindow(
                        friend,
                        friend.CachedAppearance,
                        meshFactory,
                        audio,
                        social)
                    {
                        ShowActivated = false,
                        Left = 400,
                        Top = 300
                    };

                    window.Show();
                    await FlushUiAsync();

                    var dismissButton = Assert.IsType<Button>(window.FindName("DismissButton"));
                    var cases = new (double Scale, double Opacity)[]
                    {
                        (0.5, 0.25),
                        (0.75, 0.55),
                        (1.0, 0.7),
                        (1.25, 0.85),
                        (1.5, 1.0)
                    };

                    foreach (var (scale, opacity) in cases)
                    {
                        window.ApplyDisplaySettings(scale, opacity);
                        window.UpdateLayout();
                        await FlushUiAsync();

                        var expectedViewport = PetWindow.ComputeViewportSize(scale);
                        var expectedWindowWidth = Math.Max(PetDisplayLayoutHelper.MinimumWindowWidth, expectedViewport.Width);
                        var expectedWindowHeight = expectedViewport.Height + PetDisplayLayoutHelper.NameplateRowHeight;

                        Assert.Equal(expectedViewport, window.AppliedViewportSize);
                        Assert.Equal(expectedWindowWidth, window.Width);
                        Assert.Equal(expectedWindowHeight, window.Height);

                        Assert.Equal(opacity, window.AppliedViewportOpacity, precision: 4);
                        Assert.Equal(opacity, window.AppliedNameplateOpacity, precision: 4);
                        Assert.Equal(opacity, window.AppliedBubbleOpacity, precision: 4);
                        Assert.InRange(Canvas.GetLeft(dismissButton) + dismissButton.Width / 2,
                            expectedViewport.Width * 0.60, expectedViewport.Width * 0.90);
                        Assert.InRange(Canvas.GetTop(dismissButton) + dismissButton.Height,
                            expectedViewport.Height * 0.14, expectedViewport.Height * 0.48);

                        // 3D 캐릭터 발바닥·키보드 앵커 기준으로 네임텍이 뷰포트 하단 위쪽으로 당겨져 내 펫과 동일한 상대 위치에 놓이는지 확인
                        Assert.True(window.NameplateOffsetY < 0.0,
                            $"배율 {scale}에서 방문 펫 네임텍 오프셋이 음수(캐릭터 하단 밀착)여야 합니다. 실제={window.NameplateOffsetY}");
                    }
                }
                finally
                {
                    if (window is { IsLoaded: true })
                    {
                        window.Close();
                    }
                }
            }).WaitAsync(TimeSpan.FromSeconds(15));

            await social.DisposeAsync();
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
    public async Task VisitorPopups_UseReceiveOrderWithoutMovingOrTakingFocusFromTheComposer()
    {
        var transport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
        var store = new JsonAppDataStore(new StateMigrator(), Path.Combine(Path.GetTempPath(), "LabPet_VisitorOrder_" + Guid.NewGuid().ToString("N")));
        await using var social = new SocialCoordinatorService(transport, store, new SoloProgressionService());
        using var audio = new NullAudioAnalyzer();
        var meshFactory = new ProceduralPetMeshFactory();

        await WpfTestHost.RunAsync(async () =>
        {
            var order = new VisitorPopupOrder();
            var firstFriend = new FriendEntry { PetId = "first", OwnerName = "유라", PetName = "구름" };
            var secondFriend = new FriendEntry { PetId = "second", OwnerName = "민수", PetName = "모찌" };
            var first = new VisitorPetWindow(firstFriend, firstFriend.CachedAppearance, meshFactory, audio, social, order)
            {
                ShowActivated = false,
                Left = 220,
                Top = 230
            };
            var second = new VisitorPetWindow(secondFriend, secondFriend.CachedAppearance, meshFactory, audio, social, order)
            {
                ShowActivated = false,
                Left = 315,
                Top = 230
            };

            try
            {
                first.Show();
                second.Show();
                await FlushUiAsync();

                first.OpenChatComposer();
                await FlushUiAsync();
                var input = (TextBox)first.FindName("VisitorChatInput");
                input.Text = "작성 중";
                var focus = Keyboard.FocusedElement;
                var composerCard = (Border)first.FindName("VisitorChatComposerCard");
                var composerBounds = GetScreenBounds(composerCard);
                var firstNameplate = GetScreenBounds((Border)first.FindName("VisitorNameplate"));
                var composerHandle = GetPopupHandle(first, "VisitorChatComposerPopup");

                Assert.True(first.ShowChatMessage("첫 번째 친구의 메시지"));
                await FlushUiAsync();
                var firstHandle = GetPopupHandle(first, "VisitorBubblePopup");
                var firstBounds = first.CurrentBubblePhysicalBounds;

                Assert.True(second.ShowChatMessage("두 번째 친구의 메시지"));
                await FlushUiAsync();
                var secondHandle = GetPopupHandle(second, "VisitorBubblePopup");
                Assert.True(first.CurrentBubblePhysicalBounds.IntersectsWith(second.CurrentBubblePhysicalBounds));
                Assert.Equal(firstBounds, first.CurrentBubblePhysicalBounds);
                Assert.True(Win32WindowHelper.IsWindowAbove(secondHandle, firstHandle));
                Assert.True(Win32WindowHelper.IsWindowAbove(composerHandle, secondHandle));

                var secondBounds = second.CurrentBubblePhysicalBounds;
                Assert.True(first.ShowChatMessage("첫 번째 친구의 최신 메시지", durationMs: 900));
                await FlushUiAsync();
                Assert.True(Win32WindowHelper.IsWindowAbove(firstHandle, secondHandle));
                Assert.True(Win32WindowHelper.IsWindowAbove(composerHandle, firstHandle));
                Assert.Equal(secondBounds, second.CurrentBubblePhysicalBounds);
                Assert.Same(focus, Keyboard.FocusedElement);

                await WaitUntilAsync(() => first.ChatBubbleStack.Items.Count == 1);
                await FlushUiAsync();
                var firstCards = (StackPanel)first.FindName("VisitorChatCards");
                Assert.Single(firstCards.Children);
                Assert.Equal(firstBounds, first.CurrentBubblePhysicalBounds);
                Assert.Equal(secondBounds, second.CurrentBubblePhysicalBounds);
                Assert.True(Win32WindowHelper.IsWindowAbove(firstHandle, secondHandle));

                // 입력창의 실제 화면 영역에 다른 펫의 말풍선을 겹쳐도 입력창 HWND가 위에 있어야 한다.
                Assert.True(Win32WindowHelper.TryGetWindowPhysicalBounds(second, out var petWindow));
                var secondHead = second.GetPopupHeadPhysical();
                var overlappingY = (int)Math.Round(petWindow.Top + composerBounds.Bottom + 6 - secondHead.Y);
                Assert.True(Win32WindowHelper.MoveWindowPhysical(second, (int)petWindow.Left, overlappingY));
                await FlushUiAsync();
                Assert.True(second.CurrentBubblePhysicalBounds.IntersectsWith(composerBounds));
                Assert.True(Win32WindowHelper.IsWindowAbove(firstHandle, secondHandle));
                Assert.True(Win32WindowHelper.IsWindowAbove(composerHandle, firstHandle));

                second.ApplyDisplaySettings(1.25, 0.9);
                await FlushUiAsync();
                Assert.Equal(firstHandle, GetPopupHandle(first, "VisitorBubblePopup"));
                Assert.Equal(secondHandle, GetPopupHandle(second, "VisitorBubblePopup"));
                Assert.Equal(composerHandle, GetPopupHandle(first, "VisitorChatComposerPopup"));
                Assert.True(Win32WindowHelper.IsWindowAbove(firstHandle, secondHandle));
                Assert.True(Win32WindowHelper.IsWindowAbove(composerHandle, firstHandle));
                Assert.Same(focus, Keyboard.FocusedElement);
                Assert.Equal("작성 중", input.Text);
                Assert.Equal(composerBounds, GetScreenBounds(composerCard));
                Assert.Equal(firstNameplate, GetScreenBounds((Border)first.FindName("VisitorNameplate")));
            }
            finally
            {
                second.Close();
                first.Close();
            }
        }).WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task VisitorPopups_KeepTheTailOnTheHeadAndNewestCardNearestAtAllDesktopCorners()
    {
        var transport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
        var store = new JsonAppDataStore(new StateMigrator(), Path.Combine(Path.GetTempPath(), "LabPet_VisitorEdges_" + Guid.NewGuid().ToString("N")));
        await using var social = new SocialCoordinatorService(transport, store, new SoloProgressionService());
        using var audio = new NullAudioAnalyzer();

        await WpfTestHost.RunAsync(async () =>
        {
            var friend = new FriendEntry { PetId = "corner", OwnerName = "유라", PetName = "구름" };
            var window = new VisitorPetWindow(friend, friend.CachedAppearance, new ProceduralPetMeshFactory(), audio, social)
            {
                ShowActivated = false,
                Left = 100,
                Top = 100
            };

            try
            {
                window.Show();
                await FlushUiAsync();
                Assert.True(Win32WindowHelper.TryGetNearestWorkArea(window, out var area));
                var viewport = (Viewport3D)window.FindName("VisitorViewport");
                var dpi = VisualTreeHelper.GetDpi(viewport);
                Assert.True(window.ShowChatMessage("먼저 온 메시지는 더 멀리 표시됩니다."));
                Assert.True(window.ShowChatMessage("최신 메시지"));
                await FlushUiAsync();

                foreach (var x in new[] { area.Left + 6 * dpi.DpiScaleX, area.Right - 6 * dpi.DpiScaleX })
                {
                    foreach (var y in new[] { area.Top + 6 * dpi.DpiScaleY, area.Bottom - 6 * dpi.DpiScaleY })
                    {
                        Assert.True(Win32WindowHelper.TryGetWindowPhysicalBounds(window, out var bounds));
                        var head = window.GetPopupHeadPhysical();
                        Assert.True(Win32WindowHelper.MoveWindowPhysical(window,
                            (int)Math.Round(bounds.Left + x - head.X), (int)Math.Round(bounds.Top + y - head.Y)));
                        await FlushUiAsync();

                        var cards = (StackPanel)window.FindName("VisitorChatCards");
                        var isBelowHead = y < area.Top + area.Height / 2;
                        var index = isBelowHead ? 0 : cards.Children.Count - 1;
                        var card = Assert.IsType<Border>(cards.Children[index]);
                        Assert.Equal("최신 메시지", Assert.IsType<BubbleLine>(card.DataContext).Text);
                        var cardBounds = GetScreenBounds(card);
                        Assert.True(area.Contains(cardBounds));
                        var tailBase = AssertBubbleHeadAlignment(window);
                        Assert.True(cardBounds.Contains(tailBase), $"카드 {cardBounds}, 꼬리 밑변 {tailBase}, 모서리 {x},{y}");
                        Assert.InRange(Math.Abs(tailBase.X - x), 0, 22 * dpi.DpiScaleX);
                        Assert.True(isBelowHead ? cardBounds.Top > y : cardBounds.Bottom < y);
                    }
                }

                // 화면을 벗어나더라도 전송창의 실제 화면 좌표는 이름표 아래에서 유지한다.
                var nameplate = (Border)window.FindName("VisitorNameplate");
                Assert.True(Win32WindowHelper.TryGetWindowPhysicalBounds(window, out var current));
                var initialNameplate = GetScreenBounds(nameplate);
                var cornerX = (int)Math.Round(current.Left + area.Right - initialNameplate.Right - 6);
                var cornerY = (int)Math.Round(current.Top + area.Bottom - initialNameplate.Bottom - 8 * dpi.DpiScaleY);
                Assert.True(Win32WindowHelper.MoveWindowPhysical(window, cornerX, cornerY));
                await FlushUiAsync();
                var nameplateBeforeOpening = GetScreenBounds(nameplate);
                window.OpenChatComposer();
                await FlushUiAsync();

                var composer = (Border)window.FindName("VisitorChatComposerCard");
                var composerHandle = GetPopupHandle(window, "VisitorChatComposerPopup");
                var composerBounds = GetScreenBounds(composer);
                Assert.Equal(nameplateBeforeOpening, GetScreenBounds(nameplate));
                AssertComposerBelowNameplate(composerBounds, nameplateBeforeOpening, dpi);
                Assert.True(composerBounds.Bottom > area.Bottom);
                Assert.True(composerBounds.Right > area.Right);

                // Windows 경계 보정 결과가 같아도 열린 HWND는 펫의 실제 이동량을 따라간다.
                Assert.True(Win32WindowHelper.MoveWindowPhysical(window, cornerX - 10, cornerY - 5));
                await FlushUiAsync();
                Assert.Equal(composerHandle, GetPopupHandle(window, "VisitorChatComposerPopup"));
                AssertComposerBelowNameplate(GetScreenBounds(composer), GetScreenBounds(nameplate), dpi);
            }
            finally
            {
                window.Close();
            }
        }).WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task VisitorPetWindow_StatusExpiresBeforeFriendMessage_ReopensPopupWithoutExceptionAndAlignsTail()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "LabPet_VisitorReopen_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var crashDir = Path.Combine(tempDir, "logs");
            var store = new JsonAppDataStore(new StateMigrator(), tempDir);
            await using var transport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
            await using var social = new SocialCoordinatorService(transport, store, new SoloProgressionService());
            using var audio = new NullAudioAnalyzer();
            var friend = new FriendEntry { PetId = "friend-reopen", OwnerName = "나현", PetName = "나송이" };

            await WpfTestHost.RunAsync(async () =>
            {
                var window = new VisitorPetWindow(friend, friend.CachedAppearance, new ProceduralPetMeshFactory(), audio, social)
                {
                    CrashLogDirectory = crashDir,
                    ShowActivated = false,
                    Left = 320,
                    Top = 360
                };
                window.ApplyDisplaySettings(0.7, 0.7);

                try
                {
                    window.Show();
                    await FlushUiAsync();

                    // 실제 앱 진입 흐름과 동일하게 초기 "놀러 왔어!" 상태 말풍선이 열렸다가 만료되어 팝업이 닫힌다.
                    window.ShowVisitorBubble("놀러 왔어!", 80);
                    await WaitUntilAsync(() => window.HasStatusBubble && !window.CurrentBubblePhysicalBounds.IsEmpty);
                    await WaitUntilAsync(() => !window.HasStatusBubble);
                    await FlushUiAsync();

                    var popup = Assert.IsType<Popup>(window.FindName("VisitorBubblePopup"));
                    Assert.False(popup.IsOpen);
                    Assert.True(window.CurrentBubblePhysicalBounds.IsEmpty);

                    // 닫힌 PopupRoot 하위 트리에 IsLayoutSuspended가 남아 있어도 메시지 수신 시 정확히 측정되어 예외 없이 열려야 한다.
                    Assert.True(window.ShowChatMessage("짧은 인사!"));
                    await WaitUntilAsync(() => popup.IsOpen && !window.CurrentBubblePhysicalBounds.IsEmpty);
                    await FlushUiAsync();

                    var cards = Assert.IsType<StackPanel>(window.FindName("VisitorChatCards"));
                    var firstCard = Assert.IsType<Border>(Assert.Single(cards.Children));
                    Assert.True(firstCard.DesiredSize.Width > 0);
                    Assert.True(firstCard.DesiredSize.Height > firstCard.Margin.Top + firstCard.Margin.Bottom);
                    AssertBubbleHeadAlignment(window);

                    // 긴 메시지 및 6건 연속 수신에서도 최대 5개와 꼬리 정렬을 유지해야 한다.
                    for (var i = 1; i <= 5; i++)
                    {
                        Assert.True(window.ShowChatMessage($"연속 메시지 {i}"));
                    }

                    Assert.True(window.ShowChatMessage("이것은 팝업 재오픈 후에도 줄바꿈과 꼬리 정합성이 유지되어야 하는 아주 긴 방문 펫 테스트 메시지입니다."));
                    await FlushUiAsync();

                    Assert.Equal(5, cards.Children.Count);
                    Assert.Equal(5, window.ChatBubbleStack.Items.Count);
                    AssertBubbleHeadAlignment(window);
                    Assert.False(Directory.Exists(crashDir));
                }
                finally
                {
                    if (window.IsLoaded || window.IsVisible)
                    {
                        window.Close();
                    }
                }
            }).WaitAsync(TimeSpan.FromSeconds(15));
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
    public async Task VisitorPetWindow_MultipleOpenCloseTransitions_RemeasureStatusAndChatCardsAccurately()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "LabPet_VisitorCycles_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var store = new JsonAppDataStore(new StateMigrator(), tempDir);
            await using var transport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
            await using var social = new SocialCoordinatorService(transport, store, new SoloProgressionService());
            using var audio = new NullAudioAnalyzer();
            var friend = new FriendEntry { PetId = "friend-cycle", OwnerName = "보미", PetName = "구름이" };

            await WpfTestHost.RunAsync(async () =>
            {
                var window = new VisitorPetWindow(friend, friend.CachedAppearance, new ProceduralPetMeshFactory(), audio, social)
                {
                    CrashLogDirectory = Path.Combine(tempDir, "logs"),
                    ShowActivated = false,
                    Left = 400,
                    Top = 300
                };

                try
                {
                    window.Show();
                    await FlushUiAsync();
                    var popup = Assert.IsType<Popup>(window.FindName("VisitorBubblePopup"));
                    var statusCard = Assert.IsType<Border>(window.FindName("VisitorStatusBubble"));

                    // 1) 짧은 상태 -> 만료
                    window.ShowVisitorBubble("안녕", 70);
                    await WaitUntilAsync(() => popup.IsOpen && !window.CurrentBubblePhysicalBounds.IsEmpty);
                    var shortStatusWidth = statusCard.DesiredSize.Width;
                    await WaitUntilAsync(() => !window.HasStatusBubble);
                    await WaitUntilAsync(() => !popup.IsOpen);

                    // 2) 채팅 -> 만료
                    Assert.True(window.ShowChatMessage("중간 채팅 메시지", 70));
                    await WaitUntilAsync(() => popup.IsOpen && !window.CurrentBubblePhysicalBounds.IsEmpty);
                    AssertBubbleHeadAlignment(window);
                    await WaitUntilAsync(() => !window.ChatBubbleStack.HasItems);
                    await WaitUntilAsync(() => !popup.IsOpen);

                    // 3) 훨씬 긴 상태 -> 재측정 폭 증가 확인
                    window.ShowVisitorBubble("이것은 훨씬 더 긴 상태 알림 문장입니다!", 500);
                    await WaitUntilAsync(() => popup.IsOpen && !window.CurrentBubblePhysicalBounds.IsEmpty);
                    Assert.True(statusCard.DesiredSize.Width > shortStatusWidth);
                    AssertBubbleHeadAlignment(window);

                    // 4) 상태 + 채팅 동시 -> 모두 정상 배치 후 상태 먼저 만료 시 채팅 유지
                    Assert.True(window.ShowChatMessage("상태와 함께 보는 채팅", 1200));
                    await FlushUiAsync();
                    var cards = Assert.IsType<StackPanel>(window.FindName("VisitorChatCards"));
                    Assert.Single(cards.Children);
                    AssertBubbleHeadAlignment(window);

                    await WaitUntilAsync(() => !window.HasStatusBubble);
                    await FlushUiAsync();
                    Assert.True(popup.IsOpen);
                    Assert.Equal(Visibility.Collapsed, statusCard.Visibility);
                    AssertBubbleHeadAlignment(window);
                }
                finally
                {
                    if (window.IsLoaded || window.IsVisible)
                    {
                        window.Close();
                    }
                }
            }).WaitAsync(TimeSpan.FromSeconds(15));
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
    public void VisitorBubblePlacementCalculator_HandlesInvalidInputsAndTinyWorkAreasWithoutException()
    {
        var invalidPoints = new[]
        {
            new Point(double.NaN, 100),
            new Point(double.PositiveInfinity, double.NegativeInfinity),
            new Point(100, 100)
        };
        var invalidSizes = new[]
        {
            Size.Empty,
            new Size(0, 0),
            new Size(0, 30),
            new Size(double.NaN, 40),
            new Size(double.PositiveInfinity, 40)
        };
        var areas = new[]
        {
            Rect.Empty,
            new Rect(0, 0, 0, 100),
            new Rect(0, 0, 8, 8),
            new Rect(-1920, 0, 1920, 1032)
        };
        var dpis = new[]
        {
            new DpiScale(0, 0),
            new DpiScale(double.NaN, 1.5),
            new DpiScale(1.0, 1.0),
            new DpiScale(1.75, 1.75)
        };

        foreach (var point in invalidPoints)
        {
            foreach (var size in invalidSizes)
            {
                foreach (var area in areas)
                {
                    foreach (var dpi in dpis)
                    {
                        var placement = VisitorBubblePlacementCalculator.CalculateBubblePlacement(point, size, area, dpi);
                        Assert.True(double.IsFinite(placement.Tip.X) && double.IsFinite(placement.Tip.Y));
                        Assert.True(double.IsFinite(placement.Scale) && placement.Scale >= 0);
                        if (!placement.Body.IsEmpty)
                        {
                            Assert.True(placement.Body.Width >= 0 && placement.Body.Height >= 0);
                        }

                        var tail = VisitorBubblePlacementCalculator.CalculateTailPlacement(placement, placement.Body, dpi);
                        Assert.True(double.IsFinite(tail.BaseLeft.X) && double.IsFinite(tail.BaseLeft.Y));
                        Assert.True(double.IsFinite(tail.BaseRight.X) && double.IsFinite(tail.BaseRight.Y));

                        var composer = VisitorBubblePlacementCalculator.CalculateComposerPlacement(area, size, dpi);
                        Assert.False(composer.IsEmpty);
                        Assert.True(composer.Width >= 0 && composer.Height >= 0);
                    }
                }
            }
        }
    }

    [Fact]
    public async Task VisitorPetWindow_IsolatesLayoutException_LogsRealStackAndKeepsAppAlive()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "LabPet_VisitorIsolation_" + Guid.NewGuid().ToString("N"));
        var crashDir = Path.Combine(tempDir, "logs");
        Directory.CreateDirectory(tempDir);

        try
        {
            var store = new JsonAppDataStore(new StateMigrator(), tempDir);
            await using var transport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
            await using var social = new SocialCoordinatorService(transport, store, new SoloProgressionService());
            using var audio = new NullAudioAnalyzer();
            var friend = new FriendEntry { PetId = "friend-isolate", OwnerName = "지수", PetName = "모찌" };

            await WpfTestHost.RunAsync(async () =>
            {
                var window = new VisitorPetWindow(friend, friend.CachedAppearance, new ProceduralPetMeshFactory(), audio, social)
                {
                    CrashLogDirectory = crashDir,
                    BeforeLayoutBubbleForTest = () => throw new InvalidOperationException("격리 검증용 레이아웃 예외"),
                    ShowActivated = false,
                    Left = 360,
                    Top = 320
                };

                try
                {
                    window.Show();
                    await FlushUiAsync();

                    Assert.True(window.ShowChatMessage("예외 격리 테스트 메시지"));
                    await FlushUiAsync();

                    // Dispatcher에서 예외가 전파되지 않아야 하며 실패한 말풍선 팝업은 안전하게 닫혀야 한다.
                    var logPath = Path.Combine(crashDir, CrashLogWriter.DefaultFileName);
                    Assert.True(File.Exists(logPath));
                    var logText = File.ReadAllText(logPath);
                    Assert.Contains("VisitorPetWindow.LayoutBubblePopup", logText);
                    Assert.Contains("격리 검증용 레이아웃 예외", logText);
                    Assert.True(window.CurrentBubblePhysicalBounds.IsEmpty);

                    // 이후 정상 배치에서는 다시 말풍선이 렌더링되어야 한다.
                    window.BeforeLayoutBubbleForTest = null;
                    Assert.True(window.ShowChatMessage("정상 복구 메시지"));
                    await WaitUntilAsync(() => !window.CurrentBubblePhysicalBounds.IsEmpty);
                    AssertBubbleHeadAlignment(window);
                }
                finally
                {
                    if (window.IsLoaded || window.IsVisible)
                    {
                        window.Close();
                    }
                }
            }).WaitAsync(TimeSpan.FromSeconds(15));
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
    public void CrashLogWriter_WritesAndRotatesLogFileWithoutPropagatingErrors()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "LabPet_CrashLogTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            CrashLogWriter.Write("TestSource.First", new ArgumentException("첫 번째 예외"), "Context=A", tempDir, maxFileBytes: 200);
            var primaryPath = Path.Combine(tempDir, CrashLogWriter.DefaultFileName);
            var rotatedPath = Path.Combine(tempDir, CrashLogWriter.RotatedFileName);
            Assert.True(File.Exists(primaryPath));
            var firstContent = File.ReadAllText(primaryPath);
            Assert.Contains("TestSource.First", firstContent);
            Assert.Contains("ArgumentException", firstContent);
            Assert.Contains("Context=A", firstContent);

            CrashLogWriter.Write("TestSource.Second", new InvalidOperationException("두 번째 예외"), "Context=B", tempDir, maxFileBytes: 200);
            Assert.True(File.Exists(rotatedPath));
            Assert.Contains("TestSource.First", File.ReadAllText(rotatedPath));
            Assert.Contains("TestSource.Second", File.ReadAllText(primaryPath));
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
    public async Task EndToEnd_LanAndGcpMessages_RenderOnLiveVisitorWindowAfterGreetingExpired()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "LabPet_LiveVisitorE2E_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        WebApplication? server = null;

        try
        {
            var crashDir = Path.Combine(tempRoot, "logs");

            // 1) LAN P2P 수신 메시지 -> 인사 만료 후 닫힌 VisitorPetWindow 재오픈 렌더링
            var hostLan = new PetProfile { OwnerName = "호스트", PetName = "내펫" };
            var friendLan = new PetProfile { OwnerName = "나현", PetName = "나송이" };
            var lanSecret = PacketSigner.GeneratePairSecretBase64();

            var storeLanHost = new JsonAppDataStore(new StateMigrator(), Path.Combine(tempRoot, "lan-host"));
            await using var lanHostTransport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
            await using var lanFriendTransport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
            await lanFriendTransport.StartAsync(friendLan, []);

            var lanFriendEntry = new FriendEntry
            {
                PetId = friendLan.PetId,
                OwnerName = friendLan.OwnerName,
                PetName = friendLan.PetName,
                LastKnownIp = IPAddress.Loopback.ToString(),
                LastKnownTcpPort = lanFriendTransport.BoundTcpPort,
                PairSecretBase64 = lanSecret
            };
            await storeLanHost.SaveFriendsAsync(new FriendsStore { Friends = [lanFriendEntry] });
            await using var lanHostSocial = new SocialCoordinatorService(lanHostTransport, storeLanHost, new SoloProgressionService());
            await lanHostSocial.StartAsync(hostLan);

            // 2) GCP SignalR 중계 수신 메시지 준비
            var serverPort = GetFreeTcpPort();
            var serverUrl = $"http://127.0.0.1:{serverPort}";
            server = LabPetServerHost.BuildApplication(new ServerOptions
            {
                ListenUrl = serverUrl,
                ServerSecret = "live-visitor-e2e-server-secret-value-2026!",
                RequireConfiguredSecret = true,
                DataDirectory = Path.Combine(tempRoot, "server")
            });
            await server.StartAsync();

            var connOpts = new ServerConnectionOptions { Enabled = true, ServerUrl = serverUrl };
            await using var gcpHostTransport = new GcpSignalRTransport(connOpts, new DeviceIdentityStore(Path.Combine(tempRoot, "gcp-host-id")));
            await using var gcpFriendTransport = new GcpSignalRTransport(connOpts, new DeviceIdentityStore(Path.Combine(tempRoot, "gcp-friend-id")));

            var hostGcp = new PetProfile { OwnerName = "호스트GCP", PetName = "내펫G" };
            var friendGcp = new PetProfile { OwnerName = "구대", PetName = "덕구" };
            var storeGcpHost = new JsonAppDataStore(new StateMigrator(), Path.Combine(tempRoot, "gcp-host"));
            await using var gcpHostSocial = new SocialCoordinatorService(gcpHostTransport, storeGcpHost, new SoloProgressionService());
            await gcpHostSocial.StartAsync(hostGcp);
            await gcpFriendTransport.StartAsync(friendGcp, []);
            await WaitUntilAsync(() => gcpFriendTransport.GetDiscoveredPeers().Any(p => p.PetId == hostGcp.PetId));

            var incomingReqTcs = new TaskCompletionSource<PendingFriendRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            gcpHostSocial.FriendRequestArrived += (_, req) => incomingReqTcs.TrySetResult(req);
            var friendAcceptedTcs = new TaskCompletionSource<FriendAcceptedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            gcpFriendTransport.FriendAccepted += (_, e) => friendAcceptedTcs.TrySetResult(e);

            var gcpPeer = Assert.Single(gcpFriendTransport.GetDiscoveredPeers(), p => p.PetId == hostGcp.PetId);
            Assert.True(await gcpFriendTransport.SendFriendRequestAsync(gcpPeer));
            var incomingReq = await incomingReqTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True((await gcpHostSocial.RespondFriendRequestDetailedAsync(incomingReq, accept: true)).Succeeded);
            var gcpFriendEntry = await WaitUntilAsyncValue(() => gcpHostSocial.Friends.FirstOrDefault(f => f.PetId == friendGcp.PetId));
            var friendAccepted = await friendAcceptedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var gcpHostFromFriend = new FriendEntry
            {
                PetId = hostGcp.PetId,
                OwnerName = hostGcp.OwnerName,
                PetName = hostGcp.PetName,
                PairSecretBase64 = friendAccepted.PairSecretBase64
            };
            gcpFriendTransport.UpdateKnownFriends([gcpHostFromFriend]);

            using var audio = new NullAudioAnalyzer();
            var meshFactory = new ProceduralPetMeshFactory();

            await WpfTestHost.RunAsync(async () =>
            {
                var order = new VisitorPopupOrder();
                var visitors = new Dictionary<string, VisitorPetWindow>(StringComparer.OrdinalIgnoreCase);
                var hostTarget = new StubMessageTarget();
                var router = new FriendMessageRouter(petId => visitors.GetValueOrDefault(petId), () => hostTarget);

                var lanVisitor = new VisitorPetWindow(lanFriendEntry, lanFriendEntry.CachedAppearance, meshFactory, audio, lanHostSocial, order)
                {
                    CrashLogDirectory = crashDir,
                    ShowActivated = false,
                    Left = 240,
                    Top = 360
                };
                var gcpVisitor = new VisitorPetWindow(gcpFriendEntry, gcpFriendEntry.CachedAppearance, meshFactory, audio, gcpHostSocial, order)
                {
                    CrashLogDirectory = crashDir,
                    ShowActivated = false,
                    Left = 420,
                    Top = 360
                };
                visitors[lanFriendEntry.PetId] = lanVisitor;
                visitors[gcpFriendEntry.PetId] = gcpVisitor;

                lanHostSocial.FriendMessageReceived += (_, msg) => Application.Current.Dispatcher.BeginInvoke(() => router.Route(msg));
                gcpHostSocial.FriendMessageReceived += (_, msg) => Application.Current.Dispatcher.BeginInvoke(() => router.Route(msg));

                try
                {
                    lanVisitor.Show();
                    gcpVisitor.Show();
                    lanVisitor.ShowVisitorBubble("놀러 왔어!", 60);
                    gcpVisitor.ShowVisitorBubble("놀러 왔어!", 60);
                    await WaitUntilAsync(() => !lanVisitor.HasStatusBubble && !gcpVisitor.HasStatusBubble);
                    await FlushUiAsync();

                    Assert.True(await lanFriendTransport.SendBubbleMessageAsync(
                        new FriendEntry
                        {
                            PetId = hostLan.PetId,
                            LastKnownIp = IPAddress.Loopback.ToString(),
                            LastKnownTcpPort = lanHostTransport.BoundTcpPort,
                            PairSecretBase64 = lanSecret
                        },
                        "실제 LAN 메시지 수신!"));
                    await WaitUntilAsync(() => !lanVisitor.CurrentBubblePhysicalBounds.IsEmpty);
                    await FlushUiAsync();
                    Assert.Equal("실제 LAN 메시지 수신!", Assert.Single(lanVisitor.ChatBubbleStack.Items).Text);
                    AssertBubbleHeadAlignment(lanVisitor);

                    Assert.True(await gcpFriendTransport.SendBubbleMessageAsync(gcpHostFromFriend, "실제 GCP 메시지 수신!"));
                    await WaitUntilAsync(() => !gcpVisitor.CurrentBubblePhysicalBounds.IsEmpty);
                    await FlushUiAsync();
                    Assert.Equal("실제 GCP 메시지 수신!", Assert.Single(gcpVisitor.ChatBubbleStack.Items).Text);
                    AssertBubbleHeadAlignment(gcpVisitor);

                    Assert.Empty(hostTarget.ReceivedMessages);
                    Assert.False(Directory.Exists(crashDir));
                }
                finally
                {
                    gcpVisitor.Close();
                    lanVisitor.Close();
                }
            }).WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            if (server is not null)
            {
                await server.StopAsync();
                await server.DisposeAsync();
            }

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static async Task<T> WaitUntilAsyncValue<T>(Func<T?> getter, int timeoutMs = 4000) where T : class
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var value = getter();
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(15);
        }

        return getter() ?? throw new TimeoutException("조건이 제한 시간 내에 충족되지 않았습니다.");
    }

    private static void AssertComposerBelowNameplate(Rect composer, Rect nameplate, DpiScale dpi)
    {
        Assert.InRange((composer.Top - nameplate.Bottom) / dpi.DpiScaleY, 4, 6);
        Assert.InRange(Math.Abs(composer.Left + composer.Width / 2 - nameplate.Left - nameplate.Width / 2), 0, 1.5);
    }

    private static Point AssertBubbleHeadAlignment(VisitorPetWindow window)
    {
        var tail = Assert.IsType<BubblePath>(window.FindName("VisitorBubbleTail"));
        Assert.True(tail.IsVisible);
        var geometry = Assert.IsType<PathGeometry>(tail.Data);
        var figure = Assert.Single(geometry.Figures);
        var tip = tail.PointToScreen(Assert.IsType<LineSegment>(figure.Segments[0]).Point);
        var head = window.GetPopupHeadPhysical();
        Assert.InRange((tip - head).Length, 0, 1.5);
        return tail.PointToScreen(figure.StartPoint);
    }

    private static Rect GetScreenBounds(FrameworkElement element)
        => new(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));

    private static IntPtr GetPopupHandle(VisitorPetWindow window, string name)
    {
        var popup = Assert.IsType<Popup>(window.FindName(name));
        var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(popup.Child));
        Assert.NotEqual(IntPtr.Zero, source.Handle);
        return source.Handle;
    }

    private static async Task FlushUiAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => tcs.SetResult());
        await tcs.Task;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(15);
        }

        Assert.True(condition(), "조건이 제한 시간 내에 충족되지 않았습니다.");
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
