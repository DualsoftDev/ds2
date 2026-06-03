/// E. Edge cases — empty / single / extreme N / boundary lag.
module Ds2.Reverse.Tests.EdgeCaseTests

open Xunit
open Ds2.Reverse.Core

let private cfg = CausationConfig.defaults

[<Fact>]
let ``Edge: 빈 events list → ReverseEngine 정상 동작`` () =
    let inp =
        ReverseEngine.mkInput "P" "S"
            Map.empty
            []
            []
            cfg
    let store, report = ReverseEngine.run inp
    Assert.Equal(0, store.Calls.Count)
    Assert.Equal(0, store.ArrowCalls.Count)
    Assert.Equal(0, report.FinalArrowCount)

[<Fact>]
let ``Edge: 단일 event → 검출 없음`` () =
    let evs = [ { T = 100L; Name = "A" } ]
    let inp =
        ReverseEngine.mkInput "P" "S"
            (Map.ofList [ "F", [ "F.A", "" ] ])
            []
            evs
            cfg
    let _, report = ReverseEngine.run inp
    Assert.Equal(0, report.FinalArrowCount)

[<Fact>]
let ``Edge: 모든 events 같은 timestamp → 처리 가능`` () =
    let evs = [
        { T = 100L; Name = "A" }
        { T = 100L; Name = "B" }
        { T = 100L; Name = "C" }
    ]
    let inp =
        ReverseEngine.mkInput "P" "S"
            (Map.ofList [ "F", [ "F.A", ""; "F.B", ""; "F.C", "" ] ])
            []
            evs
            cfg
    let store, _ = ReverseEngine.run inp
    Assert.Equal(3, store.Calls.Count)

[<Fact>]
let ``Edge: cycleHint 0 → window 그대로 사용 (안전)`` () =
    let cfg0 = { cfg with CycleHintMs = Some 0L }
    let a = [| for k in 0 .. 29 -> int64 k * 2000L |]
    let b = a |> Array.map (fun t -> t + 300L)
    // 호출이 throw 안 함
    let s = CausationDetection.score cfg0 a b
    Assert.NotNull(box s)

[<Fact>]
let ``Edge: window 매우 작음 (10ms) → 거의 다 미매칭`` () =
    let cfgSmall = { cfg with WindowMs = 10L }
    let a = [| for k in 0 .. 29 -> int64 k * 2000L |]
    let b = a |> Array.map (fun t -> t + 300L)
    let s = CausationDetection.score cfgSmall a b
    Assert.False(s.PassesSeq)

[<Fact>]
let ``Edge: 모든 lag 0 (정확 동시) → parallel`` () =
    let a = [| for k in 0 .. 29 -> int64 k * 2000L |]
    let b = [| for k in 0 .. 29 -> int64 k * 2000L |]   // 정확 동시
    let s = CausationDetection.score (CausationConfig.withCycleHint 2000L cfg) a b
    Assert.True(s.IsParallel)
    Assert.Equal(0.0, s.LagMean)

[<Fact>]
let ``Edge: 1000 cycles 처리 가능 (메모리 안 폭주)`` () =
    let a = [| for k in 0 .. 999 -> int64 k * 100L |]
    let b = [| for k in 0 .. 999 -> int64 k * 100L + 30L |]
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let s = CausationDetection.score (CausationConfig.withCycleHint 100L cfg) a b
    sw.Stop()
    Assert.True(sw.ElapsedMilliseconds < 1000L,
        sprintf "1000-cycle in %dms (limit 1000)" sw.ElapsedMilliseconds)
    Assert.True(s.NA = 1000)

[<Fact>]
let ``Edge: 100-노드 chain ReverseEngine 처리 가능`` () =
    let n = 100
    let names = [ for i in 0 .. n - 1 -> sprintf "F.N%d" i ]
    let evs =
        [ for cycle in 0 .. 9 do
            for i in 0 .. n - 1 do
                yield { T = int64 cycle * 20000L + int64 i * 100L
                        Name = names.[i] } ]
    let cands =
        [ for i in 0 .. n - 2 ->
            { Src = names.[i]; Tgt = names.[i + 1]; DeclaredKind = "trigger" } ]
    let inp =
        ReverseEngine.mkInput "P" "S"
            (Map.ofList [ "F", names |> List.map (fun n -> n, "") ])
            cands
            evs
            (CausationConfig.withCycleHint 20000L cfg)
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let store, _ = ReverseEngine.run inp
    sw.Stop()
    Assert.True(sw.ElapsedMilliseconds < 3000L,
        sprintf "100-node chain in %dms (limit 3000)" sw.ElapsedMilliseconds)
    Assert.Equal(n, store.Calls.Count)

