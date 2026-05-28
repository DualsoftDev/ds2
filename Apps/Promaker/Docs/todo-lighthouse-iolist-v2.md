# IoListStrategy v2 — 광명2 IO List 박제 결함 해소 (orchestrator)

본 문서는 `--orch` SSOT. main agent 가 진행 표의 미시작 첫 Phase 부터 차례로 진행한다.

## 0. 진입 절차

```bash
# 진입 시 본 문서 1개만 read 후 작업 시작 가능.
# 광명2 IO List xlsx 와 현 KB 박제 summary 의 정량 비교는 §1 박제 SSOT 인용.

# work tree: light-house (이미 분리됨)
git -C /f/Git/ds2/light-house status -uno
```

핵심 SSOT (read 권장):

- `Solutions/Core/Ds2.LightHouse/Extractors/XlsxStrategies/IoListStrategy.fs` (311 line) — 본 작업의 1차 대상
- `Solutions/Core/Ds2.LightHouse/Extractors/OoxmlExtractor.fs` (1000 line) — segment text 박제 형식 root cause 후보
- `Solutions/Core/Ds2.LightHouse/MarkdownCapPolicy.fs` (522 line) — head/tail elide 정책
- `Solutions/Core/Ds2.LightHouse/TextDumper.fs` (382 line) — strategy summary 박제 진입
- `Solutions/Core/Ds2.LightHouse/StrategyMarkdown.fs` (140 line) — common helper
- `Solutions/Tests/Ds2.LightHouse.Tests/StrategyMarkdownTests.fs` — 기존 회귀 가드
- `Apps/Promaker/Docs/documents-based-gfm.md` §1.1 / §8.5.5 / §8.5.7 — strategy SSOT
- `Apps/Promaker/Docs/todo-documents-based-gfm.md` — PR-I 번호 부여 SSOT

자료 경로 (NDA — 외부 LLM 전송 금지):

- xlsx 원본: `/f/Git/dualsoft/secrets/KBSamples/core/4-3. 광명2 SIDE OUTER SV IO LIST STD MAP.xlsx`
- 현 KB 박제 summary: `/f/Git/dualsoft/secrets/KBSamples/core/.lighthouse-kb/summary/IoListStrategy-7c2c2034-4-3._광명2_SIDE_OUTER_SV_IO_LIST_STD_MAP.md`

## 1. 배경 — 현 KB 박제 결함 정량 진단

광명2 SIDE OUTER SV IO List (43 시트, 8.4 MB) 의 KB 박제 상태를 xlsx 실측과 비교한 결과:

| 항목 | xlsx 실측 (Phase 0 갱신) | summary 박제 | 결함 |
|---|---|---|---|
| 데이터 시트 수 | 41 (43 - COVER 51-col 표지, Sheet1 1-col 빈 시트) | 41 시트별 head 5 + tail 5 = ~10 row | — |
| 총 valid tag | **4,421** (실측 — 22-col 635 / 27-col 2,940 / 8-col 846) | 약 410 row | **박제율 ~10%** |
| Direction 분포 | IW=2,647 / QW=1,774 | `-` (전부 빈 값) | **0% 박제** |
| 시트당 데이터 row | 1016 (R6~R1021) | 10 row | 1% |
| 컬럼 매핑 | block = `[빈 col] + Word + Tag + DataType + Address + Symbol` = **6-col** | 어긋남 (Tag 자리에 IW/QW 등) | strategy 결함 |

### 1.1 시트 layout 분류 (Phase 0 진단 실측)

