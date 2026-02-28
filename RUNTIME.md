# RUNTIME.md

Last Sync: 2026-02-28 (MainViewModel.cs 분리 + log4net 로깅 도입)

이 문서는 **CRUD · Undo/Redo · JSON 직렬화 · 복사붙여넣기 · 캐스케이드 삭제** 의 런타임 동작을 상세히 설명합니다.

---

## 1. 편집 명령 실행 흐름

모든 편집 동작은 아래 경로를 따릅니다.

```
[사용자 입력] — 키보드 / 마우스 / 메뉴
      │
      ▼  C# — EditorCanvas.Input.cs / MainViewModel.cs
      │  편집 입력 해석 후 EditorApi.Xxx(...) 호출
      │  (조회/투영은 Query/Projection + DsStore 경로)
      │
      ▼  F# — EditorApi.fs
      │  EditorCommand (Single or Composite) 조립
      │  → this.Exec(cmd)
      │
      ▼  F# — CommandExecutor.execute(cmd, store)
      │  EditorCommand DU 패턴 매칭
      │  → DsStore Mutation 실행
      │
      ▼  F# — ensureValidStoreOrThrow
      │  Store 무결성 검증
      │  → 실패 시: CommandExecutor.undo → StoreRefreshed + HistoryChanged 발행 → 예외
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

## 2. CRUD

### 2.1 Create

각 엔티티 Add는 `EditorApi`에서 대응 명령을 조립해 `Exec`로 실행합니다.

| API | 명령 | 비고 |
|-----|------|------|
| `AddProject` | `AddProject` Single | |
| `AddSystem` | `AddSystem` Single | isPassive 플래그로 Active/Passive 구분 |
| `AddFlow` | `AddFlow` Single | |
| `AddWork` | `AddWork` Single | 캔버스 위치(Xywh) 포함 |
| `AddCallsWithDevice` | Composite | Passive System·ApiDef 자동 생성 + Call 일괄 추가를 하나의 Undo 단위로 기록 |
| `AddCallWithLinkedApiDefs` | Composite | 기존 ApiDef ID 목록 기반 Call 1개 + ApiCall N개를 Composite 1건으로 추가 (Undo 1회 보장) |
| `ConnectSelectionInOrder` | Composite (1개면 Single) | 선택 순서 기반 Work-Work 또는 Call-Call 화살표 N개 일괄 생성. 2-노드(단일 화살표) 포함 모두 이 진입점 사용. |
| `AddApiDef` | `AddApiDef` Single | |
| `AddApiCall` | `AddApiCall` Single | |

### 2.2 Read (Projection)

Store를 직접 읽지 않고 Projection을 통해 뷰 데이터를 얻습니다.

| Projection | 입력 | 출력 |
|------------|------|------|
| `TreeProjection.buildTrees` | `DsStore` | Control 트리 + Device 트리 (두 루트 리스트 반환) |
| `CanvasProjection.buildCanvas` | `DsStore` + tabKey | `CanvasNodeInfo list` + Arrow 좌표 |

**탭 열림 기준**:

| 더블클릭 대상 | 탭 종류 | 캔버스 표시 범위 |
|--------------|---------|----------------|
| Active System | System 탭 | 하위 모든 Flow의 Work + Work-Work 화살표 |
| Flow | Flow 탭 | 해당 Flow의 Work + Work-Work 화살표 |
| Work | Work 탭 | 해당 Work의 Call + Call-Call 화살표 |
| Call | (탭 없음) | 속성 패널만 갱신 |

### 2.3 Update

| 동작 | 명령 | 비고 |
|------|------|------|
| Work 이름 변경 | `RenameWork` Single | |
| Call DevicesAlias 변경 | `RenameCall` Single | `Call.Name` = DevicesAlias + "." + ApiName; setter는 DevicesAlias만 변경 |
| 엔티티 이동(단/다중) | `MoveEntities` → Composite | 동일 위치 항목은 명령 미생성, 각 이동이 하나의 Composite에 묶임 |
| Work 속성(Duration) | `UpdateWorkDuration` Single | |
| Call 속성(Timeout) | `UpdateCallTimeout` Single | |
| ApiCall 태그/ValueSpec | `UpdateApiCallInTag` / `UpdateApiCallOutTag` / `UpdateApiCallValueSpec` Single | |
| 화살표 재연결 | `ReconnectArrow` Single | source 또는 target 교체 |
| CallCondition IsOR/IsRising 변경 | `UpdateCallConditionSettings` Single | 변경 없으면 명령 미생성(false 반환) |
| 조건 ApiCall 기대값 변경 | `UpdateConditionApiCallOutputSpec` Single | ValueSpec 동일하면 명령 미생성(false 반환) |

### 2.5 Call Conditions

Call 노드에는 **ActiveTrigger / AutoCondition / CommonCondition** 세 종류의 조건을 붙일 수 있습니다.

#### 조건 구조

```
Call
 └── CallConditions[]
       └── CallCondition
             ├── Id       (Guid — 조건 식별)
             ├── Type     (Active=0 / Auto=1 / Common=2)
             ├── IsOR     (bool — AND/OR 결합 방식)
             ├── IsRising (bool — 상승 엣지 트리거)
             └── Conditions[]   (ApiCall 목록 — 조건 기대값)
