namespace Ds2.Collector.DataApi

open System
open System.Text.Json.Serialization
open Microsoft.AspNetCore.Mvc
open Microsoft.Data.Sqlite
open Ds2.Adapter.Common
open Ds2.Collector

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

    let execute
        (telemetryDb: string)
        (resolution: SeriesResolution)
        table
        (fromUs: int64)
        (toUs: int64)
        limit : SeriesPoint list =
        use conn = new SqliteConnection($"Data Source={telemetryDb};Mode=ReadOnly;Pooling=False")
        conn.Open()
        use cmd = conn.CreateCommand()
        if table = "signals" then
            cmd.CommandText <-
                "SELECT source_ts_us, value_type, value_double, value_long, value_string, value_bool,
                        quality, unit
                 FROM signals
                 WHERE global_asset_id = $g AND signal_id = $s
                   AND source_ts_us BETWEEN $from AND $to
                 ORDER BY source_ts_us DESC LIMIT $lim"
        else
            // COALESCE(last_double, last_v)는 typed schema 도입 전 생성된 double 집계도 읽기 위한 호환 경로다.
            cmd.CommandText <-
                sprintf
                    "SELECT bucket_ts_us, value_type, COALESCE(last_double, last_v), last_long,
                            last_string, last_bool, last_quality, unit, count, mean, min_v, max_v
                     FROM %s
                     WHERE global_asset_id = $g AND signal_id = $s
                       AND bucket_ts_us BETWEEN $from AND $to
                     ORDER BY bucket_ts_us DESC LIMIT $lim" table
        cmd.Parameters.AddWithValue("$g", resolution.GlobalAssetId) |> ignore
        cmd.Parameters.AddWithValue("$s", resolution.SignalId) |> ignore
        cmd.Parameters.AddWithValue("$from", fromUs) |> ignore
        cmd.Parameters.AddWithValue("$to", toUs) |> ignore
        cmd.Parameters.AddWithValue("$lim", limit) |> ignore
        use reader = cmd.ExecuteReader()
        [ while reader.Read() do
            yield if table = "signals" then readRaw reader else readAggregate reader ]

