# LightHouse — 핵심 요약 (abstract)

> **목적**: LightHouse KB(Knowledge Base) 시스템의 *항구적 사실·설계·인터페이스* 만 한 곳에 추린 참조용 abstract.
> 작업 히스토리 / commit / 검열 과정 / 폐기 대안은 모두 제외했다. 상세가 필요하면 §10 의 원본 `done-lighthouse-*.md` 로 drill-down.
>
> **버전 상수의 SSOT 는 코드** (`Solutions/Core/Ds2.LightHouse/SqliteStore.fs` 의 `module IndexerVersion`). 본 문서의 버전 값은 작성 시점 코드 기준이며, 충돌 시 코드가 우선이다.

---

## 1. 시스템 개요

LLM chat 이 외부 사양 문서(.pdf/.docx/.pptx/.xlsx/.txt/.md + 이미지)를 "도메인 지식" 으로 검색·인용하도록 하는 **사전 색인(RAG) + 중앙 공유 서비스 + MCP 검색 호스트** 인프라.

### 컴포넌트 (4 + 1 공유)

| 컴포넌트 | 프로젝트 | 언어 | 책임 |
|---|---|---|---|
| **lib 본체** | `Solutions/Core/Ds2.LightHouse` | F# net9.0 | Extract / Chunk / Index / Search / Caption (도메인 base) |
| **Protocol (공유 SSOT)** | `Solutions/Core/Ds2.LightHouse.Protocol` | F# net9.0, dep 0 | wire 상수 + MetaJson schema + ServerEventNames + CertValidator |
| **Service** | `Solutions/Tools/Ds2.LightHouseService` | F# Kestrel HTTPS | 업로드 수신·보관 / multi-tenant 공유 / MCP search host / 인증 |
| **CLI** | `Solutions/Tools/Ds2.LightHouse.Cli` | F# console | 무인 색인·업로드 (`index --upload`) |
| **Ollama adapter** | `Solutions/Tools/Ds2.LightHouse.Ollama` | F# | `IEmbeddingProvider` (bge-m3) |
| **Promaker client** | `Apps/Promaker/Promaker/Knowledge`, `…/Dialogs`, `…/LlmAgent` | C# WPF | HTTP client / 패키징 / 업로드 / UI / citation |

### 최상위 책임 분리 SSOT (R1)
- **색인 = client, 보관·공유·검색 = server.** server 는 색인을 하지 않으므로 색인 진행률 polling API 가 없다.
- **lib 양분**: write-path(`Indexer`/`Extractor`/`Chunker`/`SqliteStore` write API) = client 전용, read-path(`Searcher`/`KnowledgeBase` facade) = server 전용. 동일 lib 를 ProjectReference 하되 `KnowledgeBase` 자체가 read-only surface 라 `readonly` 플래그 불필요.
- **dependency 방향(역방향)**: `Promaker(C#) → Ds2.LlmAgent(F#) → Ds2.LightHouse(F#)`. LlmAgent 는 LightHouse 의 `detectEncoding` / `ImageFormat` 만 참조.
- **두 경로 완전 분리**: chat 첨부 drop(`Ds2.LlmAgent.AttachmentClassifier`, 색인 안 함) vs KB ingest(`Ds2.LightHouse.classifyForKb`, 색인함) — 상태/분류기/저장소 비공유. 한 경로 변경이 다른 경로에 무영향.
- **fallback 금지**: service 미가동 시 in-process search 로 회귀하지 않음(검색 SSOT 단일 유지).

### 데이터 흐름
```
[client]  폴더 → Extract → Chunk → index.db (FTS5 + vec0) + blobs/images + caption
                  → (RAG layer 산출: keyword digest / text dump / summary)
                  → zip 패키징(.lighthouse-kb + source) → POST /collections
[server]  zip 수신·검증·보관 → registry → MCP search host (attachment_*) → citation file serving
[LLM]     system prompt(keyword digest) → attachment_search / _fulltext / _summary / _read
```

---

## 2. 핵심 버전 상수 (코드 SSOT)

`SqliteStore.fs` `module IndexerVersion` (이 module 의 **첫 `[<Literal>]` 이 `Current`** — `check-paired-release.ps1` regex 의존, 순서 변경 금지):

| 상수 | 값 | 비고 |
|---|---|---|
| `IndexerVersion.Current` | `"2.3.0"` | 색인 *결과물* 변경 시 bump (forward-compat) |
| `SchemaVersion` | `"6"` | SQL 비호환 변경 시 bump → `needsRebuild` trigger. 최신 6 = `ImageReferences.CaptionChunkId` 신설(caption-as-chunk) |
| `Tokenizer` | `"trigram"` | 한국어 필수(unicode61 은 조사 결합으로 한국어 검색 불가) |
| `EmbeddingDimension` | `1024` | bge-m3 / e5-large / jina-v3 모두 1024 |
| `MaxAttachedDbs` | `10` | active 셋 최대 collection 수(SQLite ATTACH limit) |

- **paired-release**: Promaker ↔ service 의 lib version 강제 동일. `IndexerVersion.Current` ∈ service `config.json.indexerVersionRange.[min,max]` (현재 `[1.0.0, 2.99.99]`). 검출기 = `Apps/Promaker/scripts/check-paired-release.ps1`.
- **재색인 trigger SSOT** = `Meta.schema_version` drift (`needsRebuild`). minor/patch IndexerVersion bump 은 `ensureSchema` ALTER forward-compat 로 흡수, 강제 재색인 안 함.

---

## 3. 인터페이스 레퍼런스

