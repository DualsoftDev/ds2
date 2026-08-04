module Ds2.Collector.Tests.DownsampleTests

open System
open System.IO
open Ds2.Core
open Ds2.Adapter.Common
open Ds2.Collector.DataApi
open Ds2.Collector.Sinks
open Microsoft.Data.Sqlite
open Xunit

[<Fact>]
let ``series registry catalog exposes opaque ids in stable order`` () =
    let registry = SeriesIdRegistry()
    let resolution signal = {
        GlobalAssetId = "urn:x"
        SignalId = signal
        DefaultTable = "signals"
        Retention = Some "P30D"
    }
    registry.Register("series-b", resolution "line.b")
    registry.Register("series-a", resolution "line.a")

    let entries = registry.ListEntries()
    Assert.Equal<string list>([ "series-a"; "series-b" ], entries |> List.map fst)
    Assert.Equal("line.a", (entries |> List.head |> snd).SignalId)

[<Fact>]
let ``ensureSchema creates signals_1h and signals_1d tables`` () = task {
    let dir = Path.Combine(Path.GetTempPath(), "ds2-ds-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let telemetry = Path.Combine(dir, "telemetry.db")
    let events = Path.Combine(dir, "events.db")
    try
        let sink = SqliteSinkWriter(telemetry, events)
        Downsample.ensureSchema telemetry

        // Insert some samples across an hour.
        let baseNow = DateTimeOffset.UtcNow.AddMinutes(-30.0)
        let mkAt (offsetMin: int) (v: float) : Envelope =
            let ts = baseNow.AddMinutes(float offsetMin)
            { Envelope.NewSample(GlobalAssetId "urn:x", SignalId "line.a.b", ts, ValueDouble v, None, "t") with
                SourceTimestamp = ts }
        let envs = [ mkAt 0 1.0; mkAt 5 2.0; mkAt 15 3.0; mkAt 25 4.0 ]
        let! _ = sink.WriteBatchAsync envs

        let fromUs = baseNow.AddMinutes(-5.0).ToUnixTimeMilliseconds() * 1000L
        let toUs = baseNow.AddMinutes(35.0).ToUnixTimeMilliseconds() * 1000L
        let rows = Downsample.runAggregation telemetry fromUs toUs
        Assert.True(rows > 0, sprintf "expected downsample rows > 0, got %d" rows)
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)
}

[<Fact>]
let ``raw series query preserves bool long and string values`` () = task {
    let dir = Path.Combine(Path.GetTempPath(), "ds2-series-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let telemetry = Path.Combine(dir, "telemetry.db")
    let events = Path.Combine(dir, "events.db")
    try
        let sink = SqliteSinkWriter(telemetry, events)
        let timestamp = DateTimeOffset.UtcNow
        let sample signal value unit : Envelope =
            { Envelope.NewSample(GlobalAssetId "urn:x", SignalId signal, timestamp, value, unit, "test") with
                SourceTimestamp = timestamp
                StatusCode = 0x40000000u }
        let! inserted =
            sink.WriteBatchAsync
                [ sample "line.bool" (ValueBool true) None
                  sample "line.long" (ValueLong 42L) (Some "count")
                  sample "line.string" (ValueString "RUN") None ]
        Assert.Equal(3, inserted)

        let query signal =
            SeriesQuery.execute telemetry {
                GlobalAssetId = "urn:x"
                SignalId = signal
                DefaultTable = "signals"
                Retention = None
            } "signals" 10
            |> List.exactlyOne

        let boolPoint = query "line.bool"
        Assert.Equal("bool", boolPoint.ValueType)
        Assert.True(Assert.IsType<bool>(boolPoint.Value))
        Assert.Equal(0x40000000L, boolPoint.Quality.Value)

        let longPoint = query "line.long"
        Assert.Equal(42L, Assert.IsType<int64>(longPoint.Value))
        Assert.Equal("count", longPoint.Unit)

        let stringPoint = query "line.string"
        Assert.Equal("RUN", Assert.IsType<string>(stringPoint.Value))
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)
}

