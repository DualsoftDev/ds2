using System.Collections.Generic;
using System.Linq;
using Promaker.LlmAgent;

namespace Promaker.Knowledge;

/// <summary>
/// **D-S7-3c (s6-r31) — orphan KbCollection (소속 service 가 삭제됨) 탐지 SSOT helper**
/// (done-lighthouse-kb-server.md §3.16.8).
/// <para/>
/// orphan = KbCollection.ServiceId 가 비어있지 않으나 LightHouseServices 의 어떤 entry 와도 match 안 됨.
/// KbManagerDialog 의 "orphan 정리" 버튼 + LlmChatViewModel 의 진단 chip 이 본 helper 호출.
/// </summary>
public static class KbCollectionOrphanHelper
{
    /// <summary>**D-S7-3c (s6-r31)** — orphan entry 목록 반환 (Active 여부 무관, 모든 orphan).</summary>
    public static IReadOnlyList<KbCollectionEntry> FindOrphans(LlmConfig config)
    {
        if (config is null) return System.Array.Empty<KbCollectionEntry>();
        var serviceIds = new HashSet<string>(config.LightHouseServices.Select(s => s.ServiceId));
        return config.KbCollections
            .Where(k => !string.IsNullOrEmpty(k.ServiceId) && !serviceIds.Contains(k.ServiceId))
            .ToList();
    }

    /// <summary>**D-S7-3c (s6-r31)** — orphan 개수 (chip / button 표시용).</summary>
    public static int CountOrphans(LlmConfig config) => FindOrphans(config).Count;

    /// <summary>
    /// **D-S7-3c (s6-r31)** — active service 와 match 안 되는 (= 비활성/삭제 service 의) orphan. LlmChatViewModel
    /// 의 진단 chip path 정합 — Active=true 인 collection 중 소속 service 가 active 가 아닌 경우만.
    /// </summary>
    public static int CountActiveOrphans(LlmConfig config)
    {
        if (config is null) return 0;
        var activeServiceIds = new HashSet<string>(config.LightHouseServices.Where(s => s.Active).Select(s => s.ServiceId));
        return config.KbCollections.Count(k =>
            k.Active && !string.IsNullOrEmpty(k.ServiceId) && !activeServiceIds.Contains(k.ServiceId));
    }
}
