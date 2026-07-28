module Ds2.Collector.Program

open System
open System.IO
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Ds2.Collector.Sinks
open Ds2.Collector.DataApi

/// Phase 6 · Collector Worker + Phase 7 · Data API 통합 프로세스.
///
/// 이 프로세스는:
///   - UA 서버 구독 (Wire-up 시점에 IUaWriter 반대편)
///   - Adapter Outbox pull (HTTP · Adapter.Common EdgeBuffer)
///   - SqliteSinkWriter 로 telemetry.db + events.db 적재
///   - DownsampleScheduler · Retention 백그라운드 태스크
///   - IT/클라우드 소비 REST API (v1/series, v1/events)
[<EntryPoint>]
let main argv =
    let builder = WebApplication.CreateBuilder argv

    let root =
        match Environment.GetEnvironmentVariable "DS2_COLLECTOR_ROOT" with
        | null | "" -> Path.Combine(Directory.GetCurrentDirectory(), "data", "collector")
        | v -> v
    Directory.CreateDirectory root |> ignore
    let telemetryDb = Path.Combine(root, "telemetry.db")
    let eventsDb = Path.Combine(root, "events.db")
    Downsample.ensureSchema telemetryDb

    let paths = { TelemetryDb = telemetryDb; EventsDb = eventsDb }
    let registry = SeriesIdRegistry()

    builder.Services
        .AddSingleton<SqliteSinkWriter>(fun _sp -> SqliteSinkWriter(telemetryDb, eventsDb))
        .AddSingleton<SeriesIdRegistry>(registry)
        .AddSingleton<DataApiPaths>(paths)
        .AddControllers() |> ignore

    let app = builder.Build()
    app.MapControllers() |> ignore

    let logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ds2.Collector")
    logger.LogInformation("Ds2.Collector 시작 · telemetryDb={T}, eventsDb={E}", telemetryDb, eventsDb)

    app.Run()
    0
