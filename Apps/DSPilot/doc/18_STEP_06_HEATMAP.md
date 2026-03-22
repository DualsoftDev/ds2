# Step 6: Deviation Analysis (Heatmap) Implementation

## 📋 목표

Step 5에서 구현한 Cycle Analysis에 **편차 분석 Heatmap**을 추가합니다.

- **Deviation Data**: Call별 사이클별 편차 (실제값 - 평균값)
- **Heatmap Structure**: X축(Cycle), Y축(Call), 색상(Deviation)
- **Heatmap UI**: Canvas 기반, 색상 그라디언트
- **Outlier Detection**: 비정상 사이클 자동 감지

---

## 🗄️ Step 6.1: Database Schema Extension

### Migration 006: Add Deviation Fields

```sql
-- File: DSPilot.Engine/Database/Migrations/006_add_deviation_fields.sql

-- CycleCall에 편차 정보 추가
ALTER TABLE dspCycleCall ADD COLUMN Deviation REAL DEFAULT 0;
ALTER TABLE dspCycleCall ADD COLUMN DeviationPct REAL DEFAULT 0;
ALTER TABLE dspCycleCall ADD COLUMN IsOutlier INTEGER DEFAULT 0;

-- Cycle 레벨 이상치 플래그
ALTER TABLE dspCycle ADD COLUMN HasOutliers INTEGER DEFAULT 0;
ALTER TABLE dspCycle ADD COLUMN OutlierCount INTEGER DEFAULT 0;
```

### Entity Updates

```fsharp
// File: DSPilot.Engine/Database/Entities.fs

type DspCycleCallEntity =
    { Id: int
      CycleId: int
      CallName: string
      StartTime: DateTime
      EndTime: DateTime
      DurationMs: float
      YPosition: int
      // ✨ Step 6: Deviation fields
      Deviation: float
      DeviationPct: float
      IsOutlier: int }

type DspCycleEntity =
    { Id: int
      FlowName: string
      CycleNumber: int
      StartTime: DateTime
      EndTime: DateTime
      DurationMs: float
      CallCount: int
      // ✨ Step 6: Outlier fields
      HasOutliers: int
      OutlierCount: int
      CreatedAt: DateTime }
```

---

## 📊 Step 6.2: Deviation Calculation Module

### Core Types

```fsharp
// File: DSPilot.Engine/Analysis/DeviationAnalysis.fs

namespace DSPilot.Engine.Analysis

/// 편차 분석 결과
type DeviationResult =
    { CallName: string
      ActualValue: float
      AverageValue: float
      Deviation: float
      DeviationPct: float
      IsOutlier: bool }

/// 이상치 감지 설정
type OutlierConfig =
    { ThresholdStdDev: float } // 이상치 기준 (평균 ± N * StdDev)

    static member Default =
        { ThresholdStdDev = 2.0 }
```

### Deviation Calculation

