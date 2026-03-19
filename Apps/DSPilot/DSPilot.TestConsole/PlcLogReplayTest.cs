using Ev2.Backend.PLC;
using Ev2.Backend.Common;
using Microsoft.FSharp.Core;
using Dapper;
using Microsoft.Data.Sqlite;
using PlcDataType = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType;
using PlcValue = Ev2.PLC.Common.CoreDataTypesModule.PlcValue;
using TagSpec = Ev2.PLC.Common.TagSpecModule.TagSpec;

namespace DSPilot.TestConsole;

/// <summary>
/// 현장 로그 DB 기반 PLC 쓰기 리플레이 테스트
/// 실제 로그 타임스탬프 간격을 유지하면서 PLC에 순차적으로 값을 쓰기
/// </summary>
public static class PlcLogReplayTest
{
    /// <summary>
    /// 로그 엔트리 (DB에서 읽어온 데이터)
    /// </summary>
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

    public static async Task RunAsync(PLCBackendService plcService, string connectionName, string dbPath)
    {
        Console.WriteLine("=== PLC Log Replay Test ===");
        Console.WriteLine();

        try
        {
            // 1. DB 경로 확인
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

            // 4. 사용자 확인
            Console.WriteLine("⚠️  This will write values to PLC at 192.168.9.20:5555");
            Console.WriteLine("   Press any key to start replay, or Ctrl+C to cancel...");
            Console.ReadKey();
            Console.WriteLine();

            // 5. 리플레이 시작
            Console.WriteLine("4️⃣  Starting replay...");
            Console.WriteLine();

            await ReplayLogsAsync(plcService, connectionName, logs);

            Console.WriteLine();
            Console.WriteLine("✨ Replay completed successfully!");
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"❌ Replay failed with exception:");
            Console.WriteLine($"   Type: {ex.GetType().Name}");
            Console.WriteLine($"   Message: {ex.Message}");
            Console.WriteLine($"   Stack: {ex.StackTrace}");
            Console.WriteLine();
        }
    }

    /// <summary>
    /// DB에서 로그 데이터 로드
    /// </summary>
    private static async Task<List<LogEntry>> LoadLogsFromDatabaseAsync(string dbPath)
    {
        var connectionString = $"Data Source={dbPath};Mode=ReadOnly;";

        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        // plcTagLog와 plcTag를 조인하여 로드
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

    /// <summary>
    /// 로그를 실제 시간 간격으로 리플레이
    /// </summary>
    private static async Task ReplayLogsAsync(
        PLCBackendService plcService,
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

            // PLC에 값 쓰기 (GlobalCommunication.SubjectC2S 사용)
            try
            {
                // 1. PlcValue 생성
                var plcValue = ConvertToPlcValue(log.Value, log.DataType);

                // 2. TagSpec 생성 (address, name, dataType 필요)
                var tagSpec = new TagSpec(
                    name: log.TagName ?? log.TagAddress ?? "Unknown",
                    address: log.TagAddress ?? "",
                    dataType: plcValue.DataType,
                    walType: FSharpOption<Ev2.PLC.Common.TagSpecModule.WAL>.None,
                    comment: FSharpOption<string>.Some($"Replay from DB log ID {log.Id}"),
                    plcValue: FSharpOption<PlcValue>.None
                );

                // 3. CommunicationInfo 생성
                var commInfo = CommunicationInfo.Create(
                    connectorName: connectionName,
                    tagSpec: tagSpec,
                    value: plcValue,
                    origin: FSharpOption<ValueSource>.Some(ValueSource.FromWebClient)
                );

                // 4. GlobalCommunication.SubjectC2S에 전송
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
                Console.WriteLine($"   Stopping replay due to exception (error handling: immediate stop)");
                break;
            }
        }

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("5️⃣  Replay Summary:");
        Console.WriteLine($"   ✅ Success: {successCount:N0}");
        Console.WriteLine($"   ❌ Failed: {failCount:N0}");
        Console.WriteLine($"   📊 Success rate: {(successCount * 100.0 / logs.Count):F1}%");
    }

    /// <summary>
    /// 문자열 값을 PlcValue로 변환
    /// PlcValue는 F# discriminated union
    /// 현재는 Bool 타입만 처리 (대부분의 I/O 태그는 Bool)
    /// </summary>
    private static PlcValue ConvertToPlcValue(string? value, string? dataType)
    {
        if (string.IsNullOrEmpty(value))
        {
            return PlcValue.NewBoolValue(false);
        }

        // 데이터 타입 확인
        var dataTypeLower = (dataType ?? "").ToLowerInvariant();

        // Bool 타입 처리
        if (dataTypeLower.Contains("bool") || dataTypeLower.Contains("bit") || string.IsNullOrEmpty(dataType))
        {
            // "1" 또는 "true"면 true, 아니면 false
            bool boolValue = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
            return PlcValue.NewBoolValue(boolValue);
        }

        // Int 타입 처리
        if (dataTypeLower.Contains("int16") || dataTypeLower.Contains("int"))
        {
            if (short.TryParse(value, out var intValue))
            {
                return PlcValue.NewInt16Value(intValue);
            }
        }

        // Int32 타입 처리
        if (dataTypeLower.Contains("int32") || dataTypeLower.Contains("dint"))
        {
            if (int.TryParse(value, out var int32Value))
            {
                return PlcValue.NewInt32Value(int32Value);
            }
        }

        // Float 타입 처리
        if (dataTypeLower.Contains("float") || dataTypeLower.Contains("real"))
        {
            if (float.TryParse(value, out var floatValue))
            {
                return PlcValue.NewFloat32Value(floatValue);
            }
        }

        // 기본값: Bool로 처리 (값이 "1"이면 true)
        return PlcValue.NewBoolValue(value == "1");
    }
}
