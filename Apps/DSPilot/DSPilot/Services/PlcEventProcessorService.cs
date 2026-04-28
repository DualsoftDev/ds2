using System.Threading.Channels;
using DSPilot.Abstractions;
using DSPilot.Models;
using DSPilot.Repositories;
using DSPilot.Adapters;
using Ds2.Core;

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
    private readonly PlcTagStateTrackerService _tagStateTracker;
    private readonly IDatabasePathResolver _pathResolver;
    private readonly CallStateNotificationService _notificationService;
    private readonly InMemoryCallStateStore _stateStore;
    private readonly CallStatisticsService _statisticsService;
    private readonly StateTransitionService _stateTransition;
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
        PlcTagStateTrackerService tagStateTracker,
        IDatabasePathResolver pathResolver,
        CallStateNotificationService notificationService,
        InMemoryCallStateStore stateStore,
        CallStatisticsService statisticsService,
        StateTransitionService stateTransition,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _plcEventSource = plcEventSource;
        _callMapper = callMapper;
        _tagStateTracker = tagStateTracker;
        _pathResolver = pathResolver;
        _notificationService = notificationService;
        _stateStore = stateStore;
        _statisticsService = statisticsService;
        _stateTransition = stateTransition;
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
                        _logger.LogInformation("✓ PLC event queued: {TagCount} tags", plcEvent.Tags.Count);
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
    /// PLC 이벤트 처리 (F# StateTransition 사용)
    /// </summary>
    private async Task ProcessPlcEventAsync(PlcCommunicationEvent plcEvent, CancellationToken cancellationToken)
    {
        foreach (var tagData in plcEvent.Tags)
        {
            // 1. TagStateTracker로 Edge 감지
            var edgeState = _tagStateTracker.UpdateTagValue(tagData.Address, tagData.Value ? "1" : "0");

            if (edgeState.EdgeType != EdgeType.RisingEdge && edgeState.EdgeType != EdgeType.FallingEdge)
            {
                continue; // NoChange는 무시
            }

            _logger.LogInformation("⚡ Edge detected: {TagAddress} = {Value}, EdgeType = {EdgeType}",
                tagData.Address, tagData.Value, edgeState.EdgeType);

            // 2. Call 매핑 조회
            var mapping = _callMapper.FindCallByTag("", tagData.Address);
            if (mapping == null)
            {
                _logger.LogTrace("No Call mapping for tag: {TagAddress}", tagData.Address);
                continue;
            }

            // 3. F# StateTransition 호출
            try
            {
                var dbPath = (_pathResolver as DatabasePathResolverAdapter)?.GetDatabasePaths().SharedDbPath
                    ?? _pathResolver.GetDspDbPath();

                await _stateTransition.ProcessEdgeEventAsync(
                    dbPath,
                    tagData.Address,
                    mapping.IsInTag,
                    edgeState.EdgeType,
                    DateTime.Now,
                    mapping.Call.Name
                );

                _logger.LogInformation(
                    "State transition triggered: Call={CallName}, Tag={TagAddress}, IsInTag={IsInTag}, Edge={EdgeType}",
                    mapping.Call.Name, tagData.Address, mapping.IsInTag, edgeState.EdgeType);

                // 4. 상태 변경 알림 발송 (현재 상태만 전송, 이전 상태는 구독자가 추적)
                _notificationService.NotifyStateChanged(
                    mapping.Call.Name,
                    "unknown", // TODO: 이전 상태 추적 필요 시 InMemoryCallStateStore 활용
                    "transitioned", // StateTransition이 상태를 결정하므로 여기서는 일반 상태 표시
                    DateTime.Now
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process state transition for Call: {CallName}", mapping.Call.Name);
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
