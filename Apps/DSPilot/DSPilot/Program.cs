// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
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

// 호스트 전용 설정(설치 스크립트가 기록하는 바인딩 포트 "Urls" 등)을 사용자 설정 저장소와 분리.
// appsettings.Production.json 은 AppSettingsService 의 사용자 설정 영속 저장소이므로 재설치(업그레이드) 시
// 보존되어야 한다 → 설치 스크립트는 더 이상 Production.json 에 포트를 쓰지 않고 이 파일에만 쓴다.
// 마지막에 추가하므로 Production.json 의 (구버전) Urls 보다 우선한다. optional: 개발/직접 실행 시 없어도 무방.
builder.Configuration.AddJsonFile("appsettings.Hosting.json", optional: true, reloadOnChange: false);

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

// Circuit 종료 시 브라우저 콘솔에 실제 예외 메시지가 보이도록 — 로컬/개발 환경에서만.
// 운영에서는 스택트레이스 노출 막기 위해 자동으로 비활성.
builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(o =>
{
    o.DetailedErrors = builder.Environment.IsDevelopment();
});

// SignalR for real-time monitoring
builder.Services.AddSignalR();

// 격리형 호스팅(Isolated Hosting) — /api/* JSON 컨트롤러 계층.
// 정적 HTML/JS/CSS 페이지(wwwroot/app/*)가 fetch 로 호출하는 데이터 API.
// 기존 싱글톤 서비스를 얇게 래핑만 하며(신규 데이터 로직 없음), Blazor 회로와 동일 프로세스·DI·SignalR 허브를 공유한다.
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        // camelCase(MVC 기본값) 유지 — 기존 wwwroot/js/*.js 가 camelCase 키를 기대(예: call-history-chart.js 의 d.goingTimeMs).
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        // 도메인/EF 엔티티의 양방향 참조 순환으로 인한 직렬화 예외 방지.
        o.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

// Database path resolution (Unified mode support) - F# Adapter 사용
builder.Services.AddSingleton<DatabasePathResolverAdapter>();
builder.Services.AddSingleton<IDatabasePathResolver>(sp => sp.GetRequiredService<DatabasePathResolverAdapter>());

// Core services
builder.Services.AddSingleton<AppSettingsService>();
builder.Services.AddSingleton<DsProjectService>();
builder.Services.AddScoped<DashboardEditService>();
builder.Services.AddSingleton<BlueprintService>();
builder.Services.AddSingleton<CctvOverlayService>(); // CCTV 설비 오버레이 영속(WebRoot/uploads/cctv-overlays.json)
builder.Services.AddSingleton<HeatmapService>();
builder.Services.AddSingleton<DspDbService>();
builder.Services.AddSingleton<PlcDebugService>();
builder.Services.AddScoped<ThemeService>();
builder.Services.AddSingleton<PlcIoDataService>();
builder.Services.AddSingleton<DemoModeService>();

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
// v12 경로이탈 이상감지(4종) 싱크 + 라이브 피드 — SimulationEngineService 의 abnormal 어댑터가 흘려보냄.
builder.Services.AddSingleton<AbnormalEventService>();
builder.Services.AddSingleton<IFlowMetricsService, FlowMetricsService>();
builder.Services.AddScoped<CycleAnalysisService>();
// Flow IO 세그먼트 → lane/interval 빌더 (CallTest 간트 + 자동 실측 보정 공유). CycleAnalysisService 가 Scoped 라 Scoped.
builder.Services.AddScoped<CallLaneBuilderService>();
// DspDatabaseServiceAdapter — Singleton 으로도 등록해서 Settings 페이지가 BootstrapAsync 를 다시 호출 가능
builder.Services.AddSingleton<DspDatabaseServiceAdapter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DspDatabaseServiceAdapter>());

// plc.db 라이프사이클 (삭제 + 재로딩 + 엔진 재시작) — Settings UI 에서 호출
builder.Services.AddSingleton<DatabaseLifecycleService>();

// Head/Tail 경계 변경 시 과거 dspFlowHistory 를 원시 plcTagLog 에서 새 경계로 재도출/재기록.
// 의존 서비스가 전부 싱글톤이라 백그라운드(전체-이력) 잡도 안전.
builder.Services.AddSingleton<CycleRecomputeService>();

// 주기적 자동 재계산 — 라이브 기록기가 놓친 tail 완료로 부풀려진 WT 를 원시 엣지에서 self-heal(증분).
// 간격은 HistoryView.AutoRecomputeIntervalMinutes(0=비활성).
builder.Services.AddHostedService<PeriodicCycleRecomputeService>();

