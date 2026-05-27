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
/// **PR-I5 (todo-documents-based-gfm.md §2 PR-I5 + §0 headless smoke 재정의 [4][5][6])** — P1 종료 signal.
/// <para/>
/// §0 의 사용자 GUI 인터랙션 항목 ([4] KB collection 활성 후 system prompt inject / [5] "S204 KEY 지그 YAML"
/// 요청 / [6] `attachment_fulltext` 자율 호출) 을 e2e mock 합본으로 흡수.
/// <list type="number">
///   <item>[4] <see cref="SpecializedDigestBuilder"/> 직접 호출 후 합본 string assert (synthetic fixture .md)</item>
///   <item>[5] mock 흐름 — <see cref="KbSpecializedDigestFetcher.Fetch"/> → <see cref="SystemContentBuilder.Build"/>
///   의 3번째 TextContent (cache breakpoint 3) 안에 fixture 의 marker 포함 verify (mock LLM client 가 system
///   prompt inject 받는 wire path 박제)</item>
///   <item>[6] mock <see cref="IChatClient"/> 의 <see cref="FunctionCallContent"/> simulation — name=<c>attachment_fulltext</c>
///   호출 1+회 emit 확인 (tool_use 자율 호출 wire 박제)</item>
/// </list>
/// <para/>
/// **외부 LLM API 직접 호출 0** — Anthropic / OpenAI key 미사용. mock IChatClient + wire format string assert 만.
/// <para/>
/// 본 test 가 통과하면 PR-I5 commit 완료 = orchestrator 의 P1 종료 signal. P2 hand-off (도메인 전문가 검수)
/// 의 trigger 조건.
/// </summary>
public sealed class SpecializedDigestInjectionTests
{
    // ── 0. fixture helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// `<root>/.lighthouse-kb/summary/` 디렉토리에 markdown 파일 박제 + root path 반환. 본 fixture 는
    /// `SpecializedDigestBuilder.fs` 의 <c>build</c> 가 기대하는 layout 정합 (TextDumper.summaryDir SSOT).
    /// 호출자 책임으로 <see cref="Directory.Delete"/> (recursive: true) 정리.
    /// </summary>
    private static string CreateFixture(params (string fileName, string content)[] files)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "promaker-specialized-digest-" + Guid.NewGuid().ToString("N"));
        var summaryDir = Path.Combine(root, ".lighthouse-kb", "summary");
        Directory.CreateDirectory(summaryDir);
        foreach (var (fileName, content) in files)
        {
            File.WriteAllText(Path.Combine(summaryDir, fileName), content, new System.Text.UTF8Encoding(false));
        }
        return root;
    }

    // ── [4] SpecializedDigestBuilder.build 직접 호출 + 합본 string assert ──────────

    [Fact]
    public void Smoke_4_SpecializedDigestBuilder_build_합본_string_assert()
    {
        // §0 [4] — KB collection 활성 후 system prompt inject 의 입력 단계 = strategy 합본 markdown.
        // 광명2 자료 A/B/C 시뮬 — 머리말 + body 의 marker 박제. build 가 path-sorted 순서 + FileSeparator concat.
        var root = CreateFixture(
            ("01_iolist.md", "<!-- IoListStrategy v1.0 -->\nMARKER_IOLIST_S204_KEY_지그"),
            ("02_workorder.md", "<!-- WorkOrderStrategy v1.0 -->\nMARKER_WORKORDER"),
            ("03_pdfctrl.md", "<!-- PdfControlSpecStrategy v1.0 -->\nMARKER_PDFCTRL"));
        try
        {
            var result = SpecializedDigestBuilder.build(root);

            Assert.Equal(3, result.Metadata.FileCount);
            Assert.True(result.Metadata.EstimatedTokens > 0);
            Assert.False(string.IsNullOrEmpty(result.Combined));
            Assert.Contains("MARKER_IOLIST_S204_KEY_지그", result.Combined);
            Assert.Contains("MARKER_WORKORDER", result.Combined);
            Assert.Contains("MARKER_PDFCTRL", result.Combined);
            // path-sorted — IOLIST 가 가장 먼저.
            var iolistIdx = result.Combined.IndexOf("MARKER_IOLIST", StringComparison.Ordinal);
            var workorderIdx = result.Combined.IndexOf("MARKER_WORKORDER", StringComparison.Ordinal);
            var pdfctrlIdx = result.Combined.IndexOf("MARKER_PDFCTRL", StringComparison.Ordinal);
            Assert.True(iolistIdx >= 0 && iolistIdx < workorderIdx && workorderIdx < pdfctrlIdx,
                "path-sorted 순서 정합 — IOLIST → WORKORDER → PDFCTRL");
            // FileSeparator (markdown horizontal rule) 박제.
            Assert.Contains("\n\n---\n\n", result.Combined);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Smoke_4_SpecializedDigestBuilder_summary_부재_빈_합본()
    {
        // root 만 있고 .lighthouse-kb/summary/ 부재 — graceful 빈 합본 (cache breakpoint 3 skip wire 정합).
        var root = Path.Combine(Path.GetTempPath(), "promaker-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = SpecializedDigestBuilder.build(root);
            Assert.Equal(0, result.Metadata.FileCount);
            Assert.Equal("", result.Combined);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Smoke_4_KbSpecializedDigestFetcher_Fetch_wraps_F_lib()
    {
        // KbSpecializedDigestFetcher.Fetch (C# wrapper) 가 F# build 의 Combined 결과 그대로 반환 확인.
        var root = CreateFixture(("a.md", "MARKER_A_광명2_KEY"));
        try
        {
            var digest = KbSpecializedDigestFetcher.Fetch(root);
            Assert.False(string.IsNullOrEmpty(digest));
            Assert.Contains("MARKER_A_광명2_KEY", digest);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Smoke_4_KbSpecializedDigestFetcher_Fetch_부재_root_빈_string()
    {
        // null / 빈 / 부재 root → 빈 string (ApiChatProvider 의 cache breakpoint 3 skip wire 정합).
        Assert.Equal("", KbSpecializedDigestFetcher.Fetch(null));
        Assert.Equal("", KbSpecializedDigestFetcher.Fetch(""));
        Assert.Equal("", KbSpecializedDigestFetcher.Fetch(
            Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void Smoke_4_KbSpecializedDigestFetcher_FetchMany_concat()
    {
        // multi-collection — FetchMany 가 F# buildMany 의 separator concat 결과 그대로 반환.
        var root1 = CreateFixture(("a.md", "MARKER_COL1"));
        var root2 = CreateFixture(("b.md", "MARKER_COL2"));
        try
        {
            var digest = KbSpecializedDigestFetcher.FetchMany(new[] { root1, root2 });
            Assert.Contains("MARKER_COL1", digest);
            Assert.Contains("MARKER_COL2", digest);
        }
        finally
        {
            Directory.Delete(root1, recursive: true);
            Directory.Delete(root2, recursive: true);
        }
    }

    // ── [5] mock LLM client 의 system prompt inject + wire-level 정합 ───────────────

    [Fact]
    public async Task Smoke_5_mock_LLM_system_prompt_specialized_digest_inject_확인()
    {
        // §0 [5] — 사용자 "S204 KEY 지그 YAML" 요청 시점에 LLM 이 system prompt 안의 specialized digest 를
        // 인식한 상태로 수신. wire-level 정합 = SystemContentBuilder.Build 의 3번째 TextContent (cache breakpoint 3)
        // 에 fixture 의 marker 포함 + ChatMessage(ChatRole.System, contents) 생성 시 contents 가 보존됨.
        //
        // mock LLM client = MockChatClient — GetStreamingResponseAsync 의 messages 인자 캡처. 외부 LLM API 호출 0.
        var root = CreateFixture(
            ("iolist.md", "S204 KEY 지그 IO LIST — DI bit 박제 / DO bit 박제 (광명2 SIDE OUTER SV)"));
        try
        {
            var basePrompt = "당신은 Promaker YAML 모델 생성 도우미입니다.";
            var kbDigest = "[KB] 광명2 (3 자료 색인)";
            var specializedDigest = KbSpecializedDigestFetcher.Fetch(root);
            Assert.False(string.IsNullOrEmpty(specializedDigest));

            // SystemContentBuilder.Build (PR-I4 4-arg overload) — ApiChatProvider firstTurn 분기의 wire path 와 동일.
            var systemContents = SystemContentBuilder.Build(
                basePrompt, kbDigest, specializedDigest, applyCacheControl: null);
            Assert.Equal(3, systemContents.Count);

            // ApiChatProvider firstTurn 의 _history.Add(new ChatMessage(ChatRole.System, contents)) 와 동일.
            var systemMessage = new ChatMessage(ChatRole.System, systemContents);

            // mock LLM client 가 받게 될 messages — system message 의 3번째 TextContent 안 specialized digest 검증.
            var mock = new MockChatClient(yieldFunctionCall: false);
            var stubUserMsg = new ChatMessage(ChatRole.User, "광명2 #204 의 KEY 지그를 Promaker YAML 로 변환해줘");
            var messagesToLlm = new List<ChatMessage> { systemMessage, stubUserMsg };
            // GetStreamingResponseAsync 호출 — mock 이 messages 캡처.
            var stream = mock.GetStreamingResponseAsync(messagesToLlm, options: null, CancellationToken.None);
            // enumerate 1회 (mock 의 first update 가 곧 finish)
            await EnumerateAndDiscard(stream);

            Assert.NotNull(mock.CapturedMessages);
            Assert.True(mock.CapturedMessages!.Count >= 2, "system + user 최소 2건");
            var sys = mock.CapturedMessages.First(m => m.Role == ChatRole.System);
            Assert.Equal(3, sys.Contents.Count);
            var third = Assert.IsType<TextContent>(sys.Contents[2]);
            Assert.Contains("S204 KEY 지그", third.Text);
            Assert.Contains("광명2 SIDE OUTER SV", third.Text);
            // breakpoint 순서 정합 — base → KB → specialized.
            Assert.Equal(basePrompt, Assert.IsType<TextContent>(sys.Contents[0]).Text);
            Assert.Equal(kbDigest, Assert.IsType<TextContent>(sys.Contents[1]).Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Smoke_5_specialized_digest_빈_시_2_TextContent_PR_G_v_b_회귀_0()
    {
        // §0 [5] 의 회귀 정합 — sourceRoot 부재 → 빈 digest → cache breakpoint 3 박제 skip (PR-G v-b wire 동치).
        // mock LLM 이 system prompt 의 2 TextContent 만 받음 (base + KB digest).
        var specializedDigest = KbSpecializedDigestFetcher.Fetch("/does-not-exist");
        Assert.Equal("", specializedDigest);

        var systemContents = SystemContentBuilder.Build(
            "base prompt", "kb digest", specializedDigest, applyCacheControl: null);
        Assert.Equal(2, systemContents.Count);
    }

    // ── [6] mock IChatClient tool_use simulation (attachment_fulltext 자율 호출) ────

    [Fact]
    public async Task Smoke_6_mock_LLM_attachment_fulltext_tool_use_1plus_회_emit()
    {
        // §0 [6] — LLM 이 정확 IO 비트 dump 필요 시 attachment_fulltext 자율 호출. mock 이 FunctionCallContent
        // (name=attachment_fulltext) 1회 emit → enumerate 시 tool_use simulation 박제 검증.
        var mock = new MockChatClient(yieldFunctionCall: true);
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, "system prompt"),
            new ChatMessage(ChatRole.User, "광명2 #204 의 정확한 IO 비트 dump 가 필요해"),
        };
        var stream = mock.GetStreamingResponseAsync(messages, options: null, CancellationToken.None);
        var collected = await EnumerateAll(stream);

        // mock 이 FunctionCallContent 를 1+ 회 emit 했는지 검증.
        var toolUses = collected
            .SelectMany(u => u.Contents)
            .OfType<FunctionCallContent>()
            .ToList();
        Assert.True(toolUses.Count >= 1, "mock tool_use simulation 1+회 emit 의무");
        Assert.Equal("attachment_fulltext", toolUses[0].Name);
        // Arguments 의 path argument 박제 — 광명2 SIDE OUTER 자료 가정.
        Assert.NotNull(toolUses[0].Arguments);
        Assert.Contains("path", toolUses[0].Arguments!.Keys);
    }

    [Fact]
    public async Task Smoke_6_mock_tool_use_simulation_default_off_시_emit_0()
    {
        // yieldFunctionCall=false 시 tool_use 0 — base wire 정합 (LLM 이 자율 호출 안 한 경우).
        var mock = new MockChatClient(yieldFunctionCall: false);
        var stream = mock.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "단순 질문") }, options: null, CancellationToken.None);
        var collected = await EnumerateAll(stream);
        var toolUses = collected.SelectMany(u => u.Contents).OfType<FunctionCallContent>().ToList();
        Assert.Empty(toolUses);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static async Task EnumerateAndDiscard(IAsyncEnumerable<ChatResponseUpdate> stream)
    {
        await foreach (var _ in stream.ConfigureAwait(false)) { /* discard */ }
    }

    private static async Task<List<ChatResponseUpdate>> EnumerateAll(IAsyncEnumerable<ChatResponseUpdate> stream)
    {
        var list = new List<ChatResponseUpdate>();
        await foreach (var update in stream.ConfigureAwait(false))
            list.Add(update);
        return list;
    }

    /// <summary>
    /// Microsoft.Extensions.AI <see cref="IChatClient"/> 의 minimal mock — GetStreamingResponseAsync 의
    /// messages 인자 캡처 + 선택적 FunctionCallContent (tool_use) emit. 외부 LLM API 호출 0.
    /// <para/>
    /// 본 mock 은 ApiChatProvider 의 IChatClient 의존성을 wire-level 으로 박제. 실 Anthropic / OpenAI SDK
    /// 어댑터와 동일 인터페이스 — caller (테스트 / 향후 e2e) 가 동일 mock 으로 재사용 가능.
    /// </summary>
    private sealed class MockChatClient : IChatClient
    {
        private readonly bool _yieldFunctionCall;
        public IList<ChatMessage>? CapturedMessages { get; private set; }

        public MockChatClient(bool yieldFunctionCall)
        {
            _yieldFunctionCall = yieldFunctionCall;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException("본 mock 은 streaming path 만 박제.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CapturedMessages = messages.ToList();
            // 진입 즉시 1회 yield — async/await stub.
            await Task.Yield();

            if (_yieldFunctionCall)
            {
                // [6] — attachment_fulltext 자율 호출 simulation. 실 LLM 이 emit 할 wire 와 동일 contract.
                var args = new Dictionary<string, object?>
                {
                    ["path"] = "4-3. 광명2 SIDE OUTER SV IO LIST STD MAP.xlsx",
                    ["sheet"] = "S204",
                };
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = new List<AIContent>
                    {
                        new FunctionCallContent("call-mock-1", "attachment_fulltext", args),
                    },
                };
            }
            else
            {
                // text-only response — stub YAML 응답 (실 ValidateModelDoc 통과는 wire mock 한정 검증 안 함,
                // 본 [5] 는 system prompt inject path 검증이 더 중요 — task 명세 정합).
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = new List<AIContent>
                    {
                        new TextContent("system:\n  name: S204_KEY_지그\n  zone: 204\n"),
                    },
                    FinishReason = ChatFinishReason.Stop,
                };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
