# Ds2.LightHouse — KB 서버 (central Windows Service) 도입

세션 이어받기용 TODO. `todo-lighthouse-kb-index.md` (이하 *parent*) 의 r4 위에 얹는 incremental design.
실제 진입은 parent 의 Phase 1 (in-process MVP) 완료 후. 본 문서는 그 후속 phase 의 설계 박제.

| rev | 일자 | 주요 변경 |
|---|---|---|
| s6-r1~r6 | 2026-05-18 | **본 변경 이력 표는 s6-r0 까지만 박제** — s6-r1 (server-side negative path 7 Fact + ZipBuilders) / s6-r2 (IPv6 거부 Fact) / s6-r3 (check-paired-release.ps1 follow-up 3건) / s6-r4 (LlmChatViewModel L3 정책 주석) / s6-r5 (IndexerVersion gate 415 Fact 2건 + stampIndexerVersion lib facade) / s6-r6 (P5/P6/P2-r3 묶음 — suggestedAction 정정 + S4 follow-up 5건 + ExecuteWithSessionRetryAsync facade). **SSOT = §7.1 commit chain 표** (자세한 상세 변경 박제). `git log --oneline -10` 도 보완 참조. |
| s6-r0 | 2026-05-18 | **Phase S6 P1 — `lighthouse-cli index --upload` 본격 구현 + CliUploadTests 4 Fact + 자가 검열 2건 즉시 적용**. (i) **신규 운영 2** — `Solutions/Tools/Ds2.LightHouse.Cli/LightHouseClient.fs` (~110 line, F# HTTP wrapper: createHttpClient HTTPS-only + uploadCollection POST /collections multipart + LightHouseAuthError/LightHouseProtocolError 2 exception + status code 분기) / `Solutions/Tools/Ds2.LightHouse.Cli/Packager.fs` (~135 line, staging copy + in-process Indexer.ingest + meta.json server schema wire 정합 + zip 패키징 + safeDelete cleanup). (ii) **수정 운영 1** — `Program.fs` (+114/-18): `--upload <url> --psk <key> --title <name> --user <id> --allow-invalid-certs` 5 flag + `LIGHTHOUSE_PSK` env var fallback + runUpload (staging→ingest→zip→upload→exception→exit code) + sync-over-async (`.GetAwaiter().GetResult()` console main 안전) + `--upload` value 누락 explicit reject (자가 검열 C1). (iii) **수정 운영 1** — `Ds2.LightHouse.Cli.fsproj` Compile Include 2 추가. (iv) **신규 test 1** — `Solutions/Tests/Ds2.LightHouseService.IntegrationTests/CliUploadTests.fs` (~155 line, **4 Fact**: Packager round-trip / uploadCollection 정상 (server guid 발급) / 잘못된 PSK → LightHouseAuthError 401 / http:// → ArgumentException) + fsproj Compile Include 1 + Cli ProjectReference 1. (v) **사전 결정 박제 (D-S6-1~4 정합 + 신설)**: 인증 = GUI 와 동일 PSK (D-S6-1), 명령 surface = `lighthouse-cli index <folder> [--upload <url> --psk <key> ...]` (D-S6-2), args parsing = 수동 (D-S6-3, `=` form 미지원 follow-up), exit code SSOT = 0/1/2/3/10/11/12/99 (D-S6-4). 신규 (D-S6-5) **HTTP status → CLI exit code mapping**: 401/403 → 1, 415 → 2, 413 → 3, 기타 비-2xx → 99 (s6-r0 잔여 우려: server 의 415 가 IndexerVersion mismatch 매핑인지 server source crosscheck 필요 — Phase S6 P2). (vi) **자가 검열 (sub-agent general-purpose)** Critical 1 / Major 1 / Minor 3 → **즉시 적용 2건**: **(C1)** `--upload` value 없거나 빈 string → silent `runIndex` fallback 차단, explicit reject exit 10 + 사용자 안내 / **(M1)** uploadCollection 의 stream ownership docstring 정정 — MultipartFormDataContent.Dispose 가 child dispose + FileStream.Dispose idempotent 라 caller `use` 와 double-dispose 안전 명시. **잔여 박제 (Phase 이연)**: (m1) `.lighthouse-kb/` prefix check OS-cross-safe 확인 — 정합. (m2) sync-over-async 주석 보강 — backlog. (m3) clientHost/User stamp ↔ server Audit cross-ref — 정합. (vii) **debugging 박제**: F# `[<CLIMutable>]` record 가 `private` modifier 시 JsonSerializer reflection 차단 → 모든 필드 default(0/"") 직렬화/역직렬화. `UploadResponse` + `MetaDto` 두 record `private` 제거 후 통과 (test 진행 중 발견). (viii) **결과 검증**: IntegrationTests **8→12 Fact** (+4), lib 100 + service 111 + Promaker 203 회귀 0 = **누적 426 Fact** (s5f-r0 422 → +4). |
| s5f-r0 | 2026-05-18 | **Phase S6 scaffold + s5e follow-up 3건 + N1~N5 묶음 진행**. (i) **N2 (s5e follow-up 3건)** — `E2eRoundTripTests.fs` (+44/-5): **(s5e-M2)** Fact 6 finally `.Result.Dispose()` (async-over-sync) → `try/with + ExceptionDispatchInfo.Capture/Throw` 패턴 (F# task CE 가 reraise 직접 호출 금지). **(s5e-m5)** `buildMinimalZip` 의 `Indexer.ingest` 결과 `Ingested` variant 1+ 존재 명시 검증 (회귀 detection 강화 — Skipped/Failed 만 반환 시 build 통과해도 server upload 후 attachment_search 0 hit). **(s5e-I)** Fact 7 신설 — HTTPS-only 검증 (`http://` scheme → connection refused / HttpRequestException / IOException / AuthenticationException catch). FS0760 (`new TextExtractor()`) 동시 해소. IntegrationTests **7→8 Fact** 100%. (ii) **N4 (Phase S6 scaffold)** — `Solutions/Tools/Ds2.LightHouse.Cli/{Ds2.LightHouse.Cli.fsproj, Program.fs}` 신설 (~125 line). 사전 결정 4건 박제: **(D-S6-1)** CLI 인증 = GUI 와 동일 PSK (별도 PSK 분리 부담 회피, mTLS 는 Phase S7), **(D-S6-2)** 명령 surface = `lighthouse-cli index <folder> [--upload <url>] [--psk <key>] [--title <name>]`, **(D-S6-3)** args parsing = 수동 (System.CommandLine 미사용 — 외부 의존 minimize, `=` form 미지원은 follow-up), **(D-S6-4)** exit code = 0 ok / 1 인증 실패 / 2 IndexerVersion mismatch / 3 zip size 초과 / 10 명령행 / 11 폴더 미존재 / 99 기타. log = stderr. 본 turn = `index` (색인만, in-process Indexer) + `--version` + usage. `--upload` 본격 구현은 follow-up. PrivateAssets=all 우회 위해 PdfPig + OpenXml + Sqlite + CodePages 4 PackageReference 직접 노출 (service 와 동일 패턴). sln 2개 (`Solutions/Ds2.sln` + `Apps/Promaker/Promaker.sln`) 등록. smoke test (`--version` + usage) 통과. (iii) **N3-A (parent r12 동시)**: `Apps/Promaker/Directory.Packages.props` + `Promaker.csproj` 에 OpenXml 3.5.1 직접 노출 — MSB3277 충돌 0. (iv) **N3-B (todo git mv) 취소** — 사용자 결정 (todo 본문 path 유지 = `Apps/Promaker/Docs/`). (v) **N5 (Phase 2 plan)** — 별 doc (`todo-lighthouse-phase2-images.md`) 신설 안 함, parent §3.15.5 sub-section 으로 흡수 (사용자 결정). (vi) **자가 검열 (sub-agent general-purpose)** Critical 0 / Major 1 / Minor 3 — M1 (todo 본문 stale path) 은 N3-B 취소로 자연 해소. m1 (CLI parseArgs `=` form) / m2 (Fact 7 SocketException variant) / m3 (IExtractor 누락 없음 확인) = follow-up 또는 Phase 2 이연. (vii) **회귀 검증**: lib 100 + service 111 + IntegrationTests 8 (+1) + Promaker 203 = **누적 422 Fact** (s5e-r0 421 → +1). |
| s5e-r0 | 2026-05-18 | **Phase S5e 진입 — 본격 IntegrationTests round-trip (MA22) 박제 해제 + 자가 검열 0 차단 + commit 대기**. s5c-r0 (vii) 의 이연 사유 (DPAPI / TLS / Kestrel HTTPS bind / LightHouseClient HTTPS-only 우회 부담) 해소. (i) **수정 운영 1** — `Solutions/Tools/Ds2.LightHouseService/Program.fs` (+70/-44, 110 line diff): main 의 WebApplication 빌드 로직을 `configureApp (cfg: ServiceConfig) (psk: string) (tlsCert: X509Certificate2) : WebApplication` pure 함수로 export. main = (a) log4net 초기화 + (b) config.json 로드 + (c) DPAPI 복호화 + (d) PFX file 로드 + (e) `Storage.initialize` → `configureApp` 호출 → `app.Run` 의 thin wrapper. production 표면 변화 = 함수 1개 신설 (동작 보존, signature 만). (ii) **수정 운영 1** — `Solutions/Tests/Ds2.LightHouseService.IntegrationTests/Ds2.LightHouseService.IntegrationTests.fsproj` (+4 line): `Ds2.LightHouse` ProjectReference 추가 (Indexer + Extractors in-process 호출용) + Compile Include 2개 (ServiceFixture.fs / E2eRoundTripTests.fs). (iii) **신규 운영 1** — `Solutions/Tests/Ds2.LightHouseService.IntegrationTests/ServiceFixture.fs` (~144 line) — xunit `IAsyncLifetime` fixture. 책임: temp storage root + `Storage.initialize` / **in-memory self-signed cert** (`CertificateRequest.CreateSelfSigned` + ServerAuthentication EKU + SAN `localhost`/IP loopback + PFX export/reload — production 의 `LoadPkcs12FromFile` 와 동일 의미론) / 자체 `ServiceConfig` record (DPAPI 우회 — PSK 평문 직접 전달) / Kestrel `localhost:0` random port → `app.Urls` 첫 entry 에서 actual bound port 추출. `HttpClient` factory 2개 (`CreateAuthClient` = Bearer + X-User-Identity + `DangerousAcceptAnyServerCertificateValidator` / `CreateBareClient` = 신뢰 우회만). `DisposeAsync` = app.StopAsync(5s) + Dispose + cert.Dispose + temp dir 재귀 delete (try/catch — log4net Logs/ 핸들 잠재 잠금 흡수). (iv) **신규 test 1** — `Solutions/Tests/Ds2.LightHouseService.IntegrationTests/E2eRoundTripTests.fs` (~199 line) — `IClassFixture<ServiceFixture>` 적용. **6 Fact**: (1) `GET /healthz` auth-free 200 + body="ok" / (2) `GET /collections` 인증 통과 200 + body 가 `{"collections":[...],"schemaVersion":1}` 형식 / (3) Authorization 누락 → 401 / (4) 잘못된 PSK → 401 / (5) X-User-Identity 누락 → 401 / (6) **본격 round-trip** — POST /collections (in-process `Indexer.ingest` 로 색인된 minimal zip — source/sample.txt + .lighthouse-kb/index.db + meta.json) → server 발급 guid id 응답 → GET /collections (id 포함 확인) → GET /collections/{id}/status (idle) → POST /sessions {collectionIds} → unknownIds=[] + token → DELETE /sessions/{token} 204 → finally DELETE /collections/{id} cleanup. helper `buildMinimalZip (title) : byte[] * string` 신설 (TextExtractor 1개 사용, MetaJson.save, ZipFile.CreateFromDirectory `includeBaseDirectory=false`). (v) **자가 검열 (sub-agent general-purpose)** Critical 0 / Major 2 (M1 = 실은 정상 Minor 격하 / M2 = Fact 6 finally `.Result.Dispose()` async-over-sync — round-trip Fact 격리 cleanup 이라 production 무관, Phase S5f 또는 별도 PR 이연) / Minor 5 (m1 stylistic / m2 xunit v2/v3 분기 잔여 우려 / m3 docstring 표현 / m4 사용자 검토 H 정상 확인 / m5 `Indexer.ingest` 결과 `Ingested` 분기 검증 강화 backlog). **즉시 적용 0건** (모두 정상 동작 또는 cosmetic). (vi) **잔여 박제 (Phase S5f 또는 별도 PR)**: **(s5e-M2)** Fact 6 finally 의 `.Result.Dispose()` → `let!` + 명시 dispose 로 refactor / **(s5e-m5)** `buildMinimalZip` 의 `Indexer.ingest` 결과 `Ingested` 분기 검증 강화 (회귀 detection) / **(s5e-I)** HTTPS-only 검증 Fact (`http://` scheme → connection refused) 추가 — fixture 가 `https://` baseAddress 만 노출, 별도 client 신설 필요 / **(s5e-잔여 우려)** xunit v3 migrate 시 `IAsyncLifetime.InitializeAsync : ValueTask` signature 변화 — fixture 수정 필요 / log4net 초기화 생략으로 인한 "No appenders" cosmetic warning — fixture 가 `BasicConfigurator.Configure(NullAppender)` 1회 호출하면 silence. (vii) **결정 박제 (s5e-r0 정정)**: s5c-r0 (vii) 의 "본격 IntegrationTests round-trip (MA22) = Phase 2 또는 dist 직전 이연" → **본 turn 으로 해소**. 우회 mechanism = C 안 채택 (`configureApp` export + in-memory self-signed cert + Kestrel localhost:0 random port + 신뢰 우회 HttpClient). (viii) **§4.2 Phase S5 갱신**: "본격 IntegrationTests round-trip" task ✓ marker. (ix) **결과 검증**: `dotnet build LightHouseService` 0 오류 (refactor 후). `dotnet test LightHouseService.IntegrationTests` **7/7 통과** (scaffold sentinel 1 + e2e 6, 실 Kestrel HTTPS round-trip 포함 453ms). `dotnet test LightHouseService.Tests` **111/111 회귀 0**. `dotnet test LightHouse.Tests` **100/100 회귀 0**. 본 turn 으로 Promaker.Tests **203 + service 111 + IntegrationTests 7 + lib 100 = 421 Fact (+6 vs s5d-r0)** 전체 통과. |
| s5d-r0 | 2026-05-18 | **Phase S5 완전 종결 + dist 진입 준비 (paired-release drift detector 신설) + 자가 검열 1건 즉시 적용 + commit 대기**. (i) **§4.2 Phase S5 잔여 확인 only 2건 완료** — `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs` (attachment_* 4종 미존재 fresh 확인, 6종 풀세트 lock-in = find_by_name / validate_model / apply_model_doc / validate_model_doc / export_model_doc / json_to_yaml) + `Solutions/Tests/Ds2.LlmAgent.Tests/PromakerToolNamesDriftTests.fs` (`expectedSet` 동일 6종 lock-in, set equality v4) 모두 fresh grep + 코드 변경 0. **§4.2 Phase S5 100% 완료**. (ii) **신규 스크립트 1** — `Apps/Promaker/scripts/check-paired-release.ps1` (~118 line, UTF-8 BOM + CRLF — s5c-r1 .ps1 규약 정합). PowerShell 5.1 호환. `Solutions/Core/Ds2.LightHouse/SqliteStore.fs` 의 `module IndexerVersion` 안 `let Current` literal 을 source regex 추출 + `Solutions/Tools/Ds2.LightHouseService/scripts/config.json.template` 의 `indexerVersionRange.{min,max}` 와 `[System.Version]` semver 비교 → 범위 내 exit 0 / 밖 exit 1 + 사용자 안내 (lib 조정 vs config 확장 양 옵션). 3 시나리오 검증 통과 (in-range "1.0.0" ∈ ["1.0.0","1.99.99"] OK / out-of-range "2.5.0" DRIFT / regex miss DRIFT). (iii) **수정 운영 3** — `Apps/Promaker/.claude/skills/dist/SKILL.md` Step 3.5 신설 (build Step 3 직후 + pull Step 4 이전, drift 시 즉시 중단 + working tree 변경 0 → 단순 재시도) + 롤백 표 행 분할 (1~3 / 3.5 / 4~6) / `Solutions/Tools/Ds2.LightHouseService/Ds2.LightHouseService.fsproj` 의 `PairedReleaseCheck` Target Message 갱신 (AssemblyVersion=1.0.0.0 노출 + s5d-r0 정정 + dist 책임 박제) / `Solutions/Core/Ds2.LightHouse/SqliteStore.fs` 의 `module IndexerVersion` 위 SSOT 박제 주석 5줄 (`Current` literal 이 module 첫 `[<Literal>]` 위치 유지 의무 — paired-release regex 의존). (iv) **s1-r0 박제 정정 (Errata)** — s1-r0 박제 `§4.3` 표의 "paired-release manifest hash 정의" 행 = "**`Ds2.LightHouse.dll` 의 `Version` (AssemblyVersion) 비교**" 였으나 검토 결과 의미 없음 (`Solutions/Directory.Build.props` 가 AssemblyVersion 미주입 → lib dll AssemblyVersion=`1.0.0.0` default 라 비교 SSOT 부재). F# `[<Literal>]` 은 compile-time const inline 으로 reflection 도 불가 → source regex 추출이 유일 SSOT. 본 turn 부터 **paired-release SSOT = `IndexerVersion.Current` literal ∈ `indexerVersionRange.[min, max]`** 으로 재정의. (v) **검증 의미론** — F# `ZipImport.compareVersion` (digit-only component-wise int compare) ↔ PowerShell `[System.Version]` (Major.Minor.Build.Revision component-wise) — 3-part 입력에서 의미 동일 (현재 lib/config 모두 3-part 라 안전). 향후 4-part 진입 시 `Revision=-1` sentinel 처리 필요 (s5d-M1 박제). (vi) **자가 검열 (sub-agent general-purpose)** Critical 1 / Major 3 / Minor 4 → **즉시 적용 1건**: **(s5d-M2 부분)** SqliteStore.fs `module IndexerVersion` 위에 SSOT 박제 주석 추가 — `Current` 가 module 첫 `[<Literal>]` 위치 의존 명시 + SchemaVersion / Tokenizer 추가 시 Current 뒤에 두라는 안내. **잔여 박제**: **(s5d-C1)** SKILL.md 의 `installer/Apps/Promaker/...` path 정합성 — 모든 worktree (bKwak/light-house/llm/main/paper) SKILL.md 동일 관례 + 본 worktree 들 어느것에도 `installer/` 폴더 없음 → ds2 외부 wrapper deploy repo (또는 mount/symlink) 에서 `/dist` 가 실행되는 기존 SSOT 의존. 본 turn 신설 ps1 도 동일 관례 따른 것이라 정합. path 관례 변경은 별 turn 의 작업. / **(s5d-M1)** `[System.Version]::Parse` 의 3-part 입력 시 `Revision=-1` 처리 — 향후 4-part 진입 시 normalize 또는 직접 component-wise compare 로 교체. 현재 lib/config 모두 3-part 라 안전. → Phase 2 이연. / **(s5d-M2 나머지)** Regex brittleness — `module IndexerVersion` 구조 변경 (namespace 승격 / `let Current` 의 modifier 추가 등) 시 매치 실패 → exit 1 + 명확한 에러 메시지로 fail-safe. 충분히 안전. → Phase 2 이연. / **(s5d-M3)** fsproj Target `%(LightHouseAssembly.Version)` 멀티 assembly batching — `AssemblyFiles="$(OutputPath)Ds2.LightHouse.dll"` 단일 file 이라 1회 호출 정합. 조치 불필요. / **(s5d-m1)** `Write-Error` + `exit 1` 중복 — `$ErrorActionPreference = 'Stop'` 로 `Write-Error` 가 terminating → 이후 `exit 1` 도달 안 함. 다만 child process exit code 1 보장. PS 5.1 git bash 호출 e2e 검증 후 통일 검토 → Phase 2 이연. / **(s5d-m2)** PowerShell 5.1 호환성 (ReadAllText / ConvertFrom-Json / `[System.Version]` / Regex / `$PSScriptRoot` / Resolve-Path / `[CmdletBinding()]`) 통과 — 조치 불필요. / **(s5d-m3)** `Resolve-Path` 자동추정 실패 시 진단성 — 한 줄 가드 추가 검토 → Phase 2 이연. / **(s5d-m4)** SKILL.md 롤백 표 표현 통일 — Phase 2 이연. (vii) **§4.2 Phase S5 체크리스트 완료**: PromakerToolNames.cs + PromakerToolNamesDriftTests.fs ✓ marker. **§4.2 Phase S5 완전 종결 (100%)**. (viii) **§4.3 미확정 표 결정 박제**: "**paired-release manifest hash 정의**" 행 갱신 — "AssemblyVersion 비교" → "IndexerVersion.Current literal source regex 추출 + indexerVersionRange 정합 검증" (s5d-r0 정정). dist 진입 준비 완료. (단, 본격 IntegrationTests round-trip (MA22) 는 여전히 Phase 2 / dist 직전 이연 상태 — 별 task.) (ix) **결과 검증**: `check-paired-release.ps1` 3 시나리오 (in-range / out-of-range / regex miss) exit code 정확. `dotnet build Ds2.LightHouse` 0 경고/0 오류 (SSOT 주석 추가 후도). `dotnet build Ds2.LightHouseService` 0 경고/0 오류 (Target Message 갱신 후도). PromakerToolNamesDriftTests 회귀 0 (코드 변경 없음 — 확인 only). |
| s5c-r0 | 2026-05-18 | **Phase S5c 진입 — ChatViewModel session 발급 + App exit hook + LightHouseClient singleton + .mcp-config multi-server + 자가 검열 4건 즉시 적용 + commit 대기**. (i) **신규 운영 1** — `Apps/Promaker/Promaker/Knowledge/LightHouseClientHolder.cs` (~150 line) — process singleton SSOT (§3.8 L2-2 / s5b 잔여 우려 1 통일 결정). `EnsureCreated(LlmConfig)` 가 BaseUrl + DPAPI 복호화 PSK 의 SHA256 hash 비교로 재생성 감지. `RegisterSession` / `UnregisterSession` / `LiveSessions` 추적 + `DisposeAllAsync` 가 일괄 DELETE + Dispose. `Invalidate` 가 Settings 변경 시 stale instance 폐기. (ii) **수정 운영 6** — `LightHouseClient.cs` 의 재업로드 endpoint 는 s5b 에서 추가됨 (본 phase 변경 0). `McpConfigWriter.cs` 에 `CreateMulti(IReadOnlyList<McpServerEntry>) + McpServerEntry record` (~52 line, 기존 `Create` 는 wrapper 로 위임). `LlmChatViewModel.cs` 에 `_lightHouseSession` 필드 + `InitializeAsync` 에 `TryCreateLightHouseSessionAsync` (active CollectionId → POST /sessions → token + unknownIds/unindexableIds lazy sync, Q4) + `BuildMcpConfig` (promaker + lighthouse 2 server) + `DisposeAsync` 에 DELETE /sessions (L2-1, holder.Current null 시 의도된 silent skip — review s5c M2 박제) + `ReloadKbConfig` 메서드 신설 (~109 line). `App.xaml.cs` 의 OnExit 에 `LightHouseClientHolder.DisposeAllAsync().Wait(3s)` (§3.8 L2-2 — best-effort + idle TTL backstop). `KbManagerDialog.xaml.cs` 의 `_client` / `_ingest` 필드 폐기 → `CurrentClient` / `CurrentIngest` property 가 매 호출 시점 `holder.EnsureCreated` 재조회 (review s5c M1 — modal 가정 비의존). `ApplicationSettingsDialog.xaml.cs` 의 Save 후 LightHouseService 변경 시 `LightHouseClientHolder.Invalidate` 호출. `LlmChatPanel.xaml.cs` 의 `KbManager_Click` 에 `ConfigChanged=true` → `vm.ReloadKbConfig()` trigger. (iii) **신규 test 1** — `LightHouseClientHolderTests.cs` (~140 line, **9 Fact**: null when service unset / same instance 재호출 / baseUrl 변경 재생성 / psk 변경 재생성 / Invalidate clears / Register/Unregister tracking / DisposeAllAsync no-client 안전 / null when psk missing / null when plain HTTP). (iv) **수정 test 1** — `McpConfigWriterTests.cs` 에 **CreateMulti 2 Fact** 추가 (multi-server emit + reject empty/duplicate). **누적 11 Fact**. (v) **자가 검열 (sub-agent general-purpose)** Critical 0 / Major 2 / Minor 4 → **즉시 적용 2건**: **(S5c-M1)** KbManagerDialog 의 stale `_client` ObjectDisposedException — modal 가정에 의존 → `_client` 필드 제거 + `CurrentClient` / `CurrentIngest` property 가 매 호출 시점 holder 재조회. Settings dialog 가 Invalidate 후 즉시 안전. / **(S5c-M2)** `LlmChatViewModel.DisposeAsync` 의 silent skip 의도 박제 — holder.Current=null 시 새 BaseUrl 의 client 가 stale token 으로 DELETE 시도하는 게 의미 없음 (§3.8 L2-3 idle TTL backstop) 명시. (vi) **Phase 2 / 후속 이연 (s5c-r0 follow-up)**: **(S5c-m1)** App.OnExit 의 serial DELETE → `Task.WhenAll` 병렬화 (현 통상 N=1~2 라 무영향, N≥4 시점 진입) / **(S5c-m2)** `_lastPskHash` 의 salt 없는 SHA256 — first8hex fingerprint 로 entropy 노출 최소화 / **(S5c-m3)** Holder static singleton test 의 xUnit `[Collection("Sequential")]` 명시 — cross-file Holder test 추가 시 / **(S5c-m4)** `McpConfigWriter.ServerName` legacy 필드 deprecate 후보. (vii) **§4.2 Phase S5 체크리스트 완료**: ChatViewModel session 발급 + .mcp-config multi-server + DisposeAsync DELETE + App exit hook + KbManagerDialog → ReloadKbConfig trigger 모두 ✓. **잔여 본격 IntegrationTests round-trip (MA22)** = Phase 2 또는 dist 직전으로 이연 (사유: F# Program.fs production 코드 재사용 불가 — DPAPI / TLS 인증서 / Kestrel HTTPS bind 의존 + LightHouseClient HTTPS-only 검사 우회 mechanism 도입 부담. service.Tests 111 Fact + Promaker.Tests 의 client mock 18 Fact 가 wire-level 정합 cover, *같은 protocol* 검증만 본격 e2e 부담). (viii) **결과 검증**: `dotnet build Promaker` 0 오류 / 2 경고 (transitive, 무관), `Promaker.Tests` **203/203** (+11 vs S5b 192: Holder 9 + CreateMulti 2), service / lib test 회귀 확인 보류 (변경 없음). |
| s5b-r0 | 2026-05-18 | **Phase S5b 진입 — Promaker 통합 2차 (KbManagerDialog + CollectionPackager + AttachmentIngestService + Settings LightHouse Service section) + 자가 검열 4건 즉시 적용 + commit 대기** (Q1=b LLM Chat 진입 버튼 / Q2=A 단일 commit). (i) **신규 운영 4** — `CollectionPackager.cs` (~200 line, §3.3.1 meta.json SSOT camelCase + zip 패키징, atomic move via .tmp suffix), `AttachmentIngestService.cs` (~240 line, copy → Indexer (F# in-process, `Task.Run` wrap) → Packager → LightHouseClient.Upload orchestration + `IProgress<IngestStageProgress>` + CancellationToken + StagingSession lifetime owner), `KbManagerDialog.xaml(.cs)` (~430 line — collection ListView (Active 토글 / 재업로드 / 제거) + folder picker (OpenFolderDialog .NET 8+) + 진행률 ProgressBar/Label/Cancel + consent dialog (§6 m2 T1 PII 의무) + ReconcileLlmConfig (server ↔ LlmConfig sync) + RunIngestAsync wrapper). (ii) **수정 운영 4** — `Promaker.csproj` 에 `Ds2.LightHouse.fsproj` ProjectReference 1줄 (Indexer / KnowledgeBase in-process), `LightHouseClient.cs` 에 `ReuploadCollectionPayloadAsync(POST /collections/{id}/payload)` 추가 (~27 line, D5), `ApplicationSettingsDialog.xaml(.cs)` LLM 탭 안 "LightHouse Service" section (BaseUrl + PSK PasswordBox + "연결 테스트" 버튼 → `LightHouseClient.ListCollectionsAsync` 1회) + dirty save 확장 (~100 line), `LlmChatPanel.xaml(.cs)` Header 에 "📚 KB 관리" 버튼 + `KbManager_Click` 핸들러 (Q1=b). (iii) **신규 test 1** — `Solutions/Tests/Promaker.Tests/CollectionPackagerTests.cs` (~180 line, **9 Fact**: 3 top entry / camelCase + 9 client 필드 + server 필드 미작성 / source 누락 throw / .lighthouse-kb 누락 throw / source 빈 throw / nested path forward-slash / title+indexerVersion validate / cancel / 기존 dest 덮어쓰기). (iv) **수정 test 1** — `LightHouseClientTests.cs` 에 **Reupload 3 Fact** 추가 (id path + Bearer + multipart zip-only / 404 → LightHouseProtocolException + StatusCode / empty id ArgumentException). **누적 12 Fact**. (v) **자가 검열 (sub-agent general-purpose)** Critical 0 / Major 4 / Minor 8 → **즉시 적용 4건**: **(S5b-M4)** `Indexer.ingest` 가 F# 동기 → `AttachmentIngestService` 내부에서 `Task.Run` wrap (`RunIndexerAsync`) — caller (KbManagerDialog) 매번 wrap 누락 회귀 회피, SSOT 단일 진입점 / **(S5b-M2)** `KbManagerDialog.OnClosed` race — `_cts.Cancel()` → in-flight `_inflightIngestTask` await → 그제서야 `_client.Dispose()`. HttpClient.SendAsync 의 ObjectDisposedException 차단 / **(S5b-M1)** ProgressBar 후퇴 — `IsIndeterminate=true` 진입 + Total 보고 시 determinate 전환 + 후퇴 차단 (`newVal > Value` 만 갱신) / **(S5b-M3)** `_client` 단일 기준 null check 통일 (Register/Reupload/Delete) + dead code `_ingest?.GetType()` 제거. (vi) **Phase S5c 이연 (s5b-r0 follow-up)**: **(S5b-m1)** `LightHouseClient` 의 Upload/Reupload multipart 중복 (3+ line 반복) → `SendZipMultipartAsync` helper 추출 / **(S5b-m2)** `Register_Click` / `Reupload_Click` 의 consent + folder validate + RunIngestAsync 패턴 압축 / **(S5b-잔여 우려 1)** `LightHouseClient` ownership SSOT — KbManagerDialog 가 소유 vs ChatViewModel singleton (DI 또는 App.xaml.cs) — Phase S5c 에서 통일 결정 / **(S5b-잔여 우려 2)** `ChatViewModel.ReloadConfig` trigger — KbManagerDialog `ConfigChanged=true` 반환을 caller 가 활용 (현재 무시) — Phase S5c. (vii) **§4.2 Phase S5 체크리스트 갱신**: CollectionPackager / AttachmentIngestService / KbManagerDialog / ApplicationSettingsDialog 4행 ✓ marker. ChatViewModel / App exit hook / IntegrationTests round-trip = Phase S5c. (viii) **결과 검증**: `dotnet build Promaker` 0 오류 / 2 경고 (transitive, 본 phase 무관), `Promaker.Tests` **192/192** (+12 vs S5a 직후 180), service / lib test 회귀 확인 보류 (변경 없음). |
| s5a-r0 | 2026-05-18 | **Phase S5a 진입 — LlmConfig + LightHouseClient + IntegrationTests scaffold + 자가 검열 4건 즉시 적용 + commit 대기**. (i) **수정 운영 2** — `LlmConfig.cs` 에 `KbCollections` (List of `{CollectionId(guid), DisplayName, Active}`) + `LightHouseService` (`{BaseUrl, ApiKeyEncrypted DPAPI(CurrentUser)}`) schema 신설 + `LightHousePskEntropy = "Promaker.LightHouseService.v1"` (LLM API key entropy 와 분리) + `GetLightHousePsk` / `SetLightHousePsk` / `HasLightHousePsk` helper + 두 신규 DTO type (`KbCollectionEntry`, `LightHouseServiceConfig`). `Promaker.csproj` 에 `InternalsVisibleTo("Promaker.Tests")` 추가 (review S5a-m4). (ii) **신규 운영 1** — `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs` (~330 line) — HTTPS-only HttpClient wrapper, `Authorization: Bearer <DPAPI 복호화 PSK>` + `X-User-Identity` 자동 동봉, 5 endpoint (`UploadCollectionAsync` / `ListCollectionsAsync` / `DeleteCollectionAsync` / `CreateSessionAsync` / `DeleteSessionAsync`) + `RecoverSessionAsync` L3 자동 회복 hook + 6 DTO + 2 Exception (`LightHouseAuthException` / `LightHouseProtocolException`). (iii) **신규 test project (scaffold)** — `Solutions/Tests/Ds2.LightHouseService.IntegrationTests/` (fsproj + Program.fs + `ScaffoldSentinelTests.fs` 1 Fact). 2 sln (Promaker.sln / Ds2.sln) 등록. 본격 e2e (WebApplicationFactory round-trip) 는 Phase S5c 진입 시 추가. (iv) **신규 test 1** — `Solutions/Tests/Promaker.Tests/LightHouseClientTests.cs` (~310 line, **15 Fact**: HTTPS-only 3 + 헤더/multipart 2 + List/Delete 2 + Session 2 + 4xx/5xx 4 + L3 hook 2 + 결함 1 + StatusCode 노출 1). (v) **수정 test 1** — `LlmConfigTests.cs` 에 **7 Fact 추가** (KbCollections round-trip / LightHouseService round-trip / default state / DPAPI roundtrip / null clear / entropy 분리). (vi) **자가 검열 (sub-agent general-purpose)** Critical 0 / Major 4 / Minor 10 → **즉시 적용 4건**: **(S5a-M2)** `LightHouseProtocolException` 에 `HttpStatusCode? StatusCode` property 추가 — caller (KbManagerDialog Phase S5b) 가 415 (IndexerVersion gate) / 409 (Conflict) / 5xx 별 사용자 안내 분기 가능 + Fact 1건 (415 UploadCollectionAsync) / **(S5a-m4)** `[InternalsVisibleTo("Promaker.Tests")]` 박제 + test 의 reflection ctor 호출 제거 → internal API 변경 시 compile-time 검출 / **(S5a-M1 docstring)** `UploadCollectionAsync` 의 `zipStream` ownership 명시 — `MultipartFormDataContent.Dispose` 가 child stream 까지 dispose → caller 의 `using FileStream` double-dispose 위험 사용자 안내 / **(S5a-M3 docstring)** `RecoverSessionAsync` 의 caller orchestration SSOT (§3.8 L3 "1회 retry") 명시 — 무한 retry storm 방지. (vii) **Phase S5b/S5c 이연 (s5a-r0 follow-up)**: **(S5a-M1 test)** `MemoryStream` 양도 후 caller 의 `ObjectDisposedException` 시험 1건 — S5b 진입 시 / **(S5a-M4)** `CollectionInfo` 의 server `CollectionEntry` 대비 누락 필드 (CreatedAt / ImportedBy / TotalSourceBytes / StorageRelPath / ImportedAt) → S5b KbManagerDialog 진입 시 표시 필드 SSOT 점검 + forward-compat Fact 1건 / **(S5a-m1)** HTTPS scheme 비교 paranoid double-check 옵션 — backlog / **(S5a-m2)** PSK byte buffer + Array.Clear (S3 IM-5 backlog 와 함께) — Phase S6 보안 hardening / **(S5a-m3)** 메서드별 HttpRequestMessage Timeout override — backlog / **(S5a-m6)** 빈 active set L3 회복 의미 검토 — S5c 진입 시 / **(S5a-m8)** IntegrationTests 의 round-trip 진입 — Phase S5c / **(S5a-m9)** JsonException → LightHouseProtocolException wrap — backlog / **(S5a-m10)** SetApiKey / SetLightHousePsk 의 DPAPI helper refactor — Phase S5b 또는 S6 / **(잔여 우려)** DPAPI entropy v2 마이그레이션 패턴 — backlog (todo §3.7). (viii) **§4.3 미확정 표 결정 박제**: "**LightHouseService config 의 JSON property attribute 적용 = camelCase**" (기존 LlmConfig 의 `[JsonPropertyName("camelCase")]` 명시 정합). "**LlmConfig.KbCollections schema migration = 불필요**" (parent r5 SKIP, 본 phase 가 prod 최초 도입). (ix) **결과 검증**: `dotnet build Promaker` 0 오류 / 5 경고 (기존 transitive 의존 충돌, 본 phase 무관), `Promaker.Tests` **180/180** (+22 vs S4 직후 158), `Ds2.LightHouseService.IntegrationTests` **1/1** (scaffold sentinel), `Ds2.LightHouseService.Tests` **111/111** (회귀 0), `Ds2.LightHouse.Tests` **100/100** (회귀 0). |
| s4-r0 | 2026-05-18 | **Phase S4 진입 — file serving (citation 원문 stream, D6) 풀스택 + 자가 검열 4건 즉시 적용 + commit 대기**. (i) 신규 운영 1 (`FileServing.fs` ~150 line — `contentTypeOf` / `findSourceFile` / `getFile` / `map`). (ii) 수정 운영 2 (`Program.fs` — `FileServing.map` 호출 1줄 추가 + 주석 / `fsproj` Compile 1줄). (iii) 신규 lib 2 함수 (`SqliteStore.findDocumentById` — Documents.Id → (OriginalPath, FileHash, SizeBytes) / `KnowledgeBase.lookupDocument` facade — connection lifecycle 자체 관리, review IM-6 정합). (iv) 신규 test 1 (`FileServingTests.fs` ~310 line, **19 Fact**: contentTypeOf 4 + findSourceFile 5 + getFile 10). (v) **자가 검열 (sub-agent general-purpose)** Critical 0 / Major 5 / Minor 8 → **즉시 적용 4건**: **(S4-M1)** `findSourceFile` path traversal 가드 강화 — prefix-only `OrdinalIgnoreCase` 가 `source` ↔ `source-evil` 형제 디렉토리 false-positive 가능 → `Path.DirectorySeparatorChar` 명시 부착 (paranoid double check 정합 강화) / **(S4-M2)** `If-None-Match: *` RFC 7232 §3.2 정합 — any-match (unconditional 304) 분기 추가 + Fact 1건 / **(S4-m7)** html/htm 의 `text/html` → `application/octet-stream` 강제 다운로드 (citation source 가 inline render 시 `<script>` XSS 위험 차단) + Fact 1건 / **(S4-m1)** `KnowledgeBase.lookupDocument` 의 `catch ex` 박제에 `ex.GetType().Name` 추가 (SQLite 손상 디버깅 가치). (vi) **commit 후 follow-up 박제 (S4 안)**: **(S4-M3)** 200/304 분기의 ETag 헤더 형식 일관성 + test 강화 — 현재 304 만 명시 박제, 200 은 `Results.File` 의 직렬화에 의존 / **(S4-m3)** `userIdentityOf` 의 "unknown" fallback 이 사실 invariant 위반 (AuthMiddleware 통과시 반드시 박제) → `Log.audit.Warn` anomaly 박제 권장 / **(S4-m4)** fileId SSOT 주석에 todo 항목 번호 추가 (§3.10 / §4.2 Phase S4) / **(S4-m6)** 416 (invalid range) / If-Range Fact 1-2건 추가 — ASP.NET Core 위임 정합 박제 / **(S4-m8)** `Path.GetExtension` null-safe 가드 잉여 정리. (vii) **Phase S5 / Phase 2 이연**: **(S4-M4)** `findSourceFile` recursive walk latency — Phase S5 CollectionPackager 의 source/ layout (flat vs nested) 결정 후 단축 검토 / **(S4-M5)** basename + size 매칭의 충돌 위험 (FileHash 미사용) — Phase S5 의 source/ 평면화로 자동 해소 또는 stream open 시 첫 64KB SHA256 prefix 비교 backlog / **(S4-m2)** audit log 폭증 (viewer click 매번 `Log.audit.Info`) — Phase 2 operational hardening (INFO → DEBUG 분기) / **(S4-m5)** `SqliteConnection.ClearPool` 잦은 호출 — Phase 2 lib pool 캐싱 검토. (viii) **결과 검증**: `dotnet build LightHouseService` 0 경고/0 오류, `dotnet test LightHouseService.Tests` **111/111** (S1 24 + S2 41 + S3 27 + **S4 19**), `dotnet test LightHouse.Tests` **100/100** (회귀 0). 이전 92/92 → 111/111 (+19) + lib 100/100 변경 0. |
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

- **모드**: **Phase S6 P5 — IndexerVersion gate 415 Fact 2건 + KnowledgeBase.stampIndexerVersion lib facade + 자가 검열 M1/M2/M3 즉시 적용 — commit 대기** (s6-r5). lib 102 (+2) / IntegrationTests 22 (+2). 누적 **438 Fact**. parent §3.18 facade 표면 확장 (write 대칭 추가).
- **이전 모드** (참고): **Phase S6 P4 — L3 caller orchestration 정책 주석 박제** (commit `66890e7`, s6-r4). LightHouseClient.RecoverSessionAsync hook + LightHouseAuthException + unit test 2 Fact 이미 완비. LlmChatViewModel 의 catch 분기에 주석만 1건 추가.
- **이전 모드** (참고): **Phase S6 P3 — check-paired-release.ps1 s5d 박제 잔여 3건 + 비교 quirk fix** (commit `3a3d89c`, s6-r3). s5d-M1 Assert-NumericVersion + s5d-m1 Fail-Drift + s5d-m3 Resolve-Path 가드 + [System.Version] -ge/-le quirk CompareTo 우회.
- **이전 모드** (참고): **Phase S6 P2 binding Fact** (commit `1cc3f1f`, s6-r2). E2eRoundTripTests Fact 8 — IPv6 [::1] 접속 거부 Kestrel IPv4-only bind 회귀 차단.
- **이전 모드** (참고): **Phase S6 P2** (commit `97a6f1e`, s6-r1) — server-side negative path 7 Fact + ZipBuilders 공용 module 추출 + 자가 검열 M1/M2/m1/m3 즉시 적용.
- **이전 모드** (참고): **Phase S6 P1** (commit `62ecb2b`, s6-r0) — `lighthouse-cli index --upload` 본격 구현 + CliUploadTests 4 Fact + 자가 검열 2건. HTTPS-only + multipart + PSK auth + exit code mapping (D-S6-5).
- **이전 모드** (참고): **Phase S6 scaffold (s5f-r0)** → s5e-r0. `Program.configureApp` pure 함수 export + `ServiceFixture` (in-memory self-signed cert + Kestrel localhost:0) + 6 Fact 본격 e2e (실 Kestrel HTTPS round-trip). 누적 **421 Fact** (Promaker 203 + service 111 + IntegrationTests 7 + lib 100, +6 vs s5d-r0). s5c-r0 (vii) 의 이연 박제 해소 — 우회 mechanism = C 안 (configureApp export + in-memory cert + 신뢰 우회 HttpClient).
- **이전 모드** (참고): s5d-r0 (commit `18d8d90`, Phase S5 종결 + dist 진입 준비 — paired-release drift detector 신설) → s5c-r1 (commit `83cd464` UI 테마 정합) → Phase S5c (commit `79ee30b` s5c-r0). **§4.2 Phase S5 100%** (PromakerToolNames + Drift test fresh + s5d-r0 paired-release + s5e-r0 IntegrationTests 본격 e2e 모두 ✓).
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
| D-S6-1 | CLI 인증 = GUI 와 동일 PSK (별도 PSK 분리 부담 회피, s5f-r0 결정) | §4.2 Phase S6 |
| D-S6-2 | 명령 surface = `lighthouse-cli index <folder> [--upload <url> --psk <key> --title <name>]` | §4.2 Phase S6 |
| D-S6-3 | args parsing = 수동 (System.CommandLine 미사용) — `=` form 미지원은 follow-up | §4.2 Phase S6 |
| D-S6-4 | exit code SSOT = 0 ok / 1 인증 실패 / 2 IndexerVersion mismatch / 3 zip size 초과 / 10 명령행 / 11 폴더 미존재 / 12 IO / 99 기타 | §4.2 Phase S6 |
| D-S6-5 | HTTP status → CLI exit code mapping: 401/403→1, 415→2, 413→3, 기타 비-2xx→99 | §4.2 Phase S6 |
| **D-S7-1** | **mTLS 진입 시점 = Phase S7 우선 (PSK 회전 부담 완화 SSOT)**. client cert = LocalMachine\My 박제 + service trust chain = 사내 CA. PSK 는 fallback 으로 잠시 공존 후 단계적 제거 (사전 박제) | §3.7, §4.2 Phase S7 |
| **D-S7-2** | **SSE `/events` payload schema = `{event:string, collectionId?:string, progress?:number, message?:string}`** newline-delimited JSON, ServerSentEvents text/event-stream. **event enum 후보** (s6-r7 review m1): `index-progress` (Phase 2 caption / OCR 진행률) / `payload-swapped` (collection 재업로드 완료) / `collection-deleted` (D7 purge 알림) / `session-closed` (server idle TTL 도달). SSE protocol `event:` line ↔ JSON `event` 필드 중복 — protocol layer 채택 시점 (Phase S7 진입) 정합 검토 | §3.8, §4.2 Phase S7 |
| **D-S7-3** | **multi-service routing = `LlmConfig.LightHouseServices : List<LightHouseServiceConfig>`** (각 entry 가 BaseUrl + ApiKeyEncrypted + Active flag + DisplayName). KbManagerDialog 가 service tab 분리. session 발급은 active service 별 (사전 박제) | §4.2 Phase S7 |
| **D-S7-4** | **T2/T3 multi-tenant = config-level opt-in** (`config.json` 의 `multiTenant: {mode: "T1" \| "T2" \| "T3"}`). T1 = 현 default flat / T2 = per-user namespace (X-User-Identity 기반 directory prefix) / T3 = collection ACL (registry 의 `acl: {users:[], readOnly:bool}` 행 추가). 진입 trigger = PII 격리 요구 발생 시 (사전 박제) | §3.6, §4.2 Phase S7 |
| **D-S7-5** | **resumable upload = tus-protocol 미채택, 자체 chunked**. POST /collections/staging → returns stagingId / PATCH /collections/staging/{stagingId} {offset, chunk} / POST /collections/staging/{stagingId}/finalize {title}. 수 GB zip 실패 후 offset 부터 재개. **tus 미채택 사유** (s6-r7 review m2): (a) tus.io 의 `Upload-Offset` 헤더 + 조건부 PATCH 모델은 OAuth/CORS preflight 추가 부담 (서버는 LAN 가정, CORS 비대상) / (b) tus client lib (TusClient.NET 등) 의 dependency footprint vs `LightHouseClient.cs` 의 단순 multipart 확장 (~50 line) 균형 / (c) Phase S7 진입 시점에 사용자 실 부담 (수 GB zip 빈도) 검증 후 tus 재진입 가능. 진입 시점 재검토 의무 (deferred) | §4.2 Phase S7 |

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

#### Phase S4 — file serving (citation 원문) *(s4-r0: commit 직전 — 19 Fact 100%)*

**DoD**: `GET /collections/{id}/files/{fileId}` 가 `Collections\<id>\source\` 의 원본 byte stream 반환 (D6). HTTP Range 지원 (대용량 PDF) + ETag = FileHash. Content-Type 추정 OK (PDF / DOCX / XLSX / PPTX / TXT / MD 케이스). 존재하지 않는 fileId 는 404. 권한 (PSK) 없으면 401.

- [x] `GET /collections/{id}/files/{fileId}` — `FileServing.fs` 신설. `Results.File(physicalPath, contentType, fileDownloadName, lastModified, entityTag, enableRangeProcessing=true)` 위임 → ASP.NET Core 의 PhysicalFileHttpResult 가 Range / Last-Modified / Content-Length 자동 처리. ETag = `"<sha256-hex>"` (FileHash). path 의 `{id}` = collection guid, `{fileId}` = `documents.Id` (Int64). AttachmentTools 의 외부 `<guid>:<docId>` 형식을 client (Promaker, Phase S5) 가 split 후 본 endpoint 호출 (MA23).
- [x] `KnowledgeBase.lookupDocument` (lib facade 신설) — single collection 의 `documents.Id` → `(OriginalPath, FileHash, SizeBytes)` lookup. read-only connection 자체 lifecycle (`open → query → close → ClearPool → Dispose`, parent r10 F2 정합). review IM-6 정합 — service 의 SqliteStore 직접 참조 회피.
- [x] `findSourceFile` (source/ 안 basename + size match recursive walk) + path traversal 가드 (separator 명시 부착, review S4-M1)
- [x] `contentTypeOf` (PDF/DOCX/XLSX/PPTX/TXT/MD/CSV/JSON/XML 매핑, html/htm 강제 octet-stream — review S4-m7 XSS 방어)
- [x] If-None-Match 처리 — `*` (RFC 7232 §3.2 unconditional 304) + hash substring match → 304 + body 없음. (review S4-M2)
- [x] viewer 채택 결정 (MA8) → **Phase S5 의 KbManagerDialog citation UX 와 함께 결정** (S5 진입 시 OS default vs 내장 분기). 본 phase 는 server-side stream 만 책임. **§4.3 미확정 표 유지** — "viewer 채택 (citation 클릭)" 행이 그대로 S5 진입 시점 결정으로 남음.
- [ ] (옵션) `GET /collections/{id}/files/{fileId}/thumbnail` — PDF page 0 / Office 파일 첫 슬라이드 등 작은 미리보기 — **Phase S7 옵션** (DoD 미포함, 본 phase 진입 안 함)
- [ ] (옵션) `GET /collections/{id}/files/{fileId}/page/{n}.png` — Phase 2 PDF page 렌더 — **Phase S7 옵션**

**s4-r0 잔여 follow-up (Phase S4 안 + Phase S5/Phase 2 이연)**:
- (S4-M3) 200/304 분기의 ETag 헤더 직렬화 형식 일관성 + test 강화 — S4 안
- (S4-m3) `userIdentityOf` "unknown" anomaly 박제 — S4 안
- (S4-m4) fileId SSOT 주석에 todo 항목 번호 — S4 안
- (S4-m6) 416 / If-Range Fact 추가 — S4 안
- (S4-m8) `Path.GetExtension` 가드 잉여 정리 — S4 안
- (S4-M4) `findSourceFile` recursive walk latency — Phase S5 CollectionPackager 의 source/ layout 결정 후
- (S4-M5) basename + size 충돌 위험 (FileHash 미사용) — Phase S5 와 함께
- (S4-m2) audit log 폭증 — Phase 2 operational hardening
- (S4-m5) `ClearPool` 잦은 호출 — Phase 2 lib pool 캐싱

#### Phase S5 — Promaker (client) 통합 *(S5a: s5a-r0 — commit 직전, S5b/S5c 미진입)*

**S5a DoD** (본 sub-phase): LlmConfig 의 `KbCollections` + `LightHouseService` 신설 + DPAPI roundtrip + `LightHouseClient` 의 protocol contract (HTTPS-only / Bearer / X-User-Identity / 5 endpoint + L3 hook + 4xx/5xx 분기) 통과 + IntegrationTests scaffold 진입.

**S5 전체 DoD**: Promaker 가 service 에 PSK (DPAPI 저장) 로 인증 → KbManagerDialog 에서 폴더 추가 (consent dialog) → 색인 → upload (cancel button 동작) → chat 시작 시 session 발급 → LLM 이 attachment_search 호출 → citation 포함 응답 생성. service kill → 재시작 → 진행 chat 의 다음 호출 자동 회복 (L3). KbManagerDialog 에서 active 토글 → 다음 chat 부터 반영 (L1) 확인. **`Solutions/Tests/Ds2.LightHouseService.IntegrationTests/` 의 client↔server round-trip suite 통과** (MA22). `LlmConfig.cs` round-trip 시 기존 필드와 직렬화 관례 동일 (MA4). `ApplicationSettingsDialog` 의 "연결 테스트" 버튼 동작 확인.

- [x] **(S5a)** `LlmConfig.cs` 확장 — **본 phase 가 KbCollections 최초 도입처** (parent r5 SKIP 으로 prod 미존재):
  - `KbCollections` schema 신설: `List<KbCollectionEntry>` = `{CollectionId(guid), DisplayName, Active}` (§3.4)
  - `LightHouseService` 신설: `{BaseUrl, ApiKeyEncrypted(DPAPI base64, CurrentUser scope)}` (§3.4, §3.7) — **entropy 분리** `"Promaker.LightHouseService.v1"` (LLM API key `"Promaker.LlmApi.v1"` 와 격리, leak surface 분리)
  - atomic save / corrupt fallback 패턴 유지 (기존 LlmConfig.Save 동형)
  - schema migration = **불필요** (§4.3 default c — parent r5 SKIP 으로 `{path, active}` 형태 데이터 prod 미존재)
  - 직렬화 관례 = **camelCase** (`[JsonPropertyName("camelCase")]` 명시, 기존 LlmConfig 정합) — §4.3 미확정 표 결정 박제
- [x] **(S5a)** `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs` 신설 — HTTP client wrapper
  - `UploadCollectionAsync(title, zipStream, CancellationToken)` → `{collectionId(guid)}` (server 가 발급, §3.4 D3)
  - `ListCollectionsAsync()` → CollectionInfo[] (Promaker startup 호출 — Q1)
  - `DeleteCollectionAsync(id)`
  - `CreateSessionAsync(collectionIds)` → `{token, unknownIds[], unindexableIds[]}` (§3.8)
  - `DeleteSessionAsync(token)`
  - 모든 요청에 `Authorization: Bearer <DPAPI-decrypted PSK>` 자동 동봉 + TLS 강제 (plain HTTP 거부, §3.7)
  - **MCP 호출 응답 401/403 시 `CreateSessionAsync` 자동 재발급 + 동일 호출 1회 retry** (CR6, §3.8)
- [x] **(S5b)** `Apps/Promaker/Promaker/Knowledge/CollectionPackager.cs` 신설 — folder → zip (`source/` + `.lighthouse-kb/` + `meta.json` per §3.3.1 SSOT). camelCase, atomic move via `.tmp` suffix.
- [x] **(S5b)** `Apps/Promaker/Promaker/Knowledge/AttachmentIngestService.cs` **신설** — 색인 (Ds2.LightHouse Indexer in-process, `Task.Run` wrap = review S5b-M4) → zip 패키징 → LightHouseClient.UploadCollectionAsync + ReuploadCollectionPayloadAsync. cancel = CancellationToken pass-through + StagingSession finally cleanup. `IProgress<IngestStageProgress>` 통합 (Copying / Indexing / Packaging / Uploading / Done).
- [x] **(S5b)** `Apps/Promaker/Promaker/Dialogs/KbManagerDialog.xaml(.cs)` **신설** (parent r5 SKIP 으로 미존재 — 본 phase 가 *최초 도입*):
  - folder picker (`Microsoft.Win32.OpenFolderDialog`, .NET 8+) → 색인 진행률 (client 측) → upload 진행률 (HTTP) → 완료
  - active 토글 → `LlmConfig.KbCollections[i].Active` 변경만 (server 무영향, 다음 chat 부터 반영) — chip 안내 박제
  - **consent dialog 강제** — `Register_Click` 안 매 등록마다 (§6 m2 SSOT)
  - service 미연결 시 StatusChip 안내 ("LightHouse Service 미설정 — 설정 > LLM 탭에서 BaseUrl + PSK 입력 후 \"연결 테스트\" 통과 필요")
  - `ReconcileLlmConfig` — server registry ↔ LlmConfig.KbCollections sync (stale entry 폐기 + 새 entry 추가). Q1 / Q4 정합.
  - 진입점 = LLM Chat 패널 Header "📚 KB 관리" 버튼 (Q1=b 결정, `LlmChatPanel.xaml.cs:KbManager_Click`)
  - **review M2 OnClosed race fix** — `_inflightIngestTask` 박제 + await 후 `_client.Dispose()` (HttpClient.SendAsync ObjectDisposedException 차단)
  - **review M1 ProgressBar 후퇴 fix** — `IsIndeterminate=true` 진입 + Total 보고 시 determinate 전환 + `newVal > Value` 갱신 가드
- [x] **(S5b)** `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml(.cs)` 확장 — LLM 탭에 "LightHouse Service" section (BaseUrl / PSK PasswordBox + DPAPI 암호화 저장 via `LlmConfig.SetLightHousePsk`) + **"연결 테스트" 버튼** (`LightHouseClient.ListCollectionsAsync` 1회 → 결과 chip). dirty save 확장 — BaseUrl + PSK 비교, 양쪽 빈 값이면 `LightHouseService` config 자체 제거.
- [x] **(S5c)** `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` 갱신:
  - `InitializeAsync` 의 `TryCreateLightHouseSessionAsync` — `LightHouseClient.CreateSessionAsync` 호출 + `unknownIds` (LlmConfig 정리 + Save) / `unindexableIds` (chip 안내, 재시도 가능) sync (§3.8 Q4)
  - `BuildMcpConfig` — `.mcp-config` 작성 시 promaker (loopback nonce) + lighthouse (LAN service URL + `Authorization: Bearer <psk>` + `X-LightHouse-Session: <token>`) 두 server (Phase S5c — `McpConfigWriter.CreateMulti`)
  - chat panel close / `DisposeAsync` 시 `DeleteSessionAsync` (L2-1). holder.Current null 시 의도된 silent skip — server idle TTL backstop (review s5c M2 박제).
  - `ReloadKbConfig` 메서드 — KbManagerDialog close 후 caller (LlmChatPanel.KbManager_Click) 가 ConfigChanged=true 시 호출. `_config` 만 새로 load (현 session 영향 0, §3.8 L1).
- [x] **(S5c)** `Apps/Promaker/Promaker/App.xaml.cs` process exit hook — `LightHouseClientHolder.DisposeAllAsync().Wait(3s)` 가 살아있는 token 일괄 DELETE + client Dispose (L2-2). best-effort + idle TTL backstop.
- [x] **(S5c)** `Apps/Promaker/Promaker/Knowledge/LightHouseClientHolder.cs` **신설** — process singleton SSOT. `EnsureCreated(LlmConfig)` 가 BaseUrl + PSK 변경 감지로 재생성. `RegisterSession` / `UnregisterSession` 추적 + `DisposeAllAsync`. `ApplicationSettingsDialog` 가 Save 후 LightHouseService 변경 시 `Invalidate` 호출. `KbManagerDialog` 의 `_client/_ingest` 필드 폐기 → 매 사용 시점 `CurrentClient/CurrentIngest` property 가 holder 재조회 (review s5c M1 — stale ObjectDisposedException 차단).
- [x] **(s5d-r0)** `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs` — attachment_* 4종 **추가 안 됨** fresh 확인 (현 6종 풀세트 lock-in = find_by_name / validate_model / apply_model_doc / validate_model_doc / export_model_doc / json_to_yaml). parent r5 SKIP 으로 *애초 추가 안 됨* → 본 phase 에서도 변경 0
- [x] **(s5d-r0)** `Solutions/Tests/Ds2.LlmAgent.Tests/PromakerToolNamesDriftTests.fs` — `expectedSet` **변경 없음 확인** (set equality v4 정합, 동일 6종 lock-in). grep + 확인만, 코드 변경 0
- (parent r5 SKIP 후) `Apps/Promaker/Promaker/LlmAgent/Tools/AttachmentTools.cs` — 본 phase 에서도 **만들지 않음** (service 측 신설, §4.2 Phase S3)
- (parent r5 SKIP 후) `LlmTurnContext.cs` 의 `KnowledgeBase` 필드 — 본 phase 에서도 **만들지 않음** (read-path 가 service 측이라 회피)

#### Phase S6 — CLI 도구 (옵션, 회사 IT 운영용)

**DoD**: `lighthouse-cli index <folder> --upload <url> --psk <key> --title "..."` 무인 동작으로 색인 + upload 완료 + exit code 0. 인증 실패 / IndexerVersion mismatch / zip 크기 초과 케이스 별 비-0 exit code + stderr 안내.

- [x] `Solutions/Tools/Ds2.LightHouse.Cli/Ds2.LightHouse.Cli.fsproj` *(s5f-r0 scaffold — 본 turn)*
  - `Ds2.LightHouse` write-path 사용 (Indexer + TextExtractor/PdfExtractor/OoxmlExtractor)
  - `lighthouse-cli index <folder>` (색인만, exit code 0/11) + `--version` + usage 출력
  - GUI 없이 batch 색인 (upload 는 follow-up)
- [ ] **follow-up — `lighthouse-cli index <folder> --upload <url> --psk <key> --title "..."`** (s5f-r0 박제: 본 turn 미구현)
  - 인증 실패 exit 1 / IndexerVersion mismatch exit 2 / zip size 초과 exit 3
  - LightHouseClient F# 구현 (Promaker C# LightHouseClient 의 wire 정합)
- [ ] (옵션) `lighthouse-cli sync` — registry 기준으로 stale collection 정리 등 운영 명령

#### Phase S7 — 후속 (선택)

**DoD**: 항목별 채택 시점에 결정. mTLS 도입 시 PSK 회전 부담 완화 확인. SSE `/events` 도입 시 KbManagerDialog 의 polling 제거. Resumable upload 도입 시 수 GB zip 실패 후 재개 통과.

**사전 박제 (s6-r7)**: D-S7-1~5 (§0 D-id 표 참조) — 진입 전 사용자 confirm 항목. 본 phase 진입 시 본 박제를 기점 SSOT 로 사용.

- [ ] **D-S7-1 mTLS** — PSK 회전 부담 완화 SSOT. client cert = LocalMachine\My + service trust chain = 사내 CA. PSK fallback 공존 후 단계적 제거.
- [ ] **D-S7-2 SSE `/events`** — payload schema 박제 (`event/collectionId?/progress?/message?` ndjson, text/event-stream). Phase 2 vision caption 진행률 + KbManagerDialog polling 대체.
- [ ] **D-S7-3 multi-service routing** — `LlmConfig.LightHouseServices: List<>` (각 entry = BaseUrl + ApiKeyEncrypted + Active + DisplayName). KbManagerDialog service tab 분리, session 발급은 active service 별.
- [ ] **D-S7-4 T2/T3 multi-tenant** — `config.json` 의 `multiTenant.mode: "T1"|"T2"|"T3"` opt-in. PII 격리 요구 발생 시 진입.
- [ ] **D-S7-5 resumable chunked upload** — tus-protocol 미채택, 자체 SSOT. POST /staging → PATCH offset → POST finalize. SSE 진행률 결합.
- [ ] **(S7-P5b)** s6-r6 잔여 박제 — `postCollections` TooLow `suggestedAction` 비대칭 정비 (현 client lib 업그레이드 한쪽만 — service 다운그레이드 운영 정책상 의미 낮아 의도적). s6-r7 (M1) 의 swap 경로는 정합 완료.
- [ ] connection pool (Q3 격리 정책 완화) — 메모리 압박 시점에 검토
- [ ] L3 auto-recovery MCP relay 통합 — `ExecuteWithSessionRetryAsync` facade (Promaker s6-r6 박제) 실 caller. MCP HTTP proxy 도입 시 가치 발생.

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
| ~~`LightHouseService` config 의 JSON property attribute 적용 (PascalCase 직렬화 vs camelCase)~~ | §3.4 / Phase S5 | ~~grep 으로 기존 LlmConfig 필드 직렬화 관례 확인 후 정합~~ | **s5a-r0 결정**: camelCase — 기존 LlmConfig 의 모든 필드가 `[JsonPropertyName("camelCase")]` 명시 정합. `KbCollectionEntry` / `LightHouseServiceConfig` 두 신 type 동일 패턴. Phase S5a DoD round-trip Fact 7건 통과 (MA4) | — |
| ~~⚠ P2: `ModelContextProtocol.AspNetCore` 버전~~ | §4.2 Phase S3 | ~~parent 결정 그대로 align~~ | **s3-r0 결정 (D-S3-2)**: 1.2.0 (Promaker 와 동일, parent r7 박제 정합). 1.3.0 업그레이드는 양쪽 동시 진입 별도 PR 보류 | parent §4.1 |
| `lighthouse` endpoint version prefix (`/v1/collections` 등) | §3.9, mn13 | 도입 권장 (서비스 v2 시 dual host 여지) | S2 진입 | — |
| viewer 채택 (citation 클릭) — OS default app vs 내장 | §4.2 Phase S4/S5, MA8 | PDF/DOCX/XLSX = OS default, TXT/MD = 내장 | S5 진입 | — |
| ~~paired-release manifest hash 정의 (PE 헤더 vs InformationalVersion vs 별도 manifest)~~ | MA7, §3.12, §6 m1 | ~~s1-r0 결정: `Ds2.LightHouse.dll` 의 `Version` (AssemblyVersion) 비교~~ | **s5d-r0 정정 (Errata)**: AssemblyVersion 비교는 의미 없음 (`Solutions/Directory.Build.props` 가 AssemblyVersion 미주입 → lib dll AssemblyVersion=1.0.0.0 default, F# `[<Literal>]` compile-time inline 으로 reflection 불가). **SSOT = `Ds2.LightHouse.SqliteStore.IndexerVersion.Current` literal (source regex 추출) ∈ service `config.json.template` 의 `indexerVersionRange.[min, max]`**. `Apps/Promaker/scripts/check-paired-release.ps1` 신설 + `/dist` skill Step 3.5 호출 + Service fsproj `PairedReleaseCheck` Target = informational message only (drift 강제는 dist 책임) | — |
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

## 7. 다음 세션 첫 행동 (이어받기 SSOT)

**현 상태 (s5c-r1 commit 직후 갱신 예정 / 본 단원이 handover 단일 진입점)**:
- 본 phase 의 전체 Phase S5 본체 task **모두 ✓** (S5a/b/c/d/e 누적 commit).
- **service e2e 실 부팅 검증 완료** — `make install` → `sc start Ds2.LightHouseService` → `curl https://localhost:8443/collections` 200 OK + JSON body.
- **본격 IntegrationTests round-trip (MA22) = s5e-r0 박제 해제 완료** — `ServiceFixture` (in-memory self-signed cert + Kestrel localhost:0) 위에서 6 Fact 본격 e2e (PSK auth 3종 + healthz + GET /collections + POST /collections → list → /status → POST/DELETE /sessions → DELETE /collections round-trip). 7/7 통과 (sentinel 포함).
- **새 세션 진입 시 본 §7 + §0 / §3 / §4.2 / §4.3 정독 충분** (별도 handover 문서 없음 — 본 §7 가 SSOT).

### 7.1 commit 누적 chain

| commit | rev | 내용 |
|---|---|---|
| `1be3ab8` | s1-r0 | S1 service host scaffold (24 Fact) |
| `8661fb5` | s2-r0 | S2 collection 관리 API (37 Fact) |
| `986f533` | s3-r0/r1 | S3 session + MCP search host (27 Fact + IC-1~2 + IM-3/6/8/11/10) |
| `273007c` | s4-r0 | S4 file serving (19 Fact) |
| `82b4e6a` | s5a-r0 | S5a Promaker LlmConfig + LightHouseClient + IntegrationTests scaffold (22 Fact) |
| `5c8da12` | s5b-r0 | S5b KbManagerDialog + CollectionPackager + AttachmentIngestService (12 Fact) |
| `79ee30b` | s5c-r0 | S5c ChatViewModel session + Holder singleton + multi-server .mcp-config + App exit hook (11 Fact) |
| `83cd464` | s5c-r1 | UI 테마 정합 + Makefile install/light-house + howto 문서 + .ps1 BOM + 사용자 e2e 검증 박제 |
| `18d8d90` | s5d-r0 | Phase S5 종결 + dist 진입 준비 (paired-release drift detector 신설) + s1-r0 Errata + 자가 검열 1건 |
| `4593d04` | s5e-r0 | 본격 IntegrationTests round-trip (MA22) 박제 해제 — Program.configureApp export + ServiceFixture (in-memory cert + Kestrel localhost:0) + 6 e2e Fact |
| `2d45c88` | s5f-r0 | Phase S6 scaffold (`Ds2.LightHouse.Cli`) + s5e follow-up 3건 (M2/m5/I — 8 Fact) + N3-A MSB3277 해소 + N5 parent §3.15.5 흡수 |
| `62ecb2b` | s6-r0 | Phase S6 P1 — `lighthouse-cli index --upload` + LightHouseClient.fs + Packager.fs + CliUploadTests 4 Fact + 자가 검열 C1/M1 즉시 적용 |
| `97a6f1e` | s6-r1 | Phase S6 P2 — server-side negative path 7 Fact (F1~F7) + ZipBuilders 공용 module 추출 + E2eRoundTripTests local helper 흡수 + 자가 검열 M1/M2/m1/m3 즉시 적용 |
| `1cc3f1f` | s6-r2 | (b) IPv6 [::1] 접속 거부 Fact — Kestrel IPv4 단일 bind 정합 |
| `3a3d89c` | s6-r3 | (d) check-paired-release.ps1 s5d 박제 잔여 3건 + [System.Version] 비교 quirk CompareTo 우회 |
| `66890e7` | s6-r4 | (c) L3 caller orchestration 정책 주석 박제 (LlmChatViewModel) |
| `e91b2e7` | s6-r5 | (a) IndexerVersion gate 415 Fact 2건 (F8/F9) + KnowledgeBase.stampIndexerVersion lib facade + 자가 검열 M1/M2/M3 즉시 적용 |
| `a1c0c90` | s6-r7 | **1+2+3+P3 묶음** — (1) doc 정합 정비 (§7.1 row commit hash 갱신 + 변경 이력 표 s6-r1~r6 박제 누락 row 추가) / (2) M1 정정 — `CollectionEndpoints.postCollectionPayload` swap 경로 TooLow/TooHigh 분기에 `hostingRange` + `suggestedAction` SSOT 4 키 박제 + Log.audit.Warn 정합 + IntegrationTests 415 Fact 2건 추가 (24/24) / (3) Phase S7 사전 설계 박제 (D-S7-1~5 = mTLS / SSE schema / multi-service routing / T2/T3 mode / resumable upload) + §4.2 Phase S7 task 표 갱신 / (P3) parent §3.15.5 Phase 2 default 10건 사용자 confirm 박제 + parent §4 Phase 2 진입 marker. 본 turn 코드 변경 = service 1 파일 + test 1 파일 + doc 3 파일. 자가 검열 sub-agent (Critical/Major 0 / Minor 4) → m1 (SSE event enum 보강) + m2 (tus 미채택 사유 박제) 즉시 적용. |
| (본 commit) | s6-r9 | **외부 --inspect-diff 7 reviewer 종합 review 처리** — Critical 6 중 4건 즉시 적용 + Major 26 + Minor 50+ 분류 박제. (i) **K1** ZipImport.fs `swapCollectionPayload` 의 `backup = target + ".bak"` 고정 suffix → `target + ".bak-<guid12>"` per-호출 unique 변경 (동시 swap race 차단 — R2 합의). (ii) **K2** Program.fs builder 에 `services.Configure<FormOptions>` 박제 — `MultipartBodyLengthLimit = cfg.MaxUploadBytes` + `ValueLengthLimit = Int32.MaxValue` (ASP.NET default 134MB ↔ cfg 10GB 정합, R5 합의). (iii) **K3** config.json.template `listenUrl` default `0.0.0.0:8443` → `127.0.0.1:8443` + `_listenUrl_note` 박제 (R6 합의 외부 IP override 의무 안내). (iv) **K5** SessionRegistry.fs `purgeCollectionFromSessions` + `OnPayloadSwapped` — lock 안에서는 active 셋 갱신 + KB ref snapshot 만, 실 `kb.Dispose()` 는 lock 밖 (R5 합의 N×search-time block 차단). (v) **K4/K6 + Major 26건 + Minor 50+ 분류 박제 (§7.7)** — K4 Protocol SSOT 통합 = Phase S7 묶음 / K6 + M9/M10/M11/M12 = 보안 sweep 1턴 / M1+M2 ClearPool / withConnection helper = Phase S7 / M5 415 structured 분기 = 별 turn (s6-r10 후보) / M7~M26 outlier = 각 specialist 관점 backlog 분류. (vi) **반론 (기각 3건)**: M8 (dist SKILL.md installer/ path drift) = parent r4 deploy repo SSOT 정합 / M18 (MaxAttachedDbs SSOT drift) = KnowledgeBase 의 박제가 SqliteStore 직접 참조 / M21 (stampIndexerVersion test-only API) = 향후 server-side restore tool 진입 시 박제. (vii) **결과 검증**: service Tests 115/115 + IntegrationTests 24/24 + lib Tests 109/109 회귀 0. 회귀 차단 Fact (K1 unique suffix / K2 FormOptions e2e) = s6-r9 follow-up 의무. |
| `13e4f93` | s6-r8 | **Phase 2 task A 본격 진입 — schema 확장** (parent §4 Phase 2 첫 task). (i) **수정 운영 1** — `Solutions/Core/Ds2.LightHouse/SqliteStore.fs` (+55/-2): `IndexerVersion.Current` 1.0.0→**1.1.0** (minor — backward-compat) + `SchemaVersion` 1→**2** (parent §3.17 정합). `schemaSql` 확장 = `ImageCache` 테이블 (PK ImageHash, 8 컬럼) + `ImageReferences` 테이블 (복합 PK 4 키 (DocumentId, ImageHash, RefLocator, Ordinal) + FK 3종 Documents/Chunks/ImageCache) + `IX_ImgRef_Chunk` index. (ii) **신규 함수 1** — `ensureColumn conn table column ddl` (SQLite의 `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` 미지원 → `PRAGMA table_info` 로 idempotent 분기). `ensureSchema` 가 `Chunks.ImageCount` DEFAULT 0 forward-compat ALTER 동반. (iii) **신규 test 7 Fact + 자가 검열 M1 보강 1 assertion** — `SqliteStoreTests.fs` (+125 line): IndexerVersion bump 검증 / ImageCache 8 컬럼 / ImageReferences 복합 PK + FK 3종 / Chunks.ImageCount DEFAULT 0 INSERT 검증 / IX_ImgRef_Chunk / ensureColumn idempotent / Phase 1 DB → ensureSchema forward-compat upgrade (+ M1: stampVersion 후 Meta.schema_version="2" / indexer_version="1.1.0" 일치 assertion). (iv) **자가 검열 (sub-agent general-purpose)** Critical 0 / Major 1 (M1 = forward-compat Fact 의 Meta.schema_version stale 가능성 보강 — 즉시 적용) / Minor 3 (m1 minor bump 재색인 정책 박제 backlog / m2 reader.Close() 명시성 / m3 PRAGMA table_info column 위치 hardcode = safe). **즉시 적용 1건 (M1 보강)**. (v) **결과 검증**: lib Tests **102→109** (+7) + service Tests 115/115 + IntegrationTests 24/24 회귀 0 + paired-release detector pass (1.1.0 ∈ [1.0.0, 1.99.99]). **누적 456 Fact** (Promaker 208 + service 115 + IT 24 + lib 109, +7 vs s6-r7). (vi) **parent 박제**: `todo-lighthouse-kb-index.md` §4 Phase 2 첫 task ✓ marker. | — (1) doc 정합 정비 (§7.1 row commit hash 갱신 + 변경 이력 표 s6-r1~r6 박제 누락 row 추가) / (2) M1 정정 — `CollectionEndpoints.postCollectionPayload` swap 경로 TooLow/TooHigh 분기에 `hostingRange` + `suggestedAction` SSOT 4 키 박제 + Log.audit.Warn 정합 + IntegrationTests 415 Fact 2건 추가 (24/24) / (3) Phase S7 사전 설계 박제 (D-S7-1~5 = mTLS / SSE schema / multi-service routing / T2/T3 mode / resumable upload) + §4.2 Phase S7 task 표 갱신 / (P3) parent §3.15.5 Phase 2 default 10건 사용자 confirm 박제 + parent §4 Phase 2 진입 marker. 본 turn 코드 변경 = service 1 파일 (CollectionEndpoints.fs) + test 1 파일 (NegativeRoundTripTests.fs) + doc 3 파일. 자가 검열 trigger 미충족 (단일 파일 100 line+ 미만, 신규 함수 0, control flow 변경 0). |
| `c2f0866` | s6-r6 | **P5/P6/P2-r3 묶음** — (P5) TooHigh suggestedAction 의미론 정정 (service 업그레이드 OR client lib 다운그레이드 양 옵션) + F9 Fact 강화 / (P6) S4 follow-up 5건 — userIdentityOf "unknown" Log.audit.Warn + fileId §3.10/§4.2/MA23 SSOT 박제 + contentTypeOf 가드 유지 사유 박제 + 304 ETag 일관성 Fact + 416/If-Range Fact 3건 / (P2-r3) `LightHouseClient.ExecuteWithSessionRetryAsync<T>` facade + 5 Fact (Phase S7 또는 future MCP relay 진입용, 본 phase facade only) + 자가 검열 M2 (OCE catch) + m2 (.NET 9 위임 가정 박제) 즉시 적용 |

**테스트 누적**:
- Promaker.Tests: S4 158 → S5a 180 → S5b 192 → S5c 203 → s6-r6 208 (s6-r7/r8/r9 회귀 0)
- IntegrationTests: S5a 1 → S5e 7 → s5f 8 → s6 12 → s6-r1 19 → s6-r2 20 → s6-r5 22 → s6-r7 24 (s6-r8/r9 회귀 0)
- service Tests: 111 → s6-r6 115 (s6-r7/r8/r9 회귀 0)
- lib Tests: 100 → s6-r5 102 → s6-r8 109 (s6-r9 회귀 0)
- **누적 456 Fact** (Promaker 208 + service 115 + IntegrationTests 24 + lib 109). s6-r9 = 회귀 0 + 신규 Fact 0 (review 처리만, follow-up Fact 별 turn 의무).

### 7.2 새 세션 진입 즉시 행동

```bash
cd /f/Git/ds2/light-house
git status                              # 미commit 변경 검토 (있으면 §7.3 참조)
git log --oneline -8                    # commit chain 검증
cat Apps/Promaker/Docs/howto-connect-lighthouse-service.md  # 사용자 가이드
```

### 7.3 ~~s5c-r1 미commit 박제~~ → **commit 완료 (s5c-r1 / s5d-r0 / s5e-r0)**

`83cd464` (s5c-r1) + `18d8d90` (s5d-r0) commit 완료. s5e-r0 = 본 transfer 시점에 commit 대기 (`Apps/Promaker/Docs/todo-lighthouse-kb-server.md` + `Solutions/Tests/Ds2.LightHouseService.IntegrationTests/{ServiceFixture.fs,E2eRoundTripTests.fs}` 신규 + `Ds2.LightHouseService.IntegrationTests.fsproj` + `Solutions/Tools/Ds2.LightHouseService/Program.fs` 수정). 본 단원 이하는 s5c-r1 commit 진입 시점 박제이며 historical 참고용.

**(historical) s5c-r1 미commit 산출물 (10 파일)**:
- 신규 doc (2): `howto-connect-lighthouse-service.md` (사용자 가이드), `(handover 폐기 — 본 §7 통합)`
- 신규 운영 (1): `Solutions/Tools/Ds2.LightHouseService/scripts/generate-dev-cert.ps1` (~85 line, self-signed PFX)
- 수정 운영 (3): `Apps/Promaker/Promaker/Controls/Llm/LlmChatPanel.xaml` (KB 관리 버튼 ChatButtonStyle 박제), `Apps/Promaker/Promaker/Dialogs/KbManagerDialog.xaml` (Border.Resources 의 GridViewColumnHeader/ListViewItem dark theme), `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml(.cs)` (Border.Resources 의 TextBox/PasswordBox dark theme + SuccessBrush/FailureBrush 동적 색상 + SetTestResult helper)
- 수정 운영 (1): `Apps/Promaker/Makefile` (LH_* 변수 + install/light-house target + help, service 이름 `Ds2.` prefix 정정, echo ASCII 정합)
- 수정 ps1 (2 BOM 추가): `install-service.ps1`, `uninstall-service.ps1` (PowerShell 5.1 cp949 fallback 차단)
- 수정 doc (1): `todo-lighthouse-kb-server.md` (본 §7 갱신)

**권장 commit message (단일)**:
```
s5c-r1 UI 테마 정합 + Makefile dev install/run + howto 문서 + .ps1 UTF-8 BOM

- LlmChatPanel "KB 관리" Button + KbManagerDialog GridViewColumnHeader/ListViewItem dark theme
- ApplicationSettingsDialog 의 TextBox/PasswordBox dark theme (Border.Resources implicit Style)
- 연결/Ollama 테스트 결과 동적 색상 (SuccessBrush #4FC3F7 / FailureBrush #EF5350) + SetTestResult helper
- Makefile install (cert+publish+register) / light-house (console run) + help + Ds2. prefix 정정
- generate-dev-cert.ps1 신설 (LocalMachine self-signed PFX, dev/PoC)
- install/uninstall/generate-dev-cert.ps1 에 UTF-8 BOM (PowerShell 5.1 cp949 fallback 차단)
- howto-connect-lighthouse-service.md 신설 (사용자 setup 가이드, 트러블슈팅 박제)
- todo-lighthouse-handover.md 폐기 → 본 §7 단일화
```

### 7.4 다음 세션 진입 후 권장 작업 (우선순위) — s6-r1 transfer 시점 갱신

**즉시 진입 가능 — Phase S5 100% + Phase S6 P1/P2 완료 + Phase 2 사전 결정 박제 흡수 + dist 진입 준비 완료 + server-side negative path 회귀 차단 19 Fact**.

**N1 (재진입). dist 본격 실행** — `/dist` skill **사용자 직접 호출** (외부 영향: scp + tag + push, 본 세션 자체 호출 안 함). paired-release drift detector + IntegrationTests 19 Fact + 자가 검열 통과 = dist 직전 안전망 제공.

**처리 완료 (s5f-r0)**:
- N2 (s5e follow-up): M2 + m5 + I 3건 적용 — IntegrationTests 8 Fact 통과
- N3-A (parent r12 MSB3277 해소): OpenXml 3.5.1 직접 노출
- N3-B (todo git mv): 사용자 결정 취소
- N4 (Phase S6 scaffold): `lighthouse-cli index` 색인만 + smoke test 통과
- N5 (Phase 2 plan): parent §3.15.5 sub-section 흡수 (별 doc 폐기)

**처리 완료 (s6-r0)**:
- (P1) Phase S6 P1 — `lighthouse-cli index --upload` 본격 구현 + LightHouseClient.fs + Packager.fs + CliUploadTests 4 Fact + 자가 검열 C1/M1 즉시 적용. IntegrationTests 8→12.

**처리 완료 (s6-r1)**: server-side negative path 7 Fact (F1~F7) + ZipBuilders 공용 module 추출.

**처리 완료 (s6-r2)**: (b) IPv6 [::1] 접속 거부 Fact — E2eRoundTripTests Fact 8. dual-stack bind 회귀 차단.

**처리 완료 (s6-r3)**: (d) check-paired-release.ps1 s5d 박제 잔여 3건 — Assert-NumericVersion / Fail-Drift / Resolve-Path 가드 + [System.Version] 비교 quirk CompareTo 우회 (의도치 않은 bug fix).

**처리 완료 (s6-r4)**: (c) L3 caller orchestration 정책 주석 — LightHouseClient.RecoverSessionAsync hook + unit test 2 Fact 이미 완비. LlmChatViewModel catch 분기에 의도 박제 주석만 추가 (session 발급 자체 401 = retry 무의미, L3 sweet spot 은 MCP token 만료 시나리오).

**처리 완료 (s6-r5)**: (a) IndexerVersion gate 415 Fact 2건 (F8 TooLow / F9 TooHigh) + KnowledgeBase.stampIndexerVersion lib facade 신설 + lib unit test 2 Fact + 자가 검열 M1/M2/M3 적용.

**처리 완료 (s6-r6, 본 turn)**: P5 + P6 + P2-r3 묶음 (사용자 "1~3 진행").
- **(P5)** CollectionEndpoints.fs `postCollections` TooHigh `suggestedAction` 의미론 정정 — `"service 업그레이드"` → `"service 업그레이드 또는 client Ds2.LightHouse lib 다운그레이드 후 재색인 / 재업로드"` (양 회복 경로 박제) + NegativeRoundTripTests F9 Fact 에 양 옵션 substring 검증 4줄 추가.
- **(P6)** FileServing.fs S4 follow-up 5건 — (S4-m3) `userIdentityOf` "unknown" fallback 시 `Log.audit.Warn` 박제 (invariant 위반 anomaly) / (S4-m4) fileId Int64 parse 위치에 §3.10 / §4.2 Phase S4 / MA23 SSOT 항목 박제 / (S4-m8) `contentTypeOf` `String.IsNullOrEmpty` 가드 *잉여 정리 거부* 사유 박제 (Path.GetExtension(null) → NRE 차단) / (S4-M3) 304 ETag 헤더 박제 Fact 1건 / (S4-m6) 416 invalid Range + If-Range ETag match/mismatch Fact 3건. service Tests **115/115** (+4).
- **(P2-r3)** LightHouseClient.cs `ExecuteWithSessionRetryAsync<T>` facade 신설 (~62 line) — caller op 가 LightHouseAuthException throw 시 RecoverSession 1회 + retry. 의도된 사용처는 Phase S7 또는 future MCP relay (본 phase facade only — Promaker 자체 호출은 PSK 사용이라 401 retry 무의미). LightHouseClientTests **5 Fact** 추가 (provider 미설정 / 첫 호출 성공 / 401 → recover + retry 성공 / recover 자체 401 → throw / OCE 즉시 전파). Promaker.Tests **208/208** (+5).
- **자가 검열 (sub-agent general-purpose)** Major 2 / Minor 3 → **즉시 적용 2건**: **(M2)** `ExecuteWithSessionRetryAsync` 에 `catch (OperationCanceledException) { throw; }` 박제 + 회귀 차단 Fact 1건 / **(m2)** FileServingTests 의 416/If-Range 3 Fact 위에 `.NET 9 Results.File 위임 가정` 1줄 박제.

**잔여 박제 (s6-r6 follow-up — 별 turn S7-P5b 또는 Phase 2 이연)**:
- **(s6-r6 M1)** P5 비대칭 잔존 — `postCollections` TooLow 분기 `suggestedAction` 은 client lib 업그레이드 한쪽만 박제 (운영 정책상 service 다운그레이드 옵션 의미 낮아 의도적). `postCollectionPayload` (재업로드 swap) 의 TooLow / TooHigh 분기 두 곳은 `suggestedAction` 키 자체 부재 — SSOT 불일치. 별 turn S7-P5b 일괄 정비.
- **(s6-r6 m1)** P6-m3 `Log.audit.Warn` storm 위험 — AuthMiddleware 결함 발생 시 매 file GET 마다 1 warn. 통상 0 호출 기대 + 단일 invariant 위반 박제 의도라 본 phase 차단 사유 아님. Phase S7+ rate-limit policy 도입 시 일괄 처리.
- **(s6-r6 m3)** P2-r3 docstring 의 "401/403" → `LightHouseAuthException` 이 둘 다 매핑되어 정합이나 caller 가독성 minor. backlog.

**처리 완료 (s6-r7, 본 turn)**: 1+2+3+P3 묶음.
- **(1)** doc 정합 정비 — §7.1 표 s6-r6 row commit hash `c2f0866` 박제 + 변경 이력 표 (top) 의 s6-r1~r6 누락 박제 row 1건 추가 (SSOT = §7.1).
- **(2)** s6-r6 M1 정정 — `postCollectionPayload` swap 경로 TooLow/TooHigh 분기에 `hostingRange` + `suggestedAction` SSOT 4 키 박제 (postCollections 와 일관) + `Log.audit.Warn` 정합. NegativeRoundTripTests **2 Fact 추가** (swap 415 too-low + too-high SSOT 박제 검증). IntegrationTests **24/24** (+2). F# task CE 의 finally 안에서 `let!` 사용 불가 debugging 박제 (try 끝에 명시 cleanup 으로 변경).
- **(3)** Phase S7 사전 설계 박제 — §0 D-id 표에 **D-S6-1~5** (이미 박제된 CLI 결정) + **D-S7-1~5** (mTLS / SSE schema / multi-service routing / T2/T3 mode / resumable upload) 신설. §4.2 Phase S7 task 표 갱신 (사전 박제 marker + S7-P5b 흡수 + L3 MCP relay 통합 task 추가).
- **(P3)** Phase 2 진입 confirm — parent §3.15.5 의 10건 default 사용자 confirm 박제 (✓ s6-r7) + parent §4 Phase 2 진입 marker (첫 task = schema 확장). **본격 코드 진입은 별 turn 의 명시 지시 후** (대규모 변경: PdfPig 이미지 raw 추출 + VLM caption + LlmConfig cost gate).

**처리 완료 (s6-r8)**: P3 Phase 2 본격 진입 — task A (schema 확장). parent §4 Phase 2 의 첫 task `schema 확장 — ImageCache / ImageReferences / Chunks.ImageCount + IndexerVersion bump` ✓ marker. 다음 task = `ImageStore.fs` 신설.

**처리 완료 (s6-r9, 본 turn)**: 외부 --inspect-diff 7 reviewer 종합 review 처리 — Critical 6 중 4건 즉시 적용 (K1 swap backup race / K2 multipart FormOptions / K3 config.json.template loopback / K5 purge lock 밖 dispose). K4/K6 + Major 26건 분류 박제 (§7.7 참조). 본 turn 코드 변경 = service 4 파일 + doc 1 파일 (todo). lib/service/IntegrationTests 회귀 0.

**다음 권장 (s6-r9 이후)**:
- **(N1 재진입)** dist 본격 실행 — `/dist` skill **사용자 직접 호출**. 본 세션 자체 호출 안 함.
- **(P3 Phase 2 task B)** `ImageStore.fs` — sha256 + blob 저장 + ImageCache/ImageReferences upsert (cross-document 공유, 단일 collection 안). 본 phase 의 schema 확장 (task A) 위에 build. 규모 ~150 line + Fact 5-7건.
- **(P3 Phase 2 task C)** PdfExtractor / OoxmlExtractor 가 이미지 raw 추출 + ImageStore 호출. PdfPig DCT/JPX/JBIG2 decode 추가 NuGet 필요 시 확인.
- **(P3 Phase 2 task D)** `attachment_read` image 모드 두 가지 (`caption_only` / `includeImages`) + VLM caption (Anthropic 1차) — D-S6-2 권장 default 정합.
- **(P3 Phase 2 task E)** LlmConfig cost gate (daily token cap 10K + soft warning + hard cutoff) — MR4 default 정합.
- **(P4 Phase S7)** mTLS / SSE / multi-service routing — D-S7-1~5 사전 박제 완료, 사용자 우선순위 결정 시 본격 진입. Phase 2 와 병렬 진입 가능.
- **(P7)** P2-r3 facade 실 caller 등장 시 (MCP relay or proxy) wrapper 통합 검증.

**잔여 박제 (s6-r8 follow-up — backlog)**:
- (s6-r8 m1) IndexerVersion compare 정책 — 현 §3.17 "다르면 무조건 재색인" 정합 = 1.0.0 → 1.1.0 진입 시 기존 collection 전수 재색인 비용. major/minor 분리 정책 (major=재색인 강제 / minor=ALTER forward-compat) 박제 검토 — 별 turn 의 parent §3.17 정정.

---

### 7.7 외부 --inspect-diff 7 reviewer 종합 review 처리 (s6-r9, 2026-05-18)

본 단원 = `00b72eb..HEAD` (Phase S1~S6-r7) 외부 7 reviewer (#1 generalist / #2 정확성 / #3 설계 / #4 영향범위 / #5 성능 / #6 보안 / #7 테스트) 종합 review 의 처리 박제. Critical 6 / Major 26 / Minor 50+ — 모두 표본 검증 시 hallucination 0 확인 후 분류.

**즉시 적용 (s6-r9 본 turn, 4건)** — Critical 6 중 4건:

- **K1 (swap backup race)** — `ZipImport.swapCollectionPayload` 의 `backup = target + ".bak"` 고정 suffix → 동일 collectionId 동시 swap 시 A 의 backup 을 B 가 무조건 삭제하여 A rollback path 가 빈 backup 으로 진입 → target 영구 손실 risk. **fix**: `backup = target + ".bak-<guid12>"` per-호출 unique suffix. 이전 fixed `.bak` 의 stale cleanup 분기 제거됨 — 각 호출이 자체 suffix 보유. stale `.bak-*` 잔재 sweep 은 staging sweep 정책 차원 별 turn.
- **K2 (10GB multipart spool)** — `CollectionEndpoints.postCollections` 의 `ReadFormAsync()` 가 ASP.NET FormOptions default (MultipartBodyLengthLimit = 134MB) 적용 → `cfg.MaxUploadBytes=10GB` (N6) 와 어긋남 → `InvalidDataException` 또는 temp disk 2배 점유 risk. **fix**: `Program.fs` 의 builder 에 `services.Configure<FormOptions>` 추가 — `MultipartBodyLengthLimit = cfg.MaxUploadBytes` + `ValueLengthLimit = Int32.MaxValue` + `MultipartHeadersLengthLimit = 32768` 박제. *MultipartReader streaming 으로의 전환 (R5 권장 backlog)* 은 본 phase 차단 사유 0, Phase S7 묶음.
- **K3 (config.json.template 0.0.0.0:8443 default)** — install script 가 template 을 덮어쓰지 않는 경로 (dev/PoC) 에서 모든 NIC bind → PSK 단독 보호로 외부 노출 risk. **fix**: `listenUrl` default 를 `127.0.0.1:8443` (loopback) 으로 정정 + `_listenUrl_note` 박제 (install-service.ps1 -ListenUrl 인자로 외부 IP override 의무 안내).
- **K5 (purgeCollectionFromSessions per-session lock 직렬)** — `SessionRegistry.purgeCollectionFromSessions` 및 `OnPayloadSwapped` 가 `for kvp in sessions do lock s.SyncRoot { ... SessionKb.dispose ... }` 패턴 — 큰 session 수 + long-running search 진행 중 collection delete 시 N×search-time block. **fix**: lock 안에서는 active 셋 갱신 + KB ref snapshot 만, 실 `kb.Dispose()` 는 lock 밖. SessionKb.dispose 의 idempotent 가정 정합.

**즉시 fix 불가 — 박제 사유 (Critical 6 중 2건)**:

- **K4 (LightHouseClient C# / F# 이중 구현 — wire SSOT 컴파일 강제 0)** — Promaker `LightHouseClient.cs` ↔ cli `LightHouseClient.fs` 의 의도된 분리 (Phase S5a 결정). 통합안 = `Solutions/Core/Ds2.LightHouse.Protocol` 신규 project (wire 상수 + MetaJson schema SSOT 통합). **결정**: Phase S7 묶음 (K4/M22/M5 동시) — 본격 refactor 부담, wire-level Fact 양쪽 검증 완비라 본 phase 차단 사유 0.
- **K6 (registry.json tampering)** — admin 침해 가정 위협 모델. registry 로드 직후 displayName == sanitizeTitle(displayName) 강제 검증 + storageRelPath segment 검증 필요 (~15 line). **결정**: 보안 sweep 1턴 (K6 + M9 PSK lifetime + M10 ACL + M12 staging ACL) 별 turn. %PROGRAMDATA% ACL 이 Admins write 보장 시 본 위협 = system 권한 위협 모델 외부 (defense-in-depth 가치만, 차단 risk 낮음).

**즉시 fix 불가 — Major 분류 박제**:

**합의 다수 (≥2 reviewers, Phase S7 묶음 또는 별 turn 후보)**:

- **M1** (Indexer/KnowledgeBase 의 ClearPool 부수효과 — 정상 경로에서도 process-wide pool 비움, 5+ 위치 동일 패턴) — 3/7 합의 (R1, R2, R5). M2 의 withConnection helper 추출과 함께 묶음 정비. Phase S7 또는 Phase 2 task B/C 진입 시 흡수.
- **M2** (lib `withConnection` helper 추출 — try {} finally { Close + ClearPool + Dispose } 5중복) — 2/7 합의. M1 과 동시 정비.
- **M3** (ExecuteWithSessionRetryAsync facade 무력화 + caller 0 + cancellation 분기 fact 부재) — 2/7 합의. s6-r6 의 P2-r3 박제 = facade only 의도 정합. caller = Phase S7 의 MCP relay 진입 시. cancellation Fact 는 s6-r6 자가 검열 M2 에서 *이미 1건 추가됨* — review 결과는 추가 분기 (timeout / network) Fact 누락 지적, backlog.
- **M4** (LightHouseClientHolder lock granularity + Invalidate 가 `_liveSessions.Clear()` 안 함) — 2/7 합의. Phase S7 의 multi-service routing (D-S7-3) 진입 시 흡수 — Holder 자체가 refactor 대상.
- **M5** (KbManagerDialog / cli `runUpload` 의 415 structured 분기 부재 — `LightHouseProtocolException` 단일 type, `suggestedAction` body 파싱 안 됨) — 2/7 합의. UX 직결. **별 turn (s6-r10 후보)** — 본 phase 의 K1~K5 즉시 fix 와 분리하여 UI 변경 + LlmConfig client lib 측 변경 부담 격리. cli runUpload + KbManagerDialog ProtocolException catch 분기에 415 → `JsonDocument.Parse(body).GetProperty("suggestedAction")` 출력 추가 (~30 line + Fact 2건).
- **M6** (IPv6 [::1] Fact 의 dual-stack false-positive — TaskCanceledException 도 success 분기) — 2/7 합의. 회귀 detection 강화 backlog.

**Outlier Major (1/7 — 검증 통과, 각 specialist 관점, 별 turn 후보)**:

- **M7** (`FileServing.findSourceFile` basename + size 만 비교, fileHash 검증 누락) — R1 outlier. Phase S5 의 CollectionPackager source/ flat 정책 결정 후 강화 후보. 잘못된 파일 서빙 + ETag mismatch risk. backlog.
- **M8** (dist SKILL.md 의 `installer/Apps/Promaker/scripts/check-paired-release.ps1` ↔ 신규 ps1 실제 위치 drift) — R4 outlier. SKILL.md 의 `installer/` prefix 는 외부 wrapper deploy repo 의 mount 가정. parent r4 박제 정합. **반론**: drift 아님, deploy repo SSOT.
- **M9** (PSK in-memory lifetime — string immutable + GC 비결정적, process dump 노출) — R6 outlier. K6 보안 sweep 1턴 묶음.
- **M10** (%PROGRAMDATA%\...\registry.json 의 ACL 미설정) — R6 outlier. 보안 sweep 묶음.
- **M11** (`attachment_search` 의 topK upper bound + query length limit 부재 — DoS amplification) — R6 outlier. 보안 sweep 묶음. service-side input validation 강화 별 turn.
- **M12** (staging `%TEMP%\promaker-kb-*` 의 사용자 원본 평문 + ACL 미설정) — R6 outlier. 보안 sweep 묶음.
- **M13** (`Indexer.ingestFile` outer transaction 부재 — Document/Outline insert 매 autocommit fsync) — R5 outlier. 색인 perf 강화 backlog. Phase 2 task B (`ImageStore.fs`) 진입 시 transaction 정합 동시 검토.
- **M14** (`SqliteStore.insertChunks` per-chunk CreateCommand — prepared statement reuse 누락) — R5 outlier. 색인 perf backlog.
- **M15** (`AttachmentIngestService` + `CollectionPackager` 의 같은 staging dir 4회 walk) — R5 outlier. Phase S5 client 측 perf backlog.
- **M16** (`Searcher.search UNION ALL` 의 per-collection LIMIT 누락 — 큰 KB 전 매칭 row 가 in-memory sort) — R5 outlier. search perf backlog.
- **M17** (`TextExtractor.Extract` File.ReadAllBytes 전량 적재 + UTF-16 변환 + Substring(1) — 200MB 파일 working set 1GB peak) — R5 outlier. extract perf backlog.
- **M18** (`KnowledgeBase.MaxAttachedDbs` SSOT drift 위험 — `SqliteStore.MaxAttachedDbs` alias 박제만, 컴파일 강제력 없음) — R2 outlier. F# `[<Literal>]` 재export. **반론**: 현재 KnowledgeBase 의 `MaxAttachedDbs` 는 `SqliteStore.MaxAttachedDbs` 를 직접 참조 (값 단일). SSOT drift risk 없음. **기각**.
- **M19** (Searcher.fs unqualified `ChunksFts.rowid` ambiguity 회귀 위험) — R2 outlier. Phase 2 multi-table FTS 진입 시 강화 backlog.
- **M20** (`TextExtractor.fs:46-50` BOM 부분 처리 — leading char 만, multi-section 잔존 U+FEFF 누수) — R2 outlier. 다중 BOM 입력 시나리오 backlog.
- **M21** (`KnowledgeBase.stampIndexerVersion` test-only API 가 lib production surface 에 노출) — R3 outlier. s6-r5 박제 — production caller 없음. **반론**: 향후 server-side restore tool 진입 시 사용 가능, 박제 의도. **기각**.
- **M22** (ZipBuilders ↔ CollectionPackagerTests ↔ cli production 의 zip layout SSOT 3중 박제) — R3 outlier. K4 Protocol SSOT 통합 시 동시 흡수.
- **M23** (Phase 2 schema — IndexerVersion 1.1.0 박제 의 cross-cfg invariant fact 부재 — Current ∈ [Min, Max]) — R7 outlier. paired-release detector 가 build/dist 시점에 강제 + s6-r8 Phase 2 task A 의 paired-release pass 검증 박제. lib test 의 invariant Fact 추가 backlog.
- **M24** (fact count drift — commit message "421 Fact" ↔ doc "422 Fact" ↔ lib +2 stamp) — R7 outlier. 다중 phase 누적 시 commit message ↔ §7.1 표 drift 가능성. backlog 정합.
- **M25** (NegativeRoundTripTests swap 415 Fact 의 cleanup try/with 누락 — fixture drift 위험) — R2 outlier. s6-r7 박제에서 finally → try 끝 명시 cleanup 변환 (F# task CE finally let! 불가) — 가정 실패 시 stale registry entry 가능. **반론**: fixture.DisposeAsync 의 temp dir 재귀 delete 가 흡수, 각 Fact 의 title GUID prefix 로 후속 Fact 와 충돌 0. 정합 박제. backlog.
- **M26** (Promaker Microsoft.Data.Sqlite 직접 PackageReference 부재 — lib transitive 의존, native asset 회귀 risk) — R4 outlier. s3-r1 의 IM-6 박제 정합 — service 에는 직접 노출, Promaker 는 lib transitive. 본 Promaker 가 SQLite 직접 사용 0 (in-process Indexer 만 호출) 이라 native asset 의 SqliteConnection load 시점 = service 한정. **반론**: Promaker 가 in-process Indexer 사용 = Microsoft.Data.Sqlite native asset 필요. transitive 의존만으로 충분한지 검증 backlog. (s5a-r0 의 lib ProjectReference 박제 시 동작 확인됨.)

**Minor 50+ 분류** (각 reviewer 본문 박제, 본 단원 요약):

- (F# pattern, ~15 항목) — Indexer ClearPool 5중복 / F# unreachable expression / sanitizeTitle 길이 byte vs char / 등 → Phase S7 묶음 또는 Phase 2 task B 진입 시 흡수.
- (Logging/Audit, ~10 항목) — `Log.audit.Info` 의 culture-invariant format / `tokenFingerprint` 32-bit search space / raw title 잔존 path / 등 → 보안 sweep 묶음.
- (테스트 키워드 의존성, ~8 항목) — Korean keyword "zip" / "구조" 의 `Assert.Contains` 비정밀 → 테스트 보강 별 turn.
- (운영, ~10 항목) — delayed-auto start / RecoveryActions / TLS minimum version 명시 / secret deny-list → install-service.ps1 강화 별 turn.

**즉시 적용 후 결과 검증**:
- service Tests 115/115 (회귀 0)
- IntegrationTests 24/24 (회귀 0)
- build 0 경고 / 0 오류
- K1/K2/K3/K5 즉시 적용 — 코드 5 파일 (ZipImport.fs / config.json.template / Program.fs / SessionRegistry.fs) + 새 Fact 0 (회귀 차단은 기존 Fact 가 cover, K1 의 unique suffix 회귀 차단 Fact 는 s6-r9 follow-up 보강 의무).

**follow-up 박제 (s6-r9 의무)**:
- **(s6-r9 의무 1)** K1 fix 의 회귀 차단 Fact — 동일 collectionId 동시 swap 2회가 race 없이 둘 다 성공 (또는 둘 다 fail 후 target 보존). 본 turn 박제 미진행, 별 turn 의 NegativeRoundTripTests 또는 SessionRegistry unit test 에 1-2건 추가.
- **(s6-r9 의무 2)** K2 fix 의 10GB upload e2e Fact — 실 10GB 부담은 IntegrationTests 에 부적합 (test 시간 + temp disk). 134MB ~ 1GB 사이의 중간 사이즈 multipart upload 1건으로 FormOptions 정합 회귀 차단 Fact 권장. backlog.

**(과거 권장사항, 상태 갱신)**:

**P1. 사용자 e2e 검증 (UI 테마)** — Promaker 종료/재실행 후 KB 관리 + Settings dialog 의 입력창 dark theme 정상 + 연결 테스트 light-blue/red 동적 색상 확인. s5c-r1 박제 시점 권장사항. **사용자 검증 완료 시 s5c-r1 commit msg 의 "사용자 e2e 검증 반영" 명시 정합**. 미검증 시 N1 진입 전 우선.

**P2. self-signed cert 신뢰 — `LightHouseClient` dev-only bypass flag** (사용자가 §4.2 우회 의존 안 하려면). 작업:
- `LightHouseClient` ctor 에 `bool allowInvalidCerts = false` 추가
- true 시 `HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator }`
- `LlmConfig.LightHouseService.AllowSelfSignedCert` 평문 bool + ApplicationSettingsDialog 에 "⚠ 자체 서명 인증서 허용 (dev only)" 체크박스
- `LightHouseClientHolder.EnsureCreated` 도 flag 전달
- chip 안내: "⚠ TLS 인증서 검증 우회 중 (dev 모드)"
- 규모 ~80 line + Fact 2-3건.

**P3. 본격 IntegrationTests round-trip (MA22)** — ~~Phase 2 / dist 직전~~ → **s5e-r0 박제 해제 완료**. 채택안 = **C 안** (`Program.configureApp` pure 함수 export + `ServiceFixture` 의 in-memory self-signed cert + Kestrel localhost:0 random port + 신뢰 우회 HttpClient). 6 Fact 통과 (auth 3종 + healthz + list + round-trip). 잔여 후보:
- (s5e-M2) Fact 6 finally `.Result.Dispose()` → `let!` + 명시 dispose 로 refactor
- (s5e-m5) `buildMinimalZip` 의 `Indexer.ingest` 결과 `Ingested` 분기 검증 강화
- (s5e-I) HTTPS-only 검증 Fact (`http://` scheme → connection refused) — fixture 가 `https://` baseAddress 만 노출, 별도 client 신설 필요
- 추가 fact (L3 자동 회복 / IndexerVersion gate 415 / zip bomb 거부 등) — 별 PR

**P4. KbManagerDialog 의 CollectionInfo 미표시 필드 추가** (S5a-M4 / S5b 잔여) — server `CollectionEntry` 대비 CreatedAt / ImportedBy / TotalSourceBytes / StorageRelPath / ImportedAt 5필드. ListView column / tooltip / expander 결정. forward-compat Fact 1건. 규모 ~50 line.

**P5. s5c 잔여 follow-up**:
- (S5c-m1) `App.OnExit` 의 serial DELETE → `Task.WhenAll` 병렬화 (N≥4 진입 시점)
- (S5c-m2) `LightHouseClientHolder._lastPskHash` salt 없는 SHA256 → first8hex fingerprint
- (S5c-m3) Holder static singleton test 의 `[Collection("Sequential")]` 명시 (cross-file Holder test 추가 시)
- (S5c-m4) `McpConfigWriter.ServerName` legacy 필드 deprecate 후보
- (S5b-m1) `LightHouseClient.SendZipMultipartAsync` helper 추출 (Upload/Reupload 중복)
- (S5b-m2) `KbManagerDialog.Register_Click / Reupload_Click` 의 consent+validate+RunIngestAsync 패턴 압축
- (S5a-m6) 빈 active set L3 회복 의미 검토
- (S5a-m10) `LlmConfig.SetApiKey/SetLightHousePsk` 의 DPAPI helper refactor

**P6. S4 안 follow-up (이전 phase 부채, 5건)**:
- (S4-M3) 200/304 분기 ETag 헤더 일관성 + test
- (S4-m3) `userIdentityOf` "unknown" anomaly 박제 (Log.audit.Warn)
- (S4-m4) fileId SSOT 주석에 todo 항목 번호
- (S4-m6) 416 / If-Range Fact 추가
- (S4-m8) `Path.GetExtension` 가드 잉여 정리

**P7+ 후속**:
- Phase S6 — `lighthouse-cli` 무인 batch (handover P5 와 별 분리)
- Phase S7 — SSE `/events` / resumable upload / mTLS / multi-service / T2/T3 multi-tenant
- Phase 2 — parent backlog 잔여 (s2 C3 trailing backslash / s3 M5/M9/M10/m13/m15 / s4 M4/M5/m2/m5 / s3-r1 IM-1/4/5/7/9 + Minor 18)

### 7.5 commit 정책 (재확인)

- multi-step plan 의 "go" 동의를 commit step 까지 묶지 말 것 — commit 은 별도 confirm (memory `feedback_commit_authorization`).
- `--gc` 플래그 사용 시 진행 (CLAUDE.md). remote branch 부재 (`light-house` local only) → push skip.
- `Co-Authored-By` 기입 안 함.
- 자가 검열 trigger 충족 (CLAUDE.md SSOT) 시 sub-agent 검열 미수행 상태에서 commit/다음 phase 진입/사용자 confirm 질의 금지.

### 7.6 알려진 PoC 함정 (howto §9 trubleshooting 도 참조)

1. `sc start LightHouseService` → "지정된 서비스가 설치된 서비스로는 없습니다" — 정확한 이름은 `Ds2.LightHouseService` (prefix 포함).
2. PowerShell 5.1 의 한글 깨짐 — `.ps1` 의 UTF-8 BOM 누락. s5c-r1 에서 `install/uninstall/generate-dev-cert.ps1` 3종에 BOM 추가.
3. `SEC_E_UNTRUSTED_ROOT` — Trusted Root 에 cert import 안 됨. `Import-Certificate -CertStoreLocation Cert:\LocalMachine\Root` 진행.
4. `SEC_E_WRONG_PRINCIPAL` — cert CN=`localhost` 인데 client 가 `127.0.0.1` 로 접속. **`localhost` 사용 의무** (또는 cert 재발급 시 SAN 에 IP 추가).
5. Makefile 의 echo 한글 — `cp949 ↔ UTF-8` 경계 깨짐. Makefile 상단 규약 = "echo/printf 출력은 ASCII 전용 — 한글은 주석에만 둘 것". s5c-r1 에서 install rule 의 echo 한글 → 영문 변경.
