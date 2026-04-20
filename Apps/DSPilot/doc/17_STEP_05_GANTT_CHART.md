# Step 5: Cycle Time Analysis (Gantt Chart) Implementation

## 📋 목표

Step 4에서 구현한 병목 감지에 **Cycle Time Analysis**와 **Gantt Chart**를 추가합니다.

- **Cycle Boundary Detection**: 모든 Call Done → Ready 전환 시점 감지
- **Gantt Layout**: Y축 위치 계산, 병렬 실행 감지
- **Gantt Chart UI**: Canvas 기반 타임라인 렌더링
- **Cycle-by-Cycle Comparison**: 사이클별 성능 비교

---

## 🗄️ Step 5.1: Database Schema Extension

### Migration 005: Add Cycle Analysis Tables

```sql
-- File: DSPilot.Engine/Database/Migrations/005_add_cycle_tables.sql

-- Cycle 정보 저장
CREATE TABLE IF NOT EXISTS dspCycle (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    FlowName TEXT NOT NULL,
    CycleNumber INTEGER NOT NULL,
    StartTime TEXT NOT NULL,
    EndTime TEXT NOT NULL,
    DurationMs REAL NOT NULL,
    CallCount INTEGER DEFAULT 0,
    CreatedAt TEXT NOT NULL,
    UNIQUE(FlowName, CycleNumber)
);

-- Cycle 내 Call 실행 정보
CREATE TABLE IF NOT EXISTS dspCycleCall (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CycleId INTEGER NOT NULL,
    CallName TEXT NOT NULL,
    StartTime TEXT NOT NULL,
    EndTime TEXT NOT NULL,
    DurationMs REAL NOT NULL,
    YPosition INTEGER DEFAULT 0, -- Gantt Layout Y축 위치
    FOREIGN KEY (CycleId) REFERENCES dspCycle(Id)
);

CREATE INDEX idx_cycle_flow ON dspCycle(FlowName, CycleNumber);
CREATE INDEX idx_cycle_call ON dspCycleCall(CycleId);
```

### Entity Definitions

```fsharp
// File: DSPilot.Engine/Database/Entities.fs

type DspCycleEntity =
    { Id: int
      FlowName: string
      CycleNumber: int
      StartTime: DateTime
      EndTime: DateTime
      DurationMs: float
      CallCount: int
      CreatedAt: DateTime }

type DspCycleCallEntity =
    { Id: int
      CycleId: int
      CallName: string
      StartTime: DateTime
      EndTime: DateTime
      DurationMs: float
      YPosition: int }
```

---

## 🔍 Step 5.2: Cycle Detection Module

### Core Types

```fsharp
// File: DSPilot.Engine/Analysis/CycleAnalysis.fs

namespace DSPilot.Engine.Analysis

/// Cycle 경계 이벤트
type CycleBoundary =
    { FlowName: string
      Timestamp: DateTime
      BoundaryType: BoundaryType }

and BoundaryType =
    | CycleStart
    | CycleEnd

/// Cycle 진행 상태 추적
type CycleTracker() =
    let cycleStates = Dictionary<string, CycleState>()

    member this.OnCallStateChanged(flowName: string, call: DspCallEntity) : CycleBoundary option =
        // Cycle 경계 감지 로직
        ()
```

### Cycle Boundary Detection

