# todo: LightHouse KB — collection summary + text dump 통합 (light-house-summary branch)

> 본 design 은 **두 layer 통합**:
> - **A. keyword digest (system prompt 박제)** — `done-fix-lighthouse-search-keyword.md` (r0~r5 박제, 미진입) 의 SSOT 그대로 채택
> - **B. text dump (색인 폴더 동봉)** — 본 turn 신규 design
>
> 기존 done- 문서는 line-level 박제 (관련 코드 위치 표 §6, schema version 정책 §8) 가 reference 로 유지. 본 doc 는 *통합 plan + 신규 text dump design* SSOT.

| rev | 일자 | 주요 변경 |
|---|---|---|
| r0 | 2026-05-21 | 초안 — keyword digest (기존 박제) + text dump (신규) 통합. 7 PR 분할 plan. branch = `light-house-summary` (worktree `F:/Git/ds2/light-house-summary/`) |

---

## 1. 작업 목표

LLM 이 chat 시작 시점부터 active KB 의 *영역* + *깊은 내용* 양쪽을 인지하도록 **3-layer RAG** 구축:

| layer | 박제 위치 | 호출 시점 | 용량 |
|---|---|---|---|
| **(A) keyword digest** | system prompt (chat lifetime) | 자동 inline | ~150 token / collection |
| **(C) chunk excerpt** (기존) | `attachment_search` hit | LLM query 발화 시 | top-K × ≤4K token |
| **(B) text dump** (신규) | `attachment_fulltext` tool 호출 | LLM 자율 (search 부족 시) | ~수만 token / doc |

현재 default = (C) 만. LLM 은 chat 시작 시 KB 영역도 모르고 (`5.knowledge-base.md:1-30` 의 인용 형식 룰만 박제), 깊은 인출도 chunk 단위만 가능. (A) + (B) 추가로 trigger 정확도 + 깊은 인식 보강.

---

## 2. 배경 (검증 완료)

### 현재 LLM 의 KB 인식 = 0
- system prompt baseline = `Apps/Promaker/Promaker/LlmAgent/Prompts/{1.entities, 2.modeling, 3.tooling, 4.attachments, 5.knowledge-base, 9.environment, facts}.md` 6 파일. KB 의 *영역* / *내용* / *목록* 어느 것도 박제 안 됨.
- `LlmChatViewModel.Initialize.cs:35-103` 의 `TryCreateLightHouseSessionsAsync` 는 session token 발급만 — LLM 노출 0.
- `attachment_list / outline / search / read` MCP tool 만이 유일 정보 경로 (LLM 이 능동 호출 필요).
- 사용자 query 어휘가 chunk text token 과 매칭 안 되면 LLM 이 `attachment_search` trigger 자체 안 함.

### Documents.SummaryText 컬럼 = NULL
- `Solutions/Core/Ds2.LightHouse/SqliteStore.fs` schema 에 `SummaryText TEXT` 존재 (parent §3.12).
- parent §3.16 의 enrichment phase = "skip" 박제 → 모든 row NULL.

---

## 3. 결정된 설계 SSOT

### 3.1 (A) keyword digest layer — `done-fix-lighthouse-search-keyword.md` SSOT 채택

기존 done- 박제 그대로:
- collection-level **"topic + keywords" profile** 만 (file path / id / 문서 목록 X)
- CLI 색인 시점 자동 추출 (Stats 기반 b1 — 빈도 + stop-word + 길이≥2 + 알파/숫자/한글 필터 + **self-MATCH precision floor**)
- top-N **15 keyword/collection** (잠정)
- `meta.json` 에 `description: string`, `keywords: string[]` 두 optional 필드 추가
- KB 변경 → **다음 turn lazy apply** (`ApiChatProvider._pendingSystemPrompt` field swap)
- Fetch 경로 = `LightHouseClient.ListCollectionsAsync` (REST `GET /collections`) 만
- SSE hook = `LlmChatViewModel` 가 `LightHouseClientHolder.EventReceived` 정적 event 에 +=

기존 done- 의 §3 미결정 (i)~(iv) 잠정 default (Phase 1 단독 / top-N=15 / unigram 길이≥2 / NLTK 영문 stop-word) 그대로 채택.

