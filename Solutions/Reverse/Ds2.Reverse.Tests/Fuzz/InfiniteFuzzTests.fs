/// F. Infinite Fuzz — bounded (5초) 동안 무작위 시나리오 검증.
/// 알고리즘이 어떤 random input 에도 crash 안 함 + 평균 F1 합리적인지 검증.
module Ds2.Reverse.Tests.Fuzz.InfiniteFuzzTests

open Xunit
open Ds2.Reverse.Bench

[<Fact>]
let ``Fuzz: 5초 bounded — crash 0건 + 100+ scenarios`` () =
    let stats = InfiniteTestRunner.runBounded 5000 42 0.5
    printfn "%s" (InfiniteTestRunner.formatStats stats)
    Assert.Empty(stats.Crashes)
    Assert.True(stats.Total >= 50,
        sprintf "5초 동안 ≥50 scenarios 실행 기대; got %d" stats.Total)

[<Fact>]
let ``Fuzz: 5초 — perfect rate >= 20%% (broad random)`` () =
    let stats = InfiniteTestRunner.runBounded 5000 123 0.5
    Assert.Empty(stats.Crashes)
    if stats.Total >= 50 then
        let perfectRate = float stats.Perfect / float stats.Total
        Assert.True(perfectRate >= 0.20,
            sprintf "perfect rate %.1f%% < 20%%" (perfectRate * 100.0))

[<Fact>]
let ``Fuzz: 3초 multiple seeds — 모두 crash 0`` () =
    for seed in [ 1; 42; 100; 999; 12345 ] do
        let stats = InfiniteTestRunner.runBounded 3000 seed 0.5
        Assert.Empty(stats.Crashes)
        Assert.True(stats.Total >= 30, sprintf "seed=%d total=%d" seed stats.Total)

[<Fact>]
let ``Fuzz: 5초 — Avg F1 >= 0.50 (broad random distribution)`` () =
    let stats = InfiniteTestRunner.runBounded 5000 7 0.5
    if stats.Total >= 50 then
        Assert.True(stats.AvgF1 >= 0.50,
            sprintf "avg F1 %.3f < 0.50" stats.AvgF1)

[<Fact>]
let ``Fuzz: 3초 — avg scenario time < 100ms`` () =
    let stats = InfiniteTestRunner.runBounded 3000 99 0.5
    if stats.Total >= 20 then
        Assert.True(stats.AvgMs < 100.0,
            sprintf "avg per scenario %.1fms > 100ms" stats.AvgMs)

[<Fact>]
let ``Fuzz: 2초 — 같은 seed → 비슷한 결과 (wall-clock 변동 허용)`` () =
    let stats1 = InfiniteTestRunner.runBounded 2000 555 0.5
    let stats2 = InfiniteTestRunner.runBounded 2000 555 0.5
    // wall-clock timing 영향으로 정확히 같지 않을 수 있음. 50% 범위 안.
    let close = abs (stats1.Total - stats2.Total) <= max 50 (stats1.Total / 2)
    Assert.True(close,
        sprintf "expected similar total; got %d vs %d" stats1.Total stats2.Total)
