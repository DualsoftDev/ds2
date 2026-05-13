# TODO — Read surface GUID-free 정렬 (Phase 6 후보)

> Phase 5 (mutation op-layer 15종 일소) 이후 잔존 read 6종에 남은 GUID 입출력을 청산하고,
> `export_model_doc` 의 scope 인자로 list/describe 류를 흡수하여 read surface 압축.
>
> 본 문서는 **--plan 모드 논의 결과 + 5인 reviewer 검증 통과 사항 + 메타 review 검증 통과 사항** 반영의 transfer 메모.
> 실제 구현은 사용자 명시적 지시 (§0.3 trigger 발화) 후 착수.

---

## 0. 새 세션 진입 가이드 (필독)

### 0.1 읽기 순서

1. **§0.2 closure list** — SSOT commit #1 진입 전 결정 필요한 5건 (착수 차단 해제 항목)
2. **§0.3 trigger 발화** — 사용자 의도 확인 (ambiguous 발화 오해 방지)
3. **§0.4 용어 사전**
4. **§1.1 Phase 4 변형 B 충돌 검증** — 진입 prerequisite
5. **§1 작업 목표 + §2 배경** — 맥락
6. **§3 설계 + §4 결정 항목** — 본문
7. **§5 작업 단계** — 구현 시점 참조
8. **§7.3 review 처리 이력** — 본 메모 변경 추적

### 0.2 SSOT commit #1 진입 전 closure list (★ 결정 필요)

본 5건이 결정되지 않으면 SSOT commit #1 의 본문 작성 불가. 진입 전 사용자 의사결정 요청.

| # | 항목 | 본문 위치 | 합의 |
|---|---|---|---|
| 1 | envelope 페이로드 flag 이름 — 기존 도구 인자 `scope` 와의 충돌 회피. 후보: `view: full \| partial` / `_meta.scope` / 기타 + v0 §2.0 top-level 키 enum 갱신 (legacy / unknown-key 정책) | §4.1 | 5/5 |
| 2 | path → entity 결정 메커니즘 — 후보: 강타입 `ResolvedEntity` DU (Project 포함, Arrow 미포함 footnote) / thin lookup `tryFindEntity : DsStore → string → (EntityKind * Guid) option` (사용자 철학 90/10 — `findByName` tuple 답습) | §4.6 | 5/5 |
| 3 | find_by_name path unique 정책 — 후보 (a) ordinal suffix / (b) GUID prefix tail / (c) sibling-unique invariant 강제 / (d) sanitizeName 의 `.` 거부 + legacy 마이그레이션 — + cross-kind 동명 처리 (resolver 에 kind 인자 강제 OR 출력에 `kind:` 필드) | §4.5 | 5/5 |
| 4 | partial 판정 기준 — 후보 (a) `path` OR `depth` 명시 여부 / (b) 실제 truncation 발생 여부 + 회귀 fact 5건 명세 + `depth: integer >= 0` 사전 거부 | §4.1 / §4.7 / §3.1 | 4/5 |
| 5 | `PromakerToolNamesDriftTests.fs` 회귀 가드 격상 — `opLayerStaleTokens` 에 read-4종 추가 + sanity fact 를 `Assert.Equal<Set<string>>(expected, listed)` set equality 로 강화 | §5.1 | 2/5 |

### 0.3 사용자 trigger 발화

본 작업 착수 의도를 명확히 하는 발화 (ambiguous "좋은데?" / "괜찮네" 오해 방지):

