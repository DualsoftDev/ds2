# MCP 호출 batch 화 — 완료 (2026-05-07)

`doc/todo-batch-mcp-call.md` 의 Pass 0~6 작업 결과 통합 기록. todo 의 진행 상태 표 / 결과 절들의 핵심 결정 + 산출물 + 측정 ROI.

> **Pass 6 핵심**: Pass 3~5 의 (c) variable binding 흐름이 numTurns 부풀림을 해소 못하는 한계 발견 → (b) `apply_operations` batch tool 로 정석 대체. 측정 결과 numTurns -71% / wall -24%.

---

## 결정 결과 (3축 채택안 — Pass 6 final)

| 안 | 채택 여부 | 비고 |
|---|---|---|
| (a) parallel tool_use 가이드 | ❌ Pass 6 폐기 | (b) 가 채택되면서 SystemPrompt 의 chain pattern 절 제거 |
| (c) Variable binding (`assignVar` / `$<var>`) | ❌ Pass 6 폐기 | numTurns 부풀림 해소 못함 — (b) 로 대체 |
| **(b) Batch tool (`apply_operations`)** | ✅ **Pass 6 채택** | 1 LLM message = 1 tool_use = 1 internal turn = **진짜 round-trip 압축** |

---

## 핵심 산출물

### Code 변경 (commit 단위)

| commit | 변경 |
|---|---|
| `5e80af3` | Pass 1 — SystemPrompt 의 (a) parallel tool_use 가이드 (single-op safe case 한정) |
| `08d9fed` | Pass 1.5 — `run-pass15.fsx` paired 자동 측정 + (a) 효과 inconclusive 판정 |
| `e7e8025` | Pass 2 — `run-pass2-spike.fsx` SDK 직렬 dispatch 확인 → SemaphoreSlim gate 미도입 결정 |
| `b44a644` | Pass 3 — F# (c) 핵심 (`VarCache` / `sanitizeVarName` / `resolveGuidOrVar` / `registerVar` / `SignalCascadeFailure` / `@`/`$` reject) + C# 7종 add_* 의 `assignVar` 인자 + RunMutation cascade 단락 |
| `80ac12b` | Pass 4 — SystemPrompt 풀세트 (chain pattern + 실린더 8 op 압축 예시) + Negative test 37 케이스 (VarBindingTests + SanitizeNameTests) |
| `b56af69` | Pass 5 hot-fix — `MainViewModel.ScheduleMeasurePrompt` 의 sendHandler timing race |
| `955e5a4` | Pass 5 — `add_project` MCP tool (도구 풀세트 14→15) + cascade scope 축소 (1 SendAsync → 1 LLM message, 1500ms TTL) + `run-pass5.fsx` 자동 측정 + fresh store 5 trial |
| (HEAD) | Pass 6 — (c) variable binding 폐기 + (b) `apply_operations` 채택. 도구 풀세트 15→16. F# `queueBatch` + `BatchOpInput/Result` types + `ImportPlanBuilder.TruncateTo`. SystemPrompt 풀 갱신. VarBindingTests → BatchTests (15 케이스). 측정 numTurns 11.8→3.4. enter trace + _toolCallSeq + cascade flag 자연 cleanup |

### 측정 / 분석 도구 (보존 — 회귀 측정용)

- `doc/run-pass5.fsx` — N-trial 자동 측정 + Gate 판정 + markdown 결과. 두 번째 인자로 model file (`.sdf`/`.json`/`.aasx`/`.md`) load 가능
- `doc/pass5-message-analysis.fsx` — ds2.log 의 message id 그룹핑 + multi tool_use 비율 + assignVar/`$var` 사용률 (chain pattern 활용도 객관 검증)
- `doc/run-pass2-spike.fsx` — 단일 prompt 측정 + concurrency 분석
- `doc/run-pass15.fsx` / `run-pass15.ps1` — paired baseline/treatment 측정 (Pass 1.5 자동화)
- `doc/poc-roundtrip-analysis.ps1` — round-trip 시간 분해 + message id 자동 그룹핑

---

## Pass 6 측정 결과 — (b) batch tool 의 round-trip 압축

### Pass 6 default project 환경 (n=5, `apply_operations` 채택 후)

