# Phase 0 사전 실증 — 발견 사항 (2026-05-06)

본 문서는 `todo-promaker-llm-agent.md` 의 Phase 0 사전 실증을 진행한 결과 기록이다. 후속 Phase 1a 진입에 필요한 사실 + 후속 phase 에서 재참조할 수 있는 기술적 발견을 정리한다.

---

## 환경

- OS: Windows 11 Pro (10.0.26200)
- .NET SDK: 9.0.301 (실측 spike 는 .NET 10 SDK 로 빌드됨 — `dotnet new web` 기본 TFM)
- Claude CLI: 2.1.129
- 검증 시점: 2026-05-06
- 작업 디렉토리: `F:/Git/ds2/feature-greenfield-modeling/Solutions/Core/Ds2.LlmAgent/`

---

## 결정 변경 요약

| 결정 | 이전 상태 | 변경 후 상태 | 근거 |
|---|---|---|---|
| 결정 4 (Tool 채널) | 잠정 ((c) HTTP 우선, (b) stdio fallback) | **확정 (c) HTTP MCP transport** | 실증 3 통과 |
| ProjectReference 범위 | 미확정 | **`Ds2.Editor` only** | 실증 5 통과 |
| Phase 1a 진입 차단 조건 | 실증 2a / 2b / 2c / 2d / 7 | **모두 해제** | 실증 2a + 7 통과 |

---

## 실증 1 — ModelContextProtocol C# SDK NuGet

### 검증 방법
NuGet flat container API 로 패키지 버전 확인.

### 결과
- `ModelContextProtocol` 1.2.0 (정식 GA, 다운로드 8.6M+)
- `ModelContextProtocol.Core` 1.2.0
- `ModelContextProtocol.AspNetCore` 1.2.0 — HTTP transport 지원
- 0.x preview 14건 → 1.0.0-rc → 1.0.0 → 1.1.0 → 1.2.0 순으로 안정화. 1.x 부터 API stable.

### Phase 1b-c 적용 사항
- `Ds2.LlmAgent.fsproj` 또는 Promaker 측에 `ModelContextProtocol.AspNetCore` 1.2.0 PackageReference 추가
- F# 호출 가능성: C# SDK 의 attribute 기반 등록 (`[McpServerToolType]` / `[McpServerTool]`) 은 F# 에서 attribute syntax 양립 가능 (`[<McpServerToolType>]`). 그러나 `[Description]` 등 parameter attribute 가 F# parameter 에 잘 붙는지는 phase 1b 빌드 단계 검증 필요. 양립 안 되면 `IMcpServer.Tools.Add(...)` 식 manual DI 등록 우회 가능.

---

## 실증 2a — Claude CLI 4종 인자 양립성

### 검증 방법
빈 `mcp-config.json` 작성 (`{"mcpServers":{}}`) 후, 사전 turn 의 session_id 로 resume:

```bash
claude -p "say bye in 3 words" \
  --resume <session-id> \
  --mcp-config <path-to-empty-mcp-config> \
  --output-format stream-json \
  --verbose
```

### 결과
- 4종 인자 동시 사용 정상 동작
- 응답 시간: 3691ms (`duration_ms`), API 시간: 3187ms (`duration_api_ms`)
- session_id 그대로 유지 (`94b76312-...`)
- `cache_read_input_tokens`: 32415 — 이전 turn 의 prompt cache 활용 확인
- `cache_creation_input_tokens`: 24 — 새 turn 메시지만 cache 추가
- 종료 시 `{"type":"result","subtype":"success",...}` 패킷

### 시사
- Phase 1a 의 `ClaudeCliProvider` 1차 인자 조합 = `-p <msg> --output-format stream-json --verbose --resume <sid> --mcp-config <path>` (5종) 채택 가능. fallback (`--input-format stream-json` stdin 연속 모드) 불필요.

---

## 실증 3 — HTTP MCP Transport 실제 동작 (핵심)

