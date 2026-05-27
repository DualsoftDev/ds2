# todo: documents-based-gfm — Promaker DS 모델 초안 생성 PoC 진행 계획

> 본 문서는 `documents-based-gfm.md` (사실 / 분석 SSOT) 의 **작업 항목 / PR 분할
> / 진입 조건 / 일정 / 후속 결정 항목** 분리본.
>
> 사실 (입력 자료 분석 / 정보 매트릭스 / 매핑 규칙 / 시나리오 진화 비교 / 부족 정보 /
> 참고 문서) 부분은 `documents-based-gfm.md` 참조. 본 문서는 그 위에 구축할 **실행
> 계획 / 산출물 / 검증 / 차후 결정**.
>
> 명명 정합: `documents-based-gfm.md` §0 의 시나리오 (A → A+++++) 와 본 문서의
> Phase (P1~P4) 는 직교. PR-I (Indexer 시리즈) 는 PR 분할 단위.

| rev | 일자 | 주요 변경 |
|---|---|---|
| r0 | 2026-05-27 | 초안 — `documents-based-gfm.md` 의 §5/§8.7~§8.9/§9 분리 + 메타리뷰 후속 결정 항목 흡수 |
| r1 | 2026-05-27 | P1 자동 진행 완료 — PR-I1~I5 + PR-I2.5 commit + 헤드리스 smoke 통과 + §1.1 진행 표 P1 [x][x] 갱신 |
| r2 | 2026-05-27 | `--review` 라운드 1 patch — Critical-1 (MarkdownCapPolicy Stage 3 stage2 split fix) + Major-2 (NormalizeSourceFolder narrow catch + log) + Major-5 (runPostIngestHooks extractors forward) + Critical-2 hand-off (RefreshSpecializedDigestAsync / ApplyPendingSpecializedDigest / SetActiveCollectionSourceRoots production GUI wiring 부재 docstring 박제 + 본 §0 진입 대기 갱신) |

---

## 0. 현재 진행 상태 (새 세션 진입 시 첫 정독)

### 완료
- `documents-based-gfm.md` r1 박제 (commit `fdadea69`) — 사실/분석 SSOT 확정
- 자료 A/B/C + sister KMM 매뉴얼이 `F:/Git/dualsoft/secrets/KBSamples/{core, sisters}/` 로 통일
- **P1 자동 진행 완료** (2026-05-27 turn, branch=`light-house-documents`) — PR-I1 ~ PR-I5 + PR-I2.5 commit (`2a32164f` / `79e9e736` / `4d1fbbfd` / `64e8dfd7` / `587c7e0c` / `8f149300`)
- 신규 lib code 약 1700 lines + tests 약 1400 lines (LF only / UTF-8 / 신규 warn 0 / err 0)
- 헤드리스 smoke [4][5][6] 통과 — `SpecializedDigestInjectionTests` 9 facts (Promaker.Tests 399 pass / Ds2.LightHouse.Tests 363 pass)

### 자동 진행 범위 (단일 정의 — §0, §1.1, §2, §4.1, §10.3 의 공통 SSOT)

**1-shot 정의**: single orchestrator run (내부적으로 multi-turn / sub-agent dispatch
포함, 단 사용자 추가 입력 0).

| 영역 | 자동 / hand-off | 범위 |
|---|---|---|
| **자동 진행** | ✅ | **PR-I1 의 첫 작업 agent 호출** ~ **PR-I5 의 commit 완료** (§10.3 의 8 단계 loop × 5 PR) |
| **P1 종료 신호** | 자동 판정 | PR-I5 의 commit 완료 + Promaker.Tests `SpecializedDigestInjectionTests.cs` 통과 (headless smoke) |
| **P2 hand-off** | ❌ human-in-loop | 도메인 전문가 검수 의무 (§5) — orchestrator 가 P1 종료 후 보고 + 종료 |
| **PR-I6 strategy 코드** | ◐ 자동 가능 (코드 영역 직교) | 단 운용 진입 = §4.2 spot-check 사용자 confirm 필수 |
| **PR-I7 strategy 코드** | ❌ | §4.3 KMM A/B test 사람 평가 후 자율 진입 |

**headless smoke (§4.1 [4][5][6] 재정의 — C4 해소)**:

§4.1 의 사용자 GUI 인터랙션 항목 (KB 활성 / chat 시작 / "YAML 초안" 요청 /
`attachment_fulltext` 자율 호출) 은 orchestrator 가 자동 판정 불가 → **PR-I5 의 신규
test `SpecializedDigestInjectionTests.cs` 가 e2e 흡수**:

| 기존 §4.1 항목 (사용자 GUI) | 신규 headless smoke (자동) |
|---|---|
| [4] KB collection 활성 후 chat 시작 + system prompt inject | `SpecializedDigestBuilder.Build` 직접 호출 후 합본 string assert |
| [5] 사용자 "S204 KEY 지그 YAML" 요청 | 고정 prompt 로 mock LLM client (또는 record-replay) 호출 + 출력 YAML 의 `ValidateModelDoc` 통과 assert |
| [6] `attachment_fulltext` 자율 호출 | 동일 mock 의 tool_use 시뮬레이션 호출 1+ 회 assert |

→ 사용자 GUI 검증은 P2 hand-off 후 사용자 수동 수행. P1 종료의 정량 trigger 는 위
headless smoke 통과로 단일화.

### Orchestrator 시작 시 fail-fast 점검 (A7, C1, C2, m9 해소)

**적용 시점**: orchestrator 의 **첫 PR-I1 진입 turn 만**. 이후 PR turn 은 작업 agent 의
산출물로 untracked / modified 발생이 정상이므로 git status 점검 skip.

| # | 항목 | 명령 (§11.5 와 1:1) | 합격 기준 |
|---|---|---|---|
| 1 | working tree clean | `git status --porcelain` | 출력 0 행 |
| 2 | branch 준비 | `git branch --list light-house-summary` | 1 행 (없으면 §11.5 의 생성 명령 자동 실행) |
| 3 | .NET 9 SDK | `dotnet --version` | `9.*` 매치 |
| 4 | 자료 A/B/C 존재 | `ls F:/Git/dualsoft/secrets/KBSamples/core/` | ≥ 3 파일 |
| 5 | sister 자료 존재 (PR-I7 진입 시만) | `ls F:/Git/dualsoft/secrets/KBSamples/sisters/` | ≥ 1 파일 |
| 6 | sln 위치 | `test -f F:/Git/ds2/light-house/Solutions/Ds2.sln` | exit 0 |
| 7 | service build dry-run | `dotnet build F:/Git/ds2/light-house/Solutions/Tools/Ds2.LightHouseService/Ds2.LightHouseService.fsproj -c Debug --no-incremental` | exit 0 |

→ 위 7 항목 중 1건이라도 실패 시 orchestrator 즉시 종료 + 사용자 보고. 단 #2 의 branch
부재는 즉시 종료 대신 §11.5 의 `git switch -c light-house-summary` 명령으로 자동 생성.

