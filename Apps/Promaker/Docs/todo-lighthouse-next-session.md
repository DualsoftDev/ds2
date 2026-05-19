# Ds2.LightHouse / Promaker — 다음 세션 이어받기

본 문서는 새 Claude Code 세션 진입 시 빠르게 현재 작업 상태를 파악하고 backlog 처리를 이어가도록 정리된 transfer 박제이다. 모든 SSOT 는 동일 폴더의 두 doc 에 박제됨:

- `todo-lighthouse-kb-server.md` — server-side phase 진행 SSOT (§0 / §7.1 commit chain / §7.4 backlog)
- `todo-lighthouse-kb-index.md` — parent (LightHouse lib 본체) phase 진행 SSOT (§0 / §3.x design)

본 문서는 새 세션 진입 시 *읽는 순서 + 핵심 backlog 분류* 만 박제. 자세한 박제는 위 두 doc 의 해당 단원 참조.

## 1. 작업 목표

Promaker IDE 의 KB (knowledge base) 시스템 — 사용자 폴더 색인 + central Windows Service share + MCP search host. Phase 1 (LightHouse lib 본체) / Phase S1~S6 (Windows Service + Promaker 통합 + Phase 2 image VLM caption + cli upload + paired-release) 종결. **Phase S7 진행 중**:
- ~~D-S7-2a/b (SSE server + client subscribe)~~ → s6-r27/r28 완료
- ~~D-S7-3a/b/c (multi-service routing 전체 — schema + Holder/N session/MCP + UI)~~ → s6-r29/r30/r31 완료 (§3.16)
- **잔여**: D-S7-1 (mTLS) / D-S7-2c (SSE 정합 묶음) / D-S7-4 (T2/T3 multi-tenant) / D-S7-5 (resumable upload) / Phase 2 후속 (OCR / embedding) / 정합·성능 sweep.

## 2. 현재 commit state (본 transfer 박제 시점)

- **본 turn = s6-r31** (commit 대기) — Phase S7 **D-S7-3c** UI multi-service (DataGrid + TabControl + orphan 정리). ApplicationSettingsDialog 의 LightHouse section 이 단일 BaseUrl/PSK TextBox → **DataGrid** (DisplayName/BaseUrl/PSK button/Active/Test/Remove + Add Service). PSK 편집 = **PskEditDialog modal** (평문 비노출). KbManagerDialog 단일 ListView → **TabControl** (active service 별 tab) + per-tab `_rowsByServiceId` dict + per-service `RebuildServiceRows` / `ReconcileServiceLlmConfig`. SSE event `evt.ServiceId` 로 source tab refresh. **OrphanCleanupButton** (KbCollectionOrphanHelper SSOT) + 동의 dialog. displayName uniqueness 검증 (LightHouseServiceValidator SSOT, Save 차단). **SSOT helper 2 종 신설** (`LightHouseServiceValidator` + `KbCollectionOrphanHelper`). 사용자 결정 5건 박제 (DataGrid column / TabControl tab / Save uniqueness / 동의 orphan / Settings 무변경). 자가 검열 review Critical 0 / Major 3 / Minor 6 → 즉시 적용 m5 (1-line refactor) + m2 doc 박제, 거부 8건 (Major fragile path / Minor cosmetic). 5 modified + 6 new = 11 파일, +~824 line net. Promaker.Tests **+14 Fact** (Validator 7 + OrphanHelper 7), Promaker 261→**275** / lib 154 / service 125 / IT 31 = **누적 585 Fact**. **D-S7-3 (3a/3b/3c) phase 전체 완료**. 자세한 SSOT = server.md §3.16.8 / §7.1 row.
- **직전 = s6-r30 (`51ded94`)** — Phase S7 D-S7-3b Holder multi-instance + N session + MCP multi-server. 9 파일 변경, +732/-256 line. Promaker 241→261. 누적 **571 Fact**. `LightHouseClientHolder` 전면 재작성 (단일 `_instance` → `ConcurrentDictionary<serviceId, ServiceClientEntry>`, `EnsureCreated` return `IReadOnlyList<LightHouseClient>` breaking + `GetClient(serviceId)` 신설 + `Current` [Obsolete] backward-compat). SSE callback 안에서 `evt.ServiceId` client-side tagging (`[JsonIgnore]`, server 변경 0). `RegisterSession/UnregisterSession/LiveSessions` per-service breaking signature. `LightHouseServerNaming.cs` 신설 (`McpEntryName` + `SanitizeDisplayName` SSOT). `LlmChatViewModel._lightHouseSessions: Dictionary<serviceId, token>` + `TryCreateLightHouseSessionsAsync` 복수 N session 발급 + `KbCollections.GroupBy(ServiceId)` routing + `BuildMcpConfig(IReadOnlyList<>)` 다중 entry. `KbManagerDialog.CurrentClient = EnsureCreated().FirstOrDefault()` 임시 (D-S7-3c TabControl 분리까지). 자가 검열 Major-1/2/3 + Minor-5/7 즉시 적용 (invariant doc + dedup chip + orphan chip + thread-affinity doc + `_` 치환 fact). 거부 5건 (D-S7-2c / D-S7-3c 묶음 backlog + caller 0 검증). 7 파일 변경 (5 modified + 2 new), +474/-240 line. Promaker 241→**261** / lib 154 / service 125 / IT 31 = **누적 571 Fact**. 자세한 SSOT = server.md §3.16.7 / §7.1 row.
- **그 직전 = s6-r29 (`b2492ae`)** — Phase S7 D-S7-3a multi-service routing schema 확장 + migration. 10 파일 변경, +535/-69 line. Promaker 230→241. 누적 **551 Fact**.