```

#### 조건 ApiCall 저장 방식

조건 ApiCall은 **`store.ApiCalls`에 등록하지 않습니다.**

```
원본 ApiCall (store.ApiCalls 내 존재)
  │
  ▼  buildAddApiCallToConditionCmd
     1. DsQuery.getApiCall(sourceApiCallId, store) — store 전역 조회
     2. src.DeepCopy() — Id 동일 유지, 독립 객체
     3. copy.OutputSpec <- 사용자 기대값 (ValueSpec)
     4. AddApiCallToCondition 명령 → CallCondition.Conditions에 추가
```

- 원본 ApiCall 삭제 후에도 조건 ApiCall은 `(unlinked)` fallback으로 패널에 표시됨 (Id 참조 없음)
- 조건 ApiCall은 해당 `CallCondition.Conditions`에만 존재 — 다른 Call의 `ApiCalls`와 공유하지 않음

#### 패널 표시 흐름

```
Call 선택
  → GetCallConditionsForPanel(callId)
  → CallConditionPanelItem list (ConditionId, Type, IsOR, IsRising, Items[])
  → C# RefreshCallPanel:
       ActiveTriggers / AutoConditions / CommonConditions ObservableCollection 갱신
```

#### CRUD API 요약

| 동작 | EditorApi 메서드 | 반환 false 조건 |
|------|----------------|----------------|
| 조건 추가 | `AddCallCondition(callId, type)` | Call 미존재 |
| 조건 삭제 | `RemoveCallCondition(callId, conditionId)` | Call 또는 조건 미존재 |
| IsOR/IsRising 변경 | `UpdateCallConditionSettings(callId, conditionId, isOR, isRising)` | 미존재 또는 값 동일 |
| 조건 ApiCall 추가 | `AddApiCallToCondition(callId, conditionId, sourceApiCallId, outputSpecText)` | 미존재 또는 파싱 실패 |
| 조건 ApiCall 삭제 | `RemoveApiCallFromCondition(callId, conditionId, apiCallId)` | 미존재 |
| 조건 ApiCall 기대값 변경 | `UpdateConditionApiCallOutputSpec(callId, conditionId, apiCallId, newSpecText)` | 미존재 또는 값 동일 |

모든 조작은 Undo 1회로 원상복귀됩니다.

### 2.4 Delete

삭제는 항상 **말단 → 부모** 순서의 `Composite`로 조립됩니다. Undo는 `List.rev`로 자동 역순 복원.

**진입점**: `RemoveEntities(IEnumerable<string * Guid>)` 하나로 단일·다중 삭제를 모두 처리합니다 (단일 전용 wrapper 없음).

단일 삭제도 관련 화살표가 있으면 Composite로 조립합니다.

**캐스케이드 범위**:

| 삭제 대상 | 포함 항목 |
|-----------|-----------|
| `Call` | 관련 ArrowBetweenCalls → Call |
| `Work` | ArrowBetweenCalls → Calls → ArrowBetweenWorks → Work |
| `Flow` | (Work 범위 전체) → Flow |
| `System` | (Flow 범위 전체) + ApiDef + HwButton + HwLamp + HwCondition + HwAction → System |
| `Project` | (System 범위 전체) → Project |

```
RemoveProject
 └── RemoveSystem ×N
       ├── RemoveFlow ×N
       │     └── RemoveWork ×N
       │           ├── [ArrowBetweenCalls 수집] → 삭제
       │           └── RemoveCall ×N
       │
       ├── [ArrowBetweenWorks 수집] → 삭제
       ├── RemoveApiDef ×N
       └── RemoveHw (Button/Lamp/Condition/Action) ×N
```

**Composite 실행 순서** (말단 → 부모):
```
ArrowBetweenCalls → RemoveCall → ArrowBetweenWorks → RemoveWork
→ RemoveFlow → ApiDef/HW → RemoveSystem → RemoveProject

