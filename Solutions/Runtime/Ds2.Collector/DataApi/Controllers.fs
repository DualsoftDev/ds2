namespace Ds2.Collector.DataApi

open System
open System.Text.Json.Serialization
open Microsoft.AspNetCore.Mvc
open Microsoft.Data.Sqlite

/// Phase 7 · IT/클라우드 소비 REST API (Collector 프로세스에 통합됨).
///
/// ADR-011 · SQLite 만 사용 (Kafka/InfluxDB 은 확장 스택).
/// ADR-009 · path segment 는 Base64url (여기서는 query param 만 사용).

[<CLIMutable>]
type SeriesPoint = {
    [<JsonPropertyName("ts")>]
    Ts : int64
    [<JsonPropertyName("valueType")>]
    ValueType : string
    [<JsonPropertyName("value")>]
    Value : obj
    [<JsonPropertyName("quality")>]
    Quality : Nullable<int64>
    [<JsonPropertyName("unit")>]
    Unit : string
    [<JsonPropertyName("count")>]
    Count : int64
    [<JsonPropertyName("mean")>]
    Mean : Nullable<float>
    [<JsonPropertyName("min")>]
    Min : Nullable<float>
    [<JsonPropertyName("max")>]
    Max : Nullable<float>
}

[<RequireQualifiedAccess>]
module SeriesQuery =
    let private dbString (reader: SqliteDataReader) ordinal =
        if reader.IsDBNull ordinal then null else reader.GetString ordinal

    let private dbFloat (reader: SqliteDataReader) ordinal =
        if reader.IsDBNull ordinal then Nullable<float>()
        else Nullable(reader.GetDouble ordinal)

    let private typedValue
        (reader: SqliteDataReader)
        (valueTypeOrdinal: int)
        (doubleOrdinal: int)
        (longOrdinal: int)
        (stringOrdinal: int)
        (boolOrdinal: int) : obj =
        match reader.GetString(valueTypeOrdinal).ToLowerInvariant() with
        | "double" when not (reader.IsDBNull doubleOrdinal) -> box (reader.GetDouble doubleOrdinal)
        | "long" when not (reader.IsDBNull longOrdinal) -> box (reader.GetInt64 longOrdinal)
        | "string" when not (reader.IsDBNull stringOrdinal) -> box (reader.GetString stringOrdinal)
        | "bool" when not (reader.IsDBNull boolOrdinal) -> box (reader.GetInt64(boolOrdinal) <> 0L)
        | _ -> null

    let readRaw (reader: SqliteDataReader) : SeriesPoint =
        { Ts = reader.GetInt64 0
          ValueType = reader.GetString 1
          Value = typedValue reader 1 2 3 4 5
          Quality = Nullable(reader.GetInt64 6)
          Unit = dbString reader 7
          Count = 1L
          Mean = Nullable<float>()
          Min = Nullable<float>()
          Max = Nullable<float>() }

    let readAggregate (reader: SqliteDataReader) : SeriesPoint =
        { Ts = reader.GetInt64 0
          ValueType = reader.GetString 1
          Value = typedValue reader 1 2 3 4 5
          Quality = if reader.IsDBNull 6 then Nullable<int64>() else Nullable(reader.GetInt64 6)
          Unit = dbString reader 7
          Count = reader.GetInt64 8
          Mean = dbFloat reader 9
          Min = dbFloat reader 10
          Max = dbFloat reader 11 }

    let execute (telemetryDb: string) (resolution: SeriesResolution) table limit : SeriesPoint list =
        use conn = new SqliteConnection($"Data Source={telemetryDb};Mode=ReadOnly;Pooling=False")
        conn.Open()
        use cmd = conn.CreateCommand()
        if table = "signals" then
            cmd.CommandText <-
                "SELECT source_ts_us, value_type, value_double, value_long, value_string, value_bool,
                        quality, unit
                 FROM signals
                 WHERE global_asset_id = $g AND signal_id = $s
                 ORDER BY source_ts_us DESC LIMIT $lim"
        else
            // COALESCE(last_double, last_v)는 typed schema 도입 전 생성된 double 집계도 읽기 위한 호환 경로다.
            cmd.CommandText <-
                sprintf
                    "SELECT bucket_ts_us, value_type, COALESCE(last_double, last_v), last_long,
                            last_string, last_bool, last_quality, unit, count, mean, min_v, max_v
                     FROM %s
                     WHERE global_asset_id = $g AND signal_id = $s
                     ORDER BY bucket_ts_us DESC LIMIT $lim" table
        cmd.Parameters.AddWithValue("$g", resolution.GlobalAssetId) |> ignore
        cmd.Parameters.AddWithValue("$s", resolution.SignalId) |> ignore
        cmd.Parameters.AddWithValue("$lim", limit) |> ignore
        use reader = cmd.ExecuteReader()
        [ while reader.Read() do
            yield if table = "signals" then readRaw reader else readAggregate reader ]

[<ApiController>]
[<Route("v1/series")>]
type SeriesController(registry: SeriesIdRegistry, paths: DataApiPaths) =
    inherit ControllerBase()

    [<HttpGet("catalog")>]
    member this.Catalog() : IActionResult =
        let items =
            registry.ListEntries()
            |> List.map (fun (seriesId, resolution) ->
                {| seriesId = seriesId
                   globalAssetId = resolution.GlobalAssetId
                   signalId = resolution.SignalId
                   defaultTable = resolution.DefaultTable
                   retention = resolution.Retention |> Option.toObj |})
        this.Ok({| count = List.length items; items = items |}) :> IActionResult

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
                let points = SeriesQuery.execute paths.TelemetryDb res table limit
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
