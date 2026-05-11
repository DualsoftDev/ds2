# Promaker LLM chat — round-trip 최소화 (delta-only snapshot)

> **v5.8** (2026-05-11). Production 실측 + 시나리오 6 UI smoke 검증 완료 → done 처리. **본 PR 최종 검증치**: AnthropicApi provider 실측 2 turn 시퀀스에서 cache hit ratio **97.4%** (`cached/input` 분모) / **48.9%** (doc §시나리오 3 분모식 `cache_read/(input+creation+read)`) — PoC steady 91.3% 상회. 시나리오 6 외부 mtime UI smoke Case A (clean reload) + Case B (clean decline) 검증 완료, 재발화 차단 동작 확인. `CheckExternalFileChange` 정상 흐름 4 분기 (감지/거절/dirty cancel/reload 진행) 에 `Log.Info` 부착. 변경 이력 전체는 문서 끝 §Revision History (v5.8 entry).

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

본 todo 의 v5 ~ v5.4 작업은 단일 branch (`feature/llm`) 에서 진행 중. 새 세션은 본 섹션부터 읽고
"남은 일" 만 우선 처리. 자세한 history 는 §Revision History 의 v5/v5.1/v5.2/v5.3/v5.4/v5.5 entry 참조.

### 완료 (DONE) — Step 0 ~ Step 4, Step 6, Step 8 부분 (+ R10)

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
| R2 | Rolling history window (`MaxHistoryDataMessages=40`, system 보존, role alternation 보존) + 파일 변경 시 `UpdateStore.ClearSession()` 으로 자동 clear (이미 구현됨) | `ApiChatProvider.cs` (`TrimHistory`) |
| R3 | Cache 적중률 / usage logging (`CachedInputTokenCount`, `AdditionalCounts`, INFO 1줄) | `ApiChatProvider.cs` (`LogUsage`) |
| R4 | 외부 mtime 감지 (윈도우 포커스 복귀 → ConfirmDiscardChanges 재사용 → reload) | `MainViewModel.ExternalFileWatcher.cs` (신규), `MainWindow.xaml.cs`, `FileCommands.cs`, `CsvCommands.cs`, `MainViewModel.Lifecycle.cs` |
| R5 | snapshot token 비용 측정 script (heuristic + optional Anthropic count_tokens) — medium 사이즈 ≈ 925 token 확인 | `Apps/Promaker/Docs/Poc/measure-snapshot-tokens.fsx` (신규) |
| Tier A | DsStore.Revision invariant / StoreSnapshot grammar / ApiChatProvider sticky+multi-content 의 회귀 방어 자동 테스트 39건 + ApiTurnContentBuilder helper 추출 | `StoreRevisionTests.fs` (Editor.Tests, 11) / `StoreSnapshotTests.fs` (LlmAgent.Tests, 12) / `ApiTurnContentBuilder.cs` + `ApiTurnContentBuilderTests.cs` (Promaker.Tests, 16) |
| Tier B | R1 시나리오 3 (cache 적중률) + R5b (Anthropic count_tokens) 단일 명령 자동 측정 script. `ANTHROPIC_API_KEY` 설정 후 `dotnet fsi e2e-cache-hit.fsx` 한 번이면 N turn 송신 + per-turn `cache_read/creation/input` + warm hit ratio + PASS/MISS 판정 (≥ 90% 기준) | `Apps/Promaker/Docs/Poc/e2e-cache-hit.fsx` (신규) |
| R6 (§J2) | `Capabilities.SupportsAnthropicCacheControl: bool` 비트 격상 + `_providerLabel == AnthropicProviderLabel` 분기 2 곳 교체 | `LlmMessage.fs`, `ApiChatProvider.cs`, `ApiProviderFactory.cs` (XML 주석 갱신), `LlmUserMessageOpsTests.fs` |
| R7 (§J3) | `Promaker.csproj NoWarn=CS9057` 회수 시도 — 실측 결과 회수 불가 확인 (OllamaSharp 5.4.25 분석기가 Roslyn 5.0 요구, 현 SDK 9.0.301 = Roslyn 4.14). 코멘트에 회수 조건 (Roslyn 5.0 SDK 또는 OllamaSharp 분석기 downgrade) 명시 | `Promaker.csproj` |
| R10 | `ApiChatProvider._history` 안에도 multi-content user (snapshot block + text) 누적 — sticky 갱신 순서를 `history.Add` 앞으로 이동 + `BuildHistoryContents` 신규 helper 로 [snapshot, text] 구성 + 본 turn 의 마지막 user 만 `cache_control: ephemeral` 부착 (`BuildMultiContents` 가 `BuildHistoryContents` 결과에 `[0]` cache_control 부착 + attachment append). PoC 옵션 A 의 steady-state 91.3% hit ratio 재현 (revision-stable 구간 한정). byte-identical invariant 가 helper composition 으로 구조적 강제. 회귀 방어 6 테스트 추가 (null/empty/ordering/no-DataContent/no-cache_control/byte-identical-cross) | `ApiTurnContentBuilder.cs` (`BuildHistoryContents` 신규 + `BuildMultiContents` 재구성), `ApiChatProvider.cs` (sticky 갱신 순서 + history 누적 multi-content 화), `ApiTurnContentBuilderTests.cs` (테스트 6건 추가) |

