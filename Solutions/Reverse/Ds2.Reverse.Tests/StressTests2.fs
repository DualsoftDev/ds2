/// S. Stress tests — 대량 시나리오 / 큰 데이터 / 동시 실행.
module Ds2.Reverse.Tests.StressTests2

open System
open System.Threading.Tasks
open Xunit
open Ds2.Reverse.Core
open Ds2.Reverse.Bench

[<Fact>]
let ``Stress: 200 시나리오 일괄 (m0-100 + Phase 1-5) < 30s`` () =
    let all =
        Models.all @
        Phase1Models.all @
        Phase2Models.all @
        Phase3Models.all @
        Phase4Models.all @
        Phase5Models.all
    let sw = Diagnostics.Stopwatch.StartNew()
    let summary, _ = BenchRunner.runAll all CausationConfig.defaults 42 60
    sw.Stop()
    printfn "Total %d scenarios in %dms (avg F1=%.4f, perfect %d/%d)"
        summary.Total sw.ElapsedMilliseconds summary.AvgF1 summary.Perfect summary.Total
    Assert.True(sw.ElapsedMilliseconds < 30000L,
        sprintf "Took %dms (limit 30000)" sw.ElapsedMilliseconds)

[<Fact>]
let ``Stress: 1000 cycle single chain < 3s`` () =
    let n = 5
    let names = [ for i in 0 .. n - 1 -> sprintf "F.N%d" i ]
    let evs =
        [ for cycle in 0 .. 999 do
            for i in 0 .. n - 1 do
                yield { T = int64 cycle * 2000L + int64 i * 200L
                        Name = names.[i] } ]
    let cands =
        [ for i in 0 .. n - 2 ->
            { Src = names.[i]; Tgt = names.[i + 1]; DeclaredKind = "trigger" } ]
    let inp =
        ReverseEngine.mkInput "P" "S"
            (Map.ofList [ "F", names |> List.map (fun n -> n, "") ])
            cands evs
            (CausationConfig.withCycleHint 2000L CausationConfig.defaults)
    let sw = Diagnostics.Stopwatch.StartNew()
    let store, _ = ReverseEngine.run inp
    sw.Stop()
    Assert.True(sw.ElapsedMilliseconds < 3000L,
        sprintf "1000 cycle in %dms (limit 3000)" sw.ElapsedMilliseconds)
    Assert.True(store.ArrowCalls.Count = n - 1)

[<Fact>]
let ``Stress: 100-node chain x 30 cycle < 5s`` () =
    let n = 100
    let names = [ for i in 0 .. n - 1 -> sprintf "F.N%d" i ]
    let evs =
        [ for cycle in 0 .. 29 do
            for i in 0 .. n - 1 do
                yield { T = int64 cycle * 20000L + int64 i * 100L
                        Name = names.[i] } ]
    let cands =
        [ for i in 0 .. n - 2 ->
            { Src = names.[i]; Tgt = names.[i + 1]; DeclaredKind = "trigger" } ]
    let inp =
        ReverseEngine.mkInput "P" "S"
            (Map.ofList [ "F", names |> List.map (fun n -> n, "") ])
            cands evs
            (CausationConfig.withCycleHint 20000L CausationConfig.defaults)
    let sw = Diagnostics.Stopwatch.StartNew()
    let store, _ = ReverseEngine.run inp
    sw.Stop()
    Assert.True(sw.ElapsedMilliseconds < 5000L,
        sprintf "100-node x 30 cycle in %dms (limit 5000)" sw.ElapsedMilliseconds)
    Assert.True(store.Calls.Count = n)

[<Fact>]
let ``Stress: 동시 (parallel) 10 시나리오 실행 — no state leak`` () =
    // PSeq 도 가능하지만 단순히 Parallel.For 사용
    let sc = Phase1Models.all |> List.head
    let cfg = CausationConfig.defaults
    let results = System.Collections.Concurrent.ConcurrentBag<float>()
    Parallel.For(0, 10, fun _ ->
        let r = BenchRunner.runOne sc cfg 42 30
        results.Add r.F1
    ) |> ignore
    // 모든 결과가 같아야 (deterministic)
    let arr = results.ToArray()
    let allEqual = arr |> Array.forall (fun x -> abs (x - arr.[0]) < 1e-9)
    Assert.True(allEqual,
        sprintf "expected deterministic; got %A" arr)

[<Fact>]
let ``Stress: 500 cycle 짧은 chain — memory 안정`` () =
    // 메모리 측정 — 시작과 끝 GC 측정.
    GC.Collect()
    GC.WaitForPendingFinalizers()
    let memBefore = GC.GetTotalMemory(true)
    let n = 5
    let names = [ for i in 0 .. n - 1 -> sprintf "F.N%d" i ]
    let evs =
        [ for cycle in 0 .. 499 do
            for i in 0 .. n - 1 do
                yield { T = int64 cycle * 2000L + int64 i * 200L
                        Name = names.[i] } ]
    let cands =
        [ for i in 0 .. n - 2 ->
            { Src = names.[i]; Tgt = names.[i + 1]; DeclaredKind = "trigger" } ]
    let inp =
        ReverseEngine.mkInput "P" "S"
            (Map.ofList [ "F", names |> List.map (fun n -> n, "") ])
            cands evs
            (CausationConfig.withCycleHint 2000L CausationConfig.defaults)
    let _, _ = ReverseEngine.run inp
    GC.Collect()
    GC.WaitForPendingFinalizers()
    let memAfter = GC.GetTotalMemory(true)
    let deltaMB = float (memAfter - memBefore) / (1024.0 * 1024.0)
    printfn "Memory delta: %.2f MB" deltaMB
    Assert.True(deltaMB < 50.0,
        sprintf "memory usage %.2fMB > 50MB" deltaMB)
