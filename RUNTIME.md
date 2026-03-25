# RUNTIME.md

Last Sync: 2026-03-25 (Device System AASX 분리 저장, Store/Editor 분리, Critical Path Duration, TokenSpec)

이 문서는 **CRUD · Undo/Redo · JSON 직렬화 · 복사붙여넣기 · 캐스케이드 삭제** 의 런타임 동작을 상세히 설명합니다.

---

## 1. 편집 명령 실행 흐름

모든 편집 동작은 아래 경로를 따릅니다.

```
[사용자 입력] — 키보드 / 마우스 / 메뉴
      │
      ▼  C# — EditorCanvas.Input.cs / MainViewModel.cs
      │  편집 입력 해석 후 store.Xxx(...) 호출 (DsStore Extension Method)
      │  (조회/투영은 store.BuildTrees() / store.CanvasContentForTab() 등)
      │
      ▼  F# — Ds2.Editor Extension Method
      │  store.WithTransaction(label, fun () ->
      │    Track 헬퍼(TrackAdd/Remove/Mutate/GuidSetAdd/GuidSetRemove)로 증분 변경)
      │  → 성공: UndoTransaction(Records list) → UndoRedoManager에 push
      │  → 실패: 기록된 UndoRecord를 역순 Undo() → 자동 롤백 + reraise
      │
      ▼  F# — UndoRedoManager (Ds2.Editor/Commands/)
      │  undoStack.AddFirst(UndoTransaction { Label; Records })
      │  redoStack.Clear()
      │
      ▼  F# — StoreEditorState: EditorEvent 발행
      │  StoreRefreshed       → UI 전체 재구성
      │  HistoryChanged       → History 패널 갱신 + Undo/Redo 버튼 상태 갱신
      │  SelectionChanged     → 속성 패널 갱신
      │
      ▼  C# — MainViewModel.HandleEvent(event)
         RebuildAll → WPF 바인딩 갱신 → 화면 반영
```

### 1.1 WithTransaction + Track 헬퍼 설계

**WithTransaction(label, action)** — 편집 메서드 내부의 트랜잭션 래퍼:

1. `currentRecords = Some(ResizeArray<UndoRecord>())` — 트랜잭션 진입
2. `action()` 실행 — 내부에서 Track 헬퍼가 Dictionary 조작 + UndoRecord 기록
3. 성공: `undoManager.Push({ Label = label; Records = records })` → Undo 스택에 기록
4. 실패: `Seq.rev records |> Seq.iter (fun r -> r.Undo())` → 자동 롤백 + `reraise()`
5. `currentRecords <- None` — 트랜잭션 종료

**Track 헬퍼 5종** — Dictionary 조작 + UndoRecord 기록을 원자적으로 묶는 internal 메서드:

| 헬퍼 | 동작 | Undo 클로저 |
|------|------|------------|
| `TrackAdd(dict, entity)` | `dict.[id] <- entity` | `dict.Remove(id)` |
| `TrackRemove(dict, id)` | `backupEntityAs` + `dict.Remove(id)` | `dict.[id] <- backup` |
| `TrackMutate(dict, id, mutate)` | `backupEntityAs` + `mutate entity` + 새 snapshot | `dict.[id] <- oldSnapshot` |
| `TrackGuidSetAdd(set, id)` | `set.Add(id)` | `set.Remove(id)` |
| `TrackGuidSetRemove(set, id)` | `set.Remove(id)` | `set.Add(id)` |

**핵심 불변조건**: Track 헬퍼는 WithTransaction 밖에서 호출 불가 (`RecordUndo outside transaction` 예외).

---

## 2. CRUD

### 2.1 Create

각 엔티티 Add는 Ds2.Editor의 DsStore Extension Method에서 `WithTransaction` + Track 헬퍼로 실행합니다.

| API | 트랜잭션 | 비고 |
|-----|---------|------|
| `AddProject` | Track 1건 | |
| `AddSystem` | Track 복수건 | isPassive 플래그로 Active/Passive 구분 + GuidSetAdd |
| `AddFlow` | Track 복수건 | |
| `AddWork` | Track 1건 | 캔버스 위치(Xywh) 포함 |
| `AddCallsWithDevice` | WithTransaction 1건 | Passive System·ApiDef 자동 생성 + Call 일괄 추가를 하나의 Undo 단위로 기록 |
| `AddCallWithLinkedApiDefs` | WithTransaction 1건 | 기존 ApiDef ID 목록 기반 Call 1개 + ApiCall N개 (Undo 1회 보장) |
| `AddCallWithMultipleDevicesResolved` | WithTransaction 1건 | ApiCall 복제 모드: Call 1개 + N개 Device System의 ApiDef로 ApiCall N개 생성 (Undo 1회 보장) |
| `ConnectSelectionInOrder` | WithTransaction 1건 | 선택 순서 기반 Work-Work 또는 Call-Call 화살표 N개 일괄 생성. 2-노드(단일 화살표) 포함 모두 이 진입점 사용. |
| `AddApiDef` | Track 1건 | |
| `AddApiCallFromPanel` | WithTransaction 1건 | |

#### AddCallWithMultipleDevicesResolved (ApiCall 복제 모드)

CallCreateDialog에서 "ApiCall 복제" 모드를 선택하면 하나의 Call에 여러 Device System의 ApiDef를 각각 가리키는 ApiCall N개를 일괄 생성합니다.

```
[C# — CallCreateDialog]
 CallCreateMode.ApiCallReplication 선택
  → 사용자: Call 이름 + 공통 ApiName + Device alias 목록 (체크박스)
        │
        ▼
[C# — MainViewModel]
 store.AddCallWithMultipleDevicesResolved(entityKind, entityId, workId, callDevicesAlias, apiName, deviceAliases)
        │
        ▼
[F# — Ds2.Editor/Store/Nodes/Device.fs]
 WithTransaction("Add Call (ApiCall 복제)", fun () ->
   DirectDeviceOps.addCallWithMultipleDevices store projectId workId callDevicesAlias apiName aliases)
  ├─ Call 1개 생성 (devicesAlias, apiName)
  ├─ 각 alias에 대해:
  │    Passive System `{alias}` 의 ApiDef 중 apiName 일치 → ApiCall 생성 → Call.ApiCalls에 추가
  └─ Undo 1회로 전체 롤백 가능
```

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

| 동작 | API (Ds2.Editor 확장 메서드) | 비고 |
|------|-----|------|
| Work 이름 변경 | `RenameEntity` | EntityNameAccess 기반 |
| Call DevicesAlias 변경 | `RenameEntity` | `Call.Name` = DevicesAlias + "." + ApiName; setter는 DevicesAlias만 변경 |
| 엔티티 이동(단/다중) | `MoveEntities` | 동일 위치 항목은 Track 미생성, 각 이동이 하나의 WithTransaction에 묶임 |
| Work 속성(PeriodMs) | `UpdateWorkPeriodMs` | |
| Call 속성(TimeoutMs) | `UpdateCallTimeoutMs` | |
| ApiCall 태그/ValueSpec | `UpdateApiCallFromPanel` | |
| 화살표 재연결 | `ReconnectArrow` | source 또는 target 교체 |
| CallCondition IsOR/IsRising 변경 | `UpdateCallConditionSettings` | 변경 없으면 no-op(false 반환) |
| 조건 ApiCall 기대값 변경 | `UpdateConditionApiCallOutputSpec` | ValueSpec 동일하면 no-op(false 반환) |
| Work Duration 일괄변경 | `UpdateWorkDurationsBatch` | (workId, ms) 목록 → 단일 Undo 트랜잭션 |
| ApiCall I/O 태그 일괄변경 | `UpdateApiCallIOTagsBatch` | (apiCallId, inAddr, inSym, outAddr, outSym) 목록 → 단일 Undo 트랜잭션 |

