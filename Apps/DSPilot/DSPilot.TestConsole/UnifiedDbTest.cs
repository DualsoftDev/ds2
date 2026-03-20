using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DSPilot.Services;
using DSPilot.Repositories;
using DSPilot.Models.Dsp;

namespace DSPilot.TestConsole;

/// <summary>
/// Unified database mode integration test
/// Tests schema creation and CRUD operations
/// </summary>
public static class UnifiedDbTest
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Unified Database Mode Test ===\n");

        // Setup configuration
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        // Setup logging
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var pathResolverLogger = loggerFactory.CreateLogger<DatabasePathResolver>();
        var bootstrapLogger = loggerFactory.CreateLogger<Ev2BootstrapService>();
        var repositoryLogger = loggerFactory.CreateLogger<DspRepository>();

        // Step 1: Initialize path resolver
        Console.WriteLine("Step 1: Initialize DatabasePathResolver");
        var pathResolver = new DatabasePathResolver(configuration, pathResolverLogger);
        Console.WriteLine($"  - DB Path: {pathResolver.GetDspDbPath()}");
        Console.WriteLine($"  - Unified Mode: {pathResolver.IsUnified}");
        Console.WriteLine($"  ✓ Path resolver initialized\n");

        // Step 2: Bootstrap database schema
        Console.WriteLine("Step 2: Bootstrap Database Schema");
        var bootstrapService = new Ev2BootstrapService(pathResolver, bootstrapLogger, configuration);
        await bootstrapService.StartAsync(CancellationToken.None);
        Console.WriteLine("  ✓ Schema created\n");

        // Step 3: Verify tables exist
        Console.WriteLine("Step 3: Verify Tables");
        await VerifyTablesAsync(pathResolver.GetDspDbPath());
        Console.WriteLine("  ✓ All tables verified\n");

        // Step 4: Test Repository CRUD operations
        Console.WriteLine("Step 4: Test Repository Operations");
        var repository = new DspRepository(pathResolver, repositoryLogger);
        await TestRepositoryOperationsAsync(repository);
        Console.WriteLine("  ✓ Repository operations successful\n");

        // Step 5: Test data retrieval
        Console.WriteLine("Step 5: Test Data Retrieval");
        await TestDataRetrievalAsync(repository);
        Console.WriteLine("  ✓ Data retrieval successful\n");

        Console.WriteLine("=== ALL TESTS PASSED ===\n");
    }

    private static async Task VerifyTablesAsync(string dbPath)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        var tables = new[] { "dspFlow", "dspCall", "dspCallIOEvent" };
        foreach (var table in tables)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'";
            var count = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
            if (count == 0)
                throw new Exception($"Table '{table}' does not exist!");
            Console.WriteLine($"  ✓ Table '{table}' exists");
        }
    }

    private static async Task TestRepositoryOperationsAsync(DspRepository repository)
    {
        // Insert test flows
        var flows = new List<DspFlowEntity>
        {
            new() { FlowName = "TestFlow1", MT = 1000, WT = 500, CT = 1500, State = "Active" },
            new() { FlowName = "TestFlow2", MT = 2000, WT = 1000, CT = 3000, State = "Idle" }
        };

        var flowCount = await repository.BulkInsertFlowsAsync(flows);
        Console.WriteLine($"  ✓ Inserted {flowCount} flows");

        // Insert test calls
        var calls = new List<DspCallEntity>
        {
            new()
            {
                CallName = "TestCall1",
                ApiCall = "ApiCall1",
                WorkName = "Work1",
                FlowName = "TestFlow1",
                State = "Ready",
                ProgressRate = 0.0,
                Device = "Device1"
            },
            new()
            {
                CallName = "TestCall2",
                ApiCall = "ApiCall2",
                WorkName = "Work1",
                FlowName = "TestFlow1",
                State = "Going",
                ProgressRate = 0.5,
                Device = "Device2"
            }
        };

        var callCount = await repository.BulkInsertCallsAsync(calls);
        Console.WriteLine($"  ✓ Inserted {callCount} calls");

        // Update call state
        var key = new DSPilot.Models.CallKey("TestFlow1", "TestCall1");
        var updated = await repository.UpdateCallStateAsync(key, "Going");
        Console.WriteLine($"  ✓ Updated call state: {updated}");

        // Update with statistics
        var statsUpdated = await repository.UpdateCallWithStatisticsAsync(
            key, "Finish", 1234, 1200.5, 50.3);
        Console.WriteLine($"  ✓ Updated call with statistics: {statsUpdated}");
    }

    private static async Task TestDataRetrievalAsync(DspRepository repository)
    {
        // Get call state
        var key = new DSPilot.Models.CallKey("TestFlow1", "TestCall1");
        var state = await repository.GetCallStateAsync(key);
        Console.WriteLine($"  ✓ Retrieved call state: {state}");

        // Get call by key
        var call = await repository.GetCallByKeyAsync(key);
        if (call == null)
            throw new Exception("Call not found!");
        Console.WriteLine($"  ✓ Retrieved call: {call.CallName} (State: {call.State}, Going: {call.GoingCount})");

        // Get call statistics
        var stats = await repository.GetCallStatisticsAsync();
        Console.WriteLine($"  ✓ Retrieved {stats.Count} call statistics");

        // Check if flow has going calls
        var hasGoing = await repository.HasGoingCallsInFlowAsync("TestFlow1");
        Console.WriteLine($"  ✓ Flow has going calls: {hasGoing}");
    }
}
