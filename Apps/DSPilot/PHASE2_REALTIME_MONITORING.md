# Phase 2: 실시간 모니터링 대시보드

**우선순위**: 상  
**예상 기간**: 3-4일  
**목표**: SignalR 기반 실시간 PLC 태그 모니터링 페이지 구축

---

## 📋 작업 개요

SignalR Hub를 구현하여 서버에서 클라이언트로 실시간 데이터를 푸시하고, PLC 태그 상태와 Call 상태를 실시간으로 시각화하는 모니터링 페이지를 생성합니다.

---

## 🎯 작업 항목

### 1. SignalR 설정 및 Hub 구현

#### 1.1. NuGet 패키지 추가
**파일**: `DSPilot/DSPilot.csproj`

```xml
<PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="9.0.0" />
```

#### 1.2. SignalR Hub 생성
**새 파일**: `DSPilot/Hubs/MonitoringHub.cs`

```csharp
using Microsoft.AspNetCore.SignalR;

namespace DSPilot.Hubs;

/// <summary>
/// 실시간 모니터링 SignalR Hub
/// </summary>
public class MonitoringHub : Hub
{
    private readonly ILogger<MonitoringHub> _logger;

    public MonitoringHub(ILogger<MonitoringHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// 특정 Flow 구독
    /// </summary>
    public async Task SubscribeToFlow(string flowName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"Flow_{flowName}");
        _logger.LogInformation("Client {ConnectionId} subscribed to Flow: {FlowName}", 
            Context.ConnectionId, flowName);
    }

    /// <summary>
    /// Flow 구독 해제
    /// </summary>
    public async Task UnsubscribeFromFlow(string flowName)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"Flow_{flowName}");
    }
}
```

#### 1.3. Program.cs에 SignalR 등록
**파일**: `DSPilot/Program.cs`

```csharp
// Services 등록 부분에 추가
builder.Services.AddSignalR();

// Middleware 설정 부분에 추가 (app.MapRazorComponents 전에)
app.MapHub<MonitoringHub>("/hubs/monitoring");
```

### 2. 실시간 브로드캐스트 서비스

**새 파일**: `DSPilot/Services/MonitoringBroadcastService.cs`

```csharp
using Microsoft.AspNetCore.SignalR;
using DSPilot.Hubs;
using System.Reactive.Linq;

namespace DSPilot.Services;

/// <summary>
/// 모니터링 데이터 브로드캐스트 서비스
/// CallStateNotificationService를 구독하여 SignalR로 전송
/// </summary>
public class MonitoringBroadcastService : IHostedService, IDisposable
{
    private readonly IHubContext<MonitoringHub> _hubContext;
    private readonly CallStateNotificationService _stateNotification;
    private readonly ILogger<MonitoringBroadcastService> _logger;
    private IDisposable? _stateSubscription;

    public MonitoringBroadcastService(
        IHubContext<MonitoringHub> hubContext,
        CallStateNotificationService stateNotification,
        ILogger<MonitoringBroadcastService> logger)
    {
        _hubContext = hubContext;
        _stateNotification = stateNotification;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MonitoringBroadcastService starting...");

        // Call 상태 변경 구독
        _stateSubscription = _stateNotification.StateChanges
            .Subscribe(async stateChange =>
            {
                try
                {
                    // 모든 클라이언트에게 브로드캐스트
                    await _hubContext.Clients.All.SendAsync(
                        "CallStateChanged",
                        new
                        {
                            stateChange.CallName,
                            stateChange.OldState,
                            stateChange.NewState,
                            stateChange.Timestamp
                        },
                        cancellationToken);

                    _logger.LogDebug("Broadcasted state change: {CallName} {OldState} → {NewState}",
                        stateChange.CallName, stateChange.OldState, stateChange.NewState);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to broadcast state change");
                }
            });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stateSubscription?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _stateSubscription?.Dispose();
    }
}
```