### 검증 방법
ASP.NET Core 10 + `ModelContextProtocol.AspNetCore` 1.2.0 + `[McpServerTool]` attribute 로 toy server 구성:

```csharp
using ModelContextProtocol.Server;
using System.ComponentModel;

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();
app.MapMcp();
app.Run("http://127.0.0.1:5777");

[McpServerToolType]
public static class PingTool
{
    [McpServerTool, Description("Reply with a friendly pong message.")]
    public static string Ping([Description("Optional name")] string? name = null)
        => $"pong from spike, name={name ?? "(none)"}";
}
```

`mcp-config.json`:
```json
{
  "mcpServers": {
    "spike": {
      "type": "http",
      "url": "http://127.0.0.1:5777/"
    }
  }
}
```

호출:
```bash
claude -p "Use the spike Ping tool with name=PhaseZero. Quote ONLY the result text." \
  --mcp-config <path> \
  --output-format stream-json --verbose \
  --permission-mode bypassPermissions
```

### 결과
- init 패킷에 `"mcp_servers":[{"name":"spike","status":"connected"}]` 포함 — connect 성공
- init 패킷의 `tools` 배열에 `mcp__spike__ping` 등록됨 (lowercased)
- LLM 이 `mcp__spike__ping` tool_use 호출 (`{"name":"PhaseZero"}`)
- HTTP MCP server 응답: `{"type":"text","text":"pong from spike, name=PhaseZero"}`
- 최종 결과 (`{"type":"result","subtype":"success"}`): `"pong from spike, name=PhaseZero"`

### 시사 (결정 4 (c) HTTP 확정)
- Promaker 내부 in-process Kestrel + `ModelContextProtocol.AspNetCore` 채택 가능
- 자식 Promaker spawn / Job Object / named pipe / WinExe stdio 재바인딩 / `App.xaml StartupUri` 분리 / 손자 cascade kill 등 (b) stdio 파생 작업 **전부 불필요**
- 결정 5 적용 sub-section = 5.0 (HTTP 보안: loopback bind + ephemeral port + handshake nonce + Process.SessionId)
- Tool 이름 prefix: Claude CLI 가 `mcp__<server-name>__<tool-name>` 형식으로 자동 prefix. server name = `.mcp-config` 의 key. **모두 lowercase 변환됨** (`Ping` → `mcp__spike__ping`)

### Permission 처리 발견
- 첫 시도 (`--allowedTools "mcp__spike__Ping"`) 는 **"Claude requested permissions to use mcp__spike__ping, but you haven't granted it yet."** 로 거부됨
- `--permission-mode bypassPermissions` 로만 호출 성공
- `--allowedTools` 는 LLM 의 tool 가시성 (allowlist) 만 통제, 사용자 동의 단계는 별도 permission system. tool 이름 case-sensitivity 도 영향 추정 (`Ping` vs `ping`)
- Phase 1b 의 permission 정책: opt-in dialog (consent) + `--permission-mode` 옵션 결합 필요. Promaker 측에서 Claude CLI 의 permission prompt 를 stream 으로 받아 dialog 로 변환하거나, 첫 진입 dialog 에서 한번에 자동 승인 (`bypassPermissions` 또는 `acceptEdits`) 하는 정책 검토.

---

## 실증 5 — Ds2.Editor surface inventory

### 검증 방법
직접 source 파일 확인:
- `Solutions/Core/Ds2.Core/Store/ImportPlan.fs`
- `Solutions/Core/Ds2.Editor/Editor/ImportPlanApply.fs`
- `Solutions/Core/Ds2.Editor/Store/Nodes/Nodes.fs` 등 `[<Extension>]` 표면
- `Solutions/Core/Ds2.Core/Store/DsQuery/Queries.fs` (Queries 위치)

### 결과 — `ImportPlanOperation` DU 9종

