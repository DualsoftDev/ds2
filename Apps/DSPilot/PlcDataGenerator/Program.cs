using Microsoft.Extensions.Configuration;
using Dapper;
using Microsoft.Data.Sqlite;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Collections;

namespace PlcDataGenerator;

class Program
{
    private static IConfiguration? _configuration;
    private static DsStore? _store;
    private static string? _dbPath;
    private static CycleSettings? _cycleSettings;
    private static Dictionary<Guid, PlcTagInfo> _tagCache = new();
    private static Dictionary<string, PlcTagInfo> _addressToTagMap = new(); // Address → PlcTagInfo
    private static List<CallTagMapping> _callMappings = new(); // Call → Tag mappings
    private static Random _random = new();
    private static bool _databaseWasMissing;

    static async Task Main(string[] args)
    {
        Console.WriteLine("=== PLC Data Generator ===");
        Console.WriteLine();

        // Check if user wants to verify database
        if (args.Length > 0 && args[0] == "--check-db")
        {
            LoadConfiguration();
            await CheckDatabaseAsync();
            return;
        }

        try
        {
            // 1. Load configuration
            LoadConfiguration();

            // 2. Load AASX file
            LoadAasxFile();

            // 3. Ensure PLC database exists and is initialized
            await EnsureDatabaseInitializedAsync();

            // 4. Load PLC tags from database
            await LoadPlcTagsAsync();

            // 5. Map Calls to PLC tags
            MapCallsToTags();

            // 6. Start cycle generation
            await RunCycleGenerationAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Environment.ExitCode = 1;
        }
    }

    static async Task CheckDatabaseAsync()
    {
        Console.WriteLine(">> Checking plcTagLog database...\n");

        using var connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;");
        await connection.OpenAsync();

        // Count total rows
        var count = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM plcTagLog");
        Console.WriteLine($"Total rows in plcTagLog: {count:N0}\n");

        if (count == 0)
        {
            Console.WriteLine("[WARNING] No data found in plcTagLog table.");
            return;
        }

        // Get recent entries
        const string sql = @"
            SELECT
                tl.id,
                tl.plcTagId,
                t.name as TagName,
                t.address as TagAddress,
                tl.dateTime,
                tl.value
            FROM plcTagLog tl
            INNER JOIN plcTag t ON tl.plcTagId = t.id
            ORDER BY tl.dateTime DESC
            LIMIT 20";

        var logs = await connection.QueryAsync<dynamic>(sql);

        Console.WriteLine("Recent 20 entries:");
        Console.WriteLine("=".PadRight(130, '='));

        foreach (var log in logs)
        {
            Console.WriteLine($"ID: {log.id,6} | Tag: {log.TagName,-40} | Addr: {log.TagAddress,-8} | Time: {log.dateTime,-23} | Val: {log.value}");
        }
    }

    static void LoadConfiguration()
    {
        Console.WriteLine(">> Loading configuration...");

        _configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        _dbPath = _configuration["PlcDatabase:Path"];
        if (string.IsNullOrEmpty(_dbPath))
        {
            throw new InvalidOperationException("PlcDatabase:Path is not configured");
        }

        // Try to resolve the path relative to the current directory first
        var fullPath = Path.GetFullPath(_dbPath);

        if (!File.Exists(fullPath))
        {
            // Try relative to the executable location
            var exeDir = AppContext.BaseDirectory;
            var altPath = Path.Combine(exeDir, _dbPath);
            fullPath = Path.GetFullPath(altPath);

            if (!File.Exists(fullPath))
            {
                // Try one more location: relative to project root (3 levels up from bin/Debug/net9.0)
                var projectRoot = Path.Combine(exeDir, "..", "..", "..");
                var projectPath = Path.Combine(projectRoot, _dbPath);
                fullPath = Path.GetFullPath(projectPath);
            }
        }

        _dbPath = fullPath;
        _databaseWasMissing = !File.Exists(_dbPath);

        _cycleSettings = _configuration.GetSection("CycleSettings").Get<CycleSettings>();
        if (_cycleSettings == null)
        {
            throw new InvalidOperationException("CycleSettings not found in configuration");
        }

        Console.WriteLine($"   DB Path: {_dbPath}");
        if (_databaseWasMissing)
        {
            Console.WriteLine("   DB Status: file not found, a new database will be created");
        }
        Console.WriteLine($"   Cycle Interval: {_cycleSettings.CycleIntervalMs}ms");
        Console.WriteLine();
    }

