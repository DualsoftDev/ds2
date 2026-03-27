# Ds2 3D 배치/구동 이식 스펙

## 1. 문서 목적

이 문서는 `Ev2.Frontend.Dashboard`의 3D 배치/구동 부분만 추출해 `Ds2 엔진`에 재구현하기 위한 이식 기준서다.

목표는 아래 하나다.

`Frontend Dashboard의 3D 배치 기능을 UI 기술에 종속되지 않는 Ds2 3D 엔진으로 분리하고, 이후 Blazor/WPF/기타 렌더러에서 공통으로 사용할 수 있게 만든다.`

이 문서는 "현재 코드가 무엇을 하는지"를 정리하는 데서 끝나지 않는다.  
`무엇을 그대로 가져가고`, `무엇을 버리고`, `Ds2 엔진에서는 어떤 API와 데이터 계약으로 다시 세울지`까지 규정한다.

## 2. 취출 범위

이번 문서에서 취출하는 범위는 아래다.

- 3D 씬 초기화
- Work/Device 3D 배치
- 자동 레이아웃
- 수동 드래그 편집
- 선택/하이라이트/연결선 표시
- 화면 좌표 -> 월드 좌표 변환
- 레이아웃 저장/복원
- 상태색/애니메이션 반영

이번 문서에서 제외하는 범위는 아래다.

- MudBlazor 페이지 레이아웃
- 문서 패널 UI 스타일
- 품질/작업지시 문서 상세 화면
- Plotly 차트
- Dashboard 전체 네비게이션

즉, 이번 스펙은 `3D Scene Engine + Placement Engine + Interaction Contract`만 다룬다.

## 3. 기준 소스

핵심 기준 파일은 아래다.

- `/mnt/c/ds/dsev2/solutions/Ev2.Frontend/Ev2.Frontend.Dashboard/Pages/Facility3D.razor`
- `/mnt/c/ds/dsev2/solutions/Ev2.Frontend/Ev2.Frontend.Dashboard/Components/Three3DView.razor`
- `/mnt/c/ds/dsev2/solutions/Ev2.Frontend/Ev2.Frontend.Dashboard/Components/AasTreePanel.razor`
- `/mnt/c/ds/dsev2/solutions/Ev2.Frontend/Ev2.Frontend.Dashboard/wwwroot/js/three-interop.js`
- `/mnt/c/ds/dsev2/solutions/Ev2.Frontend/Ev2.Frontend.Dashboard/wwwroot/js/ds-3d-commons.js`
- `/mnt/c/ds/dsev2/solutions/Ev2.Frontend/Ev2.Frontend.Dashboard.Shared/Models/AasTreeModels.cs`
- `/mnt/c/ds/dsev2/solutions/Ev2.Frontend/Ev2.Frontend.Dashboard.Shared/Models/WorkRow.cs`
- `/mnt/c/ds/dsev2/solutions/Ev2.Frontend/Ev2.Frontend.Dashboard.Shared/Models/CallRow.cs`
- `/mnt/c/ds/dsev2/solutions/Ev2.Frontend/Ev2.Frontend.Dashboard.Shared/Models/WorkCallGraph.cs`
- `/mnt/c/ds/dsev2/solutions/Ev2.Frontend/Ev2.Frontend.Dashboard.Shared/Models/SignalEventRow.cs`
- `/mnt/c/ds/dsev2/solutions/Ev2.Frontend/Ev2.Frontend.Dashboard.Shared/Models/ApiDefRow.cs`

## 4. 현재 구현의 핵심 결론

현재 3D 기능은 코드상 2개 모드를 갖고 있다.

1. `Work 기반 Scene`
2. `Device + ApiDef 기반 Scene`

그러나 실제 `Facility3D` 페이지는 아래 흐름으로 움직인다.

1. `Three3DView`를 비어 있는 씬으로 초기화한다.
2. `AAS Tree(System -> Flow -> Device)`를 API에서 가져온다.
3. 트리에서 `Device`를 드래그해서 3D 캔버스에 드롭한다.
4. 드롭 좌표를 월드 좌표로 바꿔 `Device`를 씬에 추가한다.
5. 배치 좌표를 `localStorage`에 저장한다.
6. 페이지 재진입 시 저장 좌표를 읽어 다시 배치한다.

