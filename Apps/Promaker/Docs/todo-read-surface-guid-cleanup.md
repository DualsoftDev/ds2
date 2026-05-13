# TODO — Read surface GUID-free 정렬 (Phase 6 후보)

> Phase 5 (mutation op-layer 15종 일소) 이후 잔존 read 6종에 남은 GUID 입출력을 청산하고,
> `export_model_doc` 의 scope 인자로 list/describe 류를 흡수하여 read surface 압축.
>
> 본 문서는 **--plan 모드 논의 결과 + 5인 reviewer 검증 통과 사항 + 메타 review 검증 통과 사항 + v4 closure list 결정** 반영의 transfer 메모.
> 실제 구현은 사용자 명시적 지시 (§0.3 trigger 발화) 후 착수.
>
> **v4 (2026-05-13)**: closure list 5건 모두 결정 완료. SSOT commit #1 본문 작성 진입 가능 상태. 결정 사항 §7.2 v4 round + 본문 §3.1 / §4 각 절 "**결정**" 라벨 참조. 작업 worktree: `F:/Git/ds2/phase6-readsurface` (브랜치 `phase6-read-surface-guid-cleanup`).

---

## 0. 새 세션 진입 가이드 (필독)

### 0.1 읽기 순서

0. **§0.5 새 세션 즉시 작업 진입 가이드** ★ — 새 세션이라면 본 절부터. worktree 진입 명령 + 현 상태 + 다음 step 명세.
1. **§0.2 closure list** — SSOT commit #1 진입 전 결정 필요했던 5건 + 부속 1건 (v4 모두 ✅ 결정 완료)
2. **§0.3 trigger 발화** — 사용자 의도 확인 (ambiguous 발화 오해 방지)
3. **§0.4 용어 사전**
4. **§1.1 Phase 4 변형 B 충돌 검증** — 진입 prerequisite (v2 통과)
5. **§1 작업 목표 + §2 배경** — 맥락
6. **§3 설계 + §4 결정 항목** — 본문 (v4 결정 라벨 위치: §3.1 path notation / §3.2 표 라벨 / §4.1 envelope flag / §4.5 find_by_name 출력 / §4.6 thin lookup)
7. **§5 작업 단계** — 구현 시점 참조
8. **§7.2 review 처리 이력** — 본 메모 변경 추적 (v1 → v4 round 누적)

### 0.2 SSOT commit #1 진입 전 closure list (✅ v4 모두 결정 완료)

본 **5건 + 부속 1건 (path notation, 표 하단)** 이 결정되지 않으면 SSOT commit #1 의 본문 작성 불가. **v4 (2026-05-13) 사용자 결정 완료**.

| # | 항목 | 본문 위치 | v4 결정 |
|---|---|---|---|
| 1 | envelope 페이로드 flag 이름 + v0 §2.0 top-level 키 enum 갱신 | §4.1 | **`view: full \| partial`** 채택. format 무관 (yaml/json 양쪽 적용 — top-level mapping key). legacy/unknown-key 정책: 부재 시 ERROR (명시 강제, ii). |
| 2 | path → entity 결정 메커니즘 | §4.6 | **thin lookup `tryFindEntity : DsStore → string → (EntityKind * Guid) option`** 채택. **kind 인자 없음** — path 자체에서 `proj/system/flow/work/call` 깊이로 EntityKind 유추 가능. |
| 3 | find_by_name path unique 정책 (4-way) | §4.5 | **폐기**. `find_by_name` 출력 spec 자체를 `[ (EntityKind, path) ]` **목록 반환** 으로 격상 — 동명 sibling 도 N건 그대로 노출. ordinal suffix / GUID tail / sibling invariant / sanitize 4 후보 모두 불필요. |
| 4 | partial 판정 기준 | §4.1 / §4.7 / §3.1 | **(b) 실제 truncation 발생 여부** 채택. `exportToJson` walk 내 `truncated: bool ref` 한 줄로 추적, 누락 1건 이상이면 `view: partial`. depth schema `>= 0` 정수 사전 거부 포함. |
| 5 | `PromakerToolNamesDriftTests.fs` 회귀 가드 격상 | §5.1 | **set equality + `opLayerStaleTokens` 격상** 채택. read-4종 stale token 추가. |

**부속 결정 (path notation, v4)**: (α) 현 dual-accept 유지 — wire 입력은 `.` 와 `/` 둘 다 허용, 정규형 dot (현 `normalizePath` 동작 유지). SSOT 본문 path 예시 dot 유지. **root 절대 경로 표기는 leading `.`** (예: `.Proj1.SysA.Flow1`) — 신규 어휘 룰. 이름의 `.` 금지 invariant 유지 (변경 0).

### 0.3 사용자 trigger 발화

본 작업 착수 의도를 명확히 하는 발화 (ambiguous "좋은데?" / "괜찮네" 오해 방지):

