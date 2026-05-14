<!-- align:center -->

# 대화형 LLM 기반 자연어 공정 기술의 DS 모델 변환 및 YAML/AASX/PLC 산출 사례

홍길동¹, Dualsoft Inc.

¹anonymous@dualsoft.com

<!-- align:left -->

## 초록 (Abstract)

산업 자동화 현장에서 공정 설계는 전통적으로 도면, 표, 사양서 등 비정형 문서에서 시작하여 모델링 도구로 옮기는 과정에 다수의 수작업 단계가 존재한다. 본 논문은 대화형 LLM(Large Language Model)을 프런트엔드로 두어 사용자가 자연어로 공정 흐름을 기술하면, 이를 자체 정의한 고수준 DSL인 DS(Dualsoft) 언어 그래프 모델로 자동 변환하고, 사람이 읽기 쉬운 YAML 모델 문서, 산업용 표준 교환 포맷인 AASX(Asset Administration Shell eXchange) 파일, LS XGI PLC 프로젝트 XML로 동시 산출하는 Promaker 시스템의 사용 사례를 보고한다. 시스템은 MCP(Model Context Protocol)를 통해 LLM에 도구 호출을 노출하며, 그래프 편집을 다수의 미시 연산으로 분해하던 초기 설계로부터 발전하여, 현재는 LLM이 한 차례의 선언적 YAML 문서를 발행하면 백엔드가 이를 단일 트랜잭션으로 적용하는 doc-level 진입점을 주력으로 한다. 본 사례 연구를 통해, 대화 단위의 자연어 입력만으로 사람이 검토 가능한 YAML, 표준 AAS 자산, 실 PLC에서 동작하는 사다리 논리(Ladder Logic)까지 동일한 그래프 출처로부터 도달할 수 있음을 보인다.

**키워드**: LLM, MCP, DSL, YAML, AAS, AASX, PLC, 산업 자동화, 모델 기반 엔지니어링

<!-- page-break -->
<!-- page-number-reset -->

## 1. 서론

### 1.1 배경

스마트 팩토리와 Industry 4.0 흐름 속에서 공정 모델을 표준화된 디지털 자산으로 표현하려는 노력은 AAS(Asset Administration Shell)와 같은 국제 표준화로 이어졌다. 그러나 현장 엔지니어가 실제로 사용하는 입력 매체는 여전히 자연어 사양서, 도면 주석, 회의록과 같은 비정형 자료이며, 이를 정형 모델로 옮기는 단계는 대부분 사람이 GUI 도구를 클릭하여 수행한다. 한편 최근의 대화형 LLM은 비정형 입력의 의도를 구조화된 형식으로 변환하는 능력을 보이며, 이는 모델 빌더의 백엔드와 결합될 경우 상기 수작업의 큰 부분을 자동화할 잠재력을 갖는다.

### 1.2 문제 정의

자연어 → 정형 공정 모델 → 산업 표준 산출물의 파이프라인을 구성할 때 다음 세 가지 실무 제약이 존재한다.

1. **의미 정확성**: LLM이 생성한 모델이 도메인 의미론(예: 동일 디바이스의 상반된 액션 간 상호 배타성)을 위반하면 안 된다.
2. **트랜잭션 일관성**: 한 번의 사용자 발화가 다수의 그래프 편집 연산으로 분해될 때, 부분 실패가 영구 손상을 남기면 안 된다.
3. **표준 호환성과 가독성**: 산출물은 AAS 3.1 환경 및 상용 PLC 엔지니어링 도구(LS XG5000 등)에서 그대로 열려야 하고, 동시에 사람이 검토·diff·revert 할 수 있는 텍스트 형태로도 보존되어야 한다.

### 1.3 기여

본 논문은 위 세 가지 제약을 만족하는 실제 동작하는 시스템인 Promaker를 다음과 같이 정리하여 보고한다.

