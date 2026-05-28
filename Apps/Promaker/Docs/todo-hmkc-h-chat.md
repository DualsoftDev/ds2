# todo — HKMC H-Chat API 연동 (Option B, ENABLE_HKMC 게이팅)

## 작업 목표

Promaker 의 LLM provider 로 **현대오토에버 H-Chat API** (현대/기아 사내 폐쇄망 LLM 게이트웨이) 를 추가한다.
H-Chat 은 Anthropic / OpenAI 표준 호환 게이트웨이로, base URL + 인증 헤더만 다르고 wire protocol 은 동일.
특정 고객 (현대/기아) 전용 기능이므로 환경변수 `ENABLE_HKMC` 가 설정되었을 때에만 visible/enable.
코드는 일반 사용자 빌드와 최대한 격리한다 — 신규 파일에 격납, 본체 침투는 한 자리수 줄로 한정.

## 배경 / 맥락

- **의뢰자**: 기아 차체생기1팀 김상민 매니저 (`sgmin@kia.com`), 2026-05-22 메일.
- **사유**:
  - 사내 PC 에서 외부 Anthropic / OpenAI 직접 호출 불가 → 현재 Promaker 는 ollama 만 가용.
  - H-Chat 경유 시 OpenAI / Claude / Gemini 전체 사용 가능.
  - 논문 실험 인용 시 "사내 PC 에서 H-Chat API 사용" 표기 가능 → 보안/컴플라이언스 회피.
- **참조 자료**: `F:\Git\ds2\paper\Apps\Promaker\Docs\Paper\materials\from-0522\Docs1.pdf ~ Docs5.pdf`, `H-chat API 개발 문의 .eml`.

## H-Chat API 핵심 사양

| 항목 | 값 |
|---|---|
| Base URL (운영) | `https://internal-apigw-kr.hmg-corp.io/hchat-in/api` |
| Base URL (검증) | `https://stg-internal-apigw-kr.hmg-corp.io/hchat-in/api` |
| Base URL (개발) | `https://dev-internal-apigw-kr.hmg-corp.io/hchat-in/api` |
| Claude endpoint | `POST /v3/claude/messages` (Anthropic Messages API 호환) |
| OpenAI endpoint | `POST /v3/openai/deployments/{model}/chat/completions` |
| 인증 헤더 | `Authorization: Bearer ${H_CHAT_API_KEY}` |
| Project Key 추가 헤더 | `X-Project-Id: ${project_id}` (Personal Key 모드에서는 미사용) |
| 통신 확인 | `GET /v1/are-you-okay` → 사내망 여부 즉시 판정 |
| SDK 호환 | `anthropic >= 0.80.0`, `openai >= 2.22.0` (base_url override 만으로 동작) |
| Claude 모델 | Sonnet 4.6 / Sonnet 4.5 / Haiku 4.5 |
| OpenAI 모델 | GPT-5.4 / 5.2 / 5.1 / 5 / 4.1 / 4o 등 |

## 결정 사항 (이번 세션에서 확정)

1. **연동 방식**: Option B — H-Chat 을 별도 provider 2종 (`HkmcHChatClaude`, `HkmcHChatOpenAi`) 으로 신설.
   Gemini 는 phase 2 로 보류.
2. **API key 모드**: **Personal API Key 만 지원** (Project ID 미사용). UI 에 Project ID 입력 칸 없음.
   `Authorization: Bearer` 헤더만 부착. `X-Project-Id` 헤더 미적용.
