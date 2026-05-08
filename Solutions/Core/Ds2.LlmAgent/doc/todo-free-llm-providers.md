# todo-free-llm-providers

> **상태**: 논의 단계 (`--plan` 모드 진행). 코드 변경 없음. 어떤 provider 를 통합할지, 어떤 순서로 진행할지 사용자 결정 대기.

> **revision history**
> - rev 1 (2026-05-08): `--plan` 1차 답변 + `--transfer` 작성. provider 후보 12종 + 통합 고려사항 4종 정리.
> - rev 2 (2026-05-09): 1차 `--review` 반영 (Major 7건). Together 무료 폐지 / Gemini OpenAI 호환 / Groq 모델별 한도 / Cerebras 모델 예시 / SambaNova / OpenRouter 정정 + Settings UI 작업 추가.
> - **rev 3 (2026-05-09)**: 2차 `--review` (5 reviewer 종합 — Critical 3 / Major 9 / Minor 다수) 반영. (1) **C1** `Microsoft.Extensions.AI.Google` NuGet 부재 → 패키지 후보 TBD wording (`GeminiDotnet.Extensions.AI` / `Google_GenerativeAI.Microsoft` 중 spike 검증) 로 단언 보류. (2) **C2 spike-first 원칙** — L1 (factory 일반화) ↔ L4 (Groq 실증) ↔ L5 (retry) ↔ L6 (회귀 fixture) 순서 역행 정정 → F-1 Groq spike + retry → F-2 baseline fixture → F-3 capabilities → F-4 factory 일반화 → 다중. (3) **C3 결정 ↔ phase 의존 그래프 + 결정 trigger** 신설. (4) **M1** §A 표 재정정 — Groq Mixtral deprecated 제거 / Cerebras = 1M TPD + 8192 ctx cap / Mistral 분당 2 / OpenRouter 크레딧 기반 (모델별 X). (5) **M2** "LlmEvent 4종" → "5종 (SessionStarted 포함, of 8 DU)" — `ApiChatProvider.cs:113-117` 첫 turn SessionStarted yield 직접 확인. (6) **M3** L1~L9 → F-1~F-9 rename — `todo-extend-mcp.md` §5.2~5.4 가 L=Layer 의미 점유 (직접 확인) 충돌 회피. (7) **M4** rate limit 분석 깊이 — `Microsoft.Extensions.AI` retry layer spike 의무 + multi-key rotation (ToS 위반) 명시 거부. (8) **M5** 회귀 metric 4종 정의 (룰 위반 카운트 / tool 시그니처 정확도 / clarification 비율 / undo step 일관성) — clarification 분리 측정은 [memory `feedback_clarification_not_noise.md`](../../../../memory) 와 정합. (9) **M6** 4-tuple → `ProviderCapabilities` record (tool_choice / streaming usage / system prompt / strict tools / max output tokens) + F-3/F-4 atomic 묶음. (10) **M7** "진행 상태" / "검증된 사실 (직접 source 검증)" / "다음 작업 진입 권장 순서" 3 섹션 신설 (baseline 4종 todo 컨벤션 적용). (11) **M8** 결정 1~6, 9 의 신규 provider 영향 평가 본문화 (결정 4 HTTP MCP / 결정 5 nonce / 결정 9 IAsyncEnumerable 준수 명시). (12) **M9** Settings UI fallback (회색/inline 입력) + 키 검증 endpoint ping (`GET /models`) 정의. (13) **Minor 다수**: "BaseAddress" → "Endpoint" (`OpenAIClientOptions.Endpoint` 정확 표현) / `ModelContextProtocol.AspNetCore` 1.3.0 업그레이드 검토 / DeepSeek Direct + Z.AI/GLM 후보 추가 / xAI Grok 별도 consent 필요로 제외 / F-7 sub-bullet 분할 / F# > C# 선호 충돌 점검 / 누락 파일 (LlmEvent.fs / Logging.fs / App.xaml.cs / LlmChatPanel.xaml.cs / Tests/) / AvailableProviders SSOT (`LlmChatViewModel.cs:112-119`) 동기 갱신 / 자가 검열 trigger 사전 명시.
> - 다음 리비전 trigger: 결정 대기 4건 풀이 / F-1 spike 결과 (capabilities 차이 발견) / NuGet `Microsoft.Extensions.AI.Google` 등장 시 / xAI Grok consent 정책 변경 시.

## 작업 목표

