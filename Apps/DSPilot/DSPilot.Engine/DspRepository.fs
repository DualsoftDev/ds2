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
                        INSERT INTO %s (FlowName, MT, WT, CT, State, MovingStartName, MovingEndName)
                        VALUES (@FlowName, @MT, @WT, @CT, @State, @MovingStartName, @MovingEndName)
                        ON CONFLICT (FlowName) DO UPDATE SET
                            MT = COALESCE(excluded.MT, %s.MT),
                            WT = COALESCE(excluded.WT, %s.WT),
                            CT = COALESCE(excluded.CT, %s.CT),
                            State = excluded.State,
                            MovingStartName = excluded.MovingStartName,
                            MovingEndName = excluded.MovingEndName,
                            UpdatedAt = datetime('now')""" flowTable flowTable flowTable flowTable

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
                        ON CONFLICT (CallId) DO UPDATE SET
                            CallName = excluded.CallName,
                            ApiCall = excluded.ApiCall,
                            WorkName = excluded.WorkName,
                            FlowName = excluded.FlowName,
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

    /// 전체 데이터 삭제 (재초기화용)
    let clearAllDataAsync (paths: DatabasePaths) (logger: ILogger) : Task<bool> =
        task {
            try
                use connection = createConnection (DatabaseConfig.createConnectionString paths.SharedDbPath)
                do! connection.OpenAsync()

                use transaction = connection.BeginTransaction()

                let flowTable = paths.GetFlowTableName()
                let callTable = paths.GetCallTableName()

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