**Program.cs에 등록**:
```csharp
builder.Services.AddHostedService<MonitoringBroadcastService>();
```

### 3. Real-time Monitor 페이지 생성

**새 파일**: `DSPilot/Components/Pages/RealtimeMonitor.razor`

```razor
@page "/realtime"
@using Microsoft.AspNetCore.SignalR.Client
@inject NavigationManager Navigation
@inject DsProjectService ProjectService
@inject PlcToCallMapperService MapperService
@implements IAsyncDisposable
@rendermode InteractiveServer

<PageTitle>실시간 모니터 - DSPilot</PageTitle>

<div class="realtime-monitor-page">
    <h5 class="typo-h5 mb-4" style="font-weight: 600; color: #0A1E5A;">실시간 모니터</h5>

    @* 연결 상태 *@
    <section class="card mb-3">
        <div class="card-content">
            <div style="display:flex; align-items:center; gap:12px;">
                <span class="material-icons" style="color:@(_connectionState == "Connected" ? "var(--color-success)" : "var(--color-error)")">
                    @(_connectionState == "Connected" ? "wifi" : "wifi_off")
                </span>
                <span class="typo-h6">연결 상태: @_connectionState</span>
                @if (_connectionState == "Connected")
                {
                    <span class="chip" style="background:var(--color-success);color:white;">실시간</span>
                }
            </div>
        </div>
    </section>

    @* Call 상태 실시간 표시 *@
    <section class="card">
        <div class="card-header">
            <span class="material-icons mr-2">dynamic_feed</span>
            <h6 class="typo-h6">Call 상태 변화</h6>
        </div>
        <div class="card-content">
            <div class="realtime-state-grid">
                @foreach (var item in _recentStateChanges.TakeLast(10))
                {
                    <div class="state-change-item" style="animation: fadeIn 0.3s ease;">
                        <span class="state-change-time">@item.Timestamp.ToString("HH:mm:ss.fff")</span>
                        <span class="state-change-call">@item.CallName</span>
                        <div class="state-transition">
                            <span class="state-badge state-@item.OldState.ToLower()">@item.OldState</span>
                            <span class="material-icons">arrow_forward</span>
                            <span class="state-badge state-@item.NewState.ToLower()">@item.NewState</span>
                        </div>
                    </div>
                }
            </div>
        </div>
    </section>
</div>

@code {
    private HubConnection? _hubConnection;
    private string _connectionState = "Disconnected";
    private List<CallStateChangeItem> _recentStateChanges = new();

    protected override async Task OnInitializedAsync()
    {
        // SignalR 연결 설정
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(Navigation.ToAbsoluteUri("/hubs/monitoring"))
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<CallStateChangedData>("CallStateChanged", OnCallStateChanged);

        _hubConnection.Reconnecting += error =>
        {
            _connectionState = "Reconnecting";
            InvokeAsync(StateHasChanged);
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += connectionId =>
        {
            _connectionState = "Connected";
            InvokeAsync(StateHasChanged);
            return Task.CompletedTask;
        };

        _hubConnection.Closed += error =>
        {
            _connectionState = "Disconnected";
            InvokeAsync(StateHasChanged);
            return Task.CompletedTask;
        };

        await _hubConnection.StartAsync();
        _connectionState = "Connected";
    }

    private void OnCallStateChanged(CallStateChangedData data)
    {
        _recentStateChanges.Add(new CallStateChangeItem
        {
            CallName = data.CallName,
            OldState = data.OldState,
            NewState = data.NewState,
            Timestamp = data.Timestamp
        });

        // 최대 100개까지만 유지
        if (_recentStateChanges.Count > 100)
        {
            _recentStateChanges.RemoveAt(0);
        }

        InvokeAsync(StateHasChanged);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection is not null)
        {
            await _hubConnection.DisposeAsync();
        }
    }

    private class CallStateChangedData
    {
        public string CallName { get; set; } = "";
        public string OldState { get; set; } = "";
        public string NewState { get; set; } = "";
        public DateTime Timestamp { get; set; }
    }

    private class CallStateChangeItem
    {
        public string CallName { get; set; } = "";
        public string OldState { get; set; } = "";
        public string NewState { get; set; } = "";
        public DateTime Timestamp { get; set; }
    }
}
```

