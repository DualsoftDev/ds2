module Ds2.Reverse.Tests.ClusterTests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``Cluster scenarios — multi-source causation`` () =
    let summary, _ = BenchRunner.runAll ClusterModels.all CausationConfig.defaults 20260519 60
    printfn "%s" (BenchRunner.formatSummary summary)
    Assert.True(summary.AvgF1 >= 0.85,
        sprintf "expected avgF1 >= 0.85; got %.4f (perfect %d/%d)"
            summary.AvgF1 summary.Perfect summary.Total)

[<Fact>]
let ``clusterScore — basic correctness`` () =
    // A1 fires 30, A2 fires 30, B fires 30 (alternating A1/A2 → B)
    let a1Times = [| for i in 0 .. 29 -> int64 i * 200L |]   // 0, 200, 400, ...
    let a2Times = [| for i in 0 .. 29 -> int64 i * 200L + 1000000L |]   // far future, no overlap
    let bTimes = [| for i in 0 .. 29 -> int64 i * 200L + 50L |]   // 50ms after each A1

    let cfg = CausationConfig.defaults |> CausationConfig.withCycleHint 2000L
    let scores =
        CausationDetection.clusterScore cfg
            [ "A1", a1Times :> seq<_>; "A2", a2Times :> seq<_> ]
            (bTimes :> seq<_>)

    let a1Score = Map.find "A1" scores
    Assert.Equal(30, a1Score.ClusterSize)
    Assert.True(a1Score.Suff >= 0.9, sprintf "A1 suff %.2f" a1Score.Suff)
    Assert.True(a1Score.PassesSeq, "A1 should pass")

    let a2Score = Map.find "A2" scores
    Assert.True(a2Score.ClusterSize < 10,
        sprintf "A2 cluster should be small; got %d" a2Score.ClusterSize)
    Assert.False(a2Score.PassesSeq, "A2 should fail")
