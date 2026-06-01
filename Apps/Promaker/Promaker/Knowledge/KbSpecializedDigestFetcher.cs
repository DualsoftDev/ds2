using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using log4net;

namespace Promaker.Knowledge;

/// <summary>
/// **layer E (specialized digest) MCP fetch — 사용자 지시 2026-06-01**.
/// active 셋의 collection 마다 MCP <c>attachment_summary(collectionId, includeSpecialized=true)</c> 를 호출하여
/// specialized digest(<c>summary.md</c> + <c>summary/*.md</c> 합본)를 수신, <c>\n\n---\n\n</c> 으로 concat.
/// <para/>
/// **로컬 <c>.lighthouse-kb/summary/</c> 직접 read (구 PR-I5 <c>SpecializedDigestBuilder</c> 경로) 폐기** — 로컬에
/// summary/ 가 없는 **원격 service 를 구조적으로 지원하지 못했음**. MCP fetch 는 collection 식별자(<see cref="LightHouseClient"/>
/// session 의 active 셋)만 의존하므로 로컬/원격 무관.
/// <para/>
/// service/collection 단위 실패는 Log.Warn 후 skip (chat 차단 0). <see cref="OperationCanceledException"/> 은 rethrow
/// (caller cancel 의도 보존). 빈 셋 / 전부 실패 / 전부 빈 응답 → "" (cache breakpoint 3 skip = 회귀 0).
/// </summary>
internal static class KbSpecializedDigestFetcher
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(KbSpecializedDigestFetcher));

    /// <summary>collection 합본 separator — server <c>SpecializedDigestBuilder.FileSeparator</c> 와 동일 (경계 인식).</summary>
    private const string CollectionSeparator = "\n\n---\n\n";

    /// <summary>
    /// active 셋(serviceId → collectionId[])의 모든 collection 에 대해 MCP <c>attachment_summary(includeSpecialized=true)</c>
    /// 호출 → 합본 string 반환. 각 service 의 client 는 <see cref="LightHouseClientHolder.GetClient"/> 로 routing.
    /// <see cref="LightHouseClient.ExecuteWithSessionRetryAsync"/> 가 app session 재발급 + 1회 retry 흡수.
    /// </summary>
    public static async Task<string> FetchManyViaMcpAsync(
        IReadOnlyDictionary<string, IReadOnlyList<string>> acceptedCollectionIds,
        CancellationToken ct = default)
    {
        if (acceptedCollectionIds is null || acceptedCollectionIds.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var serviceId in acceptedCollectionIds.Keys)
        {
            var client = LightHouseClientHolder.GetClient(serviceId);
            if (client is null)
            {
                Log.Debug($"FetchManyViaMcpAsync — client 부재 skip (service={serviceId})");
                continue;
            }
            foreach (var collectionId in acceptedCollectionIds[serviceId])
            {
                if (string.IsNullOrEmpty(collectionId)) continue;
                string raw;
                try
                {
                    raw = await client.ExecuteWithSessionRetryAsync(
                        (token, ct2) => client.CallMcpToolAsync(
                            token, "attachment_summary",
                            new { collectionId, includeSpecialized = true }, ct2),
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // collection 단위 best-effort — 한 collection 실패가 다른 collection / chat 진입을 막지 않음.
                    Log.Warn($"FetchManyViaMcpAsync attachment_summary 실패 (service={serviceId}, coll={collectionId}): {ex.Message}");
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    if (sb.Length > 0) sb.Append(CollectionSeparator);
                    sb.Append(raw);
                }
                // **주입 로그 (사용자 요청 2026-06-01)** — per-collection fetch 결과 Info. len=0 이면 server 가
                // summary/ 미반환 (옛 server / summary/ 부재) → layer E 미주입 원인 즉시 식별.
                Log.Info($"[layer E] attachment_summary(coll={collectionId}, includeSpecialized=true) → len={(raw?.Length ?? 0)}");
            }
        }
        Log.Info($"[layer E] FetchManyViaMcpAsync — services={acceptedCollectionIds.Count} digestLen={sb.Length}");
        return sb.ToString();
    }
}