- 자연어 → DS 그래프 모델 → YAML/AASX/PLC 종단(end-to-end) 파이프라인을 단일 데스크톱 애플리케이션 안에 통합한 사례 제시
- LLM이 자유 형식 텍스트로 그래프를 직접 출력하지 않고, 선언적 YAML 모델 문서를 doc-level MCP 도구로 단발 발행하는 trust boundary 설계
- 동일 그래프로부터 IDTA 표준 Submodel(Nameplate, TechnicalData, HandoverDocumentation)을 포함한 AAS 3.1 자산, LS XGI PLC 프로젝트 XML, 그리고 사람이 읽고 편집 가능한 YAML SSOT를 동시에 산출하는 구체 매핑 보고

## 2. 관련 연구 및 시스템

산업 도메인에서 LLM을 적용한 사전 사례는 (i) 태그 명명 규칙으로부터 설비 모델을 추정하는 reverse-engineering 접근, (ii) 자연어 → 사다리 논리(Ladder Logic) 직접 생성, (iii) AAS Submodel 자동 채움 등이 있다. 본 연구는 이들과 달리 **자연어가 우선 고수준 그래프 DSL로 변환된 뒤** 세 종류의 표준/가독 산출물로 동시에 투영된다는 점에서 차별된다. 즉 LLM이 직접 PLC 코드를 쓰지 않고, 검증 가능한 중간 표현(YAML 모델 문서)을 거친 후 결정적(deterministic) 변환기가 코드를 생성한다.

<!-- page-break -->

## 3. 시스템 개요

Promaker는 WPF 기반 데스크톱 애플리케이션이며 네 개의 핵심 구성 요소로 이루어진다.

| 구성 요소 | 역할 | 주요 파일 |
|----------|------|-----------|
| LLM Chat View | 사용자 입력/응답 UI, 턴 관리 | `LlmAgent/`, `ViewModels/LlmChatViewModel.cs` |
| MCP In-process Server | LLM에 도구 노출, nonce 인증 | `LlmAgent/McpHostService.cs` |
| DS Store / Model Protocol | DS 그래프 단일 진실 원천(SSoT) + YAML 직렬화 | `Solutions/Core/Ds2.Core/Store/DsStore.fs`, `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs` |
| Exporter (AASX, XGI) | DS → 표준 산출물 변환 | `Solutions/Convert/Ds2.Aasx`, `AAStoPLC.dll` |

전체 흐름은 다음과 같다. 사용자가 채팅창에 자연어로 공정을 기술하면, system prompt에 묶인 모델링 규칙과 함께 LLM에 전달되고, LLM은 선언적 YAML 모델 문서 한 편을 `apply_model_doc` MCP 도구로 발행한다. 백엔드는 이름 기반 dotted-path와 forward reference를 2-pass로 해소하여 DS 그래프를 일괄 갱신하고, 검증 실패 시에는 적용 전 상태로 자동 롤백한다. 이후 사용자가 저장을 누르면 동일한 그래프로부터 YAML, AASX 파일, LS XGI XML이 생성된다.

## 4. 자연어 → DS 그래프 변환

### 4.1 LLM 제공자 추상화

Promaker는 단일 `IChatClient` 추상화 위에 다섯 종류의 백엔드를 두고 있다(Claude CLI, Codex CLI, Anthropic API, OpenAI API, Ollama). 사용자가 어떤 백엔드를 선택하든 동일한 system prompt와 동일한 MCP 도구 집합이 노출되도록 설계되었으며, API 키는 DPAPI(`Promaker.LlmApi.v1` entropy)로 암호화하여 저장한다(`LlmAgent/LlmConfig.cs`).

### 4.2 System Prompt 3-Tier 합성

system prompt는 다음 세 계층을 자연 정렬 후 연결하여 생성된다(`PromptLoader.cs`).

1. **Baseline (어셈블리 내장)**: `Promaker.LlmAgent.Prompts/*.md` — 모델링 규칙의 기본형
2. **Operator (실행 파일 폴더)**: 설치 환경별 보정 가능
3. **User (`%APPDATA%/Promaker/Prompts/*.md`)**: 사용자 개인 규칙

