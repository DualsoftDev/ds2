using Ds2.Core;
using Ds2.UI.Core;
using Dual.Common.Db.FS;
using Ev2.Backend.Common;
using Ev2.Backend.PLC;
using Ev2.PLC.Protocol.MX;
using log4net;
using log4net.Config;
using Microsoft.FSharp.Core;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using static Ev2.PLC.Common.TagSpecModule;
using DbProvider = Dual.Common.Db.FS.DbProviderModule.DbProvider;
using PlcDataType = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType;
using PlcValue = Ev2.PLC.Common.CoreDataTypesModule.PlcValue;
using TagSpec = Ev2.PLC.Common.TagSpecModule.TagSpec;
using WAL = Ev2.PLC.Common.TagSpecModule.WAL;

namespace DSPilot.TestConsole;

/// <summary>
/// Capture Mode: AASX → PLC TagSpecs → DB + Events
/// AASX 파일에서 태그 정보를 읽어서 자동으로 TagSpec 생성 및 수집
/// </summary>
public static class CaptureMode
{
    private static IDisposable? _c2sSubscription;
    private static IDisposable? _serviceDisposable;

    public static async Task RunAsync()
    {
        Console.WriteLine("=== Capture Mode: AASX → PLC → DB + Events ===");
        Console.WriteLine();

        try
        {
            // 1. AASX 파일 경로 입력
            Console.Write("Enter AASX path (default: C:/ds/ds2/Apps/DSPilot/DsCSV_0318_C.aasx): ");
            var aasxPath = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(aasxPath))
            {
                aasxPath = "C:/ds/ds2/Apps/DSPilot/DsCSV_0318_C.aasx";
            }

            var fullPath = Path.GetFullPath(aasxPath);
            Console.WriteLine($"1️⃣  Loading AASX: {fullPath}");

            if (!File.Exists(fullPath))
            {
                Console.WriteLine($"   ❌ File not found");
                return;
            }

            // 2. AASX 파일 로드
            var store = new DsStore();
            var loadResult = Ds2.Aasx.AasxImporter.importIntoStore(store, fullPath);

            if (!loadResult)
            {
                Console.WriteLine("   ❌ Failed to load AASX");
                return;
            }

            Console.WriteLine("   ✅ AASX loaded");

            // 3. 프로젝트 정보 추출
            var projects = DsQuery.allProjects(store);
            var project = Microsoft.FSharp.Collections.ListModule.IsEmpty(projects)
                ? null
                : Microsoft.FSharp.Collections.ListModule.Head(projects);

            if (project == null)
            {
                Console.WriteLine("   ❌ No project found in AASX");
                return;
            }

            Console.WriteLine($"   📦 Project: {project.Name}");

            // 4. PLC 태그 정보 추출
            Console.WriteLine("2️⃣  Extracting PLC tags from AASX...");
            var plcTags = ExtractPlcTagsFromProject(project, store);

            if (plcTags.Count == 0)
            {
                Console.WriteLine("   ❌ No PLC tags found");
                return;
            }

            Console.WriteLine($"   ✅ {plcTags.Count} tags extracted");
            foreach (var tag in plcTags.Take(5))
            {
                Console.WriteLine($"      - {tag.Name} @ {tag.Address} ({tag.DataType})");
            }
            if (plcTags.Count > 5)
            {
                Console.WriteLine($"      ... and {plcTags.Count - 5} more");
            }

            // 5. TagSpec 생성
            Console.WriteLine("3️⃣  Creating TagSpecs...");
            var tagSpecs = CreateTagSpecs(plcTags);
            Console.WriteLine($"   ✅ {tagSpecs.Length} TagSpecs created");

            // 6. BackendAppSettings 생성
            Console.WriteLine("4️⃣  Creating BackendAppSettings...");
            var dbPath = "C:/ds/ds2/Apps/DSPilot/DSPilot/sample/db/dsdb_capture.sqlite3";
            var appSettings = CreateBackendAppSettings(tagSpecs, dbPath);
            Console.WriteLine($"   ✅ AppSettings configured");
            Console.WriteLine($"      DB: {dbPath}");

            // 7. log4net 초기화
            Console.WriteLine("5️⃣  Initializing log4net...");
            var logRepository = LogManager.GetRepository(Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly());
            XmlConfigurator.Configure(logRepository, new FileInfo("log4net.config"));
            var log = LogManager.GetLogger("AppLogger");
            Console.WriteLine("   ✅ log4net ready");

            // 8. SubjectC2S 구독
            Console.WriteLine("6️⃣  Subscribing to SubjectC2S...");
            long updateCount = 0;
            var startTime = DateTime.Now;

            _c2sSubscription = GlobalCommunication.SubjectC2S.Subscribe(info =>
            {
                if (info.Tags == null || info.Tags.Length == 0) return;

                Interlocked.Add(ref updateCount, info.Tags.Length);

                var timeStr = info.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
                foreach (var (tagSpec, value) in info.Tags)
                {
                    var valueStr = value.GetValue()?.ToString() ?? "";

                    // 매 50번째 업데이트만 표시
                    if (updateCount % 50 == 0)
                    {
                        Console.WriteLine($"📊 [{timeStr}] {info.ConnectorName}/{tagSpec.Name} = {valueStr}");
                    }
                }
            });
            Console.WriteLine("   ✅ SubjectC2S subscribed");

            // 9. EV2 Core 초기화 및 PLCBackendService 시작
            Console.WriteLine("7️⃣  Starting EV2 services...");
            var plcService = StartEv2Services(appSettings, log);
            Console.WriteLine("   ✅ PLCBackendService started");

            var connections = plcService.AllConnectionNames;
            if (connections.Any())
            {
                Console.WriteLine($"   ✅ Active Connections: {string.Join(", ", connections)}");
            }
            else
            {
                Console.WriteLine("   ⚠️  No active connections");
            }

            await Task.Delay(2000); // PLC 스캔 시작 대기

            // 10. 모니터링
            Console.WriteLine();
            Console.WriteLine("8️⃣  Monitoring PLC events...");
            Console.WriteLine("   📊 Displaying every 50th event");
            Console.WriteLine("   💾 WAL automatically flushes to DB");
            Console.WriteLine("   Press Ctrl+C to stop");
            Console.WriteLine();

            // 통계 표시 루프
            var statsCts = new CancellationTokenSource();
            var statsTask = Task.Run(async () =>
            {
                while (!statsCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(5000, statsCts.Token);

                    var elapsed = DateTime.Now - startTime;
                    var rate = updateCount / elapsed.TotalSeconds;

                    Console.WriteLine($"📈 Stats: {updateCount:N0} updates | {elapsed:hh\\:mm\\:ss} elapsed | {rate:F1} updates/sec");
                }
            }, statsCts.Token);

            // Ctrl+C 대기
            var tcs = new TaskCompletionSource<bool>();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                tcs.TrySetResult(true);
            };

