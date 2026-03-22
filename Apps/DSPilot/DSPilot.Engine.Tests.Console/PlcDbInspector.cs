using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Dapper;

namespace DSPilot.Engine.Tests.Console;

public class PlcDbInspector
{
    public static async Task InspectDatabase(string dbPath)
    {
        System.Console.WriteLine("\n========================================");
        System.Console.WriteLine("PLC Database Inspection");
        System.Console.WriteLine("========================================");
        System.Console.WriteLine($"Database: {dbPath}");

        if (!System.IO.File.Exists(dbPath))
        {
            System.Console.WriteLine("[ERROR] Database file not found!");
            return;
        }

        var fileInfo = new System.IO.FileInfo(dbPath);
        System.Console.WriteLine($"Size: {fileInfo.Length / 1024} KB");
        System.Console.WriteLine($"Modified: {fileInfo.LastWriteTime}");
        System.Console.WriteLine();

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        // List all tables
        System.Console.WriteLine("[Tables]");
        var tables = await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name");
        foreach (var table in tables)
        {
            System.Console.WriteLine($"  - {table}");
        }

        // Check if plcTagLog exists
        var hasPlcTagLog = tables.Any(t => t == "plcTagLog");

        if (hasPlcTagLog)
        {
            // First, get table schema
            System.Console.WriteLine("\n[plcTagLog - Schema]");
            var schema = await conn.QueryAsync<dynamic>("PRAGMA table_info(plcTagLog)");
            foreach (var col in schema)
            {
                System.Console.WriteLine($"  {col.name} ({col.type})");
            }

            System.Console.WriteLine("\n[plcTagLog - Sample Data]");
            var sample = await conn.QueryAsync<dynamic>(
                "SELECT * FROM plcTagLog LIMIT 5");

            var sampleList = sample.ToList();
            if (sampleList.Count > 0)
            {
                // Print first row to see column names
                var firstRow = sampleList[0] as IDictionary<string, object>;
                System.Console.WriteLine($"  Columns: {string.Join(", ", firstRow.Keys)}");

                foreach (var row in sampleList)
                {
                    var dict = row as IDictionary<string, object>;
                    System.Console.WriteLine($"  Row: {string.Join(" | ", dict.Values)}");
                }
            }

            System.Console.WriteLine("\n[plcTagLog - Recent Events]");
            var logs = await conn.QueryAsync<dynamic>(
                @"SELECT * FROM plcTagLog ORDER BY rowid DESC LIMIT 20");

            var logList = logs.ToList();
            System.Console.WriteLine($"Showing last {Math.Min(20, logList.Count)} events:");

            foreach (var log in logList)
            {
                var dict = log as IDictionary<string, object>;
                System.Console.WriteLine($"  {string.Join(" | ", dict.Values)}");
            }

                // Total count
            System.Console.WriteLine("\n[Total Event Count]");
            var totalCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM plcTagLog");
            System.Console.WriteLine($"  Total events: {totalCount}");
        }
        else
        {
            System.Console.WriteLine("\n⚠ plcTagLog table not found");
        }

        // Check plcTag table
        var hasPlcTag = tables.Any(t => t == "plcTag");
        if (hasPlcTag)
        {
            System.Console.WriteLine("\n[plcTag - Schema]");
            var tagSchema = await conn.QueryAsync<dynamic>("PRAGMA table_info(plcTag)");
            foreach (var col in tagSchema)
            {
                System.Console.WriteLine($"  {col.name} ({col.type})");
            }

            System.Console.WriteLine("\n[plcTag - All Tags]");
            var allTags = await conn.QueryAsync<dynamic>("SELECT * FROM plcTag");
            foreach (var tag in allTags)
            {
                var dict = tag as IDictionary<string, object>;
                if (dict != null)
                {
                    var name = dict.ContainsKey("name") ? dict["name"] : "N/A";
                    var address = dict.ContainsKey("address") ? dict["address"] : "N/A";
                    System.Console.WriteLine($"  ID:{dict["id"]} | Name:{name} | Address:{address}");
                }
            }
        }

        System.Console.WriteLine("\n========================================\n");
    }
}