Baseline은 네 개의 문서(`1.entities.md`, `2.modeling.md`, `3.tooling.md`, `4.attachments.md`)로 구성되며 각각 (1) 엔티티 정의, (2) 자연어 → 엔티티 분해 규칙, (3) MCP 도구 호출 규약 — 주력 진입점인 `apply_model_doc` 사용 패턴 포함, (4) 첨부물 처리 규칙을 담당한다.

### 4.3 MCP 도구 카탈로그

LLM은 in-process Kestrel HTTP 서버로 노출된 MCP 엔드포인트와 통신한다(`LlmAgent/McpHostService.cs`). 손쉬운 외부 침입을 막기 위해 루프백(127.0.0.1) 임시 포트에 바인딩되며, 32바이트 nonce가 HTTP 헤더(`X-Promaker-Nonce`)로 검증된다. 노출되는 도구는 다음과 같이 doc-level 진입점과 read-only 조회로 정리되어 있다(`LlmAgent/PromakerToolNames.cs`).

- **Doc-level 편집** (주력 진입점): `apply_model_doc`, `validate_model_doc`, `export_model_doc`, `json_to_yaml`
- **읽기**: `find_by_name`, `validate_model`

위 `export_model_doc` 는 `path?: string` (dotted-path scope, 예: `.Proj1.SysA`) 과 `depth?: int ≥ 0` (walk 깊이) 인자로 부분 export 도 수행한다. 초기 설계의 `list_projects` / `list_systems` / `describe_system` / `describe_subtree` 4종 read 도구는 `export_model_doc(path?, depth?)` 으로 흡수되었다 (Phase 6 read surface GUID-free 정렬). 부분 export 결과는 envelope 의 `view: partial` flag + `summary: { totalEntities, emitted, budget }` 진단 metadata 를 동반하며, view-only — apply/validate 재입력은 거부된다.

초기 설계는 21종에 달하는 단일 연산 도구(`add_project`, `add_flow`, `apply_operations` 등)를 노출했으나, 다단 연산이 누적되며 token 비용과 재시도가 늘어나는 문제가 있었다. 현재 설계는 위 도구만 노출하고 한 턴의 발화에 대해 LLM이 단일 YAML 문서를 발행하도록 유도한다. 단일 연산 도구는 escape hatch로만 남아 있으며 일반 사용 흐름에서는 호출되지 않는다.

### 4.4 중간 표현: YAML 모델 문서

LLM은 자연어 입력을 받아 다음과 같은 선언적 YAML 문서를 한 번에 발행한다(스키마 `promaker/v0`).

```yaml
protocol: promaker/v0

project: M1

systems:
  - system: Controller
    kind: active
    flow Run:
      works:
        Adv:    { calls: [Cyl.ADV] }
        Ret:    { calls: [Cyl.RET] }
      arrows:
        - Adv -> Ret : Start

  - system: Cyl
    kind: passive
    device: cylinder
```

식별자는 모두 사람이 읽는 이름이며, GUID는 LLM에게 노출되지 않는다. 백엔드는 1-pass에서 이름 테이블을 만들고 2-pass에서 forward reference를 GUID로 해소한다. 이로써 LLM은 그래프 편집의 순서를 신경 쓰지 않고 선언적 사양만 생산하면 된다. Arrow는 `A -> B : <Type>` 표기로 `Start`, `Reset`, `StartReset`, `ResetReset`, `Group`, `Unspecified` 여섯 가지를 명시한다. `device:` 키는 `cylinder`/`clamp`/`robot` 같은 알려진 장치 유형의 sugar이며 그 외에는 `custom(<이름>)` + `apis:` 목록으로 표현된다.

### 4.5 트랜잭션 적용과 롤백

