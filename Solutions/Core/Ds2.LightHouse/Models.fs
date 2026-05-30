namespace Ds2.LightHouse

open System

/// KB 추출기 라우팅 전용 DU (done-lighthouse-kb-index.md §3.3).
///
/// chat 측 `Ds2.LlmAgent.Classification` 과 독립 — 두 분류기는 출력 DU 가 다름 (§3.0/§3.11).
/// Phase 1 활성: Pdf / Docx / Text / Markdown. Phase 2 활성: Pptx / Xlsx / Image.
/// `Image` (Task 7) = standalone image 파일 (PNG / JPEG / GIF / WEBP raw + EMF / WMF Metafile→PNG 변환).
/// `Unsupported` 의 ext 는 항상 소문자 + leading dot (`Classifier.classifyForKb` 의 `extOf` 결과).
type FileKind =
    | Pdf
    | Docx
    | Pptx
    | Xlsx
    | Text
    | Markdown
    | Image
    | Unsupported of ext: string

/// OutlineNodes.NodeType column (§3.12) 의 type-safe DU.
///
/// 저장 시 lower-case 문자열 (`section` / `page` / `sheet` / `slide` / `heading`) 로 변환.
/// 새 type 추가 시 SqliteStore 의 매핑 동시 갱신.
type OutlineNodeType =
    | Section
    | Page
    | Sheet
    | Slide
    | Heading

/// Documents row (§3.12 schema 와 1:1 대응).
///
/// `OriginalPath` = 사용자 폴더 안 실제 파일 경로 (r4 — 사본 보관 X). collection root 기준 상대경로 또는 절대경로.
/// `IngestedAt` = ISO-8601 (SqliteStore 가 변환).
type Document = {
    Id: int64
    FileHash: string
    OriginalPath: string
    DocType: FileKind
    SizeBytes: int64
    PageOrSheetCnt: int option
    Title: string option
    SummaryText: string option
    IndexerVersion: string
    IngestedAt: DateTime
}

/// OutlineNodes row (§3.12). 계층은 `ParentId` 로 표현 (NULL = root).
type OutlineNode = {
    Id: int64
    DocumentId: int64
    ParentId: int64 option
    Ordinal: int
    NodeType: OutlineNodeType
    Label: string
    RefLocator: string
}

/// Chunks row (§3.12). FTS5 mirror (`ChunksFts`) 는 trigger 가 sync — 본 record 에는 미포함.
type Chunk = {
    Id: int64
    DocumentId: int64
    OutlineId: int64 option
    RefLocator: string
    Ordinal: int
    TokenCount: int
    Text: string
}

/// extractor 의 결과물. ingest 파이프라인의 staging 단계 (Indexer 가 SqliteStore 에 commit).
///
/// extractor 는 `Id` 미할당 (SqliteStore 가 INSERT 시 자동 채움). 상위 record 의 `Id` 는
/// 빈 record 표현용 placeholder — 본 단계에서는 `OutlineNodeStaged` / `ChunkStaged` 로 받음.
type ExtractedOutlineNode = {
    /// 부모 staged outline 의 list index (없으면 None = root). SqliteStore 가 Id 매핑.
    ParentIndex: int option
    Ordinal: int
    NodeType: OutlineNodeType
    Label: string
    RefLocator: string
}

/// Extractor 가 만든 raw 본문 segment. Chunker 가 token 한도 (200~500 token) 로 후처리하여 `ExtractedChunk` 로 분할.
///
/// 한 segment = 한 페이지 (PDF) / 한 슬라이드 (PPTX) / 한 시트 (XLSX) / 한 단락·문단 그룹 (TXT/MD).
/// segment 의 RefLocator 는 구조 단위와 일치 (`p=14` 단위 — chunk 가 같은 segment 에서 나오면 같은 RefLocator).
type ExtractedSegment = {
    /// 소속 outline 의 list index (없으면 None — segment 가 root 직속).
    OutlineIndex: int option
    /// segment 의 RefLocator (저장형, §3.13). 같은 RefLocator 안에서 chunk ordinal 로 sequence.
    RefLocator: string
    /// raw text — 인코딩 정규화 완료. Chunker 가 token 단위 분할.
    Text: string
}

type ExtractedChunk = {
    /// 소속 outline 의 list index (없으면 None — chunk 가 root 직속). SqliteStore 가 Id 매핑.
    OutlineIndex: int option
    RefLocator: string
    Ordinal: int
    TokenCount: int
    Text: string
}