### 2.4 Delete

삭제는 항상 **말단 → 부모** 순서로 `WithTransaction` 안에서 `CascadeRemove` 함수를 호출합니다. Track 헬퍼가 개별 UndoRecord를 기록하고, Undo 시 역순 실행으로 복원.

**진입점**: `RemoveEntities(IEnumerable<string * Guid>)` 하나로 단일·다중 삭제를 모두 처리합니다 (단일 전용 wrapper 없음).

**캐스케이드 범위**:

| 삭제 대상 | 포함 항목 |
|-----------|-----------|
| `Call` | 관련 ArrowBetweenCalls → Call |
| `Work` | ArrowBetweenCalls → Calls → ArrowBetweenWorks → Work |
| `Flow` | (Work 범위 전체) → Flow |
| `System` | (Flow 범위 전체) + ApiDef + HwButton + HwLamp + HwCondition + HwAction → System |
| `Project` | (System 범위 전체) → Project |

```
cascadeRemoveProject
 └── cascadeRemoveSystem ×N
       ├── cascadeRemoveFlow ×N
       │     └── cascadeRemoveWork ×N
       │           ├── cascadeRemoveCall ×N
       │           │     └── TrackRemove(ArrowCalls) + TrackRemove(Calls)
       │           └── TrackRemove(ArrowWorks) + TrackRemove(Works)
       │
       ├── removeHwComponents (ApiDef/Button/Lamp/Condition/Action)
       └── removeSystem (Project GuidSet 정리 + TrackRemove(Systems))
```

**화살표 수집 규칙**:
- `arrowsFor` 헬퍼: 노드 ID 집합에 source 또는 target이 포함된 화살표를 제네릭으로 수집
- `trackRemoveEntities` 헬퍼: 엔티티 리스트를 제네릭으로 TrackRemove

**orphan ApiCall 정리**: `removeOrphanApiCalls`가 `batchRemoveEntities` 말미에 1회만 호출 — 어떤 Call에도 참조되지 않는 ApiCall 정리

**backup**: 모든 `TrackRemove`는 `DeepCopyHelper.backupEntityAs<'T>`(원본 GUID 유지 JSON 클론)로 자동 백업을 생성합니다. 제네릭 형태로 서브타입 필드를 완전히 보존합니다.

주요 구현: `Ds2.Editor/Store/Nodes/Remove.fs` (`CascadeRemove` 내부 모듈)

### 2.5 Call Conditions

Call 노드에는 **SkipUnmatch / AutoAux / ComAux** 세 종류의 조건을 붙일 수 있습니다.

#### 조건 구조

```
Call
 └── CallConditions[]
       └── CallCondition
             ├── Id       (Guid — 조건 식별)
             ├── Type     (AutoAux=0 / ComAux=1 / SkipUnmatch=2)
             ├── IsOR     (bool — AND/OR 결합 방식)
             ├── IsRising (bool — 상승 엣지 트리거)
             └── Conditions[]   (ApiCall 목록 — 조건 기대값)
```

#### 조건 ApiCall 저장 방식

조건 ApiCall은 **`store.ApiCalls`에 등록하지 않습니다.**

```
원본 ApiCall (store.ApiCalls 내 존재)
  │
  ▼  AddApiCallsToConditionBatch
     1. DsQuery.getApiCall(sourceApiCallId, store) — store 전역 조회 (각 ID별)
     2. src.DeepCopy() — Id 동일 유지, 독립 객체
     3. TrackMutate — CallCondition.Conditions에 일괄 추가
```

- 원본 ApiCall 삭제 후에도 조건 ApiCall은 `(unlinked)` fallback으로 패널에 표시됨 (Id 참조 없음)
- 조건 ApiCall은 해당 `CallCondition.Conditions`에만 존재 — 다른 Call의 `ApiCalls`와 공유하지 않음

#### 패널 표시 흐름

```
Call 선택
  → GetCallConditionsForPanel(callId)
  → CallConditionPanelItem list (ConditionId, Type, IsOR, IsRising, Items[])
  → C# RefreshCallPanel:
       SkipUnmatch / AutoAux / ComAux ObservableCollection 갱신
```

#### CRUD API 요약

| 동작 | Ds2.Editor Extension 메서드 | 반환 false 조건 |
|------|------------------------|----------------|
| 조건 추가 | `AddCallCondition(callId, type)` | Call 미존재 |
| 조건 삭제 | `RemoveCallCondition(callId, conditionId)` | Call 또는 조건 미존재 |
| IsOR/IsRising 변경 | `UpdateCallConditionSettings(callId, conditionId, isOR, isRising)` | 미존재 또는 값 동일 |
| 조건 ApiCall 추가 | `AddApiCallsToConditionBatch(callId, condId, sourceApiCallIds)` | 미존재 |
| 조건 ApiCall 삭제 | `RemoveApiCallFromCondition(callId, condId, apiCallId)` | 미존재 |
| 조건 ApiCall 기대값 변경 | `UpdateConditionApiCallOutputSpec(callId, conditionId, apiCallId, newSpecText)` | 미존재 또는 값 동일 |

모든 조작은 Undo 1회로 원상복귀됩니다.

---

## 3. Undo / Redo

### 3.1 스택 구조

```
                  UndoRedoManager(100)  (LinkedList 기반, maxSize O(1) trim)
┌────────────────────────────────────────────────┐
│  undoStack  (LinkedList<UndoTransaction>)      │
│  ┌──────────────────────────────────────┐      │
│  │  tx₃  ← First (가장 최근)             │      │
│  │  tx₂                                 │      │
│  │  tx₁                                 │      │
│  └──────────────────────────────────────┘      │
│                                                │
│  redoStack  (새 Push 시 Clear)                 │
│  ┌──────────────────────────────────────┐      │
│  │  tx₃  ← Undo 후 이동                 │      │
│  └──────────────────────────────────────┘      │
└────────────────────────────────────────────────┘

UndoTransaction = { Label: string; Records: UndoRecord list }
UndoRecord      = { Undo: unit -> unit; Redo: unit -> unit; Description: string }
```

### 3.2 스택 동작

```
WithTransaction(label, action) 성공
  → Track 헬퍼가 UndoRecord N개 기록
  → undoManager.Push({ Label = label; Records = recordsList })
  → redoStack.Clear()
  → EditorEvent 발행 (HistoryChanged 포함)

Undo()
  → undoManager.PopUndo() → tx
  → tx.Records |> List.rev |> List.iter (fun r -> r.Undo())
  → RewireApiCallReferences()
  → undoManager.PushRedo(tx)
  → EditorEvent 발행 (HistoryChanged 포함)

Redo()
  → undoManager.PopRedo() → tx
  → tx.Records |> List.iter (fun r -> r.Redo())
  → RewireApiCallReferences()
  → undoManager.PushUndo(tx)
  → EditorEvent 발행 (HistoryChanged 포함)

UndoTo(n) / RedoTo(n)
  → n=0 : no-op
  → n=1 : Undo()/Redo() 1회 (이벤트 정상 발행)
  → n>1 : suppressEvents=true → Undo()/Redo() × n → finally: StoreRefreshed + HistoryChanged 1회
```

