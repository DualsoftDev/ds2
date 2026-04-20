# Projection 패턴

## 🎯 핵심 개념

**Projection**은 CQRS 패턴의 핵심으로, **쓰기 모델**(Write Model)과 **읽기 모델**(Read Model)을 분리합니다.

DSPilot에서 Projection 테이블(dspFlow, dspCall)은 **UI가 즉시 사용할 수 있도록 사전 계산된 읽기 전용 데이터**를 저장합니다.

---

## 📐 아키텍처 패턴

```
┌─────────────────────────────────────────────────────────────┐
│                   Projection Pattern                         │
├─────────────────────────────────────────────────────────────┤
│                                                               │
│  Event Source (이벤트 소스)                                   │
│  ├─ AASX File Load                                           │
│  ├─ PLC Tag Value Change                                     │
│  └─ Manual Command                                           │
│                                                               │
│          │                                                    │
│          ▼                                                    │
│  ┌───────────────────┐                                       │
│  │  Event Handler    │                                       │
│  │  (F# Module)      │                                       │
│  └────────┬──────────┘                                       │
│           │                                                   │
│           ▼                                                   │
│  ┌───────────────────┐                                       │
│  │  Tracker/         │                                       │
│  │  Calculator       │  ← 상태 추적 및 계산                  │
│  └────────┬──────────┘                                       │
│           │                                                   │
│           ▼                                                   │
│  ┌───────────────────┐                                       │
│  │  Patch Builder    │  ← 변경된 필드만 추출                │
│  └────────┬──────────┘                                       │
│           │                                                   │
│           ▼                                                   │
│  ┌───────────────────┐                                       │
│  │  Projection       │  ← UPDATE dspFlow/dspCall             │
│  │  Writer           │                                       │
│  └────────┬──────────┘                                       │
│           │                                                   │
│           ▼                                                   │
│  ┌─────────────────────────────────────┐                    │
│  │     dspFlow / dspCall               │                    │
│  │     (Projection Tables)             │                    │
│  └──────────────┬──────────────────────┘                    │
│                 │                                             │
│                 ▼                                             │
│  ┌─────────────────────────────────────┐                    │
│  │     UI Layer (Blazor)               │                    │
│  │     - 계산 금지                      │                    │
│  │     - 순수 읽기만                    │                    │
│  └─────────────────────────────────────┘                    │
│                                                               │
└─────────────────────────────────────────────────────────────┘
```

---

## 🔄 세 가지 계산 책임

### 1. Static Bootstrap (정적 부트스트랩)

**트리거**: AASX 프로젝트 로드 시
**책임**: 정적 메타데이터 초기화

```fsharp
// DSPilot.Engine/Bootstrap/Ev2Bootstrap.fs

let bootstrapFromAasx (aasxPath: string) =
    // 1. AASX 파싱
    let project = AasxParser.parse aasxPath

    // 2. Topology 계산
    let flows = project.Systems
                |> Seq.collect (fun sys -> sys.Flows)
                |> Seq.map (fun flow ->
                    { FlowName = flow.Name
                      SystemName = sys.Name
                      WorkName = flow.ParentWork.Name  // ⚠️ work.Name!
                      MovingStartName = flow.MovingStart
                      MovingEndName = flow.MovingEnd
                      SequenceNo = calculateSequence flow
                      IsHead = flow.IsHead
                      IsTail = flow.IsTail
                      // 통계 필드는 null/default
                      State = Some "Ready"
                      ActiveCallCount = 0
                      // ...
                    })

    // 3. Projection UPSERT (통계 보존)
    flows |> Seq.iter (fun flow ->
        DspRepository.upsertFlow flow)
```

**업데이트 규칙**:
- UPSERT를 사용하여 기존 통계는 보존
- 정적 메타데이터만 업데이트

```sql
INSERT INTO dspFlow (FlowName, SystemName, WorkName, ...)
VALUES (?, ?, ?, ...)
ON CONFLICT(FlowName) DO UPDATE SET
    SystemName = excluded.SystemName,
    WorkName = excluded.WorkName,
    -- 통계는 COALESCE로 기존 값 유지
    AverageCT = COALESCE(dspFlow.AverageCT, excluded.AverageCT),
    CompletedCycleCount = COALESCE(dspFlow.CompletedCycleCount, excluded.CompletedCycleCount)
```

---

### 2. Runtime Event Update (런타임 이벤트 업데이트)

**트리거**: PLC Tag 값 변경 이벤트
**책임**: 실시간 상태 추적

