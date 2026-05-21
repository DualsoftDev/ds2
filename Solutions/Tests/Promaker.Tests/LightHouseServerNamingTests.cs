using Promaker.Knowledge;
using Promaker.LlmAgent;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// **D-S7-3b (s6-r30) — MCP server entry 이름 sanitize SSOT 회귀 시험**
/// (todo-lighthouse-kb-server.md §3.16.7 LightHouseServerNaming).
/// <para/>
/// 영숫자(ASCII) + 하이픈만 허용 + 연속 하이픈 압축 + trailing 하이픈 제거 + 빈 결과 시 ServiceId 첫 8자 fallback.
/// </summary>
public sealed class LightHouseServerNamingTests
{
    [Theory]
    [InlineData("hq", "lighthouse-hq")]
    [InlineData("Branch-Seoul", "lighthouse-Branch-Seoul")]
    [InlineData("Dev Box 2", "lighthouse-Dev-Box-2")]
    [InlineData("a-b-c", "lighthouse-a-b-c")]
    public void McpEntryName_basic_ascii_and_hyphen_and_whitespace(string displayName, string expected)
    {
        var svc = new LightHouseServiceConfig
        {
            ServiceId = "12345678-aaaa-bbbb-cccc-111111111111",
            DisplayName = displayName,
        };
        Assert.Equal(expected, LightHouseServerNaming.McpEntryName(svc));
    }

    [Theory]
    [InlineData("본사", "lighthouse-12345678")]      // 한글 전체 drop → fallback ServiceId[:8]
    [InlineData("    ", "lighthouse-12345678")]    // whitespace 만 → fallback
    [InlineData("@@@", "lighthouse-12345678")]     // 특수문자 drop → fallback
    [InlineData("", "lighthouse-12345678")]        // 빈 displayName → fallback
    public void McpEntryName_unicode_or_empty_falls_back_to_serviceId_prefix(string displayName, string expected)
    {
        var svc = new LightHouseServiceConfig
        {
            ServiceId = "12345678-aaaa-bbbb-cccc-111111111111",
            DisplayName = displayName,
        };
        Assert.Equal(expected, LightHouseServerNaming.McpEntryName(svc));
    }

    [Theory]
    [InlineData("a   b", "a-b")]              // 연속 whitespace → 1 하이픈
    [InlineData("a-b-", "a-b")]               // trailing 하이픈 제거
    [InlineData("--a--b--", "a-b")]           // leading + 연속 하이픈
    [InlineData("a한b영c", "abc")]            // unicode 가 silent drop, 하이픈 추가 X
    [InlineData("dev box (1)", "dev-box-1")]  // 괄호 drop, 공백 → 하이픈
    [InlineData("my_service", "my-service")]  // 자가 검열 m-7 — `_` 도 하이픈 치환 입력 (whitespace 동급)
    public void SanitizeDisplayName_internal_rules(string raw, string expected)
    {
        Assert.Equal(expected, LightHouseServerNaming.SanitizeDisplayName(raw));
    }

    [Fact]
    public void McpEntryName_short_serviceId_fallback_uses_full_serviceId()
    {
        // ServiceId 가 8 자 미만 (정상 path 에서 발생 안 함, 방어 대비) → 그대로 fallback.
        var svc = new LightHouseServiceConfig { ServiceId = "abcd", DisplayName = "" };
        Assert.Equal("lighthouse-abcd", LightHouseServerNaming.McpEntryName(svc));
    }

    [Fact]
    public void McpEntryName_empty_displayName_and_empty_serviceId_falls_back_to_unknown()
    {
        // 사용자가 EnsureActiveService 거치지 않고 비정상 state — fallback "unknown".
        var svc = new LightHouseServiceConfig { ServiceId = "", DisplayName = "" };
        Assert.Equal("lighthouse-unknown", LightHouseServerNaming.McpEntryName(svc));
    }
}
