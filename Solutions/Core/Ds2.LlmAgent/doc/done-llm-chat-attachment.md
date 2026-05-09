# LLM Chat 첨부파일 — 완료된 작업 (historical record)

> `todo-llm-chat-attachment.md` 의 phase 별 산출물 누적. 본 문서는 *그 시점의 사실* 을 보존 — path / 식별자 갱신 안 함 (`git log --follow` 로 추적).

## Phase 3a-pre — Spike (rev 1, 2026-05-09)

### S-1 — Claude CLI 이미지/PDF 입력 채널 조사

- 조사 대상: `claude --version` = 2.1.136 (Claude Code)
- `claude --help` 풀 출력 검토 결과:
  - **`--image` 류 옵션 없음**
  - `--file <specs...>` 발견되나 형식이 `file_id:relative_path` (e.g. `--file file_abc:doc.txt file_def:img.png`) — Anthropic Files API 에 이미 업로드된 `file_id` 를 download 하는 용도이지, 로컬 이미지 업로드 채널이 아님
  - `--input-format <format>` 옵션 (text / **stream-json**) 발견 — non-interactive `--print` 모드 한정
- **결론**: Claude CLI 의 정석 multimodal 채널 = **`--input-format stream-json`** + JSON Lines stdin. 이 모드에서는 user message content 를 array 로 받기 때문에 Anthropic API 의 multipart content block (`{type:"image", source:{type:"base64", media_type:..., data:...}}` / `{type:"document", source:{type:"base64", media_type:"application/pdf", data:...}}`) 을 그대로 wire 가능. 이미지/PDF 모두 동일 채널.
- **`ClaudeCliProvider.fs:69` 영향**: 현재 `Stdin = Some prompt` (텍스트 stdin). multimodal turn 시 stream-json input 으로 전환 + JSON Lines 직렬화 helper 추가 필요. 기존 텍스트 only turn 은 회귀 방지 위해 text input 유지 (capability 분기) 또는 일관성을 위해 항상 stream-json 으로 전환 — commit-2 시점 결정.

### S-2 — Codex CLI multimodal 입력 spec

- 조사 대상: `codex --version` = codex-cli 0.128.0
- `codex --help` 풀 출력 검토 결과:
  - **`-i, --image <FILE>...` 발견** — 설명: "Optional image(s) to attach to the initial prompt"
  - **path 기반** 인자 (반복 가능, 다중 이미지)
  - PDF 류 옵션 **없음** → Codex 는 PDF 미지원 (정책 11 표와 일치)
  - 외부 검증 통과 사항 (rev 3) — Codex 가 PNG/JPEG/GIF/WebP 모두 지원하는 점 — 채널 (위치 인자 vs path 옵션) 이 **path 옵션** 으로 확정
- **결론**: Codex CLI 는 path 인자만 받음. 클립보드 paste 이미지처럼 disk 에 없는 경우 임시 파일 spool 후 path 전달 패턴 필요.
- **`CodexCliProvider.fs:85,107` 영향**: 현재 prompt 위치 인자만 사용. 첨부 시 `-i <path1> -i <path2> ...` 추가 + 임시 파일 spool helper 책임은 Codex 어댑터 안에서 수용 (DU 는 단일 case 유지).

### S-3 — Capabilities 표 확정

| Provider | 이미지 채널 | 이미지 cap | PDF | PDF cap | 비고 |
|---|---|---|---|---|---|
| Claude CLI 2.1.136 | `--input-format stream-json` content block (bytes base64) | Anthropic API 와 동일 = 5MB | O (document content block) | 32MB | stdin JSON Lines 로 전송 |
| Codex CLI 0.128.0 | `-i/--image <FILE>...` (path) | OpenAI API 기준 ≈ 20MB (미공식 — 추후 검증) | X | — | bytes 첨부 시 임시 파일 spool 필요 |
| Anthropic API | SDK content block (bytes) | 5MB (base64 inline) | O | 32MB | Phase 3b |
| OpenAI API | SDK content block (bytes) | 20MB | △ S-4 | △ S-4 | gpt-4o vision |
| Ollama | 모델 의존 (bytes, vision 모델만) | 모델 의존 | X | — | `EnsureCli` 시 `/api/show` 동적 조회 |

### Spike 결과로 todo 갱신된 항목

- §6 V-2 (Claude CLI 인자) → **closed** — stream-json content block 채널
- §6 V-3 (Codex CLI 채널) → **closed** — `-i/--image` path 기반
- §3.2 capability matrix Claude CLI 행 채움
- 정책 13 — DU 단일 `Image of name * bytes * mime` 유지 결정 (`ImagePath` case 미추가). Codex 어댑터가 임시 파일 spool 책임
- §7 주의사항 — Codex 임시 파일 spool 위치 + ChildProcessTracker lifetime 노트 (구현 시 명시 예정)

### S-4 — Phase 3b 진입 전 보류

- Anthropic.SDK 12.20.0 / OpenAI .NET SDK 2.10.0 / OllamaSharp 5.4.25 어댑터 PDF/image content block 매핑
- OpenAI API PDF 직접 입력 지원 여부 (V-1) 본 spike 에서 확정
- Phase 3a 완료 후 별도 진행
