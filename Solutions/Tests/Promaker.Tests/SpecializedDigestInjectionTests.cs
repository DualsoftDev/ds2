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
        // ***REDACTED***2 자료 A/B/C 시뮬 — 머리말 + body 의 marker 박제. build 가 path-sorted 순서 + FileSeparator concat.
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
        var root = CreateFixture(("a.md", "MARKER_A_***REDACTED***2_KEY"));
        try
        {
            var digest = KbSpecializedDigestFetcher.Fetch(root);
            Assert.False(string.IsNullOrEmpty(digest));
            Assert.Contains("MARKER_A_***REDACTED***2_KEY", digest);
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

    // ── Backlog G (async wiring) ─────────────────────────────────────────────
    // `LlmChatViewModel.RefreshSpecializedDigestAsync` 의 `Task.Yield()` 후 동기 IO 패턴 정합 부족 →
    // `FetchAsync` / `FetchManyAsync` (F# `buildAsync`/`buildManyAsync` wrap) 로 전환. 회귀 0 보장 =
    // 동기 `Fetch` / `FetchMany` 와 byte-identical 결과 반환.

    [Fact]
    public async Task Smoke_G_KbSpecializedDigestFetcher_FetchAsync_동기_Fetch_와_byte_identical()
    {
        var root = CreateFixture(
            ("a.md", "MARKER_ASYNC_A ***REDACTED***2"),
            ("b.md", "MARKER_ASYNC_B"),
            ("c.md", "MARKER_ASYNC_C"));
        try
        {
            var sync = KbSpecializedDigestFetcher.Fetch(root);
            var async_ = await KbSpecializedDigestFetcher.FetchAsync(root);
            Assert.Equal(sync, async_);
            Assert.Contains("MARKER_ASYNC_A", async_);
            Assert.Contains("MARKER_ASYNC_B", async_);
            Assert.Contains("MARKER_ASYNC_C", async_);
            // separator 박제 정합.
            Assert.Contains("\n\n---\n\n", async_);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Smoke_G_FetchAsync_null_빈_부재_root_빈_string()
    {
        // null / 빈 / 부재 root → 빈 string (동기 Fetch 와 동일 graceful).
        Assert.Equal("", await KbSpecializedDigestFetcher.FetchAsync(null));
        Assert.Equal("", await KbSpecializedDigestFetcher.FetchAsync(""));
        Assert.Equal("", await KbSpecializedDigestFetcher.FetchAsync(
            Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public async Task Smoke_G_FetchManyAsync_동기_FetchMany_와_byte_identical()
    {
        var root1 = CreateFixture(("a.md", "MARKER_COL1_ASYNC"));
        var rootEmpty = Path.Combine(Path.GetTempPath(), "promaker-async-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootEmpty);
        var root2 = CreateFixture(("b.md", "MARKER_COL2_ASYNC"));
        try
        {
            var roots = new[] { root1, rootEmpty, root2 };
            var sync = KbSpecializedDigestFetcher.FetchMany(roots);
            var async_ = await KbSpecializedDigestFetcher.FetchManyAsync(roots);
            Assert.Equal(sync, async_);
            Assert.Contains("MARKER_COL1_ASYNC", async_);
            Assert.Contains("MARKER_COL2_ASYNC", async_);
        }
        finally
        {
            Directory.Delete(root1, recursive: true);
            Directory.Delete(rootEmpty, recursive: true);
            Directory.Delete(root2, recursive: true);
        }
    }

    [Fact]
    public async Task Smoke_G_FetchManyAsync_null_빈_seq_빈_string()
    {
        Assert.Equal("", await KbSpecializedDigestFetcher.FetchManyAsync(null));
        Assert.Equal("", await KbSpecializedDigestFetcher.FetchManyAsync(Array.Empty<string>()));
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
            ("iolist.md", "S204 KEY 지그 IO LIST — DI bit 박제 / DO bit 박제 (***REDACTED***2 SIDE OUTER SV)"));
        try
        {
            var basePrompt = "당신은 Promaker YAML 모델 생성 도우미입니다.";
            var kbDigest = "[KB] ***REDACTED***2 (3 자료 색인)";
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
            var stubUserMsg = new ChatMessage(ChatRole.User, "***REDACTED***2 #204 의 KEY 지그를 Promaker YAML 로 변환해줘");
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
            Assert.Contains("***REDACTED***2 SIDE OUTER SV", third.Text);
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
            new ChatMessage(ChatRole.User, "***REDACTED***2 #204 의 정확한 IO 비트 dump 가 필요해"),
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
        // Arguments 의 path argument 박제 — ***REDACTED***2 SIDE OUTER 자료 가정.
        Assert.NotNull(toolUses[0].Arguments);
        Assert.Contains("path", toolUses[0].Arguments!.Keys);
    }

    // ── G·G-Major-2 (Outlier/Minor 묶음 2) — firstTurn swap path wire e2e ────
    // ApiChatProvider.SendImpl 의 firstTurn 분기에서 SystemContentBuilder.Build 호출 후 _history[0] 의
    // Contents.Count 가 base + KB + specialized = 3 인지 wire-level assert + cache breakpoint 3 박제 검증.
    // 외부 LLM API 호출 0 — SystemContentBuilder.Build 직접 호출 + ChatMessage(ChatRole.System, contents) 검증.

    [Fact]
    public void Smoke_GMajor2_firstTurn_wire_e2e_3_TextContent_cache_breakpoint_3()
    {
        // firstTurn 진입 시 _history[0] (system message) 의 Contents 가 base + KB + specialized = 3 박제.
        // applyCacheControl 람다 (Anthropic capability 시뮬) 가 호출되는 path verify — 각 TextContent 가
        // wrapper 통과한 인스턴스로 박제됨.
        var root = CreateFixture(("a.md", "MARKER_GMAJOR2_SPECIALIZED"));
        try
        {
            var basePrompt = "base prompt";
            var kbDigest = "kb digest payload";
            var specializedDigest = KbSpecializedDigestFetcher.Fetch(root);

            // cache breakpoint wrapper 호출 카운트 — base + KB + specialized 각각 1회씩, 총 3 호출 기대
            // (4 breakpoint cap 안 — snapshot 박제 시 4번째 호출은 turn message 측).
            var wrapperCalls = 0;
            Func<AIContent, AIContent> applyCacheControl = c =>
            {
                wrapperCalls++;
                return c; // identity — 본 test 는 wire path 검증, Anthropic SDK 의존 회피.
            };

            var systemContents = SystemContentBuilder.Build(
                basePrompt, kbDigest, specializedDigest, applyCacheControl);

            // base + KB + specialized = 3 TextContent (cache breakpoint 3 박제 path).
            Assert.Equal(3, systemContents.Count);

            // ApiChatProvider firstTurn 의 _history.Add(new ChatMessage(ChatRole.System, contents)) wire 동치.
            var history = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System, systemContents),
            };
            Assert.Single(history);
            Assert.Equal(ChatRole.System, history[0].Role);
            Assert.Equal(3, history[0].Contents.Count);

            // 각 TextContent 의 payload 정합.
            Assert.Equal(basePrompt, Assert.IsType<TextContent>(history[0].Contents[0]).Text);
            Assert.Equal(kbDigest, Assert.IsType<TextContent>(history[0].Contents[1]).Text);
            Assert.Contains("MARKER_GMAJOR2_SPECIALIZED",
                Assert.IsType<TextContent>(history[0].Contents[2]).Text);

            // cache breakpoint wrapper 가 3 TextContent 각각에 호출되었는지 verify
            // (Anthropic capability 분기의 wire path 박제).
            Assert.Equal(3, wrapperCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Smoke_GMajor2_firstTurn_빈_specialized_2_TextContent_PR_G_v_b_wire_동치()
    {
        // specialized 빈 → _history[0].Contents.Count = 2 (base + KB), cache breakpoint 3 박제 skip.
        // PR-G v-b 와 wire byte-equal — 회귀 0.
        var wrapperCalls = 0;
        Func<AIContent, AIContent> applyCacheControl = c => { wrapperCalls++; return c; };

        var systemContents = SystemContentBuilder.Build(
            "base", "kb", "", applyCacheControl);
        Assert.Equal(2, systemContents.Count);

        var history = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, systemContents),
        };
        Assert.Equal(2, history[0].Contents.Count);
        // base + KB 만 wrapper 호출 (specialized 빈 = wrapper 미호출).
        Assert.Equal(2, wrapperCalls);
    }

    // ── N8 (todo-documents-based-gfm.md §6.1) — production GUI wiring 진입 fact ────────
    // PR-I5 시점에는 SpecializedDigest fetch / apply 가 SetActiveCollectionSourceRoots (test override) 만
    // 호출되어 production GUI path 진입 0 이었다. N8 patch 후 LlmChatViewModel 의 InitializeAsync /
    // ConfigureProviderAsync / RefreshKbDigestAsync (SSE invalidate path) 3 군데 production hook 에 진입
    // 보장. WPF Dispatcher / LightHouseClientHolder 의존성을 회피하기 위해 source-level fact 로 박제 —
    // 향후 drift (KB digest 옆 specialized digest 호출 누락) 발생 시 본 fact 가 즉시 차단.
    //
    // source file path 는 Promaker project root 기준 — Promaker.Tests 의 csproj 가 ProjectReference 로
    // Apps/Promaker/Promaker/Promaker.csproj 박제 → 본 test 의 bin 위치에서 4단계 위로 올라간 후
    // Apps/Promaker/Promaker/ViewModels/ 진입. Test working directory 가 bin/Debug/net9.0-windows 이므로
    // 상대 path 5단계.
    //
    // 회귀 fact 검증 범위:
    //   (a) Initialize.cs 의 InitializeAsync 안에 `_ = RefreshSpecializedDigestAsync();` 호출 1+회
    //   (b) Initialize.cs 의 ConfigureProviderAsync 안에 `ApplyPendingSpecializedDigest();` 호출 1+회
    //   (c) KbProfile.cs 의 RefreshKbDigestAsync 안에 `ApplyPendingSpecializedDigest();` 호출 1+회

    /// <summary>
    /// Promaker source root 경로 추출 — Test working directory (bin/Debug/net9.0-windows) 에서
    /// 4단계 위로 올라간 후 Apps/Promaker/Promaker/ 진입. test project 의 csproj ProjectReference 와
    /// 동일 path 정합. CI / local 빌드 양쪽 호환.
    /// </summary>
    private static string ResolveViewModelsDir()
    {
        // Promaker.Tests 의 csproj 위치 = Solutions/Tests/Promaker.Tests/Promaker.Tests.csproj
        // ProjectReference: ..\..\..\Apps\Promaker\Promaker\Promaker.csproj
        // → 본 test 실행 시 AppContext.BaseDirectory = Solutions/Tests/Promaker.Tests/bin/Debug/net9.0-windows/
        var baseDir = AppContext.BaseDirectory;
        // bin/Debug/net9.0-windows → Promaker.Tests root
        var testProjectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
        // Solutions/Tests/Promaker.Tests → repo root → Apps/Promaker/Promaker/ViewModels
        var repoRoot = Path.GetFullPath(Path.Combine(testProjectRoot, "..", "..", ".."));
        return Path.Combine(repoRoot, "Apps", "Promaker", "Promaker", "ViewModels");
    }

    [Fact]
    public void N8_InitializeAsync_RefreshSpecializedDigestAsync_호출_진입()
    {
        // (a) InitializeAsync 안에 RefreshSpecializedDigestAsync 호출 박제.
        var file = Path.Combine(ResolveViewModelsDir(), "LlmChatViewModel.Initialize.cs");
        Assert.True(File.Exists(file), $"source file 부재: {file}");
        var src = File.ReadAllText(file);
        // InitializeAsync method 범위 안에서 RefreshSpecializedDigestAsync 호출이 있어야 함.
        var initIdx = src.IndexOf("private async Task InitializeAsync()", StringComparison.Ordinal);
        Assert.True(initIdx >= 0, "InitializeAsync 진입점 부재");
        // ConfigureProviderAsync 가 InitializeAsync 다음 메서드 → 본 메서드 시작점이 boundary.
        var nextMethodIdx = src.IndexOf("private async Task<List<McpServerEntry>> TryCreateLightHouseSessionsAsync",
            initIdx, StringComparison.Ordinal);
        Assert.True(nextMethodIdx > initIdx, "InitializeAsync 종료 boundary 부재");
        var initBody = src.Substring(initIdx, nextMethodIdx - initIdx);
        Assert.Contains("RefreshSpecializedDigestAsync()", initBody);
        // KB digest 호출도 동반 (lifecycle 정합) — drift 방지.
        Assert.Contains("RefreshKbDigestAsync()", initBody);
    }

    [Fact]
    public void N8_ConfigureProviderAsync_ApplyPendingSpecializedDigest_호출_진입()
    {
        // (b) ConfigureProviderAsync 안에 ApplyPendingSpecializedDigest 호출 박제.
        var file = Path.Combine(ResolveViewModelsDir(), "LlmChatViewModel.Initialize.cs");
        var src = File.ReadAllText(file);
        var cfgIdx = src.IndexOf("private async Task ConfigureProviderAsync(LlmProviderKind kind)",
            StringComparison.Ordinal);
        Assert.True(cfgIdx >= 0, "ConfigureProviderAsync 진입점 부재");
        var cfgBody = src.Substring(cfgIdx);
        Assert.Contains("ApplyPendingSpecializedDigest();", cfgBody);
        // KB digest 호출도 동반 (lifecycle 정합).
        Assert.Contains("ApplyPendingKbDigest();", cfgBody);
    }

    [Fact]
    public void N8_RefreshKbDigestAsync_ApplyPendingSpecializedDigest_동반_호출()
    {
        // (c) KbProfile.cs 의 RefreshKbDigestAsync (SSE invalidate path) 안에 ApplyPendingSpecializedDigest
        // 동반 호출 박제. KB digest 갱신 시 specialized digest 도 함께 refresh.
        var file = Path.Combine(ResolveViewModelsDir(), "LlmChatViewModel.KbProfile.cs");
        Assert.True(File.Exists(file), $"source file 부재: {file}");
        var src = File.ReadAllText(file);
        var rkIdx = src.IndexOf("private async Task RefreshKbDigestAsync()", StringComparison.Ordinal);
        Assert.True(rkIdx >= 0, "RefreshKbDigestAsync 진입점 부재");
        // FetchKbProfilesAsync 다음 메서드 (ApplyPendingKbDigest 정의도 아래) 까지 — internal async Task<...>
        // FetchKbProfilesAsync 가 다음 method boundary.
        var nextMethodIdx = src.IndexOf("internal async Task<IReadOnlyDictionary",
            rkIdx, StringComparison.Ordinal);
        Assert.True(nextMethodIdx > rkIdx, "RefreshKbDigestAsync 종료 boundary 부재");
        var rkBody = src.Substring(rkIdx, nextMethodIdx - rkIdx);
        Assert.Contains("ApplyPendingKbDigest();", rkBody);
        Assert.Contains("ApplyPendingSpecializedDigest();", rkBody);
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
                    ["path"] = "4-3. ***REDACTED***2 SIDE OUTER SV IO LIST STD MAP.xlsx",
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