```fsharp
type ImportPlanOperation =
    | LinkSystemToProject of projectId: Guid * systemId: Guid * isActive: bool
    | AddSystem of DsSystem
    | AddFlow of Flow
    | AddWork of Work
    | AddCall of Call
    | AddApiDef of ApiDef
    | AddApiCall of ApiCall
    | AddArrowWork of ArrowBetweenWorks
    | AddArrowCall of ArrowBetweenCalls
```

→ Phase 1 mutation tool 세트 (`add_system` / `add_flow` / `add_work` / `add_call` / `add_arrow` / `add_api_def`) 모두 커버.

### 결과 — `ImportPlanApply.applyWithUndo`

```fsharp
let applyWithUndo (store: DsStore) (label: string) (plan: ImportPlan) =
    store.WithTransaction(label, fun () ->
        for operation in plan.Operations do
            applyOperationTracked store operation)
    store.EmitRefreshAndHistory()
```

→ 단일 `WithTransaction` + `EmitRefreshAndHistory` 1회 emit. 결정 7 (d) 가정 그대로 정확.
→ `[<Extension>] static member ApplyImportPlan(store, label, plan)` 으로 호출 측 노출.

### 결과 — `Queries` 위치 / 노출

- `module Queries` 는 **`Ds2.Core.Store` namespace** 에 위치 (`Queries.fs`)
- `[<AutoOpen>]` attribute 로 `open Ds2.Core.Store` 만 하면 `getProject` / `getSystem` / `getFlow` / `getWork` / `allProjects` / `flowsOf` / `worksOf` 등 함수 직접 호출 가능
- 즉 `Ds2.LlmAgent` 가 `Ds2.Editor` 만 ProjectReference 해도 `Ds2.Core.Store.Queries` 는 transitive 로 사용 가능 (`Ds2.Editor` 가 `Ds2.Core` 를 ProjectReference 함)

### 결과 — `Ds2.Editor` mutation extensions

`Nodes.fs` 의 `DsStoreNodesExtensions`:
- `AddProject(name)` → 자체 `WithTransaction`
- `AddSystem(name, projectId, isActive)` → 자체 `WithTransaction`
- `AddFlow(name, systemId)` → 이름 중복 체크 후 자체 `WithTransaction`
- `AddWork(name, flowId)` → 이름 중복 체크 후 자체 `WithTransaction`
- `AddReferenceWork`, `AddReferenceCall`, `AddCallsWithDevice`, `AddCallWithLinkedApiDefs`, `AddCallsWithDeviceResolved`, `AddCallWithMultipleDevicesResolved`
- `MoveEntities`, `RemoveEntities`, `RenameEntity`

→ 각 extension 이 자체 `WithTransaction` 을 호출하므로 LLM tool handler 가 직접 호출하면 **turn 전체를 1 undo step 으로 묶을 수 없음** (각 호출이 1 undo step). 결정 7 (d) 그대로:
- LLM mutation tool handler = `ImportPlanBuilder` 에 `ImportPlanOperation` 누적 (extension 호출 X)
- Turn end = `store.ApplyImportPlan(label, plan)` 1회 호출 → 1 undo step

### 결과 — Authoring extensions (저수준)

`Authoring.fs` 의 `DsStoreAuthoringExtensions`:
- `WithTransaction(label, action)`, `TrackAdd`, `TrackRemove`, `TrackMutate`
- `EmitEvent`, `EmitHistoryChanged`, `EmitAndHistory`, `EmitConnectionsChangedAndHistory`, `EmitRefreshAndHistory`
- `ObserveEvents` (IObservable<EditorEvent>)
- `Undo`, `Redo`, `UndoTo`, `RedoTo`, `MergeLastTransactions`, `ClearHistory`

→ 결정 8 (Thread 모델) 의 `IUiDispatcher` 추상화에 사용할 surface 모두 노출.