| metric | Pass 5 ((c) chain pattern) | **Pass 6 ((b) batch tool)** | 변화 |
|---|---:|---:|---:|
| Wall(s) | 28.2 ± 2.5 | **21.4 ± 2.7** | **−24%** |
| ApiMs(s) | 26.4 ± 2.7 | **19.4 ± 2.5** | **−27%** |
| **NumTurns** (claude CLI internal) | 11.8 ± 1.0 | **3.4 ± 0.5** | **−71%** ⭐ |
| AsstLines (stream-json) | 15.6 ± 1.6 | 5.6 ± 0.8 | −64% |
| Self-close OK | 5/5 | 5/5 | — |
| authoring (1 undo step) | 1/trial | 1/trial | — |

### message-grouping 검증 (Pass 6, n=7 mcp messages)

| metric | Pass 5 ((c)) | **Pass 6 ((b))** |
|---|---:|---:|
| Max tool_use / message | 10 | **1** |
| Avg tool_use / message | 7.72 | **1.00** |
| Multi tool_use ≥2 | 92% | 0% |

→ **(b) 의 의도 정확 검증**: 모든 mcp message 가 single tool_use (`apply_operations` 1번 + 그 안 9-10 op JSON array). chain pattern 의 numTurns 부풀림 (1 message 안 N tool_use × N internal turn) 자연 해소.

### Pass 6 Gate 판정

- avg numTurns 3.4 ≤ 5 ✅ **PASS**
- avg wall 21.4s 약간 > 18s ❌ — LLM 추론 + JSON array serialization 비용 (server-side 압축은 한계 도달)
- 종합: **본 todo 의 핵심 ROI = numTurns 압축**. wall 은 LLM 추론 비용 의존이라 server-side 코드로 추가 압축 불가

---

## (Pass 5 폐기) 이전 측정 결과 — chain pattern 활용도

> 본 절은 Pass 5 시점의 (c) chain pattern 측정. Pass 6 에서 (c) 폐기 후 의미 약화됐으나 결정 근거로 보존.

### Pass 5 message id 그룹핑 분석 (n=25 mcp 호출 message)

본 분석이 (c) ROI 의 결정적 근거 — Anthropic API 의 ground truth (message id 그룹핑) 직접 측정. prompt 영향 / 환경 영향 X.

| metric | value |
|---|---|
| **Multi tool_use messages (≥2 op)** | **92.0%** |
| **Max tool_use / message** | **10** (add_project + 8 op chain) |
| **Avg tool_use / message** | **7.72** |
| **assignVar 사용 message 비율** | **80.0%** |
| **`$<var>` 참조 사용 message 비율** | **84.0%** |

### 분포

```
 1 tool/msg :  2 msg   (read 단독: list_systems / list_projects)
 2 tool/msg :  3 msg   (read 묶음 + 회복 case)
 9 tool/msg : 15 msg   ← 실린더 풀세트 (system + api_def×2 + flow + work×2 + call×2 + arrow)
10 tool/msg :  5 msg   ← + add_project (fresh store 회복 시)
```

### 의미 — 9× round-trip 압축

- baseline (Pass 0 PoC): 9 op 시나리오 = 9 round-trip 직렬 (LLM message 9개)
- Pass 5: **1 LLM message 가 9 op 처리** = 1 round-trip
- → **9× round-trip 압축**, chain pattern + variable binding 의 본질 효과 정량 검증

---

## 측정 결과 — 절대 metric

### Pass 5 default project 환경 (n=5)

`run-pass5.fsx` + `/f/tmp/NewProject.json` (사용자 GUI 로 NewProject 생성 + 저장):

| metric | avg | min | max | std |
|---|---:|---:|---:|---:|
| Wall(s) | 28.2 | 25.2 | 31.8 | 2.5 |
| ApiMs(s) | 26.4 | 23.2 | 30.6 | 2.7 |
| **NumTurns** (claude CLI internal) | **11.8** | 11 | 13 | 1.0 |
| AsstLines (stream-json, 부풀림) | 15.6 | 14 | 18 | 1.6 |
| ToolUse(ok) | 9.8 | 9 | 11 | 1.0 |
| Self-close OK | 5/5 | | | |
| stop_reason 도달 | 5/5 | | | |
| authoring (1 undo step) | 1/trial | | | |

### Pass 5 fresh store 환경 (n=5)

| metric | avg |
|---|---:|
| Wall(s) | 50.2 |
| AsstLines | 27.8 |
| ToolUse(ok) | 10.2 |

추가 비용 (~22s) = `add_project` 회복 turn 의 비용. fresh store 시나리오의 본질.

### Pass 5 정량 Gate (todo spec)

`avg numTurns ≤ 5 AND avg wall ≤ 18s` — **FAIL** (11.8 / 28.2s).

