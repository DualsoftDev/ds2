using DSPilot.Infrastructure;
using DSPilot.Services;
using Microsoft.Extensions.Configuration;

namespace DSPilot.Adapters;

/// <summary>
/// Unified 모드 DSP DB 경로 리졸버.
/// IDatabasePathResolver 인터페이스 유지.
/// </summary>
public class DatabasePathResolverAdapter : IDatabasePathResolver
{
    private readonly DatabasePaths _paths;

    public bool IsUnified => true;

    public DatabasePathResolverAdapter(IConfiguration configuration, ILogger<DatabasePathResolverAdapter> logger)
    {
        _paths = DatabaseConfigLoader.Load(configuration, logger);
    }

    public DatabasePaths GetDatabasePaths() => _paths;

    public string GetSharedDbPath() => _paths.SharedDbPath;

    public string GetPlcDbPath() => _paths.SharedDbPath;

    public string GetDspDbPath() => _paths.SharedDbPath;
}
