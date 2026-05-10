# Promaker — 파일 "닫기" 명령 + LLM 새 프로젝트 협상 작업 계획

> 본 문서는 외부 검토 사이클을 거쳐 architectural 안전성을 확보한 최종 작업 계획.
> 작성 시점: 2026-05-11
> 작업 경로: `F:\Git\ds2\feature-llm\Apps\Promaker\Promaker`
> branch: `feature/llm`

---

## 1. 작업 목표

1. **파일 메뉴에 "닫기" 명령 추가** — 앱 처음 시작 시의 빈 상태(시작화면)로 되돌리기. dirty 면 저장 여부 확인.
2. **빈 상태에서 LLM Chat 토글 시 패널이 실제로 노출되도록** 버그 수정.
3. **LLM 의 `add_project` (단일 + `apply_operations` batch 안 inputs) 호출 시 1-file/1-project 모델 강제** — store 에 이미 project 가 있으면 즉시 거부. **자동 reset 미수행** (provider `ClearSession` contract 위반 회피).
4. **닫기 시 LLM context clear** + 토큰 절약. 마지막 project 경로만 `LlmChatViewModel.LastClosedProjectPath` 에 in-memory 보존, 다음 turn `SendAsync` 의 `promptForProvider` 구성 시 별도 분기로 hint 주입.

UI 의 기존 "새 파일" 버튼(`NewProjectCommand`) 동작은 **그대로 유지** — `NewProject`/`NewSystem`/`NewFlow` 3종 자동 생성.

---

## 2. 코드 현황 / 설계 검증 (grep 시점 2026-05-11)

### 2.1 자동 reset 폐기 — 4가지 architectural 제약

LLM 측에서 in-tool 호출로 현재 프로젝트를 자동 reset 하려는 모든 시도는 다음 4가지 코드 사실 때문에 contract 위반 또는 silent data loss 를 일으킨다. 본 작업은 **자동 reset 자체를 안 함** 으로 4건 모두 회피.

1. **`LlmTurnContext.Store` 는 turn-start 시 캡처되는 immutable reference.** `LlmAgent/LlmTurnContext.cs:16-43` ctor only assign. `ViewModels/LlmChatViewModel.cs:479` 에서 `new LlmTurnContext(_store, _dispatcher)` 로 생성. mid-turn 에 store 를 swap 하면 `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs:204-214` 의 `resolveFirstProjectId` 가 store 우선 lookup 이라 옛 store/새 store 사이 불일치 + 큐잉된 plan op silent loss.

2. **provider `ClearSession` 은 turn 진행 중 호출 금지.** `Solutions/Core/Ds2.LlmAgent/LlmProvider.fs:19-20` 명시: *"ClearSession 도 진행 중 turn 이 없을 때만 안전. provider 측 lock 부재. 중간에 ClearSession 부르지 않을 것."* MCP tool call 도중 `MainViewModel.Reset()` 를 호출하면 `LlmChatViewModel.UpdateStore() → provider.ClearSession()` 이 send 진행 중 발화 = contract 위반. provider 의 `_history` lock-free List 손상 가능.

3. **`apply_operations` batch path 가 단일 `add_project` 가드를 우회.** `LlmAgent/Tools/ModelTools.cs:202` `ToolOperations.queueBatch(ctx.Plan, ctx.Store, inputs)` 가 inputs 안 `{op: "add_project"}` 도 dispatch. 단일 tool 가드만으로는 부족.

4. **`HasProject` 는 store event 만으로 자동 갱신되지 않음.** `ViewModels/Shell/EventHandling.cs:105` `case { IsStoreRefreshed: true }: RequestRebuildAll();` — UI rebuild 만 호출, `HasProject` 갱신 코드 없음. LLM `ApplyImportPlan` 후 WelcomeOverlay/CloseFileCommand 가 "no project" 상태에 잔존 가능. **본 작업 Step 8 에서 갱신 hook 1줄 추가 필수**.

### 2.2 핵심 진입점