`Ds2.LlmAgent` 의 `ILlmProvider` 추상화에 **무료 사용 가능한 LLM provider** 를 추가 통합하여, 현재 Phase 2 의 5종 (Claude CLI / Codex CLI / Anthropic API / OpenAI API / Ollama) 외에 무료 옵션을 제공.

## 진행 상태

| 단계 | 상태 |
|---|---|
| 결정 대기 4건 (D-α/β/γ/δ) | ❌ 미진입 — 사용자 confirm 필요 |
| F-1 (Groq spike + retry) | ❌ 미진입 |
| F-2 (회귀 baseline fixture) | ❌ |
| F-3 (`ProviderCapabilities` record) | ❌ |
| F-4 (factory 일반화 + key 슬롯, atomic) | ❌ |
| F-5 (Settings UI 확장 + Consent) | ❌ |
| F-6 (enum / dispatch 갱신) | ❌ |
| F-7a/b/c (Cerebras / OpenRouter / SambaNova) | ❌ |
| F-8 (Gemini smoke test → 통합) | ❌ |
| F-9 (GitHub Models, 별도 phase) | ❌ |

## 다음 작업 진입 권장 순서

1. 본 todo 의 **"검증된 사실 표"** source line 1~2개 직접 열어 sanity check (`ApiChatProvider.cs:113-117` SessionStarted yield + `LlmChatViewModel.cs:112-119` AvailableProviders SSOT + `LlmConfig.cs:62-76` Encrypted/Model 필드).
2. **결정 대기 D-α/β/γ/δ** (아래 "결정 대기 ↔ phase 의존 그래프" 절) 사용자 풀이 — D-α (Phase 분리/일괄) 결정 후 D-γ (회귀 측정 metric) 풀이 → F-1 진입 가능.
3. **F-1 (Groq spike)** — `ApiProviderFactory.cs` 에 임시 `CreateGroqAsync` 추가 (env-var `GROQ_API_KEY` 사용, LlmConfig schema 변경 미동반 — spike 의도). 429 retry 패턴 검증 + tool calling smoke test + `LlmEvent` 5종 매핑 정합 검증. 산출: `ProviderCapabilities` 차이점 + retry 위치 결정 (provider layer vs `ApiChatProvider` layer vs `Microsoft.Extensions.AI` middleware).
4. **F-2 baseline fixture** — Claude 4.6 + Groq `llama-3.3-70b-versatile` 두 케이스를 본 todo §"회귀 측정 metric" 4종으로 측정. 다중 provider 동시 추가 전 binary search 기반 확보.
5. F-3 → F-4 → F-5 → F-6 → F-7 진입.

## 검증된 사실 (직접 source 검증 완료)

