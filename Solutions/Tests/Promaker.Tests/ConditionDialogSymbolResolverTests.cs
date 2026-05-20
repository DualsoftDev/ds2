using System;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Dialogs;
using Xunit;

namespace Promaker.Tests;

public sealed class ConditionDialogSymbolResolverTests
{
    [Fact]
    public void BuildDisplayNameToApiCallId_prefers_owner_condition_before_global_duplicate()
    {
        var store = CreateBaseStore(out var activeWorkId, out var deviceSystemId);
        var firstApiDefId = store.AddApiDefWithProperties("ADV", deviceSystemId);
        var secondApiDefId = store.AddApiDefWithProperties("ADV", deviceSystemId);
        var targetApiDefId = store.AddApiDefWithProperties("TARGET", deviceSystemId);

        var firstCallId = store.AddCallWithLinkedApiDefs(activeWorkId, "DeviceA", "ADV", [firstApiDefId]);
        var firstApiCallId = store.Calls[firstCallId].ApiCalls.Single().Id;
        var secondCallId = store.AddCallWithLinkedApiDefs(activeWorkId, "DeviceB", "ADV", [secondApiDefId]);
        var secondApiCallId = store.Calls[secondCallId].ApiCalls.Single().Id;
        var targetCallId = store.AddCallWithLinkedApiDefs(activeWorkId, "Device", "TARGET", [targetApiDefId]);

        store.AddConditionWithApiCalls(targetCallId, ConditionType.ComAux, [secondApiCallId]);

        var map = ConditionDialogSymbolResolver.BuildDisplayNameToApiCallId(
            store,
            targetCallId,
            EntityKind.Call,
            ConditionType.ComAux);

        Assert.Equal(secondApiCallId, map["Device.ADV"]);
        Assert.NotEqual(firstApiCallId, map["Device.ADV"]);
    }

    [Fact]
    public void BuildRegisteredDisplayNames_for_work_returns_owner_condition_symbols_only()
    {
        var store = CreateBaseStore(out var activeWorkId, out var deviceSystemId);
        var advanceApiDefId = store.AddApiDefWithProperties("ADV", deviceSystemId);
        var returnApiDefId = store.AddApiDefWithProperties("RET", deviceSystemId);

        var advanceCallId = store.AddCallWithLinkedApiDefs(activeWorkId, "Device", "ADV", [advanceApiDefId]);
        var advanceApiCallId = store.Calls[advanceCallId].ApiCalls.Single().Id;
        store.AddCallWithLinkedApiDefs(activeWorkId, "Device", "RET", [returnApiDefId]);
        store.AddWorkConditionWithApiCalls(activeWorkId, ConditionType.SkipUnmatch, [advanceApiCallId]);

        var names = ConditionDialogSymbolResolver.BuildRegisteredDisplayNames(
            store,
            activeWorkId,
            EntityKind.Work,
            ConditionType.SkipUnmatch);

        Assert.Contains("Device.ADV", names);
        Assert.DoesNotContain("Device.RET", names);
    }

    private static DsStore CreateBaseStore(out Guid activeWorkId, out Guid deviceSystemId)
    {
        var store = new DsStore();
        var projectId = store.AddProject("P");
        var activeSystemId = store.AddSystem("Active", projectId, true);
        var activeFlowId = store.AddFlow("Flow", activeSystemId);
        activeWorkId = store.AddWork("Main", activeFlowId);
        deviceSystemId = store.AddSystem("Device", projectId, false);
        return store;
    }
}