| layout | col 수 | 시트 수 | 시트명 | 비고 |
|---|---|---|---|---|
| **22-col 4-block × 6-col + col 22 trailing** | 22 | **23** | `MCP`, `S_OTR_PLT_S_BOX1`, `REINF_PLT_S_BOX1`, `EXTN_PLT_S_BOX1`, `S201_S_BOX1`, `S203_S_BOX1`, `S204_S_BOX1`, `S205_S_BOX1`, `S_OTR_PLT`, `S201_ANTI_JIG`, `REINF_PLT`, `S202_RENIF_ARRANGE_JIG`, `EXTN_PLT`, `S203_EXTN_ARRANGE_JIG`, `S204_KEY_JIG1`, `S204_KEY1`, `S204_KEY_JIG2`, `S205_RESPOT_JIG`, `S204_BNR_PNL`, `S204_BnR_SERVO_6-11`, `S205_FESTO_PNL`, `S205_SRV1_6-21`, `S205_SRV2_6-22` | 표준 (R5 헤더: `변수명/타입/어드레스/심볼` × 4 block, block 시작 col = 2, 7, 12, 17) |
| **27-col RB 변형 5-block × 6-col + trailing** | 27 | **14** | `S201_RB1_3-01` ~ `S205_RB5_3-37` (S201 RB1/2, S202 RB1, S203 RB1, S204 RB1~5, S205 RB1~5) | Phase 1 의 6-col block 자동 흡수 가능 (Phase 3 단순) |
| **8-col WRS 변형** | 8 | 4 | `S203_WRS`, `S204_WRS1`, `S204_WRS2`, `S205_WRS` | 무선센서 sub-bit, R5 헤더 `번호/영역/변수종류/변수/타입/어드레스/설명문` — **사용자 결정: Phase 2 skip (후속 PR)** |
| **51-col COVER** | 51 | 1 | `COVER` | 표지 페이지, IO 데이터 0건. signature 매치 안 됨 (정상). 추가 분기 불요 |
| **1-col Sheet1** | 1 | 1 | `Sheet1` | 빈 시트. signature 매치 안 됨 (정상) |

### 1.2 IoListStrategy.fs 현 구현 분석 (Phase 0 진단 결과)

- `directionOf` (line 111~115): `%IW` → Input, `%QW` → Output 추출 함수 정상
- `reshapeRowToBlocks` (line 120~134): `blockSize = 5` 가정 — **결함 (Critical)**. 실제는 6-col block (`[빈 col] + Word + Tag + DataType + Address + Symbol`). i offset 1 cell 어긋나서 `directionOf` 가 `BOOL` 자리를 address 로 받아 `%IW`/`%QW` 매치 실패
- `OoxmlExtractor.fs` 의 `ExpandSparseRow`: 빈 셀을 `""` 로 채워 dense array tab-join — **정상 (무결)**
- `MarkdownCapPolicy.fs:243 applySampling`: `SampleHeadCount=5` / `SampleTailCount=5` 시트당 적용 — **Major** (4011 row 손실)

→ root cause 확정: `IoListStrategy.fs` block 구조 오인 (Critical) + `MarkdownCapPolicy.fs` head/tail elide 정책 (Major) 복합. Extractor 무결.

## 2. 박제 결정 (사용자 confirm 완료 — 변경 금지)

- ✅ **work tree** = `/f/Git/ds2/light-house` (branch `light-house`)
- ✅ **xlsx 원본 외부 LLM 전송 금지** (NDA) — agent prompt 에 raw 셀 박지 않음, 정제 markdown 경로로만 검증
- ✅ **Direction 컬럼은 Address prefix derive 가 SSOT** — xlsx 에 별도 Direction 컬럼 없음 (`%IW` → Input, `%QW` → Output, 외 `-`)
- ✅ **WRS 무선센서 sub-bit 박제 깊이** — Phase 0 진단 결과 보고 후 사용자 결정 (배터리 잔량 등 sub-bit 의 LLM 활용도 평가)
- ✅ **markdown 출력 byte-equal 회귀 가드** = 기존 3 strategy (IoList/WorkOrder/PdfControlSpec) 의 정상 박제 부분은 byte-equal 유지 (StrategyMarkdown SSOT 미변경)
- ✅ **`Apps/Promaker/Docs/guide/01-promaker-yaml-mapping.md` §1.2** 는 이미 본 PR 의 가이드 SSOT 박제 완료 (`/f/Git/dualsoft/secrets/KBSamples/core/guide/01-promaker-yaml-mapping.md`) — Direction 컬럼이 정상 박제되면 LLM 이 본 가이드와 정합 가능

## 3. Phase 진행 표

