# DS Pilot 기능 구현 가이드

## 🎯 목적

DS Pilot의 주요 기능을 DSPilot.Engine Projection 패턴 기반으로 구현하기 위한 상세 가이드입니다.

**기반 문서**: `docs/DS_PILOT_FEATURE_SUMMARY.md`

---

## 📚 기능 분류

### 1. 실시간 운영 가시화
- 공정 상태
- CCTV 모니터링
- TAG 모니터 상세 보기
- 화면 오버레이

### 2. 시간/성능 분석
- 공정 분석
- 병목공정 분석
- 사이클 타임 분석
- 편차 분석

### 3. 이상/원인 추적
- 이력 분석
- 이상 보기
- 이상 리플레이
- 이상 이력관리

### 4. 모델/구조 이해
- 정지 분석 (시퀀스 흐름도)
- 디지털 모델 뷰어

---

## 🔧 기능별 구현 설계

## 1. 공정 상태 (Process Status)

### 기능 정의
설비/생산 라인의 현재 운영 상태를 실시간으로 시각화 (Kanban, Default, List)

### 필요 Projection 필드

**dspFlow**:
- `State` - Flow 상태 (Ready, Going, Error)
- `ActiveCallCount` - 진행 중인 Call 개수
- `ErrorCallCount` - 에러 상태 Call 개수
- `LastCycleStartAt` - 마지막 사이클 시작 시각

**dspCall**:
- `State` - Call 상태 (Ready, Going, Done, Error)
- `ProgressRate` - 진행률 (0.0 ~ 1.0)
- `WorkName` - Work 그룹명
- `Device` - 설비명
- `ErrorText` - 에러 메시지

### 계산 로직

```fsharp
// DSPilot.Engine/Statistics/FlowMetrics.fs

let updateFlowState (flowName: string) : Async<unit> =
    async {
        let! calls = DspRepository.getCallsByFlow flowName

        // 집계
        let activeCount = calls |> Seq.filter (fun c -> c.State = "Going") |> Seq.length
        let errorCount = calls |> Seq.filter (fun c -> c.State = "Error") |> Seq.length

        // State 결정
        let state =
            if errorCount > 0 then "Error"
            elif activeCount > 0 then "Going"
            else "Ready"

        let patch =
            { FlowName = flowName
              State = Some state
              ActiveCallCount = Some activeCount
              ErrorCallCount = Some errorCount }

        do! DspRepository.patchFlow patch
    }
```

### UI 구현 (Blazor)

```csharp
// DSPilot/Components/Pages/ProcessStatus.razor

@page "/process-status"
@inject DspDbService DspDb

<div class="process-status">
    @foreach (var flow in flows)
    {
        <FlowCard Flow="@flow" ViewMode="@viewMode" />
    }
</div>

@code {
    private List<DspFlowEntity> flows;
    private string viewMode = "Kanban"; // Kanban, Default, List

    protected override async Task OnInitializedAsync()
    {
        flows = await DspDb.GetAllFlowsAsync();
    }

    // 100ms polling으로 실시간 업데이트
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync())
            {
                flows = await DspDb.GetAllFlowsAsync();
                StateHasChanged();
            }
        }
    }
}
```

### API 엔드포인트

```csharp
// DSPilot/Services/DspDbService.cs

public async Task<List<DspFlowEntity>> GetAllFlowsAsync()
{
    return await FSharpAsync.StartAsTask(
        DspRepository.getAllFlows(),
        null, null);
}

public async Task<List<DspCallEntity>> GetCallsByFlowAsync(string flowName)
{
    return await FSharpAsync.StartAsTask(
        DspRepository.getCallsByFlow(flowName),
        null, null);
}
```

---

## 2. 공정 분석 (Process Analysis)

### 기능 정의
설비의 세부 동작 시간 측정 및 비교 (운전시간, 대기시간, 상태별 필터링)

### 필요 Projection 필드

**dspCall**:
- `State` - 현재 상태
- `LastDurationMs` - 마지막 실행 시간
- `AverageGoingTime` - 평균 실행 시간
- `MinGoingTime` - 최소 실행 시간
- `MaxGoingTime` - 최대 실행 시간
- `GoingCount` - 실행 횟수

**dspFlow**:
- `MT` - Moving Time (총 작업 시간)
- `WT` - Waiting Time (총 대기 시간)
- `CT` - Cycle Time (총 사이클 시간)

### 계산 로직

