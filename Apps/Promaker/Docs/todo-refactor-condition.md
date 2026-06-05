# todo: Condition 표현 리팩터링

## 목표

`Call.Conditions` / `Work.Conditions`의 외부 작성 포맷을 개선한다.
현재 포맷은 한 노드 안에서 leaf 조건은 `conditions`, 중첩 그룹은 `children`으로 나뉘어 표현되어 JSON/YAML 작성 모양이 어색하다. 개선 방향은 먼저 결정 게이트에서 확정한다. 기본안은 기존 `conditions`/`children` 포맷에 기대값 sugar와 문서/진단 보강을 더하는 것이고, `op/items`는 leaf와 group을 하나의 표현식 AST로 통합하는 후보안이다.

## --orch 진행 표

현재 상태는 **Phase 6부터 자동 진행 가능**이다. Phase 0~5는 구현/독립 검열/테스트를 완료했다. Phase 4 에서 PromptCanary 선행 실패(.md/.mdx)도 해소되어 `Ds2.LlmAgent.Tests` 는 449 전부 통과한다.

> 알려진 선행 실패(Phase 1 무관): `Ds2.LlmAgent.Tests`의 `PromptCanaryTests`가 prompt 파일을 `.md`로 참조하나 실제 파일은 `.mdx`라 3건 실패한다. 코드 변경 전부터 깨진 상태이며 Phase 4(Promaker protocol docs, prompt 파일 취급) 또는 별도 작업에서 `.md`↔`.mdx` 정합으로 해소한다.

| Phase | 상태 | 작업 범위 | 주요 산출물 | 완료 조건 |
| --- | --- | --- | --- | --- |
| 0. 사전 결정 박제 | 완료 | 외부 wire 포맷, alias, ValueSpec fallback, 표시 정책, 저장 모델 변경 범위 확정 | `박제 결정` 절 갱신 | 모든 결정 항목이 하나의 선택지로 확정됨 |
| 1. Protocol foundation | 완료 | `AutoAux` 기본 타입 보정, condition object unknown-key diagnostics, Work condition 정책 | `ModelProtocol.fs`, `ModelProtocolTests.fs` | LlmAgent protocol tests 통과, legacy nested child type 보존 |
| 2. ValueSpec / `eq` | 완료 | leaf `eq` parse/emit, `InputSpec` round-trip, fallback 표현 | protocol/helper 코드, tests | `eq` 타입 추론과 unsupported fallback tests 통과 |
| 3. Multi-root emit | 완료 | `Conditions.[0]` emit 제거, 같은 `ConditionType` roots implicit AND 보존 | `ModelProtocol.fs`, tests | 다중 root export -> apply 의미 보존 |
| 4. Promaker protocol docs | 완료 | Prompt, example YAML, `yaml-protocol-v0.md` 갱신 | `yaml.md`, `flow.yaml`, `yaml-protocol-v0.md` | 문서의 canonical key와 examples가 protocol tests와 일치 |
| 5. JsonFormatter docs | 완료 | `json-format.md` stale field 정정, STJ/AASX 명칭 구분, Builder helper 설명 | `json-format.md` | `isRising` 제거, `isInverted`/Runtime 평가/명칭 정정 반영 |
| 6. Editor formula projection | 미시작 | `IsInverted`, `ContactKind`, empty condition 표시 정책 반영 | `ConditionFormulaProjection.fs`, projection tests | 표시 테스트 통과 |
| 7. Apps regression | 미시작 | Promaker, Core/Editor, AASX/Runtime 간접 회귀 확인 | test 결과, 필요 시 docs 보강 | 지정 test set 통과 또는 미실행 사유 기록 |

## 사용자 사전 결정 필요

아래 항목은 Phase 0에서 박제 완료했다. 선택 근거 확인용으로 남긴다.

1. 외부 condition wire 포맷
   - 선택: 1번. 기존 `condition: { type, isOR, isInverted, conditions, children }` 유지 + `eq`/진단/문서 보강.
   - 2번. `condition.autoAux/comAux/skipAction` + `op/items` 신설.
   - 영향: 1번은 구현량과 회귀 위험이 작고 기존 parser/emit을 재사용한다. 2번은 작성 모양은 좋아지지만 dual format과 새 diagnostics 비용이 커진다.

2. `callCondition` alias 정책
   - 선택: 1번. canonical key는 `condition`만 사용하고, 문서도 `condition`으로 정정한다.
   - 2번. `condition`을 canonical로 emit하되 입력 alias로 `callCondition`도 허용한다.
   - 영향: 1번은 현재 코드와 맞고 단순하다. 2번은 과거 문서 소비자 호환성은 좋아지지만 parser 분기가 늘어난다.

3. `eq`로 표현할 수 없는 `ValueSpec` fallback
   - 선택: 1번. `eq`는 단일 equality 전용으로 두고, `Multiple`/`Ranges`/bound는 명시적 typed `inputSpec` 호환 포맷으로만 받는다.
   - 2번. `in` / `range` 같은 사람 친화 sugar를 추가한다.
   - 3번. 이번 phase에서는 unsupported로 diagnostics 처리하고 후속 phase로 미룬다.
   - 영향: 1번은 범위 표현을 잃지 않으면서 구현 범위를 통제한다. 2번은 사용성은 좋지만 설계/테스트가 커진다. 3번은 가장 작지만 Runtime 표현력을 wire에서 다 노출하지 못한다.

4. `ValueSpec` parser/formatter 위치
   - 선택: 1번. 공용 helper를 `Ds2.Core`로 추출하고 Editor/LlmAgent가 같이 쓴다.
   - 2번. `Ds2.LlmAgent` 내부 전용 parser를 둔다.
   - 영향: 1번은 중복을 줄이고 장기 유지보수성이 좋다. 2번은 변경 범위가 작지만 Editor와 protocol 간 표현 drift 위험이 있다.

5. `ContactKind` formula 표시
   - 선택: 1번. formula projection에 `NcContact`, `RisingPulse`, `FallingPulse` 표시를 추가한다.
   - 2번. formula에는 표시하지 않고 Promaker 3D indicator 등 다른 표시 경로 담당으로 문서화한다.
   - 영향: 1번은 텍스트 수식만 봐도 의미가 보인다. 2번은 UI 변경량이 작지만 수식과 Runtime 의미가 어긋나 보일 수 있다.

6. 빈 condition 표시
   - 선택: 1번. Runtime 의미 그대로 빈 And는 `true`, 빈 Or는 `false`로 표시한다.
   - 2번. 기존 `(empty)` 표시를 유지하되 tooltip/docs/tests로 의미를 보강한다.
   - 영향: 1번은 오해가 적다. 2번은 UI 변화가 작지만 사용자가 `(empty)`를 "무시됨"으로 해석할 수 있다.