### 시사
- Phase 1a 의 `Ds2.LlmAgent.fsproj` ProjectReference: **`../Ds2.Editor/Ds2.Editor.fsproj` only**
- LLM mutation tool handler signature (잠정):
  ```fsharp
  type MutationHandler<'TArgs> = 'TArgs * ImportPlanBuilder -> unit
  ```
- LLM read tool handler signature (잠정):
  ```fsharp
  type ReadHandler<'TArgs,'TResult> = 'TArgs * IUiDispatcher -> Task<'TResult>
  ```
- Turn end: `store.ApplyImportPlan($"LLM: {userMsgSummary}", plan)` 1회

### `modify_*` / `remove_*` 처리 (phase 2)

`ImportPlanOperation` DU 에 미포함:
- `RemoveEntities` / `RenameEntity` / `MoveEntities` 는 `Ds2.Editor.DsStoreNodesExtensions` 에만 존재
- Phase 2 결정 옵션:
  - (A) `ImportPlanOperation` DU 에 `RemoveEntity` / `RenameEntity` / `MoveEntity` 추가 (Ds2.Core 수정)
  - (B) LLM phase 2 tool 은 별도 undo path — `WithTransaction` outer label 직접 호출 (turn end 시점에 mutation extension 호출 묶음)
- Phase 1 범위 밖.

---

## 실증 7 — session_id 패킷 형식 + 스트림 패킷 분류

### 검증 방법
```bash
claude -p "say hi in 3 words" --output-format stream-json --verbose
```
첫 turn 의 stream-json 출력을 라인 단위로 파싱.

### 결과 — 스트림 패킷 4종

#### 1. init 패킷 (첫 패킷, session 시작)
```json
{
  "type": "system",
  "subtype": "init",
  "cwd": "C:\\...",
  "session_id": "94b76312-93ef-4f82-967a-c78430774ea4",
  "tools": ["Task","AskUserQuestion","Bash",...,"mcp__spike__ping"],
  "mcp_servers": [{"name":"spike","status":"connected"}, ...],
  "model": "claude-opus-4-7[1m]",
  "permissionMode": "default",
  "slash_commands": [...],
  "agents": [...],
  "skills": [...],
  "claude_code_version": "2.1.129",
  "uuid": "...",
  ...
}
```
- `subtype:"init"` 으로 식별 (Builder PoC 의 `system` 타입 무시 패턴 그대로 재사용 X)
- `session_id` 캡처 → 다음 turn `--resume` 인자로 사용
- `mcp_servers` health check 결과 포함 — Promaker 측 startup 검증에 활용 가능
- `tools` 배열에 mcp__ prefix tool 등록 확인 가능

#### 2. assistant 메시지 패킷
```json
{
  "type": "assistant",
  "message": {
    "model": "claude-opus-4-7",
    "id": "msg_...",
    "type": "message",
    "role": "assistant",
    "content": [
      {"type": "thinking", "thinking": "...", "signature": "..."},
      {"type": "text", "text": "..."},
      {"type": "tool_use", "id": "toolu_...", "name": "mcp__spike__ping", "input": {...}, "caller": {"type": "direct"}}
    ],
    "stop_reason": null,
    "usage": {...}
  },
  "session_id": "...",
  "uuid": "..."
}
```
- `content` 배열에 `thinking` / `text` / `tool_use` 혼재 가능
- `LlmEvent` 매핑:
  - `text` → `AssistantDelta`
  - `tool_use` → `ToolUse(id, name, input)`
  - `thinking` → 별도 이벤트 (Phase 1 에선 무시, phase 2 표시)

#### 3. user 메시지 패킷 (tool_result 회신)
```json
{
  "type": "user",
  "message": {
    "role": "user",
    "content": [
      {"type": "tool_result", "tool_use_id": "toolu_...", "content": [{"type":"text","text":"pong ..."}]}
    ]
  },
  "session_id": "...",
  "tool_use_result": [{"type":"text","text":"pong ..."}]
}
```
- 자체 echo 도 stream 에 들어옴 (감시용)
- `LlmEvent.ToolResult(toolUseId, content)` 매핑

