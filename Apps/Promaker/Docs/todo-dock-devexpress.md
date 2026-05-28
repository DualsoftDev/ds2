# TODO — Promaker Dock Layout DevExpress 재구현

> AvalonDock 4.74.1 의 누적 안정성 문제로 인해 DevExpress.Wpf.Docking 으로 swap.
> 핵심 설계: **별도 csproj (`Promaker.Dock`) 로 격리**하여 DevExpress 의 `System.Windows.Forms` / `System.Drawing` transitive 유입을 Promaker 본체에서 차단.

## 1. 배경

### AvalonDock 4.74.1 누적 문제 (작업 폐기 사유)

`done-dock-layout.md` 의 작업 결과로 v15 (hotfix 6건 누적, dock2 worktree stash) 까지 진행했으나 다음 문제 잔여:

| # | 증상 | 원인 (trace 진단) |
|---|---|---|
| 1 | 떼었다 붙이면 크기 변경 (snapshot 미적용 회귀) | `OnAnchorIsVisibleChanged` 의 hardcoded default reset. snapshot 으로 fix (1+2). |
| 2 | 부동 → 도킹 시 default 위치로만 복귀 | `CaptureDockPlacement` 의 `Root != dockManager.Layout` 가드가 drag-drop transient state 에서 skip. `UpdateDockPaneExtents` 안에서 6 anchor placement 재confirm 으로 fix (6). |
| 3 | drag-drop 으로 새 위치 dock 후 size 가 매우 작음 (잔여) | drag-drop 으로 새로 생성된 pane (`AnchorablePaneGroup(Vertical,n=2)/AnchorablePaneGroup(Horizontal,n=1)/AnchorablePane`) 의 size 가 fix 1+2 의 처리 범위 밖. XAML 의 5종 canonical pane (explorerPane / simulationPane / historyPane / llmChatPane / propertyPane) 만 size snapshot 처리. |
| 4 | 부동 누르면 마우스 위치랑 동떨어진 위치에 부동창 (사용자 보고) | `LayoutAnchorable.Float()` 의 default location 사용. ▼ → 부동 menu click 시점의 mouse position 과 무관. |
| 5+ | (반복 패턴) 새 corner case 발견 → 추측 fix → trace → 재진단 cycle 누적 | AvalonDock 4.x 의 runtime 동작 미문서화 + PropertyChanged raise 순서 / Root propagate 시점 unstable. |

→ **fix 누적 비용 ≫ DevExpress swap 비용** 판단. v3 PR-1 spike (138 CS0104 충돌) 의 namespace 문제는 별도 csproj 격리로 회피 가능.

### DevExpress 채택 사유 재확정
- 사용자 검증 — "DevExpress 쓸 때 전혀 이런 문제가 없었음".
- 안정성 (production-grade docking framework).
- 위치/크기 보존, drag-drop dock, floating window 등 모두 framework 자체 처리.

## 2. 핵심 설계 — 별도 csproj 격리

### 구조

```
Apps/Promaker/
├── Promaker/                           ← 본체 (변경 최소)
│   ├── Promaker.csproj
│   │   <ProjectReference Include="..\Promaker.Dock\Promaker.Dock.csproj"
│   │                     PrivateAssets="all" />
│   │   ← DX assembly 직접 reference 안 함. PrivateAssets="all" 로 transitive 차단.
│   │   ← ImplicitUsings enable 유지 — WinForms / Drawing 미유입.
│   └── Windows/MainWindow/MainWindow.xaml
│       <promakerDock:DockHost x:Name="dockHost" />
│
└── Promaker.Dock/                      ← 신규 격리 project
    ├── Promaker.Dock.csproj
    │   <PackageReference Include="DevExpress.Wpf.Docking" Version="24.x" />
    │   <ImplicitUsings>disable</ImplicitUsings>  ← WinForms/Drawing 동시 import 차단
    │   <RootNamespace>Promaker.Dock</RootNamespace>
    │
    ├── DockHost.xaml(.cs)              ← DX DockLayoutManager 캡슐화 UserControl
    │   (DX type 외부 노출 X)
    ├── IDockManager.cs                 ← Promaker 가 사용할 abstract API
    ├── DockAnchor.cs                   ← VM 친화 data record (Content / Title / ContentId)
    └── DockLayoutSerializer.cs         ← Save/Restore API (string filepath)
```

### 외부 노출 API (DX type 절대 X)

