using Microsoft.Extensions.Logging;
using CSharpDspFlowEntity = DSPilot.Models.Dsp.DspFlowEntity;
using CSharpDspCallEntity = DSPilot.Models.Dsp.DspCallEntity;
using FSharpDspFlowEntity = DSPilot.Engine.DspFlowEntity;
using FSharpDspCallEntity = DSPilot.Engine.DspCallEntity;

namespace DSPilot.Adapters;

/// <summary>
/// F# DspRepository를 C#에서 사용하기 위한 Adapter
/// IDspRepository 인터페이스 유지하여 기존 코드 호환성 보장
/// </summary>
public class DspRepositoryAdapter : Repositories.IDspRepository
{
    private readonly DSPilot.Engine.DatabasePaths _paths;
    private readonly ILogger<DspRepositoryAdapter> _logger;
    private readonly bool _enabled;

    public DspRepositoryAdapter(DSPilot.Engine.DatabasePaths paths, ILogger<DspRepositoryAdapter> logger)
    {
        _paths = paths;
        _logger = logger;
        _enabled = paths.DspTablesEnabled;

        if (!_enabled)
        {
            _logger.LogInformation("DspTables:Enabled=false, DspRepositoryAdapter will operate in no-op mode.");
        }
    }

    public Task<bool> CreateSchemaAsync()
    {
        if (!_enabled) return Task.FromResult(true);
        return DSPilot.Engine.DspRepository.createSchemaAsync(_paths, _logger);
    }

    public async Task<int> BulkInsertFlowsAsync(List<CSharpDspFlowEntity> flows)
    {
        if (!_enabled) return 0;
        var fsharpFlows = Microsoft.FSharp.Collections.ListModule.OfSeq(flows.Select(ToFSharpFlowEntity));
        return await DSPilot.Engine.DspRepository.bulkInsertFlowsAsync(_paths, _logger, fsharpFlows);
    }

    public async Task<int> BulkInsertCallsAsync(List<CSharpDspCallEntity> calls)
    {
        if (!_enabled) return 0;
        var fsharpCalls = Microsoft.FSharp.Collections.ListModule.OfSeq(calls.Select(ToFSharpCallEntity));
        return await DSPilot.Engine.DspRepository.bulkInsertCallsAsync(_paths, _logger, fsharpCalls);
    }

    public async Task<bool> UpdateFlowStateAsync(string flowName, string state)
    {
        if (!_enabled) return false;
        return await DSPilot.Engine.DspRepository.updateFlowStateAsync(_paths, _logger, flowName, state);
    }

    public async Task<bool> HasGoingCallsInFlowAsync(string flowName)
    {
        if (!_enabled) return false;
        return await DSPilot.Engine.DspRepository.hasGoingCallsInFlowAsync(_paths, _logger, flowName);
    }

    public async Task<bool> UpdateFlowMetricsAsync(
        string flowName,
        int? mt,
        int? wt,
        int? ct,
        string? movingStartName,
        string? movingEndName)
    {
        if (!_enabled) return false;
        var mtOpt = ToFSharpOption(mt);
        var wtOpt = ToFSharpOption(wt);
        var ctOpt = ToFSharpOption(ct);
        var startOpt = ToFSharpOption(movingStartName);
        var endOpt = ToFSharpOption(movingEndName);

        return await DSPilot.Engine.DspRepository.updateFlowMetricsAsync(_paths, flowName, mtOpt, wtOpt, ctOpt, startOpt, endOpt);
    }

    public async Task<bool> UpdateFlowCycleBoundariesAsync(
        string flowName,
        string? movingStartName,
        string? movingEndName)
    {
        if (!_enabled) return false;
        var startOpt = ToFSharpOption(movingStartName);
        var endOpt = ToFSharpOption(movingEndName);
        return await DSPilot.Engine.DspRepository.updateFlowCycleBoundariesAsync(_paths, flowName, startOpt, endOpt);
    }

    public Task<bool> ClearAllDataAsync()
    {
        if (!_enabled) return Task.FromResult(true);
        return DSPilot.Engine.DspRepository.clearAllDataAsync(_paths, _logger);
    }

    public Task CleanupDatabaseAsync()
    {
        // F# 모듈에 없는 메서드 - 빈 구현
        return Task.CompletedTask;
    }

    public async Task<List<Repositories.CallStatisticsDto>> GetCallStatisticsAsync()
    {
        if (!_enabled) return new List<Repositories.CallStatisticsDto>();
        var fsharpStats = await DSPilot.Engine.DspRepository.getCallStatisticsAsync(_paths, _logger);
        return fsharpStats.Select(s => new Repositories.CallStatisticsDto
        {
            CallId = s.CallId,
            CallName = s.CallName,
            FlowName = s.FlowName,
            WorkName = s.WorkName,
            AverageGoingTime = s.AverageGoingTime,
            StdDevGoingTime = s.StdDevGoingTime,
            GoingCount = s.GoingCount
        }).ToList();
    }

