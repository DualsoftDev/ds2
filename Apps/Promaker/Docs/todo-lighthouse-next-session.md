# Ds2.LightHouse / Promaker — 다음 세션 이어받기

본 문서는 새 Claude Code 세션 진입 시 빠르게 현재 작업 상태를 파악하고 backlog 처리를 이어가도록 정리된 transfer 박제이다. 모든 SSOT 는 동일 폴더의 두 doc 에 박제됨:

- `todo-lighthouse-kb-server.md` — server-side phase 진행 SSOT (§0 / §7.1 commit chain / §7.4 backlog)
- `todo-lighthouse-kb-index.md` — parent (LightHouse lib 본체) phase 진행 SSOT (§0 / §3.x design)

본 문서는 새 세션 진입 시 *읽는 순서 + 핵심 backlog 분류* 만 박제. 자세한 박제는 위 두 doc 의 해당 단원 참조.

## 1. 작업 목표

Promaker IDE 의 KB (knowledge base) 시스템 — 사용자 폴더 색인 + central Windows Service share + MCP search host. Phase 1 (LightHouse lib 본체) / Phase S1~S6 (Windows Service + Promaker 통합 + Phase 2 image VLM caption + cli upload + paired-release) 종결. **Phase S7 본격 종결**:
- ~~D-S7-1 (mTLS server-side + Promaker client cert + B5 UI 4 phase)~~ → s6-r53/r61/r64/r68/r69 완료 (B5 phase 1~4 누적)
- ~~D-S7-2a/b/c (SSE server + client subscribe + 정합 묶음)~~ → s6-r27/r28/r32 완료 (D-S7-2 시리즈 종결)
- ~~D-S7-3a/b/c (multi-service routing 전체 — schema + Holder/N session/MCP + UI)~~ → s6-r29/r30/r31 완료 (§3.16)
- ~~D-S7-4 T2/T3 multi-tenant~~ → s6-r66 완료 (MultiTenantPolicy SSOT + config migration chain 제거)
- ~~D-S7-5 resumable upload phase 1~3 (scaffold + production + auto chunked)~~ → s6-r60/r63/r67 완료
- **잔여**: A2 K4 Protocol SSOT 통합 / A4 Phase 3 OCR / D-S7-4 admin endpoint (T2 owner stamp + T3 acl 편집) / D-S7-5 phase 4 swap chunked / Phase 2 후속 (OCR / embedding) / 정합·성능 sweep.

## 2. 현재 commit state (본 transfer 박제 시점 — s6-r66~r70 + doc-r3/r4 종결, 2026-05-20)

**Phase S7 본격 종결 + 15-reviewer Critical 16 + Major 1 일괄 처리** — `Ds2.LightHouse` lib + service + IT + Promaker 통합 완료. 누적 **708 Fact** (lib 177 / service 161 / IT 43 / Promaker 327). 회귀 0. paired-release ps1 통과 박제 (IndexerVersion 2.1.0 ∈ [1.0.0, 2.99.99]).

**s6-r70 누적 (본 turn, 2026-05-20)**:
- **`60a2f6a` s6-r70 15-reviewer Critical 16 + Major 1 일괄 처리** — D-S7-4 multi-tenant 격리가 4 surface (FileServing / SSE / Middleware / UploadsEndpoint) 무력화 결함을 본 turn 일괄 해소.
  - **보안/multi-tenant**: C-1 FileServing.getFile 에 `MultiTenantPolicy.evaluate` filter (Hidden=404 정보 leak 차단, cfg 인자 추가) / C-2 EventsEndpoint SSE per-subscriber tenant filter (`isEventVisibleTo` helper, T2/T3 모드 시 Hidden event skip, T1 회귀 0) / C-3 AuthMiddleware mTLS subject CN ↔ X-User-Identity 강제 (`verifyMtlsIdentity` + cfg 인자 추가, mtls.mode != off 시 mismatch 401) / C-4 UploadsEndpoint UserIdentity cross-check (`isUploadOwnedBy` helper, PATCH/GET/DELETE/finalize 4 endpoint 의 owner mismatch 403)
  - **Upload race/lifecycle**: C-5 SemaphoreSlim.Dispose race 정정 (removeLock 의 Dispose 폐기, lazy GC) / C-6 getStatus acquireLock 추가 (PATCH 진행 중 race 차단) / C-7 finalize indexerVersion gate fail 시 uploadId staging cleanup (디스크 leak 차단) / C-8 patchChunk OCE 분리 catch (client disconnect 시 misleading log / writeError 재발 OCE 차단) / C-9 ApplicationStopping → ApplicationStopped (graceful drain 동안 embedder race 차단)
  - **Promaker UI/메모리/보안**: C-10 chunk LOH ArrayPool.Rent + PatchResumableChunkAsync(buffer, effectiveLength) overload (10GB upload 시 LOH 압박 해소) / C-11 _pskChanges Closed event cleanup (Cancel/X close path 평문 lifetime 단축) / C-12 LhSelectCert_Click X509Certificate2 element dispose (CryptoApi handle 누수 차단) / C-13 LhWorkingCopyDirty read-only 계약 (SaveEmbeddingUiToWorking 호출 polkill, caller 책임 이동) / C-14 ClientCertThumbprint dirty 비교 추가 (silent data loss 차단)
  - **Minor**: C-16 ListCollectionsAsync ResponseHeadersRead drift 정정 (다른 10 메서드 정합) / C-17 cli boolean flag set (`FlagNoEmbedding` / `FlagAllowInvalidCerts`) 분리 (다음 토큰 흡수 차단) / R1-M1 ServerEventNames.UploadProgress 1 line (client SSOT 정합)
  - **반론**: R15-M2 RegistrySchema bump 거부 (Acl nullable optional, 기존 v=1 registry.json forward-compat 정합) / C-15 EndpointHelpers SSOT 추출 K4 Protocol phase 묶음 (helper 추출은 functional 영향 0, K4 진입 시 다시 refactor 의무)
  - **MtlsRoundTripFixture 갱신**: cert subject = `CN=<userIdentity>` 정합 (C-3 박제 후 handshake-level 통과)
