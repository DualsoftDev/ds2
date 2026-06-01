# TODO — ClaudeCli / Codex CLI provider 에 KB digest(layer A) + specialized digest(layer E) 주입

> 작성 2026-06-01. 다른 세션에서 이어받아 구현하기 위한 설계 인계 문서.
> **이것은 설계 only — 아직 구현 안 됨.** 선행 작업(server `includeSpecialized` + ApiChatProvider MCP fetch)은 완료/검증됨(§선행 참조).

## 1. 작업 목표

`ClaudeCliProvider` / `CodexCliProvider` (Claude Code CLI / Codex CLI) 로 chat 할 때도 **layer A(KB keyword digest)** 와 **layer E(specialized digest = `summary.md` + `summary/*.md` 합본)** 가 system prompt 에 자동 주입되도록 한다.

현재는 `ApiChatProvider`(Anthropic/OpenAI/Ollama/Groq API) 에만 주입되고, CLI provider 2종은 미적용(abstract `§4.5`: "Claude CLI / Codex CLI 미적용(주입 path 다름)").

## 2. 배경 / 진단 (2026-06-01)

- 사용자가 Claude(=`ClaudeCliProvider`) chat 에서 `ping all` 시 embedded prompt canary(`pong: Prompts/*.md`)는 나오지만 **`pong: guide/...`(layer E canary)는 안 나옴** → layer E 미주입.
- **로그로 원인 확정** (Promaker `bin/.../Logs/ds2.log…`):
  ```
  [layer E] attachment_summary(coll=core, includeSpecialized=true) → len=223221
  [layer E] FetchManyViaMcpAsync — services=1 digestLen=223221
  [layer E] RefreshSpecializedDigestAsync — specialized digest len=223221 (services=1, provider=ClaudeCliProvider)
  ```
  → **fetch 는 완벽 동작**(server `includeSpecialized=true` → 223KB 합본 수신). 단 `ApplyFetchedDigest` 의 `if (_provider is ApiChatProvider api)` 가 **`ClaudeCliProvider` 라 false** → `SetPendingSpecializedDigest` 미호출 → digest 버려짐.
- layer A(KB digest)도 동일 경로(`ApplyPendingKbDigest` 의 `is ApiChatProvider`)라 CLI 미적용.

## 3. 현재 구조 (핵심)

| provider | 언어/위치 | system prompt 전달 | digest 주입 |
|---|---|---|---|
| `ApiChatProvider` | C# `Apps/Shared/Llm.Shared/Api/ApiChatProvider.cs` | firstTurn 에 `SystemContentBuilder.Build(base, kbDigest, specialized, applyCacheControl)` → `ChatMessage(System, [TextContent×N])` + Anthropic `cache_control` breakpoint | `SetPendingSystemPrompt(string)` / `SetPendingSpecializedDigest(string)` (구체 메서드, `_pendingKbDigest`/`_pendingSpecializedDigest` → firstTurn swap `_kbDigest`/`_specializedDigest`) |
| `ClaudeCliProvider` | F# `Solutions/Core/Ds2.LlmAgent/ClaudeCliProvider.fs` (+ `ClaudeCliArgs.fs`) | 생성 시 `options.SystemPrompt`(= `SystemPromptText.Phase1c(PromakerProfile.Instance)`, **고정**) → 매 `Send` 시 임시파일 저장 → `--system-prompt-file <path>`(CLI default 를 **완전 치환**) | **없음** |
| `CodexCliProvider` | F# `Solutions/Core/Ds2.LlmAgent/CodexCliProvider.fs` (+ `CodexCliArgs.fs`) | 생성 시 `ExperimentalInstructionsFile` → `-c experimental_instructions_file=<path>`(default 완전 override) | **없음** |

