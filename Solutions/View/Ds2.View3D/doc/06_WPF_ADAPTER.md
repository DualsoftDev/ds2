# Ds2 3D View — 06. WPF 어댑터 레이어

**파일**:
- `Apps/Promaker/Promaker/ViewModels/Simulation/ThreeDViewState.cs`
- `Apps/Promaker/Promaker/Windows/View3DWindow.xaml(.cs)`
- `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.cs`

---

## 1. ThreeDViewState

`ObservableObject`를 상속하는 Layer 3 어댑터 ViewModel.
`SimulationPanelState`가 `ThreeD` 프로퍼티로 보유.

### 주요 필드

| 필드 | 타입 | 설명 |
|------|------|------|
| `_engine` | `SceneEngine?` | Layer 1 코어 엔진 |
| `_sendToWebView` | `Func<string,Task>?` | C# → JS 전송 델리게이트 |
| `_callToDevice` | `Dictionary<Guid,Guid>` | callId → deviceId 역매핑 |
| `HasScene` | `bool` (Observable) | 씬 빌드 완료 여부 |

### SetWebViewSender

```csharp
public void SetWebViewSender(Func<string, Task>? sender)
```
`View3DWindow.OnWindowLoaded()`에서 WebView2 초기화 완료 후 주입.
창 닫힐 때 `null`로 해제.

### BuildScene

```csharp
public async Task BuildScene(DsStore store, Guid projectId)
```

실행 순서:
1. `SceneEngine.BuildDeviceScene("promaker", projectId)` → SceneData
2. `callId → deviceId` 역매핑 구성 (`store.Calls[id].DevicesAlias` → `device.Name`)
3. `floorSize` 계산 (positions bounding box + 40 여유, 최소 100)
4. `{ type:"init", config:{flowZones, floorSize} }` 전송
5. 각 DeviceNode에 대해 `{ type:"addDevice", device:{...}, x, z }` 전송
6. `{ type:"fitAll" }` 전송 → 카메라 자동 맞춤
7. `HasScene = true`

### OnWorkStateChanged / OnCallStateChanged

```csharp
public void OnWorkStateChanged(Guid workId, Status4 newState)
public void OnCallStateChanged(Guid callId, Status4 newState)
```

시뮬레이션 이벤트 수신 → JSON 메시지 비동기 전송.
`HasScene = false`이거나 `_sendToWebView = null`이면 무시.

`OnCallStateChanged`: `_callToDevice`로 역매핑 후 `updateDeviceStates` 전송.

### Reset

```csharp
public void Reset()
```
`StopSimulation()` 시 호출. `_engine`, `_callToDevice` 초기화, `HasScene = false`.

### OnSelectionMessage

```csharp
public void OnSelectionMessage(string method, JsonElement[] args)
```
JS 클릭 이벤트 수신. 현재 Debug.WriteLine만 수행 (Phase 2 확장 예정).

---

## 2. View3DWindow

WebView2를 호스팅하는 WPF Window.

### 생성자

```csharp
public View3DWindow(ThreeDViewState vm, Func<Task>? onReady = null)
```
`onReady` 콜백은 페이지 로드 완료(`NavigationCompleted`) 후 실행.

### 초기화 순서 (`OnWindowLoaded`)

```
await WebView3D.EnsureCoreWebView2Async()
    ↓
SetVirtualHostNameToFolderMapping("promaker.app", wwwroot, Allow)
    ↓
WebMessageReceived += OnWebMessageReceived
    ↓
SetWebViewSender(json => ExecuteScriptAsync("handleFromCSharp({json})"))
    ↓
NavigationCompleted += OnNavigationCompleted
    ↓
WebView3D.Source = "https://promaker.app/facility3d.html"
    ↓
(페이지 로드 완료 후)
NavigationCompleted -= OnNavigationCompleted
    ↓
await onReady()   ← ThreeDViewState.BuildScene() 호출
```

### 가상 호스트 매핑

```csharp
WebView3D.CoreWebView2.SetVirtualHostNameToFolderMapping(
    "promaker.app",
    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot"),
    CoreWebView2HostResourceAccessKind.Allow);
```

`https://promaker.app/facility3d.html` → 실제 파일: `bin/Debug/.../wwwroot/facility3d.html`

### WebMessageReceived

JS → C# 방향 (선택 이벤트):
```csharp
private void OnWebMessageReceived(...)
{
    var raw = e.TryGetWebMessageAsString();
    var doc = JsonDocument.Parse(raw);
    var method = doc.RootElement.GetProperty("method").GetString();
    var args = doc.RootElement.GetProperty("args").EnumerateArray().ToArray();
    _vm.OnSelectionMessage(method, args);
}
```

### OnWindowClosed

```csharp
WebView3D.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
_vm.SetWebViewSender(null);
```

---

## 3. MainViewModel 연동

### Open3DViewCommand

```csharp
[RelayCommand(CanExecute = nameof(HasProject))]
private void Open3DView()
{
    // 이미 열려있으면 활성화
    if (_view3DWindow is { IsVisible: true })
    {
        _view3DWindow.Activate();
        return;
    }

    var store = _store;
    var projectId = DsQuery.allProjects(_store).Head.Id;
    _view3DWindow = new View3DWindow(Simulation.ThreeD,
        onReady: () => Simulation.ThreeD.BuildScene(store, projectId));
    _view3DWindow.Owner = Application.Current.MainWindow;
    _view3DWindow.Show();
}
```

**CanExecute**: `HasProject = true`일 때만 활성화.
프로젝트 없으면 버튼 비활성화.

### 툴바 버튼 (`MainToolbarEtcContent.xaml`)

```xml
<Button Style="{StaticResource RibbonFlatButton}"
        Command="{Binding Open3DViewCommand}"
        ToolTip="3D 배치 뷰 열기">
    <StackPanel Orientation="Vertical">
        <iconPacks:PackIconMaterial Kind="CubeOutline" Width="18" Height="18"/>
        <TextBlock Text="3D"/>
    </StackPanel>
</Button>
```

위치: 기타(EtcContent) 섹션, 유틸 메뉴 버튼과 테마 버튼 사이.

---

## 4. SimulationPanelState 연동

```csharp
// SimulationPanelState.cs
public ThreeDViewState ThreeD { get; } = new();

// SimulationPanelState.Events.cs
private void OnWorkStateChanged(WorkStateChangedArgs args) {
    ...
    ThreeD.OnWorkStateChanged(args.WorkGuid, args.NewState);
}

private void OnCallStateChanged(CallStateChangedArgs args) {
    ...
    ThreeD.OnCallStateChanged(args.CallGuid, args.NewState);
}

// SimulationPanelState.Lifecycle.cs
private void StopSimulation() {
    ...
    ThreeD.Reset();
}
```

---

## 5. wwwroot 배포 설정 (`Promaker.csproj`)

```xml
<Content Include="wwwroot\**\*" CopyToOutputDirectory="PreserveNewest" />
```

빌드 시 `bin/Debug/.../wwwroot/`에 복사.
WebView2 가상 호스트가 이 폴더를 읽음.

파일 목록:
```
wwwroot/
  facility3d.html
  js/
    three-interop.js
    ds-3d-commons.js
    wpf-dotnet-polyfill.js
```
