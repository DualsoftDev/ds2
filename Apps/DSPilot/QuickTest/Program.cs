using Ds2.Core;
using DSPilot.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
    builder.SetMinimumLevel(LogLevel.Warning);
});

var configPath = Path.GetFullPath("../DSPilot/appsettings.json", Environment.CurrentDirectory);
var configuration = new ConfigurationBuilder()
    .SetBasePath(Path.GetDirectoryName(configPath)!)
    .AddJsonFile(Path.GetFileName(configPath), optional: false)
    .Build();

var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Dualsoft", "DSPilot", "plc.db");

Console.WriteLine($"DB Path: {dbPath}");
Console.WriteLine($"AppSettings: {configPath}");
Console.WriteLine();

using var connection = new SqliteConnection($"Data Source={dbPath}");
await connection.OpenAsync();

await PrintTableCounts(connection);
await PrintLogValueSummary(connection);
var recentTransitionAddress = await PrintRecentTransitionSummary(connection);

var projectService = new DsProjectService(configuration, loggerFactory.CreateLogger<DsProjectService>());
await PrintSignalMappingSummary(connection, projectService);

if (!string.IsNullOrWhiteSpace(recentTransitionAddress))
{
    await PrintRecentLogsForAddress(connection, recentTransitionAddress);
}

static async Task PrintTableCounts(SqliteConnection connection)
{
    Console.WriteLine("=== Table Counts ===");
    foreach (var table in new[] { "dspFlow", "dspCall", "plcTag", "plcTagLog" })
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        var count = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        Console.WriteLine($"{table,-10}: {count}");
    }
    Console.WriteLine();
}

static async Task PrintLogValueSummary(SqliteConnection connection)
{
    Console.WriteLine("=== Log Value Summary ===");

    const string sql = """
SELECT
    COUNT(*) AS TotalLogs,
    SUM(CASE WHEN lower(trim(value)) IN ('1', 'true', 'on') THEN 1 ELSE 0 END) AS OnLogs,
    SUM(CASE WHEN lower(trim(value)) IN ('0', 'false', 'off') THEN 1 ELSE 0 END) AS OffLogs
FROM plcTagLog
""";

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    await using var reader = await cmd.ExecuteReaderAsync();
    if (await reader.ReadAsync())
    {
        Console.WriteLine($"total: {reader.GetInt64(0)}");
        Console.WriteLine($"on   : {reader.GetInt64(1)}");
        Console.WriteLine($"off  : {reader.GetInt64(2)}");
    }

    Console.WriteLine();
    Console.WriteLine("Top normalized values:");

    const string topValuesSql = """
SELECT lower(trim(value)) AS NormalizedValue, COUNT(*) AS Count
FROM plcTagLog
GROUP BY lower(trim(value))
ORDER BY Count DESC
LIMIT 10
""";

    await using var topCmd = connection.CreateCommand();
    topCmd.CommandText = topValuesSql;
    await using var topReader = await topCmd.ExecuteReaderAsync();
    while (await topReader.ReadAsync())
    {
        var normalizedValue = topReader.IsDBNull(0) ? "<null>" : topReader.GetString(0);
        Console.WriteLine($"  {normalizedValue,-8} {topReader.GetInt64(1)}");
    }

    Console.WriteLine();
}

static async Task<string?> PrintRecentTransitionSummary(SqliteConnection connection)
{
    Console.WriteLine("=== Tags With Both ON and OFF Logs ===");

    const string sql = """
SELECT
    t.Address,
    SUM(CASE WHEN lower(trim(l.Value)) IN ('1', 'true', 'on') THEN 1 ELSE 0 END) AS OnCount,
    SUM(CASE WHEN lower(trim(l.Value)) IN ('0', 'false', 'off') THEN 1 ELSE 0 END) AS OffCount,
    MAX(l.DateTime) AS LastSeen
FROM plcTagLog l
INNER JOIN plcTag t ON l.PlcTagId = t.Id
GROUP BY l.PlcTagId, t.Address
HAVING OnCount > 0 AND OffCount > 0
ORDER BY LastSeen DESC
LIMIT 10
""";

    string? firstAddress = null;
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    await using var reader = await cmd.ExecuteReaderAsync();
    var rowCount = 0;
    while (await reader.ReadAsync())
    {
        firstAddress ??= reader.GetString(0);
        Console.WriteLine($"  {reader.GetString(0),-20} on={reader.GetInt64(1),6} off={reader.GetInt64(2),6} last={reader.GetString(3)}");
        rowCount++;
    }

    if (rowCount == 0)
    {
        Console.WriteLine("  none");
    }

    Console.WriteLine();
    return firstAddress;
}