`apply_model_doc`은 직접 그래프를 갱신하지 않고 `ImportPlanBuilder.Plan`에 F# 연산을 누적한다. 적용 진입 시점에 snapshot 카운트를 기록하고, 검증에서 오류가 발견되면 `TruncateTo`로 누적 연산을 잘라내어 트랜잭션 경계를 형성한다(`ModelProtocol.fs`). 사용자 관점에서는 한 번의 발화 = 한 번의 그래프 변경 = 한 단계 Undo로 매핑된다. 또한 LLM이 한 턴에서 호출할 수 있는 편집 연산 수에는 상한이 설정되어 있어(`LlmAgent/LlmTurnContext.cs`) 도구의 폭주를 사전 차단한다.

<!-- page-break -->

## 5. DS 언어

### 5.1 위치와 형식

DS 언어는 디스크 상에 JSON으로 직렬화된 typed AST 형태로 저장된다(`Solutions/Convert/Ds2.JsonFormatter/json-format.md`). 그래프 자체는 F# `DsStore`가 관리하며 엔티티 정의는 `Solutions/Core/Ds2.Core/Entities.fs`에 있다. YAML 모델 문서는 동일 그래프의 사람-친화적 직렬화 형식이며 양방향 변환이 보장된다(§6).

### 5.2 1급 엔티티

| 엔티티 | 의미 |
|--------|------|
| Project | 프로젝트 루트, 다수의 Active/Passive System을 보유 |
| DsSystem | 독립 시스템. Active(제어기)와 Passive(물리 디바이스)로 구분 |
| Flow | 독립적으로 트리거되는 흐름 단위 |
| Work | Flow 내 작업 단계(station). 상태머신 Ready→Going→Finish→Homing |
| Call | 디바이스 API 호출 (`DeviceAlias.ApiName` 형식) |
| ApiDef | Passive System이 제공하는 행위 정의(TxGuid/RxGuid 바인딩) |
| ArrowBetweenWorks | Work 간 흐름 제어 |
| ArrowBetweenCalls | Work 내부 Call 간 제어 |

### 5.3 Arrow 시맨틱

Arrow는 단순한 화살표가 아니라 신호의 시간 특성을 지정하는 1급 개념이다(`Solutions/Core/Ds2.Core/Enum.fs:5-12`, `Prompts/2.modeling.md §3.7`). 본 시스템의 Arrow는 두 개의 layer로 구분된다 — **Work-arrow layer**(같은 Active System 안의 Work 사이 또는 Work 내부의 Call 사이)와 **Passive 내부 opposing layer**(같은 Passive 디바이스의 상반 행위 쌍 사이). 두 layer는 같은 enum 값을 공유하지만 의미 차원이 다르다.

| 종류 | layer | 신호 특성 | 의미 |
|------|------|----------|------|
| Unspecified (0) | — | — | 연결 없음 |
| Start (1)       | Work/Call | rising edge (순간 신호) | 소스 Finish 순간에 타깃이 Ready 라면 Going 으로 전이. 타깃이 Ready 가 아니면 신호 무시 |
| Reset (2)       | Work-arrow | Going high level (지속 신호) | 소스가 Going 인 동안 타깃이 Finish 라면 Homing 으로 전이시킴 |
| StartReset (3)  | Work-arrow | Start + Reset 결합 | 소스 완료 시 타깃 시작, 동시에 소스가 진행 중인 동안에는 타깃을 reset 상태로 유지 (인터로크) |
| ResetReset (4)  | Passive 내부 | 양방향 상호 reset | 동일 디바이스의 상반 행위 쌍에 대해 한쪽이 시작하면 상대를 reset — 상호 배타 |
| Group (5)       | UI | 실행 시맨틱 없음 | 캔버스 클러스터링 hint |

