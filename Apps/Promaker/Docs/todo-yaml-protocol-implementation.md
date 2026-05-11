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
| Phase 0 (schema v0 확정) | ✅ 완료 — `yaml-protocol-v0.md` 작성. design 결함 3건 (path / patch / project mode) 모두 결정 완료 (§3.3 참조) |
| Phase 1 (PoC 구현) | ⏳ **시작 가능** — 코드 변경 진입은 사용자 결정 |
| Phase 2 (export_model) | ⏳ 대기 |
| Phase 3 (prompt 마이그레이션) | ⏳ 대기 |
| Phase 4 (UI YAML preview/apply) | ⏳ 대기 |
| Phase 5 (op-layer deprecation) | ⏳ 장기 |

본 transfer 작성 직전의 *별개 작업* (apply_operations batch tolerance + prompt 보강) 은 commit `44cf62f` 로 이미 **commit 완료** (working tree 에서 tracked diff 으로 보이지 않음 — `git log` 로만 확인). YAML protocol 본격 코드는 아직 시작 전 (`Apps/Promaker/Docs/*.md` 3 파일만 untracked).

---

## 3. 다음 세션에서 바로 착수 가능한 작업

### 3.1 PoC 구현 — `apply_model` / `validate_*_doc`

**목표**: `yaml-protocol-v0.md` §3.1 (단일 cylinder) 과 §3.2 (multi-zone) 두 예시가 round-trip 통과. **Wire = JSON object**, 테스트는 YAML 예시를 YamlDotNet 으로 JSON 변환 후 입력.

**이름 — 결정 완료 (`Doc` 접미사)**: 새 도구 = `ApplyModelDoc` / `ValidateModelDoc` / `ExportModelDoc`. 기존 `ValidateModel` (consistency check, `ModelTools.cs:832-833`) 과 `PromakerToolNames.cs:23` 의 `mcp__promaker__validate_model` 그대로 유지. snake_case 매핑은 `mcp__promaker__apply_model_doc` / `mcp__promaker__validate_model_doc` / `mcp__promaker__export_model_doc`.

**Tool allowlist / drift test 동시 갱신 (Critical)**: 새 `[McpServerTool]` 추가 시 다음 2 파일도 갱신:
- `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs:16` — `All` allowlist 에 snake_case 이름 추가
- `Solutions/Tests/Ds2.LlmAgent.Tests/PromakerToolNamesDriftTests.fs:77` — sanity count `21` → 신규 도구 수만큼 증가
- 미갱신 시 빌드/테스트 즉시 실패 (drift test fail-fast).

**구현 파일 후보**:
- `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs` (신규) — JSON `JsonElement` AST + 이름 resolver + dispatcher. **AST 는 record/DU 정적 매핑이 아니라 `JsonElement` 수동 walking** — 동적 prefix 키 (`flow <Name>:`) 와 patch DSL 의 자유 형식 때문.
- `Solutions/Core/Ds2.LlmAgent/ModelProtocol.Yaml.fs` (신규) — YamlDotNet 양방향 변환 helper.
- `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` — 기존 `queueAdd*` 함수 재사용 (수정 최소화) + `sanitizeName` 에 `.` 거부 추가.
- `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs` — 새 `[McpServerTool]` 진입점 추가 (3개 + 위 §3.1 이름 결정).

**F# compile order (중요)**: `Solutions/Core/Ds2.LlmAgent/Ds2.LlmAgent.fsproj:28` 의 `ToolOperations.fs` 다음 line 에 신규 `<Compile Include="ModelProtocol.fs" />` + `<Compile Include="ModelProtocol.Yaml.fs" />` 추가. F# 은 *위→아래* 순서 의존이므로 ToolOperations 함수 호출하려면 그 뒤에 배치 필수.

