namespace DSPilot.Services;

/// <summary>
/// Call 통계 계산 서비스 - 평균 및 표준편차 계산
/// </summary>
public class CallStatisticsService
{
    private readonly ILogger<CallStatisticsService> _logger;

    // CallName → Going 시작 시간
    private readonly Dictionary<string, DateTime> _goingStartTimes = new();

    // CallName → Going 시간 히스토리 (이동 평균용)
    private readonly Dictionary<string, List<int>> _goingTimeHistory = new();

    private const int MAX_HISTORY_SIZE = 100;  // 최근 100개 샘플 저장

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
    /// Going 종료 시 시간 계산 및 통계 업데이트
    /// </summary>
    /// <returns>(Going 시간, 평균, 표준편차)</returns>
    public (int GoingTime, double Average, double StdDev) RecordGoingFinish(string callName)
    {
        if (!_goingStartTimes.TryGetValue(callName, out var startTime))
        {
            _logger.LogWarning("Call '{CallName}': No Going start time found", callName);
            return (0, 0, 0);
        }

        // 1. Going 시간 계산 (ms)
        var goingTime = (int)(DateTime.Now - startTime).TotalMilliseconds;
        _goingStartTimes.Remove(callName);

        // 2. 히스토리에 추가
        if (!_goingTimeHistory.ContainsKey(callName))
        {
            _goingTimeHistory[callName] = new List<int>();
        }

        var history = _goingTimeHistory[callName];
        history.Add(goingTime);

        // 최대 크기 유지 (이동 평균)
        if (history.Count > MAX_HISTORY_SIZE)
        {
            history.RemoveAt(0);
        }

        // 3. 평균 계산
        var average = history.Average();

        // 4. 표준편차 계산
        var variance = history.Sum(x => Math.Pow(x - average, 2)) / history.Count;
        var stdDev = Math.Sqrt(variance);

        _logger.LogInformation(
            "Call '{CallName}': Going finished - Time={Time}ms, Avg={Avg:F0}ms, StdDev={StdDev:F0}ms (Samples={Count})",
            callName, goingTime, average, stdDev, history.Count);

        return (goingTime, average, stdDev);
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
