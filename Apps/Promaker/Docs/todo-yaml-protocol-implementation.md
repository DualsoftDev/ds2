# TODO — YAML 기반 LLM ↔ MCP 프로토콜 구현 (이어받기)

> 이 파일은 다른 Claude Code 세션에서 작업을 이어받기 위한 transfer 문서입니다.
> 설계 본문은 [`yaml-protocol-v0.md`](./yaml-protocol-v0.md) 가 SSOT — 본 문서는 그 *남은 구현 할 일* 만 정리합니다.
>
> **주의**: 본 문서의 `.fs:307`, `.cs:16` 같은 라인 번호는 작성 시점 snapshot — 코드 진화 시 silent drift 가능. 작업 시 Grep 으로 함수명·심볼명 재확인 권고.

---

## 1. 작업 목표

Promaker 의 LLM ↔ MCP 통신 layer 를 현재 *op-level CRUD primitive* (현재 21개의 `[McpServerTool]`, GUID chaining 책임이 LLM 측) 에서 **doc-level 선언적 모델** (도구 3개 + 보조 2개, GUID 추상화) 로 전환합니다.

구체 의도:
- LLM 책임을 *자연어 → 선언적 모델 정규화* 로 한정
- MCP 책임을 *모델 → entity graph 변환 + cascade + 검증 + 트랜잭션* 으로 응축
- 사용자에게 YAML 을 SSOT 로 노출 (디스크 저장, diff/PR/revert, 수동 편집 가능)

**Wire format = JSON object** (LLM tool_use native, escape 0) / **Presentation = YAML** (사람·디스크 SSOT). 두 포맷은 schema 1:1 동형 — 자세한 결정 배경은 `yaml-protocol-v0.md` §8 부록.

배경·설계 결정·이점 비교·schema 정의·예시·마이그레이션 단계: **[`yaml-protocol-v0.md`](./yaml-protocol-v0.md) 참조**.

---

## 2. 현재 위치