- `MainViewModel.NewProject()` — `ViewModels/Shell/MainViewModel.Lifecycle.cs:13`. UI "새 파일" + 시작화면 "새로 만들기" 버튼이 모두 호출.
- `MainViewModel.Reset()` — 동일 파일 line 43-75. 빈 store + `HasProject=false` + `CanvasManager.Reset()` (모든 탭 닫힘) + `LlmChatVm?.UpdateStore(_store)`.
- `MainViewModel.ConfirmDiscardChanges()` — line 77-84. IsDirty + AskSaveChanges + 저장 실패 시 진행 차단.

### 2.3 LLM Chat 활성화 / 빈 상태 토글 버그

- 토글 버튼: `Controls/Shell/MainToolbarEtcContent.xaml:99-118`. `Visibility="{Binding IsLlmEnabled, Converter={StaticResource BoolToVis}}"` (환경변수 `ENABLE_LLM`).
- `LlmChatViewModel.CanSend()` (`ViewModels/LlmChatViewModel.cs:599`): `IsReady && !IsSending && (Input || HasAttachments)`. `HasProject` 의존 아님.
- 토글 코드: `ViewModels/Shell/MainViewModel.LlmChat.cs:27-36`. lazy `LlmChatVm` 생성 후 `IsLlmChatVisible` 토글.
- **버그 원인**: `WelcomeOverlay` (`MainWindow.xaml:133`) 가 `Grid.ColumnSpan="7"` + `Panel.ZIndex="10"` + `HasProject=false` Visible → LLM dock column 도 덮여서 안 보임. ColumnSpan 7→5 로 해소.

### 2.4 LLM 측 새 프로젝트 경로

- `LlmAgent/Tools/ModelTools.cs:303-315` `AddProject` 단일 tool — `ImportPlanBuilder.queueAddProject` 누적.
- `LlmAgent/Tools/ModelTools.cs:202` `ApplyOperations` batch tool — `ToolOperations.queueBatch(ctx.Plan, ctx.Store, inputs)` 안에 `add_project` op 가 dispatch 가능.
- LLM 이 `NewProjectCommand` 자체를 부르는 경로 없음 — NewSystem/NewFlow 자동 생성 안 됨 (의도된 분리).

### 2.5 LlmChatViewModel SendAsync 의 promptForProvider 구성 (Step 6 hint 주입 지점)

`LlmChatViewModel.cs:482-490`:
```csharp
var promptForProvider = prompt;
if (_editorDigest.HasAny)
{
    var prefix = _editorDigest.ToContextMessage();
    _editorDigest.Clear();
    if (!string.IsNullOrEmpty(prefix))
        promptForProvider = prefix + "\n\n" + promptForProvider;
}
```
이 직후에 `LastClosedProjectPath` 별도 분기 추가 (EditorChangeDigest 자동 `MarkProjectReset` 발화와 분리).

### 2.6 LlmChatViewModel reset 메서드 책임 분담

- `UpdateStore(newStore)` (line 689-700): `Cancel` + `provider.ClearSession` + `_store reassign` + `SessionId=null` + `SubscribeEditorEvents` + `_editorDigest.MarkProjectReset` 자동 발화.
- `Reset()` RelayCommand (line 609-620): `Cancel` + `provider.ClearSession` + `Turns.Clear` + `Attachments.Clear` + `AttachmentNotice=""` + `SessionId=null`.
- 두 메서드 idempotent. CloseFile 후 `OnProjectClosing` → `ResetCommand` → 그 후 `MainViewModel.Reset` 안의 `UpdateStore` 가 또 발화해도 안전.

### 2.7 System prompt baking (mid-session 갱신 불가)

`ApiChatProvider.cs:82, 132` system prompt 가 ctor 시점 baking. 따라서 `LastClosedProjectPath` hint 는 system prompt 가 아니라 **매 turn user message prefix** 로 들어가야 함 (Step 6).

### 2.8 탭 관리

`SplitCanvasManager.Reset()` (line 110) → `CanvasWorkspaceState.cs:74` `OpenTabs.Clear() + ActiveTab=null`. **추가 코드 불필요**.

### 2.9 단축키 충돌 + DevExpress

- `MainWindow.xaml:40` Ctrl+W = `Canvas.CloseActiveTabCommand` 점유 중 → 닫기 명령은 **Ctrl+Shift+W** 부여 (탭 닫기 보존).
- DevExpress 24.1.7 컨트롤이 자체 Ctrl+W 가로채기 가능성 — Ctrl+Shift+W 는 거의 안 쓰는 조합이라 회피 가능. Step 9 시나리오에서 검증.