Undo 복원 순서: 위 역순 자동 (List.rev)
```

**화살표 수집 규칙**:
- `ArrowBetweenWorks`: 삭제 대상 Work ID 집합에 source 또는 target이 포함된 것 전부 수집
- `ArrowBetweenCalls`: 삭제 대상 Call ID 집합에 source 또는 target이 포함된 것 전부 수집

**backup**: 모든 Remove 명령은 `DeepCopyHelper.backupEntityAs<'T>`(원본 GUID 유지 JSON 클론)로 Undo용 백업을 생성합니다. 제네릭 형태로 서브타입 필드를 완전히 보존합니다.

---

## 3. Undo / Redo

### 3.1 스택 구조

```
                  UndoRedoManager  (LinkedList 기반, maxSize O(1) trim)
┌────────────────────────────────────────────────┐
│  undoList  (LinkedList<EditorCommand>)         │
│  ┌──────────────────────────────────────┐      │
│  │  cmd₃  ← First (가장 최근)            │      │
│  │  cmd₂                                │      │
│  │  cmd₁                                │      │
│  └──────────────────────────────────────┘      │
│                                                │
│  redoList  (새 Exec 시 Clear)                  │
│  ┌──────────────────────────────────────┐      │
│  │  cmd₃  ← Undo 후 이동                 │      │
│  └──────────────────────────────────────┘      │
└────────────────────────────────────────────────┘
```

### 3.2 스택 동작

```
Exec(cmd)
  → CommandExecutor.execute(cmd, store)
  → ensureValidStoreOrThrow
       실패 시: undo(cmd, store) → StoreRefreshed + HistoryChanged → 예외
  → undoList.AddFirst(cmd)
  → redoList.Clear()
  → EditorEvent 발행 (HistoryChanged 포함)

Undo()
  → undoList.RemoveFirst() → cmd
  → CommandExecutor.undo(cmd, store)
  → redoList.AddFirst(cmd)
  → EditorEvent 발행 (HistoryChanged 포함)

Redo()
  → redoList.RemoveFirst() → cmd
  → CommandExecutor.execute(cmd, store)
  → undoList.AddFirst(cmd)
  → EditorEvent 발행 (HistoryChanged 포함)

UndoTo(n) / RedoTo(n)
  → n=0 : no-op
  → n=1 : Undo()/Redo() 1회 (이벤트 정상 발행)
  → n>1 : suppressEvents=true → Undo()/Redo() × n → finally: StoreRefreshed + HistoryChanged 1회
```

### 3.3 History 패널

```
HistoryChanged(undoLabels: string list, redoLabels: string list)
  → C#: RebuildHistoryItems 호출
       HistoryItems[0]       = "(초기 상태)"
       HistoryItems[1..N]    = undoLabels (역순 — 오래된 것이 위)
       HistoryItems[N+1..]   = redoLabels (회색 + 취소선)
       CurrentHistoryIndex   = undoList.Count (현재 상태 위치)

더블클릭 점프:
  HistoryListBox_MouseDoubleClick → JumpToHistoryCommand(item)
    delta = clickedIdx - CurrentHistoryIndex
    delta < 0 → UndoTo(-delta)
    delta > 0 → RedoTo(delta)
```

### 3.4 Composite와 의미 단위

복잡한 다중 동작은 `Composite(label, [cmd₁; cmd₂; ...; cmdₙ])` 1건으로 기록합니다.

- Composite execute: 서브 명령을 순서대로 실행, 이벤트는 완료 후 `StoreRefreshed` 1회만 발행
- Composite undo: 서브 명령을 `List.rev` 역순으로 복원

**의미 단위 보장 예시**:

| 사용자 동작 | 명령 구성 | Undo 1회 결과 |
|------------|----------|--------------|
| 다중 선택 화살표 연결 | `Composite([AddArrow₁; AddArrow₂; AddArrow₃])` | 3개 화살표 동시 제거 |
| Work 붙여넣기 (Call 포함) | `Composite([AddWork; AddCall₁; AddCall₂])` | Work + Call 전체 동시 제거 |
| System 삭제 (Flow/Work/Call 포함) | `Composite([Arrow...; Call...; Work...; Flow...; ApiDef...; System])` | 전체 복원 |

---

## 4. 복사/붙여넣기

### 4.1 개요