#### 4. result 패킷 (turn 종료)
```json
{
  "type": "result",
  "subtype": "success",
  "is_error": false,
  "duration_ms": 7195,
  "duration_api_ms": 7635,
  "num_turns": 3,
  "result": "...",
  "stop_reason": "end_turn",
  "session_id": "...",
  "total_cost_usd": 0.205,
  "usage": {...},
  "modelUsage": {...},
  "permission_denials": [],
  "terminal_reason": "completed"
}
```
- `LlmEvent.SessionEnd(durationMs, costUsd, isError, stopReason)` 매핑
- `permission_denials` 비어있지 않으면 phase 1b 의 audit log 에 기록

#### 5. rate_limit_event 패킷 (보조)
```json
{
  "type": "rate_limit_event",
  "rate_limit_info": {
    "status": "allowed",
    "resetsAt": 1778057400,
    "rateLimitType": "five_hour",
    "overageStatus": "rejected",
    "overageDisabledReason": "org_level_disabled",
    "isUsingOverage": false
  },
  "session_id": "..."
}
```
- Phase 1 에선 audit log 에만 기록, ChatPanel 에는 표시 X (또는 status bar 에 5-hour reset 시각 표시)

### 시사 — Phase 1a parser FSM
```
StreamJsonLine
  → JsonElement.GetProperty("type").GetString() match
    | "system" + subtype "init"  → Provider.SessionStarted(sessionId, model, tools, mcpServers)
    | "assistant"                → content[] foreach:
                                     | "text"     → AssistantDelta(text)
                                     | "tool_use" → ToolUse(id, name, input)
                                     | "thinking" → 무시 (phase 1)
    | "user"                     → content[] foreach:
                                     | "tool_result" → ToolResult(toolUseId, content)
    | "result"                   → SessionEnd(durationMs, costUsd, ...)
    | "rate_limit_event"         → 무시 (audit log only)
    | other                      → ProviderWarning(raw)
```

---

## 부가 발견 — `cd` 명령의 cwd reset 메시지

bash tool 에서 `cd /tmp && claude ...` 호출 후 매번:
```
Shell cwd was reset to F:\Git\ds2\feature-greenfield-modeling\Solutions\Core\Ds2.LlmAgent
```
가 stdout 에 첨부됨. claude CLI 출력이 아니라 hook / shell 측 메시지로 추정. Phase 1a parser 가 stream-json 라인이 아닌 평문 라인을 받았을 때 무시할 수 있도록 try-parse 패턴 사용 권장.

---

## Skip 사유 정리 (결정 4 (c) 채택)

| 실증 | 결정 4 (b) 시 필요 | 결정 4 (c) 채택 결과 |
|---|---|---|
| 실증 0 — WPF WinExe stdio | ✅ 필수 (자식 Promaker 가 stdin/stdout 사용) | ❌ Skip (자식 spawn 없음). `App.xaml StartupUri` 변경도 skip |
| 실증 6 — 손자 Job Object 상속 | ✅ 필수 (Claude CLI → 자식 Promaker → 자식의 Job 상속) | ❌ Skip (자식 프로세스 자체 없음) |
| 실증 2b — Claude CLI MCP server 자식 spawn 패턴 | ✅ (자식 Promaker spawn 시점 설계) | ❌ Skip (HTTP server 는 Promaker 가 직접 띄움) |
| 실증 2c — per-turn cold start latency | ✅ (자식 spawn 시간 측정) | ⚠ 단순화 (HTTP request RTT 만 측정, phase 1a 진행 중 측정) |
| 실증 2d — 자식 spawn 시점 (init vs 첫 tool 호출) | ✅ (FSM 설계) | ❌ Skip |

---

## 남은 spike (phase 2 진입 시)

