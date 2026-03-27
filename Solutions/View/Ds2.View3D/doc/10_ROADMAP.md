# Ds2 3D View — 10. 로드맵 및 확장 계획

---

## 1. Phase 현황

| Phase | 내용 | 상태 |
|-------|------|------|
| **Phase 1** | DevicePlacement 씬, 자동 배치, 시뮬 상태 반영, 컨텍스트 메뉴 | ✅ 완료 |
| **Phase 2** | 선택 이벤트 → PropertyPanel 연동, ApiDef 연결 화살표 | 계획 |
| **Phase 3** | WorkGraph 씬, Call 큐브, Work 연결선 | 계획 |
| **Phase 4** | 로봇 파티클 고도화, ds-3d-commons 재통합 | 선택 |

---

## 2. Phase 2 — 선택 & ApiDef 연동

### 2.1 선택 이벤트 처리

현재 `ThreeDViewState.OnSelectionMessage()`는 Debug.WriteLine만 수행.

구현 목표:
```csharp
public void OnSelectionMessage(string method, JsonElement[] args)
{
    switch (method)
    {
        case "OnDeviceSelected":
            var deviceId = Guid.Parse(args[0].GetString()!);
            var state = _engine!.Select(SelectionEvent.NewDeviceSelected(deviceId));
            // → PropertyPanel.ShowDevicePanel(deviceId) 호출
            break;

        case "OnApiDefSelected":
            var devId = Guid.Parse(args[0].GetString()!);
            var apiName = args[1].GetString()!;
            _engine!.Select(SelectionEvent.NewApiDefSelected(devId, apiName));
            break;

        case "OnEmptySpaceSelected":
            _engine!.ClearSelection();
            break;
    }
}
```

### 2.2 ApiDef 연결 화살표

JS 쪽 `showApiDefConnections(elementId, deviceId, apiDefName, outgoing, incoming)` 이미 존재.
C#에서 호출 메시지 타입 추가:

```json
{ "type": "showApiDefConnections",
  "deviceId": "...", "apiDefName": "WELD",
  "outgoing": [...], "incoming": [...] }
```

### 2.3 장비 신호 패널

선택된 Device의 `ApiDef` 목록을 PropertyPanel에 표시.
`SimulationPanelState`의 기존 패널 또는 새 패널 추가.

---

## 3. Phase 3 — WorkGraph 씬

### 3.1 씬 모드 전환

현재 `SceneEngine.BuildWorkScene()` 이미 구현됨.
`ThreeDViewState`에 `BuildWorkScene(store, flowId)` 메서드 추가 필요.

### 3.2 Work 스테이션

`_createStation()` 이미 `three-interop.js`에 구현.
`init` config의 `works` 배열에 데이터 전달.

### 3.3 Work 연결선

`_createConnections()` 이미 구현됨.
`init` config의 `connections` 배열에 데이터 전달.

### 3.4 메시지 타입 추가 필요

```
{ "type": "addWork", "work": {...}, "x": ..., "z": ... }
{ "type": "updateWorkStates", "states": [...] }
```

---

## 4. 개선 항목

### 4.1 레이아웃 영속성 (InMemory → File)

현재 `InMemoryLayoutStore`는 앱 재시작 시 초기화됨.

구현 목표: `JsonFileLayoutStore`
```csharp
public class JsonFileLayoutStore : ILayoutStore
{
    // .sdf 파일 경로 기반 .3dlayout.json 파일에 저장
    // sceneId + mode로 분리
}
```

### 4.2 다중 Call → Device 상태 병합

동일 Device를 참조하는 여러 Call이 동시에 다른 상태일 때,
현재는 마지막 수신 상태가 표시됨.

우선순위 정책 (제안):
```
Going > Homing > Finish > Ready
```

```csharp
// ThreeDViewState에 deviceState 캐시 추가
private readonly Dictionary<Guid, Status4> _deviceStateCache = new();
```

### 4.3 씬 재빌드 (파일 다시 열기 시)

현재 파일을 다시 열면 `ThreeD.Reset()`이 호출되지만
3D 창이 열려있으면 씬이 빈 상태로 유지됨.

개선: `Reset()` 후 `_view3DWindow`가 열려있으면 자동 재빌드.

```csharp
// MainViewModel.cs 파일 열기 처리에서
if (_view3DWindow is { IsVisible: true })
{
    _ = Simulation.ThreeD.BuildScene(_store, projectId);
}
```

### 4.4 진행 표시

대형 프로젝트(500+ Device)에서 `addDevice` 메시지 수백 개 전송 시 지연 발생.
해결책:
- 일괄 전송 메시지 타입 추가: `{ "type": "addDevices", "devices": [...] }`
- 진행 상태 오버레이 표시

---

## 5. 알려진 제한 사항

| 항목 | 현상 | 원인 |
|------|------|------|
| 앱 재시작 시 배치 위치 초기화 | Device 위치가 매번 재계산됨 | `InMemoryLayoutStore` 사용 |
| 시뮬 정지 후 색 유지 | Going 색이 정지 후에도 노란색으로 남음 | `Reset()` 시 JS에 reset 메시지 미전송 |
| 창 다시 열면 씬 재빌드 필요 | 3D 창 닫고 다시 열어야 함 | `_view3DWindow` 재생성으로 해결 |

---

## 6. 확장 포인트

### 6.1 다른 렌더러

현재 구조에서 Layer 2(JS/Three.js)를 다른 렌더러로 교체 가능:
- **Unity WebGL**: `facility3d.html` + JS 대신 Unity WebGL 빌드 사용
- **Babylon.js**: three-interop.js 대신 Babylon.js 구현

Layer 1(F# Core)과 Layer 3(C# Adapter) 코드 변경 불필요.

### 6.2 Blazor 지원

`ThreeDViewState` 역할을 하는 Blazor 어댑터 구현:
- `IJSRuntime.InvokeVoidAsync("handleFromCSharp", ...)` 사용
- `wpf-dotnet-polyfill.js` 불필요 (Blazor DotNet.invokeMethodAsync 직접 사용)

### 6.3 카메라 프리셋

```javascript
// 추가할 메시지 타입
{ "type": "setCameraPreset", "preset": "top" | "front" | "iso" }
```

### 6.4 디바이스 타입 커스터마이징

현재 `robot/small/general` 3가지.
추가 타입 (`conveyor`, `press`, `grinder`) 지원 시:
- `ContextBuilder.fs`의 `inferDeviceType` 함수 확장
- `three-interop.js`의 `_createDeviceStation`에 분기 추가

---

## 7. 테스트 체크리스트

| 항목 | 방법 |
|------|------|
| 씬 로드 확인 | `.sdf` 열기 → [3D] 클릭 → Device 박스 표시 |
| 시뮬 상태 반영 | 시뮬 시작 → Going Device 노란색 확인 |
| 로봇 애니메이션 | Robot 타입 Device Going 상태 → 관절 움직임 |
| 카메라 맞춤 | `F` 키 또는 컨텍스트 메뉴 → 전체 Device 화면에 맞춤 |
| 재배치 | 컨텍스트 메뉴 → 전체 재배치 → Flow 그룹별 격자 배치 |
| 위치 초기화 | 컨텍스트 메뉴 → 저장 위치 초기화 → 자동 재배치 |
| 창 재열기 | 3D 창 닫기 → [3D] 다시 클릭 → 씬 재빌드 |
| 대형 파일 | 500+ Device 프로젝트 → 모든 Device 바닥 안에 배치 |
