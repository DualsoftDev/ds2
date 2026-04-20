namespace DSPilot.Models.Analysis;

/// <summary>
/// 사이클 분석 전체 데이터
/// </summary>
public class CycleAnalysisData
{
    // 사이클 기본 정보
    public DateTime CycleStartTime { get; set; }
    public DateTime CycleEndTime { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public int CallCount { get; set; }
    public string FlowName { get; set; } = "";

    // 사이클 경계 정보 (자동 탐지된 경우)
    public CycleBoundary? Boundary { get; set; }

    // 동작 목록 (시간순 정렬)
    public List<CallExecutionInfo> CallSequence { get; set; } = new();

    // Gap 분석
    public List<GapInfo> Gaps { get; set; } = new();
    public List<GapInfo> TopLongGaps { get; set; } = new();  // Top 3
    public TimeSpan TotalGapDuration { get; set; }

    // 장치별 분석
    public Dictionary<string, DeviceTimeInfo> DeviceStats { get; set; } = new();

    // 병목 탐지
    public List<BottleneckInfo> Bottlenecks { get; set; } = new();

    // 성능 지표
    public PerformanceMetrics Metrics { get; set; } = new();
}

/// <summary>
/// 성능 지표
/// </summary>
public class PerformanceMetrics
{
    public TimeSpan TotalActiveTime { get; set; }   // 총 동작 시간 (모든 Call Duration 합)
    public TimeSpan TotalIdleTime { get; set; }     // 총 비가동 시간 (모든 Gap 합)
    public double UtilizationRate { get; set; }     // 가동률 (Active / Total * 100)
    public double Throughput { get; set; }          // 처리율 (CallCount / TotalDuration.TotalMinutes)
    public TimeSpan AverageCycleTime { get; set; }  // 평균 사이클 시간 (단일 사이클인 경우 = TotalDuration)
    public int ParallelExecutions { get; set; }     // 병렬 실행 횟수
}

/// <summary>
/// Call 실행 정보
/// </summary>
public class CallExecutionInfo
{
    public int SequenceNumber { get; set; }        // 실행 순서 (1, 2, 3...)
    public string CallName { get; set; } = "";
    public string FlowName { get; set; } = "";
    public string DeviceName { get; set; } = "";   // Station/Device 이름

    public DateTime StartTime { get; set; }        // 절대 시작 시간
    public DateTime EndTime { get; set; }          // 절대 종료 시간
    public TimeSpan Duration { get; set; }         // 소요 시간

    public TimeSpan RelativeStartTime { get; set; } // 사이클 시작 대비 상대 시간
    public TimeSpan GapFromPrevious { get; set; }   // 이전 Call과의 Gap

    public CallState State { get; set; }            // Running/Completed/Error
}

/// <summary>
/// Call 상태
/// </summary>
public enum CallState
{
    Unknown,
    Running,
    Completed,
    Error
}

/// <summary>
/// Gap (대기 시간) 정보
/// </summary>
public class GapInfo
{
    public int Rank { get; set; }                   // 순위 (1, 2, 3...)
    public string PreviousCall { get; set; } = "";
    public string NextCall { get; set; } = "";
    public TimeSpan GapDuration { get; set; }
    public DateTime GapStartTime { get; set; }
    public DateTime GapEndTime { get; set; }
    public bool IsBottleneck { get; set; }          // 병목 여부
}

/// <summary>
/// 장치별 시간 정보
/// </summary>
public class DeviceTimeInfo
{
    public string DeviceName { get; set; } = "";
    public TimeSpan TotalTime { get; set; }         // 총 동작 시간
    public double PercentageOfCycle { get; set; }   // 전체 사이클 대비 %
    public int CallCount { get; set; }              // 동작 횟수
    public TimeSpan AverageTime { get; set; }       // 평균 시간
    public TimeSpan MinTime { get; set; }
    public TimeSpan MaxTime { get; set; }
}

/// <summary>
/// 병목 정보
/// </summary>
public class BottleneckInfo
{
    public string CallName { get; set; } = "";
    public string Reason { get; set; } = "";        // "2배 이상 긴 동작", "반복 지연"
    public TimeSpan Duration { get; set; }
    public TimeSpan ExpectedDuration { get; set; }  // 평균 또는 목표 시간
    public double DelayRatio { get; set; }          // 지연 배율
}