- **착수 (commit #1 SSOT 본문 작성)**: "Phase 6 commit #1 진행" / "SSOT 작성" — closure list ✅ 결정 완료, 다음 단계.
- **착수 (commit #2 코드/prompt/test)**: "Phase 6 commit #2 진행" / "Phase 6 구현" — commit #1 머지 후.
- **개별 closure 재논의 (필요 시)**: "closure #N 재논의" 형태로 번호 명시.

위 형태가 아닌 발화는 **본 메모 자체 정책 (논의 only — Claude Code plan mode 가 아님)** 유지.

### 0.5 새 세션 즉시 작업 진입 가이드 (★ 새 세션 진입 시 이 절부터 확인)

본 절은 새 Claude Code 세션이 본 메모를 처음 읽고 즉시 작업 진입할 수 있도록 self-contained.

**진입 위치**:
```powershell
# worktree 로 직접 진입 — main 의 메모는 v3 stale, worktree 의 메모만 v5 (현 절 포함) 보유.
cd F:\Git\ds2\phase6-readsurface
git status   # 현 브랜치 phase6-read-surface-guid-cleanup, working tree clean 확인
git log --oneline -10   # commit #1 (SSOT) 와 commit #2-chunk1 (코드 일부) 확인
```

**현재 상태 (v6 시점, 2026-05-13)**:
- ✅ closure list 5건 + 부속 1건 모두 사용자 결정 완료 (§0.2 + §7.2 v4 round).
- ✅ Phase 4 변형 B 충돌 검증 통과 (§1.1).
- ✅ **SSOT commit #1 완료** (`e85edba`): `yaml-protocol-v0.md` §1.7 결정 표 / §2.1 top-level 키 enum / §2.5.1 path resolver 절 / §2.7 룰 #7,#8 / §2.8 partial export view-only spec / §4 도구 시그니처 / §6 phase 표. `done-yaml-protocol-implementation.md` §2 phase 표 + §3.0 후속 cycle.
- ✅ **commit #2 chunk-1a/b 완료** (`eab4537`): chunk-1a (SSOT view 정책 정정) + chunk-1b (read 4종 일소). 자가 검열 + 3-reviewer review 통과. 자세히는 §7.2 v5 round.
- ✅ **commit #2 chunk-1c 완료** (working tree staged — commit 직전 상태): `exportToJsonScoped` 본체 + `validateModelByPath` + `tryPathOf` 안전 API + summary metadata (`totalEntities/emitted/budget`) + budget 500 격상. 자가 검열 1차 + budget patch 자가 검열 + summary metadata 자가 검열 + 외부 `--review` 처리 (Major 3 / Minor 10 분류 처리) 모두 통과. 자세히는 §7.2 v6 round.
- ⏸ **commit #2 chunk-2 부터 진입 대기** — chunk-1c staged 변경 commit 후 chunk-2 (`EditorChangeDigest.cs` 어휘 sweep) 부터 진입. 또는 chunk-1c staged 그대로 두고 chunk-2/3 까지 묶어서 commit 도 가능 (사용자 결정).

**v5 시점 commit #2 chunk-1a/b 까지의 실제 코드 변경 (참조용)**:
- `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs`: `tryFindEntity : DsStore → string → (EntityKind * Guid) option` + `pathOf : DsStore → EntityKind → Guid → string` 신설 (line 803 근처). `apply` 의 `view` 키 처리 (full/부재 허용, partial 거부). `exportToJson` envelope 에 `view: "full"` emit.
- `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs`: `listProjects` / `listSystems` / `describeSystem` / `describeSubtree` / `formatProjectList` / `formatSystemList` / `formatFindResults` / `indent` / `arrowTypeName` 일소 (-171 line).
- `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs`: `ListProjects` / `ListSystems` / `DescribeSystem` / `DescribeSubtree` / `ParseGuidOrThrow` 일소. `FindByName` 의 inline format 안에서 `ModelProtocol.pathOf` 호출로 정확한 dot path emit.
- `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs`: `All` 배열 10 → 6 (read 4종 제거).
- `Solutions/Tests/Ds2.LlmAgent.Tests/DescribeSubtreeTests.fs`: 통째 삭제 + fsproj 컴파일 목록에서 제거.
- `Solutions/Tests/Ds2.LlmAgent.Tests/PromakerToolNamesDriftTests.fs`: sanity count 10 → 6 + 함수 이름 + 주석 + line 124-125 의 일소된 tool snake_case 단언 정리.

**v6 시점 commit #2 chunk-1c 까지의 실제 코드 변경 (참조용 — working tree staged, 미 commit)**:
- `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs` (+402): 
  - `pathOf` → `tryPathOf : DsStore → EntityKind → Guid → string option` (primary safe API) + compat wrapper `pathOf` (`Option.defaultValue ""`). System 분기 재귀 통일 (`tryPathOf store Project p.Id`). orphan System → None (1-segment path 가 Project round-trip 오인 회귀 회피). path-unsupported kinds (Button/Lamp/Condition/Action/ApiDefCategory/DeviceRoot/Arrow) → 명시적 `_ -> None`.
  - `exportToJsonScoped : DsStore → string option → int option → JsonDocument` 신설. 두 인자 모두 None → `exportToJson` delegate. 그 외 partial entry — 전체 export 후 `JsonNode` post-process (path scope + depth cap + budget). path 미존재 = `invalidOp VALIDATION_ERROR` fail-fast.
  - private helper 5종: `applyPathScope` (segs 별 systems/flow/works/calls/apis 필터) / `applyDepthCap` (절대 depth 0=project 1=system 2=flow/api 3=work 4=call 룰) / `applyEntityBudget` (limit 초과 시 후미 systems pop) / `countEntities` (System+Flow+Work+Call+ApiDef 합 — Arrow / device / attribute 제외) / `setView` / `isActiveSystem`.
  - `[<Literal>] PartialBudget = 500` — 50 → 500 격상 (외부 review M2 후속 + 사용자 의견). PoC scale 무영향 + 안전 catch-all.
  - **summary metadata key** (사용자 채택): 절단 발생 시 envelope 에 `summary: { totalEntities, emitted, budget }` 신규 키 emit. LLM 의 "513 vs 50000" 후속 호출 의사결정 단서. systems 는 항상 array (type 단일성 유지 — union 회피). 정상 (view: full) 결과에는 summary 부재.
  - `apply` 함수에 `summary` 키 사전 거부 분기 — view: partial 거부와 동일 패턴. round-trip 시 view:full export 결과에는 summary 없으므로 무영향.
- `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` (+95/-21):
  - `validateModelByPath : DsStore → string option → string` 신설 — path 기반 (Project/System/Flow path → 각 scope, 부재 = global).
  - `validateModelByGuid` 잔존 (Deprecated 라벨) — ValidateModelTests.fs 가 chunk-3 에서 path 기반 재작성될 때까지 backward-compat.
  - `formatScopeLabel` 가 `System(id=GUID)` → `System(path=.Proj.Sys)` emit — GUID 노출 회피.
  - local 2-level path resolver (`pathSegmentsForScope` + NFC normalize / `trySystemPathLocal` / `tryFlowPathLocal`) — fsproj 컴파일 순서상 `ModelProtocol.tryFindEntity` forward-ref 불가라 인라인. **후속 cycle PathResolver 모듈 통합 권고** (done §3.0 SRP split 작업과 묶음).
- `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs` (+24/-25):
  - `ExportModelDoc(format, path?, depth?)` — `depth < 0` 사전 거부. `exportToJsonScoped` 호출.
  - `ValidateModel` — 'global' literal + GUID 분기 폐기. dotted-path 만 받음 (부재 = global). cache key sentinel = `""` (empty path).
  - `FindByName` inline emit — `tryPathOf` 직접 호출 + None 분기에서 `<orphan:Name>` (System) / `<unsupported:Kind>` (그 외) marker emit (외부 `--review` M2 후속).
- `Apps/Promaker/Promaker/LlmAgent/LlmTurnContext.cs` (+4/-1): `_validateCache` 의 doc-comment 에 sentinel `""` 명문화 (Phase 6 의 "global" literal / GUID 폐기 sync).
- `Solutions/Tests/Ds2.LlmAgent.Tests/ValidateModelTests.fs` (+2/-1): 1건 assertion 정정 (`System(id=` → `System(path=.Project.Sys`). 다른 fact 16건은 `validateModelByGuid` 잔존 덕에 그대로 통과.

**v6 결정 (사용자) 사항**:
- (1) budget 50 → 500 격상 — Reviewer Major-1 후속. PoC scale 무영향, 케이스 A ("path=.P.S + work 100 → 약 200 entity") 가 한도 안 → SysX 보존.
- (2) `systems: null` marker (이전 v5 도입) → 폐기. 대신 `summary` metadata 키 (`totalEntities/emitted/budget`) 로 진단 풍부화. systems 는 항상 array.
- (3) `countEntities` 카운트 단위 = 5 EntityKind (System/Flow/Work/Call/ApiDef). Arrow / device / attribute 제외.

**SSOT `yaml-protocol-v0.md` 후속 commit 작업** (chunk-1c 범위 외, 별도 commit):
- §2.0 top-level 키 enum 6개로 확장 (`protocol / project / systems / patch / view / summary`).
- §2.7 룰 #9 신설 — "apply/validate 입력의 `summary` 키 거부" (코드는 이미 적용됨, 본문 sync).
- §2.8 partial export spec 본문에 budget=500 + summary metadata 구조 + countEntities 단위 (5 EntityKind) 명시.
- `done-yaml-protocol-implementation.md` §3.0 후속 cycle 에 PathResolver 모듈 SRP split 추가 (ModelProtocol.fs 의 tryPathOf / tryFindEntity 와 ToolOperations.fs 의 pathSegmentsForScope / trySystemPathLocal / tryFlowPathLocal 단일화).

**즉시 진입 가능 단계 (chunk-2 부터)** — chunk-1c 는 v6 시점 staged 상태:

0. ✅ **chunk-1c — 완료 (staged)**. 자세한 변경 내역은 위 "v6 시점 ... 실제 코드 변경" 절. 자가 검열 3차 (1차 chunk-1c 전체 / 2차 budget 500 patch / 3차 summary metadata) 모두 통과. 외부 `--review` 처리 완료 (Major 3 / Minor 10 — Major 모두 즉시 수정, Minor 일부 반론 / 후속 cycle deferral). 빌드 0 오류 / 314 test 통과 baseline 유지. **commit 직전 상태**.

1. **chunk-2 — `EditorChangeDigest.cs` 어휘 갱신** (작은 변경):
   - `EditorChangeDigest.cs:18,142,165` — 런타임 합성 message 의 read 도구 어휘 갱신. list_projects/list_systems 권고 제거, validate_model 권고 유지.

2. **chunk-3 — 테스트 (`ValidateModelTests.fs` 재작성 / `DriftTests` set equality 격상 / `ExportModelDocPathDepthTests.fs` 신규)** (큰 작업):
   - `ValidateModelTests.fs` 재작성: 'global' literal fact 4개 제거 + path scope fact 신규 + scope 미지정 = 전체 fact 신규 + `validateModelByGuid` 호출 16건 → `validateModelByPath` 로 일괄 전환 + 본 chunk-1c v6 의 deprecated `validateModelByGuid` 일소.
   - `PromakerToolNamesDriftTests.fs` closure #5 v4 격상 (chunk-1b 에서 count 만 갱신, 본 chunk 에서 set equality + stale token 추가):
     - `Assert.Equal(6, listed.Count)` → `Assert.Equal<Set<string>>(expectedSet, listed)` set equality.
     - `opLayerStaleTokens` 에 read-4종 (`list_projects` / `list_systems` / `describe_system` / `describe_subtree`) 추가.
   - `ExportModelDocPathDepthTests.fs` 신규: 회귀 fact (v6 시점 명세 — 본래 5건 + v6 추가 6건 = 총 11건):
     - (a) `view: partial` emit — path 또는 depth 로 truncation 발생 시.
     - (b) `view: full` emit — path 미지정 + depth 미지정 OR 큰 depth 인데 truncation 0건.
     - (c) `view: partial` doc → `apply_model_doc` 입력 → ERROR + 메시지 lock (substring).
     - (d) `path` 없이 `depth=999` (큰 정수, full 결과) → `view: full` emit.
     - (e) `depth=-1` / `depth=1.5` / wire schema 위반 → 사전 거부 ERROR.
     - **(f) v6 추가**: 절단 시 `summary: { totalEntities, emitted, budget }` metadata 키 emit + 정상 (view: full) 결과에는 summary 부재.
     - **(g) v6 추가**: `summary.totalEntities >= summary.emitted` invariant lock-in (semantic 검증).
     - **(h) v6 추가**: `summary.budget == 500` lock-in (PartialBudget literal 동기화 — drift 회귀 차단).
     - **(i) v6 추가**: `apply_model_doc` 입력에 `summary` 키 등장 → ERROR + 메시지 lock ("summary 는 partial export 진단 metadata 전용 ...").
     - **(j) v6 추가** (M2 lock-in): orphan System (project 미부착) 을 `find_by_name` 으로 검색 → 출력 path 가 `<orphan:Name>` marker. `<missing>` / 빈 문자열이 아님.
     - **(k) v6 추가** (Outlier 3 lock-in): `tryPathOf` 가 path-unsupported EntityKind (Button/Lamp/Condition/Action/ApiDefCategory/DeviceRoot/Arrow) 에 대해 None 반환 — round-trip identity 회귀 차단.
   - 신규 fact 명명 컨벤션 = 한글 backtick + prefix `6f-N` (Phase 6 → 6f).

**chunk-3.5 (v6 신설) — SSOT `yaml-protocol-v0.md` 후속 commit** (테스트와 묶거나 별도 cycle):
   - §2.0 top-level 키 enum 6개로 확장 (`protocol / project / systems / patch / view / summary`).
   - §2.7 룰 #9 신설 — "apply/validate 입력의 `summary` 키 거부" (코드 동작 본문 sync).
   - §2.8 partial export spec 본문에 budget=500 + summary metadata 구조 + countEntities 단위 (5 EntityKind: System/Flow/Work/Call/ApiDef — Arrow / device / attribute 제외) 명시.
   - `done-yaml-protocol-implementation.md` §3.0 후속 cycle 에 PathResolver 모듈 SRP split 추가 (ModelProtocol.fs 의 tryPathOf / tryFindEntity 와 ToolOperations.fs 의 인라인 local resolver 단일화).

3. **chunk-4 — Prompt 7 파일 sweep** (mechanical sweep):
   - `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md`: "현 도구 풀세트" 표 read 6 → 2. `export_model_doc` scope (path/depth) 사용 예시. 일소 도구 4종 어휘 sweep.
   - `Apps/Promaker/Promaker/LlmAgent/Prompts/2.modeling.md` / `1.entities.md` / `4.attachments.md`: read 도구 어휘 sweep.
   - `Apps/Promaker/Promaker/LlmAgent/Prompts/CLAUDE.md`: 풀세트 카운트 10 → 6.
   - `Apps/Promaker/Promaker/LlmAgent/Prompts/chat-simulation/CLAUDE.md`: sibling drift 동기화.
   - `Apps/Promaker/Promaker/LlmAgent/Prompts/9.environment.md`: read 도구 어휘 영향 확인.

5. **chunk-5 — 외부 문서 sweep** (5+ 파일):
   - `Apps/Promaker/Paper/paper.md:82`: "읽기" 도구 6종 → 2종 + `export_model_doc` path/depth 설명.
   - `Apps/Promaker/Docs/done-promaker-llm-roundtrip-optimization.md`: §6.2 cold-start 룰 직격 — "snapshot 없는 첫 turn 에 `list_projects` 1회 cold-start 허용" → `export_model_doc(depth=0)`. line 16/281/288/291/293/370/491/524/578/589 sweep.
   - `Apps/Promaker/Docs/Poc/e2e-cache-hit.fsx:79`: PoC script — read 도구 호출 갱신.
   - `Solutions/Core/Ds2.LlmAgent/CLAUDE.md:115`: sibling fence — read 6종 → 2종 카운트.
   - `Solutions/Core/Ds2.LlmAgent/doc/todo-*.md`, `done-batch-mcp-call.md`: historical 인지 활성 todo 인지 확인 후 sweep (현 권고: historical 은 그대로, 활성만 갱신).

6. **chunk-6 — 광역 grep 잔재 0건 확인 + 자가 검열 + commit #2 confirm**:
   - 광역 grep: `describe_system|describe_subtree|list_systems|list_projects` 잔재 0건 (parser fixture exclude).
   - 자가 검열 (Agent 위임) — CLAUDE.md trigger ①②③④⑤ 모두 충족. 리포트 5요소.
   - commit #2 사용자 confirm — 메시지 안 = "Phase 6 commit #2 — read surface GUID-free 정렬 (코드/test/prompt/외부 문서)".

**Trigger 발화 형식 reminder** (§0.3):
- 다음 step trigger = `Phase 6 chunk-1c commit` (staged 변경 commit) → `Phase 6 chunk-2 진행` 또는 `Phase 6 commit #2 계속` (chunk-2 부터 계속).

**branch 정책 (v6)**:
- 본 메모 + commit #2 chunk-1a/b 변경은 `phase6-read-surface-guid-cleanup` worktree (`F:/Git/ds2/phase6-readsurface`) 에 commit. main 머지는 commit #2 완료 + push 후.
- main 의 본 메모 = v3 stale (phase6 작업 중 main 변경 0).
- chunk-1c staged 변경은 commit 직전 — 사용자 trigger 발화 (`Phase 6 chunk-1c commit`) 시 commit 진행.

**v6 시점 git log (참고)**:
- `eab4537` (worktree HEAD) — Phase 6 commit #2 chunk-1: read surface GUID-free 정렬 (4종 일소 + helper)
- `e85edba` — Docs/Phase 6: SSOT commit #1 (설계만)
- `1b20aa7` (main, base) — Docs: Phase 6 todo v4 — closure 결정 완료
- working tree staged (chunk-1c, 미 commit): 5 파일 = `ModelProtocol.fs` / `ToolOperations.fs` / `ModelTools.cs` / `LlmTurnContext.cs` / `ValidateModelTests.fs`. 변경 line ≈ +600/-90.
- v6 todo 갱신 본 commit 안 포함 권장 — `Apps/Promaker/Docs/todo-read-surface-guid-cleanup.md`.

### 0.4 용어 사전

- **SSOT**: Single Source of Truth — 본 작업에서는 `yaml-protocol-v0.md`
- **round-trip**: `apply(export(model)) ≡ model` invariant
- **view-only**: 표면 표현 한정 사용. wire 입력으로 재사용 불가
- **sibling drift**: 같은 사실이 여러 곳에 기록되어 한쪽만 갱신될 때의 어긋남
- **op-layer**: Phase 5 일소된 mutation 도구 15종 (`apply_operations` / `add_*` / `remove_entity` / `rename_entity`)
- **wire**: LLM ↔ MCP 간 실제 직렬화 JSON object
- **partial export**: path/depth 스코프 지정 export — 전체 export 와 의미 분리
- **envelope**: export 결과의 **최상단 mapping (format 무관 — yaml/json 양쪽 동일)**. v6 키 enum 6개: `protocol` / `project` / `systems` / `patch` / `view` / `summary` (`summary` v6 추가 — partial export 진단 metadata, §0.5 v6 round 참조)
- **summary metadata**: 절단 발생 시 emit 되는 envelope 키. 구조 `{ totalEntities, emitted, budget }`. LLM 의 후속 호출 전략 (좁혀 재호출 / 포기) 의사결정 단서. v6 도입.
- **canonical**: schema 의 정규 표현 (생략 가능한 키의 기본값)
- **parent chain resolver**: entity 의 부모 path 를 root 까지 추적하는 helper
- **delta-only snapshot**: revision 변경 시에만 새로 첨부되는 sticky snapshot (`done-promaker-llm-roundtrip-optimization.md` §6.2)

---

## 1. 작업 목표

Phase 5 이후 잔존 read 6종 중 4종이 GUID 입출력. SSOT (`done-yaml-protocol-implementation.md §6` 주의 사항 #5: "GUID 는 LLM 에 절대 노출 금지") 와 직접 충돌하는 op-layer 잔재.

- read surface 를 **GUID-free 로 정렬** (LLM 표면 wire 일관성 회복)
- list/describe 4종을 `export_model_doc` 의 scope 인자 (`path`, `depth`) 로 흡수
- 도구 풀세트 **10종 → 6종** (Phase 5 의 25→10 후속, 추가 ~40% 압축)
- Phase 4 변형 B 진행 상태 확인 후 진입 (§1.1)

### 1.1 Phase 4 변형 B 와의 충돌 검증 (선행 prerequisite)

사용자 글로벌 메모리 (`yaml-protocol-phase4-ui-deferred`, 2026-05-12) — "Phase 4 부분 재개 — `apply_model_doc` 발행 doc yaml view (inline/dialog) 만 도입, preview/apply 분기는 여전히 보류". `ModelTools.cs:181` 의 `ctx.AppendModelDocYaml(...)` 흔적이 Phase 4 변형 B 부분 적용 중임을 시사.

본 Phase 6 가 같은 파일군 (prompt 5종 + `ModelTools.cs` + `LlmTurnContext.cs`) 을 건드림 → 진입 전 다음 중 1+2 충족 필요:

1. **Phase 4 변형 B commit 상태 확인** — `ChatViewModel` / `ModelDocPreviewDialog` 측 변경이 commit 되었는지 확인.
2. **파일 영역 비겹침 검증** — 아래 명령으로 overlap 0건 확인:

```powershell
# Phase 4 변형 B 영역
$p4 = @("ViewModels/LlmChatViewModel.cs", "Dialogs/ModelDocPreviewDialog*", "LlmAgent/EditorChangeDigest.cs")
# Phase 6 영역
$p6 = @("LlmAgent/Tools/ModelTools.cs", "LlmAgent/PromakerToolNames.cs", "LlmAgent/LlmTurnContext.cs")
# 두 cycle 의 prompt 5종은 동일 — sibling drift 위험. 동일 PR 또는 시간 격리 권장.
```

옵션 3 (동일 PR 통합) 은 회귀 추적 비용 ↑ 로 **권장 X**. 1+2 가 미충족이면 Phase 6 진입 보류.

**현재 검증 상태**: 미수행. SSOT commit #1 진입 직전에 새 세션에서 위 명령 + `git log --oneline -- ViewModels/LlmChatViewModel.cs Dialogs/ModelDocPreviewDialog.xaml.cs` 1회 실행.

---

## 2. 배경 / 맥락

### 2.1 현재 read 6종 상태

| 도구 | GUID 잔재 | 비고 |
|---|---|---|
| `list_projects` | 출력 (full GUID) | project 목록 + system 합계. **`exportToJson` (ModelProtocol.fs:1085-1094) 가 단일 project 만 emit — v0 schema 가 multi-project 미지원** (§3.2 비고) |
| `list_systems` | 출력 (full GUID) | 모든 system, deep tree 미포함 |
| `describe_system` | **입력+출력** (`systemId: string GUID`) | 특정 system 트리 (deep flag) |
| `describe_subtree` | **입력+출력** (`rootId: string GUID`) | 임의 root + depth (0~5). **description 명시: "Project/System/Flow/Work GUID" — 4 EntityKind cover** (closure #2 의 resolver 설계 시 Project 포함 근거). 50 entity 초과 시 truncated |
| `find_by_name` | 출력 (GUID) | substring 검색, kind 필터. `findByName` (`ToolOperations.fs:739`) 가 `(EntityKind, Guid, string)` 만 반환 — **부모 path 미보유, parent chain resolver 필요** |
| `validate_model` | scope 가 GUID OR `'global'` | 진단 (Orphan/Dangling/Empty) — entity graph 와 직교 |

### 2.2 SSOT invariant 와의 충돌

`Apps/Promaker/Docs/done-yaml-protocol-implementation.md`:
- **§6 주의 사항 #5**: "GUID 는 LLM 에 절대 노출 금지" — `apply_model_doc` 의 `refs` 필드조차 옵션.
- **§3.4 결정 표** (yaml-protocol-v0.md §1.7 normative): entity 이름 `.` 금지 → name dotted-path 가 SSOT 주소 체계.
- **§7.4 (Phase 5)**: mutation 15종 일소 후 wire 에서 GUID 완전 소멸. 잔존 10종.

`Apps/Promaker/Docs/yaml-protocol-v0.md`:
- **line 103, 230**: "entity 이름에 `.` 금지" — sanitizeName 에 `.` 거부.
- **line 158**: `protocol:` 키 [MUST] — 최상단 첫 키. v0 top-level 키 enumeration = `protocol` / `project` / `systems` / `patch`. **신규 envelope flag (closure #1) 도입 시 본 enum 갱신 필요**.
- **line 437**: 기존 도구 시그니처 `mcp__promaker__export_model_doc(scope: "project" | "system:<name>" = "project", format: "json" | "yaml" = "yaml")` — SSOT 가 `scope` 키워드를 enum 형태로 정의. **현 `ModelTools.cs:211-226` 본문에는 scope 인자 부재 (format 만) — SSOT ↔ 구현 이미 drift**.
- **line 469-471**: LLM 노출 도구 카운트 명시 + `yaml_to_json` 잠정 비노출.

mutation 경로는 path-based 통일됐는데 read 4종만 GUID 잔재 → **시스템 일관성 균열**. + SSOT 의 기존 `scope: "project" | "system:<name>"` 어휘 자체도 본 작업의 `path/depth` 와 시멘틱 충돌.

### 2.3 사용자 결론

> "현재 구도면 yaml 로 export 해 주면 다 cover 되지 않을까?"
> "Guid 는 불필요할 듯"

→ done 문서 §6#5 invariant 와 사용자 의도 일치. read surface 청산이 자연 다음 단계.

---

## 3. 결정된 설계 방향

### 3.1 `export_model_doc` scope 인자 확장

**중요 (closure #1 결정 - SSOT 영향)**: yaml-protocol-v0.md:437 의 기존 `scope: "project" | "system:<name>"` enum 인자 **폐기**. 대신 `path`/`depth` 두 신규 인자 도입.

```
export_model_doc(
  path?:   string  — name dotted-path  (예: ".Proj1.SysA", ".Proj1.SysA.Flow1")
                    leading `.` = root 절대 경로 (권장 표기)
                    segment 구분자 = `.` (정규형) — `/` 도 dual-accept (normalizePath 가 `/` → `.` replace)
                    생략 = 전체 (canonical)
  depth?:  integer >= 0  — 0 = root entity 자체 (자식 빈 list)
                           1 = 직접 자식까지
                           N = N-level 깊이
                           생략 = 전체 (canonical, 무한 의미)
  format?: yaml | json  (현행 유지)
)
```

**path notation 결정 (v4 closure #3 부속 — α 채택)**:
- 정규형 = dot (`normalizePath` 가 `/` → `.` replace 유지, 변경 0).
- wire 입력 dual-accept: `.Proj1.SysA` 와 `/Proj1/SysA` 모두 OK.
- **leading `.` = root 절대 경로** 권장 어휘. leading 없는 path 도 동일 의미로 해석 (export 는 store root 부터 dump 이므로 절대 의미 자연).
- **leading `.` strip 사양**: `normalizePath` 진입 직후 `TrimStart('.')` 적용 → 이후 `pathSegments` 의 split 결과 = pure segment list. 즉 leading `.` 은 어휘 강조 prefix 일 뿐 segment 카운트에 영향 없음 (§4.6 깊이 룰 정합).
- 이름의 `.` 금지 invariant 유지 → leading `.` 와 이름 충돌 0건 (entity 이름이 `.` 로 시작 불가).
- SSOT yaml-protocol-v0.md:103,230 의 "entity 이름에 `.` 금지" 룰 변경 없음.

**케이스 분리 (두 다른 시나리오)**:
- (i) **`path` 키 부재** → 전체 export (canonical). depth 키도 부재면 무한 depth.
- (ii) **`path` 키 있음 + leading `.` 유무** → 의미 동일 (leading 은 어휘 강조). `path=".Proj1.SysA"` ≡ `path="Proj1.SysA"`.

규칙 (closure #4 결정 적용):
- **`depth` 는 wire 에서 정수만 허용**. 음수 / 비정수 / overflow 사전 거부 (`VALIDATION_ERROR: depth must be integer >= 0`). 무한 의미는 키 생략으로만 표현.
- **`path` 미존재 시**: `VALIDATION_ERROR: path "<value>" 가 store 에 존재하지 않습니다 (fail-fast). 근사 후보: ...` — 기존 dispatcher 의 nearest-candidate 패턴 답습.
- **`path` + `depth=0`**: 정확히 그 entity 자체만 (자식 빈 list).
- **`path` 미지정 + `depth=0`**: envelope 만 (projects/systems 빈 list).
- **`query` 인자 미도입** — find_by_name 의 search semantic (절차적, 자연어 이름 → 위치 목록) 과 export 의 선언적 dump semantic 을 분리 유지. §4.4 결정.

### 3.2 흡수 매핑

좌측 = **현 도구 시그니처 (폐기 대상)** — `describe_system` / `describe_subtree` 의 GUID 입력은 LLM 이 GUID 관리해야 함을 의미 (done §6 #5 "GUID 는 LLM 에 절대 노출 금지" invariant 와 모순). 우측 = **Phase 6 후 호출** — path 기반, GUID 0건, 1-RT.

| 현 도구 (Phase 5 직후, 폐기 대상) | Phase 6 후 호출 |
|---|---|
| `list_projects()` | `export_model_doc(depth=0)` |
| `list_systems()` | `export_model_doc(depth=1)` |
| `describe_system(systemId: GUID, deep=false)` | `export_model_doc(path=".Proj.Sys", depth=1)` |
| `describe_system(systemId: GUID, deep=true)` | `export_model_doc(path=".Proj.Sys")` |
| `describe_subtree(rootId: GUID, depth=N)` | `export_model_doc(path=".Proj.Sys.Flow...", depth=N)` |

**Round-trip 영향**: 현 패턴은 `list_systems` (GUID 획득) → `describe_system(GUID)` 2-RT. Phase 6 후 path 만으로 1-RT. done-promaker-llm-roundtrip-optimization §6.2 cold-start 룰 자연 정합 (별도 갱신은 §5.2 표 참조).

비고: `list_projects` 흡수는 의미 정렬되나 `exportToJson:1085-1094` 의 단일 project emit 한계는 별도 후속 (§7.2).

### 3.3 풀세트 SSOT — 6종 (★ SSOT)

본 절이 풀세트 카운트 SSOT. 다른 절은 본 절 참조.

**doc-level 4종**:
- `apply_model_doc`
- `validate_model_doc`
- `export_model_doc` (확장 — closure #1/#4)
- `json_to_yaml`

**read 2종** (변경 사항만):
- `find_by_name` — GUID 출력 → path 출력 (§4.5 unique 정책 closure #3)
- `validate_model` — GUID scope 폐기, `'global'` literal 폐기 (생략 = global), path scope 허용

**총 6종**. Phase 5 직후 10종 → -40%.

> `yaml_to_json` LLM 비노출 결정 = done §3.4 표 + yaml-protocol-v0:471 잠정 — 본 Phase 6 에서 확정 (옵션 분기 일소). 본 결정에 따라 6 → 7 변동 가능성 0.

---

## 4. 검토 항목 + closure 결정

### 4.1 partial export 의미 + envelope flag (closure #1, #4 — v4 결정)

검증: `applyPatch` (ModelProtocol.fs:803) 의 patch.add 가 `in:` + 자식 키 추가를 PoC 미지원 — partial export 결과를 patch 로 재해석할 코드 부재.

**(정정)** v2/v3 메모에서 "JSON envelope" 으로 표기했으나 부정확. 본질은 **export 결과 최상단 mapping 의 top-level key (format 무관 — yaml/json 양쪽 적용)**. yaml format 인 경우도 top-level mapping 에 `view: partial` 한 줄 key 로 emit.

**closure #1 결정 (v4)**:
- **envelope flag 이름**: ✅ **`view: full | partial`** 채택. format 무관 top-level key. 신규 v0 §2.0 top-level 키 enum 에 추가 (기존 `protocol` / `project` / `systems` / `patch` + `view`).
- **flag 부재 시 policy**: ✅ **(ii) 명시 강제** 채택. 부재 시 `VALIDATION_ERROR: view 키 누락 — v0 이전 export 결과는 'view: full' 추가 후 재시도` 친절 에러.
- **v0 §2.0 top-level 키 enum 갱신**: SSOT commit #1 본문 포함.

**closure #4 결정 (v4)**:
- ✅ **(b) 실제 truncation 발생 여부** 채택. `exportToJson` walk 도중 entity 누락 1건 이상이면 `view: partial`, 0건이면 `view: full`. 구현 = walk 진입 시 `let truncated = ref false`, 절단 시점 (depth limit / budget overflow / path 외부 skip) 에 `truncated := true` set. `depth=999` 입력이라도 실제 절단 0건이면 `view: full` (의미 정확).

**구현**: dispatcher 의 `apply_model_doc` 에 `view: partial` 입력 사전 거부 분기. `validate_model_doc(view: partial)` 도 동일 거부 (Major-C 흡수 — alias fallback / cross-system arrow 의 misleading 회귀 차단).

**회귀 fact 5건 명세** (신규 모듈 `ExportModelDocPathDepthTests.fs`):
- (a) `view: partial` emit — path 또는 depth 로 truncation 발생 시.
- (b) `view: full` emit — path 미지정 + depth 미지정 OR 큰 depth 인데 truncation 0건.
- (c) `view: partial` doc → `apply_model_doc` 입력 → ERROR + 메시지 lock (substring).
- (d) `path` 없이 `depth=999` (큰 정수, full 결과) → `view: full` emit.
- (e) `depth=-1` / `depth=1.5` / wire schema 위반 → 사전 거부 ERROR.

### 4.2 `depth=0` 의미

- **결정**: `depth=0` = root entity, 자식 빈 list. count attribute (예: `{systems: 3}`) **emit 하지 않음**.
- **근거**: v0 schema (`yaml-protocol-v0.md §2.1`) 에 없는 count 키 emit 시 unknown key 에러 또는 silent ignore — 어느 쪽이든 round-trip invariant 위반. partial export 가 view-only 라도 schema 정합 유지.
- count UX 가 필요하면 별도 진단 도구 (예: `validate_model` 진단 카테고리에 `Stats` 추가) — Phase 6 범위 외.

### 4.3 빈 결과 의미 구분

`list_projects` 의 두 빈 케이스:
- "no projects": `export_model_doc(depth=0)` 의 envelope 에 `project` 키 부재 (현 exportToJson:1093 `| [] -> ()`).
- "system 0": `project` 키 있고 `systems: []`.

별도 metadata 없이 envelope 구조로 구분 가능. **추가 작업 없음**.

### 4.4 find_by_name — 별개 유지

- **결정**: 옵션 A (별개 유지) 채택. GUID 출력만 path 로 변경.
- **근거**: search semantic (절차적) ≠ export (선언적 dump). `query` 를 export 에 추가하면 의미 혼란.
- **옵션 B' (대안 — 본 Phase 에서 미채택)**: `export_model_doc(path="prefix*")` 형태 partial match enumerate. patch DSL 와 일관성 ↑ 이나 wildcard 어휘 신설 부담. 후속 cycle 후보 (§7.2).

### 4.5 find_by_name 출력 spec (closure #3 — v4 결정: unique 4-way 폐기)

**v4 사용자 통찰** (closure #3 본질 재정의):
> "find_by_name 구현이지? 실제 chat LLM 이 하는 역할을 생각해 보고 결정하면 될 듯. 'Proj.Sys.Flow1' 같은 path 가 있다면 대부분 성질을 유추 가능하지 않나? proj.system.flow.work.call 순서이니..
> 오히려 사용자가 자연어에서 이름을 언급하면, 해당 item 이 어디에 있고, EntityKind 가 무엇인지 목록들을 반환해 주는게 맞지 않을까? e.g [ (ApiDef, /proj/cylinder1/.../apidef1), (Work, /proj/...)]"

→ find_by_name 의 본질 = **자연어 이름 → 위치 목록 회신**. unique path 강제 불필요. v3 의 (a) ordinal / (b) GUID tail / (c) sibling invariant / (d) sanitize 4 후보 모두 **폐기**.

**출력 path 정규형 (§3.1 부속 결정과 정합)**: `leading "." + dot segment`. 아래 출력 예시는 정규형 (`.Proj1.Run`). 사용자 자연어 인용 (`/proj/.../apidef1`) 은 dual-accept 의 wire 입력 표기 예시일 뿐 — 정규화 후 dot 형으로 emit.

**find_by_name 출력 spec (v4 확정)**:
```
find_by_name(name: "Run") →
  [
    { kind: "System", path: ".Proj1.Run" },
    { kind: "Flow",   path: ".Proj1.SysA.Run" },
    { kind: "Work",   path: ".Proj1.SysA.F1.Run" }
  ]
```

규칙:
- 동명 sibling 도 그대로 N건 반환 (filter 없음).
- 호출자 (LLM) 가 `kind` + `path` 조합으로 다음 단계 결정 (`export_model_doc(path)` / `apply_model_doc` patch 작성 등).
- v3 의 cross-kind 동명 처리 (α kind 인자 강제 / β kind 필드 강제) → **(β) 자연 채택**. `kind` 필드가 출력에 항상 포함되어 호출자가 명시 구분 가능.
- **`kind` 필드 존재 의의 (path-from-kind 가 아닌 disambiguation 목적)**: path 깊이 ↔ EntityKind 매핑은 대부분 unique 하지만 **ApiDef vs Flow** 처럼 같은 깊이 (System 직접 자식 = 3-segment) 에 두 kind 가 공존 → path 만으로 유추 불가능. `kind` 필드로 명시 구분. §4.6 의 `tryFindEntity` 내부 lookup 도 동일 ambiguity 해소 필요.
- name 인자 substring/정확매칭 분기는 현 동작 유지 (별도 변경 0).

**구현 영향**:
- `ToolOperations.fs:739` `findByName : (EntityKind * Guid * string) seq` → **`findByName : (EntityKind * string) seq`** (Guid 제거, path 추가). parent chain resolver 신설 — `pathOf : EntityKind → Guid → string` (root 까지 추적, leading `.` prefix).
- `ModelTools.cs` `FindByName` Description 갱신 — 출력이 `kind` + `path` 튜플 목록임을 명시.
- closure #3 의 sibling unique 정책 4-way 의사결정 항목 자체가 사라지므로 SSOT commit #1 본문 영향 -1 항목.

### 4.6 path → EntityKind resolver (closure #2 — v4 결정)

검증: 현 ModelProtocol.fs 는 `findSystemByName` (line 786) + `findFlowByPath` (line 794, 2-segment hard-code) 만 — 일반 path resolver 부재.

**closure #2 결정 (v4)**: ✅ **(B) thin lookup** 채택. **kind 인자 없음** (사용자 통찰: path 자체에서 `proj/system/flow/work/call` 깊이로 EntityKind 유추 가능).

```fsharp
let tryFindEntity : DsStore -> string -> (EntityKind * Guid) option
// 입력 path 예: ".Proj1.SysA.Flow1.W1"  (leading `.` 권장, 없어도 동일 의미)
// 출력: Some (Work, <Guid>)
// path 깊이로 kind 자동 결정:
//   1 segment → Project
//   2 segment → System
//   3 segment → Flow
//   4 segment → Work
//   5 segment → Call
//   ApiDef 는 system 자식 (Flow 와 형제) → 2-segment 의 어떤 분기인지는 store lookup 으로 결정
```

**v3 의 (α) kind 인자 강제 폐기** — closure #3 폐기와 동시. resolver site 에서 path 만으로 unique 결정.

**사용처 일관 동작**:
- `export.path` (Phase 6 신규, 모든 kind cover) — path 그대로 lookup.
- `validate_model.scope` (Phase 6 신규) — path 그대로 lookup.
- `find_by_name` 출력의 path — `pathOf` 역방향 helper 사용 (entity → path).

**기존 helper 처리**: `findSystemByName` (line 786) / `findFlowByPath` (line 794) 는 **호출지점 그대로 유지 (병존)**. 신규 `tryFindEntity` / `pathOf` 는 `export.path` / `validate_model.scope` / `find_by_name` 진입점에만. 사용자 철학 ("기존 코드 베이스의 수정 최소화") 정합.

**path 깊이 ambiguity 처리**:
- 2-segment path = Project + 직접 자식 (System / 만약 Project 자식이 다른 kind 있다면 모호). 현 PoC 는 System 만이 Project 직접 자식이므로 명확.
- ApiDef vs Flow: 둘 다 System 의 직접 자식이므로 3-segment 일 때 store 의 두 컬렉션 모두 lookup 필요. `tryFindEntity` 내부에서 ApiDef 먼저 확인 → Flow → 미발견 시 None.
- 이 부분 명세는 SSOT commit #1 의 yaml-protocol-v0.md §2.5 path resolver 절에 명문화.

### 4.7 `describe_subtree` 의 "50 entity truncation" 정책

ModelTools.cs:272 의 50 entity 상한이 `describe_subtree` 본문에서 enforce. partial export 흡수 시 동일 상한 유지:

- **결정**: 50 entity 상한을 `export_model_doc` 의 partial 경로 (`path` OR `depth` 명시) 에 적용. 전체 export (`path` 미지정 + `depth` 미지정) 는 무제한 (round-trip 정합 필수).
- truncation 발생 시 envelope 의 `view: partial` flag emit (closure #4 의 (b) 채택 시 자연 정합).
- 적용 layer = `exportToJson` 의 entity walk 안 (D-m5).

### 4.8 validate_model 의 scope 어휘 — global literal 처리 (Major-B)

현 `validate_model(scope?: string)` 가 `'global'` literal 과 GUID 양쪽 허용.

- **입력**: `'global'` literal **폐기**. GUID 분기 **제거**. `scope?: string` (생략 = 전체. path 명시 시 해당 sub-tree).
- **출력 footer 어휘**: 유지 — "(scope=global)" 같은 user-facing 한국어 메시지는 가독성 유지 차원에서 그대로 (사용자 표현 자유, wire 입력에만 영향).
- **cache key sentinel** — `LlmTurnContext.cs:37,42,96` 의 cache key 가 GUID 또는 'global' 사용 중 → 신규 sentinel = `""` (empty path) 명문화. null/sentinel literal collision 회피.

### 4.9 describe_* deprecation 경로

**결정**: 즉시 제거. Phase 5 의 mutation 15종 단일 commit 일소 패턴 답습.

단 §5.0 의 2-단계 commit 분리 (SSOT-only → 코드 일괄) 는 채택.

---

## 5. 작업 단계

### 5.0 2-단계 commit 전략

Phase 5 와 달리 Phase 6 는 **SSOT 결정 자체가 신설** (envelope flag / scope 어휘 폐기 / depth schema / unique 정책 / resolver) 을 포함. 2-단계 commit:

1. **commit #1 — SSOT 단독** (`yaml-protocol-v0.md` + `done-yaml-protocol-implementation.md` §3.0 후속 cycle 후보 표 갱신). reviewer 가 설계만 검토.
2. **commit #2 — 코드 / prompt / test 일괄** (Phase 5 패턴 답습). closure list 5건이 commit #1 에 반영되어 있어야 함.

### 5.1 코드 변경 (commit #2)

| 파일 | 변경 (v4 결정 반영) |
|---|---|
| `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs` | `ExportModelDoc` 에 `path?: string`, `depth?: int` 인자 추가 + envelope `view: full \| partial` flag emit (closure #1 ✅). `ListProjects` / `ListSystems` / `DescribeSystem` / `DescribeSubtree` 4 메서드 본문 삭제. `FindByName` 출력 = `[ {kind, path} ]` 목록 (closure #3 v4 ✅ — Guid 제거, kind+path 튜플). `ValidateModel` 의 GUID 분기 + 'global' literal 제거 (§4.8). |
| `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs` | `All` 배열 10 → 6 (§3.3). |
| `Apps/Promaker/Promaker/LlmAgent/LlmTurnContext.cs:42,96` | `validate_model` cache key sentinel = `""` (§4.8). line 37 은 doc-comment 라 영향 없음 (인접 doc 도 sentinel 어휘 동기화 권장). |
| `Apps/Promaker/Promaker/LlmAgent/EditorChangeDigest.cs:18,142,165` | 런타임 합성 message 의 read 도구 어휘 갱신 — list_projects/list_systems 권고 제거, validate_model 권고 유지. |
| `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` | `listProjects` / `listSystems` / `describeSystem` / `describeSubtree` helper 일소. `findByName : seq<EntityKind * Guid * string>` → **`seq<EntityKind * string>`** (Guid 제거, path 추가 — `pathOf` helper 사용). `validateModelByGuid` → path 기반 lookup helper. |
| `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs` | `exportToJson` 에 `path` resolve + `depth` 절단 + `truncated: bool ref` 한 줄 + `view` flag emit (closure #4 ✅). **신규 thin lookup `tryFindEntity : DsStore → string → (EntityKind * Guid) option`** (closure #2 ✅, kind 인자 없음). **신규 역방향 helper `pathOf : EntityKind → Guid → string`** (root 까지 parent chain 추적, leading `.` prefix). dispatcher 의 `apply_model_doc` / `validate_model_doc` 에 `view: partial` 사전 거부 분기. `findSystemByName` / `findFlowByPath` 호출지점 그대로 유지 (§4.6 병존). |
| `Solutions/Core/Ds2.LlmAgent/CLAUDE.md:115` | sibling fence — read 6종 → 2종 카운트 갱신 (↔ §3.3 SSOT). |

### 5.1.1 테스트 fact 별 cover 표 (Major-E 흡수)

| 파일 | 기존 fact | 처리 |
|---|---|---|
| `Solutions/Tests/Ds2.LlmAgent.Tests/PromakerToolNamesDriftTests.fs` | sanity count 10 (`Assert.Equal(10, ...)`) | **closure #5 v4 ✅**: sanity → `Assert.Equal<Set<string>>(expectedSet, listed)` set equality + `opLayerStaleTokens` 에 `list_projects` / `list_systems` / `describe_system` / `describe_subtree` 4종 추가. 카운트만 6 통과 + description 잔재 silent 회귀 차단. |
| `DescribeSubtreeTests.fs` | 10 fact (depth cap, budget=51 truncated 등) | **모듈 통째 삭제 또는 일부 회수**: `export_model_doc(path, depth)` 회귀 모듈 (`ExportModelDocPathDepthTests.fs`) 신설로 (1) depth 0/N/무한 (2) budget 50 truncation = view: partial (3) path 미존재 = fail-fast (4) path → kind 모호 = ERROR (5) round-trip full path/depth 회귀로 흡수. describeSystem 자체 폐기 → token 회귀 baseline 비교 대상 사라짐 — e2e turn cost 측정 (§5.4) 으로 대체. |
| `ValidateModelTests.fs` | 19 fact (scope=global literal lock 등) | **재작성**: 'global' literal fact 4개 제거. path scope fact 신규 + scope 미지정 = 전체 fact 신규. |
| `HelperCascadeTests.fs:220` | `Assert.Contains("find_by_name", ex.Message)` | 그대로 유효 (find_by_name 별개 유지). 단 ex.Message 본문이 path 출력 형식으로 바뀌었으면 동기화. |
| `StoreSnapshotTests.fs:13` | 주석 cross-ref | 영향 없으면 무변경. read 도구 어휘 잔재 검사. |
| `StreamJsonParserTests.fs:54-58,64,68` | `"add_system"` literal | **parser invariant fixture — sweep false positive 주의**. 도구 이름 자체가 아닌 wire JSON parsing 동작 검증의 픽스처. 변경 불요. 단 §5.3 grep 시 false positive 발생 → exclude pattern 권장. |
| `PromptCanaryTests.fs` (있다면) | 도구 어휘 canary | stale token 0건 회귀 fact 추가 (수동 grep → build-time 격상). |

**신규 fact 명명 컨벤션** (T-m1): 한글 backtick + prefix `6f-N` (Phase 6 → 6f, N=일련번호). 예: `` `6f-1 export_model_doc(path) round-trip` ``.

**테스트 카운트 사전 grep** (§5.4 baseline 산정 prerequisite):
```powershell
# 제거 fact 수 = DescribeSubtreeTests.fs 의 [<Fact>] 수 + ValidateModelTests.fs 의 'global' literal fact 수
# 신규 fact 수 = ExportModelDocPathDepthTests.fs 의 [<Fact>] 수 + ValidateModelTests.fs 의 path scope fact 수
```

### 5.2 SSOT / 외부 노출 문서 갱신

| 파일 | 변경 | commit |
|---|---|---|
| `Apps/Promaker/Docs/yaml-protocol-v0.md` | **§2.0 top-level 키 enum 갱신** (envelope flag 추가). **§2.8 신설** (partial export view-only + view flag spec). **§4 line 437** 도구 시그니처 갱신. **line 469-471** LLM 노출 카운트 + `yaml_to_json` 결정. **§1.7 결정 표** path resolver / unique 정책 추가. | #1 |
| `Apps/Promaker/Docs/done-yaml-protocol-implementation.md` | Phase 6 결과 절 (§7.5/§8). 풀세트 10 → 6. §3.0 후속 cycle 후보 갱신. | #2 (또는 #1 의 §3.0 영역만) |
| `Apps/Promaker/Paper/paper.md:82` (외부 노출) | "읽기" 도구 6종 → 2종 + `export_model_doc` path/depth 설명. | #2 |
| `Apps/Promaker/Docs/done-promaker-llm-roundtrip-optimization.md:16,281,288,291,293,370,491,524,578,589` | **§6.2 cold-start 룰 직격** — "snapshot 없는 첫 turn 에 `list_projects` 1회 cold-start 허용" 룰을 `export_model_doc(depth=0)` 으로 교체. line 16/370 의 RT 분해 + snapshot 첨부 흐름 갱신. | #2 |
| `Apps/Promaker/Docs/Poc/e2e-cache-hit.fsx:79` | PoC script — read 도구 호출 갱신. | #2 |
| `Solutions/Core/Ds2.LlmAgent/CLAUDE.md:115` | sibling fence (↔ §3.3 SSOT). | #2 |
| `Solutions/Core/Ds2.LlmAgent/doc/todo-*.md`, `done-batch-mcp-call.md` | historical 인지 활성 todo 인지 확인 후 sweep. 현 권고: historical 은 그대로, 활성만 갱신. | #2 |

### 5.3 Prompt 갱신 (5 파일 — commit #2)

| 파일 | 변경 |
|---|---|
| `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md` | "현 도구 풀세트" 표 read 6 → 2. `export_model_doc` scope (path/depth) 사용 예시. `describe_system` / `describe_subtree` / `list_systems` / `list_projects` 어휘 일소. |
| `Apps/Promaker/Promaker/LlmAgent/Prompts/2.modeling.md` | read 도구 어휘 sweep. |
| `Apps/Promaker/Promaker/LlmAgent/Prompts/1.entities.md` | read 도구 reference sweep. |
| `Apps/Promaker/Promaker/LlmAgent/Prompts/4.attachments.md` | 변경 없을 가능성 — 단 grep 후 잔재 0건 확인 (M6 sibling drift 검증). |
| `Apps/Promaker/Promaker/LlmAgent/Prompts/CLAUDE.md` | 풀세트 카운트 line (10 → 6). |
| `Apps/Promaker/Promaker/LlmAgent/Prompts/chat-simulation/CLAUDE.md` | sibling drift 동기화. |
| `Apps/Promaker/Promaker/LlmAgent/Prompts/9.environment.md` | / `--` 명령 토큰 일반화 — read 도구 어휘 영향 가능 (grep 후 결정). |

**Prompt sweep 검증** (build 검증 불가 → Grep 잔재 0건 확인 필수):

```powershell
# 광역 sweep — Major-A 흡수 (24 파일 cover)
$pattern = "describe_system|describe_subtree|list_systems|list_projects"
$paths = @(
  "Apps/Promaker/Promaker/LlmAgent/Prompts/",
  "Apps/Promaker/Promaker/LlmAgent/",         # EditorChangeDigest, LlmTurnContext, ModelTools description
  "Solutions/Core/Ds2.LlmAgent/",             # ToolOperations + CLAUDE.md
  "Solutions/Tests/Ds2.LlmAgent.Tests/",      # parser fixture 는 exclude
  "Apps/Promaker/Docs/",                      # SSOT + done + roundtrip + todo
  "Apps/Promaker/Paper/paper.md",
  "Apps/Promaker/Docs/Poc/"
)
# 잔재 0건 (단 StreamJsonParserTests.fs 의 add_system literal 은 parser fixture — exclude).
# find_by_name / validate_model 은 잔존 유효 (별개 유지).
# Phase 5 일소 도구 (apply_operations / add_*) 함께 sweep — Phase 5 retest 겸용.
```

도구 풀세트 카운트 line cross-check (Phase 3 의 escape hatch 카운트 정합 패턴).

### 5.4 회귀 절차 + Rollback (정량 trigger)

- **baseline**: 319 통과 (`done-yaml-protocol-implementation.md §7.4`). PR 직전 `dotnet test` 재확인 + `baseline-pre-phase6.log` 캡처 (T-m4 — diff 비교 자동화).
- 신규/제거 fact 카운트는 §5.1.1 grep 으로 사전 산정.
- `dotnet test Solutions/Tests/Ds2.LlmAgent.Tests/Ds2.LlmAgent.Tests.fsproj -c Debug --nologo`
- **e2e 시나리오**: `done-promaker-llm-roundtrip-optimization.md` 의 turn-cost 답습 — Promaker GUI Debug + 동일 모델 (3 zone × N cyl + Pusher Punch) 1 turn. baseline = `$0.45 / 0 재시도 / 1 op` (done §3.0 Turn C).

**Rollback trigger (정량 — Major-G 흡수)**:
- (i) **단위 테스트 통과 수 ≠ 기대치 (319 - 제거fact + 신규fact)** → SSOT/코드 commit revert. 회귀 origin 격리.
- (ii) **e2e turn cost > baseline +25% drift** OR **재시도 ≥ 1** → 코드 commit revert + SSOT 유지 (설계 살리고 구현 다음 cycle).
- (iii) **baseline 미달 + 원인 격리 실패** → 3-PR 분할 fallback: (1) SSOT-only / (2) ModelTools + ToolOperations / (3) Prompt + 외부 노출 문서.

### 5.5 자가 검열 (CLAUDE.md 룰)

trigger 충족:
- **① 시그니처 breaking** — `ExportModelDoc` 시그니처 확장 + 4 도구 제거 + `ValidateModel.scope` 의미 변경.
- ② 신규 함수/타입 3개 이상 — `tryFindEntity` (또는 `ResolvedEntity` DU) / partial export envelope emit helper / view flag schema.
- ③ 100 line 이상 변경 — `ModelTools.cs` 4 메서드 일소 + `ToolOperations.fs` helper 일소 + `ModelProtocol.fs` scope 로직.
- ⑤ public API / SSOT 상수 갱신 — `PromakerToolNames.All`, yaml-protocol-v0 §2.0 / §2.8 / §4 / §1.7.

→ Agent (general-purpose 또는 code-review skill) 위임 필수.

**리포트 5요소 (필수)**:
1. 검열 대상 (파일 목록 + 변경 line 수)
2. Reviewer 발견 이슈 (없음 / 건수 + Critical/Major/Minor 분류)
3. 자가 수정 결과 (적용 / 거부 + 사유)
4. 잔여 우려 (없으면 "없음" 명시)
5. 본 메모와의 차이 (메모 보강 권고)

---

## 6. 관련 파일 / 경로

### 6.1 설계 / 참조 (먼저 읽기)
- **`Apps/Promaker/Docs/yaml-protocol-v0.md`** — schema SSOT
- **`Apps/Promaker/Docs/done-yaml-protocol-implementation.md`** — Phase 0~5 history (§3.4 결정, §6 #5 GUID invariant, §7.4 Phase 5 패턴)
- `Apps/Promaker/Promaker/LlmAgent/Prompts/1.entities.md` / `2.modeling.md` / `3.tooling.md` / `4.attachments.md` / `9.environment.md`
- `Apps/Promaker/Paper/paper.md` — 외부 노출 paper (line 82)

### 6.2 코드 (라인 수는 시간 진화 — `wc -l` 재확인)
- `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs`
- `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs`
- `Apps/Promaker/Promaker/LlmAgent/LlmTurnContext.cs`
- `Apps/Promaker/Promaker/LlmAgent/EditorChangeDigest.cs`
- `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs`
- `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs` — `exportToJson` line 1085 / `findSystemByName` line 786 / `findFlowByPath` line 794 / `applyPatch` line 803 / `findByName` line 739 (in ToolOperations)
- `Solutions/Core/Ds2.LlmAgent/CLAUDE.md:115` (sibling fence)
- `Solutions/Tests/Ds2.LlmAgent.Tests/PromakerToolNamesDriftTests.fs`
- `Solutions/Tests/Ds2.LlmAgent.Tests/DescribeSubtreeTests.fs` / `ValidateModelTests.fs`
- `Solutions/Tests/Ds2.LlmAgent.Tests/HelperCascadeTests.fs:220`
- `Solutions/Tests/Ds2.LlmAgent.Tests/StoreSnapshotTests.fs` / `StreamJsonParserTests.fs` (cross-ref / parser fixture)

### 6.3 외부 / 회귀 검증 SSOT
- `Apps/Promaker/Docs/done-promaker-llm-roundtrip-optimization.md` (§6.2 cold-start, line 16/281/288/291/293/370/491/524/578/589)
- `Apps/Promaker/Docs/Poc/e2e-cache-hit.fsx:79`

### 6.4 솔루션
- `Solutions/Ds2.sln`
- `Apps/Promaker/Promaker.sln`

---

## 7. 주의 사항

1. **--plan 정신**: 사용자 명시적 구현 지시 (§0.3 trigger) 전까지 코드 변경 금지.
2. **2-단계 commit**: §5.0 — SSOT-only → 코드/prompt/test 일괄.
3. **prompt 와 코드는 commit #2 안에서 묶음**: sibling drift 방지.
4. **closure list ✅ v4 결정 완료** (구 항목: "미결정 시 commit #1 본문 작성 불가"): 5건 + 부속 1건 모두 사용자 결정 완료 → SSOT commit #1 본문 작성 진입 가능 상태. trigger 발화 ("Phase 6 commit #1 진행") 대기.
5. **EntityKind 자동 판별 ✅ v4 해소** (구 항목: `describe_subtree` GUID + cross-kind 동명 risk): closure #2 `tryFindEntity` 가 path 깊이 (`proj/system/flow/work/call`) 로 EntityKind 자동 결정. ApiDef vs Flow 같은 동일 깊이 ambiguity 는 `tryFindEntity` 내부 lookup 순서 (§4.6) + find_by_name 출력의 `kind` 필드 (§4.5) 로 해소.
6. **`validate_model` scope**: §4.8 — 'global' literal 입력 폐기, 출력 footer 어휘 유지.
7. **Phase 4 변형 B 충돌 검증 (§1.1)**: 진입 전 새 세션에서 1회 실행.
8. **자가 생성 파일 sweep 제외**: `Apps/Promaker/Promaker.kwak.sln`, `Apps/Promaker/Promaker.main.sln`, `Apps/Promaker/Docs/ds2.log*`, `bin/`, `obj/`, `.vs/`, `*.user`, `*.suo`, `TestResults/`, `.idea/`.
9. **parser fixture sweep false positive**: `StreamJsonParserTests.fs:54-58` 의 `add_system` literal 은 wire JSON parsing 동작 검증의 픽스처 — 도구 이름 자체와 분리. Phase 5 일소 도구 sweep 시 exclude.

### 7.1 후속 cycle 후보 (Phase 6 와 독립)

- **list_projects 흡수의 multi-project 한계** — exportToJson:1085-1094 가 단일 project 만 emit. v0 schema multi-project 미지원과 충돌. 별도 cycle.
- **find_by_name path partial match (옵션 B')** — `export_model_doc(path="prefix*")` wildcard. wildcard 어휘 신설 부담으로 본 Phase 미채택.
- ~~**`find_by_name` long-term clean** — 잠정 (a) ordinal suffix → (c) sibling-unique invariant 강제 + 마이그레이션.~~ **[v4 archive]** closure #3 폐기 + find_by_name 출력 `[(kind, path)]` 목록 격상으로 unique 정책 의사결정 항목 자체가 소멸. sibling-unique invariant 강제는 별도 동기 (예: dotted-path apply 시점 entity 재조회 안정성) 발생 시 재검토.
- **`ModelProtocol.fs` 1201 line SRP 모듈 split** (done §3.0 의 5 module split 권고) — Phase 6 진입 전 권장. 우선순위 상승.
- `patch.arrows.remove` 의 Arrow EntityKind 확장 (done §3.0).
- doc-level dispatcher name sanitize (done §3.0).
- doc-level cascade quota 차감 재도입 (done §3.0).

### 7.2 본 메모 review 처리 이력

본 메모의 변경 추적. CLAUDE.md 룰 (reviewer 항목 번호만 나열 금지) 정합 위해 풀어서 기술.

#### v1 (`--transfer` 1차)
초기 transfer 메모 작성. read 6종 → export_model_doc 흡수 설계 + 검토 항목 5건 + 기본 작업 단계.

#### v2 (`--review` 1차 5인 reviewer 검증 반영, 2026-05-13)
사실 검증 통과한 Critical 5 / Major 14 / Minor 13 모두 수용 (기각 0건, 반론 0건). 분류 기준:
- **Critical** = 착수 차단 (사실 오류 또는 결정 미루기로 commit 진입 불가)
- **Major** = 진입 전 보강 (회귀 사각 또는 spec 모호)
- **Minor** = 정비 (가독성 / 사소한 정정)

주요 흡수 사항:
- 누락 영향 파일 (DescribeSubtreeTests / ValidateModelTests / LlmTurnContext / EditorChangeDigest).
- partial export view-only 명문화 + scope flag emit + dispatcher 사전 거부.
- SSOT scope 어휘 충돌 명시 + 기존 scope 폐기 선언.
- Phase 4 변형 B in-flight prerequisite 명시 (§1.1).
- depth=0 root entity 결정 + count metadata 미도입.
- 라인 수 / 절번호 인용 정정.
- 풀세트 6종 확정 (5/6 분기 일소).
- path → EntityKind resolver `tryResolveEntityByPath` 신설 (v2 시점).
- 외부 SSOT 4건 (paper / done-roundtrip / Poc / Solutions CLAUDE.md) 표 추가.
- 2-단계 commit 전략 (SSOT-only → 코드 일괄).
- 'global' 키워드 폐기 결정.
- depth 정수 schema 명문화.
- Rollback / e2e / trigger 발화 보강.

#### v3 (`--review` 메타 review 5인 검증 반영, 2026-05-13)
v2 재검증에서 사실 검증 통과한 Critical 5 / Major 8 / Minor + outlier 다수 수용. 주요 흡수:

- **closure list 5건 격상 (§0.2)** — SSOT commit #1 진입 전 결정 필요 사항을 1급 표로 정리:
  1. envelope flag 이름 — 기존 도구 인자 `scope` 와 충돌 회피 (yaml-protocol-v0.md:437) → `view: full | partial` 또는 `_meta.scope` 후보. + v0 §2.0 top-level 키 enum 갱신 + legacy 처리 정책.
  2. path → entity 결정 메커니즘 — 강타입 DU vs thin lookup `tryFindEntity : DsStore → string → (EntityKind * Guid) option` (사용자 철학 90/10 — `findByName` 답습). DU 채택 시 `ResolvedProject` 추가 필수 (ModelTools.cs:275 의 describe_subtree 4 EntityKind 와 정합), Arrow 미포함 사유 footnote.
  3. find_by_name path unique 정책 — (a) ordinal suffix / (b) GUID prefix tail / (c) sibling-unique invariant 강제 / (d) sanitizeName `.` 거부 마이그레이션 + cross-kind 동명 (Active System "Run" + Flow "Run" 동일 path 위험) 처리 — resolver 시그니처에 kind 인자 강제 OR 출력에 `kind:` 필드.
  4. partial 판정 기준 — 명시 여부 vs 실제 truncation 발생 여부. 후자 권장 (의미 정확성). + 회귀 fact 5건 (view: partial emit / view: full emit / partial doc apply ERROR / depth=999 full / depth=-1 사전 거부) 명세.
  5. `PromakerToolNamesDriftTests.fs` 회귀 가드 격상 — sanity count → set equality. `opLayerStaleTokens` 에 read-4종 추가.
- **새 세션 진입 가이드 (§0)** 신설 — 읽기 순서 + closure list + trigger 발화 + 용어 사전.
- **Prompt sweep 범위 광역 확장 (§5.3)** — 3 경로 → 24 파일 cover. EditorChangeDigest.cs / ToolOperations.fs / 4 test 파일 / paper.md / ModelTools.cs Description / 4.attachments.md / 9.environment.md / PromptCanaryTests.fs / done-roundtrip:16,370.
- **테스트 fact 별 cover 표 (§5.1.1)** — Major-E 흡수. `DescribeSubtreeTests` 10 fact 의 신규 모듈 흡수 / 폐기 처리. 신규 fact 명명 컨벤션 (`` `6f-N ...` ``). 신규/제거 fact 카운트 사전 grep.
- **Rollback trigger 정량 (§5.4)** — (i) 통과 수 ≠ 기대 → revert / (ii) cost > +25% OR 재시도 ≥ 1 → 코드 revert / (iii) 원인 격리 실패 → 3-PR 분할.
- **resolver 흡수 → 병존 (§4.6)** — Major-H. 기존 `findSystemByName` / `findFlowByPath` 호출지점 그대로, 신규 lookup 은 `export.path` / `validate_model.scope` 에만. 사용자 철학 (기존 코드 수정 최소화) 정합.
- **cache key sentinel `""` 명문화 (§4.8)** — Major-B. validate_model 입력 폐기 / 출력 footer 어휘 유지.
- **alias fallback partial export hard error (§4.1)** — Major-C. validate_model_doc(view: partial) 도 사전 거부.
- **path 구분자 `.` 통일 (§3.1, §3.2)** — G-m1. yaml-protocol-v0.md:103,230 의 entity 이름 `.` 금지 invariant 와 정렬. v2 의 `/` 표기 정정.
- **path 미존재 fail-fast (§3.1)** — C-m4. ERROR + 근사 후보 제안.
- **§3.3 풀세트 SSOT 격상 (Major-D)** — §3.2/§5.1 은 §3.3 참조. 표 분리 (흡수 4 행 / 유지 2 행 — 변경 사항).
- **truncation layer 명시 (§4.7)** — D-m5. `exportToJson` entity walk 안 적용.
- **자가 생성 파일 목록 보강 (§7 #8)** — `*.user`, `*.suo`, `TestResults/`, `.idea/` 추가.
- **parser fixture sweep 예외 (§7 #9, §5.1.1)** — outlier T-M7. StreamJsonParserTests.fs `add_system` literal exclude.
- **9.environment.md prompt 추가 (§5.3)** — recent commit `b4bb08b` 의 신설 파일 cover.
- **outlier 흡수**: D-M5 (옵션 B' 후속 cycle 후보), T-M7 (parser fixture exclude), C-M5 (sanitizeName `.` 거부 + 마이그레이션 = closure #3 옵션 (d)).

**검증 통과 사항 처리율**: Critical 5/5 + Major 8/8 + Minor 다수 흡수. 합의 분포 (메타 review): Critical 5×high-consensus, Major 1×5/5 + 1×3/5 + 6×{2/5, 1/5}, Minor 대부분 1/5 (cosmetic).

**반론 / 기각 0건** — 모든 권고가 사실 검증 통과 + 본 작업 spec 정합.

#### v4 (closure list 사용자 결정 round, 2026-05-13)

SSOT commit #1 진입 직전 5건 closure 모두 결정 완료. + path notation 부속 결정 + JSON envelope 표현 정정 + 표 라벨 정정.

**closure 결정 사항** (§0.2 표 참조 — 본 절은 결정 근거 / 변경 영향 보강):

1. **closure #1 `view: full | partial` 채택**:
   - format 무관 top-level mapping key (yaml/json 양쪽 적용). v2/v3 의 "JSON envelope" 표현은 부정확 — §4.1 본문 정정.
   - flag 부재 시 ERROR (명시 강제 ii) + 친절 에러 메시지.
   - v0 §2.0 top-level 키 enum 5개로 확장 (`protocol` / `project` / `systems` / `patch` / `view`).

2. **closure #2 thin lookup `tryFindEntity` 채택 (kind 인자 없음)**:
   - 사용자 통찰 = path 자체에서 `proj/system/flow/work/call` 깊이로 EntityKind 유추 가능 → kind 인자 강제 불필요.
   - 시그니처: `DsStore → string → (EntityKind * Guid) option`.
   - v3 의 `ResolvedEntity` DU + Project case + Arrow 미포함 footnote 모두 폐기.
   - 신규 역방향 helper `pathOf : EntityKind → Guid → string` (find_by_name 출력의 path 생성용).

3. **closure #3 (sibling unique 4-way) 폐기 + find_by_name 출력 spec 격상**:
   - 사용자 통찰 = find_by_name 의 본질 = 자연어 이름 → `[(EntityKind, path)]` 목록 회신. unique path 강제 불필요.
   - v3 의 (a) ordinal `[2]` / (b) GUID tail `#a1b2` / (c) sibling invariant / (d) sanitize 4 후보 모두 의사결정 항목에서 제거.
   - cross-kind 동명 → (β) kind 필드 자연 채택 (출력에 항상 포함).
   - `ToolOperations.fs:739` `findByName : seq<EntityKind * Guid * string>` → **`seq<EntityKind * string>`**.

4. **closure #4 truncation ref 채택**:
   - `exportToJson` walk 진입 시 `let truncated = ref false`, 절단 시점 (depth limit / 50 budget / path 외부 skip) `truncated := true` set.
   - depth 큰 정수 + 실제 절단 0건 = `view: full` 정확 emit.
   - `view: partial` 회귀 fact 5건 (§4.1) 그대로 유지.

5. **closure #5 DriftTests 격상 채택**:
   - sanity → set equality (`Assert.Equal<Set<string>>`).
   - `opLayerStaleTokens` 에 read-4종 (`list_projects` / `list_systems` / `describe_system` / `describe_subtree`) 추가.

**path notation 부속 결정 (closure #3 부속, v4 신규)**:
- (α) **현 dual-accept 유지** — wire 입력 `.` 와 `/` 모두 OK, 정규형 = dot (`normalizePath` 동작 변경 0).
- **root 절대 경로 표기 = leading `.`** (예: `.Proj1.SysA.Flow1`). 권장 어휘 — 호환성 위해 leading 없는 표기도 동일 의미 (export 는 store root 부터 dump).
- 이름의 `.` 금지 invariant 유지 (변경 0). leading `.` 와 이름 충돌 0건.
- yaml-protocol-v0.md §2.5 normalizePath 절 변경 0 (이미 dual-accept).

**JSON envelope 표현 정정**:
- v2/v3 본문의 "JSON envelope" → "최상단 top-level mapping key (format 무관)" 으로 §4.1 정정.

**표 라벨 정정 (§3.2)**:
- "기존 도구 / 대체 호출" → "**현 도구 (Phase 5 직후, 폐기 대상) / Phase 6 후 호출**" 로 라벨 명료화. GUID 입력이 폐기 대상임을 시각적으로 강조 (사용자 우려: "describe_system(GUID...) — LLM 이 GUID 관리해야 한다는 뜻 인가?" 응답).
- Round-trip 영향 한 줄 보강 (2-RT → 1-RT).

**SSOT commit #1 본문 작성 진입 가능 상태**. closure list ✅ all resolved. 다음 step = trigger 발화 "Phase 6 commit #1 진행".

#### v4 후속 review 처리 (3-reviewer meta-review, 2026-05-13)

본 메모 v4 update 직후 generalist + accuracy specialist + completeness specialist 3-reviewer 메타 review 수행. 검증 통과 항목 처리 내역:

**Critical (수용 — typo 정정)**:
- §0.1 line 24 의 "§7.3 review 처리 이력" 인용이 깨졌음 — 실제 헤딩은 §7.2 만 존재. typo 정정 ("§7.2 review 처리 이력 — 본 메모 변경 추적 (v1 → v4 round 누적)").

**Major (수용 — v3 → v4 전환 과정의 stale 잔재 4건)**:
- §7 주의 사항 #4 "closure list 미결정 시 commit #1 본문 작성 불가" — v4 결정 완료로 무효화. "closure list ✅ v4 결정 완료 — SSOT commit #1 본문 작성 진입 가능 상태" 로 재기술.
- §7 주의 사항 #5 "describe_subtree 의 EntityKind 자동 판별 risk" — closure #2 `tryFindEntity` 가 path 깊이로 EntityKind 자동 결정 + ApiDef vs Flow ambiguity 는 §4.6 / §4.5 kind 필드로 해소. "v4 해소" 라벨로 재기술.
- §7.1 후속 cycle "find_by_name long-term clean — (a) ordinal → (c) sibling-unique" — closure #3 v4 폐기 + 출력 `[(kind, path)]` 격상으로 unique 4-way 의사결정 항목 자체 소멸. archive 라벨 (`~~취소선~~`) + 재검토 trigger (dotted-path apply 안정성) 1줄 보강.
- §0.4 용어 사전의 envelope 정의 "top-level JSON object" — v4 §4.1 의 "JSON envelope 표현 정정 → format 무관" 결정과 직접 모순. "최상단 mapping (format 무관 — yaml/json 양쪽 동일). v4 키 enum 5개: protocol / project / systems / patch / view" 로 정정.

**Minor (수용 — 가벼운 정합 보강 7건)**:
- §0.1 읽기 순서 6번 line 에 "v4 결정 라벨 위치: §3.1 path notation / §3.2 표 라벨 / §4.1 envelope flag / §4.5 find_by_name 출력 / §4.6 thin lookup" 한 줄 보강.
- §0.2 표 caption 에 "5건 + 부속 1건 (path notation, 표 하단)" 명시 — 결정이 5건 + 1건임을 시각적 명확화.
- §3.1 의 "path 키 부재 = 전체" 와 "leading `.` 없는 path 도 동일 의미" 가 같은 단락에 혼재 — 두 다른 케이스 (i) 키 부재 vs (ii) leading 유무 무관 으로 분리 row 신설.
- §3.1 의 `normalizePath` 진입 시 `TrimStart('.')` 적용 사양 명문화 — §4.6 segment 카운트 룰과 leading `.` 정합 (segment 카운트에 영향 없음, 어휘 강조 prefix 일 뿐).
- §4.5 의 `kind` 필드 존재 의의 = ApiDef vs Flow 같은 동일 깊이 (System 직접 자식 3-segment) ambiguity disambiguation 목적 1줄 추가. `tryFindEntity` 내부 lookup 도 동일 ambiguity 해소 책임.
- §3.1 의 "query 인자 미도입" bullet 에 본문 1줄 보강 (search semantic vs export 의 선언적 dump semantic 분리 사유).
- §4.5 출력 path 예시 (정규형 = leading `.` + dot) vs §7.2 v4 closure #3 인용 (사용자 자연어 = `/proj/.../apidef1` slash) 의 비대칭 footnote 추가 — 정규화 후 dot 형으로 emit, slash 는 wire 입력 dual-accept 표기 예시.
- §5.1 코드 변경 표의 `LlmTurnContext.cs:37,42,96` 라인 인용 정정 — line 37 은 validate cache TTL doc-comment, 실제 cache key 영역은 line 42 (`_validateCache` tuple), line 96 (RunRead 안 cache read/write). `LlmTurnContext.cs:42,96` 으로 정정 + line 37 (doc-comment 동기화 권장) 비고.

**거부 / 보류 항목 (수용 안 함, 사유)**:
- "§0.2 표 컬럼명 `v4 결정` 이 round 종속" 권고 — 본 Phase 6 의 closure 결정 모두 종결 상태. 후속 v5/v6 round 발생 가능성 낮음 (commit #1/#2 이후는 결정 변경이 아닌 구현 round). 후속 round 발생 시 컬럼명 일반화 또는 row prefix `v4:` 추가로 처리. 현 시점 보류.
- "§7.2 v4 round 의 closure #2 결정 근거에 사용자 발화 인용 없음, closure #3 만 발화 직접 인용 → 비대칭" 권고 — closure #2 (path → entity 결정 메커니즘) 와 closure #3 (find_by_name 출력 spec) 에 대한 사용자 답변은 **단일 메시지 안에서 통합 발화** ("find_by_name 구현이지? 실제 chat LLM 이 하는 역할을 생각해 보고 결정하면 될 듯. ... proj.system.flow.work.call 순서이니..  오히려 사용자가 자연어에서 이름을 언급하면, 해당 item 이 어디에 있고, EntityKind 가 무엇인지 목록들을 반환해 주는게 맞지 않을까?"). 인용은 closure #3 절에 1회만 두고, 그 안에서 closure #2 (kind 인자 불필요) 도 자연 cover. 분리 인용은 단일 발화의 인위적 분리 → 거부.
- "§0 머리말 worktree 절대 경로 `F:/Git/ds2/phase6-readsurface` hardcode — 다른 머신 인수 시 부적합" 권고 — 현 단일 사용자 환경. 후속 인수 시 정정 비용 낮음 (한 줄). 현 시점 보류.
- "§5.1.1 신규 fact 6f-N 명명 컨벤션 적용 예시 (view: full|partial, tryFindEntity, pathOf 등) 1~2건 추가" 권고 — 실제 신규 모듈 (`ExportModelDocPathDepthTests.fs`) 작성은 commit #2 시점. fact 이름은 코드 작성과 함께 결정되어야 자연 정합. 메모에 가짜 예시 시기상조 → 거부.

**검증 통과 사항 처리율**: Critical 1/1 + Major 4/4 + Minor 7/11 수용. Minor 4건 거부/보류 (사유 명시).

**잔여 우려**: 없음. 본 메모는 인수자가 trigger 발화 후 SSOT commit #1 본문 작성에 진입할 정보 밀도를 갖추고 있음.

#### v5 (commit #1 + commit #2 chunk-1 실행 round, 2026-05-13)

사용자 trigger 발화 ("Phase 6 commit #1 진행" → "후속 작업 시작") 수신 후 본 메모의 spec 에 따라 단계적 실행. closure 결정 변경 없음 (v4 결정 그대로 적용) — 본 round 는 실행 round.

**SSOT commit #1 완료** (`e85edba`):
- `yaml-protocol-v0.md` 갱신: §1.7 결정 표 Phase 6 결정 7행 / §2.1 top-level 키 enum 5개 (`view` 추가) / §2.5.1 path resolver 절 신설 (`tryFindEntity` / `pathOf` 시그니처 + 깊이 ↔ EntityKind 매핑 + ApiDef vs Flow ambiguity + leading `.` strip) / §2.7 룰 #7,#8 신설 / §2.8 partial export view-only spec 신설 / §4 도구 시그니처 (export_model_doc path?/depth?, find_by_name [{kind, path}], validate_model path scope) + 풀세트 6종 / §6 phase 표 Phase 6 행.
- `done-yaml-protocol-implementation.md`: §2 phase 표 Phase 6 행 추가 / §3.0 후속 cycle 갱신 (find_by_name long-term clean archive + ModelProtocol.fs SRP split 추가).
- 자가 검열 (Agent 위임) Major 1 / Minor 2 발견. Major-1 (§6 phase 표 Phase 6 행 누락) + Minor-1 (§2.7 룰 #7,#8 ERROR prefix 비대칭) 본 commit 흡수. Minor-2 (§2.8 "50 entity budget" 출처) 는 commit #2 동기 처리.

**commit #2 chunk-1 실행** (staged + working tree, 본 commit 안):

- **chunk-1a (SSOT view 정책 정정)**: commit #1 본문의 §2.7 룰 #7 ("apply/validate 입력에 view: 키 존재 시 ERROR") 가 §2.8 ("view: full 인 export 결과는 apply/validate 재사용 가능") 와 직접 모순 — round-trip 시나리오 (자기 export 결과 재입력) 거부 문제. 정정:
  - 룰 #7: "apply/validate 입력에 `view: partial` 키" → ERROR (view: full 또는 부재는 허용).
  - 룰 #8: "apply/validate 입력의 `view:` 값이 full/partial 외" → ERROR.
  - §2.8 본문: view: full 허용 / 부재 허용 / partial 거부 + apply 입력 view: full 호환 / legacy / 사용자 직접 작성 호환.
  - §1.7 결정 표: "`view` flag 정책 (Phase 6)" row 통합 갱신.
  - §4 line 533, 537 도구 시그니처 본문: "view: partial 시 사전 거부 / view: full 또는 부재는 허용" 로 정합 정정.
- **chunk-1b (read 4종 일소)**:
  - `ToolOperations.fs`: `listProjects` / `listSystems` / `describeSystem` / `describeSubtree` / `formatProjectList` / `formatSystemList` / `formatFindResults` / 부속 `indent` / `arrowTypeName` 모두 일소 (-171 line).
  - `ModelProtocol.fs`: 신규 `tryFindEntity` (path 깊이로 EntityKind 자동 결정, ApiDef → Flow → None 순) + `pathOf` (root 까지 parent chain 추적, leading `.` prefix). `apply` 의 view 키 처리 분기 (full/부재 허용, partial 거부). `exportToJson` envelope 에 `view: "full"` emit.
  - `ModelTools.cs`: `ListProjects` / `ListSystems` / `DescribeSystem` / `DescribeSubtree` 4 메서드 + 호출지점 0 된 `ParseGuidOrThrow` 일소. `FindByName` 의 inline format 안에서 `ModelProtocol.pathOf` 호출로 정확한 dot path emit (kind + path 만 emit — closure #3 v4 정합).
  - `PromakerToolNames.cs`: `All` 배열 read 4종 제거 (10 → 6).
  - `DescribeSubtreeTests.fs`: 통째 삭제 + `Ds2.LlmAgent.Tests.fsproj` 컴파일 목록 정리.
  - `PromakerToolNamesDriftTests.fs`: sanity count 10 → 6 + 함수 이름 / 주석 / line 124-125 의 일소된 tool snake_case 단언 정리. set equality + opLayerStaleTokens 격상 (closure #5 v4) 은 chunk-3 의 ValidateModelTests 재작성과 함께 격상 예정.

**자가 검열 + 3-reviewer review 처리** (chunk-1 commit 직전):

- **자가 검열** (Agent general-purpose 위임): Major 2 (§6 phase 표 누락 — commit #1 단계에서 해소 / §2.7 ERROR prefix 비대칭 — commit #1 단계에서 해소) + Minor 2 (50 budget 출처 — chunk-1c 통합 / FindByName inline format path 부정확 — 본 round 해소).
- **3-reviewer review** (사용자 제공, generalist + logic + design 합의):
  - Critical A (DriftTests 미갱신, 합의 2/3) ✅ 수용 — sanity count 10 → 6 + 함수 이름 + 주석 + 일소된 tool snake_case 단언 정리.
  - Critical B (FindByName path 부정확, 합의 3/3) ✅ 수용 — `ModelTools.cs` 의 inline format 에서 `ModelProtocol.pathOf` 호출로 정확한 dot path emit.
  - Major C (tryFindEntity/pathOf dead code, 합의 2/3) ✅ 부분 해소 — B 처리로 pathOf 호출지점 1건 추가. tryFindEntity 는 chunk-1c.
  - Major D (tryFindEntity 가 Queries.projectSystemsOf 미재활용, 합의 2/3) ✅ 수용 — inline `@` 결합 → `Queries.projectSystemsOf` 1줄 치환 (사용자 철학 90/10 정합).
  - Outlier 1 (Generalist Major, view 거부 메시지에 "또는 view: 키 제거" 추가) ✅ 수용 — `ModelProtocol.fs` apply 의 view: partial 거부 메시지 보강.
  - Outlier 2/3/4 (Logic/Design Major) ⏸ chunk-1c 통합 — pathOf System orphan / 재귀 일관성 / EntityKind exhaustive (`tryPathOf : ... -> string option` 또는 `invalidArg`) / round-trip identity test 신설은 모두 chunk-1c 의 pathOf 실 호출지점 확대 + ExportModelDocPathDepthTests 신규와 동기 처리.
  - Outlier 5 (Design Minor, SSOT §2.7 룰 #7/#8 semantic swap deprecation marker) ✗ 반론 — 본 정정은 v3 → v4 round 의 신설 룰 본문 정합 강화이지 기존 정착 룰 semantic swap 아님. git blame 으로 추적 충분. SSOT 본문 deprecation marker 는 reader 부담만 ↑ — over-engineering.

**코멘트 추적성 보강** (사용자 우려 반영, 본 round 추가 정정):
- 본 PR 의 새 코멘트 안 closure 참조 (`closure #3 v4`, `§4.6 정합`) 가 어느 SSOT 의 § 인지 ambiguous → 파일 경로 명시.
- 정정 위치 2곳:
  - `ModelTools.cs:257` FindByName inline format 코멘트 — `(closure #3 v4)` → `(Phase 6 todo-read-surface-guid-cleanup.md closure #3 v4)`.
  - `ModelProtocol.fs` tryFindEntity docstring — `(병존 — §4.6 정합)` → `(병존 — Apps/Promaker/Docs/todo-read-surface-guid-cleanup.md §4.6 정합)`.
- 기존 컨벤션 (module / file 머리에 SSOT 파일 경로 한 번 명시, 본문은 § 번호) 은 그대로 유지 — yaml-protocol-v0.md 참조는 ModelProtocol.fs:16 / ModelTools.cs:17, 133 의 머리 명시 덕에 자연 추적 가능.

**검증**:
- `dotnet build Solutions/Ds2.sln` — 오류 0건 (경고 1건 = OllamaSharp source generator 무관, 기존).
- `dotnet test Solutions/Tests/Ds2.LlmAgent.Tests/Ds2.LlmAgent.Tests.fsproj` — 314/314 통과 (baseline 319 → -10 DescribeSubtreeTests fact 일소 - 2 일소 snake_case + 1 신규 ApiDef + ? 기존 회귀 무영향 = 314).

**잔여 작업 (chunk-1c~6)** — §0.5 의 "즉시 진입 가능 단계 (chunk-1c 부터)" 표 참조. 핵심:
- chunk-1c: `exportToJsonScoped` 본체 + `validateModel` path scope + reviewer Outlier 2/3/4 통합.
- chunk-2: `EditorChangeDigest.cs` 어휘 sweep.
- chunk-3: 테스트 (ValidateModelTests 재작성 / DriftTests set equality + opLayerStaleTokens 격상 / ExportModelDocPathDepthTests 신규).
- chunk-4: Prompt 7 파일 sweep.
- chunk-5: 외부 문서 sweep (paper / roundtrip / Poc / Solutions CLAUDE.md / doc historical).
- chunk-6: 광역 grep 잔재 0건 확인 + 자가 검열 + commit #2 confirm.

#### v6 (commit #2 chunk-1c 실행 round + budget patch + summary metadata 도입 + --review 처리, 2026-05-13)

사용자 trigger 발화 ("작업 시작") 수신 후 chunk-1c 본격 실행. v5 의 deferred chunk-1c 명세 (§0.5 v5 의 "즉시 진입 가능 단계 1번") 따라 코드/타입/테스트 정정 적용. closure 결정 변경 없음 — 실행 round.

**핵심 코드 변경** (§0.5 v6 의 "v6 시점 ... 실제 코드 변경" 절 상세 — 본 절은 결정 round 의 흐름 중심):

1. **chunk-1c 초안 구현** (working tree 1차 staged):
   - `tryPathOf` safe API 신설 + `pathOf` compat wrapper. orphan System → None / unsupported kind → None.
   - `exportToJsonScoped` 본체 신설 — `exportToJson` 후 `JsonNode` post-process 방식 (path scope / depth cap / 50-entity budget / view: full|partial 재스탬프).
   - `validateModelByPath` 신설 + `validateModelByGuid` Deprecated 잔존 (chunk-3 일소 예정).
   - `formatScopeLabel` 가 `System(path=...)` emit (GUID 노출 회피).
   - `ExportModelDoc(format, path?, depth?)` + `depth < 0` 사전 거부.
   - `ValidateModel` 'global' literal + GUID 분기 폐기.
   - LlmTurnContext cache key sentinel `""` 명문화.
   - ValidateModelTests 1건 assertion 정정 (`System(id=` → `System(path=`).
   - 빌드 0 오류 / **314 / 314 통과**.

2. **chunk-1c 1차 자가 검열** (Agent 위임): Major 0 / Minor 3 발견 (`applyEntityBudget` Major-1 = 마지막 system 도 통째 제거되는 회귀 가능성 / 중복 path resolver / JsonNode reparent 비용). 사용자 결정 단계 진입.

3. **budget patch 사용자 결정**:
   - 사용자 제안 = 상한 50 → 500 격상 + 빈 systems[] 시 `null` marker (= `[]` 의 "실제 빈 store" 와 의미 분리).
   - 채택. `[<Literal>] PartialBudget = 500` 도입.
   - 2차 자가 검열 통과 (Major 0 / Minor 3, 모두 변경 불요).

4. **summary metadata 도입 (사용자 결정)** — null marker 의 한계 ("절단 발생만 알고 정도 모름") 보완 의도:
   - 사용자 옵션 비교 결과 옵션 (C) 별도 metadata 키 채택. 키 이름 = `summary` (사용자 직접 선택).
   - 구조: `summary: { totalEntities, emitted, budget }`. LLM 이 "513 vs 50000" 으로 후속 호출 전략 (좁혀 재호출 / 포기) 결정.
   - systems 는 항상 array (type 단일성 유지 — union heterogeneity 회피).
   - `apply` 에 `summary` 키 사전 거부 분기 추가 (view: partial 거부와 동일 패턴).
   - 이전 null marker 분기는 폐기 (summary 가 그 자리 대신).
   - 3차 자가 검열 통과 → Major 2 (`totalEntitiesBefore` 주석 부정확 = "전체 store" → 실제는 "첫 project" / `countEntities` ApiDef 미카운트) 즉시 수정 + Minor 2 (PoC 무영향) 후속 cycle deferral.

5. **외부 `--review` 처리** (Major 3 / Minor 10 분류 처리):
   - **M1 (NFC normalize 누락)**: `pathSegmentsForScope` 에 `Normalize(NormalizationForm.FormC)` 1 줄 추가. ModelProtocol.normalizePath 와 정합.
   - **M2 (orphan System path emit silent 변경)**: `FindByName` inline 을 `tryPathOf` 직접 호출 + None 분기에서 `<orphan:Name>` marker emit. LLM 진단 가능성 회복.
   - **M3 (unit test 부재)**: chunk-3 의 `ExportModelDocPathDepthTests.fs` 신규 fact 리스트에 본 v6 의 lock-in 6건 (f/g/h/i/j/k — summary emit 4건 + orphan marker + tryPathOf unsupported kind) 추가 예약.
   - **Minor 10건 중 변경 불요 7건** (m1/m2/m3/m7/m8/m9/m10) — 모두 PoC scale 무영향 / 후속 cycle 후보 / production path 0건 / 기존 패턴 정합.
   - **Minor 3건 반론** (m4/m5/m6):
     - m4: `applyPathScope` 의 `>=5 segs` 분기는 Call scope path (5-segs) 가 `tryFindEntity` 통과 시 실제 도달. reviewer 가 6+ segs 차단과 혼동.
     - m5: validate_model cache key 가 path 그대로인 것은 store entity name lookup (case-sensitive) 과 정합. case-insensitive cache 는 store semantic 위배.
     - m6: "depth 명시 + 절단 0 → view: full" 은 todo v4 closure #4 (b) 결정 ("실제 truncation 발생 여부") 정합. v2/v3 의 "depth 명시 = partial" 표기는 v4 에서 의식적 폐기 — reviewer 가 stale 표기를 참조.

**최종 v6 staged 상태 검증**:
- `dotnet build Solutions/Ds2.sln` — 0 오류 / 0 신규 경고.
- `dotnet test Ds2.LlmAgent.Tests` — **314/314 통과** (chunk-1b baseline 그대로).
- 자가 검열 통과 (3차) + 외부 `--review` 통과.
- **commit 진행 가능 상태**.

**잔여 우려**: 없음. 본 메모는 chunk-2 이후를 다른 세션이 인수받을 수 있도록 §0.5 v6 절 + 본 v6 round 명세 + 본문 §5.1 / §5.1.1 / §5.2 / §5.3 (변경 없음) 으로 충실 cover.