### 진입 대기 (Phase P2 — 도메인 전문가 검수, human-in-loop)
- §5.4 의 6 step 사용자 액션 시퀀스 (`광명2_204_draft.yaml` 생성 → HKMC 검수 의뢰 → 보정 → golden 박제)
- 운영 GUI 활성화는 `KbCollectionEntry.SourceFolder` schema 확장 후 (PR-I5 의 `GetActiveCollectionSourceRoots()` 가 production 시 빈 list 반환 — 별 PR backlog)
- **(r2 박제 — --review 라운드 1 Critical-2 hand-off)** `RefreshSpecializedDigestAsync` / `ApplyPendingSpecializedDigest` / `SetActiveCollectionSourceRoots` 의 production wiring (`Initialize.cs` 의 ConfigureProviderAsync hook + `KbProfile.cs` SSE invalidate path + chat panel open trigger) 도 본 backlog 와 함께 후속 PR. PR-I5 시점에는 `SpecializedDigestInjectionTests` + headless smoke 만 본 진입점 호출, production GUI 진입점 wiring 부재 (관련 docstring 박제 — `LlmChatViewModel.SpecializedDigest.cs`).

### 잔여 backlog (Phase P2~P4)
- §1 일정 표 + §2 PR 표 참조

---

## 1. Phase P1~P4 일정 (전체)

`documents-based-gfm.md` §0 의 시나리오 A+++++ 채택 전제 하의 구현 일정:

| Phase | 명칭 | 기간 | 산출물 SSOT |
|---|---|---|---|
| **P1** | xlsx/pdf strategy + 박제 + 주입 | 1.5~2주 | PR-I1 ~ PR-I5 (§2) |
| **P2** | Promaker YAML 매퍼 + 도메인 전문가 검수 후 golden 박제 | 0.5~1주 | §5 산출물 |
| **P3** | 다른 라인 확장 (FLR / BB / CRP / ROOF / BC) | 라인 당 0.5주 | line 별 strategy 변형 |
| **P4** | image caption strategy 도입 + KMM 매뉴얼 prior-art 활용 | 1주 | PR-I6 + PR-I7 (자율) |

전체 wall-clock 합 = P1 + P2 + P4 = **3~4주** + P3 (라인 N 개당 0.5N 주, m3 — P3 제외
사유 = 다른 라인 자료 외부 입수 의존이며 N 사전 미정).

**strategy 코드 자체** 의 추정치는 약 2~2.5주 (§3 합계 표 참조) — 위 합산의 P1
+ P4 일부에 해당. 매퍼 / 검수 / 라인 확장은 별도.

### 1.1 Phase ↔ PR 매핑 + 진행 체크박스

| Phase | 범위 | 자동 진행 | 시작 | 종료 |
|---|---|---|:---:|:---:|
| **P1** | PR-I1 + PR-I2 + PR-I2.5 + PR-I3 + PR-I4 + PR-I5 | ✅ 자동 | [x] | [x] |
| **P2** | YAML 매퍼 + 도메인 전문가 검수 + golden 박제 | ❌ human-in-loop | [ ] | [ ] |
| **P3** | 다른 라인 (FLR/BB/CRP/ROOF/BC) signature 매치 + variant 추가 | ❌ 자료 외부 입수 의존 | [ ] | [ ] |
| **P4** | PR-I6 + PR-I7 (자율) | ❌ spot-check / A/B 사람 평가 | [ ] | [ ] |

`PR-I3` (TextDumper hook + Packager whitelist) = **P1 내부** (strategy 코드 박제와 인프라 hook 이 모두 P1).
`PR-I5` (Promaker WPF) = **P1 종료 시점** — P1 의 마지막 PR. 통과 시 P2 hand-off.
P1 의 strategy 코드 (2~2.5주 추정) 와 P4 의 caption strategy (1주 추정) 는 **코드 영역 직교**
→ 평행 가능. 단 P4 의 운용 진입은 P1~P3 검증 후.

### 1.2 자동 진행 중단점 (human-in-loop)

| 진행 위치 | 중단 trigger | 사용자 액션 |
|---|---|---|
| **PR-I5 통과 직후 (P1 종료)** | orchestrator 가 §4.1 의 6 체크박스 전부 통과 보고 → P2 진입 시점 | 도메인 전문가 검수 (§5.1) + golden 박제 (§5.2) 수동 진행 |
| **PR-I6 진입 시점 (P4 의 strategy 코드 완료 후)** | spot-check (§4.2) 의 35장 human eval 의무 | 사용자가 각 kind 5장 × 7 = 35장 평가, 합격/불합격 통보 |
| **PR-I7 진입 시점 (PR-I6 합격 후)** | KMM A/B test (§4.3) 의 control vs treatment 5회씩 × 3 지표 평가 | 사용자가 결과 비교 + 도메인 전문가 검수 + 합격 통보 |
| **sister 자료 추가 입수 시점** | `04.SIDE COMPL LINE` 등 추가 입수 알림 | 별 turn 에서 사용자가 자료 위치 + strategy 변형 결정 (PLC Ladder 는 입수 가정 없음 — 본 PoC 범위 외) |

---

## 2. PR 분할 (PR-I1 ~ PR-I7) — `light-house-summary` branch 확장

| PR | scope | 산출물 | 진입 조건 | 종료 조건 (정량) |
|---|---|---|---|---|
| **PR-I1** | strategy interface (`IXlsxStrategy` + `IPdfStrategy`) + `XlsxSignatureClassifier` + `IoListStrategy` 단독 | 자료 A 의 summary 박제 e2e | — (P1 진입) | §4.1 의 [1][2][3] 부분 만족 — `core/.lighthouse-kb/summary/광명2_iolist.md` 박제 + 머리말 5행 + 표 컬럼 정합 (xlsx 1건) |
| **PR-I2** | `WorkOrderStrategy` + `PdfControlSpecStrategy` 추가 (interface 확장 0, classifier 의 strategy list 에 등록만) | 자료 B/C summary 박제 | PR-I1 종료 조건 통과 | §4.1 의 [1][2][3] 만족 (xlsx 3건 전부 + pdf 1건) |
| **PR-I3** | `TextDumper.fs` 의 `summary/` dir 박제 hook + `Packager.fs` zip whitelist + `runIndex` dump 진입점 | 색인/upload e2e (CLI level) | PR-I2 종료 조건 통과 | `core/.lighthouse-kb/summary/` 디렉토리 박제 + server upload 후 zip 안 동봉 확인 |
| **PR-I4** | `SpecializedDigestBuilder` + `SystemContentBuilder.cs` cache breakpoint 3 + Anthropic wire 검증 | system prompt 주입 | PR-I3 종료 조건 통과 | first turn 의 system prompt 안 3개 markdown 합본 박제 + 둘째 turn 의 `cache_read_input_tokens` ~38K 확인 |
| **PR-I5** | Promaker WPF — `KbSpecializedDigestFetcher.cs` + `LlmChatViewModel.SpecializedDigest.cs` (partial) + 회귀 테스트 | 사용자 e2e | PR-I4 종료 조건 통과 | §4.1 의 [4][5][6] 만족 — 사용자가 "S204 KEY 지그 YAML 초안" 요청 → 정상 출력 + `attachment_fulltext` 자율 호출 확인 |
| **PR-I6** | `CaptionPromptStrategy` 7 kind + `detect` signature + Opus model 차등 | image caption quality 도약 | PR-I1~I5 와 코드 영역 평행 가능, 운용 진입은 Phase P4 | §4.2 의 spot-check 35장 4/5 이상 통과 |
| **PR-I7** | (자율) `KmmManualStrategy` (pptx) — 2 step: (a) caption-only 임시 합본 산출 + A/B test 진입 → (b) A/B 합격 후 `summary/` 통합 박제 | KMM 매뉴얼 baseline 활용 | (a) PR-I6 spot-check 통과 / (b) (a) 의 A/B test 합격 (C5 해소) | A/B test 의 3 지표 모두 ≥ 5%p 향상 + 회귀 0 |