```fsharp
// DSPilot.Engine/Statistics/Statistics.fs

let updateCallStatistics (callId: Guid) (newDuration: float) : Async<unit> =
    async {
        let! call = DspRepository.getCallById callId

        let n = call.GoingCount + 1
        let oldAvg = call.AverageGoingTime |> Option.defaultValue 0.0

        // Incremental Average
        let newAvg = (oldAvg * float(n - 1) + newDuration) / float(n)

        // Min/Max
        let newMin = min (call.MinGoingTime |> Option.defaultValue newDuration) newDuration
        let newMax = max (call.MaxGoingTime |> Option.defaultValue newDuration) newDuration

        let patch =
            { CallId = callId
              LastDurationMs = Some newDuration
              AverageGoingTime = Some newAvg
              MinGoingTime = Some newMin
              MaxGoingTime = Some newMax
              GoingCount = Some n }

        do! DspRepository.patchCall patch
    }
```

### UI 구현

```csharp
// DSPilot/Components/Pages/ProcessAnalysis.razor

@page "/process-analysis/{FlowName}"
@inject DspDbService DspDb

<div class="process-analysis">
    <h2>@FlowName 공정 분석</h2>

    <table>
        <thead>
            <tr>
                <th>Call</th>
                <th>상태</th>
                <th>마지막 실행</th>
                <th>평균</th>
                <th>최소</th>
                <th>최대</th>
                <th>실행 횟수</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var call in calls)
            {
                <tr class="@GetStateClass(call.State)">
                    <td>@call.CallName</td>
                    <td>@call.State</td>
                    <td>@FormatTime(call.LastDurationMs)</td>
                    <td>@FormatTime(call.AverageGoingTime)</td>
                    <td>@FormatTime(call.MinGoingTime)</td>
                    <td>@FormatTime(call.MaxGoingTime)</td>
                    <td>@call.GoingCount</td>
                </tr>
            }
        </tbody>
    </table>
</div>

@code {
    [Parameter] public string FlowName { get; set; }
    private List<DspCallEntity> calls;

    protected override async Task OnInitializedAsync()
    {
        calls = await DspDb.GetCallsByFlowAsync(FlowName);
    }

    private string GetStateClass(string state) => state switch
    {
        "Ready" => "state-ready",
        "Going" => "state-going",
        "Done" => "state-done",
        "Error" => "state-error",
        _ => ""
    };

    private string FormatTime(double? ms) =>
        ms.HasValue ? $"{ms.Value:F1} ms" : "-";
}
```

---

## 3. 병목공정 분석 (Bottleneck Analysis)

### 기능 정의
설비별 Moving Time과 Waiting Time 비교로 병목 지점 파악

### 필요 Projection 필드

**dspFlow**:
- `MT` - Moving Time
- `WT` - Waiting Time
- `CT` - Cycle Time

**dspCall**:
- `LastDurationMs` - 개별 Call 실행 시간
- `AverageGoingTime` - 평균 실행 시간

### 계산 로직

```fsharp
// DSPilot.Engine/Statistics/FlowMetrics.fs

let calculateCycleMetrics (flowName: string) : Async<unit> =
    async {
        let! calls = DspRepository.getCallsByFlow flowName
        let! headCall = DspRepository.getHeadCall flowName
        let! tailCall = DspRepository.getTailCall flowName

        match headCall, tailCall with
        | Some head, Some tail when head.LastStartAt.IsSome && tail.LastFinishAt.IsSome ->
            // MT = 모든 Call의 LastDurationMs 합
            let mt = calls |> Seq.sumBy (fun c -> c.LastDurationMs |> Option.defaultValue 0.0)

            // CT = Head Start ~ Tail Finish
            let ct = (tail.LastFinishAt.Value - head.LastStartAt.Value).TotalMilliseconds

            // WT = CT - MT
            let wt = ct - mt

            let patch =
                { FlowName = flowName
                  MT = Some mt
                  WT = Some wt
                  CT = Some ct }

            do! DspRepository.patchFlow patch
        | _ ->
            return ()
    }
```

### UI 구현