    // ===== CallId 기반 메서드 (New Primary API) =====

    public async Task<string> GetCallStateAsync(Guid callId)
    {
        if (!_enabled) return "Ready";
        return await DSPilot.Engine.DspRepository.getCallStateByIdAsync(_paths, _logger, callId);
    }

    public async Task<(string WorkName, string FlowName)?> GetCallInfoAsync(Guid callId)
    {
        if (!_enabled) return null;
        var result = await DSPilot.Engine.DspRepository.getCallInfoByIdAsync(_paths, _logger, callId);
        if (Microsoft.FSharp.Core.FSharpOption<System.Tuple<string, string>>.get_IsNone(result))
            return null;
        var tuple = result.Value;
        return (tuple.Item1, tuple.Item2);
    }

    public async Task<CSharpDspCallEntity?> GetCallByIdAsync(Guid callId)
    {
        if (!_enabled) return null;
        var result = await DSPilot.Engine.DspRepository.getCallByIdAsync(_paths, _logger, callId);
        if (Microsoft.FSharp.Core.FSharpOption<FSharpDspCallEntity>.get_IsNone(result))
            return null;
        return ToCSharpCallEntity(result.Value);
    }

    public async Task<bool> UpdateCallStateAsync(Guid callId, string state)
    {
        if (!_enabled) return false;
        return await DSPilot.Engine.DspRepository.updateCallStateByIdAsync(_paths, _logger, callId, state);
    }

    public async Task<bool> UpdateCallWithStatisticsAsync(
        Guid callId,
        string state,
        int previousGoingTime,
        double averageGoingTime,
        double stdDevGoingTime)
    {
        if (!_enabled) return false;
        return await DSPilot.Engine.DspRepository.updateCallWithStatisticsByIdAsync(
            _paths, _logger, callId, state, previousGoingTime, averageGoingTime, stdDevGoingTime);
    }

    // ===== Flow Metrics with Averages =====

    public async Task<bool> UpdateFlowWithAveragesAsync(
        string flowName,
        int mt,
        int wt,
        int ct,
        double avgMT,
        double avgWT,
        double avgCT,
        string? movingStartName,
        string? movingEndName)
    {
        if (!_enabled) return false;
        var startOpt = ToFSharpOption(movingStartName);
        var endOpt = ToFSharpOption(movingEndName);

        return await DSPilot.Engine.DspRepository.updateFlowWithAveragesAsync(
            _paths, _logger, flowName, mt, wt, ct, avgMT, avgWT, avgCT, startOpt, endOpt);
    }

    // ===== Flow History Methods =====

    public async Task<int> InsertFlowHistoryAsync(Models.Dsp.DspFlowHistoryEntity history)
    {
        if (!_enabled) return 0;
        var fsharpHistory = ToFSharpFlowHistoryEntity(history);
        return await DSPilot.Engine.DspRepository.insertFlowHistoryAsync(_paths, _logger, fsharpHistory);
    }

    public async Task<List<Models.Dsp.DspFlowHistoryEntity>> GetFlowHistoryAsync(string flowName, int limit)
    {
        if (!_enabled) return new List<Models.Dsp.DspFlowHistoryEntity>();
        var fsharpList = await DSPilot.Engine.DspRepository.getFlowHistoryAsync(_paths, _logger, flowName, limit);
        return fsharpList.Select(ToCSharpFlowHistoryEntity).ToList();
    }

    public async Task<List<Models.Dsp.DspFlowHistoryEntity>> GetFlowHistoryByDaysAsync(string flowName, int days)
    {
        if (!_enabled) return new List<Models.Dsp.DspFlowHistoryEntity>();
        var fsharpList = await DSPilot.Engine.DspRepository.getFlowHistoryByDaysAsync(_paths, _logger, flowName, days);
        return fsharpList.Select(ToCSharpFlowHistoryEntity).ToList();
    }

    public async Task<List<Models.Dsp.DspFlowHistoryEntity>> GetFlowHistoryByStartTimeAsync(string flowName, DateTime startTime)
    {
        if (!_enabled) return new List<Models.Dsp.DspFlowHistoryEntity>();
        var fsharpList = await DSPilot.Engine.DspRepository.getFlowHistoryByStartTimeAsync(_paths, _logger, flowName, startTime);
        return fsharpList.Select(ToCSharpFlowHistoryEntity).ToList();
    }

    public async Task<int> ClearFlowHistoryAsync()
    {
        if (!_enabled) return 0;
        return await DSPilot.Engine.DspRepository.clearFlowHistoryAsync(_paths, _logger);
    }

    // ===== Helper Methods =====

