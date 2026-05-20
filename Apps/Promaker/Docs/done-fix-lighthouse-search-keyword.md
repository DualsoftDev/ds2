# todo: LightHouse KB 검색 keyword 자동 박제 (Phase 1 — system prompt digest)

> 이전 세션 산출 + 5-reviewer 메타리뷰 (Critical 6 / Major 14 / Outlier 5 / Minor 9) 반영본.
> 이전 판본의 잘못된 식별자 / 파일 위치 / 정책 결정은 본문에 풀어서 정정 (라벨 나열 금지).

## 1. 목적

LLM 이 chat 시작 / KB set 변경 시점에 *active 인 collection 들의 topic + keyword profile* 을 인지하도록 system prompt 에 박제하여 `attachment_search` MCP tool 의 trigger 정확도를 향상한다.

### 현재 상태 (변경 전)
- `Promaker/LlmAgent/SystemPrompt.cs:11` 의 `SystemPromptText.Phase1c` 가 유일 멤버. 본문은 `PromptLoader.LoadComposed()` 가 baseline(embedded `*.md`) + operator(`AppContext.BaseDirectory/Prompts/`) + user(`%APPDATA%/Promaker/Prompts/`) 3-tier 로 합성. KB 관련 prefix 없음.
- LLM 은 MCP tool schema (`attachment_list / attachment_outline / attachment_search / attachment_read`) 만 보고 능동 탐색 수행.
- 사용자 query 의 어휘가 KB chunk text 의 token 과 매칭되지 않으면 LLM 이 검색을 trigger 하지 않음.
- meta.json / Registry / `GET /collections` 응답 어디에도 collection-level 의미 정보 (description/keywords) 가 없음.

### MCP tool 명 확정
실제 노출 명: `attachment_list`, `attachment_outline`, `attachment_search`, `attachment_read` (`Solutions/Tools/Ds2.LightHouseService/AttachmentTools.fs`). digest 박제 시 본 이름 그대로 인용 — silent fail 회피.

## 2. 결정된 설계 SSOT

| 결정 항목 | 결론 | 사유 |
|---|---|---|
| 정보 수준 | collection-level **"topic + keywords" profile** 만. file path / id / 문서 목록은 박제 안 함 | LLM 은 검색 도구가 있고 본문 인출은 호출로 충분. trigger 판단에 필요한 *영역 정보* 만 박제 |
| 정보 출처 | CLI **색인 시점 자동 추출** | (수동 입력 옵션 폐기) 사용자 입력 keyword 가 본문 token 에 없으면 BM25 0 hit. 자동 추출은 *본문 token 셋 안에서 선택* 하므로 매칭 정합 보장. 단 LLM 의 query echo 자체에 의존하는 부분은 자동/수동 공통 (어휘 매칭이 약하면 그래도 miss 가능) — 자동 추출은 매칭 가능성 *상한* 을 본문 어휘로 박는 효과 |
| Phase 1 알고리즘 | (b1) **Stats 기반** — 빈도 + stop-word + 길이≥2 + 알파/숫자/한글 필터 | 외부 LLM 의존 0, 결정적. 잠정 unigram 만 (한글 trigram noise 수용) |
| Phase 2 (보류) | (b2) LLM-driven 또는 (b1+b2) 하이브리드 | 외부 LLM endpoint 정책 결정 후. captionGen 과 동일 endpoint 재활용 가능 |
| 저장 위치 | collection 의 `meta.json` 에 `description: string`, `keywords: string[]` 두 필드 *optional* 추가. Registry 와 `GET /collections` 응답에 전파 | meta.json schema 의 의미 추가만 — 자세한 schema bump 정책은 §8 |
| **KB 변경 처리** | **다음 turn lazy apply** — `ApiChatProvider` 에 `_pendingSystemPrompt` field + 다음 turn 의 system message 합성 직전 swap | (이전 판본의 "Provider 재생성" 정책 **폐기**). 이유: `ApiChatProvider.ClearSession()` 이 `_history.Clear()` 라 streaming 중 KB toggle 1회로 전체 history 폐기 + chat-scoped invariant (`LlmChatViewModel.cs:931` 주석 "active 토글은 다음 chat panel 부터 반영") 깨짐. lazy 는 in-flight turn 보호 + history 보존 + race-free |
| Fetch 경로 | `LightHouseClient.ListCollectionsAsync` (REST `GET /collections`) 만. MCP `attachment_list` 경유 불필요 | 문서 목록 박제 안 함 — title / description / keywords 만 필요 |
| SSE hook 위치 | **LlmChatViewModel** 가 `LightHouseClientHolder.EventReceived` 에 `+=` (정적 event SSOT). KbManagerDialog 의 기존 subscription 은 보조 (dialog UI refresh 용) | chat 사용 중 KbManagerDialog 는 닫혀있는 게 일반적 — dialog 의 hook 만으로는 SSE 미수신. `LightHouseClientHolder.cs:49` 의 정적 event 가 SSOT |

