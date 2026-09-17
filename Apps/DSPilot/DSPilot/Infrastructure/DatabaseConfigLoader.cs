// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
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
            "Unified database mode: 모든 데이터가 단일 {File} 에 들어간다(시간 기반 코어 v68, doc/30 §9).",
            Kpi.KpiDb.FileName);

        var sharedPath = TryGetPathFromConnectionString(config, logger)
            ?? TryGetPathFromConfig(config, "Database:SharedDbPath")
            ?? throw new InvalidOperationException(
                "Database path is not configured. Set Database:ConnectionString with Data Source=... or specify Database:SharedDbPath.");

        // 파일 이름은 코드가 정한다 — 설정에 남아 있는 옛 이름(plc.db)이 폴더만 알려 주고 파일은 새 것으로 간다.
        // 현장 appsettings 는 업데이트 설치 때도 보존되므로, 설정을 고쳐 주길 기다리면 구 파일로 되돌아간다.
        sharedPath = RedirectToCurrentFile(sharedPath, logger);

        bool dspTablesEnabled;
        var dspEnabledStr = config["DspTables:Enabled"];
        dspTablesEnabled = !string.IsNullOrWhiteSpace(dspEnabledStr) && bool.Parse(dspEnabledStr);

        EnsureDirectoryExists(sharedPath);

        var paths = new DatabasePaths(sharedPath, dspTablesEnabled);
        LogPaths(logger, paths);
        return paths;
    }

    /// <summary>설정에 어떤 파일 이름이 적혀 있든 현재 정본 파일로 돌린다(폴더는 설정값 유지).</summary>
    private static string RedirectToCurrentFile(string configuredPath, ILogger logger)
    {
        var dir = Path.GetDirectoryName(configuredPath);
        var target = string.IsNullOrEmpty(dir)
            ? Kpi.KpiDb.FileName
            : Path.Combine(dir, Kpi.KpiDb.FileName);

        var name = Path.GetFileName(configuredPath);
        if (!string.Equals(name, Kpi.KpiDb.FileName, StringComparison.OrdinalIgnoreCase))
            logger.LogInformation("Database file redirected: {Old} → {New}", name, Kpi.KpiDb.FileName);

        return target;
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