```fsharp
// DSPilot.Engine/Tracking/StateTransition.fs

let handlePlcEvent (event: PlcTagEvent) (tracker: TagStateTracker) =
    // 1. Rising Edge 감지
    match tracker.detectRisingEdge event with
    | Some risingEdge ->
        // 2. Tag → Call 매핑
        let callOpt = PlcToCallMapper.findCallByTag risingEdge.TagName

        match callOpt with
        | Some call ->
            // 3. InTag vs OutTag 판정
            if risingEdge.TagName = call.InTag then
                // InTag Rising → Going
                let patch =
                    { CallId = call.CallId
                      State = Some "Going"
                      LastStartAt = Some risingEdge.Timestamp
                      ProgressRate = Some 0.5
                      CurrentCycleNo = Some (call.CurrentCycleNo + 1) }

                DspRepository.patchCall patch

            elif risingEdge.TagName = call.OutTag then
                // OutTag Rising → Done
                let duration = risingEdge.Timestamp - call.LastStartAt
                let patch =
                    { CallId = call.CallId
                      State = Some "Done"
                      LastFinishAt = Some risingEdge.Timestamp
                      LastDurationMs = Some duration.TotalMilliseconds
                      ProgressRate = Some 1.0 }

                DspRepository.patchCall patch

                // 통계 계산 트리거
                StatisticsCalculator.updateCallStatistics call.CallId duration
        | None ->
            // Unmapped tag
            ()
    | None ->
        // No rising edge
        ()
```

**업데이트 필드**:
- State, LastStartAt, LastFinishAt, LastDurationMs
- ProgressRate, CurrentCycleNo
- ActiveCallCount, ErrorCallCount

---

### 3. Aggregate Recompute (집계 재계산)

**트리거**: Call 완료 시, Tail Call 완료 시
**책임**: 통계 및 Flow 레벨 집계

```fsharp
// DSPilot.Engine/Statistics/Statistics.fs

let updateCallStatistics (callId: Guid) (newDuration: TimeSpan) =
    // 1. 현재 통계 조회
    let call = DspRepository.getCallById callId

    // 2. Incremental 통계 계산
    let n = call.GoingCount + 1
    let oldAvg = call.AverageGoingTime |> Option.defaultValue 0.0
    let newValue = newDuration.TotalMilliseconds

    // Incremental Average
    let newAvg = (oldAvg * float(n - 1) + newValue) / float(n)

    // Incremental StdDev (Welford's method)
    let oldM2 = (call.StdDevGoingTime |> Option.defaultValue 0.0) ** 2.0 * float(n - 1)
    let delta = newValue - oldAvg
    let newAvgAdjusted = oldAvg + delta / float(n)
    let delta2 = newValue - newAvgAdjusted
    let newM2 = oldM2 + delta * delta2
    let newStdDev = sqrt(newM2 / float(n))

    // Min/Max
    let newMin = min (call.MinGoingTime |> Option.defaultValue newValue) newValue
    let newMax = max (call.MaxGoingTime |> Option.defaultValue newValue) newValue

    // SlowFlag 판정
    let slowFlag = newValue > (newAvg + 2.0 * newStdDev)

    // FocusScore 계산
    let focusScore = FocusScoreCalculator.calculate call slowFlag

    // 3. Projection Update
    let patch =
        { CallId = callId
          AverageGoingTime = Some newAvg
          StdDevGoingTime = Some newStdDev
          MinGoingTime = Some newMin
          MaxGoingTime = Some newMax
          GoingCount = Some n
          SlowFlag = Some slowFlag
          FocusScore = Some focusScore }

    DspRepository.patchCall patch

    // 4. Flow Aggregation 트리거
    if call.IsTail then
        FlowMetricsCalculator.updateFlowMetrics call.FlowName
```

```fsharp
// DSPilot.Engine/Statistics/FlowMetrics.fs

let updateFlowMetrics (flowName: string) =
    // 1. Flow의 모든 Call 조회
    let calls = DspRepository.getCallsByFlow flowName

    // 2. 집계 계산
    let activeCount = calls |> Seq.filter (fun c -> c.State = "Going") |> Seq.length
    let errorCount = calls |> Seq.filter (fun c -> c.State = "Error") |> Seq.length
    let unmappedCount = calls |> Seq.filter (fun c -> c.UnmappedFlag) |> Seq.length

    // Flow State 결정
    let flowState =
        if errorCount > 0 then "Error"
        elif activeCount > 0 then "Going"
        else "Ready"

    // Cycle Metrics (Tail Call 완료 시만)
    let tailCall = calls |> Seq.tryFind (fun c -> c.IsTail)
    let cycleMetrics =
        match tailCall with
        | Some tail when tail.State = "Done" ->
            let mt = calls |> Seq.sumBy (fun c -> c.LastDurationMs |> Option.defaultValue 0.0)
            let ct = (tail.LastFinishAt.Value - (calls |> Seq.find (fun c -> c.IsHead)).LastStartAt.Value).TotalMilliseconds
            let wt = ct - mt
            Some (mt, wt, ct)
        | _ ->
            None

    // 3. Projection Update
    let patch =
        { FlowName = flowName
          State = Some flowState
          ActiveCallCount = Some activeCount
          ErrorCallCount = Some errorCount
          UnmappedCallCount = Some unmappedCount
          MT = cycleMetrics |> Option.map (fun (m,_,_) -> m)
          WT = cycleMetrics |> Option.map (fun (_,w,_) -> w)
          CT = cycleMetrics |> Option.map (fun (_,_,c) -> c) }

    DspRepository.patchFlow patch
```

---

## 🔧 Patch-based Update

### 기존 방식 (❌ 너무 좁음)

