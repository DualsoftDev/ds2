# Step 3: Statistics Calculation Implementation

## 📋 목표

Step 2에서 구현한 PLC Event 처리에 **통계 계산**을 추가합니다.

- **Incremental Statistics**: Welford's Method로 O(1) 업데이트
- **Call 레벨 통계**: 각 Call의 실행 시간 통계 (평균, 표준편차, 최소, 최대)
- **Flow 레벨 통계**: Flow의 MT/WT/CT 계산
- **Append 방식**: 기존 Step 2 코드에 통계 계산만 추가

---

## 🗄️ Step 3.1: Database Schema Extension

### Migration 003: Add Statistics Fields

```sql
-- File: DSPilot.Engine/Database/Migrations/003_add_statistics_fields.sql

-- Call 레벨 통계 필드
ALTER TABLE dspCall ADD COLUMN AverageGoingTime REAL DEFAULT 0;
ALTER TABLE dspCall ADD COLUMN StdDevGoingTime REAL DEFAULT 0;
ALTER TABLE dspCall ADD COLUMN MinGoingTime REAL;
ALTER TABLE dspCall ADD COLUMN MaxGoingTime REAL;
ALTER TABLE dspCall ADD COLUMN GoingCount INTEGER DEFAULT 0;

-- Welford's Method를 위한 내부 필드 (M2 = sum of squared differences)
ALTER TABLE dspCall ADD COLUMN GoingTimeM2 REAL DEFAULT 0;

-- Flow 레벨 통계 필드
ALTER TABLE dspFlow ADD COLUMN MT REAL DEFAULT 0;
ALTER TABLE dspFlow ADD COLUMN WT REAL DEFAULT 0;
ALTER TABLE dspFlow ADD COLUMN CT REAL DEFAULT 0;
ALTER TABLE dspFlow ADD COLUMN AverageCT REAL DEFAULT 0;
ALTER TABLE dspFlow ADD COLUMN StdDevCT REAL DEFAULT 0;
ALTER TABLE dspFlow ADD COLUMN CycleCount INTEGER DEFAULT 0;
ALTER TABLE dspFlow ADD COLUMN CycleTimeM2 REAL DEFAULT 0;
```

### Entity Updates

```fsharp
// File: DSPilot.Engine/Database/Entities.fs

type DspCallEntity =
    { Id: int
      CallName: string
      FlowName: string
      State: string
      InTag: string
      OutTag: string
      LastStartAt: DateTime option
      LastFinishAt: DateTime option
      LastDurationMs: float option
      // ✨ Step 3: Statistics fields
      AverageGoingTime: float
      StdDevGoingTime: float
      MinGoingTime: float option
      MaxGoingTime: float option
      GoingCount: int
      GoingTimeM2: float // Welford's M2
      CreatedAt: DateTime
      UpdatedAt: DateTime }

type DspFlowEntity =
    { Id: int
      FlowName: string
      State: string
      ActiveCallCount: int
      // ✨ Step 3: Flow statistics fields
      MT: float
      WT: float
      CT: float
      AverageCT: float
      StdDevCT: float
      CycleCount: int
      CycleTimeM2: float
      CreatedAt: DateTime
      UpdatedAt: DateTime }
```

---

## 📊 Step 3.2: Statistics Module (Welford's Method)

### Core Types

```fsharp
// File: DSPilot.Engine/Statistics/IncrementalStats.fs

namespace DSPilot.Engine.Statistics

/// Welford's Method로 계산된 통계 결과
type IncrementalStatsResult =
    { Count: int
      Mean: float
      Variance: float
      StdDev: float
      Min: float option
      Max: float option
      M2: float } // Sum of squared differences

/// 새 값으로 통계 업데이트
module IncrementalStats =

    /// Welford's Method: O(1) 평균/표준편차 업데이트
    /// https://en.wikipedia.org/wiki/Algorithms_for_calculating_variance#Welford's_online_algorithm
    let update
        (currentCount: int)
        (currentMean: float)
        (currentM2: float)
        (currentMin: float option)
        (currentMax: float option)
        (newValue: float)
        : IncrementalStatsResult =

        let newCount = currentCount + 1
        let delta = newValue - currentMean
        let newMean = currentMean + delta / float newCount
        let delta2 = newValue - newMean
        let newM2 = currentM2 + delta * delta2

        let newVariance =
            if newCount < 2 then 0.0
            else newM2 / float newCount

        let newStdDev = sqrt newVariance

        let newMin =
            match currentMin with
            | None -> Some newValue
            | Some minVal -> Some (min minVal newValue)

        let newMax =
            match currentMax with
            | None -> Some newValue
            | Some maxVal -> Some (max maxVal newValue)

        { Count = newCount
          Mean = newMean
          Variance = newVariance
          StdDev = newStdDev
          Min = newMin
          Max = newMax
          M2 = newM2 }

    /// 빈 통계 생성
    let empty : IncrementalStatsResult =
        { Count = 0
          Mean = 0.0
          Variance = 0.0
          StdDev = 0.0
          Min = None
          Max = None
          M2 = 0.0 }
```

