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
        public int? PlcId { get; set; }  // 추가: PLC ID
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

            // 3. 고유 태그 목록 확인 및 TagSpec 생성
            var uniqueTags = logs
                .GroupBy(l => l.TagAddress)
                .Select(g => g.First())
                .ToList();

            Console.WriteLine($"3️⃣  Found {uniqueTags.Count} unique tags:");
            foreach (var tag in uniqueTags.Take(10))
            {
                Console.WriteLine($"   - {tag.TagName} @ {tag.TagAddress}");
            }
            if (uniqueTags.Count > 10)
            {
                Console.WriteLine($"   ... and {uniqueTags.Count - 10} more");
            }
            Console.WriteLine();

            // 4. DB 태그로 TagSpec 배열 생성
            Console.WriteLine("4️⃣  Creating TagSpecs from DB tags...");

            var tagSpecs = uniqueTags.Select(tag =>
            {
                var dataType = ConvertToPlcDataType(tag.DataType);
                return new TagSpec(
                    name: tag.TagName ?? tag.TagAddress ?? "Unknown",
                    address: tag.TagAddress ?? "",
                    dataType: dataType,
                    walType: FSharpOption<Ev2.PLC.Common.TagSpecModule.WAL>.None,
                    comment: FSharpOption<string>.Some($"From DB: {tag.TagName}"),
                    plcValue: FSharpOption<PlcValue>.None
                );
            }).ToArray();

            Console.WriteLine($"   ✅ Created {tagSpecs.Length} TagSpecs");
            Console.WriteLine();

            // 5. 동적으로 ScanConfiguration 생성 (DB 태그 포함)
            Console.WriteLine("5️⃣  Creating dynamic ScanConfiguration with DB tags...");

            var connectionConfig = new MxConnectionConfig
            {
                IpAddress = "192.168.9.120",
                Port = 4444,  // 실제 PLC 포트
                Name = "MitsubishiPLC",
                EnableScan = true,
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

            var scanConfigs = new[]
            {
                new ScanConfiguration(connectionConfig, tagSpecs)
            };

            Console.WriteLine($"   ✅ ScanConfiguration created with {tagSpecs.Length} tags");
            Console.WriteLine();

            // 6. PLCBackendService 생성 및 시작
            Console.WriteLine("6️⃣  Starting PLCBackendService...");

            IDisposable? disposable = null;
            try
            {
                var plcService = new PLCBackendService(
                    scanConfigs: scanConfigs,
                    tagHistoricWAL: FSharpOption<TagHistoricWAL>.None
                );

                disposable = plcService.Start();
                Console.WriteLine($"   ✅ PLCBackendService started");
                Console.WriteLine($"   ✅ Active Connections: {string.Join(", ", plcService.AllConnectionNames)}");
                Console.WriteLine();

                // 스캔 루프가 시작될 때까지 대기
                Console.WriteLine("   ⏳ Waiting 2 seconds for scan loop to start...");
                await Task.Delay(2000);
                Console.WriteLine();

                // 7. 사용자 확인
                Console.WriteLine("7️⃣  Ready to replay logs");
                Console.WriteLine($"⚠️  This will write {logs.Count:N0} values to PLC");
                Console.WriteLine("   Press any key to start replay, or Ctrl+C to cancel...");
                Console.ReadKey();
                Console.WriteLine();

                // 8. 무한 반복 리플레이 시작
                Console.WriteLine("8️⃣  Starting infinite replay loop...");
                Console.WriteLine("   Press Ctrl+C to stop");
                Console.WriteLine();

                int cycleCount = 0;
                while (true)
                {
                    cycleCount++;
                    Console.WriteLine($"🔄 Cycle {cycleCount} started");
                    Console.WriteLine();

                    await ReplayLogsAsync(plcService, logs, cycleCount);

                    Console.WriteLine();
                    Console.WriteLine($"✅ Cycle {cycleCount} completed. Restarting...");
                    Console.WriteLine();

                    // 사이클 간 짧은 대기
                    await Task.Delay(1000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ PLCBackendService failed: {ex.Message}");
                Console.WriteLine($"   Stack: {ex.StackTrace}");
            }
            finally
            {
                // 9. 정리
                disposable?.Dispose();
                Console.WriteLine("9️⃣  PLCBackendService disposed");
            }
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

        // 먼저 plcTag 테이블의 컬럼 확인
        var columns = await connection.QueryAsync<string>(
            "SELECT name FROM PRAGMA_TABLE_INFO('plcTag')");
        var columnList = columns.ToList();

        bool hasAddress = columnList.Contains("address");
        bool hasDataType = columnList.Contains("dataType");
        bool hasPlcId = columnList.Contains("plcId");

        // 동적으로 SQL 구성
        var addressCol = hasAddress ? "t.address" : "t.name";
        var dataTypeCol = hasDataType ? "t.dataType" : "'BOOL'";
        var plcIdCol = hasPlcId ? "t.plcId" : "NULL";

        var sql = $@"
            SELECT
                l.id as Id,
                l.plcTagId as PlcTagId,
                l.dateTime as DateTime,
                l.value as Value,
                t.name as TagName,
                {addressCol} as TagAddress,
                {dataTypeCol} as DataType,
                {plcIdCol} as PlcId
            FROM plcTagLog l
            INNER JOIN plcTag t ON l.plcTagId = t.id
            ORDER BY l.dateTime ASC";

        var logs = await connection.QueryAsync<LogEntry>(sql);
        return logs.ToList();
    }

    private static async Task ReplayLogsAsync(
        PLCBackendService plcService,
        List<LogEntry> logs,
        int cycleNumber)
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
                Console.Write($"\r   Cycle {cycleNumber} - Progress: [{i + 1}/{logs.Count}] {progress:F1}% - {log.TagAddress} = {log.Value}");
            }

            // PLC에 값 쓰기 (WinForm TestApp 방식)
            try
            {
                // 연결 이름 찾기 (첫 번째 연결 사용)
                var connectionName = plcService.AllConnectionNames.FirstOrDefault();
                if (connectionName == null)
                {
                    Console.WriteLine($"   ❌ No active connections found");
                    break;
                }

                // TagSpec 가져오기 (실제 스캔 설정에서)
                var tagName = log.TagName ?? log.TagAddress ?? "Unknown";
                var tagSpecOpt = plcService.TryGetTagSpec(connectionName, tagName);

                TagSpec tagSpec;
                if (FSharpOption<TagSpec>.get_IsSome(tagSpecOpt))
                {
                    // 실제 TagSpec 사용
                    tagSpec = tagSpecOpt.Value;
                }
                else
                {
                    // TagSpec이 없으면 생성 (동적 태그)
                    var dataType = ConvertToPlcDataType(log.DataType);
                    tagSpec = new TagSpec(
                        name: tagName,
                        address: log.TagAddress ?? tagName,
                        dataType: dataType,
                        walType: FSharpOption<Ev2.PLC.Common.TagSpecModule.WAL>.None,
                        comment: FSharpOption<string>.Some($"Dynamic tag from DB log"),
                        plcValue: FSharpOption<PlcValue>.None
                    );
                }

                // PlcValue.TryParse 사용 (WinForm TestApp 방식)
                var plcValueOpt = PlcValue.TryParse(log.Value ?? "0", tagSpec.DataType);
                if (FSharpOption<PlcValue>.get_IsNone(plcValueOpt))
                {
                    Console.WriteLine();
                    Console.WriteLine($"   ⚠️  Cannot parse '{log.Value}' as {tagSpec.DataType}");
                    failCount++;
                    continue;
                }

                // CommunicationInfo 생성 및 전송
                var commInfo = CommunicationInfo.Create(
                    connectorName: connectionName,
                    tagSpec: tagSpec,
                    value: plcValueOpt.Value,
                    origin: FSharpOption<ValueSource>.Some(ValueSource.FromWebClient)
                );

                GlobalCommunication.SubjectC2S.OnNext(commInfo);

                successCount++;

                // 디버그: 모든 쓰기 표시
                {
                    Console.WriteLine();
                    Console.WriteLine($"   ✅ Written: {connectionName}/{tagName} @ {tagSpec.Address} = {log.Value}");
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

    /// <summary>
    /// 문자열 데이터 타입을 PlcDataType으로 변환
    /// </summary>
    private static PlcDataType ConvertToPlcDataType(string? dataType)
    {
        if (string.IsNullOrEmpty(dataType))
        {
            return PlcDataType.Bool;
        }

        var dataTypeLower = dataType.ToLowerInvariant();

        if (dataTypeLower.Contains("bool") || dataTypeLower.Contains("bit"))
            return PlcDataType.Bool;

        if (dataTypeLower.Contains("int8") || dataTypeLower.Contains("byte"))
            return PlcDataType.Int8;

        if (dataTypeLower.Contains("uint8"))
            return PlcDataType.UInt8;

        if (dataTypeLower.Contains("int16") || dataTypeLower.Contains("short"))
            return PlcDataType.Int16;

        if (dataTypeLower.Contains("uint16") || dataTypeLower.Contains("word"))
            return PlcDataType.UInt16;

        if (dataTypeLower.Contains("int32") || dataTypeLower.Contains("dint") || dataTypeLower.Contains("int"))
            return PlcDataType.Int32;

        if (dataTypeLower.Contains("uint32") || dataTypeLower.Contains("dword"))
            return PlcDataType.UInt32;

        if (dataTypeLower.Contains("int64") || dataTypeLower.Contains("lint"))
            return PlcDataType.Int64;

        if (dataTypeLower.Contains("uint64") || dataTypeLower.Contains("lword"))
            return PlcDataType.UInt64;

        if (dataTypeLower.Contains("float") || dataTypeLower.Contains("real"))
            return PlcDataType.Float32;

        if (dataTypeLower.Contains("double") || dataTypeLower.Contains("lreal"))
            return PlcDataType.Float64;

        if (dataTypeLower.Contains("string"))
            return PlcDataType.NewString(255);  // String은 길이 지정 필요

        // 기본값
        return PlcDataType.Bool;
    }
}