**의존성 / Layer 룰**:
- `YamlDotNet` NuGet — **`Solutions/Directory.Packages.props`** 에 `<PackageVersion Include="YamlDotNet" Version="16.x" />` 추가 (CPM 활성). 그 후 `Ds2.LlmAgent.fsproj` 에 `<PackageReference Include="YamlDotNet" />` (Version 생략 — CPM 이 주입).
- **YAML 의존은 LlmAgent layer 안에서만**. Ds2.Core / Ds2.Editor 는 YAML 미인지 — wire 가 JSON object 이므로 자연 분리.

**구현 단계** (체크리스트 — SSOT 의 §2/§3 참조):
1. [ ] `YamlDotNet` 16.x → `Solutions/Directory.Packages.props` 등록 + 변환 helper 1쌍 (`yamlToJson` / `jsonToYaml`).
2. [ ] `JsonElement` walker — SSOT §2 schema 의 각 키 별 dispatcher.
3. [ ] 이름 → entity 테이블 1-pass 빌드 + dotted-path resolver (`/` → `.` normalize, NFC 정규화 — SSOT §2.0/§2.5).
4. [ ] device DU literal parse — SSOT §2.3 regex (ASCII-only).
5. [ ] `queueAddCallAllowDup` 신규 entry 추가 (`ToolOperations.fs`) — clash check bypass 변형.
6. [ ] dispatcher — `queueAdd*` 호출 (mapping 표 = SSOT §5). concurrent path 분기 시 `queueAddCallAllowDup` 사용.
7. [ ] validate 룰 6개 (SSOT §2.7) + Levenshtein 후보 제안.
8. [ ] patch DSL 4종 (add/arrows/rename/remove) — SSOT §2.6 entries 분리 표기.
9. [ ] **Round-trip 의미-동등 helper** (`Ds2.LlmAgent.Tests/Helpers/ModelEquivalence.fs` 신규, ~50 LoC) — `HelperGuiParityTests.fs` 의 `parseGuiShape`/`GuiCascadeShape` 패턴 재활용. duration 형식 (`"00:00:00.5000000"` ↔ `500ms`) 변환 helper 내부 정규화.
10. [ ] xUnit/FsUnit 테스트 — SSOT §3.1, §3.2 fixture round-trip 통과.

**검증 방법**:
- 테스트 위치: `Solutions/Tests/Ds2.LlmAgent.Tests/`. 실행 명령:
  ```bash
  dotnet test Solutions/Tests/Ds2.LlmAgent.Tests/Ds2.LlmAgent.Tests.fsproj -c Debug --nologo
  ```
- 현재 baseline: 258 test pass (xunit 의 InlineData 확장 포함 — 정적 카운트 243 와 일치 시 정상).
- xUnit/FsUnit 테스트: SSOT §3.1 / §3.2 YAML → (YamlDotNet) → JSON object → `ApplyModelDoc` → store 상태가 기대 entity 트리와 일치.
- Round-trip 정의 (Major M13): *의미-동등* 비교 — entity 개수 + 이름 + 부모-자식 관계 + arrow source/target/type 비교. anchor / comment / GUID 등 표면 차이 무시. (key 순서·whitespace 무시.)
- 회귀 fixture: `Solutions/Tests/Ds2.LlmAgent.Tests/Fixtures/WithCyl.json` — YAML 로 manual 작성 → apply → `ExportModelDoc(format=json)` 이 의미-동등.

### 3.2 SSOT (`yaml-protocol-v0.md`) 의 미결정 항목

PoC 진입 후 실제 사례 만나는 시점에 결정 — *사전 over-design 금지*. trigger = "구현 중 해당 키/형식이 *실제* YAML 입력으로 들어와야 결정":

1. **Active system 의 직접 ApiDef 키** — Active 가 외부 노출 인터페이스 갖는 사양이 PoC corpus 에 등장하면 키 추가.
2. **arrowType `Group` / `Unspecified` 빈도** — PoC 의 corpus 에서 0건이면 v0 schema 에서 제외, 1건 이상이면 유지.
3. **patch 안 cross-system 추가 scope 표기 통일** — SSOT §3.4 가 `systems:` list 와 `in: <path>` 혼용 — PoC 의 patch case 처음 만나는 형태로 고정.
4. **Error message i18n** — 한국어 유지 (기존 `VALIDATION_ERROR` 정책 일치).

