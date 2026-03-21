namespace DSPilot.Engine

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Logging
open Ds2.Core

/// Database initialization module
module DatabaseInitialization =

    // ===== Schema Management =====

    /// Schema management (from Ev2Bootstrap)
    module Schema =

        /// Execute SQL command
        let private executeSqlAsync (connection: SqliteConnection) (sql: string) : Task<unit> =
            task {
                use command = connection.CreateCommand()
                command.CommandText <- sql
                let! _ = command.ExecuteNonQueryAsync()
                return ()
            }

        /// Initialize EV2 base schema (Placeholder - PlcCaptureService handles actual creation)
        let private initializeEv2SchemaAsync (paths: DatabasePaths) (logger: ILogger) : Task<unit> =
            task {
                logger.LogInformation("Initializing EV2 base schema")

                let dbPath = paths.SharedDbPath

                // EV2 schema is created by PlcCaptureService when it initializes AppDbApi
                // This method is a placeholder for future explicit EV2 schema initialization
                logger.LogInformation("EV2 base schema initialization delegated to PlcCaptureService")
                logger.LogInformation("DB Path: {DbPath}", dbPath)
            }

  
        /// Execute full bootstrap process
        let startAsync (paths: DatabasePaths) (logger: ILogger) : Task<unit> =
            task {
                logger.LogInformation("Starting EV2 Bootstrap Service")

                try
                    if not paths.DspTablesEnabled then
                        logger.LogInformation("DSP state tables are disabled. Skipping DSP schema/bootstrap.")
                        return ()

                    logger.LogInformation("EV2 Bootstrap completed successfully")
                    return ()
                with ex ->
                    logger.LogError(ex, "Failed to bootstrap database schemas")
                    return raise ex
            }

    // ===== AASX Data Loading =====

    /// AASX data loading (from DspDatabaseInit)
    module AasxLoader =

        /// Create Flow entities from AASX
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

        /// Create Call entities from AASX
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
                              CallId = call.Id  // AASX Call.Id
                              CallName = call.Name
                              ApiCall = apiCallName
                              WorkName = flow.Name  // Use Flow name instead of Work name
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

                        // Log ApiCall's InTag/OutTag information
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

        /// Load initial data from AASX
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

                    // Exclude flows with "_Flow" suffix
                    let flows = allFlows |> List.filter (fun f -> not (f.Name.EndsWith("_Flow", StringComparison.OrdinalIgnoreCase)))
                    logger.LogInformation("Filtered flows (excluding '*_Flow'): {Count}", flows.Length)

                    // Convert and insert Flow data
                    let flowEntities = createFlowEntities allFlows
                    let! flowCount = DspRepository.bulkInsertFlowsAsync paths logger flowEntities
                    logger.LogInformation("BulkInsertFlowsAsync returned: {Count} flows (expected: {Expected})", flowCount, flowEntities.Length)

                    // Convert and insert Call data
                    let callEntities = createCallEntities flows getWorks getCalls logger
                    let! callCount = DspRepository.bulkInsertCallsAsync paths logger callEntities
                    logger.LogInformation("BulkInsertCallsAsync returned: {Count} calls (expected: {Expected})", callCount, callEntities.Length)

                    return (flowCount, callCount)
                with ex ->
                    logger.LogError(ex, "Failed to initialize from AASX")
                    return raise ex
            }

        /// Load data from AASX (with retry logic)
        let initializeFromAasxWithRetryAsync
            (paths: DatabasePaths)
            (logger: ILogger)
            (getAllFlows: unit -> Flow list)
            (getWorks: Guid -> Work list)
            (getCalls: Guid -> Call list)
            (stoppingToken: CancellationToken) : Task<bool> =
            task {
                if not paths.DspTablesEnabled then
                    logger.LogInformation("DSP state tables are disabled. Skipping AASX load into dspFlow/dspCall.")
                    return false
                else
                    let maxRetries = 5
                    let delayMs = 2000

                    let mutable attempt = 1
                    let mutable success = false

                    while attempt <= maxRetries && not success do
                        try
                            logger.LogInformation("Attempt {Attempt}/{MaxRetries}: Loading data from AASX...", attempt, maxRetries)

                            let! (flowCount, callCount) = initializeFromAasxAsync paths logger getAllFlows getWorks getCalls

                            if flowCount > 0 || callCount > 0 then
                                logger.LogInformation("Successfully loaded {FlowCount} flows and {CallCount} calls from AASX", flowCount, callCount)
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

    // ===== Public API =====

    /// Complete initialization process (schema creation, data loading, cleanup)
    let initializeDatabaseAsync
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
                // 1. Create schema
                do! Schema.startAsync paths logger

                // 2. Check AASX load
                if not isProjectLoaded then
                    logger.LogWarning("AASX project not loaded. DSP database will be empty.")
                    return false
                else
                    // 3. Load initial data from AASX (with retry logic)
                    logger.LogInformation("Loading initial data from AASX...")
                    let! dataLoaded = AasxLoader.initializeFromAasxWithRetryAsync paths logger getAllFlows getWorks getCalls stoppingToken

                    if not dataLoaded then
                        logger.LogError("Failed to load data from AASX after multiple retries")
                        return false
                    else
                        // 4. Clean up duplicate data
                        logger.LogInformation("Cleaning up database...")
                        do! cleanupDatabase()

                        logger.LogInformation("DSP Database Service initialized successfully")
                        return true
            with ex ->
                logger.LogError(ex, "Failed to initialize DSP Database Service")
                return false
        }
