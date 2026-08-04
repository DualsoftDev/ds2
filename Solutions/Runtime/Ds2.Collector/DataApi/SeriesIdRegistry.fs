namespace Ds2.Collector.DataApi

open System.Collections.Concurrent

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
/// SQLite 조회 명세로 매핑. 실제 매핑은 후속 phase 에서 AasHost TimeSeries SM
/// scan 으로 채워질 예정. 여기서는 in-memory registry 만.
type SeriesIdRegistry() =
    let store = ConcurrentDictionary<string, SeriesResolution>()

    member _.Register(seriesId: string, res: SeriesResolution) =
        store.[seriesId] <- res

    member _.Resolve(seriesId: string) : SeriesResolution option =
        match store.TryGetValue seriesId with
        | true, v -> Some v
        | _ -> None

    member _.ListAll() : SeriesResolution list =
        store.Values |> Seq.toList

    /// Data API diagnostics/discovery. Series identifiers are opaque to callers,
    /// so expose their registered resolution instead of forcing clients to guess.
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
