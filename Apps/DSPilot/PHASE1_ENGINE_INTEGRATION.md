# Phase 1: DSPilot.Engine 핵심 통합

**우선순위**: 최상  
**예상 기간**: 2-3일  
**목표**: F# StateTransition 로직을 활용한 실시간 Call 상태 관리

---

## 📋 작업 개요

PlcEventProcessorService를 활성화하고 DSPilot.Engine의 StateTransition.fs를 완전히 통합하여 Direction 기반 자동 상태 전이를 구현합니다.

---

## 🎯 작업 항목

### 1. PlcEventProcessorService 활성화 및 리팩토링

**파일**: `DSPilot/Services/PlcEventProcessorService.cs`

**현재 상태**:
- Program.cs에서 주석 처리됨 (line 59-80)
- Channel 기반 백프레셔 구현됨
- StateTransition 로직 미사용

**변경 사항**:

#### 1.1. StateTransition 통합
```csharp
// 추가할 using
using DSPilot.Engine.Tracking;
using DSPilot.Engine.Core;

// ProcessPlcEventAsync 메서드 수정
private async Task ProcessPlcEventAsync(PlcCommunicationEvent plcEvent, CancellationToken cancellationToken)
{
    foreach (var tag in plcEvent.Tags)
    {
        // 1. TagStateTracker로 Edge 감지
        var edgeState = _tagStateTracker.UpdateTagValue(tag.Address, tag.Value);
        
        if (edgeState.EdgeType == EdgeType.RisingEdge || 
            edgeState.EdgeType == EdgeType.FallingEdge)
        {
            _logger.LogDebug("Edge detected: {TagAddress} = {Value}, EdgeType = {EdgeType}",
                tag.Address, tag.Value, edgeState.EdgeType);
            
            // 2. Call 매핑 조회
            var mapping = _callMapper.FindCallByTag(tag.Name, tag.Address);
            if (mapping == null)
            {
                _logger.LogTrace("No Call mapping for tag: {TagAddress}", tag.Address);
                continue;
            }
            
            // 3. F# StateTransition 호출
            var dbPath = _pathResolver.GetDatabasePaths().DspDbPath;
            await StateTransition.processEdgeEvent(
                dbPath,
                tag.Address,
                mapping.IsInTag,
                edgeState.EdgeType,
                DateTime.Now,
                mapping.Call.Name
            );
            
            _logger.LogInformation(
                "State transition triggered: Call={CallName}, Tag={TagAddress}, IsInTag={IsInTag}, Edge={EdgeType}",
                mapping.Call.Name, tag.Address, mapping.IsInTag, edgeState.EdgeType);
        }
    }
}
```

#### 1.2. 필요한 의존성 추가
```csharp
public PlcEventProcessorService(
    ILogger<PlcEventProcessorService> logger,
    IPlcEventSource plcEventSource,
    PlcToCallMapperService callMapper,
    PlcTagStateTrackerService tagStateTracker,  // 추가
    IDatabasePathResolver pathResolver,          // 추가
    InMemoryCallStateStore stateStore,
    CallStatisticsService statisticsService,
    IServiceScopeFactory scopeFactory)
{
    _logger = logger;
    _plcEventSource = plcEventSource;
    _callMapper = callMapper;
    _tagStateTracker = tagStateTracker;  // 추가
    _pathResolver = pathResolver;         // 추가
    _stateStore = stateStore;
    _statisticsService = statisticsService;
    _scopeFactory = scopeFactory;
    
    // ... Channel 설정
}
```

### 2. Program.cs에서 PlcEventProcessorService 활성화

**파일**: `DSPilot/Program.cs`

**변경 사항**:

```csharp
// Line 59-80 주석 해제 및 수정
var plcConnectionEnabled = builder.Configuration.GetValue<bool>("PlcConnection:Enabled");
if (plcConnectionEnabled)
{
    // PLC 연결 설정
    var plcConfig = new PlcConnectionConfig
    {
        PlcName = builder.Configuration["PlcConnection:PlcName"] ?? "PLC_01",
        IpAddress = builder.Configuration["PlcConnection:IpAddress"] ?? "192.168.0.100",
        ScanIntervalMs = ResolveScanIntervalMs(builder.Configuration, "PlcConnection:ScanIntervalMs", defaultEv2ScanIntervalMs),
        TagAddresses = builder.Configuration.GetSection("PlcConnection:TagAddresses").Get<List<string>>() ?? new List<string>()
    };

    builder.Services.AddSingleton(plcConfig);
    builder.Services.AddSingleton<IPlcEventSource, Ev2PlcEventSource>();
    builder.Services.AddHostedService<PlcEventProcessorService>();

    builder.Logging.AddConsole();
    builder.Logging.SetMinimumLevel(LogLevel.Debug);
}
```

### 3. appsettings.json 설정

**파일**: `DSPilot/appsettings.json`

**변경 사항**:

```json
{
  "PlcConnection": {
    "Enabled": true,  // false에서 true로 변경
    "PlcName": "MitsubishiPLC",
    "IpAddress": "192.168.9.120",
    "ScanIntervalMs": 100,
    "TagAddresses": []  // 빈 배열 = 모든 태그 (AASX에서 자동 추출)
  }
}
```

