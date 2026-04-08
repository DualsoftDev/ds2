using Ds2.Core;
using Ds2.Core.Store;
using Microsoft.FSharp.Core;

namespace DSPilot.TestConsole;

/// <summary>
/// DsStore 확장 메서드 — 기존 Ds2.UI.Core.DsStoreQueriesExtensions에서 이전
/// </summary>
public static class DsStoreExtensions
{
    public static List<IOTag> GetCallIOTags(this DsStore store)
    {
        return store.ApiCallsReadOnly.Values
            .SelectMany(apiCall => new[] { apiCall.InTag, apiCall.OutTag })
            .Where(opt => OptionModule.IsSome(opt))
            .Select(opt => opt.Value)
            .DistinctBy(tag => tag.Address)
            .ToList();
    }

    public static List<IOTag> GetHwComponentIOTags(this DsStore store)
    {
        return [];
    }
}
