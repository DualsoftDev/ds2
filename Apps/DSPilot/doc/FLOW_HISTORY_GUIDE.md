# Flow History 가이드

**작성일**: 2026-03-23
**목적**: dspFlowHistory 테이블을 사용한 사이클 히스토리 관리

---

## 📋 개요

`dspFlowHistory` 테이블은 각 Flow의 사이클 완료 시 MT, WT, CT 값을 기록하여 시간대별 성능 추세를 분석할 수 있도록 합니다.

---

## 🗄️ 데이터베이스 스키마

### dspFlowHistory 테이블

```sql
CREATE TABLE dspFlowHistory (
    id              INTEGER PRIMARY KEY AUTOINCREMENT
    , flowName      TEXT NOT NULL
    , mt            INT                  -- Machine Time (ms)
    , wt            INT                  -- Wait Time (ms)
    , ct            INT                  -- Cycle Time (ms)
    , cycleNo       INT                  -- 사이클 번호
    , recordedAt    DATETIME NOT NULL    -- 기록 시간
    , FOREIGN KEY(flowName) REFERENCES dspFlow(flowName) ON DELETE CASCADE
);

CREATE INDEX idx_dspFlowHistory_flowName ON dspFlowHistory (flowName);
CREATE INDEX idx_dspFlowHistory_recordedAt ON dspFlowHistory (recordedAt DESC);
```

### 주요 특징

1. **자동 증가 ID**: 각 히스토리 레코드는 고유 ID를 가짐
2. **시간순 정렬**: `recordedAt DESC` 인덱스로 최신 기록 빠른 조회
3. **Flow 연결**: `flowName`으로 Flow와 연결 (CASCADE DELETE)
4. **사이클 번호**: `cycleNo`로 특정 사이클 추적 가능

---

## 🔧 F# Entity 정의

### DspFlowHistoryEntity

```fsharp
// DSPilot.Engine/Database/Entities.fs

[<CLIMutable>]
type DspFlowHistoryEntity =
    { Id: int
      FlowName: string
      MT: int option
      WT: int option
      CT: int option
      CycleNo: int option
      RecordedAt: DateTime }

    static member Create(flowName: string, mt: int option, wt: int option, ct: int option, cycleNo: int option) =
        { Id = 0
          FlowName = flowName
          MT = mt
          WT = wt
          CT = ct
          CycleNo = cycleNo
          RecordedAt = DateTime.UtcNow }
```

### DapperFlowHistoryDto

```fsharp
// DSPilot.Engine/Database/Dtos.fs

[<CLIMutable>]
type DapperFlowHistoryDto =
    { FlowName: string
      MT: Nullable<int>
      WT: Nullable<int>
      CT: Nullable<int>
      CycleNo: Nullable<int>
      RecordedAt: DateTime }

    static member FromEntity(entity: DspFlowHistoryEntity) =
        { FlowName = entity.FlowName
          MT = match entity.MT with Some v -> Nullable v | None -> Nullable()
          WT = match entity.WT with Some v -> Nullable v | None -> Nullable()
          CT = match entity.CT with Some v -> Nullable v | None -> Nullable()
          CycleNo = match entity.CycleNo with Some v -> Nullable v | None -> Nullable()
          RecordedAt = entity.RecordedAt }
```

---

## 📝 Repository API

### insertFlowHistoryAsync

사이클 완료 시 히스토리 레코드 삽입

```fsharp
let insertFlowHistoryAsync
    (paths: DatabasePaths)
    (logger: ILogger)
    (history: DspFlowHistoryEntity) : Task<int>
```

**사용 예시**:
```fsharp
let history = DspFlowHistoryEntity.Create(
    flowName = "Assembly_Flow",
    mt = Some 15000,
    wt = Some 5000,
    ct = Some 20000,
    cycleNo = Some 42
)

let! result = DspRepository.insertFlowHistoryAsync paths logger history
```

### getFlowHistoryAsync

특정 Flow의 최근 N개 히스토리 조회

```fsharp
let getFlowHistoryAsync
    (paths: DatabasePaths)
    (logger: ILogger)
    (flowName: string)
    (limit: int) : Task<DspFlowHistoryEntity list>
```

**사용 예시**:
```fsharp
// 최근 100개 사이클 조회
let! histories = DspRepository.getFlowHistoryAsync paths logger "Assembly_Flow" 100

// 차트 데이터 생성
let chartData =
    histories
    |> List.map (fun h ->
        {| CycleNo = h.CycleNo
           CT = h.CT
           MT = h.MT
           WT = h.WT
           Time = h.RecordedAt |})
```

---

## 💡 사용 시나리오

### 1. 사이클 완료 시 자동 기록

**CycleAnalysisService.cs** (현재 미구현):
```csharp
public async Task OnCycleCompleteAsync(string flowName, int mt, int wt, int ct, int cycleNo)
{
    // 1. dspFlow 테이블 업데이트 (현재값)
    await _repository.UpdateFlowMetricsAsync(flowName, mt, wt, ct, ...);

    // 2. 평균값 계산 및 업데이트
    var avgMT = await CalculateAverageAsync(flowName, "MT", mt);
    var avgWT = await CalculateAverageAsync(flowName, "WT", wt);
    var avgCT = await CalculateAverageAsync(flowName, "CT", ct);
    await _repository.UpdateFlowAveragesAsync(flowName, avgMT, avgWT, avgCT);

    // 3. 히스토리 기록 (F# Repository 호출)
    var history = new FSharpDspFlowHistoryEntity(
        Id: 0,
        FlowName: flowName,
        MT: FSharpOption<int>.Some(mt),
        WT: FSharpOption<int>.Some(wt),
        CT: FSharpOption<int>.Some(ct),
        CycleNo: FSharpOption<int>.Some(cycleNo),
        RecordedAt: DateTime.UtcNow
    );

    await _dspRepositoryAdapter.InsertFlowHistoryAsync(history);
}
```

