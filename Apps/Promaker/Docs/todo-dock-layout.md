# TODO — Promaker Dock Layout 도입 (AvalonDock 4.x 기반)

> Canvas 를 제외한 모든 창(Explorer, Properties, History, Simulation/Gantt, LLM Chat)에 Attach/Detach + 마그넷 dock 지원.
> **AvalonDock 4.74.x** (`Dirkster.AvalonDock`, Ms-PL) 기반.
>
> ⚠️ 본 문서 변경 이력:
> - v1: DevExpress `DockLayoutManager` 기반 plan 작성
> - v2: 외부 reviewer 5명 통합 검증 → 사실관계 정정 (MahApps 본체 미사용 / WebView2 LlmChat 무관 / PanelHeader 패턴 3곳)
> - v3: --review web 검증 + 본 환경 직접 검증 (DevExpress 24.1.7 로컬 feed 자동 등록 확인, 옵션 A 채택)
> - v4: PR-1 spike 결과로 DevExpress.Wpf.Docking 24.1.7 도입 시 138 곳 모호 참조 + WinForms transitive + size 비용. AvalonDock 4.x 로 변경.
> - v5: 5명 메타 reviewer (Generalist / 정확성 / 설계 / 영향범위 / 인터넷 검색) 통합 검증 반영. 핵심: 테마 NuGet 별도, 4.74.1 stable, ContentId e.Cancel 패턴, §3.4 헤더 API 3안 비교, §3.3 single-source-of-truth 모델, PR-2 분할, log4net C# 패턴(`Log.Info`) 정정.
> - **v9 (현재, 2026-05-13)**: B-1 (닫힌 anchor 복원 UI) Phase B 구현 working tree. 핵심 변경:
>   - **§3.1 Q2 단일 popup 통합 안 채택** — v7/v8 의 "메인 메뉴 + ▼ 드롭다운 조합" 명세에서 ▼ 드롭다운 분리 안 폐기. 닫힌 anchor 도 unchecked 로 자연 표시되어 동일 UI 한 곳에서 확인/토글 가능 → UX 단순화. todo §3.1 v7 절 자체에 v9 정정 marker + §8 B-1 review 처리 표 신설.
>   - **`MainViewModel.Dock.cs` partial 신규** — `[ObservableProperty] LayoutAnchorable?` 4종 (Explorer/Property/History/Simulation). MainToolbarEtcContent 가 별도 UserControl 이라 ElementName 으로 anchor 접근 불가 → VM mirror. LlmChat 은 IsLlmChatVisible SSOT 별도 + 별도 LLM 토글 버튼 이미 존재 → 본 메뉴 제외.
>   - **`MainToolbarEtcContent.xaml`** — 유틸 다음에 "보기" ToggleButton + Popup + 4 CheckBox (`IsChecked={Binding ...Anchor.IsVisible, Mode=TwoWay}` + `IsEnabled={Binding HasProject}`). LayoutAnchorable.IsVisible 자체가 INPC + setter → wrapper 불필요.
>   - **외부 review 결과** — Minor 1 (ToggleButton 자체 IsEnabled 없음) / Minor 2 (VM 이 View type 보관) / Refactoring (4건 명시 합당) 3건 모두 현재 안 유지 합당 판단. 추가 발견 1건 — anchor 가 floating 상태에서 hide → re-show 시 복귀 위치 (4.74.1 spike 미검증) 은 Phase B 수동 회귀에 추가.
> - **v8 (2026-05-13)**: PR-2a 외곽 교체 + PR-2b SSOT 흡수 commit (`1479f74`) + PR-2a/2b Phase A commit (`89a23e2`) + 외부 reviewer (Generalist / Logic / Design) 검토 처리 working tree. 핵심 변경:
>   - **§3.1 보조 anchor 동기화 명세 보강** — Welcome 모드 (`HasProject=false`) 시 `explorerAnchor`/`propertyAnchor`/`historyAnchor`/`simulationAnchor` 의 `IsVisible=false` 토글 (외부 reviewer M1 — 이전 v7 까지는 "나머지 anchor 들" 로 모호 표기, 실구현이 누락하여 회귀 발생). LlmChat 은 IsLlmChatVisible SSOT 별도라 제외.
>   - **§4 PR-2a / PR-2b 경계 정정 (외부 reviewer M2)** — v7 까지는 "PR-2a 는 minimal wiring 만, PR-2b 는 SSOT 완성" 분리. 실구현 (`1479f74` + `89a23e2`) 은 SSOT 가 PR-2a 안으로 흡수됨. revert 비현실 → 명세 정정. PR-2b 는 측정 자동화 검증 1건으로 축소.
>   - **§3.3 변수명 통일** — `_suppressVmSync` → `_suppressLlmChatSync` (실구현 명명 채택). v6 까지 todo 표기는 정정.
>   - **§3.3 false 분기 3속성 set** — VM → View 의 false 분기도 IsVisible/IsActive/IsSelected 동시 false (89a23e2 자가 검열 Major 2 수용 — 비대칭 set 시 다음 Show 사이클 첫 클릭 dead 회피).
>   - **§3.3 `Hiding` 의 `e.Cancel`** — 기본값 false 유지가 의도 (X 버튼이 hide 진행 + VM 만 동기화). reviewer 가 `LayoutSerializationCallback` 의 e.Cancel (§3.2 PR-3 영역) 과 혼동한 부분 반론.
>   - **§4 PR-2a 의 v7 신규 항목 7건 Phase A 처리 commit** — 빈 column 자동 collapse / floating Topmost=false / LlmChatVm null edge case / Autostart race / Window_Closing 순서 / PropertyPane 정석 refactor / dockManager x:Name placeholder. 닫힌 anchor 복원 UI (Q2) 만 Phase B 로 이월 (UI 신규 + 사용자 검증 필요).
>   - **§8 갱신** — commit hash 명시, 외부 reviewer 처리 표 추가, Phase B / PR-3 / PR-4 / PR-5 잔여 액션 명시화.
>   - **다른 파일 가리키는 참조 표기 정정** — 코드 주석 / 본 문서 안에서 다른 문서 / 파일 가리키는 참조는 `Apps/Promaker/Docs/todo-dock-layout.md §X.Y` 처럼 파일 경로 명시. "PR-3 이후 backlog", "todo §3.1" 등 모호 표기 회피.
> - v7: PR-1a 완료 + PR-1b spike 자동 검증 회신 + 사용자 Q1/Q2 결정 반영. 핵심 변경:
>   - **§2 끝부분 단정 6건 → "spike 결과 의존" → "spike 확정"** 으로 전환. 항목 1(Hiding), 3(Deserialize), 4(IsVisible round-trip PASS), 5(Theme namespace 충돌 없음), 6(x:Name 보존 FAIL → ReconcileAnchors 필수) 확정. 항목 2(5경로 차단)는 수동 검증 PR-2a 진입 시 별도.
>   - **§2 새 발견 사항 절** 신설 — F1 (AvalonDock 4.74.1 의 ClosedPanelsBar 자동 노출 X), F2 (모든 anchor hidden 시 빈 column 잔존), F3 (IsVisibleChanged 중복 raise), F4 (e.Cancel 미설정 시 unknown ContentId placeholder 보존), F5 (CloseCommand=ICommand → InputGestures 변경 불가), F6 (테마 brush key 5종 도달), **F7 (floating window 기본 TopMost — 사용자 결정으로 `Topmost=false` 정책)**.
>   - **§2 단정 정정** — `Hidden` 이벤트는 존재하지 않음 (todo v6 까지 "Hiding 또는 Hidden" 으로 표기한 부분 정정). VM 역동기화는 **`Hiding`** (cancelable) 또는 **`IsVisibleChanged`** 사용.
>   - **§3.1 Q1 결정 반영**: 모든 anchor hidden 시 column **자동 collapse** (DockWidth=0 set, 다른 anchor 가 dock 복귀 시 복원).
>   - **§3.1 Q2 결정 반영**: 닫힌 anchor 복원 UI = **메인 메뉴 → 보기(View) 하위 + 우측 상단 ▼ 드롭다운 조합**. AvalonDock `LayoutAnchorableExpanderControl` 자동 노출 안 됨 → 별도 wiring 필수.
>   - **§3.2 단정 강화**: `ReconcileAnchors()` 선택 → **필수** (x:Name FAIL 확정). `LayoutSerializationCallback` 핸들러는 unknown ContentId 에 **반드시 `e.Cancel=true`** 명시 (미설정 시 ghost placeholder 잔존). `Deserialize(string filepath)` 직접 사용 가능.
>   - **§3.3 가드 강화**: `IsVisibleChanged` 가 `Hide()` 1회 호출에 4회 raise (`True` 2번 + `False` 2번) — `_suppressVmSync` 가드는 **절대 필수**.
>   - **§3.6 정정**: `LayoutItem.CloseCommand : System.Windows.Input.ICommand` (RoutedUICommand 아님) → InputGestures 직접 변경 **불가**. Ctrl+F4/W 충돌 회피는 **KeyBinding 우선순위** 또는 **`Window.PreviewKeyDown` 가로채기** 로 변경.
>   - **§4 PR-1a 완료** + **PR-1b spike 결과 회수 완료**. PR-2a 신규 항목: 빈 column 자동 collapse 로직, 닫힌 anchor 복원 메뉴 + ▼ 드롭다운, callback 의 `e.Cancel=true` 명시.
>   - PR-1b 수동 미검증 항목 (Q3 — 5경로 차단 / Float cycle delta / WindowChrome) 은 **PR-2a 진입 시 본 코드 위에서 직접 검증** 으로 전환 (Spike Window 추가 검증 없음).
>   - todo §2 line 박제 모두 시점 표현 유지.
> - v6: `--inspect` 3명 메타 reviewer (Generalist / 정확성·버그 / 설계·구조) 합의 반영. 핵심 변경:
>   - **§2 에 Welcome/Open 이중 layout 구조 명시** (R1.C1 + R3.C3) — `MainWindow.xaml:59` `OpenLayout` / `:151` `WelcomeLayout` DataTrigger 토글 실재.
>   - §2 / §3.2 의 **line 번호 박제 제거** — `MainWindow.xaml.cs:124-125` 가 현재 `_llmChatDisposed=true; await DisposeLlmChatAsync();` 실제 위치 (v5 는 108/109 로 stale).
>   - **§3.1 에 Welcome 통합 정책 명시** + BusyOverlay 형제 배치 (§3.5).
>   - **§3.2 default layout SSOT = XAML 단일** (PR-3 의 EmbeddedResource 박제 폐기).
>   - **§3.2 atomic write 패턴** (`write-temp + File.Replace`) + `ReconcileAnchors` 헬퍼 명시.
>   - **§3.2 XML wrapper 보류 → 별도 메타파일 `dock-layout.meta.xml` 권장** (PR-1 결정).
>   - **§3.2 `RestoreOutcome` enum + `RestoreResult` record → `bool` 단순화** (호출처 1곳).
>   - **§3.3 SSOT 보강**: `IsActive`/`IsSelected`/`IsVisible` 3속성 동시 set, `_suppressVmSync` 양방향, autostart priority `ApplicationIdle`.
>   - **§3.4 안 B 단점 보강** (LayoutAnchorable.Title 식별자 역할) + 안 A 단점 재검증 (LayoutItemContainerStyle setter 패턴).
>   - **§3.6 정석 안 본 채택**: `FocusNameEditorRequested` ad-hoc Action 슬롯 → 기존 `FocusNameEditorCommand` RoutedCommand 흡수 (`_propertyPaneWired` 플래그는 fallback).
>   - **§4 PR-1 분할** (PR-1a 패키지/빌드 + PR-1b spike Window).
>   - **§4 PR-2a/2b 분리 정책 확정**: PR-2a 안에 anchor.IsVisible minimal wiring 포함 (단독 머지 시 사용자 회귀 회피) — 또는 stacked 머지.
>   - **§4 PR-3 default SSOT** (XAML) + Reset 은 layout 파일 삭제 + default 분기 재실행 방식.
>   - **§4 PR-3 ThemeManager 분리**: 신규 `DockThemeBridge` 클래스, ThemeManager 직접 수정 없음.
>   - **§4 PR-1 spike 항목 6개 추가** (R2 §5 권고): Hiding/Hidden 이벤트, 5경로 차단, Deserialize 오버로드, IsVisible round-trip, Theme property, anchor reference 보존.
>   - **AvalonDock 4.74.x API 단정 → "spike 결과 의존" 으로 약화**.
>   - log4net 메시지 영문화, Ms-PL 표현 약화, namespace `Promaker.Shell.Persistence` → **`Promaker.Persistence`**.