## 3. 미결정 (이어받는 세션에서 확정 필요)

### Phase 1 알고리즘 직접 결정 (즉시 필요)
- **(i)** Phase 1 단독 vs 처음부터 b1+b2 하이브리드. 잠정 **Phase 1 단독**. b2 의 LLM endpoint 의존이 offline 정책과 충돌 가능.
- **(ii)** 추출량 top-N. 잠정 **15** keyword/collection. *Collection 수 N 개일 때 총 토큰 = N × 15 × ~3 token = N × 45*. 활성 collection 10개면 ~450 토큰 — 무난. 활성이 100개 등 다수면 collection 수 적응형 (예: `max(5, 30/N)`) 검토 필요.
- **(iii)** N-gram 범위. 잠정 **unigram + 길이≥2 chars**.
- **(iv)** stop-word 정책. 잠정: **영문 NLTK 표준 list** + 한글은 단순 길이/문자 필터만 (한글 단어 경계 처리 어려움, trigram tokenizer 환경 noise 수용). list SSOT 출처 명시 박제 필요.

### KB 변경 / cache / fetch 정책 결정
- **(v)** Anthropic prompt cache 박제 — 현재 `ApiChatProvider.cs:169` system + `:211` snapshot 두 곳에 `cache_control: ephemeral` 박제 (2/4 cap 사용). 옵션:
    - **(v-a)** digest 를 base prompt 와 동일 `system` TextContent 안에 concat — breakpoint 추가 없음. 단 KB 변경 시 base 영역까지 cache miss.
    - **(v-b)** system 을 `BaseSystem TextContent` + `KbDigest TextContent` 두 블록 으로 분리, 각각 `cache_control` 박제. KB 변경 시 base 영역 cache hit 유지. breakpoint 3/4 사용. 권장.
- **(vi)** 빈 description fallback. Phase 1 에서는 description 항상 빈 값 (b1 stats 만으로는 topic 한 줄 합성 어려움) — 잠정 **title 그대로** 가 *default path* (Phase 1 한정으로 결정 사항이 아닌 강제 동작). Phase 2 의 b2 도입 시 LLM 이 topic 1줄 합성.
- **(vii) ** `GET /collections` polling/cache 정책. SSE event burst (`caption-progress` 등) 마다 호출하면 부하 발생. 잠정: **service 단위 in-memory cache + SSE `collection-{added,updated,deleted}` 만 invalidate**. `caption-progress` 류 progress event 는 cache invalidate 대상 아님.
- **(viii)** provider swap **debounce window** — KB chip 다중 toggle 시 burst 방지. 잠정 **500~1000ms**.
- **(ix)** digest 박제 대상 provider 셋. 잠정 **Api provider 만** (Claude CLI / Codex CLI 는 본 phase 미적용 — system prompt 주입 path 가 다름).
- **(x)** 기존 collection (description/keywords 빈 값) 처리. 잠정: **silent fallback (title 만 박제) + 사용자 명시 재upload 시 갱신**. server-side migration 미수행 (원본 데이터 없음). 운영상 admin re-index 안내 path 별도.

