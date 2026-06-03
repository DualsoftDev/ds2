/// FP. Fuzz Pipeline — 전체 ReverseEngine 파이프라인 무작위 검증.
module Ds2.Reverse.Tests.Fuzz.FuzzPipelineTests

open System
open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

let private cfg = CausationConfig.defaults

[<Fact>]
let ``FP: 100 random scenarios — crash 0, avg time < 50ms`` () =
    let rng = Random(42)
    let mutable sumMs = 0L
    let mutable crashes = 0
    for _ in 1 .. 100 do
        let spec = RandomScenarioGen.random rng
        try
            let scen = RandomScenarioGen.toScenario spec
            let sw = Diagnostics.Stopwatch.StartNew()
            let _r = BenchRunner.runOne scen cfg spec.Seed (min 30 spec.NCycles)
            sw.Stop()
            sumMs <- sumMs + sw.ElapsedMilliseconds
        with _ ->
            crashes <- crashes + 1
    Assert.Equal(0, crashes)
    let avg = float sumMs / 100.0
    Assert.True(avg < 50.0, sprintf "avg %.1fms > 50ms" avg)

[<Fact>]
let ``FP: ReverseEngine.run 직접 — 100 random + autoTune 모두 정상 동작`` () =
    let rng = Random(42)
    let mutable crashes = 0
    for _ in 1 .. 100 do
        let spec = RandomScenarioGen.random rng
        try
            let scen = RandomScenarioGen.toScenario spec
            let events = Simulator.simulate spec.Seed scen.CycleMs 30 scen.Pattern
            let flowCalls =
                Map.ofList [ scen.Flow, scen.AllCalls |> List.map (fun n -> n, "") ]
            let cands =
                scen.GroundTruth |> List.map (fun a ->
                    { Src = a.Src; Tgt = a.Tgt; DeclaredKind = "trigger" })
            let baseInp =
                ReverseEngine.mkInput "P" "S" flowCalls cands events
                    (CausationConfig.withCycleHint scen.CycleMs cfg)
            let inp = { baseInp with AutoTuneThreshold = true }
            let _store, _report = ReverseEngine.run inp
            ()
        with _ ->
            crashes <- crashes + 1
    Assert.Equal(0, crashes)

[<Fact>]
let ``FP: Online + Anomaly + Confidence 모듈 random input 에서 crash 0`` () =
    let rng = Random(42)
    let mutable crashes = 0
    for _ in 1 .. 50 do
        let spec = RandomScenarioGen.random rng
        try
            let scen = RandomScenarioGen.toScenario spec
            let events = Simulator.simulate spec.Seed scen.CycleMs 30 scen.Pattern
            let evPairs = events |> List.map (fun e -> e.T, e.Name)

            // Online
            let online = OnlineDetection.OnlineScore()
            online.SetWindow scen.CycleMs
            for (t, name) in evPairs do
                if name.EndsWith ".N0" then online.AddA t
                elif name.EndsWith ".N1" then online.AddB t
            let _ = online.Snapshot()

            // Anomaly
            let pattern = AnomalyDetection.learn evPairs scen.CycleMs 10
            let _ = AnomalyDetection.analyzeAllCycles pattern evPairs scen.CycleMs 3.0
            ()
        with _ ->
            crashes <- crashes + 1
    Assert.Equal(0, crashes)

[<Fact>]
let ``FP: 시나리오마다 confidence 분포 0~1 안 (50 random)`` () =
    let rng = Random(123)
    for _ in 1 .. 50 do
        let spec = RandomScenarioGen.random rng
        let scen = RandomScenarioGen.toScenario spec
        let r = BenchRunner.runOne scen cfg spec.Seed (min 30 spec.NCycles)
        for (_, _, conf) in r.Report.EmittedConfidence do
            Assert.True(conf.Score >= 0.0 && conf.Score <= 1.0,
                sprintf "out of range: %.4f" conf.Score)

[<Fact>]
let ``FP: Failure recorder write + load round-trip`` () =
    let tmpPath = System.IO.Path.GetTempFileName()
    try
        let r1 = { Seed = 100; Description = "test"; F1 = 0.5
                   Detected = 3; Truth = 5; TimestampUtc = DateTime.UtcNow }
        let r2 = { Seed = 200; Description = "test2"; F1 = 0.3
                   Detected = 1; Truth = 4; TimestampUtc = DateTime.UtcNow }
        FailureRecorder.record tmpPath r1
        FailureRecorder.record tmpPath r2
        let loaded = FailureRecorder.load tmpPath
        Assert.Equal(2, List.length loaded)
        let seeds = loaded |> List.map (fun r -> r.Seed) |> Set.ofList
        Assert.True(Set.contains 100 seeds)
        Assert.True(Set.contains 200 seeds)
    finally
        if System.IO.File.Exists tmpPath then System.IO.File.Delete tmpPath