| 사실 | 파일:line | 의미 |
|---|---|---|
| `ApiChatProvider` 가 SessionStarted 도 yield | `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs:113-117` | 첫 turn (`firstTurn = _sessionId == null`) 에 `LlmEvent.NewSessionStarted` yield. 신규 provider 도 5종 매핑 (SessionStarted + AssistantDelta + ToolUse + ToolResult + SessionEnd) 준수 필수. (`LlmEvent` DU 자체는 8종 — Thinking / RateLimitEvent / ProviderError 미사용은 phase 외) |
| `AvailableProviders` 별도 SSOT | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:112-119` | `LlmProviderKind` enum (line 23-30) 외에 ComboBox 바인딩용 `IReadOnlyList<LlmProviderKind>` 별도 존재. **F-6 진입 시 enum + AvailableProviders 동시 갱신** (drift 회피) |
| `LlmConfig` 모델 / URL property 위치 | `Apps/Promaker/Promaker/LlmAgent/LlmConfig.cs:66-76` | `AnthropicModel` / `OpenAiModel` / `OllamaModel` / `OllamaBaseUrl` 4 property. F-4 에서 신규 provider 별 추가 |
| `EncryptedKeys` Dictionary 구조 | `Apps/Promaker/Promaker/LlmAgent/LlmConfig.cs:62-64` | `Dictionary<string, string>` (provider key → DPAPI ciphertext). 구조 자체는 확장 가능 — 신규 key 상수 (`GroqKey` 등) 만 추가하면 됨 |
| Settings UI 가 Anthropic/OpenAI key + 3 model + Ollama URL 만 처리 | `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml.cs:208-224` (`LoadLlmTab`) / `:314-` (`SaveLlmTab`) | 신규 provider 진입점 노출 안 됨. F-5 의 핵심 작업 위치 |
| OpenAI 2.10.0 SDK 의 endpoint 속성 | `OpenAIClientOptions.Endpoint` | "BaseAddress 파라미터화" 가 아니라 **"Endpoint 파라미터화"** 가 정확 — F-4 작업 시 wording 통일 |
| `todo-extend-mcp.md` 가 L=Layer 의미 점유 | `doc/todo-extend-mcp.md` rev 1~15 본문 + §5.2/5.3/5.4 | 동일 폴더 내 동음이의어 회피 — 본 todo 는 **F-1 ~ F-9** (Phase Free) 사용 |
| `ImportPlanBuilder` mutation 1 undo step 정책 (결정 7) | `Solutions/Core/Ds2.Editor/Editor/ImportPlanApply.fs:46-50` | 신규 provider 도 turn end 의 single `store.ApplyImportPlan` 호출 준수 |
| dispatcher policy (결정 8) | `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.cs:685-` | `IUiDispatcher.InvokeAsync` Background priority. AssistantDelta 만 dispatcher 우회 |
| MCP 21 tool SSOT | `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs:16-39` | provider 추가 시 tool 이름 변경 X — `PromakerToolNamesDriftTests` 회귀 영향 없음 |

## 결정 대기 ↔ phase 의존 그래프

| 결정 ID | 결정 내용 | 결정 trigger | 영향 phase |
|---|---|---|---|
| **D-α** | **Phase 분리 vs 일괄** — Groq 1개만 우선 (분리) / OpenAI 호환 N개 동시 추가 (일괄) | 사용자 단독 confirm | F-1 (분리 시 진입 즉시 / 일괄 시 F-3 와 묶음) |
| **D-β** | **Refactoring 범위** — `ApiProviderFactory` Endpoint 파라미터화 + `LlmChatViewModel` dispatch 일반화를 F-4 + F-6 분리 vs F-4 묶음 | 사용자 단독 (D-α 결정 후) | F-4 / F-6 |
| **D-γ** | **회귀 측정 전략** — 본 todo §"회귀 측정 metric" 4종 채택 / 일부 채택 / 별도 fixture 정의 | 사용자 + 본인 plan (F-1 spike 결과 본 후) | F-2 |
| **D-δ** | **Consent 다이얼로그 갱신 범위** — 신규 provider 별 별도 동의 / 통합 동의 / 기존 동의 본문 단순 갱신 | 사용자 단독 (개인정보 정책 영역) | F-5 (Consent sub-task) |
| **D-ε** (신규) | **`Microsoft.Extensions.AI.Google` 패키지 정합성** — `GeminiDotnet.Extensions.AI` (0.14.1) / `Google_GenerativeAI.Microsoft` (2.7.0) / OpenAI 호환 endpoint 단독 사용 중 선택 | F-8 진입 시점 spike 결과 기반 | F-8 |

**의존 흐름**: D-α → (D-β + D-γ) → F-1 spike → F-2 baseline → F-3/F-4 → F-5 (D-δ 풀이 직전) → F-6 → F-7a/b/c → F-8 (D-ε 풀이) → F-9.

## 무료 provider 후보 정리

> **주의 (rev 3)**: 외부 한도 / 정책은 자주 변경되므로 통합 직전 각 provider docs 재확인 필수. 본 표는 **2026-05-08 기준** reviewer 검증치 반영 (출처: 본 문서 말미 "참조 sources").

### A. 클라우드 무료 티어 — OpenAI 호환 endpoint (통합 비용 최소)

| Provider | Endpoint | 무료 한도 (2026-05-08) | 모델 예시 |
|---|---|---|---|
| **Groq** | `https://api.groq.com/openai/v1` | **모델별 차등** — 일반: 30 RPM / 14,400 RPD. `llama-3.3-70b-versatile`: 30 RPM / **1,000 RPD**. `qwen/qwen3-32b`: 60 RPM / **1,000 RPD** | Llama 3.3 70B, Llama 4 Scout, DeepSeek R1 Distill, Qwen QwQ 32B (Mixtral = 2025-03-05 deprecate, 제거) |
| **Cerebras** | OpenAI 호환 | 30 RPM / **1,000,000 TPD / 8,192 ctx cap** (free tier, 모델별) | `gpt-oss-120b`, `llama3.1-8b`, `qwen-3-235b-a22b-instruct-2507`, `zai-glm-4.7` |
| **SambaNova Cloud** | OpenAI 호환 | **Free production: 20 RPM / 20 RPD / 200K TPD**. 60–240 RPM 행은 결제수단 등록한 developer tier (무료 아님) | Llama, DeepSeek, Qwen |
| **OpenRouter** | OpenAI 호환 (`:free` suffix 모델) | **20 RPM / 50 RPD** (계정 크레딧 기반, 모델별 X). $10 이상 크레딧 구매 시 1,000 RPD 로 상향 | DeepSeek R1, Llama 등 |
| **Google AI Studio (Gemini)** | **OpenAI 호환** `https://generativelanguage.googleapis.com/v1beta/openai/` (streaming + function calling 지원) | Gemini 2.0/2.5 Flash 무료 등급 (분당 15 / 일 1,500 부근, 모델별) | Gemini 2.0/2.5 Flash, Pro |
| **DeepSeek (Direct API)** | OpenAI 호환 | 가입 시 5M tokens 30일 무료 (소진 후 유료) | DeepSeek V3 / R1 |
| **Z.AI (GLM)** | OpenAI 호환 | GLM-4.5/4.7 Flash **영구 무료** | GLM-4.5 Flash, GLM-4.7 Flash |

