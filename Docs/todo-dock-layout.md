# TODO — Promaker Dock Layout 도입 (AvalonDock 4.x 기반)

> Canvas 를 제외한 모든 창(Explorer, Properties, History, Simulation/Gantt, LLM Chat)에 Attach/Detach + 마그넷 dock 지원.
> **AvalonDock 4.74.x** (`Dirkster.AvalonDock`, Ms-PL) 기반.
>
> ⚠️ 본 문서 변경 이력:
> - v1: DevExpress `DockLayoutManager` 기반 plan 작성
> - v2: 외부 reviewer 5명 통합 검증 → 사실관계 정정 (MahApps 본체 미사용 / WebView2 LlmChat 무관 / PanelHeader 패턴 3곳)
> - v3: --review web 검증 + 본 환경 직접 검증 (DevExpress 24.1.7 로컬 feed 자동 등록 확인, 옵션 A 채택)
> - v4: PR-1 spike 결과로 DevExpress.Wpf.Docking 24.1.7 도입 시 138 곳 모호 참조 + WinForms transitive + size 비용. AvalonDock 4.x 로 변경.
> - **v5 (현재)**: 5명 메타 reviewer (Generalist / 정확성 / 설계 / 영향범위 / 인터넷 검색) 통합 검증 반영. 핵심: 테마 NuGet 별도, 4.74.1 stable, ContentId e.Cancel 패턴, §3.4 헤더 API 3안 비교, §3.3 single-source-of-truth 모델, PR-2 분할, log4net C# 패턴(`Log.Info`) 정정.

## 1. 작업 목표 (변경 없음)

`MainWindow.xaml` 의 `Grid + GridSplitter` 하드코딩 layout 을 AvalonDock `DockingManager` 로 교체하여:

- 패널 attach / detach (floating window)
- 마그넷 가이드 (Visual Studio 스타일 다이아몬드)
- Auto-hide (사이드 탭으로 접힘)
- Layout 자동 저장/복원 (+ 사용자 수동 reset)
- 향후 다중 Canvas tab 확장 가능

대상: `ExplorerPane`, `PropertyPanel`, `HistoryPanel`, `SimulationPanel`(Gantt/Status Monitor/Event Log), `LlmChatPanel`
**제외**: `SplitCanvasContainer`(Canvas) — `LayoutDocument` 로 두되 close/float 불가.

## 2. 배경 / 사실관계 (검증 완료)