### 2.1 PR 간 dispatch 정합 규칙 (F 해소)

PR-I2/I3 가 PR-I1 의 산출물에 의존하는 정합 규칙:

- **PR-I1 의 산출물**:
  - `Solutions/Core/Ds2.LightHouse/Extractors/XlsxStrategies/IXlsxStrategy.fs` (interface)
  - `Solutions/Core/Ds2.LightHouse/Extractors/XlsxStrategies/XlsxSignatureClassifier.fs` (진입점 + strategy list — PR-I1 시점엔 `IoListStrategy` 만 등록)
  - `Solutions/Core/Ds2.LightHouse/Extractors/XlsxStrategies/IoListStrategy.fs`
- **PR-I2 의 변경 가능 영역**:
  - `Solutions/Core/Ds2.LightHouse/Extractors/XlsxStrategies/WorkOrderStrategy.fs` (신규)
  - `Solutions/Core/Ds2.LightHouse/PdfStrategies/PdfControlSpecStrategy.fs` (신규)
  - `XlsxSignatureClassifier.fs` 의 strategy list **append 1줄** + `PdfSignatureClassifier.fs` (pdf 진입점) 신규
  - **금지**: `IXlsxStrategy.fs` 의 interface signature 변경, `IoListStrategy.fs` 의 출력 결과 변경
- **PR-I3 의 변경 가능 영역**:
  - `Solutions/Core/Ds2.LightHouse/TextDumper.fs` 의 `dumpAll` 함수에 strategy 분기 추가 (~50 줄)
  - `Solutions/Tools/Ds2.LightHouse.Cli/Packager.fs` 의 zip whitelist 에 `summary/` 추가 (~5 줄)
  - `Solutions/Tools/Ds2.LightHouse.Cli/Program.fs` 의 `runIndex` 에 dump hook 진입점 (~20 줄)
  - **금지**: strategy 코드 영역, classifier 코드 영역 (PR-I1/I2 와 동시 변경 시 conflict)
- **PR-I4 의 변경 가능 영역** (M2):
  - `Solutions/Core/Ds2.LightHouse/SpecializedDigestBuilder.fs` (신규)
  - `Apps/Shared/Llm.Shared/Api/SystemContentBuilder.cs` 의 cache breakpoint 3 추가 (~20 줄)
  - **금지**: strategy 코드 영역 (PR-I1/I2) / TextDumper / Packager / runIndex (PR-I3) — 모두 PR-I3 의 산출물에 의존하지만 변경 0
- **PR-I5 의 변경 가능 영역**:
  - `Apps/Promaker/Promaker/Knowledge/KbSpecializedDigestFetcher.cs` (신규)
  - `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.SpecializedDigest.cs` (신규 partial)
  - `Solutions/Tests/Promaker.Tests/SpecializedDigestInjectionTests.cs` (신규, headless smoke)
  - **금지**: lib (Solutions/Core/Ds2.LightHouse/) / CLI / service / SystemContentBuilder 본체 — 모두 호출만
- **PR-I6 의 변경 가능 영역**:
  - `Solutions/Core/Ds2.LightHouse/CaptionGenerator.fs` 의 helper 분리 (~50 줄, callAnthropic wire 변경 0)
  - `Solutions/Core/Ds2.LightHouse/CaptionPromptStrategy.fs` (신규)
  - `Solutions/Tools/Ds2.LightHouse.Cli/Program.fs` 의 `print-caption-prompt --kind <Kind>` 분기 (~20 줄)
  - `Solutions/Tools/Ds2.LightHouseService/` 의 Indexer 흐름 — image 처리 시 detect → promptFor → callAnthropic (~30 줄)
  - **금지**: 기존 strategy 코드 영역 (XlsxStrategies/, PdfStrategies/) 변경 0 / callAnthropic 의 wire 변경 0
- **PR-I7 의 변경 가능 영역**:
  - `Solutions/Core/Ds2.LightHouse/PptxStrategies/KmmManualStrategy.fs` (신규)
  - `XlsxSignatureClassifier.fs` 의 pptx 분기 또는 별 `PptxSignatureClassifier.fs` (신규)
  - **금지**: 기존 strategy 코드 영역 / caption strategy (PR-I6) 변경 0 / summary 통합 박제는 A/B test 통과 후 step (b) 에서만

### 2.2 외부 평행 / 충돌
- **PR-H** (`todo-lighthouse-index-summary.md`) 의 (D) doc summary 1줄 박제 (`summary.md`
  단일 파일) 와 본 (E) specialized digest 박제 (`summary/` 디렉토리) 는 **디렉토리 분리
  → 충돌 0**. PR-H2 (subagent batch) 후 또는 평행 진행 가능.

---

## 3. 구현 위치 + 코드량

### 3.1 §8.5 strategy 코드 (Phase P1)

```
Solutions/Core/Ds2.LightHouse/
├── TextDumper.fs                          ← summary/ dir 박제 추가 (~50 줄)
├── SpecializedDigestBuilder.fs            ← 신규 (~80 줄)
├── Diagnostics/                           ← 신규 폴더 (M11 — N6 schema 박제 위치)
│   └── DiagnosticSchemas.fs               ← 신규 (~60 줄, PR-I1 시점)
│                                            type RejectedEntry, NearMissEntry, StaleEntry
│                                            + Newtonsoft.Json serialize/deserialize
├── Extractors/
│   ├── OoxmlExtractor.fs                  ← 기존 (XlsxSheetRoles SSOT 재사용)
│   ├── PdfExtractor.fs                    ← 기존 (r3 `f88e60c4` CJK 띄어쓰기 fix 포함)
│   └── XlsxStrategies/                    ← 신규 폴더
│       ├── IXlsxStrategy.fs               ← interface (~30 줄)
│       ├── XlsxSignatureClassifier.fs     ← 진입점 (~80 줄)
│       ├── IoListStrategy.fs              ← 4-block reshape (~150 줄)
│       └── WorkOrderStrategy.fs           ← Gantt 매핑 (~80 줄, XlsxSheetRoles 재사용)
└── PdfStrategies/                         ← 신규 폴더
    └── PdfControlSpecStrategy.fs          ← 광명2 PDF (~120 줄)

Solutions/Tools/Ds2.LightHouse.Cli/
└── Program.fs                             ← runIndex dump hook 확장 (~20 줄)

Apps/Promaker/Promaker/Knowledge/
└── KbSpecializedDigestFetcher.cs          ← 신규 (~80 줄, PR-F KbProfileExtractor 패턴)

Apps/Promaker/Promaker/ViewModels/
└── LlmChatViewModel.SpecializedDigest.cs  ← 신규 partial (~80 줄, PR-G KbDigest 패턴)

Apps/Shared/Llm.Shared/Api/
└── SystemContentBuilder.cs                ← 갱신 (~20 줄, cache breakpoint 추가, Llm.Shared 마이그레이션 결과)

Tests/
├── Ds2.LightHouse.Tests/
│   └── XlsxStrategiesTests.fs             ← signature 분류 + 변환 회귀 (~300 줄)
├── Ds2.LightHouseService.Tests/
│   └── SummaryDirIntegrationTests.fs      ← upload/download e2e (~150 줄)
└── Promaker.Tests/
    └── SpecializedDigestInjectionTests.cs ← 주입 회귀 (~100 줄)
```

