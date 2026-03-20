using Ds2.Core;

namespace DSPilot.Models;

/// <summary>
/// PLC 태그와 Call 매핑 정보
/// PlcToCallMapperService에서 반환하는 타입
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
    /// InTag 여부 (true: InTag, false: OutTag)
    /// </summary>
    public required bool IsInTag { get; init; }

    /// <summary>
    /// Flow 이름
    /// </summary>
    public required string FlowName { get; init; }

    /// <summary>
    /// CallKey 생성
    /// </summary>
    public CallKey ToCallKey()
    {
        return new CallKey(FlowName, Call.Name);
    }
}