---

## 🔄 Step 3.3: Repository - Statistics Update Methods

### Call Statistics Update

```fsharp
// File: DSPilot.Engine/Database/Repository.fs

module DspRepository =

    // ... (기존 Step 0, 1, 2 메서드들)

    /// Call의 통계 필드만 업데이트 (Welford's Method)
    let updateCallStatistics
        (dbPath: string)
        (callName: string)
        (newDurationMs: float)
        : Async<unit> = async {

        use conn = new SQLiteConnection($"Data Source={dbPath}")
        do! conn.OpenAsync() |> Async.AwaitTask

        // 1. 현재 통계 값 조회
        let! current = async {
            let sql = """
                SELECT AverageGoingTime, GoingTimeM2, MinGoingTime, MaxGoingTime, GoingCount
                FROM dspCall
                WHERE CallName = @CallName
            """
            let! result = conn.QueryFirstOrDefaultAsync<{|
                AverageGoingTime: float
                GoingTimeM2: float
                MinGoingTime: float option
                MaxGoingTime: float option
                GoingCount: int
            |}>(sql, {| CallName = callName |}) |> Async.AwaitTask
            return result
        }

        // 2. Welford's Method로 새 통계 계산
        let newStats = IncrementalStats.update
            current.GoingCount
            current.AverageGoingTime
            current.GoingTimeM2
            current.MinGoingTime
            current.MaxGoingTime
            newDurationMs

        // 3. 업데이트
        let updateSql = """
            UPDATE dspCall
            SET AverageGoingTime = @Mean,
                StdDevGoingTime = @StdDev,
                MinGoingTime = @Min,
                MaxGoingTime = @Max,
                GoingCount = @Count,
                GoingTimeM2 = @M2,
                UpdatedAt = @Now
            WHERE CallName = @CallName
        """
        do! conn.ExecuteAsync(updateSql, {|
            Mean = newStats.Mean
            StdDev = newStats.StdDev
            Min = newStats.Min
            Max = newStats.Max
            Count = newStats.Count
            M2 = newStats.M2
            Now = DateTime.UtcNow.ToString("o")
            CallName = callName
        |}) |> Async.AwaitTask |> Async.Ignore
    }

    /// Flow의 CT 통계만 업데이트
    let updateFlowCycleTimeStatistics
        (dbPath: string)
        (flowName: string)
        (newCycleTimeMs: float)
        : Async<unit> = async {

        use conn = new SQLiteConnection($"Data Source={dbPath}")
        do! conn.OpenAsync() |> Async.AwaitTask

        let! current = async {
            let sql = """
                SELECT AverageCT, CycleTimeM2, CycleCount
                FROM dspFlow
                WHERE FlowName = @FlowName
            """
            let! result = conn.QueryFirstOrDefaultAsync<{|
                AverageCT: float
                CycleTimeM2: float
                CycleCount: int
            |}>(sql, {| FlowName = flowName |}) |> Async.AwaitTask
            return result
        }

        let newStats = IncrementalStats.update
            current.CycleCount
            current.AverageCT
            current.CycleTimeM2
            None
            None
            newCycleTimeMs

        let updateSql = """
            UPDATE dspFlow
            SET AverageCT = @Mean,
                StdDevCT = @StdDev,
                CycleCount = @Count,
                CycleTimeM2 = @M2,
                UpdatedAt = @Now
            WHERE FlowName = @FlowName
        """
        do! conn.ExecuteAsync(updateSql, {|
            Mean = newStats.Mean
            StdDev = newStats.StdDev
            Count = newStats.Count
            M2 = newStats.M2
            Now = DateTime.UtcNow.ToString("o")
            FlowName = flowName
        |}) |> Async.AwaitTask |> Async.Ignore
    }
```

