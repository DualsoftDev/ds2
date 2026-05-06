# Promaker 대화형 LLM agent 통합 설계 todo

## 작업 목표

WPF C# 데스크탑 앱 **Promaker** 에 **대화형 LLM agent 패널** 을 통합한다. 사용자는 채팅으로 모델(Project / DsSystem / Flow / Work / Call / ApiDef / Arrow) 을 점진적으로 만들고, 이미 만든 모델에 대해 LLM 과 대화한다.

- 통합 형태: **Promaker 내부 dock panel** (별도 console exe 가 아님)
- LLM provider 우선순위: **Claude CLI subscription 1st-class**, Codex CLI best-effort, API/SDK/Ollama 는 후순위
- Tool 채널: **MCP transport (HTTP 우선, stdio fallback)** — 결정 4 잠정, 사전 실증 3 후 확정. (c) HTTP 채택 시 Promaker in-process Kestrel + loopback bind, (b) stdio 채택 시 Promaker 자기 자신 `--mcp-server` 재진입
- 다중 Promaker 인스턴스 동시 실행 지원 (인스턴스별 격리된 MCP 서비스)
- Undo: **`ImportPlan` 활용 — 1 LLM turn = 1 undo step** (F# editor 무수정, 결정 7)

---

## 배경 / 맥락

### Promaker 현황 (요약)
- WPF C# (`net9.0-windows`, `UseWPF=true`, `OutputType=WinExe`)
- 위치 (이 문서 기준): `../../../Apps/Promaker/` (ds2 `feature-greenfield-modeling` branch). 주 통합은 ds2 main 흡수 후 진행 가능.
- 진입점: `App.xaml.cs` → `MainWindow` → `MainViewModel` (CommunityToolkit.Mvvm `ObservableObject` / `RelayCommand` source generator 활용)
- F# core 직접 ProjectReference: `Ds2.Core` / `Ds2.Editor` / `Ds2.Aasx` / `Ds2.Mermaid` / `Ds2.IOList` / `Ds2.CSV` / `Ds2.View3D` / `Ds2.Runtime` / `Ds2.Backend`
- **모델 SSOT (Single Source of Truth) = `DsStore` (F# 인스턴스, Promaker 프로세스 메모리)**. 디스크 파일 (SDF / JSON / AASX / Mermaid) 은 SSOT 의 스냅샷.
- 변경 전파: `_store.ObserveEvents()` → `EditorEvent` IObservable → MainViewModel 이 dispatcher 로 받아 `RebuildAll()` (트리 / 캔버스 / 시뮬레이션)
- Mutation 경로: `RelayCommand` (예: `AddSystem`, `AddFlow`, `AddWork`, `AddCall`) → `_store.AddSystem(...)` / `_store.AddFlow(...)` 등 F# API
- Undo/Redo: `_store.Undo()` / `_store.Redo()` + `HistoryItems` 패널
- Dialog 추상화: `IDialogService` (테스트성 + 추상)
- I/O 포맷: SDF / JSON / AASX / Mermaid (`.md`)
- 부가 기능: 3D 뷰 (WebView2), 시뮬레이션, IO/PLC/Tag wizard 다이얼로그, 한국어/영어 로컬라이제이션, 다크/라이트 테마

### Ev2 측 학습 자산 (별도 repo `/f/Git/ev2/master`, 직접 통합 X — 패턴 차용만)
- `/f/Git/ev2/master/solutions/Ev2.Backend/todos/todo-greenfield-model-builder.md` — Python 기반 PoC (GreenfieldModelerPoC) 별도 진행 중. 5 카테고리 provider 추상화 / system prompt / tool schema / MCP 채널 분기점 등의 **설계 결정 자산** 을 본 작업이 차용.
- `/f/Git/ev2/master/solutions/Ev2.Backend/src/Ev2.Oracle/Builder/pipeline/llm_providers/claude_cli.py` — Builder 의 stateless 1-shot Claude CLI provider. 본 작업의 multi-turn provider 와는 인자 조합이 다르므로 **재사용 불가**, process spawn / Job Object / stream-json 파싱 **유틸성 패턴만 차용**.

### 사용자 선호 (글로벌 CLAUDE.md)
- 언어 선호: F# > C#
- 정석 해결 선호 (우회 X), 기존 코드 재사용 / refactoring 우선
- F# 가능한 곳은 F# 로, C# 는 WPF binding 등 불가피한 곳에만

---

## 현재까지 결정된 설계 방향

### 결정 1 — 통합 형태: Promaker 내부 dock panel
- 외부 console exe 가 아니라 chat panel 을 Promaker MainWindow 의 한 dock 영역으로 추가
- 사용자가 GUI 직접 편집과 LLM 대화를 **동시 사용** 가능 — 양쪽 mutation 이 같은 DsStore 를 만짐
- EditorEvent 가 자연스러운 sync 채널 역할 (사용자 GUI 변경 → LLM session context 주입)

### 결정 2 — 구현 언어: F# DLL + C# WPF binding (+ Tool registry 흡수)
- **F# DLL `Ds2.LlmAgent.fsproj` 신규** — 비-UI 핵심 로직
  - `ILlmProvider` 인터페이스 — **phase 1 은 `ClaudeCliProvider` 1종 concrete only**, 인터페이스는 회의적/실험적 ("phase 2 의 Codex 실증 후 재설계 가능" 단서 명시) — review M5 반영
  - 공통 Event 타입 (`AssistantDelta` / `ToolUse` / `ToolResult` / `SessionEnd` / `ProviderError`)
  - `Capabilities` flag (streaming / resume / structured_tools / mcp_stdio)
  - **Tool registry + tool handler = "JsonNode → Ds2.Editor extension 호출 → JsonNode" 한 줄 함수의 dictionary** (결정 6 흡수 — review M8)
    - mutation tool: `Ds2.Editor` 의 extension API (예: `store.AddSystem(name, projectId, isActive)`) 직접 호출
    - read tool: `Ds2.Core.Queries` 의 read API 직접 호출
  - Tool JSON Schema 정의
  - MCP server 어댑터
  - System prompt / 환각 방지 지침
- **C# 영역 (Promaker 안)** — WPF binding 만
  - `ChatPanel.xaml` + `ChatPanelViewModel.cs`
  - `IAsyncEnumerable<LlmEvent>` 구독 → `ObservableCollection<ChatTurn>`
  - `RelayCommand` (Send / Cancel / Reset)
  - Provider 선택 / 설정 binding
- **분리 근거** (review M9 반영): F# 미적 선호가 1차 이유 아님. **CommunityToolkit.Mvvm `[ObservableProperty]` source generator 가 C#-only** 이라 F# 단독 ViewModel 은 Promaker 의 기존 MVVM 패턴을 깸. F# core 호출의 자연스러움은 보너스.
- **ProjectReference** (review M3 반영):
  - `Ds2.LlmAgent` → `Ds2.Editor` (mutation extension) + `Ds2.Core` (DsStore / Queries / EditorEvent)
  - `Promaker.csproj` → `Ds2.LlmAgent` 추가
  - **DsStore 자체는 read-only dictionary + JSON I/O**. mutation API (`AddSystem` 등) 는 모두 `Ds2.Editor` 의 `[<Extension>] static member`. tool handler 는 RelayCommand 핸들러 떼어내는 게 아니라 **`Ds2.Editor` extension 을 직접 호출** (M3).

### 결정 3 — Provider 우선순위 (subscription 우선)
- **Phase 1 (MVP) — Claude CLI 1종만 1st-class**
  - 추가 비용 없음 (사용자가 이미 구독 중), API key 발급/관리 부담 없음
  - 사용자 zero-config 진입 가능
  - Codex CLI 는 동일 phase 에서 **실증만** (multi-turn / stream / MCP 지원 확인). 가능하면 추가, 불가하면 phase 2 로 이월
- **Phase 2** — Codex CLI 정식 또는 OpenAI API (Codex 가 막힌 경우 보험), Anthropic API (구독 한도 초과 사용자용), Ollama (local)
- 추상화 인터페이스가 모두 흡수하므로 등록 테이블만 조정하면 됨

### 결정 4 — Tool 채널: MCP transport (잠정 — 사전 실증 3 후 확정)
**잠정 채택 우선순위** (review C4 반영):
- **(c) Claude CLI HTTP MCP transport** — 가능하면 1순위. Promaker 가 in-process HTTP MCP server 띄우고 Claude CLI 는 `--transport http` 로 접속. 자식 프로세스 / Job Object / named pipe 스택 전체 불필요. **사전 실증 3 (~30분 spike) 으로 동작 확인 시 (b) 폐기**.
- **(b) Promaker `--mcp-server` 모드 재진입 (stdio)** — (c) 가 막힐 때 fallback. 아래 세부는 (b) 채택 시 적용:
  - 부모 Promaker → Claude CLI 기동 시 동적으로 `.mcp-config` JSON 생성 → Claude CLI 가 자식 Promaker 를 `--mcp-server` 로 spawn
  - 자식 Promaker (MCP server 모드) 는 부모 Promaker 와 **named pipe** 로 통신, MCP stdio 와의 양 채널을 중계
  - 단일 바이너리, 배포 단순. 명령행 진입점 분기를 처음부터 설계 필요.
  - **WPF WinExe 제약** (review C2 반영):
    - `App.xaml` 의 `StartupUri="MainWindow.xaml"` **반드시 제거** + `App.OnStartup` 에서 argv 파싱 후 MainWindow 명시 호출
    - `--mcp-server` 모드는 WPF 진입 자체 skip (StartupObject 직접 작성 또는 OnStartup 분기에서 `Application.Shutdown` 회피하며 console-mode 진입)
    - WinExe 는 stdin/stdout 이 `TextReader.Null` / `TextWriter.Null` 로 묶임 → `GetStdHandle` + `Console.SetIn/SetOut` 재바인딩 필요 (사전 실증 0)
- (a) 별도 bridge exe — 폐기 (배포 / sync drift 비용 > 효용)

### 결정 5 — 다중 인스턴스 격리 + IPC 보안

> 결정 4 의 transport 분기에 따라 적용 sub-section 이 다름. (c) HTTP 채택 시 5.0, (b) stdio 채택 시 5.1~5.6. 5.7 은 양쪽 공통.

#### 5.0 HTTP transport 보안 (결정 4 (c) 채택 시)
- **Loopback-only bind** (`127.0.0.1`) — 외부 NIC 노출 차단
- **OS-assigned ephemeral port** — port 충돌 방지 (다중 인스턴스)
- **`.mcp-config` 에 url + handshake nonce 기록** (ACL 명세는 5.3 동일 적용)
- **첫 요청 handshake nonce 검증** — Claude CLI 가 첫 요청 헤더 (e.g. `X-Promaker-Nonce`) 로 송신, server 가 비교 → 불일치 시 401 + connection close
- **Process.SessionId 식별** — `.mcp-config` 파일명에 `<WindowsSessionId>` 포함하여 RDP / Fast User Switching 격리 (5.3)
- **HTTP MCP transport schema 버전 핀** — Phase 1b-c 진입 시 명시 (사전 실증 3 sub-task)
- **Kestrel cold start 측정** (review 2차 Minor R4) — chat 세션 시작 latency 영향 확인

#### 5.1 Pipe 이름 / 식별자 (결정 4 (b) stdio 채택 시 — 이하 5.6 까지)
- Pipe 이름: `\\.\pipe\Promaker-Mcp-<WindowsSessionId>-<SessionGUID>` (review 2차 R5: `Process.GetCurrentProcess().SessionId` 포함 — RDP / Fast User Switching 격리)
  - SessionGUID: PID 재사용 / chat 재시작 충돌 방지
- 부모 Promaker 가 chat 세션 시작 시 GUID 생성, `.mcp-config` 의 args 또는 env 로 자식에게 전달
- ParentPID 는 log/debug 메타로만 (review 1차 Mi2)

#### 5.2 Pipe SECURITY_DESCRIPTOR (review 2차 R5 Critical)
- pipe 생성 시 명시적 SECURITY_DESCRIPTOR:
  - DACL: 현재 user 의 SID FullControl only, 그 외 deny
  - SACL/Owner: current user
- 생성 옵션: `PIPE_REJECT_REMOTE_CLIENTS` flag (Windows Vista+) — remote SMB 접속 거부
- 부모 server 측 connect 수락 직후 **handshake nonce 검증**:
  - `.mcp-config` 안에 short-lived secret (32-byte random) 기록
  - 자식이 첫 메시지로 nonce 송신, 부모가 비교 → 불일치 시 connection 즉시 close
  - 같은 user 의 다른 logon session 또는 악성 프로세스가 GUID leak 으로 connect 해도 nonce 없으면 차단
- 부모는 connect 수락 후 `GetNamedPipeClientProcessId` 로 client PID 조회 → expected (자식 Promaker spawn 추적) 와 비교 보조 검증

#### 5.3 `.mcp-config` 임시 파일 ACL
- 위치: `%TEMP%\Promaker\mcp-<WindowsSessionId>-<GUID>.json` (sweep scope 가 자기 session 안에서만 동작하도록)
- ACL 명세 (review 1차 M10 / Mi1, 2차 R5 보강):
  - Owner = current user
  - DACL = user FullControl only
  - `FileSecurity.SetAccessRuleProtection(true, false)` 로 inheritance 차단
  - %TEMP% redirect 환경 (group policy / roaming) 의 ACL drift 검증 후 적용 (review 2차 Minor R5)

#### 5.4 stale `.mcp-config` sweep (review 2차 R2 M3 — self-race 방지)
- Sweep 조건 (AND):
  1. 자기 WindowsSessionId 안의 파일만 (다른 session 의 파일 건드리지 않음)
  2. 파일 안 ParentPID 가 죽어있음 (`Process.GetProcessById` 실패 또는 다른 image)
  3. mtime > 5분
- 자기 자신이 막 만든 파일 보호: 생성 직후 own-pid lock file 또는 PID-기반 own check
- 비정상 종료 leak 회수가 목적, 실행 중 파일은 절대 삭제 X

#### 5.5 자식 Promaker `--mcp-server` 모드 진입 sequence
- argv `--parent-pid <N>` + `--session <GUID>` + `--nonce <secret>` 파싱
- pipe 이름 조립 (5.1)
- **race FSM 재기술 — review 2차 R2 Critical**:
  - 부모 ↔ 자식 ↔ Claude CLI 의 spawn 순서: 부모 → Claude CLI spawn → Claude CLI 가 lazy 시점에 자식 Promaker spawn (init 시? 첫 tool 호출 시?). **lazy spawn 시점 자체가 사전 실증 2 의 sub-task** (Phase 0 측정).
  - 부모는 chat 세션 시작 즉시 (= Claude CLI spawn 이전) `WaitForConnectionAsync` 시작 — 자식이 언제든 connect 가능하게 listen 상태 유지
  - 자식은 connect retry policy (3회 × 100ms backoff)
  - connect 후 nonce handshake (5.2)
- MCP stdio (Claude CLI ↔ 자식) ↔ named pipe (자식 ↔ 부모) 중계

#### 5.6 라이프사이클 / 좀비 방지
- **부모 → Claude CLI 를 Windows Job Object 에 attach** (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`)
- **⚠ 손자(자식 Promaker) Job 상속 미보장** (review 1차 M1): Claude CLI 는 Node.js, `CREATE_BREAKAWAY_FROM_JOB` 또는 자체 Job 생성 시 cascade 빠져나감. 사전 실증 6 으로 검증 필수
- 보험: 자식 Promaker **자체 watchdog** — review 2차 R2 M4 정정: "polling" 이 아니라 별도 thread 가 `WaitForSingleObject(parent_handle, INFINITE)` 으로 **blocking wait**, 부모 종료 신호 시 self-exit. + named pipe broken 감지 시 즉시 self-exit (이중)
- 자식 Promaker 는 MCP stdio EOF / pipe broken 감지 시 즉시 self-exit

### 결정 6 — (결정 2 로 흡수, 결정 7 (d) 와 통합)
> 1차 review M8 + 2차 review R1 로 본 결정은 **결정 2 의 "Tool registry 공통 invoker" + 결정 7 (d) "ImportPlan 활용"** 두 결정으로 분해 흡수.
>
> **결과**:
> - Mutation tool handler = `ImportPlanBuilder` 에 `ImportPlanOperation` 누적하는 한 줄 함수 (결정 2)
> - Read tool handler = `Queries` 호출 + lightweight snapshot + projection (결정 8 thread 모델)
> - Turn end 에 단일 `store.ApplyImportPlan(label, plan)` 호출 → 1 undo step (결정 7 (d))
>
> 본 항목은 trace 보존용 placeholder. 본문 내용은 결정 2 / 7 / 8 참조.

### 결정 7 — Undo 단위: ImportPlan 활용 (review 2차 R1 — 정석 path 확정)
> ⚠ **review 2차 R1 (검증 완료) 로 결정 7 이 (c) 잠정에서 (d) ImportPlan 활용 으로 격상.** 1차 review 의 (a)/(b)/(c) 비교는 더 단순한 path 를 놓친 것.

**검증 결과 (직접 source 확인)**:
- `Ds2.Core/Store/ImportPlan.fs` — `ImportPlanOperation` DU (`AddSystem` / `AddFlow` / `AddWork` / `AddCall` / `AddApiDef` / `AddApiCall` / `AddArrowWork` / `AddArrowCall` / `LinkSystemToProject`)
- `Ds2.Editor/Editor/ImportPlanApply.fs:34-38` — `applyWithUndo` 가 **단일 `store.WithTransaction(label, ...)` 으로 plan 의 모든 operation 을 한 묶음 처리** + `EmitRefreshAndHistory()` 1회 emit
- `[<Extension>] ApplyImportPlan(store: DsStore, label: string, plan: ImportPlan)` — C# / F# 호출 측 노출 완료

**채택 (d) ImportPlan 활용**:
- LLM 1 turn 의 모든 mutation tool 호출 → 각 handler 가 `ImportPlanOperation` 누적 (in-memory plan)
- Turn end 시점에 `store.ApplyImportPlan("LLM: <user msg 요약>", plan)` 1회 호출 → **1 undo step 자동 생성**
- F# editor **무수정** (사용자 철학 "기존 재활용 90점" 부합)
- nested transaction 충돌 자연 해소 (outer transaction 1회만)
- review M4 의 deferred-apply 모델이 자연스럽게 따라옴 (turn 안 mutation 은 plan 누적, turn end batch apply)
- 1차 review 의 (a)/(b)/(c) 모두 폐기

**잔여 검증 (사전 실증 5 재기술)**:
- Phase 1 mutation tool 세트 (`add_system` / `add_flow` / `add_work` / `add_call` / `add_arrow` / `add_api_def`) 모두 `ImportPlanOperation` 으로 표현 가능 — **이미 검증 (DU 가 모두 커버)**
- ⚠ **`modify_*` / `remove_*` 는 `ImportPlanOperation` 미포함** — phase 2 에서 ImportPlan 확장 vs 별도 undo path 결정 (phase 1 범위 밖)
- LLM tool 호출 도중 schema validation 실패 → plan 일부 누적 후 turn end 직전 fail-safe 정책 (전체 plan 폐기 + LLM 에 전체 에러 반환)

**read tool 과의 상호작용 — review 2차 R3 mi2 / R4 M2 통합**:
- LLM 의 read tool 은 **현재 DsStore 상태 (plan apply 전)** 를 본다 — turn 안에서 in-memory plan 진행 상태는 LLM 에게 노출 안 함. LLM 은 "내가 방금 add_system 한 결과" 를 바로 못 보는 게 정상 (turn end 적용 후 다음 turn 에서 확인)
- 또는: in-memory plan 을 read tool 응답에 합쳐서 보여주는 옵션 (구현 비용 ↑)
- Phase 1 디폴트 = 전자 (단순). LLM system prompt 에 "mutation 결과는 turn end 에 적용" 명시.

### 결정 9 — 비동기 표현 통일 (review 2차 outlier R3 → 잠정 확정)
**LLM 측 stream → ChatPanel 사이의 비동기 표현은 `IAsyncEnumerable<LlmEvent>` 1종으로 통일**.
- 후보 비교:
  - `IObservable<T>` (Rx) — Promaker 의 `_store.ObserveEvents()` 가 이미 사용 중이라 일관성 ↑. 단 cold/hot 의미와 backpressure 가 모호
  - **`IAsyncEnumerable<T>` (C# 8+ / F# 8 `taskSeq`) — 채택**. cancellation token 자연 통합 + `await foreach` / F# `taskSeq` 자연. backpressure (Phase 1a 의 `Channel.CreateBounded` 후 `ReadAllAsync()`) 와 자연 합류
  - `Event<T>` / `IEvent<T>` (F#) — fire-and-forget, backpressure 없음. provider stream 부적합
- **DsStore 내부 `EditorEvent` 는 기존 `IObservable` 그대로** (코드베이스 패턴 보존, 결정 8 dispatcher marshalling 으로 흡수)
- **provider ↔ ChatPanel 사이의 LlmEvent 는 `IAsyncEnumerable`** — 신규 도입 영역이므로 표현 통일 우선
- 양쪽 사이 어댑터는 Phase 1a 의 stream backpressure (`Channel.CreateBounded`) 가 자연 다리

### 결정 8 — Thread 모델 (review 1차 C3 + 2차 R4 Critical / R4 M1 / M4 통합)
**모든 mutation/read tool handler 는 부모 Promaker UI Dispatcher 에 async marshalling, AssistantDelta 등 stream-only 이벤트는 dispatcher 우회**.
- 근거: `Ds2.Editor/Editor/Authoring.fs` 의 `StoreEditorState` 가 `mutable CurrentRecords / SuppressEvents / CurrentAffectedIds` 를 lock 없이 사용. `EventBus.Trigger` 도 동기 호출. `Dictionary<Guid, _>` enumeration race 가능.
- **Sync vs async — review 2차 R4 Critical 반영**:
  - `IUiDispatcher.InvokeAsync<'T>(action: unit -> 'T): Task<'T>` (Background priority) — sync `Invoke` 금지
  - 근거: Promaker `MainViewModel.cs:652-679` 의 `RequestRebuildAll` 도 이미 `BeginInvoke` + `DispatcherPriority.Background` 패턴. Dispatcher.Invoke (sync) 진입은 사용자 GUI drag / 큰 RebuildAll 진행 중 stream 처리 thread block → AssistantDelta 표시 frozen.
  - AssistantDelta / ProviderError / SessionEnd 같은 **stream-only 이벤트는 dispatcher 우회**, ChatPanel ViewModel 의 ObservableCollection 갱신만 별도 dispatcher (이미 ViewModel 측 책임)
- **EditorEvent coalescing 의존 — review 2차 R4 M1 반영**:
  - 1 turn N mutation 결과 EditorEvent N개 → `MainViewModel.RequestRebuildAll` 의 `_rebuildQueued` flag + `BeginInvoke(...,Background)` 자연 coalescing 으로 N→1 합쳐짐
  - dispatcher marshalling 도 **async + Background priority** 여야 coalescing 유지. sync 진입은 매번 dispatcher pump 풀려 coalescing 깨짐
  - 결정 7 (d) ImportPlan 채택으로 turn 단위 EditorEvent = 1회 (`EmitRefreshAndHistory` 1번) → coalescing 불필요해짐. 이중 안전망.
- **Snapshot lightweight — review 2차 R4 M4 반영**:
  - Read tool dispatcher 안에서는 `.Values.ToArray()` 같은 lightweight copy 만, projection / serialization 은 background. 큰 모델에서 UI thread 점유 회피
  - 장기적으로 ImmutableDictionary 검토 (phase 3+)
- 구조:
  - F# DLL 에 `IUiDispatcher` 추상 주입
  - C# 측에서 Promaker `Dispatcher.CurrentDispatcher` 를 어댑터로 감싸 주입 (`InvokeAsync` = `Dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task`)
- review 1차 Mi5 (`Event<EditorEvent>` 동기 trigger) / Mi6 (Dictionary race) 자연 해소

---

## 남은 할 일

### Phase 0 — 사전 실증 + surface inventory (review 1차 + 2차 반영, scaffold 진입 전 필수)
> 모든 spike 30분~2시간 이내 종료. 결과로 결정 4 (Tool 채널) 잠정 → 확정 전환. 결정 7 은 (d) ImportPlan 으로 이미 확정, 사전 실증 5 는 surface 검증 spike 로 변경.

- [ ] **사전 실증 0** (review 1차 C2): WPF `WinExe` 가 `--mcp-server` 인자로 진입했을 때 `GetStdHandle` + `Console.SetIn/SetOut` 재바인딩 후 stdin/stdout 으로 1KB MCP JSON-RPC 왕복 echo 가능한지 확인. 불가 시 결정 4 (b) 자체 폐기.
  - Sub-task (review 2차 R5 M5): `<DisableWinExeOutputInference>` + 별도 `static int Main` 진입점 분리 vs `App.OnStartup` 분기의 designer / Hot Reload / Theme 영향 측정
- [ ] **사전 실증 3** (review 1차 C4, 우선순위 최상위): `claude mcp add --transport http <url>` 또는 `.mcp-config` 의 `transport: "http"` 가 동작하는지 30분 spike. 동작 시 결정 4 → (c) 확정, 사전 실증 0/2/6 skippable.
  - Sub-task (review 2차 Minor R2): HTTP MCP transport schema 버전 핀
- [ ] **사전 실증 1**: `ModelContextProtocol` C# SDK (.NET 9 공식) 의 F# 호출 가능성 (결정 4 (c) / (b) 양쪽 필요)
- [ ] **사전 실증 2 — 보강** (review 2차 R2 Critical / R4 Major): Claude CLI 멀티턴 인자 양립성 + spawn 시점 정량 측정 (결정 4 (b) 채택 시 모두; (c) 시 4종 인자만)
  - **Sub-task 2a** (review 2차 R2 Critical): `claude -p <msg> --resume <sid> --mcp-config <path> --output-format stream-json` 4종 인자 조합이 실제 동작하는가. `-p` 는 본래 print-and-exit 의도라 `--resume` 양립이 ev2 측에서도 미검증 spike. **양립 불가 시 `--input-format stream-json` stdin 연속 모드로 전환 검토** — Phase 1a 진입 차단 조건
  - **Sub-task 2b**: Claude CLI 가 매 turn MCP server 자식을 re-spawn vs long-running
  - **Sub-task 2c** (review 2차 R4 Major): per-turn spawn-to-first-token / handshake / 메모리 정량 측정. cold start 1~3s/turn × 누적 측정
  - **Sub-task 2d** (review 2차 R2 Critical): Claude CLI 의 자식 (MCP server) spawn 시점 — init 시 vs 첫 tool 호출 시
- [ ] **사전 실증 4**: Codex CLI multi-turn / stream / MCP 지원 — spike 만, 결과로 phase 2 작업 분량 결정 (review 1차 Mi3)
- [ ] **사전 실증 5 — 재기술** (review 2차 R1 Critical / R1 M5 + R3 M5): Ds2.Editor extension / `ImportPlanOperation` surface inventory (~30분)
  - phase 1 mutation tool 세트 (`add_system` / `add_flow` / `add_work` / `add_call` / `add_arrow` / `add_api_def`) 가 모두 `ImportPlanOperation` 으로 표현 가능 — **이미 확인** (DU 가 모두 커버)
  - `modify_*` / `remove_*` 가 `ImportPlanOperation` 미포함 — phase 2 처리 방식 결정
  - `Ds2.Editor` 의 `[<Extension>]` 노출 surface 와 `internal` 차이 점검 (1차 review M3 보강)
  - read tool 용 `Queries` 노출 surface 점검 → `Ds2.LlmAgent` 가 `Ds2.Editor` 만 ProjectReference 하면 충분한지 확인 (review 2차 R3 M5)
- [ ] **사전 실증 6** (review 1차 M1, 결정 4 (b) 채택 시만): 손자(Claude CLI 가 spawn 한 자식 Promaker) 가 부모 Job Object 안에 머무는가
- [ ] **사전 실증 7 — 신설** (review 2차 R2 Critical): session_id 패킷 실제 형식 측정. **claim**: `{"type":"system","subtype":"init","session_id":"..."}` (Builder PoC 의 `parse_stream_line` 은 `system` 타입 무시 — 그대로 재사용 불가). parser 가 `subtype` 분기 필요

### Phase 1 — MVP (review Mi8 반영, 4분할)

> **모든 phase 1a~1d 는 internal milestone (PR 단위)** — 단독 release 아님 (review 2차 Minor R3)

#### Phase 1a — Scaffold + Claude CLI echo (PR 1, internal) — ✅ 2026-05-06 완료
- [x] `./Ds2.LlmAgent.fsproj` 생성 — `Ds2.Editor` only ProjectReference (Ds2.Core 는 transitive)
- [x] `../../Apps/Promaker/Promaker.sln` 에 명시적 추가 (Core 폴더 그룹)
- [x] `../../Apps/Promaker/Promaker/Promaker.csproj` ProjectReference 추가
- [x] ~~`App.xaml` 의 `StartupUri` 제거 + 진입점 분리~~ — **결정 4 (c) HTTP 채택으로 자식 Promaker spawn 없음 → skip**
- [x] `ClaudeCliProvider` concrete (interface 없이) — `LlmEvent` DU 8종 + `--resume` FSM (`SessionStarted` 캡처) + stream-json 5종 패킷 parser + `Channel.CreateBounded<LlmEvent>(256)` backpressure + `IAsyncEnumerable<LlmEvent>` 반환. AssistantDelta 50ms aggregation throttle 은 phase 1d 로 미룸. Job Object attach 도 phase 1b 로 미룸 (현재 Promaker 비정상 종료 시 `claude` 자식 process orphan 가능)
- [x] Claude CLI 버전 핀 — `ClaudeCliVersion.ensureMinimum` (≥2.1.0) → C# 친화 record `Result { IsValid; Message; VersionString }`. ViewModel 에서 lazy `Task.Run` background 검증 (UI thread block 회피)
- [x] Audit log 인프라 — `Log.provider` (Ds2.LlmAgent.Provider) / `Log.rawStream` (Promaker.LlmAgent.RawStream). `ToolCall` logger 는 mutation tool 진입 시점 (phase 1b) 추가
- [x] 최소 chat panel — `Apps/Promaker/Promaker/Windows/LlmChatWindow.xaml` + `ViewModels/LlmChatViewModel.cs`. MainViewModel 에 `OpenLlmChatCommand` + 유틸 popup 메뉴 항목 1개. 별도 Window (dock 통합은 phase 1d).

#### Phase 1b-c — HTTP MCP transport 채널 (PR 2, 결정 4 (c) 채택) — ✅ 2026-05-06 완료
- [x] Promaker in-process Kestrel + `ModelContextProtocol.AspNetCore` 1.2.0 HTTP transport — `Apps/Promaker/Promaker/LlmAgent/McpHostService.cs`
- [x] loopback-only bind (`127.0.0.1`) + OS-assigned ephemeral port (`ListenLocalhost(0)`)
- [x] `.mcp-config` JSON 작성 (`McpConfigWriter`) — `%TEMP%/Promaker/mcp-<sessionId>-<guid>.json`. `transport=http + url + headers["X-Promaker-Nonce"]`. ACL 강화는 phase 1d
- [x] `IUiDispatcher` 추상 주입 — F# DLL `UiDispatcher.fs` interface, Promaker 측 `WpfDispatcherAdapter.cs` 어댑터 (Background priority). Tool handler 진입 marshalling 은 phase 1c 진입 시 사용
- [x] `ImportPlanBuilder` (turn 단위 mutation 누적 buffer) — phase 1c 에서 mutation tool handler 가 `Add(ImportPlanOperation)` 호출
- [x] Tool registry 공통 invoker 골격 — Phase 1b-c 에선 dummy `PingTool` (`[McpServerToolType]` attribute 등록) 1개만. 실제 invoker (schema validation / sanitize / dispatcher / audit / quota 7개 책임) 는 phase 1c 진입 시 mutation tool 1번째 (`add_system`) 부터 채움

#### Phase 1b-b — stdio MCP + 자기자신 재진입 (PR 2, 결정 4 (b) 채택 시)
> 결정 4 가 사전 실증 3 결과로 (b) 확정 시 진행. 1b-c 와 sibling — 둘 중 하나만.

- [ ] `App` 진입점 argv `--mcp-server --parent-pid N --session GUID --nonce <secret>` 분기 (Phase 1a 의 진입점 분리 후속)
- [ ] `.mcp-config` JSON 동적 생성 + ACL 헬퍼 (결정 5.3)
- [ ] Promaker 시작 시 stale `mcp-*.json` sweep (결정 5.4 — self-race 방지 조건)
- [ ] Named pipe SECURITY_DESCRIPTOR + handshake nonce + Process.SessionId 격리 (결정 5.1, 5.2)
- [ ] `WaitForConnectionAsync` FSM (결정 5.5)
- [ ] 자식 watchdog (`WaitForSingleObject` blocking thread + pipe broken self-exit, 결정 5.6 — review 2차 R2 M4 정정)
- [ ] **`IUiDispatcher` 추상 주입** (1b-c 동일)
- [ ] **Tool registry 공통 invoker** (1b-c 동일)

#### Tool registry 공통 invoker 사양 (review 2차 R3 M4 + R5 prompt injection / consent / audit 통합)
1c/1d 진입 전 1b 단계에서 골격 작성 — phase 1 의 cross-cutting 책임 흡수:
```fsharp
type ToolDef<'TArgs,'TResult> = {
    Name: string
    Schema: JsonSchema           // pattern / maxLength / required
    Sanitize: 'TArgs -> Result<'TArgs, string>
    Handle: 'TArgs * IUiDispatcher * ImportPlanBuilder -> Async<'TResult>
}
```
- 공통 invoker 책임 (Handler 본문은 정말 한 줄):
  1. JSON Schema validation (Schema enforce — pattern/maxLength/required)
  2. 인자 sanitize (max length / charset whitelist / GUID 포맷 / null byte / unicode bomb 방어 — review 2차 R5 prompt injection)
  3. **dispatcher.InvokeAsync(Background)** 로 marshalling (결정 8)
  4. handler 호출 (mutation = `ImportPlanBuilder` 누적 / read = `Queries` 호출)
  5. 결과 직렬화 + 에러 → `VALIDATION_ERROR` 변환
  6. **audit log** (review 2차 R5 M4): `ToolCall` logger 에 tool name / argv hash / latency / 결과 size / error 기록
  7. **turn 당 mutation tool quota** (review 2차 R5 prompt injection 방어): e.g. 50회 초과 시 `QUOTA_EXCEEDED` — runaway loop / injection 회피
- **Data egress consent** (review 2차 R5 M3): 첫 chat 진입 시 opt-in 다이얼로그 — "이 모델 정보가 외부 LLM 으로 전송됩니다" + `%APPDATA%/Promaker/llm-config.json` consent flag + timestamp 저장. consent 없으면 read tool 차단.

#### Phase 1c — 최소 system prompt + `add_system` end-to-end (PR 3) — ✅ 2026-05-06 완료
- [x] 최소 system prompt — `Promaker.LlmAgent.SystemPromptText.Phase1c` 상수, `ClaudeCliOptions.SystemPrompt` 로 `--append-system-prompt` 인자 적용
- [x] Tool handler 첫 mutation: `add_system(name, isActive?)` — `Promaker.LlmAgent.Tools.ModelTools.AddSystem`, F# `ToolOperations.queueAddSystem` 호출 (DsSystem internal ctor → `Ds2.LlmAgent` 에 InternalsVisibleTo 추가). 첫 번째 project 자동 부착 (phase 1c 단순화)
- [x] Turn end `ApplyImportPlan` — `LlmChatViewModel.ApplyTurnPlanAsync` 가 dispatcher 안에서 `DsStoreImportPlanExtensions.ApplyImportPlan(_store, "LLM: <50자>", plan)` 1회 호출 → 1 undo step
- [x] Read tool 1개: `list_systems()` — `ModelTools.ListSystems` → `ToolOperations.listSystems` → 모든 project 의 active+passive 시스템
- [x] `LlmTurnContext` + `LlmTurnContextProvider` (turn-scoped, McpHostService DI singleton). Tool method 가 `[FromKeyedServices(null)]` 로 주입받음. mutation quota 50회 (`IncrementMutationCount`)
- [x] Audit log — `Promaker.LlmAgent.ToolCall` logger 가 tool name / 결과 size / latency / error 기록
- [x] **end-to-end 검증** (사용자 검증 완료): LLM 이 add_system 호출 → turn end → ApplyImportPlan → DsStore 변경 → EditorEvent → MainViewModel rebuild → 다음 turn 에서 list_systems 로 결과 확인 / Undo 1회로 turn 전체 롤백 모두 정상

#### Phase 1d — Tool 풀세트 + UI 완성 (PR 4)

> **하위 단위로 분할 진행** (단일 PR 안의 internal sub-milestone): 1d-1 Mutation 풀세트 → 1d-2 Read tool composite + system prompt 보강 → 1d-3 validate_model → 1d-4 UX (dock / throttle / consent / strict-mcp-config) → 1d-5 lifecycle 보안 (Job Object / ACL / sweep) → 1d-6 golden scenario.

- [x] **1d-1 — Mutation tool 풀세트** (2026-05-06 빌드 통과 + 사용자 e2e 검증 통과): `add_flow` / `add_work` / `add_call` / `add_arrow` / `add_api_def` 모두 `ImportPlanOperation` 누적 + turn end ApplyImportPlan. `ToolOperations` 의 plan+store 합산 lookup 으로 같은 turn 안 ID chaining 지원 (e.g. add_flow → add_work). `ImportPlanBuilder.Operations` seq 노출. `ModelTools` 가 sanitize/Guid parse/dispatcher/audit/quota 통합 헬퍼 (`Sanitize`/`ParseGuid`/`RunMutation`) 로 7개 책임 inline 압축. ID 표기 full GUID 통일 (list_systems / 모든 mutation 응답).
- [ ] **Read tool — N+1 token 폭증 방지** (review 2차 R1·R2·R4 Critical):
  - `list_systems()` → System.Id + Name + 통계 (Flow 수 / Work 수). 1KB 이내 메타
  - `describe_system(id, expand?: 'none'|'shallow'|'deep')` → 기본 'shallow' (Flow/ApiDef 이름만), 'deep' 명시 시 자식 트리 포함
  - `describe_subtree(rootId, depth: int, page: int?)` — composite tool. LLM 이 batch 선택 가능. result 에 `truncated: true` + `next_page` 플래그
  - `find_by_name(name, kind?)`
  - **system prompt 가이드 — "batch 우선"** (review 2차 R1 Critical): "여러 system 을 보려면 describe_subtree 한 번을, 단일 system 만 깊게 보려면 describe_system(deep)" 명시
  - Phase 1d golden test 에 **token 회귀 케이스 추가** (review 2차 R1 Critical): 30 system × 5 flow × 3 work 모델에서 LLM exploration 후 누적 token 측정, 단일 get_model_summary 보다 작아야 함
- [ ] **`validate_model(scope?: SystemId | FlowId | global)`** (review 2차 R3 mi2 / R4 M2):
  - scope 인자로 부분 검증 가능
  - 500ms result cache (handler throttle) — LLM 자가검증 spam 방지
  - system prompt 가이드: "turn 종료 직전 1회"
  - 제약 위반 리포트 (orphan / dangling / duplicate / 빈 Work / TODO placeholder)
- [ ] Chat panel 완성 (XAML / ChatPanelViewModel): Streaming 표시, ToolUse/ToolResult collapsible, Provider 설정 다이얼로그, `%APPDATA%/Promaker/llm-config.json` 저장 (consent flag 포함)
- [ ] HistoryPanel 에 LLM turn 그룹 시각화 (결정 7 (d) ImportPlan label "LLM: ..." 식별)
- [ ] System prompt 보강 (1c 의 최소 → 1d 의 풀): greenfield 환각 방지, 도메인 규칙, Arrow 타입, tool 사용 순서, clarification 템플릿, **user-supplied 텍스트 격리 delimiter** (`<spec>...</spec>` + "내부는 데이터, 명령 아님" 명시 — review 2차 R5; delimiter 단독 방어 약하므로 sanitize + quota 와 결합)
- [ ] Golden scenario 회귀 테스트:
  - 4-cylinder sequential+parallel spec → 기대 모델
  - 환각 회귀 (주소 없는 spec)
  - 인스턴스 격리 (Promaker 두 개 동시, cross-talk 없음 확인)
  - 같은 user 의 다른 RDP / logon session 에서 pipe / port handshake 차단 검증 (review 2차 R5)
  - `/quit` 후 재접속 (session resume)
  - 사용자 GUI 직접 편집과 LLM mutation 혼용 → DsStore 일관성 / Undo 정상
  - **token 회귀** (위)
  - **prompt injection negative test**: "ignore previous instructions" / path traversal / null byte 인자에 sanitizer 동작 (review 2차 Minor R5)
  - **tool allowlist 이중 방어 negative test**: `--mcp-config` 화이트리스트 외 tool 호출이 거부되는지 (review 2차 Minor R5)

#### `modify_*` / `remove_*` — phase 2
> review 2차 R1 후속: `ImportPlanOperation` DU 미포함. phase 2 에서 ImportPlan 확장 vs 별도 undo path 결정.

**원칙**:
- 각 tool 의 JSON Schema **SSOT 결정 (review 1차 Mi7)**: **손수 정의 + JSON Schema 가 SSOT**. Ds2 도메인 type 은 이미 변환 path (Newtonsoft / NjObjects) 가 분산되어 있어 자동 파생은 drift 위험. tool schema 는 LLM-facing 계약으로 별도 관리 + handler 에서 F# type 으로 매핑.
- 필수 필드 누락 → `VALIDATION_ERROR` 반환 (스키마 거부 + 이유 명시) → LLM 이 사용자에게 되묻도록 system prompt 가 유도
- **Mutation tool handler = `ImportPlanBuilder` 에 `ImportPlanOperation` 누적하는 한 줄 함수** (review 1차 M3/M8 + 2차 R1). turn end 에 단일 `store.ApplyImportPlan(label, plan)` 호출
- **Read tool handler = `Queries` 호출 + lightweight snapshot + projection** (결정 8 thread 모델)
- 공통 invoker 가 schema validation / sanitize / dispatcher / audit / quota / consent 흡수 (위 "Tool registry 공통 invoker 사양" 절)

### Phase 2
- [ ] Codex CLI provider (사전 실증 4 결과에 따라). 인터페이스가 phase 1 의 ClaudeCli concrete 1종으로만 검증되었으므로 **Codex 추가 시 인터페이스 재설계 가능성 인정** (review M5)
- [ ] OpenAI API provider (Codex CLI 막힌 경우 보험)
- [ ] Anthropic API provider (구독 한도 초과 사용자용)
- [ ] Ollama provider (local, OpenAI 호환 endpoint)
- [ ] Provider fallback 정책 (지정 provider 실패 시 다른 provider 자동 전환 vs 에러 종료)
- [ ] (결정 7 (c) 채택했고 UX 가 부족하면) `withTransaction` nested 허용 (a) 또는 `BeginTurnBatch` (b) 도입
- [ ] (결정 7 (a)/(b) 도입 시) deferred-apply 모델 — turn 안 mutation 은 in-memory plan, turn end 에 batch apply (review M4)

### Phase 3
- [ ] LLM 제안 적용 전 미리보기 / confirm 모드 (opt-in)
- [ ] 제안 diff 시각화 (캔버스에 ghost 노드 등)
- [ ] LLM 추천 액션 chip / quick-apply UX

---

## 사전 실증 항목

> 단일 source: 위의 **"Phase 0 — 사전 실증 + surface inventory"** 섹션 (line ~194) 본문 참조. 중복 표 제거 (drift 방지).

---

## 검증된 사실 (직접 source 검증 완료)

> 새 세션이 결정의 근거를 빠르게 확인할 수 있도록 한 곳에 모아둔 매핑. 모든 line 번호는 검증 시점 기준.

| 결정 | 근거 source | line | 사실 |
|---|---|---|---|
| 결정 7 (d) | `Solutions/Core/Ds2.Core/Store/ImportPlan.fs` | 5-15 | `ImportPlanOperation` DU 9종 (`AddSystem`/`AddFlow`/`AddWork`/`AddCall`/`AddApiDef`/`AddApiCall`/`AddArrowWork`/`AddArrowCall`/`LinkSystemToProject`) |
| 결정 7 (d) | `Solutions/Core/Ds2.Editor/Editor/ImportPlanApply.fs` | 34-38 | `applyWithUndo` = 단일 `store.WithTransaction(label, ...)` + `EmitRefreshAndHistory()` 1회 emit. `[<Extension>] ApplyImportPlan` 으로 호출 측 노출 |
| 결정 7 (d) 의 부정 근거 | `Solutions/Core/Ds2.Editor/Editor/Authoring.fs` | 28-29 | `if editorState.CurrentRecords.IsSome then invalidOp "Nested transactions are not supported"` — outer transaction 가정은 즉시 invalidOp |
| 결정 7 (d) 의 부정 근거 | `Solutions/Core/Ds2.Editor/Store/Nodes/Nodes.fs` | 24-34 | `[<Extension>] static member AddSystem(...)` 이 자체 `store.WithTransaction(...)` 호출 — 외부에서 outer 감싸면 nested |
| 결정 8 (Thread) | `Solutions/Core/Ds2.Editor/Editor/Authoring.fs` | StoreEditorState | `mutable CurrentRecords / SuppressEvents / CurrentAffectedIds` lock 없음 → background thread 직접 접근 위험 |
| 결정 8 (`InvokeAsync` Background) | `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.cs` | 652-679 | 기존 `RequestRebuildAll` 이 `BeginInvoke` + `DispatcherPriority.Background` 패턴. coalescing 의존. sync `Invoke` 는 이 패턴 깨짐 |
| 주의 13 (WPF WinExe) | `Apps/Promaker/Promaker/App.xaml` | 4-5 | `StartupUri="MainWindow.xaml"` + `ShutdownMode="OnMainWindowClose"` — argv 분기 없이 자식도 MainWindow 자동 생성 |
| 결정 2 (mutation API 위치) | `Solutions/Core/Ds2.Editor/Store/Nodes/Nodes.fs` | 24 | `AddSystem` 은 `[<Extension>] static member` on `DsStoreNodesExtensions` (Ds2.Editor) — DsStore 자체는 read-only dictionary |

**미검증 (사전 실증 위임)**:
- 결정 4 (transport): HTTP MCP transport 동작 가능 여부 — **사전 실증 3** 으로 위임
- Phase 1a (`-p` + `--resume` + `--mcp-config` + `stream-json` 양립성) — **사전 실증 2a** 로 위임
- Phase 1a (session_id 실제 packet format `subtype:init` 분기) — **사전 실증 7** 로 위임
- 결정 5 (손자 Job Object 상속) — **사전 실증 6** 으로 위임 ((b) 채택 시)

---

## 새 세션 진입 절차

새 Claude Code 세션이 본 작업을 이어받을 때:

1. **본 문서 전체 1회 통독** — 결정 1~8 + Phase 0~1d + 주의 사항 17개. 약 500 lines
2. **검증된 사실 표** (위 섹션) 의 source line 1~2개를 직접 열어 코드 변경 없는지 sanity check (특히 `ImportPlanApply.fs:34-38`, `Authoring.fs:28-29`, `App.xaml:4-5` 우선)
3. **사용자에게 진입 단계 확인**:
   - Phase 0 (사전 실증) 미진행 상태인지 → 가장 먼저 spike 시작
   - Phase 0 일부 완료 → 어느 결정이 잠정→확정 전환되었는지
   - Phase 1a/1b/1c/1d 어느 PR 진행 중인지
4. **Phase 0 미완료 시 권장 진입**: 사전 실증 3 (HTTP MCP transport, ~30분) 먼저 — 결정 4 의 (c)/(b) 분기 결정. 그 후 1, 2a, 5, 7 병렬 spike.
5. **Phase 1a 진입 시 필수 사전 차단 조건**:
   - 사전 실증 2a (CLI 인자 양립성) 통과
   - 사전 실증 7 (session_id format) 측정 결과 반영
   - 사전 실증 5 (Editor surface) 결과로 ProjectReference 확정
6. **잠정 결정 1개 (결정 4) 가 확정 전환 시** — 본 문서 결정 4 / 결정 5 / Phase 1b 섹션 갱신 + 진행 상태 의 "잠정→확정" 표기 이동

---

## 관련 파일 / 경로

**기준 디렉토리**: 이 문서 위치 = `<repo>/Solutions/Core/Ds2.LlmAgent/` (ds2 `feature-greenfield-modeling` branch). 아래 경로는 모두 **이 문서 기준 상대경로**.

### 신규 (모두 ds2 `feature-greenfield-modeling` branch)
- **이 문서**: `./todo-promaker-llm-agent.md`
- (예정) `./Ds2.LlmAgent.fsproj`
- (예정) Chat panel: **신설 `../../Apps/Promaker/Promaker/Controls/Llm/`** 잠정 결정 (Phase 1d 진입 시 최종 확인). 근거: 기존 `Controls/Shell/` 은 explorer/history/toolbar 등 shell 인프라 용도이므로 LLM agent panel 은 별도 디렉토리가 응집도 ↑

### 참고 (모두 ds2 `feature-greenfield-modeling` branch — 본 작업 분기)
- Promaker 진입점: `../../Apps/Promaker/Promaker/App.xaml.cs`
- Promaker MainViewModel: `../../Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.cs`
- Promaker csproj: `../../Apps/Promaker/Promaker/Promaker.csproj`
- Promaker 솔루션: `../../Apps/Promaker/Promaker.sln`
- Ds2 도메인 core (F#): `../Ds2.Core/` / `../Ds2.Editor/`

### Cross-repo / cross-branch 참고 (절대경로 — 다른 repo 또는 다른 branch)
- Ds2 entity 개념 문서: `/f/Git/kwak/kwak/DsConcepts/ds.md`, `ds-entities.md` (별도 repo, CLAUDE.local.md 인용)
- Ds2 JSON 포맷 문서: `/f/Git/ds2/main/Solutions/Convert/Ds2.JsonFormatter/json-format.md` (포맷은 main 의 안정 문서 참조)

### Ev2 측 학습 자산 (별도 repo `/f/Git/ev2/master`, 직접 통합 X, 패턴만 참조)
- todo-greenfield-model-builder: `/f/Git/ev2/master/solutions/Ev2.Backend/todos/todo-greenfield-model-builder.md`
- Builder Claude CLI provider (1-shot): `/f/Git/ev2/master/solutions/Ev2.Backend/src/Ev2.Oracle/Builder/pipeline/llm_providers/claude_cli.py`
- GreenfieldModelerPoC (Python): `/f/Git/ev2/master/solutions/Ev2.Backend/src/Ev2.Oracle/GreenfieldModelerPoC/`
- Ev2.Oracle Backend (FastAPI tool registry): `/f/Git/ev2/master/solutions/Ev2.Backend/src/Ev2.Oracle/Backend/`

---

## 주의 사항

1. **모델 SSOT 는 DsStore 1개**. LLM mutation / 사용자 GUI 편집 / 파일 import 모두 DsStore 를 거친다. EditorEvent 로 양방향 sync 보장.
2. **세션 SSOT 는 Provider 의 SessionHandle**. CLI 는 `--resume` ID, API 는 message array. 둘이 분리되어 있으므로 사용자 GUI 변경을 LLM session 에 단방향 주입하는 다리가 필요 (model_changed system message 또는 다음 turn 전 context refresh).
3. **Promaker 는 다중 인스턴스 동시 실행 가능**. 모든 IPC 식별자(named pipe / 임시 파일) 는 SessionGUID 로 unique 화 (PID 는 log 메타). cross-talk 방지가 보안/안전 양면에서 필수.
4. **Job Object cascade kill 필수 — 단 손자 상속 미보장** (review M1). 자식 Promaker 자체 watchdog (parent_handle 폴링 + pipe broken self-exit) 이중 방어. 부모 Promaker 가 비정상 종료해도 Claude CLI + 자식 Promaker (MCP server) 가 좀비로 남지 않게.
5. **`--tools ""` + `--strict-mcp-config` 충돌 가능성** — Builder 의 1-shot provider 는 도구 비활성 + 빈 MCP 였음. 본 작업은 도구 활성 + MCP server 연결이 필요하므로 인자 조합이 정반대. 실측 단계에서 충돌/우회 확인 필수. **Claude CLI 최소 버전 핀** 도 첫 적용 시 명시 (review Mi4).
6. **`.mcp-config` 임시 파일 ACL** = Owner: current user, DACL: user FullControl only, `SetAccessRuleProtection(true,false)` 로 inheritance 차단. 시작 시 stale `mcp-*.json` sweep (review M10 / Mi1).
7. **Tool 범위 제한**: model mutation 도구로만 한정. `Bash` / `Read` / `Write` 같은 filesystem 도구는 전면 금지. Claude CLI 의 `--tools ""` allowlist + `--mcp-config` 화이트리스트 이중 방어. (Ev2.Hub 의 headless XG5000 사고 같은 외부 프로세스 spawn 방지)
8. **F# / C# 분리 근거** = `CommunityToolkit.Mvvm [ObservableProperty]` source generator 가 C#-only 라 Promaker 의 기존 MVVM 패턴 깨지 않으려는 절충 (review M9). F# 선호는 보너스. `Ds2.LlmAgent` DLL 은 F# 단독, Promaker 안의 ChatPanel ViewModel 만 C#.
9. **작업 branch 는 `feature-greenfield-modeling`** (Promaker 가 위치한 branch). `Ds2.LlmAgent` DLL / todo 문서 모두 이 branch 의 `Solutions/Core/Ds2.LlmAgent/` 에 두고 작업. main 흡수 시점은 사용자 결정.
10. **Codex CLI 가 막혀도 진행**. provider 추상화가 흡수하나, phase 1 은 ClaudeCli concrete 1종이므로 Codex 추가 시 인터페이스 재설계 인정 (review M5).
11. **Thread 모델 = `IUiDispatcher.InvokeAsync<'T>` Background priority** (review 1차 C3 + 2차 R4). `Ds2.Editor` 의 mutable state 가 lock 없이 사용되므로 background thread 직접 mutation 시 Undo 스택 corruption / `InvalidOperationException` 위험. **sync `Invoke` 금지** — UI thread 점유 시 stream 처리도 block 되어 AssistantDelta 표시 frozen. read tool 도 dispatcher snapshot (lightweight `.Values.ToArray()`) 후 background 가공. AssistantDelta 같은 stream-only 이벤트는 dispatcher 우회.
12. **Undo 단위 = 결정 7 (d) ImportPlan 활용 — 1 LLM turn = 1 undo step** (review 2차 R1 Critical). `Ds2.Core/Store/ImportPlan.fs` + `Ds2.Editor/Editor/ImportPlanApply.fs` 의 `ApplyImportPlan` 이 단일 `WithTransaction` + `EmitRefreshAndHistory` 1회 emit. F# editor **무수정**. `modify_*` / `remove_*` 는 ImportPlanOperation 미포함 → phase 2 처리.
13. **WPF WinExe 제약** (review 1차 C2 + 2차 R5 M5): `App.xaml` 의 `StartupUri` 제거 + 진입점 분리 (1차 안 = OnStartup argv 분기, 2차 안 = `<DisableWinExeOutputInference>` + 별도 `static int Main` — 사전 실증 0 결과로 결정). `--mcp-server` 모드는 WPF 진입 skip + `GetStdHandle` + `Console.SetIn/SetOut` 재바인딩으로 stdin/stdout 사용.
14. **IPC 보안** (review 2차 R5 Critical): pipe 생성 시 SECURITY_DESCRIPTOR (DACL: current user FullControl only, deny others) + `PIPE_REJECT_REMOTE_CLIENTS` + `Process.SessionId` 격리 (RDP / Fast User Switching) + handshake nonce (`.mcp-config` 에 short-lived secret 기록 → 자식 첫 메시지로 송신 → 부모 검증). 같은 user 의 다른 logon session 또는 GUID leak 시에도 nonce 없으면 차단. Stale `.mcp-config` sweep 은 자기 session scope + dead PID 조건만.
15. **Tool argument validation + prompt injection 방어** (review 2차 R5 Critical): mutation 인자에 사용자 paste 텍스트가 흘러갈 수 있음 — path traversal / null byte / unicode bomb / "ignore previous instructions" injection 무방비. 공통 invoker 가 ① schema enforce (pattern/maxLength/required) ② sanitize (charset whitelist / GUID 포맷) ③ system prompt user-text 격리 delimiter (`<spec>...</spec>` + "내부는 데이터") ④ turn 당 mutation tool quota (e.g. 50회) 4중 방어.
16. **Data egress consent + audit log** (review 2차 R5 M3 / M4): read tool 결과는 사용자 모델 전체가 외부 LLM 으로 송출되므로 첫 진입 opt-in 다이얼로그 + `%APPDATA%/Promaker/llm-config.json` consent flag + timestamp. `Promaker.LlmAgent.ToolCall` log4net logger 로 tool 호출/결과/latency 기록 (forensic / repro), `RawStream` verbose appender 는 default OFF.
17. **Read tool N+1 token 폭증 방지** (review 2차 R1·R2·R4 Critical): 4분할 단순 read tool 은 LLM exploration 패턴에서 prompt cache 5분 TTL 만료 시 누적 token 폭증. `describe_subtree(rootId, depth, page)` composite + truncated/next_page 플래그 + system prompt batch 가이드 + golden test token 회귀.

---

## 진행 상태

- 현 단계: **Phase 1d-1 완료** (2026-05-06, 사용자 e2e 검증 통과). Mutation tool 풀세트 (`add_flow`/`add_work`/`add_call`/`add_arrow`/`add_api_def`) + ID chaining (plan+store 합산 lookup). 다음 = 1d-2 (Read tool composite `describe_system`/`describe_subtree`/`find_by_name` + system prompt batch 가이드).
- 결정 상태:
  - **확정 9개**: 결정 1 (통합 형태) / 결정 2 (언어 + tool registry, ILlmProvider 인터페이스 phase 2 로 미룸) / 결정 3 (Provider 우선순위) / **결정 4 ((c) HTTP MCP transport — 2026-05-06 사전 실증 3 통과)** / 결정 5 (인스턴스 격리 + IPC 보안, (c) 채택으로 5.0 적용) / 결정 7 ((d) ImportPlan 활용) / 결정 8 (Thread 모델, InvokeAsync Background priority) / 결정 9 (비동기 표현 — provider stream `IAsyncEnumerable`, EditorEvent `IObservable`) / 결정 6 흡수 완료
  - **잠정 0개** — 모든 결정 확정

### Phase 0 사전 실증 결과 (2026-05-06 진행)

| 실증 # | 상태 | 결과 요약 |
|---|---|---|
| 실증 1 — MCP SDK 패키지 | ✅ | `ModelContextProtocol` / `.Core` / `.AspNetCore` 모두 1.2.0 정식 GA. F# 호출은 빌드 단계 검증 (manual DI 등록 우회 가능) |
| 실증 2a — CLI 4종 인자 양립성 | ✅ | `claude -p <msg> --resume <sid> --mcp-config <path> --output-format stream-json --verbose` 동시 동작 확인. session resume 정상 (cache_read 활용), 응답 ~3.7s |
| 실증 3 — HTTP MCP transport | ✅ | ASP.NET Core 10 + `ModelContextProtocol.AspNetCore` 1.2.0 + `[McpServerTool]` attribute 로 toy server (`PingTool`) 띄움 → `.mcp-config` 의 `{"type":"http","url":"http://127.0.0.1:5777/"}` 항목으로 Claude CLI 가 connect 성공 → `mcp__spike__ping` 호출하여 응답 정상 수신. 결정 4 → (c) 확정 |
| 실증 5 — Editor surface inventory | ✅ | `ImportPlanOperation` 9종 DU 모두 phase 1 mutation tool 세트 (add_system/flow/work/call/arrow/api_def) 커버. `ImportPlanApply.applyWithUndo` = 단일 `WithTransaction` + `EmitRefreshAndHistory` 1회 emit. `[<Extension>] ApplyImportPlan` 노출. **`Ds2.LlmAgent` ProjectReference = `Ds2.Editor` only 충분** (Ds2.Core 는 transitive, `Queries` 는 `[<AutoOpen>] module Queries` in `Ds2.Core.Store`). `modify_*`/`remove_*` 는 DU 미포함 — phase 2 처리 |
| 실증 7 — session_id 패킷 형식 | ✅ | 첫 패킷 = `{"type":"system","subtype":"init","cwd":"...","session_id":"...","tools":[...],"mcp_servers":[...],"model":"...",...}`. 종료 패킷 = `{"type":"result","subtype":"success",...}`. 추가 발견: `mcp_servers` 가 init 패킷에 health check 결과 포함 (e.g. `{"name":"spike","status":"connected"}`) — 별도 health-check 호출 불필요 |

**Skip 처리 (결정 4 (c) 확정으로 불필요)**:
- 실증 0 (WPF WinExe stdio 진입점 분리) — (c) 채택 시 자식 Promaker spawn 없으므로 불필요. App.xaml StartupUri 변경도 skip 가능
- 실증 6 (손자 Job Object 상속) — (c) 채택 시 자식 프로세스 자체 없음

**남은 spike (phase 2 사전)**:
- 실증 2b/2c/2d (per-turn spawn vs long-running, latency, lazy spawn 시점) — (c) 채택으로 자식 spawn 자체가 in-process Kestrel 로 대체. CLI 측 spawn 시점은 phase 1a 단순 측정으로 충분
- 실증 4 (Codex CLI multi-turn / stream / MCP) — phase 2 진입 시점

### 다음 작업 진입 권장 순서

1. ✅ Phase 0 사전 실증 완료 (2026-05-06)
2. ✅ 결정 4 (c) 확정 (2026-05-06)
3. ✅ **Phase 1a** — scaffold + ClaudeCliProvider concrete + 최소 chat panel (PR 1, internal) — 2026-05-06 완료
4. ✅ **Phase 1b-c** (HTTP) — Promaker in-process Kestrel + `ModelContextProtocol.AspNetCore` 1.2.0 + loopback bind + ephemeral port + handshake nonce + `IUiDispatcher` 추상 + `ImportPlanBuilder` + dummy `PingTool` (PR 2) — 2026-05-06 완료
5. ✅ **Phase 1c** — 최소 system prompt + `add_system` mutation tool + `list_systems` read tool + Turn end `ApplyImportPlan` + LlmTurnContext + audit log (PR 3) — 2026-05-06 완료. 산출물 `done-promaker-llm-agent.md`. end-to-end 검증 통과 (add_system → ApplyImportPlan → tree rebuild → Undo 롤백 모두 정상)
6. ✅ **Phase 1d-1** — Mutation tool 풀세트 (`add_flow`/`add_work`/`add_call`/`add_arrow`/`add_api_def`) — 2026-05-06 완료 (사용자 e2e 검증 통과). ID chaining (plan+store 합산 lookup) 으로 같은 turn 안 add_flow → add_work 가능
7. **Phase 1d-2** Read tool composite (`describe_system` expand + `describe_subtree` depth/page + `find_by_name`) + system prompt batch 가이드 + token 회귀 golden test
8. **Phase 1d-3** `validate_model(scope?)` + 500ms result cache
9. **Phase 1d-4** UX (ChatPanel dock 통합 / AssistantDelta 50ms aggregation throttle / HistoryPanel LLM turn 시각화 / data egress consent dialog / `--strict-mcp-config` + `--allowed-tools`)
10. **Phase 1d-5** Lifecycle 보안 (Job Object attach / `.mcp-config` ACL 강화 / stale sweep — 결정 5.0 / 5.4)
11. **Phase 1d-6** Golden scenario 회귀 테스트 (4-cylinder spec / 환각 / 인스턴스 격리 / RDP / token 회귀 / prompt injection / tool allowlist)

---

## Review 반영 이력

### 2차 review (--review 2, 5 reviewers — Generalist / 정확성 / 설계구조 / 성능 / 보안유지보수)

**Critical (검증 완료, 즉시 결정 변경)**:
- **R1 (검증 완료) ImportPlan path 누락 → 결정 7 격상**: 1차 review C1 의 (a)/(b)/(c) 비교가 더 단순한 `ImportPlanApply.applyWithUndo` (단일 `WithTransaction` + `EmitRefreshAndHistory` 1회) 를 놓침. 결정 7 → **(d) ImportPlan 활용** 확정. F# editor 무수정. 사전 실증 5 → surface inventory spike 로 변경. (`ImportPlan.fs` + `ImportPlanApply.fs:34-38` 직접 검증 완료)
- **R1·R2·R4 (consensus 3/5) Read tool 4분할 N+1 token 폭증** → `describe_subtree` composite + page/depth/truncated + system prompt batch 가이드 + golden token 회귀 (Phase 1d + 주의 17)
- **R2 (검증 완료) `claude -p` + `--resume` + `--mcp-config` + `stream-json` 양립성 미검증** → 사전 실증 2a 신설, Phase 1a 진입 차단 조건. fallback = `--input-format stream-json` stdin 연속 모드
- **R2 (검증 완료) session_id 패킷 형식 정정** → `subtype:init` 분기. 사전 실증 7 신설. parser 가 Builder PoC 의 `system` 무시 패턴 그대로 재사용 X
- **R2 (검증 완료) 결정 5 FSM ↔ "lazy spawn" 부정합** → 결정 5.5 재기술. 부모는 chat 세션 시작 즉시 listen, Claude CLI 의 자식 spawn 시점은 사전 실증 2d 측정
- **R5 (consensus 2/5) 보안 — pipe SD / RDP 격리 / handshake nonce / data egress consent / prompt injection / audit log** → 결정 5.1~5.6 신설 + 주의 14~16 + Tool registry 공통 invoker 사양에 흡수
- **R4 (consensus 2/5) `IUiDispatcher.Invoke` sync block** → `InvokeAsync<'T>` Background priority + AssistantDelta 우회 (결정 8 + 주의 11)

**Major (전부 반영)**:
- R1 M5 / R3 M5 Ds2.Editor surface inventory → 사전 실증 5 재기술
- R4 M1 EditorEvent coalescing 의존 → 결정 8 명문화 (결정 7 (d) 로 자연 해소)
- R3 mi2 / R4 M2 validate_model scope/throttle → Phase 1d
- R3 C1 결정 4 fork 분량 차이 → Phase 1b-c / 1b-b sibling 분리
- R3 M5 Ds2.Editor only ProjectReference → Phase 1a
- R3 M2 system prompt 배치 → Phase 1c 에 최소 prompt 선행
- R1 M3 / R3 M1 ILlmProvider 인터페이스 dead abstraction → phase 1 = ClaudeCli concrete only, 인터페이스 phase 2 로 미룸
- R3 M4 Tool registry "한 줄" 추상화 → 공통 invoker (`ToolDef<'TArgs,'TResult>`) 사양 신설, phase 1b 에 도입
- R4 C1 spawn latency 측정 → 사전 실증 2c
- R4 M4 read snapshot copy cost → 결정 8 lightweight snapshot 명시
- R4 M3 stream backpressure → `Channel.CreateBounded` + AssistantDelta merge throttle (Phase 1a)
- R2 M3 stale sweep self-race → 결정 5.4 sweep 조건 명시 (자기 session + dead PID + mtime > 5분)
- R2 M4 watchdog "polling" → blocking thread + `WaitForSingleObject` 정정 (결정 5.6)
- R1 M4 read tool depth/cycle → expand/depth 인자 (Phase 1d)
- R5 M2 Claude CLI 버전 핀 enforce → 기동 시 SemVer 체크 fail-fast (Phase 1a + 주의 5)
- R5 M3 data egress consent → 첫 진입 opt-in (주의 16)
- R5 M4 audit log → log4net `Promaker.LlmAgent.ToolCall` (Phase 1a + 주의 16)
- R5 M5 진입점 분리 trade-off → 사전 실증 0 sub-task

**Minor 모두 반영**:
- 사전 실증 시간 예산 정합 / Phase 1a~1d internal milestone 표기 / HTTP transport schema 버전 핀 / Kestrel cold start 측정 / `_pendingRebuildActions` unbounded 검토 / %TEMP% redirect ACL 사전 검증 / tool allowlist negative test / EditorEvent dispatcher reentrancy 모두 todo 본문 또는 Phase 1d golden test 에 흡수

**반론 0건** — 5 reviewer 모두 사실 기반. R1 ImportPlan / R4 dispatcher InvokeAsync / R5 보안은 source 직접 검증 또는 `MainViewModel.cs:652-679` 패턴 검증 완료.

### 1차 review (--review 1, cross-validated 3 reviewers A/B/C)

**Critical (즉시 결정 변경 / 실증 필요)**:
- C1 (3/3) `withTransaction` nested 거부 ↔ "1 turn = 1 undo unit" 충돌 → **결정 7 잠정 강등 + 사전 실증 5 재기술** + Authoring.fs:28-29 / Nodes.fs:24-34 직접 검증 완료
- C2 (B + A 보강) WinExe `StartupUri` + Console.In/Out null 제약 → **결정 4 (b) 세부 보강 + 사전 실증 0 신설 + 주의 사항 13 신설** + App.xaml 직접 검증 완료
- C3 (2/3) DsStore thread-safety 부재 → **결정 8 (Thread 모델) 신설 + IUiDispatcher 도입 + 주의 사항 11**
- C4 (1/3 outlier, 검증 통과) (b) 가 사전 실증 3 보다 선행 채택 → **결정 4 잠정 강등 + 사전 실증 3 우선순위 최상위 격상**

**Major**:
- M1 (B) Job Object 손자 상속 미보장 → **결정 5 + 주의 사항 4 명문화 + 사전 실증 6 신설 + 자식 watchdog 이중 방어**
- M2 (A) Promaker.sln 별도 솔루션 명시 → Phase 1a 작업 항목에 명시적 추가 step
- M3 (2/3) `Ds2.Editor` extension 표현 정정 → 결정 2 의 ProjectReference 에 `Ds2.Editor` 명시 + tool handler = "extension 직접 호출"
- M4 (B + A 부분) sync race + stale read → **(c) 채택 시 자연 해소**, (a)/(b) 채택 시 deferred-apply 모델 phase 2 검토
- M5 (2/3) Provider 5종 over-eng → **phase 1 = ClaudeCli concrete 1종 only**, 인터페이스는 회의적 (재설계 가능 단서)
- M6 (2/3) `get_model_summary` 토큰 폭증 → **read tool 4분할** (`list_systems` / `describe_system` / `describe_flow` / `describe_work`)
- M7 (B) session_id race + pipe race → FSM (SessionEnd 후 Send 활성화, server WaitForConnection 선행, retry policy)
- M8 (C) 결정 6 ↔ 결정 2 중복 → **결정 6 → 결정 2 흡수 완료**
- M9 (C) 분리 근거 정합 → "MVVM source generator 비호환" 으로 재기술 (주의 사항 8)
- M10 (2/3) ACL 명세 → DACL 명시 + stale sweep (결정 5 + 주의 사항 6)

**Minor 모두 반영**:
- Mi1 ACL Owner/DACL/inheritance 명세화 (주의 사항 6)
- Mi2 Pipe 이름 PID 제거, GUID 만 (결정 5)
- Mi3 Codex CLI phase 1 작업에서 제거, spike 분리 (사전 실증 4)
- Mi4 Claude CLI 버전 핀 (주의 사항 5)
- Mi5/Mi6 `Event<EditorEvent>` / Dictionary enumeration race → dispatcher marshalling 으로 해소 (결정 8)
- Mi7 JSON Schema SSOT 한 줄 결정 → 손수 정의 + JSON Schema SSOT (Phase 1d 원칙 절)
- Mi8 Phase 1 4분할 (1a/1b/1c/1d) 적용

**반론 0건** — 3 reviewer 모두 사실 기반 견고, 직접 source 검증 (`Authoring.fs` / `App.xaml` / `Nodes.fs`) 으로 C1·C2·M3 확정.