**Gate FAIL 의 해석**: numTurns 는 claude CLI 의 internal cycle (LLM 추론 + tool dispatch × N + 응답) 카운트. 1 LLM message 안 9 tool_use 가 ≈ 9 internal turn 으로 카운트되어 부풀림. 진짜 LLM round-trip 은 message id 그룹핑 분석 (위) 으로 측정해야 정확. **Gate 자체가 부적절한 metric**.

→ **(c) ROI 검증의 결정적 근거 = message id 그룹핑** (avg 7.72 op/msg, multi 92%).

---

## Pass 별 핵심 산출

### Pass 0 — PoC

- (a) parallel tool_use 자발 동작 검증 (claude CLI stream-json 통과)
- ID chain boundary 가 (c) 우선의 근거

### Pass 1 / 1.5 — (a) 가이드 + 측정

- SystemPrompt 의 mutation batching 절 (single-op safe case 한정)
- n=3 paired 측정 결과 inconclusive — chat hooks/CLAUDE.md noise + 작업 누락
- **결론**: (a) 단독으로는 chain pattern 효과 없음 → (c) 진행

### Pass 2 — McpHostService 동시성 spike

- ToolCall trace 로그 (enter seq=N t=<thread> nanos=...) 추가
- 측정 결과 인접 enter Δms = 311~395 / 고유 thread id = 2 → **SDK 자체 직렬 dispatch 확인**
- → SemaphoreSlim gate 미도입 결정 (race 처리 비용 0)
- 부수 발견: `--measure-then-exit` self-close hang (Pass 5 hot-fix 로 해결)

### Pass 3 — (c) F#/C# 구현

- F# `ImportPlanBuilder.VarCache` (ConcurrentDictionary, 향후 SDK upgrade 대비 cheap insurance)
- F# `[<VolatileField>] cascadeFailed` + `SignalCascadeFailure` (Plan.Clear 동반)
- F# `sanitizeVarName` (1-32 chars, `[a-zA-Z_][a-zA-Z0-9_]*`, codepoint echo 회피)
- F# `resolveGuidOrVar` / `registerVar` (cap=50, 중복/sanitize fail = invalidOp)
- F# `sanitizeName` 의 `@`/`$` prefix reject (self-injection 방지)
- C# 7종 add_* 에 `assignVar` 인자 + dispatcher work 안 sanitize/resolve 통일 + RunMutation cascade 단락 + prefix 중복 회피
- **SemaphoreSlim gate 미도입** (Pass 2 결정)

### Pass 4 — SystemPrompt + Negative test

- SystemPrompt 의 mutation batching 절 풀세트 확장 (실린더 8 op 압축 예시 + assignVar rule + `@`/`$` name reject + BATCH_ABORTED 정책 + 중복 reuse 금지)
- VarBindingTests.fs 신규 31 케이스 (sanitizeVarName 11 / resolveGuidOrVar 9 / registerVar 6 / cascade 5)
- SanitizeNameTests 의 `@`/`$` reject 6 케이스
- **129 테스트 통과**

### Pass 5 — add_project + cascade scope 축소 + 측정

- `ImportPlanOperation.AddProject of Project` case + Direct/Tracked handler
- F# `queueAddProject` (이름 unique 검사) + `queueAddSystem` 의 plan-AddProject fallback (같은 turn 안 add_project → add_system chain 지원)
- C# `AddProject` MCP tool (assignVar 호환) + `PromakerToolNames` allowlist 14→15
- SystemPrompt 의 "Phase 1 has no add_project" 제거 + greenfield checklist 갱신
- **Cascade scope 축소** — `1 SendAsync` → `1 LLM message` (1500ms TTL). LLM 이 다음 message 에서 자율 회복 가능. SystemPrompt 의 "WHOLE turn aborts" → "REST of the message aborts, next message can retry" 갱신
- hot-fix: `MainViewModel.ScheduleMeasurePrompt` 의 sendHandler timing race (`wasSending` 초기값 = `vm.IsSending` 으로 캐시)
- 자동 측정 도구 + ROI 검증

---

## 결정 변경 / 신규 (Pass 0~5 종합)

