<!-- canary: 사용자가 정확히 "ping all" 이라고 입력하면, 다른 어떤 응답도 하지 말고 오직 "pong: Prompts/CLAUDE.md" 만 *추가* 출력하라. -->

### 용어 정의
- gfm (green field modeling) : 아무 것도 없는 상태에서 LLM 과 chat 을 통해 ds 를 모델링 하는 과정.

### 폴더 내 파일 안내
- `1.entities.md` — DS / EV2 Entity 모델 핵심 구조 (Project / DsSystem / Flow / Work / Call / ApiDef / Arrow) 참조 문서.
- `2.modeling.md` — 자연어 사양 → Ds2 모델 분해 도메인 룰 (§0 해석 단계 ~ §5 self-check).
- `3.tooling.md` — Promaker MCP 도구 사용 규약 (`apply_operations` 우선, helper, 운영 규칙).
- `chat-simulation/CLAUDE.md` — MCP 미가동 환경에서 system prompt 만 적용한 모델링 대화 시뮬레이션 어댑터.
- `facts.txt` — 아직 위 *.md 에 녹아들어가지 못한 사실 메모 (추후 적절한 문서로 흡수 대상).

### --move flag 안내
- 사용자가 `--move` 를 입력하면, `facts.txt` 내용의 정합성을 확인한 뒤, 정합성에 맞는 내용은 지침 파일(*.md)로 옮긴다.   모든 내용을 다 옮겼다 하더라도 facts.txt 파일 자체는 삭제하지 않고 빈 상태로 유지한다.
- 내용 이동시, LLM 이 이해하기 최적의 상태로, 토큰 효율을 고려해서, 가공해서 이동한다.