### 4. StateTransition 결과 실시간 반영

**새 파일**: `DSPilot/Services/CallStateNotificationService.cs`

**목적**: StateTransition 결과를 클라이언트에 실시간 전달

```csharp
using System.Reactive.Subjects;

namespace DSPilot.Services;

/// <summary>
/// Call 상태 변경 알림 서비스
/// StateTransition 결과를 구독자에게 전달
/// </summary>
public class CallStateNotificationService
{
    private readonly Subject<CallStateChangedEvent> _stateChanges = new();
    
    public IObservable<CallStateChangedEvent> StateChanges => _stateChanges;
    
    public void NotifyStateChanged(string callName, string oldState, string newState, DateTime timestamp)
    {
        _stateChanges.OnNext(new CallStateChangedEvent
        {
            CallName = callName,
            OldState = oldState,
            NewState = newState,
            Timestamp = timestamp
        });
    }
}

public class CallStateChangedEvent
{
    public string CallName { get; set; } = "";
    public string OldState { get; set; } = "";
    public string NewState { get; set; } = "";
    public DateTime Timestamp { get; set; }
}
```

**Program.cs에 등록**:
```csharp
builder.Services.AddSingleton<CallStateNotificationService>();
```

### 5. PlcEventProcessorService에서 알림 발송

```csharp
// ProcessPlcEventAsync 메서드에 추가
private readonly CallStateNotificationService _notificationService;

// StateTransition 호출 후
await StateTransition.processEdgeEvent(...);

// 상태 변경 알림
using (var scope = _scopeFactory.CreateScope())
{
    var repo = scope.ServiceProvider.GetRequiredService<IDspRepository>();
    var callData = await repo.GetCallByNameAsync(mapping.Call.Name);
    if (callData != null)
    {
        _notificationService.NotifyStateChanged(
            mapping.Call.Name,
            oldState: "unknown", // TODO: 이전 상태 추적
            newState: callData.State,
            timestamp: DateTime.Now
        );
    }
}
```

---

## 🧪 테스트 계획

### 1. 단위 테스트
- [ ] TagStateTracker Edge Detection 테스트
- [ ] PlcToCallMapper 매핑 테스트
- [ ] StateTransition 각 Direction 테스트

### 2. 통합 테스트
- [ ] PlcEventProcessorService 전체 플로우 테스트
- [ ] DB 상태 변경 확인 테스트
- [ ] 알림 발송 테스트

### 3. 실제 PLC 테스트
- [ ] Mitsubishi PLC 연결 확인
- [ ] 태그 변경 시 상태 전이 확인
- [ ] 통계 수집 확인

---

## 📊 검증 방법

### 1. 로그 확인
```bash
# PlcEventProcessorService 시작 로그
[INFO] PlcEventProcessorService starting...
[INFO] CallMapper initialized: 199 tag mappings

# Edge Detection 로그
[DEBUG] Edge detected: TagAddress=D100, Value=1, EdgeType=RisingEdge

# StateTransition 로그
[INFO] State transition triggered: Call=Call1, Tag=D100, IsInTag=False, Edge=RisingEdge
```

### 2. 데이터베이스 확인
```sql
-- dspCall 테이블에서 State 변경 확인
SELECT CallName, State, LastStartAt, LastFinishAt, CycleCount 
FROM dspCall 
WHERE CallName = 'Call1';

-- 통계 확인
SELECT CallName, MT, WT, CT 
FROM dspFlow 
WHERE FlowName = 'Flow1';
```

### 3. FlowWorkspace 페이지 확인
- Call 테이블에서 State 컬럼 실시간 업데이트 확인
- CycleCount 증가 확인
- LastStartAt, LastFinishAt 타임스탬프 확인

---

## ⚠️ 주의사항

### 1. 데이터베이스 동시성
- StateTransition은 DB를 직접 업데이트합니다
- PlcDataReaderService와의 읽기/쓰기 충돌 주의
- SQLite BusyTimeout 설정 확인 (appsettings.json)

### 2. 성능 고려사항
- 100ms 스캔 간격에서 태그 199개 처리
- Edge Detection은 메모리 기반으로 빠름
- DB 업데이트는 비동기로 처리

### 3. 에러 처리
- PLC 연결 끊김 시 재연결 로직 필요
- DB 업데이트 실패 시 재시도 로직
- 예외 발생 시 서비스 중단 방지

---

## 📝 구현 체크리스트

- [ ] PlcEventProcessorService.cs 수정
- [ ] Program.cs 주석 해제 및 수정
- [ ] appsettings.json 설정 변경
- [ ] CallStateNotificationService.cs 생성
- [ ] 의존성 주입 설정
- [ ] 빌드 성공 확인
- [ ] 단위 테스트 작성 및 실행
- [ ] 실제 PLC 연결 테스트
- [ ] FlowWorkspace 페이지 동작 확인
- [ ] 로그 확인 및 검증

---

## 🚀 다음 단계

Phase 1 완료 후:
- `PHASE2_REALTIME_MONITORING.md` 진행
- SignalR을 통한 실시간 클라이언트 업데이트 구현
