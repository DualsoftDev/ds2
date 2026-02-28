# Ds2.Promaker

Last Sync: 2026-02-28 (Undo History 패널 구현 + EditorApi 헬퍼 추출 리팩토링)

## 프로젝트 목표

Ds2.Promaker 프로젝트는 다음 세 가지를 설계를 중심으로 **Seqeunce control editor**를 구현 중입니다.

- **편집 코어(F#) 분리**: 추가/삭제/이동/연결/복사/붙여넣기의 로직을 F# 레이어에 집중시켜 UI 기술이 바뀌어도 재사용 가능
- **사용자 제스처 단위 Undo/Redo**: 복잡한 다중 동작도 Composite 1건으로 기록해 Undo 1회 = 1 제스처를 보장
- **레이어 경계 강제**: C#(WPF)는 wiring·binding·rendering만, 편집 상태 변경은 반드시 F# `EditorApi` 경유

---

## 아키텍처 조감도

```
╔══════════════════════════════════════════════════════════════════════════════╗
║                           Ds2.Promaker 전체 구조                              ║
╠══════════════════════════════════════════════════════════════════════════════╣
║                                                                              ║
║  ┌──────────────── Ds2.UI.Frontend  (C#, WPF) ────────────────────────────┐  ║
║  │                                                                        │  ║
║  │  MainWindow  │  EditorCanvas (Input/Select/Nav/Connect)                │  ║
║  │  ViewModels  │  Dialogs                                                │  ║
║  │                                                                        │  ║
║  │          역할: wiring / binding / rendering 만 담당                     │  ║
║  │          금지: 직접 Store 수정, 자체 Undo 스택, 비즈니스 로직            │  ║
║  └────────────────────────┬───────────────────────────────────────────────┘  ║
║                           │ 편집: EditorApi.Xxx(...)   ▲ EditorEvent         ║
║           ┌───────────────┘ 조회: Query/Projection + DsStore                 ║
║           │                                           │ (StoreRefreshed 등)  ║
║           │               ┌───────────────────────────┘                      ║
║           ▼               ▼                                                  ║
║  ┌──────────────── Ds2.UI.Core  (F#) ─────────────────────────────────────┐  ║
║  │                                                                        │  ║
║  │  Store:  DsStore  DsQuery.*  Mutation.*  ValidationRules               │  ║
║  │                                                                        │  ║
║  │  EditorApi  ──►  CommandExecutor  ──►  UndoRedoManager                 │  ║
║  │  (진입점)         (execute / undo         (undo/redo 스택)              │  ║
║  │                   DU 패턴 매칭)                                         │  ║
║  │       │                                                                │  ║
║  │       ├── Editing:   RemoveOps  ArrowOps  DeviceOps                    │  ║
║  │       │              PasteOps  PanelOps  (명령 조립 헬퍼)               │  ║
║  │       │                                                                │  ║
║  │       ├── Projection: TreeProjection  CanvasProjection                 │  ║
║  │       │               (Store → 트리/캔버스 뷰 데이터)                   │  ║
║  │       │                                                                │  ║
║  │       └── Queries:   EntityHierarchy  Selection  Connection            │  ║
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
║  ┌──────────── Ds2.Aasx  (F#) ──────────────┐  ┌── Ds2.Core.Contracts ───┐   ║
║  │  AasxSemantics  idShort 상수 정의         │  │  ArrowType (enum)       │   ║
║  │  AasxFileIO     AASX ZIP 읽기/쓰기        │  │  CallConditionType      │   ║
║  │  AasxExporter   DsStore → AASX           │  │  Xywh (class)           │   ║
║  │  AasxImporter   AASX → DsStore           │  │                         │   ║
║  │                 (AasCore.Aas3_0 v1.0.0)  │  │  Ds2.Core 참조 없음      │   ║
║  │  → Ds2.Core + Ds2.UI.Core 양쪽 참조       │  │  C# 측 공유 타입 전용    │   ║
║  │  ← UI.Core 에서는 참조 없음 (순환 방지)    │  │                         │   ║
║  └──────────────────────────────────────────┘  └─────────────────────────┘   ║
╚══════════════════════════════════════════════════════════════════════════════╝
```

### 레이어 의존 방향

```
Ds2.UI.Frontend  →  Ds2.UI.Core  →  Ds2.Core  →  Ds2.Core.Contracts
     (C#, WPF)        (F#)            (F#)              (F#)
         │                                ▲                  ▲
         ├──────►  Ds2.Aasx  ─────────────┘                  │
         │          (F#)      + Ds2.UI.Core 참조              │
         └──────────────────────────────────────────────────┘
                         (직접 참조 — Ds2.Core 없이 공유 타입 접근)
```

상위 레이어는 하위 레이어만 의존합니다.
`Ds2.Aasx`는 `Ds2.UI.Frontend`에서 직접 참조되며 `Ds2.UI.Core → Ds2.Aasx` 순환 의존은 없습니다.
`Ds2.UI.Frontend`는 `Ds2.Core.Contracts`를 직접 참조하여 `Ds2.Core` 없이도 공유 타입(`ArrowType`, `Xywh` 등)에 접근합니다.

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
solutions/Ds2.Promaker/
  Ds2.Core/                # 순수 도메인 타입 (Entities, Properties, Enum, ValueSpec, JsonConverter)
  Ds2.UI.Core/             # 편집 코어(F#) — 24개 모듈 (DsStore/Query/Mutation 포함)
  Ds2.Aasx/                # AASX I/O(F#) — 4개 모듈 (AasCore.Aas3_0 v1.0.0 기반 AASX 양방향 변환)
  Ds2.Database/            # 데이터 계층
  Ds2.UI.Frontend/         # WPF UI(C#) — 28개 파일
  Ds2.Core.Tests/          # Core 단위 테스트 (24개)
  Ds2.UI.Core.Tests/       # UI.Core 단위 테스트 (119개)
  Ds2.Integration.Tests/   # 통합 테스트 (13개)
  Ds2.Promaker.sln
```

테스트 합계: **155개** (24 + 119 + 13)

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
| `Enum.fs` | `Status4`, `CallType` 도메인 열거형 (`ArrowType`/`CallConditionType`은 Ds2.Core.Contracts로 이전) |
| `Class.fs` | `IOTag` 등 값 타입 클래스 (`Xywh`는 Ds2.Core.Contracts로 이전) |
| `ValueSpec.fs` | `ValueSpec` DU (None / Bool / Int / Float / String / Range 등) |
| `JsonConverter.fs` | `System.Text.Json` 기반 직렬화 옵션 및 커스텀 컨버터 |

---

### Ds2.Core.Contracts — C# 공유 타입 격리 (F#)

`Ds2.Core`와 `Ds2.UI.Frontend`가 공통으로 사용하는 단순 타입을 격리한 독립 프로젝트. 외부 참조 없음.

| 파일 | 역할 |
|------|------|
| `Enum.fs` | `ArrowType` (None/Start/Reset/StartReset/ResetReset/Group), `CallConditionType` (Active/Auto/Common) |
| `Class.fs` | `Xywh(x,y,w,h)` — 위치/크기 값 객체 |

- **`Ds2.Core`가 참조**: 도메인 코드에서 `ArrowType`, `Xywh` 등 재사용
- **`Ds2.UI.Frontend`가 직접 참조**: `Ds2.Core` 없이도 C#에서 공유 타입에 접근 (F# 런타임 타입 노출 방지)
- **`Ds2.Aasx`는 전이 참조**: `Ds2.Core → Ds2.Core.Contracts` 경로로 사용

---

### Ds2.UI.Core — 편집 코어 (F#)

컴파일 순서 = 의존 순서입니다. 위 파일을 아래 파일에서만 의존합니다.

| # | 파일 | 역할 |
|---|------|------|
| 1 | `Core/DsStore.fs` | `DsStore` 타입 (13개 컬렉션 + ReadOnly 뷰), `StoreCopy.replaceAllCollections` |
| 2 | `Core/DsQuery.fs` | `DsQuery.*` — 엔티티 조회 쿼리 (`getXxx`, `allXxxs`, `xxxsOf`) |
| 3 | `Core/Mutation.fs` | `Mutation.*` — 엔티티 추가/수정/삭제 (특수: removeSystem, removeCall, removeApiCall) |
| 4 | `Core/EditorTypes.fs` | `EditorCommand` DU, `CommandLabel.ofCommand`(전 케이스 → 한국어 레이블), `EditorEvent` DU(`HistoryChanged` 포함), `EntityKind` DU, `CallCopyContext` DU, `EntityNameAccess` |
| 5 | `Core/ValidationRules.fs` | 이름 · 주소 · 값 검증 규칙 (`ProjectValidation`, `OneToOneValidation` 등) |
| 6 | `Commands/CommandExecutor.fs` | `EditorCommand` DU 패턴 매칭 → `DsStore` Mutation 실행, `requireMutationOk`로 에러 보장 |
| 7 | `Commands/UndoRedoManager.fs` | `LinkedList` 기반 undo/redo 스택 관리, maxSize O(1) trim. `UndoLabels`/`RedoLabels` 프로퍼티로 레이블 목록 노출 |
| 8 | `Geometry/ArrowPathCalculator.fs` | Work/Call 캔버스 화살표 polyline 경로 계산 (직교 꺾임) |
| 9 | `Projection/ViewTypes.fs` | `TreeNodeInfo` · `CanvasNodeInfo` · `SelectionKey` 등 뷰 전용 레코드 |
| 10 | `Projection/TreeProjection.fs` | `DsStore` → 트리 데이터 변환 (`buildTrees`: 컨트롤 트리 + 디바이스 트리) |
| 11 | `Projection/CanvasProjection.fs` | `DsStore` → 캔버스 콘텐츠 변환 (노드 위치, 화살표 포인트) |
| 12 | `Queries/EntityHierarchyQueries.fs` | 계층 역탐색 (stepCallToWork / stepWorkToFlow / stepFlowToSystem), 탭 정보 해석, ApiDef 검색 |
| 13 | `Queries/AddTargetQueries.fs` | Add System/Flow 대상 해석 |
| 14 | `Queries/SelectionQueries.fs` | 캔버스 선택 정렬/범위 선택/Ctrl+Shift 다중 선택 해석 |
| 15 | `Queries/ConnectionQueries.fs` | 화살표 연결 가능 대상 Flow 해석, 선택 순서 연결 |
| 16 | `Editing/EditorApi.CascadeHelpers.fs` | 캐스케이드 삭제용 저수준 유틸리티 (Arrow 수집, 하위 ID 수집) |
| 17 | `Editing/EditorApi.RemoveOps.fs` | `buildRemoveXxxCmd` — 엔티티별 삭제 명령 조립 (Composite 포함). `buildRemoveEntitiesCmds` — 다중 선택 일괄 삭제 |
| 18 | `Editing/EditorApi.ArrowOps.fs` | 화살표 Add/Remove 명령 조립, ReconnectArrow |
| 19 | `Editing/EditorApi.DeviceOps.fs` | `AddCallsWithDevice` — Passive System(`{flowName}_{devAlias}` 이름)/ApiDef 자동 생성 후 Call 일괄 추가. `buildAddCallWithLinkedApiDefsCmd` — 단일 Call + 연결 ApiDef ApiCall Composite 빌드 |
| 20 | `Editing/EditorApi.PasteResolvers.fs` | 복사 가능 타입 판정, 붙여넣기 대상(System/Flow/Work) 해석 |
| 21 | `Editing/EditorApi.PasteOps.fs` | 붙여넣기 명령 조립, `CallCopyContext` 기반 ApiCall 공유/복제 분기. DifferentFlow 시 `DevicePasteState`로 Device System 복제/재사용. `dispatchPaste` — 엔티티 타입별 붙여넣기 라우팅 진입점 |
| 22 | `Editing/EditorApi.PropertyPanel.fs` | 속성 패널 전용 타입: `DeviceApiDefOption`, `CallApiCallPanelItem`, `CallConditionApiCallItem`, `CallConditionPanelItem`, `PropertyPanelValueSpec` |
| 23 | `Editing/EditorApi.PanelOps.fs` | 패널 데이터 조회(`getWorkDurationText`, `getCallTimeoutText`, `getCallApiCallsForPanel`, `getAllApiCallsForPanel`, `getCallConditionsForPanel` 등) 및 ApiCall/CallCondition 커맨드 빌더(`buildAddApiCallsToConditionBatchCmd`, `buildUpdateApiDefPropertiesCmd`, `buildRemoveApiCallFromCallCmd` 포함) |
| 24 | `Editing/EditorApi.fs` | **외부 진입 API** — `Undo`, `Redo`, `UndoTo(steps)`, `RedoTo(steps)`, `LoadFromFile`(백업+롤백), `ReplaceStore`(외부 I/O 경로용 store 전체 교체 + StoreRefreshed 발행). 내부 헬퍼: `RunUndoRedoStep`, `RunBatchedSteps`, `ApplyNewStore`. `suppressEvents` 플래그로 배치 이벤트 폭증 방지. Add* 6개 `internal` 전용, `AndGetId` 공개 래퍼. `ExecuteCommand`는 private, `Exec`/`ExecBatch`는 internal |

---

### Ds2.Aasx — AASX I/O (F#)

| 파일 | 역할 |
|------|------|
| `AasxSemantics.fs` | Ds2 전용 idShort 상수 (`Ds2SequenceControlSubmodel`, `Name_`, `Guid_` 등) |
| `AasxFileIO.fs` | AASX ZIP 읽기(`readEnvironment`) / 쓰기(`writeEnvironment`) — AasCore.Aas3_0 Xmlization 사용 |
| `AasxExporter.fs` | `DsStore` → AASX 변환 (`exportToAasxFile`): Project 계층을 SMC/SML로 직렬화, 복잡한 타입은 JSON Property로 저장 |
| `AasxImporter.fs` | AASX → `DsStore option` 역변환 (`importFromAasxFile`): SMC/SML 파싱 후 엔티티 재구성 |

---

### Ds2.UI.Frontend — WPF UI (C#)

| 파일 | 역할 |
|------|------|
| `App.xaml / App.xaml.cs` | 앱 리소스 루트 및 시작 코드 |
| `EntityTypes.cs` | Entity type 문자열 상수 (`"Work"`, `"Call"` 등) + `Is` / `IsWorkOrCall` / `IsCanvasOpenable` 헬퍼 |
| `MainWindow.xaml` | 메인 화면 레이아웃 (트리 패널 / 캔버스 탭 / 속성 패널) |
| `MainWindow.xaml.cs` | 트리·탭·메뉴 이벤트 wiring, `EditorApi` 호출 진입 |
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
| `ViewModels/MainViewModel.cs` | `HandleEvent` 허브, 파일 I/O, Undo/Redo, 리셋 |
| `ViewModels/MainViewModel.FileIO.cs` | AASX 임포트(`ImportAasxCommand`) / 익스포트(`ExportAasxCommand`) RelayCommand |
| `ViewModels/MainViewModel.Selection.cs` | 트리·캔버스 선택 동기화 |
| `ViewModels/MainViewModel.CanvasTabs.cs` | 탭 상태, `RebuildAll` (트리+캔버스 전체 재구성) |
| `ViewModels/MainViewModel.PropertiesPanel.cs` | 속성 패널 커맨드 (ApiCall CRUD, Call Conditions CRUD, ValueSpec, 더티 추적) |
| `ViewModels/MainViewModel.PropertyPanelItems.cs` | 속성 패널 보조 뷰모델 타입: `CallApiCallItem`, `DeviceApiDefOptionItem`, `CallConditionItem`, `ConditionApiCallRow`, `ConditionSectionItem` |
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
| `Ds2.UI.Core.Tests/EditorApiTests.fs` | EditorApi CRUD·Undo/Redo·캐스케이드 테스트 |
| `Ds2.UI.Core.Tests/ViewProjectionTests.fs` | Tree/Canvas Projection·Selection·Query 테스트 |
| `Ds2.UI.Core.Tests/TestHelpers.fs` | 테스트 헬퍼 |
| `Ds2.Integration.Tests/Tests.fs` | 통합 시나리오 테스트 (13개) |

---

## 편집 한 동작의 전체 경로

```
사용자 입력 (키보드 / 마우스 / 메뉴)
    │
    ▼  C# — EditorCanvas / MainViewModel
    │  편집 입력 해석 → EditorApi.Xxx() 호출
    │  조회/탭/투영 계산은 Query/Projection + DsStore 사용
    │
    ▼  F# — EditorApi.fs
    │  EditorCommand 조립 (Single or Composite)
    │  → this.Exec(cmd)
    │
    ▼  F# — CommandExecutor.execute(cmd, store)
    │  DU 패턴 매칭 → DsStore Mutation 실행
    │
    ▼  F# — ValidationHelpers.ensureValidStoreOrThrow
    │  검증 실패 시: undo() → StoreRefreshed + HistoryChanged 발행 → 예외
    │
    ▼  F# — UndoRedoManager
    │  undoList.AddFirst / redoList.Clear
    │
    ▼  F# — EditorApi: EditorEvent 발행
    │  StoreRefreshed       → UI 전체 재구성
    │  HistoryChanged       → History 패널 갱신 + Undo/Redo 버튼 상태 갱신
    │  SelectionChanged     → 속성 패널 갱신
    │
    ▼  C# — MainViewModel.HandleEvent(event)
       RebuildAll → WPF 바인딩 갱신 → 화면 반영
```

---

## 빌드 및 테스트

```bash
# 빌드
dotnet build solutions/Ds2.Promaker/Ds2.Promaker.sln -nologo

# 테스트
dotnet test solutions/Ds2.Promaker/Ds2.Promaker.sln -nologo
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