- 복사 가능 타입: `Flow`, `Work`, `Call` (판정: `PasteResolvers.isCopyableEntityType`)
- 단일/다중 선택 모두 `EditorApi.PasteEntities`로 진입
- 다중 붙여넣기는 `Composite` 1건으로 기록 → Undo 1회 처리

### 4.2 붙여넣기 대상 해석

| 복사 타입 | 대상 해석 함수 | 기본 대상 (미해석 시) |
|----------|--------------|----------------------|
| `Flow` | `resolveSystemTarget` | 원본 Flow의 ParentId (System) |
| `Work` | `resolveFlowTarget` | 원본 Work의 ParentId (Flow) |
| `Call` | `resolveWorkTarget` | 원본 Call의 ParentId (Work) |

### 4.3 ApiCall 복사 정책 (`CallCopyContext`)

Call을 붙여넣을 때 내부의 ApiCall을 어떻게 처리할지는 대상 위치에 따라 분기합니다.

```
pasteCallsToWorkBatch(sourceCalls, targetWorkId)
          │
          ▼
   detectCopyContext(sourceCall, targetWorkId, store)
          │
          ├─ sourceCall.ParentId == targetWorkId ?
          │         YES ──► SameWork
          │                  └─ AddSharedApiCallToCall
          │                       (store.ApiCalls 불변, Call.ApiCalls만 추가)
          │
          ├─ 같은 Flow 내 다른 Work ?
          │         YES ──► DifferentWork
          │                  └─ AddApiCallToCall (새 ApiCall 객체, 새 GUID)
          │
          └─ 다른 Flow ?
                    YES ──► DifferentFlow
                             ├─ copyApiCalls() — ApiCall 새 GUID
                             └─ Device System `{targetFlowName}_{devAlias}`
                                  존재 시 재사용(ApiDef 이름 매칭), 없으면 신규 + ApiDef 복제
```

| 컨텍스트 | 조건 | Call GUID | ApiCall GUID | 비고 |
|---------|------|-----------|-------------|------|
| `SameWork` | 소스·대상 Work ID 동일 | 새 GUID | 원본 그대로(공유) | `AddSharedApiCallToCall` 사용 |
| `DifferentWork` | 같은 Flow 내 다른 Work | 새 GUID | 새 GUID | `AddApiCallToCall` 사용 |
| `DifferentFlow` | 다른 Flow | 새 GUID | 새 GUID | Passive System `{targetFlow}_{devAlias}` 복제/재사용, ApiDefId 매핑 |

### 4.4 ApiCall 공유 참조 불변 조건

- `AddSharedApiCallToCall` execute: `Call.ApiCalls`에만 추가, `store.ApiCalls` 변경 없음
- `AddSharedApiCallToCall` undo: `Call.ApiCalls`에서 제거, `store.ApiCalls` 변경 없음
- `Mutation.removeCall`: 다른 Call에서 아직 참조 중인 ApiCall은 `store.ApiCalls`에서 삭제하지 않음 (레퍼런스 카운팅)

---

## 5. 화살표 (연결선)

### 5.1 연결 규칙

- Work-Work 연결: System 캔버스 또는 Flow 캔버스에서만 허용
- Call-Call 연결: Work 캔버스에서만 허용
- 자기 자신에게 연결 불가, 이미 존재하는 연결 중복 불가

### 5.2 ArrowType

| 타입 | 의미 | 화살표 모양 |
|------|------|------------|
| `Start` | A 완료 → B 시작 | 실선 + V자 화살촉 (초록) |
| `Reset` | A 시작 → B 리셋 | 점선 + V자 화살촉 (주황) |
| `StartReset` | A 완료 → B 시작 + A 리셋 | 실선 + V자 화살촉 + 시작점 사각형 (빨강) |
| `ResetReset` | A·B 상호 리셋 | 점선 + 양방향 V자 화살촉 (주황) |
| `Group` | 그룹 관계 | 화살촉 없는 직선 |

### 5.3 경로 계산

화살표 경로는 `ArrowPathCalculator`에서 직교 꺾임 polyline으로 계산합니다.

- 입력: source 노드 Xywh, target 노드 Xywh
- 출력: `ArrowVisual.Points` (꺾임점 좌표 목록)
- C#의 `ArrowNode.UpdateFromVisual`에서 WPF `StreamGeometry`로 변환

---

## 6. JSON 직렬화

### 6.1 저장

- 저장 단위: **`DsStore` 전체**
- 진입점: `EditorApi.SaveToFile(path)` → `JsonConverter.serialize(store)`
- 모든 Dictionary는 JSON 배열(key-value pair)로 직렬화

