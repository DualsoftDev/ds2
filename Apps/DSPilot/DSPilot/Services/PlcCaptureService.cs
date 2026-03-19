using Ds2.Core;
using Ds2.UI.Core;
using Ev2.Backend.PLC;
using Ev2.Backend.Common;
using Ev2.PLC.Protocol.MX;
using Dual.Common.Db.FS;
using log4net;
using log4net.Config;
using System.Reactive.Linq;
using System.Reflection;
using Microsoft.FSharp.Core;
using PlcDataType = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType;
using PlcValue = Ev2.PLC.Common.CoreDataTypesModule.PlcValue;
using TagSpec = Ev2.PLC.Common.TagSpecModule.TagSpec;
using WAL = Ev2.PLC.Common.TagSpecModule.WAL;
using DbProvider = Dual.Common.Db.FS.DbProviderModule.DbProvider;

namespace DSPilot.Services;

/// <summary>
/// PLC 데이터 수집 백그라운드 서비스
/// DsStore에서 IOTag를 추출하여 실시간 PLC 데이터를 DB에 저장
/// </summary>
public class PlcCaptureService : IHostedService, IDisposable
{
    private readonly DsProjectService _projectService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlcCaptureService> _logger;
    private IDisposable? _c2sSubscription;
    private IDisposable? _serviceDisposable;
    private ILog? _log4netLogger;

    private class PlcTagInfo
    {
        public string Name { get; set; } = "";
        public string Address { get; set; } = "";
        public string DataType { get; set; } = "";
    }

    public PlcCaptureService(
        DsProjectService projectService,
        IConfiguration configuration,
        ILogger<PlcCaptureService> logger)
    {
        _projectService = projectService;
        _configuration = configuration;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("PlcCaptureService starting...");

            // log4net 초기화
            InitializeLog4Net();

            if (_log4netLogger == null)
            {
                _logger.LogError("Failed to initialize log4net");
                return Task.CompletedTask;
            }

            // DsStore 확인
            if (!_projectService.IsLoaded)
            {
                _logger.LogWarning("DsStore is not loaded. PlcCapture cannot start.");
                _log4netLogger.Warn("DsStore is not loaded. Waiting for project to load...");
                return Task.CompletedTask;
            }

            _log4netLogger.Info("=== Starting PLC Capture Mode ===");

            // 1. PLC 태그 추출
            _log4netLogger.Info("Extracting PLC tags from DsStore...");
            var store = _projectService.GetStore();
            var plcTags = ExtractPlcTags(store);
            _log4netLogger.InfoFormat("Extracted {0} unique tags", plcTags.Count);

            if (plcTags.Count == 0)
            {
                _log4netLogger.Warn("No PLC tags found in DsStore");
                return Task.CompletedTask;
            }

            // 상위 5개 태그 로깅
            foreach (var tag in plcTags.Take(5))
            {
                _log4netLogger.InfoFormat("  - {0} @ {1} ({2})", tag.Name, tag.Address, tag.DataType);
            }
            if (plcTags.Count > 5)
            {
                _log4netLogger.InfoFormat("  ... and {0} more", plcTags.Count - 5);
            }

            // 2. TagSpec 생성
            _log4netLogger.Info("Creating TagSpecs...");
            var tagSpecs = CreateTagSpecs(plcTags);
            _log4netLogger.InfoFormat("Created {0} TagSpecs", tagSpecs.Length);

            // 3. BackendAppSettings 생성
            _log4netLogger.Info("Creating BackendAppSettings...");
            var dbPath = GetDbPath();
            var appSettings = CreateBackendAppSettings(tagSpecs, dbPath);
            _log4netLogger.InfoFormat("DB Path: {0}", dbPath);

            // 4. SubjectC2S 구독 (모니터링용)
            long updateCount = 0;
            _c2sSubscription = GlobalCommunication.SubjectC2S.Subscribe(info =>
            {
                if (info.Tags != null && info.Tags.Length > 0)
                {
                    Interlocked.Add(ref updateCount, info.Tags.Length);

                    // 매 100번째 업데이트만 로깅
                    if (updateCount % 100 == 0)
                    {
                        var timeStr = info.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
                        _log4netLogger.DebugFormat("[{0}] {1} tags updated (total: {2})",
                            timeStr, info.Tags.Length, updateCount);
                    }
                }
            });
            _log4netLogger.Info("SubjectC2S subscribed");

            // 5. EV2 서비스 시작
            _log4netLogger.Info("Starting EV2 services...");
            var plcService = StartEv2Services(appSettings, _log4netLogger);

            _log4netLogger.Info("=== PLC Capture Mode Started Successfully ===");
            _logger.LogInformation("PlcCaptureService started successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start PlcCaptureService");
            _log4netLogger?.Error("Failed to start PlcCaptureService", ex);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PlcCaptureService stopping...");

        try
        {
            _c2sSubscription?.Dispose();
            _c2sSubscription = null;

            _serviceDisposable?.Dispose();
            _serviceDisposable = null;

            _log4netLogger?.Info("PlcCaptureService stopped");
            _logger.LogInformation("PlcCaptureService stopped successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping PlcCaptureService");
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _c2sSubscription?.Dispose();
        _serviceDisposable?.Dispose();
    }

    private void InitializeLog4Net()
    {
        try
        {
            var logConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log4net.config");

            if (!File.Exists(logConfigPath))
            {
                _logger.LogWarning("log4net.config not found at {Path}", logConfigPath);
                return;
            }

            var logRepository = LogManager.GetRepository(Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly());
            XmlConfigurator.Configure(logRepository, new FileInfo(logConfigPath));

            _log4netLogger = LogManager.GetLogger("PlcCapture");
            _log4netLogger.Info("log4net initialized successfully");

            _logger.LogInformation("log4net initialized from {Path}", logConfigPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize log4net");
        }
    }

    private string GetDbPath()
    {
        var dbPath = _configuration["PlcDatabase:SourceDbPath"] ?? "sample/db/DsDB.sqlite3";

        // 환경 변수 확장 (%APPDATA% 등)
        dbPath = Environment.ExpandEnvironmentVariables(dbPath);

        // Windows 경로 구분자 정규화 (/ → \)
        dbPath = dbPath.Replace('/', Path.DirectorySeparatorChar);

        // 상대 경로를 절대 경로로 변환
        if (!Path.IsPathRooted(dbPath))
        {
            dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dbPath);
        }

        // DB 디렉토리 생성
        var dbDirectory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dbDirectory) && !Directory.Exists(dbDirectory))
        {
            Directory.CreateDirectory(dbDirectory);
            _log4netLogger?.InfoFormat("Created DB directory: {0}", dbDirectory);
            _logger.LogInformation("Created DB directory: {Directory}", dbDirectory);
        }

