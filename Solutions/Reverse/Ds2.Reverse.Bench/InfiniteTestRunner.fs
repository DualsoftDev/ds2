/// Bounded / unbounded infinite scenario runner.
namespace Ds2.Reverse.Bench

open System
open System.Diagnostics
open System.Threading
open Ds2.Reverse.Core

/// 실행 통계 + failure list.
type RunStats = {
    Total: int
    Perfect: int
    Failed: (ScenarioSpec * float) list      // (spec, F1) — F1 < threshold
    AvgF1: float
    AvgMs: float
    ElapsedMs: int64
    Crashes: (ScenarioSpec * string) list    // (spec, exception message)
}

module InfiniteTestRunner =

    /// Bounded — timeoutMs 동안 가능한 많은 random scenario 실행.
    /// failThreshold: F1 < threshold 면 failure list 에 기록.
    let runBounded (timeoutMs: int) (seed: int) (failThreshold: float) : RunStats =
        let rng = Random(seed)
        let cfg = CausationConfig.defaults
        let sw = Stopwatch.StartNew()
        let mutable total = 0
        let mutable perfect = 0
        let mutable sumF1 = 0.0
        let mutable sumMs = 0L
        let failed = ResizeArray<ScenarioSpec * float>()
        let crashes = ResizeArray<ScenarioSpec * string>()
        while sw.ElapsedMilliseconds < int64 timeoutMs do
            let spec = RandomScenarioGen.random rng
            try
                let scen = RandomScenarioGen.toScenario spec
                let scSw = Stopwatch.StartNew()
                let r = BenchRunner.runOne scen cfg spec.Seed (min spec.NCycles 60)
                scSw.Stop()
                total <- total + 1
                sumF1 <- sumF1 + r.F1
                sumMs <- sumMs + scSw.ElapsedMilliseconds
                if r.F1 >= 0.9999 then perfect <- perfect + 1
                if r.F1 < failThreshold then
                    failed.Add(spec, r.F1)
            with ex ->
                crashes.Add(spec, ex.Message)
        sw.Stop()
        {
            Total = total
            Perfect = perfect
            Failed = failed |> List.ofSeq
            AvgF1 = if total = 0 then 0.0 else sumF1 / float total
            AvgMs = if total = 0 then 0.0 else float sumMs / float total
            ElapsedMs = sw.ElapsedMilliseconds
            Crashes = crashes |> List.ofSeq
        }

    /// 무한 모드 — CancellationToken 가 cancel 될 때까지.
    let runUntilStop (token: CancellationToken) (seed: int) (failThreshold: float) : RunStats =
        let rng = Random(seed)
        let cfg = CausationConfig.defaults
        let sw = Stopwatch.StartNew()
        let mutable total = 0
        let mutable perfect = 0
        let mutable sumF1 = 0.0
        let mutable sumMs = 0L
        let failed = ResizeArray<ScenarioSpec * float>()
        let crashes = ResizeArray<ScenarioSpec * string>()
        while not token.IsCancellationRequested do
            let spec = RandomScenarioGen.random rng
            try
                let scen = RandomScenarioGen.toScenario spec
                let scSw = Stopwatch.StartNew()
                let r = BenchRunner.runOne scen cfg spec.Seed (min spec.NCycles 60)
                scSw.Stop()
                total <- total + 1
                sumF1 <- sumF1 + r.F1
                sumMs <- sumMs + scSw.ElapsedMilliseconds
                if r.F1 >= 0.9999 then perfect <- perfect + 1
                if r.F1 < failThreshold then
                    failed.Add(spec, r.F1)
            with ex ->
                crashes.Add(spec, ex.Message)
        sw.Stop()
        {
            Total = total
            Perfect = perfect
            Failed = failed |> List.ofSeq
            AvgF1 = if total = 0 then 0.0 else sumF1 / float total
            AvgMs = if total = 0 then 0.0 else float sumMs / float total
            ElapsedMs = sw.ElapsedMilliseconds
            Crashes = crashes |> List.ofSeq
        }

    /// 통계 사람-읽기.
    let formatStats (stats: RunStats) : string =
        let sb = System.Text.StringBuilder()
        sb.AppendLine(sprintf "━━ Infinite Runner Stats ━━") |> ignore
        sb.AppendLine(sprintf "  Total: %d scenarios in %dms" stats.Total stats.ElapsedMs) |> ignore
        sb.AppendLine(sprintf "  Perfect (F1=1.0): %d (%.1f%%)"
            stats.Perfect
            (if stats.Total = 0 then 0.0 else float stats.Perfect * 100.0 / float stats.Total))
            |> ignore
        sb.AppendLine(sprintf "  Avg F1: %.4f, Avg time/scenario: %.1fms" stats.AvgF1 stats.AvgMs)
            |> ignore
        sb.AppendLine(sprintf "  Failed (F1<threshold): %d" (List.length stats.Failed)) |> ignore
        sb.AppendLine(sprintf "  Crashes: %d" (List.length stats.Crashes)) |> ignore
        if not (List.isEmpty stats.Crashes) then
            sb.AppendLine "  Crashes detail:" |> ignore
            for (spec, msg) in stats.Crashes |> List.truncate 5 do
                sb.AppendLine(sprintf "    %s → %s" (RandomScenarioGen.describe spec) msg)
                    |> ignore
        if not (List.isEmpty stats.Failed) then
            sb.AppendLine "  Top failures (worst F1 first):" |> ignore
            for (spec, f1) in stats.Failed |> List.sortBy snd |> List.truncate 5 do
                sb.AppendLine(sprintf "    F1=%.3f %s" f1 (RandomScenarioGen.describe spec))
                    |> ignore
        sb.ToString()
