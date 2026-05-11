# Promaker LLM chat — round-trip 최소화 (delta-only snapshot)

> v5 — Step 0 (선결 확인) 결과 반영. (1) `apply_operations` transaction = Case A 확정 → outer wrapper 작업 제거. (2) Provider 분류를 "외부 CLI subprocess (Claude/Codex CLI)" + "in-process IChatClient (AnthropicApi/OpenAiApi/Groq/Ollama)" 2군으로 정정. (3) §5 cache 정책을 3분류 (공통 prefix 강제 / AnthropicApi-only `cache_control` 직접 부여 / CLI 위임) 로 단순화. 변경 이력은 문서 끝 §Revision History.

## 작업 목표

Promaker LLM chat 의 단순 모델링 요청 1건 처리 시 round-trip 수를 **4 → 1 RT** 로 축소.

핵심:
1. **store snapshot delta-only** — 변경 시점에만 자동 첨부
2. **MCP tool schema preload — provider-specific**
3. **명시 cache_control breakpoint — provider-specific**

## 배경 / 맥락

현재 1 요청당 RT 분해: `ToolSearch` (R1) → `list_projects` (R2) → `ToolSearch` (R3) → `apply_operations` (R4) = 4. snapshot 자동 첨부로 R2 제거, schema preload 로 R1/R3 제거, R4 만 남기는 것이 목표.

## 결정된 설계

### 1. Store.Revision — atomicity / transaction / 중앙 hook

**Store 측 변경**
```
Store.Revision : int  (monotonic, runtime-only — 디스크 저장하지 않음)
Store.RenderSnapshot() : string
```

**중앙 hook (UI 분산 금지)**

| 동작 | 파일:라인 |
|---|---|
| Editor transaction commit 성공 | `Solutions/Core/Ds2.Editor/Editor/Authoring.fs:27` |
| Undo / Redo 성공 | `Solutions/Core/Ds2.Editor/Editor/Authoring.fs:161` |
| Store load / replace / import / new / close | `Solutions/Core/Ds2.Core/Store/DsStore.fs:98` |

**금지**: `ClearHistory()`, 파일 저장, in-flight 외부 노출.

**규칙**
- **Atomicity**: `Interlocked.Increment` 또는 store-level lock
- **Transaction boundary**: 단일 transaction (=1 mutation 또는 `apply_operations` 의 N op batch) 당 **`Revision++` 1회**. 중간 상태 외부 노출 금지
- **`RenderSnapshot()`**: commit 직후 일관 상태에서만 호출

### 1.0. 선결 확인 — apply_operations 의 transaction 경계 (Step 0.1, **DONE**)

**결과: Case A 확정** — outer wrapper 추가 작업 불필요.

근거:
- `ModelTools.cs:170` `ApplyOperations` → `RunMutation` → `LlmTurnContext.Plan` 누적
- turn end (`LlmChatViewModel.cs:565` `ApplyTurnPlanAsync`) → `DsStoreImportPlanExtensions.ApplyImportPlan(_store, label, plan)`
- `ImportPlanApply.fs:48-52` `applyWithUndo` 가 plan.Operations **전체** 를 `store.WithTransaction(label, ...)` **하나** 로 감싸서 처리
- 따라서 N op batch = `WithTransaction` 1회 = `Revision++` 1회 자동 보장
- 단일 mutation tool (`AddProject` 등 fallback) 도 동일 RunMutation→Plan→ApplyImportPlan 경로 → 동일하게 1 transaction

§1 hook (`Authoring.fs:27` 의 `withTransaction` 종료 시점) 으로 충분.

### 2. Invalidation trigger (모두 §1 hook 으로 자동 처리)

위 3 지점 hook 이 다음 모두를 trigger:
- 파일 open / close / new
- `apply_operations` commit 성공 (batch 1회)
- GUI 직접 편집 (rename, delete, drag-drop)
- Undo / Redo
- 외부 import / merge

**별도 처리**:
- 파일 외부 수정 감지: 파일 open 또는 윈도우 포커스 복귀 시 mtime 비교 → reload + `Revision++`

### 3. 송신 파이프라인 — anchor / retry 안전

**Anchor**: `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:477`

**snapshot 삽입 순서 (cache-friendly fixed prefix 강제)**
```
[1] system block (tools schema 포함)              ← cache breakpoint #1
[2] <store-snapshot revision="N"> ... </store-snapshot>  ← cache breakpoint #2
[3] editor_changes (변동)
[4] closed-project hints (변동)
[5] text attachments (변동)
[6] user message body
```

**송신 의사코드 (성공 후 commit)**
```fsharp
let snapshotToAttach, revisionAtSend =
    if session.LastSentRevision <> Some store.Revision then
        Some (renderSnapshot store), store.Revision
    else
        None, store.Revision

let result = sendToLlm(messages, snapshotToAttach)
match result with
| Ok _ -> session.LastSentRevision <- Some revisionAtSend  // 응답 streaming 완료 후
| Error _ -> ()  // 재시도 시 재첨부
```

**`LastSentRevision` reset 위치**

| 시점 | 파일:라인 |
|---|---|
| `Reset()` | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:620` |
| `UpdateStore()` | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:700` |
| Provider switch | provider 변경 핸들러 |
| message edit / regenerate | regenerate 진입점 |

**Scope**: in-memory only. app restart / chat history 복원 시 첫 송신에 자동 재첨부 (None 상태로 시작).

### 4. Snapshot wire format

