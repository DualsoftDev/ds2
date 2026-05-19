using System;
using System.Windows.Threading;
using Ds2.LlmAgent;
using Promaker.LlmAgent;

namespace Promaker.ViewModels;

/// <summary>
/// LlmChatViewModel partial — LlmEvent 스트림 핸들링 + ChatTurn 추가/append + 50ms throttle 로 UI thrash 차단.
/// 본체와의 share: Turns / _streamingTurn / _pendingAssistant / _assistantFlushTimer / SessionId / StatusText.
/// </summary>
public partial class LlmChatViewModel
{
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
                EnsureStreamingTurn();
                AppendAssistant(delta.text);
                break;

            case LlmEvent.Thinking think:
                AddThinkingTurn(think.text);
                break;

            case LlmEvent.ToolUse tu:
                AddToolTurn($"[tool_use] {tu.name}");
                break;

            case LlmEvent.ToolResult tr:
                AddToolTurn($"[tool_result] {(tr.isError ? "ERROR " : "")}{Truncate(tr.content, 400)}");
                break;

            case LlmEvent.RateLimitEvent rl:
                StatusText = $"rate-limit: {rl.status} (resets {rl.resetsAtUnix})";
                break;

            case LlmEvent.SessionEnd end:
                StatusText = $"turn 종료 — {end.durationMs}ms, ${end.costUsd:0.0000}, stop={end.stopReason}, denials={end.permissionDenialCount}";
                break;

            case LlmEvent.ProviderError err:
                AddErrorTurn($"[provider error] {err.message}");
                StatusText = err.message;
                break;
        }
    }

    /// <summary>
    /// Streaming assistant turn lazy-create. AssistantDelta / ProviderError / catch 의 ERROR 텍스트가
    /// _streamingTurn=null 상태에서 호출되더라도 새 assistant 버블을 생성한다.
    /// tool_use → tool_result → assistant 순서일 때 사이에 새 assistant 버블이 chronologically 삽입되도록 함.
    /// </summary>
    private void EnsureStreamingTurn()
    {
        if (_streamingTurn != null) return;
        _streamingTurn = new ChatTurn { Role = ChatTurn.Roles.Assistant, Text = "", IsStreaming = true };
        Turns.Add(_streamingTurn);
    }

    /// <summary>현재 streaming turn 의 throttle buffer flush + IsStreaming=false + null 화. 비어있으면 제거.</summary>
    private void EndStreamingTurn()
    {
        _assistantFlushTimer?.Stop();
        FlushAssistantBuffer();
        if (_streamingTurn == null) return;
        _streamingTurn.IsStreaming = false;
        if (string.IsNullOrEmpty(_streamingTurn.Text)) Turns.Remove(_streamingTurn);
        _streamingTurn = null;
    }

    /// <summary>Tool 관련 메시지를 별도 turn 으로 추가 (XAML 의 Role=tool DataTrigger 가 gray 적용).</summary>
    private void AddToolTurn(string text)
    {
        EndStreamingTurn();
        Turns.Add(new ChatTurn { Role = ChatTurn.Roles.Tool, Text = text });
    }

    /// <summary>Thinking block 을 별도 turn 으로 추가 (Role=thinking — 기본 어시스턴트 스타일 + 좌측 색띠).</summary>
    private void AddThinkingTurn(string text)
    {
        EndStreamingTurn();
        Turns.Add(new ChatTurn { Role = ChatTurn.Roles.Thinking, Text = text });
    }

    /// <summary>에러 메시지를 별도 turn 으로 추가 (Role=error — XAML DataTrigger 가 dark orange 적용).</summary>
    private void AddErrorTurn(string text)
    {
        EndStreamingTurn();
        Turns.Add(new ChatTurn { Role = ChatTurn.Roles.Error, Text = text });
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
}
