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
  - "../05_변경이력/2026-11-25.md"
---

# 08-3. 서버리스 LAN P2P 소셜 기능명세

## 눈던지기 0.3.13

- 친구 퀵 오버레이, 통합 창 친구 상세, 방문 펫 우클릭 메뉴의 명령이 같은 대상별 1초 상태를 사용한다. 시간은 단조 시계로 계산한다. 전송 경로가 없거나 상대 앱이 0.3.13 미만이면 상태를 알린다.
- 기존 인증·서명·암호화 `PetInteraction`을 통해서만 수신 장면을 시작한다. LAN과 GCP에 동일한 이벤트 ID를 보내며 수신자는 ID 중복 제거와 발신자별 초당 1건 보충, 최대 2건 순간 허용을 적용한다. 미확인 형식은 인사·쓰다듬기로 처리하지 않는다. 보상 적립과 오프라인 저장은 없다.
- WPF 창은 내 펫이 있는 모니터에 열고 방문 펫 리그 또는 발신자 외형의 임시 리그를 쓴다. 내 펫의 이동을 비행 목표에 반영하고 대상 숨김·종료·모니터 변경에 장면을 정리한다.
- LAN과 로컬 SignalR 중계에서 전달, 중복 제거, 구버전 LAN 및 미확인 유형, 보상 미적립을 검사한다. 실기기 혼합 DPI·운영 GCP·CPU/GPU·메모리는 별도 측정이 필요하다. GCP 서버 코드는 기존 암호문 중계를 사용한다.
- [공유 버튼 상태 및 전송](../../src/LabPet.Client/Social/SnowballInteractionService.cs), [혼합 전송](../../src/LabPet.Client/Network/HybridTransportRouter.cs), [데스크톱 장면](../../src/LabPet.Client/Views/SnowballDesktopScene.cs)

## 송신자 측 투척과 양방향 반격

- 전송 성공 응답을 받은 이벤트 ID만 송신 연출에 등록한다. 전송 전에 대기와 진행 중인 전송을 합쳐 256개까지 예약하며, 한도에 도달하면 추가 전송 전에 기존 안내 말풍선으로 알린다. 성공한 요청의 상대나 개수를 병합하지 않으며 요청 순서대로 발사한다.
- 다음 투척이 시작될 때 대기 수 0~1, 2~3, 4~5, 6개 이상에 각각 1배, 1.5배, 2배, 2.5배를 적용한다. 대기 9개 이상이거나 대기 6개 이상에서 가장 오래 기다린 요청이 1.6초를 넘으면, 비슷한 방향으로 최대 3개를 한 투척 구간에서 순차 발사한다. 대기 3개 이하에서 일반 배속 선택으로 복귀한다. 날아가는 눈덩이와 눈 입자의 시간은 별도로 유지한다.
- 방문 펫, 충분히 노출된 임시 펫, 현재 모니터의 무작위 둘레 좌표 순서로 준비와 발사 시점에 대상을 다시 확인한다. 비행은 독립된 목표를 갱신한다. 대상이 사라지면 마지막 유효 위치를 기준으로 모니터 밖으로 이어 날아가며 명중 효과를 내지 않는다.
- 준비 중이거나 비행 중인 반격만 임시 펫의 퇴장을 보류한다. 보류 가능한 기간은 최초 보류부터 1.5초다. 관련 비행이 끝나면 그 전에 퇴장을 재개할 수 있다. 방문 펫으로 전환될 때는 수신 투척 진행과 이미 비행 중인 송신 눈덩이를 같은 펫 ID로 연결한다.
- 각 펫의 투척은 서로 독립이다. 드래그와 게임 자세 동안 내 펫의 투척 진행을 일시 중지한다. 투척 중 피격은 눈 입자와 제한된 표정 반응을 표시한다. 연속 투척 후 한 번 웃고 기존 상태로 복귀한다. 로컬 반격 연출은 별도의 네트워크 요청을 만들지 않는다.
- 관련 구현과 검증은 [송신 시간표](../../src/LabPet.Client/Rendering/SnowballOutgoingTimeline.cs), [송신 연출](../../src/LabPet.Client/Rendering/SnowballOutgoingActor.cs), [임시 펫 시간표](../../src/LabPet.Client/Rendering/SnowballActorTimeline.cs), [대상별 비행 검증](../../tests/LabPet.Tests/SnowballFlightLayerTests.cs)을 참조한다.

