# 이벤트 처리 파이프라인

## 🎯 목적

PLC 이벤트를 수신하여 Projection 테이블(dspFlow, dspCall)을 실시간으로 업데이트하는 파이프라인 설계입니다.

---

## 📐 전체 파이프라인

```
┌─────────────────────────────────────────────────────────────┐
│                   Event Processing Pipeline                  │
├─────────────────────────────────────────────────────────────┤
│                                                               │
│  1. Event Source (이벤트 소스)                                │
│  ┌──────────────────────────────────────┐                   │
│  │  PLC Tag Value Change                │                   │
│  │  { TagName, Value, Timestamp }       │                   │
│  └──────────────┬───────────────────────┘                   │
│                 │                                             │
│                 ▼                                             │
│  2. Edge Detection (엣지 감지)                                │
│  ┌──────────────────────────────────────┐                   │
│  │  TagStateTracker                     │                   │
│  │  - Detect Rising Edge                │                   │
│  │  - Detect Falling Edge               │                   │
│  └──────────────┬───────────────────────┘                   │
│                 │                                             │
│                 ▼                                             │
│  3. Tag to Call Mapping (태그 → 콜 매핑)                     │
│  ┌──────────────────────────────────────┐                   │
│  │  PlcToCallMapper                     │                   │
│  │  - FindCallByTag                     │                   │
│  │  - Identify InTag vs OutTag          │                   │
│  └──────────────┬───────────────────────┘                   │
│                 │                                             │
│                 ├──────────────┬────────────────┐            │
│                 ▼              ▼                ▼            │
│  4. State Transition (상태 전이)                              │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐      │
│  │  InTag       │  │  OutTag      │  │  Error       │      │
│  │  Rising      │  │  Rising      │  │  Handler     │      │
│  └──────┬───────┘  └──────┬───────┘  └──────┬───────┘      │
│         │                  │                  │              │
│         ▼                  ▼                  ▼              │
│  ┌───────────────────────────────────────────────┐          │
│  │  Projection Update (Projection 업데이트)      │          │
│  │  - Call State Update                          │          │
│  │  - Flow State Update                          │          │
│  │  - Statistics Calculation                     │          │
│  └───────────────────┬───────────────────────────┘          │
│                      │                                       │
│                      ▼                                       │
│  ┌──────────────────────────────────────┐                   │
│  │  dspFlow / dspCall                   │                   │
│  │  (Projection Tables)                 │                   │
│  └──────────────────────────────────────┘                   │
│                                                               │
└─────────────────────────────────────────────────────────────┘
```

---

## 🔄 Stage 별 상세 설명

### Stage 1: Event Source (이벤트 소스)

**입력**: PLC Tag 값 변경
**출력**: PlcCommunicationEvent

```csharp
// DSPilot/Models/PlcCommunicationEvent.cs

public class PlcCommunicationEvent
{
    public string PlcName { get; set; }
    public DateTime Timestamp { get; set; }
    public List<PlcTagData> Tags { get; set; }
}

public class PlcTagData
{
    public string Address { get; set; }
    public bool Value { get; set; }
}
```

**이벤트 발생 경로**:
1. **실시간 PLC 통신** (Ev2PlcEventSource + IPlcEventSource)
   ```csharp
   // DSPilot/Services/Ev2PlcEventSource.cs
   // Ev2.Backend.PLC 라이브러리를 사용하여 실제 PLC 연결

   public class Ev2PlcEventSource : IPlcEventSource
   {
       private readonly IObservable<PlcCommunicationEvent> _events;

       public IObservable<PlcCommunicationEvent> Events => _events;

       public async Task StartAsync(CancellationToken cancellationToken)
       {
           // EV2 PLC 연결 및 스캔 시작
           await _plcService.ConnectAsync();
           await _plcService.StartScanAsync(_scanIntervalMs);
       }

       // PLC 스캔 결과를 IObservable 스트림으로 발행
   }
   ```

2. **이력 재생** (SqlitePlcHistorySource)
   ```csharp
   // DSPilot/Services/SqlitePlcHistorySource.cs
   // plcTagLog 테이블에서 과거 이벤트 읽기

   public class SqlitePlcHistorySource : IPlcHistorySource
   {
       public async Task<List<PlcTagLogEntity>> GetLogsAsync(
           DateTime from,
           DateTime to)
       {
           return await _plcRepository.GetPlcTagLogsAsync(from, to);
       }
   }
   ```