`git log --oneline -20` 으로 s6-r0 ~ s6-r31 전체 commit chain 확인 가능. server.md §7.1 표 가 의미적 SSOT.

## 3. 새 세션 진입 시 읽기 순서

1. **본 doc** (현재 위치 + 핵심 backlog 박제 만)
2. `todo-lighthouse-kb-server.md` §0 — 모드 박제 + D-id 결정 표 (D-S7-1~5 / D-2-1~7 포함)
3. `todo-lighthouse-kb-server.md` §7.1 — commit chain 표, 가장 최근 row (s6-r31 → s6-r0) 부터 역순으로 5~10개 정독
4. `todo-lighthouse-kb-server.md` §3.16 multi-service routing — D-S7-3a/b/c 의 sub-section 3건 (§3.16.1~8). 새 세션에서 multi-service mental model 파악 의무.
5. `todo-lighthouse-kb-server.md` §7.4 — backlog 표 (처리 완료 marker + 잔여 D-S7-1/2c/4/5)
6. `todo-lighthouse-kb-index.md` §0 — parent rev 박제 + 사용자 동의 결정
7. 필요 시 `git log --oneline -30` + `git show <hash>` 로 commit 상세

## 4. 남은 backlog (우선순위 순)

### A. 단독 turn 진입 권장 (큰 phase)

| # | 항목 | 위치 | 진입 마커 |
|---|---|---|---|
| A1 | **Phase S7 — mTLS / SSE / multi-service routing** | server.md §7.4 "P4 Phase S7" | D-S7-1~5 사전 박제 완료. ~~D-S7-2a/b~~ → s6-r27/r28 완료. ~~D-S7-3a/b/c (multi-service routing 전체)~~ → **s6-r29/r30/r31 완료** (§3.16). 잔여 = **D-S7-1** (mTLS) / **D-S7-2c** (caption-progress + reconnect exponential backoff + magic string SSOT + s6-r30 review m-1/m-2 묶음) / **D-S7-4** (T2/T3) / **D-S7-5** (resumable upload). |
| A2 | **K4 Protocol SSOT 통합** | server.md §7.7 K4 박제 | `Solutions/Core/Ds2.LightHouse.Protocol` 신규 project — wire 상수 + MetaJson schema SSOT 통합. LightHouseClient C# / F# 이중 구현 → 단일 SSOT 의존. Phase S7 묶음. |
| A3 | **보안 sweep 1턴 (K6 + M9/M10/M11/M12)** | server.md §7.7 K6 + Major outlier | registry.json tampering 검증 + PSK in-memory lifetime + %PROGRAMDATA% ACL + DoS guards (topK upper bound / query length). admin 권한 위협 모델 한정 (defense-in-depth). |
| A4 | **Phase 2 후속 (Phase 3 OCR / Phase 4 Embedding)** | parent §3.15 / `todo-lighthouse-kb-index.md` Phase 3 / Phase 4 | Phase 2 eager VLM caption 완료 후 OCR (Tesseract.NET + 한글) 또는 embedding (sqlite-vec) 진입. 별 PR. |