// 실측 duration 자동 보정 — 첫 설치 후 각 Flow 가 클린사이클 N개 도달 시 디바이스 duration/min/max 를 1회 자동 채운다.
// 1회성 플래그(AutoCalibration.CompletedAt, Production.json)로 재시작 시 스킵. Singleton + HostedService —
// SettingsController 가 동일 인스턴스로 수동 "지금 실측값 채우기"(RunAsync(manual:true)) 호출.
builder.Services.AddSingleton<AutoCalibrationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoCalibrationService>());

// 라이브 상태 self-heal — 엔진 in-memory 정본과 DB 가 발산(DB=Going/엔진=non-Going)한 행을 주기 교정.
// 재계산 락 경합 등으로 드롭된 Going→Ready 쓰기를 흡수. 간격은 HistoryView.StateReconcileIntervalSeconds(0=비활성).
builder.Services.AddHostedService<StateReconcileService>();

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

// 시계열 데이터 보존 정책: plcTagLog / dspFlowHistory / userTagAlertLog 는 DB 에 영구 보존.
// 기간 한정은 reader (PlcRepository / DspRepositoryAdapter.GetFlowHistoryByDays / UserTagAlertRepository.BuildFilter) 에서 수행.

// Ds2.Runtime 기반 Engine + RuntimeModeSession + PassiveInferenceSession 통합
builder.Services.AddSingleton<SimulationEngineService>();

// UserTag 알림 — AASX 정의 + plcTagLog 폴링 매칭 (UI: /user-tags)
builder.Services.AddScoped<IUserTagAlertRepository, UserTagAlertRepository>();
builder.Services.AddSingleton<UserTagAlertService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UserTagAlertService>());
// userTagAlertDaily 일별 집계 backfill (purge 없음 — raw 는 영구 보존)
builder.Services.AddHostedService<UserTagAlertAggregationService>();

// OEE / 정지 이벤트 — 별도 oee.db(수동입력 자산 보존, plc.db rebuild 무관). repo scoped, 무사이클 상태머신 HostedService.
builder.Services.AddScoped<IOeeRepository, OeeRepositoryAdapter>();
builder.Services.AddHostedService<OeeDowntimeStateMachine>();
// UserTag 기반 OEE 자동수집(detectSource='usertag') — raw plcTagLog 를 직접 쿼리해 고장 onset/clear·정지원인
// 자동분류·생산/불량을 채운다. 무사이클 상태머신과 소스 구분으로 공존. OeeSignals 설정 없으면 무동작.
builder.Services.AddHostedService<OeeUserTagPollerService>();

// CCTV — 카메라 목록을 별도 프로세스 MediaMTX(:9997) 로 동기화. WebRTC 재게시는 MediaMTX 담당.
// Singleton + HostedService — Settings 페이지가 동일 인스턴스로 SyncAsync 직접 호출.
builder.Services.AddSingleton<CctvMediaMtxService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CctvMediaMtxService>());

// Promaker.Agent 가 broadcast 하는 PLC 어댑터 연결 상태 캐시 — UI 배너 / 대시보드가 구독.
builder.Services.AddSingleton<PlcConnectionStatusTracker>();

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

// OEE 전용 oee.db 스키마 1회 생성 (별도 파일이라 plc.db CreateSchemaAsync 가 만들지 않음). repo 가 scoped 라 scope 필요.
{
    using var oeeScope = app.Services.CreateScope();
    var oeeRepo = oeeScope.ServiceProvider.GetRequiredService<IOeeRepository>();
    var oeeOk = await oeeRepo.CreateSchemaAsync();
    app.Logger.LogInformation("[Startup] OEE schema creation: {Ok}", oeeOk);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // app.UseHsts(); // Allow HTTP for debugging
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
                context.Response.StatusCode, context.Request.Path, app.Environment.WebRootPath ?? "(null)");
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "▶ uploads EXCEPTION — Path: {Path}, WebRoot: {WR}",
            context.Request.Path, app.Environment.WebRootPath ?? "(null)");
        throw;
    }
});

// 동적 업로드 파일: uploads 디렉토리 보장 후 PhysicalFileProvider로 직접 서빙
// WebRootPath: Release 직접 실행 시 (dotnet publish 미사용) wwwroot 가 bin 옆에 없으면 null → ContentRoot 로 폴백
var webRoot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
var uploadsPath = Path.Combine(webRoot, "uploads");
Directory.CreateDirectory(uploadsPath); // 서비스 시작 시 디렉토리 없으면 생성
app.Logger.LogInformation("▶ DSPilot uploads dir: {Path}, exists: {E}", uploadsPath, Directory.Exists(uploadsPath));

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsPath),
    RequestPath = "/uploads"
});

app.UseStaticFiles();
app.UseAntiforgery();