### 3.2 §8.6 image caption strategy 추가 (Phase P4)

```
Solutions/Core/Ds2.LightHouse/
├── CaptionGenerator.fs               ← Generic prompt 유지 + helper 분리 (~50 줄 변경)
└── CaptionPromptStrategy.fs          ← 신규 (~200 줄)
    ├── CaptionPromptKind DU 8 case   ← Generic / FlowChart / InterlockDiagram / SystemTopology /
    │                                   LayoutFloorPlan / HmiScreenshot / EquipmentPhoto / TableSnapshot
    │                                   (M3 — Generic + EquipmentPhoto 둘이 fallback 군. spot-check 35 = 7 kind × 5 장 — Generic 제외)
    ├── detect (slideContext, imageHash) → Kind
    ├── promptFor (Kind) → string + maxTokens
    └── modelFor (Kind) → string      ← env lookup + literal fallback (M1, M14 정합):
                                        `Environment.GetEnvironmentVariable("LIGHTHOUSE_CAPTION_MODEL_PRIMARY")` 우선,
                                        부재 시 kind 별 literal default ("claude-opus-4-7" for FlowChart/Interlock/Topology,
                                        나머지 "claude-sonnet-4-6"). 실패 시 fallback chain (`opus-4-7 → sonnet-4-6 → sonnet-4-5`)

Solutions/Tools/Ds2.LightHouse.Cli/
└── Program.fs                        ← print-caption-prompt --kind <Kind> 분기 (~20 줄)

Solutions/Tools/Ds2.LightHouseService/
└── (Indexer 흐름 변경)               ← image 처리 시 detect → promptFor → callAnthropic (~30 줄)

.claude/skills/indexer/SKILL.md       ← subagent dispatch 시 kind 별 prompt fetch (~doc only)

Tests/
└── Ds2.LightHouse.Tests/
    └── CaptionPromptStrategyTests.fs ← detect signature + prompt SSOT 회귀 (~150 줄)
```

### 3.3 합계 breakdown

| 영역 | §8.5 strategy | §8.6 caption | 합 |
|---|---|---|---|
| **lib** (Solutions/Core/Ds2.LightHouse/) | 590 (TextDumper 50 + SpecializedDigestBuilder 80 + interface 30 + classifier 80 + IoList 150 + WorkOrder 80 + PdfControlSpec 120) | 250 (CaptionGenerator 변경 50 + CaptionPromptStrategy 신규 200) | **840** |
| **UI** (Apps/Promaker/Promaker/) | 180 (KbFetcher 80 + ChatVM partial 80 + SystemContentBuilder 20) | 0 | **180** |
| **tests** | 550 (xlsx 300 + summary 150 + injection 100) | 150 (CaptionPromptStrategyTests) | **700** |
| **misc** (CLI / service) | 20 (runIndex hook) | 50 (CLI print-prompt 20 + Indexer flow 30) | **70** |
| **합계** | **1340** | **450** | **1790** |

> **모델 ID hardcode 회피 권고** — `claude-opus-4-7` / `claude-sonnet-4-6` 는
> `settings.json` 또는 `LIGHTHOUSE_CAPTION_MODEL_*` env 외부화 + fallback chain
> (`opus-4-7 → sonnet-4-6 → sonnet-4-5`) 정책. 모델 deprecation cycle 12~18 개월
> 로 stale 빠름 — PR-I6 진입 시 함께 작업.

---

## 4. 검증 시나리오

### 4.1 §8.5 strategy 검증 (PR-I1 ~ PR-I5 의 e2e)

색인 대상 = `F:/Git/dualsoft/secrets/KBSamples/core/`:

```
F:/Git/dualsoft/secrets/KBSamples/core/
├── 4-3. 광명2 SIDE OUTER SV IO LIST STD MAP.xlsx   ← 자료 A (IoListStrategy 발동)
├── 3.광명2_전동화공장_제어시스템(HMI편집됨).pdf      ← 자료 B (PdfControlSpecStrategy 발동)
├── 4-1. SV_SIDE_조립작업서_240328.xlsx              ← 자료 C (WorkOrderStrategy 발동)
└── (후속 추가 자료 — 사양관련 기타 PDF / 작업표준서 docx / 기타 메모 .md 등은
     signature 미매치 → summary/ 박제 0, 기존 (B) text dump 와 (A) keyword digest 로 흡수)
```

색인 후 검증 항목:
- [ ] `F:/Git/dualsoft/secrets/KBSamples/core/.lighthouse-kb/summary/` 디렉토리에
  **정확히 3개 파일** (자료 A/B/C 의 specialized markdown) 만 박제
- [ ] 각 파일 머리말의 strategy version 박제 확인 (예: `<!-- generated by
  IoListStrategy v1.0 -->`)
- [ ] KB collection 활성화 후 Promaker chat 시작 → first turn 의 system prompt 에
  3개 markdown 합본 inject 박제 확인
- [ ] 둘째 turn 의 Anthropic response 에서 `cache_read_input_tokens` **30K ~ 50K range** 박제 확인 (M6 — ~38K 는 광명2 자료 추정치, range 벗어남 자체는 cache 정상 동작 보장이라 fail 처리 X)
- [ ] 사용자가 "S204 KEY 지그의 YAML 초안" 요청 → `documents-based-gfm.md` §4.4 시연
  같은 YAML 출력 확인
- [ ] LLM 이 정확 IO 비트 dump 필요 시 `attachment_fulltext` 자율 호출 확인

### 4.2 §8.6 Caption strategy 검증 (PR-I6)

별도 검증 폴더 = `F:/Git/dualsoft/secrets/KBSamples/sisters/` (KMM 매뉴얼 1 파일만 색인,
core 와 collection 분리):

- [ ] detect 결과 분포 확인 — 76 slides × ~1.5 img 중 FlowChart 5+ / InterlockDiagram
  6+ / SystemTopology 2+ / LayoutFloorPlan 6+ / HmiScreenshot 20+ / TableSnapshot 4+
  / 나머지 Generic
  (m7 — 본 분포는 광명2 PoC 분석 turn 의 python `python-pptx` 추출 결과 + 슬라이드 제목
  키워드 매핑 추정. PR-I6 진입 시 실측 detect 결과로 재확인 의무)
- [ ] Opus 차등 — FlowChart / InterlockDiagram / SystemTopology 호출 model
  `claude-opus-4-7` 확인, 나머지 Sonnet 4.6
- [ ] **spot-check** (`documents-based-gfm.md` §8.6.4 갱신본 기준) — 각 kind 5장 × 7
  = 35장 human eval:
  - FlowChart: edge `A → B` 가 도식과 정합 4/5 이상
  - InterlockDiagram: signal source/target 라벨 정합 4/5 이상
  - SystemTopology: 통신 protocol 라벨 정합 4/5 이상
  - 나머지: legacy 동등성 4/5 이상
- [ ] 합격 후 자율 PR-I7 진입 — KmmManualStrategy 박제 + `.lighthouse-kb/summary/`
  통합

