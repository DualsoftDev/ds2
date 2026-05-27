namespace Ds2.LightHouse.PdfStrategies

open Ds2.LightHouse
open Ds2.LightHouse.Diagnostics
open Ds2.LightHouse.Extractors.XlsxStrategies

/// **PR-I2 (todo-documents-based-gfm.md §2 + §3.1 + documents-based-gfm.md §8.5/§8.5.7)** —
/// pdf strategy interface SSOT (IXlsxStrategy 동등 패턴).
///
/// 입력 = PdfExtractor 가 추출한 `ExtractedDocument` (DocType=Pdf) + source 경로.
/// 출력 = signature 매치 + 변환 성공 시 markdown string, 미매치 또는 변환 실패 시 RejectedEntry.
///
/// **설계 의도** (todo §2.1):
///   - IXlsxStrategy 와 패턴 동등 (Name / Version / Signature / Build) — classifier 재사용성 보장.
///   - SignatureResult / StrategyOutcome 은 XlsxStrategies 에서 **재사용** (별 type 신설 금지).
///     pdf / xlsx 모두 같은 진단 schema 박제 (NearMissEntry / RejectedEntry).
///   - 진정한 예외 (예: 손상된 input 의 NRE) 는 reraise → fail-fast 정합 (CLAUDE.md).
type IPdfStrategy =
    /// strategy 의 사람-읽는 이름 (`PdfControlSpecStrategy` 등).
    abstract member Name : string
    /// semver string (예: `"1.0.0"`). MAJOR bump 시 기존 색인 stale 처리 (§8.5.8).
    abstract member Version : string
    /// 입력 pdf 의 signature 매치 평가. 변환 단계 진입 여부 결정.
    abstract member Signature : ExtractedDocument -> SignatureResult
    /// signature 매치 후 markdown 빌드 진입점. `sourcePath` = cross-ref-hash 계산 + diagnostic 식별용.
    abstract member Build : sourcePath: string * extracted: ExtractedDocument -> StrategyOutcome
