# Step 2: PLC Event 수신 및 상태 업데이트

## 🎯 목표

PLC Tag 이벤트를 수신하여 Call의 State를 실시간으로 업데이트합니다.

**범위**:
- EdgeDetection (Rising Edge 감지)
- PlcToCallMapper (Tag → Call 매핑)
- StateTransition (InTag/OutTag 판정 및 State 업데이트)

**검증**: InTag Rising Edge → State: Ready → Going, OutTag Rising Edge → State: Going → Done

---

## 📋 작업 항목

### 2.1 Database Schema 확장

Step 1의 기본 스키마에 필드 추가:

```sql
-- dspCall 확장
ALTER TABLE dspCall ADD COLUMN InTag TEXT;
ALTER TABLE dspCall ADD COLUMN OutTag TEXT;
ALTER TABLE dspCall ADD COLUMN LastStartAt TEXT;
ALTER TABLE dspCall ADD COLUMN LastFinishAt TEXT;
ALTER TABLE dspCall ADD COLUMN LastDurationMs REAL;
ALTER TABLE dspCall ADD COLUMN ProgressRate REAL DEFAULT 0.0;

-- dspFlow 확장
ALTER TABLE dspFlow ADD COLUMN ActiveCallCount INTEGER DEFAULT 0;

-- plcTagLog 테이블 생성 (Raw Event 저장)
CREATE TABLE IF NOT EXISTS plcTagLog (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    TagName TEXT NOT NULL,
    Value INTEGER NOT NULL,
    Timestamp TEXT NOT NULL,
    Source TEXT
);

CREATE INDEX IF NOT EXISTS idx_plcTagLog_Timestamp ON plcTagLog(Timestamp);
CREATE INDEX IF NOT EXISTS idx_plcTagLog_TagName ON plcTagLog(TagName);
```

**파일**: `DSPilot.Engine/Database/Initialization.fs` - `migration002` 함수 추가

```fsharp
let private migration002 (conn: SqliteConnection) =
    async {
        // dspCall 확장
        let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN InTag TEXT") |> Async.AwaitTask
        let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN OutTag TEXT") |> Async.AwaitTask
        let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN LastStartAt TEXT") |> Async.AwaitTask
        let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN LastFinishAt TEXT") |> Async.AwaitTask
        let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN LastDurationMs REAL") |> Async.AwaitTask
        let! _ = conn.ExecuteAsync("ALTER TABLE dspCall ADD COLUMN ProgressRate REAL DEFAULT 0.0") |> Async.AwaitTask

        // dspFlow 확장
        let! _ = conn.ExecuteAsync("ALTER TABLE dspFlow ADD COLUMN ActiveCallCount INTEGER DEFAULT 0") |> Async.AwaitTask

        // plcTagLog 테이블
        let plcTagLogSql = """
            CREATE TABLE IF NOT EXISTS plcTagLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TagName TEXT NOT NULL,
                Value INTEGER NOT NULL,
                Timestamp TEXT NOT NULL,
                Source TEXT
            )
        """
        let! _ = conn.ExecuteAsync(plcTagLogSql) |> Async.AwaitTask
        let! _ = conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_plcTagLog_Timestamp ON plcTagLog(Timestamp)") |> Async.AwaitTask
        let! _ = conn.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_plcTagLog_TagName ON plcTagLog(TagName)") |> Async.AwaitTask

        return ()
    }
```

### 2.2 Entity 확장

**파일**: `DSPilot.Engine/Database/Entities.fs` - 필드 추가

```fsharp
type DspFlowEntity =
    { Id: int option
      FlowName: string
      State: string option
      ActiveCallCount: int  // 신규
      CreatedAt: DateTime
      UpdatedAt: DateTime }

type DspCallEntity =
    { Id: int option
      CallId: Guid
      CallName: string
      FlowName: string
      WorkName: string
      State: string
      InTag: string option  // 신규
      OutTag: string option  // 신규
      LastStartAt: DateTime option  // 신규
      LastFinishAt: DateTime option  // 신규
      LastDurationMs: float option  // 신규
      ProgressRate: float  // 신규
      CreatedAt: DateTime
      UpdatedAt: DateTime }

type PlcTagLog =
    { Id: int option
      TagName: string
      Value: int
      Timestamp: DateTime
      Source: string option }
```

### 2.3 Core Types 정의

**파일**: `DSPilot.Engine/Core/Types.fs`

```fsharp
module DSPilot.Engine.Core.Types

open System

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
```

