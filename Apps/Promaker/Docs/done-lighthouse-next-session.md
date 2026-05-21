# Ds2.LightHouse / Promaker — 다음 세션 이어받기

본 문서는 새 Claude Code 세션 진입 시 빠르게 현재 작업 상태를 파악하고 backlog 처리를 이어가도록 정리된 transfer 박제이다. 모든 SSOT 는 동일 폴더의 두 doc 에 박제됨:

- `done-lighthouse-kb-server.md` — server-side phase 진행 SSOT (§0 / §7.1 commit chain / §7.4 backlog)
- `done-lighthouse-kb-index.md` — parent (LightHouse lib 본체) phase 진행 SSOT (§0 / §3.x design)

본 문서는 새 세션 진입 시 *읽는 순서 + 핵심 backlog 분류* 만 박제. 자세한 박제는 위 두 doc 의 해당 단원 참조.

## 1. 작업 목표

Promaker IDE 의 KB (knowledge base) 시스템 — 사용자 폴더 색인 + central Windows Service share + MCP search host. Phase 1 (LightHouse lib 본체) / Phase S1~S6 (Windows Service + Promaker 통합 + Phase 2 image VLM caption + cli upload + paired-release) 종결. **Phase S7 본격 종결**:
- ~~D-S7-1 (mTLS server-side + Promaker client cert + B5 UI 4 phase)~~ → s6-r53/r61/r64/r68/r69 완료 (B5 phase 1~4 누적)
- ~~D-S7-2a/b/c (SSE server + client subscribe + 정합 묶음)~~ → s6-r27/r28/r32 완료 (D-S7-2 시리즈 종결)
- ~~D-S7-3a/b/c (multi-service routing 전체 — schema + Holder/N session/MCP + UI)~~ → s6-r29/r30/r31 완료 (§3.16)
- ~~D-S7-4 T2/T3 multi-tenant~~ → s6-r66 완료 (MultiTenantPolicy SSOT + config migration chain 제거)
- ~~D-S7-5 resumable upload phase 1~3 (scaffold + production + auto chunked)~~ → s6-r60/r63/r67 완료
- ~~**A2 K4 Protocol SSOT 통합**~~ → s6-r71 종결 (`53c939f`)
- ~~**D-S7-4 admin endpoint**~~ → s6-r72 종결 (`1d2f1f9`)
- ~~**A4 Phase 3 OCR**~~ → **drop 결정 (사용자, 2026-05-20)** — Phase 2 D-2-2 eager VLM caption (Sonnet 4.6) 가 image 안 한/영 text 도 caption 으로 추출 → Tesseract.NET 별도 통합은 redundant. backlog 폐기.
- ~~**B-S7-5 phase 4 swap chunked**~~ → s6-r74 종결 (`e8391dd`)
- ~~**B-R14 MultiTenant T2/T3 round-trip IT**~~ → s6-r74 종결 (`e8391dd`)
- ~~**N-1 CertValidator.Normalize SSOT 통합**~~ → s6-r75 종결 (`f104374`)
- ~~**external review backlog Major 5**~~ → s6-r76/r77 종결.
- ~~**A2 m3 Middleware Protocol routing**~~ → s6-r78 종결.
- ~~**B2 OoxmlExtractor 강화 (comments/footnotes/endnotes Drawing)**~~ → s6-r78 (comments 대표) + s6-r82 (footnotes/endnotes 별 fact) 종결.
- ~~**B3 image-only paragraph 분기 분리**~~ → s6-r21 기존 박제로 흡수 confirm.
- ~~**C-15 EndpointHelpers SSOT 추출**~~ → s6-r79 종결 (5 SSOT helper + 6 endpoint alias).
- ~~**B-S7-4 admin-only ACL 분리**~~ → s6-r80 종결 (config.adminUsers + requireAdmin + 5 fixture migration).
- ~~**B PR M-3 acl users element 정규화**~~ → s6-r80 종결 (normalizeAclUsers helper).
- ~~**external review backlog Minor (N-M1~M4 + R6-M3 + R5-M2 doc)**~~ → s6-r81 종결.
- ~~**config.json.template adminUsers 박제**~~ → s6-r84 (A4 부분) 종결.
- ~~**15-reviewer 보안 critical 4건** (A1 mTLS CN escape / A2 Execution diag / A8 Sqlite leak / A9 fsproj)~~ → s6-r83 종결.
- ~~**15-reviewer K4 마무리** (B20 alias / A3 반론)~~ → s6-r84 종결.
- ~~**15-reviewer Major** (B3 OoxmlExtractor / B4 vec0 wire / B17 install PSK / B2 반론)~~ → s6-r85 종결.
- ~~**15-reviewer B8 + A6** (symlink reject / reader loop disconnectSignal)~~ → s6-r86 종결.
- ~~**B19 doc SSOT drift sweep**~~ → s6-r87 (doc-r8 안) 종결.
- ~~**Major 검증 fix small 묶음** (B5/B7/B9/B10/B11/B12/B16/B21 PSK)~~ → s6-r88 종결.
- ~~**B13 admin atomic race + B14 ACL revoke event**~~ → s6-r89 종결.
- ~~**B18 closure 회피 + N-M5 cfg DRY**~~ → s6-r90 종결.
- **잔여**: **A5 Runtime spec 사용자 confirm** / **A7 Session lifecycle TOCTOU single lock** / **B1 dispatchEmbeddings partial state** (schema bump) / **B6 OriginalPath UNIQUE migration** / **B15 caption-progress owner (T2/T3 IT fixture)** / **B2 lib async transformation** (별 phase) / **R8-M5 UI Task.Run wrap** (별 phase) / **B21 잔여**: KbManager SSE swallow / KnowledgeBase.Dispose / UploadsResumableTests race / SseReconnectBackoff cap / **N-M1 단위 fact** / **MimeTypes.JsonUtf8 literal** / **A1/A3/A10 단위 fact** / **Promaker client OnAclChanged IT round-trip** (s6-r89 후속) / **IT admin-only round-trip fact** / R12 4건 (사용자 명세 input) / E1 `/dist` (사용자 직접 호출 의무).

## 2. 현재 commit state (본 transfer 박제 시점 — s6-r88~r90 + doc-r9 종결, 2026-05-21)

**Major 검증 fix 묶음** (B5/B7/B9/B10/B11/B12/B13/B14/B16/B18/B21/N-M5 + B1/B2/B6/B15/B21cap/R8-M5/R12 defer) — `Ds2.LightHouse` lib + service + IT + Promaker 통합 완료. 누적 **746 Fact** 유지 (lib 180 / service 179 / IT 55 / Promaker 332). 회귀 0. paired-release ps1 통과 박제 (IndexerVersion 2.1.0 ∈ [1.0.0, 2.99.99]).