---

## 🔄 Step 3.4: StateTransition 확장 (통계 계산 추가)

### OutTag Rising Edge에 통계 업데이트 추가

```fsharp
// File: DSPilot.Engine/Tracking/StateTransition.fs

module StateTransition =

    // ... (기존 Step 2 코드)

    /// OutTag Rising Edge 처리 (✨ Step 3: 통계 계산 추가)
    let handleOutTagRisingEdge
        (dbPath: string)
        (call: DspCallEntity)
        (timestamp: DateTime)
        : Async<unit> = async {

        // 1. Duration 계산
        let durationMs =
            match call.LastStartAt with
            | Some startTime ->
                let duration = (timestamp - startTime).TotalMilliseconds
                Some duration
            | None ->
                printfn $"[WARN] OutTag Rising but no LastStartAt for Call: {call.CallName}"
                None

        // 2. Call State 업데이트 (Done)
        do! DspRepository.patchCall dbPath {
            CallName = call.CallName
            State = Some "Done"
            LastFinishAt = Some timestamp
            LastDurationMs = durationMs
        }

        // ✨ 3. Call 통계 업데이트 (Welford's Method)
        match durationMs with
        | Some duration ->
            do! DspRepository.updateCallStatistics dbPath call.CallName duration
        | None -> ()

        // 4. Flow ActiveCallCount 감소
        do! DspRepository.decrementActiveCallCount dbPath call.FlowName

        // 5. Flow State 재계산
        do! FlowStateCalculator.recalculateFlowState dbPath call.FlowName
    }
```

---

## 📊 Step 3.5: Flow MT/WT/CT 계산

### Flow Aggregation Module

```fsharp
// File: DSPilot.Engine/Statistics/FlowAggregation.fs

namespace DSPilot.Engine.Statistics

open DSPilot.Engine.Database

/// Flow의 MT/WT/CT 재계산
module FlowAggregation =

    /// Flow의 모든 Call 통계를 집계하여 MT/WT/CT 계산
    let recalculateFlowMetrics (dbPath: string) (flowName: string) : Async<unit> = async {

        // 1. Flow의 모든 Call 조회
        let! calls = DspRepository.getCallsByFlow dbPath flowName

        // 2. MT: 모든 Call의 평균 GoingTime 중 최대값
        let mt =
            calls
            |> List.map (fun c -> c.AverageGoingTime)
            |> List.filter (fun avg -> avg > 0.0)
            |> function
               | [] -> 0.0
               | times -> List.max times

        // 3. WT: 모든 Call의 평균 GoingTime 합
        let wt =
            calls
            |> List.sumBy (fun c -> c.AverageGoingTime)

        // 4. CT: MT (실제로는 Cycle Analysis에서 정확히 계산해야 함)
        // 현재는 간단히 MT = CT로 설정
        let ct = mt

        // 5. Flow 업데이트
        do! DspRepository.patchFlow dbPath {
            FlowName = flowName
            MT = Some mt
            WT = Some wt
            CT = Some ct
        }
    }

    /// 모든 Flow의 메트릭 재계산 (Batch)
    let recalculateAllFlowMetrics (dbPath: string) : Async<unit> = async {
        let! flows = DspRepository.getAllFlows dbPath
        for flow in flows do
            do! recalculateFlowMetrics dbPath flow.FlowName
    }
```

### Repository Patch Methods 확장