즉, 현재 실사용 기준의 핵심은 `Device 배치 엔진`이다.  
`Work 기반 Scene`은 남아 있지만 실제 페이지에서는 초기 장면 생성에 직접 사용되지 않는다.

따라서 Ds2 엔진 이식 우선순위는 아래로 둔다.

1. `DevicePlacementScene`
2. `ApiDef visualization`
3. `WorkGraphScene` 호환 계층

## 5. 현재 동작 시퀀스

## 5.1 페이지 초기화

`Facility3D`는 아래 두 데이터를 별도로 읽는다.

- `AAS Tree`: `api/devices/aas-tree`
- `WorkCallGraph`: `api/insights/graph?workLimit=20`

실제 3D 초기화는 `Three3DView.InitializeEmptySceneAsync()`를 사용한다.  
즉 처음에는 work도 device도 없는 빈 바닥과 카메라만 만든다.

## 5.2 저장 레이아웃 복원

초기 렌더 후 약간의 지연 뒤 아래를 수행한다.

1. `getStoredDevicePositions(elementId)` 호출
2. 저장된 `deviceId -> {x,z}` 좌표 로드
3. 현재 AAS Tree에서 같은 `deviceId`를 찾음
4. `AddDeviceAtPosition(device, x, z)`로 다시 씬에 추가
5. 트리의 `IsPlaced = true`로 반영

## 5.3 개별 드래그 배치

트리에서 Device 드래그 시작 시 `DeviceDragData`를 만든다.  
캔버스 드롭 시 아래가 실행된다.

1. 클라이언트 좌표 `(clientX, clientY)` 획득
2. `screenToWorldPosition()`로 바닥 평면 기준 `(x, z)` 계산
3. `addDeviceAtPosition()`으로 Device group 생성
4. `localStorage`에 좌표 저장
5. 트리의 `IsPlaced = true` 갱신

## 5.4 일괄 배치

`Place All`은 `IsUsedInSimulation == true` 이고 아직 배치되지 않은 Device만 모아 실행한다.

1. Flow별로 Device를 그룹핑
2. 각 Flow 안에서 grid 배치
3. Flow 간에는 `Z`축 offset을 줘 분리
4. 생성된 모든 Device 위치를 저장
5. 트리 상태를 전부 `IsPlaced = true`로 갱신

## 5.5 편집 모드

Edit Mode가 켜지면:

- OrbitControls를 끈다
- 클릭 선택 대신 드래그 편집을 허용한다
- Work 또는 Device group을 XZ 평면에서 drag 할 수 있다
- mouse up 시 좌표를 자동 저장한다

## 5.6 선택 동작

선택 가능한 대상:

- Work station
- Call cube
- Device model
- ApiDef cube
- Empty space

선택 결과:

- Work 선택: 연결된 incoming/outgoing Work 강조
- Call 선택: call chain 시각화
- Device 선택: 신호 패널 표시
- ApiDef 선택: 관련 연결 화살표 표시
- Empty space: 하이라이트 해제

## 6. 현재 아키텍처 분해

## 6.1 `Facility3D.razor`

역할:

- 페이지 조합
- API 로드
- AAS Tree와 Three3DView 연결
- 트리 drag/drop 오케스트레이션
- 저장 좌표 복원

이 파일은 엔진이 아니라 `Scene Host`다.

## 6.2 `AasTreePanel.razor`

역할:

- `System -> Flow -> Device` 트리 표시
- Drag source 제공
- `Place All`, `Clear All`, `Refresh` 버튼 제공

이 파일은 엔진이 아니라 `Placement Source UI`다.

## 6.3 `Three3DView.razor`

역할:

- Blazor <-> JS interop wrapper
- public engine-like 메서드 노출
- JS callback 수신
- 선택 패널 상태 관리
- 주기적 device signal refresh

이 파일은 `엔진 어댑터 + ViewModel`에 가깝다.

## 6.4 `three-interop.js`

역할:

- 실제 Three.js scene 구현
- geometry/model 생성
- camera/control/lighting
- raycast selection
- drag edit
- localStorage 저장
- auto layout
- connection arrow

현재는 사실상 `3D 엔진 전체`가 이 파일 하나에 몰려 있다.

