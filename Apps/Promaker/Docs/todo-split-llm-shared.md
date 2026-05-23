# LLM 인프라 분리 — `Apps/Shared/Llm.Shared/` 신설 todo

> 본 문서는 Promaker 의 LLM 인프라 (McpHost / LlmTurnContext / Api provider / PromptLoader / LlmChatPanel 등 ~18 파일) 를 **공통 lib `Apps/Shared/Llm.Shared/`** 로 추출하여 Promaker 와 다른 LLM 사용 App (HmiDesigner 등) 이 동시 참조하는 분리 작업의 인수인계 노트.
>
> **현재 상태 — 유보 (YAGNI 결정)**. 본 트랙은 별도 트랙 `todo-hmi-designer.md` (HMI Designer 독립 도구) 와 한때 짝으로 진행됐으나, Designer 트랙이 *Promaker 무변경 + 자체 LLM 모듈 작성* 으로 방향 전환 (commit `95cb71da`, 2026-05-23). 본 분리 작업의 진입 전제 (양 App 이 동일 LLM 인프라 공유) 가 약화 — 본 todo 는 *후속 트리거 시 재개* 용 자료로 박제.
>
> **진입 트리거 조건** (셋 중 하나 충족 시 본 todo 재개 검토):
> 1. Designer 트랙의 자체 LLM 모듈이 작성된 후 Promaker 측과의 *drift* 가 maintenance 비용으로 확인 (e.g. 같은 API provider 버그 양쪽 fix 필요)
> 2. 제3의 App (CostSim / DSPilot 등) 이 LLM chat 을 도입하여 인프라 3중화 우려
> 3. Promaker 측 LLM 인프라 refactor (e.g. McpHost 의 transport 변경) 가 *큰* PR 로 진입할 때 — 그 시점에 공통 lib 추출이 자연
>
> 관련 문서:
> - `Apps/Hmi/Docs/todo-hmi-designer.md` — HMI Designer 독립 트랙 (현 진행, Promaker 무변경 전제)
> - `Solutions/Core/Ds2.LlmAgent/CLAUDE.md` — modeling agent 현 상태
>
> **경로 표기 규약**: 본 문서의 모든 상대 경로 (`Apps/...`, `Solutions/...`) 는 `hmi/` worktree 안 작업자 시점 기준.

---

## 작업 목표

1. **Promaker 의 LLM 인프라를 공통 lib `Apps/Shared/Llm.Shared/`** 로 이관 (~18 파일)
2. 양 App (Promaker + 후속 LLM 사용 App) 이 `Llm.Shared` 를 `<ProjectReference>` 로 참조
3. App-specific 영역 (modeling tool / hmi tool / App 별 system prompt) 만 각 App 잔류
4. **Promaker 동작 무변경** 검증 (golden snapshot + smoke test)

---

## 배경 / 맥락

### Promaker 측 현 LLM 인프라 위치 (2026-05 sniff 결과)

```
Apps/Promaker/Promaker/
├── LlmAgent/
│   ├── McpHostService.cs           in-process Kestrel + handshake nonce
│   ├── McpConfigWriter.cs          Owner-only ACL config + sweep
│   ├── ChildProcessTracker.cs      Job Object cascade kill
│   ├── LlmConfig.cs                provider config
│   ├── LlmTurnContext.cs           turn-scoped + validate cache
│   ├── WpfDispatcherAdapter.cs     UI dispatcher 추상화
│   ├── PromptLoader.cs             3-tier prompt 합성 (embedded + operator + user)
│   ├── SystemPrompt.cs             PromptLoader wrapper (14줄)
│   ├── EditorChangeDigest.cs       DsStore 상태 digest
│   ├── KbDigestBuilder.cs          KB profile dict → digest text
│   ├── PromakerToolNames.cs        Promaker tool 이름 상수 (App-specific)
│   ├── Api/
│   │   ├── ApiChatProvider.cs      Anthropic/OpenAI/Ollama 통합 chat
│   │   ├── ApiProviderFactory.cs   provider 팩토리
│   │   ├── ApiTurnContentBuilder.cs
│   │   └── SystemContentBuilder.cs
│   ├── Tools/
│   │   └── ModelTools.cs           Promaker 의 modeling tool 11~21개 (App-specific)
│   └── Prompts/                    *.md (1.entities, 2.modeling, 3.tooling, 4.attachments, 5.knowledge-base, 9.environment, facts, CLAUDE)
└── Controls/Llm/
    ├── LlmChatPanel.xaml
    └── LlmChatPanel.xaml.cs
```

