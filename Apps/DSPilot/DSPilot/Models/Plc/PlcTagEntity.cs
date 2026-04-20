namespace DSPilot.Models.Plc;

/// <summary>
/// PLC 태그 엔티티 - plcTag 테이블
/// </summary>
public class PlcTagEntity
{
    /// <summary>
    /// 태그 ID
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// PLC ID (외래키)
    /// </summary>
    public int PlcId { get; set; }

    /// <summary>
    /// 태그 이름
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 태그 주소 (예: DB1.DBX0.0)
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// 데이터 타입 (예: INT, REAL, BOOL)
    /// </summary>
    public string DataType { get; set; } = string.Empty;

    /// <summary>
    /// 부모 PLC
    /// </summary>
    public PlcEntity? Plc { get; set; }

    /// <summary>
    /// 관련 로그 목록
    /// </summary>
    public List<PlcTagLogEntity> Logs { get; set; } = new();
}
