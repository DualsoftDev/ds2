# DspRepository API 설계

## 🎯 목적

DSPilot.Engine의 데이터 액세스 계층(Repository)의 API 설계를 정의합니다.

---

## 📐 설계 원칙

### 1. Patch 기반 업데이트

**기존 방식 (❌)**:
```fsharp
let updateFlowMetrics flowName mt wt ct state = // 4개 필드만
let updateCallStatistics callId avg stdDev count = // 3개 필드만
```

**새로운 방식 (✅)**:
```fsharp
let patchFlow (patch: FlowPatch) = // 모든 필드 지원
let patchCall (patch: CallPatch) = // 모든 필드 지원
```

### 2. UPSERT로 통계 보존

```sql
INSERT ... ON CONFLICT DO UPDATE SET
    -- 메타데이터는 업데이트
    SystemName = excluded.SystemName,
    -- 통계는 COALESCE로 기존 값 유지
    AverageCT = COALESCE(dspFlow.AverageCT, excluded.AverageCT)
```

### 3. Query 최적화

- Index 활용
- 필요한 컬럼만 SELECT
- Prepared Statement 사용

---

## 📋 API 목록

### 1. CRUD 연산

#### 1.1 Bulk Insert/Upsert

```fsharp
// DSPilot.Engine/Database/Repository.fs

/// Flow 일괄 UPSERT (통계 보존)
let bulkUpsertFlows (flows: DspFlowEntity list) : Async<unit> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        do! conn.OpenAsync() |> Async.AwaitTask

        use tx = conn.BeginTransaction()

        let sql = """
            INSERT INTO dspFlow (
                FlowName, SystemName, WorkName, MovingStartName, MovingEndName,
                SequenceNo, IsHead, IsTail, State,
                ActiveCallCount, ErrorCallCount,
                CreatedAt, UpdatedAt
            ) VALUES (
                @FlowName, @SystemName, @WorkName, @MovingStartName, @MovingEndName,
                @SequenceNo, @IsHead, @IsTail, @State,
                @ActiveCallCount, @ErrorCallCount,
                datetime('now'), datetime('now')
            )
            ON CONFLICT(FlowName) DO UPDATE SET
                SystemName = excluded.SystemName,
                WorkName = excluded.WorkName,
                MovingStartName = excluded.MovingStartName,
                MovingEndName = excluded.MovingEndName,
                SequenceNo = excluded.SequenceNo,
                IsHead = excluded.IsHead,
                IsTail = excluded.IsTail,
                -- 통계 보존
                AverageCT = COALESCE(dspFlow.AverageCT, excluded.AverageCT),
                StdDevCT = COALESCE(dspFlow.StdDevCT, excluded.StdDevCT),
                CompletedCycleCount = COALESCE(dspFlow.CompletedCycleCount, excluded.CompletedCycleCount),
                UpdatedAt = datetime('now')
        """

        for flow in flows do
            let! _ = conn.ExecuteAsync(sql, flow, tx) |> Async.AwaitTask
            ()

        do! tx.CommitAsync() |> Async.AwaitTask
    }

/// Call 일괄 UPSERT (통계 보존)
let bulkUpsertCalls (calls: DspCallEntity list) : Async<unit> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        do! conn.OpenAsync() |> Async.AwaitTask

        use tx = conn.BeginTransaction()

        let sql = """
            INSERT INTO dspCall (
                CallId, CallName, ApiCall, WorkName, FlowName, SystemName,
                Next, Prev, IsHead, IsTail, SequenceNo, Device, InTag, OutTag,
                State, ProgressRate, CurrentCycleNo,
                CreatedAt, UpdatedAt
            ) VALUES (
                @CallId, @CallName, @ApiCall, @WorkName, @FlowName, @SystemName,
                @Next, @Prev, @IsHead, @IsTail, @SequenceNo, @Device, @InTag, @OutTag,
                @State, @ProgressRate, @CurrentCycleNo,
                datetime('now'), datetime('now')
            )
            ON CONFLICT(CallId) DO UPDATE SET
                CallName = excluded.CallName,
                ApiCall = excluded.ApiCall,
                WorkName = excluded.WorkName,
                FlowName = excluded.FlowName,
                SystemName = excluded.SystemName,
                Next = excluded.Next,
                Prev = excluded.Prev,
                IsHead = excluded.IsHead,
                IsTail = excluded.IsTail,
                SequenceNo = excluded.SequenceNo,
                Device = excluded.Device,
                InTag = excluded.InTag,
                OutTag = excluded.OutTag,
                -- 통계 보존
                AverageGoingTime = COALESCE(dspCall.AverageGoingTime, excluded.AverageGoingTime),
                StdDevGoingTime = COALESCE(dspCall.StdDevGoingTime, excluded.StdDevGoingTime),
                GoingCount = COALESCE(dspCall.GoingCount, excluded.GoingCount),
                UpdatedAt = datetime('now')
        """

        for call in calls do
            let! _ = conn.ExecuteAsync(sql, call, tx) |> Async.AwaitTask
            ()

        do! tx.CommitAsync() |> Async.AwaitTask
    }
```