기존 done- 의 §3-(v) prompt cache = **옵션 (v-b) 2 TextContent 분리** (base + digest, breakpoint 3/4) 채택.

기존 done- 의 §6 line-level 박제 (관련 코드 위치 표) + §8 schema version 정책 = reference 로 직접 참조.

### 3.2 (B) text dump layer — 신규 design

#### 위치 / 형식
- **위치**: `<source>/.lighthouse-kb/text/<docId>-<sanitized-filename>.md`
  - server storage 정합: `Collections\<guid>\.lighthouse-kb\text\<docId>-<filename>.md`
  - zip layout (parent §3.3) 확장 — server sanitize whitelist 에 `text/` prefix 추가 의무
- **형식**: **markdown** — heading 보존 + LLM 가독성 + grep 가능 (사람도 활용)
  - PDF: `## p.N` heading + 페이지 본문
  - DOCX: heading 1~6 그대로 + paragraph + table → markdown table
  - PPTX: `## slide N: <title>` + body + `> 노트:` blockquote
  - XLSX: `## sheet: <name>` + 컬럼 헤더 + 행 데이터 (markdown table)
  - TXT / MD: 그대로 복사
- **이미지 inline marker**: `![<caption>](attachment://<docId>/<ref>#img=N)` — Phase 2 D-2-2 의 VLM caption (ImageCache.CaptionText) 활용

#### 생성 시점 / 책임
- **CLI 색인 시 자동** — `Solutions/Tools/Ds2.LightHouse.Cli/Packager.fs` → 신규 `Solutions/Core/Ds2.LightHouse/TextDumper.fs` 호출
- 옵션 X — 항상 생성 (Extractor 이미 segment 단위 text 추출 → markdown 직렬화만, cost 미미)
- **lib 측 신규 module** `TextDumper.fs` (~150 line)
  - 입력: `ExtractedDocument` + `ImageCache` lookup
  - 출력: markdown string
  - 재사용 — server-side text dump 강제 재생성 시에도 호출 가능

#### Size 가드
- 단일 doc text dump **≤ 512 KB markdown** (≈ 100~150K tokens)
- 초과 시 truncate + footer `[text dump truncated at 512KB — use attachment_search for specific ref]`
- split (`.part1.md`) 미진입 — 복잡도 증가, 산업 사양서 통상 미초과

#### Chat 측 활용 — 신규 MCP tool
- **`attachment_fulltext(fileId) -> string`** (server-side `AttachmentTools` 신설)
- 단일 호출로 text dump file stream 반환
- system prompt inline 박제 안 함 — context window 부담 회피 (keyword digest 만 inline)
- 응답 size 가드 ≤ 1MB
- `5.knowledge-base.md` 룰 1줄 추가 — "전체 본문 필요 시 호출, search 만으로 부족할 때"

#### PDF 손상 / 미지원 처리
- Extractor 가 이미 fail-safe → text dump 도 빈 markdown + `[extraction failed]` footer 만 생성
- File 존재 invariant 보장 (LLM 호출 시 404 회피)

#### 재색인 trigger
- 기존 collection 의 text dump 부재 시 = 사용자 명시 "재업로드" 만 (parent D5 정합, 자동 backfill 안 함)

### 3.3 IndexerVersion bump 정책
- text dump scheme 추가는 forward-compat (기존 collection 재색인 강제 안 함, 신규 색인부터 text dump 생성)
- **잠정 IndexerVersion 2.1.0 → 2.2.0 minor bump** + range max 변경 없음 (server `config.json.template.indexerVersionRange.max` 그대로 `"2.99.99"`)
- `Meta.schema_version` (lib `SqliteStore.IndexerVersion.SchemaVersion`) bump 없음 (DB schema 미변경, text dump 는 외부 file)

---

## 4. 미결정 항목 (잠정 default 박제, 사용자 confirm 시 진입)