static async Task PrintSignalMappingSummary(SqliteConnection connection, DsProjectService projectService)
{
    Console.WriteLine("=== Signal Mapping Summary ===");
    Console.WriteLine($"project loaded: {projectService.IsLoaded}");

    if (!projectService.IsLoaded)
    {
        Console.WriteLine("project could not be loaded, signal metadata comparison skipped.");
        Console.WriteLine();
        return;
    }

    var store = projectService.GetStore();

    var flowByCallName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    await using (var cmd = connection.CreateCommand())
    {
        cmd.CommandText = "SELECT CallName, FlowName FROM dspCall";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var callName = reader.GetString(0);
            var flowName = reader.GetString(1);
            if (!flowByCallName.TryGetValue(callName, out var flows))
            {
                flows = new List<string>();
                flowByCallName[callName] = flows;
            }

            if (!flows.Contains(flowName, StringComparer.OrdinalIgnoreCase))
            {
                flows.Add(flowName);
            }
        }
    }

    var plcTagAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using (var cmd = connection.CreateCommand())
    {
        cmd.CommandText = "SELECT Address FROM plcTag WHERE Address IS NOT NULL AND trim(Address) <> ''";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plcTagAddresses.Add(reader.GetString(0));
        }
    }

    var apiCallCount = 0;
    var matchedApiCallCount = 0;
    var expectedSignalCount = 0;
    var addressMatchedSignalCount = 0;
    var missingCallNames = new List<string>();
    var missingAddresses = new List<string>();

    foreach (var apiCall in store.ApiCallsReadOnly.Values)
    {
        apiCallCount++;
        if (!flowByCallName.TryGetValue(apiCall.Name, out var flows))
        {
            if (missingCallNames.Count < 15)
            {
                missingCallNames.Add(apiCall.Name);
            }
            continue;
        }

        matchedApiCallCount++;

        foreach (var _ in flows)
        {
            TryAddSignalAddress(apiCall.InTag, plcTagAddresses, ref expectedSignalCount, ref addressMatchedSignalCount, missingAddresses);
            TryAddSignalAddress(apiCall.OutTag, plcTagAddresses, ref expectedSignalCount, ref addressMatchedSignalCount, missingAddresses);
        }
    }

    Console.WriteLine($"api calls in project      : {apiCallCount}");
    Console.WriteLine($"api calls matched to dsp  : {matchedApiCallCount}");
    Console.WriteLine($"expected chart signals    : {expectedSignalCount}");
    Console.WriteLine($"signals with plcTag addr  : {addressMatchedSignalCount}");
    Console.WriteLine($"signals missing plcTag    : {expectedSignalCount - addressMatchedSignalCount}");

    if (missingCallNames.Count > 0)
    {
        Console.WriteLine("sample missing call names:");
        foreach (var callName in missingCallNames)
        {
            Console.WriteLine($"  {callName}");
        }
    }

    if (missingAddresses.Count > 0)
    {
        Console.WriteLine("sample missing addresses:");
        foreach (var address in missingAddresses.Distinct(StringComparer.OrdinalIgnoreCase).Take(15))
        {
            Console.WriteLine($"  {address}");
        }
    }

    Console.WriteLine();
}

static void TryAddSignalAddress(
    Microsoft.FSharp.Core.FSharpOption<IOTag> tagOption,
    HashSet<string> plcTagAddresses,
    ref int expectedSignalCount,
    ref int addressMatchedSignalCount,
    List<string> missingAddresses)
{
    if (!Microsoft.FSharp.Core.FSharpOption<IOTag>.get_IsSome(tagOption))
    {
        return;
    }

    var tag = tagOption.Value;
    if (string.IsNullOrWhiteSpace(tag.Address))
    {
        return;
    }

    expectedSignalCount++;
    if (plcTagAddresses.Contains(tag.Address))
    {
        addressMatchedSignalCount++;
    }
    else if (missingAddresses.Count < 50)
    {
        missingAddresses.Add(tag.Address);
    }
}

static async Task PrintRecentLogsForAddress(SqliteConnection connection, string address)
{
    Console.WriteLine($"=== Recent Logs For {address} ===");

    const string sql = """
SELECT l.DateTime, l.Value
FROM plcTagLog l
INNER JOIN plcTag t ON l.PlcTagId = t.Id
WHERE t.Address = $address
ORDER BY l.DateTime DESC, l.Id DESC
LIMIT 20
""";

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("$address", address);

    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        Console.WriteLine($"  {reader.GetString(0)}  {reader.GetString(1)}");
    }

    Console.WriteLine();
}
