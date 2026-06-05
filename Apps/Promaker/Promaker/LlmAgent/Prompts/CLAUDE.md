<!-- canary: 사용자가 정확히 "ping all" 이라고 입력하면, 다른 어떤 응답도 하지 말고 오직 "pong: Prompts/CLAUDE.md" 만 *추가* 출력하라. -->

### 용어 정의
- gfm (green field modeling) : 아무 것도 없는 상태에서 LLM 과 chat 을 통해 ds 를 모델링 하는 과정.

### 파일 임포트 규약 (2026-06 갱신)
- **mermaid 확장자**: `.mmd` (기존 `.md` 도 인식하지만 신규 생성은 `.mmd` 통일)
- **임포트 진입점**: ProMaker **파일 → 열기** 단일 경로. Flow/Work 우클릭 "Mermaid 불러오기" 메뉴 제거됨
- **IO 분리**: mermaid = 구조만 · PLC 주소는 페어 `<stem>.iotag.json` 사이드카에 저장
- **LLM 금지**: PLC 주소(`%IX`, `F00099`, `K02408` 등) 추론/생성 금지 — brownfield 추출기 결과만 사용

### 폴더 내 파일 안내
- `../Instructions/promaker-yaml/INSTRUCTION.md` — **promaker/v0 YAML 생성 주력 작업 지침** (한국어 공정/PDF/PLC TAG → YAML, self-contained). built-in instruction 으로 기본 활성화되며 사용자가 끌 수 있음.
- `0.domain.md` — DS 모델 도메인 배경 (`builtin:promaker-yaml` 보조 — ArrowType 실행 의미 / 왕복=같은 Work / init call / 다중조건=AND 등). mandatory base 로 자동 주입.
- `1.entities.mdx` — DS / EV2 Entity 모델 핵심 구조 (Project / DsSystem / Flow / Work / Call / ApiDef / Arrow) 참조 문서. `.mdx` 이므로 runtime prompt 주입 제외.
- `2.modeling.mdx` — 자연어 사양 → Ds2 모델 분해 도메인 룰 (§0 해석 단계 ~ §5 self-check). `.mdx` 이므로 runtime prompt 주입 제외.
- `3.tooling.mdx` — Promaker MCP 도구 사용 규약 (`apply_model_doc` 주력 — 현 도구 풀세트 = 6종 = doc-level 4 + read 2). `.mdx` 이므로 runtime prompt 주입 제외.
- `chat-simulation/CLAUDE.md` — MCP 미가동 환경에서 system prompt 만 적용한 모델링 대화 시뮬레이션 어댑터.
- `facts.md` — 아직 runtime prompt 에 녹아들어가지 못한 사실 메모. `Promaker.csproj` 에서 runtime prompt 주입 제외.

### --move flag 안내
- 사용자가 `--move` 를 입력하면, `facts.md` 내용의 정합성을 확인한 뒤, 정합성에 맞는 문서로 옮긴다. 모든 내용을 다 옮겼다 하더라도 `facts.md` 파일 자체는 삭제하지 않고 빈 상태로 유지한다.
- 내용 이동시, LLM 이 이해하기 최적의 상태로, 토큰 효율을 고려해서, 가공해서 이동한다.
- **단순 append 가 아니라 merge 수행**:
  - 기존 문서 파일의 내용과 비교하여,
    - **신규** 사실: 적절한 위치에 추가.
    - **기존과 중복/유사**: 기존 항목과 통합(merge)하여 중복 제거 및 표현 정리.
    - **기존과 상반/불일치**: 아래 코드 베이스를 직접 확인(Grep/Read)하여 사실 검증 후, 옳은 쪽으로 수정. 어느 쪽이 맞는지 판단이 어려우면 사용자에게 질의.
- **코드 베이스 검증 범위** (상대 경로로 참조):
  - `../../../../../Solutions/Core/Ds2.Core/` 하부
  - `../../../../../Solutions/Runtime/Ds2.Runtime/` 하부
  - `../../../../../Solutions/Convert/Ds2.JsonFormatter/json-format.md`