### 2.10 Welcome overlay drop 부수효과

`Controls/Llm/LlmChatPanel.xaml:6-10` 에 `AllowDrop="True"` + `PreviewDrop="Panel_PreviewDrop"` 자체 핸들러 존재. ColumnSpan 7→5 변경 후 LLM dock 위 drop 은 LLM 패널 자체 핸들러가 처리 → 회귀 없음.

### 2.11 HasProject ObservableProperty 정의

`MainViewModel.cs:108-125` `_hasProject` 는 `[ObservableProperty]` + 다수 `[NotifyCanExecuteChangedFor(...)]`. CloseFileCommand 의 CanExecute 자동 갱신을 위해 attribute 리스트에 1줄 추가만 필요.

### 2.12 MainToolbar 파일 섹션

`Controls/Shell/MainToolbar.xaml:43` `<Grid x:Name="ProjectQuickAccessSection" Grid.Column="0">` 실재 — 닫기 버튼 추가 위치.

---

## 3. 결정된 정책

| 항목 | 결정 |
|---|---|
| 단축키 | **Ctrl+Shift+W** = 프로젝트 닫기. Ctrl+W = 기존 탭 닫기 유지 |
| LLM `add_project` 정책 | **store 에 project 가 이미 있으면 무조건 거부** (dirty 무관, plan 누적 무관). 자동 reset 안 함. 사용자에게 UI 메뉴 닫기 안내 |
| 거부 메시지 | **`VALIDATION_ERROR: 현재 프로젝트가 열려있습니다. '파일 > 닫기' 메뉴 또는 Ctrl+Shift+W 로 닫은 뒤 다시 시도해 주세요.`** |
| 가드 적용 위치 | **`AddProject` 단일 tool + `ApplyOperations` batch 안 inputs pre-scan** 둘 다 |
| `prepare_new_project` / `close_project` tool 신설 | **안 함** (필요해지면 향후 incremental 확장) |
| `LlmTurnContext` 변경 | **안 함** (`Store`/`Plan` immutable 유지) |
| LLM 메모리 clear 시 마지막 project 경로 보존 | YES (in-memory `LlmChatViewModel.LastClosedProjectPath` — 앱 세션 한정) |
| LastClosedProjectPath 주입 위치 | **`LlmChatViewModel.SendAsync` 의 `promptForProvider` 구성 path** (line 490 직후), `EditorChangeDigest` 와 분리된 별도 분기 |
| LastClosedProjectPath 라이프사이클 | set: CloseFile 시. clear: Open 성공 / NewProject 성공 / Reset RelayCommand |
| Welcome overlay 수정 | `Grid.ColumnSpan` 7 → 5 |
| UI "새 파일" (`NewProject()`) 변경 | 변경 안 함 |
| `OnProjectClosing` 책임 | 기존 `LlmChatViewModel.Reset()` RelayCommand 재활용 + `LastClosedProjectPath = lastPath` 마지막에 set |
| HasProject 동기화 hook | **필수** (`EventHandling.cs:105` `IsStoreRefreshed` 분기에 1줄 추가) |
| Strings.Designer.cs | VS GUI 사용 시 자동 갱신. CLI 환경에서는 빌드 전 수동 update 필요 |
| 단위 테스트 | 가드 분기만 단위 테스트 가능 (FsUnit / xUnit) — UI/dispatch 부분은 통합 시나리오로 |
| Hint 분량 | 100 토큰 미만, 1-2줄 |
| 거부 메시지 prefix | `VALIDATION_ERROR:` (`ModelTools.cs:293` `EnsureErrorPrefix` 컨벤션과 일치) |

---

## 4. 작업 순서

작업 의존성:
```
Step 1 (welcome overlay)  ─┐
                           ├→ Step 9 (검증)
Step 2 → Step 2.5 → Step 3 → Step 4 → Step 5 ─┤
                                              │
Step 6 → Step 7 → Step 8 ─────────────────────┘
```
- 트랙 분리: Step 1 단독 / Step 2~5 (UI 닫기) / Step 6~8 (LLM 흡수).
- Commit 단위: 3개 (Step 1 / Step 2~5 / Step 6~8). 롤백 시 트랙 단위 revert.

