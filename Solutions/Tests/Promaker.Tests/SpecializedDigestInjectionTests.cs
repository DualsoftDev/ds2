using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Ds2.LightHouse;
using Llm.Shared.Api;
using Microsoft.Extensions.AI;
using Promaker.Knowledge;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// **specialized digest(RAG layer E) 주입 wire 회귀 박제**.
/// <para/>
/// **2026-06-01 — 로컬 read → MCP fetch 전환**: 구 PR-I5 는 <c>KbSpecializedDigestFetcher</c> 가 로컬
/// <c>.lighthouse-kb/summary/*.md</c> 를 직접 read 했으나, 로컬에 summary/ 가 없는 원격 service 를 지원하지
/// 못했다. MCP <c>attachment_summary(includeSpecialized=true)</c> fetch 로 전환 (로컬/원격 모두 지원). 본 test:
/// <list type="number">
///   <item>F# lib <see cref="SpecializedDigestBuilder"/>.<c>build</c> 합본 — server 측 attachment_summary 가 재사용하는 SSOT</item>
///   <item>MCP fetcher <see cref="KbSpecializedDigestFetcher.FetchManyViaMcpAsync"/> graceful 경계 (빈 셋 / null → "")</item>
///   <item><see cref="SystemContentBuilder.Build"/> 의 cache breakpoint 3 (system prompt 3번째 TextContent) wire</item>
///   <item>mock <see cref="IChatClient"/> 의 system prompt inject + tool_use simulation</item>
///   <item>production GUI wiring source-fact (Initialize / ConfigureProvider / RefreshKbDigest 진입점)</item>
/// </list>
/// 외부 LLM API 호출 0 — mock IChatClient + wire format string assert 만.
/// </summary>
public sealed class SpecializedDigestInjectionTests
{
    // ── 0. fixture helper (lib build 검증용) ──────────────────────────────────────

