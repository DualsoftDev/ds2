# todo-free-llm-providers

> **상태**: 논의 단계 (`--plan` 모드 진행). 코드 변경 없음. 어떤 provider 를 통합할지, 어떤 순서로 진행할지 사용자 결정 대기.

> **revision history**
> - rev 1 (2026-05-08): `--plan` 1차 답변 + `--transfer` 작성. provider 후보 12종 + 통합 고려사항 4종 정리.
> - rev 2 (2026-05-09): 1차 `--review` 반영 (Major 7건). Together 무료 폐지 / Gemini OpenAI 호환 / Groq 모델별 한도 / Cerebras 모델 예시 / SambaNova / OpenRouter 정정 + Settings UI 작업 추가.
> - rev 3 (2026-05-09): 2차 `--review` (5 reviewer 종합 — Critical 3 / Major 9 / Minor 다수) 반영. C1 Microsoft.Extensions.AI.Google 패키지 후보 TBD / C2 spike-first 원칙 / C3 결정 ↔ phase 의존 그래프 / M1 §A 표 재정정 / M2 LlmEvent 5종 / M3 L→F rename / M4 retry layer spike + multi-key rotation 거부 / M5 회귀 metric 4종 / M6 ProviderCapabilities record / M7 진행 상태 신설 / M8 결정 1~6/9 영향평가 / M9 Settings UI fallback / Minor 다수.
> - **rev 4 (2026-05-09)**: 3차 `--inspect` (3 reviewer 교차검증 — Critical 4 / Major 14 / Minor 11) 반영.
>   - **C1 stale 인용 정정**: `MainViewModel.cs:685-` (245줄 파일에 존재 불가) → `MainViewModel.Rebuild.cs:41 / :50 / :68` (정의 / BeginInvoke / DispatcherPriority.Background). `ImportPlanApply.fs:46-50` → `:48-52` (의미상 정확) 동시 정정.
>   - **C2 ProviderCapabilities DU 변량 명시**: `UsageLocation` / `SystemPromptStyle` case 본문 정의. F-3 진입 시 임의 정의 위험 제거.
>   - **C3 D-α↔D-γ 순환 dependency 해소**: D-γ 를 `D-γ-pre` (metric 후보 합의, F-1 prerequisite) + `D-γ-post` (F-1 spike 결과 반영 확정, F-2 진입 직전) 로 분할.
>   - **C4 ProviderCapabilities API 한정 명시 + ProviderConnection 분리**: capabilities (정적/불변) ↔ connection (런타임/사용자 입력) lifetime 분리. CLI provider 비호환 명시.
>   - **M1 system prompt 채널 차이 표 신설** (§1A) — Claude CLI = `--append-system-prompt-file`, Codex CLI = 위치 인자, API = `ChatRole.System`, Gemini = system role 흡수.
>   - **M2 `--allowed-tools` ↔ server-side allowlist 결론** (§2 sub-bullet) — HTTP API 는 MCP 21 tool `[McpServerToolType]` 등록으로 자연 충족.
>   - **M3 self-check 항목 카운트 정정**: "16" → **"18"** (직접 `^- \[` grep 검증; `2.modeling.md:431-448`).
>   - **M4 결정 6 wording 정정**: "(없음 — rev 1 history)" → "(결정 2 로 흡수, 결정 7 (d) 와 통합)" — `todo-promaker-llm-agent.md:53/153/608` 정합.
>   - **M5 record 위치 결정**: F# `LlmProvider.fs` 정의 + `[<CLIMutable>]` + `enum SystemPromptStyle` (DU 격하) — C# pattern matching cost 회피, 결정 2 정합.
>   - **M6 F-1 cleanup 책임 명시**: spike 코드는 별도 branch / main leak 금지. F-4 commit 에서 정식 schema 로 대체 + 임시 분기 삭제.
>   - **M7 F-2 분할**: F-2-infra (검증기 / 분류기 / 측정 hook) + F-2-baseline (Claude 4.6 + Groq 측정).
>   - **M8 F-7d PASS 기준 명시**: ① streaming delta 정상 / ② tool_use_id 매칭 정상 / ③ 21 tool 모두 invocation 가능 — 3 항목 모두 통과만 F-7d, ②③ 1개라도 실패 시 F-8.
>   - **M9 F-5 분할**: F-5a (XAML Row + Load/Save) / F-5b (모델 candidates + ContextMenu) / F-5c (GET /models ping + 회색 fallback) / F-5d (Consent 본문 갱신).
>   - **M10 LlmProviderKindDriftTest sub-task 신설** (F-6) — enum + AvailableProviders + dispatch 3 SSOT 자동 drift 감지.
>   - **M11 §7 CLI vs API two-class 추상화** 신설 — CLI : API 비대칭 심화 (1:5) 대비 통합점 명시.
>   - **M12 Decision matrix 압축**: 9 행 → 5 행 (영향 있음만) + 1 footer (영향 없음 4건).
>   - **M13 D- prefix disambiguate**: 절 제목 "**미정 결정 (D = Decision-pending)**" / "**확정 결정 1~9**" 분리.
>   - **M14 line 30 절에 D-β/δ/ε 추가**: 흐름 묘사 누락 정정.
>   - **자가 검열 trigger 확장**: F-3+F-4 atomic 외 F-5 / F-6 도 trigger ③/⑤ 충족 → 명시 적용.
>   - **Minor 일괄**: rev 3 history bullet 분할 / 참조 sources access date / Send 시그니처 tuple 표기 / xAI Grok §5 단일화 / `ModelContextProtocol.AspNetCore` 1.3.0 별도 todo / F-7e wording 명확화 등.
> - **rev 5 (2026-05-09)**: API key 발급 / 회원가입 요건 조사 결과 반영.
>   - **§A1 신설**: provider 별 회원가입 채널 / 신용카드 요구 / 발급 위치 표.
>   - **§A 표 비고 정정**: DeepSeek 의 신용카드 정책 명시 — 5M 토큰 무료 grant 와 별개로 **API 호출 시점 $2 최소 잔액 요구** (소진 전부터 카드 등록 의무). F-7e 진입 시 사용자 사전 안내 의무.
>   - **§A 표 비고 추가**: Gemini API key — **2026-06-19 부터 unrestricted traffic key 폐지** (referrer / IP 제한 의무화). F-7d / F-8 진입 시점에 Settings UI 측 안내 + key 발급 가이드 갱신 의무.
>   - **§5 Consent 갱신**: API key 보관 = 사용자 직접 콘솔 가입 → DPAPI 보관 책임 chain 명시. 키 미입력 / 검증 실패 시 회색 fallback 동작 (F-5c) 와 정합.
>   - **F-7d 비고**: Gemini key restriction 정책 변경 trigger 추가.
>   - **F-7e 비고**: DeepSeek 진입 시 카드 등록 사전 안내 의무.
> - **rev 6 (2026-05-09)**: F-1 spike 1차 manual smoke test 결과 반영 (Groq + Llama 4 Scout 17B).
>   - **F-1 spike 통과**: OpenAI SDK Endpoint override / Microsoft.Extensions.AI streaming function calling / MCP HTTP 21 tool 노출 / LlmEvent 5종 매핑 모두 정상 동작 (`session=5163ceb0..` + `turn 종료 — 124244ms, $0.0000, stop=stop, denials=0`).
>   - **§A 표 비고 갱신**: Groq free tier 의 RPM/RPD 외 **TPM (tokens/min) 이 가장 빡빡한 제약**. `llama-3.3-70b-versatile` = TPM 12,000 (Promaker system prompt + 21 tool descriptions 합 ~25K → 단일 호출 HTTP 413 즉시 거부 — retry 무관).
>   - **§A 표 모델 ID prefix 명시**: Groq Llama 4 모델 = `meta-llama/` prefix 필수 (prefix 누락 시 HTTP 404 `model_not_found` — 1차 spike 시도 시 발견). Llama 4 Scout 17B 는 Groq **Preview Model** 분류 (production 비권장).
>   - **§F-1 산출 절 신설**: spike 결과 4종 (통과 / architectural / Groq specific / retry layer 미결정) + 새 todo trigger.
>   - **Architectural 발견 `todo-hotfix-llm.md` 분리**: ① ImportPlan queued vs LLM 동기 검증 mismatch (결정 7 (d) side-effect) / ② Promaker UI 한글 `\uXXXX` escape 미해석 → provider/모델 무관 hotfix 별도 todo.
>   - **모델 품질 요건 발견**: Llama 4 Scout 17B class = Promaker modeling 작업에 부족. 2회차 spike (`system NewSystem2 생성` prompt) 에서 **NewSystem2 ×2 중복 생성** + LLM 자기 첫 mutation 결과 context 누락 관찰. architectural Issue 1 의 모델 quality 영향 직접 입증. free tier 의 70B+ 만 실용 가능, 그러나 70B+ 는 TPM 12K 한도 → system prompt 압축 prerequisite.
>   - **새 todo trigger — `todo-prompt-compaction.md`**: Promaker system prompt ~25K 토큰 압축. 모든 free tier provider 의 TPM 한도 / Cerebras 8K context cap 영향 = 본 phase 의 횡단 작업으로 분리.
>   - **F-1 cleanup 미결정 (사용자 단독)**: spike 코드 (`feature/groq` branch, +46 line / 2 파일) 보존 vs memo-only 전환.
>   - **D-γ-pre 풀이 trigger**: §4 회귀 측정 metric 의 "clarification 비율" 에 *unnecessary self-verification* sub-category 추가 검토 (NewSystem2 ×2 케이스로 입증).
>   - **retry layer 미결정**: TPM 12K 즉시 거부 (HTTP 413, retry 무관) + Llama 4 Scout 17B 환경에서 한도 미도달 → spike 환경에서 발현 안 됨. 추가 spike (`llama-3.1-8b-instant` 빠른 연속 호출) 또는 F-7 진입 시점 자연 검증으로 이연.
> - 다음 리비전 trigger: F-1 cleanup 결정 / `todo-hotfix-llm.md` 작업 진입 / `todo-prompt-compaction.md` 신설 / D-γ-pre 풀이 / 결정 대기 풀이 / NuGet `Microsoft.Extensions.AI.Google` 등장 시 / xAI Grok consent 정책 변경 시 / 2026-06-19 Gemini key restriction 정책 시행 시 / DeepSeek 카드 정책 변경 시.