Start 신호의 edge-triggered 성질(소스가 Finish에 머물러 있어도 신호는 1회만)과 Reset 신호의 level 성질의 비대칭은 capacity > 1 cycle의 핵심 메커니즘이다(`2.modeling.md §3.7`). ResetReset은 위 Work-arrow 시맨틱과는 다른 layer로, 자연어 입력에서 흔히 빠지므로 모델링 규칙(`2.modeling.md §3.5`)에 "동일 Passive 디바이스의 상반된 두 행위는 반드시 ResetReset Arrow로 연결" 규약을 명시한다. YAML 발행 시 Passive 내부의 ADV↔RET 같은 상반 쌍에 대해서는 백엔드가 자동으로 ResetReset을 채워주므로 LLM이 이를 별도로 기술할 필요는 없다.

### 5.4 작은 예시

`Solutions/Convert/Ds2.JsonFormatter/Samples/OR.json` 샘플은 두 실린더 `cyl1`, `cyl2`가 있을 때 `cyl1.ADV → cyl1.RET`과 `cyl2.ADV → cyl1.RET`을 OR 조건으로 결합한 단일 Work를 정의하고, 각 Passive 내부에서 ADV↔RET을 ResetReset으로 묶는다.

<!-- page-break -->

## 6. YAML 모델 문서

### 6.1 위치와 역할

YAML 모델 문서는 두 가지 역할을 동시에 수행한다.

1. **LLM ↔ Promaker 사이의 wire format**: `apply_model_doc`의 입력이자 `export_model_doc`의 출력.
2. **사람이 읽고 편집 가능한 SSOT 텍스트**: 디스크에 저장하여 diff, code review, revert에 활용 가능.

스키마 정의는 `Apps/Promaker/Docs/yaml-protocol-v0.md`(SSOT 문서)에 명시되어 있으며, 양방향 변환 코어는 `Solutions/Core/Ds2.LlmAgent/ModelProtocol.fs`에 구현되어 있다.

### 6.2 스키마 핵심

- **Top-level keys**: `protocol`(필수, `promaker/v0`), `project`(선택), `systems`(선택), `patch`(선택)
- **Active system**: `kind: active`, `flow <Name>:` 블록 안에 `works`(Work 이름 → `calls`) + `arrows`(Work 간 화살표) 선언
- **Passive system**: `kind: passive`, `device:`(알려진 sugar) 또는 `custom(<이름>)` + `apis:` 목록
- **경로 구분자**: `.` 또는 `/`, 엔티티 이름에는 `.` 금지
- **Arrow 표기**: `A -> B : <Type>`, 6종 타입 명시 필수
- **Override**: `workDuration`, `opposing` 등은 기본값과 다를 때만 emit (round-trip 동등성 유지)

### 6.3 산출/적용 도구

| 도구 | 방향 | 설명 |
|------|------|------|
| `apply_model_doc(model)` | YAML → 그래프 | 전체 적용. 부분 실패 시 자동 rollback. 정보 미스매치 시 Levenshtein 기반 가까운 후보를 진단 메시지로 반환 |
| `validate_model_doc(model)` | YAML 검증 | dry-run. 그래프를 건드리지 않고 스키마와 제약만 점검 |
| `export_model_doc(scope, format)` | 그래프 → YAML/JSON | `scope`는 `project` 또는 `system:<name>`, `format`은 `yaml` 또는 `json` |
| `json_to_yaml(json)` | 형식 변환 | 검증 없이 텍스트 변환만 수행 (`apply_model_doc` 응답을 YAML로 미리보기할 때 사용) |

### 6.4 Round-trip 보장

`apply(export(M)) ≡ M`이 단위 테스트로 강제된다. 이 등가성은 다음과 같은 export 정교화 위에서 성립한다.

- Call 참조는 `Call.DevicesAlias`를 무시하고 Passive system 이름으로 정규화 emit (alias→systemName SSOT)
- Passive device 추정은 `SystemType` + `apis` fingerprint로 known sugar 매칭, 실패 시 `custom(Unknown)` fallback과 함께 경고 로그를 남김
- `workDuration`/`opposing` override는 기본값과 다를 때만 emit

