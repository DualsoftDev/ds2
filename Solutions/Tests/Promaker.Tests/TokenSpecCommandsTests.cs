using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;
using Promaker.Dialogs;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

public sealed class TokenSpecCommandsTests
{
    [Fact]
    public void BuildTokenSpecSourceWorks_deduplicates_reference_works_without_reference_suffix()
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");
        var systemId = store.AddSystem("S", projectId, true);
        var flowId = store.AddFlow("F", systemId);
        var workId = store.AddWork("W1", flowId);
        var referenceWorkId = store.AddReferenceWork(workId);

        store.UpdateWorkTokenRole(referenceWorkId, TokenRole.Source);

        var method = typeof(MainViewModel).GetMethod(
            "BuildTokenSpecSourceWorks",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var items = (List<WorkOption>)method.Invoke(null, [store])!;

        var item = Assert.Single(items);
        var originalWork = Queries.getWork(workId, store)!.Value;

        Assert.Equal(workId, item.Id);
        Assert.Equal(originalWork.Name, item.Name);
        Assert.DoesNotContain("#", item.Name);
    }

    [Fact]
    public void NormalizeTokenSpecsForDialog_canonicalizes_reference_work_links()
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");
        var systemId = store.AddSystem("S", projectId, true);
        var flowId = store.AddFlow("F", systemId);
        var workId = store.AddWork("W1", flowId);
        var referenceWorkId = store.AddReferenceWork(workId);

        var specs = new[]
        {
            new TokenSpec(
                1,
                "SpecA",
                Microsoft.FSharp.Collections.MapModule.Empty<string, string>(),
                FSharpOption<Guid>.Some(referenceWorkId))
        };

        var method = typeof(MainViewModel).GetMethod(
            "NormalizeTokenSpecsForDialog",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var normalized = (List<TokenSpec>)method.Invoke(null, [store, specs])!;

        var normalizedSpec = Assert.Single(normalized);
        Assert.Equal(workId, normalizedSpec.WorkId?.Value);
    }
}
