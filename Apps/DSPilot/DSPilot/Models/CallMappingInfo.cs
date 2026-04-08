using Ds2.Core;
using CallDirection = DSPilot.Engine.Tracking.CallDirection;

namespace DSPilot.Models;

/// <summary>
/// PLC 태그와 Call 매핑 정보
/// PlcToCallMapperService에서 반환하는 타입
///
/// [InTag / OutTag 방향 기준: PLC 제어기 관점]
///   - OutTag (IsInTag=false): PLC가 장비로 출력(DO)하는 신호 (명령)
///   - InTag  (IsInTag=true):  장비에서 PLC로 입력(DI)되는 신호 (응답)
/// </summary>
public record CallMappingInfo
{
    /// <summary>
    /// 매핑된 Call 엔티티
    /// </summary>
    public required Call Call { get; init; }

    /// <summary>
    /// 매핑된 ApiCall 엔티티
    /// </summary>
    public required ApiCall ApiCall { get; init; }

    /// <summary>
    /// InTag 여부 (PLC 제어기 관점: true = PLC 입력(DI, 응답), false = PLC 출력(DO, 명령))
    /// </summary>
    public required bool IsInTag { get; init; }

    /// <summary>
    /// Flow 이름
    /// </summary>
    public required string FlowName { get; init; }

    /// <summary>
    /// Work 이름
    /// </summary>
    public string WorkName { get; init; } = string.Empty;

    /// <summary>
    /// Call Direction (InOut, InOnly, OutOnly)
    /// </summary>
    public CallDirection Direction { get; init; } = CallDirection.None;
}
