# 단계별 구현 설계

## 🎯 구현 전략

**핵심 원칙**: 1개 기능을 완전히 구현하고 검증한 후, 다음 기능을 추가(append)하는 방식

**장점**:
- 각 단계마다 동작하는 시스템 확보
- 문제 발생 시 롤백 지점 명확
- 점진적 학습 및 개선 가능
- 데모 및 피드백 수시 가능

---

## 📋 구현 순서 (Total 6 Steps)

### Step 0: 기반 준비 (Infrastructure)
- Database Schema 기본 구조
- Repository 기본 CRUD
- Blazor 프로젝트 구조

### Step 1: 공정 상태 (Process Status) ⭐ 최우선
- **목표**: Flow 목록과 현재 상태를 화면에 표시
- **범위**: Static 데이터만 (실시간 업데이트 없음)
- **검증**: Blazor에서 Flow 목록 조회 성공

### Step 2: PLC Event 수신 및 상태 업데이트
- **목표**: PLC 이벤트를 받아 Call 상태를 업데이트
- **범위**: EdgeDetection + StateTransition
- **검증**: InTag/OutTag Rising Edge로 State 변경 확인

### Step 3: 통계 계산 (Statistics)
- **목표**: Call 완료 시 평균, 표준편차 계산
- **범위**: Incremental Statistics (Welford's Method)
- **검증**: AverageGoingTime, StdDevGoingTime 업데이트 확인

### Step 4: Flow 집계 (Flow Metrics)
- **목표**: Flow 레벨 MT, WT, CT 계산
- **범위**: Tail Call 완료 시 집계
- **검증**: MT/WT/CT 계산 정확도 확인

### Step 5: 병목공정 분석 (Bottleneck Analysis)
- **목표**: MT/WT를 Bar Chart로 시각화
- **범위**: Blazor UI 컴포넌트
- **검증**: WT 기준 내림차순 정렬 확인

### Step 6: 사이클 타임 분석 (Gantt Chart)
- **목표**: Call 시퀀스를 Gantt Chart로 표시
- **범위**: SVG 기반 시각화
- **검증**: 시간축 정렬 및 색상 구분 확인

---

## 🚀 Step 0: 기반 준비 (Infrastructure)

### 목표
최소한의 Database 및 Repository 구조를 구축하여 Step 1 진행 가능하게 함.

### 작업 항목

#### 0.1 Database Schema (최소 버전)

```sql
-- 초기 버전: 가장 기본 필드만
CREATE TABLE IF NOT EXISTS dspFlow (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    FlowName TEXT NOT NULL UNIQUE,
    State TEXT DEFAULT 'Ready',
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS dspCall (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CallId TEXT NOT NULL UNIQUE,
    CallName TEXT NOT NULL,
    FlowName TEXT NOT NULL,
    WorkName TEXT NOT NULL,
    State TEXT DEFAULT 'Ready',
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_dspFlow_FlowName ON dspFlow(FlowName);
CREATE INDEX IF NOT EXISTS idx_dspCall_FlowName ON dspCall(FlowName);
CREATE INDEX IF NOT EXISTS idx_dspCall_CallId ON dspCall(CallId);
```

**파일**: `DSPilot.Engine/Database/Initialization.fs`

```fsharp
module DSPilot.Engine.Database.Initialization

open System.Data.SQLite
open Dapper

let private createMinimalSchema (connStr: string) =
    async {
        use conn = new SqliteConnection(connStr)
        do! conn.OpenAsync() |> Async.AwaitTask

        let flowTableSql = """
            CREATE TABLE IF NOT EXISTS dspFlow (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FlowName TEXT NOT NULL UNIQUE,
                State TEXT DEFAULT 'Ready',
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """

        let callTableSql = """
            CREATE TABLE IF NOT EXISTS dspCall (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CallId TEXT NOT NULL UNIQUE,
                CallName TEXT NOT NULL,
                FlowName TEXT NOT NULL,
                WorkName TEXT NOT NULL,
                State TEXT DEFAULT 'Ready',
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            )
        """

        let! _ = conn.ExecuteAsync(flowTableSql) |> Async.AwaitTask
        let! _ = conn.ExecuteAsync(callTableSql) |> Async.AwaitTask

        // Index
        let! _ = conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_dspFlow_FlowName ON dspFlow(FlowName)") |> Async.AwaitTask
        let! _ = conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_dspCall_FlowName ON dspCall(FlowName)") |> Async.AwaitTask
        let! _ = conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_dspCall_CallId ON dspCall(CallId)") |> Async.AwaitTask

        return ()
    }

let initializeDatabase (dbPath: string) =
    let connStr = $"Data Source={dbPath};Version=3;"
    createMinimalSchema connStr |> Async.RunSynchronously
```

#### 0.2 Entity 정의 (최소 버전)

**파일**: `DSPilot.Engine/Database/Entities.fs`

```fsharp
module DSPilot.Engine.Database.Entities

open System

type DspFlowEntity =
    { Id: int option
      FlowName: string
      State: string option
      CreatedAt: DateTime
      UpdatedAt: DateTime }

type DspCallEntity =
    { Id: int option
      CallId: Guid
      CallName: string
      FlowName: string
      WorkName: string
      State: string
      CreatedAt: DateTime
      UpdatedAt: DateTime }
```

#### 0.3 Repository 기본 CRUD

**파일**: `DSPilot.Engine/Database/Repository.fs`

```fsharp
module DSPilot.Engine.Database.Repository

open System
open System.Data.SQLite
open Dapper
open DSPilot.Engine.Database.Entities

let private getConnectionString (dbPath: string) =
    $"Data Source={dbPath};Version=3;"

// Flow CRUD
let getAllFlows (dbPath: string) : Async<DspFlowEntity list> =
    async {
        use conn = new SqliteConnection(getConnectionString dbPath)
        let sql = "SELECT * FROM dspFlow ORDER BY FlowName"
        let! results = conn.QueryAsync<DspFlowEntity>(sql) |> Async.AwaitTask
        return results |> Seq.toList
    }

let getFlowByName (dbPath: string) (flowName: string) : Async<DspFlowEntity option> =
    async {
        use conn = new SqliteConnection(getConnectionString dbPath)
        let sql = "SELECT * FROM dspFlow WHERE FlowName = @flowName"
        let! result = conn.QueryFirstOrDefaultAsync<DspFlowEntity>(sql, {| flowName = flowName |}) |> Async.AwaitTask
        return Option.ofObj result
    }

let insertFlow (dbPath: string) (flow: DspFlowEntity) : Async<unit> =
    async {
        use conn = new SqliteConnection(getConnectionString dbPath)
        let sql = """
            INSERT INTO dspFlow (FlowName, State, CreatedAt, UpdatedAt)
            VALUES (@FlowName, @State, datetime('now'), datetime('now'))
        """
        let! _ = conn.ExecuteAsync(sql, flow) |> Async.AwaitTask
        return ()
    }

// Call CRUD
let getCallsByFlow (dbPath: string) (flowName: string) : Async<DspCallEntity list> =
    async {
        use conn = new SqliteConnection(getConnectionString dbPath)
        let sql = "SELECT * FROM dspCall WHERE FlowName = @flowName ORDER BY CallName"
        let! results = conn.QueryAsync<DspCallEntity>(sql, {| flowName = flowName |}) |> Async.AwaitTask
        return results |> Seq.toList
    }

let insertCall (dbPath: string) (call: DspCallEntity) : Async<unit> =
    async {
        use conn = new SqliteConnection(getConnectionString dbPath)
        let sql = """
            INSERT INTO dspCall (CallId, CallName, FlowName, WorkName, State, CreatedAt, UpdatedAt)
            VALUES (@CallId, @CallName, @FlowName, @WorkName, @State, datetime('now'), datetime('now'))
        """
        let! _ = conn.ExecuteAsync(sql, call) |> Async.AwaitTask
        return ()
    }
```

#### 0.4 C# Adapter (Blazor에서 F# 호출)

**파일**: `DSPilot/Adapters/DspRepositoryAdapter.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using DSPilot.Engine.Database;

namespace DSPilot.Adapters
{
    public class DspRepositoryAdapter
    {
        private readonly string _dbPath;

        public DspRepositoryAdapter(string dbPath)
        {
            _dbPath = dbPath;
        }

        public async Task<List<Entities.DspFlowEntity>> GetAllFlowsAsync()
        {
            return await FSharpAsync.StartAsTask(
                Repository.getAllFlows(_dbPath),
                FSharpOption<TaskCreationOptions>.None,
                FSharpOption<CancellationToken>.None);
        }

        public async Task<List<Entities.DspCallEntity>> GetCallsByFlowAsync(string flowName)
        {
            return await FSharpAsync.StartAsTask(
                Repository.getCallsByFlow(_dbPath, flowName),
                FSharpOption<TaskCreationOptions>.None,
                FSharpOption<CancellationToken>.None);
        }

        public async Task InsertFlowAsync(Entities.DspFlowEntity flow)
        {
            await FSharpAsync.StartAsTask(
                Repository.insertFlow(_dbPath, flow),
                FSharpOption<TaskCreationOptions>.None,
                FSharpOption<CancellationToken>.None);
        }

        public async Task InsertCallAsync(Entities.DspCallEntity call)
        {
            await FSharpAsync.StartAsTask(
                Repository.insertCall(_dbPath, call),
                FSharpOption<TaskCreationOptions>.None,
                FSharpOption<CancellationToken>.None);
        }
    }
}
```

#### 0.5 DI 등록

**파일**: `DSPilot/Program.cs`

```csharp
// Program.cs에 추가
builder.Services.AddSingleton(sp =>
{
    var dbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Dualsoft", "DSPilot", "plc.db");

    Directory.CreateDirectory(Path.GetDirectoryName(dbPath));

    // Initialize DB
    DSPilot.Engine.Database.Initialization.initializeDatabase(dbPath);

    return new DspRepositoryAdapter(dbPath);
});
```

### 검증 (Step 0)

```bash
# 1. DB 파일 생성 확인
ls ~/.local/share/Dualsoft/DSPilot/plc.db  # Linux/WSL
# 또는
ls ~/AppData/Roaming/Dualsoft/DSPilot/plc.db  # Windows

# 2. 테이블 확인
sqlite3 <dbPath> ".tables"
# 출력: dspCall  dspFlow

# 3. 스키마 확인
sqlite3 <dbPath> ".schema dspFlow"
```

---

## ⭐ Step 1: 공정 상태 (Process Status) - 최우선 구현

### 목표
Flow 목록을 화면에 표시하고, 각 Flow의 기본 정보 확인

### 작업 항목

#### 1.1 테스트 데이터 삽입

**파일**: `DSPilot.TestConsole/SeedData.cs`

```csharp
using System;
using System.Threading.Tasks;
using DSPilot.Adapters;
using DSPilot.Engine.Database.Entities;

namespace DSPilot.TestConsole
{
    public static class SeedData
    {
        public static async Task SeedMinimalDataAsync(DspRepositoryAdapter repo)
        {
            // Flow 삽입
            await repo.InsertFlowAsync(new DspFlowEntity
            {
                Id = FSharpOption<int>.None,
                FlowName = "FLOW_001",
                State = FSharpOption<string>.Some("Ready"),
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            });

            await repo.InsertFlowAsync(new DspFlowEntity
            {
                Id = FSharpOption<int>.None,
                FlowName = "FLOW_002",
                State = FSharpOption<string>.Some("Ready"),
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            });

            // Call 삽입 (FLOW_001용)
            await repo.InsertCallAsync(new DspCallEntity
            {
                Id = FSharpOption<int>.None,
                CallId = Guid.NewGuid(),
                CallName = "CALL_001",
                FlowName = "FLOW_001",
                WorkName = "WORK_A",
                State = "Ready",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            });

            await repo.InsertCallAsync(new DspCallEntity
            {
                Id = FSharpOption<int>.None,
                CallId = Guid.NewGuid(),
                CallName = "CALL_002",
                FlowName = "FLOW_001",
                WorkName = "WORK_A",
                State = "Ready",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            });

            Console.WriteLine("✅ Seed data inserted: 2 Flows, 2 Calls");
        }
    }
}
```

#### 1.2 Blazor Component (공정 상태)

**파일**: `DSPilot/Components/Pages/ProcessStatus.razor`

```razor
@page "/process-status"
@using DSPilot.Adapters
@inject DspRepositoryAdapter Repo

<PageTitle>공정 상태</PageTitle>

<h1>📊 공정 상태</h1>

@if (flows == null)
{
    <p><em>Loading...</em></p>
}
else if (flows.Count == 0)
{
    <p>No flows found.</p>
}
else
{
    <div class="flow-grid">
        @foreach (var flow in flows)
        {
            <div class="flow-card @GetStateClass(flow.State)">
                <h3>@flow.FlowName</h3>
                <div class="state-badge">@flow.State</div>
                <button class="btn btn-sm btn-primary" @onclick="() => ShowDetail(flow.FlowName)">
                    상세 보기
                </button>
            </div>
        }
    </div>
}

@if (selectedFlow != null)
{
    <div class="detail-section">
        <h2>@selectedFlow - Call 목록</h2>
        <table class="table">
            <thead>
                <tr>
                    <th>Call Name</th>
                    <th>Work Name</th>
                    <th>State</th>
                </tr>
            </thead>
            <tbody>
                @foreach (var call in calls)
                {
                    <tr>
                        <td>@call.CallName</td>
                        <td>@call.WorkName</td>
                        <td><span class="badge @GetStateClass(call.State)">@call.State</span></td>
                    </tr>
                }
            </tbody>
        </table>
    </div>
}

@code {
    private List<DspFlowEntity> flows;
    private List<DspCallEntity> calls = new();
    private string selectedFlow;

    protected override async Task OnInitializedAsync()
    {
        flows = await Repo.GetAllFlowsAsync();
    }

    private async Task ShowDetail(string flowName)
    {
        selectedFlow = flowName;
        calls = await Repo.GetCallsByFlowAsync(flowName);
    }

    private string GetStateClass(FSharpOption<string> stateOpt)
    {
        var state = FSharpOption<string>.get_IsSome(stateOpt)
            ? stateOpt.Value
            : "Unknown";
        return GetStateClass(state);
    }

    private string GetStateClass(string state)
    {
        return state switch
        {
            "Ready" => "state-ready",
            "Going" => "state-going",
            "Done" => "state-done",
            "Error" => "state-error",
            _ => ""
        };
    }
}
```

#### 1.3 CSS 스타일

**파일**: `DSPilot/wwwroot/css/process-status.css`

```css
.flow-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(250px, 1fr));
    gap: 20px;
    margin: 30px 0;
}

.flow-card {
    background: white;
    border-radius: 10px;
    padding: 20px;
    box-shadow: 0 4px 10px rgba(0, 0, 0, 0.1);
    transition: transform 0.3s;
    border-left: 5px solid #ccc;
}

.flow-card:hover {
    transform: translateY(-5px);
    box-shadow: 0 8px 20px rgba(0, 0, 0, 0.2);
}

.flow-card.state-ready {
    border-left-color: #6c757d;
}

.flow-card.state-going {
    border-left-color: #007bff;
}

.flow-card.state-done {
    border-left-color: #28a745;
}

.flow-card.state-error {
    border-left-color: #dc3545;
}

.state-badge {
    display: inline-block;
    padding: 5px 15px;
    border-radius: 20px;
    font-size: 0.9em;
    font-weight: bold;
    margin: 10px 0;
}

.state-ready .state-badge {
    background: #6c757d;
    color: white;
}

.state-going .state-badge {
    background: #007bff;
    color: white;
}

.state-done .state-badge {
    background: #28a745;
    color: white;
}

.state-error .state-badge {
    background: #dc3545;
    color: white;
}

.detail-section {
    margin-top: 40px;
    padding: 20px;
    background: #f8f9fa;
    border-radius: 10px;
}
```

### 검증 (Step 1)

1. **앱 실행**
   ```bash
   cd DSPilot
   dotnet run
   ```

2. **브라우저 확인**
   - URL: `http://localhost:5000/process-status`
   - 2개 Flow 카드 표시 확인 (FLOW_001, FLOW_002)
   - "상세 보기" 클릭 시 Call 목록 표시 확인

3. **결과**
   - ✅ Flow 목록 조회 성공
   - ✅ Call 목록 조회 성공
   - ✅ State 색상 구분 표시 성공

---

## 📌 Step 1 완료 후 체크리스트

- [ ] Database 파일 생성 확인
- [ ] dspFlow 테이블에 2개 레코드 확인
- [ ] dspCall 테이블에 2개 레코드 확인
- [ ] Blazor 앱 실행 성공
- [ ] Process Status 페이지 접근 성공
- [ ] Flow 카드 2개 표시 확인
- [ ] "상세 보기" 클릭 시 Call 목록 표시 확인

---

## 🔜 다음 단계 (Step 2 이후)

Step 1이 완료되면, 다음 문서에서 계속:
- `14_STEP_02_PLC_EVENT.md` - PLC Event 수신 및 상태 업데이트
- `15_STEP_03_STATISTICS.md` - 통계 계산
- `16_STEP_04_FLOW_METRICS.md` - Flow 집계
- `17_STEP_05_BOTTLENECK_ANALYSIS.md` - 병목공정 분석
- `18_STEP_06_GANTT_CHART.md` - 사이클 타임 분석

---

## 📊 전체 로드맵

```
Step 0: 기반 준비 ✅
    ↓
Step 1: 공정 상태 ← 현재 작업
    ↓
Step 2: PLC Event 수신
    ↓
Step 3: 통계 계산
    ↓
Step 4: Flow 집계
    ↓
Step 5: 병목공정 분석
    ↓
Step 6: 사이클 타임 분석
```

---

## 💡 핵심 포인트

### Append 가능한 구조

각 Step은 이전 Step에 영향을 주지 않고 추가 가능:

- **Step 0 → Step 1**: 기본 CRUD 추가
- **Step 1 → Step 2**: Event Handler 추가 (기존 CRUD 유지)
- **Step 2 → Step 3**: Statistics 모듈 추가 (기존 Event Handler 유지)
- **Step 3 → Step 4**: FlowMetrics 모듈 추가 (기존 Statistics 유지)
- **Step 4 → Step 5**: UI 컴포넌트 추가 (백엔드 영향 없음)
- **Step 5 → Step 6**: UI 컴포넌트 추가 (백엔드 영향 없음)

### Schema 확장 방식

```sql
-- Step 1 → Step 2: 필드 추가
ALTER TABLE dspCall ADD COLUMN LastStartAt TEXT;
ALTER TABLE dspCall ADD COLUMN LastFinishAt TEXT;

-- Step 2 → Step 3: 필드 추가
ALTER TABLE dspCall ADD COLUMN AverageGoingTime REAL;
ALTER TABLE dspCall ADD COLUMN StdDevGoingTime REAL;
ALTER TABLE dspCall ADD COLUMN GoingCount INTEGER DEFAULT 0;

-- Step 3 → Step 4: 필드 추가
ALTER TABLE dspFlow ADD COLUMN MT REAL;
ALTER TABLE dspFlow ADD COLUMN WT REAL;
ALTER TABLE dspFlow ADD COLUMN CT REAL;
```

### 테스트 전략

각 Step마다 독립적인 테스트:
- **Unit Test**: F# 모듈별 단위 테스트
- **Integration Test**: Repository CRUD 테스트
- **UI Test**: Blazor 컴포넌트 렌더링 테스트
- **E2E Test**: 전체 흐름 테스트 (Step 6 이후)

---

## 📚 관련 문서

- [09_REFACTORING_PLAN.md](./09_REFACTORING_PLAN.md) - 전체 리팩토링 계획
- [02_DATABASE_SCHEMA.md](./02_DATABASE_SCHEMA.md) - 최종 스키마 설계
- [12_REPOSITORY_API.md](./12_REPOSITORY_API.md) - Repository 전체 API
