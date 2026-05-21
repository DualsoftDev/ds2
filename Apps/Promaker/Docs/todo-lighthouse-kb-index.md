# Ds2.LightHouse — 문서 사전 색인 + MCP 노출 작업

세션 이어받기용 TODO. 본 문서는 *결정된 설계* 와 *남은 할 일* 만 담는다.

| rev | 일자 | 주요 변경 |
|---|---|---|
| r0 | 2026-05-17 | 초안 (LightHouse 분리 / 4 tool / FTS5 → hybrid 점진 / cached lazy 이미지) |
| r1 | 2026-05-17 | --review 6 reviewer 반영: AttachmentClassifier 활성 호출 사실 정정, FTS5 tokenizer trigram, Phase 1 이미지 인프라 환원, Phase 3↔4 swap, 두 경로 분리 SSOT 신설, schema 결함 5항 명문화, NuGet 분리, WAL/동시성 PRAGMA, RefLocator SSOT, 모델 generation 단위 invalidation 외 30여건 |
| r2 | 2026-05-17 | --review 3 reviewer 재검증 반영: drift fact 9→13 정정, PromptLoader 묘사 (glob+3-tier+facts 포함 6 baseline) 정정, LightHouse 의 Ds2.Editor 미참조 invariant 추가, Ds2.LlmAgent/CLAUDE.md SSOT 갱신 task, active todo-llm-chat-attachment.md cross-PR 동기화, .gitignore .promaker-kb/ 를 §4.1 첫 task 로 격상, INDEX 5종 + external content mirror 명명 정정, App.xaml.cs line 정정 외 minor |
| r3 | 2026-05-17 | --review 1 reviewer 추가 반영 (4건): (1) PromakerToolNames.All 6→10 + DriftTests Tools/*.cs 전체 스캔 확장 (allowlist drift 자동 검출) (2) KB root/index 주입 경로 — `LlmTurnContext` 확장 또는 별도 DI singleton 결정 + `AttachmentTools` 가 어떻게 도달할지 (3) `Solutions/Ds2.sln` 갱신 누락 차단 (sln 2개 모두 등록) (4) r2 잔류 stale 정정 (line 448 "Fact 9건", line 452 "App.xaml.cs:147") |
| r3-transfer | 2026-05-17 | 다음 세션 이어받기용 §0 신설. 코드 변경은 여전히 0 (전체 작업은 --plan 모드 안). |
| r4 | 2026-05-17 | n×m KB 운영 도입 — collection = 사용자가 임의 폴더 선택 (path-based, project 종속 X). 한 폴더 = 1 collection. LlmConfig.KbCollections (OS 사용자 전역) 에 등록. SQLite ATTACH 로 m 개 active union 검색. KbManagerDialog (ApplicationSettingsDialog 진입 버튼) 신설. 원본 사본 정책 폐기 (사용자 폴더 안 원본이 SSOT, `.lighthouse-kb/` 은 index.db + Phase 2 image blob 만). read-only collection: read OK, write (색인/재색인) fail. dock 패널 §4.7 폐기 — KB UI 는 dialog 전용. |
| r5 | 2026-05-17 | **대안 B 채택 — Phase 1 의 §4.5 / §4.1 첫 task / §4.6 / §4.8 일부 SKIP** (server phase 흡수). parent Phase 1 = LightHouse lib 본체 (§4.2/§4.3/§4.4) + lib 자체 unit test 까지만. Promaker 측 통합 전체는 `todo-lighthouse-kb-server.md` Phase S5 가 단일 SSOT. 사유: parent Phase 1 의 §4.5 가 server phase 진입 시 60%+ throwaway 라 yo-yo / migration / `.lighthouse-kb/` 고아 / `LlmTurnContext` 자가 모순 6건 회피. `--inspect-diff 5` 사후 사용자 추가 검증으로 도출. |
| r6 | 2026-05-17 | `--inspect 3` reviewer 결과 반영 (Critical 6 + Major 23 + Minor 15 = 44건). 주요 갱신: (1) §3.18.2 채택안 (a) r5 SKIP marker 강화 — Phase 1 의 결정사항 아님 명시 (CR1/MA9), (2) §0 보류 항목 표 5→3행 — §4.5 의존 2행 server §4.3 로 이전 (CR1/MA2), (3) 진입 박스 7→9 row 보강 (§3.17/§3.18 추가, §4.6 신설 row, MA10), (4) §3.9 의 ad-hoc Q1/Q2/Q3/D-1 표기에 `r4-` prefix — server §0 D-id namespace 충돌 회피 (MA1), (5) §5 수정 목록을 "Phase 1 본체" vs "r5 SKIP — server phase 흡수" 두 sub-section 으로 분리 (CR1), (6) §7 다음 세션 행동 r5 박제 + grep checklist 강화, (7) §4.8 lib unit test 시나리오 구체화 (a/b/c/d 4 sub-section: parser/FTS5/multi-collection/cross-PR) (MA21), (8) §6.13 박제 fresh marker (2026-05-17) (mn10), (9) §3.15.3 `.promaker-kb/` 잔재 → `.lighthouse-kb/` (mn1), (10) §4.1 의 무관 grep task 제거 (mn5), (11) §4.2a 에 ImageFormat 호출처 전수 grep task 추가 (mn14). server.md 와 동시 갱신 (s0-r3). |
| r7 | 2026-05-17 | **§4.1 진입 commit (`bccb0ea`) + §4.2a 진입 commit (본 turn)**. 사용자 결정: (a) Solutions/Ds2.sln 갱신 SKIP — `Apps/Promaker/Promaker.sln` 만 갱신 (§4.1 박제 "sln 2개" → 1개로 좁힘), (b) `ModelContextProtocol.AspNetCore 1.2.0 → 1.3.0` 업그레이드는 본 Phase 1 보류 — server phase Phase S3 P2 결정 시 함께, (c) 본 todo 파일 git mv 보류 (Phase 1 완료 후 별도 결정). **§4.2c "C# 무영향 확인" 가정 정정**: F# type abbreviation 이 C# interop 작동 안 함 — C# 3 파일 (`LlmChatViewModel.Attachments.cs` / `LlmChatPanel.xaml.cs` / `ApiTurnContentBuilderTests.cs`) `using Ds2.LightHouse;` 추가 + `Ds2.LlmAgent.ImageFormat.Png` → `Ds2.LightHouse.ImageFormat.Png` namespace 갱신 필요 (수행 완료). §4.1 의 `Solutions/Directory.Packages.props` 에 PdfPig 0.1.14 + DocumentFormat.OpenXml **3.5.1** (최신 stable, 자가 검열 minor) 등록. §4.2a 추가 grep 발견 — `ClaudeStreamJsonInputTests.fs:33,74` 의 `Png` constructor 도 `open Ds2.LightHouse` 추가 (총 F# Test 3 파일). |
| r8 | 2026-05-17 | **§4.2b/§4.2c commit (`b8c747c`) 완료 + §4.3 전체 (lib 본체 — 추출/청킹/분류 layer) 작성 완료** (commit 보류, 본 박제로 transfer). (i) §4.2b: AttachmentClassifier.detectEncoding 본체+부속 (TextEncodingDetect/tryCp949/isStrictDecodable/Log.provider.Warn) 제거, shim 1줄 잔류 + 영구 호환 표기. (ii) §4.2c: LlmAgent.fsproj 의 System.Text.Encoding.CodePages 직접 참조 제거 (LightHouse transitive). Promaker.csproj 는 entry-point assembly 의 명시적 자기 의존 + fail-fast 안전망으로 직접 참조 유지. (iii) §4.3: 신규 8 F# 파일 (Models 155 / RefLocator 143 / Classifier 71 / Chunker 136 / IExtractor 22 / TextExtractor 89 / PdfExtractor 68 / OoxmlExtractor 124 = 793 line) + Logging.fs `Log.lighthouse` 추가 + fsproj Compile Include 8. **자가 검열 1차 (sub-agent)** Critical 0 / Major 3 / Minor 5 → M1 (Chunker sentence regex)/M2 (splitBySentences self-contained)/M3 (OoxmlExtractor 한정 catch 4종) 즉시 적용 + 3 Phase 2 보류 (heading depth stack / outline region / extract_status DU) + 1 명문화 (surrogate pair) + 1 미적용 (RefLocator parseFragment 단순화). **--review 메타리뷰 (외부, b8c747c)** Critical 0 / Major 3 / Minor 4 → M1 (shim type annotation + 영구 호환 docstring) / M2 (Promaker.csproj 주석 보강 + (a) 안 채택) / M3 (ImageFormat.fs 주석 정정 — wildcard 분기로 컴파일 강제 안 됨 명시) + m1 (logger 변경 박제 CLAUDE.md Logging.fs 행) + m3 (CLAUDE.md AttachmentClassifier 행 line 박제 → 함수명 anchor 약화) 모두 적용. m2 (LightHouse → KB 패키지 transitive 비대화 — `<PrivateAssets>all</PrivateAssets>` 적용 여부) 는 §4.4 facade 진입 후 결정 박제 (보류 항목 표 추가). m4 (todo 진행 표시 갱신 누락) 는 본 r8 박제로 흡수. |
| r10 | 2026-05-17 | **§4.4 commit (`00b72eb`) + §4.8 commit (`9736237`) 완료 — Phase 1 lib 본체 + lib unit test 전부 종결**. (i) §4.8 신규 11 F# test 파일 (TestInit + RefLocator 9 / Chunker 9 / Classifier 9 / TextEncoding 9 / TextExtractor 5 / OoxmlExtractor 3 / PdfExtractor 3 / SqliteStore 14 / Indexer 8 / KnowledgeBase 13 = **99 Fact, 100% 통과**). fsproj 갱신 (Compile Include 11 + PackageReference 4 — `System.Text.Encoding.CodePages` / `Microsoft.Data.Sqlite` / `DocumentFormat.OpenXml` / `PdfPig` 직접 의존, LightHouse 의 PrivateAssets=all 우회). (ii) §4.4 lib 본체 잠재 버그 3건 발견/fix (test 가 노출): **(F1) Searcher.fs FTS5 SQL 호환성** — `bm25(kb0.ChunksFts)` / `bm25(<alias>)` 둘 다 `SQLite Error 1: no such column` → FTS5 의 auxiliary function 과 MATCH 는 *unqualified table name* 만 받음. FROM 절만 schema-qualify, JOIN/WHERE/bm25 는 모두 unqualified `ChunksFts`. **(F2) Indexer.fs SqliteConnection pool 의 lock 보존** — Microsoft.Data.Sqlite 가 dispose 후에도 native handle pool 보존 → `File.Replace` (shadow swap) 시 source/target lock 충돌. `SqliteConnection.ClearPool` 명시 호출을 3 경로 (probe / shadow rebuild / 정상 ingest) 모두 적용. **(F3) TextExtractor.fs UTF-8 BOM 첫 char 잔류** — `Encoding.UTF8.GetString` 이 BOM 을 U+FEFF 로 보존 → chunk 본문 / FTS5 색인 / search excerpt / read 결과 invisible char 회귀. leading U+FEFF strip. (iii) **자가 검열 (sub-agent general-purpose)** Critical 0 / Major 3 / Minor 9 → M1 (빈 active 셋 search/list empty 검증) + §4.8c boundary 10 ATTACH 검증 + m4 (OoxmlExtractor segment count brittleness 완화) + m9 (FTS5 AU trigger 검증 — r9 자가 검열 M2 잔여 우려의 마지막 분기) + §4.8a 정확 boundary 200/501 token + M2 (정상 ingest 경로 ClearPool 방어적) **6건 즉시 적용**. M3 (swapShadow try-finally 바깥 — 현 SSOT 명문화) / m1/m2/m3/m6/m7/m8/r9 C1 drift 보호 / r9 M4 cross-talk 보호 = Phase 2 보류. (iv) **AttachmentClassifierDriftTests 13/13 통과 유지** (§4.8d, 별 project, 무영향 확인). (v) **Phase 1 lib 본체 + lib unit test 종결** — server phase (`todo-lighthouse-kb-server.md` Phase S0 → S1) 진입 confirm 대기. |
| r15 | 2026-05-19 | **server-side s6-r26 묶음 §3.17 정정 동기화** — "색인 재구성 정책" trigger SSOT 변경 (`Meta.IndexerVersion` 단순 equality → `Meta.schema_version drift`). IndexerVersion minor / patch bump 의 ALTER forward-compat 흡수 + SchemaVersion / IndexerVersion 동반 bump (s6-r22 mn3 패턴) lib 개발자 책임 명시. 기존 collection 재색인 cost 회피 정책 진입. server-side `todo-lighthouse-kb-server.md` s6-r26 row 와 paired. |
| r16 | 2026-05-21 | **Phase 2 PPTX + XLSX 활성 완료** (`todo-lighthouse-kb-index-xlsx-pptx-images.md` r2 의 Task 0~3 commit 5건 — Task 0 refactor `0a9833e` / Task 1 PPTX `cc53628` / Task 2 XLSX `3948bdc` / Task 3 RefLocator regression `8a72de4`). (i) **§3.13 RefLocator 표 행 2개 추가** — PPTX 이미지 (`slide=5#img=2` → "슬라이드 5 그림 2") + XLSX 이미지 (`sheet=BOM#img=1` → "시트 BOM 그림 1"). (ii) **OoxmlExtractor 활성 — Docx + Pptx + Xlsx**: PPTX (SlideIdList SSOT 순회 + Title/CenteredTitle placeholder + paragraph break + speaker notes `--- 노트 ---` marker), XLSX (SST + PhoneticRun rPh 제외 + Sheet.State enum Hidden/VeryHidden skip + expandSparseRow + Cell.DataType 6 분기 + sheet 단위 RefLocator only). 사용자 결정 박제 — xlsx 의 수식 cached value only / hidden skip / merged top-left / 빈 행 skip / 좌표 RefLocator Phase 3 backlog. (iii) **Test fixture 패턴** — `PresentationDocument.Create` / `SpreadsheetDocument.Create` + SDK 객체 직접 build (raw outerXml 우회). 누적 lib 100 → 154 Fact (`Ds2.LightHouse.Tests` 의 OoxmlExtractor 9 → 35 + RefLocator 16 → 21 = 본 turn 31 신규 Fact). (iv) **Phase 3 backlog 박제** — xlsx 좌표 RefLocator (`sheet=BOM!A1:D40`) + `attachment_read` ref 파서 강화 / xlsx Defined Name / Pivot Table / Chart / pptx SlideMaster / SlideLayout / comments / notes master / Boolean cell raw 0/1 → TRUE/FALSE 변환 / pptx hidden slide skip 정책. (v) **Phase 2 Task 5/6 (VLM caption / attachment_read image 모드) scope out** — lib 측 `captionGen` surface 박제 완료 + server 측 단일 책임. server phase 진입 confirm 후 별 todo 분리. |
| r17 | 2026-05-21 | **Phase 2 Task 2-extra + Task 7 완료** (`todo-lighthouse-kb-index-xlsx-pptx-images.md` r6 의 Task 2-extra Gantt 힌트 commit `1dcc7f8` + Task 7 standalone image commit `7964ec6`). (i) **§3.3 FileKind DU 갱신** — `Image` case 추가 + `RefUnit.Image` 와 동명 disambiguation 박제 (`FileKind.Image` qualification 의무 — `OutlineNodeType.*` 패턴 정합). (ii) **§3.11 image 활성 정책 박제** — Classifier `rejectedExtensions` 에서 raster 5 (`.png`/`.jpg`/`.jpeg`/`.gif`/`.webp`) 제거 + vector 2 (`.emf`/`.wmf`) 활성. `supportedExtensions` 에 7 매핑 (모두 `FileKind.Image`). **BMP/TIFF/SVG/ICO/HEIC** 는 Phase 3 backlog (raster 변환 / SVG raster 추가 의존성 필요). (iii) **§3.13 RefLocator 표 행 1개 추가** — `image=1` → "이미지 1". `RefUnit.Image` 신설 + `tryParse`/`toStored`/`formatDisplay` 일반화. (iv) **신규 lib 파일** — `MetafileConverter.fs` (OoxmlExtractor + ImageExtractor 양쪽 재사용 SSOT — 단일 EMF/WMF→PNG 변환 helper) + `Extractors/ImageExtractor.fs` (IExtractor 구현, magic byte 검증 + Width/Height parse + per-image fail-safe). (v) **xlsx Gantt schedule 힌트** — 8 role synonym map + normalize header + 2-row merged header concat + score 판정 (distinct role ≥3 AND start/dur/cum ≥2) + 동적 preamble prepend + outline `[Gantt schedule]` suffix. 산업 .xlsx 작업일정표 색인 시 LLM 이 row tab-join 데이터를 컬럼 의미 기반 정확 해석. (vi) **caller 등록 4곳** — Packager / Program / IndexerTests extractors list + AttachmentTools `fileKindString` `FileKind.Image` 매핑. (vii) **Test fixture** — `System.Drawing.Bitmap.Save` 로 JPEG/GIF minimal bytes 생성 (WEBP/EMF 는 raw header bytes 또는 invalid bytes fail-safe 검증). 누적 lib 154 → **268 Fact** (+9 ImageExtractor + 6 Gantt + 7 Classifier 보강 + 1 RefLocator image=1 round-trip + 다수 기존 Fact 회귀 통과). (viii) **server phase 분담 미진입** — Task 5 (VLM caption provider 본체) / Task 6 (`attachment_read` image 모드) 는 `Ds2.LightHouseService` 측 단일 책임. server phase 진입 confirm 후 별 todo 분리. |
| r14 | 2026-05-19 | **server-side s6-r25 묶음 sweep 동기화** — §3.15 sweep 잔여 3개소 정정 (line 200 §3.0 표 / line 790 Phase 2 헤더 / line 818 Phase 3 검토 — "cached lazy" → "eager VLM caption — s6-r18 D-2-2"). line 7 (r0 historical, 변경 불필요) / line 581 mr4 (취소선 정합 적용 완료, 변경 0) 박제 정합 확인. 코드 변경 0 (parent doc only — Phase 1 본체 r10 그대로 유지). server-side `todo-lighthouse-kb-server.md` s6-r25 row 와 paired. |
| r13 | 2026-05-18 | **§3.18 KnowledgeBase facade module 표면 확장 (record 무영향)** — `stampIndexerVersion : kbRoot -> version -> unit` 신설. `probeIndexerVersion` 의 write 대칭. 용도 = test 한정 (server `todo-lighthouse-kb-server.md` s6-r5 의 IndexerVersion gate 415 시나리오에서 zip 안 `.lighthouse-kb/index.db` Meta.indexer_version 행 override). production 색인 (`Indexer.ingest`) 가 stamp 한 `IndexerVersion.Current` 강제 override 시 schema/version drift 위험 — docstring "production 호출 금지" 박제. KnowledgeBase **record** 표면은 무변경 (Search/List/Outline/Read/ActivePaths/Dispose 6 함수 필드 그대로) — test 한정 + lifecycle 짧음이라 record-of-functions 표면에 안 박음 (자가 검열 sub-agent 권고 정합). lib unit test 2 Fact 추가 (KnowledgeBaseTests: override → probe 반영 / 미존재 시 InvalidOperationException). lib 100 → 102 Fact. |
| r12 | 2026-05-18 | **N3-A (MSB3277 해소) + N5 (§3.15.5 Phase 2 사전 결정 박제 흡수)** — (i) **N3-A**: `Apps/Promaker/Directory.Packages.props` 에 `DocumentFormat.OpenXml 3.5.1` PackageVersion 추가 + `Apps/Promaker/Promaker/Promaker.csproj` 에 `DocumentFormat.OpenXml` PackageReference 직접 추가 (transitive PdfPig 3.1.1 압도, 3.5.1 통일). 빌드 결과 MSB3277 충돌 0건 (이전 다수 → 0). r9 (v) 보류 항목 해소. (ii) **N5**: §3.15.5 "Phase 2 진입 사전 결정 표" 신설 — CR1/CR2 + MR1~MR4 + mr1~mr4 = 10건 + 진입 trigger / cost forecast / 진입 순서. 별 doc 신설하지 않고 본 todo 안 흡수 (사용자 결정). (iii) **N3-B (todo git mv) 취소** — 사용자 결정 시 본 todo 파일 위치 유지 (`Apps/Promaker/Docs/`). r7 보류 항목 (§6.14) 은 추후 별 PR 결정 시점에 재검토. (iv) **회귀 검증**: `dotnet build Promaker.sln` 0 오류 + MSB3277 0건 + `Promaker.Tests` **203/203** + `Ds2.LightHouse.Tests` **100/100** + `Ds2.LightHouseService.Tests` **111/111** + `Ds2.LightHouseService.IntegrationTests` **8/8** = 누적 **422 Fact**. (v) 자가 검열 trigger 미충족 (단순 PackageReference 추가 + doc 흡수). |
| r9 | 2026-05-17 | **§4.3 commit (`16b50c3`) 완료 + §4.4 전체 (lib 본체 — 저장/검색/orchestrator/facade) 작성 완료** (commit 진입 직전). (i) 진입 직전 보류 2건 사용자 결정 — **(a) facade 형식 = record-of-functions** (todo 권장 default, §3.18.1), **(b) PrivateAssets=all 적용 범위 = PdfPig + Microsoft.Data.Sqlite + DocumentFormat.OpenXml 3 package 한정** (r8 m2). (ii) §4.4 신규 4 F# 파일 (SqliteStore ~270 / Searcher ~280 / Indexer ~175 / KnowledgeBase ~105 ≈ 830 line) + fsproj Compile Include 4 + PrivateAssets attribute 3. (iii) **자가 검열 (sub-agent general-purpose)** Critical 1 / Major 6 / Minor 8 → C2 (ATTACH path parameter binding 불가 → inline + single-quote escape) / M1 (FTS5 token split + per-token phrase quoting / implicit AND) / M3 (Dispose with _ swallow → Log.Warn 박제) / M4 (`:memory:` cache=Shared cross-talk → cache=private + unique URI `lhmain-<guid>`) / M5 (SHA-256 stream 주석 정정) / M6 (BM25 부호 반전 — 높을수록 hit 강도) / m6 (`ingest` 시그니처 `(string * FileIngestResult) array` 반환) / m8 (Dispose swallow log 흡수) **9건 적용**, m1/m2/m3/m5/m7/C1 (probe reopen 실측 OK) 6건 Phase 2 refactor 보류. (iv) **--review 메타리뷰 (외부)** Critical 1 / Major 3 / Minor 5 → **C1 (KnowledgeBase.openCollections ATTACH 실패 시 conn 누수)** = try-with reraise + Dispose / **M3 (Searcher fileId parse 실패 silent fallback)** = log warn + 명시 빈 결과 (`Hint = "invalid fileId"`) / **m1 (SqliteStore.deleteDocument 미사용)** = 보존 사유 주석 (매뉴얼 purge / unit test cleanup) / **m5 (.bak 즉시 삭제 정책)** = todo §3.17 미명시이나 idempotent + atomic 안전 주석. M1/M2/m3 는 자가 검열에서 이미 반영. (v) **OpenXml 3.1.1 ↔ 3.5.1 transitive 충돌 경고** (`Apps/Promaker/Promaker.csproj` 빌드 시 MSB3277 2건) — 본 §4.4 scope 외, 다른 패키지의 transitive (PdfPig 가 OpenXml 3.1.1 끌어옴 추정). `Apps/Promaker/Directory.Packages.props` 에 OpenXml 3.5.1 명시 검토 = **§0 보류 항목 표 신설 (Phase 1 완료 직전 또는 server phase Phase S3 결정 시 함께)**. (vi) 빌드 검증 — LightHouse 0 경고/0 오류, LlmAgent + Tests 0 경고/0 오류, AttachmentClassifierDriftTests 13/13 통과, Promaker 빌드 성공 (transitive 충돌 경고 2건만). |

---

> **⚠ 후속 phase 별도 추적 — `todo-lighthouse-kb-server.md` (s0)**
>
> r4 의 in-process MVP **그 위에** central Windows Service 를 얹는 design 이 별도 todo 로 박제됨. 본 문서의 Phase 1 완료 이후 phase. 이하 r4 결정 중 service 도입 시 **회귀** 하는 단원이 있음 — 각 단원 헤더에 marker 표기.
>
> | 회귀 단원 | service 도입 후 (요약 hint) |
> |---|---|
> | §3.9 (사용자 폴더 = SSOT, 사본 X) | server-side `Collections\<guid>-<title>\` 가 SSOT, 사용자 폴더에 흔적 0 |
> | §3.10 (MCP tool host = Promaker) | host 위치만 service 로 이동, tool 이름/인자 변경 0, 호출 context 만 session 헤더로 갈음 |
> | §3.17 (SQLite 운영 — WAL/PRAGMA/재색인) | client write-path SSOT 유지, service read-path 는 read-only ATTACH 만 |
> | §3.18 (KnowledgeBase facade) | client 측 Indexer facade / service 측 Searcher facade 양분 (`kb-server.md §3.1.2`) |
> | §3.18.2 (`LlmTurnContext` 에 `KnowledgeBase` 주입) | **회피** — read-path 가 service 측이라 LlmTurnContext 와 무관 |
> | §4.1 첫 task (`.gitignore` 에 `.lighthouse-kb/` 추가) | **삭제** (사용자 폴더에 안 생김) |
> | §4.5 (Promaker `AttachmentTools.cs` / `LlmConfig.KbCollections` schema 등) | 큰 폭 재작성 — `kb-server.md` §3.13 참조 |
> | §4.6 (`5.knowledge-base.md`) | server-side host 명시 + MCP host 2개 정책 (`kb-server.md §3.1.3`) 포함하여 신설 |
> | §6 m15 (PII 위험) | 강화 (multi-tenant α flat 이라 위험 ↑) |
> | §6 m16 (schema 불일치) | 완화 (server IndexerVersion gate 가 흡수) |
>
> **본 매트릭스는 진입용 hint** — 전체 회귀 매트릭스 SSOT 는 `kb-server.md` §3.14 (11 행, r6/s0-r3 에서 §4.6 row 신설). r5 통합 시점에 본 박스 ↔ `kb-server.md` §3.14 동시 갱신 의무.
>
> **본 문서 Phase 1 진입 시점에는 위 회귀를 무시하고 r4 결정대로 진행**. service phase 진입 시점에 본 문서를 r5 로 통합 또는 `kb-server.md` 와 병존 결정.

---

## 0. 현재 상태 요약 (transfer 시점 — 다음 세션 진입 시 가장 먼저 읽기)

### 진행 상태
- **현재 rev**: **r15 (s6-r26 §3.17 정정 동기화)** — server-side `todo-lighthouse-kb-server.md` s6-r26 묶음에서 본 doc §3.17 "색인 재구성 정책" 본문 정정 — shadow rebuild trigger SSOT 를 `Meta.IndexerVersion` 단순 equality → `Meta.schema_version drift` 로 변경. IndexerVersion minor / patch bump 의 ALTER forward-compat 흡수 + SchemaVersion / IndexerVersion 동반 bump (s6-r22 mn3 패턴) lib 개발자 책임 명시. lib 152 → **154 Fact** (+2 needsRebuild Fact). r14 (§3.15 sweep) 박제 그대로 유지.
- **후속 phase 별도 추적**: `todo-lighthouse-kb-server.md` (**s6-r26**, IndexerVersion compare 정책 변경 완료 + 자가 검열 review M1/m1 즉시 적용 + 누적 532 Fact). 다음 권장 = Phase S7 (mTLS / SSE / multi-service routing) 또는 Phase 2 task F (OCR / embedding) — 사용자 우선순위 결정 시 진입.
- **모드**: Phase 1 종결. server phase 진입 후 본 todo 는 회귀 매트릭스 SSOT (§4.1 보류 박스 + server.md §3.14 동시 참조) 로 잔류.
- **본 세션까지 commit 누적**:
  - `bccb0ea` — §4.1 scaffold: Ds2.LightHouse + Tests project 신설 + Promaker.sln 등록 + Directory.Packages.props (PdfPig 0.1.14 + DocumentFormat.OpenXml 3.5.1 신규)
  - `cfc2c29` — §4.2a: ImageFormat + TextEncoding → LightHouse 이전 + C# interop namespace 갱신 + AttachmentClassifierDriftTests 13 통과 + CLAUDE.md SSOT 박제 line 70 갱신
  - `b8c747c` — §4.2b/§4.2c: AttachmentClassifier.detectEncoding shim 화 + CodePages 참조 정리 (LlmAgent.fsproj 직접 참조 제거, Promaker.csproj 안전망 유지). 13/13 통과
  - `16b50c3` — §4.3 lib 본체: 신규 8 F# 파일 (Models / RefLocator / Classifier / Chunker / IExtractor / TextExtractor / PdfExtractor / OoxmlExtractor = 793 line) + Logging.fs `Log.lighthouse` + fsproj Compile Include 8 + r8 박제 (자가 검열 sub-agent + 외부 메타리뷰 5건 반영)
  - `00b72eb` — §4.4 lib 본체: 신규 4 F# 파일 (SqliteStore ~270 / Searcher ~280 / Indexer ~175 / KnowledgeBase ~105 ≈ 830 line) + fsproj Compile Include 4 + PrivateAssets="all" 3 package + r9 자가 검열 + 외부 --review 13건 반영. 빌드 0 경고/0 오류, drift tests 13/13.
  - `9736237` — §4.8 lib unit test: 신규 11 F# test 파일 (TestInit + 10 test = 99 Fact, 100% 통과) + fsproj PackageReference 4 + lib 본체 잠재 버그 3건 fix (Searcher FTS5 unqualified-name / Indexer SqliteConnection.ClearPool / TextExtractor UTF-8 BOM strip) + r10 자가 검열 6건 반영

### 사용자가 명시적으로 동의한 결정 (이전 세션에서 확정)
1. **신규 F# project 명 `Ds2.LightHouse`** — `Solutions/Core/` 하 신설, base 의미 (§3.1)
2. **타입 이름 KB-aware 일반화** — `FileKind` 신설, `Classification` 영구 잔류 (§3.3)
3. **`detectEncoding` 별도 모듈 분리** (§3.4)
4. **LightHouse 의 `Ds2.Core` / `Ds2.Editor` 미참조 invariant** (§3.2, §3.5)
5. **두 경로 완전 분리 SSOT** — chat image drop ≠ KB ingest (§3.0)
6. **AttachmentClassifier 부분 통합 축소** — `detectEncoding` / `ImageFormat` 만 LightHouse 이전, `Classification` 표면 무변경 (§3.11)
7. **Phase 1 환원** — 순수 FTS5 + text + outline + 4 tools, 이미지 인프라 Phase 2 로 이동 (§3.12, Phase 1)
8. **Phase 3↔4 swap** — Phase 3 = OCR, Phase 4 = embedding (§3.15.2)
9. **이미지 처리는 eager-at-indexing** — 색인 시점에 client 측 색인 프로그램이 모든 image 에 VLM caption 1회 호출 + `ImageCache.CaptionText` 영구 저장. chat 시점은 cache hit 만 (사용자 명시 "더 정밀하게" 요청 시 model escalation 별 path). **s6-r18 (2026-05-18) 정정** — 이전 r0~s6-r17 박제의 "cached lazy" 원칙은 server todo `todo-lighthouse-kb-server.md` §0 **D-2-2** (사용자 명시 결정 = "색인 = 분석 완료" 단순 일관 mental model) 으로 정정됨. (§3.15.3 / §3.15.4 / §3.15.5 / §3.15.6 단원 본문 sweep 은 별 turn 의무, marker 만 박제)
10. **FTS5 tokenizer trigram** (한국어 필수) (§3.7, §3.12)
11. **n×m KB 운영** (r4) — collection = 사용자 임의 폴더 선택 (path-based, project 종속 X). LlmConfig.KbCollections (OS 사용자 전역) 에 등록. m 개 active → SQLite ATTACH union 검색. KbManagerDialog (ApplicationSettingsDialog 진입 버튼). 원본 사본 없음. read-only collection 은 read OK, write fail.
12. **server phase 흡수 — 대안 B** (r5) — parent Phase 1 의 **§4.5 전체 / §4.1 첫 task (.gitignore) / §4.6 (5.knowledge-base.md) / §4.8 의 Promaker 통합 의존 항목** 은 본 Phase 1 에서 진행하지 않음. server phase (`todo-lighthouse-kb-server.md` Phase S5) 가 흡수. 본 Phase 1 = LightHouse lib 본체 (§4.2/§4.3/§4.4) + lib 자체 unit test 만. 사유: parent Phase 1 의 §4.5 가 server phase 진입 시 60%+ throwaway / 사용자 데이터 migration noise / yo-yo commit history 6건 회피.

### 다음 세션에서 확정해야 할 보류 항목 (Phase 1 lib 본체에 한정)

대안 B (r5) 로 §4.5 / §4.6 SKIP — 그 단원 의존 보류 항목은 본 표에서 제거하고 `kb-server.md §4.3` 미확정 표로 이전.

| 항목 | 위치 | 권장 default | 확정 시점 |
|---|---|---|---|
| ~~KnowledgeBase facade 형식 (record-of-functions vs interface)~~ | §3.18.1 | ~~record-of-functions~~ | **r9 결정**: record-of-functions 채택, `KnowledgeBase.fs` 적용 완료 |
| ~~`<PrivateAssets>all</PrivateAssets>` 적용 범위~~ | r8 메타리뷰 m2 | ~~PdfPig + Microsoft.Data.Sqlite + DocumentFormat.OpenXml 3 package 한정 적용~~ | **r9 결정**: 3 package 한정 적용, `Ds2.LightHouse.fsproj` 적용 완료 |
| 본 todo 파일 위치 git mv 여부 (Apps/Promaker/Docs/ → Solutions/Core/Ds2.LightHouse/doc/) | §6.14 | Phase 1 완료 commit 직전 mv | **r7 보류 박제** — Phase 1 완료 후 별도 confirm |
| **Apps/Promaker/Directory.Packages.props 에 OpenXml 3.5.1 명시 (MSB3277 충돌 해소)** | r9 신설 | 3.5.1 명시로 transitive 통일 — Promaker.csproj output 의 두 버전 충돌 제거 | **Phase 1 완료 직전 또는 server phase Phase S3 결정 시 함께** |
| ~~ModelContextProtocol.AspNetCore 1.2.0 → 1.3.0 업그레이드 여부~~ | §2, §4.1 | ~~별 release note 검토 + nuget list 후 결정~~ | **r7 결정**: 본 Phase 1 (lib only — MCP 무관) 무관, server phase Phase S3 P2 결정 시 함께 |

**server phase 로 이전된 항목** (`kb-server.md §4.3` 미확정 표 참조):
- `attachment_*` 의 KB root 도달 경로 — server `§3.8` session-based routing 으로 대체 (parent §3.18.2 의 채택안 (a) 는 r5 SKIP)
- SQLite ATTACH limit (10) 초과 시 안내 — server `§3.8` Q2 의 hard fail 가드로 흡수

### 다음 세션 즉시 할 일 (r10 갱신)

**Phase 1 lib 본체 + lib unit test 종결 — server phase 진입 confirm 대기**.

1. **본 todo + `todo-lighthouse-kb-server.md` 동시 정독** — 특히 §0 / §3.0 / §3.11 / §3.18 / §6 주의 사항 16건 / **r10 박제 (§4.8 lib unit test 99 Fact 통과 + lib 본체 잠재 버그 3건 fix + 자가 검열 6건 반영)**
2. **(보류 사용자 confirm)** **server phase 진입 결정** — `todo-lighthouse-kb-server.md` Phase S0 → S1 (또는 본 todo 의 r5 통합 vs 별도 todo 유지 재결정). 진입 시점에 본 todo 의 §3.9 / §3.10 / §3.18.2 / §4.1 / §4.5 / §6 m15 / §6 m16 회귀 매트릭스 적용 (§4.1 보류 박스 매트릭스 + `kb-server.md §3.14`).
3. **(보류 사용자 confirm)** **본 todo 파일 git mv** — `Apps/Promaker/Docs/` → `Solutions/Core/Ds2.LightHouse/doc/` (§6.14). Phase 1 완료 시점 결정 박제 (r7 보류 박제).
4. **(보류 사용자 confirm)** **OpenXml transitive 충돌** (r9 보류 신설) — `Apps/Promaker/Directory.Packages.props` 에 3.5.1 명시 검토 (MSB3277 2건). Phase 1 완료 직전 또는 server phase 진입 시 함께.
5. **(완료)** ~~MEMORY.md `## Project` 등록 (§6.11)~~ — r10 박제 시점에 등록 완료 ([Phase 1 LightHouse lib + tests 종결](lighthouse-phase1-lib-tests-done.md)).
6. **r10 자가 검열 잔여 (Phase 2 보류)** — M3 (swapShadow try-finally 바깥 docstring) / m1/m2/m3/m6/m7/m8 / r9 C1 drift 보호 (ATTACH 강제 실패 + conn 누수 0 검증) / r9 M4 cross-talk 보호 (2 KB instance 병렬 open) — 모두 Phase 2 (server phase 진입 후 또는 별도 보강 PR).
7. **commit 은 단계별 별도 confirm** (memory: `feedback_commit_authorization`)

### r10 자가 검열 (sub-agent general-purpose) 처리 결과
**리포트 통계**: Critical 0 / Major 3 / Minor 9
- M1 (KnowledgeBaseTests 빈 active 셋 — search/list empty 검증 누락) — **적용**: `kb.Search` + `kb.List` empty 검증 추가 (3 line) + IDisposable wrap 제거
- M2 (Indexer 정상 ingest 경로에도 ClearPool 필요) — **적용**: `use conn` → `try ... finally Close + ClearPool + Dispose` 패턴을 dbExists/!needsRebuild + new DB 두 분기에 추가 (방어적, 미래 회귀 차단)
- M3 (swapShadow try-finally 바깥) — **보류 (SSOT 명문화)**: 현 SSOT (§3.17 swap 실패 → reraise → caller retry) 가 이미 명시. docstring 보강만 Phase 2.
- §4.8c 누락 (boundary 9/10 ATTACH) — **적용**: `MaxAttachedDbs = 10` 까지 실 색인된 10 collection 동시 ATTACH 통과 Fact 추가
- §4.8a 누락 (Chunker 정확 200/500/501 token) — **적용**: 정확 한도 token = 1 chunk / 한도+1 token = ≥2 chunk 2 Fact 추가
- m4 (OoxmlExtractor segment count brittleness) — **적용**: `Assert.Equal(5, ...)` → `Assert.True(>= 5) + Assert.Contains` 약화
- m9 (FTS5 AU trigger 검증 누락 — r9 자가 검열 M2 잔여 우려 마지막 분기) — **적용**: Chunks UPDATE → ChunksFts AU trigger 동기 갱신 Fact 추가
- m1 (IDisposable wrap redundant `()`) — **흡수**: M1 적용 시 함께 제거
- m2/m3 (Assert.Throws `|> ignore` 이중 / null bytes 검증 약함) — **보류 Phase 2**: 사소
- m6 (test cleanup swallow log) — **보류 Phase 2**: silent 누수 시 다음 빌드에서 인지
- m7 (TestInit `bool` return → unit) — **보류 Phase 2**: 의미 미흡하지만 동작 무영향
- m8 (chat path cross-PR BOM strip 검토) — **보류 cross-PR 후보**: `Ds2.LlmAgent.AttachmentClassifier` 의 chat text 첨부도 동일 leading U+FEFF 위험. `todo-llm-chat-attachment.md` 와 함께 별도 commit 검토.
- **r9 박제 결정 정합 확인**: r9 C1 drift 보호 (ATTACH 강제 실패 + conn 누수 0) + r9 M4 cross-talk 보호 (2 KB instance 병렬) → **Phase 2 보류** (test 추가 권장이나 본 PR scope 외).

### r9 외부 --review 처리 결과
- C1 (KnowledgeBase.openCollections ATTACH 실패 시 conn 누수) — **적용**: try-with reraise + conn.Dispose() (`KnowledgeBase.fs:73-94`)
- M1 (Searcher unused `docId` binding) — **이미 적용** (자가 검열 단계에서 Searcher.fs:213/254 모두 `Some (kbIdx, _)`)
- M2 (Indexer.ingestFile 결과 폐기) — **이미 적용** (자가 검열 m6 단계에서 `(string * FileIngestResult) array` 반환으로 변경)
- M3 (Searcher fileId parse 실패 silent fallback) — **적용**: log warn + 명시 빈 결과 + `Hint = Some "invalid fileId"` (`Searcher.fs:104-119`)
- m1 (SqliteStore.deleteDocument 미사용) — **적용**: 보존 사유 주석 (매뉴얼 purge / unit test cleanup)
- m2 (ensureSchema 반복 호출 비용) — 무시 (idempotent + 비용 미미)
- m3 (phrase quoting) — **이미 적용** (자가 검열 M1 단계에서 token split + per-token phrase, implicit AND)
- m4 (nested collection `.lighthouse-kb`) — 무시 (예외 케이스)
- m5 (.bak 즉시 삭제 정책) — **적용**: todo §3.17 미명시 + atomic 보장 명시 주석 (`SqliteStore.fs swapShadow`)

### r8 외부 메타리뷰 (`--review` 3 R) 처리 결과
- M1 (shim 정체성/시그니처 박제, 2/3) — **적용**: `AttachmentClassifier.fs:115` 의 shim 에 `: TextEncoding.TextEncodingDetect` return type annotation + "영구적 호환 shim — 임시 마이그레이션 아님" docstring 추가
- M2 (Promaker CodePages 직접 참조 정당성, 2/3) — **적용 (a 안 채택)**: 직접 참조 유지 + 주석 보강 — "entry-point assembly 의 명시적 자기 의존 + LightHouse 가 CodePages 의존 끊으면 NuGet restore fail-fast 안전망"
- M3 (ImageFormat 주석 "컴파일러 강제" 오류, 1/3 outlier 검증 ✓) — **적용**: `ImageFormat.fs:7-8` 정정 — `imageFormatOf` 의 wildcard 분기로 컴파일 강제 안 됨 명시, drift test reflection 가 런타임 보호
- m1 (logger 이름 변경 사용자 config drift, 2/3) — **적용**: `Solutions/Core/Ds2.LlmAgent/CLAUDE.md` 의 Logging.fs 행에 분기 박제 1줄 추가
- m2 (LightHouse → KB transitive 비대화, 1/3 검증 ✓) — **보류**: §4.4 facade 진입 후 결정. **반론 포함**: 핵심 type (`FileKind`/`ImageFormat`/`KnowledgeBase`) 은 transitive 유지 필요 — PrivateAssets=all 은 PdfPig / Sqlite / OpenXml 3 package 한정 적용 권장. 보류 항목 표 1 행 추가
- m3 (line 박제 fragility, 2/3) — **적용**: `Solutions/Core/Ds2.LlmAgent/CLAUDE.md` 의 AttachmentClassifier 행 line 박제 (38-86 / 115) 를 함수명 anchor 로 약화
- m4 (todo 진행 표시 갱신 누락, 1/3) — **흡수**: 본 r8 박제로 처리. §4.3 commit 에 본 todo 포함
- 명시 미적용: shim eta-expansion (현 형태 유지 권장, 본 메타리뷰 의견 = "현 유지") / Log internal 가시성 (수정 불요)

### 본 작업이 영향을 줄 다른 활성 todo (cross-PR)
- `Apps/Promaker/Docs/todo-lighthouse-kb-server.md` (s0, 후속 phase) — service 도입 design. 본 Phase 1 완료 후 진입. parent r4 의 §3.9 / §3.10 / §3.18.2 / §4.1 / §4.5 / §6 m15 / §6 m16 회귀 매트릭스 보유.
- `Solutions/Core/Ds2.LlmAgent/doc/todo-llm-chat-attachment.md` (active, 318 line) — 정책 19 (AttachmentClassifier SSOT) + ImageFormat DU wire 진행 중. Phase 1 4.2a 진입 전 그 todo 의 최근 commit 동기화 의무 (§6.12).
- `Apps/Promaker/Docs/todo-dock-layout.md` — Phase 1 4.7 (Attachments dock 패널) 진입 시 anchor 추가 동시 PR (§6 m9).

### 본 turn 까지의 review 결과 처리 통계 (외부 reviewer 만)
- r1~r3 누계: Critical 8 / Major 18 / Minor 37 / 충돌 1 = 64건
- r6 `--inspect 3` 누계: Critical 6 / Major 23 / Minor 15 = 44건 (hallucination 기각 0, consensus 2건: CR1 = 3/3, MA1/MA9 = 2/3)
- **외부 reviewer 총 누계: Critical 14 / Major 41 / Minor 52 / 충돌 1 = 108건 반영**
- r4 (n×m KB) / r5 (대안 B) 는 사용자 design 입력 — review 결과 아님 (별도 추적).

---

## 1. 작업 목표

LLM chat 이 외부 사양 문서 (.pdf, .docx, .pptx, .xlsx, .txt, .md) 를 "도메인 지식" 으로
참조할 수 있도록 **사전 색인 (RAG) + MCP 도구** 인프라를 구성. LLM 은 색인된 문서를
`attachment_*` 도구로 검색·인용하여 모델링 (apply_model_doc 등) 의 정확도를 끌어올린다.

---

## 2. 배경 / 맥락 (검증 완료)

### 기존 인프라
- Promaker 의 LLM chat 은 in-process Kestrel + ModelContextProtocol.AspNetCore **1.2.0** 으로
  HTTP MCP 서버 운영 (1.3.0 출시 2026-05-08 — 업그레이드 검토 항목 §4.1).
  위치: `Apps/Promaker/Promaker/LlmAgent/McpHostService.cs`
- tool 등록은 `[McpServerToolType]` + `WithToolsFromAssembly()` 자동 스캔.
  주력 진입점: `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs`
- system prompt 는 `Apps/Promaker/Promaker/LlmAgent/Prompts/*.md` (CLAUDE.md 제외) EmbeddedResource glob.
  `PromptLoader.cs:46-66` 의 `LoadEmbeddedAll` 이 glob + natural sort (NaturalComparer) 로 baseline 합성.
  3-tier 조합: baseline (assembly resource) → operator (`<exedir>/Prompts/`) → user (`%APPDATA%/Promaker/Prompts/`).
  현재 baseline = `1.entities`, `2.modeling`, `3.tooling`, `4.attachments`, `9.environment`, `facts` (6 파일).
- LLM provider: `Solutions/Core/Ds2.LlmAgent/` F# 프로젝트 (ClaudeCliProvider, CodexCliProvider, 그리고 Anthropic/OpenAI/Ollama API providers via Microsoft.Extensions.AI).
- **`Solutions/Core/Ds2.LlmAgent/AttachmentClassifier.fs` 는 활성 코드** (정정):
  - `LlmChatViewModel.Attachments.cs:265, 270, 348` 가 `classify` + `Classification.AcceptImage` 호출 (UI thread). `:360` 가 `detectEncoding` 호출 (background thread).
  - `App.xaml.cs:148` 가 `CodePagesEncodingProvider.RegisterProvider` 로 CP949 fallback 활성화 (line 147 은 주석)
  - `Solutions/Tests/Ds2.LlmAgent.Tests/AttachmentClassifierDriftTests.fs` 가 **Fact 13건** (현행, grep `^\[<Fact>]` 결과) 으로 SSOT 회귀 보호 — 신규 case 추가 시 본 todo 갱신
  - 광범위 변경은 별도 PR 권고 — done/`done-llm-chat-attachment.md:222` 의 전례 + active `Solutions/Core/Ds2.LlmAgent/doc/todo-llm-chat-attachment.md` (318 line) 가 정책 19 진행 중 — Phase 1 4.2a 진입 전 그 todo 의 최근 commit 동기화 의무 (§6.12)
- `Promaker.csproj` 에 이미 포함된 패키지: **PdfPig, Anthropic, OpenAI, OllamaSharp, Microsoft.Extensions.AI, ModelContextProtocol(.AspNetCore)**.
- `Prompts/4.attachments.md` 는 *prompt injection 방어* 룰임 — 본 작업과 결이 다름. 본 작업용 system prompt 는 **`Prompts/5.knowledge-base.md` 신설** (4.attachments.md 와 분리 유지).
- `Solutions/Directory.Packages.props` 와 `Apps/Promaker/Directory.Packages.props` 가 **두 위치 분리** — CPM 적용 시 등록 위치 주의 (§4.1).

### 기존 dependency 그래프
```
Promaker.csproj (C#, net9.0-windows, WPF)
  └─ Ds2.LlmAgent (F#, net9.0)
      ├─ Ds2.Core (F#)
      └─ Ds2.Editor (F#)
```

---

## 3. 결정된 설계

### 3.0 두 경로 완전 분리 SSOT (가장 상위 invariant)

| 경로 | 진입점 | 분류기 | LLM 전달 | 색인 |
|---|---|---|---|---|
| **chat image/text/pdf drop** | 채팅 입력란 drop/paste | `Ds2.LlmAgent.AttachmentClassifier` (현 SSOT, 변경 0) | 즉시 multimodal content block (현행 그대로) | **반영 안 함** |
| **KB ingest** | Attachments dock 패널 (별도 UI) | `Ds2.LightHouse.classifyForKb` (신규, 독립) | 색인 후 MCP tool 통해 *검색 시점* 의 LLM 이 요청 (eager VLM caption — s6-r18 D-2-2: 색인 시점에 모든 image 에 VLM 1회 호출 + ImageCache.CaptionText 저장. r0~s6-r17 박제의 "cached lazy" 는 본 결정으로 정정) | **색인** |

두 경로는 **서로의 상태/분류기/저장소를 공유하지 않음**. 한 경로 변경이 다른 경로에 영향 없음을 invariant 로 한다. chat 에서 drop 한 image 가 KB 에 들어가지 않고, KB 의 image 가 chat 에 자동 inline 되지도 않음.

### 3.1 신규 project — `Ds2.LightHouse` (F#)
- 위치: `Solutions/Core/Ds2.LightHouse/Ds2.LightHouse.fsproj`
- TargetFramework: `net9.0` (WPF 의존 0)
- **`Ds2.Core` 미참조** — entity 와 무관, 독립 base
- 역할: 첨부물/문서 도메인의 **base 인프라**. KB(색인·검색) + 공통 텍스트 인프라 (인코딩 추정, 이미지 포맷 enum).

### 3.2 dependency 방향 — `LlmAgent → LightHouse` (역방향)
```
Ds2.Core (F#)                       ── 기존, 무관
Ds2.Editor (F#)                     ── 기존, 무관
    ▲
    │ (LightHouse 는 Core / Editor 모두 참조 안 함 — invariant)

Ds2.LightHouse (F#, 신규)            ── 첨부물/문서 base
    │   NuGet: PdfPig (이전), DocumentFormat.OpenXml (신규),
    │          Microsoft.Data.Sqlite (기등록), log4net (기등록),
    │          System.Text.Encoding.CodePages (기등록)
    ▲
    │ ProjectReference 추가
    │
Ds2.LlmAgent (F#, 기존)              ── LightHouse 의 detectEncoding / ImageFormat 만 참조
    │                                  (chat 측 AttachmentClassifier 는 영구 잔류, 변경 0)
    ▲
    │
Promaker (C# WPF)                    ── MCP tool wrapper + UI
```

이유: LightHouse 가 더 *기반* 도메인이라 LLM 무관 / 순환 회피 / Ev2.Backend 등이
LlmAgent 끌어들이지 않고도 색인 활용 가능 / net9.0 순수 → server-side 가능.

### 3.3 LightHouse 의 타입 — KB 전용 FileKind 신설 (Classification 통합 X)
- `Ds2.LightHouse.FileKind` DU = `{ Pdf | Docx | Pptx | Xlsx | Text | Markdown | Image | Unsupported of ext }`
  — KB 추출기 라우팅 전용. Phase 별 활성 case 확대.
  - **Task 7 (r6) 박제** — `Image` case = standalone image 파일 (PNG / JPEG / GIF / WEBP raw + EMF / WMF Metafile→PNG 변환).
  - `RefUnit.Image` (RefLocator.fs) 와 동명 → caller 는 `FileKind.Image` qualification 의무 (`OutlineNodeType.*` 패턴 정합).
- chat 측 `Ds2.LlmAgent.Classification` 은 **영구 잔류, 변경 0**. 두 분류기 독립 (3.0 SSOT).
- 두 분류기는 공통 입력 (파일 path) 만 공유. 출력 DU 다름.

### 3.4 LightHouse 의 공통 인프라 — chat 도 참조
LightHouse 가 base 라 chat 측 분류기도 *순수 함수성 인프라* 는 LightHouse 의 것을 참조:
- `Ds2.LightHouse.TextEncoding.detectEncoding` (UTF-8/CP949/UTF-16) — 별도 모듈 분리, KB TextExtractor + chat 텍스트 첨부 둘 다 사용
- `Ds2.LightHouse.ImageFormat` enum — 현 `Ds2.LlmAgent/LlmMessage.fs:4` 에서 이전, KB 이미지 메타 + chat 이미지 첨부 둘 다 사용

이전 시 동반 이동: `isStrictDecodable`, `tryCp949`, `TextEncodingDetect` record, log4net `Log.provider` 의존.

### 3.5 LightHouse API 의 entity 미참조 약속
LightHouse 의 public API 는 다음 타입을 인자/반환에 사용하지 않음:
- `Ds2.Core` 의 entity 타입 (`Project`, `DsSystem`, `Flow`, `Work`, `Call`, `ApiDef`, `Arrow` 등)
- `Ds2.Editor` 의 editor-domain 타입 (`SaveSession`, `EditorState` 등)

**r4 갱신** — collection 식별은 단일 `projectKbRoot` 가 아닌 `string[] activeCollectionPaths` 만 받음 (사용자가 LlmConfig 에 등록한 collection path 들의 active subset). MCP tool 4종은 인자에 collection 정보 없음 — 서버 측이 active 셋 fix (§3.10). Promaker 측 turn 시작 시 LightHouse 에 active paths 주입.

### 3.6 색인 구조 — 3-layer
```
ingest                index store              MCP tools
PDF  → PdfPig         doc meta (sqlite)        attachment_list
DOCX → OpenXml        outline (TOC)            attachment_outline
PPTX → OpenXml        FTS5 (lexical, trigram)  attachment_search
XLSX → OpenXml        sqlite-vec (Phase 4)     attachment_read
TXT/MD → 그대로
```

### 3.7 검색 방식 — Phase 1 FTS5 lexical-only, **trigram tokenizer (한국어 필수)**
- `unicode61` 는 한국어 어절+조사 결합 ("컨베이어가" / "컨베이어를" 별 token) 으로 사실상 한국어 검색 불가. **`tokenize='trigram'`** (SQLite 3.34+ 내장) 채택. 색인 크기 ~3배 trade-off 수용.
- Phase 1 MVP: enrichment / embedding 모두 skip, FTS5 trigram 만으로 출발
- Phase 4 에서 hybrid (BM25 + vector, RRF) 로 확장
- embedding backend 는 `Microsoft.Extensions.AI.IEmbeddingGenerator<,>` 추상화 채택
  - ⚠ ONNX backend 는 직접 wrapper 필요 (일등시민 X). OllamaSharp 가 `IEmbeddingGenerator` 직접 구현
  - 기본 backend 는 **로컬** (ONNX/Ollama), 외부 API (OpenAI/Voyage) 는 **opt-in only** — KB raw 외부 송신 방지
- 모델 후보: bge-m3 (1024d), multilingual-e5-large-instruct, jina-v3 — Phase 4 진입 시 재검토 (단순히 "bge-m3 small" 같은 비공식 명명 사용 금지)

### 3.8 청킹 — 구조 우선
PDF 페이지, PPT 슬라이드, Excel 시트 단위 우선 → 너무 크면 행/단락 보조 분할 (200~500 token 한도).
- **xlsx**: 컬럼 헤더 + 행 그룹 (10~50 행 packed) 패턴. 빈 행 / 머지 셀 / 표 영역 자동 인식.
citation 가독성 최우선 ("스펙 §3.2 Conveyor" 단위 인용).

### 3.9 저장 위치 — path-based 사용자 자유 (r4 전면 재작성)

> ⚠ **service 도입 시 전면 회귀** — collection 식별이 사용자 폴더 path → server-side guid 로 바뀌고, 사용자 폴더에 흔적 0 (사본 정책 부활). 본 단원의 layout / `.gitignore` 정책 / read-only NAS 시나리오 모두 `kb-server.md` §3.5 (사본 정책) + §3.10 (server storage layout) 으로 대체.

> ℹ️ **id namespace 주의**: 본 §3.9 본문에서 사용하는 `r4-D1` / `r4-Q2` 등 임시 결정 식별자는 r4 시점의 *parent 내부 ad-hoc* 식별자다. `kb-server.md §0` 의 D-id 정의표 (R1/Q1-Q4/D1-D7/α/N5/N6/L1/L2) 와 **별개 namespace** — server.md 의 D-id 와 의미 매핑 없음.

**collection 의 정의**: 사용자가 KbManagerDialog 의 folder picker 로 선택한 임의 폴더 1개 = 1 collection. project 와 무관 (회사 공유 폴더 / 로컬 폴더 / 네트워크 드라이브 모두 OK).

**물리 layout** — 사용자 폴더 안에 LightHouse 가 자동 생성한 hidden subfolder:
```
<사용자 선택 폴더>/                    ← 사용자가 임의 선택 (path-based)
  ├─ plant-spec-v3.pdf               ← 원본 (사용자가 둔 것, 그대로 SSOT)
  ├─ io-list-2026.xlsx
  ├─ manual.docx
  └─ .lighthouse-kb/                  ← LightHouse 가 자동 생성 (hidden)
      ├─ index.db                     ← SQLite (Phase 1 필수)
      └─ blobs/images/<sha256>.png    ← Phase 2 부터: PDF/PPTX 안에서 추출한 raster 이미지만
```

**원본 사본 정책 변경 (r4)**:
- 원본 PDF/DOCX/PPTX/XLSX/TXT/MD 등은 **사용자 폴더 안에 그대로** — 사본 보관 X
- `Documents.OriginalPath` = collection root 기준 상대경로 또는 절대경로 (구현 시 결정). `Documents.StoredPath` 컬럼 자체 폐기.
- FileHash 는 *원본 파일의* hash. 변경 감지 / idempotent ingest 의 key.

**registry 와 active 셋**:
- 사용자가 등록한 collection path 목록은 `LlmConfig.KbCollections` (OS 사용자 전역, `%APPDATA%\Dualsoft\Promaker\Settings\llm-config.json`) 에 persist
- 형식: `[ { path: string, active: bool } ]` (alias 없음 — r4-Q3)
- 한 사용자가 어느 Promaker project 를 열든 같은 collection list 가 보임 (r4-D1)
- project 와 무관 (r4-Q1 의 "project 종속 X" 결정)

**read-only collection 정책** (r4-Q2):
- write (최초 색인 / 재색인 / IndexerVersion bump 자동 재색인) → fail + 사용자 안내. 자동 trigger 금지.
- read (search / `attachment_read`) → OK. SQLite `Mode=ReadOnly` 로 open.
- 시나리오: 회사 IT 가 한 번 색인 → `\\server\사양서\라인A\.lighthouse-kb\index.db` 까지 만들어 둠 → 각 엔지니어가 read-only 로 attach.

**`.gitignore`** *(r5 SKIP — server phase 흡수)*: 사용자가 collection 으로 *Promaker repo 안 폴더* 를 선택했을 때를 위해 root `.gitignore` 에 `.lighthouse-kb/` 추가 — **§4.1 첫 task 였으나 대안 B 로 SKIP**. server phase 진입 후엔 사용자 폴더에 흔적 0 정책이라 영구 불필요.

**cross-collection 공유**:
- image blob (sha256) 은 각 collection 의 `.lighthouse-kb/blobs/images/` 안 분리 (cross-collection 공유 X, 단순함 우선)
- cross-collection 캐시는 Phase 5+ 보류 (기존 §3.9 정책 유지)

### 3.10 MCP tool surface — 4종

> ⚠ **service 도입 시 host 위치 이동** — tool surface 자체는 그대로. host 위치만 Promaker in-process → service 측으로 이동. 본질 단원은 `kb-server.md` §3.1 (책임 분리표 의 "MCP search host" 행) + §3.13 (Promaker 측 `AttachmentTools.cs` 삭제 / `PromakerToolNames.All` 환원). API endpoint 목록은 `kb-server.md` §3.9.

| 도구 | 목적 |
|---|---|
| `attachment_list()` | 등록 문서 목록 + 메타 (active 셋의 union) |
| `attachment_outline(fileId)` | TOC / 시트명 / 슬라이드 제목 트리 |
| `attachment_search(query, fileId?, k=5)` | (Phase 1) FTS5 trigram → (Phase 4) hybrid. active 셋 union 검색 (서버 측 fix) |
| `attachment_read(fileId, ref, includeImages?, caption_only?)` | 특정 page/sheet/slide raw text (+ Phase 2 부터 image 동봉/캡션) |

**r4 결정 — tool surface 변경 0**: collection 선택은 *사용자가 LlmConfig 에서 active toggle* 함. LLM 은 어떤 collection 이 있는지 모르고, 활성 셋만 결과로 받음 (서버 측 fix). `collectionIds[]` 같은 인자 추가 없음 — LLM 인지 부담 0, system prompt `5.knowledge-base.md` 변경 없음.

**fileId 의 unique 보장**: m 개 collection ATTACH 시 각 collection 의 `Documents.Id` 는 같은 값 가능. fileId 는 `<collection-index>:<documents-id>` 또는 `<collection-name>:<documents-id>` 형태로 합성하여 cross-collection unique 보장 (구현 시 확정, §4.4).

모두 read-only → `LlmTurnContext` 의 mutation visibility 규칙 무관. `PlanVisibilityHint` 불필요.

**quota 가드 (tool wrapper hard enforce)**:
- `maxCallsPerTurn` = 8
- `maxExcerptTokens` = 4000
- `maxCumulativeTokens` = 16000

**`attachment_search` 반환 JSON schema (SSOT)** — §3.14 자연어 흐름의 예시는 본 schema 의 인스턴스:
```json
{
  "results": [
    {
      "fileId": "string",
      "fileName": "string",
      "ref": "string",                  // RefLocator 저장형 (3.13)
      "outlinePath": "string",          // "3. 설비 사양 > 3.2 Conveyor"
      "score": "number",                // BM25 또는 hybrid 결합
      "excerpt": "string",              // ≤ maxExcerptTokens
      "tokenCount": "integer",
      "hasImages": "boolean"            // Phase 2 부터 의미. Phase 1 은 항상 false
    }
  ],
  "moreAvailable": "boolean",
  "hint": "string?"                     // 0-hit 등 회복 안내
}
```

### 3.11 AttachmentClassifier 통합 정책 — **부분 통합 축소 (확정)**

3.0 의 두 경로 분리 SSOT 에 따라:

**`Ds2.LlmAgent.AttachmentClassifier` 전체 영구 잔류** (chat 측 SSOT, 변경 0):
- `Classification` DU, `classify`, `textExtensions`/`rejectedExtensions`/`extensionlessTextNames`, UI Drop/Paste 호출 경로 모두 그대로.
- C# interop break 0 (Classification.AcceptImage 등 9 fact drift test + 5 wire 진입점 무영향).

**LightHouse 가 독립적으로** 다음 신설:
- `FileKind` DU + `classifyForKb` + `KbExtensions` (3.3)
- `IExtractor` + `PdfExtractor / OoxmlExtractor / TextExtractor` (Phase 1)

**공통 함수성 인프라만** LightHouse 로 이전 후 LlmAgent 가 참조 (3.4):
- `detectEncoding` 외 부속 (`isStrictDecodable`, `tryCp949`, `TextEncodingDetect`, `Log.provider`)
- `ImageFormat` enum (현 LlmMessage.fs:4)

**chat 측 `Classification` 에 신규 case 추가 없음** — KB 색인 경로가 독립 분류기를 쓰므로 `RejectExtension` 의미 충돌 (보안 영구 거부 vs KB 라우팅) 자체가 발생 안 함. chat 에 .docx drop → `RejectUnknown` (현행). UI 가 별도로 "KB 색인 메뉴 이용" 안내를 띄우고 싶으면 chat UI 측에서 RejectUnknown 응답 받은 후 별도 분기 — `AttachmentClassifier` SSOT 변경 없음.

**Task 7 (r6) — image 활성 정책 변경** — `Classifier.rejectedExtensions` 에서 raster 4종 (`.png` / `.jpg` / `.jpeg` / `.gif` / `.webp`) 제거 + vector 2종 (`.emf` / `.wmf`) 도 활성. `supportedExtensions` 에 7 매핑 (모두 `FileKind.Image`) 추가. 즉 standalone image 파일이 KB 색인 진입 → `ImageExtractor` 가 `ExtractedDocument { DocType = Image; Outline = [||]; Segments = [||]; Images = [|단일|]; Title = filename }` 박제 → 기존 `Indexer.ingestImagesIntoStore` flow (icon 가드 / sha256 dedup / VLM caption 주입) 그대로 재사용. **BMP / TIFF / SVG / ICO / HEIC 는 Phase 3 backlog** — raster 변환 (Bitmap→PNG 재인코딩) 또는 SVG raster 추가 의존성 (`Svg.Skia`) 필요. chat 측 `AttachmentClassifier` 의 image accept 정책과 무관 (두 경로 분리 SSOT, §3.0).

### 3.12 index.db schema (Phase 1 환원판 — 이미지 schema Phase 2 로 이전)

```sql
-- ── Phase 1 (MVP) ─────────────────────────────────────────────────────
CREATE TABLE Documents (
  Id              INTEGER PRIMARY KEY,
  FileHash        TEXT NOT NULL UNIQUE,         -- SHA-256 (원본 파일의 hash)
  OriginalPath    TEXT NOT NULL,                -- collection root 기준 상대경로 (cross-collection 이동 가능)
  DocType         TEXT NOT NULL,                -- pdf|docx|pptx|xlsx|txt|md
  SizeBytes       INTEGER NOT NULL,
  PageOrSheetCnt  INTEGER,
  Title           TEXT,
  SummaryText     TEXT,                         -- (선택) Phase 4 enrichment
  IndexerVersion  TEXT NOT NULL,                -- schema 변경 시 자동 재색인 트리거
  IngestedAt      TEXT NOT NULL                 -- ISO-8601
);
-- StoredPath 컬럼 폐기 (r4) — 원본 사본 보관 안 함, OriginalPath 가 사용자 폴더 안 실제 파일을 가리킴.
-- IX_Documents_Hash 는 UNIQUE 컬럼 제약과 중복 — 명시 INDEX 제거, UNIQUE 만 유지.

CREATE TABLE OutlineNodes (
  Id          INTEGER PRIMARY KEY,
  DocumentId  INTEGER NOT NULL REFERENCES Documents(Id) ON DELETE CASCADE,
  ParentId    INTEGER REFERENCES OutlineNodes(Id) ON DELETE CASCADE,
  Ordinal     INTEGER NOT NULL,
  NodeType    TEXT NOT NULL,                    -- section|page|sheet|slide|heading
  Label       TEXT NOT NULL,
  RefLocator  TEXT NOT NULL                     -- 저장형 (3.13)
);
CREATE INDEX IX_Outline_Doc      ON OutlineNodes(DocumentId);
CREATE INDEX IX_Outline_Parent   ON OutlineNodes(ParentId);

CREATE TABLE Chunks (
  Id          INTEGER PRIMARY KEY,
  DocumentId  INTEGER NOT NULL REFERENCES Documents(Id) ON DELETE CASCADE,
  OutlineId   INTEGER REFERENCES OutlineNodes(Id) ON DELETE SET NULL,
  RefLocator  TEXT NOT NULL,
  Ordinal     INTEGER NOT NULL,
  TokenCount  INTEGER NOT NULL,
  Text        TEXT NOT NULL
);
CREATE INDEX IX_Chunks_Doc       ON Chunks(DocumentId);
CREATE INDEX IX_Chunks_Outline   ON Chunks(OutlineId);

-- FTS5 external content mirror (한국어 대응 필수 — trigram)
-- contentless (content='') 가 아니라 content='Chunks' 의 external content. 본문은 Chunks 에만 1부.
CREATE VIRTUAL TABLE ChunksFts USING fts5(
  Text,
  content='Chunks', content_rowid='Id',
  tokenize='trigram'
);

-- FTS5 sync trigger 3종 (본문 명문화)
CREATE TRIGGER Chunks_AI AFTER INSERT ON Chunks BEGIN
  INSERT INTO ChunksFts(rowid, Text) VALUES (new.Id, new.Text);
END;
CREATE TRIGGER Chunks_AD AFTER DELETE ON Chunks BEGIN
  INSERT INTO ChunksFts(ChunksFts, rowid, Text) VALUES ('delete', old.Id, old.Text);
END;
CREATE TRIGGER Chunks_AU AFTER UPDATE ON Chunks BEGIN
  INSERT INTO ChunksFts(ChunksFts, rowid, Text) VALUES ('delete', old.Id, old.Text);
  INSERT INTO ChunksFts(rowid, Text) VALUES (new.Id, new.Text);
END;

CREATE TABLE Meta (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
-- Meta 필수 키: schema_version, indexer_version, tokenizer, created_at

-- ── Phase 2 (이미지 인프라 도입 시 추가) ──────────────────────────────
-- ALTER TABLE Chunks ADD COLUMN ImageCount INTEGER NOT NULL DEFAULT 0;
-- CREATE TABLE ImageCache (
--   ImageHash    TEXT PRIMARY KEY,             -- sha256
--   StoredPath   TEXT NOT NULL,                -- blobs/images/<sha256>.<ext>
--   MimeType     TEXT, Width INTEGER, Height INTEGER,
--   CaptionText  TEXT,                         -- nullable, on-demand
--   CaptionAt    TEXT,                         -- ISO-8601
--   CaptionModel TEXT                          -- generation tier invalidation 키
-- );
-- CREATE TABLE ImageReferences (
--   DocumentId INTEGER NOT NULL REFERENCES Documents(Id) ON DELETE CASCADE,
--   ChunkId    INTEGER REFERENCES Chunks(Id) ON DELETE SET NULL,
--   ImageHash  TEXT NOT NULL REFERENCES ImageCache(ImageHash),
--   RefLocator TEXT NOT NULL,                  -- "p=14#img=2"
--   Ordinal    INTEGER NOT NULL,
--   PRIMARY KEY (DocumentId, ImageHash, RefLocator, Ordinal)
-- );
-- CREATE INDEX IX_ImgRef_Chunk ON ImageReferences(ChunkId);

-- ── Phase 4 (vector / enrichment) ─────────────────────────────────────
-- CREATE VIRTUAL TABLE ChunkVectors USING vec0(chunk_id INTEGER PRIMARY KEY, embedding FLOAT[N]);
-- CREATE TABLE ChunkTags (ChunkId FK, TagKind, TagValue, PRIMARY KEY (ChunkId, TagKind, TagValue));
```

**schema 결함 5항 명문화** (review M5 반영):
1. ImageReferences PK = `(DocumentId, ImageHash, RefLocator, Ordinal)` 복합
2. `Chunks.ImageCount` 갱신 책임 = Phase 2 도입 시 ImageReferences trigger 또는 ingest 시 명시 update
3. FTS5 trigger 본문 3종 명시 (AI/AD/AU, 위 SQL 참조)
4. ON DELETE: Documents → CASCADE, OutlineNodes → SET NULL (chunk 는 보존)
5. 필수 INDEX 4종 (r4 정정 — IX_Documents_Hash 제거 후): IX_Outline_Doc, IX_Outline_Parent, IX_Chunks_Doc, IX_Chunks_Outline. `Documents.FileHash` 의 UNIQUE 제약이 sqlite_autoindex 자동 생성 → 명시 INDEX 불필요.

### 3.13 RefLocator SSOT — 저장형 vs 표시형

| 단위 | 저장형 (`Chunks.RefLocator`, MCP `ref` 인자) | citation 표시형 (LLM 응답) |
|---|---|---|
| PDF 페이지 | `p=14` | `p.14` |
| PPTX 슬라이드 | `slide=5` | `슬라이드 5` |
| XLSX 시트 | `sheet=BOM` | `시트 BOM` |
| XLSX 범위 | `sheet=BOM!A1:D40` | `시트 BOM A1:D40` |
| Phase 2 이미지 (PDF/DOCX) | `p=14#img=2` | `p.14 그림 2` |
| Phase 2 이미지 (PPTX) | `slide=5#img=2` | `슬라이드 5 그림 2` |
| Phase 2 이미지 (XLSX) | `sheet=BOM#img=1` | `시트 BOM 그림 1` |
| **Task 7 — standalone image 파일** | `image=1` | `이미지 1` |

**EBNF (저장형)**:
```
RefLocator   = Unit "=" Value ( "#" SubKey "=" SubValue )*
Unit         = "p" | "slide" | "sheet" | "image"
Value        = digits | sheet-name [ "!" range ]
SubKey       = "img" | ...
```

**`image=N` 박제 정책** (Task 7 r6):
- standalone image 파일 1개 = `image=1` (N=1 고정). 향후 multi-frame / animated image 의 embedded 분할 활성 시 `image=N` 자연 호환 — `RefUnit.Image` DU + `tryParse`/`toStored`/`formatDisplay` 일반화 박제.
- `attachment_read` 의 `includeImages` / `caption_only` 가 `ImageReferences.RefLocator = "image=1"` 매칭하여 단일 image 반환. lib 변경 0 — server phase Phase S5 가 흡수.

**resolution rule**: `attachment_read(ref="p=14")` 는 chunk-level (해당 페이지 안 chunk 들의 합), `ref="p=14#img=2"` 는 page-level 의 N번째 이미지.

**citation 표시형 변환은 LLM 또는 UI 책임** — system prompt `5.knowledge-base.md` 에 표시형 SSOT 명시.

### 3.14 자연어 질문 응답 흐름 (예시)
1. 사용자: "Conveyor 동작 사양 알려줘"
2. LLM: `attachment_search({query:"Conveyor 동작 사양", k:5})` 호출
3. MCP: top-K 결과 (3.10 의 JSON schema)
4. (Phase 2+, 도면 자체 추궁 시) LLM: `attachment_read(fileId, ref="p=14", caption_only=true)` → 부족하면 `includeImages=true`
5. LLM 응답에 **citation 의무화** — `[Plant-Spec-v2.pdf p.14]` 표시형 (3.13)
6. (선택) 같은 turn 안에서 이어서 `apply_model_doc` 까지 묶기 — read tool 이라 turn snapshot 규칙 무관
7. 0-hit 처리: `{results:[], hint:"synonym retry"}` → 동의어 재검색 → 그래도 없으면 사용자 안내

### 3.15 이미지 처리 정책 (Phase 2 부터 — 본 단원의 모든 결정은 Phase 1 무관)

> ✅ **s6-r24 (2026-05-19) sweep 완료** — 본 단원이 **eager at indexing time** (D-2-2, server todo `todo-lighthouse-kb-server.md` §0) 으로 본문 재작성됨. 색인 시점 client 측 색인 프로그램이 모든 image 에 VLM caption 1회 호출 + `ImageCache.CaptionText` 영구 저장. 사용자 의도: "색인 = 분석 완료" 단순 일관 mental model. 이전 박제의 "cached lazy / on-demand caption cache / Phase 5 격하" 흐름은 본 sweep 으로 모두 정합 정정.

사양서에는 plant layout, 시퀀스 차트, wiring diagram, 표/그래프가 raster 로 박힌 경우가 빈번 →
모델링 의사결정의 직접 근거가 되므로 색인 시점에 caption 박제. Phase 2 부터 도입.

#### 3.15.1 vision token 발생 시점 (정확한 분해)

| 단계 | 처리 주체 | vision token |
|---|---|---|
| **(0) 색인 시점 caption 생성** (D-2-2 eager) | LightHouse 색인 (client-side) | **이미지당 1회** — 영구 cache 박제 |
| (a) 자연어 query → text 검색 (FTS5 trigram) | LightHouse / Searcher | **0** |
| (b) hit 결과 LLM 에 전달 (excerpt + `hasImages=true` 메타) | MCP tool 응답 | **0** |
| (c) LLM 응답 안 citation `[파일.pdf p.14]` | LLM 텍스트 출력 | **0** |
| (d) UI 가 사용자에게 image 표시 (citation 클릭 / 결과 패널) | WPF UI | **0** (LLM 무관) |
| (e) LLM 이 *이미지 자체 해석 필요* 판단 → `attachment_read(includeImages=true)` | LLM ↔ MCP | image binary base64 inline — caption 은 cache hit 으로 추가 호출 0 |

즉 vision token 의 burden 은 **(0) 색인 시점 1회 / 이미지당** 으로 집중. query/turn 시점은 caption cache hit 으로 vision token 0 (base64 image binary 의 1회 input cost 만 — caption 생성 안 함). (e) 의 includeImages 모드는 D-2-3 size 정책 (≤5MB/장, ≤5장/응답) 가드 통과 시.

#### 3.15.2 Phase 별 방식 비교

| Phase | 방식 | 색인 시점 vision token | (e) 시점 vision token | 비고 |
|---|---|---|---|---|
| 1 (MVP) | **이미지 미처리** | 0 | — (e) 미지원 | 페이지 본문/캡션 텍스트로 페이지 *위치* 까지 도달. ImageCache/ImageReferences 도 schema 없음. |
| 2 ⭐ | **eager at indexing time** — 색인 시점 caption 영구 박제 + `attachment_read(includeImages\|caption_only)` | **이미지당 1회** (전수) | caption cache hit → 0 (includeImages 시 image binary input cost 만) | D-2-2 정합. MR4 cost gate 가 색인 시점 token 누적 통제. caption_only 모드는 0. |
| 3 | **OCR (Tesseract.NET + 한글 traineddata)** | 0 (로컬 CPU) | 영향 없음 | 도면 라벨 / I/O 번호 (CV01, DI12) → `ChunkTags(TagKind="ocr")` 또는 `Chunks.Text` 보강. ⚠ Tesseract 한영혼합 산업도면 CER ~0.31 한계 → PaddleOCR microservice 폴백 또는 Phase 2 VLM caption 흡수 검토 |
| 4 | **embedding / hybrid retrieval** | 이미지당 임베딩 1회 (선택) | — | swap 됨: OCR 이 embedding 앞에 와야 한국어 도메인 어휘가 retrieval 에 반영 |
| 보류 | **Multimodal embedding (CLIP/SigLIP)** | 이미지당 임베딩 1회 | — | SigLIP 2 (2025) 로 일반 한국어 개선, 단 산업 도메인 한국어 약점 잔존 → VLM 캡션 + text embedding 이 통상 더 우수 |

#### 3.15.3 eager 색인 전략 (Phase 2 구성, 3 layer)

1. **image blob + sha256 keying** (Phase 2 색인 시점에 추출/저장)
   - `<collection-root>/.lighthouse-kb/blobs/images/<sha256>.<ext>` (r4 SSOT)
   - 같은 도면이 한 collection 안 여러 문서/페이지에 중복 등장해도 1개 blob (cross-collection 은 §3.9 — collection 별 분리)
2. **eager caption cache** (D-2-2) — sha256 → caption text **색인 시점에 영구 박제**
   - 색인 단계에서 추출한 모든 image 에 대해 VLM 1회 호출 (D-2-1 = Anthropic Sonnet 4.6 default) → `ImageCache.CaptionText` 박제
   - per-image fail-safe (D-2-4) — VLM 호출 실패 / cost gate hard cap 시 NULL 유지 + 다음 재색인 시 재시도
   - **invalidation policy** (MR3): `CaptionModel` 의 generation tier (예: `claude-sonnet-4-6` → `claude-opus-4-7`) 가 다르면 재생성. Patch/release 차이는 재생성 X.
   - **cost gate** (MR4): daily token cap (default 10K) — 색인 시점에 누적 visible (Promaker UI 의 chip 으로 사용자 안내). 초과 시 hard cutoff (해당 image 만 SkippedCaption, 다른 image 진행).
3. **Anthropic prompt cache** (optional, Phase 4+) — 같은 채팅 5분 TTL
   - cache_control breakpoint 를 image content block 뒤에 둠. base64 inline image 가 자주 reuse 되는 multi-turn 시 vision token 절감.
   - ⚠ **Anthropic 전용** (OpenAI/Ollama 미지원). cache write premium 1.25× → 재사용 ≥ 2회 시만 net 이득.

#### 3.15.4 핵심 결정 — Phase 2 eager at indexing time (D-2-2)

이유:
1. **사용자 mental model 단순화** — "색인 = 분석 완료" 일관 model. 사용자가 색인하기로 결정한 파일 = 의도적 분석 대상 (사용자 의도, s6-r18 박제).
2. **query/turn 시점 latency 0** — caption 이 이미 박제됨. `attachment_search` hit 의 caption 도 즉시 노출. lazy 안의 첫 (e) latency 문제 제거.
3. **cost 가시성 강화** — 색인 시점에 token 누적이 발생하므로 사용자가 색인 진입 전 cost gate chip (MR4) 으로 비용 예상 가능. lazy 안의 "사용 안 해도 0 / 사용 시 N" 의 비가시성 trade-off 해소.
4. **MR3 invalidation 정합** — 모델 generation tier 변경 시 재색인 trigger 로 ImageCache.CaptionText 가 자동 갱신 (`IndexerVersion` mismatch → shadow rebuild). lazy 의 per-turn invalidation 보다 SSOT 명확.

trade-off:
- **사용 안 한 image 에도 cost 발생** — 사용자가 색인한 모든 image 의 caption 비용. cost gate (MR4) 의 daily cap 으로 통제.
- **VLM down / API key 미박제 시 색인 진행 보장** (D-2-4) — per-image fail-safe 로 caption NULL 유지, 다음 재색인 시 재시도. 색인 자체는 차단 안 함.

#### 3.15.5 Phase 2 진입 사전 결정 표 (r12 — 간략 박제)

본 표는 Phase 2 본격 진입 전 사용자 confirm 의무 항목. 진입 시점에 별 todo 신설하지 말고 본 표를 기점 SSOT 로 사용.

**✓ s6-r7 (2026-05-18) 사용자 confirm 완료 — 10건 default 전부 채택**. 본격 코드 진입 = parent §4 Phase 2 첫 task (`schema 확장 — ImageCache / ImageReferences / IndexerVersion bump`). server todo §7.4 의 P3 marker 동시 갱신. 본 confirm 은 의미론적 진입 — 실 코드 작업은 별 turn 의 명시 지시 후 개시.

| id | 항목 | 권장 default | dep |
|---|---|---|---|
| CR1 | 이미지 추출 backend | PdfPig (PDF) + DocumentFormat.OpenXml (PPTX/DOCX/XLSX) — 본 단원 Phase 1 stack 정합 | — |
| CR2 | VLM caption provider | Anthropic (opus / sonnet) 1차, OpenAI fallback. Ollama 미달 | CR1 |
| MR1 | blob 저장 | `<collection-root>/.lighthouse-kb/blobs/images/<sha256>.<ext>` (r4 SSOT 정합) | CR1 |
| MR2 | image dedup 단위 | per-collection (cross-collection 격리 §3.9 정합) | MR1 |
| MR3 | caption cache invalidation | `CaptionModel` generation tier (e.g. "claude-opus-4-7") | CR2 |
| MR4 | cost gate (daily/monthly token cap) | daily token cap (default 10K vision token/day) + soft warning + hard cutoff. LlmConfig 에 신설 | CR2 |
| mr1 | OCR engine (Phase 3) | Tesseract.NET 1차, 임계 미달 시 PaddleOCR 폴백 | — |
| mr2 | embedding provider (Phase 4) | OpenAI text-embedding-3-large 또는 차세대 Anthropic | — |
| mr3 | sqlite-vec 배포 | vec0.dll service runtime 동봉 | mr2 |
| mr4 | Phase 5 진입 | ~~보류 — Phase 2 cached lazy 가 우월~~ → **s6-r18 D-2-2 정정**: Phase 5 의 "선제 batch caption" = **Phase 2 본 결정의 본질** (격하 해제). 별 Phase 분리 불필요 — Phase 2 자체가 eager indexing. | CR2, MR4 |

**진입 trigger**: 사용자가 도면 자체를 추궁하는 use case 누적 발생 + 위 10건 결정.

**cost forecast (간략)**: Phase 2 eager indexing 의 색인 시점 비용 = 이미지 수 × Sonnet 4.6 input cost (~$0.003 / image, 1280×1280 정합). 통상 manual ≈ 100 image / collection ≈ $0.3 / 색인. query 시점은 (e) includeImages 만 vision input cost (caption 생성 추가 호출 0). Phase 4 embedding $0.65 / 1000 turn 그대로. 실 단가는 vendor pricing 으로 재검증.

**진입 순서**: Phase 2 → 3 (OCR) → 4 (embedding) — parent r1 swap 결정 (OCR 이 embedding 앞에) 정합. Phase S7 (mTLS / SSE) 는 병렬 진입 가능.

### 3.16 LLM 의 색인 단계 참여

| 단계 | LLM | MVP 처리 |
|---|---|---|
| 1 추출 (parser) | ❌ | PdfPig / OpenXml SDK |
| 2 청킹 | ❌ | 결정적 규칙 |
| 3 enrichment (요약/태그/TOC 보강) | 선택 (API 권장, batch) | **skip** |
| 4 embedding | 선택 (ONNX/Ollama 권장, 로컬 기본) | **skip** |
| 5 저장 | ❌ | SQLite |

CLI provider 는 색인용으로 사용 안 함 (chat 용도 유지).

### 3.17 SQLite 운영 — WAL/동시성/재색인

**PRAGMA SSOT** (KB 연결 open 시 1회). **DB 연결 open 의 단일 진입점은 `SqliteStore.openConnection`** — 다른 곳에서 직접 `new SqliteConnection` 금지 (PRAGMA 누락 회귀 방지):
```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous  = NORMAL;
PRAGMA busy_timeout = 5000;
PRAGMA foreign_keys = ON;
```

**색인 재구성 정책 (s6-r26 — major/minor 분리, s6-r8 m1 박제 해소)**:
- **shadow rebuild trigger SSOT = `Meta.schema_version` drift** (≠ `IndexerVersion.SchemaVersion`).
  SQL 비호환 변경 (column constraint 변경 / FK 추가 / DROP 등) 시 lib 개발자가 `SchemaVersion` bump
  + `IndexerVersion` 동반 bump (s6-r22 mn3 패턴 = 1.2.0→1.3.0 / SchemaVersion 2→3 정합).
- **IndexerVersion minor / patch bump** (예: 1.3.0 → 1.4.0) 시 = `ensureSchema` 의 ALTER forward-compat
  으로 흡수 + `stampVersion` 으로 Meta 갱신. **기존 collection 재색인 cost 회피**.
- shadow rebuild = `index.db.new` 에 새로 색인 → 완료 시 atomic rename (Windows 의 경우 close 후 swap)
- batch commit 단위 = chunk **500 / commit** (메모리 / WAL 크기 / cancellation 응답성 균형)
- CancellationToken 지원 — 사용자 UI 의 "취소" 버튼에서 종료 가능
- 부분 commit 상태 회복 — SchemaVersion drift 또는 Meta 누락 시 자동 재색인

### 3.18 DI / lifecycle 결정 항목 (Phase 1 구현 시 확정)

#### 3.18.1 KnowledgeBase facade
- `Ds2.LightHouse.KnowledgeBase` facade 의 형식: **F# record-of-functions** vs **interface (IKnowledgeBase)**
- `openProject(projectKbRoot)` 의 lifecycle: per-project singleton vs request-scoped + IDisposable
- WPF DI 컨테이너 (현재 Promaker 는 어떤 컨테이너 사용 중인지 확인 필요)

#### 3.18.2 attachment_* 도구의 active collection 도달 경로 (r4 단순화)

> ⛔ **대안 B / r5: 본 채택안 (a) 는 Phase 1 에서 실현되지 않음**. parent r5 결정 12 로 §4.5 통째 SKIP → `LlmTurnContext` 확장 task / `MainViewModel.LlmChat.cs` 의 `openCollections` 주입 모두 Phase 1 에서 진행 안 함. server phase 진입 시 `kb-server.md §3.8` (session 기반 routing) + §3.13 (client 단순화 — KnowledgeBase 필드 미도입) 가 직접 대체. 본 단원 본문 (채택안 a 의 구체적 wire) 은 **r4 시점 historical reference 박제용**. 다음 세션이 본 단원을 읽고 Phase 1 의 결정사항으로 오해하지 말 것.
>
> 즉 parent Phase 1 의 `KnowledgeBase` facade (§4.4) 는 **`Ds2.LightHouse.Tests` 가 유일한 호출처**. server phase 진입 시 `Ds2.LightHouseService` 가 두 번째 호출처로 합류.

**채택안 (a) — `LlmTurnContext` 확장 [r5 SKIP — historical reference]**:
- `LlmTurnContext.cs:57` 의 생성자에 `KnowledgeBase kb` (또는 `string[] activeCollectionPaths` + LightHouse facade) 인자 추가
- `MainViewModel.LlmChat.cs` 가 turn 시작 시:
  1. `LlmConfig.Load()` → `KbCollections.Where(c => c.Active).Select(c => c.Path)` 로 active paths 추출
  2. `LightHouse.openCollections(activePaths)` → multi-db SQLite ATTACH + UNION facade
  3. 결과 `KnowledgeBase` instance 를 `LlmTurnContext` 에 주입
- turn end 시 dispose 또는 cache (§3.18.1 의 lifecycle 결정 따라)

**multi-db ATTACH 동작**:
- 각 collection 의 `.lighthouse-kb/index.db` 를 main DB 에 ATTACH (alias = `kb0`, `kb1`, ..., `kbN-1`)
- 검색 시 `SELECT ... FROM kb0.ChunksFts UNION ALL SELECT ... FROM kb1.ChunksFts ...` 동적 생성
- SQLite default ATTACH limit = 10 → UI 가 active toggle 시 10번째까지만 허용 (§0 보류 항목)
- 첫 collection 만 read-write open 권한 필요 (재색인 시), 나머지는 read-only ATTACH 가능

**LightHouse 측 invariant**:
- `KnowledgeBase` instance 는 한 active 셋에 lock-in. 사용자가 active 토글하면 turn 종료 후 다음 turn 시작 시 새 instance.
- in-progress turn 중 active 셋 변경은 무시 (다음 turn 부터 반영) — race 회피.

---

## 4. 남은 할 일 (Phase 별)

### Phase 1 — MVP (환원판: PDF + DOCX + TXT + MD, FTS5 trigram only, 4 tools, **이미지 미처리**)

**4.1 Ds2.LightHouse project 생성** *(r7: commit `bccb0ea` 완료)*

> ⛔ **대안 B (r5) — 첫 task `.gitignore` 는 SKIP**. server phase 가 사용자 폴더에 흔적 0 정책이라 in-process MVP 단계에서도 prod 사용자 노출 없음 (§4.5 skip 이라 사용자가 KbManagerDialog 로 collection 등록 불가). lib 자체 unit test 만으로는 사용자 폴더 직접 사용 안 함.
>
> ⛔ **r7 사용자 결정**: (a) sln 갱신 = **`Apps/Promaker/Promaker.sln` 만** (Solutions/Ds2.sln SKIP). (b) `ModelContextProtocol.AspNetCore 1.2.0 → 1.3.0` 업그레이드 = **본 Phase 1 보류** (lib only — MCP 무관 / server phase Phase S3 P2 결정 시 함께). (c) 본 todo 파일 git mv = **Phase 1 완료 commit 직전 별도 confirm**.

- [ ] ~~**선행 (코드보다 먼저)** — root `.gitignore` 에 `.lighthouse-kb/` 추가 (r4 — 폴더 이름 변경, project 무관). 사용자가 collection 으로 *Promaker repo 안 폴더* 를 선택할 경우를 위한 보호. 4.4 의 SqliteStore 가 동작하는 순간 그 폴더 안에 index.db (+ Phase 2 부터 image blob) 자동 생성.~~ **(r5 SKIP — server phase 흡수)**
- [ ] 사전 grep — `Directory.Packages.props` 가 Solutions/Apps/Promaker 두 위치 어떻게 분리되어 있는지 (CPM 적용 범위) 확정
- [ ] sln **2개** 모두 갱신 — `Apps/Promaker/Promaker.sln` + `Solutions/Ds2.sln` (실재 확인). 새 project (`Solutions/Core/Ds2.LightHouse`) + 테스트 (`Solutions/Tests/Ds2.LightHouse.Tests`) 가 Solutions/ 하위라 `Solutions/Ds2.sln` 누락 시 CI/전체 빌드에서 빠짐.
- [ ] `Solutions/Core/Ds2.LightHouse/Ds2.LightHouse.fsproj` 생성 (net9.0)
- [ ] `dotnet sln Apps/Promaker/Promaker.sln add Solutions/Core/Ds2.LightHouse/Ds2.LightHouse.fsproj`
- [ ] NuGet 등록 — **실제 신규는 DocumentFormat.OpenXml 1건만**:
  - 신규: `DocumentFormat.OpenXml` → `Solutions/Directory.Packages.props`
  - 이전 필요: `PdfPig` → `Apps/Promaker/Directory.Packages.props:13` 에서 `Solutions/Directory.Packages.props` 로 이동 + Promaker.csproj:43 의 `<PackageReference Include="PdfPig" />` 제거 (transitive 로 받음)
  - 이미 등록 (재사용): `Microsoft.Data.Sqlite`, `System.Text.Encoding.CodePages`, `log4net`, `FSharp.Core`
- [ ] (검토) ModelContextProtocol.AspNetCore 1.2.0 → 1.3.0 (2026-05-08 출시) 업그레이드 여부

**4.2 부분 통합 (LightHouse 로 이전 — 3 commit sub-grouping)** *(r7: §4.2a commit 완료 본 turn, §4.2b 부터 next)*

> ⛔ **r7 정정 — §4.2c "C# 무영향 확인" 가정 폐기**: F# type abbreviation (`type X = Y`) 이 C# interop 작동 안 함 (.NET metadata 에 X type 미생성). §4.2a 에서 C# 3 파일에 `using Ds2.LightHouse;` 추가 + `Ds2.LlmAgent.ImageFormat.Png` → `Ds2.LightHouse.ImageFormat.Png` namespace 갱신 수행 완료. §4.2c 의 "호출 경로 무영향" 박제는 더 이상 SSOT 아님.
>
> r7 추가 grep 발견: §4.2a 본문이 누락한 `ClaudeStreamJsonInputTests.fs:33,74` 의 `Png` constructor 도 `open Ds2.LightHouse` 추가 (F# Test 총 3 파일 — AttachmentClassifierDriftTests / LlmUserMessageOpsTests / ClaudeStreamJsonInputTests).

*4.2a (이전)*
- [ ] **사전 grep — `ImageFormat` 호출처 전수 확인** (F# + C#). LlmMessage.fs 외 다른 곳 (예: `LlmChatViewModel.Attachments.cs` 의 image 첨부 logic) 에서도 사용 시 alias / open 으로 호환 처리 누락 회피.
- [ ] `Ds2.LlmAgent/LlmMessage.fs:4` 의 `ImageFormat` 을 `Ds2.LightHouse.ImageFormat` 으로 신설. LlmAgent 측은 `type ImageFormat = Ds2.LightHouse.ImageFormat` alias 또는 `open Ds2.LightHouse` 로 호환.
- [ ] `AttachmentClassifier.fs` 의 `detectEncoding` + 부속 (`isStrictDecodable`, `tryCp949`, `TextEncodingDetect`, `Log.provider` 의존) 을 `Ds2.LightHouse/TextEncoding.fs` 로 신설 (LightHouse 자체 logger).
- [ ] `Ds2.LlmAgent.fsproj` 에 `Ds2.LightHouse` ProjectReference 추가.
- [ ] **`Solutions/Core/Ds2.LlmAgent/CLAUDE.md` SSOT 박제 동시 갱신** — line 70 (`LlmMessage.fs ... ImageFormat enum`) / line 71 (`AttachmentClassifier.fs 첨부 분류 SSOT (정책 19) ... detectEncoding`) / line 170 (`Solutions/Core/Ds2.LlmAgent/AttachmentClassifier.fs:38-83` 직접 경로:라인 박제) 가 본 이전으로 stale 됨. **4.2a 와 같은 commit 으로 묶을 것** (분리 시 중간 PR 이 stale 표기 따라 회귀 위험).

*4.2b (LlmAgent 측 slim)*
- [ ] `Ds2.LlmAgent/AttachmentClassifier.fs` 의 `detectEncoding` 호출을 `Ds2.LightHouse.TextEncoding.detectEncoding` 으로 forward.
- [ ] `Classification` DU + `classify` + extensions set + 호출 표면은 **그대로 유지** (3.0/3.11).
- [ ] `AttachmentClassifierDriftTests.fs` (**현행 Fact 13건**) 통과 확인 — 동작 변경 없음.

*4.2c (참조 갱신)*
- [ ] `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.Attachments.cs:265, 270, 348` 호출 경로 무영향 확인.
- [ ] Promaker.csproj 의 `System.Text.Encoding.CodePages` 직접 참조는 LightHouse transitive 로 받게 되지만, App.xaml.cs:148 의 `CodePagesEncodingProvider.RegisterProvider` 는 **그대로 유지** (entry 시점 한 번).
- [ ] `Solutions/Tests/Ds2.LlmAgent.Tests/Ds2.LlmAgent.Tests.fsproj` — `Ds2.LlmAgent` 참조의 transitive 로 `Ds2.LightHouse` + 그 NuGet 의존 (PdfPig / OpenXml / Sqlite) 가 끌려옴. test 실행 시 native sqlite binding 동반 — restore 부담 확인. 필요 시 별도 fixture project 분리 검토.

**4.3 LightHouse 본체 — 추출 / 청킹 (Phase 1, 이미지 미처리)**
- [x] `Models.fs` (155 line) — FileKind / OutlineNodeType DU + Document / OutlineNode / Chunk / ExtractedSegment / ExtractedChunk / ExtractedOutlineNode / ExtractedDocument / Query / SearchHit / SearchResults / Citation
- [x] `RefLocator.fs` (143 line, r8 추가 — todo §3.13 EBNF SSOT 구현체. 원 §4.3 task 목록에 누락 — §4.8a round-trip test 의존성으로 신설) — RefUnit/RefSubKey DU + tryParse / parse / toStored / formatDisplay
- [x] `Extractors/IExtractor.fs` (22 line) — 공통 interface (Supports + Extract + IDisposable)
- [x] `Extractors/PdfExtractor.fs` (68 line, 이전 세션 작성 → r8 본 세션 채택) (PdfPig 0.1.14) — 페이지 단위 raw text segment + 문서 제목 (Information.Title). bookmark TOC 는 Phase 2 (`TryGetBookmarks` byref 진입 복잡 + 한국어 산업 PDF bookmark 누락률 사유). 손상/암호화 fail-safe
- [x] `Extractors/OoxmlExtractor.fs` (124 line) (OpenXml 3.5.1) — docx 만 활성. Heading style id 검사 + paragraph/table InnerText. pptx/xlsx Phase 2 (`Supports false` 반환). `Title` 추출 skip (PackageProperties experimental — Indexer filename fallback). 한정 catch 4종 (FileFormatException / OpenXmlPackageException / InvalidDataException / IOException)
- [x] `Extractors/TextExtractor.fs` (89 line) (txt/md) — `Ds2.LightHouse.TextEncoding.detectEncoding` 사용. Markdown heading (^#{1,6}\s+) flat outline (depth stack Phase 2 보류 — sub-agent m1)
- [x] `Chunker.fs` (136 line) — 구조 우선 + 보조 분할. 단락 → 문장 (regex `(?<=[.!?。?!])\s*` lookbehind) → hard 자르기 cascade. `estimateTokens` 한국어 char/2 + ASCII char/4. UTF-16 surrogate pair 한계 박제 (sub-agent m3)
- [x] `Classifier.fs` (71 line) — `classifyForKb : string -> FileKind` + `supportedExtensions` Map + `rejectedExtensions` Set (§6 m15 PII 보호 — `.env` / 실행파일 / 미디어 / 압축 / 이미지)

**4.4 LightHouse 본체 — 저장 / 검색 (Phase 1)** *(r9: 코드 작성 완료 — 단일 commit 진입 직전)*
- [x] `SqliteStore.fs` (~270 line) — §3.12 schema (Phase 1: Documents/OutlineNodes/Chunks/ChunksFts/Meta + FTS5 trigger AI/AD/AU) + §3.17 PRAGMA (WAL/NORMAL/busy=5000/FK ON) **단일 진입점** `openConnection` / `IndexerVersion` 모듈 (Current=1.0.0, SchemaVersion=1, Tokenizer=trigram) / `ensureSchema` idempotent / `stampVersion` / `needsRebuild` / Document/Outline/Chunk CRUD primitives / `insertChunks` 500/commit + CancellationToken + transaction rollback / `swapShadow` File.Replace atomic + .bak 즉시 삭제 (r9 m5 주석) / read-only `checkWritable` probe / path helpers / `MaxAttachedDbs=10` 상수. `deleteDocument` 매뉴얼 purge 보존 (r9 m1).
- [x] `Searcher.fs` (~280 line) — FTS5 BM25 trigram **multi-collection ATTACH UNION ALL** (`buildCollectionSelect` 동적 생성 per alias) / `buildFtsQuery` token split + per-token phrase quoting (implicit AND, r9 자가 검열 M1) / **fileId 합성** `<kbIdx>:<docId>` cross-collection unique / `parseFileId` 실패 시 명시 빈 결과 + log warn (r9 review M3) / `truncateExcerpt` token 한도 절단 / **Score 부호 반전** (BM25 음수 → 높을수록 좋음, r9 자가 검열 M6) / `listDocuments` / `getOutline` / `readByRef` ordinal concat / `HasImages: false` (Phase 1) / over-fetch +1 으로 `MoreAvailable` 판별.
- [x] `Indexer.fs` (~175 line) — Extract → Chunk → Store orchestrator / SHA-256 stream hash idempotent (`computeFileHash`) / `routeExtractor` 첫 매칭 / `titleOf` filename fallback / `ingestFile` → `FileIngestResult` (Ingested/Skipped/Failed) / `enumerateFiles` `.lighthouse-kb/` 제외 / `rebuildShadow` IndexerVersion drift 시 자동 / read-only collection fail-fast / `ingest` 시그니처 `(string * FileIngestResult) array` 반환 (r9 자가 검열 m6) / 진행률 콜백 `IngestProgress`.
- [x] `KnowledgeBase.fs` (~105 line) — **record-of-functions facade** (r9 결정 a) / `openCollections(activePaths)` `:memory:` main (URI `lhmain-<guid>?mode=memory&cache=private`, r9 자가 검열 M4) + read-only ATTACH `kb0..kbN-1` (URI mode=ro) / ATTACH inline + single-quote escape (parameter binding 불가, r9 자가 검열 C2) / **ATTACH 실패 시 try-with reraise + conn.Dispose** (r9 review C1) / `MaxAttachedDbs=10` 가드 / Search/List/Outline/Read/ActivePaths/Dispose surface / `Ds2.Core`/`Ds2.Editor`/`Ds2.LlmAgent` 미참조 invariant 준수 (§3.5).
- [x] **ATTACH limit 가드** — `SqliteStore.MaxAttachedDbs=10` 상수 + `KnowledgeBase.openCollections` 사전 fail.
- [x] **fsproj 갱신** — Compile Include 4 추가 + `PrivateAssets="all"` 3 package (Microsoft.Data.Sqlite / PdfPig / DocumentFormat.OpenXml, r9 결정 b — r8 메타리뷰 m2 흡수).

**4.5 Promaker 측 통합 (r4 — multi-collection + KbManagerDialog)**

> ⛔ **대안 B (r5) — 본 §4.5 전체 SKIP**. server phase (`todo-lighthouse-kb-server.md` Phase S5) 가 단일 SSOT 로 흡수. 본 §4.5 의 모든 task (`AttachmentTools.cs` / `KbManagerDialog` / `LlmConfig.KbCollections` 확장 / `LlmTurnContext` 의 `KnowledgeBase` 필드 추가 / `PromakerToolNames.All` 의 attachment_* 추가 / `MainViewModel.LlmChat.cs` 의 `LightHouse.openCollections` 호출 / `AttachmentIngestService` / `ApplicationSettingsDialog` 의 KB 관리 버튼 등) 는 **Phase 1 에서 만들지 않음**. server phase 직접 진입 시 server SSOT 에 맞춰 신설. 사유: 60%+ throwaway / 사용자 데이터 migration / `.lighthouse-kb/` 고아 / `LlmTurnContext` 자가 모순 회피 (§0 결정 12, r5).
>
> **본 §4.5 의 task 체크박스들은 모두 SKIP 되었다고 간주**. 다음 세션이 본 단원 task 들을 진행하면 안 됨.
- [ ] `Apps/Promaker/Promaker/LlmAgent/Tools/AttachmentTools.cs` — `[McpServerToolType]` 4종 tool wrapper. quota hard enforce (3.10). `LlmTurnContext` 인자 자동 검출 (§3.18.2 의 채택안 a). collection 인자 없음 (서버 측 active 셋 fix).
- [ ] `Apps/Promaker/Promaker/Knowledge/AttachmentIngestService.cs` — KbManagerDialog ↔ Indexer 큐 / 진행률 (background worker) / CancellationToken. collection 단위 색인 진행률 (n개 문서 중 k번째 진행).
- [ ] **`Apps/Promaker/Promaker/LlmAgent/LlmTurnContext.cs` 확장** (§3.18.2) — `KnowledgeBase kb` 필드 추가 + 생성자 갱신. `MainViewModel.LlmChat.cs` 의 turn 시작 코드가 `LlmConfig.KbCollections.Where(c => c.Active).Select(c => c.Path)` → `LightHouse.openCollections(activePaths)` 호출 결과를 주입. turn end 시 dispose/cache 정책 §3.18.1 따라.
- [ ] **`Apps/Promaker/Promaker/LlmAgent/LlmConfig.cs` 확장** (r4) — `[JsonPropertyName("kbCollections")] public List<KbCollectionEntry> KbCollections { get; set; } = new();` 추가 + `KbCollectionEntry { string Path; bool Active; }` 정의. 기존 atomic Save / corrupt fallback / OS 사용자 전역 path 그대로 활용.
- [ ] **`Apps/Promaker/Promaker/Dialogs/KbManagerDialog.xaml(.cs)` 신설** (r4) — collection 등록 list (path 표시) / "추가" (folder picker) / "제거" / "재색인" / "활성 토글" / 색인 진행률 표시. ATTACH limit 10 시점에 toggle 막음 (사용자 안내).
- [ ] **`Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml(.cs)` 의 LLM 탭에 "KB 관리..." 버튼 추가** (r4) — 클릭 시 KbManagerDialog 띄움 (modal).
- [ ] **`Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs:16-29` 의 `All` 배열에 attachment 4종 추가** — `mcp__promaker__attachment_list`, `..._outline`, `..._search`, `..._read`. ⚠ 빠뜨리면 `ClaudeCliProvider` 의 `--allowed-tools` 화이트리스트에서 누락 → MCP 등록되어도 Claude CLI 가 silent 차단.
- [ ] **`Solutions/Tests/Ds2.LlmAgent.Tests/PromakerToolNamesDriftTests.fs` 확장** — 현재 `modelToolsPath` 만 파싱하는 `extractMcpServerToolMethods` 를 `Tools/*.cs` 전체 스캔으로 확대. `expectedSet` (line 87-91) 을 doc-level 4 + read 2 + attachment 4 = **10종** 으로 갱신. snake_case 단위 테스트 (line 132-141) 에 attachment 4종 추가 (`attachment_list`/`_outline`/`_search`/`_read`).
- [ ] Promaker.csproj 에 `Ds2.LightHouse` ProjectReference 추가
- [ ] Promaker.csproj 의 `<PackageReference Include="PdfPig" />` (line 43) 제거 — LightHouse transitive 로 받음
- (`.gitignore` 의 `.lighthouse-kb/` 추가는 §4.1 첫 task 로 격상됨)
- (~~project open 시 .promaker-kb 자동 attach~~ — r4 폐기, KB 가 project 와 무관)

**4.6 System prompt**

> ⛔ **대안 B (r5) — 본 §4.6 SKIP**. `5.knowledge-base.md` 는 LLM 이 attachment_* tool 을 호출할 때 사용하는 prompt 인데, §4.5 skip 으로 Promaker MCP 에 attachment_* tool 자체가 등록 안 됨 → prompt 만 있으면 의미 없음. server phase 진입 시 server-side host 명시까지 포함하여 신설 (`todo-lighthouse-kb-server.md` Phase S5).

- [ ] ~~`Apps/Promaker/Promaker/LlmAgent/Prompts/5.knowledge-base.md` 신설:
  - attachment_* 도구 4종 사용 절차
  - citation 의무화 — **표시형** `[파일명 p.14]` 형식 (3.13)
  - 0-hit 시 동의어 재검색 후 사용자 안내
  - quota (3.10 의 maxCallsPerTurn=8 / maxExcerptTokens=4000 / maxCumulativeTokens=16000)
  - `4.attachments.md` (prompt injection 방어) 와의 책임 분리 명시 (3.0 의 두 경로 분리)
  - Phase 2 부터: `hasImages=true` 면 vision 확인 권장. `caption_only` 우선 → 부족 시 `includeImages`~~ **(r5 SKIP — server phase 흡수)**
- [ ] ~~`Promaker.csproj` 의 `EmbeddedResource Include="LlmAgent\Prompts\*.md"` 가 자동 포함 — 별도 등록 불필요~~ **(r5 SKIP)**

**4.7 UI — 폐기 (r4)**

dock 패널 "Attachments" 폐기. KB UI 는 KbManagerDialog (§4.5) 가 전담. 이유:
- collection 등록/제거/재색인/active 토글이 dock 패널보다 modal dialog 에 더 자연스러움 (자주 보는 화면 X)
- chat 첨부 chip 표시는 기존 chat UI 가 이미 처리 — KB 별도 dock 불필요
- §3.0 의 두 경로 분리 SSOT 와 정합 (chat 첨부 ≠ KB)

따라서 `todo-dock-layout.md` 에 Attachments anchor 추가도 불필요 (§6 m9 / §7 step 7 무효화).

**4.8 검증 / 테스트** *(r10: commit `9736237` 완료 — 99 Fact 100% 통과)*

> ⚠ **대안 B (r5) — 본 §4.8 의 일부 항목 SKIP**. Promaker 통합 (§4.5) 에 의존하는 테스트는 본 Phase 1 에서 수행 불가. **lib 자체 unit test 만** 진행. SKIP 항목은 `~~취소선~~` 표시. server phase Phase S5 에서 in-process 동등 시나리오를 service 경로로 검증.

**진행 항목 (lib 자체 unit test)** — `Solutions/Tests/Ds2.LightHouse.Tests/` (xunit, 11 파일, 99 Fact). 4.2 의 부분 통합 무영향 회귀 보호.

*4.8a (parser / chunker / locator — 결정적 변환)*:
- [x] **RefLocator parser round-trip** (§3.13) — 저장형 ("p=14", "slide=5", "sheet=BOM!A1:D40", "p=14#img=2") → parsed → 저장형 동일성. EBNF 규칙 위반 입력 (예: "page=14") 거부 (`RefLocatorTests` 9 Fact)
- [x] **Chunker boundary 검증** (§3.8) — 정확 한도 (DefaultMaxTokens) token = 1 chunk 유지 / 한도+1 token = ≥2 chunk 분할 결정성. 빈 입력 / 단일 token / 매우 긴 단일 단락 (`ChunkerTests` 9 Fact, r10 자가 검열 §4.8a 정확 boundary 추가)
- [x] **PdfExtractor fail-safe** — 손상 PDF / 빈 파일 fail-safe 빈 결과 (`PdfExtractorTests` 3 Fact). 정상 PDF fixture 는 PdfPig read-only 라 Phase 2 보류
- [x] **OoxmlExtractor (docx)** — heading + paragraph + table InnerText + 손상 docx fail-safe (`OoxmlExtractorTests` 3 Fact). 5단계 깊이 / 한자 혼합은 Phase 2
- [x] **TextExtractor + detectEncoding** — UTF-8 BOM / UTF-8 no-BOM / CP949 / UTF-16 LE/BE / 깨진 byte stream / null / 빈 (`TextEncodingTests` 9 Fact + `TextExtractorTests` 5 Fact)

*4.8b (FTS5 / SQLite 운영)*:
- [x] **한국어 trigram 회귀** — query "컨베이어" 가 본문 "컨베이어가/를" 포함 페이지 hit (`KnowledgeBaseTests`)
- [x] **FileHash idempotent** 재첨부 검증 (`IndexerTests`)
- [x] **IndexerVersion bump** 자동 재색인 검증 — drift → shadow rebuild → atomic rename (`IndexerTests` + r10 lib fix F2 `SqliteConnection.ClearPool`)
- [x] **WAL 동시성** — 색인 중 별 connection 으로 read 가능 (`SqliteStoreTests`)
- [x] **0-doc collection** — 빈 폴더 ingest → valid index.db + Documents 0 (`IndexerTests`)
- [x] **0-byte 파일 / 미지원 ext / rejected ext** — Extractor 가 log + skip (`IndexerTests`). extensionless 텍스트 (`README` 등) 와 Documents.extract_status DU 박제는 Phase 2 보류
- [x] **FTS5 trigger AI/AD/AU** sync (`SqliteStoreTests` 3 Fact, r10 자가 검열 m9 AU 추가 — r9 자가 검열 M2 잔여 우려 해소)

*4.8c (multi-collection lib API)*:
- [x] **multi-collection ATTACH** — 2 collection 동시 ATTACH 시 UNION 검색 + fileId `<kbIdx>:<docId>` cross-collection unique (`KnowledgeBaseTests`)
- [x] **ATTACH boundary 10** — `MaxAttachedDbs = 10` 까지 실 색인된 collection 동시 ATTACH 정상 (r10 자가 검열 §4.8c boundary 추가)
- [x] **ATTACH limit 초과 11** — `InvalidOperationException` (`KnowledgeBaseTests`)
- [x] **ATTACH parameter binding 실측** (r9 review C2 잔여 우려) — 폴더명에 작은따옴표 포함 시 single-quote escape 정상 hit (`KnowledgeBaseTests`)
- [x] **빈 active 셋** — `openCollections [||]` 정상 + search/list empty (r10 자가 검열 M1 추가)

*4.8d (cross-PR 회귀 보호)*:
- [x] **AttachmentClassifierDriftTests (Fact 13건) 통과 유지** — 본 PR scope 외 별 project (`Solutions/Tests/Ds2.LlmAgent.Tests/`). r10 commit `9736237` 후 13/13 통과 확인. 4.2 의 부분 통합 무영향 보장

**SKIP 항목 (대안 B / r5 — Promaker 통합 의존, server phase Phase S5 가 흡수)**:
- ~~LLM 이 `attachment_search` 호출 → citation 포함 응답 생성~~ → server phase S5 의 IntegrationTests
- ~~LlmConfig.KbCollections persist round-trip~~ → server phase S5
- ~~read-only collection 검증 (r4)~~ → server phase 에서는 사용자 폴더 읽기 자체 없음 (server-side storage), 폐기. parent §3.18.2 회피와 함께.

### Phase 2 — 포맷 확대 + 이미지 인프라 신설 + eager VLM caption

**진입 marker (s6-r7)**: §3.15.5 의 10건 default 사용자 confirm 완료. 본 phase 의 첫 task = 아래 schema 확장. **본격 코드 진입은 별 turn 의 명시 지시 후** (대규모 변경 — PdfPig 이미지 raw 추출 + VLM caption provider integration + LlmConfig cost gate). server todo §7.4 의 다음 권장 = 본 phase 첫 task 진입.

- [x] schema 확장 — `ImageCache` / `ImageReferences` 테이블 (3.12 의 주석 처리 블록 활성) + `Chunks.ImageCount` 컬럼 (ALTER TABLE) + IndexerVersion bump **(s6-r8 완료)**: `SqliteStore.IndexerVersion.Current` 1.0.0→1.1.0 (minor) + `SchemaVersion` 1→2. ensureColumn helper 신설 (SQLite의 ALTER ADD COLUMN IF NOT EXISTS 미지원 → PRAGMA table_info 분기). ensureSchema 가 Chunks.ImageCount DEFAULT 0 forward-compat ALTER 동반. lib Tests **102→109** (+7 Phase 2 schema Fact + 자가 검열 M1 보강 1 assertion). paired-release detector pass (1.1.0 ∈ [1.0.0, 1.99.99]). service Tests 115/115 + IntegrationTests 24/24 회귀 0.
- [x] `ImageStore.fs` — sha256 + blob 저장 + ImageCache/ImageReferences upsert (cross-document 공유, 단일 프로젝트 안) **(s6-r11 완료)**: `Solutions/Core/Ds2.LightHouse/ImageStore.fs` (+170 line) 신설. 9 함수 surface (`extOf` / `mimeOf` / `blobsImagesDir` / `blobFilePath` / `computeSha256` / `saveBlob` (idempotent) / `upsertImageCache` (INSERT OR IGNORE caption 보존) / `addImageReference` (복합 PK 4 키 INSERT OR IGNORE) / `getImageCache` / `lookupReferencesByDocument`). literal `BlobsImagesSubPath = "blobs/images"`. parent §3.15.5 MR1 (`<root>/.lighthouse-kb/blobs/images/<sha256>.<ext>`) + MR2 (per-collection dedup) SSOT 정합. `ImageStoreTests.fs` 14 Fact (cross-document 공유 / FK PRAGMA / ON DELETE CASCADE + 자가 검열 M1 `Attachment.mimeOf` cross-drift Theory 4 case + m3-b/c). lib Tests 109→123, service/IT/Promaker 회귀 0. **누적 472 Fact**.
- [x] **wiring (task C1, s6-r12 완료)**: `ExtractedImage` record + `ExtractedDocument.Images` 필드 (Models.fs) + `Indexer.ingestImagesIntoStore` public helper (collectionRoot/documentId/images dispatch — sha256 + saveBlob + upsertImageCache + addImageReference). 기존 9 extractor literal 박제 (`Images = [||]` Phase 1 default — PdfExtractor 2 + OoxmlExtractor 6 + TextExtractor 1). `ingestFile` 시그니처에 collectionRoot 추가. IndexerTests 5 Fact (빈 배열 no-op / 단일 dispatch / cross-document dedup / 중복 PK 차단 / Phase 1 e2e 회귀). lib Tests 123→128. **누적 477 Fact**.
- [ ] PdfExtractor / OoxmlExtractor 가 이미지 raw 추출 + ImageStore 호출 (Phase 1 무변경 분리)
  - **task C2 (별 turn)**: PdfExtractor — `page.GetImages()` + `IPdfImage.TryGetPng(out bytes)`. 화이트리스트 외 image (JPX/JBIG2) 는 skip 또는 PNG re-encode. **본격 진입 전 결론 의무 (s6-r12 자가 검열 M2/m6)**: (a) transaction partial failure 정책 = per-image try/catch + log skip (fail-safe) vs `ingestFile` outer txn 도입 / (b) `ExtractedImage.Bytes` 빈 배열 가드 박제 위치 (extractor vs `ingestImagesIntoStore` 안).
  - **task C3 (별 turn)**: OoxmlExtractor (docx) — `MainDocumentPart.ImageParts` 추출. ContentType 화이트리스트 매칭.
  - **task C4 (별 turn)**: `Chunks.ImageCount` 갱신 + `ExtractedImage.ChunkIndex` (segment → chunk 매핑) 박제 + `ingestImagesIntoStore` 시그니처에 ChunkId Some 박제.
  - PdfPig DCT/JPX/JBIG2 decode 는 별도 NuGet 필요 — task C2 진입 시 확인
  - PdfPig 페이지 PNG 렌더 fallback 필요 시 `PdfPig.Rendering.Skia` 동반
- [x] OoxmlExtractor 에서 pptx (슬라이드 + speaker notes) 활성 **(xlsx-pptx-images r2 Task 1 — `cc53628`)** — SlideIdList SSOT 순회 + Title/CenteredTitle placeholder + paragraph break + speaker notes `--- 노트 ---` marker.
  - 슬라이드 PNG export — Phase 4 backlog 유지 (LibreOffice headless `--convert-to png` 폴백, VLM caption-only 흡수)
- [x] OoxmlExtractor 에서 xlsx 활성 — 사용자 결정 박제 간편 정책 (수식 cached only / hidden skip / merged top-left / 빈 행 skip / 좌표 RefLocator Phase 3 backlog) **(xlsx-pptx-images r2 Task 2 — `3948bdc`)** — SST + PhoneticRun rPh 제외 + Sheet.State enum Hidden/VeryHidden skip + expandSparseRow (Critical-4) + Cell.DataType 6 분기 + 좁은 컬럼 (Gantt 시각화) filter.
- [x] **xlsx Gantt schedule 시트 type 힌트** **(xlsx-pptx-images r6 Task 2-extra — `1dcc7f8`)** — 산업 .xlsx 작업일정표 검출 + 8 role synonym map (NO/SYM/TASK/START/DURATION/CUMULATIVE/SCORE/GRADE) + normalize header (공백/괄호/한자) + 2-row merged header concat + score 판정 (distinct role ≥3 AND start/dur/cum ≥2) + 동적 preamble prepend (`A=NO(순번), D=START(시작초), ...`) + outline `[Gantt schedule]` suffix. LLM 이 row tab-join 데이터를 컬럼 의미 기반 정확 해석. Fact 6 (정상/순서 바뀜/영문/normalize/2-row/false negative).
- [x] **standalone image 파일 색인 활성** **(xlsx-pptx-images r6 Task 7 — `7964ec6`)** — 활성 6 종 (PNG/JPEG/GIF/WEBP raw + EMF/WMF Metafile→PNG 변환). Classifier rejected 5 제거 + supported 7 매핑 + `Models.FileKind.Image` + `RefUnit.Image` + `MetafileConverter` 모듈 분리 (OoxmlExtractor + ImageExtractor 양쪽 재사용) + `Extractors/ImageExtractor.fs` 신규 (magic byte 검증 + Width/Height parse + per-image fail-safe). Fact 9 (정상 4 종 + magic byte mismatch + 0 byte + EMF invalid + title + Supports).
- [ ] `attachment_read` 의 ref 파서 강화 (sheet=BOM!A1:D40 + p=14#img=2) — server phase Phase S5 흡수
- [ ] `attachment_read` 의 image 모드 두 가지 (3.15.3):
  - `caption_only=true`: ImageCache.CaptionText 반환. cache miss 시 그때 LLM vision 1회 호출하여 caption 생성·저장 (on-demand caption cache, layer 3)
  - `includeImages=true`: image content block 동봉 반환. caption 있으면 hint 동반. cache_control breakpoint 를 image block 뒤에 (Anthropic 전용, layer 2)
  - Microsoft.Extensions.AI 의 image content 추상화 활용 → provider-agnostic (단 prompt cache 는 Anthropic 전용)
- [ ] `attachment_search` 결과의 `hasImages` 의미화 (Phase 1 의 false → 실제 값)
- [ ] 5.knowledge-base.md 룰 보강 — "텍스트로 충분하면 (e) 생략, 도면 추궁 시 `caption_only` 우선 → 부족 시 `includeImages`"
- [ ] UI: citation 클릭 시 원문 page/slide 띄우기 (PDF viewer / Excel highlight)

### Phase 3 — OCR (한국어 도메인 어휘 보강)
- [ ] Tesseract.NET + 한글 traineddata. ⚠ 한영혼합 산업도면 CER ~0.31 한계
- [ ] PdfPig / OoxmlExtractor 가 추출한 이미지에 OCR 적용 → `ChunkTags(TagKind="ocr")` 또는 `Chunks.Text` 보강 (도면 라벨 CV01/DI12)
- [ ] (검토) Tesseract 정확도 미달 시 PaddleOCR microservice 폴백 또는 Phase 2 의 LLM vision caption 으로 흡수 (어차피 eager VLM caption — s6-r18 D-2-2 — 이 OCR 보다 정밀할 가능성)
- [ ] FTS5 trigger 가 OCR 보강 텍스트도 자동 색인

### Phase 4 — Embedding / Hybrid Retrieval
- [ ] sqlite-vec native binding 추가
- [ ] `IEmbeddingGenerator<,>` 추상화 wrapper — 기본 backend 로컬 (ONNX direct wrapper 또는 OllamaSharp `nomic-embed-text`), 외부 API (OpenAI/Voyage) opt-in only
- [ ] 모델 선정 — bge-m3 (1024d) / multilingual-e5-large-instruct / jina-v3 진입 시 재검토
- [ ] Searcher 에 hybrid retrieval (BM25 + vector, RRF 결합) — OCR 보강 텍스트도 embedding 대상
- [ ] enrichment (요약/태그) — Microsoft.Extensions.AI batch API 활용 (선택)

### Phase 5 — 고급 (선제 batch caption [격하·옵션] / 자동화 / 증분)
- [ ] **선제 batch caption (격하·옵션)** — Phase 2 의 on-demand caption cache 가 통상 우월. "색인 직후 사용자가 문서 통째 LLM 질문" 같은 특수 시나리오만. Anthropic/OpenAI Batch API (50% 할인). 구현은 Phase 2 의 caption cache 채우기 logic 재사용.
- [ ] "스펙 §X → Flow/Work 자동 분해" 워크플로 (LLM 이 search 후 apply_model_doc 까지 한 turn)
- [ ] 증분 색인 — 문서 update 감지 (FileHash 비교)
- [ ] (보류) Multimodal embedding (CLIP/SigLIP) — SigLIP 2 일반 한국어 개선, 단 산업 도메인 한국어 약점 잔존

---

## 5. 관련 파일 / 경로 (요약)

### 신규
- `Solutions/Core/Ds2.LightHouse/Ds2.LightHouse.fsproj` (+ 4.3/4.4 의 .fs 파일들)
- `Solutions/Tests/Ds2.LightHouse.Tests/` (xunit + FsCheck)
- `Apps/Promaker/Promaker/LlmAgent/Tools/AttachmentTools.cs`
- `Apps/Promaker/Promaker/Knowledge/AttachmentIngestService.cs`
- `Apps/Promaker/Promaker/LlmAgent/Prompts/5.knowledge-base.md`
- **`Apps/Promaker/Promaker/Dialogs/KbManagerDialog.xaml(.cs)`** (r4 — collection 등록/관리 UI)

### 수정 (Phase 1 — lib 본체 + 부분 통합)
- `Solutions/Core/Ds2.LlmAgent/AttachmentClassifier.fs` (detectEncoding forward 만, 표면 무변경)
- `Solutions/Core/Ds2.LlmAgent/LlmMessage.fs` (ImageFormat 을 LightHouse alias 로)
- `Solutions/Core/Ds2.LlmAgent/Ds2.LlmAgent.fsproj` (LightHouse 참조 + 컴파일 순서)
- **`Solutions/Core/Ds2.LlmAgent/CLAUDE.md`** (ImageFormat / AttachmentClassifier SSOT 박제 갱신 — 4.2a 와 같은 commit, line 진입 시 grep 재확인)
- `Solutions/Directory.Packages.props` (DocumentFormat.OpenXml 신규, PdfPig 이전)
- `Apps/Promaker/Directory.Packages.props` (PdfPig 제거)
- `Apps/Promaker/Promaker/Promaker.csproj` (LightHouse 참조, PdfPig 직접 참조 제거)
- `Apps/Promaker/Promaker.sln` (LightHouse + Tests 추가)
- **`Solutions/Ds2.sln`** (LightHouse + Tests 추가 — sln 2개 모두 갱신, §4.1)

### 수정 (r5 SKIP — server phase Phase S5 가 흡수)
대안 B 로 본 Phase 1 에서 진행 안 함. server phase 진입 시 server SSOT 에 맞춰 신설/갱신:
- ~~`Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs` (`All` 배열에 attachment 4종 추가)~~ — Phase 1 에서 등록 안 함. server phase 도 host 위치가 service 측이라 *Promaker 측은 영구 미추가*. 현 6종 (`expectedSet`) 유지 확인만.
- ~~`Solutions/Tests/Ds2.LlmAgent.Tests/PromakerToolNamesDriftTests.fs` (expectedSet 6→10)~~ — 위와 동일 사유. **확인만** (현 6종 유지)
- ~~`Apps/Promaker/Promaker/LlmAgent/LlmTurnContext.cs` (KnowledgeBase 필드 추가)~~ — server phase 에서도 **회피** (`kb-server.md §3.13`, read-path 가 service 측)
- ~~`Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.LlmChat.cs` (turn 시작 시 LightHouse.openCollections 주입)~~ — server phase 에서 session 발급/해제 코드로 재설계 (`kb-server.md §4.2 Phase S5`)
- ~~`Apps/Promaker/Promaker/LlmAgent/LlmConfig.cs` (`KbCollections : List<KbCollectionEntry>` 필드 추가)~~ — server phase 에서 `{CollectionId, DisplayName, Active}` + `LightHouseService {BaseUrl, ApiKeyEncrypted}` schema 로 신설
- ~~`Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml(.cs)` (LLM 탭에 "KB 관리..." 버튼)~~ — server phase 에서 신설 (`kb-server.md §4.2 Phase S5`)
- ~~root `.gitignore` (`.lighthouse-kb/` 추가)~~ — server phase 에서는 사용자 폴더에 흔적 0 정책이라 영구 불필요

### 동기화 의무 (수정 안 하지만 cross-PR 추적 필요)
- **active** `Solutions/Core/Ds2.LlmAgent/doc/todo-llm-chat-attachment.md` (318 line) — 정책 19 (AttachmentClassifier SSOT) + ImageFormat DU wire 책임 진행 중. Phase 1 4.2a 진입 전 최근 commit 동기화 확인 (§6.12). 본 작업 commit 후 그 todo 의 정책 19 항목에 cross-link 추가 (별도 commit).

### 참조용 (수정 없음)
- **`Apps/Promaker/Docs/todo-lighthouse-kb-server.md`** (s0, 후속 phase) — service 도입 design 박제. 본 Phase 1 완료 후 진입. parent r4 의 §3.9 / §3.10 / §3.18.2 / §4.1 / §4.5 / §6 m15 / §6 m16 회귀 매트릭스 보유.
- `Apps/Promaker/Promaker/LlmAgent/McpHostService.cs` — Kestrel + MCP 호스트 패턴
- `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs` — 기존 tool 패턴
- `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md` — tool surface 문서 패턴
- `Apps/Promaker/Promaker/LlmAgent/Prompts/4.attachments.md` — prompt injection 방어 룰 (3.0 의 chat 경로)
- `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.Attachments.cs` — 4.2 의 무영향 확인 대상
- `Solutions/Core/Ds2.LlmAgent/doc/done-llm-chat-attachment.md` — 5종 wire 진입점 변경 시 별도 PR 전례 (m22)
- `Apps/Promaker/Docs/yaml-protocol-v0.md` — apply_model_doc SSOT

---

## 6. 주의 사항

1. **CLAUDE.md 자가 검열 trigger 다중 충족 예상** — phase 별 변경 commit 시점에 sub-agent 검열 의무. 본 작업은 (i) 신규 타입/함수 다수, (ii) 단일 파일/2+파일 동시 변경, (iii) public API/SSOT 갱신 (FileKind / KnowledgeBase facade / RefLocator EBNF / index.db schema), (iv) C# interop 인접 영역 변경 모두 해당.
2. **commit 정책** — multi-step plan 의 "go" 동의를 commit step 까지 묶지 말 것. commit 은 별도 confirm. (memory: `feedback_commit_authorization`)
3. **F# 진입 시점에 `~/.claude/dotnet.md` 지침 준수**.
4. **선호 stack** — log4net (logDebug/logInfo/logWarn/logError), Dapper, JSON 은 Newtonsoft 기본. **SQLite 는 본 작업에 한해 `Microsoft.Data.Sqlite` 채택** — 사유: (a) repo 표준 (Solutions 의 다수 위치 사용), (b) `bundle_e_sqlite3` 가 FTS5/JSON1/R*Tree 기본 포함 (사실 검증 완료, R6 의 "별도 bundle 필수" 권장 기각), (c) Dapper/async/AOT 친화. 사용자 글로벌 선호 `System.Data.SQLite` 와 다름 — 진입 시 재확인 권장.
5. **예외 처리** — try/catch 자제. fail-fast 우선. 외부 환경 (파일 손상, 인코딩 실패) 만 log 후 skip.
6. **line ending / 인코딩** — F# / C# 은 **LF + UTF-8 (BOM 없음)**. .bat/.ps1 은 CR/LF. Promaker.csproj mojibake 전례 회피 — 문서 작성 시 PowerShell `Out-File` 의 기본 UTF-16 BOM 경계.
7. **AttachmentClassifier 는 활성 코드** — `LlmChatViewModel.Attachments.cs:265,270,348` (UI thread, classify+Classification.AcceptImage) + `:360` (background thread, detectEncoding) + `App.xaml.cs:148` (CodePagesEncodingProvider 등록) + `AttachmentClassifierDriftTests.fs` (**현행 Fact 13건**). 4.2 의 부분 통합은 **표면 무변경** 원칙 (detectEncoding forward + ImageFormat alias 만). C# DU interop break 위험 차단.
8. **`Ds2.Core` 미참조 약속 준수** — LightHouse 가 entity 타입을 받기 시작하면 base 책임 경계가 무너짐. API 는 `string projectKbRoot` 만.
9. **두 경로 완전 분리 SSOT (3.0)** — chat image drop ≠ KB ingest. chat 측 변경 0, KB 측 독립 신설. 한 경로 변경이 다른 경로에 영향 없음을 PR review 시 점검.
10. **`Prompts/4.attachments.md` vs 신설 `5.knowledge-base.md`** — 책임 분리: 4 는 단발 inline 첨부 + injection 방어 (3.0 의 chat 경로), 5 는 사전 색인 KB 검색 (3.0 의 KB 경로).
11. **MEMORY.md `## Project` 등록 트리거** — Phase 1 진입 commit 직후 본 todo 항목을 메모리에 등록.
12. **active todo-llm-chat-attachment.md 동기화 의무** — Phase 1 4.2a 진입 전 `Solutions/Core/Ds2.LlmAgent/doc/todo-llm-chat-attachment.md` 의 최근 commit 확인. AttachmentClassifier / ImageFormat 의 wire 책임이 진행 중일 수 있어 본 작업과 cross-PR 충돌 가능. 본 작업 후 그 todo 의 정책 19 항목에 cross-link 추가 (별도 commit).
13. **본 todo 의 line 번호 박제 (예: LlmMessage.fs:4, App.xaml.cs:148, LlmChatViewModel.Attachments.cs:265 등) 는 stale 위험** — `todo-dock-layout.md` v6 의 `MainWindow.xaml.cs:108-109` stale 화 전례. 진입 시 grep 으로 재확인. 가능한 곳은 symbol 기반 (함수/클래스명) 참조로 약화 검토.
    > **본 grep 시점 (2026-05-17, r6 commit 시점)**: 박제 8종 (Fact 13건 / PdfPig / LlmTurnContext.cs:57 / App.xaml.cs:148 / LlmChatViewModel.Attachments.cs:265-360 / Promaker.csproj PdfPig:43 / Directory.Packages.props 2위치 / PromakerToolNamesDriftTests expectedSet 6종) 모두 fresh. 다음 r7 진입 시 재검증 trigger.
14. **`Apps/Promaker/Docs/` 위치 vs Solutions 무게중심 불일치** — 실제 신규 코드 산출물의 80% 가 `Solutions/Core/Ds2.LightHouse/` 임에도 본 todo 는 Apps/Promaker/Docs/. 기존 관례 (`Solutions/Core/Ds2.LlmAgent/doc/todo-*.md` 4건) 와 다름. Phase 1 진입 commit 직전에 `git mv` 검토 (대안: 현 위치 유지 + 본 항목 stale 표기).
15. **r4 — collection 의 PII / 보안** — 사용자가 임의 폴더를 collection 으로 등록 → LLM 이 search/read 로 폴더 안 모든 파일 내용 접근. 사용자가 무심코 비밀 문서 (`.env`, 인사기록, 영업비밀) 가 든 폴더를 등록하면 LLM 외부 전송 시 누출. `5.knowledge-base.md` 의 system prompt 에 "KB 내용은 LLM provider 로 송신될 수 있음" 명시 + `KbExtensions` filter 가 `.env` 등 거부 + KbManagerDialog 첫 등록 시 consent 다이얼로그 권장.

    > ⚠ **service 도입 시 강화** — multi-tenant α flat 정책이라 *다른 사용자도 검색 가능* → consent dialog 의무화 (단순 권장이 아님). 상세 `kb-server.md` §6 m2.

16. **r4 — ATTACH 된 collection 의 index.db schema 버전 불일치** — 두 collection 이 다른 `IndexerVersion` 으로 색인되었으면 UNION 검색 결과의 column 의미가 다를 수 있음. open 시 모든 active collection 의 `Meta.indexer_version` 비교 → 불일치 시 사용자 안내 + 해당 collection 비활성 fallback.

    > ⚠ **service 도입 시 완화** — server 의 upload 시점 IndexerVersion gate (`kb-server.md` §3.12) 가 흡수. paired-release 강제 (`kb-server.md` §6 m1) 로 사실상 drift 발생 안 함.

---

## 7. 다음 세션 첫 행동 권장

**대안 B (r5/r6) 적용 후 Phase 1 = lib 본체 + lib unit test 만**. §4.5/§4.6/§4.1 첫 task/§4.8 일부는 SKIP — server phase (`todo-lighthouse-kb-server.md`) 가 흡수.

1. **본 문서 + `todo-lighthouse-kb-server.md` 의 §0 D-id 정의표 + §3.14 회귀 매트릭스** 동시 정독 (§0 의 진행 상태 요약 부터).
2. 진입 시 grep / 사실 재확인 (§6.13 박제 stale 위험):
   - `ModelContextProtocol.AspNetCore` 버전 (nuget list 로 최신 stable + breaking change 확인)
   - `Directory.Packages.props` 두 위치 (Solutions / Apps/Promaker) 의 CPM 적용 범위 + PdfPig 위치
   - `Promaker.sln` 외 `Solutions/Ds2.sln` 존재 확인
   - WPF DI 컨테이너 (KnowledgeBase facade lifecycle 결정 — §3.18.1) — grep `AddSingleton|AddTransient|ServiceCollection`
   - `AttachmentClassifierDriftTests.fs` 의 `^\[<Fact>]` count = 현재 13건 유지 확인 (§6.7 박제 stale 점검)
3. **Phase 1 의 §4.1 진입 confirm** (`.gitignore` 첫 task 는 SKIP, 나머지 = sln 2개 + LightHouse project 생성 + NuGet 등록 진행).
4. **Phase 1 의 §4.2 (부분 통합)** 는 commit 3개 분리 (4.2a 이전 / 4.2b LlmAgent slim / 4.2c 참조갱신). 각 commit 마다 `AttachmentClassifierDriftTests` 통과 확인. 필요 시 별도 PR 분리.
5. **Phase 1 의 §4.3/§4.4/§4.8 lib unit test** 까지 진행. §4.5 (Promaker 통합) / §4.6 (`5.knowledge-base.md`) 는 진행 *금지* — server phase 흡수.
6. MEMORY.md `## Project` 에 본 todo 등록 (주의 사항 11).
7. Phase 1 4.2a 진입 전 `Solutions/Core/Ds2.LlmAgent/doc/todo-llm-chat-attachment.md` (active) 의 최근 commit 동기화 (주의 사항 12).
8. Phase 1 lib 본체 완료 → server phase 진입 confirm 받기 (`todo-lighthouse-kb-server.md` Phase S0 → S1).
9. commit message — (i) 키워드 LightHouse/KB/MCP/attachment_search 포함 (4줄 이내), (ii) SQLite 채택 관련 안내는 §6.4 SSOT 참조, (iii) rev 표 footnote 로 "외부 reviewer N명 검증 반영 통합본" 명시.
