using System;
using System.Collections.Generic;
using Microsoft.FSharp.Collections;
using Promaker.Services;
using Xunit;

namespace Promaker.Tests;

public class WizardSummaryBuilderTests
{
    [Fact]
    public void FormatSignalStats_CountsInputsAndOutputsCorrectly()
    {
        var entries = new List<IoListEntryDto>
        {
            new() { Direction = "Input"  },
            new() { Direction = "Input"  },
            new() { Direction = "Output" },
        };
        var text = WizardSummaryBuilder.FormatSignalStats(entries, dummyCount: 4, ioRowCount: 10);
        Assert.Contains("생성된 IO 신호: 10개", text);
        Assert.Contains("총 3개", text);
        Assert.Contains("Input 2개", text);
        Assert.Contains("Output 1개", text);
        Assert.Contains("Dummy 신호: 4개", text);
    }

    [Fact]
    public void FormatSignalStats_EmptyInputShowsZeros()
    {
        var text = WizardSummaryBuilder.FormatSignalStats(new List<IoListEntryDto>(), 0, 0);
        Assert.Contains("총 0개", text);
        Assert.Contains("Dummy 신호: 0개", text);
    }

    [Fact]
    public void FormatBindingStats_ShowsBoundAndFbTypeBreakdown()
    {
        var entries = new List<IoListEntryDto>
        {
            new() { TargetFBType = "CYL", TargetFBPort = "P1" },
            new() { TargetFBType = "CYL", TargetFBPort = "P2" },
            new() { TargetFBType = "STN", TargetFBPort = "P1" },
            new() { TargetFBType = "",    TargetFBPort = "" }, // unbound
        };
        var text = WizardSummaryBuilder.FormatBindingStats(entries);
        Assert.Contains("바인딩 완료: 3개", text);
        Assert.Contains("미바인딩: 1개", text);
        Assert.Contains("사용된 FB 타입: 2개", text);
        Assert.Contains("CYL: 2개", text);
        Assert.Contains("STN: 1개", text);
    }

    [Fact]
    public void FormatBindingStats_OmitsUnboundLineWhenAllBound()
    {
        var entries = new List<IoListEntryDto>
        {
            new() { TargetFBType = "CYL", TargetFBPort = "P1" },
        };
        var text = WizardSummaryBuilder.FormatBindingStats(entries);
        Assert.Contains("바인딩 완료: 1개", text);
        Assert.DoesNotContain("미바인딩", text);
    }

    [Fact]
    public void FormatCompletionStatus_AllPassShowsCheckmarks()
    {
        var text = WizardSummaryBuilder.FormatCompletionStatus(
            successCount: 5,
            validationErrors: new List<string>(),
            invalidCalls: NoInvalidCalls());
        Assert.Contains("✓ 패턴 5개 ApiCall 에 적용됨", text);
        Assert.Contains("✓ 모든 바인딩 검증 통과", text);
        Assert.Contains("✓ Call 구조 검증 통과", text);
    }

    [Fact]
    public void FormatCompletionStatus_NoPatternShowsInfoMarker()
    {
        var text = WizardSummaryBuilder.FormatCompletionStatus(
            successCount: 0,
            validationErrors: new List<string>(),
            invalidCalls: NoInvalidCalls());
        Assert.Contains("ℹ 패턴 미적용", text);
    }

    [Fact]
    public void FormatCompletionStatus_ValidationErrorsTruncatedToThree()
    {
        var errors = new List<string> { "err1", "err2", "err3", "err4", "err5" };
        var text = WizardSummaryBuilder.FormatCompletionStatus(1, errors, NoInvalidCalls());
        Assert.Contains("검증 경고 5건", text);
        Assert.Contains("err1", text);
        Assert.Contains("err3", text);
        Assert.DoesNotContain("err4", text);
    }

    [Fact]
    public void FormatCompletionStatus_InvalidCallsTruncatedToFiveWithSuffix()
    {
        var invalid = new List<Tuple<Guid, FSharpList<string>>>();
        for (var i = 0; i < 7; i++)
        {
            var reasons = ListModule.OfSeq(new[] { $"reason{i}" });
            invalid.Add(Tuple.Create(Guid.NewGuid(), reasons));
        }
        var text = WizardSummaryBuilder.FormatCompletionStatus(1, new List<string>(), invalid);
        Assert.Contains("🚨 Call 구조 위반 7건", text);
        Assert.Contains("reason0", text);
        Assert.Contains("reason4", text);
        Assert.Contains("외 2건", text);
        Assert.DoesNotContain("reason5", text);
    }

    private static List<Tuple<Guid, FSharpList<string>>> NoInvalidCalls() => new();
}