### `Promaker.Shared` 와의 관계

`Promaker.Shared` (Apps/Promaker/Promaker.Shared/) 에는 **LLM 코드 0개** — PLC POCO / Agent IPC / SharedPaths 만. 본 분리 작업과 **무관**. `Promaker.Shared` 는 무변경, 새 `Llm.Shared` lib 신설.

### 핵심 관찰

- **App-agnostic 인프라 ~ 16 파일** — McpHost / Api provider / PromptLoader / LlmChatPanel 등. `Ds2.LlmAgent` (F# DLL) / log4net / MEAI / MCP / 외부 SDK 만 의존, Promaker 자체 의존 0 또는 극소
- **App-specific 영역 ~ 3 파일** — `Tools/ModelTools.cs`, `PromakerToolNames.cs`, `SystemPrompt.cs` (이 중 SystemPrompt 는 단순 wrapper 라 이관 가능)
- **Prompts 폴더** = modeling 전용 + 공통 후보 혼재 (1.entities/2.modeling/3.tooling = Promaker 전용, 4.attachments/5.knowledge-base/9.environment = 공통 후보)
- `PromptLoader.cs` 안 3건의 App-specific hardcoding:
  - `const string EmbeddedPrefix = "Promaker.LlmAgent.Prompts.";`
  - `using Promaker.Services;` → `SettingsPaths.UserPromptsDir / LegacyUserPromptsDir`
  - log4net logger 이름 `"Promaker.LlmAgent.Provider"`

---

## 결정 사항

### L1. 공유 lib 위치 — `Apps/Shared/Llm.Shared/`

- 새 lib `Apps/Shared/Llm.Shared/Llm.Shared.csproj` 신설 (net9.0-windows, UseWPF=true, RootNamespace=Llm.Shared)
- `Promaker.Shared` 는 무변경 (LLM 무관 — PLC POCO / Agent IPC 만)
- 각 LLM 사용 App 이 `<ProjectReference Include="..\..\Shared\Llm.Shared\Llm.Shared.csproj" />` 로 참조

### L2. App-specific hook — `ILlmAppProfile` interface

raw tuple `(Assembly, string)` 보다 명명 record 가 확장성 ↑:

```csharp
namespace Llm.Shared.Abstractions;

/// 한 assembly 안의 embedded prompt resource 집합을 가리키는 source descriptor.
/// `Prefix` 로 시작하는 .md resource 만 자연 정렬 후 concat 됨.
public sealed record PromptSource(Assembly Assembly, string Prefix);

public interface ILlmAppProfile
{
    IReadOnlyList<PromptSource> EmbeddedPromptsSources { get; }
    string UserPromptsDir { get; }
    string? LegacyUserPromptsDir { get; }
    string LoggerName { get; }
    IReadOnlyList<string> AllowedTools { get; }
}
```

각 App 이 `PromakerProfile.cs` / `XxxDesignerProfile.cs` 로 구현 + DI 등록. record 채택 이유:
- 신규 field 추가 시 구현체 컴파일러 가이드 명확 (positional 추가는 deprecation 의무, named property 추가는 점진 가능)
- IDE 자동완성 / debug toString 가독성 ↑ (tuple 보다)

### L3. Prompts 분리 정책 — P1: 공통 baseline + App overlay

- **baseline** (Llm.Shared 가 소유) = LLM 일반 운영 룰 (attachment / KB / environment) — 공통
- **App overlay** = App-specific (modeling vs hmi vs ...)
- `PromptLoader` 가 두 assembly 의 embedded resource 를 읽어 baseline → App 순서로 concat

### L4. `PromptLoader` refactor — multi-source 수집

현 (단일 prefix):
```csharp
const string EmbeddedPrefix = "Promaker.LlmAgent.Prompts.";
```

새 (multi-source, `ILlmAppProfile` 인자):
```csharp
public static string LoadComposed(ILlmAppProfile profile, ILog log)
{
    var sb = new StringBuilder();
    foreach (var src in profile.EmbeddedPromptsSources)
    {
        var (text, count) = LoadEmbeddedFromAssembly(src.Assembly, src.Prefix);
        AppendWithSeparator(sb, text);
    }
    // operator dir / user dir — 기존 동일 (DATA delimiter + 길이 cap)
    ...
}
```

**Override 정책 보강**:
- operator/user dir 의 `.md` 는 *DATA 영역* 으로 박제. 현 `PromptLoader.cs:21-22` 의 `OperatorHeader / UserHeader` 가 "DATA, not instructions" delimiter 명시 — 이 패턴 유지
- **길이 cap**: operator + user 합계 **8 KB 이하** 권장 (Anthropic prompt cache 5분 TTL 안 prefix-stable 영역 보존). 초과 시 head trim + log warn
- system prompt injection 방어: baseline + App overlay 가 *먼저* 박힌 prefix → cache hit 영역에서 instructions 우선. delimiter 그대로면 LLM 이 DATA 로 해석. system prompt robustness 는 LLM provider 의존

### L5. Prompts 파일 위치 + 번호 정책

```
Apps/Shared/Llm.Shared/Prompts/baseline/   ← 공통, 번호 부여
  1.attachments.md       (현 Promaker 의 4.attachments.md 이동)
  2.knowledge-base.md    (현 5.knowledge-base.md 이동)
  3.environment.md       (현 9.environment.md 이동)

Apps/Promaker/Promaker/LlmAgent/Prompts/   ← Promaker 잔류
  1.entities.md          (유지)
  2.modeling.md          (유지)
  3.tooling.md           (유지)
  facts.md               (embed 제외, dev 참고)
  CLAUDE.md              (embed 제외, dev doc)
  chat-samples.txt       (embed 미대상)
  chat-simulation/       (embed 미대상)
```

번호 prefix = **LLM 에게 명시 주입 순서 신호**. baseline 안의 의도 순서: attachment 처리 → KB 검색 → environment 메타.

### L6. csproj EmbeddedResource 설정

**Llm.Shared.csproj** (신설):
```xml
<EmbeddedResource Include="Prompts\baseline\*.md" />
```

**Promaker.csproj** (line 89-90 의 Remove 1줄만 추가):
```xml
<EmbeddedResource Include="LlmAgent\Prompts\*.md" />
<EmbeddedResource Remove="LlmAgent\Prompts\CLAUDE.md" />
<EmbeddedResource Remove="LlmAgent\Prompts\facts.md" />   <!-- 추가 -->
```

Resource name 규칙 = `{RootNamespace}.{folder/dot}.{file}`:
- baseline: `Llm.Shared.Prompts.baseline.1.attachments.md`
- Promaker: `Promaker.LlmAgent.Prompts.1.entities.md`

### L7. LLM 최종 합성 view (Promaker 의 경우)

```
[baseline]                     ← Llm.Shared
  1.attachments → 2.knowledge-base → 3.environment

[Promaker]                     ← Promaker.dll
  1.entities → 2.modeling → 3.tooling

[Operator-supplied — if any]
[User-supplied — if any]
```

---

## 이관 분류표

### A. `Apps/Shared/Llm.Shared/` 로 이관 (App-agnostic — ~16 파일)

```
[LlmAgent/]
  McpHostService.cs              namespace: Promaker.LlmAgent → Llm.Shared.Mcp
  McpConfigWriter.cs             namespace: Promaker.LlmAgent → Llm.Shared.Mcp
  ChildProcessTracker.cs         namespace: Promaker.LlmAgent → Llm.Shared
  LlmConfig.cs                   namespace: Promaker.LlmAgent → Llm.Shared
  LlmTurnContext.cs              namespace: Promaker.LlmAgent → Llm.Shared
  WpfDispatcherAdapter.cs        namespace: Promaker.LlmAgent → Llm.Shared
  PromptLoader.cs                namespace: Promaker.LlmAgent → Llm.Shared
                                 ← refactor (multi-source, ILlmAppProfile 인자)
  SystemPrompt.cs                namespace: Promaker.LlmAgent → Llm.Shared
                                 ← Phase1c → Default rename + Promaker 측 alias 1줄
  EditorChangeDigest.cs          namespace: Promaker.LlmAgent → Llm.Shared
  KbDigestBuilder.cs             namespace: Promaker.LlmAgent → Llm.Shared
                                 ← using Promaker.Knowledge → using Ds2.LightHouse.Protocol
[LlmAgent/Api/]
  ApiChatProvider.cs             namespace: Promaker.LlmAgent.Api → Llm.Shared.Api
  ApiProviderFactory.cs          namespace: Promaker.LlmAgent.Api → Llm.Shared.Api
  ApiTurnContentBuilder.cs       namespace: Promaker.LlmAgent.Api → Llm.Shared.Api
  SystemContentBuilder.cs        namespace: Promaker.LlmAgent.Api → Llm.Shared.Api
[Controls/Llm/]
  LlmChatPanel.xaml              x:Class 네임스페이스 갱신
  LlmChatPanel.xaml.cs           namespace: Promaker.Controls.Llm → Llm.Shared.Controls
[Abstractions/] 신설
  ILlmAppProfile.cs              새 interface (L2)
[Prompts/baseline/]
  1.attachments.md               (현 Promaker 의 4.attachments.md 이동)
  2.knowledge-base.md            (현 5.knowledge-base.md 이동)
  3.environment.md               (현 9.environment.md 이동)
```

### B. Promaker 잔류 (App-specific)

```
LlmAgent/Tools/ModelTools.cs     (modeling tool 11~21)
LlmAgent/PromakerToolNames.cs
LlmAgent/Prompts/                ← 모델링 전용 + dev doc 만 남음
  1.entities.md / 2.modeling.md / 3.tooling.md (유지)
  facts.md / CLAUDE.md / chat-samples.txt / chat-simulation (유지)
PromakerProfile.cs               (신설 — ILlmAppProfile 구현)
```

---

## PR-S1: `Apps/Shared/Llm.Shared/` skeleton + LLM 인프라 이관

### 단계

1. `Apps/Shared/Llm.Shared/Llm.Shared.csproj` 신설 (net9.0-windows, UseWPF=true, RootNamespace=Llm.Shared)
2. 위 A 표의 파일들 `git mv` (이력 보존) → namespace 일괄 갱신
3. `ILlmAppProfile` interface + `PromptSource` record 신설
4. `PromptLoader.cs` refactor (multi-source, `ILlmAppProfile` 인자, 기존 `Promaker.Services.SettingsPaths` 의존 제거 — profile 의 `UserPromptsDir` 사용)
5. `KbDigestBuilder.cs` 의 `using Promaker.Knowledge` → `using Ds2.LightHouse.Protocol` 교체
6. `LlmChatPanel.xaml` 의 `x:Class` + `xmlns` 갱신 (`clr-namespace:Llm.Shared.Controls`)
7. `Promaker.csproj` 변경:
   - `<ProjectReference Include="..\..\Shared\Llm.Shared\Llm.Shared.csproj" />` 추가
   - `<Compile Include>` / `<Page Include>` 의 이관 파일 항목 제거
   - `<EmbeddedResource>` 의 baseline 3 파일 제거 + facts.md Remove 추가
8. `Promaker/PromakerProfile.cs` 신설 (`ILlmAppProfile` 구현)
9. Promaker 측 caller `using Llm.Shared.*;` 추가 + 일부 namespace 갱신
10. 빌드 검증 (경고 0 / 오류 0) + 기존 Promaker 동작 무변경 검증

### Definition of Done (DoD)

PR-S1 은 ~18 파일 이관 + namespace 일괄 변경 + PromptLoader refactor 의 대규모 작업 — "빌드 OK" 만으론 회귀 위험 큼. 다음 검증 통과를 commit 전 조건으로 박제:

1. **빌드 경고 0 / 오류 0** — Promaker / Llm.Shared / 기존 Promaker.Tests 모두
2. **PromptLoader golden snapshot test** — 이관 *전/후* 의 `PromptLoader.LoadComposed(PromakerProfile)` 산출물이 *byte-level 동일*. baseline 3 파일 + Promaker overlay 3 파일 합성 결과의 prompt-cache prefix-stability 보장. 골든 텍스트 = `Solutions/Tests/Promaker.Tests/Goldens/system-prompt.txt` 에 박제
3. **Manifest resource name sanity** — `Assembly.GetManifestResourceNames()` 호출 결과에 `Llm.Shared.Prompts.baseline.{1.attachments,2.knowledge-base,3.environment}.md` 3건 + `Promaker.LlmAgent.Prompts.{1.entities,2.modeling,3.tooling}.md` 3건이 정확히 등장. drift 시 즉시 fail
4. **MCP server smoke test** — Promaker 시작 시 `McpHostService` 가 정상 listen + handshake nonce 발급 + 1 tool call (`describe_subtree` 등) round-trip OK
5. **WPF 렌더 smoke test** — MainWindow 띄우고 `LlmChatPanel` 의 visual tree 가 정상 mount (binding error 0 — output log capture)
6. **Namespace drift check** — `git grep "namespace Promaker.LlmAgent\(\.Api\)\?"` 결과가 Promaker 측에서 0건 (전부 Llm.Shared.* 로 이동했음을 검증)
7. **자가 검열** — `Agent` 도구 (general-purpose 또는 code-review) 로 diff 위임 review → Critical/Major 발견 0 또는 해결 완료

### PR-S1 진입 전 1차 read 필요 (검증 미완 영역)

- `Apps/Promaker/Promaker/LlmAgent/Prompts/CLAUDE.md` / `chat-samples.txt` / `chat-simulation/` 의 실제 내용 — embed 제외인지 다른 용도인지
- `Apps/Promaker/Promaker/LlmAgent/Api/*.cs` 4개 파일의 모든 `using` (App-agnostic 확정 위해 — 1차 sniff 결과 OK 였음)
- `LlmChatPanel.xaml(.cs)` 의 binding / converter 참조 (Promaker 측 ViewModel 의존 여부 — 의존 있으면 interface 추출 필요)
- `Apps/Promaker/Promaker.csproj` 의 `<EmbeddedResource>` 외 다른 LLM 인프라 의존 항목 (Resources / Page Include 등)
- `Apps/Promaker/Promaker/LlmAgent/` 에서 `Assembly.GetManifestResourceNames()` 호출 결과 사전 캡쳐 — 이관 후 golden 비교용 reference

---

## 미해결 결정 지점

| # | 항목 | 비고 |
|---|---|---|
| 1 | **`Llm.Shared` ↔ `Ds2.LlmAgent`(F# DLL) 의존 방향** — `LlmTurnContext.Plan` 이 `ImportPlanBuilder` (F# 타입) 를 직접 보유하면 Llm.Shared 가 F# DLL 참조 → modeling op 까지 알게 되어 App-agnostic 약화. `IPlanBuilder` interface 추출 후 `Llm.Shared.Abstractions` 에 두고 Ds2.LlmAgent 가 구현하는 방향 vs 단방향 참조 유지 | PR-S1 1차 read 시점 (LlmTurnContext.cs / ImportPlanBuilder.fs 실측 후 결정) |
| 2 | **`Llm.Shared.csproj` 의 NuGet 종속성 분할** — Anthropic / OpenAI / OllamaSharp 모두 가져갈지 (Promaker 만 쓰는 게 있다면 Promaker.csproj 잔류) | PR-S1 진입 시 검토 |
| 3 | **`LlmChatPanel.xaml(.cs)` 의 ViewModel 의존** — Promaker 측 ViewModel 에 의존하면 interface 추출 필요 | PR-S1 1차 read 시점 |

---

## 후속 phase 별 검토 항목 (PR-S1 외)

PR-S1 본체 외에 인접 영역에서 추가 검토가 필요한 항목 (meta review 결과 잔여):

- **테스트 가능성** — `IMcpHost / IProcessTracker` 등 최소 abstraction 신설 + `Llm.Shared.Testing` 표준 fake set. PR-S1 후속 PR 또는 PR-S1 안 포함
- **EntityToTagId stub golden 분리** — phase 1 stub 의존 golden 은 stub 교체 시 모두 깨짐 — test fake 기반 분리 필요
- **PII / secret scrubber** — entity 자동 inject digest 의 secret pattern scrubber + log4net turn payload 마스킹 (entity Properties 에 시리얼/credential-like 문자열 포함 시 외부 LLM provider 평문 전송 위험)
- **App 별 tool allowlist 분리 + Host deterministic guard** — Promaker 의 통합 chat 안 prompt injection 시 권한 escalation 위험. LLM 분류 신뢰성이 보안 경계가 되면 안 됨

---

## 관련 파일 / 경로

### 본 작업 신규 (예정)

- `Apps/Shared/Llm.Shared/` 폴더 + csproj
- `Apps/Shared/Llm.Shared/Abstractions/ILlmAppProfile.cs` + `PromptSource.cs`
- `Apps/Shared/Llm.Shared/Prompts/baseline/{1.attachments,2.knowledge-base,3.environment}.md`
- `Apps/Promaker/Promaker/PromakerProfile.cs`

### 본 작업 수정 (예정)

- `Apps/Promaker/Promaker/Promaker.csproj` — `Llm.Shared` ProjectReference 추가 / 이관 파일의 Include 제거 / facts.md Remove 추가
- `Apps/Promaker/Promaker/App.xaml.cs` — `PromakerProfile` DI 등록
- `Apps/Promaker/Promaker/LlmAgent/` — Tools/ModelTools / PromakerToolNames / Prompts/ 만 잔류

### 참조 (read-only)

- `Apps/Hmi/Docs/todo-hmi-designer.md` — Designer 트랙 (Promaker 무변경 전제 + 자체 LLM 모듈). 본 분리 작업 재개 트리거 조건의 하나
- `Solutions/Core/Ds2.LlmAgent/CLAUDE.md` — modeling agent 현 상태

---

## 주의 사항

1. **`Promaker.Shared` 와 `Llm.Shared` 는 서로 다른 lib**. `Promaker.Shared` 안에 LLM 코드 0개 (PLC POCO 만) — rename 불필요. `Promaker.Shared` 무변경 + 새 `Llm.Shared` 신설.

2. **PromptLoader 의 App-specific hardcoding 3건 제거**:
   - `const string EmbeddedPrefix = "Promaker.LlmAgent.Prompts.";` — profile.EmbeddedPromptsSources 로
   - `using Promaker.Services;` `SettingsPaths.UserPromptsDir/LegacyUserPromptsDir` — profile 의 `UserPromptsDir / LegacyUserPromptsDir` 로
   - log4net logger 이름 `"Promaker.LlmAgent.Provider"` — profile 의 `LoggerName` 로

3. **번호 prefix 의 의미** — LLM 에게 명시 주입 순서 신호. baseline 도 prefix 부여 (alphabet sort 에 맡기면 신호 소실).

4. **`facts.md` 는 embed 제외** — dev 참고용으로 Promaker 측 위치 유지, csproj `Remove` 추가.

5. **자가 검열 trigger** — PR-S1 은 *함수/메서드 시그니처 변경 1건 이상 + 신규 함수/타입 3개 이상 + 2개 이상 파일 동시 변경* 모두 해당. PR-S1 commit 전 자가 검열 (Agent / code-review) 의무.

6. **사용자 글로벌 규칙 준수** — `~/.claude/CLAUDE.md` 의 철학 / 코드 생성 / 예외 처리 / 명명 규칙 따름. 본 문서 본문에 재기재 안 함.

7. **Designer 트랙과의 관계** — 현 결정 (`todo-hmi-designer.md` D3) 은 *Promaker 무변경 + Designer 자체 LLM 모듈* 작성. 본 분리 작업은 그 결정을 *뒤집는* 트랙이 아니라, Designer 트랙의 자체 모듈이 작성된 *이후* drift 발생 시 *수렴* 시키는 후속 작업의 backup plan. 본 todo 재개 시점에는 Designer 트랙 의 LLM 모듈 코드도 같이 분류 대상에 포함.

---

## 새 세션 진입 시 체크리스트 (본 todo 재개 트리거 시)

1. 본 문서 (`Apps/Hmi/Docs/todo-split-llm-shared.md`) 통독
2. `Apps/Hmi/Docs/todo-hmi-designer.md` 통독 — Designer 트랙의 현 진행 상태 + 자체 LLM 모듈 작성 여부
3. 진입 트리거 (3건 중 어느 것이 충족됐는지) 사용자 확인
4. 현재 Promaker LLM 인프라 sniff 갱신:
   - `Apps/Promaker/Promaker/LlmAgent/` 파일 목록 (이관 대상 ~16 파일 재검증)
   - `Apps/Promaker/Promaker.Shared/` 파일 목록 (LLM 무관 확정 재확인)
   - `Apps/Promaker/Promaker/Promaker.csproj` 의 line 89-90 `EmbeddedResource` 패턴
5. PR-S1 의 1차 read 5건 (위 § 항목) 수행
6. PR-S1 file-by-file 이관 계획 작성 — 각 .cs 의 namespace before/after 표 + `git mv` 명령 + Promaker.csproj diff
7. 사용자 confirm → PR-S1 실행 → DoD 7-bullet 통과 확인 → commit