[<ApiController>]
[<Route("v1/series")>]
type SeriesController(registry: SeriesIdRegistry, paths: DataApiPaths) =
    inherit ControllerBase()

    [<HttpGet("catalog")>]
    member this.Catalog(
        [<FromQuery>] afterSeriesId: string,
        [<FromQuery>] pageSize: int) : IActionResult =
        let limit = if pageSize <= 0 then 500 else min pageSize 1000
        let remaining =
            registry.ListEntries()
            |> List.filter (fun (seriesId, _) ->
                String.IsNullOrWhiteSpace afterSeriesId
                || String.CompareOrdinal(seriesId, afterSeriesId) > 0)
        let page = remaining |> List.truncate (limit + 1)
        let hasMore = List.length page > limit
        let selected = page |> List.truncate limit
        let items =
            selected
            |> List.map (fun (seriesId, resolution) ->
                {| seriesId = seriesId
                   globalAssetId = resolution.GlobalAssetId
                   signalId = resolution.SignalId
                   defaultTable = resolution.DefaultTable
                   retention = resolution.Retention |> Option.toObj |})
        let nextCursor =
            if hasMore then selected |> List.tryLast |> Option.map fst |> Option.toObj
            else null
        this.Ok({| count = List.length items; nextCursor = nextCursor; items = items |}) :> IActionResult

    [<HttpGet>]
    member this.Get(
        [<FromQuery>] seriesId: string,
        [<FromQuery>] rangeSeconds: float,
        [<FromQuery>] maxPoints: int,
        [<FromQuery>] fromUs: Nullable<int64>,
        [<FromQuery>] toUs: Nullable<int64>) : IActionResult =
        if String.IsNullOrWhiteSpace seriesId then
            this.BadRequest("seriesId required") :> IActionResult
        elif (fromUs.HasValue && fromUs.Value < 0L) || (toUs.HasValue && toUs.Value < 0L) then
            this.BadRequest("fromUs/toUs must be non-negative Unix microseconds") :> IActionResult
        else
            match registry.Resolve seriesId with
            | None -> this.NotFound({| seriesId = seriesId; reason = "unknown seriesId" |}) :> IActionResult
            | Some res ->
                let effectiveRangeSeconds =
                    if Double.IsNaN rangeSeconds || Double.IsInfinity rangeSeconds || rangeSeconds <= 0.0 then 3600.0
                    else rangeSeconds
                let endUs =
                    if toUs.HasValue then toUs.Value
                    else UnixTime.toMicroseconds DateTimeOffset.UtcNow
                let requestedSpanUs =
                    let requested = effectiveRangeSeconds * 1_000_000.0
                    if requested >= float Int64.MaxValue then Int64.MaxValue else int64 requested
                let startUs =
                    if fromUs.HasValue then fromUs.Value
                    elif requestedSpanUs > endUs && endUs >= 0L then 0L
                    else endUs - requestedSpanUs
                if startUs > endUs then
                    this.BadRequest("fromUs must be less than or equal to toUs") :> IActionResult
                else
                    let actualRangeSeconds = float (endUs - startUs) / 1_000_000.0
                    let table = TableSelector.pickForRange actualRangeSeconds
                    let limit = if maxPoints <= 0 then 10000 else min maxPoints 10000
                    let points = SeriesQuery.execute paths.TelemetryDb res table startUs endUs limit
                    let response = {|
                        seriesId = seriesId
                        table = table
                        globalAssetId = res.GlobalAssetId
                        signalId = res.SignalId
                        fromUs = startUs
                        toUs = endUs
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
                    [<FromQuery>] pageSize: int,
                    [<FromQuery>] fromUs: Nullable<int64>,
                    [<FromQuery>] toUs: Nullable<int64>,
                    [<FromQuery>] beforeTsUs: Nullable<int64>,
                    [<FromQuery>] beforeId: Nullable<int64>) : IActionResult =
        if String.IsNullOrWhiteSpace asset then
            this.BadRequest("asset required") :> IActionResult
        elif (fromUs.HasValue && fromUs.Value < 0L)
             || (toUs.HasValue && toUs.Value < 0L)
             || (fromUs.HasValue && toUs.HasValue && fromUs.Value > toUs.Value) then
            this.BadRequest("fromUs/toUs must be non-negative and ordered") :> IActionResult
        elif beforeTsUs.HasValue <> beforeId.HasValue then
            this.BadRequest("beforeTsUs and beforeId must be supplied together") :> IActionResult
        elif (beforeTsUs.HasValue && beforeTsUs.Value < 0L)
             || (beforeId.HasValue && beforeId.Value <= 0L) then
            this.BadRequest("beforeTsUs must be non-negative and beforeId must be positive") :> IActionResult
        else
            let limit = if pageSize <= 0 then 100 else min pageSize 100
            use conn = new SqliteConnection($"Data Source={paths.EventsDb};Mode=ReadOnly;Pooling=False")
            conn.Open()
            use cmd = conn.CreateCommand()
            let whereEventType = if String.IsNullOrWhiteSpace eventType then "" else " AND event_type_semantic_id = $et"
            let whereCursor =
                if beforeTsUs.HasValue then
                    " AND (source_ts_us < $beforeTs OR (source_ts_us = $beforeTs AND id < $beforeId))"
                else ""
            cmd.CommandText <-
                sprintf "SELECT id, envelope_id, source_ts_us, event_type_semantic_id, payload
                         FROM events
                         WHERE global_asset_id = $g
                           AND source_ts_us BETWEEN $from AND $to %s %s
                         ORDER BY source_ts_us DESC, id DESC LIMIT $lim" whereEventType whereCursor
            cmd.Parameters.AddWithValue("$g", asset) |> ignore
            cmd.Parameters.AddWithValue("$from", if fromUs.HasValue then fromUs.Value else 0L) |> ignore
            cmd.Parameters.AddWithValue("$to", if toUs.HasValue then toUs.Value else Int64.MaxValue) |> ignore
            if not (String.IsNullOrWhiteSpace eventType) then
                cmd.Parameters.AddWithValue("$et", eventType) |> ignore
            if beforeTsUs.HasValue then
                cmd.Parameters.AddWithValue("$beforeTs", beforeTsUs.Value) |> ignore
                cmd.Parameters.AddWithValue("$beforeId", beforeId.Value) |> ignore
            cmd.Parameters.AddWithValue("$lim", limit) |> ignore
            use reader = cmd.ExecuteReader()
            let items =
                [ while reader.Read() do
                    yield {| id = reader.GetInt64 0
                             envelopeId = Guid(reader.GetFieldValue<byte[]> 1)
                             sourceTsUs = reader.GetInt64 2
                             eventType = reader.GetString 3
                             payload = reader.GetString 4 |} ]
            let nextCursor =
                items
                |> List.tryLast
                |> Option.map (fun item -> {| beforeTsUs = item.sourceTsUs; beforeId = item.id |})
                |> Option.toObj
            this.Ok({| count = List.length items; nextCursor = nextCursor; items = items |}) :> IActionResult

[<ApiController>]
type InfoController(runtimeState: CollectorRuntimeState, outbox: SqliteEdgeBuffer) =
    inherit ControllerBase()

    [<HttpGet("/healthz")>]
    member this.Healthz() : IActionResult =
        this.Ok({| status = "ok" |}) :> IActionResult

    [<HttpGet("/readyz")>]
    member this.Readyz() : IActionResult =
        let snapshot = runtimeState.Snapshot(outbox.PendingCount())
        let response = {| status = (if snapshot.Ready then "ready" else "not_ready"); ready = snapshot.Ready |}
        if snapshot.Ready then this.Ok(response) :> IActionResult
        else this.StatusCode(503, response) :> IActionResult

    [<HttpGet("/v1/info")>]
    member _.Info() : obj =
        let snapshot = runtimeState.Snapshot(outbox.PendingCount())
        let pendingRows, pendingPayloadBytes = outbox.PendingUsage()
        {| service = "Ds2.Collector"
           storage = "SQLite (ADR-011)"
           delivery = "durable outbox + at-least-once + sink dedup"
           pendingEnvelopes = snapshot.PendingEnvelopes
           pendingRows = pendingRows
           pendingPayloadBytes = pendingPayloadBytes
           outboxMaximumRows = outbox.MaximumRows
           outboxMaximumPayloadBytes = outbox.MaximumPayloadBytes |} :> obj