### 6.5 UI: 발행된 YAML 미리보기

채팅 turn이 끝나면 발행된 YAML이 chat bubble 또는 별도 다이얼로그(`ModelDocPreviewDialog`)로 노출된다. 다이얼로그는 두 개의 탭을 제공한다.

- **YAML 탭**: 텍스트 표시 + Clipboard 복사
- **Mermaid 탭**: YAML → 중간 JSON → work-flow / call-flow 블록으로 변환 후 WebView2로 Mermaid v11 다이어그램 렌더링 (`ModelDocPreviewDialog.xaml.cs`)

작은 응답(30줄 이하)은 chat bubble에 inline으로 표시되고, 큰 응답은 button을 통해 다이얼로그로 열리도록 ViewModel이 분기한다.

<!-- page-break -->

## 7. AASX 산출

### 7.1 모듈과 SDK

AASX 변환은 `Solutions/Convert/Ds2.Aasx` 프로젝트가 담당하며, 내부적으로 `AasCore.Aas3_1` SDK를 사용해 AAS 3.1 표준 환경으로 출력한다. 입력 측에서는 1.0/2.0/3.0의 구버전 XML 네임스페이스를 3.1로 정규화하는 변환기를 두어 하위 호환성을 확보한다(`AasxFileIO.fs`).

### 7.2 매핑

DS 그래프의 계층은 다음과 같이 AAS Submodel 트리로 투영된다.

- Project → 최상위 SubmodelElementCollection(SequenceModel Submodel)
- DsSystem → SMC (Flows SML + ApiDefs SML, `IsActiveSystem` 플래그 포함)
- Flow → SMC (Works SML + ArrowsBetweenWorks SML)
- Work → SMC (Calls SML + ArrowsBetweenCalls SML)
- Call / ApiCall → SMC (속성들)

각 필드는 Concept Description(CD)에 일대일로 등록되며, CD URI는 `{cdBase}/{TypeName}/{FieldName}` 규약을 따른다(`Export/Graph.fs`).

### 7.3 표준 Submodel 동봉

IDTA가 정한 표준 Submodel을 동시에 포함하여 AAS 환경 간 상호 운용을 보장한다.

- Nameplate (IDTA 02006-3-0)
- TechnicalData
- HandoverDocumentation (IDTA 02004-1-2)

### 7.4 UI 진입점

저장 대화상자에서 `.aasx` 확장자가 감지되면 `AasxExporter.exportFromStore(store, path, iriPrefix, splitDeviceAasx, autoCreateEmptySubmodels)`가 호출된다(`FileCommands.cs`). `splitDeviceAasx` 옵션은 각 Passive 디바이스를 독립 자산으로 분리하여 별도 AASX 패키지로 산출한다.

## 8. PLC 산출 (LS XGI)

### 8.1 출력물

PLC 산출의 1차 대상은 LS 산전 XGI XML(`.xml`)이며, 이는 XG5000 엔지니어링 도구가 그대로 열 수 있는 포맷이다. POU(Program), Function Block 인스턴스, IO 신호 매핑, 진단 메시지가 포함된다.

### 8.2 파이프라인

```
DsStore
  └─→ IoSignalPipeline.GenerateAll(store)        // IO 신호/주소 추출
  └─→ Plc.Xgi.Api.generateXmlWithDetail(...)     // FB 매핑 + IR → XML
  └─→ Plc.Xgi.Api.persistFbMappings(...)         // 매핑 결과를 store에 저장
```

핵심 호출은 `Dialogs/PlcXmlGeneratorDialog.xaml.cs`의 `RunGeneration`이다. 변환기는 `AAStoPLC.dll` 안의 F# API로 구현되어 있으며, 템플릿 기반 골격(`XGI_Template.xml`)과 코드 방출(IR → Rung)을 혼합한 전략을 사용한다.

### 8.3 템플릿 사용