7. Work condition의 잘못된 top-level type 처리
   - 선택: warning-only 경로 없이 fail diagnostics로 처리한다. Work context에서 top-level `AutoAux` / `ComAux`는 legacy/new 모두 실패 diagnostics가 되어야 한다.
   - 기각: legacy 입력을 허용하고 warning만 내는 방식.
   - 영향: 의미 없는 조건이 저장되고 Runtime에서 무시되는 경로를 막는다.

8. Core/AASX 저장 모델 변경 범위
   - 선택: 1번. 이번 작업은 Promaker YAML/LLM wire와 표시/문서만 변경하고 `Ds2.Core.Condition` 및 AASX field payload는 변경하지 않는다.
   - 2번. 장기 정리까지 포함해 Core `Condition` 모델도 `ConditionExpression` 유사 구조로 바꾼다.
   - 영향: 1번은 Apps 영향이 작다. 2번은 범위가 커져 `CostSim`, `DSPilot`, `AasxEditor`, `Tutorial` 회귀가 필수다.

## 박제 결정

현재 확정된 결정:

- 내부 `Ds2.Core.Condition` 저장 모델은 즉시 갈아엎지 않고 기존 모델을 우선 재사용한다.
- NOT은 기존 `Condition.IsInverted`에 대응되는 `isInverted: true` 노드 플래그를 사용한다.
- 같은 `ConditionType`의 여러 top-level root 의미는 implicit AND다.
- legacy `children` 입력의 explicit child `type`은 기존 round-trip test와 호환되도록 보존한다.
- AASX field 명칭 `[<AasxField("Conditions")>]`는 `Conditions`로 유지한다.
- `PropertyPanelValueSpec`는 `internal`이므로 `Ds2.LlmAgent`에서 직접 재사용하지 않는다.
- 외부 condition wire 포맷은 기존 `condition: { type, isOR, isInverted, conditions, children }`를 유지하고 `eq`/진단/문서 보강을 적용한다. `op/items`는 이번 자동 진행 범위에서 신설하지 않는다.
- LLM/YAML canonical key는 `condition`만 사용한다. `callCondition` alias parse는 추가하지 않고 문서를 `condition`으로 정정한다.
- `eq`는 단일 equality 전용 sugar로 둔다. `Multiple` / `Ranges` / bound 조건은 명시적 typed `inputSpec` 호환 포맷으로만 받는다.
- `ValueSpec` parser/formatter 공용 helper는 `Ds2.Core`로 추출하고 Editor/LlmAgent가 함께 사용한다.
- `ContactKind`는 formula projection에 표시한다. `NcContact`, `RisingPulse`, `FallingPulse`를 텍스트 수식에서 구분 가능해야 한다.
- 빈 condition은 Runtime 의미 그대로 표시한다. 빈 And는 `true`, 빈 Or는 `false`로 표시한다.
- Work context에서 top-level `AutoAux` / `ComAux`는 legacy/new 모두 fail diagnostics로 처리한다. warning-only 허용 경로는 만들지 않는다.
- 이번 작업은 Promaker YAML/LLM wire와 표시/문서만 변경하고 `Ds2.Core.Condition` 및 AASX field payload는 변경하지 않는다.

미확정 결정: 없음.

## Phase 별 명세

### Phase 0. 사전 결정 박제

- 작업 범위: `사용자 사전 결정 필요` 절의 선택지를 사용자 답변 기준으로 `박제 결정` 절에 확정값으로 기록한다.
- 산출물: 진행 표의 Phase 0 상태를 `완료`로, Phase 1 상태를 `미시작`으로 갱신한다.
- 완료 조건: 자동 진행 차단 지점이 `없음` 또는 명시된 optional gate만 남는다.

### Phase 1. Protocol foundation

- 작업 범위:
  - `ModelProtocol.parseCondition`의 top-level call condition에서 `type` 생략을 `Some AutoAux`로 보정한다.
  - condition object unknown-key whitelist를 추가한다.
  - Work context에서 허용되지 않는 top-level condition type 정책을 박제 결정대로 구현한다.
  - legacy nested child `type` 보존을 깨지 않는다.
- 수정 예상 파일:
  - `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs`
  - `Solutions/Tests/Ds2.LlmAgent.Tests/ModelProtocolTests.fs`
- 완료 조건:
  - AutoAux export -> apply round-trip test 통과.
  - unknown-key diagnostics test 통과.
  - legacy nested child type round-trip test 통과.

### Phase 2. ValueSpec / `eq`

- 작업 범위:
  - leaf object `{ ref, contactKind?, eq? }`에서 `eq`를 `ApiCall.InputSpec`로 parse한다.
  - 대상 `ApiDef` 데이터 타입 metadata 기준으로 `ValueSpec` case를 결정한다.
  - fallback 표현을 박제 결정대로 구현한다.
  - emit 시 `UndefinedValue`는 생략하고 non-default `InputSpec`은 wire에 보존한다.
- 수정 예상 파일:
  - `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs`
  - `Solutions/Tests/Ds2.LlmAgent.Tests/ModelProtocolTests.fs`
  - 공용 helper 채택 시 `Solutions/Core/Ds2.Core/<ValueSpec helper>.fs`
  - 공용 helper 채택 시 `Solutions/Core/Ds2.Core/Ds2.Core.fsproj`
  - 공용 helper 채택 시 `Solutions/Core/Ds2.Editor/Projection/PropertyPanelValueSpec.fs`
  - 공용 helper 채택 시 `Solutions/Core/Ds2.Editor/Ds2.Editor.fsproj`
- 완료 조건:
  - bool/int/float/string `eq` parse/emit tests 통과.
  - unsupported 또는 fallback ValueSpec tests 통과.
  - 기존 condition leaf string/object 입력 회귀 통과.

### Phase 3. Multi-root emit

- 작업 범위:
  - Work/Call emit에서 `Conditions.[0]`만 내보내는 제한을 제거한다.
  - 같은 `ConditionType`의 모든 top-level roots를 implicit AND로 보존한다.
  - 선택한 wire 포맷에 맞춰 canonical emit을 고정한다.
- 수정 예상 파일:
  - `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs`
  - `Solutions/Tests/Ds2.LlmAgent.Tests/ModelProtocolTests.fs`
- 완료 조건:
  - 다중 root export -> apply -> Runtime 의미 동등성 test 통과.
  - legacy parse compatibility 유지.

### Phase 4. Promaker protocol docs

- 작업 범위:
  - `condition` / `callCondition` canonical/alias 정책을 prompt와 protocol 문서에 반영한다.
  - 선택한 wire 포맷과 `eq`/fallback 문법을 문서화한다.
  - 예제 YAML을 protocol tests와 일치시킨다.
