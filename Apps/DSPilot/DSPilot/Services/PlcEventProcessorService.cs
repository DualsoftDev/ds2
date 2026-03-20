using System.Threading.Channels;
using DSPilot.Abstractions;
using DSPilot.Models;

namespace DSPilot.Services;

/// <summary>
/// PLC 이벤트 처리 서비스 (Channel 기반 백프레셔)
/// Producer: IPlcEventSource → Channel
/// Consumer: Channel → PlcToCallMapperService → State Transition
/// </summary>
public class PlcEventProcessorService : BackgroundService
{
    private readonly ILogger<PlcEventProcessorService> _logger;
    private readonly IPlcEventSource _plcEventSource;
    private readonly PlcToCallMapperService _callMapper;
    private readonly InMemoryCallStateStore _stateStore;
    private readonly CallStatisticsService _statisticsService;
    private readonly IServiceScopeFactory _scopeFactory;

    // Channel 설정: Bounded + Wait 전략 (백프레셔)
    private readonly Channel<PlcCommunicationEvent> _eventChannel;
    private readonly ChannelReader<PlcCommunicationEvent> _reader;
    private readonly ChannelWriter<PlcCommunicationEvent> _writer;

    private IDisposable? _plcSubscription;

    public PlcEventProcessorService(
        ILogger<PlcEventProcessorService> logger,
        IPlcEventSource plcEventSource,
        PlcToCallMapperService callMapper,
        InMemoryCallStateStore stateStore,
        CallStatisticsService statisticsService,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _plcEventSource = plcEventSource;
        _callMapper = callMapper;
        _stateStore = stateStore;
        _statisticsService = statisticsService;
        _scopeFactory = scopeFactory;

        // Channel 생성 (용량: 100, Full일 때 대기)
        _eventChannel = Channel.CreateBounded<PlcCommunicationEvent>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
        _reader = _eventChannel.Reader;
        _writer = _eventChannel.Writer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PlcEventProcessorService starting...");

        try
        {
            // CallMapper 초기화
            _callMapper.Initialize();

            // PLC 연결 시작
            await _plcEventSource.StartAsync(stoppingToken);

            // Producer: PLC 이벤트 → Channel
            _plcSubscription = _plcEventSource.Events.Subscribe(
                onNext: async plcEvent =>
                {
                    try
                    {
                        // WriteAsync: Channel이 full이면 대기 (백프레셔)
                        await _writer.WriteAsync(plcEvent, stoppingToken);
                        _logger.LogDebug("PLC event queued: {TagCount} tags", plcEvent.Tags.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to queue PLC event");
                    }
                },
                onError: ex =>
                {
                    _logger.LogError(ex, "PLC event stream error");
                    _writer.Complete(ex);
                },
                onCompleted: () =>
                {
                    _logger.LogInformation("PLC event stream completed");
                    _writer.Complete();
                });

            // Consumer: Channel → 순차 처리 (단일 스레드)
            await foreach (var plcEvent in _reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessPlcEventAsync(plcEvent, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process PLC event");
                }
            }

            _logger.LogInformation("PlcEventProcessorService stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PlcEventProcessorService failed");
            throw;
        }
        finally
        {
            _plcSubscription?.Dispose();
            await _plcEventSource.StopAsync(stoppingToken);
        }
    }

    /// <summary>
    /// PLC 이벤트 처리 (순차 실행)
    /// </summary>
    private async Task ProcessPlcEventAsync(PlcCommunicationEvent plcEvent, CancellationToken cancellationToken)
    {
        var batchTimestamp = plcEvent.BatchTimestamp;

        foreach (var tagData in plcEvent.Tags)
        {
            // Rising Edge 감지 (PreviousValue: false → Value: true)
            var isRisingEdge = !tagData.PreviousValue && tagData.Value;
            if (!isRisingEdge)
            {
                continue;
            }

            // Tag → Call 매핑
            var callInfo = _callMapper.FindCallByTag("", tagData.Address);
            if (callInfo == null)
            {
                _logger.LogDebug("No Call mapped for tag: {Address}", tagData.Address);
                continue;
            }

            var call = callInfo.Call;
            var callId = call.Id;
            var isInTag = callInfo.IsInTag;
            var flowName = callInfo.FlowName;

            _logger.LogDebug("Rising Edge detected: {Address} → Call {CallName} (ID: {CallId}, {InOut})",
                tagData.Address, call.Name, callId, isInTag ? "In" : "Out");

            // 현재 Call 상태 조회
            var currentState = await _stateStore.GetCallStateAsync(callId);
            var stateValue = currentState?.State ?? "Ready";

            // 상태 천이 로직
            string? newState = null;

            if (isInTag && stateValue == "Ready")
            {
                // In Tag Rising → Ready → Going
                newState = "Going";
                await _statisticsService.RecordGoingStartAsync(callId, call.Name);

                _logger.LogInformation("Call '{CallName}' (ID: {CallId}) state transition: Ready → Going (BatchTimestamp: {Time})",
                    call.Name, callId, batchTimestamp);
            }
            else if (!isInTag && stateValue == "Going")
            {
                // Out Tag Rising → Going → Finish
                var (startTime, finishTime, goingTime, average, stdDev, goingCount) =
                    _statisticsService.RecordGoingFinish(call.Name);

                newState = "Finish";

                // 통계 업데이트 (메모리 + DB)
                await _stateStore.UpdateCallWithStatisticsAsync(
                    callId, newState, goingTime, average, stdDev, goingCount);

                _logger.LogInformation(
                    "Call '{CallName}' (ID: {CallId}) state transition: Going → Finish | " +
                    "GoingTime: {GoingTime}ms, Avg: {Avg:F0}ms, StdDev: {StdDev:F0}ms, Count: {Count} (BatchTimestamp: {Time})",
                    call.Name, callId, goingTime, average, stdDev, goingCount, batchTimestamp);

                // Finish → Ready 자동 천이
                newState = "Ready";
            }

            // 상태 업데이트
            if (newState != null)
            {
                await _stateStore.UpdateCallStateAsync(callId, newState);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PlcEventProcessorService stopping...");

        _plcSubscription?.Dispose();
        _writer.Complete();

        await base.StopAsync(cancellationToken);
    }
}
