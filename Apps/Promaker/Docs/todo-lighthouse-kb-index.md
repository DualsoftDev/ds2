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
- **현재 rev**: **r6** (r5 대안 B 위에 `--inspect 3` reviewer Critical 6 + Major 23 + Minor 15 반영. r0~r3 외부 reviewer 11명 + r4/r5 사용자 design 입력 + r6 reviewer 3명 = 누계 14 reviewer 검증)
- **후속 phase 별도 추적**: `todo-lighthouse-kb-server.md` (s0-r3) — service 도입 design 박제 (r5 대안 B 위에 얹는 incremental). 본 todo Phase 1 (lib 본체 + lib unit test 만) 완료 후 진입.
- **모드**: `--plan` 만 수행 (코드 변경 0, 본 todo 만 갱신).
- **신규 코드 / 신규 파일 / git stage 변경 — 모두 0**.

### 사용자가 명시적으로 동의한 결정 (이전 세션에서 확정)
1. **신규 F# project 명 `Ds2.LightHouse`** — `Solutions/Core/` 하 신설, base 의미 (§3.1)
2. **타입 이름 KB-aware 일반화** — `FileKind` 신설, `Classification` 영구 잔류 (§3.3)
3. **`detectEncoding` 별도 모듈 분리** (§3.4)
4. **LightHouse 의 `Ds2.Core` / `Ds2.Editor` 미참조 invariant** (§3.2, §3.5)
5. **두 경로 완전 분리 SSOT** — chat image drop ≠ KB ingest (§3.0)
6. **AttachmentClassifier 부분 통합 축소** — `detectEncoding` / `ImageFormat` 만 LightHouse 이전, `Classification` 표면 무변경 (§3.11)
7. **Phase 1 환원** — 순수 FTS5 + text + outline + 4 tools, 이미지 인프라 Phase 2 로 이동 (§3.12, Phase 1)
8. **Phase 3↔4 swap** — Phase 3 = OCR, Phase 4 = embedding (§3.15.2)
9. **이미지 처리는 cached lazy** — 3 layer 캐싱 (blob/prompt cache/caption cache) (§3.15.3, §3.15.4)
10. **FTS5 tokenizer trigram** (한국어 필수) (§3.7, §3.12)
11. **n×m KB 운영** (r4) — collection = 사용자 임의 폴더 선택 (path-based, project 종속 X). LlmConfig.KbCollections (OS 사용자 전역) 에 등록. m 개 active → SQLite ATTACH union 검색. KbManagerDialog (ApplicationSettingsDialog 진입 버튼). 원본 사본 없음. read-only collection 은 read OK, write fail.
12. **server phase 흡수 — 대안 B** (r5) — parent Phase 1 의 **§4.5 전체 / §4.1 첫 task (.gitignore) / §4.6 (5.knowledge-base.md) / §4.8 의 Promaker 통합 의존 항목** 은 본 Phase 1 에서 진행하지 않음. server phase (`todo-lighthouse-kb-server.md` Phase S5) 가 흡수. 본 Phase 1 = LightHouse lib 본체 (§4.2/§4.3/§4.4) + lib 자체 unit test 만. 사유: parent Phase 1 의 §4.5 가 server phase 진입 시 60%+ throwaway / 사용자 데이터 migration noise / yo-yo commit history 6건 회피.

### 다음 세션에서 확정해야 할 보류 항목 (Phase 1 lib 본체에 한정)

대안 B (r5) 로 §4.5 / §4.6 SKIP — 그 단원 의존 보류 항목은 본 표에서 제거하고 `kb-server.md §4.3` 미확정 표로 이전.