## 작업 목표

`Ds2.LlmAgent` 의 `ILlmProvider` 추상화에 **무료 사용 가능한 LLM provider** 를 추가 통합하여, 현재 Phase 2 의 5종 (Claude CLI / Codex CLI / Anthropic API / OpenAI API / Ollama) 외에 무료 옵션을 제공.

## 진행 상태

| 단계 | 상태 |
|---|---|
| D-α 결정 (Phase 분리/일괄) | ✅ rev 6 — (a) 분리 채택 (Groq 단독 spike 우선) |
| 결정 대기 4건 (D-β/γ-pre/γ-post/δ/ε) | ❌ 미진입 — 사용자 confirm 필요 |
| F-1 (Groq spike) | ⚠️ 1차 manual smoke 완료 — Groq endpoint 동작 / 21 tool / LlmEvent 5종 통과. retry layer 미결정 + cleanup 미결정 |
| F-2-infra (검증기 / 분류기 / 측정 hook) | ❌ |
| F-2-baseline (Claude 4.6 + Groq 측정) | ❌ |
| F-3 (`ProviderCapabilities` + `ProviderConnection` record) | ❌ |
| F-4 (factory 일반화 + key 슬롯, atomic) | ❌ |
| F-5a/b/c/d (Settings UI 4 sub-task) | ❌ |
| F-6 (enum / dispatch / DriftTest) | ❌ |
| F-7a/b/c/d/e (Cerebras / OpenRouter / SambaNova / Gemini 조건부 / DeepSeek·Z.AI 조건부) | ❌ |
| F-8 (Gemini smoke test 미통과 시 별도 phase) | ❌ |
| F-9 (GitHub Models, 별도 phase) | ❌ |

## 다음 작업 진입 권장 순서 (rev 6)

1. **F-1 cleanup 결정** (사용자 단독, **즉시 진입 가능**):
   - **(a) 코드 보존**: `feature/groq` branch 의 spike 코드 (+46 line / 2 파일) 그대로 두고 F-2/F-3/F-4 진입 시 정식 schema 로 점진 승격
   - **(b) memo-only 전환**: spike 코드 revert + F-4 commit 에서 처음부터 정식 schema 로 작성. 본 todo 본문 (§F-1 산출 절) 만 결과 보존
2. **`todo-hotfix-llm.md` 진입** (rev 2) — Issue 2 (단일 line, token reduce 효과) → Issue 1 옵션 B → 옵션 A 순. **F-7 진입 prerequisite** (모든 free tier provider 의 작은 모델에서 발현 보장).
3. **`todo-prompt-compaction.md` 신설** (사용자 합의) — Promaker system prompt ~25K → 압축. Cerebras 8K context cap + Groq TPM 12K 등 free tier 호환 prerequisite.
4. **D-γ-pre 풀이** — §"4. 회귀 측정 metric" (4종 vs 부분 채택 vs 별도 fixture). *unnecessary self-verification* sub-category 추가 검토 (rev 6 입증 trigger).
5. **D-β 풀이** — refactoring 범위 (F-4 묶음 vs F-4/F-6 분리).
6. **F-2-infra → F-2-baseline → D-γ-post → F-3 + F-4 atomic** → **F-5a (F-4 와 묶음 가능)** → **F-5b/c/d** → **F-6 (DriftTest 포함)** → **F-7a/b/c** → **F-7d / F-8 분기** (D-ε 풀이 직전) → **F-7e** → **F-9**.

> **rev 6 변경**: F-1 산출은 §"F-1 spike 산출" 절에 정리 완료. 결정 대기 풀이는 D-α (a) 분리 채택 후 D-β/γ-pre/δ/ε 4건 남음. sanity check 항목은 §"검증된 사실" 절 하단 표 그대로 유효 (변경 없음).

## F-1 spike 산출 (rev 6 — Groq + Llama 4 Scout 17B 1차/2차 manual smoke)

### ✅ 통과 항목

| 항목 | 결과 |
|---|---|
| OpenAI SDK Endpoint override | ✅ `OpenAIClientOptions.Endpoint = https://api.groq.com/openai/v1` 정상 통과 (`OpenAIClient(ApiKeyCredential, OpenAIClientOptions)` ctor 검증됨) |
| Microsoft.Extensions.AI 어댑터 | ✅ `IChatClient.GetStreamingResponseAsync` + `ChatClientBuilder.UseFunctionInvocation` multi-turn loop 정상. tool result 후 LLM 재호출 자동 진행 |
| MCP HTTP self-call (21 tool) | ✅ `tools=21 mcp=0` (mcp server count 0, tool 21 노출). Groq 가 OpenAI tool calling 포맷 그대로 수용 |
| LlmEvent 매핑 | ✅ SessionStarted + AssistantDelta + ToolUse + ToolResult + SessionEnd 모두 노출 (`turn 종료 — 124244ms, $0.0000, stop=stop, denials=0`) |
| Tool 시그니처 정확도 | ✅ `add_project` / `add_active_system` / `apply_operations` / `list_projects` / `list_systems` / `describe_subtree` 모두 정확한 시그니처로 호출 |

### ⚠️ Provider/모델 무관 (architectural) — `todo-hotfix-llm.md` 로 분리

- **Issue 1**: ImportPlan queued vs LLM 동기 검증 mismatch (결정 7 (d) side-effect, 작은 모델에서 발현). **2회차 spike 에서 NewSystem2 ×2 중복 생성 으로 입증** (사용자 데이터 오염 위험).
- **Issue 2**: Promaker UI 한글 `\uXXXX` escape 미해석 (`ApiChatProvider.ExtractToolResult` JsonSerializer default Encoder).

본 phase 의 F-7 진입 *전* 또는 *병행* 처리 권장. 상세 = `todo-hotfix-llm.md` (rev 2).

### ⚠️ Groq / Llama 4 Scout 17B specific 발견

| 발견 | 영향 |
|---|---|
| Groq free tier `llama-3.3-70b-versatile` TPM 12,000 < Promaker system prompt ~25K → 단일 호출 HTTP 413 (retry 무관, 영구 거부) | F-7 의 Groq 모델 선택 (TPM 큰 모델 한정) + `todo-prompt-compaction.md` 신설 trigger |
| Groq Llama 4 모델 ID = **`meta-llama/llama-4-scout-17b-16e-instruct`** (prefix 누락 시 HTTP 404 `model_not_found`) | F-7 의 모델 candidates 배열 작성 시 정확한 prefix 사용 |
| Llama 4 Scout 17B = Groq **Preview Model** ("evaluation purposes only, not for production") | F-7 의 Groq 정식 모델 선택 시 stable 모델 (`llama-3.3-70b-versatile` / `llama-3.1-8b-instant`) 우선 |
| **Llama 4 Scout 17B class = Promaker modeling 작업에 부족** — NewSystem2 ×2 중복 생성, 자기 첫 mutation 결과 (id=ab18a871) context 누락하고 두 번째 (id=fb88e232) 만 보고. architectural Issue 1 의 모델 quality 영향 직접 입증 | F-2-baseline 의 모델 품질 측정 → free tier 의 70B+ 만 실용 가능 결론. 그러나 70B+ 는 TPM 12K 한도 → system prompt 압축 prerequisite |

### ⚠️ retry layer 미결정 (rev 6 시점)

