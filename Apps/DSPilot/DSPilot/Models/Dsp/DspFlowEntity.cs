namespace DSPilot.Models.Dsp;

/// <summary>
/// Flow 엔티티 - dsp.db의 Flow 테이블
/// </summary>
public class DspFlowEntity
{
    /// <summary>
    /// Flow ID (자동 증가)
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Flow 이름 (UNIQUE)
    /// </summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>
    /// Machine Time (ms) - 실제 작업 시간
    /// </summary>
    public int? MT { get; set; }

    /// <summary>
    /// Wait Time (ms) - 대기 시간
    /// </summary>
    public int? WT { get; set; }

    /// <summary>
    /// Cycle Time (ms) - MT + WT
    /// </summary>
    public int? CT { get; set; }

    /// <summary>
    /// Average Machine Time (ms)
    /// </summary>
    public double? AvgMT { get; set; }

    /// <summary>
    /// Average Wait Time (ms)
    /// </summary>
    public double? AvgWT { get; set; }

    /// <summary>
    /// Average Cycle Time (ms)
    /// </summary>
    public double? AvgCT { get; set; }

    /// <summary>
    /// Flow 상태 (Going/Ready)
    /// </summary>
    public string? State { get; set; }

    /// <summary>
    /// 시작 Call 이름
    /// </summary>
    public string? MovingStartName { get; set; }

    /// <summary>
    /// 종료 Call 이름
    /// </summary>
    public string? MovingEndName { get; set; }

    /// <summary>
    /// 생성 시간
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 수정 시간
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}
