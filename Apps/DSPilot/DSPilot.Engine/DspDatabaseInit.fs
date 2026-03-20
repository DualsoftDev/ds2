namespace DSPilot.Engine

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Ds2.Core

/// DSP 데이터베이스 초기화 모듈
module DspDatabaseInit =

    /// AASX에서 Flow 엔티티 리스트 생성
    let private createFlowEntities (flows: Flow list) : DspFlowEntity list =
        flows
        |> List.filter (fun f -> not (f.Name.EndsWith("_Flow", StringComparison.OrdinalIgnoreCase)))
        |> List.map (fun f ->
            { Id = 0
              FlowName = f.Name
              State = Some "Ready"
              MT = None
              WT = None
              CT = None
              MovingStartName = None
              MovingEndName = None
              CreatedAt = DateTime.UtcNow
              UpdatedAt = DateTime.UtcNow })

    /// AASX에서 Call 엔티티 리스트 생성
    let private createCallEntities
        (flows: Flow list)
        (getWorks: Guid -> Work list)
        (getCalls: Guid -> Call list)
        (logger: ILogger) : DspCallEntity list =

        let mutable callEntities = []

        for flow in flows do
            let works = getWorks flow.Id
            for work in works do
                let calls = getCalls work.Id
                for call in calls do
                    let apiCallName =
                        if call.ApiCalls.Count > 0 then
                            call.ApiCalls.[0].Name
                        else
                            call.ApiName

                    let callEntity =
                        { Id = 0
                          CallName = call.Name
                          ApiCall = apiCallName
                          WorkName = flow.Name  // Work 이름 대신 Flow 이름 사용
                          FlowName = flow.Name
                          Next = None
                          Prev = None
                          AutoPre = None
                          CommonPre = None
                          State = "Ready"
                          ProgressRate = 0.0
                          Device = if String.IsNullOrEmpty(call.DevicesAlias) then None else Some call.DevicesAlias
                          PreviousGoingTime = None
                          AverageGoingTime = None
                          StdDevGoingTime = None
                          GoingCount = 0
                          ErrorText = None
                          CreatedAt = DateTime.UtcNow
                          UpdatedAt = DateTime.UtcNow }

                    callEntities <- callEntity :: callEntities

                    // ApiCall의 InTag/OutTag 정보 로그
                    for apiCall in call.ApiCalls do
                        let inTagInfo =
                            match apiCall.InTag with
                            | Some tag -> sprintf "Name=%s, Address=%s" tag.Name tag.Address
                            | None -> "(none)"

                        let outTagInfo =
                            match apiCall.OutTag with
                            | Some tag -> sprintf "Name=%s, Address=%s" tag.Name tag.Address
                            | None -> "(none)"

                        logger.LogDebug(
                            "Call '{CallName}' (Flow: {FlowName}) - ApiCall: {ApiCallName}, InTag: [{InTag}], OutTag: [{OutTag}]",
                            call.Name, flow.Name, apiCall.Name, inTagInfo, outTagInfo)

        callEntities |> List.rev

    /// AASX에서 초기 데이터 로드
    let private initializeFromAasxAsync
        (paths: DatabasePaths)
        (logger: ILogger)
        (getAllFlows: unit -> Flow list)
        (getWorks: Guid -> Work list)
        (getCalls: Guid -> Call list) : Task<int * int> =
        task {
            try
                let allFlows = getAllFlows()
                logger.LogInformation("Total flows in AASX: {Count}", allFlows.Length)

                // "_Flow" 접미사를 가진 Flow 제외
                let flows = allFlows |> List.filter (fun f -> not (f.Name.EndsWith("_Flow", StringComparison.OrdinalIgnoreCase)))
                logger.LogInformation("Filtered flows (excluding '*_Flow'): {Count}", flows.Length)

                // Flow 데이터 변환 및 삽입
                let flowEntities = createFlowEntities allFlows
                let! flowCount = DspRepository.bulkInsertFlowsAsync paths logger flowEntities
                logger.LogInformation("BulkInsertFlowsAsync returned: {Count} flows (expected: {Expected})", flowCount, flowEntities.Length)

                // Call 데이터 변환 및 삽입
                let callEntities = createCallEntities flows getWorks getCalls logger
                let! callCount = DspRepository.bulkInsertCallsAsync paths logger callEntities
                logger.LogInformation("BulkInsertCallsAsync returned: {Count} calls (expected: {Expected})", callCount, callEntities.Length)

                return (flowCount, callCount)
            with ex ->
                logger.LogError(ex, "Failed to initialize from AASX")
                return raise ex
        }

    /// AASX에서 데이터 로드 (재시도 로직 포함)
    let initializeFromAasxWithRetryAsync
        (paths: DatabasePaths)
        (logger: ILogger)
        (getAllFlows: unit -> Flow list)
        (getWorks: Guid -> Work list)
        (getCalls: Guid -> Call list)
        (stoppingToken: CancellationToken) : Task<bool> =
        task {
            let maxRetries = 5
            let delayMs = 2000

            let mutable attempt = 1
            let mutable success = false

            while attempt <= maxRetries && not success do
                try
                    logger.LogInformation("Attempt {Attempt}/{MaxRetries}: Loading data from AASX...", attempt, maxRetries)

                    let! (flowCount, callCount) = initializeFromAasxAsync paths logger getAllFlows getWorks getCalls

                    if flowCount > 0 || callCount > 0 then
                        logger.LogInformation("✓ Successfully loaded {FlowCount} flows and {CallCount} calls from AASX", flowCount, callCount)
                        success <- true
                    else
                        logger.LogWarning("No data was loaded (flowCount={FlowCount}, callCount={CallCount}). Schema may not be ready yet.", flowCount, callCount)
                with ex ->
                    logger.LogWarning(ex, "Attempt {Attempt}/{MaxRetries} failed: {Message}", attempt, maxRetries, ex.Message)

                if not success && attempt < maxRetries then
                    logger.LogInformation("Waiting {DelayMs}ms before retry...", delayMs)
                    do! Task.Delay(delayMs, stoppingToken)

                attempt <- attempt + 1

            return success
        }

    /// 전체 초기화 프로세스 (스키마 생성, 데이터 로드, Cleanup)
    let initializeAsync
        (paths: DatabasePaths)
        (logger: ILogger)
        (isProjectLoaded: bool)
        (getAllFlows: unit -> Flow list)
        (getWorks: Guid -> Work list)
        (getCalls: Guid -> Call list)
        (cleanupDatabase: unit -> Task<unit>)
        (stoppingToken: CancellationToken) : Task<bool> =
        task {
            logger.LogInformation("DSP Database Service starting...")

            try
                // 1. 스키마 생성
                let! schemaCreated = DspRepository.createSchemaAsync paths logger
                if not schemaCreated then
                    logger.LogError("Failed to create DSP database schema")
                    return false
                else
                    // 2. AASX 로드 확인
                    if not isProjectLoaded then
                        logger.LogWarning("AASX project not loaded. DSP database will be empty.")
                        return false
                    else
                        // 3. AASX에서 초기 데이터 로드 (재시도 로직 포함)
                        logger.LogInformation("Loading initial data from AASX...")
                        let! dataLoaded = initializeFromAasxWithRetryAsync paths logger getAllFlows getWorks getCalls stoppingToken

                        if not dataLoaded then
                            logger.LogError("Failed to load data from AASX after multiple retries")
                            return false
                        else
                            // 4. 중복 데이터 정리
                            logger.LogInformation("Cleaning up database...")
                            do! cleanupDatabase()

                            logger.LogInformation("✓ DSP Database Service initialized successfully")
                            return true
            with ex ->
                logger.LogError(ex, "Failed to initialize DSP Database Service")
                return false
        }