3. **모델 ID 기본값**: ~~빈 문자열 — 사용자 입력 강제.~~ → **정정 (paper2 fix)**: H-Chat docs (Docs4) 의 latest stable 명시 default (`claude-sonnet-4-6` / `gpt-5.4`) + Settings panel 의 ▾ Button dropdown 으로 후보 선택 또는 직접 입력. 사용자가 명시로 비울 경우만 declined.
4. **enum 값 prefix**: `Hkmc` 로 통일 → grep `Hkmc` 한 번이면 HKMC 관련 모든 코드 식별 가능.
5. **빌드 분기**: `#if HKMC` conditional compilation 미사용. runtime flag (`ENABLE_HKMC` 환경변수) 로만 게이팅. 빌드 설정 분기 부담 회피 + 테스트 reflection 자유.
6. **MCP tool calling 통과 검증**: H-Chat 의 endpoint 가 `tools` 파라미터를 표준 그대로 통과시키는지 미확인이지만, 일단 구현 후 라이브 검증으로 진행.
7. **`EnsureApiCostConsent`**: HKMC 모드에서 **skip**. 사내 부서 비용 청구 구조라 개인 과금 경고 메시지 부정합. 별도 안내 메시지도 박지 않음.
8. **SSL 인터셉트 우회 토글**: 본 phase 범위 외. 기본 `HttpClientHandler` cert validation 그대로 사용. 라이브 검증 시 실제 차단 발생하면 별 phase 로 박제.
9. **환경 URL**: 운영 URL (`https://internal-apigw-kr.hmg-corp.io/hchat-in/api`) 만 default. 검증/개발 환경은 사용자가 Settings 의 BaseUrl 직접 수정으로 전환.
10. **AvailableProviders 분기 패턴**: 기존 배열 초기화를 `BuildAvailableProviders()` static 메서드로 변경, 내부에서 `HkmcFeature.IsEnabled` 분기로 list concat.

## Phase 구조 (orch SSOT)

| Phase | 산출물 | 종료 조건 |
|---|---|---|
| Phase 0 (spike) | Anthropic .NET SDK 의 base URL override + Authorization 헤더 변환 방식 결정 → 본 todo 의 §"SDK 사용 패턴" 박제 | 결정 문구가 todo 에 박혀 있음 |
| Phase 1 | `HkmcFeature.cs` 신규 + `LlmConfig.cs` 의 `sealed class` → `sealed partial class` 1단어 + `LlmConfig.Hkmc.cs` 신규 | `dotnet build Promaker.sln` 성공 |
| Phase 2 | `Llm.Shared\Api\HkmcApiProviderFactory.cs` 신규 (CreateHChatClaudeAsync / CreateHChatOpenAiAsync / HkmcHChatKey) | `dotnet build Promaker.sln` 성공 |
| Phase 3 | `LlmProviderKind` 에 2값 + `AvailableProviders` 분기 + `ConfigureProviderAsync` switch case 2개 + `LlmChatViewModel.Providers.Hkmc.cs` 신규 | `dotnet build Promaker.sln` 성공 |
| Phase 4 | `HkmcHChatSettingsPanel.xaml(.cs)` 신규 + `ApplicationSettingsDialog` ContentPresenter + Loaded 분기 + enum 라벨 매핑 | `dotnet build Promaker.sln` 성공 |
| Phase 5 | 단위 테스트 — LlmConfig JSON round-trip (HkmcHChat 필드) + Feature flag off/on 시 `BuildAvailableProviders` cardinality | `dotnet test` 통과 |
| Phase 6 (외부 트랙) | 사내망 PC + 의뢰자 협조하 라이브 검증 (`/v1/are-you-okay` ping → 단순 Claude 호출 → MCP tool calling round-trip) | orch 범위 외 — 별도 트랙 |

### Sub agent prompt 골격

**작업 agent 공통**
- 본 phase 의 산출물만 작성. 다른 phase 가 침해할 파일은 절대 수정 금지.
- 본체 파일 수정 cap 은 §"침투 / 신규 파일 매핑" 표 의 "변경 내용" 칸. 초과 금지.
- 빌드 통과 의무: `dotnet build F:\Git\ds2\light-house\Apps\Promaker\Promaker.sln` (또는 솔루션 로컬 명).
- 모든 신규 .cs 파일 LF 라인 엔딩, UTF-8 (BOM 없음).
- 보고 형식: ① 작성/수정 파일 목록 + 변경 줄 수 ② 빌드 결과 ③ 잔여 우려.