### 2. 추세 분석 차트

**FlowTrendChart.razor** (예정):
```razor
@code {
    private async Task LoadTrendDataAsync()
    {
        var histories = await FlowHistoryService.GetRecentHistoryAsync(FlowName, 100);

        ChartData = histories.Select(h => new
        {
            Time = h.RecordedAt,
            CT = h.CT ?? 0,
            MT = h.MT ?? 0,
            WT = h.WT ?? 0
        }).ToList();
    }
}
```

### 3. 성능 저하 탐지

```csharp
public async Task<bool> DetectPerformanceDegradationAsync(string flowName)
{
    var recent = await GetFlowHistoryAsync(flowName, 10);
    var baseline = await GetFlowHistoryAsync(flowName, 100);

    var recentAvgCT = recent.Average(h => h.CT ?? 0);
    var baselineAvgCT = baseline.Average(h => h.CT ?? 0);

    // 최근 10 사이클의 평균이 전체 평균보다 20% 이상 느린 경우
    return recentAvgCT > baselineAvgCT * 1.2;
}
```

---

## 📊 데이터 관리

### 보관 정책

히스토리 데이터가 누적되면 DB 크기가 증가하므로 정기적인 정리 필요:

```sql
-- 30일 이전 데이터 삭제
DELETE FROM dspFlowHistory
WHERE recordedAt < datetime('now', '-30 days');

-- 또는 사이클 개수 제한 (Flow당 최근 1000개만 유지)
DELETE FROM dspFlowHistory
WHERE id NOT IN (
    SELECT id FROM dspFlowHistory
    WHERE flowName = 'Assembly_Flow'
    ORDER BY recordedAt DESC
    LIMIT 1000
);
```

### 집계 테이블 생성 (선택)

매일 집계 데이터 생성하여 장기 추세 분석:

```sql
CREATE TABLE dspFlowDailyStats (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    flowName TEXT NOT NULL,
    date DATE NOT NULL,
    avgMT REAL,
    avgWT REAL,
    avgCT REAL,
    minCT INT,
    maxCT INT,
    cycleCount INT,
    UNIQUE(flowName, date)
);
```

---

## 🚀 향후 개선 사항

### 1. Adapter 추가

**DspRepositoryAdapter.cs**에 FlowHistory 메서드 추가:
```csharp
public async Task<int> InsertFlowHistoryAsync(DspFlowHistoryEntity history)
{
    var fsharpHistory = ToFSharpFlowHistoryEntity(history);
    return await DSPilot.Engine.DspRepository.insertFlowHistoryAsync(
        _paths, _logger, fsharpHistory);
}

public async Task<List<DspFlowHistoryEntity>> GetFlowHistoryAsync(
    string flowName, int limit)
{
    var fsharpList = await DSPilot.Engine.DspRepository.getFlowHistoryAsync(
        _paths, _logger, flowName, limit);
    return fsharpList.Select(ToCSharpFlowHistoryEntity).ToList();
}
```

### 2. Service 계층

**FlowHistoryService.cs** (신규):
```csharp
public class FlowHistoryService
{
    private readonly IDspRepository _repository;

    public async Task RecordCycleAsync(string flowName, int mt, int wt, int ct, int cycleNo)
    {
        var history = new DspFlowHistoryEntity
        {
            FlowName = flowName,
            MT = mt,
            WT = wt,
            CT = ct,
            CycleNo = cycleNo,
            RecordedAt = DateTime.UtcNow
        };

        await _repository.InsertFlowHistoryAsync(history);
    }

    public async Task<List<DspFlowHistoryEntity>> GetTrendDataAsync(
        string flowName, int limit = 100)
    {
        return await _repository.GetFlowHistoryAsync(flowName, limit);
    }
}
```

### 3. UI 컴포넌트

**FlowHistoryChart.razor** (신규):
- Line Chart: CT 추세
- Stacked Bar Chart: MT/WT 비율
- Time Range Selector: 기간 선택

---

## ⚠️ 주의사항

1. **성능 고려**:
   - 히스토리 테이블은 계속 증가하므로 정기적인 정리 필요
   - 대량 조회 시 인덱스 활용 (flowName, recordedAt)

2. **트랜잭션**:
   - Flow 메트릭 업데이트와 히스토리 삽입은 별도 트랜잭션
   - 히스토리 삽입 실패 시에도 현재값은 업데이트됨

3. **Null 처리**:
   - MT, WT, CT는 Nullable이므로 평균 계산 시 주의
   - CycleNo가 없을 경우 RecordedAt으로 순서 파악

4. **마이그레이션**:
   - 기존 DB에는 dspFlowHistory 테이블이 없음
   - 애플리케이션 재시작 시 자동 생성됨

---

## 📚 관련 문서

- [02_DATABASE_SCHEMA.md](./02_DATABASE_SCHEMA.md) - dspFlow 스키마
- [04_EVENT_PIPELINE.md](./04_EVENT_PIPELINE.md) - 사이클 완료 이벤트
- [README.md](./README.md) - 전체 문서 인덱스