> **Gemini 주의**: OpenAI 호환 endpoint 가 streaming + function calling 을 공식 지원하지만, MCP HTTP self-call 경유 tool 호출은 **smoke test 필요** (특히 partial JSON delta + `tool_use_id` 매칭 동작). 호환 확인되면 일반화된 OpenAI 호환 경로 그대로 사용 가능. 미통과 시 D-ε 결정 (별도 SDK 도입) 트리거.

### B. 무료 사용 불가 / 제외

| Provider | 사유 |
|---|---|
| **Together AI** | **무료 trial 폐지** (현행 docs). 최소 $5 크레딧 구매 필수 → 본 phase 제외 |
| **Hugging Face Inference API** | **credit 기반으로 전환** (구 무료 endpoint 폐지). 본 phase 제외 |
| **xAI Grok** | 무료 사용 가능하나 **데이터 공유 동의 필수** → §"5. Consent / 보안 정책" 의 "파일 시스템 경로 등 전송 X" 약속과 직접 충돌. 본 phase 제외 (별도 consent 분리 진행 시 재평가) |

### C. 별도 SDK / 어댑터 필요 — 우선순위 후순위

| Provider | SDK / 어댑터 | 비고 |
|---|---|---|
| **Mistral La Plateforme** | OpenAI 호환 또는 `Mistral.SDK` | "Experiment" 무료 티어, **분당 2 req** (rev 2 의 "분당 1" 정정) |
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
4. **DeepSeek / Z.AI** — F-7 와 동일 패턴, 후순위 (모델 품질 검증 후).
5. **Mistral / GitHub Models** — F-9 별도 phase.

## 통합 시 고려 사항

### 1. OpenAI 호환 provider 일반화 (Endpoint 파라미터화)

- 현재 `ApiProviderFactory.cs:38` 의 `CreateOpenAiAsync` 가 `new OpenAIClient(apiKey)` default endpoint 사용 → **`OpenAIClientOptions { Endpoint = ... }` 받는 overload** 로 일반화. (rev 2 의 "BaseAddress 파라미터화" wording 정정 — OpenAI 2.10.0 SDK 정확 속성명은 `OpenAIClientOptions.Endpoint`)
- **`ProviderCapabilities` record** (M6) 도입 — `(displayName, endpoint, apiKey, modelId)` 4-tuple 만으로는 provider 차이 흡수 불가:
  ```fsharp
  type ProviderCapabilities = {
    DisplayName       : string
    Endpoint          : Uri
    ApiKey            : string
    ModelId           : string
    SupportsToolChoice: bool             // tool_choice 필드 지원 여부
    StreamingUsage    : UsageLocation    // last-chunk vs separate event
    SystemPromptStyle : SystemPromptStyle  // ChatRole.System vs developer message
    StrictTools       : bool             // strict tool schema 지원
    MaxOutputTokens   : int option
  }
  ```
- F-3 (capabilities) + F-4 (factory + key 슬롯) **atomic 묶음 commit** — 분리 시 schema 가 두 번 깨짐.

### 2. MCP HTTP tool 호환성

