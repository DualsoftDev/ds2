namespace Ds2.LightHouse.Extractors.XlsxStrategies

open System
open Ds2.LightHouse
open Ds2.LightHouse.Diagnostics

/// **PR-I1 (todo-documents-based-gfm.md §2 + documents-based-gfm.md §8.5.7)** —
/// xlsx strategy classifier 진입점. 등록된 strategy list 를 priority 순으로 평가 후
/// 매치된 strategy 의 `Build` 호출.
///
/// **PR-I1 시점 strategy list** = `[ IoListStrategy ]` 단독.
/// PR-I2 진입 시 `WorkOrderStrategy` append (`documents-based-gfm.md` §8.5.7 priority order #3).
/// **금지** (todo §2.1): 본 PR 의 strategy list 에 IoList 외 추가 0.
///
/// 분류 결과 표현:
///   - `Matched (strategy, markdown)` — 매치 + 변환 성공.
///   - `Rejected entry` — signature 매치는 되었으나 변환 실패 (또는 매크로 xlsx 등 비처리).
///   - `NearMiss entries` — strategy score > 0 이나 threshold 미달 (사용자 진단용).
///   - `Unmatched` — 모든 strategy 가 score 0 또는 DocType 불일치 (조용히 fallback).
type XlsxClassificationResult =
    | Matched of strategyName: string * markdown: string
    | Rejected of RejectedEntry
    | NearMiss of NearMissEntry list
    | Unmatched

/// strategy classifier — 등록된 strategy list 의 priority 순 평가 + dispatch.
module XlsxSignatureClassifier =

    /// PR-I1 시점 등록 strategy list. **본 list 에 추가는 PR-I2 의 영역** (todo §2.1).
    /// priority order = `documents-based-gfm.md` §8.5.7 → IoList (현재 단독).
    let strategies : IXlsxStrategy list = [
        IoListStrategy() :> IXlsxStrategy
    ]

    /// 입력 `ExtractedDocument` 의 strategy 매치 시도.
    /// 동작:
    ///   1. priority 순 strategy 순회 → Signature 평가.
    ///   2. 첫 매치 strategy 의 Build 호출 → Built / Rejected 결과 그대로 반환.
    ///   3. 매치 strategy 0건 + score > 0 strategy 가 있으면 NearMiss list 반환.
    ///   4. 모든 strategy score 0 → Unmatched.
    let classify (sourcePath: string) (extracted: ExtractedDocument) : XlsxClassificationResult =
        let evaluations =
            strategies
            |> List.map (fun s -> s, s.Signature extracted)
        let matched = evaluations |> List.tryFind (fun (_, sigR) -> sigR.Matched)
        match matched with
        | Some (strategy, _) ->
            match strategy.Build (sourcePath, extracted) with
            | StrategyOutcome.Built md -> Matched (strategy.Name, md)
            | StrategyOutcome.Rejected entry -> Rejected entry
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