### 2.4 EdgeDetection 모듈

**파일**: `DSPilot.Engine/Tracking/TagStateTracker.fs`

```fsharp
module DSPilot.Engine.Tracking.TagStateTracker

open System
open System.Collections.Generic
open DSPilot.Engine.Core.Types

type TagStateTracker() =
    let lastStates = Dictionary<string, bool>()

    member this.DetectEdge(event: PlcTagEvent) : EdgeEvent option =
        match lastStates.TryGetValue(event.TagName) with
        | true, prevValue ->
            lastStates.[event.TagName] <- event.Value

            // Rising Edge: false → true
            if not prevValue && event.Value then
                Some { TagName = event.TagName
                       EdgeType = RisingEdge
                       Timestamp = event.Timestamp }
            // Falling Edge: true → false (미사용)
            elif prevValue && not event.Value then
                Some { TagName = event.TagName
                       EdgeType = FallingEdge
                       Timestamp = event.Timestamp }
            else
                None
        | false, _ ->
            // 첫 이벤트: 저장만
            lastStates.[event.TagName] <- event.Value
            None

    member this.Reset() =
        lastStates.Clear()
```

### 2.5 PlcToCallMapper 모듈

**파일**: `DSPilot.Engine/Tracking/PlcToCallMapper.fs`

```fsharp
module DSPilot.Engine.Tracking.PlcToCallMapper

open DSPilot.Engine.Database.Entities
open DSPilot.Engine.Database.Repository

let findCallByTag (dbPath: string) (tagName: string) : Async<DspCallEntity option> =
    async {
        use conn = new System.Data.SQLite.SqliteConnection(getConnectionString dbPath)
        let sql = """
            SELECT * FROM dspCall
            WHERE InTag = @tagName OR OutTag = @tagName
            LIMIT 1
        """
        let! result = Dapper.SqlMapper.QueryFirstOrDefaultAsync<DspCallEntity>(conn, sql, {| tagName = tagName |})
                      |> Async.AwaitTask
        return Option.ofObj result
    }

let isInTag (call: DspCallEntity) (tagName: string) : bool =
    call.InTag = Some tagName

let isOutTag (call: DspCallEntity) (tagName: string) : bool =
    call.OutTag = Some tagName
```

### 2.6 StateTransition 모듈

**파일**: `DSPilot.Engine/Tracking/StateTransition.fs`

```fsharp
module DSPilot.Engine.Tracking.StateTransition

open System
open DSPilot.Engine.Core.Types
open DSPilot.Engine.Database.Entities
open DSPilot.Engine.Tracking.PlcToCallMapper

let handleInTagRisingEdge (dbPath: string) (call: DspCallEntity) (timestamp: DateTime) : Async<unit> =
    async {
        use conn = new System.Data.SQLite.SqliteConnection(Repository.getConnectionString dbPath)

        // 1. Call State 업데이트: Ready → Going
        let callSql = """
            UPDATE dspCall
            SET State = 'Going',
                LastStartAt = @timestamp,
                ProgressRate = 0.5,
                UpdatedAt = datetime('now')
            WHERE CallId = @callId
        """
        let! _ = Dapper.SqlMapper.ExecuteAsync(conn, callSql, {| callId = call.CallId; timestamp = timestamp |})
                 |> Async.AwaitTask

        // 2. Flow ActiveCallCount 증가
        let flowSql = """
            UPDATE dspFlow
            SET ActiveCallCount = ActiveCallCount + 1,
                State = 'Going',
                UpdatedAt = datetime('now')
            WHERE FlowName = @flowName
        """
        let! _ = Dapper.SqlMapper.ExecuteAsync(conn, flowSql, {| flowName = call.FlowName |})
                 |> Async.AwaitTask

        return ()
    }

let handleOutTagRisingEdge (dbPath: string) (call: DspCallEntity) (timestamp: DateTime) : Async<unit> =
    async {
        // Duration 계산
        let duration =
            match call.LastStartAt with
            | Some startAt -> (timestamp - startAt).TotalMilliseconds
            | None -> 0.0

        use conn = new System.Data.SQLite.SqliteConnection(Repository.getConnectionString dbPath)

        // 1. Call State 업데이트: Going → Done
        let callSql = """
            UPDATE dspCall
            SET State = 'Done',
                LastFinishAt = @timestamp,
                LastDurationMs = @duration,
                ProgressRate = 1.0,
                UpdatedAt = datetime('now')
            WHERE CallId = @callId
        """
        let! _ = Dapper.SqlMapper.ExecuteAsync(conn, callSql,
                     {| callId = call.CallId; timestamp = timestamp; duration = duration |})
                 |> Async.AwaitTask

        // 2. Flow ActiveCallCount 감소
        let flowSql = """
            UPDATE dspFlow
            SET ActiveCallCount = ActiveCallCount - 1,
                UpdatedAt = datetime('now')
            WHERE FlowName = @flowName
        """
        let! _ = Dapper.SqlMapper.ExecuteAsync(conn, flowSql, {| flowName = call.FlowName |})
                 |> Async.AwaitTask

        // 3. Flow State 업데이트 (ActiveCallCount가 0이면 Ready)
        let! flow = Repository.getFlowByName dbPath call.FlowName
        match flow with
        | Some f when f.ActiveCallCount <= 1 ->
            let updateStateSql = """
                UPDATE dspFlow
                SET State = 'Ready',
                    UpdatedAt = datetime('now')
                WHERE FlowName = @flowName
            """
            let! _ = Dapper.SqlMapper.ExecuteAsync(conn, updateStateSql, {| flowName = call.FlowName |})
                     |> Async.AwaitTask
            return ()
        | _ -> return ()
    }

let handlePlcEvent (dbPath: string) (event: PlcTagEvent) (tracker: TagStateTracker) : Async<unit> =
    async {
        // 1. Edge Detection
        let edgeOpt = tracker.DetectEdge(event)

        match edgeOpt with
        | Some edge when edge.EdgeType = RisingEdge ->
            // 2. Tag → Call 매핑
            let! callOpt = findCallByTag dbPath edge.TagName

            match callOpt with
            | Some call ->
                // 3. InTag vs OutTag 판정
                if isInTag call edge.TagName then
                    do! handleInTagRisingEdge dbPath call edge.Timestamp
                elif isOutTag call edge.TagName then
                    do! handleOutTagRisingEdge dbPath call edge.Timestamp
            | None ->
                printfn $"⚠️ Unmapped tag: {edge.TagName}"
        | _ -> ()
    }
```

