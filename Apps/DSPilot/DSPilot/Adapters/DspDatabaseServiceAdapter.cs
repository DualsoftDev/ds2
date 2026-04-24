using Ds2.Core;
using DSPilot.Infrastructure;
using DSPilot.Models.Dsp;
using DSPilot.Repositories;
using DSPilot.Services;

namespace DSPilot.Adapters;

/// <summary>
/// DSP DB 초기화 IHostedService.
/// AASX 프로젝트에서 Flow/Call을 읽어 dspFlow/dspCall에 초기 적재.
/// F# DatabaseInitialization.AasxLoader의 pure C# 포팅.
/// </summary>
public class DspDatabaseServiceAdapter : BackgroundService
{
    private const int MaxRetries = 30;
    private const int RetryDelayMs = 2000;

    private readonly ILogger<DspDatabaseServiceAdapter> _logger;
    private readonly DatabasePaths _paths;
    private readonly DsProjectService _projectService;
    private readonly PlcToCallMapperService _mapper;
    private readonly IFlowMetricsService _flowMetricsService;
    private readonly IDspRepository _dspRepository;

    public DspDatabaseServiceAdapter(
        ILogger<DspDatabaseServiceAdapter> logger,
        DatabasePathResolverAdapter pathResolver,
        DsProjectService projectService,
        PlcToCallMapperService mapper,
        IFlowMetricsService flowMetricsService,
        IDspRepository dspRepository)
    {
        _logger = logger;
        _paths = pathResolver.GetDatabasePaths();
        _projectService = projectService;
        _mapper = mapper;
        _flowMetricsService = flowMetricsService;
        _dspRepository = dspRepository;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!_paths.DspTablesEnabled)
            {
                _logger.LogInformation("DspTables:Enabled=false, skipping AASX load and DSP DB initialization.");
                await WaitForCancellationAsync(stoppingToken);
                return;
            }

            var success = await InitializeFromAasxWithRetryAsync(stoppingToken);

            if (success)
            {
                _logger.LogInformation("Initializing PlcToCallMapper...");
                _mapper.Initialize();

                _logger.LogInformation("Initializing FlowMetricsService...");
                await _flowMetricsService.InitializeAsync();
            }

            await WaitForCancellationAsync(stoppingToken);
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("DSP Database Service stopping gracefully");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("DSP Database Service operation cancelled");
        }
    }

    private async Task<bool> InitializeFromAasxWithRetryAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation("Attempt {Attempt}/{MaxRetries}: Loading data from AASX...", attempt, MaxRetries);

                var (flowCount, callCount) = await InitializeFromAasxAsync();

                if (flowCount > 0 || callCount > 0)
                {
                    _logger.LogInformation("Successfully loaded {FlowCount} flows and {CallCount} calls from AASX", flowCount, callCount);
                    return true;
                }

                _logger.LogWarning(
                    "No data was loaded (flowCount={FlowCount}, callCount={CallCount}). Schema may not be ready yet.",
                    flowCount, callCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Attempt {Attempt}/{MaxRetries} failed: {Message}", attempt, MaxRetries, ex.Message);
            }

            if (attempt < MaxRetries)
            {
                _logger.LogInformation("Waiting {DelayMs}ms before retry...", RetryDelayMs);
                await Task.Delay(RetryDelayMs, stoppingToken);
            }
        }

        return false;
    }

    private async Task<(int flowCount, int callCount)> InitializeFromAasxAsync()
    {
        var allFlows = _projectService.GetAllFlows().ToList();
        _logger.LogInformation("Total flows in AASX: {Count}", allFlows.Count);

        var filteredFlows = allFlows
            .Where(f => !f.Name.EndsWith("_Flow", StringComparison.OrdinalIgnoreCase))
            .ToList();
        _logger.LogInformation("Filtered flows (excluding '*_Flow'): {Count}", filteredFlows.Count);

        var flowEntities = CreateFlowEntities(allFlows);
        var flowCount = await _dspRepository.BulkInsertFlowsAsync(flowEntities);
        _logger.LogInformation("BulkInsertFlowsAsync returned: {Count} flows (expected: {Expected})", flowCount, flowEntities.Count);

        var callEntities = CreateCallEntities(filteredFlows);
        var callCount = await _dspRepository.BulkInsertCallsAsync(callEntities);
        _logger.LogInformation("BulkInsertCallsAsync returned: {Count} calls (expected: {Expected})", callCount, callEntities.Count);

        return (flowCount, callCount);
    }

    private static List<DspFlowEntity> CreateFlowEntities(IEnumerable<Flow> flows)
    {
        var now = DateTime.UtcNow;
        return flows
            .Where(f => !f.Name.EndsWith("_Flow", StringComparison.OrdinalIgnoreCase))
            .Select(f => new DspFlowEntity
            {
                FlowName = f.Name,
                State = "Ready",
                CreatedAt = now,
                UpdatedAt = now,
            })
            .ToList();
    }

    private List<DspCallEntity> CreateCallEntities(IEnumerable<Flow> flows)
    {
        var now = DateTime.UtcNow;
        var list = new List<DspCallEntity>();

        foreach (var flow in flows)
        {
            foreach (var work in _projectService.GetWorks(flow.Id))
            {
                foreach (var call in _projectService.GetCalls(work.Id))
                {
                    var apiCallName = call.ApiCalls.Count > 0
                        ? call.ApiCalls[0].Name
                        : call.ApiName;

                    list.Add(new DspCallEntity
                    {
                        CallId = call.Id,
                        CallName = call.Name,
                        ApiCall = apiCallName,
                        WorkName = flow.Name,  // Use Flow name instead of Work name (F# parity)
                        FlowName = flow.Name,
                        State = "Ready",
                        ProgressRate = 0.0,
                        GoingCount = 0,
                        Device = string.IsNullOrEmpty(call.DevicesAlias) ? null : call.DevicesAlias,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });

                    foreach (var apiCall in call.ApiCalls)
                    {
                        var inTagInfo = apiCall.InTag.IsSome()
                            ? $"Name={apiCall.InTag.Value.Name}, Address={apiCall.InTag.Value.Address}"
                            : "(none)";
                        var outTagInfo = apiCall.OutTag.IsSome()
                            ? $"Name={apiCall.OutTag.Value.Name}, Address={apiCall.OutTag.Value.Address}"
                            : "(none)";

                        _logger.LogDebug(
                            "Call '{CallName}' (Flow: {FlowName}) - ApiCall: {ApiCallName}, InTag: [{InTag}], OutTag: [{OutTag}]",
                            call.Name, flow.Name, apiCall.Name, inTagInfo, outTagInfo);
                    }
                }
            }
        }

        return list;
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

file static class FSharpOptionHelpers
{
    public static bool IsSome<T>(this Microsoft.FSharp.Core.FSharpOption<T> option)
        => Microsoft.FSharp.Core.FSharpOption<T>.get_IsSome(option);
}
