---
title: "서버리스 LAN P2P 소셜 기능명세"
doc_id: SPEC-FT-03
version: "0.2.2"
baseline_commit: "672f24a"
updated: "2026-11-23"
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
- **빠른 메시지 입력창**은 선택한 온라인 친구의 행 아래에 표시된다. 입력칸에 키보드 포커스가 살아있는 동안에는 포인터가 영역 밖으로 나가도 유지되며, 전송 중 새로 입력한 문장은 보존한다. 발신 화면은 전송 실패를 알린다. [펫 창 ViewModel](../../src/LabPet.Client/ViewModels/PetWindowViewModel.cs), [친구 창 ViewModel](../../src/LabPet.Client/ViewModels/SocialViewModel.cs)
- **받은 친구 메시지**에는 본문, 발신자 이름, 펫 외형이 전달된다. 탐색 정보에 외형이 있으면 이를 사용하고, 그렇지 않으면 친구 목록에 저장된 외형을 사용한다. [소셜 코디네이터](../../src/LabPet.Client/Social/SocialCoordinatorService.cs), [펫 창 XAML](../../src/LabPet.Client/Views/PetWindow.xaml)
- **구현 파일**: [LanP2pTransport.cs](../../src/LabPet.Client/Network/LanP2pTransport.cs), [SocialCoordinatorService.cs](../../src/LabPet.Client/Social/SocialCoordinatorService.cs), [SocialWindow.xaml.cs](../../src/LabPet.Client/Views/SocialWindow.xaml.cs), [VisitorPetWindow.xaml.cs](../../src/LabPet.Client/Views/VisitorPetWindow.xaml.cs), [PetWindow.xaml.cs](../../src/LabPet.Client/Views/PetWindow.xaml.cs), [CoinGamesViewModel.cs](../../src/LabPet.Client/ViewModels/CoinGamesViewModel.cs)
- **동작 규칙**:
  - 친구 신청의 수락 버튼을 누르면 TCP `45680`에서 P-256 ECDH 키 합의를 자동으로 수행한다. 수락의 키 확인 서명과 ACK가 검증되면 키를 DPAPI로 보호해 저장한다. [친구 등록](../../src/LabPet.Client/Network/LanP2pTransport.Pairing.cs)
  - 친구 전용 제어 패킷(`BubbleMessage`, `QuickReaction`, `PetInteraction`, `MusicFollowOn/Off`, `GameEvent`)은 프로토콜 v3에서 `AES-256-GCM`(HKDF-SHA256 파생 키 + 헤더 AAD) 암호화 전송과 `HMACSHA256` 서명 검증을 모두 필수로 적용하며(미암호화 친구 패킷은 `ENCRYPTION_REQUIRED`로 차단), UDP `45679` 오디오 프레임도 `HMACSHA256` 서명 검증으로 비친구/위변조를 차단
  - 다중 네트워크 어댑터 환경에서 TCP 수신 시 검증된 친구의 실제 발신 IP(`remoteIp`)로 탐색 캐시를 즉시 동기화하고, 제어 패킷 송신 실패 시 `LastKnownIp` 보조 주소로 1회 폴백 재시도
  - 노래 따라하기 구독 시 호스트가 구독 중인 친구 IP 목록에만 30Hz 유니캐스트 송신, 곡 간 전환·잠시 일시정지 중에는 팔로우를 유지하며 15초 패킷 미수신 또는 3분 완전 무음 시에만 팔로워 자동 로컬 모드 복귀(미재생·재생 중·따라하기 중 3단계 버튼 시각 구분 및 호스트 무음 중 로컬 음악 우선 옵션 지원)
  - 친구 3D 외형 360도 프리뷰 및 내 바탕화면 옆에 친구 펫을 띄우는 `VisitorPetWindow`(내 펫과 크기·투명도 실시간 공유, 호버 시 우측 상단 `✕` 돌려보내기 버튼 제공)
  - **수면(`zzz`) 상태 동기화**: `DiscoveryHelloPacket`, `FriendPresencePayload`, `PeerSnapshot`, `LocalPresenceState`에 선택 필드 `IsSleeping`(기본 `false`로 하위 호환 유지)을 포함하고, [LanP2pTransport.cs](../../src/LabPet.Client/Network/LanP2pTransport.cs)는 로컬 `IsSleeping`/`IsMusicActive` 변경 시 온라인 친구에게 즉시 `PresenceSync`와 탐색 `HELLO`를 푸시한다. 친구가 5분 무입력으로 수면 상태에 들어가면 [친구 창](../../src/LabPet.Client/Views/SocialWindow.xaml), 펫 호버 친구 오버레이([PetWindow.xaml](../../src/LabPet.Client/Views/PetWindow.xaml)) 상태에 `zzz`가 표시되며, 바탕화면 방문 친구 펫([VisitorPetWindow.xaml.cs](../../src/LabPet.Client/Views/VisitorPetWindow.xaml.cs)) 및 친구 3D 미리보기([SocialWindow.xaml.cs](../../src/LabPet.Client/Views/SocialWindow.xaml.cs))도 함께 자는 모션을 취하고 입력 재개 시 깜짝 기상 모션을 재생한다. [수면 프레즌스 검증](../../tests/LabPet.Tests/SocialCoordinatorTests.cs)
  - **GCP 실시간 서버 및 하이브리드 인터넷 친구 연동**: [LabPet.Server](../../src/LabPet.Server/Program.cs), [GcpSignalRTransport.cs](../../src/LabPet.Client/Network/GcpSignalRTransport.cs), [HybridTransportRouter.cs](../../src/LabPet.Client/Network/HybridTransportRouter.cs)를 통해 외부 네트워크에서도 기존 `주변 펫` 탭과 동일한 방식의 온라인 원격 펫 탐색(비친구는 음악·수면 상태 비공개) 및 `친구 신청`/수락(P-256 ECDH), E2EE 메시지·반응·쓰다듬기·코인 게임·원격 노래 따라하기(구독자 있을 때만 스로틀링 중계)를 지원한다. 채팅·오프라인 메시지는 저장하지 않으며 LAN 온라인 친구는 항상 LAN P2P를 우선 사용한다. [GCP 통합 검증](../../tests/LabPet.Tests/GcpServerAndHybridTests.cs), [GCP 배포 가이드](../../deploy/gcp/README.md)
  - `PacketType.GameEvent` 기반 친구 LAN 가위바위보 배틀, 10초 타이핑 스프린트(3초 카운트다운·자동 다음 문장 넘김), 연구실 공동 사다리타기(온라인 친구 직접 초대 선택 → 상품·무료/유료 공개 초대 → 참가자 확정 → 중복 없는 시작 위치 선택 완료 → CSPRNG Fisher–Yates 불변 사다리판 동시 하강·도착 시 `"?"` 결과 공개 · 창 닫기 시 진행률 보존).
  - 게임 초대는 `GameInviteStatus`(`Pending`/`Accepting`/`Accepted`/`Declined`/`Expired`, 20초 제한) 상태 머신으로 중복 클릭과 취소·만료 초대 수락을 차단한다. 게임 창이 닫혀 있으면 펫 주변 경량 비활성화 창([GameInviteCardWindow.xaml](../../src/LabPet.Client/Views/GameInviteCardWindow.xaml))에 친구 펫 초상·게임·참가비·상품 요약·남은 시간을 표시해 클릭 투과 집중 모드 중에도 포커스 탈취 없이 수락/거절할 수 있고, 수락 시 대시보드를 거치지 않고 해당 게임 화면으로 직행한다. 게임 창이 열린 상태에서는 현재 화면을 방해하지 않고 상단 슬림 배너에 표시하며, 진행 중인 다른 게임에 새 초대가 끼어들지 않는다.
  - 가위바위보는 확정·공개(양측 공개 전 상대 패 비노출), 타이핑은 문장 기록 검증, 사다리는 참가자 확정·위치 선택·가로줄/상품/결과 일관성 검증 및 이탈/실패 시 유료 참가비 1회 환불을 적용한다. P2P 방식의 신뢰 한계는 보안 명세에 기록한다. [LAN 게임 보안](../03_네트워크_프로토콜_및_보안명세.md)

---
[← 이전: 08-2 오디오 반응 및 개인 성장](./08-2_오디오반응_및_개인성장.md) | [개요](./08-0_기능명세서_개요.md)
