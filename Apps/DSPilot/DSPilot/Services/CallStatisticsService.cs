using DSPilot.Engine;
using DSPilot.Models;
using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// Call 통계 계산 서비스 (F# RuntimeStatistics 래퍼)
/// </summary>
public class CallStatisticsService
{
    private readonly ILogger<CallStatisticsService> _logger;
    private readonly IDspRepository _dspRepository;
    private readonly RuntimeStatisticsTrackerMutable _tracker;
    private volatile bool _isDisposing = false;

    // CallName → DB에서 로드한 기존 GoingCount (캐시)
    private readonly Dictionary<string, int> _baseCountCache = new();

    public CallStatisticsService(
        ILogger<CallStatisticsService> logger,
        IDspRepository dspRepository)
    {
        _logger = logger;
        _dspRepository = dspRepository;
        _tracker = new RuntimeStatisticsTrackerMutable();
    }

    /// <summary>
    /// Mark service as disposing to prevent new operations
    /// </summary>
    public void MarkDisposing()
    {
        _isDisposing = true;
    }

    /// <summary>
    /// Going 상태 시작 기록
    /// </summary>
    public async Task RecordGoingStartAsync(string callName)
    {
        // DB에서 기존 GoingCount 로드 (처음 한 번만, 캐시 사용)
        int baseCount = 0;
        if (!_baseCountCache.ContainsKey(callName))
        {
            baseCount = await LoadBaseCountAsync(callName);
            _baseCountCache[callName] = baseCount;
        }
        else
        {
            baseCount = _baseCountCache[callName];
        }

        // F# RuntimeStatisticsTracker 호출
        _tracker.RecordStart(callName, baseCount);
        _logger.LogDebug("Call '{CallName}': Going started", callName);
    }

    private async Task<int> LoadBaseCountAsync(string callName)
    {
        // Check if service is disposing
        if (_isDisposing)
        {
            _logger.LogDebug("Service is disposing, using default GoingCount for '{CallName}'", callName);
            return 0;
        }

        try
        {
            var callInfo = await _dspRepository.GetCallInfoAsync(callName);
            if (callInfo != null)
            {
                var (workName, flowName) = callInfo.Value;
                var callKey = new DSPilot.Models.CallKey(flowName, callName);
                var callData = await _dspRepository.GetCallByKeyAsync(callKey);
                int count = callData?.GoingCount ?? 0;
                _logger.LogDebug("Call '{CallName}': Loaded base GoingCount = {Count}", callName, count);
                return count;
            }
            else
            {
                _logger.LogWarning("Call '{CallName}': Not found in database, defaulting GoingCount to 0", callName);
                return 0;
            }
        }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug("Repository disposed, using default GoingCount for '{CallName}'", callName);
            _isDisposing = true;
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load base GoingCount for Call '{CallName}', defaulting to 0", callName);
            return 0;
        }
    }

    /// <summary>
    /// Going 종료 시 시간 계산 및 통계 업데이트 (F# RuntimeStatistics 사용)
    /// </summary>
    /// <returns>(시작 시간, 종료 시간, Going 시간, 평균, 표준편차, 누적 횟수)</returns>
    public (DateTime? StartTime, DateTime FinishTime, int GoingTime, double Average, double StdDev, int GoingCount) RecordGoingFinish(string callName)
    {
        var finishTime = DateTime.Now;

        // F# RuntimeStatisticsTracker 호출
        var stats = _tracker.RecordFinish(callName);

        if (stats == null)
        {
            _logger.LogWarning("Call '{CallName}': No Going start time found", callName);
            return (null, finishTime, 0, 0, 0, 0);
        }

        _logger.LogInformation(
            "Call '{CallName}': Going finished - Time={Time}ms, Avg={Avg:F0}ms, StdDev={StdDev:F0}ms (Session={Session}, Base={Base}, Total={Total})",
            callName, stats.Value.GoingTime, stats.Value.Average, stats.Value.StdDev,
            stats.Value.SessionCount, stats.Value.BaseCount, stats.Value.TotalCount);

        // StartTime은 F#에서 이미 처리되었으므로 null 반환 (필요시 F#에 저장 가능)
        return (null, finishTime, stats.Value.GoingTime, stats.Value.Average, stats.Value.StdDev, stats.Value.TotalCount);
    }

    /// <summary>
    /// 모든 통계 초기화 (세션 데이터만 클리어, DB 기존 값은 유지)
    /// </summary>
    public void Reset()
    {
        _tracker.ResetAllSessions();
        _logger.LogInformation("Session statistics cleared (base GoingCount preserved)");
    }

    /// <summary>
    /// 통계 추적 중인 Call 개수
    /// </summary>
    public int TrackedCallCount => _tracker.TrackedCallCount;
}