3. **데이터베이스 모니터링** (PlcDatabaseMonitorService)
   ```csharp
   // DSPilot/Services/PlcDatabaseMonitorService.cs
   // DB 변경사항 폴링 및 SignalR 브로드캐스트

   protected override async Task ExecuteAsync(CancellationToken stoppingToken)
   {
       while (!stoppingToken.IsCancellationRequested)
       {
           var snapshot = await _dspDbService.GetSnapshotAsync();
           await _hubContext.Clients.All.SendAsync(
               "ReceiveDatabaseUpdate",
               snapshot);
           await Task.Delay(1000);  // 1초마다 폴링
       }
   }
   ```

---

### Stage 2: Edge Detection (엣지 감지)

**입력**: PlcCommunicationEvent (Tag Address + Value)
**출력**: EdgeState (EdgeType)

```fsharp
// DSPilot.Engine/Core/EdgeDetection.fs

type EdgeType =
    | NoChange
    | RisingEdge
    | FallingEdge

type EdgeState =
    { TagAddress: string
      PreviousValue: string option
      CurrentValue: string
      EdgeType: EdgeType }
```

**C# Service 래퍼** (PlcTagStateTrackerService):
```csharp
// DSPilot/Services/PlcTagStateTrackerService.cs

public class PlcTagStateTrackerService
{
    private readonly TagStateTracker _tracker = new TagStateTracker();

    public EdgeState UpdateTagValue(string tagAddress, string value)
    {
        return _tracker.UpdateTagValue(tagAddress, value);
    }
}
```

**F# 구현** (TagStateTracker):
```fsharp
// DSPilot.Engine/Tracking/TagStateTracker.fs

type TagStateTracker() =
    let mutable tagStates = Map.empty<string, string>

    member this.UpdateTagValue(tagAddress: string, currentValue: string) : EdgeState =
        let prevValue = Map.tryFind tagAddress tagStates
        tagStates <- Map.add tagAddress currentValue tagStates

        let edgeType =
            match prevValue with
            | Some prev when prev <> currentValue ->
                if prev = "0" && currentValue = "1" then RisingEdge
                elif prev = "1" && currentValue = "0" then FallingEdge
                else NoChange
            | _ -> NoChange

        { TagAddress = tagAddress
          PreviousValue = prevValue
          CurrentValue = currentValue
          EdgeType = edgeType }
```

**사용 예시** (PlcEventProcessorService에서):
```csharp
foreach (var tagData in plcEvent.Tags)
{
    // Edge 감지
    var edgeState = _tagStateTracker.UpdateTagValue(
        tagData.Address,
        tagData.Value ? "1" : "0");

    if (edgeState.EdgeType == EdgeType.RisingEdge)
    {
        // Rising Edge만 처리
        await ProcessRisingEdgeAsync(tagData.Address, plcEvent.Timestamp);
    }
}
```

**주의사항**:
- 현재는 Rising Edge만 사용 (Falling Edge는 무시)
- 상태 추적을 위해 이전 값을 메모리에 저장 (Map<string, string>)
- string 값으로 저장하여 "0"/"1" 외의 값도 처리 가능

---

### Stage 3: Tag to Call Mapping (태그 → 콜 매핑)

**입력**: Tag Address (Rising Edge 감지됨)
**출력**: CallMappingInfo (옵션)

```csharp
// DSPilot/Models/CallMappingInfo.cs

public class CallMappingInfo
{
    public Call Call { get; set; }  // AASX Call 객체
    public string TagAddress { get; set; }
    public bool IsInTag { get; set; }  // true: InTag, false: OutTag
}
```

