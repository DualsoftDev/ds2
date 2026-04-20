# F# 모듈 구조

## 🎯 목적

DSPilot.Engine의 F# 모듈 구조와 각 모듈의 책임을 정의합니다.

---

## 📦 모듈 계층 구조

```
DSPilot.Engine/
├── Core/                          # 핵심 타입 및 유틸리티
│   ├── Types.fs                   # 공통 타입 정의
│   ├── CallKey.fs                 # Call 식별자
│   └── EdgeDetection.fs           # Edge 감지 로직
│
├── Database/                      # 데이터베이스 계층
│   ├── Configuration.fs           # DB 경로 설정
│   ├── Entities.fs                # Entity 타입
│   ├── Dtos.fs                    # Patch 타입
│   ├── Initialization.fs          # 스키마 초기화
│   ├── QueryHelpers.fs            # SQL 헬퍼
│   └── Repository.fs              # CRUD 및 Query
│
├── Bootstrap/                     # 정적 초기화
│   ├── Ev2Bootstrap.fs            # AASX 로드
│   ├── TopologyCalculator.fs     # Topology 계산
│   └── TagMapper.fs               # Tag Mapping
│
├── Tracking/                      # 실시간 추적
│   ├── TagStateTracker.fs         # Tag 상태 추적
│   ├── PlcToCallMapper.fs         # Tag → Call 매핑
│   └── StateTransition.fs         # State 전이 로직
│
├── Statistics/                    # 통계 계산
│   ├── Statistics.fs              # Call 통계
│   ├── RuntimeStatistics.fs       # Runtime 메트릭
│   └── FlowMetrics.fs             # Flow 집계
│
└── Analysis/                      # 분석 기능
    ├── CycleAnalysis.fs           # Cycle 분석
    ├── BottleneckDetection.fs     # 병목 감지
    ├── Performance.fs             # 성능 분석
    └── GanttLayout.fs             # Gantt 차트 레이아웃
```

---

## 📋 모듈별 상세 설명

### Core/ (핵심 타입)

#### Core/Types.fs

**책임**: 공통 타입 정의

```fsharp
module DSPilot.Engine.Core.Types

type PlcTagEvent =
    { TagName: string
      Value: bool
      Timestamp: DateTime
      Source: string }

type EdgeType =
    | RisingEdge
    | FallingEdge

type EdgeEvent =
    { TagName: string
      EdgeType: EdgeType
      Timestamp: DateTime }

type CallState =
    | Ready
    | Going
    | Done
    | Error

type FlowState =
    | Ready
    | Going
    | Error
```

#### Core/CallKey.fs

**책임**: Call 식별 및 키 생성

```fsharp
module DSPilot.Engine.Core.CallKey

type CallKey =
    { FlowName: string
      CallName: string }

let create flowName callName =
    { FlowName = flowName; CallName = callName }

let toString (key: CallKey) =
    $"{key.FlowName}/{key.CallName}"

let parse (str: string) : CallKey option =
    match str.Split('/') with
    | [| flowName; callName |] ->
        Some { FlowName = flowName; CallName = callName }
    | _ -> None
```

#### Core/EdgeDetection.fs

**책임**: Edge 감지 공통 로직

```fsharp
module DSPilot.Engine.Core.EdgeDetection

let isRisingEdge (prevValue: bool) (currentValue: bool) : bool =
    not prevValue && currentValue

let isFallingEdge (prevValue: bool) (currentValue: bool) : bool =
    prevValue && not currentValue

let detectEdge (prevValue: bool) (currentValue: bool) : EdgeType option =
    if isRisingEdge prevValue currentValue then Some RisingEdge
    elif isFallingEdge prevValue currentValue then Some FallingEdge
    else None
```

---

### Database/ (데이터베이스 계층)

#### Database/Configuration.fs

**책임**: DB 연결 설정 및 경로 관리

```fsharp
module DSPilot.Engine.Database.Configuration

type DatabasePaths =
    { SharedDbPath: string
      DspTablesEnabled: bool }

let private expandEnvVars (path: string) : string =
    Environment.ExpandEnvironmentVariables(path)

let getConnectionString (dbPath: string) : string =
    $"Data Source={expandEnvVars dbPath};Version=3;"

let getFlowTableName() = "dspFlow"
let getCallTableName() = "dspCall"
```

#### Database/Entities.fs

**책임**: Entity 타입 정의 (전체 모델)

