using System.Collections.Concurrent;
using DSPilot.Models;
using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// 메모리 기반 Call 상태 저장소 (DB 병행)
/// 실시간 상태는 메모리 우선, DB는 폴백 및 영속화용
/// </summary>
public class InMemoryCallStateStore
{
    private readonly ConcurrentDictionary<string, CallStateSnapshot> _states = new();
    private readonly IDspRepository? _dspRepo;
    private readonly ILogger<InMemoryCallStateStore> _logger;

    public InMemoryCallStateStore(
        ILogger<InMemoryCallStateStore> logger,
        IDspRepository? dspRepo = null)
    {
        _logger = logger;
        _dspRepo = dspRepo;
    }

    /// <summary>
    /// Call 상태 조회 (메모리 우선, DB 폴백)
    /// </summary>
    public async ValueTask<CallStateSnapshot?> GetCallStateAsync(CallKey key)
    {
        var hashKey = key.ToHashKey();

        // 1. 메모리 우선
        if (_states.TryGetValue(hashKey, out var state))
        {
            return state;
        }

        // 2. DB 폴백 (옵션)
        if (_dspRepo != null)
        {
            try
            {
                var dbState = await _dspRepo.GetCallStateAsync(key);
                var callData = await _dspRepo.GetCallByKeyAsync(key);

                if (callData != null)
                {
                    var snapshot = new CallStateSnapshot
                    {
                        Key = key,
                        State = dbState,
                        LastGoingTime = callData.PreviousGoingTime,
                        AverageGoingTime = callData.AverageGoingTime,
                        GoingCount = callData.GoingCount
                    };

                    // 메모리에 캐싱
                    _states.TryAdd(hashKey, snapshot);

                    return snapshot;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load Call state from DB for {CallKey}", key);
            }
        }

        return null;
    }

    /// <summary>
    /// Call 상태 업데이트 (메모리 + DB)
    /// </summary>
    public async ValueTask UpdateCallStateAsync(CallKey key, string state)
    {
        var hashKey = key.ToHashKey();

        _states.AddOrUpdate(hashKey,
            new CallStateSnapshot { Key = key, State = state },
            (_, old) => old with { State = state });

        // DB 업데이트 (비동기)
        if (_dspRepo != null)
        {
            try
            {
                await _dspRepo.UpdateCallStateAsync(key, state);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update Call state in DB for {CallKey}", key);
            }
        }
    }

    /// <summary>
    /// Call 상태 및 통계 업데이트 (Going → Finish 시)
    /// </summary>
    public async ValueTask UpdateCallWithStatisticsAsync(
        CallKey key,
        string state,
        int goingTime,
        double average,
        double stdDev,
        int goingCount)
    {
        var hashKey = key.ToHashKey();

        _states.AddOrUpdate(hashKey,
            new CallStateSnapshot
            {
                Key = key,
                State = state,
                LastGoingTime = goingTime,
                AverageGoingTime = average,
                GoingCount = goingCount
            },
            (_, old) => old with
            {
                State = state,
                LastGoingTime = goingTime,
                AverageGoingTime = average,
                GoingCount = goingCount
            });

        // DB 업데이트 (비동기)
        if (_dspRepo != null)
        {
            try
            {
                await _dspRepo.UpdateCallWithStatisticsAsync(key, state, goingTime, average, stdDev);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update Call statistics in DB for {CallKey}", key);
            }
        }
    }

    /// <summary>
    /// 모든 Call 상태 스냅샷 조회 (메모리만, 동기)
    /// UI에서 빠른 조회용
    /// </summary>
    public IReadOnlyDictionary<string, CallStateSnapshot> GetAllStatesSnapshot()
    {
        return _states;
    }

    /// <summary>
    /// 특정 Flow의 Call 상태 목록 조회
    /// </summary>
    public List<CallStateSnapshot> GetCallStatesByFlow(string flowName)
    {
        return _states.Values
            .Where(s => s.Key.FlowName == flowName)
            .ToList();
    }

    /// <summary>
    /// 메모리 상태 초기화
    /// </summary>
    public void Clear()
    {
        _states.Clear();
        _logger.LogInformation("InMemoryCallStateStore cleared");
    }

    /// <summary>
    /// 현재 캐시된 상태 개수
    /// </summary>
    public int CachedStateCount => _states.Count;
}

/// <summary>
/// Call 상태 스냅샷 (메모리 캐시용)
/// </summary>
public record CallStateSnapshot
{
    public required CallKey Key { get; init; }
    public string State { get; init; } = "Ready";
    public int? LastGoingTime { get; init; }
    public double? AverageGoingTime { get; init; }
    public int GoingCount { get; init; }
}
