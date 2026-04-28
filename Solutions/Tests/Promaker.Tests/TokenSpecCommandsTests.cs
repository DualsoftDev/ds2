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

    [Fact]
    public void BuildTokenSpecPickerWorks_includes_all_works_with_source_flag()
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");
        var systemId = store.AddSystem("S", projectId, true);
        var flowId = store.AddFlow("F", systemId);
        var sourceWorkId = store.AddWork("Src", flowId);
        var plainWorkId = store.AddWork("Plain", flowId);
        var refWorkId = store.AddReferenceWork(plainWorkId);

        store.UpdateWorkTokenRole(sourceWorkId, TokenRole.Source);

        var method = typeof(MainViewModel).GetMethod(
            "BuildTokenSpecPickerWorks",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var items = (List<WorkOption>)method.Invoke(null, [store])!;

        Assert.Equal(2, items.Count);
        Assert.Contains(items, w => w.Id == sourceWorkId && w.IsSource);
        Assert.Contains(items, w => w.Id == plainWorkId && !w.IsSource);
        Assert.DoesNotContain(items, w => w.Id == refWorkId);
    }

    [Fact]
    public void BuildTokenSpecPickerWorks_marks_source_when_only_reference_has_role()
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");
        var systemId = store.AddSystem("S", projectId, true);
        var flowId = store.AddFlow("F", systemId);
        var workId = store.AddWork("W", flowId);
        var refWorkId = store.AddReferenceWork(workId);

        store.UpdateWorkTokenRole(refWorkId, TokenRole.Source);

        var method = typeof(MainViewModel).GetMethod(
            "BuildTokenSpecPickerWorks",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var items = (List<WorkOption>)method.Invoke(null, [store])!;

        var item = Assert.Single(items);
        Assert.Equal(workId, item.Id);
        Assert.True(item.IsSource, "원본 Work 는 자기 자신의 reference Work 가 Source Role 을 갖는 경우 Source 로 표시되어야 한다.");
    }
}