```fsharp
module DSPilot.Engine.Database.Entities

type DspFlowEntity =
    { Id: int option
      FlowName: string
      SystemName: string option
      WorkName: string option
      MovingStartName: string option
      MovingEndName: string option
      SequenceNo: int option
      IsHead: bool
      IsTail: bool
      State: string option
      ActiveCallCount: int
      ErrorCallCount: int
      LastCycleStartAt: DateTime option
      LastCycleEndAt: DateTime option
      LastCycleNo: int
      MT: float option
      WT: float option
      CT: float option
      LastCycleDurationMs: float option
      AverageCT: float option
      StdDevCT: float option
      MinCT: float option
      MaxCT: float option
      CompletedCycleCount: int
      SlowCycleFlag: bool
      UnmappedCallCount: int
      FocusScore: int
      CreatedAt: DateTime
      UpdatedAt: DateTime }

type DspCallEntity =
    { Id: int option
      CallId: Guid
      CallName: string
      ApiCall: string option
      WorkName: string
      FlowName: string
      SystemName: string option
      Next: string option
      Prev: string option
      IsHead: bool
      IsTail: bool
      SequenceNo: int option
      Device: string option
      InTag: string option
      OutTag: string option
      State: string
      ProgressRate: float
      LastStartAt: DateTime option
      LastFinishAt: DateTime option
      LastDurationMs: float option
      CurrentCycleNo: int
      AverageGoingTime: float option
      StdDevGoingTime: float option
      MinGoingTime: float option
      MaxGoingTime: float option
      GoingCount: int
      ErrorCount: int
      ErrorText: string option
      SlowFlag: bool
      UnmappedFlag: bool
      FocusScore: int
      CreatedAt: DateTime
      UpdatedAt: DateTime }
```

#### Database/Dtos.fs

**책임**: Patch DTO 정의 (부분 업데이트용)

```fsharp
module DSPilot.Engine.Database.Dtos

type FlowPatch =
    { FlowName: string
      // Static Metadata
      SystemName: string option
      WorkName: string option
      MovingStartName: string option
      MovingEndName: string option
      SequenceNo: int option
      IsHead: bool option
      IsTail: bool option
      // Real-time State
      State: string option
      ActiveCallCount: int option
      ErrorCallCount: int option
      LastCycleStartAt: DateTime option
      LastCycleEndAt: DateTime option
      LastCycleNo: int option
      // Cumulative Statistics
      MT: float option
      WT: float option
      CT: float option
      LastCycleDurationMs: float option
      AverageCT: float option
      StdDevCT: float option
      MinCT: float option
      MaxCT: float option
      CompletedCycleCount: int option
      // Derived Warnings
      SlowCycleFlag: bool option
      UnmappedCallCount: int option
      FocusScore: int option }

type CallPatch =
    { CallId: Guid
      // Static Metadata
      CallName: string option
      ApiCall: string option
      WorkName: string option
      FlowName: string option
      SystemName: string option
      Next: string option
      Prev: string option
      IsHead: bool option
      IsTail: bool option
      SequenceNo: int option
      Device: string option
      InTag: string option
      OutTag: string option
      // Real-time State
      State: string option
      ProgressRate: float option
      LastStartAt: DateTime option
      LastFinishAt: DateTime option
      LastDurationMs: float option
      CurrentCycleNo: int option
      // Cumulative Statistics
      AverageGoingTime: float option
      StdDevGoingTime: float option
      MinGoingTime: float option
      MaxGoingTime: float option
      GoingCount: int option
      ErrorCount: int option
      ErrorText: string option
      // Derived Warnings
      SlowFlag: bool option
      UnmappedFlag: bool option
      FocusScore: int option }
```

#### Database/Repository.fs

**책임**: CRUD 및 Query 연산

주요 메서드는 [12_REPOSITORY_API.md](./12_REPOSITORY_API.md) 참조

---

### Bootstrap/ (정적 초기화)

#### Bootstrap/Ev2Bootstrap.fs

**책임**: AASX 파일 로드 및 Projection 초기화

