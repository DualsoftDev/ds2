using DSPilot.Engine;
using DSPilot.Repositories;
using Microsoft.FSharp.Collections;

namespace DSPilot.Services;

/// <summary>
/// Call 통계 계산 서비스 - F# Statistics 모듈 사용
/// </summary>
public class CallStatisticsService
{
    private readonly ILogger<CallStatisticsService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    // CallName → Going 시작 시간
    private readonly Dictionary<string, DateTime> _goingStartTimes = new();

    // CallName → Going 시간 히스토리
    private readonly Dictionary<string, List<int>> _goingTimeHistory = new();

    // CallName → DB에서 로드한 기존 GoingCount
    private readonly Dictionary<string, int> _baseGoingCount = new();

    // CallName → 세션 내 실행 횟수 (히스토리 캡과 무관하게 정확한 카운트 유지)
    private readonly Dictionary<string, int> _sessionExecutionCount = new();

    public CallStatisticsService(
        ILogger<CallStatisticsService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Going 상태 시작 기록
    /// </summary>
    public async Task RecordGoingStartAsync(string callName)
    {
        _goingStartTimes[callName] = DateTime.Now;

        // DB에서 기존 GoingCount 로드 (처음 한 번만)
        if (!_baseGoingCount.ContainsKey(callName))
        {
            using var scope = _scopeFactory.CreateScope();
            var dspRepo = scope.ServiceProvider.GetRequiredService<IDspRepository>();

            try
            {
                var callData = await dspRepo.GetCallByNameAsync(callName);
                _baseGoingCount[callName] = callData?.GoingCount ?? 0;
                _logger.LogDebug("Call '{CallName}': Loaded base GoingCount = {Count}", callName, _baseGoingCount[callName]);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load base GoingCount for Call '{CallName}', defaulting to 0", callName);
                _baseGoingCount[callName] = 0;
            }
        }

        _logger.LogDebug("Call '{CallName}': Going started", callName);
    }

    /// <summary>
    /// Going 종료 시 시간 계산 및 통계 업데이트 (F# Statistics 사용)
    /// </summary>
    /// <returns>(시작 시간, 종료 시간, Going 시간, 평균, 표준편차, 누적 횟수)</returns>
    public (DateTime? StartTime, DateTime FinishTime, int GoingTime, double Average, double StdDev, int GoingCount) RecordGoingFinish(string callName)
    {
        var finishTime = DateTime.Now;

        if (!_goingStartTimes.TryGetValue(callName, out var startTime))
        {
            _logger.LogWarning("Call '{CallName}': No Going start time found", callName);
            return (null, finishTime, 0, 0, 0, 0);
        }

        var goingTime = (int)(finishTime - startTime).TotalMilliseconds;
        _goingStartTimes.Remove(callName);

        if (!_goingTimeHistory.ContainsKey(callName))
        {
            _goingTimeHistory[callName] = new List<int>();
        }

        var history = _goingTimeHistory[callName];

        // F# Statistics 모듈 사용
        var fsharpList = ListModule.OfSeq(history);
        var (average, stdDev, cv, updatedSamples) = Statistics.calculateStatistics(fsharpList, goingTime);

        // 업데이트된 샘플로 히스토리 갱신
        _goingTimeHistory[callName] = new List<int>(updatedSamples);

        // 세션 내 증가분 + DB의 기존 값 (히스토리 캡과 무관하게 정확한 카운트)
        _sessionExecutionCount[callName] = _sessionExecutionCount.GetValueOrDefault(callName) + 1;
        var baseCount = _baseGoingCount.TryGetValue(callName, out var bc) ? bc : 0;
        var totalGoingCount = baseCount + _sessionExecutionCount[callName];

        _logger.LogInformation(
            "Call '{CallName}': Going finished - Time={Time}ms, Avg={Avg:F0}ms, StdDev={StdDev:F0}ms (Session={Session}, Base={Base}, Total={Total})",
            callName, goingTime, average, stdDev, _sessionExecutionCount[callName], baseCount, totalGoingCount);

        return (startTime, finishTime, goingTime, average, stdDev, totalGoingCount);
    }

    /// <summary>
    /// 모든 통계 초기화 (세션 데이터만 클리어, DB 기존 값은 유지)
    /// </summary>
    public void Reset()
    {
        _goingStartTimes.Clear();
        _goingTimeHistory.Clear();
        _sessionExecutionCount.Clear();
        // _baseGoingCount는 유지 (DB 값)
        _logger.LogInformation("Session statistics cleared (base GoingCount preserved)");
    }

    /// <summary>
    /// 통계 추적 중인 Call 개수
    /// </summary>
    public int TrackedCallCount => _goingTimeHistory.Count;
}
