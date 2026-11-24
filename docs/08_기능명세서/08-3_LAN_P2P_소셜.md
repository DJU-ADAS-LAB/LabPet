---
title: "서버리스 LAN P2P 소셜 기능명세"
doc_id: SPEC-FT-03
version: "0.3.8"
baseline_commit: "ca491ef"
updated: "2026-11-24"
status: "활성"
tags:
  - 기능명세서
  - Phase3
  - LAN_P2P
  - HMAC
related:
  - "./08-0_기능명세서_개요.md"
  - "../03_네트워크_프로토콜_및_보안명세.md"
  - "../05_변경이력/2026-11-23.md"
  - "../05_변경이력/2026-11-24.md"
---

# 08-3. 서버리스 LAN P2P 소셜 기능명세

## FT-09: 물리 어댑터 필터링 UDP 탐색 및 `protocolVersion` 호환 배지
- **구현 파일**: [LanNetworkHelper.cs](../../src/LabPet.Client/Network/LanNetworkHelper.cs), [LanP2pTransport.cs](../../src/LabPet.Client/Network/LanP2pTransport.cs), [SocialViewModel.cs](../../src/LabPet.Client/ViewModels/SocialViewModel.cs)
- **동작 규칙**:
  - VPN·Docker·WSL·Hyper-V 등 가상 어댑터를 제외하고 활성 물리 LAN/Wi-Fi 어댑터의 서브넷 브로드캐스트 주소로 3초마다 UDP `45678` `HELLO` 송신, 10초 무응답 시 오프라인 처리
  - 영구 `petId` 키로 피어·친구 IP/포트 자동 갱신
  - 프로토콜 호환 범위를 벗어난 피어는 연결을 차단하고 UI에 `업데이트 필요` 배지 및 툴팁 표시