#### 1.2 Patch Update

```fsharp
/// Flow Patch 업데이트 (변경된 필드만)
let patchFlow (patch: FlowPatch) : Async<unit> =
    async {
        // 1. 변경된 필드만 추출
        let updates = [
            if patch.SystemName.IsSome then "SystemName = @SystemName"
            if patch.WorkName.IsSome then "WorkName = @WorkName"
            if patch.MovingStartName.IsSome then "MovingStartName = @MovingStartName"
            if patch.MovingEndName.IsSome then "MovingEndName = @MovingEndName"
            if patch.SequenceNo.IsSome then "SequenceNo = @SequenceNo"
            if patch.IsHead.IsSome then "IsHead = @IsHead"
            if patch.IsTail.IsSome then "IsTail = @IsTail"
            if patch.State.IsSome then "State = @State"
            if patch.ActiveCallCount.IsSome then "ActiveCallCount = @ActiveCallCount"
            if patch.ErrorCallCount.IsSome then "ErrorCallCount = @ErrorCallCount"
            if patch.LastCycleStartAt.IsSome then "LastCycleStartAt = @LastCycleStartAt"
            if patch.LastCycleEndAt.IsSome then "LastCycleEndAt = @LastCycleEndAt"
            if patch.LastCycleNo.IsSome then "LastCycleNo = @LastCycleNo"
            if patch.MT.IsSome then "MT = @MT"
            if patch.WT.IsSome then "WT = @WT"
            if patch.CT.IsSome then "CT = @CT"
            if patch.LastCycleDurationMs.IsSome then "LastCycleDurationMs = @LastCycleDurationMs"
            if patch.AverageCT.IsSome then "AverageCT = @AverageCT"
            if patch.StdDevCT.IsSome then "StdDevCT = @StdDevCT"
            if patch.MinCT.IsSome then "MinCT = @MinCT"
            if patch.MaxCT.IsSome then "MaxCT = @MaxCT"
            if patch.CompletedCycleCount.IsSome then "CompletedCycleCount = @CompletedCycleCount"
            if patch.SlowCycleFlag.IsSome then "SlowCycleFlag = @SlowCycleFlag"
            if patch.UnmappedCallCount.IsSome then "UnmappedCallCount = @UnmappedCallCount"
            if patch.FocusScore.IsSome then "FocusScore = @FocusScore"
        ]

        if updates.IsEmpty then
            return ()
        else
            // 2. Dynamic SQL 생성
            let sql = sprintf "UPDATE dspFlow SET %s, UpdatedAt = datetime('now') WHERE FlowName = @FlowName"
                             (String.concat ", " updates)

            // 3. Execute
            use conn = new SqliteConnection(getConnectionString())
            let! _ = conn.ExecuteAsync(sql, patch) |> Async.AwaitTask
            return ()
    }

/// Call Patch 업데이트 (변경된 필드만)
let patchCall (patch: CallPatch) : Async<unit> =
    async {
        let updates = [
            if patch.CallName.IsSome then "CallName = @CallName"
            if patch.ApiCall.IsSome then "ApiCall = @ApiCall"
            if patch.WorkName.IsSome then "WorkName = @WorkName"
            if patch.FlowName.IsSome then "FlowName = @FlowName"
            if patch.SystemName.IsSome then "SystemName = @SystemName"
            if patch.Next.IsSome then "Next = @Next"
            if patch.Prev.IsSome then "Prev = @Prev"
            if patch.IsHead.IsSome then "IsHead = @IsHead"
            if patch.IsTail.IsSome then "IsTail = @IsTail"
            if patch.SequenceNo.IsSome then "SequenceNo = @SequenceNo"
            if patch.Device.IsSome then "Device = @Device"
            if patch.InTag.IsSome then "InTag = @InTag"
            if patch.OutTag.IsSome then "OutTag = @OutTag"
            if patch.State.IsSome then "State = @State"
            if patch.ProgressRate.IsSome then "ProgressRate = @ProgressRate"
            if patch.LastStartAt.IsSome then "LastStartAt = @LastStartAt"
            if patch.LastFinishAt.IsSome then "LastFinishAt = @LastFinishAt"
            if patch.LastDurationMs.IsSome then "LastDurationMs = @LastDurationMs"
            if patch.CurrentCycleNo.IsSome then "CurrentCycleNo = @CurrentCycleNo"
            if patch.AverageGoingTime.IsSome then "AverageGoingTime = @AverageGoingTime"
            if patch.StdDevGoingTime.IsSome then "StdDevGoingTime = @StdDevGoingTime"
            if patch.MinGoingTime.IsSome then "MinGoingTime = @MinGoingTime"
            if patch.MaxGoingTime.IsSome then "MaxGoingTime = @MaxGoingTime"
            if patch.GoingCount.IsSome then "GoingCount = @GoingCount"
            if patch.ErrorCount.IsSome then "ErrorCount = @ErrorCount"
            if patch.ErrorText.IsSome then "ErrorText = @ErrorText"
            if patch.SlowFlag.IsSome then "SlowFlag = @SlowFlag"
            if patch.UnmappedFlag.IsSome then "UnmappedFlag = @UnmappedFlag"
            if patch.FocusScore.IsSome then "FocusScore = @FocusScore"
        ]

        if updates.IsEmpty then
            return ()
        else
            let sql = sprintf "UPDATE dspCall SET %s, UpdatedAt = datetime('now') WHERE CallId = @CallId"
                             (String.concat ", " updates)

            use conn = new SqliteConnection(getConnectionString())
            let! _ = conn.ExecuteAsync(sql, patch) |> Async.AwaitTask
            return ()
    }
```

