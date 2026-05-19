namespace Promaker.Knowledge;

/// <summary>
/// **Phase S7 D-S7-2c (s6-r32) — SSE event name SSOT** (client-side).
/// <para/>
/// server-side 의 F# <c>Ds2.LightHouseService.ServerEventNames</c> module 과 wire string 정합 의무.
/// 양쪽 SSOT 가 분리된 사유 = K4 Protocol SSOT 통합 phase 가 별 phase (todo-lighthouse-kb-server.md §7.7 K4)
/// — 본 phase 까지는 양쪽 박제, K4 통합 시 `Ds2.LightHouse.Protocol` neutral project 의 단일 SSOT 로 합침.
/// <para/>
/// 신규 event 추가 시 server <c>EventBus.fs</c> 의 <c>module ServerEventNames</c> + 본 class 둘 다 1 줄씩 박제.
/// </summary>
public static class ServerEventNames
{
    public const string CollectionAdded = "collection-added";
    public const string CollectionUpdated = "collection-updated";
    public const string CollectionDeleted = "collection-deleted";
    public const string Keepalive = "keepalive";

    /// <summary>
    /// **D-S7-2c (s6-r32)** — client → server publish (POST /events/caption-progress) → server fan-out.
    /// Phase 2 vision caption indexing 진행률 박제 path.
    /// </summary>
    public const string CaptionProgress = "caption-progress";
}