| Phase | 상태 | 작업 | 추정 line | 검열 |
|---|---|---|---|---|
| 0. 진단 (root cause 확정) | ✅ 완료 | OoxmlExtractor segment text 실측 + cap policy elide 위치 trace + 8-col/27-col 변형 검증 | 0 (보고만) | — |
| 1. 22-col 6-col block fix | ✅ 완료 (`adf4970b`) | `reshapeRowToBlocks` `blockSize=5` + leading 빈 col skip `i=1` 시작 + trailing 빈 col 자동 흡수. Direction 정상 박제 | ~30~40 | 불필요 |
| 2. 8-col WRS 변형 | ⏭ **사용자 결정 — skip** (후속 PR) | — | — | — |
| 3. 27-col RB 검증 | ✅ 완료 (Phase 1 흡수) | Phase 1 의 `i=1 + blockSize=5 × N + trailing` 정합 — 27-col (N=5) 자동 흡수. 신규 test 회귀 가드 박제 (Phase 5) | 0 (Phase 1 흡수) | 불필요 |
| 4. cap policy 보강 | ✅ 완료 (`3c8d99df`) | `MarkdownCapPolicy.applyCapFor strategyName` 인자 추가 + IoList escalation 분기 (`applyIoListSampling` — device base token 단위 sampling + Direction 분포 박제) | ~100~120 | **필수** (①④⑤ — 완료) |
| 5. 통합 test + docs SSOT 갱신 | ✅ 완료 (본 PR) | XlsxStrategiesTests fixture 22-col 정합 갱신 + IoListStrategyTests 신설 6건 + 검열 Major fix 4건 (elide marker 정확 elidedCnt / `deviceOnly` head-tail overlap / fallback 주석 정정) + `todo-documents-based-gfm.md` §6.1 PR-I8 추가 + `documents-based-gfm.md` §1056/§8.5.7 보강 | ~200~300 | 불필요 (test 추가 + docs 갱신 — code change 적음) |
| 6. KB 재색인 + LLM quality 검증 | ⬜ (사용자 수동) | `/indexer` 호출 + 사용자 검증 | — | — |

**자동 commit 예산** (사용자 허용 5회): Phase 1 / 3 / 4 / 5 = 4 commit. Phase 6 은 사용자 수동.

## 4. Phase 별 작업 범위 / 산출물 / 종료 조건 / prompt 골격

### Phase 0 — 진단 (root cause 확정)

**작업 범위** (코드 수정 0건):
- `OoxmlExtractor.fs` 의 시트 → segment text 변환 로직 확인. 빈 셀의 tab 보존 여부 / cells.Length 결과 실측
- 디버깅 console app (또는 test 1회성) 으로 광명2 IO List xlsx 의 `S204_KEY1` 시트에 대한 IoListStrategy.buildSheetTable 의 cells array snapshot 박제
- `MarkdownCapPolicy.fs` / `TextDumper.fs` 의 head/tail elide 진입 조건 trace
- 22-col `S204_KEY1` 시트 + 8-col `S204_WRS1` 시트 + 27-col `S204_RB1_3-25` 시트 각 1개 sample 의 segment text 실 박제 형식 확인

**산출물**:
- root cause 보고 (어느 layer 에서 결함 발생 — Extractor / Strategy / CapPolicy 중 어디?)
- 8-col / 27-col 변형의 signature 매치 여부 + reshape 가능 layout 인지 판정
- 후속 Phase 1~4 의 정확한 범위 / 변경 line 수 재산정

**종료 조건**:
- 사용자가 root cause 보고를 receive + 후속 Phase 진입 confirm (step 옵션)
- WRS 무선센서 sub-bit 박제 깊이 결정 (배터리 잔량 등 박제 범위)

**작업 agent prompt 골격**:

```
## 목적
LightHouse 의 IoListStrategy 가 광명2 IO List xlsx 의 ~90% tag 를 누락하고
Direction 컬럼을 전혀 박제 못 하는 root cause 를 확정.

## 작업 범위 (코드 수정 0건 — 진단만)
1. /f/Git/ds2/light-house/Solutions/Core/Ds2.LightHouse/Extractors/OoxmlExtractor.fs
   의 xlsx 시트 → ExtractedSegment.Text 변환 로직 trace. 빈 셀의 tab 보존 여부 확인.
2. IoListStrategy.fs:120 reshapeRowToBlocks 의 cells.Length 가 22-col 시트에서
   몇이 되는지 실측 — 시트 1개 (S204_KEY1) sample 박제.
3. MarkdownCapPolicy.fs + TextDumper.fs 의 head/tail elide 진입 조건 trace.
4. 8-col (S204_WRS1) / 27-col (S204_RB1_3-25) 시트의 segment text 박제 형식 확인.

## 박제 자료
- xlsx 원본: /f/Git/dualsoft/secrets/KBSamples/core/4-3. ... xlsx (외부 전송 금지)
- 현 KB summary: /f/Git/dualsoft/secrets/KBSamples/core/.lighthouse-kb/summary/IoListStrategy-7c2c2034-... .md

## 보고 형식
1. root cause layer (Extractor / Strategy / CapPolicy / 복합)
2. layer 별 결함 증거 (file:line + 실측 값)
3. 후속 Phase 1~4 의 정확한 변경 범위 + 변경 line 수 재산정
4. 8-col / 27-col 변형의 후처리 권고 (별 분기 vs 기존 reshape 재사용)
5. WRS sub-bit 박제 깊이 권고
```