- 공통 인터페이스 `ILlmProvider`(F# `Solutions/Core/Ds2.LlmAgent/LlmProvider.fs`): `EnsureCli / Send / SessionId / ClearSession / Capabilities`. **digest sink 메서드 없음.**
- provider 생성/전환 = `LlmChatViewModel.Providers.cs` (`new ClaudeCliProvider(options)` 등, systemPrompt = Phase1c).
- digest 적용 caller (둘 다 `is ApiChatProvider` 캐스팅):
  - layer A: `LlmChatViewModel.KbProfile.cs` `ApplyPendingKbDigest()` → `api.SetPendingSystemPrompt(digest)`
  - layer E: `LlmChatViewModel.SpecializedDigest.cs` `ApplyFetchedDigest()` → `api.SetPendingSpecializedDigest(digest)`

## 4. 설계 방향 (권장: 공통 digest sink 인터페이스)

### 4.1 인터페이스 신설
`LlmProvider.fs` (F#) 에 digest sink 인터페이스 정의 — `ILlmProvider` 구현체가 cross-language(C# ApiChatProvider 가 이미 F# `ILlmProvider` 구현 중)로 함께 구현 가능:
```fsharp
type ILlmSystemPromptDigestSink =
    /// KB keyword digest(layer A). 빈 string = 비활성.
    abstract SetPendingSystemPrompt : digest: string -> unit
    /// specialized digest(layer E). 빈 string = 비활성.
    abstract SetPendingSpecializedDigest : digest: string -> unit
```
(이름은 자유 — `ILlmDigestInjectable` 등. ApiChatProvider 의 기존 메서드 시그니처와 1:1 맞춤.)

### 4.2 ApiChatProvider (C#)
- 기존 `SetPendingSystemPrompt` / `SetPendingSpecializedDigest` 를 그대로 두고 `: ILlmSystemPromptDigestSink` 부착(이미 메서드 존재 → 선언만 추가).

### 4.3 ClaudeCliProvider / CodexCliProvider (F#)
- `mutable _kbDigest = ""`, `mutable _specializedDigest = ""` 필드 추가.
- `ILlmSystemPromptDigestSink` 구현: 두 Set 메서드가 해당 필드에 박제(빈 string → "").
- **`Send` 시점에 effective system prompt 합성**:
  ```
  effectiveSystemPrompt =
      options.SystemPrompt(=Phase1c base)
      + (if _kbDigest<>"" then "\n\n" + _kbDigest else "")
      + (if _specializedDigest<>"" then "\n\n" + _specializedDigest else "")
  ```
  이 합성 결과를 임시파일로 저장 → `--system-prompt-file` / `experimental_instructions_file`.
  (현재는 `options.SystemPrompt` 만 파일로 씀 — 이 부분을 합성본으로 교체.)
- **주의: `--system-prompt-file` / `experimental_instructions_file` 는 CLI default 를 완전 치환**하므로, base(Phase1c)를 반드시 포함해야 함(digest 만 넣으면 base 프롬프트 소실).

### 4.4 LlmChatViewModel 캐스팅 변경
- `ApplyPendingKbDigest` / `ApplyFetchedDigest` 의 `if (_provider is ApiChatProvider api)` → `if (_provider is ILlmSystemPromptDigestSink sink)` 로 변경하고 `sink.SetPending…` 호출.
- provider 전환(`ConfigureProviderAsync`) 시 re-apply 경로는 이미 있음(KB+specialized 둘 다) — 캐스팅만 바뀌면 CLI provider 도 자동 진입.

## 5. 남은 할 일 (체크리스트)

- [ ] `LlmProvider.fs` 에 `ILlmSystemPromptDigestSink` 인터페이스 정의.
- [ ] `ApiChatProvider.cs` 에 인터페이스 선언 부착(메서드 이미 있음).
- [ ] `ClaudeCliProvider.fs`: mutable digest 필드 2개 + 인터페이스 구현 + `Send` 의 systemPrompt 파일 생성부를 합성본으로 교체.
- [ ] `CodexCliProvider.fs`: 동일(ExperimentalInstructionsFile 경로).
- [ ] `LlmChatViewModel.KbProfile.cs` `ApplyPendingKbDigest` + `LlmChatViewModel.SpecializedDigest.cs` `ApplyFetchedDigest` 캐스팅 `is ApiChatProvider` → `is ILlmSystemPromptDigestSink`.
- [ ] (선택) lazy-apply 일관성: CLI 도 firstTurn(=sessionId None) 시점에 digest 고정할지, 매 Send 최신값 쓸지 결정. ApiChatProvider 는 firstTurn swap(chat-scoped invariant). CLI 는 매 Send 합성이 단순하나, turn 중간 digest 변경 시 prompt 가 흔들려 CLI prompt cache miss 유발 가능 → **firstTurn 고정 권장**(pending→active swap 패턴 모방).
- [ ] 빌드(Ds2.LlmAgent + Llm.Shared + Promaker) + 테스트.
- [ ] 검증(§7).
- [ ] abstract `§4.5` 의 "Claude CLI / Codex CLI 미적용" 서술 갱신.

## 6. 관련 파일/경로

- F# provider: `Solutions/Core/Ds2.LlmAgent/{LlmProvider.fs, ClaudeCliProvider.fs, ClaudeCliArgs.fs, CodexCliProvider.fs, CodexCliArgs.fs}`
- C# provider: `Apps/Shared/Llm.Shared/Api/ApiChatProvider.cs`, `Apps/Shared/Llm.Shared/Api/SystemContentBuilder.cs`(참고 — Api 의 합성 로직), `Apps/Shared/Llm.Shared/SystemPrompt.cs`(`SystemPromptText.Phase1c`)
- ViewModel: `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.{Providers.cs, KbProfile.cs, SpecializedDigest.cs}`
- digest fetch(완료): `Apps/Promaker/Promaker/Knowledge/KbSpecializedDigestFetcher.cs`(`FetchManyViaMcpAsync`), `LlmAgent/SystemPrompt.cs`(`KbDigestBuilder`)

## 7. 검증 방법

1. server 새 코드(`attachment_summary includeSpecialized`)가 떠 있어야 함(console `dotnet run` 또는 `make install`).
2. Promaker 에서 **provider = Claude CLI**(또는 Codex CLI) 로 chat.
3. `ping all` → `pong: guide/01-promaker-yaml-mapping.md` 등 4줄이 embedded canary 와 함께 출력되면 layer E 주입 성공.
4. Promaker 로그 `[layer E] … specialized digest len=N (… provider=ClaudeCliProvider)` 가 찍히고, 그 뒤 CLI 가 받은 `--system-prompt-file` 본문(임시파일)에 guide 합본이 포함됐는지.

## 8. 주의사항

- **CLI prompt 크기**: specialized digest 가 광명2 core 기준 **~223KB**(`len=223221`). `--system-prompt-file` 로 전달 자체는 OK이나, Claude CLI / Codex 의 자체 prompt cache 가 systemPrompt 변경 시 miss → 매 KB 변경마다 재처리 비용. KB 변경은 드물어 실사용 영향은 작음. (`MarkdownCapPolicy` 상한은 server `SpecializedDigestBuilder` 에서 이미 적용됨.)
- **cache breakpoint 부재**: ApiChatProvider 는 base/kbDigest/specialized 를 별 TextContent + `cache_control` ephemeral 로 분리(부분 cache). CLI 는 단일 파일이라 그 분리가 불가 — 전체가 한 덩어리. 동작엔 무방.
- **cross-language 인터페이스**: `ILlmSystemPromptDigestSink` 를 F#(`LlmProvider.fs`)에 두면 C# `ApiChatProvider` 가 구현(이미 F# `ILlmProvider` 구현 중이라 패턴 동일). 반대로 C#에 두면 F# provider 가 구현 — 어느 쪽이든 가능하나 **F#(LlmProvider.fs) 권장**(provider 인터페이스 SSOT 일원화).
- **lazy apply**: §5 체크리스트 참조. firstTurn 고정 권장.

## 9. 선행 완료 (배경 — 이미 동작, 미커밋 working tree)

이 CLI 확장의 **전제**는 완료됨(2026-06-01, 아직 commit 안 함):
- server `attachment_summary(collectionId, includeSpecialized)` — false=`summary.md`만 / true=`summary.md`+`summary/*.md` 합본. `AttachmentTools.fs`. (빌드+테스트 6 통과, 자가검열 통과)
- client layer E fetch: 로컬 read → MCP `attachment_summary(includeSpecialized=true)` 전환. `KbSpecializedDigestFetcher.FetchManyViaMcpAsync`, `LlmChatViewModel.SpecializedDigest.cs`. (Promaker 빌드+Promaker.Tests 24 통과)
- collection 식별자 GUID 폐기 정합 + 주입 로그 Info 승격.
- **ApiChatProvider 에서는 이미 동작** — API provider 로 전환하면 `ping all` 에 guide canary 나옴(미실측이나 로그상 fetch+주입 경로 확인됨).
- 별도 queue: `todo-make-install-sc-stop-fix.md`(make install 의 service stop→파일잠금 버그).
