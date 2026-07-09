// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Collections.Concurrent;

namespace DSPilot.Infrastructure;

/// <summary>
/// 폴링 엔드포인트용 프로세스 전역 TTL 캐시 + single-flight. 동접 탭들이 같은 파라미터로 반복하는
/// 계산을 TTL 창 안에서 1회로 코얼레싱한다(동접 N탭 = N배 재계산 방지). 동시 도착분은 첫 요청의
/// 계산을 공유하되, 계산 주체 요청이 취소/실패하면(Scoped 의존성은 주체 요청 수명에 묶인다)
/// 대기자는 예외를 공유하지 않고 자기 factory 로 직접 재계산한다.
/// 참조형 결과를 캐시하면 호출측이 mutate 하지 말 것 — 필요하면 clone 을 넘겨라.
/// </summary>
public static class TtlRequestCache
{
    private static readonly ConcurrentDictionary<string, (DateTime ExpiresUtc, object? Value)> s_cache = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task<object?>>> s_inflight = new();

    public static async Task<T> GetOrComputeAsync<T>(
        string key, TimeSpan ttl, Func<Task<T>> factory, Func<T, T>? clone = null)
    {
        T Out(T v) => clone is null ? v : clone(v);

        if (s_cache.TryGetValue(key, out var hit) && hit.ExpiresUtc > DateTime.UtcNow)
            return Out((T)hit.Value!);

        var lazy = new Lazy<Task<object?>>(async () => (object?)await factory().ConfigureAwait(false));
        var winner = s_inflight.GetOrAdd(key, lazy);
        if (ReferenceEquals(winner, lazy))
        {
            try
            {
                var v = (T)(await lazy.Value.ConfigureAwait(false))!;
                s_cache[key] = (DateTime.UtcNow.Add(ttl), v);
                // 키 공간은 (엔드포인트 × 파라미터 조합)으로 유한하지만 기간 파라미터가 격자 이동하므로 만료분만 정리.
                if (s_cache.Count > 512)
                    foreach (var stale in s_cache.Where(e => e.Value.ExpiresUtc <= DateTime.UtcNow).ToList())
                        s_cache.TryRemove(stale.Key, out _);
                return Out(v);
            }
            finally
            {
                s_inflight.TryRemove(key, out _);
            }
        }

        try
        {
            return Out((T)(await winner.Value.ConfigureAwait(false))!);
        }
        catch
        {
            var v = await factory().ConfigureAwait(false);
            s_cache[key] = (DateTime.UtcNow.Add(ttl), v);
            return Out(v);
        }
    }
}