---

### Step 0 — 사전 확인

코드 진입 시 첫 작업으로 다음 3건 확정:

1. **`ImportPlanBuilder` 정의 위치 + `HasAddProjectOp` 추가 가능 여부** — F# (`Solutions/Core/Ds2.LlmAgent`) 또는 C#. 가장 작은 patch 로 진입.
2. **`MainViewModel.Hosts.cs:64` 위임 속성** `public bool HasProject => Owner.HasProject;` 의 PropertyChanged 전파 검증 — 위임 속성이 자체 PropertyChanged 발화 안 하면 Owner 의 `OnHasProjectChanged` partial 에서 명시 propagate.
3. **`Strings.Designer.cs` 자동/수동 결정** — 본 작업 환경이 VS GUI 인지 CLI 인지.

---

### Step 1 — `WelcomeOverlay` ColumnSpan 수정
- 파일: `MainWindow.xaml:133`
- 변경: `Grid.ColumnSpan="7"` → `Grid.ColumnSpan="5"`.
- 검증: 빈 상태 + LLM 토글 → 패널 노출.

---

### Step 2 — `CloseFile` RelayCommand + `InternalClose` helper
- 파일: `ViewModels/Shell/MainViewModel.Lifecycle.cs`
```csharp
private void InternalClose(string statusText)
{
    var lastPath = _currentFilePath;
    LlmChatVm?.OnProjectClosing(lastPath);
    Reset();
    StatusText = statusText;
    Log.Info($"Project closed (path={lastPath ?? "(unsaved)"}).");
}

[RelayCommand(CanExecute = nameof(HasProject))]
private void CloseFile()
{
    if (!GuardSimulationSemanticEdit("프로젝트 닫기")) return;
    if (!ConfirmDiscardChanges()) return;
    InternalClose("Closed.");
}
```
- 순서 핵심: `OnProjectClosing(lastPath)` → `Reset()`. (`OnProjectClosing` 안에서 `LastClosedProjectPath` 가 ResetCommand 후에 다시 set 되도록 Step 6 에서 보장.)

---

### Step 2.5 — HasProject attribute + Hosts 위임
- `MainViewModel.cs:108-125` `_hasProject` 의 `[NotifyCanExecuteChangedFor(...)]` 리스트에 `[NotifyCanExecuteChangedFor(nameof(CloseFileCommand))]` 1줄 추가.
- `MainViewModel.Hosts.cs:64` 위임 속성 PropertyChanged 전파 검증 — 필요 시 `partial void OnHasProjectChanged(bool value)` 에서 Hosts 측 propagate.

---

### Step 3 — Strings.resx / Strings.en.resx
- 신규 키:
  - `CloseFile` = "닫기" / en: "Close"
  - `CloseFileTooltip` = "프로젝트 닫기 (Ctrl+Shift+W)" / en: "Close project (Ctrl+Shift+W)"
- VS GUI 사용 시 `Strings.Designer.cs` 자동 갱신. CLI 환경 (예: `dotnet build`) 에서는 수동 update 필요 — Resgen 또는 Designer.cs 직접 편집.

---

### Step 4 — MainToolbar.xaml 닫기 버튼
- 파일: `Controls/Shell/MainToolbar.xaml`
- 위치: `ProjectQuickAccessSection` (line 43, Grid.Column=0) WrapPanel 안, "저장" 버튼 우측.
- 패턴: `RibbonFlatButton` + `iconPacks:PackIconMaterial Kind="FileDocumentRemoveOutline"` + `Command="{Binding CloseFileCommand}"` + `ToolTip="{x:Static res:Strings.CloseFileTooltip}"` + `{x:Static res:Strings.CloseFile}`.

---

### Step 5 — Ctrl+Shift+W 단축키
- 파일: `MainWindow.xaml:37` 근처.
- 추가: `<KeyBinding Gesture="Ctrl+Shift+W" Command="{Binding CloseFileCommand}"/>`
- Ctrl+W (`Canvas.CloseActiveTabCommand`) 그대로 유지.

---

### Step 6 — LLM context clear + LastClosedProjectPath + SendAsync hint 주입