### 3.3 RewireApiCallReferences

Undo/Redo가 deep copy된 별도 객체를 복원하므로 `Call.ApiCalls` 항목과 `store.ApiCalls` 항목이 다른 인스턴스가 됩니다. `RewireApiCallReferences()`가 매 Undo/Redo 후 자동 실행:

```
for call in store.Calls.Values:
  call.ApiCalls → store.ApiCalls[apiCall.Id]로 교체 (Id 기준 재연결)
  call.CallConditions[].Conditions → 동일 패턴 재연결
```

### 3.4 History 패널

```
HistoryChanged(undoLabels: string list, redoLabels: string list)
  → C#: RebuildHistoryItems 호출
       HistoryItems[0]       = "(초기 상태)"
       HistoryItems[1..N]    = undoLabels (역순 — 오래된 것이 위)
       HistoryItems[N+1..]   = redoLabels (회색 + 취소선)
       CurrentHistoryIndex   = undoStack.Count (현재 상태 위치)

더블클릭 점프:
  HistoryListBox_MouseDoubleClick → JumpToHistoryCommand(item)
    delta = clickedIdx - CurrentHistoryIndex
    delta < 0 → UndoTo(-delta)
    delta > 0 → RedoTo(delta)
```

### 3.5 WithTransaction과 의미 단위

복잡한 다중 동작은 `WithTransaction(label, fun () -> ...)` 1건으로 기록합니다.

- WithTransaction 내부에서 Track 헬퍼가 N개의 UndoRecord를 기록
- 이벤트는 완료 후 `StoreRefreshed` 1회만 발행
- Undo 시: `List.rev tx.Records |> List.iter (fun r -> r.Undo())` → 역순 복원

**의미 단위 보장 예시**:

| 사용자 동작 | Track 기록 | Undo 1회 결과 |
|------------|----------|--------------|
| 다중 선택 화살표 연결 | `TrackAdd(ArrowWorks/ArrowCalls) ×3` | 3개 화살표 동시 제거 |
| Work 붙여넣기 (Call 포함) | `TrackAdd(Work) + TrackAdd(Call) ×2` | Work + Call 전체 동시 제거 |
| System 삭제 (Flow/Work/Call 포함) | `TrackRemove(Arrow/Call/Work/Flow/ApiDef/System) ×N` | 전체 복원 |

---

## 4. 복사/붙여넣기

### 4.1 개요

- 복사 가능 타입: `Flow`, `Work`, `Call` (판정: `PasteResolvers.isCopyableEntityType`)
- 단일/다중 선택 모두 `store.PasteEntities`로 진입
- 다중 붙여넣기는 `WithTransaction` 1건으로 기록 → Undo 1회 처리

#### 복사 금지 규칙 (C# `CopySelected`)

- **혼합 타입 금지**: `Work + Call` 등 서로 다른 EntityType을 동시에 복사하면 경고 다이얼로그 표시 후 취소
- **다른 부모 금지**: 같은 타입이어도 서로 다른 부모(ParentId)에 속한 항목을 동시에 복사하면 경고 다이얼로그 표시 후 취소. ParentId는 `store.GetEntityParentId`로 store에서 직접 조회 — 트리 패널 다중 선택 시에도 올바르게 작동

#### 붙여넣기 대상 제한 (C# `PasteCopied`)

- **System 대상 Work/Call 금지**: System 노드를 target으로 선택한 상태에서 Work/Call 붙여넣기 시 "붙여넣기 대상으로 Flow를 선택하세요" 경고 다이얼로그 표시 후 취소
- **탭 전환 시 target 오염 방지**: `OnActiveTabChanged`에서 `_orderedNodeSelection.Clear()` + `SelectedNode = null` 실행 — 이전 탭 노드가 붙여넣기 대상으로 잘못 사용되는 버그 수정

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
          │                  └─ shareApiCalls()
          │                       (store.ApiCalls 불변, Call.ApiCalls만 TrackMutate)
          │
          ├─ 같은 Flow 내 다른 Work ?
          │         YES ──► DifferentWork
          │                  └─ copyApiCalls() (새 ApiCall 객체, 새 GUID → TrackAdd)
          │
          └─ 다른 Flow ?
                    YES ──► DifferentFlow
                             ├─ copyApiCalls() — ApiCall 새 GUID → TrackAdd
                             └─ Device System `{targetFlowName}_{devAlias}`
                                  존재 시 재사용(ApiDef 이름 매칭), 없으면 신규 + ApiDef 복제
```

| 컨텍스트 | 조건 | Call GUID | ApiCall GUID | 비고 |
|---------|------|-----------|-------------|------|
| `SameWork` | 소스·대상 Work ID 동일 | 새 GUID | 원본 그대로(공유) | Call.ApiCalls만 TrackMutate |
| `DifferentWork` | 같은 Flow 내 다른 Work | 새 GUID | 새 GUID | store.ApiCalls에 TrackAdd |
| `DifferentFlow` | 다른 Flow | 새 GUID | 새 GUID | Passive System `{targetFlow}_{devAlias}` 복제/재사용, ApiDefId 매핑 |

### 4.4 ApiCall 공유 참조 불변 조건

- `SameWork` 붙여넣기: `Call.ApiCalls`에만 추가(TrackMutate), `store.ApiCalls` 변경 없음
- `CascadeRemove.removeOrphanApiCalls`: 다른 Call에서 아직 참조 중인 ApiCall은 `store.ApiCalls`에서 삭제하지 않음 (레퍼런스 카운팅)

### 4.5 DifferentFlow paste Device System 생성 보장

`pasteFlowToSystem`에서 pastedFlow 추가 후 store에 pastedFlow가 아직 반영되지 않아 Device System이 생성되지 않는 버그를 수정했습니다.

```
수정:
  makeDeviceFlowCtxDirect store targetSystemId pastedFlow.Name
  → store 조회 없이 targetSystemId + targetFlowName으로 DeviceFlowCtx 직접 구성
  → Device System 정상 생성 + ApiDefId 매핑 보장
```

주요 구현: `Ds2.Editor/Store/Paste/` (`Paste.fs` + `Paste.DirectOps.fs` + `Paste.DeviceOps.fs`)

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

주요 구현: `Ds2.Editor/Geometry/ArrowPathCalculator.fs`, `EditorCanvas.Connect.cs`, `Ds2.Editor/Queries/ConnectionQueries.fs`

---

## 6. JSON 직렬화

### 6.1 저장

- 저장 단위: **`DsStore` 전체**
- 진입점: `store.SaveToFile(path)` → `JsonConverter.serialize(store)`
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
| `ProjectSerialization` | `JsonConverter.serialize / deserialize` | O | CamelCase | O | O |
| `DeepCopy` | `DeepCopyHelper.backupEntityAs / jsonCloneEntity` | — | — | — | — |

공통: `System.Text.Json` + `FSharp.SystemTextJson`, `JsonFSharpConverter(WithIncludeRecordProperties(true))`

**AASX에서의 재사용**: `AasxExporter.mkJsonProp<T>` / `AasxImporter.fromJsonProp<T>`에서 `JsonConverter.serialize/deserialize`를 직접 호출 — JSON 옵션 중복 없음.

### 6.4 불러오기

```
store.LoadFromFile(path)
  → backup = deepClone store         ← 현재 전체 상태 백업 (실패 시 복원용)
  → JsonConverter.deserialize(json)  → 새 DsStore
  → ensureValidStoreOrThrow loaded   ← 로드 데이터 사전 검증
       실패 시: ReplaceAllCollections backup → 예외
  → ReplaceAllCollections loaded     ← 13개 Dictionary 교체
  → undoManager.Clear()              ← Undo/Redo 스택 초기화
  → StoreRefreshed 이벤트 발행       ← UI 전체 재구성
  → HistoryChanged([], []) 발행      ← History 패널 초기화
