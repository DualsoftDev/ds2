using System;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;

namespace Promaker.Services;

/// <summary>
/// ApiCall 1개에서 (FlowName, DeviceAlias, ApiName, ParentCallId) 를 해석.
/// TagWizardBasicDialog.ResolveContext / ResolveParentCallId 의 로직을 단일 서비스로 추출.
/// </summary>
public static class ApiCallContextResolver
{
    public readonly record struct Context(string FlowName, string DeviceAlias, string ApiName, Guid ParentCallId);

    public static Context Resolve(DsStore store, ApiCall apiCall)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));
        if (apiCall == null) throw new ArgumentNullException(nameof(apiCall));

        // Flow: OriginFlowId 우선, 없으면 parent Call → Work → Flow 체인
        string flow = "";
        if (apiCall.OriginFlowId != null && FSharpOption<Guid>.get_IsSome(apiCall.OriginFlowId))
        {
            var f = Queries.getFlow(apiCall.OriginFlowId.Value, store);
            if (f != null && FSharpOption<Flow>.get_IsSome(f)) flow = f.Value.Name;
        }

        var parentCall = store.Calls.Values.FirstOrDefault(c =>
            c.ApiCalls != null && c.ApiCalls.Any(ac => ac.Id == apiCall.Id));

        if (string.IsNullOrEmpty(flow) && parentCall != null)
        {
            var workOpt = Queries.getWork(parentCall.ParentId, store);
            if (workOpt != null && FSharpOption<Work>.get_IsSome(workOpt))
            {
                var flowOpt = Queries.getFlow(workOpt.Value.ParentId, store);
                if (flowOpt != null && FSharpOption<Flow>.get_IsSome(flowOpt))
                    flow = flowOpt.Value.Name;
            }
        }

        string device = parentCall?.DevicesAlias ?? "";

        string api = "";
        if (apiCall.ApiDefId != null && FSharpOption<Guid>.get_IsSome(apiCall.ApiDefId))
        {
            var adOpt = Queries.getApiDef(apiCall.ApiDefId.Value, store);
            if (adOpt != null && FSharpOption<ApiDef>.get_IsSome(adOpt))
                api = adOpt.Value.Name;
        }

        return new Context(flow, device, api, parentCall?.Id ?? Guid.Empty);
    }
}
