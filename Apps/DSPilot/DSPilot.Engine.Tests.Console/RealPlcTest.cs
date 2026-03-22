using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Ds2.Core;
using Ds2.UI.Core;
using Ev2.Backend.Common;
using Ev2.Backend.PLC;
using Ev2.PLC.Protocol.MX;
using Microsoft.FSharp.Core;
using PlcDataType = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType;
using PlcValue = Ev2.PLC.Common.CoreDataTypesModule.PlcValue;
using TagSpec = Ev2.PLC.Common.TagSpecModule.TagSpec;

namespace DSPilot.Engine.Tests.Console;

/// <summary>
/// Real PLC Connection Test
/// Connects to Mitsubishi PLC at 192.168.9.120:4444 (TCP)
/// Monitors ApiCall InTag/OutTag addresses and processes state transitions
/// </summary>
public class RealPlcTest
{
    private readonly string _aasxPath;
    private readonly string _dbPath;
    private DsStore _store;
    private Dictionary<Guid, CallState> _callStates = new();
    private Dictionary<string, bool> _previousTagValues = new();
    private Dictionary<string, CallTagMapping> _tagMappings = new();

    private class CallState
    {
        public required string FlowName { get; init; }
        public required string CallName { get; init; }
        public required Guid CallId { get; init; }
        public required string State { get; set; }
        public required CallDirection Direction { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? FinishTime { get; set; }
        public double? DurationMs { get; set; }
        public int CycleCount { get; set; }
    }

    private class CallTagMapping
    {
        public required Guid CallId { get; init; }
        public required string FlowName { get; init; }
        public required string CallName { get; init; }
        public required string TagAddress { get; init; }
        public required bool IsInTag { get; init; } // true = InTag (응답/결과), false = OutTag (활성화)
    }

    private enum CallDirection
    {
        InOut,   // In + Out 모두 존재
        InOnly,  // In만 존재
        OutOnly, // Out만 존재
        None     // 태그 없음 (에러)
    }

    public RealPlcTest(string aasxPath, string dbPath)
    {
        _aasxPath = aasxPath;
        _dbPath = dbPath;
        _store = new DsStore(); // Initialize to avoid null warning
    }