## 눈던지기 기반 구현 당시 기록 (2026-11-25)

- 임시 펫 연결 후속 구현: [SnowballTemporaryActor.cs](../../src/LabPet.Client/Rendering/SnowballTemporaryActor.cs)가 시간표·관절·배치를 연결하여 연속 투척 중 같은 펫과 등장 위치를 유지한다. [SnowballOverlayWindow.cs](../../src/LabPet.Client/Views/SnowballOverlayWindow.cs)는 손·대상·포물선 높이를 물리 픽셀에서 DIP로 변환해 실제 비행 계층에 전달하고, 이동 대상에 대한 명중 ID를 반환한다. 자체 창이나 타이머를 투척마다 생성하지 않는다.
- [오버레이 테스트](../../tests/LabPet.Tests/SnowballOverlayWindowTests.cs)의 1초 간격 3회·10회 투척은 실제 WPF 창·손 투영·비행·명중·최종 정리를 함께 검사한다. 최신 해당 Release 실행은 통과 10·실패 0·건너뜀 0, 1초다. 아래 건수는 이전 단계 기록이다. 명령과 범위는 [변경 이력](../05_변경이력/2026-11-25.md)을 참조한다.
- 이 검사는 실제 PetWindow·방문 펫·네트워크 통합 검증이 아니다. UI 세 위치·공유 쿨다운·지원 협상·수신 관리자·손의 눈덩이 표시·피격·위치 분산·공용 루프 연결 및 실제 LAN/GCP·다중 모니터/DPI·성능 검증은 남아 있다. 소셜 코디네이터와 공용 전송 인터페이스는 전용 도구가 `sensitive_file`로 읽기를 거부하여 연결 작업을 보류했다. 사용자 기능 완료 상태로 승격하지 않는다.

- 공용 비행 관리 후속 구현: `SnowballFlightLayer`가 한 Canvas에서 여러 눈덩이와 명중 입자를 독립 갱신하고 명중 ID를 한 번 반환한다. 자체 창·타이머는 생성하지 않는다. 발신자별 취소와 전체 정리를 지원하며, `SnowballOverlayWindow` 종료 시 소유 비행 객체도 정리한다. 종료된 창에서 관리 객체를 다시 생성할 수 없다.
- `PetWindow.TryTriggerSnowballHit`는 UI 스레드·창 가시성·종료·리그 상태를 검사한 뒤 기존 피격 반응을 호출한다. 비행 관리와 이 API의 실제 수신 이벤트·갱신 루프 연결은 아직 남아 있다.
- 비행 관리·오버레이 Release 필터는 통과 7 / 실패 0 / 건너뜀 0, 521ms다. 6개 동시 비행의 독립 명중, 발신자별 취소, 실제 창 종료 후 정리와 늦은 명중 차단을 검사했다. 이는 6개 캐릭터 동시 연출이나 실제 네트워크 검증 결과가 아니다.
- 현재 활성 비행 ID 검사만으로 LAN/GCP 중복 수신을 방지하지 않는다. 대상별 1초 쿨다운, 기능지원 협상, UI 세 위치 및 방문 펫·임시 캐릭터 연결은 미완료다. CPU/GPU·메모리 측정 결과는 없으며 GCP 수정·재배포도 수행하지 않았다. 상세 명령은 [변경 이력](../05_변경이력/2026-11-25.md)을 참조한다.

- 후속 부분 구현: `PetWindow.TryGetSnowballTargetPhysical`이 현재 몸통 중심의 화면 물리 픽셀 좌표를 제공한다. `SnowballImpactVisual`은 100 DIP 영역에서 18개 눈 입자·결정을 0.55초 동안 확산·페이드하며 자체 창·타이머 없이 외부 공용 루프로 진행한다. 두 구성 요소 모두 실제 수신 연출 관리자와의 연결은 아직 남아 있다.
- 명중 효과의 실제 WPF 비트맵 렌더링·페이드·완료 후 투명화·효과 간 독립성을 포함한 `SnowballOverlayWindowTests` Release 실행은 통과 3 / 실패 0 / 건너뜀 0, 411ms다. 아래 29개는 이전 패킷 단계 기록이다. 취소 처리 후 35개 검사 및 입자 추가 전 관련 회귀 필터 78개 검사는 서로 다른 실행이며 상세 명령과 범위는 [변경 이력](../05_변경이력/2026-11-25.md)을 참조한다.
- 실제 버튼 세 위치·공유 쿨다운·LAN/GCP 전송·방문 펫 재사용·화면 연출·DPI·성능 검증은 미완료다. 구성 요소 검사 통과를 사용자 기능 완성으로 간주하지 않는다.