## 6.5 `ds-3d-commons.js`

역할:

- 케이블 스플라인
- 파티클
- 히트맵 플레인

현재 Facility3D 핵심 배치 기능에는 직접 의존도가 낮다.  
`robot welding effect`, `heatmap`, `cable` 같은 확장 기능용 보조 라이브러리다.

## 7. Ds2 엔진에 이식할 기능 단위

Ds2 엔진으로 이식할 때는 아래 6개 모듈로 쪼개야 한다.

## 7.1 Scene Core

책임:

- 씬 생성/파괴
- 카메라/컨트롤/라이트
- ground/grid 생성
- resize 처리

## 7.2 Placement Engine

책임:

- Work auto-layout
- Device auto-layout
- 드래그 수동 배치
- flow zone 계산
- camera fit

## 7.3 Visual Node Builder

책임:

- Work station mesh 생성
- Device station mesh 생성
- ApiDef cube 생성
- 라벨/배지/상태색
- robot/small/general 분기

## 7.4 Interaction Engine

책임:

- raycast hit test
- 클릭 대상 판정
- drag threshold 판단
- hover highlight
- selection state

## 7.5 Persistence Adapter

책임:

- layout load/save
- key 관리
- versioning
- 저장소 교체 가능성 확보

## 7.6 Visualization Overlay Engine

책임:

- Work 연결선
- Call chain arrow
- ApiDef connection arrow
- 선택 하이라이트

## 8. 이식 대상 데이터 계약

Ds2 엔진은 프론트엔드 모델을 그대로 쓰지 말고, 아래 계약으로 재정의해야 한다.

## 8.1 SceneMode

- `Empty`
- `WorkGraph`
- `DevicePlacement`

## 8.2 WorkNode

필수 필드:

- `Id`
- `Name`
- `FlowName`
- `State`
- `Elapsed`
- `Total`
- `Mt`
- `Wt`
- `IncomingWorkIds`
- `OutgoingWorkIds`
- `Calls`

## 8.3 CallNode

필수 필드:

- `Id`
- `WorkId`
- `WorkName`
- `FlowName`
- `State`
- `Progress`
- `DeviceNames`
- `ApiDefName`
- `NextCallId`
- `PrevCallId`
- `IncomingCallIds`
- `OutgoingCallIds`
- `ActiveTrigger`
- `AutoCondition`
- `CommonCondition`
- `HasError`
- `ErrorText`

## 8.4 DeviceNode

필수 필드:

- `Id`
- `Name`
- `DeviceType`
- `FlowName`
- `State`
- `ApiDefs`
- `IsUsedInSimulation`

## 8.5 ApiDefNode

필수 필드:

- `Id`
- `Name`
- `Guid`
- `CallerCount`
- `State`

## 8.6 FlowZone

필수 필드:

- `FlowName`
- `CenterX`
- `CenterZ`
- `SizeX`
- `SizeZ`
- `Color`
- `WorkIds`

## 8.7 LayoutPosition

필수 필드:

- `NodeId`
- `NodeKind`
  - `Work`
  - `Device`
- `X`
- `Y`
- `Z`

## 8.8 SelectionEvent

필수 이벤트 타입:

- `WorkSelected`
- `CallSelected`
- `DeviceSelected`
- `ApiDefSelected`
- `EmptySpaceSelected`

## 9. 현재 소스에서 확인된 입력 모델

## 9.1 AAS Tree 계층

현재 트리는 아래 계층이다.

- `AasSystemNode`
- `AasFlowNode`
- `DeviceTreeNode`

`WorkTreeNode`도 모델에는 있지만, 실제 페이지는 Device drag/drop 위주다.

## 9.2 Device drag 계약

현재 drag payload는 아래다.

- `DeviceId`
- `DeviceName`
- `DeviceType`
- `FlowName`
- `State`
- `ApiDefs`

이 구조는 Ds2 엔진의 `PlaceDevice()` 입력 계약으로 거의 그대로 가져갈 수 있다.

## 9.3 Work graph 계약

현재 work graph는 아래다.

- `Works`
- `Calls`
- `WorkToCalls`
- `CallChains`
- `DistinctFlows`
- `DistinctDevices`