    public async Task RunAsync()
    {
        System.Console.WriteLine("============================================");
        System.Console.WriteLine("     Real PLC Connection Test              ");
        System.Console.WriteLine("============================================");
        System.Console.WriteLine();

        // 1. Load AASX
        System.Console.WriteLine($"[1]  Loading AASX: {_aasxPath}");
        if (!File.Exists(_aasxPath))
        {
            System.Console.WriteLine($"   [ERROR] AASX file not found");
            return;
        }

        _store = new DsStore();
        bool loaded = Ds2.Aasx.AasxImporter.importIntoStore(_store, _aasxPath);
        if (!loaded)
        {
            System.Console.WriteLine("   [ERROR] Failed to load AASX");
            return;
        }
        System.Console.WriteLine("   [OK] AASX loaded successfully");
        System.Console.WriteLine();

        // 2. Initialize database
        System.Console.WriteLine($"[2]  Initializing database: {_dbPath}");
        await InitializeDatabaseAsync();
        System.Console.WriteLine("   [OK] Database initialized");
        System.Console.WriteLine();

        // 3. Build tag mappings from AASX
        System.Console.WriteLine("[3]  Building tag mappings from AASX...");
        BuildTagMappings();
        System.Console.WriteLine($"   [OK] {_tagMappings.Count} tag mappings created");

        // 3a. Update Direction in database
        await UpdateDirectionsInDatabaseAsync();

        // Show diagnostic info for calls with 0 cycle count
        var callsWithoutTags = _callStates.Values.Where(c =>
        {
            var hasInTag = _tagMappings.Values.Any(m => m.CallId == c.CallId && m.IsInTag);
            var hasOutTag = _tagMappings.Values.Any(m => m.CallId == c.CallId && !m.IsInTag);
            return !hasInTag || !hasOutTag;
        }).ToList();

        if (callsWithoutTags.Any())
        {
            System.Console.WriteLine();
            System.Console.WriteLine("   [WARNING] Calls with incomplete tag mappings:");
            foreach (var call in callsWithoutTags)
            {
                var hasInTag = _tagMappings.Values.Any(m => m.CallId == call.CallId && m.IsInTag);
                var hasOutTag = _tagMappings.Values.Any(m => m.CallId == call.CallId && !m.IsInTag);
                var inTagAddr = _tagMappings.Values.FirstOrDefault(m => m.CallId == call.CallId && m.IsInTag)?.TagAddress ?? "NONE";
                var outTagAddr = _tagMappings.Values.FirstOrDefault(m => m.CallId == call.CallId && !m.IsInTag)?.TagAddress ?? "NONE";

                System.Console.WriteLine($"     - {call.FlowName}/{call.CallName}:");
                System.Console.WriteLine($"       InTag:  {inTagAddr} {(hasInTag ? "[OK]" : "[MISSING]")}");
                System.Console.WriteLine($"       OutTag: {outTagAddr} {(hasOutTag ? "[OK]" : "[MISSING]")}");
            }
        }
        System.Console.WriteLine();

        // 4. Create TagSpecs for all tags
        System.Console.WriteLine("[4]  Creating TagSpecs...");
        var tagSpecs = _tagMappings.Values
            .Select(m => new TagSpec(
                name: m.TagAddress,
                address: m.TagAddress,
                dataType: PlcDataType.Bool,
                walType: FSharpOption<Ev2.PLC.Common.TagSpecModule.WAL>.None,
                comment: FSharpOption<string>.None,
                plcValue: FSharpOption<PlcValue>.None
            ))
            .DistinctBy(t => t.Address)
            .ToArray();
        System.Console.WriteLine($"   [OK] {tagSpecs.Length} unique tag specs created");
        System.Console.WriteLine();

        // 5. Configure Mitsubishi PLC connection
        System.Console.WriteLine("[5]  Configuring Mitsubishi PLC connection...");
        var connectionConfig = new MxConnectionConfig
        {
            IpAddress = "192.168.9.120",
            Port = 4444,
            Name = "MitsubishiPLC",
            EnableScan = true,
            Timeout = TimeSpan.FromSeconds(5),
            ScanInterval = TimeSpan.FromMilliseconds(100), // 100ms PLC scan interval
            FrameType = FrameType.QnA_3E_Binary,
            Protocol = TransportProtocol.TCP,
            AccessRoute = new AccessRoute(0, 255, 1023, 0),
            MonitoringTimer = 16
        };

        var scanConfigs = new[] { new ScanConfiguration(connectionConfig, tagSpecs) };
        System.Console.WriteLine("   [OK] PLC config ready");
        System.Console.WriteLine();

        // 6. Start PLC service
        System.Console.WriteLine("[6]  Starting PLC service...");
        IDisposable? disposable = null;

        try
        {
            var plcService = new PLCBackendService(
                scanConfigs: scanConfigs,
                tagHistoricWAL: FSharpOption<TagHistoricWAL>.None
            );

            disposable = plcService.Start();
            var connectionName = plcService.AllConnectionNames.FirstOrDefault();
            System.Console.WriteLine($"   [OK] Connected: {connectionName}");
            System.Console.WriteLine();

            await Task.Delay(2000); // Wait for PLC scan loop to start

            // 7. Subscribe to PLC events
            System.Console.WriteLine("[7]  Subscribing to PLC tag change events...");
            System.Console.WriteLine("   Event-driven monitoring (no polling)");
            System.Console.WriteLine();
            await Task.Delay(1000);

            // Clear console and start table UI (skip if not supported)
            try { System.Console.Clear(); } catch { /* Ignore if running in non-interactive mode */ }

            using var cts = new CancellationTokenSource();

            // Keyboard listener task
            var keyTask = Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    if (System.Console.KeyAvailable)
                    {
                        var key = System.Console.ReadKey(true);
                        if (key.Key == ConsoleKey.Q)
                        {
                            cts.Cancel();
                            break;
                        }
                    }
                    Thread.Sleep(100);
                }
            }, cts.Token);

