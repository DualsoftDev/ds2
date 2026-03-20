using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DSPilot.Engine;
using DSPilot.Services;

namespace DSPilot.Adapters;

/// <summary>
/// F# DspDatabaseInit을 C#에서 사용하기 위한 IHostedService Adapter
/// </summary>
public class DspDatabaseServiceAdapter : BackgroundService
{
    private readonly ILogger<DspDatabaseServiceAdapter> _logger;
    private readonly DatabasePaths _paths;
    private readonly DsProjectService _projectService;
    private readonly PlcToCallMapperService _mapper;
    private readonly IFlowMetricsService _flowMetricsService;

    public DspDatabaseServiceAdapter(
        ILogger<DspDatabaseServiceAdapter> logger,
        DatabasePathResolverAdapter pathResolver,
        DsProjectService projectService,
        PlcToCallMapperService mapper,
        IFlowMetricsService flowMetricsService)
    {
        _logger = logger;
        _paths = pathResolver.GetDatabasePaths();
        _projectService = projectService;
        _mapper = mapper;
        _flowMetricsService = flowMetricsService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var getAllFlows = Microsoft.FSharp.Core.FSharpFunc<Microsoft.FSharp.Core.Unit, Microsoft.FSharp.Collections.FSharpList<Ds2.Core.Flow>>
                .FromConverter(_ =>
                {
                    var flows = _projectService.GetAllFlows();
                    return Microsoft.FSharp.Collections.ListModule.OfSeq(flows);
                });

            var getWorks = Microsoft.FSharp.Core.FSharpFunc<Guid, Microsoft.FSharp.Collections.FSharpList<Ds2.Core.Work>>
                .FromConverter(flowId =>
                {
                    var works = _projectService.GetWorks(flowId);
                    return Microsoft.FSharp.Collections.ListModule.OfSeq(works);
                });

            var getCalls = Microsoft.FSharp.Core.FSharpFunc<Guid, Microsoft.FSharp.Collections.FSharpList<Ds2.Core.Call>>
                .FromConverter(workId =>
                {
                    var calls = _projectService.GetCalls(workId);
                    return Microsoft.FSharp.Collections.ListModule.OfSeq(calls);
                });

            var cleanupDatabase = Microsoft.FSharp.Core.FSharpFunc<Microsoft.FSharp.Core.Unit, Task<Microsoft.FSharp.Core.Unit>>
                .FromConverter(_ =>
                {
                    // Cleanup logic if needed
                    return Task.FromResult<Microsoft.FSharp.Core.Unit>(null!);
                });

            var success = await DspDatabaseInit.initializeAsync(
                _paths,
                _logger,
                _projectService.IsLoaded,
                getAllFlows,
                getWorks,
                getCalls,
                cleanupDatabase,
                stoppingToken);

            if (success)
            {
                // PlcToCallMapper 초기화
                _logger.LogInformation("Initializing PlcToCallMapper...");
                _mapper.Initialize();

                // FlowMetricsService 초기화
                _logger.LogInformation("Initializing FlowMetricsService...");
                await _flowMetricsService.InitializeAsync();
            }

            // 백그라운드 서비스는 여기서 종료하지 않고 유지
            // Task.Delay(Timeout.Infinite) 대신 WaitForCancellationAsync 사용
            await WaitForCancellationAsync(stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // Expected when application is shutting down
            _logger.LogInformation("DSP Database Service stopping gracefully");
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation token is triggered
            _logger.LogInformation("DSP Database Service operation cancelled");
        }
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>();
        using (cancellationToken.Register(() => tcs.TrySetResult(true)))
        {
            await tcs.Task;
        }
    }
}
