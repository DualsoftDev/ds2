using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Ds2.LlmAgent;
using log4net;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Promaker.LlmAgent.Api;

/// <summary>
/// API 기반 ILlmProvider 구현체 (Anthropic / OpenAI / Ollama 공통).
///
/// **핵심 설계** — Microsoft.Extensions.AI 의 IChatClient 를 공통 추상으로 사용.
///   - Anthropic SDK 의 `client.AsIChatClient(model)` 어댑터
///   - OpenAI SDK + `Microsoft.Extensions.AI.OpenAI` 의 `chatClient.AsIChatClient()` 어댑터
///   - OllamaSharp 의 `OllamaApiClient` (직접 IChatClient 구현)
/// 모두 같은 IChatClient 인터페이스로 단일 ApiChatProvider 가 처리.
///
/// **Multi-turn loop** — `.AsBuilder().UseFunctionInvocation().Build()` 미들웨어가 자동 처리.
///   ChatResponseUpdate 가 FunctionCall 이면 자동으로 IMcpClient 의 tool 호출 → FunctionResult 메시지 push →
///   LLM 재호출. CLI provider 의 `--resume FSM` 과 달리 in-process 에서 multi-turn 이 완결.
///
/// **Tool 노출** — McpClient.CreateAsync(HttpClientTransport) 로 자기 자신의 Promaker McpHostService 에
/// HTTP connect → ListToolsAsync() → AIFunction[] → ChatOptions.Tools 로 전달.
/// nonce 헤더는 HttpClient 의 DefaultRequestHeaders 로 부착.
///
/// **Streaming → LlmEvent** — GetStreamingResponseAsync 의 ChatResponseUpdate.Contents 를 type 별로 분기:
///   - TextContent → AssistantDelta
///   - FunctionCallContent → ToolUse
///   - FunctionResultContent → ToolResult
///   - UsageContent → SessionEnd
/// </summary>
public sealed class ApiChatProvider : ILlmProvider, IAsyncDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ApiChatProvider));

    // tool_result payload 직렬화 옵션. 기본 JsonSerializerOptions 는 JavaScriptEncoder.Default (ASCII-safe)
    // 라 한국어 / 일본어 / emoji 등 non-ASCII 가 \uXXXX 로 escape 되어 LLM Chat panel 에 raw escape 형태로 노출.
    // UnsafeRelaxedJsonEscaping 은 한글 등을 그대로 유지 (HTML inject 위험은 plain text TextBlock 표시라 무관).
    // 부수 효과로 token 사용량 감소 (UTF-8 1글자 ≈ 1.5 token vs \uXXXX = 6 token) → 작은 free tier TPM 한도 완화.
    private static readonly JsonSerializerOptions ToolResultJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IChatClient _chatClient;
    private readonly McpClient _mcpClient;
    private readonly HttpClient _mcpHttp;
    private readonly string _providerLabel;
    private readonly string _modelLabel;
    private readonly string _systemPrompt;
    private readonly Func<bool> _validate;
    private readonly Capabilities _capabilities;

    private readonly List<ChatMessage> _history = new();
    private string? _sessionId;
    private IList<AITool>? _cachedTools;

    public ApiChatProvider(
        IChatClient chatClient,
        McpClient mcpClient,
        HttpClient mcpHttp,
        string providerLabel,
        string modelLabel,
        string systemPrompt,
        Func<bool> validate,
        Capabilities capabilities)
    {
        _chatClient = chatClient;
        _mcpClient = mcpClient;
        _mcpHttp = mcpHttp;
        _providerLabel = providerLabel;
        _modelLabel = modelLabel;
        _systemPrompt = systemPrompt;
        _validate = validate;
        _capabilities = capabilities;
    }

    public Capabilities Capabilities => _capabilities;

    public Microsoft.FSharp.Core.FSharpOption<string> SessionId =>
        _sessionId == null
            ? Microsoft.FSharp.Core.FSharpOption<string>.None
            : Microsoft.FSharp.Core.FSharpOption<string>.Some(_sessionId);

    public void ClearSession()
    {
        _history.Clear();
        _sessionId = null;
    }

    /// <summary>API key 검증. CLI 가 아닌 API 라 EnsureCli 명칭은 인터페이스 호환용. 실 검증은 첫 호출 시 401 등으로 노출.</summary>
    public ClaudeCliVersion.Result EnsureCli()
    {
        var ok = _validate();
        return ok
            ? new ClaudeCliVersion.Result(true, $"{_providerLabel} API ready", _modelLabel)
            : new ClaudeCliVersion.Result(false, $"{_providerLabel} API key 미설정", _modelLabel);
    }

    // rev 4 (commit-2): `string prompt` → `LlmUserMessage msg`. 현 단계 Attachments 무시 / msg.Text 만 사용.
    public IAsyncEnumerable<LlmEvent> Send(LlmUserMessage msg, CancellationToken cancellationToken)
        => SendImpl(msg.Text, cancellationToken);

    private async IAsyncEnumerable<LlmEvent> SendImpl(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 첫 turn 에서 tools 먼저 로드 → sessionId/history 확정. 순서가 중요:
        //   ListToolsAsync 가 throw/cancel 시 _sessionId/_history 가 남으면 다음 호출이 firstTurn=false 로 들어와
        //   _cachedTools=null 로 tool 호출 불가 상태가 silent 하게 굳음. 성공 후에만 state 확정.
        var firstTurn = _sessionId == null;
        if (firstTurn)
        {
            var tools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            _cachedTools = new List<AITool>(tools);
            _sessionId = Guid.NewGuid().ToString("N");
            _history.Add(new ChatMessage(ChatRole.System, _systemPrompt));

            var toolNames = new List<string>(_cachedTools.Count);
            foreach (var t in _cachedTools) toolNames.Add(t.Name);
            yield return LlmEvent.NewSessionStarted(
                _sessionId,
                _modelLabel,
                Microsoft.FSharp.Collections.ListModule.OfSeq(toolNames),
                Microsoft.FSharp.Collections.ListModule.Empty<McpServerStatus>());
        }

        _history.Add(new ChatMessage(ChatRole.User, prompt));

        var options = new ChatOptions { Tools = _cachedTools, ModelId = _modelLabel };
        var startedAt = DateTime.UtcNow;

        var stopReason = "end_turn";
        var isError = false;

        var stream = _chatClient.GetStreamingResponseAsync(_history, options, cancellationToken);
        var collected = new List<ChatResponseUpdate>();
        await foreach (var update in stream.ConfigureAwait(false))
        {
            collected.Add(update);

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        yield return LlmEvent.NewAssistantDelta(text.Text);
                        break;

                    case FunctionCallContent call:
                        var argsJson = call.Arguments != null
                            ? JsonSerializer.SerializeToElement(call.Arguments)
                            : JsonSerializer.SerializeToElement(new { });
                        yield return LlmEvent.NewToolUse(call.CallId ?? "", call.Name, argsJson);
                        break;

                    case FunctionResultContent result:
                        var (isErr, payload) = ExtractToolResult(result);
                        yield return LlmEvent.NewToolResult(result.CallId ?? "", isErr, payload);
                        if (isErr) isError = true;
                        break;
                }
            }

            if (update.FinishReason.HasValue)
                stopReason = update.FinishReason.Value.ToString();
        }

        // collected updates 를 history 에 단일 ChatResponse 로 통합 (다음 turn 의 multi-turn context).
        var response = collected.ToChatResponse();
        foreach (var msg in response.Messages)
            _history.Add(msg);

        var elapsedMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
        yield return LlmEvent.NewSessionEnd(elapsedMs, 0m, isError, stopReason, 0);
    }

    private static (bool isError, string content) ExtractToolResult(FunctionResultContent result)
    {
        if (result.Result is null) return (false, "");
        if (result.Result is string s) return (false, s);
        try
        {
            return (false, JsonSerializer.Serialize(result.Result, ToolResultJsonOptions));
        }
        catch (Exception ex)
        {
            return (true, ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_chatClient is IAsyncDisposable a) await a.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn("IChatClient DisposeAsync 실패", ex); }
        try { await _mcpClient.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn("McpClient DisposeAsync 실패", ex); }
        _mcpHttp.Dispose();
    }
}
