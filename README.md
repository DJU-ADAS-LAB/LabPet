# LabPet

Windows 바탕화면 위에서 투명 창으로 상주하는 **3D 커스텀 데스크톱 펫** 애플리케이션입니다.

## 핵심 기능 및 추적성 (`FT-XX`)
- **FT-01 공용 프로토콜·모델·전송 인터페이스 (`LabPet.Shared`)**는 신규 친구 키 합의와 서명된 응답을 사용하는 프로토콜 v3를 제공한다. [네트워크 보안 명세](docs/03_네트워크_프로토콜_및_보안명세.md)
- **FT-02 Blender 3D 캐릭터 7종·커스텀 레이어 및 절차적 폴백**: 말랑 모찌 정령, 치비 고양이, 솜구름, 솜뭉치 강아지, 하치와레, 치이카와, 우사기 + 5개 커스텀 레이어(종·컬러·표정·머리 악세서리·오라) + 무재생성 색상 교체
- **FT-03 360도 3D 턴테이블 온보딩 및 외형 편집**: 실시간 드래그 회전 미리보기 및 펫 이름/외형 설정
- **FT-04 투명 바탕화면 펫 창 UX**: 시선 추적(Look-At), 5px 클릭/드래그/흔들기 판정, 클릭 투과 집중 모드, 시스템 트레이 메뉴, 단일 인스턴스 보장
- **FT-05 AppData 분리 저장 및 스키마 마이그레이션**: `%AppData%\LabPet\` 원자적 저장(`.tmp`→`Move`), `.bak` 자동 복구, `schemaVersion` 승격
- **FT-06 `NAudio` 루프백 오디오 분석기**: 1,024-point FFT, 적응형 비트 감지, 무음 120ms 감쇠, 장치 0개 폴백, 핫스왑 자동 재연결
- **FT-07 종별 모션 클립 리듬 댄스·3D 풀링 이펙트·타이핑 앞발**: 8종 모션 클립 크로스페이드, 종별 움직임 프로파일, 풀링 3D 음표/별/눈꽃 이펙트, 미니 키보드 앞발 타건
- **FT-08 개인 성장 루프 · 뽀모도로 · 코인 게임**: 활동량/가동시간/뽀모도로 보상 및 4종 코인 게임(하이로우 · LAN 가위바위보 · LAN 타이핑 대결 · 연구실 공동 사다리타기)
- **FT-09 ~ FT-10 서버리스 LAN P2P 소셜**: 물리 어댑터 필터링 UDP 탐색, `protocolVersion` 업데이트 배지, TCP 친구 인증/HMAC, 말풍선 메시지, 30Hz 노래 따라하기
- **FT-11 ~ FT-12 배포 · 자동 업데이트 · 운영**: Velopack `Program.cs` 및 `UpdateService`, GitHub Actions 차분 릴리스, 시작 프로그램 등록, 방화벽/SmartScreen 안내

## 기술 스택
| 구분 | 기술 / 패키지 |
| --- | --- |
| 런타임 / UI | `.NET 10` (`net10.0-windows`), WPF |
| 3D 렌더링 | `HelixToolkit.Wpf 3.1.2` (`HelixToolkit.Geometry.MeshBuilder` + WPF `Viewport3D`) |
| 오디오 분석 | `NAudio 2.2.1` (`WasapiLoopbackCapture`, `FastFourierTransform`, `MMDeviceEnumerator`) |
| MVVM / DI | `CommunityToolkit.Mvvm 8.4.0`, `Microsoft.Extensions.DependencyInjection 10.0.0` |
| 시스템 트레이 | `H.NotifyIcon.Wpf 2.3.0` |
| 배포 / 업데이트 | `Velopack 1.2.161` + GitHub Actions (`.github/workflows/release.yml`) |
| 테스트 | `xUnit 2.9.3` |

## 빌드 · 테스트 · 실행

`main` 푸시와 `main` 대상 PR에서는 Release 빌드와 전체 단위 테스트를 실행한다. [Windows 검증 워크플로](.github/workflows/verify.yml) · [Windows 릴리스 워크플로](.github/workflows/release.yml)
```powershell
dotnet restore LabPet.slnx
dotnet build LabPet.slnx -c Release --no-restore
dotnet test LabPet.slnx -c Release --no-build --no-restore
dotnet run --project src/LabPet.Client/LabPet.Client.csproj
```

## 문서 목차 (`docs/` — Obsidian 볼트 호환)
- [AI 작업 기준서 (AGENTS.md)](AGENTS.md)
- [01. 제품요구사항정의서](docs/01_제품요구사항정의서.md)
- [02. 아키텍처 설계서](docs/02_아키텍처_설계서.md)
- [03. 네트워크 프로토콜 및 보안 명세](docs/03_네트워크_프로토콜_및_보안명세.md)
- [04-0. UI/UX 설계서 개요](docs/04_UI_UX_설계서/04-0_UI_UX_설계서_개요.md)
  - [04-1. 3D 캐릭터 및 투명 창 UX](docs/04_UI_UX_설계서/04-1_3D캐릭터_및_투명창_UX.md)
- [05. 일자별 변경 이력 (2026-11-20)](docs/05_변경이력/2026-11-20.md)
- [05. 일자별 변경 이력 (2026-11-21)](docs/05_변경이력/2026-11-21.md)
- [05. 일자별 변경 이력 (2026-11-22)](docs/05_변경이력/2026-11-22.md)
- [06. 배포 · 자동 업데이트 · 방화벽 및 SmartScreen 가이드](docs/06_배포_자동업데이트_및_방화벽가이드.md)
- [Windows 배포 운영](docs/배포_운영_가이드.md) · [배포 장애 및 재발 방지](docs/배포_장애_및_재발방지.md)
- [07. LabPet.Server GCP 운영 가이드](docs/07_GCP_클라우드_확장가이드.md)
- [08-0. 기능명세서 개요 및 RTM](docs/08_기능명세서/08-0_기능명세서_개요.md)
  - [08-1. 코어 3D · 온보딩 · 분리 저장소 명세](docs/08_기능명세서/08-1_코어3D_온보딩_저장소.md)
  - [08-2. 오디오 반응 및 개인 성장 명세](docs/08_기능명세서/08-2_오디오반응_및_개인성장.md)
  - [08-3. 서버리스 LAN P2P 소셜 명세](docs/08_기능명세서/08-3_LAN_P2P_소셜.md)
  - [08-4. 배포 · 자동 업데이트 · 운영 명세](docs/08_기능명세서/08-4_배포_업데이트_운영.md)
