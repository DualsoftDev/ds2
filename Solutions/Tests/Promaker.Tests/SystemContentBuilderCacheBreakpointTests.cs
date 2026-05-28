using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using Llm.Shared.Api;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// **PR-I4 (todo-documents-based-gfm.md §2 PR-I4 + documents-based-gfm.md §8.3)** —
/// <see cref="SystemContentBuilder"/> 의 cache breakpoint 3 (specialized digest) 회귀.
///
/// scope: base (#1) + KB digest (#2) + specialized digest (#3) 의 *분리 TextContent* 박제 + 순서 정합 +
/// applyCacheControl 람다 각 TextContent 별 호출 (= 각각 cache_control: ephemeral). 외부 LLM API 호출 0 —
/// wire format string assert 만 (mock 도 불필요, builder 단위).
///
/// 회귀 보호 항목:
///   - cache breakpoint 위치 순서 (base → KB → specialized) — wire 순서가 cache prefix-match 결정
///   - specialized digest 빈/null → 박제 skip (PR-G v-b 회귀 0, 2-TextContent 또는 1-TextContent)
///   - applyCacheControl 람다 호출 횟수 = 박제된 TextContent 수와 일치 (각 breakpoint 가 ephemeral)
///   - 2-arg overload (PR-G v-b) 와 4-arg overload (PR-I4) 둘 다 backward-compatible.
/// </summary>
public sealed class SystemContentBuilderCacheBreakpointTests
{
    // ── 1. specialized digest 박제 시 3 TextContent (cache breakpoint 3) ─────────────

    [Fact]
    public void Build_specialized_digest_박제_시_3_TextContent_순서_정합()
    {
        var contents = SystemContentBuilder.Build(
            "base prompt",
            "kb digest",
            "specialized digest",
            applyCacheControl: null);

        Assert.Equal(3, contents.Count);
        Assert.Equal("base prompt", Assert.IsType<TextContent>(contents[0]).Text);
        Assert.Equal("kb digest", Assert.IsType<TextContent>(contents[1]).Text);
        Assert.Equal("specialized digest", Assert.IsType<TextContent>(contents[2]).Text);
    }

    [Fact]
    public void Build_specialized_digest_빈_string_박제_skip()
    {
        // PR-G v-b 회귀 — specialized digest 빈 시 2 TextContent (base + KB).
        var contents = SystemContentBuilder.Build(
            "base", "kb digest", specializedDigest: "", applyCacheControl: null);
        Assert.Equal(2, contents.Count);
        Assert.Equal("base", Assert.IsType<TextContent>(contents[0]).Text);
        Assert.Equal("kb digest", Assert.IsType<TextContent>(contents[1]).Text);
    }

    [Fact]
    public void Build_specialized_digest_null_박제_skip()
    {
        var contents = SystemContentBuilder.Build(
            "base", "kb", specializedDigest: null, applyCacheControl: null);
        Assert.Equal(2, contents.Count);
    }

    [Fact]
    public void Build_KB_digest_빈_specialized_digest_박제_2_TextContent()
    {
        // KB digest 가 collection 토글 OFF 라도 specialized markdown 은 색인된 collection 만 보면 되므로 독립.
        // 결과 = base + specialized (KB digest skip). cache breakpoint 1 + 3 (중간 2 skip).
        var contents = SystemContentBuilder.Build(
            "base", kbDigest: "", specializedDigest: "specialized", applyCacheControl: null);
        Assert.Equal(2, contents.Count);
        Assert.Equal("base", Assert.IsType<TextContent>(contents[0]).Text);
        Assert.Equal("specialized", Assert.IsType<TextContent>(contents[1]).Text);
    }

    [Fact]
    public void Build_모두_빈_1_TextContent()
    {
        var contents = SystemContentBuilder.Build(
            "base", kbDigest: "", specializedDigest: "", applyCacheControl: null);
        Assert.Single(contents);
        Assert.Equal("base", Assert.IsType<TextContent>(contents[0]).Text);
    }

    // ── 2. applyCacheControl 람다 호출 = 각 TextContent 별 (cache_control: ephemeral 박제) ─

    [Fact]
    public void Build_cacheControl_람다_3_TextContent_각_별도_호출()
    {
        // PR-I4 — cache breakpoint 3 박제 시 람다가 base + KB + specialized 각각 별도 호출.
        // 한 번만 호출되면 3 영역이 같은 cache prefix 로 묶여 specialized 갱신 시 base 영역까지 invalidate.
        var calls = new List<AIContent>();
        Func<AIContent, AIContent> applyCacheControl = c =>
        {
            calls.Add(c);
            return c;
        };

        var contents = SystemContentBuilder.Build(
            "base", "kb", "specialized", applyCacheControl);

        Assert.Equal(3, calls.Count);
        Assert.Equal(3, contents.Count);
        // 호출 순서 = 박제 순서 = base → KB → specialized.
        Assert.Equal("base", Assert.IsType<TextContent>(calls[0]).Text);
        Assert.Equal("kb", Assert.IsType<TextContent>(calls[1]).Text);
        Assert.Equal("specialized", Assert.IsType<TextContent>(calls[2]).Text);
    }

    [Fact]
    public void Build_cacheControl_specialized_빈_2회_호출()
    {
        var calls = 0;
        var contents = SystemContentBuilder.Build(
            "base", "kb", specializedDigest: "", applyCacheControl: c => { calls++; return c; });
        Assert.Equal(2, calls);
        Assert.Equal(2, contents.Count);
    }

    // ── 3. backward compatibility (PR-G v-b 2-arg overload 회귀 0) ───────────────────

    [Fact]
    public void Build_2arg_overload_PR_G_v_b_회귀_0()
    {
        // 기존 PR-G v-b 의 3-arg Build (basePrompt, kbDigest, applyCacheControl) overload 가
        // 내부적으로 specializedDigest=null 로 forward — wire format 동치.
        var contents = SystemContentBuilder.Build("base", "kb", applyCacheControl: null);
        Assert.Equal(2, contents.Count);
        Assert.Equal("base", Assert.IsType<TextContent>(contents[0]).Text);
        Assert.Equal("kb", Assert.IsType<TextContent>(contents[1]).Text);
    }

    [Fact]
    public void Build_2arg_overload_빈_digest_1_TextContent()
    {
        var contents = SystemContentBuilder.Build("base", "", applyCacheControl: null);
        Assert.Single(contents);
    }

    // ── 4. wire format 박제 — TextContent type 정합 (Anthropic SDK 의 cache_control 변환 전 단계) ─

    [Fact]
    public void Build_모든_breakpoint_TextContent_type_정합()
    {
        // ApiChatProvider 의 WithCacheControl(CacheControlEphemeral) extension 은 TextContent 등 AIContent
        // 에만 적용 가능. 박제 type 이 TextContent 가 아니면 Anthropic SDK adapter 가 cache_control 변환 skip.
        var contents = SystemContentBuilder.Build("base", "kb", "specialized", applyCacheControl: null);
        Assert.All(contents, c => Assert.IsType<TextContent>(c));
    }

    [Fact]
    public void Build_basePrompt_null_specialized_과_함께_정규화()
    {
        // **review m-6** 회귀 — null basePrompt 도 안전 (`?? ""` 정규화). specialized digest 박제 시에도 NRE 0.
        var contents = SystemContentBuilder.Build(
            basePrompt: null!, "kb", "specialized", applyCacheControl: null);
        Assert.Equal(3, contents.Count);
        Assert.Equal("", Assert.IsType<TextContent>(contents[0]).Text);
    }
}