### 6.2 직렬화 필드 (`DsStore` 기준)

| 컬렉션 | 포함 내용 |
|--------|----------|
| `Projects` | Project (ActiveSystemIds, PassiveSystemIds 포함) |
| `Systems` | DsSystem |
| `Flows` | Flow |
| `Works` | Work + WorkProperties |
| `Calls` | Call + CallProperties (`devicesAlias`, `apiName` 필드로 저장, `name`은 computed이므로 제외) |
| `ApiDefs` | ApiDef + ApiDefProperties |
| `ApiCalls` | ApiCall (InTag, OutTag, ApiDefId, OutputSpec, InputSpec) |
| `ArrowWorks` | ArrowBetweenWorks |
| `ArrowCalls` | ArrowBetweenCalls |
| `HwButtons` | HwButton |
| `HwLamps` | HwLamp |
| `HwConditions` | HwCondition |
| `HwActions` | HwAction |

- `Project.ActiveSystemIds / PassiveSystemIds`: `ResizeArray<Guid>` (DsSystem 객체 아닌 ID만 저장)
- `ApiCall.ApiDefId`: `Guid option` (ApiDef 객체 아닌 ID만 참조)

### 6.3 JSON 옵션 (`Ds2.Core/JsonOptions.fs`)

두 직렬화 목적의 설정을 단일 파일에서 중앙 관리합니다. `JsonConverter`와 `DeepCopyHelper`가 각각 위임하여 설정 드리프트를 방지합니다.

```
JsonOptions 모듈
  ├── JsonOptionsProfile (private DU)
  │     ├── ProjectSerialization  — 프로젝트 저장/로드용
  │     └── DeepCopy              — 엔티티 복제/Undo 백업용
  │
  ├── createProjectSerializationOptions() ← JsonConverter.defaultOptions 위임
  └── createDeepCopyOptions()             ← DeepCopyHelper.jsonOptions 위임
```

| 프로필 | 사용처 | WriteIndented | NamingPolicy | IgnoreNull | IncludeFields |
|--------|--------|:---:|:---:|:---:|:---:|
| `ProjectSerialization` | `JsonConverter.serialize / deserialize` | ✓ | CamelCase | ✓ | ✓ |
| `DeepCopy` | `DeepCopyHelper.backupEntityAs / jsonCloneEntity` | — | — | — | — |

공통: `System.Text.Json` + `FSharp.SystemTextJson`, `JsonFSharpConverter(WithIncludeRecordProperties(true))`

**AASX에서의 재사용**: `AasxExporter.mkJsonProp<T>` / `AasxImporter.fromJsonProp<T>`에서 `JsonConverter.serialize/deserialize`를 직접 호출 — JSON 옵션 중복 없음.

### 6.4 불러오기

```
EditorApi.LoadFromFile(path)
  → cloneStore store  ← 현재 전체 상태 백업 (실패 시 복원용)
  → JsonConverter.deserialize(json) → 새 DsStore
  → ensureValidStoreOrThrow loaded  ← 로드 데이터 사전 검증
       실패 시: StoreCopy.replaceAllCollections backup store → 예외
  → StoreCopy.replaceAllCollections loaded store  ← 각 컬렉션 교체 (기존 참조 유지)
  → ensureValidStoreOrThrow store  ← 적용 후 재검증
  → undoManager.Clear()  ← Undo/Redo 스택 초기화
  → StoreRefreshed 이벤트 발행  ← UI 전체 재구성
  → HistoryChanged([], []) 발행  ← History 패널 초기화
```

파일 로드 후 Undo 스택은 초기화되므로 로드 직후 Undo 동작은 불가합니다.

### 6.5 Store 교체 API 비교

`LoadFromFile`과 `ReplaceStore`는 내부 헬퍼 `ApplyNewStore(contextLabel)`를 공유합니다:

```
1. backup = cloneStore store          ← 실패 시 복원용 스냅샷
2. ensureValidStoreOrThrow newStore   ← 입력 데이터 검증
3. StoreCopy.replaceAllCollections    ← 기존 참조 유지하며 컬렉션 교체
4. undoManager.Clear()                ← Undo/Redo 스택 초기화
5. StoreRefreshed 발행                ← UI 전체 재구성
6. HistoryChanged([], []) 발행        ← History 패널 초기화
   실패 시: backup으로 복원 후 예외
```

| API | 입력 소스 | 주요 차이점 |
|-----|---------|-----------|
| `LoadFromFile(path)` | JSON 파일 | `JsonConverter.deserialize<DsStore>` |
| `ReplaceStore(newStore)` | 외부 구성된 DsStore | AASX 임포트 등 파일 I/O 경로에서 사용 |