**s6-r88 ~ s6-r90 누적 (본 turn, 2026-05-21)**:
- **`c3edb53` s6-r88 Major fix 묶음** — B5 (Indexer mtime ±2초 tolerance, FAT32/SMB drift 흡수) + B7 (Registry.upsertAsync write-path validateEntry) + B9 (FileServing If-None-Match RFC 7232 정합) + B10 (UploadsEndpoint exception 4-arm 의 uploadId staging cleanup) + B11 (StagingSweep effectiveLastWriteUtc — Linux ext4 silent strangle 차단) + B12 (SessionSweep TryRemove 후 LastUsedAt re-check + re-insert race 보호) + B16 (EventBus lifecycle channel Unbounded — R5-M2 commit message 정합 drop 0) + B21 PSK byte cache (Middleware.compareBearerSecret signature breaking byte[]). **반론**: B15 caption-progress owner validate (IT race 원인 불명 별 turn) / B21 SseReconnectBackoff cap (기존 fact 회귀 risk).
- **`9031800` s6-r89 B13 admin race + B14 ACL revoke event** — Registry.updateByIdAsync helper (lock 안 atomic mutate, last-writer-wins 차단) + ICollectionLifecycleNotifier.OnAclChanged + SessionRegistry phasedKbDispose (affected session KB 폐기 + lazy re-attach 시점 acl 재검증). AdminEndpoints.handleAcl signature breaking (notifier 인자) + map signature breaking (cfg notifier 박제).
- **`b8a77b2` s6-r90 B18 closure 회피 + N-M5 cfg DRY** — AttachmentIngestService.BuildCaptionGenStatic 의 apiKey/model 매 호출 재조회 (closure 캡쳐 회피, process lifetime 평문 잔존 차단). TestFixtures.fs 의 ServiceConfigBuilder.defaultConfig helper + 3 caller (AuthMiddlewareTests / FileServingTests / EndpointHelpersTests) migration.

**누적 사용자 turn 전체 통계 (본 turn s6-r88~r90 + doc-r9)**: 4 commit / +250+ line / 0 신규 fact (refactor + 보안 fix 만) / 회귀 0.

**이전 turn (참고 — 직전 transfer 박제)**:

**s6-r83 ~ s6-r86 + doc-r8 누적 (이전 turn, 2026-05-21)**: 746 Fact (lib 180 / service 179 / IT 55 / Promaker 332). 15-reviewer 종합 review 처리 (보안 critical 4건 + K4 마무리 + Major 검증 fix + B19 doc sweep).

**s6-r83 ~ s6-r86 누적 (이전 turn 발생, 2026-05-21)**:
- **`bfd6a9a` s6-r83 review PR 1 — 보안 critical 4건** — A1 mTLS CN escape (Middleware.extractCommonName subject.Split(',') 가 RDN escape `\,` 미인식 → BCL X509Certificate2.GetNameInfo(SimpleName, false) 표준 API 로 교체, signature `string → X509Certificate2` breaking) + A2 Execution.fs 진단 file append 제거 (log4net 으로 교체, production binary 디버그 잔재 차단) + A8 SqliteStore.openConnection 누수 guard (try-with reraise + Dispose) + A9 fsproj 순서 정합 (K4 SSOT 의도).
- **`7554f79` s6-r84 review PR 2 — K4 마무리 + security defaults** — **A3 반론** (UnmappedMemberHandling.Disallow 가 운영자 친화 `_*_note` field 와 충돌 → 별 audit warn helper 박제 backlog) + B20 Maj-5 alias 정합 (AdminEndpoints prefix → alias 박제) + A4 부분 config.json.template adminUsers 박제.
- **`2c5b891` s6-r85 review PR 3 — Major 검증 fix** — B3 OoxmlExtractor.extractImagesAtRefLocator (40+ line 본문) → delegation refactor + B4 vec0 wire format SSOT 통합 (SqliteStore.buildVec0WireFormat 단일, Searcher.runVector 의 별 JsonSerializer.Serialize 폐기) + B17 install-service.ps1 PSK byte buffer 변환 (managed string 평문 우회) + **B2 반론** (lib API async transformation = 별 phase, 본 turn scope 외).
- **`98749fb` s6-r86 review PR 4 — B8 + A6 race + A7 defer** — B8 FileServing.findSourceFile 가 FileInfo.Attributes.ReparsePoint 검사 추가 (symlink target follow 차단) + A6 reader loop Task.WhenAny 가 disconnectSignal.Task 도 포함 (Kestrel half-close 시점차 reader 영구 sleep 차단) + **A7 defer** (Session lifecycle TOCTOU = single lock SSOT, scope large).

**B19 doc SSOT drift sweep (s6-r87 doc-r8 안 박제)** — done-lighthouse-kb-server.md 의 service config 박제 정정 (schemaVersion 1→4 / listenUrl 0.0.0.0→127.0.0.1 / range max 1.99.99→2.99.99 / embedding / mtls / multiTenant / adminUsers field 추가) + meta.json 예시 indexerVersion 1.0.0→2.1.0 정합.

**누적 사용자 turn 전체 통계 (본 turn s6-r83~r86 + doc-r8)**: 5 commit / +200+ line / 0 신규 fact (보안 fix + refactor 만) / 회귀 0.

**이전 turn (참고 — 직전 transfer 박제)**:

**s6-r79 ~ s6-r82 + doc-r7 누적 (이전 turn, 2026-05-21)**: 746 Fact (lib 180 / service 179 / IT 55 / Promaker 332). C-15 EndpointHelpers SSOT + B-S7-4 admin-only ACL + B PR M-3 acl users 정규화 + external review backlog Minor (N-M1~M4 + R6-M3 + R5-M2 doc) + B2 footnotes/endnotes 별 fact 박제.

