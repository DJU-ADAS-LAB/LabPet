using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using Ellipse = System.Windows.Shapes.Ellipse;
using LabPet.Client.Audio;
using LabPet.Client.Navigation;
using LabPet.Client.Network;
using LabPet.Client.Social;
using LabPet.Client.Solo;
using LabPet.Client.Storage;
using LabPet.Client.ViewModels;
using LabPet.Client.Views;
using LabPet.Client.Views.Hub.Pages;
using LabPet.Server;
using LabPet.Server.Options;
using LabPet.Shared.Models;
using LabPet.Shared.Protocol;
using LabPet.Shared.Transport;
using Xunit;

namespace LabPet.Tests;

[Collection("WpfUi")]
public sealed class PomodoroFocusAndUiTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "LabPet_PomodoroTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // 임시 테스트 디렉터리 정리 예외 무시
        }
    }

    [Fact]
    public async Task Pomodoro_DurationBoundaries_PauseLock_BreakRelease_And_PageReentry()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var store = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "ui"));
            await using var transport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
            var progression = new SoloProgressionService();
            await using var social = new SocialCoordinatorService(transport, store, progression);
            var pomodoro = new PomodoroTimerService();
            var focusEvents = new List<bool>();
            pomodoro.FocusStateChanged += (_, isFocusing) => focusEvents.Add(isFocusing);

            var audio = new StubAudioAnalyzer();
            var profile = new PetProfile { PetName = "모찌" };
            var petVm = new PetWindowViewModel(store, social);
            petVm.ApplyProfile(profile);

            using var gamesVm = new CoinGamesViewModel(store, progression, social);
            using var vm1 = new SoloHubViewModel(pomodoro, audio, gamesVm, progression.Economy);
            var page1 = new PomodoroPageView(vm1, pomodoro, petVm);

            try
            {
                page1.OnActivated(new NavigationRequest(HubPage.Pomodoro));

                // 1. 1~60분 경계값 및 원형 다이얼 슬라이더·버튼·입력칸 조절, 종료 버튼·안내문 확인
                Assert.Equal("종료", page1.EndPomodoroButton.Content);
                Assert.Contains("차단", page1.FocusBlockInfoText.Text);
                Assert.Contains("집중", page1.FocusBlockInfoText.Text);

                // 원형 다이얼 슬라이더 드래그(3시=15분, 6시=30분, 9시=45분, 12시 경계 클램핑)
                Assert.True(page1.ApplyDialPointerForTest(new Point(SoloHubViewModel.PomodoroDialCenter + 95, SoloHubViewModel.PomodoroDialCenter)));
                Assert.Equal(15, pomodoro.FocusMinutes);
                Assert.Equal("15:00", vm1.PomodoroTimeText);
                Assert.True(page1.ApplyDialPointerForTest(new Point(SoloHubViewModel.PomodoroDialCenter, SoloHubViewModel.PomodoroDialCenter + 95), usePreviousMinutes: true));
                Assert.Equal(30, pomodoro.FocusMinutes);
                Assert.True(page1.ApplyDialPointerForTest(new Point(SoloHubViewModel.PomodoroDialCenter - 95, SoloHubViewModel.PomodoroDialCenter), usePreviousMinutes: true));
                Assert.Equal(45, pomodoro.FocusMinutes);
                // 55분 근처에서 시계 방향 12시 초과 드래그 시 60분으로 고정 (1분으로 튀지 않음)
                vm1.PomodoroFocusMinutes = 55;
                Assert.True(page1.ApplyDialPointerForTest(new Point(SoloHubViewModel.PomodoroDialCenter + 20, SoloHubViewModel.PomodoroDialCenter - 95), usePreviousMinutes: true));
                Assert.Equal(60, pomodoro.FocusMinutes);
                // 5분 근처에서 반시계 방향 12시 초과 드래그 시 1분으로 고정 (60분으로 튀지 않음)
                vm1.PomodoroFocusMinutes = 5;
                Assert.True(page1.ApplyDialPointerForTest(new Point(SoloHubViewModel.PomodoroDialCenter - 20, SoloHubViewModel.PomodoroDialCenter - 95), usePreviousMinutes: true));
                Assert.Equal(1, pomodoro.FocusMinutes);

                vm1.PomodoroFocusMinutes = 0;
                Assert.Equal(1, pomodoro.FocusMinutes);
                Assert.Equal("01:00", vm1.PomodoroTimeText);
                Assert.Equal("1", vm1.PomodoroFocusMinutesInputText);
                Assert.Contains("1분 설정 · 완료 시 +0 코인", vm1.PomodoroRewardPreviewText);

                vm1.IncrementPomodoroMinutesCommand.Execute(null);
                Assert.Equal(2, pomodoro.FocusMinutes);
                Assert.Equal("02:00", vm1.PomodoroTimeText);

                vm1.DecrementPomodoroMinutesCommand.Execute(null);
                Assert.Equal(1, pomodoro.FocusMinutes);

                vm1.PomodoroFocusMinutesInputText = "99";
                vm1.CommitPomodoroFocusMinutesInput();
                Assert.Equal(60, pomodoro.FocusMinutes);
                Assert.Equal("60:00", vm1.PomodoroTimeText);
                Assert.Contains($"60분 설정 · 완료 시 +{PetEconomyService.GetPomodoroRewardCoins(60)} 코인", vm1.PomodoroRewardPreviewText);

                vm1.PomodoroFocusMinutesInputText = "35";
                vm1.CommitPomodoroFocusMinutesInput();
                Assert.Equal(35, pomodoro.FocusMinutes);
                Assert.Equal("35:00", vm1.PomodoroTimeText);
                Assert.Contains($"35분 설정 · 완료 시 +{PetEconomyService.GetPomodoroRewardCoins(35)} 코인", vm1.PomodoroRewardPreviewText);
                Assert.True(vm1.CanConfigurePomodoro);
                Assert.Equal(Visibility.Visible, page1.CircularDialKnob.Visibility);
                Assert.NotEqual(System.Windows.Media.Geometry.Empty, vm1.PomodoroDialArcGeometry);
                Assert.True(page1.FocusMinutesTextBox.IsEnabled);
                Assert.True(page1.DecrementMinutesButton.IsEnabled);
                Assert.True(page1.IncrementMinutesButton.IsEnabled);
                Assert.True(page1.Preset15Button.IsEnabled);

                // 2. 시작 후 원형 다이얼·입력·프리셋 모두 잠금 + 서비스 거부 + 집중 활성
                vm1.TogglePomodoroCommand.Execute(null);
                await WpfTestHost.FlushAsync();

                Assert.True(pomodoro.IsRunning);
                Assert.True(pomodoro.IsDurationLocked);
                Assert.True(pomodoro.IsFocusActive);
                Assert.False(vm1.CanConfigurePomodoro);
                Assert.Equal(Visibility.Collapsed, page1.CircularDialKnob.Visibility);
                Assert.False(page1.ApplyDialPointerForTest(new Point(SoloHubViewModel.PomodoroDialCenter + 95, SoloHubViewModel.PomodoroDialCenter)));
                Assert.Equal(35, pomodoro.FocusMinutes);
                Assert.False(page1.FocusMinutesTextBox.IsEnabled);
                Assert.False(page1.DecrementMinutesButton.IsEnabled);
                Assert.False(page1.IncrementMinutesButton.IsEnabled);
                Assert.False(page1.Preset15Button.IsEnabled);
                Assert.False(page1.Preset25Button.IsEnabled);
                Assert.False(page1.Preset50Button.IsEnabled);
                Assert.False(vm1.SetPomodoroPresetCommand.CanExecute("15"));
                Assert.False(vm1.IncrementPomodoroMinutesCommand.CanExecute(null));
                Assert.False(vm1.DecrementPomodoroMinutesCommand.CanExecute(null));

                vm1.PomodoroFocusMinutes = 10;
                pomodoro.ConfigureDurations(10, 2);
                Assert.Equal(35, pomodoro.FocusMinutes);

                // 3. 일시정지 중에도 시간 잠금과 집중 차단 유지
                vm1.TogglePomodoroCommand.Execute(null);
                await WpfTestHost.FlushAsync();

                Assert.False(pomodoro.IsRunning);
                Assert.True(pomodoro.IsDurationLocked);
                Assert.True(pomodoro.IsFocusActive);
                Assert.False(vm1.CanConfigurePomodoro);
                Assert.False(page1.ApplyDialPointerForTest(new Point(SoloHubViewModel.PomodoroDialCenter, SoloHubViewModel.PomodoroDialCenter + 95)));
                vm1.PomodoroFocusMinutes = 20;
                pomodoro.ConfigureDurations(20, 4);
                Assert.Equal(35, pomodoro.FocusMinutes);

                // 4. 페이지 이탈 후 재진입(새 View/ViewModel 생성) 시에도 잠금 유지
                page1.OnDeactivated();
                page1.Dispose();

                using var vm2 = new SoloHubViewModel(pomodoro, audio, gamesVm, progression.Economy);
                var page2 = new PomodoroPageView(vm2, pomodoro, petVm);
                try
                {
                    page2.OnActivated(new NavigationRequest(HubPage.Pomodoro));
                    await WpfTestHost.FlushAsync();

                    Assert.False(vm2.CanConfigurePomodoro);
                    Assert.True(pomodoro.IsDurationLocked);
                    Assert.True(pomodoro.IsFocusActive);
                    Assert.Equal(Visibility.Collapsed, page2.CircularDialKnob.Visibility);
                    Assert.False(page2.FocusMinutesTextBox.IsEnabled);
                    Assert.False(page2.Preset25Button.IsEnabled);
                    Assert.Equal(35, vm2.PomodoroFocusMinutes);

                    // 5. 집중 완료 -> 휴식 전환 시 집중 차단 해제(휴식 중에는 차단 안 함)
                    pomodoro.CompleteCurrentPhaseForTest();
                    await WpfTestHost.FlushAsync();

                    Assert.Equal(PomodoroPhase.Break, pomodoro.CurrentPhase);
                    Assert.False(pomodoro.IsFocusActive);
                    Assert.True(pomodoro.IsDurationLocked);
                    Assert.Equal([true, false], focusEvents);

                    // 휴식 시작 후 휴식 완료 -> Focus 대기 상태로 돌아와도 타이머 정지 중이므로 집중 차단 자동 재활성화 금지
                    pomodoro.StartOrPause();
                    Assert.False(pomodoro.IsFocusActive);
                    pomodoro.CompleteCurrentPhaseForTest();
                    await WpfTestHost.FlushAsync();

                    Assert.Equal(PomodoroPhase.Focus, pomodoro.CurrentPhase);
                    Assert.False(pomodoro.IsRunning);
                    Assert.False(pomodoro.IsFocusActive);
                    Assert.True(pomodoro.IsDurationLocked);

                    // 6. 종료(Reset) 시 시간 설정 다시 허용 및 원형 다이얼 재활성화
                    vm2.ResetPomodoroCommand.Execute(null);
                    await WpfTestHost.FlushAsync();

                    Assert.False(pomodoro.IsDurationLocked);
                    Assert.False(pomodoro.IsFocusActive);
                    Assert.True(vm2.CanConfigurePomodoro);
                    Assert.Equal(Visibility.Visible, page2.CircularDialKnob.Visibility);
                    Assert.True(page2.FocusMinutesTextBox.IsEnabled);

                    Assert.True(page2.ApplyDialPointerForTest(new Point(SoloHubViewModel.PomodoroDialCenter + 95, SoloHubViewModel.PomodoroDialCenter)));
                    Assert.Equal(15, pomodoro.FocusMinutes);
                    Assert.Equal("15:00", vm2.PomodoroTimeText);
                }
                finally
                {
                    page2.Dispose();
                }
            }
            finally
            {
                pomodoro.Reset();
                page1.Dispose();
            }
        });
    }

    [Fact]
    public async Task PomodoroFocus_TwoClientsLan_BlocksAllInteractions_ShowsExactMessage_NoRewardsOrLoop_AndSyncsOverlayIndicator()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var storeA = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "lanA"));
            var storeB = new JsonAppDataStore(new StateMigrator(), Path.Combine(_tempRoot, "lanB"));
            var progA = new SoloProgressionService();
            var progB = new SoloProgressionService();

            var portA = GetFreeTcpPort();
            var portB = GetFreeTcpPort();

            await using var transportA = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: portA);
            await using var transportB = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: portB);

            await using var socialA = new SocialCoordinatorService(transportA, storeA, progA);
            await using var socialB = new SocialCoordinatorService(transportB, storeB, progB);

            var profileA = new PetProfile { OwnerName = "철수", PetName = "모찌A" };
            var profileB = new PetProfile { OwnerName = "영희", PetName = "냥이B" };
            profileA.SoloProgress.Coins = 200;
            profileB.SoloProgress.Coins = 200;
            await storeA.SaveProfileAsync(profileA);
            await storeB.SaveProfileAsync(profileB);

            var pairSecret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

            var friendBForA = new FriendEntry
            {
                PetId = profileB.PetId,
                OwnerName = profileB.OwnerName,
                PetName = profileB.PetName,
                LastKnownIp = "127.0.0.1",
                LastKnownTcpPort = portB,
                PairSecretBase64 = pairSecret,
                Intimacy = 1
            };
            var friendAForB = new FriendEntry
            {
                PetId = profileA.PetId,
                OwnerName = profileA.OwnerName,
                PetName = profileA.PetName,
                LastKnownIp = "127.0.0.1",
                LastKnownTcpPort = portA,
                PairSecretBase64 = pairSecret,
                Intimacy = 1
            };

            await storeA.SaveFriendsAsync(new FriendsStore { Friends = [friendBForA] });
            await storeB.SaveFriendsAsync(new FriendsStore { Friends = [friendAForB] });
            await socialA.StartAsync(profileA);
            await socialB.StartAsync(profileB);

            // 상호 친구 프레즌스 동기화 (B는 온라인, 깨어있고 음악 재생 중 상태)
            await socialB.UpdateLocalPresenceAsync(profileB, isMusicActive: true, isSleeping: false, isFocusing: false);
            transportA.HandleDiscoveryHello(
                new DiscoveryHelloPacket
                {
                    PetId = profileB.PetId,
                    OwnerName = profileB.OwnerName,
                    PetName = profileB.PetName,
                    TcpPort = transportB.BoundTcpPort,
                    AppVersion = "0.5.0",
                    IsMusicActive = true,
                    IsSleeping = false,
                    IsFocusing = false
                },
                "127.0.0.1",
                CancellationToken.None);

            await WaitUntilAsync(() => socialA.DiscoveredPeers.Any(p => p.PetId == profileB.PetId && p.IsFriend));

            using var snowballsA = new SnowballInteractionService(transportA, socialA);
            using var snowballsB = new SnowballInteractionService(transportB, socialB);
            var petVmA = new PetWindowViewModel(storeA, socialA, snowballsA);
            petVmA.ApplyProfile(profileA);
            var socialVmA = new SocialViewModel(socialA, snowballsA);
            using var gamesVmA = new CoinGamesViewModel(storeA, progA, socialA);
            using var gamesVmB = new CoinGamesViewModel(storeB, progB, socialB);
            gamesVmA.BindProfile(profileA);
            gamesVmB.BindProfile(profileB);

            var snowballRejectedMessages = new List<string>();
            snowballsA.InteractionRejected += (_, msg) => snowballRejectedMessages.Add(msg);

            var receivedMessagesOnB = 0;
            var receivedStatusOnB = 0;
            var receivedReactionsOnB = 0;
            var receivedSnowballsOnB = 0;
            var receivedGameEventsOnB = 0;
            var musicFollowEventsOnB = 0;
            socialB.FriendMessageReceived += (_, _) => receivedMessagesOnB++;
            socialB.FriendStatusNotification += (_, _) => receivedStatusOnB++;
            socialB.PetReactionTriggered += (_, _) => receivedReactionsOnB++;
            snowballsB.Received += (_, _) => receivedSnowballsOnB++;
            socialB.GameEventReceived += (_, _) => receivedGameEventsOnB++;
            transportB.MusicFollowStateChanged += (_, _) => musicFollowEventsOnB++;

            // 1) B가 집중 시작 (프레즌스 전파 전 in-flight 경쟁 상황 포함: A의 로컬 스냅샷이 아직 IsFocusing=false여도 전송 계층 ACK로 차단!)
            await transportB.UpdateLocalPresenceAsync(new LocalPresenceState(
                profileB.OwnerName, profileB.PetName, profileB.Appearance, profileB.SoloProgress.Level,
                IsMusicActive: true, AppVersion: "0.5.0", IsSleeping: true, IsFocusing: true));

            var msgResult = await socialA.SendBubbleMessageDetailedAsync(friendBForA, "집중 방해 테스트");
            Assert.False(msgResult.Succeeded);
            Assert.True(msgResult.IsPomodoroFocusBlocked);

            var reactResult = await socialA.SendQuickReactionDetailedAsync(friendBForA, "♥");
            Assert.False(reactResult.Succeeded);
            Assert.True(reactResult.IsPomodoroFocusBlocked);

            var petResult = await socialA.SendPetInteractionAsync(friendBForA);
            Assert.Equal(PetInteractionSendResult.PomodoroFocusBlocked, petResult);

            var snowballResult = await snowballsA.SendDetailedAsync(friendBForA);
            Assert.False(snowballResult.Succeeded);
            Assert.True(snowballResult.IsPomodoroFocusBlocked);
            Assert.Contains(FriendOperationErrorCodes.PomodoroFocusMessage, snowballRejectedMessages);

            var musicResult = await socialA.ToggleMusicFollowDetailedAsync(friendBForA);
            Assert.False(musicResult.Succeeded);
            Assert.True(musicResult.IsPomodoroFocusBlocked);
            Assert.Null(socialA.FollowingHostPetId);

            var gameResult = await socialA.SendGameEventDetailedAsync(friendBForA, new GameEventPayload
            {
                GameType = "rps",
                SessionId = Guid.NewGuid().ToString("N"),
                Action = "offer",
                WagerCoins = 30,
                InviteExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(20)
            });
            Assert.False(gameResult.Succeeded);
            Assert.True(gameResult.IsPomodoroFocusBlocked);

            // 게임 초대(가위바위보·타이핑·사다리): 피커 차단 및 직접 커맨드 우회 시에도 정확히 거절 문구 표시, 코인 차감/수신 없음
            gamesVmA.RefreshOnlineFriends();
            gamesVmA.SelectedRpsFriend = gamesVmA.OnlineFriends.Single();
            await gamesVmA.SendRpsInviteCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => !gamesVmA.IsRpsWaitingResponse && gamesVmA.RpsStatusText == FriendOperationErrorCodes.PomodoroFocusMessage);
            Assert.Equal(200, profileA.SoloProgress.Coins);
            Assert.Null(gamesVmB.IncomingInvite);

            gamesVmA.SelectedTypingFriend = gamesVmA.OnlineFriends.Single();
            await gamesVmA.SendTypingInviteCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => !gamesVmA.IsTypingWaitingResponse && gamesVmA.TypingStatusText == FriendOperationErrorCodes.PomodoroFocusMessage);
            Assert.Equal(200, profileA.SoloProgress.Coins);
            Assert.Null(gamesVmB.IncomingInvite);

            var ladderCandidate = gamesVmA.LadderInviteCandidates.Single();
            ladderCandidate.IsSelected = true;
            gamesVmA.ApplyCoinJackpotPresetCommand.Execute(null);
            await gamesVmA.OpenLadderRoomCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => !gamesVmA.IsLadderRoomOpen && gamesVmA.LadderStatusText == FriendOperationErrorCodes.PomodoroFocusMessage);
            Assert.Equal(200, profileA.SoloProgress.Coins);
            Assert.Null(gamesVmB.IncomingInvite);

            // 거절된 요청에 보상·친밀도·수신 연출이 전혀 발생하지 않아야 함
            await Task.Delay(120);
            Assert.Equal(200, profileA.SoloProgress.Coins);
            Assert.Equal(200, profileB.SoloProgress.Coins);
            Assert.Equal(1, socialA.Friends.Single(f => f.PetId == profileB.PetId).Intimacy);
            Assert.Equal(1, socialB.Friends.Single(f => f.PetId == profileA.PetId).Intimacy);
            Assert.Equal(0, receivedMessagesOnB);
            Assert.Equal(0, receivedStatusOnB);
            Assert.Equal(0, receivedReactionsOnB);
            Assert.Equal(0, receivedSnowballsOnB);
            Assert.Equal(0, receivedGameEventsOnB);
            Assert.Equal(0, musicFollowEventsOnB);

            // 2) B의 집중 상태(수면+음악 동시 활성)를 코디네이터로 동기화 -> A 오버레이에 ✏️ 우선 표시 + 정확 툴팁 검증
            await socialB.UpdateLocalPresenceAsync(profileB, isMusicActive: true, isSleeping: true, isFocusing: true);
            await WaitUntilAsync(() => socialA.IsPeerFocusing(profileB.PetId));

            petVmA.RefreshQuickOverlayEntries();
            socialVmA.RefreshLists();
            gamesVmA.RefreshOnlineFriends();
            await WpfTestHost.FlushAsync();

            var overlayEntry = Assert.Single(petVmA.QuickOverlayEntries);
            Assert.True(overlayEntry.IsFocusing);
            Assert.True(overlayEntry.IsSleeping);
            Assert.True(overlayEntry.IsMusicActive);
            Assert.True(overlayEntry.ShowsFocusIndicator);
            Assert.False(overlayEntry.ShowsSleepIndicator);
            Assert.False(overlayEntry.ShowsOnlineDot);
            Assert.Equal(FriendOperationErrorCodes.PomodoroFocusMessage, overlayEntry.StatusTooltip);
            Assert.False(overlayEntry.CanInteract);
            Assert.False(overlayEntry.CanFollowMusic);
            Assert.False(overlayEntry.CanToggleVisit);
            Assert.Equal(FriendOperationErrorCodes.PomodoroFocusMessage, overlayEntry.MessageTooltip);
            Assert.Equal(FriendOperationErrorCodes.PomodoroFocusMessage, overlayEntry.PetTooltip);
            Assert.Equal(FriendOperationErrorCodes.PomodoroFocusMessage, overlayEntry.MusicTooltip);
            Assert.Equal(FriendOperationErrorCodes.PomodoroFocusMessage, overlayEntry.VisitTooltip);
            Assert.NotNull(overlayEntry.Snowball);
            Assert.False(overlayEntry.Snowball!.Command.CanExecute(null));
            Assert.Equal(FriendOperationErrorCodes.PomodoroFocusMessage, overlayEntry.Snowball.Tooltip);

            // 우회 커맨드 실행 시 정확히 '뽀모도로 타이머로 집중 중!' 표시 & 방문 미호출
            var visitRequested = false;
            petVmA.ToggleVisitFriendRequested += (_, _) => visitRequested = true;
            petVmA.QuickToggleVisitFriendCommand.Execute(overlayEntry);
            Assert.False(visitRequested);
            Assert.Contains(petVmA.BubbleLines, l => l.Text == FriendOperationErrorCodes.PomodoroFocusMessage);

            await petVmA.QuickPetFriendCommand.ExecuteAsync(overlayEntry);
            Assert.Equal(FriendOperationErrorCodes.PomodoroFocusMessage, petVmA.BubbleLines[^1].Text);

            // FriendsPage ViewModel에서도 모든 상호작용 차단 및 문구 확인
            var friendVm = Assert.Single(socialVmA.Friends);
            Assert.True(friendVm.IsFocusing);
            Assert.True(friendVm.ShowsFocusBadge);
            Assert.False(friendVm.ShowsSleepBadge);
            Assert.False(friendVm.CanInteract);
            Assert.False(friendVm.CanFollowMusic);
            Assert.False(friendVm.CanToggleVisit);
            socialVmA.SelectedFriend = friendVm;
            socialVmA.MessageText = "안녕?";
            await socialVmA.SendBubbleMessageCommand.ExecuteAsync(null);
            Assert.Equal(FriendOperationErrorCodes.PomodoroFocusMessage, socialVmA.StatusBanner);

            // 게임 초대 피커에서 집중 중인 친구 선택 불가 및 직접 초대 차단
            var rpsCard = Assert.Single(gamesVmA.RpsFriendPicker.FilteredCandidates);
            Assert.True(rpsCard.IsFocusing);
            Assert.False(rpsCard.CanSelect);
            gamesVmA.RpsFriendPicker.ToggleCandidateCommand.Execute(rpsCard);
            Assert.False(rpsCard.IsSelected);
            gamesVmA.SelectedRpsFriend = gamesVmA.OnlineFriends.Single();
            await gamesVmA.SendRpsInviteCommand.ExecuteAsync(null);
            Assert.Equal(FriendOperationErrorCodes.PomodoroFocusMessage, gamesVmA.RpsStatusText);
            Assert.False(gamesVmA.IsRpsWaitingResponse);
            Assert.Null(gamesVmB.IncomingInvite);

            // 3) 양쪽 모두 집중 중일 때도 자동응답 루프 없음
            await socialA.SetLocalFocusStateAsync(true);
            var receivedMessagesOnA = 0;
            socialA.FriendMessageReceived += (_, _) => receivedMessagesOnA++;
            var mutualAtoB = await transportA.SendBubbleMessageDetailedAsync(friendBForA, "루프방지1");
            var mutualBtoA = await transportB.SendBubbleMessageDetailedAsync(friendAForB, "루프방지2");
            Assert.True(mutualAtoB.IsPomodoroFocusBlocked);
            Assert.True(mutualBtoA.IsPomodoroFocusBlocked);
            await Task.Delay(120);
            Assert.Equal(0, receivedMessagesOnA);
            Assert.Equal(0, receivedMessagesOnB);

            // 4) 양쪽 집중 해제 -> ✏️ 해제 및 수면/온라인 상태 복귀 + 오버레이 모든 상호작용 버튼 재활성화 + 정상 메시지 수신
            await socialA.SetLocalFocusStateAsync(false);
            await socialB.UpdateLocalPresenceAsync(profileB, isMusicActive: true, isSleeping: true, isFocusing: false);
            await WaitUntilAsync(() => !socialA.IsPeerFocusing(profileB.PetId));

            petVmA.RefreshQuickOverlayEntries();
            await WpfTestHost.FlushAsync();

            var restoredEntry = Assert.Single(petVmA.QuickOverlayEntries);
            Assert.False(restoredEntry.IsFocusing);
            Assert.False(restoredEntry.ShowsFocusIndicator);
            Assert.True(restoredEntry.ShowsSleepIndicator);
            Assert.Equal("자는 중", restoredEntry.StatusTooltip);
            Assert.True(restoredEntry.CanInteract);
            Assert.True(restoredEntry.CanFollowMusic);
            Assert.True(restoredEntry.CanToggleVisit);
            Assert.NotNull(restoredEntry.Snowball);
            Assert.True(restoredEntry.Snowball!.Command.CanExecute(null));
            Assert.Equal("눈던지기", restoredEntry.Snowball.Tooltip);

            var msgTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            socialB.FriendMessageReceived += (_, msg) => msgTcs.TrySetResult(msg.Text);
            var okAfterFocus = await socialA.SendBubbleMessageDetailedAsync(friendBForA, "집중 끝 수고했어!");
            Assert.True(okAfterFocus.Succeeded);
            Assert.Equal("집중 끝 수고했어!", await msgTcs.Task.WaitAsync(TimeSpan.FromSeconds(3)));

            socialVmA.Detach();
            await petVmA.DisposeAsync();
        });
    }

    [Fact]
    public async Task PomodoroFocus_TwoClientsGcp_SyncsFocusAcrossReconnect_AndBlocksInFlightControlPackets()
    {
        var serverPort = GetFreeTcpPort();
        var serverUrl = $"http://127.0.0.1:{serverPort}";
        var serverDir = Path.Combine(_tempRoot, "gcp-server");
        Directory.CreateDirectory(serverDir);

        await using var server = LabPetServerHost.BuildApplication(new ServerOptions
        {
            ListenUrl = serverUrl,
            ServerSecret = "pomodoro-focus-test-secret-key-2026-verify!",
            RequireConfiguredSecret = true,
            DataDirectory = serverDir
        });
        await server.StartAsync();

        try
        {
            var serverOpts = new ServerConnectionOptions { Enabled = true, ServerUrl = serverUrl };
            var idStoreA = new DeviceIdentityStore(Path.Combine(_tempRoot, "gcpA"));
            var idStoreB = new DeviceIdentityStore(Path.Combine(_tempRoot, "gcpB"));

            await using var clientA = new GcpSignalRTransport(serverOpts, idStoreA);
            await using var clientB = new GcpSignalRTransport(serverOpts, idStoreB);

            var profileA = new PetProfile { OwnerName = "민지", PetName = "구름A", FriendCode = "LP-5555-AAAA" };
            var profileB = new PetProfile { OwnerName = "하늘", PetName = "강아지B", FriendCode = "LP-6666-BBBB" };

            await clientA.StartAsync(profileA, []);
            await clientB.StartAsync(profileB, []);
            await WaitUntilAsync(() => clientA.IsRemoteConnected && clientB.IsRemoteConnected);

            // 친구 페어링 완료
            var reqTcs = new TaskCompletionSource<FriendRequestReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            clientB.FriendRequestReceived += (_, e) => reqTcs.TrySetResult(e);
            await WaitUntilAsync(() => clientA.GetDiscoveredPeers().Any(p => p.PetId == profileB.PetId));
            var peerB = clientA.GetDiscoveredPeers().Single(p => p.PetId == profileB.PetId);
            Assert.True(await clientA.SendFriendRequestAsync(peerB));
            var req = await reqTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var accTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            clientA.FriendAccepted += (_, e) => accTcs.TrySetResult(e.PairSecretBase64);
            var acceptRes = await clientB.RespondFriendRequestDetailedAsync(req.RequestId, accept: true);
            Assert.True(acceptRes.Succeeded);
            var secret = await accTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var friendB = new FriendEntry { PetId = profileB.PetId, OwnerName = profileB.OwnerName, PetName = profileB.PetName, PairSecretBase64 = secret };
            var friendA = new FriendEntry { PetId = profileA.PetId, OwnerName = profileA.OwnerName, PetName = profileA.PetName, PairSecretBase64 = secret };
            clientA.UpdateKnownFriends([friendB]);
            clientB.UpdateKnownFriends([friendA]);

            // B가 뽀모도로 집중 진입 -> A가 GCP 실시간 프레즌스로 IsFocusing=true 수신
            await clientB.UpdateLocalPresenceAsync(new LocalPresenceState(
                profileB.OwnerName, profileB.PetName, profileB.Appearance, 1,
                IsMusicActive: false, AppVersion: "0.5.0", IsSleeping: false, FriendCode: profileB.FriendCode, IsFocusing: true));

            await WaitUntilAsync(() => clientA.GetDiscoveredPeers().Any(p => p.PetId == profileB.PetId && p.IsFocusing));

            // GCP 중계로 직접 제어 패킷 전송 시도 -> 서명 ACK POMODORO_FOCUS 거절, B 이벤트 미발생
            var receivedOnB = 0;
            clientB.BubbleMessageReceived += (_, _) => receivedOnB++;
            clientB.PetInteractionReceived += (_, _) => receivedOnB++;
            clientB.SnowballReceived += (_, _) => receivedOnB++;
            clientB.GameEventReceived += (_, _) => receivedOnB++;

            var gcpMsg = await clientA.SendBubbleMessageDetailedAsync(friendB, "GCP 방해 테스트");
            Assert.False(gcpMsg.Succeeded);
            Assert.True(gcpMsg.IsPomodoroFocusBlocked);

            var gcpPet = await clientA.SendPetInteractionDetailedAsync(friendB, PetInteractionPayload.PetType);
            Assert.False(gcpPet.Succeeded);
            Assert.True(gcpPet.IsPomodoroFocusBlocked);

            var gcpSnow = await clientA.SendSnowballDetailedAsync(friendB, Guid.NewGuid());
            Assert.False(gcpSnow.Succeeded);
            Assert.True(gcpSnow.IsPomodoroFocusBlocked);

            Assert.Equal(0, receivedOnB);

            // A가 재연결해도 B의 집중 상태(IsFocusing=true)를 즉시 다시 수신해야 함
            await clientA.StopAsync();
            await clientA.StartAsync(profileA, [friendB]);
            await WaitUntilAsync(() => clientA.IsRemoteConnected
                && clientA.GetDiscoveredPeers().Any(p => p.PetId == profileB.PetId && p.IsFocusing));

            // B가 집중 종료 -> A에게 IsFocusing=false 동기화되고 메시지 정상 수신
            await clientB.UpdateLocalPresenceAsync(new LocalPresenceState(
                profileB.OwnerName, profileB.PetName, profileB.Appearance, 1,
                IsMusicActive: false, AppVersion: "0.5.0", IsSleeping: false, FriendCode: profileB.FriendCode, IsFocusing: false));

            await WaitUntilAsync(() => clientA.GetDiscoveredPeers().Any(p => p.PetId == profileB.PetId && !p.IsFocusing));

            var deliveredTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            clientB.BubbleMessageReceived += (_, e) => deliveredTcs.TrySetResult(e.Text);
            var afterFocus = await clientA.SendBubbleMessageDetailedAsync(friendB, "GCP 집중 완료!");
            Assert.True(afterFocus.Succeeded);
            Assert.Equal("GCP 집중 완료!", await deliveredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task PomodoroOverlayIndicator_RedDotDisappearsPermanentlyAfterClick_AndMatchesPomodoroProgress()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var dataDir = Path.Combine(_tempRoot, "badge-persist");
            var store = new JsonAppDataStore(new StateMigrator(), dataDir);
            var initialProfile = new PetProfile
            {
                PetName = "모찌",
                HasSeenPomodoroFeatureBadge = false
            };
            await store.SaveProfileAsync(initialProfile);

            await using var transport = new LanP2pTransport(udpDiscoveryPort: 0, udpMusicPort: 0, tcpControlPort: 0);
            await using var social = new SocialCoordinatorService(transport, store, new SoloProgressionService());
            var pomodoro = new PomodoroTimerService();

            try
            {
                // 1) 업데이트 직후 최초 실행: 오버레이 원형 인디케이터와 우클릭 메뉴 뽀모도로 항목 모두 빨간 점 표시
                var petVm1 = new PetWindowViewModel(store, social, snowballs: null, pomodoro: pomodoro);
                petVm1.ApplyProfile(await store.LoadProfileAsync());
                var petMenu1 = PetContextMenuFactory.Create(petVm1, includeStartupAutoRun: false);
                var trayMenu1 = PetContextMenuFactory.Create(petVm1, includeStartupAutoRun: true);

                static Ellipse FindPomodoroMenuRedDot(ContextMenu menu)
                {
                    var item = menu.Items.OfType<MenuItem>().Single(m =>
                        m.Header is StackPanel sp
                        && sp.Children.OfType<TextBlock>().Any(tb => tb.Text.Contains("뽀모도로", StringComparison.Ordinal)));
                    return ((StackPanel)item.Header).Children.OfType<Ellipse>().Single(e => e.Name == "PomodoroMenuRedDot");
                }

                var petMenuDot1 = FindPomodoroMenuRedDot(petMenu1);
                var trayMenuDot1 = FindPomodoroMenuRedDot(trayMenu1);

                Assert.True(petVm1.ShowPomodoroFeatureBadge);
                Assert.Equal(Visibility.Visible, petMenuDot1.Visibility);
                Assert.Equal(Visibility.Visible, trayMenuDot1.Visibility);

                // 원형 인디케이터 진행률이 뽀모도로 UI와 동일하게 연동되는지 확인
                pomodoro.ConfigureDurations(30, 6);
                Assert.Equal(0.5, petVm1.PomodoroOverlayProgressRatio, 3);
                Assert.NotEqual(System.Windows.Media.Geometry.Empty, petVm1.PomodoroOverlayArcGeometry);

                pomodoro.StartOrPause();
                Assert.Equal(1.0, petVm1.PomodoroOverlayProgressRatio, 3);
                Assert.Contains("집중 중", petVm1.PomodoroOverlayTooltip);

                pomodoro.CompleteCurrentPhaseForTest();
                Assert.Contains("휴식", petVm1.PomodoroOverlayTooltip);

                pomodoro.Reset();
                Assert.Equal(0.5, petVm1.PomodoroOverlayProgressRatio, 3);

                // 2) 오버레이 원형 인디케이터(OpenPomodoroCommand) 클릭 시 뽀모도로 창 열림 + 양쪽 빨간 점 즉시 소멸
                var navigated = new List<NavigationRequest>();
                petVm1.HubNavigationRequested += (_, req) => navigated.Add(req);
                petVm1.OpenPomodoroCommand.Execute(null);
                await WpfTestHost.FlushAsync();

                Assert.Contains(navigated, r => r.Page == HubPage.Pomodoro);
                Assert.False(petVm1.ShowPomodoroFeatureBadge);
                Assert.True(petVm1.CurrentProfile.HasSeenPomodoroFeatureBadge);
                Assert.Equal(Visibility.Collapsed, petMenuDot1.Visibility);
                Assert.Equal(Visibility.Collapsed, trayMenuDot1.Visibility);

                // 저장 완료 대기(DisposeAsync가 진행 중 프로필 저장을 드레인함)
                await petVm1.DisposeAsync();

                // 3) 앱 재시작 시뮬레이션: 저장된 profile.json 재로드 후 빨간 점이 다시 생기지 않아야 함
                var reloadedProfile = await store.LoadProfileAsync();
                Assert.True(reloadedProfile.HasSeenPomodoroFeatureBadge);

                var petVm2 = new PetWindowViewModel(store, social, snowballs: null, pomodoro: pomodoro);
                petVm2.ApplyProfile(reloadedProfile);
                var petMenu2 = PetContextMenuFactory.Create(petVm2, includeStartupAutoRun: false);
                var petMenuDot2 = FindPomodoroMenuRedDot(petMenu2);

                Assert.False(petVm2.ShowPomodoroFeatureBadge);
                Assert.Equal(Visibility.Collapsed, petMenuDot2.Visibility);

                await petVm2.DisposeAsync();
            }
            finally
            {
                pomodoro.Reset();
            }
        });
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 4000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(predicate(), "지정된 시간 내에 기대 상태에 도달하지 못했습니다.");
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class StubAudioAnalyzer : IAudioAnalyzer
    {
        public void Start() { }
        public void Stop() { }
        public AudioFrameSnapshot GetCurrentSnapshot() => new(0f, 0f, 0f, false, false, true, "Test Device");
        public void Dispose() { }
    }
}