```fsharp
// File: DSPilot.Engine/Analysis/CycleAnalysis.fs

module CycleAnalysis =

    /// Flow의 모든 Call이 Done 상태인지 확인 → Cycle End
    let detectCycleEnd
        (dbPath: string)
        (flowName: string)
        : Async<bool> = async {

        let! calls = DspRepository.getCallsByFlow dbPath flowName

        // 모든 Call이 Done이고, 최소 1개 이상 있어야 함
        let allDone =
            calls.Length > 0 &&
            calls |> List.forall (fun c -> c.State = "Done")

        return allDone
    }

    /// Flow의 어떤 Call이라도 Going으로 전환 → Cycle Start
    let detectCycleStart
        (dbPath: string)
        (flowName: string)
        : Async<bool> = async {

        let! calls = DspRepository.getCallsByFlow dbPath flowName

        // 이전에 모두 Ready/Done이었다가 Going으로 전환
        let hasGoing = calls |> List.exists (fun c -> c.State = "Going")
        return hasGoing
    }

    /// Cycle 저장
    let saveCycle
        (dbPath: string)
        (flowName: string)
        (startTime: DateTime)
        (endTime: DateTime)
        : Async<int> = async {

        use conn = new SQLiteConnection($"Data Source={dbPath}")
        do! conn.OpenAsync() |> Async.AwaitTask

        // 1. Cycle Number 결정 (기존 최대값 + 1)
        let! maxCycleNumber = async {
            let sql = "SELECT COALESCE(MAX(CycleNumber), 0) FROM dspCycle WHERE FlowName = @FlowName"
            let! result = conn.ExecuteScalarAsync<int>(sql, {| FlowName = flowName |}) |> Async.AwaitTask
            return result
        }

        let cycleNumber = maxCycleNumber + 1
        let durationMs = (endTime - startTime).TotalMilliseconds

        // 2. Cycle 저장
        let insertSql = """
            INSERT INTO dspCycle (FlowName, CycleNumber, StartTime, EndTime, DurationMs, CallCount, CreatedAt)
            VALUES (@FlowName, @CycleNumber, @StartTime, @EndTime, @DurationMs, 0, @CreatedAt)
        """
        do! conn.ExecuteAsync(insertSql, {|
            FlowName = flowName
            CycleNumber = cycleNumber
            StartTime = startTime.ToString("o")
            EndTime = endTime.ToString("o")
            DurationMs = durationMs
            CreatedAt = DateTime.UtcNow.ToString("o")
        |}) |> Async.AwaitTask |> Async.Ignore

        // 3. 저장된 Cycle ID 반환
        let! cycleId = async {
            let sql = "SELECT Id FROM dspCycle WHERE FlowName = @FlowName AND CycleNumber = @CycleNumber"
            let! result = conn.ExecuteScalarAsync<int>(sql, {| FlowName = flowName; CycleNumber = cycleNumber |}) |> Async.AwaitTask
            return result
        }

        return cycleId
    }

    /// Cycle 내 Call 실행 정보 저장
    let saveCycleCalls
        (dbPath: string)
        (cycleId: int)
        (calls: DspCallEntity list)
        : Async<unit> = async {

        use conn = new SQLiteConnection($"Data Source={dbPath}")
        do! conn.OpenAsync() |> Async.AwaitTask

        for call in calls do
            match call.LastStartAt, call.LastFinishAt with
            | Some startTime, Some endTime ->
                let durationMs = (endTime - startTime).TotalMilliseconds

                let insertSql = """
                    INSERT INTO dspCycleCall (CycleId, CallName, StartTime, EndTime, DurationMs, YPosition)
                    VALUES (@CycleId, @CallName, @StartTime, @EndTime, @DurationMs, 0)
                """
                do! conn.ExecuteAsync(insertSql, {|
                    CycleId = cycleId
                    CallName = call.CallName
                    StartTime = startTime.ToString("o")
                    EndTime = endTime.ToString("o")
                    DurationMs = durationMs
                |}) |> Async.AwaitTask |> Async.Ignore

            | _ -> () // Skip calls without timing data

        // CallCount 업데이트
        let updateSql = """
            UPDATE dspCycle
            SET CallCount = (SELECT COUNT(*) FROM dspCycleCall WHERE CycleId = @CycleId)
            WHERE Id = @CycleId
        """
        do! conn.ExecuteAsync(updateSql, {| CycleId = cycleId |}) |> Async.AwaitTask |> Async.Ignore
    }
```

---

## 📐 Step 5.3: Gantt Layout Calculation

### Layout Algorithm

