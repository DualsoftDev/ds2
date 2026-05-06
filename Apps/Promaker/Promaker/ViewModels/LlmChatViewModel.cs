using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.LlmAgent;
using log4net;

namespace Promaker.ViewModels;

/// <summary>
/// Phase 1a — Promaker 의 LLM chat 진입 ViewModel.
///
/// Mutation tool 미통합 (echo only). 첫 turn 의 SessionStarted 패킷에서 session_id 를 캡처하여
/// 이후 turn 에 --resume 자동 전달. AssistantDelta 는 마지막 ChatTurn 에 누적.
/// Phase 1d 에서 dock panel 통합 / 50ms aggregation throttle / mutation tool wiring 추가.
/// </summary>
public partial class LlmChatViewModel : ObservableObject
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LlmChatViewModel));

    private readonly ClaudeCliProvider _provider;
    private CancellationTokenSource? _cts;
    private ChatTurn? _streamingTurn;

    public ObservableCollection<ChatTurn> Turns { get; } = new();

    [ObservableProperty]
    private string _input = "";

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string? _sessionId;

    public LlmChatViewModel()
    {
        _provider = new ClaudeCliProvider(ClaudeCliOptions.Default);
        StatusText = "Claude CLI 검출 중…";
        // 동기 EnsureCli (WaitForExit 5초) 가 ctor 를 막지 않도록 background.
        // 결과는 dispatcher 로 marshalling 후 status / turn 표시.
        var uiContext = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(() => _provider.EnsureCli()).ContinueWith(t =>
        {
            var result = t.Result;
            StatusText = result.Message;
            if (!result.IsValid)
            {
                Turns.Add(new ChatTurn { Role = "system", Text = result.Message });
            }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, uiContext);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
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

    private bool CanSend() => !IsSending && !string.IsNullOrWhiteSpace(Input);

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
        _provider.ClearSession();
        Turns.Clear();
        SessionId = null;
        StatusText = "세션 초기화 완료";
    }

    partial void OnInputChanged(string value)
    {
        SendCommand.NotifyCanExecuteChanged();
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
                // phase 1a — 무시
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