### 4.3 KMM caption A/B test (PR-I7 진입 조건)

KMM 매뉴얼 caption 을 광명2 모델 생성에 inject 하는 것이 noise 가 아닌지 사전 검증.

**실험 설계**:
- 샘플 task: 광명2 #204 (KEY 지그) zone 의 Active YAML 생성. LLM 입력 = 자료 A/B/C
  의 `.lighthouse-kb/summary/*.md` (고정).
- **조건 A** (control): KMM caption 미주입.
- **조건 B** (treatment): KMM 매뉴얼의 FlowChart/InterlockDiagram caption 동봉 주입.
- 각 조건 5회 반복 실행 (LLM stochastic 흡수).

**평가 지표**:
- [ ] Arrow Type 정확도 (golden vs LLM 출력의 Start/Reset/StartReset/ResetReset 일치율)
- [ ] callCondition 트리 정합 (도메인 전문가 검수)
- [ ] YAML validate 통과율 (`ValidateModelDoc`)

**합격 기준**: 조건 B 가 모든 지표에서 조건 A 보다 ≥ 5%p 향상 + 어느 지표도
회귀 0. 미합격 시 KmmManualStrategy 박제 보류 또는 prompt 재설계.

> **m4 hedge**: ≥ 5%p 와 n=5 는 **PoC heuristic** — n=5 의 통계적 유의성 한계 (표준편차
> 미관측) 인정. 본 PoC 한정 합격 기준, 운영 진입 전 n 상향 또는 별 통계 검정 도입 결정.

---

## 5. 도메인 전문가 검수 (Phase P2)

`documents-based-gfm.md` §0 의 NDA / 외부 LLM 전송 동의 정책과 정합.

### 5.1 검수 의무

광명2 #204 zone YAML 출력을 golden 으로 KB 색인하기 **전에**, 자료 B 작성처 (HKMC
설비제어기술2팀) 또는 광명2 SV 라인 운영자의 검수 1회 통과 필수.

**이유**: LLM 생성물을 검수 없이 golden 박제하면 **첫 오류가 영구화** 되어 이후
strategy 개선의 회귀 baseline 자체가 오염됨.

### 5.2 검수 산출물

- [ ] 광명2 #204 YAML 초안 (LLM 출력 그대로)
- [ ] 도메인 전문가의 검수 의견 + 보정 사항 (한국어 노트)
- [ ] 보정 적용된 final YAML (`광명2_204_golden.yaml`)
- [ ] KB 색인 (golden baseline collection 으로 별도 등록)
- [ ] 후속 strategy 개선 시 회귀 검증 — 동일 입력 → 동일 LLM 으로 동일 출력
  (cache hit, deterministic verification)

### 5.3 검수 후 작업

- [ ] golden 의 차이점 분석 → strategy SSOT (`documents-based-gfm.md` §4 매핑 규칙
  + §8.5 strategy 카탈로그) 에 fix 반영
- [ ] 차이 패턴이 광명2 한정인지 HKMC 전사 패턴인지 판단 → strategy 코드 또는
  prompt 갱신 위치 결정

### 5.4 P2 hand-off 의 사용자 액션 시퀀스 (M7 — orchestrator hand-off 직후)

orchestrator 가 PR-I5 commit + headless smoke 통과 보고 후 **사용자 (모델 작성자)** 가
직접 수행하는 단계:

| step | 액션 | 소요 |
|---|---|---|
| 1 | `광명2_204_draft.yaml` 생성 — Promaker GUI 또는 chat 으로 PR-I5 의 인프라 활용 | ~30분 |
| 2 | 검수 의뢰 발송 — HKMC 설비제어기술2팀 또는 광명2 SV 라인 운영자에게 draft + 검수 요청 한국어 노트 송부 | (외부 응답 대기) |
| 3 | 검수 의견 수령 — 보정 사항 한국어 노트로 회신 받음 | (외부) |
| 4 | 보정 적용 — `광명2_204_golden.yaml` 작성 (수동 Patch 또는 GUI 편집) | ~30분 |
| 5 | golden 박제 — `F:/Git/dualsoft/secrets/KBSamples/golden/광명2_204_golden.yaml` 위치 (신규 디렉토리) + KB 색인 (별 collection `광명2_golden`) | ~10분 |
| 6 | 후속 strategy 개선 시 회귀 baseline — 동일 입력 → 동일 LLM 으로 동일 출력 (cache hit) verification | (PR-I 진행 시) |

→ orchestrator 가 P1 종료 보고 시 위 6 step 의 체크리스트 + 검수자 연락처 placeholder
를 함께 출력 (사용자가 다음 액션 즉시 시작 가능).

---

## 6. 메타리뷰 후속 결정 항목

`documents-based-gfm.md` 의 5인 메타리뷰 (Critical 7 + Major 27 + Minor + Outlier)
중 **부분 채택 / 보류** 항목들의 후속 결정 시점.

| ID | 항목 | 결정 시점 | **default 결정 (자동 진행 채택, 사용자 turn override 가능)** |
|---|---|---|---|
| **M5** | 자료 B/C 외부 절대경로 → fixture 단일 머신 종속 | NDA 정합 확인 후 | **fixture commit 보류** — secrets repo clone 전제로 진행, PoC 외부 공유 시점에 별 작업 |
| **M7** | Prompt cache TTL (5분/1h) / cost / 1M context premium 가정 검증 | PR-I4 진입 시 | **TTL=5분 default + cache write 1.25x cost 가정** — `done-promaker-llm-roundtrip-optimization.md` 실측치 있으면 그것 우선 |
| **M11** | Stale 추적 (source mtime / content hash) 메커니즘 | 후속 PR backlog | **cross-ref-hash 박제만 활성** (`documents-based-gfm.md` §8.5.5 footer) — 자동 stale 감지는 P3 이후 backlog |
| **M12** | IO List layout magic constant 단일 라인 종속 | Phase P3 진입 시 | **광명2 한정 4-block 가정** — Phase P3 에서 header row auto-detect 추가 |
| **M13** | PDF text-layer 의존, OCR fallback 미정의 | 후속 PR backlog | **PowerPoint export 가정 (text-layer 보유)** — 스캔본 진입 시 backlog |
| **M14** | 모델 ID hardcode (claude-opus-4-7 / claude-sonnet-4-6) | PR-I6 진입 시 | **PR-I6 turn 에 `LIGHTHOUSE_CAPTION_MODEL_PRIMARY` / `_FALLBACK` env 박제 + lib literal fallback chain (`opus-4-7 → sonnet-4-6 → sonnet-4-5`)** |
| **M15** | `light-house-summary` branch 정책 | PR-I1 진입 turn | **`light-house-summary` branch 누적 + 최종 squash merge to `light-house`** (A4 해소) |
| **M22** | PDF P89-104 페이지 인용 검증 | PR-I2 진입 시 | **PR-I2 turn 의 첫 PdfControlSpecStrategy 산출물 spot-check 1회** (5 페이지 정합 확인) — orchestrator 가 자동 실행 |
| **M26** | Symbol SSOT 광명2 한정 vs HKMC 전사 표준 | Phase P3 진입 시 | **광명2 한정 가정** — P3 에서 다른 라인 자료 비교로 판정 |

### 6.0 default 결정의 적용 범위 (m8)

