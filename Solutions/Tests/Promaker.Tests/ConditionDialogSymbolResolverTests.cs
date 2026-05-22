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
    // 이전 테스트 `BuildDisplayNameToApiCallId_prefers_owner_condition_before_global_duplicate` 삭제:
    // 시나리오 (같은 Work 안 두 원본 Call 이 동일 표시명) 자체가 새 정책에서 *Core 가드로 차단*.
    // resolver 의 owner condition 우선 동작은 *깨진 store* 가정에서만 의미가 있었는데, 그 깨진 상태가
    // 이제 store 에 진입 불가 — 테스트가 정책 우회 fixture 를 강제했었다.


    [Fact]
    public void BuildRegisteredDisplayNames_for_work_returns_owner_condition_symbols_only()
    {
        var store = CreateBaseStore(out var activeWorkId, out var deviceSystemId);
        var advanceApiDefId = store.AddApiDefWithProperties("ADV", deviceSystemId);
        var returnApiDefId = store.AddApiDefWithProperties("RET", deviceSystemId);

        var advanceCallId = store.AddCallWithLinkedApiDefs(activeWorkId, "Device", "ADV", [advanceApiDefId]);
        var advanceApiCallId = store.Calls[advanceCallId].ApiCalls.Single().Id;
        store.AddCallWithLinkedApiDefs(activeWorkId, "Device", "RET", [returnApiDefId]);
        store.AddWorkConditionWithApiCalls(activeWorkId, ConditionType.SkipAction, [advanceApiCallId]);

        var names = ConditionDialogSymbolResolver.BuildRegisteredDisplayNames(
            store,
            activeWorkId,
            EntityKind.Work,
            ConditionType.SkipAction);

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
