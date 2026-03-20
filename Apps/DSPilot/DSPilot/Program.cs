using DSPilot.Services;
using DSPilot.Repositories;
using DSPilot.Abstractions;

var builder = WebApplication.CreateBuilder(args);

// 진단 모드 체크
if (args.Contains("--diagnose"))
{
    var dbPath = builder.Configuration["PlcDatabase:SourceDbPath"] ?? "sample/db/DsDB.sqlite3";
    DSPilot.DiagnosticTool.DiagnosePlcDatabase(dbPath);
    return;
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<AppSettingsService>();
builder.Services.AddSingleton<DsProjectService>();
builder.Services.AddScoped<DashboardEditService>();
builder.Services.AddSingleton<BlueprintService>();
builder.Services.AddSingleton<HeatmapService>();
builder.Services.AddSingleton<DspDbService>();

// PLC 데이터 읽기 서비스 등록
builder.Services.AddSingleton<IPlcRepository, PlcRepository>();
builder.Services.AddHostedService<PlcDataReaderService>();

// DSP 데이터베이스 서비스 등록
builder.Services.AddSingleton<IDspRepository, DspRepository>();
builder.Services.AddSingleton<PlcToCallMapperService>();
builder.Services.AddSingleton<PlcTagStateTrackerService>();
builder.Services.AddSingleton<CallStatisticsService>();
builder.Services.AddSingleton<InMemoryCallStateStore>();
builder.Services.AddSingleton<IFlowMetricsService, FlowMetricsService>();
builder.Services.AddScoped<CycleAnalysisService>();
builder.Services.AddHostedService<DspDatabaseService>();

// Ev2.Backend.PLC 기반 이벤트 처리 (옵션)
var plcConnectionEnabled = builder.Configuration.GetValue<bool>("PlcConnection:Enabled");
if (plcConnectionEnabled)
{
    // PLC 연결 설정
    var plcConfig = new PlcConnectionConfig
    {
        PlcName = builder.Configuration["PlcConnection:PlcName"] ?? "PLC_01",
        IpAddress = builder.Configuration["PlcConnection:IpAddress"] ?? "192.168.0.100",
        ScanIntervalMs = builder.Configuration.GetValue<int>("PlcConnection:ScanIntervalMs", 100),
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

// 재시작 딜레이: 이전 프로세스가 포트를 해제할 시간 확보
var delayIndex = Array.IndexOf(args, "--restart-delay");
if (delayIndex >= 0 && delayIndex + 1 < args.Length && int.TryParse(args[delayIndex + 1], out var delayMs))
{
    app.Logger.LogInformation("재시작 딜레이 {Delay}ms 대기 중...", delayMs);
    Thread.Sleep(delayMs);
}

app.MapStaticAssets();
app.MapRazorComponents<DSPilot.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