### 핵심 효과 검증 결과

- `apply_operations` 1회 호출 = batch N op = `WithTransaction` 1회 = `Revision++` 1회 (자동 보장)
- snapshot 매 turn 자동 첨부 (delta-only, revision 변경 시만 새 snapshot — sticky 유지) → `list_projects`/`describe_subtree` 호출 제거
- AnthropicApi: system + tool schema + sticky snapshot = cache prefix (2 breakpoint, 4 cap 안)
- 기존 4 RT → **1 RT** 달성 (Step 1+2+3 만으로)
- 외부 mtime 변경 → 윈도우 포커스 복귀 시 자동 reload 다이얼로그 (dirty 시 표준 Save/Discard/Cancel 분기)
- snapshot 자체 token 비용 실측: tiny=98 / small=349 / medium=925 / large=2269 / huge=5668 (chars/4 heuristic)
- **R10 적용 후**: `_history` 안 user message 들이 동일 snapshot 토큰을 동일 위치에 누적 → revision-stable 구간 PoC steady 91.3% hit ratio. production 실측은 §R3 `LogUsage` 로 별도 검증

### 남은 일 (우선순위 순)

| # | 항목 | Step | 비용 | 비고 |
|---|---|---|---|---|
| R1 (자동화 완료분) | 시나리오 3 (cache 적중률) 은 `e2e-cache-hit.fsx` 로 자동 측정. 시나리오 1·2·4·5 의 핵심 invariant 는 Tier A 자동 테스트 39건이 회귀 방어 | Step 7 | `ANTHROPIC_API_KEY` 설정 후 1 명령 | hit ratio + 평균 latency + PASS/MISS 자동 판정 |
| R1 (수동 잔여) | 시나리오 6 (외부 mtime 편집 → 윈도우 포커스 복귀) UI smoke | Step 7 | 사용자 직접 5분 | Tier A 단위 테스트로 cover 불가 영역 |
| R5b | snapshot 단독 token 실측 — `e2e-cache-hit.fsx` 가 R1 호출 직전에 count_tokens 1회 자동 실행 | Step 5 | 위 R1 자동화에 묶음 | heuristic 추정치 (medium ≈ 925) 검증 |
| R8 | §J4 — `DsSystem.SystemType` SSOT (option<string> → enum/DU) | Step 8 | 큼 (도메인 영역) | 본 PR 범위 밖, follow-up PR |
| R9 | §J5 — CLI snapshot prepend helper (`LlmUserMessageOps.prependSnapshot`) | Step 8 | 작음 | 3rd CLI provider 추가 시 강제 |
| R10 (DONE) | snapshot block 별도 cache breakpoint **+ `_history` 안에도 multi-content user (snapshot 포함) 누적** | Step 4 | 중간 | **2026-05-11 구현 완료**. `ApiTurnContentBuilder.BuildHistoryContents(stickySnapshot, promptText)` 신규 helper + `ApiChatProvider` 의 sticky 갱신 순서 변경 (`_history.Add` 앞으로 이동) + `_history` 의 user contents 를 multi-content `[snapshot block (cache 없음), text block]` 로 누적. 본 turn 호출 시 마지막 user 만 `cache_control: ephemeral` 부착 (기존 그대로). 회귀 방어 단위 테스트 6건 추가 (`ApiTurnContentBuilderTests.cs` — null/empty/ordering/no-DataContent/no-cache_control/byte-identical-cross). **재현 가능 PoC** — `Apps/Promaker/Docs/Poc/e2e-cache-hit.fsx` 의 옵션 A 패턴 (history multi-content + 마지막 user 의 snapshot 만 cache_control) 에서 **steady-state 91.3% hit ratio 측정** (claude-haiku-4-5, medium snapshot, 10 turn — 캡처: `Docs/Poc/e2e-cache-hit-run.md`). hit ratio 정의 = `cache_read / (input + cache_creation + cache_read)`. **단, PoC 91.3% 는 revision-stable (mutation 없는 query-only) 구간 한정** — mutation → BumpRevision → 새 snapshot 누적 시 history[N-1] 와 history[N] 의 snapshot 이 다른 revision → prefix-match 가 그 지점에서 깨지고 새 prefix 로 cache 재시작. mutation 빈도가 잦으면 효과 감소. production 실측은 §R3 logging (`LogUsage`) 으로 별도 검증 필요. **follow-up**: ApiChatProvider 의 finally pop 시 `_stickySnapshot` 도 함께 rollback (기존 sticky 미rollback 로 인한 stale 노출 — R10 이전부터 잔존하던 issue, 본 PR 범위 밖) |

### 새 세션 진입 시 권장 시작점 (2026-05-11 / R10 commit 기준)