| 항목 | 위치 | 권장 default | 확정 시점 |
|---|---|---|---|
| KnowledgeBase facade 형식 (record-of-functions vs interface) | §3.18.1 | record-of-functions (F# idiomatic) | Phase 1 4.4 진입 |
| 본 todo 파일 위치 git mv 여부 (Apps/Promaker/Docs/ → Solutions/Core/Ds2.LightHouse/doc/) | §6.14 | Phase 1 진입 commit 직전 mv | Phase 1 4.1 진입 직전 |
| ModelContextProtocol.AspNetCore 1.2.0 → 1.3.0 업그레이드 여부 (출처 + breaking change 확인) | §2, §4.1 | 별 release note 검토 + nuget list 후 결정 | Phase 1 4.1 진입 |

**server phase 로 이전된 항목** (`kb-server.md §4.3` 미확정 표 참조):
- `attachment_*` 의 KB root 도달 경로 — server `§3.8` session-based routing 으로 대체 (parent §3.18.2 의 채택안 (a) 는 r5 SKIP)
- SQLite ATTACH limit (10) 초과 시 안내 — server `§3.8` Q2 의 hard fail 가드로 흡수

### 다음 세션 즉시 할 일 (4 step)
1. **본 todo 정독** — 특히 §0 / §3.0 / §3.11 / §3.18.2 / §6 주의 사항 14건
2. **사실 재확인** — §7 의 grep 항목 일괄 실행 (line 박제 stale 위험 §6.13). 특히:
   - `AttachmentClassifier` 호출처 (4.2 진입 전 동기화 의무 §6.12)
   - `active todo-llm-chat-attachment.md` 최근 commit 동기화
   - `PromakerToolNamesDriftTests.fs:18` 의 modelToolsPath 단일 vs 전체 스캔 현황
   - `LlmTurnContext.cs:57` 의 현재 생성자 signature
3. **Phase 1 4.1 진입 confirm** 받기 — `.gitignore` 격상 + project 생성 + sln 2개 + NuGet 등록.
4. **commit 은 별도 confirm** (memory: `feedback_commit_authorization` — multi-step plan 의 "go" 동의에 commit 까지 묶지 말 것)

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
| **KB ingest** | Attachments dock 패널 (별도 UI) | `Ds2.LightHouse.classifyForKb` (신규, 독립) | 색인 후 MCP tool 통해 *검색 시점* 의 LLM 이 요청해야 (cached lazy) | **색인** |

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
- `Ds2.LightHouse.FileKind` DU = `{ Pdf | Docx | Pptx | Xlsx | Text | Markdown | Unsupported of ext }`
  — KB 추출기 라우팅 전용. Phase 별 활성 case 확대.
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
| Phase 2 이미지 | `p=14#img=2` | `p.14 그림 2` |

**EBNF (저장형)**:
```
RefLocator   = Unit "=" Value ( "#" SubKey "=" SubValue )*
Unit         = "p" | "slide" | "sheet"
Value        = digits | sheet-name [ "!" range ]
SubKey       = "img" | ...
```

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

사양서에는 plant layout, 시퀀스 차트, wiring diagram, 표/그래프가 raster 로 박힌 경우가 빈번 →
모델링 의사결정의 직접 근거가 되므로 *언젠가는* 필요. Phase 2 부터 도입.

#### 3.15.1 vision token 발생 시점 (정확한 분해)

| 단계 | 처리 주체 | vision token |
|---|---|---|
| (a) 자연어 query → text 검색 (FTS5 trigram) | LightHouse / Searcher | **0** |
| (b) hit 결과 LLM 에 전달 (excerpt + `hasImages=true` 메타) | MCP tool 응답 | **0** |
| (c) LLM 응답 안 citation `[파일.pdf p.14]` | LLM 텍스트 출력 | **0** |
| (d) UI 가 사용자에게 image 표시 (citation 클릭 / 결과 패널) | WPF UI | **0** (LLM 무관) |
| (e) LLM 이 *이미지 자체 해석 필요* 판단 → `attachment_read(includeImages=true)` | LLM ↔ MCP | **여기서 1회 발생** |

즉 통상 텍스트 사양 질의는 vision 0. (e) 는 사용자가 도면 자체를 추궁할 때나
LLM 이 답변 정확도를 위해 도면 추론이 필요할 때만 발생.

#### 3.15.2 Phase 별 방식 비교

| Phase | 방식 | 색인 시점 LLM 호출 | (e) 시점 vision token | 비고 |
|---|---|---|---|---|
| 1 (MVP) | **이미지 미처리** | 0 | — (e) 미지원 | 페이지 본문/캡션 텍스트로 페이지 *위치* 까지 도달. ImageCache/ImageReferences 도 schema 없음. |
| 2 ⭐ | **cached lazy 첨부** — `attachment_read(includeImages|caption_only)` | 0 | 이미지당 *모델 generation 당 1회* (caption cache miss 시) | (e) 일어난 turn 만 vision. caption cache hit 시 0. |
| 3 | **OCR (Tesseract.NET + 한글 traineddata)** | 0 (로컬 CPU) | 영향 없음 | 도면 라벨 / I/O 번호 (CV01, DI12) → `ChunkTags(TagKind="ocr")` 또는 `Chunks.Text` 보강. ⚠ Tesseract 한영혼합 산업도면 CER ~0.31 한계 → PaddleOCR microservice 폴백 또는 Phase 2 LLM vision caption 흡수 검토 |
| 4 | **embedding / hybrid retrieval** | 이미지당 임베딩 1회 (선택) | — | swap 됨: OCR 이 embedding 앞에 와야 한국어 도메인 어휘가 retrieval 에 반영 |
| 5 (옵션) | **선제 batch caption / 자동 모델링** | 이미지당 1회 (전수) | 이후 0 | 통상 Phase 2 cached lazy 가 우월. 특수 시나리오만. |
| 보류 | **Multimodal embedding (CLIP/SigLIP)** | 이미지당 임베딩 1회 | — | SigLIP 2 (2025) 로 일반 한국어 개선, 단 산업 도메인 한국어 약점 잔존 → VLM 캡션 + text embedding 이 통상 더 우수 |

#### 3.15.3 lazy 캐싱 전략 (Phase 2 "cached lazy" 구성, 3 layer)

1. **image blob + sha256 keying** (Phase 2 색인 시점에 추출/저장)
   - `<collection-root>/.lighthouse-kb/blobs/images/<sha256>.<ext>` (r4 — 폴더 이름 정정. 이전 rev 의 `.promaker-kb/` 잔재 제거)
   - 같은 도면이 한 collection 안 여러 문서/페이지에 중복 등장해도 1개 blob (cross-collection 은 §3.9 — collection 별 분리)
2. **Anthropic prompt cache** — 같은 채팅 5분 TTL
   - cache_control breakpoint 를 image content block 뒤에 둠
   - ⚠ **Anthropic 전용** (OpenAI/Ollama 미지원). cache write premium 1.25× → 재사용 ≥ 2회 시만 net 이득.
   - 같은 image 를 5분 안에 재전송 시 vision token 약 1/10
3. **on-demand caption cache** — sha256 → caption text 영구 저장
   - 최초 (e) 시점에 "이 이미지를 1~2문장으로 설명" LLM 호출 → `ImageCache.CaptionText` 저장
   - 다음부터 `caption_only=true` 모드는 vision 호출 0, 텍스트 갈음
   - `includeImages=true` 모드도 caption 을 hint 로 동반
   - **invalidation policy**: `CaptionModel` 의 generation tier (예: "claude-opus-4-7") 가 다르면 재생성. Patch/release 차이는 재생성 X.

#### 3.15.4 핵심 결정 — Phase 2 cached lazy 우선

이유:
1. **색인 변경 0** (Phase 2 의 image schema 도입 외) — Phase 1 인프라 위에 image blob 저장 + `attachment_read` 옵션 추가
2. **LLM native vision 활용** — query 시점의 모델이 *문맥에 맞게* 해석 (색인 시점에 미리 결정 ×)
3. **모델 교체 시 vision 품질 자동 개선** (caption cache 의 invalidation policy 와 정합)
4. **vision 비용 최소** — 이미지당 *모델 generation 당 1회* (caption 생성 시) + 사용자 명시 추궁 시 (prompt cache 로 추가 절감)
5. **trade-off 거의 없음** — 첫 (e) 시점의 약간의 latency (UX 적, 비용 아님) 외 단점 없음

→ Phase 5 의 선제 batch caption 은 사용 안 해도 발생하는 전수 비용 사유로 격하 (특수 시나리오만).

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

**색인 재구성 정책**:
- `Meta.IndexerVersion` 이 코드의 현재 IndexerVersion 과 다르면 자동 재색인 트리거
- shadow rebuild — `index.db.new` 에 새로 색인 → 완료 시 atomic rename (Windows 의 경우 close 후 swap)
- batch commit 단위 = chunk **500 / commit** (메모리 / WAL 크기 / cancellation 응답성 균형)
- CancellationToken 지원 — 사용자 UI 의 "취소" 버튼에서 종료 가능
- 부분 commit 상태 회복 — IndexerVersion mismatch 로 자동 재색인

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

**4.1 Ds2.LightHouse project 생성**

> ⛔ **대안 B (r5) — 첫 task `.gitignore` 는 SKIP**. server phase 가 사용자 폴더에 흔적 0 정책이라 in-process MVP 단계에서도 prod 사용자 노출 없음 (§4.5 skip 이라 사용자가 KbManagerDialog 로 collection 등록 불가). lib 자체 unit test 만으로는 사용자 폴더 직접 사용 안 함. **하단 첫 task 줄에 line-through 표시**, 그 외 task (project 생성 / sln / NuGet 등록) 는 정상 진행.

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

**4.2 부분 통합 (LightHouse 로 이전 — 3 commit sub-grouping)**

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
- [ ] `Models.fs` — Document / OutlineNode / Chunk / Citation / Query / Result / FileKind 타입
- [ ] `Extractors/IExtractor.fs` — 공통 인터페이스 (CancellationToken + IDisposable)
- [ ] `Extractors/PdfExtractor.fs` (PdfPig) — 페이지 단위 text + bookmark (TOC). PdfPig 페이지 IDisposable 즉시 dispose (OOM 회피).
- [ ] `Extractors/OoxmlExtractor.fs` (OpenXml) — docx 만 활성 (Phase 1). pptx/xlsx 는 Phase 2.
- [ ] `Extractors/TextExtractor.fs` (txt/md) — `Ds2.LightHouse.TextEncoding.detectEncoding` 사용
- [ ] `Chunker.fs` — 구조 우선 + 보조 분할 (200~500 token)
- [ ] `Classifier.fs` — `classifyForKb : string -> FileKind`

**4.4 LightHouse 본체 — 저장 / 검색 (Phase 1)**
- [ ] `SqliteStore.fs` — 3.12 의 schema + 3.17 PRAGMA + IndexerVersion 자동 재색인 + shadow rebuild + batch commit (500/commit) + CancellationToken. **read-only 폴더 처리**: open 시점에 폴더 쓰기 권한 probe → write 시도 (색인/재색인) 면 fail+안내, read 만 (search) 이면 `Mode=ReadOnly` 로 open.
- [ ] `Searcher.fs` — FTS5 BM25 (trigram), k 제한, excerpt 생성 (≤ maxExcerptTokens), `hasImages: false` (Phase 1). **multi-db UNION 동적 생성** (r4) — m 개 ATTACH 된 collection 의 `ChunksFts` 를 UNION ALL 로 결합 후 BM25 점수 정렬. **fileId 합성** — `<collection-index>:<documents-id>` 형태로 cross-collection unique 보장.
- [ ] `Indexer.fs` — Extract → Chunk → Store 파이프라인 orchestrator
- [ ] `KnowledgeBase.fs` — 외부 진입점 facade. **`openCollections(activePaths: string[]) -> KnowledgeBase`** (r4 — multi-collection). 내부에 SQLite ATTACH (alias `kb0`/`kb1`/.../`kbN-1`) + UNION search. `Ds2.Core` entity 미참조. DI lifecycle = §3.18.1 에서 결정.
- [ ] **ATTACH limit 가드** — active 셋 길이 > 10 시 사전 fail (사용자 UI 가 active toggle 단계에서 막아야 정상)

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

**4.8 검증 / 테스트**

> ⚠ **대안 B (r5) — 본 §4.8 의 일부 항목 SKIP**. Promaker 통합 (§4.5) 에 의존하는 테스트는 본 Phase 1 에서 수행 불가. **lib 자체 unit test 만** 진행. SKIP 항목은 `~~취소선~~` 표시. server phase Phase S5 에서 in-process 동등 시나리오를 service 경로로 검증.

**진행 항목 (lib 자체 unit test)** — `Solutions/Tests/Ds2.LightHouse.Tests/` 신설 (xunit + FsCheck). 4.2 의 부분 통합 무영향 회귀 보호.

*4.8a (parser / chunker / locator — 결정적 변환)*:
- [ ] **RefLocator parser round-trip** (§3.13) — 저장형 ("p=14", "slide=5", "sheet=BOM!A1:D40", "p=14#img=2") → parsed → 저장형 동일성. EBNF 규칙 위반 입력 (예: "page=14") 거부
- [ ] **Chunker boundary 검증** (§3.8) — 정확히 199/200/500/501 token 입력 시 chunk 분할 위치 결정성. 빈 입력 / 단일 token / 매우 긴 단일 단락
- [ ] **PdfExtractor** — 정상 PDF + bookmark / 손상 PDF (`log + skip`, §3.16 fail-safe 정책) / 암호화 PDF (`log + skip`) / TOC 없는 PDF / 한자 PDF / 빈 페이지 다수
- [ ] **OoxmlExtractor (docx)** — heading 깊이 5단계 + 빈 paragraph + 표 + 한국어/한자/영문 혼합
- [ ] **TextExtractor + detectEncoding** — UTF-8 BOM / UTF-8 no-BOM / CP949 / UTF-16 LE/BE / 깨진 byte stream

*4.8b (FTS5 / SQLite 운영)*:
- [ ] **한국어 trigram 회귀** — query "컨베이어" 가 본문 "컨베이어가/를/는/도" 포함 페이지 hit. "컨베" 2-char 만 hit 여부. 영문/한글 혼합 ("CV01 컨베이어"). 한자 ("運転") boundary
- [ ] **FileHash idempotent** 재첨부 검증 (같은 파일 두 번 ingest → Documents.Id 1개 유지)
- [ ] **IndexerVersion bump** 자동 재색인 검증 (Meta.indexer_version 불일치 시 shadow rebuild → atomic rename)
- [ ] **WAL 동시성** — 색인 중 search 호출 가능 검증
- [ ] **0-doc collection** — 빈 폴더 ingest → valid index.db + search empty 정상 응답 (MA18)
- [ ] **0-byte 파일 / extensionless / 미지원 ext** — Extractor 가 log + skip (Documents 테이블에 status='extract_failed' 박제 — 정책 명문화)

*4.8c (multi-collection lib API)*:
- [ ] **multi-collection ATTACH** — `Searcher.fs` 의 단위 테스트로 2~3 collection 동시 ATTACH 시 UNION 검색 결과의 fileId cross-collection unique 보장 (fileId 형태는 MA23 의 `<collection-guid>:<documents-id>` 또는 lib 단위에서는 인덱스 기반 OK — server phase 에서 guid 로 합성). **9/10/11 boundary test** (MA17, SQLITE_MAX_ATTACHED 확인 후)

*4.8d (cross-PR 회귀 보호)*:
- [ ] **AttachmentClassifierDriftTests (현행 Fact 13건) 통과 유지** — LlmAgent 측 SSOT 회귀 보호. 본 lib 와 별개 project (`Solutions/Tests/Ds2.LlmAgent.Tests/`) 라 LightHouse.Tests 와 분리. 4.2 의 부분 통합 무영향 보장. 신규 case 추가 시 본 todo 갱신.

**SKIP 항목 (대안 B / r5 — Promaker 통합 의존, server phase Phase S5 가 흡수)**:
- ~~LLM 이 `attachment_search` 호출 → citation 포함 응답 생성~~ → server phase S5 의 IntegrationTests
- ~~LlmConfig.KbCollections persist round-trip~~ → server phase S5
- ~~read-only collection 검증 (r4)~~ → server phase 에서는 사용자 폴더 읽기 자체 없음 (server-side storage), 폐기. parent §3.18.2 회피와 함께.

### Phase 2 — 포맷 확대 + 이미지 인프라 신설 + cached lazy
- [ ] schema 확장 — `ImageCache` / `ImageReferences` 테이블 (3.12 의 주석 처리 블록 활성) + `Chunks.ImageCount` 컬럼 (ALTER TABLE) + IndexerVersion bump
- [ ] `ImageStore.fs` — sha256 + blob 저장 + ImageCache/ImageReferences upsert (cross-document 공유, 단일 프로젝트 안)
- [ ] PdfExtractor / OoxmlExtractor 가 이미지 raw 추출 + ImageStore 호출 (Phase 1 무변경 분리)
  - PdfPig DCT/JPX/JBIG2 decode 는 별도 NuGet 필요 — 진입 시 확인
  - PdfPig 페이지 PNG 렌더 fallback 필요 시 `PdfPig.Rendering.Skia` 동반
- [ ] OoxmlExtractor 에서 pptx (슬라이드 + speaker notes) 활성
  - 슬라이드 PNG export — LibreOffice headless `--convert-to png` 폴백 또는 raster image 만 추출 (caption_only)
- [ ] OoxmlExtractor 에서 xlsx 활성 — 컬럼 헤더 + 행 그룹 (10~50 행 packed) 패턴 (3.8)
- [ ] `attachment_read` 의 ref 파서 강화 (sheet=BOM!A1:D40 + p=14#img=2)
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
- [ ] (검토) Tesseract 정확도 미달 시 PaddleOCR microservice 폴백 또는 Phase 2 의 LLM vision caption 으로 흡수 (어차피 cached lazy 가 OCR 보다 정밀할 가능성)
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