### 3.1 REST endpoints (Service)
| Endpoint | 용도 |
|---|---|
| `POST /collections` (multipart: zip+title+overwrite) | 등록/멱등 overwrite. **collectionId = sanitized 폴더명**(guid 폐기). 동일명+`overwrite=false`→409. 신규 201 / overwrite 200 `{id}` |
| `GET /collections` | registry list |
| `GET /collections/{id}/status` | `{id, status, errorReason, lastImportedAt}` |
| `POST /collections/{id}/payload` | 같은 id 새 zip swap (.bak rename + rollback) |
| `DELETE /collections/{id}` | `Collections\<id>\` 전체 purge |
| `GET /collections/{id}/files/{fileId}` | 원문 byte stream (Range + ETag=FileHash) |
| `POST /sessions {collectionIds}` | active 셋 token 발급 → `{token, unknownIds?, unindexableIds?}` |
| `DELETE /sessions/{token}` | session 해제 |
| `GET /events` | SSE (text/event-stream, ndjson, 30s keepalive) |
| `POST /events/caption-progress` | `{collectionId, progress 0~100, message?}` → 204 |
| `POST /admin/collections/{id}/owner {user}` | ImportedBy stamp (admin) |
| `PUT /admin/collections/{id}/acl {users, readOnly}` | ACL 설정 (admin) |
| resumable upload | `POST /uploads-rs` → `PATCH /uploads-rs/{id}` → `…/finalize`(body `swapTargetCollectionId`) / `GET`(status) / `DELETE`(cancel) |

- **collection 식별 (2026-05-30 옵션 A)**: `collectionId = sanitizeTitle(폴더명)` — server guid 발급 폐기, 디렉토리 = `Collections\<폴더명>\`. 같은 폴더명 재업로드 = **멱등 overwrite**(기존 payload swap, `Registry.upsertAsync` 가 같은 Id 라 row 1개 유지 → 중복 누적 차단). `overwrite=false`(CLI `--no-overwrite`) 시 동일명 존재하면 409. resumable finalize 도 동일(명시 `swapTargetCollectionId` 없으면 폴더명 멱등).
- 공통 헤더: `Authorization: Bearer <PSK>`(전체), `X-LightHouse-Session: <token>`(session/MCP), `X-User-Identity: <username>`(의무, 누락 401).
- SSE payload: `{event, collectionId?, progress?, message?, timestamp}`. event 이름 SSOT = `ServerEventNames`(Protocol Literal).

### 3.2 MCP tools — `attachment_*`
| tool | 인자 | 반환 |
|---|---|---|
| `attachment_list` | — | active 셋 union 문서 목록 + 메타 |
| `attachment_outline` | `fileId` | TOC / 시트명 / 슬라이드 제목 트리 |
| `attachment_search` | `query, fileId?, k=5` | FTS5 trigram(BM25)→hybrid 검색 결과 (active 셋 union, 서버 측 routing — `collectionIds` 인자 없음) |
| `attachment_read` | `fileId, ref, includeImages?, caption_only?` | page/sheet/slide raw text (+ image 동봉/caption) |
| `attachment_fulltext` | `fileId` | text dump 전체 (RAG layer B) |
| `attachment_summary` | `collectionId` | doc summary (RAG layer D) |

- `attachment_search` 반환 JSON: `{ results: [{ fileId, fileName, ref, outlinePath, score, excerpt, tokenCount, hasImages }], moreAvailable, hint? }`
- **fileId 합성** = `<collection-id>:<documents-id>` (service, collection-id = sanitized 폴더명) / `<collection-index>:<docId>` (lib in-memory). cross-collection unique.
- `attachment_read` 응답 = MCP ContentBlock (text + base64 image). size 가드: 단일 ≤ ~5MB / ≤ 5장 → 초과 시 `caption_only` 자동 강등.
- citation link: `[<fileName>](attachment:///<fileId>/<ref>)` (slash 3개 의무). `attachment:///` = popup, `http(s)` = OS shell, 그 외 차단.
- **quota 가드(tool wrapper hard enforce)**: `maxCallsPerTurn=8`, `maxExcerptTokens=4000`, `maxCumulativeTokens=16000`.

### 3.3 CLI — `lighthouse-cli`
| 명령 / flag | 용도 |
|---|---|
| `index <folder> [--upload --psk --title]` | 무인 색인 + 업로드 |
| `index <folder> --skip-upload` | 색인만(`/indexer` skill Step 1, force-without-caption 자동) |
| `index <folder> --no-embedding` | embedding opt-out (BM25-only) |
| `--reuse-kb` (upload 시) | 기존 `.lighthouse-kb/` wipe 없이 그대로 zip+POST (Step 3) |
| `--no-overwrite` (upload 시) | 동일 폴더명 collection 이 server 에 이미 있으면 fail(409). default = overwrite (옵션 A) |
| `list-pending-captions <folder>` | caption=NULL row JSON stream (skill Step 2) |
| `caption-update <folder> <batch.json>` | subagent caption batch → 단일 SQLite transaction UPDATE |
| `print-caption-prompt` | lib `CaptionGenerator.CaptionPrompt` literal stdout (drift 차단 fetch) |
| env override | `LIGHTHOUSE_OLLAMA_URL/_MODEL/_DIM`, `LIGHTHOUSE_VLM_API_KEY`, `--allow-invalid-certs` |

### 3.4 디렉토리 / zip layout
```
<source 폴더>/
  ├─ <원본들>                       ← SSOT, 사본 보관 안 함
  ├─ guide/                         ← (선택) 사용자 작성 가이드 *.md (재귀) — RAG layer E 의 source
  └─ .lighthouse-kb/                ← 자동 생성(hidden), in-place 색인
      ├─ index.db                   ← SQLite (FTS5 + vec0)
      ├─ meta.json
      ├─ text/<docId>-<filename>.md ← RAG layer B (text dump, UTF-8 no-BOM, ≤512KB)
      ├─ summary.md                 ← RAG layer D (doc 1줄 요약, per-collection 1 파일)
      ├─ summary/                    ← RAG layer E (specialized digest, system prompt inline)
      │   ├─ _user-guide-<basename>.md   ← UserGuideImporter 가 guide/*.md 에서 박제 (path-sort 맨 앞)
      │   └─ <docId>-<filename>.md       ← strategy(IoList 등) 정제 산출물 (signature 매치 doc 만)
      └─ blobs/images/<sha256>.<ext>← 추출 raster (regex ^[0-9a-f]{64}\.(png|jpg|jpeg|webp|tif|jp2)$)

zip = source/ + .lighthouse-kb/{meta.json, index.db, text/, summary.md, summary/, blobs/images/}

server storage = %PROGRAMDATA%\Dualsoft\LightHouseService\
  ├─ config.json, registry.json
  ├─ Collections\<sanitized-title>\{source\, .lighthouse-kb\}   ← collectionId = 폴더명 (guid 폐기, 동일명 멱등 overwrite)
  └─ Logs\, Audit\, Staging\
```

### 3.5 DB schema (`index.db`) — 핵심 테이블
- `Documents`(Id, **FileHash UNIQUE(SHA-256)**, OriginalPath, DocType, SizeBytes, PageOrSheetCnt, Title, **SummaryText**, IndexerVersion, IngestedAt, **FileMTimeTicks**)
- `OutlineNodes`(Id, DocumentId FK CASCADE, ParentId, Ordinal, NodeType, Label, RefLocator) — ON DELETE SET NULL
- `Chunks`(Id, DocumentId, OutlineId, RefLocator, Ordinal, TokenCount, Text, **ImageCount**)
- `ChunksFts` — FTS5 external content(`content='Chunks'`, `tokenize='trigram'`) + sync trigger 3종(`Chunks_AI/AD/AU`)
- `Chunks_Vectors USING vec0(ChunkId PRIMARY KEY, embedding float[1024])` — sqlite-vec, UPSERT 미지원→DELETE+INSERT
- `ImageCache`(**ImageHash PK(sha256)**, StoredPath, MimeType, Width, Height, CaptionText, CaptionAt, CaptionModel)
- `ImageReferences`(복합 PK `(DocumentId, ImageHash, RefLocator, Ordinal)`, **CaptionChunkId**) — caption-as-chunk(Schema 6): image caption 을 별 Chunks row 로 박제 → ChunksFts BM25 retrieval 대상
- `Meta`(Key PK, Value; 필수 키 schema_version / indexer_version / tokenizer / created_at)
- 필수 INDEX: IX_Outline_Doc, IX_Outline_Parent, IX_Chunks_Doc, IX_Chunks_Outline

### 3.6 RefLocator SSOT
EBNF: `RefLocator = Unit "=" Value ( "#" SubKey "=" SubValue )*` / `Unit = "p" | "slide" | "sheet" | "image"` / `SubKey = "img"`

| 단위 | 저장형 | 표시형 |
|---|---|---|
| PDF 페이지 | `p=14` | `p.14` |
| PPTX 슬라이드 | `slide=5` | `슬라이드 5` |
| XLSX 시트(범위) | `sheet=BOM` / `sheet=BOM!A1:D40` | `시트 BOM` / `시트 BOM A1:D40` |
| 이미지(서브) | `p=14#img=2` / `slide=5#img=2` / `sheet=BOM#img=1` | `p.14 그림 2` 등 |
| standalone image | `image=1` | `이미지 1` |

DU: `RefUnit = P | Slide | Sheet | Image`, `RefSubKey = Img`. API = `tryParse` / `toStored` / `formatDisplay`. (`comments`/`footnotes`/`endnotes` scheme = OoxmlExtractor singleton parts.)

### 3.7 meta.json / registry.json (Protocol SSOT)
- `meta.json`(camelCase, `MetaJsonSchema.Current=1`): [client] schemaVersion, indexerVersion, title, sourcePathHint, fileCount, totalSourceBytes, createdAt, clientHost, clientUser / [server] id, importedAt, importedBy, storageRelPath / [optional] description, keywords[], summaryFile. zip 내 위치 `SubDir=".lighthouse-kb"`.
- `registry.json`: `{schemaVersion:1, collections:[…]}` + optional `CollectionAcl{users, readOnly}`. mutation = `SemaphoreSlim(1,1)` 직렬화 + atomic save(`.tmp`→File.Replace).

---

## 4. 서브시스템별 핵심

### 4.1 색인 엔진
- `FileKind` DU = `Pdf | Docx | Pptx | Xlsx | Text | Markdown | Image | Unsupported of ext`. `classifyForKb : string -> FileKind`.
- 추출기(`IExtractor` = Supports + Extract + IDisposable):
  - `PdfExtractor`(PdfPig 0.1.14, `ContentOrderTextExtractor` — CJK 어절 분리), 페이지 image = `page.GetImages()`+`IPdfImage.TryGetPng`.
  - `OoxmlExtractor`(OpenXml 3.5.1) — `Extract` 가 `FileKind` 로 `ExtractDocx/ExtractPptx/ExtractXlsx` 분기. `ExtractWithFailSafe` 가 5종 예외(FileFormat/OpenXmlPackage/InvalidData/IO/Xml) catch → 빈 doc 박제.
    - PPTX: `SlideIdList`의 `SlideId` 순회(`SlideParts` 직접 enumerate 금지). title/body/speaker notes + image.
    - XLSX: `expandSparseRow`로 sparse→dense(gap `""` 채움, B컬럼 silent 소실 방지). `resolveCellValue` DataType 분기. hidden/veryHidden 시트 skip, merged=top-left, 수식=cached value. `MaxXlsxColumnsPerRow=1024`.
  - `TextExtractor`(txt/md), `ImageExtractor`(standalone png/jpg/jpeg/gif/webp/emf/wmf), `MetafileConverter`/`EmfToPng`(EMF/WMF→PNG, Windows-only `System.Drawing`).
- `Chunker`: 구조 우선(PDF페이지/PPT슬라이드/Excel시트) → 200~500 token 초과 시 단락→문장→hard cut. `insertChunks` 500/commit + CancellationToken.
- `Indexer.ingest` → `FileIngestResult`(Ingested/Skipped/Failed). SHA-256 stream hash 로 idempotent. IndexerVersion drift 시 shadow rebuild(`index.db.new` → `swapShadow` atomic rename). `MinImageBytesForIndex=8192`(8KB 미만 image skip).

### 4.2 검색
- lexical: FTS5 **trigram** BM25. `buildFtsQuery`(per-token phrase, implicit AND).
- hybrid: BM25 + vector **RRF**(`RrfK=60`, `HybridFetchMultiplier=2`). vector backend = Ollama **bge-m3(1024d)**, chunk-level, **opt-in**(default off). embedding disable 시 BM25-only fallback.
- multi-collection: 각 `index.db` 를 `:memory:` main 에 read-only ATTACH(`kb0..kbN-1`) → UNION ALL. ATTACH limit 10.
- 가드: `MaxSearchTopK=100` clamp, `MaxSearchQueryLength=1024` truncate.
- server embedder lifecycle: service-singleton `OllamaEmbedder` 1회 + 매 호출 `NonOwningEmbedder(singleton)` wrap(socket exhaustion 회피). vec0 native = `runtimes/<rid>/native/vec0.dll`.

### 4.3 이미지 / VLM caption
- **eager at indexing time**: 색인 시점 client 가 모든 image 에 caption 1회 호출 + `ImageCache.CaptionText` 영구 저장. query 시점은 cache hit 만.
- provider: Anthropic vision(claude-sonnet-4-6), HttpClient 자체 wire(SDK 미사용, `x-api-key` + `anthropic-version: 2023-06-01`). lib default 는 `CaptionGenerator.noop`(caller 가 `captionGen` 주입). `CaptionResult = Captioned | SkippedCaption`.
- per-image fail-safe: 실패 시 CaptionText NULL 유지 + 다음 재색인 재시도. daily token cap 도달 시만 hard cutoff(`LlmConfig.VlmConfig`).
- invalidation: `CaptionModel` generation tier 변경 시 caption NULL reset → 재생성.
- image dedup: sha256, **per-collection 분리**(cross-collection 공유 X). 동일 image cross-slide/sheet = `ImageCache` 1행 + `ImageReferences` N행.
- ContentType 화이트리스트(4종): png/jpeg/gif/webp. EMF/WMF 는 변환, BMP/TIFF/ICO/HEIC 등은 skip(backlog).
- **별도 OCR(Tesseract)은 redundant 로 drop** — VLM caption 이 한/영 text 도 추출.

### 4.4 `/indexer` skill — subagent caption 위임 (skill path 한정)
- 목적: VLM caption 을 Anthropic direct(`LIGHTHOUSE_VLM_API_KEY`) 대신 Claude Code subagent 로 위임 → API key 불필요 + 비용을 subscription 으로 통합. server/Promaker/CI default 는 Anthropic direct 유지.
- 흐름: **Step 1**(index-only, caption=NULL) → **Step 2**(skill 이 pending row fetch → subagent 병렬 dispatch → caption 회수 → `caption-update`) → **Step 3**(`--reuse-kb` upload).
- SSOT: caption-prompt = lib `CaptionGenerator.CaptionPrompt`(Literal, skill 은 `print-caption-prompt` 로 fetch). caption-pending = SQLite(`ImageCache.CaptionText IS NULL`, manifest 파일 폐기).
- subagent 는 SQLite 직접 접근 0 — caption text+model 만 return → 단일 `caption-update` 가 `SqliteStore.openConnection` 단일 진입점으로 BEGIN→N회 UPDATE→COMMIT(atomic, idempotent, crash 시 NULL row 만 재열거).
- `CaptionModel` = `claude-{model}-via-subagent`(env `$CLAUDE_MODEL`, 미박제 시 `claude-via-subagent`) — Anthropic direct literal 과 구분.
- threshold(코드/SKILL.md 최신): **lower=1**(N≥1 이면 항상 dispatch). summary-fill K=2/P=4(1≤N≤60), caption-fill K=4/P=4(1≤N≤120, N>120 시 사용자 confirm). per-image max 2 attempts. `caption-update` 빈 batch → exit 0. Agent 호출 `max_output_tokens=300`.
- image path = `<folder>/.lighthouse-kb/blobs/images/<hash>.<ext>` — subagent 가 Read 도구로 read(prompt 직접 base64 금지). JSON = Newtonsoft + `CamelCasePropertyNamesContractResolver`.
- lib 신규: `ImageStore.listPendingCaptions : SqliteConnection -> CaptionPendingRecord seq`(`{Hash;Ext;RefLocator;DocPath}`, SQL `ImageReferences` join + `GROUP BY ImageHash` + `MIN(RefLocator)`).

### 4.5 RAG 5-layer (chat 인지)
| layer | 단위 | 박제 위치 | 호출 | scale |
|---|---|---|---|---|
| **A keyword digest** | collection | system prompt inline (`KbDigestBuilder.Build`) | 자동(firstTurn) | ~150 tok × N |
| **B text dump** | doc 전체 | `attachment_fulltext(fileId)` | LLM 능동 | 수만 tok/doc |
| **C chunk excerpt** | chunk | `attachment_search(query)` | LLM 능동 | top-K × ≤4K |
| **D doc summary** | doc 1줄 | `summary.md` + `attachment_summary(collectionId)` | hybrid | ~50 tok × M |
| **E specialized digest** | doc/guide 정제 markdown | system prompt inline (`SpecializedDigestBuilder` → cache breakpoint 3) | 자동(firstTurn) | doc당 수K~수십K tok |

- A = "어느 collection 이 어느 영역"(routing), D = "어느 file 에 어느 내용"(narrowing). 2-step 정밀화.
- keyword 추출: CLI 색인 시점 자동(빈도 + NLTK stop-word + 길이≥2 + 알파/숫자/한글 + self-MATCH precision floor), top-15/collection. collection-level topic+keywords 만(file path/id 비노출).
- summary 1줄 = `[top-5 keywords] — Title(또는 첫 sentence)`, zero-cost(LLM 호출 0). markdown heading = RefLocator 기반(`## p.N` / `## 슬라이드 N` / `## 시트 N`), 이미지는 doc 끝 `## Images (N)`.
- lazy apply: KB 변경 → 다음 panel 또는 다음 firstTurn 까지 적용 안 함. `ApiChatProvider._pendingSystemPrompt` field swap(thread-safe `Interlocked.Exchange`). Anthropic prompt cache breakpoint = base + digest + snapshot 분리(`cache_control: ephemeral`).
- 적용 provider = Api(Anthropic/OpenAI/Ollama/Groq) + Claude CLI + Codex CLI 전부. 공통 sink `ILlmSystemPromptDigestSink`(F# `LlmProvider.fs`) 를 3 provider 가 구현 → `LlmChatViewModel` 의 `ApplyPendingKbDigest`/`ApplyFetchedDigest` 가 `is ILlmSystemPromptDigestSink` 단일 캐스팅. CLI 는 firstTurn(=sessionId None) 시점에 digest 고정 후 base(Phase1c)+digest 합성본을 `--system-prompt-file`/`experimental_instructions_file` 로 전달(default 완전 치환이라 base 포함 필수, breakpoint 분리는 불가하고 단일 덩어리). debounce 500~1000ms(toggle/SSE burst 차단).
- size cap: text dump ≤512KB(생성), `attachment_fulltext` ≤1MB, `attachment_summary` ≤1MB.
- **(E) specialized digest — `guide/*` 및 strategy 산출물 → system prompt 주입** (RAG layer E, `documents-based-gfm.md §8`):
  - **색인 시점 (`/indexer`)** — 두 source 가 `<source>/.lighthouse-kb/summary/` 한 디렉토리로 모임:
    1. **strategy 산출물**: XLSX/PDF signature 매치 doc 을 정제 markdown 으로 `summary/<docId>-<filename>.md` 박제 (IoList 등, 미매치 doc 은 파일 없음).
    2. **사용자 가이드 (`UserGuideImporter.fs`)**: 사용자 작성 `<source>/guide/*.md`(재귀 sweep)에 머리말 6행(canary `pong: guide/<rel>` + `<!-- source: guide/<rel> (cross-ref-hash: sha256) -->`) prepend → `summary/_user-guide-<basename>.md` 박제(subdir = `_user-guide-<sub>-<basename>.md`). `_` prefix 라 path-sort 시 **맨 앞**(LLM 우선 인식). 원본 `guide/*.md` 는 `Classifier.isUserGuideSourcePath`=true → (A)keyword/(B)text dump/(C)chunk 색인에서 **제외**(이중 박제 회피). `GuideSubDirName="guide"` / `SummaryFilenamePrefix="_user-guide-"` SSOT.
    3. `summary/` 디렉토리는 zip 에 동봉되어 server 까지 전달.
  - **chat 시작 시점** — `LlmChatViewModel.SpecializedDigest` → `KbSpecializedDigestFetcher`(C# thin wrapper) → `SpecializedDigestBuilder.buildMany(roots)`(F# lib)가 active collection root 들의 `.lighthouse-kb/summary/*.md` 를 **path-sort 후 `\n\n---\n\n` 으로 합본**(+`MarkdownCapPolicy.applyCap` truncate, ≤`MaxMarkdownBytes`) → `ApiChatProvider.SetPendingSpecializedDigest` → 다음 firstTurn 의 system message **3번째 `TextContent`(cache breakpoint 3, §4.7)**. 빈 디렉토리/부재 시 빈 string → breakpoint 3 skip(PR-G v-b 와 wire 동치, 회귀 0). 토큰 예산 ≈ 광명2 3자료 38K(IoList 30K + WorkOrder 3K + PdfControlSpec 5K). 사용자가 `summary/*.md` 외부 수정 시 cross-ref-hash drift → cache invalidate.
  - **현황**: 색인 시점 `UserGuideImporter` + strategy 박제 = production 활성. **chat 주입(layer E)도 N8(`documents-based-gfm.md §6.1`)에서 production GUI 진입점에 연결됨**(PR-I5 의 fetch/주입 logic 을 실 trigger 에 wire) — 3경로: ① chat panel open(`Initialize.cs:63-64` `SetActiveCollectionSourceRoots(null)` + `RefreshSpecializedDigestAsync()`) ② provider 토글(`Initialize.cs:233` `ConfigureProviderAsync` → `ApplyPendingSpecializedDigest`) ③ SSE invalidate·초기 fetch(`KbProfile.cs:172` `RefreshKbDigestAsync`). `SpecializedDigestInjectionTests` + headless smoke 는 동일 path 검증.
  - **소스 = 로컬 `SourceFolder` (server 아님)**: layer E fetch 는 `<SourceFolder>/.lighthouse-kb/summary/*.md` 를 **로컬 디스크에서 직접 read** — `summary/` 가 zip 으로 server 까지 전달돼도(line 223) server-side fetch API 는 미노출(backlog). 따라서 색인 PC 가 아니라 server 업로드본만 받은 환경에서는 layer E 미부착. `SourceFolder` = `KbManagerDialog` 등록 시 박제(`LlmConfig.KbCollectionEntry.SourceFolder`, Active=true + non-empty 만 채집). 미박제 → 빈 list → breakpoint 3 skip(회귀 0).

### 4.6 XLSX 도메인 strategy (IoListStrategy)
- 광명2 IO List(43시트) 등 PLC IO list xlsx 의 가로 multi-block layout 을 long-form CSV markdown 으로 reshape.
- block 매핑 = `[leading 빈 col] + (Word/Tag/DataType/Address/Symbol) × N block + [trailing 빈 col]`. `reshapeRowToBlocks`: `i=1` 시작 + `blockSize=5` + `while i + 5 <= cells.Length`(22-col N=4, 27-col N=5 자동 흡수). trailing 빈 block skip.
- **Direction = Address prefix derive 가 SSOT**: `directionOf` — `%IW`→Input, `%QW`→Output, 외 `-`(별도 Direction 컬럼 없음).
- signature `hasFourBlockLayout` threshold = 컬럼 ≥22.
- cap policy: `MarkdownCapPolicy.applyCapFor strategyName` dispatch → IoList 전용 `applyIoListSampling`(head 10 + tail 10 + unique device base token sample 합집합, device coverage 80%+ / Direction 박제율 80%+).
- byte-equal 회귀 가드: 다른 strategy(WorkOrder/PdfControlSpec) markdown 출력 + `StrategyMarkdown` SSOT(header 6행/footer 7행/docId/fullHash/estimateTokens) 변경 금지.

### 4.7 Prompt 조립·주입 파이프라인 (`PromptLoader` / `PromakerProfile`)
system prompt 는 **두 어셈블리의 embedded `.md`(PR-S1 분리) + 선택형 작업 지침 + 운영자/사용자 DATA tier** 를 합성한다.

| tier | `.csproj` 규칙 | resource prefix | 포함 `.md` |
|---|---|---|---|
| **baseline** | `Llm.Shared.csproj` `Prompts\baseline\*.md` | `Llm.Shared.Prompts.baseline.` | `1.attachments` · `2.knowledge-base` · `3.environment` |
| **App overlay** | `Promaker.csproj` `LlmAgent\Prompts\*.md` + `CLAUDE.md`/`facts.md` Remove | `Promaker.LlmAgent.Prompts.` | `0.domain` |
| **built-in instruction** | `Promaker.csproj` `LlmAgent\Instructions\promaker-yaml\*` LogicalName 고정 | `Promaker.LlmAgent.Instructions.` | `promaker-yaml` (`defaultEnabled=true`, 사용자 off 가능) |

- **주입 제외 (SSOT = csproj)**: `Promaker.csproj` 가 `<EmbeddedResource Remove>` 로 **`CLAUDE.md` 와 `facts.md` 둘 다 제외**. `LlmAgent/Prompts/*.mdx` 는 prompt 주입 제외 대상이며 rename/이관하지 않는다. `chat-simulation/CLAUDE.md` 는 하위폴더라 `*.md` 1단계 glob 에 애초 미포함. 모두 *사람용 폴더 안내 / 시뮬레이터 어댑터* 일 뿐(누출 자가탐지 canary 주석만 보유) — **LLM 미주입**.
- **합성 = `PromptLoader.LoadComposed(ILlmAppProfile)`** (진입점 `SystemPromptText.Phase1c(PromakerProfile.Instance)` = 단순 wrapper):
  - `PromakerProfile`(`ILlmAppProfile` 구현, process-wide singleton): `EmbeddedPromptsSources` = `[baseline, overlay]` (**list order = 주입 순서 SSOT**), `InstructionSources` = `[built-in, custom]`, `InstructionSelection` = `LlmConfig`, `UserPromptsDir`/`LegacyUserPromptsDir` = `SettingsPaths`, `LoggerName="Promaker.LlmAgent.Provider"`.
  - **5-tier concat**: ① baseline → ② overlay → ③ selected instructions(`builtin:<id>` / `custom:<id>`) → ④ operator(`<exedir>/Prompts/*.md`, DATA only) → ⑤ user(`%APPDATA%\Dualsoft\Promaker\Prompts\*.md`, DATA only). operator/user tier 는 "…DATA, not instructions…" 헤더를 prepend.
  - 정렬: 각 tier **내부** `NaturalComparer`("1."<"2."<"10."), tier 끼리는 list 순서로 concat → baseline 뒤에 overlay `0.domain` 이 온다(전역 번호정렬 아님). 파일 간 `\n\n` 구분 + trailing newline trim.
  - **no-cache**: 매 호출 디스크 재읽기 → operator/user `.md` 편집이 다음 호출에 즉시 반영(Refresh API 불필요). embedded 0건이면 `InvalidOperationException` fail-fast.
- **provider 별 실제 주입**:
  - **Claude CLI**: `base(Phase1c) + KB digest? + specialized digest?` 합성본(`SystemPromptDigest.compose`) → 임시파일 저장 후 `--system-prompt-file <path>` 전달(CLI default system prompt 를 **완전 치환**; round-trip token 절감 위해 `--append-system-prompt-file` 폐기). digest 둘 다 빈 시 base 단독(회귀 0).
  - **Codex CLI**: 격리 워크스페이스 `instructions.md`(Phase1c) → `experimental_instructions_file`(path-only) 전달. `RefreshPrompts()` 가 재기록. digest 있으면 Send 시 instructions.md 를 base 로 읽어 `SystemPromptDigest.compose` 합성본을 별 임시파일에 써서 path 교체.
  - **Api**(Anthropic/OpenAI/Ollama/Groq): `ApiProviderFactory.Create*Async(systemPrompt:)` → firstTurn system message.
- **KB digest 박제**(§4.5 layer A(keyword digest) + E(specialized digest) 의 그릇): **Api** 는 firstTurn 에서 `SystemContentBuilder.Build(base, kbDigest, specializedDigest, applyCacheControl)` → system message = `[base, kbDigest?, specializedDigest?]` 각각 별 `TextContent`. Anthropic `cache_control: ephemeral` breakpoint = base + KB digest + specialized digest + snapshot(최대 4/4), digest 빈 시 base 1개만(회귀 0). **CLI**(Claude/Codex)는 동일 본문을 `SystemPromptDigest.compose` 로 단일 string 합성(breakpoint 분리 불가). lazy swap = `SetPendingSystemPrompt`/`SetPendingSpecializedDigest`(공통 `ILlmSystemPromptDigestSink`, Api 는 `Interlocked.Exchange`) → 다음 firstTurn 진입 시 `_kbDigest`/`_specializedDigest` 로 교체(in-flight turn 보호, chat-scoped). KbManager 토글/SSE `collection-*` → debounce 750ms → fetch → swap.

---

## 5. 인증 / 보안 / multi-tenant

- **전송**: HTTPS-only(plain HTTP 거부) + PSK(`Authorization: Bearer`, `CryptographicOperations.FixedTimeEquals` const-time). PSK 저장 = server DPAPI(LocalMachine) / client DPAPI(CurrentUser, per-service entropy `Promaker.LightHouseService.v1.<serviceId>`), 평문 저장 금지.
- **mTLS**(옵션): Kestrel `ClientCertificateMode` off/optional/required + `allowedThumbprints` whitelist + chain.Build() + ChainStatus audit. optional/required 시 PSK 추가 검증(defense-in-depth). cert lookup = LocalMachine\My + CurrentUser\My, NotAfter + 30일 ExpiringSoon. mtls != off 시 subject CN ↔ X-User-Identity mismatch 401(CN = `X509Certificate2.GetNameInfo(SimpleName,false)`). Thumbprint normalize SSOT = Protocol `CertValidator.normalize`.
- **session**: chat panel lifetime = session lifetime(turn 단위 재발급 X). session 당 `SqliteConnection` 1개 + ATTACH 별칭 격리(pool X). 3중 cleanup(panel close / process exit / idle TTL). MCP 401/403 시 client 가 `POST /sessions` 재발급 + 1회 retry.
- **multi-tenant**(`MultiTenantPolicy` SSOT): **T1** flat(default, 전체 공개) / **T2** per-user(X-User-Identity = ImportedBy 일치, legacy 빈 값은 전체 공개) / **T3** collection ACL(acl.users + readOnly). 격리 위반 = **Hidden→404 / ReadOnly→403**. FileServing/SSE/Middleware/UploadsEndpoint 4 surface 통과. admin = `ServiceConfig.AdminUsers` + `requireAdmin`.
- **upload 검증**: `maxUploadBytes`=10GB, `zipBombRatioLimit`=50:1, blob path regex, `..`/절대경로/symlink 거부, IndexerVersion gate(too-low/too-high → 415). zip > `ResumableUploadThresholdBytes`(256 MiB) 시 chunked 자동.
- **audit**: `Audit\audit-YYYYMMDD.log`(timestamp/user/action/collectionId/queryText 200자/result/errorReason), 365일. SSE backoff = exp 1→30s cap + 60s stable reset.
- **PII consent**: collection 등록 시 consent dialog 의무.
- **확장자 filter**: `KbExtensions` 허용 / `rejectedExtensions`(.env/실행파일/미디어/압축) 거부.

### config.json (service, schemaVersion 4)
`listenUrl: https://127.0.0.1:8443`, `tlsCertPath`, `tlsCertPasswordEncrypted`/`preSharedKeyEncrypted`(DPAPI LocalMachine), `storageRoot`, `maxUploadBytes: 10737418240`, `zipBombRatioLimit: 50`, `sessionIdleTtlMinutes: 240`, `stagingSweepIntervalMinutes: 10`, `logRetentionDays: 30`, `auditRetentionDays: 365`, `indexerVersionRange: {min:"1.0.0", max:"2.99.99"}`, `embedding: {enabled:false, baseUrl:"http://localhost:11434", model:"bge-m3", dimension:1024}`, `mtls: {mode:"off", allowedThumbprints:[]}`, `multiTenant: {mode:"T1"}`, `adminUsers: []`.

---

## 6. 동시성 / SQLite 불변식

- **단일 진입점**: 모든 DB 연결은 `SqliteStore.openConnection` 으로만(직접 `new SqliteConnection` 금지). PRAGMA SSOT = `journal_mode=WAL`, `synchronous=NORMAL`, `busy_timeout=5000`, `foreign_keys=ON`.
- registry mutation = `SemaphoreSlim(1,1)` + atomic save. resumable upload = per-uploadId `SemaphoreSlim` + Content-Length=Content-Range 검증.
- read-only collection: read OK(`Mode=ReadOnly`), write(색인/재색인/bump) fail + 안내. 자동 재색인 trigger 금지.
- EventBus 2-channel: lifecycle(capacity 32) + progress(capacity 64) 분리 — lifecycle event 가 progress burst 에 drop 안 됨(client invariant = lifecycle 우선 도착).

---

## 7. 잔여 미완 / backlog

- **IoListStrategy Phase 2** — 8-col WRS 변형(4시트 `S203_WRS`/`S204_WRS1`/`S204_WRS2`/`S205_WRS`, ~846 tag): 사용자 결정 skip, 후속 PR. `IoListStrategy.fs` 내 별 분기 신설 권고(R5 헤더 `번호/영역/변수종류/변수/타입/어드레스/설명문`).
- **IoListStrategy Phase 6** — KB 재색인 + LLM quality 검증(`/indexer` + 가이드 §1.2 정합): 사용자 수동 미완.
- **Plan B B8** — `LightHouseClient` PSK 를 `Func<SecureString?>` 로(평문 lifetime 최소화), breaking ~80-150 LOC. production risk 없음.
- **XLSX backlog** — 좌표 RefLocator(`sheet=BOM!A1:D40`)/Defined Name/Pivot/Chart/셀 메모, SVG/BMP/TIFF/ICO/HEIC image, thumbnail endpoint.

---

## 8. 코드 파일 위치 맵

- **lib** `Solutions/Core/Ds2.LightHouse/`: `Models.fs`, `RefLocator.fs`, `Classifier.fs`, `Chunker.fs`, `SqliteStore.fs`, `Searcher.fs`, `Indexer.fs`, `KnowledgeBase.fs`, `ImageStore.fs`, `CaptionGenerator.fs`, `TextEncoding.fs`, `ImageFormat.fs`, `MetafileConverter.fs`, `KeywordExtractor.fs`, `TextDumper.fs`, `SummaryBuilder.fs`, `SpecializedDigestBuilder.fs`, `UserGuideImporter.fs`, `StrategyMarkdown.fs`, `MarkdownCapPolicy.fs`; `Extractors/`(`IExtractor.fs`, `PdfExtractor.fs`, `OoxmlExtractor.fs`, `TextExtractor.fs`, `ImageExtractor.fs`, `XlsxStrategies/IoListStrategy.fs`)
- **Protocol** `Solutions/Core/Ds2.LightHouse.Protocol/`: wire 상수 + MetaJson + ServerEventNames + CertValidator
- **Service** `Solutions/Tools/Ds2.LightHouseService/`: `Config.fs`, `Program.fs`(`configureApp`), `Middleware.fs`, `Storage.fs`, `Registry.fs`, `CollectionEndpoints.fs`, `SessionEndpoints.fs`, `FileServing.fs`, `EventsEndpoint.fs`, `UploadsEndpoint.fs`, `AdminEndpoints.fs`, `EventBus.fs`, `MultiTenantPolicy`, `AttachmentTools.fs`, `SessionRegistry.fs`; `scripts/{install-service.ps1, uninstall-service.ps1, config.json.template}`
- **CLI** `Solutions/Tools/Ds2.LightHouse.Cli/`: `Program.fs`, `Packager.fs`, `Vlm.fs`
- **Ollama** `Solutions/Tools/Ds2.LightHouse.Ollama/OllamaEmbedder.fs`
- **Promaker** `Apps/Promaker/Promaker/`: `Knowledge/`(`LightHouseClient.cs`, `LightHouseClientHolder.cs`, `AttachmentIngestService.cs`, `CollectionPackager.cs`, `LightHouseServerNaming.cs`, `ServerEventNames.cs`, `SseReconnectBackoff.cs`, `KbSpecializedDigestFetcher.cs`), `Dialogs/`(`KbManagerDialog`, `ApplicationSettingsDialog`, `PskEditDialog`), `LlmAgent/`(`LlmConfig.cs`, `SystemPrompt.cs`(`KbDigestBuilder`), `Api/ApiChatProvider.cs`, `Tools/LightHouseTools.cs`, `Tools/AttachmentTools.cs`, `Prompts/5.knowledge-base.md`), `ViewModels/LlmChatViewModel*.cs`
- **scripts** `Apps/Promaker/scripts/check-paired-release.ps1`
- **tests** `Solutions/Tests/`: `Ds2.LightHouse.Tests`(lib), `Ds2.LightHouseService.Tests`, `Ds2.LightHouseService.IntegrationTests`(e2e/cli/mTLS/admin), `Promaker.Tests`

---

## 9. skill (참고)

- `/indexer` — 폴더를 collection 으로 색인/업로드(lighthouse-cli wrapping). subagent caption 위임(§4.4).
- `/search` — 색인된 폴더(`<folder>/.lighthouse-kb/index.db`) 를 hybrid(BM25 + bge-m3 ANN) 로컬 검색(서비스 업로드 없이).

---

## 10. 원본 문서 인덱스 (drill-down)

| 원본 `done-lighthouse-*.md` | 상세 내용 |
|---|---|
| `done-lighthouse-kb-index.md` | lib 본체 설계 SSOT — DB schema(§3.12), RefLocator EBNF(§3.13), 색인 파이프라인, PRAGMA(§3.17), 경로 분리(§3.0) |
| `done-lighthouse-kb-index-xlsx-pptx-images.md` | OoxmlExtractor(pptx/xlsx) + image/standalone 추출 + caption surface 상세 |
| `done-lighthouse-kb-server.md` | central Windows Service 전체 — REST/MCP/인증/mTLS/multi-tenant/resumable/audit (가장 방대) |
| `done-lighthouse-index-summary.md` | RAG 4-layer(keyword digest / text dump / summary) + KbDigestBuilder + prompt cache |
| `done-lighthouse-next-session.md` | Protocol SSOT(K4) + MultiTenantPolicy + sqlite-vec + EventBus 등 인수인계 사실 |
| `done-lighthouse-indexer-claude-caption.md` | `/indexer` skill subagent caption 위임 — CLI 4 entry + JSON contract |
| `done-lighthouse-iolist-v2.md` | IoListStrategy v2 — IO list xlsx block reshape + Direction derive + cap policy |
| `done-lighthouse-backlog.md` | Plan B(Promaker MCP fan-out) 잔여 backlog(B8 SecureString PSK) |