**검열 agent 공통 (별 세션, 코드/git 수정 금지)**
- 검토 항목 0: 사용자 명시 의도 verbatim 인용 + patch ↔ 의도 1:1 매핑. 의도 누락 = Critical.
- 검토 항목 1: HKMC 코드의 본체 침투가 §"침투 / 신규 파일 매핑" cap 안인지.
- 검토 항목 2: ENABLE_HKMC off 환경에서 신규 코드의 dead path 가 실제 unreachable 한지.
- 검토 항목 3: Personal Key 모드만 지원 / 모델 ID 빈 문자열 → declined 분기 / `EnsureApiCostConsent` skip 정합.
- 보고 형식: Critical / Major / Minor 분류.

### Phase 0 spike 결과 — 박제

- **Anthropic 패키지**: 공식 `Anthropic` v12.20.0 (`anthropics/anthropic-sdk-csharp`). `ClientOptions.BaseUrl` + `AuthToken` 1급 지원 → DelegatingHandler 불필요.
  - **필수 패턴**: `new AnthropicClient(new ClientOptions { BaseUrl = ..., ApiKey = null, AuthToken = hchatKey })`.
  - `ApiKey = null` 명시 의무 — env var `ANTHROPIC_API_KEY` 가 우연히 설정돼 있으면 `X-Api-Key` 헤더가 추가 부착되어 게이트웨이 4xx 가능 (sdk issue #47).
- **OpenAI 패키지**: H-Chat 의 `/v3/openai/deployments/{model}/chat/completions` 는 Azure OpenAI 스타일 deployment path.
  - **`Azure.AI.OpenAI` 신규 의존성 추가 필요** (Directory.Packages.props 양쪽: Apps/Promaker + Apps/Shared).
  - 패턴: `new AzureOpenAIClient(new Uri(".../v3"), new ApiKeyCredential(key)).GetChatClient(deployment).AsIChatClient()`.
- **잔여 검증 항목** (라이브 검증 phase 에서 확인):
  - Anthropic SDK 12.20.0 의 string table 박제 = **`/v1/messages` append 확정**. 현 구현 `BaseUrl = {baseUrl}/v3/claude` + SDK append `/v1/messages` = `{baseUrl}/v3/claude/v1/messages` → H-Chat 의 `.../v3/claude/messages` 와 path 불일치. 라이브 호출 시 404 예상. hotfix 두 후보:
    - (a) DelegatingHandler 로 `/v1/messages` → `/messages` rewrite (BaseUrl 은 `/v3/claude` 유지).
    - (b) BaseUrl 을 잘라내고 SDK append 후 H-Chat path 와 정합 확인.
    - 본 phase 빌드/구조는 그대로, Phase 6 라이브 검증 결과 후 hotfix.
  - `anthropic-version` 헤더의 H-Chat 수용 여부.
  - Azure OpenAI 의 `api-version` query 의 H-Chat 수용 값.

### 금지사항

- `ApiProviderFactory.cs` 본체 *수정 금지* — HKMC 전용은 신규 `HkmcApiProviderFactory.cs` 에 격납.
- `ENABLE_HKMC` 환경변수 평가는 `HkmcFeature.cs` SSOT 만 사용. 다른 곳에서 직접 `Environment.GetEnvironmentVariable("ENABLE_HKMC")` 호출 금지.
- `LlmConfig.cs` 본체의 본문 수정은 `sealed class` → `sealed partial class` 한 단어로 한정.
- `LlmChatViewModel.cs` 본체의 본문 수정은 enum 2값 + `AvailableProviders` 분기 + (필요시) `OnSelectedProviderChanged` HKMC 가드만 허용.

## 침투 / 신규 파일 매핑

| 파일 | 상태 | 변경 내용 |
|---|---|---|
| `Promaker\HkmcFeature.cs` | **신규** | `IsEnabled` SSOT (env-var `ENABLE_HKMC` 평가) |
| `Promaker\ViewModels\LlmChatViewModel.cs` | 수정 | `LlmProviderKind` enum 에 2값 추가 + `AvailableProviders` 초기화 분기 |
| `Promaker\ViewModels\LlmChatViewModel.Initialize.cs` | 수정 | `ConfigureProviderAsync` switch case 2개 추가 |
| `Promaker\ViewModels\LlmChatViewModel.Providers.Hkmc.cs` | **신규** | partial 파일. `CreateHkmcHChatClaudeProviderAsync` / `CreateHkmcHChatOpenAiProviderAsync` |
| `Llm.Shared\Api\HkmcApiProviderFactory.cs` | **신규** | `CreateHChatClaudeAsync` / `CreateHChatOpenAiAsync` + `HkmcHChatKey` const |
| `Promaker\LlmAgent\LlmConfig.cs` | 수정 | `sealed class` → `sealed partial class` 한 단어만 |
| `Promaker\LlmAgent\LlmConfig.Hkmc.cs` | **신규** | partial 파일. `HkmcHChatConfig` nested + `LlmConfig.HkmcHChat` property |
| `Promaker\Dialogs\Settings\ApplicationSettingsDialog.xaml(.cs)` | 수정 | XAML 3줄 (ContentPresenter + 주석) + cs ~19줄 (필드 2 + 생성자 분기 6 + Ok_Click Save dispatch 11). 본래 cap 6줄 박제했으나 SaveLlmTab 의 dirty 비교 메커니즘이 HkmcHChat 변경을 추적하지 않아 별 분기 필요 → cap 갱신. **향후 cleanup 후보**: SaveLlmTab 흡수 + 단일 `_llmConfig.Save()` 통합 시 이중 Save 비효율 제거 가능 (현 박제는 idempotent + cross-process lock 안전, 결함 아님). |
| `Promaker\Dialogs\Settings\HkmcHChatSettingsPanel.xaml(.cs)` | **신규** | Base URL / API Key / Claude model / OpenAI model 입력 panel |
| enum label converter (있는 경우) | 수정 | HKMC 항목 라벨 매핑 추가 |

**합계**: 신규 6 파일, 기존 본체 수정 ~10줄.

## 구현 단계

1. **현 상태 grep**: `LlmProviderKind` → 표시 라벨 매핑 converter 가 있는지 확인. 다국어 리소스 사용 여부 확인.
2. **`HkmcFeature.cs` 작성** (가장 단순, dependency 0).
3. **`LlmConfig.Hkmc.cs` partial + `LlmConfig.cs` 의 `sealed` 키워드 → `sealed partial` 변경**. JSON 시리얼라이저 round-trip 테스트.
4. **`HkmcApiProviderFactory.cs` 작성**.
   - Anthropic SDK 의 base URL override + custom header 주입 방법 확인 (`AnthropicClient` 가 `Endpoint` / `DefaultHeaders` property 를 노출하는지). 노출 안 되면 `HttpClient` 직접 주입하거나 wrapper handler 사용.
   - OpenAI SDK 는 `OpenAIClientOptions.Endpoint` 로 가능 — Groq 구현 (`ApiProviderFactory.cs` line 86) 참조.
   - 내부 위임은 기존 `CreateInternalAsync` 재사용 (`ApiChatProvider` + MCP client 결선 코드 중복 없이).
5. **`LlmChatViewModel.Providers.Hkmc.cs` partial 작성**. `_config.HkmcHChat?.BaseUrl` 등 참조.
6. **enum 2값 추가** + `AvailableProviders` 초기화 분기.
7. **`ConfigureProviderAsync` switch case 2개 추가**.
8. **`HkmcHChatSettingsPanel` UserControl 작성**.
9. **`ApplicationSettingsDialog` 에 ContentPresenter + Loaded 분기 박제**.
10. **enum 라벨 매핑** (`H-Chat (Claude)`, `H-Chat (OpenAI)`) 추가.
11. **라이브 검증**: 사내망 PC 에서 의뢰자 협조하에 (a) `/v1/are-you-okay` ping, (b) 단순 Claude messages 호출, (c) **MCP tool calling round-trip 검증** (Promaker 의 핵심 기능).

## ⚠️ 주의사항 (구현 시 반드시 검토)

### 보안 / 동의

- **외부 LLM 데이터 전송 동의 (`LlmConfig.EnsureGranted`)** 는 그대로 적용. H-Chat 도 사내망이긴 하나 본질은 외부 LLM 서비스 (Anthropic / OpenAI VertexAI 기반) 로의 데이터 송신 — 같은 동의 메시지 그대로 통과 권장.
- **`EnsureApiCostConsent` 는 적용 여부 재검토**. H-Chat 은 사내 부서 비용 청구라 개인 과금 경고가 부정합. HKMC provider 의 경우 별도 안내 ("귀하 부서의 H-Chat 사용량으로 청구됩니다") 또는 skip 결정 필요. **현재 결정 미확정** — 구현 시 plain skip 으로 시작하고 사용자 피드백 받아 조정.
- **API key DPAPI 슬롯 키 충돌 회피**: `"hkmc-hchat"` 슬롯 사용. 기존 `"anthropic"` / `"openai"` / `"groq"` 와 겹치지 않음.
- **Env-var fallback**: `H_CHAT_API_KEY` 이름 그대로 사용 (H-Chat 공식 문서 표기 정합). DPAPI 슬롯 우선 / env-var fallback 2-tier (기존 Anthropic / OpenAI 패턴 동일).

### 네트워크 / SSL

- **사내망 only**. 일반 사용자 환경에서 H-Chat URL 접속 시 timeout / DNS 실패 발생. 첫 호출 전 `/v1/are-you-okay` ping → 사내망 미접속 시 친절한 안내 메시지.
- **Zscaler 등 SSL 인터셉트 환경**: `SELF_SIGNED_CERT_IN_CHAIN` / `UNABLE_TO_GET_ISSUER_CERT_LOCALLY` 발생 가능 (FAQ Q9, Q23). .NET `HttpClientHandler.ServerCertificateCustomValidationCallback` 우회 옵션을 Settings 패널에 토글로 제공 검토 — 단 기본값 off, 보안 경고 메시지 동반.
- **방화벽**: 운영 IP 대역 `10.146.18.96/27`, `10.146.18.128/27`, `10.146.18.160/27` (FAQ Q21).

### SDK / Wire 호환성

- **Anthropic .NET SDK 의 base URL override 가능 여부 확인 필수**. 패키지 (`Anthropic.SDK` 또는 `Anthropic`) 의 `AnthropicClient` 가 `Endpoint` 또는 `BaseUrl` 속성을 노출하는지 — 노출 안 되면 `HttpClient` + custom `DelegatingHandler` 로 base URL 재라우팅 + 헤더 변환 필요.
- **Authorization 헤더 차이**:
  - Anthropic 표준: `x-api-key: <key>` + `anthropic-version: 2023-06-01`
  - H-Chat: `Authorization: Bearer <key>`
  - SDK 가 `x-api-key` 를 강제 부착한다면 H-Chat 측이 이 헤더를 무시하고 Authorization 만 검사하면 OK. 무시 안 되고 conflict 발생 시 `DelegatingHandler` 로 헤더 strip 필요.
- **`anthropic-version` 헤더**: H-Chat 측 수용 여부 미확인. 라이브 검증 시 함께 확인.
- **`stream: true`**: H-Chat 도 SSE 표준 추정. 기존 Anthropic / OpenAI 어댑터 (Microsoft.Extensions.AI) 코드 그대로 통과 추정.

### MCP tool calling (최대 리스크)

- Promaker 의 핵심은 MCP 12종 tool 사용. H-Chat 의 Claude/OpenAI endpoint 가 클라이언트 정의 `tools` 배열 (tool schema) 을 표준 그대로 통과시켜 주는지 **공식 문서에 명시 없음**.
- Docs1 의 Messages API 명세에 `tool_use` / `tool_result` 블록은 지원 명시되어 있으나, *클라이언트가 정의한 tool schema 정의* 의 통과 여부는 별개 — 일부 게이트웨이는 tool 스키마를 화이트리스트 처리하는 경우가 있음.
- **막혀 있으면 Promaker 의 핵심 기능 (모델링 자동화) 무용**. 구현 후 가장 먼저 검증할 항목.
- 막혀 있을 경우 backup plan:
  - (a) H-Chat 측에 tool calling 통과 요청 (의뢰자 경유로 h-chat@hyundai-autoever.com 문의).
  - (b) Promaker 측에서 tool call 을 자체 parsing 으로 우회 (XML / function-call format 평문 응답 파싱) — 복잡도 ↑, 권장 X.

### enum / partial / 빌드

- `LlmConfig.cs` 의 `public sealed class` → `public sealed partial class` 변경 시 컴파일 영향 0 (sealed + partial 양립). 한 단어 변경.
- 신규 enum 값 (`HkmcHChatClaude`, `HkmcHChatOpenAi`) 은 ENABLE_HKMC off 환경에서도 코드 상 존재. `AvailableProviders` list 에서 제외되므로 UI 노출 0. switch 분기는 unreachable 코드처럼 동작하지만 case 가 정의되어 있어 컴파일러 경고 없음.
- `_config.DefaultProvider` 에서 ENABLE_HKMC off 인 사용자의 disk JSON 에 우연히 HKMC 값이 들어있을 경우 (ENABLE_HKMC on 으로 한 번 켰다가 끈 사용자), `OnSelectedProviderChanged` 시 dispatch 가 case 로 들어가서 H-Chat provider 인스턴스화 시도 — `HkmcFeature.IsEnabled` 가드를 case 진입부에 추가하여 비활성 시 declined exception 던지도록.

### 환경변수 평가 타이밍

- `HkmcFeature.IsEnabled` 는 process 시작 시 1회 평가 (static readonly). **재시작 없이 toggle 불가** — 의뢰자에게 명시.
- 검증 / 디버깅 편의 위해 `Environment.SetEnvironmentVariable` 로 runtime 변경할 일은 거의 없으나, 테스트 코드에서 필요 시 별 entry point (internal property setter) 추가 검토.

### 데이터 보호 / 저장

- `LlmConfig` 의 `JsonIgnore(WhenWritingNull)` 가 `HkmcHChat = null` 일 때 disk JSON 에서 자동 누락 → ENABLE_HKMC 미설정 사용자의 JSON 에 흔적 없음.
- 단 한 번이라도 ENABLE_HKMC on 으로 Settings 진입 후 저장한 사용자는 disk JSON 에 `hkmcHChat` 블록 잔존. 향후 HKMC 기능 제거 시 마이그레이션 코드 필요 (또는 unknown 필드 무시 정책 활용).

## 미해결 / 추후 결정

- **Gemini provider 추가**: phase 2. 별도 SDK (`Google.Generative` 등) 필요, MCP 어댑터도 별도. 본 phase 미박제.
- **부서 비용 청구 안내 메시지 문구** 확정 필요.
- **SSL 인터셉트 우회 토글** UI 표기 방식.
- **검증 환경 / 개발 환경 URL** 도 Settings 에서 선택 가능하게 할지, 운영 URL 만 default 로 박을지 (운영 URL 만 default + 사용자가 BaseUrl 직접 수정 가능 형태로 시작 권장).

## 관련 파일 / 경로

- 참조 코드:
  - `F:\Git\ds2\light-house\Apps\Promaker\Promaker\ViewModels\LlmChatViewModel.cs` (enum, AvailableProviders)
  - `F:\Git\ds2\light-house\Apps\Promaker\Promaker\ViewModels\LlmChatViewModel.Initialize.cs` (ConfigureProviderAsync switch)
  - `F:\Git\ds2\light-house\Apps\Promaker\Promaker\ViewModels\LlmChatViewModel.Providers.cs` (기존 6 provider 생성 패턴)
  - `F:\Git\ds2\light-house\Apps\Promaker\Promaker\LlmAgent\LlmConfig.cs` (DPAPI / EncryptedKeys / Save lock)
  - `F:\Git\ds2\light-house\Apps\Shared\Llm.Shared\Api\ApiProviderFactory.cs` (CreateInternalAsync 재사용 대상, Groq 의 OpenAIClientOptions.Endpoint override 패턴)
  - `F:\Git\ds2\light-house\Apps\Promaker\Promaker\Dialogs\Settings\ApplicationSettingsDialog.xaml.cs`
- 참조 문서:
  - `F:\Git\ds2\paper\Apps\Promaker\Docs\Paper\materials\from-0522\Docs1.pdf` — Messages API 명세
  - `F:\Git\ds2\paper\Apps\Promaker\Docs\Paper\materials\from-0522\Docs2.pdf` — API Guide (Base URL / 환경)
  - `F:\Git\ds2\paper\Apps\Promaker\Docs\Paper\materials\from-0522\Docs3.pdf` — Claude API 요청 예제
  - `F:\Git\ds2\paper\Apps\Promaker\Docs\Paper\materials\from-0522\Docs4.pdf` — H Chat Platform 개요 + 모델 목록
  - `F:\Git\ds2\paper\Apps\Promaker\Docs\Paper\materials\from-0522\Docs5.pdf` — FAQ
  - `F:\Git\ds2\paper\Apps\Promaker\Docs\Paper\materials\from-0522\H-chat API 개발 문의 .eml` — 의뢰 메일 원본

## 진행 상태

- [x] H-Chat API 사양 숙지
- [x] 분리 전략 (Option B) 확정
- [x] 핵심 결정 (Personal Key / 모델 ID 강제 입력 / 분기 전략) 확정
- [x] Phase 0 spike — Anthropic SDK 12.20.0 (AuthToken + ApiKey=null) / Azure.AI.OpenAI 패턴 박제
- [x] Phase 1 — `HkmcFeature.cs` + `LlmConfig` partial 화 + `LlmConfig.Hkmc.cs` (commit 9b8824a2)
- [x] Phase 2 — `HkmcApiProviderFactory.cs` + Azure.AI.OpenAI 2.1.0 패키지 (commit 41fe55cf)
- [x] Phase 3 — enum 2값 + AvailableProviders 분기 + switch case + Providers.Hkmc partial (commit ed0c83b4)
- [x] Phase 4 — `HkmcHChatSettingsPanel` + `ApplicationSettingsDialog` 박제 (commit 29815b55)
- [x] Phase 5 — 단위 테스트 3건 (LlmConfig round-trip / null 누락 / defaults), 회귀 0 (commit d566327e)
- [ ] enum 라벨 매핑 (skip — 기존 enum→label converter 메커니즘 부재. ComboBox 가 `HkmcHChatClaude` / `HkmcHChatOpenAi` 그대로 표시. 본체 침투 회피 우선.)
- [ ] **Phase 6 (외부 트랙)**: 라이브 검증 (사내망 ping → 단순 호출 → **MCP tool calling round-trip**)
- [ ] **매뉴얼 검증**: `ENABLE_HKMC=1` / unset 두 환경에서 Promaker 실행 → ComboBox 의 provider 항목 수 8 vs 6 확인 (HkmcHChatClaude / HkmcHChatOpenAi 가시성). process-static SSOT 라 자동 테스트 회피, 매뉴얼 검증 의무.
- [ ] **Anthropic SDK path 정합 hotfix**: 라이브 검증에서 404 발생 시 DelegatingHandler 로 `/v1/messages` → `/messages` rewrite 또는 BaseUrl 조정 (HkmcApiProviderFactory.cs 의 TODO 코멘트 참조).
- [ ] **매뉴얼 검증**: `ENABLE_HKMC=1` / unset 두 환경에서 Promaker 실행 → ComboBox 의 provider 항목 수 8 vs 6 확인 (HkmcHChatClaude / HkmcHChatOpenAi 가시성). process-static SSOT 라 자동 테스트 회피, 매뉴얼 검증 의무.
- [ ] **Anthropic SDK path 정합 hotfix**: 라이브 검증에서 404 발생 시 DelegatingHandler 로 `/v1/messages` → `/messages` rewrite 또는 BaseUrl 조정 (HkmcApiProviderFactory.cs 의 TODO 코멘트 참조).