**검열 agent prompt 골격** (Phase 0 는 코드 변경 0건이라 검열 의무 없음 — 보고 검증 1회로 대체):

main 이 작업 agent 보고를 직접 read + xlsx/summary 실측과 cross-check + 보고 일관성 검증.

### Phase 1 — 5-block fix (22-col)

**작업 범위**:
- `IoListStrategy.fs` 의 `reshapeRowToBlocks` 또는 `buildSheetTable` 보강 (Phase 0 root cause 따라 결정)
- `OoxmlExtractor.fs` 의 빈 셀 tab 보존 (root cause 가 여기면)
- 22-col 시트의 5-block × 5 col 완전 reshape + Direction 정상 박제 확인

**종료 조건**:
- `S204_KEY1` 시트의 박제 결과 = 약 60 tag (xlsx 실측) ± 5%
- Direction 컬럼이 `Input` / `Output` / `-` 중 하나로 박제됨 (`-` 전부는 결함)
- 기존 WorkOrder / PdfControlSpec strategy markdown 출력 byte-equal 유지
- `dotnet build` 통과 + 기존 test (`StrategyMarkdownTests.fs`) 통과

**작업 agent prompt 골격**:

```
## 목적
Phase 0 진단 보고 (<here-insert-cause>) 의 root cause 를 fix.
22-col 시트의 5-block 가로 layout 을 완전 박제 + Direction 컬럼 정상화.

## 작업 범위
<here-insert-phase0-scope>

## 박제 결정
- markdown header/footer + StrategyMarkdown SSOT 미변경
- 기존 strategy markdown 출력 byte-equal 유지 (WorkOrder / PdfControlSpec)
- Direction = Address prefix derive (%IW/%QW), 외 `-`

## 종료 조건
1. dotnet build Apps/Promaker/Promaker.sln -c Debug --nologo -v q 성공
   (Promaker.exe 실행 중이면 file lock 으로 fail — 사용자에게 종료 요청)
2. Ds2.LightHouse.Tests 전체 통과
3. S204_KEY1 시트 박제 결과 60±5 tag, Direction 비빈 값

## 보고 형식
1. 변경 파일 + 변경 line 수 + 변경 의도 1줄 설명
2. 결정/대안 박제 (root cause 의 대안 path 와 비교)
3. 자가검열 trigger 충족 여부
4. 다음 phase 가 받을 invariant
```

**검열 agent prompt 골격**:

```
## 목적
작업 agent 의 Phase 1 변경 (git diff) 를 독립 검토. Critical/Major/Minor 라벨 명시.

## 검증 항목
0. 사용자 명시 의도 verbatim 인용 + patch ↔ 의도 1:1 매핑. 의도 누락 = Critical
1. 22-col 5-block reshape 의 의도 정합 (Phase 0 root cause 와 일치)
2. Direction 추출 (`%IW`/`%QW`) 정확성
3. 기존 strategy (WorkOrder/PdfControlSpec) 의 markdown 출력 byte-equal 유지 (regression)
4. F# 코드 컨벤션 (Apps/Promaker/Docs/yaml-protocol-v0.md 의 F# 어휘 + ~/.claude/dotnet.md 지침)
5. 예외 처리 (try/catch 남용 X — ~/.claude/CLAUDE.md "예외 처리" 박제)
6. 자가검열 trigger 5종 (시그니처/신규 3+/100 line+/dispatch/SSOT) 위반 시 별도 박제

## 금지
- 코드 수정 X
- git 명령 X (commit / push / branch / mv)
- 작업 agent 의 의도 반론 시 사용자 결정에 위임 박제

## 보고 형식
| 우선순위 | 항목 | 위치 (file:line) | 사유 | 권고 |
모든 발견 검증 + Critical 발견 0 / Major N / Minor M 집계
```