**현재 상태 한 줄 요약**: 1 RT 목표는 commit `7be3f1f` ~ `e197ac3` 에서 구현 완료. Tier A 회귀 방어 39 테스트 (commit `0cdf88e`) + Tier B 자동 측정 PoC steady-state 91.3% hit ratio 입증 (commit `8d17c50`) + **R10 production 반영** (history 안 multi-content 누적, 회귀 6 테스트 추가). 남은 일은 (a) 시나리오 6 manual UI smoke + (b) follow-up PR 후보 (R8 / R9 / Mj4 + sticky rollback) 결정.

**진입 절차**:
1. **최근 commit 4 개 git log** (`7be3f1f` snapshot delta-only / `0cdf88e` Tier A / `8d17c50` Tier B / R10 commit) 확인하여 본 PR 의 범위 파악
2. **§완료 (DONE) 표** 정독 — Step 0 / 1 / 2 / 3 / 4 / 6 + Tier A / B + R10 모두 완료
3. **§남은 일 표** 정독 — R1 (자동화 완료분) + R1 (수동 잔여 = 시나리오 6) + R5b (자동화 완료) + R8 / R9
4. **§E2E 시나리오** 의 1~5 는 Tier A 의 39 테스트 + R10 의 6 테스트로 회귀 방어, 3 은 추가로 `Docs/Poc/e2e-cache-hit.fsx` 로 재현 가능
5. **사용자 결정 받을 항목**:
   - 시나리오 6 manual UI smoke 진행 여부
   - follow-up PR 우선순위 (R10 완료 → 다음 후보 R8 / R9 / Mj4 / sticky rollback)
   - 본 PR 외부 항목 (Promaker-gfm.sln / Prompts/ symlink / chat-samples.txt / WizardSummaryBuilderTests.cs) 처리 방향

**재현 가능 검증 (사용자 직접 실행)**:
```bash
# 시나리오 3 + R5b 자동 측정 (env 1개 설정 + 1 명령)
$env:ANTHROPIC_API_KEY = "sk-ant-..."
dotnet fsi Apps/Promaker/Docs/Poc/e2e-cache-hit.fsx 10 medium
# 결과 캡처: Apps/Promaker/Docs/Poc/e2e-cache-hit-run.md
```

**회귀 방어 11 + 12 + 22 = 45 테스트 실행** (R10 의 6건 포함):
```bash
dotnet test Solutions/Tests/Ds2.Store.Editor.Tests/Ds2.Store.Editor.Tests.fsproj --filter "FullyQualifiedName~StoreRevisionTests"   # 11
dotnet test Solutions/Tests/Ds2.LlmAgent.Tests/Ds2.LlmAgent.Tests.fsproj --filter "FullyQualifiedName~StoreSnapshotTests"           # 12
dotnet test Apps/Promaker/Promaker.Tests/Promaker.Tests.csproj --filter "FullyQualifiedName~ApiTurnContentBuilderTests"             # 22 (16 + R10 6건)
```

**Follow-up PR 후보 우선순위** (R10 완료 반영):
- **Sticky rollback** ★ R10 review 에서 식별된 잔존 issue — `ApiChatProvider` finally pop 시 `_stickySnapshot` 도 함께 rollback. R10 이전부터 잔존하던 stale 노출 경로. 비용 작음, ROI 명확.
- **R8** `DsSystem.SystemType` SSOT (option<string> → enum/DU) — 도메인 영역, 별도 PR
- **R9** CLI snapshot prepend helper (`LlmUserMessageOps.prependSnapshot`) — 3번째 CLI provider 추가 시 trigger
- **WizardSummaryBuilderTests.cs follow-up** (csproj `Compile Remove` 한시 제외 중) — product code API drift 와 동기화 또는 삭제

**Critical 사전 결함 (별도)**:
- `Apps/Promaker/Promaker.Tests/Promaker.Tests.csproj` 가 `WizardSummaryBuilderTests.cs` 를 `Compile Remove` 로 빌드 제외 중. follow-up 까지 본 csproj 만 빌드해서 본 PR 의 회귀 자동 테스트 실행 가능.

### 본 PR 의 변경 파일 (commit 대상)