            await tcs.Task;

            // 정리
            Console.WriteLine();
            Console.WriteLine("🛑 Stopping...");
            statsCts.Cancel();

            try { await statsTask; } catch { }

            _c2sSubscription?.Dispose();
            _serviceDisposable?.Dispose();

            Console.WriteLine("✅ Stopped cleanly");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}");
            Console.WriteLine($"   Type: {ex.GetType().Name}");

            if (ex.InnerException != null)
            {
                Console.WriteLine($"   Inner: {ex.InnerException.Message}");
            }
        }
        finally
        {
            _c2sSubscription?.Dispose();
            _serviceDisposable?.Dispose();
        }
    }

    private class PlcTagInfo
    {
        public string Name { get; set; } = "";
        public string Address { get; set; } = "";
        public string DataType { get; set; } = "";
    }

    private static TagSpec[] CreateTagSpecs(List<PlcTagInfo> tags)
    {
        var duplicatedNames = tags
            .GroupBy(tag => tag.Name)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.Select(tag => tag.Address).OrderBy(address => address).ToArray());

        foreach (var duplicate in duplicatedNames)
        {
            Console.WriteLine(
                $"   ⚠️  Duplicate PLC tag name detected: '{duplicate.Key}' @ [{string.Join(", ", duplicate.Value)}] -> EV2 tag name will include address");
        }

        return tags.Select(tag =>
        {
            var originalName = string.IsNullOrWhiteSpace(tag.Name) ? tag.Address : tag.Name;
            var isDuplicated = !string.IsNullOrWhiteSpace(tag.Name) && duplicatedNames.ContainsKey(tag.Name);
            var ev2TagName = isDuplicated ? $"{originalName} [{tag.Address}]" : originalName;
            var comment = isDuplicated
                ? $"Auto-generated from AASX | OriginalName={originalName} | Address={tag.Address}"
                : "Auto-generated from AASX";

            return new TagSpec(
                name: ev2TagName,
                address: tag.Address,
                dataType: ConvertToPlcDataType(tag.DataType),
                walType: FSharpOption<WAL>.Some(WAL.Memory),
                comment: FSharpOption<string>.Some(comment),
                everyNScan: FSharpOption<int>.None,
                directionHint: FSharpOption<DirectionHint>.None,
                plcValue: FSharpOption<PlcValue>.None
            );
        }).ToArray();
    }

    private static PlcDataType ConvertToPlcDataType(string dataType)
    {
        if (string.IsNullOrEmpty(dataType)) return PlcDataType.Bool;

        var dt = dataType.ToUpperInvariant();
        if (dt.Contains("BOOL") || dt.Contains("BIT")) return PlcDataType.Bool;
        if (dt.Contains("INT16") || dt.Contains("SHORT")) return PlcDataType.Int16;
        if (dt.Contains("UINT16") || dt.Contains("WORD")) return PlcDataType.UInt16;
        if (dt.Contains("INT32") || dt.Contains("DINT")) return PlcDataType.Int32;
        if (dt.Contains("UINT32") || dt.Contains("DWORD")) return PlcDataType.UInt32;
        if (dt.Contains("INT64") || dt.Contains("LINT")) return PlcDataType.Int64;
        if (dt.Contains("UINT64") || dt.Contains("LWORD")) return PlcDataType.UInt64;
        if (dt.Contains("FLOAT32") || dt.Contains("REAL")) return PlcDataType.Float32;
        if (dt.Contains("FLOAT64") || dt.Contains("LREAL")) return PlcDataType.Float64;
        if (dt.Contains("STRING")) return PlcDataType.NewString(255);

        return PlcDataType.Bool;
    }

    private static BackendAppSettings CreateBackendAppSettings(TagSpec[] tagSpecs, string dbPath)
    {
        var appSettings = new BackendAppSettings();

        // DB 설정 - ConnectionString은 PascalCase 키 사용
        var connectionString = $"Data Source={dbPath};Version=3;BusyTimeout=20000";
        appSettings.DbProvider = DbProvider.NewSqlite(connectionString);

        // TagHistoric 설정
        appSettings.TagHistoric = new TagHistoricSettings
        {
            WALBufferSize = 1000,
            FlushInterval = TimeSpan.FromSeconds(5)
        };

        // ScanConfiguration 설정
        var connectionConfig = new MxConnectionConfig
        {
            IpAddress = PlcDefaults.IpAddress,
            Port = PlcDefaults.Port,
            Name = PlcDefaults.Name,
            EnableScan = true,
            Timeout = TimeSpan.FromSeconds(5),
            ScanInterval = TimeSpan.FromMilliseconds(500),
            FrameType = Ev2.PLC.Protocol.MX.FrameType.QnA_3E_Binary,
            Protocol = Ev2.PLC.Protocol.MX.TransportProtocol.UDP,
            AccessRoute = new Ev2.PLC.Protocol.MX.AccessRoute(0, 255, 1023, 0),
            MonitoringTimer = 16
        };

        appSettings.ScanConfigurations = new[]
        {
            new ScanConfiguration
            {
                Connection = connectionConfig,
                TagSpecs = tagSpecs
            }
        };

        return appSettings;
    }

    private static PLCBackendService StartEv2Services(BackendAppSettings appSettings, ILog log)
    {
        // EV2 Core 초기화
        Ev2.PLC.Common.ModuleInitializer.Initialize(log);
        Ev2.PLC.Protocol.AB.ModuleInitializer.Initialize(log);
        Ev2.PLC.Protocol.MX.ModuleInitializer.Initialize(log);
        Ev2.PLC.Protocol.S7.ModuleInitializer.Initialize(log);
        Ev2.Core.FS.ModuleInitializer.Initialize(log, appSettings);

        // AppDbApi 생성 (DB 자동 생성)
        if (appSettings.DbProvider != null)
        {
            new Ev2.Core.FS.AppDbApi(appSettings.DbProvider);
            Console.WriteLine("      AppDbApi created");
        }

        // TagHistoricWAL 생성
        TagHistoricWAL? wal = null;
        if (appSettings.DbProvider != null)
        {
            var th = appSettings.TagHistoric;
            var walPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DualSoft", "EV2", "tag-historic.wal.jsonl");

            var walDir = Path.GetDirectoryName(walPath);
            if (!string.IsNullOrEmpty(walDir) && !Directory.Exists(walDir))
            {
                Directory.CreateDirectory(walDir);
            }

            var memoryBuffer = new MemoryWalBuffer();
            var diskBuffer = new FileWalBuffer(walPath);
            wal = new TagHistoricWAL(th.WALBufferSize, th.FlushInterval, memoryBuffer, diskBuffer);

            // 잔여 WAL flush
            var diskBuf = diskBuffer as IWalBuffer;
            if (diskBuf.Count > 0)
            {
                Console.WriteLine($"      Flushing {diskBuf.Count} residual WAL entries...");
                wal.Flush();
            }

            wal.InsertRestartMarker();
            wal.SyncWalTypesFromConfig(appSettings.ScanConfigurations);
            wal.LoadLastTagValues();
            Console.WriteLine("      TagHistoricWAL created");
        }

        // PLCBackendService 생성 및 시작
        var walOption = wal != null
            ? FSharpOption<TagHistoricWAL>.Some(wal)
            : FSharpOption<TagHistoricWAL>.None;

        var plcService = new PLCBackendService(
            appSettings.ScanConfigurations,
            walOption);

        _serviceDisposable = plcService.Start();

        return plcService;
    }

    private static List<PlcTagInfo> ExtractPlcTagsFromProject(Project project, DsStore store)
    {
        Console.WriteLine($"      AASX loaded: {project.Name}");
        Console.WriteLine($"      Extracting tags from ApiCalls and HwComponents...");

        // DsStore 확장 메서드 사용 (GetCallIOTags + GetHwComponentIOTags)
        var callIOTags = store.GetCallIOTags();
        var hwIOTags = store.GetHwComponentIOTags();

        var allPlcTags = Microsoft.FSharp.Collections.ListModule.ToArray(callIOTags)
            .Concat(Microsoft.FSharp.Collections.ListModule.ToArray(hwIOTags))
            .Where(ioTag => !string.IsNullOrEmpty(ioTag.Address))
            .Select(ioTag => new PlcTagInfo
            {
                Name = ioTag.Name,
                Address = ioTag.Address,
                DataType = ExtractDataTypeFromIOTag(ioTag) // Extract from Description or default to BOOL
            })
            .DistinctBy(t => t.Address)
            .ToList();

        Console.WriteLine($"      Extracted {allPlcTags.Count} unique tags");
        return allPlcTags;
    }

    private static string ExtractDataTypeFromIOTag(Ds2.Core.IOTag ioTag)
    {
        // Description에서 데이터 타입 추출 시도
        if (!string.IsNullOrEmpty(ioTag.Description))
        {
            var desc = ioTag.Description.ToLowerInvariant();
            if (desc.Contains("int16") || desc.Contains("short")) return "INT16";
            if (desc.Contains("uint16") || desc.Contains("word")) return "UINT16";
            if (desc.Contains("int32") || desc.Contains("dint")) return "INT32";
            if (desc.Contains("uint32") || desc.Contains("dword")) return "UINT32";
            if (desc.Contains("int64") || desc.Contains("lint")) return "INT64";
            if (desc.Contains("uint64") || desc.Contains("lword")) return "UINT64";
            if (desc.Contains("float") || desc.Contains("real")) return "FLOAT32";
            if (desc.Contains("double") || desc.Contains("lreal")) return "FLOAT64";
            if (desc.Contains("bool") || desc.Contains("bit")) return "BOOL";
            if (desc.Contains("string")) return "STRING";
        }

        // Address 패턴으로 타입 추정
        if (!string.IsNullOrEmpty(ioTag.Address))
        {
            var addr = ioTag.Address.ToUpperInvariant();
            if (addr.StartsWith("M") || addr.StartsWith("X") || addr.StartsWith("Y")) return "BOOL";
            if (addr.StartsWith("D")) return "INT16";
            if (addr.StartsWith("W")) return "UINT16";
        }

        // 기본값
        return "BOOL";
    }

}
