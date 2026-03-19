using Microsoft.Data.Sqlite;
using Dapper;

namespace DSPilot.TestConsole;

public static class DbVerifier
{
    public static async Task RunAsync(string dbPath)
    {
        Console.WriteLine("=== Database Verification ===");
        Console.WriteLine($"Path: {dbPath}");
        Console.WriteLine();

        if (!File.Exists(dbPath))
        {
            Console.WriteLine("❌ Database file not found!");
            return;
        }

        var fileInfo = new FileInfo(dbPath);
        Console.WriteLine($"✅ File exists");
        Console.WriteLine($"   Size: {fileInfo.Length} bytes");
        Console.WriteLine($"   Created: {fileInfo.CreationTime}");
        Console.WriteLine($"   Modified: {fileInfo.LastWriteTime}");
        Console.WriteLine();

        var connectionString = $"Data Source={dbPath};Mode=ReadOnly;";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        // Check tables
        Console.WriteLine("📊 Tables:");
        var tables = await connection.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name");

        foreach (var table in tables)
        {
            Console.WriteLine($"   - {table}");
        }
        Console.WriteLine();

        // Check plcTag schema
        if (tables.Contains("plcTag"))
        {
            Console.WriteLine("📋 plcTag schema:");
            var columns = await connection.QueryAsync<dynamic>(
                "SELECT name, type FROM PRAGMA_TABLE_INFO('plcTag')");

            foreach (var col in columns)
            {
                Console.WriteLine($"   - {col.name} ({col.type})");
            }
            Console.WriteLine();

            // Count tags
            var tagCount = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM plcTag");
            Console.WriteLine($"📌 Tag count: {tagCount}");
            Console.WriteLine();

            if (tagCount > 0)
            {
                Console.WriteLine("🏷️  Sample tags:");
                var tags = await connection.QueryAsync<dynamic>(
                    "SELECT id, name, address, dataType FROM plcTag LIMIT 5");

                foreach (var tag in tags)
                {
                    Console.WriteLine($"   [{tag.id}] {tag.name} @ {tag.address} ({tag.dataType})");
                }
                Console.WriteLine();
            }
        }

        // Check plcTagLog schema
        if (tables.Contains("plcTagLog"))
        {
            Console.WriteLine("📋 plcTagLog schema:");
            var columns = await connection.QueryAsync<dynamic>(
                "SELECT name, type FROM PRAGMA_TABLE_INFO('plcTagLog')");

            foreach (var col in columns)
            {
                Console.WriteLine($"   - {col.name} ({col.type})");
            }
            Console.WriteLine();

            // Count logs
            var logCount = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM plcTagLog");
            Console.WriteLine($"📝 Log count: {logCount}");
            Console.WriteLine();

            if (logCount > 0)
            {
                Console.WriteLine("📜 Recent logs:");
                var logs = await connection.QueryAsync<dynamic>(
                    @"SELECT l.id, t.name, l.dateTime, l.value
                      FROM plcTagLog l
                      JOIN plcTag t ON l.plcTagId = t.id
                      ORDER BY l.dateTime DESC LIMIT 5");

                foreach (var log in logs)
                {
                    Console.WriteLine($"   [{log.id}] {log.name} = {log.value} @ {log.dateTime}");
                }
                Console.WriteLine();
            }
        }

        Console.WriteLine("✅ Database verification complete");
    }
}