### Promaker 의 실제 UI 스택
- **MahApps.Metro 본체 미사용**, `MahApps.Metro.IconPacks.Material` 만 의존 (아이콘 전용).
- 테마는 **Promaker 자체 ResourceDictionary**. 핵심 브러시: `PrimaryBackgroundBrush`, `SecondaryBackgroundBrush`, `TertiaryBackgroundBrush`, `BorderBrush`, `AccentBrush`, `PanelHeaderBrush`, `PrimaryTextBrush`, `SecondaryTextBrush`, `IconBlueBrush`. 자체 스타일 키 `PanelContainer`, `PanelHeader`, `HelpButton`, `DarkButton`.
- 테마 전환은 `Presentation/ThemeManager.cs` 의 `ThemeChanged`.
- **log4net 사용 패턴 (C#)**: `private static readonly ILog Log = LogManager.GetLogger(typeof(...));` + `Log.Info/Warn/Error` (App.xaml.cs:14 등 8곳 확인).
- Promaker target: `net9.0-windows`. NuGet feed: `Solutions/nuget.config` (`nuget.org` 만, AvalonDock 도 nuget.org 정식).

### 현재 layout 구조 (`MainWindow.xaml`)
- 외곽 Grid: col 0 Explorer 320 / col 1 splitter 4 / col 2 Workspace * (row 0 Canvas, row 1 splitter, row 2 SimulationPanel 200) / col 3 splitter 4 / col 4 Right 280 (row 0 Property, row 1 splitter, row 2 History 220) / col 5 LlmChat splitter Thumb 4 / col 6 LlmChat (기본 width 0)
- LlmChat 토글 = `LlmChatPanelCol` width 0 ↔ 380 + splitter 0 ↔ 4 (Workspace `*` 폭 침식 회피용 Thumb DragDelta)
- `WelcomeOverlay`: `Grid.ColumnSpan=7` + 자체 AllowDrop/DragEnter/Over/Leave/Drop (자체 drop target)
- `FileDragOverlay`: `Grid.RowSpan=3`, `IsHitTestVisible="False"` (순수 visual feedback)
- `Window_Closing` (MainWindow.xaml.cs:96-113): consent → e.Cancel=true → `_llmChatDisposed=true` → `await DisposeLlmChatAsync()` → `Dispatcher.BeginInvoke(Close, Background)`. 두 번째 진입은 line 105 에서 return.

### LlmChat 토글 흐름
- `MainViewModel.LlmChat.cs` (partial): `[ObservableProperty] _isLlmChatVisible`, `[RelayCommand] ToggleLlmChat()` (Lazy `LlmChatVm` 생성), `InitLlmAutostart()` (`App.StartupAutoOpenLlm` 시 `Dispatcher.BeginInvoke(..., DispatcherPriority.Loaded)` 로 ToggleLlmChat 호출).
- `MainWindow.xaml.cs:32` 가 `IsLlmChatVisible` PropertyChanged 구독 → `UpdateLlmChatColumnWidths`.
- Toolbar `MainToolbarEtcContent.xaml:100` 가 `Command="{Binding ToggleLlmChatCommand}"`.
- `ENABLE_LLM=false` 환경에서는 ToggleLlmChat 자체가 no-op 가능 (consent 거부 / VM null) — 새 dock UI 의 컨텍스트 메뉴/X 버튼 노출도 같은 원칙 따라야 함.

### WebView2 사용처 (LlmChat 영향권 밖)
`Windows/View3DWindow`, `Windows/CustomModelDialog`, `ViewModels/Simulation/ThreeDViewState`, `ViewModels/CustomModelRegistry`. **LlmChat 폴더에는 0건**.

### v3 PR-1 spike 결과 (DevExpress 폐기 사유)
- `DevExpress.Wpf.Docking 24.1.7` 추가 후 `dotnet build` 시 138 곳 CS0104 모호 참조 (`Point`/`Brush`/`Rectangle`/`UserControl`/`Button`/`TextBox`/`ListBox`/`TreeView`/`CheckBox`/`KeyEventArgs`/`MouseEventArgs`).
- 원인: DX 가 transitive `System.Windows.Forms` + `System.Drawing`. Promaker `<ImplicitUsings>enable</ImplicitUsings>` 충돌.
- 해결안 비용 과대 → DevExpress 폐기, csproj/Packages.props revert 완료 (빌드 클린).

### 사용자 결정 사항 (확정)
| # | 항목 | 결정 |
|---|---|---|
| 1 | 라이브러리 | **AvalonDock 4.74.x (`Dirkster.AvalonDock`, Ms-PL)** |
| 2 | Canvas 처리 | `LayoutDocument` (다중 Canvas tabbing 대비) |
| 3 | Auto-hide | 활성 |
| 4 | Layout 저장 | 자동 (종료 시 저장 / 시작 시 복원) |
| 5 | 기본 비율 | 현행 유지 (Explorer 320 / * / Right 280, Sim 200, History 220) |
| 6 | LlmChat 기본 | 우측 끝, 기본 닫힘 |

### 외부 사실 요약 (v5 reviewer R5 검증)
| 항목 | 값 |
|---|---|
| 최신 안정 버전 | **4.74.1** (2026-04-25) |
| .NET 9 지원 | ✓ (`net9.0-windows7.0` TFM) |
| 라이선스 | Ms-PL (OSI approved, 상용 OK, source 공개 의무 X, notice 보존 O) |
| 유지보수 활성도 | 1,025 commits, 1.7k stars, 585 dependents, 2026-04 release |
| **테마 별도 패키지** | `Dirkster.AvalonDock.Themes.Aero/Expression/Metro/VS2010/VS2013` 5종 분리 배포 — **본체 설치만으론 캡션/탭 시각 깨짐** |
| 알려진 이슈 | #283(Prism IsVisible), #368(Ctrl+F4 reopen), #258(WindowChrome), #27(2nd monitor), #38/#59/#131(ContentId NRE), xceed #859/#1033(AutoHide leak) |

## 3. 설계 / 방향

### 3.1 새 layout 트리 (AvalonDock)

```
DockingManager x:Name="dockManager"
    AllowMixedOrientation="True"
└─ LayoutRoot
    └─ LayoutPanel Orientation="Horizontal"
        ├─ LayoutAnchorablePane DockWidth="320">
        │   └─ LayoutAnchorable x:Name="explorerAnchor"
        │          ContentId="explorer" Title="Explorer"
        │          CanClose="False" CanHide="True" CanFloat="True" CanAutoHide="True">
        │
        ├─ LayoutPanel Orientation="Vertical">
        │   ├─ LayoutDocumentPane x:Name="workspaceDocs">
        │   │   └─ LayoutDocument x:Name="canvasDoc"
        │   │          ContentId="canvas" Title="Workspace"
        │   │          CanClose="False" CanFloat="False">  ← PR-1 spike 검증 항목
        │   ├─ LayoutAnchorablePane DockHeight="200">
        │   │   └─ LayoutAnchorable x:Name="simulationAnchor"
        │   │          ContentId="simulation" Title="Simulation">
        │
        └─ LayoutPanel Orientation="Vertical" DockWidth="280">
            ├─ LayoutAnchorablePane>
            │   └─ LayoutAnchorable x:Name="propertyAnchor"
            │          ContentId="properties" Title="Properties">
            ├─ LayoutAnchorablePane DockHeight="220">
            │   └─ LayoutAnchorable x:Name="historyAnchor"
            │          ContentId="history" Title="History">
            └─ LayoutAnchorablePane>
                └─ LayoutAnchorable x:Name="llmChatAnchor"
                       ContentId="llmchat" Title="LLM Chat"
                       CanFloat="True" CanHide="True"
                       IsVisible="False">  ← 기본 닫힘
```

설계 메모 (Content 매핑 표 — DataTemplate 활용):
| ContentId | Content 타입 | 매핑 방식 |
|---|---|---|
| `canvas` | `SplitCanvasContainer` | 직접 인스턴스 |
| `explorer` | `ExplorerPane` | 직접 인스턴스 |
| `properties` | `PropertyPanel` (DataContext={Binding PropertyPanel}) | 직접 인스턴스 |
| `history` | `HistoryPanel` | 직접 인스턴스 |
| `simulation` | `SimulationPanel` (DataContext={Binding Simulation}) | 직접 인스턴스 |
| `llmchat` | `LlmChatViewModel` → `LlmChatPanel` (MainWindow.xaml:25 의 DataTemplate 활용) | ContentControl + DataTemplate |

핵심 포인트:
- **`ContentId` 모든 항목 필수** (layout serialize/deserialize key). 누락 시 복원 NRE.
- `CanClose=false, CanHide=true`: X 버튼이 영구 close 가 아닌 "숨김" → ClosedPanelsBar 또는 메뉴로 복원.
- **테마 NuGet 별도**: PR-1 에서 Vs2013/Metro/Aero/Expression/자체 5안 비교 후 결정. 라이브러리 테마 채택 시 `Dirkster.AvalonDock.Themes.<X>` 별도 PackageReference 추가.

### 3.2 Layout 영속화 — 결정 트리 (5-케이스)

경로: `%LOCALAPPDATA%\Promaker\dock-layout.xml` (디렉토리 없으면 `Directory.CreateDirectory`).

**XML wrapper 구조** (XmlLayoutSerializer 가 `<LayoutRoot>` 직접 root 라 version metadata wrapping 필요):
```xml
<PromakerDockLayout Version="1.0">
  <LayoutRoot ...>...</LayoutRoot>  <!-- XmlLayoutSerializer 의 출력을 inner XML 로 -->
</PromakerDockLayout>
```
또는 별도 메타파일(`dock-layout.meta.xml`) 분리. PR-3 spike 시 결정.

**저장 시점** (정확한 line):
- `MainWindow.xaml.cs:108` `_llmChatDisposed = true;` **직후**, line 109 `await DisposeLlmChatAsync()` **직전**.
- idempotent: 두 번째 진입은 line 105 `if (_llmChatDisposed) return;` 에서 차단.
- 측정 자동화(`App.StartupAutoOpenLlm`) 모드는 저장 skip.

**복원 5-케이스**:
1. 파일 없음 → default (XAML 정의) 사용. `Log.Info("default layout 사용")`.
2. version 일치 → `XmlLayoutSerializer.Deserialize(stream)` + `LayoutSerializationCallback`. `Log.Info`.
3. version mismatch → `dock-layout.bak.xml` 백업 후 default. `Log.Warn`.
4. parse 실패 (try/catch 좁게) → `dock-layout.bak.xml` 백업 후 default. `Log.Error`.
5. **ContentId 미매칭 (가장 빈번한 일상 케이스)**: `LayoutSerializationCallback` 에서
   - 코드에 없는 ContentId → `e.Cancel = true` 로 항목 skip + `Log.Warn`
   - XML 에 없는 신규 ContentId → default 위치로 추가 (코드의 default layout 트리 참고)

**API 박제** (`Promaker.Shell.Persistence.DockLayoutPersistence` static class — namespace v5 통일):
```csharp
public enum RestoreOutcome { NotFound, Restored, VersionMismatch, ParseError }
public sealed record RestoreResult(RestoreOutcome Outcome, string? BackupPath);
public static RestoreResult Restore(DockingManager dock, string path);
public static void Save(DockingManager dock, string path);
```
로깅 책임은 호출자 측 (컨텍스트 풍부).

### 3.3 LlmChat 토글 — Single-Source-of-Truth 모델

배경: 단순 `IsLlmChatVisible ↔ IsVisible` 양방향 + bool gate 는 brittle. AvalonDock `LayoutAnchorable.IsVisible` 은 auto-hide / floating / minimize 합성 결과라 "사용자 의도(boolean)" 와 의미 어긋남.

**채택 모델** (V5 reviewer R3 안):
- **VM 의 `IsLlmChatVisible` 가 single-source-of-truth**.
- **VM → View 단방향 흐름**: `IsLlmChatVisible` PropertyChanged → `llmChatAnchor.Show()` / `llmChatAnchor.Hide()` (또는 `IsVisible = ...`).
- **역방향은 X 버튼 한 곳만**: `llmChatAnchor.Hiding` 이벤트 (사용자 명시 close) → `_vm.IsLlmChatVisible = false`.
- auto-hide / float 상태 변화는 VM 무관 (View 의 시각 상태일 뿐).
- 재진입 가드: View → VM 동기화 시 `_suppressVmSync` 플래그.

**Edge cases**:
- `LlmChatVm == null` (consent 거부 / autostart 미실행): `llmChatAnchor.IsEnabled = false` + Hide.
- `ENABLE_LLM=false`: anchor 자체를 layout 에서 제거 vs `Hide` 유지 → PR-2b spike 시 결정.

**Autostart race (v5 reviewer A5)**:
- `InitLlmAutostart()` 가 `Dispatcher.BeginInvoke(..., DispatcherPriority.Loaded)` 로 ToggleLlmChat 호출 → `IsLlmChatVisible=true` 트리거.
- 새 layout 에서는 `Window.Loaded` 의 layout 복원이 같은 dispatcher tick 에 anchor 객체를 교체할 수 있음.
- 조치: layout 복원 완료(LayoutSerializationCallback 종료) 후 anchor 재참조 + autostart flow 재검증. PR-2b 항목.

### 3.4 헤더(PanelHeader+HelpButton) 이전 — 3안 비교 후 결정

PanelHeader + HelpButton 패턴 3곳:
- `MainWindow.xaml:107-114` (History wrapper, "history")
- `Controls/Shell/ExplorerPane.xaml:135-138` ("explorer")
- `Controls/PropertyPanel/PropertyPanel.xaml:37-40` ("properties")

**3안 비교 (PR-4 spike 결정)**:

| 안 | 설명 | 장점 | 단점 |
|---|---|---|---|
| **A. AnchorableTitleTemplate** | `DockingManager.AnchorableTitleTemplate`/`DocumentHeaderTemplate`/`LayoutItemTemplate` 활용. attached property `DockCaption.HelpKey` | AvalonDock 표준 hook, caption 영역 일관 커스터마이즈 | attached property 가 layout serialize 시 직렬화 안 됨 → floating reparent 시 손실 가능. LayoutItemContainerStyle 추가 wiring 필요 |
| **B. UserControl 컴포지션 (R3 안, 권장)** | 기존 UserControl 본문 첫 줄에 PanelHeader Style 그대로 유지 (현 구조 보존) | CLAUDE.md "기존 함수 재활용 90점" 부합. 변경 최소. floating/dock 무관 (UserControl 내부) | LayoutAnchorable.Title 과 헤더 텍스트 중복 표시 가능 → `Title=""` 또는 헤더 hide 처리 필요 |
| C. ControlTemplate override | `LayoutAnchorableControl` ControlTemplate 통째 override | 가장 자유도 높음 | 4.x 의 hook 점이 wrapper 라 단일 override 로 caption 통제 어려움 (v5 reviewer A4 지적). 유지비 큼 |

**현 권장**: **B** (UserControl 컴포지션) — 변경 최소. PR-4 spike 결과로 확정.
SimulationPanel 은 자체 헤더 없이 TabControl 직접 노출 → 별도 처리 불필요.

### 3.5 Overlay 처리 — 책임 분리

| Overlay | 역할 | 현 구조 | 새 구조 |
|---|---|---|---|
| `WelcomeOverlay` | 자체 drop target (AllowDrop + 핸들러) | `Grid.ColumnSpan=7` | dockManager 와 형제 배치(같은 셀, ZIndex 차등). Welcome 표시 중에는 `dockManager.Visibility=Collapsed` (drop event 가로채임 회피) |
| `FileDragOverlay` | 순수 visual feedback | `Grid.RowSpan=3`, `IsHitTestVisible="False"` | 형제 배치 + ZIndex 최상위. dock 영역과 입력 충돌 없음 |

floating window 로 분리된 패널은 Window 단위 AllowDrop → file drop 안 됨. main window 만 drop 받도록 명문화.

### 3.6 KeyBinding / Floating 라우팅 / PropertyPane wiring

- `Window.InputBindings` (Ctrl+Z/Y/C/V/S/O/N, F2, Delete, F1, Ctrl+W) 는 floating window reparent 시 도달 안 함.
- 1차 (PR-2a): floating 시 동작 안 해도 무방한 단축키 그대로 유지. Ctrl+W + Ctrl+F4 vs AvalonDock close command 충돌 검증 (#368: Ctrl+F4 floating close 후 토글 재오픈 불가 — fallback: Avalon `LayoutItem.CloseCommand` InputGesture 제거 / Promaker KeyBinding 우선).
- 2차 (PR-5 후속): 필요 단축키만 `RoutedCommand` + `CommandManager.RegisterClassCommandBinding`.
- **PropertyPane wiring** (`MainWindow.xaml.cs:26` `_vm.FocusNameEditorRequested = PropertyPane.FocusNameEditorControl`):
  - visual tree 의존 → `PropertyPane.Loaded` 시점 늦은 해석.
  - **floating ↔ docked 전환 시 Loaded 재발화 → 핸들러 중복 등록 위험**: `_propertyPaneWired` 플래그 + `Unloaded` 해제. 또는 ad-hoc `Action` 슬롯 자체를 RoutedCommand 로 refactor.

## 4. 남은 할 일 (PR 단위)

### PR-1 — 환경 검증 + spike (작음)
- [ ] **AvalonDock 패키지 추가**: `Dirkster.AvalonDock` **4.74.1** (외부 검증) PackageVersion → `Apps/Promaker/Directory.Packages.props` + PackageReference → `Promaker.csproj`.
- [ ] **테마 패키지 결정 spike**: 5안 비교 (Vs2013 / Metro / Aero / Expression / 자체) — Promaker 자체 ResourceDictionary 와의 시각 정합성 + 별도 NuGet 추가 필요 여부. 결정된 안만 PackageReference 추가.
- [ ] **빌드 spike (1차 차단점)**: `dotnet restore` + `dotnet build` 통과 여부.
  - WPF 전용 → WinForms transitive 없음 예상. 모호 참조 0 기대.
  - fail → 원인별 분기.
- [ ] **transitive 검증** (DevExpress 학습 반영): `dotnet list package --include-transitive` 1회 + Central Package Management 충돌 (특히 `Microsoft.Xaml.Behaviors.Wpf`) 확인.
- [ ] **출력 size 영향 측정**: `bin\Debug\net9.0-windows\` 폴더 크기 변화 (DevExpress 비교 참고).
- [ ] **별도 spike 검증 항목** (임시 테스트 Window):
  - DockingManager + LayoutDocument + LayoutAnchorable 기본 동작 (dock/detach/마그넷/auto-hide)
  - LayoutDocument `CanFloat=False` + `CanClose=False` 4.74.1 실제 동작 (컨텍스트 메뉴/drag detach/단축키 close 차단)
  - LayoutAnchorable.IsVisible ↔ IsVisibleChanged raise 순서 (auto-hide/floating/minimize 합성)
  - LayoutSerializationCallback 미매핑 ContentId 의 4.74.1 기본 동작 (e.Cancel 없이도 안전한지)
  - Ctrl+W / Ctrl+F4 vs Avalon close command 충돌 케이스
  - WindowChrome 사용 여부 (#258)
  - **메모리 회귀 측정** (#859/#1033 reference): anchor float→re-dock 5회 cycle, WorkingSet delta
- [ ] **테마 매핑 spike**: 검증 키 5개 (`PrimaryBackgroundBrush`, `SecondaryBackgroundBrush`, `BorderBrush`, `AccentBrush`, `PanelHeaderBrush`).
- [ ] PR-1 결과를 본 todo §3 / §6 에 반영.

### PR-2a — `MainWindow` Grid → DockingManager 외곽 교체 (구조)
- [ ] `MainWindow.xaml` 외곽 Grid 통째 제거 → §3.1 트리.
- [ ] LlmChat anchor 는 임시로 항상 dock (토글 동기화는 PR-2b).
- [ ] `LayoutSerializationCallback` 핸들러 등록 (ContentId → Content + e.Cancel 패턴).
- [ ] `WelcomeOverlay` / `FileDragOverlay` 책임 분리 (§3.5).
- [ ] `SplitCanvasContainer` `MinHeight` 부여 (LayoutDocument 0-height 방지).
- [ ] `Ctrl+W` / `Ctrl+F4` vs AvalonDock close command 충돌 검증 + fallback 적용.
- [ ] `MainWindow.xaml.cs`: `LlmChatSplitter_DragDelta` 제거, `LlmChatSplitterCol`/`LlmChatPanelCol` 참조 제거.
- [ ] `FocusNameEditorRequested` wiring 을 `PropertyPane.Loaded` + `_propertyPaneWired` 가드 + `Unloaded` 해제로 이동.
- [ ] 빌드 통과 + 수동 시나리오 (Canvas/SimPanel/Property/History/Explorer dock·float·auto-hide·마그넷·Welcome·FileDrag).
- [ ] 단일 commit (revert 가능).

### PR-2b — LlmChat 동기화 + 측정 자동화
- [ ] §3.3 single-source-of-truth 모델 적용 (`MainWindow.xaml.cs` 의 PropertyChanged 핸들러 교체).
- [ ] `llmChatAnchor.Hiding` 이벤트 → VM 역방향 동기화 + `_suppressVmSync` 가드.
- [ ] `LlmChatVm == null` / `ENABLE_LLM=false` edge case (PR-2b spike 결정).
- [ ] **Autostart race (A5)**: layout 복원 완료 후 anchor 재참조 + autostart flow 재검증.
- [ ] 측정 자동화 시나리오: `App.StartupAutoOpenLlm` true 시 정상 close 확인.
- [ ] `Window_Closing` 에서 `dockManager.FloatingWindows.ToList().ForEach(close)` (collection 변경 예외 회피).

### PR-3 — Layout 저장/복원 + ThemeManager + Reset
- [ ] `Promaker.Shell.Persistence.DockLayoutPersistence` 헬퍼 추가 (§3.2 API 박제).
- [ ] `MainWindow.xaml.cs:108` 직후 + line 109 직전에 `Save` 호출.
- [ ] `Window.Loaded` 에서 §3.2 5-케이스 결정 트리 적용.
- [ ] XML wrapper `<PromakerDockLayout Version="1.0">` 또는 별도 메타파일 (PR-3 spike).
- [ ] log4net (C# 패턴): `Log.Info`(저장/복원/version), `Log.Warn`(version mismatch / 파일 없음 / ContentId 미매칭), `Log.Error`(parse 실패).
- [ ] **Reset Layout 메뉴 (필수, 정석)**: default layout XML 을 EmbeddedResource 로 박제 → `XmlLayoutSerializer.Deserialize` 즉시 적용 (재시작 불필요). `MainToolbarEtcContent` "기타" popup 에 "Layout 초기화" 추가.
- [ ] **ThemeManager dock theme 동기 (필수)**: `ThemeManager.cs` `ThemeChanged` 시 `dockManager.Theme` 또는 dock 머지 슬롯 갱신.
- [ ] **DPI / multi-monitor fallback 알고리즘**: `SystemParameters.VirtualScreen` 와 floating window `RestoreBounds` 교집합 면적 < 50% 시 main window 중앙 재배치 + `Log.Warn`. DPI 변환은 `VisualTreeHelper.GetDpi(this)` 보정.
- [ ] 측정 모드 시 복원 skip 검증.

### PR-4 — Caption 헤더 이전 (3개 패널 일괄)
- [ ] §3.4 의 3안 비교 후 채택 (현재 권장: B UserControl 컴포지션 보존).
- [ ] 채택안에 따른 wiring (안 B 라면 본문 그대로 두고 LayoutAnchorable.Title 만 빈 문자열 또는 헤더 시각 통일).
- [ ] **App.xaml DockResources 머지** (B10): floating window 도 자체 Window 라 `{StaticResource ...}` 도달성 보장 + floating 에서 caption/HelpButton 시각 검증.
- [ ] 캡션 우클릭 ContextMenu (AvalonDock 기본) vs HelpButton 영역 충돌 검증.
- [ ] DataTemplate (`MainWindow.xaml:25` LlmChatViewModel→LlmChatPanel) 가 LayoutAnchorable.Content 안에서 해석되는지 검증.

### PR-5 — KeyBinding 일반화 (선택, 후속)
- [ ] floating 시 라우팅 필요한 단축키만 `RoutedCommand` + `CommandManager.RegisterClassCommandBinding`.

## 5. 관련 파일 / 경로

### 수정 대상
- `Apps\Promaker\Promaker\MainWindow.xaml`, `MainWindow.xaml.cs`
- `Apps\Promaker\Directory.Packages.props` (Dirkster.AvalonDock 4.74.1 + 선택 테마 패키지)
- `Apps\Promaker\Promaker\Promaker.csproj`
- `Apps\Promaker\Promaker\Controls\Shell\ExplorerPane.xaml` (헤더 처리, PR-4)
- `Apps\Promaker\Promaker\Controls\PropertyPanel\PropertyPanel.xaml` (헤더 처리, PR-4)
- `Apps\Promaker\Promaker\Controls\Shell\HistoryPanel.xaml`
- `Apps\Promaker\Promaker\Controls\Shell\MainToolbarEtcContent.xaml` ("Layout 초기화" 메뉴, PR-3)
- `Apps\Promaker\Promaker\App.xaml` / `App.xaml.cs` (DockResources 머지, PR-4)
- `Apps\Promaker\Promaker\Presentation\ThemeManager.cs` (dock theme 동기, PR-3)
- 신규: `Apps\Promaker\Promaker\Themes\Theme.Controls.Dock.xaml` (기존 `Theme.*.xaml` 컨벤션 따름, v5 C4)
- 신규: `Apps\Promaker\Promaker\Shell\Persistence\DockLayoutPersistence.cs`

### 참조용 (수정 없음)
- `Controls\Canvas\SplitCanvasContainer.xaml`, `Controls\Simulation\SimulationPanel.xaml`, `Controls\Simulation\GanttChartControl.xaml`, `Controls\Llm\LlmChatPanel`
- `ViewModels\Shell\MainViewModel.LlmChat.cs`
- `Themes\Theme.Dark.xaml` / `Theme.Light.*` / `Theme.Controls.Forms.xaml` / `Theme.Icons.xaml`
- `Promaker.Tests` — 본 작업 영향 없음.
- `Promaker.main.sln` (untracked — git status 확인 후 처리, v5 C7)

## 6. 핵심 리스크 (요약, 본문 §3 참조)

1. **테마 NuGet 별도 패키지 누락 시 캡션/탭 시각 깨짐** → §3.1 / PR-1 첫 spike (v5 A1).
2. **AvalonDock 테마 매핑** (Promaker 자체 ResourceDictionary 와 정합) → §3.1 / PR-1.
3. **LlmChat single-source-of-truth 모델** → §3.3 / PR-2b.
4. **헤더 통합 안 결정** (3안 비교) → §3.4 / PR-4 spike.
5. **Overlay 책임 분리** → §3.5 / PR-2a.
6. **PropertyPane.Loaded 중복 등록** → §3.6 / PR-2a `_propertyPaneWired` 가드.
7. **측정 자동화 + floating + autostart race** → §3.3 / PR-2b.
8. **ContentId 미매칭 케이스** (가장 빈번) → §3.2 / PR-3 e.Cancel 패턴.
9. **AutoHide 메모리 leak** (#859/#1033) → PR-1 spike 회귀 측정.
10. **DPI / multi-monitor fallback** (50% 교집합 기준) → §6 알고리즘 명시 / PR-3.
11. **try/catch 자제** — layout parse 실패 한 곳만 좁게.
12. **빌드 확인 필수** — 각 PR 종료 시.
13. **사용자 명시 없이 git commit 금지** — CLAUDE.md.
14. **Ms-PL 라이선스** — notice 보존 의무 (LICENSE/NOTICE 갱신 — PR-1 또는 PR-3).

## 7. 반론 (수용하지 않은 reviewer 항목)

| 항목 | 반론 |
|---|---|
| v2 C5 `--classic-layout` feature flag | 거부. PR-2a 단일 revert commit 으로 충분. |
| v2 m26 `Visibility="Hidden"` | 거부. AvalonDock `IsVisible` 표준 사용. |
| v5 D AOT/Trim 호환성 | 현 plan 범위 외. 본 작업에서 trim publish 도입 없음. |
| v5 D 대안 라이브러리 비교 (Syncfusion/Telerik/ActiPro) | 결정 번복 사유 없음 (Ms-PL/.NET 9/transitive 0/유지보수 활성). 참고만. |

## 8. 진행 체크포인트 (이어받는 세션용)

- 현재까지 **plan v5 확정**. 코드 변경 **없음** (csproj/Packages.props revert 완료, 빌드 클린).
- 다음 액션: **PR-1 환경 검증** — `Dirkster.AvalonDock 4.74.1` PackageReference 추가 → 빌드 spike → 테마 패키지 결정 → 임시 spike 화면에서 §4 PR-1 항목 일괄 검증.
- 사용자 "PR-1 진행" 신호 후 코드 수정 시작.
- 각 PR 완료 시 빌드 통과 확인, 수동 시나리오 검증, 결과 보고. 사용자 명시 없이 git commit 금지.
- todo 진행 상태 갱신 규약: 각 체크박스 옆 완료 시 `(commit <hash>)` 추가.
