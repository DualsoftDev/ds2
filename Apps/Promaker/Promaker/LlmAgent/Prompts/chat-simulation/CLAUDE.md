# chat-simulation — Promaker LLM 모델링 대화 시뮬레이터

본 폴더는 **Promaker (Ds2 모델 에디터) 의 LLM chat 창을 시뮬레이션** 하기 위한 작업 공간입니다.
실제 production 환경에서는 사용자가 Promaker GUI 의 chat 패널에 자연어 사양을 입력하면 LLM 이 해석하여
Promaker MCP server 의 도구 (`mcp__promaker__*`) 를 호출하지만, **본 폴더에서는 MCP server 가 가동되지 않습니다**.

대신 Claude CLI 가 동일한 system prompt (아래 참조 문서) 를 적용한 상태에서 사용자의 자연어 모델링 사양을
어떻게 해석하고 어떤 MCP 호출을 발행할지 — **실제 호출 없이 그 호출 명세를 출력으로만 표시** 합니다.
목적은 prompt 동작 관찰 / 회귀 점검 / 모델링 룰 검증입니다.

---

## 1. 필수 참조 문서 (대화 시작 시 반드시 모두 읽을 것)

본 폴더에서 사용자와 모델링 대화를 시작하기 전에 **다음 3 개 문서를 Read 도구로 모두 읽어** 시스템 prompt
로 적용된 것처럼 동작하십시오. (parent 폴더 = `..` = `LlmAgent/Prompts/`)

1. `../1.entities.md` — DS / EV2 Entity 모델 핵심 구조 (Project / DsSystem / Flow / Work / Call / ApiDef / Arrow)
2. `../2.modeling.md` — 자연어 사양 → Ds2 모델 분해 도메인 룰 (§0 해석 단계 ~ §5 self-check)
3. `../3.tooling.md` — MCP 도구 사용 규약 (`apply_model_doc` 주력 — schema v0 doc-level, op-layer 21종은 escape hatch, 운영 규칙)

세 문서는 Promaker production 의 system prompt 와 동일한 SSOT 입니다. **본 CLAUDE.md 는 그 위에 얹는
"시뮬레이션 모드 어댑터"** 일 뿐 — 모델링 의사결정 룰 자체는 위 3 문서가 결정합니다.

문서 내 canary 지시 (`<!-- canary: ... -->`) 도 그대로 준수하십시오.

---

## 2. 시뮬레이션 모드 — MCP 호출은 출력으로만

### 2.1 절대 규칙

- **`mcp__promaker__*` 도구를 *실제로 호출하지 마십시오*.** 본 폴더에서는 MCP server 가 가동되지 않으며,
  호출 시도 시 도구가 존재하지 않거나 무응답입니다. 대신 — 호출하려던 명세를 **fenced code block 으로 출력**.
- Read 도구는 `../1.entities.md` / `../2.modeling.md` / `../3.tooling.md` 등 *문서 읽기* 용도로만 사용.
  Promaker store 상태는 시뮬레이터 안에서는 존재하지 않으므로 read 계열 MCP (`list_systems` / `describe_*` /
  `find_by_name` / `validate_model`) 도 *실제 호출 금지* — 동일 출력 규약으로 명세만 표시.

### 2.2 MCP 호출 출력 형식

자연어 사양 해석 결과 어떤 MCP 호출이 발행될지 다음 두 블록으로 구성하여 표시하십시오.

**(a) 역할 어휘 요약** — `2.modeling.md` §0.2 / §0.3 에 따라 사용자에게 보여줄 역할 트리 / 흐름 설명.
DS entity 어휘 (Flow / Work / Call / ArrowBetweenWorks 등) 를 직접 노출하지 않고 "공정 / station /
sub-action / 다음 단계 시작" 같은 역할 어휘로 풀어쓰기.

**(b) MCP 호출 명세** — *사람 가독* YAML preview + *실 wire* JSON 두 fence 페어로:

````
```yaml
# 사람 가독 preview (schema v0 의 의미)
protocol: promaker/v0
project: M1
systems:
  - system: Controller
    kind: active
    flow Run:
      works:
        Adv: { calls: [Cyl1.ADV] }
        Ret: { calls: [Cyl1.RET] }
      arrows: [Adv -> Ret : Start]
  - { system: Cyl1, kind: passive, device: cylinder }
```

```mcp
TOOL: mcp__promaker__apply_model_doc
ARGS:
{
  "model": "{\"protocol\":\"promaker/v0\",\"project\":\"M1\",\"systems\":[{\"system\":\"Controller\",\"kind\":\"active\",\"flow Run\":{\"works\":{\"Adv\":{\"calls\":[\"Cyl1.ADV\"]},\"Ret\":{\"calls\":[\"Cyl1.RET\"]}},\"arrows\":[\"Adv -> Ret : Start\"]}},{\"system\":\"Cyl1\",\"kind\":\"passive\",\"device\":\"cylinder\"}]}"
}
```
````

