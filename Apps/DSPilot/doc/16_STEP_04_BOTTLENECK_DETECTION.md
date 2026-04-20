# Step 4: Bottleneck Detection Implementation

## 📋 목표

Step 3에서 구현한 통계 계산에 **병목 감지** 로직을 추가합니다.

- **SlowCycleFlag**: 표준편차 기반 느린 Call 감지
- **FocusScore**: 우선순위 점수 계산 (Error: +100, Unmapped: +70, Slow: +50, High StdDev: +30)
- **Critical Path Analysis**: Flow의 병목 구간 찾기
- **Bottleneck Analysis UI**: 병목 구간 시각화

---

## 🗄️ Step 4.1: Database Schema Extension

### Migration 004: Add Bottleneck Fields

```sql
-- File: DSPilot.Engine/Database/Migrations/004_add_bottleneck_fields.sql

-- Call 레벨 병목 지표
ALTER TABLE dspCall ADD COLUMN SlowCycleFlag INTEGER DEFAULT 0;
ALTER TABLE dspCall ADD COLUMN FocusScore INTEGER DEFAULT 0;
ALTER TABLE dspCall ADD COLUMN IsCriticalPath INTEGER DEFAULT 0;
ALTER TABLE dspCall ADD COLUMN BottleneckReason TEXT;

-- Flow 레벨 병목 지표
ALTER TABLE dspFlow ADD COLUMN SlowCycleFlag INTEGER DEFAULT 0;
ALTER TABLE dspFlow ADD COLUMN BottleneckCallName TEXT;
ALTER TABLE dspFlow ADD COLUMN CriticalPathDuration REAL DEFAULT 0;
```

### Entity Updates

```fsharp
// File: DSPilot.Engine/Database/Entities.fs

type DspCallEntity =
    { // ... (기존 필드들)
      // Step 3 통계 필드들
      AverageGoingTime: float
      StdDevGoingTime: float
      MinGoingTime: float option
      MaxGoingTime: float option
      GoingCount: int
      GoingTimeM2: float
      // ✨ Step 4: Bottleneck fields
      SlowCycleFlag: int
      FocusScore: int
      IsCriticalPath: int
      BottleneckReason: string option
      CreatedAt: DateTime
      UpdatedAt: DateTime }

type DspFlowEntity =
    { // ... (기존 필드들)
      // Step 3 통계 필드들
      MT: float
      WT: float
      CT: float
      AverageCT: float
      StdDevCT: float
      CycleCount: int
      CycleTimeM2: float
      // ✨ Step 4: Bottleneck fields
      SlowCycleFlag: int
      BottleneckCallName: string option
      CriticalPathDuration: float
      CreatedAt: DateTime
      UpdatedAt: DateTime }
```

---

## 🔍 Step 4.2: Bottleneck Detection Module

### Core Types

```fsharp
// File: DSPilot.Engine/Analysis/BottleneckDetection.fs

namespace DSPilot.Engine.Analysis

/// 병목 감지 이유
type BottleneckReason =
    | ErrorState
    | UnmappedTag
    | SlowExecution
    | HighVariance
    | CriticalPath

/// Focus Score 계산 결과
type FocusScoreResult =
    { CallName: string
      Score: int
      Reasons: BottleneckReason list
      Description: string }

/// 병목 감지 설정
type BottleneckConfig =
    { SlowThresholdStdDev: float // 느린 Call 기준 (평균 + N * StdDev)
      HighVarianceThresholdPct: float // 높은 분산 기준 (StdDev / Mean > X%)
      MinSampleSize: int } // 최소 샘플 수

    static member Default =
        { SlowThresholdStdDev = 2.0
          HighVarianceThresholdPct = 0.3
          MinSampleSize = 5 }
```

### SlowCycleFlag Calculation

