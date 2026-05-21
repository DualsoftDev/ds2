using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ds2.LightHouse.Protocol;
using log4net;

namespace Promaker.Knowledge;

/// <summary>
/// **PR-F (todo-lighthouse-index-summary.md §5.1)** — KB profile fetch / SSE event 분류 핵심 로직.
/// <para/>
/// ViewModel 인스턴스 의존성 없이 pure 로 호출 가능한 단위로 분리 — test 친화성 +
/// LlmChatViewModel 의 partial 비중 감소. ViewModel 의 <c>FetchKbProfilesAsync</c> 가 본 helper 의
/// <see cref="FetchForServiceAsync"/> 를 service 별로 호출.
/// </summary>
internal static class KbProfileExtractor
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(KbProfileExtractor));

    /// <summary>
    /// SSE event 가 KB profile invalidate trigger 인지 판정.
    /// <para/>
    /// <see cref="ServerEventNames.CollectionAdded"/> / <see cref="ServerEventNames.CollectionUpdated"/> /
    /// <see cref="ServerEventNames.CollectionDeleted"/> 만 true. caption-progress / upload-progress /
    /// keepalive 등 progress event 는 false (Burst 차단 — §5.1 SSE event 분류 정합).
    /// </summary>
    public static bool IsKbProfileEvent(ServerEventDto? evt)
    {
        if (evt is null || string.IsNullOrEmpty(evt.Event)) return false;
        return evt.Event == ServerEventNames.CollectionAdded
            || evt.Event == ServerEventNames.CollectionUpdated
            || evt.Event == ServerEventNames.CollectionDeleted;
    }

    /// <summary>
    /// server response collection 목록을 acceptedIds 셋으로 filter.
    /// <para/>
    /// accepted = session 발급 응답의 <c>acceptedCollectionIds</c> — server 가 본 chat panel 에 노출 허용한
    /// collection 만 KB digest 에 포함. unknown / unindexable / 비활성 collection 은 자연 제외.
    /// case-insensitive (collection id 가 guid 형식이라 무관하지만 defense-in-depth).
    /// </summary>
    public static IReadOnlyList<CollectionInfo> FilterAccepted(
        IReadOnlyList<CollectionInfo> collections,
        IReadOnlyList<string> acceptedIds)
    {
        if (collections is null || collections.Count == 0) return Array.Empty<CollectionInfo>();
        if (acceptedIds is null || acceptedIds.Count == 0) return Array.Empty<CollectionInfo>();
        var set = new HashSet<string>(acceptedIds, StringComparer.OrdinalIgnoreCase);
        return collections
            .Where(c => !string.IsNullOrEmpty(c.Id) && set.Contains(c.Id))
            .ToList();
    }

    /// <summary>
    /// 단일 service 에 대한 KB profile fetch — <see cref="LightHouseClient.ListCollectionsAsync"/> 호출 후
    /// <see cref="FilterAccepted"/> 결과 반환.
    /// <para/>
    /// service 실패 (LightHouseAuthException / Timeout / Network 등) 시 빈 list 반환 — caller 의 다른 service
    /// 진행에 영향 0. <see cref="OperationCanceledException"/> 만 caller 로 전파 (cancellation 우선).
    /// </summary>
    public static async Task<IReadOnlyList<CollectionInfo>> FetchForServiceAsync(
        LightHouseClient client,
        IReadOnlyList<string> acceptedIds,
        CancellationToken ct = default)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        if (acceptedIds is null || acceptedIds.Count == 0) return Array.Empty<CollectionInfo>();
        try
        {
            var resp = await client.ListCollectionsAsync(ct).ConfigureAwait(false);
            return FilterAccepted(resp.Collections, acceptedIds);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"KB profile fetch 실패 — {ex.GetType().Name}: {ex.Message}");
            return Array.Empty<CollectionInfo>();
        }
    }
}
