# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 본 디렉토리의 역할

`Solutions/Core/Ds2.LlmAgent/` = Promaker (WPF C# 데스크탑 앱) 에 **대화형 LLM agent** 를 통합하는 F# DLL 의 source root, 그리고 통합 작업 자체의 설계/진척 문서 holder.

- `doc/todo-promaker-llm-agent.md` — 남은 작업, 모든 결정 사항 (확정 9개), Phase 0/1a/1b-c/1c/1d/2 체크리스트, 검증된 사실 표
- `doc/done-promaker-llm-agent.md` — 완료된 spike / phase 의 발견 사항 + 작업 산출물 누적

> **두 문서 유지 정책**: `git commit` 시점에 다른 Claude Code 세션이 바로 이어받아 작업할 수 있는 수준으로 동기화되어야 한다. 진척 / 결정 변경 / 미적용 의도 / 다음 단계 권장 순서가 두 문서 안에서 일관된 상태여야 함. 작업 종료 직전 두 문서 갱신을 빠뜨리지 말 것.

## 새 세션 진입 절차

1. `doc/todo-promaker-llm-agent.md` 의 **"진행 상태"** + **"다음 작업 진입 권장 순서"** 섹션부터 읽기 (현재 phase 위치 파악)
2. `doc/done-promaker-llm-agent.md` 의 가장 최근 phase 절 (현재까지의 산출물 + 의도적 미적용 항목)
3. todo 의 **"검증된 사실 표"** 의 source line 1~2개를 직접 열어 sanity check (특히 `ImportPlanApply.fs:46-50`, `Authoring.fs:28-29`, `Nodes.fs:24-34`)
4. 사용자에게 다음 작업 단위 확인 후 진행

## Build / Run

```bash
# F# DLL 단독 빌드 (이 디렉토리에서)
dotnet build

# Promaker.sln 전체 빌드 (UI 통합 검증 시)
dotnet build ../../../Apps/Promaker/Promaker.sln

# Promaker 실행 (.NET 9 WPF WinExe)
dotnet run --project ../../../Apps/Promaker/Promaker
```

빌드 시 Promaker.exe 가 실행 중이면 DLL copy 단계에서 잠금 오류 발생 → 사용자에게 종료 요청 후 재시도.

LLM Chat 검증: Promaker → 상단 ribbon "기타" → "유틸" 토글 popup → "LLM Chat".

## 확정된 핵심 결정 (변경 시 todo 갱신 필수)

| # | 결정 | 핵심 |
|---|---|---|
| 1 | 통합 형태 | Promaker 내부 dock panel (별도 console exe 아님). Phase 1a 는 별도 Window, Phase 1d 에서 dock 통합 |
| 2 | 언어 | F# DLL `Ds2.LlmAgent` (비-UI) + C# WPF binding (`LlmChatViewModel`). C# 분리 사유 = `CommunityToolkit.Mvvm` source generator 가 C# only |
| 3 | Provider 우선순위 | Phase 1 = Claude CLI subscription 1st-class only. Phase 2 = Codex CLI + Anthropic API + OpenAI API + Ollama 통합 (`ILlmProvider` 추상화) |
| **4** | **Tool 채널** | **(c) HTTP MCP transport 확정** (Phase 0 실증 3 통과). Promaker in-process Kestrel + `ModelContextProtocol.AspNetCore` 1.2.0. 자식 Promaker spawn / Job Object / named pipe / WinExe stdio 진입점 분리 = 영구 skip |
| 5 | IPC 보안 | (c) 채택 → 5.0 적용: loopback bind + ephemeral port + handshake nonce + Process.SessionId |
| **7** | **Undo 단위** | **(d) ImportPlan 활용**. 1 LLM turn = 1 undo step. Mutation tool handler = `ImportPlanBuilder` 에 `ImportPlanOperation` 누적, turn end 시점 `store.ApplyImportPlan(label, plan)` 1회 호출. `Ds2.Editor` 무수정 |
| **8** | **Thread 모델** | `IUiDispatcher.InvokeAsync<'T>` Background priority 만 사용 (sync `Invoke` 금지 — `Authoring.fs` 의 `mutable` state 가 lock 없음). **mutation tool 뿐 아니라 read tool 도 dispatcher 경유** — store dict 가 lock-free 라도 동시 변경 중 inconsistent snapshot 회피 + 결정 일관성. AssistantDelta 등 stream-only 이벤트만 dispatcher 우회 (UI 측 throttle 가 race 흡수) |
| 9 | 비동기 표현 | provider→ChatPanel = `IAsyncEnumerable<LlmEvent>`. EditorEvent = 기존 `IObservable<EditorEvent>` 유지 |

## Architecture (큰 그림 — Phase 2 기준)

### `Ds2.LlmAgent` F# DLL (현재 디렉토리)

`fsproj` 의 `<Compile Include>` 순서 (F# 컴파일 순서 의존):

```
AssemblyInfo.fs           InternalsVisibleTo("Ds2.LlmAgent.Tests")
Logging.fs                log4net `Ds2.LlmAgent.Provider` / `Promaker.LlmAgent.RawStream` (verbose, default OFF)
ProcessUtils.fs           PATH 검색 + resolveOrDiagnostic + child process spawn 헬퍼
LlmEvent.fs               DU 8종 (SessionStarted/AssistantDelta/Thinking/ToolUse/ToolResult/RateLimitEvent/SessionEnd/ProviderError)
StreamJsonParser.fs       Claude stream-json 5종 패킷 → LlmEvent seq. MaxLineLength=1MB / MaxJsonDepth=32 cap + parse 실패 시 Log.Warn (Pass B m5/m10)
ClaudeCliVersion.fs       `claude --version` SemVer 검증, ≥2.1.0 fail-fast, C# 친화 record `Result { IsValid; Message; VersionString }`
LlmProvider.fs            `ILlmProvider` 인터페이스 (Phase 2) — Claude / Codex / API 3종 (Anthropic/OpenAI/Ollama) 공통 추상화
ClaudeCliProvider.fs      Claude CLI multi-turn provider, --resume FSM, Channel.CreateBounded<LlmEvent>(256) backpressure
CodexCliArgs.fs           Codex CLI 인자 빌더 (process spawn 없이 단위 검증). danger-full-access sandbox + cd: 임시 폴더 격리
CodexStreamJsonParser.fs  Codex stream-json (turn.created/turn.assistant.message/turn.completed) → LlmEvent seq
CodexCliProvider.fs       Codex CLI provider — 자체 sessions/{sid}.jsonl rollout + MCP HTTP 통신
UiDispatcher.fs           `IUiDispatcher` 인터페이스 (Background priority 권장)
ImportPlanBuilder.fs      mutable plan accumulator (turn 단위, plan.Add / plan.Operations / plan.Count)
ToolOperations.fs         mutation 13종 (queueAddProject/System/Flow/Work/Call/ApiDef/Arrow + queueRemoveEntity/queueRenameEntity + queueBatch + helper) + read 6종 (listProjects/listSystems/describeSystem/describeSubtree/findByName/validateModelByGuid) + format helper 3종. NameMaxLength=128 + sanitizeName Cc/Cf + @/$ prefix 차단
```

ProjectReference = `Ds2.Editor` only. `Ds2.Core` 는 transitive (특히 `[<AutoOpen>] module Queries` in `Ds2.Core.Store`, `ImportPlanOperation` DU).

### Mutation 경로 (Phase 1c 부터)

LLM tool 호출 → handler 가 `ImportPlanBuilder` 에 `ImportPlanOperation` 누적 (현재 store 직접 변경 X) → turn end 의 single `store.ApplyImportPlan("LLM: <user msg>", plan)` 호출 → `Ds2.Editor.ImportPlanApply.applyWithUndo` = 단일 `WithTransaction` + `EmitRefreshAndHistory` 1회 emit → 1 undo step.

직접 `Ds2.Editor.DsStoreNodesExtensions.AddSystem(...)` 등 호출 금지 — 각 호출이 자체 `WithTransaction` 을 만들어 turn 단위 묶음 깨짐.

### Promaker 통합 (Phase 2 완료 상태)

```
Apps/Promaker/Promaker/
├── Controls/Llm/LlmChatPanel.xaml(.cs)         dock UserControl (1d-4 D — 기존 Windows/LlmChatWindow 폐기 후 이전)
├── ViewModels/LlmChatViewModel.cs              [ObservableProperty]/[RelayCommand] + AssistantDelta throttle + LlmTurnLabelPrefix 상수 + provider 5종 dispatch (CLI 2 + API 3)
├── ViewModels/Shell/MainViewModel.cs           ToggleLlmChatCommand + LlmChatVm (lazy) + IsLlmChatVisible + DisposeLlmChatAsync
├── Controls/Shell/MainToolbarEtcContent.xaml   유틸 popup 메뉴 ("LLM Chat (토글)")
├── Controls/Shell/HistoryPanel.xaml            IsLlmTurn 좌측 색띠 + accent foreground (1d-4 F)
├── Behaviors/CtrlWheelZoom.cs                  Ctrl+Wheel zoom 일반화 (LLM Chat / HistoryPanel 등 ListBox 4곳 적용)
├── MainWindow.xaml(.cs)                        column 5/6 (Splitter + Panel) + DataTemplate + Closing 정석 cleanup
├── App.xaml.cs                                 OnStartup 안 McpConfigWriter.SweepStale (1d-5)
└── LlmAgent/
    ├── McpHostService.cs                       in-process Kestrel + ModelContextProtocol.AspNetCore 1.2.0 + handshake nonce 미들웨어 + WaitReadyAsync
    ├── McpConfigWriter.cs                      atomic Owner-only ACL (FileStream + FileSecurity) + PID 포함 파일명 + SweepStale
    ├── ChildProcessTracker.cs                  Job Object cascade kill (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)
    ├── LlmConfig.cs                            consent + API config 통합 (이전 LlmConsent.cs / LlmApiConfig.cs 통합). atomic write + corrupt fallback + DPAPI CurrentUser scope key 보관 + EnsureGranted (Yes/No MessageBox)
    ├── LlmTurnContext.cs                       turn-scoped (plan / dispatcher / 500ms validate cache / mutation quota=50)
    ├── LlmTurnContextProvider.cs               McpHostService 의 AddSingleton 등록 — tool method 인자 자동 DI
    ├── PromakerToolNames.cs                    16개 fully-qualified mcp__promaker__* (allowlist SSOT) — list_projects/list_systems/describe_system/describe_subtree/find_by_name/validate_model + apply_operations (Pass 6 batch) + add_project/system/flow/work/call/api_def/arrow + remove_entity/rename_entity
    ├── SystemPrompt.cs                         static readonly = PromptLoader.LoadComposed() 1회 호출. 본문은 외부 `.md` 로 이전
    ├── PromptLoader.cs                         3-tier 로드 (embedded baseline / `<exedir>\Prompts\*.md` / `%APPDATA%\Promaker\Prompts\*.md`) + 자연 정렬 + append merge. 시작 시 활성 소스 1줄 log
    ├── Prompts/1.SystemPrompt.md               baseline (모델 schema + batch + injection 격리 + 1d 풀세트: Arrow 시맨틱 / greenfield checklist / clarification 템플릿 / `<spec>` delimiter). 추가 `.md` 자연 정렬 후 concat
    ├── WpfDispatcherAdapter.cs                 IUiDispatcher.InvokeAsync (Background priority)
    ├── Api/                                    Phase 2 API providers
    │   ├── ApiChatProvider.cs                  Microsoft.Extensions.AI 기반 IChatClient → ILlmProvider 어댑터. update.Contents 를 LlmEvent 4종으로 매핑
    │   └── ApiProviderFactory.cs               Anthropic 12.20.0 / OpenAI 2.10.0 / OllamaSharp 5.4.25 → IChatClient 빌드 + MCP HttpClient 부착
    └── Tools/ModelTools.cs                     [McpServerToolType] + Sanitize → ToolOperations.sanitizeName 위임 + RunMutation/RunRead 헬퍼 + 16 tool method (Pass 6: ApplyOperations + AddProject)
```

EnsureCli 는 `Task.Run` background → `TaskScheduler.FromCurrentSynchronizationContext` 로 marshalling (UI block 회피).

## 사용자 글로벌 규칙 (`~/.claude/CLAUDE.md` 의 핵심)

- 모든 응답 한국어, 반말 X
- 임의 git commit 금지 (사용자가 `--git-commit` 등 명시 시에만)
- F# > C# 선호. 정석 해결 우선, 우회는 사전 합의
- 신규 추가 10점 / 재활용·refactoring 90점
- F# 8.0 / C# 12.0, .NET 9.0.301
- F# logging: `logDebug` / `logInfo` / `logWarn` / `logError` (사전 정의) 또는 `log4net.LogManager.GetLogger(...)` 패턴 (Ds2.Editor 측 관례)
- F# 변수/함수: PascalCase = Property/public, camelCase = private/internal/F# 함수
- DotNet 환경 — 코드 생성 후 build 통과 확인
- JSON: Newtonsoft.Json 주로, 필요 시 System.Text.Json (현 모듈 stream-json 파싱은 `JsonDocument` 사용)
- 예외처리 자제 — try/catch 는 꼭 필요한 경우에만, 평소엔 fail-fast

## `--git-commit` 플래그 처리 (사용자 규칙)

- `git pull --ff-only` 성공 시에만 진행
- commit 이전에 본 디렉토리의 `doc/todo-*.md` / `doc/done-*.md` 가 현재 작업과 동기화되어 있는지 확인 (다른 세션이 이어받을 수 있는 수준)
- summary 한 줄 + line break + itemize (통상 4줄 이내)
- `Co-Authored-By:` 등 추가 표기 X
- 자동 생성 파일 (bin/obj 등) 은 add 대상 외
- commit user = `kwak@dualsoft.com` (현재 git 체크아웃 사용자)
- commit 성공 후 push (remote branch 존재 시)

## 검증된 사실 표 (todo 의 동일 표 요약 — Phase 2 기준)

| 결정 근거 | 파일:line | 의미 |
|---|---|---|
| 결정 7 (d) | `Solutions/Core/Ds2.Core/Store/ImportPlan.fs:6-21` | `ImportPlanOperation` DU (Phase 2 RemoveEntity/RenameEntity + Pass 5 AddProject 포함) 가 mutation tool 16개 세트 모두 커버 |
| 결정 7 (d) | `Solutions/Core/Ds2.Editor/Editor/ImportPlanApply.fs:46-50` | `applyWithUndo` = 단일 `WithTransaction` + `EmitRefreshAndHistory` 1회. RemoveEntity 분기는 `CascadeRemove.batchRemoveEntities` 위임 |
| 결정 7 (d) 부정 | `Solutions/Core/Ds2.Editor/Editor/Authoring.fs:28-29` | nested transaction = `invalidOp`. outer 감싸기 금지 |
| 결정 7 (d) 부정 | `Solutions/Core/Ds2.Editor/Store/Nodes/Nodes.fs:24-34` | `AddSystem` 등이 자체 `WithTransaction` 호출 — handler 가 직접 호출하면 nested |
| 결정 8 | `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.cs:685-` | 기존 `RequestRebuildAll` 이 `BeginInvoke` + `DispatcherPriority.Background` 패턴 — sync `Invoke` 는 coalescing 깨뜨림 |
| 1d-3 cache 위치 | `Apps/Promaker/Promaker/LlmAgent/LlmTurnContext.cs` `_validateCache` field | turn 단위 (LlmTurnContext 인스턴스 lifetime) — turn 종료 시 자연 expire. dispatcher 단일 sync 안에서만 R/W 라 lock 불필요 |
| 1d-3 검사 카테고리 | `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` `validateModel` (placeholderTokens / categoryOrder) | 6 카테고리 고정 출력 순서 (Orphan / DanglingArrow / EmptyFlow / EmptyWork / DuplicateName / TodoPlaceholder). placeholder = 대문자 정규화 후 {TODO,TBD,FIXME,XXX,?,??,???}. Orphan 은 global scope 만 |
| 1d-4 인자 빌더 분리 | `Solutions/Core/Ds2.LlmAgent/ClaudeCliProvider.fs` `module ClaudeCliArgs` | `build / formatArgs` module-level 노출 — process spawn 없이 단위 검증. `--allowed-tools` 는 반복 인자 형식 (`T1 --allowed-tools T2 ...`) |
| 1d-4 tool 화이트리스트 | `Apps/Promaker/Promaker/LlmAgent/PromakerToolNames.cs:16-34` | 16개 fully-qualified `mcp__promaker__*` 이름 (Pass 6 의 apply_operations / add_project + Phase 2 의 remove_entity / rename_entity 포함). drift 시 LLM 측 차단 → `PromakerToolNamesDriftTests` 가 회귀 검출 |
| 1d-4 Sanitize 차단 카테고리 | `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` `sanitizeName` (Pass E F# 이전) | `CharUnicodeInfo.GetUnicodeCategory` 검사로 Control(Cc) + Format(Cf) 차단. RLO/ZWJ/null byte/제어문자 모두 거부. `ModelTools.cs` 의 `Sanitize` 가 위임 |
| Phase 2 Codex sandbox | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs:265` | `sandbox_mode = "danger-full-access"` + `cd: <임시 폴더>` 격리. consent 다이얼로그 약속 ("파일 시스템 경로 등 전송 X") 대비 별도 정책 — 향후 codexConsentGranted 분리 검토 |
| Phase 2 ILlmProvider | `Solutions/Core/Ds2.LlmAgent/LlmProvider.fs` | CLI 2종 (Claude/Codex) + API 3종 (Anthropic/OpenAI/Ollama) 공통 `Send : msg -> CancellationToken -> IAsyncEnumerable<LlmEvent>`. `ApiChatProvider` 는 `IChatClient.GetStreamingResponseAsync` 어댑터 |

수정 시 두 문서 (todo + done) 의 line 번호 동기 갱신.