    private static string CreateFixture(params (string fileName, string content)[] files)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "promaker-specialized-digest-" + Guid.NewGuid().ToString("N"));
        var summaryDir = Path.Combine(root, ".lighthouse-kb", "summary");
        Directory.CreateDirectory(summaryDir);
        foreach (var (fileName, content) in files)
            File.WriteAllText(Path.Combine(summaryDir, fileName), content, new System.Text.UTF8Encoding(false));
        return root;
    }

    // ── [1] SpecializedDigestBuilder.build (F# lib — server attachment_summary 가 재사용) ──

    [Fact]
    public void SpecializedDigestBuilder_build_합본_path_sorted_separator()
    {
        // server 의 attachment_summary(includeSpecialized=true) 가 호출하는 lib SSOT. path-sorted + FileSeparator concat.
        var root = CreateFixture(
            ("01_iolist.md", "<!-- IoListStrategy v1.0 -->\nMARKER_IOLIST_S204_KEY_지그"),
            ("02_workorder.md", "<!-- WorkOrderStrategy v1.0 -->\nMARKER_WORKORDER"),
            ("03_pdfctrl.md", "<!-- PdfControlSpecStrategy v1.0 -->\nMARKER_PDFCTRL"));
        try
        {
            var result = SpecializedDigestBuilder.build(root);
            Assert.Equal(3, result.Metadata.FileCount);
            Assert.True(result.Metadata.EstimatedTokens > 0);
            Assert.Contains("MARKER_IOLIST_S204_KEY_지그", result.Combined);
            Assert.Contains("MARKER_WORKORDER", result.Combined);
            Assert.Contains("MARKER_PDFCTRL", result.Combined);
            var iolistIdx = result.Combined.IndexOf("MARKER_IOLIST", StringComparison.Ordinal);
            var workorderIdx = result.Combined.IndexOf("MARKER_WORKORDER", StringComparison.Ordinal);
            var pdfctrlIdx = result.Combined.IndexOf("MARKER_PDFCTRL", StringComparison.Ordinal);
            Assert.True(iolistIdx >= 0 && iolistIdx < workorderIdx && workorderIdx < pdfctrlIdx,
                "path-sorted 순서 정합 — IOLIST → WORKORDER → PDFCTRL");
            Assert.Contains("\n\n---\n\n", result.Combined);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SpecializedDigestBuilder_build_summary_부재_빈_합본()
    {
        var root = Path.Combine(Path.GetTempPath(), "promaker-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = SpecializedDigestBuilder.build(root);
            Assert.Equal(0, result.Metadata.FileCount);
            Assert.Equal("", result.Combined);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── [2] KbSpecializedDigestFetcher MCP fetch — graceful 경계 (client 없이 unit) ──

    [Fact]
    public async Task FetchManyViaMcp_빈_셋_빈_string()
    {
        // active 셋 부재 → MCP 호출 0 → 빈 digest (cache breakpoint 3 skip = 회귀 0).
        var empty = new Dictionary<string, IReadOnlyList<string>>();
        Assert.Equal("", await KbSpecializedDigestFetcher.FetchManyViaMcpAsync(empty));
    }

    [Fact]
    public async Task FetchManyViaMcp_null_빈_string()
    {
        Assert.Equal("", await KbSpecializedDigestFetcher.FetchManyViaMcpAsync(null!));
    }

    // ── [3] SystemContentBuilder cache breakpoint 3 wire ──────────────────────────

    [Fact]
    public void SystemContentBuilder_specialized_digest_3rd_TextContent()
    {
        // MCP attachment_summary(includeSpecialized=true) 응답(모사)이 system prompt 3번째 TextContent 로 박제.
        var basePrompt = "당신은 Promaker YAML 모델 생성 도우미입니다.";
        var kbDigest = "[KB] ***REDACTED***2 (3 자료 색인)";
        var specializedDigest = "S204 KEY 지그 IO LIST — DI bit 박제 / DO bit 박제 (***REDACTED***2 SIDE OUTER SV)";

        var systemContents = SystemContentBuilder.Build(
            basePrompt, kbDigest, specializedDigest, applyCacheControl: null);
        Assert.Equal(3, systemContents.Count);
        Assert.Equal(basePrompt, Assert.IsType<TextContent>(systemContents[0]).Text);
        Assert.Equal(kbDigest, Assert.IsType<TextContent>(systemContents[1]).Text);
        var third = Assert.IsType<TextContent>(systemContents[2]);
        Assert.Contains("S204 KEY 지그", third.Text);
        Assert.Contains("***REDACTED***2 SIDE OUTER SV", third.Text);
    }

    [Fact]
    public void SystemContentBuilder_빈_specialized_2_TextContent_회귀_0()
    {
        // 빈 active 셋 → 빈 digest → cache breakpoint 3 박제 skip (PR-G v-b wire 동치).
        var systemContents = SystemContentBuilder.Build(
            "base prompt", "kb digest", "", applyCacheControl: null);
        Assert.Equal(2, systemContents.Count);
    }

    [Fact]
    public void SystemContentBuilder_firstTurn_wire_cache_breakpoint_wrapper_3회()
    {
        // firstTurn 진입 시 _history[0] (system message) Contents = base + KB + specialized = 3.
        // applyCacheControl wrapper 가 3 TextContent 각각에 호출 (Anthropic capability 분기 wire).
        var basePrompt = "base prompt";
        var kbDigest = "kb digest payload";
        var specializedDigest = "MARKER_SPECIALIZED_***REDACTED***2";

        var wrapperCalls = 0;
        Func<AIContent, AIContent> applyCacheControl = c => { wrapperCalls++; return c; };

        var systemContents = SystemContentBuilder.Build(
            basePrompt, kbDigest, specializedDigest, applyCacheControl);
        Assert.Equal(3, systemContents.Count);

        var history = new List<ChatMessage> { new ChatMessage(ChatRole.System, systemContents) };
        Assert.Single(history);
        Assert.Equal(ChatRole.System, history[0].Role);
        Assert.Equal(3, history[0].Contents.Count);
        Assert.Equal(basePrompt, Assert.IsType<TextContent>(history[0].Contents[0]).Text);
        Assert.Equal(kbDigest, Assert.IsType<TextContent>(history[0].Contents[1]).Text);
        Assert.Contains("MARKER_SPECIALIZED_***REDACTED***2", Assert.IsType<TextContent>(history[0].Contents[2]).Text);
        Assert.Equal(3, wrapperCalls);
    }

    [Fact]
    public void SystemContentBuilder_빈_specialized_wrapper_2회()
    {
        var wrapperCalls = 0;
        Func<AIContent, AIContent> applyCacheControl = c => { wrapperCalls++; return c; };
        var systemContents = SystemContentBuilder.Build("base", "kb", "", applyCacheControl);
        Assert.Equal(2, systemContents.Count);
        Assert.Equal(2, wrapperCalls);
    }

    // ── [4] mock LLM system prompt inject + tool_use simulation ───────────────────

    [Fact]
    public async Task mock_LLM_system_prompt_specialized_digest_inject()
    {
        var basePrompt = "당신은 Promaker YAML 모델 생성 도우미입니다.";
        var kbDigest = "[KB] ***REDACTED***2";
        var specializedDigest = "S204 KEY 지그 IO LIST (***REDACTED***2 SIDE OUTER SV)";
        var systemContents = SystemContentBuilder.Build(basePrompt, kbDigest, specializedDigest, applyCacheControl: null);
        var systemMessage = new ChatMessage(ChatRole.System, systemContents);

        var mock = new MockChatClient(yieldFunctionCall: false);
        var stubUserMsg = new ChatMessage(ChatRole.User, "***REDACTED***2 #204 의 KEY 지그를 Promaker YAML 로 변환해줘");
        var messagesToLlm = new List<ChatMessage> { systemMessage, stubUserMsg };
        var stream = mock.GetStreamingResponseAsync(messagesToLlm, options: null, CancellationToken.None);
        await EnumerateAndDiscard(stream);

        Assert.NotNull(mock.CapturedMessages);
        var sys = mock.CapturedMessages!.First(m => m.Role == ChatRole.System);
        Assert.Equal(3, sys.Contents.Count);
        var third = Assert.IsType<TextContent>(sys.Contents[2]);
        Assert.Contains("S204 KEY 지그", third.Text);
    }

    [Fact]
    public async Task mock_LLM_attachment_fulltext_tool_use_emit()
    {
        var mock = new MockChatClient(yieldFunctionCall: true);
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, "system prompt"),
            new ChatMessage(ChatRole.User, "***REDACTED***2 #204 의 정확한 IO 비트 dump 가 필요해"),
        };
        var stream = mock.GetStreamingResponseAsync(messages, options: null, CancellationToken.None);
        var collected = await EnumerateAll(stream);
        var toolUses = collected.SelectMany(u => u.Contents).OfType<FunctionCallContent>().ToList();
        Assert.True(toolUses.Count >= 1, "mock tool_use simulation 1+회 emit 의무");
        Assert.Equal("attachment_fulltext", toolUses[0].Name);
        Assert.NotNull(toolUses[0].Arguments);
        Assert.Contains("path", toolUses[0].Arguments!.Keys);
    }

    [Fact]
    public async Task mock_tool_use_default_off_emit_0()
    {
        var mock = new MockChatClient(yieldFunctionCall: false);
        var stream = mock.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "단순 질문") }, options: null, CancellationToken.None);
        var collected = await EnumerateAll(stream);
        Assert.Empty(collected.SelectMany(u => u.Contents).OfType<FunctionCallContent>());
    }

    // ── [5] production GUI wiring source-fact (MCP 전환 후 진입점) ─────────────────
    // specialized digest fetch 가 production 진입점에 연결됐는지 source-level 박제 — drift (KB digest 옆
    // specialized fetch 호출 누락) 발생 시 즉시 차단. WPF Dispatcher / LightHouseClientHolder 의존 회피.

    private static string ResolveViewModelsDir([CallerFilePath] string sourceFile = "")
    {
        if (!string.IsNullOrEmpty(sourceFile))
        {
            var testProjectDir = Path.GetDirectoryName(sourceFile);
            if (!string.IsNullOrEmpty(testProjectDir))
            {
                var repoRoot = Path.GetFullPath(Path.Combine(testProjectDir, "..", "..", ".."));
                var fromSource = Path.Combine(repoRoot, "Apps", "Promaker", "Promaker", "ViewModels");
                if (Directory.Exists(fromSource)) return fromSource;
            }
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(Path.GetFullPath(start));
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "Apps", "Promaker", "Promaker", "ViewModels");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }

        var fallbackRepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        return Path.Combine(fallbackRepoRoot, "Apps", "Promaker", "Promaker", "ViewModels");
    }

    [Fact]
    public void Wiring_InitializeAsync_PrimeKbProfile_before_provider()
    {
        // InitializeAsync primes the KB profile cache before provider creation; ConfigureProviderAsync then applies
        // that cache to the new provider before IsReady=true.
        var file = Path.Combine(ResolveViewModelsDir(), "LlmChatViewModel.Initialize.cs");
        Assert.True(File.Exists(file), $"source file 부재: {file}");
        var src = File.ReadAllText(file);
        var initIdx = src.IndexOf("private async Task InitializeAsync()", StringComparison.Ordinal);
        Assert.True(initIdx >= 0, "InitializeAsync 진입점 부재");
        var nextMethodIdx = src.IndexOf("private async Task TryCreateLightHouseSessionsAsync", initIdx, StringComparison.Ordinal);
        Assert.True(nextMethodIdx > initIdx, "InitializeAsync 종료 boundary 부재");
        var initBody = src.Substring(initIdx, nextMethodIdx - initIdx);
        var primeIdx = initBody.IndexOf("PrimeInitialKbProfileCacheAsync()", StringComparison.Ordinal);
        var configureIdx = initBody.IndexOf("ConfigureProviderAsync(SelectedProvider)", StringComparison.Ordinal);
        Assert.True(primeIdx >= 0, "InitializeAsync KB profile prime 진입점 부재");
        Assert.True(configureIdx > primeIdx, "KB profile prime 은 provider 구성 전에 끝나야 함");
    }

    [Fact]
    public void Wiring_ConfigureProviderAsync_RefreshSpecializedDigest_진입()
    {
        var file = Path.Combine(ResolveViewModelsDir(), "LlmChatViewModel.Initialize.cs");
        var src = File.ReadAllText(file);
        var cfgIdx = src.IndexOf("private async Task ConfigureProviderAsync(LlmProviderKind kind)", StringComparison.Ordinal);
        Assert.True(cfgIdx >= 0, "ConfigureProviderAsync 진입점 부재");
        var cfgBody = src.Substring(cfgIdx);
        var refreshIdx = cfgBody.IndexOf("await RefreshSpecializedDigestAsync(provider).ConfigureAwait(true);", StringComparison.Ordinal);
        var readyIdx = cfgBody.IndexOf("IsReady = true;", StringComparison.Ordinal);
        Assert.True(refreshIdx >= 0, "ConfigureProviderAsync layer E 동기 fetch 진입점 부재");
        Assert.True(readyIdx > refreshIdx, "layer E summary fetch 는 IsReady=true 전에 끝나야 함");
        Assert.Contains("ApplyPendingKbDigest();", cfgBody);
    }

    [Fact]
    public void Wiring_RefreshKbDigestAsync_RefreshSpecializedDigest_동반()
    {
        var file = Path.Combine(ResolveViewModelsDir(), "LlmChatViewModel.KbProfile.cs");
        Assert.True(File.Exists(file), $"source file 부재: {file}");
        var src = File.ReadAllText(file);
        var rkIdx = src.IndexOf("private async Task RefreshKbDigestAsync()", StringComparison.Ordinal);
        Assert.True(rkIdx >= 0, "RefreshKbDigestAsync 진입점 부재");
        var nextMethodIdx = src.IndexOf("private async Task PrimeInitialKbProfileCacheAsync()", rkIdx, StringComparison.Ordinal);
        Assert.True(nextMethodIdx > rkIdx, "RefreshKbDigestAsync 종료 boundary 부재");
        var rkBody = src.Substring(rkIdx, nextMethodIdx - rkIdx);
        Assert.Contains("ApplyPendingKbDigest();", rkBody);
        Assert.Contains("RefreshSpecializedDigestAsync()", rkBody);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static async Task EnumerateAndDiscard(IAsyncEnumerable<ChatResponseUpdate> stream)
    {
        await foreach (var _ in stream.ConfigureAwait(false)) { /* discard */ }
    }

    private static async Task<List<ChatResponseUpdate>> EnumerateAll(IAsyncEnumerable<ChatResponseUpdate> stream)
    {
        var list = new List<ChatResponseUpdate>();
        await foreach (var update in stream.ConfigureAwait(false)) list.Add(update);
        return list;
    }

    /// <summary>
    /// Microsoft.Extensions.AI <see cref="IChatClient"/> 의 minimal mock — GetStreamingResponseAsync 의 messages
    /// 인자 캡처 + 선택적 FunctionCallContent (tool_use) emit. 외부 LLM API 호출 0.
    /// </summary>
    private sealed class MockChatClient : IChatClient
    {
        private readonly bool _yieldFunctionCall;
        public IList<ChatMessage>? CapturedMessages { get; private set; }
        public MockChatClient(bool yieldFunctionCall) => _yieldFunctionCall = yieldFunctionCall;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("본 mock 은 streaming path 만 박제.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CapturedMessages = messages.ToList();
            await Task.Yield();
            if (_yieldFunctionCall)
            {
                var args = new Dictionary<string, object?>
                {
                    ["path"] = "4-3. ***REDACTED***2 SIDE OUTER SV IO LIST STD MAP.xlsx",
                    ["sheet"] = "S204",
                };
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = new List<AIContent> { new FunctionCallContent("call-mock-1", "attachment_fulltext", args) },
                };
            }
            else
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = new List<AIContent> { new TextContent("system:\n  name: S204_KEY_지그\n") },
                    FinishReason = ChatFinishReason.Stop,
                };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
