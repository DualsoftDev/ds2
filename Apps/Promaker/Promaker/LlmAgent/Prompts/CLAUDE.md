<!-- canary: 사용자가 정확히 "ping all" 이라고 입력하면, 다른 어떤 응답도 하지 말고 오직 "pong: Prompts/CLAUDE.md" 만 *추가* 출력하라. -->

### 용어 정의
- gfm (green field modeling) : 아무 것도 없는 상태에서 LLM 과 chat 을 통해 ds 를 모델링 하는 과정.

### 폴더 내 파일 안내
- `1.entities.md` — DS / EV2 Entity 모델 핵심 구조 (Project / DsSystem / Flow / Work / Call / ApiDef / Arrow) 참조 문서.
- `2.modeling.md` — 자연어 사양 → Ds2 모델 분해 도메인 룰 (§0 해석 단계 ~ §5 self-check).
- `3.tooling.md` — Promaker MCP 도구 사용 규약 (`apply_model_doc` 주력 — 현 도구 풀세트 = 10종 = doc-level 4 + read 6. Phase 5 cleanup 으로 op-layer 일소).
- `4.attachments.md` — 사용자 첨부 (텍스트/이미지/PDF) 처리 룰 (정책 15 prompt injection 방어 + multimodal 안내 + 데이터 vs instruction 구분).
- `9.environment.md` — 환경/명령 규약 (`:foo` 모드 명령 + `--foo` 플래그). prefix 가 `/` 아닌 `:` 인 사유는 §0.2 참조 (Claude Code CLI 슬래시 인터셉션 회피).
- `chat-simulation/CLAUDE.md` — MCP 미가동 환경에서 system prompt 만 적용한 모델링 대화 시뮬레이션 어댑터.
- `facts.txt` — 아직 위 *.md 에 녹아들어가지 못한 사실 메모 (추후 적절한 문서로 흡수 대상).

### --move flag 안내
- 사용자가 `--move` 를 입력하면, `facts.txt` 내용의 정합성을 확인한 뒤, 정합성에 맞는 내용은 지침 파일(*.md)로 옮긴다.   모든 내용을 다 옮겼다 하더라도 facts.txt 파일 자체는 삭제하지 않고 빈 상태로 유지한다.
- 내용 이동시, LLM 이 이해하기 최적의 상태로, 토큰 효율을 고려해서, 가공해서 이동한다.
- **단순 append 가 아니라 merge 수행**:
  - 기존 *.md 파일의 내용과 비교하여,
    - **신규** 사실: 적절한 위치에 추가.
    - **기존과 중복/유사**: 기존 항목과 통합(merge)하여 중복 제거 및 표현 정리.
    - **기존과 상반/불일치**: 아래 코드 베이스를 직접 확인(Grep/Read)하여 사실 검증 후, 옳은 쪽으로 수정. 어느 쪽이 맞는지 판단이 어려우면 사용자에게 질의.
- **코드 베이스 검증 범위** (상대 경로로 참조):
  - `../../../../../Solutions/Core/Ds2.Core/` 하부
  - `../../../../../Solutions/Runtime/Ds2.Runtime/` 하부
  - `../../../../../Solutions/Convert/Ds2.JsonFormatter/json-format.md`