```fsharp
module DSPilot.Engine.Bootstrap.Ev2Bootstrap

let bootstrapFromAasx (aasxPath: string) : Async<unit> =
    async {
        // 1. AASX 파싱
        let! project = Ev2AasxParser.parse aasxPath

        // 2. System → Flow 추출
        let flows =
            project.Systems
            |> Seq.collect (fun sys ->
                sys.Flows
                |> Seq.map (fun flow ->
                    { FlowName = flow.Name
                      SystemName = Some sys.Name
                      WorkName = Some flow.ParentWork.Name  // ⚠️ work.Name!
                      MovingStartName = Some flow.MovingStart
                      MovingEndName = Some flow.MovingEnd
                      // Topology는 Call 기반 계산
                      SequenceNo = None
                      IsHead = false
                      IsTail = false
                      // 초기 상태
                      State = Some "Ready"
                      ActiveCallCount = 0
                      ErrorCallCount = 0
                      // 나머지는 default
                      // ...
                    }))
            |> Seq.toList

        // 3. Flow → Work → Call 추출
        let calls =
            project.Systems
            |> Seq.collect (fun sys ->
                sys.Flows
                |> Seq.collect (fun flow ->
                    flow.Works
                    |> Seq.collect (fun work ->
                        work.Calls
                        |> Seq.map (fun call ->
                            { CallId = call.Id
                              CallName = call.Name
                              ApiCall = Some call.ApiCall
                              WorkName = work.Name  // ⚠️ work.Name!
                              FlowName = flow.Name
                              SystemName = Some sys.Name
                              // Topology 계산은 다음 단계
                              Next = None
                              Prev = None
                              IsHead = false
                              IsTail = false
                              SequenceNo = Some call.SequenceNo
                              Device = Some call.Device
                              // Tag Mapping은 다음 단계
                              InTag = None
                              OutTag = None
                              // 초기 상태
                              State = "Ready"
                              ProgressRate = 0.0
                              CurrentCycleNo = 0
                              // 나머지는 default
                              // ...
                            }))))
            |> Seq.toList

        // 4. Topology 계산
        let callsWithTopology = TopologyCalculator.calculate calls

        // 5. Tag Mapping
        let callsWithTags = TagMapper.mapTags callsWithTopology project.PlcMapping

        // 6. Projection UPSERT
        do! DspRepository.bulkUpsertFlows flows
        do! DspRepository.bulkUpsertCalls callsWithTags
    }
```

#### Bootstrap/TopologyCalculator.fs

**책임**: Prev, Next, IsHead, IsTail 계산

```fsharp
module DSPilot.Engine.Bootstrap.TopologyCalculator

let calculate (calls: DspCallEntity list) : DspCallEntity list =
    calls
    |> List.groupBy (fun c -> c.FlowName)
    |> List.collect (fun (flowName, flowCalls) ->
        flowCalls
        |> List.sortBy (fun c -> c.SequenceNo |> Option.defaultValue 0)
        |> List.mapi (fun i call ->
            let prev = if i > 0 then Some flowCalls.[i-1].CallName else None
            let next = if i < flowCalls.Length - 1 then Some flowCalls.[i+1].CallName else None
            let isHead = i = 0
            let isTail = i = flowCalls.Length - 1

            { call with
                Prev = prev
                Next = next
                IsHead = isHead
                IsTail = isTail }))
```

#### Bootstrap/TagMapper.fs

**책임**: Call ↔ PLC Tag 매핑

```fsharp
module DSPilot.Engine.Bootstrap.TagMapper

type PlcMapping =
    { InTags: Map<string, string>   // CallName → InTag
      OutTags: Map<string, string>  // CallName → OutTag }

let mapTags (calls: DspCallEntity list) (plcMapping: PlcMapping) : DspCallEntity list =
    calls
    |> List.map (fun call ->
        let inTag = plcMapping.InTags.TryFind call.CallName
        let outTag = plcMapping.OutTags.TryFind call.CallName
        let unmappedFlag = inTag.IsNone || outTag.IsNone

        { call with
            InTag = inTag
            OutTag = outTag
            UnmappedFlag = unmappedFlag })
```

---

### Tracking/ (실시간 추적)

#### Tracking/TagStateTracker.fs

**책임**: Tag 상태 추적 및 Edge 감지

```fsharp
module DSPilot.Engine.Tracking.TagStateTracker

type TagStateTracker() =
    let mutable lastStates = Map.empty<string, bool>

    member this.DetectEdge(event: PlcTagEvent) : EdgeEvent option =
        match Map.tryFind event.TagName lastStates with
        | Some prevValue ->
            lastStates <- Map.add event.TagName event.Value lastStates

            match EdgeDetection.detectEdge prevValue event.Value with
            | Some edgeType ->
                Some { TagName = event.TagName
                       EdgeType = edgeType
                       Timestamp = event.Timestamp }
            | None -> None
        | None ->
            lastStates <- Map.add event.TagName event.Value lastStates
            None

    member this.Reset() =
        lastStates <- Map.empty
```

#### Tracking/PlcToCallMapper.fs

**책임**: Tag → Call 매핑

```fsharp
module DSPilot.Engine.Tracking.PlcToCallMapper

let findCallByTag (tagName: string) : Async<DspCallEntity option> =
    DspRepository.queryCallsByTag tagName

let isInTag (call: DspCallEntity) (tagName: string) : bool =
    call.InTag = Some tagName

let isOutTag (call: DspCallEntity) (tagName: string) : bool =
    call.OutTag = Some tagName
```

#### Tracking/StateTransition.fs