**첨부되는 turn**
```xml
<store-snapshot revision="42">
... 직렬화 본문 ...
</store-snapshot>
```

**미첨부되는 turn**: 완전 침묵 (marker 없음).

### 4.1. Snapshot 직렬화 포맷 (확정 명세)

**원칙**
- 한 entity 또는 한 관계를 한 줄로
- indent 2 spaces, 트리 구조
- 정렬: entity 종류 안에서 이름 오름차순
- UUID 제외 (이름만)
- arrow type 약자: `S`=Start, `R`=Reset, `SR`=StartReset, `RR`=ResetReset, `G`=Group, `U`=Unspecified
- Work 안 Call DAG: `A → B → C`. 다중 source 는 `(A,B) → C`. 분기는 `A → (B,C)`

**Grammar (informal EBNF)**
```
snapshot      = "projects:" (empty | project+)
empty         = "(empty)"
project       = INDENT projectName ":" NEWLINE
                INDENT2 "systems:" NEWLINE system+
                INDENT2 "flows:" NEWLINE flow*

system        = INDENT3 systemName " (" role kind? ")" apiDefs? NEWLINE
                children*   // nested sub-system 은 한 단계 더 indent
role          = "active" | "passive"
kind          = "/cylinder" | "/clamp" | "/robot" | "/device"
apiDefs       = " {" apiDefName ("," apiDefName)* "}"

flow          = INDENT3 flowName " @" ownerSystemName ":" NEWLINE
                INDENT4 "works:" NEWLINE work+

work          = INDENT5 workName " [" callDag "]" NEWLINE
callDag       = callExpr (" → " callExpr)*
callExpr      = device "." apiDef | "(" callExpr ("," callExpr)+ ")"

workArrows    = INDENT2 "work-arrows:" NEWLINE
                (INDENT3 "@" systemName ":" NEWLINE
                  (INDENT4 source " →" arrowType " " target NEWLINE)+ )+
```

**v5 정정**: `ArrowBetweenWorks.ParentId = systemId` (Arrows.fs:26 도메인 룰) → work-arrows 는 system 단위 grouping.
이전 v4 grammar 의 "flow 안 work-arrows" 는 잘못된 가정이었음 (flow 단위 query 결과는 항상 빈 array). source / target work 는
같은 system 안 두 work 이지만 다른 flow 일 수 있으므로 work full Name (`FlowPrefix.LocalName`) 으로 표기.

**예시 1 — 빈 store**
```
projects: (empty)
```

**예시 2 — 단순 (현재 두 cylinder 모델)**
```xml
<store-snapshot revision="1">
projects:
  Project1:
    systems:
      Controller (active)
      Cyl1 (passive/cylinder) {Adv, Ret}
      Cyl2 (passive/cylinder) {Adv, Ret}
    flows:
      Run @Controller:
        works:
          Advance [Cyl1.Adv → Cyl2.Adv]
          Retract [Cyl1.Ret → Cyl2.Ret]
    work-arrows:
      @Controller:
        Run.Advance →S Run.Retract
</store-snapshot>
```

**예시 3 — 다중 system + 분기 (v5 grammar)**
```xml
<store-snapshot revision="17">
projects:
  AutoLine:
    systems:
      MainCtrl (active)
      Robot1 (active/robot) {Home, Pick, Place}
      Conveyor (passive/device) {Run, Stop}
      Vision (active)
    flows:
      Inspect @Vision:
        works:
          Scan [Vision.Capture]
      MainFlow @MainCtrl:
        works:
          Load   [Conveyor.Run]
          Pickup [Robot1.Pick → Robot1.Place]
    work-arrows:
      @MainCtrl:
        MainFlow.Load →S MainFlow.Pickup
        MainFlow.Pickup →SR MainFlow.Load
</store-snapshot>
```

**포함 / 제외 결정**

| 포함 | 제외 |
|---|---|
| Project 이름 | UUID 모든 entity |
| System 이름 + role + kind | Device 의 IO 주소 / pin / 네트워크 설정 |
| Device 의 ApiDef 이름 (호출 가능 API 식별 필수) | ApiDef 파라미터 시그니처 |
| Flow 이름 + 소유 system | HwButton 등 외부 IO 의 물리 매핑 |
| Work 이름 + Call DAG | Call 의 IO 주소 |
| Work-arrows + arrow type | Call-arrows 안의 detail (Work 안 DAG 로 표현됨) |

**Size 추정**: 10 system, 30 work, 100 call 규모도 1500~2500 token 수준 — cache breakpoint 효과 대비 경제적.

### 5. Cache 친화 배치 — provider 분류 (Step 0.2 / 0.3 결과 반영)

**Provider 군 (5종)**:

| 군 | Provider | 우리 코드의 message body 직접 조작 |
|---|---|---|
| 외부 CLI subprocess | `Claude CLI` (`ClaudeCliProvider`), `Codex CLI` (`CodexCliProvider`) | **불가** — 외부 실행 파일이 자체 API 호출 |
| In-process IChatClient | `AnthropicApi`, `OpenAiApi`, `Groq`, `Ollama` (모두 `ApiChatProvider` + `Microsoft.Extensions.AI.IChatClient` 어댑터) | **가능** |

**핵심 단순화**: snapshot 삽입 anchor (`LlmChatViewModel.cs:477`) 는 모든 provider 공통. 거기서 fixed prefix 순서를 강제하면 5 provider 모두 즉시 효과. 명시 cache_control 부여만 AnthropicApi 별도.

