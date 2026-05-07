using System;
using AAStoPLC.TagWizard;
using Ds2.Core;
using Ds2.Core.Store;

namespace Promaker.Services;

/// <summary>
/// ApiCall 1개에서 (FlowName, DeviceAlias, ApiName, ParentCallId) 를 해석.
/// 실제 구현은 <see cref="AAStoPLC.TagWizard.ApiCallContextResolver"/>(F#) 으로 이관됨.
/// 호출부 호환을 위한 thin shim — 내부 record struct 를 동일 시그니처로 재export.
/// </summary>
public static class ApiCallContextResolver
{
    public readonly record struct Context(string FlowName, string DeviceAlias, string ApiName, Guid ParentCallId);

    public static Context Resolve(DsStore store, ApiCall apiCall)
    {
        var c = AAStoPLC.TagWizard.ApiCallContextResolver.Resolve(store, apiCall);
        return new Context(c.FlowName, c.DeviceAlias, c.ApiName, c.ParentCallId);
    }
}