**C# Service** (PlcToCallMapperService):
```csharp
// DSPilot/Services/PlcToCallMapperService.cs

public class PlcToCallMapperService
{
    private readonly DsProjectService _projectService;
    private Dictionary<string, CallMappingInfo> _tagToCallMap;

    // 프로젝트 로드 시 호출: AASX에서 Tag 매핑 추출
    public void Initialize()
    {
        _tagToCallMap = new Dictionary<string, CallMappingInfo>();

        var systems = _projectService.GetActiveSystems();
        foreach (var system in systems)
        {
            foreach (var flow in _projectService.GetFlows(system.Id))
            {
                foreach (var work in _projectService.GetWorks(flow.Id))
                {
                    foreach (var call in _projectService.GetCalls(work.Id))
                    {
                        // AASX Call.Args에서 InTag/OutTag 추출
                        var inTag = ExtractInTag(call);
                        var outTag = ExtractOutTag(call);

                        if (!string.IsNullOrEmpty(inTag))
                        {
                            _tagToCallMap[inTag] = new CallMappingInfo
                            {
                                Call = call,
                                TagAddress = inTag,
                                IsInTag = true
                            };
                        }

                        if (!string.IsNullOrEmpty(outTag))
                        {
                            _tagToCallMap[outTag] = new CallMappingInfo
                            {
                                Call = call,
                                TagAddress = outTag,
                                IsInTag = false
                            };
                        }
                    }
                }
            }
        }

        _logger.LogInformation(
            "PlcToCallMapper initialized with {Count} tag mappings",
            _tagToCallMap.Count);
    }

    // PLC 이벤트 처리 시 호출: Tag Address → CallMappingInfo
    public CallMappingInfo? FindCallByTag(string plcName, string tagAddress)
    {
        _tagToCallMap.TryGetValue(tagAddress, out var mapping);
        return mapping;
    }

    // Call ID로 Tag 조회 (Cycle Analysis 등에서 사용)
    public (string InTag, string OutTag)? GetCallTagsByCallId(Guid callId)
    {
        var mappings = _tagToCallMap.Values
            .Where(m => m.Call.Id == callId)
            .ToList();

        var inTag = mappings.FirstOrDefault(m => m.IsInTag)?.TagAddress ?? "";
        var outTag = mappings.FirstOrDefault(m => !m.IsInTag)?.TagAddress ?? "";

        return (inTag, outTag);
    }
}
```

**Unmapped Tag 처리**:
```csharp
// PlcEventProcessorService에서
var mapping = _callMapper.FindCallByTag("", tagData.Address);
if (mapping == null)
{
    _logger.LogTrace("No Call mapping for tag: {TagAddress}", tagData.Address);
    continue;  // 이벤트 무시
}

// 정상 처리
await ProcessMappedEventAsync(mapping, plcEvent.Timestamp);
```

**주의사항**:
- 매핑은 메모리에만 존재 (Dictionary<string, CallMappingInfo>)
- 프로젝트 로드 시 Initialize() 호출 필수
- DB에는 Tag 매핑 정보 미저장 (AASX가 Source of Truth)

---

### Stage 4: State Transition (상태 전이)

