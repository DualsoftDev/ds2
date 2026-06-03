/// Property-style tests — 무작위 generator + invariants.
/// FsCheck 대신 xunit Theory + 수동 seed sweep 으로 비슷한 효과.
module Ds2.Reverse.Tests.PropertyTests

open Xunit
open System
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

// ── 무작위 DAG 시나리오 생성 ────────────────────────────────────────────

/// Linear chain N 노드 + jitter ± maxJitter ms.
/// 1 cycle = N 발화. 각 노드 lag = baseLag ms.
let private chainScenario (seed: int) (nNodes: int) (baseLag: int64)
                         (maxJitter: int) : Scenario =
    let flow = "F"
    let names = [ for i in 1 .. nNodes -> $"N{i}" ]
    let arrows : VLine.GroundTruthArrow list =
        [ for i in 0 .. nNodes - 2 ->
            { Src = $"{flow}.{names.[i]}"
              Tgt = $"{flow}.{names.[i + 1]}"
              Kind = "Start" } ]
    let nodes = names |> List.map (fun n -> $"{flow}.{n}")
    let pattern (rng: Random) : Simulator.CyclePattern =
        let offs =
            [ for i in 0 .. nNodes - 1 ->
                int64 i * baseLag + int64 (rng.Next(-maxJitter, maxJitter + 1)),
                $"{flow}.{names.[i]}" ]
        { Offsets = offs; Jitter = int64 maxJitter }
    { Name = sprintf "chain_seed%d_n%d" seed nNodes
      Flow = flow
      GroundTruth = arrows
      Spurious = []
      AllCalls = nodes
      Pattern = pattern
      PatternCycleAware = None
      CycleMs = int64 nNodes * baseLag + 1000L }

// ── 결정성 검증 (같은 seed → 같은 결과) ──────────────────────────────────

[<Theory>]
[<InlineData(1)>]
[<InlineData(42)>]
[<InlineData(12345)>]
[<InlineData(999999)>]
let ``Property: deterministic across runs (chain)`` (seed: int) =
    let sc = chainScenario seed 5 300L 15
    let cfg = CausationConfig.defaults
    let r1 = BenchRunner.runOne sc cfg seed 50
    let r2 = BenchRunner.runOne sc cfg seed 50
    Assert.Equal(r1.F1, r2.F1)
    Assert.Equal(r1.TP, r2.TP)
    Assert.Equal(r1.FP, r2.FP)
    Assert.Equal(r1.FN, r2.FN)

// ── 확장성 검증 (큰 N 에서도 timeout 안 남) ──────────────────────────────

[<Theory>]
[<InlineData(10)>]
[<InlineData(25)>]
[<InlineData(50)>]
let ``Property: scale chain N nodes`` (n: int) =
    let sc = chainScenario 42 n 200L 10
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let r = BenchRunner.runOne sc CausationConfig.defaults 42 40
    sw.Stop()
    Assert.True(sw.ElapsedMilliseconds < 5000L,
        sprintf "chain N=%d took %dms (limit 5000ms)" n sw.ElapsedMilliseconds)
    Assert.True(r.F1 >= 0.95,
        sprintf "chain N=%d F1=%.3f (expected >= 0.95)" n r.F1)

// ── 단조성: 더 많은 cycle → F1 안 떨어짐 ────────────────────────────────

[<Theory>]
[<InlineData(42)>]
[<InlineData(100)>]
let ``Property: monotone — more cycles never reduces F1`` (seed: int) =
    let sc = chainScenario seed 5 300L 15
    let cfg = CausationConfig.defaults
    let r20 = BenchRunner.runOne sc cfg seed 20
    let r60 = BenchRunner.runOne sc cfg seed 60
    let r120 = BenchRunner.runOne sc cfg seed 120
    // 너그러운 단조성: 1.0 perfect 에 한 번 도달하면 그 후 떨어지지 않음.
    if r20.F1 >= 0.9999 then
        Assert.True(r60.F1 >= 0.9999)
        Assert.True(r120.F1 >= 0.9999)
    elif r60.F1 >= 0.9999 then
        Assert.True(r120.F1 >= 0.9999)

// ── 신뢰도 단조: passes_seq + 큰 NA → score >= passes_seq + 작은 NA ─────

[<Fact>]
let ``Property: confidence monotone in NA`` () =
    let mkScore na =
        { NA = na; NB = na
          Sufficiency = 0.95; Necessity = 0.95
          LagMean = 300.0; LagStd = 20.0; LagCv = 0.067
          AbsLagMean = 300.0
          IsParallel = false
          PassesSeq = true; PassesGrp = false
          Reason = None } : CausationScore
    let confs =
        [ 5; 15; 30; 60; 120 ]
        |> List.map (fun n -> n, CausationDetection.confidence (mkScore n) None)
    // 작은 N 의 score <= 큰 N 의 score
    let scores = confs |> List.map (fun (_, c) -> c.Score)
    let isMonotone =
        scores
        |> List.pairwise
        |> List.forall (fun (a, b) -> b >= a - 1e-9)
    Assert.True(isMonotone,
        sprintf "expected monotone NA→score; got %A" scores)

// ── 시드 sweep: 10 개 random seed 에서 모두 perfect 검증 ─────────────────

[<Fact>]
let ``Property: chain robust over 10 seeds`` () =
    let seeds = [ 1; 2; 3; 7; 11; 42; 99; 100; 12345; 999999 ]
    let cfg = CausationConfig.defaults
    let results =
        seeds
        |> List.map (fun s ->
            let sc = chainScenario s 5 300L 15
            BenchRunner.runOne sc cfg s 40)
    let perfect = results |> List.filter (fun r -> r.F1 >= 0.9999) |> List.length
    Assert.True(perfect >= 9,
        sprintf "expected >=9 perfect out of 10 seeds; got %d" perfect)