- 파일: **신규 partial** `ViewModels/LlmChatViewModel.ProjectContext.cs`
```csharp
namespace Promaker.ViewModels;

public partial class LlmChatViewModel
{
    /// <summary>마지막 닫힌 프로젝트 경로. 앱 세션 한정 (in-memory).
    /// 라이프사이클: set = OnProjectClosing 시점. clear = OnProjectOpened / Reset RelayCommand.</summary>
    public string? LastClosedProjectPath { get; private set; }

    /// <summary>
    /// MainViewModel.CloseFile 가 Reset() 직전에 호출.
    /// 책임 분담: 기존 ResetCommand 재활용 (Cancel/ClearSession/Turns/Attachments) + LastClosedProjectPath 캡처.
    /// </summary>
    public void OnProjectClosing(string? lastPath)
    {
        ResetCommand.Execute(null);              // 기존 동작 재활용 (Cancel/ClearSession/Turns/Attachments clear)
        LastClosedProjectPath = lastPath;        // ResetCommand 가 null clear 했으므로 마지막에 다시 set
        Log.Info($"LLM context cleared on project close (lastPath={lastPath ?? "(unsaved)"}).");
    }

    /// <summary>새 프로젝트가 열리거나 생성되면 LastClosedProjectPath 무효화.</summary>
    public void OnProjectOpened()
    {
        LastClosedProjectPath = null;
        Log.Info("LastClosedProjectPath cleared (new project opened).");
    }
}
```

- **MainViewModel 측 hook**:
  - `FileCommands.cs:64` `CompleteOpen` 끝: `LlmChatVm?.OnProjectOpened();`
  - `Lifecycle.cs:30` `NewProject()` 의 `HasProject = true` 직후: `LlmChatVm?.OnProjectOpened();`

- **`LlmChatViewModel.Reset()` RelayCommand (line 609-620) 본문 보강** — 사용자가 직접 호출했을 때 LastClosedProjectPath 도 clear:
```csharp
[RelayCommand]
private void Reset()
{
    Cancel();
    _provider?.ClearSession();
    Turns.Clear();
    Attachments.Clear();
    AttachmentNotice = "";
    SessionId = null;
    LastClosedProjectPath = null;        // 신규: 명시 Reset 시 hint 도 clear
    StatusText = "세션 초기화 완료";
}
```

- **순서 함정 해소**: `OnProjectClosing` 본문이 `ResetCommand.Execute(null)` 먼저 호출 → ResetCommand 본문이 `LastClosedProjectPath = null` 로 비움 → 그 후에 `LastClosedProjectPath = lastPath` 다시 set. 위 코드 순서대로 작성.

- **SendAsync 의 promptForProvider hint 주입** (`LlmChatViewModel.cs:482-490` 직후):
```csharp
// 기존 EditorChangeDigest prefix 처리 직후
if (!string.IsNullOrEmpty(LastClosedProjectPath))
{
    var hint = $"<closed_project>\n직전 세션 닫힌 프로젝트 경로: {LastClosedProjectPath}\n사용자가 이 프로젝트를 다시 참조하면 해당 파일을 읽어 컨텍스트를 재구축하세요.\n</closed_project>";
    promptForProvider = hint + "\n\n" + promptForProvider;
}
```
- EditorChangeDigest 의 자동 `MarkProjectReset` 발화와 무관 — `LastClosedProjectPath` 만 검사.
- 100 토큰 미만 가이드 준수.

---

### Step 7 — LLM `add_project` + `apply_operations` batch 가드

- 파일1: `LlmAgent/Tools/ModelTools.cs:303-315` `AddProject` 본문 도입부:
```csharp
return RunMutation(turnProvider, "add_project", ctx =>
{
    SanitizeOrThrow(name, "name");
    var trimmed = name.Trim();

    // store 에 project 가 이미 있으면 거부 (dirty 무관, plan 무관).
    if (Queries.allProjects(ctx.Store).Any())
        return "VALIDATION_ERROR: 현재 프로젝트가 열려있습니다. '파일 > 닫기' 메뉴 또는 Ctrl+Shift+W 로 닫은 뒤 다시 시도해 주세요.";

    // 같은 turn 안 plan 누적 가드 (사용자가 같은 turn 에 add_project 를 두 번 요청하는 경우):
    if (PlanHasQueuedAddProject(ctx.Plan))
        return "VALIDATION_ERROR: 이미 같은 turn 안에 add_project 가 큐잉되어 있습니다. 1 file = 1 project 정책상 한 turn 에 하나만 가능합니다.";

    var projId = ToolOperations.queueAddProject(ctx.Plan, ctx.Store, trimmed);
    return $"[plan] add_project queued: name=\"{trimmed}\", id={projId:D}, planSize={ctx.Plan.Count}{PlanVisibilityHint}";
});
```