```fsharp
// File: DSPilot.Engine/Analysis/BottleneckDetection.fs

module BottleneckDetection =

    /// SlowCycleFlag 계산: 평균 + 2 * StdDev 초과 여부
    let calculateSlowCycleFlag
        (config: BottleneckConfig)
        (call: DspCallEntity)
        : bool =

        // 최소 샘플 수 확인
        if call.GoingCount < config.MinSampleSize then
            false
        else
            let threshold = call.AverageGoingTime + config.SlowThresholdStdDev * call.StdDevGoingTime

            match call.LastDurationMs with
            | Some lastDuration -> lastDuration > threshold
            | None -> false

    /// HighVariance 확인: StdDev / Mean > 30%
    let hasHighVariance
        (config: BottleneckConfig)
        (call: DspCallEntity)
        : bool =

        if call.GoingCount < config.MinSampleSize then
            false
        elif call.AverageGoingTime = 0.0 then
            false
        else
            let coefficientOfVariation = call.StdDevGoingTime / call.AverageGoingTime
            coefficientOfVariation > config.HighVarianceThresholdPct

    /// FocusScore 계산
    let calculateFocusScore
        (config: BottleneckConfig)
        (call: DspCallEntity)
        : FocusScoreResult =

        let mutable score = 0
        let mutable reasons = []

        // 1. Error State: +100
        if call.State = "Error" then
            score <- score + 100
            reasons <- reasons @ [ErrorState]

        // 2. Unmapped Tag: +70
        if String.IsNullOrEmpty(call.InTag) || String.IsNullOrEmpty(call.OutTag) then
            score <- score + 70
            reasons <- reasons @ [UnmappedTag]

        // 3. Slow Execution: +50
        if calculateSlowCycleFlag config call then
            score <- score + 50
            reasons <- reasons @ [SlowExecution]

        // 4. High Variance: +30
        if hasHighVariance config call then
            score <- score + 30
            reasons <- reasons @ [HighVariance]

        // 5. Critical Path: +100
        if call.IsCriticalPath = 1 then
            score <- score + 100
            reasons <- reasons @ [CriticalPath]

        let description =
            reasons
            |> List.map (function
                | ErrorState -> "Error"
                | UnmappedTag -> "Unmapped"
                | SlowExecution -> "Slow"
                | HighVariance -> "High Variance"
                | CriticalPath -> "Critical Path")
            |> String.concat ", "

        { CallName = call.CallName
          Score = score
          Reasons = reasons
          Description = description }
```

---

## 🛤️ Step 4.3: Critical Path Analysis

### Critical Path Algorithm

```fsharp
// File: DSPilot.Engine/Analysis/CriticalPathAnalysis.fs

namespace DSPilot.Engine.Analysis

open DSPilot.Engine.Database

/// Critical Path 분석 (병렬 실행 고려하지 않음, 순차 실행 가정)
module CriticalPathAnalysis =

    /// Flow의 Critical Path 찾기
    let findCriticalPath
        (dbPath: string)
        (flowName: string)
        : Async<DspCallEntity list> = async {

        // 1. Flow의 모든 Call 조회
        let! calls = DspRepository.getCallsByFlow dbPath flowName

        // 2. AverageGoingTime 기준으로 정렬 (내림차순)
        let sortedCalls =
            calls
            |> List.filter (fun c -> c.AverageGoingTime > 0.0)
            |> List.sortByDescending (fun c -> c.AverageGoingTime)

        // 3. Critical Path: 상위 20% 또는 최소 1개
        let criticalPathSize = max 1 (sortedCalls.Length / 5)
        let criticalPath = sortedCalls |> List.take criticalPathSize

        return criticalPath
    }

    /// Critical Path의 Call들을 IsCriticalPath = 1로 업데이트
    let markCriticalPath
        (dbPath: string)
        (flowName: string)
        : Async<unit> = async {

        // 1. 기존 Critical Path 초기화
        do! DspRepository.clearCriticalPathFlags dbPath flowName

        // 2. Critical Path 찾기
        let! criticalPath = findCriticalPath dbPath flowName

        // 3. Critical Path 마킹
        for call in criticalPath do
            do! DspRepository.patchCall dbPath {
                CallName = call.CallName
                IsCriticalPath = Some 1
                BottleneckReason = Some "Critical Path: High Average Time"
            }

        // 4. Flow의 BottleneckCallName 업데이트 (가장 느린 Call)
        match criticalPath with
        | head :: _ ->
            do! DspRepository.patchFlow dbPath {
                FlowName = flowName
                BottleneckCallName = Some head.CallName
                CriticalPathDuration = Some head.AverageGoingTime
            }
        | [] -> ()
    }

    /// 모든 Flow의 Critical Path 재계산
    let recalculateAllCriticalPaths (dbPath: string) : Async<unit> = async {
        let! flows = DspRepository.getAllFlows dbPath
        for flow in flows do
            do! markCriticalPath dbPath flow.FlowName
    }
```