```csharp
// DSPilot/Components/Pages/BottleneckAnalysis.razor

@page "/bottleneck-analysis"
@inject DspDbService DspDb

<div class="bottleneck-analysis">
    <h2>병목공정 분석</h2>

    <div class="chart-container">
        @foreach (var flow in flows.OrderByDescending(f => f.WT))
        {
            <div class="flow-bar">
                <div class="flow-name">@flow.FlowName</div>
                <div class="time-bars">
                    <div class="bar moving" style="width: @GetBarWidth(flow.MT)%">
                        MT: @FormatTime(flow.MT)
                    </div>
                    <div class="bar waiting" style="width: @GetBarWidth(flow.WT)%">
                        WT: @FormatTime(flow.WT)
                    </div>
                </div>
                <div class="total">CT: @FormatTime(flow.CT)</div>
            </div>
        }
    </div>
</div>

@code {
    private List<DspFlowEntity> flows;
    private double maxCT;

    protected override async Task OnInitializedAsync()
    {
        flows = await DspDb.GetAllFlowsAsync();
        maxCT = flows.Max(f => f.CT ?? 0);
    }

    private double GetBarWidth(double? time)
    {
        if (!time.HasValue || maxCT == 0) return 0;
        return (time.Value / maxCT) * 100;
    }

    private string FormatTime(double? ms) =>
        ms.HasValue ? $"{ms.Value:F1} ms" : "-";
}
```

---

## 4. 사이클 타임 분석 (Cycle Time Analysis - Gantt Chart)

### 기능 정의
각 설비의 동작 시퀀스를 Gantt Chart로 표현, 타이밍과 대기 구간 분석

### 필요 Projection 필드

**dspCallHistory** (⚠️ 이력 테이블 필수!):
- `CallId` - Call 참조
- `CallName` - Call 이름
- `FlowName` - Flow 이름
- `CycleNo` - 사이클 번호
- `StartAt` - 시작 시각
- `FinishAt` - 종료 시각
- `DurationMs` - 실행 시간
- `State` - 실행 상태

**dspCall** (Join용):
- `WorkName` - Work 그룹핑
- `SequenceNo` - 순서
- `Prev` - 이전 Call
- `Next` - 다음 Call

### 계산 로직 (✅ 이력 기반)

```fsharp
// DSPilot.Engine/Analysis/GanttLayout.fs

type GanttBar =
    { CallId: Guid
      CallName: string
      WorkName: string
      StartTime: DateTime
      EndTime: DateTime
      Duration: float
      YPosition: int
      State: string }

/// ✅ 이력 테이블에서 Gantt Chart 데이터 조회
let calculateGanttLayout (flowName: string) (cycleNo: int) : Async<GanttBar list> =
    async {
        use conn = new SqliteConnection(getConnectionString())

        // ⚠️ dspCallHistory에서 조회 (덮어쓰기 없음!)
        let sql = """
            SELECT
                h.CallId,
                h.CallName,
                c.WorkName,
                h.StartAt,
                h.FinishAt,
                h.DurationMs,
                c.SequenceNo,
                h.State
            FROM dspCallHistory h
            JOIN dspCall c ON h.CallId = c.Id
            WHERE h.FlowName = @FlowName
              AND h.CycleNo = @CycleNo
              AND h.FinishAt IS NOT NULL
            ORDER BY h.StartAt
        """

        let! rows =
            conn.QueryAsync<GanttRow>(sql, {| FlowName = flowName; CycleNo = cycleNo |})
            |> Async.AwaitTask

        let bars =
            rows
            |> Seq.mapi (fun i row ->
                { CallId = row.CallId
                  CallName = row.CallName
                  WorkName = row.WorkName
                  StartTime = DateTime.Parse(row.StartAt)
                  EndTime = DateTime.Parse(row.FinishAt)
                  Duration = row.DurationMs
                  YPosition = i
                  State = row.State })
            |> Seq.toList

        return bars
    }

/// ✅ Gap 분석 (연속된 Call 간 대기시간)
let calculateGaps (flowName: string) (cycleNo: int) : Async<GapInfo list> =
    async {
        use conn = new SqliteConnection(getConnectionString())

        let sql = """
            WITH OrderedCalls AS (
                SELECT
                    h.CallName,
                    h.StartAt,
                    h.FinishAt,
                    c.SequenceNo,
                    LAG(h.FinishAt) OVER (ORDER BY c.SequenceNo) AS PrevFinishAt
                FROM dspCallHistory h
                JOIN dspCall c ON h.CallId = c.Id
                WHERE h.FlowName = @FlowName
                  AND h.CycleNo = @CycleNo
                  AND h.FinishAt IS NOT NULL
            )
            SELECT
                CallName,
                PrevFinishAt,
                StartAt,
                (julianday(StartAt) - julianday(PrevFinishAt)) * 86400000.0 AS GapMs
            FROM OrderedCalls
            WHERE PrevFinishAt IS NOT NULL
        """

        let! gaps =
            conn.QueryAsync<GapInfo>(sql, {| FlowName = flowName; CycleNo = cycleNo |})
            |> Async.AwaitTask

        return gaps |> Seq.toList
    }
```