`XgiTemplateExtractor`는 어셈블리 임베디드 리소스로부터 사용자 폴더(`AppData\Dualsoft\Promaker\PlcTemplate\XGI_Template.xml`)에 템플릿을 추출한다. 사용자는 이 템플릿을 편집하여 사이트별 조정(예: 통신 설정, 기본 라이브러리)을 할 수 있고, 이후 업그레이드 시 기존 편집은 보존된다.

### 8.4 매크로 기반 IO 명명

Wizard 모드에서 `BasicMacroIoGenerator`는 매크로(`$(F)`, `$(D)`, `$(A)`)를 Flow/Device/Api 이름으로 치환하여 IO 심볼명을 일괄 생성한다. 예: `Input_$(F)_$(D)_$(A)` → `Input_Run_Cyl1_ADV`. 이로써 자연어 시점부터 PLC 태그명까지의 명명 일관성이 유지된다.

### 8.5 지원 벤더 범위

현재 UI에 노출된 벤더는 LS XGI/XGK, Mitsubishi MX이다(`PlcSettingsDialog.xaml.cs`). Allen-Bradley와 Siemens S7용 프로토콜 DLL(`Ev2.PLC.Protocol.AB.dll`, `Ev2.PLC.Protocol.S7.dll`)이 동봉되어 있으나 UI 노출은 보류 상태이며, 향후 확장 여지를 시사한다.

<!-- page-break -->

## 9. 사용 사례

### 9.1 시나리오

사용자가 다음과 같이 채팅창에 입력한다.

> "실린더 한 개로 단순 왕복 동작 흐름 하나 만들어줘. 시스템 이름은 Controller, 흐름 이름은 Run."

### 9.2 LLM 응답 (단일 YAML 발행)

system prompt가 `2.modeling.md`의 분해 규칙과 `3.tooling.md`의 `apply_model_doc` 우선 사용 규약을 함께 주입하므로, LLM은 한 차례의 도구 호출로 다음 YAML 문서를 발행한다.

```yaml
protocol: promaker/v0
project: M1

systems:
  - system: Controller
    kind: active
    flow Run:
      works:
        Adv: { calls: [Cyl.ADV] }
        Ret: { calls: [Cyl.RET] }
      arrows:
        - Adv -> Ret : Start

  - system: Cyl
    kind: passive
    device: cylinder
```

규약상 동일 디바이스의 ADV/RET은 Passive 내부에서 ResetReset Arrow로 자동 연결되므로 LLM이 이를 별도로 기술할 필요가 없다(백엔드가 채워준다).

### 9.3 트랜잭션 적용

`apply_model_doc` 호출은 `ImportPlanBuilder`에 누적되며, 적용 직전에 `validate_model_doc` 단계를 통과해야 확정된다. 검증 단계에서 다음 사항이 점검된다.

- 모든 Work는 Flow에 속한다.
- 모든 Call의 ApiName은 해당 디바이스의 ApiDef 집합 안에 존재한다.
- ResetReset 제약이 동일 디바이스의 상반 행위 쌍에 대해 만족된다.

실패 시 `TruncateTo`로 누적 연산이 잘려나가 그래프는 적용 전 상태로 되돌아간다.

### 9.4 산출물

이후 파일 저장 시 사용자는 동일한 그래프로부터 세 종류 산출물을 얻는다.

- `M1.yaml`: 위와 동일한 모델 문서. 사람이 검토·diff·revert 가능한 SSOT 텍스트.
- `M1.aasx`: AAS 3.1 환경 파일. 외부 AAS 뷰어에서 열어 Submodel 트리를 확인할 수 있다.
- `M1.xml`: LS XGI 프로젝트 파일. XG5000에서 열어 자동 생성된 사다리 논리와 FB 인스턴스, IO 매핑을 확인할 수 있다.

세 산출물은 동일한 DS 그래프를 출처(SSoT)로 하므로, 향후 그래프가 변경되면 일관되게 재생성된다. 사용자는 YAML 산출물을 직접 편집한 뒤 `apply_model_doc`으로 재투입함으로써 LLM을 거치지 않고 그래프를 갱신할 수도 있다.