### Repository Extensions

```fsharp
// File: DSPilot.Engine/Database/Repository.fs

module DspRepository =

    // ... (기존 메서드들)

    /// Flow의 모든 Call의 IsCriticalPath를 0으로 초기화
    let clearCriticalPathFlags (dbPath: string) (flowName: string) : Async<unit> = async {
        use conn = new SQLiteConnection($"Data Source={dbPath}")
        do! conn.OpenAsync() |> Async.AwaitTask

        let sql = """
            UPDATE dspCall
            SET IsCriticalPath = 0,
                BottleneckReason = NULL,
                UpdatedAt = @Now
            WHERE FlowName = @FlowName
        """
        do! conn.ExecuteAsync(sql, {|
            FlowName = flowName
            Now = DateTime.UtcNow.ToString("o")
        |}) |> Async.AwaitTask |> Async.Ignore
    }

    /// Call Patch에 Bottleneck 필드 추가
    type CallPatch =
        { CallName: string
          State: string option
          LastStartAt: DateTime option
          LastFinishAt: DateTime option
          LastDurationMs: float option
          // ✨ Step 4: Bottleneck fields
          SlowCycleFlag: int option
          FocusScore: int option
          IsCriticalPath: int option
          BottleneckReason: string option }

    let patchCall (dbPath: string) (patch: CallPatch) : Async<unit> = async {
        // ... (기존 로직 + 아래 필드 추가)

        patch.SlowCycleFlag |> Option.iter (fun v ->
            setParts <- setParts @ ["SlowCycleFlag = @SlowCycleFlag"]
            parameters <- parameters.Add("SlowCycleFlag", box v))

        patch.FocusScore |> Option.iter (fun v ->
            setParts <- setParts @ ["FocusScore = @FocusScore"]
            parameters <- parameters.Add("FocusScore", box v))

        patch.IsCriticalPath |> Option.iter (fun v ->
            setParts <- setParts @ ["IsCriticalPath = @IsCriticalPath"]
            parameters <- parameters.Add("IsCriticalPath", box v))

        patch.BottleneckReason |> Option.iter (fun v ->
            setParts <- setParts @ ["BottleneckReason = @BottleneckReason"]
            parameters <- parameters.Add("BottleneckReason", box v))

        // ... (나머지 UPDATE SQL 실행)
    }
```

---

## 🔄 Step 4.4: Bottleneck Update Pipeline

### OutTag Rising Edge에 병목 감지 추가