§6 메타리뷰 + §6.1 N1~N7 의 "default 결정" 컬럼은 **자동 진행 범위 (§0 = PR-I1~I5) 안의
박제 신호** 가 default. 자동 진행 범위 외 (PR-I6 이후 / Phase P3) 의 default 는
**self-driven 가능한 시점에 orchestrator 가 자동 채택, hand-off 후의 사용자 액션은
override 우선**. 즉:

- PR-I1~I5 turn: default 자동 채택, 사용자가 동일 turn 안에서 override 입력 시 그것 우선
- PR-I6 / PR-I7 / P2~P3 turn: 사용자 confirm 필수, default 는 "사용자 미응답 시 채택" 의
  최후 신호

### 6.1 누락 점검 (2026-05-27 turn) 추가 backlog

`documents-based-gfm.md` §8.5.5~§8.5.8 박제 후 잔여 운영 측면 결정 항목:

| ID | 항목 | 결정 시점 | **default 결정 (자동 진행 채택, 사용자 turn override 가능)** |
|---|---|---|---|
| **N1** | error handling / partial failure | PR-I1 진입 시 | **부분 산출물 박제 + 실패한 시트만 `.lighthouse-kb/rejected.json` 박제** — strategy catch 후 부분 markdown 박제 (실패 시트 skip + footer warning) + rejected.json 에 `{file, sheet, reason}` |
| **N2** | user feedback loop | Phase P2 진입 후 | **golden 과 LLM 출력의 diff 를 strategy 개선 회귀 신호로 흡수** — Phase P2 의 hand-off 시점에 별도 결정 (자동 진행 범위 외) |
| **N3** | caption 두 모델 모두 실패 시 fallback | PR-I6 진입 시 | **Opus → Sonnet → SkippedCaption + caption=NULL** — 외부 todo `todo-lighthouse-indexer-claude-caption.md` §2 #10 의 per-image max 2 attempts 정합. retry queue 별도 박제 안 함 (다음 색인 시 NULL row 자동 재시도) |
| **N4** | strategy near-miss 사용자 진단 path | PR-I1 진입 시 | **`.lighthouse-kb/near-miss.json` 박제만 활성** — Promaker UI manual override 입력 path 는 Phase P3 backlog |
| **N5** | summary markdown 256KB cap 초과 시 split 또는 truncate | Phase P3 진입 시 | **3단계 압축 (컬럼 truncate → sample → split) 정책 박제** (`documents-based-gfm.md` §8.5.5) — P3 진입 시 실측 |
| **N6** | `.lighthouse-kb/{rejected, near-miss, stale}.json` schema 박제 | PR-I1 ~ PR-I3 진입 시 | **JSON schema PR-I1 시점 박제 (rejected + near-miss)**, **stale.json 은 후속 PR backlog** (M11 정합) |
| **N7** | strategy MAJOR 갱신 시 재색인 trigger UX | Phase P3 진입 시 | **`lighthouse-cli reindex --strategy <name>` CLI 진입점만 박제** — Promaker UI 자동 trigger 는 P3 backlog |
| **N8** | specialized digest production GUI wiring 활성화 (`RefreshSpecializedDigestAsync` / `ApplyPendingSpecializedDigest` / `SetActiveCollectionSourceRoots` 의 production 진입점 박제) — PR-I5 의 `--review` 라운드 1 Critical-2 hand-off 정합. 미박제 시 production chat 의 system prompt cache breakpoint 3 영구 skip (test only). | P1 종료 직후 별 PR (PR-I5 후속, P2 검수 진행과 평행 가능 — wiring 작업과 도메인 검수 시퀀스 독립) | **별 PR 1건 — wiring 3-point patch**: (a) `LlmChatViewModel.Initialize.cs` 의 `InitializeAsync` 안 `_ = RefreshKbDigestAsync();` 옆에 `_ = RefreshSpecializedDigestAsync();` 추가, (b) `ConfigureProviderAsync` 의 `ApplyPendingKbDigest()` 옆에 `ApplyPendingSpecializedDigest()` 추가, (c) `LlmChatViewModel.KbProfile.cs` 의 `RefreshKbDigestAsync` 안 `ApplyPendingKbDigest()` 다음에 `ApplyPendingSpecializedDigest()` 동반 호출 — SSE `collection-*` invalidate 시 함께 갱신. 코드 영역 = `LlmChatViewModel.Initialize.cs` / `LlmChatViewModel.KbProfile.cs` 한정 (lib / CLI / service / `SystemContentBuilder` 무관 — 모두 호출만). 회귀 fact = `SpecializedDigestInjectionTests.cs` 에 production hook 진입 1회 확인 fact 추가. |

---

## 7. 차후 결정 포인트

PoC 진행 후 별도 turn 에서 결정 권장:

- **다른 차체공장 / 다른 OEM 자료 형식** — signature 기반이라 영향 0, strategy
  1세트 추가
- **PR-H2 (subagent batch summary)** 와 본 (E) layer 의 정보 영역 분리 — (D) =
  doc 1줄, (E) = 정제 markdown 수십K. 충돌 0