- 수정 예상 파일:
  - `Apps/Promaker/Promaker/LlmAgent/Prompts/yaml.md`
  - `Apps/Promaker/Promaker/LlmAgent/Prompts/flow.yaml`
  - `Apps/Promaker/Docs/yaml-protocol-v0.md`
- 완료 조건:
  - prompt와 protocol 문서가 같은 canonical key와 같은 문법을 안내한다.
  - 예제 condition 구문이 `ModelProtocolTests` fixture와 충돌하지 않는다.

### Phase 5. JsonFormatter docs

- 작업 범위:
  - `json-format.md`의 `isRising`을 제거하고 `isInverted`를 추가한다.
  - `children` / `isOR` / `isInverted`가 Runtime에서 평가됨을 정정한다.
  - STJ JSON `conditions`, AASX `Conditions`, stale `callConditions` 명칭을 분리한다.
  - `Builder.addCondition` 한계와 group helper 필요성을 설명한다.
- 수정 예상 파일:
  - `Solutions/Convert/Ds2.JsonFormatter/json-format.md`
  - Builder helper를 실제 추가하기로 결정한 경우 `Solutions/Convert/Ds2.JsonFormatter/*`
- 완료 조건:
  - 문서에서 ghost field `isRising`이 제거된다.
  - AASX field rename을 유도하는 문구가 없다.

### Phase 6. Editor formula projection

- 작업 범위:
  - `IsInverted`를 formula text에 표시한다.
  - `ContactKind`와 empty condition 표시 정책을 박제 결정대로 반영한다.
- 수정 예상 파일:
  - `Solutions/Core/Ds2.Editor/Projection/ConditionFormulaProjection.fs`
  - 관련 projection test 파일. 기존 test 위치 확인 후 추가한다.
- 완료 조건:
  - `not (A | B)` 표시 test 통과.
  - `ContactKind` 표시 또는 담당 경로 문서화 test 통과.
  - empty condition 표시 정책 test 통과.

### Phase 7. Apps regression

- 작업 범위:
  - Promaker model doc apply/export 회귀 확인.
  - Core/Editor condition tests 회귀 확인.
  - Core/AASX payload 변경이 없다면 다른 Apps는 smoke test 중심으로 확인한다.
  - Phase 2 검열 Minor 후속 검토: `eq` emit→re-parse 비대칭. condition leaf에 typed inputSpec으로 Single 숫자를 직접 주고 그 `ApiDef`를 참조하는 어떤 `ApiCall`도 타입 metadata가 없으면, emit은 `eq` scalar로 내보내나 re-parse가 hint 부재로 거부(잠재 data loss). 좁은 정수(Int8/Int16/UInt8/UInt16)·실수 Single의 case 손실이 원인. fallback 정책(좁은/실수 Single을 `inputSpec` raw DU로 보존 vs 현 동작 유지 + round-trip 테스트 박제)을 결정한다. 현 테스트/통상 경로에서는 미발생.
  - Phase 4 후속 cleanup: `Ds2.LlmAgent/ModelingCategory.fs` docstring(A_Modeling 분류 주석 등, 약 line 13/74)의 stale `SkipInputSensor` / `callCondition` 표기를 코드 실제와 정합한다(폐기 키 제거, `condition`/`Condition` 표기). 기능 영향 없는 주석 정정.
- 수정 예상 파일:
  - 필요 시 `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs`
  - 필요 시 `Apps/Promaker/Promaker/ViewModels/PropertyPanel/CallPanel.Conditions.cs`
  - 필요 시 관련 test files
- 완료 조건:
  - 지정 검증 명령 통과 또는 미실행 사유 기록.
  - 진행 표 모든 phase 상태가 `완료`로 갱신된다.

## 공통 규약

- 코드 변경은 기존 `ModelProtocol.parseCondition` / `emitCondition`, `ConditionTreeDto`, `ConditionExpression` 경로를 우선 재사용한다.
- `Ds2.Core.Condition`과 AASX `Conditions` payload는 사용자가 명시적으로 8번 2번을 선택하지 않는 한 변경하지 않는다.
- legacy `conditions`/`children` 입력은 backward compatibility로 유지한다.
- silent skip 대신 diagnostics를 남긴다.
- F# / C# 코드 변경 시 line ending은 LF를 유지한다.
- 문서 수정은 UTF-8로 작성한다.
- 임의 git commit/push는 하지 않는다. `--orch` 실행에서 commit이 필요한 경우 사용자 플래그 정책을 따른다.

## 검증 명령

Phase 별 기본 검증:

```powershell
dotnet test Solutions/Tests/Ds2.LlmAgent.Tests/Ds2.LlmAgent.Tests.fsproj
dotnet test Solutions/Tests/Ds2.Core.Tests/Ds2.Core.Tests.fsproj
dotnet test Solutions/Tests/Ds2.Store.Editor.Tests/Ds2.Store.Editor.Tests.fsproj
dotnet test Solutions/Tests/Promaker.Tests/Promaker.Tests.csproj
```

문서만 바꾼 phase는 build/test 생략 가능하지만, 생략 사유를 보고에 남긴다.

## 자동 진행 차단 지점

- 현재 차단: 없음.
- 외부 환경 prerequisite: 없음.
- 사용자 승인 필요: Core/AASX 저장 모델 변경을 선택하는 경우 별도 phase로 분리해야 한다.

## 작업 sub-agent prompt 골격

```text
사용자 명시 의도:
"todo-refactor-condition.md 의 Phase <N> 를 문서의 박제 결정과 공통 규약에 맞춰 구현한다."

작업 범위:
- Phase <N> 명세의 작업 범위와 수정 예상 파일만 변경한다.
- 관련 없는 파일과 Core/AASX 저장 모델은 박제 결정 없이 변경하지 않는다.

박제 결정:
- todo-refactor-condition.md 의 "박제 결정" 절을 SSOT 로 사용한다.

금지사항:
- legacy conditions/children compatibility 를 깨지 않는다.
- silent skip 을 추가하지 않는다.
- 임의 commit/push 하지 않는다.

검증:
- Phase 명세의 완료 조건과 검증 명령을 수행한다.
- 실패 시 원인과 미완료 항목을 보고한다.

보고 형식:
- 변경 파일
- 구현 내용
- 실행한 검증 명령과 결과
- 남은 위험
```

## 검열 sub-agent prompt 골격

```text
검열 대상:
- 현재 git diff 중 Phase <N> 변경 범위.

검증 항목 0:
- 사용자 명시 의도 verbatim 인용 + patch 와 의도 1:1 매핑. 의도 항목 중 누락된 것이 있으면 Critical.

검토 항목:
- 논리 오류, data loss, legacy compatibility regression
- Runtime 의미와 protocol emit/parse 동등성
- diagnostics 누락 또는 silent skip
- 불필요한 신규 모델/중복 helper
- 테스트 누락

금지사항:
- 파일 수정, git 명령, commit 금지.

보고 형식:
- 검열 대상 파일과 변경 범위
- Critical / Major / Minor 발견 사항
- 수정 권장 사항
- 잔여 우려
```