```fsharp
// File: DSPilot.Engine/Analysis/DeviationAnalysis.fs

module DeviationAnalysis =

    /// 편차 계산: (실제값 - 평균값)
    let calculateDeviation
        (actualValue: float)
        (avgValue: float)
        : float =
        actualValue - avgValue

    /// 편차 퍼센트 계산: ((실제값 - 평균값) / 평균값 * 100)
    let calculateDeviationPct
        (actualValue: float)
        (avgValue: float)
        : float =
        if avgValue = 0.0 then 0.0
        else (actualValue - avgValue) / avgValue * 100.0

    /// 이상치 여부 판단: |실제값 - 평균값| > N * StdDev
    let isOutlier
        (config: OutlierConfig)
        (actualValue: float)
        (avgValue: float)
        (stdDev: float)
        : bool =
        let deviation = abs (actualValue - avgValue)
        deviation > config.ThresholdStdDev * stdDev

    /// CycleCall의 편차 계산 (Call의 통계 기반)
    let calculateCycleCallDeviation
        (dbPath: string)
        (cycleCallId: int)
        : Async<DeviationResult> = async {

        use conn = new SQLiteConnection($"Data Source={dbPath}")
        do! conn.OpenAsync() |> Async.AwaitTask

        // 1. CycleCall 정보 조회
        let! cycleCall = async {
            let sql = "SELECT CallName, DurationMs FROM dspCycleCall WHERE Id = @Id"
            let! result = conn.QueryFirstAsync<{| CallName: string; DurationMs: float |}>(sql, {| Id = cycleCallId |}) |> Async.AwaitTask
            return result
        }

        // 2. Call의 통계 정보 조회
        let! callStats = async {
            let sql = """
                SELECT AverageGoingTime, StdDevGoingTime
                FROM dspCall
                WHERE CallName = @CallName
            """
            let! result = conn.QueryFirstAsync<{| AverageGoingTime: float; StdDevGoingTime: float |}>(sql, {| CallName = cycleCall.CallName |}) |> Async.AwaitTask
            return result
        }

        // 3. 편차 계산
        let deviation = calculateDeviation cycleCall.DurationMs callStats.AverageGoingTime
        let deviationPct = calculateDeviationPct cycleCall.DurationMs callStats.AverageGoingTime
        let config = OutlierConfig.Default
        let outlier = isOutlier config cycleCall.DurationMs callStats.AverageGoingTime callStats.StdDevGoingTime

        return {
            CallName = cycleCall.CallName
            ActualValue = cycleCall.DurationMs
            AverageValue = callStats.AverageGoingTime
            Deviation = deviation
            DeviationPct = deviationPct
            IsOutlier = outlier
        }
    }

    /// Cycle의 모든 CycleCall 편차 업데이트
    let updateCycleDeviations
        (dbPath: string)
        (cycleId: int)
        : Async<unit> = async {

        use conn = new SQLiteConnection($"Data Source={dbPath}")
        do! conn.OpenAsync() |> Async.AwaitTask

        // 1. Cycle의 모든 CycleCall 조회
        let! cycleCalls = async {
            let sql = "SELECT Id FROM dspCycleCall WHERE CycleId = @CycleId"
            let! result = conn.QueryAsync<{| Id: int |}>(sql, {| CycleId = cycleId |}) |> Async.AwaitTask
            return result |> Seq.toList
        }

        // 2. 각 CycleCall의 편차 계산 및 업데이트
        let mutable outlierCount = 0

        for cc in cycleCalls do
            let! deviationResult = calculateCycleCallDeviation dbPath cc.Id

            if deviationResult.IsOutlier then
                outlierCount <- outlierCount + 1

            let updateSql = """
                UPDATE dspCycleCall
                SET Deviation = @Deviation,
                    DeviationPct = @DeviationPct,
                    IsOutlier = @IsOutlier
                WHERE Id = @Id
            """
            do! conn.ExecuteAsync(updateSql, {|
                Deviation = deviationResult.Deviation
                DeviationPct = deviationResult.DeviationPct
                IsOutlier = if deviationResult.IsOutlier then 1 else 0
                Id = cc.Id
            |}) |> Async.AwaitTask |> Async.Ignore

        // 3. Cycle의 이상치 플래그 업데이트
        let updateCycleSql = """
            UPDATE dspCycle
            SET HasOutliers = @HasOutliers,
                OutlierCount = @OutlierCount
            WHERE Id = @CycleId
        """
        do! conn.ExecuteAsync(updateCycleSql, {|
            HasOutliers = if outlierCount > 0 then 1 else 0
            OutlierCount = outlierCount
            CycleId = cycleId
        |}) |> Async.AwaitTask |> Async.Ignore
    }
```

---

## 🔄 Step 6.3: Update Cycle Capture Pipeline

### StateTransition 확장

```fsharp
// File: DSPilot.Engine/Tracking/StateTransition.fs

module StateTransition =

    /// OutTag Rising Edge 처리 (✨ Step 6: Deviation Analysis 추가)
    let handleOutTagRisingEdge
        (dbPath: string)
        (call: DspCallEntity)
        (timestamp: DateTime)
        : Async<unit> = async {

        // 1-5. 기존 Step 2, 3, 4, 5 로직
        // ... (동일)

        // 6. Cycle End 감지 및 Capture (Step 5)
        let! cycleEnded = CycleAnalysis.detectCycleEnd dbPath call.FlowName

        if cycleEnded then
            // ... (Step 5 로직: Cycle 저장, Gantt Layout)
            let! cycleId = CycleAnalysis.saveCycle dbPath call.FlowName startTime endTime
            do! CycleAnalysis.saveCycleCalls dbPath cycleId calls
            do! GanttLayout.updateGanttLayout dbPath cycleId
            do! DspRepository.updateFlowCycleTimeStatistics dbPath call.FlowName cycleDurationMs

            // ✨ 7. Deviation Analysis (Step 6)
            do! DeviationAnalysis.updateCycleDeviations dbPath cycleId
    }
```

---

## 🎨 Step 6.4: Heatmap UI

### Heatmap Page