- **caption prompt SSOT 갱신** — `todo-lighthouse-indexer-claude-caption.md` 의
  "단일 CaptionPrompt SSOT 정책" (외부 §2 #5) 과 "spot-check 합격 기준" (외부
  §2 #14) 두 항목을 `documents-based-gfm.md` §8.6.4 갱신본으로 patch. patch 시점
  = PR-I6 진입 turn 의 commit message + 외부 todo 문서의 rev 라인 stamp 추가
- **HKMC NDA / 외부 LLM 전송 동의** — 운영 진입 전 별 문서로 박제
- **sister 자료 추가 입수 시** — `04.SIDE COMPL LINE_KMM 라인 운영 매뉴얼` (13MB,
  현재 secrets 미이전) + 다른 OEM 라인 매뉴얼 → `KBSamples/sisters/` 에 추가 +
  §10 참고 표 갱신

---

## 8. 사용자 액션 시퀀스 (참고 — `documents-based-gfm.md` §5 의 todo 부분)

PoC 진입 후 사용자가 실제로 수행하는 액션 시퀀스. 시스템 흐름은
`documents-based-gfm.md` §5 / §8 참조.

### Step 0 — 자료 정렬 (1회, 색인 전)

- [ ] `광명2_xref.json` 작성 — 자료 A/B/C 의 zone × pdf_page × workorder_sheet
  × expected_io 매핑. 산출물 예시 = `documents-based-gfm.md` §5 Step 0 참조
- [ ] (선택) `광명2_xref.json` 을 KB 색인 폴더에 같이 두어 LLM retrieval 시 cross-check
  보조

### Step 1 — LightHouse 폴더 등록 + 색인 (1회)

- [ ] Promaker 의 KB 관리 다이얼로그에서 `F:/Git/dualsoft/secrets/KBSamples/core/`
  를 collection 으로 등록
- [ ] 색인 완료 확인 — `.lighthouse-kb/summary/` 의 3개 파일 박제 (§4.1)

### Step 2 — chat 시작 (사용자 N회)

- [ ] Promaker LLM Chat panel open → 자동 KB digest fetch + specialized digest 주입
  박제 확인 (§4.1 의 cache_read 검증과 동일)

### Step 3 — DS 모델 초안 생성 (1 turn)

- [ ] LLM 에 "광명2 #204 의 KEY 지그를 Promaker YAML 로 변환해줘" 요청
- [ ] LLM 출력 YAML 을 GUI 에 로딩 (`ApplyModelDoc`)
- [ ] `ValidateModelDoc` 통과 확인

### Step 4 — 사용자 검토 + iteration

- [ ] GUI 에서 도메인 전문가 검수 (§5)
- [ ] 보정 결과를 다음 turn 에 입력 → 갱신 YAML 출력
- [ ] golden 박제 → KB 재색인 → 후속 strategy 개선 baseline

---

## 9. 관련 문서

- `documents-based-gfm.md` — 본 todo 의 사실 / 분석 SSOT
- `todo-lighthouse-index-summary.md` — LightHouse 4-layer RAG (A keyword / B text
  dump / C chunk / D doc summary) + PR-H summary 정책
- `todo-lighthouse-indexer-claude-caption.md` — `/indexer` skill subagent caption
  위임. 본 todo §6 의 M14 / `documents-based-gfm.md` §8.6.4 의 SSOT 갱신 대상
- `yaml-protocol-v0.md` — Promaker YAML v0 schema SSOT
- `howto-connect-lighthouse-service.md` — LightHouse Service 설치 + Promaker 연결

---

## 10. sub agent prompt 골격 (자동 진행 표준화)

`--orchestrate` 가 매 PR turn 에서 사용. 작업 / 검열 두 종류.

### 10.1 작업 agent (구현)

```
<task>
PR-I{N} 구현: {scope 한 줄}

## 입력
- 본 todo 의 §2 PR-I{N} 행의 scope + 산출물 + 종료 조건
- `documents-based-gfm.md` 의 관련 절 (strategy 정의는 §8.5, caption 은 §8.6, summary 포맷은 §8.5.5)
- §3 구현 위치 트리의 해당 파일 (신규/변경 대상)

## 작업 영역 (정확히 이것만 — F 해소)
- {PR 별 변경 가능 파일 목록 — §2.1 dispatch 정합 규칙 의 "변경 가능 영역" 인용}

## 금지 (공통)
- 위 영역 외 파일 수정 0
- git commit / push / stage (git add) 직접 호출 0 — main 이 §10.3 step 6.5 에서 stage + step 7 에서 commit
- 외부 LLM API 직접 호출 0 (Anthropic key 박제 X — caption 은 별 path, §10.1 의 보고에 model 만 명시)
- todo-documents-based-gfm.md / documents-based-gfm.md 수정 0

## 금지 (PR-I{N} 추가 — §2.1 인용)
{PR 별 금지 항목 — §2.1 의 "금지" 컬럼 명시적 복붙. placeholder:}
- {PR-I1: signature classifier 의 strategy list 변경 0 (PR-I2 영역) / strategy interface 외 dispatch hook 변경 0 (PR-I3 영역)}
- {PR-I2: IXlsxStrategy.fs interface signature 변경 0 / IoListStrategy.fs 의 출력 결과 변경 0}
- {PR-I3: strategy 코드 영역 (XlsxStrategies/ + PdfStrategies/) 수정 0 / classifier 코드 영역 수정 0}
- {PR-I4: strategy 코드 영역 + TextDumper.fs + Packager.fs + runIndex 수정 0 (PR-I1~I3 영역)}
- {PR-I5: lib (Solutions/Core/Ds2.LightHouse/) 수정 0 / CLI (Solutions/Tools/Ds2.LightHouse.Cli/) 수정 0 / service (Ds2.LightHouseService) 수정 0}
- {PR-I6: 기존 strategy 코드 영역 수정 0 / CaptionGenerator.fs 의 callAnthropic wire 변경 0 (helper 분리만)}
- {PR-I7: 기존 strategy 코드 영역 수정 0 / KMM caption-only 임시 합본 외 summary/ 통합은 A/B test 통과 후}

## 종료 조건
- §2 의 PR-I{N} "종료 조건 (정량)" 컬럼의 모든 항목 만족
- §11 의 빌드 / 테스트 명령 통과
- 자가 검열 (CLAUDE.md trigger ①~⑤ 충족 시) 보고서 첨부

## 보고 형식 (마지막 message)
### 변경 파일 (n건)
- {path}: {line +N/-M, 신규/변경}

### 빌드 / 테스트 결과
- dotnet build: {pass/fail + warn 수}
- dotnet test: {N pass / M fail}

### 종료 조건 점검 ({n}/{m})
- [x] / [ ] 각 항목

### 잔여 우려 (없으면 "없음")
- {Critical/Major/Minor 분류}
</task>
```

### 10.2 검열 agent (review)

`Agent` 도구의 subagent_type=`code-review` 또는 `general-purpose` 사용.

```
<task>
PR-I{N} 자가 검열

## 입력
- 작업 agent 의 변경 diff (commit 안 한 unstaged + staged 양쪽)
- 본 todo §2 의 종료 조건 + §10.1 의 금지 사항 list

## 검토 항목
1. 논리 오류 / 누락 / refactoring 기회 (CLAUDE.md 자가 검열 절차 정합)
2. 작업 영역 침범 여부 (§10.1 의 작업 영역 외 변경 0 확인)
3. summary markdown 출력 시 `documents-based-gfm.md` §8.5.5 포맷 SSOT 정합
4. test 커버리지 — 신규 함수의 happy path + edge case 최소 1건씩
5. 빌드 warning 0 / dotnet test 통과 0 회귀

## 금지
- 코드 직접 수정 0 (지적만)
- git 수정 0

## 보고 형식
### Critical (차단 — fix 후 재진입)
- {파일:라인}: {이슈} → {제안}

### Major (수정 권장 — 의도 확인 후 진행)
- ...

### Minor (스타일 / 가독성)
- ...

### 잔여 우려
- ...
</task>
```

### 10.3 main (orchestrator) 의 PR turn 흐름

```
# 시작 시 1회만
0.   fail-fast 점검 (§0 의 7 항목 — §11.5 의 명령 사용)
0.1  branch 준비 — light-house-summary 부재 시 `git switch -c light-house-summary light-house` (§11.5)

for PR in [PR-I1, PR-I2, PR-I3, PR-I4, PR-I5]:
    1.   작업 agent 호출 (§10.1 의 prompt — PR 별 input + 금지 placeholder 채움)
    2.   작업 agent 산출물 받음 — 파일 변경 list + 빌드/테스트 결과
    3.   main 이 변경 파일 stage (M9 — `git add <변경 파일 list>`. 작업 agent 는 stage 안 함)
    4.   검열 agent 호출 (§10.2 의 prompt + staged diff)
    5.   Critical 0건 확인 → 미통과 시 step 1 재진입 (max 3 attempts):
          - attempts 1: 검열 보고서 + 재작업 지시
          - attempts 2: 검열 보고서 + 작업 agent 의 산출물 reset + 재시작
          - attempts 3: 검열 보고서 + 작업 agent 의 산출물 reset + 재시작
          - 3 회 초과 (M10): `git restore --staged + 작업 git stash` + orchestrator abort + 사용자 hand-off
            (Phase P{현재 PR 의 단계} 종료 보고, 다음 PR 진입 0)
    6.   종료 조건 (§2 의 PR-I{N} "종료 조건 (정량)" 컬럼) 정량 만족 확인 + §11 의 빌드/테스트 통과
    7.   git commit (branch=light-house-summary, message=[light-house-summary] PR-I{N}: ...)
    8.   다음 PR
종료: PR-I5 commit + Promaker.Tests SpecializedDigestInjectionTests.cs 통과 (headless smoke) → Phase P1 종료 보고 → 사용자 hand-off (§5.4 의 6 step 출력)
```

---

## 11. 빌드 / 테스트 / 환경 명령 SSOT

### 11.1 빌드

```bash
# Solution build (전체)
dotnet build /f/Git/ds2/light-house/Solutions/Ds2.sln -c Debug

# 개별 project build (PR-I1~I5 의 lib + tests)
dotnet build /f/Git/ds2/light-house/Solutions/Core/Ds2.LightHouse/Ds2.LightHouse.fsproj -c Debug
dotnet build /f/Git/ds2/light-house/Solutions/Tools/Ds2.LightHouse.Cli/Ds2.LightHouse.Cli.fsproj -c Debug
dotnet build /f/Git/ds2/light-house/Solutions/Tools/Ds2.LightHouseService/Ds2.LightHouseService.fsproj -c Debug
dotnet build /f/Git/ds2/light-house/Solutions/Tests/Ds2.LightHouse.Tests/Ds2.LightHouse.Tests.fsproj -c Debug
```

통과 기준: `error 0` 강제, `warning N` 박제만 (회귀는 base commit 대비 warning 증가 0).

### 11.2 테스트

```bash
# Core lib tests
dotnet test /f/Git/ds2/light-house/Solutions/Tests/Ds2.LightHouse.Tests/Ds2.LightHouse.Tests.fsproj -c Debug --logger "console;verbosity=minimal"

# Service integration tests (PR-I3/I4 이후)
dotnet test /f/Git/ds2/light-house/Solutions/Tests/Ds2.LightHouseService.Tests/ -c Debug

# Promaker WPF tests (PR-I5 이후)
dotnet test /f/Git/ds2/light-house/Apps/Promaker/Promaker.Tests/ -c Debug
```

통과 기준: `Failed = 0` 강제. base commit 대비 `Total` 감소 0 (회귀).

### 11.3 console run (PoC 검증)

```bash
cd /f/Git/ds2/light-house/Apps/Promaker

# LightHouse service console 실행 (Windows Service 정지 후)
sc stop Ds2.LightHouseService     # 별 PowerShell admin
make light-house                  # dotnet run --project ../../Solutions/Tools/Ds2.LightHouseService/Ds2.LightHouseService.fsproj -c Debug
```

### 11.4 신규 .fs 파일의 .fsproj 등록

F# 의 `.fsproj` 는 `<ItemGroup>` 안 `<Compile Include="..." />` **순서 의존** (forward
declaration 강제). 신규 파일 추가 위치:

