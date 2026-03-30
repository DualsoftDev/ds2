using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Dapper;
using DSPilot.Engine;

namespace DSPilot.Engine.Tests.Console;

class Program
{
    static async Task Main(string[] args)
    {
        System.Console.WriteLine("========================================");
        System.Console.WriteLine("DSPilot.Engine Step-by-Step Test Console");
        System.Console.WriteLine("========================================\n");

        string step;

        // If no arguments provided, show interactive menu
        if (args.Length == 0)
        {
            step = ShowInteractiveMenu();
        }
        else
        {
            step = args[0];
        }

        switch (step)
        {
            case "0":
                await RunStep0();
                break;
            case "2":
                await RunStep2();
                break;
            case "3":
            case "realplc":
                await RunRealPlcConnection();
                break;
            case "4":
            case "real":
            case "plc":
                await RunRealPlcTest();
                break;
            case "5":
            case "verify":
                await RunDbVerification();
                break;
            case "6":
            case "inspect":
            case "plcdb":
                await RunPlcDbInspection();
                break;
            case "realdata":
                await RunRealPlcDataTest();
                break;
            case "quit":
            case "q":
                System.Console.WriteLine("Exiting...");
                break;
            default:
                System.Console.WriteLine("Invalid option. Exiting...");
                break;
        }
    }

    static string ShowInteractiveMenu()
    {
        System.Console.WriteLine("=========================================================");
        System.Console.WriteLine("           DSPilot.Engine Test Menu");
        System.Console.WriteLine("=========================================================");
        System.Console.WriteLine("  [0] Step 0: Basic Database CRUD Test");
        System.Console.WriteLine("  [2] Step 2: Integration Test");
        System.Console.WriteLine($"  [3] Real PLC Connection Test (Mitsubishi {PlcDefaults.IpAddress})");
        System.Console.WriteLine("  [4] Real PLC Integration with AASX");
        System.Console.WriteLine("  [5] Database Verification");
        System.Console.WriteLine("  [6] Inspect PLC Database");
        System.Console.WriteLine("  [q] Quit");
        System.Console.WriteLine("=========================================================");
        System.Console.WriteLine();
        System.Console.Write("Select test mode: ");

        var input = System.Console.ReadLine()?.Trim().ToLower() ?? "q";
        System.Console.WriteLine();

        return input;
    }