```fsharp
// File: DSPilot.Engine/Tracking/StateTransition.fs

module StateTransition =

    // ... (기존 Step 2, 3 코드)

    /// OutTag Rising Edge 처리 (✨ Step 4: 병목 감지 추가)
    let handleOutTagRisingEdge
        (dbPath: string)
        (call: DspCallEntity)
        (timestamp: DateTime)
        : Async<unit> = async {

        // 1-3. Duration 계산, Call State 업데이트, Call 통계 업데이트 (Step 3)
        let durationMs = // ... (동일)
        do! DspRepository.patchCall dbPath { /* State = Done */ }
        match durationMs with
        | Some duration -> do! DspRepository.updateCallStatistics dbPath call.CallName duration
        | None -> ()

        // 4. Flow ActiveCallCount 감소, State 재계산
        do! DspRepository.decrementActiveCallCount dbPath call.FlowName
        do! FlowStateCalculator.recalculateFlowState dbPath call.FlowName

        // ✨ 5. Bottleneck 감지 (Step 4)
        let! updatedCall = DspRepository.getCallByName dbPath call.CallName

        let config = BottleneckConfig.Default
        let slowFlag = if BottleneckDetection.calculateSlowCycleFlag config updatedCall then 1 else 0
        let focusScore = BottleneckDetection.calculateFocusScore config updatedCall

        do! DspRepository.patchCall dbPath {
            CallName = call.CallName
            SlowCycleFlag = Some slowFlag
            FocusScore = Some focusScore.Score
            BottleneckReason = if focusScore.Score > 0 then Some focusScore.Description else None
        }

        // ✨ 6. Flow의 Critical Path 재계산 (Step 4)
        // 매 이벤트마다 계산하면 비효율적이므로, 주기적으로 또는 수동으로 실행
        // 여기서는 생략, 별도 타이머로 실행
    }
```

### Background Service: Periodic Critical Path Update

```csharp
// File: DSPilot/Services/BottleneckAnalysisService.cs

using Microsoft.Extensions.Hosting;
using DSPilot.Adapters;

namespace DSPilot.Services;

/// Critical Path를 주기적으로 재계산하는 백그라운드 서비스
public class BottleneckAnalysisService : BackgroundService
{
    private readonly Ev2BootstrapServiceAdapter _bootstrap;
    private readonly ILogger<BottleneckAnalysisService> _logger;
    private readonly TimeSpan _updateInterval = TimeSpan.FromSeconds(10);

    public BottleneckAnalysisService(
        Ev2BootstrapServiceAdapter bootstrap,
        ILogger<BottleneckAnalysisService> logger)
    {
        _bootstrap = bootstrap;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BottleneckAnalysisService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_updateInterval, stoppingToken);

                // Critical Path 재계산
                await _bootstrap.RecalculateAllCriticalPaths();

                _logger.LogDebug("Critical Path recalculated");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in BottleneckAnalysisService");
            }
        }
    }
}
```

### DI Registration

```csharp
// File: DSPilot/Program.cs

builder.Services.AddHostedService<BottleneckAnalysisService>();
```

---

## 🎨 Step 4.5: Bottleneck Analysis UI

### Bottleneck Analysis Page

```razor
<!-- File: DSPilot/Components/Pages/BottleneckAnalysis.razor -->

@page "/bottleneck-analysis"
@using DSPilot.Adapters
@inject DspRepositoryAdapter Repo

<div class="bottleneck-container">
    <h2>🔍 Bottleneck Analysis</h2>

    @foreach (var flow in flows)
    {
        <div class="flow-section">
            <h3>@flow.FlowName</h3>

            @if (!string.IsNullOrEmpty(flow.BottleneckCallName))
            {
                <div class="bottleneck-summary">
                    <span class="label">Main Bottleneck:</span>
                    <span class="value">@flow.BottleneckCallName</span>
                    <span class="duration">(@flow.CriticalPathDuration.ToString("F0") ms)</span>
                </div>
            }

            <div class="call-list">
                @foreach (var call in GetCallsByFlow(flow.FlowName).OrderByDescending(c => c.FocusScore))
                {
                    <div class="call-card @GetCallClass(call)">
                        <div class="call-header">
                            <h4>@call.CallName</h4>
                            <div class="focus-score">@call.FocusScore</div>
                        </div>

                        <div class="call-stats">
                            <div class="stat-row">
                                <span>Avg Time:</span>
                                <span>@call.AverageGoingTime.ToString("F0") ± @call.StdDevGoingTime.ToString("F0") ms</span>
                            </div>
                            <div class="stat-row">
                                <span>Min/Max:</span>
                                <span>@call.MinGoingTime?.ToString("F0") / @call.MaxGoingTime?.ToString("F0") ms</span>
                            </div>
                            <div class="stat-row">
                                <span>Count:</span>
                                <span>@call.GoingCount</span>
                            </div>
                        </div>

                        @if (call.SlowCycleFlag == 1)
                        {
                            <div class="warning-badge slow">⚠️ Slow</div>
                        }

                        @if (call.IsCriticalPath == 1)
                        {
                            <div class="warning-badge critical">🔴 Critical Path</div>
                        }

                        @if (!string.IsNullOrEmpty(call.BottleneckReason))
                        {
                            <div class="bottleneck-reason">@call.BottleneckReason</div>
                        }
                    </div>
                }
            </div>
        </div>
    }
</div>

@code {
    private List<DspFlowEntity> flows = new();
    private List<DspCallEntity> calls = new();
    private System.Threading.Timer? timer;

    protected override async Task OnInitializedAsync()
    {
        await LoadData();
        timer = new System.Threading.Timer(async _ => {
            await InvokeAsync(async () => {
                await LoadData();
                StateHasChanged();
            });
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private async Task LoadData()
    {
        flows = await Repo.GetAllFlows();
        calls = await Repo.GetAllCalls();
    }

    private List<DspCallEntity> GetCallsByFlow(string flowName) =>
        calls.Where(c => c.FlowName == flowName).ToList();

    private string GetCallClass(DspCallEntity call)
    {
        if (call.FocusScore >= 100) return "high-priority";
        if (call.FocusScore >= 50) return "medium-priority";
        return "low-priority";
    }

    public void Dispose() => timer?.Dispose();
}
```

