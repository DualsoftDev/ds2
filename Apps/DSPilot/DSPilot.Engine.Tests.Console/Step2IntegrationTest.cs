using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Dapper;
using DSPilot.Engine.Core;
using DSPilot.Engine.Tracking;

namespace DSPilot.Engine.Tests.Console;

/// <summary>
/// Step 2: Complete Integration Test
/// EdgeDetection + PlcToCallMapper + StateTransition
/// </summary>
public class Step2IntegrationTest
{
    private readonly string _testDbPath;

    public Step2IntegrationTest()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), "dspilot_test_step2.db");
    }

    public async Task Run()
    {
        System.Console.WriteLine("\n========================================");
        System.Console.WriteLine("Step 2: Integration Test");
        System.Console.WriteLine("EdgeDetection + Mapper + StateTransition");
        System.Console.WriteLine("========================================\n");

        // Clean up
        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
            System.Console.WriteLine($"✓ Cleaned up test DB");
        }

        try
        {
            // Setup
            await SetupDatabase();
            await SeedTestData();

            // Test 1: EdgeDetection
            System.Console.WriteLine("\n[Test 1] EdgeDetection");
            System.Console.WriteLine("------------------------");
            await TestEdgeDetection();

            // Test 2: Tag to Call Mapping
            System.Console.WriteLine("\n[Test 2] Tag to Call Mapping");
            System.Console.WriteLine("------------------------");
            await TestTagMapping();

            // Test 3: State Transition (Full Workflow)
            System.Console.WriteLine("\n[Test 3] State Transition Workflow");
            System.Console.WriteLine("------------------------");
            await TestStateTransition();

            System.Console.WriteLine("\n========================================");
            System.Console.WriteLine("[OK] Step 2 Integration Test Complete!");
            System.Console.WriteLine("========================================");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"\n[ERROR] Integration Test Failed: {ex.Message}");
            System.Console.WriteLine(ex.StackTrace);
            throw;
        }
    }

    private async Task SetupDatabase()
    {
        System.Console.WriteLine("[Setup] Creating database schema...");

        using var conn = new SqliteConnection($"Data Source={_testDbPath}");
        await conn.OpenAsync();

        // Migration 001
        await conn.ExecuteAsync(File.ReadAllText(
            "/mnt/c/ds/ds2/Apps/DSPilot/DSPilot.Engine/Database/Migrations/001_initial_schema.sql"));

        // Migration 002
        await conn.ExecuteAsync(File.ReadAllText(
            "/mnt/c/ds/ds2/Apps/DSPilot/DSPilot.Engine/Database/Migrations/002_add_plc_event_fields.sql"));

        System.Console.WriteLine("  ✓ Schema created (Migration 001 + 002)");
    }

    private async Task SeedTestData()
    {
        System.Console.WriteLine("[Setup] Seeding test data...");

        using var conn = new SqliteConnection($"Data Source={_testDbPath}");
        await conn.OpenAsync();

        var now = DateTime.UtcNow.ToString("o");

        // Insert Flows
        await conn.ExecuteAsync("""
            INSERT INTO dspFlow (FlowName, State, ActiveCallCount, CreatedAt, UpdatedAt)
            VALUES (@FlowName, @State, @ActiveCallCount, @CreatedAt, @UpdatedAt)
            """, new
        {
            FlowName = "Flow1",
            State = "Ready",
            ActiveCallCount = 0,
            CreatedAt = now,
            UpdatedAt = now
        });

        // Insert Calls with InTag/OutTag
        await conn.ExecuteAsync("""
            INSERT INTO dspCall (CallName, FlowName, State, InTag, OutTag, CreatedAt, UpdatedAt)
            VALUES (@CallName, @FlowName, @State, @InTag, @OutTag, @CreatedAt, @UpdatedAt)
            """, new[]
        {
            new { CallName = "Work1", FlowName = "Flow1", State = "Ready", InTag = "D100", OutTag = "D101", CreatedAt = now, UpdatedAt = now },
            new { CallName = "Work2", FlowName = "Flow1", State = "Ready", InTag = "D102", OutTag = "D103", CreatedAt = now, UpdatedAt = now }
        });

        System.Console.WriteLine("  ✓ Test data seeded");
        System.Console.WriteLine("    - Flow1 (Ready)");
        System.Console.WriteLine("    - Work1: InTag=D100, OutTag=D101");
        System.Console.WriteLine("    - Work2: InTag=D102, OutTag=D103");
    }

    private async Task TestEdgeDetection()
    {
        var tracker = new TagStateTracker();

        // Event 1: D100 = 0
        var edge1 = tracker.DetectEdge(new PlcTagEvent
        {
            TagName = "D100",
            Value = false,
            Timestamp = DateTime.UtcNow,
            Source = "Test"
        });

        System.Console.WriteLine($"  Event: D100=0 → Edge: {(edge1.HasValue ? edge1.Value.EdgeType.ToString() : "None")}");

        // Event 2: D100 = 1 (Rising Edge)
        var edge2 = tracker.DetectEdge(new PlcTagEvent
        {
            TagName = "D100",
            Value = true,
            Timestamp = DateTime.UtcNow,
            Source = "Test"
        });

        System.Console.WriteLine($"  Event: D100=1 → Edge: {(edge2.HasValue ? edge2.Value.EdgeType.ToString() : "None")}");

        if (edge2.HasValue && edge2.Value.EdgeType == EdgeType.RisingEdge)
        {
            System.Console.WriteLine("  ✓ Rising Edge detected correctly");
        }
        else
        {
            throw new Exception("Rising Edge detection failed!");
        }

        // Event 3: D100 = 0 (Falling Edge)
        var edge3 = tracker.DetectEdge(new PlcTagEvent
        {
            TagName = "D100",
            Value = false,
            Timestamp = DateTime.UtcNow,
            Source = "Test"
        });

        System.Console.WriteLine($"  Event: D100=0 → Edge: {(edge3.HasValue ? edge3.Value.EdgeType.ToString() : "None")}");

        if (edge3.HasValue && edge3.Value.EdgeType == EdgeType.FallingEdge)
        {
            System.Console.WriteLine("  ✓ Falling Edge detected correctly");
        }

        await Task.CompletedTask;
    }

    private async Task TestTagMapping()
    {
        var mapper = new PlcToCallMapper();

        // Load mappings from database
        using var conn = new SqliteConnection($"Data Source={_testDbPath}");
        await conn.OpenAsync();

        var calls = await conn.QueryAsync<dynamic>(
            "SELECT CallName, InTag, OutTag FROM dspCall WHERE InTag IS NOT NULL");

        foreach (var call in calls)
        {
            if (!string.IsNullOrEmpty(call.InTag))
                mapper.AddInTag(call.InTag, call.CallName);
            if (!string.IsNullOrEmpty(call.OutTag))
                mapper.AddOutTag(call.OutTag, call.CallName);
        }

        System.Console.WriteLine("  ✓ Tag mappings loaded from database");

        // Test mapping
        var work1ByInTag = mapper.GetCallByInTag("D100");
        System.Console.WriteLine($"  D100 (InTag) → {work1ByInTag.GetValueOrDefault("NOT FOUND")}");

        var work1ByOutTag = mapper.GetCallByOutTag("D101");
        System.Console.WriteLine($"  D101 (OutTag) → {work1ByOutTag.GetValueOrDefault("NOT FOUND")}");

        if (work1ByInTag == "Work1" && work1ByOutTag == "Work1")
        {
            System.Console.WriteLine("  ✓ Tag to Call mapping works correctly");
        }
        else
        {
            throw new Exception("Tag mapping failed!");
        }
    }

    private async Task TestStateTransition()
    {
        var tracker = new TagStateTracker();
        var mapper = new PlcToCallMapper();

        // Load mappings
        using var conn = new SqliteConnection($"Data Source={_testDbPath}");
        await conn.OpenAsync();

        var calls = await conn.QueryAsync<dynamic>(
            "SELECT CallName, InTag, OutTag FROM dspCall WHERE InTag IS NOT NULL");

        foreach (var call in calls)
        {
            if (!string.IsNullOrEmpty(call.InTag))
                mapper.AddInTag(call.InTag, call.CallName);
            if (!string.IsNullOrEmpty(call.OutTag))
                mapper.AddOutTag(call.OutTag, call.CallName);
        }

        System.Console.WriteLine("  Simulating PLC events...\n");

        // Scenario: Work1 execution
        // 1. D100 (InTag) Rising → Work1: Ready → Going
        System.Console.WriteLine("  [1] D100 (InTag) = 1 (Rising Edge)");
        var inTagEvent = new PlcTagEvent
        {
            TagName = "D100",
            Value = true,
            Timestamp = DateTime.UtcNow,
            Source = "Test"
        };

        tracker.DetectEdge(new PlcTagEvent { TagName = "D100", Value = false, Timestamp = DateTime.UtcNow, Source = "Test" });
        var inEdge = tracker.DetectEdge(inTagEvent);

        if (inEdge.HasValue)
        {
            await StateTransition.processEdgeEvent(_testDbPath, mapper, inEdge.Value);
        }

        await Task.Delay(100);

        // Verify Work1 state
        var work1State = await conn.QueryFirstAsync<string>(
            "SELECT State FROM dspCall WHERE CallName = @CallName",
            new { CallName = "Work1" });

        System.Console.WriteLine($"      → Work1 State: {work1State}");

        if (work1State == "Going")
        {
            System.Console.WriteLine("      ✓ State transition: Ready → Going");
        }
        else
        {
            throw new Exception($"Expected 'Going', got '{work1State}'");
        }

        // Verify Flow ActiveCallCount
        var flowActive = await conn.QueryFirstAsync<int>(
            "SELECT ActiveCallCount FROM dspFlow WHERE FlowName = @FlowName",
            new { FlowName = "Flow1" });

        System.Console.WriteLine($"      → Flow1 ActiveCallCount: {flowActive}");

        if (flowActive == 1)
        {
            System.Console.WriteLine("      ✓ Flow ActiveCallCount increased\n");
        }

        // 2. D101 (OutTag) Rising → Work1: Going → Done
        await Task.Delay(2000); // Simulate 2 seconds of work

        System.Console.WriteLine("  [2] D101 (OutTag) = 1 (Rising Edge) [2s later]");
        var outTagEvent = new PlcTagEvent
        {
            TagName = "D101",
            Value = true,
            Timestamp = DateTime.UtcNow,
            Source = "Test"
        };

        tracker.DetectEdge(new PlcTagEvent { TagName = "D101", Value = false, Timestamp = DateTime.UtcNow, Source = "Test" });
        var outEdge = tracker.DetectEdge(outTagEvent);

        if (outEdge.HasValue)
        {
            await StateTransition.processEdgeEvent(_testDbPath, mapper, outEdge.Value);
        }

        await Task.Delay(100);

        // Verify Work1 state
        var work1FinalState = await conn.QueryFirstAsync<string>(
            "SELECT State FROM dspCall WHERE CallName = @CallName",
            new { CallName = "Work1" });

        System.Console.WriteLine($"      → Work1 State: {work1FinalState}");

        // Verify duration
        var work1Duration = await conn.QueryFirstAsync<double?>(
            "SELECT LastDurationMs FROM dspCall WHERE CallName = @CallName",
            new { CallName = "Work1" });

        System.Console.WriteLine($"      → Work1 Duration: {work1Duration:F0} ms");

        if (work1FinalState == "Done" && work1Duration > 1900 && work1Duration < 2100)
        {
            System.Console.WriteLine("      ✓ State transition: Going → Done");
            System.Console.WriteLine("      ✓ Duration calculated correctly\n");
        }
        else
        {
            throw new Exception($"Final state check failed: State={work1FinalState}, Duration={work1Duration}");
        }

        // Verify Flow ActiveCallCount decreased
        var flowFinalActive = await conn.QueryFirstAsync<int>(
            "SELECT ActiveCallCount FROM dspFlow WHERE FlowName = @FlowName",
            new { FlowName = "Flow1" });

        System.Console.WriteLine($"      → Flow1 ActiveCallCount: {flowFinalActive}");

        if (flowFinalActive == 0)
        {
            System.Console.WriteLine("      ✓ Flow ActiveCallCount decreased");
        }

        // Display final state
        System.Console.WriteLine("\n  Final Database State:");
        var finalCalls = await conn.QueryAsync<dynamic>(
            "SELECT CallName, State, LastDurationMs FROM dspCall");

        foreach (var call in finalCalls)
        {
            System.Console.WriteLine($"    - {call.CallName}: {call.State} (Duration: {call.LastDurationMs ?? 0:F0} ms)");
        }
    }
}