### 6.6 backupEntity vs jsonCloneEntity

| 함수 | GUID | 사용 위치 | 용도 |
|------|------|----------|------|
| `DeepCopyHelper.backupEntityAs<'T>` | **원본 유지** | 모든 Remove 명령 | Undo 복원용 백업 (서브타입 필드 보존) |
| `DeepCopyHelper.jsonCloneEntity` | **새 GUID 생성** | PasteOps | 복사/붙여넣기용 독립 사본 |

이 두 함수를 혼동하면 Undo 시 GUID가 바뀌거나 붙여넣기 결과가 원본과 ID가 겹치는 문제가 발생합니다.
`backupEntityAs<'T>`는 제네릭이라 `typeof<'T>`로 실제 서브타입의 모든 필드를 직렬화·복원합니다.

---

## 7. 선택 동기화

- 캔버스 선택 ↔ 트리 선택은 단일 경로(`MainViewModel.Selection.cs`)에서 처리
- Ctrl 다중 선택, Shift 범위 선택, 박스(드래그) 선택 지원
- 선택 정렬·범위 판정은 F# `SelectionQueries`에서 처리, C#에서 중복 구현하지 않음
- 선택 변경 시 `SelectionChanged` 이벤트 → 속성 패널 갱신

---

## 8. AASX 임포트/익스포트

### 8.1 개요

