# Ds2 3D View — 08. 시뮬레이션 이벤트 연동

---

## 1. 이벤트 흐름 전체

```
SimulationEngine (F# EventDrivenEngine)
    │ WorkStateChanged event
    │ CallStateChanged event
    ↓
SimulationPanelState.Events.cs
    │ _dispatcher.BeginInvoke (UI thread)
    ↓
OnWorkStateChanged(args)          OnCallStateChanged(args)
    │ args.WorkGuid, args.NewState     │ args.CallGuid, args.NewState
    │                                  │
    ↓                                  ↓
ThreeDViewState.OnWorkStateChanged   ThreeDViewState.OnCallStateChanged
    │                                  │
    │                                  ├─ _callToDevice[callId] → deviceId
    │                                  │
    ↓                                  ↓
SendAsync({ type:"updateWorkStates"}) SendAsync({ type:"updateDeviceStates"})
    ↓                                  ↓
ExecuteScriptAsync                 ExecuteScriptAsync
    ↓                                  ↓
Ev23DViewer.updateWorkStates()     Ev23DViewer.updateDeviceStates()
    ↓                                  ↓
stationMeshes[id] traverse          deviceMeshes[id] traverse
emissive color 변경                  emissive color + state 변경
                                     로봇 애니메이션 활성화/비활성화
```

---

## 2. Call → Device 역매핑

시뮬레이션은 `CallStateChanged`를 발생시키지만 3D 씬에는 Device가 배치되어 있다.
`ThreeDViewState.BuildScene()`에서 매핑 구성:

```csharp
// DeviceNode.Name → DeviceNode.Id
var deviceNameToId = scene.DeviceNodes
    .ToDictionary(d => d.Name, d => d.Id, StringComparer.OrdinalIgnoreCase);

// Call.DevicesAlias → deviceId
foreach (var kv in store.Calls)
{
    var callId = kv.Key;
    var call = kv.Value;
    if (!string.IsNullOrEmpty(call.DevicesAlias) &&
        deviceNameToId.TryGetValue(call.DevicesAlias, out var deviceId))
    {
        _callToDevice[callId] = deviceId;
    }
}
```

**`Call.DevicesAlias`**: DsStore의 Call 엔티티가 가지는 장비 이름 문자열.
대소문자 무시 비교(`OrdinalIgnoreCase`).

**매핑 누락 케이스**:
- `DevicesAlias`가 빈 문자열인 Call (장비 미연결 동작)
- DeviceNode 목록에 없는 이름 → 3D에서 상태 업데이트 없음 (정상)

---

## 3. Status4 → 상태 코드 변환

```csharp
// ThreeDViewState.cs
private static string ToStateCode(Status4 s) => s switch
{
    Status4.Going  => "G",
    Status4.Finish => "F",
    Status4.Homing => "H",
    _              => "R"   // Ready 포함 기본값
};
```

| Status4 | 코드 | 3D 색 | 로봇 애니메이션 |
|---------|------|--------|----------------|
| Ready   | R | `0x10b981` Green | 정지 |
| Going   | G | `0xeab308` Yellow | 관절 회전 + 용접 파티클 |
| Finish  | F | `0x3b82f6` Blue | 정지 |
| Homing  | H | `0x6b7280` Gray | 정지 |

---

## 4. updateDeviceStates JS 구현 (수정 후)

로봇(Group 타입) 처리를 위해 traverse 사용:

```javascript
updateDeviceStates: function(elementId, deviceStates) {
    const sceneData = this._scenes[elementId];
    if (!sceneData) return;

    deviceStates.forEach(({ id, state }) => {
        const model = sceneData.deviceMeshes[id];
        if (!model) return;
        const color = this.stateColors[state] || this.stateColors.R;

        // deviceData.state 갱신 → 애니메이션 루프 반영
        const device = sceneData.devices.find(d => d.id === id);
        if (device) device.state = state;
        if (model.userData?.deviceData) model.userData.deviceData.state = state;

        // 모든 자식 Mesh 색 업데이트
        model.traverse(child => {
            if (child.userData?.deviceData) child.userData.deviceData.state = state;
            if (child.isMesh && child.material?.emissive !== undefined) {
                child.material.color.setHex(color.hex);
                child.material.emissive.setHex(color.hex);
                child.material.emissiveIntensity = state === 'G' ? 0.5 : 0.3;
            }
        });
    });
}
```

---

## 5. HasScene 가드

`OnWorkStateChanged`, `OnCallStateChanged` 둘 다:

```csharp
if (_sendToWebView == null || !HasScene) return;
```

- `_sendToWebView == null`: 창이 닫혀 있거나 아직 초기화 안 됨
- `!HasScene`: `BuildScene()`이 아직 완료되지 않음

---

## 6. 시뮬레이션 시작/정지 생명주기

### 시작
```
StartSimulation()
  → SimIndex 재빌드
  → WorkStateChanged / CallStateChanged 이벤트 시작
  → ThreeD가 HasScene=true이면 실시간 반영
```

### 정지
```
StopSimulation()
  └─ ThreeD.Reset()
       ├─ _engine = null
       ├─ _callToDevice.Clear()
       └─ HasScene = false
```

**주의**: 시뮬레이션 정지 시 3D 씬 자체는 초기화되지 않음.
다음 시뮬레이션 시작 전에 3D 버튼을 다시 눌러야 씬이 재빌드됨.

---

## 7. 이벤트 스레드 안전성

`SimulationPanelState.Events.cs`:

```csharp
_simEngine.WorkStateChanged += (_, args) =>
    _dispatcher.BeginInvoke(() => OnWorkStateChanged(args));

_simEngine.CallStateChanged += (_, args) =>
    _dispatcher.BeginInvoke(() => OnCallStateChanged(args));
```

`_dispatcher.BeginInvoke`로 UI 스레드에서 처리.
`ThreeDViewState.SendAsync()`는 `async Task`이므로 fire-and-forget (`_ = SendAsync(...)`) 처리.

WebView2 `ExecuteScriptAsync`는 내부적으로 스레드 안전.

---

## 8. 다중 Call → 동일 Device

동일 Device에 여러 Call이 매핑될 경우 (여러 Work에서 같은 Device 사용):
- 각 `CallStateChanged` 이벤트마다 독립적으로 `updateDeviceStates` 전송
- 마지막으로 수신된 상태가 최종 색으로 표시됨
- 현재 "최고 우선순위 상태 병합" 로직 없음 → Phase 2 검토
