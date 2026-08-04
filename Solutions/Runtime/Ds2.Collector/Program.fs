module Ds2.Collector.Program

open System
open System.IO
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Hosting.WindowsServices
open Ds2.Collector.Sinks
open Ds2.Collector.DataApi

/// Phase 6 · Collector Worker + Phase 7 · Data API 통합 프로세스.
///
/// 이 프로세스는:
///   - Agent UA 서버 browse/subscription → SQLite batch 적재
///   - Adapter Outbox pull (HTTP · Adapter.Common EdgeBuffer)
///   - SqliteSinkWriter 로 telemetry.db + events.db 적재
///   - DownsampleScheduler · Retention 백그라운드 태스크
///   - IT/클라우드 소비 REST API (v1/series, v1/events)
[<EntryPoint>]
let main argv =
    let builder = WebApplication.CreateBuilder argv
    let runningAsWindowsService = WindowsServiceHelpers.IsWindowsService()

    if runningAsWindowsService then
        builder.Services.AddWindowsService(fun options -> options.ServiceName <- "Ds2CollectorService") |> ignore
        // SCM 기본 작업 디렉터리(System32)에 DB/인증서를 만들지 않도록 exe 위치로 정규화한다.
        Environment.CurrentDirectory <- AppContext.BaseDirectory

    // Collector API는 기본 localhost 전용. 외부 공개는 명시적인 ASPNETCORE_URLS 설정으로만 연다.
    if String.IsNullOrWhiteSpace(builder.Configuration.["urls"]) then
        builder.WebHost.UseUrls("http://127.0.0.1:62542") |> ignore

    let root =
        match Environment.GetEnvironmentVariable "DS2_COLLECTOR_ROOT" with
        | null | "" when runningAsWindowsService ->
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "DualSoft", "Collector")
        | null | "" -> Path.Combine(Directory.GetCurrentDirectory(), "data", "collector")
        | v -> v
    Directory.CreateDirectory root |> ignore
    let telemetryDb = Path.Combine(root, "telemetry.db")
    let eventsDb = Path.Combine(root, "events.db")
    Downsample.ensureSchema telemetryDb

    let paths = { TelemetryDb = telemetryDb; EventsDb = eventsDb }
    let registry = SeriesIdRegistry()
    let uaOptions = UaSubscriptionOptions.fromEnvironment root
    let retentionOptions = RetentionOptions.fromEnvironment ()
    let downsampleOptions = DownsampleOptions.fromEnvironment ()

    builder.Services
        .AddSingleton<SqliteSinkWriter>(fun _sp -> SqliteSinkWriter(telemetryDb, eventsDb))
        .AddSingleton<SeriesIdRegistry>(registry)
        .AddSingleton<UaSubscriptionOptions>(uaOptions)
        .AddSingleton<RetentionOptions>(retentionOptions)
        .AddSingleton<DownsampleOptions>(downsampleOptions)
        .AddSingleton<DataApiPaths>(paths)
        .AddControllers() |> ignore
    builder.Services.AddHostedService<UaSubscriptionService>() |> ignore
    builder.Services.AddHostedService<DownsampleService>() |> ignore
    builder.Services.AddHostedService<RetentionService>() |> ignore

    let app = builder.Build()
    app.MapControllers() |> ignore

    let logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ds2.Collector")
    logger.LogInformation("Ds2.Collector 시작 · telemetryDb={T}, eventsDb={E}", telemetryDb, eventsDb)
    logger.LogInformation(
        "UA subscription · enabled={Enabled} endpoint={Endpoint} security={Security} certificateIdentity={CertificateIdentity} autoAcceptUntrusted={AutoAccept}",
        uaOptions.Enabled,
        uaOptions.EndpointUrl,
        uaOptions.UseSecurity,
        uaOptions.UseCertificateIdentity,
        uaOptions.AutoAcceptUntrustedCertificates)

    app.Run()
    0