---

### 2. Query 연산

#### 2.1 단일 조회

```fsharp
/// FlowName으로 Flow 조회
let getFlowByName (flowName: string) : Async<DspFlowEntity option> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = "SELECT * FROM dspFlow WHERE FlowName = @flowName"
        let! result = conn.QueryFirstOrDefaultAsync<DspFlowEntity>(sql, {| flowName = flowName |})
                      |> Async.AwaitTask
        return Option.ofObj result
    }

/// CallId로 Call 조회
let getCallById (callId: Guid) : Async<DspCallEntity option> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = "SELECT * FROM dspCall WHERE CallId = @callId"
        let! result = conn.QueryFirstOrDefaultAsync<DspCallEntity>(sql, {| callId = callId |})
                      |> Async.AwaitTask
        return Option.ofObj result
    }

/// CallName과 FlowName으로 Call 조회
let getCallByName (flowName: string) (callName: string) : Async<DspCallEntity option> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = "SELECT * FROM dspCall WHERE FlowName = @flowName AND CallName = @callName"
        let! result = conn.QueryFirstOrDefaultAsync<DspCallEntity>(sql, {| flowName = flowName; callName = callName |})
                      |> Async.AwaitTask
        return Option.ofObj result
    }
```

#### 2.2 목록 조회

```fsharp
/// 모든 Flow 조회
let getAllFlows() : Async<DspFlowEntity list> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = "SELECT * FROM dspFlow ORDER BY FlowName"
        let! results = conn.QueryAsync<DspFlowEntity>(sql) |> Async.AwaitTask
        return results |> Seq.toList
    }

/// FlowName으로 모든 Call 조회
let getCallsByFlow (flowName: string) : Async<DspCallEntity list> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = """
            SELECT * FROM dspCall
            WHERE FlowName = @flowName
            ORDER BY SequenceNo
        """
        let! results = conn.QueryAsync<DspCallEntity>(sql, {| flowName = flowName |}) |> Async.AwaitTask
        return results |> Seq.toList
    }

/// WorkName으로 모든 Call 조회
let getCallsByWork (workName: string) : Async<DspCallEntity list> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = """
            SELECT * FROM dspCall
            WHERE WorkName = @workName
            ORDER BY FlowName, SequenceNo
        """
        let! results = conn.QueryAsync<DspCallEntity>(sql, {| workName = workName |}) |> Async.AwaitTask
        return results |> Seq.toList
    }

/// Tag로 Call 조회 (InTag 또는 OutTag)
let queryCallsByTag (tagName: string) : Async<DspCallEntity list> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = """
            SELECT * FROM dspCall
            WHERE InTag = @tagName OR OutTag = @tagName
            LIMIT 10
        """
        let! results = conn.QueryAsync<DspCallEntity>(sql, {| tagName = tagName |}) |> Async.AwaitTask
        return results |> Seq.toList
    }
```