// ── 데모 모드 차단 ──
// DemoModeService.IsBlocked 인 경우 /pw 백도어/정적 자원/Blazor 회로 외 모든 요청을 정지 화면으로 응답.
// MainLayout 에서도 동일 상태를 감지하여 회로 내 네비게이션을 차단한다.
app.Use(async (context, next) =>
{
    var demo = context.RequestServices.GetRequiredService<DemoModeService>();
    if (!demo.IsBlocked)
    {
        await next();
        return;
    }

    var path = context.Request.Path.Value ?? string.Empty;
    if (IsAllowedDuringDemoBlock(path))
    {
        await next();
        return;
    }

    context.Response.StatusCode = 503;
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(DemoBlockedHtml());
});

// ── 격리형 호스팅: 정식 라우트를 정적 /app/*.html 로 대체 ──
// 이전 완료된 8개 Blazor 페이지를 정적 페이지로 "대체". 미들웨어로 short-circuit 하므로
// (엔드포인트가 아님) Blazor @page 엔드포인트와 ambiguity 없음 — 이 줄을 지우면 즉시 원복.
// 데모 게이트 뒤에 위치하므로 데모 차단 시 정식 라우트도 503(기존 Blazor 동작과 동일).
// /flow·/pw 도 정적 페이지로 이전 완료 — 이제 모든 페이지가 정적(격리형 호스팅)이다.
// /editor(레이아웃 편집기)는 폐지: 대시보드 도면 인플레이스 자유배치 편집으로 흡수(라우트·editor.html·Editor.razor 제거).
// 기존 Blazor @page(.razor)는 폴백으로 남겨두며(딕셔너리에서 해당 줄 삭제 시 즉시 Blazor 로 원복).
var canonicalStaticRoutes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["/"] = "dashboard2.html",
    ["/dashboard"] = "dashboard2.html",
    ["/heatmap"] = "heatmap.html",
    ["/cycle-time-analysis"] = "cycle-time-analysis.html",
    ["/uptime"] = "uptime.html",
    ["/oee"] = "oee.html",
    ["/cctv"] = "cctv.html",
    ["/plc-debug"] = "plc-debug.html",
    ["/settings"] = "settings.html",
    ["/flow"] = "flow.html",
    ["/flow-all"] = "flow-all.html",
    ["/pw"] = "pw.html",
};
app.Use(async (context, next) =>
{
    if (HttpMethods.IsGet(context.Request.Method)
        && canonicalStaticRoutes.TryGetValue(context.Request.Path.Value ?? string.Empty, out var file))
    {
        var path = Path.Combine(webRoot, "app", file);
        if (File.Exists(path))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(path);
            return;
        }
    }
    await next();
});

// TODO: MapStaticAssets 500 진단 — 원인 파악 후 복원
// app.MapStaticAssets();

// /api/* 컨트롤러 — 반드시 Blazor SPA catch-all(MapRazorComponents) 보다 먼저 매핑해야
// attribute 라우트가 Blazor 라우팅에 흡수되지 않는다.
// 정적 페이지(wwwroot/app/*)는 기본 UseStaticFiles 가 서빙하므로 별도 매핑 불필요.
// (데모 차단 시: 정적 셸은 short-circuit 으로 로드되지만 /api 는 데모 게이트에서 503 → 페이지가 자체 "데모 만료" 안내)
app.MapControllers();

app.MapRazorComponents<DSPilot.Components.App>()
    .AddInteractiveServerRenderMode();

// SignalR Hub endpoint
app.MapHub<DSPilot.Hubs.MonitoringHub>("/hubs/monitoring");

app.Run();

static bool IsAllowedDuringDemoBlock(string path)
{
    if (string.IsNullOrEmpty(path)) return true;
    return path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/images/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/DSPilot.styles.css", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/pw", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/pw/", StringComparison.OrdinalIgnoreCase)
        // /pw 정적 백도어가 데모 차단 중에도 바이패스를 토글할 수 있도록 데모 API 만 예외 허용.
        || path.StartsWith("/api/demo", StringComparison.OrdinalIgnoreCase);
}

static string DemoBlockedHtml() => """
<!DOCTYPE html>
<html lang="ko">
<head>
<meta charset="utf-8" />
<title>DEMO 만료 - DSPilot</title>
<style>
  html,body{margin:0;height:100%;background:#0f1419;color:#e6edf3;font-family:'Noto Sans KR',system-ui,sans-serif;}
  .wrap{display:flex;align-items:center;justify-content:center;height:100%;}
  .card{max-width:520px;padding:40px;border-radius:12px;background:#161b22;border:1px solid #30363d;text-align:center;}
  h1{margin:0 0 12px;font-size:28px;color:#f0883e;}
  p{margin:0 0 8px;line-height:1.6;color:#c9d1d9;}
</style>
</head>
<body>
<div class="wrap"><div class="card">
  <h1>DEMO 만료</h1>
  <p>데모 사용 시간(3시간)이 종료되었습니다.</p>
  <p>서비스 관리자에게 문의해 주세요.</p>
</div></div>
</body>
</html>
""";

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