F-1 spike 의 핵심 산출 후보였던 **429/Retry-After backoff layer 결정** (provider / `ApiChatProvider` / `Microsoft.Extensions.AI` 미들웨어 4 후보) 는:
- TPM 12K 즉시 거부 (HTTP 413) = **retry 무관** (요청 자체가 한도 초과)
- Llama 4 Scout 17B 로의 전환 후에는 한도 미도달 → spike 환경에서 retry layer 발현 안 됨

→ 추가 spike (`llama-3.1-8b-instant` 빠른 연속 5~10회 호출) 또는 F-7a/b/c 진입 시점 자연 검증으로 이연.

### ⚠️ 새 todo trigger — `todo-prompt-compaction.md` (system prompt 압축)

Promaker system prompt = `Apps/Promaker/Promaker/LlmAgent/Prompts/1.entities.md` + `2.modeling.md` + `3.tooling.md` + 21 tool descriptions concat ≈ **~25K 토큰**. 모든 free tier provider 의 한도와 충돌:

| Provider / 모델 | Free tier 한도 | 25K 충돌 여부 |
|---|---|---|
| Groq llama-3.3-70b-versatile | TPM 12,000 | ❌ 영구 거부 (rev 6 입증) |
| Groq llama-3.1-8b-instant | TPM ~30,000 (추정) | ⚠️ 단일 호출 가능, 분당 1회 한도 |
| Groq meta-llama/llama-4-scout-17b-16e-instruct | TPM ~30,000 (rev 6 검증) | ⚠️ 단일 호출 가능 + 모델 품질 부족 |
| Cerebras 8K context cap (free tier) | context cap 8,192 | ❌ context 자체 초과 |
| Gemini 2.0 Flash 무료 | TPM 미공개 (RPM/RPD 한정) | 추가 확인 필요 |
| 기타 (OpenRouter / SambaNova / DeepSeek / Z.AI) | provider 별 상이 | 추가 확인 필요 |

→ **`todo-prompt-compaction.md` 별도 todo 신설 권장**. F-7 진입 *전* 또는 *병행*. 본 phase 의 횡단 작업.

## 검증된 사실 (직접 source 검증 완료, rev 4 stale 인용 정정)

