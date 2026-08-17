namespace Ds2.Collector.DataApi

open System
open System.Collections.Concurrent
open System.IO
open Microsoft.Data.Sqlite

/// Collector 프로세스가 소유하는 telemetry / events DB 경로.
type DataApiPaths = {
    TelemetryDb : string
    EventsDb    : string
}

/// SeriesId 해석 결과: SQLite 조회 명세.
type SeriesResolution = {
    GlobalAssetId : string
    SignalId      : string
    DefaultTable  : string   // "signals" | "signals_1h" | "signals_1d"
    Retention     : string option
}

/// ADR-011 · SeriesId Registry — TimeSeries LinkedSegment 의 seriesId 를
/// SQLite 조회 명세로 매핑. UA 주소공간에서 발견한 AID/Runtime 신호를 메모리와
/// SQLite에 함께 유지하며 모델 교체 시 stale mapping을 제거한다.
type SeriesIdRegistry(?databasePath: string) =
    let store = ConcurrentDictionary<string, SeriesResolution>()
    let gate = obj()
    let persistentPath =
        databasePath
        |> Option.bind (fun path -> if String.IsNullOrWhiteSpace path then None else Some(Path.GetFullPath path))

    let openDatabase path =
        let connection = new SqliteConnection($"Data Source={path};Pooling=False;Default Timeout=5")
        connection.Open()
        connection

    let addParameters (command: SqliteCommand) seriesId (resolution: SeriesResolution) =
        command.Parameters.AddWithValue("$series_id", seriesId) |> ignore
        command.Parameters.AddWithValue("$global_asset_id", resolution.GlobalAssetId) |> ignore
        command.Parameters.AddWithValue("$signal_id", resolution.SignalId) |> ignore
        command.Parameters.AddWithValue("$default_table", resolution.DefaultTable) |> ignore
        command.Parameters.AddWithValue(
            "$retention",
            resolution.Retention |> Option.map box |> Option.defaultValue (box DBNull.Value)) |> ignore

    let upsert (connection: SqliteConnection) (transaction: SqliteTransaction option) seriesId resolution =
        use command = connection.CreateCommand()
        transaction |> Option.iter (fun tx -> command.Transaction <- tx)
        command.CommandText <-
            "INSERT INTO series_registry (series_id, global_asset_id, signal_id, default_table, retention)
             VALUES ($series_id, $global_asset_id, $signal_id, $default_table, $retention)
             ON CONFLICT(series_id) DO UPDATE SET
               global_asset_id = excluded.global_asset_id,
               signal_id = excluded.signal_id,
               default_table = excluded.default_table,
               retention = excluded.retention"
        addParameters command seriesId resolution
        command.ExecuteNonQuery() |> ignore

    do
        match persistentPath with
        | None -> ()
        | Some path ->
            let parent = Path.GetDirectoryName path
            if not (String.IsNullOrWhiteSpace parent) then Directory.CreateDirectory parent |> ignore
            use connection = openDatabase path
            use schema = connection.CreateCommand()
            schema.CommandText <-
                "PRAGMA journal_mode=WAL;
                 CREATE TABLE IF NOT EXISTS series_registry (
                   series_id TEXT PRIMARY KEY,
                   global_asset_id TEXT NOT NULL,
                   signal_id TEXT NOT NULL,
                   default_table TEXT NOT NULL,
                   retention TEXT
                 ) WITHOUT ROWID;"
            schema.ExecuteNonQuery() |> ignore
            use query = connection.CreateCommand()
            query.CommandText <-
                "SELECT series_id, global_asset_id, signal_id, default_table, retention FROM series_registry"
            use reader = query.ExecuteReader()
            while reader.Read() do
                store.[reader.GetString 0] <- {
                    GlobalAssetId = reader.GetString 1
                    SignalId = reader.GetString 2
                    DefaultTable = reader.GetString 3
                    Retention = if reader.IsDBNull 4 then None else Some(reader.GetString 4)
                }

    member _.Register(seriesId: string, res: SeriesResolution) =
        if String.IsNullOrWhiteSpace seriesId then invalidArg (nameof seriesId) "seriesId must not be empty"
        lock gate (fun () ->
            persistentPath
            |> Option.iter (fun path ->
                use connection = openDatabase path
                upsert connection None seriesId res)
            store.[seriesId] <- res)

    /// Replaces the discovered catalog and removes stale mappings that no
    /// longer exist in the active OPC UA model.
    member _.ReplaceAll(entries: seq<string * SeriesResolution>) =
        let snapshot = entries |> Seq.toArray
        if snapshot |> Array.exists (fst >> String.IsNullOrWhiteSpace) then
            invalidArg (nameof entries) "seriesId must not be empty"
        lock gate (fun () ->
            persistentPath
            |> Option.iter (fun path ->
                use connection = openDatabase path
                use transaction = connection.BeginTransaction()
                use clear = connection.CreateCommand()
                clear.Transaction <- transaction
                clear.CommandText <- "DELETE FROM series_registry"
                clear.ExecuteNonQuery() |> ignore
                for seriesId, resolution in snapshot do
                    upsert connection (Some transaction) seriesId resolution
                transaction.Commit())
            store.Clear()
            for seriesId, resolution in snapshot do store.[seriesId] <- resolution)

    member _.Resolve(seriesId: string) : SeriesResolution option =
        match store.TryGetValue seriesId with
        | true, value -> Some value
        | _ -> None

    member _.ListAll() : SeriesResolution list =
        store.Values |> Seq.toList

    member _.ListEntries() : (string * SeriesResolution) list =
        store
        |> Seq.map (fun entry -> entry.Key, entry.Value)
        |> Seq.sortBy fst
        |> Seq.toList

/// Range 크기에 따라 signals / signals_1h / signals_1d 자동 선택.
module TableSelector =
    let pickForRange (rangeSeconds: float) : string =
        if rangeSeconds <= 3600.0 then "signals"
        elif rangeSeconds <= 30.0 * 86400.0 then "signals_1h"
        else "signals_1d"