    static async Task EnsureDatabaseInitializedAsync()
    {
        if (string.IsNullOrWhiteSpace(_dbPath))
        {
            throw new InvalidOperationException("Database path is not initialized");
        }

        Console.WriteLine(">> Ensuring PLC database...");

        var dbDirectory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(dbDirectory))
        {
            Directory.CreateDirectory(dbDirectory);
        }

        await using var connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadWriteCreate;");
        await connection.OpenAsync();

        const string schemaSql = @"
CREATE TABLE IF NOT EXISTS plc (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    projectId INTEGER NULL,
    name TEXT NOT NULL,
    connection TEXT NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS plcTag (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    plcId INTEGER NOT NULL,
    name TEXT NOT NULL,
    address TEXT NOT NULL,
    dataType TEXT NOT NULL DEFAULT 'BOOL',
    FOREIGN KEY(plcId) REFERENCES plc(id)
);

CREATE TABLE IF NOT EXISTS plcTagLog (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    plcTagId INTEGER NOT NULL,
    dateTime TEXT NOT NULL,
    value TEXT NULL,
    FOREIGN KEY(plcTagId) REFERENCES plcTag(id)
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_plcTag_address ON plcTag(address);
CREATE INDEX IF NOT EXISTS idx_plcTag_plcId ON plcTag(plcId);
CREATE INDEX IF NOT EXISTS idx_plcTagLog_tag_datetime ON plcTagLog(plcTagId, dateTime);
CREATE INDEX IF NOT EXISTS idx_plcTagLog_datetime ON plcTagLog(dateTime);";

        await connection.ExecuteAsync(schemaSql);

        var plcCount = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM plc");
        if (plcCount == 0)
        {
            const string insertPlcSql = @"
INSERT INTO plc (projectId, name, connection)
VALUES (NULL, @Name, @Connection);";

            await connection.ExecuteAsync(insertPlcSql, new
            {
                Name = "DSPilot Virtual PLC",
                Connection = "{}"
            });
        }

        await SeedTagsFromAasxAsync(connection);

        var tagCount = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM plcTag");
        var logCount = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM plcTagLog");

        if (_databaseWasMissing)
        {
            Console.WriteLine("   Created new PLC database file");
        }

        Console.WriteLine($"   plc rows: {await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM plc")}");
        Console.WriteLine($"   plcTag rows: {tagCount}");
        Console.WriteLine($"   plcTagLog rows: {logCount}");
        Console.WriteLine();
    }

    static async Task SeedTagsFromAasxAsync(SqliteConnection connection)
    {
        if (_store == null)
        {
            throw new InvalidOperationException("AASX store is not loaded");
        }

        var plcId = await connection.ExecuteScalarAsync<int>("SELECT id FROM plc ORDER BY id LIMIT 1");
        var desiredTags = CollectDesiredTagsFromAasx();

        if (desiredTags.Count == 0)
        {
            Console.WriteLine("   No InTag/OutTag addresses found in AASX, skipping plcTag seed");
            return;
        }

        const string insertTagSql = @"
INSERT OR IGNORE INTO plcTag (plcId, name, address, dataType)
VALUES (@PlcId, @Name, @Address, @DataType);";

        var inserted = 0;
        foreach (var tag in desiredTags.Values)
        {
            inserted += await connection.ExecuteAsync(insertTagSql, new
            {
                PlcId = plcId,
                Name = tag.Name,
                Address = tag.Address,
                DataType = tag.DataType
            });
        }

        Console.WriteLine($"   AASX tag seed candidates: {desiredTags.Count}, inserted: {inserted}");
    }