| 신규 파일 | 등록 .fsproj/.csproj | 등록 위치 (기존 ItemGroup 안) | PR |
|---|---|---|---|
| `Diagnostics/DiagnosticSchemas.fs` | `Solutions/Core/Ds2.LightHouse/Ds2.LightHouse.fsproj` | `Models.fs` 다음 (모든 use site 보다 앞) | PR-I1 |
| `Extractors/XlsxStrategies/IXlsxStrategy.fs` | 동 | `Extractors/OoxmlExtractor.fs` 보다 **앞** (의존 역방향) | PR-I1 |
| `Extractors/XlsxStrategies/XlsxSignatureClassifier.fs` | 동 | 모든 `XlsxStrategies/*.fs` 다음, `OoxmlExtractor.fs` 보다 앞 | PR-I1 |
| `Extractors/XlsxStrategies/IoListStrategy.fs` | 동 | `IXlsxStrategy.fs` 다음, `XlsxSignatureClassifier.fs` 보다 앞 | PR-I1 |
| `Extractors/XlsxStrategies/WorkOrderStrategy.fs` | 동 | `IoListStrategy.fs` 다음, `XlsxSignatureClassifier.fs` 보다 앞 | PR-I2 |
| `PdfStrategies/PdfControlSpecStrategy.fs` | 동 | `PdfExtractor.fs` 다음 | PR-I2 |
| `SpecializedDigestBuilder.fs` | 동 | `TextDumper.fs` 다음 | PR-I4 |
| `CaptionPromptStrategy.fs` | 동 | `CaptionGenerator.fs` 다음 | PR-I6 |
| `PptxStrategies/KmmManualStrategy.fs` | 동 | `OoxmlExtractor.fs` 다음 (pptx 분기 의존) | PR-I7 |
| `Apps/Promaker/Promaker/Knowledge/KbSpecializedDigestFetcher.cs` | `Apps/Promaker/Promaker/Promaker.csproj` | 일반 C# (alphabetical 권장, 순서 무관) | PR-I5 |
| `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.SpecializedDigest.cs` | 동 | 일반 C# | PR-I5 |
| `Solutions/Tests/Ds2.LightHouse.Tests/XlsxStrategiesTests.fs` | `Solutions/Tests/Ds2.LightHouse.Tests/Ds2.LightHouse.Tests.fsproj` | 기존 *Tests.fs 다음 (alphabetical) | PR-I1~I2 |
| `Solutions/Tests/Ds2.LightHouse.Tests/CaptionPromptStrategyTests.fs` | 동 | `XlsxStrategiesTests.fs` 다음 | PR-I6 |
| `Solutions/Tests/Ds2.LightHouseService.Tests/SummaryDirIntegrationTests.fs` | 동명 Service.Tests.fsproj | 기존 *Tests.fs 다음 | PR-I3 |
| `Solutions/Tests/Promaker.Tests/SpecializedDigestInjectionTests.cs` | `Solutions/Tests/Promaker.Tests/Promaker.Tests.csproj` | 일반 C# | PR-I5 |

작업 agent 가 신규 파일 추가 시 .fsproj 의 `<Compile Include="..."/>` line 도 같이 patch
의무. 누락 시 build error → 검열 agent 가 Critical 으로 보고.

### 11.5 fail-fast / branch / 빌드 dry-run 명령 SSOT (§0 점검 7 항목과 1:1)

```bash
# git Bash 환경 (`/f/Git/...`). PowerShell 환경이면 `F:/Git/...` 또는 `F:\Git\...` 치환.

# #1 working tree clean
cd /f/Git/ds2/light-house && git status --porcelain        # 출력 0 행

# #2 branch 준비 (없으면 자동 생성 — light-house base 에서 분기)
cd /f/Git/ds2/light-house && \
  ( git branch --list light-house-summary | grep -q . || git switch -c light-house-summary light-house )
cd /f/Git/ds2/light-house && git branch --show-current     # = light-house-summary

# #3 .NET 9 SDK
dotnet --version | grep -E "^9\."

# #4 자료 A/B/C
ls /f/Git/dualsoft/secrets/KBSamples/core/ | wc -l         # ≥ 3

# #5 sister 자료 (PR-I7 진입 시만)
ls /f/Git/dualsoft/secrets/KBSamples/sisters/ | wc -l      # ≥ 1

# #6 sln 위치
test -f /f/Git/ds2/light-house/Solutions/Ds2.sln           # exit 0

# #7 service build dry-run
dotnet build /f/Git/ds2/light-house/Solutions/Tools/Ds2.LightHouseService/Ds2.LightHouseService.fsproj -c Debug --no-incremental
```
