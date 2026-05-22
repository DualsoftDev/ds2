# todo: LightHouse KB — collection summary + text dump 통합 (light-house-summary branch)

> 본 design 은 **두 layer 통합**:
> - **A. keyword digest (system prompt 박제)** — `done-fix-lighthouse-search-keyword.md` (r0~r5 박제, design SSOT 만 박제) 의 결정 표 그대로 채택
> - **B. text dump (색인 폴더 동봉)** — 본 worktree 신규 design
>
> 기존 done- 문서는 line-level 박제 (관련 코드 위치 표 §6, schema version 정책 §8) 가 reference 로 유지. 본 doc 는 *통합 plan + 신규 text dump design + 진행 상태* SSOT.

| rev | 일자 | 주요 변경 |
|---|---|---|
| r0 | 2026-05-21 | 초안 — keyword digest (기존 박제) + text dump (신규) 통합. 7 PR 분할 plan. branch = `light-house-summary` (worktree `F:/Git/ds2/light-house-summary/`) |
| r1 | 2026-05-21 | **PR-A/B/C/D 진입 완료** (commit 6 누적 / +1072 line / +25 신규 fact). 잔여 = PR-E/F/G (Promaker WPF UI 영역). PR-F/G scope 상세화 박제 (직전 turn 사용자 질문 답변 흡수) |
| r2 | 2026-05-21 | **PR-F/G+E 진입 완료** (commit 2 추가 — `c93b591` PR-F / `ae58e11` PR-G+E / +798 line / +24 fact). **모든 7 PR 완료**. Promaker.Tests 340 → 364 (+24). 잔여 backlog: ApiChatProvider stale cache (ReloadKbConfig invalidate), KbDigestBuilder service 헤더 (multi-service), CLI provider feedback (§4 #3) |
| r3 | 2026-05-22 | **본 worktree 재진입** (light-house merge 후 fork). 검수용 진입 1 commit (`682fc812` runIndex 에 dump hook) + CJK 띄어쓰기 fix 1 commit (`f88e60c4` PdfExtractor `ContentOrderTextExtractor`). 신규 **PR-H summary.md** 결정 박제 (§11) — P1→P2 점진, P1 (방법 3 zero-cost) → 검증 후 P2 (방법 5 subagent batch + MCP tool) 자율 진입. P1 (`91d0df55`) + P2 (a) `8f6234c7` / (b) `91eaacfd` / (c) `f3a79dce` 5 commit 박제 (`e2ad3121` doc + P1 + P2 a/b/c) |
| r4 | 2026-05-22 | **외부 multi-reviewer (`--inspect-diff 7`) 종합 메타 리뷰** 처리 — 채택 7 / 반박 3. 단일 commit (`c6c96fb4`) 통합. (a) fail-safe = silent misreport (n+ExecuteNonQuery, summary+caption 양쪽) + JSON null guard (runSummary/CaptionUpdate). (b) SSOT = TextDumper.sanitizedFilename public + SummaryBuilder/AttachmentTools 사본 제거 + dead _title YAGNI. (c) refactor = runPostIngestHooks + requireIndexedFolder + camelJsonOpts helper (Program.fs ~80 line 감축). 반박 = attachment_fulltext IO helper (PR-D 회귀 위험), CLI integration framework (비용↑가치↓, lib unit test fact 1로 대체), write 반환 path eprintfn (caption 일관성 우선) |

---

## 0. 현재 진행 상태 (새 세션 진입 시 첫 정독)

### 완료 (본 worktree commit `7964ec6..HEAD`, 8 commit) — **모든 7 PR 완료**

| commit | scope | 신규 fact |
|---|---|---|
| `2572c6e` | r0 doc transfer — keyword digest + text dump 통합 design SSOT | 0 (doc-only) |
| `de14577` | PR-A schema 확장 — Protocol MetaJson + Registry CollectionEntry + Promaker CollectionInfo 에 description / keywords optional 필드 | +6 (MetaJson 3 / Registry 2 / LightHouseClient 1) |
| `2f43d57` | PR-A 자가 검열 — doc drift fix (`text/` whitelist PR-A → PR-C scope 정정) | 0 |
| `ddaf0d6` | PR-B KeywordExtractor — lib KeywordExtractor.fs (b1 stats + NLTK 영문 stopword + 길이≥2 + self-MATCH precision floor) + Packager.writeMeta signature breaking + CLI runUpload hook | +7 |
| `714b8ad` | PR-C TextDumper — lib TextDumper.fs (markdown heading by RefLocator + ImageReferences caption inline + 512KB cap) + IndexerVersion 2.1.0→2.2.0 | +7 |
| `494c4ed` | PR-D attachment_fulltext MCP tool — server AttachmentTools.attachment_fulltext + 1MB cap + 4 분기 audit log + legacy backward-compat | +5 |
| `c93b591` | **PR-F** FetchKbProfilesAsync + SSE hook + debounce — KbProfileExtractor (internal helper) + LlmChatViewModel.KbProfile.cs partial + Initialize.cs/DisposeAsync hook + 자가 검열 Major-1 fix (dispose race try/catch) | +14 (Theory inline 5+1+3 fact, 8 method) |
| `ae58e11` | **PR-G+E** KbDigestBuilder + ApiChatProvider lazy apply (v-b) + 5.knowledge-base.md fulltext 룰 — KbDigestBuilder.Build (digest text 박제) + SystemContentBuilder (base+digest 2 TextContent 분리) + ApiChatProvider._pendingKbDigest + SetPendingSystemPrompt (Interlocked.Exchange) + firstTurn swap + 자가 검열 Minor 1+4 fix | +10 (KbDigestBuilder 5 / SystemContentBuilder 5) |

**누적 신규 fact = +49**. 누적 test 통과 (회귀 0):
- Ds2.LightHouse.Tests: 268 → **282** (+14)
- Ds2.LightHouseService.Tests: 179 → **189** (+10)
- Ds2.LightHouseService.IntegrationTests: 55 → **57** (+2)
- Promaker.Tests: 339 → **364** (+25 — PR-A 1 + PR-F 14 + PR-G 10)
- Ds2.LlmAgent.Tests: 398 (변경 무관)

### 잔여 backlog (모든 PR 완료 후 우선순위 별 turn 또는 Phase 2)

1. **TextDumper size cap linear search** — `applySizeCap` 의 `while ... > availableBytes do cut <- cut - 1` 가 큰 markdown 에서 O(N²) 위험. binary search 또는 UTF-8 byte streaming truncate. (Phase 2 perf)
2. **attachment_fulltext legacy collection audit level** — 현재 text/ 폴더 부재 시 `Log.audit.Info` 박제. legacy = backward-compat 의도라 Info OK 이나, 운영상 detection 강화 시 `Warn` 으로 격상 검토.
3. **PR-F 자가 검열 Major-2** — `LlmChatViewModel.ReloadKbConfig` / `UpdateStore` 후 `_kbProfileCache` stale. KbManagerDialog 에서 collection 토글 변경 시 다음 fetch 가 server-side 변경 (예: keywords 재추출) 반영하려면 cache invalidate hook 필요. user-visible bug 0 (cache 의 keyword 만 stale, accepted set 은 session 발급 시점 셋 정합) — 향후 Phase 2.
4. **PR-G 자가 검열 Minor 2** — `KbDigestBuilder` 의 다중 service 평탄화. 동일 displayName 이 service 별 존재 시 LLM 이 모호 인식. 현재 회귀 0, multi-service 운용 단계에서 service 헤더 도입 고려.
5. **PR-G 자가 검열 Minor 3** — Claude/Codex CLI provider 사용 중 KB 토글 변경 시 user 무피드백 (§4 #3 미결정 정합). 별 PR 에서 chip 안내 또는 disable 처리.
6. **e2e 검증 (사용자 수동)** — §5.2 의 4 항목 — Promaker 시작 → chat panel open → system message 에 `# ─── Active Knowledge Bases ───` 포함 / KbManagerDialog 토글 → debounce 후 다음 turn 갱신 / Anthropic `cache_read_input_tokens` 측정 / LLM 자발적 `attachment_search` 호출 빈도 증가.

### r3 진입 commit (light-house merge 후 본 worktree 재진입)

| commit | scope | 신규 fact |
|---|---|---|
| `682fc812` | **runIndex 에 dump hook** — `--skip-upload` 후 `.lighthouse-kb/text/` 박제 (upload 전 검수). `runUpload` 와 동일 패턴, `meta.json` 박제 안 함 (충돌 회피). ingested 분기 없이 항상 호출 (fast-skip 케이스도 dump) | 0 (CLI integration test 부재) |
| `f88e60c4` | **PdfExtractor CJK 띄어쓰기 fix** — `page.Text` (글자 stream raw concat) → `ContentOrderTextExtractor.GetText page` (PdfPig DocumentLayoutAnalysis). 한글/CJK 어절 단위 word/line 분리 + 공백 자동 박제. per-page try/with 가드 (자가 검열 Major-1 fix — image 추출 패턴 정합) | 0 (기존 fixture 회귀 0, PdfExtractorTests 통과) |

**검증** (`/f/tmp/g/3.광명2_전동화공장_제어시스템(HMI편집됨).pdf`):
- before: `자동화기술실설비제어기술2팀2022. 12. 15...` (128,960 byte, 모든 글자 붙음)
- after: 어절/문장 단위 띄어쓰기 + line 분리 (`광명2 전동화공장(SV) 제어시스템`, `○ 의사결정`, 146,822 byte / +12%)

### 본 worktree merge 권장 next step

- §11 PR-H 진행 완료 후 `light-house` branch merge (사용자 결정 — fast-forward vs squash)
- 본 worktree 자체는 r2 merge 후 재활용 — 새 PR-H scope 흡수 중

---

## 1. 작업 목표 (불변)

LLM 이 chat 시작 시점부터 active KB 의 *영역* + *깊은 내용* 양쪽을 인지하도록 **3-layer RAG** 구축:

| layer | 박제 위치 | 호출 시점 | 용량 |
|---|---|---|---|
| **(A) keyword digest** | system prompt (chat lifetime) | 자동 inline | ~150 token / collection |
| **(C) chunk excerpt** (기존) | `attachment_search` hit | LLM query 발화 시 | top-K × ≤4K token |
| **(B) text dump** (신규 — PR-C/D 완료) | `attachment_fulltext` tool 호출 | LLM 자율 (search 부족 시) | ~수만 token / doc, server 응답 ≤1MB |

현재 default = (C) 만. PR-A/B/C/D 완료로 (B) 활성 — `lighthouse-cli index --upload` 가 색인 시 `.lighthouse-kb/text/<docId>-<filename>.md` 생성 + server 가 `attachment_fulltext` MCP tool 으로 stream. (A) 는 PR-G 진입 후 활성.

---

## 2. 배경 (불변)

### 현재 LLM 의 KB 인식 = 0 (PR-G 진입 전)
- system prompt baseline = `Apps/Promaker/Promaker/LlmAgent/Prompts/{1.entities, 2.modeling, 3.tooling, 4.attachments, 5.knowledge-base, 9.environment, facts}.md` 6 파일. KB 의 *영역* / *내용* / *목록* 어느 것도 박제 안 됨.
- `LlmChatViewModel.Initialize.cs:35-103` 의 `TryCreateLightHouseSessionsAsync` 는 session token 발급만 — LLM 노출 0.
- `attachment_list / outline / search / read / fulltext` MCP tool 만이 유일 정보 경로 (LLM 능동 호출 필요).
- 사용자 query 어휘가 chunk text token 과 매칭 안 되면 LLM 이 `attachment_search` trigger 자체 안 함 → PR-F/G 가 keyword digest 로 trigger 정확도 향상.

### Documents.SummaryText 컬럼 = 항상 NULL
- `Solutions/Core/Ds2.LightHouse/SqliteStore.fs` schema 에 `SummaryText TEXT` 존재 (parent §3.12).
- parent §3.16 enrichment phase = "skip" 박제 → 모든 row NULL.
- 본 worktree 의 PR-C 가 text dump 로 대체 (외부 markdown file artifact).

---

## 3. 결정된 설계 SSOT

### 3.1 (A) keyword digest layer — `done-fix-lighthouse-search-keyword.md` SSOT 채택

> **PR-B 진입 완료 (commit `ddaf0d6`)** — lib KeywordExtractor.fs 본 design SSOT 정합 구현. 사용자 측 노출 (system prompt) 은 PR-G 진입 후 활성.

설계 결정 그대로:
- collection-level **"topic + keywords" profile** 만 (file path / id / 문서 목록 X)
- CLI 색인 시점 자동 추출 (Stats 기반 b1 — 빈도 + stop-word + 길이≥2 + 알파/숫자/한글 필터 + **self-MATCH precision floor**)
- top-N **15 keyword/collection** (잠정 default 채택)
- `meta.json` 에 `description: string`, `keywords: string[]` 두 optional 필드 추가 (PR-A 완료)
- KB 변경 → **다음 turn lazy apply** (`ApiChatProvider._pendingSystemPrompt` field swap) — PR-G 진입 시 활성
- Fetch 경로 = `LightHouseClient.ListCollectionsAsync` (REST `GET /collections`) 만 — PR-F 진입 시 활성
- SSE hook = `LlmChatViewModel` 가 `LightHouseClientHolder.EventReceived` 정적 event 에 += — PR-F 진입 시 활성

### 3.2 (B) text dump layer

> **PR-C 진입 완료 (commit `714b8ad`)** — lib TextDumper.fs + IndexerVersion 2.1.0→2.2.0 minor bump.
> **PR-D 진입 완료 (commit `494c4ed`)** — server AttachmentTools.attachment_fulltext MCP tool.

설계 결정 그대로:
- **위치**: `<source>/.lighthouse-kb/text/<docId>-<sanitized-filename>.md` (server storage: `Collections\<guid>\.lighthouse-kb\text\<docId>-<filename>.md`)
- **형식**: markdown (heading by RefLocator: `## p.N` / `## 슬라이드 N` / `## 시트 N`)
- **이미지 inline marker**: doc 끝 `## Images (N)` section + ImageStore.getCaption 박제
- **CLI 색인 시 자동** (Packager.runUpload hook)
- **Size 가드**: 단일 doc text dump ≤ 512 KB markdown (PR-C) / `attachment_fulltext` 응답 ≤ 1 MB (PR-D backstop)
- **MCP tool 신설**: `attachment_fulltext(fileId)` — system prompt inline 안 함 (tool 호출 path)
- **legacy collection** (text/ 부재): empty string + audit Info (backward-compat)
- **재색인 trigger**: 사용자 명시 "재업로드" 만 (parent D5 정합)

### 3.3 IndexerVersion bump 정책

> **PR-C 진입 완료** — IndexerVersion `"2.1.0"` → `"2.2.0"` minor bump (`Solutions/Core/Ds2.LightHouse/SqliteStore.fs:41`).
> SchemaVersion `"5"` 그대로 (DB schema 미변경).
> server `config.json.template.indexerVersionRange.max="2.99.99"` 그대로 — paired-release ps1 통과.

---

## 4. 미결정 항목 (PR-F / PR-G 진입 전 확정 의무)

| # | 항목 | 잠정 default (다음 세션 진입 시 그대로 채택 가능) |
|---|---|---|
| 1 | **PR-G Anthropic cache 옵션** | **(v-b) 2 TextContent 분리** — base + digest 각각 `cache_control: ephemeral` 박제 (breakpoint 3/4 사용 — 기존 system + snapshot 2/4 + digest 1). KB 변경 시 base 영역 cache hit 유지. |
| 2 | **PR-F debounce window** | **500~1000 ms** — KB chip 다중 toggle / SSE event burst 시 polling 폭주 차단 |
| 3 | **PR-G 적용 provider 셋** | **Api provider 만** (Anthropic / OpenAI / Ollama / Groq). Claude CLI / Codex CLI 는 system prompt 주입 path 다름 — 본 phase 미적용, 별 PR backlog |

---

## 5. PR 분할 (통합 7 PR — 진행 상태 박제)

### 그룹 A — Schema 확장 (parent §3.3 zip layout 확장) — **완료**
- [x] **PR-A** `de14577` + `2f43d57` — meta.json + registry 에 description / keywords optional 필드. schema bump 없음 (forward-compat). 신규 6 fact.
- [x] **PR-B** `ddaf0d6` — lib KeywordExtractor.fs (~110 line) + Packager.writeMeta signature breaking + CLI runUpload hook + 7 fact (self-MATCH precision floor 포함).

### 그룹 B — Text dump 신규 — **완료**
- [x] **PR-C** `714b8ad` — lib TextDumper.fs (~150 line) + CLI 호출 + 512KB cap + ImageCache caption inline (Phase 2 D-2-2 박제 활용) + IndexerVersion 2.2.0 bump + 7 fact.
- [x] **PR-D** `494c4ed` — server-side `attachment_fulltext` MCP tool + size 가드 (1MB backstop) + ServiceConfig DI 자동 주입 + 4 분기 audit log + 5 fact.
- [ ] **PR-E** — `5.knowledge-base.md` 룰 1줄 추가 ("전체 본문 필요 시 `attachment_fulltext(fileId)` 호출"). PR-G 와 묶음 가능.

### 그룹 C — Promaker chat 측 (UI 영역) — **잔여**
- [ ] **PR-F** — Promaker Client fetch + SSE hook (상세 §5.1)
- [ ] **PR-G** — SystemPrompt digest + lazy apply (상세 §5.2)

---

### 5.1 PR-F 상세 scope

**목적**: server `GET /collections` 응답에서 PR-A 가 추가한 `description` / `keywords` 두 필드를 받아서 chat ViewModel 에 보관. SSE event 받으면 cache invalidate + 다음 turn 의 system prompt swap trigger 의 input.

#### 변경 파일

1. **`Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs`** (또는 partial Initialize.cs)
   - 신규 field: `private readonly Dictionary<string, IReadOnlyList<CollectionInfo>> _kbProfileCache = new();` (key = serviceId)
   - 신규 field: `private readonly Dictionary<string, IReadOnlyList<string>> _acceptedCollectionIds = new();` (key = serviceId — TryCreateLightHouseSessionsAsync 가 박제, FetchKbProfilesAsync 의 필터 input)
   - `TryCreateLightHouseSessionsAsync` (line 226) 가 session 발급 시 `_acceptedCollectionIds[serviceId] = resp.AcceptedIds` 박제
   - 신규 method `FetchKbProfilesAsync()`:
     - 각 active service 마다 `LightHouseClient.ListCollectionsAsync()` 호출 → 응답 `IReadOnlyList<CollectionInfo>` 을 `_acceptedCollectionIds[serviceId]` 와 교차 후 cache 박제
     - service 별 try/catch (한 service 실패 ≠ chat 차단, chip 안내만)
     - in-memory cache (key = serviceId) — SSE event 가 invalidate
   - 신규 method `OnKbProfileChanged()` (PR-G 에서 impl) — 본 PR 에서는 skeleton 만 박제

2. **`Apps/Promaker/Promaker/Knowledge/LightHouseClientHolder.cs`** 의 정적 `EventReceived` 에 ViewModel 가 chat panel lifetime 동안 `+= OnSseEventReceived` (Init 진입 시점 + Dispose 시점 -= 매칭)
   - SSE event 분류:
     - `collection-added / collection-updated / collection-deleted` → 본 service 의 `_acceptedCollectionIds` 와 교차 후 cache invalidate + debounce timer trigger
     - `caption-progress` / `upload-progress` 등 progress event 무시 (digest refresh 불필요 — Burst 차단)
   - **debounce window 500~1000ms** (잠정 default) — `System.Timers.Timer` 또는 `Task.Delay` + CTS

#### 자가 검열 trigger
- ③ 2+ 파일 동시 변경 (ViewModel + Holder)
- ⑤ public API/SSOT (FetchKbProfilesAsync surface)
- → sub-agent 위임 또는 inline self-review 의무

#### 신규 fact (Promaker.Tests) — 예상 +6
- `FetchKbProfilesAsync` cache hit / miss (2 fact)
- service 실패 분기 (1 fact, mock handler 가 401 또는 timeout 박제)
- SSE event 분류 `collection-*` vs `progress-*` (2 fact)
- debounce timer (1 fact)

---

### 5.2 PR-G 상세 scope

**목적**: PR-F 가 fetch 한 keyword profile 을 LLM 의 system prompt 에 inline 박제. KB 변경 시 다음 turn 의 firstTurn 진입 시 system message swap (lazy apply, chat-scoped invariant 정합 — `LlmChatViewModel.cs:931` "active 토글은 다음 chat 부터 반영" 룰).

#### 변경 파일

1. **`Apps/Promaker/Promaker/LlmAgent/SystemPrompt.cs`** — 신규 helper class `KbDigestBuilder`:
   ```csharp
   public static class KbDigestBuilder
   {
       public static string Build(IReadOnlyList<CollectionInfo> kbs);
   }
   ```
   - 빈 리스트 → 빈 string (digest section 자체 생략 → ApiChatProvider 가 system 박제 시 자연 skip)
   - 산출물 예시:
     ```
     # ─── Active Knowledge Bases ───

     다음 영역에 해당하는 질문이면 `attachment_search(query)` MCP tool 을 호출하세요.
     전체 본문 필요 시 `attachment_fulltext(fileId)` 호출 (search 만으로 부족할 때).

     - "Poc"
         keywords: cache_rd, cache_cr, token, turn, cache, hit, steady
     - "Promaker Docs"
         keywords: prompt, cache, MCP, ApiChatProvider, ...
     ```

2. **`Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs`** (line 62 `_systemPrompt` field, line 167-171 system message 박제, line 211 snapshot block cache_control):
   - `_pendingSystemPrompt: string?` field 신설 — `Interlocked.Exchange` 또는 lock 박제 (thread-safe)
   - 신규 method `SetPendingSystemPrompt(string s)` — thread-safe write
   - `SendImpl` (line 148~) 의 첫 turn 분기 (line 155~180) 진입 시 `_pendingSystemPrompt` snapshot → `_systemPrompt` 로 적용 (swap)
   - history 시작 후 (`firstTurn=false`) swap = 다음 panel 시작까지 적용 안 됨 (chat-scoped invariant)
   - **Anthropic prompt cache 옵션 (v-b) 적용** (line 169 인근 — 두 TextContent 로 분리):
     ```csharp
     AIContent baseContent = new TextContent(_basePrompt);
     AIContent digestContent = new TextContent(_kbDigest);
     if (_applyCacheControl != null) {
         baseContent = _applyCacheControl(baseContent);
         digestContent = _applyCacheControl(digestContent);
     }
     _history.Add(new ChatMessage(ChatRole.System,
         new List<AIContent> { baseContent, digestContent }));
     ```
   - breakpoint 사용량 3/4 (base + digest + snapshot). 여유 1

3. **`Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs`**:
   - `OnKbProfileChanged()` impl (PR-F skeleton 위에서):
     1. `FetchKbProfilesAsync` 로 최신 KB profile fetch
     2. `KbDigestBuilder.Build(profiles)` 로 digest 생성
     3. `(_provider as ApiChatProvider)?.SetPendingSystemPrompt(SystemPromptText.Phase1c + digest)` 호출
   - **provider swap path 폐기** — done- 박제의 `OnSelectedProviderChanged` + `ConfigureProviderAsync` 호출 path 사용 안 함 (lazy apply 가 history 보존, race-free)
   - 적용 대상 provider = **Api provider 만** (Claude CLI / Codex CLI 는 별 PR backlog, 미결정 항목 3)

4. **`Apps/Promaker/Promaker/LlmAgent/Prompts/5.knowledge-base.md`** (PR-E 흡수 가능) — 룰 1줄 추가:
   - "전체 본문 필요 시 `attachment_fulltext(fileId)` 호출, search 만으로 부족할 때"

#### 자가 검열 trigger
- ② 신규 type / class 3+ (KbDigestBuilder + ApiChatProvider 확장 + ViewModel 메서드)
- ③ 3+ 파일 동시 변경
- ⑤ public API/SSOT (SetPendingSystemPrompt + KbDigestBuilder)
- → sub-agent 위임 의무

#### 신규 fact (Promaker.Tests) — 예상 +7
- `KbDigestBuilder.Build` (3 fact: 빈 리스트 / 단일 collection / 다중 collection + keyword empty fallback)
- `ApiChatProvider._pendingSystemPrompt` lazy apply (3 fact: pending swap 시점 / in-flight turn 보호 / history 보존)
- Anthropic cache breakpoint 박제 검증 (1 fact, mock provider 의 ChatMessage AIContent 박제 확인)

#### E2E 검증 (사용자 수동, PR-F + PR-G 완료 후)

1. Promaker 시작 → chat panel open → first turn 의 system message 에 `# ─── Active Knowledge Bases ───` section 포함 확인
2. KbManagerDialog 에서 collection 토글 → debounce 후 다음 turn 의 system message 갱신 확인
3. Anthropic API 응답의 `cache_read_input_tokens` / `cache_creation_input_tokens` 로 cache hit rate 측정 (v-b 가 base 영역 cache 유지 검증)
4. LLM 이 keyword digest 박제 후 사용자 query 시 `attachment_search` 자발적 호출 빈도 증가 확인 + 깊은 질문 시 `attachment_fulltext` 호출 확인

---

## 6. 변경 포인트 — 잔여 PR 만 (PR-A/B/C/D 는 commit log 참조)

### lib (`Solutions/Core/Ds2.LightHouse/`)
- (PR-B/C 완료 — KeywordExtractor.fs + TextDumper.fs 신설 박제 완료)

### Protocol / server / cli
- (PR-A/B/C/D 완료 — Protocol MetaJson + Registry CollectionEntry + AttachmentTools.attachment_fulltext + Packager.fs writeMeta + Program.fs runUpload hook 모두 박제 완료)

### Promaker (`Apps/Promaker/Promaker/`) — **잔여 (PR-F + PR-G)**
- `Knowledge/LightHouseClient.cs:719-733` `CollectionInfo` 의 두 필드 deserialize — **PR-A 에서 이미 완료** (PR-F 의 fetch path 가 활용)
- `ViewModels/LlmChatViewModel.cs:226` `TryCreateLightHouseSessionsAsync` 확장 + `_acceptedCollectionIds` 보관 + SSE hook (PR-F)
- `Knowledge/LightHouseClientHolder.cs:49` `static event Action<ServerEventDto>? EventReceived` 에 ViewModel subscribe (PR-F)
- `LlmAgent/SystemPrompt.cs:11` + 신규 `KbDigestBuilder` class (PR-G)
- `LlmAgent/Api/ApiChatProvider.cs:62, 167-171, 211` `_pendingSystemPrompt` + `SetPendingSystemPrompt` + firstTurn swap + Anthropic cache (v-b) 박제 (PR-G)
- `LlmAgent/Prompts/5.knowledge-base.md` 룰 1줄 추가 (PR-E 흡수 가능)

### Tests — 잔여
- `Promaker.Tests` — FetchKbProfilesAsync (PR-F) + KbDigestBuilder + ApiChatProvider lazy apply (PR-G)

---

## 7. 주의사항 (불변)

1. **schema bump 없음** — `MetaJsonSchema.Current=1` / `RegistrySchema.Current=1` 유지. optional 필드 추가만 forward-compat. (PR-A 완료, 다음 세션이 무심코 bump 하지 말 것)
2. **IndexerVersion 2.2.0** (PR-C 완료) — range max 변경 없음 (2.99.99 그대로). 다음 phase 진입 시 paired-release ps1 통과 의무.
3. **paired-release ps1 검증** — IndexerVersion bump 시 `Apps/Promaker/scripts/check-paired-release.ps1` 통과 의무. PR-C 이후 변경 없으면 자동 통과.
4. **두 layer 독립성** — keyword digest (PR-A/B/F/G) 와 text dump (PR-A/C/D/E) 는 schema 만 공유. 한쪽 미진입 시 다른쪽 정상 동작.
5. **자가 검열** — 각 PR 별 trigger ① ~ ⑤ 충족 시 sub-agent 위임 (또는 inline self-review) 의무. CLAUDE.md 차단 규칙 — 미수행 상태에서 commit/push/다음 phase/사용자 질의 금지.
6. **commit 정책** — 사용자 명시 `--gc` 또는 budget 박제 시점만. memory `feedback_commit_authorization.md` 정합.
7. **AskUserQuestion 도구 사용 금지** — `done-lighthouse-next-session.md:512` SSOT + CLAUDE.md `## 질문 방식` SSOT. 의사결정 요청 시 일반 텍스트 (번호 매긴 목록) 로 박제.
8. **file 인코딩 UTF-8** (BOM 없음 — TextDumper Encoding.UTF8(false) 정합). commit message HEREDOC 박제.
9. **commit message branch prefix** — `[lighthouse-summary] ...` (CLAUDE.md `--gc` 룰 정합).
10. **PR-G 의 chat-scoped invariant** — KB 변경은 **다음 panel 또는 다음 firstTurn 까지 적용 안 됨**. 현 chat 안 *즉시* 박제 path 가 필요하면 별도 turn injection (예: `[KB-changed notice]` 짧은 text 를 user message prepend) 박제 — Phase 2 검토.

---

## 8. 진행 순서 (다음 세션 진입 시)

### 진입 권장 (우선순위)

1. **본 doc + `done-fix-lighthouse-search-keyword.md` (기존 박제) 동시 정독** — §0 진행 상태 + §3.1 / §3.2 결정 SSOT + §4 미결정 잠정 default + §5.1 / §5.2 PR-F/G 상세 scope
2. **PR-F 진입** (방안 A — PR-F + PR-G 두 commit 분리)
   - Phase 1: `LlmChatViewModel` 의 `FetchKbProfilesAsync` + `_acceptedCollectionIds` + cache
   - Phase 2: SSE hook subscribe + debounce
   - 자가 검열 sub-agent 위임 → commit
3. **PR-G 진입** (PR-E 흡수 권장 — `5.knowledge-base.md` 룰 1줄 추가까지 묶음)
   - Phase 1: `KbDigestBuilder.Build` impl + unit test
   - Phase 2: `ApiChatProvider._pendingSystemPrompt` + `SetPendingSystemPrompt` + firstTurn swap
   - Phase 3: Anthropic cache (v-b) — 두 TextContent 분리 + breakpoint 3/4
   - Phase 4: `LlmChatViewModel.OnKbProfileChanged` impl (PR-F 의 skeleton 위에서)
   - Phase 5: `5.knowledge-base.md` 룰 1줄 추가
   - 자가 검열 sub-agent 위임 → commit
4. **E2E 검증** (사용자 수동, §5.2 E2E 검증 4 항목)
5. **본 worktree merge → `light-house` branch** (사용자 결정 — fast-forward vs squash)
6. **본 worktree 삭제** (`git worktree remove`)

### 방안 분기 (다음 세션이 결정)

- **방안 A** (가장 자연): PR-F 먼저 (Promaker fetch 인프라) → PR-G (system prompt swap). 두 PR 분리 commit
- **방안 B** (통합): PR-F + PR-G 한 commit (lazy apply 가 fetch 와 강결합)
- **방안 C** (PR-E 흡수): PR-G 안에 `5.knowledge-base.md` 룰 1줄 추가까지 묶음 — 권장

---

## 9. 본 worktree 검증 SSOT

빌드 / 테스트 회귀 0 검증:
```bash
cd /f/Git/ds2/light-house-summary
dotnet build Apps/Promaker/Promaker.sln -nologo -v q                  # 오류 0
dotnet test Apps/Promaker/Promaker.sln --no-build -nologo             # 4 project 통과 (Promaker.Tests 제외)
dotnet test Solutions/Tests/Promaker.Tests/Promaker.Tests.csproj --no-build -nologo  # WPF 별도
```

PR-F / PR-G 진입 후 누적 예상:
- Promaker.Tests: 340 → ~353 (+13 PR-F 6 + PR-G 7)
- 다른 project 회귀 0 의무

---

## 10. 관련 파일 / 경로

### 본 worktree 신규 (PR-A~D)
- `Solutions/Core/Ds2.LightHouse/KeywordExtractor.fs` (PR-B)
- `Solutions/Core/Ds2.LightHouse/TextDumper.fs` (PR-C)
- `Solutions/Tests/Ds2.LightHouse.Tests/KeywordExtractorTests.fs` (PR-B)
- `Solutions/Tests/Ds2.LightHouse.Tests/TextDumperTests.fs` (PR-C)

### 본 worktree 수정 (PR-A~D)
- `Solutions/Core/Ds2.LightHouse.Protocol/MetaJson.fs` (PR-A — Description/Keywords)
- `Solutions/Core/Ds2.LightHouse/SqliteStore.fs` (PR-C — IndexerVersion 2.2.0)
- `Solutions/Core/Ds2.LightHouse/Ds2.LightHouse.fsproj` (PR-B/C Compile Include)
- `Solutions/Tools/Ds2.LightHouseService/MetaJson.fs` (PR-A — toRegistryEntry propagate)
- `Solutions/Tools/Ds2.LightHouseService/Registry.fs` (PR-A — CollectionEntry)
- `Solutions/Tools/Ds2.LightHouseService/AttachmentTools.fs` (PR-D — attachment_fulltext)
- `Solutions/Tools/Ds2.LightHouse.Cli/Packager.fs` (PR-B — writeMeta signature)
- `Solutions/Tools/Ds2.LightHouse.Cli/Program.fs` (PR-B/C — runUpload hook)
- `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs` (PR-A — CollectionInfo)
- `Solutions/Tests/Ds2.LightHouseService.Tests/{MetaJson,Registry,MultiTenantPolicy,FileServing,AttachmentTools}Tests.fs` (PR-A/D)
- `Solutions/Tests/Ds2.LightHouse.Tests/SqliteStoreTests.fs` (PR-C — IndexerVersion 2.2.0)
- `Solutions/Tests/Ds2.LightHouseService.IntegrationTests/{ZipBuilders.fs, CliUploadTests.fs}` (PR-A/B caller fix)
- `Solutions/Tests/Promaker.Tests/LightHouseClientTests.cs` (PR-A — description+keywords deserialize)

### 본 worktree 예상 신규 (PR-F/G)
- `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` (또는 partial — FetchKbProfilesAsync + OnKbProfileChanged + SSE hook)
- `Apps/Promaker/Promaker/Knowledge/LightHouseClientHolder.cs` (SSE event handler 분류)
- `Apps/Promaker/Promaker/LlmAgent/SystemPrompt.cs` (KbDigestBuilder)
- `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs` (_pendingSystemPrompt + SetPendingSystemPrompt + Anthropic cache v-b)
- `Apps/Promaker/Promaker/LlmAgent/Prompts/5.knowledge-base.md` (PR-E 흡수 — 룰 1줄)

### 참조 (수정 없음)
- `done-fix-lighthouse-search-keyword.md` (line-level 박제 표 §6 + schema version 정책 §8)
- `done-lighthouse-next-session.md` (parent worktree 의 main backlog — 본 worktree 와 별도 흐름)
- `done-lighthouse-kb-server.md` (server SSOT — IndexerVersion / config / multi-tenant 박제)
- `done-lighthouse-kb-index.md` (parent LightHouse lib SSOT — schema §3.12 / RefLocator §3.13)

---

## 11. PR-H — `summary.md` (doc-level summary, r3 결정)

### 11.1 작업 목표

§1 의 3-layer RAG 를 **4-layer** 로 확장 — doc-level "1줄 요약" layer 추가:

| layer | 단위 | 박제 위치 | 호출 시점 | scale |
|---|---|---|---|---|
| (A) keyword digest *(r2 완료)* | collection | system prompt inline | 자동 (firstTurn) | ~150 tok × N coll |
| (B) text dump *(r2 완료)* | doc (full body) | `attachment_fulltext(fileId)` MCP | LLM 능동 호출 | ~수만 tok / doc |
| (C) chunk excerpt *(parent SSOT)* | chunk | `attachment_search(query)` MCP | LLM 능동 호출 | top-K × ≤4K |
| **(D) doc summary** *(r3 신규)* | **doc (1줄)** | `.lighthouse-kb/summary.md` + (`attachment_summary` MCP P2) | (γ) hybrid | ~50 tok × M doc / coll |

(A) 와 (D) 의 분담 — (A) = *"어느 collection 이 어느 영역"* (영역 routing), (D) = *"어느 file 에 어느 내용"* (file narrowing). 둘 다 박제 시 LLM reasoning 의 2-step 정밀화 — 영역 → file → search/fulltext.

### 11.2 미결정 표 default (사용자 r3 turn 채택)

| # | 항목 | default |
|---|---|---|
| 1 | format | **`summary.md`** (markdown table) 단독. jsonl 은 doc 100+ scale 시 Phase 2 검토 |
| 2 | scope | **per-collection 1 파일** (`<source>/.lighthouse-kb/summary.md`) |
| 3 | summary 1줄 생성 | **방법 5 (subagent batch)** + **방법 3 (KeywordExtractor + Title) zero-cost fallback**. P1 = 방법 3 만, P2 = 방법 5 흡수 |
| 4 | inject path | **(γ) hybrid** — 첫 turn 영역 인지 (A) + on-demand `attachment_summary(collectionId)` MCP (D 상세). P2 진입 시 활성 |
| 5 | 갱신 trigger | `/indexer` skill 의 **Step 2b "summary-fill"** 추가. caption-fill (Step 2) 와 동형 패턴 |
| 6 | server upload | zip 에 `summary.md` 포함 + `meta.json` 의 `SummaryFile` field (optional) |
| 7 | MCP tool 명 | **`attachment_summary(collectionId)`** (P2) |
| 8 | inline threshold | doc ≤ 5 시 (α) inline 자연 흡수, >5 시 (γ) — 분기점 별 PR 검토 (P2 진입 후 결정) |

### 11.3 PR 분할 (P1 → P2 점진)

#### **PR-H1 (P1) — `summary.md` 박제 only (방법 3 zero-cost)**

| commit | scope |
|---|---|
| TBD | **lib `SummaryBuilder.fs`** 신설 + CLI `runIndex` / `runUpload` hook + unit test 신규. format = markdown table, summary 1줄 = (Title 또는 첫 chunk 첫 sentence) + top-5 keyword 결합. 색인 비용 추가 0 |
| TBD | doc / SKILL.md update (본 §11 박제) |

**산출물**: `<source>/.lighthouse-kb/summary.md`
**LLM inject**: 0 — Human 검수 + 차후 P2 의 inject path 기반 자료
**검증**: `/f/tmp/g` 재색인 후 `summary.md` 의 *정보 품질* 확인 — 한 줄로 doc 의 본질이 전달되는지

#### **PR-H2 (P2) — subagent batch (방법 5) + MCP tool + inject (자율 진입)**

PR-H1 e2e 검증 결과가 정보 품질 *system prompt inject 의미 있음* 으로 평가 시에만 자율 진입. 분량 = ~3 commit:

| commit | scope |
|---|---|
| TBD | **CLI Step 2b infra** — `list-pending-summaries` + `summary-update` 2 entry 추가 (caption-fill 의 동형 패턴). lib `SummaryStore` (또는 SqliteStore 의 Summary column) 신설 |
| TBD | **SummaryBuilder 확장** — 방법 5 (subagent batch 결과) 흡수, 방법 3 = 자연 fallback. `/indexer` SKILL.md Step 2b 추가 |
| TBD | **server `attachment_summary` MCP tool** — PR-D 패턴. server Collections/{guid}/.lighthouse-kb/summary.md stream. 1MB cap, audit log 4분기 |

**P2 자율 진입 결정 기준** (PR-H1 e2e 후):
- ✅ **진입**: summary 1줄이 doc 의 영역/주제를 *자연어 한 문장* 으로 전달 → LLM file narrowing 의미 있음
- ❌ **종결**: summary 가 keyword list 의 단순 join 수준 → P1 만으로 종결, P2 의 subagent 비용 정당화 부족
- 사용자 검수 없이 자율 결정 (r3 turn 명시 박제)

### 11.4 PR-H1 (P1) 상세 design

#### lib `SummaryBuilder.fs` API (잠정)

```fsharp
[<RequireQualifiedAccess>]
module SummaryBuilder =
    type DocSummary = {
        DocId: int64
        OriginalPath: string  // 원본 (예: 3.광명2_전동화공장.pdf)
        TextDumpPath: string  // text/1-...md (TextDumper 산출물 - relative)
        Summary: string       // 1줄 요약 (방법 3 zero-cost 또는 방법 5 subagent)
    }
    val build: SqliteConnection -> string (* collectionRoot *) -> DocSummary array
    val write: string (* summaryPath *) -> DocSummary array -> unit
```

#### `summary.md` format (방법 3 zero-cost)

```markdown
# Collection Summary
_생성: 2026-05-22 09:10 | docs: 1 | 총 byte: 146,822_

| 원본 | text dump | 요약 |
|---|---|---|
| 3.광명2_전동화공장_제어시스템(HMI편집됨).pdf | text/1-3.광명2_전동화공장_제어시스템_HMI편집됨_.md | [PLC, RAPIENET, HMI, 제어반, ...] — 광명2 전동화공장(SV) 제어시스템 설명회 |
```

방법 3 의 요약 = `[top-5 keywords] — Title (또는 첫 chunk 의 첫 sentence)`. LLM 호출 0.

#### CLI hook 위치

- `runIndex` (Program.fs:~190) — 기존 `TextDumper.dumpAll` 직후 (단일 read-only connection 안)
- `runUpload` (Program.fs:~280) — 기존 `TextDumper.dumpAll` 직후 + `summary.md` 를 zip 에 포함 (`Packager.createZip` 의 whitelist 갱신)
- 자가 검열 trigger: ② (신규 module + API 1) + ③ (단일 파일 변경 > 100 line 가능성) → sub-agent 위임 예정

### 11.5 PR-H1 신규 / 수정 파일 (예상)

#### 신규
- `Solutions/Core/Ds2.LightHouse/SummaryBuilder.fs` (~80 line, 방법 3)
- `Solutions/Tests/Ds2.LightHouse.Tests/SummaryBuilderTests.fs` (~6 fact)

#### 수정
- `Solutions/Core/Ds2.LightHouse/Ds2.LightHouse.fsproj` (Compile Include 신규)
- `Solutions/Tools/Ds2.LightHouse.Cli/Program.fs` (runIndex + runUpload hook)
- `Solutions/Tools/Ds2.LightHouse.Cli/Packager.fs` (createZip whitelist 에 `summary.md` 추가, P1 에서 결정)

### 11.6 PR-H2 (P2) 예상 추가 (자율 진입 시)

#### 신규
- `Solutions/Tools/Ds2.LightHouseService/AttachmentTools.fs` 에 `attachment_summary` 추가 (PR-D 패턴 — 1MB cap, 4분기 audit log)
- `Solutions/Tests/Ds2.LightHouseService.Tests/AttachmentToolsTests.fs` (~5 fact)
- `Apps/Promaker/Promaker/LlmAgent/SystemPrompt.cs` 의 `KbDigestBuilder` 확장 — `attachment_summary` 호출 안내 1줄 (γ hybrid)
- (선택) lib `SummaryStore.fs` — SQLite 의 신규 column 또는 별 table (방법 5 subagent 결과 보관)

#### 수정
- `Solutions/Tools/Ds2.LightHouse.Cli/Program.fs` — `list-pending-summaries` + `summary-update` entry 2개 추가 (caption path 와 동형)
- `.claude/skills/indexer/SKILL.md` — Step 2b "summary-fill" section 추가 (Step 2 caption-fill 패턴 정합)
- `Apps/Promaker/Promaker/LlmAgent/Prompts/5.knowledge-base.md` — `attachment_summary` 룰 1줄 추가
- IndexerVersion `2.2.0` → `2.3.0` minor bump (paired-release ps1 통과 의무, range max 2.99.99 그대로)

### 11.7 자가 검열 + auto commit budget

- 사용자 r3 turn 명시: **auto commit 5회 허용** — PR-H1 (2 commit: lib + doc) + PR-H2 (3 commit: infra + builder + MCP)
- 자가 검열 trigger ② / ③ / ⑤ 충족 시 sub-agent (general-purpose) 위임 의무
- 회귀 test 검증 — Ds2.LightHouse.Tests 회귀 0 + 신규 fact +6 (P1) / +5 (P2 server 측)
