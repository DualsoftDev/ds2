namespace DSPilot.Engine

open System
open System.IO
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

    /// 설정에서 경로 읽기 (기본값 포함)
    let private getPathFromConfig (config: IConfiguration) (key: string) (defaultValue: string) =
        match config.[key] with
        | null -> defaultValue
        | value -> value
        |> resolvePath

    /// 데이터베이스 경로 설정 로드 (Unified 모드 전용)
    let loadDatabasePaths (config: IConfiguration) (logger: ILogger) : DatabasePaths =
        logger.LogInformation("✓ Unified database mode: All data stored in single plc.db with EV2 base schema + DSP extensions.")

        // Unified 모드: 단일 데이터베이스 경로 사용
        let sharedPath = getPathFromConfig config "Database:SharedDbPath" "%APPDATA%/Dualsoft/DSPilot/plc.db"
        let paths = { SharedDbPath = sharedPath }

        // 디렉토리 생성
        ensureDirectoryExists paths.SharedDbPath

        paths

    /// SQLite 연결 문자열 생성
    let createConnectionString (dbPath: string) =
        sprintf "Data Source=%s" dbPath

    /// 데이터베이스 경로 정보 로깅
    let logDatabasePaths (logger: ILogger) (paths: DatabasePaths) =
        logger.LogInformation("Database configuration:")
        logger.LogInformation("  Shared DB: {Path}", paths.SharedDbPath)
        logger.LogInformation("  Flow Table: {Table}", paths.GetFlowTableName())
        logger.LogInformation("  Call Table: {Table}", paths.GetCallTableName())
