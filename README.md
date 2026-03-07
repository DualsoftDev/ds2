# Promaker

Last Sync: 2026-03-08 (보일러플레이트 제거 + string→typed 바인딩 전환)

## 프로젝트 목표

Promaker 프로젝트는 다음 세 가지를 설계를 중심으로 **Seqeunce control editor**를 구현 중입니다.

- **편집 코어(F#) 분리**: 추가/삭제/이동/연결/복사/붙여넣기의 로직을 F# 레이어에 집중시켜 UI 기술이 바뀌어도 재사용 가능
- **증분 UndoRecord 기반 Undo/Redo**: 변경된 엔티티만 클로저 기반 UndoRecord로 추적, Undo 1회 = 1 사용자 제스처 보장
- **레이어 경계 강제**: C#(WPF)는 wiring·binding·rendering만, 편집 상태 변경은 반드시 F# `DsStore` 확장 메서드 경유

---

## 아키텍처 조감도

```
╔══════════════════════════════════════════════════════════════════════════════╗
║                              Promaker 전체 구조                               ║
╠══════════════════════════════════════════════════════════════════════════════╣
║                                                                              ║
║  ┌──────────────── Promaker  (C#, WPF) ────────────────────────────┐  ║
║  │                                                                        │  ║
║  │  MainWindow  │  EditorCanvas (Input/Select/Nav/Connect)                │  ║
║  │  ViewModels  │  Dialogs                                                │  ║
║  │                                                                        │  ║
║  │          역할: wiring / binding / rendering 만 담당                     │  ║
║  │          금지: 직접 Store 수정, 자체 Undo 스택, 비즈니스 로직            │  ║
║  └────────────────────────┬───────────────────────────────────────────────┘  ║
║                           │ 편집: store.Xxx(...)       ▲ EditorEvent         ║
║           ┌───────────────┘ 조회: store.Query/Projection                     ║
║           │                                           │ (StoreRefreshed 등)  ║
║           │               ┌───────────────────────────┘                      ║
║           ▼               ▼                                                  ║
║  ┌──────────────── Ds2.UI.Core  (F#) ─────────────────────────────────────┐  ║
║  │                                                                        │  ║
║  │  Core:  DsStore (컬렉션 + Undo/Redo + 이벤트 + File I/O)              │  ║
║  │         DsQuery.*  ValidationRules                                     │  ║
║  │                                                                        │  ║
║  │  Extensions ([<Extension>] C# 확장 메서드):                             │  ║
║  │       DsStore.Queries  — 읽기 전용 쿼리/프로젝션 위임                  │  ║
║  │       DsStore.Nodes    — 엔티티 CRUD/이동/삭제 + WithTransaction       │  ║
║  │       DsStore.Arrows   — 화살표 연결/제거/재연결                        │  ║
║  │       DsStore.Panel    — 속성 패널 읽기/쓰기                            │  ║
║  │       DsStore.Paste    — 복사/붙여넣기                                  │  ║
║  │                                                                        │  ║
║  │  Projection: TreeProjection  CanvasProjection                          │  ║
║  │              (Store → 트리/캔버스 뷰 데이터)                            │  ║
║  │                                                                        │  ║
║  │  Queries:   EntityHierarchy  Selection  Connection  AddTarget          │  ║
║  │                                                                        │  ║
║  └────────────────────────┬───────────────────────────────────────────────┘  ║
║                           │ entity 타입 참조                                  ║
║                           ▼                                                  ║
║  ┌──────────────── Ds2.Core  (F#) ────────────────────────────────────────┐  ║
║  │                                                                        │  ║
║  │  Entities:  Project / DsSystem / Flow / Work / Call                    │  ║
║  │             ApiCall / ApiDef / HW components                           │  ║
║  │  Serialization:  JsonConverter  JsonOptions  DeepCopyHelper            │  ║
║  │  Types:  Properties / Enum / Class / ValueSpec                         │  ║
║  │                                                                        │  ║
║  └────────────────────────────────────────────────────────────────────────┘  ║
║                                                                              ║
║  ┌──────────── Ds2.Aasx  (F#) ──────────────────────────────────────────────┐  ║
║  │  AasxSemantics  idShort 상수 정의                                         │  ║
║  │  AasxFileIO     AASX ZIP 읽기/쓰기                                        │  ║
║  │  AasxExporter   DsStore → AASX  (AasCore.Aas3_0 v1.0.0)                 │  ║
║  │  AasxImporter   AASX → DsStore                                           │  ║
║  │  → Ds2.Core + Ds2.UI.Core 양쪽 참조  ← UI.Core에서는 참조 없음 (순환 방지) │  ║
║  └──────────────────────────────────────────────────────────────────────────┘  ║
╚══════════════════════════════════════════════════════════════════════════════╝
```

### 레이어 의존 방향

```
Promaker  →  Ds2.UI.Core  →  Ds2.Core
     (C#, WPF)        (F#)            (F#)
         │                ▲
         ├──►  Ds2.Aasx ──┘ (+ Ds2.UI.Core 참조)
         │      (F#)
         └──►  Ds2.Core (도메인 타입 직접 참조: ValueSpec 등)
```

상위 레이어는 하위 레이어만 의존합니다.
`Ds2.Aasx`는 `Promaker`에서 직접 참조되며 `Ds2.UI.Core → Ds2.Aasx` 순환 의존은 없습니다.
`Promaker`는 `Ds2.Core`를 직접 참조합니다 — 도메인 타입(`ValueSpec` 등)을 C#에서 직접 사용하기 위함.
C#용 공유 타입(`UiArrowType`, `UiCallConditionType` 등)은 `Ds2.UI.Core/Core/Types.fs`에서 정의됩니다.

---

## 엔티티 관계도

```
Project
 ├─[Active]──► DsSystem ──► Flow ──► Work ─────────────────► Call
 │                                    │      .ApiCalls[]       │  .ApiDefId
 │                           ArrowBetweenWorks                 │      │
 │                                             ArrowBetweenCalls      │
 │                                                                    │
 └─[Passive]─► DsSystem (Device) ◄────────────────────────────────────┘
                   ├── ApiDef  ◄── ApiCall.ApiDefId 로 연결
                   ├── HwButton
                   ├── HwLamp
                   ├── HwCondition
                   └── HwAction

Call.Name = DevicesAlias + "." + ApiName   (computed, Rename 시 DevicesAlias만 변경)
ApiCall   = ApiDef 실행 1건 (OutTag 주소 / InTag 주소 / OutputSpec / InputSpec 포함)
CallCondition = Call 동작 조건 1건 (Active/Auto/Common 타입, IsOR, IsRising, 조건 ApiCall 목록)
```

- **Active System**: 제어 흐름 트리 (Flow → Work → Call)
- **Passive System**: 장치 정의 트리 (ApiDef, HW 컴포넌트)
- **ArrowBetweenWorks**: System/Flow 캔버스에서 Work-Work 연결선
- **ArrowBetweenCalls**: Work 캔버스에서 Call-Call 연결선

---

## 솔루션 구조

```text
BuildAll/                  # 빌드 산출물 배포 스크립트
ExternalDlls/              # 외부 DLL (NuGet 외 수동 참조)
Solutions/
  Ds2.sln
  Core/
    Ds2.Core/              # 순수 도메인 타입 (Entities, Properties, Enum, ValueSpec, JsonConverter)
    Ds2.UI.Core/           # 편집 코어(F#) — 21개 모듈 (DsStore + Extensions + Projection + Queries)
  Convert/
    Ds2.Aasx/              # AASX I/O(F#) — 4개 모듈 (AasCore.Aas3_0 v1.0.0 기반 AASX 양방향 변환)
  Backend/
    Ds2.Database/          # 데이터 계층
  Tests/
    Ds2.Core.Tests/        # Core 단위 테스트 (24개)
    Ds2.UI.Core.Tests/     # UI.Core 단위 테스트 (49개: DsStore 39 + ViewProjection 10)
    Ds2.Integration.Tests/ # 통합 테스트 (14개)

Apps/Promaker/
  Promaker.sln
  Promaker/                # WPF UI(C#) — 어셈블리명 Promaker, namespace Promaker.*
```

테스트 합계: **87개** (24 + 49 + 14)

---

## 파일 구조 및 역할

### 루트

| 파일 | 역할 |
|------|------|
| `README.md` | 프로젝트 개요, 구조, 파일 역할 인수인계 문서 |
| `RUNTIME.md` | CRUD · Undo/Redo · JSON 직렬화 동작 상세 |
| `.editorconfig` | 코드 스타일/포맷 기본 규칙 |

---

### Ds2.Core — 순수 도메인 타입 (Store/Query/Mutation 없음)

| 파일 | 역할 |
|------|------|
| `AbstractClass.fs` | `DsEntity` 추상 베이스 타입, `DeepCopyHelper` (backupEntityAs&lt;'T&gt; / jsonCloneEntity) |
| `Entities.fs` | Project · DsSystem · Flow · Work · Call · ApiDef · ApiCall · HW 엔티티 정의 |
| `Properties.fs` | WorkProperties · CallProperties · ApiDefProperties 등 속성 모델 |
| `Enum.fs` | `Status4`, `CallType`, `ArrowType`, `CallConditionType` 도메인 열거형 |
| `Class.fs` | `IOTag`, `Xywh` 등 값 타입 클래스 |
| `ValueSpec.fs` | `ValueSpec` DU (None / Bool / Int / Float / String / Range 등) |
| `JsonConverter.fs` | `System.Text.Json` 기반 직렬화 옵션 및 커스텀 컨버터 |

---

---

### Ds2.UI.Core — 편집 코어 (F#)

컴파일 순서 = 의존 순서입니다. 위 파일을 아래 파일에서만 의존합니다.

| # | 파일 | 역할 |
|---|------|------|
| 1 | `Core/Types.fs` | `EditorEvent` DU (`HistoryChanged` 포함), `EntityKind` DU, `EntityTypeNames`, `UiDefaults`, `UiArrowType`/`UiCallConditionType` + ofCore/toCore, `UiMoveEntityRequest`, `MoveEntityRequest`, `CallCopyContext` DU |
| 2 | `Core/UndoRecord.fs` | `UndoRecord` (`Undo`/`Redo` 클로저 + `Description`), `UndoTransaction` (`Label` + `Records` 리스트) |
| 3 | `Commands/UndoRedoManager.fs` | 비제네릭 `UndoRedoManager(maxSize)` — `LinkedList<UndoTransaction>` 기반 undo/redo 스택 관리, maxSize O(1) trim. `UndoLabels`/`RedoLabels` 프로퍼티로 레이블 목록 노출 |
| 4 | `Core/DsStore.fs` | `DsStore` 타입 (13개 Dictionary 컬렉션 + ReadOnly 뷰 + 증분 Undo/Redo + 이벤트 발행 + File I/O). `WithTransaction(label, action)` — 증분 UndoRecord 기반 트랜잭션. `TrackAdd`/`TrackRemove`/`TrackMutate`/`TrackGuidSetAdd`/`TrackGuidSetRemove` — dict 조작 + UndoRecord 기록을 하나로 묶는 Track 헬퍼. `RewireApiCallReferences` — Undo/Redo 후 Call↔ApiCall 참조 재연결. `DirectWrite` — 비 Undo 직접 쓰기 (AASX 임포트용). `Undo`/`Redo`/`UndoTo`/`RedoTo`. `LoadFromFile`/`SaveToFile`/`ReplaceStore` |
| 4 | `Core/DsQuery.fs` | `DsQuery.*` — 엔티티 조회 쿼리 (`getXxx`, `allXxxs`, `xxxsOf`) |
| 5 | `Core/EntityNameAccess.fs` | `EntityNameAccess` — 엔티티 종류별 이름/ID 접근 헬퍼 |
| 6 | `Core/ValidationRules.fs` | 이름 · 주소 · 값 검증 규칙 (`ProjectValidation`, `OneToOneValidation` 등) |
| 7 | `Geometry/ArrowPathCalculator.fs` | Work/Call 캔버스 화살표 polyline 경로 계산 (직교 꺾임) |
| 8 | `Projection/ViewTypes.fs` | `TreeNodeInfo` · `CanvasNodeInfo` · `SelectionKey` · `DeviceApiDefOption` · `CallApiCallPanelItem` · `CallConditionPanelItem` 등 뷰/패널 전용 레코드 |
| 9 | `Projection/PropertyPanelValueSpec.fs` | `PropertyPanelValueSpec` — ValueSpec 타입 인덱스 ↔ DU 변환, 텍스트 파싱 (`specFromTypeIndex`, `tryParseAs`) |
| 10 | `Projection/TreeProjection.fs` | `DsStore` → 트리 데이터 변환 (`buildTrees`: 컨트롤 트리 + 디바이스 트리) |
| 11 | `Projection/CanvasProjection.fs` | `DsStore` → 캔버스 콘텐츠 변환 (노드 위치, 화살표 포인트) |
| 12 | `Queries/EntityHierarchyQueries.fs` | 계층 역탐색 (stepCallToWork / stepWorkToFlow / stepFlowToSystem), `parentIdOf`, 탭 정보 해석, ApiDef 검색 |
| 13 | `Queries/AddTargetQueries.fs` | Add System/Flow 대상 해석 |
| 14 | `Queries/SelectionQueries.fs` | 캔버스 선택 정렬/범위 선택/Ctrl+Shift 다중 선택 해석 |
| 15 | `Queries/ConnectionQueries.fs` | 화살표 연결 가능 대상 Flow 해석, 선택 순서 연결 |
| | **[Extensions — `[<Extension>]` C# 확장 메서드]** | DsStore의 모든 편집/조회 메서드를 정의. C#에서 `store.Xxx(...)` 형태로 호출. 내부에서 `store.WithTransaction` + Track 헬퍼로 증분 변경 추적 |
| 16 | `Extensions/DsStore.Paste.fs` | 복사/붙여넣기 — `PasteEntities`, `IsCopyableEntityType`, `EntityTypeForTabKind`. 내부: `PasteResolvers` (대상 해석), `DirectPasteOps` (`CallCopyContext` 기반 ApiCall 공유/복제, `DevicePasteState`로 Device System 복제/재사용) |
| 17 | `Extensions/DsStore.Queries.fs` | 읽기 전용 쿼리 래퍼 — `BuildTrees`, `CanvasContentForTabUi`, `TryOpenTabForEntity`, `FindApiDefsByName`, `ParseValueSpec`, `OrderCanvasSelectionKeys`, `ApplyNodeSelection` 등. Projection/Queries 모듈에 위임 |
| 18 | `Extensions/DsStore.Nodes.fs` | 엔티티 CRUD/이동/삭제 — `AddProject`/`AddSystem`/`AddFlow`/`AddWork`/`AddApiDef`/`AddCallsWithDevice`/`AddCallWithLinkedApiDefs`, `MoveEntitiesUi`, `RemoveEntities`, `RenameEntity`. 내부: `CascadeRemove`, `DirectDeviceOps` |
| 19 | `Extensions/DsStore.Arrows.fs` | 화살표 연산 — `RemoveArrows`, `ReconnectArrow`, `ConnectSelectionInOrderUi`. 내부: `DirectArrowOps` |
| 20 | `Extensions/DsStore.Panel.fs` | 속성 패널 읽기/쓰기 — PeriodMs/TimeoutMs 조회·수정, ApiDef CRUD, ApiCall CRUD, CallCondition CRUD, OutputSpec 수정. 내부: `DirectPanelOps` |

---

### Ds2.Aasx — AASX I/O (F#)

| 파일 | 역할 |
|------|------|
| `AasxSemantics.fs` | Ds2 전용 idShort 상수 (`Ds2SequenceControlSubmodel`, `Name_`, `Guid_` 등) |
| `AasxFileIO.fs` | AASX ZIP 읽기(`readEnvironment`) / 쓰기(`writeEnvironment`) — AasCore.Aas3_0 Xmlization 사용 |
| `AasxExporter.fs` | `DsStore` → AASX 변환 (`exportToAasxFile`): Project 계층을 SMC/SML로 직렬화, 복잡한 타입은 JSON Property로 저장 |
| `AasxImporter.fs` | AASX → `DsStore option` 역변환 (`importFromAasxFile`): SMC/SML 파싱 후 엔티티 재구성 |

---

### Promaker — WPF UI (C#)

| 파일 | 역할 |
|------|------|
| `App.xaml / App.xaml.cs` | 앱 리소스 루트 및 시작/종료 코드. `OnStartup`에서 log4net.config 로딩, `DispatcherUnhandledException` FATAL 로깅 처리 |
| `log4net.config` | log4net 설정 파일 (RollingFile + DebugAppender). 빌드 시 출력 폴더로 복사 (`PreserveNewest`) |
| `EntityTypes.cs` | Entity type 문자열 상수 (`"Work"`, `"Call"` 등) + `Is` / `IsWorkOrCall` / `IsCanvasOpenable` 헬퍼 |
| `MainWindow.xaml` | 메인 화면 레이아웃 (트리 패널 / 캔버스 탭 / 속성 패널) |
| `MainWindow.xaml.cs` | 트리·탭·메뉴 이벤트 wiring, `DsStore` 확장 메서드 호출 진입 |
| `Themes/Theme.Dark.xaml` | 다크 테마 리소스 딕셔너리 (브러시, 컨트롤 스타일) |
| `Converters/Converters.cs` | WPF 바인딩 컨버터 |
| `Controls/EditorCanvas.xaml` | 캔버스 UI 템플릿 |
| `Controls/EditorCanvas.xaml.cs` | 캔버스 공통 상수/헬퍼, AddWork/AddCall 클릭 |
| `Controls/EditorCanvas.Input.cs` | 마우스·키보드 입력 처리 (드래그 이동, Delete, 연결 시작) |
| `Controls/EditorCanvas.Selection.cs` | 박스 선택, 화살표 선택 |
| `Controls/EditorCanvas.Navigation.cs` | 줌·패닝, FitToView |
| `Controls/EditorCanvas.Connect.cs` | 화살표 연결 시작/완료/취소 |
| `Controls/ValueSpecEditorControl.xaml` | ValueSpec 인라인 편집 컨트롤 UI |
| `Controls/ValueSpecEditorControl.xaml.cs` | ValueSpec 인라인 편집 컨트롤 코드 |
| `Controls/ConditionSectionControl.xaml` | CallCondition 섹션(Active/Auto/Common) 공통 UserControl UI |
| `Controls/ConditionSectionControl.xaml.cs` | `ConditionSectionControl` 코드 (Header, AddToolTip, Conditions 바인딩) |
| `ViewModels/MainViewModel.cs` | 핵심 필드/컬렉션/프로퍼티, 생성자, NewProject/Undo/Redo, Reset, UpdateTitle |
| `ViewModels/MainViewModel.EditorGuards.cs` | `TryEditorAction` / `TryEditorFunc` / `TryEditorRef` — DsStore 확장 메서드 호출 공통 예외 처리 가드. `TryMoveEntitiesFromCanvas`, `TryReconnectArrowFromCanvas`, `TryConnectNodesFromCanvas` Canvas 공용 메서드 |
| `ViewModels/MainViewModel.History.cs` | `HistoryPanelItem` 타입 + `JumpToHistory` + `RebuildHistoryItems` |
| `ViewModels/MainViewModel.Events.cs` | `WireEvents` + `HandleEvent` + `ApplyEntityRename` + `ActionObserver<T>` |
| `ViewModels/MainViewModel.NodeCommands.cs` | AddProject/System/Flow/Work/Call, Delete, Rename, Copy(혼합 타입/부모 금지 경고), Paste(System 대상 경고) + 타겟 결정 헬퍼 |
| `ViewModels/MainViewModel.FileIO.cs` | JSON Open/Save + AASX 임포트(`ImportAasxCommand`) / 익스포트(`ExportAasxCommand`) |
| `ViewModels/MainViewModel.Selection.cs` | 트리·캔버스 선택 동기화 |
| `ViewModels/MainViewModel.CanvasTabs.cs` | 탭 상태, `RebuildAll` (트리+캔버스 전체 재구성), `OnActiveTabChanged` (탭 전환 시 노드/화살표 선택 자동 해제 포함) |
| `ViewModels/MainViewModel.PropertiesPanel.cs` | 속성 패널 공용 Collections/Properties + ApplyWorkPeriod + RefreshPropertyPanel + RequireSelectedAs + ShowOwnedDialog |
| `ViewModels/MainViewModel.CallPanel.cs` | Call 속성 패널 — ApplyCallTimeout, ApiCall CRUD 5개, CallCondition CRUD 7개, RefreshCallPanel, ReloadConditions, 섹션 헬퍼 |
| `ViewModels/MainViewModel.SystemPanel.cs` | System 속성 패널 — ApiDef CRUD 3개, RefreshSystemPanel, EditApiDefNode |
| `ViewModels/MainViewModel.PropertyPanelItems.cs` | 속성 패널 보조 뷰모델 타입: `CallApiCallItem`, `DeviceApiDefOptionItem`, `CallConditionItem`, `ConditionApiCallRow`, `ConditionSectionItem` |
| `ViewModels/CanvasTab.cs` | `CanvasTab` ObservableObject + `TreePaneKind` enum |
| `ViewModels/ArrowNode.cs` | 화살표 뷰모델 (Geometry 계산, 화살촉 타입별 렌더링) |
| `ViewModels/EntityNode.cs` | 트리/캔버스 노드 뷰모델 |
| `ViewModels/TreeNodeSearch.cs` | 트리 탐색 정적 유틸리티 |
| `Dialogs/ApiCallCreateDialog.xaml(.cs)` | ApiCall 생성 다이얼로그 |
| `Dialogs/ApiCallSpecDialog.xaml(.cs)` | ApiCall InTag/OutTag/ValueSpec 편집 다이얼로그 |
| `Dialogs/ApiDefEditDialog.xaml(.cs)` | ApiDef 속성 편집 다이얼로그 |
| `Dialogs/ArrowTypeDialog.xaml(.cs)` | 화살표 유형 선택 다이얼로그 (Start / Reset / StartReset / ResetReset / Group) |
| `Dialogs/CallCreateDialog.xaml(.cs)` | Call 생성 다이얼로그 (Device 모드 / Call only 모드) |
| `Dialogs/ConditionApiCallPickerDialog.xaml(.cs)` | 조건 ApiCall 선택 다이얼로그 (전체 목록, 다중 선택 지원) |
| `Dialogs/ValueSpecDialog.xaml(.cs)` | ValueSpec 독립 편집 다이얼로그 |

---

### 테스트 프로젝트

| 파일 | 역할 |
|------|------|
| `Ds2.Core.Tests/Tests.fs` | Core 엔티티·DeepCopy·ValueSpec 단위 테스트 (24개) |
| `Ds2.Core.Tests/JsonConverterTests.fs` | JSON 직렬화 라운드트립 테스트 |
| `Ds2.UI.Core.Tests/DsStoreTests.fs` | DsStore CRUD·Undo/Redo·캐스케이드·복사붙여넣기·패널 테스트 (39개) |
| `Ds2.UI.Core.Tests/ViewProjectionTests.fs` | Tree/Canvas Projection·Selection·Query 테스트 (10개) |
| `Ds2.UI.Core.Tests/TestHelpers.fs` | 테스트 헬퍼 |
| `Ds2.Integration.Tests/Tests.fs` | 통합 시나리오 테스트 (14개) |

---

## 편집 한 동작의 전체 경로

```
사용자 입력 (키보드 / 마우스 / 메뉴)
    │
    ▼  C# — EditorCanvas / MainViewModel
    │  편집 입력 해석 → store.Xxx(...) 호출
    │  조회/탭/투영 계산은 store.BuildTrees() / store.CanvasContentForTabUi() 등
    │
    ▼  F# — DsStore Extensions ([<Extension>] 메서드)
    │  store.WithTransaction(label, fun () -> Track 헬퍼로 증분 변경)
    │  → TrackAdd/TrackRemove/TrackMutate가 UndoRecord 기록
    │  → 실패 시 기록된 UndoRecord를 역순 실행하여 자동 복원 + reraise
    │
    ▼  F# — UndoRedoManager(100)
    │  undoStack.AddFirst(UndoTransaction) / redoStack.Clear
    │
    ▼  F# — DsStore: EditorEvent 발행
    │  StoreRefreshed       → UI 전체 재구성
    │  HistoryChanged       → History 패널 갱신 + Undo/Redo 버튼 상태 갱신
    │  SelectionChanged     → 속성 패널 갱신
    │
    ▼  C# — MainViewModel.HandleEvent(event)
       RebuildAll → WPF 바인딩 갱신 → 화면 반영
```

### 증분 UndoRecord 기반 Undo 설계

변경된 엔티티만 클로저 기반 `UndoRecord`로 추적합니다. Undo 시 기록된 클로저를 역순 실행하여 복원합니다.

- **Track 헬퍼**: `TrackAdd`/`TrackRemove`/`TrackMutate`/`TrackGuidSetAdd`/`TrackGuidSetRemove` — Dictionary 조작 + UndoRecord 기록을 하나로 묶어 Record 누락 방지
- **WithTransaction**: 여러 Track 호출을 하나의 `UndoTransaction`으로 묶음. 실패 시 기록된 UndoRecord를 역순 Undo로 자동 롤백
- **RewireApiCallReferences**: Undo/Redo 후 Call↔ApiCall 공유 참조 재연결 (deep copy로 인스턴스 불일치 해소)
- **제약**: 외부에서 `store.GetProject(id).Name <- "new"` 직접 필드 수정 시 Undo 추적 불가. 변경은 반드시 `store.메서드()` 경유

> 상세 내용: [`RUNTIME.md`](RUNTIME.md)

---

## 로깅 (log4net)

log4net 2.0.17이 F#+C# 전 레이어에 적용되어 있습니다.

### 초기화

`App.xaml.cs OnStartup`에서 `XmlConfigurator.Configure(new FileInfo("log4net.config"))`로 초기화합니다.
log4net.config 파일이 없으면 로깅 없이 앱이 정상 실행됩니다.

### 로그 파일 위치

```
<실행 파일 위치>/logs/ds2_yyyyMMdd.log
```

- Composite 롤링 (날짜 + 크기): 최대 10MB × 10개 백업 보관
- Visual Studio 출력 창(DebugAppender)에도 동시 출력

### 로거별 레벨 전략

| 지점 | 레벨 | 예시 |
|------|------|------|
| 앱 시작/종료 | INFO | `=== Promaker startup ===` |
| 전역 미처리 예외 | FATAL | `DispatcherUnhandledException` + 스택 트레이스 |
| EditorEvent 구독자 에러 | ERROR | `EditorEvent 구독자 에러` + 예외 |
| JSON 파일 열기/저장 성공 | INFO | `파일 열기/저장 완료: {path}` |
| JSON 파일 열기/저장 실패 | ERROR | 예외 포함 |
| AASX import/export 성공 | INFO | 경로 포함 |
| AASX import 빈 결과 | WARN | |
| AASX import ReplaceStore 실패 | ERROR | 예외 포함 → DialogHelpers.Warn |
| AASX export 실패 | WARN / ERROR | |
| AASX 내부 파싱 실패 (`AasxImporter`) | WARN | 9곳 `with _ -> None` → 함수명 + 예외 포함 |
| AASX ZIP 읽기 실패 | WARN | 예외 객체 포함 (stack trace) |
| AASX Submodels null / Submodel IdShort 불일치 | WARN | 경로 포함 |
| Unhandled EditorEvent | WARN | `Unhandled event: {타입명}` |
| `SaveToFile` 성공 | INFO | `저장 완료: {path}` |
| `SaveToFile` 실패 | ERROR | 예외 포함 (재throw) |
| `WithTransaction` 성공 | DEBUG | `Executed: {label}` |
| `WithTransaction` 실패 | ERROR | `Transaction failed: {label} — {msg}` + 예외 |
| Undo/Redo 성공 | DEBUG | `Undo: {명령 레이블}` / `Redo: …` |
| Undo/Redo 실패 | ERROR | 예외 포함 |
| `ApplyNewStore` 성공 | INFO | `Store applied: {context}` |
| `ApplyNewStore` 실패 | ERROR | 예외 포함 |
| AASX ZIP 읽기 실패 | WARN | `AASX 읽기 실패: {msg}` (기존 silent failure 개선) |

### 패키지 적용 범위

| 프로젝트 | 로거 선언 방식 |
|---------|-------------|
| `Ds2.UI.Core` (F#) | `LogManager.GetLogger(typedefof<DsStore>)` |
| `Ds2.Aasx` (F#) | `LogManager.GetLogger("Ds2.Aasx.AasxFileIO")` / `LogManager.GetLogger("Ds2.Aasx.AasxImporter")` |
| `Promaker` (C#) | `LogManager.GetLogger(typeof(App))` / `typeof(MainViewModel)` |

---

## 빌드 및 테스트

```bash
# 빌드
dotnet build Solutions/Ds2.sln -nologo
dotnet build Apps/Promaker/Promaker.sln -nologo

# 테스트
dotnet test Solutions/Ds2.sln -nologo
```

---

## 관련 문서

| 문서 | 내용 |
|------|------|
| `RUNTIME.md` | CRUD · Undo/Redo · JSON 직렬화 · 복사붙여넣기 · 캐스케이드 삭제 동작 상세 |

---

## License and Notices

- **License:** Apache License 2.0 (see `LICENSE`)
- **Notice:** Project notices and attribution information (see `NOTICE`)
- **Patent information:** See `PATENTS.md` and the official page:
  http://dualsoft.co.kr/HelpDS/patents/patents.html
- **Commercial services / enterprise support:** See `COMMERCIAL.md`
