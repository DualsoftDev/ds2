using MudBlazor.Services;
using DSPilot.Services;
using DSPilot.Repositories;

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

builder.Services.AddMudServices();
builder.Services.AddSingleton<DsProjectService>();
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
builder.Services.AddSingleton<IFlowMetricsService, FlowMetricsService>();
builder.Services.AddScoped<CycleAnalysisService>();
builder.Services.AddHostedService<DspDatabaseService>();

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

app.Run();