#### 5.1. 공통 — fixed prefix 순서 강제 (전 provider)

`promptForProvider` 빌드 흐름 (`LlmChatViewModel.cs:482~508`) 의 prepend 순서를 §3 송신 파이프라인 순으로 통일:

```
[1] (system prompt — provider 측에 별도 전달, 변경 없음)
[2] <store-snapshot revision="N"> ... </store-snapshot>   ← 신규, snapshot 변경 시만
[3] <editor_changes> ... </editor_changes>                ← 기존 _editorDigest.ToContextMessage()
[4] <closed_project> ... </closed_project>                ← 기존 LastClosedProjectPath hint
[5] 텍스트 첨부 inline (fenced wrapper)                    ← 기존
[6] user query 본문                                        ← 기존
```

**OpenAI/Groq**: 자동 prompt cache (≥1024 token stable prefix, TTL 5~10분) — 위 순서만 보장하면 자동 hit. 추가 코드 없음.
**Ollama**: 로컬 추론, cache 개념 무관하지만 prefix 일관성은 향후 KV cache 재활용에 유리.
**Claude/Codex CLI**: 외부 CLI 가 자체적으로 cache 처리. 우리는 prefix 순서만 보장 → CLI 가 stable prefix 검출 시 cache 적용.

#### 5.2. AnthropicApi only — 명시 `cache_control: ephemeral`

In-process AnthropicApi provider 만 우리 코드가 message body 를 만듦 → cache_control 직접 부여 가능. 최대 4 breakpoint, TTL 5분.

**부착 위치**:

| # | 위치 | 효과 |
|---|---|---|
| 1 | system block 끝 | snapshot 변경 turn 에도 system 까지 hit |
| 2 | snapshot block 끝 | revision 미변경 turn 에 snapshot 까지 포함 prefix hit |

**구현 경로 조사 필요**: `Microsoft.Extensions.AI.IChatClient` 추상이 provider-specific raw 옵션을 어떻게 노출하는지 확인 (`ChatOptions.RawRepresentationFactory` / `AdditionalProperties` / `ChatMessage.AdditionalProperties` 후보). 이 hook 이 없으면 `Anthropic` SDK 의 raw client 를 별도 우회 path 로 호출하거나, 본 항목은 deferred.

**구현 hook**: `ApiChatProvider.cs:129` 부근 (`_history` 누적 시점) — provider label 이 "Anthropic" 인 경우만 cache_control 부여.

#### 5.3. CLI providers (Claude / Codex) — 별도 작업 없음

외부 subprocess 라 우리가 message body 를 만들지 않음. §5.1 prefix 순서만 보장 → CLI 의 자체 cache 정책에 위임.

- Claude CLI: Claude Code 가 stable system prompt + tool schema + 사용자 첫 메시지 prefix 에 자동 ephemeral cache 적용 (Anthropic 측 동작)
- Codex CLI: cache 정책 미공개. prefix 일관성 보장이 최선.

### 6. Prompt 룰 — `3.tooling.md` 에 추가

**중요**: `Apps/Promaker/Promaker/Promaker.csproj:62` 가 `*.md` 만 embed. `chat-samples.txt` 는 embed 대상 아님.

**6.1. 신규 룰 추가** (`3.tooling.md` 의 새 섹션, `# Greenfield` 앞)

```
# Store snapshot (자동 첨부)
  - <store-snapshot revision="N"> block 이 보이면 그것을 현재 store 상태로 신뢰.
  - block 이 보이지 않는 turn 에서는 직전 transcript 의 마지막 snapshot 을 그대로 사용.
  - 사용자가 명시적으로 현재 상태 확인을 요청하지 않는 한 list_projects / list_systems 호출 금지.
  - helper add_* 도구는 ToolSearch 로 발견·로드 가능, 또는 apply_operations 의 op 필드로 직접 호출.
```

**6.2. 기존 greenfield 룰 수정** (`3.tooling.md:103`~111)

변경 전:
> "`list_projects` 가 비어 있으면 같은 turn 에 add_project 1회 호출 허용"

변경 후:
> "**snapshot 의 `projects` 가 비어 있으면** 같은 turn 에 add_project 1회 호출 허용. snapshot 이 첨부되지 않은 turn 에서는 직전 transcript 의 snapshot 을 참조. snapshot 도 없는 첫 turn 에 한해 `list_projects` 1회 호출 허용 (cold start)."

→ 룰 충돌 시 LLM 이 보수적으로 `list_projects` 호출 → R2 제거 의미 상실.

### 7. MCP 도구 schema preload — provider 분류

| 군 | 현재 코드 | 정책 |
|---|---|---|
| In-process IChatClient (`AnthropicApi`/`OpenAi`/`Groq`/`Ollama`) | `ApiChatProvider.cs:129` 에서 `ListToolsAsync()` 결과 전부 → `_cachedTools` → `ChatOptions.Tools` | 이미 eager. **추가 작업 없음** |
| Claude CLI (`ClaudeCliProvider`) | `--allowed-tools` allowlist (`PromakerToolNames.All`) 로 21개 노출. CLI 가 MCP server 와 별도 connect → schema 자체 fetch | CLI 정책에 위임. allowlist 가 곧 exposure. **추가 작업 없음** |
| Codex CLI (`CodexCliProvider`) | tool 단위 allowlist 없음. MCP server 가 노출하는 모든 tool 이 CLI 에 보임 | sandbox + system prompt 가이드로 차단. **추가 작업 없음** |

