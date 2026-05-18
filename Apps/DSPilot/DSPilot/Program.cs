using DSPilot.Services;
using DSPilot.Repositories;
using DSPilot.Adapters;
using DSPilot.Infrastructure;
using System.Data.Common;
using Microsoft.Extensions.Hosting.WindowsServices;
using Dapper;

// Dapper Guid <-> SQLite TEXT 양방향 매핑 (dspCall.callId 등이 TEXT 로 저장됨)
// Microsoft.Data.Sqlite 의 기본 BLOB 시도를 우회.
SqlMapper.RemoveTypeMap(typeof(Guid));
SqlMapper.RemoveTypeMap(typeof(Guid?));
SqlMapper.AddTypeHandler(new SqliteGuidHandler());
SqlMapper.AddTypeHandler(new SqliteNullableGuidHandler());

// Windows 서비스 실행 시 작업 디렉터리가 System32이므로 exe 위치로 변경
if (WindowsServiceHelpers.IsWindowsService())
    Environment.CurrentDirectory = AppContext.BaseDirectory;

// appsettings.json이 없으면 defaults에서 자동 생성
AppSettingsService.EnsureSettingsFiles(Environment.CurrentDirectory);

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

// 진단 모드 체크
if (args.Contains("--diagnose"))
{
    var dbPath = ResolveConfiguredDatabasePath(builder.Configuration) ?? "sample/db/DsDB.sqlite3";
    DSPilot.DiagnosticTool.DiagnosePlcDatabase(dbPath);
    return;
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        // 대용량 차트(예: cycle-time-analysis 의 수천 개 SVG bar)에서
        // 기본 32KB 한계를 넘어 circuit이 끊기는 문제 방지
        options.MaximumReceiveMessageSize = 10 * 1024 * 1024; // 10 MB
    });

// SignalR for real-time monitoring
builder.Services.AddSignalR();

// Database path resolution (Unified mode support) - F# Adapter 사용
builder.Services.AddSingleton<DatabasePathResolverAdapter>();
builder.Services.AddSingleton<IDatabasePathResolver>(sp => sp.GetRequiredService<DatabasePathResolverAdapter>());

// Core services
builder.Services.AddSingleton<AppSettingsService>();
builder.Services.AddSingleton<DsProjectService>();
builder.Services.AddScoped<DashboardEditService>();
builder.Services.AddSingleton<BlueprintService>();
builder.Services.AddSingleton<HeatmapService>();
builder.Services.AddSingleton<DspDbService>();
builder.Services.AddSingleton<PlcDebugService>();
builder.Services.AddScoped<ThemeService>();
builder.Services.AddSingleton<PlcIoDataService>();

// PLC 데이터 읽기 서비스 (plcTag/plcTagLog 조회 — Hub 가 채운 데이터를 UI 에서 사용)
builder.Services.AddSingleton<IPlcRepository, PlcRepository>();

// DSP 데이터베이스 서비스 등록 - F# Adapter 사용
builder.Services.AddSingleton<DspRepositoryAdapter>(sp =>
{
    var pathResolver = sp.GetRequiredService<DatabasePathResolverAdapter>();
    var logger = sp.GetRequiredService<ILogger<DspRepositoryAdapter>>();
    return new DspRepositoryAdapter(pathResolver.GetDatabasePaths(), logger);
});

// Register as interface for existing consumers
builder.Services.AddSingleton<IDspRepository>(sp => sp.GetRequiredService<DspRepositoryAdapter>());

// Tag → Call 매핑 + 상태 변경 알림
builder.Services.AddSingleton<PlcToCallMapperService>();
builder.Services.AddSingleton<CallStateNotificationService>();
builder.Services.AddSingleton<IFlowMetricsService, FlowMetricsService>();
builder.Services.AddScoped<CycleAnalysisService>();
// DspDatabaseServiceAdapter — Singleton 으로도 등록해서 Settings 페이지가 BootstrapAsync 를 다시 호출 가능
builder.Services.AddSingleton<DspDatabaseServiceAdapter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DspDatabaseServiceAdapter>());