### ⚠️ 주요 변경 사항

1. **데이터 소스 변경**:
   - ❌ `dspCall.LastStartAt/LastFinishAt` (덮어쓰기됨)
   - ✅ `dspCallHistory` (Append-Only, 모든 이력 보존)

2. **Cycle 선택**:
   - `CycleNo` 필터링으로 과거 사이클 복원 가능
   - 병렬/반복 호출 모두 추적

3. **Gap 계산 추가**:
   - 연속된 Call 간 대기시간 계산
   - Bottleneck 분석에 필수
```

### UI 구현

```csharp
// DSPilot/Components/Pages/CycleTimeAnalysis.razor

@page "/cycle-time-analysis/{FlowName}"
@inject DspDbService DspDb

<div class="cycle-time-analysis">
    <h2>@FlowName 사이클 타임 분석</h2>

    <div class="gantt-chart">
        <svg width="100%" height="@(bars.Count * 40 + 50)">
            <!-- Time axis -->
            <g class="time-axis">
                @for (int i = 0; i <= 10; i++)
                {
                    var x = i * 10;
                    <line x1="@x%" y1="0" x2="@x%" y2="100%" stroke="#ddd" />
                    <text x="@x%" y="20" fill="#666">@GetTimeLabel(i)</text>
                }
            </g>

            <!-- Gantt bars -->
            @foreach (var bar in bars)
            {
                var x = GetXPosition(bar.StartTime);
                var width = GetBarWidth(bar.Duration);
                var y = bar.YPosition * 40 + 30;

                <g class="gantt-bar">
                    <rect x="@x%" y="@y" width="@width%" height="30"
                          fill="@GetBarColor(bar.State)" opacity="0.8" />
                    <text x="@(x + 1)%" y="@(y + 20)" fill="#fff">
                        @bar.CallName (@bar.Duration.ToString("F1") ms)
                    </text>
                </g>
            }
        </svg>
    </div>
</div>

@code {
    [Parameter] public string FlowName { get; set; }
    private List<GanttBar> bars;
    private DateTime startTime;
    private DateTime endTime;

    protected override async Task OnInitializedAsync()
    {
        // F# 모듈 호출
        bars = await FSharpAsync.StartAsTask(
            GanttLayout.calculateGanttLayout(FlowName, currentCycleNo),
            null, null);

        if (bars.Any())
        {
            startTime = bars.Min(b => b.StartTime);
            endTime = bars.Max(b => b.EndTime);
        }
    }

    private double GetXPosition(DateTime time)
    {
        var totalDuration = (endTime - startTime).TotalMilliseconds;
        var offset = (time - startTime).TotalMilliseconds;
        return (offset / totalDuration) * 100;
    }

    private double GetBarWidth(double duration)
    {
        var totalDuration = (endTime - startTime).TotalMilliseconds;
        return (duration / totalDuration) * 100;
    }

    private string GetBarColor(string state) => state switch
    {
        "Ready" => "#6c757d",
        "Going" => "#007bff",
        "Done" => "#28a745",
        "Error" => "#dc3545",
        _ => "#6c757d"
    };

    private string GetTimeLabel(int index)
    {
        var time = startTime.AddMilliseconds((endTime - startTime).TotalMilliseconds * index / 10);
        return time.ToString("HH:mm:ss.fff");
    }
}
```

---

## 5. 편차 분석 (Deviation Analysis - Heatmap)

### 기능 정의
동작 시간의 반복 변동성을 CV(변동계수)로 수치화하여 Heatmap 시각화

### 필요 Projection 필드

**dspCall**:
- `AverageGoingTime` - 평균
- `StdDevGoingTime` - 표준편차
- `GoingCount` - 실행 횟수

### 계산 로직

```fsharp
// DSPilot.Engine/Statistics/Statistics.fs

let calculateCV (avg: float option) (stdDev: float option) : float option =
    match avg, stdDev with
    | Some a, Some s when a > 0.0 -> Some (s / a)
    | _ -> None

