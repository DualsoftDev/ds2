<div align="center">

# DS2 Sequence Control Editor


[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![F#](https://img.shields.io/badge/F%23-Core_Engine-378BBA?logo=fsharp&logoColor=white)](https://fsharp.org/)
[![C#](https://img.shields.io/badge/C%23-WPF_UI-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![Tests](https://img.shields.io/badge/Tests-174_passing-brightgreen)](#빌드-및-테스트)

---

[Architecture](#아키텍처) · [Entities](#엔티티-관계도) · [Build](#빌드-및-테스트) · [Runtime Docs](RUNTIME.md)

</div>

> **Last Sync:** 2026-03-17 — CSV 변환기, AASX/Mermaid 모듈 분리, CallCreate TabControl 개편, ApiCall 복제 모드, Promaker.Tests

## 핵심 설계 원칙

```mermaid
mindmap
  root((Promaker))
    편집 코어 분리
      F# 레이어에 로직 집중
      UI 기술 변경에도 재사용
    증분 Undo/Redo
      변경 엔티티만 클로저 추적
      1 Undo = 1 사용자 제스처
    레이어 경계 강제
      C#은 wiring/binding/rendering만
      상태 변경은 F# DsStore 경유
```

---

## 아키텍처

### 전체 구조

```mermaid
block-beta
  columns 4

  UITITLE["🖥️ Promaker (C# · WPF)"]:4
  MW["MainWindow"] EC["EditorCanvas"] VM["ViewModels"] DLG["Dialogs"]

  space:4

  UICORETITLE["⚙️ Ds2.UI.Core (F# · 편집 코어)"]:4
  DS["DsStore\n13 Dictionaries\nUndo/Redo\nEvents & File I/O"]
  ST["Store Extensions\nNodes · Arrows\nPanel · Paste"]
  PJ["Projections\nTreeProjection\nCanvasProjection"]
  QR["Queries\nHierarchy\nSelection\nConnection"]

  space:4

  CORETITLE["📦 Ds2.Core (F# · 순수 도메인)"]:4
  ENT["Entities\nProject · System\nFlow · Work · Call"]
  TYP["Types\nProperties\nEnum · Class"]
  VS["ValueSpec\nBool·Int·Float\nString·Range"]
  SER["Serialization\nJsonConverter\nDeepCopyHelper"]

  UITITLE --> UICORETITLE
  UICORETITLE --> CORETITLE
```

### 레이어 의존 방향

```mermaid
graph LR
  PM["<b>Promaker</b><br/>C#, WPF"]
  UIC["<b>Ds2.UI.Core</b><br/>F#, 편집 코어"]
  CORE["<b>Ds2.Core</b><br/>F#, 도메인"]
  AASX["<b>Ds2.Aasx</b><br/>F#, AASX I/O"]
  MER["<b>Ds2.Mermaid</b><br/>F#, Mermaid 변환"]
  CSV["<b>Ds2.CSV</b><br/>F#, CSV I/O"]

  PM -->|편집 API| UIC
  PM -->|도메인 타입| CORE
  PM --> AASX
  PM --> MER
  PM --> CSV
  UIC --> CORE
  AASX --> UIC
  AASX --> CORE
  MER --> UIC
  MER --> CORE
  CSV --> UIC
  CSV --> CORE

  style PM fill:#4a90d9,color:#fff,stroke:#2c5f8a
  style UIC fill:#7b68ee,color:#fff,stroke:#5a4db5
  style CORE fill:#6b8e23,color:#fff,stroke:#4a6319
  style AASX fill:#cd853f,color:#fff,stroke:#8b5e2b
  style MER fill:#cd853f,color:#fff,stroke:#8b5e2b
  style CSV fill:#cd853f,color:#fff,stroke:#8b5e2b
```

> - 상위 레이어는 하위 레이어만 의존합니다
> - `Ds2.UI.Core → Ds2.Aasx` 순환 의존은 없습니다
> - C#용 공유 타입(`EntityKind`, `TabKind` 등)은 `Ds2.UI.Core/Core/Types.fs`에서 정의

---

## 엔티티 관계도

```mermaid
erDiagram
    Project ||--o{ DsSystem : "contains (Active)"
    Project ||--o{ DsSystem : "contains (Passive/Device)"

    DsSystem ||--o{ Flow : contains
    DsSystem ||--o{ ArrowBetweenWorks : owns
    DsSystem ||--o{ ApiDef : "defines (Device)"
    DsSystem ||--o{ HwButton : "has (Device)"
    DsSystem ||--o{ HwLamp : "has (Device)"
    DsSystem ||--o{ HwCondition : "has (Device)"
    DsSystem ||--o{ HwAction : "has (Device)"

    Flow ||--o{ Work : contains

    Work ||--o{ Call : contains
    Work ||--o{ ArrowBetweenCalls : owns

    Call ||--o{ ApiCall : "has (.ApiCalls[])"
    Call ||--o{ CallCondition : "has conditions"
    Call }o--|| DsSystem : "references (.ApiDefId → Device)"

    ApiCall }o--|| ApiDef : "linked by .ApiDefId"

    CallCondition ||--o{ ApiCall : "condition targets"
```

### 엔티티 설명

| 구분 | 설명 |
|:-----|:-----|
| **Active System** | 제어 흐름 트리 — `Flow → Work → Call` |
| **Passive System** | 장치 정의 트리 — `ApiDef`, HW 컴포넌트 |
| **ArrowBetweenWorks** | DsSystem의 자식, Work↔Work 연결선 (`parentId = systemId`) |
| **ArrowBetweenCalls** | Work의 자식, Call↔Call 연결선 (`parentId = workId`) |
| **Call.Name** | `DevicesAlias + "." + ApiName` (computed, Rename 시 DevicesAlias만 변경) |
| **ApiCall** | ApiDef 실행 1건 (OutTag/InTag 주소, OutputSpec/InputSpec 포함) |
| **CallCondition** | Call 동작 조건 (Active/Auto/Common 타입, IsOR, IsRising, 조건 ApiCall 목록) |

---

## 편집 흐름

하나의 편집 동작이 시스템을 통과하는 전체 경로:

```mermaid
sequenceDiagram
    actor User
    participant WPF as Promaker (C#)<br/>EditorCanvas / MainViewModel
    participant Ext as Ds2.UI.Core (F#)<br/>Store Extensions
    participant Undo as UndoRedoManager
    participant Event as EditorEvent

    User->>WPF: 입력 (키보드 / 마우스 / 메뉴)
    WPF->>Ext: store.Xxx(...) 호출

    rect rgb(240, 248, 255)
        Note over Ext: WithTransaction(label, action)
        Ext->>Ext: TrackAdd / TrackRemove / TrackMutate
        Note over Ext: 실패 시 UndoRecord 역순 실행 → 자동 복원
    end

    Ext->>Undo: UndoTransaction 기록

    Ext->>Event: 이벤트 발행

    Event-->>WPF: StoreRefreshed → UI 전체 재구성
    Event-->>WPF: HistoryChanged → Undo/Redo 버튼 갱신
    Event-->>WPF: SelectionChanged → 속성 패널 갱신

    WPF->>WPF: RebuildAll → WPF 바인딩 갱신 → 화면 반영
```

### 증분 Undo/Redo 설계

```mermaid
graph TD
    subgraph "Track 헬퍼"
        TA["TrackAdd"] --> UR["UndoRecord 생성"]
        TR["TrackRemove"] --> UR
        TM["TrackMutate"] --> UR
        TG["TrackGuidSetAdd/Remove"] --> UR
    end

    UR --> WT["WithTransaction<br/>여러 Track → 1 UndoTransaction"]
    WT -->|성공| US["Undo Stack에 push"]
    WT -->|실패| RB["UndoRecord 역순 실행 → 롤백"]

    US --> UNDO["Undo 실행"]
    UNDO --> RW["RewireApiCallReferences<br/>Call↔ApiCall 참조 재연결"]

    style WT fill:#e8f5e9,stroke:#388e3c
    style RB fill:#ffebee,stroke:#c62828
```

> **제약**: `store.GetProject(id).Name <- "new"` 같은 직접 필드 수정은 Undo 추적 불가. 변경은 반드시 `store.메서드()` 경유
>
> 상세 내용: [`RUNTIME.md`](RUNTIME.md)

---

## 솔루션 구조

```mermaid
graph TD
    subgraph Solutions["Solutions/Ds2.sln"]
        direction TB

        subgraph Core["Core/"]
            DC["Ds2.Core<br/><sub>순수 도메인 타입<br/>9개 모듈</sub>"]
            DUC["Ds2.UI.Core<br/><sub>편집 코어 F#<br/>28개 모듈</sub>"]
        end

        subgraph Convert["Convert/"]
            AASX["Ds2.Aasx<br/><sub>AASX I/O<br/>12개 모듈</sub>"]
            MER["Ds2.Mermaid<br/><sub>Mermaid 변환<br/>11개 모듈</sub>"]
            CSV["Ds2.CSV<br/><sub>CSV I/O<br/>5개 모듈</sub>"]
        end

        subgraph Sim["Simulation/"]
            SIM["Ds2.Runtime.Sim<br/><sub>시뮬레이션 엔진</sub>"]
        end

        subgraph Tests["Tests/"]
            T1["Core.Tests<br/><sub>26개</sub>"]
            T2["UI.Core.Tests<br/><sub>102개</sub>"]
            T3["Integration.Tests<br/><sub>6개</sub>"]
            T4["Mermaid.Tests<br/><sub>30개</sub>"]
            T5["Promaker.Tests<br/><sub>10개</sub>"]
        end
    end

    subgraph Apps["Apps/Promaker/Promaker.sln"]
        PM["Promaker<br/><sub>WPF UI (C#)<br/>63+ files</sub>"]
    end

    style Solutions fill:#f8f9fa,stroke:#dee2e6
    style Apps fill:#f8f9fa,stroke:#dee2e6
    style Core fill:#e3f2fd,stroke:#90caf9
    style Convert fill:#fff3e0,stroke:#ffcc80
    style Sim fill:#e8f5e9,stroke:#a5d6a7
    style Tests fill:#fce4ec,stroke:#ef9a9a
```

테스트 합계: **174개** (26 Core + 102 UI.Core + 6 Integration + 30 Mermaid + 10 Promaker)

---

## 파일 구조 및 역할

<details>
<summary><b>📄 루트 문서</b></summary>

| 파일 | 역할 |
|------|------|
| `README.md` | 프로젝트 개요, 구조, 파일 역할 인수인계 문서 |
| `RUNTIME.md` | CRUD · Undo/Redo · JSON 직렬화 동작 상세 |
| `.editorconfig` | 코드 스타일/포맷 기본 규칙 |

</details>

<details>
<summary><b>📦 Ds2.Core — 순수 도메인 타입</b> (Store/Query/Mutation 없음)</summary>

| 파일 | 역할 |
|------|------|
| `AbstractClass.fs` | `DsEntity` 추상 베이스 타입, `DeepCopyHelper` |
| `Entities.fs` | Project · DsSystem · Flow · Work · Call · ApiDef · ApiCall · HW 엔티티 정의 |
| `Properties.fs` | WorkProperties · CallProperties · ApiDefProperties 등 속성 모델 |
| `Enum.fs` | `Status4`, `CallType`, `ArrowType`, `CallConditionType` 도메인 열거형 |
| `Class.fs` | `IOTag`, `Xywh` 등 값 타입 클래스 |
| `ValueSpec.fs` | `ValueSpec` DU (None / Bool / Int / Float / String / Range 등) |
| `Nameplate.fs` | AASX Nameplate Submodel 데이터 타입 |
| `HandoverDocumentation.fs` | AASX HandoverDocumentation Submodel 데이터 타입 |
| `JsonConverter.fs` | `System.Text.Json` 기반 직렬화 옵션 및 커스텀 컨버터 |

</details>

<details>
<summary><b>⚙️ Ds2.UI.Core — 편집 코어 (F#)</b> — 컴파일 순서 = 의존 순서</summary>

| # | 파일 | 역할 |
|:---:|------|------|
| 1 | `Core/Types.fs` | `UndoRecord`/`UndoTransaction`, `Labels`, `EntityKind`, `EditorEvent` DU, `TabKind` |
| 2 | `Commands/UndoRedoManager.fs` | `LinkedList<UndoTransaction>` 기반 undo/redo 스택 관리 |
| 3 | `Core/DsStore.fs` | `DsStore` 타입 — 13개 Dictionary + Undo/Redo + 이벤트 + File I/O |
| 4 | `Core/DsQuery.fs` | 엔티티 조회 쿼리 (`getXxx`, `allXxxs`, `xxxsOf`) |
| 5 | `Queries/CallConditionQueries.fs` | CallCondition 조회 쿼리 |
| 6 | `Geometry/ArrowPathCalculator.fs` | 화살표 polyline 경로 계산 (직교 꺾임) |
| 7 | `Projection/ViewTypes.fs` | `TreeNodeInfo` · `CanvasNodeInfo` · `SelectionKey` |
| 8 | `Projection/PropertyPanelValueSpec.fs` | ValueSpec 포맷/파싱 |
| 9 | `Projection/TreeProjection.fs` | Store → 트리 데이터 변환 |
| 10–12 | `Projection/CanvasLayout/*.fs` | 자동 배치 (Layering → Placement → Entry) |
| 13 | `Projection/CanvasProjection.fs` | Store → 캔버스 콘텐츠 변환 |
| 14 | `Queries/EntityHierarchyQueries.fs` | 계층 역탐색, 탭 정보 해석 |
| 15 | `Queries/AddTargetQueries.fs` | Add System/Flow 대상 해석 |
| 16 | `Queries/SelectionQueries.fs` | 선택 정렬/범위/Ctrl+Shift |
| 17 | `Queries/ConnectionQueries.fs` | 화살표 연결 대상 해석, 순서 연결 |
| | **Store/ — `[<Extension>]` C# 확장 메서드** | |
| 18 | `Store/DsStore.Log.fs` | 공유 로깅 + require 헬퍼 |
| 19 | `Store/DsStore.Paste.fs` | 붙여넣기 — ApiCall별 Device System 독립 매핑 |
| 20 | `Store/DsStore.Queries.fs` | 읽기 전용 쿼리 래퍼 |
| 21 | `Store/DsStore.Nodes.Remove.fs` | 캐스케이드 삭제 |
| 22 | `Store/DsStore.Nodes.Device.fs` | 디바이스/HW CRUD + ApiCall 복제 |
| 23 | `Store/DsStore.Nodes.fs` | CRUD/이동/삭제 — `AddCallWithMultipleDevicesResolved` |
| 24 | `Store/DsStore.Arrows.fs` | 화살표 — RemoveArrows, ReconnectArrow |
| 25 | `Store/DsStore.Panel.fs` | 속성 패널 — Time/Conditions CRUD |
| 26 | `Store/DsStore.Panel.Api.fs` | ApiDef/ApiCall CRUD |
| 27 | `Store/DsStore.Panel.Batch.fs` | Duration/IO 일괄 편집 |

</details>

<details>
<summary><b>🔄 Ds2.Aasx — AASX I/O (F#)</b></summary>

| 파일 | 역할 |
|------|------|
| `AasxSemantics.fs` | idShort 상수 + Nameplate/Documentation 상수 |
| `AasxFileIO.fs` | AASX ZIP 읽기/쓰기 |
| `Import/Core.fs` | 임포트 공통 헬퍼 |
| `Import/Graph.fs` | SMC/SML → 엔티티 재구성 |
| `Import/Metadata.fs` | Nameplate/Documentation 임포트 |
| `Import/Entry.fs` | `importFromAasxFile` 진입점 |
| `Export/Core.fs` | 익스포트 공통 헬퍼 |
| `Export/Graph.fs` | DsStore → SMC/SML 직렬화 |
| `Export/Metadata.fs` | Nameplate/Documentation 익스포트 |
| `Export/Entry.fs` | `exportFromStore` 진입점 |
| `Concepts/Builder.fs` | ConceptDescription 빌더 |
| `Concepts/Catalog.fs` | 41개 IRDI 카탈로그 |

</details>

<details>
<summary><b>🖥️ Promaker — WPF UI (C#)</b></summary>

#### ViewModels/Shell/

| 파일 | 역할 |
|------|------|
| `MainViewModel.cs` | 핵심 필드/컬렉션, NewProject/Undo/Redo, Reset, UpdateTitle |
| `EditorGuards.cs` | DsStore 확장 메서드 호출 공통 예외 처리 가드 |
| `EventHandling.cs` | `WireEvents` + `HandleEvent` + `ApplyEntityRename` |
| `FileCommands.cs` | JSON/AASX/Mermaid Open/Save |
| `SaveOutcomeFlow.cs` | Mermaid/AASX 저장 결과 처리 |
| `MermaidImportCommands.cs` | Mermaid 다이어그램 가져오기 |
| `CsvCommands.cs` | CSV 가져오기/내보내기 |
| `DurationBatchCommands.cs` | Duration 일괄 설정 |
| `IoBatchCommands.cs` | I/O 태그 일괄 설정 |
| `DiscardChangesFlow.cs` | 미저장 변경사항 확인/폐기 흐름 |

#### ViewModels/PropertyPanel/

| 파일 | 역할 |
|------|------|
| `PropertyPanelState.cs` | 속성 패널 공용 Collections/Properties |
| `PropertyPanelItems.cs` | 보조 뷰모델 타입 (`CallApiCallItem`, `CallConditionItem` 등) |
| `CallPanel.cs` | Call 속성 패널 — ApplyCallTimeout, RefreshCallPanel |
| `CallPanel.ApiCalls.cs` | ApiCall CRUD 메서드 |
| `CallPanel.Conditions.cs` | CallCondition CRUD, ReloadConditions |
| `SystemPanel.cs` | System 속성 패널 — ApiDef CRUD |

#### ViewModels/Simulation/

| 파일 | 역할 |
|------|------|
| `SimulationPanelState.cs` | 시뮬레이션 패널 상태 |
| `SimulationPanelState.Canvas.cs` | 시뮬레이션 캔버스 렌더링 |
| `SimulationPanelState.Events.cs` | 시뮬레이션 이벤트 처리 |
| `GanttChartState.cs` | Gantt 차트 뷰모델 상태 |

#### Controls/

| 파일 | 역할 |
|------|------|
| `Canvas/EditorCanvas.xaml(.cs)` | 캔버스 UI + AddWork/AddCall 클릭 |
| `Canvas/EditorCanvas.Input.cs` | 마우스·키보드 입력 (드래그, Delete, 연결) |
| `Canvas/EditorCanvas.Selection.cs` | 박스 선택, 화살표 선택 |
| `Canvas/EditorCanvas.Navigation.cs` | 줌·패닝, FitToView |
| `Canvas/EditorCanvas.Connect.cs` | 화살표 연결 시작/완료/취소 |
| `PropertyPanel/PropertyPanel.xaml(.cs)` | 속성 패널 루트 UserControl |
| `PropertyPanel/ConditionSectionControl.xaml(.cs)` | CallCondition 섹션 공통 UserControl |
| `PropertyPanel/ValueSpecEditorControl.xaml(.cs)` | ValueSpec 인라인 편집 컨트롤 |
| `Shell/ExplorerPane.xaml(.cs)` | 좌측 탐색기 패널 (트리 뷰) |
| `Shell/MainToolbar.xaml(.cs)` | 상단 툴바 |
| `Simulation/SimulationPanel.xaml(.cs)` | 시뮬레이션 패널 |
| `Simulation/GanttChartControl.xaml(.cs)` | Gantt 차트 컨트롤 |

#### Dialogs/

| 파일 | 역할 |
|------|------|
| `CallCreateDialog.xaml(.cs)` | Call 생성 — CallReplication/ApiCallReplication/ApiDefPicker 모드 |
| `ApiCallCreateDialog.xaml(.cs)` | ApiCall 생성 |
| `ApiCallSpecDialog.xaml(.cs)` | ApiCall InTag/OutTag/ValueSpec 편집 |
| `ApiDefEditDialog.xaml(.cs)` | ApiDef 속성 편집 |
| `ArrowTypeDialog.xaml(.cs)` | 화살표 유형 선택 (Start/Reset/StartReset/Group) |
| `ConditionApiCallPickerDialog.xaml(.cs)` | 조건 ApiCall 선택 |
| `DurationBatchDialog.xaml(.cs)` | Duration 일괄 설정 |
| `IoBatchSettingsDialog.xaml(.cs)` | I/O 태그 일괄 설정 |
| `CsvExportDialog.xaml(.cs)` | CSV 내보내기 옵션 |
| `CsvImportDialog.xaml(.cs)` | CSV 불러오기 (미리보기 + 매핑) |
| `MermaidImportDialog.xaml(.cs)` | Mermaid 텍스트 가져오기 |
| `ProjectPropertiesDialog.xaml(.cs)` | 프로젝트 속성 편집 |
| `ValueSpecDialog.xaml(.cs)` | ValueSpec 독립 편집 |

</details>

<details>
<summary><b>🧪 테스트 프로젝트</b></summary>

| 파일 | 역할 |
|------|------|
| `Ds2.Core.Tests/Tests.fs` | Core 엔티티·DeepCopy·ValueSpec 단위 테스트 (25개) |
| `Ds2.Core.Tests/JsonConverterTests.fs` | JSON 직렬화 라운드트립 테스트 |
| `Ds2.UI.Core.Tests/DsStoreTests.fs` | DsStore CRUD·Undo/Redo·캐스케이드·복사붙여넣기·패널 테스트 |
| `Ds2.UI.Core.Tests/ViewProjectionTests.fs` | Tree/Canvas Projection·Selection·Query 테스트 |
| `Ds2.UI.Core.Tests/TestHelpers.fs` | 테스트 헬퍼 |
| `Ds2.Integration.Tests/Tests.fs` | 통합 시나리오 테스트 (6개) |
| `Ds2.Mermaid.Tests/Tests.fs` | Mermaid 변환 단위 테스트 (16개) |

</details>

---

## 로깅 (log4net)

log4net 2.0.17이 F#+C# 전 레이어에 적용되어 있습니다.

<details>
<summary><b>로깅 설정 상세</b></summary>

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
|------|:----:|------|
| 앱 시작/종료 | `INFO` | `=== Promaker startup ===` |
| 전역 미처리 예외 | `FATAL` | `DispatcherUnhandledException` + 스택 트레이스 |
| EditorEvent 구독자 에러 | `ERROR` | `EditorEvent 구독자 에러` + 예외 |
| JSON 파일 열기/저장 성공 | `INFO` | `파일 열기/저장 완료: {path}` |
| JSON 파일 열기/저장 실패 | `ERROR` | 예외 포함 |
| AASX import/export 성공 | `INFO` | 경로 포함 |
| AASX import 빈 결과 | `WARN` | |
| AASX import ReplaceStore 실패 | `ERROR` | 예외 포함 → DialogHelpers.Warn |
| AASX export 실패 | `WARN`/`ERROR` | |
| AASX 내부 파싱 실패 | `WARN` | 함수명 + 예외 포함 |
| AASX ZIP 읽기 실패 | `WARN` | 예외 객체 포함 |
| `WithTransaction` 성공 | `DEBUG` | `Executed: {label}` |
| `WithTransaction` 실패 | `ERROR` | `Transaction failed: {label}` + 예외 |
| Undo/Redo 성공 | `DEBUG` | `Undo: {명령 레이블}` |
| Undo/Redo 실패 | `ERROR` | 예외 포함 |

### 패키지 적용 범위

| 프로젝트 | 로거 선언 방식 |
|---------|-------------|
| `Ds2.UI.Core` (F#) | `LogManager.GetLogger(typedefof<DsStore>)` |
| `Ds2.Aasx` (F#) | `LogManager.GetLogger("Ds2.Aasx.AasxFileIO")` |
| `Promaker` (C#) | `LogManager.GetLogger(typeof(App))` / `typeof(MainViewModel)` |

</details>

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
|:-----|:-----|
| [`RUNTIME.md`](RUNTIME.md) | CRUD · Undo/Redo · JSON 직렬화 · 복사붙여넣기 · 캐스케이드 삭제 동작 상세 |

---

## License and Notices

| | |
|:--|:--|
| **License** | Apache License 2.0 — see [`LICENSE`](LICENSE) |
| **Notice** | Project notices and attribution — see [`NOTICE`](NOTICE) |
| **Patents** | See [`PATENTS.md`](PATENTS.md) and [dualsoft.co.kr/HelpDS/patents](http://dualsoft.co.kr/HelpDS/patents/patents.html) |
| **Commercial** | Enterprise support — see [`COMMERCIAL.md`](COMMERCIAL.md) |
