# todo-extend-mcp.md — Promaker MCP tool 확장 (Active/Passive 분리 + Device helper)

본 문서는 다른 Claude Code 세션에서 본 작업을 그대로 이어받아 진행할 수 있도록 작성된 인계 문서입니다.

> **revision history**
> - rev 1 (2026-05-?): 초안.
> - rev 2 (2026-05-07): 1차 review (5 reviewers) → 사실 정정 + 사용자 추가 결정 (C2 수용 / robot opposing = chain default) 반영.
> - rev 3 (2026-05-07): 2차 `--inspect 5` review 반영 — Critical 6건 / Major 18건 / Minor 17건. 산수 정정 (67→68) / "thin wrapper" → "partial cascade wrapper" / decision 6 의도 본문 강화 / decision 8 quota 정책 신설 / drift 영향 범위 확장 / Layer commit 정의 / rollback 가이드 등.
> - rev 4 (2026-05-07): 3차 `--review` 반영 — Critical 2건 / Major 2건 / Medium 1건. (1) `ensureSystem` 등이 `let private` 라 `InternalsVisibleTo` 만으로 노출 불가 → Ds2.Core 안에 internal wrapper 신설 권장. (2) helper 책임 범위 일관화 (ApiCall 제외 — D6 ref-required 의도와 정합). (3) `[<InternalsVisibleTo("Ds2.LlmAgent")>]` 가 `AssemblyInfo.fs:11` 에 *이미 존재* 반영. (4) `IncrementMutationCount(int delta = 1)` 가 `LlmTurnContext.cs:50` 에 *이미 존재* 반영. (5) `--git-add`/`--git-commit` 표현 일반화. (6) Tests 카운트 stale (신규 7→6). (7) WithCyl.json SSOT (doc 폴더에 이미 untracked 존재).
> - **rev 5 (2026-05-07)**: 4차 `--review` 반영 — Critical 2건 / Major 3건. (1) **D8 quota 처리는 F#→C# 참조 불가하므로 C# `ModelTools` boundary 에서 처리** (`ctx.IncrementMutationCount(cascadeOpCount - 1)`). (2) **wrapper 시그니처 재정의** — `DeviceBatchState` 가 type private 이라 외부 노출 불가 + unit 반환은 ref 등록 경로 부재 → `... -> Guid * (string * Guid) list` 로 변경. (3) **Core `ensureSystem` / `ensurePendingWork` 시그니처 변경 (systemType / workDuration 인자 추가)** — 현행이 systemType 인자 없음 + duration 500ms hardcoded. (4) **OriginFlowId 별도 단계 명시** — `createAndRegisterApiCall` 패턴 그대로 복사하면 OriginFlowId 누락 회귀. (5) **all-pairs 임계값 N≥8 → N≥9 정정** — 산식 `3 + 2N + N(N-1)/2`, N=8=47 pass / N=9=57 reject.
> - **rev 6 (2026-05-08)**: 5차 self-review 반영 — Critical 2건 / Major 3건. (1) **§5.6 cylinder cascade op count 산수 오류 정정**: "7 op" → 8 op (chain N=2 산식 3+2N+(N-1)=8 과 일치). (2) **D8 quota batch 경로 매커니즘 명시** (Critical) — rev 5 의 "single/batch 모두 동일" 표현이 batch 경로 (C# ApplyOperations → F# dispatchBatchOp) 에서 *불성립* → C# helper 메서드 미경유. batch 경로는 `ApplyOperations` 가 dispatch 진입 *전* inputs walk 하며 helper op 별 cascade op 수 사전 합산 차감 (§3.1 D8 ② 신설). (3) **§5.2 caller 갱신 정책 단일화** — rev 5 의 두 안 (caller 직접 default / wrapper 기본값) 혼재 해소 → wrapper 기본값 방식 채택 (Mermaid/CSV caller 무수정). (4) **D6 single tool 호출 의미론 명시** — single helper 호출 시 ref 등록 skip + 반환 JSON 에 GUID 직접 노출 / single add_call 은 GUID only / batch 만 ref 형태 허용. (5) **dispatchBatchOp 시그니처 변경의 caller 누적 정책 명시** — list 누적 + 중복 ref 명 BATCH\_ERROR + primitive op 단원소 wrapper.
> - **rev 7 (2026-05-08)**: 5명 reviewer 교차검증 메타리뷰 반영 — Critical 4건 / Major 2건 (Critical 5건 중 C4 는 rev 6 에서 이미 처리, C5 Active/Passive 분리는 사용자 confirm 사항으로 보존). (1) **C1 §9.7 stale 정정** — 직전 revision 의 "옵션 b (InternalsVisibleTo) 권장" 이 §4.2 rev 4/5 결론 ("internal wrapper 신설") 과 충돌 → §9.7 전면 재작성, §4.2 / §5.2 결론을 단일 SSOT 로 통합. (2) **C2 F# `let private` 가시성 + fsproj ProjectReference 부재** (검증된 Critical) — `module internal ImportPlanDeviceOps` + 핵심 함수들 `let private` 직접 확인 → InternalsVisibleTo 만으로 노출 *불가*. 추가로 `Ds2.LlmAgent.fsproj` ProjectReference 가 `Ds2.Editor` 단일 → `Ds2.Core` 직접 참조 부재 검증. wrapper 신설 시 `let internal` 강제 + fsproj 에 `<ProjectReference Include="..\Ds2.Core\Ds2.Core.fsproj" />` 추가 의무 §5.2 / §9.7 명시. (3) **C3 caller 경로 정정** — `Solutions/Core/Ds2.Core/Store/Mermaid/...` / `Csv/...` 는 phantom, 실제 = `Solutions/Convert/Ds2.Mermaid/Import/Targets/{MapperTargetPlanning,MapperTargets}.fs` + `Solutions/Convert/Ds2.CSV/CsvMapper.fs`. §5.2 / §8 / §4.2 caller 영향 점검 의무 모두 갱신. (4) **M7 llm-samples phantom 제거** — `Solutions/Core/Ds2.LlmAgent/llm-samples/` 폴더 자체 부재 (`ls` 검증). §5.5 항목 삭제. (5) **M10 §4 절 제목 모순 해소** — "추가 작업 불필요" 가 §4.2 본문의 "Core 시그니처 변경 4건" 과 충돌 → "plan-level DU/applyDirect 추가는 불필요, ImportPlan.Device.fs 내부 수정 4건은 필요" 로 정정. (6) **M4 invalidOp line 인용 caveat** — `:168/183/191/312` 일부가 주석일 가능성 → 진입 시 grep 분리 의무 §8 명시.
> - **rev 8 (2026-05-08)**: prompt review 산출 반영 (별도 review 세션 — `Apps/Promaker/Promaker/LlmAgent/Prompts/*.md` greenfield modeling 지침 타당성 평가). (1) **§5.5 prompt 갱신 sub-bullet 구체화** — 3.tooling.md 의 9-op 예시가 `add_system "Cyl"` (isActive 미명시 → 기본 true → Active 로 등록) 로 §2 룰 A 위반. todo 적용 시점에 `add_passive_system "Cyl"` + `add_active_system "Controller"` 분리 형태로 재작성 필요 명시. (2) **§5.5 에 csproj `ds.md` 제외 행 추가** — `Apps/Promaker/Promaker/Promaker.csproj` 의 `<EmbeddedResource Remove="LlmAgent\Prompts\ds.md" />` 추가 (EV2 구버전 SequenceControlSubmodel AASX 트리 + KPI — ds2 entity 모델과 plural 컬렉션 표기 충돌 + greenfield modeling 직접 무관 + token 부피 기여만 있음). 본 rev 작업 시점에 csproj / prompt 양쪽 즉시 적용 (helper 도입 미관계, 즉시 가치). (3) **2.modeling.md 룰 D vs §4.2 노트 모순 해소** — "ADV 만 사양 시 RET 자동 동반" 권고가 §2 룰 D (추측 금지) 와 직접 충돌. rev 8 작업에서 룰 D 우선으로 정정 (사용자 confirm 시에만 동반). (4) **2.modeling.md 매핑표 / §3.5 / §4.1 / §4.2 / §4.4 의 "Passive 안 Flow/Work" 표기 일관화** — GUI canonical (WithCyl.json) 의 형태와 현 phase 도구 한계를 동시에 명시: 매핑표 행은 GUI canonical 표시 + "현 phase 직접 생성 불가, 후속 helper 영역" cross-link. §4 표준 묶음 예시들은 "Passive 내부 cascade 생략" 명시. (5) **3.tooling.md 에 "본 phase 의 도구 범위 — 후속 확장 예정 entity" 절 신설** — IOTag / ValueSpec / CallCondition / TokenSpec / HwButton·Lamp·Cond·Action / SubmodelProperty 32 / ApiDefActionType / ApiDef.TxGuid·RxGuid / Work.TokenRole·Duration·ReferenceOf 가 ds-entities.md 에 정의되어 있으나 본 phase 도구 부재 — "도구 부재 = 영구 범위 밖" 이 아니라 "본 phase 직접 작업 범위 외" 안내. **본 절은 todo 의 후속 phase 확장 trigger 와 정렬** — IOTag / CallCondition / SubmodelProperty 등을 노출할 후속 todo 가 진입 시 본 절 갱신 의무. (6) **2.modeling.md self-check 항목 보강** — apiName ↔ ApiDef.Name 일치 검사 / Passive 내부 Flow/Work 미생성 검사 / opposing 임의 동반 금지 검사 / 도구 외 entity 안내 검사 4 항목 추가.
> - **C5 (Active/Passive 분리 over-engineering 가능성)** — Design reviewer 단독 제기. 사용자 confirm 사항으로 보존 (§3.0 권한 표 ⚙️ 결정이라 변경 가능하나 단일/분리 비교 분석은 별도 작업). 본 rev 에서 분리 정책 그대로 유지.
> - **rev 9 (2026-05-08)**: prompt 파일 rename — LLM 학습 순서 (foundation → rules → tools) 자연화 위해 `ds-entities.md → 1.entities.md` / `2.Model.md → 2.modeling.md` / `1.SystemPrompt.md → 3.tooling.md`. 본 rev 본문 + rev 1~8 본문의 prompt 파일 인용도 모두 새 path 로 일괄 갱신 (historical 시점 인용은 git log 로 추적). canary trigger 의 `pong:` 메시지도 새 파일명으로 동기 갱신. 외부 repo 참조 (`/f/Git/kwak/kwak/DsConcepts/ds-entities.md`) 는 변경 없음. csproj `*.md` glob 자동 매칭이라 csproj 변경 불필요.
> - **rev 11 (2026-05-08)**: rev 10 산출물 자체에 대한 외부 reviewer 점검 (8건 정확) 반영 + 사용자 confirm 1건 (D7 default 변경). (1) **§2.1 절대 룰 4개 / self-check 8 항목 stale 정정 (Major)** — 실측 룰 5개 (A/B/C/D/E, E = ArrowBetweenWorks 같은 Flow 안), self-check 16 항목 (rev 8 보강 후). (2) **D2 clamp ApiDef 이름 SSOT 충돌 정정 (Critical)** — todo D2 의 `clamp=["CLAMP","UNCLAMP"]` ↔ `2.modeling.md` §3.3 line 174 의 `CLP, UNCLP` 충돌. **rev 8 본문이 "LLM 학습 SSOT = 2.modeling.md §3.3 표" 라 명시했으므로 todo 측이 stale** → todo D2 + Tier 1 helper default 모두 `["CLP","UNCLP"]` 로 정정. (3) **D7 robot opposing default `"chain"` → `"none"` 변경 (🔒 사용자 confirm)** — 도메인 룰 (`2.modeling.md` §3.3 line 181 "ROBOT opposing 없음" / §4.3 line 356 "ResetReset 안 만듭니다") ↔ todo 도구 default `"chain"` 의 의미 충돌. ROBOT 의 HOME / WORK1 / WORK2 는 *순차 동작이지 상호 배타가 아님* — chain default 시 silent over-modeling. **사용자 confirm: (a) D7 default `"none"` 채택, 도메인 룰 우선**. chain/all-pairs 는 사용자가 명시 시에만. cylinder/clamp 는 N=2 + (CLP,UNCLP) opposing 짝이 분명해서 helper 내부에서 ResetReset 강제 (D7 영향 없음). (4) **Tier 0 시그니처에 `add_project(name)` 누락 정정 (Major)** — `3.tooling.md` line 40 의 11-op 예시가 `add_project` 사용 중인데 todo §3.2 Tier 0 표기가 빠져있어 unchanged primitive 임이 불명확. unchanged primitive 로 명시. (5) **§5.5 의 `Promaker.csproj` ds.md exclude phantom 정정 (Major)** — rev 8 본문 "본 rev 작업 시점에 즉시 적용 완료" 표기는 *허위*. 실측 (`find /f/Git/ds2/feature-llm`): ds.md 파일이 어디에도 없고 csproj 에는 CLAUDE.md remove 만 있음. rev 8 작성자가 phantom 파일을 잘못 인용 → §5.5 해당 행 자체 삭제. (6) **§5.5 sub-bullet ① "9-op Cylinder 예시" wording 명확화 (Minor)** — rev 10 §1 표는 정정됐으나 sub-bullet ① 의 "9-op" 표기는 "현재 11-op → helper 후 9-op 형태 (project 포함, Active+Passive 분리)" 로 의미 보강. (7) **§5.5 cross-todo sync `todo-promaker-hmi-llm-agent.md` line 보강 (Major)** — 직전 revision 의 line 인용 `:26,131,253,319` 중 line 23 (`[McpServerToolType] 11개 tool`) 누락 + line 286 (`hmi tool 11~15`) 누락 + line 132 (`SystemPrompt.cs Phase1c stale` — 현재는 PromptLoader 가 외부 .md 로드) 누락 + line 320 (SystemPrompt.cs PromptLoader 변경) 누락 + line 319 오지정 (PromakerToolNames.cs hmi tool 이름 추가는 21 갱신 영역 아님). 정확한 인용으로 정정. (8) **§5.1 호환성 invalidate 절에 `todo-promaker-llm-agent.md:495` ProjectReference 표기 cross-link 추가 (Major)** — Phase 1 시점 기록 "Ds2.LlmAgent ProjectReference = Ds2.Editor only 충분" 은 본 todo §5.2 §9.7 의 Critical 2 (`Ds2.Core` 직접 참조 추가 의무) 와 충돌. 역사 기록은 보존, "Phase 1 한정 — extend MCP 에서 invalidate" 주석 추가. (9) **§9.3 / §5.6 robot all-pairs quota 시나리오 — D7 default `"none"` 변경 영향 동기 갱신** — chain default 폐기로 helper 호출 시 LLM 명시 없으면 ResetReset 0 개. all-pairs 트립 시나리오는 사용자가 *명시적 선택* 시 한정.
> - **rev 10 (2026-05-08)**: prompt 4 파일 (`{1.entities,2.modeling,3.tooling,CLAUDE}.md`) 재독 후 todo 정합성 점검 산출 — 4건 정정 + self-check 의미 반전 wording 확정. (1) **§5.5 표에 `1.entities.md` 행 신설 (Critical)** — 직전 revision 의 §5.5 prompt 표는 `3.tooling.md` / `2.modeling.md` 만 다루고 `1.entities.md` 가 누락 → helper 도입 후 §4.6 ApiDef 표 ("현 phase MCP 도구는 Tx/RxGuid 인자를 노출하지 않으며 ...") 와 §6 "GFM 범위 외" 의 `ApiDef.TxGuid / RxGuid` 항목이 stale 그대로 잔류 위험. 본 rev 에서 행 신설 + sub-bullet 명시. (2) **§5.5 의 3.tooling.md sub-bullet ④⑤⑥ 추가 (Major)** — 직전 revision 의 sub-bullet ①②③ 외에 추가 갱신 영역 누락: ④ line 59-62 "operations 배열에서 지원되는 op" 목록 (`add_system` 제거 + 6종 추가), ⑤ line 75-86 "단일 mutation 도구 (legacy)" 절 도구 행 갱신, ⑥ line 81 `add_api_def(name, systemId)` → `add_api_def(name, systemId, txWorkId?, rxWorkId?)` 시그니처 노출. (3) **§1 op 수 감소 표기 정정 (Major)** — 직전 revision 의 "cylinder 1개 케이스 8 → 4 수준 / robot N=4 케이스 4 → 1" 표기는 *어디 기준 추정* 인지 불명. 실측 (3.tooling.md line 38 의 11-op 예시 + helper 후 §3.3 의 9-op 예시 기준) 으로 정정 — Cylinder 1개 (project 포함): 11 → 9 / Robot N=4 (Passive 측만): 13 → 1. 표 형태로 명시. (4) **§5.5 의 2.modeling.md sub-bullet ⑤ self-check 의미 반전 wording 확정 (사용자 confirm)** — 직전 revision 의 "Passive 내부 cascade 가 helper 로 생성되었는가" 가 (1) 항목 제거 / (2) 의미 반전 (helper 사용 강제) 둘 중 어느 쪽인지 불명. **사용자 confirm: (2) 의미 반전 채택 — 단 이미 store 에 존재하는 Passive System 을 *재사용* 하는 경우는 helper 호출 불필요 (primitive `add_call` 로 기존 ApiDef.Id 참조 가능)**. 즉 self-check 항목은 "*새* 디바이스 인스턴스 등장 시 helper 사용 / 기존 인스턴스 재사용 시 primitive 그대로" 양분 형태로 wording.
> - **rev 12 (2026-05-08)**: L1 (F# Core) 작업 진입 + 외부 reviewer 4건 review 처리 반영. (1) **L1.a `Ds2.LlmAgent.fsproj` 에 `Ds2.Core` 직접 ProjectReference 추가** — InternalsVisibleTo 효력화. CLAUDE.md L19 "ProjectReference = Ds2.Editor only" 표기 동시 갱신. (2) **L1.b `ImportPlan.Device.fs` 변경 4건 적용** — `WiringMode` DU 신설, `ensureSystem`/`ensurePendingWork` 시그니처 변경 (systemType / workDuration option 인자), `buildWorkArrowsBy` 공통화 (`buildWorkArrows` + `buildWorkArrowsAllPairs` 가 wrapper, reviewer #1 권고 수용 — 30 line 중복 제거), `buildPassiveDeviceCascade` 신설 (`let internal`). (3) Mermaid/CSV caller 무수정 정책 단일화 — `linkCallsToDevicesWithState` 내부에서 default 값 (None / Some 500ms) 채워 전달. (4) **D9 신설 (사용자 confirm)** — helper 의 동명 PassiveSystem 충돌 시 `invalidOp` (BATCH_ERROR). reviewer #2 의 silent ignore 우려 차단. ensureSystem 자체는 변경 안 함 → Mermaid/CSV idempotent 호환 보존, strict 검사는 `buildPassiveDeviceCascade` 진입점 한정. (5) `NoneMode` 명명은 reviewer #4 권고 거부 — F# Option.None 패턴 매칭 ambiguity 회피 + opposing `"none"` 1:1 대응 + todo §5.2 합의 명명. (6) **L2 미진입** — 다음 세션이 §7.3 의 L2 의존성 그래프 (queueAddActiveSystem/queueAddPassiveSystem 분리 → queueAddCall 시그니처 변경 → helper 4종 → dispatchBatchOp 갱신 → invalidOp 메시지 → D8 quota mechanism) 부터 진입.
> - 다음 리비전 trigger: 사용자가 결정 1~5 변경 / 본 todo 가 phase 진입 / `2.modeling.md` 영속화 후 / WithCyl.json SSOT 옵션 결정 후 / Core ensureSystem/ensurePendingWork 시그니처 변경 caller 영향 최종 확정 후 / **C5 Active/Passive 단일 vs 분리 비교 결정 후** / **rev 8 의 "본 phase 도구 범위 — 후속 확장 예정 entity" 목록의 IOTag / CallCondition / SubmodelProperty 등을 다룰 후속 todo 진입 시점** / **rev 10 의 §1 op 수 감소 실측치 — 실제 helper 도입 후 LLM 발행 op 수 측정 (Pass 5 baseline) 으로 추정치 → 측정치 갱신 시점**.

---

## 0. 선결 조건 (작업 진입 전 *반드시*)

본 절의 항목 중 하나라도 미충족 시 본 작업 진입 금지. 미충족 상태로 빌드는 통과하나 *silent degradation* 발생 (도메인 룰 부재로 LLM 회귀).

### 0.1 `2.modeling.md` worktree 영속화 (git add + commit, "track")

- 현재 git status 상 `Apps/Promaker/Promaker/LlmAgent/Prompts/2.modeling.md` 가 untracked. 다른 머신/clean clone 에서 SSOT 부재.
- **silent degradation 메커니즘**: `Apps/Promaker/Promaker/Promaker.csproj:59` 의 `<EmbeddedResource Include="LlmAgent\Prompts\*.md" />` 가 glob → worktree 에 파일이 없으면 임베딩 안 됨. `Apps/Promaker/Promaker/LlmAgent/PromptLoader.cs:48-55` 의 baseline 검사가 "≥1개 매치" 통과로 끝나 *빌드 실패 아님* — LLM 이 도메인 룰을 모르는 채 동작 (회귀 silent).
- 사용자 규칙상 임의 git commit 금지 → 작업 진입 시 사용자에게 **git add + git commit 권한 요청** 후 진행. *권한 요청 형태는 환경 의존*: 본 사용자 환경 (`~/.claude/CLAUDE.md`) 에는 `--git-add` / `--git-commit` 플래그가 정의되어 있어 활용 가능하나, 다른 환경/세션에서 본 todo 만 보고 진입할 경우 해당 플래그가 부재할 수 있음 — 그 경우 `git add <path>` + `git commit -m "..."` 일반 명령으로 사용자에게 confirm 후 실행.
- 4 파일 (`{1.entities,2.modeling,3.tooling,CLAUDE}.md`) 모두 *맨 앞* 에 canary HTML 주석 (`<!-- canary: ... -->`) 이 삽입되어 있음. **canary 줄을 절대 제거하지 말 것** — 진단 도구로 계속 사용 (M7 PromptCanaryTests 와 동기). 다음 세션 sanity check: `Get-Content -TotalCount 1 <path>` (PowerShell) 또는 `head -1 <path>` (bash) 로 4 파일 첫 줄 canary 확인.

### 0.2 `HelperGuiParityTests` fixture 의 repo 내 이전

- 본 todo §5 의 `HelperGuiParityTests.fs` 는 `F:\tmp\WithCyl.json` 을 fixture 로 사용하는데 이는 *작성자 머신 절대경로* — 다른 머신/CI 에서 fixture missing → 테스트 RED.
- **현재 상태 (rev 4 검증)**: `Solutions/Core/Ds2.LlmAgent/doc/WithCyl.json` 이 *이미* untracked 로 존재 (`F:\tmp\WithCyl.json` 과 동일 해시). git status 의 `?? llm-samples/` 옆 `?? WithCyl.json` 으로 확인.
- **SSOT 결정 필요** (사용자 확인 권장):
  - **옵션 A**: `Solutions/Core/Ds2.LlmAgent/doc/WithCyl.json` 을 그대로 git track. 단점 = doc 폴더가 fixture 보관 위치가 아님 (의미 불일치).
  - **옵션 B (권장)**: `Solutions/Tests/Ds2.LlmAgent.Tests/Fixtures/WithCyl.json` 으로 git mv 한 후 track. test fixture 의미상 정합. 본 todo §5.6 의 `HelperGuiParityTests.fs` 도 이 경로 참조.
  - 어느 옵션이든 `F:\tmp\WithCyl.json` 절대경로 의존은 제거.
- **사전 작업**: 옵션 결정 후 git mv (또는 add) + commit. 본 작업 §5 의 test 작성 시 fixture 경로는 `Path.Combine(AppContext.BaseDirectory, "Fixtures", "WithCyl.json")` (옵션 B) 또는 동등.

---

## 1. 작업 목표

Promaker MCP server 를 다음과 같이 재편하여 LLM 이 `2.modeling.md` 의 도메인 룰을 더 정확하고 적은 op 수로 충족하게 한다.

1. 현 `add_system(name, isActive)` 를 **`add_active_system` / `add_passive_system` 로 분리**.
2. **Device-class helper 4종 신설** (`add_cylinder` / `add_clamp` / `add_robot` / `add_device`) — **PassiveSystem + Flow + Work + ApiDef + ResetReset Arrow** cascade 를 1 op 로 (rev 4 정정: ApiCall binding 은 helper *외부* 책임 — 후속 `add_call` 이 LLM 의 D6 ref-required 작명으로 binding. helper 가 ApiCall 까지 만들면 D6 의 "외부 ref 명명 contract" 의도 ↔ ApiCall 자동 생성이 충돌).
3. **`add_call` 시그니처 단순화** — `(workId, apiDefId)` 2 인자만. devicesAlias / apiName 은 ApiDef → ParentSystem.Name / ApiDef.Name 으로 자동 도출.
   - **룰 B (alias = Passive.Name)** = *구문상* 차단 (alias 인자가 사라짐).
   - **룰 C (ApiDef 는 Passive 의 자식)** = *런타임* 차단 (도구 내부에서 `ApiDef.ParentSystem.IsActive == false` 검증 — ApiDef 가 Active System 자식인 경우 BATCH_ERROR).
4. **ApiDef ↔ Work binding (TxGuid/RxGuid)** 인자 노출 (helper 가 자동 채움, primitive 만 쓰는 경우 LLM 명시).
5. helper 가 만든 ApiDef 의 Id 를 batch ref table 에 추가 등록 (multi-output ref). decision 6 (strict required) 정책.

op 수 감소 효과는 정량 baseline 없음 — 실측치 (3.tooling.md line 38 의 11-op cylinder 예시 + helper 후 §3.3 의 9-op 예시 기준):

| 케이스 | helper 도입 전 | 도입 후 | 감소 |
|---|---|---|---|
| Cylinder 1개 (project 포함, Active+Passive 분리) | 11 op | 9 op | -2 |
| Cylinder 1개 (project 제외) | 9 op | 8 op | -1 |
| Robot N=4 (Passive 측 cascade 만 — passive_system 1 + flow 1 + work×4 + api_def×4 + arrow×3 chain) | 13 op | 1 op (`add_robot`) | -12 |

device 다수 batch 일수록 helper 절감 ↑ (Active 측 Flow/Work/Call/Arrow 는 helper 도입과 무관하게 동일 op 수). **실 helper 도입 후 LLM 발행 mcp tool call 수 측정 (Pass 5 baseline 비교) 시점에 본 표를 측정치로 갱신**.

---

## 2. 배경 / 맥락

### 2.1 시작점 — 사용자가 `2.modeling.md` 새로 작성

`Apps/Promaker/Promaker/LlmAgent/Prompts/2.modeling.md` (untracked, §0 참조) 가 도메인 룰 (사양 → Ds2 모델 분해) 단일 진실 원천으로 추가됨.
- §1 핵심 매핑표 — 현장 어휘 (실린더/로봇/컨베이어) → Ds2 entity (Project/DsSystem/Flow/Work/Call/ApiDef/Arrow).
- §2 절대 룰 5개 (A: Passive 선행 / B: devicesAlias 일치 / C: ApiDef 는 Passive / D: 추측 금지 / E: ArrowBetweenWorks 는 같은 Flow 안의 두 Work 사이로만).
- §3 분해 절차 (Active/Passive 식별, deviceType→ApiDef 매핑표, opposing 쌍 ResetReset, §3.4a Flow vs Work 결정 트리, §3.4b capacity 어휘 인식, §3.7 RGFH FSM + capacity-2 cycle 4-arrow 패턴).
- §4 표준 묶음 예시, §5 self-check 16 항목 (rev 8 보강 후 — apiName ↔ ApiDef.Name 일치 / Passive 내부 cascade 정책 / opposing 동반 금지 / 도구 외 entity 안내 4 항목 추가).

### 2.2 코드베이스 검증 결과

- `DsSystem.SystemType : string option` 이 **이미 존재** (`Solutions/Core/Ds2.Core/Entities.fs:39`). 즉 deviceType 저장 필드는 신규 추가 아님 — `add_system` 도구가 인자로 안 받았을 뿐.
- **`isActive` 는 System 속성이 아니라 Project↔System link 의 속성** (`ToolOperations.fs:204` `LinkSystemToProject(projectId, sysId, isActive)`).
- `DevicePresets.KnownNames` (`Solutions/View/Ds2.View3D/Ds2.View3D.Core/Types.fs:133-153`) — **19종** 표준 deviceType (Robot, Conveyor, Unit, AGV, Gripper, Lifter, Crane, Stacker, Sorter, Transfer, Barrier, Door, Gate, Elevator, Hoist, Pusher, Rotary, Turntable, Tilter). 3D view 자동 매핑 부산물.
- **AddApiCall DU + ImportPlanDeviceOps 가 이미 존재** (§4 검증 결과 참조).

### 2.3 `F:\tmp\WithCyl.json` 분석 — Promaker GUI 의 cylinder default cascade

Promaker GUI 에서 work 안에 Call 만들면 자동으로 device 가 cylinder 로 생성되며, 다음 형태:

```
PassiveSystem  "<flowName>_<devAlias>"   systemType="Unit"     ← 일반형 ({flow}_{devAlias})
├─ Flow         "<devAlias>_Flow"
│   ├─ Work     localName="ADV"     duration=500ms
│   └─ Work     localName="RET"     duration=500ms
├─ ApiDef       "ADV"               TxGuid=RxGuid=Work(ADV).Id
├─ ApiDef       "RET"               TxGuid=RxGuid=Work(RET).Id
└─ ArrowWork    ADV→RET             arrowType=4 (ResetReset)
ActiveSystem.Work.Call "cyl.ADV"
└─ apiCalls: [{ apiDefId=ApiDef(ADV).Id, name="cyl.ADV", originFlowId=... }]
```

핵심 발견:
- **`SystemType="Unit"`** — cylinder/clamp 등 simple 2-state device 의 표기.
- **ApiDef.TxGuid / RxGuid 가 동일 Work.Id 로 binding** — ApiDef 가 자기 실행 Work 와 묶임.
- **Call 안의 ApiCall 이 apiDefId 로 ApiDef.Id 직접 GUID 참조**.
- arrowType=4 = ResetReset (enum: Unspecified=0, Start=1, Reset=2, StartReset=3, ResetReset=4, Group=5).
- naming convention: PassiveSystem `{flow}_{devAlias}`, internal Flow `{devAlias}_Flow`, Work.localName = ApiDef.name.
- ApiCall 의 `originFlowId` = caller Active Flow.Id (Call → 호출 출처 Flow 역참조).

---

## 3. 확정된 결정 + 설계

### 3.0 terminology / 권한 표

**용어 통일** (이전 revision 의 표현 혼용 정정):
- "device-class helper" = "Tier 1 helper" = "helper 4종" → 본 revision 부터 **"Tier 1 helper"** 로 통일.
- "decision 6 strict" = "strict required" = "ref required" → **"D6 ref-required"** 로 통일.
- "Active/Passive System" 표기 = 마크다운 본문 PascalCase, 코드/식별자 = camelCase 또는 snake\_case 그대로.

**결정 권한** (변경 시 절차):
- 🔒 **사용자 명시 결정** (변경 시 사용자 confirm 필요): D6, D7.
- ⚙️ **Claude 추정 + 사용자 묵시 동의** (객관적 근거 있으면 변경 가능, 변경 시 본 todo §3 갱신 + commit message 에 표기): D1, D2, D3, D4, D5, D8.

### 3.1 결정 표 (총 9개 — rev 12 D9 추가)

| # | 권한 | 결정 |
|---|---|---|
| 1 | ⚙️ | deviceType: cylinder/clamp/lifter 모두 일단 `"Unit"`. robot=`"Robot"`, conveyor=`"Conveyor"`. ※ KnownNames 에 `"Lifter"` 가 별도 존재하므로 dual-axis (추상 분류 vs 3D 매핑) 분리 가능성은 *후속 todo* (별도 `todo-devicetype-axis.md` 분리 권장. 분리 trigger = 본 작업 완료 후 사용자 명시 진입). **forward-compat 정책**: 후속 dual-axis 도입 시 `deviceType` 인자 의미는 *KnownNames axis* 로 고정 — 추상 분류는 *추가 인자* (예: `deviceClass?`) 로만 확장. 본 작업의 helper 시그니처 안정성 보장 |
| 2 | ⚙️ | default apiNames: cylinder=`["ADV","RET"]`, clamp=`["CLP","UNCLP"]`, lifter=`["UP","DOWN"]` (lifter 는 helper 별도 안 두고 add\_device 로 처리). **rev 11 정정 (Critical)**: 직전 revision 의 clamp=`["CLAMP","UNCLAMP"]` 는 `2.modeling.md` §3.3 line 174 의 `CLP, UNCLP` 와 SSOT 충돌. 본 행 자체에 명시되어 있는 "**LLM 학습 SSOT 는 2.modeling.md §3.3 표**" 정책에 따라 todo 측이 stale → CLP/UNCLP 채택. default 는 도구 description level 의 fallback 일 뿐, 두 곳 동기화 필수. (사용자가 사양에 "CLAMP/UNCLAMP" 명시하면 룰 D 따라 그대로 사용 — default override.) |
| 3 | ⚙️ | ApiCall.apiDefId binding = explicit GUID. apiName 문자열 매칭 fallback 금지 |
| 4 | ⚙️ | ref name unique 검증 = LLM 책임 (의도 차원). 단 도구 측에서 queueBatch 의 BatchOpInput list 검증이 1차 중복 차단 — 즉 strict 검증은 결국 도구 측 책임이고 LLM 은 의도적 unique 부여 |
| 5 | ⚙️ | workDuration default: cylinder/clamp 500ms, robot/conveyor omit (None) |
| **6** | 🔒 | **ApiDef ref 누락 = error (D6 ref-required)**. helper 의 `apiDef*Ref` 인자 / `add_call` 의 `apiDefId` 인자 모두 *required* (omit 시 BATCH\_ERROR). <br/>**의도** (rev 3 본문 강화): helper 가 cascade 안에서 *생성하는* ApiDef 의 **외부 ref 명명을 LLM 이 미리 작명하도록 강제** — 후속 add\_call 의 `apiDefId: "@<ref>"` 가 동일 ref 로 ApiDef 를 가리킬 수 있게 함. ref 미등록 시 후속 add\_call 이 다른 device 의 ApiDef 를 잘못 가리키는 silent miscompile 가능 (§9.2 시나리오). <br/>**rev 6 추가 — single tool 호출 시 의미론**: D6 의 "ref required" 는 인자 omit 금지 (값 자체 required) + batch context 에서 ref table 등록 강제 *둘 다* 의미. <br/>① **single tool 호출 (helper)**: `apiDef*Ref` 인자 omit 시 BATCH\_ERROR (값 required 부분만 적용). 단 single 경로에서는 batch ref table 부재 → 등록 의미 없음. **권장 정책**: helper single 호출 시에도 인자는 받되 ref 등록 skip + 반환 JSON 에 `apiDefIds: { <ref>: <guid> }` 형태로 직접 GUID 노출 → caller (LLM) 가 후속 add_call 호출 시 GUID 로 직접 참조 가능. <br/>② **single tool 호출 (add_call)**: `apiDefId` 인자는 GUID 만 허용 (ref 표현 무의미). batch 안 add_call 만 `"@<ref>"` 형태 허용. <br/>③ **batch context**: 기존 정책 그대로 — apiDef\*Ref / apiDefId 모두 ref 형태 또는 GUID. ref 형태 시 ref table 등록/조회. |
| **7** | 🔒 | **robot 의 ResetReset 위상 인자**: `opposing: "none" \| "chain" \| "all-pairs"`, **default `"none"` (rev 11 정정 — 사용자 confirm)**. <br/>**rev 11 정정 사유**: 직전 revision 의 default `"chain"` 은 `2.modeling.md` §3.3 line 181 ("ROBOT opposing 없음 — HOME 은 일반적으로 입력만") + §4.3 line 356 ("ROBOT 은 §3.3 표대로 opposing 없음 — ResetReset 안 만듭니다") 와 의미 충돌. ROBOT 의 HOME / WORK1 / WORK2 는 *순차 동작이지 상호 배타가 아님* — CYLINDER(ADV/RET) 처럼 "둘 중 하나만 활성" 의미가 부재. chain default 시 LLM 이 robot helper 호출 → 의미 없는 N-1 ResetReset arrow 자동 생성 → silent over-modeling. **(a) 도메인 룰 우선** = `"none"` default 채택 (사용자 confirm — 2026-05-08). <br/>**선택지 의미**: <br/>　• `"none"` (default) — ResetReset 안 만듦. ROBOT 표준 / CONVEYOR.MOVE 단일 / PUMP 등에 부합. <br/>　• `"chain"` — 사용자가 명시 시. ImportPlanDeviceOps.buildWorkArrows 의 pairwise 재활용 (Home→W1→W2→W3 = N-1 arrow). 의미 = "인접 차수만 상호 배타". 일반 ROBOT 에 부적합하나 사용자가 의도적으로 선택 가능. <br/>　• `"all-pairs"` — 사용자가 명시 시. C(N,2) 별도 로직 (quota cap 50 과 정량 충돌 가능 — 산식 §4.3 / §9.3). 의미 = "전 차수 상호 배타". 거의 사용 안 됨. <br/>**cylinder/clamp 는 N=2 + 분명한 opposing 짝 (ADV/RET, CLP/UNCLP)** 이라 helper 내부에서 ResetReset 1 개 강제 생성 — D7 영향 없음 (`opposing` 인자 자체를 시그니처에 노출 안 함). robot/add_device 만 노출. |
| **9** | 🔒 | **helper 의 동명 PassiveSystem 충돌 정책 (rev 12 신설 — 사용자 confirm)**: `buildPassiveDeviceCascade` 진입 시 `Queries.passiveSystemsOf projectId store` 에서 동명 매칭이 *나타나면* `invalidOp` (LlmAgent 측 BATCH_ERROR 로 변환). <br/>**의도**: helper 는 *신규 device 생성* 책임만 짐. 기존 device 재사용 시나리오는 LLM 이 사전에 `find_by_name`/`list_systems` 로 조회 후 primitive `add_call` 로 기존 ApiDef.Id 참조 — `2.modeling.md` self-check rev 10 wording (사용자 confirm) 의 *런타임 강제*. <br/>**범위 제한**: ensureSystem 자체는 변경하지 않음 → Mermaid/CSV import 의 idempotent 재실행 (같은 device 가 여러 행에 등장 시 기존 인스턴스 재사용) 호환 보존. strict 검사는 `buildPassiveDeviceCascade` (helper 진입점) 안에서만 작동. <br/>**reviewer #2 (rev 11 review) 의 silent ignore 우려 차단** — existing 분기에서 SystemType 덮어쓰지 않는 동작이 LLM helper context 에서는 silent miscompile 위험이었음. 본 결정으로 helper context 만 strict, Mermaid/CSV 무영향. |
| **8** | ⚙️ | **helper cascade op 의 quota 합산 정책 (rev 3 신설, rev 4·5·6 정정)**: 현행 `LlmTurnContext.cs:24` `MutationQuota = 50` + `MutationCallCount` 누적. **`IncrementMutationCount(int delta = 1)` 가 `LlmTurnContext.cs:50` 에 *이미 존재*** (review C1 주석에 batch 우회 DoS 표면 명시). batch tool 은 `ModelTools.cs:163` 에서 `inputs.Length - 1` delta 추가 차감 (RunMutation 이 +1 한 후) — 동일 패턴을 helper 에 적용. <br/>**rev 5 정정 (Critical 1)**: F# `ToolOperations.fs` 가 C# `Promaker.LlmAgent.LlmTurnContext` 를 *참조 불가* (assembly 의존 방향: Promaker → Ds2.LlmAgent). 따라서 quota delta 차감은 **C# `ModelTools.cs` 의 helper 메서드 안에서** 처리해야 함 (F# 측 ToolOperations 함수는 quota 무관). <br/>**rev 6 정정 (Critical 2)**: rev 5 의 "single helper / batch 안 helper 모두 동일" 표현이 batch 경로에서 *불성립* — batch 경로는 C# `ApplyOperations` (apply\_operations tool) → F# `dispatchBatchOp` 로 진행되어 **C# helper 메서드 (`AddCylinder` 등) 가 호출되지 않음**. 따라서 batch 안 helper 의 quota 차감은 별도 매커니즘 필요. <br/>**채택 정책 (rev 6 최종)**: <br/>① **single helper 호출 경로**: helper 진입점 (예: `ModelTools.AddCylinder` 등) 이 *RunMutation 진입 직후* helper 종류별 cascade op 수 사전 계산 → `ctx.IncrementMutationCount(cascadeOpCount - 1)` 추가 차감 (RunMutation 이 +1 했으므로). <br/>② **batch 경로 (apply\_operations)**: `ModelTools.cs:163` 의 batch 진입점이 `inputs.Length - 1` 차감 *후*, **inputs 를 walk 하며 helper op (`add_cylinder`/`add_clamp`/`add_robot`/`add_device`) 인 경우 op 별 cascade op 수 계산 → 추가로 `(cascadeOpCount - 1)` 차감** (이미 input 1개로 +1 카운트되어 있음). cascade op 수 산식 = `3 + 2N + (N-1)` (chain) / `3 + 2N + C(N,2)` (all-pairs) / `3 + 2N` (none). args 의 `apiNames.length` 와 `opposing` 인자로 결정. cylinder/clamp 는 N=2 / chain 고정 = 8 op. <br/>③ batch dispatch *시작 전* 사전 합산 차감 → 초과 시 dispatch 진입 전 `QuotaExceededException` throw → BATCH\_ERROR (op[0] 이전 단계). 부분 적용 회피. <br/>cascade op 수 산식 = §4.3 의 `3 + 2N + C(N,2) (all-pairs)` 또는 `3 + 2N + (N-1) (chain)` 또는 `3 + 2N (none)`. |

### 3.2 최종 도구 시그니처

> **표기 규약**: `?` 없는 인자 = **required** (omit 시 BATCH\_ERROR — D6 ref-required 와 동일 stage).<br/>
> `?` 붙은 인자 = optional, default 표기 있으면 default 사용.<br/>
> `apiDef*Ref` / `apiDefRefs` 의 *길이 검증* 은 **dispatchBatchOp stage** (ref resolution 직전, D6 strict 검사와 동일 stage).

#### Tier 0 — primitives

```
add_project(name)                                   ★ unchanged   → ref = Project.Id
add_active_system(name)                                          → ref = System.Id
add_passive_system(name, deviceType)                             → ref = System.Id
add_flow(name, systemId)                                         → ref = Flow.Id
add_work(localName, flowId)                                      → ref = Work.Id
add_call(workId, apiDefId)                          ★ 2 인자       → ref = Call.Id
add_api_def(name, systemId, txWorkId?, rxWorkId?)                → ref = ApiDef.Id
add_arrow(sourceId, targetId, arrowType)                         → ref = Arrow.Id
remove_entity / rename_entity (현행 유지)
```

`add_project` 는 **시그니처 무변경** — `3.tooling.md` line 40 의 11-op 예시가 그대로 사용 (`{op:"add_project", ref:"p", args:{name:"M1"}}`). rev 11 정정으로 unchanged primitive 임을 명시 (직전 revision 표기 누락 — Major).

`add_call` 변경점 (C2 수용):
- 인자 4 → 2. `devicesAlias` 는 `ApiDef.ParentId` → `System.Name` 으로 자동 도출. `apiName` 은 `ApiDef.Name` 그대로.
- Call.Name 조립 (`{alias}.{apiName}`) 은 도구 내부에서 자동.
- 룰 B (alias=Passive.Name) = *구문상* 차단 (alias 인자 자체가 사라짐). 룰 C (apiDef 는 Passive 의 자식) = *런타임* 차단 (도구 내부 `ApiDef.ParentSystem.IsActive == false` 검증; 위반 시 BATCH\_ERROR).
- ApiCall 자동 cascade — `createAndRegisterApiCall` 패턴 (ImportPlan.Device.fs:152-160) 그대로 재활용.
- `originFlowId` 자동 도출 = `Work.Parent.Flow.Id` (workId 인자로부터 역추적). LLM 이 명시할 필요 없음.
- **Call.Name 충돌 검사 유지**: 시그니처가 자동 조립이지만 기존 `hasCallNameClash plan store workId fullName` 검사 정책 그대로 (같은 Work 내 동일 ApiDef 중복 호출 방지).

#### Tier 1 — device-class helpers

```
add_cylinder(name, apiDef1Ref, apiDef2Ref,
             apiNames? = ["ADV","RET"], workDuration? = 500ms)
add_clamp(name, apiDef1Ref, apiDef2Ref,
          apiNames? = ["CLP","UNCLP"], workDuration? = 500ms)   ★ rev 11: CLAMP→CLP, UNCLAMP→UNCLP (2.modeling.md §3.3 SSOT 정합)
add_robot(name, apiDefRefs, apiNames,
          opposing? = "none", workDuration?)         ★ rev 11: chain→none (도메인 룰 우선, 사용자 confirm) — apiDefRefs.length === apiNames.length 강제
```

각 helper:
- main `ref` = PassiveSystem.Id (외부 ref 메커니즘 그대로)
- 인자로 받은 apiDef\*Ref / apiDefRefs 는 *required* (D6 ref-required) — batch ref table 에 추가 등록
- internal: PassiveSystem(SystemType=deviceType) + Flow `{name}_Flow` + Work×N (duration) + ApiDef×N (TxGuid=RxGuid=Work.Id) + ResetReset Arrow
- ResetReset Arrow 위상: cylinder/clamp = pairwise (=C(2,2)=1, helper 내부 강제), robot = `opposing` 인자에 따라 (**rev 11 정정: default `"none"` = 0 arrow** — 도메인 룰 우선)
- **`opposing` 인자의 비대칭** (rev 3 정책 명시 / rev 11 default 변경): cylinder/clamp 는 N=2 고정 + 분명한 opposing 짝 (ADV/RET, CLP/UNCLP) 이므로 `opposing` 인자를 *노출하지 않음* — helper 내부에서 ResetReset 1 개 강제. robot 만 노출, **default `"none"`**. 후속 dual-axis 도입 시 cylinder 가 N>2 로 확장되면 시그니처 깨질 수 있음 — 그 경우 별도 todo 로 helper 시그니처 일괄 갱신.
- **PassiveSystem.Name = 사용자가 helper 에 넘긴 `name` 인자 그대로**. 자동 접미 없음 (§9.6). GUI default `{flow}_{devAlias}` 패턴과 다름 — `HelperGuiParityTests` 의 비교 범위는 *naming 외 cascade 구조 (Work/ApiDef/Arrow 갯수·관계) parity* 로 한정 (M11).

#### Tier 2 — generic fallback

```
add_device(name, deviceType, apiNames, apiDefRefs,
           opposing? = "none", workDuration?)        ★ rev 11: chain→none — apiDefRefs.length === apiNames.length 강제
```

opposing 값:
- `"none"` (**default — rev 11**) — ResetReset 안 만듦. Conveyor.MOVE 단일 / Pump / ROBOT 표준 등. 사용자가 명시 안 하면 이 값 적용 — 도메인 룰 (`2.modeling.md` §3.3) 의 "opposing 없음" 다수 케이스에 부합.
- `"chain"` — N-1 arrow, ImportPlanDeviceOps.buildWorkArrows 그대로. 사용자가 명시 시에만 (예: "각 차수 끝나면 직전 차수 reset" 의도 명시).
- `"all-pairs"` — C(N,2) arrow, 별도 로직 + D8 quota 사전 reject. 사용자가 명시 시에만 (거의 사용 안 됨, N≥9 부터 quota 50 초과).

### 3.3 사용 예 (D6 ref-required 형태)

```json
[
  {"op":"add_active_system", "ref":"ctl", "args":{"name":"Controller"}},
  {"op":"add_cylinder", "ref":"cyl1", "args":{
      "name":"Cyl1", "apiDef1Ref":"cyl1Adv", "apiDef2Ref":"cyl1Ret"}},
  {"op":"add_flow", "ref":"run", "args":{"name":"Run", "systemId":"@ctl"}},
  {"op":"add_work", "ref":"wAdv", "args":{"localName":"Adv", "flowId":"@run"}},
  {"op":"add_work", "ref":"wRet", "args":{"localName":"Ret", "flowId":"@run"}},
  {"op":"add_call", "args":{"workId":"@wAdv", "apiDefId":"@cyl1Adv"}},
  {"op":"add_call", "args":{"workId":"@wRet", "apiDefId":"@cyl1Ret"}},
  {"op":"add_arrow", "args":{"sourceId":"@wAdv", "targetId":"@wRet", "arrowType":"Start"}}
]
```

apiDef1Ref/apiDef2Ref/apiDefId 중 하나라도 빠지면 BATCH_ERROR.

robot 표준 케이스 (default `"none"` — rev 11 변경 후, 도메인 룰 부합):
```json
{"op":"add_robot", "ref":"rb1", "args":{
    "name":"RB1",
    "apiNames":["HOME","WORK1","WORK2","WORK3"],
    "apiDefRefs":["rb1Home","rb1W1","rb1W2","rb1W3"]}}
```
→ Work×4 + ApiDef×4 + ArrowBetweenWorks×0 (ResetReset 안 만듦). `2.modeling.md` §3.3 line 181 "ROBOT opposing 없음" 정합.

robot all-pairs 케이스 (사용자가 *명시적으로* opposing 선택 시 — 거의 사용 안 됨):
```json
{"op":"add_robot", "ref":"rb1", "args":{
    "name":"RB1",
    "apiNames":["HOME","WORK1","WORK2","WORK3"],
    "apiDefRefs":["rb1Home","rb1W1","rb1W2","rb1W3"],
    "opposing":"all-pairs"}}
```
→ Work×4 + ApiDef×4 + ArrowBetweenWorks×6 (C(4,2)=6 ResetReset). `opposing":"chain"` 명시 시 ×3 (N-1).

---

## 4. ImportPlan / DU / Editor 측 검증 결과 — *plan-level 신규 DU/applyDirect 분기는 불필요, 단 ImportPlan.Device.fs 자체는 rev 5 부터 시그니처 변경 4건 필요*

(rev 7 제목 정정 — Major M10) 직전 revision 의 "추가 작업 불필요" 표기는 §4.1 의 ApiCall DU + §4.2 의 cascade 코드 *기존재* 만 의미했으나, rev 5 정정에서 §4.2 의 ensureSystem/ensurePendingWork 시그니처 변경 + buildPassiveDeviceCascade 신설 + buildWorkArrowsAllPairs 신설 등 Core 내부 수정 4건이 추가됨. 절 제목과 본문 결론 충돌 → 정정. **현재 의미**: ImportPlan DU 추가 / ImportPlan.fs applyDirect 분기 추가는 *불필요* (이미 존재). ImportPlan.Device.fs 의 *내부 시그니처 변경* 은 *필요*.

초안의 §4 "구현 진입 전 확인 필요" 였던 두 항목은 *plan-level 에서는* 모두 이미 충족됨 (review C1). 단 §4.2 본문 그대로의 ImportPlan.Device.fs 내부 수정 4건은 진행 필요.

### 4.1 ✅ AddApiCall DU + applyDirect 분기 기존재

`Solutions/Core/Ds2.Core/Store/ImportPlan.fs:14` 에 `AddApiCall of ApiCall` DU case 존재. `:56-57` 에 `store.DirectWrite(store.ApiCalls, apiCall)` 분기. 즉 ApiCall 의 plan 표현 + apply 경로 모두 완비.

### 4.2 ✅ ImportPlanDeviceOps 가 본 작업의 cascade 를 *이미 구현* (단 helper 의 wrapping 형태에 주의)

`Solutions/Core/Ds2.Core/Store/ImportPlan.Device.fs` (총 241 lines) 의 internal 함수들이 다음을 모두 처리:
- `ensureSystem` (line 37-97): PassiveSystem 생성 + LinkSystemToProject(isActive=false) + Flow `{devAlias}_Flow`. **System name = `{flowName}_{devAlias}`** (line 45) — review M1 정정.
- `ensurePendingWork` (line 99-122): Work 생성, **duration = 500ms** (line 116).
- `ensureApiDef` (line 124-150): ApiDef 생성 + **TxGuid=RxGuid=Work.Id binding** (line 144-145).
- `createAndRegisterApiCall` (line 152-160): **ApiCall 생성 + ApiDef.Id binding** (line 158).
- `buildWorkArrows` (line 162-193): **ResetReset Arrow** (line 188) — 단 **`List.pairwise`** 로 chain 형태만 (Home→W1→W2→W3 = 3 arrow).
- public/exposed entry: `linkCallsToDevices` (line 218) / `linkCallsToDevicesMultiFlow` (line 230). Mermaid/CSV importer 가 이미 호출 중 (production path).

> **rev 3 정정 (Critical C2)**: 본 절 직전 revision 은 helper 4종을 *"`linkCallsToDevicesMultiFlow` 의 thin wrapper"* 로 표현했으나 **부정확**. `linkCallsToDevicesMultiFlow` 의 시그니처는 `(string * (Call * string * string option) list) list` 를 받음 — 즉 caller 가 `Call` 인스턴스를 *미리 들고 있어야* 함 (Mermaid/CSV importer 가 텍스트 call name 으로부터 `Call(devAlias, apiName, workId)` 를 만들어 넘기는 경로). LLM helper 는 정반대 — **Active Call 이 없는 상태에서 PassiveSystem cascade + ApiDef 까지만 만들고**, 후속 `add_call` 에서 ApiDef.Id 로 binding 하는 패턴. 따라서 본 작업의 helper 는 `ensureSystem` / `ensurePendingWork` / `ensureApiDef` / `buildWorkArrows` 4개 함수의 partial cascade 가 필요.

> **rev 4 추가 정정 (Critical 1)**: `ensureSystem` / `ensurePendingWork` / `ensureApiDef` / `buildWorkArrows` 는 모두 `let **private**` (line 37, 99, 124, 162) — `module internal ImportPlanDeviceOps` *내부* private. **`InternalsVisibleTo` 는 `internal` 만 노출하고 `private` 은 노출하지 않음** → `Ds2.LlmAgent` 에서 직접 호출 불가. `AssemblyInfo.fs:11` 의 `[<InternalsVisibleTo("Ds2.LlmAgent")>]` 는 **이미 존재** (rev 3 의 "옵션 b 추가" 권장은 stale 이므로 §5.2 에서 제거).

> **rev 5 추가 정정 (Critical 2 + Major 3)**: rev 4 의 wrapper 예시 `... -> DeviceBatchState -> unit` 도 *부정확* — `DeviceBatchState` 가 `type private` (line 9) 이라 외부 caller 가 받을 수 없음. unit 반환이면 helper 가 만든 `PassiveSystem.Id` / `ApiDef.Id` 를 batch ref table 에 등록할 경로 부재. 또한 현행 `ensureSystem` 은 `systemType` 인자가 없고 (line 37 시그니처 — store/projectId/flowName/devAlias/systemNameHint/operations/state), `ensurePendingWork:116` 의 `Duration <- Some (TimeSpan.FromMilliseconds 500.)` 는 *hardcoded* — todo §3.2 의 `PassiveSystem(SystemType=deviceType)` + `workDuration` option 요구 모두 *불충족*.

> **실제로 필요한 작업** (rev 5 — 본 작업이 ImportPlan.Device.fs 자체를 *수정* 해야 함):
>
> 1. **`ensureSystem` 에 `systemType: string option` 인자 추가** — 매개 받은 값을 `system.SystemType <- systemType` 으로 set (생성 분기 line 88 직후). 기존 caller (Mermaid `MapperTargetPlanning.fs` / CSV `CsvMapper.fs` / `linkCallsToDevices*`) 는 default `Some "Unit"` 또는 `None` 으로 호출 (caller 의 의도에 맞춰 결정 — Mermaid/CSV 는 기존 동작 보존 위해 `None` 권장).
> 2. **`ensurePendingWork` 에 `workDuration: TimeSpan option` 인자 추가** — `created.Duration <- workDuration |> Option.defaultValue (TimeSpan.FromMilliseconds 500.)` 또는 `created.Duration <- workDuration` (None 이면 None 그대로). 기존 caller default = `Some (TimeSpan.FromMilliseconds 500.)` (보존).
> 3. **새 wrapper 신설** (`ImportPlanDeviceOps` 모듈 안, *internal* 노출):
>    ```fsharp
>    type WiringMode = Chain | AllPairs | NoneMode
>
>    let buildPassiveDeviceCascade
>        (store: DsStore)
>        (projectId: Guid)
>        (operations: ResizeArray<ImportPlanOperation>)
>        (name: string)            // PassiveSystem.Name (자동 접미 없음, §9.6)
>        (deviceType: string)      // SystemType 값 ("Unit" / "Robot" / "Conveyor" 등)
>        (apiNames: string list)   // 길이 N
>        (workDuration: TimeSpan option)
>        (wiringMode: WiringMode)
>        : Guid * (string * Guid) list =   // PassiveSystemId + (apiName * ApiDefId) pairs
>        let initState = (* DeviceBatchState 빈 init *)
>        // 1) ensureSystem (systemType=Some deviceType, systemNameHint=Some name)
>        // 2) for apiName in apiNames: ensurePendingWork (workDuration)
>        // 3) for apiName in apiNames: ensureApiDef → ApiDef.Id 수집
>        // 4) wiringMode 분기 — Chain: buildWorkArrows / AllPairs: buildWorkArrowsAllPairs (신설) / NoneMode: skip
>        passiveSystemId, apiNamesAndIds
>    ```
>    - `DeviceBatchState` 는 wrapper 내부에서 init / discard — 외부에 *노출되지 않음* (private 유지).
>    - 반환값 `(string * Guid) list` 가 helper caller (LlmAgent 측 `queueAddCylinder` 등) 가 batch ref table 에 다중 등록할 source.
> 4. **`buildWorkArrowsAllPairs` 신설** (rev 3 M17): 기존 `buildWorkArrows` 의 `List.pairwise` 분기와 별도. 새 wrapper 가 `wiringMode = AllPairs` 면 호출.

> **rev 5 결과: 본 작업은 Ds2.Core 측 변경 = ① ensureSystem 인자 추가 (caller 갱신) ② ensurePendingWork 인자 추가 (caller 갱신) ③ buildPassiveDeviceCascade 신설 (internal) ④ buildWorkArrowsAllPairs 신설**. Authoring/Editor 무수정 (CLAUDE.md 결정 7 정책 유지). 기존 caller (Mermaid/CSV) 가 *호출부에서* default 값을 명시적으로 넘기도록 호출부 갱신 — 시그니처 변경 영향이 production path 까지 닿음 → §5.2 에 caller 갱신 의무 명시.

> **caller 영향 점검 의무 (M17, rev 6 단일화 / rev 7 경로 정정)**: `linkCallsToDevicesMultiFlow` 시그니처는 *무변경*. `buildWorkArrows` 만 별도 함수 (`buildWorkArrowsAllPairs` 등) 로 신설하여 기존 caller 무수정 유지 — D7 의 all-pairs 옵션은 helper 측에서 분기. `ensureSystem` / `ensurePendingWork` 의 신규 인자 (`systemType?` / `workDuration?`) 는 *wrapper (`linkCallsToDevices*`) 내부에서 default 로 채워서 전달* → Mermaid/CSV caller 호출부 무영향 (§5.2 의 단일화 정책 참조). 실제 caller 위치 = `Solutions/Convert/Ds2.Mermaid/Import/Targets/{MapperTargetPlanning,MapperTargets}.fs` + `Solutions/Convert/Ds2.CSV/CsvMapper.fs` (rev 7 정정).

### 4.3 ⚠️ 단 — robot all-pairs 는 별도 로직 필요

`buildWorkArrows` 의 `List.pairwise` 는 chain 만. decision 7 의 `opposing="all-pairs"` 는 C(N,2) → 별도 loop 필요. **rev 11 변경 후**: D7 default 가 `"none"` 으로 바뀌어 사용자 명시 없으면 ResetReset 0 — all-pairs / chain 은 사용자 명시 시 한정 진입. 정량 영향 (D8 quota): 산식별 비교:
- `"none"` (default): `3 + 2N`
- `"chain"`: `3 + 2N + (N-1)`
- `"all-pairs"`: `3 + 2N + N(N-1)/2`

| N | 산식 | 결과 | quota 50 vs |
|---|---|---|---|
| 2 | 3+4+1 | 8 | pass |
| 4 | 3+8+6 | 17 | pass |
| 6 | 3+12+15 | 30 | pass |
| 8 | 3+16+28 | **47** | **pass** |
| 9 | 3+18+36 | **57** | **reject** |
| 10 | 3+20+45 | **68** | reject |

**rev 5 정정 (Major 5)**: 직전 revision 은 "N≥8 부터 50 초과" 로 적었으나 산수 오류 — 실제 임계값은 **N≥9**. helper 측 사전 reject 분기는 N=9/10 케이스에서 동작.

권장 메시지 형태: `"op 수 초과: 68 > 50, opposing='chain' 으로 변경 권장 (chain N=10 = 3+20+9 = 32 ops, quota 통과)"`.<br/>
※ 회복 단서로 "apiNames 분할" 도 가능하나 helper 2회 호출 = PassiveSystem 2개 분리 = 도메인적으로 *다른 device* 가 되어 의도와 다를 수 있음. 회복 단서로는 chain 변경이 1순위.

---

## 5. 영향 파일 체크리스트 (Layer 별 분리)

> rev 3 보강: drift / 영향 범위 reviewer 가 식별한 누락 영역 (CLAUDE.md hardcoded "16" 4곳 / done-\* trace / cross-todo / llm-samples / StreamJsonParserTests fixture / ToolOperations.fs invalidOp 메시지 / Mermaid·CSV importer caller / Pass E 호환성 약속 invalidate) 모두 표에 명시.

### 5.1 호환성 약속 invalidate (rev 3 신설 — Critical C6)

본 작업의 add\_call 시그니처 변경은 다음 *기존 약속* 을 명시적으로 invalidate 함. 작업 commit message / done 문서에 반드시 표기:
- `done-promaker-llm-agent.md:1516` 의 *Pass E refactoring* 시점 약속 "queueAddCall 시그니처 + 동작 100% 보존" → 본 작업으로 invalidate.
- `done-batch-mcp-call.md` 의 "Tool 풀세트 = 16종" 흐름 → 본 작업으로 21종.
- **`todo-promaker-llm-agent.md` (Phase 1 todo) 의 실증 5 표 — "Ds2.LlmAgent ProjectReference = Ds2.Editor only 충분" 표기 (rev 11 신설)** → 본 작업으로 invalidate. 본 todo §5.2 L1 + §9.7 의 Critical 2 (`Ds2.Core` 직접 ProjectReference 추가 의무) 가 그 표기를 갱신. **Phase 1 시점 historical 기록은 보존** (CLAUDE.md "done-\*.md 는 historical record 보존" 정책 준수) — `todo-promaker-llm-agent.md` 의 해당 표 행 끝에 *주석* 으로 "Phase 1 한정. extend MCP (todo-extend-mcp.md §5.2 / §9.7) 에서 invalidate — `Ds2.Core` 직접 참조 추가 필요" 한 줄 추가. 표기 자체 갱신은 Phase 1 본문이라 보존.
- 다음 reviewer 가 회귀 의심 시 본 절 인용으로 의도 변경임을 확인 가능.

### 5.2 L1 — F# Core

| 파일 | 변경 |
|---|---|
| `Solutions/Core/Ds2.Core/Entities.fs` | 변경 없음 |
| `Solutions/Core/Ds2.Core/Store/ImportPlan.fs` | 변경 없음 (AddApiCall DU 기존재) |
| `Solutions/Core/Ds2.Core/Store/ImportPlan.Device.fs` | (rev 5 재정의 / rev 7 visibility 강조) (1) `[<InternalsVisibleTo("Ds2.LlmAgent")>]` 는 `Ds2.Core/AssemblyInfo.fs:11` 에 **이미 존재** — 추가 작업 불필요 (단 §5.3 L2 의 fsproj ProjectReference 추가가 *함께* 되어야 효력). (2) **`ensureSystem` 시그니처 변경**: `systemType: string option` 인자 추가 → `system.SystemType <- systemType` 설정 (기존 line 88 직후). (3) **`ensurePendingWork` 시그니처 변경**: `workDuration: TimeSpan option` 인자 추가 → 500ms hardcoded 제거. (4) **`buildPassiveDeviceCascade` 신설 (`let internal`)** — §4.2 의 시그니처. caller 가 PassiveSystemId + (apiName \* ApiDefId) list 받음. **반드시 `let internal` 로 작성** (rev 7 — `let private` 작성 시 LlmAgent 에서 호출 불가). (5) **`buildWorkArrowsAllPairs` 신설 (`let internal`)** — D7 의 all-pairs 분기. (6) `WiringMode` DU 신설 (Chain / AllPairs / NoneMode) — `type internal` 또는 module-기본 internal. |
| `Solutions/Core/Ds2.LlmAgent/Ds2.LlmAgent.fsproj` | **rev 7 신설 — Critical 2 (fsproj ProjectReference 부재)**: 현행 fsproj 의 ProjectReference 가 `Ds2.Editor` 단일 → `Ds2.Core` 직접 참조 부재. F# `InternalsVisibleTo` 는 직접 ProjectReference 만 효력 (transitive 확장 안 됨). **`<ProjectReference Include="..\Ds2.Core\Ds2.Core.fsproj" />` 추가** 필요. 추가 후 빌드 검증 (Ds2.Core 가 transitive 로 이미 들어와 있어 명시 추가가 NuGet 충돌 없는지). |
| `Solutions/Convert/Ds2.Mermaid/Import/Targets/MapperTargetPlanning.fs` / `Solutions/Convert/Ds2.Mermaid/Import/Targets/MapperTargets.fs` | (rev 7 경로 정정 — 직전 revision 의 `Solutions/Core/Ds2.Core/Store/Mermaid/...` 는 stale, 실제 위치는 `Solutions/Convert/Ds2.Mermaid/Import/Targets/`) (rev 6 단일화) **채택 정책: wrapper 기본값 방식 (caller 무수정)**. `linkCallsToDevices` / `linkCallsToDevicesMultiFlow` 의 *시그니처는 무변경* — 내부적으로 `ensureSystem` 호출 시 `systemType=None`, `ensurePendingWork` 호출 시 `workDuration=Some (TimeSpan.FromMilliseconds 500.)` 을 *wrapper 내부에서 default 로 채워서* 전달. 이로써 Mermaid/CSV 기존 caller 의 호출부 *전혀* 무수정. <br/>**rev 5 의 "caller 직접 default 명시" 안은 폐기** — caller 갯수 (Mermaid 4곳 + CSV 1곳) 각각 호출부 수정 부담 + 향후 추가 caller 발생 시 누락 위험 + 기존 동작 보존이 명시적 일관성보다 *중요*. <br/>본 단일화로 §4.2 의 "caller 영향 점검 의무" 도 *내부 ensureSystem/ensurePendingWork 시그니처 변경* 한정 — 외부 production path (Mermaid/CSV) 는 무영향. |
| `Solutions/Convert/Ds2.CSV/CsvMapper.fs` | (rev 7 경로 정정 — `Solutions/Core/Ds2.Core/Store/Csv/...` 는 stale, 실제 위치는 `Solutions/Convert/Ds2.CSV/`) (rev 6 단일화) 동상 — `linkCallsToDevices*` 가 wrapper 내부에서 default 채우므로 호출부 무수정. |
| `Solutions/Core/Ds2.Editor/Editor/ImportPlanApply.fs` | 변경 없음 |
| `Solutions/Core/Ds2.Editor/Store/Nodes/Device.fs:225` | **동명 함수 disambiguation** (M5): `let linkCallsToDevices` 가 `Ds2.Editor.Store.Nodes.Device` module 에 별도 존재. helper 가 사용할 정전 = `Ds2.Core.Store.ImportPlanDeviceOps.linkCallsToDevices` 측. open 충돌 회피. |

### 5.3 L2 — F# LlmAgent

| 파일 | 변경 |
|---|---|
| `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` | (1) `queueAddSystem` → `queueAddActiveSystem`/`queueAddPassiveSystem` 분리. (2) `queueAddCall` 시그니처 변경 (workId, apiDefId) + ApiDef→ParentSystem 역추적. **ApiCall 생성 코드는 `createAndRegisterApiCall` (`ImportPlan.Device.fs:152`) 패턴을 *그대로* 복사하지 말 것** (rev 5 정정 — 그 함수는 `apiCall.ApiDefId` 만 set 하고 `OriginFlowId` 누락). 본 작업은 다음 *3 단계 모두* 수행: <br/> ① `apiCall.ApiDefId <- Some apiDefId` <br/> ② **`apiCall.OriginFlowId <- Some callerFlow.Id`** (callerFlow = workId 의 parent Flow) — 누락 시 ApiCall 의 호출 출처 역참조 깨짐 (회귀). <br/> ③ `call.ApiCalls.Add(apiCall)` + `queueOperation (AddApiCall apiCall)`. <br/> + 기존 `hasCallNameClash` 검사 정책 *유지*. (3) `queueAddApiDef` 에 `txWorkId?`/`rxWorkId?` 인자 노출. (4) helper 4종 신설 (`queueAddCylinder`/`queueAddClamp`/`queueAddRobot`/`queueAddDevice`) — **§5.2 L1 의 `buildPassiveDeviceCascade` (Ds2.Core 신설 internal wrapper) 호출** 후 반환된 `(string * Guid) list` (apiName \* ApiDefId) 를 caller (dispatchBatchOp) 에 넘겨 batch refTable 에 다중 등록. helper 는 PassiveSystem + Flow + Work + ApiDef + Arrow 까지만 만들고 ApiCall 은 *후속 add\_call* 책임 (rev 4 일관화). (5) batch dispatcher (`dispatchBatchOp`, **line 849**) 갱신 — `add_system` 제거, 6종 추가, `apiDef*Ref` / `apiDefRefs` 인자 파싱 + refTable 다중 등록 분기 + 길이 검증 (D6 strict 와 동일 stage). (6) `dispatchBatchOp` 시그니처 `(Guid option * string)` → `((string * Guid) list * string)` 확장. **rev 6 caller 영향 명시**: 호출부는 `ToolOperations.fs` 내부 batch dispatcher loop 와 `ImportPlanBuilder.fs` 의 batch 결과 집계 (`runBatch` 등). list 내 *첫 요소* `(refName, guid)` = 기존 단일 ref (primitive op 의 경우 `[(refName, guid)]` 단원소 list). helper op 의 경우 *추가 요소* 는 `apiDef*Ref` → `ApiDef.Id` 다중 등록. **caller 누적 정책**: dispatcher loop 가 list 를 walk 하며 ref table (`Dictionary<string, Guid>`) 에 모두 register. 중복 ref 명 발생 시 기존 BATCH\_ERROR 정책 (ref name unique 검증) 그대로 적용 — helper 의 `apiDef*Ref` 가 main `ref` 와 충돌 시도 동일 BATCH\_ERROR. primitive op 호출부는 `[(ref, guid)]` 단원소 형태로 wrapper 처리 → 호출부 변경 최소화. (7) **`invalidOp` 허용 op 목록 메시지** (`:168`, `:183`, `:191`, `:312`, `:913` 등) — `add_system` 제거 + 6종 추가. **`:913` 은 runtime 에 BATCH\_ERROR 로 LLM 노출** → 갱신 누락 시 LLM 이 잘못된 허용 목록 보고 add\_system 시도 → 무한 retry. (8) **D8 quota — F# 측 책임 *없음*** (rev 5 정정: assembly 참조 방향상 F# 가 C# LlmTurnContext 참조 불가). 본 함수는 단순히 cascade op 들을 plan 에 enqueue. quota 차감은 C# `ModelTools` 측 책임. **rev 6 추가**: batch 경로 (`dispatchBatchOp`) 에서도 quota 차감을 *직접 수행하지 않음* — batch 진입점인 C# `ModelTools.ApplyOperations` 가 dispatch *진입 전* inputs 를 walk 하여 helper op 별 cascade op 수 사전 합산 후 `ctx.IncrementMutationCount(...)` 일괄 차감 (D8 ②). F# dispatcher 는 cascade op 발행만 담당. |
| `Solutions/Core/Ds2.LlmAgent/ImportPlanBuilder.fs` | 변경 없음 |

### 5.4 L3 — C# Promaker

| 파일 | 변경 |
|---|---|
| `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs` | (1) `AddSystem` 분리 (`AddActiveSystem`/`AddPassiveSystem`), `AddCall` 시그니처 변경 (`workId, apiDefId`), helper 4종 신설 메서드 (`AddCylinder`/`AddClamp`/`AddRobot`/`AddDevice`). (2) **McpServerTool description / xmldoc 본문 6곳 grep 후 일괄 갱신** (`:18`, `:225`, `:245`, `:250`, `:373` 의 "add\_system" 잔재). (3) **D8 quota cascade — single helper 경로**: helper 메서드 (`AddCylinder` 등) 안에서 RunMutation 진입 직후 `ctx.IncrementMutationCount(cascadeOpCount - 1)` 추가 차감 (rev 5: F# 측이 아니라 C# 측 책임). cascadeOpCount = chain `3 + 2N + (N-1)` / all-pairs `3 + 2N + C(N,2)` / none `3 + 2N`. (4) **D8 quota cascade — batch 경로 (rev 6 신설, Critical 2)**: `ApplyOperations` (batch tool, `:163` 부근) 의 `inputs.Length - 1` 차감 직후, **inputs 를 walk 하며 helper op 인지 검사 → helper op 면 args 에서 `apiNames.length`/`opposing` 추출 → cascadeOpCount 계산 → `ctx.IncrementMutationCount(cascadeOpCount - 1)` 추가 차감** (이미 input 1개로 +1 카운트되어 있으므로 -1). 모든 helper op 의 추가 차감은 dispatch *진입 전* 사전 수행 → 초과 시 즉시 BATCH\_ERROR (op[0] 진입 전, 부분 적용 회피). pseudo: <br/>`foreach (input in inputs) if (IsHelperOp(input.Op)) ctx.IncrementMutationCount(CalcCascade(input) - 1);` <br/>(5) helper op 식별/cascade 계산 헬퍼 (`IsHelperOp` / `CalcCascadeOpCount`) 신설 — `add_cylinder`/`add_clamp`/`add_robot`/`add_device` 4종 분기 + `apiNames` / `opposing` 파싱. |
| `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs` | `mcp__promaker__add_system` 제거, 6종 추가 (`add_active_system`/`add_passive_system`/`add_cylinder`/`add_clamp`/`add_robot`/`add_device`). **16 → 21** (산식: `16 - 1 + 6 = 21`). |
| `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:35` | xmldoc 잔재 "add\_system / list\_systems" 갱신. |
| `Apps/Promaker/Promaker/LlmAgent/LlmTurnContext.cs` | **변경 없음** (rev 4 정정) — `IncrementMutationCount(int delta = 1)` 가 이미 `:50` 에 존재 (batch tool 용). helper 는 이걸 그대로 활용 (`IncrementMutationCount(cascadeOpCount)`). 추가 method 불필요. |

### 5.5 Drift / Prompt — count "16" hardcoded 갱신 (rev 3 보강 Critical C3)

| 파일 | 변경 |
|---|---|
| ~~`Apps/Promaker/Promaker/Promaker.csproj`~~ | **rev 11 항목 제거 (Major)** — 직전 revision (rev 8) 의 "ds.md remove 즉시 적용 완료" 표기는 *허위*. 실측 검증 결과 (rev 11 — `find /f/Git/ds2/feature-llm`): ① **ds.md 파일이 어디에도 존재하지 않음** (Apps / Solutions 모두 0건). ② csproj 의 EmbeddedResource directive 는 line 59 `<Include LlmAgent\Prompts\*.md/>` + line 60 `<Remove ...\CLAUDE.md />` 단 2 건. ds.md remove 부재. rev 8 작성자가 phantom 파일을 잘못 인용한 것으로 추정. 본 항목은 todo 에서 삭제. (LLM context 에서 제외할 추가 prompt 파일이 미래 등장하면 그때 신규 항목으로 추가.) |
| `Apps/Promaker/Promaker/LlmAgent/Prompts/1.entities.md` | (rev 10 신설 — Critical) 직전 revision 의 §5.5 표에 본 행이 *부재* 했음. helper 도입 후 다음 두 곳이 stale 잔류 위험: <br/>① **§4.6 ApiDef 표** (line 119-123 — "현 phase MCP 도구는 Tx/RxGuid 인자를 노출하지 않으며 ... 후속 phase 의 device-class helper (`add_cylinder` 등) 가 Passive 내부 Flow/Work cascade 와 함께 자동 채움") 표현 → "helper (`add_cylinder` / `add_clamp` / `add_robot` / `add_device`) 가 PassiveSystem cascade 시 자동 채움 / primitive `add_api_def` 사용 시 `txWorkId?` / `rxWorkId?` 인자로 명시" 로 재작성. <br/>② **§6 "GFM 범위 외 — 후속 phase 확장 예정" 의 `ApiDef.TxGuid / RxGuid` 항목** (line 185) 제거 — helper 도입 후 자동 채움 영역이라 *후속 확장 예정* 목록에서 빠짐. (다른 항목 — IOTag / ValueSpec / CallCondition / TokenSpec / HwButton / SubmodelProperty 32 / ApiDefActionType / Work.TokenRole / Duration / ReferenceOf 등 — 은 helper 무관, 그대로 유지.) canary 보존. |
| `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md` | 도구 목록 갱신. canary 보존. (본문에 "16" 직접 hardcoded 는 검증 결과 없음) <br/>**rev 8 sub-bullet** — 본 todo 적용 시점에 추가로 처리할 갱신: <br/>① **현재 11-op cylinder 예시** (line 38-51 — `add_project` + `add_system isActive:false` + `add_api_def×2` + `add_system isActive:true` + `add_flow` + `add_work×2` + `add_call×2` + `add_arrow`) **를 helper 도입 후 9-op 형태** (`add_project` + `add_cylinder` 1 op (Passive cascade 자동) + `add_active_system` + `add_flow` + `add_work×2` + `add_call×2` (`workId`/`apiDefId` 2-인자 시그니처) + `add_arrow`) **로 재작성**. ApiDef ref 다중 등록 (`apiDef1Ref:"cylAdv"` / `apiDef2Ref:"cylRet"`) + add_call 2-인자 시그니처 반영. **rev 11 보강**: rev 10 §1 표 (Cylinder 1개 11→9 op) 와 정합. <br/>② "본 phase 의 도구 범위 — 후속 확장 예정" 절 (rev 8 작업에서 신설) 의 항목 정리 — IOTag / CallCondition / SubmodelProperty 등이 본 todo 의 helper 도입 *이후에도 여전히* 도구 부재 영역인지 확인. helper 도입 후 ApiDef.TxGuid/RxGuid 가 자동 채움 → 해당 항목은 "후속 확장 예정" 목록에서 제거. <br/>③ "Passive System 의 GUI canonical cascade — 도구 한계 인지" 절 (rev 8 신설) 의 "현 phase 한계" 문구를 helper 도입 후 형태로 재작성 — `add_cylinder` 등 1 op 가 자동 cascade 처리하므로 *도구 한계 → helper 사용 권고* 로 의미 전환. <br/>**rev 10 sub-bullet 추가** — sub-bullet ①②③ 외에 별도 갱신 영역: <br/>④ **line 59-62 "operations 배열에서 지원되는 op" 목록** — 현재 `add_project | add_system | add_flow | add_work | add_call | add_api_def | add_arrow | remove_entity | rename_entity` → `add_system` 제거 + 6종 추가 (`add_project | add_active_system | add_passive_system | add_flow | add_work | add_call | add_api_def | add_arrow | add_cylinder | add_clamp | add_robot | add_device | remove_entity | rename_entity`). <br/>⑤ **line 75-86 "단일 mutation 도구 (legacy)" 절** — `mcp__promaker__add_system(name, isActive?)` 행 제거 + 다음 6종 행 추가: `add_active_system(name)` / `add_passive_system(name, deviceType)` / `add_cylinder(name, apiDef1Ref, apiDef2Ref, apiNames?, workDuration?)` / `add_clamp(...)` / `add_robot(name, apiDefRefs, apiNames, opposing?, workDuration?)` / `add_device(name, deviceType, apiNames, apiDefRefs, opposing?, workDuration?)`. 또한 같은 절의 `add_call(devicesAlias, apiName, workId)` → `add_call(workId, apiDefId)` 변경. <br/>⑥ **line 81 `add_api_def(name, systemId)` 시그니처** → `add_api_def(name, systemId, txWorkId?, rxWorkId?)` 로 인자 노출. (line 194 의 "도구 한계" 절 `add_api_def` 표기는 sub-bullet ③ 의 의미 전환에 흡수.) |
| `Apps/Promaker/Promaker/LlmAgent/Prompts/2.modeling.md` | §1 표 / §2 ✅예시 / §3.3 표 / §4 모든 예시 helper 사용 형태로 재작성. deviceType 표기 정렬 (cylinder/clamp/lifter→"Unit", robot→"Robot", conveyor→"Conveyor"). canary 보존 <br/>**rev 8 sub-bullet** — 본 todo 적용 시점에 추가로 처리할 갱신: <br/>① §1 매핑표 "디바이스 행위의 실행 단위" 행의 "현 phase 도구 한계" 문구 → "helper (`add_cylinder` 등) 가 1 op 로 자동 cascade" 로 재작성. <br/>② §3.5 "Passive 내부 — opposing Arrow" 절의 "현 phase 직접 생성 안 함" 문구 → "helper 가 자동 cascade — primitive 만으로는 만들지 마십시오" 로 의미 전환. <br/>③ §4.1 / §4.2 / §4.4 표준 묶음 예시의 "Passive 내부 cascade 생략" 표기 → helper 사용 형태 (`add_cylinder` / `add_device` 1 op) 로 재작성. WithCyl.json parity 모델 도달. <br/>④ §4.2 노트의 룰 D vs "RET 자동 동반" 모순 해소 (rev 8 작업에서 룰 D 우선으로 정정 완료) — helper 도입 후에도 정책 그대로 (사용자 confirm 시에만 동반). <br/>⑤ §5 self-check 항목 — **rev 10 wording 확정 (사용자 confirm: 의미 반전 + 기존 device 재사용 예외)**: 현행 "**현 phase 한정**: Passive 내부에 Flow / Work / ArrowBetweenWorks 를 *만들지 않았는가*?" 항목을 다음 양분 형태로 재작성: <br/>　• "사양에 *새로* 등장한 디바이스 인스턴스에 대해 helper (`add_cylinder` / `add_clamp` / `add_robot` / `add_device`) 를 사용했는가? primitive (`add_passive_system` + `add_flow` + `add_work` + `add_api_def` + `add_arrow`) 만으로 PassiveSystem 내부 cascade 를 직접 작성하지 *않았는가*?" <br/>　• "**예외**: 사양이 *이미 store 에 존재* 하는 Passive System (예: 기존 Cyl1 에 새 Active Work 만 추가) 만 지칭하면 helper 호출 *불필요* — primitive `add_call` 로 기존 ApiDef.Id 를 ref 또는 GUID 로 참조. helper 는 신규 디바이스 인스턴스 *생성* 책임만 짐. (즉 디바이스 *재사용* 시나리오는 PassiveSystem cascade 가 이미 존재하므로 helper 가 중복 생성 시 BATCH\_ERROR 위험 — `find_by_name` 또는 `list_systems` 로 기존 인스턴스 우선 조회.)" |
| `Solutions/Core/Ds2.LlmAgent/CLAUDE.md` (line 101 / 109 / 141 / 149) | **"16개" / "16 tool method" / "mutation tool 16개 세트" / "16개 fully-qualified" 4곳 모두 21 로 갱신**. CLAUDE.md 는 "두 문서 유지 정책" SSOT — 갱신 누락 시 다음 세션이 stale 정보로 진입. |
| `Solutions/Core/Ds2.LlmAgent/doc/done-batch-mcp-call.md:32,203,250` | "도구 풀세트 15→16" / "Tool 풀세트 16종" trace — 본 작업 완료 시점에 21 추가 또는 "16 시점 동결, 본 작업이 다음 phase" 로 명시 |
| `Solutions/Core/Ds2.LlmAgent/doc/done-promaker-llm-agent.md:295,341,664,694,1516,1683` | 풀세트 카운트 trace + queueAddCall 시그니처 trace + Pass E 호환성 약속 → §5.1 invalidate 와 cross-link |
| `Solutions/Core/Ds2.LlmAgent/doc/todo-promaker-hmi-llm-agent.md:23,26,131,132,253,286,320` | (**rev 11 정정 — Major**: 직전 revision 의 인용 `:26,131,253,319` 는 누락 + 오지정. 실측 검증 후 정확한 인용으로 갱신.) <br/>① **line 23** `ModelTools ([McpServerToolType] 11개 tool)` → 21 갱신 (rev 8 `[McpServerToolType] 16개 tool` 시점도 stale, helper 후 21). <br/>② **line 26** `현재 mcp tool 11개: mcp__promaker__add_system / add_flow / ...` 목록 → 21개 도구 목록 (`add_active_system / add_passive_system / add_cylinder / add_clamp / add_robot / add_device / ...`) 로 재작성. <br/>③ **line 131-132** SystemPrompt.cs Phase1c stale 정정 — 현재는 `SystemPrompt.cs` = `PromptLoader.LoadComposed()` 1회 호출이고 본문은 외부 `Apps/Promaker/Promaker/LlmAgent/Prompts/{1.entities,2.modeling,3.tooling}.md` 에서 로드. HMI prompt 보강 위치는 `3.tooling.md` 중심 + 신규 `4.hmi.md` 등으로 명시 (또는 `SystemPrompt.cs` 의 `PromptLoader.LoadComposed` 호출에 hmi schema 추가). <br/>④ **line 253** Phase Hmi-1d 의 modeling vs hmi 의도 자동 판별 rule — modeling tool 21 갱신 + hmi tool 신규 N 개 표기. <br/>⑤ **line 286** `hmi tool 11~15` 가정 → 본 작업 완료 후 modeling tool 21 base 위에 hmi 추가 N 개 형식. <br/>⑥ **line 320** `SystemPrompt.cs HMI schema/tool/rule 추가` → PromptLoader 외부 .md 추가 형태로 재작성. <br/>⑦ **line 319 오지정 정정 (rev 11)**: 직전 revision 의 line 319 인용 (PromakerToolNames.cs hmi tool 이름 추가) 은 *21 갱신 영역이 아님* — 그 라인은 hmi tool 추가 영역이고 21 갱신 책임은 line 23/26 임. 인용에서 제거. |
| `Solutions/Core/Ds2.LlmAgent/script/pass5-message-analysis.fsx` | tool 이름 enum / baseline 갱신. Pass 5 측정 결과 비교 시 baseline drift 경고. |
| ~~`Solutions/Core/Ds2.LlmAgent/llm-samples/**/*.md`~~ | **rev 7 항목 제거 (Major M7)** — 직전 revision 의 "6개 파일" 표기는 phantom. `Solutions/Core/Ds2.LlmAgent/llm-samples/` 폴더 *자체 부재* 검증 통과 (`ls` 결과 폴더 없음). 본 항목은 todo 에서 삭제. 향후 sample 폴더가 실제 생성되면 그때 별도 todo 항목으로 추가. |

### 5.6 Tests (신규 6 + 기존 보강 4 = 10개)

> rev 4 정정: 이전 revision 의 헤더 "신규 7" 은 stale 산수 (실제 표 항목 6개). 카운트 정정. **HelperCascadeTests 의 expected = ApiCall 제외** (rev 4: helper 책임 범위 일관화 — ApiCall 은 후속 add\_call 책임이므로 별도 test 인 ImportPlanApplyApiCallTests 에서 검증).

| 파일 | 목적 |
|---|---|
| `Solutions/Tests/Ds2.LlmAgent.Tests/BatchTests.fs` | **기존 보강 1** — op 명 갱신, multi-ref (`apiDef*Ref`) 파싱/등록, D6 ref-required (omit 시 BATCH\_ERROR), `add_call` 2-인자 시그니처 |
| `Solutions/Tests/Ds2.LlmAgent.Tests/RemoveRenameTests.fs` | **기존 보강 2** — Active/Passive 분리 후 회귀 |
| `Solutions/Tests/Ds2.LlmAgent.Tests/PromakerToolNamesDriftTests.fs` | **기존 보강 3** — sanity count `Assert.Equal(16, ...)` → `21` 수동 갱신. **fact 이름 자체에 "16개" 포함 여부 grep → 같이 갱신** (Assert 값만 갱신 시 fact 이름 drift). drift 동시 갱신 = false-green 회피. (rev 3 m2) |
| `Solutions/Tests/Ds2.LlmAgent.Tests/StreamJsonParserTests.fs:54-58` | **기존 보강 4** — fixture `"name":"add_system"` → `"add_active_system"` 등 갱신 (파싱 동작 영향 없으나 misleading 회피) |
| `HelperCascadeTests.fs` (**신규 1**) | helper 4종 cascade 결과 검증. **expected = PassiveSystem + Flow + Work + ApiDef + ResetReset Arrow 갯수/관계** (rev 4: ApiCall 제외 — helper 책임 범위 외). cylinder N=2 = 1 System + 1 Link + 1 Flow + 2 Work + 2 ApiDef + 1 Arrow = **8 op** (chain 산식 `3 + 2N + (N-1)` = 3+4+1=8 과 일치). rev 6 정정: 직전 revision 의 "7 op" 는 산수 오류. |
| `HelperGuiParityTests.fs` (**신규 2**) | fixture = `Solutions/Core/Ds2.LlmAgent/doc/WithCyl.json` 또는 `Solutions/Tests/Ds2.LlmAgent.Tests/Fixtures/WithCyl.json` (§0.2 SSOT 결정 후). 비교 범위 = *naming 외 cascade 구조 (Work/ApiDef/Arrow 갯수·관계) parity*. naming 자체는 helper `name` ≠ GUI `{flow}_{devAlias}` 라 비교 제외 (M11). GUI fixture 가 ApiCall 까지 가지고 있더라도 비교 시 ApiCall 부분은 제외 (rev 4 helper 책임 일관화). |
| `PromptCanaryTests.fs` (**신규 3**, M7, rev 9 protocol) | `Assert.Contains("pong: Prompts/1.entities.md", ...)` / `"pong: Prompts/2.modeling.md"` / `"pong: Prompts/3.tooling.md"` / `"pong: Prompts/CLAUDE.md"` — 4 파일 통일 protocol 의 canary 회귀 방어. 단일 trigger (`ping all`) 이므로 fixture 는 4 Assert 한 set. **release 시점 canary 제거 정책 시 본 test 도 동시 skip/제거** (§6.4 회수 절차 참조) |
| `DeviceTypeDriftTests.fs` (**신규 4**) | KnownNames 19종 ↔ 2.modeling.md §3.3 표 / D1 통합 정합성 검증 |
| `LlmTurnContextQuotaTests.fs` (**신규 5**, D8) | helper 가 cascade 로 push 하는 op 수가 mutation quota (50) 와 정합. **임계값 검증 (rev 5 정정 / rev 11 default 변경)**: <br/>　• **default `"none"` (rev 11 — robot 표준 케이스)**: N=10 = `3+2N` = 23 op, pass. N=20 = 43 op, pass. <br/>　• `"chain"` 명시: N=10 = `3+2N+(N-1)` = 32 op, pass. <br/>　• `"all-pairs"` 명시: N=8 (47 op) = pass / N=9 (57 op) = reject / N=10 (68 op) = reject. <br/>**default `"none"` 케이스가 일반 robot 시나리오의 표준 path** — chain/all-pairs 는 사용자 명시 path 한정 검증. **C# `ModelTools` 측 helper 진입점이 `ctx.IncrementMutationCount(cascadeOpCount - 1)` 로 추가 차감하는지 검증** (rev 5: F# 측이 아닌 C# boundary 에서 처리). |
| `ImportPlanApplyApiCallTests.fs` (**신규 6**) | `add_call` 의 ApiCall 자동 cascade 가 ImportPlanApply 후 store.ApiCalls 에 정확히 binding. **반드시 검증할 binding 2개**: ① `apiCall.ApiDefId` = 인자 apiDefId, ② **`apiCall.OriginFlowId` = workId 의 parent Flow.Id** (rev 5 정정 — createAndRegisterApiCall 패턴이 OriginFlowId 누락이라 회귀 방어 필수). **helper + 후속 add_call 시퀀스 통합 검증** — helper 가 만든 ApiDef.Id 가 ref 로 add_call 에 전달되어 ApiCall.ApiDefId 에 정확히 binding 되는지. |

### 5.7 문서

| 파일 | 변경 |
|---|---|
| `Solutions/Core/Ds2.LlmAgent/doc/todo-promaker-llm-agent.md` | 본 작업이 phase X 로 진입 시점에 phase 갱신 |
| `Solutions/Core/Ds2.LlmAgent/doc/done-promaker-llm-agent.md` 또는 `done-extend-mcp.md` (분리) | 본 작업 완료 시 산출물 누적. CLAUDE.md 의 "두 문서 유지 정책" 준수. §5.1 호환성 invalidate 표기 |
| 본 todo (`todo-extend-mcp.md`) | 작업 진행 중 §4 검증 결과 / 결정 변경 시 동기 갱신. **Layer commit 시점에 todo/done sync 책임** (§7 정의). 인용 line 번호 stale 시 갱신 의무 (CLAUDE.md "검증된 사실 표 동기 갱신" 정책 본 todo 에도 적용) |

---

## 6. 본 세션에서 이미 처리된 것 (재작업 금지) + canary 회수 절차

### 6.1 처리 완료 항목

rev 9 (prompt rename + canary protocol 통일) 후 — `Apps/Promaker/Promaker/LlmAgent/Prompts/{1.entities,2.modeling,3.tooling,CLAUDE}.md` 4 파일 *맨 앞* 에 canary 주석 통일 적용 완료. (`CLAUDE.md` 는 csproj 의 `<EmbeddedResource Remove="LlmAgent\Prompts\CLAUDE.md" />` directive 로 LLM context 외이지만 canary 는 동일 protocol 로 보존 — 파일 자체의 의도 유지).
- 4 파일 모두 — trigger `ping all` → expected `pong: Prompts/<basename>` (단일 통합 trigger).
- 1.entities.md → `pong: Prompts/1.entities.md`
- 2.modeling.md → `pong: Prompts/2.modeling.md`
- 3.tooling.md → `pong: Prompts/3.tooling.md`
- CLAUDE.md → `pong: Prompts/CLAUDE.md`
- 어미는 모든 파일 통일 — "이라고" (한국어 표준).
- HTML 주석 (`<!-- canary: ... -->`) 형식 — 마크다운 렌더에 안 보이고 LLM 에 평문 전달.
- 옛 4-token 시스템 (`@SP-9C12-2026` / `@MDL-7F3A-2026`) 은 폐기. PromptCanaryTests fixture 는 단일 패턴 (`Assert.Contains("pong: Prompts/", ...)` 또는 file-별 `Assert.Contains("pong: Prompts/<basename>", ...)`) 으로 작성.

### 6.2 sanity check (다음 세션 진입 시)

PowerShell:
```powershell
Get-ChildItem Apps/Promaker/Promaker/LlmAgent/Prompts/*.md | ForEach-Object { Get-Content -TotalCount 1 $_ }
```
bash:
```bash
head -1 Apps/Promaker/Promaker/LlmAgent/Prompts/*.md
```
4 파일 모두 첫 줄에 `<!-- canary: ... pong: Prompts/<basename> ... -->` 가 보여야 함. 부재 시 §6.4 회복 절차.

### 6.3 작업 중 보존 의무

- 본 작업의 prompt 파일 수정 시 canary 줄 보존 (M7 PromptCanaryTests 와 동기).
- canary trigger 변경 시 PromptCanaryTests 의 `Assert.Contains(...)` 도 동시 갱신.

### 6.4 release 시점 회수 절차 (rev 3 신설, rev 9 갱신)

production release 직전 canary 제거 절차 — *prompt 측 canary 제거 + test 측 PromptCanaryTests 동시 제거/skip* 가 atomic:

1. `Apps/Promaker/Promaker/LlmAgent/Prompts/{1.entities,2.modeling,3.tooling,CLAUDE}.md` 4 파일 첫 줄 `<!-- canary: ... -->` 일괄 제거.
2. `Solutions/Tests/Ds2.LlmAgent.Tests/PromptCanaryTests.fs` 도 *동시* 삭제 또는 `[<Fact(Skip = "release: canary removed")>]` 처리. (1번만 하고 2번 누락 시 release build 의 test 단계 RED → release 차단.)
3. CLAUDE.md / done 문서의 canary 관련 trace cleanup (rev 9 단일 protocol 이라 token grep 부담은 옛 시스템 대비 적음 — `pong: Prompts/` 로 일괄 grep).
4. release commit message 에 "canary 일괄 제거" 표기.

회복 절차 (canary 가 의도치 않게 제거/오작동 시):
- 4 파일 첫 줄 복원 (git history 에서 `<!-- canary: ... -->` 형태 grep).
- canary 자체 변경이 의도면 PromptCanaryTests 의 `Assert.Contains` 갱신 + revision history 에 reason 기록.

---

## 7. 다음 세션 진입 시 권장 순서

### 7.1 commit 단위 정의 (rev 3 — Tier 동음이의 해소)

본 todo 에서 **"Tier"** 는 §3.2 의 도구 분류 (Tier 0/1/2 = primitives / device-class helper / generic fallback) 만 가리킴. **commit boundary 는 "Layer"** = §5 의 L1/L2/L3 (F# Core / F# LlmAgent / C# Promaker). 두 개념을 혼동하지 말 것.

- "Layer commit" = L1 끝나면 1차 commit / L2 끝나면 2차 commit / L3 끝나면 3차 commit (+ Drift/Prompt + Tests 단위 추가 commit).
- 각 Layer commit 시 todo/done 문서 sync 책임 = commit 작성 세션.

### 7.2 진입 절차

1. 본 문서 + `Apps/Promaker/Promaker/LlmAgent/Prompts/2.modeling.md` + `Solutions/Tests/Ds2.LlmAgent.Tests/Fixtures/WithCyl.json` (§0.2 영속화 후) 같이 읽기.
2. **§0 선결 조건 (0.1 2.modeling.md 영속화 + 0.2 WithCyl.json 영속화)** *모두* 처리 후 본격 작업 진입. 미처리 시 silent degradation.
3. §6.2 sanity check 통과 확인.

### 7.3 구현 의존성 그래프 + RED-GREEN cycle

```
        ┌─ L1.a InternalsVisibleTo ──┐
        │                            │
        ├─ L1.b buildWorkArrowsAllPairs (별도 함수, 기존 caller 무수정) ─┐
        │                                                              │
        ▼                                                              ▼
  [L2 helper 4종 신호 정의 가능]──→ L2.a queueAddActiveSystem/queueAddPassiveSystem 분리
                                  ├─ L2.b queueAddCall 시그니처 변경 + originFlowId 자동 도출 + hasCallNameClash 유지
                                  ├─ L2.c helper 4종 (partial cascade wrapper)
                                  ├─ L2.d dispatchBatchOp 갱신 (line 849) + invalidOp 메시지 (특히 :913 runtime 노출)
                                  └─ L2.e D8 quota 합산 mechanism
                                            │
                                            ▼
                                   L3.a ModelTools.cs + PromakerToolNames.cs (16→21) + LlmChatViewModel.cs:35 xmldoc
                                            │
                                            ▼
                          ┌── Drift / Prompt 일괄 갱신 (CLAUDE.md / done-* / cross-todo / 3.tooling.md / 2.modeling.md / 1.entities.md) ──┐
                          │                                                                                                          │
                          ▼                                                                                                          ▼
                   Tests 보강/신규 (BatchTests / RemoveRenameTests / DriftTests sanity 21 / StreamJsonParserTests fixture / HelperCascade / HelperGuiParity / PromptCanary / DeviceTypeDrift / LlmTurnContextQuota / ImportPlanApplyApiCall)
```

**RED-GREEN 권장 순서** (각 helper / 시그니처 변경별):
- *RED 먼저*: BatchTests 의 새 op 시그니처 / HelperCascadeTests / ImportPlanApplyApiCallTests 의 expected 작성 → 컴파일 통과 / test 자체 RED.
- *GREEN 구현*: L2 의 해당 함수 구현.
- *Drift 동시 갱신*: PromakerToolNamesDriftTests sanity 21 + fact 이름 + CLAUDE.md hardcoded 4곳 = **반드시 동시 commit** (false-green 회피).

### 7.4 commit 시 sync 의무

- 각 Layer commit message 에 어떤 결정/문서를 갱신했는지 표기.
- `Solutions/Core/Ds2.LlmAgent/CLAUDE.md` "두 문서 유지 정책" 적용 — 본 todo + done-promaker-llm-agent.md (또는 done-extend-mcp.md 분리).
- 인용 line 번호 stale 시 갱신 의무 (예: `dispatchBatchOp` 위치 변경 시 §8 색인 동기 갱신).

### 7.5 완료 시점

- `done-extend-mcp.md` 분리 작성 vs `done-promaker-llm-agent.md` 합병 — 사용자 명시 결정 필요. 분리가 권장 (history 흐름 보존).
- 본 todo 의 §5.1 호환성 invalidate 절은 done 문서로 그대로 이전.

---

## 8. 관련 파일 / 경로 색인

- 본 문서: `Solutions/Core/Ds2.LlmAgent/doc/todo-extend-mcp.md`
- 도메인 룰: `Apps/Promaker/Promaker/LlmAgent/Prompts/2.modeling.md` (untracked, canary 포함)
- 시스템 프롬프트: `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md` (canary 포함)
- 핵심 sample: `F:\tmp\WithCyl.json` — Promaker GUI 의 cylinder default cascade 결과
- F# entities: `Solutions/Core/Ds2.Core/Entities.fs` (DsSystem.SystemType 등)
- ImportPlan DU: `Solutions/Core/Ds2.Core/Store/ImportPlan.fs:14` AddApiCall 기존재
- Device cascade 구현 (재활용 대상): `Solutions/Core/Ds2.Core/Store/ImportPlan.Device.fs:218,230` linkCallsToDevices / linkCallsToDevicesMultiFlow
- queueAdd\* 함수: `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs:188 queueAddSystem`, `:230 queueAddCall`, `:242 queueAddApiDef`, `:322 queueAddArrow`, **`:849` `dispatchBatchOp`** (rev 3 정정 — 직전 revision 의 `:861-` 은 dispatcher 안 `add_system` case 첫 줄 위치였음). `add_system` 허용 op 메시지 위치: `:168`, `:183`, `:191`, `:312`, **`:913` (runtime LLM 노출)**. **rev 7 caveat (Major M4)**: `:168/183/191/312` 의 invalidOp 인용은 일부가 *주석/log 메시지* 일 가능성 — 진입 시 grep `"add_system"` `ToolOperations.fs` 로 *런타임 LLM 노출* 라인만 분리 후 갱신. `:913` 만 BATCH\_ERROR 로 LLM 노출 확정.
- Mermaid/CSV caller 경로 (rev 7 정정): `Solutions/Convert/Ds2.Mermaid/Import/Targets/MapperTargetPlanning.fs` + `Solutions/Convert/Ds2.Mermaid/Import/Targets/MapperTargets.fs` + `Solutions/Convert/Ds2.CSV/CsvMapper.fs` (직전 revision 의 `Solutions/Core/Ds2.Core/Store/Mermaid/...` / `Csv/...` 는 stale).
- LlmAgent fsproj (rev 7 신설): `Solutions/Core/Ds2.LlmAgent/Ds2.LlmAgent.fsproj` — ProjectReference 가 `Ds2.Editor` 단일 (Critical 2). `Ds2.Core` 직접 참조 추가 필요.
- KnownNames (19종): `Solutions/View/Ds2.View3D/Ds2.View3D.Core/Types.fs:133-153`
- C# tool wrapper: `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs`
- Tool name SSOT: `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs`
- 디렉토리 가이드: `Solutions/Core/Ds2.LlmAgent/CLAUDE.md` (작업 방식 / phase 정책 / 결정 7/8)
- 도메인 개념 참고: `/f/Git/kwak/kwak/DsConcepts/ds.md`, `/f/Git/kwak/kwak/DsConcepts/ds-entities.md`

---

## 9. 주의 사항 / 시나리오

### 9.1 ResetReset Arrow 위치 — *정정*

ResetReset Arrow 의 parentId = PassiveSystem.Id (즉 PassiveSystem 내부에 위치). 단 `queueAddArrow` (`ToolOperations.fs:322`) 자체는 **Active/Passive 구분 없이 "같은 System 안 두 Work" 만 검사** — 즉 도구 시그니처에서 enforce 되는 것은 아님 (review R2 정정). helper 가 PassiveSystem.Id 를 parentId 로 자체 부여하여 결과적으로 PassiveSystem 내부에 배치되는 패턴.

### 9.2 decision 6 strict 의 silent 오작동 차단 시나리오

helper 가 만든 ApiDef.Id 가 batch ref table 에 등록 안 되는 경우 (apiDef\*Ref omit 가 허용된다고 가정):
1. LLM 이 helper 호출 → ApiDef×N 생성 (Id 는 plan 안에만, ref table 미등록).
2. 후속 add_call 의 apiDefId 는 ref 로 표현 불가 → LLM 이 임의 GUID 또는 다른 ref 를 잘못 참조.
3. ApiCall.apiDefId 가 *다른 device 의 ApiDef* 를 가리킴 → store 로딩 시점엔 GUID 가 valid 하므로 BATCH_ERROR 도 안 남.
4. 런타임에 잘못된 device 가 호출됨 (silent miscompile).

→ decision 6 의 strict required 가 이 silent skip 을 *구문상* 차단. 사용자 명시 결정 (`ApiDef ref 누락은 error case 로 간주`) 의 정당화.

### 9.3 robot all-pairs + quota 트립

**rev 11 변경 후 진입 빈도 감소**: D7 default 가 `"none"` 으로 바뀌어 사용자가 *명시적으로* `opposing:"all-pairs"` 선택하지 않으면 본 시나리오에 진입하지 않음. 도메인 룰 (`2.modeling.md` §3.3 line 181 ROBOT opposing 없음) 에 부합한 default 라 over-modeling 방어.

decision 7 의 `opposing="all-pairs"` 를 사용자가 *명시적으로* 선택할 시 **N≥9** 부터 cascade total op 수가 quota cap 50 을 초과 (rev 5 산식: `3 + 2N + N(N-1)/2` — N=8=47 pass / N=9=57 / N=10=68). helper 가 cascade 발행 전 예상 op 수 계산 → 사전 BATCH\_ERROR (D8 채택 정책). error 메시지 권장 형태는 §4.3 참조 — **회복 단서 1순위는 default `"none"` 변경 (사용자가 정말 ResetReset 필요한지 재검토)**, 2순위는 `"chain"` 변경 (chain N=10 = 32 op, quota 통과). apiNames 분할 = device 의미 분리라 1·2순위가 우선.

### 9.4 BATCH_ERROR 메시지 형식

기존 queueBatch 의 형식을 그대로 재사용:
```
BATCH_ERROR: op[N] '<opName>' 실패 — VALIDATION_ERROR: <상세> (rollback applied, 0 ops queued in this call)
```
decision 6 의 ref required 위반 메시지 예:
```
BATCH_ERROR: op[2] 'add_cylinder' 실패 — VALIDATION_ERROR: apiDef1Ref 가 비어있습니다 (decision 6: ApiDef ref required) (rollback applied, 0 ops queued in this call)
```

### 9.5 untracked 파일 주의

`2.modeling.md` 가 untracked 라 본 작업 도중 git 작업 시 누락되지 않도록 주의 (사용자 규칙: 임의 git commit 금지, `--git-add`/`--git-commit` 명시 시에만). §0 선결 조건과 동기.

### 9.6 add_call 자동 도출 — naming / originFlowId 일치 보장

`add_call(workId, apiDefId)` 시:
1. ApiDef.Id 로 ApiDef 조회 → `ApiDef.Name` = apiName 자동.
2. `ApiDef.ParentId = System.Id` → `System.Name` = devicesAlias 자동.
3. `Call.Name` = `{System.Name}.{ApiDef.Name}` 조립.
4. **`ApiCall.OriginFlowId` 자동 도출** (rev 3 명시 / rev 5 강조): `workId → Work.Parent.Flow.Id`. LLM 명시 불필요. **구현 시 `apiCall.OriginFlowId <- Some flow.Id` 단계를 명시적으로 작성** — `createAndRegisterApiCall` (Device.fs:152) 은 ApiDefId 만 설정하므로 그 패턴을 "재활용" 하면 OriginFlowId 누락 회귀. `ImportPlanApplyApiCallTests` (§5.6) 가 *OriginFlowId* 까지 binding 정확성 검증.
5. **룰 C 런타임 검증** (§3.2 add\_call 변경점): `ApiDef.ParentSystem.IsActive == true` 인 경우 BATCH\_ERROR ("ApiDef 는 Passive System 의 자식이어야 합니다 (룰 C)").
6. **Call.Name 충돌 검사 유지** (rev 3 명시 — m5): 같은 Work 내 두 add\_call 이 같은 ApiDef 를 가리키면 fullName 동일 → 기존 `hasCallNameClash` 발화로 BATCH\_ERROR.

GUI 측 Call 도 동일 규약 (`devicesAlias = PassiveSystem.Name`) 이라 *Call 명명* mismatch 없음. **단** PassiveSystem.Name 이 사용자 helper 인자와 *동일* (자동 접미 없음) 정책이라 GUI default 의 `{flow}_{devAlias}` 패턴과 *PassiveSystem 명명 자체* 는 다름. `add_cylinder("Cyl1")` → PassiveSystem.Name = `"Cyl1"`, Call.Name = `"Cyl1.ADV"`. GUI 가 만든 데이터를 helper 가 그대로 재현하지는 *않음* — helper 의 의미는 "LLM 의 의도대로 alias 직접 작명". HelperGuiParityTests (§5.6) 의 비교 범위는 cascade *구조* 만.

### 9.7 ImportPlanDeviceOps 의 *외부 노출* 정책 + 동명 함수 disambiguation

**rev 7 전면 재작성** — 직전 revision 의 "옵션 b 권장" 은 §4.2 rev 4/5 결론 ("internal wrapper 신설") 과 충돌하던 stale. rev 7 부터는 §4.2 / §5.2 의 결론을 단일 SSOT 로 사용.

현재 `module internal ImportPlanDeviceOps` in `Ds2.Core/Store/ImportPlan.Device.fs:7`. 핵심 함수들 (`ensureSystem:37` / `ensurePendingWork:99` / `ensureApiDef:124` / `buildWorkArrows:162`) 은 모두 `let private` — *module 안* 에서만 가시. F# `let private` 은 IL private 으로 컴파일 → **`InternalsVisibleTo` 만으로는 노출 *불가*** (Critical, rev 7 신규 명시).

**검증된 제약 2건 (rev 7 추가)**:
1. **F# let private 가시성**: 위 4개 함수는 `module internal` 외부 어디서든 호출 *불가* — InternalsVisibleTo 효과 없음. 외부 caller (`linkCallsToDevices*` 라인 218/230 — `let` = module 기본 internal) 만이 module 내부 동일 namespace 에서 호출 가능.
2. **`Ds2.LlmAgent.fsproj` 의 ProjectReference 부재**: 현행 fsproj (`Solutions/Core/Ds2.LlmAgent/Ds2.LlmAgent.fsproj:28`) 는 `<ProjectReference Include="..\Ds2.Editor\Ds2.Editor.fsproj" />` 단 한 줄 — **Ds2.Core 직접 참조 *없음***. F# 의 `InternalsVisibleTo` 는 *직접 ProjectReference* 가 있어야 효력 발생 (transitive 확장 안 됨). `Ds2.Core/AssemblyInfo.fs:11` 의 `[<InternalsVisibleTo("Ds2.LlmAgent")>]` 는 의도가 직접 참조였을 가능성이 높지만 *현재* 는 미연결.

**채택 정책 (rev 7 — §4.2 / §5.2 와 정합)**:
- **(a) `Ds2.LlmAgent.fsproj` 에 `<ProjectReference Include="..\Ds2.Core\Ds2.Core.fsproj" />` 추가** — InternalsVisibleTo 가 효력 발휘. transitive 가 아닌 직접 참조 강제.
- **(b) `Ds2.Core/Store/ImportPlan.Device.fs` 안에 신규 wrapper `buildPassiveDeviceCascade` 신설 (`let internal`)** — `let private` 이 아닌 `let internal` (또는 module 기본 visibility = internal) 명시. **반드시 `let internal` 로 작성** (rev 7 강조 — `let private` 으로 작성 시 LlmAgent 에서 호출 불가, 컴파일 에러).
- **(c) wrapper 가 module-private 함수들 (`ensureSystem` 등) 을 *module 내부에서* 호출** — caller 가 module 외부에 있어도 wrapper 만 노출되면 됨.
- 옵션 c (Editor 경유 thin wrapper) 도 이론상 가능하나 Editor 의 Authoring 무수정 정책 (CLAUDE.md 결정 7) 위반 가능 → 비채택.

**기존 옵션 a/b 권장 표기 폐기** (rev 7) — §4.2 rev 5 의 wrapper 신설 정책으로 흡수.

#### 동명 함수 disambiguation (rev 3 신설 — Major M5)

`linkCallsToDevices` 라는 동일 이름의 함수가 *두 module* 에 존재:
- ✅ **사용 대상**: `Ds2.Core.Store.ImportPlanDeviceOps.linkCallsToDevices` (`Solutions/Core/Ds2.Core/Store/ImportPlan.Device.fs:218`) — 본 작업 helper 가 사용할 정전.
- ❌ **혼동 주의**: `Ds2.Editor.Store.Nodes.Device.linkCallsToDevices` (`Solutions/Core/Ds2.Editor/Store/Nodes/Device.fs:225`) — 별도 Editor 측 함수. 역할 다름.

helper 구현 시 `open Ds2.Core.Store` 명시 또는 fully qualified name (`ImportPlanDeviceOps.linkCallsToDevices`) 사용. `open Ds2.Editor.Store.Nodes` 와 동시에 쓰면 마지막 open 이 우선 — 의도치 않은 함수 호출 위험.

---

## 10. rollback / 회복 가이드 (rev 3 신설 — Major M16)

### 10.1 Layer commit 단위 rollback

작업 진행 도중 design flaw 발견 / 빌드 회복 불가 시:

| 시점 | 회복 절차 |
|---|---|
| **L1 commit 후 L2 진입 전 발견** | `git reset HEAD~1` (working tree 보존) 또는 `git revert HEAD` (history 보존). visibility / `buildWorkArrowsAllPairs` 신설은 외부 영향 적어 reset 무리 없음 |
| **L2 commit 후 L3 진입 전 발견** | `git revert HEAD` 권장 (이미 다른 세션이 fetch 했을 가능성). L2 의 dispatchBatchOp 갱신은 LLM 동작 자체 영향 — revert 안 하면 BATCH\_ERROR 다발 |
| **L3 commit 후 발견** | revert 의 cascade 영향 큼 (PromakerToolNames 21 → 16 으로 회귀 시 모든 sample / done 문서 / CLAUDE.md hardcoded 재정정) — *전제 결정 변경* 인지 *구현 버그* 인지 분류 후 buggy 부분만 patch commit 우선 검토 |
| **Drift / Prompt 일괄 갱신 commit 발견** | hardcoded 갯수 (16 / 21) 가 일관 안 맞으면 false-green 회피용 sanity test 가 RED → 차단됨. 본 단계 commit 은 *atomic* 으로 모든 hardcoded 동시 갱신 필수 |

### 10.2 결정 변경 시 절차

§3.0 의 권한 표 기준:
- ⚙️ Claude 추정 결정 (D1, D2, D3, D4, D5, D8) 변경: 본 todo §3.1 갱신 + commit message 에 "결정 X 변경: <reason>" 표기. 기존 commit 의 결정 의존 부분이 invalidate 되면 §5.1 호환성 invalidate 절에 추가.
- 🔒 사용자 명시 결정 (D6, D7) 변경: **반드시 사용자 confirm 필요**. 변경 합의 후 본 todo + done 문서 + commit message 모두 sync.

### 10.3 canary 오작동 회복

§6.4 회수 절차의 *역방향*:
- 두 prompt 파일 첫 줄 `<!-- canary: ... -->` 가 의도치 않게 사라진 경우: git history grep 으로 원본 복원 (`git log -p Apps/Promaker/Promaker/LlmAgent/Prompts/`).
- canary trigger 변경이 의도라면 PromptCanaryTests 의 `Assert.Contains(...)` 와 본 todo §6.1 의 trigger 표기 모두 동시 갱신.

### 10.4 fixture / 영속화 회복

`Solutions/Tests/Ds2.LlmAgent.Tests/Fixtures/WithCyl.json` (또는 §0.2 옵션 A 의 doc 폴더 경로) 누락 시:
- 작성자 머신 `F:\tmp\WithCyl.json` 또는 `Solutions/Core/Ds2.LlmAgent/doc/WithCyl.json` 이 보존되어 있으면 그곳에서 복구.
- 보존 안 되어 있으면 Promaker GUI 에서 `add_cylinder` 동작 (PassiveSystem cascade 자동 생성) 후 export 로 재생성. 재생성 시 `HelperGuiParityTests` 의 expected 도 동시 갱신.