    private FSharpDspFlowEntity ToFSharpFlowEntity(CSharpDspFlowEntity entity)
    {
        return new FSharpDspFlowEntity(
            entity.Id,
            entity.FlowName,
            ToFSharpOption(entity.MT),
            ToFSharpOption(entity.WT),
            ToFSharpOption(entity.CT),
            ToFSharpOption(entity.AvgMT),
            ToFSharpOption(entity.AvgWT),
            ToFSharpOption(entity.AvgCT),
            ToFSharpOption(entity.State),
            ToFSharpOption(entity.MovingStartName),
            ToFSharpOption(entity.MovingEndName),
            entity.CreatedAt,
            entity.UpdatedAt
        );
    }

    private FSharpDspCallEntity ToFSharpCallEntity(CSharpDspCallEntity entity)
    {
        return new FSharpDspCallEntity(
            entity.Id,
            entity.CallId,
            entity.CallName,
            entity.ApiCall,
            entity.WorkName,
            entity.FlowName,
            ToFSharpOption(entity.Next),
            ToFSharpOption(entity.Prev),
            ToFSharpOption(entity.AutoPre),
            ToFSharpOption(entity.CommonPre),
            entity.State,
            entity.ProgressRate,
            ToFSharpOption(entity.PreviousGoingTime),
            ToFSharpOption(entity.AverageGoingTime),
            ToFSharpOption(entity.StdDevGoingTime),
            entity.GoingCount,
            ToFSharpOption(entity.Device),
            ToFSharpOption(entity.ErrorText),
            entity.CreatedAt,
            entity.UpdatedAt
        );
    }

    private CSharpDspCallEntity ToCSharpCallEntity(FSharpDspCallEntity entity)
    {
        return new CSharpDspCallEntity
        {
            Id = entity.Id,
            CallId = entity.CallId,
            CallName = entity.CallName,
            ApiCall = entity.ApiCall,
            WorkName = entity.WorkName,
            FlowName = entity.FlowName,
            Next = FromFSharpOptionClass(entity.Next),
            Prev = FromFSharpOptionClass(entity.Prev),
            AutoPre = FromFSharpOptionClass(entity.AutoPre),
            CommonPre = FromFSharpOptionClass(entity.CommonPre),
            State = entity.State,
            ProgressRate = entity.ProgressRate,
            PreviousGoingTime = FromFSharpOptionStruct(entity.PreviousGoingTime),
            AverageGoingTime = FromFSharpOptionStruct(entity.AverageGoingTime),
            StdDevGoingTime = FromFSharpOptionStruct(entity.StdDevGoingTime),
            GoingCount = entity.GoingCount,
            Device = FromFSharpOptionClass(entity.Device),
            ErrorText = FromFSharpOptionClass(entity.ErrorText),
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    }

    private static Microsoft.FSharp.Core.FSharpOption<T> ToFSharpOption<T>(T? value) where T : struct
    {
        return value.HasValue
            ? Microsoft.FSharp.Core.FSharpOption<T>.Some(value.Value)
            : Microsoft.FSharp.Core.FSharpOption<T>.None;
    }

    private static Microsoft.FSharp.Core.FSharpOption<T> ToFSharpOption<T>(T? value) where T : class
    {
        return value != null
            ? Microsoft.FSharp.Core.FSharpOption<T>.Some(value)
            : Microsoft.FSharp.Core.FSharpOption<T>.None;
    }

    private static T? FromFSharpOptionStruct<T>(Microsoft.FSharp.Core.FSharpOption<T> option) where T : struct
    {
        return Microsoft.FSharp.Core.FSharpOption<T>.get_IsSome(option)
            ? option.Value
            : null;
    }

    private static T? FromFSharpOptionClass<T>(Microsoft.FSharp.Core.FSharpOption<T> option) where T : class
    {
        return Microsoft.FSharp.Core.FSharpOption<T>.get_IsSome(option)
            ? option.Value
            : null;
    }

    private DSPilot.Engine.DspFlowHistoryEntity ToFSharpFlowHistoryEntity(Models.Dsp.DspFlowHistoryEntity entity)
    {
        return new DSPilot.Engine.DspFlowHistoryEntity(
            entity.Id,
            entity.FlowName,
            ToFSharpOption(entity.MT),
            ToFSharpOption(entity.WT),
            ToFSharpOption(entity.CT),
            ToFSharpOption(entity.CycleNo),
            entity.RecordedAt
        );
    }

    private Models.Dsp.DspFlowHistoryEntity ToCSharpFlowHistoryEntity(DSPilot.Engine.DspFlowHistoryEntity entity)
    {
        return new Models.Dsp.DspFlowHistoryEntity
        {
            Id = entity.Id,
            FlowName = entity.FlowName,
            MT = FromFSharpOptionStruct(entity.MT),
            WT = FromFSharpOptionStruct(entity.WT),
            CT = FromFSharpOptionStruct(entity.CT),
            CycleNo = FromFSharpOptionStruct(entity.CycleNo),
            RecordedAt = entity.RecordedAt
        };
    }
}
