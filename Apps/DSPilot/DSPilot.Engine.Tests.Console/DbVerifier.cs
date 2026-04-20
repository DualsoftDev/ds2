using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Dapper;

namespace DSPilot.Engine.Tests.Console;

public class DbVerifier
{
    public static async Task VerifyDatabase(string dbPath)
    {
        System.Console.WriteLine("\n========================================");
        System.Console.WriteLine("Database Verification");
        System.Console.WriteLine("========================================");
        System.Console.WriteLine($"DB Path: {dbPath}");

        if (!File.Exists(dbPath))
        {
            System.Console.WriteLine("[ERROR] Database file not found!");
            return;
        }

        var fileInfo = new FileInfo(dbPath);
        System.Console.WriteLine($"DB Size: {fileInfo.Length / 1024} KB");
        System.Console.WriteLine($"Modified: {fileInfo.LastWriteTime}");

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        // Verify Flows
        System.Console.WriteLine("\n[Flows]");
        var flows = await conn.QueryAsync<dynamic>(
            "SELECT FlowName, State, ActiveCallCount, CreatedAt, UpdatedAt FROM dspFlow");

        foreach (var flow in flows)
        {
            System.Console.WriteLine($"  - {flow.FlowName}:");
            System.Console.WriteLine($"      State: {flow.State}");
            System.Console.WriteLine($"      ActiveCallCount: {flow.ActiveCallCount}");
            System.Console.WriteLine($"      Created: {flow.CreatedAt}");
            System.Console.WriteLine($"      Updated: {flow.UpdatedAt}");
        }

        // Verify Calls
        System.Console.WriteLine("\n[Calls]");
        var calls = await conn.QueryAsync<dynamic>(
            "SELECT CallName, FlowName, State, InTag, OutTag, LastStartAt, LastFinishAt, LastDurationMs FROM dspCall");

        foreach (var call in calls)
        {
            System.Console.WriteLine($"  - {call.CallName} ({call.FlowName}):");
            System.Console.WriteLine($"      State: {call.State}");
            System.Console.WriteLine($"      Tags: In={call.InTag}, Out={call.OutTag}");
            System.Console.WriteLine($"      LastStartAt: {call.LastStartAt}");
            System.Console.WriteLine($"      LastFinishAt: {call.LastFinishAt}");
            System.Console.WriteLine($"      LastDurationMs: {call.LastDurationMs}");
        }

        // Verify State Transitions
        System.Console.WriteLine("\n[State Transition Summary]");
        var completedCalls = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dspCall WHERE State = 'Done'");
        var readyCalls = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dspCall WHERE State = 'Ready'");
        var goingCalls = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dspCall WHERE State = 'Going'");

        System.Console.WriteLine($"  Completed (Done): {completedCalls}");
        System.Console.WriteLine($"  Ready: {readyCalls}");
        System.Console.WriteLine($"  Going: {goingCalls}");

        // Verify Duration Statistics
        var avgDuration = await conn.ExecuteScalarAsync<double?>(
            "SELECT AVG(LastDurationMs) FROM dspCall WHERE LastDurationMs IS NOT NULL");
        var minDuration = await conn.ExecuteScalarAsync<double?>(
            "SELECT MIN(LastDurationMs) FROM dspCall WHERE LastDurationMs IS NOT NULL");
        var maxDuration = await conn.ExecuteScalarAsync<double?>(
            "SELECT MAX(LastDurationMs) FROM dspCall WHERE LastDurationMs IS NOT NULL");

        System.Console.WriteLine("\n[Duration Statistics]");
        System.Console.WriteLine($"  Average: {avgDuration:F2} ms");
        System.Console.WriteLine($"  Min: {minDuration:F2} ms");
        System.Console.WriteLine($"  Max: {maxDuration:F2} ms");

        System.Console.WriteLine("\n========================================");
        System.Console.WriteLine("[OK] Database Verification Complete");
        System.Console.WriteLine("========================================\n");
    }
}
