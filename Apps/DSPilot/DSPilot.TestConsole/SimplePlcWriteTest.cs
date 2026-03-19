using Ev2.Backend.Common;
using Ev2.Backend.PLC;
using Ev2.PLC.Protocol.MX;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Core;
using PlcDataType = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType;
using PlcValue = Ev2.PLC.Common.CoreDataTypesModule.PlcValue;
using TagSpec = Ev2.PLC.Common.TagSpecModule.TagSpec;

namespace DSPilot.TestConsole;

/// <summary>
/// 현장 로그 DB를 읽어서 PLC에 직접 쓰는 간단한 테스트
/// PLCBackendService의 복잡한 설정 없이 GlobalCommunication.SubjectC2S만 사용
/// </summary>
public static class SimplePlcWriteTest
{
    private class LogEntry
    {
        public int Id { get; set; }
        public int PlcTagId { get; set; }
        public DateTime DateTime { get; set; }
        public string? Value { get; set; }
        public string? TagName { get; set; }
        public string? TagAddress { get; set; }
        public string? DataType { get; set; }
    }

    public static async Task RunAsync(string dbPath)
    {
        Console.WriteLine("=== Simple PLC Write Test (Direct DB → PLC) ===");
        Console.WriteLine();

        try
        {
            // 1. 현장 로그 DB 로드
            var fullPath = Path.GetFullPath(dbPath);
            Console.WriteLine($"1️⃣  Loading log database...");
            Console.WriteLine($"   Path: {fullPath}");

            if (!File.Exists(fullPath))
            {
                Console.WriteLine($"   ❌ Database file not found: {fullPath}");
                return;
            }

            Console.WriteLine($"   ✅ Database file found");
            Console.WriteLine();

            // 2. 로그 데이터 로드
            Console.WriteLine("2️⃣  Loading log entries...");
            var logs = await LoadLogsFromDatabaseAsync(fullPath);

            if (logs.Count == 0)
            {
                Console.WriteLine("   ⚠️  No log entries found in database");
                return;
            }

            Console.WriteLine($"   ✅ Loaded {logs.Count:N0} log entries");

            var firstLog = logs.First();
            var lastLog = logs.Last();

            Console.WriteLine($"   📅 Time range: {firstLog.DateTime:yyyy-MM-dd HH:mm:ss.fff} ~ {lastLog.DateTime:yyyy-MM-dd HH:mm:ss.fff}");
            Console.WriteLine($"   ⏱️  Total duration: {(lastLog.DateTime - firstLog.DateTime).TotalSeconds:F1} seconds");
            Console.WriteLine();

            // 3. 고유 태그 목록 확인
            var uniqueTags = logs.Select(l => l.TagAddress).Distinct().ToList();
            Console.WriteLine($"3️⃣  Found {uniqueTags.Count} unique tags:");
            foreach (var tag in uniqueTags.Take(10))
            {
                Console.WriteLine($"   - {tag}");
            }
            if (uniqueTags.Count > 10)
            {
                Console.WriteLine($"   ... and {uniqueTags.Count - 10} more");
            }
            Console.WriteLine();

            // 4. PLCBackendService 생성 (최소 설정)
            Console.WriteLine("4️⃣  Creating minimal PLCBackendService for write...");

            var connectionConfig = new MxConnectionConfig
            {
                IpAddress = "192.168.9.120",
                Port = 4444,
                Name = "MitsubishiPLC",
                EnableScan = false,  // 스캔 비활성화 (쓰기만 수행)
                Timeout = TimeSpan.FromSeconds(5),
                ScanInterval = TimeSpan.FromMilliseconds(500),
                FrameType = FrameType.QnA_3E_Binary,
                Protocol = TransportProtocol.TCP,
                AccessRoute = new AccessRoute(
                    networkNumber: 0,
                    stationNumber: 255,
                    ioNumber: 1023,
                    relayType: 0
                ),
                MonitoringTimer = 16
            };

            // 빈 TagSpec 배열로 설정 (쓰기만 하므로 스캔 태그 불필요)
            var scanConfigs = new[]
            {
                new ScanConfiguration(connectionConfig, Array.Empty<TagSpec>())
            };

            var plcService = new PLCBackendService(
                scanConfigs: scanConfigs,
                tagHistoricWAL: FSharpOption<TagHistoricWAL>.None  // WAL 없이
            );

            Console.WriteLine($"   ✅ PLCBackendService created (write-only mode)");
            Console.WriteLine();

            // 5. 서비스 시작
            Console.WriteLine("5️⃣  Starting PLC service...");
            var disposable = plcService.Start();
            Console.WriteLine($"   ✅ Service started");
            Console.WriteLine();

            // 6. 사용자 확인
            Console.WriteLine("⚠️  This will write values to PLC at 192.168.9.120:4444");
            Console.WriteLine("   Press any key to start replay, or Ctrl+C to cancel...");
            Console.ReadKey();
            Console.WriteLine();

            // 7. 리플레이 시작
            Console.WriteLine("6️⃣  Starting replay...");
            Console.WriteLine();

            await ReplayLogsAsync("MitsubishiPLC", logs);

            Console.WriteLine();
            Console.WriteLine("✨ Replay completed successfully!");
            Console.WriteLine();

            // 8. 정리
            disposable?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"❌ Test failed with exception:");
            Console.WriteLine($"   Type: {ex.GetType().Name}");
            Console.WriteLine($"   Message: {ex.Message}");
            Console.WriteLine($"   Stack: {ex.StackTrace}");
            Console.WriteLine();
        }
    }

    private static async Task<List<LogEntry>> LoadLogsFromDatabaseAsync(string dbPath)
    {
        var connectionString = $"Data Source={dbPath};Mode=ReadOnly;";

        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        const string sql = @"
            SELECT
                l.id as Id,
                l.plcTagId as PlcTagId,
                l.dateTime as DateTime,
                l.value as Value,
                t.name as TagName,
                t.address as TagAddress,
                t.dataType as DataType
            FROM plcTagLog l
            INNER JOIN plcTag t ON l.plcTagId = t.id
            ORDER BY l.dateTime ASC";

        var logs = await connection.QueryAsync<LogEntry>(sql);
        return logs.ToList();
    }

    private static async Task ReplayLogsAsync(
        string connectionName,
        List<LogEntry> logs)
    {
        DateTime? previousTime = null;
        int successCount = 0;
        int failCount = 0;

        for (int i = 0; i < logs.Count; i++)
        {
            var log = logs[i];

            // 이전 로그와의 시간차 계산 및 대기
            if (previousTime.HasValue)
            {
                var delay = log.DateTime - previousTime.Value;
                if (delay.TotalMilliseconds > 0)
                {
                    await Task.Delay(delay);
                }
            }

            previousTime = log.DateTime;

            // 진행률 표시 (매 100개마다)
            if (i % 100 == 0 || i == logs.Count - 1)
            {
                var progress = (i + 1) * 100.0 / logs.Count;
                Console.Write($"\r   Progress: [{i + 1}/{logs.Count}] {progress:F1}% - {log.TagAddress} = {log.Value} ({log.DateTime:HH:mm:ss.fff})");
            }

            // PLC에 값 쓰기
            try
            {
                var plcValue = ConvertToPlcValue(log.Value, log.DataType);

                var tagSpec = new TagSpec(
                    name: log.TagName ?? log.TagAddress ?? "Unknown",
                    address: log.TagAddress ?? "",
                    dataType: plcValue.DataType,
                    walType: FSharpOption<Ev2.PLC.Common.TagSpecModule.WAL>.None,
                    comment: FSharpOption<string>.Some($"Replay from DB log ID {log.Id}"),
                    plcValue: FSharpOption<PlcValue>.None
                );

                var commInfo = CommunicationInfo.Create(
                    connectorName: connectionName,
                    tagSpec: tagSpec,
                    value: plcValue,
                    origin: FSharpOption<ValueSource>.Some(ValueSource.FromWebClient)
                );

                GlobalCommunication.SubjectC2S.OnNext(commInfo);

                successCount++;

                // 디버그: 처음 5개만 상세 로그
                if (i < 5)
                {
                    Console.WriteLine();
                    Console.WriteLine($"   ✅ Written: {log.TagAddress} = {log.Value}");
                }
            }
            catch (Exception ex)
            {
                failCount++;
                Console.WriteLine();
                Console.WriteLine($"   ❌ Exception writing tag '{log.TagAddress}': {ex.Message}");
                Console.WriteLine($"   Stopping replay due to exception");
                break;
            }
        }

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("7️⃣  Replay Summary:");
        Console.WriteLine($"   ✅ Success: {successCount:N0}");
        Console.WriteLine($"   ❌ Failed: {failCount:N0}");
        Console.WriteLine($"   📊 Success rate: {(successCount * 100.0 / logs.Count):F1}%");
    }

    private static PlcValue ConvertToPlcValue(string? value, string? dataType)
    {
        if (string.IsNullOrEmpty(value))
        {
            return PlcValue.NewBoolValue(false);
        }

        var dataTypeLower = (dataType ?? "").ToLowerInvariant();

        if (dataTypeLower.Contains("bool") || dataTypeLower.Contains("bit") || string.IsNullOrEmpty(dataType))
        {
            bool boolValue = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
            return PlcValue.NewBoolValue(boolValue);
        }

        if (dataTypeLower.Contains("int16") || dataTypeLower.Contains("int"))
        {
            if (short.TryParse(value, out var intValue))
            {
                return PlcValue.NewInt16Value(intValue);
            }
        }

        if (dataTypeLower.Contains("int32") || dataTypeLower.Contains("dint"))
        {
            if (int.TryParse(value, out var int32Value))
            {
                return PlcValue.NewInt32Value(int32Value);
            }
        }

        if (dataTypeLower.Contains("float") || dataTypeLower.Contains("real"))
        {
            if (float.TryParse(value, out var floatValue))
            {
                return PlcValue.NewFloat32Value(floatValue);
            }
        }

        return PlcValue.NewBoolValue(value == "1");
    }
}
