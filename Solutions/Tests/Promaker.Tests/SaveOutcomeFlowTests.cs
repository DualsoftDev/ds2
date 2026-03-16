using Microsoft.FSharp.Core;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class SaveOutcomeFlowTests
{
    [Fact]
    public void MermaidSave_returns_false_and_skips_success_on_error()
    {
        string? warned = null;
        var successCalled = false;

        var saved = SaveOutcomeFlow.TryCompleteMermaidSave(
            FSharpResult<Unit, string>.NewError("save failed"),
            message => warned = message,
            () => successCalled = true);

        Assert.False(saved);
        Assert.Equal("save failed", warned);
        Assert.False(successCalled);
    }

    [Fact]
    public void AasxSave_returns_false_and_skips_success_when_export_fails()
    {
        string? warned = null;
        var successCalled = false;

        var saved = SaveOutcomeFlow.TryCompleteAasxSave(
            false,
            message => warned = message,
            "no project",
            () => successCalled = true);

        Assert.False(saved);
        Assert.Equal("no project", warned);
        Assert.False(successCalled);
    }
}