    static Dictionary<string, SeedTagInfo> CollectDesiredTagsFromAasx()
    {
        var tags = new Dictionary<string, SeedTagInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var flow in Queries.allFlows(_store!).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var work in GetOrderedWorksForFlow(flow))
            {
                foreach (var call in GetOrderedCallsForWork(work))
                {
                    foreach (var apiCall in call.ApiCalls)
                    {
                        if (Microsoft.FSharp.Core.FSharpOption<Ds2.Core.IOTag>.get_IsSome(apiCall.InTag))
                        {
                            AddSeedTag(tags, apiCall.InTag.Value, $"{call.Name} In");
                        }

                        if (Microsoft.FSharp.Core.FSharpOption<Ds2.Core.IOTag>.get_IsSome(apiCall.OutTag))
                        {
                            AddSeedTag(tags, apiCall.OutTag.Value, $"{call.Name} Out");
                        }
                    }
                }
            }
        }

        return tags;
    }

    static void AddSeedTag(Dictionary<string, SeedTagInfo> tags, IOTag ioTag, string fallbackName)
    {
        if (string.IsNullOrWhiteSpace(ioTag.Address))
        {
            return;
        }

        if (tags.ContainsKey(ioTag.Address))
        {
            return;
        }

        tags[ioTag.Address] = new SeedTagInfo
        {
            Name = string.IsNullOrWhiteSpace(ioTag.Name) ? fallbackName : ioTag.Name,
            Address = ioTag.Address,
            DataType = "BOOL"
        };
    }

    static void LoadAasxFile()
    {
        Console.WriteLine(">> Loading AASX file...");

        var aasxPath = _configuration!["AasxFilePath"];
        if (string.IsNullOrEmpty(aasxPath))
        {
            throw new InvalidOperationException("AasxFilePath is not configured");
        }

        // Try to resolve the path
        var fullPath = Path.GetFullPath(aasxPath);

        if (!File.Exists(fullPath))
        {
            // Try relative to the executable location
            var exeDir = AppContext.BaseDirectory;
            var altPath = Path.Combine(exeDir, aasxPath);
            fullPath = Path.GetFullPath(altPath);

            if (!File.Exists(fullPath))
            {
                // Try relative to project root
                var projectRoot = Path.Combine(exeDir, "..", "..", "..");
                var projectPath = Path.Combine(projectRoot, aasxPath);
                fullPath = Path.GetFullPath(projectPath);
            }
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"AASX file not found: {aasxPath} (tried: {fullPath})");
        }

        aasxPath = fullPath;

        _store = new DsStore();
        var result = Ds2.Aasx.AasxImporter.importIntoStore(_store, aasxPath);

        if (!result)
        {
            throw new InvalidOperationException($"Failed to load AASX file: {aasxPath}");
        }

        var projects = Queries.allProjects(_store);
        var project = ListModule.IsEmpty(projects) ? null : ListModule.Head(projects);

        if (project == null)
        {
            throw new InvalidOperationException("No project found in AASX file");
        }

        Console.WriteLine($"   Project loaded: {project.Name}");

        var flows = Queries.allFlows(_store).ToList();
        Console.WriteLine($"   Flows found: {flows.Count}");

        foreach (var flow in flows)
        {
            var works = Queries.worksOf(flow.Id, _store).ToList();
            var callCount = works.Sum(w => Queries.callsOf(w.Id, _store).Count());
            Console.WriteLine($"      - {flow.Name}: {callCount} calls");
        }
        Console.WriteLine();
    }

    static async Task LoadPlcTagsAsync()
    {
        Console.WriteLine(">> Loading PLC tags from database...");

        using var connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadWrite;");
        await connection.OpenAsync();

        const string sql = @"
            SELECT
                t.id as Id,
                t.plcId as PlcId,
                t.name as Name,
                t.address as Address,
                t.dataType as DataType
            FROM plcTag t
            ORDER BY t.id";

        var tags = await connection.QueryAsync<PlcTagRow>(sql);

        foreach (var tag in tags)
        {
            var tagInfo = new PlcTagInfo
            {
                Id = tag.Id,
                Name = tag.Name,
                Address = tag.Address,
                DataType = tag.DataType
            };

            _tagCache[Guid.NewGuid()] = tagInfo;
            _addressToTagMap[tag.Address] = tagInfo; // Address-based lookup
        }

        Console.WriteLine($"   Tags loaded: {_tagCache.Count}");

        // Show sample tags for debugging
        Console.WriteLine("\n   Sample tag names:");
        var sampleTags = _tagCache.Values.Take(10).ToList();
        foreach (var tag in sampleTags)
        {
            Console.WriteLine($"      - {tag.Name} (Address: {tag.Address})");
        }
        Console.WriteLine();
    }

    static void MapCallsToTags()
    {
        Console.WriteLine(">> Mapping Calls to PLC tags (by Address)...");

        _callMappings.Clear();

        var allFlows = Queries.allFlows(_store!)
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mappingCount = 0;
        var sampleMappings = new List<string>();

        foreach (var flow in allFlows)
        {
            var works = GetOrderedWorksForFlow(flow);
            foreach (var work in works)
            {
                var calls = GetOrderedCallsForWork(work);
                foreach (var call in calls)
                {
                    PlcTagInfo? inTag = null;
                    PlcTagInfo? outTag = null;

                    // Process each ApiCall for this Call
                    foreach (var apiCall in call.ApiCalls)
                    {
                        // Check InTag (F# Option type)
                        if (Microsoft.FSharp.Core.FSharpOption<Ds2.Core.IOTag>.get_IsSome(apiCall.InTag))
                        {
                            var ioTag = apiCall.InTag.Value;
                            if (_addressToTagMap.TryGetValue(ioTag.Address, out var tag))
                            {
                                inTag = tag;
                                mappingCount++;

                                if (sampleMappings.Count < 10)
                                {
                                    sampleMappings.Add($"{call.Name} → InTag: {tag.Name} ({tag.Address})");
                                }
                            }
                        }

                        // Check OutTag (F# Option type)
                        if (Microsoft.FSharp.Core.FSharpOption<Ds2.Core.IOTag>.get_IsSome(apiCall.OutTag))
                        {
                            var ioTag = apiCall.OutTag.Value;
                            if (_addressToTagMap.TryGetValue(ioTag.Address, out var tag))
                            {
                                outTag = tag;
                                mappingCount++;

                                if (sampleMappings.Count < 10)
                                {
                                    sampleMappings.Add($"{call.Name} → OutTag: {tag.Name} ({tag.Address})");
                                }
                            }
                        }
                    }

                    // If at least one tag was mapped, add to call mappings
                    if (inTag != null || outTag != null)
                    {
                        _callMappings.Add(new CallTagMapping
                        {
                            Call = call,
                            InTag = inTag,
                            OutTag = outTag,
                            FlowName = flow.Name
                        });
                    }
                }
            }
        }

        Console.WriteLine($"   Calls with mapped tags: {_callMappings.Count}");
        Console.WriteLine($"   Total tag mappings: {mappingCount}");

        // Show sample mappings for debugging
        if (sampleMappings.Any())
        {
            Console.WriteLine("\n   Sample mappings:");
            foreach (var mapping in sampleMappings)
            {
                Console.WriteLine($"      - {mapping}");
            }
        }
        else
        {
            Console.WriteLine("\n   [WARNING] No mappings found! Check that:");
            Console.WriteLine("      1. AASX ApiCalls have InTag/OutTag defined");
            Console.WriteLine("      2. InTag/OutTag Address matches PLC tag Address");
        }

        Console.WriteLine();
    }

    static async Task RunCycleGenerationAsync()
    {
        if (_callMappings.Count == 0)
        {
            Console.WriteLine("[WARNING] No call mappings found. Cannot generate cycles.");
            Console.WriteLine("   Please check AASX ApiCall InTag/OutTag configuration.");
            return;
        }

        // Group mappings by Flow
        var flowGroups = _callMappings
            .GroupBy(m => m.FlowName)
            .Select(g => new FlowGroup
            {
                FlowName = g.Key,
                Mappings = g.ToList()
            })
            .Where(fg => fg.Mappings.Any())
            .ToList();

        Console.WriteLine(">> Starting cycle generation (Flow-based parallel execution)...");
        Console.WriteLine($"   Total Flows: {flowGroups.Count}");
        Console.WriteLine($"   Total Calls: {_callMappings.Count}");
        Console.WriteLine("   Press Ctrl+C to stop");
        Console.WriteLine();

        var cycleNumber = 0;

        while (_cycleSettings!.AutoLoopEnabled)
        {
            cycleNumber++;
            Console.WriteLine($"--- Cycle #{cycleNumber} ---");

            // Execute all flows in parallel, while preserving sequential call order inside each flow
            var flowTasks = flowGroups
                .Select(flowGroup => ExecuteFlowAsync(flowGroup, cycleNumber))
                .ToList();

            await Task.WhenAll(flowTasks);

            Console.WriteLine($"Waiting {_cycleSettings.CycleIntervalMs}ms until next cycle...");
            Console.WriteLine();

            await Task.Delay(_cycleSettings.CycleIntervalMs);
        }
    }

    static async Task ExecuteFlowAsync(FlowGroup flowGroup, int cycleNumber)
    {
        Console.WriteLine($"  Flow: {flowGroup.FlowName} ({flowGroup.Mappings.Count} calls)");

        for (var i = 0; i < flowGroup.Mappings.Count; i++)
        {
            await ResetPreviousInTagAsync(flowGroup);
            await ExecuteCallAsync(flowGroup, flowGroup.Mappings[i], cycleNumber);

            if (i < flowGroup.Mappings.Count - 1)
            {
                await Task.Delay(_cycleSettings!.CallGapMs);
            }
        }
    }

    static async Task ResetPreviousInTagAsync(FlowGroup flowGroup)
    {
        if (flowGroup.ActiveInTag == null)
        {
            return;
        }

        await WriteTagLogAsync(flowGroup.ActiveInTag.Id, "0", "Previous input off by next call start");
        flowGroup.ActiveInTag = null;
    }

    static async Task ExecuteCallAsync(FlowGroup flowGroup, CallTagMapping mapping, int cycleNumber)
    {
        var call = mapping.Call;
        var inTag = mapping.InTag;
        var outTag = mapping.OutTag;

        Console.Write($"    > {call.Name}");

        if (outTag != null)
        {
            await WriteTagLogAsync(outTag.Id, "1", "Call output on");
            Console.Write($" [Out:{outTag.Address}=1]");

            if (inTag != null)
            {
                await Task.Delay(_cycleSettings!.CallDurationMs);

                await WriteTagLogAsync(inTag.Id, "1", "Call input on after output");
                flowGroup.ActiveInTag = inTag;
                Console.Write($" [In:{inTag.Address}=1]");

                await WriteTagLogAsync(outTag.Id, "0", "Call output off by input on");
                Console.Write($" [Out:{outTag.Address}=0]");

                await Task.Delay(_cycleSettings.FinishSignalDurationMs);
            }
            else
            {
                await Task.Delay(_cycleSettings!.CallDurationMs);

                await WriteTagLogAsync(outTag.Id, "0", "Call output pulse off");
                Console.Write($" [Out:{outTag.Address}=0]");

                await Task.Delay(_cycleSettings.FinishSignalDurationMs);
            }
        }
        else if (inTag != null)
        {
            await WriteTagLogAsync(inTag.Id, "1", "In-only call input on");
            flowGroup.ActiveInTag = inTag;
            Console.Write($" [In:{inTag.Address}=1]");

            await Task.Delay(_cycleSettings!.FinishSignalDurationMs);
        }
        else
        {
            Console.Write(" [No mapped tags]");
        }

        Console.WriteLine(" OK");
    }

    static List<Work> GetOrderedWorksForFlow(Flow flow)
    {
        var works = Queries.worksOf(flow.Id, _store!).ToList();
        if (works.Count <= 1)
        {
            return works;
        }

        var workIds = works.Select(w => w.Id).ToHashSet();
        var arrows = Queries.arrowWorksOf(flow.ParentId, _store!)
            .Where(a => workIds.Contains(a.SourceId) && workIds.Contains(a.TargetId) && IsStartLike(a.ArrowType))
            .Select(a => (a.SourceId, a.TargetId))
            .ToList();

        return TopologicallyOrder(works, w => w.Id, w => w.Name, arrows);
    }

    static List<Call> GetOrderedCallsForWork(Work work)
    {
        var calls = Queries.callsOf(work.Id, _store!).ToList();
        if (calls.Count <= 1)
        {
            return calls;
        }

        var callIds = calls.Select(c => c.Id).ToHashSet();
        var arrows = Queries.arrowCallsOf(work.Id, _store!)
            .Where(a => callIds.Contains(a.SourceId) && callIds.Contains(a.TargetId) && IsStartLike(a.ArrowType))
            .Select(a => (a.SourceId, a.TargetId))
            .ToList();

        return TopologicallyOrder(calls, c => c.Id, c => c.Name, arrows);
    }

    static bool IsStartLike(ArrowType arrowType) =>
        arrowType == ArrowType.Start || arrowType == ArrowType.StartReset;

    static List<T> TopologicallyOrder<T>(
        List<T> nodes,
        Func<T, Guid> idSelector,
        Func<T, string> nameSelector,
        List<(Guid SourceId, Guid TargetId)> edges)
        where T : class
    {
        var nodeById = nodes.ToDictionary(idSelector);
        var indegree = nodes.ToDictionary(idSelector, _ => 0);
        var outgoing = nodes.ToDictionary(idSelector, _ => new List<Guid>());
        var seenEdges = new HashSet<(Guid SourceId, Guid TargetId)>();

        foreach (var (sourceId, targetId) in edges)
        {
            if (!nodeById.ContainsKey(sourceId) || !nodeById.ContainsKey(targetId))
            {
                continue;
            }

            if (!seenEdges.Add((sourceId, targetId)))
            {
                continue;
            }

            outgoing[sourceId].Add(targetId);
            indegree[targetId]++;
        }

        var result = new List<T>(nodes.Count);
        var processed = new HashSet<Guid>();

        while (processed.Count < nodes.Count)
        {
            var nextNode = nodes
                .Where(node =>
                {
                    var id = idSelector(node);
                    return !processed.Contains(id) && indegree[id] == 0;
                })
                .OrderBy(node => nameSelector(node), StringComparer.OrdinalIgnoreCase)
                .ThenBy(node => idSelector(node).ToString(), StringComparer.Ordinal)
                .FirstOrDefault();

            if (nextNode == null)
            {
                break;
            }

            var nextId = idSelector(nextNode);
            processed.Add(nextId);
            result.Add(nextNode);

            foreach (var targetId in outgoing[nextId])
            {
                indegree[targetId]--;
            }
        }

        if (result.Count == nodes.Count)
        {
            return result;
        }

        var remaining = nodes
            .Where(node => !processed.Contains(idSelector(node)))
            .OrderBy(node => nameSelector(node), StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => idSelector(node).ToString(), StringComparer.Ordinal);

        result.AddRange(remaining);
        return result;
    }

    static async Task WriteTagLogAsync(int tagId, string value, string? comment = null)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadWrite;");
        await connection.OpenAsync();

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");

        const string sql = @"
            INSERT INTO plcTagLog (plcTagId, dateTime, value)
            VALUES (@TagId, @DateTime, @Value)";

        await connection.ExecuteAsync(sql, new
        {
            TagId = tagId,
            DateTime = timestamp,
            Value = value
        });
    }
}

// Configuration classes
class CycleSettings
{
    public int CycleIntervalMs { get; set; }
    public int CallDurationMs { get; set; }
    public int CallGapMs { get; set; }
    public int FinishSignalDurationMs { get; set; }
    public bool AutoLoopEnabled { get; set; }
}

// Data models
class PlcTagRow
{
    public int Id { get; set; }
    public int PlcId { get; set; }
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string DataType { get; set; } = "";
}

class PlcTagInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string DataType { get; set; } = "";
}

class CallTagMapping
{
    public Call Call { get; set; } = null!;
    public PlcTagInfo? InTag { get; set; }
    public PlcTagInfo? OutTag { get; set; }
    public string FlowName { get; set; } = "";
}

class FlowGroup
{
    public string FlowName { get; set; } = "";
    public List<CallTagMapping> Mappings { get; set; } = new();
    public PlcTagInfo? ActiveInTag { get; set; }
}

class SeedTagInfo
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string DataType { get; set; } = "BOOL";
}
