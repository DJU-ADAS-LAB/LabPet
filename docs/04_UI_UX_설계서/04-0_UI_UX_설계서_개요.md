---
title: "UI/UX 설계서 개요"
doc_id: SPEC-UI-00
version: "0.1.0"
baseline_commit: "49d7700"
updated: "2026-11-20"
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
| 기준 릴리스 | `v0.1.5` (`49d7700`) |
| 렌더링 기반 | WPF `Viewport3D` + `HelixToolkit.Wpf 3.1.2` (`AllowsTransparency="True"`) |
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
| `SCR-02` | [PetWindow.xaml](../../src/LabPet.Client/Views/PetWindow.xaml) | 바탕화면 상주 투명 3D 펫 창 및 시스템 트레이 메뉴 |

---
[개요](./04-0_UI_UX_설계서_개요.md) | [다음: 04-1 3D 캐릭터 및 투명 창 UX →](./04-1_3D캐릭터_및_투명창_UX.md)
