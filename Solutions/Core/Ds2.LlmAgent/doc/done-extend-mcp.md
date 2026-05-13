# done-extend-mcp.md — Promaker MCP tool 확장 (Active/Passive 분리 + Device helper) 완료 기록

본 문서는 `todo-extend-mcp.md` 의 phase 4 stage (L1/L2/L3 + Tests) 가 모두 완료된 시점의 historical record 입니다. 진행 중 결정의 변경, 적용 시점의 path/식별자, 측정 결과 등 todo 측의 *현재 상태 갱신* 정책과 달리 *완료 시점의 사실 그대로* 보존됩니다 (CLAUDE.md "두 문서 유지 정책" / `done-*.md` historical 정책).

> **trace 정책**: 본 작업의 phase 진행 중 발견 사항 / 결정 / commit 단위는 todo-extend-mcp.md 의 rev 12~15 본문이 SSOT. 본 문서는 phase 종결 시점의 *결과물* 만 누적 — 세부 결정 history 추적이 필요한 경우 todo rev 본문 + `git log -p doc/todo-extend-mcp.md` 활용.

---

## phase 진행 history

| stage | commit | 핵심 |
|---|---|---|
| L1 (F# Core) | `7482ced` (rev 11 시작) → `ff52a70` (rev 12 자가 검열) | `Ds2.LlmAgent.fsproj` 에 `Ds2.Core` 직접 ProjectReference 추가, `ImportPlan.Device.fs` 시그니처 변경 4건 (ensureSystem `systemType`, ensurePendingWork `workDuration`, `WiringMode` DU 신설, `buildPassiveDeviceCascade` `let internal` 신설), `buildWorkArrowsAllPairs` 신설, D9 동명 PassiveSystem 충돌 차단 (cross-project 한정). Mermaid/CSV caller 무수정 (wrapper 기본값 방식). |
| L2 (F# LlmAgent) | `3e7c694` (rev 13) | `ToolOperations.fs` ~270 line 변경. queueAddSystem → Active/Passive 분리, queueAddCall (workId, apiDefId) 2-인자 + 룰 C 런타임 검증 + ApiCall cascade (ApiDefId / OriginFlowId), queueAddApiDef txWorkId/rxWorkId 인자 추가, helper 4종 신설 (queueAddCylinder/Clamp/Robot/Device — runDeviceCascade 공통 wrapper), dispatchBatchOp 반환 시그니처 `(Guid option * (string * Guid) list * string)` 확장, `MutationQuotaSync = 50` literal 신설 (LlmTurnContext.cs sync), `cascadeOpCount` SSOT 단일 함수. |
| L3 (C# Promaker) + Drift/Prompt + Tests | `0facff1` (rev 14) | `ModelTools.cs` (+315/-19) Active/Passive 분리 + AddCall 2-인자 + AddApiDef txWorkId/rxWorkId + helper 4종 (`RunPairedDeviceCascadeWork` / `RunListDeviceCascadeWork` 통합 본문) + D8 quota cascade single/batch 양 경로. `PromakerToolNames.cs` 16→21. `LlmChatViewModel.cs:35` xmldoc. `IsHelperOp` / `CalcCascadeOpCount` 헬퍼 (`ToolOperations.cascadeOpCount` SSOT 호출). Prompt md 3 파일 갱신 (helper 권고 / 11→9 op cylinder 예시 / self-check 의미 반전). `Solutions/Core/Ds2.LlmAgent/CLAUDE.md` "16개" 4곳 → 21. BatchTests.fs 5 fixture cutover + ValidateModelTests partial application fix + DriftTests 21 sanity. **F# 0w/0e, C# 0e, 147/147 PASS.** |
| Tests (§5.6 신규 6) | rev 15 (본 commit) | `HelperCascadeTests.fs` (13 facts) + `HelperGuiParityTests.fs` (2 facts, Fixtures/WithCyl.json) + `PromptCanaryTests.fs` (4 facts) + `DeviceTypeDriftTests.fs` (3 facts) + `LlmTurnContextQuotaTests.fs` (17 cases) + `ImportPlanApplyApiCallTests.fs` (4 facts) = 44 신규 tests. **191/191 PASS.** WithCyl.json `doc/` → `Solutions/Tests/Ds2.LlmAgent.Tests/Fixtures/` mv (옵션 B 영속화). 자가 검열 (sub-agent) Critical/Major 0건. |

---

## 호환성 invalidate 사항 (todo §5.1 → 본 문서로 이전)

본 작업은 다음 *기존 약속* 을 명시적으로 invalidate. 후속 작업이 회귀 의심 시 본 절 인용으로 의도 변경임을 확인 가능:

1. **`done-promaker-llm-agent.md:1516` Pass E refactoring 시점 약속** — "queueAddCall 시그니처 + 동작 100% 보존" → 본 작업 L2 (rev 13) 의 (workId, apiDefId) 2-인자 시그니처 변경으로 invalidate.
2. **`done-batch-mcp-call.md` 의 "Tool 풀세트 = 16종" 흐름** → 본 작업 L3 (rev 14) 의 21종으로 갱신.
3. **`todo-promaker-llm-agent.md:495` 부근 Phase 1 시점 표 — "Ds2.LlmAgent ProjectReference = Ds2.Editor only 충분"** → 본 작업 L1 (rev 12) 의 `Ds2.Core` 직접 ProjectReference 추가로 invalidate. Phase 1 historical 기록은 보존, "Phase 1 한정. extend MCP 에서 invalidate" 주석 추가됨.
4. **`done-promaker-llm-agent.md:295,341,664,694,1683` 풀세트 카운트 trace** → 16 시점 동결 (해당 phase 의 historical record 그대로 보존), 본 작업이 다음 phase 로 명시.

---

## 결정 적용 결과 (todo §3.1 9 결정 모두 implemented)

| # | 결정 (todo §3.1) | 적용 결과 |
|---|---|---|
| 1 | deviceType 단일축 (cylinder/clamp/lifter→"Unit", robot→"Robot", conveyor→"Conveyor") | DeviceTypeDriftTests 가 KnownNames sync 검증 |
| 2 | default apiNames cylinder=ADV/RET, clamp=CLP/UNCLP (rev 11 정정) | HelperCascadeTests 가 default 검증 |
| 3 | ApiCall.apiDefId binding = explicit GUID | ImportPlanApplyApiCallTests 가 binding 보존 검증 |
| 4 | ref name unique = LLM 책임 (도구 1차 차단) | BatchTests 의 중복 ref / undefined ref 검증 |
| 5 | workDuration default cylinder/clamp 500ms, robot/conveyor None | HelperCascadeTests Work.Duration Assert |
| 6 | 🔒 ApiDef ref 누락 = error (D6 strict required) | helper 인자 모두 required, single 호출 시 GUID 직접 노출 |
| 7 | 🔒 robot opposing default `"none"` (rev 11 — 도메인 룰 우선) | HelperCascadeTests robot none N=4 = 11 op |
| 8 | helper cascade quota single/batch 양 경로 차감 | LlmTurnContextQuotaTests 사전 reject + ModelTools `IncrementMutationCount(cascadeOpCount-1)` |
| 9 | 🔒 helper 동명 PassiveSystem 충돌 = invalidOp | HelperCascadeTests D9 차단 검증 (cross-project 한정) |

---

## 측정된 op 수 변화 (todo §1 표 — 산식 측정치)

| 케이스 | helper 도입 전 | 도입 후 |
|---|---|---|
| Cylinder 1개 (project 포함, Active+Passive 분리) | 11 op | 9 op |
| Cylinder 1개 (project 제외) | 9 op | 8 op |
| Robot N=4 (Passive 측 cascade — system+flow+work×4+apidef×4+arrow×3 chain) | 13 op | 1 op (`add_robot`) |

**미측정**: 실 helper 도입 후 LLM 발행 mcp tool call 수 (Pass 5 baseline 비교) 는 본 작업 종결 시점에 미수행 — 후속 측정 trigger.

---

## 잔여 후속 (본 phase 외 영역)

todo `다음 리비전 trigger` 와 동일 — 본 todo 는 phase 종결 후에도 다음 trigger 발생 시 새 rev 추가 가능:

- **M5** single helper validateRefName 보강
- **M6** MutationCallCount rollback docstring
- **rev 10 §1 op 수 실측치** Pass 5 baseline 비교 측정
- **C5** Active/Passive 단일 vs 분리 비교 결정 (별도 분석)
- **후속 phase entity** — IOTag / CallCondition / SubmodelProperty / TokenSpec / HwButton·Lamp·Cond·Action / SubmodelProperty 32 / ApiDefActionType / Work.TokenRole·Duration·ReferenceOf 등. 본 phase 의 도구 범위 외 — 별도 todo 진입 시 `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md` 의 "본 phase 의 도구 범위 — 후속 확장 예정 entity" 절 갱신.

---

## 최종 상태 sanity (다음 세션 진입 시)

```bash
# 빌드 + test sanity
dotnet build Solutions/Tests/Ds2.LlmAgent.Tests/Ds2.LlmAgent.Tests.fsproj -c Debug
dotnet test  Solutions/Tests/Ds2.LlmAgent.Tests/Ds2.LlmAgent.Tests.fsproj -c Debug --no-build

# 기대치: F# 0w/0e + 191/191 PASS
```

```powershell
# canary sanity
Get-ChildItem Apps/Promaker/Promaker/LlmAgent/Prompts/*.md | ForEach-Object { Get-Content -TotalCount 1 $_ }
# 기대치: 4 파일 모두 첫 줄에 "<!-- canary: ... pong: Prompts/<basename> ... -->" 출력
```

```bash
# tool 풀세트 sanity (drift 회귀 검출)
grep -c '"mcp__promaker__' Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs
# 기대치: 21
```
