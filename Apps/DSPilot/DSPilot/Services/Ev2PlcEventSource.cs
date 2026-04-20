using System.Reactive.Subjects;
using DSPilot.Abstractions;
using DSPilot.Models;

namespace DSPilot.Services;

/// <summary>
/// Ev2.Backend.PLC 기반 PLC 이벤트 소스 (모의 구현)
/// 실제 PLC 연결은 Ev2.Backend.PLC DLL의 타입을 정확히 매핑한 후 구현 필요
/// </summary>
public class Ev2PlcEventSource : IPlcEventSource
{
    private readonly ILogger<Ev2PlcEventSource> _logger;
    private readonly PlcConnectionConfig _config;
    private readonly Subject<PlcCommunicationEvent> _eventSubject = new();
    private Timer? _simulationTimer;

    public Ev2PlcEventSource(
        ILogger<Ev2PlcEventSource> logger,
        PlcConnectionConfig config)
    {
        _logger = logger;
        _config = config;
    }

    /// <inheritdoc />
    public IObservable<PlcCommunicationEvent> Events => _eventSubject;

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Ev2PlcEventSource: Using mock implementation");
        _logger.LogInformation("Mock PLC connection: {PlcName} ({IpAddress})",
            _config.PlcName, _config.IpAddress);

        IsConnected = true;

        // 모의 데이터 생성 (주기적으로 이벤트 발생)
        _simulationTimer = new Timer(_ =>
        {
            try
            {
                var tags = _config.TagAddresses.Select(addr => new PlcTagData
                {
                    Address = addr,
                    Value = Random.Shared.Next(0, 2) == 1,
                    PreviousValue = Random.Shared.Next(0, 2) == 1
                }).ToList();

                var ev = new PlcCommunicationEvent
                {
                    BatchTimestamp = DateTime.Now,
                    Tags = tags,
                    PlcName = _config.PlcName
                };

                _eventSubject.OnNext(ev);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mock PLC event generation failed");
            }
        }, null, TimeSpan.FromMilliseconds(_config.ScanIntervalMs),
                  TimeSpan.FromMilliseconds(_config.ScanIntervalMs));

        _logger.LogInformation("Mock PLC connection started");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Stopping mock PLC connection");

        _simulationTimer?.Dispose();
        _simulationTimer = null;

        IsConnected = false;

        _logger.LogInformation("Mock PLC connection stopped");

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _eventSubject.Dispose();
    }
}

/// <summary>
/// PLC 연결 설정
/// </summary>
public record PlcConnectionConfig
{
    public required string PlcName { get; init; }
    public required string IpAddress { get; init; }
    public required int ScanIntervalMs { get; init; }
    public required List<string> TagAddresses { get; init; }
}
