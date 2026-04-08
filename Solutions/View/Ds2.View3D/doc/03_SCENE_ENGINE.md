# Ds2 3D View — 03. SceneEngine & ContextBuilder

**파일**: `SceneEngine.fs`, `ContextBuilder.fs`, `Persistence.fs`

---

## 1. SceneEngine

`SceneEngine(store, layoutStore)` — DsStore와 ILayoutStore를 주입받아 씬 데이터를 구성하는 통합 API.

### 생성자 파라미터

| 파라미터 | 타입 | 설명 |
|---------|------|------|
| `store` | `DsStore` | Promaker 프로젝트 데이터 소스 |
| `layoutStore` | `ILayoutStore` | 레이아웃 저장/복원 추상화 |

### 핵심 메서드

#### BuildDeviceScene
```fsharp
member this.BuildDeviceScene(sceneId: string, projectId: Guid) : SceneData
```
단일 호출로 완전한 `SceneData` 반환. `ThreeDViewState.BuildScene()`에서 사용.

내부 순서:
1. `GetDeviceNodes(projectId)` → DeviceNode list
2. `PlaceAllDevices(sceneId, projectId)` → LayoutPosition list (저장 있으면 복원, 없으면 계산)
3. `ContextBuilder.buildFlowZones(devices, positions)` → FlowZone list
4. 결합해서 `SceneData` 반환

#### PlaceAllDevices
```fsharp
member this.PlaceAllDevices(sceneId, projectId, ?mode) : LayoutPosition list
```
저장된 레이아웃이 있으면 그것을 반환. 없으면 `LayoutAlgorithm.layoutDevices` 호출 후 저장.

#### PlaceDevice (수동)
```fsharp
member _.PlaceDevice(sceneId, deviceId, x, z) : LayoutPosition
```
단일 Device 수동 배치. 기존 저장 레이아웃에 merge해서 재저장.

#### Select
```fsharp
member _.Select(event: SelectionEvent) : SelectionState.State
```
JS 클릭 이벤트를 받아 선택 상태 적용. Phase 2 구현 예정.

---

## 2. ContextBuilder

`DsStore`의 원시 엔티티를 3D View DTO로 변환하는 순수 함수 모듈.

### buildDeviceNodes
```fsharp
val buildDeviceNodes : DsStore -> projectId: Guid -> DeviceNode list
```
- `DsQuery.allSystems(store)` → Active/Passive Systems 필터
- System → DeviceNode 변환
  - `DeviceType` 추론 (이름 패턴 + SystemType)
  - `ApiDefs` 수집 (Call에서 역추적)
  - `IsUsedInSimulation` = 해당 Device를 참조하는 Call 존재 여부

### buildWorkNodes
```fsharp
val buildWorkNodes : DsStore -> flowId: Guid -> WorkNode list
```
Flow 안의 Work들을 `WorkNode`로 변환. incoming/outgoing 연결 포함.

### buildFlowZones
```fsharp
val buildFlowZones : DeviceNode list -> LayoutPosition list -> FlowZone list
```
배치된 Device 좌표를 기반으로 각 Flow의 bounding box 계산.
`LayoutAlgorithm.computeFlowZones`를 호출.

---

## 3. ILayoutStore & InMemoryLayoutStore

```fsharp
type ILayoutStore =
    abstract LoadLayout : sceneId:string * mode:SceneMode -> LayoutPosition list
    abstract SaveLayout : sceneId:string * mode:SceneMode * positions:LayoutPosition list -> unit
    abstract ClearLayout : sceneId:string * mode:SceneMode -> unit
```

현재 Promaker에서는 `InMemoryLayoutStore`를 사용.
세션 내에서 배치 위치가 유지됨. 앱 재시작 시 초기화.

**미래 구현 옵션**:
- `JsonFileLayoutStore` — `.sdf` 파일 옆에 `.3dlayout.json` 저장
- `SqliteLayoutStore` — SQLite에 레이아웃 테이블 저장

---

## 4. SelectionState

```fsharp
module SelectionState =
    type State = ...

    val empty : State
    val applyEvent : SelectionEvent -> WorkNode list -> DeviceNode list -> State -> State
```

선택 이벤트를 받아 새 상태를 반환하는 순수 함수형 상태 머신.

현재 Phase 1에서는 `ThreeDViewState.OnSelectionMessage()`가 Debug.WriteLine만 수행.
Phase 2에서 `SceneEngine.Select()` 호출 + PropertyPanel 연동 예정.

---

## 5. SceneEngine ↔ ThreeDViewState 연동

```
ThreeDViewState.BuildScene(store, projectId)
    │
    ├─ new InMemoryLayoutStore()
    ├─ new SceneEngine(store, layoutStore)
    └─ engine.BuildDeviceScene("promaker", projectId)
         │
         └─ SceneData
              ├─ DeviceNodes  → addDevice 메시지
              ├─ FlowZones    → init 메시지 config
              └─ Positions    → x, z 좌표 (addDevice 메시지)
```

`sceneId = "promaker"` 고정값. 추후 멀티 프로젝트 지원 시 projectId 기반으로 변경 가능.