            // Subscribe to PLC tag change events (event-driven, no polling)
            await MonitorPlcEventsAsync(plcService, connectionName!, cts.Token);

            System.Console.WriteLine();
            System.Console.WriteLine("[OK] Monitoring stopped");
        }
        catch (OperationCanceledException)
        {
            System.Console.WriteLine("[OK] Monitoring stopped");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[ERROR] Error: {ex.Message}");
            System.Console.WriteLine($"   Stack: {ex.StackTrace}");
        }
        finally
        {
            disposable?.Dispose();
            System.Console.WriteLine("[STOP] PLC service stopped");
        }
    }

    private async Task InitializeDatabaseAsync()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();

        // Initialize schema
        var initSql = @"
CREATE TABLE IF NOT EXISTS dspFlow (
    Id TEXT PRIMARY KEY,
    FlowName TEXT NOT NULL,
    State TEXT NOT NULL DEFAULT 'Ready',
    ActiveCallCount INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS dspCall (
    Id TEXT PRIMARY KEY,
    CallName TEXT NOT NULL,
    FlowId TEXT NOT NULL,
    FlowName TEXT NOT NULL,
    State TEXT NOT NULL DEFAULT 'Ready',
    LastStartAt TEXT,
    LastFinishAt TEXT,
    LastDurationMs REAL,
    FOREIGN KEY(FlowId) REFERENCES dspFlow(Id)
);
";
        await Dapper.SqlMapper.ExecuteAsync(conn, initSql);

        // Add new columns if they don't exist (for existing databases)
        // Check if Direction column exists
        var directionExists = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM pragma_table_info('dspCall') WHERE name='Direction'");
        if (directionExists == 0)
        {
            await Dapper.SqlMapper.ExecuteAsync(conn, "ALTER TABLE dspCall ADD COLUMN Direction TEXT NOT NULL DEFAULT 'None'");
        }

        // Check if CycleCount column exists
        var cycleCountExists = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM pragma_table_info('dspCall') WHERE name='CycleCount'");
        if (cycleCountExists == 0)
        {
            await Dapper.SqlMapper.ExecuteAsync(conn, "ALTER TABLE dspCall ADD COLUMN CycleCount INTEGER NOT NULL DEFAULT 0");
        }

        // Insert Flows and Calls from AASX
        var allFlows = DsQuery.allFlows(_store).ToList();

        foreach (var flow in allFlows)
        {
            await Dapper.SqlMapper.ExecuteAsync(conn,
                "INSERT OR REPLACE INTO dspFlow (Id, FlowName, State, ActiveCallCount) VALUES (@Id, @FlowName, 'Ready', 0)",
                new { Id = flow.Id.ToString(), FlowName = flow.Name });

            var works = DsQuery.worksOf(flow.Id, _store).ToList();
            foreach (var work in works)
            {
                var calls = DsQuery.callsOf(work.Id, _store).ToList();
                foreach (var call in calls)
                {
                    await Dapper.SqlMapper.ExecuteAsync(conn,
                        @"INSERT OR REPLACE INTO dspCall (Id, CallName, FlowId, FlowName, State, Direction, CycleCount)
                          VALUES (@Id, @CallName, @FlowId, @FlowName, 'Ready', 'None', 0)",
                        new
                        {
                            Id = call.Id.ToString(),
                            CallName = call.Name,
                            FlowId = flow.Id.ToString(),
                            FlowName = flow.Name
                        });

                    // Initialize in-memory state (Direction will be set after tag mapping)
                    _callStates[call.Id] = new CallState
                    {
                        FlowName = flow.Name,
                        CallName = call.Name,
                        CallId = call.Id,
                        State = "Ready",
                        Direction = CallDirection.None // Will be updated in BuildTagMappings
                    };
                }
            }
        }

        System.Console.WriteLine($"   Initialized {allFlows.Count} flows, {_callStates.Count} calls");
    }

    private void BuildTagMappings()
    {
        var allFlows = DsQuery.allFlows(_store).ToList();

        foreach (var flow in allFlows)
        {
            var works = DsQuery.worksOf(flow.Id, _store).ToList();

            foreach (var work in works)
            {
                var calls = DsQuery.callsOf(work.Id, _store).ToList();

                foreach (var call in calls)
                {
                    if (call.ApiCalls.Count == 0)
                        continue;

                    var apiCall = call.ApiCalls[0];

                    // OutTag (Rising edge triggers Ready → Going)
                    if (FSharpOption<IOTag>.get_IsSome(apiCall.OutTag))
                    {
                        var outAddress = apiCall.OutTag.Value.Address;
                        _tagMappings[outAddress] = new CallTagMapping
                        {
                            CallId = call.Id,
                            FlowName = flow.Name,
                            CallName = call.Name,
                            TagAddress = outAddress,
                            IsInTag = false // OutTag
                        };
                    }

                    // InTag (Rising edge triggers Going → Done)
                    if (FSharpOption<IOTag>.get_IsSome(apiCall.InTag))
                    {
                        var inAddress = apiCall.InTag.Value.Address;
                        _tagMappings[inAddress] = new CallTagMapping
                        {
                            CallId = call.Id,
                            FlowName = flow.Name,
                            CallName = call.Name,
                            TagAddress = inAddress,
                            IsInTag = true // InTag
                        };
                    }
                }
            }
        }

        // Update Direction for each call based on tag mappings
        foreach (var callState in _callStates.Values)
        {
            var hasInTag = _tagMappings.Values.Any(m => m.CallId == callState.CallId && m.IsInTag);
            var hasOutTag = _tagMappings.Values.Any(m => m.CallId == callState.CallId && !m.IsInTag);

            callState.Direction = (hasInTag, hasOutTag) switch
            {
                (true, true) => CallDirection.InOut,
                (true, false) => CallDirection.InOnly,
                (false, true) => CallDirection.OutOnly,
                (false, false) => CallDirection.None
            };
        }
    }

    private async Task UpdateDirectionsInDatabaseAsync()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();

        foreach (var callState in _callStates.Values)
        {
            var directionStr = callState.Direction switch
            {
                CallDirection.InOut => "InOut",
                CallDirection.InOnly => "InOnly",
                CallDirection.OutOnly => "OutOnly",
                _ => "None"
            };

            await Dapper.SqlMapper.ExecuteAsync(conn,
                "UPDATE dspCall SET Direction = @Direction WHERE Id = @Id",
                new { Id = callState.CallId.ToString(), Direction = directionStr });
        }

        System.Console.WriteLine($"   Updated Direction for {_callStates.Count} calls in database");
    }

    private async Task MonitorPlcEventsAsync(PLCBackendService plcService, string connectionName, CancellationToken cancellationToken)
    {
        var table = new ConsoleTable();

        // Initial render
        foreach (var state in _callStates.Values)
        {
            table.UpdateRow(new ConsoleTable.Row
            {
                FlowName = state.FlowName,
                CallName = state.CallName,
                State = state.State,
                LastStartAt = state.StartTime?.ToString("o"),
                LastFinishAt = state.FinishTime?.ToString("o"),
                LastDurationMs = state.DurationMs,
                CycleCount = state.CycleCount
            });
        }
        table.Render();

        var lastTableUpdate = DateTime.Now;

        // Read tags from PLC cache (PLC service scans at 100ms, we just read the cached values fast)
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                bool hasChange = false;

                // Read all tags from PLC service cache (very fast, no network delay)
                foreach (var tagAddress in _tagMappings.Keys.Distinct())
                {
                    var result = plcService.RTryReadTagValue(connectionName, tagAddress);

                    if (result.IsOk)
                    {
                        var plcValue = result.ResultValue;
                        var currentValue = ConvertPlcValueToBool(plcValue);
                        var previousValue = _previousTagValues.GetValueOrDefault(tagAddress, false);

                        // Detect rising edge (0 → 1)
                        if (!previousValue && currentValue)
                        {
                            if (_tagMappings.TryGetValue(tagAddress, out var mapping))
                            {
                                System.Console.SetCursorPosition(0, System.Console.CursorTop);
                                System.Console.Write($"[EDGE] {tagAddress} ({mapping.CallName}) -> {(mapping.IsInTag ? "In" : "Out")} Rising");
                                System.Console.Write(new string(' ', 20));
                            }
                            await HandleRisingEdgeAsync(tagAddress);
                            hasChange = true;
                        }
                        // Detect falling edge (1 → 0)
                        else if (previousValue && !currentValue)
                        {
                            if (_tagMappings.TryGetValue(tagAddress, out var mapping))
                            {
                                System.Console.SetCursorPosition(0, System.Console.CursorTop);
                                System.Console.Write($"[EDGE] {tagAddress} ({mapping.CallName}) -> {(mapping.IsInTag ? "In" : "Out")} Falling");
                                System.Console.Write(new string(' ', 20));
                            }
                            await HandleFallingEdgeAsync(tagAddress);
                            hasChange = true;
                        }

                        _previousTagValues[tagAddress] = currentValue;
                    }
                }

                // Update table if there was a change or every 500ms
                var now = DateTime.Now;
                if (hasChange || (now - lastTableUpdate).TotalMilliseconds >= 500)
                {
                    foreach (var state in _callStates.Values)
                    {
                        table.UpdateRow(new ConsoleTable.Row
                        {
                            FlowName = state.FlowName,
                            CallName = state.CallName,
                            State = state.State,
                            LastStartAt = state.StartTime?.ToString("o"),
                            LastFinishAt = state.FinishTime?.ToString("o"),
                            LastDurationMs = state.DurationMs,
                            CycleCount = state.CycleCount
                        });
                    }
                    table.Render();
                    lastTableUpdate = now;
                }

                // Fast polling of cached values (20ms cycle, reads from PLC service cache)
                await Task.Delay(20, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"   Monitoring error: {ex.Message}");
            }
        }
    }

    private async Task HandleRisingEdgeAsync(string tagAddress)
    {
        if (!_tagMappings.TryGetValue(tagAddress, out var mapping))
            return;

        var callState = _callStates[mapping.CallId];
        var timestamp = DateTime.Now;

        switch (callState.Direction)
        {
            case CallDirection.InOut:
                if (mapping.IsInTag)
                {
                    // In ON: Going → Finish
                    if (callState.State == "Going")
                    {
                        callState.FinishTime = timestamp;
                        callState.DurationMs = (callState.FinishTime.Value - callState.StartTime!.Value).TotalMilliseconds;
                        callState.State = "Finish";
                        callState.CycleCount++;
                        await UpdateDatabaseAsync(mapping.CallId, "Finish", callState.StartTime!.Value, callState.FinishTime.Value, callState.DurationMs.Value, callState.CycleCount);
                    }
                }
                else
                {
                    // Out ON: Ready → Going
                    if (callState.State == "Ready")
                    {
                        callState.StartTime = timestamp;
                        callState.State = "Going";
                        await UpdateDatabaseAsync(mapping.CallId, "Going", callState.StartTime.Value, null, null);
                    }
                }
                break;

            case CallDirection.InOnly:
                // In ON: Ready → Going → Finish (즉시)
                if (callState.State == "Ready")
                {
                    callState.StartTime = timestamp;
                    callState.State = "Going";
                    await UpdateDatabaseAsync(mapping.CallId, "Going", callState.StartTime.Value, null, null);

                    // 즉시 Finish로 전이
                    callState.FinishTime = timestamp;
                    callState.DurationMs = 0; // 순간 경유
                    callState.State = "Finish";
                    callState.CycleCount++;
                    await UpdateDatabaseAsync(mapping.CallId, "Finish", callState.StartTime.Value, callState.FinishTime.Value, callState.DurationMs.Value, callState.CycleCount);
                }
                break;

            case CallDirection.OutOnly:
                // Out ON: Ready → Going
                if (callState.State == "Ready")
                {
                    callState.StartTime = timestamp;
                    callState.State = "Going";
                    await UpdateDatabaseAsync(mapping.CallId, "Going", callState.StartTime.Value, null, null);
                }
                break;
        }
    }

    private async Task HandleFallingEdgeAsync(string tagAddress)
    {
        if (!_tagMappings.TryGetValue(tagAddress, out var mapping))
            return;

        var callState = _callStates[mapping.CallId];
        var timestamp = DateTime.Now;

        switch (callState.Direction)
        {
            case CallDirection.InOut:
                if (mapping.IsInTag)
                {
                    // In OFF: Finish → Ready
                    if (callState.State == "Finish")
                    {
                        callState.State = "Ready";
                        await UpdateDatabaseAsync(mapping.CallId, "Ready", null, null, null);
                    }
                }
                break;

            case CallDirection.InOnly:
                // In OFF: Finish → Ready
                if (callState.State == "Finish")
                {
                    callState.State = "Ready";
                    await UpdateDatabaseAsync(mapping.CallId, "Ready", null, null, null);
                }
                break;

            case CallDirection.OutOnly:
                // Out OFF: Going → Finish → Ready (자동)
                if (callState.State == "Going")
                {
                    callState.FinishTime = timestamp;
                    callState.DurationMs = (callState.FinishTime.Value - callState.StartTime!.Value).TotalMilliseconds;
                    callState.State = "Finish";
                    callState.CycleCount++;
                    await UpdateDatabaseAsync(mapping.CallId, "Finish", callState.StartTime!.Value, callState.FinishTime.Value, callState.DurationMs.Value, callState.CycleCount);

                    // 자동 즉시 Ready 복귀
                    callState.State = "Ready";
                    await UpdateDatabaseAsync(mapping.CallId, "Ready", null, null, null);
                }
                break;
        }
    }

    private async Task UpdateDatabaseAsync(Guid callId, string state, DateTime? startTime, DateTime? finishTime, double? durationMs, int? cycleCount = null)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();

        var sql = cycleCount.HasValue
            ? @"UPDATE dspCall
                SET State = @State,
                    LastStartAt = @StartTime,
                    LastFinishAt = @FinishTime,
                    LastDurationMs = @DurationMs,
                    CycleCount = @CycleCount
                WHERE Id = @CallId"
            : @"UPDATE dspCall
                SET State = @State,
                    LastStartAt = @StartTime,
                    LastFinishAt = @FinishTime,
                    LastDurationMs = @DurationMs
                WHERE Id = @CallId";

        await Dapper.SqlMapper.ExecuteAsync(conn, sql,
            new
            {
                CallId = callId.ToString(),
                State = state,
                StartTime = startTime?.ToString("o"),
                FinishTime = finishTime?.ToString("o"),
                DurationMs = durationMs,
                CycleCount = cycleCount
            });
    }

    private bool ConvertPlcValueToBool(PlcValue plcValue)
    {
        var str = plcValue.ToString();
        if (str.Contains("True", StringComparison.OrdinalIgnoreCase))
            return true;
        if (str.Contains("False", StringComparison.OrdinalIgnoreCase))
            return false;

        if (int.TryParse(str, out var intValue))
            return intValue != 0;

        return false;
    }
}