Ds2 엔진은 `GetOrderedCallsForWork()` 수준의 정렬 책임을 내부 utility로 가져가야 한다.

## 10. 자동 배치 알고리즘 명세

## 10.1 Work 기반 자동 배치

현재 C# `CalculateFlowBasedLayout()`는 flow 수에 따라 배치를 다르게 한다.

### 경우 A: Flow 1개

- 정사각형에 가까운 grid
- `worksPerRow = ceil(sqrt(count))`
- spacing = `8.0`
- 결과를 중심 `(0,0)` 기준으로 centering

### 경우 B: Flow 2개

- 2개의 생산 라인을 평행하게 배치
- 각 flow는 `CalculateProductionLineLayout()` 사용
- Flow 0은 `Z = -15`
- Flow 1은 `Z = +15`

`CalculateProductionLineLayout()` 규칙:

- work 수 `<= 6`: 직선 line
- work 수 `> 6`: L-shape
- 이후 전체를 center 정렬

### 경우 C: Flow 3개 이상

- flow 자체를 zone으로 본다
- zone을 grid로 배치
- zoneSize = `25`
- zoneSpacing = `28`
- zone 내부 work는 다시 grid
- workSpacing = `min(8, zoneSize * 0.8 / worksPerRow)`

## 10.2 Device 기반 자동 배치

현재 JS `placeAllDevices()`는 flow 중심 배치다.

규칙:

- Device를 `flowName` 기준으로 그룹
- 각 flow 안에서 grid 배치
- flow별 Z offset 적용
- `spacing = 12`
- `flowSpacing = 20`

계산식:

- `cols = ceil(sqrt(flowDevicesCount))`
- `row = floor(idx / cols)`
- `col = idx % cols`
- `x = (col - (cols - 1)/2) * spacing`
- `z = (row - (rows - 1)/2) * spacing + flowOffsetZ`

즉 Device 모드의 기본 레이아웃은 `flow row + 내부 grid`다.

## 10.3 추가 Layout 타입

JS에는 Device용 추가 layout 함수도 있다.

- `grid`
- `circular`
- `hierarchical`
- `flow`

Ds2 엔진 1차에서는 아래만 필수로 본다.

- `grid`
- `flow-grouped-grid`

아래는 후순위다.

- `circular`
- `hierarchical`
- `flow(rotated hierarchical)`

## 10.4 셔플

`shuffleLayout()`은 현재 임의 target position을 생성하고 1.5초 ease-out cubic 애니메이션으로 이동시킨다.

Ds2 엔진에서는 기능을 유지하되, 이것은 디버그/데모 성격이므로 선택 기능으로 둔다.

## 11. 수동 배치/편집 규칙

## 11.1 드래그 시작

Edit Mode가 켜진 상태에서만 drag를 시작한다.

조건:

- 마우스 좌클릭
- Call cube는 drag 대상에서 제외
- 클릭한 mesh 또는 parent를 따라 올라가며 `workId` 또는 `deviceId`를 찾는다

## 11.2 드래그 시작 임계치

현재 구현은 `5px` 이상 이동해야 click이 아니라 drag로 본다.

이 규칙은 Ds2 엔진에 그대로 유지한다.

## 11.3 드래그 평면

이동은 항상 XZ 평면에서 일어난다.

- plane normal = `(0,1,0)`
- Y는 고정
- `screen -> ray -> plane intersection`으로 이동 좌표 결정

## 11.4 드래그 종료

mouse up 시 아래를 수행한다.

- 이동 종료
- OrbitControls 상태 복원
- cursor 복원
- work/device 좌표 저장

## 12. 좌표 저장/복원 규칙

## 12.1 현재 저장 방식

현재는 `localStorage`를 사용한다.

Work 저장 키:

- `ev2-3d-layout-{elementId}`

Device 저장 키:

- `ev2_device_positions_{elementId}`

## 12.2 저장 포맷

현재 저장 포맷:

- `nodeId -> { x, y?, z }`

Device는 `{x, z}`만 저장하고, Work는 `{x, y, z}`를 저장한다.

## 12.3 Ds2 엔진 이식 기준

Ds2 엔진은 저장소를 추상화해야 한다.

필수 인터페이스:

- `LoadLayout(sceneId, mode) -> LayoutSnapshot`
- `SaveLayout(sceneId, mode, positions)`
- `ClearLayout(sceneId, mode)`

기본 구현:

- 브라우저: `localStorage`
- 데스크탑: file/json
- 서버: optional DB 저장

## 13. 씬 그래픽 규칙

## 13.1 공통 바닥

- plane floor
- grid helper
- dark theme background
- fog 적용

## 13.2 Work scene 시각 요소

- platform
- support pillar
- 대표 device model
- progress bar
- call cube
- device label
- detailed label
- floating AAS icon

## 13.3 Device scene 시각 요소

- device model
- device label
- ApiDef cube
- caller count badge
- optional connection line

## 13.4 대표 장비 타입 분류

현재 소스는 장비 타입을 아래 3개로 본다.

- `robot`
- `small` / `cylinder`
- `general`

분류 방식:

- 명시적 `deviceType`
- 또는 이름 패턴 기반 추론

추론 예:

- `RB`, `ROBOT` -> `robot`
- `CLP`, `CT`, `SV`, `AIR`, `SLIDE`, `LOCK`, `CYLINDER` -> `small`

## 14. 상태/애니메이션 규칙

## 14.1 상태 색

현재 상태값은 `R/G/F/H`를 사용한다.

- `R` -> Ready -> Green
- `G` -> Going -> Yellow
- `F` -> Finish -> Blue
- `H` -> Homing -> Gray

## 14.2 Work 상태 반영

`updateWorkStates()`는 아래를 한다.

- 대표 model emissive 색 변경
- child material 동기화
- robot animation의 상태값 갱신

## 14.3 Robot 애니메이션

현재 로봇 타입은 `G` 상태일 때:

- base rotation
- arm oscillation
- gripper open/close
- welding particle

비 `G` 상태로 돌아오면 초기 pose로 복원한다.

Ds2 엔진에서는 이것을 `RendererEffect` 계층으로 분리한다.  
1차 이식에서 필수는 아니지만 hook은 남겨야 한다.

## 14.4 Device 상태 반영

JS에는 `updateDeviceStates()`가 있지만 현재 Facility3D 실사용 흐름에서는 거의 연결되지 않는다.

Ds2 엔진에서는 이것을 정식 기능으로 승격해야 한다.

필수 API:

- `UpdateDeviceStates(sceneId, stateChanges)`
- `UpdateApiDefStates(sceneId, stateChanges)`

## 15. 선택/시각화 규칙

## 15.1 Work 선택

선택 시:

- Work panel 표시
- 해당 Work emissive 강조
- incoming Work는 cyan 연결선
- outgoing Work는 amber 연결선

## 15.2 Call 선택

선택 시:

- 선택한 Call cube 강조
- `incoming/outgoing` 또는 `prev/next` 기준으로 chain 시각화
- cyan = incoming
- amber = outgoing

## 15.3 Device 선택

선택 시:

- 장비 패널 표시
- `GetDeviceSignalsAsync(deviceName)` 호출
- 300ms 주기로 signal refresh

## 15.4 ApiDef 선택

선택 시:

- ApiDef 패널 표시
- `GetApiDefConnectionsAsync(device, apiDefName)` 호출
- outgoing/incoming arrows 시각화

## 15.5 Empty space 선택

선택 해제, 하이라이트 해제, 패널 닫기를 의미한다.  
현재 JS는 `OnEmptySpaceClicked`를 호출하려 하지만 C# 구현은 없다.

Ds2 엔진 이식 시에는 이것을 정식 이벤트로 정의한다.

## 16. Ds2 엔진 목표 API

아래 API로 재구현하는 것을 권장한다.

## 16.1 Scene Lifecycle

- `CreateScene(sceneId, SceneMode mode, SceneInitData data)`
- `DisposeScene(sceneId)`
- `ResizeScene(sceneId, width, height)`

## 16.2 Placement

- `PlaceDevice(sceneId, DeviceNode node, x, z)`
- `PlaceDevices(sceneId, IReadOnlyList<DeviceNode> nodes, LayoutMode mode)`
- `PlaceWork(sceneId, WorkNode node, x, z)`
- `PlaceWorks(sceneId, IReadOnlyList<WorkNode> nodes, IReadOnlyList<ConnectionEdge> edges, LayoutMode mode)`
- `ClearDevices(sceneId)`
- `ClearWorks(sceneId)`

