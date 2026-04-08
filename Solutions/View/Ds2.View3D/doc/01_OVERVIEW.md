# Ds2 3D View — 01. 전체 구조 개요

## 1. 목적

Ds2 Promaker 시뮬레이션 창에서 **공장 설비 배치를 3D로 시각화**하고,
시뮬레이션 실행 중 각 장비의 상태(Ready/Going/Finish/Homing)를 실시간으로 반영한다.

---

## 2. 위치

```
기타 섹션 툴바 [3D] 버튼
    ↓
View3DWindow (WPF Window)
    └─ WebView2
         └─ facility3d.html (Three.js 3D 씬)
```

---

## 3. 3-Layer 아키텍처

```
┌─────────────────────────────────────────────────────────────┐
│  Layer 1 — Core Engine (F#, 렌더러 독립)                      │
│  Ds2.3DView.Core.dll                                         │
│  · Types.fs          — 데이터 계약                            │
│  · LayoutAlgorithm.fs — 좌표 계산                             │
│  · ContextBuilder.fs  — DsStore → View DTO 변환               │
│  · SceneEngine.fs     — 통합 Public API                       │
│  · SelectionState.fs  — 선택 상태 머신                         │
│  · Persistence.fs     — 레이아웃 저장/복원 추상화               │
└─────────────────────────────────────────────────────────────┘
         ↕ SceneData (F# 레코드)
┌─────────────────────────────────────────────────────────────┐
│  Layer 2 — Renderer (JavaScript, Three.js r128)               │
│  wwwroot/js/three-interop.js  — 3D 씬 전체 구현               │
│  wwwroot/js/ds-3d-commons.js  — 케이블·파티클 유틸             │
│  wwwroot/facility3d.html      — 씬 컨테이너 + 컨텍스트 메뉴   │
└─────────────────────────────────────────────────────────────┘
         ↕ JSON 메시지 (ExecuteScriptAsync / WebMessageReceived)
┌─────────────────────────────────────────────────────────────┐
│  Layer 3 — WPF UI Adapter (C#)                               │
│  ThreeDViewState.cs  — ViewModel + WebView2 브릿지            │
│  View3DWindow.xaml   — WPF 창 (WebView2 호스트)               │
│  MainViewModel.cs    — Open3DViewCommand                      │
│  SimulationPanelState.Events.cs — 시뮬 이벤트 → ThreeD 라우팅 │
└─────────────────────────────────────────────────────────────┘
```

---

## 4. 컴포넌트 파일 맵

| 파일 | Layer | 역할 |
|------|-------|------|
| `Solutions/View/Ds2.3DView/Ds2.3DView.Core/Types.fs` | 1 | 모든 데이터 타입 정의 |
| `Solutions/View/Ds2.3DView/Ds2.3DView.Core/LayoutAlgorithm.fs` | 1 | 순수 함수형 좌표 계산 |
| `Solutions/View/Ds2.3DView/Ds2.3DView.Core/ContextBuilder.fs` | 1 | DsStore → View DTO |
| `Solutions/View/Ds2.3DView/Ds2.3DView.Core/SceneEngine.fs` | 1 | Public API 진입점 |
| `Solutions/View/Ds2.3DView/Ds2.3DView.Core/SelectionState.fs` | 1 | 선택 상태 |
| `Solutions/View/Ds2.3DView/Ds2.3DView.Core/Persistence.fs` | 1 | ILayoutStore 추상화 |
| `Apps/Promaker/Promaker/wwwroot/js/three-interop.js` | 2 | Three.js 씬 엔진 (~5600줄) |
| `Apps/Promaker/Promaker/wwwroot/js/ds-3d-commons.js` | 2 | 보조 시각 효과 |
| `Apps/Promaker/Promaker/wwwroot/js/wpf-dotnet-polyfill.js` | 2↔3 | Blazor 콜백 → WebView2 브릿지 |
| `Apps/Promaker/Promaker/wwwroot/facility3d.html` | 2 | HTML 호스트 + 컨텍스트 메뉴 |
| `Apps/Promaker/Promaker/ViewModels/Simulation/ThreeDViewState.cs` | 3 | WPF ViewModel + WebView2 전송 |
| `Apps/Promaker/Promaker/Windows/View3DWindow.xaml(.cs)` | 3 | WPF 창, WebView2 초기화 |
| `Apps/Promaker/Promaker/ViewModels/Simulation/SimulationPanelState.Events.cs` | 3 | 시뮬 → 3D 이벤트 라우팅 |
| `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.cs` | 3 | Open3DViewCommand |
| `Apps/Promaker/Promaker/Controls/Shell/MainToolbarEtcContent.xaml` | 3 | 3D 툴바 버튼 |

---

## 5. 주요 데이터 흐름 요약

```
[파일 열기]
DsStore
  └─ SceneEngine.BuildDeviceScene()
       ├─ ContextBuilder → DeviceNode list
       ├─ LayoutAlgorithm → LayoutPosition list
       └─ ContextBuilder → FlowZone list
            ↓ SceneData
ThreeDViewState.BuildScene()
  ├─ floorSize 계산
  ├─ { type:"init", config:{flowZones, floorSize} }  → JS
  └─ foreach device → { type:"addDevice", x, z }    → JS
       ↓
Ev23DViewer.init() + addDeviceAtPosition()
  └─ Three.js 씬 빌드

[시뮬레이션 실행]
SimEngine.WorkStateChanged / CallStateChanged
  └─ SimulationPanelState.Events.cs
       └─ ThreeDViewState.OnWorkStateChanged / OnCallStateChanged
            └─ { type:"updateDeviceStates" / "updateWorkStates" } → JS
                 └─ Ev23DViewer.updateDeviceStates() / updateWorkStates()
```

---

## 6. 빌드 의존성

```
Promaker.csproj
  ├─ <PackageReference Include="Microsoft.Web.WebView2" />
  └─ <ProjectReference Include="..\..\..\Solutions\View\Ds2.3DView\Ds2.3DView.Core\Ds2.3DView.Core.fsproj" />

Ds2.3DView.Core.fsproj
  └─ <ProjectReference Include="..\..\..\Core\Ds2.Store\Ds2.Store.fsproj" />
```

---

## 7. 실행 진입점

1. Promaker 실행 → 프로젝트 파일(`.sdf`) 열기
2. 기타 툴바 **[3D]** 버튼 클릭 (`Open3DViewCommand`)
3. `View3DWindow` 생성 → WebView2 초기화 → `facility3d.html` 로드
4. `NavigationCompleted` → `ThreeDViewState.BuildScene()` 호출
5. 씬 데이터 JSON으로 WebView2 전달 → Three.js 씬 렌더링
6. 시뮬레이션 실행 시 상태 변경 이벤트 자동 반영