- **착수 (commit #1 SSOT 결정 단계)**: "Phase 6 go" / "Phase 6 진입" / "closure list 결정 시작"
- **착수 (commit #2 코드/prompt/test)**: "Phase 6 commit #2 진행" / "Phase 6 구현"
- **개별 closure 결정**: "closure #1: view 채택" / "#2: thin lookup 채택" 형태로 번호 + 결정 명시

위 형태가 아닌 발화는 **--plan 정신 유지 (논의 only)**.

### 0.4 용어 사전

- **SSOT**: Single Source of Truth — 본 작업에서는 `yaml-protocol-v0.md`
- **round-trip**: `apply(export(model)) ≡ model` invariant
- **view-only**: 표면 표현 한정 사용. wire 입력으로 재사용 불가
- **sibling drift**: 같은 사실이 여러 곳에 기록되어 한쪽만 갱신될 때의 어긋남
- **op-layer**: Phase 5 일소된 mutation 도구 15종 (`apply_operations` / `add_*` / `remove_entity` / `rename_entity`)
- **wire**: LLM ↔ MCP 간 실제 직렬화 JSON object
- **partial export**: path/depth 스코프 지정 export — 전체 export 와 의미 분리
- **envelope**: export 결과의 top-level JSON object (`protocol` / `project` / `systems` / `patch` + 신규 view flag)
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

**중요 (closure #1 - SSOT 영향)**: yaml-protocol-v0.md:437 의 기존 `scope: "project" | "system:<name>"` enum 인자 **폐기**. 대신 `path`/`depth` 두 신규 인자 도입.

```
export_model_doc(
  path?:   string  — name dotted-path  (예: "Proj1.SysA", "Proj1.SysA.Flow1" — segment 구분자 = '.')
                    생략 = 전체 (canonical)
  depth?:  integer >= 0  — 0 = root entity 자체 (자식 빈 list)
                           1 = 직접 자식까지
                           N = N-level 깊이
                           생략 = 전체 (canonical, 무한 의미)
  format?: yaml | json  (현행 유지)
)
```

규칙 (closure #4 결정 후 확정):
- **`depth` 는 wire 에서 정수만 허용**. 음수 / 비정수 / overflow 사전 거부 (`VALIDATION_ERROR: depth must be integer >= 0`). 무한 의미는 키 생략으로만 표현.
- **`path` 는 segment 구분자 `.`** (SSOT yaml-protocol-v0.md:103,230 의 entity 이름 `.` 금지 invariant 와 정렬). 본 메모의 path 예시는 모두 `.` 으로 통일.
- **`path` 미존재 시**: `VALIDATION_ERROR: path "<value>" 가 store 에 존재하지 않습니다 (fail-fast). 근사 후보: ...` — 기존 dispatcher 의 nearest-candidate 패턴 답습.
- **`path` + `depth=0`**: 정확히 그 entity 자체만 (자식 빈 list).
- **`path` 미지정 + `depth=0`**: envelope 만 (projects/systems 빈 list).
- **`query` 인자 미도입** — §4.4 결정 (find_by_name 별개 유지).

### 3.2 흡수 매핑

| 기존 도구 | 대체 호출 |
|---|---|
| `list_projects` | `export_model_doc(depth=0)` |
| `list_systems` | `export_model_doc(depth=1)` |
| `describe_system(GUID, deep=false)` | `export_model_doc(path="Proj.Sys", depth=1)` |
| `describe_system(GUID, deep=true)` | `export_model_doc(path="Proj.Sys")` |
| `describe_subtree(GUID, depth=N)` | `export_model_doc(path="...", depth=N)` |

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

### 4.1 partial export 의미 + envelope flag (closure #1, #4)

검증: `applyPatch` (ModelProtocol.fs:803) 의 patch.add 가 `in:` + 자식 키 추가를 PoC 미지원 — partial export 결과를 patch 로 재해석할 코드 부재.

**closure #1 결정 사항**:
- **envelope flag 이름** — 기존 도구 인자 `scope` 와 충돌 회피. 후보:
  - (a) `view: full | partial` — 짧고 의미 명료. 신규 top-level 키 +1 (v0 §2.0 enum 갱신).
  - (b) `_meta.scope` — reserved namespace 패턴. 향후 metadata 확장 여지. 단 nesting 추가.
  - (c) 기타 — 사용자 제안 가능.
- **scope 부재 시 policy** — legacy export (v0 이전) / 사용자 손편집 시:
  - (i) **full 추정** — silent 통과 위험 (partial 결과가 잘못 apply 될 수 있음).
  - (ii) **명시 강제** — flag 부재 시 `VALIDATION_ERROR: view 키 누락` (안전). 다만 손편집 사용자에게 부담.
  - 권장: (ii) + 친절 에러 메시지 ("v0 이전 export 결과는 `view: full` 추가 후 재시도").
- **v0 §2.0 top-level 키 enum 갱신** — 신규 flag 명을 enum 에 추가. SSOT commit #1 본문 포함.

**closure #4 결정 사항 (partial 판정 기준)**:
- (a) `path` OR `depth` 명시 여부 — 단순. 단 `depth=999` 시 결과 full 인데 partial 표시 → 의미 부정확.
- (b) **실제 truncation 발생 여부** — `exportToJson` walk 도중 entity 누락 1건 이상이면 partial, 0건이면 full. 의미 정확. 권장.

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

### 4.5 find_by_name path unique 정책 (closure #3)

`findByName` (`ToolOperations.fs:739`) 이 `(EntityKind, Guid, string)` 만 반환 — parent chain resolver 필요. sibling 이름 중복 시 동일 path → N entity 식별 충돌. + **cross-kind 동명**: 같은 project 안 Active System "Run" + Flow "Run" 이 동일 path 표현 → resolver silent first-match risk.

**unique 정책 4-way 결정 (closure #3 — 어느 것이든 OK, 명문화 필수)**:
- (a) **ordinal suffix** — `Proj.Sys.Flow1[2]` (같은 path 의 N번째). 단순. wire 안정성 낮음.
- (b) **GUID prefix tail** — `Proj.Sys.Flow1#a1b2`. wire 안정성 높음. 단 GUID-free invariant 와 미묘한 충돌.
- (c) **sibling-unique invariant 강제** — sanitizeName 단계에서 sibling 중복 reject. 가장 깔끔. 단 기존 모델 마이그레이션 필요.
- (d) **sanitizeName `.` 거부 + legacy 마이그레이션** — sanitizeName 에 `.` 거부 (Phase 1 적용 완료). legacy store 에 `.` 포함 이름이 있으면 quoting 또는 reject. (a/b/c 와 직교 — 보조 정책).

**cross-kind 동명 처리** — 위 (a-d) 와 별개:
- (α) **resolver 시그니처에 kind 인자 강제**: `tryResolveEntityByPath : DsStore → string → EntityKind → ResolvedEntity option` (또는 thin lookup 시 `(EntityKind * Guid) option`).
- (β) **find_by_name 출력에 `kind:` 필드 강제** + kind 미지정 호출은 multi-kind 매칭 시 ERROR.

권장: 잠정 (a) + (α) 조합 — 마이그레이션 cost 없이 wire 안정성 + cross-kind 안전성 확보. long-term clean 은 (c) + (α).

### 4.6 path → EntityKind resolver (closure #2)

검증: 현 ModelProtocol.fs 는 `findSystemByName` (line 786) + `findFlowByPath` (line 794, 2-segment hard-code) 만 — 일반 path resolver 부재.

**closure #2 — 후보**:
- (A) 강타입 DU:
  ```fsharp
  type ResolvedEntity =
      | ResolvedProject of DsProject      // ★ Project 포함 — ModelTools.cs:275 describe_subtree 4 EntityKind 와 정합
      | ResolvedSystem  of DsSystem
      | ResolvedFlow    of Flow
      | ResolvedWork    of Work
      | ResolvedCall    of Call
      | ResolvedApiDef  of ApiDef
      // Arrow 미포함 — done §3.0 후속 cycle (patch.arrows.remove) 와 연동. 본 Phase 의 export.path 시나리오에 Arrow root 없음.
  let tryResolveEntityByPath : DsStore → string → EntityKind → ResolvedEntity option
  ```
- (B) thin lookup (★ 사용자 철학 90/10 — `findByName` tuple 답습):
  ```fsharp
  let tryFindEntity : DsStore → string → (EntityKind * Guid) option
  ```
  Caller 가 EntityKind switch + Store lookup 으로 entity 본체 조회. site 별 type 분기 폭증 회피.

**사용처 비대칭 분석**:
- `patch.in:` (mutation, 현 PoC = Flow path 만)
- `export.path` (Phase 6 신규, 모든 kind cover)
- `validate_model.scope` (Phase 6 신규, Project/System/Flow 만)
→ DU 사용 시 site 별 unsupported case match → 컴파일 warning + 분기 폭증. thin lookup 시 caller 가 자연스럽게 site 별 kind 제한 enforce.

**권장**: (B) thin lookup. site 별 type 분기 폭증 회피 + 사용자 철학 정합 + 기존 `findByName` 답습. 단 사용자 의사결정 필요 (closure #2).

**기존 helper 처리** — Major-H 흡수: `findSystemByName` / `findFlowByPath` 는 **호출지점 그대로 유지 (병존)**. 신규 lookup 은 `export.path` / `validate_model.scope` 진입점에만. 사용자 철학 ("기존 코드 베이스의 수정 최소화") 정합.

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

| 파일 | 변경 |
|---|---|
| `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs` | `ExportModelDoc` 에 `path?`, `depth?` 인자 추가 + envelope `view` flag emit (closure #1). `ListProjects` / `ListSystems` / `DescribeSystem` / `DescribeSubtree` 4 메서드 본문 삭제. `FindByName` 출력 GUID → path (closure #3). `ValidateModel` 의 GUID 분기 + 'global' literal 제거 (§4.8). |
| `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs` | `All` 배열 10 → 6 (§3.3). |
| `Apps/Promaker/Promaker/LlmAgent/LlmTurnContext.cs:37,42,96` | `validate_model` cache key sentinel = `""` (§4.8). |
| `Apps/Promaker/Promaker/LlmAgent/EditorChangeDigest.cs:18,142,165` | 런타임 합성 message 의 read 도구 어휘 갱신 — list_projects/list_systems 권고 제거, validate_model 권고 유지. |
| `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` | `listProjects` / `listSystems` / `describeSystem` / `describeSubtree` helper 일소. `findByName` 출력 format 갱신 (GUID → path). `validateModelByGuid` → path 기반 lookup helper. |
| `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs` | `exportToJson` 에 `path` resolve + `depth` 절단 + view flag emit. **신규 lookup (closure #2 결정 따라 thin `tryFindEntity` 또는 DU)**. dispatcher 의 `apply_model_doc` / `validate_model_doc` 에 `view: partial` 사전 거부 분기. `findSystemByName` / `findFlowByPath` 호출지점 그대로 유지 (§4.6 병존). |
| `Solutions/Core/Ds2.LlmAgent/CLAUDE.md:115` | sibling fence — read 6종 → 2종 카운트 갱신 (↔ §3.3 SSOT). |

### 5.1.1 테스트 fact 별 cover 표 (Major-E 흡수)

| 파일 | 기존 fact | 처리 |
|---|---|---|
| `Solutions/Tests/Ds2.LlmAgent.Tests/PromakerToolNamesDriftTests.fs` | sanity count 10 (`Assert.Equal(10, ...)`) | **closure #5**: sanity → `Assert.Equal<Set<string>>(expectedSet, listed)` set equality + `opLayerStaleTokens` 에 `list_projects` / `list_systems` / `describe_system` / `describe_subtree` 4종 추가. 카운트만 6 통과 + description 잔재 silent 회귀 차단. |
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
4. **partial export 의미 spec 우선**: closure list (§0.2) 미결정 시 commit #1 본문 작성 불가.
5. **`describe_subtree` 의 EntityKind 자동 판별**: 현재 GUID. path 로 대체 시 cross-kind 동명 처리 (§4.5 closure #3).
6. **`validate_model` scope**: §4.8 — 'global' literal 입력 폐기, 출력 footer 어휘 유지.
7. **Phase 4 변형 B 충돌 검증 (§1.1)**: 진입 전 새 세션에서 1회 실행.
8. **자가 생성 파일 sweep 제외**: `Apps/Promaker/Promaker.kwak.sln`, `Apps/Promaker/Promaker.main.sln`, `Apps/Promaker/Docs/ds2.log*`, `bin/`, `obj/`, `.vs/`, `*.user`, `*.suo`, `TestResults/`, `.idea/`.
9. **parser fixture sweep false positive**: `StreamJsonParserTests.fs:54-58` 의 `add_system` literal 은 wire JSON parsing 동작 검증의 픽스처 — 도구 이름 자체와 분리. Phase 5 일소 도구 sweep 시 exclude.

### 7.1 후속 cycle 후보 (Phase 6 와 독립)

- **list_projects 흡수의 multi-project 한계** — exportToJson:1085-1094 가 단일 project 만 emit. v0 schema multi-project 미지원과 충돌. 별도 cycle.
- **find_by_name path partial match (옵션 B')** — `export_model_doc(path="prefix*")` wildcard. wildcard 어휘 신설 부담으로 본 Phase 미채택.
- **`find_by_name` long-term clean** — 잠정 (a) ordinal suffix → (c) sibling-unique invariant 강제 + 마이그레이션.
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
