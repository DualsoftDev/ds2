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
**출력**: PlcTagEvent

```fsharp
// DSPilot.Engine/Core/Types.fs

type PlcTagEvent =
    { TagName: string
      Value: bool
      Timestamp: DateTime
      Source: string }
```

**이벤트 발생 경로**:
1. **실시간 PLC 통신** (Ev2PlcEventSource)
   ```csharp
   // DSPilot/Services/Ev2PlcEventSource.cs
   private async Task PollPlcTagsAsync()
   {
       while (!_cts.Token.IsCancellationRequested)
       {
           var tags = await _ev2Service.ReadTagsAsync();
           foreach (var tag in tags)
           {
               if (HasValueChanged(tag))
               {
                   var evt = new PlcTagEvent
                   {
                       TagName = tag.Name,
                       Value = tag.Value,
                       Timestamp = DateTime.Now,
                       Source = "Real-time PLC"
                   };
                   await _eventProcessor.ProcessEventAsync(evt);
               }
           }
           await Task.Delay(100);  // 100ms polling
       }
   }
   ```

2. **이력 재생** (SqlitePlcHistorySource)
   ```csharp
   // DSPilot/Services/SqlitePlcHistorySource.cs
   public async IAsyncEnumerable<PlcTagEvent> ReplayHistoryAsync(DateTime from, DateTime to)
   {
       var logs = await _repository.GetPlcTagLogsAsync(from, to);
       foreach (var log in logs)
       {
           yield return new PlcTagEvent
           {
               TagName = log.TagName,
               Value = log.Value,
               Timestamp = log.Timestamp,
               Source = "History Replay"
           };
       }
   }
   ```

---

### Stage 2: Edge Detection (엣지 감지)

**입력**: PlcTagEvent
**출력**: RisingEdge 또는 FallingEdge (옵션)

```fsharp
// DSPilot.Engine/Tracking/TagStateTracker.fs

type EdgeType =
    | RisingEdge
    | FallingEdge
    | NoEdge

type EdgeEvent =
    { TagName: string
      EdgeType: EdgeType
      Timestamp: DateTime }

type TagStateTracker() =
    let mutable lastStates = Map.empty<string, bool>

    member this.DetectEdge(event: PlcTagEvent) : EdgeEvent option =
        match Map.tryFind event.TagName lastStates with
        | Some prevValue ->
            lastStates <- Map.add event.TagName event.Value lastStates

            // Rising Edge: false → true
            if not prevValue && event.Value then
                Some { TagName = event.TagName
                       EdgeType = RisingEdge
                       Timestamp = event.Timestamp }
            // Falling Edge: true → false
            elif prevValue && not event.Value then
                Some { TagName = event.TagName
                       EdgeType = FallingEdge
                       Timestamp = event.Timestamp }
            else
                None
        | None ->
            // 첫 번째 이벤트: 이전 상태 없음
            lastStates <- Map.add event.TagName event.Value lastStates
            None
```

**주의사항**:
- Rising Edge만 사용 (Falling Edge는 무시)
- 상태 추적을 위해 이전 값 저장 필요

---

### Stage 3: Tag to Call Mapping (태그 → 콜 매핑)

**입력**: EdgeEvent (TagName)
**출력**: DspCallEntity (옵션)

```fsharp
// DSPilot.Engine/Tracking/PlcToCallMapper.fs

module PlcToCallMapper =

    let findCallByTag (tagName: string) : Async<DspCallEntity option> =
        async {
            // dspCall 테이블에서 InTag 또는 OutTag로 검색
            let! calls = DspRepository.queryCallsByTag tagName
            return calls |> Seq.tryHead
        }

    let isInTag (call: DspCallEntity) (tagName: string) : bool =
        call.InTag = Some tagName

    let isOutTag (call: DspCallEntity) (tagName: string) : bool =
        call.OutTag = Some tagName
```

```sql
-- Repository Query
SELECT * FROM dspCall
WHERE InTag = ? OR OutTag = ?
LIMIT 1
```

**Unmapped Tag 처리**:
- 매핑 실패 시 이벤트 무시
- 디버깅을 위해 로그 기록

```fsharp
match! PlcToCallMapper.findCallByTag edgeEvent.TagName with
| Some call ->
    // 정상 처리
    ()
| None ->
    // Unmapped tag
    Logger.warn $"Unmapped tag: {edgeEvent.TagName}"
```

---

### Stage 4: State Transition (상태 전이)

**입력**: DspCallEntity, EdgeEvent
**출력**: Projection Update

#### 4.1 InTag Rising Edge

```fsharp
// DSPilot.Engine/Tracking/StateTransition.fs

let handleInTagRisingEdge (call: DspCallEntity) (timestamp: DateTime) =
    async {
        // 1. Call State 업데이트: Ready → Going
        let callPatch =
            { CallId = call.CallId
              State = Some "Going"
              LastStartAt = Some timestamp
              ProgressRate = Some 0.5
              CurrentCycleNo = Some (call.CurrentCycleNo + 1) }

        do! DspRepository.patchCall callPatch

        // 2. Flow ActiveCallCount 증가
        let! flow = DspRepository.getFlowByName call.FlowName
        let flowPatch =
            { FlowName = call.FlowName
              ActiveCallCount = Some (flow.ActiveCallCount + 1)
              State = Some "Going" }

        do! DspRepository.patchFlow flowPatch

        // 3. Head Call이면 Cycle 시작 기록
        if call.IsHead then
            let cycleStartPatch =
                { FlowName = call.FlowName
                  LastCycleStartAt = Some timestamp
                  LastCycleNo = Some (flow.LastCycleNo + 1) }

            do! DspRepository.patchFlow cycleStartPatch
    }
```

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
