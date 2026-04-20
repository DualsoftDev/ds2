using DSPilot.Abstractions;
using DSPilot.Models;
using Ev2.Backend.Common;
using Ev2.Backend.PLC;
using Ev2.PLC.Protocol.AB;
using Microsoft.FSharp.Core;
using System.Linq;
using System.Reactive.Subjects;
using static Ev2.PLC.Common.TagSpecModule;
using PlcDataType = Ev2.PLC.Common.CoreDataTypesModule.PlcDataType;
using PlcValue = Ev2.PLC.Common.CoreDataTypesModule.PlcValue;
using TagSpec = Ev2.PLC.Common.TagSpecModule.TagSpec;
using WAL = Ev2.PLC.Common.TagSpecModule.WAL;

namespace DSPilot.Services;

/// <summary>
/// Ev2.Backend.PLC PLCBackendService 기반 실제 PLC 이벤트 소스
/// </summary>
public class Ev2PlcEventSourceReal : IPlcEventSource
{
    private readonly ILogger<Ev2PlcEventSourceReal> _logger;
    private readonly PlcConnectionConfig _config;
    private readonly IConfiguration _configuration;
    private readonly Subject<PlcCommunicationEvent> _eventSubject = new();
    private PLCBackendService? _plcService;
    private IDisposable? _scanDisposable;
    private Timer? _pollingTimer;
    private Dictionary<string, bool> _previousValues = new();

    public Ev2PlcEventSourceReal(
        ILogger<Ev2PlcEventSourceReal> logger,
        PlcConnectionConfig config,
        IConfiguration configuration)
    {
        _logger = logger;
        _config = config;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public IObservable<PlcCommunicationEvent> Events => _eventSubject;

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Ev2 PLC connection: {PlcName} ({IpAddress})",
            _config.PlcName, _config.IpAddress);