→ **결론**: 5 provider 모두 schema preload 추가 작업 불필요. plan v4 의 "preload" 효과는 이미 달성된 상태. RT 축소 효과는 §1 (snapshot), §6 (prompt 룰) 만으로 달성 가능.

### 7.1. Token 측정 procedure (Step 5 선행)

**측정 항목**
1. `apply_operations` schema 의 token 수
2. 후보 always-load 8개 schema 총합
3. ToolSearch 1 RT 의 실측 latency
4. cached vs 비-cached input token 단가 차이

**측정 방법**

| 항목 | Anthropic | OpenAI 호환 |
|---|---|---|
| Schema token | `POST /v1/messages/count_tokens` 에 schema 만 채워 호출 | tiktoken (cl100k_base) 로직으로 schema JSON 계측 |
| RT latency | Anthropic API 호출 wall-clock | API 호출 wall-clock |
| Cached ratio | response `usage.cache_read_input_tokens / (input_tokens + cache_read_input_tokens)` | response `usage.prompt_tokens_details.cached_tokens / prompt_tokens` |

**측정 script 권장 위치**: `Apps/Promaker/Promaker/Tools/measure-tool-tokens/` 또는 단발 F# script.

**의사결정 기준**
- always-load total ≤ ~3K token 이면 eager 채택 (1 ToolSearch RT ≈ 500ms~1s 의 본전 회수 충분)
- ≥ 5K token 이면 deferred 유지 + ToolSearch
- 3~5K 사이는 cache 적중률 시뮬레이션 후 결정

## 적용 시 round 수 효과

v5 정정 — schema preload 가 이미 5 provider 모두 eager 라 §7 단독 효과는 0. 핵심 효과는 §1 (snapshot delta-only) + §6 (prompt 룰) 의 조합.

| 조합 | RT |
|---|---|
| 현재 | 4 |
| §6 prompt 룰만 | 3 |
| §1+§3+§6 (snapshot + 룰) | **1** |

## 새 세션 이어받기 — 현재 상태 요약 (2026-05-11 시점)

본 todo 의 v5 ~ v5.3 작업은 단일 branch (`feature/llm`) 에서 진행 중. 새 세션은 본 섹션부터 읽고
"남은 일" 만 우선 처리. 자세한 history 는 §Revision History 의 v5/v5.1/v5.2/v5.3 entry 참조.

### 완료 (DONE) — Step 0 ~ Step 4

| Step | 내용 | 핵심 파일 |
|---|---|---|
| 0.1 | `apply_operations` Case A 확정 | `ImportPlanApply.fs:48-52` (조사만) |
| 0.2/0.3 | provider 분류 (외부 CLI 2 + in-process IChatClient 4) | (조사만) |
| 1 | `DsStore.Revision` + `BumpRevision()` (internal) + hook 3 지점 | `DsStore.fs`, `Authoring.fs` |
| 1 | `RenderSnapshot()` + `RenderSnapshotEnvelope()` + `RenderSnapshotEnvelopeAtomic()` | `StoreSnapshot.fs` (신규) |
| 2 | `LlmChatViewModel._lastSentRevision` + reset 4 지점 + retry-safe commit | `LlmChatViewModel.cs` |
| 3 | `3.tooling.md` snapshot 룰 신설 + greenfield 룰 갱신 | `3.tooling.md` |
| 4 | AnthropicApi `cache_control: ephemeral` (system + sticky snapshot 양쪽) | `ApiChatProvider.cs` |
| C1 | `LlmUserMessage.SnapshotPrefix` + multi-content 분리 (history 누적 회피) | `LlmMessage.fs`, `ApiChatProvider.cs`, CLI 2종 |
| H1 | API provider sticky snapshot (`_stickySnapshot`) — 매 turn prepend | `ApiChatProvider.cs` |
| H2 | Call DAG fan-in 누락 edge 별도 표기 (`representedEdges`) | `StoreSnapshot.fs` |

### 핵심 효과 검증 결과

- `apply_operations` 1회 호출 = batch N op = `WithTransaction` 1회 = `Revision++` 1회 (자동 보장)
- snapshot 매 turn 자동 첨부 (delta-only, revision 변경 시만 새 snapshot — sticky 유지) → `list_projects`/`describe_subtree` 호출 제거
- AnthropicApi: system + tool schema + sticky snapshot = cache prefix (2 breakpoint, 4 cap 안)
- 기존 4 RT → **1 RT** 달성 (Step 1+2+3 만으로)

### 남은 일 (우선순위 순)

