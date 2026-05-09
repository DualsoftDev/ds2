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

## Phase 3a — commit-1 (a93e763, 2026-05-09)

### 산출물

- `Solutions/Core/Ds2.LlmAgent/LlmMessage.fs` (신규) — `ImageFormat` enum (Png/Jpeg/Gif/Webp) + `Attachment` DU (Image/Pdf/TextFile) + `LlmUserMessage` record + `OfText` factory + `Capabilities` record + `TextOnly` factory
- `Ds2.LlmAgent.fsproj` `<Compile Include>` 에 `LlmEvent.fs` 다음으로 등록
- 빌드 통과 (오류 0 / 경고 0). 사용처 0 — dead code 의도 (commit 정의 = "type 신설")

## Phase 3a — commit-2 (7450520, 2026-05-09)

### 산출물

- `Solutions/Core/Ds2.LlmAgent/LlmMessage.fs` — `Capabilities.ImagesOnly(maxImageBytes)` / `ImagesAndPdf(maxImageBytes, maxPdfBytes)` static factory 2종 추가 (C# 측 호출 친화)
- `Solutions/Core/Ds2.LlmAgent/LlmProvider.fs` — `ILlmProvider.Send` 시그니처 `string prompt` → `LlmUserMessage msg` 교체 + `Capabilities` 추상 멤버 추가
- `Solutions/Core/Ds2.LlmAgent/ClaudeCliProvider.fs` — instance Send + interface impl 갱신, `Capabilities = ImagesAndPdf(5L*1024L*1024L, 32L*1024L*1024L)` (Anthropic API 와 동일 — `--input-format stream-json` 으로 동일 wire)
- `Solutions/Core/Ds2.LlmAgent/CodexCliProvider.fs` — 동일 패턴, `Capabilities = ImagesOnly(20L*1024L*1024L)` (V-1 미해결 placeholder)
- `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs` — ctor 에 `Capabilities` 인자 추가, `Send(LlmUserMessage)` 시그니처, `Capabilities` 프로퍼티 노출
- `Apps/Promaker/Promaker/LlmAgent/Api/ApiProviderFactory.cs` — 4종 factory (Anthropic/OpenAI/Groq/Ollama) 에 capability 분배. Anthropic = `ImagesAndPdf(5MB, 32MB)` / OpenAI = `ImagesOnly(20MB)` / Groq = `TextOnly` / Ollama = `TextOnly` (모델 의존 동적 갱신은 commit-4..N 으로 미룸)
- `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:466` — `_provider.Send(promptForProvider, ct)` → `_provider.Send(LlmUserMessage.OfText(promptForProvider), ct)` wrap
- 빌드 통과 (Promaker.sln 전체, 오류 0 / 경고 2 — OllamaSharp source generator 사전 환경 무관)
- 자가 검열 통과: 7파일 +76/-15, Critical/Major 0건, Minor 1 (V-1 placeholder, 코드 주석 명시)

### 회귀 호환

- 본 commit 정의 = "Attachments 무시 / msg.Text 만 사용 → 기존 텍스트 송신 회귀 통과"
- 5종 어댑터 모두 `let prompt = msg.Text` 또는 `msg.Text` 로 추출 후 기존 흐름 그대로
- 테스트 (`Solutions/Tests/Ds2.LlmAgent.Tests/`) 와 `MainViewModel.LlmChat.cs` 모두 `_provider.Send` 직접 호출 0건 — 마이그레이션 누락 없음 (자가 검열 grep 검증)