```fsharp
// File: DSPilot.Engine/Analysis/GanttLayout.fs

namespace DSPilot.Engine.Analysis

/// Gantt Chart Y축 레이아웃 계산 (병렬 실행 감지)
module GanttLayout =

    type TimeRange = { Start: DateTime; End: DateTime }

    /// 두 시간 범위가 겹치는지 확인
    let overlaps (a: TimeRange) (b: TimeRange) : bool =
        a.Start < b.End && b.Start < a.End

    /// Y 위치 할당 (병렬 실행 감지, 같은 Y 레벨에 배치 불가)
    let calculateYPositions (calls: DspCycleCallEntity list) : Map<int, int> =

        // Y 레벨별 사용 중인 시간 범위
        let mutable yLevels: Map<int, TimeRange list> = Map.empty

        // Call ID → Y Position 매핑
        let mutable positions: Map<int, int> = Map.empty

        for call in calls do
            let timeRange = { Start = call.StartTime; End = call.EndTime }

            // 배치 가능한 최소 Y 레벨 찾기
            let rec findYLevel (y: int) : int =
                match yLevels.TryFind(y) with
                | None ->
                    // 빈 레벨 발견
                    y
                | Some ranges ->
                    // 겹치는지 확인
                    let hasOverlap = ranges |> List.exists (fun r -> overlaps r timeRange)
                    if hasOverlap then
                        // 다음 레벨 시도
                        findYLevel (y + 1)
                    else
                        // 겹치지 않음, 현재 레벨 사용
                        y

            let yPos = findYLevel 0

            // Y 레벨에 시간 범위 추가
            yLevels <-
                yLevels
                |> Map.change yPos (fun existing ->
                    match existing with
                    | None -> Some [timeRange]
                    | Some ranges -> Some (timeRange :: ranges))

            // 위치 기록
            positions <- positions.Add(call.Id, yPos)

        positions

    /// Gantt Layout 계산 및 DB 업데이트
    let updateGanttLayout
        (dbPath: string)
        (cycleId: int)
        : Async<unit> = async {

        // 1. Cycle의 모든 Call 조회
        let! cycleCalls = DspRepository.getCycleCallsByCycleId dbPath cycleId

        // 2. Y 위치 계산
        let positions = calculateYPositions cycleCalls

        // 3. DB 업데이트
        use conn = new SQLiteConnection($"Data Source={dbPath}")
        do! conn.OpenAsync() |> Async.AwaitTask

        for call in cycleCalls do
            match positions.TryFind(call.Id) with
            | Some yPos ->
                let updateSql = "UPDATE dspCycleCall SET YPosition = @YPosition WHERE Id = @Id"
                do! conn.ExecuteAsync(updateSql, {| YPosition = yPos; Id = call.Id |}) |> Async.AwaitTask |> Async.Ignore
            | None -> ()
    }
```

---

## 🔄 Step 5.4: Cycle Capture on State Transition

### StateTransition 확장

```fsharp
// File: DSPilot.Engine/Tracking/StateTransition.fs

module StateTransition =

    /// OutTag Rising Edge 처리 (✨ Step 5: Cycle Capture 추가)
    let handleOutTagRisingEdge
        (dbPath: string)
        (call: DspCallEntity)
        (timestamp: DateTime)
        : Async<unit> = async {

        // 1-5. 기존 Step 2, 3, 4 로직 (State, Statistics, Bottleneck)
        // ... (동일)

        // ✨ 6. Cycle End 감지 (Step 5)
        let! cycleEnded = CycleAnalysis.detectCycleEnd dbPath call.FlowName

        if cycleEnded then
            // 6.1. Cycle 시작/종료 시간 결정
            let! calls = DspRepository.getCallsByFlow dbPath call.FlowName
            let startTime = calls |> List.choose (fun c -> c.LastStartAt) |> List.min
            let endTime = calls |> List.choose (fun c -> c.LastFinishAt) |> List.max

            // 6.2. Cycle 저장
            let! cycleId = CycleAnalysis.saveCycle dbPath call.FlowName startTime endTime

            // 6.3. Cycle 내 Call 정보 저장
            do! CycleAnalysis.saveCycleCalls dbPath cycleId calls

            // 6.4. Gantt Layout 계산
            do! GanttLayout.updateGanttLayout dbPath cycleId

            // 6.5. Flow CT 통계 업데이트
            let cycleDurationMs = (endTime - startTime).TotalMilliseconds
            do! DspRepository.updateFlowCycleTimeStatistics dbPath call.FlowName cycleDurationMs
    }
```

---

## 🎨 Step 5.5: Gantt Chart UI

### Gantt Chart Page