```

파일 로드 후 Undo 스택은 초기화되므로 로드 직후 Undo 동작은 불가합니다.

### 6.5 Store 교체 API 비교

`LoadFromFile`과 `ReplaceStore`는 동일한 방어 패턴을 따릅니다:

```
1. backup = deepClone store           ← 실패 시 복원용 스냅샷
2. ensureValidStoreOrThrow newStore   ← 입력 데이터 검증
3. ReplaceAllCollections              ← 13개 Dictionary 교체
4. undoManager.Clear()                ← Undo/Redo 스택 초기화
5. StoreRefreshed 발행                ← UI 전체 재구성
6. HistoryChanged([], []) 발행        ← History 패널 초기화
   실패 시: backup으로 복원 후 예외
```

| API | 입력 소스 | 주요 차이점 |
|-----|---------|-----------|
| `LoadFromFile(path)` | JSON 파일 | `JsonConverter.deserialize<DsStore>` |
| `ReplaceStore(newStore)` | 외부 구성된 DsStore | AASX 임포트 등 파일 I/O 경로에서 사용 |

주요 구현: `Ds2.Core/JsonConverter.fs` (namespace `Ds2.Serialization`), `Ds2.Store/Store/DsStore.fs`

### 6.6 backupEntity vs jsonCloneEntity

| 함수 | GUID | 사용 위치 | 용도 |
|------|------|----------|------|
| `DeepCopyHelper.backupEntityAs<'T>` | **원본 유지** | TrackRemove / TrackMutate | Undo 복원용 백업 (서브타입 필드 보존) |
| `DeepCopyHelper.jsonCloneEntity` | **새 GUID 생성** | DirectPasteOps | 복사/붙여넣기용 독립 사본 |

이 두 함수를 혼동하면 Undo 시 GUID가 바뀌거나 붙여넣기 결과가 원본과 ID가 겹치는 문제가 발생합니다.
`backupEntityAs<'T>`는 제네릭이라 `typeof<'T>`로 실제 서브타입의 모든 필드를 직렬화·복원합니다.

---

## 7. 선택 동기화

- 캔버스 선택 ↔ 트리 선택은 단일 경로(`SelectionState.cs`)에서 처리
- Ctrl 다중 선택, Shift 범위 선택, 박스(드래그) 선택 지원
- 선택 정렬·범위 판정은 F# `SelectionQueries`에서 처리, C#에서 중복 구현하지 않음
- 선택 변경 시 `SelectionChanged` 이벤트 → 속성 패널 갱신

---

## 8. AASX 임포트/익스포트

### 8.1 개요

