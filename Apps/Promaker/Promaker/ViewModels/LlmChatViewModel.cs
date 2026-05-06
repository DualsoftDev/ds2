using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.LlmAgent;
using Promaker.LlmAgent;
using log4net;

namespace Promaker.ViewModels;

/// <summary>
/// Phase 2 후속 — Claude / Codex 둘 다 ILlmProvider 로 추상화 후 dispatch.
/// </summary>
public enum LlmProviderKind
{
    Claude,
    Codex,
}

/// <summary>
/// Promaker 의 LLM chat ViewModel.
///
/// Phase 1c — system prompt + add_system / list_systems mutation/read tool + turn end ApplyImportPlan.
///   1 LLM turn = 1 undo step (결정 7 (d), `Ds2.Editor.ImportPlanApply.applyWithUndo`).
/// Phase 2 — `ILlmProvider` 추상화 + Claude / Codex provider dispatch (UI ComboBox).
/// </summary>
public partial class LlmChatViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LlmChatViewModel));

    private readonly DsStore _store;
    private readonly IUiDispatcher _dispatcher;
    private readonly McpHostService _mcpHost = new();
    private McpConfigWriter? _mcpConfig;
    /// <summary>
    /// Codex 격리 워크스페이스 디렉토리 (`%TEMP%/Promaker/codex-workspace-&lt;guid&gt;/`). lazy 생성 (Codex provider
    /// 첫 선택 시), DisposeAsync 시 재귀 삭제. McpConfigWriter 와 동일 lifecycle 패턴.
    ///
    /// **격리 사유**: codex 0.125 는 `sandbox_mode = "danger-full-access"` 만 MCP tool call 통과 (community
    /// issue 1379772) → file system / network 자유 접근. 의도는 codex 가 promaker MCP tool 만 호출하는 것.
    /// `cd:` 를 사용자 repo 가 아닌 빈 임시 폴더로 두면 codex 의 file 탐색 / git 호출이 사용자 작업물에 닿지 않음.
    /// instructions 파일도 같은 디렉토리 안에 두어 단일 cleanup.
    /// </summary>
    private string? _codexWorkspacePath;
    private string? _codexInstructionsPath;
    private ILlmProvider? _provider;

    /// <summary>
    /// Provider 전환 race 가드 — `OnSelectedProviderChanged` 가 빠르게 두 번 호출되어도 stale 결과로
    /// IsReady/StatusText 갱신되지 않도록 sequence 비교. async EnsureCli 의 await 후에 검사.
    /// </summary>
    private int _switchCounter;

    private CancellationTokenSource? _cts;
    private ChatTurn? _streamingTurn;

    /// <summary>
    /// AssistantDelta aggregation buffer. Claude CLI 가 30~60Hz 로 fragment 를 보내면
    /// `_streamingTurn.Text +=` 매 호출이 INotifyPropertyChanged → TextBlock invalidate 를 유발해
    /// 깜빡임 + CPU 부하. 50ms 윈도로 묶어 ~20Hz 로 down-sample (사용자 체감 변화 없음).
    /// Codex 는 단일 패킷이라 throttle 효과 없음 (noop) — Claude 와 공유해도 무해.
    /// </summary>
    private readonly System.Text.StringBuilder _pendingAssistant = new();
    private DispatcherTimer? _assistantFlushTimer;
    private const int AssistantFlushIntervalMs = 50;

    public ObservableCollection<ChatTurn> Turns { get; } = new();

    public IReadOnlyList<LlmProviderKind> AvailableProviders { get; } =
        new[] { LlmProviderKind.Claude, LlmProviderKind.Codex };

    [ObservableProperty]
    private string _input = "";

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private string _statusText = "초기화 중…";

    [ObservableProperty]
    private string? _sessionId;

    [ObservableProperty]
    private LlmProviderKind _selectedProvider = LlmProviderKind.Claude;

    public LlmChatViewModel(DsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dispatcher = new WpfDispatcherAdapter(Dispatcher.CurrentDispatcher);
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        // Defense-in-depth (1d-4 E): OpenLlmChat 진입점이 1차 차단하나 다른 진입점 추가 시 안전망.
        // 거부 상태에서는 MCP host 도 띄우지 않아 LLM tool 호출 자체가 불가.
        if (!LlmConsent.IsGranted())
        {
            StatusText = "LLM 데이터 전송 동의 미완료 — LLM Chat 메뉴 재진입 시 다이얼로그 표시";
            Turns.Add(new ChatTurn { Role = "system", Text = StatusText });
            return;
        }

        try
        {
            await _mcpHost.StartAsync().ConfigureAwait(true);
            _mcpConfig = McpConfigWriter.Create("promaker", _mcpHost.ServerUrl, _mcpHost.HandshakeNonce);

            await ConfigureProviderAsync(SelectedProvider).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("LlmChatViewModel 초기화 실패", ex);
            StatusText = $"초기화 실패: {ex.Message}";
            Turns.Add(new ChatTurn { Role = "system", Text = $"초기화 실패: {ex.Message}" });
            // McpHostService.WaitReadyAsync timeout 등으로 throw 시 _app 은 이미 set 된 상태.
            // panel close 까지 DisposeAsync 가 지연되면 background Kestrel + ephemeral port leak →
            // defense-in-depth 로 즉시 stop. StopAsync 자체가 _app == null 이면 noop 이라 idempotent.
            await _mcpHost.StopAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Provider 생성 + EnsureCli 검증. SelectedProvider 변경 시 / 초기화 시 호출.
    /// stale switch race = `_switchCounter` 증가 후 await 경계 뒤에서 비교.
    ///
    /// **try/catch 사유**: `OnSelectedProviderChanged` 의 `_ = ConfigureProviderAsync(...)` fire-and-forget
    /// 경로에서 unobserved task exception 이 발생하면 GC finalizer 까지 노출이 지연되어 디버깅 어려움.
    /// `InitializeAsync` 가 동일 try/catch 패턴이므로 일관성 + StatusText/Turns 에 사용자 가시화. provider
    /// ctor / dispatcher 호출 / collection 수정 등의 동기 예외도 본 catch 가 흡수.
    /// </summary>
    private async Task ConfigureProviderAsync(LlmProviderKind kind)
    {
        var myCounter = Interlocked.Increment(ref _switchCounter);

        try
        {
            // 진행 중 turn 취소 + 기존 provider session 정리.
            _cts?.Cancel();
            _provider?.ClearSession();

            ILlmProvider provider = kind switch
            {
                LlmProviderKind.Claude => CreateClaudeProvider(),
                LlmProviderKind.Codex => CreateCodexProvider(),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown provider"),
            };

            _provider = provider;
            IsReady = false;
            SessionId = null;
            StatusText = $"{kind} CLI 검출 중…";
            SendCommand.NotifyCanExecuteChanged();

            var result = await Task.Run(() => provider.EnsureCli()).ConfigureAwait(true);

            // stale 결과 무시 (다른 switch 가 더 늦게 들어와 _switchCounter 증가시켰으면).
            if (myCounter != _switchCounter) return;

            if (result.IsValid)
            {
                StatusText = $"준비 완료 — {kind}, MCP {_mcpHost.ServerUrl}, CLI {result.VersionString}";
                IsReady = true;
            }
            else
            {
                StatusText = $"{kind} 초기화 실패: {result.Message}";
                Turns.Add(new ChatTurn { Role = "system", Text = result.Message });
            }
            SendCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            if (myCounter != _switchCounter) return;
            Log.Error($"ConfigureProviderAsync({kind}) 실패", ex);
            StatusText = $"{kind} 초기화 실패: {ex.Message}";
            Turns.Add(new ChatTurn { Role = "system", Text = $"{kind} 초기화 실패: {ex.Message}" });
            IsReady = false;
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    private ILlmProvider CreateClaudeProvider()
    {
        var options = new ClaudeCliOptions(
            executablePath: Microsoft.FSharp.Core.FSharpOption<string>.None,
            mcpConfigPath: Microsoft.FSharp.Core.FSharpOption<string>.Some(_mcpConfig!.Path),
            permissionMode: Microsoft.FSharp.Core.FSharpOption<string>.Some("bypassPermissions"),
            model: Microsoft.FSharp.Core.FSharpOption<string>.None,
            systemPrompt: Microsoft.FSharp.Core.FSharpOption<string>.Some(SystemPromptText.Phase1c),
            strictMcpConfig: true,
            allowedTools: Microsoft.FSharp.Core.FSharpOption<string[]>.Some(PromakerToolNames.All),
            channelCapacity: 256,
            onProcessStarted: Microsoft.FSharp.Core.FSharpOption<Action<System.Diagnostics.Process>>.Some(
                new Action<System.Diagnostics.Process>(ChildProcessTracker.AddProcess)));
        return new ClaudeCliProvider(options);
    }

    /// <summary>
    /// Codex CLI provider. config override 4종 (mcp url + http_headers + approval_policy + sandbox_mode) +
    /// `experimental_instructions_file` 로 system prompt override + `cd:` 격리 워크스페이스.
    ///
    /// **`sandbox_mode = "danger-full-access"` 사유**: codex 0.125 는 read-only / workspace-write 모드에서
    /// MCP tool call 을 "user cancelled" 로 자동 cancel (community issue 1379772). danger-full-access 만 통과.
    /// 부작용 (file system / network 자유 접근) 은 `cd:` 격리로 1차 완화 — codex 가 빈 임시 폴더에서
    /// 실행되어 사용자 git working tree / 작업 디렉토리에 닿지 않음.
    /// </summary>
    private ILlmProvider CreateCodexProvider()
    {
        // 워크스페이스 디렉토리 + instructions 파일 lazy 생성 (Codex 첫 선택 시점), DisposeAsync 에서 일괄 삭제.
        // experimental_instructions_file 은 path 만 받음 → Phase1c 본문을 워크스페이스 안 .md 파일에 쓰고 path 전달.
        if (_codexWorkspacePath == null)
        {
            _codexWorkspacePath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "Promaker", $"codex-workspace-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(_codexWorkspacePath);
            _codexInstructionsPath = System.IO.Path.Combine(_codexWorkspacePath, "instructions.md");
            System.IO.File.WriteAllText(_codexInstructionsPath, SystemPromptText.Phase1c, System.Text.Encoding.UTF8);
            // CODEX_HOME 격리는 e2e 검증 결과 인증 정보까지 격리시켜 401 Unauthorized 발생 (codex 의
            // auth 토큰이 ~/.codex/auth.json 에 저장되어 있어 격리 디렉토리에서는 못 찾음). cleanup 정책은
            // 후속 패스에서 (b) Reset() 시 ~/.codex/sessions/<sid>.jsonl 직접 삭제 또는 인증 파일 복사로 전환 예정.
            // 옵션 필드 자체는 보존 (향후 conditional 사용 가능성).
            Log.Info($"Codex 워크스페이스 격리 디렉토리 생성 — {_codexWorkspacePath}");
        }

        var configOverrides = new[]
        {
            new System.Tuple<string, string>(
                "mcp_servers.promaker.url",
                $"\"{_mcpHost.ServerUrl}\""),
            // codex docs 의 공식 예시 (`http_headers = { "X-Figma-Region" = "..." }`) 와 동일 inline table 형식.
            new System.Tuple<string, string>(
                "mcp_servers.promaker.http_headers",
                $"{{ \"X-Promaker-Nonce\" = \"{_mcpHost.HandshakeNonce}\" }}"),
            // 사용자가 chat 명령으로 의도 표현 → mcp tool 호출에 추가 approval 불필요.
            new System.Tuple<string, string>("approval_policy", "\"never\""),
            // codex 0.125 의 mcp tool call 통과 조건 (위 docstring 참조).
            new System.Tuple<string, string>("sandbox_mode", "\"danger-full-access\""),
        };
        var options = new CodexCliOptions(
            executablePath: Microsoft.FSharp.Core.FSharpOption<string>.None,
            cd: Microsoft.FSharp.Core.FSharpOption<string>.Some(_codexWorkspacePath!),
            model: Microsoft.FSharp.Core.FSharpOption<string>.None,
            json: true,
            // ephemeral=false: thread rollout 을 disk 에 남겨 다음 turn 의 `exec resume <sid>` 가 동작.
            ephemeral: false,
            ignoreUserConfig: true,
            skipGitRepoCheck: true,
            fullAuto: false,
            dangerouslyBypassApprovalsAndSandbox: false,
            configOverrides: Microsoft.FSharp.Core.FSharpOption<System.Tuple<string, string>[]>.Some(configOverrides),
            experimentalInstructionsFile: Microsoft.FSharp.Core.FSharpOption<string>.Some(_codexInstructionsPath!),
            // CODEX_HOME 격리는 인증 정보까지 격리시켜 401 — None 으로 두어 사용자 ~/.codex/ 사용.
            // cleanup 은 후속 패스에서 (b) Reset rollout 직접 삭제 등으로 전환.
            codexHome: Microsoft.FSharp.Core.FSharpOption<string>.None,
            channelCapacity: 256);
        return new CodexCliProvider(options);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (_provider == null) return;
        var prompt = (Input ?? "").Trim();
        if (prompt.Length == 0) return;

        IsSending = true;
        SendCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();

        Turns.Add(new ChatTurn { Role = "user", Text = prompt });
        _streamingTurn = new ChatTurn { Role = "assistant", Text = "", IsStreaming = true };
        Turns.Add(_streamingTurn);

        Input = "";

        // Turn-scoped context — tool method 가 [FromServices] 로 받음.
        // 안전망: 이전 turn 의 EndTurn 누락 방어 (정상 흐름은 finally 에서 종료됨).
        // EndTurn 은 idempotent — current=null 이면 null 반환.
        _mcpHost.TurnProvider.EndTurn();
        var turnCtx = new LlmTurnContext(_store, _dispatcher);
        _mcpHost.TurnProvider.BeginTurn(turnCtx);

        _cts = new CancellationTokenSource();
        try
        {
            var stream = _provider.Send(prompt, _cts.Token);
            await foreach (var evt in stream.ConfigureAwait(true))
            {
                HandleEvent(evt);
            }
        }
        catch (OperationCanceledException) { /* user cancel */ }
        catch (Exception ex)
        {
            Log.Error("LlmChatViewModel.SendAsync 실패", ex);
            AppendAssistant($"\n[ERROR] {ex.Message}");
        }
        finally
        {
            // Stream 종료 시 throttle 의 잔여 fragment 즉시 반영 (마지막 50ms 안 들어온 텍스트가 손실되지 않도록).
            _assistantFlushTimer?.Stop();
            FlushAssistantBuffer();

            // Turn end — plan apply (결정 7 (d): 1 turn = 1 undo step)
            var endedCtx = _mcpHost.TurnProvider.EndTurn();
            if (endedCtx != null && !endedCtx.Plan.IsEmpty)
            {
                try
                {
                    await ApplyTurnPlanAsync(endedCtx, prompt);
                }
                catch (Exception ex)
                {
                    Log.Error("ApplyImportPlan 실패", ex);
                    AppendAssistant($"\n[ApplyImportPlan ERROR] {ex.Message}");
                }
            }

            if (_streamingTurn != null) _streamingTurn.IsStreaming = false;
            _streamingTurn = null;
            IsSending = false;
            SendCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task ApplyTurnPlanAsync(LlmTurnContext ctx, string prompt)
    {
        var label = $"{LlmTurnLabelPrefix}{Truncate(prompt, 50)}";
        var plan = ctx.Plan.Build();
        await _dispatcher.InvokeAsync(() =>
            DsStoreImportPlanExtensions.ApplyImportPlan(_store, label, plan));
        AppendAssistant($"\n[applied] {plan.Operations.Length} operation(s) committed as 1 undo step.");
        // m6 — finally 의 first flush 이후 timer 가 다시 시작되었으므로, [applied] 메시지가 손실되지
        // 않도록 즉시 한 번 더 flush. 이후 finally 의 IsStreaming=false 흐름이 자연스럽게 종료.
        _assistantFlushTimer?.Stop();
        FlushAssistantBuffer();
    }

    /// <summary>
    /// m8 — `LlmChatViewModel` 의 ApplyImportPlan label prefix. `HistoryPanelItem.IsLlmTurn` 도 같은 prefix 로 식별.
    /// 양쪽 magic string 중복 회피.
    /// </summary>
    public const string LlmTurnLabelPrefix = "LLM: ";

    private bool CanSend() => IsReady && !IsSending && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    private bool CanCancel() => IsSending;

    [RelayCommand]
    private void Reset()
    {
        Cancel();
        _provider?.ClearSession();
        Turns.Clear();
        SessionId = null;
        StatusText = "세션 초기화 완료";
    }

    partial void OnInputChanged(string value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnIsReadyChanged(bool value) => SendCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// ComboBox 변경 시 provider 재구성. 첫 init 이전 (`_mcpConfig == null`) 에는 무시 — InitializeAsync 의
    /// ConfigureProviderAsync 가 SelectedProvider 기본값으로 처리.
    /// </summary>
    partial void OnSelectedProviderChanged(LlmProviderKind value)
    {
        if (_mcpConfig == null) return;
        _ = ConfigureProviderAsync(value);
    }

    private void HandleEvent(LlmEvent evt)
    {
        switch (evt)
        {
            case LlmEvent.SessionStarted started:
                SessionId = started.sessionId;
                var sidShort = started.sessionId.Length >= 8 ? started.sessionId.Substring(0, 8) : started.sessionId;
                StatusText = $"session={sidShort}… model={started.model} tools={started.tools.Length} mcp={started.mcpServers.Length}";
                break;

            case LlmEvent.AssistantDelta delta:
                AppendAssistant(delta.text);
                break;

            case LlmEvent.Thinking _:
                break;

            case LlmEvent.ToolUse tu:
                AppendAssistant($"\n[tool_use] {tu.name}\n");
                break;

            case LlmEvent.ToolResult tr:
                AppendAssistant($"\n[tool_result] {(tr.isError ? "ERROR " : "")}{Truncate(tr.content, 400)}\n");
                break;

            case LlmEvent.RateLimitEvent rl:
                StatusText = $"rate-limit: {rl.status} (resets {rl.resetsAtUnix})";
                break;

            case LlmEvent.SessionEnd end:
                StatusText = $"turn 종료 — {end.durationMs}ms, ${end.costUsd:0.0000}, stop={end.stopReason}, denials={end.permissionDenialCount}";
                break;

            case LlmEvent.ProviderError err:
                AppendAssistant($"\n[provider error] {err.message}\n");
                StatusText = err.message;
                break;
        }
    }

    private void AppendAssistant(string fragment)
    {
        if (_streamingTurn == null) return;
        _pendingAssistant.Append(fragment);

        if (_assistantFlushTimer == null)
        {
            _assistantFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(AssistantFlushIntervalMs)
            };
            _assistantFlushTimer.Tick += (_, _) =>
            {
                _assistantFlushTimer!.Stop();
                FlushAssistantBuffer();
            };
        }
        if (!_assistantFlushTimer.IsEnabled) _assistantFlushTimer.Start();
    }

    private void FlushAssistantBuffer()
    {
        if (_pendingAssistant.Length == 0 || _streamingTurn == null) return;
        _streamingTurn.Text += _pendingAssistant.ToString();
        _pendingAssistant.Clear();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _assistantFlushTimer?.Stop();
        _mcpConfig?.Dispose();
        if (_codexWorkspacePath != null)
        {
            try
            {
                if (System.IO.Directory.Exists(_codexWorkspacePath))
                    System.IO.Directory.Delete(_codexWorkspacePath, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"Codex 워크스페이스 디렉토리 삭제 실패 — {_codexWorkspacePath}", ex);
            }
        }
        await _mcpHost.DisposeAsync().ConfigureAwait(false);
    }
}

public partial class ChatTurn : ObservableObject
{
    [ObservableProperty]
    private string _role = "";

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private bool _isStreaming;
}