| 사실 | 파일:line | 의미 |
|---|---|---|
| `ApiChatProvider` 가 SessionStarted 도 yield | `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs:113-117` | 첫 turn (`firstTurn = _sessionId == null`) 에 `LlmEvent.NewSessionStarted` yield. 신규 provider 도 5종 매핑 (SessionStarted + AssistantDelta + ToolUse + ToolResult + SessionEnd) 준수 필수. (`LlmEvent` DU 자체는 8종 — Thinking / RateLimitEvent / ProviderError 미사용은 phase 외) |
| `ApiChatProvider` 의 SystemPrompt 주입 | `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs:109` | `_history.Add(new ChatMessage(ChatRole.System, _systemPrompt));` — firstTurn 분기 안 SessionStarted yield 직전. system prompt 는 turn-stable 하게 history 첫 entry 로 굳음. 신규 OpenAI 호환 provider 모두 자동 흡수 (Microsoft.Extensions.AI 추상화) |
| Claude CLI 의 SystemPrompt 주입 채널 | `Solutions/Core/Ds2.LlmAgent/ClaudeCliArgs.fs:54-84` + `ClaudeCliProvider.fs:33-52` | **`--append-system-prompt-file <path>`** 인자 (임시 파일 경유). 본 phase 의 신규 provider 는 모두 HTTP API → 본 채널 무관 |
| `AvailableProviders` 별도 SSOT | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:112-119` | `LlmProviderKind` enum (line 23-30) 외에 ComboBox 바인딩용 `IReadOnlyList<LlmProviderKind>` 별도 존재. **F-6 진입 시 enum + AvailableProviders + switch dispatch 3 SSOT 동시 갱신** (drift 회피). `LlmProviderKindDriftTest` 신설 (F-6 sub-task) |
| `LlmConfig` 모델 / URL property 위치 | `Apps/Promaker/Promaker/LlmAgent/LlmConfig.cs:66-76` | `AnthropicModel` / `OpenAiModel` / `OllamaModel` / `OllamaBaseUrl` 4 property. F-4 에서 신규 provider 별 추가 |
| `EncryptedKeys` Dictionary 구조 | `Apps/Promaker/Promaker/LlmAgent/LlmConfig.cs:62-64` | `Dictionary<string, string>` (provider key → DPAPI ciphertext). 구조 자체는 확장 가능 — 신규 key 상수 (`GroqKey` 등) 만 추가하면 됨 |
| Settings UI 가 Anthropic/OpenAI key + 3 model + Ollama URL 만 처리 | `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml.cs:208` (`LoadLlmTab`) / `:314` (`SaveLlmTab`) | 신규 provider 진입점 노출 안 됨. F-5a 의 핵심 작업 위치 |
| OpenAI 2.10.0 SDK 의 endpoint 속성 | `OpenAIClientOptions.Endpoint` | "BaseAddress 파라미터화" 가 아니라 **"Endpoint 파라미터화"** 가 정확 — F-4 작업 시 wording 통일 |
| `todo-extend-mcp.md` 가 L=Layer 의미 점유 | `doc/todo-extend-mcp.md` rev 1~15 본문 + §5.2/5.3/5.4 | 동일 폴더 내 동음이의어 회피 — 본 todo 는 **F-1 ~ F-9** (Phase Free) 사용 |
| `ImportPlanBuilder` mutation 1 undo step 정책 (결정 7) | `Solutions/Core/Ds2.Editor/Editor/ImportPlanApply.fs:48-52` | `applyWithUndo` 정의 + `WithTransaction` + for loop + `EmitRefreshAndHistory` 1회. (rev 4: `:46-50` 인용 → `:48-52` 정정 — line 46 은 `invalidOp` RenameEntity 분기) |
| dispatcher policy (결정 8) | `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.Rebuild.cs:41/50/68` | (rev 4: `MainViewModel.cs:685-` 245줄 파일에 존재 불가 → partial class `Rebuild.cs` 정정). line 41 `RequestRebuildAll` 정의, line 50 `_dispatcher.BeginInvoke`, line 68 `DispatcherPriority.Background`. `IUiDispatcher.InvokeAsync` Background priority. AssistantDelta 만 dispatcher 우회 |
| MCP 21 tool SSOT (server-side allowlist) | `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs:16-39` | `[McpServerToolType]` 등록된 21 tool **만** 노출. provider 추가 시 tool 이름 변경 X — `PromakerToolNamesDriftTests` 회귀 영향 없음. **CLI 의 `--allowed-tools` client-side allowlist 와 달리 HTTP API 는 server-side allowlist 로 자연 충족** |
| self-check 항목 카운트 | `Apps/Promaker/Promaker/LlmAgent/Prompts/2.modeling.md:431-448` | (rev 4: "16" → **"18"** 정정 — `^- \[` grep 직접 검증). F-2-infra 의 룰 위반 자동 감지기는 18 항목 base |

## 미정 결정 (D = Decision-pending) ↔ phase 의존 그래프

| 결정 ID | 결정 내용 | 결정 trigger | 영향 phase |
|---|---|---|---|
| **D-α** | **Phase 분리 vs 일괄** — Groq 1개만 우선 (분리) / OpenAI 호환 N개 동시 추가 (일괄) | 사용자 단독 confirm | F-1 (분리 시 진입 즉시 / 일괄 시 F-3 와 묶음) |
| **D-β** | **Refactoring 범위** — `ApiProviderFactory` Endpoint 파라미터화 + `LlmChatViewModel` dispatch 일반화를 F-4 + F-6 분리 vs F-4 묶음 | 사용자 단독 (D-α 결정 후) | F-4 / F-6 |
| **D-γ-pre** | **회귀 측정 metric 후보 합의** — 본 todo §"4. 회귀 측정 metric" 4종 채택 / 일부 채택 / 별도 fixture 정의 (F-1 spike 진입 prerequisite) | 사용자 단독 | F-2-infra |
| **D-γ-post** | **F-1 spike 결과 반영 metric 확정** — capabilities 차이 / retry layer 결정 후 metric 최종화 (rev 4 분할: 순환 dependency 해소) | 사용자 + 본인 plan (F-1 spike 결과 본 후) | F-2-baseline |
| **D-δ** | **Consent 다이얼로그 갱신 범위** — 신규 provider 별 별도 동의 / 통합 동의 / 기존 동의 본문 단순 갱신 | 사용자 단독 (개인정보 정책 영역) | F-5d |
| **D-ε** | **`Microsoft.Extensions.AI.Google` 패키지 정합성** — `GeminiDotnet.Extensions.AI` (0.14.1) / `Google_GenerativeAI.Microsoft` (2.7.0) / OpenAI 호환 endpoint 단독 사용 중 선택 | F-8 진입 시점 spike 결과 기반 | F-8 |

**의존 흐름 (rev 4, 순환 해소)**: D-α → (D-β + D-γ-pre) → F-1 spike → D-γ-post → F-2-infra → F-2-baseline → F-3/F-4 (atomic) → F-5a (F-4 묶음 가능) → F-5b → F-5c → F-5d (D-δ 풀이 직전) → F-6 (DriftTest 포함) → F-7a/b/c → F-7d / F-8 분기 (D-ε 풀이) → F-7e → F-9.

## 무료 provider 후보 정리

> **주의 (rev 3)**: 외부 한도 / 정책은 자주 변경되므로 통합 직전 각 provider docs 재확인 필수. 본 표는 **2026-05-08 기준** reviewer 검증치 반영 (출처: 본 문서 말미 "참조 sources" — rev 4 access date 부착).

### A. 클라우드 무료 티어 — OpenAI 호환 endpoint (통합 비용 최소)

| Provider | Endpoint | 무료 한도 (2026-05-08) | 모델 예시 |
|---|---|---|---|
| **Groq** | `https://api.groq.com/openai/v1` | **모델별 차등 + TPM 가장 빡빡** — 일반: 30 RPM / 14,400 RPD. `llama-3.3-70b-versatile`: 30 RPM / **1,000 RPD / TPM 12,000** (Promaker 25K prompt 단일 호출 ❌ HTTP 413 — rev 6 입증). `qwen/qwen3-32b`: 60 RPM / **1,000 RPD / TPM 6,000**. `meta-llama/llama-4-scout-17b-16e-instruct` (**Preview Model**): TPM 추정 30K (rev 6 검증됨) | Llama 3.3 70B, **`meta-llama/llama-4-scout-17b-16e-instruct`** (prefix 필수 — rev 6), DeepSeek R1 Distill, Qwen QwQ 32B (Mixtral = 2025-03-05 deprecate) |
| **Cerebras** | OpenAI 호환 | 30 RPM / **1,000,000 TPD / 8,192 ctx cap** (free tier, 모델별) | `gpt-oss-120b`, `llama3.1-8b`, `qwen-3-235b-a22b-instruct-2507`, `zai-glm-4.7` |
| **SambaNova Cloud** | OpenAI 호환 | **Free production: 20 RPM / 20 RPD / 200K TPD**. 60–240 RPM 행은 결제수단 등록한 developer tier (무료 아님) | Llama, DeepSeek, Qwen |
| **OpenRouter** | OpenAI 호환 (`:free` suffix 모델) | **20 RPM / 50 RPD** (계정 크레딧 기반, 모델별 X). $10 이상 크레딧 구매 시 1,000 RPD 로 상향 | DeepSeek R1, Llama 등 |
| **Google AI Studio (Gemini)** | **OpenAI 호환** `https://generativelanguage.googleapis.com/v1beta/openai/` (streaming + function calling 지원) | Gemini 2.0/2.5 Flash 무료 등급 (분당 15 / 일 1,500 부근, 모델별) | Gemini 2.0/2.5 Flash, Pro |
| **DeepSeek (Direct API)** | OpenAI 호환 | 가입 시 5M tokens 30일 무료 (소진 후 유료). **단 API 호출 시점 $2 최소 잔액 요구 — 카드 등록 의무 (rev 5)** | DeepSeek V3 / R1 |
| **Z.AI (GLM)** | OpenAI 호환 | GLM-4.5/4.7 Flash **영구 무료** | GLM-4.5 Flash, GLM-4.7 Flash |

> **Gemini 주의**: OpenAI 호환 endpoint 가 streaming + function calling 을 공식 지원하지만, MCP HTTP self-call 경유 tool 호출은 **smoke test 필요** (특히 partial JSON delta + `tool_use_id` 매칭 동작). 호환 확인되면 일반화된 OpenAI 호환 경로 그대로 사용 가능. 미통과 시 D-ε 결정 (별도 SDK 도입) 트리거. PASS 기준은 §"F-7d PASS 기준" (M8) 참조.
>
> **Gemini API key 정책 변경 (rev 5, 2026-06-19 시행)**: unrestricted traffic key 폐지 → key 발급 시 referrer / IP 제한 추가 의무화. Promaker 가 사용하는 key 는 **API restriction = "Generative Language API"** + **Application restriction = none (또는 IP 화이트리스트)** 로 발급 안내. F-5c 의 `GET /models` ping 구현 시 401/403 응답에 "key restriction 점검" 안내 분기 추가.

### A1. API key 발급 / 회원가입 요건 (rev 5)

본 phase 의 모든 provider 는 **API key 필수 + 사용자 직접 콘솔 가입 의무**. 신용카드 요구 여부 / 가입 채널이 다름. F-5a (XAML Row) / F-5c (ping 검증) / F-5d (Consent 본문) 작업 시 본 표를 사용자 안내 문구의 SSOT 로 사용.

| Provider | 신용카드 | 가입 채널 | 발급 위치 (URL) | 비고 |
|---|---|---|---|---|
| **Groq** | 불필요 | 이메일 / Google / GitHub | https://console.groq.com/keys | 가장 단순 — 60초 내 가입 + key 발급 |
| **Cerebras** | 불필요 | 이메일 | Cerebras Platform 콘솔 | 이메일만으로 즉시 free tier 진입 |
| **OpenRouter** | 불필요 (free 모델 한정) | 이메일 / Google / GitHub | https://openrouter.ai → Keys | $10 크레딧 충전 시 RPD 상향 — 본 phase 무관 |
| **SambaNova** | 불필요 | 이메일 | https://cloud.sambanova.ai | $5 초기 크레딧 (30일) + free tier 영구 지속 |
| **Gemini (Google AI Studio)** | 불필요 | **Google 계정 필수** | https://aistudio.google.com/app/apikey | 2026-06-19 부터 key restriction 의무 (위 §A 비고 참조) |
| **DeepSeek (Direct API)** | ⚠️ **$2 최소 잔액** | 이메일 | https://platform.deepseek.com | F-7e 진입 시 사용자 사전 안내 의무 |
| **Z.AI (GLM)** | 불필요 (Flash 모델) | 이메일 | Z.AI 플랫폼 / Zhipu AI 오픈플랫폼 | Flash 모델 영구 무료 — 다른 모델은 별도 결제 |

### B. 무료 사용 불가 / 제외

| Provider | 사유 |
|---|---|
| **Together AI** | **무료 trial 폐지** (현행 docs). 최소 $5 크레딧 구매 필수 → 본 phase 제외 |
| **Hugging Face Inference API** | **credit 기반으로 전환** (구 무료 endpoint 폐지). 본 phase 제외 |
| **xAI Grok** | 데이터 공유 동의 필수 — Consent 정책 충돌 (상세 §5 단일화). 본 phase 제외 |

### C. 별도 SDK / 어댑터 필요 — 우선순위 후순위

| Provider | SDK / 어댑터 | 비고 |
|---|---|---|
| **Mistral La Plateforme** | OpenAI 호환 또는 `Mistral.SDK` | "Experiment" 무료 티어, **분당 2 req** (rev 2 의 "분당 1" 정정). OpenAI 호환 endpoint 가 partial 일 가능성 (function calling SDK 차이 spike 시 결정) |
| **GitHub Models** | `Azure.AI.Inference` SDK | GitHub 계정으로 GPT-4o/Llama/Phi/Mistral. Copilot 구독자 우대 |
| **Cloudflare Workers AI** | 자체 REST | edge inference, 일 10,000 neuron |
| **NVIDIA NIM (build.nvidia.com)** | OpenAI 호환 | 1,000~5,000 req 가입 크레딧 (소진 후 유료) |

### D. 로컬 실행 (이미 통합 / 참고)

- **Ollama** — 이미 통합됨 (`OllamaSharp 5.4.25`)
- LM Studio / llama.cpp / vLLM / TGI — 모두 OpenAI 호환 endpoint 노출 가능 (Ollama 와 동일 통합 패턴 재사용 가능)

### E. CLI 구독 (이미 통합)

- **Claude Code** — `ClaudeCliProvider` 활용 중
- **Codex CLI** — `CodexCliProvider` 활용 중

## 우선순위 권장 (rev 3)

1. **Groq** — OpenAI 호환 + 속도 우수 + tool calling 거의 OpenAI 동일. **F-1 spike 진입점** + 429/Retry-After 처리 동시 도입 (무료 티어 모델별 RPD 1K 한도 빡빡 → backoff 필수).
2. **Cerebras / OpenRouter / SambaNova** — F-7a/b/c sub-bullet 점진 추가 (incremental 자연스러움).
3. **Gemini** — OpenAI 호환 endpoint 사용 가능 → F-7 묶음 가능 여부는 F-1/F-2 결과 기반 결정. MCP smoke test 통과 시 F-7d 로 편입, 미통과 시 F-8 (별도 phase + D-ε).
4. **DeepSeek / Z.AI** — F-7e (F-7a/b/c 완료 후 사용자 합의 시 진입), 모델 품질은 F-2-baseline 에서 옵션으로 측정.
5. **Mistral / GitHub Models** — F-9 별도 phase.

## 통합 시 고려 사항

### 1. OpenAI 호환 provider 일반화 (Endpoint 파라미터화)

- 현재 `ApiProviderFactory.cs:38` 의 `CreateOpenAiAsync` 가 `new OpenAIClient(apiKey)` default endpoint 사용 → **`OpenAIClientOptions { Endpoint = ... }` 받는 overload** 로 일반화. (rev 2 의 "BaseAddress 파라미터화" wording 정정 — OpenAI 2.10.0 SDK 정확 속성명은 `OpenAIClientOptions.Endpoint`)
- **`ProviderCapabilities` + `ProviderConnection` 2-record 분리** (M6 + rev 4 C4) — 정적/불변 (capabilities) ↔ 런타임/사용자 입력 (connection) lifetime 분리. **API provider 한정** (CLI provider 는 자체 인자 / 구독 / 인증 모델 유지):
  ```fsharp
  // Solutions/Core/Ds2.LlmAgent/LlmProvider.fs (F# 정의 — 결정 2 정합)
  // C# pattern matching cost 회피 위해 DU 대신 enum 사용 + [<CLIMutable>] 부착

  type UsageLocation =
      | LastChunk          = 0   // OpenAI 표준 — 마지막 chunk 의 usage
      | SeparateUsageEvent = 1   // Anthropic — 별도 event
      | NotEmitted         = 2   // streaming usage 미지원

  type SystemPromptStyle =
      | ChatRoleSystem    = 0   // OpenAI / Groq / Cerebras / OpenRouter / SambaNova / Gemini OpenAI 호환
      | DeveloperMessage  = 1   // OpenAI o1/o3 (developer role)
      | TopLevelField     = 2   // Anthropic API (system top-level field)
      | InstructionsField = 3   // Gemini native (system_instruction)

  [<CLIMutable>]
  type ProviderCapabilities = {  // 정적 / 컴파일 시점 결정 — API provider 한정
      DisplayName        : string
      DefaultEndpoint    : Uri              // SDK default — connection.OverrideEndpoint 가 None 이면 사용
      EndpointConfigurable : bool           // Ollama BaseUrl 같이 사용자 override 가능 여부
      SupportsToolChoice : bool             // tool_choice 필드 지원 여부
      StreamingUsage     : UsageLocation    // last-chunk vs separate event
      SystemPromptStyle  : SystemPromptStyle
      StrictTools        : bool             // strict tool schema 지원
      MaxOutputTokens    : Nullable<int>
  }

  [<CLIMutable>]
  type ProviderConnection = {  // 런타임 / 사용자 입력
      ApiKey            : string
      ModelId           : string             // 사용자 선택 가능 → connection 영역
      OverrideEndpoint  : Uri                // EndpointConfigurable=true 인 경우만 의미 (else null)
  }
  ```
- F-3 (record 도입) + F-4 (factory + key 슬롯) **atomic 묶음 commit** — 분리 시 schema 가 두 번 깨짐. F-4 는 F-5a 와도 묶음 가능 (config schema ↔ UI binding 단일 commit).

### 1A. System Prompt 전달 채널 차이 (rev 4 M1)

신규 provider 통합 시 system prompt 가 어느 채널로 흐르는지 명시. CLI / API 채널 분리는 `ApiChatProvider` 어댑터 vs `ClaudeCliProvider` / `CodexCliProvider` 의 별도 path 로 흡수 — `ProviderCapabilities.SystemPromptStyle` 은 **API provider 한정** 흡수.

| Provider | 채널 | role / 필드 | 비고 |
|---|---|---|---|
| Claude CLI | child process arg (임시 파일 경유) | n/a (process input) | `--append-system-prompt-file <path>` (`ClaudeCliArgs.fs:84`). `--allowed-tools` 별도 client-side flag |
| Codex CLI | rollout jsonl + 위치 인자 | n/a | sandbox + cd 격리 (`LlmChatViewModel.cs:265`). system prompt 분리 인자 없음 (prompt 본문에 prepend 검증 필요) |
| Anthropic API | `system` top-level field | (별도 필드, role 아님) | `Microsoft.Extensions.AI` 추상화가 흡수 |
| OpenAI Chat Completions | `messages[0].role = "system"` | system | o1/o3 = `developer` role |
| Groq / Cerebras / OpenRouter / SambaNova | OpenAI 호환 동일 | system | `ChatRoleSystem` case 흡수 |
| Gemini OpenAI 호환 endpoint | `messages[0].role = "system"` | system | streaming + function calling smoke test 필요 (D-ε trigger) |
| Gemini native | `system_instruction` top-level | (별도 필드) | `InstructionsField` case — D-ε 시 SDK 선택 따라 진입 |

### 2. MCP HTTP tool 호환성

- 21개 `mcp__promaker__*` tool 노출 (`PromakerToolNames.cs`). OpenAI 호환 provider 라도 tool calling streaming 형태가 SDK 별 미묘히 다름 (`tool_use_id` 매칭 / partial JSON delta).
- Groq/Cerebras/OpenRouter 는 OpenAI tool calling 거의 동일 → 무난 예상.
- Gemini 는 `functionCall` 포맷 차이 → `Microsoft.Extensions.AI` 추상화에 의존. F-8 smoke test 의 핵심 검증 항목.
- **F-1 spike 산출** = provider 별 tool calling 호환 매트릭스.
- **CLI `--allowed-tools` ↔ HTTP API server-side allowlist** (rev 4 M2): HTTP API 는 **MCP HTTP 가 노출하는 tool 풀 자체가 21개로 한정** (`ModelTools.cs` 의 `[McpServerToolType]` 등록 + `PromakerToolNames` SSOT) → 별도 client-side allowlist flag 불필요. tool 추가 시 `PromakerToolNames.cs` SSOT 가 자동 적용. 단, provider 가 임의 tool 명을 hallucinate 해 호출 시도하면 `ApiChatProvider` 의 `FunctionCallContent.Name` 검증에서 reject 필요 — F-1 spike 항목에 추가.

### 2A. F-7d PASS 기준 (Gemini smoke test, rev 4 M8)

F-7d (F-7 편입) ↔ F-8 (별도 phase) 분기 시 partial-pass grey zone 회피용 **3 항목 모두 통과** binary 기준:

1. **streaming delta 정상** — `IChatClient.GetStreamingResponseAsync` 가 `ChatResponseUpdate` chunk 를 끊김 없이 반환 (text content + tool call argument delta 모두).
2. **`tool_use_id` 매칭 정상** — partial JSON delta 가 동일 `Id` 로 묶임 (`FunctionCallContent.CallId` 일관성).
3. **21 tool 모두 invocation 가능** — MCP HTTP self-call 경유 21 tool 1회 이상 성공 호출 (clarification turn 제외, 인자 정확도 100%).

3 항목 모두 통과 시 F-7d (F-7 편입). ②③ 중 1개라도 실패 시 F-8 (별도 phase + D-ε 풀이).

### 3. Rate limit 대응

- 무료 티어는 분당 한도 빡빡. `LlmTurnContext.cs` 의 `mutation quota=50` 외에 *provider 측 rate-limit retry* 정책 (현재 `ClaudeCliProvider` 의 `RateLimitEvent` 와 같은 처리) 가 API provider 에는 없음.
- **F-1 spike 의무**: `Microsoft.Extensions.AI` 자체 retry policy 가 SDK 어느 layer 에서 처리하는지 spike (provider layer / `ApiChatProvider` layer / `ChatClientBuilder` 미들웨어 / 사용자 코드 layer 4 후보).
- `ApiChatProvider` 에 429 / Retry-After backoff 추가 (spike 결과 기반 위치 결정).
- **명시적 거부**: **multi-key rotation** (provider ToS 위반) 채택 안 함. 무료 한도 초과 시 fail-fast + 사용자 안내.

### 4. 회귀 측정 metric (M5 — F-2-infra 의 측정 도구 작성 + F-2-baseline 의 측정 항목 정의)

신규 provider 추가 시 모델 품질 trade-off 측정용. Promaker 도메인 (Ds2 entity 모델링 + MCP tool orchestration) 중심.

| metric | 정의 | 측정 방법 | **측정 인프라 작성 비용 (F-2-infra)** |
|---|---|---|---|
| **룰 위반 카운트** | `Prompts/2.modeling.md` §3 결정 트리 / §5 self-check 의 **18 항목** (rev 4: "16" → 18 정정) 위반 수 | fixture prompt set N개 → 응답 → 위반 자동 감지 | **High** — `Solutions/Tests/Ds2.LlmAgent.Tests/Fixtures/RuleViolationAnalyzer.fs` 신설 (18 항목 parser) |
| **tool 시그니처 정확도** | 21 tool 호출 시 인자 타입 / 필수 인자 누락 / 불필요 인자 비율 | `LlmTurnContext.cs` mutation count + ApplyImportPlan 결과 비교 | **Low** — 기존 mutation count 활용 |
| **clarification 비율** | LLM 이 clarification 질문 turn 비율 (별도 metric, **noise 가 아님** — `feedback_clarification_not_noise.md` 정합) | turn 분류 (mutation / read / clarification / chat) | **Mid** — turn 분류 heuristic 신설 (또는 사용자 수동 분류) |
| **undo step 일관성** | turn 당 undo step = 1 정책 (결정 7) 위반 여부 | `EmitRefreshAndHistory` 호출 횟수 | **Low** — `EmitRefreshAndHistory` 호출 hook 추가 |

`PromakerToolNamesDriftTests` 류 패턴으로 fixture 영속화 (`Solutions/Tests/Ds2.LlmAgent.Tests/Fixtures/`). 측정 비용이 측정 가치보다 큰 metric 은 D-γ-pre 단계에서 제외 검토.

### 5. Consent / 보안 정책

- `LlmConfig.cs` 의 `EnsureGranted` 다이얼로그가 "파일 시스템 경로 등 전송 X" 약속.
- 무료 provider 추가 시 **동일 정책 적용** + DPAPI 보관 키 entry 추가 필요.
- Codex 의 `danger-full-access` sandbox 와 같은 별도 약속이 필요한 provider 는 별도 consent 분리 검토.
- **xAI Grok 정책 충돌 (rev 4 단일화)**: 데이터 공유 동의 필수 → 본 phase 의 "파일 시스템 경로 등 전송 X" 약속과 직접 충돌. 본 phase 제외 (별도 consent 분리 진행 시 재평가).
- **API key 발급 책임 chain (rev 5)**:
  - 모든 provider key = 사용자 직접 콘솔 가입 → 발급 → Settings UI 입력 → DPAPI 보관. (§A1 표 SSOT)
  - Promaker 측은 key 를 *발급* 또는 *프록시* 하지 않음. provider 정책 변경 (e.g. Gemini 2026-06-19 key restriction 의무화 / DeepSeek $2 잔액 요구) 은 사용자 콘솔 측 정책이며 Promaker 코드 변경 없이 사용자 안내만 갱신.
  - F-5d Consent 본문 갱신 시 §A1 표를 inline 또는 Tooltip 으로 노출 (provider 별 "콘솔 가서 가입 후 key 입력" 안내 + Hyperlink 부착).
- **F-5c Settings UI fallback (M9)**:
  - 키 미입력 상태 동작: 해당 provider 행 회색 / `IsEnabled=false` + Tooltip "API key 미입력 — 콘솔 가입 후 발급한 key 입력" + 인라인 입력 시 즉시 활성화.
  - 키 검증 endpoint ping: provider 별 `GET /models` (또는 동등 가벼운 endpoint) 호출 → 200 응답 시 ✅ 표시. Ollama 의 `LlmTestOllama_Click` (`ApplicationSettingsDialog.xaml.cs:282`) 패턴 재사용.
  - **401/403 응답 분기 (rev 5)**: Gemini = "key restriction 점검 필요 (2026-06-19 정책)" / 기타 provider = "key 무효 또는 quota 초과" 안내 문구.
  - **402 응답 분기 (rev 5, DeepSeek 한정)**: "$2 최소 잔액 요구 — 콘솔에서 카드 등록 후 재시도" 안내.

### 6. 결정 1~9 의 신규 provider 영향 평가 (M8 — rev 4 압축)

영향 있는 결정 5건만 표에 기재 (영향 없는 결정 4건은 footer 1줄 처리).

| 결정 | 신규 provider 영향 |
|---|---|
| **결정 4 (HTTP MCP transport)** | **신규 provider 도 동일 in-process Kestrel + MCP HTTP self-call 패턴 준수** — `ApiProviderFactory.CreateMcpClientAsync` (`:75-`) 재사용 |
| **결정 5 (loopback nonce)** | **신규 provider 의 MCP HttpClient 도 동일 nonce header 부착** (`ApiProviderFactory.cs` 의 `McpNonceHeader` 패턴) |
| **결정 7 (ImportPlan 1 turn = 1 undo)** | 신규 provider 도 turn end 의 single `ApplyImportPlan` 호출 준수. `ApiChatProvider` 가 이미 `LlmTurnContext` 와 연동 — 신규 provider 가 `ApiChatProvider` 를 재사용하면 자동 준수 |
| **결정 8 (dispatcher Background)** | 신규 provider 의 stream loop 도 `IUiDispatcher.InvokeAsync` Background priority. AssistantDelta 만 dispatcher 우회 (`MainViewModel.Rebuild.cs:50/68` 패턴 재사용) |
| **결정 9 (`IAsyncEnumerable<LlmEvent>`)** | **신규 provider 도 `Send : (prompt: string * cancellationToken: CancellationToken) -> IAsyncEnumerable<LlmEvent>` 시그니처 준수** + LlmEvent 5종 매핑 (M2 — SessionStarted 누락 회귀 회피). 단, 신규 provider 가 *CLI 형태* 면 system prompt 주입 layer 별도 검토 (§1A 참조) |

> **영향 없음**: 결정 1 (dock panel — `LlmChatPanel.xaml` 내부 ComboBox 항목만 추가) / 결정 2 (F# DLL + C# binding — `ApiProviderFactory` C# 영역 유지, F-3 record 는 F# `LlmProvider.fs` 정의 후 enum interop) / 결정 3 (Phase 2 후속 — `todo-promaker-llm-agent.md` cross-link 검토 trigger) / 결정 6 (결정 2 로 흡수, 결정 7(d) 와 통합 — rev 4 wording 정정).

### 7. CLI vs API two-class 추상화 (rev 4 M11)

- **현재 비대칭**: `ILlmProvider` 구현체 = CLI 2종 (Claude/Codex) + API 3종 (Anthropic/OpenAI/Ollama). 본 phase 후 = CLI 2 + API 10 = **1 : 5** 비대칭 심화.
- **두 클래스의 본질 차이**:
  - **system prompt 채널**: CLI = `--append-system-prompt-file` / 위치 인자. API = `ChatRole.System` ChatMessage. (§1A 표 참조)
  - **tool allowlist 강제 채널**: CLI = `--allowed-tools` client-side flag (반복 인자). API = MCP server-side allowlist (21 tool `[McpServerToolType]` 등록).
  - **rate-limit retry 위치**: CLI = `RateLimitEvent` (provider layer). API = `Microsoft.Extensions.AI` middleware (F-1 spike 결정).
- **통합점 (rev 4 명시)**:
  - 결정 9 (`IAsyncEnumerable<LlmEvent>` + LlmEvent 5종 매핑) = **유일 강제 통합점**.
  - `ProviderCapabilities` / `ProviderConnection` record = **API provider 한정** (CLI 측 비호환 명시).
  - 향후 `IApiChatProvider` sub-interface 분리 trigger 가 본 phase *외* 에 발동 가능 — 현재는 `ApiChatProvider` 단일 어댑터 + `ProviderCapabilities` parameterization 으로 충분 (CLI : API = 1 : 5 단계까지). API 측 capability 분기가 5개 이상 case 로 확장되면 sub-interface 도입 재검토.

## 남은 할 일 목록

### 결정 대기 (D-α/β/γ-pre/γ-post/δ/ε)

위 "미정 결정 ↔ phase 의존 그래프" 절 참조.

### 구현 작업 (F-1 ~ F-9)

> **명명 컨벤션 (M3)**: `todo-extend-mcp.md` 가 L1=F# Core / L2=F# LlmAgent / L3=C# Promaker (Layer) 의미를 점유. 본 todo 는 동일 폴더 내 동음이의어 회피 위해 **F-1 ~ F-9** (Phase Free) 사용.

> **자가 검열 trigger 사전 명시 (rev 4 확장)**:
> - **F-3 + F-4 atomic** = 신규 함수/타입 3개+ + public API 일반화 → trigger ② + ⑤ 동시 충족.
> - **F-5a / F-5b / F-5c / F-5d** = 단일 파일 100 line 이상 변경 또는 2 이상 파일 동시 변경 → trigger ③ 충족 가능.
> - **F-6** = `LlmChatViewModel.cs` enum + dispatch 변경 = SSOT 갱신 → trigger ⑤ 충족.
> - 위 phase commit 직전 sub-agent (general-purpose) 위임 review 의무.

- [ ] **F-1** (spike-first): **Groq 단일 통합 spike** — `ApiProviderFactory.cs` 에 임시 `CreateGroqAsync` (env-var `GROQ_API_KEY`, LlmConfig schema 무수정) + 429/Retry-After backoff 동시 도입. tool calling smoke test (21 tool 21회 호출) + LlmEvent 5종 (SessionStarted 포함) 매핑 정합 검증 + `FunctionCallContent.Name` 의 21 tool 외 hallucinate reject 검증. **`ProviderCapabilities` 차이점 발견** + retry layer 결정 (provider / ApiChatProvider / Microsoft.Extensions.AI 미들웨어 4 후보 중) + Anthropic API `ReasoningContent` (Thinking) 발생 여부 검증. **cleanup 책임 (rev 4 M6)**: F-1 spike 코드는 **별도 branch 에서 작업, main 에 leak 금지**. F-4 commit 에서 정식 schema 경유로 대체 + `CreateGroqAsync` env-var 분기 삭제. F-1 단독 commit 시 spike 결과를 본 todo 본문에 적고 코드는 commit 하지 않는 memo-only spike 옵션 가능.
- [ ] **F-2-infra** (rev 4 M7 분할): 회귀 측정 인프라 작성.
  - [ ] **F-2-infra-a**: 룰 위반 자동 감지기 — `Solutions/Tests/Ds2.LlmAgent.Tests/Fixtures/RuleViolationAnalyzer.fs` 신설 (18 항목 parser, `2.modeling.md:431-448` 동기화).
  - [ ] **F-2-infra-b**: turn 분류기 (mutation / read / clarification / chat) — heuristic 신설.
  - [ ] **F-2-infra-c**: `EmitRefreshAndHistory` 호출 hook 추가 (undo step 카운트).
  - [ ] **F-2-infra-d**: fixture prompt set N개 작성 (N 결정 trigger — D-γ-pre 풀이 시 합의).
- [ ] **F-2-baseline** (rev 4 M7 분할): 측정 실행. Claude 4.6 + Groq `llama-3.3-70b-versatile` 두 케이스를 §"4. 회귀 측정 metric" 4종으로 측정. 다중 provider 추가 전 binary search 기반.
- [ ] **F-3** (atomic with F-4): **`ProviderCapabilities` + `ProviderConnection` 2-record 도입** — F-1 spike 결과 기반 차이점 흡수. `Solutions/Core/Ds2.LlmAgent/LlmProvider.fs` 에 정의 (F# enum + `[<CLIMutable>]` record, C# pattern matching cost 회피). **API provider 한정** 명시.
- [ ] **F-4** (atomic with F-3): **Factory 일반화 + Config schema** — `CreateOpenAiAsync` 의 `OpenAIClientOptions { Endpoint }` overload 도입. `LlmConfig.cs:62-76` 에 신규 provider key 상수 (`GroqKey` 등) + Model / Endpoint property + env-var fallback 정책 (DPAPI 우선 / env-var fallback) 추가. F-3 + F-4 단일 commit (schema 두 번 깨짐 회피). F-1 spike 의 임시 분기 동시 삭제.
  - **5-reviewer F-1 review (2026-05-09) 합의 사항 흡수**:
    - **R-1**: `CreateOpenAiCompatibleAsync(... Uri? endpoint = null, Capabilities? caps = null)` 단일화 — OpenAI / Groq 차이 (a) placeholder 키 (b) `OpenAIClientOptions.Endpoint` (c) `Capabilities` 3개. CLAUDE.md "재활용 90점" 정합. 향후 Together / Fireworks / DeepSeek 추가 = 3줄 wrapper.
    - **GroqModel property**: `LlmConfig.cs:66-76` 에 `GroqModel` 추가 (현재 spike 의 `meta-llama/llama-4-scout-17b-16e-instruct` 하드코딩 제거).
    - **즉시 조치 적용 후 잔존**: spike scope 안에서 (a) consent 문구 Groq 추가 (b) `_config.GetApiKey(GroqKey) ?? Env.Trim() ?? ""` 2-tier 정렬 (c) env-var Trim() 은 **Groq 한정** 적용 — F-4 합류 시 Anthropic / OpenAI 도 동일 Trim 패턴으로 정렬 검토.
- [ ] **F-5a** (F-4 와 묶음 가능): **XAML Row + Load/Save** — `ApplicationSettingsDialog.xaml(.cs)` LLM 탭에 **provider 1개** Row 추가 + `LoadLlmTab` `:208` / `SaveLlmTab` `:314` 확장. 첫 진입 후 패턴 확정.
- [ ] **F-5b**: **모델 candidates + ContextMenu** — 모델 candidates 배열 (`AnthropicModelCandidates` 등) 신규 provider 추천 모델 목록 추가. `LlmAnthropicCandidates_Click` `:228-235` 패턴 / `ShowCandidatesMenu` `:237` 재사용.
- [ ] **F-5c**: **GET /models ping + 회색 fallback** — 키 미입력 상태 회색 처리 + `GET /models` ping 검증 버튼 (Ollama 의 `LlmTestOllama_Click` `:282` 패턴 재사용). `LlmClear*Key_Click` `:269-270` 패턴.
- [ ] **F-5d**: **Consent 본문 갱신** — D-δ 풀이 결과 반영. xAI Grok 영구 제외 정책 명시 유지.
- [ ] **F-6**: **enum + dispatch + DriftTest 갱신** — `LlmChatViewModel.cs:23` `LlmProviderKind` enum + `:112-119` `AvailableProviders` SSOT + `:207` switch dispatch 동시 갱신 (drift 회피). dispatch 일반화 vs 단순 case 추가 trade-off 는 case 수 8+ 시점 재평가.
  - [ ] **F-6 sub**: `LlmProviderKindDriftTest` 신설 (rev 4 M10) — `AvailableProviders.Count == Enum.GetValues<LlmProviderKind>().Length` + 모든 enum 값이 `AvailableProviders` 에 포함 + `ConfigureProviderAsync` switch 가 모든 enum 값 cover (default branch ArgumentOutOfRangeException 의도적 throw 검증).
- [ ] **F-7** (incremental sub-bullet):
  - [ ] **F-7a**: Cerebras 추가
  - [ ] **F-7b**: OpenRouter 추가
  - [ ] **F-7c**: SambaNova 추가
  - [ ] **F-7d** (조건부): Gemini OpenAI 호환 경로 진입 (§"2A. F-7d PASS 기준" 3 항목 모두 통과 시 본 phase 편입). **rev 5 비고**: Gemini key 발급 시 2026-06-19 시행 restriction 정책 반영 — F-5c ping 검증의 401/403 분기에 안내 문구 추가. key 발급 가이드는 §A1 표 + Hyperlink (`https://aistudio.google.com/app/apikey`) 노출.
  - [ ] **F-7e** (F-7a/b/c 완료 후 사용자 합의 시 진입): DeepSeek Direct / Z.AI GLM 추가. 모델 품질은 F-2-baseline 에서 옵션으로 동시 측정. **rev 5 비고 (DeepSeek 한정)**: API 호출 전 $2 최소 잔액 요구 — Settings UI 진입 시점에 사전 안내 + F-5c ping 의 402 응답 분기에서 카드 등록 안내. Z.AI GLM Flash 모델은 카드 불필요 (분기 차별화 필요).
- [ ] **F-8** (조건부 별도 phase): Gemini smoke test 미통과 시 — D-ε 풀이 (`GeminiDotnet.Extensions.AI` 0.14.1 / `Google_GenerativeAI.Microsoft` 2.7.0 / 단독 OpenAI 호환 endpoint 중 선택) → 별도 SDK 도입.
- [ ] **F-9** (별도 phase): GitHub Models — `Azure.AI.Inference` SDK 도입.

## 관련 파일 / 경로 (rev 4 stale 인용 정정)

| 파일:line | 역할 |
|---|---|
| `Solutions/Core/Ds2.LlmAgent/LlmProvider.fs` | `ILlmProvider` 인터페이스 (Phase 2). **`ProviderCapabilities` + `ProviderConnection` record + `UsageLocation` / `SystemPromptStyle` enum 정의 위치** (F# enum + `[<CLIMutable>]`, 결정 2 정합) |
| `Solutions/Core/Ds2.LlmAgent/LlmEvent.fs` | DU 8종 (SessionStarted/AssistantDelta/Thinking/ToolUse/ToolResult/RateLimitEvent/SessionEnd/ProviderError). **신규 provider 5종 매핑 SSOT** |
| `Solutions/Core/Ds2.LlmAgent/Logging.fs` | log4net `Ds2.LlmAgent.Provider` logger. 신규 provider 동일 namespace 사용 |
| `Solutions/Core/Ds2.LlmAgent/ClaudeCliArgs.fs:54-84` | `--append-system-prompt-file` 인자 빌드. CLI provider 의 system prompt 채널 |
| `Apps/Promaker/Promaker/LlmAgent/Api/ApiProviderFactory.cs:38` | `CreateOpenAiAsync` — `new OpenAIClient(apiKey)` default endpoint. **F-1 임시 / F-4 정식 일반화 진입점** |
| `Apps/Promaker/Promaker/LlmAgent/Api/ApiProviderFactory.cs:65` | `UseFunctionInvocation()` 미들웨어 중앙화 — 신규 provider 도 동일 경로 |
| `Apps/Promaker/Promaker/LlmAgent/Api/ApiProviderFactory.cs:75-` | `CreateMcpClientAsync` — `McpNonceHeader` (결정 5) 부착. 신규 provider 재사용 |
| `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs:109` | `_history.Add(new ChatMessage(ChatRole.System, _systemPrompt));` — API provider 의 system prompt 주입 (§1A) |
| `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs:113-117` | **첫 turn `SessionStarted` yield** (M2 — LlmEvent 5종 매핑 SSOT) |
| `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs` | `IChatClient` → `ILlmProvider` 어댑터. update.Contents → LlmEvent 매핑. **rate-limit retry 추가 위치 후보** (F-1 spike 결과 기반 결정) |
| `Apps/Promaker/Promaker/LlmAgent/McpHostService.cs` | in-process Kestrel + MCP HTTP (결정 4). 신규 provider 도 동일 endpoint 사용 |
| `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:23-30` | `LlmProviderKind` enum (5종). **F-6 enum 확장 위치** |
| `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:112-119` | **`AvailableProviders` 별도 SSOT** — F-6 시 enum 과 동시 갱신 (drift 회피) |
| `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:207` | provider switch dispatch. **F-6 dispatch 일반화 / case 추가 위치** + `LlmProviderKindDriftTest` 검증 대상 |
| `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.Rebuild.cs:41/50/68` | (rev 4 정정 — 기존 `MainViewModel.cs:685-` 는 stale, partial class 분할 후 위치). line 41 `RequestRebuildAll` 정의, line 50 `BeginInvoke`, line 68 `DispatcherPriority.Background`. **결정 8 dispatcher 정책 인용** |
| `Apps/Promaker/Promaker/LlmAgent/LlmConfig.cs:62-64` | `EncryptedKeys` Dictionary. **F-4 신규 provider key 슬롯** |
| `Apps/Promaker/Promaker/LlmAgent/LlmConfig.cs:66-76` | `AnthropicModel` / `OpenAiModel` / `OllamaModel` / `OllamaBaseUrl`. **F-4 신규 provider Model / Endpoint property 추가** |
| `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml.cs:208` | `LoadLlmTab()` — Anthropic/OpenAI key 만 load. **F-5a 신규 provider key 추가** |
| `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml.cs:228-235` | `LlmAnthropicCandidates_Click` 등 — 모델 후보 메뉴 핸들러. **F-5b 신규 provider 메뉴 추가** |
| `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml.cs:237` | `ShowCandidatesMenu` — 후보 배열 → ContextMenu 빌드 (재사용 가능) |
| `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml.cs:269-270` | `LlmClear*Key_Click` 패턴. **F-5c 신규 provider 별 Clear 핸들러** |
| `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml.cs:282-` | `LlmTestOllama_Click` — `GET /api/version` ping 패턴. **F-5c 신규 provider `GET /models` ping 재사용** |
| `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml.cs:314` | `SaveLlmTab()` — Anthropic/OpenAI key + 3 model + Ollama URL 만 저장. **F-5a 신규 provider 저장** |
| `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml` | LLM 탭 XAML — F-5a 신규 provider 별 Row 추가 위치 |
| `Apps/Promaker/Promaker/App.xaml.cs` | `OnStartup` 에 `McpConfigWriter.SweepStale` 호출. 신규 provider 영향 없음 |
| `Apps/Promaker/Promaker/Controls/Llm/LlmChatPanel.xaml(.cs)` | dock UserControl. provider ComboBox 항목 추가 외 영향 없음 |
| `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs:16-39` | 21 tool allowlist SSOT (server-side allowlist, §2). provider 추가 시 변경 없음 |
| `Apps/Promaker/Promaker/LlmAgent/Prompts/2.modeling.md:431-448` | §5 self-check **18 항목** (rev 4: "16" → 18 정정). F-2-infra-a 의 룰 위반 자동 감지기 base |
| `Solutions/Core/Ds2.Editor/Editor/ImportPlanApply.fs:48-52` | (rev 4 정정 — 기존 `:46-50` 인용은 line 46 invalidOp 분기 포함 부정확). `applyWithUndo` 정의 + `WithTransaction` + for loop + `EmitRefreshAndHistory` 1회 |
| `Solutions/Tests/Ds2.LlmAgent.Tests/` | F-2-infra fixture / F-2-baseline 회귀 측정 / F-6 DriftTest / F-7 회귀 측정 신규 위치 |

## 주의 사항

- **`ImportPlanBuilder` mutation 경로 유지** (결정 7): 신규 provider 도 turn end 의 single `store.ApplyImportPlan` 호출 = 1 undo step. `ApiChatProvider` 재사용 시 자동 준수.
- **dispatcher 정책 유지** (결정 8): 신규 provider stream loop 도 `IUiDispatcher.InvokeAsync` Background priority. AssistantDelta 만 dispatcher 우회. (`MainViewModel.Rebuild.cs:50/68` 패턴 재사용)
- **`IAsyncEnumerable<LlmEvent>` 시그니처 유지** (결정 9): 신규 provider `Send : (prompt: string * cancellationToken: CancellationToken) -> IAsyncEnumerable<LlmEvent>` 메서드 시그니처. **5종 매핑** (SessionStarted 포함) 회귀 회피.
- **MCP HTTP transport 유지** (결정 4): in-process Kestrel + nonce header (결정 5). `ApiProviderFactory.CreateMcpClientAsync` 재사용.
- **drift 회귀**: `PromakerToolNamesDriftTests` 21 tool 이름 SSOT 검증 + `LlmProviderKindDriftTest` (F-6 신설) — provider 추가 시 enum/AvailableProviders/dispatch 3 SSOT 동기 검증.
- **multi-key rotation 명시 거부**: provider ToS 위반 — 무료 한도 초과 시 fail-fast.
- **`ModelContextProtocol.AspNetCore` 1.3.0 (2026-05-08 release) 업그레이드**: 본 phase 와 직접 dependency 없음 (provider 추가 영향 없음). **별도 todo 분리** 권장 (`todo-mcp-aspnetcore-upgrade.md` 등). F-1 진입 prerequisite 가 아니므로 본 todo 의 phase grid 외.
- **빌드 시 Promaker.exe 종료 필요**: DLL copy 잠금. 사용자에게 종료 요청 후 재시도.
- **F-1 spike code main leak 금지**: 별도 branch 에서 spike 후 F-4 commit 에서 정식 schema 로 대체 + 임시 분기 삭제. memo-only spike (코드 commit X) 옵션 가능.

## 참조 sources (rev 4 access date 부착)

- Groq rate limits: https://console.groq.com/docs/rate-limits (확인 2026-05-08)
- Cerebras rate limits: https://inference-docs.cerebras.ai/support/rate-limits (확인 2026-05-08)
- SambaNova rate limits: https://docs.sambanova.ai/docs/en/models/rate-limits (확인 2026-05-08)
- OpenRouter limits: https://openrouter.ai/docs/api/reference/limits (확인 2026-05-08)
- Gemini OpenAI compatibility: https://ai.google.dev/gemini-api/docs/openai (확인 2026-05-08)
- Together billing: https://docs.together.ai/docs/billing-credits (확인 2026-05-08)
- Cloudflare Workers AI pricing: https://developers.cloudflare.com/workers-ai/platform/pricing/ (확인 2026-05-08)
- GitHub Models responsible use: https://docs.github.com/en/github-models/responsible-use-of-github-models (확인 2026-05-08)
- DeepSeek Direct API pricing / OpenAI compatibility: https://api-docs.deepseek.com/ (확인 2026-05-08)
- Z.AI / GLM models: https://docs.z.ai/ (확인 2026-05-08)
- xAI Grok consent / data sharing: https://docs.x.ai/docs/legal-policies/usage-policies (확인 2026-05-08)
- Claude Code headless mode (`-p` / stream-json): https://code.claude.com/docs/en/headless (확인 2026-05-09)
- Groq API Keys console: https://console.groq.com/keys (확인 2026-05-09)
- Google AI Studio API Key: https://aistudio.google.com/app/apikey (확인 2026-05-09)
- Gemini API key restriction 정책 (2026-06-19 시행): https://ai.google.dev/gemini-api/docs/api-key (확인 2026-05-09)
- DeepSeek 카드 정책: https://tokenmix.ai/blog/deepseek-api-free-credits (확인 2026-05-09)
- SambaNova Cloud signup: https://cloud.sambanova.ai (확인 2026-05-09)