| # | 항목 | Step | 비용 | 비고 |
|---|---|---|---|---|
| R1 | E2E 시나리오 6종 (§E2E) 실제 Promaker 실행하여 검증 | Step 7 | 사용자 직접 검증 | round-trip 효과 확인 (HTTP 요청 횟수, 첫 token latency, cache 적중률) |
| R2 | Compaction keepalive (Claude CLI 기본 ON, N=20 turn) | Step 6 | 작음 (`ClaudeCliProvider`) | 긴 대화에서 snapshot prefix 보호 |
| R3 | Cache 적중률 모니터링 (`usage.cache_read_input_tokens`/`prompt_tokens_details.cached_tokens` logging) | Step 6 | 작음 (`ApiChatProvider` finally 부) | Step 5 에서 token 측정과 묶음 가능 |
| R4 | 외부 mtime 감지 (윈도우 포커스 복귀 시 비교 → reload) | Step 6 | 중간 (MainViewModel 또는 Window 측 hook) | reload 자체는 §1.3 hook 통과 — mtime 비교만 추가 |
| R5 | snapshot token 비용 측정 (Anthropic count_tokens / tiktoken) | Step 5 | 작음 (단발 script) | 1.5~2.5K 추정치 검증 (정보 수집용) |
| R6 | §J2 — `Capabilities.SupportsAnthropicCacheControl: bool` 비트 격상 | Step 8 | 중간 (4 preset + 5 factory) | Bedrock 등 Anthropic 호환 endpoint 추가 PR 진입 시 |
| R7 | §J3 — `Promaker.csproj NoWarn=CS9057` 회수 | Step 8 | 작음 | OllamaSharp SDK 정식 대응 후 |
| R8 | §J4 — `DsSystem.SystemType` SSOT (option<string> → enum/DU) | Step 8 | 큼 (도메인 영역) | 본 PR 범위 밖 |
| R9 | §J5 — CLI snapshot prepend helper (`LlmUserMessageOps.prependSnapshot`) | Step 8 | 작음 | 3rd CLI provider 추가 시 강제 |
| R10 | snapshot multi-content 의 snapshot block 별도 cache breakpoint (Step 4 deferred) | Step 4 | 중간 | `LlmUserMessage` 가 이미 SnapshotPrefix 분리하므로 적용 가능. cache hit 효과 부수적이라 우선순위 낮음 |
| R11 | Step 8 후속 (J2~J5) 진행 결정 | - | - | follow-up PR 분리 권장 |

### 새 세션 진입 시 권장 시작점

1. **검증 우선** (R1) — 실제 Promaker 실행 + Anthropic API key 로 시나리오 1~6 수행, round-trip 횟수 측정
2. **검증 결과에 따라**:
   - 1 RT 달성 OK → R2/R3/R4 (운영 안전망) 진입
   - 1 RT 미달성 → 원인 분석 (snapshot 누락 / prompt 룰 미준수 / cache miss 등) 후 fix
3. R5 token 측정은 R1 검증과 묶음 가능 (실 호출의 `usage` 필드 관찰)
4. R6~R10 은 별도 PR

### 본 PR 의 변경 파일 (commit 대상)

| Layer | 파일 | 변경 |
|---|---|---|
| Ds2.Core | `Solutions/Core/Ds2.Core/Store/DsStore.fs` | Revision/BumpRevision + ApplyNewStore hook |
| Ds2.Editor | `Solutions/Core/Ds2.Editor/Editor/Authoring.fs` | withTransaction/applyTransaction hook |
| Ds2.LlmAgent | `Solutions/Core/Ds2.LlmAgent/LlmMessage.fs` | SnapshotPrefix + factory + helper |
| Ds2.LlmAgent | `Solutions/Core/Ds2.LlmAgent/ClaudeCliProvider.fs` | snapshot prepend |
| Ds2.LlmAgent | `Solutions/Core/Ds2.LlmAgent/CodexCliProvider.fs` | snapshot prepend |
| Ds2.LlmAgent | `Solutions/Core/Ds2.LlmAgent/StoreSnapshot.fs` | **신규** — RenderSnapshot + envelope helper |
| Ds2.LlmAgent | `Solutions/Core/Ds2.LlmAgent/Ds2.LlmAgent.fsproj` | StoreSnapshot.fs Compile Include |
| Promaker | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` | _lastSentRevision + snapshot envelope + reset 4 지점 |
| Promaker | `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs` | sticky snapshot + multi-content + cache_control |
| Promaker | `Apps/Promaker/Promaker/LlmAgent/Api/ApiProviderFactory.cs` | AnthropicProviderLabel const |
| Promaker | `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md` | snapshot 룰 신설 + greenfield 룰 갱신 |
| Docs | `Apps/Promaker/Docs/todo-promaker-llm-roundtrip-optimization.md` | 본 문서 |

### 본 PR 외부 (별도 결정 필요)

- `Apps/Promaker-gfm.sln` — `Promaker.sln` 와 byte-identical 중복. 자동 생성 출처 불명, 사용자 결정 후 처리
- `Apps/Promaker/Prompts/`, `Solutions/Core/Ds2.LlmAgent/doc/Prompts/` — symlink 추정. 처리 결정 필요
- `Apps/Promaker/Promaker/LlmAgent/Prompts/chat-samples.txt` — csproj `*.md` embed 대상 외 (빌드 영향 X), 처리 결정 필요

## 구현 순서 (v5)

### Step 0 — 선결 확인 (DONE)
1. **§1.0 transaction 경계** — Case A 확정 (DONE)
2. **§7 schema preload** — 5 provider 모두 이미 eager 또는 자체 처리 (DONE)
3. **§5 provider 분류** — 외부 CLI 2 + in-process IChatClient 4 로 정정 (DONE)

### Step 1 — Store 측 기반
1. `DsStore.Revision : int` 도입 (`Interlocked.Increment`, runtime-only, 직렬화 제외)
2. `Store.RenderSnapshot()` 구현 (§4.1 grammar) — F# 측 또는 별도 module
3. Revision bump hook 부착 3 지점:
   - `Authoring.fs:27` `withTransaction` (commit 성공 시 records.Count > 0 일 때만 ++)
   - `Authoring.fs:161` `applyTransaction` (undo/redo 성공 시 ++)
   - `DsStore.fs:98` `ApplyNewStore` (load/replace 의 중앙 hub — 끝에서 ++)
4. 파일 외부 수정 감지 (mtime) → reload 경로가 자동 §3 hook 통과하므로 별도 작업 불필요. 다만 mtime 감지 자체 (윈도우 포커스 복귀 시 비교) 는 Step 6 로 이연

### Step 2 — Session / 송신
5. `LlmChatViewModel.LastSentRevision: int?` 필드 추가 (in-memory only)
6. Reset 4 지점:
   - `Reset() :620`
   - `UpdateStore() :700`
   - provider switch (`ConfigureProviderAsync` :214 분기 직전)
   - message edit / regenerate (해당 진입점 식별 후)
7. 송신 anchor (`:477` 부근, `promptForProvider` 빌드 영역) — snapshot 을 §5.1 순서로 prepend, retry-safe (성공 응답 도착 후에만 `LastSentRevision <- store.Revision`)

### Step 3 — Prompt 룰
8. `3.tooling.md` 에 §6.1 snapshot 룰 신설 (`# Greenfield` 앞)
9. `3.tooling.md:103` 의 list_projects 권장 문구를 §6.2 로 교체

