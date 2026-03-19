using Dapper;
using Microsoft.Data.Sqlite;

namespace DSPilot.TestConsole;

/// <summary>
/// DB 내용을 빠르게 확인하는 유틸리티
/// </summary>
public static class DbInspector
{
    public static async Task InspectAsync(string dbPath)
    {
        Console.WriteLine("=== Database Inspector ===");
        Console.WriteLine($"Path: {dbPath}");
        Console.WriteLine();

        if (!File.Exists(dbPath))
        {
            Console.WriteLine($"❌ Database file not found: {dbPath}");
            return;
        }

        Console.WriteLine("✅ Database file found");
        Console.WriteLine();

        try
        {
            var connectionString = $"Data Source={dbPath};Mode=ReadOnly;";
            using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();

            // 테이블 목록
            Console.WriteLine("📋 Tables:");
            var tables = await connection.QueryAsync<string>(
                "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name");
            foreach (var table in tables)
            {
                Console.WriteLine($"  - {table}");
            }
            Console.WriteLine();

            // plcTag 개수
            var tagCount = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM plcTag");
            Console.WriteLine($"🏷️  plcTag: {tagCount:N0} tags");

            // plcTagLog 개수
            var logCount = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM plcTagLog");
            Console.WriteLine($"📝 plcTagLog: {logCount:N0} entries");
            Console.WriteLine();

            // 시간 범위
            var timeRange = await connection.QueryFirstOrDefaultAsync<(DateTime? Min, DateTime? Max)>(
                "SELECT MIN(dateTime) as Min, MAX(dateTime) as Max FROM plcTagLog");

            if (timeRange.Min.HasValue && timeRange.Max.HasValue)
            {
                Console.WriteLine($"📅 Time range:");
                Console.WriteLine($"  From: {timeRange.Min:yyyy-MM-dd HH:mm:ss.fff}");
                Console.WriteLine($"  To:   {timeRange.Max:yyyy-MM-dd HH:mm:ss.fff}");
                Console.WriteLine($"  Duration: {(timeRange.Max.Value - timeRange.Min.Value).TotalSeconds:F1} seconds");
                Console.WriteLine();
            }

            // plcTag 스키마 확인
            Console.WriteLine("🔍 plcTag table schema:");
            var tagSchema = await connection.QueryAsync<(string name, string type)>(
                "PRAGMA table_info(plcTag)");
            foreach (var col in tagSchema)
            {
                Console.WriteLine($"  {col.name,-20} {col.type}");
            }
            Console.WriteLine();

            // 샘플 태그들 (모든 컬럼)
            Console.WriteLine("🔍 Sample tags (first 10):");
            var sampleTagsDict = await connection.QueryAsync(
                "SELECT * FROM plcTag LIMIT 10");

            foreach (var tag in sampleTagsDict)
            {
                var dict = tag as IDictionary<string, object>;
                var fields = string.Join(", ", dict.Select(kvp => $"{kvp.Key}={kvp.Value}"));
                Console.WriteLine($"  {fields}");
            }
            Console.WriteLine();

            // plcTagLog 스키마 확인
            Console.WriteLine("📊 plcTagLog table schema:");
            var logSchema = await connection.QueryAsync<(string name, string type)>(
                "PRAGMA table_info(plcTagLog)");
            foreach (var col in logSchema)
            {
                Console.WriteLine($"  {col.name,-20} {col.type}");
            }
            Console.WriteLine();

            // 샘플 로그들
            Console.WriteLine("📊 Sample log entries (first 5):");
            var sampleLogs = await connection.QueryAsync(@"
                SELECT l.*, t.name as tagName
                FROM plcTagLog l
                INNER JOIN plcTag t ON l.plcTagId = t.id
                ORDER BY l.dateTime ASC
                LIMIT 5");

            foreach (var log in sampleLogs)
            {
                var dict = log as IDictionary<string, object>;
                var fields = string.Join(", ", dict.Select(kvp => $"{kvp.Key}={kvp.Value}"));
                Console.WriteLine($"  {fields}");
            }
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error inspecting database: {ex.Message}");
        }
    }
}
