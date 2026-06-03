/// 시나리오 일괄 실행 + 회귀 통계.
namespace Ds2.Reverse.Bench

open Ds2.Reverse.Core
open Ds2.Core.Store

module BenchRunner =

    type ScenarioResult = {
        Name: string
        Truth: int
        Detected: int
        TP: int
        FP: int
        FN: int
        Precision: float
        Recall: float
        F1: float
        Report: DetectionReport
        FpDetail: (string * string) list
        FnDetail: (string * string) list
    }

    let runOne (sc: Scenario) (cfg: CausationConfig) (seed: int) (nCycles: int) : ScenarioResult =
        let events =
            match sc.PatternCycleAware with
            | Some pca -> Simulator.simulateCycleAware seed sc.CycleMs nCycles pca
            | None -> Simulator.simulate seed sc.CycleMs nCycles sc.Pattern
        let cfgWithHint = CausationConfig.withCycleHint sc.CycleMs cfg
        // 다중-flow 시나리오 (Phase 6: f/fa/fh/fs prefix) 는 prefix 기반 auto-group.
        // 단일 flow 시나리오는 기존 단일 매핑.
        let isMultiFlow =
            (sc.Name.StartsWith "f" || sc.Name.StartsWith "fa"
             || sc.Name.StartsWith "fh" || sc.Name.StartsWith "fs")
            && (sc.AllCalls
                |> List.choose (fun n ->
                    match n.IndexOf '.' with
                    | -1 -> None
                    | i -> Some (n.Substring(0, i)))
                |> List.distinct
                |> List.length) > 1
        let flowCallsMap =
            if isMultiFlow then
                Scenario.flowCallsAuto sc.AllCalls
            else
                Scenario.flowCalls sc.Flow sc.AllCalls
        let baseInput =
            ReverseEngine.mkInput
                sc.Name
                "Main"
                flowCallsMap
                (Scenario.candidatesFor sc.GroundTruth sc.Spurious)
                events
                cfgWithHint
        // LogicModels 에 등록된 rungs 가 있으면 사용
        let input =
            match LogicModels.logicRungsBySc.TryGetValue sc.Name with
            | true, rungs -> { baseInput with LogicRungs = Some rungs }
            | _ -> baseInput
        let store, report = ReverseEngine.run input
        let metrics = Evaluation.evaluate sc.GroundTruth store
        let m = metrics.Overall
        let fp = metrics.SeqFP @ (metrics.GrpFP |> List.map (fun s ->
                                    match Set.toList s with [a; b] -> a, b | _ -> "?", "?"))
        let fn = metrics.SeqFN @ (metrics.GrpFN |> List.map (fun s ->
                                    match Set.toList s with [a; b] -> a, b | _ -> "?", "?"))
        { Name = sc.Name
          Truth = m.Truth; Detected = m.Detected
          TP = m.TP; FP = m.FP; FN = m.FN
          Precision = m.Precision; Recall = m.Recall; F1 = m.F1
          Report = report
          FpDetail = fp; FnDetail = fn }

    type Summary = {
        Total: int
        Perfect: int           // F1 = 1.0
        AvgPrecision: float
        AvgRecall: float
        AvgF1: float
        TotalTp: int
        TotalFp: int
        TotalFn: int
        Failed: ScenarioResult list   // F1 < 1.0
    }

    let runAll (scenarios: Scenario seq) (cfg: CausationConfig) (seed: int) (nCycles: int) : Summary * ScenarioResult list =
        let results = scenarios |> Seq.map (fun s -> runOne s cfg seed nCycles) |> List.ofSeq
        let n = List.length results
        let perfect = results |> List.filter (fun r -> r.F1 >= 0.9999) |> List.length
        let sumP = results |> List.sumBy (fun r -> r.Precision)
        let sumR = results |> List.sumBy (fun r -> r.Recall)
        let sumF = results |> List.sumBy (fun r -> r.F1)
        let tp = results |> List.sumBy (fun r -> r.TP)
        let fp = results |> List.sumBy (fun r -> r.FP)
        let fn = results |> List.sumBy (fun r -> r.FN)
        let failed = results |> List.filter (fun r -> r.F1 < 0.9999)
        { Total = n; Perfect = perfect
          AvgPrecision = sumP / float n
          AvgRecall = sumR / float n
          AvgF1 = sumF / float n
          TotalTp = tp; TotalFp = fp; TotalFn = fn
          Failed = failed }, results

    /// 보고서 출력 — failures 상세 포함.
    let formatSummary (summary: Summary) : string =
        let lines = ResizeArray<string>()
        lines.Add(sprintf "━━ Aggregate (%d scenarios) ━━" summary.Total)
        lines.Add(sprintf "  Perfect (F1=1.0): %d / %d" summary.Perfect summary.Total)
        lines.Add(sprintf "  Avg Precision: %.4f" summary.AvgPrecision)
        lines.Add(sprintf "  Avg Recall:    %.4f" summary.AvgRecall)
        lines.Add(sprintf "  Avg F1:        %.4f" summary.AvgF1)
        lines.Add(sprintf "  Total TP=%d FP=%d FN=%d" summary.TotalTp summary.TotalFp summary.TotalFn)
        if not (List.isEmpty summary.Failed) then
            lines.Add(sprintf "━━ Failed (%d) ━━" (List.length summary.Failed))
            for f in summary.Failed do
                lines.Add(sprintf "  ✗ %-25s F1=%.3f (P=%.3f R=%.3f) [T=%d D=%d TP=%d FP=%d FN=%d]"
                    f.Name f.F1 f.Precision f.Recall f.Truth f.Detected f.TP f.FP f.FN)
                for s, t in f.FpDetail do
                    lines.Add(sprintf "      FP: %s → %s" s t)
                for s, t in f.FnDetail do
                    lines.Add(sprintf "      FN: %s → %s" s t)
        String.concat "\n" lines