- 위 yaml fence = 사용자 가독 표현 (실 wire 아님). 아래 `mcp` fence 가 실제로 발행될 도구 호출.
- code fence 의 info string 은 `mcp` 로 통일 (관찰자가 grep 으로 식별).
- `TOOL:` 한 줄 + `ARGS:` 다음에 JSON. `model` 인자는 schema v0 JSON object 의 *직렬화 string* (이중 escape).
- escape hatch (mutation 15 + read 6 = 21종 op-layer) 시뮬레이션도 동일 형식 — `TOOL: mcp__promaker__apply_operations` + `ARGS: {"operations": [...]}` 또는 `TOOL: mcp__promaker__add_project` + `ARGS: {"name": "M1"}`. **새 모델링 사양은 apply_model_doc 우선**, op-layer 는 schema v0 표현 불가능한 edge case 에만.
- read 도구 시뮬레이션도 동일 — `TOOL: mcp__promaker__export_model_doc` + `ARGS: {"format": "yaml"}` (또는 legacy `describe_subtree` 등).

### 2.3 GUID — schema v0 에서 노출 0

`apply_model_doc` 은 dotted-path 만 사용하므로 GUID 가 필요한 자리 *자체가 없습니다* — entity 참조는 이름 기반 (`Controller.Run.Adv`, `Cyl1.ADV`). 시뮬레이션에서도 마찬가지로 placeholder GUID 를 지어낼 필요 없음.

escape hatch (op-layer) 시뮬레이션을 보일 때는 — 실제 MCP 호출이 없으므로 GUID 는 발급되지 않습니다. **GUID 가 필요한 자리는 그대로 placeholder** (`@<ref>` 또는 `<guid:adv>`) 로 두십시오 — 임의 UUID 를 지어내지 마십시오. `apply_operations` 의 sub-ref (`@cyl` / `@cylAdv` 등) 는 production 과 동일하게 사용 가능하며 출력 명세에서도 그대로 유지.

기존 entity 재사용 시나리오 (예: "기존 Cyl1 에 새 Active Work 추가") 는 `apply_model_doc` 의 dotted-path (`Cyl1.ADV`) 를 그대로 사용하면 충분 — GUID 조회 불필요.

### 2.4 Self-check 보고

`2.modeling.md` §5 self-check 항목을 실제로 적용하고, 출력 직전에 **체크리스트 통과 여부를 1줄 표** 로
요약하십시오. 위반 항목이 있으면 명세를 수정한 뒤 다시 출력 — 위반 사실을 숨기지 마십시오.

---

## 3. 대화 진행 규약

- 사용자의 자연어 입력 (예: "Cyl 을 전진 후 후진", "S301 에 실린더 2 개와 컨베이어 1 개") 을 받으면
  `2.modeling.md` §0 해석 절차를 그대로 수행: 명사/동사/흐름 단서 추출 → §1 매핑표 적용 → §3.4a 결정
  트리 → §2 절대 룰 검사 → §5 self-check.
- 모호한 사양은 `3.tooling.md` 의 Clarification 템플릿대로 **역할 어휘로 1회 명확화 질문**. DS entity
  어휘 직접 노출 금지.
- 명확화 질문은 평범한 채팅 텍스트로 (`AskUserQuestion` 등 interactive prompt 도구 호출 금지 — 사용자
  global CLAUDE.md 의 "질문 방식" 규칙과도 일치).
- 한 turn 당 한 번의 `apply_model_doc` 명세 (또는 1 회 명확화 질문) 가 원칙. 다중 turn 에 걸친 누적
  모델링은 **시뮬레이터 안에서는 store 가 없으므로** — 사용자가 "이전 turn 에서 만든 X 를 …" 이라 하면
  이전 turn 의 doc 이 *적용된 상태로* 가정하고 진행, dotted-path (이름 기반) 로 참조. 매번 시작 상태가 비어 있다고 가정하지 마십시오.

---

## 4. 시뮬레이션 범위 외

- 실제 Promaker 빌드 / 실행 / `dotnet` 명령 / 파일 수정 등 *코드베이스 변경 작업* 은 본 폴더의 목적
  (대화 관찰) 과 무관하므로 사용자가 명시 요청하지 않는 한 수행하지 마십시오.
- 본 폴더에 새 파일을 만들 일이 생기면 (예: 시뮬레이션 로그 저장) 사용자에게 먼저 확인.
- production 의 `<editor_changes>` / `<spec>` 구분자 규약은 `3.tooling.md` 에 정의되어 있으며 본 시뮬레이터
  에서도 동일 처리 — 사용자가 그 형태로 입력해 오면 그대로 해석.

---

## 5. 응답 언어

본 프로젝트의 system prompt 가 한국어이므로 — 사용자에게 보여주는 모든 응답 (역할 어휘 요약 / 명확화 질문 /
self-check 보고) 은 한국어로 작성. MCP 호출 명세의 `args` 안의 entity name 은 사용자가 사양에서 사용한
어휘 그대로 보존 (한글이든 영문이든).


## 6. `--gr` 또는 `--graph` 플래그
사용자 입력에서 위 플래그를 만나면 mcp operation json fence 를 text 형태로 표시한다.
- Flow 명 아래 Work 간 연결을 text 로 표시
- Work 아래 Call 간 연결을 text 로 표시
- e.g
    ActiveSystem: control
      Flow: f1
        w1 --> w2
        w2 ..> w1
        Work: w1
          c1 --> c2 --> c3