[<Fact>]
let ``Edge: 모두 같은 이름 (중복 발화) → 단일 call`` () =
    let evs = [
        { T = 0L; Name = "A" }
        { T = 1000L; Name = "A" }
        { T = 2000L; Name = "A" }
    ]
    let inp =
        ReverseEngine.mkInput "P" "S"
            (Map.ofList [ "F", [ "F.A", "" ] ])
            []
            evs
            cfg
    let store, _ = ReverseEngine.run inp
    Assert.Equal(1, store.Calls.Count)

[<Fact>]
let ``Edge: candidate 자기-자신 (A→A) → skip`` () =
    let evs = [
        { T = 0L; Name = "A" }
        { T = 1000L; Name = "A" }
    ]
    let inp =
        ReverseEngine.mkInput "P" "S"
            (Map.ofList [ "F", [ "F.A", "" ] ])
            [ { Src = "F.A"; Tgt = "F.A"; DeclaredKind = "trigger" } ]
            evs
            cfg
    let store, _ = ReverseEngine.run inp
    // self-loop skip
    Assert.Equal(0, store.ArrowCalls.Count)

[<Fact>]
let ``Edge: 매우 짧은 cycle (50ms) — group lag 10ms 인정 시 declared group`` () =
    // lag 10ms 는 parallel zone (< 50ms). declared trigger → passes_seq 실패.
    // declared group 이면 passes_grp 통과.
    let evs =
        [ for cycle in 0 .. 99 do
            yield { T = int64 cycle * 50L; Name = "F.A" }
            yield { T = int64 cycle * 50L + 10L; Name = "F.B" } ]
    let inp =
        ReverseEngine.mkInput "P" "S"
            (Map.ofList [ "F", [ "F.A", ""; "F.B", "" ] ])
            [ { Src = "F.A"; Tgt = "F.B"; DeclaredKind = "group" } ]
            evs
            (CausationConfig.withCycleHint 50L cfg)
    let store, _ = ReverseEngine.run inp
    Assert.True(store.ArrowCalls.Count >= 1)

[<Fact>]
let ``Edge: 매우 긴 cycle (1 hour) — 동작 정상`` () =
    let cycleMs = 3600000L   // 1 hour
    let evs =
        [ for cycle in 0 .. 9 do
            yield { T = int64 cycle * cycleMs; Name = "F.A" }
            yield { T = int64 cycle * cycleMs + 100L; Name = "F.B" } ]
    let inp =
        ReverseEngine.mkInput "P" "S"
            (Map.ofList [ "F", [ "F.A", ""; "F.B", "" ] ])
            [ { Src = "F.A"; Tgt = "F.B"; DeclaredKind = "trigger" } ]
            evs
            (CausationConfig.withCycleHint cycleMs cfg)
    let store, _ = ReverseEngine.run inp
    Assert.True(store.Calls.Count = 2)

[<Fact>]
let ``Edge: 0 candidates → arrows 0 emit`` () =
    let evs = [
        { T = 0L; Name = "F.A" }
        { T = 300L; Name = "F.B" }
    ]
    let inp =
        ReverseEngine.mkInput "P" "S"
            (Map.ofList [ "F", [ "F.A", ""; "F.B", "" ] ])
            []
            evs
            cfg
    let store, _ = ReverseEngine.run inp
    Assert.Equal(0, store.ArrowCalls.Count)

[<Fact>]
let ``Edge: candidate src/tgt 매칭 안 됨 → skip`` () =
    let evs = [
        { T = 0L; Name = "F.A" }
        { T = 300L; Name = "F.B" }
    ]
    let inp =
        ReverseEngine.mkInput "P" "S"
            (Map.ofList [ "F", [ "F.A", ""; "F.B", "" ] ])
            [ { Src = "Unknown1"; Tgt = "Unknown2"; DeclaredKind = "trigger" } ]
            evs
            cfg
    let store, _ = ReverseEngine.run inp
    Assert.Equal(0, store.ArrowCalls.Count)

[<Fact>]
let ``Edge: 같은 candidate 가 중복 입력됨 → 1번만 emit`` () =
    let evs =
        [ for k in 0 .. 29 do
            yield { T = int64 k * 2000L; Name = "F.A" }
            yield { T = int64 k * 2000L + 300L; Name = "F.B" } ]
    let cands = [
        { Src = "F.A"; Tgt = "F.B"; DeclaredKind = "trigger" }
        { Src = "F.A"; Tgt = "F.B"; DeclaredKind = "trigger" }   // 중복
    ]
    let inp =
        ReverseEngine.mkInput "P" "S"
            (Map.ofList [ "F", [ "F.A", ""; "F.B", "" ] ])
            cands
            evs
            (CausationConfig.withCycleHint 2000L cfg)
    let store, _ = ReverseEngine.run inp
    Assert.Equal(1, store.ArrowCalls.Count)