## 4. 변경 포인트 (파일 단위)

### 사전 작업 (선행 PR 권장)
- **wire schema 일원화** — `Packager.MetaDto` (CLI, `Packager.fs:30-45`) / `MetaJson` (server, `MetaJson.fs:15-32`) / `CollectionInfo` (Promaker, `LightHouseClient.cs:426`) 3-way 중복. 본 phase 가 두 필드를 세 곳에 박제하면 drift 영구화 + KbProfile 신설로 4중복.
- 옵션 A: `Solutions/Core/Ds2.LightHouse/KbSchema.fs` 신설 → 세 record 공통 source. F# record 를 C# 측에서도 deserialize 가능하도록 `[<CLIMutable>]` + camelCase `JsonPropertyName`.
- 옵션 B (최소): 신설 없이 각 record 에 두 필드 박제하되 **wire contract test** 추가 (`Solutions/Tests/Promaker.Tests/...` 와 `Ds2.LightHouseService.Tests` 에서 양쪽 `JsonPropertyName` 셋 일치 assert).
- 진행: 옵션 B 가 본 phase scope. 옵션 A 는 별도 phase.

### 4.1 CLI / Indexer 측 (lib)

- **`Solutions/Core/Ds2.LightHouse/KeywordExtractor.fs` (신규, ~80 line)**
    - 위치는 **lib (Core)** — Chunker / Searcher 와 결이 같고, server-side 추출 (`POST /collections` 가 zip 풀고 fallback 추출) 시에도 재사용.
    - 입력: `chunkText: string array` 또는 `chunksTable: SqliteConnection`
    - 출력 record:
      ```fsharp
      type KeywordExtractionResult = {
          Topic: string option   // Phase 1 = None (b2 이후 채움)
          Keywords: string array
      }
      ```
    - 알고리즘 (Phase 1 = b1):
        1. chunk text 순회 → token 분리 (whitespace + 구두점)
        2. 영문 stop-word 제거 (출처 SSOT 명시 — NLTK English stop-word)
        3. 길이 ≥ 2 chars, 알파/숫자/한글 (`Char.IsLetterOrDigit`) 만 유지
        4. 빈도 dict 누적 — **streaming SELECT + dict cap (예: 50K)** 으로 대형 KB (5000 chunks) 시 메모리 8~12MB 안에 박제
        5. 빈도 top-N (잠정 15)
        6. **self-MATCH 검증** (필수) — 추출된 각 keyword 가 자기 collection 의 `ChunksFts MATCH 'keyword'` 에 ≥ 1 hit 되는지 assert. 0 hit keyword 는 결과에서 drop (precision floor — `Solutions/Core/Ds2.LightHouse/SqliteStore.fs:155-158` FTS trigger 정합)

- **`Solutions/Tools/Ds2.LightHouse.Cli/Packager.fs:30-45`**
    - `MetaDto` record 에 `description: string`, `keywords: string array` 두 필드 추가 (기본값 `""`, `[||]`)
    - `writeMeta` 직전 단계에서 KeywordExtractor 호출 → 결과를 MetaDto 에 박제 → `JsonSerializer.Serialize` 시 두 필드 출력

- **`Solutions/Tools/Ds2.LightHouse.Cli/Program.fs:100-163`**
    - `runUpload` 의 `Packager.writeMeta` 직전 hook 추가:
      ```fsharp
      let kwResult = KeywordExtractor.extract (stagingDir + "/.lighthouse-kb/index.db")
      Packager.writeMeta stagingDir title folder fileCount totalBytes userIdentity kwResult
      ```
    - CLI 인자 추가 없음 (완전 자동)

### 4.2 Server 측