## 10. 논의

### 10.1 doc-level 단발 발행으로의 진화

초기 설계는 LLM에게 21종에 달하는 단일 연산 도구(`add_project`, `add_flow`, `apply_operations` 등)를 노출했다. 이는 LLM이 그래프를 점진적으로 빚어가게 했지만, 한 발화가 수십 회의 tool call로 분해되어 token 비용과 재시도 빈도가 늘어나는 부작용이 있었다. 현재 설계는 LLM이 한 차례의 선언적 YAML 문서를 발행하는 doc-level 진입점을 주력으로 하며, 단일 연산 도구는 escape hatch로만 남는다. 사내 측정에서는 같은 시나리오 대비 토큰 사용량과 재시도 횟수가 모두 감소했다.

### 10.2 LLM이 직접 PLC 코드를 쓰지 않는 이유

본 설계는 LLM에게 PLC 코드 생성권을 주지 않는다. 대신 선언적 모델 문서를 만들게 하고, 검증 가능한 변환기가 코드를 생성한다. 그 이유는 (i) 안전성: 잘못된 코드가 PLC에 그대로 흘러가면 물리적 위험을 야기할 수 있다, (ii) 표준 준수: 벤더 XML 사양은 LLM이 학습한 일반 코드 분포와 멀다, (iii) 재현성: 동일 모델 문서는 항상 동일 코드를 만든다 — 이 세 가지다.

### 10.3 트랜잭션 경계와 사용자 경험

`apply_model_doc`의 단발 적용은 단일 Undo 단위를 만들어 사용자가 "한 번의 채팅 → 한 번의 되돌리기"라는 직관적인 모델을 가질 수 있게 한다. 또한 YAML이 텍스트 SSOT로서 디스크에 보존되므로 사용자는 LLM 없이도 직접 편집·diff·revert를 통해 모델을 진화시킬 수 있다.

### 10.4 한계 및 향후 과제

- 현재 도구 카탈로그는 그래프 편집에 한정되어 있어 도면 인식, 사양서 첨부 파일 파싱 등은 별도 단계가 필요하다.
- AASX의 동적 정보(런타임 측정값) 매핑은 본 사례 범위 밖이다.
- PLC 산출은 LS XGI에 가장 성숙해 있으며, S7/AB 지원은 프로토콜 DLL 단계에 머무른다.
- YAML preview 단계에서 사용자가 적용 여부를 명시적으로 confirm/cancel 하는 인터랙션은 현재 보류 상태로, 발행과 적용이 한 턴에 묶여 있다.

## 11. 결론

본 논문은 자연어 → DS 그래프 → YAML/AASX/PLC라는 종단 파이프라인을 실제로 동작하는 Promaker 시스템으로 구현한 사례를 보고하였다. 핵심은 LLM에 자유 형식 코드 생성을 맡기지 않고, 선언적 YAML 모델 문서를 거쳐 표준 산출물로 결정적으로 변환하는 분리 설계에 있다. 동일한 단일 진실 원천(DS 그래프)으로부터 사람이 읽을 수 있는 YAML, AAS 3.1 자산, LS XGI 프로젝트가 동시에 생성됨을 보였으며, 이는 자연어 단계부터 PLC 코드 단계까지의 명명·구조 일관성을 보장한다.

## 참고문헌

[1] IDTA, "Specification of the Asset Administration Shell — Part 1: Metamodel," v3.1, 2024.

[2] IDTA, "Submodel Templates — Digital Nameplate," IDTA 02006-3-0.

[3] IDTA, "Submodel Templates — Handover Documentation," IDTA 02004-1-2.

[4] Model Context Protocol Specification, https://modelcontextprotocol.io

[5] LS Electric, "XG5000 Programming Manual."

[6] YAML 1.2 Specification, https://yaml.org/spec/1.2.2/
