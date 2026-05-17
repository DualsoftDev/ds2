namespace Ds2.LightHouse

open System

/// KB 추출기 라우팅 전용 DU (todo-lighthouse-kb-index.md §3.3).
///
/// chat 측 `Ds2.LlmAgent.Classification` 과 독립 — 두 분류기는 출력 DU 가 다름 (§3.0/§3.11).
/// Phase 1 활성: Pdf / Docx / Text / Markdown. Phase 2 활성: Pptx / Xlsx.
/// `Unsupported` 의 ext 는 항상 소문자 + leading dot (`Classifier.classifyForKb` 의 `extOf` 결과).
type FileKind =
    | Pdf
    | Docx
    | Pptx
    | Xlsx
    | Text
    | Markdown
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

/// Extractor 출력 단위 (한 문서당 1개).
///
/// 빈 outline / 빈 segments 도 valid (예: 빈 PDF / extract_failed 의 정책 명문화) — Indexer 가 빈 문서를 정상 commit.
type ExtractedDocument = {
    DocType: FileKind
    /// PDF 페이지 수 / xlsx 시트 수 / pptx 슬라이드 수. txt/md 는 None.
    PageOrSheetCnt: int option
    /// 문서 제목 추정 (PDF Info.Title / docx core properties). 미상 시 None — Indexer 가 filename fallback.
    Title: string option
    Outline: ExtractedOutlineNode array
    Segments: ExtractedSegment array
}

/// MCP `attachment_search` 의 query parameter (§3.10).
///
/// `FileId` = 특정 문서 한정 검색. None = 전체 (active 셋 union).
type Query = {
    Text: string
    TopK: int
    FileId: string option
}

/// MCP `attachment_search` 의 한 hit (§3.10 JSON schema 와 1:1 대응).
///
/// `Ref` = 저장형 (`RefLocator` SSOT, §3.13). 표시형 변환은 LLM / UI 책임.
/// `HasImages` = Phase 1 항상 false. Phase 2 부터 의미 부여.
type SearchHit = {
    FileId: string
    FileName: string
    Ref: string
    OutlinePath: string
    Score: float
    Excerpt: string
    TokenCount: int
    HasImages: bool
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
