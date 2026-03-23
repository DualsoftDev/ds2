using Dapper;
using Ev2.Backend.Common;
using Ev2.Backend.PLC;
using Ev2.PLC.Protocol.MX;
using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Core;
using static Ev2.PLC.Common.TagSpecModule;
using PlcDataType = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType;
using PlcValue = Ev2.PLC.Common.CoreDataTypesModule.PlcValue;
using TagSpec = Ev2.PLC.Common.TagSpecModule.TagSpec;

namespace DSPilot.TestConsole;

/// <summary>
/// Replay Mode: DB → PLC
/// DB 로그를 읽어서 PLC에 타임스탬프 간격으로 리플레이
/// </summary>
public static class ReplayMode
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
        public int? PlcId { get; set; }
    }

    public static async Task RunAsync(string dbPath)
    {
        Console.WriteLine("=== Replay Mode: DB → PLC ===");
        Console.WriteLine();

        try
        {
            // 1. DB 로드
            var fullPath = Path.GetFullPath(dbPath);
            Console.WriteLine($"1️⃣  Loading DB: {fullPath}");

            if (!File.Exists(fullPath))
            {
                Console.WriteLine($"   ❌ File not found");
                return;
            }

            Console.WriteLine($"   ✅ DB found");

            // 2. 시작 위치 선택
            Console.WriteLine();
            Console.Write("Start from? (beginning/middle/[M]iddle by default): ");
            var startOption = Console.ReadLine()?.Trim().ToLower();
            bool startFromMiddle = string.IsNullOrEmpty(startOption) || startOption == "m" || startOption == "middle";

            // 3. 로그 데이터 로드
            Console.WriteLine("2️⃣  Loading logs...");
            var logs = await LoadLogsAsync(fullPath, startFromMiddle);

            if (logs.Count == 0)
            {
                Console.WriteLine("   ❌ No logs found");
                return;
            }

            Console.WriteLine($"   ✅ {logs.Count:N0} logs");
            Console.WriteLine($"   📅 {logs.First().DateTime:HH:mm:ss} ~ {logs.Last().DateTime:HH:mm:ss}");
            if (startFromMiddle)
            {
                Console.WriteLine($"   ℹ️  Starting from middle (50%)");
            }

            // 3. TagSpec 생성
            Console.WriteLine("3️⃣  Creating TagSpecs...");
            var uniqueTags = logs.GroupBy(l => l.TagAddress).Select(g => g.First()).ToList();

            TagSpec[] tagSpecs;
            try
            {
                tagSpecs = uniqueTags.Select(tag => new TagSpec(
                    name: tag.TagName ?? tag.TagAddress ?? "Unknown",
                    address: tag.TagAddress ?? "",
                    dataType: ConvertDataType(tag.DataType),
                    walType: FSharpOption<Ev2.PLC.Common.TagSpecModule.WAL>.None,
                    comment: FSharpOption<string>.None, // Simplified: None instead of Some
                    everyNScan: FSharpOption<int>.None,
                    directionHint: FSharpOption<DirectionHint>.None,
                    plcValue: FSharpOption<PlcValue>.None
                )).ToArray();

                Console.WriteLine($"   ✅ {tagSpecs.Length} tags");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ TagSpec creation failed: {ex.Message}");
                Console.WriteLine($"   Type: {ex.GetType().Name}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"   Inner: {ex.InnerException.Message}");
                }
                Console.WriteLine($"   Stack: {ex.StackTrace}");
                return;
            }

            // 4. PLC 연결 설정
            Console.WriteLine("4️⃣  Configuring PLC connection...");
            var connectionConfig = new MxConnectionConfig
            {
                IpAddress = "192.168.9.120",
                Port = 5555,
                Name = "MitsubishiPLC",
                EnableScan = true,
                Timeout = TimeSpan.FromSeconds(5),
                ScanInterval = TimeSpan.FromMilliseconds(500),
                FrameType = FrameType.QnA_3E_Binary,
                Protocol = TransportProtocol.UDP,
                AccessRoute = new AccessRoute(0, 255, 1023, 0),
                MonitoringTimer = 16
            };

            var scanConfigs = new[] { new ScanConfiguration(connectionConfig, tagSpecs) };
            Console.WriteLine($"   ✅ Config ready");

            // 5. PLC 서비스 시작
            Console.WriteLine("5️⃣  Starting PLC service...");
            IDisposable? disposable = null;

            try
            {
                var plcService = new PLCBackendService(
                    scanConfigs: scanConfigs,
                    tagHistoricWAL: FSharpOption<TagHistoricWAL>.None
                );

                disposable = plcService.Start();
                Console.WriteLine($"   ✅ Connected: {string.Join(", ", plcService.AllConnectionNames)}");

                await Task.Delay(2000); // PLC 스캔 루프 시작 대기

                // 6. 리플레이 확인
                Console.WriteLine();
                Console.WriteLine($"6️⃣  Ready to replay {logs.Count:N0} values");
                Console.WriteLine("   Press any key to start, Ctrl+C to cancel...");
                Console.ReadKey();
                Console.WriteLine();

                // 7. 무한 반복 리플레이
                Console.WriteLine("7️⃣  Replaying (infinite loop)...");
                Console.WriteLine("   Press Ctrl+C to stop");
                Console.WriteLine();

                int cycle = 0;
                while (true)
                {
                    cycle++;
                    Console.WriteLine($"🔄 Cycle {cycle}");
                    await ReplayLogsAsync(plcService, logs, cycle);
                    Console.WriteLine($"✅ Cycle {cycle} done");
                    Console.WriteLine();
                    await Task.Delay(1000);
                }
            }
            finally
            {
                disposable?.Dispose();
                Console.WriteLine("🛑 PLC service stopped");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}");
        }
    }

    private static async Task<List<LogEntry>> LoadLogsAsync(string dbPath, bool startFromMiddle = true)
    {
        var connStr = $"Data Source={dbPath};Mode=ReadOnly;";
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();

        // 스키마 확인
        var columns = await conn.QueryAsync<string>("SELECT name FROM PRAGMA_TABLE_INFO('plcTag')");
        var cols = columns.ToList();

        var addrCol = cols.Contains("address") ? "t.address" : "t.name";
        var typeCol = cols.Contains("dataType") ? "t.dataType" : "'BOOL'";
        var plcIdCol = cols.Contains("plcId") ? "t.plcId" : "NULL";

        // 전체 로그 수 조회
        var totalCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM plcTagLog");

        // 시작 위치 계산
        int offset = startFromMiddle ? totalCount / 2 : 0;

        var sql = $@"
            SELECT l.id, l.plcTagId, l.dateTime, l.value,
                   t.name as TagName,
                   {addrCol} as TagAddress,
                   {typeCol} as DataType,
                   {plcIdCol} as PlcId
            FROM plcTagLog l
            INNER JOIN plcTag t ON l.plcTagId = t.id
            ORDER BY l.dateTime ASC
            LIMIT -1 OFFSET {offset}";

        var logs = await conn.QueryAsync<LogEntry>(sql);
        return logs.ToList();
    }

    private static async Task ReplayLogsAsync(PLCBackendService plcService, List<LogEntry> logs, int cycle)
    {
        DateTime? prevTime = null;
        int success = 0, fail = 0;
        var connName = plcService.AllConnectionNames.FirstOrDefault();

        if (connName == null)
        {
            Console.WriteLine("   ❌ No connection");
            return;
        }

        for (int i = 0; i < logs.Count; i++)
        {
            var log = logs[i];

            // 타임스탬프 간격 유지
            if (prevTime.HasValue)
            {
                var delay = log.DateTime - prevTime.Value;
                if (delay.TotalMilliseconds > 0)
                    await Task.Delay(delay);
            }
            prevTime = log.DateTime;

            // 진행률 표시 (매 100개마다)
            if (i % 100 == 0 || i == logs.Count - 1)
            {
                var pct = (i + 1) * 100.0 / logs.Count;
                Console.Write($"\r   [{i + 1}/{logs.Count}] {pct:F1}% - {log.TagAddress} = {log.Value}");
            }

            // PLC 쓰기
            try
            {
                var tagName = log.TagName ?? log.TagAddress ?? "Unknown";
                var tagSpecOpt = plcService.TryGetTagSpec(connName, tagName);

                TagSpec tagSpec;
                if (FSharpOption<TagSpec>.get_IsSome(tagSpecOpt))
                {
                    tagSpec = tagSpecOpt.Value;
                }
                else
                {
                    // 동적 TagSpec 생성
                    tagSpec = new TagSpec(
                        name: tagName,
                        address: log.TagAddress ?? tagName,
                        dataType: ConvertDataType(log.DataType),
                        walType: FSharpOption<Ev2.PLC.Common.TagSpecModule.WAL>.None,
                        comment: FSharpOption<string>.Some("Dynamic"),
                        everyNScan: FSharpOption<int>.None,
                        directionHint: FSharpOption<DirectionHint>.None,
                        plcValue: FSharpOption<PlcValue>.None
                    );
                }

                // 값 파싱
                var valueOpt = PlcValue.TryParse(log.Value ?? "0", tagSpec.DataType);
                if (FSharpOption<PlcValue>.get_IsNone(valueOpt))
                {
                    fail++;
                    continue;
                }

                // SubjectC2S 전송
                var commInfo = CommunicationInfo.Create(
                    connectorName: connName,
                    tagSpec: tagSpec,
                    value: valueOpt.Value,
                    origin: FSharpOption<ValueSource>.Some(ValueSource.FromWebClient)
                );
                GlobalCommunication.SubjectC2S.OnNext(commInfo);

                success++;
            }
            catch
            {
                fail++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"   ✅ {success:N0} | ❌ {fail:N0}");
    }

    private static PlcDataType ConvertDataType(string? dataType)
    {
        if (string.IsNullOrEmpty(dataType)) return PlcDataType.Bool;

        var dt = dataType.ToLowerInvariant();
        if (dt.Contains("bool") || dt.Contains("bit")) return PlcDataType.Bool;
        if (dt.Contains("int16") || dt.Contains("short")) return PlcDataType.Int16;
        if (dt.Contains("uint16") || dt.Contains("word")) return PlcDataType.UInt16;
        if (dt.Contains("int32") || dt.Contains("dint")) return PlcDataType.Int32;
        if (dt.Contains("uint32") || dt.Contains("dword")) return PlcDataType.UInt32;
        if (dt.Contains("int64") || dt.Contains("lint")) return PlcDataType.Int64;
        if (dt.Contains("uint64") || dt.Contains("lword")) return PlcDataType.UInt64;
        if (dt.Contains("float") || dt.Contains("real")) return PlcDataType.Float32;
        if (dt.Contains("double") || dt.Contains("lreal")) return PlcDataType.Float64;
        if (dt.Contains("string")) return PlcDataType.NewString(255);

        return PlcDataType.Bool;
    }
}
