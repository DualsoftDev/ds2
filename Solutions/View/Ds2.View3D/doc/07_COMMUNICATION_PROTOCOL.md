# Ds2 3D View — 07. C#↔JS 통신 프로토콜

---

## 1. 통신 방향 및 수단

```
C# (WPF)                          JavaScript (WebView2)
─────────────────────────────────────────────────────
ThreeDViewState._sendToWebView     facility3d.html
  │                                  handleFromCSharp(msg)
  └─→ ExecuteScriptAsync ─────────────────────────────→ Ev23DViewer.*
        "handleFromCSharp({json})"

  ←─ WebMessageReceived ←───────── window.chrome.webview.postMessage
        e.TryGetWebMessageAsString()   wpf-dotnet-polyfill.js
```

---

## 2. C# → JS 메시지 타입

모든 메시지는 `{ type: string, ... }` 형식의 JSON 객체.
`ThreeDViewState.SendAsync(payload)`가 직렬화 후 전송.

---

### `init`

씬 초기화. 반드시 첫 번째로 전송.

```json
{
  "type": "init",
  "config": {
    "flowZones": [
      {
        "flowName": "FLOW_A",
        "centerX": 22.0,
        "centerZ": 0.0,
        "sizeX": 38.0,
        "sizeZ": 18.0,
        "color": 4360085
      }
    ],
    "floorSize": 200.0
  }
}
```

`color`는 `#RRGGBB` 파싱 결과 정수값 (`ParseColorToInt()`).

---

### `addDevice`

Device 1개를 씬에 배치. `init` 이후 Device 수만큼 반복 전송.

```json
{
  "type": "addDevice",
  "device": {
    "id": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
    "name": "WELD_RB1",
    "flowName": "FLOW_A",
    "deviceType": "robot",
    "state": "R",
    "isUsedInSimulation": true,
    "apiDefs": [
      { "id": "...", "name": "WELD", "callerCount": 3 }
    ]
  },
  "x": 12.0,
  "z": 0.0
}
```

`deviceType`: `"robot"` | `"small"` | `"general"`

---

### `updateWorkStates`

시뮬레이션 Work 상태 변경 반영.

```json
{
  "type": "updateWorkStates",
  "states": [
    { "id": "xxxxxxxx-...", "state": "G" }
  ]
}
```

`state`: `"R"` | `"G"` | `"F"` | `"H"`

---

### `updateDeviceStates`

시뮬레이션 Device 상태 변경 반영 (Call → Device 역매핑 결과).

```json
{
  "type": "updateDeviceStates",
  "states": [
    { "id": "xxxxxxxx-...", "state": "G" }
  ]
}
```

---

### `fitAll`

모든 Device 배치 완료 후 카메라 자동 맞춤. 페이로드 없음.

```json
{ "type": "fitAll" }
```

---

## 3. JS → C# 메시지 (선택 콜백)

### 원리

`three-interop.js`는 원래 Blazor `DotNet.invokeMethodAsync()`를 사용.
WPF에서는 `wpf-dotnet-polyfill.js`가 이를 `window.chrome.webview.postMessage()`로 대체.

```javascript
// wpf-dotnet-polyfill.js
window._wpfCallbackRef = {
    invokeMethodAsync: function(methodName, ...args) {
        window.chrome.webview.postMessage(JSON.stringify({
            method: methodName, args: args
        }));
        return Promise.resolve(null);
    }
};
```

---

### 콜백 등록

`facility3d.html`의 `init` 처리:
```javascript
Ev23DViewer.setCallSelectionCallback(SCENE_ID, window._wpfCallbackRef);
```

---

### 전송 메시지 형식

```json
{
  "method": "OnCallSelected",
  "args": ["callId-guid-string"]
}
```

C#의 `View3DWindow.OnWebMessageReceived()`가 파싱:
```csharp
var method = doc.RootElement.GetProperty("method").GetString();
var args = doc.RootElement.GetProperty("args").EnumerateArray().ToArray();
_vm.OnSelectionMessage(method, args);
```

현재 `OnSelectionMessage()`는 Debug.WriteLine만 수행 (Phase 2 확장 예정).

---

## 4. ExecuteScriptAsync 에러 처리

`ThreeDViewState.SendAsync()`:
```csharp
private async Task SendAsync(object payload)
{
    if (_sendToWebView == null) return;
    try {
        var json = JsonSerializer.Serialize(payload);
        await _sendToWebView(json);
    }
    catch { /* WebView 준비 안 된 경우 무시 */ }
}
```

`View3DWindow`의 전송 델리게이트:
```csharp
_vm.SetWebViewSender(async json =>
{
    if (WebView3D.CoreWebView2 != null)
        await WebView3D.CoreWebView2.ExecuteScriptAsync($"handleFromCSharp({json})");
});
```

---

## 5. 메시지 처리 순서 제약

```
init   → 반드시 첫 번째
addDevice (n개)  → init 이후
fitAll → addDevice 전부 완료 후

updateWorkStates / updateDeviceStates
  → HasScene = true 이후 언제든지
  → OnWorkStateChanged / OnCallStateChanged에서 fire-and-forget
```

`facility3d.html`의 `initialized` 플래그:
```javascript
var initialized = false;

function handleFromCSharp(msg) {
    if (msg.type === 'init') {
        ...
        initialized = true;
    } else if (!initialized) {
        return;  // init 전 메시지 무시
    }
    ...
}
```

---

## 6. WPF 타이밍 이슈 해결

**문제**: `Open3DView()`에서 `Show()` 즉시 `BuildScene()` 호출 시 WebView2 미준비.

**해결**: `NavigationCompleted` 이벤트 후 콜백 실행:
```csharp
// View3DWindow.xaml.cs
private async void OnNavigationCompleted(...)
{
    WebView3D.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
    if (_onReady != null)
        await _onReady();  // ← BuildScene() 호출
}
```

**순서**:
```
View3DWindow.Show()
  → OnWindowLoaded
    → EnsureCoreWebView2Async
      → SetVirtualHostName + WebMessageReceived + SetWebViewSender
        → Navigate to facility3d.html
          → (페이지 로드 완료)
            → OnNavigationCompleted
              → BuildScene()
                → init / addDevice 메시지들 전송
```
