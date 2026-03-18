namespace DSPilot.Models;

/// <summary>
/// PlcDataReaderService가 Call 상태를 변경할 때 즉시 발행하는 인메모리 이벤트.
/// Channel&lt;T&gt;를 통해 DspDbService로 전달되어 DB 폴링 없이 스냅샷을 즉시 갱신한다.
/// </summary>
public record CallStateChangedEvent
{
    public required string CallName         { get; init; }
    public required string NewState         { get; init; }
    public int?    GoingCount               { get; init; }
    public double? AverageGoingTime         { get; init; }
    public int?    PreviousGoingTime        { get; init; }
    public DateTimeOffset OccurredAt        { get; init; } = DateTimeOffset.UtcNow;
}
