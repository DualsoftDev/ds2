/// 성능 회귀 — 큰 모델 / 많은 cycle 에서 알고리즘이 timeout 안 남.
module Ds2.Reverse.Tests.PerformanceTests

open Xunit
open System
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

/// N 노드 chain + M cycle 시뮬 후 reverse 시간 측정.
let private timeReverse (nNodes: int) (nCycles: int) : int64 =
    let flow = "F"
    let names = [ for i in 1 .. nNodes -> $"N{i}" ]
    let arrows : VLine.GroundTruthArrow list =
        [ for i in 0 .. nNodes - 2 ->
            { Src = $"{flow}.{names.[i]}"
              Tgt = $"{flow}.{names.[i + 1]}"
              Kind = "Start" } ]
    let nodes = names |> List.map (fun n -> $"{flow}.{n}")
    let pattern (rng: Random) : Simulator.CyclePattern =
        { Offsets = [ for i in 0 .. nNodes - 1 ->
                        int64 i * 200L + int64 (rng.Next(-10, 11)),
                        $"{flow}.{names.[i]}" ]
          Jitter = 10L }
    let sc : Scenario = {
        Name = sprintf "perf_n%d_c%d" nNodes nCycles
        Flow = flow
        GroundTruth = arrows
        Spurious = []
        AllCalls = nodes
        Pattern = pattern
        PatternCycleAware = None
        CycleMs = int64 nNodes * 200L + 500L
    }
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let _ = BenchRunner.runOne sc CausationConfig.defaults 42 nCycles
    sw.Stop()
    sw.ElapsedMilliseconds

[<Theory>]
[<InlineData(10, 50)>]
[<InlineData(25, 50)>]
[<InlineData(50, 50)>]
[<InlineData(100, 30)>]
let ``Performance: chain N nodes within 2 seconds`` (n: int) (cycles: int) =
    let ms = timeReverse n cycles
    Assert.True(ms < 2000L,
        sprintf "chain N=%d cycles=%d took %dms (budget 2000ms)" n cycles ms)

[<Fact>]
let ``Performance: 100 cycle chain N=20 within 1 second`` () =
    let ms = timeReverse 20 100
    Assert.True(ms < 1000L,
        sprintf "chain N=20 cycles=100 took %dms (budget 1000ms)" ms)

[<Fact>]
let ``Performance: all-phase sweep within 5 seconds`` () =
    let all =
        Phase1Models.all @
        Phase2Models.all @
        Phase3Models.all
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let _, _ = BenchRunner.runAll all CausationConfig.defaults 42 60
    sw.Stop()
    Assert.True(sw.ElapsedMilliseconds < 5000L,
        sprintf "28 scenarios × 60 cycles took %dms (budget 5000ms)"
            sw.ElapsedMilliseconds)
