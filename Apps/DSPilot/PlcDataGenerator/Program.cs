using Microsoft.Extensions.Configuration;
using Dapper;
using Microsoft.Data.Sqlite;
using Ds2.Core;
using Ds2.UI.Core;
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

            // 3. Load PLC tags from database
            await LoadPlcTagsAsync();

            // 4. Map Calls to PLC tags
            MapCallsToTags();

            // 5. Start cycle generation
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

        if (!File.Exists(fullPath))
        {
            Console.WriteLine($"[ERROR] Current directory: {Directory.GetCurrentDirectory()}");
            Console.WriteLine($"[ERROR] Executable directory: {AppContext.BaseDirectory}");
            Console.WriteLine($"[ERROR] Configured path: {_dbPath}");
            Console.WriteLine($"[ERROR] Full path tried: {fullPath}");
            throw new FileNotFoundException($"PLC Database not found: {_dbPath}");
        }

        _dbPath = fullPath;

        _cycleSettings = _configuration.GetSection("CycleSettings").Get<CycleSettings>();
        if (_cycleSettings == null)
        {
            throw new InvalidOperationException("CycleSettings not found in configuration");
        }

        Console.WriteLine($"   DB Path: {_dbPath}");
        Console.WriteLine($"   Cycle Interval: {_cycleSettings.CycleIntervalMs}ms");
        Console.WriteLine();
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

        var projects = DsQuery.allProjects(_store);
        var project = ListModule.IsEmpty(projects) ? null : ListModule.Head(projects);

        if (project == null)
        {
            throw new InvalidOperationException("No project found in AASX file");
        }

        Console.WriteLine($"   Project loaded: {project.Name}");

        var flows = DsQuery.allFlows(_store).ToList();
        Console.WriteLine($"   Flows found: {flows.Count}");

        foreach (var flow in flows)
        {
            var works = DsQuery.worksOf(flow.Id, _store).ToList();
            var callCount = works.Sum(w => DsQuery.callsOf(w.Id, _store).Count());
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

        var allFlows = DsQuery.allFlows(_store!).ToList();
        var mappingCount = 0;
        var sampleMappings = new List<string>();

        foreach (var flow in allFlows)
        {
            var works = DsQuery.worksOf(flow.Id, _store!).ToList();
            foreach (var work in works)
            {
                var calls = DsQuery.callsOf(work.Id, _store!).ToList();
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

            // Execute each flow's calls in parallel
            foreach (var flowGroup in flowGroups)
            {
                await ExecuteFlowAsync(flowGroup, cycleNumber);
            }

            Console.WriteLine($"Waiting {_cycleSettings.CycleIntervalMs}ms until next cycle...");
            Console.WriteLine();

            await Task.Delay(_cycleSettings.CycleIntervalMs);
        }
    }

    static async Task ExecuteFlowAsync(FlowGroup flowGroup, int cycleNumber)
    {
        Console.WriteLine($"  Flow: {flowGroup.FlowName} ({flowGroup.Mappings.Count} calls)");

        // Execute all calls in this flow simultaneously
        var tasks = flowGroup.Mappings.Select(mapping => ExecuteCallAsync(mapping, cycleNumber)).ToList();

        await Task.WhenAll(tasks);
    }

    static async Task ExecuteCallAsync(CallTagMapping mapping, int cycleNumber)
    {
        var call = mapping.Call;
        var inTag = mapping.InTag;
        var outTag = mapping.OutTag;

        Console.Write($"    > {call.Name}");

        // InTag: Going signal
        if (inTag != null)
        {
            await WriteTagLogAsync(inTag.Id, "1", "Going signal");
            Console.Write($" [In:{inTag.Address}=1]");
        }

        await Task.Delay(_cycleSettings!.CallDurationMs);

        // OutTag: Finish signal
        if (outTag != null)
        {
            await WriteTagLogAsync(outTag.Id, "1", "Finish signal");
            Console.Write($" [Out:{outTag.Address}=1]");
        }

        await Task.Delay(_cycleSettings.FinishSignalDurationMs);

        // Reset signals
        if (inTag != null)
        {
            await WriteTagLogAsync(inTag.Id, "0", "Reset");
        }
        if (outTag != null)
        {
            await WriteTagLogAsync(outTag.Id, "0", "Reset");
        }

        Console.WriteLine(" OK");

        await Task.Delay(_cycleSettings.CallGapMs);
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
}