```csharp
namespace Promaker.Dock;

public interface IDockManager
{
    // 5 anchor + 1 document 등록.
    void RegisterAnchor(DockAnchor anchor);
    void RegisterDocument(DockAnchor document);

    // 토글 / 호출 — Promaker 의 SSOT (IsLlmChatVisible) 가 호출.
    void SetAnchorVisible(string contentId, bool visible);
    bool IsAnchorVisible(string contentId);

    // 이벤트 — Hiding (X 버튼) → VM 으로 통보.
    event EventHandler<DockAnchorVisibilityChangedEventArgs> AnchorVisibilityChanged;
}

public sealed record DockAnchor(
    string ContentId,
    string Title,
    FrameworkElement Content,
    DockAnchorPosition DefaultPosition);

public enum DockAnchorPosition { LeftTop, LeftBottom, BottomLeft, BottomRight, RightTop, RightMiddle, RightBottom }
```

### namespace 격리 검증 포인트
- Promaker.csproj 의 `<ProjectReference PrivateAssets="all">` 로 DX assembly transitive 가 Promaker.dll 의 reference 에 포함되지 않음을 빌드 결과 검증 (`dotnet list package --include-transitive`).
- `Promaker.csproj` 안의 .cs 에서 DX type 직접 참조 시 컴파일 에러 발생함을 확인 (격리 강제).

## 3. 작업 step (PR 단위)

