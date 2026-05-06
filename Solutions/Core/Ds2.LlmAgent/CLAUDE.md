# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 본 디렉토리의 역할

`Solutions/Core/Ds2.LlmAgent/` = Promaker (WPF C# 데스크탑 앱) 에 **대화형 LLM agent** 를 통합하는 F# DLL 의 source root, 그리고 통합 작업 자체의 설계/진척 문서 holder.

- `todo-promaker-llm-agent.md` — 남은 작업, 모든 결정 사항 (확정 9개), Phase 0/1a/1b-c/1c/1d 체크리스트, 검증된 사실 표
- `done-promaker-llm-agent.md` — 완료된 spike / phase 의 발견 사항 + 작업 산출물 누적

> **두 문서 유지 정책**: `git commit` 시점에 다른 Claude Code 세션이 바로 이어받아 작업할 수 있는 수준으로 동기화되어야 한다. 진척 / 결정 변경 / 미적용 의도 / 다음 단계 권장 순서가 두 문서 안에서 일관된 상태여야 함. 작업 종료 직전 두 문서 갱신을 빠뜨리지 말 것.

## 새 세션 진입 절차

1. `todo-promaker-llm-agent.md` 의 **"진행 상태"** + **"다음 작업 진입 권장 순서"** 섹션부터 읽기 (현재 phase 위치 파악)
2. `done-promaker-llm-agent.md` 의 가장 최근 phase 절 (현재까지의 산출물 + 의도적 미적용 항목)
3. todo 의 **"검증된 사실 표"** 의 source line 1~2개를 직접 열어 sanity check (특히 `ImportPlanApply.fs:34-38`, `Authoring.fs:28-29`, `App.xaml:4-5`)
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

LLM Chat 검증: Promaker → 상단 ribbon "기타" → "유틸" 토글 popup → "LLM Chat (Phase 1a)".

## 확정된 핵심 결정 (변경 시 todo 갱신 필수)

| # | 결정 | 핵심 |
|---|---|---|
| 1 | 통합 형태 | Promaker 내부 dock panel (별도 console exe 아님). Phase 1a 는 별도 Window, Phase 1d 에서 dock 통합 |
| 2 | 언어 | F# DLL `Ds2.LlmAgent` (비-UI) + C# WPF binding (`LlmChatViewModel`). C# 분리 사유 = `CommunityToolkit.Mvvm` source generator 가 C# only |
| 3 | Provider 우선순위 | Phase 1 = Claude CLI subscription 1st-class only. Codex / OpenAI / Anthropic API / Ollama 는 Phase 2 |
| **4** | **Tool 채널** | **(c) HTTP MCP transport 확정** (Phase 0 실증 3 통과). Promaker in-process Kestrel + `ModelContextProtocol.AspNetCore` 1.2.0. 자식 Promaker spawn / Job Object / named pipe / WinExe stdio 진입점 분리 = 영구 skip |
| 5 | IPC 보안 | (c) 채택 → 5.0 적용: loopback bind + ephemeral port + handshake nonce + Process.SessionId |
| **7** | **Undo 단위** | **(d) ImportPlan 활용**. 1 LLM turn = 1 undo step. Mutation tool handler = `ImportPlanBuilder` 에 `ImportPlanOperation` 누적, turn end 시점 `store.ApplyImportPlan(label, plan)` 1회 호출. `Ds2.Editor` 무수정 |
| **8** | **Thread 모델** | `IUiDispatcher.InvokeAsync<'T>` Background priority 만 사용 (sync `Invoke` 금지 — `Authoring.fs` 의 `mutable` state 가 lock 없음). AssistantDelta 등 stream-only 이벤트는 dispatcher 우회 |
| 9 | 비동기 표현 | provider→ChatPanel = `IAsyncEnumerable<LlmEvent>`. EditorEvent = 기존 `IObservable<EditorEvent>` 유지 |

## Architecture (큰 그림)

### `Ds2.LlmAgent` F# DLL (현재 디렉토리)

```
LlmEvent.fs           DU 8종 (SessionStarted/AssistantDelta/Thinking/ToolUse/ToolResult/RateLimitEvent/SessionEnd/ProviderError)
StreamJsonParser.fs   stream-json 5종 패킷 (system/init, assistant, user, rate_limit_event, result) → LlmEvent seq
ClaudeCliVersion.fs   `claude --version` SemVer 검증, ≥2.1.0 fail-fast, C# 친화 record `Result { IsValid; Message; VersionString }`
ClaudeCliProvider.fs  multi-turn provider, --resume FSM, Channel.CreateBounded<LlmEvent>(256) backpressure, IAsyncEnumerable<LlmEvent>
Logging.fs            log4net `Ds2.LlmAgent.Provider` / `Promaker.LlmAgent.RawStream` (verbose, default OFF)
```

ProjectReference = `Ds2.Editor` only. `Ds2.Core` 는 transitive (특히 `[<AutoOpen>] module Queries` in `Ds2.Core.Store`, `ImportPlanOperation` DU).

### Mutation 경로 (Phase 1c 부터)

LLM tool 호출 → handler 가 `ImportPlanBuilder` 에 `ImportPlanOperation` 누적 (현재 store 직접 변경 X) → turn end 의 single `store.ApplyImportPlan("LLM: <user msg>", plan)` 호출 → `Ds2.Editor.ImportPlanApply.applyWithUndo` = 단일 `WithTransaction` + `EmitRefreshAndHistory` 1회 emit → 1 undo step.

직접 `Ds2.Editor.DsStoreNodesExtensions.AddSystem(...)` 등 호출 금지 — 각 호출이 자체 `WithTransaction` 을 만들어 turn 단위 묶음 깨짐.

### Promaker 통합 (Phase 1a 현재 상태)

```
Apps/Promaker/Promaker/
├── Windows/LlmChatWindow.xaml(.cs)      별도 Window (Phase 1d 에서 dock 통합)
├── ViewModels/LlmChatViewModel.cs       CommunityToolkit.Mvvm [ObservableProperty] / [RelayCommand]
├── ViewModels/Shell/MainViewModel.cs    OpenLlmChatCommand 1개 추가
└── Controls/Shell/MainToolbarEtcContent.xaml   유틸 popup 메뉴 항목 1개
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
- commit 이전에 본 디렉토리의 `todo-*.md` / `done-*.md` 가 현재 작업과 동기화되어 있는지 확인 (다른 세션이 이어받을 수 있는 수준)
- summary 한 줄 + line break + itemize (통상 4줄 이내)
- `Co-Authored-By:` 등 추가 표기 X
- 자동 생성 파일 (bin/obj 등) 은 add 대상 외
- commit user = `kwak@dualsoft.com` (현재 git 체크아웃 사용자)
- commit 성공 후 push (remote branch 존재 시)

## 검증된 사실 표 (todo 의 동일 표 요약)

| 결정 근거 | 파일:line | 의미 |
|---|---|---|
| 결정 7 (d) | `Solutions/Core/Ds2.Core/Store/ImportPlan.fs:5-15` | `ImportPlanOperation` DU 9종이 phase 1 mutation tool 세트 모두 커버 |
| 결정 7 (d) | `Solutions/Core/Ds2.Editor/Editor/ImportPlanApply.fs:34-38` | `applyWithUndo` = 단일 `WithTransaction` + `EmitRefreshAndHistory` 1회 |
| 결정 7 (d) 부정 | `Solutions/Core/Ds2.Editor/Editor/Authoring.fs:28-29` | nested transaction = `invalidOp`. outer 감싸기 금지 |
| 결정 7 (d) 부정 | `Solutions/Core/Ds2.Editor/Store/Nodes/Nodes.fs:24-34` | `AddSystem` 등이 자체 `WithTransaction` 호출 — handler 가 직접 호출하면 nested |
| 결정 8 | `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.cs:652-679` | 기존 `RequestRebuildAll` 이 `BeginInvoke` + `DispatcherPriority.Background` 패턴 — sync `Invoke` 는 coalescing 깨뜨림 |

수정 시 두 문서 (todo + done) 의 line 번호 동기 갱신.
