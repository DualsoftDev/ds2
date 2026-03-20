namespace DSPilot.Engine

open System
open System.IO
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging

/// Database paths configuration (Unified mode only)
type DatabasePaths =
    { SharedDbPath: string }

    /// Return Flow table name (always uses dsp* prefix)
    member this.GetFlowTableName() = "dspFlow"

    member this.GetCallTableName() = "dspCall"

    member this.GetCallIOEventTableName() = "dspCallIOEvent"

/// Database configuration module
module DatabaseConfig =

    /// Expand environment variables and normalize path separators
    let private resolvePath (path: string) =
        let expanded = Environment.ExpandEnvironmentVariables(path)
        expanded.Replace('/', Path.DirectorySeparatorChar)

    /// Create directory if it doesn't exist
    let ensureDirectoryExists (dbPath: string) =
        let directory = Path.GetDirectoryName(dbPath)
        if not (String.IsNullOrEmpty(directory)) && not (Directory.Exists(directory)) then
            Directory.CreateDirectory(directory) |> ignore

    /// Read path from configuration (with default value)
    let private getPathFromConfig (config: IConfiguration) (key: string) (defaultValue: string) =
        match config.[key] with
        | null -> defaultValue
        | value -> value
        |> resolvePath

    /// Load database paths configuration (Unified mode only)
    let loadDatabasePaths (config: IConfiguration) (logger: ILogger) : DatabasePaths =
        logger.LogInformation("Unified database mode: All data stored in single plc.db with EV2 base schema + DSP extensions.")

        // Unified mode: use single database path
        let sharedPath = getPathFromConfig config "Database:SharedDbPath" "%APPDATA%/Dualsoft/DSPilot/plc.db"
        let paths = { SharedDbPath = sharedPath }

        // Create directory
        ensureDirectoryExists paths.SharedDbPath

        paths

    /// Create SQLite connection string
    let createConnectionString (dbPath: string) =
        sprintf "Data Source=%s" dbPath

    /// Log database path information
    let logDatabasePaths (logger: ILogger) (paths: DatabasePaths) =
        logger.LogInformation("Database configuration:")
        logger.LogInformation("  Shared DB: {Path}", paths.SharedDbPath)
        logger.LogInformation("  Flow Table: {Table}", paths.GetFlowTableName())
        logger.LogInformation("  Call Table: {Table}", paths.GetCallTableName())
