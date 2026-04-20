#!/bin/bash
# Check PLC database contents

DB_PATH="/mnt/c/Users/dual/AppData/Roaming/Dualsoft/DSPilot/plc.db"

echo "=========================================="
echo "PLC Database Contents Check"
echo "=========================================="
echo "Database: $DB_PATH"
echo ""

# Use .NET to query the database
cat > /tmp/check_plc.cs << 'EOF'
using System;
using Microsoft.Data.Sqlite;
using Dapper;

var dbPath = "/mnt/c/Users/dual/AppData/Roaming/Dualsoft/DSPilot/plc.db";
using var conn = new SqliteConnection($"Data Source={dbPath}");
await conn.OpenAsync();

// List tables
Console.WriteLine("[Tables]");
var tables = await conn.QueryAsync<string>(
    "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name");
foreach (var table in tables)
{
    Console.WriteLine($"  - {table}");
}

// Check plcTagLog
Console.WriteLine("\n[plcTagLog Sample]");
var logs = await conn.QueryAsync<dynamic>(
    @"SELECT TagName, Value, Timestamp
      FROM plcTagLog
      WHERE TagName IN ('D100', 'D101', 'D102', 'D103', 'D104', 'D105')
      ORDER BY Timestamp DESC
      LIMIT 10");

foreach (var log in logs)
{
    Console.WriteLine($"  {log.Timestamp} | {log.TagName} = {log.Value}");
}

// Count by tag
Console.WriteLine("\n[Tag Event Counts]");
var counts = await conn.QueryAsync<dynamic>(
    @"SELECT TagName, COUNT(*) as Count
      FROM plcTagLog
      WHERE TagName IN ('D100', 'D101', 'D102', 'D103', 'D104', 'D105')
      GROUP BY TagName
      ORDER BY TagName");

foreach (var count in counts)
{
    Console.WriteLine($"  {count.TagName}: {count.Count} events");
}
EOF

dotnet script /tmp/check_plc.cs