```fsharp
// File: DSPilot.Engine/Database/Repository.fs

module DspRepository =

    type FlowPatch =
        { FlowName: string
          State: string option
          ActiveCallCount: int option
          // ✨ Step 3: MT/WT/CT fields
          MT: float option
          WT: float option
          CT: float option }

    let patchFlow (dbPath: string) (patch: FlowPatch) : Async<unit> = async {
        use conn = new SQLiteConnection($"Data Source={dbPath}")
        do! conn.OpenAsync() |> Async.AwaitTask

        let mutable setParts = []
        let mutable parameters = dict [ "FlowName", box patch.FlowName ]

        patch.State |> Option.iter (fun v ->
            setParts <- setParts @ ["State = @State"]
            parameters <- parameters.Add("State", box v))

        patch.ActiveCallCount |> Option.iter (fun v ->
            setParts <- setParts @ ["ActiveCallCount = @ActiveCallCount"]
            parameters <- parameters.Add("ActiveCallCount", box v))

        // ✨ Step 3: MT/WT/CT
        patch.MT |> Option.iter (fun v ->
            setParts <- setParts @ ["MT = @MT"]
            parameters <- parameters.Add("MT", box v))

        patch.WT |> Option.iter (fun v ->
            setParts <- setParts @ ["WT = @WT"]
            parameters <- parameters.Add("WT", box v))

        patch.CT |> Option.iter (fun v ->
            setParts <- setParts @ ["CT = @CT"]
            parameters <- parameters.Add("CT", box v))

        if setParts.IsEmpty then return ()

        let sql = $"""
            UPDATE dspFlow
            SET {String.concat ", " setParts}, UpdatedAt = @Now
            WHERE FlowName = @FlowName
        """
        parameters <- parameters.Add("Now", box (DateTime.UtcNow.ToString("o")))

        do! conn.ExecuteAsync(sql, parameters) |> Async.AwaitTask |> Async.Ignore
    }
```

---

## 🎨 Step 3.6: UI - Statistics Display

### Process Status Page에 통계 표시 추가

```razor
<!-- File: DSPilot/Components/Pages/ProcessStatus.razor -->

@page "/process-status"
@using DSPilot.Adapters
@inject DspRepositoryAdapter Repo

<div class="flow-grid">
    @foreach (var flow in flows)
    {
        <div class="flow-card @GetStateClass(flow.State)">
            <h3>@flow.FlowName</h3>
            <div class="flow-state">@flow.State</div>
            <div class="flow-active">Active: @flow.ActiveCallCount</div>

            <!-- ✨ Step 3: Statistics -->
            @if (flow.MT > 0)
            {
                <div class="flow-stats">
                    <div class="stat-item">
                        <span class="stat-label">MT:</span>
                        <span class="stat-value">@flow.MT.ToString("F0") ms</span>
                    </div>
                    <div class="stat-item">
                        <span class="stat-label">WT:</span>
                        <span class="stat-value">@flow.WT.ToString("F0") ms</span>
                    </div>
                    <div class="stat-item">
                        <span class="stat-label">CT:</span>
                        <span class="stat-value">@flow.CT.ToString("F0") ms</span>
                    </div>
                    @if (flow.CycleCount > 0)
                    {
                        <div class="stat-item">
                            <span class="stat-label">Avg CT:</span>
                            <span class="stat-value">@flow.AverageCT.ToString("F0") ± @flow.StdDevCT.ToString("F0") ms</span>
                        </div>
                        <div class="stat-item">
                            <span class="stat-label">Cycles:</span>
                            <span class="stat-value">@flow.CycleCount</span>
                        </div>
                    }
                </div>
            }
        </div>
    }
</div>

@code {
    private List<DspFlowEntity> flows = new();
    private System.Threading.Timer? timer;

    protected override async Task OnInitializedAsync()
    {
        await LoadData();
        timer = new System.Threading.Timer(async _ => {
            await InvokeAsync(async () => {
                await LoadData();
                StateHasChanged();
            });
        }, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    private async Task LoadData()
    {
        flows = await Repo.GetAllFlows();
    }

    private string GetStateClass(string state) => state.ToLower() switch {
        "going" => "state-going",
        "error" => "state-error",
        _ => "state-ready"
    };

    public void Dispose() => timer?.Dispose();
}
```

### CSS for Statistics

```css
/* File: DSPilot/wwwroot/css/process-status.css */

.flow-stats {
    margin-top: 12px;
    padding-top: 12px;
    border-top: 1px solid rgba(255, 255, 255, 0.1);
}

.stat-item {
    display: flex;
    justify-content: space-between;
    margin: 4px 0;
    font-size: 13px;
}

.stat-label {
    color: rgba(255, 255, 255, 0.7);
    font-weight: 500;
}

.stat-value {
    color: #fff;
    font-weight: 600;
    font-family: 'Consolas', monospace;
}
```

---

## ✅ Step 3.7: Verification Checklist

