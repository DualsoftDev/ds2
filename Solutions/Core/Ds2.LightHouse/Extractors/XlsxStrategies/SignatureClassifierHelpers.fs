namespace Ds2.LightHouse.Extractors.XlsxStrategies

open System
open Ds2.LightHouse
open Ds2.LightHouse.Diagnostics
open Ds2.LightHouse.Extractors

/// **라운드 3 Major-1 + Major-4 통합 fix** —
/// XlsxSignatureClassifier ↔ PdfSignatureClassifier 의 24-line byte-equal 중복 (Major-1) 와
/// Strategy.Build 안 evaluateSignature 재호출 (Major-4) 를 동시 해소하는 공통 dispatch helper.
///
/// **Major-1 (중복 제거)**: 두 classifier 의 classify 본문은 동일 4 단계 dispatch
/// (Signature 평가 → 첫 Matched → Build 호출 → NearMiss/Unmatched fallback). 본 helper 가
/// generic 으로 dispatch 만 담당하고 strategy 별 (name, Signature, Build) 를 lambda 인자로 받음.
/// IXlsxStrategy / IPdfStrategy interface 의존 0 → 두 strategy interface 의 변경 영향 최소.
///
/// **Major-4 (evaluateSignature 재호출 제거)**: helper 가 평가한 `SignatureResult` 를
/// 매치 strategy 의 `Build` 에 forward — strategy 가 동일 `evaluateSignature` 를 다시 호출하던
/// 중복 비용 (자료 B PDF 의 경우 zone code count 등 O(N) 작업 2회) 제거.
///
/// helper 가 interface 회피 lambda 방식 채택 — 새 ISignedStrategy 부모 interface 신설 0
/// (interface 안정성 유지 + SignatureResult/StrategyOutcome SSOT 위치 그대로).
module SignatureClassifierHelpers =

    /// strategy 의 dispatch 에 필요한 4종 fn 묶음. classifier 가 strategy 마다 1개씩 박제.
    [<NoComparison; NoEquality>]
    type StrategyDispatch = {
        /// strategy 의 사람-읽는 이름 (`IoListStrategy` 등). NearMissEntry.CandidateStrategy + Matched 첫
        /// 인자에 박제.
        Name: string
        /// 입력 ExtractedDocument 의 signature 평가. Build 진입 여부 + NearMiss 진단용 score 산출.
        EvaluateSignature: ExtractedDocument -> SignatureResult
        /// signature 매치 후 markdown 빌드 진입점. helper 가 평가한 SignatureResult 를 forward —
        /// strategy 안 evaluateSignature 재호출 제거 (Major-4 fix).
        Build: string * ExtractedDocument * SignatureResult -> StrategyOutcome
    }

    /// 4 단계 dispatch (XlsxSignatureClassifier / PdfSignatureClassifier 공용):
    ///   1. priority 순 strategy 순회 → Signature 평가.
    ///   2. 첫 Matched strategy 의 Build 호출 → Built / Rejected → ClassificationResult 박제.
    ///   3. 매치 0건 + score > 0 strategy 있으면 NearMiss list 반환.
    ///   4. 모든 strategy score 0 → Unmatched.
    ///
    /// `sourcePath` = NearMissEntry.File 박제 + strategy.Build forward.
    let classify
        (strategies: StrategyDispatch list)
        (sourcePath: string)
        (extracted: ExtractedDocument) : ClassificationResult =
        let evaluations =
            strategies
            |> List.map (fun s -> s, s.EvaluateSignature extracted)
        let matched = evaluations |> List.tryFind (fun (_, sigR) -> sigR.Matched)
        match matched with
        | Some (strategy, sigR) ->
            match strategy.Build (sourcePath, extracted, sigR) with
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