## 16.3 Interaction

- `ScreenToWorld(sceneId, clientX, clientY) -> (x, z)`
- `SetEditMode(sceneId, bool enabled)`
- `ShuffleLayout(sceneId)`
- `SelectAtScreen(sceneId, clientX, clientY)`

## 16.4 State Update

- `UpdateWorkStates(sceneId, IReadOnlyList<StateChange>)`
- `UpdateDeviceStates(sceneId, IReadOnlyList<StateChange>)`
- `UpdateApiDefStates(sceneId, IReadOnlyList<StateChange>)`

## 16.5 Persistence

- `LoadStoredLayout(sceneId, SceneMode mode)`
- `SaveCurrentLayout(sceneId, SceneMode mode)`
- `ResetStoredLayout(sceneId, SceneMode mode)`

## 16.6 Visualization

- `ShowWorkConnections(sceneId, workId)`
- `ShowCallChain(sceneId, callId)`
- `ShowApiDefConnections(sceneId, apiDefId, deviceId, outgoing, incoming)`
- `ClearAllVisualizations(sceneId)`

## 16.7 Events

- `SelectionChanged`
- `LayoutChanged`
- `EditModeChanged`
- `StateUpdated`
- `SceneInitialized`

## 17. Renderer 독립 구조

Ds2 엔진은 Three.js 구현과 직접 결합하면 안 된다.  
아래 3층 구조로 가야 한다.

### 17.1 Ds2.ThreeD.Core

순수 엔진 계층.

책임:

- layout 계산
- selection state
- persistence contract
- scene graph description

### 17.2 Ds2.ThreeD.Renderer

렌더러 종속 계층.

예:

- Three.js renderer
- Unity/Omniverse bridge
- WPF 3D renderer

### 17.3 Ds2.ThreeD.UIAdapter

UI 프레임워크 종속 계층.

예:

- Blazor adapter
- WPF adapter
- Electron adapter

현재 `Three3DView.razor + three-interop.js`는 이 3층이 섞여 있다.  
이식에서는 반드시 분리해야 한다.

## 18. 마이그레이션 우선순위

## Phase 1

- `DevicePlacementScene`
- `PlaceDevice`
- `PlaceAllDevices`
- `ScreenToWorld`
- `localStorage/file persistence`
- `Device / ApiDef selection`

## Phase 2

- `ApiDef connection arrows`
- `Edit mode drag`
- `camera fit`
- `flow-grouped auto layout`

## Phase 3

- `WorkGraphScene`
- `Call cube`
- `Work connections`
- `Call chain visualization`

## Phase 4

- robot animation
- particle effect
- ds-3d-commons 재통합

## 19. 이식 시 정리해야 할 현재 결함/정리 항목

현재 소스에는 Ds2 엔진 이식 전에 정리해야 할 점이 있다.

1. `Three3DView`는 Work mode와 Device mode가 한 파일에 섞여 있다.
2. `three-interop.js`가 과도하게 비대하며 scene/render/layout/selection/persistence가 한 객체에 몰려 있다.
3. `screenToWorldPosition` 함수가 JS에 중복 정의되어 있다.
4. `OnEmptySpaceClicked`는 JS에서 호출하지만 C# 쪽 구현이 없다.
5. Device state update 경로는 존재하지만 현재 페이지에서 정식으로 연결되지 않는다.
6. localStorage key가 하드코딩되어 있어 scene/version 분리가 약하다.

Ds2 엔진 구현에서는 이 상태를 그대로 옮기면 안 된다.

## 20. 최종 결론

이번 이식의 본질은 `Three.js 코드를 복사`하는 것이 아니다.  
핵심은 아래를 `Ds2 엔진 계약`으로 다시 세우는 것이다.

- 배치 대상 모델
- 자동 배치 규칙
- 수동 이동 규칙
- 선택/시각화 이벤트
- 저장/복원 규약

즉 최종 목표는 아래 한 줄로 정리된다.

`Ev2 Dashboard의 3D 배치 동작을, UI와 렌더러에 독립적인 Ds2 3D Layout Engine으로 재정의한다.`