## 1. 작업 목표 (변경 없음)

`MainWindow.xaml` 의 `Grid + GridSplitter` 하드코딩 layout 을 AvalonDock `DockingManager` 로 교체하여:

- 패널 attach / detach (floating window)
- 마그넷 가이드 (Visual Studio 스타일 다이아몬드)
- Auto-hide (사이드 탭으로 접힘)
- Layout 자동 저장/복원 (+ 사용자 수동 reset)
- 향후 다중 Canvas tab 확장 가능

대상: `ExplorerPane`, `PropertyPanel`, `HistoryPanel`, `SimulationPanel`(Gantt/Status Monitor/Event Log), `LlmChatPanel`
**제외**: `SplitCanvasContainer`(Canvas) — `LayoutDocument` 로 두되 close/float 불가 (PR-1b spike 5경로 검증).

## 2. 배경 / 사실관계 (검증 완료)

### Promaker 의 실제 UI 스택
- **MahApps.Metro 본체 미사용**, `MahApps.Metro.IconPacks.Material` 만 의존 (아이콘 전용).
- 테마는 **Promaker 자체 ResourceDictionary**. 핵심 브러시: `PrimaryBackgroundBrush`, `SecondaryBackgroundBrush`, `TertiaryBackgroundBrush`, `BorderBrush`, `AccentBrush`, `PanelHeaderBrush`, `PrimaryTextBrush`, `SecondaryTextBrush`, `IconBlueBrush`. 자체 스타일 키 `PanelContainer`, `PanelHeader`, `HelpButton`, `DarkButton`.
- 테마 전환은 `Presentation/ThemeManager.cs` 의 `ThemeChanged` event (static).
- **log4net 사용 패턴 (C#)**: `private static readonly ILog Log = LogManager.GetLogger(typeof(...));` + `Log.Info/Warn/Error` (App.xaml.cs:14 등 8곳 확인).
- Promaker target: `net9.0-windows`. NuGet feed: `Solutions/nuget.config` (`nuget.org` 만, AvalonDock 도 nuget.org 정식).

### 현재 layout 구조 (`MainWindow.xaml`) — **Welcome/Open 이중 layout (v6 정정)**

`MainWindow.xaml` 의 본문은 **HasProject 에 따라 DataTrigger 로 토글되는 2 개의 독립 Grid 트리**:

#### OpenLayout (`MainWindow.xaml:59`, HasProject=true)
- 외곽 Grid: col 0 Explorer 320 / col 1 splitter 4 / col 2 Workspace * (row 0 Canvas, row 1 splitter, row 2 SimulationPanel 200) / col 3 splitter 4 / col 4 Right 280 (row 0 Property, row 1 splitter, row 2 History 220) / col 5 LlmChat splitter Thumb 4 / col 6 LlmChat (기본 width 0)
- LlmChat 토글 = `LlmChatPanelCol` width 0 ↔ 380 + splitter 0 ↔ 4 (Workspace `*` 폭 침식 회피용 Thumb DragDelta)

#### WelcomeLayout (`MainWindow.xaml:151`, HasProject=false)
- 안내 텍스트 + "새로 만들기" / "열기" 버튼 + 별도 `SimulationPanel` (없을 수도) + 별도 LLM column (`WelcomeLlmChatSplitterCol` / `WelcomeLlmChatPanelCol`).
- `MainWindow.xaml.cs:64-70` 의 `UpdateLlmChatColumnWidths` 가 **두 layout 의 column 을 동시에 갱신** → 전환 직후 폭 일관성 유지.

#### 공통 (양 layout 외부, 형제)
- `WelcomeOverlay`: `Grid.ColumnSpan=7` + 자체 AllowDrop/DragEnter/Over/Leave/Drop (자체 drop target)
- `FileDragOverlay`: `Grid.RowSpan=3`, `IsHitTestVisible="False"` (순수 visual feedback)
- **`BusyOverlay`** (`MainWindow.xaml:246`): `Grid.RowSpan=3`, `Panel.ZIndex=200`, `IsHitTestVisible="True"` — IsBusy 동안 dock 영역 포함 모든 입력 차단.

### Window_Closing 흐름 (`MainWindow.xaml.cs:109-129`, **v6 line 정정**)
1. `if (_llmChatDisposed) return;` (두 번째 진입 통과) — `:113`
2. 측정 모드 아니고 dirty 면 confirm — `:117`
3. `e.Cancel = true; _llmChatDisposed = true;` — `:123-124`
4. `await _vm.DisposeLlmChatAsync();` — `:125`
5. `Dispatcher.BeginInvoke(Close, Background)` — `:128`

> v5 가 `:108` `:109` 로 박제했으나 stale. v6 이후로는 **시점 표현**만 사용 ("`_llmChatDisposed=true` 직후, `await DisposeLlmChatAsync()` 직전") — line 번호 직접 박제 금지.

### LlmChat 토글 흐름
- `MainViewModel.LlmChat.cs` (partial): `[ObservableProperty] _isLlmChatVisible`, `[RelayCommand] ToggleLlmChat()` (Lazy `LlmChatVm` 생성), `InitLlmAutostart()` (`App.StartupAutoOpenLlm` 시 `Dispatcher.BeginInvoke(..., DispatcherPriority.Loaded)` 로 ToggleLlmChat 호출).
- `MainWindow.xaml.cs:32` 가 `IsLlmChatVisible` PropertyChanged 구독 → `UpdateLlmChatColumnWidths`.
- Toolbar `MainToolbarEtcContent.xaml:100` 가 `Command="{Binding ToggleLlmChatCommand}"`.
- `ENABLE_LLM=false` 환경에서는 ToggleLlmChat 자체가 no-op 가능 (consent 거부 / VM null) — 새 dock UI 의 컨텍스트 메뉴/X 버튼 노출도 같은 원칙 따라야 함.

### PropertyPane wiring (`MainWindow.xaml.cs:27`, **v6 정정 — M1**)
- 현재: `_vm.FocusNameEditorRequested = PropertyPane.FocusNameEditorControl;` (ad-hoc Action 슬롯, visual-tree 의존).
- **정석 안 본 채택 (v6)**: 기존 `FocusNameEditorCommand` RoutedCommand (`MainWindow.xaml:39` KeyBinding, `ExplorerPane.xaml:119` MenuItem, `ViewModels/Shell/MainViewModel.cs:155-168` Action 슬롯) 활용 → `PropertyPane` 이 `CommandBinding(Executed)` 등록 → visual tree reparent 무관.

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
| 라이선스 | Ms-PL (OSI approved, 상용 OK, source 공개 의무 X, notice 보존 O — NuGet 4.9.0+ 가 LICENSE 자동 포함하므로 publish 산출물에서 함께 배포되는지 확인만) |
| 유지보수 활성도 | 1,025 commits, 1.7k stars, 585 dependents, 2026-04 release |
| **테마 별도 패키지** | `Dirkster.AvalonDock.Themes.Aero/Expression/Metro/VS2010/VS2013` 5종 분리 배포 — **본체 설치만으론 캡션/탭 시각 깨짐** |
| 알려진 이슈 | #283(Prism IsVisible), #368(Ctrl+F4 reopen), #258(WindowChrome), #27(2nd monitor), #38/#59/#131(ContentId NRE), xceed #859/#1033(AutoHide leak) |

### AvalonDock 4.74.1 API spike 확정 결과 (v7 PR-1b)

본 표는 PR-1b spike (`Promaker.exe --dock-spike`) 자동 검증 회신 + 사용자 수동 검증 일부로 확정된 결과.

| # | 항목 | 결과 |
|---|---|---|
| 1 | `LayoutAnchorable` 이벤트 | **`Hiding` (EventHandler\<CancelEventArgs\>) 존재 + `e.Cancel` 가능. `Hidden` 이벤트는 존재하지 않음** (todo v6 까지 "Hiding 또는 Hidden" 표기 오기재 정정). 추가 이벤트: `Closed`, `Closing`, `FloatingPropertiesUpdated`, `IsActiveChanged`, `IsSelectedChanged`, `IsVisibleChanged`. |
| 2 | `LayoutDocument.CanFloat=False` 5경로 차단 | **수동 검증 PR-2a 진입 시 본 코드 위에서 직접 검증** (Spike Window 회신 보류). 캡션 drag / 우클릭 / double-click / Ctrl+drag / Ctrl+F4 / Ctrl+W 각각 회귀 시 fallback 필요. |
| 3 | `XmlLayoutSerializer.Deserialize` 오버로드 | **4종**: `Deserialize(Stream)`, `(TextReader)`, `(XmlReader)`, **`(String filepath)`** 직접 지원. `Deserialize(string filepath)` 사용으로 §3.2 atomic write 후 단순 호출 가능. |
| 4 | `IsVisible="False"` serialize round-trip | **PASS**. `llmChatAnchor.IsVisible=False` setup → Serialize → Deserialize 후 IsVisible 그대로 `False`. 별도 reconcile 단계 불필요. |
| 5 | `DockingManager.Theme` property + Promaker `ThemeManager` namespace 충돌 | **충돌 없음**. `DockingManager.Theme : AvalonDock.Themes.Theme` vs `Promaker.Presentation.ThemeManager` (static class). namespace 다름 + 의미 다름 → using alias 불필요. `dockManager.Theme = new Vs2013Theme();` 식 직접 set 가능. |
| 6 | `x:Name` field reference 보존 | **FAIL — `ReferenceEquals(field, descendent) = False`** (llmChat / explorer 둘 다). → §3.2 `ReconcileAnchors()` 헬퍼 **필수 확정** (선택 아님). callback 종료 직후 `dockManager.Layout.Descendents().OfType<LayoutAnchorable>()` 로 ContentId 기반 lookup 재할당. |

### v7 새 발견 사항 (PR-1b spike)

#### F1. ClosedPanelsBar 자동 노출 X (v7)
- AvalonDock 4.74.1 는 `CanClose=False CanHide=True` 인 anchor 에 대해 X 버튼 누름 시 hide 처리는 하지만, 닫힌 anchor 복원용 ClosedPanelsBar / 사이드 탭을 **자동으로 노출하지 않음**.
- todo v6 §3.1 의 "X 버튼이 hide → ClosedPanelsBar 또는 메뉴로 복원" 가정 중 ClosedPanelsBar 자동 노출 부분 정정.
- → **PR-2a 에서 닫힌 anchor 복원 UI 별도 구현 필수**. 사용자 결정 (Q2) = **메인 메뉴 → 보기(View) 하위 + 우측 상단 ▼ 드롭다운 조합**.

#### F2. 모든 anchor hidden 시 빈 column 잔존 (v7)
- `LayoutAnchorablePane(DockWidth=240)` 안의 anchor 가 모두 hidden 되면 anchor pane 은 사라지지만, **column 의 폭 240 영역이 그대로 잔존**.
- 시각상 빈 여백으로 보임.
- → **PR-2a 에서 자동 collapse 정책 구현 필수**. 사용자 결정 (Q1) = **모든 anchor hidden 시 `DockWidth=0` 자동 set** (다른 anchor 가 dock 복귀 시 복원). 구현 위치: `LayoutAnchorable.IsVisibleChanged` listener 에서 parent pane 의 children 모두 hidden 인지 확인 후 pane.DockWidth = new GridLength(0) 설정.

#### F3. `IsVisibleChanged` 중복 raise 패턴 (v7)
- spike trace 결과: `Hide()` 1회 호출 → **4회 raise** (IsVisible=True 2번 + IsVisible=False 2번). `Show()` 1회 호출 → **3회 raise** (IsVisible=True 3번).
- → §3.3 의 `_suppressVmSync` 가드는 **절대 필수**. 가드 없으면 VM ↔ View 역류 + 잠재 무한 재진입.

#### F4. `LayoutSerializationCallback` 미매핑 ContentId 동작 (v7)
- callback 핸들러에서 `e.Cancel` 을 **미설정** (기본값 false) 으로 두면, **4.74.1 가 unknown ContentId anchor 를 layout 안에 placeholder 로 보존** (`unknownX` anchor 가 deserialize 후에도 `Descendents().OfType<LayoutAnchorable>()` 에서 EXISTS).
- → §3.2 callback 에서 unknown ContentId 는 **반드시 `e.Cancel=true`** 명시.

#### F5. `LayoutItem.CloseCommand` 정정 (v7)
- type 은 `System.Windows.Input.ICommand` (**RoutedUICommand 아님**).
- → **InputGestures 직접 변경 불가**. todo v6 §3.6 의 "Avalon `LayoutItem.CloseCommand.InputGestures` 제거 가능 여부" 답: **불가능**.
- Ctrl+F4 / Ctrl+W 충돌 해소는 **KeyBinding 우선순위** (Promaker `Window.InputBindings` 가 먼저 매칭) 또는 **`Window.PreviewKeyDown` 가로채기** 로 우회.

#### F6. 테마 brush key 5종 모두 도달성 OK (v7)
- `PrimaryBackgroundBrush` / `SecondaryBackgroundBrush` / `BorderBrush` / `AccentBrush` / `PanelHeaderBrush` 모두 `Application.Current.Resources` 에서 SolidColorBrush 로 조회 가능 (`ThemeManager.ApplySavedTheme()` 통과 이후).
- → PR-4 의 DockResources 매핑 청신호.

#### F7. Floating window 기본 TopMost 동작 (v7, 사용자 수동 검증)
- 사용자 회신: "floating 상태에서 떼어낸 창은 항상 맨 앞인데, 뒤로 가게 할 수도 있어야 할 듯".
- AvalonDock 의 floating window (LayoutFloatingWindowControl / LayoutAnchorableFloatingWindowControl) 가 main window 위에 강제 z-order 로 노출되는 동작.
- → **PR-2a (또는 PR-3) 에서 floating window 의 z-order 정책 변경**: main window 클릭 시 floating window 가 뒤로 갈 수 있어야 함.
- 구현 후보:
  - (a) `LayoutAnchorableFloatingWindowControl.Topmost = false` setter — `dockManager.LayoutUpdated` 또는 `LayoutFloatingWindowControl` Style setter
  - (b) AvalonDock GitHub issue / option 으로 노출되어 있는지 spike 추가 검증 (PR-2a 진입 시 직접 확인)
- VS 표준 동작 = floating tool window 가 main window 뒤로 갈 수 있음 (`Topmost=false`). 사용자 의도와 일치.

## 3. 설계 / 방향

### 3.1 새 layout 트리 (AvalonDock)

#### Welcome/Open 통합 정책 (v6 C1)

**채택안: 안 A — Welcome 도 `LayoutDocument` 1개로 dock 안에 통합**

- HasProject=false 시 `welcomeDoc` 만 활성 (보조 anchor 4종 `explorerAnchor`/`propertyAnchor`/`historyAnchor`/`simulationAnchor` 의 `IsVisible=false`). `llmChatAnchor` 는 `IsLlmChatVisible` SSOT 가 별도라 토글 동기화 대상에서 제외 (외부 reviewer M1 으로 정정 — Welcome 모드 보조 anchor 미동기화 누락 수용).
- HasProject=true 시 `welcomeDoc` 은 `workspaceDocs.Children` 에서 detach + `canvasDoc` attach + 보조 anchor 4종 `IsVisible=true` 로 복원.
- 단순 정책: 사용자가 프로젝트 열린 상태에서 수동 hide 한 anchor 의 상태는 Welcome 토글로 잃을 수 있음 (HasProject 가 IsVisible 를 덮어씀). 사용자 수동 hide 상태 보존이 필요해지면 별도 저장 필드 도입 (본 문서 `Apps/Promaker/Docs/todo-dock-layout.md` §4 의 PR-3 이후 후속 작업으로 backlog 추가 예정).
- `WelcomeLayout` 의 별도 LLM column / SimulationPanel 제거 → 단일 dockManager 가 양 모드 통합.
- `UpdateLlmChatColumnWidths` 의 양 layout 동시 갱신 로직 제거.

**Plan B (보류)**: dockManager 와 WelcomeOverlay 가 진짜 형제 + HasProject=false 시 floating window 강제 dock-back. 안 A 가 spike 에서 막히면 fallback.

#### Layout 트리

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
        │   │   ├─ LayoutDocument x:Name="welcomeDoc"
        │   │   │      ContentId="welcome" Title="Welcome"
        │   │   │      CanClose="False" CanFloat="False"
        │   │   │      IsVisible="False">  ← HasProject=false 시만 활성 (PR-2a 에서 동기화)
        │   │   └─ LayoutDocument x:Name="canvasDoc"
        │   │          ContentId="canvas" Title="Workspace"
        │   │          CanClose="False" CanFloat="False">  ← PR-2a 진입 시 5경로 차단 수동 회귀 검증
        │   ├─ LayoutAnchorablePane DockHeight="200">
        │   │   └─ LayoutAnchorable x:Name="simulationAnchor"
        │   │          ContentId="simulation" Title="Simulation"
        │   │          CanClose="False" CanHide="True" CanFloat="True" CanAutoHide="True">
        │
        └─ LayoutPanel Orientation="Vertical" DockWidth="280">
            ├─ LayoutAnchorablePane>
            │   └─ LayoutAnchorable x:Name="propertyAnchor"
            │          ContentId="properties" Title="Properties"
            │          CanClose="False" CanHide="True" CanFloat="True" CanAutoHide="True">
            ├─ LayoutAnchorablePane DockHeight="220">
            │   └─ LayoutAnchorable x:Name="historyAnchor"
            │          ContentId="history" Title="History"
            │          CanClose="False" CanHide="True" CanFloat="True" CanAutoHide="True">
            └─ LayoutAnchorablePane>
                └─ LayoutAnchorable x:Name="llmChatAnchor"
                       ContentId="llmchat" Title="LLM Chat"
                       CanClose="False" CanHide="True" CanFloat="True" CanAutoHide="True"
                       IsVisible="False">  ← 기본 닫힘 (v7 spike PASS — round-trip OK)
```

설계 메모 (Content 매핑 — `ContentTemplateSelector` 또는 단일 dictionary SSOT):
| ContentId | Content 타입 | 매핑 방식 |
|---|---|---|
| `welcome` | `WelcomeView` (신규 또는 기존 WelcomeLayout 본문 추출) | DataTemplate 또는 직접 인스턴스 |
| `canvas` | `SplitCanvasContainer` | 직접 인스턴스 |
| `explorer` | `ExplorerPane` | 직접 인스턴스 |
| `properties` | `PropertyPanel` (DataContext={Binding PropertyPanel}) | 직접 인스턴스 |
| `history` | `HistoryPanel` | 직접 인스턴스 |
| `simulation` | `SimulationPanel` (DataContext={Binding Simulation}) | 직접 인스턴스 |
| `llmchat` | `LlmChatViewModel` → `LlmChatPanel` (MainWindow.xaml:25 의 DataTemplate 활용) | ContentControl + DataTemplate |

**SSOT 통합 (v6 R3.M6)**: ContentId → Content 매핑을 **단일 `Dictionary<string, FrameworkElement>` 필드 또는 ContentTemplateSelector** 로 통합 — §3.1 표 = §3.2 LayoutSerializationCallback lookup 의 single source.

핵심 포인트:
- **`ContentId` 모든 항목 필수** (layout serialize/deserialize key). 누락 시 복원 NRE.
- **모든 anchor 에 `CanClose=False CanHide=True` 일괄 명시 (v7 PR-1b 확정)**: 누락 시 AvalonDock 4.x 기본값 `CanClose=true` 가 적용되어 X 버튼이 hide 가 아닌 layout 영구 제거로 동작 (spike 회귀로 입증).
- **CanClose=false 라도 layout XML 에 IsVisible=false 가 저장되면 복원 시 hidden 상태로 시작** (v7 spike PASS 확정).
- **테마 NuGet 별도**: PR-1a 에서 Vs2013/Metro/Aero/Expression/자체 5안 비교 후 결정. 라이브러리 테마 채택 시 `Dirkster.AvalonDock.Themes.<X>` 별도 PackageReference 추가.
- **`Title` 처리는 §3.4 결정안 따름** — 안 B 채택 시 UserControl 헤더와 중복되므로 docked 상태에서만 Title 비우는 conditional, 안 A 채택 시 AnchorableTitleTemplate 가 통일 표시.

#### v7 신규 정책 — 빈 column 자동 collapse (Q1 결정)

AvalonDock 4.74.1 는 모든 anchor hidden 시 `LayoutAnchorablePane` 의 column 폭 (DockWidth) 을 자동으로 0 으로 만들지 **않음** (F2 발견). 사용자 결정으로 **자동 collapse** 정책 채택:

- 구현: `LayoutAnchorable.IsVisibleChanged` listener 1개 + parent pane 의 `Children.All(c => !c.IsVisible)` 확인 후 `pane.DockWidth = new GridLength(0)` 또는 `pane.IsVisible = false`.
- 복원 트리거: 사용자가 닫힌 anchor 를 복원 메뉴 / ▼ 드롭다운에서 다시 켜면 `pane.DockWidth = <기억된 width>` 복원.
- DockHeight 동일 패턴 (Simulation / History 의 가로 row).
- 헬퍼: `Promaker.Presentation.Dock.PaneCollapser` 신규 클래스 또는 `MainWindow.xaml.cs` 의 attached behavior.

#### v7 신규 정책 — 닫힌 anchor 복원 UI (Q2 결정) — **v9 정정**

ClosedPanelsBar 자동 노출 X (F1 발견). v7 까지의 명세 = **메인 메뉴 + 우측 상단 ▼ 드롭다운 조합**.

**v9 정정 (Phase B 구현 후)**: ▼ 드롭다운 항목을 **단일 popup 으로 통합**. 닫힌 anchor 도 unchecked 로 자연 표시되어 동일 UI 한 곳에서 확인 + 토글 가능 → ▼ 드롭다운 별도 노출의 정보 가치가 적음.

1. **Toolbar Etc section "보기" Popup (PR-2a/Phase B 완료)**: `MainToolbarEtcContent.xaml` 의 유틸 다음에 `ToggleButton` + `Popup`. Popup 안 4 CheckBox 명시 (Explorer/Properties/History/Simulation).
   - `IsChecked={Binding ExplorerAnchor.IsVisible, Mode=TwoWay}` (LayoutAnchorable.IsVisible 자체가 binding source — INPC + setter).
   - `IsEnabled={Binding HasProject}` (Welcome 모드 무의미 토글 차단).
   - LlmChat 은 본 메뉴 제외 — `IsLlmChatVisible` SSOT 분리 + Toolbar Etc 의 별도 LLM 토글 버튼 이미 보장.
2. **VM mirror** (`MainViewModel.Dock.cs` partial): `[ObservableProperty] LayoutAnchorable? ExplorerAnchor/PropertyAnchor/HistoryAnchor/SimulationAnchor`. MainWindow 생성자가 anchor 참조 4개 set. MainToolbarEtcContent 가 별도 UserControl 이라 ElementName 으로 anchor 접근 불가 → VM mirror 가 가장 짧음.
3. **HasProject 토글이 사용자 user-toggle 을 reset**: `SyncWelcomeCanvasVisibility` 가 4 anchor IsVisible 을 HasProject 로 강제 set → 보기 메뉴에서 사용자가 close 한 anchor 는 다음 file open 시 visible 로 복귀. todo §3.1 안 A (Welcome 통합) 와 일치하는 의도된 동작. 사용자 수동 hide 상태 보존 필요해지면 별도 저장 필드 도입 (`Apps/Promaker/Docs/todo-dock-layout.md` §4 PR-3 이후 backlog).
4. **▼ 드롭다운 별도 노출 폐기**: 단일 popup 통합으로 흡수. 추후 추가 가치 발견 시 부활 가능.

#### v7 신규 정책 — Floating window TopMost 해제 (F7)

AvalonDock 의 floating window 기본 동작이 main window 보다 항상 앞 노출 (사용자 검증). VS 표준 = `Topmost=false` 로 main window 뒤로 갈 수 있어야 함.

- 구현 후보 (PR-2a 또는 PR-3 진입 시 spike 1차 확인):
  - (a) `dockManager.LayoutItemContainerStyle` 안 setter: `<Style TargetType="LayoutFloatingWindowControl"><Setter Property="Topmost" Value="False"/></Style>`
  - (b) `dockManager.LayoutFloatingWindowControlCreated` 이벤트 (있다면) 에서 `e.Window.Topmost = false`
  - (c) `LayoutAnchorable.FloatingPropertiesUpdated` 이벤트 (spike 결과로 존재 확인) 에서 floating window 참조 추적 후 Topmost set
- PR-2a 진입 시 위 3개 중 작동하는 안 채택. 모두 실패 시 추가 spike.

### 3.2 Layout 영속화 — 결정 트리 (5-케이스)

경로: `%LOCALAPPDATA%\Promaker\dock-layout.xml` + **별도 메타파일 `dock-layout.meta.xml`** (디렉토리 없으면 `Directory.CreateDirectory`).

**v6 결정 — 별도 메타파일 안 채택 (R2.m3)**:
- `dock-layout.xml`: AvalonDock 원본 그대로 (wrapper 없음). `XmlLayoutSerializer.Deserialize(Stream)` 직접 사용 가능.
- `dock-layout.meta.xml`: `<PromakerDockLayoutMeta Version="1.0" SavedAt="..."/>` 만. version 감지 / 호환성 판정 전용.
- 두 파일이 짝. 한쪽만 존재 → case 4 (parse 실패) 로 처리.

**atomic write 패턴 (v6 R2.M2)**:
- `Save` 는 `dock-layout.xml.tmp` 에 write → `File.Replace(tmp, target, backup: dock-layout.bak.xml)` 로 atomic swap.
- 메타파일도 동일 패턴.
- `FileShare.None` Open 으로 다중 인스턴스 동시 쓰기 차단 (실패 시 `Log.Warn` 후 skip).

**저장 시점** (시점 표현 — v6 line 박제 제거):
- `Window_Closing` 의 `_llmChatDisposed=true` 설정 직후, `await DisposeLlmChatAsync()` 직전.
- idempotent: 두 번째 진입은 `if (_llmChatDisposed) return;` 에서 차단.
- 측정 자동화(`App.StartupAutoOpenLlm`) 모드는 저장 skip (disk 의 기존 layout modify 금지).

**복원 5-케이스**:
1. 파일 없음 (둘 중 하나라도) → default (XAML 정의) 사용. `Log.Info("dock layout: default in use")`.
2. version 일치 → **`XmlLayoutSerializer.Deserialize(string filepath)` 직접 호출** (v7 spike — 4종 오버로드 중 filepath 직접 지원) + `LayoutSerializationCallback`. `Log.Info("dock layout: restored")`. **callback 종료 직후 `ReconcileAnchors()` 호출 필수** (v7 spike FAIL 확정 — 아래 헬퍼 참조).
3. version mismatch (meta 와 코드 version 불일치) → `dock-layout.bak.xml` / `dock-layout.bak.meta.xml` 백업 후 default. `Log.Warn("dock layout: version mismatch, backed up")`.
4. parse 실패 (try/catch 좁게 — `XmlException` / `InvalidOperationException` 만, `Exception` 금지) → 백업 후 default. `Log.Error("dock layout: parse failed", ex)`.
5. **ContentId 미매칭 (가장 빈번한 일상 케이스)**: `LayoutSerializationCallback` 에서
   - 코드에 없는 ContentId → **`e.Cancel = true` 명시 필수 (v7 spike F4 — 미설정 시 unknown anchor 가 placeholder 로 layout 에 ghost 잔존)** + `Log.Warn("dock layout: unknown contentId={id}")`
   - XML 에 없는 신규 ContentId → default 위치로 추가 (코드의 default layout 트리 참고)

**ReconcileAnchors 헬퍼 — v7 필수 확정 (spike FAIL)**:
- `XmlLayoutSerializer.Deserialize` 후 `ReferenceEquals(_llmChatAnchor_field, descendent) = False` 로 확인됨 → x:Name field 가 stale 됨 (v7 spike 결과).
- callback 종료 직후 `dockManager.Layout.Descendents().OfType<LayoutAnchorable>()` 로 ContentId 기반 lookup 하여 `_llmChatAnchor`, `_explorerAnchor` 등 private field 재할당.
- SSOT 모델 (§3.3) 의 anchor 참조가 deserialize 무관하게 유효해짐.
- 본 헬퍼는 v7 부로 "권장" 이 아닌 **필수**. 호출 누락 시 §3.3 의 VM ↔ View 동기화가 정확히 stale anchor 를 set 하여 사용자 화면 무반응.

**API 박제 (v6 단순화 — R3.M5)** — `Promaker.Persistence.DockLayoutPersistence` static class (namespace `Shell` 제거):
```csharp
public static class DockLayoutPersistence
{
    public static bool Save(DockingManager dock, string layoutPath, string metaPath, string version);
    /// <returns>true: 복원 성공 / false: default 사용 (파일 없음 / version mismatch / parse 실패)</returns>
    public static bool Restore(DockingManager dock, string layoutPath, string metaPath, string expectedVersion);
}
```
- enum `RestoreOutcome` + record `RestoreResult` 폐기 (호출처 1곳, switch 분기 어차피 호출자).
- 백업 경로 결정 / version compare 는 클래스 내부. 호출자는 `if (!Restore(...))` 만 분기.
- 로깅은 클래스 내부 + 호출자 부가 컨텍스트.

### 3.3 LlmChat 토글 — Single-Source-of-Truth 모델 (v6 보강)

배경: 단순 `IsLlmChatVisible ↔ IsVisible` 양방향 + bool gate 는 brittle. AvalonDock `LayoutAnchorable.IsVisible` 은 auto-hide / floating / minimize 합성 결과라 "사용자 의도(boolean)" 와 의미 어긋남.

**채택 모델**:
- **VM 의 `IsLlmChatVisible` 가 single-source-of-truth**.
- **VM → View 단방향 흐름**: `IsLlmChatVisible` PropertyChanged →
  - true: `llmChatAnchor.IsVisible = true; llmChatAnchor.IsActive = true; llmChatAnchor.IsSelected = true;` (3속성 동시 set — v6 R1.M4).
  - false: 동일하게 3속성 모두 false (검열 Major 2 수용 — 비대칭 set 시 다음 Show 사이클 첫 클릭 dead 가능).
- **역방향은 X 버튼 한 곳만 — v7 spike 확정**: `llmChatAnchor.Hiding` 이벤트 (`EventHandler<CancelEventArgs>`) 사용 → `_vm.IsLlmChatVisible = false`. `e.Cancel` 은 기본값 false 유지 (X 버튼이 hide 진행 + VM 만 동기화). (`Hidden` 이벤트는 AvalonDock 4.74.1 에 존재하지 않음 — v6 까지의 "Hiding 또는 Hidden" 표기는 정정됨.)
- auto-hide / float 상태 변화는 VM 무관 (View 의 시각 상태일 뿐).

**재진입 가드 — 양방향 절대 필수 (v7 spike F3 입증)**:
- spike trace: `Hide()` 1회 호출 → `IsVisibleChanged` 4회 raise (True 2번 + False 2번). `Show()` 1회 → 3회 raise (True 3번). 가드 없으면 VM 으로 4회 역류 → 무한 재진입 위험.
- `_suppressLlmChatSync` 플래그 (실구현 명명, v6 까지 todo 의 `_suppressVmSync` 표기는 정정).
- VM → View 진입 시 `_suppressLlmChatSync = true` wrap (Show/Hide 가 다시 이벤트 raise 해도 VM 으로 역류 X).
- View → VM 진입 시 동일 wrap.

**Edge cases (PR-2a 통합 구현 완료, commit `89a23e2`)**:
- `LlmChatVm == null` (consent 거부 / autostart 미실행): `SyncLlmChatAnchorFromVm` 의 `show = _vm.IsLlmChatVisible && _vm.LlmChatVm != null && _vm.IsLlmEnabled` AND 가드. anchor.IsVisible/IsActive/IsSelected 모두 false 로 fall-through.
- `ENABLE_LLM=false`: 동일 AND 가드로 hide. anchor 자체는 layout 에 남김 (Phase B 복원 메뉴에서 비활성/숨김 표시).

**Autostart race 해결 (v6 R3.C2)**:
- `InitLlmAutostart()` 의 `Dispatcher.BeginInvoke(..., DispatcherPriority.Loaded)` → **`DispatcherPriority.ApplicationIdle` 로 변경** → `Window.Loaded` 의 layout 복원 + ReconcileAnchors 완료 후 실행 보장.
- ReconcileAnchors 후 anchor 재참조한 상태에서 VM → View 단방향 흐름이 다시 한 번 reconcile (idempotent).

### 3.4 헤더(PanelHeader+HelpButton) 이전 — 3안 비교 후 결정 (v6 단점 보강)

PanelHeader + HelpButton 패턴 3곳:
- `MainWindow.xaml:107-114` (History wrapper, "history")
- `Controls/Shell/ExplorerPane.xaml:135-138` ("explorer")
- `Controls/PropertyPanel/PropertyPanel.xaml:37-40` ("properties")

**3안 비교 (PR-4 spike 결정 — v6 보강)**:

| 안 | 설명 | 장점 | 단점 |
|---|---|---|---|
| A. AnchorableTitleTemplate | `DockingManager.AnchorableTitleTemplate`/`DocumentHeaderTemplate`/`LayoutItemTemplate` 활용. attached property `DockCaption.HelpKey` | AvalonDock 표준 hook, caption 영역 일관 커스터마이즈. **attached property 가 LayoutItemContainerStyle setter 로 들어가면 매 reparent 마다 자동 재적용 → serialize 한계 무력화 가능 (PR-4 spike 검증)** | LayoutItemContainerStyle 추가 wiring 필요. 안 B 보다 변경 면적 큼 |
| **B. UserControl 컴포지션 (현 권장, 단점 보강)** | 기존 UserControl 본문 첫 줄에 PanelHeader Style 그대로 유지 | CLAUDE.md "기존 함수 재활용 90점" 부합. 변경 최소. floating/dock 무관 (UserControl 내부) | **LayoutAnchorable.Title 은 auto-hide 사이드 탭 / floating window caption / closed panels bar 의 식별자 역할** → `Title=""` 비우면 시각 깨짐. **conditional 필요**: docked 상태 = UserControl 헤더 + Title 비움, auto-hide/floating = Title 노출 + UserControl 헤더 hide |
| C. ControlTemplate override | `LayoutAnchorableControl` ControlTemplate 통째 override | 가장 자유도 높음 | 4.x 의 hook 점이 wrapper 라 단일 override 로 caption 통제 어려움 (v5 reviewer A4 지적). 유지비 큼 |

**현 권장**: **B** (UserControl 컴포지션) — 변경 최소. 단 conditional 처리는 PR-4 spike 에서 구체화. 안 A 의 setter 패턴 검증 결과 우월하면 안 A 로 전환.
SimulationPanel 은 자체 헤더 없이 TabControl 직접 노출 → 별도 처리 불필요.

### 3.5 Overlay 처리 — 책임 분리 (v6 BusyOverlay 추가)

| Overlay | 역할 | 현 구조 | 새 구조 |
|---|---|---|---|
| `WelcomeOverlay` | 자체 drop target (AllowDrop + 핸들러) | `Grid.ColumnSpan=7` | dockManager 와 형제 배치(같은 셀, ZIndex 차등). Welcome 모드는 안 A (LayoutDocument 통합) 채택으로 overlay 자체 불필요할 수 있음 — PR-2a 결정 |
| `FileDragOverlay` | 순수 visual feedback | `Grid.RowSpan=3`, `IsHitTestVisible="False"` | 형제 배치 + ZIndex 최상위. dock 영역과 입력 충돌 없음 |
| **`BusyOverlay` (v6 추가)** | IsBusy 동안 모든 입력 차단 | `Grid.RowSpan=3`, `Panel.ZIndex=200`, `IsHitTestVisible="True"` | 형제 배치 + ZIndex 200 (FileDragOverlay 와 별도 stacking). **IsBusy=true 동안 `dockManager.IsHitTestVisible=false` 명시** — floating window 가 BusyOverlay 위로 떠오를 가능성은 PR-2a spike 에서 검증 (떠오르면 floating window 일괄 `IsEnabled=false`) |

**floating window AllowDrop 정책 (v6 R3.m6)**: floating window 는 별도 Window 인스턴스 → file drop 받지 않도록 `LayoutItemContainerStyle` setter 또는 `LayoutFloatingWindowControl.AllowDrop=False` 일괄 wiring. main window 만 drop receiver.

### 3.6 KeyBinding / Floating 라우팅 / PropertyPane wiring (v7 CloseCommand 정정)

- `Window.InputBindings` (Ctrl+Z/Y/C/V/S/O/N, F2, Delete, F1, Ctrl+W) 는 floating window reparent 시 도달 안 함.
- **v7 spike F5 확정**: `LayoutItem.CloseCommand : System.Windows.Input.ICommand` (RoutedUICommand 아님) → **InputGestures 직접 변경 불가**. v6 §3.6 의 "InputGesture 제거 가능 여부" 답: **불가능**.
- 1차 (PR-2a): floating 시 동작 안 해도 무방한 단축키 그대로 유지. **Ctrl+W + Ctrl+F4 vs AvalonDock close command 충돌 회피 우회안**:
  - (a) `Window.InputBindings` 의 Ctrl+W KeyBinding 이 AvalonDock 의 close command 보다 먼저 매칭되는지 확인 (WPF 의 KeyBinding 우선순위).
  - (b) `Window.PreviewKeyDown` 핸들러에서 Ctrl+F4 / Ctrl+W 캐치 후 `e.Handled=true` 로 AvalonDock 까지 propagate 차단.
  - (c) PR-2a 진입 시 위 두 안을 코드 위에서 직접 검증, 작동하는 안 채택.
- 2차 (PR-5 후속): 필요 단축키만 `RoutedCommand` + `EventManager.RegisterClassHandler` (floating window 의 Window-level routing). `CommandManager.RegisterClassCommandBinding(typeof(Window), ...)` 는 Promaker 의 모든 Window 영향 → 회피.

**PropertyPane wiring — 정석 안 본 채택 (v6 M1, CLAUDE.md "정석 우선" 원칙)**:
- 기존 `FocusNameEditorCommand` RoutedCommand (`MainWindow.xaml:39` `<KeyBinding Key="F2" Command="{Binding FocusNameEditorCommand}"/>`, `ExplorerPane.xaml:119` MenuItem, `MainViewModel.cs:155-168` `FocusNameEditorRequested` Action 슬롯) 활용.
- `PropertyPane` 의 `CommandBindings` 에 `FocusNameEditorCommand` 의 `Executed` 핸들러 등록 → 내부에서 `FocusNameEditorControl()` 호출.
- `MainWindow.xaml.cs:27` 의 `_vm.FocusNameEditorRequested = PropertyPane.FocusNameEditorControl;` ad-hoc Action 슬롯 폐기.
- visual tree reparent (floating ↔ docked / auto-hide / tab close-reopen) 무관 → `_propertyPaneWired` 가드 불필요.
- **fallback**: 정석 안에 막히면 `_propertyPaneWired` 플래그 + `Unloaded` 해제 패턴 (v5 안 그대로). 단, ViewModel 의 staled `FocusNameEditorRequested` reference 위험 별도 처리 필요.

## 4. 남은 할 일 (PR 단위)

### PR-1a — 환경 검증 (작음, 빌드만) — ✅ 완료 (uncommitted, v7)
- [x] **AvalonDock 패키지 추가**: `Dirkster.AvalonDock` **4.74.1** PackageVersion → `Apps/Promaker/Directory.Packages.props` + PackageReference → `Promaker.csproj`. ✓
- [ ] **테마 패키지 결정**: PR-1b spike 의 brush key 5종 모두 도달성 확인 → **자체 ResourceDictionary 유지 안 채택 가능**. AvalonDock Themes.<X> NuGet 추가 없이 PR-4 의 DockResources 머지 만으로 통합 가능 여부는 PR-4 진입 시 최종 확정.
- [x] **빌드 spike**: `dotnet build` 0 경고/0 오류 통과. ✓
- [x] **transitive 검증**: WinForms/System.Drawing 0건 / `CommunityToolkit.Mvvm` 충돌 0 / `MahApps.Metro.IconPacks.Material` 충돌 0 / `Microsoft.Web.WebView2` 충돌 0 / `Microsoft.Xaml.Behaviors.Wpf` 미유입. ✓
- [x] **출력 size 영향**: baseline 59,170,758 B → +AvalonDock 59,802,355 B (+617 KB, +1.07%). AvalonDock.dll 510 KB + locale satellite 4개. ✓
- [ ] 단일 commit (uncommitted — 사용자 명시 신호 대기).

### PR-1b — Spike Window — ✅ 자동 검증 완료 (uncommitted, v7)
**파일**: `Apps/Promaker/Promaker/Spike/DockSpikeWindow.xaml(.cs)` (PR-2a 진입 시 제거 예정) + `App.xaml.cs` 의 `--dock-spike` args 분기.

- [x] **[3] LayoutAnchorable 이벤트** — `Hiding (EventHandler<CancelEventArgs>)` 존재, e.Cancel 가능. **`Hidden` 이벤트는 없음** (todo v6 오기재 정정).
- [x] **[4] IsVisible round-trip** — PASS (llmChat.IsVisible=False 보존).
- [x] **[5] Deserialize 오버로드** — 4종 (Stream / TextReader / XmlReader / string filepath).
- [x] **[6] x:Name 보존** — FAIL (ReferenceEquals=False). ReconcileAnchors() 필수 확정.
- [x] **[7] Theme property + namespace 충돌** — 충돌 없음.
- [x] **[8] CloseCommand 정정** — ICommand (RoutedUICommand 아님). InputGestures 직접 변경 불가.
- [x] **[337] IsVisibleChanged 중복 raise** — Hide 1회 → 4회 raise. Show 1회 → 3회 raise. `_suppressVmSync` 가드 필수.
- [x] **[338] e.Cancel 미설정 시 unknown ContentId** — placeholder 보존. callback 에서 명시 `e.Cancel=true` 필수.
- [x] **[테마] brush key 5종 도달성** — 모두 SolidColorBrush OK.
- [x] **F1 ClosedPanelsBar 미노출** — 사용자 회신으로 확정. PR-2a 의 복원 UI (메뉴+▼) 필수.
- [x] **F2 빈 column 잔존** — 사용자 회신으로 확정. PR-2a 의 자동 collapse 필수.
- [x] **F7 floating TopMost** — 사용자 회신으로 확정. `Topmost=false` 정책.
- [ ] **PR-2a 진입 시 본 코드 위에서 직접 검증할 수동 항목** (Spike Window 추가 검증 없음):
  - LayoutDocument 5경로 차단 (캡션 drag / 우클릭 / double-click / Ctrl+drag / Ctrl+F4 / Ctrl+W) 회귀.
  - Float 5회 cycle WorkingSet delta (메모리 leak #859/#1033).
  - floating window WindowChrome 사용 여부 (#258).
  - dock 마그넷 / auto-hide 정상 동작.
- [ ] 단일 commit (uncommitted — 사용자 명시 신호 대기).

### PR-2a — `MainWindow` Grid → DockingManager 외곽 교체 (구조) + LlmChat SSOT 통합

**경계 정정 (외부 reviewer M2 수용)**: v6 까지 todo 는 PR-2a 를 "minimal wiring 만, PR-2b 는 SSOT 완성" 으로 분리했으나, 실구현은 1479f74 + 89a23e2 에서 PR-2b 의 SSOT (3속성 set + `_suppressLlmChatSync` 양방향 가드 + `Hiding` 역동기화 + LlmChatVm null edge case + Autostart race + Window_Closing 순서) 가 PR-2a 안으로 통합됨. revert 비현실 → 명세 정정. PR-2b 의 잔여 항목은 측정 자동화 검증 1개로 축소.

- [x] `MainWindow.xaml` 외곽 Grid 통째 제거 → §3.1 트리 (Welcome/Canvas 통합 안 A). (commit 1479f74)
- [x] `welcomeDoc` ↔ `canvasDoc` 의 `IsVisible` 을 `HasProject` 에 동기화 — LayoutDocument.IsVisible read-only 라 `workspaceDocs.Children` Add/Remove 동적 관리로. (commit 1479f74)
- [x] **외부 reviewer M1 — Welcome 모드 보조 anchor 4종 동기화** (`explorerAnchor`/`propertyAnchor`/`historyAnchor`/`simulationAnchor` 의 `IsVisible` 를 `HasProject` 와 토글). LlmChat 은 IsLlmChatVisible SSOT 별도라 제외.
- [x] **LlmChat anchor SSOT 통합**: `IsLlmChatVisible` PropertyChanged → 3속성 동시 set + `_suppressLlmChatSync` 양방향 가드 + `Hiding` 역동기화 + LlmChatVm null/IsLlmEnabled=false edge case. (commit 1479f74 + 89a23e2)
- [ ] `LayoutSerializationCallback` 핸들러 등록 (ContentId → Content + e.Cancel 패턴, 단일 dictionary SSOT). → **PR-3 으로 이월** (Serialize/Deserialize 도입 시).
- [ ] `ReconcileAnchors()` 헬퍼 도입 (callback 종료 직후 호출). → **PR-3 으로 이월**.
- [x] `WelcomeOverlay` / `FileDragOverlay` / `BusyOverlay` 형제 배치 (§3.5). WelcomeOverlay 의 drop target 역할 → Window 레벨 핸들러로 흡수 (FileDragOverlay 의 watchdog 그대로). (commit 1479f74)
- [x] `SplitCanvasContainer` `MinHeight=120` 부여. (commit 1479f74)
- [ ] `Ctrl+W` / `Ctrl+F4` vs AvalonDock close command 충돌 fallback. → **Phase B 또는 PR-5 후속**. (anchor 모두 CanClose=False 라 close command 자체 비활성 — 현 코드에서 충돌 없는 것으로 보임. 수동 회귀 후 결정.)
- [x] `MainWindow.xaml.cs`: `LlmChatSplitter_DragDelta` 제거, `LlmChatSplitterCol`/`LlmChatPanelCol` 참조 제거, `UpdateLlmChatColumnWidths` 제거. (commit 1479f74)
- [x] **PropertyPane wiring 정석 refactor (v6 M1)**: `_vm.FocusNameEditorRequested = PropertyPane.FocusNameEditorControl;` 제거. PropertyPanel.xaml.cs 의 Loaded/Unloaded 에서 자가 등록/해지 (`Application.Current.MainWindow.DataContext as MainViewModel`). RoutedCommand 안 대신 fallback 안 채택 — RelayCommand 라 CommandBinding(Executed) 가 부적합.
- [x] 빌드 통과 (Phase A commit 시점 0/0).
- [ ] **수동 시나리오 검증** (Canvas/SimPanel/Property/History/Explorer dock·float·auto-hide·마그넷·Welcome 전환·FileDrag·BusyOverlay). → **Phase B**.
- [ ] **PR-2a/PR-4 사이 헤더 중복 표시 기간 회피**: LayoutAnchorable.Title 정책. → **PR-4 와 stacked 머지** (Phase B 까지 임시 노출 OK).
- [x] **v7 빈 column 자동 collapse 정책 (Q1)**: 5 anchor IsVisibleChanged listener 1개 핸들러 `OnAnchorIsVisibleChanged` — explorerPane / simulationPane / historyPane / rightPanel 의 DockWidth/DockHeight 를 default ↔ 0 toggle. rightPanel 은 property/history/llm 셋 다 hidden 일 때만 collapse.
- [x] **v7 닫힌 anchor 복원 UI (Q2) — v9 단일 popup 통합 안 채택**: `MainToolbarEtcContent.xaml` 의 유틸 다음에 "보기" ToggleButton + Popup + 4 CheckBox (Explorer/Properties/History/Simulation). `MainViewModel.Dock.cs` 신규 partial 로 4 anchor mirror (`[ObservableProperty] LayoutAnchorable?`). ▼ 드롭다운 분리 안 폐기 — 단일 popup 으로 흡수.
- [ ] **v7 callback e.Cancel=true 명시 (F4)**: unknown ContentId 발생 시. → **PR-3 으로 이월**.
- [x] **v7 floating window Topmost=false (F7)**: 안 (a) `DockingManager.Resources` 의 `LayoutAnchorableFloatingWindowControl` / `LayoutDocumentFloatingWindowControl` Style setter (Topmost=False) 적용. → **Phase B 수동 검증 필요** (Style 매핑 동작 여부).
- [ ] **v7 PR-1b 수동 항목 본 코드 위 회귀 검증**: 5경로 차단 / WindowChrome 사용 여부 / Float cycle 메모리 delta. → **Phase B**.
- [x] commit 분할: Phase A (이번 turn) + Phase B (UI 추가).

### PR-2b — 측정 자동화 시나리오 검증 (PR-2a 흡수 후 잔여 1건)

**경계 정정 (외부 reviewer M2 수용)**: SSOT 모델 / Hiding / LlmChatVm null / Autostart race / Window_Closing 순서 항목들은 PR-2a 의 1479f74 + 89a23e2 commit 안으로 흡수됨. PR-2b 는 측정 자동화 수동 회귀로 축소.

- [ ] 측정 자동화 시나리오: `App.StartupAutoOpenLlm` true 시 정상 close 확인. → **Phase B 수동 검증**.

### PR-3 — Layout 저장/복원 + DockThemeBridge + Reset
- [ ] **`Promaker.Persistence.DockLayoutPersistence`** 헬퍼 추가 (§3.2 단순화 API).
- [ ] atomic write (`write-temp + File.Replace`) + `FileShare.None` 적용.
- [ ] `Window_Closing` 의 `_llmChatDisposed=true` 직후 + `DisposeLlmChatAsync` 직전에 `Save` 호출 (시점 표현, line 박제 금지).
- [ ] `Window.Loaded` 에서 §3.2 5-케이스 결정 트리 적용.
- [ ] 별도 메타파일 `dock-layout.meta.xml` 형식 확정 + version 1.0 박제.
- [ ] log4net 영문 메시지 (`Log.Info("dock layout: default in use")` 등). `log4net.config` 의 `<encoding value="utf-8" />` 명시 확인.
- [ ] **Reset Layout 메뉴 (default SSOT = XAML)**: layout 파일 삭제 + default 분기 재실행 방식 (재시작 불필요하면 `dockManager.Layout = <코드의 default LayoutRoot 인스턴스>` 또는 동등 API). EmbeddedResource 박제 폐기.
- [ ] **DockThemeBridge 신규 클래스 (v6 R3.M2)**: `Promaker.Presentation.Dock.DockThemeBridge` — MainWindow ctor 에서 `new DockThemeBridge(dockManager).Attach()` → 내부에서 `ThemeManager.ThemeChanged` 구독 → `dockManager.Theme` 갱신. **ThemeManager 직접 수정 없음**.
- [ ] **DPI / multi-monitor fallback 알고리즘**: `SystemParameters.VirtualScreen` 와 floating window `RestoreBounds` 교집합 면적 < 50% 시 main window 중앙 재배치 + `Log.Warn`. DPI 변환은 `VisualTreeHelper.GetDpi(this)` 보정.
- [ ] 측정 모드 시 복원 skip 검증 (disk modify 0).

### PR-4 — Caption 헤더 이전 (3개 패널 일괄)
- [ ] §3.4 의 3안 비교 후 채택. 안 B 채택 시 conditional 처리 (docked = UserControl 헤더 + Title 비움, auto-hide/floating = Title 노출 + UserControl 헤더 hide). 안 A 가 setter 패턴 spike 에서 우월하면 안 A 전환.
- [ ] 채택안에 따른 wiring.
- [ ] **App.xaml DockResources 머지**: floating window 도 자체 Window 라 `Application.Current.Resources` 도달성 보장. **`DynamicResource` 강제** (StaticResource 금지 — 테마 전환 시 floating window 의 resource 갱신 보장).
- [ ] 캡션 우클릭 ContextMenu (AvalonDock 기본) vs HelpButton 영역 충돌 검증.
- [ ] DataTemplate (`MainWindow.xaml:25` LlmChatViewModel→LlmChatPanel) 가 LayoutAnchorable.Content 안에서 해석되는지 검증.

### PR-5 — KeyBinding 일반화 (선택, 후속)
- [ ] floating 시 라우팅 필요한 단축키만 `RoutedCommand` + `EventManager.RegisterClassHandler(typeof(Window), Window.KeyDownEvent, ...)` 로 좁게 (모든 Promaker Window 영향 회피).

## 5. 관련 파일 / 경로

### 수정 대상
- `Apps\Promaker\Promaker\MainWindow.xaml`, `MainWindow.xaml.cs`
- `Apps\Promaker\Directory.Packages.props` (Dirkster.AvalonDock 4.74.1 + 선택 테마 패키지)
- `Apps\Promaker\Promaker\Promaker.csproj`
- `Apps\Promaker\Promaker\Controls\Shell\ExplorerPane.xaml` (헤더 처리, PR-4)
- `Apps\Promaker\Promaker\Controls\PropertyPanel\PropertyPanel.xaml` (헤더 처리, PR-4 + CommandBindings 추가 PR-2a)
- `Apps\Promaker\Promaker\Controls\Shell\HistoryPanel.xaml`
- `Apps\Promaker\Promaker\Controls\Shell\MainToolbarEtcContent.xaml` ("Layout 초기화" 메뉴, PR-3)
- `Apps\Promaker\Promaker\App.xaml` / `App.xaml.cs` (DockResources 머지, PR-4)
- 신규: `Apps\Promaker\Promaker\Themes\Theme.Controls.Dock.xaml` (dock caption/tab/auto-hide 의 Promaker 자체 brush 매핑. App.xaml ResourceDictionary 머지로 등록. DynamicResource 사용으로 ThemeChanged 시 자동 갱신, PR-4)
- 신규: `Apps\Promaker\Promaker\Persistence\DockLayoutPersistence.cs` (namespace `Promaker.Persistence`)
- 신규: `Apps\Promaker\Promaker\Presentation\Dock\DockThemeBridge.cs` (PR-3)

### 참조용 (수정 없음)
- `Controls\Canvas\SplitCanvasContainer.xaml`, `Controls\Simulation\SimulationPanel.xaml`, `Controls\Simulation\GanttChartControl.xaml`, `Controls\Llm\LlmChatPanel`
- `ViewModels\Shell\MainViewModel.LlmChat.cs`
- **`Presentation\ThemeManager.cs`** (v6 직접 수정 없음 — DockThemeBridge 가 ThemeChanged 만 구독)
- `Themes\Theme.Dark.xaml` / `Theme.Light.*` / `Theme.Controls.Forms.xaml` / `Theme.Icons.xaml`
- `Promaker.Tests` — 본 작업 영향 없음.
- `Promaker.main.sln` (untracked — git status 확인 후 처리, v5 C7)

## 6. 핵심 리스크 (요약, 본문 §3 참조)

1. **테마 NuGet 별도 패키지 누락 시 캡션/탭 시각 깨짐** → §3.1 / PR-1a 첫 spike.
2. **AvalonDock 테마 매핑** (Promaker 자체 ResourceDictionary 와 정합) → §3.1 / PR-1b.
3. **AvalonDock 4.74.1 API 단정 6건** → ✅ v7 spike 완료 (§2 끝부분 확정 결과 표 참조). 단정 자제 → 단정 확정.
3a. **F1 ClosedPanelsBar 미노출** (v7) → PR-2a 의 복원 UI (메뉴+▼) 필수.
3b. **F2 빈 column 잔존** (v7) → PR-2a 의 자동 collapse 필수.
3c. **F3 IsVisibleChanged 중복 raise** (v7) → `_suppressVmSync` 가드 절대 필수.
3d. **F4 e.Cancel 미설정 시 ghost** (v7) → callback 의 명시 `e.Cancel=true` 필수.
3e. **F5 CloseCommand=ICommand** (v7) → InputGestures 직접 변경 불가, KeyBinding 우선순위 / PreviewKeyDown 우회.
3f. **F7 floating Topmost** (v7) → `Topmost=false` 정책 / 3안 spike 후 채택.
4. **LlmChat single-source-of-truth 모델 + autostart race** → §3.3 / PR-2b.
5. **헤더 통합 안 결정** (3안 비교, B 의 conditional 처리) → §3.4 / PR-4 spike.
6. **Welcome/Open 이중 layout 통합** → §3.1 안 A / PR-2a.
7. **Overlay 책임 분리 (BusyOverlay 포함)** → §3.5 / PR-2a.
8. **PropertyPane wiring 정석 안** → §3.6 / PR-2a (FocusNameEditorCommand RoutedCommand 흡수).
9. **측정 자동화 + floating + autostart race** → §3.3 / PR-2b.
10. **ContentId 미매칭 케이스** (가장 빈번) → §3.2 / PR-3 e.Cancel 패턴.
11. **AutoHide 메모리 leak** (#859/#1033) → PR-1b spike 회귀 측정.
12. **DPI / multi-monitor fallback** (50% 교집합 기준) → §6 알고리즘 명시 / PR-3.
13. **anchor reference reconcile** (deserialize 후 x:Name stale) → §3.2 ReconcileAnchors / PR-2a.
14. **default layout SSOT** (XAML 단일, EmbeddedResource 금지) → §3.2 / PR-3.
15. **ThemeManager SRP** (DockThemeBridge 분리) → PR-3.
16. **try/catch 자제** — `XmlException`/`InvalidOperationException` 으로 좁게 (기존 `MainWindow.xaml.cs:189-197`, `:370-380` 의 watchdog/DWM try/catch 는 본 작업 범위 외, 유지).
17. **빌드 확인 필수** — 각 PR 종료 시.
18. **사용자 명시 없이 git commit 금지** — CLAUDE.md.
19. **Ms-PL 라이선스** — NuGet 4.9.0+ 가 LICENSE 자동 포함, publish 산출물에 패키지 LICENSE 동봉되는지 확인만.

## 7. 반론 (수용하지 않은 reviewer 항목)

| 항목 | 반론 |
|---|---|
| v2 C5 `--classic-layout` feature flag | 거부. PR-2a 단일 revert commit 으로 충분. |
| v2 m26 `Visibility="Hidden"` | 거부. AvalonDock `IsVisible` 표준 사용. |
| v5 D AOT/Trim 호환성 | 현 plan 범위 외. 본 작업에서 trim publish 도입 없음. |
| v5 D 대안 라이브러리 비교 (Syncfusion/Telerik/ActiPro) | 결정 번복 사유 없음 (Ms-PL/.NET 9/transitive 0/유지보수 활성). 참고만. |
| v6 R3.M5 `RestoreOutcome` enum 유지 | 거부. 호출처 1곳 + 측정 skip 분기는 boolean 으로 충분. 향후 migration 필요 시 enum 승격. |

## 8. 진행 체크포인트 (이어받는 세션용)

- **plan v9 (2026-05-13)**. v8 → v9 핵심 변경: B-1 (닫힌 anchor 복원 UI) Phase B 구현 완료 — Toolbar Etc 의 "보기" 단일 popup 통합 안 채택 (▼ 드롭다운 분리 안 폐기). `MainViewModel.Dock.cs` partial 신규 (4 anchor mirror `[ObservableProperty] LayoutAnchorable?`).
- **plan v8 (2026-05-13)**. v7 → v8 핵심 변경: 외부 reviewer M1/M2/M3 처리, PR-2a / PR-2b 경계 정정 (SSOT 가 PR-2a 안으로 흡수), Welcome 모드 보조 anchor 4종 동기화 추가, 변수명 `_suppressVmSync` → `_suppressLlmChatSync` 통일.
- 현재 commit 상태 (branch `dock`):
  - `5e40fa6` Dock layout: PR-1a (AvalonDock 4.74.1) + PR-1b (Spike Window) + todo v7 (옛 시점). 패키지 추가 + spike 자동 검증.
  - `1479f74` Dock layout: PR-2a 외곽 DockingManager 교체 + PR-2b LlmChat SSOT (부분). MainWindow.xaml Grid → 6 anchor 트리, WelcomeView 신규 (`Apps/Promaker/Promaker/Controls/Shell/WelcomeView.xaml(.cs)`), SSOT 가드 + 3속성 set + Hiding 역동기화, workspaceDocs.Children Add/Remove 동적 관리, todo v5 → v7.
  - `89a23e2` Dock layout: PR-2a/2b Phase A — 빈 column 자동 collapse (5 anchor IsVisibleChanged → pane DockWidth/Height toggle) + DockingManager.Resources 의 LayoutAnchorableFloatingWindowControl/LayoutDocumentFloatingWindowControl Topmost=False Style + LlmChatVm null/IsLlmEnabled=false edge case + DispatcherPriority Loaded → ApplicationIdle + CloseAllFloatingWindows + PropertyPanel Loaded/Unloaded 자가 등록.
  - `8a96e47` Dock layout: 외부 reviewer M1 수용 + todo v8.
- working tree (--git-commit 대상, dock branch — remote 없음 → local commit only):
  - B-1 (닫힌 anchor 복원 UI) Phase B 구현. `M Apps/Promaker/Docs/todo-dock-layout.md` + `M Apps/Promaker/Promaker/MainWindow.xaml.cs` + `M Apps/Promaker/Promaker/Controls/Shell/MainToolbarEtcContent.xaml` + `?? Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.Dock.cs` (신규).
  - 내용: `MainViewModel.Dock.cs` partial 4 anchor mirror, `MainWindow` 생성자가 anchor 4개 VM set, `MainToolbarEtcContent.xaml` 의 유틸 다음에 "보기" ToggleButton + Popup + 4 CheckBox (Explorer/Properties/History/Simulation, TwoWay binding). 자가 검열 sub-agent Major 0/Minor 3 — 모두 의도된 동작 또는 본 PR 범위 외로 처리. todo v8 → v9.

### B-1 (보기 메뉴) review 처리 내역 (v9 working tree 시점)

| 항목 | reviewer 의견 | 처리 |
|---|---|---|
| Minor — "보기" ToggleButton 에 `IsEnabled` binding 없음 | Welcome 모드 (HasProject=false) 에서 ToggleButton 자체는 활성 → popup 은 열리지만 4 항목 모두 회색 disabled 라 UX trade-off. 일관성 안 (ToggleButton 도 IsEnabled={Binding HasProject}) 과 발견성 안 (현재) 모두 정당. | 현재 안 유지. 사용자가 명시적으로 개선 요청하기 전에는 그대로. 절충 옵션 (popup 상단에 "프로젝트를 먼저 열어주세요" 안내 텍스트 추가) 도 가능하나 본 PR 범위 외. |
| Minor — VM 이 View 객체 (LayoutAnchorable) 를 보관 (MVVM 순수성 위반) | `MainViewModel.Dock.cs` 의 `[ObservableProperty] LayoutAnchorable?` 4종이 View type 을 VM 에 노출. | 의도된 단축. `MainViewModel.Dock.cs` 주석 + 본 문서 §3.1 v9 정정 절 양쪽에 trade-off 사유 명시 ("MainToolbarEtcContent 가 별도 UserControl 이라 ElementName 으로 anchor 직접 접근 불가 → VM mirror 가 가장 짧음"). 추상 인터페이스 (예: `IDockAnchorHandle`) 도입은 over-engineering. |
| Refactoring — 4 anchor 의 ObservableProperty / VM set / CheckBox 가 동일 pattern 4번 반복 | ItemsControl + collection 일반화 시 IsEnabled/Content 변종 가능성으로 binding/XAML 오히려 복잡. | 현재 4건 명시 합당. partial 파일 위치 (`ViewModels/Shell/MainViewModel.Dock.cs`) 도 기존 `MainViewModel.LlmChat.cs` / `MainViewModel.History.cs` naming pattern 과 일관 OK. |
| 추가 발견 — anchor 가 floating window 안에 있는 상태에서 CheckBox 로 hide → 다시 show 시 복귀 위치 | `IsVisible=false` 시 AvalonDock 가 floating window 도 close 처리 예상. 다시 `IsVisible=true` 시 직전 floating 위치 복귀 vs 원래 docked 위치 복귀 동작 4.74.1 미확정. spike 는 docked 상태 IsVisible round-trip 만 검증. | Phase B 수동 회귀 시나리오에 추가. 결과에 따라 추가 wiring 필요 여부 결정. |

### 외부 reviewer 처리 내역 (1479f74 시점 기준)

| 항목 | reviewer 의견 | 처리 |
|---|---|---|
| M1 (합의 2/3) | Welcome 모드에서 보조 anchor 4종 (Explorer/Property/History/Simulation) IsVisible 동기화 누락 — todo §3.1 안 A 명세 drift + 이전 WelcomeLayout 안내 화면 대비 UX 회귀 | 수용. `SyncWelcomeCanvasVisibility` 안에 4 anchor `IsVisible = _vm.HasProject` 추가. LlmChat 은 IsLlmChatVisible SSOT 별도라 제외. |
| M2 (B 단독) — PR-2a 가 PR-2b 영역 (SSOT 가드 / 3속성 / Hiding) 선반영 | todo line 437 의 "PR-2a 는 minimal wiring 만, PR-2b 는 SSOT 완성" 분리 위반 | 부분 수용. 1479f74 + 89a23e2 이미 commit 됨 → revert 비현실. todo 의 PR-2a/PR-2b 경계 명세 정정 (§4). PR-2b 는 측정 자동화 검증 1건으로 축소. |
| M2 (B 단독) — callback `e.Cancel=true` 누락 | callback 에서 e.Cancel 미설정 시 ghost placeholder | 반론. reviewer 가 `LayoutSerializationCallback` (§3.2, PR-3 영역) 의 e.Cancel 과 `Hiding` 이벤트의 e.Cancel 을 혼동. 현 코드는 `LayoutSerializationCallback` 미등록 (PR-3 이월 명시). `Hiding` 의 e.Cancel=false 유지는 의도 — true 면 X 버튼이 hide 실패. 변경 없음. |
| M3 (A 단독) | 초기 1 frame 동안 welcomeDoc + canvasDoc 두 LayoutDocument 동시 노출 | 89a23e2 에서 `SyncWelcomeCanvasVisibility` 호출을 ctor → `MainWindow_Loaded` 로 이동 완료. reviewer 검토가 그 commit 이전 시점 기준이라 미반영. |
| B4 (B outlier) | Window Drop vs AvalonDock 내부 drop 충돌 가능성 | Phase B 수동 회귀로 이월 — anchor pane 위 .sdf drop 1회 확인. |
| B5 (B outlier) | FocusNameEditorRequested 가 hidden anchor 시 실패 | 기각. 89a23e2 의 PropertyPanel Loaded/Unloaded 자가 등록 패턴으로 자동 해제. |
| m1 (C 단독) | WelcomeView 가 부모 DataContext 강결합 | PR-3 backlog. |
| m2 (A 단독) | 변수명 `_suppressVmSync` → `_suppressLlmChatSync` 표기 불일치 | 수용. todo §3.3 갱신. |
| m3 (C 단독) | `SyncWelcomeCanvasVisibility` 재진입 가드 부재 → LlmChat 비대칭 | 수용. 주석으로 의도 명시 (View 갱신만 단방향이라 가드 불필요). |
| m4 (C 단독) | dockManager x:Name 미사용 | 수용. PR-3 placeholder 주석. |
| m5 (A 단독) | `MainWindow_Loaded` 책임 비대 | PR-3 진입 시 분리. |
| m6 (B 단독) | `MainWindow_Closed` 핸들러 해제 순서 race | PR-3 정리 시 같이. |

### 다음 액션 순서

1. ~~PR-1a~~ ✅ commit `5e40fa6`.
2. ~~PR-1b 자동 검증~~ ✅ commit `5e40fa6`. 수동 검증 (5경로 차단 / WindowChrome / Float cycle) 은 Phase B 회귀.
3. ~~PR-2a 외곽 교체 + PR-2b SSOT 흡수~~ ✅ commit `1479f74`.
4. ~~PR-2a/2b Phase A~~ ✅ commit `89a23e2`.
5. ~~외부 reviewer M1 수용 + todo v8~~ ✅ commit `8a96e47`.
6. ~~B-1 (닫힌 anchor 복원 UI) Phase B~~ ✅ working tree (--git-commit 대상). 단일 popup 통합 안 채택 — ▼ 드롭다운 분리 안 폐기. 빌드 0/0 통과. 자가 검열 Major 0.
7. **Phase B 잔여**: 사용자 검증 필요.
   - Ctrl+W / Ctrl+F4 충돌 fallback (anchor 모두 CanClose=False 라 현재 충돌 없는 것으로 추정 — 수동 회귀 후 결정).
   - 수동 시나리오 검증: dock·float·auto-hide·마그넷·Welcome 전환·FileDrag·BusyOverlay·floating Topmost=false 동작·5경로 차단·Float cycle 메모리·WindowChrome.
   - **B-1 추가 검증** — anchor 를 floating window 로 이동한 상태에서 보기 메뉴의 CheckBox uncheck → re-check 시 복귀 위치 확인 (직전 floating 위치 vs 원래 docked 위치). 결과에 따라 추가 wiring 필요 여부 결정.
   - 측정 자동화 close 확인 (App.StartupAutoOpenLlm 시).
   - LayoutAnchorable.Title 정책 (PR-4 와 stacked 또는 임시 노출 유지).
8. **PR-3 진행**: §3.2 결정 트리 적용.
   - `Apps/Promaker/Promaker/Persistence/DockLayoutPersistence.cs` (신규) — Save/Restore bool API.
   - atomic write (`write-temp` + `File.Replace`) + `FileShare.None`.
   - `dock-layout.xml` + `dock-layout.meta.xml` (version 1.0 박제) atomic 쌍.
   - `Window_Closing` 의 `_llmChatDisposed=true` 직후 Save 호출.
   - `Window.Loaded` 5-케이스 결정 트리 + `ReconcileAnchors()` 헬퍼 + `LayoutSerializationCallback` 의 `e.Cancel=true` 명시 (§3.2 — unknown ContentId placeholder 잔존 회피).
   - Reset Layout 메뉴 (`Apps/Promaker/Promaker/Controls/Shell/MainToolbarEtcContent.xaml`) — layout 파일 삭제 + default 분기 재실행.
   - `Apps/Promaker/Promaker/Presentation/Dock/DockThemeBridge.cs` (신규) — ThemeManager.ThemeChanged 만 구독 → dockManager.Theme 갱신.
   - DPI / multi-monitor fallback (50% 교집합 미만 시 main 중앙 재배치 + `Log.Warn`).
   - 측정 모드 시 복원 skip 검증.
   - 외부 reviewer 후속 (m5 — Loaded 책임 분리, m6 — Closed 핸들러 해제 순서 race) 일괄 정리.
9. **PR-4 진행**: §3.4 헤더 안 B (UserControl 컴포지션) conditional 또는 안 A (AnchorableTitleTemplate) setter 비교 후 채택. `Apps/Promaker/Promaker/Themes/Theme.Controls.Dock.xaml` (신규) 의 dock caption/tab/auto-hide brush 매핑 + `App.xaml` DockResources DynamicResource 머지.
10. **PR-5 진행** (선택): KeyBinding floating 라우팅 — `EventManager.RegisterClassHandler(typeof(Window), Window.KeyDownEvent, ...)` 좁게 적용.

### 주의 사항
- AvalonDock 4.74.1 API 6건은 **v7 spike 로 확정 완료** (§2 끝부분 표). 새 가정 추가 시 동일 절차로 spike 확인.
- `MainWindow.xaml.cs` 의 line 번호는 v5 작성 이후 이미 drift 발생. **line 박제 금지, 시점 표현 사용**.
- 자가 검열 trigger (CLAUDE.md): 함수 시그니처 변경 / 신규 type 3개 이상 / 단일 파일 100 line 이상 / dispatch 재작성 / public API 갱신 중 하나라도 충족 시 sub-agent (general-purpose 또는 code-review skill) 위임 후 commit 제안.
- `--git-commit` 진행 시 dock branch 가 remote 없으면 local commit only (push 생략).
- 다른 문서 / 파일 가리키는 참조는 파일 경로 명시 (예: `Apps/Promaker/Docs/todo-dock-layout.md §3.1`, "PR-3 이후 backlog" 처럼 모호한 표기 회피).
- 사용자 명시 없이 git commit 금지. **memory feedback**: multi-step plan 의 "go" 동의로 commit 까지 묶지 않음, commit step 별도 confirm.
- PR-1b Spike Window (`Apps/Promaker/Promaker/Spike/`) 는 Phase B 종료 또는 PR-3 commit 시 제거 예정.
- working tree 의 임시 산출물 (`_review_*.diff` 등 자가 검열 agent 가 남긴 untracked 파일) 은 사용자 명시 후 삭제 — CLAUDE.md "관련 없는 파일을 함부로 삭제하지 않는다".
