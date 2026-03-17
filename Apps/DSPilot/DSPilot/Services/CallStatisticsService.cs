using DSPilot.Engine;
using Microsoft.FSharp.Collections;

namespace DSPilot.Services;

/// <summary>
/// Call 통계 계산 서비스 - F# Statistics 모듈 사용
/// </summary>
public class CallStatisticsService
{
    private readonly ILogger<CallStatisticsService> _logger;

    // CallName → Going 시작 시간
    private readonly Dictionary<string, DateTime> _goingStartTimes = new();

    // CallName → Going 시간 히스토리
    private readonly Dictionary<string, List<int>> _goingTimeHistory = new();

    public CallStatisticsService(ILogger<CallStatisticsService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Going 상태 시작 기록
    /// </summary>
    public void RecordGoingStart(string callName)
    {
        _goingStartTimes[callName] = DateTime.Now;
        _logger.LogDebug("Call '{CallName}': Going started", callName);
    }

    /// <summary>
    /// Going 종료 시 시간 계산 및 통계 업데이트 (F# Statistics 사용)
    /// </summary>
    /// <returns>(시작 시간, 종료 시간, Going 시간, 평균, 표준편차)</returns>
    public (DateTime? StartTime, DateTime FinishTime, int GoingTime, double Average, double StdDev) RecordGoingFinish(string callName)
    {
        var finishTime = DateTime.Now;

        if (!_goingStartTimes.TryGetValue(callName, out var startTime))
        {
            _logger.LogWarning("Call '{CallName}': No Going start time found", callName);
            return (null, finishTime, 0, 0, 0);
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

        _logger.LogInformation(
            "Call '{CallName}': Going finished - Time={Time}ms, Avg={Avg:F0}ms, StdDev={StdDev:F0}ms (Samples={Count})",
            callName, goingTime, average, stdDev, _goingTimeHistory[callName].Count);

        return (startTime, finishTime, goingTime, average, stdDev);
    }

    /// <summary>
    /// 모든 통계 초기화
    /// </summary>
    public void Reset()
    {
        _goingStartTimes.Clear();
        _goingTimeHistory.Clear();
        _logger.LogInformation("All statistics cleared");
    }

    /// <summary>
    /// 통계 추적 중인 Call 개수
    /// </summary>
    public int TrackedCallCount => _goingTimeHistory.Count;
}
