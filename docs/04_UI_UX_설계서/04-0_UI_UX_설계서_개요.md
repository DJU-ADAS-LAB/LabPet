---
title: "UI/UX 설계서 개요"
doc_id: SPEC-UI-00
version: "0.2.2"
baseline_commit: "672f24a"
updated: "2026-11-22"
status: "활성"
tags:
  - UIUX
  - 개요
  - LabPet
related:
  - "../01_제품요구사항정의서.md"
  - "./04-1_3D캐릭터_및_투명창_UX.md"
  - "../08_기능명세서/08-0_기능명세서_개요.md"
---

# 04-0. UI/UX 설계서 개요

## 기준선
| 항목 | 내용 |
| --- | --- |
| 기준 릴리스 | `v0.2.2` (게임 UI/UX 및 초대 카드 전면 개편) |
| 렌더링 기반 | WPF `Viewport3D` + `HelixToolkit.Wpf 3.1.2` (`AllowsTransparency="True"`) + `Views/Games/` 전용 게임 셸 |
| 트레이 UI | `H.NotifyIcon.Wpf 2.3.0` |

## 문서 맵
```mermaid
flowchart LR
    Hub["04-0 UI/UX 설계서 개요"] --> S1["04-1 3D 캐릭터 및 투명 창 UX"]
    Hub --> RTM["08-0 기능명세서 개요"]
```

## 화면 인벤토리
| 화면 ID | XAML 파일 | 역할 |
| --- | --- | --- |
| `SCR-01` | [OnboardingWindow.xaml](../../src/LabPet.Client/Views/OnboardingWindow.xaml) | 최초 온보딩 마법사 및 외형·이름 편집 (360도 턴테이블) |
| `SCR-02` | [PetWindow.xaml](../../src/LabPet.Client/Views/PetWindow.xaml) | 바탕화면 상주 투명 3D 펫 창, 상단 말풍선 `Popup`, 친구 오버레이, 시스템 트레이 메뉴 |
| `SCR-03` | [SoloHubWindow.xaml](../../src/LabPet.Client/Views/SoloHubWindow.xaml) | 상단 세그먼트 내비게이션(성장 & 오디오 · 뽀모도로 · 게임), 상단 초대 배너, 8방향 리사이즈 창 |
| `SCR-03A` | [GamesHubView.xaml](../../src/LabPet.Client/Views/Games/GamesHubView.xaml) | 2×2 카드형 게임 대시보드 및 개별 전용 게임 화면(`HiLowGameView`, `RpsGameView`, `TypingGameView`, `LadderGameView`) 단일 마운트 셸 |
| `SCR-04` | [SocialWindow.xaml](../../src/LabPet.Client/Views/SocialWindow.xaml) | 친구 3D 프리뷰 · 말풍선 메시지 · 상태별 노래 따라하기 · 주변 LAN 펫 탐색 |
| `SCR-05` | [EnvironmentGuideWindow.xaml](../../src/LabPet.Client/Views/EnvironmentGuideWindow.xaml) | 방화벽 PowerShell 명령 원클릭 복사 및 SmartScreen 안내 |
| `SCR-06` | [VisitorPetWindow.xaml](../../src/LabPet.Client/Views/VisitorPetWindow.xaml) | 내 바탕화면 옆 방문 친구 3D 펫 창 (크기·투명도 공유, 호버 `✕` 돌려보내기) |
| `SCR-07` | [GameInviteCardWindow.xaml](../../src/LabPet.Client/Views/GameInviteCardWindow.xaml) | 게임 창이 닫혀 있을 때 펫 주변에 표시되는 비활성화 독립 초대 카드(클릭 투과 중 수락 가능) |

---
[개요](./04-0_UI_UX_설계서_개요.md) | [다음: 04-1 3D 캐릭터 및 투명 창 UX →](./04-1_3D캐릭터_및_투명창_UX.md)
