namespace DSPilot.Models.Dsp;

/// <summary>
/// Call 엔티티 - dsp.db의 Call 테이블 (통계 필드 포함)
/// </summary>
public class DspCallEntity
{
    /// <summary>
    /// Call ID (자동 증가)
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Call 고유 식별자 (AASX Call.Id)
    /// </summary>
    public Guid CallId { get; set; }

    /// <summary>
    /// Call 이름 (Device.Api 형식)
    /// </summary>
    public string CallName { get; set; } = string.Empty;

    /// <summary>
    /// ApiCall 이름
    /// </summary>
    public string ApiCall { get; set; } = string.Empty;

    /// <summary>
    /// Work 이름
    /// </summary>
    public string WorkName { get; set; } = string.Empty;

    /// <summary>
    /// Flow 이름 (FK)
    /// </summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>
    /// 다음 Call
    /// </summary>
    public string? Next { get; set; }

    /// <summary>
    /// 이전 Call
    /// </summary>
    public string? Prev { get; set; }

    /// <summary>
    /// 자동 전제 조건
    /// </summary>
    public string? AutoPre { get; set; }

    /// <summary>
    /// 공통 전제 조건
    /// </summary>
    public string? CommonPre { get; set; }

    /// <summary>
    /// Call 상태 (Ready/Going/Finish/Error)
    /// </summary>
    public string State { get; set; } = "Ready";

    /// <summary>
    /// 진행률 (0.0 ~ 100.0)
    /// </summary>
    public double ProgressRate { get; set; } = 0.0;

    /// <summary>
    /// 마지막 Going 시간 (ms)
    /// </summary>
    public int? PreviousGoingTime { get; set; }

    /// <summary>
    /// 평균 Going 시간 (ms)
    /// </summary>
    public double? AverageGoingTime { get; set; }

    /// <summary>
    /// Going 시간 표준편차 (ms)
    /// </summary>
    public double? StdDevGoingTime { get; set; }

    /// <summary>
    /// Going 완료 횟수 (통계용)
    /// </summary>
    public int GoingCount { get; set; } = 0;

    /// <summary>
    /// 디바이스 이름
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// 에러 메시지
    /// </summary>
    public string? ErrorText { get; set; }

    /// <summary>
    /// 생성 시간
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 수정 시간
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}