#### 2.3 필터 조회

```fsharp
/// State로 Call 조회
let getCallsByState (state: string) : Async<DspCallEntity list> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = """
            SELECT * FROM dspCall
            WHERE State = @state
            ORDER BY FocusScore DESC
        """
        let! results = conn.QueryAsync<DspCallEntity>(sql, {| state = state |}) |> Async.AwaitTask
        return results |> Seq.toList
    }

/// FocusScore 순으로 상위 N개 Call 조회
let getTopFocusCalls (limit: int) : Async<DspCallEntity list> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = """
            SELECT * FROM dspCall
            WHERE FocusScore > 0
            ORDER BY FocusScore DESC, FlowName
            LIMIT @limit
        """
        let! results = conn.QueryAsync<DspCallEntity>(sql, {| limit = limit |}) |> Async.AwaitTask
        return results |> Seq.toList
    }

/// Unmapped Call 조회
let getUnmappedCalls() : Async<DspCallEntity list> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = """
            SELECT * FROM dspCall
            WHERE UnmappedFlag = 1
            ORDER BY FlowName, SequenceNo
        """
        let! results = conn.QueryAsync<DspCallEntity>(sql) |> Async.AwaitTask
        return results |> Seq.toList
    }

/// Error 상태 Call 조회
let getErrorCalls() : Async<DspCallEntity list> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = """
            SELECT * FROM dspCall
            WHERE State = 'Error'
            ORDER BY FlowName, SequenceNo
        """
        let! results = conn.QueryAsync<DspCallEntity>(sql) |> Async.AwaitTask
        return results |> Seq.toList
    }
```

#### 2.4 집계 조회

```fsharp
/// State별 Call 개수
let countCallsByState (flowName: string) : Async<Map<string, int>> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = """
            SELECT State, COUNT(*) as Count
            FROM dspCall
            WHERE FlowName = @flowName
            GROUP BY State
        """
        let! results = conn.QueryAsync<{| State: string; Count: int |}>(sql, {| flowName = flowName |})
                       |> Async.AwaitTask
        return results
               |> Seq.map (fun r -> r.State, r.Count)
               |> Map.ofSeq
    }

/// Flow의 진행 중인 Call 개수
let countActiveCallsInFlow (flowName: string) : Async<int> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = """
            SELECT COUNT(*) FROM dspCall
            WHERE FlowName = @flowName AND State = 'Going'
        """
        let! count = conn.ExecuteScalarAsync<int>(sql, {| flowName = flowName |}) |> Async.AwaitTask
        return count
    }

/// 전체 Unmapped Call 개수
let countUnmappedCalls() : Async<int> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = "SELECT COUNT(*) FROM dspCall WHERE UnmappedFlag = 1"
        let! count = conn.ExecuteScalarAsync<int>(sql) |> Async.AwaitTask
        return count
    }
```

---

### 3. 특수 연산

#### 3.1 Head/Tail Call 조회

```fsharp
/// Flow의 Head Call 조회
let getHeadCall (flowName: string) : Async<DspCallEntity option> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = """
            SELECT * FROM dspCall
            WHERE FlowName = @flowName AND IsHead = 1
            LIMIT 1
        """
        let! result = conn.QueryFirstOrDefaultAsync<DspCallEntity>(sql, {| flowName = flowName |})
                      |> Async.AwaitTask
        return Option.ofObj result
    }

/// Flow의 Tail Call 조회
let getTailCall (flowName: string) : Async<DspCallEntity option> =
    async {
        use conn = new SqliteConnection(getConnectionString())
        let sql = """
            SELECT * FROM dspCall
            WHERE FlowName = @flowName AND IsTail = 1
            LIMIT 1
        """
        let! result = conn.QueryFirstOrDefaultAsync<DspCallEntity>(sql, {| flowName = flowName |})
                      |> Async.AwaitTask
        return Option.ofObj result
    }
```