```razor
<!-- File: DSPilot/Components/Pages/Heatmap.razor -->

@page "/heatmap"
@using DSPilot.Adapters
@inject DspRepositoryAdapter Repo
@inject IJSRuntime JS

<div class="heatmap-container">
    <h2>🔥 Heatmap - Deviation Analysis</h2>

    <div class="flow-selector">
        <label>Flow:</label>
        <select @onchange="OnFlowChanged">
            @foreach (var flow in flows)
            {
                <option value="@flow.FlowName">@flow.FlowName</option>
            }
        </select>
    </div>

    @if (!string.IsNullOrEmpty(selectedFlowName) && heatmapData != null)
    {
        <div class="heatmap-summary">
            <div class="summary-item">
                <span class="label">Total Cycles:</span>
                <span class="value">@heatmapData.Cycles.Count</span>
            </div>
            <div class="summary-item">
                <span class="label">Total Calls:</span>
                <span class="value">@heatmapData.CallNames.Count</span>
            </div>
            <div class="summary-item">
                <span class="label">Outliers:</span>
                <span class="value alert">@heatmapData.TotalOutliers</span>
            </div>
        </div>

        <div class="legend">
            <span class="legend-label">Deviation:</span>
            <div class="legend-gradient"></div>
            <span class="legend-min">-100%</span>
            <span class="legend-mid">0%</span>
            <span class="legend-max">+100%</span>
        </div>

        <canvas id="heatmapCanvas" width="1200" height="@canvasHeight"></canvas>
    }
</div>

@code {
    private List<DspFlowEntity> flows = new();
    private string? selectedFlowName;
    private HeatmapData? heatmapData;
    private int canvasHeight = 600;

    protected override async Task OnInitializedAsync()
    {
        flows = await Repo.GetAllFlows();
        if (flows.Count > 0)
        {
            selectedFlowName = flows[0].FlowName;
            await LoadHeatmapData();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (heatmapData != null)
        {
            await RenderHeatmap();
        }
    }

    private async Task OnFlowChanged(ChangeEventArgs e)
    {
        selectedFlowName = e.Value?.ToString();
        await LoadHeatmapData();
    }

    private async Task LoadHeatmapData()
    {
        if (string.IsNullOrEmpty(selectedFlowName)) return;

        // 1. Cycles 조회
        var cycles = await Repo.GetCyclesByFlow(selectedFlowName);

        // 2. Call 목록 조회
        var calls = await Repo.GetCallsByFlow(selectedFlowName);
        var callNames = calls.Select(c => c.CallName).Distinct().ToList();

        // 3. Heatmap 데이터 구성
        var matrix = new List<List<HeatmapCell>>();
        int totalOutliers = 0;

        foreach (var cycle in cycles)
        {
            var cycleCalls = await Repo.GetCycleCallsByCycleId(cycle.Id);
            var row = new List<HeatmapCell>();

            foreach (var callName in callNames)
            {
                var cycleCall = cycleCalls.FirstOrDefault(cc => cc.CallName == callName);
                if (cycleCall != null)
                {
                    row.Add(new HeatmapCell
                    {
                        CallName = callName,
                        CycleNumber = cycle.CycleNumber,
                        DeviationPct = cycleCall.DeviationPct,
                        IsOutlier = cycleCall.IsOutlier == 1
                    });

                    if (cycleCall.IsOutlier == 1)
                        totalOutliers++;
                }
                else
                {
                    row.Add(new HeatmapCell
                    {
                        CallName = callName,
                        CycleNumber = cycle.CycleNumber,
                        DeviationPct = 0,
                        IsOutlier = false
                    });
                }
            }

            matrix.Add(row);
        }

        heatmapData = new HeatmapData
        {
            Cycles = cycles,
            CallNames = callNames,
            Matrix = matrix,
            TotalOutliers = totalOutliers
        };

        canvasHeight = Math.Max(600, callNames.Count * 40 + 100);
        StateHasChanged();
    }

    private async Task RenderHeatmap()
    {
        if (heatmapData == null) return;

        var chartData = new
        {
            callNames = heatmapData.CallNames,
            cycles = heatmapData.Cycles.Select(c => c.CycleNumber).ToArray(),
            matrix = heatmapData.Matrix.Select(row =>
                row.Select(cell => new
                {
                    deviationPct = cell.DeviationPct,
                    isOutlier = cell.IsOutlier
                }).ToArray()
            ).ToArray()
        };

        await JS.InvokeVoidAsync("renderHeatmap", "heatmapCanvas", chartData);
    }

    public class HeatmapData
    {
        public List<DspCycleEntity> Cycles { get; set; } = new();
        public List<string> CallNames { get; set; } = new();
        public List<List<HeatmapCell>> Matrix { get; set; } = new();
        public int TotalOutliers { get; set; }
    }

    public class HeatmapCell
    {
        public string CallName { get; set; } = "";
        public int CycleNumber { get; set; }
        public double DeviationPct { get; set; }
        public bool IsOutlier { get; set; }
    }
}
```

