using System.Data.Common;
using Microsoft.Extensions.Configuration;

namespace DSPilot.Infrastructure;

/// <summary>
/// Unified 모드 DSP DB 경로 로더.
/// 원 F# DatabaseConfig.loadDatabasePaths의 pure C# 포팅.
/// </summary>
public static class DatabaseConfigLoader
{
    private static string ResolvePath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        return expanded.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string? TryGetPathFromConfig(IConfiguration config, string key)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value)) return null;
        return ResolvePath(value);
    }

    private static string? TryGetPathFromConnectionString(IConfiguration config, ILogger logger)
    {
        var dbType = config["Database:Type"];
        var connStr = config["Database:ConnectionString"];

        if (string.IsNullOrWhiteSpace(connStr)) return null;

        if (!string.IsNullOrWhiteSpace(dbType) && !dbType.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Database:Type={DbType} is not supported by DSPilot unified DB path resolver. Falling back to file path settings.",
                dbType);
            return null;
        }

        try
        {
            var expandedConnStr = Environment.ExpandEnvironmentVariables(connStr);
            var builder = new DbConnectionStringBuilder { ConnectionString = expandedConnStr };

            string? TryGet(string k)
            {
                if (builder.TryGetValue(k, out var v) && v is not null)
                    return v.ToString();
                return null;
            }

            var dataSource = TryGet("Data Source") ?? TryGet("DataSource") ?? TryGet("Filename");
            if (string.IsNullOrWhiteSpace(dataSource)) return null;
            return ResolvePath(dataSource);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse Database:ConnectionString. Falling back to file path settings.");
            return null;
        }
    }

    private static void EnsureDirectoryExists(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public static DatabasePaths Load(IConfiguration config, ILogger logger)
    {
        logger.LogInformation(
            "Unified database mode: All data stored in single plc.db with EV2 base schema + DSP extensions.");

        var sharedPath = TryGetPathFromConnectionString(config, logger)
            ?? TryGetPathFromConfig(config, "Database:SharedDbPath")
            ?? throw new InvalidOperationException(
                "Database path is not configured. Set Database:ConnectionString with Data Source=... or specify Database:SharedDbPath.");

        bool dspTablesEnabled;
        var dspEnabledStr = config["DspTables:Enabled"];
        dspTablesEnabled = !string.IsNullOrWhiteSpace(dspEnabledStr) && bool.Parse(dspEnabledStr);

        EnsureDirectoryExists(sharedPath);

        var paths = new DatabasePaths(sharedPath, dspTablesEnabled);
        LogPaths(logger, paths);
        return paths;
    }

    public static string CreateConnectionString(string dbPath)
        => $"Data Source={dbPath};Mode=ReadWriteCreate;Default Timeout=20";

    private static void LogPaths(ILogger logger, DatabasePaths paths)
    {
        logger.LogInformation("Database configuration:");
        logger.LogInformation("  Shared DB: {Path}", paths.SharedDbPath);
        logger.LogInformation("  DSP Tables Enabled: {Enabled}", paths.DspTablesEnabled);
        logger.LogInformation("  Flow Table: {Table}", paths.GetFlowTableName());
        logger.LogInformation("  Call Table: {Table}", paths.GetCallTableName());
    }
}
