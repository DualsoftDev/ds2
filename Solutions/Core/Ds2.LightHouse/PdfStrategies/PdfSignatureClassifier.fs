namespace Ds2.LightHouse.PdfStrategies

open Ds2.LightHouse
open Ds2.LightHouse.Extractors
open Ds2.LightHouse.Extractors.XlsxStrategies

/// **PR-I2 (todo-documents-based-gfm.md §2 + §2.1 + documents-based-gfm.md §8.5.7)** —
/// pdf strategy classifier 진입점. 등록된 strategy list 를 priority 순으로 평가 후 매치된 strategy 의 `Build` 호출.
///
/// **strategy list** = 빈 list (2026-06-04 PdfControlSpecStrategy 제거 — PDF 는 일반 KB 자료, specialized 미적용).
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

    /// 등록 strategy list = 빈 list (2026-06-04 PdfControlSpecStrategy 제거 — PDF 는 일반 KB 자료).
    /// 제어시스템 PDF 는 specialized 정제 없이 일반 색인 (WorkOrder 와 동일 정책). PDF 전용 strategy 가
    /// 다시 필요해지면 본 list 에 등록 (PdfControlSpecStrategy.fs 코드는 보존).
    let strategies : IPdfStrategy list = []

    /// 입력 `ExtractedDocument` 의 strategy 매치 시도.
    /// 동작:
    ///   1. priority 순 strategy 순회 → Signature 평가.
    ///   2. 첫 매치 strategy 의 Build 호출 → Built / Rejected 결과 그대로 반환.
    ///   3. 매치 strategy 0건 + score > 0 strategy 가 있으면 NearMiss list 반환.
    ///   4. 모든 strategy score 0 → Unmatched.
    ///
    /// **라운드 3 Major-1 + Major-4 통합 fix**: dispatch 본문은 `SignatureClassifierHelpers.classify`
    /// SSOT 호출로 단축 (XlsxSignatureClassifier 와 byte-equal 중복 제거). 매치 strategy 의 Build 에
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