### 3.3 SSOT 결정 완료 항목의 Phase 1 작업 spec

결정 자체는 `yaml-protocol-v0.md` §1.7 결정 표가 normative SSOT. 본 절은 *Phase 1 코드 작업 spec* 만 — 결정 배경/룰 본문은 SSOT 참조.

#### 3.3.1 entity 이름 `.` 금지 (← Critical C5)
**결정**: 시스템 / Flow / Work / ApiDef 등 모든 entity 이름에 `.` (점) 불허. path 구분자 `.` 와 충돌 회피.

**Phase 1 작업**:
- `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs:70-90` `sanitizeName` 에 *`.` 포함 시 거부* 룰 추가 (예: `if trimmed.Contains('.') then $"VALIDATION_ERROR: {field} 에 '.' 사용 불가 (path 구분자 예약)."`).
- 기존 sample / 테스트 fixture (`Solutions/Tests/Ds2.LlmAgent.Tests/Fixtures/WithCyl.json` 등) 검사 — 점 포함 이름 있으면 마이그레이션 (rename).
- SSOT 예시 (§3.2 등) 의 `Z1.C1`, `Z2.C3` 같은 시스템 이름을 `Z1_C1`, `Z2_C3` 으로 정정.

#### 3.3.2 patch DSL 의 add+remove 룰 (← Critical C6)
**결정**: 같은 patch 호출 안에서—
- ✅ store 에 *원래 있던* entity 제거 + 신규 entity 추가 = **허용** (자연 시나리오)
- ❌ 같은 호출 안에서 *방금 추가한* entity 를 곧바로 제거 = **불허** (자기 모순)

**Phase 1 작업**:
- 코드 변경 *불필요* — 현재 `ToolOperations.fs:475` 의 `queueRemoveEntity` invalidOp 가 이미 그대로 보장.
- validate 메시지만 더 명료히: `같은 patch 안 add 직후 remove 는 미지원 — patch 에서 add 자체를 빼시면 됩니다.`

#### 3.3.3 `apply_model` 의 project 키 처리 (← Critical C7, mode 인자 폐기)
**결정**: 별도 `mode` 인자 없음. LLM 책임 + MCP 시나리오별 자동 처리.

**LLM 책임** (prompt 측 규약, Phase 3 마이그레이션 시점에 명시):
- store 에 *이미 project 가 열려있다* 는 사실은 snapshot / `<editor_changes>` block 으로 자동 전달됨 — LLM 이 인지.
- 사용자가 *명확히 새 프로젝트* 를 요청하지 않은 한, `apply_model` 의 `project:` 키를 **생략하거나 기존 project 이름 그대로** 사용 → 기존 project 에 자연 추가.
- 사용자가 *명시적 새 프로젝트* 를 원하면 — LLM 이 사용자에게 "파일 > 닫기 (Ctrl+Shift+W) 로 닫아주세요" 안내. *LLM 이 자가 결정으로 기존 project close 하지 않음* (사용자 작업 보호).

**MCP 동작** (`apply_model` 의 시나리오 자동 분기):

| Store 상태 | `project:` 키 | MCP 동작 |
|---|---|---|
| 빈 store | 있음 (name=X) | 새 project X 생성, systems add |
| 빈 store | 없음 | 에러 — "빈 store 에서 시작하려면 project 이름 명시 필요" |
| project P 있음 | 없음 또는 동일 `P` | P 에 systems 추가 (자연 merge) |
| project P 있음 | 다른 `Q` | 에러 — "프로젝트 P 가 이미 열려 있습니다. Q 로 바꾸려면 '파일 > 닫기' 후 재시도" |