- **`Solutions/Tools/Ds2.LightHouseService/MetaJson.fs:15-32`** (CR-2 정정 — 이전 판본의 ZipImport.fs 지정은 잘못)
    - `MetaJson` record 에 `Description: string`, `Keywords: string array` 두 필드 추가 + `[<JsonPropertyName>]` 박제
    - `MetaJsonSchema.Current` 는 **bump 하지 않음** (§8) — schema version 동일 유지 (optional field 추가 정책).
    - `MetaJson.load` 의 strict `<>` 검사 그대로 (line 67). 누락 field 는 STJ default 처리 (`""`, `[||]`).
    - `MetaJson.toRegistryEntry` (line 101) 가 두 필드를 Registry entry 로 전파.

- **`Solutions/Tools/Ds2.LightHouseService/Registry.fs`**
    - `CollectionEntry` record (또는 동등 type) 에 두 필드 추가
    - `RegistrySchema.Current` 도 **bump 하지 않음** (§8). registry.json 의 strict `<>` 검사 (line 85) 보존, 누락 field default 처리.
    - 기존 registry.json 의 빈 값 자연 처리.

- **`Solutions/Tools/Ds2.LightHouseService/CollectionEndpoints.fs:160` (`GET /collections`)**
    - 응답 JSON 의 collection entry 에 두 필드 expose
    - `POST /collections` (line 65~) 는 이미 meta.json 을 `MetaJson.load` 로 읽고 Registry 에 박제하는 path 이므로 별도 변경 없음 (MetaJson record 확장만으로 자동 전파).

### 4.3 Promaker 측