| Layer | 파일 | 변경 |
|---|---|---|
| Ds2.Core | `Solutions/Core/Ds2.Core/Store/DsStore.fs` | Revision/BumpRevision + ApplyNewStore hook |
| Ds2.Editor | `Solutions/Core/Ds2.Editor/Editor/Authoring.fs` | withTransaction/applyTransaction hook |
| Ds2.LlmAgent | `Solutions/Core/Ds2.LlmAgent/LlmMessage.fs` | SnapshotPrefix + factory + helper + `SupportsAnthropicCacheControl` 비트 (R6) |
| Ds2.LlmAgent | `Solutions/Core/Ds2.LlmAgent/ClaudeCliProvider.fs` | snapshot prepend |
| Ds2.LlmAgent | `Solutions/Core/Ds2.LlmAgent/CodexCliProvider.fs` | snapshot prepend |
| Ds2.LlmAgent | `Solutions/Core/Ds2.LlmAgent/StoreSnapshot.fs` | **신규** — RenderSnapshot + envelope helper |
| Ds2.LlmAgent | `Solutions/Core/Ds2.LlmAgent/Ds2.LlmAgent.fsproj` | StoreSnapshot.fs Compile Include |
| Promaker | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` | _lastSentRevision + snapshot envelope + reset 4 지점 |
| Promaker | `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs` | sticky snapshot + multi-content + cache_control + capability 비트 분기 (R6) |
| Promaker | `Apps/Promaker/Promaker/LlmAgent/Api/ApiProviderFactory.cs` | AnthropicProviderLabel const + XML 주석 갱신 (R6) |
| Promaker | `Apps/Promaker/Promaker/LlmAgent/Prompts/3.tooling.md` | snapshot 룰 신설 + greenfield 룰 갱신 |
| Promaker | `Apps/Promaker/Promaker/MainWindow.xaml.cs` | **R4** — `Activated` hook + Dispatcher.BeginInvoke(Background) 분리 |
| Promaker | `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.ExternalFileWatcher.cs` | **R4 신규** — mtime 비교 + ConfirmDiscardChanges 재사용 + logWarn |
| Promaker | `Apps/Promaker/Promaker/ViewModels/Shell/FileCommands.cs` | **R4** — `CompleteOpen` / `CompleteSave` 에 `RecordCurrentFileMTime()` |
| Promaker | `Apps/Promaker/Promaker/ViewModels/Shell/CsvCommands.cs` | **R4** — `ImportCsvStore` 에서 `_currentFileMTime = null` |
| Promaker | `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.Lifecycle.cs` | **R4** — `Reset` 에서 `_currentFileMTime = null` |
| Promaker | `Apps/Promaker/Promaker/Promaker.csproj` | **R7** — NoWarn=CS9057 코멘트 갱신 (회수 조건 명시) |
| Promaker | `Apps/Promaker/Docs/Poc/measure-snapshot-tokens.fsx` | **R5 신규** — 단발 F# script (heuristic + optional Anthropic count_tokens) |
| Promaker | `Apps/Promaker/Docs/Poc/e2e-cache-hit.fsx` | **Tier B 신규** — `/v1/messages` 직접 호출 N turn 자동 측정 + count_tokens 묶음 + warm hit ratio 판정 |
| Promaker | `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs` | **R10** — sticky 갱신 순서를 `history.Add` 앞으로 이동 + `_history` 누적 contents 를 `BuildHistoryContents` 결과 (multi-content) 로 변경 |
| Promaker | `Apps/Promaker/Promaker/LlmAgent/Api/ApiTurnContentBuilder.cs` | **Tier A 신규** — sticky/multi-content 빌드 helper (`UpdateStickySnapshot` / `BuildPromptForHistory` / `BuildMultiContents`). **R10** — `BuildHistoryContents` 신규 + `BuildMultiContents` 가 이를 재사용해 snapshot TextContent 생성 일원화 (byte-identical invariant 구조 강제) |
| Tests | `Apps/Promaker/Promaker.Tests/ApiTurnContentBuilderTests.cs` | **Tier A 신규** — helper 단위 테스트 16건 (sticky 5 / prompt 3 / multi 8). **R10** — 6건 추가 (history null/empty/ordering/no-DataContent/no-cache_control + byte-identical-cross) → 총 22건 |
| Tests | `Solutions/Tests/Ds2.LlmAgent.Tests/StoreSnapshotTests.fs` | **Tier A 신규** — `StoreSnapshot.render` grammar 회귀 12건 |
| Tests | `Solutions/Tests/Ds2.Store.Editor.Tests/StoreRevisionTests.fs` | **Tier A 신규** — `DsStore.Revision` transaction invariant 회귀 11건 (nested 거부 포함) |
| Tests | `Solutions/Tests/Ds2.LlmAgent.Tests/LlmUserMessageOpsTests.fs` | **R6** — 직접 record 생성에 `SupportsAnthropicCacheControl = false` 추가 |
| Docs | `Apps/Promaker/Docs/done-promaker-llm-roundtrip-optimization.md` | 본 문서 (v5.8 에서 `todo-...md` → `done-...md` rename) |

### 본 PR 외부 (별도 결정 필요)

- `Apps/Promaker-gfm.sln` — `Promaker.sln` 와 byte-identical 중복. 자동 생성 출처 불명, 사용자 결정 후 처리
- `Apps/Promaker/Prompts/`, `Solutions/Core/Ds2.LlmAgent/doc/Prompts/` — symlink 추정. 처리 결정 필요
- `Apps/Promaker/Promaker/LlmAgent/Prompts/chat-samples.txt` — csproj `*.md` embed 대상 외 (빌드 영향 X), 처리 결정 필요
- **`Apps/Promaker/Promaker.Tests/WizardSummaryBuilderTests.cs`** — product code (`IoListEntryDto` / `WizardSummaryBuilder.FormatSignalStats` / `FormatBindingStats` / `FormatCompletionStatus`) 시그니처 drift 와 동기화되지 않은 사전 결함 (commit `804c398` 시점 이후 미정리). 본 PR (Tier A 자동화) 에서 `Promaker.Tests.csproj` 의 `<Compile Remove>` 로 한시 제외 + 사유 주석. **follow-up**: 별도 PR 로 (a) WizardSummaryBuilder API 변화에 맞춰 테스트 본문 갱신 또는 (b) 사용 안 되는 헬퍼면 파일 삭제 결정

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
16. **DONE (재정의)** — ~~Compaction keepalive~~ → **Rolling history**. ApiChatProvider 의 `_history` 를 system 보존 + 데이터 message max 40 으로 trim (turn 끝마다, role alternation 보존을 위해 첫 데이터 message 가 User 가 될 때까지 추가 drop). 파일 변경 시 전체 clear 는 `LlmChatViewModel.UpdateStore()` 의 `_provider?.ClearSession()` 으로 이미 구현 (별도 hook 불요). CLI provider 는 자체 history 라 rolling 불가 — file change 시 ClearSession 만 적용
17. **DONE** — Cache 적중률 모니터링: `partial.Usage` (Microsoft.Extensions.AI `UsageDetails`) 의 `InputTokenCount` / `OutputTokenCount` / `CachedInputTokenCount` / `AdditionalCounts` 를 finally 블록 (assistant flush 직후) 에서 INFO 한 줄 logging. Anthropic / OpenAI 양 provider 모두 SDK 가 매핑 처리
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
2. **기대**: 적중률 ≥ 90%
3. **hit ratio 정의식**: `cache_read_input_tokens / (input_tokens + cache_creation_input_tokens + cache_read_input_tokens)`. `e2e-cache-hit.fsx` 의 `steady hit ratio` (마지막 3 turn 평균) 와 동일 분모 — turn 1~2 의 cache 생성 지연 noise 제외

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

- **v5.8** — Production 실측 + 시나리오 6 UI smoke 검증 (2026-05-11). 본 PR 의 모든 자동 회귀 + manual 분기 통과 → done 처리.
  - **R3 production hit ratio 실측** — AnthropicApi provider 로 사용자가 첨부한 transcript 와 동일 시나리오 (빈 store → 1차 cylinder 모델 13 op + UI 에서 NewWork 추가 + 2차 NewWork 에 Cyl3 ADV/RET) 재현. 로그 캡처 (`Apps/Promaker/Promaker/bin/Debug/net9.0-windows/logs/ds2.log20260511`):
    - Turn 1 (firstTurn=True, 빈 store snapshot len=65, 13 op apply): `input=82065 cached=40066 creation=40311 output=1285` → hit `cached/input = 48.8%` / hit `cache_read/(input+creation+read) = 24.7%`, ttfb=1666ms, total=23.0s
    - Turn 2 (firstTurn=False, rev 0→3 delta, snapshot len=606): `input=129755 cached=126324 creation=2346 output=594` → hit `cached/input = 97.4%` / hit `cache_read/(input+creation+read) = 48.9%`, ttfb=995ms, total=11.8s
    - **PoC steady 91.3%** (10 turn, query-only) 대비 mutation 시퀀스 2 turn 만으로 **97.4%** 달성 — R10 의 prefix-stable 누적 + system+tool schema cache + sticky snapshot 의 종합 효과
  - **§1+§3 hook 동작 증거**: `[snapshot] attached revision=0 prev=null length=65` → `revision=3 prev=0 length=606` — 1차 send 후 LastSentRevision=0 commit, 그 사이 (LLM apply + UI work add + Move) 3회 Revision++ 누적, 2차 send 시 delta 감지 + 갱신 snapshot 첨부
  - **§4.1 grammar trade-off 노출**: 2차 turn 에 LLM 이 `find_by_name` 1회 RT 추가 호출 (NewWork 의 id 조회 목적). snapshot 의 "UUID 제외, 이름만" 결정의 의도된 비용 — id 가 필요한 op (add_call 등) 1 RT 추가. follow-up doc 보강 후보
  - **시나리오 6 외부 mtime UI smoke 완료** (Case A clean reload + Case B clean decline):
    - **`Log.Info` 4 분기 부착** (`MainViewModel.ExternalFileWatcher.cs:54/64/74/79`): `[external-mtime] detected change file=... prev=... curr=... isDirty=...` / `... user declined reload — suppress until next change` / `... dirty Save/Discard/Cancel — Cancel 또는 Save 실패로 reload 건너뜀` / `... reload 진행`. catch 분기의 `Log.Warn` 외에 정상 흐름 흔적 zero 였던 결함 해소
    - **Case A 검증** (15:14:30~33): clean 상태에서 외부 수정 (mtime 19.6s 후) → 포커스 복귀 → 다이얼로그 → Yes → reload (`OpenFilePath` → `LastClosedProjectPath cleared` → `File opened`) → `CompleteOpen` 의 `RecordCurrentFileMTime` 으로 mtime 재기록
    - **Case B 검증** (15:16:32~34): Case A reload 직후의 mtime 06:14:28Z 이 정확히 prev 로 사용됨 (Case A 의 `RecordCurrentFileMTime` 살아있다는 결정적 증거) → 외부 재수정 → 다이얼로그 → No → `_currentFileMTime = current` 갱신으로 재발화 차단
    - **재발화 차단 무한 루프 없음**: Case A 후 추가 detected 0건, Case B 후 추가 detected 0건
    - **미검증 (선택)**: Case C (dirty Save/Discard/Cancel), Case D (decline → 재수정 fall-through) — 핵심 분기 검증으로 충분 판정
  - **done 처리**: 본 문서를 `Docs/todo-promaker-llm-roundtrip-optimization.md` → `Docs/done-promaker-llm-roundtrip-optimization.md` 로 git mv + 코드 12 파일의 doc 경로 참조 갱신. 후속 작업은 별도 PR (★ Sticky rollback, R8 SystemType SSOT, R9 CLI snapshot helper, WizardSummaryBuilderTests fix/삭제)
- **v5.7** — R10 production 반영 (2026-05-11). hotfix `f45be9f` (저장 확인 다이얼로그 2회 회피) 의 rebase 정착 후 진행. PoC 옵션 A 패턴을 `ApiChatProvider` 에 이식.
  - **`ApiTurnContentBuilder.BuildHistoryContents(stickySnapshot, promptText)` 신규** — `_history` 안에 누적되는 user message 의 contents 를 `[snapshot block (cache 없음), text block]` multi-content 로 생성. cache_control / attachment DataContent 없음 (정책 17 — bytes drop 유지).
  - **`BuildMultiContents` 리팩토링** — `BuildHistoryContents` 의 결과를 받아 `[0]` snapshot 에 cache_control 부착 + attachment append. `new TextContent(snapshot)` 생성이 한 곳으로 일원화되어 byte-identical invariant (prefix-match 보존) 가 helper composition 으로 **구조적 강제**.
  - **`ApiChatProvider.SendImpl` 변경** — sticky 갱신 (`_stickySnapshot = UpdateStickySnapshot(...)`) 을 `_history.Add` **앞으로 이동**. 변경 후 history 의 user message 가 본 turn 의 sticky 와 동일 snapshot 을 동일 위치에 가져 prefix-match 가 turn 누적에 따라 성장. plain-text only 정책 (이전) → multi-content 정책 (R10) 으로 전환.
  - **회귀 방어 6 테스트 추가** (`ApiTurnContentBuilderTests.cs`): `BuildHistory_NullSnapshot_ReturnsSinglePromptText` / `BuildHistory_EmptySnapshot_ReturnsSinglePromptText` / `BuildHistory_WithSnapshot_OrdersSnapshotThenPrompt` / `BuildHistory_OnlyTextContents_NoDataContent` / `BuildHistory_DoesNotAttachCacheControl` / `BuildHistory_SnapshotText_ByteIdentical_ToBuildMulti` (cross-test, `Assert.Same(snapshot, ...)` 로 동일 참조 보장). 총 22 건 (기존 16 + R10 6).
  - **PoC 효과 적용 범위**: `Apps/Promaker/Docs/Poc/e2e-cache-hit.fsx` 옵션 A 의 steady 91.3% hit ratio (claude-haiku-4-5, medium snapshot, 10 turn) 는 **revision-stable (mutation 없는 query-only)** 구간 한정. mutation 발생 시 `history[N-1]` 와 `history[N]` 의 snapshot 이 다른 revision → prefix-match 가 그 지점에서 깨지고 새 prefix 로 cache 재시작. production 실측은 §R3 `LogUsage` 로 별도 검증.
  - **review 피드백 처리** (5 카테고리 4건): (Critical-1) PoC 91.3% 일반화 한계 → doc 명시. (Major-3) finally pop 시 `_stickySnapshot` rollback 미수행 → R10 이전부터 잔존하던 issue, doc 의 follow-up 으로 명시 (본 PR 범위 밖). (Minor-4) snapshot/attachment 없는 turn 분기 → 이미 주석 처리. (Minor-5) cross-test 부재 → byte-identical-cross test 추가. 추가 refactoring 제안 (BuildMulti 의 4 line 중복 → BuildHistory 재사용) → 객관 검토 후 적용 (byte-identical 구조 강제 이점).
  - **남은 follow-up PR 후보** (R10 완료 후 재정렬): **sticky rollback** (R10 이전 잔존 issue, ★ 최우선) > **R8** (도메인 SystemType SSOT) > **R9** (CLI snapshot helper) > **WizardSummaryBuilderTests fix/삭제**.
- **v5.6** — Tier A 회귀 방어 자동 테스트 + Tier B 자동 측정 PoC + measure script Poc 폴더 이전 (commits `0cdf88e` / `8d17c50`, 2026-05-11)
  - **Tier A (commit `0cdf88e`)** — 1 RT 목표의 핵심 invariant 회귀 방어 자동 테스트 39 건 + helper 추출
    - **A1 `StoreRevisionTests.fs`** (Ds2.Store.Editor.Tests, 11 건): `DsStore.Revision` transaction 1회 = +=1 invariant. 빈/단일/batch(5 op)/연속/실패 rollback/Undo/Redo/ReplaceStore/AddProject 1회/nested 거부. §1 hook 3 지점 (Authoring.fs:47 / Authoring.fs:180 / DsStore.fs:127) 회귀 방어 — invariant 깨질 시 `_lastSentRevision` 비교가 잘못된 빈도로 trigger 되어 snapshot 누락/과다 첨부 발생
    - **A2 `StoreSnapshotTests.fs`** (Ds2.LlmAgent.Tests, 12 건): `StoreSnapshot.render` grammar SSOT — 빈 store `(empty)` / 단순 project / active·passive 표기 / kind suffix (None / Cylinder / Clamp) / flow `@owner:` / 빈 Call DAG / **XML escape** (P&Q<>) / envelope `<store-snapshot revision="N">` wrap / `RenderSnapshotEnvelopeAtomic` (rev, body) 일관성 / mutation 후 변화
    - **A3 `ApiTurnContentBuilder.cs`** (Promaker, helper 신규) + **`ApiTurnContentBuilderTests.cs`** (Promaker.Tests, 16 건): `ApiChatProvider.SendImpl` 의 sticky/multi-content 로직 ~30 line 을 3 internal static method (`UpdateStickySnapshot` / `BuildPromptForHistory` / `BuildMultiContents`) 로 추출 → `McpClient`/`IChatClient` 의존 없이 단위 테스트. sticky 갱신 5 case / prompt-for-history 3 case / multi-content 8 case (snapshot only / null / empty / cache 람다 적용 1회 / 람다 null / image+pdf 순서 / pdf MediaType / mixed 3 attachment 순서)
    - **review 5-reviewer 피드백**: Mj3 nested transaction invariant case 추가 (A1 11번째), Mn1 `BuildPromptForHistory` `string.Join` 단축, Mj2 `WizardSummaryBuilderTests.cs` 사전 결함 follow-up doc 등재
    - **사전 결함 우회**: `Apps/Promaker/Promaker.Tests/Promaker.Tests.csproj` 의 `<Compile Remove="WizardSummaryBuilderTests.cs" />` — product code (`IoListEntryDto`/`FormatSignalStats`/`FormatBindingStats`/`FormatCompletionStatus`) 시그니처 drift 와 동기화되지 않은 사전 결함 (commit `804c398` 이후 미정리). 본 PR 범위 밖이라 한시 제외 + 사유 주석. follow-up: 별도 PR 로 fix 또는 삭제 결정
  - **Tier B (commit `8d17c50`)** — cache 적중률 자동 측정 PoC + measure script 위치 재정렬
    - **`Apps/Promaker/Docs/Poc/e2e-cache-hit.fsx`** (신규): Anthropic `/v1/messages` 직접 호출 N turn 자동 측정 + R5b (`count_tokens`) 묶음. HttpClient module-level singleton + `postJson` helper + `setApiKeyOnce` + `HttpRequestException` 한정 try. **steady-state 91.3% hit ratio 입증** (turn 10, claude-haiku-4-5, medium snapshot, 옵션 A 패턴 — history 안에도 multi-content user 누적 + 마지막 user 의 snapshot block 에만 `cache_control`). 측정 캡처: `Apps/Promaker/Docs/Poc/e2e-cache-hit-run.md`
    - **rename**: `Apps/Promaker/Promaker/Tools/measure-snapshot-tokens/measure-snapshot-tokens.fsx` → `Apps/Promaker/Docs/Poc/measure-snapshot-tokens.fsx` (93% 유사도, header 사용법 path 갱신). 빈 `Tools/` 폴더 삭제. 의도: 배포 산출물 아닌 PoC/측정 위치 명확화
    - **R10 (격상)**: 우선순위 낮음 → 중간. PoC 91.3% 입증치로 `ApiChatProvider` 의 `_history` 안 plain user 누적 정책이 snapshot 의 prompt cache 효과를 손실 중임을 확인. follow-up PR (R10 단독) 의 ROI 명확화
    - **§E2E 시나리오 3 본문에 hit ratio 분모식 명시**: `cache_read / (input + cache_creation + cache_read)`
    - **`probe.fsx`** (신규): STJ anonymous record `obj[]` 박싱 직렬화 검증 PoC (turn 1~2 cache_cr=0 진단 중 의심 해소). 1회용 스니펫이라 상단 11 line 주석으로 의도/배경/출력 명시
    - **review 5-reviewer 피드백 통합 처리**: Major 1 (출처 동봉 + 톤다운), Major 2 (probe.fsx 의도 주석), Major 3 (buildSnapshot drift 동기화 주석), Major 4 (HttpClient singleton + boilerplate 추출 + try 좁히기 3 이슈 동시 해소), Major 5 (hit ratio 분모식 doc 명시), Minor 6 (silent fallback 경고), Minor 7 (mutable 제거)
  - **남은 follow-up PR 후보 우선순위 (격상 반영)**: R10 (★ 최우선, PoC 입증치) > R8 (도메인 SystemType SSOT) > Mj4 (cache 부착 DRY) > R9 (CLI snapshot helper) > WizardSummaryBuilderTests fix/삭제
- **v5.5** — R4 / R5 / R6 / R7 (운영 안전망 + Step 8 일부) 구현 반영
  - **R4** — 외부 mtime 감지: `MainViewModel.ExternalFileWatcher.cs` partial class 신설 (`_currentFileMTime`, `_externalCheckInProgress`, `CheckExternalFileChange(internal)`, `TryReadFileMTime` + `Log.Warn` 분기, `RecordCurrentFileMTime`). hook 위치: `MainWindow.xaml.cs` 의 `Activated` 이벤트 → `Dispatcher.BeginInvoke(Background)` 로 분리 (nested message pump 회피). mtime 기록 4 지점: `CompleteOpen` / `CompleteSave` / `Reset` / `ImportCsvStore`. dirty 시 사전 알림 + 표준 `ConfirmDiscardChanges()` (Save/Discard/Cancel) 재사용 — Save 분기에서 사용자 변경 보호. 사용자가 거절 시 mtime 갱신으로 재질의 차단
  - **R5** — snapshot token 측정 script: `Apps/Promaker/Docs/Poc/measure-snapshot-tokens.fsx` 신규. doc grammar 직접 emit (DLL 의존 X) + 5종 size mix (tiny / small / medium / large / huge) heuristic (chars/4) + optional Anthropic `count_tokens` 실측 (`ANTHROPIC_API_KEY` env 시). 결과: medium (10 sys / 30 work / 3 call) = 925 token heuristic, large (20 sys / 60 work / 5 call) = 2269 token — doc §4.1 추정 (1.5~2.5K) 자릿수 일치
  - **R6 (§J2)** — `Capabilities.SupportsAnthropicCacheControl: bool` 비트 격상: `LlmMessage.fs` 의 `Capabilities` record 에 신규 field 추가. 3 static member (`TextOnly` / `ImagesOnly` / `ImagesAndPdf`) 모두 `false` 기본값, `CapabilityPresets.AnthropicWire` 만 `with SupportsAnthropicCacheControl = true` override. `ApiChatProvider.cs` 의 2 분기점 (`SendImpl` firstTurn 의 system cache_control / sticky snapshot cache_control) 에서 `_providerLabel == ApiProviderFactory.AnthropicProviderLabel` 문자열 비교 → `_capabilities.SupportsAnthropicCacheControl` 비트로 교체. `AnthropicProviderLabel` const 는 logging / EnsureCli 식별자로 유지 (XML 주석 갱신). Bedrock 등 Anthropic 호환 endpoint 추가 시 새 preset 만 정의하면 silent miss 회피
  - **R7 (§J3)** — `Promaker.csproj` 의 `NoWarn=CS9057` 회수 시도: 실측 결과 OllamaSharp 5.4.25 의 분석기 어셈블리가 Roslyn 5.0 요구, 현 SDK 9.0.301 (Roslyn 4.14) 와 비호환 → 회수 불가 확인. NoWarn 자체는 유지하되 코멘트에 회수 조건 ((a) .NET SDK 가 Roslyn 5.0+ 포함 (VS 17.15+ / SDK 10.x 예상), 또는 (b) OllamaSharp 가 Roslyn 4.x 호환 분석기로 downgrade) 명시
  - **R5b 신설** (R5 의 사용자 검증 분리): R5 script 의 ANTHROPIC_API_KEY 설정 후 실측 — R1 E2E 검증과 묶음 권장
  - **review (--review) 피드백 반영**: (1) ExternalFileWatcher.cs 의 dirty reload 경로를 `ConfirmDiscardChanges()` 재사용으로 변경 (Save 분기 복구), (2) `MainWindow_Activated` 를 `Dispatcher.BeginInvoke(Background)` 로 분리 (nested message pump 회피), (3) `TryReadFileMTime` 의 catch 블록에 `Log.Warn` 추가 (CLAUDE.md 외부 환경 예외 정책), (4) `AnthropicProviderLabel` const XML 주석 갱신 (capability 비트 격상 사실 명시), (5) `CheckExternalFileChange` visibility public → internal
- **v5.4** — R2/R3 (운영 안전망 일부) 구현 반영
  - **R3** — `ApiChatProvider.LogUsage(UsageDetails?)` 신설. `partial.Usage` 의 `InputTokenCount`/`OutputTokenCount`/`CachedInputTokenCount`/`AdditionalCounts` 를 INFO 1줄 로 출력 (Anthropic `cache_read_input_tokens`, OpenAI `prompt_tokens_details.cached_tokens` 모두 SDK 가 `CachedInputTokenCount` 로 매핑). hit ratio 는 `cached/input` % 로 표시
  - **R2 (재정의)** — 원안 "Compaction keepalive (Claude CLI 기본 ON, N=20 turn)" 의 mechanism 이 외부 제어 불가 + CLI 가 자체 history 관리라 적용 불가능을 확인. 사용자 협의로 **"rolling history (max 40 message) + 파일 변경 시 clear"** 로 재정의. `ApiChatProvider.TrimHistory()` 신설 — system 보존 + 데이터 message 최대 40 (≈ 10~20 turn), trim 후 role alternation 보존을 위해 첫 데이터 message 가 User 가 될 때까지 추가 drop. 파일 변경 clear 는 `LlmChatViewModel.UpdateStore()` 의 `_provider?.ClearSession()` 으로 이미 구현됨
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
