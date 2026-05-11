using System;
using System.Collections.Generic;
using System.Linq;
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

// Round-trip 최적화 — doc: Apps/Promaker/Docs/todo-promaker-llm-roundtrip-optimization.md
// 본 파일 안의 `§C1` / `§5.2` 는 위 doc 의 섹션/이슈 ID.

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

    /// <summary>
    /// round-trip §H1 (review) — sticky snapshot. ApiChatProvider 는 매 turn `_history` 를 다시 보내는
    /// 구조라, snapshot 을 본문에서 분리한 후 revision 무변경 turn 에서는 LLM 이 snapshot 을 영영 못 본다.
    /// 해결: 마지막으로 받은 snapshot 을 본 field 에 보관 → 매 turn 호출 시 multi-content 의 stable prefix
    /// 로 prepend (cache_control 부착). 새 snapshot 도착 시 갱신, ClearSession 시 null reset.
    /// CLI provider 는 자체 session transcript 에 prompt 본문이 보존되므로 본 sticky 불요.
    /// </summary>
    private string? _stickySnapshot;

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
        _stickySnapshot = null;
    }

    /// <summary>API key 검증. CLI 가 아닌 API 라 EnsureCli 명칭은 인터페이스 호환용. 실 검증은 첫 호출 시 401 등으로 노출.</summary>
    public ClaudeCliVersion.Result EnsureCli()
    {
        var ok = _validate();
        return ok
            ? new ClaudeCliVersion.Result(true, $"{_providerLabel} API ready", _modelLabel)
            : new ClaudeCliVersion.Result(false, $"{_providerLabel} API key 미설정", _modelLabel);
    }

    // rev 4 (commit-2): `string prompt` → `LlmUserMessage msg`.
    // commit-6b: capability strict 모드 — 미지원 첨부 발견 시 invalidArg throw (silent drop 차단).
    // 정책 17: history 에는 text summary 만 누적 (bytes drop). 본 turn 호출 시에는 multi-content
    //   ChatMessage (TextContent + DataContent) 를 별도로 만들어 GetStreamingResponseAsync 에 전달.
    public IAsyncEnumerable<LlmEvent> Send(LlmUserMessage msg, CancellationToken cancellationToken)
    {
        LlmUserMessageOps.EnforceCapabilityOrFail(_capabilities, msg);
        return SendImpl(msg, cancellationToken);
    }

    private async IAsyncEnumerable<LlmEvent> SendImpl(
        LlmUserMessage msg,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 첫 turn 에서 tools 먼저 로드 → sessionId/history 확정. 순서가 중요:
        //   ListToolsAsync 가 throw/cancel 시 _sessionId/_history 가 남으면 다음 호출이 firstTurn=false 로 들어와
        //   _cachedTools=null 로 tool 호출 불가 상태가 silent 하게 굳음. 성공 후에만 state 확정.
        var firstTurn = _sessionId == null;
        if (firstTurn)
        {
            // 진단 로깅 (첫 RT 지연 원인 분리용) — MCP ListTools RPC 단독 소요. localhost Kestrel 이므로
            // 통상 수~수십 ms. 비정상적으로 크면 MCP server cold start / nonce 검증 / DI 비용 의심.
            var listToolsStartedAt = DateTime.UtcNow;
            var tools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var listToolsElapsedMs = (int)(DateTime.UtcNow - listToolsStartedAt).TotalMilliseconds;
            _cachedTools = new List<AITool>(tools);
            _sessionId = Guid.NewGuid().ToString("N");
            Log.Info($"[timing] firstTurn ListToolsAsync elapsedMs={listToolsElapsedMs} toolCount={_cachedTools.Count}");

            // round-trip §5.2 — Anthropic provider 만 system prompt 의 TextContent 에 cache_control: ephemeral 부착.
            // Anthropic SDK 가 Microsoft.Extensions.AI extension 으로 WithCacheControl 을 제공 — 다른 provider
            // (OpenAI/Groq/Ollama) 어댑터에서는 이 attribute 가 raw API body 에 전달되지 않으므로 nop (안전).
            // snapshot block 까지 별도 cache breakpoint 를 추가하려면 LlmUserMessage 가 multi-content 로 변경되어야
            // 하므로 본 단계에서는 system prompt + tool schema prefix 까지만 hit (deferred — todo Step 4 후속).
            AIContent systemContent = new TextContent(_systemPrompt);
            if (_providerLabel == ApiProviderFactory.AnthropicProviderLabel)
                systemContent = systemContent.WithCacheControl(new Anthropic.Models.Messages.CacheControlEphemeral());
            _history.Add(new ChatMessage(ChatRole.System, new List<AIContent> { systemContent }));

            var toolNames = new List<string>(_cachedTools.Count);
            foreach (var t in _cachedTools) toolNames.Add(t.Name);
            yield return LlmEvent.NewSessionStarted(
                _sessionId,
                _modelLabel,
                Microsoft.FSharp.Collections.ListModule.OfSeq(toolNames),
                Microsoft.FSharp.Collections.ListModule.Empty<McpServerStatus>());
        }

        // commit-6b — multi-content turn message 분리.
        //   - history 에 누적 = text-only (prompt + 첨부 summary metadata, 정책 17 bytes drop)
        //   - 본 turn 호출 = 동일 위치에 multi-content (TextContent + DataContent) 로 swap
        // 둘이 다른 ChatMessage 인스턴스. 다음 turn 의 history 에는 bytes 가 사라져 OOM/비용 폭증 회피.
        var nonTextAttachments = msg.Attachments != null
            ? msg.Attachments.Where(a => !a.IsTextFile).ToArray()
            : Array.Empty<Attachment>();

        var promptForHistory = msg.Text;
        if (nonTextAttachments.Length > 0)
        {
            var summaries = new System.Text.StringBuilder();
            foreach (var att in nonTextAttachments)
            {
                if (summaries.Length > 0) summaries.Append(' ');
                summaries.Append(AttachmentRendering.summarize(att));
            }
            promptForHistory = summaries.ToString() + "\n" + msg.Text;
        }
        // round-trip §C1 — snapshot 은 _history 에 누적하지 않음 (turn 마다 1.5~2.5K stale 토큰 영구 누적 차단).
        // 본 turn 호출 시점에만 turnUserMessage 의 multi-content 앞부분으로 분리해 prepend.
        // **trade-off (round-trip §n1)**: 다음 turn 의 LLM context 에는 직전 snapshot 이 사라지지만,
        // mutation 시 BumpRevision 으로 _lastSentRevision 무효화 → 자동 재첨부. revision 무변경 turn 은
        // doc §6.1 룰대로 직전 transcript 의 snapshot 을 LLM 이 그대로 사용 (history 안 user message 본문은
        // 남으므로 recall 가능, snapshot 만 별도 분리).
        _history.Add(new ChatMessage(ChatRole.User, promptForHistory));

        // round-trip §H1 — incoming snapshot 은 sticky 갱신만 하고, 매 turn 호출 시 _stickySnapshot 을 prepend.
        // revision 무변경 turn (incoming SnapshotPrefix 가 null) 에서도 직전 sticky 가 유지되어 LLM 이 store 상태 인지.
        var incomingSnapshot = msg.SnapshotPrefixOrNull;
        if (!string.IsNullOrEmpty(incomingSnapshot))
            _stickySnapshot = incomingSnapshot;
        var hasSnapshot = !string.IsNullOrEmpty(_stickySnapshot);
        var hasAttachments = nonTextAttachments.Length > 0;

        ChatMessage turnUserMessage;
        if (hasSnapshot || hasAttachments)
        {
            var contents = new List<AIContent>();
            if (hasSnapshot)
            {
                // round-trip §5.2 — snapshot block 끝에 cache_control: ephemeral (Anthropic only). system 의 부착과
                // 합쳐 2 breakpoint (4 breakpoint cap 안). 다른 provider 어댑터에서는 silently ignored.
                // sticky 라 매 turn 동일 내용 → Anthropic prompt cache 의 stable prefix hit. revision 변화 시점에만
                // miss + 새 cache 시작.
                AIContent snapshotContent = new TextContent(_stickySnapshot!);
                if (_providerLabel == ApiProviderFactory.AnthropicProviderLabel)
                    snapshotContent = snapshotContent.WithCacheControl(new Anthropic.Models.Messages.CacheControlEphemeral());
                contents.Add(snapshotContent);
            }
            contents.Add(new TextContent(promptForHistory));
            foreach (var att in nonTextAttachments)
            {
                var imgOpt = AttachmentInfo.tryGetImage(att);
                if (imgOpt != null)
                {
                    var img = imgOpt.Value;
                    contents.Add(new DataContent(img.Bytes, img.Mime) { Name = img.Name });
                    continue;
                }
                var pdfOpt = AttachmentInfo.tryGetPdf(att);
                if (pdfOpt != null)
                {
                    var pdf = pdfOpt.Value;
                    contents.Add(new DataContent(pdf.Bytes, "application/pdf") { Name = pdf.Name });
                }
            }
            turnUserMessage = new ChatMessage(ChatRole.User, contents);
        }
        else
        {
            turnUserMessage = _history[_history.Count - 1];
        }

        var options = new ChatOptions { Tools = _cachedTools, ModelId = _modelLabel };
        // 진단 timing 측정 — Stopwatch 가 DateTime.UtcNow 차이 대비 monotonic + 시스템 시계 변경/NTP 보정에 무관.
        // CliProcessHost.fs 의 timingSw 패턴과 통일.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var stopReason = "end_turn";
        var isError = false;

        // history 의 마지막 user message 를 turn 용 multi-content 버전으로 치환한 list 로 stream 호출.
        // round-trip §C1 — snapshot 또는 attachment 어느 쪽이든 multi-content turnUserMessage 가 만들어진 경우 모두 치환 필요.
        IList<ChatMessage> historyForStream;
        if (hasSnapshot || hasAttachments)
        {
            var copy = new List<ChatMessage>(_history);
            copy[copy.Count - 1] = turnUserMessage;
            historyForStream = copy;
        }
        else
        {
            historyForStream = _history;
        }

        var stream = _chatClient.GetStreamingResponseAsync(historyForStream, options, cancellationToken);
        var collected = new List<ChatResponseUpdate>();
        // 진단 로깅 — TTFB (time to first byte/chunk). 첫 chunk 도착까지가 prompt cache cold write +
        // 모델 prompt processing 시간의 합. firstTurn vs 후속 turn 비교 시 cache hit 효과 정량화.
        int? ttfbMs = null;

        // C1 fix — cancel/exception 시에도 partial collected 를 history 에 flush.
        // _history 는 line 162 에서 user message 가 이미 add 된 상태. 여기서 throw 시 finally 가
        // assistant/tool message 를 마저 추가하지 않으면 다음 turn 이 user→user 연속 메시지로
        // Anthropic API 400 (role alternation) reject. try/finally 로 role 정합성 보존.
        try
        {
            await foreach (var update in stream.ConfigureAwait(false))
            {
                if (ttfbMs == null)
                {
                    ttfbMs = (int)stopwatch.ElapsedMilliseconds;
                    Log.Info($"[timing] firstChunk(TTFB) ms={ttfbMs} firstTurn={firstTurn} hasSnapshot={hasSnapshot} snapshotLen={(_stickySnapshot?.Length ?? 0)} historyMsgs={historyForStream.Count}");
                }
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
        }
        finally
        {
            // collected 가 비어있으면 stream 시작 전 throw — assistant 메시지 부재. user message 만 history 에
            // 남으나, 다음 turn 의 _history.Add(user) 가 또 user 추가하면 alternation 깨짐. 이 경우 마지막
            // user message 를 도로 제거 (firstTurn 이면 system 도 보존되어야 하므로 user 만 pop).
            // line 162 의 user message add 직후이므로 _history 의 마지막은 항상 ChatRole.User.
            // collected 가 비어있거나 ToChatResponse() / Add 가 실패하면 마지막 user message 를 pop —
            // 그러지 않으면 다음 turn 의 user add 가 user→user 연속을 만들어 alternation 깨짐.
            var assistantAdded = false;
            if (collected.Count > 0)
            {
                try
                {
                    var partial = collected.ToChatResponse();
                    // 진단 로깅 — Anthropic usage 만 의미 있음. AdditionalCounts 의 cache_creation_input_tokens /
                    // cache_read_input_tokens 는 Anthropic SDK 어댑터 한정 키. OpenAI / Groq / Ollama
                    // 호출 시 로깅해도 키가 비거나 다른 의미라 noise. 첫 turn 에는 cache_creation 이 크고
                    // cache_read=0, 후속 turn 은 그 반대.
                    if (partial.Usage != null && _providerLabel == ApiProviderFactory.AnthropicProviderLabel)
                    {
                        var u = partial.Usage;
                        var addInfo = "(none)";
                        if (u.AdditionalCounts != null && u.AdditionalCounts.Count > 0)
                            addInfo = string.Join(", ", u.AdditionalCounts.Select(kv => $"{kv.Key}={kv.Value}"));
                        var totalMs = (int)stopwatch.ElapsedMilliseconds;
                        Log.Info($"[timing] usage input={u.InputTokenCount} output={u.OutputTokenCount} additional=[{addInfo}] firstTurn={firstTurn} ttfbMs={ttfbMs} totalMs={totalMs}");
                    }
                    foreach (var responseMsg in partial.Messages)
                        _history.Add(responseMsg);
                    assistantAdded = partial.Messages.Count > 0;
                }
                catch (Exception ex)
                {
                    Log.Warn("partial response history flush 실패 — user message pop 으로 alternation 보존", ex);
                }
            }
            if (!assistantAdded
                && _history.Count > 0
                && _history[_history.Count - 1].Role == ChatRole.User)
            {
                _history.RemoveAt(_history.Count - 1);
            }
        }

        var elapsedMs = (int)stopwatch.ElapsedMilliseconds;
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