## 보고 형식

각 phase 완료 보고는 다음 형식을 따른다.

- Phase 번호와 이름
- 변경 파일 목록
- 완료 조건 충족 여부
- 실행한 검증 명령
- 실패 또는 생략한 검증과 사유
- 다음 phase 진입 가능 여부

## 현재 코드 기준

- 저장 모델: `Solutions/Core/Ds2.Core/Entities.fs`
  - `Condition`은 `ApiCalls`, `Children`, `IsOR`, `IsInverted`, `Type`을 가진다.
  - `Work.Conditions`와 `Call.Conditions`가 같은 `Condition` 타입을 공유한다.
- 편집 DTO/API: `Solutions/Core/Ds2.Editor/Store/Panel/Panel.Condition.fs`
  - `ConditionTreeDto`도 `ApiCallIds`와 `Children`을 분리한다.
  - `ReplaceCallConditionTree` / `ReplaceWorkConditionTree`가 DTO를 기존 `Condition`으로 변환한다.
- 표시: `Solutions/Core/Ds2.Editor/Projection/ConditionFormulaProjection.fs`
  - `items`와 `children`을 다시 합쳐 수식 문자열을 만든다.
- 런타임: `Solutions/Runtime/Ds2.Runtime/Engine/Core/SimIndex/Types.fs`
  - 이미 `ConditionExpression = Const | Leaf | And | Or | Not` 형태가 존재한다.
- 인덱싱: `Solutions/Runtime/Ds2.Runtime/Engine/Core/SimIndex/Build.fs`
  - 기존 저장 모델의 `ApiCalls + Children`을 `ConditionExpression`으로 변환한다.
- LLM/YAML 프로토콜: `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs`
  - 현재 `condition.conditions[]`는 leaf, `condition.children[]`는 nested condition으로 파싱/emit한다.
  - 이미 leaf dual-format(string scalar 또는 object `{ ref, contactKind? }`), `isInverted` parse/emit, `ContactKind` parse, non-array/invalid leaf diagnostics를 지원한다.
  - 현재 leaf object는 `ref` / `contactKind`만 읽고 쓴다. Runtime은 `InputSpec`를 평가에 사용하므로 새 leaf 포맷의 기대값 지원은 parser/emit/test 범위에 반드시 포함해야 한다.
  - 현재 emit은 `AutoAux` 타입을 생략하지만 parse는 타입 키가 없을 때 `Condition.Type = None`으로 둔다. Runtime은 `Type = Some AutoAux`만 평가하므로 export -> apply round-trip 후 `AutoAux` 조건이 무시될 수 있다.
  - 현재 Work/Call emit은 `Conditions.[0]`만 내보내므로 top-level condition root가 여러 개인 경우 data loss 위험이 있다.
  - 현재 call object의 실제 입력 키는 `condition`이다. `yaml-protocol-v0.md`에는 `callCondition` 설명이 남아 있으나, 이번 작업에서는 alias를 추가하지 않고 문서를 `condition`으로 정정한다.
  - `parseCondition`은 `conditions`/`children` 배열 및 leaf shape diagnostics는 갖고 있지만, condition object 자체의 unknown-key whitelist는 없다. 기존 포맷 키 오타와 `op/items` 채택 시 legacy/new 혼용을 diagnostics로 막는 작업이 필요하다.
- Promaker 작성 지침: `Apps/Promaker/Promaker/LlmAgent/Prompts/yaml.md`, `Apps/Promaker/Docs/yaml-protocol-v0.md`
  - 현재 `condition: { type, conditions }` 형태를 직접 안내한다. 외부 작성 포맷 개선 범위에 두 문서/프롬프트 갱신을 포함한다.
- Promaker 앱 UI/API: `Apps/Promaker/Promaker/Dialogs/Condition`, `Apps/Promaker/Promaker/ViewModels/PropertyPanel`, `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs`
  - Condition editor, property panel, MCP `apply_model_doc` / `export_model_doc`, YAML open/save 경로가 직접 영향권이다.
- JSON 포맷 문서: `Solutions/Convert/Ds2.JsonFormatter/json-format.md`
  - `callConditions`, `conditions`, `children`, `isOR`, `isRising` 설명이 있다.
  - 현재 Runtime 코드와 다르게 `children/isOR`가 시뮬레이션에서 평가되지 않는다고 설명하므로 정정 필요하다.
  - `Condition`에는 `isRising` 필드가 없고 실제 필드는 `isInverted`다.

## 진척 기준선

2차 리뷰 기준으로, 이 작업은 greenfield 신규 설계가 아니다. 현재 구현은 이미 다음 기능을 갖고 있다.

- `ModelProtocol.parseCondition`은 legacy `conditions`/`children` 트리, leaf string/object dual-format, `isOR`, `isInverted`, `contactKind`, 잘못된 배열/leaf 형태 diagnostics를 처리한다.
- `ModelProtocol.emitCondition`은 legacy `conditions`/`children`, `isOR`, `isInverted`, `contactKind`를 출력한다.
- Runtime은 `Condition`을 `ConditionExpression = Const | Leaf | And | Or | Not`로 변환하고 `children` / `isOR` / `isInverted`를 실제 평가한다.
- Editor panel에는 `ConditionTreeDto`가 있고, `ApiCallKinds`, `RawSymbols`, `RawSymbolKinds`, `Children`, `IsInverted`를 이미 담는다.

따라서 남은 핵심은 다음 다섯 가지다.

1. 선행 round-trip 버그를 먼저 막는다. 특히 `type` 생략을 top-level call condition에서 `AutoAux`로 보정해야 한다.
2. 외부 작성 모양은 기존 `conditions`/`children` 유지 + 문서/표현 보강으로 고정한다.
3. 조건 leaf 기대값(`ApiCall.InputSpec`)의 wire 표현을 추가한다.
4. stale 문서(`json-format.md`, Promaker prompt, `yaml-protocol-v0.md`)를 현재 구현과 맞춘다.
5. condition object의 unknown key, legacy/new 혼용, 빈/inert 결과 정규화 정책을 테스트 가능한 규칙으로 고정한다.

## Apps 영향 검토

`Apps` 하부의 직접 포맷 소비자는 Promaker에 집중되어 있다. 다만 다른 앱들도 `DsStore`, AASX, Runtime을 간접 소비하므로 저장 모델이나 AASX 직렬화까지 바꾸면 영향권에 들어온다.