**책임**: State 전이 및 Projection 업데이트

주요 로직은 [04_EVENT_PIPELINE.md](./04_EVENT_PIPELINE.md) 참조

---

### Statistics/ (통계 계산)

#### Statistics/Statistics.fs

**책임**: Call 레벨 통계 계산

```fsharp
module DSPilot.Engine.Statistics.Statistics

let updateCallStatistics (callId: Guid) (newDuration: float) : Async<unit> =
    async {
        let! call = DspRepository.getCallById callId

        let n = call.GoingCount + 1
        let oldAvg = call.AverageGoingTime |> Option.defaultValue 0.0
        let oldStdDev = call.StdDevGoingTime |> Option.defaultValue 0.0

        // Incremental Average
        let newAvg = (oldAvg * float(n - 1) + newDuration) / float(n)

        // Incremental StdDev (Welford's method)
        let oldM2 = oldStdDev * oldStdDev * float(n - 1)
        let delta = newDuration - oldAvg
        let newAvgAdjusted = oldAvg + delta / float(n)
        let delta2 = newDuration - newAvgAdjusted
        let newM2 = oldM2 + delta * delta2
        let newStdDev = sqrt(newM2 / float(n))

        // Min/Max
        let newMin = min (call.MinGoingTime |> Option.defaultValue newDuration) newDuration
        let newMax = max (call.MaxGoingTime |> Option.defaultValue newDuration) newDuration

        // SlowFlag 판정
        let slowFlag = newDuration > (newAvg + 2.0 * newStdDev)

        // FocusScore 계산
        let focusScore = FocusScoreCalculator.calculate call slowFlag

        let patch =
            { CallId = callId
              AverageGoingTime = Some newAvg
              StdDevGoingTime = Some newStdDev
              MinGoingTime = Some newMin
              MaxGoingTime = Some newMax
              GoingCount = Some n
              SlowFlag = Some slowFlag
              FocusScore = Some focusScore }

        do! DspRepository.patchCall patch
    }

module FocusScoreCalculator =
    let calculate (call: DspCallEntity) (slowFlag: bool) : int =
        let mutable score = 0

        // Error: +100
        if call.State = "Error" then score <- score + 100

        // Unmapped: +70
        if call.UnmappedFlag then score <- score + 70

        // Slow: +50
        if slowFlag then score <- score + 50

        // High StdDev: +30
        let stdDevRatio =
            match call.AverageGoingTime, call.StdDevGoingTime with
            | Some avg, Some stdDev when avg > 0.0 -> stdDev / avg
            | _ -> 0.0

        if stdDevRatio > 0.3 then score <- score + 30

        score
```

#### Statistics/FlowMetrics.fs

**책임**: Flow 레벨 집계

```fsharp
module DSPilot.Engine.Statistics.FlowMetrics

let updateFlowMetrics (flowName: string) : Async<unit> =
    async {
        let! calls = DspRepository.getCallsByFlow flowName

        // 집계 계산
        let activeCount = calls |> Seq.filter (fun c -> c.State = "Going") |> Seq.length
        let errorCount = calls |> Seq.filter (fun c -> c.State = "Error") |> Seq.length
        let unmappedCount = calls |> Seq.filter (fun c -> c.UnmappedFlag) |> Seq.length

        // Flow State
        let flowState =
            if errorCount > 0 then "Error"
            elif activeCount > 0 then "Going"
            else "Ready"

        let patch =
            { FlowName = flowName
              State = Some flowState
              ActiveCallCount = Some activeCount
              ErrorCallCount = Some errorCount
              UnmappedCallCount = Some unmappedCount }

        do! DspRepository.patchFlow patch
    }
```

---

## 🔗 모듈 간 의존성

```
┌─────────────────────────────────────────────┐
│             Application Layer               │
│  (C# Services, Blazor UI)                   │
└──────────────────┬──────────────────────────┘
                   │
                   ▼
┌─────────────────────────────────────────────┐
│          DSPilot.Engine (F#)                │
├─────────────────────────────────────────────┤
│  Bootstrap  →  Repository                   │
│  Tracking   →  Repository                   │
│  Statistics →  Repository                   │
│                                              │
│  All modules depend on:                     │
│  - Core (Types)                             │
│  - Database (Entities, Dtos)                │
└─────────────────────────────────────────────┘
```

---

## 📚 관련 문서

- [01_ARCHITECTURE.md](./01_ARCHITECTURE.md) - 전체 아키텍처
- [12_REPOSITORY_API.md](./12_REPOSITORY_API.md) - Repository API
- [09_REFACTORING_PLAN.md](./09_REFACTORING_PLAN.md) - 리팩토링 계획