| 단계 | 상태 |
|---|---|
| Phase 0 (schema v0 확정) | ✅ 완료 — SSOT (`yaml-protocol-v0.md`) + 본 transfer 가 4차 외부 review 누적 반영하여 commit (`0b5fce4` 초안, `87e5035` 4차 정정) |
| Phase 1 (PoC 구현) | ✅ **완료** — commit `ed6c7c3` (PoC) + `0d1dc6f` (외부 review 반영). 자가 검열 + 외부 review 결과는 §7 참조 |
| Phase 1.5 (1차 외부 review 반영) | ✅ **완료** — commit `0d1dc6f`. 회귀 위험 3건 + 중간 우려 4건 + 개선 4건 + 보너스 1건 (dispatcher Active Work workDuration 누락) 처리. 신규 19 테스트 → **289/289 통과** |
| Phase 1.6 (2차 외부 review 반영) | ✅ **완료** — commit `2b40746`. refactor 4건 (flow dedup single-pass / Option.map 우회 정리 / ArrowType 테스트 export 경유 / firstWorkDur 가정 주석). 무회귀 (289/289 유지) |
| Phase 2 (export_model_doc 정교화) | ⏳ **시작 가능** — §3.1 (Phase 2) 체크리스트. *Phase 1.5 가 일부 선반영* (workDuration/opposing override emit, ArrowType formatter, parseDuration 정리, patch.arrows.add 풀구현) — Phase 2 잔여 작업 = WithCyl.json round-trip + plain scalar YAML 출력 + cascade 자식 emit 정책 결정 |
| Phase 3 (prompt 마이그레이션) | ⏳ 대기 — **독립 PR 필수** (§6 룰 #11) |
| Phase 4 (UI YAML preview/apply) | ⏳ 대기 |
| Phase 5 (op-layer deprecation) | ⏳ 장기 |

**현 working tree 상태**: Phase 1.6 commit `2b40746` 완료 (`bKwak` branch, local only — upstream 없음). 남은 untracked = `Apps/Promaker/Promaker.kwak.sln` + `Apps/Promaker/Docs/ds2.log20260512` (자동 로그). Phase 2 진입 시 *clean working tree* 가정. baseline: **289 테스트 통과**.

---

## 3. 다음 세션에서 바로 착수 가능한 작업

### 3.1 Phase 2 — `export_model_doc` 정교화 + round-trip SSOT

**목표**: 현재 `ModelProtocol.exportToJson` 의 *얇은 export* 를 strengthening 하여, *export → apply 후 store 가 의미-동등* 인 SSOT 역할 보장. 우선 fixture: `Solutions/Tests/Ds2.LlmAgent.Tests/Fixtures/WithCyl.json` (GUI canonical cylinder).

**Phase 1.5 commit `0d1dc6f` 가 선반영한 항목**:
- ✅ workDuration override emit (Active Work + Passive 첫 work duration) — `formatDuration` helper
- ✅ opposing override emit (`inferOpposing` + `defaultOpposing`)
- ✅ ArrowType enum → 명시 string formatter (`formatArrowType`) — `%A` 의존 제거
- ✅ ArrowType 6종 round-trip 단위 테스트 (`InlineData`)
- ✅ patch.arrows.add 풀구현 + patch.add store-side 충돌 검출 + Critical 3 (apis:[] / 중복 flow / patch.arrows)

**Phase 2 잔여 작업** (체크리스트):
1. [ ] **WithCyl.json round-trip 테스트** — `Fixtures/WithCyl.json` → DsStore (manual deserialize 또는 ImportJson 호출) → `exportToJson` → `apply` (새 store) → `ModelEquivalence.captureShape` 일치. 핵심 SSOT 검증.
2. [ ] **WithCyl.json 의 store 로딩 경로 확인** — ImportFromJson / StoreSnapshot.fromJson 등 헬퍼 위치:
  ```bash
  grep -rn "ImportFromJson\|StoreSnapshot\.fromJson" Solutions/Core/Ds2.Editor/ Solutions/Core/Ds2.LlmAgent/
  ```
3. [ ] **export 의 cascade 자식 emit 정책 결정** — sugar 만 emit (short-form, 현재) vs cascade 자식 long-form emit. **추천**: short-form 유지 + sugar 매핑 deterministic 검증 단위 테스트 lock-in (사용자가 GUI 에서 cylinder cascade 를 *직접 수정* 한 경우만 long-form fallback 필요 — 이는 후속 cycle).
4. [ ] **format=yaml 출력 안정화** — 현재 `jsonElementToYaml` 이 string 을 항상 DoubleQuoted 로 출력 → 사람 친화 view 가 noisy. SSOT §3 예시처럼 plain scalar 가 안전한 경우 (영문 식별자, no special char) plain 으로 emit. 안전 검사 = `^[A-Za-z_][A-Za-z0-9_\-]*$` 일 때만 plain.
5. [ ] **device 추정 로직 정교화** — 현재 `apis = [ADV; RET]` 만으로 cylinder 판정. workDuration / opposing 까지 포함한 *fingerprint* 매칭으로 강화. mismatch 시 `custom(<systemType>) + apis` long-form fallback.
6. [ ] **drift test sanity 확인** — Phase 2 는 신규 [McpServerTool] 추가 *없음* → 25 그대로 유지.

**검증 방법**:
- baseline: **289 테스트** (Phase 1.5 commit `0d1dc6f` 시점). Phase 2 는 ~5-10 신규 테스트 추가 예상.
- `ModelEquivalence` 의 `FlowCount` / `ApiDefCount` int 필드가 cascade 중복 실행 detect 보장.

**잠재적 차후 작업**:
- export 자식 entity long-form emit 옵션 (`mode=verbose`) — 사용자가 GUI 에서 cascade 자식을 직접 수정한 경우. PoC 범위 외.
- export 응답에 GUID 매핑 표 (`refs`) 옵션적 동봉 — 디버깅용.

### 3.2 자가 검열 / 외부 review 잔여 우려 (Phase 2 진입 전 인지)

**M2 (deferred, Phase 1 자가 검열)**: `ModelProtocol.fs` 의 `applyPatch` 가 `collectSystems → buildSystems → buildActiveFlows` 를 직렬 호출. **Phase 1.5 commit `0d1dc6f` 에서 부분 보완** (collectSystems 후 diagnostic 게이트 추가). 단 patch 경로의 `buildSystems` 와 `buildActiveFlows` 사이의 게이트는 여전히 없음 — patch corpus 가 본격 들어오는 Phase 후속 cycle 에서 재검토.

**보류된 외부 review Minor 2건**:
- **m2**: `processOneArrow` (work-scope) / `processOneFlowArrow` (flow-scope) 25줄 가량 중복. closure (`resolveCallId` vs `resolveWorkId`) 차이로 추출 시 generic 파라미터 도입 필요 → 가독성 저하 가능. 별도 cycle 검토.
- **m5**: `ModelEquivalence.fs` 의 `Queries.allArrowCalls` 가 work 마다 전체 enumerate (O(N×M)). 테스트 helper + 실 모델 N≤20 라 functional 영향 0. perf 필요 시 helper 내 1회 캐싱.

### 3.3 SSOT (`yaml-protocol-v0.md`) 의 미결정 항목 (Phase 2 진행 중 결정 가능)

PoC 진입 후 실제 사례 만나는 시점에 결정 — *사전 over-design 금지*. trigger = "구현 중 해당 키/형식이 *실제* YAML 입력으로 들어와야 결정":

1. **Active system 의 직접 ApiDef 키** — Active 가 외부 노출 인터페이스 갖는 사양이 PoC corpus 에 등장하면 키 추가.
2. **arrowType `Group` / `Unspecified` 빈도** — PoC 의 corpus 에서 0건이면 v0 schema 에서 제외, 1건 이상이면 유지.
3. **patch 안 cross-system 추가 scope 표기 통일** — SSOT §3.4 가 `systems:` list 와 `in: <path>` 혼용 — PoC 의 patch case 처음 만나는 형태로 고정.
4. **Error message i18n** — 한국어 유지 (기존 `VALIDATION_ERROR` 정책 일치).

### 3.3 SSOT 결정 완료 항목의 Phase 1 작업 spec

결정 자체는 `yaml-protocol-v0.md` §1.7 결정 표가 normative SSOT. 본 절은 결정 본문만 — Phase 1 작업 적용 결과는 모두 §7 참조.

#### 3.3.1 entity 이름 `.` 금지 (← Critical C5)
**결정**: 시스템 / Flow / Work / ApiDef 등 모든 entity 이름에 `.` (점) 불허. path 구분자 `.` 와 충돌 회피. `sanitizeName` 적용 완료 (commit `ed6c7c3`).

#### 3.3.2 patch DSL 의 add+remove 룰 (← Critical C6)
**결정**: 같은 patch 호출 안에서—
- ✅ store 에 *원래 있던* entity 제거 + 신규 entity 추가 = **허용** (자연 시나리오)
- ❌ 같은 호출 안에서 *방금 추가한* entity 를 곧바로 제거 = **불허** (자기 모순)

`queueRemoveEntity` 의 기존 invalidOp 가 보장. **단** Phase 1 자가 검열 M2 (deferred) — `applyPatch` 의 게이팅 누락은 Phase 2 또는 별도 cycle 에서 보완 (§3.2 참조).

#### 3.3.3 `apply_model_doc` 의 project 키 처리 (← Critical C7, mode 인자 폐기)
**결정**: 별도 `mode` 인자 없음. LLM 책임 + MCP 시나리오별 자동 처리. dispatcher 의 4 시나리오 분기 적용 완료 (commit `ed6c7c3` `resolveProjectKey`).

| Store 상태 | `project:` 키 | MCP 동작 |
|---|---|---|
| 빈 store | 있음 (name=X) | 새 project X 생성, systems add |
| 빈 store | 없음 | 에러 — "빈 store 에서 시작하려면 project 이름 명시 필요" |
| project P 있음 | 없음 또는 동일 `P` | P 에 systems 추가 (자연 merge) |
| project P 있음 | 다른 `Q` | 에러 — "프로젝트 P 가 이미 열려 있습니다. Q 로 바꾸려면 '파일 > 닫기' 후 재시도" |

**Phase 3 prompt 작업**: store 에 이미 project 열려있는 사실을 LLM 이 snapshot 으로 인지하도록 prompt 명시 + 새 project 요청 시 GUI 닫기 안내 룰 추가.

#### 3.3.4 `workDuration` 키 통일 (← 1차 review CR-5)
**결정**: Active Work duration override 와 Passive device workDuration 모두 **`workDuration: <duration>`** 단일 키. dispatcher 가 `workDuration` 만 수용 + 옛 `duration:` 발견 시 친절 에러 메시지 적용 완료. 단위 grammar `^(\d+)(ms|s)$` SSOT §2.3 적용.

### 3.4 2차 review 발굴 사항 — 결정 완료 (모두 Phase 1 적용)

#### 3.4.1 Known device sugar = 3종 한정 (← Review #2)
**결정**: `cylinder` / `clamp` / `robot` 만 sugar. 그 외 모두 **`device: custom(<Type>), apis: [...]` long-form** 사용. dispatcher 적용 완료.

#### 3.4.2 ApiDef 중복 Call — 그대로 허용, alias/#index 폐기 (← Review #4)
**룰 요약**:
- *concurrent 의미* (arrow 없음) — 중복 ApiDef Call 자유 (예: 두 cylinder 동시 ADV)
- *순차 의미* (arrow 로 chain) — 그 Work 안 각 ApiDef Call 은 *고유* 이름이어야 함

`queueAddCallAllowDup` 신규 entry + dispatcher 의 *useAllowDup* 분기 (Work 안 arrows 없음 ∧ 중복 calls 검출 시) 적용 완료. `hasCallNameClash` 함수 자체 미수정 — 기존 op-layer 회귀 차단.

#### 3.4.3 `yaml_to_json` LLM 노출 여부 — Phase 1 잠정 비노출
**현 정책**: `json_to_yaml` 만 LLM 노출. `yaml_to_json` 은 사용자 UI 편집 워크플로 내부 helper 로 *비노출* — drift sanity = 25.

**남은 결정 trigger**: PoC corpus 에서 LLM 이 YAML 문자열을 *직접 받아 변환 필요* 한 시나리오 발생 시 노출 검토. Phase 3 prompt 작성 중 결정.

#### 3.4.4 device sugar 외 입력 정책 (`device: pusher` 등) — Phase 1 적용
**정책**: known sugar 3종 외 bare literal → **validate 에러** + 친절 메시지: `'pusher' 는 sugar 미정의. device: custom(<Type>), apis: [...] long-form 사용.` `parseDevice` 의 `UnknownSugar` case 가 dispatcher 단계에서 reject.

---

## 4. Phase 3 이후 (참조용 메모)

### Phase 3 — Prompt 마이그레이션
- 대상 파일:
  - `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md` — *주력 진입점을 `apply_model` 로 교체*. 기존 op-layer 도구 21개 정의는 *escape hatch 섹션으로 강등*. schema 예시는 YAML 표기로 제공 (사람·LLM 모두 가독), LLM 은 의미 동형의 JSON object 로 직접 생성.
  - `2.modeling.md` — cascade 규칙 / canonical 형상 / arrow 결정 트리는 그대로 유지.
  - `1.entities.md` — 변경 없음.
  - `4.attachments.md` — 영향 검토 (사용자 첨부 처리 룰 — apply_model schema 와 충돌 가능성).
  - `CLAUDE.md` (Prompts 폴더) — facts.txt 흡수 규칙 등 — 영향 검토.

### Phase 4 — UI
- Promaker 채팅 패널: LLM 이 만든 JSON 모델을 *YAML preview + apply 버튼* 으로 렌더링. 사용자 검토 → 명시적 confirm → commit.
- 보조: Mermaid 다이어그램 view (SSOT §8.4) 도 함께 렌더 — Phase 4 의 nice-to-have.

### Phase 5 — Deprecation (장기)
- op-layer 도구 21개 중 PoC 후 사용 빈도 0 인 것부터 점진 정리.
- escape hatch 유력 후보: `add_api_def` / `add_arrow` / `remove_entity` / `rename_entity` (해당 케이스는 patch DSL 도 적합).
- 측정 방법: `Apps/Promaker/Promaker/bin/Debug/.../logs/ds2.log*` 의 `ToolCall ...` 라인 grep 으로 도구별 호출 빈도 집계. 일정 기간 (예: 1개월) 0건이면 deprecation 후보.

---

## 5. 관련 파일 / 경로

### 설계 / 참조
- `Apps/Promaker/Docs/yaml-protocol-v0.md` — **SSOT (먼저 읽기)**
- `Apps/Promaker/Promaker/LlmAgent/Prompts/1.entities.md` — entity 모델 참조
- `Apps/Promaker/Promaker/LlmAgent/Prompts/2.modeling.md` — 도메인 룰 SSOT
- `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md` — 현 op-layer 도구 사용 규약 (Phase 3 에서 교체 대상)

### 코드
- `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` — 기존 `queueAdd*` / `queueRemoveEntity` / `queueRenameEntity` — 재사용 대상
- `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs` — MCP tool 진입점. 현재 21개 `[McpServerTool]`. Phase 1 에서 신규 `ApplyModelDoc` / `ValidateModelDoc` / `ExportModelDoc` (+ 보조 `YamlToJson` / `JsonToYaml`) 추가. snake_case = `apply_model_doc` 등.
- `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs` — `All` allowlist 에 신규 4~5종 추가 (`json_to_yaml` 만 노출 잠정 → 4종, `yaml_to_json` 노출 결정 시 → 5종)
- `Solutions/Tests/Ds2.LlmAgent.Tests/PromakerToolNamesDriftTests.fs` — `Assert.Equal(21, ..)` 실제 본체는 line 83 (`:77` 은 test 이름 라인). sanity count `21` → `25` (잠정) 또는 `26`
- `Solutions/Directory.Packages.props` — `YamlDotNet` 16.x 등록 (CPM)
- `Solutions/Tests/Ds2.LlmAgent.Tests/Helpers/ModelEquivalence.fs` — round-trip 의미-동등 helper 신규
- `Solutions/Tests/Ds2.LlmAgent.Tests/` — Phase 1 검증 테스트 위치
  - Fixture: `Solutions/Tests/Ds2.LlmAgent.Tests/Fixtures/WithCyl.json` (canonical)
  - 실행: `dotnet test Solutions/Tests/Ds2.LlmAgent.Tests/Ds2.LlmAgent.Tests.fsproj -c Debug --nologo`

### 솔루션
- `Solutions/Ds2.sln`
- `Apps/Promaker/Promaker.sln`

### 로그 / 검증 데이터
- `Apps/Promaker/Promaker/bin/Debug/net9.0-windows/logs/ds2.log20260512` — 현 op-layer turn 비용 실측 (turn #2 = $0.61 / 62 op / 1 재시도). Phase 1 후 동일 사양으로 측정해 절감 비교. **OS / 사용자 / Debug 빌드 의존** — Release 또는 다른 사용자 환경에서는 경로 다름.

---

## 6. 주의 사항

1. **--plan 정신 유지**: 사용자 명시적 구현 요청 전까지 코드 변경 금지. Phase 2 착수 시점은 사용자 결정.
2. **기존 op-layer 도구는 유지** — Phase 2~4 동안 escape hatch. Phase 5 에서 점진 deprecation.
3. **GUI canonical 정합 보존** — Passive cascade (Flow + Work×N + ApiDef×N + ResetReset Arrow) 가 `WithCyl.json` 과 형상 일치. 기존 `queueAddCylinder` 등 helper 가 이를 보장하므로 *그 함수들을 그대로 호출*하는 게 안전.
4. **kind 명시 강제** — SSOT §2.7 룰 #1.
5. **GUID 는 LLM 에 절대 노출 금지** — `apply_model_doc` 응답의 `refs` 필드도 *옵션* 으로 유지하되, prompt 에는 *이름만 사용* 명시.
6. **`device: custom(WedgeClamp)`** 표기는 valid YAML scalar + valid JSON string. parser regex (`ModelProtocol.deviceLiteralRegex`) 한 곳에서 처리.
7. **Wire = JSON object, View = YAML** — 두 경로 schema 동형 (`ModelProtocol.Yaml.fs` 의 `yamlToJson` / `jsonElementToYaml` 양방향).
8. **Drift test + Allowlist 동시 갱신** — `PromakerToolNamesDriftTests.fs` 의 sanity count (현 25) + `PromakerToolNames.cs` 의 All 배열. Phase 2 는 신규 도구 추가 *없음* → 25 그대로.
9. **F# compile order** — `Ds2.LlmAgent.fsproj` 에 `ModelProtocol.Yaml.fs` → `ModelProtocol.fs` 순 (Yaml helper 가 ModelProtocol 호출 안 함). 새 파일 추가 시 `StoreSnapshot.fs` *위* 배치.
10. **Phase 3 (prompt 마이그레이션) 은 독립 PR** — `3.tooling.md` (264 라인) + `2.modeling.md` (476 라인) 의 주력 진입점 교체는 50-70% 재작성. Phase 2 commit 과 한 PR 에 묶으면 회귀 추적 불가. *Phase 2 = export 정교화 / Phase 3 = prompt 재작성* 으로 분리.
11. **`hasCallNameClash` 함수 자체 수정 금지** — 신규 entry `queueAddCallAllowDup` 추가 방식 (Phase 1 commit `ed6c7c3` 적용 완료). 기존 op-layer 호출 경로 회귀 차단.
12. **YAML arrow 자연 형태**: `- A -> B : T` 가 YamlDotNet 에 의해 mapping `{"A -> B": "T"}` 로 해석됨. dispatcher 의 `extractArrowString` helper 가 string 과 1-key object 양쪽 정규화. 새 arrow path 추가 시 동일 helper 사용 권장.

---

## 7. 이미 정리된 사안 (참고)

### 7.1 Phase 1 적용 결과 (commit `ed6c7c3`)

**신규 파일**:
- `Solutions/Core/Ds2.LlmAgent/ModelProtocol.Yaml.fs` — yaml↔json (anchor/tag/merge/YAML 1.1 boolean 거부 enforce)
- `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs` — schema v0 dispatcher + 이름 테이블 + Levenshtein 후보 제안 + export
- `Solutions/Tests/Ds2.LlmAgent.Tests/Helpers/ModelEquivalence.fs` — round-trip 의미-동등 helper. **`FlowCount`/`ApiDefCount` int 필드 포함** (set 만으로 중복 흡수 방지 — M1 fix)
- `Solutions/Tests/Ds2.LlmAgent.Tests/ModelProtocolTests.fs` — SSOT §3.1/§3.2 round-trip + parser subset 부정 케이스 + concurrent ApiDef + sanitize

**수정 파일**:
- `ToolOperations.fs` — `sanitizeName` 에 `.` 거부 + `queueAddCallAllowDup` 신규 (`queueAddCallCore` 추출 패턴)
- `ModelTools.cs` — 신규 [McpServerTool] 4종 (`ApplyModelDoc` / `ValidateModelDoc` / `ExportModelDoc` / `JsonToYaml`)
- `PromakerToolNames.cs` — allowlist +4
- `PromakerToolNamesDriftTests.fs` — sanity 21 → 25
- `Directory.Packages.props` — YamlDotNet 16.2.0
- `Ds2.LlmAgent.fsproj` — compile order + PackageReference

**자가 검열 결과** (sub-agent general-purpose):
- Critical 0
- Major 3 → 적용 2 (M1 ModelEquivalence count, M3 validate docstring), deferred 1 (M2 applyPatch 게이팅 — §3.2 참조)
- Minor 6 → 적용 1 (m6 suggestion None 처리), 정보성 5

**테스트**: 270/270 통과 (baseline 258 + 신규 12).

### 7.1.5 Phase 1.5 — 1차 외부 review 반영 (commit `0d1dc6f`)

외부 reviewer (별개 sub-agent) 가 `ed6c7c3` 코드 리뷰 결과 식별·반영한 사항. 본 절은 review 문서가 부재한 환경에서도 완전 이해 가능하도록 항목별 풀어서 기술 (CLAUDE.md 룰 — review 처리 기록 시 reviewer 표기 번호만 나열 금지).

**회귀 위험 — 모두 수정 적용**:
- **patch.arrows 분기 누락**: SSOT 의 `patch.arrows.add` / `patch.arrows.remove` 시나리오가 dispatcher 에 없어 silent ignore. `findFlowByPath` helper 추가 + `patch.arrows.add` 풀구현 (Flow path 해석, work 이름 resolve, queueAddArrow 호출). `patch.arrows.remove` 는 `EntityKind` enum 에 Arrow case 부재로 PoC 명시 미지원 (친절 에러 메시지 + 후속 cycle 에 EntityKind 확장 + CascadeRemove 분기 추가 필요 명시).
- **apis 빈 list 명시 시 default 무력화**: 사용자가 `apis: []` 를 명시 입력하면 `Option.defaultValue` 가 `Some []` 를 통과시켜 cylinder 등 sugar 의 default `[ADV;RET]` 가 무력화 → 빈 cascade 생성. `Option.bind (fun l -> if List.isEmpty l then None else Some l)` 로 빈 list 를 None 으로 정규화.
- **중복 flow 키 처리**: 같은 `flow X:` 키가 두 번 등장 시 두 번째도 `queueAddFlow` 호출되어 `sysEntry.FlowIds[X]` 가 두 번째 ID 로 덮어써짐 (첫 번째는 leak). `dedupedFlowKeys` 로 첫 등장만 채택하도록 변경.

**중간 우려 — 모두 수정 적용**:
- **patch.add 의 store-side 충돌 검출 누락**: `collectSystems` 가 `ctx.Systems` (이번 입력의 system 이름 collection) 만 검사. patch 시나리오에서는 store 에 *이미 존재하는* 같은 이름 system 추가 시도 detect 못 함. `findSystemByName` helper 로 store 측도 검사 + diagnostic.
- **exportToJson 의 workDuration / opposing override 직렬화 누락**: Active Work 의 `Duration` 이 default (500ms) 와 다르거나 Passive 의 internal Work duration override / 실제 opposing 형태 (chain/all-pairs/none) 가 device default 와 다르면 emit 되어야 round-trip. `formatDuration` (TimeSpan → "Nms"/"Ns" grammar), `formatArrowType` (enum → SSOT §2.4 string), `inferOpposing` (ResetReset arrow 갯수 → opposing 추정), `defaultOpposing` (cylinder/clamp=chain, 그 외 none) 추가하여 mismatch 시 emit. **별도 발견**: dispatcher 의 Active Work `workDuration` 키 자체를 처리하지 않아 (read 만 하고 plan 에 적용 안 함) override 가 export 에서 보이지 않을 뿐 아니라 *입력 round-trip 도 깨짐*. 동시 수정.
- **parseDuration regex fallback unreachable**: regex 가 `(ms|s)` 만 capture 보장하므로 `match | u -> Error` 분기 도달 불가. 단순 `if/else` 로 정리.
- **테스트 커버리지 보강**: 신규 19 테스트 추가 — YAML custom tag (!tag) 거부, YAML merge key (`<<:`) 거부, YAML duplicate map key 거부, YAML multi-document (`---`) 거부, kind=passive 인데 flow 키 존재 시 에러, kind=active 인데 device 키 존재 시 에러, `apis: []` default 적용 회귀, 중복 flow 키 회귀, ArrowType 6종 InlineData round-trip, §3.4 patch (Zone 4 추가) round-trip, store-side 충돌 patch.add 에러, patch.arrows.remove 미지원 에러, workDuration override export round-trip, opposing override (robot=chain) export round-trip.

**개선 — 적용 4건 / 보류 2건**:
- **deviceDefaults 의 UnknownSugar dead branch**: 호출처에서 사전 분기 처리되므로 본 함수 도달 불가. 시그니처를 `option` 제거 + UnknownSugar 케이스에 `failwithf` (호출 시 invariant 위반 명시).
- **nearestCandidates 키 비대칭**: `resolveCallId` 는 normalized 키, `resolveWorkId` 는 raw 키로 Levenshtein 계산 — 일관성 부족. `resolveWorkId` 도 normalized 통일.
- **Levenshtein F# 관용성**: `for j in 0 .. lb do` (IEnumerable seq) → `for j = 0 to lb do` (primitive loop) — 성능/관용성.
- **suggestion 빈 string 처리**: 후보 없을 때 `Some ""` 가 들어가 ` (제안: )` 빈 괄호 출력. `if candidates.IsEmpty then None` 로 정정 (Phase 1 자가 검열 단계에서 이미 적용 완료).
- **arrow processing 25줄 중복** (보류): work-scope `processOneArrow` 와 flow-scope `processOneFlowArrow` 의 골격 중복. closure (`resolveCallId` vs `resolveWorkId`) 차이로 추출 시 generic 파라미터 도입 필요 — 가독성 저하 가능. 별도 cycle 검토.
- **ModelEquivalence 의 allArrowCalls O(N×M)** (보류): work 마다 전체 ArrowBetweenCalls 순회. 테스트 helper + 실 모델 N≤20 라 functional 영향 0. 필요 시 helper 내 1회 캐싱.

**테스트**: 289/289 통과 (270 baseline + 신규 19).

### 7.1.6 Phase 1.6 — 2차 외부 review 반영 (commit `2b40746`)

`0d1dc6f` 이후 외부 reviewer 의 2차 검토 — Critical / 중간 우려 *없음* 판정 + 정확성 모두 ✅. 개선 항목 4건 적용·반영, 참고 우려 2건 명시 보류.

**개선 — 적용**:
- **flow dedup single-pass 통합**: 이전 구현이 HashSet 을 두 번 만들어 (1: diagnostic, 2: dedup) 의도가 분산. `List.filter` 한 pass 안에서 dedup + diagnostic 동시 수행하도록 통합.
- **Option.map 우회 패턴 정리**: `Passive opposing` 추정 시 `internalFlow |> Option.map (fun _ -> ...)` 가 Option 내부값을 사용하지 않고 단지 `IsSome` 여부만 보는 구조였음. `if internalFlow.IsSome then ... else 0` 로 자연스럽게 변경.
- **ArrowType round-trip 테스트의 `%A` 의존 제거**: 본 변경의 핵심 motivation 이 `%A` 의존 회피였는데 회귀 테스트는 여전히 `Assert.Equal(typeName, sprintf "%A" t)` 로 검증 → 같은 path 를 fence 하지 못함. `exportToJson` 경유 round-trip 으로 변경 — 실제 `formatArrowType` (private) emit path 까지 검증.
- **Passive `firstWorkDur` 가정 주석 명시**: 현재 구현은 internal Flow 의 *첫* Work duration 만 보고 emit 결정. 모든 known sugar (cylinder/clamp/robot/device) 가 동일 duration 으로 모든 Work 를 만든다는 가정에 의존 — 후속 sugar 가 Work 별 다른 duration 을 만드는 케이스 도입 시 본 가정이 깨짐. 주석으로 가정 명시 + 정책 재검토 필요성 표기.

**참고 우려 — 보류 2건 (현 PoC 범위 내 functional OK)**:
- **deviceDefaults 의 failwithf 안전망**: 호출처 사전 분기가 깨질 경우 unhelpful exception. 현 코드 흐름상 안전하나, 본 함수가 *공개 helper 로 진화* 시점에 Option/Result 재도입 권장.
- **workDuration override 검색의 plan 전체 linear scan**: `dispatchWork` 가 work 마다 `ctx.Plan.Operations |> Seq.tryPick` 로 방금 추가한 AddWork 검색 → Work N 개에서 O(N²). PoC 규모 (N ≤ 20) 에서는 무시 가능. Phase 2 의 cascade emit 단계에서 entry 별 `Dictionary<Guid, AddWork>` backref 도입 검토.

**테스트**: 289/289 통과 (변경 무회귀).

- SSOT (`yaml-protocol-v0.md`) 작성 — 549 라인. §2.0 Parser subset / §2.5 flow prefix grammar / §2.3 duration grammar / `protocol: promaker/v0` 키 / device regex ASCII-only / Unspecified 명료화 등 4차 누적 review 반영
- todo (`todo-yaml-protocol-implementation.md`) 작성 — 본 문서
- SSOT §3.2 예시의 `Z1.C1` 형 이름 → `Z1_C1` 등으로 정정

### 7.3 Phase 0 직전 별개 작업 (commit `44cf62f`)

- `chat-samples.txt` — `Pusher → Puncher` 정정 + 1줄 추가
- `ToolOperations.fs` — `tryReadStringArrayProp` helper 추가
- `3.tooling.md` — batch robot/device 예시 + 비대칭 주의 라인

**빌드/테스트 baseline**:
- Phase 0 commit 시점: 258 통과
- Phase 1 commit (`ed6c7c3`): 270 통과 (신규 12)
- Phase 1.5 commit (`0d1dc6f`): 289 통과 (신규 19 — 1차 review 반영)
- Phase 1.6 commit (`2b40746`): **289 통과 유지** (refactor — 무회귀)
- Phase 2 진입 시 *이 baseline 위에서 시작*
