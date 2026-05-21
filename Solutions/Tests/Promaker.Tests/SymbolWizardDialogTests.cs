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
            ListModule.OfSeq(new[] { apiDef }));

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
            FSharpList<ModelGenerator.ApiDefPlan>.Empty);

        return ListModule.OfSeq(new[] { controller, device });
    }
}