- 파일2: `LlmAgent/Tools/ModelTools.cs:202` `ApplyOperations` 본문, `queueBatch` 호출 직전 inputs pre-scan:
```csharp
// batch 우회 차단:
bool inputsHaveAddProject = inputs.Any(i => i.Op == "add_project");
if (inputsHaveAddProject)
{
    if (Queries.allProjects(ctx.Store).Any())
        return "VALIDATION_ERROR: apply_operations batch 안에 add_project 가 포함되어 있으나 현재 프로젝트가 열려있습니다. '파일 > 닫기' 메뉴 또는 Ctrl+Shift+W 로 닫은 뒤 다시 시도해 주세요.";

    if (inputs.Count(i => i.Op == "add_project") > 1)
        return "VALIDATION_ERROR: apply_operations batch 안에 add_project 가 2개 이상 포함되어 있습니다. 1 file = 1 project 정책상 batch 당 최대 1개.";

    if (PlanHasQueuedAddProject(ctx.Plan))
        return "VALIDATION_ERROR: 이미 같은 turn 안에 add_project 가 큐잉되어 있습니다. 1 file = 1 project 정책상 한 turn 에 하나만 가능합니다.";
}

var result = ToolOperations.queueBatch(ctx.Plan, ctx.Store, inputs);
```

- **`PlanHasQueuedAddProject(ImportPlanBuilder)` helper** — `ImportPlanBuilder` 내부 op 컬렉션 순회로 `AddProject` op 존재 검사:
  - 옵션 1: F# 측에 `member plan.HasAddProjectOp = ...` 신설.
  - 옵션 2: C# 측 `ModelTools` 에 `IEnumerable<ImportPlanOperation>` 노출이 가능하면 LINQ 로 검사.
  - Step 0 단축 조사로 정의 위치 확인 후 가장 작은 patch 로 진입.

- **`add_project` / `apply_operations` Description 갱신** — 정책을 LLM 에 노출:
  - `add_project` Description 끝에 한 줄: *"현재 store 에 project 가 이미 있으면 거부됨. 사용자가 UI 메뉴 (Ctrl+Shift+W) 로 닫은 후 재시도 필요."*
  - `apply_operations` Description 에 동일 안내.
  - `SystemPromptText.cs` 의 정책 섹션에도 1줄 추가.

---

### Step 8 — `HasProject` 동기화 hook (필수)

- LLM `ApplyImportPlan` 이 store 에 commit → store event `IsStoreRefreshed` 발화 → `EventHandling.cs:105` `RequestRebuildAll()` 만 호출, `HasProject` 갱신 안 됨.
- 수정: `EventHandling.cs:105` `case { IsStoreRefreshed: true }` 분기에 1줄 추가:
```csharp
case { IsStoreRefreshed: true }:
    HasProject = Queries.allProjects(_store).Any();   // 신규 추가
    RequestRebuildAll();
    return;
```
- 또는 `ApplyImportPlan` 호출 직후 별도 callback 으로 dispatcher.Invoke 안에서 갱신.
- **Welcome overlay 자동 닫힘 / 닫기 명령 활성 보장**.

---

### Step 9 — 빌드 / 실행 검증 (시나리오 16종)

