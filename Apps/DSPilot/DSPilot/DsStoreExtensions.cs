using Ds2.Core;
using Ds2.Core.Store;
using Microsoft.FSharp.Core;

namespace DSPilot;

/// <summary>
/// DsStore 확장 메서드 — 기존 Ds2.UI.Core.DsStoreQueriesExtensions에서 DSPilot이 사용하던 메서드 이전
/// </summary>
public static class DsStoreExtensions
{
    /// <summary>ApiCall의 InTag/OutTag에서 IOTag 추출</summary>
    public static List<IOTag> GetCallIOTags(this DsStore store)
    {
        return store.ApiCallsReadOnly.Values
            .SelectMany(apiCall => new[] { apiCall.InTag, apiCall.OutTag })
            .Where(opt => OptionModule.IsSome(opt))
            .Select(opt => opt.Value)
            .DistinctBy(tag => tag.Address)
            .ToList();
    }

    /// <summary>HwComponent의 InTag/OutTag에서 IOTag 추출 (현재 DsStore에 HW 딕셔너리 없음 — 빈 리스트 반환)</summary>
    public static List<IOTag> GetHwComponentIOTags(this DsStore store)
    {
        // DsStore에서 HwButtons/HwLamps/HwConditions/HwActions 딕셔너리가 제거됨
        // HW 컴포넌트 IOTag가 필요한 경우 별도 저장소에서 조회 필요
        return [];
    }
}
