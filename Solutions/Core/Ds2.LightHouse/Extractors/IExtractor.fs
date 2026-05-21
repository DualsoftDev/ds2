namespace Ds2.LightHouse.Extractors

open System
open System.Threading
open Ds2.LightHouse

/// KB ingest extractor 공통 interface (done-lighthouse-kb-index.md §4.3).
///
/// 각 구현체 (PdfExtractor / OoxmlExtractor / TextExtractor) 가 `Supports` 로 type-safe routing,
/// `Extract` 가 동기 호출로 `ExtractedDocument` 반환. long-running 추출은 CancellationToken 으로 중단.
///
/// IDisposable 상속 사유: 일부 extractor (PdfPig / OpenXml) 가 내부적으로 native handle / temp resource 를
/// 유지할 가능성. stateless 구현은 빈 `Dispose` OK. Indexer 가 한 ingest run 마다 `use` 로 lifecycle 관리.
type IExtractor =
    inherit IDisposable

    /// 본 extractor 가 처리 가능한 FileKind. Indexer 가 routing 시 호출 — 첫 매칭 extractor 가 추출.
    abstract member Supports: kind: FileKind -> bool

    /// 단일 파일 추출. 손상 / 암호화 / 미지원 nested format 은 fail-safe (log + 빈 ExtractedDocument) 정책 (§3.16).
    /// 단 본 interface 는 정책 강제 안 함 — 구현체가 fail-fast 와 fail-safe 사이를 선택.
    abstract member Extract: path: string * ct: CancellationToken -> ExtractedDocument