- 빌드 컴파일 오류 0.
- 시나리오:
  1. 빈 상태 → LLM 토글 → 패널 노출 (Step 1).
  2. UI "새 파일" → NewProject + NewSystem + NewFlow 정상 (regression).
  3. 파일 열기 → "닫기" 메뉴 → 빈 상태 + 모든 탭 닫힘.
  4. dirty 상태 "닫기" → 저장 다이얼로그 → 취소 시 닫기 중단 (LLM context 유지).
  5. **Ctrl+Shift+W = 프로젝트 닫기**, **Ctrl+W = 탭 닫기** 분리 동작.
  6. **LLM `add_project` 호출**: store 비어있으면 정상 큐잉, project 있으면 거부 메시지.
  7. **LLM `apply_operations` batch 안 add_project**: store 비어있으면 정상, project 있으면 거부.
  8. 같은 turn 안 add_project 두 번 호출 → 두 번째는 plan 가드로 거부.
  9. close 직후 NewProject — InternalClose 후 NewProject 호출 시 정상.
  10. dirty → 저장 → LLM 새 프로젝트 재요청 — 저장 후 IsDirty=false, 사용자가 UI 닫기 후 LLM 재요청 정상.
  11. streaming 중 close — LLM turn in-flight 도중 사용자 닫기 → ResetCommand 의 Cancel 이 _cts cancel → turn 정상 종료 + UI 빈 상태.
  12. drop zone 회귀 — Welcome 화면 (col 0~4) drop 정상.
  13. hint 잔존 — Open 후 LastClosedProjectPath null clear, 다음 turn promptForProvider 에 hint 안 노출.
  14. LLM 토글 회귀 — 빈/project/dirty 모든 상태에서 LLM 토글 정상.
  15. DevExpress focus + Ctrl+Shift+W — GridControl/Editor 활성 상태에서 닫기 명령 도달 검증.
  16. **HasProject 동기화** (Step 8) — LLM 이 add_project commit 후 WelcomeOverlay 자동 닫힘 + CloseFileCommand 활성.

---

## 5. 관련 파일 인덱스