**s6-r79 ~ s6-r82 누적 (이전 turn 발생, 2026-05-21)**:
- **`9f31f68` s6-r79 C-15 EndpointHelpers SSOT 추출 — K4 잔여 helper 통합** — 신규 `EndpointHelpers.fs` (~57 line, 5 helper SSOT). 6 endpoint (AdminEndpoints / CollectionEndpoints / FileServing / SessionEndpoints / EventsEndpoint / UploadsEndpoint) 의 각자 박제 helper 폐기 → alias 박제 (F# first-class function, caller 변경 0). UploadsEndpoint 의 jsonOpts → `metaJsonOpts` rename + file write 용으로 유지. 의미 변경: AdminEndpoints ContentType + userIdentityOf Warn invariant 박제 일치. 자가 검열 sub-agent — Critical 0 / Major 1 (M-1 MimeTypes SSOT 분리 backlog) / Minor 3 (거부). 회귀 0.
- **`eb1d6d0` s6-r80 보안 묶음 — B-S7-4 admin-only ACL + B PR M-3 acl users 정규화** — (B-S7-4) ServiceConfig.AdminUsers 신설 (nullable optional, backward-compat). EndpointHelpers.requireAdmin helper (case-insensitive ordinal + whitespace trim). AdminEndpoints.map signature breaking (cfg 인자) + handleOwner/handleAcl 분리 + 403 audit log. (B PR M-3) AdminEndpoints.normalizeAclUsers helper (null safe + whitespace trim + empty filter + case-insensitive dedup). 신규 EndpointHelpersTests 12 fact (userIdentityOf 2 + requireAdmin 6 + normalizeAclUsers 4). 자가 검열 sub-agent — Critical/Major 0 / Minor 3 (거부). 보안 결함 0건.
- **`3e206e2` s6-r81 external review backlog Minor 묶음 — N-M1/M2/M3/M4 + R6-M3 + R5-M2 doc** — (N-M1) CertValidator.TryFind 가 다중 매칭 시 가장 늦은 NotAfter 선택 (만료 직전 + 신규 재발급 race 차단). (N-M2~M4) docstring 강화 (CryptoAPI duplicate / IsHandshakeReject swallow 범위 / DisposeAsync cleanup 순서). (R6-M3) Config.validateHttpsOnly 가 IPv6 link-local zone identifier (`%`) 거부 — Kestrel 미지원, loopback / global IPv6 통과. (R5-M2 doc) EventsEndpoint.handle docstring 박제 (client invariant: lifecycle 우선 도착). 신규 1 fact (R6-M3). 자가 검열 inline (Critical/Major/Minor 0).
- **`4bd7ae1` s6-r82 B2 footnotes/endnotes 별 fact — PR 4 잔여 fact 박제** — 신규 fixture 2 + 신규 fact 2 (`docx + footnote image — RefLocator="footnotes"` / `docx + endnote image — RefLocator="endnotes"`). extractImagesFromOpenXmlPart helper path 분기 누락 0 검증.

**누적 사용자 turn 전체 통계 (본 turn s6-r79~r82 + doc-r7)**: 5 commit / +400+ line / +15 Fact / 회귀 0.

**이전 turn (참고 — 직전 transfer 박제)**:

**s6-r76 ~ s6-r78 + doc-r6 누적 (이전 turn, 2026-05-21)**: 731 Fact (lib 178 / service 166 / IT 55 / Promaker 332). external review backlog Major 5 (R2-M1/R8-M2/R6-M1/R8-M5/R5-M2 EventBus) + A2 m3 Middleware Protocol routing + B2 OoxmlExtractor comments 박제.

**s6-r76 ~ s6-r78 누적 (이전 turn 발생, 2026-05-21)**:
- **`3c4b70c` s6-r76 external review backlog Major 4 — R2-M1 idx + R8-M2 jitter + R6-M1 null guard + R8-M5 file lock** — (R2-M1) SqliteStore.schemaSql 의 `IX_Documents_OriginalPath` 추가 (findDocumentByPath mtime fast-skip hot path O(N²) 차단, ensureSchema forward-compat). (R8-M2) SseReconnectBackoff production ctor 가 base delay 에 [-20%, +20%) random jitter (thundering herd 회피) + test 친화 internal ctor 가 jitter=0 deterministic 박제 (기존 6 fact 회귀 0). (R6-M1) Config.validateMtls 가 `cfg.Mtls` null guard (validateMultiTenant 정합 박제, defense-in-depth). (R8-M5) LlmConfig.Save() 가 cross-process file lock 통과 + `WithFileLock` helper 추출 (Save / ModifyWithLock 공통 SSOT) + EffectiveConfigPath 박제 정합 (TestConfigPathOverride prod path 침투 차단). 신규 7 fact (ConfigTests +1 / SseReconnectBackoff +4 / LlmConfigModifyWithLock +1 + B1). 자가 검열 inline (trigger ③+⑤, Critical/Major/Minor 0). 잔여 우려: Save() UI freeze risk (caller 8건 모두 UI thread, Task.Run wrap 별 turn).
- **`ec9112c` s6-r77 R5-M2 EventBus lifecycle / progress channel 분리 — DropOldest event loss 차단** — per-subscriber 2 channel 분리 (lifecycle capacity 32 + progress capacity 64). 이전 단일 channel 박제는 progress burst (caption/upload-progress) 시 lifecycle event (collection-added/updated/deleted) 가 oldest drop 가능 — client 가 collection 신호 영구 손실. `Subscribe` return = (Guid * lifecycleReader * progressReader) **breaking** — caller 6건 (EventsEndpoint 1 + EventBusTests 5) 정합. Publish 가 `isLifecycle` 분기 → lifecycle channel / progress channel 분기 fan-out. 신규 `IsLifecycleEvent` (test 진단용). EventsEndpoint.handle 가 Task.WhenAny 로 두 reader watch + lifecycle 먼저 drain (우선순위 박제). reviewer M1 patch — close 판정 race window 보호 (양 channel 모두 closed 일 때만 종료). 신규 3 fact (IsLifecycleEvent / cross-channel leak 0 / progress burst 시 lifecycle drop 0). 자가 검열 sub-agent — Critical 0 / Major 1 (M1 즉시 patch 적용) / Minor 3 (거부). 잔여 우려: lifecycle 우선 도착 invariant 박제 (client side documentation 의무).
- **`6dffc5e` s6-r78 A2 m3 Middleware Protocol routing + B2 OoxmlExtractor comments/footnotes/endnotes 박제** — (A2 m3) Middleware.fs `UserIdentityHeader = HeaderNames.UserIdentity` (Protocol SSOT alias). magic string `"X-User-Identity"` 폐기 — K4 A2 (s6-r71) 잔여 caller routing 흡수. C-15 EndpointHelpers SSOT 추출 (writeJson / writeError / userIdentityOf / jsonOpts 4 helper × 5 endpoint) 은 scope > 본 turn budget — 별 turn 박제 backlog. (B2) OoxmlExtractor 가 `mainPart.WordprocessingCommentsPart` / `FootnotesPart` / `EndnotesPart` 각 singleton 의 Drawing 추가 박제. `extractImagesFromOpenXmlPart` helper 재사용 (s6-r21 header/footer 와 동일 패턴). RefLocator scheme = `"comments"` / `"footnotes"` / `"endnotes"`. 신규 1 fact (comments 대표 — footnotes/endnotes 는 동일 helper path 통과로 별 fact 별 turn). 자가 검열 inline (trigger ③, Critical/Major/Minor 0).

**누적 사용자 turn 전체 통계 (본 turn s6-r76~r78 + doc-r6)**: 4 commit / +730+ line / +11 Fact / 회귀 0.

**이전 turn (참고 — 직전 transfer 박제)**:

**s6-r71~r75 + doc-r5 누적 (이전 turn, 2026-05-21)**: 721 Fact (lib 177 / service 162 / IT 55 / Promaker 327). K4 Protocol SSOT 통합 + B PR 보안/perf/admin + external review hotfix + B-S7-5 phase 4 swap chunked + B-R14 MultiTenant T2/T3 IT + N-1 CertValidator.Normalize SSOT.

**s6-r74 ~ s6-r75 누적 (이전 turn 발생, 2026-05-21)**:
- **`e8391dd` s6-r74 B-S7-5 phase 4 swap chunked + B-R14 MultiTenant T2/T3 round-trip IT** — (b1) `UploadsEndpoint.postFinalize` 가 `FinalizeBody.swapTargetCollectionId` 분기 — Registry.tryFindById 검증 (404 미존재) + MultiTenantPolicy.evaluate (Hidden=404 / ReadOnly=403) + ZipImport.swapCollectionPayload (s6-r9 K1 rollback-safe atomic) + Registry.upsertAsync (existing entry 갱신 + Acl 보존) + notifier.OnPayloadSwapped + EventBus.collectionUpdated. map 시그니처 변경 — notifier 인자 추가 (Program.fs caller 정합). LightHouseClient.FinalizeResumableUploadAsync + UploadCollectionResumableAsync 에 optional swapTargetCollectionId + 신규 ResumableFinalizeBody DTO. AttachmentIngestService.ReingestAndReuploadAsync 가 zip size > 256 MiB 시 chunked swap path 자동 진입. (b2) 신규 MultiTenantFixture.fs (T2/T3 두 fixture, ~180 line) + MultiTenantRoundTripTests.fs (T2 4 fact + T3 3 fact, ~140 line) + UploadsResumableTests swap 2 fact (chunked swap round-trip + 404). Promaker CollectionPackagerTests 1 fact s6-r71 K4 follow-up (JsonIgnoreCondition.Never 후 server-side 필드 빈 string 직렬화 검증). IT 46→55 (+9). 자가 검열 sub-agent — Critical 0 / Major 0 / Minor 3 (M1 fixture body 중복 trade-off / M2 setAcl helper SSOT 통합 별 turn / M3 user literal 의도). +395/-96 line / 9 files.
- **`f104374` s6-r75 N-1 CertValidator.Normalize SSOT 통합 — Protocol 단일 routing** — Solutions/Core/Ds2.LightHouse.Protocol/CertValidator.fs 신설 (`module CertValidator` + `[<CompiledName("Normalize")>] normalize`). strict 정합 (client behavior 채택): hex char + separator (`:` / 공백 / hyphen / tab) 외 non-hex 발견 시 빈 string + 길이 40 (SHA-1) / 64 (SHA-256) 검증. server Config.normalizeThumbprint + Promaker CertValidator.Normalize 둘 다 Protocol routing — client/server 의 silent strip ↔ strict empty 의미 drift 차단. Config.validateMtls 가 silent skip → strict reject (정합 결함 시 InvalidDataException, mtls.mode="required" 시 service 시작 fail-fast). 신규 ConfigTests fact 1건 (`validateMtls — non-hex 문자 fail-fast (N-1, Protocol CertValidator SSOT)`) → service 161→162. Promaker CertValidatorTests 9 fact 회귀 0 (Protocol routing 후 strict 의미 정합 검증). 자가 검열 inline (scope ~150 line / 5 파일) — Critical 0 / Major 0 / Minor 0.

**누적 사용자 turn 전체 통계 (본 turn s6-r74 + s6-r75 + doc-r5)**: 3 commit / +550+ line / +10 Fact (IT 9 + service 1) / 회귀 0.

**이전 turn (참고 — 직전 transfer 박제)**:

**s6-r71~r73 누적 (이전 turn, 2026-05-20 ~ 2026-05-21)**:
- **`53c939f` s6-r71 A2 K4 Protocol SSOT 통합** — `Solutions/Core/Ds2.LightHouse.Protocol` 신규 fsproj (F# net9.0, dependency 0 base). `ServerEventNames` module (Literal 6종, server F# + client C# 양분 → 단일) / `MetaJson` [<CLIMutable>] record (14 필드, JsonPropertyName camelCase) + `MetaJsonSchema.Current=1` + `MetaJsonIO` module (FileName / SubDir / path / load / save / stampServerFields / jsonOptions, F# 의 record-module 동명 자동 `MetaJsonModule` IL suffix 회피 → `MetaJsonIO` 명시 rename) / `ZipLayout` (SourceFolderName / KbFolderName) + `HeaderNames` (UserIdentity / IndexerVersion) + `MimeTypes` (Json / OctetStream / EventStream). server `MetaJson.fs` 의 `toRegistryEntry` 만 `MetaJsonRegistry` server-internal helper 로 잔류 (`CollectionEntry` / `CollectionAcl` Registry 의존 박제). Promaker `ServerEventNames.cs` 삭제 + caller (KbManagerDialog / LightHouseClientTests) `using Ds2.LightHouse.Protocol` 추가. Promaker `CollectionPackager.MetaPayload` private class 폐기 → Protocol `MetaJson` 사용 (CLIMutable + JsonPropertyName C# 호환). cli `Packager.MetaDto` 폐기 → Protocol `MetaJson` 사용. lib `SqliteStore.KbFolderName` 가 `ZipLayout.KbFolderName` SSOT inline (M22 zip layout 3중 박제 흡수 완성). 자가 검열 sub-agent 위임 — Critical 0 / Major 1 (M22 흡수) / Minor 3 (m1 dead `emptyClientFill` 제거 / m2 Promaker `JsonOptions.Never` 통일 / m3 caller routing backlog). +375/-248 line / 23 files.
- **`1d2f1f9` s6-r72 B PR 1~4 — 보안/perf/admin endpoint 일괄** — (B6) `LightHouseClient.NewRequest` 의 PSK 평문 lifetime 단축 (`psk = null` defense-in-depth, `HeaderNames.UserIdentity` Protocol SSOT 사용) + `StagingSession` ctor 의 explicit DirectorySecurity ACL 박제 (current user FullControl + protect inheritance, Windows 한정 best-effort fallback). (B1) `Indexer.ingestFile` 의 Document + OutlineNodes outer transaction (autocommit fsync N+1→1, `insertChunks` batch txn 은 nested 차단으로 별 scope 유지, Microsoft.Data.Sqlite ambient txn 자동 인식 정합). (B-R12 3건) `vec0PathLazy` System.Lazy cache (매 openConnection 의 RuntimeInformation + Path.Combine 반복 cost 회피) + `buildVec0WireFormat` StringBuilder 직접 wire (JsonSerializer<float32[]> reflection cost 회피, R round-trip format) + `rrfMerge` Map (immutable copy O(log N)) → Dictionary (O(1) mutable). (B-S7-4) 신규 `AdminEndpoints.fs` (~123 line, `POST /admin/collections/{id}/owner` ImportedBy stamp + `PUT /admin/collections/{id}/acl` Acl 갱신 + `Log.audit.Info` 추적, body schema = OwnerBody / AclBody `[<CLIMutable>]` 비-private record, single trust pool — 별 admin-only ACL 분리는 backlog). 신규 `AdminEndpointsTests.fs` (~95 line, 3 Fact = owner 갱신 / acl 갱신 / 잘못된 id 404). + EventsSseTests.fs A2 `open Ds2.LightHouse.Protocol` 누락 정합. 자가 검열 sub-agent 위임 — Critical 0 / Major 3 (M-1 jsonOpts singleton + M-2 writeJson options 전달 즉시 적용, M-3 acl users 정규화 backlog) / Minor 5. +336/-21 line / 11 files / +3 IT Fact = **+711 누적**.
- **`a5c82e0` s6-r73 external review hotfix — SSE race + mTLS IT wrongCert lifetime** — (R5-M1, production-blocking) `EventsEndpoint.handle` 의 keepalive task + reader loop 동시 `ctx.Response.Body.WriteAsync` race (ASP.NET Core chunk encoding 손상 → client SSE parse 실패 회귀) → per-context `SemaphoreSlim writeLock` + `tryWriteEventSerialized` helper (3 caller: firstWrite / keepalive / reader 모두 lock 통과). (N-2, IT 의미 회복) `MtlsRequiredFixture` 의 `CreateMtlsClientWithWrongCert` 안 `use wrongCert` 가 method return 시점 Dispose → handler 가 disposed cert handle 보유 → handshake reject 이유가 "disposed cert" wash-out → mutable field `wrongCert : X509Certificate2` + lazy 생성 + DisposeAsync 회수 (fixture-managed lifetime). +40/-5 line / 2 files / 회귀 0.

**누적 사용자 turn 전체 통계**: 3 commit (s6-r71/r72/r73) / +751/-274 line / +3 Fact / 회귀 0.

**이전 turn (참고 — 직전 transfer 박제)**:

**s6-r70 누적 (이전 turn, 2026-05-20)**:
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

**새 세션 진입 prompt (--transfer 박제 SSOT, s6-r88~r90 + doc-r9 종결 후, 2026-05-21)**:

```
@done-lighthouse-next-session.md @done-lighthouse-kb-server.md @done-lighthouse-kb-index.md 기준.

[직전 turn 사용자 흐름 — "Major 검증 fix 묶음 (B1/B5/B6/B7/B9~B16/B18/B21) / B2 + R8-M5 / N-M1/N-M5 단위 fact / R12 4건 진행. 1 PR 로 끝내보고 부족하면 auto commit 3회 허용" → PR 1 small fix 묶음 → PR 2 B13+B14 → PR 3 B18+N-M5 → PR 4 doc-r9]
- c3edb53 s6-r88 Major fix 묶음 — B5 mtime tolerance + B7 Registry validate + B9 ETag RFC 7232 + B10 finalize uploadId leak + B11 StagingSweep effective mtime + B12 SessionSweep race + B16 lifecycle Unbounded + B21 PSK byte cache. 반론: B15 (IT race 원인 불명 별 turn) / B21 SseReconnectBackoff cap (기존 fact 회귀 risk).
- 9031800 s6-r89 B13 admin atomic race + B14 ACL revoke event — Registry.updateByIdAsync helper + ICollectionLifecycleNotifier.OnAclChanged + SessionRegistry phasedKbDispose. AdminEndpoints handleAcl/map signature breaking.
- b8a77b2 s6-r90 B18 closure 회피 + N-M5 cfg DRY — AttachmentIngestService apiKey/model 매 호출 재조회 + ServiceConfigBuilder.defaultConfig helper.

[그 이전 turn 누적]
- s6-r83 ~ s6-r86 + doc-r8: 15-reviewer 종합 review 처리 (보안 critical A1/A2/A8/A9 + K4 마무리 + Major 검증 fix + B19 doc sweep).
- s6-r79 ~ s6-r82 + doc-r7: C-15 EndpointHelpers SSOT + B-S7-4 admin-only ACL + external review Minor + B2 footnotes/endnotes 별 fact.
- s6-r76 ~ s6-r78 + doc-r6: external review Major 5 + A2 m3 Middleware Protocol routing + B2 OoxmlExtractor comments.
- s6-r71~r75 + doc-r5: K4 Protocol SSOT + B PR + external review hotfix + B-S7-5 phase 4 + N-1 CertValidator.
- s6-r66~r70 + doc-r3/r4: D-S7-4 multi-tenant + D-S7-5 phase 3 + B5 phase 3/4 + 15-reviewer Critical 16 hotfix.
- s6-r53~r65: markdown view + C6 + D-S7-5 phase 2 + B5 phase 2 + D-S7-1 mTLS.
- Phase 4 P4-A~P4-C: sqlite-vec + IEmbeddingProvider + Searcher hybrid + OllamaSharp + IT round-trip.

누적 746 Fact (lib 180 / service 179 / IT 55 / Promaker 332). 회귀 0. branch = light-house (local-only).
paired-release ps1 통과 박제 (IndexerVersion 2.1.0 ∈ [1.0.0, 2.99.99]).
외부 --review 전체 종결 (L-Maj-1/3/4/5/6/10 + ⑬⑭⑮⑰⑱⑲⑳).
external review Major 5 + Minor + 15-reviewer 종합 review 핵심 + Major 검증 fix 12+건 종결.
K4 Protocol SSOT 완료 (wire 상수 + N-1 CertValidator + A2 m3 + C-15 EndpointHelpers + B20 alias).

잔여 작업 (s6-r90 + doc-r9 종결 후, 다음 turn 박제):

A. 본 turn 반론 / defer (별 phase scope):
  - **B1 dispatchEmbeddings partial state** — Documents.EmbeddingStatus column + schema bump + paired-release impact.
  - **B6 OriginalPath UNIQUE + 정규화** — UNIQUE constraint migration (Windows case-insensitive drift).
  - **B2 lib API async transformation** — lib + 모든 caller chain 수십 파일.
  - **R8-M5 UI Task.Run wrap** — Promaker LlmConfig.Save caller 8건.
  - **B15 caption-progress owner validate** — T2/T3 IT fixture 신설 후 진입 (IT race 원인 디버그 의무).
  - **R12 4건** — 사용자 명세 input 필요.

B. 본 turn 미진행 잔여 (defer 별 turn):
  - **B21 잔여**: KbManager SSE swallow (Promaker C# large) / KnowledgeBase.Dispose record vs interface ambiguity / UploadsResumableTests 동시 PATCH race (test deterministic) / SseReconnectBackoff cap saturate.
  - **N-M1 단위 fact** (mock store + cert thumbprint hash).
  - **MimeTypes.JsonUtf8 literal** (PR 1 s6-r79 sub-agent M-1 backlog).
  - **IT level admin-only round-trip fact** (s6-r80 sub-agent 잔여 우려).
  - **Promaker client OnAclChanged 인지 IT round-trip** (s6-r89 B14 후속).
  - **A1 escape attack vector unit fact** (cert generation 의무).
  - **A3 audit warn helper** (known field key set 비교).
  - **A10 MtlsRoundTrip production validate unit fact**.
  - **A5 Runtime engine v10 spec 후퇴** (사용자 confirm 의무).
  - **A7 Session lifecycle TOCTOU single lock SSOT**.

C. 외부 / 운영:
  - **E1 `/dist` 실행** — 사용자 직접 호출 의무.
  - E3 server.md §7.7 Minor outliers ~13건.

우선순위 (a) A5 Runtime spec 사용자 confirm (1순위 — 의도된 변경 / 회귀 분기 결정)
       (b) A7 Session lifecycle single lock SSOT (보안 critical 잔여)
       (c) B1 dispatchEmbeddings partial state (schema bump / paired-release 묶음 phase)
       (d) B6 OriginalPath UNIQUE (migration 묶음 phase)
       (e) B2 lib async transformation (별 phase, 수십 파일)
       (f) R8-M5 UI Task.Run wrap (Promaker UI 별 phase)
       (g) R12 4건 (사용자 명세 input)
       (h) B21 잔여 + N-M1/MimeTypes.JsonUtf8 등 small 묶음
       (i) E1 /dist 실행 (사용자 직접 호출 의무)
       — 선택 부탁드립니다.
```
- bfd6a9a s6-r83 review PR 1 보안 critical — A1 mTLS CN escape (BCL GetNameInfo 교체) + A2 Execution diag (log4net) + A8 Sqlite leak (try-reraise) + A9 fsproj 순서.
- 7554f79 s6-r84 review PR 2 — A3 반론 (UnmappedMemberHandling 박제는 운영자 친화 _*_note field 충돌, audit warn 별 박제 권장) + B20 alias 정합 + A4 부분 (config.json.template adminUsers).
- 2c5b891 s6-r85 review PR 3 — B3 OoxmlExtractor delegation refactor + B4 vec0 wire SSOT 통합 + B17 install-service.ps1 PSK byte buffer + B2 반론 (lib async transformation = 별 phase).
- 98749fb s6-r86 review PR 4 — B8 symlink reject + A6 reader loop disconnectSignal 추가 + A7 defer (single lock SSOT 별 turn).

[그 이전 turn 누적]
- s6-r79 ~ s6-r82 + doc-r7: C-15 EndpointHelpers SSOT + B-S7-4 admin-only ACL + B PR M-3 normalize + external review Minor (N-M1~M4 + R6-M3 + R5-M2 doc) + B2 footnotes/endnotes 별 fact.
- s6-r76 ~ s6-r78 + doc-r6: external review backlog Major 5 + A2 m3 Middleware Protocol routing + B2 OoxmlExtractor comments 박제.
- s6-r71~r75 + doc-r5: K4 Protocol SSOT + B PR 보안/perf/admin + external review hotfix + B-S7-5 phase 4 swap chunked + N-1 CertValidator.
- s6-r66~r70 + doc-r3/r4: D-S7-4 multi-tenant + D-S7-5 phase 3 chunked + B5 phase 3/4 cert + mTLS IT + 15-reviewer Critical 16 hotfix.
- s6-r53~r65: markdown view + C6 citation UI + D-S7-5 phase 2 + B5 phase 2 + D-S7-1 mTLS + 보안 sweep.
- Phase 4 P4-A~P4-C: sqlite-vec + IEmbeddingProvider + Indexer + Searcher hybrid + OllamaSharp adapter + IT round-trip.

누적 746 Fact (lib 180 / service 179 / IT 55 / Promaker 332). 회귀 0. branch = light-house (local-only).
paired-release ps1 통과 박제 (IndexerVersion 2.1.0 ∈ [1.0.0, 2.99.99]).
외부 --review 전체 종결 (L-Maj-1/3/4/5/6/10 + ⑬⑭⑮⑰⑱⑲⑳).
external review backlog Major 5 + Minor 핵심 + 15-reviewer 종합 review 핵심 종결 (A1/A2/A8/A9 보안 + A4/A6 부분 + B3/B4/B8/B17/B20 refactor / SSOT). 잔여 보안 Critical = A5 (Runtime spec 사용자 confirm) / A7 (Session TOCTOU single lock).
Phase S7 본격 종결 + B-S7-4 admin-only ACL 분리 완료.
K4 Protocol SSOT = wire 상수 + N-1 CertValidator + A2 m3 + C-15 EndpointHelpers SSOT + B20 alias 정합 완료. **잔여 = MimeTypes.JsonUtf8 literal 신설** (Protocol.MimeTypes 확장).

잔여 작업 (s6-r86 + doc-r8 종결 후, 다음 turn 박제):

A. 15-reviewer 종합 review 잔여 (defer 별 turn):
  - **A5 Runtime engine v10 spec 후퇴** — light-house phase 외 추정 (ContactKind / IsInverted / SkipAction / RuntimeSemantics 제거가 light-house branch 변경 아님, main..HEAD diff 0 가능). **사용자 confirm 의무** — 의도된 spec roll-back 이면 Editor UI 비활성화 + 마이그레이션 경고 / 우발적 회귀면 평가 분기 복구.
  - **A7 Session lifecycle TOCTOU single lock SSOT** — CollectionEndpoints.deleteCollection 의 notifier.OnDeleted → Registry.removeAsync 순서 + SessionRegistry.CreateSession resolve-then-add race. scope large (lock SSOT 설계).
  - **A10 MtlsRoundTrip production validate unit fact** — IT override 가 production chain.Build 우회 → productionValidate internal 노출 + 3 분기 unit fact.
  - **A1 escape attack vector unit fact** — cert generation 의무.
  - **A3 audit warn helper** — known field key set 비교 (UnmappedMemberHandling.Disallow 의 안전한 대안).

B. Major 검증 fix 잔여 (별 turn):
  - **B1 dispatchEmbeddings partial state** — Documents.EmbeddingStatus column + Searcher partial 처리 / 재시도 path.
  - **B5 mtime fast-skip OS/FS tolerance** — tick 절사 또는 ±2초 tolerance.
  - **B6 OriginalPath UNIQUE + 정규화** — Windows case-insensitive drift 차단.
  - **B7 Registry write-path validation** — validateEntry defense-in-depth.
  - **B9 ETag substring → RFC 7232** — Contains 비교 → multi-tag If-None-Match 정합.
  - **B10 finalize uploadId staging leak** — exception 분기 4종 uploadId staging cleanup.
  - **B11 StagingSweep mtime 의존** — meta.json / partial mtime 사용 (Linux ext4 silent strangle 차단).
  - **B12 SessionSweep race** — filter ↔ TryRemove lock.
  - **B13 admin read-modify-write race** — CAS or single-lock helper.
  - **B14 session/ACL revoke lifecycle hook** — OnAclRevoked event.
  - **B15 caption-progress owner validate** — cross-tenant DoS 차단.
  - **B16 R5-M2 commit message 정정** — lifecycle DropOldest unbounded 화 검토.
  - **B18 AttachmentTools _vlmHttp + API key closure** — Promaker 측 평문 API key 매 호출 재조회.
  - **B21 잔여 일괄** — PSK byte cache (timing leak) / KbManager SSE swallow / SseReconnectBackoff _attempt cap saturate / KnowledgeBase.Dispose record vs interface ambiguity / Work-level SkipAction silent drop / UploadsResumableTests 동시 PATCH race.
  - **B2 lib API async transformation** — Searcher/Indexer/KnowledgeBase facade + 모든 caller chain.
  - **R8-M5 UI Task.Run wrap** — LlmConfig.Save caller 8건.

C. K4 마무리 / Minor:
  - **MimeTypes.JsonUtf8 literal 신설** (PR 1 s6-r79 sub-agent M-1 backlog).
  - **N-M1 단위 fact** (mock store + cert thumbprint hash).
  - **N-M5 cfg DRY** (ServiceConfig builder helper).
  - **IT level admin-only round-trip fact** (s6-r80 PR 2 sub-agent 잔여 우려).

D. R12 잔여 4건 (사용자 명세 input 필요).

E. 외부 / 운영:
  - **E1 `/dist` 실행** — paired-release ps1 통과 박제 확인 완료. **사용자 직접 호출 의무** (`make dist` 또는 `/dist` skill).
  - E3 server.md §7.7 Minor outliers ~13건.

우선순위 (a) A5 Runtime spec 사용자 confirm (1순위 — 의도된 변경 / 회귀 분기 결정)
       (b) A7 Session lifecycle single lock SSOT (보안 critical 잔여)
       (c) MimeTypes.JsonUtf8 literal 신설 (K4 마무리, smallest scope)
       (d) Major 검증 fix 묶음 (B1/B6/B9/B10/B11 보안 / 성능 정합)
       (e) E1 /dist 실행 (사용자 직접 호출 의무)
       — 선택 부탁드립니다.
```
- 9f31f68 s6-r79 C-15 EndpointHelpers SSOT 추출 — K4 잔여 helper 통합. 신규 EndpointHelpers.fs (5 SSOT) + 6 endpoint alias 박제 + UploadsEndpoint metaJsonOpts rename. sub-agent 검열 — Critical 0 / Major 1 (M-1 MimeTypes SSOT 분리 backlog) / Minor 3 (거부).
- eb1d6d0 s6-r80 보안 묶음 — B-S7-4 admin-only ACL + B PR M-3 acl users 정규화. ServiceConfig.AdminUsers (nullable backward-compat) + EndpointHelpers.requireAdmin + AdminEndpoints.map signature breaking + handleOwner/handleAcl 분리 + 403 audit. normalizeAclUsers helper (null safe + whitespace trim + dedup). 신규 12 fact. sub-agent 검열 — Critical/Major 0 / Minor 3 (거부).
- 3e206e2 s6-r81 external review backlog Minor — CertValidator.TryFind 다중 매칭 fix (가장 늦은 NotAfter) + N-M2~M4 docstring + Config.validateHttpsOnly IPv6 link-local zone reject + R5-M2 client invariant doc. 신규 1 fact (R6-M3).
- 4bd7ae1 s6-r82 B2 footnotes/endnotes 별 fact — 신규 fixture 2 + 신규 fact 2. extractImagesFromOpenXmlPart helper path 분기 누락 0 검증.

[그 이전 turn 누적]
- s6-r76 ~ s6-r78 + doc-r6: external review backlog Major 5 (R2-M1 idx + R8-M2 jitter + R6-M1 null + R8-M5 file lock + R5-M2 EventBus channel 분리) + A2 m3 Middleware Protocol routing + B2 OoxmlExtractor comments 박제.
- s6-r71~r75 + doc-r5: K4 Protocol SSOT + B PR 보안/perf/admin + external review hotfix + B-S7-5 phase 4 swap chunked + B-R14 MultiTenant T2/T3 IT + N-1 CertValidator.Normalize SSOT.
- s6-r66~r70 + doc-r3/r4: D-S7-4 multi-tenant + D-S7-5 phase 3 chunked auto + B5 phase 3/4 cert + mTLS IT + 15-reviewer Critical 16 hotfix.
- s6-r53~r65: markdown view cherry-pick + C6 citation UI + D-S7-5 phase 2 + B5 phase 2 + light-house-meta-in-kb squash + D-S7-1 mTLS + 보안 sweep.
- Phase 4 P4-A~P4-C: sqlite-vec + IEmbeddingProvider + Indexer + Searcher hybrid + OllamaSharp adapter + Promaker LlmConfig.Embedding + server-side embedder + IT round-trip.

누적 746 Fact (lib 180 / service 179 / IT 55 / Promaker 332). 회귀 0. branch = light-house (local-only).
paired-release ps1 통과 박제 (IndexerVersion 2.1.0 ∈ [1.0.0, 2.99.99]).
외부 --review 전체 종결 (L-Maj-1/3/4/5/6/10 + ⑬⑭⑮⑰⑱⑲⑳).
external review backlog Major 5 + Minor 핵심 (N-M1~M4 + R6-M3 + R5-M2 doc) 종결. 잔여 Minor 일부 (N-M5 cfg DRY / R8-M5 UI Task.Run wrap) 별 turn.
Phase S7 본격 종결 + B-S7-4 admin-only ACL 분리 완료.
K4 Protocol SSOT = wire 상수 + N-1 CertValidator + A2 m3 + C-15 EndpointHelpers SSOT 박제 완료. **잔여 = MimeTypes.JsonUtf8 literal 신설** (sub-agent M-1 backlog — Protocol.MimeTypes 확장).

잔여 작업 (s6-r82 + doc-r7 종결 후, 다음 turn 박제):

A. K4 Protocol SSOT 마무리 (small):
  - **MimeTypes.JsonUtf8 literal 신설** — `Ds2.LightHouse.Protocol.MimeTypes` 에 `JsonUtf8 = "application/json; charset=utf-8"` 추가. EndpointHelpers.writeJson / writeJsonOpts / FileServing line 55 의 hardcoded literal 2건 routing. ~10 line refactor + 0 fact.

B. external review backlog 잔여:
  - **N-M1 CertValidator 다중 매칭 단위 fact** (medium) — mock store + cert thumbprint hash 정합 의무. 본 turn s6-r81 의 logic fix 는 in-doc 검증, 단위 fact 별 turn.
  - **N-M5 cfg DRY** (small) — MtlsRoundTripTests / ServiceFixture 의 ServiceConfig record 박제 중복. builder helper 추출.
  - **R8-M5 UI Task.Run wrap** (medium-large) — LlmConfig.Save() UI thread caller 8건 (ApplicationSettingsDialog 3 / KbManagerDialog 5 / LlmChatViewModel 2). Task.Run wrap + async/await refactor.
  - **IT level admin-only round-trip fact** (s6-r80 PR 2 sub-agent 잔여 우려) — adminUsers 박제 + non-admin caller 403 + admin caller 200 IT fact.
  - **config.json.template adminUsers 박제** (small) — install-service.ps1 박제 default `[]` + admin 안내 주석.

C. R12 잔여 4건 (사용자 명세 input 필요):
  - 사용자 명시 미박제 (vec0/RRF/embedding 외 4건). 명세 박제 의무.

D. (s6-r74 b 검열 Minor 2 잔여 / s6-r66 자가 검열 Minor 5건 등):
  - M1 fixture body 중복 (xunit IClassFixture 한계 trade-off 거부)
  - M2 setAcl helper SSOT 통합 (AdminEndpointsTests 와 의미 중복 별 turn scope)

E. 소량 cosmetic / 정책 결정:
  - (string * SearchHit) list → KeyedHit record (lib internal refactor)
  - mn6 fixture 한영 혼재 / C2 P7 facade 통합 / C3 자가 검열 미적용
  - D1 D-2-3 SSOT 정정 / D2 PrivateAssets 확장 / D3 todo git mv
  - s6-r79 sub-agent jitter alias 변수명 중복 (jsonOpts / requestJsonOpts in EventsEndpoint)

F. 외부 / 운영:
  - **E1 `/dist` 실행** — paired-release ps1 통과 박제 확인 완료. **사용자 직접 호출 의무** (`make dist` 또는 `/dist` skill)
  - E3 server.md §7.7 Minor outliers ~13건

우선순위 (a) MimeTypes.JsonUtf8 literal 신설 (K4 마무리, 가장 가볍고 본 turn 의 미해소 sub-agent finding)
       (b) IT level admin-only round-trip fact (보안 검증 강화)
       (c) N-M5 cfg DRY (cosmetic 박제, builder helper refactor)
       (d) R8-M5 UI Task.Run wrap (medium-large Promaker UI refactor — 사용자 input 필요 시점)
       (e) E1 /dist 실행 (paired-release 정합, 사용자 직접 호출 의무)
       — 선택 부탁드립니다.
```
- 3c4b70c s6-r76 external review backlog Major 4 — R2-M1 idx + R8-M2 jitter + R6-M1 null guard + R8-M5 file lock — (R2-M1) SqliteStore.schemaSql IX_Documents_OriginalPath 추가 (findDocumentByPath O(N²) 차단). (R8-M2) SseReconnectBackoff ±20% jitter + test deterministic Func<double> 주입. (R6-M1) Config.validateMtls cfg.Mtls null guard (validateMultiTenant 정합). (R8-M5) LlmConfig.Save() cross-process file lock + WithFileLock helper 추출 + EffectiveConfigPath 박제. 신규 7 fact. 자가 검열 inline.
- ec9112c s6-r77 R5-M2 EventBus lifecycle / progress channel 분리 — per-subscriber 2 channel (lifecycle 32 + progress 64). Subscribe return = (Guid * lifecycleReader * progressReader) breaking. Publish 가 isLifecycle 분기. EventsEndpoint.handle reader loop Task.WhenAny + lifecycle 먼저 drain. reviewer M1 patch (close 판정 race window 보호). 신규 3 fact. 자가 검열 sub-agent — Critical 0 / Major 1 (M1 적용) / Minor 3 (거부).
- 6dffc5e s6-r78 A2 m3 Middleware Protocol routing + B2 OoxmlExtractor comments 박제 — Middleware.fs UserIdentityHeader = HeaderNames.UserIdentity alias. OoxmlExtractor 가 WordprocessingCommentsPart / FootnotesPart / EndnotesPart Drawing 추가 박제 (extractImagesFromOpenXmlPart 재사용, singleton scheme). 신규 1 fact (comments 대표). 자가 검열 inline.

[그 이전 turn 누적]
- s6-r71~r75 + doc-r5: K4 Protocol SSOT + B PR 보안/perf/admin + external review hotfix + B-S7-5 phase 4 swap chunked + B-R14 MultiTenant T2/T3 IT + N-1 CertValidator.Normalize SSOT.
- s6-r66~r70 + doc-r3/r4: D-S7-4 multi-tenant + D-S7-5 phase 3 chunked auto + B5 phase 3/4 cert + mTLS IT + 15-reviewer Critical 16 hotfix.
- s6-r53~r65: markdown view cherry-pick + C6 citation UI + D-S7-5 phase 2 + B5 phase 2 + light-house-meta-in-kb squash + D-S7-1 mTLS + 보안 sweep.
- Phase 4 P4-A~P4-C: sqlite-vec + IEmbeddingProvider + Indexer + Searcher hybrid + OllamaSharp adapter + Promaker LlmConfig.Embedding + server-side embedder + IT round-trip.

누적 731 Fact (lib 178 / service 166 / IT 55 / Promaker 332). 회귀 0. branch = light-house (local-only).
paired-release ps1 통과 박제 (IndexerVersion 2.1.0 ∈ [1.0.0, 2.99.99]).
외부 --review 전체 종결 (L-Maj-1/3/4/5/6/10 + ⑬⑭⑮⑰⑱⑲⑳).
external review backlog Major 5 종결 (R2-M1 + R8-M2 + R6-M1 + R8-M5 + R5-M2).
Phase S7 본격 종결 (D-S7-1 mTLS / D-S7-2 SSE / D-S7-3 multi-service / D-S7-4 multi-tenant + admin endpoint / D-S7-5 resumable phase 1~4 swap chunked).
K4 Protocol SSOT = wire 상수 + N-1 CertValidator + A2 m3 Middleware Protocol routing 박제 완료. **잔여 = C-15 EndpointHelpers 추출 (writeJson / writeError / userIdentityOf / jsonOpts 4 helper × 5 endpoint, ~300 line refactor scope)**.

잔여 작업 (s6-r78 + doc-r6 종결 후, 다음 turn 박제):

A. C-15 EndpointHelpers SSOT 추출 (~300 line, K4 잔여 핵심):
  - 신규 `Solutions/Tools/Ds2.LightHouseService/EndpointHelpers.fs` — writeJson / writeError / userIdentityOf / jsonOpts (PropertyNameCaseInsensitive=true default + override)
  - 기존 5 endpoint (CollectionEndpoints / SessionEndpoints / EventsEndpoint / FileServing / UploadsEndpoint / AdminEndpoints) 의 private helper 폐기 + 신 module routing
  - jsonOpts caller 별 다양성 (PropertyNameCaseInsensitive vs WriteIndented) 정합 의무 — 사용자 input 후 진입.

B. external review backlog Minor 7 잔여 (별 turn 소형 묶음):
  - C-14 INotifyPropertyChanged (DataGrid cell 단위 real-time validation)
  - N-M1~N-M6 (CertValidator + MtlsRoundTrip 정밀화 — TryFind 다중 매칭 / store dispose 후 cert 반환 / IsHandshakeReject swallow / DisposeAsync swallow / cfg DRY / chain.Build IT 우회)
  - R6-M3 IPv6 listenUrl (일부 해소 — runtime OK, link-local zone 만 거부)
  - **s6-r77 R5-M2 reviewer 잔여 우려** — lifecycle 우선 도착 invariant 박제 (client documentation 의무, 회귀 0 확인됨)
  - **s6-r76 R8-M5 잔여 우려** — LlmConfig.Save() UI thread Task.Run wrap (caller 8건 모두 UI thread)

C. B PR + 자가 검열 미적용 backlog:
  - **B3 image-only paragraph 분기 분리** — s6-r21 기존 박제로 충분 (본 turn 확인) — 별 backlog 의무 0
  - **B2 footnotes/endnotes 별 fact** — comments 대표 fact 통과로 흡수, footnotes/endnotes 는 동일 helper path. 별 fact 별 turn 권장 (분기 검증 의도)
  - **R12 잔여 4건** — 사용자 명시 미박제 (vec0/RRF/embedding 외 4건). 명세 박제 의무 (사용자 input 필요).
  - **B PR M-3 acl users element 정규화** — defense-in-depth.
  - **B-S7-4 admin-only ACL 분리** — config.adminUsers SSOT + requireAdmin helper.
  - **(s6-r74 b 검열 Minor 2 잔여)** — M1 fixture body 중복 / M2 setAcl helper SSOT 통합.

D. 소량 cosmetic / 정책 결정:
  - (string * SearchHit) list → KeyedHit record (lib internal refactor)
  - mn6 fixture 한영 혼재 / C2 P7 facade 통합 / C3 자가 검열 미적용
  - s6-r66 자가 검열 Minor 5건 backlog
  - D1 D-2-3 SSOT 정정 / D2 PrivateAssets 확장 / D3 todo git mv

E. 외부 / 운영:
  - **E1 `/dist` 실행** — paired-release ps1 통과 박제 확인 완료. **사용자 직접 호출 의무** (`make dist` 또는 `/dist` skill)
  - E3 server.md §7.7 Minor outliers ~13건

우선순위 (a) C-15 EndpointHelpers SSOT 추출 (K4 잔여 핵심 — large refactor ~300 line, scope confirm 후 진입)
       (b) external review backlog Minor 7 묶음 (small/medium, 본 turn 의 Major 5 종결 후속)
       (c) B PR 잔여 + 자가 검열 미적용 backlog (R12 4건 명세 박제 사용자 input + acl 정규화 + admin ACL 분리)
       (d) E1 /dist 실행 (paired-release 정합, 사용자 직접 호출 의무)
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
2. `done-lighthouse-kb-server.md` §0 — 모드 박제 + D-id 결정 표 (D-S7-1~5 / D-2-1~7 포함)
3. `done-lighthouse-kb-server.md` §7.1 — commit chain 표, 가장 최근 row (s6-r32 → s6-r0) 부터 역순으로 5~10개 정독
4. `done-lighthouse-kb-server.md` §3.16 multi-service routing — D-S7-3a/b/c 의 sub-section 3건 (§3.16.1~8). 새 세션에서 multi-service mental model 파악 의무.
5. `done-lighthouse-kb-server.md` §7.4 — backlog 표 (처리 완료 marker + 잔여 D-S7-1/2c/4/5)
6. `done-lighthouse-kb-index.md` §0 — parent rev 박제 + 사용자 동의 결정
7. 필요 시 `git log --oneline -30` + `git show <hash>` 로 commit 상세

## 4. 남은 backlog (우선순위 순)

### A. 단독 turn 진입 권장 (큰 phase)

| # | 항목 | 위치 | 진입 마커 |
|---|---|---|---|
| A1 | **Phase S7 — mTLS / multi-tenant / resumable upload** | server.md §7.4 "P4 Phase S7" | D-S7-1~5 사전 박제 완료. ~~D-S7-2a/b/c (SSE 시리즈 전체)~~ → s6-r27/r28/r32 완료. ~~D-S7-3a/b/c (multi-service routing 전체)~~ → s6-r29/r30/r31 완료 (§3.16). 잔여 = **D-S7-1** (mTLS) / **D-S7-4** (T2/T3) / **D-S7-5** (resumable upload). |
| A2 | **K4 Protocol SSOT 통합** | server.md §7.7 K4 박제 | `Solutions/Core/Ds2.LightHouse.Protocol` 신규 project — wire 상수 + MetaJson schema SSOT 통합. LightHouseClient C# / F# 이중 구현 → 단일 SSOT 의존. Phase S7 묶음. |
| A3 | **보안 sweep 1턴 (K6 + M9/M10/M11/M12)** | server.md §7.7 K6 + Major outlier | registry.json tampering 검증 + PSK in-memory lifetime + %PROGRAMDATA% ACL + DoS guards (topK upper bound / query length). admin 권한 위협 모델 한정 (defense-in-depth). |
| A4 | **Phase 2 후속 (Phase 3 OCR / Phase 4 Embedding)** | parent §3.15 / `done-lighthouse-kb-index.md` Phase 3 / Phase 4 | Phase 2 eager VLM caption 완료 후 OCR (Tesseract.NET + 한글) 또는 embedding (sqlite-vec) 진입. 별 PR. |

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
- **test** = `Solutions/Tests/Ds2.LightHouse.Tests` (lib, 177) / `Solutions/Tests/Ds2.LightHouseService.Tests` (service, 161) / `Solutions/Tests/Ds2.LightHouseService.IntegrationTests` (e2e + cli + mTLS + admin, 46) / `Solutions/Tests/Promaker.Tests` (Promaker, 327) = **누적 711 Fact**
- **Protocol** = `Solutions/Core/Ds2.LightHouse.Protocol/` (F# net9.0, dependency 0 base — s6-r71 신설) — wire 상수 + MetaJson schema + ServerEventNames 단일 SSOT
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
 M Apps/Promaker/Docs/done-lighthouse-kb-server.md
 M Apps/Promaker/Docs/done-lighthouse-next-session.md
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