- 현재 공용 `PetInteractionPayload`에 `snowball` 유형 상수와 선택적 `EventId`가 정의되어 있다. 기존 `pet` 기본값과 ID 없는 인사하기 JSON은 유지된다. ID 필드 추가만으로 중복 수신이 차단되는 것은 아니다.
- 목표 동작은 UI 세 위치의 공용 전송 서비스, 대상별 독립 1초 쿨다운, 발신자별 연속 투척 및 최대 6명 동시 연출이다. 방문 펫이 있으면 현재 창을 재사용하고, 없으면 수신자 모니터 둘레에서 임시 펫을 등장시킨다. 코인·친밀도 보상과 효과음은 추가하지 않는다. **이 항목들은 아직 종단 연결 및 실제 화면 검증이 완료되지 않았다.**
- 구버전에는 기능지원 확인 없이 눈던지기를 보내면 안 된다. 지원 협상, 알 수 없는 유형 무시, 이벤트 ID 중복 제거와 오프라인 이벤트 폐기는 후속 구현·검증 대상이다.
- Release 선택 테스트는 패킷 호환성 3개와 기존 애니메이션·시간표 검사를 합쳐 29개 통과했다. 실제 LAN/GCP, 다중 모니터·DPI, 버튼 크기·클릭 동작 및 CPU/GPU·메모리는 미검증이다. 상세 진행 상태는 [아키텍처 설계서](../02_아키텍처_설계서.md)에 기록한다.

## FT-09: 물리 어댑터 필터링 UDP 탐색 및 `protocolVersion` 호환 배지
- **구현 파일**: [LanNetworkHelper.cs](../../src/LabPet.Client/Network/LanNetworkHelper.cs), [LanP2pTransport.cs](../../src/LabPet.Client/Network/LanP2pTransport.cs), [SocialViewModel.cs](../../src/LabPet.Client/ViewModels/SocialViewModel.cs)
- **동작 규칙**:
  - VPN·Docker·WSL·Hyper-V 등 가상 어댑터를 제외하고 활성 물리 LAN/Wi-Fi 어댑터의 서브넷 브로드캐스트 주소로 3초마다 UDP `45678` `HELLO` 송신, 10초 무응답 시 오프라인 처리
  - 영구 `petId` 키로 피어·친구 IP/포트 자동 갱신
  - 프로토콜 호환 범위를 벗어난 피어는 연결을 차단하고 UI에 `업데이트 필요` 배지 및 툴팁 표시

## 눈덩이 소셜 인터랙션: 장면 기반 구현 상태

### 최신 후속 상태

- **로컬 창 연결** — [데스크톱 장면](../../src/LabPet.Client/Views/SnowballDesktopScene.cs)은 로컬 펫과 방문 펫에서 사용할 수 있다. 발신자 방문 펫 재사용, 임시 리그 생성, 내 펫 좌표 기반 명중, 대상 숨김과 모니터 변경 시 정리를 연결했다. 전용 파일 읽기 도구가 없는 현재 세션에서는 보호된 코디네이터를 열지 않았다. LAN/GCP 이벤트의 실제 전달, 지원 협상, 이벤트 중복 제거, 세 발신 UI와 공유 쿨다운은 미연결이다.

- **진입 방향 자세**: 임시 펫의 진입 방향·등장 진행률을 `SetSnowballPeek`로 전달하여 몸통·머리·팔을 작은 각도로 보정한다. 취소·리그 교체·해제 시 초기화한다. 기존 방문 펫에는 적용하지 않는다. 실제 앱에서 방향별 시각 품질은 미검증이다.
- **최신 실제 검증**: `SnowballOverlayWindowTests` Release 통과 40 / 실패 0 / 건너뜀 0, 9초(종료 코드 0). 아래 31개 검사에 네 가장자리·네 대각선의 실제 손 투영 변화 및 취소 후 복원 8개 사례와 대기열 공정성 1개를 추가했다. 공정성 검사는 최대 actor 1에서 기존 비행 중 수락한 후속 투척을 보존하고, 다른 친구 대기 중 신규 연장은 두 진입점에서 거부하며 대기 친구가 차례를 얻어 세 이벤트가 각각 한 번 명중하고 정리되는지 확인한다. 제품 수락 정책은 변경하지 않았다. 실행 명령은 변경 이력에 기록했다. 아래 단계별 숫자는 이전 실행 결과이며 정상 부하의 다중 발신자 연속 수락과 제품 통합 검증은 남아 있다.
- **남은 범위** — 소셜 코디네이터를 통한 제품 송수신, 지원 협상, 송·수신 제한과 이벤트 중복 제거, 세 발신 UI의 공용 상태가 남아 있다. 실제 장치의 혼합 DPI·다중 모니터, 운영 LAN/GCP, CPU·GPU·메모리는 미검증이다. 현재 클라이언트 변경에는 서버 재배포가 필요하지 않으며, 보호된 경로를 연결한 뒤 최종 동작을 확인해야 한다.