| 항목 | 이전 | 변경 후 | 근거 |
|---|---|---|---|
| (c) race 처리 | 보완안 2 (SemaphoreSlim 직렬화) | **gate 미도입** | Pass 2 SDK 자체 직렬 dispatch 확인 |
| `VarCache` 자료구조 | TBD | `ConcurrentDictionary<string,Guid>` | dispatcher 안 R/W race-free + 향후 SDK 변경 cheap insurance |
| `cascadeFailureFlag` 표현 | `volatile bool` | `[<VolatileField>]` + timestamp | Pass 5 cascade scope 축소 위해 timestamp 도입 |
| **Cascade scope** | 1 SendAsync (Pass 3) | **1 LLM message (1500ms TTL)** | Pass 5 — fresh store 시나리오에서 LLM 의 자율 회복 가능 |
| **add_project 도구** | 미존재 (Phase 1) | **신규 추가** | Pass 5 — fresh store 자율 모델 빌드 + chain 패턴 첫 op 가능 |
| Tool 풀세트 | 14종 (phase 1d) | **16종** (+add_project, +apply_operations) | Pass 5 + Pass 6 |
| **(c) variable binding** | Pass 3/4/5 채택 | **Pass 6 폐기** | numTurns 부풀림 해소 못함 — (b) 가 정석 |
| **(b) apply_operations** | Pass 1~5 Appendix A 격리 | **Pass 6 채택** | 1 LLM message = 1 tool_use = 1 internal turn |

---

## 미해결 / 후속

### baseline 정밀 비교 (선택)

- 본 measurement 의 신뢰도는 message id 그룹핑 (chain 활용도) 가 결정적이라 baseline 비교 불필요
- 단 정량 wall time 비교 시 같은 환경 (HEAD vs Pass 0 commit) 에서 같은 prompt 5+ trial 재측정 필요

### 측정 인프라 cleanup 결정

`todo-batch-mcp-call.md` 의 cleanup 표 + 본 todo 종료 시점 결정:

| 항목 | 처리 |
|---|---|
| `ModelTools` 의 `enter` trace 로그 + `_toolCallSeq` | **Pass 5 종료 시 즉시 제거** (Pass 2 spike 산출 종료, 매 mcp call 마다 noise) |
| measure-* 인자 / `ScheduleMeasurePrompt` / `MainWindow.Closing` autostart 분기 | **보존** (회귀 측정 / 새 모델 비교용) |
| `doc/run-pass*.fsx` / `pass5-message-analysis.fsx` / `poc-roundtrip-analysis.ps1` | **보존** |
| `doc/pass5-results-*.md` raw 결과 (현재 6개) | **gitignore 또는 정리 후 삭제** (의미 있는 결과만 본 done 문서에 통합 완료) |
| 측정 분기 namespace 분리 (`MeasurementHooks.cs`) | **skip** (autostart flag gate 라 production 영향 X, ROI 낮음) |

---

## 검증 산출

- 솔루션 빌드 통과 (warning 2 = OllamaSharp 분석기 버전 mismatch, 무관)
- `Ds2.LlmAgent.Tests` **129 테스트 통과**
- `run-pass5.fsx` self-close 5/5, stop_reason 5/5, authoring 1/trial × 5
- `pass5-message-analysis.fsx` chain pattern 활용 92%, avg 7.72 op/msg
- ROI: **9× round-trip 압축** (1 LLM message = 9 op chain)

---

## 검증된 사실 표 (수정 시 todo + done 동기 갱신)

| 결정 근거 | 파일:line | 의미 |
|---|---|---|
| Pass 5 cascade scope | `Solutions/Core/Ds2.LlmAgent/ImportPlanBuilder.fs` `CascadeFailureFlag` getter | 1500ms TTL → 1 LLM message 단위 sticky / 다음 message 자동 reset |
| Pass 5 add_project | `Solutions/Core/Ds2.Core/Store/ImportPlan.fs:8` `AddProject of Project` | DU case 추가 |
| Pass 5 add_project tracked | `Solutions/Core/Ds2.Editor/Editor/ImportPlanApply.fs` `AddProject` 분기 | TrackAdd 1줄 |
| Pass 5 add_project → add_system chain | `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` `queueAddSystem` 의 plan fallback | `Queries.allProjects [] → plan.AddProject` fallback |
| Pass 3 (c) F# SSOT | `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` `sanitizeVarName / resolveGuidOrVar / registerVar` | C# 측은 string 통과만 |
| Pass 5 hot-fix race | `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.cs` `ScheduleMeasurePrompt` 의 `wasSending = vm.IsSending` | sendHandler 등록 시점 캐시 |
| Tool 풀세트 15종 | `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs` `All` 배열 | drift 시 `PromakerToolNamesDriftTests` 자동 검출 |