```fsharp
// 5개 필드만 업데이트
let updateFlowMetricsAsync (flowName: string) (mt: int) (wt: int) (ct: int) (state: string) =
    use conn = new SqliteConnection(connStr)
    conn.ExecuteAsync(
        "UPDATE dspFlow SET MT=@mt, WT=@wt, CT=@ct, State=@state WHERE FlowName=@flowName",
        {| flowName=flowName; mt=mt; wt=wt; ct=ct; state=state |})
```

**문제점**:
- 다른 필드 업데이트 시 새 함수 필요
- 확장성 없음

### 새로운 방식 (✅ Patch 기반)

```fsharp
// DSPilot.Engine/Database/Repository.fs

type FlowPatch =
    { FlowName: string
      SystemName: string option
      WorkName: string option
      State: string option
      ActiveCallCount: int option
      ErrorCallCount: int option
      MT: float option
      WT: float option
      CT: float option
      AverageCT: float option
      StdDevCT: float option
      SlowCycleFlag: bool option
      FocusScore: int option
      // ... 모든 업데이트 가능 필드 }

let patchFlow (patch: FlowPatch) : Async<unit> =
    async {
        // 1. 변경된 필드만 추출
        let updates =
            [ if patch.SystemName.IsSome then "SystemName = @SystemName"
              if patch.WorkName.IsSome then "WorkName = @WorkName"
              if patch.State.IsSome then "State = @State"
              if patch.ActiveCallCount.IsSome then "ActiveCallCount = @ActiveCallCount"
              if patch.MT.IsSome then "MT = @MT"
              if patch.WT.IsSome then "WT = @WT"
              if patch.CT.IsSome then "CT = @CT"
              // ...
            ]

        if updates.IsEmpty then
            return ()
        else
            // 2. Dynamic SQL 생성
            let sql = sprintf "UPDATE dspFlow SET %s, UpdatedAt = datetime('now') WHERE FlowName = @FlowName"
                             (String.concat ", " updates)

            // 3. Execute
            use conn = new SqliteConnection(connStr)
            let! _ = conn.ExecuteAsync(sql, patch) |> Async.AwaitTask
            return ()
    }
```

**장점**:
- 단일 함수로 모든 필드 업데이트 가능
- 변경된 필드만 SQL에 포함 (효율적)
- 확장 가능 (새 필드 추가 시 Patch 타입만 수정)

---

## 📋 사용 예시

### Bootstrap 시

```fsharp
// AASX 로드 → Static Metadata UPSERT
let flow =
    { FlowName = "FLOW_001"
      SystemName = Some "SYS_A"
      WorkName = Some "WORK_X"
      MovingStartName = Some "CALL_001"
      MovingEndName = Some "CALL_010"
      SequenceNo = Some 1
      IsHead = true
      IsTail = false
      State = Some "Ready"
      ActiveCallCount = Some 0
      // 나머지는 None (기존 값 유지)
    }

DspRepository.upsertFlow flow
```

### PLC Event 수신 시

```fsharp
// InTag Rising Edge → State 변경
let patch =
    { CallId = Guid.Parse("...")
      State = Some "Going"
      LastStartAt = Some DateTime.Now
      ProgressRate = Some 0.5
      CurrentCycleNo = Some 42
      // 나머지 필드는 None
    }

DspRepository.patchCall patch
```

### Call 완료 시

```fsharp
// 통계 업데이트
let patch =
    { CallId = Guid.Parse("...")
      State = Some "Done"
      LastFinishAt = Some DateTime.Now
      LastDurationMs = Some 1234.5
      AverageGoingTime = Some 1200.0
      StdDevGoingTime = Some 50.0
      GoingCount = Some 100
      SlowFlag = Some true
      FocusScore = Some 50
      // 나머지 필드는 None
    }

DspRepository.patchCall patch
```

---

## 🚨 UI 계산 금지 규칙

### ❌ 금지된 코드

```csharp
// UI에서 계산 금지!
var averageDuration = calls.Average(c => c.LastDurationMs);
var activeCount = calls.Count(c => c.State == "Going");
var mt = calls.Sum(c => c.LastDurationMs);
```

### ✅ 허용된 코드

```csharp
// Projection에서 읽기만
var averageDuration = flow.AverageCT;
var activeCount = flow.ActiveCallCount;
var mt = flow.MT;
```

**원칙**:
- UI는 Projection 테이블만 읽음
- 모든 계산은 DSPilot.Engine에서 사전 수행
- Projection은 항상 최신 상태 유지

---

## 📚 관련 문서

- [01_ARCHITECTURE.md](./01_ARCHITECTURE.md) - 전체 아키텍처
- [02_DATABASE_SCHEMA.md](./02_DATABASE_SCHEMA.md) - 스키마 설계
- [04_EVENT_PIPELINE.md](./04_EVENT_PIPELINE.md) - 이벤트 파이프라인
- [05_UPDATE_RULES.md](./05_UPDATE_RULES.md) - 업데이트 규칙
- [12_REPOSITORY_API.md](./12_REPOSITORY_API.md) - Repository API