let updateCallStatisticsWithCV (callId: Guid) (newDuration: float) : Async<unit> =
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

        let patch =
            { CallId = callId
              AverageGoingTime = Some newAvg
              StdDevGoingTime = Some newStdDev
              GoingCount = Some n }

        do! DspRepository.patchCall patch
    }
```

### UI 구현

```csharp
// DSPilot/Components/Pages/DeviationAnalysis.razor

@page "/deviation-analysis"
@inject DspDbService DspDb

<div class="deviation-analysis">
    <h2>편차 분석 (Heatmap)</h2>

    <div class="heatmap-container">
        <table class="heatmap">
            <thead>
                <tr>
                    <th>Flow</th>
                    @foreach (var work in workNames)
                    {
                        <th>@work</th>
                    }
                </tr>
            </thead>
            <tbody>
                @foreach (var flow in flowNames)
                {
                    <tr>
                        <td>@flow</td>
                        @foreach (var work in workNames)
                        {
                            var cell = GetHeatmapCell(flow, work);
                            <td style="background-color: @GetHeatmapColor(cell.CV)"
                                title="@GetTooltip(cell)">
                                @cell.CV?.ToString("F2")
                            </td>
                        }
                    </tr>
                }
            </tbody>
        </table>
    </div>

    <div class="legend">
        <div class="legend-item" style="background: #00f">안정 (CV < 0.1)</div>
        <div class="legend-item" style="background: #0f0">보통 (0.1 ≤ CV < 0.3)</div>
        <div class="legend-item" style="background: #ff0">주의 (0.3 ≤ CV < 0.5)</div>
        <div class="legend-item" style="background: #f00">불안정 (CV ≥ 0.5)</div>
    </div>
</div>

@code {
    private List<DspCallEntity> allCalls;
    private List<string> flowNames;
    private List<string> workNames;

    private class HeatmapCell
    {
        public double? CV { get; set; }
        public double? Avg { get; set; }
        public double? StdDev { get; set; }
        public int Count { get; set; }
    }

    protected override async Task OnInitializedAsync()
    {
        allCalls = await DspDb.GetAllCallsAsync();
        flowNames = allCalls.Select(c => c.FlowName).Distinct().OrderBy(n => n).ToList();
        workNames = allCalls.Select(c => c.WorkName).Distinct().OrderBy(n => n).ToList();
    }

    private HeatmapCell GetHeatmapCell(string flow, string work)
    {
        var calls = allCalls.Where(c => c.FlowName == flow && c.WorkName == work).ToList();
        if (!calls.Any()) return new HeatmapCell();

        var avgAvg = calls.Average(c => c.AverageGoingTime ?? 0);
        var avgStdDev = calls.Average(c => c.StdDevGoingTime ?? 0);
        var cv = avgAvg > 0 ? avgStdDev / avgAvg : (double?)null;

        return new HeatmapCell
        {
            CV = cv,
            Avg = avgAvg,
            StdDev = avgStdDev,
            Count = calls.Sum(c => c.GoingCount)
        };
    }

    private string GetHeatmapColor(double? cv)
    {
        if (!cv.HasValue) return "#eee";
        if (cv.Value < 0.1) return "#0000ff"; // 청색 - 안정
        if (cv.Value < 0.3) return "#00ff00"; // 녹색 - 보통
        if (cv.Value < 0.5) return "#ffff00"; // 황색 - 주의
        return "#ff0000"; // 적색 - 불안정
    }

    private string GetTooltip(HeatmapCell cell)
    {
        if (cell.CV == null) return "데이터 없음";
        return $"CV: {cell.CV:F2}\nAvg: {cell.Avg:F1} ms\nStdDev: {cell.StdDev:F1} ms\nCount: {cell.Count}";
    }
}
```

---

## 6. 이력 분석 (History Analysis)

### 기능 정의
시스템 전체 이벤트와 상태 변화를 시간 순서대로 추적

### 필요 데이터

**plcTagLog 테이블** (Raw PLC Event Log):
```sql
CREATE TABLE IF NOT EXISTS plcTagLog (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    TagName TEXT NOT NULL,
    Value INTEGER NOT NULL,  -- 0 or 1
    Timestamp TEXT NOT NULL,
    Source TEXT
);

CREATE INDEX IF NOT EXISTS idx_plcTagLog_Timestamp ON plcTagLog(Timestamp);
CREATE INDEX IF NOT EXISTS idx_plcTagLog_TagName ON plcTagLog(TagName);
```

### 계산 로직

```fsharp
// DSPilot.Engine/Database/Repository.fs