// plc.db 라이프사이클 (삭제 + 재로딩 + 엔진 재시작) — Settings UI 에서 호출
builder.Services.AddSingleton<DatabaseLifecycleService>();

// 공유 AASX 파일 감시 — 콘텐츠(SHA256) 변경 시 UI 알림.
//   - 미로드 상태(초기 설치)에서 첫 AASX 감지 시 자동 DB 재구축
//   - 이후 변경은 알림만, 사용자가 Settings 에서 수동 재구축
builder.Services.AddHostedService<AasxFileWatcherService>();

// Real-time monitoring broadcast service
builder.Services.AddHostedService<MonitoringBroadcastService>();

// Real-time PLC Database Monitor (plcTagLog 변경 감지 및 SignalR 브로드캐스트 — PlcDebug 페이지용)
builder.Services.AddHostedService<PlcDatabaseMonitorService>();

// plcTagLog 배치 writer (250ms / 100건 단위 트랜잭션 INSERT) — Singleton + HostedService
builder.Services.AddSingleton<PlcTagLogWriterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PlcTagLogWriterService>());

// plcTagLog retention — 30일 이상 된 행 자동 삭제 (디스크 폭증 방지)
builder.Services.AddHostedService<PlcTagLogRetentionService>();

// Ds2.Runtime 기반 Engine + RuntimeModeSession + PassiveInferenceSession 통합
builder.Services.AddSingleton<SimulationEngineService>();

// UserTag 알림 — AASX 정의 + plcTagLog 폴링 매칭 (UI: /user-tags)
builder.Services.AddSingleton<UserTagAlertService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UserTagAlertService>());

// Promaker SignalHub 클라이언트 — DSPilot 의 핵심 모니터링 경로라 무조건 등록.
// URL/AcceptedSources 는 여전히 appsettings 의 Hub 섹션에서 오버라이드 가능 (HubSubscriberService 가 직접 읽음).
// Singleton + HostedService 패턴 — MonitoringHub 가 NudgeConnectAsync 호출용으로 동일 인스턴스 주입.
builder.Services.AddSingleton<HubSubscriberService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HubSubscriberService>());

var app = builder.Build();

// H1 fix: HostedService 시작 전에 plc.db 스키마를 보장 — Hub 신호가 빨리 들어와도
// BootstrapPlcTags 가 plcTag 없는 상태에서 INSERT 실패하지 않도록.
{
    var dspRepoEarly = app.Services.GetRequiredService<DspRepositoryAdapter>();
    var schemaOk = await dspRepoEarly.CreateSchemaAsync();
    app.Logger.LogInformation("[Startup] Eager schema creation: {Ok}", schemaOk);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// ── 진단용: uploads 요청 예외 캡처 (원인 파악 후 제거) ──
app.Use(async (context, next) =>
{
    try
    {
        await next();
        if (context.Response.StatusCode >= 400 && context.Request.Path.StartsWithSegments("/uploads"))
        {
            app.Logger.LogError("▶ uploads {Status} — Path: {Path}, WebRoot: {WR}",
                context.Response.StatusCode, context.Request.Path, app.Environment.WebRootPath);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "▶ uploads EXCEPTION — Path: {Path}, WebRoot: {WR}",
            context.Request.Path, app.Environment.WebRootPath);
        throw;
    }
});

// 동적 업로드 파일: uploads 디렉토리 보장 후 PhysicalFileProvider로 직접 서빙
var uploadsPath = Path.Combine(app.Environment.WebRootPath, "uploads");
Directory.CreateDirectory(uploadsPath); // 서비스 시작 시 디렉토리 없으면 생성
app.Logger.LogInformation("▶ DSPilot uploads dir: {Path}, exists: {E}", uploadsPath, Directory.Exists(uploadsPath));

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsPath),
    RequestPath = "/uploads"
});

app.UseStaticFiles();
app.UseAntiforgery();

// TODO: MapStaticAssets 500 진단 — 원인 파악 후 복원
// app.MapStaticAssets();
app.MapRazorComponents<DSPilot.Components.App>()
    .AddInteractiveServerRenderMode();

// SignalR Hub endpoint
app.MapHub<DSPilot.Hubs.MonitoringHub>("/hubs/monitoring");

app.Run();

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
