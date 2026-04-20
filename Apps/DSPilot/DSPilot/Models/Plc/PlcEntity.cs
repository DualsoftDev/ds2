namespace DSPilot.Models.Plc;

/// <summary>
/// PLC 엔티티 - plc 테이블
/// </summary>
public class PlcEntity
{
    /// <summary>
    /// PLC ID
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// 프로젝트 ID
    /// </summary>
    public int? ProjectId { get; set; }

    /// <summary>
    /// PLC 이름
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 연결 정보 (JSON)
    /// </summary>
    public string Connection { get; set; } = string.Empty;

    /// <summary>
    /// 관련 태그 목록
    /// </summary>
    public List<PlcTagEntity> Tags { get; set; } = new();
}