**Phase 1 작업**:
- `apply_model` 의 dispatcher 가 위 4 시나리오 분기.
- 기존 `add_project` 의 1-project 정책 (`ModelTools.cs:391-396`) 은 그대로 유지 — `apply_model` 의 dispatcher 가 *project 키 + store 상태 조합* 으로 사전 분기 처리.

#### 3.3.4 `workDuration` 키 통일 (← 1차 review CR-5)
**결정**: Active Work duration override 와 Passive device workDuration 모두 **`workDuration: <duration>`** 단일 키. SSOT 본문의 옛 `duration:` 표기는 폐기.

**Phase 1 작업**:
- ModelProtocol.fs dispatcher 가 `workDuration` 키만 수용. `duration` 키 발견 시 validate 에러 + 친절 메시지 ("`workDuration` 으로 변경하세요").
- 단위 표기 grammar 는 `^\d+(ms|s)$` 권고 (MJ-4 — PoC 시작 시점 wire scalar type 함께 확정).

### 3.4 2차 review 발굴 사항 — 결정 완료

#### 3.4.1 Known device sugar = 3종 한정 (← Review #2)
**결정**: `cylinder` / `clamp` / `robot` 만 sugar (각각 `queueAddCylinder` / `queueAddClamp` / `queueAddRobot` 매핑). 그 외 (pusher / conveyor / agv / gripper / lifter / crane 등) 모두 **`device: custom(<Type>), apis: [...]` long-form** 사용.

**Phase 1 작업**:
- SSOT §2.3 의 known case 표가 이미 3종으로 정정.
- SSOT §3 예시의 `device: pusher` 등이 이미 `device: custom(Pusher), apis: [PUNCH]` 로 정정.
- ModelProtocol.fs dispatcher 가 (cylinder/clamp/robot) 만 sugar dispatch, 그 외는 `queueAddDevice` 호출.

#### 3.4.2 ApiDef 중복 Call — 그대로 허용, alias/#index 폐기 (← Review #4)
**결정**: 같은 Work 안 같은 ApiDef 가 *N회 등장 가능* (concurrent 의미). alias / `#index` 같은 표기 *불필요·폐기*. 단 ArrowBetweenCalls 의 source/target 으로 *중복 이름* 참조는 validate 에러 (모호성).

**룰 요약**:
- *concurrent 의미* (arrow 없음) — 중복 ApiDef Call 자유 (예: 두 cylinder 동시 ADV)
- *순차 의미* (arrow 로 chain) — 그 Work 안 각 ApiDef Call 은 *고유* 이름이어야 함

**Phase 1 작업** (← 4차 review Critical C1, 결정 완료):
- `hasCallNameClash` 함수 *자체는 수정 금지* — `let private` 가시성이고 기존 op-layer (`add_call` direct + `apply_operations` batch via `queueAddCall`) 모두 의존. 완화 시 기존 LLM workflow 의 순차 chain 중복 차단 회귀.
- **신규 entry `queueAddCallAllowDup`** 추가 (`ToolOperations.fs` 내 `queueAddCall` 변형). clash check 만 bypass, 나머지 (이름 sanitize / GUID resolve / cascade) 동일.
- YAML dispatcher 가 *concurrent path* (Work 안 arrows 없음, calls 에 중복 ApiDef 포함) 일 때만 `queueAddCallAllowDup` 호출. *순차 chain path* 는 그대로 `queueAddCall` (기존 차단 유지).
- validate 룰 추가: ArrowBetweenCalls walker 가 *Work 안 같은 `{system}.{api}` Call 이 N개일 때 그 이름의 source/target 참조* 검출 → 친절한 에러 메시지.
- SSOT §5 매핑표가 두 path 분리 명시 (`queueAddCall` vs `queueAddCallAllowDup`).

#### 3.4.4 `yaml_to_json` LLM 노출 여부 — PoC 중 결정
**현 잠정 결정**: `json_to_yaml` 만 LLM 노출 (apply 응답 YAML preview 용). `yaml_to_json` 은 사용자 UI 편집 워크플로 내부 helper 로 *비노출*.

