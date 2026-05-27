namespace Ds2.LightHouse.PdfStrategies

open System
open Ds2.LightHouse
open Ds2.LightHouse.Diagnostics
open Ds2.LightHouse.Extractors
open Ds2.LightHouse.Extractors.XlsxStrategies

/// **PR-I2 (todo-documents-based-gfm.md §2 + §2.1 + documents-based-gfm.md §8.5.7)** —
/// pdf strategy classifier 진입점. 등록된 strategy list 를 priority 순으로 평가 후 매치된 strategy 의 `Build` 호출.
///
/// **PR-I2 시점 strategy list** = `[ PdfControlSpecStrategy ]` 단독.
/// 진단 schema (NearMissEntry / RejectedEntry) 는 XlsxStrategies 와 공유 — 사용자 진단 view 통일.
///
/// **Backlog C (PR-I2 검열 Major 2)**: 종전 pdf 전용 분류 DU 를 `ClassificationResult`
/// (Extractors/ClassificationResult.fs) 로 통합. xlsx classifier 와 동일 type 공유 — 두 classifier 의
/// signature detect 진입점만 별, 분류 결과 표현은 단일 SSOT.
///
/// 분류 결과 표현 (`ClassificationResult` 참조):
///   - `Matched (strategy, markdown)` — signature 매치 + Build 성공.
///   - `RejectedByStrategy entry` — signature 매치 후 변환 실패. `StrategyOutcome.Rejected` 와 case 명
///     혼동 방지 위해 `RejectedByStrategy` 유지 (PR-I2.5 Major 3 정합).
///   - `NearMiss entries` — strategy score > 0 이나 threshold 미달.
///   - `Unmatched` — 모든 strategy score 0 또는 DocType 불일치.
///
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
    let classify (sourcePath: string) (extracted: ExtractedDocument) : ClassificationResult =
        let evaluations =
            strategies
            |> List.map (fun s -> s, s.Signature extracted)
        let matched = evaluations |> List.tryFind (fun (_, sigR) -> sigR.Matched)
        match matched with
        | Some (strategy, _) ->
            match strategy.Build (sourcePath, extracted) with
            | StrategyOutcome.Built md -> ClassificationResult.Matched (strategy.Name, md)
            | StrategyOutcome.Rejected entry -> ClassificationResult.RejectedByStrategy entry
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
            if List.isEmpty nearMisses then ClassificationResult.Unmatched
            else ClassificationResult.NearMiss nearMisses