#### 3.2 Cycle 조회

```fsharp
/// 최근 완료된 Cycle의 Call 목록
let getLastCompletedCycleCalls (flowName: string) : Async<DspCallEntity list> =
    async {
        let! flow = getFlowByName flowName
        match flow with
        | Some f when f.LastCycleNo > 0 ->
            use conn = new SqliteConnection(getConnectionString())
            let sql = """
                SELECT * FROM dspCall
                WHERE FlowName = @flowName AND CurrentCycleNo = @cycleNo
                ORDER BY SequenceNo
            """
            let! results = conn.QueryAsync<DspCallEntity>(sql, {| flowName = flowName; cycleNo = f.LastCycleNo |})
                           |> Async.AwaitTask
            return results |> Seq.toList
        | _ ->
            return []
    }
```

---

## 🔧 Helper Functions

### SQL 빌더

```fsharp
// DSPilot.Engine/Database/QueryHelpers.fs

module QueryHelpers =

    /// option 필드를 SET 절로 변환
    let buildSetClause (updates: (string * bool) list) : string =
        updates
        |> List.filter snd
        |> List.map fst
        |> String.concat ", "

    /// WHERE 조건 빌더
    let buildWhereClause (conditions: (string * bool) list) : string =
        let clauses =
            conditions
            |> List.filter snd
            |> List.map fst

        if clauses.IsEmpty then ""
        else "WHERE " + String.concat " AND " clauses

    /// ORDER BY 빌더
    let buildOrderBy (columns: string list) : string =
        if columns.IsEmpty then ""
        else "ORDER BY " + String.concat ", " columns
```

---

## 📊 성능 최적화

### Index 전략

```sql
-- 자주 사용되는 쿼리를 위한 Index
CREATE INDEX IF NOT EXISTS idx_dspFlow_FlowName ON dspFlow(FlowName);
CREATE INDEX IF NOT EXISTS idx_dspFlow_State ON dspFlow(State);
CREATE INDEX IF NOT EXISTS idx_dspFlow_FocusScore ON dspFlow(FocusScore DESC);

CREATE INDEX IF NOT EXISTS idx_dspCall_CallId ON dspCall(CallId);
CREATE INDEX IF NOT EXISTS idx_dspCall_FlowName ON dspCall(FlowName);
CREATE INDEX IF NOT EXISTS idx_dspCall_WorkName ON dspCall(WorkName);
CREATE INDEX IF NOT EXISTS idx_dspCall_State ON dspCall(State);
CREATE INDEX IF NOT EXISTS idx_dspCall_FocusScore ON dspCall(FocusScore DESC);
CREATE INDEX IF NOT EXISTS idx_dspCall_InTag ON dspCall(InTag);
CREATE INDEX IF NOT EXISTS idx_dspCall_OutTag ON dspCall(OutTag);
```

### Connection Pooling

```fsharp
// Dapper는 자동으로 connection을 풀에 반환
// use conn으로 Dispose 보장
use conn = new SqliteConnection(getConnectionString())
```

---

## 🚨 주의사항

### 1. Async 사용

모든 DB 연산은 Async로 감싸서 non-blocking 보장

```fsharp
// ✅ 권장
let getFlow flowName : Async<DspFlowEntity option> = async { ... }

// ❌ 지양
let getFlow flowName : DspFlowEntity option = ... // Blocking
```

### 2. Transaction 사용

일괄 작업 시 Transaction 사용

```fsharp
use tx = conn.BeginTransaction()
// ... 여러 작업
do! tx.CommitAsync() |> Async.AwaitTask
```

### 3. SQL Injection 방지

Parameterized Query 사용

```fsharp
// ✅ 안전
let sql = "SELECT * FROM dspCall WHERE FlowName = @flowName"
conn.QueryAsync<DspCallEntity>(sql, {| flowName = flowName |})

// ❌ 위험
let sql = $"SELECT * FROM dspCall WHERE FlowName = '{flowName}'"
```

---

## 📚 관련 문서

- [02_DATABASE_SCHEMA.md](./02_DATABASE_SCHEMA.md) - 스키마 설계
- [03_PROJECTION_PATTERN.md](./03_PROJECTION_PATTERN.md) - Projection 패턴
- [11_FSHARP_MODULES.md](./11_FSHARP_MODULES.md) - F# 모듈 구조
