using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DSPilot.Engine;
using DSPilot.Services;

namespace DSPilot.Adapters;

/// <summary>
/// F# DatabaseConfig를 C#에서 사용하기 위한 Adapter (Unified 모드 전용)
/// IDatabasePathResolver 인터페이스 유지하여 기존 코드 호환성 보장
/// </summary>
public class DatabasePathResolverAdapter : IDatabasePathResolver
{
    private readonly DatabasePaths _paths;

    public bool IsUnified => true; // 항상 Unified 모드

    public DatabasePathResolverAdapter(IConfiguration configuration, ILogger<DatabasePathResolverAdapter> logger)
    {
        _paths = DatabaseConfig.loadDatabasePaths(configuration, logger);
        DatabaseConfig.logDatabasePaths(logger, _paths);
    }

    /// <summary>
    /// 내부 F# DatabasePaths 객체 반환 (F# 모듈에서 사용)
    /// </summary>
    public DatabasePaths GetDatabasePaths() => _paths;

    public string GetSharedDbPath() => _paths.SharedDbPath;

    public string GetPlcDbPath() => _paths.SharedDbPath; // Unified 모드: 모두 같은 경로

    public string GetDspDbPath() => _paths.SharedDbPath; // Unified 모드: 모두 같은 경로
}