### 1. Database Migration

```bash
# Migration 003 실행 확인
sqlite3 plc.db ".schema dspCall" | grep -E "Average|StdDev|Min|Max|GoingCount|M2"
sqlite3 plc.db ".schema dspFlow" | grep -E "MT|WT|CT|AverageCT|StdDevCT|CycleCount"
```

Expected output:
```
AverageGoingTime REAL DEFAULT 0,
StdDevGoingTime REAL DEFAULT 0,
MinGoingTime REAL,
MaxGoingTime REAL,
GoingCount INTEGER DEFAULT 0,
GoingTimeM2 REAL DEFAULT 0,
MT REAL DEFAULT 0,
WT REAL DEFAULT 0,
CT REAL DEFAULT 0,
...
```

### 2. Welford's Method Unit Test

```fsharp
// File: DSPilot.Engine.Tests/IncrementalStatsTests.fs

[<Fact>]
let ``Welford's Method - 3 values - correct mean and stddev`` () =
    // Values: 10, 20, 30 → Mean: 20, StdDev: 8.165
    let result1 = IncrementalStats.update 0 0.0 0.0 None None 10.0
    let result2 = IncrementalStats.update result1.Count result1.Mean result1.M2 result1.Min result1.Max 20.0
    let result3 = IncrementalStats.update result2.Count result2.Mean result2.M2 result2.Min result2.Max 30.0

    Assert.Equal(3, result3.Count)
    Assert.Equal(20.0, result3.Mean, 2)
    Assert.Equal(8.165, result3.StdDev, 2) // sqrt(200/3) ≈ 8.165
    Assert.Equal(Some 10.0, result3.Min)
    Assert.Equal(Some 30.0, result3.Max)
```

### 3. Statistics Update Test

```bash
# PlcEventSimulator 실행 후 통계 확인
sqlite3 plc.db "
SELECT CallName,
       GoingCount,
       AverageGoingTime,
       StdDevGoingTime,
       MinGoingTime,
       MaxGoingTime
FROM dspCall
WHERE GoingCount > 0
"
```

Expected output (after 10 cycles):
```
Work1|10|2000.0|50.2|1950.0|2100.0
Work2|10|1500.0|30.1|1480.0|1550.0
```

### 4. Flow Metrics Test

```bash
sqlite3 plc.db "
SELECT FlowName,
       MT,
       WT,
       CT,
       AverageCT,
       StdDevCT,
       CycleCount
FROM dspFlow
WHERE CycleCount > 0
"
```

Expected output:
```
Flow1|2000.0|3500.0|2000.0|2010.5|45.3|10
```

### 5. UI Verification

- [ ] Process Status 페이지에서 MT/WT/CT 표시 확인
- [ ] Average CT ± StdDev 표시 확인
- [ ] Cycle Count 표시 확인
- [ ] 실시간 업데이트 확인 (100ms 폴링)

---

## 🎯 Step 3 완료 조건

- [x] Migration 003 실행 (통계 필드 추가)
- [x] IncrementalStats 모듈 구현 (Welford's Method)
- [x] DspRepository.updateCallStatistics 구현
- [x] DspRepository.updateFlowCycleTimeStatistics 구현
- [x] StateTransition.handleOutTagRisingEdge에 통계 업데이트 추가
- [x] FlowAggregation 모듈 구현 (MT/WT/CT 계산)
- [x] UI에 통계 표시 추가
- [x] 통계 계산 정확성 검증 (Unit Test)
- [x] 실시간 통계 업데이트 확인

---

## 🚀 Next Step: Step 4

**Step 4: Bottleneck Detection**
- SlowCycleFlag 계산 (StdDev 기반)
- FocusScore 계산 (Error: +100, Unmapped: +70, Slow: +50, High StdDev: +30)
- Bottleneck 알고리즘 (Critical Path 분석)
- Bottleneck Analysis UI

---

## 📝 Notes

1. **Welford's Method**: O(1) 시간복잡도로 평균과 표준편차를 증분 업데이트
2. **M2 필드**: Sum of squared differences, 표준편차 계산에 필요
3. **MT/WT/CT**: 현재는 간단히 계산, Step 5에서 Cycle Analysis로 정확히 계산
4. **ALTER TABLE**: DROP TABLE 금지, 누적 통계 보존