**입력**: CallMappingInfo, EdgeState, Timestamp
**출력**: Database Update (F# Async)

**C# → F# 호출 흐름**:
```csharp
// DSPilot/Services/PlcEventProcessorService.cs

private async Task ProcessPlcEventAsync(PlcCommunicationEvent plcEvent, CancellationToken cancellationToken)
{
    foreach (var tagData in plcEvent.Tags)
    {
        // 1. Edge 감지
        var edgeState = _tagStateTracker.UpdateTagValue(tagData.Address, tagData.Value ? "1" : "0");

        if (edgeState.EdgeType != EdgeType.RisingEdge && edgeState.EdgeType != EdgeType.FallingEdge)
            continue;

        // 2. Call 매핑
        var mapping = _callMapper.FindCallByTag("", tagData.Address);
        if (mapping == null)
            continue;

        // 3. F# StateTransition 호출
        try
        {
            var dbPath = _pathResolver.GetDspDbPath();

            var asyncOp = StateTransition.processEdgeEvent(
                dbPath,
                tagData.Address,
                mapping.IsInTag,
                edgeState.EdgeType,
                DateTime.Now,
                mapping.Call.Name
            );

            await FSharpAsync.StartAsTask(asyncOp, null, cancellationToken);

            _logger.LogInformation(
                "State transition triggered: Call={CallName}, Tag={TagAddress}, IsInTag={IsInTag}, Edge={EdgeType}",
                mapping.Call.Name, tagData.Address, mapping.IsInTag, edgeState.EdgeType);

            // 4. 상태 변경 알림 발송 (SignalR)
            _notificationService.NotifyStateChanged(
                mapping.Call.Name,
                "unknown",
                "transitioned",
                DateTime.Now
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process state transition for Call: {CallName}", mapping.Call.Name);
        }
    }
}
```

#### 4.1 F# StateTransition 모듈

```fsharp
// DSPilot.Engine/Tracking/StateTransition.fs

module StateTransition =
    open System
    open DSPilot.Engine.Core

    /// PLC Edge 이벤트 처리 (C#에서 호출)
    let processEdgeEvent
        (dbPath: string)
        (tagAddress: string)
        (isInTag: bool)
        (edgeType: EdgeType)
        (timestamp: DateTime)
        (callName: string) : Async<unit> =
        async {
            try
                // InTag Rising Edge: Call 시작
                if isInTag && edgeType = EdgeType.RisingEdge then
                    do! handleInTagRisingEdge dbPath callName timestamp

                // OutTag Rising Edge: Call 종료
                elif not isInTag && edgeType = EdgeType.RisingEdge then
                    do! handleOutTagRisingEdge dbPath callName timestamp

                else
                    () // Falling Edge는 무시
            with ex ->
                printfn "StateTransition error: %s" ex.Message
        }

    /// InTag Rising Edge 처리: Ready → Going
    let private handleInTagRisingEdge (dbPath: string) (callName: string) (timestamp: DateTime) =
        async {
            // TODO: DB 업데이트 구현
            // - Call.State = "Going"
            // - Call.LastStartAt = timestamp
            // - Flow.ActiveCallCount += 1
            printfn "Call started: %s at %s" callName (timestamp.ToString("HH:mm:ss.fff"))
        }

    /// OutTag Rising Edge 처리: Going → Done
    let private handleOutTagRisingEdge (dbPath: string) (callName: string) (timestamp: DateTime) =
        async {
            // TODO: DB 업데이트 구현
            // - Call.State = "Done" (or "Ready")
            // - Call.LastFinishAt = timestamp
            // - Call.LastDurationMs = finishTime - startTime
            // - Flow.ActiveCallCount -= 1
            // - 통계 업데이트
            printfn "Call finished: %s at %s" callName (timestamp.ToString("HH:mm:ss.fff"))
        }
```

**주의사항**:
- 현재는 기본 구조만 구현되어 있음 (로그만 출력)
- 실제 DB 업데이트 로직은 향후 구현 필요
- 통계 계산 (AverageGoingTime, StdDev 등)도 미구현

#### 4.2 OutTag Rising Edge

```fsharp
let handleOutTagRisingEdge (call: DspCallEntity) (timestamp: DateTime) =
    async {
        // 1. Duration 계산
        let duration =
            match call.LastStartAt with
            | Some startAt -> (timestamp - startAt).TotalMilliseconds
            | None ->
                Logger.error $"OutTag without LastStartAt: {call.CallName}"
                0.0

        // 2. Call State 업데이트: Going → Done
        let callPatch =
            { CallId = call.CallId
              State = Some "Done"
              LastFinishAt = Some timestamp
              LastDurationMs = Some duration
              ProgressRate = Some 1.0 }

        do! DspRepository.patchCall callPatch

        // 3. 통계 업데이트
        do! StatisticsCalculator.updateCallStatistics call.CallId duration

        // 4. Flow ActiveCallCount 감소
        let! flow = DspRepository.getFlowByName call.FlowName
        let newActiveCount = max 0 (flow.ActiveCallCount - 1)
        let newFlowState = if newActiveCount = 0 then "Ready" else "Going"

        let flowPatch =
            { FlowName = call.FlowName
              ActiveCallCount = Some newActiveCount
              State = Some newFlowState }

        do! DspRepository.patchFlow flowPatch

        // 5. Tail Call이면 Cycle 완료 처리
        if call.IsTail then
            do! handleCycleComplete call.FlowName timestamp
    }
```

#### 4.3 Cycle Complete 처리

```fsharp
let handleCycleComplete (flowName: string) (timestamp: DateTime) =
    async {
        // 1. Flow 전체 Call 조회
        let! calls = DspRepository.getCallsByFlow flowName
        let! flow = DspRepository.getFlowByName flowName

        // 2. MT, WT, CT 계산
        let mt = calls |> Seq.sumBy (fun c -> c.LastDurationMs |> Option.defaultValue 0.0)

        let headStart = calls |> Seq.tryFind (fun c -> c.IsHead) |> Option.bind (fun c -> c.LastStartAt)
        let ct =
            match headStart, flow.LastCycleStartAt with
            | Some start, _ -> (timestamp - start).TotalMilliseconds
            | None, Some cycleStart -> (timestamp - cycleStart).TotalMilliseconds
            | _ -> 0.0

        let wt = ct - mt

        // 3. Cycle Statistics 업데이트
        let n = flow.CompletedCycleCount + 1
        let oldAvgCT = flow.AverageCT |> Option.defaultValue 0.0
        let newAvgCT = (oldAvgCT * float(n - 1) + ct) / float(n)

        // StdDev 계산 (Welford's method)
        let oldStdDevCT = flow.StdDevCT |> Option.defaultValue 0.0
        let oldM2 = oldStdDevCT * oldStdDevCT * float(n - 1)
        let delta = ct - oldAvgCT
        let newAvgAdjusted = oldAvgCT + delta / float(n)
        let delta2 = ct - newAvgAdjusted
        let newM2 = oldM2 + delta * delta2
        let newStdDevCT = sqrt(newM2 / float(n))

        // Min/Max
        let newMinCT = min (flow.MinCT |> Option.defaultValue ct) ct
        let newMaxCT = max (flow.MaxCT |> Option.defaultValue ct) ct

        // SlowCycleFlag 판정
        let slowCycleFlag = ct > (newAvgCT + 2.0 * newStdDevCT)

        // 4. Projection Update
        let patch =
            { FlowName = flowName
              LastCycleEndAt = Some timestamp
              MT = Some mt
              WT = Some wt
              CT = Some ct
              LastCycleDurationMs = Some ct
              AverageCT = Some newAvgCT
              StdDevCT = Some newStdDevCT
              MinCT = Some newMinCT
              MaxCT = Some newMaxCT
              CompletedCycleCount = Some n
              SlowCycleFlag = Some slowCycleFlag }

        do! DspRepository.patchFlow patch
    }
```

---

## ⚙️ 에러 처리

### Error State 전이

```fsharp
let handleError (callId: Guid) (errorText: string) =
    async {
        let! call = DspRepository.getCallById callId

        // 1. Call Error State
        let callPatch =
            { CallId = callId
              State = Some "Error"
              ErrorText = Some errorText
              ErrorCount = Some (call.ErrorCount + 1)
              FocusScore = Some 100  // Error는 최우선
            }

        do! DspRepository.patchCall callPatch

        // 2. Flow Error State
        let! flow = DspRepository.getFlowByName call.FlowName
        let flowPatch =
            { FlowName = call.FlowName
              ErrorCallCount = Some (flow.ErrorCallCount + 1)
              State = Some "Error" }

        do! DspRepository.patchFlow flowPatch
    }
```

---

## 🔧 최적화 전략

### 1. Batch Update (미래 개선)

현재: 이벤트마다 개별 UPDATE
개선: 일정 시간(예: 100ms) 동안 Patch 누적 후 일괄 UPDATE

```fsharp
type PatchBatcher() =
    let mutable callPatches = Map.empty<Guid, CallPatch>
    let mutable flowPatches = Map.empty<string, FlowPatch>

    member this.AddCallPatch(patch: CallPatch) =
        // Merge patches
        callPatches <- Map.add patch.CallId (mergePatch callPatches.[patch.CallId] patch) callPatches

    member this.FlushAsync() =
        async {
            // Batch execute all patches
            for patch in callPatches.Values do
                do! DspRepository.patchCall patch
            for patch in flowPatches.Values do
                do! DspRepository.patchFlow patch

            callPatches <- Map.empty
            flowPatches <- Map.empty
        }
```

### 2. Async Processing

```fsharp
// Fire-and-forget for non-critical updates
let handleEventAsync (event: PlcTagEvent) =
    async {
        // Critical path: State transition
        do! handleStateTransition event

        // Non-critical: Statistics update (async)
        Async.Start (updateStatisticsAsync event)
    }
```

---

## 📊 성능 지표

### Latency 목표
- Event → Edge Detection: < 5ms
- Edge Detection → Mapping: < 10ms
- Mapping → State Transition: < 20ms
- State Transition → DB Update: < 50ms
- **Total: < 100ms**

### Throughput 목표
- PLC Events: > 100 events/sec
- Rising Edge Events: > 50 edges/sec
- Projection Updates: > 50 updates/sec

---

## 📚 관련 문서

- [01_ARCHITECTURE.md](./01_ARCHITECTURE.md) - 전체 아키텍처
- [05_UPDATE_RULES.md](./05_UPDATE_RULES.md) - 업데이트 규칙 상세
- [06_AGGREGATION.md](./06_AGGREGATION.md) - 집계 계산
- [07_STATISTICS.md](./07_STATISTICS.md) - 통계 계산