let queryPlcTagLogs (fromTime: DateTime option) (toTime: DateTime option) (tagFilter: string option) : Async<PlcTagLog list> =
    async {
        use conn = new SqliteConnection(getConnectionString())

        let conditions = [
            if fromTime.IsSome then "Timestamp >= @fromTime"
            if toTime.IsSome then "Timestamp <= @toTime"
            if tagFilter.IsSome then "TagName LIKE @tagFilter"
        ]

        let whereClause = if conditions.IsEmpty then "" else "WHERE " + String.concat " AND " conditions

        let sql = $"SELECT * FROM plcTagLog {whereClause} ORDER BY Timestamp DESC LIMIT 1000"

        let! results = conn.QueryAsync<PlcTagLog>(sql, {| fromTime = fromTime; toTime = toTime; tagFilter = tagFilter |})
                       |> Async.AwaitTask
        return results |> Seq.toList
    }
```

### UI 구현

```csharp
// DSPilot/Components/Pages/HistoryAnalysis.razor

@page "/history-analysis"
@inject DspDbService DspDb

<div class="history-analysis">
    <h2>이력 분석</h2>

    <div class="filter-panel">
        <input type="datetime-local" @bind="fromTime" />
        <input type="datetime-local" @bind="toTime" />
        <input type="text" placeholder="TAG 필터" @bind="tagFilter" />
        <button @onclick="Search">검색</button>
        <button @onclick="ExportCsv">Export CSV</button>
    </div>

    <table class="history-table">
        <thead>
            <tr>
                <th>시각</th>
                <th>TAG</th>
                <th>값</th>
                <th>소스</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var log in logs)
            {
                <tr class="@GetRowClass(log.Value)">
                    <td>@log.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")</td>
                    <td>@log.TagName</td>
                    <td>@(log.Value ? "TRUE" : "FALSE")</td>
                    <td>@log.Source</td>
                </tr>
            }
        </tbody>
    </table>
</div>

@code {
    private DateTime? fromTime;
    private DateTime? toTime;
    private string tagFilter;
    private List<PlcTagLog> logs = new();

    private async Task Search()
    {
        logs = await FSharpAsync.StartAsTask(
            DspRepository.queryPlcTagLogs(
                FSharpOption.OfObj(fromTime),
                FSharpOption.OfObj(toTime),
                FSharpOption.OfObj(tagFilter)),
            null, null);
    }

    private async Task ExportCsv()
    {
        var csv = string.Join("\n",
            new[] { "Timestamp,TagName,Value,Source" }
            .Concat(logs.Select(l => $"{l.Timestamp:O},{l.TagName},{l.Value},{l.Source}")));

        // Download CSV
        await JSRuntime.InvokeVoidAsync("downloadFile", "history.csv", csv);
    }

    private string GetRowClass(bool value) => value ? "value-true" : "value-false";
}
```

---

## 📊 통합 데이터 흐름

### 실시간 업데이트 Flow

```
PLC Event
    ↓
EdgeDetection (F#)
    ↓
PlcToCallMapper (F#)
    ↓
StateTransition (F#)
    ├→ patchCall (State, Times)
    ├→ updateCallStatistics (Avg, StdDev, CV)
    └→ updateFlowMetrics (MT, WT, CT, ActiveCount)
    ↓
Projection Updated (dspFlow, dspCall)
    ↓
UI Polling (100ms) → Blazor Components → User
```

### 분석 화면 데이터 흐름

```
User Request (Analysis Page)
    ↓
C# Service Layer
    ↓
F# Repository (Query Projection)
    ↓
dspFlow / dspCall (Projection Tables)
    ↓
UI Rendering (No Calculation!)
```

---

## 📚 관련 문서

- [01_ARCHITECTURE.md](./01_ARCHITECTURE.md) - 전체 아키텍처
- [02_DATABASE_SCHEMA.md](./02_DATABASE_SCHEMA.md) - 스키마 설계
- [03_PROJECTION_PATTERN.md](./03_PROJECTION_PATTERN.md) - Projection 패턴
- [04_EVENT_PIPELINE.md](./04_EVENT_PIPELINE.md) - 이벤트 파이프라인
- [09_REFACTORING_PLAN.md](./09_REFACTORING_PLAN.md) - 리팩토링 계획
- `docs/DS_PILOT_FEATURE_SUMMARY.md` - 기능 요약 (원본)
