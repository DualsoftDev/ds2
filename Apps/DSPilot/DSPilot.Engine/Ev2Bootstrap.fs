namespace DSPilot.Engine

open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Logging

/// EV2 부트스트랩 - 데이터베이스 스키마 초기화
module Ev2Bootstrap =

    /// SQL 명령 실행
    let private executeSqlAsync (connection: SqliteConnection) (sql: string) : Task<unit> =
        task {
            use command = connection.CreateCommand()
            command.CommandText <- sql
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }

    /// EV2 기본 스키마 초기화 (Placeholder - PlcCaptureService가 실제 생성)
    let private initializeEv2SchemaAsync (paths: DatabasePaths) (logger: ILogger) : Task<unit> =
        task {
            logger.LogInformation("Initializing EV2 base schema")

            let dbPath = paths.SharedDbPath

            // EV2 schema is created by PlcCaptureService when it initializes AppDbApi
            // This method is a placeholder for future explicit EV2 schema initialization
            logger.LogInformation("EV2 base schema initialization delegated to PlcCaptureService")
            logger.LogInformation("DB Path: {DbPath}", dbPath)
        }

    /// DSPilot 확장 스키마 초기화 (dspFlow, dspCall)
    let private initializeDspSchemaAsync (paths: DatabasePaths) (logger: ILogger) : Task<unit> =
        task {
            if not paths.DspTablesEnabled then
                logger.LogInformation("DSP state tables are disabled. Skipping DSPilot extension schema creation.")
                return ()

            logger.LogInformation("Initializing DSPilot extension schema")

            let dbPath = paths.SharedDbPath
            let connectionString = sprintf "Data Source=%s;" dbPath

            use connection = new SqliteConnection(connectionString)
            do! connection.OpenAsync()

            // Drop existing DSP tables to apply schema changes
            // Legacy cleanup: dspCallIOEvent is no longer used.
            do! executeSqlAsync connection "DROP TABLE IF EXISTS dspCallIOEvent"
            do! executeSqlAsync connection "DROP TABLE IF EXISTS dspCall"
            do! executeSqlAsync connection "DROP TABLE IF EXISTS dspFlow"

            // dspFlow 테이블 생성
            do! executeSqlAsync connection """
                CREATE TABLE IF NOT EXISTS dspFlow (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FlowName TEXT NOT NULL UNIQUE,
                    MT INTEGER,
                    WT INTEGER,
                    CT INTEGER,
                    State TEXT,
                    MovingStartName TEXT,
                    MovingEndName TEXT,
                    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
                )"""

            // dspCall 테이블 생성
            do! executeSqlAsync connection """
                CREATE TABLE IF NOT EXISTS dspCall (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    CallId TEXT NOT NULL UNIQUE,
                    CallName TEXT NOT NULL,
                    ApiCall TEXT NOT NULL,
                    WorkName TEXT NOT NULL,
                    FlowName TEXT NOT NULL,
                    Next TEXT,
                    Prev TEXT,
                    AutoPre TEXT,
                    CommonPre TEXT,
                    State TEXT NOT NULL DEFAULT 'Ready',
                    ProgressRate REAL DEFAULT 0.0,
                    PreviousGoingTime INTEGER,
                    AverageGoingTime REAL,
                    StdDevGoingTime REAL,
                    GoingCount INTEGER DEFAULT 0,
                    Device TEXT,
                    ErrorText TEXT,
                    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                    UNIQUE(CallName, FlowName, WorkName),
                    FOREIGN KEY(FlowName) REFERENCES dspFlow(FlowName) ON DELETE CASCADE
                )"""

            // dspFlow 인덱스 생성
            do! executeSqlAsync connection """
                CREATE INDEX IF NOT EXISTS idx_dspFlow_FlowName
                ON dspFlow(FlowName)"""

            do! executeSqlAsync connection """
                CREATE INDEX IF NOT EXISTS idx_dspFlow_State
                ON dspFlow(State)"""

            // dspCall 인덱스 생성
            do! executeSqlAsync connection """
                CREATE INDEX IF NOT EXISTS idx_dspCall_FlowName
                ON dspCall(FlowName)"""

            do! executeSqlAsync connection """
                CREATE INDEX IF NOT EXISTS idx_dspCall_CallName
                ON dspCall(CallName)"""

            do! executeSqlAsync connection """
                CREATE INDEX IF NOT EXISTS idx_dspCall_State
                ON dspCall(State)"""

            // dspCall CallId 인덱스 생성
            do! executeSqlAsync connection """
                CREATE INDEX IF NOT EXISTS idx_dspCall_CallId
                ON dspCall(CallId)"""

            logger.LogInformation("DSPilot schema initialized at {DbPath} (Unified mode)", dbPath)
        }

    /// 전체 부트스트랩 프로세스 실행
    let startAsync (paths: DatabasePaths) (logger: ILogger) : Task<unit> =
        task {
            logger.LogInformation("Starting EV2 Bootstrap Service")

            try
                if not paths.DspTablesEnabled then
                    logger.LogInformation("DSP state tables are disabled. Skipping EV2 bootstrap DSP steps.")
                    return ()

                // Step 1: Initialize EV2 base schema
                do! initializeEv2SchemaAsync paths logger

                // Step 2: Initialize DSPilot extension schema
                do! initializeDspSchemaAsync paths logger

                logger.LogInformation("EV2 Bootstrap completed successfully")
                return ()
            with ex ->
                logger.LogError(ex, "Failed to bootstrap database schemas")
                return raise ex
        }