| 카테고리 | 경로 |
|---|---|
| ViewModel 라이프사이클 | `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.Lifecycle.cs` |
| ViewModel 파일 명령 | `Apps/Promaker/Promaker/ViewModels/Shell/FileCommands.cs` |
| ViewModel LLM 토글 | `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.LlmChat.cs` |
| ViewModel 본체 (HasProject) | `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.cs` (line 108-125) |
| ViewModel Hosts (위임) | `Apps/Promaker/Promaker/ViewModels/Shell/MainViewModel.Hosts.cs` (line 64) |
| Event handling | `Apps/Promaker/Promaker/ViewModels/Shell/EventHandling.cs` (line 105) |
| LLM Chat VM | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` (599 CanSend, 609-620 Reset, 482-490 promptForProvider, 689-700 UpdateStore) |
| LLM Chat VM ProjectContext (신규) | `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.ProjectContext.cs` |
| LLM Turn Context (변경 없음) | `Apps/Promaker/Promaker/LlmAgent/LlmTurnContext.cs` (line 14-43, 95-117) |
| LLM MCP 도구 | `Apps/Promaker/Promaker/LlmAgent/Tools/ModelTools.cs` (202 ApplyOperations, 303-315 AddProject) |
| Editor change digest (변경 없음) | `Apps/Promaker/Promaker/LlmAgent/EditorChangeDigest.cs` |
| API chat provider | `Apps/Promaker/Promaker/LlmAgent/Api/ApiChatProvider.cs` (line 82, 132 system prompt baking) |
| LLM Provider contract | `Solutions/Core/Ds2.LlmAgent/LlmProvider.fs` (line 19-20 ClearSession contract) |
| Tool operations (F# core) | `Solutions/Core/Ds2.LlmAgent/ToolOperations.fs` (line 204-214 resolveFirstProjectId) |
| ImportPlanBuilder | F#/C# 위치 식별 필요 (HasAddProjectOp 신설 위치) |
| System prompt 텍스트 | `Apps/Promaker/Promaker/LlmAgent/SystemPromptText.cs` |
| 메인 윈도우 XAML | `Apps/Promaker/Promaker/MainWindow.xaml` (30-42 InputBindings, 133 WelcomeOverlay) |
| 메인 윈도우 code-behind | `Apps/Promaker/Promaker/MainWindow.xaml.cs` |
| 툴바 (파일 섹션) | `Apps/Promaker/Promaker/Controls/Shell/MainToolbar.xaml` (line 43 ProjectQuickAccessSection) |
| 툴바 (LLM/설정) | `Apps/Promaker/Promaker/Controls/Shell/MainToolbarEtcContent.xaml` |
| LlmChatPanel (drop) | `Apps/Promaker/Promaker/Controls/Llm/LlmChatPanel.xaml` (line 6-10) |
| Canvas Workspace 탭 | `Apps/Promaker/Promaker/ViewModels/CanvasWorkspaceState.cs` (line 74) |
| Split Canvas | `Apps/Promaker/Promaker/ViewModels/SplitCanvasManager.cs` (line 110) |
| 리소스 (한글) | `Apps/Promaker/Promaker/Resources/Strings.resx` |
| 리소스 (영문) | `Apps/Promaker/Promaker/Resources/Strings.en.resx` |
| 리소스 Designer | `Apps/Promaker/Promaker/Resources/Strings.Designer.cs` |

---

## 6. 주의 사항 / 함정

- **자동 reset 폐기 핵심**: store 에 project 있으면 LLM 자동 reset 안 함. 무조건 거부 + UI 닫기 안내. provider `ClearSession` contract 위반 자동 회피.
- **batch 가드 필수**: `apply_operations` 의 inputs pre-scan. 단일 `add_project` 가드만으로는 batch 우회 가능.
- **HasProject hook 필수**: `EventHandling.cs:105` 에 `HasProject = Queries.allProjects(_store).Any()` 1줄 필수. 미적용 시 LLM commit 후 WelcomeOverlay 잔존.
- **OnProjectClosing 순서 함정**: `ResetCommand.Execute(null)` 가 `LastClosedProjectPath` 를 null 로 비우므로 **ResetCommand 호출 직후에 다시 set** (Step 6 코드 순서 그대로):
```csharp
ResetCommand.Execute(null);
LastClosedProjectPath = lastPath;
```
- **Cancel/ClearSession 이중 호출**: `_cts.Cancel()` 과 `provider.ClearSession()` 모두 idempotent 검증됨. CloseFile → OnProjectClosing → ResetCommand → MainViewModel.Reset → UpdateStore 흐름에서 두 번 발화돼도 안전.
- **`_currentFilePath` 캡처 시점**: `Reset()` 직전 로컬 변수. Reset 이후엔 null.
- **CanExecute 자동 갱신**: `_hasProject` 가 `[ObservableProperty]` 이므로 `[NotifyCanExecuteChangedFor(nameof(CloseFileCommand))]` 1줄 추가만으로 자동 작동.
- **Welcome overlay drop 영역**: ColumnSpan 7→5 후 LLM dock 위 drop 은 LlmChatPanel 자체 PreviewDrop 핸들러가 처리. 회귀 없음.
- **Hint 단일 주입**: `LlmChatViewModel.SendAsync` 의 `promptForProvider` 분기 1곳만. EditorChangeDigest 에 추가하지 말 것 (자동 `MarkProjectReset` 발화와 충돌 회피).
- **Strings.Designer.cs CLI 환경**: VS GUI 사용 시 자동. CLI 만 쓰면 빌드 전 수동 update.
- **Log4net 사용**: 모든 신규 메서드에 `Log.Info` / `Log.Warn` 적용. 사용자 컨벤션.
- **단위 테스트**: `add_project` / `apply_operations` 가드 분기를 FsUnit 또는 xUnit 으로. UI 부분은 Step 9 통합 시나리오.
- **DevExpress Ctrl+Shift+W**: Step 9 시나리오 15 에서 GridControl/Editor focus 상태 검증.
- **위임 속성**: `MainViewModel.Hosts.cs:64` Owner 변경 시 propagate 검증 (Step 0).
- **commit 절차**: 사용자 명시 지시 없으면 임의 commit 금지.
- **자가 검열 trigger 충족**: 신규 함수 ≥3 (`CloseFile`, `InternalClose`, `OnProjectClosing`, `OnProjectOpened`) + ≥2 파일 동시 변경 → 자가 검열 절차 필수. 각 Step 후 git diff 기반 sub-agent 리뷰 후 commit / 다음 Step.

---

본 문서대로 Step 0 부터 순서 실행. 각 Step 후 빌드 검증 + 자가 검열 + 사용자 confirm.