### 2.7 PLC Event Source (시뮬레이션)

**파일**: `DSPilot/Services/PlcEventSimulator.cs`

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using DSPilot.Engine.Core.Types;
using DSPilot.Engine.Tracking;

namespace DSPilot.Services
{
    public class PlcEventSimulator
    {
        private readonly string _dbPath;
        private readonly TagStateTracker _tracker;
        private CancellationTokenSource _cts;

        public PlcEventSimulator(string dbPath)
        {
            _dbPath = dbPath;
            _tracker = new TagStateTracker();
        }

        public async Task StartAsync()
        {
            _cts = new CancellationTokenSource();

            // 시뮬레이션: 5초마다 InTag → OutTag 순서로 이벤트 발생
            while (!_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(5000);

                // InTag Rising Edge
                var inTagEvent = new PlcTagEvent
                {
                    TagName = "PLC.CALL_001.InTag",
                    Value = true,
                    Timestamp = DateTime.Now,
                    Source = "Simulator"
                };

                await FSharpAsync.StartAsTask(
                    StateTransition.handlePlcEvent(_dbPath, inTagEvent, _tracker),
                    null, null);

                Console.WriteLine($"📡 InTag Rising Edge: {inTagEvent.TagName}");

                await Task.Delay(2000);

                // OutTag Rising Edge
                var outTagEvent = new PlcTagEvent
                {
                    TagName = "PLC.CALL_001.OutTag",
                    Value = true,
                    Timestamp = DateTime.Now,
                    Source = "Simulator"
                };

                await FSharpAsync.StartAsTask(
                    StateTransition.handlePlcEvent(_dbPath, outTagEvent, _tracker),
                    null, null);

                Console.WriteLine($"📡 OutTag Rising Edge: {outTagEvent.TagName}");
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
        }
    }
}
```

### 2.8 Seed Data에 Tag 매핑 추가

**파일**: `DSPilot.TestConsole/SeedData.cs` - 업데이트

```csharp
// Call 삽입 시 InTag, OutTag 추가
await repo.InsertCallAsync(new DspCallEntity
{
    Id = FSharpOption<int>.None,
    CallId = Guid.NewGuid(),
    CallName = "CALL_001",
    FlowName = "FLOW_001",
    WorkName = "WORK_A",
    State = "Ready",
    InTag = FSharpOption<string>.Some("PLC.CALL_001.InTag"),
    OutTag = FSharpOption<string>.Some("PLC.CALL_001.OutTag"),
    LastStartAt = FSharpOption<DateTime>.None,
    LastFinishAt = FSharpOption<DateTime>.None,
    LastDurationMs = FSharpOption<double>.None,
    ProgressRate = 0.0,
    CreatedAt = DateTime.Now,
    UpdatedAt = DateTime.Now
});
```

### 2.9 DI 등록 (Simulator)

**파일**: `DSPilot/Program.cs` - 추가

```csharp
builder.Services.AddSingleton<PlcEventSimulator>(sp =>
{
    var dbPath = // ... (기존 코드)
    return new PlcEventSimulator(dbPath);
});

// 앱 시작 시 Simulator 실행
var app = builder.Build();

var simulator = app.Services.GetRequiredService<PlcEventSimulator>();
_ = Task.Run(() => simulator.StartAsync());
```

### 2.10 UI 업데이트 (실시간 Polling)

**파일**: `DSPilot/Components/Pages/ProcessStatus.razor` - 업데이트

```razor
@code {
    private List<DspFlowEntity> flows;
    private Timer _timer;

    protected override async Task OnInitializedAsync()
    {
        flows = await Repo.GetAllFlowsAsync();

        // 100ms polling
        _timer = new Timer(async _ =>
        {
            flows = await Repo.GetAllFlowsAsync();
            await InvokeAsync(StateHasChanged);
        }, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}

@implements IDisposable
```

---

## ✅ 검증 (Step 2)

### 1. Migration 실행

```bash
dotnet run --project DSPilot
# Migration 002가 자동 실행되는지 확인
```

### 2. 테이블 스키마 확인

```bash
sqlite3 <dbPath> ".schema dspCall"
# InTag, OutTag, LastStartAt, LastFinishAt, LastDurationMs, ProgressRate 필드 확인

sqlite3 <dbPath> ".tables"
# plcTagLog 테이블 확인
```

### 3. Seed Data 확인

```bash
sqlite3 <dbPath> "SELECT CallName, InTag, OutTag FROM dspCall"
# CALL_001의 Tag 매핑 확인
```

### 4. Simulator 실행 및 State 변경 확인

```bash
# 앱 실행
dotnet run --project DSPilot

# 콘솔 출력 확인
# 📡 InTag Rising Edge: PLC.CALL_001.InTag
# (2초 후)
# 📡 OutTag Rising Edge: PLC.CALL_001.OutTag
```

### 5. DB State 확인

```bash
# InTag 이벤트 후
sqlite3 <dbPath> "SELECT CallName, State, LastStartAt FROM dspCall WHERE CallName = 'CALL_001'"
# State: Going, LastStartAt: (timestamp)

# OutTag 이벤트 후
sqlite3 <dbPath> "SELECT CallName, State, LastFinishAt, LastDurationMs FROM dspCall WHERE CallName = 'CALL_001'"
# State: Done, LastFinishAt: (timestamp), LastDurationMs: (약 2000ms)
```

### 6. UI 실시간 업데이트 확인

- 브라우저: `http://localhost:5000/process-status`
- Flow 카드의 State가 실시간으로 변경되는지 확인
  - Ready → Going (InTag 이벤트)
  - Going → Ready (OutTag 이벤트 후 ActiveCallCount = 0)

---

## 📌 Step 2 완료 체크리스트

- [ ] Migration 002 실행 확인
- [ ] dspCall에 InTag, OutTag 필드 추가 확인
- [ ] plcTagLog 테이블 생성 확인
- [ ] TagStateTracker 모듈 동작 확인
- [ ] PlcToCallMapper 모듈 동작 확인
- [ ] StateTransition 모듈 동작 확인
- [ ] PlcEventSimulator 실행 확인
- [ ] InTag Rising Edge → State: Going 확인
- [ ] OutTag Rising Edge → State: Done 확인
- [ ] Flow ActiveCallCount 증감 확인
- [ ] UI 실시간 업데이트 확인 (100ms polling)

---

## 🔜 다음 단계

Step 2 완료 후:
- **Step 3**: 통계 계산 (Incremental Avg, StdDev)
  - `15_STEP_03_STATISTICS.md` 참조

---

## 📚 관련 문서

- [13_STEP_BY_STEP_IMPLEMENTATION.md](./13_STEP_BY_STEP_IMPLEMENTATION.md) - 전체 단계 개요
- [04_EVENT_PIPELINE.md](./04_EVENT_PIPELINE.md) - 이벤트 파이프라인 상세