`Ds2.Aasx` 프로젝트가 IEC 62714 / AAS 3.0 AASX 파일과 `DsStore` 간 양방향 변환을 담당합니다.
`Ds2.UI.Core`는 `Ds2.Aasx`를 참조하지 않습니다. `Ds2.UI.Frontend`(C#)가 양쪽을 직접 참조합니다.

### 8.2 익스포트 흐름

```
MainViewModel.ExportAasxCommand (C#)
  → _store.Projects.Values.FirstOrDefault()  ← 프로젝트 취득
  → AasxExporter.exportToAasxFile(_store, project, path)  ← F# 직접 호출
      → exportToSubmodel: Project 계층 → AAS Submodel
            Call → SMC
            Work → SMC (Calls SML + ArrowsBetweenCalls SML)
            Flow → SMC (Works SML + ArrowsBetweenWorks SML)
            DsSystem → SMC (Flows SML + ApiDefs SML, IsActiveSystem 플래그)
            Project → 최상위 SMC
      → writeEnvironment env path  ← AASX ZIP 생성 (XML 직렬화)
```

**직렬화 전략**:
- 단순 필드 (Name, Guid, DevicesAlias, ArrowType 등): AAS `Property` (string)
- 복잡한 타입 (ApiCall list, CallCondition list, *Properties, Xywh option): `JsonConverter.serialize<T>` → JSON 문자열 → AAS `Property`

### 8.3 임포트 흐름

```
MainViewModel.ImportAasxCommand (C#)
  → AasxImporter.importFromAasxFile(path)  ← F# 직접 호출 → DsStore option
      → readEnvironment path  ← AASX ZIP 열기 (XML/JSON 자동 판별)
      → Submodel → Project + 모든 엔티티 재구성
            SMC → Call (ApiCalls/CallConditions JSON 역직렬화)
            SMC → Work → Calls + ArrowBetweenCalls
            SMC → Flow → Works + ArrowBetweenWorks
            SMC → DsSystem (isActive 플래그로 ActiveSystemIds/PassiveSystemIds 분류)
            Submodel → Project + DsStore
  → FSharpOption<DsStore>.get_IsSome(storeOpt) 검사
  → _editor.ReplaceStore(storeOpt.Value)  ← EditorApi: store 교체 + StoreRefreshed 발행
  → _currentFilePath = null, IsDirty = false, UpdateTitle()
```

### 8.4 AASX 파일 구조 (ds2 전용)

```
Environment
└── AssetAdministrationShell (id="urn:ds2:shell:{projectId}")
└── Submodel (idShort="Ds2SequenceControlSubmodel")
    └── SMC (Project)
        ├── Property "Name", "Guid", "Properties" (JSON)
        ├── SML "ActiveSystems"
        │   └── SMC (DsSystem, IsActiveSystem="true")
        │       ├── SML "Flows"
        │       │   └── SMC (Flow)
        │       │       ├── SML "Works"
        │       │       │   └── SMC (Work)
        │       │       │       ├── SML "Calls"
        │       │       │       │   └── SMC (Call) — ApiCalls/CallConditions as JSON
        │       │       │       └── SML "ArrowsBetweenCalls"
        │       │       └── SML "ArrowsBetweenWorks"
        │       └── SML "ApiDefs"
        └── SML "PassiveSystems"
            └── SMC (DsSystem, IsActiveSystem="false")
```

### 8.5 ReplaceStore

```
EditorApi.ReplaceStore(newStore: DsStore)  — LoadFromFile 패턴 동일
  → cloneStore store          ← 현재 상태 백업
  → ensureValidStoreOrThrow newStore
  → StoreCopy.replaceAllCollections newStore store
  → ensureValidStoreOrThrow store
  → undoManager.Clear()       ← Undo/Redo 스택 초기화
  → StoreRefreshed 이벤트 발행
  → HistoryChanged([], []) 발행  ← History 패널 초기화
  실패 시: StoreCopy.replaceAllCollections backup store → 예외
```

임포트 후 Undo 스택은 초기화되므로 임포트 직후 Undo 동작은 불가합니다.

---

## 9. 로깅 (log4net)

### 9.1 초기화 흐름

```
앱 시작
  → App.xaml.cs OnStartup
      → XmlConfigurator.Configure(new FileInfo("log4net.config"))
           log4net.config 있음  → log4net 초기화
           log4net.config 없음  → System.Diagnostics.Trace.TraceWarning("log4net.config 파일을 찾을 수 없습니다...")
                                   (log4net 미초기화 상태에서도 VS 출력 창에 표시)
      → Log.Info("=== Ds2.Promaker 시작 ===")
      → DispatcherUnhandledException += (_, ex) =>
              Log.Fatal("처리되지 않은 예외", ex.Exception); ex.Handled = true
앱 종료
  → App.xaml.cs OnExit
      → Log.Info("=== Ds2.Promaker 종료 ===")
```

### 9.2 로그 파일

```
<실행 파일 위치>/logs/ds2_yyyyMMdd.log
```

- **Appender**: `RollingFileAppender` (Composite 롤링, 최대 10MB × 10개 백업)
- **패턴**: `%date{yyyy-MM-dd HH:mm:ss.fff} [%-5level] %logger{1} — %message%newline%exception`
- **Visual Studio**: `DebugAppender`로 출력 창에도 동시 출력 (패턴 단축)

### 9.3 레이어별 로거와 레벨

#### F# — EditorApi.fs

| 지점 | 레벨 | 메시지 형태 |
|------|------|------------|
| `ExecuteCommand` 성공 | INFO | `Executed: {CommandLabel.ofCommand cmd}` |
| `ExecuteCommand` 실패 | ERROR | `Command failed: {label} — {ex.Message}` + 예외 |
| `RunUndoRedoStep` (Undo/Redo) 성공 | DEBUG | `Undo: {label}` / `Redo: {label}` |
| `RunUndoRedoStep` (Undo/Redo) 실패 | ERROR | `Undo failed: {label} — {ex.Message}` + 예외 |
| `ApplyNewStore` 성공 | INFO | `Store applied: {contextLabel}` |
| `ApplyNewStore` 실패 | ERROR | `ApplyNewStore failed: {contextLabel} — {ex.Message}` + 예외 |
| `SaveToFile` 성공 | INFO | `저장 완료: {path}` |
| `SaveToFile` 실패 | ERROR | `저장 실패: {path} — {ex.Message}` + 예외 (재throw) |

로거 선언:
```fsharp
// EditorApi 클래스 내부
let log = LogManager.GetLogger(typedefof<EditorApi>)
```

#### F# — AasxFileIO.fs

| 지점 | 레벨 | 메시지 형태 |
|------|------|------------|
| `readEnvironment` 예외 (AASX ZIP 읽기 실패) | WARN | `"AASX 읽기 실패"` + 예외 객체 (stack trace 포함) |

기존의 `with _ -> None` 패턴을 `with ex -> log.Warn("AASX 읽기 실패", ex); None`으로 개선하여 silent failure 제거.
예외 객체를 두 번째 인자로 전달하므로 log4net이 stack trace를 자동 포함합니다.

로거 선언:
```fsharp
// 모듈 레벨
let private log = LogManager.GetLogger("Ds2.Aasx.AasxFileIO")
```

#### F# — AasxImporter.fs

| 지점 | 레벨 | 메시지 형태 |
|------|------|------------|
| `fromJsonProp` JSON 역직렬화 실패 | WARN | `JSON 역직렬화 실패: {idShort} — {ex.Message}` + 예외 |
| `smcToArrowCall` 파싱 실패 | WARN | `smcToArrowCall 실패: {ex.Message}` + 예외 |
| `smcToArrowWork` 파싱 실패 | WARN | `smcToArrowWork 실패: {ex.Message}` + 예외 |
| `smcToCall` 파싱 실패 | WARN | `smcToCall 실패: {ex.Message}` + 예외 |
| `smcToWork` 파싱 실패 | WARN | `smcToWork 실패: {ex.Message}` + 예외 |
| `smcToFlow` 파싱 실패 | WARN | `smcToFlow 실패: {ex.Message}` + 예외 |
| `smcToApiDef` 파싱 실패 | WARN | `smcToApiDef 실패: {ex.Message}` + 예외 |
| `smcToSystem` 파싱 실패 | WARN | `smcToSystem 실패: {ex.Message}` + 예외 |
| `submodelToProjectStore` 실패 | WARN | `submodelToProjectStore 실패: {ex.Message}` + 예외 |

기존의 `with _ -> None` 패턴 9곳을 모두 `with ex -> log.Warn(..., ex); None`으로 변경.
각 변환 계층에서 어느 단계가 실패했는지 정확히 파악 가능합니다.

진입점 `importFromAasxFile`의 silent None 분기도 WARN으로 노출됩니다:

| 지점 | 레벨 | 메시지 형태 |
|------|------|------------|
| `env.Submodels = null` | WARN | `AASX 파싱 실패: Submodels null ({path})` |
| `Seq.tryPick` 결과 None (IdShort 불일치) | WARN | `AASX 파싱 실패: '{SubmodelIdShort}' Submodel을 찾을 수 없습니다 ({path})` |

로거 선언:
```fsharp
// 모듈 레벨
let private log = LogManager.GetLogger("Ds2.Aasx.AasxImporter")
```

#### C# — MainViewModel (partial class 공유)

`MainViewModel.cs`에 `private static readonly ILog Log` 선언.
모든 partial 파일(`Events.cs`, `FileIO.cs`, `NodeCommands.cs` 등)은 같은 partial class이므로 별도 선언 없이 `Log` 공유.

| 지점 | 레벨 | 메시지 형태 |
|------|------|------------|
| EditorEvent 구독자 에러 | ERROR | `EditorEvent 구독자 에러` + 예외 |
| JSON 파일 열기 성공 | INFO | `파일 열기 완료: {path}` |
| JSON 파일 열기 실패 | ERROR | `파일 열기 실패: {path}` + 예외 → DialogHelpers.Warn |
| JSON 파일 저장 성공 | INFO | `파일 저장 완료: {path}` |
| JSON 파일 저장 실패 | ERROR | `파일 저장 실패: {path}` + 예외 → DialogHelpers.Warn |
| AASX import 성공 | INFO | `AASX import 완료: {path}` |
| AASX import 빈 결과 | WARN | `AASX import 실패 (빈 결과): {path}` |
| AASX import ReplaceStore 실패 | ERROR | `AASX import 실패 (ReplaceStore): {path}` + 예외 → DialogHelpers.Warn |
| Unhandled EditorEvent | WARN | `Unhandled event: {evt.GetType().Name}` |
| AASX export 성공 | INFO | `AASX export 완료: {path}` |
| AASX export 프로젝트 없음 | WARN | `AASX export 실패: 프로젝트 없음 ({path})` |
| AASX export 예외 | ERROR | `AASX export 실패: {path}` + 예외 → DialogHelpers.Warn |

`ImportAasx()`의 `_editor.ReplaceStore(...)` 호출이 try/catch로 감싸져 있어, 임포트 후 Store 교체 단계에서 예외가 발생해도 파일 경로 맥락과 함께 ERROR 로그가 남습니다.

### 9.4 로깅 레벨 요약

| 레벨 | 의미 | 지점 |
|------|------|------|
| FATAL | 앱이 계속 실행되기 어려운 미처리 예외 | `DispatcherUnhandledException` |
| ERROR | 복구 시도했거나 사용자에게 알린 실패 | 파일 I/O 실패, ExecuteCommand 실패, Undo/Redo 실패 |
| WARN | 기능 저하지만 계속 실행 가능 | AASX import 빈 결과, AASX ZIP 읽기 실패 |
| INFO | 정상 수명주기 이벤트 | 앱 시작/종료, 파일 I/O 성공, ExecuteCommand 성공, Store 교체 |
| DEBUG | 반복 호출 상세 추적 | Undo/Redo 각 단계 |
