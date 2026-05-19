# Ds2.LightHouse / Promaker — 다음 세션 이어받기

본 문서는 새 Claude Code 세션 진입 시 빠르게 현재 작업 상태를 파악하고 backlog 처리를 이어가도록 정리된 transfer 박제이다. 모든 SSOT 는 동일 폴더의 두 doc 에 박제됨:

- `todo-lighthouse-kb-server.md` — server-side phase 진행 SSOT (§0 / §7.1 commit chain / §7.4 backlog)
- `todo-lighthouse-kb-index.md` — parent (LightHouse lib 본체) phase 진행 SSOT (§0 / §3.x design)

본 문서는 새 세션 진입 시 *읽는 순서 + 핵심 backlog 분류* 만 박제. 자세한 박제는 위 두 doc 의 해당 단원 참조.

## 1. 작업 목표

Promaker IDE 의 KB (knowledge base) 시스템 — 사용자 폴더 색인 + central Windows Service share + MCP search host. Phase 1 (LightHouse lib 본체) / Phase S1~S6 (Windows Service + Promaker 통합 + Phase 2 image VLM caption + cli upload + paired-release) 종결. **Phase S7 진행 중**:
- ~~D-S7-2a/b/c (SSE server + client subscribe + 정합 묶음)~~ → s6-r27/r28/r32 완료 (D-S7-2 시리즈 종결)
- ~~D-S7-3a/b/c (multi-service routing 전체 — schema + Holder/N session/MCP + UI)~~ → s6-r29/r30/r31 완료 (§3.16)
- **잔여**: D-S7-1 (mTLS) / D-S7-4 (T2/T3 multi-tenant) / D-S7-5 (resumable upload) / Phase 2 후속 (OCR / embedding) / 정합·성능 sweep.

## 2. 현재 commit state (본 transfer 박제 시점)