- `Apps/Promaker`
  - 직접 영향. YAML prompt, `yaml-protocol-v0.md`, `ModelTools.ApplyModelDoc` / `ExportModelDoc`, YAML open/save, Condition dialog, PropertyPanel, 3D 조건 indicator, release/help 문서가 모두 점검 대상이다.
  - `Promaker/LlmAgent/Prompts/flow.yaml` 같은 예제 prompt도 `condition: { type, conditions }` 구문을 다수 포함하므로 갱신 대상이다.
- `Apps/Shared`
  - 직접 condition 포맷 처리는 없다. 하지만 `Llm.Shared`가 `Ds2.LlmAgent`, `LlmTurnContext`, model doc yaml 축적을 공유하므로 Promaker 외 앱이 같은 LLM shared layer를 재사용하면 선택한 포맷 안내/검증 메시지 영향을 받을 수 있다.
- `Apps/CostSim`
  - condition 포맷을 직접 다루는 코드는 검색되지 않았다. 그러나 `DsStore`를 로드/저장하고 `Ds2.Aasx`를 참조하므로 Core `Condition` 저장 구조나 AASX import/export를 변경하면 회귀 대상이다.
- `Apps/DSPilot`
  - condition 포맷 직접 참조는 검색되지 않았다. 하지만 `DSPilot`, `DSPilot.AasxSimulator`, `PlcDataGenerator`가 AASX를 읽어 `DsStore`를 구성하고 Runtime/Editor/Core를 참조한다. AASX 내 Condition JSON 표현 또는 Runtime 평가 의미 변경 시 회귀 대상이다.
- `Apps/AasxEditor`
  - 직접 condition 포맷 참조는 검색되지 않았다. `Ds2.Aasx` 기반 AASX 편집/변환 앱이므로 AASX graph import/export의 `ResizeArray<Condition>` JSON payload가 바뀌면 확인 대상이다.
- `Apps/Tutorial`
  - 직접 condition 편집은 없다. 다만 `DsStore.SaveToFile` / `LoadFromFile`, AASX round-trip, Runtime 예제를 포함하므로 저장 포맷 호환성 smoke test 대상이다.

결론: `op/items` 같은 wire 변경을 채택하더라도 Promaker 전용 YAML 프로토콜에서만 처리하면 다른 앱의 직접 변경은 작다. 반대로 `Ds2.Core.Condition` 타입, `DsStore` JSON 직렬화, `Ds2.Aasx` Condition payload를 바꾸면 `CostSim`, `DSPilot`, `AasxEditor`, `Tutorial`까지 회귀 범위가 확장된다.

## 권장 방향

내부 `Ds2.Core.Condition` 저장 모델을 즉시 갈아엎지 않는다. 또한 별도 wire DTO를 무조건 새로 만들지 않는다. 우선 기존 `ModelProtocol.parseCondition` / `emitCondition`, `ConditionTreeDto`, `ConditionExpression` 변환 경로를 재사용하고, 필요한 경우에만 얇은 변환 helper를 추가한다.

결정 게이트의 기본안은 기존 `conditions`/`children` 포맷 유지 + `eq`/문서/표시/진단 보강이다. 이 경로는 이미 구현된 parser/emit, diagnostics, round-trip 테스트를 가장 많이 재사용한다.

`op/items`는 사람이 읽기 쉬운 후보였지만 이번 자동 진행 범위에서는 채택하지 않는다. 기존 `conditions`/`children` 유지 + `eq`/문서/표시/진단 보강을 구현한다.

- 기존 포맷 유지 + `inputSpec`/문서/표시 보강: 구현량이 작고 기존 diagnostics/round-trip 재사용 가능.
- `op/items` 신설: leaf와 group이 한 배열에 들어가 작성 모양은 단순하지만, legacy 포맷과 `op/items` 포맷을 동시에 유지해야 한다.

`op/items`를 채택할 경우의 외부 표현 예:

```json
"condition": {
  "comAux": {
    "op": "or",
    "items": [
      { "ref": "Cyl1.RET", "contactKind": "RisingPulse" },
      {
        "op": "and",
        "items": [
          { "ref": "Cyl1.ADV" },
          {
            "ref": "Sensor.Ready",
            "eq": true
          }
        ]
      }
    ]
  }
}
```

공통 의미 규칙과 `op/items` 후보 규칙:

- leaf: `{ "ref": "...", "contactKind"?, "eq"? }`를 우선 검토한다. `eq`는 `ApiCall.InputSpec`로 정규화되는 사람 친화 sugar다.
- `eq` 값의 `ValueSpec` case는 JSON token만으로 추론하지 않는다. 대상 `ApiDef`의 데이터 타입 metadata를 기준으로 `BoolValue`, 정수/실수/문자열 계열을 결정해야 한다.
- `eq`는 단일 equality sugar다. `ValueSpec.Multiple`, `ValueSpec.Ranges`, bound 포함 비교 조건은 `eq`만으로 표현할 수 없으므로 명시적 typed `inputSpec` 호환 포맷으로만 받는다.
- raw `inputSpec` DU(`{"Case":"BoolValue",...}`) 직접 노출은 저수준 호환 옵션으로만 검토한다. 기본 작성 문법으로 삼으면 "사람이 읽기 쉬운 포맷" 목표와 충돌한다.
- NOT은 기존 `Condition.IsInverted`에 대응되는 노드 플래그(`isInverted: true`)를 재사용한다. 별도 `{ "op": "not", "item": ... }` 노드는 wrapper 합성이 필요해 복잡도만 늘리므로 사용하지 않는다.
- 같은 조건 종류의 기존 top-level `Condition` root가 여러 개 있으면 의미는 implicit AND다. emit 시에는 모든 root를 보존해야 하며, `op/items` 채택 시 하나의 `{ "op": "and", "items": [...] }`로 접는다.
- Work 조건은 `skipAction`만 허용한다. Work context에서 top-level `AutoAux` / `ComAux`가 들어오면 legacy/new 모두 fail diagnostics로 처리한다.
- legacy `children` 입력은 현재 테스트가 child의 explicit `type` 보존을 요구한다. child `type` 금지는 새 `op/items` 포맷에만 적용하거나, legacy까지 금지하려면 별도 migration/compatibility 결정을 먼저 내린다.
- `op/items` 채택 시 `op`는 `"and"` 또는 `"or"`이고, `items`는 leaf 또는 group을 동일 배열에 담는다.
- `op/items` 채택 시 group은 `{ "op": "...", "items": [...] }`로 표현하고, 조건 종류는 child에 섞지 않고 root에서 `autoAux`, `comAux`, `skipAction`처럼 분리한다.

## 남은 작업