- 실증 4 — Codex CLI multi-turn / stream / MCP 지원 (phase 2 provider 추가 시점)
- HTTP MCP transport schema version pin — claude CLI 2.x 와 ModelContextProtocol 1.x 는 protocolVersion `"2024-11-05"` 사용 확인. 2.x 출시 시 재검증.

---

## Phase 1a 진입 사전 차단 조건 — 모두 통과

- ✅ 실증 2a — `claude -p` + `--resume` + `--mcp-config` + `stream-json` 양립
- ✅ 실증 7 — session_id `subtype:init` 형식
- ✅ 실증 5 — Editor surface inventory (`Ds2.Editor` only ProjectReference)
- ✅ 실증 3 — HTTP MCP transport 동작 (결정 4 (c) 확정)
- ✅ 실증 1 — `ModelContextProtocol.AspNetCore` 1.2.0 가용

→ Phase 1a 시작 가능.

---

# Phase 1a — Scaffold + Claude CLI echo (2026-05-06 완료)

## 신규 파일

### F# DLL `Solutions/Core/Ds2.LlmAgent/`
- `Ds2.LlmAgent.fsproj` — `Ds2.Editor` only ProjectReference, FSharp.Core + log4net
- `AssemblyInfo.fs` — `InternalsVisibleTo("Ds2.LlmAgent.Tests")`
- `Logging.fs` — `Log.provider` (Ds2.LlmAgent.Provider) / `Log.rawStream` (Promaker.LlmAgent.RawStream, default OFF appender)
- `LlmEvent.fs` — `LlmEvent` DU 8종 (`SessionStarted` / `AssistantDelta` / `Thinking` / `ToolUse` / `ToolResult` / `RateLimitEvent` / `SessionEnd` / `ProviderError`) + `McpServerStatus` record
- `StreamJsonParser.fs` — stream-json 5종 패킷 파싱 (실증 7 결과 그대로 적용). 단일 라인 → 0~N `LlmEvent` 시퀀스. JSON parse 실패 / 비-JSON 라인 / 알 수 없는 type 은 silent skip
- `ClaudeCliVersion.fs` — SemVer 검증, minimum 2.1.0, C# 친화 record `Result { IsValid; Message; VersionString }` 반환
- `ClaudeCliProvider.fs` — multi-turn provider, `ClaudeCliOptions` record + `static member Default`, `--resume` FSM (`SessionStarted` 이후 자동), `Channel.CreateBounded<LlmEvent>(256)` backpressure, `IAsyncEnumerable<LlmEvent>` 반환

### Promaker `Apps/Promaker/Promaker/`
- `Windows/LlmChatWindow.xaml` + `.xaml.cs` — 별도 Window (Phase 1d 에서 dock 통합). Ctrl+Enter 전송 / Send / Cancel / Reset 버튼 / Status bar
- `ViewModels/LlmChatViewModel.cs` — CommunityToolkit.Mvvm `[ObservableProperty]` + `[RelayCommand]`. EnsureCli 는 `Task.Run` background 후 `TaskScheduler.FromCurrentSynchronizationContext` 로 marshalling (UI block 회피)

## 변경 파일

- `Apps/Promaker/Promaker.sln` — `Ds2.LlmAgent` project 등록 (Core folder 그룹)
- `Apps/Promaker/Promaker/Promaker.csproj` — `Ds2.LlmAgent.fsproj` ProjectReference
- `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.cs` — `OpenLlmChatCommand` 1개 추가 (instance 캐싱, 재오픈 시 Activate)
- `Apps/Promaker/Promaker/Controls/Shell/MainToolbarEtcContent.xaml` — 유틸 popup 에 "LLM" 섹션 + "LLM Chat (Phase 1a)" 메뉴 항목 1개

## 의도적 미적용 (후속 phase)