/// Phase 2 task C (s6-r12) — extractor 가 추출한 단일 image staging.
///
/// `Bytes` = raw image bytes (sha256 산출 + blob 저장 원본). format 화이트리스트 (ImageFormat) 외 image
/// (예: PDF JPX/JBIG2 / docx EMF/WMF/BMP) 는 extractor 가 skip 또는 PNG re-encode 후 본 record 박제.
/// `RefLocator` = 저장형 (§3.13):
///   - PdfExtractor (s6-r14 옵션 B): `"p=%d"` (page 단위, segment scheme 과 동일)
///   - OoxmlExtractor (s6-r16 C4-Q2 강화): `"p=%d"` (paragraph ordinal 1-based, segment scheme 통일).
///     같은 paragraph 안 inline Drawing 들은 동일 RefLocator + `Ordinal` 만 다름.
/// `Ordinal` = 같은 RefLocator 안 N번째 (1..N).
/// `Width` / `Height` = pixel dim (extractor 가 header parse 한 값, 미상 시 None — OpenXml ImagePart 는 항상 None).
///
/// Indexer.ingestImagesIntoStore 가 본 array 를 받아 ImageStore.computeSha256 + saveBlob + upsertImageCache +
/// addImageReference 로 dispatch. ChunkId 매핑은 Phase 2 task C4 (segment→chunk 결정 후) 진입 시 강화.
type ExtractedImage = {
    Bytes: byte[]
    Format: ImageFormat
    Width: int option
    Height: int option
    RefLocator: string
    Ordinal: int
}

/// Extractor 출력 단위 (한 문서당 1개).
///
/// 빈 outline / 빈 segments / 빈 images 도 valid (예: 빈 PDF / extract_failed 의 정책 명문화) —
/// Indexer 가 빈 문서를 정상 commit.
/// `Images` = Phase 2 task C (s6-r12) 추가. Phase 1 extractor 는 항상 `[||]` 박제 (이미지 추출 미진입).
type ExtractedDocument = {
    DocType: FileKind
    /// PDF 페이지 수 / xlsx 시트 수 / pptx 슬라이드 수. txt/md 는 None.
    PageOrSheetCnt: int option
    /// 문서 제목 추정 (PDF Info.Title / docx core properties). 미상 시 None — Indexer 가 filename fallback.
    Title: string option
    Outline: ExtractedOutlineNode array
    Segments: ExtractedSegment array
    /// 추출된 image staging — Phase 2 task C 진입 후 PdfExtractor/OoxmlExtractor 가 박제. 빈 배열 = "이미지 추출 안 함".
    Images: ExtractedImage array
}

/// MCP `attachment_search` 의 query parameter (§3.10).
///
/// `FileId` = 특정 문서 한정 검색. None = 전체 (active 셋 union).
type Query = {
    Text: string
    TopK: int
    FileId: string option
}

/// `SearchHit` 이 참조하는 한 이미지 — chunk ↔ image 매핑 (`ImageReferences.ChunkId` 기반).
///
/// `HasImages` (= `Chunks.ImageCount > 0`) 의 SSOT 와 동일 셋: `updateChunkImageCounts` 가
/// `COUNT(ImageReferences WHERE ChunkId = Chunks.Id)` 로 ImageCount 를 박제하고, `ImageReferences.ChunkId` 는
/// 한 RefLocator 의 **첫 chunk** 만 가리키므로 (Indexer C4 매핑), chunkId 기반 조회가 HasImages 와 정확히 정합.
///   - `Hash` = sha256 64 char lowercase (**full** — frontend / 이미지 다운로드 endpoint 가 64자 요구).
///   - `Ordinal` = 같은 chunk 안 N번째 (ImageReferences.Ordinal, asc 정렬).
///   - `Caption` = `ImageCache.CaptionText` (미생성 시 None).
type SearchHitImage = {
    Hash: string
    Ordinal: int
    Caption: string option
}

/// MCP `attachment_search` 의 한 hit (§3.10 JSON schema 와 1:1 대응).
///
/// `Ref` = 저장형 (`RefLocator` SSOT, §3.13). 표시형 변환은 LLM / UI 책임.
/// `HasImages` = Phase 1 항상 false. Phase 2 부터 의미 부여.
/// `Images` = 그 chunk 가 참조하는 이미지 hash 목록 (HasImages 와 동일 셋, 빈 배열 허용). frontend 가
///            특정 이미지를 가리킬 수 있도록 full 64자 hash 노출 — `HasImages` 는 하위호환 유지 (기존 ev2 caller).
type SearchHit = {
    FileId: string
    FileName: string
    Ref: string
    OutlinePath: string
    Score: float
    Excerpt: string
    TokenCount: int
    HasImages: bool
    Images: SearchHitImage array
}

/// MCP `attachment_search` 응답 wrapper. `Hint` 는 0-hit 회복 안내 (예: "synonym retry").
type SearchResults = {
    Results: SearchHit array
    MoreAvailable: bool
    Hint: string option
}

/// Document 의 citation 표시형 (§3.13). lib 는 저장형만 다룸 — 표시형 변환은 호출자.
///
/// 본 record 는 LLM 응답에 citation 박을 때 호출자 (Promaker / server) 가 만드는 형식의 hint.
/// 본 lib 에서는 type 만 노출, 변환 함수 미제공.
type Citation = {
    FileName: string
    Display: string
}
