namespace DSPilot.Models;

/// <summary>
/// PLC 통신 이벤트 (Ev2.Backend.PLC CommunicationInfo 래핑)
/// </summary>
public record PlcCommunicationEvent
{
    /// <summary>
    /// 배치 생성 시각 (PLC 스캔 시작 시각)
    /// </summary>
    public required DateTime BatchTimestamp { get; init; }

    /// <summary>
    /// 태그 데이터 목록
    /// </summary>
    public required List<PlcTagData> Tags { get; init; }

    /// <summary>
    /// PLC 이름
    /// </summary>
    public required string PlcName { get; init; }
}

/// <summary>
/// PLC 태그 데이터
/// </summary>
public record PlcTagData
{
    /// <summary>
    /// 태그 주소
    /// </summary>
    public required string Address { get; init; }

    /// <summary>
    /// 태그 값 (bool)
    /// </summary>
    public required bool Value { get; init; }

    /// <summary>
    /// 이전 값 (Rising/Falling Edge 판정용)
    /// </summary>
    public required bool PreviousValue { get; init; }
}