[<Fact>]
let ``downsample preserves typed last value and numeric statistics`` () = task {
    let dir = Path.Combine(Path.GetTempPath(), "ds2-typed-ds-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let telemetry = Path.Combine(dir, "telemetry.db")
    let events = Path.Combine(dir, "events.db")
    try
        let sink = SqliteSinkWriter(telemetry, events)
        Downsample.ensureSchema telemetry
        let now = DateTimeOffset.UtcNow
        let hourStart = DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero)
        let sample signal offset value unit quality : Envelope =
            let timestamp = hourStart.AddMinutes offset
            { Envelope.NewSample(GlobalAssetId "urn:x", SignalId signal, timestamp, value, unit, "test") with
                SourceTimestamp = timestamp
                StatusCode = quality }
        let! inserted =
            sink.WriteBatchAsync
                [ sample "line.bool" 5.0 (ValueBool false) None 0u
                  sample "line.bool" 10.0 (ValueBool true) None 0x40000000u
                  sample "line.long" 5.0 (ValueLong 10L) (Some "count") 0u
                  sample "line.long" 10.0 (ValueLong 20L) (Some "count") 0u
                  sample "line.string" 5.0 (ValueString "IDLE") None 0u
                  sample "line.string" 10.0 (ValueString "RUN") None 0u ]
        Assert.Equal(6, inserted)

        let fromUs = hourStart.ToUnixTimeMilliseconds() * 1000L
        let toUs = hourStart.AddMinutes(15.0).ToUnixTimeMilliseconds() * 1000L
        Assert.True(Downsample.runAggregation telemetry fromUs toUs >= 6)

        let query signal =
            SeriesQuery.execute telemetry {
                GlobalAssetId = "urn:x"
                SignalId = signal
                DefaultTable = "signals_1h"
                Retention = None
            } "signals_1h" 10
            |> List.exactlyOne

        let boolPoint = query "line.bool"
        Assert.Equal("bool", boolPoint.ValueType)
        Assert.True(Assert.IsType<bool>(boolPoint.Value))
        Assert.Equal(2L, boolPoint.Count)
        Assert.Equal(0.5, boolPoint.Mean.Value, 6)
        Assert.Equal(0x40000000L, boolPoint.Quality.Value)

        let longPoint = query "line.long"
        Assert.Equal(20L, Assert.IsType<int64>(longPoint.Value))
        Assert.Equal(15.0, longPoint.Mean.Value, 6)
        Assert.Equal("count", longPoint.Unit)

        let stringPoint = query "line.string"
        Assert.Equal("RUN", Assert.IsType<string>(stringPoint.Value))
        Assert.False(stringPoint.Mean.HasValue)
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)
}

[<Fact>]
let ``ensureSchema migrates legacy numeric aggregate tables`` () =
    let dir = Path.Combine(Path.GetTempPath(), "ds2-ds-migrate-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let telemetry = Path.Combine(dir, "telemetry.db")
    try
        use connection = new SqliteConnection($"Data Source={telemetry};Pooling=False")
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- """
            CREATE TABLE signals_1h (
                global_asset_id TEXT NOT NULL, signal_id TEXT NOT NULL, bucket_ts_us INTEGER NOT NULL,
                count INTEGER NOT NULL, mean REAL, min_v REAL, max_v REAL, last_v REAL,
                PRIMARY KEY (global_asset_id, signal_id, bucket_ts_us)
            ) WITHOUT ROWID;
            CREATE TABLE signals_1d (
                global_asset_id TEXT NOT NULL, signal_id TEXT NOT NULL, bucket_ts_us INTEGER NOT NULL,
                count INTEGER NOT NULL, mean REAL, min_v REAL, max_v REAL, last_v REAL,
                PRIMARY KEY (global_asset_id, signal_id, bucket_ts_us)
            ) WITHOUT ROWID;
        """
        command.ExecuteNonQuery() |> ignore
        connection.Close()

        Downsample.ensureSchema telemetry

        use migrated = new SqliteConnection($"Data Source={telemetry};Pooling=False")
        migrated.Open()
        use schema = migrated.CreateCommand()
        schema.CommandText <- "PRAGMA table_info(signals_1h)"
        use reader = schema.ExecuteReader()
        let columns = [ while reader.Read() do yield reader.GetString 1 ] |> Set.ofList
        Assert.Contains("value_type", columns)
        Assert.Contains("last_bool", columns)
        Assert.Contains("last_quality", columns)
    finally
        if Directory.Exists dir then Directory.Delete(dir, true)