### Step 4 — Cache control (in-process AnthropicApi only) — partial DONE
10. **DONE** — `ApiChatProvider.SendImpl` (firstTurn 분기) 에서 provider label "Anthropic" 인 경우 system prompt 의 TextContent 에 `cache_control: ephemeral` 부착. Anthropic SDK 가 `Microsoft.Extensions.AI.AIContentCacheExtensions.WithCacheControl<T>(T, CacheControlEphemeral)` extension 으로 정식 지원. system + tool schema 까지 cache prefix.
11. **DEFERRED** — snapshot block 까지 별도 cache breakpoint 부여하려면 `LlmUserMessage` 가 multi-content (TextContent[] 복수) 로 변경되어야 한다. snapshot 자체는 1.5~2.5K 토큰 규모로 system 대비 작아 cache hit 효과 부수적. follow-up PR.
12. OpenAI/Groq/Ollama: §5.1 prefix 순서 보장만 — Step 2 의 anchor 작업으로 자동 충족 (DONE)
13. Claude/Codex CLI: 작업 없음 (CLI 위임)

### Step 5 — Token 측정 (정보 수집용, optional)
13. snapshot 자체 token 비용 측정 (Anthropic count_tokens / tiktoken). 중규모 (10 system 30 work) 가 1.5~2.5K 라는 §4.1 추정 검증
14. 측정 script 위치: `Apps/Promaker/Promaker/Tools/measure-tool-tokens/` 또는 단발 F# script
15. (Step 0 결과 schema preload 가 이미 완료라 always-load vs deferred 의사결정은 N/A)

### Step 6 — 운영 안전망
16. Compaction keepalive: Claude CLI 기본 ON, N=20 turn (구현 위치는 `ClaudeCliProvider` 측)
17. Cache 적중률 모니터링: AnthropicApi 의 `usage.cache_read_input_tokens` / OpenAI 의 `usage.prompt_tokens_details.cached_tokens` 를 `_streamingTurn` 부 로깅
18. 외부 mtime 감지: 윈도우 포커스 복귀 시 현재 열린 파일 mtime 비교 → 변경 감지 시 reload (자동으로 Step 1.3 hook 통과)

### Step 7 — E2E 검증
19. §E2E 시나리오 6종 실행 및 측정

### Step 8 — review v5.2 후속 cleanup (deferred)
20. **§J2** — `Capabilities.SupportsAnthropicCacheControl: bool` 비트 격상. 현재 `_providerLabel == AnthropicProviderLabel` 분기는 SSOT const 로 정리됨 (review M5 처리). Bedrock 등 Anthropic 호환 endpoint 추가 PR 진입 시 capability 비트로 격상하여 silent 누락 방지. 본 PR 범위 밖.
21. **§J3** — `Promaker.csproj` 의 `NoWarn=CS9057` 회수. OllamaSharp SDK 가 정식 대응할 때까지 임시. SDK 버전 monitoring 후 회수 시점 결정.
22. **§J4** — `DsSystem.SystemType` (option<string>) 의 SSOT 정리. 현재 `kindSuffix` 가 stringly-typed StartsWith 매칭. enum/DU 일원화 + 공유 helper 추출 (도메인 영역, 본 PR 범위 밖).
23. **§J5** — CLI provider 의 snapshot prepend 패턴 (`ClaudeCliProvider` / `CodexCliProvider`) 중복. 3번째 CLI provider 추가 시 `LlmUserMessageOps.prependSnapshot` helper 로 추출.

## E2E 테스트 시나리오

**시나리오 1 — 1 RT 달성 (핵심 목표 검증)**
1. 새 chat session 시작 (LastSentRevision = None)
2. store 비어 있는 상태 (snapshot = `projects: (empty)`)
3. 사용자 입력: "새 프로젝트 생성해서 두 cylinder 가 순차로 전진하고, 다시 두 cylinder 가 순차로 후진"
4. **기대**: 단일 turn 안에 `apply_operations` 1회 호출 + 응답 종료. 중간에 `list_projects`, 추가 `ToolSearch` 호출 없음.
5. **측정**: HTTP 요청 횟수, 첫 token latency, 총 turn 수

