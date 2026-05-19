using System.Collections.Generic;
using Promaker.Knowledge;
using Promaker.LlmAgent;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// **D-S7-3c (s6-r31) — LightHouseServiceValidator SSOT 회귀 시험**
/// (todo-lighthouse-kb-server.md §3.16.8).
/// </summary>
public sealed class LightHouseServiceValidatorTests
{
    [Fact]
    public void Empty_list_is_valid()
    {
        var (ok, reason) = LightHouseServiceValidator.ValidateDisplayNames(new List<LightHouseServiceConfig>());
        Assert.True(ok);
        Assert.Null(reason);
    }

    [Fact]
    public void Single_service_with_name_is_valid()
    {
        var svcs = new List<LightHouseServiceConfig>
        {
            new() { ServiceId = "a", DisplayName = "본사" },
        };
        var (ok, reason) = LightHouseServiceValidator.ValidateDisplayNames(svcs);
        Assert.True(ok);
        Assert.Null(reason);
    }

    [Fact]
    public void Empty_displayName_is_invalid()
    {
        var svcs = new List<LightHouseServiceConfig>
        {
            new() { ServiceId = "a", DisplayName = "" },
        };
        var (ok, reason) = LightHouseServiceValidator.ValidateDisplayNames(svcs);
        Assert.False(ok);
        Assert.Equal("(빈 값)", reason);
    }

    [Fact]
    public void Whitespace_only_displayName_is_invalid()
    {
        var svcs = new List<LightHouseServiceConfig>
        {
            new() { ServiceId = "a", DisplayName = "   " },
        };
        var (ok, reason) = LightHouseServiceValidator.ValidateDisplayNames(svcs);
        Assert.False(ok);
        Assert.Equal("(빈 값)", reason);
    }

    [Fact]
    public void Duplicate_displayName_is_invalid()
    {
        var svcs = new List<LightHouseServiceConfig>
        {
            new() { ServiceId = "a", DisplayName = "본사" },
            new() { ServiceId = "b", DisplayName = "본사" },
        };
        var (ok, reason) = LightHouseServiceValidator.ValidateDisplayNames(svcs);
        Assert.False(ok);
        Assert.Equal("본사", reason);
    }

    [Fact]
    public void Duplicate_after_trim_is_invalid()
    {
        // trim 후 비교 → "본사" + " 본사 " 가 충돌.
        var svcs = new List<LightHouseServiceConfig>
        {
            new() { ServiceId = "a", DisplayName = "본사" },
            new() { ServiceId = "b", DisplayName = " 본사 " },
        };
        var (ok, reason) = LightHouseServiceValidator.ValidateDisplayNames(svcs);
        Assert.False(ok);
        Assert.Equal("본사", reason);
    }

    [Fact]
    public void Case_sensitive_distinct_is_valid()
    {
        // 대소문자 구분 — "HQ" 와 "hq" 는 다른 entry 로 허용 (의도된 strict 정합).
        var svcs = new List<LightHouseServiceConfig>
        {
            new() { ServiceId = "a", DisplayName = "HQ" },
            new() { ServiceId = "b", DisplayName = "hq" },
        };
        var (ok, _) = LightHouseServiceValidator.ValidateDisplayNames(svcs);
        Assert.True(ok);
    }
}
