namespace Ds2.LightHouse.PdfStrategies

open System
open Ds2.LightHouse
open Ds2.LightHouse.Diagnostics
open Ds2.LightHouse.Extractors.XlsxStrategies

/// **PR-I2 (todo-documents-based-gfm.md §2 + §2.1 + documents-based-gfm.md §8.5.7)** —
/// pdf strategy classifier 진입점. 등록된 strategy list 를 priority 순으로 평가 후 매치된 strategy 의 `Build` 호출.
///
/// **PR-I2 시점 strategy list** = `[ PdfControlSpecStrategy ]` 단독.
/// 진단 schema (NearMissEntry / RejectedEntry) 는 XlsxStrategies 와 공유 — 사용자 진단 view 통일.
///
/// XlsxClassificationResult 와 동등 (재명명) — 본 PR 의 변경 가능 영역 한정 (todo §2.1 의 "신규" 영역).
/// 단 type 자체는 별 신설 (xlsx classifier 의 type 을 reuse 하면 case 가 "Xlsx" prefix 등으로 의미 오염).
///
/// **PR-I2.5 (Major 3)**: `Rejected` case → `RejectedByStrategy` rename. `StrategyOutcome.Rejected` 와
/// case name 동일하여 reader 가 두 type 을 혼동하던 문제 해소.
type PdfClassificationResult =
    | Matched of strategyName: string * markdown: string
    | RejectedByStrategy of RejectedEntry
    | NearMiss of NearMissEntry list
    | Unmatched

/// pdf strategy classifier — 등록된 strategy list 의 priority 순 평가 + dispatch.
module PdfSignatureClassifier =

    /// PR-I2 시점 등록 strategy list. priority order = `documents-based-gfm.md` §8.5.7 → PdfControlSpec (현재 단독).
    let strategies : IPdfStrategy list = [
        PdfControlSpecStrategy() :> IPdfStrategy
    ]

    /// 입력 `ExtractedDocument` 의 strategy 매치 시도.
    /// 동작:
    ///   1. priority 순 strategy 순회 → Signature 평가.
    ///   2. 첫 매치 strategy 의 Build 호출 → Built / Rejected 결과 그대로 반환.
    ///   3. 매치 strategy 0건 + score > 0 strategy 가 있으면 NearMiss list 반환.
    ///   4. 모든 strategy score 0 → Unmatched.
    let classify (sourcePath: string) (extracted: ExtractedDocument) : PdfClassificationResult =
        let evaluations =
            strategies
            |> List.map (fun s -> s, s.Signature extracted)
        let matched = evaluations |> List.tryFind (fun (_, sigR) -> sigR.Matched)
        match matched with
        | Some (strategy, _) ->
            match strategy.Build (sourcePath, extracted) with
            | StrategyOutcome.Built md -> Matched (strategy.Name, md)
            | StrategyOutcome.Rejected entry -> RejectedByStrategy entry
        | None ->
            let nearMisses =
                evaluations
                |> List.filter (fun (_, sigR) -> sigR.Score > 0)
                |> List.map (fun (strategy, sigR) -> {
                    File = sourcePath
                    CandidateStrategy = strategy.Name
                    Score = sigR.Score
                    Threshold = sigR.Threshold
                    Detail = sigR.Detail
                    DetectedAt = DateTime.UtcNow
                })
            if List.isEmpty nearMisses then Unmatched
            else NearMiss nearMisses
