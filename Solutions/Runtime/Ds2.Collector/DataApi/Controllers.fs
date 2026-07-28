namespace Ds2.Collector.DataApi

open System
open Microsoft.AspNetCore.Mvc
open Microsoft.Data.Sqlite

/// Phase 7 · IT/클라우드 소비 REST API (Collector 프로세스에 통합됨).
///
/// ADR-011 · SQLite 만 사용 (Kafka/InfluxDB 은 확장 스택).
/// ADR-009 · path segment 는 Base64url (여기서는 query param 만 사용).

/// Collector 프로세스가 소유한 telemetry / events DB 경로를 전달하는 컨테이너.
type DataApiPaths = {
    TelemetryDb : string
    EventsDb    : string
}

[<ApiController>]
[<Route("v1/series")>]
type SeriesController(registry: SeriesIdRegistry, paths: DataApiPaths) =
    inherit ControllerBase()

    [<HttpGet>]
    member this.Get([<FromQuery>] seriesId: string, [<FromQuery>] rangeSeconds: float, [<FromQuery>] maxPoints: int) : IActionResult =
        if String.IsNullOrWhiteSpace seriesId then
            this.BadRequest("seriesId required") :> IActionResult
        else
            match registry.Resolve seriesId with
            | None -> this.NotFound({| seriesId = seriesId; reason = "unknown seriesId" |}) :> IActionResult
            | Some res ->
                let table = TableSelector.pickForRange rangeSeconds
                let limit = if maxPoints <= 0 then 10000 else min maxPoints 10000
                use conn = new SqliteConnection($"Data Source={paths.TelemetryDb};Mode=ReadOnly;Pooling=False")
                conn.Open()
                use cmd = conn.CreateCommand()
                let tsColumn = if table = "signals" then "source_ts_us" else "bucket_ts_us"
                let valueColumn = if table = "signals" then "value_double" else "mean"
                cmd.CommandText <-
                    sprintf "SELECT %s, %s FROM %s
                             WHERE global_asset_id = $g AND signal_id = $s
                             ORDER BY %s DESC LIMIT $lim" tsColumn valueColumn table tsColumn
                cmd.Parameters.AddWithValue("$g", res.GlobalAssetId) |> ignore
                cmd.Parameters.AddWithValue("$s", res.SignalId) |> ignore
                cmd.Parameters.AddWithValue("$lim", limit) |> ignore
                use reader = cmd.ExecuteReader()
                let points =
                    [ while reader.Read() do
                        yield {| ts = reader.GetInt64 0
                                 value = if reader.IsDBNull 1 then Double.NaN else reader.GetDouble 1 |} ]
                let response = {|
                    seriesId = seriesId
                    table = table
                    globalAssetId = res.GlobalAssetId
                    signalId = res.SignalId
                    count = List.length points
                    points = points
                |}
                this.Ok(response) :> IActionResult

[<ApiController>]
[<Route("v1/events")>]
type EventsController(paths: DataApiPaths) =
    inherit ControllerBase()

    [<HttpGet>]
    member this.Get([<FromQuery>] asset: string,
                    [<FromQuery>] eventType: string,
                    [<FromQuery>] pageSize: int) : IActionResult =
        if String.IsNullOrWhiteSpace asset then
            this.BadRequest("asset required") :> IActionResult
        else
            let limit = if pageSize <= 0 then 100 else min pageSize 500
            use conn = new SqliteConnection($"Data Source={paths.EventsDb};Mode=ReadOnly;Pooling=False")
            conn.Open()
            use cmd = conn.CreateCommand()
            let whereEventType = if String.IsNullOrWhiteSpace eventType then "" else " AND event_type_semantic_id = $et"
            cmd.CommandText <-
                sprintf "SELECT id, envelope_id, source_ts_us, event_type_semantic_id, payload
                         FROM events
                         WHERE global_asset_id = $g %s
                         ORDER BY source_ts_us DESC LIMIT $lim" whereEventType
            cmd.Parameters.AddWithValue("$g", asset) |> ignore
            if not (String.IsNullOrWhiteSpace eventType) then
                cmd.Parameters.AddWithValue("$et", eventType) |> ignore
            cmd.Parameters.AddWithValue("$lim", limit) |> ignore
            use reader = cmd.ExecuteReader()
            let items =
                [ while reader.Read() do
                    yield {| id = reader.GetInt64 0
                             envelopeId = Guid(reader.GetFieldValue<byte[]> 1)
                             sourceTsUs = reader.GetInt64 2
                             eventType = reader.GetString 3
                             payload = reader.GetString 4 |} ]
            this.Ok({| count = List.length items; items = items |}) :> IActionResult

[<ApiController>]
type InfoController() =
    inherit ControllerBase()
    [<HttpGet("/healthz")>]
    member _.Healthz() : obj = "ok" :> obj
    [<HttpGet("/v1/info")>]
    member _.Info() : obj = {| service = "Ds2.Collector"; storage = "SQLite (ADR-011)" |} :> obj