| # | 항목 | 잠정 default |
|---|---|---|
| 1 | (A) keyword digest 의 §3 (i)~(iv) | Phase 1 단독 / top-N=15 / unigram 길이≥2 / NLTK 영문 stop-word |
| 2 | (A) prompt cache 박제 (§3-v) | (v-b) 2 TextContent 분리, breakpoint 3/4 |
| 3 | (A) 사전작업 (§4 옵션 A vs B) | 옵션 B — wire contract test (Protocol single SSOT 라 trivial). A 의 KbSchema 공통 record 신설은 별 phase |
| 4 | (B) text dump 형식 | markdown — heading 보존 + LLM 가독 |
| 5 | (B) single file vs split | single + 512KB truncate |
| 6 | (B) MCP tool 신설 vs `attachment_read(ref=null)` 확장 | 신설 (`attachment_fulltext`) — schema 명시성 + quota 별도 박제 |
| 7 | (B) system prompt inline 정책 | keyword digest 만 inline, text dump 는 tool 호출만 |
| 8 | (B) IndexerVersion bump | 2.1.0 → 2.2.0 minor (forward-compat) |
| 9 | (B) server storage layout | `.lighthouse-kb/text/<docId>-<filename>.md` |
| 10 | (B) size 가드 단위 | 단일 doc 512KB / `attachment_fulltext` 응답 1MB |
| 11 | (B) PDF 손상 시 | 빈 markdown + `[extraction failed]` footer (file 존재 invariant) |
| 12 | (B) 재색인 trigger | 사용자 명시 재업로드만 (자동 backfill 안 함) |

---

## 5. PR 분할 (통합 7 PR)

### 그룹 A — Schema 확장 (parent §3.3 zip layout 확장)
- **PR-A**: meta.json `keywords` / `description` 두 optional 필드 추가 + zip layout 의 `text/` whitelist 추가 (server sanitize) + Tests
- **PR-B**: lib `KeywordExtractor.fs` 신설 + CLI runUpload hook + self-MATCH precision floor 단위 test

### 그룹 B — Text dump 신규
- **PR-C**: lib `TextDumper.fs` 신설 + CLI 호출 + 512KB 가드 + ImageCache caption inline + 단위 test
- **PR-D**: server-side `attachment_fulltext` MCP tool 신설 + size 가드 + audit log + IT round-trip
- **PR-E**: `5.knowledge-base.md` 룰 1줄 추가 ("전체 본문 필요 시 attachment_fulltext 호출")

### 그룹 C — Promaker chat 측 (keyword digest 활용)
- **PR-F**: `LightHouseClient.CollectionInfo` 두 필드 deserialize + `LlmChatViewModel.FetchKbProfilesAsync` + `_acceptedCollectionIds` 보관 + SSE hook + service 별 try/catch
- **PR-G**: `Apps/Promaker/Promaker/LlmAgent/SystemPrompt.cs` 의 `KbDigestBuilder.Build` + `ApiChatProvider._pendingSystemPrompt` + `SetPendingSystemPrompt` + `SendImpl` firstTurn swap + debounce 500~1000ms + Anthropic cache (v-b)

각 PR 자가 검열 trigger (CLAUDE.md ① ~ ⑤) 충족 시 sub-agent 위임 의무.

---

## 6. 변경 포인트 (요약 — 자세한 line-level 박제는 done- 참조)

### lib (`Solutions/Core/Ds2.LightHouse/`)
- 신규 `KeywordExtractor.fs` (~80 line, PR-B)
- 신규 `TextDumper.fs` (~150 line, PR-C)

### Protocol (`Solutions/Core/Ds2.LightHouse.Protocol/`)
- `MetaJson.fs:23-41` record 에 `Description` / `Keywords` 두 필드 추가 (PR-A) — single SSOT 라 server / cli / Promaker 자동 전파

### server (`Solutions/Tools/Ds2.LightHouseService/`)
- `Registry.fs:34-50` `CollectionEntry` 에 두 필드 추가 + JsonPropertyName (PR-A)
- `MetaJson.fs:19-32` `MetaJsonRegistry.toRegistryEntry` 가 두 필드 propagate (PR-A)
- `AttachmentTools.fs` 신규 method `attachment_fulltext` (PR-D)
- `ZipImport.fs` sanitize whitelist 에 `text/` prefix 추가 (**PR-C** — text dump 활성 시점 의무. PR-A 는 schema 확장만, text 파일 생성 0)

### cli (`Solutions/Tools/Ds2.LightHouse.Cli/`)
- `Program.fs` `runUpload` 에 KeywordExtractor + TextDumper 호출 (PR-B / PR-C)
- `Packager.fs` zip 패키징 시 `.lighthouse-kb/text/` 폴더 포함 (PR-C)