1. 선행 버그 수정: `AutoAux` 기본 타입 round-trip
   - `emitCondition`이 `AutoAux`의 `type` 키를 생략하므로, top-level call condition parse에서 `type` 키가 없으면 `Some AutoAux`로 보정한다.
   - legacy child condition은 기존 호환을 우선한다. child의 `type` 생략은 `None` 유지, child의 explicit `type`은 현재 round-trip 테스트처럼 보존하는 정책을 기본안으로 둔다.
   - export -> apply 후 `AutoAux` 조건이 Runtime `CallAutoAuxConditions` 평가 대상에 남는 테스트를 추가한다.
   - `type` 생략 보정은 Work `skipAction` 기본값과 혼동하지 않도록 call/work context를 분리해 명시한다.

2. 외부 포맷 결정 박제
   - 완료된 결정: 기존 `condition: { type, isOR, isInverted, conditions, children }` 포맷을 유지하고 `eq`, diagnostics, 문서, 표시를 보강한다.
   - `condition.autoAux`, `condition.comAux`, `condition.skipAction` + `op/items` 포맷은 이번 자동 진행 범위에서 신설하지 않는다.
   - `op/items`는 장기 후보로만 남긴다. 향후 신설하려면 legacy/new wire dual 유지 비용, 타입 안전성 저하, 모델 중복 비용을 별도 phase에서 다시 검토한다.
   - 다중 root 정책: store의 같은 `ConditionType` top-level roots는 어떤 wire 포맷을 선택하더라도 implicit AND로 보존한다.

3. `Ds2.LlmAgent.ModelProtocol` 파서 확장
   - 기존 `conditions`/`children` 포맷은 backward compatibility로 유지한다.
   - 기존 `parseCondition`을 재작성하지 말고 확장한다.
   - leaf object의 기대값 sugar(`eq`)를 파싱해 조건 내부 `ApiCall.InputSpec`에 반영한다.
   - `eq` 값의 `ValueSpec` 타입은 대상 `ApiDef`의 데이터 타입 metadata로 결정한다. 숫자 JSON token만 보고 `Int32` / `Float64`를 임의 선택하지 않는다.
   - `eq`로 표현할 수 없는 `Multiple` / `Ranges` / bound 비교 조건은 명시적 typed `inputSpec` 호환 포맷으로 받는다.
   - `PropertyPanelValueSpec`는 `Ds2.Editor`의 `internal` 모듈이고 `Ds2.LlmAgent`가 friend assembly가 아니므로 직접 재사용하지 않는다. 공용 ValueSpec text parser/formatter를 `Ds2.Core`로 추출한다.
   - Work context에서는 `skipAction`만 허용하고 `AutoAux` / `ComAux`는 fail diagnostics로 막는다.
   - condition object 허용 키 whitelist를 둔다. legacy 허용 키는 `type`, `isOR`, `isInverted`, `conditions`, `children`이며, `op/items` 채택 시 root 키와 expression 키를 별도로 whitelist한다.
   - `op/items`는 이번 범위에서 신설하지 않는다. 향후 신설 시 legacy `conditions`/`children`과 새 `op/items` 표현이 한 object 안에서 섞이면 diagnostics로 막는다.
   - 선택한 wire 포맷에서 빈/inert condition이 생기는 입력은 `None` 정규화 또는 diagnostics 중 하나로 고정하고 테스트한다.
   - 잘못된 타입은 silent skip하지 말고 기존 diagnostics 패턴을 재사용해 보고한다.
   - 재귀 깊이 상한은 기존 parser 일반 hardening으로 분류한다. `op/items` 채택의 필수 선행 작업은 아니며, 적용한다면 권장 상한은 32~64이고 초과 시 StackOverflowException이 아니라 diagnostics로 실패시킨다.

4. `Ds2.LlmAgent.ModelProtocol` emit 변경
   - 결정 게이트 결과를 따른다. 기존 포맷 유지 시 legacy 포맷을 계속 emit하되 `eq`/다중 root/data loss 문제를 보강한다.
   - 기존 `conditions`/`children` 포맷을 canonical emit으로 유지한다.
   - `Conditions.[0]`만 emit하지 않는다. 같은 `ConditionType`의 모든 top-level root를 수집해 implicit AND 표현으로 emit한다.
   - leaf object의 기대값 sugar(`eq`)를 emit한다. default `UndefinedValue`는 생략 가능하게 한다.
   - `op/items` emit은 이번 범위에서 추가하지 않는다.

5. Promaker 작성 지침 갱신
   - 기존 포맷 유지 결정에 맞춰 `condition: { type, conditions, children, isOR, isInverted }`에 `eq`, typed `inputSpec`, diagnostics 규칙을 보강한다.
   - `Apps/Promaker/Promaker/LlmAgent/Prompts/flow.yaml`의 예제 condition 구문도 함께 갱신한다.
   - `Apps/Promaker/Docs/yaml-protocol-v0.md`의 condition/callCondition 섹션을 `condition` canonical 기준으로 갱신한다.
   - prompt와 프로토콜 문서가 서로 다른 키(`condition`, `callCondition`)를 안내하지 않도록 명칭을 정리한다. 현재 코드의 실제 키는 `condition`이므로 기본안은 문서를 `condition`으로 정정하는 것이다.
   - `callCondition` alias parse는 추가하지 않는다. `callCondition` 입력은 unknown-key diagnostics가 나는 테스트를 둔다.

6. Promaker UI/API 영향 반영
   - `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs`의 `ApplyModelDoc` / `ExportModelDoc` 경로에서 선택한 포맷의 diagnostics와 emit 결과가 UI에 자연스럽게 표시되는지 확인한다.
   - `Apps/Promaker/Promaker/Dialogs/Condition`의 ladder editor 저장 경로가 `ConditionTreeDto`의 `RawSymbols`, `RawSymbolKinds`, `ApiCallKinds`, `IsInverted`를 계속 보존하는지 확인한다.
   - `Apps/Promaker/Promaker/ViewModels/PropertyPanel/CallPanel.Conditions.cs`의 조건 leaf `InputSpec` 편집 값이 선택한 wire emit/apply와 의미적으로 일치하는지 확인한다.

7. `Ds2.JsonFormatter` 문서와 Builder 개선
   - `json-format.md`의 `CallCondition` 스키마와 `isOR / isRising / children` 섹션을 현재 코드 기준으로 정정한다.
   - `isRising` 표기는 제거하고 `isInverted`를 추가한다. 상승/하강 엣지는 leaf의 `ContactKind.RisingPulse/FallingPulse`로 설명한다.
   - `children` / `isOR` / `isInverted`가 Runtime에서 모두 평가된다는 점을 반영한다.
   - `callConditions` 명칭을 실제 `Conditions` camelCase 직렬화명인 `conditions`로 바꿀지 결정하기 전에 `JsonPropertyName` attribute 부재와 실제 저장 JSON을 확인한다.
   - AASX 경로명과 STJ JSON 명칭을 분리해서 설명한다. `Work.Conditions` / `Call.Conditions`에는 `[<AasxField("Conditions")>]`가 붙어 있으므로 AASX field 명칭은 `Conditions` 유지가 기본이고, 정정 대상은 STJ JSON 문서명(`conditions`)과 stale 문서명(`callConditions`)이다.
   - `Builder.addCondition`은 flat leaf만 지원하므로 group/중첩을 쓰려면 `Builder.addConditionExpr` 또는 동등 helper가 필수다.

