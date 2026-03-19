using DSPilot.Models;

namespace DSPilot.Abstractions;

/// <summary>
/// PLC 이벤트 소스 인터페이스 (Ev2.Backend.PLC 추상화)
/// </summary>
public interface IPlcEventSource : IDisposable
{
    /// <summary>
    /// PLC 통신 이벤트 스트림
    /// </summary>
    IObservable<PlcCommunicationEvent> Events { get; }

    /// <summary>
    /// PLC 연결 시작
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// PLC 연결 중지
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 현재 연결 상태
    /// </summary>
    bool IsConnected { get; }
}
