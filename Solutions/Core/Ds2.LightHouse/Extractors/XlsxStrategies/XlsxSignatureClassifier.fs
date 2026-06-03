namespace Ds2.LightHouse.Extractors.XlsxStrategies

open Ds2.LightHouse
open Ds2.LightHouse.Extractors

/// **PR-I1 (todo-documents-based-gfm.md §2 + documents-based-gfm.md §8.5.7)** —
/// xlsx strategy classifier 진입점. 등록된 strategy list 를 priority 순으로 평가 후
/// 매치된 strategy 의 `Build` 호출.
///
/// **strategy list** = `[ IoListStrategy ]` 단독 (2026-06-04 WorkOrderStrategy 제거).
/// IoList 만 specialized strategy 로 정제 → SpecializedDigestBuilder 가 cache breakpoint 3 상시 주입.
/// WorkOrder/제어 PDF 는 일반 KB 자료 (specialized 미적용) — WorkOrderStrategy.fs 코드는 보존(등록만 해제).
///
/// **Backlog C (PR-I2 검열 Major 2)**: 종전 xlsx 전용 분류 DU 를 `ClassificationResult`
/// (Extractors/ClassificationResult.fs) 로 통합. case 명 `Rejected` → `RejectedByStrategy` 정합
/// (PR-I2.5 Major 3 의 pdf classifier 와 동일 표기 — `StrategyOutcome.Rejected` 와 혼동 차단).
///
/// 분류 결과 표현 (`ClassificationResult` 참조):
///   - `Matched (strategy, markdown)` — 매치 + 변환 성공.
///   - `RejectedByStrategy entry` — signature 매치는 되었으나 변환 실패 (또는 매크로 xlsx 등 비처리).
///   - `NearMiss entries` — strategy score > 0 이나 threshold 미달 (사용자 진단용).
///   - `Unmatched` — 모든 strategy 가 score 0 또는 DocType 불일치 (조용히 fallback).
///
/// strategy classifier — 등록된 strategy list 의 priority 순 평가 + dispatch.
module XlsxSignatureClassifier =

    /// 등록 strategy list = `[ IoListStrategy ]` 단독.
    /// **2026-06-04 (XlsxSignatureClassifier.fs)**: WorkOrderStrategy 제거 — IoList 만 specialized strategy 로
    /// 정제·상시 주입. WorkOrder(조립작업서)는 일반 KB 자료로 색인 (text dump + summary + 검색, specialized 미포함).
    let strategies : IXlsxStrategy list = [
        IoListStrategy() :> IXlsxStrategy
    ]

    /// 입력 `ExtractedDocument` 의 strategy 매치 시도.
    /// 동작:
    ///   1. priority 순 strategy 순회 → Signature 평가.
    ///   2. 첫 매치 strategy 의 Build 호출 → Built / Rejected 결과 그대로 반환.
    ///   3. 매치 strategy 0건 + score > 0 strategy 가 있으면 NearMiss list 반환.
    ///   4. 모든 strategy score 0 → Unmatched.
    ///
    /// **라운드 3 Major-1 + Major-4 통합 fix**: dispatch 본문은 `SignatureClassifierHelpers.classify`
    /// SSOT 호출로 단축 (PdfSignatureClassifier 와 byte-equal 중복 제거). 매치 strategy 의 Build 에
    /// helper 평가 `sigResult` 가 forward → strategy 안 evaluateSignature 재호출 제거.
    let classify (sourcePath: string) (extracted: ExtractedDocument) : ClassificationResult =
        let dispatch =
            strategies
            |> List.map (fun s -> {
                SignatureClassifierHelpers.Name = s.Name
                SignatureClassifierHelpers.EvaluateSignature = s.Signature
                SignatureClassifierHelpers.Build = s.Build
            })
        SignatureClassifierHelpers.classify dispatch sourcePath extracted
