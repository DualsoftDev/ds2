using DSPilot.Services;
using DSPilot.Repositories;
using DSPilot.Abstractions;
using DSPilot.Adapters;
using System.Data.Common;
using Microsoft.Extensions.Hosting.WindowsServices;

// Windows 서비스 실행 시 작업 디렉터리가 System32이므로 exe 위치로 변경
if (WindowsServiceHelpers.IsWindowsService())
    Environment.CurrentDirectory = AppContext.BaseDirectory;

// appsettings.json이 없으면 defaults에서 자동 생성
AppSettingsService.EnsureSettingsFiles(Environment.CurrentDirectory);

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();
var defaultEv2ScanIntervalMs = ResolveScanIntervalMs(builder.Configuration, "PlcCapture:ScanIntervalMs", 100);

// 진단 모드 체크
if (args.Contains("--diagnose"))
{
    var dbPath = ResolveConfiguredDatabasePath(builder.Configuration) ?? "sample/db/DsDB.sqlite3";
    DSPilot.DiagnosticTool.DiagnosePlcDatabase(dbPath);
    return;
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// SignalR for real-time monitoring
builder.Services.AddSignalR();

// Database path resolution (Unified mode support) - F# Adapter 사용
builder.Services.AddSingleton<DatabasePathResolverAdapter>();
builder.Services.AddSingleton<IDatabasePathResolver>(sp => sp.GetRequiredService<DatabasePathResolverAdapter>());

// Bootstrap service for EV2 + DSP schema initialization - F# Adapter 사용
builder.Services.AddHostedService<Ev2BootstrapServiceAdapter>();

// Core services
builder.Services.AddSingleton<AppSettingsService>();
builder.Services.AddSingleton<DsProjectService>();
builder.Services.AddScoped<DashboardEditService>();
builder.Services.AddSingleton<BlueprintService>();
builder.Services.AddSingleton<HeatmapService>();
builder.Services.AddSingleton<DspDbService>();
builder.Services.AddSingleton<PlcDebugService>();
builder.Services.AddSingleton<ThemeService>();
builder.Services.AddSingleton<PlcIoDataService>();

// PLC 데이터 읽기 서비스 등록
builder.Services.AddSingleton<IPlcRepository, PlcRepository>();
builder.Services.AddHostedService<PlcDataReaderService>();

// DSP 데이터베이스 서비스 등록 - F# Adapter 사용
builder.Services.AddSingleton<DspRepositoryAdapter>(sp =>
{
    var pathResolver = sp.GetRequiredService<DatabasePathResolverAdapter>();
    var logger = sp.GetRequiredService<ILogger<DspRepositoryAdapter>>();
    return new DspRepositoryAdapter(pathResolver.GetDatabasePaths(), logger);
});

// Register as interface for existing consumers
builder.Services.AddSingleton<IDspRepository>(sp => sp.GetRequiredService<DspRepositoryAdapter>());

// Tracking services (F# Engine integration)
builder.Services.AddSingleton<PlcToCallMapperService>();
builder.Services.AddSingleton<PlcTagStateTrackerService>();
builder.Services.AddSingleton<CallStateNotificationService>();

builder.Services.AddSingleton<CallStatisticsService>();
builder.Services.AddSingleton<InMemoryCallStateStore>();
builder.Services.AddSingleton<IFlowMetricsService, FlowMetricsService>();
builder.Services.AddScoped<CycleAnalysisService>();
builder.Services.AddHostedService<DspDatabaseServiceAdapter>();

// Real-time monitoring broadcast service
builder.Services.AddHostedService<MonitoringBroadcastService>();

// Real-time PLC Database Monitor (실제 DB에서 변경사항 감지 및 브로드캐스트)
builder.Services.AddHostedService<PlcDatabaseMonitorService>();

// Ev2.Backend.PLC 기반 실시간 이벤트 처리 (F# StateTransition 통합)
var plcConnectionEnabled = builder.Configuration.GetValue<bool>("PlcConnection:Enabled");
if (plcConnectionEnabled)
{
    // PLC 연결 설정
    var plcConfig = new PlcConnectionConfig
    {
        PlcName = builder.Configuration["PlcConnection:PlcName"] ?? "PLC_01",
        IpAddress = builder.Configuration["PlcConnection:IpAddress"] ?? "192.168.0.100",
        ScanIntervalMs = ResolveScanIntervalMs(builder.Configuration, "PlcConnection:ScanIntervalMs", defaultEv2ScanIntervalMs),
        TagAddresses = builder.Configuration.GetSection("PlcConnection:TagAddresses").Get<List<string>>() ?? new List<string>()
    };

    builder.Services.AddSingleton(plcConfig);
    builder.Services.AddSingleton<IPlcEventSource, Ev2PlcEventSource>();
    builder.Services.AddHostedService<PlcEventProcessorService>();

    builder.Logging.AddConsole();
    builder.Logging.SetMinimumLevel(LogLevel.Debug);
}

// PLC Capture 서비스 등록 (DsStore → PLC → DB)
var captureEnabled = builder.Configuration.GetValue<bool>("PlcCapture:Enabled");
if (captureEnabled)
{
    builder.Services.AddHostedService<PlcCaptureService>();
    builder.Logging.AddConsole();
    builder.Logging.SetMinimumLevel(LogLevel.Information);
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();



app.MapStaticAssets();
app.MapRazorComponents<DSPilot.Components.App>()
    .AddInteractiveServerRenderMode();

// SignalR Hub endpoint
app.MapHub<DSPilot.Hubs.MonitoringHub>("/hubs/monitoring");

app.Run();

static int ResolveScanIntervalMs(IConfiguration configuration, string legacyKey, int fallbackMs)
{
    var configuredMs = configuration.GetValue<int?>(legacyKey);
    if (configuredMs.HasValue && configuredMs.Value > 0)
    {
        return configuredMs.Value;
    }

    var configuredValue = configuration["ScanInterval"];
    if (TimeSpan.TryParse(configuredValue, out var configuredInterval) && configuredInterval > TimeSpan.Zero)
    {
        return (int)Math.Max(1, configuredInterval.TotalMilliseconds);
    }

    return fallbackMs;
}

static string? ResolveConfiguredDatabasePath(IConfiguration configuration)
{
    var connectionString = configuration["Database:ConnectionString"];
    if (!string.IsNullOrWhiteSpace(connectionString))
    {
        try
        {
            var builder = new DbConnectionStringBuilder
            {
                ConnectionString = Environment.ExpandEnvironmentVariables(connectionString)
            };

            if (TryGetValue(builder, "Data Source", out var dataSource) ||
                TryGetValue(builder, "DataSource", out dataSource) ||
                TryGetValue(builder, "Filename", out dataSource))
            {
                return NormalizePath(dataSource);
            }
        }
        catch
        {
            // Ignore and fall back to legacy setting.
        }
    }

    var legacyPath = configuration["Database:SharedDbPath"];
    return string.IsNullOrWhiteSpace(legacyPath) ? null : NormalizePath(legacyPath);
}

static bool TryGetValue(DbConnectionStringBuilder builder, string key, out string value)
{
    if (builder.TryGetValue(key, out var rawValue) && rawValue is not null)
    {
        value = rawValue.ToString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    value = string.Empty;
    return false;
}

static string NormalizePath(string path)
{
    var normalized = Environment.ExpandEnvironmentVariables(path)
        .Replace('/', Path.DirectorySeparatorChar);

    return Path.IsPathRooted(normalized)
        ? normalized
        : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, normalized);
}