- 21개 `mcp__promaker__*` tool 노출 (`PromakerToolNames.cs`). OpenAI 호환 provider 라도 tool calling streaming 형태가 SDK 별 미묘히 다름 (`tool_use_id` 매칭 / partial JSON delta).
- Groq/Cerebras/OpenRouter 는 OpenAI tool calling 거의 동일 → 무난 예상.
- Gemini 는 `functionCall` 포맷 차이 → `Microsoft.Extensions.AI` 추상화에 의존. F-8 smoke test 의 핵심 검증 항목.
- **F-1 spike 산출** = provider 별 tool calling 호환 매트릭스.

### 3. Rate limit 대응

- 무료 티어는 분당 한도 빡빡. `LlmTurnContext.cs` 의 `mutation quota=50` 외에 *provider 측 rate-limit retry* 정책 (현재 `ClaudeCliProvider` 의 `RateLimitEvent` 와 같은 처리) 가 API provider 에는 없음.
- **F-1 spike 의무**: `Microsoft.Extensions.AI` 자체 retry policy 가 SDK 어느 layer 에서 처리하는지 spike (provider layer / `ApiChatProvider` layer / `ChatClientBuilder` 미들웨어 / 사용자 코드 layer 4 후보).
- `ApiChatProvider` 에 429 / Retry-After backoff 추가 (spike 결과 기반 위치 결정).
- **명시적 거부**: **multi-key rotation** (provider ToS 위반) 채택 안 함. 무료 한도 초과 시 fail-fast + 사용자 안내.

### 4. 회귀 측정 metric (M5 — F-2 baseline fixture 의 측정 항목 정의)

신규 provider 추가 시 모델 품질 trade-off 측정용. Promaker 도메인 (Ds2 entity 모델링 + MCP tool orchestration) 중심.

| metric | 정의 | 측정 방법 |
|---|---|---|
| **룰 위반 카운트** | `Prompts/2.modeling.md` §3 결정 트리 / §5 self-check 의 16 항목 위반 수 | fixture prompt set N개 → 응답 → 위반 자동 감지 (별도 검증기 작성) |
| **tool 시그니처 정확도** | 21 tool 호출 시 인자 타입 / 필수 인자 누락 / 불필요 인자 비율 | `LlmTurnContext.cs` mutation count + ApplyImportPlan 결과 비교 |
| **clarification 비율** | LLM 이 clarification 질문 turn 비율 (별도 metric, **noise 가 아님** — `feedback_clarification_not_noise.md` 정합) | turn 분류 (mutation / read / clarification / chat) |
| **undo step 일관성** | turn 당 undo step = 1 정책 (결정 7) 위반 여부 | `EmitRefreshAndHistory` 호출 횟수 |

`PromakerToolNamesDriftTests` 류 패턴으로 fixture 영속화 (`Solutions/Tests/Ds2.LlmAgent.Tests/Fixtures/`).

### 5. Consent / 보안 정책

- `LlmConfig.cs` 의 `EnsureGranted` 다이얼로그가 "파일 시스템 경로 등 전송 X" 약속.
- 무료 provider 추가 시 **동일 정책 적용** + DPAPI 보관 키 entry 추가 필요.
- Codex 의 `danger-full-access` sandbox 와 같은 별도 약속이 필요한 provider 는 별도 consent 분리 검토.
- **xAI Grok 정책 충돌**: 데이터 공유 동의 필수 → 본 phase 제외 (B 표).
- **F-5 Settings UI fallback (M9)**:
  - 키 미입력 상태 동작: 해당 provider 행 회색 / `IsEnabled=false` + Tooltip "API key 미입력" + 인라인 입력 시 즉시 활성화.
  - 키 검증 endpoint ping: provider 별 `GET /models` (또는 동등 가벼운 endpoint) 호출 → 200 응답 시 ✅ 표시. Ollama 의 `LlmTestOllama_Click` (`ApplicationSettingsDialog.xaml.cs:282`) 패턴 재사용.

### 6. 결정 1~9 의 신규 provider 영향 평가 (M8)

