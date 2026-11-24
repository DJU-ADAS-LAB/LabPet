using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using Polygon = System.Windows.Shapes.Polygon;
using System.Windows.Threading;
using LabPet.Client;
using LabPet.Client.Audio;
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

    [Fact]
    public void VisitorBubblePlacement_HandlesMoveScaleMultiMonitorAndCollisionAvoidance()
    {
        var workAreaPrimary = new Rect(0, 0, 1920, 1040);

        // 1. 이동 및 크기 변경 시에도 상단 중앙 유지
        var petSmall = new Rect(600, 500, 140, 140);
        var petLarge = new Rect(900, 420, 264, 279);
        var popupSize = new Size(200, 88);

        var placedSmall = VisitorBubblePlacementCalculator.CalculatePlacement(petSmall, popupSize, workAreaPrimary);
        var placedLarge = VisitorBubblePlacementCalculator.CalculatePlacement(petLarge, popupSize, workAreaPrimary);

        Assert.Equal(petSmall.Left + (petSmall.Width - popupSize.Width) * 0.5, placedSmall.X, precision: 2);
        Assert.Equal(petSmall.Top - popupSize.Height + 8.0, placedSmall.Y, precision: 2);
        Assert.Equal(petLarge.Left + (petLarge.Width - popupSize.Width) * 0.5, placedLarge.X, precision: 2);
        Assert.Equal(petLarge.Top - popupSize.Height + 8.0, placedLarge.Y, precision: 2);

        // 2. 좌상단 화면 경계(다중 모니터 음수 좌표 포함)를 벗어나지 않음
        var secondaryMonitorArea = new Rect(-1920, 0, 1920, 1040);
        var petAtSecondaryCorner = new Rect(-1918, 8, 176, 186);
        var clamped = VisitorBubblePlacementCalculator.CalculatePlacement(petAtSecondaryCorner, popupSize, secondaryMonitorArea);
        var clampedRect = new Rect(clamped, popupSize);

        Assert.True(secondaryMonitorArea.Contains(clampedRect), $"보조 모니터 경계 이탈: {clampedRect} vs {secondaryMonitorArea}");

        // 3. 인접한 두 방문 펫의 말풍선 충돌 회피
        var occupiedBubble = new Rect(920, 380, 220, 96);
        var adjacentPet = new Rect(980, 468, 176, 186);
        var avoided = VisitorBubblePlacementCalculator.CalculatePlacement(
            adjacentPet,
            new Size(220, 96),
            workAreaPrimary,
            [occupiedBubble]);
        var avoidedRect = new Rect(avoided, new Size(220, 96));

        Assert.True(workAreaPrimary.Contains(avoidedRect));
        Assert.False(occupiedBubble.IntersectsWith(avoidedRect), $"기존 말풍선과 겹침: {occupiedBubble} vs {avoidedRect}");
    }

    [Fact]
    public void VisitorBubblePlacement_StackedComposerAndBubbleNeverOverlapAcrossScreenEdges()
    {
        var workArea = new Rect(0, 0, 1920, 1040);
        var composerSize = new Size(242, 58);
        var bubbleSize = new Size(220, 88);

        // 1) 화면 중앙/하단: 말풍선은 채팅 입력창 바로 위에 쌓이며 겹치지 않는다.
        var midPet = new Rect(600, 500, 214, 224);
        var composerPosMid = VisitorBubblePlacementCalculator.CalculatePlacement(
            midPet, composerSize, workArea, overlapYPhysical: 4.0, gapPhysical: 8.0);
        var composerRectMid = new Rect(composerPosMid, composerSize);

        var bubblePosMid = VisitorBubblePlacementCalculator.CalculateStackedBubblePlacement(
            midPet, composerRectMid, bubbleSize, workArea, baseOverlapYPhysical: 8.0, gapPhysical: 8.0);
        var bubbleRectMid = new Rect(bubblePosMid, bubbleSize);

        Assert.True(workArea.Contains(composerRectMid));
        Assert.True(workArea.Contains(bubbleRectMid));
        Assert.False(composerRectMid.IntersectsWith(bubbleRectMid), $"중앙 배치 겹침: {composerRectMid} vs {bubbleRectMid}");
        Assert.True(bubbleRectMid.Bottom <= composerRectMid.Top, $"말풍선이 채팅 입력창 위에 배치되어야 합니다: {bubbleRectMid} vs {composerRectMid}");

        // 2) 화면 최상단 근처: 채팅창 위 공간이 부족하면 말풍선이 겹치지 않는 대체 위치로 회피한다.
        var topPet = new Rect(600, 44, 214, 224);
        var composerPosTop = VisitorBubblePlacementCalculator.CalculatePlacement(
            topPet, composerSize, workArea, overlapYPhysical: 4.0, gapPhysical: 8.0);
        var composerRectTop = new Rect(composerPosTop, composerSize);

        var bubblePosTop = VisitorBubblePlacementCalculator.CalculateStackedBubblePlacement(
            topPet, composerRectTop, bubbleSize, workArea, baseOverlapYPhysical: 8.0, gapPhysical: 8.0);
        var bubbleRectTop = new Rect(bubblePosTop, bubbleSize);

        Assert.True(workArea.Contains(composerRectTop));
        Assert.True(workArea.Contains(bubbleRectTop));
        Assert.False(composerRectTop.IntersectsWith(bubbleRectTop), $"최상단 배치 겹침: {composerRectTop} vs {bubbleRectTop}");
    }

    [Fact]
    public void VisitorComposerPlacement_UsesTheSideWhenABubbleBlocksTheSpaceAboveThePet()
    {
        var workArea = new Rect(0, 0, 1600, 900);
        var pet = new Rect(600, 440, 214, 175);
        var composerSize = new Size(242, 58);
        var occupied = new Rect(470, 310, 475, 138);
        var composer = new Rect(VisitorBubblePlacementCalculator.CalculatePlacement(
            pet, composerSize, workArea, [occupied], overlapYPhysical: 4, allowAboveObstacles: false), composerSize);

        Assert.True(workArea.Contains(composer));
        Assert.False(composer.IntersectsWith(occupied));
        Assert.False(composer.IntersectsWith(pet));
        Assert.True(composer.Bottom >= pet.Top);
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

                Assert.Equal(FriendMessageRouteOutcome.Visitor, router.RouteStatus(new("second", "쓰다듬기 전송!", 5000)));
                await FlushUiAsync();
                Assert.False(first.HasStatusBubble);
                Assert.Equal("쓰다듬기 전송!", second.StatusBubbleText);
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
                Assert.Equal(FriendMessageRouteOutcome.Host, router.RouteStatus(new("absent", "쓰다듬어 줬어요!", 2000)));
                Assert.Equal("쓰다듬어 줬어요!", Assert.Single(host.ReceivedStatuses));
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

            var peerTransport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
            var hostTransport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
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
            var social = new SocialCoordinatorService(hostTransport, store, new SoloProgressionService());
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
                    var tail = Assert.IsType<Polygon>(window.FindName("VisitorBubbleTail"));
                    Assert.True(tail.IsVisible);
                    var shape = tail.RenderedGeometry.Bounds;
                    var tailTip = tail.PointToScreen(new Point(shape.Left + shape.Width / 2, shape.Bottom));
                    var petCenter = viewport.PointToScreen(new Point(viewport.ActualWidth / 2, 0));
                    Assert.InRange(Math.Abs(tailTip.X - petCenter.X), 0, 1);

                    // 클릭 시 쓰다듬기 대신 채팅 입력창이 열려야 한다.
                    window.HandleVisitorClick();
                    await FlushUiAsync();
                    Assert.True(window.IsChatComposerOpen);
                    Assert.False(window.CurrentComposerPhysicalBounds.IsEmpty);
                    Assert.Equal(0, Volatile.Read(ref petInteractionCount));

                    // 채팅 입력창이 열린 상태에서 수신 메시지가 와도 입력창이 유지되고 영역이 겹치지 않는다.
                    Assert.True(window.TryShowFriendMessage(CreateMessage(friend.PetId, "지금 답장하는 중이야?")));
                    await FlushUiAsync();

                    Assert.True(window.IsChatComposerOpen);
                    Assert.False(window.CurrentBubblePhysicalBounds.IsEmpty);
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
                    Assert.True(await window.SendVisitorChatMessageAsync());
                    Assert.Equal("이어 보내는 메시지", await secondBubbleTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
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

            await peerTransport.DisposeAsync();
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
                    window.ShowVisitorBubble("쓰다듬기 전송!", 5000);
                    Assert.True(window.HasStatusBubble);
                    Assert.Equal("쓰다듬기 전송!", window.StatusBubbleText);

                    // 긴 메시지 포함 7연속 채팅 전송 → 최대 5개 유지 + 시스템 알림은 덮어쓰지 않음 + 창 높이/위치 불변
                    var longText = "이것은 아주 긴 친구 메시지입니다. 방문 펫 말풍선에서 자동 줄바꿈되고 창이 흔들리지 않아야 합니다.";
                    for (var i = 1; i <= 6; i++)
                    {
                        Assert.Equal(FriendMessageRouteOutcome.Visitor, router.Route(CreateMessage(friend.PetId, $"채팅 {i}")));
                    }

                    Assert.Equal(FriendMessageRouteOutcome.Visitor, router.Route(CreateMessage(friend.PetId, longText)));
                    await FlushUiAsync();

                    Assert.Equal(5, window.ChatBubbleStack.Items.Count);
                    Assert.Equal(["채팅 3", "채팅 4", "채팅 5", "채팅 6", longText],
                        window.ChatBubbleStack.Items.Select(x => x.Text));
                    Assert.True(window.HasStatusBubble);
                    Assert.Equal("쓰다듬기 전송!", window.StatusBubbleText);
                    Assert.Equal(initialTop, window.Top);
                    Assert.Equal(initialHeight, window.Height);
                    Assert.False(window.CurrentBubblePhysicalBounds.IsEmpty);

                    // 두 번째 방문 펫 창을 인접 배치하면 말풍선 영역이 겹치지 않도록 자동 회피
                    var friend2 = new FriendEntry
                    {
                        PetId = "friend-ui-02",
                        OwnerName = "민수",
                        PetName = "모찌",
                        CachedAppearance = new PetAppearance()
                    };
                    VisitorPetWindow? window2 = null;
                    try
                    {
                        var visitors = new Dictionary<string, VisitorPetWindow>(StringComparer.OrdinalIgnoreCase)
                        {
                            [friend.PetId] = window
                        };
                        IReadOnlyList<Rect> GetOccupied(string excludePetId) => visitors
                            .Where(kv => !string.Equals(kv.Key, excludePetId, StringComparison.OrdinalIgnoreCase)
                                         && kv.Value.IsAvailableForMessages
                                         && !kv.Value.CurrentBubblePhysicalBounds.IsEmpty)
                            .Select(kv => kv.Value.CurrentBubblePhysicalBounds)
                            .ToList();

                        window2 = new VisitorPetWindow(
                            friend2,
                            friend2.CachedAppearance,
                            meshFactory,
                            audio,
                            social,
                            GetOccupied)
                        {
                            ShowActivated = false,
                            Left = window.Left + 36,
                            Top = window.Top
                        };
                        visitors[friend2.PetId] = window2;
                        window2.Show();
                        await FlushUiAsync();

                        Assert.True(window2.TryShowFriendMessage(CreateMessage(friend2.PetId, "인접한 친구의 말풍선 테스트 메시지")));
                        await FlushUiAsync();

                        Assert.False(window2.CurrentBubblePhysicalBounds.IsEmpty);
                        Assert.False(window.CurrentBubblePhysicalBounds.IntersectsWith(window2.CurrentBubblePhysicalBounds));
                    }
                    finally
                    {
                        if (window2 is { IsLoaded: true })
                        {
                            window2.Close();
                        }
                    }

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