- **`Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs:190 `ListCollectionsAsync` + `:426 CollectionInfo`** (메서드명 정정 — `GetCollectionsAsync` 는 오기)
    - `CollectionInfo` 에 두 필드 추가 + `[JsonPropertyName]` 박제
    - `ListCollectionsAsync` 시그니처는 그대로 (응답 schema 자동 확장)
    - `IReadOnlyList<string> Keywords` 는 STJ 가 직접 deserialize 못하므로 `List<string>` 또는 `string[]` 으로 박제 후 caller 가 readonly view.

- **`Apps/Promaker/Promaker/LlmAgent/SystemPrompt.cs:9-12`**
    - `Phase1c` 본문 변경 없음 (PromptLoader 가 baseline + operator + user SSOT)
    - 신규: `KbDigest` static helper class 또는 namespace 추가:
      ```csharp
      public static class KbDigestBuilder
      {
          public static string Build(IReadOnlyList<CollectionInfo> kbs);
      }
      ```
    - 빈 리스트 시 빈 문자열 반환 (digest 섹션 자체 생략 → `ApiChatProvider` 가 system 박제 시 자연 skip).
    - 산출물 예시:
      ```
      # ─── Active Knowledge Bases ───

      다음 영역에 해당하는 질문이면 `attachment_search(query)` MCP tool 을 호출하세요.

      - "Poc"
          keywords: cache_rd, cache_cr, token, turn, cache, hit, steady
      - "Promaker Docs"
          keywords: prompt, cache, MCP, ApiChatProvider, ...
      ```

- **`Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs`**
    - `TryCreateLightHouseSessionsAsync` (line 226) 가 `acceptedCollectionIds` 를 `_lightHouseSessions` 외 별도 보관 (현재는 token 만 저장 — line 267 인근).
      ```csharp
      private readonly Dictionary<string, IReadOnlyList<string>> _acceptedCollectionIds = new();
      ```
    - 신규 `FetchKbProfilesAsync` — 각 active service 마다 `ListCollectionsAsync` 호출 + `_acceptedCollectionIds[serviceId]` 로 필터.
      - service 별 try/catch (한 service 실패가 chat 자체 차단 X)
      - in-memory cache (key = serviceId) — SSE event 가 invalidate.
    - **SSE hook** — `LightHouseClientHolder.EventReceived` 에 정적 event subscribe (chat panel lifetime 동안). `KbManagerDialog` 의 hook (line 60/520) 과 별도.
      - event 분류 — `collection-added/updated/deleted` 는 `_acceptedCollectionIds[ownerServiceId]` 와 교차 후 invalidate + lazy apply trigger. `caption-progress` 류는 무시.
    - **provider swap 폐기** — `OnSelectedProviderChanged` 와 `ConfigureProviderAsync` 호출 경로 사용 *안 함*. 대신:
      ```csharp
      // 신규 메서드
      private void OnKbProfileChanged(...)
      {
          var newProfiles = await FetchKbProfilesAsync(...);
          var newSystemPrompt = SystemPromptText.Phase1c + KbDigestBuilder.Build(newProfiles);
          if (_provider is ApiChatProvider api) api.SetPendingSystemPrompt(newSystemPrompt);
      }
      ```
    - debounce window (잠정 500~1000ms) — `Microsoft.Reactive` 또는 `Task.Delay` + token 기반.

- **`Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs:62-127, 167-171`**
    - `_systemPrompt` 는 `readonly` 해제 (`string` 으로 mutable 화) 또는 `_pendingSystemPrompt` 별도 field 신설 — 후자 권장.
    - 신규 `SetPendingSystemPrompt(string s)` 메서드 — thread-safe interlocked write.
    - `SendImpl` (line 148-) 의 첫 turn 분기 (line 155-180) 진입 시 `_pendingSystemPrompt` snapshot 후 `_systemPrompt` 로 적용.
    - 이미 history 가 시작된 후 (`firstTurn==false`) 의 적용 정책 — system message 는 history[0] 에 박제되어 있어 swap 시 *다음 firstTurn 까지 적용 안 됨*. 즉 본 phase 의 lazy apply 는 "다음 panel 시작 또는 다음 firstTurn" 단위로 박힘 (chat-scoped invariant 정합). 만약 *현 chat 안에서도 KB 변경이 LLM 인지로 즉시 박혀야 한다* 면 별도 turn injection (예: `[KB-changed notice]` 짧은 text 를 user message prepend) path 가 추가로 필요 — Phase 1 scope 외, Phase 2 검토.
    - **prompt cache 옵션 (v-b) 적용 시**:
      ```csharp
      // line 169 인근 — 두 TextContent 로 분리
      AIContent baseContent = new TextContent(_basePrompt);
      AIContent digestContent = new TextContent(_kbDigest);
      if (_applyCacheControl != null) {
          baseContent = _applyCacheControl(baseContent);
          digestContent = _applyCacheControl(digestContent);
      }
      _history.Add(new ChatMessage(ChatRole.System,
          new List<AIContent> { baseContent, digestContent }));
      ```
      breakpoint 사용량 3/4 (base + digest + snapshot). 여유 1.

### 4.4 테스트

- **`Solutions/Tests/Ds2.LightHouse.Tests/KeywordExtractorTests.fs` (신규)**
    - 영문 / 한글 / 혼합 chunk 입력 → top-N 기대
    - stop-word 제거 검증
    - **self-MATCH precision floor** — 추출 keyword 가 동일 DB 의 `ChunksFts MATCH` hit 보장
- **`Solutions/Tests/Promaker.Tests/...` (modified)**
    - `KbDigestBuilder.Build` 단위 테스트 — 빈 리스트 / 단일 collection / 다중 collection
    - `LightHouseClientTests` 의 fixture 확장 — 두 필드 deserialize round-trip
    - wire contract test (선행 PR 옵션 B) — `CollectionInfo` ↔ `MetaJson` ↔ `MetaDto` 의 `JsonPropertyName` 셋 동등성 assert
- **`Solutions/Tests/Ds2.LightHouseService.Tests/...`**
    - `MetaJson` 의 두 optional field round-trip (누락 시 default, 있을 때 보존)
    - `Registry` upsert / load 의 두 필드 보존

## 5. 권장 작업 순서 (PR 분리)

본 phase 는 CLI / Server / Promaker 3 영역 동시 변경 + 10 파일 — 단일 PR 시 review 어려움. 다음 PR 단위 권장:

1. **PR-A: Schema 확장 (CLI + Server)**
    - `Packager.MetaDto`, `MetaJson`, `Registry.CollectionEntry`, `GET /collections` 응답에 두 optional 필드 추가
    - `MetaJsonSchema.Current` / `RegistrySchema.Current` *bump 없음* — §8 검증 통과
    - Tests: meta round-trip, registry round-trip, GET 응답 schema
    - 본 PR 만 머지해도 기존 client 정상 동작 (필드 비어있음)

2. **PR-B: KeywordExtractor (lib)**
    - `Solutions/Core/Ds2.LightHouse/KeywordExtractor.fs` 신설
    - `Solutions/Tools/Ds2.LightHouse.Cli/Program.fs` `runUpload` 의 hook + `Packager.writeMeta` 두 필드 박제
    - Tests: Keyword precision floor (self-MATCH) + 영문/한글 fixture
    - E2E: 새 zip 의 meta.json 에 keywords 박제 확인

3. **PR-C: Promaker Client fetch**
    - `LightHouseClient.CollectionInfo` 두 필드 deserialize
    - `LlmChatViewModel.FetchKbProfilesAsync` + `_acceptedCollectionIds` 보관 + service-별 try/catch
    - Tests: client fixture 확장
    - 본 PR 만 머지해도 chat 동작 변화 없음 (digest 미박제)

4. **PR-D: SystemPrompt digest + lazy apply**
    - `KbDigestBuilder.Build`
    - `ApiChatProvider._pendingSystemPrompt` + `SetPendingSystemPrompt` + `SendImpl` swap path
    - `LlmChatViewModel.OnKbProfileChanged` + SSE hook (`LightHouseClientHolder.EventReceived`) + debounce
    - prompt cache 옵션 (v-b) 적용 또는 (v-a) 단순 concat
    - E2E: 색인 → upload → chat 시작 → first turn 의 system message 에 digest 포함 확인 + KB chip toggle → 다음 firstTurn 에 갱신 + cache hit rate 측정

5. **(선택) PR-E: 적응형 top-N + Phase 2 b2 도입**
    - Phase 2 결정 후 진행.

## 6. 관련 코드 위치 (검증 완료 — 진입 시 그대로 사용)

| 파일 | line | 역할 |
|---|---|---|
| `Apps/Promaker/Promaker/LlmAgent/SystemPrompt.cs` | 11 | `Phase1c = PromptLoader.LoadComposed()` |
| `Apps/Promaker/Promaker/LlmAgent/PromptLoader.cs` | 13-44 | baseline + operator + user 3-tier 합성 |
| `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` | 201 | `lhEntries = await TryCreateLightHouseSessionsAsync()` |
| `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` | 226 | session 발급 메서드 본체 |
| `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` | 267 | `_lightHouseSessions[serviceId] = token` (collectionIds 별도 보관 필요) |
| `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` | 345-405 | `ConfigureProviderAsync` (`_switchCounter` race guard) |
| `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` | 353-354 | `_cts.Cancel()` + `_provider?.ClearSession()` (provider 재생성 = history 폐기 증거) |
| `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` | 931-937 | `ReloadKbConfig` — chat-scoped invariant 박제 |
| `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` | 998-1005 | `OnSelectedProviderChanged` (본 phase 에서 직접 호출 *안 함*) |
| `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs` | 62, 107 | `_systemPrompt` field |
| `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs` | 122-127 | `ClearSession()` — `_history.Clear()` 호출 (provider 재생성 path 폐기 사유) |
| `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs` | 167-171 | system message 박제 + `cache_control: ephemeral` (1/4 사용) |
| `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs` | 211 | snapshot block 끝 `cache_control` (2/4 사용) |
| `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs` | 190 | `ListCollectionsAsync` (메서드명 SSOT — `Get*` 아님) |
| `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs` | 426-441 | `CollectionInfo` record |
| `Apps/Promaker/Promaker/Knowledge/LightHouseClientHolder.cs` | 49 | `static event Action<ServerEventDto>? EventReceived` (SSOT) |
| `Apps/Promaker/Promaker/Knowledge/LightHouseClientHolder.cs` | 271-275 | SSE event fan-out (handler 결함 swallow) |
| `Apps/Promaker/Promaker/Dialogs/KbManagerDialog.xaml.cs` | 60, 520 | `+= / -= OnSseEventReceived` (dialog open 동안만 — 본 phase 와 별개) |
| `Solutions/Tools/Ds2.LightHouseService/MetaJson.fs` | 15-32 | `MetaJson` record (deserialize SSOT) |
| `Solutions/Tools/Ds2.LightHouseService/MetaJson.fs` | 36-38 | `MetaJsonSchema.Current = 1` |
| `Solutions/Tools/Ds2.LightHouseService/MetaJson.fs` | 59-71 | `MetaJson.load` (strict schema check) |
| `Solutions/Tools/Ds2.LightHouseService/MetaJson.fs` | 101-113 | `toRegistryEntry` 전파 |
| `Solutions/Tools/Ds2.LightHouseService/Registry.fs` | 42-44 | `RegistrySchema.Current = 1` |
| `Solutions/Tools/Ds2.LightHouseService/Registry.fs` | 85-88 | strict schema check |
| `Solutions/Tools/Ds2.LightHouseService/CollectionEndpoints.fs` | 160 | `GET /collections` handler |
| `Solutions/Tools/Ds2.LightHouse.Cli/Packager.fs` | 30-45 | `MetaDto` record (CLI wire) |
| `Solutions/Tools/Ds2.LightHouse.Cli/Program.fs` | 100-163 | `runUpload` flow |
| `Solutions/Core/Ds2.LightHouse/SqliteStore.fs` | 14-25 | `IndexerVersion.Current = "1.3.0"` (색인 결과물 trigger — 본 phase 와 무관) |
| `Solutions/Core/Ds2.LightHouse/SqliteStore.fs` | 155-158 | `ChunksFts` FTS5 trigger (KeywordExtractor self-MATCH 정합 SSOT) |
| `Solutions/Tools/Ds2.LightHouseService/AttachmentTools.fs` | 145-251 | MCP tool 4종 SSOT (`attachment_list/outline/search/read`) |

## 7. 검증 참고 데이터

이번 turn 의 색인 sample (Poc 폴더 → zip, 이전 inspect 결과):
- `meta.json`: `fileCount=4`, `totalSourceBytes=32869` (source 폴더 4 파일)
- DB `Documents=1` (이중 `.md` 1개만 색인 — `.fsx` 3개는 `Classifier.supportedExtensions` 미포함으로 `Unsupported` skip)
- DB `Chunks=7`, `ChunksFts=7`, `OutlineNodes=0`, `ImageCache=0`
- BM25 `MATCH 'cache'` → 5 hits 정상 회수
- chunk text 빈도 분석 시 자연 추출 후보: `cache_rd`, `cache_cr`, `token`, `turn`, `cache`, `hit`, `steady`, `prompt`
- 이 keyword 셋으로 `ChunksFts MATCH '<kw>'` 자가 호출 시 ≥ 1 hit 기대 — KeywordExtractor 의 self-MATCH precision floor 단위 테스트 fixture 로 적합

테스트용 zip (이전 캡쳐): `Apps/Promaker/Docs/Poc/lh-cli-5f2eb2d6066142df83fb2fa4c85b7134.zip`
- 풀어본 inspect 폴더: `Apps/Promaker/Docs/Poc/.tmp-kb-inspect/` (검토 후 삭제 가능)
- 이 zip 의 `meta.json` 에는 두 필드 부재 (1.3.0 색인) — PR-A 머지 후에도 silent fallback (빈 값) 검증 input.

## 8. Schema version 정책 (세 layer 분리)

| layer | SSOT | 의미 | 본 phase 의 bump 여부 |
|---|---|---|---|
| `MetaJsonSchema.Current` | `MetaJson.fs:36-38` (현재 `1`) | wire schema (zip 안 meta.json) | **bump 없음** — optional field 추가만. bump 시 기존 zip 의 `MetaJson.load` 가 InvalidDataException (line 67 strict `<>`) → 400 reject |
| `RegistrySchema.Current` | `Registry.fs:42-44` (현재 `1`) | server-side registry.json | **bump 없음** — 동일 사유. bump 시 운영 머신의 registry.json 전체 reject |
| `IndexerVersion.Current` | `SqliteStore.fs:14-25` (현재 `"1.3.0"`) | 색인 결과물 (`.lighthouse-kb/index.db`) | **bump 없음** — 본 phase 는 chunk/FTS/outline schema 미변경. meta-only |

→ 세 version 모두 bump 없이 진행. 만약 향후 어떤 layer 의 schema 의미 변경 (예: keyword 추출 알고리즘 변경) 시 IndexerVersion patch (1.3.1) 만 bump 검토.

## 9. 주의사항

- **meta.json schema 호환성** — `description` / `keywords` 모두 *optional*. STJ deserialize 시 누락 필드는 default (`""`, `[||]`) 처리되어야 함. `MetaJson.fs` 의 strict `<>` 검사는 `schemaVersion` 만 — field 추가는 자동 forward-compat.
- **BM25 trigram + 한글** — trigram 단위 색인이라 한글 단어 경계 추출 어려움. Phase 1 은 단순 빈도 + 길이 ≥ 2 + 알파/숫자/한글 필터만, noise 수용. self-MATCH precision floor 가 noise 부분 자동 drop (key 가 본 collection 에 매칭 안 되면 결과 제외).
- **provider swap race** — `_pendingSystemPrompt` 의 적용은 `SendImpl` 의 `firstTurn==true` 분기에서만 → in-flight turn 보호 자동.
- **prompt cache breakpoint** — 옵션 (v-b) 적용 시 3/4 사용. 만약 다른 부분 (예: tool definition) 에서도 cache 박제 도입되면 cap 재검토.
- **SSE event 분류** — `LightHouseClientHolder.EventReceived` 는 정적 event 라 *모든* server event (collection-* + caption-progress) 가 fire. handler 측에서 type 필터 필수 — 무필터 시 caption-progress burst 마다 digest refresh + cache miss 폭증.
- **debounce 미설치 시 부하** — KB chip 다중 toggle / SSE event burst 시 `ListCollectionsAsync` 폭주. §3-(viii) 의 debounce 와 §3-(vii) 의 in-memory cache 가 paired guard.
- **자가 검열** — 본 phase 의 코드 작업은 3 영역 동시 변경 + 100 line 이상 → CLAUDE.md 의 자가 검열 trigger ③ ⑤ 해당. 각 PR 마무리 시 sub-agent 위임 review 필수.

## 10. 이어받을 때 첫 단계

1. §3 의 (i)~(iv) 4개 즉시 확정 — Phase 1 알고리즘 박제 시 즉시 필요. 잠정값 그대로 진행해도 OK.
2. §3 의 (v) (prompt cache 옵션 v-a vs v-b) 확정 — PR-D 진입 시 필요. 잠정 (v-b) 권장.
3. §4.사전작업 의 옵션 A (Shared schema 신설) vs B (wire contract test) 결정 — 옵션 B 가 본 phase scope. A 는 별도 phase 권장.
4. §5 의 PR-A 부터 순서대로 진입. 각 PR 단위 자가 검열 + 머지.
5. PR-A 머지 후 기존 collection 의 description/keywords 빈 값 처리는 §3-(x) (silent fallback) 정책 정합 확인.