- **(doc-r4)** transfer + server.md §7.1 commit chain + §7.4 backlog row 박제.

**누적 본 turn 전체**: 6 commit (s6-r66/r67/r68/r69/r70 + doc-r3/r4) / +1349 line / +32 Fact / 회귀 0.

**이전 turn (참고)**:

**s6-r66~r69 누적 (본 turn, 2026-05-20)**:
- **`c01af8f` s6-r66 D-S7-4 T2/T3 multi-tenant + config migration chain 제거** — `MultiTenantPolicy` module 신설 (T1 Allow 전체 / T2 ImportedBy 일치 + legacy 빈 값 전체 공개 / T3 acl.users 검증 + readOnly 분기, Hidden=404 / ReadOnly=403). `Config` schemaVersion 3→4 bump + `MultiTenantConfigSection` + `MultiTenantMode` literal + `validateMultiTenant`. **migration chain v1→v4 전체 제거** (paired-release `/dist` 0회 = legacy schema 인스턴스 0건, dead code 정합 — 사용자 옵션 #2 선택, scope 확장). `Registry.CollectionAcl` (users/readOnly) + `CollectionEntry.Acl` optional. `SessionEndpoints` + `CollectionEndpoints` filter (cfg 인자 추가). `Program.fs` validateMultiTenant 호출. 신규 **+18 service Fact** (MultiTenantPolicy 14 + ConfigTests 4). 자가 검열 sub-agent 위임 (Critical 0 / Major 0 / Minor 5 backlog).
- **`3ed6c1e` s6-r67 D-S7-5 phase 3 — chunked path 자동 선택** — `LightHouseClient.ResumableUploadThresholdBytes` const SSOT (256 MiB) 신설 — HttpClient single multipart buffer OOM 분기점 + 사내 LAN 재시도 비용 분기점 박제. `AttachmentIngestService.UploadAsync` 가 zip size 검사 → 임계 초과 시 `UploadCollectionResumableAsync` 자동 진입. progress 매핑 byte→KiB cast (int 안전). 신규 **+1 Promaker Fact** (const 정합).
- **`77c8a88` s6-r68 B5 phase 3 — DataGrid cert thumbprint 편집 + CertValidator** — `CertValidator` helper 신설 (Normalize hex sanitize + LocalMachine\My / CurrentUser\My X509Store 탐색 + NotAfter 4 분기 `ValidationResult` enum, ExpiringSoonThresholdDays=30 const SSOT). DataGrid Client Cert column 에 '검증' 버튼 + 직접 편집 TextBox 추가 (paste 가능). `LhSelectCert_Click` 가 선택 직후 자동 validity 진단. 신규 **+10 Promaker Fact** (Normalize 5 + Validate 3 + const 1 + FormatMessage 1).
- **`901975f` s6-r69 B5 phase 4 — mTLS server mode='required' e2e IT** — `configureApp` 시그니처에 `mtlsValidationOverride` 인자 추가 (embedderFactoryOverride 와 동일 test 친화 hook 패턴). `configureMtls` 가 override 박제 시 chain.Build production logic 우회 → self-signed client cert 의 chain 부재 환경에서 handshake-level e2e 검증 가능. production chain.Build + whitelist logic 은 s6-r53 unit fact 별도 박제. `MtlsRequiredFixture` 신설 (server/client self-signed cert 2 종 + AllowedThumbprints + HttpClientHandler.ClientCertificates wire). 신규 **+3 IT Fact** (valid cert 200 / 미박제 handshake 거부 / wrong cert thumbprint mismatch 거부).
- **(doc-r3)** 본 transfer + server.md §7.1 commit chain + §7.4 backlog row 박제.

**누적 사용자 turn 전체 통계**: 5 commit (s6-r66~r69 + doc-r3) / +1005 line / +32 Fact / 회귀 0.

**이전 turn (참고)**:

**Phase 4 종결 + 외부 --review 전체 종결 + s6-r62~r65 누적 (markdown view cherry-pick + C6 citation UI + D-S7-5 phase 2 production + B5 phase 2 UI cert + light-house-meta-in-kb squash merge)** — `Ds2.LightHouse` lib + service + IT + Promaker 통합 완료. 누적 **676 Fact** (lib 177 / service 143 / IT 40 / Promaker 316). 회귀 0. paired-release ps1 통과 박제 (IndexerVersion 2.1.0 ∈ [1.0.0, 2.99.99]).

**s6-r62~r65 누적 (본 turn, 2026-05-20)**:
- **markdown view branch cherry-pick (4 commit)** — `2d998d1` / `b3dfab9` / `7ed410a` / `8b7bb0e` — llm branch 의 MdXaml 1.27.0 (`MarkdownScrollViewer` + A1 assistant 전체 markdown) 4 commit 을 light-house 위로 cherry-pick (conflict 0 chain merge). llm branch + worktree 삭제 완료.
- **`ac0ce89` s6-r62 (a) C6 Promaker citation UI** — `[fileName](attachment:///fileId/ref)` 형식 + MdXaml Hyperlink 자동 변환 + RequestNavigate handler (attachment/// = popup / http(s) = OS shell / 그 외 = 보안 차단) + turn-scoped citation cache (LlmChatViewModel.Citation.cs) + Reset 시 ClearCitationCache + `5.knowledge-base.md` system prompt + `TryParseAttachmentUri` (URI fileId `:` port 해석 결함 회피) + Promaker.Tests +8 (CitationUriParseTests 8 fact).
- **`ff80387` s6-r63 (b) D-S7-5 phase 2 resumable upload production-ready** — UploadsEndpoint per-uploadId SemaphoreSlim race / Content-Length=Content-Range length 검증 / crash 회복 (partial>offset truncate) / finalize 본격화 (extractAll + MetaJson + IndexerVersion gate + moveStagingToCollection + Registry.upsertAsync + bus.collection-added). CollectionEndpoints.processStagingExtractGate private 제거 (SSOT helper 재사용). LightHouseClient 5 method + UploadCollectionResumableAsync wrapper + 2 DTO (Start/Status). IT +2 (size mismatch / lock race) + Promaker.Tests +5.
- **`60e6ed4` s6-r64 (c) B5 phase 2 UI X509Store 선택 dialog** — ApplicationSettingsDialog DataGrid 의 "Client Cert" column + LhSelectCert_Click handler (X509Certificate2UI.SelectFromCollection, LocalMachine\My) + ThumbprintShortConverter (마지막 8 자리 표시). cert 발급/관리 정책 docstring (사내 CA Import-PfxCertificate / certmgr.msc). ThumbprintShortConverterTests +3.
- **`71744fb` s6-r64+ doc-r1** — next-session.md / server.md §7.1 + §7.4 SSOT row 박제.
- **`be91dc5` s6-r65 light-house-meta-in-kb squash merge** — 3 commit (`94f02e0` + `7223432` + `da706cb`) 의 변경을 단일 commit 으로 압축. **server MetaJson SubDir=".lighthouse-kb" 박제** (zip layout §3.3 / storage §3.10 마이그레이션 wipe) + CLI Packager **in-place 색인** (staging copy 폐기, 산출물 `<source>/.lighthouse-kb/` 보관) + Promaker CollectionPackager 의 metaPath 정합 + `install-ollama.ps1` 신규 (78 line) + Makefile (+7/-4). light-house-meta-in-kb branch + worktree 삭제 완료. 11 파일 +299/-147.
- **(rename)** `todo-fix-lighthouse-search-keyword.md` → `done-fix-lighthouse-search-keyword.md` / `todo-llm-imageview.md` → `done-llm-imageview.md` — git mv pure rename, 외부 참조 0.

**누적 사용자 turn 전체 통계**: 10 commit (cherry-pick 4 + 본격 phase 3 + doc 2 + rename 1 + squash 1) / +2200+ line / +21 Fact / 회귀 0.

**잔여 (별 turn 의무, 본 turn 종결 후)**:
- ~~**C6 Promaker citation UI**~~ → **s6-r62 종결 (ac0ce89)** ✅
- ~~**D-S7-5 phase 2**~~ → **s6-r63 종결 (ff80387)** ✅
- ~~**B5 phase 2 UI X509Store**~~ → **s6-r64 종결 (60e6ed4)** ✅
- ~~**A1 D-S7-4 T2/T3 multi-tenant**~~ → **s6-r66 종결 (c01af8f)** ✅
- ~~**D-S7-5 phase 3 chunked auto**~~ → **s6-r67 종결 (3ed6c1e)** ✅
- ~~**B5 phase 3 DataGrid edit + CertValidator**~~ → **s6-r68 종결 (77c8a88)** ✅
- ~~**B5 phase 4 mTLS server mode='required' e2e IT**~~ → **s6-r69 종결 (901975f)** ✅
- ~~**15-reviewer Critical 16 + Major 1 (C-1~C-14, C-16, C-17, R1-M1)**~~ → **s6-r70 종결 (60a2f6a)** ✅
- **D-S7-4 admin endpoint 후속** — T2 → `POST /admin/collections/{id}/owner {user}` (ImportedBy stamp) / T3 → `PUT /admin/collections/{id}/acl {users, readOnly}` (acl 편집). multi-tenant 실 활용 path. 별 turn.
- **D-S7-5 phase 4 swap chunked** — `ReingestAndReuploadAsync` 의 큰 zip swap 시 chunked path. server `POST /collections/{id}/payload` 가 chunked 지원하도록 별 endpoint. 별 turn.
- **C-15 EndpointHelpers SSOT 추출 (K4 Protocol phase 묶음)** — writeJson / writeError / userIdentityOf / jsonOpts 4 helper 가 CollectionEndpoints / SessionEndpoints / EventsEndpoint / FileServing / UploadsEndpoint 5 박제. K4 Protocol SSOT 통합 phase 진입 시 묶음.
- **R14 Major (mTLS / MultiTenant T2-T3 / Hybrid retrieval wire-level round-trip IT)** — 본 turn s6-r69 B5 phase 4 + s6-r70 C-1~C-4 가 부분 박제. 본격 T2/T3 e2e IT (MultiTenant fixture) 는 별 turn.
- **R12 Major 7 (성능)** — withReadOnlyConn 매 호출 vec0 LoadLibrary 등 — 별 perf phase.
- **15-reviewer 잔여 Major (R2/R3/R5/R6/R8/R9/R11/R13/R14/R15 묶음)** — 본 turn 비처리. 별 backlog phase.
- **`/dist` 실행** — paired-release ps1 통과 박제 확인 완료. **사용자 직접 호출 의무** — `make dist` 또는 `/dist` skill. **잔존**.

**별 세션 이연 의무**:
- **#3 Phase S7 잔여** (D-S7-4 T2/T3 multi-tenant / D-S7-5 resumable upload) — ~~D-S7-1 mTLS server-side~~ → s6-r53 종결. 각 매우 large (~300~500 line), sub-agent 위임 의무. 단독 phase. **Promaker client cert 적용 + PSK fallback 단계적 제거 = 별 phase 박제** (client cert 발급/관리 정책 박제 의무).
- **#5 외부 --review 잔여 ⑬ ⑭ ⑲** (3건, medium-large) — Searcher tuple→record / CaptionGenerator HTTP wire fact / EventsEndpoint client-write keepalive. ~~⑱ SessionRegistry purge helper~~ → s6-r55 종결. 각 별 turn.
- **(d) C4~C7 묶음** (4건, 별 영역) — C4 RefLocator parser 강화 (`sheet=BOM!A1:D40` / `p=14#img=2`) / C5 attachment_read image mode 정합 / C6 Promaker citation UI / C7 Searcher hit hasImages 의미화. 각 별 영역 — 별 turn.

**새 세션 진입 prompt (--transfer 박제 SSOT, s6-r66~r70 + doc-r3/r4 종결 후, 2026-05-20)**:

```
@todo-lighthouse-next-session.md @todo-lighthouse-kb-server.md @todo-lighthouse-kb-index.md 기준.

[직전 turn 누적 6 commit — 사용자 "b 제외 일괄 진행" → "1, 2 진행" → "--review" → "본 turn 에서 반론건 제외하고 한꺼번에 처리" 흐름]
- c01af8f s6-r66 D-S7-4 T2/T3 multi-tenant + config migration chain 제거 (사용자 옵션 #2 scope 확장) — MultiTenantPolicy module + Config schema 3→4 + Registry CollectionAcl + SessionEndpoints/CollectionEndpoints filter + +18 service Fact (자가 검열 sub-agent 위임)
- 3ed6c1e s6-r67 D-S7-5 phase 3 — chunked path 자동 선택 (LightHouseClient.ResumableUploadThresholdBytes=256 MiB const SSOT) + AttachmentIngestService.UploadAsync 분기 + +1 Promaker Fact
- 77c8a88 s6-r68 B5 phase 3 — CertValidator helper (Normalize/Validate/FormatMessage 4 분기 ValidationResult enum) + DataGrid edit TextBox + '검증' 버튼 + +10 Promaker Fact
- 901975f s6-r69 B5 phase 4 — configureApp 시그니처 mtlsValidationOverride 추가 + MtlsRequiredFixture (server/client self-signed 2 cert + ClientCertificates wire) + +3 IT Fact (valid/미박제/wrong cert handshake)
- 60a2f6a s6-r70 15-reviewer Critical 16 + Major 1 일괄 처리 — D-S7-4 multi-tenant 격리 4 surface 무력화 결함 hotfix (C-1 FileServing / C-2 SSE / C-3 mTLS subject CN / C-4 Uploads UserIdentity) + Upload race 5건 (C-5~C-9) + Promaker UI 5건 (C-10~C-14) + Minor 3건 (C-16/C-17/R1-M1). 반론 2건 (R15-M2 RegistrySchema bump / C-15 EndpointHelpers SSOT K4 phase 묶음). 회귀 0
- (doc-r3/r4) 본 prompt 박제

[그 이전 turn 누적]
- s6-r62~r65: markdown view cherry-pick 4 + C6 citation UI + D-S7-5 phase 2 + B5 phase 2 + light-house-meta-in-kb squash merge + doc-r1/r2
- s6-r53~r61: D-S7-1 mTLS server-side / 보안 sweep / ⑱ purge helper / C7 hasImages / C4 ref EBNF / C5 image mode / D-S7-5 scaffold / B5 client cert
- Phase 4 P4-A~P4-C: sqlite-vec + IEmbeddingProvider + Indexer + Searcher hybrid + OllamaSharp adapter + Promaker LlmConfig.Embedding + server-side embedder + IT round-trip

누적 708 Fact (lib 177 / service 161 / IT 43 / Promaker 327). 회귀 0. branch = light-house (local-only).
paired-release ps1 통과 박제 (IndexerVersion 2.1.0 ∈ [1.0.0, 2.99.99]).
외부 --review 전체 종결 (L-Maj-1/3/4/5/6/10 + ⑬⑭⑮⑰⑱⑲⑳).
Phase S7 본격 종결 (D-S7-1 mTLS / D-S7-2 SSE / D-S7-3 multi-service / D-S7-4 multi-tenant / D-S7-5 resumable phase 1~3).

잔여 작업 (s6-r69 + doc-r3 종결 후, 다음 turn 박제):
- A. large phase (단독 turn, sub-agent 위임 의무):
  - A2 K4 Protocol SSOT 통합 (Solutions/Core/Ds2.LightHouse.Protocol 신규 project) — wire 상수 + MetaJson schema SSOT 통합 (server F# + client C# 이중 박제 → 단일 SSOT)
  - A4 Phase 3 OCR (Tesseract.NET + 한글)
- B. medium 별 turn:
  - **D-S7-4 admin endpoint 후속** (T2 ImportedBy stamp + T3 acl 편집) — multi-tenant 실 활용 path
  - **D-S7-5 phase 4 swap chunked** — ReingestAndReuploadAsync 의 큰 zip swap chunked path
  - B1 M13 Indexer.ingestFile outer transaction (perf)
  - B2 OoxmlExtractor 강화 (comments/footnotes/endnotes Drawing)
  - B3 image-only paragraph 분기 분리
  - B6 보안 sweep 잔여 (M9 PSK lifetime / M12 Promaker staging %TEMP% ACL)
- C. 소량 cosmetic:
  - (string * SearchHit) list → KeyedHit record (lib internal refactor)
  - C1 mn6 fixture 한영 혼재 / C2 P7 facade 통합 / C3 자가 검열 미적용
  - s6-r66 자가 검열 Minor 5건 backlog
- D. 정책 결정 (사용자 confirm):
  - D1 D-2-3 SSOT 정정 / D2 PrivateAssets 확장 / D3 todo git mv
- E. 외부 / 운영:
  - **E1 `/dist` 실행** — paired-release ps1 통과 박제 확인 완료. **사용자 직접 호출 의무** (`make dist` 또는 `/dist` skill)
  - E2 server.md §7.7 Minor outliers ~13건

우선순위 (a) A2 K4 Protocol SSOT 통합 (large phase, sub-agent 위임 의무 — 사용자 confirm 후 진입)
       (b) E1 /dist 실행 (paired-release 정합, 사용자 직접 호출 의무)
       (c) D-S7-4 admin endpoint 또는 D-S7-5 phase 4 (선택 medium, multi-tenant / chunked swap 실 활용 path)
       — 선택 부탁드립니다.
```

**위 prompt 를 새 Claude Code 세션 첫 메시지로 그대로 붙여넣기**. §3 읽기 순서 → §4 backlog 표 → 우선순위 (a)~(e) 선택 흐름으로 이어진다.

**다음 turn 진입 시 우선 작업**:
1. **A1 Phase S7 잔여** — ~~D-S7-1 mTLS~~ → s6-r53 종결 (server-side minimum viable, Promaker client cert 별 phase). 잔여 D-S7-4 (T2/T3 multi-tenant) / D-S7-5 (resumable upload) 중 사용자 우선순위 선택. 각 단독 phase, sub-agent 위임 의무.
   - D-S7-4 T2/T3 multi-tenant = Registry per-tenant + Session isolation + storage layout 변경. ~400~500 line.
   - D-S7-5 resumable upload = tus protocol 또는 Content-Range append + ZipImport 변경. ~300~400 line.
   - **B5 Promaker client cert 적용** (D-S7-1 후속) = HttpClientHandler.ClientCertificates + LocalMachine\My X509Store thumbprint lookup + LlmConfig.LightHouseServiceConfig.ClientCertThumbprint 박제. ~100~150 line. **client cert 발급/관리 정책 박제 의무**.
2. **A2 K4 Protocol SSOT 통합** (server.md §7.7 K4) — Solutions/Core/Ds2.LightHouse.Protocol 신규 project. wire 상수 + MetaJson schema SSOT 통합 (server F# + client C# 이중 박제 → 단일 SSOT). Phase S7 묶음 권장.
3. **A3 보안 sweep 1턴** (server.md §7.7 K6 + M9~M12) — registry.json tampering + PSK in-memory lifetime + %PROGRAMDATA% ACL + DoS guards.
4. **B4 #5 외부 --review 잔여 4건** — ⑬ Searcher tuple→record / ⑭ CaptionGenerator HTTP wire fact / ⑱ SessionRegistry purge helper / ⑲ EventsEndpoint client-write keepalive. 각 별 turn 또는 묶음.
5. **C4~C7 묶음** — attachment_read ref parser 강화 (`sheet=BOM!A1:D40` / `p=14#img=2`) + image mode 정합 + citation UI + hasImages 의미화. Phase 2 잔여 cosmetic.
6. **E1 `/dist` 실행** — IndexerVersion 2.1.0 정합. paired-release ps1 통과 박제. 외부 영향 (scp + tag + push) — 사용자 직접 호출 의무.

**Phase 4 P4-A → 외부 --review ⑳ commit chain (s6-r34 ~ s6-r52, 19 commit)**:

- **`d3d520c` s6-r52 #5 ⑳ ZipImport ArrayPool** — entry 마다 81920-byte alloc → ArrayPool.Shared.Rent 1회 + try/finally Return.
- **`6096cac` s6-r51 #5 ⑰ insertChunks cmd 재사용** — outer scope cmd + Parameters 재활용. SQL parse 1회.
- **`0d770f6` s6-r50 #5 ⑮ EventBus unit fact 5건** — DropOldest / Unsubscribe lifecycle / fan-out N=3 / silent skip. service Tests 125→130.
- **`4e28a86` s6-r49 #2 mtime fast-skip** — SqliteStore IndexerVersion 2.0.0→2.1.0 + SchemaVersion 4→5 + Documents.FileMTimeTicks ALTER. findDocumentByPath 신규 + insertDocumentWithMtime + legacy wrapper. Indexer ingestFile fast-skip path (mtime/size 일치 시 hash 계산 skip). Tests fact 정정 3 + 신규 2 = lib 168→170. 누적 619 Fact (+2). paired-release ps1 통과.
- **`eed18a8` s6-r48 #4 ApplicationStopping hook** — service-singleton embedder graceful shutdown Dispose (IHostApplicationLifetime.ApplicationStopping.Register). override path 미적용. C2 잔여 종결.
- **`c10ccac` s6-r47 #1 Pooling=False (read-only path 만)** — SqliteStore.openConnection csb 의 Pooling=not readOnly. KnowledgeBase withReadOnlyConn ClearPool 제거. s6-r42 의 IT 7건 실패 회귀 회복 (전역 pool flush 부작용 해소).
- **`7a42961` s6-r46 C2 server-singleton + NonOwning** — `Ds2.LightHouse.NonOwningEmbedder` type 신설 (lib EmbeddingProvider.fs line 28 doc 예고 정합). Program.fs production path = service-singleton OllamaEmbedder 1회 생성 + 매 호출마다 NonOwningEmbedder(singleton). HttpClient socket exhaustion 회피 + 다중 session 누적 cost 0. KB.Dispose 가 wrap 만 dispose → inner singleton 보호. 회귀 0. trigger ④/⑤ 충족 — self-review.
- **`6ca7c1a` s6-r45 C1 IT hybrid path 활성** — configureApp 의 embedderFactoryOverride optional 인자 신규. IT ServiceFixture 가 Embedding.Enabled=true + MockEmbedder factory 주입. IT 33 자동 hybrid path 회귀 차단 검증. main caller 1 line 변경. C2 안전망.
- **`e77e0ec` s6-r44 B3 OoxmlExtractor Blip cache** — paragraph hot path 의 `hasInlineDrawing` + `extractImagesFromBlock` 이중 deep enumerate 정정. `collectValidBlips` (1회 enumerate + valid Blip ResizeArray) + `extractImagesFromBlips` (cached variant) 신설. `extractImagesFromBlock` signature `OpenXmlElement → Blip ResizeArray`. paragraph match arm 만 cached path. 회귀 0 (lib 168 OoxmlExtractor 16 fact 정합).
- **`ee067a0` s6-r43 B2 CollectionEndpoints helper 추출** — `postCollections` / `postCollectionPayload` 양쪽 IndexerVersion gate 분기 박제 중복 → private `processStagingExtractGate` 흡수. `labelSuffix` (`""` / `" (swap)"`). postCollectionPayload Missing 분기 Log.audit.Warn drift 정정. 415 응답 4 키 그대로. 회귀 0 (service 125 + IT 33).
- **`8991f9a` s6-r42 B1 KnowledgeBase helper 추출** — `probeIndexerVersion` + `lookupDocument` 박제 중복 → private `withReadOnlyConn` 흡수. `stampIndexerVersion` (write path) 별도 유지. ClearPool 의 hot path 전역 pool flush 부작용 정정 (`Pooling=False`) 은 별 turn (SqliteStore.openConnection SSOT 변경 trigger ⑤). 회귀 0 (lib 168).
- **`b9e5c51` s6-r41 D1+D2+D3 Minor backlog 묶음** — s6-r40 자가 검열 Minor 3건 (env var SSOT / ct 전파 / csproj mojibake) 한 commit 처리. (D1) cli `Program.fs` 5건 top-level `[<Literal>]` (`EnvOllamaUrl/Model/Dim/Psk`) + `Vlm.fs` 2건 nested `[<Literal>]` (`EnvApiKey/EnvModel`), magic string → literal 치환 caller 9건. (D2) `AttachmentTools.fs:235` 의 `kb.Search q CancellationToken.None` → `accessor.HttpContext.RequestAborted` (null fallback fail-safe). (D3) `Promaker.csproj` 11 라인 한글 코멘트 mojibake 의미 복원. 변경 = 4 파일 (cli/Program.fs + cli/Vlm.fs + service/AttachmentTools.fs + csproj) + doc 2. build 통과 + lib 168 + service 125 + IT 33 + Promaker 291 = **누적 617 Fact** 유지. 자가 검열 trigger ③ 충족 만 — 변경 trivial (literal 치환 + ct 1 line + 코멘트). 잔존 Minor = M3 (server embedderFactory HttpClient lifecycle, P4-D 또는 별 turn).
- **`2ff4d59` s6-r40 P4-C 자가 검열 + Major-1 patch** — Phase 4 P4-C 시리즈 (s6-r36~s6-r39, 4 commit `5344de0` → `d428609`) 누적 자가 검열 sub-agent (general-purpose) 위임 후속. 27 파일 / +791 / -118 cross-commit drift + 4 cross-cutting 항목 (a/b/c/d) 점검 결과 Critical 0 / Major 1 / Minor 4 / 잔여 우려 4. 박제 의도 vs 실 구현 drift 0. **Major-1 patch** = `ApplicationSettingsDialog.xaml.cs` 의 `EmbeddingConfigEquals` (line 707-715) + `LhWorkingCopyDirty` (line 692-695) 의 비대칭 Trim → 양쪽 Trim 일관 정정. legacy disk JSON 의 untrimmed BaseUrl/Model 박제 시 dirty=false 잘못 판정 → Save 누락 회피. Minor 4건 backlog 박제 (ct=None / env var SSOT / embedderFactory HttpClient lifecycle / csproj mojibake). 본 turn 변경 = Promaker 1 + doc 2 (server.md §7.1 row + §7.4 marker + next-session.md §2). build 통과 + Promaker.Tests 291/291 회귀 0. 누적 **617 Fact** 유지.
- **`d428609` s6-r39 P4-C.3** — server-side embedder 주입 + server config schema 1→2 migration. `EmbeddingConfigSection` record 신설 (Enabled/BaseUrl/Model/Dimension) + `ServiceConfig.Embedding` 추가 + `ConfigSchema.Current = 2` bump + `Config.load` 의 1→2 in-place migration path (default Enabled=false BM25-only). `ISessionRegistry.AttachKb` member 신설 + `SessionRegistry(resolver, embedderFactory)` constructor + 편의 생성자 backward-compat. `SessionKb.attach` signature breaking (embedderFactory 인자 추가). `AttachmentTools.withKb` + 4 method 시그니처 `AttachmentResolver` → `ISessionRegistry` 치환. `Program.fs` 의 embedderFactory SSOT (config 기반 OllamaEmbedder 생성, per-session lifecycle). `AttachmentToolsTests` 21 caller + helper 시그니처 전파 + `ConfigTests` 의 schemaVersion 2 + Embedding migration 4 assertion + IT `ServiceFixture` Embedding 박제. 누적 617 유지.
- **`19e3e6f` s6-r38 P4-C.2** — Promaker LlmConfig.Embedding schema + Settings dialog UI + AttachmentIngestService 주입. 사용자 결정 **(b) nested + DPAPI skip + per-ingest lifecycle**. `EmbeddingProviderConfig` class 신설 (Enabled/BaseUrl/Model/Dimension) + `LightHouseServiceConfig.Embedding: EmbeddingProviderConfig?` nullable. `AttachmentIngestService.TryCreateEmbedder` helper (active service Embedding 박제 → OllamaEmbedder 생성, validation 실패 시 null + warn). try/finally lifecycle + Log.Info embedder type name. `ApplicationSettingsDialog` 의 "Embedding (벡터 검색)" section (CheckBox + 3 TextBox, active service 1개 가정). `LoadEmbeddingUi` / `SaveEmbeddingUiToWorking` + `EmbeddingConfigEquals` dirty check 박제. `Promaker.csproj` 의 `Ds2.LightHouse.Ollama` ProjectReference. 신규 4 Fact (LlmConfigTests 32→36): default Ollama bge-m3 / Embedding null = BM25 fallback / round-trip / null round-trip. 누적 613→**617 Fact**.
- **`045965d` s6-r37 P4-C.1** — OllamaSharp adapter 별 fsproj 신설 + cli resolveEmbedder 본격화. 사용자 결정 **안 B** (lib backend-neutral 유지, 별 project). 신규 `Solutions/Tools/Ds2.LightHouse.Ollama/` (fsproj + OllamaEmbedder.fs ~100 line). `IEmbeddingProvider` 구현, 생성자 overload 2종 (`IOllamaApiClient` + ownsClient 직접 / baseUrl+model+dim 편의), `EmbedAsync(EmbedRequest)` batch wrapper, dim 검증 fail-fast, IDisposable impl. `OllamaDefaults` SSOT (bge-m3/1024/localhost:11434). `OllamaSharp 5.4.25` PackageVersion (Solutions). cli `resolveEmbedder` 본격화 + env var override (`LIGHTHOUSE_OLLAMA_URL/MODEL/DIM`) + lifecycle try/finally. sln 등록 (Promaker.sln + Promaker.light-house.sln). 누적 613 유지.
- **`5344de0` s6-r36 P4-C.0** — 자가 검열 잔여 박제 (P4-B 자가 검열 + 외부 --review 잔여 중 P4-C 진입 전 의무 3건 선처리). `IEmbeddingProvider` 가 `inherit IDisposable` (외부 review L-Maj-4 + 자가 검열 M4 해소). `KnowledgeBase.Dispose` 가 own 한 embedderOpt 동반 dispose. `Searcher.search` signature breaking (`ct: CancellationToken` 6번째 인자 추가, hybrid path 의 `embedder.GenerateAsync` ct 전파 + BM25/hybrid path 진입 시 `ThrowIfCancellationRequested` — 자가 검열 C1 + 외부 --review L-Min-6 해소). `KnowledgeBase.Search` record field 시그니처 breaking (`Query -> ct -> SearchResults`). cli flag key 7건 top-level `[<Literal>]` SSOT (F# 9 strict-indentation 정합) — 자가 검열 m3 해소. mock embedder 2종 (MockEmbedder / QueryFriendlyEmbedder) IDisposable impl. AttachmentTools (server) + KnowledgeBaseTests 15 caller 전파. 누적 613 유지.
- **`ee5ddbc` s6-r35 P4-B (안 A)** — Searcher hybrid retrieval (BM25 + vector RRF) + cli `--no-embedding` flag wire. 사용자 결정 안 A (lib hybrid path SSOT + cli flag forward-compat 만, OllamaSharp adapter 는 P4-C 박제). `RrfK=60` + `HybridFetchMultiplier=2` literal SSOT. `buildCollectionSelect` 가 `c.Id AS ChunkId` 추가. `buildVectorSelect` 신설 — sqlite-vec subquery 격리 표준 패턴 (vec0 vtable 의 JOIN/외부 column filter 직접 결합 syntax error 회피). `readHit` / `runBm25` / `runVector` / `rrfMerge` (Map 누적, `1/(k_rrf+rank)`). `search` 시그니처 breaking (`embedderOpt: IEmbeddingProvider option` 5번째 인자). `KnowledgeBase.openCollections` 도 embedderOpt 추가 + `:memory:` main connection vec0 load 의무 박제 (test 회귀에서 발견). cli `--no-embedding` flag + `resolveEmbedder` SSOT (P4-C forward-compat). 외부 --review 적용 2건 (L-Maj-1 `upsertChunkEmbeddingsBatch` transaction wrap + L-Maj-4 `KnowledgeBase` IDisposable interface). 신규 5 Fact (KnowledgeBaseTests 163→168). 자가 검열 sub-agent: Critical 1 / Major 4 / Minor 4 → 즉시 적용 m4 mock xmldoc + 거부 8 (P4-C 잔여). 누적 608→**613 Fact**.
- **`83e57af` s6-r34 P4-A 본격** (참고) — sqlite-vec schema 확장 + IEmbeddingProvider interface + Indexer embedder hook + IndexerVersion 2.0.0 major bump. `EmbeddingProvider.fs` 신규 (`IEmbeddingProvider` interface, Dimension + GenerateAsync batch). `SqliteStore.fs` 의 `Chunks_Vectors USING vec0(ChunkId PRIMARY KEY, embedding float[1024])` virtual table 신설 + `loadVec0Extension` 절대 path (AppContext.BaseDirectory + runtimes/<rid>/native/vec0.dll) + `EmbeddingDimension=1024` SSOT + `upsertChunkEmbedding` (DELETE+INSERT, vec0 UPSERT 미지원 우회) + `listChunkIdsByDocument`. `Indexer.fs` 의 `dispatchEmbeddings` 신규 + `ingest/ingestFile/ingestFiles/rebuildShadow` 4 signature breaking (embedderOpt parameter). caller 4 (cli + Promaker) + tests 7 모두 `None` 주입. service `config.json.template` indexerVersionRange.max "1.99.99" → "2.99.99". IT fixture 동일 갱신. 자가 검열 sub-agent: Critical 0 / Major 0 / Minor 5 (모두 backlog). 잔여 우려 0. **회귀 0** — lib 154→**163** (+9: Chunks_Vectors / EmbeddingDimension SSOT / upsertChunkEmbedding INSERT/REPLACE / listChunkIdsByDocument ORDER+empty / Indexer embedder None/Some/Some+empty) / service 125 / IT 33 / Promaker 287 = **누적 608 Fact**. **다음 turn = P4-B 본격** (Searcher hybrid retrieval BM25+vector RRF + Promaker/cli `--no-embedding` flag + OllamaSharp adapter + Settings dialog + IT round-trip).
- **직전 = s6-r33 (`7d700a8`)** — Phase 4 **P4-A.0** sqlite-vec NuGet 의존 추가 + native binary 배포 path 검증 (사용자 결정 (α) 안전 분할). 결정 박제: backend = **Ollama** + 모델 = **bge-m3** (1024 dim) + chunk-level + Chunks_Vectors virtual table + RRF hybrid + CLI default-on + IndexerVersion major bump (다음 turn). 본 turn = **P4-A 본격 진입 전 native binding 실 배포 path 검증 분리 commit**. `sqlite-vec 0.1.7-alpha.2.1` NuGet 박제 (Solutions + Apps Directory.Packages.props 양쪽), lib `PrivateAssets="all"` + 모든 caller fsproj `ExcludeAssets="contentFiles"` 박제 (Vector.cs C# wrapper compile fail 차단, F# raw LoadExtension path 정합). 모든 caller publish output 의 `runtimes/win-x64/native/vec0.dll` 자동 배포 검증 완료. 7 fsproj 변경 (의존성만, 기능 코드 0). 회귀 0 — lib 154 / service 125 / IT 33 / Promaker 287 = **누적 599 Fact 유지**. **다음 turn = P4-A 본격** (SqliteStore schema 확장 + IEmbeddingProvider interface + Indexer embedder hook + IndexerVersion major bump + lib unit tests).
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
- **test** = `Solutions/Tests/Ds2.LightHouse.Tests` (lib, 177) / `Solutions/Tests/Ds2.LightHouseService.Tests` (service, 161) / `Solutions/Tests/Ds2.LightHouseService.IntegrationTests` (e2e + cli + mTLS, 43) / `Solutions/Tests/Promaker.Tests` (Promaker, 327) = **누적 708 Fact**
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