8. Editor 표시 경로 개선
   - `Solutions/Core/Ds2.Editor/Projection/ConditionFormulaProjection.fs`가 `IsInverted`를 표시하도록 수정한다.
   - `ConditionFormulaProjection.formatApiCallItem`이 현재 `OutputSpecText`만 표시하고 `ContactKind`를 표시하지 않는 점을 보강한다. Rising/Falling/NcContact를 수식 표기에 반영한다.
   - 빈 condition은 Runtime 의미 그대로 표시한다. 빈 And = true, 빈 Or = false 표시 정책을 적용한다.
   - `isInverted: true`가 UI 수식에서 `!(...)` 또는 동등한 표기로 보이는지 projection test를 추가한다.
   - `ConditionTreeDto`의 `RawSymbols`, `RawSymbolKinds`, `ApiCallKinds`, `ContactKind.Inverter` placeholder leaf가 선택한 wire 표현에서 손실 없이 round-trip 되는지 확인한다.

9. Apps 회귀 범위
   - Promaker: `ModelProtocolTests`, YAML IO tests, Condition dialog/property panel 관련 tests, prompt canary/drift tests를 갱신한다.
   - Shared: `Llm.Shared`는 직접 condition 포맷 테스트보다 Promaker model doc yaml 축적/표시가 깨지지 않는지 확인한다.
   - CostSim: `DsStore` JSON/AASX load/save smoke test를 실행한다. Core/AASX payload를 바꾸지 않는다면 별도 기능 변경은 없어야 한다.
   - DSPilot: AASX reload/import 및 Runtime smoke test를 확인한다. Condition payload 또는 Runtime 평가를 바꾸는 경우 `DSPilot.AasxSimulator` / `PlcDataGenerator`까지 포함한다.
   - AasxEditor: AASX import/export payload 변경 시 AASX JSON editor smoke test를 포함한다.
   - Tutorial: `Step04_SaveLoad`, `Step08_ConvertCli` 성격의 save/load/AASX round-trip smoke test를 포함한다.

10. 테스트 추가
   - `A & (B | C)`, `(A & B) | (C & D)`, `not (A | B)` round-trip.
   - NOT 중첩/이중부정 round-trip 및 Runtime 평가.
   - leaf 기대값 sugar(`eq`) parse/emit/round-trip 및 Runtime 평가.
   - `eq` 값의 타입이 대상 `ApiDef` 데이터 타입 기준으로 `ValueSpec`에 매핑되는지 확인한다. 특히 숫자 literal이 잘못된 정수/실수 case로 고정되지 않아야 한다.
   - `eq`로 표현할 수 없는 `Multiple` / `Ranges` / bound 비교 조건의 fallback parse/emit 정책을 테스트한다.
   - `ContactKind` 5종(`NoContact`, `NcContact`, `RisingPulse`, `FallingPulse`, `Inverter`) 보존과 Runtime 평가.
   - `ContactKind` formula projection 표시 테스트를 추가한다.
   - 조건 type 3종(`AutoAux`, `ComAux`, `SkipAction`)의 default 생략/emit 규칙.
   - top-level call condition에서 `type` 생략 입력이 `AutoAux`로 보정되고 Runtime 평가 대상에 포함되는지 확인.
   - 빈 `and`/`or` 의미와 diagnostics/표시 정책. Runtime 기준 빈 And는 `true`, 빈 Or는 `false`로 projection에 표시되는지 확인한다.
   - condition object unknown key diagnostics를 확인한다.
   - `op/items`는 이번 범위에서 신설하지 않으므로 legacy/new format 혼용 테스트는 장기 후보 항목으로만 남긴다.
   - `condition` / `callCondition` 명칭 정책 테스트. `condition`만 canonical로 parse/emit하고, `callCondition` 입력은 명확히 실패해야 한다.
   - 같은 `ConditionType`의 top-level root 여러 개가 선택한 wire 포맷에서 implicit AND로 보존되는지 확인.
   - Work context의 `type: AutoAux` / `type: ComAux` 입력이 fail diagnostics가 되는지 테스트한다.
   - legacy `children` 입력에서 explicit child `type`이 현재처럼 보존되는지 확인한다. `op/items` child type diagnostics는 이번 범위가 아니라 장기 후보 항목으로 남긴다.
   - 기존 `conditions`/`children` 입력이 계속 동작하는지 회귀 테스트.
   - Runtime 평가가 기존 `ConditionExpression` 결과와 동일한지 확인.
   - `IsInverted` 표시 projection test.

11. 장기 검토
   - 외부 포맷이 안정된 뒤 `Ds2.Core.Condition` 자체를 `ConditionExpression` 유사 모델로 정리할지 검토한다.
   - 이 단계는 AASX import/export, Editor panel, LLM protocol, Runtime SimIndex까지 영향이 크므로 별도 phase로 진행한다.

## 주의 사항

- `DsStore.RebuildApiCallsDictionary`는 조건 내부 `ApiCall`을 전역 `ApiCalls` dictionary에 등록하지 않는다. 새 외부 포맷에서도 조건 leaf는 독립 `ApiCall` 객체처럼 보이게 하지 말고, `ref + 기대값/contactKind`로 보이게 하는 편이 안전하다.
- `Condition.Type`은 top-level condition에서 의미가 있고, 현재 child condition은 `Type = None`으로 생성되는 경로가 있다.
- `op/items` 포맷 변환기는 child `Type = None` 불변식을 유지해야 한다. legacy `children` 입력은 기존 테스트가 explicit child `Type` 보존을 요구하므로, `op/items` 진단 정책과 legacy 호환 정책을 분리한다.
- `Work.Conditions`는 현재 주석상 `SkipAction`만 의미가 있다. Work용 wire 포맷도 이 제약을 문서화해야 한다.
- `json-format.md`에는 `callConditions`라는 명칭이 남아 있지만 실제 Core 엔티티 속성명은 `Conditions`이며 camelCase 직렬화 시 `conditions`다. AASX field 명칭은 `[<AasxField("Conditions")>]` 기준 `Conditions`이므로 STJ JSON 문서명과 AASX 명칭을 섞어 바꾸지 않는다.
- 빈 condition은 Runtime 의미 그대로 표시한다. projection에서 빈 And는 `true`, 빈 Or는 `false`로 보이도록 문서/테스트에서 명시한다.
- Apps 하부 다른 앱은 Promaker YAML wire에는 직접 의존하지 않지만, Core/AASX 저장 모델에는 간접 의존한다. wire-only 변경과 Core/AASX 변경의 회귀 범위를 분리해서 진행한다.