    static async Task RunStep0()
    {
        System.Console.WriteLine("====================================");
        System.Console.WriteLine("DSPilot.Engine Step 0 Test");
        System.Console.WriteLine("====================================\n");

        // Test database path
        var testDbPath = Path.Combine(Path.GetTempPath(), "dspilot_test_step0.db");

        // Clean up existing test DB
        if (File.Exists(testDbPath))
        {
            File.Delete(testDbPath);
            System.Console.WriteLine($"✓ Cleaned up existing test DB: {testDbPath}");
        }

        try
        {
            // ===== Step 0.1: Database Initialization =====
            System.Console.WriteLine("\n[Step 0.1] Database Initialization");
            System.Console.WriteLine("-----------------------------------");

            await InitializeDatabase(testDbPath);
            System.Console.WriteLine($"✓ Database created: {testDbPath}");

            // ===== Step 0.2: Basic CRUD Test =====
            System.Console.WriteLine("\n[Step 0.2] Repository CRUD Test");
            System.Console.WriteLine("-----------------------------------");

            await TestFlowCrud(testDbPath);
            await TestCallCrud(testDbPath);

            // ===== Step 0.3: Query Test =====
            System.Console.WriteLine("\n[Step 0.3] Repository Query Test");
            System.Console.WriteLine("-----------------------------------");

            await TestQueries(testDbPath);

            System.Console.WriteLine("\n====================================");
            System.Console.WriteLine("[OK] Step 0 Test Complete!");
            System.Console.WriteLine("====================================");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"\n[ERROR] Test Failed: {ex.Message}");
            System.Console.WriteLine(ex.StackTrace);
            Environment.Exit(1);
        }
    }

    static async Task RunStep2()
    {
        System.Console.WriteLine("Step 2 test has been replaced with Real PLC test.");
        System.Console.WriteLine("Please run: dotnet run real");
        await Task.CompletedTask;
    }

    static async Task RunRealPlcTest()
    {
        try
        {
            var test = new RealPlcIntegrationTest();
            await test.Run();
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"\n[ERROR] Real PLC Test Failed: {ex.Message}");
            System.Console.WriteLine(ex.StackTrace);
            Environment.Exit(1);
        }
    }

    static async Task RunDbVerification()
    {
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DSPilot", "real_plc_test.db");

        await DbVerifier.VerifyDatabase(dbPath);
    }

    static async Task RunPlcDbInspection()
    {
        var plcDbPath = @"C:\Users\dual\AppData\Roaming\Dualsoft\DSPilot\plc.db";
        await PlcDbInspector.InspectDatabase(plcDbPath);
    }

    static async Task RunRealPlcDataTest()
    {
        System.Console.WriteLine("========================================");
        System.Console.WriteLine("Real PLC Data Integration Test");
        System.Console.WriteLine("========================================\n");

        var plcDbPath = @"C:\Users\dual\AppData\Roaming\Dualsoft\DSPilot\plc.db";
        var testDbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DSPilot", "real_plc_data_test.db");

        try
        {
            // Step 1: Inspect PLC database
            await PlcDbInspector.InspectDatabase(plcDbPath);

            // Step 2: Setup test database
            System.Console.WriteLine("[Step 1] Test Database Setup");
            System.Console.WriteLine("------------------------");

            var dbDir = Path.GetDirectoryName(testDbPath);
            if (!Directory.Exists(dbDir))
            {
                Directory.CreateDirectory(dbDir!);
            }

            if (File.Exists(testDbPath))
            {
                File.Delete(testDbPath);
                System.Console.WriteLine($"  ✓ Cleaned up existing test database");
            }

            await InitializeDatabase(testDbPath);
            System.Console.WriteLine($"  ✓ Test database ready: {testDbPath}\n");

            // Step 3: Load AASX and create Calls
            System.Console.WriteLine("[Step 2] Create Test Calls");
            System.Console.WriteLine("------------------------");

            using (var conn = new SqliteConnection($"Data Source={testDbPath}"))
            {
                await conn.OpenAsync();
                var now = DateTime.UtcNow.ToString("o");

                await conn.ExecuteAsync("""
                    INSERT INTO dspFlow (FlowName, State, ActiveCallCount, CreatedAt, UpdatedAt)
                    VALUES (@FlowName, @State, @ActiveCallCount, @CreatedAt, @UpdatedAt)
                    """, new[]
                {
                    new { FlowName = "RealFlow", State = "Ready", ActiveCallCount = 0, CreatedAt = now, UpdatedAt = now }
                });

                await conn.ExecuteAsync("""
                    INSERT INTO dspCall (CallName, FlowName, State, InTag, OutTag, CreatedAt, UpdatedAt)
                    VALUES (@CallName, @FlowName, @State, @InTag, @OutTag, @CreatedAt, @UpdatedAt)
                    """, new[]
                {
                    new { CallName = "RealWork1", FlowName = "RealFlow", State = "Ready", InTag = "D100", OutTag = "D101", CreatedAt = now, UpdatedAt = now },
                    new { CallName = "RealWork2", FlowName = "RealFlow", State = "Ready", InTag = "D102", OutTag = "D103", CreatedAt = now, UpdatedAt = now },
                    new { CallName = "RealWork3", FlowName = "RealFlow", State = "Ready", InTag = "D104", OutTag = "D105", CreatedAt = now, UpdatedAt = now }
                });

                System.Console.WriteLine("  ✓ Created 1 Flow and 3 Calls\n");
            }

            // Step 4: Process real PLC data
            System.Console.WriteLine("[Step 3] Process Real PLC Data");
            System.Console.WriteLine("------------------------");

            var tags = new[] { "D100", "D101", "D102", "D103", "D104", "D105" };
            using var plcConnector = new RealPlcConnector(PlcDefaults.IpAddress, PlcDefaults.Port, tags);

            int eventCount = 0;
            plcConnector.TagChanged += async (sender, e) =>
            {
                eventCount++;
                System.Console.WriteLine($"\n  🔔 Event #{eventCount}: {e.TagName} = {e.Value} ({e.EdgeType})");

                // Process the event through RealPlcIntegrationTest's ProcessTagEvent logic
                using var conn = new SqliteConnection($"Data Source={testDbPath}");
                await conn.OpenAsync();

                var callInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT CallName, FlowName FROM dspCall WHERE InTag = @Tag OR OutTag = @Tag",
                    new { Tag = e.TagName });

                if (callInfo != null)
                {
                    string callName = callInfo.CallName;
                    string flowName = callInfo.FlowName;

                    var isInTag = await conn.ExecuteScalarAsync<bool>(
                        "SELECT COUNT(*) > 0 FROM dspCall WHERE CallName = @CallName AND InTag = @Tag",
                        new { CallName = callName, Tag = e.TagName });

                    if (isInTag && e.EdgeType == EdgeType.Rising)
                    {
                        System.Console.WriteLine($"    📍 {callName}: Ready → Going");
                        await conn.ExecuteAsync(
                            "UPDATE dspCall SET State = 'Going', LastStartAt = @StartAt, UpdatedAt = @UpdatedAt WHERE CallName = @CallName",
                            new { StartAt = e.Timestamp.ToString("o"), UpdatedAt = DateTime.UtcNow.ToString("o"), CallName = callName });

                        await conn.ExecuteAsync(
                            "UPDATE dspFlow SET ActiveCallCount = ActiveCallCount + 1, State = 'Going', UpdatedAt = @UpdatedAt WHERE FlowName = @FlowName",
                            new { UpdatedAt = DateTime.UtcNow.ToString("o"), FlowName = flowName });
                    }
                    else if (!isInTag && e.EdgeType == EdgeType.Rising)
                    {
                        var startAt = await conn.ExecuteScalarAsync<string>(
                            "SELECT LastStartAt FROM dspCall WHERE CallName = @CallName",
                            new { CallName = callName });

                        double? durationMs = null;
                        if (!string.IsNullOrEmpty(startAt))
                        {
                            var startTime = DateTime.Parse(startAt);
                            durationMs = (e.Timestamp - startTime).TotalMilliseconds;
                        }

                        System.Console.WriteLine($"    [OK] {callName}: Going → Done (Duration: {durationMs:F0}ms)");

                        await conn.ExecuteAsync(
                            "UPDATE dspCall SET State = 'Done', LastFinishAt = @FinishAt, LastDurationMs = @DurationMs, UpdatedAt = @UpdatedAt WHERE CallName = @CallName",
                            new { FinishAt = e.Timestamp.ToString("o"), DurationMs = durationMs, UpdatedAt = DateTime.UtcNow.ToString("o"), CallName = callName });

                        await conn.ExecuteAsync(
                            "UPDATE dspFlow SET ActiveCallCount = MAX(0, ActiveCallCount - 1), UpdatedAt = @UpdatedAt WHERE FlowName = @FlowName",
                            new { UpdatedAt = DateTime.UtcNow.ToString("o"), FlowName = flowName });

                        var activeCount = await conn.ExecuteScalarAsync<int>(
                            "SELECT ActiveCallCount FROM dspFlow WHERE FlowName = @FlowName",
                            new { FlowName = flowName });

                        if (activeCount == 0)
                        {
                            await conn.ExecuteAsync(
                                "UPDATE dspFlow SET State = 'Ready', UpdatedAt = @UpdatedAt WHERE FlowName = @FlowName",
                                new { UpdatedAt = DateTime.UtcNow.ToString("o"), FlowName = flowName });
                        }
                    }
                }
            };

            if (await plcConnector.TryConnectAsync())
            {
                using var cts = new CancellationTokenSource();
                await plcConnector.MonitorHistoricalDataAsync(plcDbPath, cts.Token);
            }

            System.Console.WriteLine($"\n  ✓ Processed {eventCount} PLC events\n");

            // Step 5: Verify results
            System.Console.WriteLine("[Step 4] Verify Results");
            System.Console.WriteLine("------------------------");
            await DbVerifier.VerifyDatabase(testDbPath);

            // Display statistics
            StatsVerifier.DisplayStatistics();

            System.Console.WriteLine("\n========================================");
            System.Console.WriteLine("[OK] Real PLC Data Test Complete!");
            System.Console.WriteLine("========================================\n");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"\n[ERROR] Real PLC Data Test Failed: {ex.Message}");
            System.Console.WriteLine(ex.StackTrace);
            Environment.Exit(1);
        }
    }

    static async Task InitializeDatabase(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        // Create dspFlow table (Step 0 minimal schema)
        var createFlowTable = """
            CREATE TABLE IF NOT EXISTS dspFlow (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FlowName TEXT NOT NULL UNIQUE,
                State TEXT DEFAULT 'Ready',
                ActiveCallCount INTEGER DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            )
            """;

        await connection.ExecuteAsync(createFlowTable);
        System.Console.WriteLine("  ✓ dspFlow table created");

        // Create dspCall table (Step 0 minimal schema)
        var createCallTable = """
            CREATE TABLE IF NOT EXISTS dspCall (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CallName TEXT NOT NULL UNIQUE,
                FlowName TEXT NOT NULL,
                State TEXT DEFAULT 'Ready',
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                FOREIGN KEY (FlowName) REFERENCES dspFlow(FlowName)
            )
            """;

        await connection.ExecuteAsync(createCallTable);
        System.Console.WriteLine("  ✓ dspCall table created");

        // Create indexes
        await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_call_flow ON dspCall(FlowName)");
        System.Console.WriteLine("  ✓ Indexes created");
    }

    static async Task TestFlowCrud(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        // Insert Flow
        var insertSql = """
            INSERT INTO dspFlow (FlowName, State, ActiveCallCount, CreatedAt, UpdatedAt)
            VALUES (@FlowName, @State, @ActiveCallCount, @CreatedAt, @UpdatedAt)
            """;

        var now = DateTime.UtcNow.ToString("o");
        await connection.ExecuteAsync(insertSql, new
        {
            FlowName = "Flow1",
            State = "Ready",
            ActiveCallCount = 0,
            CreatedAt = now,
            UpdatedAt = now
        });

        System.Console.WriteLine("  ✓ Inserted Flow1");

        await connection.ExecuteAsync(insertSql, new
        {
            FlowName = "Flow2",
            State = "Ready",
            ActiveCallCount = 0,
            CreatedAt = now,
            UpdatedAt = now
        });

        System.Console.WriteLine("  ✓ Inserted Flow2");

        // Get all Flows
        var flows = await connection.QueryAsync<dynamic>("SELECT * FROM dspFlow");
        System.Console.WriteLine($"  ✓ Retrieved {flows.Count()} flows");

        foreach (var flow in flows)
        {
            System.Console.WriteLine($"    - {flow.FlowName}: {flow.State} (Active: {flow.ActiveCallCount})");
        }
    }

    static async Task TestCallCrud(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        // Insert Calls
        var insertSql = """
            INSERT INTO dspCall (CallName, FlowName, State, CreatedAt, UpdatedAt)
            VALUES (@CallName, @FlowName, @State, @CreatedAt, @UpdatedAt)
            """;

        var now = DateTime.UtcNow.ToString("o");

        await connection.ExecuteAsync(insertSql, new
        {
            CallName = "Work1",
            FlowName = "Flow1",
            State = "Ready",
            CreatedAt = now,
            UpdatedAt = now
        });

        System.Console.WriteLine("  ✓ Inserted Work1 (Flow1)");

        await connection.ExecuteAsync(insertSql, new
        {
            CallName = "Work2",
            FlowName = "Flow1",
            State = "Ready",
            CreatedAt = now,
            UpdatedAt = now
        });

        System.Console.WriteLine("  ✓ Inserted Work2 (Flow1)");

        await connection.ExecuteAsync(insertSql, new
        {
            CallName = "Work3",
            FlowName = "Flow2",
            State = "Ready",
            CreatedAt = now,
            UpdatedAt = now
        });

        System.Console.WriteLine("  ✓ Inserted Work3 (Flow2)");

        // Get all Calls
        var calls = await connection.QueryAsync<dynamic>("SELECT * FROM dspCall");
        System.Console.WriteLine($"  ✓ Retrieved {calls.Count()} calls");

        foreach (var call in calls)
        {
            System.Console.WriteLine($"    - {call.CallName} ({call.FlowName}): {call.State}");
        }
    }

    static async Task TestQueries(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        // Get Calls by Flow
        var flow1Calls = await connection.QueryAsync<dynamic>(
            "SELECT * FROM dspCall WHERE FlowName = @FlowName",
            new { FlowName = "Flow1" });

        System.Console.WriteLine($"  ✓ Flow1 has {flow1Calls.Count()} calls:");
        foreach (var call in flow1Calls)
        {
            System.Console.WriteLine($"    - {call.CallName}: {call.State}");
        }

        var flow2Calls = await connection.QueryAsync<dynamic>(
            "SELECT * FROM dspCall WHERE FlowName = @FlowName",
            new { FlowName = "Flow2" });

        System.Console.WriteLine($"  ✓ Flow2 has {flow2Calls.Count()} calls:");
        foreach (var call in flow2Calls)
        {
            System.Console.WriteLine($"    - {call.CallName}: {call.State}");
        }

        // Update Call State
        await connection.ExecuteAsync(
            "UPDATE dspCall SET State = @State, UpdatedAt = @UpdatedAt WHERE CallName = @CallName",
            new { State = "Going", UpdatedAt = DateTime.UtcNow.ToString("o"), CallName = "Work1" });

        System.Console.WriteLine("  ✓ Updated Work1 state to 'Going'");

        // Verify Update
        var work1 = await connection.QueryFirstAsync<dynamic>(
            "SELECT * FROM dspCall WHERE CallName = @CallName",
            new { CallName = "Work1" });

        System.Console.WriteLine($"  ✓ Work1 state verified: {work1.State}");
    }

    static async Task RunRealPlcConnection()
    {
        var defaultAasxPath = @"C:\ds\ds2\Apps\DSPilot\DsCSV_0318_C.aasx";
        var defaultDbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DSPilot", "real_plc_connection.db");

        System.Console.Write($"AASX path (default: {defaultAasxPath}): ");
        var aasxPath = System.Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(aasxPath))
            aasxPath = defaultAasxPath;

        System.Console.Write($"DB path (default: {defaultDbPath}): ");
        var dbPath = System.Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(dbPath))
            dbPath = defaultDbPath;

        // Ensure directory exists
        var dbDir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
        {
            Directory.CreateDirectory(dbDir);
        }

        var test = new RealPlcTest(aasxPath, dbPath);
        await test.RunAsync();
    }
}
