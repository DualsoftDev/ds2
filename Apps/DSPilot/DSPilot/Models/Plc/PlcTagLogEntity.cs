namespace DSPilot.Models.Plc;

/// <summary>
/// PLC 태그 로그 엔티티 - plcTagLog 테이블
/// </summary>
public class PlcTagLogEntity
{
    /// <summary>
    /// 로그 ID
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// 태그 ID (외래키)
    /// </summary>
    public int PlcTagId { get; set; }

    /// <summary>
    /// 로그 기록 시간
    /// </summary>
    public DateTime DateTime { get; set; }

    /// <summary>
    /// 태그 값 (TEXT로 저장)
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// 부모 태그
    /// </summary>
    public PlcTagEntity? PlcTag { get; set; }

    /// <summary>
    /// 태그 이름 (조인 시 사용)
    /// </summary>
    public string TagName { get; set; } = string.Empty;

    /// <summary>
    /// 태그 주소 (조인 시 사용)
    /// </summary>
    public string Address { get; set; } = string.Empty;
}