### JavaScript Heatmap Renderer

```javascript
// File: DSPilot/wwwroot/js/heatmap.js

window.renderHeatmap = function(canvasId, chartData) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;

    const ctx = canvas.getContext('2d');
    const width = canvas.width;
    const height = canvas.height;

    // Clear canvas
    ctx.clearRect(0, 0, width, height);

    // Constants
    const marginLeft = 150;
    const marginTop = 80;
    const cellWidth = 50;
    const cellHeight = 35;

    const numCalls = chartData.callNames.length;
    const numCycles = chartData.cycles.length;

    // Color mapping: -100% (blue) → 0% (white) → +100% (red)
    function getColor(deviationPct) {
        // Clamp to [-100, 100]
        const clamped = Math.max(-100, Math.min(100, deviationPct));

        if (clamped < 0) {
            // Blue gradient
            const intensity = Math.abs(clamped) / 100;
            const r = Math.floor(255 * (1 - intensity));
            const g = Math.floor(255 * (1 - intensity));
            const b = 255;
            return `rgb(${r}, ${g}, ${b})`;
        } else {
            // Red gradient
            const intensity = clamped / 100;
            const r = 255;
            const g = Math.floor(255 * (1 - intensity));
            const b = Math.floor(255 * (1 - intensity));
            return `rgb(${r}, ${g}, ${b})`;
        }
    }

    // Draw Call Names (Y-axis)
    ctx.fillStyle = '#fff';
    ctx.font = '12px Arial';
    ctx.textAlign = 'right';
    for (let i = 0; i < numCalls; i++) {
        const y = marginTop + i * cellHeight + cellHeight / 2 + 5;
        ctx.fillText(chartData.callNames[i], marginLeft - 10, y);
    }

    // Draw Cycle Numbers (X-axis)
    ctx.textAlign = 'center';
    for (let j = 0; j < numCycles; j++) {
        const x = marginLeft + j * cellWidth + cellWidth / 2;
        ctx.fillText('C' + chartData.cycles[j], x, marginTop - 10);
    }

    // Draw Heatmap Cells
    for (let j = 0; j < numCycles; j++) {
        for (let i = 0; i < numCalls; i++) {
            const cell = chartData.matrix[j][i];
            const x = marginLeft + j * cellWidth;
            const y = marginTop + i * cellHeight;

            // Fill color
            ctx.fillStyle = getColor(cell.deviationPct);
            ctx.fillRect(x, y, cellWidth, cellHeight);

            // Border
            ctx.strokeStyle = '#333';
            ctx.lineWidth = 1;
            ctx.strokeRect(x, y, cellWidth, cellHeight);

            // Outlier indicator
            if (cell.isOutlier) {
                ctx.strokeStyle = '#ff0000';
                ctx.lineWidth = 3;
                ctx.strokeRect(x + 2, y + 2, cellWidth - 4, cellHeight - 4);
            }

            // Deviation text
            ctx.fillStyle = '#000';
            ctx.font = 'bold 10px Arial';
            ctx.textAlign = 'center';
            const text = cell.deviationPct.toFixed(0) + '%';
            ctx.fillText(text, x + cellWidth / 2, y + cellHeight / 2 + 4);
        }
    }
};
```

### CSS for Heatmap

```css
/* File: DSPilot/wwwroot/css/heatmap.css */

.heatmap-container {
    padding: 20px;
}

.heatmap-summary {
    display: flex;
    gap: 30px;
    margin: 20px 0;
    padding: 15px;
    background: #2a2a2a;
    border-radius: 8px;
}

.summary-item {
    display: flex;
    flex-direction: column;
}

.summary-item .label {
    font-size: 12px;
    color: #aaa;
    margin-bottom: 4px;
}

.summary-item .value {
    font-size: 20px;
    font-weight: bold;
    color: #fff;
}

.summary-item .value.alert {
    color: #ff4444;
}

.legend {
    display: flex;
    align-items: center;
    gap: 10px;
    margin: 20px 0;
}

.legend-label {
    font-weight: bold;
    color: #fff;
}

.legend-gradient {
    width: 200px;
    height: 20px;
    background: linear-gradient(to right,
        rgb(100, 100, 255) 0%,
        rgb(255, 255, 255) 50%,
        rgb(255, 100, 100) 100%);
    border: 1px solid #555;
}

.legend-min, .legend-mid, .legend-max {
    font-size: 12px;
    color: #aaa;
    font-family: 'Consolas', monospace;
}

canvas {
    border: 1px solid #555;
    background: #1e1e1e;
    margin-top: 20px;
}
```