### PR-D1 — Promaker.Dock csproj skeleton (Foundation) — ✅ 완료 (commit `87ad64e1`)
- [x] `Apps/Promaker/Promaker.Dock/Promaker.Dock.csproj` 신규 (WPF / net9.0-windows / ImplicitUsings disable / RootNamespace=`Promaker.Dock`).
- [x] `Apps/Promaker/Directory.Packages.props` 에 `DevExpress.Wpf.Docking` PackageVersion 등록 (24.1.7 — local feed `C:\Program Files\DevExpress 24.1\Components\System\Components\Packages`).
- [x] `Promaker.Dock.csproj` 에 PackageReference.
- [x] `dotnet build` 성공 (0/0). transitive 9개 package (DevExpress.Data / Drawing / Mvvm / Pdf / Printing / Wpf.Core / Office2019Colorful) + `System.Drawing.Common 4.7.2`. Promaker.Dock 의 bin output 은 .dll 만 (library — Promaker 본체가 reference 할 때 deploy).
- [x] DevExpress 24.1 설치 확인 (`C:\Program Files\DevExpress 24.1\`). 라이선스 정식 file 검증은 사용자 책임.

### PR-D2 — Abstract API + DockHost skeleton — ✅ 완료 (commit `27c85180`)
- [x] `Promaker.Dock/IDockManager.cs` — interface 정의 (RegisterAnchor / RegisterDocument / SetAnchorVisible / IsAnchorVisible / AnchorVisibilityChanged event).
- [x] `Promaker.Dock/DockAnchor.cs` — record (ContentId / Title / Content / DefaultPosition) + `DockAnchorPosition` enum (Left / Bottom / RightTop / RightMiddle / RightBottom / Document).
- [x] `Promaker.Dock/DockHost.xaml(.cs)` — DX `DockLayoutManager` 캡슐화 UserControl skeleton. XAML 의 `x:Name="_dockLayout"` / `"_rootGroup"` = generated partial 에서 `internal` 접근자 (격리 OK). 메서드 4개 모두 `throw new NotImplementedException("PR-D3: ...")`.
- [x] 빌드 통과 (0 오류 / 1 경고 — CS0067 `AnchorVisibilityChanged` 미사용, PR-D3 raise 시 자연 해소).
- [x] DX type 외부 노출 0 검증 (자가 검열 agent 통과, 0 finding).

### PR-D3 — DevExpress DockLayoutManager 기본 layout 구축
- [ ] DockHost.xaml 안에 DX `DockLayoutManager` + `LayoutGroup` 트리 — done-dock-layout.md §3.1 의 안 A (Welcome/Open 통합 LayoutDocument) 그대로 이전.
- [ ] 5 anchor (explorer / simulation / log / property / history / llmchat) + canvas LayoutDocument.
- [ ] DX 의 layout 트리에서 size 보존 / drag-drop / floating 동작이 native 로 처리됨을 검증.

### PR-D4 — Promaker 본체 wire-up
- [ ] `MainWindow.xaml` 의 AvalonDock `DockingManager` 통째 제거 → `<promakerDock:DockHost x:Name="dockHost" />` embed.
- [ ] 5 anchor 의 Content (ExplorerPane / SimulationPanel / etc) 를 `DockAnchor` 로 wrapping + `dockHost.RegisterAnchor(...)`.
- [ ] Welcome / Canvas LayoutDocument 동일.
- [ ] `MainWindow.xaml.cs` 의 AvalonDock 관련 wiring (5+ partial 파일 — DockExtents.cs / DockPlacement.cs / DockTrace.cs / MainViewModel.Dock.cs) 통째 제거.
- [ ] 빌드 통과.

### PR-D5 — SSOT (IsLlmChatVisible) 재구성
- [ ] `MainViewModel.LlmChat.cs` 의 `IsLlmChatVisible` PropertyChanged → `dockHost.SetAnchorVisible("llmchat", show)`.
- [ ] X 버튼 → `AnchorVisibilityChanged` event → VM `IsLlmChatVisible = false` 단방향 wiring.
- [ ] LlmChatVm null / IsLlmEnabled=false edge case 동일 처리.
- [ ] 보기 메뉴 (`MainToolbarEtcContent.xaml`) — `IDockManager` 의 `IsAnchorVisible` / `SetAnchorVisible` 활용. `LayoutAnchorable` 직접 binding 제거.

### PR-D6 — Layout 영속화
- [ ] DX `DockLayoutManager.SaveLayoutToXml(path)` / `RestoreLayoutFromXml(path)` 활용.
- [ ] `Window_Closing` 의 `_llmChatDisposed=true` 직후 Save.
- [ ] `Window.Loaded` 에서 Restore (파일 없음 / parse 실패 시 default).
- [ ] `%LOCALAPPDATA%\Promaker\dock-layout.xml`.

### PR-D7 — 추가 wiring
- [ ] ▼ → 부동 메뉴 click 시점의 mouse position 으로 floating window 위치 보정 (사용자 보고 issue).
- [ ] 헤더 (PanelHeader + HelpButton) — DX `LayoutPanel.Caption` 와 정합. Conditional (docked = UserControl 헤더 + Title 비움 / floating = Title 노출) 또는 DX 자체 caption template 사용.
- [ ] Promaker 자체 dark theme 과 DX skin 정합 — DX `WindowsUI Dark` skin 또는 자체 mapping.

### PR-D8 — AvalonDock 잔재 정리
- [ ] `Apps/Promaker/Directory.Packages.props` 의 `Dirkster.AvalonDock` / `Dirkster.AvalonDock.Themes.Metro` PackageVersion 제거.
- [ ] `Apps/Promaker/Promaker/Promaker.csproj` 의 PackageReference 제거.
- [ ] `Apps/Promaker/Promaker/Spike/DockSpikeWindow.xaml(.cs)` 제거.
- [ ] `Apps/Promaker/Promaker/Windows/MainWindow/DockExtents.cs` / `DockPlacement.cs` / `DockTrace.cs` 제거.
- [ ] `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel/Dock.cs` — DX API 로 재작성 또는 제거.
- [ ] `Apps/Promaker/Docs/done-dock-layout.md` → `Apps/Promaker/Docs/done-dock-avalon.md` 로 rename (역사 보존). 본 문서 (`todo-dock-devexpress.md`) → 완료 후 `done-dock-devexpress.md` rename.

## 4. 관련 파일

### 신규
- `Apps/Promaker/Promaker.Dock/Promaker.Dock.csproj`
- `Apps/Promaker/Promaker.Dock/DockHost.xaml(.cs)`
- `Apps/Promaker/Promaker.Dock/IDockManager.cs`
- `Apps/Promaker/Promaker.Dock/DockAnchor.cs`
- `Apps/Promaker/Promaker.Dock/DockLayoutSerializer.cs` (PR-D6)

### 수정
- `Apps/Promaker/Directory.Packages.props` (DevExpress 추가, AvalonDock 제거)
- `Apps/Promaker/Promaker/Promaker.csproj` (PrivateAssets="all" ProjectReference, AvalonDock 제거)
- `Apps/Promaker/Promaker/Windows/MainWindow/MainWindow.xaml(.cs)` (DockingManager → DockHost)
- `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel/Dock.cs` (IDockManager 활용)
- `Apps/Promaker/Promaker/Controls/Shell/MainToolbarEtcContent.xaml` (보기 메뉴 binding)

### 제거 (PR-D8)
- `Apps/Promaker/Promaker/Spike/DockSpikeWindow.xaml(.cs)`
- `Apps/Promaker/Promaker/Windows/MainWindow/DockExtents.cs`
- `Apps/Promaker/Promaker/Windows/MainWindow/DockPlacement.cs`
- `Apps/Promaker/Promaker/Windows/MainWindow/DockTrace.cs`

## 5. 보존 (참조용, 변경 없음)
- `Apps/Promaker/Promaker/Controls/Canvas/SplitCanvasContainer.xaml`
- `Apps/Promaker/Promaker/Controls/Shell/ExplorerPane.xaml`
- `Apps/Promaker/Promaker/Controls/PropertyPanel/PropertyPanel.xaml`
- `Apps/Promaker/Promaker/Controls/Shell/HistoryPanel.xaml`
- `Apps/Promaker/Promaker/Controls/Simulation/SimulationPanel.xaml`
- `Apps/Promaker/Promaker/Controls/Llm/LlmChatPanel.xaml` 외 LLM 영역
- `Apps/Promaker/Promaker/Controls/Logging/AppLogView.xaml`
- `Apps/Promaker/Promaker/Presentation/ThemeManager.cs`

## 6. AvalonDock 작업물 보존

- dock2 worktree 의 stash@{0} (label: "AvalonDock size snapshot + Root null deferred capture (fix 1-6) — superseded by DevExpress migration plan") 에 본 turn 까지의 6 fix 보존.
- 사용자 명시 시 stash drop. 그 외는 보존 (회귀 fallback / 학습 참조용).
- `done-dock-layout.md` 의 v1~v15 박제는 그대로 — DevExpress 작업 검증 후 PR-D8 rename.

## 7. 결정 사항

| # | 항목 | 결정 |
|---|---|---|
| 1 | 라이브러리 | DevExpress.Wpf.Docking 24.x (안정 latest) |
| 2 | 격리 방식 | 별도 csproj `Promaker.Dock` + `PrivateAssets="all"` |
| 3 | namespace 충돌 회피 | Promaker.Dock 만 `<ImplicitUsings>disable</...>` + 명시 using |
| 4 | API 노출 | `IDockManager` + `DockAnchor` record + `DockHost` UserControl (DX type 외부 노출 0) |
| 5 | 작업 순서 | PR-D1 → D2 → D3 → D4 → D5 → D6 → D7 → D8 |
| 6 | 검증 | 각 PR 단위 빌드 + 수동 시나리오 + 사용자 명시 후 commit |
| 7 | AvalonDock 작업물 | dock2 stash@{0} 보존 / `done-dock-layout.md` 보존 (PR-D8 에서 rename) |
| 8 | DX 라이선스 | PR-D1 첫 step 에서 점검 |

## 8. 주의 사항

- DevExpress 라이선스 미보유 / 만료 시 PR-D1 진행 불가 — 즉시 사용자 알림.
- 본 작업은 `dock2` worktree 에서 진행 (사용자가 의도한 work area). dock 폴더 (옛 worktree) 와 혼동 주의 — 사용자가 `make run` / `cd` 시 dock2 경로 명시.
- 각 PR 의 build 검증 — `dotnet build` 0 경고 / 0 오류 후 사용자 명시 시 commit.
- 자가 검열 trigger (CLAUDE.md): 신규 type 3개 이상 / dispatch 재작성 / public API 갱신 충족 시 sub-agent 위임 후 commit 제안.
- 사용자 명시 없이 git commit 금지.
- `--git-commit` 진행 시 dock2 branch 가 remote 없으면 local commit only (push 생략).
- 본 문서 외 다른 문서 (e.g. `done-dock-layout.md`) 가리키는 참조는 파일 경로 명시.

## 9. 진행 체크포인트 (이어받는 세션용)

### 현재 상태 (branch `dock2`, remote 없음)
- `87ad64e1` — PR-D1: Promaker.Dock csproj skeleton + Directory.Packages.props (DevExpress 24.1.7) + todo 문서 신규.
- `27c85180` — PR-D2: IDockManager / DockAnchor / DockHost skeleton + 자가 검열 0 finding.
- working tree clean (본 §9 추가 commit 직전).
- AvalonDock 6 fix 작업물은 dock2 stash@{0} 보존 (label: "AvalonDock size snapshot + Root null deferred capture (fix 1-6) — superseded by DevExpress migration plan").

### 다음 작업 — PR-D3 (§3 참조)

DockHost 의 실제 layout 트리 구성 + IDockManager 구현. 작업 흐름:

1. **DevExpress 24.1 docking API spike** (선결):
   - DX docs / local feed 의 `DevExpress.Wpf.Docking.24.1.7.nupkg` 내부 또는 `C:\Program Files\DevExpress 24.1\` 의 sample 확인.
   - 핵심 API 확정: `DockLayoutManager.LayoutRoot`, `LayoutGroup`, `LayoutPanel`, `DocumentGroup`, `DocumentPanel`, `LayoutPanel.Closed` event/property, `LayoutPanel.ItemHeight` / `ItemWidth`, layout serializer.
   - 격리 검증 — DockHost.xaml.cs 안의 명시 using (`DevExpress.Xpf.Docking;`) 으로 `LayoutPanel` / `DockLayoutManager` 사용 후 빌드 0 경고 / 0 오류.

2. **DockHost.xaml layout 트리 구성**:
   - done-dock-layout.md §3.1 의 안 A (Welcome/Open 통합) 그대로 이전.
   - 외곽 horizontal `LayoutGroup`: Left column (Explorer) | Center (Document area + Bottom group) | Right column (Properties / History / LlmChat).
   - `DocumentGroup` 안에 Welcome / Canvas LayoutDocument 2종 (`HasProject` 토글로 Add/Remove 또는 visibility 토글).

3. **`DockHost.xaml.cs` 의 IDockManager 구현**:
   - `RegisterAnchor(DockAnchor)`: `DefaultPosition` 에 따라 미리 만든 6 LayoutPanel 중 하나에 anchor 의 `Content` set + `Caption=anchor.Title` + `Name=anchor.ContentId` (binding key).
   - `RegisterDocument(DockAnchor)`: DocumentGroup 안에 DocumentPanel 추가.
   - `SetAnchorVisible(string contentId, bool visible)`: `LayoutPanel.Closed` (또는 동등 property) toggle.
   - `IsAnchorVisible(string contentId)`: 동일 property 조회.
   - `AnchorVisibilityChanged` raise — DX 의 `LayoutPanel.Closed` event (또는 `ClosedChanged`) hook → `_contentId → bool` 으로 변환 후 발화. 본 raise 로 PR-D2 의 CS0067 경고 자연 해소.

4. **격리 재검증**:
   - `_dockLayout` / `_rootGroup` / 추가 신규 LayoutPanel field 들이 모두 `internal` 또는 `private` (public 노출 0).
   - IDockManager 의 public 시그니처에 DX namespace type 0건 유지.

5. **빌드 + 자가 검열 (general-purpose agent)**:
   - 신규 type 3개 이상 + dispatch 변경 (RegisterAnchor 의 position dispatch) 충족 → 자가 검열 의무.
   - prompt 의 검증 항목 0번 박제: 사용자 의도 verbatim 인용 + patch ↔ 의도 1:1 매핑.
   - 통과 시 commit (메시지 박제: `[dock2] Dock layout: PR-D3 — DockLayoutManager layout 트리 + IDockManager 구현`).

### 새 세션 시작 시 첫 행동
1. 본 문서 `Apps/Promaker/Docs/todo-dock-devexpress.md` 전체 read.
2. 현재 commit 상태 확인 — `git log --oneline -5` 로 `87ad64e1` / `27c85180` / 본 §9 commit 확인.
3. dock2 worktree (`/f/Git/ds2/dock2`) 안에서 작업 — `cd` / `make run` 시 경로 명시.
4. PR-D3 의 §3 항목 5건 진행 → 본 §9 의 "다음 작업" 흐름 따라 step 1~5 순서.
5. PR-D3 commit 후 본 §9 갱신 (PR-D3 completion 박제 + PR-D4 시작점 명시).

### 주의 — DX API spike 결과를 본 문서에 박제

PR-D3 진행 중 DX 의 정확한 API (메서드 명 / event 명 / Closed vs Closing 등) 가 본인이 가정한 것과 다를 가능성. spike 후 정확한 API 를 §9 또는 §3 의 PR-D3 단계에 박제 — 다음 PR (D4~D8) 진행 시 같은 spike 반복 회피.