```razor
<!-- File: DSPilot/Components/Pages/GanttChart.razor -->

@page "/gantt-chart"
@using DSPilot.Adapters
@inject DspRepositoryAdapter Repo
@inject IJSRuntime JS

<div class="gantt-container">
    <h2>📊 Gantt Chart - Cycle Time Analysis</h2>

    <div class="flow-selector">
        <label>Flow:</label>
        <select @onchange="OnFlowChanged">
            @foreach (var flow in flows)
            {
                <option value="@flow.FlowName">@flow.FlowName</option>
            }
        </select>

        @if (!string.IsNullOrEmpty(selectedFlowName))
        {
            <label style="margin-left: 20px;">Cycle:</label>
            <select @onchange="OnCycleChanged">
                @foreach (var cycle in cycles)
                {
                    <option value="@cycle.Id">Cycle #@cycle.CycleNumber (@cycle.DurationMs.ToString("F0") ms)</option>
                }
            </select>
        }
    </div>

    @if (selectedCycle != null)
    {
        <div class="gantt-summary">
            <div class="summary-item">
                <span class="label">Duration:</span>
                <span class="value">@selectedCycle.DurationMs.ToString("F0") ms</span>
            </div>
            <div class="summary-item">
                <span class="label">Call Count:</span>
                <span class="value">@selectedCycle.CallCount</span>
            </div>
            <div class="summary-item">
                <span class="label">Start:</span>
                <span class="value">@selectedCycle.StartTime.ToString("HH:mm:ss.fff")</span>
            </div>
            <div class="summary-item">
                <span class="label">End:</span>
                <span class="value">@selectedCycle.EndTime.ToString("HH:mm:ss.fff")</span>
            </div>
        </div>

        <canvas id="ganttCanvas" width="1200" height="@canvasHeight"></canvas>
    }
</div>

@code {
    private List<DspFlowEntity> flows = new();
    private List<DspCycleEntity> cycles = new();
    private DspCycleEntity? selectedCycle;
    private List<DspCycleCallEntity> cycleCalls = new();
    private string? selectedFlowName;
    private int canvasHeight = 400;

    protected override async Task OnInitializedAsync()
    {
        flows = await Repo.GetAllFlows();
        if (flows.Count > 0)
        {
            selectedFlowName = flows[0].FlowName;
            await LoadCycles();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (selectedCycle != null && cycleCalls.Count > 0)
        {
            await RenderGanttChart();
        }
    }

    private async Task OnFlowChanged(ChangeEventArgs e)
    {
        selectedFlowName = e.Value?.ToString();
        await LoadCycles();
    }

    private async Task OnCycleChanged(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out int cycleId))
        {
            selectedCycle = cycles.FirstOrDefault(c => c.Id == cycleId);
            if (selectedCycle != null)
            {
                cycleCalls = await Repo.GetCycleCallsByCycleId(cycleId);
                canvasHeight = Math.Max(400, (cycleCalls.Max(c => c.YPosition) + 1) * 60 + 100);
                StateHasChanged();
            }
        }
    }

    private async Task LoadCycles()
    {
        if (!string.IsNullOrEmpty(selectedFlowName))
        {
            cycles = await Repo.GetCyclesByFlow(selectedFlowName);
            if (cycles.Count > 0)
            {
                selectedCycle = cycles[^1]; // 최신 Cycle
                cycleCalls = await Repo.GetCycleCallsByCycleId(selectedCycle.Id);
                canvasHeight = Math.Max(400, (cycleCalls.Max(c => c.YPosition) + 1) * 60 + 100);
            }
            StateHasChanged();
        }
    }

    private async Task RenderGanttChart()
    {
        if (selectedCycle == null) return;

        var chartData = new
        {
            cycle = selectedCycle,
            calls = cycleCalls.Select(c => new
            {
                callName = c.CallName,
                startTime = c.StartTime,
                endTime = c.EndTime,
                durationMs = c.DurationMs,
                yPosition = c.YPosition
            }).ToArray()
        };

        await JS.InvokeVoidAsync("renderGanttChart", "ganttCanvas", chartData);
    }
}
```

### JavaScript Gantt Renderer

