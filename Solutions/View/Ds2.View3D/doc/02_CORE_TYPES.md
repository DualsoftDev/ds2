# Ds2 3D View — 02. Core 타입 계약

**파일**: `Solutions/View/Ds2.3DView/Ds2.3DView.Core/Types.fs`
**네임스페이스**: `Ds2.ThreeDView`

---

## 1. 기본 값 타입

### Vec3
```fsharp
[<Struct>]
type Vec3 = { X: float; Y: float; Z: float }
```
3D 월드 좌표. Y=0 이 바닥 평면. Three.js 좌표계와 일치.

---

## 2. 열거형 (Discriminated Unions / Enums)

### SceneMode
```fsharp
type SceneMode =
    | Empty           = 0   // 빈 씬 (초기 상태)
    | WorkGraph       = 2   // Work 기반 씬 (Phase 3)
    | DevicePlacement = 2   // Device 배치 씬 (현재 사용)
```

### DeviceType
```fsharp
type DeviceType =
    | Robot   = 0   // 6축 로봇 모델, Going 시 애니메이션
    | Small   = 1   // 실린더/소형 장치 모델
    | General = 2   // 일반 박스형 모델
```

**자동 분류 패턴** (three-interop.js `_createDeviceStation`):
- 이름에 `_RB`, `RB\d+`, `ROBOT` → `robot`
- 이름에 `_CLP`, `_CT`, `_SV`, `_AIR`, `SLIDE`, `LOCK`, `CYLINDER` → `small`
- 그 외 → `general`

### LayoutMode
```fsharp
type LayoutMode =
    | Grid            = 0   // 단순 격자
    | FlowGroupedGrid = 1   // Flow별 그룹 격자 (기본값)
```

### NodeKind
```fsharp
type NodeKind =
    | WorkNode   = 0
    | DeviceNode = 1
```
`LayoutPosition`에서 노드 종류를 구분하는 데 사용.

---

## 3. View DTO (렌더러 독립 레코드)

### ApiDefNode
```fsharp
type ApiDefNode =
    { Id: Guid; Name: string; CallerCount: int; State: Status4 }
```
Device의 API 정의. `CallerCount > 1`이면 큐브 위에 배지 표시.

### CallNode
```fsharp
type CallNode =
    { Id: Guid; WorkId: Guid; WorkName: string; FlowName: string
      State: Status4; DevicesAlias: string; ApiDefName: string
      NextCallId: Guid option; PrevCallId: Guid option }
```
Work의 단계별 호출. `DevicesAlias`로 Device와 연결.

### WorkNode
```fsharp
type WorkNode =
    { Id: Guid; Name: string; FlowName: string; FlowId: Guid
      State: Status4; IncomingWorkIds: Guid list; OutgoingWorkIds: Guid list
      Calls: CallNode list; Position: Vec3 option }
```

### DeviceNode
```fsharp
type DeviceNode =
    { Id: Guid; Name: string; DeviceType: DeviceType; FlowName: string
      SystemType: string option; State: Status4
      ApiDefs: ApiDefNode list; IsUsedInSimulation: bool
      Position: Vec3 option }
```
3D 씬의 핵심 노드. `IsUsedInSimulation=false`이면 씬에 배치되어도 시뮬 상태 업데이트 안 함.

### FlowZone
```fsharp
type FlowZone =
    { FlowName: string; CenterX: float; CenterZ: float
      SizeX: float; SizeZ: float; Color: string }
```
바닥에 반투명 채색 영역으로 표시되는 Flow 경계 박스. `Color`는 `"#RRGGBB"` 형식.

### LayoutPosition
```fsharp
type LayoutPosition =
    { NodeId: Guid; NodeKind: NodeKind; X: float; Y: float; Z: float }
```
자동/수동 배치 결과 좌표. Y=0 고정(바닥 기준).

---

## 4. 선택 이벤트

```fsharp
type SelectionEvent =
    | WorkSelected   of workId: Guid
    | CallSelected   of callId: Guid
    | DeviceSelected of deviceId: Guid
    | ApiDefSelected of deviceId: Guid * apiDefName: string
    | EmptySpaceSelected
```
JS 클릭 → C# 콜백 경로로 전달됨.

---

## 5. SceneData (씬 빌드 결과)

```fsharp
type SceneData =
    { Mode: SceneMode
      WorkNodes: WorkNode list
      DeviceNodes: DeviceNode list
      FlowZones: FlowZone list
      Positions: LayoutPosition list }
```

`BuildDeviceScene()` 호출 결과:
- `DeviceNodes`: 배치할 장비 목록
- `Positions`: 각 장비의 월드 좌표 (NodeId로 연결)
- `FlowZones`: 바닥 구역 표시용 bounding box

---

## 6. Status4 → 3D 상태 코드 매핑

`Ds2.Core.Status4` (F#) → ThreeDViewState.cs의 `ToStateCode()` → JS 색상

| Status4 | 코드 | Three.js 색 | 의미 |
|---------|------|-------------|------|
| Ready   | `"R"` | `0x10b981` (Green) | 대기 |
| Going   | `"G"` | `0xeab308` (Yellow) | 동작 중 + 로봇 애니메이션 |
| Finish  | `"F"` | `0x3b82f6` (Blue) | 완료 |
| Homing  | `"H"` | `0x6b7280` (Gray) | 복귀 중 |