### 이전 단계 기록

- **구현된 기반**: `SnowballSceneController`에서 기본 최대 6명 임시 펫·독립 비행·명중을 병렬 갱신한다. 동일 발신자와 다른 오버레이 actor의 중복 등록을 거부한다. `SnowballSceneLoop`는 같은 창의 장면에 하나만 연결되며 단조시간으로 갱신하고, actor·대기 이벤트·잔여 입자가 없으면 Rendering 구독을 해제한다. 창·장면 종료 시 즉시 정리한다.
- **초과 인원 대기열**: [SnowballSceneController.Pending.cs](../../src/LabPet.Client/Rendering/SnowballSceneController.Pending.cs)는 최대 12명·발신자별 1건의 FIFO 대기열과 단조시간 4초 만료를 적용한다. 빈자리가 생길 때만 actor를 생성하며 생성 전후 만료·취소·종료를 재확인한다. 생성 예외·null 반환은 로그를 남기고, 즉시 생성 실패는 수락 성공으로 반환하지 않는다. 대기자가 있으면 기존 actor 연장 요청은 미수락 처리하여 독점을 제한한다. 이 과부하 정책의 정상 연속 투척 수락 보장은 추가 검토 대상이다.
- **등장 위치 분산 기반**: [SnowballSpawnGeometry.cs](../../src/LabPet.Client/Rendering/SnowballSpawnGeometry.cs)의 `TrySelect`는 화면 둘레 전체의 연속 무작위 좌표를 최대 48회 추출한다. 겹침 없는 후보를 우선하고 포화 시 추출 후보 중 화면 내 겹침 면적이 가장 작은 위치를 선택한다. 모든 빈자리 발견이나 전역 최적 위치를 보장하지 않는다. 장면의 `TrySelectSpawn`은 기존 actor의 점유 영역을 반영하며 기존 위치를 변경하지 않는다. 제품 수신 경로의 생성부 연결은 아직 남아 있다.
- **손의 눈덩이 표시**: [SnowballPetVisual.cs](../../src/LabPet.Client/Rendering/SnowballPetVisual.cs)는 실제 3D 손 관절을 투영한 DIP 위치에 경량 2D 눈덩이를 표시한다. [SnowballTemporaryActor.cs](../../src/LabPet.Client/Rendering/SnowballTemporaryActor.cs)는 투척 준비 중 이를 갱신하고 발사 순간 숨긴다. 추가 창·타이머 없이 재사용하고 종료 시 참조를 정리한다. 실제 앱에서 캐릭터별 시각 품질은 미검증이다.
- **실제 검증**: `SnowballOverlayWindowTests` Release 통과 31 / 실패 0 / 건너뜀 0(8초). 기존 대기열 포함 27개에 겹침 후보 재선택·48회 탐색 종료·무효 크기, 음수 원점 모니터에서 12,000개 연속 좌표와 가장자리 길이 비례 분포, 실제 WPF 장면 6명 분산·기존 위치 유지·취소 후 공간 회수, 손의 눈덩이 요소 위치 추적·재사용·발사 순간 숨김·종료 정리 검증을 추가했다.
- **미완료**: 이 결과는 테스트 대상 좌표의 독립 장면 검증이다. UI 세 위치·대상별 공유 쿨다운·지원 협상·전송/수신·방문 펫 재사용·실제 대상 피격·등장 방향별 자세 보정 및 위치 선택·대기열의 앱 수신 경로 연결이 남아 있다. LAN/GCP 실환경·DPI 실기기·CPU/GPU·메모리 측정은 미실행이며 기능 전체 완료로 판정하지 않는다. GCP 서버와 배포 파이프라인 변경·재배포 없음.

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
  - 친구 3D 외형 360도 프리뷰와 바탕화면 방문 펫을 제공한다. 방문 펫의 크기·투명도는 내 펫과 공유한다. 마우스를 올리면 오른쪽 위에 `✕` 버튼이 표시되고 우클릭 메뉴에는 인사하기가 표시된다. 좌클릭하면 이름표 아래 중앙 5 DIP에 전송창이 열린다. 화면 경계에서도 이 상대 위치를 유지한다. 머리 위 수신 팝업은 각 펫이 최대 5개의 독립 만료 카드를 표시한다. 팝업 닫힘 이후 재오픈 시에는 팝업을 먼저 연 뒤 가시 카드 스냅샷을 재측정하고 `NaN`/빈 영역/무효 HWND를 방어하며, 말풍선 레이아웃 예외가 발생해도 `%AppData%\LabPet\logs\crash.log` 기록 후 팝업만 닫아 앱 전체 종료를 방지한다. 모니터 경계에서 본체가 보정되면 화살표가 머리 방향을 따라가며, 상단 공간이 부족하면 아래쪽 배치를 사용한다. 겹친 팝업에는 로컬 수신 순번에 따른 실제 HWND 순서와 전송창 우선순위를 사용한다. [방문 펫 배치 계산기](../../src/LabPet.Client/Views/VisitorBubblePlacementCalculator.cs), [팝업 순서 관리](../../src/LabPet.Client/Views/VisitorPopupOrder.cs), [크래시 로거](../../src/LabPet.Client/Diagnostics/CrashLogWriter.cs), [방문 펫 회귀 테스트](../../tests/LabPet.Tests/VisitorPetBubbleTests.cs)
  - **수면(`zzz`) 상태 동기화**: `DiscoveryHelloPacket`, `FriendPresencePayload`, `PeerSnapshot`, `LocalPresenceState`에 선택 필드 `IsSleeping`(기본 `false`로 하위 호환 유지)을 포함하고, [LanP2pTransport.cs](../../src/LabPet.Client/Network/LanP2pTransport.cs)는 로컬 `IsSleeping`/`IsMusicActive` 변경 시 온라인 친구에게 즉시 `PresenceSync`와 탐색 `HELLO`를 푸시한다. 친구가 수면 중이거나 오프라인이 되면 [친구 페이지](../../src/LabPet.Client/Views/Hub/Pages/FriendsPageView.xaml) 목록에 `zzz`가 표시된다. 온라인 상태인 수면 친구는 펫 호버 친구 오버레이([PetWindow.xaml](../../src/LabPet.Client/Views/PetWindow.xaml)) 왼쪽 고정 상태 슬롯에는 글자가 차례로 떠오르는 미니 `zzz` 애니메이션([MiniSleepZzzIndicator.cs](../../src/LabPet.Client/Views/MiniSleepZzzIndicator.cs))이 재생된다. 바탕화면 방문 친구 펫([VisitorPetWindow.xaml.cs](../../src/LabPet.Client/Views/VisitorPetWindow.xaml.cs)) 및 친구 3D 미리보기([FriendsPageView.xaml.cs](../../src/LabPet.Client/Views/Hub/Pages/FriendsPageView.xaml.cs))도 함께 자는 모션을 취하고 내 펫과 같은 [공용 수면 글자](../../src/LabPet.Client/Views/SleepZzzOverlay.cs)를 오른쪽 위에 표시한다. 친구 목록과 주변 펫 목록도 같은 컨트롤의 미니 변형을 쓰며 글자 애니메이션에는 배경이 없다. 깨어 있는 온라인 상태가 확인되면 기상 모션을 재생한다. 호버 친구 오버레이에는 접속 중인 등록 친구만 표시한다. [수면 프레즌스 검증](../../tests/LabPet.Tests/SocialCoordinatorTests.cs)
  - **GCP 실시간 서버 및 하이브리드 인터넷 친구 연동**: [LabPet.Server](../../src/LabPet.Server/Program.cs), [GcpSignalRTransport.cs](../../src/LabPet.Client/Network/GcpSignalRTransport.cs), [HybridTransportRouter.cs](../../src/LabPet.Client/Network/HybridTransportRouter.cs)를 통해 외부 네트워크에서도 기존 `주변 펫` 탭과 동일한 방식의 온라인 원격 펫 탐색(비친구는 음악·수면 상태 비공개) 및 `친구 신청`/수락(P-256 ECDH), E2EE 메시지·반응·인사하기·코인 게임·원격 노래 따라하기(구독자 있을 때만 스로틀링 중계)를 지원한다. 채팅·오프라인 메시지는 저장하지 않으며 LAN 온라인 친구는 항상 LAN P2P를 우선 사용한다. [GCP 통합 검증](../../tests/LabPet.Tests/GcpServerAndHybridTests.cs), [GCP 배포 가이드](../../deploy/gcp/README.md)
  - `PacketType.GameEvent` 기반 친구 LAN 가위바위보 배틀, 10초 타이핑 스프린트(3초 카운트다운·자동 다음 문장 넘김), 연구실 단체 사다리타기(공용 `FriendInvitePicker` 다중 초대 선택 → 구조화 상품/프리셋 설정 → 9종 상태 대기실[`방장`/`전송 중`/`수락 대기`/`참가 완료`/`거절`/`코인 부족`/`시간 초과`/`연결 끊김`/`초대 취소`] 및 4종 제한 시간[`초대 응답 20초`/`방장 확정 60초`/`위치 선택 30초`/`재연결 유예 10초`]·인원 불일치 시 방장 재조정 및 참가자 동의(`agree-prizes`)·참가자 확정·중복 없는 시작 위치 선택 완료 → CSPRNG Fisher–Yates 불변 사다리판 동시 하강·도착 시 `"?"` 결과 공개 · 게임 시작 이후 취소 차단).
  - 게임 초대는 공용 `FriendInvitePicker`에서 상대를 선택해 발송하고, 1:1 게임은 `OneToOneInviteWaitView` 전용 대기 화면을 표시한다. `GameInviteStatus` 상태 머신에서 발신자 20초 UTC 만료 시각을 공유해 등록·수락·준비·시작 확인을 처리한다. 통합 창의 가시성과 독립적으로 내 펫 머리 위에 카드를 표시하고 상단 배너와 수신 상태를 공유한다. **코인 부족 초대 수신** 시에도 통합 창 상단 초대 배너(`HubWindow.xaml`)와 데스크톱 초대 카드(`GameInviteCardWindow.xaml`)를 숨기지 않고 필요 코인·보유 코인·부족 코인·남은 시간·충족 여부를 표시하며, 수락 버튼만 비활성화한 뒤 코인이 충족되면 실시간으로 해제하고 수락 시 재검증한다(무료 게임은 검사 생략). 거절·부족 응답에는 잔액 숫자를 노출하지 않고 `"coins"` 사유 토큰만 전송한다.
  - 가위바위보·타이핑은 보류 참가비와 양쪽 준비가 확인된 뒤 시작하며, 단체 사다리는 방장 개별/전체 취소, 참가자 자진 퇴장, 2인 미만 자동 전체 취소, 확정 후 이탈/위치 선택 시간 초과 시 전체 취소·환불, 중복 취소/중복 환불/지연 수락/재연결 불일치 방지(`_abortedLadderSessions`)를 적용한다. [초대 회귀 테스트](../../tests/LabPet.Tests/GameInviteTransportTests.cs), [사다리·초대 UI 테스트](../../tests/LabPet.Tests/FriendInviteAndLadderTests.cs)
  - 초대 배너, 데스크톱 카드와 단체 사다리 대기실은 긴 발신자·상품·상태 문구를 줄바꿈해 응답 또는 참가 확정 영역을 확보한다. [WPF 배치 테스트](../../tests/LabPet.Tests/GameUiUxAndInviteTests.cs)
  - 가위바위보는 확정·공개(양측 공개 전 상대 패 비노출), 타이핑은 문장 기록 검증, 사다리는 참가자 확정·위치 선택·가로줄/상품/결과 일관성 검증 및 이탈/실패 시 유료 참가비 1회 환불을 적용한다. P2P 방식의 신뢰 한계는 보안 명세에 기록한다. [LAN 게임 보안](../03_네트워크_프로토콜_및_보안명세.md)

---
[← 이전: 08-2 오디오 반응 및 개인 성장](./08-2_오디오반응_및_개인성장.md) | [개요](./08-0_기능명세서_개요.md)

방문 펫의 말풍선 본문과 꼬리 끝점을 별개로 배치한다. 화면 좌표 양자화 이후에도 본문을 작업영역 내부에 두며, 알림과 메시지는 개별적으로 만료한다.