### 4. CSS 스타일 추가

**파일**: `DSPilot/wwwroot/css/components.css` (끝에 추가)

```css
/* Real-time Monitor 스타일 */
.realtime-monitor-page {
    padding: 20px;
}

.realtime-state-grid {
    display: flex;
    flex-direction: column;
    gap: 8px;
}

.state-change-item {
    display: grid;
    grid-template-columns: 100px 200px 1fr;
    align-items: center;
    padding: 12px;
    background: var(--color-background);
    border-radius: 8px;
    gap: 16px;
}

.state-change-time {
    font-size: 12px;
    color: var(--color-text-secondary);
    font-family: 'Courier New', monospace;
}

.state-change-call {
    font-weight: 600;
    color: var(--color-primary);
}

.state-transition {
    display: flex;
    align-items: center;
    gap: 12px;
}

.state-badge {
    padding: 4px 12px;
    border-radius: 12px;
    font-size: 11px;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.5px;
}

.state-badge.state-ready {
    background: #E3F2FD;
    color: #1976D2;
}

.state-badge.state-going {
    background: #FFF3E0;
    color: #F57C00;
}

.state-badge.state-finish {
    background: #E8F5E9;
    color: #388E3C;
}

@keyframes fadeIn {
    from {
        opacity: 0;
        transform: translateY(-10px);
    }
    to {
        opacity: 1;
        transform: translateY(0);
    }
}
```

### 5. 네비게이션 메뉴에 추가

**파일**: `DSPilot/Components/Layout/NavMenu.razor`

```razor
<NavLink class="nav-link" href="realtime" Match="NavLinkMatch.Prefix">
    <span class="material-icons">wifi_tethering</span>
    실시간 모니터
</NavLink>
```

---

## 🧪 테스트 계획

### 1. SignalR 연결 테스트
- [ ] 페이지 로드 시 자동 연결 확인
- [ ] 재연결 동작 확인
- [ ] 다중 클라이언트 연결 확인

### 2. 실시간 업데이트 테스트
- [ ] PLC 태그 변경 시 즉시 반영 확인
- [ ] Call 상태 변화 애니메이션 확인
- [ ] 100개 이상 이벤트 발생 시 오래된 항목 제거 확인

### 3. 성능 테스트
- [ ] 100ms 간격으로 데이터 수신 시 UI 반응성 확인
- [ ] 메모리 누수 확인
- [ ] 네트워크 대역폭 사용량 확인

---

## 📊 검증 방법

### 1. 브라우저 개발자 도구
```javascript
// Console에서 SignalR 연결 확인
// Network 탭에서 WebSocket 연결 확인
```

### 2. 로그 확인
```bash
[INFO] Client connected: {ConnectionId}
[DEBUG] Broadcasted state change: Call1 Ready → Going
```

### 3. 실제 동작 확인
- Real-time Monitor 페이지 접속
- PLC 태그 값 변경
- 1초 이내에 UI 업데이트 확인

---

## 📝 구현 체크리스트

- [ ] SignalR NuGet 패키지 추가
- [ ] MonitoringHub.cs 생성
- [ ] MonitoringBroadcastService.cs 생성
- [ ] RealtimeMonitor.razor 페이지 생성
- [ ] CSS 스타일 추가
- [ ] 네비게이션 메뉴 업데이트
- [ ] Program.cs 설정
- [ ] 빌드 성공 확인
- [ ] SignalR 연결 테스트
- [ ] 실시간 업데이트 동작 확인

---

## 🚀 다음 단계

Phase 2 완료 후:
- `PHASE3_DASHBOARD_WIDGETS.md` 진행
- Dashboard에 실시간 통계 위젯 추가
