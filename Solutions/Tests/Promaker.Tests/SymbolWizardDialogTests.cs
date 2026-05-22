using System;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Ds2.SymbolImport;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Promaker.Dialogs;
using Xunit;

namespace Promaker.Tests;

public sealed class SymbolWizardDialogTests
{
    [Fact]
    public void ApplyPlansToStore_removes_default_empty_NewSystem_placeholder()
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");
        var placeholderSystemId = store.AddSystem("NewSystem", projectId, isActive: true);
        store.AddFlow("NewFlow", placeholderSystemId);

        SymbolWizardDialog.ApplyPlansToStore(store, projectId, BuildPlans());

        var activeSystems = Queries.activeSystemsOf(projectId, store).ToList();
        Assert.Single(activeSystems);
        Assert.Equal("Controller", activeSystems[0].Name);
        Assert.DoesNotContain(store.Systems.Values, s => s.Name == "NewSystem");
    }

    [Fact]
    public void ApplyPlansToStore_adds_generated_UserTags()
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");

        SymbolWizardDialog.ApplyPlansToStore(store, projectId, BuildPlans());

        var userTags = store.GetAllUserTagsForProject().ToList();
        var tag = Assert.Single(userTags);
        Assert.Equal("Main_PLC_ERR", tag.Name);
        Assert.Equal("Error", tag.LogLevel);
        Assert.Equal("M70", tag.TagAddress);
        Assert.Equal("Bit", tag.ValueType);
        Assert.Equal("RisingEdge", tag.MatchOp);
        Assert.Equal("1", tag.MatchValue);
    }

    private static FSharpList<ModelGenerator.SystemPlan> BuildPlans()
    {
        var apiDef = new ModelGenerator.ApiDefPlan(
            "ADV",
            ActionType.NewReal(SignalMode.Level, FSharpOption<TimePolicy>.None),
            SensingType.NewReal(SignalMode.Level, FSharpOption<TimePolicy>.None));

        var device = new ModelGenerator.SystemPlan(
            "Device",
            false,
            FSharpList<ModelGenerator.FlowPlan>.Empty,
            ListModule.OfSeq(new[] { apiDef }),
            FSharpList<ModelGenerator.UserTagPlan>.Empty);

        var call = new ModelGenerator.CallPlan(
            "Device.ADV",
            "Device",
            "ADV",
            FSharpOption<IOTag>.Some(new IOTag("Device_ADV_IN", "X0", "")),
            FSharpOption<IOTag>.Some(new IOTag("Device_ADV_OUT", "Y0", "")));

        var work = new ModelGenerator.WorkPlan(
            "Work",
            ListModule.OfSeq(new[] { call }));

        var flow = new ModelGenerator.FlowPlan(
            "Flow",
            ListModule.OfSeq(new[] { work }));

        var controller = new ModelGenerator.SystemPlan(
            "Controller",
            true,
            ListModule.OfSeq(new[] { flow }),
            FSharpList<ModelGenerator.ApiDefPlan>.Empty,
            ListModule.OfSeq(new[]
            {
                new ModelGenerator.UserTagPlan(
                    "Main_PLC_ERR",
                    "Error",
                    "M70",
                    "Bit",
                    "RisingEdge",
                    "1")
            }));

        return ListModule.OfSeq(new[] { controller, device });
    }
}
