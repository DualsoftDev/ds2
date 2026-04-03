namespace DSPilot.Engine

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Logging
open Dapper

/// DSP 실시간 데이터베이스 저장소 - Dapper 기반 SQLite 구현
module DspRepository =

    // ===== Private Helpers =====

    /// SQLite 연결 생성
    let private createConnection (connectionString: string) =
        new SqliteConnection(connectionString)

    /// 테이블 존재 여부 확인
    let private tablesExistAsync (connection: SqliteConnection) (flowTable: string) (callTable: string) =
        task {
            let sql = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN (@flowTable, @callTable)"
            let! count = connection.ExecuteScalarAsync<int>(sql, {| flowTable = flowTable; callTable = callTable |})
            return count >= 2
        }

    /// 테이블 존재 여부 확인 (단일 테이블)
    let private tableExistsAsync (connection: SqliteConnection) (tableName: string) =
        task {
            let sql = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@tableName"
            let! count = connection.ExecuteScalarAsync<int64>(sql, {| tableName = tableName |})
            return count > 0L
        }

    /// 컬럼 존재 여부 확인
    let private columnExistsAsync (connection: SqliteConnection) (tableName: string) (columnName: string) =
        task {
            let sql = sprintf "PRAGMA table_info(%s)" tableName
            let! columns = connection.QueryAsync<{| name: string |}>(sql)
            return columns |> Seq.exists (fun c -> c.name = columnName)
        }

    /// IsIdle 컬럼 자동 마이그레이션 (dspFlowHistory)
    let private ensureIsIdleColumnAsync (connection: SqliteConnection) (logger: ILogger) =
        task {
            let! exists = columnExistsAsync connection "dspFlowHistory" "IsIdle"
            if not exists then
                try
                    let! _ = connection.ExecuteAsync("ALTER TABLE dspFlowHistory ADD COLUMN IsIdle INTEGER NOT NULL DEFAULT 0")
                    logger.LogInformation("Added IsIdle column to dspFlowHistory table")
                with ex ->
                    logger.LogWarning(ex, "Failed to add IsIdle column (may already exist)")
        }

    // ===== Public API =====

    /// Unified 모드 전용: 스키마 생성은 Ev2Bootstrap이 담당
    let createSchemaAsync (paths: DatabasePaths) (logger: ILogger) : Task<bool> =
        task {
            try
                // Unified 모드: EV2가 스키마 생성 담당
                logger.LogInformation("Unified mode: Schema creation delegated to EV2 (no action needed)")
                return true
            with ex ->
                logger.LogError(ex, "Failed to verify DSP database schema")
                return false
        }

    /// Flow 대량 삽입 (AASX 초기 로드용)
    let bulkInsertFlowsAsync (paths: DatabasePaths) (logger: ILogger) (flows: DspFlowEntity list) : Task<int> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let flowTable = paths.GetFlowTableName()
            let callTable = paths.GetCallTableName()

            // 테이블 존재 확인
            let! exists = tablesExistAsync connection flowTable callTable
            if not exists then
                logger.LogWarning("Tables do not exist yet, cannot insert {Count} flows. Waiting for schema initialization.", flows.Length)
                return 0
            else
                use transaction = connection.BeginTransaction()
                try
                    let sql = sprintf """
                        INSERT INTO %s (FlowName, MT, WT, CT, AvgMT, AvgWT, AvgCT, State, MovingStartName, MovingEndName)
                        VALUES (@FlowName, @MT, @WT, @CT, @AvgMT, @AvgWT, @AvgCT, @State, @MovingStartName, @MovingEndName)
                        ON CONFLICT (FlowName) DO UPDATE SET
                            MT = COALESCE(excluded.MT, %s.MT),
                            WT = COALESCE(excluded.WT, %s.WT),
                            CT = COALESCE(excluded.CT, %s.CT),
                            AvgMT = COALESCE(excluded.AvgMT, %s.AvgMT),
                            AvgWT = COALESCE(excluded.AvgWT, %s.AvgWT),
                            AvgCT = COALESCE(excluded.AvgCT, %s.AvgCT),
                            State = excluded.State,
                            MovingStartName = excluded.MovingStartName,
                            MovingEndName = excluded.MovingEndName,
                            UpdatedAt = datetime('now')""" flowTable flowTable flowTable flowTable flowTable flowTable flowTable

                    // F# Option을 Nullable로 변환한 DTO 사용
                    let dtos = flows |> List.map DapperFlowDto.FromEntity
                    let! count = connection.ExecuteAsync(sql, dtos, transaction)
                    transaction.Commit() |> ignore

                    logger.LogInformation("Inserted {Count} flows into DSP database (Table: {Table})", count, flowTable)
                    return count
                with ex ->
                    transaction.Rollback()
                    logger.LogError(ex, "Failed to bulk insert flows")
                    return raise ex
        }

    /// Call 대량 삽입 (AASX 초기 로드용)
    let bulkInsertCallsAsync (paths: DatabasePaths) (logger: ILogger) (calls: DspCallEntity list) : Task<int> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let flowTable = paths.GetFlowTableName()
            let callTable = paths.GetCallTableName()

            // 테이블 존재 확인
            let! exists = tablesExistAsync connection flowTable callTable
            if not exists then
                logger.LogWarning("Tables do not exist yet, cannot insert {Count} calls. Waiting for schema initialization.", calls.Length)
                return 0
            else
                use transaction = connection.BeginTransaction()
                try
                    // 중복 제거 (CallName, FlowName, WorkName 기준)
                    let uniqueCalls =
                        calls
                        |> List.groupBy (fun c -> (c.CallName, c.FlowName, c.WorkName))
                        |> List.map (fun (_, group) -> group |> List.head)

                    if uniqueCalls.Length < calls.Length then
                        logger.LogWarning(
                            "Input data contains {DuplicateCount} duplicate calls (Total: {Total}, Unique: {Unique})",
                            calls.Length - uniqueCalls.Length, calls.Length, uniqueCalls.Length)

                    // Flow 존재 보장
                    let flowNames = uniqueCalls |> List.map (fun c -> c.FlowName) |> List.distinct
                    for flowName in flowNames do
                        let! _ = connection.ExecuteAsync(
                                    sprintf "INSERT INTO %s (FlowName) VALUES (@FlowName) ON CONFLICT (FlowName) DO NOTHING" flowTable,
                                    {| FlowName = flowName |},
                                    transaction)
                        ()

                    // Call 삽입
                    let sql = sprintf """
                        INSERT INTO %s (CallId, CallName, ApiCall, WorkName, FlowName, Next, Prev, AutoPre, CommonPre, State, ProgressRate, Device)
                        VALUES (@CallId, @CallName, @ApiCall, @WorkName, @FlowName, @Next, @Prev, @AutoPre, @CommonPre, @State, @ProgressRate, @Device)
                        ON CONFLICT (CallName, FlowName, WorkName) DO UPDATE SET
                            CallId = excluded.CallId,
                            ApiCall = excluded.ApiCall,
                            Next = excluded.Next,
                            Prev = excluded.Prev,
                            AutoPre = excluded.AutoPre,
                            CommonPre = excluded.CommonPre,
                            State = excluded.State,
                            ProgressRate = excluded.ProgressRate,
                            Device = excluded.Device,
                            UpdatedAt = datetime('now')""" callTable

                    // F# Option을 Nullable로 변환한 DTO 사용
                    let dtos = uniqueCalls |> List.map DapperCallDto.FromEntity
                    let! count = connection.ExecuteAsync(sql, dtos, transaction)
                    transaction.Commit() |> ignore

                    logger.LogInformation("Inserted {Count} calls into DSP database", count)
                    return count
                with
                | ex ->
                    transaction.Rollback()
                    return raise ex
        }

    /// Call 상태 조회 (CallKey 기반)
    let getCallStateAsync (paths: DatabasePaths) (logger: ILogger) (key: CallKey) : Task<string> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let flowTable = paths.GetFlowTableName()
            let callTable = paths.GetCallTableName()

            // 테이블 존재 확인
            let! exists = tablesExistAsync connection flowTable callTable
            if not exists then
                logger.LogDebug("Tables do not exist yet, returning default state")
                return "Ready"
            else
                let sql = sprintf "SELECT State FROM %s WHERE FlowName = @FlowName AND CallName = @CallName LIMIT 1" callTable
                let! state = connection.QueryFirstOrDefaultAsync<string>(sql, {| FlowName = key.FlowName; CallName = key.CallName |})
                return if isNull state then "Ready" else state
        }

    /// Call 정보 조회 (WorkName, FlowName 반환)
    let getCallInfoAsync (paths: DatabasePaths) (logger: ILogger) (callName: string) : Task<(string * string) option> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let flowTable = paths.GetFlowTableName()
            let callTable = paths.GetCallTableName()

            let! exists = tablesExistAsync connection flowTable callTable
            if not exists then
                logger.LogDebug("Tables do not exist yet")
                return None
            else
                let sql = sprintf "SELECT WorkName, FlowName FROM %s WHERE CallName = @CallName LIMIT 1" callTable
                let! result = connection.QueryFirstOrDefaultAsync<CallInfoDto>(sql, {| CallName = callName |})
                if isNull (box result) then
                    return None
                else
                    return Some (result.WorkName, result.FlowName)
        }

    /// Call 전체 데이터 조회 (GoingCount 등 포함, CallKey 기반)
    let getCallByKeyAsync (paths: DatabasePaths) (logger: ILogger) (key: CallKey) : Task<DspCallEntity option> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let flowTable = paths.GetFlowTableName()
            let callTable = paths.GetCallTableName()

            let! exists = tablesExistAsync connection flowTable callTable
            if not exists then
                logger.LogDebug("Tables do not exist yet")
                return None
            else
                let sql = sprintf """
                    SELECT CallName, ApiCall, WorkName, FlowName, Next, Prev, AutoPre, CommonPre,
                           State, ProgressRate, Device, PreviousGoingTime, AverageGoingTime, StdDevGoingTime, GoingCount
                    FROM %s
                    WHERE FlowName = @FlowName AND CallName = @CallName
                    LIMIT 1""" callTable

                let! result = connection.QueryFirstOrDefaultAsync<DspCallEntity>(sql, {| FlowName = key.FlowName; CallName = key.CallName |})
                if isNull (box result) then
                    return None
                else
                    return Some result
        }

    /// Call 상태 업데이트 (CallKey 기반)
    let updateCallStateAsync (paths: DatabasePaths) (logger: ILogger) (key: CallKey) (state: string) : Task<bool> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let flowTable = paths.GetFlowTableName()
            let callTable = paths.GetCallTableName()

            let! exists = tablesExistAsync connection flowTable callTable
            if not exists then
                logger.LogDebug("Tables do not exist yet, skipping update")
                return false
            else
                let sql = sprintf """
                    UPDATE %s
                    SET State = @State,
                        UpdatedAt = datetime('now')
                    WHERE FlowName = @FlowName AND CallName = @CallName""" callTable

                let! result = connection.ExecuteAsync(sql, {| State = state; FlowName = key.FlowName; CallName = key.CallName |})
                return result > 0
        }

    /// Call 상태 및 통계 업데이트 (Going → Finish 시)
    let updateCallWithStatisticsAsync
        (paths: DatabasePaths)
        (logger: ILogger)
        (key: CallKey)
        (state: string)
        (previousGoingTime: int)
        (averageGoingTime: float)
        (stdDevGoingTime: float) : Task<bool> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let flowTable = paths.GetFlowTableName()
            let callTable = paths.GetCallTableName()

            let! exists = tablesExistAsync connection flowTable callTable
            if not exists then
                logger.LogDebug("Tables do not exist yet, skipping update")
                return false
            else
                let sql = sprintf """
                    UPDATE %s
                    SET State = @State,
                        PreviousGoingTime = @PreviousGoingTime,
                        AverageGoingTime = @AverageGoingTime,
                        StdDevGoingTime = @StdDevGoingTime,
                        GoingCount = GoingCount + 1,
                        UpdatedAt = datetime('now')
                    WHERE FlowName = @FlowName AND CallName = @CallName""" callTable

                let! result = connection.ExecuteAsync(sql, {|
                    State = state
                    PreviousGoingTime = previousGoingTime
                    AverageGoingTime = averageGoingTime
                    StdDevGoingTime = stdDevGoingTime
                    FlowName = key.FlowName
                    CallName = key.CallName
                |})

                if result > 0 then
                    logger.LogDebug(
                        "Updated Call '{CallName}' (Flow: {FlowName}): State={State}, GoingTime={Time}ms, Avg={Avg:F0}ms, StdDev={StdDev:F0}ms",
                        key.CallName, key.FlowName, state, previousGoingTime, averageGoingTime, stdDevGoingTime)

                return result > 0
        }

    /// Flow 상태 업데이트
    let updateFlowStateAsync (paths: DatabasePaths) (logger: ILogger) (flowName: string) (state: string) : Task<bool> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let flowTable = paths.GetFlowTableName()
            let callTable = paths.GetCallTableName()

            let! exists = tablesExistAsync connection flowTable callTable
            if not exists then
                logger.LogDebug("Tables do not exist yet, skipping update")
                return false
            else
                let sql = sprintf """
                    UPDATE %s
                    SET State = @State,
                        UpdatedAt = datetime('now')
                    WHERE FlowName = @FlowName""" flowTable

                let! result = connection.ExecuteAsync(sql, {| State = state; FlowName = flowName |})
                return result > 0
        }

    /// Flow 내 Going 상태 Call 존재 여부 확인
    let hasGoingCallsInFlowAsync (paths: DatabasePaths) (logger: ILogger) (flowName: string) : Task<bool> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let flowTable = paths.GetFlowTableName()
            let callTable = paths.GetCallTableName()

            let! exists = tablesExistAsync connection flowTable callTable
            if not exists then
                logger.LogDebug("Tables do not exist yet")
                return false
            else
                let sql = sprintf "SELECT COUNT(*) FROM %s WHERE FlowName = @FlowName AND State = 'Going'" callTable
                let! count = connection.ExecuteScalarAsync<int>(sql, {| FlowName = flowName |})
                return count > 0
        }

    /// Flow 메트릭 업데이트 (MT, WT, CT, MovingStartName, MovingEndName)
    let updateFlowMetricsAsync
        (paths: DatabasePaths)
        (flowName: string)
        (mt: int option)
        (wt: int option)
        (ct: int option)
        (movingStartName: string option)
        (movingEndName: string option) : Task<bool> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            let flowTable = paths.GetFlowTableName()

            let sql = sprintf """
                UPDATE %s
                SET MT = @MT,
                    WT = @WT,
                    CT = @CT,
                    MovingStartName = @MovingStartName,
                    MovingEndName = @MovingEndName,
                    UpdatedAt = datetime('now')
                WHERE FlowName = @FlowName""" flowTable

            // Option을 Nullable로 변환
            let mtNullable = match mt with Some v -> Nullable v | None -> Nullable()
            let wtNullable = match wt with Some v -> Nullable v | None -> Nullable()
            let ctNullable = match ct with Some v -> Nullable v | None -> Nullable()
            let movingStartNameStr = match movingStartName with Some v -> v | None -> null
            let movingEndNameStr = match movingEndName with Some v -> v | None -> null

            let! result = connection.ExecuteAsync(sql, {|
                MT = mtNullable
                WT = wtNullable
                CT = ctNullable
                MovingStartName = movingStartNameStr
                MovingEndName = movingEndNameStr
                FlowName = flowName
            |})

            return result > 0
        }

    /// Flow 메트릭 및 평균값 업데이트 (사이클 완료 시 호출)
    let updateFlowWithAveragesAsync
        (paths: DatabasePaths)
        (logger: ILogger)
        (flowName: string)
        (mt: int)
        (wt: int)
        (ct: int)
        (avgMT: float)
        (avgWT: float)
        (avgCT: float)
        (movingStartName: string option)
        (movingEndName: string option) : Task<bool> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            let flowTable = paths.GetFlowTableName()

            let sql = sprintf """
                UPDATE %s
                SET MT = @MT,
                    WT = @WT,
                    CT = @CT,
                    AvgMT = @AvgMT,
                    AvgWT = @AvgWT,
                    AvgCT = @AvgCT,
                    MovingStartName = @MovingStartName,
                    MovingEndName = @MovingEndName,
                    UpdatedAt = datetime('now')
                WHERE FlowName = @FlowName""" flowTable

            let movingStartNameStr = match movingStartName with Some v -> v | None -> null
            let movingEndNameStr = match movingEndName with Some v -> v | None -> null

            let! result = connection.ExecuteAsync(sql, {|
                MT = mt
                WT = wt
                CT = ct
                AvgMT = avgMT
                AvgWT = avgWT
                AvgCT = avgCT
                MovingStartName = movingStartNameStr
                MovingEndName = movingEndNameStr
                FlowName = flowName
            |})

            return result > 0
        }

    /// Flow 사이클 기준 Call만 업데이트 (MT/WT/CT는 유지)
    let updateFlowCycleBoundariesAsync
        (paths: DatabasePaths)
        (flowName: string)
        (movingStartName: string option)
        (movingEndName: string option) : Task<bool> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            let flowTable = paths.GetFlowTableName()

            let sql = sprintf """
                UPDATE %s
                SET MovingStartName = @MovingStartName,
                    MovingEndName = @MovingEndName,
                    UpdatedAt = datetime('now')
                WHERE FlowName = @FlowName""" flowTable

            let movingStartNameStr = match movingStartName with Some v -> v | None -> null
            let movingEndNameStr = match movingEndName with Some v -> v | None -> null

            let! result = connection.ExecuteAsync(sql, {|
                MovingStartName = movingStartNameStr
                MovingEndName = movingEndNameStr
                FlowName = flowName
            |})

            return result > 0
        }

    /// Flow History 삽입 (사이클 완료 시 호출)
    let insertFlowHistoryAsync (paths: DatabasePaths) (logger: ILogger) (history: DspFlowHistoryEntity) : Task<int> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let historyTable = "dspFlowHistory"

            // 테이블 존재 확인
            let! tableExists = tableExistsAsync connection historyTable
            if not tableExists then
                logger.LogWarning("dspFlowHistory table does not exist yet")
                return 0
            else
                // IsIdle 컬럼 자동 마이그레이션
                do! ensureIsIdleColumnAsync connection logger

                try
                    let sql = sprintf """
                        INSERT INTO %s (FlowName, MT, WT, CT, CycleNo, RecordedAt, IsIdle)
                        VALUES (@FlowName, @MT, @WT, @CT, @CycleNo, @RecordedAt, @IsIdle)""" historyTable

                    let dto = DapperFlowHistoryDto.FromEntity(history)
                    let! result = connection.ExecuteAsync(sql, dto)

                    logger.LogDebug(
                        "Inserted Flow history for '{FlowName}': Cycle={CycleNo}, MT={MT}ms, WT={WT}ms, CT={CT}ms",
                        history.FlowName, history.CycleNo, history.MT, history.WT, history.CT)

                    return result
                with ex ->
                    logger.LogError(ex, "Failed to insert Flow history for '{FlowName}'", history.FlowName)
                    return 0
        }

    /// Flow History 조회 (최근 N개)
    let getFlowHistoryAsync (paths: DatabasePaths) (logger: ILogger) (flowName: string) (limit: int) : Task<DspFlowHistoryEntity list> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let historyTable = "dspFlowHistory"

            let! tableExists = tableExistsAsync connection historyTable
            if not tableExists then
                return []
            else
                try
                    let sql = sprintf """
                        SELECT Id, FlowName, MT, WT, CT, CycleNo, RecordedAt, COALESCE(IsIdle, 0) AS IsIdle
                        FROM %s
                        WHERE FlowName = @FlowName
                        ORDER BY RecordedAt DESC
                        LIMIT @Limit""" historyTable

                    let! results = connection.QueryAsync<DspFlowHistoryEntity>(sql, {| FlowName = flowName; Limit = limit |})
                    return results |> Seq.toList
                with ex ->
                    logger.LogError(ex, "Failed to get Flow history for '{FlowName}'", flowName)
                    return []
        }

    /// Flow History 조회 (최근 N일)
    let getFlowHistoryByDaysAsync (paths: DatabasePaths) (logger: ILogger) (flowName: string) (days: int) : Task<DspFlowHistoryEntity list> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let historyTable = "dspFlowHistory"

            let! tableExists = tableExistsAsync connection historyTable
            if not tableExists then
                return []
            else
                try
                    let sql = sprintf """
                        SELECT Id, FlowName, MT, WT, CT, CycleNo, RecordedAt, COALESCE(IsIdle, 0) AS IsIdle
                        FROM %s
                        WHERE FlowName = @FlowName
                          AND RecordedAt >= @SinceDate
                        ORDER BY RecordedAt DESC""" historyTable

                    let sinceDate = System.DateTime.UtcNow.AddDays(float -days)
                    let! results = connection.QueryAsync<DspFlowHistoryEntity>(sql, {| FlowName = flowName; SinceDate = sinceDate |})
                    return results |> Seq.toList
                with ex ->
                    logger.LogError(ex, "Failed to get Flow history by days for '{FlowName}'", flowName)
                    return []
        }

    /// Flow History 조회 (특정 시작시간 이후)
    let getFlowHistoryByStartTimeAsync (paths: DatabasePaths) (logger: ILogger) (flowName: string) (startTime: DateTime) : Task<DspFlowHistoryEntity list> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let historyTable = "dspFlowHistory"

            let! tableExists = tableExistsAsync connection historyTable
            if not tableExists then
                return []
            else
                try
                    let sql = sprintf """
                        SELECT Id, FlowName, MT, WT, CT, CycleNo, RecordedAt, COALESCE(IsIdle, 0) AS IsIdle
                        FROM %s
                        WHERE FlowName = @FlowName
                          AND RecordedAt >= @SinceDate
                        ORDER BY RecordedAt DESC""" historyTable

                    let! results = connection.QueryAsync<DspFlowHistoryEntity>(sql, {| FlowName = flowName; SinceDate = startTime |})
                    return results |> Seq.toList
                with ex ->
                    logger.LogError(ex, "Failed to get Flow history by start time for '{FlowName}'", flowName)
                    return []
        }

    /// Flow History 전체 삭제
    let clearFlowHistoryAsync (paths: DatabasePaths) (logger: ILogger) : Task<int> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let historyTable = "dspFlowHistory"

            let! tableExists = tableExistsAsync connection historyTable
            if not tableExists then
                logger.LogWarning("dspFlowHistory table does not exist, nothing to clear")
                return 0
            else
                try
                    let! deleted = connection.ExecuteAsync(sprintf "DELETE FROM %s" historyTable)
                    logger.LogInformation("Cleared {Count} rows from dspFlowHistory", deleted)
                    return deleted
                with ex ->
                    logger.LogError(ex, "Failed to clear dspFlowHistory")
                    return 0
        }

    /// 전체 데이터 삭제 (재초기화용)
    let clearAllDataAsync (paths: DatabasePaths) (logger: ILogger) : Task<bool> =
        task {
            try
                use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
                do! connection.OpenAsync()

                use transaction = connection.BeginTransaction()

                let flowTable = paths.GetFlowTableName()
                let callTable = paths.GetCallTableName()
                let historyTable = "dspFlowHistory"

                // History 테이블이 있으면 삭제
                let! historyExists = tableExistsAsync connection historyTable
                if historyExists then
                    let! _ = connection.ExecuteAsync(sprintf "DELETE FROM %s" historyTable, transaction = transaction)
                    ()

                let! _ = connection.ExecuteAsync(sprintf "DELETE FROM %s" callTable, transaction = transaction)
                let! _ = connection.ExecuteAsync(sprintf "DELETE FROM %s" flowTable, transaction = transaction)

                transaction.Commit()

                logger.LogInformation("Cleared all data from DSP database")
                return true
            with ex ->
                logger.LogError(ex, "Failed to clear DSP database")
                return false
        }

    /// Heatmap용 Call 통계 데이터 조회
    let getCallStatisticsAsync (paths: DatabasePaths) (logger: ILogger) : Task<CallStatisticsDto list> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let flowTable = paths.GetFlowTableName()
            let callTable = paths.GetCallTableName()

            let! exists = tablesExistAsync connection flowTable callTable
            if not exists then
                logger.LogDebug("Tables do not exist yet, returning empty list")
                return []
            else
                let sql = sprintf """
                    SELECT
                        CallId,
                        CallName,
                        FlowName,
                        WorkName,
                        AverageGoingTime,
                        StdDevGoingTime,
                        GoingCount
                    FROM %s
                    WHERE GoingCount > 0
                      AND AverageGoingTime IS NOT NULL
                      AND StdDevGoingTime IS NOT NULL
                    ORDER BY FlowName, CallName""" callTable

                let! results = connection.QueryAsync<CallStatisticsDto>(sql)
                return results |> Seq.toList
        }

    // ===== CallId 기반 메서드 (New Primary API) =====

    /// Call 상태 조회 (CallId 기반)
    let getCallStateByIdAsync (paths: DatabasePaths) (logger: ILogger) (callId: Guid) : Task<string> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let flowTable = paths.GetFlowTableName()
            let callTable = paths.GetCallTableName()

            let! exists = tablesExistAsync connection flowTable callTable
            if not exists then
                logger.LogDebug("Tables do not exist yet, returning default state")
                return "Ready"
            else
                let sql = sprintf "SELECT State FROM %s WHERE CallId = @CallId LIMIT 1" callTable
                let! state = connection.QueryFirstOrDefaultAsync<string>(sql, {| CallId = callId |})
                return if isNull state then "Ready" else state
        }

    /// Call 정보 조회 (CallId 기반 - WorkName, FlowName 반환)
    let getCallInfoByIdAsync (paths: DatabasePaths) (logger: ILogger) (callId: Guid) : Task<(string * string) option> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let flowTable = paths.GetFlowTableName()
            let callTable = paths.GetCallTableName()

            let! exists = tablesExistAsync connection flowTable callTable
            if not exists then
                logger.LogDebug("Tables do not exist yet")
                return None
            else
                let sql = sprintf "SELECT WorkName, FlowName FROM %s WHERE CallId = @CallId LIMIT 1" callTable
                let! result = connection.QueryFirstOrDefaultAsync<CallInfoDto>(sql, {| CallId = callId |})
                if isNull (box result) then
                    return None
                else
                    return Some (result.WorkName, result.FlowName)
        }

    /// Call 전체 데이터 조회 (CallId 기반)
    let getCallByIdAsync (paths: DatabasePaths) (logger: ILogger) (callId: Guid) : Task<DspCallEntity option> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let flowTable = paths.GetFlowTableName()
            let callTable = paths.GetCallTableName()

            let! exists = tablesExistAsync connection flowTable callTable
            if not exists then
                logger.LogDebug("Tables do not exist yet")
                return None
            else
                let sql = sprintf """
                    SELECT CallId, CallName, ApiCall, WorkName, FlowName, Next, Prev, AutoPre, CommonPre,
                           State, ProgressRate, Device, PreviousGoingTime, AverageGoingTime, StdDevGoingTime, GoingCount
                    FROM %s
                    WHERE CallId = @CallId
                    LIMIT 1""" callTable

                let! result = connection.QueryFirstOrDefaultAsync<DspCallEntity>(sql, {| CallId = callId |})
                if isNull (box result) then
                    return None
                else
                    return Some result
        }

    /// Call 상태 업데이트 (CallId 기반)
    let updateCallStateByIdAsync (paths: DatabasePaths) (logger: ILogger) (callId: Guid) (state: string) : Task<bool> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let flowTable = paths.GetFlowTableName()
            let callTable = paths.GetCallTableName()

            let! exists = tablesExistAsync connection flowTable callTable
            if not exists then
                logger.LogDebug("Tables do not exist yet, skipping update")
                return false
            else
                let sql = sprintf """
                    UPDATE %s
                    SET State = @State,
                        UpdatedAt = datetime('now')
                    WHERE CallId = @CallId""" callTable

                let! result = connection.ExecuteAsync(sql, {| State = state; CallId = callId |})
                return result > 0
        }

    /// Call 상태 및 통계 업데이트 (CallId 기반, Going → Finish 시)
    let updateCallWithStatisticsByIdAsync
        (paths: DatabasePaths)
        (logger: ILogger)
        (callId: Guid)
        (state: string)
        (previousGoingTime: int)
        (averageGoingTime: float)
        (stdDevGoingTime: float) : Task<bool> =
        task {
            use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
            do! connection.OpenAsync()

            let flowTable = paths.GetFlowTableName()
            let callTable = paths.GetCallTableName()

            let! exists = tablesExistAsync connection flowTable callTable
            if not exists then
                logger.LogDebug("Tables do not exist yet, skipping update")
                return false
            else
                let sql = sprintf """
                    UPDATE %s
                    SET State = @State,
                        PreviousGoingTime = @PreviousGoingTime,
                        AverageGoingTime = @AverageGoingTime,
                        StdDevGoingTime = @StdDevGoingTime,
                        GoingCount = GoingCount + 1,
                        UpdatedAt = datetime('now')
                    WHERE CallId = @CallId""" callTable

                let! result = connection.ExecuteAsync(sql, {|
                    State = state
                    PreviousGoingTime = previousGoingTime
                    AverageGoingTime = averageGoingTime
                    StdDevGoingTime = stdDevGoingTime
                    CallId = callId
                |})

                if result > 0 then
                    logger.LogDebug(
                        "Updated Call (CallId: {CallId}): State={State}, GoingTime={Time}ms, Avg={Avg:F0}ms, StdDev={StdDev:F0}ms",
                        callId, state, previousGoingTime, averageGoingTime, stdDevGoingTime)

                return result > 0
        }
