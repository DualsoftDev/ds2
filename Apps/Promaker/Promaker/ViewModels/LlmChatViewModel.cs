using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.LlmAgent;
using Promaker.LlmAgent;
using log4net;

namespace Promaker.ViewModels;

/// <summary>
/// Promaker 의 LLM chat ViewModel.
///
/// Phase 1a — Claude CLI multi-turn echo (--resume FSM).
/// Phase 1b-c — McpHostService 자동 start + .mcp-config 자동 작성 + ClaudeCliOptions 적용.
///   현재 등록 tool = PingTool 1개 (검증용). 실제 mutation/read tool 은 phase 1c 부터.
/// Phase 1d 예정 — dock panel 통합 / 50ms aggregation throttle / consent dialog.
/// </summary>
public partial class LlmChatViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LlmChatViewModel));

    private readonly McpHostService _mcpHost = new();
    private McpConfigWriter? _mcpConfig;
    private ClaudeCliProvider? _provider;

    // ReSharper disable once NotAccessedField.Local — Phase 1c 진입 시 tool handler 에 주입.
    private readonly IUiDispatcher _dispatcher;

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

    public LlmChatViewModel()
    {
        _dispatcher = new WpfDispatcherAdapter(Dispatcher.CurrentDispatcher);
        _ = InitializeAsync();
    }

    /// <summary>
    /// Window 진입 시 1회 호출:
    ///   1) MCP host 띄우고 ServerUrl / HandshakeNonce 확정
    ///   2) .mcp-config 임시 파일 작성
    ///   3) ClaudeCliProvider 구성 (mcp-config + bypassPermissions)
    ///   4) Claude CLI 버전 확인 (background)
    /// </summary>
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
                channelCapacity: 256);
            _provider = new ClaudeCliProvider(options);

            StatusText = $"MCP host {_mcpHost.ServerUrl} 준비 / Claude CLI 검출 중…";

            await Task.Run(() =>
            {
                var ver = _provider!.EnsureCli();
                return ver;
            }).ContinueWith(t =>
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
            if (_streamingTurn != null) _streamingTurn.IsStreaming = false;
            _streamingTurn = null;
            IsSending = false;
            SendCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            _cts?.Dispose();
            _cts = null;
        }
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
                AppendAssistant($"\n[tool_result] {(tr.isError ? "ERROR " : "")}{Truncate(tr.content, 200)}\n");
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