## 1차 리뷰 반영 내역

- leaf `inputSpec`는 제거하지 않고 공식 지원 대상으로 유지한다. 다만 현재 `ModelProtocol`은 `ref` / `contactKind`만 처리하므로 parse/emit/test 작업을 명시했다.
- top-level 다중 root는 implicit AND로 고정한다. 현재 emit의 `Conditions.[0]` 제한은 data loss 위험이 있으므로 모든 root를 수집해 새 표현식 하나로 접는 작업을 추가했다.
- Work 조건은 `skipAction`만 허용한다. 현재 parser가 Call과 같은 `parseCondition`을 재사용하는 문제를 diagnostics 오류와 테스트로 막도록 추가했다.
- Promaker prompt와 `yaml-protocol-v0.md` 갱신을 작업 범위에 추가했다.
- NOT 표현 검토 항목을 추가했다.
- `IsInverted`가 현재 formula projection에 표시되지 않는 문제를 Editor 표시 작업과 projection test로 추가했다.

## 2차 리뷰 반영 내역

- 2차 리뷰가 1차 반영 전 기준이라는 점을 감안해, 이미 반영된 `inputSpec`, 다중 root, Work `skipAction` 제한, prompt/protocol 문서 갱신 항목은 유지했다.
- `ModelProtocol`이 이미 leaf dual-format, `isInverted`, `ContactKind`, diagnostics를 지원한다는 진척 기준선을 추가했다. 남은 작업을 신규 파서 작성이 아니라 기존 `parseCondition` / `emitCondition` 확장으로 재정의했다.
- `op/items`는 확정 권장안에서 검토 후보로 낮췄다. 기존 `conditions`/`children` 확장안과 비교하고, 신설 시 모델 중복 비용을 명시하도록 했다.
- leaf 기대값은 raw `inputSpec` DU 직접 노출보다 `eq` sugar를 우선 검토하도록 바꿨다. anchor1 이후 확인 결과 `PropertyPanelValueSpec` 직접 재사용은 불가능하므로, 공용 helper 추출 또는 `Ds2.LlmAgent` 전용 parser를 별도 검토한다.
- NOT 표현은 1차 반영의 `{ "op": "not", "item": ... }`에서 기존 `IsInverted`에 맞춘 `isInverted: true` 노드 플래그로 되돌렸다.
- `json-format.md` 정정 범위를 구체화했다. `isRising` 제거, `isInverted` 추가, `children` / `isOR` / `isInverted` Runtime 평가 반영, `callConditions` 명칭 검증을 포함한다.
- 테스트 축을 확장했다. NOT 중첩/이중부정, `ContactKind` 5종, 기대값 sugar, condition type default emit, 빈 `and`/`or`, unknown op/items diagnostics를 포함한다. 재귀 깊이 가드는 새 포맷 필수 작업이 아니라 기존 parser 일반 hardening으로 낮췄다.
- `ConditionTreeDto`의 `RawSymbols`, `RawSymbolKinds`, `ApiCallKinds`, `ContactKind.Inverter` placeholder leaf가 선택한 wire 표현에서 손실되지 않도록 영향 항목을 추가했다.

## Apps 영향 검토 반영 내역

- `Apps` 하부 앱별 직접/간접 영향 범위를 추가했다.
- Promaker 내부 UI/API 영향(`ModelTools`, Condition dialog, PropertyPanel, prompt 예제)을 별도 작업으로 분리했다.
- `CostSim`, `DSPilot`, `AasxEditor`, `Tutorial`은 Promaker YAML wire 직접 소비자는 아니지만 `DsStore`/AASX/Runtime 간접 소비자이므로 Core 또는 AASX payload 변경 시 회귀 대상임을 명시했다.
- wire-only 변경과 Core/AASX 저장 모델 변경의 회귀 범위를 분리하도록 주의 사항을 추가했다.

## anchor1 이후 리뷰 반영 내역

- 사용자가 지정한 anchor1 기준 이후 리뷰를 반영했다.
- `AutoAux` 기본 타입 round-trip 손실을 선행 버그 수정 항목으로 추가했다. 현재 emit은 `AutoAux` type을 생략하고 parse는 누락 type을 `None`으로 두므로 Runtime 평가 대상에서 빠질 수 있다.
- child `type` 진단 정책을 새 `op/items` 포맷과 legacy `children` 포맷으로 분리했다. 현재 legacy nested condition 테스트는 explicit child `type: SkipAction` 보존을 요구하므로, legacy 입력까지 즉시 금지하면 호환 테스트와 충돌한다.
- `condition` / `callCondition` 명칭 정리를 테스트 가능한 결정 항목으로 바꿨다. 현재 코드의 실제 키는 `condition`이며, `callCondition`은 alias 지원 여부를 먼저 결정해야 한다.
- condition object unknown-key whitelist, legacy/new 혼용 금지, 빈/inert 결과 정규화 테스트를 파서 작업과 테스트 축에 추가했다.
- `eq` sugar 구현 시 `PropertyPanelValueSpec` 직접 재사용이 불가능하다는 점을 반영했다. 공용 ValueSpec helper를 `Ds2.Core`로 추출하거나 `Ds2.LlmAgent` 전용 parser를 두는 방향으로 정정했다.

## anchor1 기준 추가 리뷰 반영 내역

- `op/items`가 미결 후보인데 후속 작업이 확정처럼 쓰인 모순을 정리했다. 외부 포맷 결정을 선행 게이트로 승격하고, 기본안은 기존 `conditions`/`children` 포맷 유지 + `eq`/문서/표시/진단 보강으로 두었다.
- `eq` sugar의 `ValueSpec` 타입 결정 규칙을 추가했다. JSON token만으로 정하지 않고 대상 `ApiDef` 데이터 타입 metadata를 기준으로 결정하며, 범위/다중값/bound 비교는 별도 fallback 정책이 필요하다고 명시했다.
- NOT 표현 변경 이력을 1차/2차 리뷰 반영 내역에서 중복 서술하지 않도록 시간선을 정리했다. 최종 결정은 기존 `IsInverted`와 맞는 `isInverted: true` 유지다.
- `ConditionFormulaProjection`의 `ContactKind` 미표시와 빈 condition `(empty)` 표시가 Runtime 의미와 어긋나 보일 수 있는 문제를 Editor 표시 작업과 테스트 확인 항목에 추가했다.
- `callConditions` / `conditions` 문서명 정정과 AASX `[<AasxField("Conditions")>]` 경로명을 구분하도록 `Ds2.JsonFormatter` 작업과 주의 사항을 보강했다.
- 재귀 깊이 상한은 새 포맷 핵심 작업이 아니라 기존 parser 일반 hardening으로 재분류했다.
