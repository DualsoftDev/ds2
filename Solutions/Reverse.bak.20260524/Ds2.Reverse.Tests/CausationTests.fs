module Ds2.Reverse.Tests.CausationTests

open Xunit
open Ds2.Reverse.Core

let cfg = CausationConfig.defaults

[<Fact>]
let ``perfect sequential — passes_seq true`` () =
    // 일정 100ms lag, 안정
    let a = [ for i in 0 .. 9 -> int64 i * 1000L ]
    let b = [ for i in 0 .. 9 -> int64 i * 1000L + 100L ]
    let sco = CausationDetection.score cfg a b
    Assert.True(sco.PassesSeq, sprintf "expected passesSeq; got %A" sco)
    Assert.False sco.PassesGrp
    Assert.True(sco.Sufficiency >= 0.99)
    Assert.True(sco.Necessity >= 0.99)

[<Fact>]
let ``parallel (lag near 0) — passes_grp true`` () =
    let a = [ for i in 0 .. 9 -> int64 i * 1000L ]
    let b = [ for i in 0 .. 9 -> int64 i * 1000L + 5L ]
    let sco = CausationDetection.score cfg a b
    Assert.True(sco.IsParallel, sprintf "expected parallel; got %A" sco)
    Assert.True(sco.PassesGrp)

[<Fact>]
let ``random / low correlation — both fail`` () =
    let rng = System.Random(42)
    let a = [ for _ in 0 .. 9 -> int64 (rng.Next(0, 100000)) ]
    let b = [ for _ in 0 .. 9 -> int64 (rng.Next(0, 100000)) ]
    let sco = CausationDetection.score cfg a b
    Assert.False sco.PassesSeq
    Assert.False sco.PassesGrp

[<Fact>]
let ``high CV — fails seq gate (confounded)`` () =
    let rng = System.Random(7)
    let a = [ for i in 0 .. 19 -> int64 i * 1000L ]
    // lag 평균 600, std ~400 → CV 큼
    let b = [ for i in 0 .. 19 ->
                int64 i * 1000L + 600L + int64 (rng.Next(-400, 700)) ]
    let sco = CausationDetection.score cfg a b
    Assert.False(sco.PassesSeq, sprintf "expected fail due to high CV; %A" sco)

[<Fact>]
let ``small sample — both fail`` () =
    let a = [ 0L; 1000L ]
    let b = [ 100L; 1100L ]
    let sco = CausationDetection.score cfg a b
    Assert.False sco.PassesSeq
    Assert.False sco.PassesGrp
    match sco.Reason with
    | Some r -> Assert.Contains("low_n", r)
    | None -> Assert.Fail("expected reason")

[<Fact>]
let ``gate respects declared kind — group requires passes_grp`` () =
    // sequential 통과하는 score 인데 declared=group 이면 dropped (parallel 아님)
    let a = [ for i in 0 .. 9 -> int64 i * 1000L ]
    let b = [ for i in 0 .. 9 -> int64 i * 1000L + 500L ]
    let sco = CausationDetection.score cfg a b
    Assert.True sco.PassesSeq
    Assert.False sco.IsParallel
    match CausationDetection.gate "group" sco with
    | Dropped(reason, _) -> Assert.Equal("declared_group_but_lag_too_large", reason)
    | _ -> Assert.Fail("expected Dropped for declared=group with non-parallel data")

[<Fact>]
let ``stability fallback — small lag with jitter still seq`` () =
    // lag 10ms, std ~50ms → CV=5 큼, 하지만 std < 150 이라 통과
    let rng = System.Random(13)
    let a = [ for i in 0 .. 19 -> int64 i * 1000L ]
    let b = [ for i in 0 .. 19 ->
                int64 i * 1000L + 10L + int64 (rng.Next(-30, 31)) ]
    let sco = CausationDetection.score cfg a b
    Assert.True(sco.LagCv > 0.30, sprintf "expected high CV; %A" sco)
    Assert.True(sco.LagStd <= 150.0, sprintf "expected std<=150; %A" sco)
    Assert.True(sco.PassesSeq, sprintf "stability fallback should pass; %A" sco)