| 결정 | 신규 provider 영향 |
|---|---|
| 결정 1 (dock panel 통합) | 영향 없음 — `LlmChatPanel.xaml` 내부 ComboBox 항목만 추가 |
| 결정 2 (F# DLL + C# binding) | 영향 없음 — `ApiProviderFactory` 가 C# 측 (Promaker 통합 layer) 이라 신규 provider 추가는 C# 영역. **F# > C# 선호 충돌 점검**: 결정 2 가 `CommunityToolkit.Mvvm` source generator 가 C# only 라 명시 — F-4 의 factory 일반화도 동일 사유로 C# 유지 정당화 |
| 결정 3 (provider 우선순위) | **본 todo 가 결정 3 의 Phase 2 후속 — "API provider 다양화" 영역**. 결정 3 본문 갱신 trigger (rev 3 작업 시 `todo-promaker-llm-agent.md` cross-link 검토) |
| **결정 4 (HTTP MCP transport)** | **신규 provider 도 동일 in-process Kestrel + MCP HTTP self-call 패턴 준수** — `ApiProviderFactory.CreateMcpClientAsync` (`:75-`) 재사용 |
| **결정 5 (loopback nonce)** | **신규 provider 의 MCP HttpClient 도 동일 nonce header 부착** (`ApiProviderFactory.cs` 의 `McpNonceHeader` 패턴) |
| 결정 6 (없음 — rev 1 history) | n/a |
| **결정 7 (ImportPlan 1 turn = 1 undo)** | 신규 provider 도 turn end 의 single `ApplyImportPlan` 호출 준수. `ApiChatProvider` 가 이미 `LlmTurnContext` 와 연동 — 신규 provider 가 `ApiChatProvider` 를 재사용하면 자동 준수 |
| **결정 8 (dispatcher Background)** | 신규 provider 의 stream loop 도 `IUiDispatcher.InvokeAsync` Background priority. AssistantDelta 만 dispatcher 우회 |
| **결정 9 (`IAsyncEnumerable<LlmEvent>`)** | **신규 provider 도 `Send : msg -> CancellationToken -> IAsyncEnumerable<LlmEvent>` 시그니처 준수** + LlmEvent 5종 매핑 (M2 — SessionStarted 누락 회귀 회피) |

## 남은 할 일 목록

### 결정 대기 (D-α/β/γ/δ/ε)

위 "결정 대기 ↔ phase 의존 그래프" 절 참조.

### 구현 작업 (F-1 ~ F-9)

> **명명 컨벤션 (M3)**: `todo-extend-mcp.md` 가 L1=F# Core / L2=F# LlmAgent / L3=C# Promaker (Layer) 의미를 점유. 본 todo 는 동일 폴더 내 동음이의어 회피 위해 **F-1 ~ F-9** (Phase Free) 사용.

> **자가 검열 trigger 사전 명시**: F-3 + F-4 atomic = 신규 함수/타입 3개+ + public API 일반화 → CLAUDE.md `자가 검열 (강제 절차)` trigger ② + ⑤ 동시 충족. F-3/F-4 commit 직전 sub-agent (general-purpose) 위임 review 의무.

- [ ] **F-1** (spike-first): **Groq 단일 통합 spike** — `ApiProviderFactory.cs` 에 임시 `CreateGroqAsync` (env-var `GROQ_API_KEY`, LlmConfig schema 무수정) + 429/Retry-After backoff 동시 도입. tool calling smoke test (21 tool 21회 호출) + LlmEvent 5종 (SessionStarted 포함) 매핑 정합 검증. **`ProviderCapabilities` 차이점 발견** + retry layer 결정 (provider / ApiChatProvider / Microsoft.Extensions.AI 미들웨어 4 후보 중).
- [ ] **F-2**: **회귀 baseline fixture** — Claude 4.6 + Groq `llama-3.3-70b-versatile` 두 케이스를 §"4. 회귀 측정 metric" 4종으로 측정. 다중 provider 추가 전 binary search 기반.
- [ ] **F-3** (atomic with F-4): **`ProviderCapabilities` record 도입** — F-1 spike 결과 기반 차이점 흡수. `LlmProvider.fs` 또는 `ApiProviderFactory.cs` 에 정의.
- [ ] **F-4** (atomic with F-3): **Factory 일반화 + Config schema** — `CreateOpenAiAsync` 의 `OpenAIClientOptions { Endpoint }` overload 도입. `LlmConfig.cs:62-76` 에 신규 provider key 상수 (`GroqKey` 등) + Model / Endpoint property + env-var fallback (`GROQ_API_KEY` 등) 추가. F-3 + F-4 단일 commit (schema 두 번 깨짐 회피).
- [ ] **F-5**: **Settings UI 확장 + Consent** — `ApplicationSettingsDialog.xaml(.cs)` LLM 탭에 신규 provider 별 Row 추가 (`LoadLlmTab` `:208` / `SaveLlmTab` `:314` / `LlmAnthropicCandidates_Click` `:228-235` / `ShowCandidatesMenu` `:237` / `LlmClear*Key_Click` `:269-270` 패턴 복사). 모델 candidates 배열 (`AnthropicModelCandidates` 등) 신규 provider 추천 모델 목록 추가. 키 미입력 상태 회색 처리 + `GET /models` ping 검증 버튼 (Ollama 의 `LlmTestOllama_Click` `:282` 패턴 재사용). Consent 본문 갱신 (D-δ 풀이 결과 반영).
- [ ] **F-6**: **enum + dispatch 갱신** — `LlmChatViewModel.cs:23` `LlmProviderKind` enum + `:112-119` `AvailableProviders` SSOT + `:207` switch dispatch 동시 갱신 (drift 회피). dispatch 일반화 vs 단순 case 추가 trade-off 는 case 수 8+ 시점 재평가.
- [ ] **F-7** (incremental sub-bullet):
  - [ ] **F-7a**: Cerebras 추가
  - [ ] **F-7b**: OpenRouter 추가
  - [ ] **F-7c**: SambaNova 추가
  - [ ] **F-7d** (조건부): Gemini OpenAI 호환 경로 진입 (F-8 smoke test 통과 시 본 phase 편입).
  - [ ] **F-7e** (선택): DeepSeek Direct / Z.AI GLM 추가.
- [ ] **F-8** (조건부 별도 phase): Gemini smoke test 미통과 시 — D-ε 풀이 (`GeminiDotnet.Extensions.AI` 0.14.1 / `Google_GenerativeAI.Microsoft` 2.7.0 / 단독 OpenAI 호환 endpoint 중 선택) → 별도 SDK 도입.
- [ ] **F-9** (별도 phase): GitHub Models — `Azure.AI.Inference` SDK 도입.

## 관련 파일 / 경로

| 파일:line | 역할 |
|---|---|
| `Solutions/Core/Ds2.LlmAgent/LlmProvider.fs` | `ILlmProvider` 인터페이스 (Phase 2). **`ProviderCapabilities` record 정의 후보 위치** |
| `Solutions/Core/Ds2.LlmAgent/LlmEvent.fs` | DU 8종 (SessionStarted/AssistantDelta/Thinking/ToolUse/ToolResult/RateLimitEvent/SessionEnd/ProviderError). **신규 provider 5종 매핑 SSOT** |
| `Solutions/Core/Ds2.LlmAgent/Logging.fs` | log4net `Ds2.LlmAgent.Provider` logger. 신규 provider 동일 namespace 사용 |
| `Apps/Promaker/Promaker/LlmAgent/Api/ApiProviderFactory.cs:38` | `CreateOpenAiAsync` — `new OpenAIClient(apiKey)` default endpoint. **F-1 임시 / F-4 정식 일반화 진입점** |
| `Apps/Promaker/Promaker/LlmAgent/Api/ApiProviderFactory.cs:65` | `UseFunctionInvocation()` 미들웨어 중앙화 — 신규 provider 도 동일 경로 |
| `Apps/Promaker/Promaker/LlmAgent/Api/ApiProviderFactory.cs:75-` | `CreateMcpClientAsync` — `McpNonceHeader` (결정 5) 부착. 신규 provider 재사용 |
| `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs:113-117` | **첫 turn `SessionStarted` yield** (M2 — LlmEvent 5종 매핑 SSOT) |
| `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs` | `IChatClient` → `ILlmProvider` 어댑터. update.Contents → LlmEvent 매핑. **rate-limit retry 추가 위치 후보** (F-1 spike 결과 기반 결정) |
| `Apps/Promaker/Promaker/LlmAgent/McpHostService.cs` | in-process Kestrel + MCP HTTP (결정 4). 신규 provider 도 동일 endpoint 사용 |
| `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:23-30` | `LlmProviderKind` enum (5종). **F-6 enum 확장 위치** |
| `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:112-119` | **`AvailableProviders` 별도 SSOT** — F-6 시 enum 과 동시 갱신 (drift 회피) |
| `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:207` | provider switch dispatch. **F-6 dispatch 일반화 / case 추가 위치** |
| `Apps/Promaker/Promaker/LlmAgent/LlmConfig.cs:62-64` | `EncryptedKeys` Dictionary. **F-4 신규 provider key 슬롯** |
| `Apps/Promaker/Promaker/LlmAgent/LlmConfig.cs:66-76` | `AnthropicModel` / `OpenAiModel` / `OllamaModel` / `OllamaBaseUrl`. **F-4 신규 provider Model / Endpoint property 추가** |
| `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml.cs:208` | `LoadLlmTab()` — Anthropic/OpenAI key 만 load. **F-5 신규 provider key 추가** |
| `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml.cs:228-235` | `LlmAnthropicCandidates_Click` 등 — 모델 후보 메뉴 핸들러. **F-5 신규 provider 메뉴 추가** |
| `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml.cs:237` | `ShowCandidatesMenu` — 후보 배열 → ContextMenu 빌드 (재사용 가능) |
| `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml.cs:269-270` | `LlmClear*Key_Click` 패턴. **F-5 신규 provider 별 Clear 핸들러** |
| `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml.cs:282-` | `LlmTestOllama_Click` — `GET /api/version` ping 패턴. **F-5 신규 provider `GET /models` ping 재사용** |
| `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml.cs:314` | `SaveLlmTab()` — Anthropic/OpenAI key + 3 model + Ollama URL 만 저장. **F-5 신규 provider 저장** |
| `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml` | LLM 탭 XAML — F-5 신규 provider 별 Row 추가 위치 |
| `Apps/Promaker/Promaker/App.xaml.cs` | `OnStartup` 에 `McpConfigWriter.SweepStale` 호출. 신규 provider 영향 없음 |
| `Apps/Promaker/Promaker/Controls/Llm/LlmChatPanel.xaml(.cs)` | dock UserControl. provider ComboBox 항목 추가 외 영향 없음 |
| `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs` | 21 tool allowlist SSOT. provider 추가 시 변경 없음 |
| `Apps/Promaker/Promaker/LlmAgent/Prompts/2.modeling.md` | §3 결정 트리 / §5 self-check (16 항목) — F-2 회귀 측정 기준 |
| `Solutions/Tests/Ds2.LlmAgent.Tests/` | F-2 baseline fixture / F-7 회귀 측정 신규 위치 |

## 주의 사항

- **`ImportPlanBuilder` mutation 경로 유지** (결정 7): 신규 provider 도 turn end 의 single `store.ApplyImportPlan` 호출 = 1 undo step. `ApiChatProvider` 재사용 시 자동 준수.
- **dispatcher 정책 유지** (결정 8): 신규 provider stream loop 도 `IUiDispatcher.InvokeAsync` Background priority. AssistantDelta 만 dispatcher 우회.
- **`IAsyncEnumerable<LlmEvent>` 시그니처 유지** (결정 9): 신규 provider `Send` 메서드 시그니처. **5종 매핑** (SessionStarted 포함) 회귀 회피.
- **MCP HTTP transport 유지** (결정 4): in-process Kestrel + nonce header (결정 5). `ApiProviderFactory.CreateMcpClientAsync` 재사용.
- **drift 회귀**: `PromakerToolNamesDriftTests` 21 tool 이름 SSOT 검증. provider 추가 영향 없음.
- **multi-key rotation 명시 거부**: provider ToS 위반 — 무료 한도 초과 시 fail-fast.
- **`ModelContextProtocol.AspNetCore` 1.3.0 (2026-05-08 release) 업그레이드 검토**: 본 phase 진입 시점에 1.2.0 → 1.3.0 업그레이드 별도 commit 으로 분리 (변경량 / 호환성 영향).
- **빌드 시 Promaker.exe 종료 필요**: DLL copy 잠금. 사용자에게 종료 요청 후 재시도.

## 참조 sources

- Groq rate limits: https://console.groq.com/docs/rate-limits
- Cerebras rate limits: https://inference-docs.cerebras.ai/support/rate-limits
- SambaNova rate limits: https://docs.sambanova.ai/docs/en/models/rate-limits
- OpenRouter limits: https://openrouter.ai/docs/api/reference/limits
- Gemini OpenAI compatibility: https://ai.google.dev/gemini-api/docs/openai
- Together billing: https://docs.together.ai/docs/billing-credits
- Cloudflare Workers AI pricing: https://developers.cloudflare.com/workers-ai/platform/pricing/
- GitHub Models responsible use: https://docs.github.com/en/github-models/responsible-use-of-github-models
- DeepSeek Direct API pricing / OpenAI compatibility: https://api-docs.deepseek.com/
- Z.AI / GLM models: https://docs.z.ai/
- xAI Grok consent / data sharing: https://docs.x.ai/docs/legal-policies/usage-policies