### CSS for Bottleneck Analysis

```css
/* File: DSPilot/wwwroot/css/bottleneck-analysis.css */

.bottleneck-container {
    padding: 20px;
}

.flow-section {
    margin-bottom: 30px;
    border: 1px solid #444;
    border-radius: 8px;
    padding: 16px;
    background: #1e1e1e;
}

.bottleneck-summary {
    margin: 12px 0;
    padding: 12px;
    background: rgba(255, 0, 0, 0.1);
    border-left: 4px solid #ff4444;
    border-radius: 4px;
}

.bottleneck-summary .label {
    font-weight: bold;
    color: #ff4444;
}

.bottleneck-summary .value {
    margin-left: 8px;
    font-size: 16px;
    color: #fff;
}

.bottleneck-summary .duration {
    margin-left: 8px;
    color: #aaa;
    font-family: 'Consolas', monospace;
}

.call-list {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
    gap: 16px;
    margin-top: 16px;
}

.call-card {
    border: 1px solid #555;
    border-radius: 6px;
    padding: 12px;
    background: #2a2a2a;
    position: relative;
}

.call-card.high-priority {
    border-color: #ff4444;
    background: rgba(255, 68, 68, 0.1);
}

.call-card.medium-priority {
    border-color: #ffaa44;
    background: rgba(255, 170, 68, 0.1);
}

.call-card.low-priority {
    border-color: #4444ff;
    background: rgba(68, 68, 255, 0.05);
}

.call-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 12px;
}

.call-header h4 {
    margin: 0;
    font-size: 16px;
}

.focus-score {
    background: #ff4444;
    color: #fff;
    padding: 4px 10px;
    border-radius: 12px;
    font-weight: bold;
    font-size: 14px;
}

.call-stats {
    margin: 8px 0;
}

.stat-row {
    display: flex;
    justify-content: space-between;
    margin: 4px 0;
    font-size: 13px;
}

.stat-row span:first-child {
    color: #aaa;
}

.stat-row span:last-child {
    font-family: 'Consolas', monospace;
    color: #fff;
}

.warning-badge {
    display: inline-block;
    margin: 4px 4px 0 0;
    padding: 4px 8px;
    border-radius: 4px;
    font-size: 12px;
    font-weight: bold;
}

.warning-badge.slow {
    background: #ffaa44;
    color: #000;
}

.warning-badge.critical {
    background: #ff4444;
    color: #fff;
}

.bottleneck-reason {
    margin-top: 8px;
    padding: 8px;
    background: rgba(255, 255, 255, 0.05);
    border-radius: 4px;
    font-size: 12px;
    color: #ccc;
    font-style: italic;
}
```

---

## ✅ Step 4.6: Verification Checklist

