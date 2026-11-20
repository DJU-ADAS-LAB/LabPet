# LabPet AI 에이전트 작업 기준서 (`AGENTS.md`)

## 1. 프로젝트 개요 및 핵심 목표
- **프로젝트명**: `LabPet` (Windows WPF 3D 데스크톱 펫 애플리케이션)
- **기술 스택**: `.NET 10` (`net10.0` / `net10.0-windows`), WPF, `HelixToolkit.Wpf 3.1.2`, `CommunityToolkit.Mvvm`, `H.NotifyIcon.Wpf`, `Microsoft.Extensions.DependencyInjection`
- **핵심 목표**:
  1. 투명 바탕화면 창 위에서 3종 3D 캐릭터(`mochi_spirit`, `chibi_cat`, `fluffy_cloud`)를 말랑한 클레이/토이 감성으로 저부하 렌더링
  2. 외부 서버 없이 로컬 네트워크(LAN)만으로 동작하는 서버리스 P2P 소셜 기능(`ISocialTransport`) 구현
  3. 개인 성장 루프 및 시스템 오디오 반응 콘텐츠 제공
  4. 추후 GCP 서버(ASP.NET Core SignalR + Cloud Firestore) 확장 시 UI/도메인 수정 없이 전송 계층만 교체 가능하도록 `LabPet.Shared` 분리 유지
  5. Velopack + GitHub Actions 기반 차분 자동 업데이트 체계 구축

## 2. 최우선 준수 규칙
- 모든 응답, 코드 주석, 문서 파일명과 본문은 한국어로 작성한다(코드 심볼·식별자·CLI 명령·스펙 키워드 제외).
- 요구사항에 모호한 부분이 있으면 추측하지 않고 먼저 사용자에게 질문한다.
- 사용자 지시 이외의 임의 기능·UI·API를 추가하기 전에 반드시 피드백을 받는다.
- 요청 없는 더미 데이터나 시뮬레이션 가정을 넣지 않는다.
- 근거 없는 수치나 과장·단정 표현을 쓰지 않는다.
- 하드코딩을 자제하고 설정·상수로 분리한다.
- 삭제한 기능의 관련 코드·파일·문서 링크는 잔여물 없이 정리한다.
- 문제 해결 전 원인을 파악하고 계획을 세운다.
- 구현 후 실행한 빌드·테스트 결과와 사용자가 직접 확인할 수 있는 수동 검증 시나리오를 안내한다.

## 3. 로컬 환경, 빌드·테스트·실행 명령
- **SDK**: `.NET 10.0.404` ([global.json](global.json) 고정)
- **빌드**: `dotnet build LabPet.slnx -c Debug`
- **단위 테스트**: `dotnet test LabPet.slnx -c Debug`
- **클라이언트 실행**: `dotnet run --project src/LabPet.Client/LabPet.Client.csproj`