**시나리오 2 — Delta-only 검증**
1. 시나리오 1 완료 후 사용자 입력: "Cyl3 추가해서 마지막에 전진"
2. **기대**: 첨부 snapshot 은 revision 1 (Cyl1, Cyl2 이미 있는 상태). LLM 이 add_cylinder + add_call + add_arrow 발행.
3. 다음 turn 사용자 입력: "ok 잘 됐어"
4. **기대**: snapshot 미첨부 (revision 무변경 가정). marker 도 없음. LLM 은 직전 snapshot 사용.

**시나리오 3 — Cache 적중률**
1. 동일 session 에서 10회 query (mutation 없음, 단순 질의)
2. **기대**: 적중률 ≥ 90% (`usage.cache_read_input_tokens / total_input_tokens`)

**시나리오 4 — Retry 안전성**
1. 송신 중 네트워크 차단 시뮬레이션 → 첫 send 실패
2. 재시도 → 동일 snapshot 재첨부 확인 (LastSentRevision 미갱신)

**시나리오 5 — Session 분기**
1. 사용자 message edit → 이전 send 의 LastSentRevision 무효화
2. 새 send 에 snapshot 재첨부 확인

**시나리오 6 — 외부 편집**
1. LLM 으로 모델 작업 후 별도 에디터로 파일 수정 → Promaker 윈도우 포커스 복귀
2. **기대**: mtime 비교 트리거 → store reload + `Revision++` → 다음 send 에 갱신 snapshot 첨부

## 관련 파일 / 라인 정리

| 용도 | 경로 |
|---|---|
| Editor transaction commit | `Solutions/Core/Ds2.Editor/Editor/Authoring.fs:27` |
| Undo / Redo | `Solutions/Core/Ds2.Editor/Editor/Authoring.fs:161` |
| Store load / replace | `Solutions/Core/Ds2.Core/Store/DsStore.fs:98` |
| Chat send anchor | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:477` |
| Reset() | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:620` |
| UpdateStore() | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:700` |
| Claude tool allowlist | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:277` |
| API providers tool exposure | `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs:129` |
| Prompt embed (md only) | `Apps/Promaker/Promaker/Promaker.csproj:62` |
| Greenfield 룰 (수정 대상) | `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md:103` |
| Snapshot 룰 신설 위치 | `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md` (`# Greenfield` 앞) |

## 주의 사항 / Edge cases

| 상황 | 처리 |
|---|---|
| 새 chat session | `LastSentRevision = None` → 첫 메시지 무조건 첨부 |
| API 실패 / 재시도 | 성공 응답 확인 후에만 commit (§3) |
| message edit / regenerate | reset |
| Provider switch | reset |
| Anthropic context compaction | snapshot 을 stable prefix + cache_control 로 보호 + keepalive 기본 ON (N=20) |
| LLM mutation → 같은 turn 내 다음 read | 기존 룰 유지. 다음 user turn 에서 갱신 snapshot 자동 첨부 |
| GUI 와 LLM 동시 mutation | Revision atomicity + transaction layer 일원화 |
| 외부 에디터 파일 직접 변조 | mtime 체크 → reload + `Revision++` |
| Stale snapshot 의심 시 | system prompt 룰 + 첫 turn 한정 `list_projects` cold-start 허용 (§6.2) |
| snapshot token 비용 | 중규모 (10 system 30 work) 도 1.5~2.5K token. cache breakpoint 효과 대비 경제적 |

## Revision History

- **v1** — 초기 작성
- **v2** — Critical/Major 5 + 부가 2 반영 (atomicity, retry-safe, marker 폐기, cache_control 도입, 측정 선행, keepalive ON, mtime 체크)
- **v3** — 코드 사실 기반 리뷰 6건 반영 (hook 위치 확정, 송신 anchor 확정, reset 위치 확정, `3.tooling.md` 룰 위치 확정, greenfield 룰 교체, provider-specific 분리, 구현 순서 재정렬)
- **v4** — 새 세션 즉시 착수 가능 수준으로 보강
  - §1.0 `apply_operations` transaction 경계 선결 확인 task 신설
  - §4.1 Snapshot 직렬화 grammar + 예시 3종 (빈 / 단순 / 다중) 확정
  - §5 cache_control 을 provider 분기 (Anthropic / OpenAI / Codex) 로 재작성, JSON 예시 포함
  - §7.1 Token 측정 procedure (count_tokens / tiktoken / usage 필드) 명세
  - §E2E 통합 테스트 시나리오 6종 추가
  - Step 0 (선결 확인) 신설, 구현 순서 7 step 으로 재정렬
- **v5** — Step 0 결과 반영
  - §1.0: Case A 확정 → outer wrapper 작업 제거 (`ImportPlanApply.fs:48-52` 가 단일 `WithTransaction` 으로 plan.Operations 전체 처리)
  - §5: provider 분류를 외부 CLI subprocess (Claude/Codex CLI) + in-process IChatClient (AnthropicApi/OpenAiApi/Groq/Ollama) 2군으로 정정. cache_control 명시 부여는 AnthropicApi 만 가능. 그 외는 anchor `:477` 의 fixed prefix 순서 강제로 충분
  - §7: 5 provider 모두 schema preload 이미 eager (in-process 는 `_cachedTools`, Claude CLI 는 `--allowed-tools` allowlist, Codex CLI 는 MCP server 자체 노출). 추가 작업 없음
  - 적용 효과 표 정정: §7 단독 효과 0 으로 재계산 → 핵심은 §1+§3+§6 = 1 RT
  - Step 0 완료 표기, Step 1~7 hook 위치를 line-level 로 확정