### 1. Database Migration

```bash
sqlite3 plc.db ".schema dspCall" | grep -E "SlowCycleFlag|FocusScore|IsCriticalPath|BottleneckReason"
sqlite3 plc.db ".schema dspFlow" | grep -E "SlowCycleFlag|BottleneckCallName|CriticalPathDuration"
```

Expected output:
```
SlowCycleFlag INTEGER DEFAULT 0,
FocusScore INTEGER DEFAULT 0,
IsCriticalPath INTEGER DEFAULT 0,
BottleneckReason TEXT,
...
```

### 2. SlowCycleFlag Test

```bash
# PlcEventSimulator로 10 cycles 실행 후
sqlite3 plc.db "
SELECT CallName,
       AverageGoingTime,
       StdDevGoingTime,
       LastDurationMs,
       SlowCycleFlag
FROM dspCall
WHERE GoingCount > 5
"
```

Expected output:
```
Work1|2000.0|50.0|2150.0|1   -- Slow (> 평균 + 2*StdDev)
Work2|1500.0|30.0|1520.0|0   -- Normal
```

### 3. FocusScore Test

```bash
sqlite3 plc.db "
SELECT CallName,
       FocusScore,
       SlowCycleFlag,
       IsCriticalPath,
       BottleneckReason
FROM dspCall
WHERE FocusScore > 0
ORDER BY FocusScore DESC
"
```

Expected output:
```
Work1|150|1|1|Slow, Critical Path
Work3|70|0|0|Unmapped
Work2|30|0|0|High Variance
```

### 4. Critical Path Test

```bash
sqlite3 plc.db "
SELECT FlowName,
       BottleneckCallName,
       CriticalPathDuration
FROM dspFlow
WHERE BottleneckCallName IS NOT NULL
"
```

Expected output:
```
Flow1|Work1|2000.0
```

### 5. UI Verification

- [ ] Bottleneck Analysis 페이지 접근 확인
- [ ] Flow별 Bottleneck Call 표시 확인
- [ ] FocusScore 기준 정렬 확인 (높은 순)
- [ ] SlowCycleFlag 배지 표시 확인
- [ ] Critical Path 배지 표시 확인
- [ ] BottleneckReason 표시 확인
- [ ] 실시간 업데이트 확인 (1초 폴링)

### 6. Background Service Test

```bash
# 로그에서 Critical Path 재계산 확인
grep "Critical Path recalculated" dspilot.log
```

Expected output (10초마다):
```
[12:00:10] Critical Path recalculated
[12:00:20] Critical Path recalculated
[12:00:30] Critical Path recalculated
```

---

## 🎯 Step 4 완료 조건

- [x] Migration 004 실행 (병목 필드 추가)
- [x] BottleneckDetection 모듈 구현 (SlowCycleFlag, FocusScore)
- [x] CriticalPathAnalysis 모듈 구현
- [x] StateTransition에 병목 감지 추가
- [x] BottleneckAnalysisService 구현 (주기적 Critical Path 재계산)
- [x] Bottleneck Analysis UI 구현
- [x] FocusScore 계산 정확성 검증
- [x] Critical Path 감지 확인

---

## 🚀 Next Step: Step 5

**Step 5: Cycle Time Analysis (Gantt Chart)**
- Cycle 경계 감지 (모든 Call Done → Ready)
- Gantt Layout 계산 (Y축 위치, 병렬 실행 감지)
- Gantt Chart UI (Canvas 기반 렌더링)
- Cycle-by-Cycle 비교

---

## 📝 Notes

1. **FocusScore**: 여러 지표를 통합하여 우선순위 결정
2. **Critical Path**: 가장 시간이 오래 걸리는 Call들 (상위 20%)
3. **Background Service**: 매 이벤트마다 Critical Path 계산은 비효율적, 주기적으로 실행
4. **SlowCycleFlag**: 통계적 이상치 감지 (평균 + 2 * StdDev 초과)
5. **Coefficient of Variation**: StdDev / Mean, 분산의 상대적 크기 측정