### Promaker (`Apps/Promaker/Promaker/`)
- `Knowledge/LightHouseClient.cs:719-728` `CollectionInfo` 에 두 필드 추가 (PR-F)
- `ViewModels/LlmChatViewModel.cs:226` `TryCreateLightHouseSessionsAsync` 확장 + `_acceptedCollectionIds` 보관 + SSE hook (PR-F)
- `LlmAgent/SystemPrompt.cs:11` + 신규 `KbDigestBuilder` (PR-G)
- `LlmAgent/Api/ApiChatProvider.cs:62, 167-171, 211` `_pendingSystemPrompt` + `SetPendingSystemPrompt` + firstTurn swap + cache 박제 (PR-G)
- `LlmAgent/Prompts/5.knowledge-base.md` 룰 1줄 추가 (PR-E)

### Tests
- `Ds2.LightHouse.Tests` — KeywordExtractor / TextDumper 단위
- `Ds2.LightHouseService.Tests` — MetaJson round-trip + Registry round-trip
- `Ds2.LightHouseService.IntegrationTests` — `attachment_fulltext` e2e + GET /collections schema
- `Promaker.Tests` — CollectionInfo deserialize + KbDigestBuilder unit + ApiChatProvider lazy apply

---

## 7. 주의사항

1. **schema bump 없음** — `MetaJsonSchema.Current=1` / `RegistrySchema.Current=1` 유지. optional 필드 추가 만 forward-compat. bump 시 기존 zip / registry.json 전체 reject (기존 done- §8 SSOT 참조).
2. **IndexerVersion 2.1.0 → 2.2.0 minor bump** (text dump scheme 추가 시점). range max 변경 없음 — 기존 collection backward-compat 보존.
3. **paired-release ps1 검증** — IndexerVersion bump 시 `Apps/Promaker/scripts/check-paired-release.ps1` 통과 의무.
4. **두 layer 독립성** — keyword digest (PR-A/B/F/G) 와 text dump (PR-A/C/D/E) 는 schema 만 공유. 한쪽 미진입 시 다른쪽 정상 동작.
5. **자가 검열** — 각 PR 별 trigger ① ~ ⑤ 충족 시 sub-agent 위임 의무. 누락 commit 차단.
6. **commit 정책** — 사용자 명시 `--gc` 또는 budget 박제 시점만. memory `feedback_commit_authorization.md` 정합.
7. **AskUserQuestion 도구 사용 금지** — `todo-lighthouse-next-session.md:512` SSOT. 의사결정 요청 시 일반 텍스트 (번호 매긴 목록) 로 박제.
8. **commit 시 자동 한글 mojibake 회피** — file 인코딩 UTF-8 (BOM 없음) 일관. commit message HEREDOC 박제.

---

## 8. 진행 순서 (다음 진입)

1. **PR-A 진입** (본 turn) — Protocol MetaJson + Registry CollectionEntry + Promaker CollectionInfo 에 두 optional 필드 추가 + 단위 test. schema bump 없음.
2. **PR-B** (별 turn) — KeywordExtractor + CLI hook + self-MATCH precision floor test
3. **PR-C** (별 turn) — TextDumper + CLI hook + 512KB 가드 + IndexerVersion 2.2.0 bump + paired-release ps1 통과
4. **PR-D** (별 turn) — server `attachment_fulltext` + size 가드 + IT
5. **PR-E** (별 turn) — `5.knowledge-base.md` 룰 1줄
6. **PR-F** (별 turn) — Promaker Client fetch + SSE hook + `_acceptedCollectionIds`
7. **PR-G** (별 turn) — KbDigestBuilder + ApiChatProvider lazy apply + debounce + cache (v-b)

각 PR 자가 검열 + commit + 빌드 / 테스트 통과 확인 후 다음 PR 진입.

---

## 9. 본 turn budget 박제 (사용자 명시 — auto commit 3회 허용)

- **Commit 1**: doc transfer (본 todo 신설, doc-only)
- **Commit 2**: PR-A schema 확장 (Protocol + Registry + CollectionInfo + 단위 test)
- **Commit 3**: 자가 검열 후 fix 또는 PR-B 진입 (budget 여유 시)
