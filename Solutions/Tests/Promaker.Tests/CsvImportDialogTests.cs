using System;
using System.Reflection;
using Ds2.CSV;
using Microsoft.FSharp.Collections;
using Promaker.Dialogs;
using Xunit;

namespace Promaker.Tests;

public sealed class CsvImportDialogTests
{
    [Fact]
    public void BuildSyntheticWarningText_returns_expected_text()
    {
        var method = typeof(CsvImportDialog).GetMethod(
            "BuildSyntheticWarningText",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var text = (string)method.Invoke(null, [3])!;

        Assert.Contains("3개", text, StringComparison.Ordinal);
        Assert.Contains("Signal_<addr>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPreviewSummary_includes_preview_cap_notice()
    {
        var method = typeof(CsvImportDialog).GetMethod(
            "BuildPreviewSummary",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var preview = new CsvImportPreview(
            ListModule.OfSeq(new[] { "FlowA" }),
            ListModule.OfSeq(new[] { "WorkA" }),
            ListModule.OfSeq(Array.Empty<string>()),
            ListModule.OfSeq(new[] { "CallA" }),
            0);

        var text = (string)method.Invoke(null, [preview, 101])!;

        Assert.Contains("FlowA", text, StringComparison.Ordinal);
        Assert.Contains("101개", text, StringComparison.Ordinal);
    }
}