---

## ✅ Step 6.5: Verification Checklist

### 1. Database Migration

```bash
sqlite3 plc.db ".schema dspCycleCall" | grep -E "Deviation|DeviationPct|IsOutlier"
sqlite3 plc.db ".schema dspCycle" | grep -E "HasOutliers|OutlierCount"
```

Expected output:
```
Deviation REAL DEFAULT 0,
DeviationPct REAL DEFAULT 0,
IsOutlier INTEGER DEFAULT 0,
HasOutliers INTEGER DEFAULT 0,
OutlierCount INTEGER DEFAULT 0,
```

### 2. Deviation Calculation Test

```bash
# PlcEventSimulator로 10 cycles 실행 후
sqlite3 plc.db "
SELECT cc.CallName,
       c.CycleNumber,
       cc.DurationMs,
       cc.Deviation,
       cc.DeviationPct,
       cc.IsOutlier
FROM dspCycleCall cc
JOIN dspCycle c ON cc.CycleId = c.Id
WHERE c.FlowName = 'Flow1'
ORDER BY c.CycleNumber, cc.CallName
LIMIT 20
"
```

Expected output:
```
Work1|1|2000.0|0.0|0.0|0
Work2|1|1500.0|0.0|0.0|0
Work1|2|2150.0|150.0|7.5|1  -- Outlier!
Work2|2|1520.0|20.0|1.3|0
...
```

### 3. Outlier Detection Test

```bash
sqlite3 plc.db "
SELECT FlowName,
       CycleNumber,
       HasOutliers,
       OutlierCount
FROM dspCycle
WHERE HasOutliers = 1
"
```

Expected output:
```
Flow1|2|1|1
Flow1|7|1|2
```

### 4. UI Verification

- [ ] Heatmap 페이지 접근 확인
- [ ] Flow 선택 드롭다운 동작 확인
- [ ] Canvas에 Heatmap 렌더링 확인
- [ ] 색상 그라디언트 정확성 (파랑-흰색-빨강)
- [ ] Outlier 셀 빨간 테두리 표시 확인
- [ ] 편차 퍼센트 라벨 표시 확인
- [ ] Legend 표시 확인

---

## 🎯 Step 6 완료 조건

- [x] Migration 006 실행 (편차 필드 추가)
- [x] DeviationAnalysis 모듈 구현
- [x] updateCycleDeviations 구현
- [x] StateTransition에 Deviation Analysis 추가
- [x] Heatmap UI 구현 (Canvas 렌더링)
- [x] 색상 그라디언트 정확성 확인
- [x] Outlier 자동 감지 확인

---

## 🎉 All Steps Complete!

**Step 0-6 완료**: DSPilot.Engine의 모든 핵심 기능이 구현되었습니다.

### 구현된 기능

1. **Step 0**: Infrastructure (Database, Repository)
2. **Step 1**: Process Status (공정 상태)
3. **Step 2**: PLC Event Handling (EdgeDetection, StateTransition)
4. **Step 3**: Statistics (Welford's Method, MT/WT/CT)
5. **Step 4**: Bottleneck Detection (FocusScore, Critical Path)
6. **Step 5**: Gantt Chart (Cycle Analysis, Layout)
7. **Step 6**: Heatmap (Deviation Analysis, Outlier Detection)

### Next Steps (Optional Enhancements)

- **Step 7**: 실시간 알림 (SignalR)
- **Step 8**: 데이터 Export (CSV, Excel)
- **Step 9**: 설정 UI (BottleneckConfig, OutlierConfig)
- **Step 10**: 성능 최적화 (인덱스, 캐싱)

---

## 📝 Notes

1. **Color Mapping**: Deviation Pct를 색상으로 매핑 (파랑 = 빠름, 빨강 = 느림)
2. **Outlier Detection**: 평균 ± 2*StdDev 기준
3. **Canvas Performance**: 대량 데이터는 Virtual Scrolling 고려
4. **Cycle Comparison**: Heatmap으로 사이클 간 차이를 한눈에 파악