## FT-10: TCP 친구 신청/인증, 말풍선 메시지, 30Hz 노래 따라하기
- **동시 갱신** 시에도 화면에서는 친구 정보의 복사본을 읽는다. 코디네이터는 친구 등록과 상호작용 보상 저장을 순서대로 처리하고, 파일 쓰기에 실패하면 펫 말풍선으로 알린다. [소셜 코디네이터 회귀 테스트](../../tests/LabPet.Tests/SocialCoordinatorTests.cs)
- **빠른 메시지 입력창**은 선택한 온라인 친구의 행 아래에 표시된다. 친구 오버레이와 방문 펫의 입력창은 입력칸 또는 버튼에 포커스, 포인터, 마우스 캡처가 남아 있으면 유지된다. 전송 버튼을 눌러도 같은 입력창에서 이어서 작성할 수 있으며, 전송 중 새로 입력한 문장은 보존한다. 발신 화면은 전송 실패를 알린다. 방문 펫 입력창은 한 번에 하나만 열리며, 다른 방문 펫을 누르면 이전 창을 닫고 새 창을 연다. 캐릭터 머리 위가 막혀 있으면 옆이나 아래 공간으로 배치한다. [펫 창 ViewModel](../../src/LabPet.Client/ViewModels/PetWindowViewModel.cs), [친구 창 ViewModel](../../src/LabPet.Client/ViewModels/SocialViewModel.cs)
- **인사하기와 빠른 반응 알림**은 해당 친구의 방문 펫 위에 우선 표시하고, 방문 펫이 없으면 내 펫 위에 표시한다(UI 문구는 모두 `"인사하기"`로 표시하고 프로토콜 와이어 토큰 `PacketType.PetInteraction` `"pet"`은 하위 호환을 위해 유지한다). 보내는 인사하기는 호버 오버레이·방문 펫 우클릭·통합 친구 창에서 같은 1초 간격을 공유한다. 친구별 **친밀도는 `0~9999` 상한**으로 관리되며 최대치 도달 시 `"친밀도 MAX"`와 정확한 수치 툴팁을 표시하고, 일일 소셜 코인 상한(10코인) 도달 후에도 인사하기·메시지·빠른 반응 및 친밀도 증가(상한 미만일 때)는 계속 허용된다. [소셜 코디네이터](../../src/LabPet.Client/Social/SocialCoordinatorService.cs), [상태 알림 라우터](../../src/LabPet.Client/ViewModels/FriendMessageRouter.cs), [경제 서비스](../../src/LabPet.Client/Solo/PetEconomyService.cs)
- **받은 친구 메시지**에는 `SenderPetId`, `MessageId`, 발신자 이름, 본문, 펫 외형이 전달되며 [App.xaml.cs](../../src/LabPet.Client/App.xaml.cs)의 [FriendMessageRouter.cs](../../src/LabPet.Client/ViewModels/FriendMessageRouter.cs) 단일 경로로 라우팅한다. 발신자의 방문 펫(`VisitorPetWindow`)이 열려 있으면 해당 방문 펫 상단 전용 `Popup` 말풍선에만 표시하고, 방문 중이 아닐 때만 내 `PetWindow`에 표시하며, 양쪽 말풍선 모두 [BubbleMessageStack.cs](../../src/LabPet.Client/ViewModels/BubbleMessageStack.cs)를 통해 최대 5개·개별 만료·중복 차단 규칙을 따른다. [소셜 코디네이터](../../src/LabPet.Client/Social/SocialCoordinatorService.cs), [방문 펫 말풍선 검증](../../tests/LabPet.Tests/VisitorPetBubbleTests.cs), [펫 창 XAML](../../src/LabPet.Client/Views/PetWindow.xaml)
- **구현 파일**: [LanP2pTransport.cs](../../src/LabPet.Client/Network/LanP2pTransport.cs), [SocialCoordinatorService.cs](../../src/LabPet.Client/Social/SocialCoordinatorService.cs), [FriendsPageView.xaml.cs](../../src/LabPet.Client/Views/Hub/Pages/FriendsPageView.xaml.cs), [VisitorPetWindow.xaml.cs](../../src/LabPet.Client/Views/VisitorPetWindow.xaml.cs), [PetWindow.xaml.cs](../../src/LabPet.Client/Views/PetWindow.xaml.cs), [CoinGamesViewModel.cs](../../src/LabPet.Client/ViewModels/CoinGamesViewModel.cs)
- **동작 규칙**:
  - 친구 신청의 수락 버튼을 누르면 TCP `45680`에서 P-256 ECDH 키 합의를 자동으로 수행한다. 수락의 키 확인 서명과 ACK가 검증되면 키를 DPAPI로 보호해 저장한다. [친구 등록](../../src/LabPet.Client/Network/LanP2pTransport.Pairing.cs)
  - 친구 전용 제어 패킷(`BubbleMessage`, `QuickReaction`, `PetInteraction`, `MusicFollowOn/Off`, `GameEvent`)은 프로토콜 v3에서 `AES-256-GCM`(HKDF-SHA256 파생 키 + 헤더 AAD) 암호화 전송과 `HMACSHA256` 서명 검증을 모두 필수로 적용하며(미암호화 친구 패킷은 `ENCRYPTION_REQUIRED`로 차단), UDP `45679` 오디오 프레임도 `HMACSHA256` 서명 검증으로 비친구/위변조를 차단
  - 다중 네트워크 어댑터 환경에서 TCP 수신 시 검증된 친구의 실제 발신 IP(`remoteIp`)로 탐색 캐시를 즉시 동기화하고, 제어 패킷 송신 실패 시 `LastKnownIp` 보조 주소로 1회 폴백 재시도
  - 노래 따라하기 구독 시 호스트가 구독 중인 친구 IP 목록에만 30Hz 유니캐스트 송신, 곡 간 전환·잠시 일시정지 중에는 팔로우를 유지하며 15초 패킷 미수신 또는 3분 완전 무음 시에만 팔로워 자동 로컬 모드 복귀(미재생·재생 중·따라하기 중 3단계 버튼 시각 구분 및 호스트 무음 중 로컬 음악 우선 옵션 지원)
  - 친구 3D 외형 360도 프리뷰 및 내 바탕화면 옆에 친구 펫을 띄우는 `VisitorPetWindow`(내 펫과 크기·투명도 실시간 공유, 좌클릭 시 해당 친구에게 바로 메시지를 보내는 빠른 채팅 입력창 `VisitorChatComposerPopup` 표시, 호버 시 3D 캐릭터 근처 오른쪽 위 `✕` 돌려보내기 버튼·우클릭 `👋 인사하기 보내기` 제공, 채팅 메시지 최대 5개 상단 `Popup` 표시 및 시스템 알림 분리, 채팅창·수신 말풍선 `CalculateStackedBubblePlacement` 비겹침 배치 및 다중 모니터 경계·다중 방문 펫 말풍선 충돌 회피·수신 말풍선 꼬리의 펫 중심 정렬) [방문 펫 배치 계산기](../../src/LabPet.Client/Views/VisitorBubblePlacementCalculator.cs)
  - **수면(`zzz`) 상태 동기화**: `DiscoveryHelloPacket`, `FriendPresencePayload`, `PeerSnapshot`, `LocalPresenceState`에 선택 필드 `IsSleeping`(기본 `false`로 하위 호환 유지)을 포함하고, [LanP2pTransport.cs](../../src/LabPet.Client/Network/LanP2pTransport.cs)는 로컬 `IsSleeping`/`IsMusicActive` 변경 시 온라인 친구에게 즉시 `PresenceSync`와 탐색 `HELLO`를 푸시한다. 친구가 수면 중이거나 오프라인이 되면 [친구 페이지](../../src/LabPet.Client/Views/Hub/Pages/FriendsPageView.xaml) 목록에 `zzz`가 표시된다. 온라인 상태인 수면 친구는 펫 호버 친구 오버레이([PetWindow.xaml](../../src/LabPet.Client/Views/PetWindow.xaml)) 왼쪽 고정 상태 슬롯에는 글자가 차례로 떠오르는 미니 `zzz` 애니메이션([MiniSleepZzzIndicator.cs](../../src/LabPet.Client/Views/MiniSleepZzzIndicator.cs))이 재생된다. 바탕화면 방문 친구 펫([VisitorPetWindow.xaml.cs](../../src/LabPet.Client/Views/VisitorPetWindow.xaml.cs)) 및 친구 3D 미리보기([FriendsPageView.xaml.cs](../../src/LabPet.Client/Views/Hub/Pages/FriendsPageView.xaml.cs))도 함께 자는 모션을 취하고 내 펫과 같은 `zzz`를 오른쪽 위에 표시한다. 깨어 있는 온라인 상태가 확인되면 기상 모션을 재생한다. 호버 친구 오버레이에는 접속 중인 등록 친구만 표시한다. [수면 프레즌스 검증](../../tests/LabPet.Tests/SocialCoordinatorTests.cs)
  - **GCP 실시간 서버 및 하이브리드 인터넷 친구 연동**: [LabPet.Server](../../src/LabPet.Server/Program.cs), [GcpSignalRTransport.cs](../../src/LabPet.Client/Network/GcpSignalRTransport.cs), [HybridTransportRouter.cs](../../src/LabPet.Client/Network/HybridTransportRouter.cs)를 통해 외부 네트워크에서도 기존 `주변 펫` 탭과 동일한 방식의 온라인 원격 펫 탐색(비친구는 음악·수면 상태 비공개) 및 `친구 신청`/수락(P-256 ECDH), E2EE 메시지·반응·인사하기·코인 게임·원격 노래 따라하기(구독자 있을 때만 스로틀링 중계)를 지원한다. 채팅·오프라인 메시지는 저장하지 않으며 LAN 온라인 친구는 항상 LAN P2P를 우선 사용한다. [GCP 통합 검증](../../tests/LabPet.Tests/GcpServerAndHybridTests.cs), [GCP 배포 가이드](../../deploy/gcp/README.md)
  - `PacketType.GameEvent` 기반 친구 LAN 가위바위보 배틀, 10초 타이핑 스프린트(3초 카운트다운·자동 다음 문장 넘김), 연구실 단체 사다리타기(공용 `FriendInvitePicker` 다중 초대 선택 → 구조화 상품/프리셋 설정 → 9종 상태 대기실[`방장`/`전송 중`/`수락 대기`/`참가 완료`/`거절`/`코인 부족`/`시간 초과`/`연결 끊김`/`초대 취소`] 및 4종 제한 시간[`초대 응답 20초`/`방장 확정 60초`/`위치 선택 30초`/`재연결 유예 10초`]·인원 불일치 시 방장 재조정 및 참가자 동의(`agree-prizes`)·참가자 확정·중복 없는 시작 위치 선택 완료 → CSPRNG Fisher–Yates 불변 사다리판 동시 하강·도착 시 `"?"` 결과 공개 · 게임 시작 이후 취소 차단).
  - 게임 초대는 공용 `FriendInvitePicker`에서 상대를 선택해 발송하고, 1:1 게임은 `OneToOneInviteWaitView` 전용 대기 화면을 표시한다. `GameInviteStatus` 상태 머신에서 발신자 20초 UTC 만료 시각을 공유해 등록·수락·준비·시작 확인을 처리한다. **코인 부족 초대 수신** 시에도 통합 창 상단 초대 배너(`HubWindow.xaml`)와 데스크톱 초대 카드(`GameInviteCardWindow.xaml`)를 숨기지 않고 필요 코인·보유 코인·부족 코인·남은 시간·충족 여부를 표시하며, 수락 버튼만 비활성화한 뒤 코인이 충족되면 실시간으로 해제하고 수락 시 재검증한다(무료 게임은 검사 생략). 거절·부족 응답에는 잔액 숫자를 노출하지 않고 `"coins"` 사유 토큰만 전송한다.
  - 가위바위보·타이핑은 보류 참가비와 양쪽 준비가 확인된 뒤 시작하며, 단체 사다리는 방장 개별/전체 취소, 참가자 자진 퇴장, 2인 미만 자동 전체 취소, 확정 후 이탈/위치 선택 시간 초과 시 전체 취소·환불, 중복 취소/중복 환불/지연 수락/재연결 불일치 방지(`_abortedLadderSessions`)를 적용한다. [초대 회귀 테스트](../../tests/LabPet.Tests/GameInviteTransportTests.cs), [사다리·초대 UI 테스트](../../tests/LabPet.Tests/FriendInviteAndLadderTests.cs)
  - 가위바위보는 확정·공개(양측 공개 전 상대 패 비노출), 타이핑은 문장 기록 검증, 사다리는 참가자 확정·위치 선택·가로줄/상품/결과 일관성 검증 및 이탈/실패 시 유료 참가비 1회 환불을 적용한다. P2P 방식의 신뢰 한계는 보안 명세에 기록한다. [LAN 게임 보안](../03_네트워크_프로토콜_및_보안명세.md)

---
[← 이전: 08-2 오디오 반응 및 개인 성장](./08-2_오디오반응_및_개인성장.md) | [개요](./08-0_기능명세서_개요.md)