### Phase 2 — 8-col WRS 변형 (⏭ 사용자 결정: skip)

사용자 confirm: "Q2. skip" → 본 Phase 보류. 후속 PR 로 처리.

8-col WRS 4 시트 (`S203_WRS`/`S204_WRS1`/`S204_WRS2`/`S205_WRS`, ~846 tag) 의 무선센서 sub-bit 박제는 본 PR 범위 외. 광명2 가이드 §1.2 의 zone/device/action Direction 정합은 22-col + 27-col 박제로 충족 (총 3,575 tag).

후속 PR 진입 시 본 절 참조:
- R5 헤더: `번호/영역/변수종류/변수/타입/어드레스/설명문` (Tag Name = E 컬럼)
- sub-bit 종류: 배터리 잔량 6M/3M/즉시교체, 통신 이상 등
- IoListStrategy.fs 안 별 분기 신설 권고 (별 strategy 신설은 over-engineering)

### Phase 3 — 27-col RB 검증

**작업 범위**:
- 13 RB 시트의 layout 확인 (Phase 0 권고 따름)
- 기존 5-block reshape 재사용 가능하면 signature score 추가, 변형이면 별 분기

**종료 조건**:
- 13 RB 시트 박제 결과 = 시트당 약 230 tag (Phase 0 실측 잠정) 이상
- 다른 시트 박제 byte-equal

### Phase 4 — cap policy 보강

**작업 범위**:
- `MarkdownCapPolicy.fs` 의 IoList summary 박제 elide 정책 수정
- 현재 head 5 + tail 5 → 시트별 unique device base token 단위로 head/tail 박제 (예: 시트당 device 별 1 sample tag + 다른 device 의 다른 base token sample)
- 토큰 budget 초과 시 escalation Stage 2/3 박제

**종료 조건**:
- 토큰 budget 초과 시에도 device coverage 80%+ (xlsx 의 unique device base token 수 기준)
- Direction 정보 박제율 80%+ (이상적으로는 100%)
- 다른 strategy summary byte-equal 유지

### Phase 5 — 통합 test + docs SSOT 갱신

**작업 범위**:
- `Solutions/Tests/Ds2.LightHouse.Tests/IoListStrategyTests.fs` 신설 — 골든 fixture (광명2 IO List) 박제 결과 + Direction 분포 + WRS 무선센서 박제 검증
- `Apps/Promaker/Docs/todo-documents-based-gfm.md` 에 PR-I 신규 번호 부여 + 본 작업 박제
- `Apps/Promaker/Docs/documents-based-gfm.md` §1056 (fallback hierarchy) / §8.5.7 (signature weighting) 보강 — 5-block / 8-col / 27-col 변형 박제

**종료 조건**:
- IoListStrategyTests 신규 test 5건+ 통과
- 기존 test 회귀 0건
- docs SSOT 갱신 완료

### Phase 6 — KB 재색인 + LLM quality 검증 (사용자 수동)

**작업 범위**:
- `/indexer` skill 로 `/f/Git/dualsoft/secrets/KBSamples/core/` 재색인
- summary 박제 결과 (Direction 박제율 / tag 수 / WRS 박제) 확인
- 광명2 가이드 (`guide/01-promaker-yaml-mapping.md` §1.2) 와 정합 확인 — LLM 이 KB 만 read 해도 zone/device/action 정확 추출 가능한지

**종료 조건**:
- 사용자 검증 통과

## 5. 주의 사항 (W1~W6 박제)

### W1 — 사용자 의도 verbatim
사용자 의도: "1. 진행하되, work tree 를 light-house 로 옮겨서 작업". `--orch` 모드 진행. work tree = `/f/Git/ds2/light-house`. main 은 코드 직접 수정 X, 모든 Phase 구현을 Agent 위임. Phase 0 는 step 옵션 (사용자 confirm 후 Phase 1 진입).

### W2 — 자가검열 prompt 의도 정합
모든 검열 agent prompt 의 0번 항목 = "사용자 명시 의도 verbatim 인용 + patch ↔ 의도 1:1 매핑. 의도 누락 = Critical".

### W3 — prerequisite checklist
- `Promaker.exe` 실행 중이면 dotnet build file lock 발생. 작업 진입 전 사용자에게 종료 요청.
- LightHouse Service 가 KB 색인 중이면 .lighthouse-kb/ 폴더 lock. Phase 6 진입 전 확인.