### B. 중간 cost 별 turn

| # | 항목 | 위치 | 비고 |
|---|---|---|---|
| B1 | **M13/M14 perf** | server.md §7.7 M13/M14 | `Indexer.ingestFile` outer transaction + `insertChunks` prepared statement reuse. 색인 hot path 변경, perf 측정 의무. |
| B2 | **comments / footnotes / endnotes Drawing 커버** | server.md §7.4 "s6-r16 backlog" | OoxmlExtractor 강화 — 산업 docx 빈도 낮음, 별 turn. lib Fact 3~5건. |
| B3 | **image-only paragraph 박제 분기 분리 (산업 매뉴얼 docx)** | server.md §7.4 "s6-r16 backlog" | OoxmlExtractor 강화. lib Fact 1~2건. |

### C. 소량 정합 (별 turn 또는 묶음)

| # | 항목 | 위치 | 비고 |
|---|---|---|---|
| C1 | **mn6 fixture 한영 혼재** | server.md §7.4 s6-r26 박제 | s6-r26 turn 검토 결과 "fact name 한국어 + technical term 영어" 정합 패턴 — 구체적 어색 case 미발견. 구체 case 식별 후 재진입. |
| C2 | **Phase S6 P7 facade 통합 검증** | server.md §7.4 "P7" | `LightHouseClient.ExecuteWithSessionRetryAsync<T>` facade 실 caller 등장 시 (MCP relay or proxy) wrapper 통합. 보류 박제. |
| C3 | **자가 검열 미적용 backlog** | server.md §7.4 s6-r25 / s6-r26 박제 | Minor-2 (extractCaptionText ArgumentNullException catch) + m2 (raw SQL DELETE for None test) 모두 거부 박제 — 현행 정합. |

### D. 정책 / 의사결정 박제 (코드 변경 없음, 사용자 confirm 시 진입)

| # | 항목 | 위치 |
|---|---|---|
| D1 | **D-2-3 SSOT 정정 (per-image granularity)** | server.md §7.7 M9 박제 — 응답 일관성 vs partial result trade-off |
| D2 | **`<PrivateAssets>all</PrivateAssets>` 적용 범위 확장** | parent r9 박제 — 현재 3 package 한정 |
| D3 | **본 todo 파일 git mv** | parent r7 보류 박제 — `Apps/Promaker/Docs/` → `Solutions/Core/Ds2.LightHouse/doc/` |

## 5. 관련 파일 / 경로

