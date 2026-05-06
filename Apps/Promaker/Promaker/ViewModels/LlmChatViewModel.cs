using System;
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
/// Promaker 의 LLM chat ViewModel.
///
/// Phase 1c — system prompt + add_system / list_systems mutation/read tool + turn end ApplyImportPlan.
///   1 LLM turn = 1 undo step (결정 7 (d), `Ds2.Editor.ImportPlanApply.applyWithUndo`).
/// Phase 1d 예정 — dock panel 통합 / 50ms aggregation throttle / consent dialog / tool 풀세트.
/// </summary>
public partial class LlmChatViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LlmChatViewModel));

    private readonly DsStore _store;
    private readonly IUiDispatcher _dispatcher;
    private readonly McpHostService _mcpHost = new();
    private McpConfigWriter? _mcpConfig;
    private ClaudeCliProvider? _provider;

    private CancellationTokenSource? _cts;
    private ChatTurn? _streamingTurn;

    public ObservableCollection<ChatTurn> Turns { get; } = new();

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

    public LlmChatViewModel(DsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dispatcher = new WpfDispatcherAdapter(Dispatcher.CurrentDispatcher);
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        var uiContext = TaskScheduler.FromCurrentSynchronizationContext();
        try
        {
            await _mcpHost.StartAsync().ConfigureAwait(true);
            _mcpConfig = McpConfigWriter.Create("promaker", _mcpHost.ServerUrl, _mcpHost.HandshakeNonce);

            var options = new ClaudeCliOptions(
                executablePath: Microsoft.FSharp.Core.FSharpOption<string>.None,
                mcpConfigPath: Microsoft.FSharp.Core.FSharpOption<string>.Some(_mcpConfig.Path),
                permissionMode: Microsoft.FSharp.Core.FSharpOption<string>.Some("bypassPermissions"),
                model: Microsoft.FSharp.Core.FSharpOption<string>.None,
                systemPrompt: Microsoft.FSharp.Core.FSharpOption<string>.Some(SystemPromptText.Phase1c),
                channelCapacity: 256);
            _provider = new ClaudeCliProvider(options);

            StatusText = $"MCP host {_mcpHost.ServerUrl} 준비 / Claude CLI 검출 중…";

            await Task.Run(() => _provider!.EnsureCli()).ContinueWith(t =>
            {
                var result = t.Result;
                if (result.IsValid)
                {
                    StatusText = $"준비 완료 — MCP host {_mcpHost.ServerUrl}, Claude CLI {result.VersionString}";
                    IsReady = true;
                    SendCommand.NotifyCanExecuteChanged();
                }
                else
                {
                    StatusText = result.Message;
                    Turns.Add(new ChatTurn { Role = "system", Text = result.Message });
                }
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, uiContext);
        }
        catch (Exception ex)
        {
            Log.Error("LlmChatViewModel 초기화 실패", ex);
            StatusText = $"초기화 실패: {ex.Message}";
            Turns.Add(new ChatTurn { Role = "system", Text = $"초기화 실패: {ex.Message}" });
        }
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
        var label = $"LLM: {Truncate(prompt, 50)}";
        var plan = ctx.Plan.Build();
        await _dispatcher.InvokeAsync(() =>
            DsStoreImportPlanExtensions.ApplyImportPlan(_store, label, plan));
        AppendAssistant($"\n[applied] {plan.Operations.Length} operation(s) committed as 1 undo step.");
    }

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
        _streamingTurn.Text += fragment;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _mcpConfig?.Dispose();
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
