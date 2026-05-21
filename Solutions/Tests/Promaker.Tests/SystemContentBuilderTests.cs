using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using Promaker.LlmAgent.Api;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// **PR-G (todo-lighthouse-index-summary.md §5.2 v-b)** — SystemContentBuilder 단위 시험.
///
/// scope: base + (optional) digest 분리 박제 + cache_control 람다 호출 검증.
/// </summary>
public sealed class SystemContentBuilderTests
{
    [Fact]
    public void Build_빈_digest_1_TextContent_회귀_0()
    {
        // digest 빈 시 base 1 TextContent 만 — 기존 단일 prompt 와 동치 (회귀 0 보장).
        var contents = SystemContentBuilder.Build("base prompt", "", applyCacheControl: null);
        Assert.Single(contents);
        Assert.Equal("base prompt", Assert.IsType<TextContent>(contents[0]).Text);
    }

    [Fact]
    public void Build_null_digest_1_TextContent()
    {
        var contents = SystemContentBuilder.Build("base", null, applyCacheControl: null);
        Assert.Single(contents);
    }

    [Fact]
    public void Build_digest_박제_시_base_와_digest_분리_2_TextContent()
    {
        // PR-G v-b — Anthropic prompt cache 의 breakpoint 분리 위해 base + digest 가 *별 TextContent*.
        var contents = SystemContentBuilder.Build("base prompt", "kb digest", applyCacheControl: null);
        Assert.Equal(2, contents.Count);
        Assert.Equal("base prompt", Assert.IsType<TextContent>(contents[0]).Text);
        Assert.Equal("kb digest", Assert.IsType<TextContent>(contents[1]).Text);
    }

    [Fact]
    public void Build_cacheControl_람다_각_TextContent_별도_호출()
    {
        // PR-G v-b — Anthropic 호환 wire 한정 람다가 각 TextContent 에 *개별* 호출되어야 cache breakpoint 가 둘.
        // 한 번만 호출되면 base + digest 가 같은 cache prefix 로 묶여 KB 변경 시 base 영역까지 invalidate.
        var calls = new List<AIContent>();
        Func<AIContent, AIContent> applyCacheControl = c =>
        {
            calls.Add(c);
            return c;
        };

        var contents = SystemContentBuilder.Build("base", "digest", applyCacheControl);
        Assert.Equal(2, calls.Count);
        Assert.Equal(2, contents.Count);
        // 첫 호출 = base TextContent, 두 번째 = digest TextContent.
        Assert.Equal("base", Assert.IsType<TextContent>(calls[0]).Text);
        Assert.Equal("digest", Assert.IsType<TextContent>(calls[1]).Text);
    }

    [Fact]
    public void Build_빈_digest_cacheControl_base_만_호출()
    {
        // digest 빈 시 cacheControl 람다는 base 1회만 — digest 영역 cache 박제 안 함.
        var calls = 0;
        var contents = SystemContentBuilder.Build("base", "", c => { calls++; return c; });
        Assert.Equal(1, calls);
        Assert.Single(contents);
    }
}