**남은 결정 trigger**: PoC corpus 에서 LLM 이 YAML 문자열 입력을 *직접 받아 변환 필요* 한 시나리오가 발생하면 노출 검토. 발생하지 않으면 그대로 비노출 유지.

**drift sanity count 영향**: 비노출 유지 → `21 → 25`. 노출 결정 시 → `21 → 26`. Phase 1 진입 시 잠정 `25` 로 작성, 결정 변경 시 1줄 정정.

### Phase 2 — `export_model(format: json|yaml)`
- 현재 store 상태 → JSON object 또는 YAML 문자열. round-trip 검증 (`apply(export(model)) ≡ model`) 의 SSOT 역할.
- 우선 `Solutions/Tests/Ds2.LlmAgent.Tests/Fixtures/WithCyl.json` 으로 검증.

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

1. **--plan 정신 유지**: 사용자 명시적 구현 요청 전까지 코드 변경 금지. Phase 1 착수 시점은 사용자 결정.
2. **§3.3 design 결함 3건 결정 완료** — Phase 1 작업 spec 으로 그대로 진입 가능.
3. **기존 op-layer 도구는 유지** — Phase 1~4 동안 escape hatch. Phase 5 에서 점진 deprecation.
4. **GUI canonical 정합 보존** — Passive cascade (Flow + Work×N + ApiDef×N + ResetReset Arrow) 가 `WithCyl.json` 과 형상 일치. 기존 `queueAddCylinder` 등 helper 가 이를 보장하므로 *그 함수들을 그대로 호출*하는 게 안전.
5. **kind 명시 강제** — SSOT §2.7 룰 #1.
6. **GUID 는 LLM 에 절대 노출 금지** — `apply_model` 응답의 `refs` 필드도 *옵션* 으로 유지하되, prompt 에는 *이름만 사용* 명시.
7. **`device: custom(WedgeClamp)`** 표기는 valid YAML scalar + valid JSON string. parser regex 한 곳에서 처리.
8. **Wire = JSON object, View = YAML** — 두 경로 schema 동형.
9. **Drift test + Allowlist 동시 갱신** — `PromakerToolNamesDriftTests.fs:77` 의 sanity count + `PromakerToolNames.cs:16` 의 All 배열. 미갱신 시 빌드/테스트 즉시 실패.
10. **F# compile order** — `Ds2.LlmAgent.fsproj` 에 `ModelProtocol.fs` 는 `ToolOperations.fs` *다음 line* 에 배치 (line 28 직후).
11. **Phase 3 (prompt 마이그레이션) 은 독립 PR** — `3.tooling.md` (264 라인) + `2.modeling.md` (476 라인) 의 주력 진입점 교체는 50-70% 재작성. Phase 1 PoC commit 과 한 PR 에 묶으면 회귀 추적 불가. *Phase 1 = 코드 + 도구 추가 / Phase 3 = prompt 재작성* 으로 분리.
12. **`hasCallNameClash` 함수 자체 수정 금지** — 신규 entry `queueAddCallAllowDup` 추가 방식 (4차 review C1 결정). 기존 op-layer 호출 경로 회귀 차단.

---

## 7. 이미 정리된 사안 (참고)

다음은 본 transfer 작성 직전 처리되어 *남은 일 아님*. 잘못된 정정 시도 회피 차원에서만 기록:

- `Apps/Promaker/Promaker/LlmAgent/Prompts/chat-samples.txt` — `Pusher → Puncher` (n 추가) 3건 + 1줄 추가, EOF newline 부재. 모두 commit `44cf62f` 에 포함.
- `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` — `tryReadStringArrayProp` helper. commit `44cf62f`.
- `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md` — batch robot/device 예시 + 비대칭 주의 라인. commit `44cf62f`.

빌드 0 경고 / 0 오류, LlmAgent.Tests 통과 상태로 commit 됨. 본 transfer 시작 시점 working tree 는 깨끗 (`?? Apps/Promaker/Docs/*` 의 새 문서 3개만 untracked).
