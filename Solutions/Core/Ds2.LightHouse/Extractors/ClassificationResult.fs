namespace Ds2.LightHouse.Extractors

open Ds2.LightHouse.Diagnostics

/// **Backlog C (PR-I2 검열 Major 2 통합)** — strategy classifier 진입점의 통합 분류 결과 DU.
///
/// 종전 xlsx 전용 분류 DU (XlsxSignatureClassifier.fs) + pdf 전용 분류 DU
/// (PdfSignatureClassifier.fs) 두 type 이 동일 4 case (`Matched / RejectedByStrategy / NearMiss /
/// Unmatched`) 구조로 type 명만 달랐던 결함을 단일 DU 로 통합. xlsx/pdf classifier 의 sig 만 본 type
/// 으로 정합, signature detect 로직 / score / threshold 는 각 classifier 가 별 유지 (xlsx vs pdf 의
/// 진입점 차이는 그대로 보존).
///
/// case 정의 (xlsx/pdf 공통):
///   - `Matched (name, markdown)` — signature 매치 + Build 성공 → strategy 의 markdown 박제.
///   - `RejectedByStrategy entry` — signature 매치 후 변환 실패 (매크로 xlsx / 비밀번호 / 손상 등).
///     case name 은 `StrategyOutcome.Rejected` 와 혼동 방지 위해 `RejectedByStrategy` (PR-I2.5 Major 3 정합).
///   - `NearMiss entries` — strategy score > 0 이나 threshold 미달 (near-miss 진단 list).
///   - `Unmatched` — 모든 strategy score 0 또는 DocType 불일치 (조용히 fallback).
type ClassificationResult =
    | Matched of strategyName: string * markdown: string
    | RejectedByStrategy of RejectedEntry
    | NearMiss of NearMissEntry list
    | Unmatched