### W4 — Edit replace_all 위험
`reshapeRowToBlocks` / `directionOf` / `IoListStrategy` 등 의 이름 변경 시 Grep 으로 영향 범위 확인 의무.

### W5 — 진단 가설 1회 1단계
Phase 0 는 root cause 가설 1개씩 검증. 누적 변경 금지.

### W6 — 결정 직후 부분 되돌림 금지
사용자가 "5-block 박제 우선" 결정 후 같은 turn 에 "8-col 도 같이" 류 부분 되돌림 옵션 제안 금지. 결정 후 추가는 사용자 명시 요청 시만.

### 자가검열 trigger (필수 충족 시 sub-agent 위임)
- ① 함수 시그니처 변경 1건 이상 / ② 신규 함수 3개 이상 / ③ 단일 파일 100 line 이상 변경 또는 2개 이상 파일 동시 변경 / ④ dispatch / state machine 재작성 / ⑤ public API / SSOT 상수 갱신
- 충족 시: 작업 agent 결과를 별 검열 agent 위임 → C/M 발견 시 작업 agent 재호출

### build 검증
모든 변경 후:
```bash
dotnet build /f/Git/ds2/light-house/Solutions/Ds2.LightHouse.sln -c Debug --nologo -v q
dotnet test /f/Git/ds2/light-house/Solutions/Tests/Ds2.LightHouse.Tests/Ds2.LightHouse.Tests.fsproj --nologo -v q
```

### commit message 형식
- `[light-house] IoList v2 Phase N — <요약>` 한 줄 + line break + itemize 항목 (3~4줄)
- `--gc` 절차 (`git pull --ff-only` → commit → push)
- `Co-Authored-By` 박제 금지

## 6. 금지 사항

- xlsx 원본 셀 데이터를 agent prompt 에 박지 않음 (NDA)
- 기존 WorkOrder / PdfControlSpec strategy markdown 출력 형식 변경 금지 (byte-equal 회귀 가드)
- StrategyMarkdown SSOT (header 6행 / footer 7행 / docId / fullHash / estimateTokens) 변경 금지
- Phase 별 분리 commit. 단일 commit 으로 묶기 금지.
- main 이 코드 직접 수정 금지 (sln 편집 / SSOT drift 갱신 / 진행 표 갱신만 main 권한)

## 7. 처리 완료 history

| Phase | commit | 작업 | 검열 | 비고 |
|---|---|---|---|---|
| 0 | (보고만) | root cause 진단 — `IoListStrategy.fs:120 reshapeRowToBlocks` blockSize 5 정합 결함 (Critical) + cap policy head/tail elide (Major) 복합 확정 | — | 8-col WRS skip 결정 + 22-col / 27-col 모두 5-col block × N 흡수 가능 박제 |
| 1 | `adf4970b` | 22-col 6-col block fix — `i=1` 시작 + `blockSize=5` × N + trailing 빈 col 자동 흡수. Direction Input/Output 박제율 0% → 100% 복구 | 불필요 | signature `hasFourBlockLayout` threshold 22 박제 |
| 3 | (Phase 1 흡수) | 27-col RB 검증 — Phase 1 의 `while i + 5 <= cells.Length` 가 27-col (N=5) 자동 흡수. 신규 test 회귀 가드 박제 (Phase 5) | — | — |
| 4 | `3c8d99df` | cap policy 보강 — `applyCapFor strategyName` dispatch + IoList 전용 `applyIoListSampling` (device base token 단위 sampling + Direction 분포 박제). default 분기 byte-equal 회귀 가드 박제 | ✅ 완료 (Major 4건 박제 → Phase 5 fix scope) | head 10 + tail 10 + unique device sample row 합집합 keep |
| 5 | (본 PR) | XlsxStrategiesTests fixture 22-col 정합 갱신 + IoListStrategyTests 신설 6건 + 검열 Major 4건 fix (elide marker 정확 elidedCnt / `deviceOnly` head-tail overlap 흡수 / 6-col fallback 주석 정정 / `reshapeTagToDeviceBase` edge case test 박제) + docs SSOT 갱신 (`todo-documents-based-gfm.md` PR-I8 + `documents-based-gfm.md` §1056/§8.5.7) | 불필요 (test 추가 + docs 갱신 — Phase 4 검열에서 흡수된 Major 4건 fix 완료) | 전체 test 430 / 0 통과 |