```javascript
// File: DSPilot/wwwroot/js/gantt-chart.js

window.renderGanttChart = function(canvasId, chartData) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;

    const ctx = canvas.getContext('2d');
    const width = canvas.width;
    const height = canvas.height;

    // Clear canvas
    ctx.clearRect(0, 0, width, height);

    // Constants
    const marginLeft = 150;
    const marginTop = 50;
    const marginRight = 50;
    const rowHeight = 50;

    // Time range
    const cycleStart = new Date(chartData.cycle.startTime);
    const cycleEnd = new Date(chartData.cycle.endTime);
    const cycleDuration = cycleEnd - cycleStart;

    const chartWidth = width - marginLeft - marginRight;

    // Time to X coordinate
    function timeToX(time) {
        const elapsed = new Date(time) - cycleStart;
        return marginLeft + (elapsed / cycleDuration) * chartWidth;
    }

    // Draw timeline axis
    ctx.strokeStyle = '#555';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(marginLeft, marginTop);
    ctx.lineTo(marginLeft + chartWidth, marginTop);
    ctx.stroke();

    // Draw time labels
    ctx.fillStyle = '#aaa';
    ctx.font = '12px Consolas';
    ctx.textAlign = 'center';
    for (let i = 0; i <= 10; i++) {
        const x = marginLeft + (chartWidth * i / 10);
        const timeMs = (cycleDuration * i / 10);
        ctx.fillText(timeMs.toFixed(0) + ' ms', x, marginTop - 10);
    }

    // Draw calls
    chartData.calls.forEach(call => {
        const x1 = timeToX(call.startTime);
        const x2 = timeToX(call.endTime);
        const y = marginTop + 30 + call.yPosition * rowHeight;

        // Bar
        ctx.fillStyle = '#4CAF50';
        ctx.fillRect(x1, y, x2 - x1, 30);

        // Border
        ctx.strokeStyle = '#2E7D32';
        ctx.lineWidth = 2;
        ctx.strokeRect(x1, y, x2 - x1, 30);

        // Call name
        ctx.fillStyle = '#fff';
        ctx.font = 'bold 12px Arial';
        ctx.textAlign = 'left';
        ctx.fillText(call.callName, 10, y + 20);

        // Duration
        ctx.fillStyle = '#000';
        ctx.textAlign = 'center';
        ctx.fillText(call.durationMs.toFixed(0) + ' ms', (x1 + x2) / 2, y + 20);
    });
};
```

---

## ✅ Step 5.6: Verification Checklist

### 1. Database Migration

```bash
sqlite3 plc.db ".tables" | grep -E "dspCycle|dspCycleCall"
sqlite3 plc.db ".schema dspCycle"
sqlite3 plc.db ".schema dspCycleCall"
```

Expected output:
```
dspCycle
dspCycleCall
```

### 2. Cycle Capture Test

```bash
# PlcEventSimulator로 3 cycles 실행 후
sqlite3 plc.db "
SELECT FlowName,
       CycleNumber,
       DurationMs,
       CallCount
FROM dspCycle
ORDER BY CycleNumber
"
```

Expected output:
```
Flow1|1|3500.0|2
Flow1|2|3480.0|2
Flow1|3|3520.0|2
```

### 3. Gantt Layout Test

```bash
sqlite3 plc.db "
SELECT c.CallName,
       cc.StartTime,
       cc.EndTime,
       cc.DurationMs,
       cc.YPosition
FROM dspCycleCall cc
JOIN dspCycle c ON cc.CycleId = c.Id
WHERE c.CycleNumber = 1
ORDER BY cc.StartTime
"
```

Expected output (병렬 실행 시):
```
Work1|12:00:00.000|12:00:02.000|2000.0|0
Work2|12:00:00.500|12:00:02.000|1500.0|1  -- 병렬 실행, Y=1
```

### 4. UI Verification

- [ ] Gantt Chart 페이지 접근 확인
- [ ] Flow 선택 드롭다운 동작 확인
- [ ] Cycle 선택 드롭다운 동작 확인
- [ ] Canvas에 Gantt Chart 렌더링 확인
- [ ] 병렬 실행 Call이 다른 Y 레벨에 표시되는지 확인
- [ ] Duration 라벨 표시 확인

---

## 🎯 Step 5 완료 조건

- [x] Migration 005 실행 (dspCycle, dspCycleCall 테이블 생성)
- [x] CycleAnalysis 모듈 구현 (Cycle 경계 감지, 저장)
- [x] GanttLayout 모듈 구현 (Y 위치 계산)
- [x] StateTransition에 Cycle Capture 추가
- [x] Gantt Chart UI 구현 (Canvas 렌더링)
- [x] Cycle-by-Cycle 비교 가능 확인

---

## 🚀 Next Step: Step 6

**Step 6: Deviation Analysis (Heatmap)**
- Call별 편차 데이터 수집 (실제값 - 평균값)
- Heatmap 데이터 구조 (X축: Cycle, Y축: Call, Color: Deviation)
- Heatmap UI (Canvas 기반, 색상 그라디언트)

---

## 📝 Notes

1. **Cycle Boundary**: 모든 Call Done 상태 = Cycle End
2. **Gantt Layout**: Interval Scheduling 알고리즘 (Y축 레벨 할당)
3. **Canvas Rendering**: JavaScript로 고성능 렌더링
4. **Cycle Number**: 1부터 시작, Flow별로 독립적

