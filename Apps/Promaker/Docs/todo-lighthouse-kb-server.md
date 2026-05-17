# Ds2.LightHouse — KB 서버 (central Windows Service) 도입

세션 이어받기용 TODO. `todo-lighthouse-kb-index.md` (이하 *parent*) 의 r4 위에 얹는 incremental design.
실제 진입은 parent 의 Phase 1 (in-process MVP) 완료 후. 본 문서는 그 후속 phase 의 설계 박제.

| rev | 일자 | 주요 변경 |
|---|---|---|
| s0 | 2026-05-17 | 초안 — plan 모드 논의 결과 박제. 코드 변경 0. parent r4 의 결정 일부 회귀 (사본 정책 / MCP 호스트 위치 / search 경로 등). |
| s0-r | 2026-05-17 | --inspect-diff 5 reviewer 결과 반영 (16건): (1) D-id / 결정 enum 정의표 §0 신설, (2) §3.1 sub-section 분리 (책임/lib 양분/MCP host 2개), (3) §3.2 통신 흐름 다이어그램 보강, (4) §3.7 mTLS 단원 참조 정정 (S4→S7), (5) §3.8 `unindexableIds` 처리 명시, (6) §3.13 ↔ Phase S5 중복 분리 (사유 vs 체크리스트), (7) §3.14 가 parent ↔ service 회귀 SSOT 임을 명시, (8) parent 패턴 정렬을 위해 단원 번호 환원 (이전 §5/§6/§7/§8 → §5/§6/§7), (9) Phase S1~S7 별 DoD 1줄 추가, (10) `LlmConfig.KbCollections` schema migration 정책을 §4.3 미확정에 추가. |
| s0-r2 | 2026-05-17 | parent r5 의 **대안 B 채택** 반영: (1) §4.1 Phase S0 에 "parent §4.5 / §4.1 첫 task / §4.6 / §4.8 일부 정상 skip 확인" 추가, (2) §4.3 schema migration default 를 "(c) parent §4.5 skip 이라 migration 불필요" 로 갱신, (3) §0 의 선행 의존 항목에 parent r5 결정 12 박제. parent Phase 1 산출물의 60%+ throwaway 문제 해소. |
| s2-r0 | 2026-05-17 | **Phase S2 진입 commit 직전 — 단일 commit 예정 (사용자 옵션 C 선택)**. (i) 신규 6 F# 파일 (`Solutions/Tools/Ds2.LightHouseService/`): Registry / MetaJson / ZipImport / CollectionLifecycle / StagingSweep / CollectionEndpoints ≈ 950 line + Endpoints/Program/Middleware/fsproj 수정. (ii) 신규 4 test 파일 + AuthMiddleware m2 추가 = 37 Fact (S1 24 + S2 35 + 자가 검열 4 = 총 63/63 통과). (iii) **자가 검열 (sub-agent general-purpose)** Critical 3 / Major 7 / Minor 6 → **즉시 차단 5건 적용**: **(C1)** DOS device path (`\\?\` / `\\.\`) 명시 거부 + regression test / **(C2)** symlink 분기 통합 — 디렉토리/파일 entry 모두 검사 + Linux symlink test 1 Fact / **(M6)** FileNotFoundException / InvalidDataException → 400 분기 (zip 결함 client UX) — POST /collections + POST payload 둘 다 / **(m1)** blob regex `IgnoreCase` 제거 — §3.3 SSOT lowercase 정합 + 대문자 sha256 reject test / **(m2)** X-User-Identity `StringValues.Count = 1` 검증 — multi-value (`kwak, attacker@evil.com`) 거부 + AuthMiddleware test 1 Fact. (iv) **commit 후 follow-up 박제 (잔여)**: **(C3)** §3.3.1 SSOT 의 `storageRelPath` trailing backslash 정책 박제 (있음 / 없음 둘 중 하나로 fix) — 현 코드는 `Collections\\<guid>-<title>` (trailing X) / **(M2)** `probeIndexerVersion` 진단 DU (FileMissing / OpenFailure / MetaKeyMissing / Found) — Phase S3 SessionRegistry ATTACH 와의 상호작용 같이 / **(M3)** service single-instance Mutex — Phase S6 install-service.ps1 또는 Phase S1 보강 / **(M5)** delete 분기 notifier ordering — Phase S3 SessionRegistry impl 시 (mark-for-delete → detach → remove → purge 두 단계 모델 검토) / **(M7)** zip layout 정합 (`source/` 디렉토리 존재 / `meta.fileCount` ↔ 실 파일 일치) — Phase S5 IntegrationTests / **(m3)** JsonSerializerOptions module-level cache — perf, sourge-generated context 도입 시 / **(m5)** `errorReason: null` vs 키 부재 정책 SSOT 박제. (v) §4.3 미확정 표 갱신 필요 (commit 후 박제): "**ATTACH connection lazy open vs eager**" (S3 진입) / "**Promaker in-process search fallback**" (S5 진입) 잔류. |
| s3-r1 | 2026-05-18 | **`--review` (외부 7 reviewer 메타 리뷰) 처리 — Critical 2 + Major 6 + Minor 1 즉시 적용**. 검토 범위 = `00b72eb..HEAD` (Phase 1 §4.4 ~ Phase S2 박제). 본 turn 의 Phase S3 working tree 도 일부 영향. **즉시 적용 (8건)**: **(IC-1)** `ZipImport.swapCollectionPayload` rollback 안전화 — silent swallow → `<target>.broken-<ts>` 격리 rename + ExceptionDispatchInfo 로 원래 ex 보존 + fatal log + audit sentinel (3/7 reviewer 합의 G C-G2 / R2 m1 / R4 M1). **(IC-2)** title sanitize SSOT 일원화 — `CollectionEndpoints.postCollections` 가 `parseMultipart` 직후 `ZipImport.sanitizeTitle` 1회 산출 → `meta.Title` overwrite + storageRelPath + audit log 일관. swap path 는 별도 처리: `existing.DisplayName` 을 dir/meta SSOT 유지, 새 title 은 `titleHint` audit 박제 (title rename 은 별도 endpoint = Phase S7 옵션). **(IM-3)** `Endpoints.getCollectionsStub` dead code 제거 (Phase S2 진입 후 `CollectionEndpoints.getCollectionsList` 가 SSOT). **(IM-6 부분)** lib facade 강화 — `Ds2.LightHouse.KnowledgeBase` 에 `isIndexed` / `probeIndexerVersion` / `MaxAttachedDbs` (Literal) 신설 → service 의 `ZipImport.probeIndexerVersion` 가 lib delegation 으로 변경 + `AttachmentResolver.isIndexed` / `SessionRegistry` 의 `SqliteStore` 직접 참조 우회 제거. *PackageReference 자체는 복원* (lib 의 `PrivateAssets=all` 정책상 runtime native asset 이 caller output 에 copy 안 됨 → service test 가 lib 의 SqliteConnection load fail). 코드 측 SqliteStore 직접 호출은 0건이지만 runtime 보강. **(IM-8)** parent §4.8b TextExtractor BOM strip 회귀 test 1 Fact 추가 — F3 fix 보호 + invisible char literal `'﻿'` escape 사용 (R2/R4 m1). **(IM-2 부분)** `CollectionEndpoints` / `SessionEndpoints` 의 hot-path `jsonOptions ()` → module-level singleton (`jsonResponseOpts`). 다른 모듈 (`Config` / `Registry` / `MetaJson` — IO 빈도 낮음) 은 보류. **(IM-10)** `install-service.ps1` 3건 — (a) `Read-SecretAsPlain` helper + `ZeroFreeBSTR` finally 의무 (BSTR unmanaged memory zero), (b) `Protect-DpapiLocalMachine` finally 의 `[Array]::Clear` (평문 byte buffer zero), (c) `Set-Content -Encoding UTF8` (BOM 포함) → `[System.IO.File]::WriteAllText` + `UTF8Encoding(false)` (BOM 없음, 외부 reader 호환), (d) default listenUrl `0.0.0.0:8443` → `127.0.0.1:8443` (의도치 않은 외부 노출 차단). **(IM-11)** audit log 보안 강화 — `Logging.fs` 에 `tokenFingerprint` (SHA-256 8hex) + `sanitizeForLog` (CR/LF/Tab → space) helper. `SessionRegistry` 의 session create/delete/sweep + `SessionAuth`/`AuthMiddleware` 의 path/user 박제 모두 helper 통과. R6 M2/M4 의 log injection + token capability 노출 차단. **보류 (Phase 2 / S3 follow-up / backlog)**: **(IM-1)** endpoint try/with 5-way 헬퍼 압축 (refactoring 부담) / **(IM-4)** `MetaJson.load` schemaVersion 단순 `<>` vs 주석의 forward-compat 주장 — 주석 정정 backlog / **(IM-5)** TLS cert pwd / PSK 평문 string lifetime + 매 요청 UTF-8 alloc → byte[] 캐시 (복잡도 높음, 별 PR) / **(IM-7)** CollectionEndpoints unit test 0건 — Phase S5 WebApplicationFactory IntegrationTests 영역 / **(IM-9)** StagingSweep LastWriteTimeUtc 의 진행 중 upload stale 분류 위험 (Phase S2 의 multipart 가 in-memory + 추출 sub-second 라 위험 낮음) — `.in-progress` sentinel backlog / **Minor 18건** (withConnection 헬퍼 / fixture SSOT / Assert.Single 일괄 / EndpointUtil 모듈 / forEachSessionWithCollection / reject401 helper / Searcher dead arg kbIdx / Kestrel MultipartBodyLengthLimit / compareVersion pre-release / Registry.upsertAsync O(n²) / Indexer.ingestFile transaction wrap / SqliteStore CreateCommand 재할당 / enumerateFiles 후행 필터 / extractAll 단일 entry bomb 가드 / SweepIdle expired ↔ TryRemove race / ConfigTests temp config leak / Storage.initialize probe cleanup / rollback caller doc) — Phase 2 일괄 정리 후보. **기각 (검증 후)**: G C-G1 (`postCollections` leftover staging — stagingId = collectionId guid 라 충돌 사실상 0, IC-1 의 swap 시나리오로 covered) / G m-G4 (zip backslash middle 정합, 주석 수정만) / R2 M2 (BOM literal 가독성, IM-8 의 escape 사용으로 흡수). **Outlier**: C1-r2 (Indexer ClearPool connection string 동일성) — SqliteStore.openConnection spot-check 결과 동일 path 의 same conn string 사용 정합, Phase 2 보류 / C2-r2 (SessionRegistry lock-iteration deadlock) — 본 turn 의 Phase S3 자가 검열에서 이미 정합 분석 완료 (단방향 lock, deadlock 0). **결과 검증**: `dotnet build` 0 경고/0 오류, `dotnet test LightHouseService.Tests` **92/92** (S1 24 + S2 41 + S3 27 = sanitizeTitle idempotency 1 + unicode bidi 1 추가), `dotnet test LightHouse.Tests` **100/100** (BOM strip 회귀 +1). 이전 90/90 (Phase S3 직후) → 92/92 (review 후) + parent 99 → 100. |
| s3-r0 | 2026-05-17 | **Phase S3 진입 — code 작성 + 자가 검열 완료 (commit 대기, 90/90 Fact 통과)**. (i) **결정 4건 박제**: **(D-S3-1)** ATTACH connection = **lazy on first MCP call** (`POST /sessions` 응답 시점에는 token 발급 + registry validate + unknown/unindexable 응답만, `SqliteConnection.Open` + `ATTACH` 는 첫 `attachment_*` MCP call 시점). 사유 = panel open 후 chat 안 하고 닫는 경우 connection 절약 + ATTACH 실패 분기가 session 발급 흐름에 노이즈 안 줌 + idle TTL sweep 가 connection 만 닫고 next MCP call 에 lazy re-open 가능. **(D-S3-2 / P2)** `ModelContextProtocol.AspNetCore` 버전 = **1.2.0** (Promaker 와 동일 alignment, parent r7 박제 정합). 1.3.0 (2026-05-08 출시) 검토 = 양쪽 동시 진입 별도 PR. **(D-S3-3 / M5 follow-up)** delete 분기 notifier ordering = **두 단계 모델 채택** — `notifier.OnDeleted(id)` 호출 시 SessionRegistry 가 즉시 (1) 모든 session 의 `activeCollectionIds` 에서 해당 id 제거 + (2) ATTACH 된 connection 이 있으면 `kb.Dispose()` (다음 MCP call 시 새 activePaths 로 lazy re-open). KnowledgeBase facade 가 부분 detach 미지원 → 단순화로 connection 통째 폐기 + lazy re-open. SessionRegistry 의 mutation 은 `SemaphoreSlim(1,1)` 직렬화. **(D-S3-4 / M2 follow-up)** ATTACH 시 진단 DU = **`AttachDiagnostic` 신설** (`Ok` / `DbMissing` / `OpenFailure of string` / `SchemaMismatch of string`) — `IndexerVersionGateResult` 와 분리 (gate = upload 시점, attach = session 시점). (ii) **§4.3 미확정 표 갱신**: 위 4건 모두 (D-S3-1/2/3/4) 행 신설 + "**s3-r0 결정**" marker. (iii) **§0 D-id 표 갱신**: D-S3-1 (lazy ATTACH) / D-S3-2 (MCP 1.2.0) / D-S3-3 (delete two-stage) / D-S3-4 (AttachDiagnostic) 4행 추가. (iv) Phase S3 신규 파일: `SessionRegistry.fs` (~250 line, in-memory + SemaphoreSlim + ICollectionLifecycleNotifier 구현 swap), `SessionSweep.fs` (~80 line, BackgroundService idle TTL), `SessionEndpoints.fs` (~180 line, POST/DELETE /sessions), `AttachmentTools.fs` (~250 line, MCP 4 tool + X-LightHouse-Session middleware), `Middleware.fs` 확장 (session header 검증 + UseSessionAuth helper). (v) test 신규 4 파일: `SessionRegistryTests` (boundary 9/10/11 + 동시 race + unknown/unindexable + OnDeleted + OnPayloadSwapped 등), `AttachmentToolsTests` (X-LightHouse-Session 누락/invalid → 401, valid + tool 호출), `SessionEndpointsTests` (POST → token / DELETE → 204 / collectionIds validate). (vi) commit 없이 풀스택 진행 — 자가 검열 후 사용자 confirm 시 단일 commit 또는 sub-step 분리. (vii) **빌드 검증 결과**: `dotnet build LightHouseService` 0 경고/0 오류, `dotnet test LightHouseService.Tests` **90/90 통과** (S1 24 + S2 39 + **S3 신규 27 = SessionRegistry 14 / SessionAuth 5 / AttachmentTools 8**) + parent `LightHouse.Tests` **99/99 통과** (회귀 0). 빌드 fix: SessionSweep.fs 의 (a) `task { ... } :> Task` 단일 라인 결합 syntax → 줄바꿈 분리, (b) `task { ... base.StopAsync ... }` FS0405 (computation expression 안 base 호출 금지) → method body 직접 호출 패턴으로 변경. test fix: SessionRegistryTests 의 `OnDeleted` 시나리오에서 input-independent stub resolver 가 cross-session 검증에 부적합 → `mkRegistryFromAvailable` 신설 (input-dependent filter). (viii) **자가 검열 (sub-agent general-purpose)** Critical 4 / Major 6 / Minor 6 = 16건 — **즉시 적용 권장 = 0건** (모두 정합 확인 또는 Phase S5 e2e 책임). **잔여 우려 (Phase S5 도 또는 backlog 박제)**: **(M5)** SDK 1.2.0 `WithToolsFromAssembly()` 가 F# `[<McpServerToolType>]` 클래스 + `[<McpServerTool>]` static member 4종을 reflection 으로 정상 발견하는지 — server 단독 검증 한계, Phase S5 (LightHouseClient e2e) integration test 책임 / **(M9)** `SessionIdleTtlMinutes * 60 * 1000 / 4` 가 Int32 overflow 위험 (TTL > 35,791 분 ≈ 25일) — 운영 default 240 분 안전, Config.validate 에 상한 가드 추가 backlog / **(M10)** SessionAuth 미들웨어의 raw HTTP 401 응답이 MCP HTTP transport (SSE) 의 connect 단계 vs in-flight 호출 모두 client 의 L3 catch 와 호환되는지 — Phase S5 LightHouseClient retry 로직 검증 책임 / **(m13)** `AttachmentResolver.fromRegistry` 매 호출 file IO (`Registry.listSnapshot`) — TTL 1초 캐싱 검토 backlog / **(m15)** fsproj 의 `ModelContextProtocol` 직접 PackageReference 가 `AspNetCore` 의 transitive 와 중복 — `dotnet list package --include-transitive` 확인 후 정리 검토. **Critical / Major / Minor 분석 정합 결과 본 phase 코드 자체는 검열 통과**. |
| s1-r0 | 2026-05-17 | **Phase S1 진입 commit (`1be3ab8`) 완료**. (i) 신규 project `Solutions/Tools/Ds2.LightHouseService` (F# Windows Service host) — 7 F# 파일 (Logging/Config/Storage/Middleware/Endpoints/Program ≈ 360 line) + `log4net.config` + install/uninstall ps1 + config.json.template + paired-release Target placeholder. (ii) 신규 `Solutions/Tests/Ds2.LightHouseService.Tests` (24 Fact 100%) — Config 7 / Storage 4 / DpapiRoundTrip 4 / AuthMiddleware 9. (iii) Phase S0 점검 4 task 전부 통과 — parent commit chain (`fddfbbf` 까지) + §4.5/§4.6 SKIP 확인 + lib facade read/write 분리 가능 + 사용자 머신 `.lighthouse-kb/` 잔재 0. (iv) **§4.3 미확정 16 행 중 S1 진입 결정 6 행 확정** — P1 (lib facade 양분 — 현 `KnowledgeBase` facade 가 이미 read-only surface, `readonly:bool` 파라미터 **추가 안 함** — §4.3 default 갱신), service host = F#, TLS = self-signed PoC, PSK 회전 = mTLS Phase S7, ATTACH limit boundary = parent r4 박제 `MaxAttachedDbs = 10` 유지, paired-release manifest hash = `Ds2.LightHouse.dll` InformationalVersion 비교 (dist post-build target, 본 phase 는 placeholder). (v) **자가 검열 (sub-agent general-purpose)** Critical 3 / Major 8 / Minor 14 → **15 건 적용**: **(C1)** AuthMiddleware 시그니처 mismatch `Func<HttpContext, RequestDelegate, Task>` (ASP.NET Core 9 미존재 오버로드) → `Func<HttpContext, Func<Task>, Task>` 표준형 / **(C2)** `compareBearerSecret` 단순화 — `CryptographicOperations.FixedTimeEquals` 가 BCL contract 상 length mismatch 시 const-time false → buffer normalize 불요 / **(C3)** AuthMiddleware unit test 9 Fact 신설 (DefaultHttpContext 직접 invoke) / **(M4)** Kestrel default URL (`http://localhost:5000`) 차단 `builder.WebHost.UseUrls("")` / **(M7)** log4net.config 미존재 fail-fast / **(M8)** `/healthz` path skip — health probe 가 auth middleware 에 401 잠재 bug 차단 / **(M1)** install script `obj= LocalSystem` 명시 / **(M2/m11)** EventLog Source install 등록 + uninstall 제거 / **(M3)** log4net retention 주석 Phase S2 IHostedService 이연 / **(m10)** EventLog threshold INFO→WARN + service start Warn 명시 / **(M5/m6)** `0.0.0.0` startup Warn + hostname 명시 fail / **(m12)** Program.fs scheme check 중복 제거 (Config.validateHttpsOnly 로 통일). M3/M6 (PFX path 검증) / m1/m5/m7/m8/m13/m14 = Phase S2 / Phase 2 보류. (vi) sln 2개 + `Solutions/Directory.Packages.props` 갱신 (Microsoft.Extensions.Hosting + Hosting.WindowsServices + System.Security.Cryptography.ProtectedData 신규). |
| s0-r3 | 2026-05-17 | `--inspect 3` reviewer 결과 (parent 와 동시 작업) 의 server 측 영향분 반영. 주요 갱신: **Critical**: (CR1) §3.13/§3.14/§4.2 Phase S5 의 "환원/제외" 어휘 → "애초 미추가 — 신규 등록만", §3.14 회귀 매트릭스에 §4.6 (`5.knowledge-base.md`) row 신설 (10→11행); (CR2) **§3.3.1 `meta.json` schema SSOT 신설** (camelCase + 필드별 생성 주체 + `id` 출처); (CR3) §3.4 D3 강화 — id 발급 주체 = server (첫 POST 응답에 반환); (CR4) §3.7 **PSK DPAPI 의무화** (server: LocalMachine, client: CurrentUser, `ApiKey` → `ApiKeyEncrypted`), plain HTTP 거부, `X-User-Identity` 헤더; (CR5) **§3.9.1 신설 — registry mutation SemaphoreSlim 직렬화**; (CR6) **§3.8 session 401/403 자동 회복** (L3 신설). **Major**: §0 D-id 표에 L3/P1/P2 row 추가, α → T1 통일, §3.11 config 의 schemaVersion / logRetentionDays / auditRetentionDays / indexerVersionRange 추가, Phase S1~S5 DoD 일제 강화 (install script / paired-release / blob regex / zip body abort / 0-doc / swap 동시성 / ATTACH boundary / fileId guid 합성 / Integration test / 연결 테스트 버튼), §4.3 미확정 표 16행으로 확장 + dep column, §5 산출물 목록 7건 추가 (IntegrationTests / install scripts / KbManagerDialog 등 "최초 도입" marker), §6 주의 사항 16~20 신설 (PSK 단일 실패점 / audit / restart 회복 / registry race / paired-release). **Minor**: §0 의 T1 통일, §4.2 Phase S7 의 T2/T3 명명, §3.10 storage layout 의 `Audit\` 디렉토리 추가. parent r6 와 동시 갱신. |

---

## 0. 현재 상태 / 본 문서 위치

- **모드**: **Phase S3 풀스택 + `--review` 7 reviewer 메타 리뷰 처리 완료 — commit 대기** (s3-r1). 92/92 service test + 100/100 lib test 모두 통과. Phase S2 = commit `8661fb5` (s2-r0 박제) 완료.
- **선행 의존**: parent 의 Phase 1 **lib 본체 + lib unit test** 완료 (대안 B / parent r5 결정 12, parent r10 박제 `fddfbbf`). parent §4.5 (Promaker 통합) / §4.1 첫 task (`.gitignore`) / §4.6 (5.knowledge-base.md) / §4.8 의 Promaker 의존 테스트는 **본 phase (Phase S5) 가 흡수**.
- parent Phase 1 의 schema (§3.12) / RefLocator EBNF (§3.13) / PRAGMA (§3.17) / facade 결정 (§3.18.1) 등은 본 문서에서도 그대로 SSOT.
- **본 문서가 parent r5 로 통합될지, 별도 todo 로 유지될지**: Phase S5 (Promaker 통합) 진입 시점에 재결정. 현 시점 (S2 진입) 까지는 별도 todo 유지.
- **본 phase commit 누적**: `1be3ab8` (S1 service host scaffold, 24 Fact, 자가 검열 15건 반영) + **(commit 진행 중 — s2-r0)** S2 collection 관리 API (37 Fact + 자가 검열 5건 + follow-up 박제).

### 본 세션 결정 enum (D-id 정의표)

본 문서 본문에서 참조하는 결정 id 의 의미. 외부 회의록 의존 회피 (stale 차단).

| id | 결정 | 위치 |
|---|---|---|
| R1 | service = storage + share + MCP search host **ONLY** (색인 자체는 client) | §1, §3.1.1 |
| Q1 | collection registry sync = Promaker **시작 시점 1회만** (turn 마다 X) | §3.8 |
| Q2 | ATTACH limit (10) 가드 위치 = server `POST /sessions` hard fail (UI 도 sanity) | §3.9, §4.2 Phase S3 |
| Q3 | session 간 connection 격리 — 각자 별 `SqliteConnection` (pool X) | §3.8 |
| Q4 | active 셋 sync = lazy reject driven (Promaker 가 그대로 보내고 server reject 시 sync) | §3.8 |
| D1 | 사본 정책 = **1회성 import** (등록 시점 snapshot, 자동 drift 감지 X) | §3.5 |
| D2 | server 디렉토리 = `Collections\<guid>-<sanitized-title>\` (guid 식별 + title hint) | §3.4, §3.10 |
| D3 | collection 식별 SSOT = **guid v4**, **id 발급 주체 = server** (첫 POST 응답에 반환) | §3.4 |
| D4 | client → server upload = **multipart zip** (사용자 폴더 통째 포함) | §3.3, §3.9 |
| D5 | drift 갱신 = 사용자 명시 "새 버전 업로드" trigger 만 (FileSystemWatcher X) | §3.5 |
| D6 | citation 클릭 시 원문 보기 = **server 가 stream 응답** (LAN 가정) | §3.9, §4.2 Phase S4 |
| D7 | `DELETE /collections/{id}` = `Collections\<id>\` 전체 purge | §3.9 |
| T1 (옛 α) | multi-tenant 정책 = **flat** (누구나 모든 collection 보기). β/γ → T2/T3 (Phase S7 옵션, §4.2 Phase S7) | §3.6 |
| N5 | server 가 색인 안 함 → 색인 진행률 polling API 불필요 | §3.1.1, §4.2 Phase S7 |
| N6 | `maxUploadBytes` = **10 GB** (외부 config 노출 상수) | §3.11 |
| L1 | session 생성 trigger = Promaker LLM chat panel **open 시 1회** (chat lifetime) | §3.8 |
| L2 | session 해제 = **3중 cleanup** (panel close / process exit / server idle TTL) | §3.8 |
| L3 | session 401/403 자동 회복 = client 가 `POST /sessions` 재발급 + 동일 호출 1회 retry | §3.8 |
| P1 | (parent dependency) facade 양분 형태 = parent §3.18.1 결정 후 확정 (S1 진입 시 unblock) | §3.1.2, §4.3 |
| P2 | (parent dependency) ModelContextProtocol.AspNetCore 버전 align = parent §4.1 결정 따라 | §3.7, §4.2 Phase S3 |
| D-S3-1 | ATTACH connection lazy open vs eager — **lazy on first MCP call** (s3-r0 결정) | §3.8, §4.2 Phase S3 |
| D-S3-2 | `ModelContextProtocol.AspNetCore` 버전 = **1.2.0** (Promaker alignment, s3-r0 결정, P2 의 구체화) | §3.7, §4.2 Phase S3 |
| D-S3-3 | delete 분기 notifier ordering = **두 단계 모델** (mark-for-delete + connection 통째 폐기 + lazy re-open) | §3.9, §4.2 Phase S3 |
| D-S3-4 | ATTACH 시 진단 DU = **`AttachDiagnostic`** (Ok/DbMissing/OpenFailure/SchemaMismatch) — `IndexerVersionGateResult` 와 분리 | §3.8, §4.2 Phase S3 |

---

## 1. 작업 목표

LightHouse 의 색인 결과물을 central Windows Service 에 보관 / 다중 client 간 공유 / MCP search 호스트를 service 측에서 운영. Promaker 측은 client 색인 (write-path) + zip upload + HTTP client + chat UI 만 책임.

핵심 한 줄: **색인은 client, 보관·공유·검색은 server** (R1).

---

## 2. 배경 / motivation (parent r4 모델에서 발견한 부담)

| 항목 | parent r4 (in-process) | service 도입 이득 |
|---|---|---|
| Promaker 종료 후 codex / 외부 LLM client 가 KB 사용 | 불가 | **가능** — service 가 항상 켜져있음 |
| 다중 Promaker instance 가 같은 KB 사용 | 각자 in-process Kestrel + MCP host + `.mcp-config` 중복 발행 | **단일 service endpoint 공유** |
| 회사 NAS 의 공용 사양서 검색 | r4 §3.9 의 read-only NAS ATTACH (SMB + SQLite WAL fragility) | **LAN service** 가 자연 흡수 |
| Phase 4 embedding 모델 warm cache | Promaker 시작마다 load (수백MB~GB) | (client 측 색인 시점에 발생 — service 무관) |
| KB 가 사내 공용 자원화 | 가능하나 file-share 의존 | **multi-tenant T1 flat** 자연 |

note: 이전 세션 중간 논의에서 "service 가 색인까지 수행" 안도 있었으나 **사용자 결정 R1 으로 service = storage + search 호스트만**. 색인 (Extract/Chunk/FTS5/index.db 생성) 은 client 측 `Ds2.LightHouse` lib 의 write-path 가 책임.

---

## 3. 결정된 설계

### 3.1 책임 분리 (SSOT)

#### 3.1.1 client vs service 책임 분리표 (R1)

| 책임 | client (Promaker / `lighthouse-cli`) | service |
|---|---|---|
| Extract (PdfPig / OpenXml) | ✓ | ✗ |
| Chunk | ✓ | ✗ |
| FTS5 `index.db` 생성 | ✓ | ✗ |
| IndexerVersion bump 자동 재색인 | ✓ (재색인 후 새 zip upload) | ✗ |
| Phase 3 OCR | ✓ | ✗ |
| Phase 4 embedding | ✓ | ✗ |
| Phase 2/5 caption | ✓ | ✗ |
| zip 패키징 | ✓ | ✗ |
| **upload 수신 + storage** | ✗ | ✓ |
| **multi-tenant share (T1 flat)** | ✗ | ✓ |
| **MCP search host** (`attachment_*` 4종) | ✗ | ✓ |
| **session routing** (active 셋) | ✗ | ✓ |
| **file serving** (citation 원문 stream) | ✗ | ✓ |
| **인증 (TLS + PSK) + zip sanitize** | ✗ | ✓ |

derivative: service 가 색인 안 하므로 **색인 진행률 polling API 불필요 (N5)**. server-side 처리 시간 = sanitize + atomic move + IndexerVersion 검증 (수 초 이내). 진행률 표현은 client-side upload byte 만 의미.

#### 3.1.2 LightHouse lib 양분 (write-path / read-path)

- **write-path** (`Indexer` / `Extractor` / `Chunker` / `SqliteStore` 의 write API) → client 측만 사용
- **read-path** (`Searcher` / `KnowledgeBase` facade 의 read API) → service 측만 사용
- 동일 lib, 양쪽 모두 ProjectReference (client = `Promaker.csproj` / `Ds2.LightHouse.Cli.fsproj`, service = `Ds2.LightHouseService.fsproj`)
- **양분 형태 (facade 가 read-only mode 옵션 받는지) 는 §4.3 미확정** — parent §3.18.1 의 facade 결정 (record-of-functions vs interface) 결과에 의존. S1 진입 시 확정.

#### 3.1.3 MCP host 2개 정책

Mutation MCP (parent 의 `apply_model_doc` 등) 은 Promaker in-process 그대로. LLM client 의 `.mcp-config` 에 server 2개 등록:
```
mcpServers:
  promaker   : Promaker in-process (mutation tool 일체)
  lighthouse : Service (attachment_* 4종 + session)
```
LLM 인지 부담 0 — tool 14종 (10 mutation/read + 4 attachment) 가 한 공간에 자연 공존 (이름 중복 0). system prompt `5.knowledge-base.md` 에 출처 명시 권장.

### 3.2 통신 흐름

```
[등록 단계 — 비빈번]
client (Promaker / lighthouse-cli)
  ├─ folder 색인 (Ds2.LightHouse Indexer in-process)
  ├─ zip 패키징 (source/ + .lighthouse-kb/index.db + meta.json)
  └─ POST /collections (multipart) ──────────→ server
                                                ├─ sanitize + atomic move
                                                ├─ Meta.indexer_version 호환성 검증 (§3.12)
                                                └─ Collections\<guid>-<title>\ 배치

[검색 단계 — chat panel lifetime (L1)]
Promaker (panel open)
  ├─ POST /sessions { collectionIds: [...] } ──→ server: SessionState 생성 + ATTACH 준비
  │                                              ← { token, unknownIds?, unindexableIds? }
  ├─ unknownIds / unindexableIds 처리 (§3.8)
  └─ .mcp-config 갱신 (lighthouse server URL + auth + session header)

LLM (codex / API provider)
  └─ attachment_search(...) over MCP HTTP ─────→ server
                                                ├─ session lookup → active collections
                                                ├─ Searcher.fs → multi-db UNION BM25
                                                └─ result return

(citation 클릭 시 — D6)
Promaker UI
  └─ GET /collections/{id}/files/{fileId} ────→ server: source\ 의 원본 byte stream
                                                ← stream + Content-Type

[해제 단계 — 3중 cleanup (L2)]
panel close   → DELETE /sessions/{token}
process exit  → 살아있는 token 일괄 DELETE
server idle TTL → backstop
```

- service bind = **LAN (HTTPS)**
- 모든 호출 = `Authorization: Bearer <PSK>` (§3.7)
- session 호출 = 추가 `X-LightHouse-Session: <token>` (§3.8)

### 3.3 zip layout SSOT (D4)

client ↔ service 의 정합 SSOT:

```
<zip root>/
  meta.json                  # 필드 정의는 §3.3.1 SSOT
  source/
    plant-spec-v3.pdf        # 원본 파일 사본 (사용자 폴더 통째)
    io-list-2026.xlsx
  .lighthouse-kb/
    index.db                 # FTS5 완성품 (client 가 색인 끝낸 SQLite)
    blobs/images/<sha256>.<ext>  # Phase 2 이미지 blob (client 추출분, ext ∈ {png,jpg,jpeg,webp,tif,jp2})
```

service 는 zip 받으면:
1. **sanitize** (entry path `..` traversal 가드, 절대경로 거부, symlink 거부, 전개 후 storage root 하위인지 verify)
2. **blob path regex 강제** — `blobs/images/` 하위 entry 는 `^[0-9a-f]{64}\.(png|jpg|jpeg|webp|tif|jp2)$` 매치 의무
3. **zip bomb 가드** (누적 해제 byte / 압축 byte 비율 = `zipBombRatioLimit` 외부 설정, default `50` = 50:1)
4. **`.lighthouse-kb/index.db` 의 `Meta.indexer_version` / `Meta.schema_version` 호환성 검증** (§3.12)
5. **request body 도착 byte < Content-Length 면 즉시 abort + Staging entry 삭제 + 4xx 응답** (10 GB upload 중 단절 회복)
6. **atomic move** → `Collections\<guid>-<sanitized-title>\` (guid = server 가 부여, §3.4)

### 3.3.1 `meta.json` schema SSOT

client (CollectionPackager) ↔ service (sanitize + import) 사이 단일 schema. 필드명 = **camelCase** (JSON 통례). client 가 채우는 필드 / server 가 import 시 추가하는 필드 구분.

```json
{
  "schemaVersion": 1,                    // [client] meta.json 의 schema 자체 버전 (본 SSOT 의 rev)
  "indexerVersion": "1.0.0",             // [client] index.db 생성 시 사용한 Ds2.LightHouse lib version (§3.12 gate key)
  "title": "라인A 사양서 v3",            // [client] 사용자 표시 이름 (KbManagerDialog 입력)
  "sourcePathHint": "C:\\Users\\...\\라인A", // [client] 색인 원본 폴더 경로 (감사/진단용 hint, server 미사용)
  "fileCount": 42,                       // [client] source/ 안 원본 파일 개수
  "totalSourceBytes": 1234567890,        // [client] source/ 안 원본 byte 합 (zip bomb 가드 비교용)
  "createdAt": "2026-05-17T13:45:00Z",   // [client] zip 패키징 시점 (ISO-8601 UTC)
  "clientHost": "WIN-ABC123",            // [client] 색인 머신 식별 (audit hint, 필수 아님)
  "clientUser": "kwak@dualsoft.com",     // [client] 색인 사용자 (audit hint, §6 m11 audit 추가 시 활용)

  // ── server 가 import 시 추가 (client 가 보낸 값 무시) ──
  "id": "550e8400-e29b-41d4-a716-446655440000", // [server] guid v4 (D3, 첫 POST 응답에 반환)
  "importedAt": "2026-05-17T13:45:30Z",         // [server] storage 배치 완료 시점
  "importedBy": "kwak@dualsoft.com",            // [server] X-User-Identity 헤더 박제 (§6 m11)
  "storageRelPath": "Collections\\550e8400-...\\" // [server] storage root 기준 상대경로
}
```

- 양쪽이 enumerate 하는 자리 (§3.10, §4.2 Phase S2) 는 본 SSOT 참조로 축약 — 필드 추가/변경 시 본 §3.3.1 만 갱신.
- 미정의 필드는 forward-compat 차원에서 보존 (이후 schemaVersion bump 까지). server 는 미인식 필드 reject 안 함.
- `clientUser` / `importedBy` 가 분리된 이유: 색인은 사용자 A 가 했어도 upload 는 사용자 B 의 머신 (예: 회사 IT 가 batch 색인 후 upload). 둘 다 audit log 에 기록.

### 3.4 collection 식별 체계

- **server-side stable id = guid v4 (D3)** — 정렬·식별 SSOT
- **id 발급 주체 = server** (D3 강화) — client 가 `POST /collections` (multipart zip + title) 호출 시 **server 가 guid 생성** → 첫 응답 body `{"id": "<guid>", ...}` 로 반환. client 는 받은 id 를 `LlmConfig.KbCollections[].CollectionId` 에 박제 후 재업로드 (`POST /collections/{id}/payload`) 시 사용.
  - 사유: client 생성 모델은 두 client 가 같은 guid 동시 등록 race 처리 필요 / `meta.json` 의 `id` 필드를 client 가 알기 전 채워야 함. server 부여 모델은 atomic + 단순.
  - `meta.json` 의 `id` 필드는 server import 시점에 server 가 채워서 storage 의 `meta.json` 에 박제 (zip 안 client `meta.json` 의 `id` 필드는 비워서 보냄. server 가 발견 시 무시).
- 디렉토리 명 = `<guid>-<sanitized-title>` (D2) — 디스크 list 시 사람 가독, 정렬은 guid prefix
- `meta.json` 의 `title` 필드가 표시 SSOT (디렉토리 명은 단순 hint)
- client 측 `LlmConfig.KbCollections` schema (parent r5 SKIP 으로 `{path, active}` schema 자체가 prod 미존재 — migration 부담 0):
  ```json
  {
    "KbCollections": [
      { "CollectionId": "550e8400-e29b-41d4-a716-446655440000", "DisplayName": "라인A 사양서 v3", "Active": true }
    ],
    "LightHouseService": {
      "BaseUrl": "https://service.company.local:8443",
      "ApiKeyEncrypted": "<DPAPI(CurrentUser) base64 of PSK>"
    }
  }
  ```
  (parent r4 의 `LlmConfig.cs` 직렬화 관례 = PascalCase. JSON property name attribute 적용 위치 + 기존 필드 직렬화 관례 일관성 = §4.2 Phase S5 DoD 에 round-trip 검증 포함.)

### 3.5 사용자 폴더 ↔ server 사본 정책

- **1회성 import (D1)** — 등록 시점 snapshot. 사용자가 폴더 갱신해도 service 자동 미인지.
- **drift 자동 감지 X (D5)** — KbManagerDialog 의 명시 "새 버전 업로드" trigger 만. FileSystemWatcher 사용 안 함.
- **사용자 폴더 안 흔적 0** — `.lighthouse-kb/` 가 사용자 폴더에 안 생김. parent r4 §4.1 의 "`.gitignore` 에 `.lighthouse-kb/` 추가" task 본 phase 진입 시 무효.

### 3.6 multi-tenant 정책 — T1 flat

사용자 결정: **flat — 누구나 모든 collection 보기** (사내 공유 KB 모델).

- per-user namespace 격리 (β) 안 함
- collection 별 ACL (γ) 안 함
- 즉 `GET /collections` 응답은 모든 client 동일
- 회사 IT / 사용자가 등록한 collection 을 모든 사용자가 active 토글 가능
- **PII 위험 강화**: 사용자가 무심코 비밀 폴더 등록 시 다른 사용자 노출 → 등록 시 **consent dialog 의무화** (§6 m2). parent r4 §6 m15 의 강화판.

### 3.7 인증 / 보안

- **TLS 필수** (HTTPS bind, self-signed 도 OK, 회사 deployment 면 사내 CA 발급 권장)
  - **plain HTTP 요청은 거부** (Kestrel HTTPS-only listener). Phase S1 DoD 강제.
  - PSK 는 TLS 종단 안에서만 의미 — TLS 없으면 packet capture 로 영구 재사용 가능.
- **PSK (Pre-Shared Key)** — service 설치 시 발급, Promaker 설정에 수동 입력
  - 모든 API 호출에 `Authorization: Bearer <decrypted PSK>` + fixed-time compare (timing attack 방어)
  - **저장 형식 의무** (CR4):
    - server: `config.json` 의 `preSharedKey` 는 **DPAPI (LocalMachine scope)** base64 — install script 가 평문 입력받아 즉시 암호화하여 저장. tlsCertPassword 도 동일.
    - client (`LlmConfig.LightHouseService.ApiKeyEncrypted`): **DPAPI (CurrentUser scope)** base64 — ApplicationSettingsDialog 입력 즉시 암호화하여 persist. **평문 `ApiKey` 키 사용 금지** (LlmConfig load/save 시 항상 암호화된 값으로만 직렬화).
  - 회전 정책: service config 갱신 + 모든 client 재입력 (운영 부담 — **Phase S7 에서 mTLS 검토**, 사내 deployment 권장 default = mTLS Phase S7 우선 진입)
  - **PSK = 유일 인증 자산** (mTLS 까지) — leak 1건 = 사내 KB 전체 노출. multi-tenant T1 flat 의 단일 실패점. §6 m9 SSOT.
- **session token** 은 별도 routing key (§3.8). PSK 와 역할 분리:
  - PSK = "이 호출자가 신뢰된 client" (LAN 인증)
  - session token = "어느 active 셋 routing" (chat lifetime)
- **`X-User-Identity` 헤더 의무** (M11/§6 m11) — PSK 만으로는 호출자 식별 불가 → client 가 `X-User-Identity: <username>` (Windows username 또는 LlmConfig 의 user identifier) 동봉. server 가 audit log 에 박제.
- **replay 방어** — TLS 가 1차 방어. 추가 필요 시 Phase S7 mTLS 또는 HMAC-SHA256 (timestamp + nonce) 채택.
- **zip sanitize** — `..` traversal, 절대경로 entry 거부, symlink 거부. `Collections\<id>\` 하위로만 전개 강제.
- **zip bomb 가드** — 누적 압축 해제 byte 한도 (`zipBombRatioLimit`, default 50:1).
- **blob path regex 강제** — `blobs/images/` 하위 entry = `^[0-9a-f]{64}\.(png|jpg|jpeg|webp|tif|jp2)$` (§3.3).

### 3.8 session model (L1/L2/Q1/Q3/Q4 정합)

| 시점 | 처리 |
|---|---|
| Promaker startup | (Q1) `GET /collections` 1회 호출로 registry sync — KbManagerDialog 가 LlmConfig.KbCollections 와 server registry 의 stale entry 비교 + 정리 |
| chat panel open (`LlmChatViewModel.InitializeAsync`) | (L1) `LlmConfig.KbCollections.Active` → `POST /sessions { collectionIds }` → token 박제. **chat lifetime 동안 재사용** (turn 단위 재발급 X — parent r4 §3.18.2 의 turn-scoped 컨텍스트 모델은 본 design 에서 chat-scoped session 으로 대체) |
| chat 진행 (multi-turn) | 같은 token 재사용. MCP HTTP 헤더 `X-LightHouse-Session` 동봉 |
| chat 진행 중 사용자가 KbManagerDialog 에서 active 토글 | **현 session 영향 0**. 다음 chat 부터 반영. UI chip 안내 "변경은 다음 chat 부터" |
| chat panel close | (L2-1) `DELETE /sessions/{token}` (1차 cleanup) |
| Promaker process exit | (L2-2) 살아있는 token 들 일괄 DELETE (panel close 못한 경우 대비) |
| server-side idle TTL (예: 1h) | (L2-3) backstop — process kill / network drop 대비 |

**Active 셋 sync (lazy reject driven, Q4)**:
```
POST /sessions { collectionIds: [LlmConfig active set] }
  service:
    각 id → registry lookup
    각 id 의 status 확인 (idle / indexing / error)
    response: { token, unknownIds?, unindexableIds? }

Promaker:
  if unknownIds 존재:
     → GET /collections 로 sync
     → LlmConfig.KbCollections 에서 해당 entry 제거 (server 가 영구 폐기)
     → atomic Save
  if unindexableIds 존재 (status=error):
     → LlmConfig 에서 제거하지 않음 (재시도 가능 — server 측 복구 후 다시 active)
     → chip 안내 "색인 실패 collection N개 제외"
  unknown + unindexable 모두 제외 후 재요청 POST /sessions
  최종 응답 chip 통합 안내
```

**Session 간 connection 격리 (Q3)**:
- session 당 `SqliteConnection` 1개 + ATTACH 별칭 `kb0..kbN-1`
- 같은 collection 이 여러 session 에 등장해도 각자 별 connection (SQLite WAL multi-reader 라 file lock 부담 없음)
- pool 도입은 Phase S7

**Session 401/403 자동 회복 (CR6 / L3)**:
- service restart / idle TTL 만료 / 일시 network drop 모두 client 입장에서 동일 — MCP 응답 401/403.
- `LightHouseClient` 가 401/403 받으면:
  1. 현재 `LlmConfig.KbCollections.Where(Active).Select(CollectionId)` 로 `POST /sessions` 재발급
  2. 새 token 으로 동일 MCP 호출 **1회 retry**
  3. 재 401/403 이면 사용자/LLM 에게 명확한 fail 보고 (chip + log)
- session token 은 in-memory (§3.8 의 SessionRegistry) 라 service restart 시 invalid. 본 회복 정책이 chat lifetime 전체에 걸쳐 투명성을 보장.
- Phase S3 DoD 에 "service restart 중 진행 chat 의 MCP 호출이 자동 회복" test 포함.
- Phase S5 DoD 에 "service kill → 재시작 → 진행 chat 의 다음 attachment_search 가 즉시 회복" 시나리오 포함.

**ATTACH limit (Q2) boundary 명확화 (MA17)**:
- `Microsoft.Data.Sqlite` 의 `bundle_e_sqlite3` 가 사용하는 SQLite SQLITE_MAX_ATTACHED default = 10 (main DB 제외 *추가* 10개). 즉 active 셋 최대 10 collection.
- 정확한 boundary 는 Phase S1 진입 시점에 `PRAGMA compile_options` 로 재검증 (bundle 빌드 옵션 의존).
- session 별 connection 격리 (Q3) 라 다른 session 의 ATTACH 수와 무관, session 내 active 셋 길이만 가드.
- Phase S3 DoD 에 "active 10개 정확 정상 / 11개 hard fail / 동시에 같은 collection 을 2 session 이 ATTACH" test 포함.

### 3.9 API surface

| API | 용도 | 호출 시점 |
|---|---|---|
| `POST /collections` (multipart: zip + title) | 신규 collection 등록 (client 가 색인한 폴더 zip, D4) | KbManagerDialog 의 "추가" / lighthouse-cli upload |
| `GET /collections` | registry list (T1 flat 이라 전체 응답) | Promaker startup (Q1) / KbManagerDialog open / session reject 시 sync |
| `GET /collections/{id}/status` | 단일 collection 상태 (idle / error / not-found) | UI polling (필요 시) |
| `POST /collections/{id}/payload` | 재업로드 — 같은 id 에 새 zip swap | KbManagerDialog 의 "새 버전 업로드" (D5) |
| `DELETE /collections/{id}` | 제거 (`Collections\<id>\` 전체 purge, D7) | KbManagerDialog 의 "제거" |
| `GET /collections/{id}/files/{fileId}` | 원문 byte stream (PDF/DOCX/..., D6) | citation 클릭 시 |
| `GET /collections/{id}/files/{fileId}/thumbnail` | 미리보기 (Phase S7 옵션) | UI 보조 |
| `POST /sessions` `{ collectionIds }` | active 셋 routing token 발급 (Q2 ATTACH limit hard fail 포함) | chat panel open |
| `DELETE /sessions/{token}` | session 해제 | chat panel close / process exit |
| (MCP) `attachment_list/_outline/_search/_read` | 검색·읽기 (session 헤더로 active 셋 routing) | LLM 호출 |

모든 호출 공통 헤더:
- `Authorization: Bearer <PSK>`
- `X-LightHouse-Session: <token>` (session API 와 MCP 만)

### 3.9.1 registry.json 동시성 (CR5)

`POST /collections` / `DELETE /collections/{id}` / `POST /collections/{id}/payload` 모두 registry.json read-modify-write. Kestrel 다중 thread 환경에서 race 회피:

- **service 내 `SemaphoreSlim(1, 1)` 으로 mutation 직렬화**. read-only 조회 (`GET /collections`, `GET /collections/{id}/status`) 는 lock 불필요 (read-time snapshot).
- atomic save 패턴 (write to `.tmp` → fsync → rename) + lock 으로 이중 가드.
- parent `LlmConfig.Save` 는 single-process WPF UI thread 가정이라 패턴만 차용, 동시성 가드는 추가 의무.
- Phase S2 DoD 에 "두 client 동시 `POST /collections` race 후 두 entry 모두 보존" test 포함.

### 3.10 server-side storage layout

```
%PROGRAMDATA%\Dualsoft\LightHouseService\
  config.json                # 외부 설정 (§3.11)
  registry.json              # { schemaVersion: 1, collections: [...] } — SemaphoreSlim 직렬화 (§3.9.1)
  Collections\
    <guid>-<sanitized-title>\
      meta.json              # 필드 정의 = §3.3.1 SSOT
      source\
        plant-spec-v3.pdf
        io-list-2026.xlsx
      .lighthouse-kb\
        index.db
        blobs\images\<sha256>.<ext>   # Phase 2+, ext ∈ {png,jpg,jpeg,webp,tif,jp2}
  Logs\
    service-YYYYMMDD.log     # log4net RollingFileAppender, logRetentionDays default 30 (§3.11)
  Audit\
    audit-YYYYMMDD.log       # 등록 / 삭제 / search query — user / collection / timestamp (§6 m11)
  Staging\                   # multipart upload 임시 영역, sweep 대상 (incomplete upload 즉시 cleanup + 주기 sweep)
    <upload-guid>.tmp
```

### 3.11 service config 외부 노출

```json
// %PROGRAMDATA%\Dualsoft\LightHouseService\config.json
{
  "schemaVersion": 1,                  // service config schema 자체 버전. service binary upgrade 시 migration trigger
  "listenUrl": "https://0.0.0.0:8443", // HTTPS 만 — plain HTTP listener 미바인드 (§3.7 강제)
  "tlsCertPath": "C:\\...\\service.pfx",
  "tlsCertPasswordEncrypted": "...",   // DPAPI (LocalMachine scope) base64 — 평문 저장 금지 (CR4)
  "preSharedKeyEncrypted": "...",      // DPAPI (LocalMachine scope) base64 — 평문 저장 금지 (CR4)
  "storageRoot": "%PROGRAMDATA%\\Dualsoft\\LightHouseService\\Collections",
  "maxUploadBytes": 10737418240,       // 10 GB (N6). Kestrel MaxRequestBodySize = 본 값
  "zipBombRatioLimit": 50,             // 50:1 (해제 byte / 압축 byte). 50 = 압축률 약 98% 이상 zip 거부
  "sessionIdleTtlMinutes": 240,        // 4시간 default (사용자 점심/회의 대비, CR6 자동 회복과 함께)
  "stagingSweepIntervalMinutes": 10,
  "logRetentionDays": 30,              // log4net RollingFileAppender — size + date rolling, retention 30일
  "logMaxSizeMB": 100,                 // 단일 log 파일 한도
  "auditRetentionDays": 365,           // Audit\ 별도 retention (보안 추적 필요)
  "indexerVersionRange": {             // §3.12 IndexerVersion gate
    "min": "1.0.0",
    "max": "1.99.99"
  }
}
```

- `schemaVersion` 가 service binary 가 인식하는 값보다 낮으면 in-place migration (backup → upgrade), 높으면 fail-fast.
- Phase S1 DoD 에 schema_version check + migration hook 포함.

### 3.12 IndexerVersion 호환성 — server gate

client (Promaker / CLI) 가 build 한 `index.db` 의 `Meta.indexer_version` 과 service 의 hosting 가능 범위가 일치해야 검색 정확. service 가 upload 시점에 검증:

```
POST /collections
  → service:
    extract zip → probe `.lighthouse-kb/index.db`
    read Meta.indexer_version, Meta.schema_version
    compare with self.HostingRange (min..max)
    → 범위 안: accept (201 Created)
    → 너무 낮음: 415 + "client lib 업그레이드 필요"
    → 너무 높음: 415 + "service 업그레이드 필요"
```

**paired-release 정책**: Promaker 와 service 의 `Ds2.LightHouse` lib version 은 dist 워크플로에서 강제 동일 (manifest hash 비교). drift 회피 (parent r4 §6 m16 의 완화 근거).

### 3.13 client (Promaker) 측 단순화 — *사유 SSOT*

본 단원은 변경 사유 SSOT. **실제 변경 항목 체크리스트는 §4.2 Phase S5 가 SSOT** (중복 회피).

**왜 변경하는가** (parent r5 대안 B 후, 본 phase 에서 *최초 도입*):
- attachment_* read tool 의 host = service 측. Promaker 측 in-process MCP 는 read tool 을 **애초 host 안 함** (parent r5 SKIP 으로 `AttachmentTools.cs` 자체가 Phase 1 에서 미생성)
- read-path = service 측 → Promaker 의 `LlmTurnContext` 에 `KnowledgeBase` 주입 회피 (parent §3.18.2 채택안 (a) 자체가 r5 에서 SKIP — 본 phase 에서도 *추가하지 않음*)
- multi-tenant + LAN → `LlmConfig` 에 service endpoint (`BaseUrl` + `ApiKeyEncrypted`) 추가, `KbCollections` schema = `{CollectionId(guid), DisplayName, Active}` (§3.4) — **본 phase 에서 최초 도입** (parent r5 SKIP 이라 `{path, active}` schema 도 prod 에 깔린 적 없음 → migration 부담 0)
- 색인 자체는 여전히 client → `AttachmentIngestService` 본 phase 에서 **최초 신설**, 색인 후 zip 패키징 + upload 흐름

**부수 효과**:
- `PromakerToolNames.All` 의 attachment_* 4종은 parent r5 SKIP 으로 *애초 미추가* — 본 phase 진입 후에도 Promaker 측 MCP 가 host 안 함. 현 6종 (`expectedSet = {apply_model_doc, validate_model_doc, export_model_doc, json_to_yaml, find_by_name, validate_model}`) **그대로 유지** (DriftTests 의 expectedSet 변경 task 자체 불필요 — 본 phase 진입 시점에 grep 으로 6종 fresh 확인만).
- parent §3.0 의 두 경로 분리 invariant (chat image drop ≠ KB ingest) 유지 — service 도입이 KB 경로만 영향, chat 경로 무관.

### 3.14 parent ↔ service 회귀 매트릭스 (전체 SSOT)

본 매트릭스가 parent ↔ service 회귀의 단일 SSOT (11행). parent 진입 박스의 9행은 *진입 hint* (요약), 본 표가 *전체*.

| parent 단원 | 회귀 내용 |
|---|---|
| §3.9 (저장 위치 — path-based 사용자 자유) | **재작성** — collection = server-side guid, 사용자 폴더 안 사본 X |
| §3.10 (MCP tool surface — server 측 active 셋 fix) | tool 이름/인자 자체는 그대로. host 위치만 service 로 신설 (본 문서 §3.1.1 / §3.1.3). 호출 context 는 `X-LightHouse-Session` 헤더로 갈음 (§3.8). quota 가드 `maxCallsPerTurn` 은 `maxCallsPerSession` 으로 의미 갱신 |
| §3.17 (SQLite 운영 — WAL/동시성/재색인) | client 측 색인 build 의 SSOT 로 유지. service read 측은 read-only ATTACH 만 |
| §3.18 (DI / lifecycle — KnowledgeBase facade) | client 측은 Indexer facade, service 측은 Searcher facade 로 양분 (본 문서 §3.1.2). r4 의 단일 facade 가정 변경 |
| §3.18.2 (LlmTurnContext 에 KnowledgeBase 주입) | **회피** — parent r5 SKIP 으로 *애초 추가 안 함*. service 측은 session 기반 routing (§3.8) 으로 대체 |
| §4.1 첫 task (`.gitignore .lighthouse-kb/`) | **삭제** — 사용자 폴더에 안 생김 (parent r5 SKIP, 본 phase 에서도 영구 불필요) |
| §4.5 (Promaker 측 통합 — `AttachmentTools.cs` / KbManagerDialog / LlmConfig 등) | **신설** (parent r5 SKIP 으로 prod 미존재) — 본 문서 §3.13 (사유) + §4.2 Phase S5 (체크리스트). KbManagerDialog / LlmConfig.KbCollections / ApplicationSettingsDialog 의 KB 버튼 = 본 phase 가 *최초 도입처* |
| §4.6 (`5.knowledge-base.md` 시스템 prompt) | **신설** (parent r5 SKIP 으로 미생성) — 본 phase 에서 server-side host 명시 + MCP host 2개 정책 (§3.1.3) 포함하여 작성. citation 표시형 / quota / `attachment_search → attachment_read` 흐름 등은 parent §3.14/§3.10 SSOT 참조 |
| §4.7 (UI dock 패널 폐기 결정) | 유지 (영향 없음) |
| §6 m15 (PII / 보안 — collection 등록 시 consent) | **강화** — multi-tenant T1 flat 이라 위험 ↑. 등록 시 "이 collection 은 다른 사용자도 검색 가능" 명시 + consent 의무화 (본 문서 §6 m2) |
| §6 m16 (ATTACH 된 collection schema 불일치) | **완화** — service 의 IndexerVersion gate (본 문서 §3.12) + paired-release (본 문서 §6 m1) 가 흡수 |

---

## 4. 남은 할 일 (Phase 별)

### 4.1 Phase S0 — 진입 전 확인 (선행 의존) *(s1-r0: 4 task 전부 통과)*

- [x] parent r4 의 Phase 1 **lib 본체** 완료 — §4.2 / §4.3 / §4.4 / §4.8 의 lib unit test 까지. commit chain `bccb0ea → cfc2c29 → b8c747c → 16b50c3 → 00b72eb → 9736237 → fddfbbf`
- [x] **parent §4.5 / §4.1 첫 task / §4.6 / §4.8 의 Promaker 의존 테스트가 정상적으로 SKIP** — `AttachmentTools.cs` / `Knowledge/` / `KbManagerDialog` / `5.knowledge-base.md` / `KbCollections` / `.lighthouse-kb/` 모두 prod 미존재 확인 (grep 0 hit)
- [x] parent r4 의 `Ds2.LightHouse` lib facade read/write 분리 가능 확인 — 현 lib 이미 양분 (`Searcher` / `KnowledgeBase` = read-only surface, `Indexer` = write module). **§4.3 P1 결정: `readonly:bool` 파라미터 추가 *불요*** (server 가 KnowledgeBase facade 그대로 ProjectReference, Indexer 는 dead-link OK)
- [x] 사용자 머신에 parent Phase 1 의 시험 산출물 (`<폴더>/.lighthouse-kb/`) 잔재 0 확인

### 4.2 Phase S1~S7

각 phase 헤더의 **DoD** = 완료 정의 (acceptance criteria).

#### Phase S1 — service 기반 host *(s1-r0: commit `1be3ab8` 완료 — 24 Fact 100%)*

**DoD**: TLS bind 성공 + plain HTTP 거부 + DPAPI 로 PSK 복호화 + PSK 인증 미들웨어가 빈 `GET /collections` 요청에 200 응답 (registry 비어있음). `Collections\` / `Staging\` / `Logs\` / `Audit\` 초기화 완료. log4net RollingFileAppender (size + date) 가 첫 로그 라인 기록. EventLog 에 service start 이벤트 등록. config schema_version check 통과. install/uninstall script 산출물 동작 검증.

- [x] 신규 project `Solutions/Tools/Ds2.LightHouseService/Ds2.LightHouseService.fsproj` (F# 채택 — §4.3 결정)
  - TargetFramework `net9.0`, `Microsoft.NET.Sdk.Web`
  - `Microsoft.Extensions.Hosting.WindowsServices` + `builder.Host.UseWindowsService()` (콘솔 실행 시 자동 fallback)
  - `Ds2.LightHouse` ProjectReference (read-path 만 사용 — §4.3 P1 결정: `readonly:bool` 파라미터 추가 안 함)
- [x] config 로드 (`Config.fs`) — `%PROGRAMDATA%\Dualsoft\LightHouseService\config.json` + schema_version check (낮으면 명시 fail-fast, 향후 bump 시 in-place migration 분기 추가 박제 — Minor m8 보류)
- [x] **DPAPI 복호화** (`Config.decryptDpapi`) — `tlsCertPasswordEncrypted` / `preSharedKeyEncrypted` LocalMachine scope. 빈 입력 ArgumentException, 잘못된 base64 FormatException (DpapiRoundTripTests 4 Fact)
- [x] TLS 바인드 (`Program.fs`) — Kestrel HTTPS-only, `X509CertificateLoader.LoadPkcs12FromFile`. `Config.validateHttpsOnly` + `WebHost.UseUrls("")` 로 default URL 차단 (review M4)
- [x] PSK auth middleware (`Middleware.fs`) — `Authorization: Bearer` + `CryptographicOperations.FixedTimeEquals` (BCL 의 length-mismatch const-time 보장, review C2). 시그니처 = `Func<HttpContext, Func<Task>, Task>` (ASP.NET Core 9 표준, review C1)
- [x] **`X-User-Identity` 헤더 의무 middleware** — 누락 시 401 + audit log warn (`HttpContext.Items.[UserIdentityItemKey]` 박제)
- [x] storage layout (`Storage.fs`) — `Collections/Staging/Logs/Audit` subdir 자동 생성 + 쓰기 가능 probe (Logs/.probe-<guid> 파일 생성/삭제)
- [x] log4net (`log4net.config`) — `ServiceRollingFile` (size+date) + `AuditRollingFile` (date) + `EventLog` (WARN, review m10). `Audit` logger `additivity=false` + LoggerMatchFilter cross-write 차단. **retention sweep (date-based)** 는 Phase S2 IHostedService 이연 (review M3)
- [x] **install / uninstall script** (`scripts/install-service.ps1` + `uninstall-service.ps1` + `config.json.template`) — PSK / cert pw SecureString 입력 → `ProtectedData.Protect LocalMachine` → config.json + EventLog Source 등록 + `sc.exe create obj= LocalSystem` (review M1/M2/m11)
- [x] sln 등록 — `Apps/Promaker/Promaker.sln` + `Solutions/Ds2.sln`
- [x] **paired-release manifest hash hook** (placeholder) — fsproj 의 `PairedReleaseCheck` Target 이 `GetAssemblyIdentity` 로 `Ds2.LightHouse.dll` version 추출 + Message 박제. 본격 검증 (drift 시 build fail) 은 dist 워크플로 (`make dist`) 진입 시 `scripts/check-paired-release.ps1` 신설 + dist skill 의 build step 사이

#### Phase S2 — collection 관리 API *(s2-r0: commit 진입 직전 — 37 Fact 100%)*

**DoD**: 최소 zip (`source/` + `.lighthouse-kb/index.db` 최소형 + `meta.json`) 으로 `POST /collections` 성공 → server 가 guid 부여 → `{id}` 응답 → `Collections\<guid>-<title>\` 배치 → `GET /collections` 1행 응답. sanitize (`..` traversal / 절대경로 / symlink / blob regex) / zip bomb (50x ratio) / IndexerVersion gate (호환 / too-low / too-high 3 케이스) / 도착 byte 부족 abort / 동시 race (두 client 동시 POST 후 두 entry 보존) / swap 중 active session 잔존 / 0-doc collection 등록 / Staging incomplete cleanup unit test 통과. `POST /collections/{id}/payload` swap rollback 시나리오 통과.

- [x] `POST /collections` — multipart 수신 + Staging\ 임시 저장 + 응답 body 의 server 발급 `{id}` (CR3, D3). **request body 도착 byte abort (MA12)** 는 ASP.NET Core 의 IFormFile 이 multipart 전체 읽기 후 진입이라 Phase S2 에선 Kestrel MaxRequestBodySize (10 GB) 가드만 — 본격 streaming abort 는 Phase S7 chunked upload 와 함께
- [x] zip sanitize — entry path `..` / 절대경로 (Linux `/` + Windows `C:` + DOS device `\\?\` 모두 거부, review C1) / symlink (POSIX S_IFLNK, review C2) / `blobs/images/` lowercase sha256 regex (review m1, §3.3)
- [x] zip bomb 가드 — `zipBombRatioLimit` (50:1) 누적 byte (`compressedTotalBytes * limit < accumDecompressed` 시 abort)
- [x] IndexerVersion 호환성 검증 (§3.12) — 415 + `{clientVersion, hostingRange, suggestedAction}` (Compatible/TooLow/TooHigh/Missing 4 분기)
- [x] atomic move (Staging → Collections\<guid>-<title>\) + `MetaJson.stampServerFields` 로 server 필드 채움 (client 가 박은 server 필드 무시)
- [x] **`registry.json` upsert with SemaphoreSlim** (CR5, §3.9.1) — process-wide singleton lock + atomic save (`.tmp` → File.Replace) + read snapshot lock-free
- [x] **0-doc collection 처리 정책** (MA18) — `MetaJson.toRegistryEntry` 가 FileCount=0 도 status=idle 정상 처리 + RegistryTests 회귀 보호
- [x] `GET /collections` — registry snapshot (T1 flat). ETag 캐시는 Phase S7 (운영 부담 시점에 도입)
- [x] `GET /collections/{id}/status` — `{id, status, errorReason, lastImportedAt}`
- [x] `POST /collections/{id}/payload` — `ZipImport.swapCollectionPayload` (`.bak` rename + rollback) + `notifier.OnPayloadSwapped` 신호 + Registry upsert. **swap 시 active session 정책 (MA6)** = Phase S3 SessionRegistry impl 시 실 detach (현재 `LoggingCollectionLifecycleNotifier` = audit log 만, M5 review 박제)
- [x] `DELETE /collections/{id}` — `notifier.OnDeleted` 신호 → Registry remove → `ZipImport.purgeCollection` (D7). notifier ordering 은 Phase S3 진입 시 재검토 (review M5)
- [x] `DELETE /uploads/{stagingId}` — `StagingSweep.removeStaging` (guid v4 validate + 디렉토리/`.tmp` 둘 다 제거)
- [x] Staging\ stale sweep — `StagingSweepService : BackgroundService` (service start 시 1회 초기 sweep + `stagingSweepIntervalMinutes × 2` maxAge 주기 sweep)
- [x] **Audit log 기록** — POST register / payload swap / DELETE / staging cancel 모두 `Log.audit.Info` 박제 + user identity (`X-User-Identity` 헤더, AuthMiddleware 가 HttpContext.Items 박제) 동봉

#### Phase S3 — session + MCP search host

**DoD**: `POST /sessions { collectionIds }` → token 발급. unknownIds / unindexableIds 응답 unit test 통과. ATTACH limit boundary (9/10/11 + 동시에 같은 collection 을 2 session) 통과. service restart 중 진행 chat 의 MCP 호출 자동 회복 (L3) 통과. LLM client (codex 가능) 가 MCP `attachment_list` 호출 시 active 셋 union 응답. session 헤더 누락 시 401. idle TTL sweep 후 connection dispose 통과.

- [ ] `POST /sessions` — collectionIds validate (registry 부분집합) + **ATTACH limit boundary 검증** (MA17, `PRAGMA compile_options` 로 SQLITE_MAX_ATTACHED 재확인) + token 발급
- [ ] `SessionRegistry` (in-memory) — `{token, activeCollectionIds, attachedAliases, connection, lastUsedAt, userIdentity}`
- [ ] per-session `SqliteConnection` 격리 (Q3) — open + ATTACH `kb0..kbN-1` lazy on first MCP call
- [ ] idle TTL sweep (`sessionIdleTtlMinutes` default 240) — connection dispose + registry 제거 (L2-3)
- [ ] `DELETE /sessions/{token}` — 명시 해제
- [ ] MCP server host — `ModelContextProtocol.AspNetCore` `WithHttpTransport()` + `WithToolsFromAssembly()` (parent r4 의 `McpHostService` 패턴 동일, P2 의 버전 align 결정 따라)
- [ ] `AttachmentTools` (서버측 신설) — 4종 (`attachment_list/_outline/_search/_read`). session 헤더로 `SessionState` lookup → 그 connection 으로 `Ds2.LightHouse.Searcher` 호출. **quota 가드는 session 누적 enforce** (parent §3.10 의 `maxCallsPerTurn` 은 `maxCallsPerSession` 으로 의미 갱신, mn12)
- [ ] fileId 합성 — **`<collection-guid>:<documents-id>`** (MA23, D3 guid 와 정합. parent §3.10 의 `<collection-index>` 형은 폐기 — turn 간 stable 보장 약함)
- [ ] 응답에 unknownIds / unindexableIds 동봉 (active 셋 sync 용, §3.8)
- [ ] **service restart 시 in-memory SessionRegistry 손실 → client (L3) 자동 회복 test 통과** (CR6)

#### Phase S4 — file serving (citation 원문)

**DoD**: `GET /collections/{id}/files/{fileId}` 가 `Collections\<id>\source\` 의 원본 byte stream 반환 (D6). HTTP Range 지원 (대용량 PDF) + ETag = FileHash. Content-Type 추정 OK (PDF / DOCX / XLSX / PPTX / TXT / MD 케이스). 존재하지 않는 fileId 는 404. 권한 (PSK) 없으면 401.

- [ ] `GET /collections/{id}/files/{fileId}` — Collections\<id>\source\ 의 원본 stream (Content-Type 추정 + **HTTP Range 지원** + **ETag = FileHash** for client cache, MA8)
- [ ] viewer 채택 결정 (MA8) — Phase S5 의 citation 클릭 UX 가 (a) OS default app 호출 vs (b) 내장 viewer. 권장 default: PDF/DOCX/XLSX 는 (a), TXT/MD 는 (b)
- [ ] (옵션) `GET /collections/{id}/files/{fileId}/thumbnail` — PDF page 0 / Office 파일 첫 슬라이드 등 작은 미리보기
- [ ] (옵션) `GET /collections/{id}/files/{fileId}/page/{n}.png` — Phase 2 PDF page 렌더

#### Phase S5 — Promaker (client) 통합

**DoD**: Promaker 가 service 에 PSK (DPAPI 저장) 로 인증 → KbManagerDialog 에서 폴더 추가 (consent dialog) → 색인 → upload (cancel button 동작) → chat 시작 시 session 발급 → LLM 이 attachment_search 호출 → citation 포함 응답 생성. service kill → 재시작 → 진행 chat 의 다음 호출 자동 회복 (L3). KbManagerDialog 에서 active 토글 → 다음 chat 부터 반영 (L1) 확인. **`Solutions/Tests/Ds2.LightHouseService.IntegrationTests/` 의 client↔server round-trip suite 통과** (MA22). `LlmConfig.cs` round-trip 시 기존 필드와 직렬화 관례 동일 (MA4). `ApplicationSettingsDialog` 의 "연결 테스트" 버튼 동작 확인.

- [ ] `LlmConfig.cs` 확장 — **본 phase 가 KbCollections 최초 도입처** (parent r5 SKIP 으로 prod 미존재):
  - `KbCollections` schema 신설: `List<KbCollectionEntry>` = `{CollectionId(guid), DisplayName, Active}` (§3.4)
  - `LightHouseService` 신설: `{BaseUrl, ApiKeyEncrypted(DPAPI base64, CurrentUser scope)}` (§3.4, §3.7)
  - atomic save / corrupt fallback 패턴 유지 (parent r4 `LlmConfig.Save` 동형)
  - schema migration = **불필요** (§4.3 default c — parent r5 SKIP 으로 `{path, active}` 형태 데이터 prod 미존재)
  - 직렬화 관례 = grep 으로 기존 `LlmConfig` 필드 (PascalCase vs camelCase) 확인 후 정합 ⚠ DoD 에 round-trip 검증 포함
- [ ] `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs` 신설 — HTTP client wrapper
  - `UploadCollectionAsync(title, zipStream, CancellationToken)` → `{collectionId(guid)}` (server 가 발급, §3.4 D3)
  - `ListCollectionsAsync()` → CollectionInfo[] (Promaker startup 호출 — Q1)
  - `DeleteCollectionAsync(id)`
  - `CreateSessionAsync(collectionIds)` → `{token, unknownIds[], unindexableIds[]}` (§3.8)
  - `DeleteSessionAsync(token)`
  - 모든 요청에 `Authorization: Bearer <DPAPI-decrypted PSK>` 자동 동봉 + TLS 강제 (plain HTTP 거부, §3.7)
  - **MCP 호출 응답 401/403 시 `CreateSessionAsync` 자동 재발급 + 동일 호출 1회 retry** (CR6, §3.8)
- [ ] `Apps/Promaker/Promaker/Knowledge/CollectionPackager.cs` 신설 — folder → zip (`source/` + `.lighthouse-kb/` + `meta.json` per §3.3.1 SSOT)
- [ ] `Apps/Promaker/Promaker/Knowledge/AttachmentIngestService.cs` **신설** — 색인 (Ds2.LightHouse Indexer in-process) → zip 패키징 → LightHouseClient.UploadCollectionAsync. cancel button hook (HTTP request abort + 즉시 `DELETE /uploads/{stagingId}` 호출)
- [ ] `Apps/Promaker/Promaker/Dialogs/KbManagerDialog.xaml(.cs)` **신설** (parent r5 SKIP 으로 미존재 — 본 phase 가 *최초 도입*):
  - folder picker → 색인 진행률 (client 측) → upload 진행률 (HTTP) → 완료
  - active 토글 → `LlmConfig.KbCollections[i].Active` 변경만 (server 무영향, 다음 chat 부터 반영)
  - chip 안내 "변경은 다음 chat 부터" (§3.8)
  - **consent dialog 강제** — folder 추가 직전 "이 collection 은 multi-tenant T1 flat 정책상 다른 사용자도 검색 가능. 비밀 문서 포함 폴더 등록 금지" 표시, 매 등록마다 (§6 m2 SSOT)
  - service 미연결 시 안내 chip ("LightHouse Service 에 연결할 수 없습니다. ApplicationSettingsDialog 의 BaseUrl/PSK 확인")
- [ ] `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml(.cs)` **신설/확장** — LLM 탭에 "LightHouse Service" section (BaseUrl / PSK 입력 + DPAPI 암호화 저장) + **"연결 테스트" 버튼** (`GET /collections` 1회 → 결과 chip)
- [ ] `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` 갱신:
  - `InitializeAsync` 에서 `LightHouseClient.CreateSessionAsync` 호출 + 응답 처리 (unknownIds/unindexableIds sync — §3.8)
  - `.mcp-config` 작성 시 lighthouse server 항목 추가 (service URL + session header)
  - chat panel close / Dispose 시 `DeleteSessionAsync` (L2-1)
- [ ] `Apps/Promaker/Promaker/App.xaml.cs` process exit hook — 살아있는 token 일괄 DELETE (L2-2)
- [ ] `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs` — attachment_* 4종 **추가 안 함** (현 6종 유지). parent r5 SKIP 으로 *애초 추가 안 됨* → 본 phase 에서도 변경 0 (grep 으로 fresh 확인만)
- [ ] `Solutions/Tests/Ds2.LlmAgent.Tests/PromakerToolNamesDriftTests.fs` — `expectedSet` **변경 없음 확인** (현 6종 fresh). 본 task = grep + 확인만, 코드 변경 0
- (parent r5 SKIP 후) `Apps/Promaker/Promaker/LlmAgent/Tools/AttachmentTools.cs` — 본 phase 에서도 **만들지 않음** (service 측 신설, §4.2 Phase S3)
- (parent r5 SKIP 후) `LlmTurnContext.cs` 의 `KnowledgeBase` 필드 — 본 phase 에서도 **만들지 않음** (read-path 가 service 측이라 회피)

#### Phase S6 — CLI 도구 (옵션, 회사 IT 운영용)

**DoD**: `lighthouse-cli index <folder> --upload <url> --psk <key> --title "..."` 무인 동작으로 색인 + upload 완료 + exit code 0. 인증 실패 / IndexerVersion mismatch / zip 크기 초과 케이스 별 비-0 exit code + stderr 안내.

- [ ] `Solutions/Tools/Ds2.LightHouse.Cli/Ds2.LightHouse.Cli.fsproj`
  - `Ds2.LightHouse` write-path 사용
  - `lighthouse-cli index <folder> --upload <service-url> --psk <key> --title "..."` 명령
  - GUI 없이 batch 색인 + upload
- [ ] (옵션) `lighthouse-cli sync` — registry 기준으로 stale collection 정리 등 운영 명령

#### Phase S7 — 후속 (선택)

**DoD**: 항목별 채택 시점에 결정. mTLS 도입 시 PSK 회전 부담 완화 확인. SSE `/events` 도입 시 KbManagerDialog 의 polling 제거. Resumable upload 도입 시 수 GB zip 실패 후 재개 통과.

- [ ] SSE `/events` — 색인 진행률 stream (현재 upload progress 만 의미. server-side 색인 없음 — N5)
- [ ] resume 가능한 chunked upload (tus-protocol 등) — 대용량 (수 GB) zip 실패 시 재전송 부담 완화
- [ ] **mTLS** (PSK 회전 부담 완화 — §3.7 참조)
- [ ] multi-service routing — 사용자가 동시에 회사 service + 개인 PC service 두 군데 등록 가능. `LlmConfig.LightHouseServices : List<...>`
- [ ] T2/T3 (옛 β/γ) multi-tenant 확장 (PII 격리 요구 발생 시) — T2 = per-user namespace, T3 = collection 별 ACL
- [ ] connection pool (Q3 격리 정책 완화) — 메모리 압박 시점에 검토

### 4.3 미확정 항목 (Phase S 진입 시 결정)

dep = 결정 dependence (먼저 해결되어야 함). ⚠ = parent 결정 결과에 의존 (P-id, §0 표 참조).

| 항목 | 위치 | 권장 default | 확정 시점 | dep |
|---|---|---|---|---|
| ~~⚠ P1: `Ds2.LightHouse` lib facade 의 양분 형태~~ | §3.1.2 | ~~facade 에 `readonly: bool` 파라미터 추가~~ | **s1-r0 결정**: `readonly:bool` 파라미터 **추가 안 함**. 현 `KnowledgeBase` facade 가 이미 read-only surface (Search/List/Outline/Read/ActivePaths/Dispose), `Indexer` 는 별 module → server 가 KnowledgeBase 만 ProjectReference, Indexer 는 dead-link OK | parent §3.18.1 |
| ~~service host 언어 — F# vs C#~~ | §4.2 Phase S1 | ~~F# (LightHouse 와 동일 stack)~~ | **s1-r0 결정**: F# 채택 (`Microsoft.NET.Sdk.Web`) | P1 |
| TLS 인증서 발급 운영 — self-signed vs 사내 CA | §3.7 | **s1-r0 결정**: self-signed (PoC). deployment 시 사내 CA 전환 | S1 진입 → 정책 박제 | — |
| PSK 회전 정책 | §3.7 | **s1-r0 결정**: 수동 + Phase S7 mTLS 우선 진입 | S1 진입 → 정책 박제 | — |
| ~~ATTACH connection lazy open vs eager~~ | §3.8 | ~~lazy on first MCP call~~ | **s3-r0 결정**: lazy on first MCP call (`POST /sessions` 응답 = token 발급만, `SqliteConnection.Open` + `ATTACH` 는 첫 `attachment_*` 호출 시점) | — |
| ~~ATTACH 시 진단 DU (Compatible/TooLow/TooHigh/Missing 외에 attach 시점 분기)~~ | §3.8, s2-r0 follow-up M2 | ~~AttachDiagnostic 신설~~ | **s3-r0 결정 (D-S3-4)**: `AttachDiagnostic` (Ok / DbMissing / OpenFailure of string / SchemaMismatch of string) 신설. `IndexerVersionGateResult` (upload 시점) 와 분리 | — |
| ~~delete 분기 notifier ordering — mark-for-delete 두 단계 모델~~ | §3.9, s2-r0 follow-up M5 | ~~SessionRegistry 진입 시 두 단계 모델~~ | **s3-r0 결정 (D-S3-3)**: 두 단계 모델 채택. notifier.OnDeleted → 즉시 모든 session activeCollectionIds 에서 제거 + ATTACH 된 connection 통째 폐기 → 다음 MCP call 시 lazy re-open. KnowledgeBase facade 부분 detach 미지원 → connection 통째 폐기 단순화 | — |
| ~~ATTACH limit boundary (main 포함 10 vs 추가 10)~~ | §3.8, MA17 | **s1-r0 결정**: parent r4 박제 `MaxAttachedDbs = 10` 그대로 사용. `PRAGMA compile_options` 재확인은 Phase S3 진입 시 (실 ATTACH 위치) | S3 진입 | — |
| Promaker 의 in-process search fallback 제공 여부 (service 미가동 시) | §3.13 | **fallback 안 함** (SSOT 일관성, §6 m8) | S5 진입 | — |
| `lighthouse-cli` 의 Phase 진입 시점 | §4.2 Phase S6 | **Phase S5 완료 후 별도** (단순화) | S5 진입 | — |
| CLI 인증 모델 (별도 PSK vs GUI 와 동일) | §4.2 Phase S6, mn9 | 별도 PSK 또는 mTLS client cert 권장 (leak 부담 격리) | S6 진입 | — |
| `LlmConfig.KbCollections` schema migration — parent r4 의 `{path,active}` 데이터 처리 | §3.4 / Phase S5 | **(c) 마이그레이션 불필요** (parent r5 SKIP 으로 prod 미존재). fallback: (a) 자동 폐기 + chip 안내 | S5 진입 직전 (Phase S0 의 잔재 점검 항목과 함께 확인) | — |
| `LightHouseService` config 의 JSON property attribute 적용 (PascalCase 직렬화 vs camelCase) — 기존 `LlmConfig.cs` 와 정합 | §3.4 / Phase S5 | grep 으로 기존 LlmConfig 필드 직렬화 관례 확인 후 정합. Phase S5 DoD 에 round-trip 검증 (MA4) | S5 진입 직전 (sln 갱신 commit 직전) | — |
| ~~⚠ P2: `ModelContextProtocol.AspNetCore` 버전~~ | §4.2 Phase S3 | ~~parent 결정 그대로 align~~ | **s3-r0 결정 (D-S3-2)**: 1.2.0 (Promaker 와 동일, parent r7 박제 정합). 1.3.0 업그레이드는 양쪽 동시 진입 별도 PR 보류 | parent §4.1 |
| `lighthouse` endpoint version prefix (`/v1/collections` 등) | §3.9, mn13 | 도입 권장 (서비스 v2 시 dual host 여지) | S2 진입 | — |
| viewer 채택 (citation 클릭) — OS default app vs 내장 | §4.2 Phase S4/S5, MA8 | PDF/DOCX/XLSX = OS default, TXT/MD = 내장 | S5 진입 | — |
| ~~paired-release manifest hash 정의 (PE 헤더 vs InformationalVersion vs 별도 manifest)~~ | MA7, §3.12, §6 m1 | **s1-r0 결정**: `Ds2.LightHouse.dll` 의 `Version` (AssemblyVersion) 비교. Service fsproj 의 `PairedReleaseCheck` Target placeholder + dist 워크플로 (`make dist`) 진입 시 `scripts/check-paired-release.ps1` 신설 (build fail 강제) | dist 진입 | — |
| consent dialog 문구 SSOT + persist 위치 (audit log 행 박제 vs LlmConfig 행) | §3.6, §6 m2 | audit log 박제 (per-등록) + consent 거부 시 등록 중단 | S5 진입 | — |
| backup / DR 정책 (Collections\ + registry.json snapshot 도구) | §6 m3 (신설) | 운영자 책임 (VSS / robocopy /mir + flush). 또는 Phase S7 `POST /admin/backup` endpoint | S5 진입 (운영 가이드 문서화 시점) | — |

---

## 5. 관련 파일 / 경로

### 신규 (Phase S 진입 시)

- `Solutions/Tools/Ds2.LightHouseService/Ds2.LightHouseService.fsproj` + 본체 (.fs)
- `Solutions/Tools/Ds2.LightHouse.Cli/Ds2.LightHouse.Cli.fsproj` (옵션, Phase S6)
- `Solutions/Tests/Ds2.LightHouseService.Tests/` (unit)
- `Solutions/Tests/Ds2.LightHouseService.IntegrationTests/` (client↔server round-trip, WebApplicationFactory 또는 TestContainers, MA22)
- `Solutions/Tools/Ds2.LightHouseService/scripts/install-service.ps1` (PSK 평문 입력 → DPAPI 암호화, MA13)
- `Solutions/Tools/Ds2.LightHouseService/scripts/uninstall-service.ps1`
- `Solutions/Tools/Ds2.LightHouseService/scripts/config.json.template`
- `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs` (HTTP wrapper, CR6 L3 자동 회복 포함)
- `Apps/Promaker/Promaker/Knowledge/CollectionPackager.cs` (folder → zip per §3.3.1)
- `Apps/Promaker/Promaker/Knowledge/AttachmentIngestService.cs` (parent r5 SKIP 으로 본 phase 가 *최초 도입*)
- `Apps/Promaker/Promaker/Dialogs/KbManagerDialog.xaml(.cs)` (parent r5 SKIP 으로 *최초 도입*, consent dialog 포함)
- `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml(.cs)` 의 "LightHouse Service" section + "연결 테스트" 버튼 (parent r5 SKIP 으로 *최초 도입*)
- `Apps/Promaker/Promaker/LlmAgent/Prompts/5.knowledge-base.md` (parent §4.6 SKIP 으로 *최초 도입*, server-side host + MCP host 2개 정책 포함)

### 수정

- `Apps/Promaker/Promaker/LlmAgent/LlmConfig.cs` — schema 변경 (`KbCollections` `{CollectionId, DisplayName, Active}` + `LightHouseService { BaseUrl, ApiKey }`)
- `Apps/Promaker/Promaker/Knowledge/AttachmentIngestService.cs` — 색인 후 zip + upload 단계 추가
- `Apps/Promaker/Promaker/Dialogs/KbManagerDialog.xaml(.cs)` — server registry sync, upload UI
- `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml(.cs)` — LightHouse Service section
- `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` — session 발급/해제, `.mcp-config` lighthouse 항목 작성
- `Apps/Promaker/Promaker/App.xaml.cs` — process exit hook (살아있는 session 일괄 DELETE)
- `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs` — attachment_* 제외 환원
- `Apps/Promaker/Promaker/LlmAgent/Prompts/5.knowledge-base.md` — server-side host 명시
- `Solutions/Tests/Ds2.LlmAgent.Tests/PromakerToolNamesDriftTests.fs` — expectedSet 6종으로 환원
- `Apps/Promaker/Promaker.sln` + `Solutions/Ds2.sln` — LightHouseService + Tests 추가

### 삭제 / 미도입 (parent r4 대비)

- (parent r4 §4.5 의) `Apps/Promaker/Promaker/LlmAgent/Tools/AttachmentTools.cs` — 본 phase 진입 시 **만들지 않음** (service 측 신설)
- (parent r4 §4.5 의) `LlmTurnContext.cs` 의 `KnowledgeBase` 필드 — 본 phase 진입 시 **만들지 않음** (read-path 가 service 라 회피)

### 참조용 (수정 없음)

- `todo-lighthouse-kb-index.md` (parent design) — Phase 1 본체 + §3.12 schema + §3.13 RefLocator + §3.17 PRAGMA 등 SSOT
- `Apps/Promaker/Promaker/LlmAgent/McpHostService.cs` — service 측 MCP host 의 패턴 참조 (loopback nonce → LAN PSK 로 변환)
- `Apps/Promaker/Promaker/LlmAgent/McpConfigWriter.cs` — `.mcp-config` 작성 패턴 (lighthouse 항목 추가 시 참조)
- `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` — `InitializeAsync` 의 `_mcpHost.StartAsync` 호출 패턴, session 발급 코드의 자연 위치
- `Solutions/Core/Ds2.LlmAgent/doc/todo-llm-chat-attachment.md` — chat 경로 측 (parent §3.0 두 경로 분리 invariant 유지)

---

## 6. 주의 사항

1. **paired-release 강제** — Promaker 와 service 의 `Ds2.LightHouse` lib version 이 다르면 schema 의미 drift. dist 워크플로 (`make dist`) 에서 manifest hash 비교 후 mismatch 시 build fail.
2. **multi-tenant T1 flat 의 PII 위험** — 사용자가 등록한 collection 은 모든 사용자가 검색 가능. KbManagerDialog 의 "추가" 클릭 시 **consent dialog 의무화** ("이 collection 의 내용은 다른 사용자도 검색 가능합니다. 비밀 문서 포함 폴더는 등록 금지"). parent r4 §6 m15 의 강화판.
3. **zip sanitize 우선** — entry path `..` traversal / 절대경로 / symlink (있을 경우) 모두 거부. `Collections\<id>\` 하위로만 전개. 누적 byte 한도 (`zipBombRatioLimit`) 가드.
4. **chat panel lifetime = session lifetime (L1)** — turn 마다 재발급 X. 사용자가 KbManagerDialog 에서 active 토글해도 현 chat 영향 0. UI chip "변경은 다음 chat 부터" 강제.
5. **session 3중 cleanup (L2)** — panel close (1차) + process exit (2차) + idle TTL (3차). leak 차단.
6. **server reject 시 lazy sync (Q4)** — `POST /sessions` 응답의 `unknownIds[]` 받으면 Promaker 가 `GET /collections` 로 동기화 + LlmConfig 정리. `unindexableIds[]` 는 LlmConfig 보존 + 재시도 가능. Promaker 시작 시 1회 외 추가 polling 없음 (Q1).
7. **LightHouse lib 양분 시 facade 설계 주의** — write-path (Indexer) 와 read-path (Searcher) 가 같은 facade record 안에 섞이면 service publish 시 write-path 코드 dead-link. parent §3.18.1 의 record-of-functions 선택이라면 read 전용 record 분리 검토. §4.3 미확정.
8. **fallback 금지** — service 미설정 / 미가동 시 Promaker 가 in-process search 로 회귀하지 말 것. SSOT 가 둘이 되면 일관성 깨짐. KB 비활성 + 명확한 안내만.
9. **parent r4 의 §3.18.2 채택안 (a) 회귀** — service 도입 시 read-path 가 LlmTurnContext 와 무관 (server 측에서 자체 routing). r5 통합 시 §3.18.2 단원 큰 폭 재작성.
10. **MCP host 2개의 LLM 인지 부담** — `.mcp-config` 에 promaker + lighthouse 두 server 등록 시 tool 14종 (10 mutation/read + 4 attachment) 가 한 공간에 보임. 충돌 없음 (이름 중복 0) 이지만 system prompt `5.knowledge-base.md` 에 출처 명시 권장.
11. **CLAUDE.md 자가 검열 trigger** — Phase S1~S5 각각 다음 trigger 다중 충족 예상: ② 신규 함수/타입 3+ 신설, ③ 단일 파일 100+ line 또는 2+ 파일 동시 변경, ⑤ public API/SSOT 갱신. 각 phase 별 sub-agent 검열 의무.
12. **commit 정책** — multi-step plan 의 "go" 동의를 commit step 까지 묶지 말 것. commit 은 별도 confirm (memory: `feedback_commit_authorization`).
13. **line number 박제 회피** — 본 문서의 parent 참조 (예: `LlmChatViewModel.InitializeAsync` 의 `_mcpHost.StartAsync` 호출 line) 는 가능한 symbol 기반. 진입 시 grep 재확인. parent r4 §6 m13 와 동일 정책.
14. **parent §3.0 두 경로 분리 invariant 유지** — service 도입은 KB ingest 경로만 영향. chat image/text drop 경로 (`AttachmentClassifier`) 와 무관. `todo-llm-chat-attachment.md` 와의 cross-PR 충돌 없음.
15. **MEMORY.md `## Project` 등록** — Phase S1 진입 commit 직후 본 todo 항목을 메모리에 등록 (parent r4 §6 m11 의 본 phase 판).
16. **PSK = 유일 인증 자산 (단일 실패점)** — mTLS Phase S7 까지 PSK 만이 인증 수단. leak 1건 = multi-tenant T1 flat 전체 collection 노출. server `config.json` 의 `preSharedKey` + client `LlmConfig.LightHouseService.ApiKeyEncrypted` 둘 다 **DPAPI 의무** (§3.7 CR4). 평문 키 저장 절대 금지. 사내 deployment 면 Phase S7 mTLS 우선 진입 권장.
17. **audit log SSOT** — `Audit\audit-YYYYMMDD.log` (server-side, log4net 별도 appender). 기록 항목: `{timestamp, user(X-User-Identity), action(register/delete/search/payload-swap), collectionId, queryText(검색 시 첫 200자), result(success/fail), errorReason}`. PII consent 결과도 동일 log 에 박제 (등록 시점). 보관 기간 = `auditRetentionDays` (default 365, §3.11).
18. **service restart 자동 회복 (CR6 / L3)** — `LightHouseClient` 가 MCP 응답 401/403 받으면 `POST /sessions` 재발급 + 동일 호출 1회 retry. 사용자/LLM 입장에서 service restart / idle TTL 만료 / 일시 network drop 모두 투명 회복. 이 정책 누락 시 chat 회복 불가 (parent §3.18.2 회피로 LLM 단순 "MCP 실패" 만 받음).
19. **registry mutation 직렬화 (CR5)** — service 내 `SemaphoreSlim(1,1)` 으로 registry.json read-modify-write 가드. parent `LlmConfig.Save` 의 single-thread 가정과 다름 (Kestrel 다중 thread). Phase S2 DoD 에 동시 race test 포함.
20. **paired-release manifest hash (M1 강화)** — `Ds2.LightHouse.dll` 의 `InformationalVersion` 비교 + service `HostingRange.min..max` 와 client lib version 의 호환성 검증. dist 워크플로 (`make dist`) 의 post-build target 에서 강제. mismatch 시 build fail. 구현 위치 = §4.3 미확정 표 추가 후 결정.

---

## 7. 다음 세션 첫 행동

**현 상태 (s3-r1 박제 시점)**: Phase S3 풀스택 작성 완료 + `--review` 7 reviewer 처리 완료. **commit 0건** — 사용자 confirm 대기.

1. **본 문서 정독** — 특히 **s3-r1 rev** (review 처리 결과 + 보류 분류) + **s3-r0 rev** (결정 4건 D-S3-1~4) + §0 D-id 표 (R1/Q1~Q4/D1~D7/T1/N5/N6/L1~L3/P1/P2/D-S3-1~4 모두 SSOT).
2. **현 working tree 변경 점검** — `git status` 로 변경 파일 확인. 미commit 산출물:
   - 신규 운영 (4): `Solutions/Tools/Ds2.LightHouseService/{SessionRegistry,SessionEndpoints,SessionSweep,AttachmentTools}.fs`
   - 수정 운영 (8): `Program.fs` (MCP host + DI), `Middleware.fs` (SessionAuth 추가 + IM-11 audit log helper), `Logging.fs` (tokenFingerprint + sanitizeForLog), `CollectionEndpoints.fs` (IC-2 title SSOT + IM-2 singleton), `ZipImport.fs` (IC-1 rollback + IM-6 delegation + IC-2 Cf 거부), `Endpoints.fs` (IM-3 dead code 제거), `Ds2.LightHouseService.fsproj` (4 신규 + MCP 1.2.0), `scripts/install-service.ps1` (IM-10 ZeroFreeBSTR + UTF-8 no-BOM + 127.0.0.1)
   - 신규 lib (1): `Solutions/Core/Ds2.LightHouse/KnowledgeBase.fs` 확장 (probeIndexerVersion / isIndexed / MaxAttachedDbs)
   - 신규 test (3): `SessionRegistryTests.fs` / `SessionAuthTests.fs` / `AttachmentToolsTests.fs` (27 Fact)
   - 수정 test (2): `ZipImportTests.fs` (sanitizeTitle 2 추가) / `TextExtractorTests.fs` (BOM 회귀 1 추가)
   - 수정 sln/cfg (3): `Solutions/Directory.Packages.props` (MCP 1.2.0), `Ds2.LightHouseService.Tests.fsproj` (3 신규 Compile), `todo-lighthouse-kb-server.md` (s3-r0 + s3-r1 박제)
3. **commit 진입 결정** — 단일 commit 또는 sub-step 분리:
   - 옵션 A: 단일 commit "S3 session + MCP search host (27 fact) + review 8건 + s3-r1 박제"
   - 옵션 B: 두 commit — (a) "S3 풀스택 + s3-r0" + (b) "--review 8건 + s3-r1"
   - 권장 = **B (review 처리가 phase 본체와 의미적으로 별개)**.
4. **Phase S4 (file serving) 또는 Phase S5 (Promaker 통합) 진입 confirm**:
   - Phase S4 = `GET /collections/{id}/files/{fileId}` + HTTP Range + ETag (citation 원문 stream, D6) — 소규모
   - Phase S5 = Promaker 측 LightHouseClient + KbManagerDialog + LlmConfig.KbCollections + AttachmentIngestService — 대규모 (parent §4.5 흡수, S5 가 IM-7 IntegrationTests 도 흡수)
5. **commit 은 단계별 별도 confirm** (memory: `feedback_commit_authorization`).

**s3-r1 review 잔여 우려 (Phase 2 / S5 follow-up)**:
- IM-1 endpoint try/with 5-way 헬퍼 압축 (Phase 2)
- IM-4 MetaJson.load schemaVersion 주석 정정 (backlog)
- IM-5 PSK byte cache + Array.Clear (별 PR, 복잡도)
- IM-7 CollectionEndpoints unit test 0건 → Phase S5 WebApplicationFactory IntegrationTests 흡수
- IM-9 StagingSweep `.in-progress` sentinel (Phase 2)
- Minor 18건 일괄 정리 (Phase 2)
- Phase S3 자가 검열의 잔여 — M5 (MCP attribute reflection e2e 검증) / M9 (Config.validate SessionIdleTtlMinutes 상한) / M10 (Phase S5 LightHouseClient L3 catch 검증) / m13 (Resolver caching) / m15 (ModelContextProtocol transitive 정리)
