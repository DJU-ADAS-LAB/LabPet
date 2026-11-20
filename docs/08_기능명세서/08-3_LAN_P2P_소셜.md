---
title: "서버리스 LAN P2P 소셜 기능명세"
doc_id: SPEC-FT-03
version: "0.3.0"
baseline_commit: "49d7700"
updated: "2026-11-20"
status: "활성"
tags:
  - 기능명세서
  - Phase3
  - LAN_P2P
  - HMAC
related:
  - "./08-0_기능명세서_개요.md"
  - "../03_네트워크_프로토콜_및_보안명세.md"
  - "../05_변경이력/2026-11-20.md"
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
- **빠른 메시지 입력창**은 선택한 온라인 친구의 행 아래에 표시된다. 포인터가 입력 영역에 있는 동안 이어서 전송할 수 있고, 전송 중 새로 입력한 문장은 보존한다. 발신 화면은 전송 실패를 알린다. [펫 창 ViewModel](../../src/LabPet.Client/ViewModels/PetWindowViewModel.cs), [친구 창 ViewModel](../../src/LabPet.Client/ViewModels/SocialViewModel.cs)
- **받은 친구 메시지**에는 본문, 발신자 이름, 펫 외형이 전달된다. 탐색 정보에 외형이 있으면 이를 사용하고, 그렇지 않으면 친구 목록에 저장된 외형을 사용한다. [소셜 코디네이터](../../src/LabPet.Client/Social/SocialCoordinatorService.cs), [펫 창 XAML](../../src/LabPet.Client/Views/PetWindow.xaml)
- **구현 파일**: [LanP2pTransport.cs](../../src/LabPet.Client/Network/LanP2pTransport.cs), [SocialCoordinatorService.cs](../../src/LabPet.Client/Social/SocialCoordinatorService.cs), [SocialWindow.xaml.cs](../../src/LabPet.Client/Views/SocialWindow.xaml.cs), [PetWindow.xaml.cs](../../src/LabPet.Client/Views/PetWindow.xaml.cs)
- **동작 규칙**:
  - TCP `45680`으로 친구 신청(`FriendRequest`) → 상대 펫 위 `[수락 / 거절]` 말풍선 표시 → 수락 시 32바이트 `pairSecret` 생성·회신 및 `friends.json` 저장
  - 친구 전용 제어 패킷 및 UDP `45679` 오디오 프레임은 `HMACSHA256` 서명 검증으로 비친구/위변조 차단
  - 노래 따라하기 구독 시 호스트가 구독 중인 친구 IP 목록에만 30Hz 유니캐스트 송신, 곡 간 전환·잠시 일시정지 중에는 팔로우를 유지하며 15초 패킷 미수신 또는 3분 완전 무음 시에만 팔로워 자동 로컬 모드 복귀(호스트 무음 중 로컬 음악 우선 옵션 지원)

---
[← 이전: 08-2 오디오 반응 및 개인 성장](./08-2_오디오반응_및_개인성장.md) | [개요](./08-0_기능명세서_개요.md)
