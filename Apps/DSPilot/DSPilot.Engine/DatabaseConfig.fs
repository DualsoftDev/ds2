namespace DSPilot.Engine

open System
open System.IO
open System.Data.Common
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging

/// 데이터베이스 경로 설정 및 해결 모듈
module DatabaseConfig =
    /// 환경 변수를 확장하고 경로 구분자를 정규화
    let private resolvePath (path: string) =
        let expanded = Environment.ExpandEnvironmentVariables(path)
        expanded.Replace('/', Path.DirectorySeparatorChar)

    /// 디렉토리가 없으면 생성
    let ensureDirectoryExists (dbPath: string) =
        let directory = Path.GetDirectoryName(dbPath)
        if not (String.IsNullOrEmpty(directory)) && not (Directory.Exists(directory)) then
            Directory.CreateDirectory(directory) |> ignore

    /// 설정에서 경로 읽기
    let private tryGetPathFromConfig (config: IConfiguration) (key: string) =
        match config.[key] with
        | null -> None
        | value when String.IsNullOrWhiteSpace(value) -> None
        | value -> value |> resolvePath |> Some

    let private tryGetPathFromConnectionString (config: IConfiguration) (logger: ILogger) =
        let dbType = config.["Database:Type"]
        let connStr = config.["Database:ConnectionString"]

        if String.IsNullOrWhiteSpace(connStr) then
            None
        elif not (String.IsNullOrWhiteSpace(dbType)) &&
             not (dbType.Equals("Sqlite", StringComparison.OrdinalIgnoreCase)) then
            logger.LogWarning("Database:Type={DbType} is not supported by DSPilot unified DB path resolver. Falling back to file path settings.", dbType)
            None
        else
            try
                let expandedConnStr = Environment.ExpandEnvironmentVariables(connStr)
                let builder = DbConnectionStringBuilder()
                builder.ConnectionString <- expandedConnStr

                let tryGetValue key =
                    let mutable value = null
                    if builder.TryGetValue(key, &value) && not (isNull value) then
                        Some(string value)
                    else
                        None

                let dataSource =
                    tryGetValue "Data Source"
                    |> Option.orElseWith (fun () -> tryGetValue "DataSource")
                    |> Option.orElseWith (fun () -> tryGetValue "Filename")

                match dataSource with
                | Some value when not (String.IsNullOrWhiteSpace(value)) ->
                    value |> resolvePath |> Some
                | _ ->
                    None
            with ex ->
                logger.LogWarning(ex, "Failed to parse Database:ConnectionString. Falling back to file path settings.")
                None

    /// 데이터베이스 경로 설정 로드 (Unified 모드 전용)
    let loadDatabasePaths (config: IConfiguration) (logger: ILogger) : DatabasePaths =
        logger.LogInformation("✓ Unified database mode: All data stored in single plc.db with EV2 base schema + DSP extensions.")

        let sharedPath =
            tryGetPathFromConnectionString config logger
            |> Option.orElseWith (fun () ->
                tryGetPathFromConfig config "Database:SharedDbPath")
            |> Option.defaultWith (fun () ->
                invalidOp "Database path is not configured. Set Database:ConnectionString with Data Source=... or specify Database:SharedDbPath.")
        let dspTablesEnabled =
            match config.["DspTables:Enabled"] with
            | null -> false
            | value when String.IsNullOrWhiteSpace(value) -> false
            | value -> Boolean.Parse(value)
        let paths =
            { SharedDbPath = sharedPath
              DspTablesEnabled = dspTablesEnabled }

        // 디렉토리 생성
        ensureDirectoryExists paths.SharedDbPath

        paths

    /// SQLite 연결 문자열 생성
    let createConnectionString (dbPath: string) =
        sprintf "Data Source=%s;Mode=ReadWriteCreate;Default Timeout=20" dbPath

    /// 데이터베이스 경로 정보 로깅
    let logDatabasePaths (logger: ILogger) (paths: DatabasePaths) =
        logger.LogInformation("Database configuration:")
        logger.LogInformation("  Shared DB: {Path}", paths.SharedDbPath)
        logger.LogInformation("  DSP Tables Enabled: {Enabled}", paths.DspTablesEnabled)
        logger.LogInformation("  Flow Table: {Table}", paths.GetFlowTableName())
        logger.LogInformation("  Call Table: {Table}", paths.GetCallTableName())