| 항목 | 미적용 사유 / 후속 phase |
|---|---|
| `App.xaml StartupUri` 변경 | 결정 4 (c) HTTP 채택으로 자식 Promaker spawn 없음 → skip 영구 |
| Job Object attach + `CREATE_NO_WINDOW` | Phase 1b 에서 추가. 현재 Promaker 비정상 종료 시 `claude` orphan 가능 |
| AssistantDelta 50ms aggregation throttle | Phase 1d. 현재는 단순 string concat |
| mutation / read tool registry | Phase 1c 부터. 현재 echo only |
| Mcp config / permission mode 인자 wiring | `ClaudeCliOptions.McpConfigPath` / `.PermissionMode` 필드는 phase 1b 진입 시 채움 |
| ChatPanel dock 통합 | Phase 1d. 현재는 별도 Window |
| `--allowed-tools` / `--strict-mcp-config` | Phase 1b. tool 허용 목록 통제 |
| `Promaker.LlmAgent.ToolCall` audit logger | Phase 1b. mutation tool 호출 시점부터 의미 |

## 빌드 검증 결과

- `Solutions/Core/Ds2.LlmAgent` 단독 빌드 — 경고 0, 오류 0
- `Apps/Promaker/Promaker.sln` 전체 빌드 — 경고 0, 오류 0

## 점검 사항 처리 (review)

| # | 항목 | 처리 |
|---|---|---|
| 1 | F# DU named field → C# property 대소문자 (camelCase 접근) | 빌드 통과로 검증. F# 8 의 named DU field 는 C# 측에서 lowercase property 로 노출됨 |
| 2 | EnsureCli 동기 5초 timeout → UI block | ctor 즉시 return + `Task.Run` background → `TaskScheduler.FromCurrentSynchronizationContext` 로 결과 marshalling. 진행 중 status = "Claude CLI 검출 중…" |
| 3 | `xmlns:local` 위치 | root `<Window>` attribute 로 이동 (이전 ResourceDictionary 안). Resource 정의 위치도 `<Window.Resources>` 첫 child 로 이동 — XAML forward-reference 회피 |
| 4 | `RateLimitEvent` JSON key 케이스 | `rate_limit_info` 객체 *내부* 만 camelCase (`resetsAt` / `rateLimitType` / `isUsingOverage`) — 다른 패킷의 snake_case 와 일관성 X. spike 결과 직접 검증 후 코드 주석으로 명시 |
| 5 | fire-and-forget unobserved exception | `runProcess()` returned `Task` 에 `ContinueWith(t => Log.Error if Faulted) + writer.TryComplete()` 안전망 |
| 6 | Substring(0, 8) 길이 방어 | `sessionId.Length >= 8 ? Substring(0, 8) : sessionId` 삼항 |
| 7 | `AssistantDelta` 명명 | Phase 1d `--include-partial-messages` 도입 가능성 있어 유지 |
| 8 | `--allowed-tools` / `--strict-mcp-config` 미적용 | Phase 1a 범위 밖. `ClaudeCliOptions` 가 phase 1b 확장 가능하게 설계됨 |
| 9 | Promaker.csproj ProjectReference | NestedProjects 도 Core 폴더 (`FBF56CC3-...`) 그룹 아래에 등록 |

## 사용 방법

1. Promaker 실행 (`F5` 또는 `dotnet run`)
2. 상단 ribbon "기타" 영역의 "유틸" 토글 → popup 의 "LLM Chat (Phase 1a)" 클릭
3. 별도 Window 가 열리며 status bar 에 `Claude CLI 검출 중…` → 잠시 후 `Claude CLI 2.1.x 검출` 갱신
4. TextBox 에 메시지 입력 → "전송" 버튼 또는 Ctrl+Enter
5. 첫 turn 후 status bar 에 `session=xxxxxxxx… model=... tools=N mcp=M` 갱신
6. 다음 turn 부터 자동으로 `--resume <sid>` 적용 (이전 대화 맥락 유지)
7. "초기화" 클릭 → 새 세션 시작 (`--resume` 인자 제거)