        return dbPath;
    }

    private List<PlcTagInfo> ExtractPlcTags(DsStore store)
    {
        var callIOTags = store.GetCallIOTags();
        var hwIOTags = store.GetHwComponentIOTags();

        var allPlcTags = callIOTags
            .Concat(hwIOTags)
            .Where(tag => !string.IsNullOrEmpty(tag.Address))
            .Select(ioTag => new PlcTagInfo
            {
                Name = ioTag.Name,
                Address = ioTag.Address,
                DataType = ExtractDataTypeFromIOTag(ioTag)
            })
            .DistinctBy(t => t.Address)
            .ToList();

        return allPlcTags;
    }

    private string ExtractDataTypeFromIOTag(IOTag ioTag)
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

        return "BOOL";
    }

    private TagSpec[] CreateTagSpecs(List<PlcTagInfo> tags)
    {
        return tags.Select(tag => new TagSpec(
            name: tag.Name,
            address: tag.Address,
            dataType: ConvertToPlcDataType(tag.DataType),
            walType: FSharpOption<WAL>.Some(WAL.Disk),
            comment: FSharpOption<string>.Some("Auto-generated from DsStore"),
            plcValue: FSharpOption<PlcValue>.None
        )).ToArray();
    }

    private PlcDataType ConvertToPlcDataType(string dataType)
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

    private BackendAppSettings CreateBackendAppSettings(TagSpec[] tagSpecs, string dbPath)
    {
        var appSettings = new BackendAppSettings();

        // DB 설정 - PascalCase 키 사용
        var connectionString = $"Data Source={dbPath};Version=3;BusyTimeout=20000";
        appSettings.DbProvider = DbProvider.NewSqlite(connectionString);

        // TagHistoric 설정
        appSettings.TagHistoric = new TagHistoricSettings
        {
            WALBufferSize = _configuration.GetValue<int>("PlcCapture:WALBufferSize", 1000),
            FlushInterval = TimeSpan.FromSeconds(_configuration.GetValue<int>("PlcCapture:FlushIntervalSec", 5))
        };

        // ScanConfiguration 설정
        var connectionConfig = new MxConnectionConfig
        {
            IpAddress = _configuration["PlcCapture:PlcIpAddress"] ?? "192.168.9.120",
            Port = _configuration.GetValue<int>("PlcCapture:PlcPort", 5555),
            Name = "MitsubishiPLC",
            EnableScan = true,
            Timeout = TimeSpan.FromSeconds(5),
            ScanInterval = TimeSpan.FromMilliseconds(_configuration.GetValue<int>("PlcCapture:ScanIntervalMs", 500)),
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

    private PLCBackendService StartEv2Services(BackendAppSettings appSettings, ILog log)
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
            log.Info("AppDbApi created, DB initialized");
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
                log.InfoFormat("Flushing {0} residual WAL entries...", diskBuf.Count);
                wal.Flush();
            }

            wal.InsertRestartMarker();
            wal.SyncWalTypesFromConfig(appSettings.ScanConfigurations);
            wal.LoadLastTagValues();
            log.Info("TagHistoricWAL created");
        }

        // PLCBackendService 생성 및 시작
        var walOption = wal != null
            ? FSharpOption<TagHistoricWAL>.Some(wal)
            : FSharpOption<TagHistoricWAL>.None;

        var plcService = new PLCBackendService(
            appSettings.ScanConfigurations,
            walOption);

        _serviceDisposable = plcService.Start();

        log.Info("PLCBackendService started");

        var connections = plcService.AllConnectionNames;
        if (connections.Any())
        {
            log.InfoFormat("Active Connections: {0}", string.Join(", ", connections));
        }

        return plcService;
    }
}
