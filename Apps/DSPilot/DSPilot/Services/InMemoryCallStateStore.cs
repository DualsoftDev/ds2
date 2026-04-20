using System.Collections.Concurrent;
using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// 메모리 기반 Call 상태 저장소 (DB 병행)
/// 실시간 상태는 메모리 우선, DB는 폴백 및 영속화용
/// </summary>
public class InMemoryCallStateStore
{
    private readonly ConcurrentDictionary<Guid, CallStateSnapshot> _states = new();
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
    public async ValueTask<CallStateSnapshot?> GetCallStateAsync(Guid callId)
    {
        // 1. 메모리 우선
        if (_states.TryGetValue(callId, out var state))
        {
            return state;
        }

        // 2. DB 폴백 (옵션)
        if (_dspRepo != null)
        {
            try
            {
                var dbState = await _dspRepo.GetCallStateAsync(callId);
                var callData = await _dspRepo.GetCallByIdAsync(callId);

                if (callData != null)
                {
                    var snapshot = new CallStateSnapshot
                    {
                        CallId = callId,
                        State = dbState,
                        LastGoingTime = callData.PreviousGoingTime,
                        AverageGoingTime = callData.AverageGoingTime,
                        GoingCount = callData.GoingCount
                    };

                    // 메모리에 캐싱
                    _states.TryAdd(callId, snapshot);

                    return snapshot;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load Call state from DB for CallId {CallId}", callId);
            }
        }

        return null;
    }

    /// <summary>
    /// Call 상태 업데이트 (메모리 + DB)
    /// </summary>
    public async ValueTask UpdateCallStateAsync(Guid callId, string state)
    {
        _states.AddOrUpdate(callId,
            new CallStateSnapshot { CallId = callId, State = state },
            (_, old) => old with { State = state });

        // DB 업데이트 (비동기)
        if (_dspRepo != null)
        {
            try
            {
                await _dspRepo.UpdateCallStateAsync(callId, state);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update Call state in DB for CallId {CallId}", callId);
            }
        }
    }

    /// <summary>
    /// Call 상태 및 통계 업데이트 (Going → Finish 시)
    /// </summary>
    public async ValueTask UpdateCallWithStatisticsAsync(
        Guid callId,
        string state,
        int goingTime,
        double average,
        double stdDev,
        int goingCount)
    {
        _states.AddOrUpdate(callId,
            new CallStateSnapshot
            {
                CallId = callId,
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
                await _dspRepo.UpdateCallWithStatisticsAsync(callId, state, goingTime, average, stdDev);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update Call statistics in DB for CallId {CallId}", callId);
            }
        }
    }

    /// <summary>
    /// 모든 Call 상태 스냅샷 조회 (메모리만, 동기)
    /// UI에서 빠른 조회용
    /// </summary>
    public IReadOnlyDictionary<Guid, CallStateSnapshot> GetAllStatesSnapshot()
    {
        return _states;
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
    public required Guid CallId { get; init; }
    public string State { get; init; } = "Ready";
    public int? LastGoingTime { get; init; }
    public double? AverageGoingTime { get; init; }
    public int GoingCount { get; init; }
}