- **lib 본체** = `Solutions/Core/Ds2.LightHouse/` (F#, 12 module: Models / RefLocator / Classifier / Chunker / IExtractor / Text/Pdf/OoxmlExtractor / SqliteStore / Searcher / Indexer / KnowledgeBase / ImageStore / CaptionGenerator)
- **server** = `Solutions/Tools/Ds2.LightHouseService/` (F# Kestrel HTTPS service — Program.fs configureApp export / Storage / Registry / Sessions / ZipImport / FileServing / AttachmentTools (MCP 4 tool))
- **cli** = `Solutions/Tools/Ds2.LightHouse.Cli/` (F# console — index --upload, LIGHTHOUSE_VLM_API_KEY env var fallback)
- **Promaker 통합** = `Apps/Promaker/Promaker/` 의 `Knowledge/` 폴더 (LightHouseClient.cs / LightHouseClientHolder.cs / AttachmentIngestService.cs / CollectionPackager.cs), `Dialogs/KbManagerDialog.xaml(.cs)` + `Dialogs/ApplicationSettingsDialog.xaml(.cs)` (LightHouse Service + VLM section), `LlmAgent/LlmConfig.cs` (KbCollections + LightHouseService + VisionCostGate + ModifyWithLock)
- **test** = `Solutions/Tests/Ds2.LightHouse.Tests` (lib, 154) / `Solutions/Tests/Ds2.LightHouseService.Tests` (service, 125) / `Solutions/Tests/Ds2.LightHouseService.IntegrationTests` (e2e + cli, 31) / `Solutions/Tests/Promaker.Tests` (Promaker, 275) = **누적 585 Fact**
- **doc** = `Apps/Promaker/Docs/` (본 transfer + 두 SSOT todo + done-* archive + howto)
- **scripts** = `Apps/Promaker/scripts/check-paired-release.ps1` (paired-release drift detector)

## 6. 주의 사항

- **commit 진입 전 자가 검열 강제 절차** — CLAUDE.md `## 코드 생성` 단원의 trigger ① ~ ⑤ 중 하나라도 충족 시 sub-agent (general-purpose) 위임 의무. 미수행 상태에서 commit / push / 다음 phase 진입 / 다음 단계 질의 차단.
- **자동 git commit 금지** — 사용자가 명시 `--gc` 또는 commit 지시 시점만. multi-step plan 의 "go" 동의가 commit 까지 포함 안 됨 (메모리 [feedback_commit_authorization.md] 박제).
- **AskUserQuestion 도구 사용 금지** — 사용자 instruction. 의사결정 요청 시 일반 텍스트 (번호 매긴 목록) 로 본문 박제.
- **paired-release ps1 (s5d-r0)** — lib 의 `IndexerVersion.Current` literal 위치 변경 시 ps1 regex 깨짐 (`SqliteStore.fs:24` 의 module 첫 [<Literal>] 위치 의무). SchemaVersion / Tokenizer 추가 시 Current 뒤에 둘 것.
- **CLAUDE.md SSOT 정합** — `~/.claude/CLAUDE.md` 의 "철학" / "코드 생성" / "예외 처리" / "JSON" / "Database" 박제 정합 의무. try-catch 가급적 자제 (fail-safe 우선).
- **memory MEMORY.md** = `C:\Users\dualk\.claude\projects\F--Git-ds2--bare\memory\` — 사용자 / feedback / project / reference 4 type. 새 세션 자동 로드.

## 7. 본 transfer 박제 시점 git status

(s6-r31 commit 직전 상태 — Phase S7 D-S7-3c UI multi-service 본 turn 변경 5 modified + 6 new = 11 production/test 파일 + transfer/server doc 갱신 2 파일 = 13 파일 staged 대상)

```
 M Apps/Promaker/Docs/todo-lighthouse-kb-server.md
 M Apps/Promaker/Docs/todo-lighthouse-next-session.md
 M Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml
 M Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml.cs
 M Apps/Promaker/Promaker/Dialogs/KbManagerDialog.xaml
 M Apps/Promaker/Promaker/Dialogs/KbManagerDialog.xaml.cs
 M Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs
?? Apps/Promaker/Promaker/Dialogs/PskEditDialog.xaml
?? Apps/Promaker/Promaker/Dialogs/PskEditDialog.xaml.cs
?? Apps/Promaker/Promaker/Knowledge/KbCollectionOrphanHelper.cs
?? Apps/Promaker/Promaker/Knowledge/LightHouseServiceValidator.cs
?? Solutions/Tests/Promaker.Tests/KbCollectionOrphanHelperTests.cs
?? Solutions/Tests/Promaker.Tests/LightHouseServiceValidatorTests.cs
```