- **본 turn = s6-r33** (commit 대기) — Phase 4 **P4-A.0** sqlite-vec NuGet 의존 추가 + native binary 배포 path 검증 (사용자 결정 (α) 안전 분할). 결정 박제: backend = **Ollama** + 모델 = **bge-m3** (1024 dim) + chunk-level + Chunks_Vectors virtual table + RRF hybrid + CLI default-on + IndexerVersion major bump (다음 turn). 본 turn = **P4-A 본격 진입 전 native binding 실 배포 path 검증 분리 commit**. `sqlite-vec 0.1.7-alpha.2.1` NuGet 박제 (Solutions + Apps Directory.Packages.props 양쪽), lib `PrivateAssets="all"` + 모든 caller fsproj `ExcludeAssets="contentFiles"` 박제 (Vector.cs C# wrapper compile fail 차단, F# raw LoadExtension path 정합). 모든 caller publish output 의 `runtimes/win-x64/native/vec0.dll` 자동 배포 검증 완료. 7 fsproj 변경 (의존성만, 기능 코드 0). 회귀 0 — lib 154 / service 125 / IT 33 / Promaker 287 = **누적 599 Fact 유지**. **다음 turn = P4-A 본격** (SqliteStore schema 확장 + IEmbeddingProvider interface + Indexer embedder hook + IndexerVersion major bump + lib unit tests).
- **직전 = s6-r32 (`78e2004`)** — Phase S7 **D-S7-2c** SSE 정합 묶음 (ServerEventNames SSOT + exponential backoff + StopSseLoop timeout + caption-progress wire + using var resp docstring). magic string SSOT 양분 (server F# `module ServerEventNames` [<Literal>] 5종 + client C# `static class ServerEventNames` const 5종, K4 통합 전 양분 유지). `SseReconnectBackoff` helper 신설 (exponential 1→2→4→8→16→30 cap + 60s stable reset + log throttle ≤3/5배수, Func<DateTime> clock injection). `LightHouseClientHolder.StartSseLoopLocked` 가 fixed 5s 폐기 후 본 helper 사용. `StopSseLoop` Wait 1s→3s (s6-r30 review Minor-1 흡수). `POST /events/caption-progress` 신설 (body schema + 검증 400 + `ServerEvent.captionProgress` factory + EventBus.Publish + 204). client `LightHouseClient.PublishCaptionProgressAsync` 신설. `OpenEventsStreamAsync` doc-comment `using var resp` 의도 박제 (s6-r28 review m-3 흡수). KbManagerDialog / Tests 의 magic string 모두 SSOT 참조. 12 파일 변경 (production 6 + test 4 + doc 2). Promaker.Tests **+12 Fact** (SseReconnectBackoff 6 + PublishCaptionProgress 6) + IT **+2 Fact** (round-trip + body 400), Promaker 275→**287** / lib 154 / service 125 / IT 31→**33** = **누적 599 Fact**. **D-S7-2 (2a/2b/2c) phase 전체 완료** — SSE 시리즈 종결. 자세한 SSOT = server.md §0 / §7.1 s6-r32 row / §7.4 D-S7-2c marker.
- **직전 = s6-r31 (`1c086b0`)** — Phase S7 D-S7-3c UI multi-service (DataGrid + TabControl + orphan 정리). 11 파일 변경, +~824 line net. Promaker 261→275. 누적 **585 Fact**. ApplicationSettingsDialog 의 LightHouse section 이 단일 BaseUrl/PSK TextBox → DataGrid + PskEditDialog modal. KbManagerDialog 단일 ListView → TabControl + per-tab `_rowsByServiceId` dict. OrphanCleanupButton + 동의 dialog. displayName uniqueness 검증. SSOT helper 2 종 신설 (LightHouseServiceValidator + KbCollectionOrphanHelper). 자세한 SSOT = server.md §3.16.8 / §7.1 row.
- **그 직전 = s6-r30 (`51ded94`)** — Phase S7 D-S7-3b Holder multi-instance + N session + MCP multi-server. Promaker 241→261. 누적 **571 Fact**.

`git log --oneline -20` 으로 s6-r0 ~ s6-r32 전체 commit chain 확인 가능. server.md §7.1 표 가 의미적 SSOT.

## 3. 새 세션 진입 시 읽기 순서

1. **본 doc** (현재 위치 + 핵심 backlog 박제 만)
2. `todo-lighthouse-kb-server.md` §0 — 모드 박제 + D-id 결정 표 (D-S7-1~5 / D-2-1~7 포함)
3. `todo-lighthouse-kb-server.md` §7.1 — commit chain 표, 가장 최근 row (s6-r32 → s6-r0) 부터 역순으로 5~10개 정독
4. `todo-lighthouse-kb-server.md` §3.16 multi-service routing — D-S7-3a/b/c 의 sub-section 3건 (§3.16.1~8). 새 세션에서 multi-service mental model 파악 의무.
5. `todo-lighthouse-kb-server.md` §7.4 — backlog 표 (처리 완료 marker + 잔여 D-S7-1/2c/4/5)
6. `todo-lighthouse-kb-index.md` §0 — parent rev 박제 + 사용자 동의 결정
7. 필요 시 `git log --oneline -30` + `git show <hash>` 로 commit 상세

## 4. 남은 backlog (우선순위 순)

### A. 단독 turn 진입 권장 (큰 phase)

| # | 항목 | 위치 | 진입 마커 |
|---|---|---|---|
| A1 | **Phase S7 — mTLS / multi-tenant / resumable upload** | server.md §7.4 "P4 Phase S7" | D-S7-1~5 사전 박제 완료. ~~D-S7-2a/b/c (SSE 시리즈 전체)~~ → s6-r27/r28/r32 완료. ~~D-S7-3a/b/c (multi-service routing 전체)~~ → s6-r29/r30/r31 완료 (§3.16). 잔여 = **D-S7-1** (mTLS) / **D-S7-4** (T2/T3) / **D-S7-5** (resumable upload). |
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
- **test** = `Solutions/Tests/Ds2.LightHouse.Tests` (lib, 154) / `Solutions/Tests/Ds2.LightHouseService.Tests` (service, 125) / `Solutions/Tests/Ds2.LightHouseService.IntegrationTests` (e2e + cli, 33) / `Solutions/Tests/Promaker.Tests` (Promaker, 287) = **누적 599 Fact**
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

(s6-r32 commit 직전 상태 — Phase S7 D-S7-2c SSE 정합 묶음 본 turn 변경 = production 6 (server 2 + client 4) + test 4 + doc 2 = 12 파일 staged 대상)

```
 M Apps/Promaker/Docs/todo-lighthouse-kb-server.md
 M Apps/Promaker/Docs/todo-lighthouse-next-session.md
 M Apps/Promaker/Promaker/Dialogs/KbManagerDialog.xaml.cs
 M Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs
 M Apps/Promaker/Promaker/Knowledge/LightHouseClientHolder.cs
 M Solutions/Tests/Ds2.LightHouseService.IntegrationTests/EventsSseTests.fs
 M Solutions/Tests/Promaker.Tests/LightHouseClientTests.cs
 M Solutions/Tools/Ds2.LightHouseService/EventBus.fs
 M Solutions/Tools/Ds2.LightHouseService/EventsEndpoint.fs
?? Apps/Promaker/Promaker/Knowledge/ServerEventNames.cs
?? Apps/Promaker/Promaker/Knowledge/SseReconnectBackoff.cs
?? Solutions/Tests/Promaker.Tests/SseReconnectBackoffTests.cs
```