## 4. 아키텍처 및 핵심 디렉터리·모듈 맵
- [src/LabPet.Shared](src/LabPet.Shared): 프로필/친구/피어 DTO, `ProtocolConstants`, `ProtocolJson`, `Packets`, `PacketSigner`, `ISocialTransport`
- [src/LabPet.Client](src/LabPet.Client):
  - `Storage/`: `%AppData%\LabPet\` 원자적 JSON 저장소(`JsonAppDataStore`) 및 `schemaVersion` 마이그레이터(`StateMigrator`)
  - `Rendering/`: `IPetMeshFactory`, `ProceduralPetMeshFactory`, `PetRigHandle`, `PetAnimationController`, `PetCustomizationCatalog`
  - `Interop/`: `Win32WindowHelper` (`WS_EX_TRANSPARENT` 클릭 투과, 전역 커서 위치)
  - `ViewModels/`: `PetWindowViewModel`, `OnboardingViewModel`
  - `Views/`: `PetWindow.xaml(.cs)`, `OnboardingWindow.xaml(.cs)`
- [tests/LabPet.Tests](tests/LabPet.Tests): 프로토콜 호환성, HMAC 서명, JSON 폴백, 저장소 원자성/복구, 3D 메시 생성 단위 테스트

## 5. 핵심 동작·성능·보안·호환성 유의사항 (회귀 방지 체크리스트)
- **데이터 경로 분리**: 모든 사용자 데이터는 `%AppData%\LabPet\`에만 저장하며 `AppContext.BaseDirectory`에 절대 기록하지 않는다.
- **원자적 저장**: 임시 파일(`.tmp`) 기록 후 플러시 → 기존 파일 `.bak` 백업 → `File.Move(..., overwrite: true)` 순서를 유지한다.
- **투명 창 호환성**: SharpDX 기반이 아닌 순수 WPF `Viewport3D` + `HelixToolkit.Wpf` 조합을 유지해 `AllowsTransparency="True"` 에어스페이스 충돌을 방지한다.
- **60fps 저부하 갱신**: 백그라운드 스레드에서 `Dispatcher.Invoke`를 남발하지 않고, UI 타이머에서 `Transform3D`와 `EmissiveMaterial`만 갱신한다.
- **통신 계층 분리**: ViewModel과 View는 `ISocialTransport` 인터페이스만 의존한다.

## 6. 문서화(`docs/`) 및 Git 형상관리 규칙

### 문서화(`docs/`) 및 변경 이력 유지 상세 규칙
1. 모든 신규 문서와 `docs/` 하위 파일명은 한국어로 작성하고, 모든 코드·테스트·문서 참조는 Obsidian·GitHub·IDE 모두에서 동작하는 정확한 상대경로 마크다운 링크(`[표시명](상대경로)`)로 연결한다. (`docs/.obsidian/` 작업 공간 파일은 `.gitignore`의 `.obsidian/` 규칙으로 커밋에서 제외한다.)
2. 기능 추가·수정·삭제 후에는 커밋 전에 관련 설계서(`01`, `02`, `04-*`), 기능명세서 RTM(`08-0` 및 `08-1`~`08-N`), 그리고 `docs/05_변경이력/YYYY-MM-DD.md`를 함께 갱신한다.
3. 삭제한 기능이나 폐기된 파일·상수는 관련 명세와 RTM 및 `related`/푸터 링크에서도 즉시 제거하거나 상태를 정리해 고아 문서나 깨진 링크를 남기지 않는다.
4. `docs/05_변경이력/YYYY-MM-DD.md` 각 항목에는 반드시 **[변경 배경 / 수정 파일 및 동작 / 직접 실행한 빌드·테스트 결과(스위트·건수·미실행 범위) / 사용자 수동 확인 시나리오]** 를 사실대로 기록한다.
5. 과장되거나 단정적인 표현이나 근거 없는 수치를 문서에 적지 않는다.

### Git 커밋 컨벤션 (Conventional Commits)
- `docs:` 문서 생성 및 갱신
- `feat:` 새로운 기능 구현
- `perf:` 처리 속도·응답 시간·렌더링·메모리 등 성능 최적화
- `fix:` 버그 및 예외 처리 수정
- `refactor:` 동작 변경 없는 구조·코드 개선
- `test:` 테스트 코드 추가 및 보완
- `chore:` 빌드 설정·의존성·CI·도구 환경 정비
- 커밋 제목은 `<타입>: <한국어 요약>` 형식으로 작성하며(필요 시 `<타입>(<범위>): <한국어 요약>` 선택 사용), 관련 없는 변경을 하나의 커밋에 섞지 않는다.
- 작업 단위 완료 시 반드시 `dotnet build LabPet.slnx -c Debug && dotnet test LabPet.slnx -c Debug` 통과 확인 후 `docs/` 갱신과 함께 커밋한다. (원격 저장소 `push`는 명시적 요청 시에만 수행한다.)