`Ds2.Aasx` 프로젝트가 IEC 62714 / AAS 3.0 AASX 파일과 `DsStore` 간 양방향 변환을 담당합니다.
`Ds2.Store`/`Ds2.Editor`는 `Ds2.Aasx`를 참조하지 않습니다. `Promaker`(C#)가 양쪽을 직접 참조합니다.

### 8.2 익스포트 흐름

```
FileCommands.SaveToPath (C#)
  → 확장자 `.aasx` 분기
  → AasxExporter.exportFromStore(_store, path)  ← F# 직접 호출
  → SaveOutcomeFlow.TryCompleteAasxSave(exported, warn, failMsg, onSuccess)
      → SplitDeviceAasx 분기:
            false → exportToAasxFile (인라인, 기존 동작)
            true  → exportSplitAasx ({baseName}_devices/ 폴더에 Device별 AASX 분리 저장)
      → exportToSubmodel: Project 계층 → AAS Submodel (Ds2SequenceControlSubmodel)
            Call → SMC
            Work → SMC (Calls SML + ArrowsBetweenCalls SML)
            Flow → SMC (Works SML + ArrowsBetweenWorks SML)
            DsSystem → SMC (Flows SML + ApiDefs SML, IsActiveSystem 플래그)
            Project → 최상위 SMC
      → exportNameplateSubmodel: Project.Nameplate → Nameplate Submodel
            IDTA 02006-3-0 표준 준수
            프로젝트 명판 정보 (제조사, 제품명, 시리얼번호 등)
      → exportHandoverDocumentationSubmodel: Project.HandoverDocumentation → HandoverDocumentation Submodel
            IDTA 02004-1-2 표준 준수
            인수인계 문서 (Document + DocumentVersion SMC)
      → ConceptDescription 41개 자동 생성
            Nameplate IRDI 35개 + Documentation IRDI 6개
      → IriPrefix 기반 Shell ID 생성, GlobalAssetId 자동 해석
      → writeEnvironment env path  ← AASX ZIP 생성 (XML 직렬화)
```

**직렬화 전략**:
- 단순 필드 (Name, Guid, DevicesAlias, ArrowType 등): AAS `Property` (string)
- 복잡한 타입 (ApiCall list, CallCondition list, *Properties, Xywh option): `JsonConverter.serialize<T>` → JSON 문자열 → AAS `Property`

### 8.3 임포트 흐름

```
FileCommands.OpenFile (C#)
  → 확장자 `.aasx` 분기
  → TryRunFileOperation("Open AASX", ...)
  → AasxImporter.importIntoStore(_store, path)  ← F# 직접 호출 → bool
      → importFromAasxFile(path)  ← 내부 변환 함수
            → readEnvironment path  ← AASX ZIP 열기 (XML/JSON 자동 판별)
            → Ds2SequenceControlSubmodel → Project + 모든 엔티티 재구성
                  SMC → Call (ApiCalls/CallConditions JSON 역직렬화)
                  SMC → Work → Calls + ArrowBetweenCalls
                  SMC → Flow → Works + ArrowBetweenWorks
                  SMC → DsSystem (isActive 플래그로 ActiveSystemIds/PassiveSystemIds 분류)
                  Submodel → Project + DsStore
            → Nameplate Submodel → Project.Nameplate (IDTA 02006-3-0)
                  Nameplate SMC 항목 → Project.Nameplate 레코드 필드로 매핑
            → HandoverDocumentation Submodel → Project.HandoverDocumentation (IDTA 02004-1-2)
                  Document/DocumentVersion SMC → Project.HandoverDocumentation 레코드 필드로 매핑
            → DeviceReference에 DeviceRelativePath 있으면 → 외부 참조 모드
                  상대경로 검증 (.. 금지, 절대경로 금지) → Device AASX 파일 로드
                  파일 미존재 시 Warn + 스킵 (graceful degradation)
            → 새 DsStore 직접 구성 (store.DirectWrite로 컬렉션 채움)
      → importIntoStore: _store.ReplaceStore(imported) + bool 반환
  → PrepareForLoadedStore() → CompleteOpen(fileName, "AASX") → RequestRebuildAll(AfterFileLoad)
```

### 8.4 AASX 파일 구조 (ds2 전용)

```
Environment
└── AssetAdministrationShell (id="{iriPrefix}{projectId}" 또는 "urn:ds2:shell:{projectId}")
│   └── GlobalAssetId: iriPrefix + projectId 또는 자동 해석
│
├── Submodel (idShort="Ds2SequenceControlSubmodel")
│   └── SMC (Project)
│       ├── Property "Name", "Guid", "Properties" (JSON)
│       ├── SML "ActiveSystems"
│       │   └── SMC (DsSystem, IsActiveSystem="true")
│       │       ├── SML "Flows"
│       │       │   └── SMC (Flow)
│       │       │       ├── SML "Works"
│       │       │       │   └── SMC (Work)
│       │       │       │       ├── SML "Calls"
│       │       │       │       │   └── SMC (Call) — ApiCalls/CallConditions as JSON
│       │       │       │       └── SML "ArrowsBetweenCalls"
│       │       │       └── SML "ArrowsBetweenWorks"
│       │       └── SML "ApiDefs"
│       └── SML "DeviceReferences"  (= PassiveSystems)
│           ├── [인라인 모드] SMC (DsSystem, IsActiveSystem="false") — 기존 동작
│           └── [분리 모드] SMC (DeviceReference)
│                   ├── Property "DeviceGuid"
│                   ├── Property "DeviceName"
│                   ├── Property "DeviceIRI"
│                   └── Property "DeviceRelativePath" (예: "MyProject_devices/PLC_Siemens.aasx")
│
├── Submodel (idShort="Nameplate", IDTA 02006-3-0)
│   └── Property "ManufacturerName", "ManufacturerProductDesignation", ...
│       (Project.Nameplate 레코드 필드 매핑)
│
├── Submodel (idShort="HandoverDocumentation", IDTA 02004-1-2)
│   └── SML "Document"
│       └── SMC "DocumentVersion" — 인수인계 문서 정보
│
└── ConceptDescription × 41
    ├── Nameplate IRDI × 35
    └── Documentation IRDI × 6
```

### 8.5 ReplaceStore

```
store.ReplaceStore(newStore: DsStore)
  → backup = deepClone store          ← 현재 상태 백업
  → ensureValidStoreOrThrow newStore
  → ReplaceAllCollections newStore     ← 13개 Dictionary 교체
  → undoManager.Clear()               ← Undo/Redo 스택 초기화
  → StoreRefreshed 이벤트 발행
  → HistoryChanged([], []) 발행       ← History 패널 초기화
  실패 시: ReplaceAllCollections backup → 예외
```

임포트 후 Undo 스택은 초기화되므로 임포트 직후 Undo 동작은 불가합니다.

### 8.6 Device System AASX 분리 저장

`ProjectProperties.SplitDeviceAasx = true`이면 저장 시 PassiveSystem을 별도 AASX 파일로 분리합니다.

**설정**: 프로젝트 설정 다이얼로그 → "Device System을 별도 AASX 파일로 분리 저장" 체크박스

**익스포트 (`exportSplitAasx`)**:
1. `{baseName}_devices/` 폴더 생성
2. 각 PassiveSystem → `exportDeviceAasx`로 독립 AASX 저장 (`ActiveSystems=[device]` 래핑)
3. 메인 AASX → ActiveSystems + DeviceReference SMC (Guid, Name, IRI, RelativePath만)
4. Device 이름 충돌 시 `{Name}_{Guid짧은해시}.aasx` 자동 부여

**임포트 (`submodelToProjectStore`)**:
1. DeviceReference에 `DeviceRelativePath` 프로퍼티 존재 → 외부 참조 모드
2. 상대경로 검증: `..` 금지, 절대경로 금지
3. `Path.Combine(mainDir, relativePath)` → Device AASX 읽기
4. 파일 미존재 시 `log.Warn` + 스킵 (graceful degradation)
5. DeviceRelativePath 없는 DeviceReference → 기존 인라인 모드 (역호환)

**동작 시나리오**:
- 분리 저장 → 다시 열기: `_devices/` 폴더에서 각 Device 로드
- 분리 저장 → 체크 해제 → 저장: 모든 Device가 메인 AASX에 인라인 포함 (기존 동작)
- `_devices/` 파일 일부 누락: 존재하는 Device만 로드, 누락분은 경고 후 스킵
- 폴더째 이동: 상대경로 기반이므로 정상 동작

---

## 9. Mermaid 임포트/익스포트

### 9.1 개요

`Ds2.Mermaid` 프로젝트가 Mermaid `.md` 파일과 `DsStore` 간 양방향 변환을 담당합니다.
파이프라인: `Lexer → Parser → Analyzer → MapperCommon → Targets/ (MapperTargets + MapperTargetPlanning) → Mapper → Importer` (임포트) / `Exporter` (익스포트).

### 9.2 임포트 흐름

```
[C# — FileCommands.cs]
 OpenFile()
  └─ 확장자 `.md` 분기
  └─ TryRunFileOperation(
        MermaidImporter.loadProjectFromFile(fileName)  ← F# 직접 호출
        → FSharpResult<DsStore, string list>
        → TryGetResult로 에러 처리)
        │
        ▼
[F# — Ds2.Mermaid.Import/]
 loadProjectFromFile path : Result<DsStore, string list>
  ├─ MermaidLexer.tokenize(text)        → Token list
  ├─ MermaidParser.parse(tokens)        → MermaidAst
  ├─ MermaidAnalyzer.analyze(ast)       → AnalyzedModel
  ├─ MermaidMapper.mapToStore(model)    → DsStore
  └─ 성공: Ok store / 실패: Error errorMessages
        │
        ▼
[C# — FileCommands.cs]
 PrepareForLoadedStore() → ReplaceOpenedStore(fileName, store, "Mermaid")
  └─ _store.ReplaceStore(store)
  └─ CompleteOpen(fileName, "Mermaid") — _currentFilePath/IsDirty/Title 갱신
  └─ RequestRebuildAll(AfterFileLoad) — 트리 확장 + 첫 System 탭 열기
```

### 9.3 익스포트 흐름

```
[C# — FileCommands.cs]
 SaveFile() → SaveToPath(filePath)
  └─ 확장자 `.md` 분기
  └─ MermaidExporter.saveProjectToFile(_store, filePath)  ← F# 직접 호출
        → FSharpResult<unit, string>
        │
        ▼
[C# — SaveOutcomeFlow.cs]
 TryCompleteMermaidSave(result, warn, onSuccess)
  ├─ result.IsError → warn(errorValue) + return false
  └─ 성공 → CompleteSave(filePath, "Mermaid") + return true
```

---

## 10. CSV 임포트/익스포트

### 10.1 개요

`Ds2.CSV` 프로젝트가 CSV 파일과 `DsStore` 간 양방향 변환을 담당합니다.
파이프라인: `CsvTypes → CsvParser → CsvMapper → CsvImporter/CsvExporter` (5 모듈).

### 10.2 임포트 흐름

```
[C# — CsvCommands.cs]
 ImportCsv()
  └─ ConfirmDiscardChanges() → TryRunFileOperation(
        TryCreateCsvStore(out store, out sourceName))
        │
        ▼
[C# — CsvImportDialog]
 사용자 입력: CSV 텍스트 또는 파일 + ProjectName + SystemName
  → dialog.Document (파싱할 CSV 문자열)
  → dialog.ProjectName, dialog.SystemName
        │
        ▼
[F# — Ds2.CSV]
 CsvImporter.loadProject(document, projectName, systemName) : Result<DsStore, string list>
  ├─ CsvParser.parse(document) → CsvRow list
  ├─ CsvMapper.mapToEntities(rows) → EntityTree
  └─ DsStore 구성 (Project + System + Flow + Work + Call)
        │
        ▼
[C# — CsvCommands.cs]
 ImportCsvStore(store, sourceName)
  └─ PrepareForLoadedStore()
  └─ _store.ReplaceStore(store)
  └─ _currentFilePath = null, IsDirty = false
  └─ RequestRebuildAll(AfterFileLoad)
```

### 10.3 익스포트 흐름

```
[C# — CsvCommands.cs]
 ExportCsv()
  └─ CsvExportDialog(projectName, preview, suggestedFileName)
  └─ TryRunFileOperation(
        CsvExporter.saveProjectToFile(_store, outputPath))
        │
        ▼
[F# — Ds2.CSV]
 CsvExporter.saveProjectToFile(store, path) : Result<unit, string>
  ├─ DsQuery로 Project → System → Flow → Work → Call 계층 순회
  ├─ CSV 행 생성: Flow, Work, Call, DevicesAlias, ApiName 등
  └─ File.WriteAllText(path, csvText)
```

---

## 11. FileCommands 헬퍼 구조

FileCommands.cs의 Open/Save 공통 코드는 헬퍼 메서드와 전용 Flow 클래스로 분리되어 있습니다.

### 11.1 Open 헬퍼

| 헬퍼 | 역할 |
|------|------|
| `TryRunFileOperation(operation, action, warnMessage)` | try-catch + Log.Error + DialogHelpers.Warn, bool 반환 |
| `TryGetResult<T, TError>(result, formatError, out value)` | FSharpResult 분기: Error → Warn, Ok → out value |
| `CompleteOpen(filePath, kind)` | `_currentFilePath` + `IsDirty` + `UpdateTitle` + `RequestRebuildAll(AfterFileLoad)` |
| `ReplaceOpenedStore(filePath, store, kind)` | `_store.ReplaceStore(store)` + `CompleteOpen` |
| `PrepareForLoadedStore()` | 로드 전 UI 정리 (MainViewModel.cs에 정의) |

### 11.2 Save 헬퍼

| 헬퍼 | 역할 |
|------|------|
| `CompleteSave(filePath, kind)` | `_currentFilePath` + `IsDirty` + `UpdateTitle` + StatusText + Log |
| `SaveOutcomeFlow.TryCompleteMermaidSave` | `FSharpResult<unit, string>` → warn/success 분기 |
| `SaveOutcomeFlow.TryCompleteAasxSave` | `bool exported` → warn/success 분기 |

### 11.3 DiscardChangesFlow

`ConfirmDiscardChanges()` 호출 시 사용자 선택에 따라:

| MessageBoxResult | 동작 |
|-----------------|------|
| `Yes` | `TrySaveFile()` 호출 → 저장 성공 시만 true |
| `No` | 변경 사항 버리고 true |
| `Cancel` | false (동작 취소) |

---

## 12. 일괄편집 (Batch)

### 12.1 Duration 일괄편집

```
[C# — DurationBatchCommands.cs]
 OpenDurationBatchDialog()
  └─ _store.GetAllWorkDurationRows() → WorkDurationBatchRow list
  └─ DurationBatchDialog(rows) — DataGrid UI
  └─ dialog.ChangedRows → (workId, ms) 변경분만 추출
  └─ _store.UpdateWorkDurationsBatch(changes)
        │
        ▼
[F# — Ds2.Editor/Store/Panel/Panel/Batch.fs]
 UpdateWorkDurationsBatch(changes: seq<struct(Guid * int)>)
  └─ WithTransaction("Work Duration 일괄 변경", ...)
  └─ TrackMutate per work → work.Properties.Period 변경
  └─ EmitRefreshAndHistory()
```

### 12.2 I/O 태그 일괄편집

```
[C# — IoBatchCommands.cs]
 OpenIoBatchDialog()
  └─ _store.GetAllApiCallIORows() → ApiCallIOBatchRow list
  └─ IoBatchSettingsDialog(rows) — DataGrid UI
  └─ dialog.ChangedRows → (apiCallId, inAddr, inSym, outAddr, outSym) 변경분
  └─ _store.UpdateApiCallIOTagsBatch(changes)
        │
        ▼
[F# — Ds2.Editor/Store/Panel/Panel/Batch.fs]
 UpdateApiCallIOTagsBatch(changes: seq<struct(Guid * string * string * string * string)>)
  └─ WithTransaction("I/O 태그 일괄 변경", ...)
  └─ TrackMutate per apiCall → InTag/OutTag 변경
  └─ EmitRefreshAndHistory()
```

---

## 13. 로깅 (log4net)

### 13.1 초기화 흐름

```
앱 시작
  → App.xaml.cs OnStartup
      → XmlConfigurator.Configure(new FileInfo("log4net.config"))
           log4net.config 있음  → log4net 초기화
           log4net.config 없음  → System.Diagnostics.Trace.TraceWarning("log4net.config 파일을 찾을 수 없습니다...")
                                   (log4net 미초기화 상태에서도 VS 출력 창에 표시)
      → Log.Info("=== Promaker startup ===")
      → DispatcherUnhandledException += (_, ex) =>
              Log.Fatal("처리되지 않은 예외", ex.Exception); ex.Handled = true
앱 종료
  → App.xaml.cs OnExit
      → Log.Info("=== Promaker shutdown ===")
```

### 13.2 로그 파일

```
<실행 파일 위치>/logs/ds2_yyyyMMdd.log
```

- **Appender**: `RollingFileAppender` (Composite 롤링, 최대 10MB × 10개 백업)
- **패턴**: `%date{yyyy-MM-dd HH:mm:ss.fff} [%-5level] %logger{1} — %message%newline%exception`
- **Visual Studio**: `DebugAppender`로 출력 창에도 동시 출력 (패턴 단축)

### 13.3 레이어별 로거와 레벨

#### F# — Ds2.Store/DsStore.fs + Ds2.Editor/Store/Log.fs

| 지점 | 레벨 | 메시지 형태 |
|------|------|------------|
| `WithTransaction` 성공 | DEBUG | `Executed: {label}` |
| `WithTransaction` 실패 | ERROR | `Transaction failed: {label} — {ex.Message}` + 예외 |
| Undo/Redo 성공 | DEBUG | `Undo: {label}` / `Redo: {label}` |
| Undo/Redo 실패 | ERROR | `Undo failed: {label} — {ex.Message}` + 예외 |
| `ApplyNewStore` 성공 | INFO | `Store applied: {contextLabel}` |
| `ApplyNewStore` 실패 | ERROR | `ApplyNewStore failed: {contextLabel} — {ex.Message}` + 예외 |
| `SaveToFile` 성공 | INFO | `저장 완료: {path}` |
| `SaveToFile` 실패 | ERROR | `저장 실패: {path} — {ex.Message}` + 예외 (재throw) |

로거 선언:
```fsharp
// Ds2.Editor/Store/Log.fs (StoreLog 모듈)
let private log = LogManager.GetLogger("Ds2.Editor.StoreLog")
// Ds2.Store/Store/DsStore.fs (DsStore 클래스 내부)
let log = LogManager.GetLogger(typedefof<DsStore>)
```

#### F# — AasxFileIO.fs

| 지점 | 레벨 | 메시지 형태 |
|------|------|------------|
| `readEnvironment` 예외 (AASX ZIP 읽기 실패) | WARN | `"AASX 읽기 실패"` + 예외 객체 (stack trace 포함) |

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

진입점 `importFromAasxFile`의 silent None 분기도 WARN으로 노출:

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
모든 partial 파일(`Shell/EventHandling.cs`, `Shell/FileCommands.cs`, `NodeCommands.cs` 등)은 같은 partial class이므로 별도 선언 없이 `Log` 공유.

| 지점 | 레벨 | 메시지 형태 |
|------|------|------------|
| EditorEvent 구독자 에러 | ERROR | `EditorEvent 구독자 에러` + 예외 |
| 파일 열기 성공 | INFO | `{kind} opened: {path}` (kind=File/AASX/Mermaid) |
| 파일 열기 실패 | ERROR | `{operation} failed` + 예외 (TryRunFileOperation) |
| 파일 저장 성공 | INFO | `{kind} saved: {path}` |
| 파일 저장 실패 | ERROR | `Save {kind} '{path}' failed` + 예외 |
| AASX open 빈 결과 | WARN | `AASX open failed: empty result ({path})` |
| Unhandled EditorEvent | WARN | `Unhandled event: {evt.GetType().Name}` |
| Mermaid 열기 성공 | INFO | `Mermaid opened: {path}` |
| Mermaid 저장 성공 | INFO | `Mermaid saved: {path}` |
| Mermaid 열기/저장 실패 | ERROR | `{operation} failed` + 예외 → DialogHelpers.Warn |
| CSV import 성공 | INFO | `CSV imported: {sourceName}` |
| CSV export 성공 | INFO | `CSV exported: {path}` |
| CSV import/export 실패 | ERROR | `{operation} failed` + 예외 → DialogHelpers.Warn |

### 13.4 로깅 레벨 요약

| 레벨 | 의미 | 지점 |
|------|------|------|
| FATAL | 앱이 계속 실행되기 어려운 미처리 예외 | `DispatcherUnhandledException` |
| ERROR | 복구 시도했거나 사용자에게 알린 실패 | 파일 I/O 실패, WithTransaction 실패, Undo/Redo 실패 |
| WARN | 기능 저하지만 계속 실행 가능 | AASX import 빈 결과, AASX ZIP 읽기 실패 |
| INFO | 정상 수명주기 이벤트 | 앱 시작/종료, 파일 I/O 성공, Store 교체 |
| DEBUG | 반복 호출 상세 추적 | WithTransaction 각 성공, Undo/Redo 각 단계 |

---

## 14. 시뮬레이션 엔진 런타임

### 14.1 개요

`Ds2.Runtime.Sim` 프로젝트가 이벤트 기반 시뮬레이션을 담당합니다. Work/Call의 상태 전이와 DataToken 시프트를 처리합니다.

### 14.2 엔진 시작 흐름

```
[C# — SimulationPanelState.cs]
 StartSimulation()
  ├─ GraphValidator 경고 수집 (7종)
  │    findUnresetWorks, findDeadlockCandidates,
  │    findSourcesWithPredecessors, findSourceCandidates,
  │    findGroupWorksWithoutIgnore, findTokenUnreachableWorks
  │    → ShowThemedMessageBox (시뮬레이션 차단 안 함)
  ├─ SimIndex.build(store) → SimIndex
  ├─ EventDrivenEngine(index) 생성
  ├─ 이벤트 구독 (WorkStateChanged, CallStateChanged, TokenEvent)
  └─ engine.Start()
       → startEngineThread() → EventDrivenEngineRuntime.simulationLoop()
```

### 14.3 이벤트 루프 (EngineRuntime 모듈)

`EventDrivenEngineRuntime` 모듈(`Engine/EventDriven/EngineRuntime.fs`)이 `processEvent`와 `simulationLoop`를 소유합니다.
`EventDrivenEngine`은 `RuntimeContext` 레코드를 구성하여 주입합니다.

```fsharp
type RuntimeContext = {
    Scheduler: EventScheduler
    GetStatus: unit -> SimulationStatus
    SpeedMultiplier: unit -> float
    GetWorkState: Guid -> Status4
    GetWorkToken: Guid -> TokenValue option
    ClearAndApplyWorkTransition: Guid -> Status4 -> unit
    ClearAndApplyCallTransition: Guid -> Status4 -> unit
    ApplyWorkTransition: Guid -> Status4 -> unit
    HandleDurationComplete: Guid -> unit
    ShiftToken: Guid -> TokenValue -> unit
    EmitTokenEvent: TokenEventKind -> TokenValue -> Guid -> Guid option -> unit
    ScheduleConditionEvaluation: unit -> unit
    EvaluateConditions: unit -> unit
}
```

```
simulationLoop:
  while Running:
    simDelta = realDelta * speedMultiplier
    targetMs = currentTime + simDelta
    events = scheduler.AdvanceTo(targetMs)
    for event in events:
      processEvent(event)

    // drain 루프 — cascade 완주
    while draining && Running:
      pending = scheduler.AdvanceTo(targetMs)
      if pending.IsEmpty then stop
      else processEvent each

    Thread.Sleep(1)
```

### 14.4 이벤트 타입 및 처리

| 이벤트 | 처리 (EngineRuntime.processEvent) |
|--------|------|
| `WorkTransition(guid, state)` | `ClearAndApplyWorkTransition` → 상태 변경 + 후속 효과 |
| `CallTransition(guid, state)` | `ClearAndApplyCallTransition` → 상태 변경 |
| `DurationComplete(guid)` | `HandleDurationComplete` → leaf Work: Going→Finish / Call있는 Work: MinDurationMet 마킹 |
| `HomingComplete(guid)` | 토큰 shiftToken 시도 → Ready 또는 BlockedOnHoming |
| `EvaluateConditions` | 6단계 평가 (evaluateConditions) |

### 14.4.1 Work Duration 계산 (Effective Duration)

SimIndex.build 시 각 Work의 Duration을 결정합니다:

```
Work에 Call이 없는 경우 (leaf):
  duration = userPeriodMs (사용자 설정값)

Work에 Call이 있는 경우:
  deviceCriticalPathMs = DsQuery.tryGetDeviceDurationMs(workId, store)
    → ArrowBetweenCalls 토폴로지 기반 최장 경로 (DAG Critical Path)
    → longestPath(c) = duration(c) + max(longestPath(pred))
  duration = max(userPeriodMs, deviceCriticalPathMs)
```

**Critical Path 알고리즘**: Call 간 Start/StartReset 화살표로 DAG를 구성하고, 각 Call의 device duration(ApiDef → RxWork → Period)을 가중치로 최장 경로를 메모이제이션으로 계산합니다.

### 14.4.2 Min Duration 메커니즘

Work에 Call이 있을 때, "모든 Call 완료" AND "최소 시간(effective duration) 경과" 양쪽 조건을 모두 충족해야 Work가 완료됩니다.

```
Work Going 진입:
  ├─ leaf (Call 없음): MarkMinDurationMet 즉시, DurationComplete 스케줄
  ├─ Call 있음 + duration > 0: DurationComplete 스케줄 (완료 시 MarkMinDurationMet)
  └─ Call 있음 + duration = 0: MarkMinDurationMet 즉시

evaluateWorkCompletions:
  모든 Call Finish + IsMinDurationMet(workGuid) → Work Finish
```

### 14.4.3 TimeIgnore 핫스왑

`TimeIgnore = true` 전환 시, 이미 스케줄된 `DurationComplete`/`HomingComplete`가 원래 딜레이를 기다리는 문제를 방지합니다.

```
TimeIgnore setter:
  prev=false → i=true 전환 + Running 상태:
    for wg in AllWorkGuids:
      Going + MinDurationMet 안 됨 → ScheduleNow(DurationComplete wg)
      Homing                       → ScheduleNow(HomingComplete wg)
```

processEvent의 상태 체크(`GetWorkState == Going/Homing`)가 중복 이벤트를 안전하게 필터링합니다.

### 14.5 토큰 흐름

```
Source Work Going
  → 자동 Seed (TokenSpec.Label#{n} 또는 WorkName#{n})
  → StateManager.SetWorkToken(workGuid, Some token)
  → StateManager.SetTokenOrigin(token, name)
  → emitTokenEvent Seed

Work Finish
  → TokenFlow.onWorkFinish
    → shiftToken
      → Sink/successor없음 → Complete (토큰 소멸)
      → Ready+빈슬롯 successor → Shift (토큰 이동)
      → 모두 점유 → Blocked (재시도 대기)

Token Conflict (Finish + 토큰 + 리셋 조건)
  → Conflict 이벤트 발행
  → Homing 정상 진행 (토큰 보유한 채)
  → HomingComplete 시 shiftToken 재시도
    → 성공 → Ready 전환
    → 실패 → BlockedOnHoming 이벤트
```

### 14.6 Sink 감지 (SimIndex.build)

| 유형 | 감지 방식 |
|------|----------|
| 선형 Sink | successor 없는 Work |
| 순환 Sink | DFS back edge의 source (A→B→C→A → C가 sink) |
| Source기반 Sink | successor가 Source인 Work |

Source 자체는 sink에서 제외됩니다. `TokenPathGuids`는 Source에서 BFS로 전체 토큰 경로를 수집합니다.

### 14.7 evaluateConditions 6단계

```
1. evaluateWorkStarts()      — Ready Work → Going 스케줄 (scheduledGoingGuids 반환)
2. evaluateWorkResets()       — Finish Work → Homing (Conflict 이벤트 포함)
3. evaluateCallStarts()       — Ready Call → Going
4. evaluateCallCompletions()  — Going Call → Finish
5. evaluateWorkCompletions()  — Going Work (모든 Call Finish + MinDurationMet) → Finish
6. retryBlockedTokens()       — Blocked 토큰 재시도 (Finish/Homing Work 대상)
```

### 14.8 GraphValidator (시뮬레이션 시작 시)

| 검증 | 함수 | 의미 | 심각도 |
|------|------|------|:------:|
| Reset 연결 누락 | `findUnresetWorks` | Sink 제외 Work 중 reset predecessor 없음 | Yellow |
| 순환 데드락 위험 | `findDeadlockCandidates` | 정방향 descendant 순환 의존 | Red |
| Source 자동 시작 불가 | `findSourcesWithPredecessors` | predecessor 있는 Source → 자동 시작 불가 | Yellow |
| Source 후보 | `findSourceCandidates` | Source 지정 시 데드락 해소 가능한 후보 | Yellow |
| Group Ignore 누락 | `findGroupWorksWithoutIgnore` | 그룹 N개 Work 중 비Ignore가 2개 이상 | Yellow |
| 토큰 도달 불가 | `findTokenUnreachableWorks` | 모든 토큰 선행자가 Ignore → 토큰 전달 불가 | Red |

경고만 표시하고 시뮬레이션은 차단하지 않습니다.

**토큰 도달 불가 감지 원리**: `WorkTokenSuccessors` 역전 맵으로 각 Work의 토큰 선행자(token predecessor)를 구하고, Source가 아닌 Work 중 모든 선행자가 Ignore이면 해당 Work로 토큰이 도달할 수 없음을 경고합니다.

### 14.9 ForceWork (수동 제어)

```
ForceWorkStart:
  자동선택 → BatchStartSources
    → WarnSourcesWithoutTokenSpec / WarnFinishedSources / CollectBlockedSources
    → EnsureSourceToken → ForceWorkState(Going)
  단일 → SingleStartWork
    → Finish/Homing → WarnWorkNotReady
    → Source → predecessor 확인 + EnsureSourceToken

ForceWorkReset:
  토큰 보유 → ShowPausedMessageBox 확인 → DiscardToken
  → ForceWorkState(Ready)
```

### 14.10 엔진 Context 패턴

각 서브모듈은 독립 `Context` 레코드를 받습니다:

```
EventDrivenEngine (오케스트레이션)
  ├─ TokenFlow.Context                   — Index, StateManager, CurrentTimeMs, TriggerTokenEvent
  ├─ WorkTransitions.Context             — Index, StateManager, Scheduler, TimeIgnore, OnWorkFinish, ...
  ├─ ConditionEvaluation.Context         — Index, StateManager, Scheduler, CanStartWork, ShiftToken, ...
  └─ EventDrivenEngineRuntime.RuntimeContext — Scheduler, GetStatus, processEvent 의존 함수들
```

Context 레코드로 의존성을 명시적으로 주입하므로 서브모듈 간 순환 참조가 없습니다.

### 14.11 SimIndex 서브모듈

`SimIndex.build(store)` 내부에서 2개 서브모듈을 호출합니다:

| 서브모듈 | 파일 | 역할 |
|---------|------|------|
| `SimIndex.GroupExpansion` | `Engine/Core/SimIndex.GroupExpansion.fs` | Union-Find로 순환 그룹 확장 → 그룹 내 Work를 동일 엔진 단위로 처리 |
| `SimIndex.TokenGraph` | `Engine/Core/SimIndex.TokenGraph.fs` | DFS 기반 토큰 경로 수집 + back edge 사이클 검출 → TokenSinkGuids, TokenPathGuids 생성 |

### 14.12 PropertyPanel Device Duration 표시

속성 패널에서 Work 선택 시 `DsQuery.tryGetDeviceDurationMs`로 예상 소요 시간을 계산하여 표시합니다.

```
PropertyPanelState.Refresh():
  Work 선택 시:
    DsQuery.tryGetDeviceDurationMs(workId, store) → int option
    Some ms → DeviceDurationHint = "예상 소요 시간: {ms}ms"
    None    → DeviceDurationHint = "" (숨김)

ApplyWorkPeriod():
  deviceDurationMs 존재 시:
    userMs > deviceMs → "설정값이 적용됩니다" 안내
    userMs ≤ deviceMs → "예상 시간이 우선됩니다" 안내
    확인 후 저장
```

### 14.13 프로젝트 의존성

```
Ds2.Runtime.Sim → Ds2.Store → Ds2.Core
                  (Editor 불필요 — Store만으로 SimIndex 빌드 가능)
Ds2.Runtime.Sim.Report → Ds2.Runtime.Sim
```
