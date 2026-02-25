# RUNTIME.md

Last Sync: 2026-02-25

이 문서는 **CRUD · Undo/Redo · JSON 직렬화 · 복사붙여넣기 · 캐스케이드 삭제** 의 런타임 동작을 상세히 설명합니다.

---

## 1. 편집 명령 실행 흐름

모든 편집 동작은 아래 경로를 따릅니다.

```
[사용자 입력] — 키보드 / 마우스 / 메뉴
      │
      ▼  C# — EditorCanvas.Input.cs / MainViewModel.cs
      │  입력 해석 후 EditorApi.Xxx(...) 호출
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
      │  → 실패 시: CommandExecutor.undo → StoreRefreshed + UndoRedoChanged 발행 → 예외
      │
      ▼  F# — UndoRedoManager
      │  undoList.AddFirst / redoList.Clear
      │
      ▼  F# — EditorApi: EditorEvent 발행
      │  StoreRefreshed       → UI 전체 재구성
      │  UndoRedoChanged      → Undo/Redo 버튼 상태 갱신
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
| `AddCall` | `AddCall` Single | `devicesAlias.apiName` 형식 |
| `AddCallsWithDevice` | Composite | Passive System·ApiDef 자동 생성 + Call 일괄 추가를 하나의 Undo 단위로 기록 |
| `AddArrowBetweenWorks` | `AddArrowBetweenWorks` Single | |
| `AddArrowBetweenCalls` | `AddArrowBetweenCalls` Single | |
| `AddApiDef` | `AddApiDef` Single | |
| `AddApiCall` | `AddApiCall` Single | |
| `AddButton / AddLamp / AddHwCondition / AddHwAction` | Single | |

**일괄 연결** (`ConnectSelectionInOrder`): 선택 순서에 따라 Work-Work 또는 Call-Call 화살표를 N개 생성하고 `Composite` 1건으로 기록 → Undo 1회로 전체 제거.

### 2.2 Read (Projection)

Store를 직접 읽지 않고 Projection을 통해 뷰 데이터를 얻습니다.

| Projection | 입력 | 출력 |
|------------|------|------|
| `TreeProjection.buildTree` | `DsStore` + rootId | `TreeNodeInfo list` |
| `TreeProjection.buildTrees` | `DsStore` | Control 트리 + Device 트리 |
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
| Work 위치 이동 | `MoveWork` Single | 동일 위치면 명령 미생성 |
| 다중 엔티티 이동 | `MoveEntities` → Composite | 각 이동이 하나의 Composite에 묶임 |
| Work 속성(Duration) | `UpdateWorkDuration` Single | |
| Call 속성(Timeout) | `UpdateCallTimeout` Single | |
| ApiCall 태그/ValueSpec | `UpdateApiCallInTag` / `UpdateApiCallOutTag` / `UpdateApiCallValueSpec` Single | |
| 화살표 재연결 | `ReconnectArrow` Single | source 또는 target 교체 |

### 2.4 Delete

삭제는 항상 **말단 → 부모** 순서의 `Composite`로 조립됩니다. Undo는 `List.rev`로 자동 역순 복원.

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
       실패 시: undo(cmd, store) → StoreRefreshed + UndoRedoChanged → 예외
  → undoList.AddFirst(cmd)
  → redoList.Clear()
  → EditorEvent 발행

Undo()
  → undoList.RemoveFirst() → cmd
  → CommandExecutor.undo(cmd, store)
  → redoList.AddFirst(cmd)
  → EditorEvent 발행

Redo()
  → redoList.RemoveFirst() → cmd
  → CommandExecutor.execute(cmd, store)
  → undoList.AddFirst(cmd)
  → EditorEvent 발행
```

### 3.3 Composite와 의미 단위

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
- 단일/다중 선택 모두 `EditorApi.PasteEntity` / `EditorApi.PasteEntities`로 진입
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
                             └─ AddApiCallToCall (현재는 DifferentWork와 동일)
```

| 컨텍스트 | 조건 | Call GUID | ApiCall GUID | 비고 |
|---------|------|-----------|-------------|------|
| `SameWork` | 소스·대상 Work ID 동일 | 새 GUID | 원본 그대로(공유) | `AddSharedApiCallToCall` 사용 |
| `DifferentWork` | 같은 Flow 내 다른 Work | 새 GUID | 새 GUID | `AddApiCallToCall` 사용 |
| `DifferentFlow` | 다른 Flow | 새 GUID | 새 GUID | 현재는 DifferentWork와 동일 |

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

### 6.3 JSON 옵션

| 옵션 | 값 |
|------|---|
| 라이브러리 | `System.Text.Json` + `FSharp.SystemTextJson` |
| 들여쓰기 | `WriteIndented = true` |
| 키 네이밍 | `CamelCase` |
| 키 대소문자 | `PropertyNameCaseInsensitive = true` |
| null 필드 | `DefaultIgnoreCondition = WhenWritingNull` |
| F# DU/레코드 | `JsonFSharpConverter(WithIncludeRecordProperties(true))` |

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
  → UndoRedoChanged(false, false) 발행
```

파일 로드 후 Undo 스택은 초기화되므로 로드 직후 Undo 동작은 불가합니다.

### 6.5 backupEntity vs jsonCloneEntity

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