module Ds2.Reverse.Tests.BenchTests

open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``m0~m100 bench — perfect score expected`` () =
    let summary, _ = BenchRunner.runAll Models.all CausationConfig.defaults 20260519 60
    printfn "=== Category stats ==="
    for (cat, n) in Models.stats () do
        printfn "  %s: %d" cat n
    printfn ""
    printfn "%s" (BenchRunner.formatSummary summary)
    Assert.True(summary.Total >= 100, sprintf "expected ~101 scenarios; got %d" summary.Total)
    Assert.Equal(summary.Total, summary.Perfect)
    Assert.Equal(1.0, summary.AvgF1)
    Assert.Equal(0, summary.TotalFp)
    Assert.Equal(0, summary.TotalFn)

[<Fact>]
let ``m0~m100 bench — perfect rate >= 0.90`` () =
    let summary, _ = BenchRunner.runAll Models.all CausationConfig.defaults 20260519 60
    let perfectRate = float summary.Perfect / float summary.Total
    printfn "Perfect %d/%d = %.2f%%" summary.Perfect summary.Total (perfectRate * 100.0)
    Assert.True(perfectRate >= 0.90,
        sprintf "expected >=90%% perfect; got %.2f%% (%d/%d)"
            (perfectRate * 100.0) summary.Perfect summary.Total)

[<Fact>]
let ``m0~m100 bench — robust across multiple seeds`` () =
    // 5개 다른 seed 로 회귀 — 알고리즘이 시드 의존성 없어야 함
    let seeds = [ 20260519; 1; 42; 12345; 999999 ]
    let avgF1s =
        seeds
        |> List.map (fun s ->
            let summary, _ = BenchRunner.runAll Models.all CausationConfig.defaults s 60
            printfn "  seed=%d → perfect %d/%d, avgF1=%.4f"
                s summary.Perfect summary.Total summary.AvgF1
            summary.AvgF1)
    let minF1 = List.min avgF1s
    Assert.True(minF1 >= 0.99,
        sprintf "expected min avgF1 >= 0.99 across seeds; got %.4f" minF1)

[<Fact>]
let ``m0~m100 bench — robust across cycle counts`` () =
    // 적은 사이클 (20) 부터 많은 사이클 (200) 까지
    let cycles = [ 20; 40; 60; 100; 200 ]
    let avgF1s =
        cycles
        |> List.map (fun n ->
            let summary, _ = BenchRunner.runAll Models.all CausationConfig.defaults 20260519 n
            printfn "  nCycles=%d → perfect %d/%d, avgF1=%.4f"
                n summary.Perfect summary.Total summary.AvgF1
            summary.AvgF1)
    let minF1 = List.min avgF1s
    Assert.True(minF1 >= 0.95,
        sprintf "expected min avgF1 >= 0.95 across cycles; got %.4f" minF1)