- **v5.3** — 추가 review 결과 반영
  - **§H1** (High — API provider 의 다음 turn snapshot 누락): `ApiChatProvider._stickySnapshot: string?` field 추가. incoming `SnapshotPrefix` 가 들어오면 갱신, 없으면 sticky 유지. 매 turn 호출 시 sticky 가 multi-content 의 stable prefix 로 prepend → revision 무변경 turn 에도 LLM 이 store 상태 인지. Anthropic prompt cache 의 stable prefix hit 효과는 sticky 라서 그대로 유지 (revision 변화 시점만 cache miss). `ClearSession()` 시 함께 reset. CLI provider 는 자체 session transcript 보존 → sticky 불요
  - **§H2** (Medium — Call DAG fan-in edge 누락): `representedEdges: HashSet<struct(Guid * Guid)>` 추가. chain/fan-out 으로 표현된 edge 추적, 누락된 edge (e.g. `A → C, B → C` 의 B → C) 는 chain 끝에 `; ...` 로 별도 표기. doc grammar 의 "다중 source `(A,B) → C`" 표현은 추후 follow-up — 현재는 평탄 edge list 로 누락 차단
  - **§H3** (Medium — `StoreSnapshot.fs` untracked): commit 직전 `git add Solutions/Core/Ds2.LlmAgent/StoreSnapshot.fs` 필수. fsproj `Compile Include` 만 staged 면 clean checkout 빌드 깨짐
- **v5.2** — 3-reviewer cross-validation 결과 반영
  - **§J1** (fan-out 중복 표기): `enqueued: HashSet<Guid>` 도입 — fan-in 시 같은 자식이 두 부모의 fan-out 괄호 + 큐 중복 push 동시 차단. fan-out 자식의 후속 chain 별도 segment 노출은 의도된 동작 (LLM 이 동일 이름을 동일 노드의 분기로 인식, doc grammar 명시)
  - **§J6** (revision read race): `DsStore.Revision` getter 를 `Volatile.Read(&revision)` 으로 변경 + `RenderSnapshotEnvelopeAtomic()` extension 추가 (rev, body 단일 호출 캡쳐). UI dispatcher 외 thread 의 BumpRevision 시점에도 stale read / 캡쳐 race 차단
  - **§n5** (arrowAbbrev wildcard): 명시 코멘트로 future-proofing 책임 표기. wildcard 자체는 production 안전성 위해 유지
  - **§n6** (snapshot log 보강): `length=N prev=X` 추가 — token 측정 자동화 (Step 5) 진입 전 사전 logging
  - **§n1** (snapshot history 미누적 trade-off): 정책 의도를 코드 주석으로 명시 (revision 무변경 turn 의 LLM context recall 경로 설명)
  - **§n2** (UpdateStore 의 revision=0 시작): 두 path (ApplyNewStore vs UpdateStore) 의 의미 차이 명시
  - **Step 8 신설**: J2 (capability 비트 격상), J3 (CS9057 회수), J4 (SystemType SSOT), J5 (CLI snapshot helper) 후속 작업 항목 명시
- **v5.1** — meta review (5 reviewer) 결과 반영
  - **Critical C1**: snapshot 을 `_history` 누적 회피 → `LlmUserMessage.SnapshotPrefix` 분리, `ApiChatProvider` 가 본 turn 호출 시점에만 multi-content TextContent 로 prepend (Anthropic 시 cache_control 추가 부착, system 과 합쳐 2 breakpoint). CLI 는 prompt 본문 prepend 유지
  - **Major M1**: `ApplyTurnPlanAsync` catch 에 `_lastSentRevision = null` 추가 — apply 실패 시 stale snapshot 차단
  - **Major M2 + Critical 보너스**: `Queries.callsOf` / `apiDefsOf` / `worksOf` / `flowsOf` / `arrowWorksOf` / `arrowCallsOf` / `allProjects` 로 6곳 helper 화. 동시에 **`ArrowBetweenWorks.ParentId = systemId`** (Arrows.fs:26) 도메인 룰 발견 — v4 grammar 가 "flow 안 work-arrows" 로 가정하여 항상 빈 결과였던 버그 발견. system 단위 grouping 으로 grammar 정정
  - **Major M3**: `BumpRevision` public → internal, doc 에서 LLM 정책 설명 제거 → "monotonic mutation counter" 만 (Ds2.Editor / Ds2.LlmAgent 는 InternalsVisibleTo 이미 등록)
  - **Major M4**: `RenderSnapshotEnvelope(revision)` extension 추가 — wire format SSOT, ViewModel 1줄 호출
  - **Major M5**: `ApiProviderFactory.AnthropicProviderLabel` const SSOT — `ApiChatProvider` cache_control 분기에서 magic string 제거
  - **Minor**: `escapeXml` 적용 (delimiter injection 방어, 7곳), `kindSuffix` dead branch 정리, fan-out leaf 미큐잉 (review 1차), render 함수를 4 helper 로 분리 (`renderSystem` / `renderFlow` / `renderWorkArrows` / `renderCallDag`)

## 참고

- Anthropic prompt cache TTL: 5분 (cache_control ephemeral). 최대 4 breakpoint.
- OpenAI 자동 cache: 1024 token 이상 stable prefix, TTL 5~10분.
- "1 파일 = 1 project" 룰.
- 본 최적화는 user-perceived latency (enter → 첫 token) 를 직접 축소.