        try
        {
            // Step 1: IConnectionConfiguration 생성 (Allen-Bradley 예제)
            var connectionConfig = AbConnectionConfig.Create(
                ipAddress: _config.IpAddress,
                port: FSharpOption<int>.None,
                name: FSharpOption<string>.Some(_config.PlcName),
                plcType: FSharpOption<Ev2.PLC.Protocol.AB.PlcType>.None,
                slot: FSharpOption<byte>.Some((byte)0),
                scanInterval: FSharpOption<TimeSpan>.Some(TimeSpan.FromMilliseconds(_config.ScanIntervalMs)),
                timeout: FSharpOption<TimeSpan>.Some(TimeSpan.FromSeconds(5)),
                maxRetries: FSharpOption<int>.Some(3),
                retryDelay: FSharpOption<TimeSpan>.Some(TimeSpan.FromMilliseconds(100))
            );

            // Step 2: TagSpec 배열 생성
            var tagSpecs = _config.TagAddresses.Select(addr => new TagSpec(
                name: addr,
                address: addr,
                dataType: PlcDataType.Bool,  // 일단 모두 Bool로 가정
                walType: FSharpOption<WAL>.None,
                comment: FSharpOption<string>.None,
                everyNScan: FSharpOption<int>.None,
                directionHint: FSharpOption<DirectionHint>.None,
                plcValue: FSharpOption<PlcValue>.None
            )).ToArray();

            _logger.LogInformation("Created {Count} TagSpecs", tagSpecs.Length);

            // Step 3: ScanConfiguration 생성
            var scanConfigs = new[]
            {
                new ScanConfiguration(connectionConfig, tagSpecs)
            };

            // Step 4: TagHistoricWAL 생성 (선택사항 - 이력 관리용)
            var memoryBuffer = new MemoryWalBuffer();
            var walFilePath = Path.Combine(Path.GetTempPath(), $"dspilot_wal_{_config.PlcName}.db");
            var fileBuffer = new FileWalBuffer(walFilePath);

            // appsettings.json에서 TagHistoric 설정 읽기
            var walBufferSize = _configuration.GetValue<int?>("TagHistoric:WALBufferSize") ?? 100;
            var flushValue = _configuration["TagHistoric:FlushInterval"];
            var flushInterval = TimeSpan.TryParse(flushValue, out var configuredInterval) && configuredInterval > TimeSpan.Zero
                ? configuredInterval
                : TimeSpan.FromSeconds(1);

            var tagHistoricWAL = new TagHistoricWAL(
                walSize: walBufferSize,
                flushInterval: flushInterval,
                memoryBuffer: memoryBuffer,
                diskBuffer: fileBuffer
            );

            _logger.LogInformation("Created TagHistoricWAL: Path={Path}, BufferSize={BufferSize}, FlushInterval={FlushInterval}",
                walFilePath, walBufferSize, flushInterval);

            // Step 5: PLCBackendService 생성
            _plcService = new PLCBackendService(
                scanConfigs: scanConfigs,
                tagHistoricWAL: FSharpOption<TagHistoricWAL>.Some(tagHistoricWAL)
            );

            _logger.LogInformation("Created PLCBackendService");

            // Step 6: 서비스 시작
            _scanDisposable = _plcService.Start();
            IsConnected = true;

            _logger.LogInformation("PLCBackendService started");

            // Step 7: Polling을 통해 태그 값 읽기 및 변경 감지
            StartPolling();

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Ev2 PLC connection");
            IsConnected = false;
            throw;
        }
    }

    private void StartPolling()
    {
        _pollingTimer = new Timer(_ =>
        {
            try
            {
                PollTagValues();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during tag polling");
            }
        }, null, TimeSpan.FromMilliseconds(_config.ScanIntervalMs),
                  TimeSpan.FromMilliseconds(_config.ScanIntervalMs));

        _logger.LogInformation("Tag polling started (interval: {Interval}ms)", _config.ScanIntervalMs);
    }

    private void PollTagValues()
    {
        if (_plcService == null || !IsConnected)
            return;

        var tags = new List<PlcTagData>();
        var batchTimestamp = DateTime.Now;

        foreach (var tagAddress in _config.TagAddresses)
        {
            try
            {
                var result = _plcService.RTryReadTagValue(_config.PlcName, tagAddress);

                // F# Result 처리 - FSharpChoice.Choice1Of2 = Ok, Choice2Of2 = Error
                var choice = Microsoft.FSharp.Core.FSharpChoice<PlcValue, string>.NewChoice1Of2;

                if (result.IsOk)
                {
                    // ResultValue는 Ok 케이스의 값을 가져옴
                    var plcValue = result.ResultValue;
                    var currentValue = ConvertPlcValueToBool(plcValue);
                    var previousValue = _previousValues.GetValueOrDefault(tagAddress, false);

                    tags.Add(new PlcTagData
                    {
                        Address = tagAddress,
                        Value = currentValue,
                        PreviousValue = previousValue
                    });

                    _previousValues[tagAddress] = currentValue;
                }
                else
                {
                    // ErrorValue는 Error 케이스의 값을 가져옴
                    var error = result.ErrorValue;
                    _logger.LogWarning("Failed to read tag {Tag}: {Error}", tagAddress, error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception reading tag {Tag}", tagAddress);
            }
        }

        if (tags.Any())
        {
            var ev = new PlcCommunicationEvent
            {
                BatchTimestamp = batchTimestamp,
                Tags = tags,
                PlcName = _config.PlcName
            };

            _eventSubject.OnNext(ev);
        }
    }

    private bool ConvertPlcValueToBool(PlcValue plcValue)
    {
        // PlcValue는 F# discriminated union
        // 여기서는 간단히 처리 - 실제로는 PlcValue의 케이스를 확인해야 함
        // PlcValue.Bool, PlcValue.Int, PlcValue.Real 등이 있을 것으로 예상

        // 임시 구현: ToString()으로 확인
        var str = plcValue.ToString();
        if (str.Contains("True", StringComparison.OrdinalIgnoreCase))
            return true;
        if (str.Contains("False", StringComparison.OrdinalIgnoreCase))
            return false;

        // 숫자 값이면 0이 아니면 true
        if (int.TryParse(str, out var intValue))
            return intValue != 0;

        return false;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Stopping Ev2 PLC connection");

        _pollingTimer?.Dispose();
        _pollingTimer = null;

        if (_plcService != null && IsConnected)
        {
            try
            {
                _plcService.Stop(_config.PlcName);
                _scanDisposable?.Dispose();
                _scanDisposable = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping PLC service");
            }
        }

        IsConnected = false;

        _logger.LogInformation("Ev2 PLC connection stopped");

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _eventSubject.Dispose();
    }
}
