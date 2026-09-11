// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
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
    private readonly AppSettingsService _settings;
    private readonly IServiceScopeFactory _scopeFactory;

    public DspDatabaseServiceAdapter(
        ILogger<DspDatabaseServiceAdapter> logger,
        DatabasePathResolverAdapter pathResolver,
        DsProjectService projectService,
        PlcToCallMapperService mapper,
        IFlowMetricsService flowMetricsService,
        IDspRepository dspRepository,
        AppSettingsService settings,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _paths = pathResolver.GetDatabasePaths();
        _projectService = projectService;
        _mapper = mapper;
        _flowMetricsService = flowMetricsService;
        _dspRepository = dspRepository;
        _settings = settings;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await BootstrapAsync(stoppingToken);
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

    /// <summary>
    /// 스키마 생성 + AASX 로부터 dspFlow / dspCall 행 적재 + Mapper / FlowMetrics 초기화.
    /// HostedService 시작 시 자동 실행되며, plc.db 삭제 후 재초기화 시 외부에서도 호출 가능.
    /// </summary>
    public async Task<bool> BootstrapAsync(CancellationToken stoppingToken = default)
    {
        if (!_paths.DspTablesEnabled)
        {
            _logger.LogInformation("DspTables:Enabled=false, skipping AASX load and DSP DB initialization.");
            return false;
        }

        var success = await InitializeFromAasxWithRetryAsync(stoppingToken);

        if (success)
        {
            // Reinitialize — 재로딩 시에도 강제 재빌드. Initialize() 는 _isInitialized 가드로
            // NO-OP 가 되어 stale 매핑(사이클 페이지 flow 리스트 등)이 남던 문제 수정.
            _logger.LogInformation("Initializing PlcToCallMapper...");
            _mapper.Reinitialize();

            _logger.LogInformation("Initializing FlowMetricsService...");
            await _flowMetricsService.InitializeAsync();
        }

        return success;
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
                    // 부팅 경로에도 참조 재해석 — 서비스 정지 중 AASX 가 교체되면 워처 경로(ReloadAndResync)를 안 거친다.
                    // (LastLoadedSha256 이 인메모리라 재시작 후엔 "변경 없음" 으로 보이는 것과 같은 사각지대.)
                    _settings.ReconcileCallReferences(_projectService);
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
        // Hub 모니터링 단독 시나리오에서는 PlcCapture 가 꺼져 있어 EV2 가 스키마를 만들지 않음.
        // DsPilot 이 직접 dsp* 테이블을 보장한다.
        await _dspRepository.CreateSchemaAsync();

        var allFlows = _projectService.GetAllFlows().ToList();
        _logger.LogInformation("Total flows in AASX: {Count}", allFlows.Count);

        var filteredFlows = allFlows
            .Where(f => !f.Name.EndsWith("_Flow", StringComparison.OrdinalIgnoreCase))
            .ToList();
        _logger.LogInformation("Filtered flows (excluding '*_Flow'): {Count}", filteredFlows.Count);

        // flow 이름만 바뀐 경우 옛 이름의 행·설정을 새 이름으로 승계 — UPSERT 가 새 이름 행을 만들기 전에(2026-09-11).
        await ApplyFlowRenamesAsync();

        var flowEntities = CreateFlowEntities(allFlows);
        var flowCount = await _dspRepository.BulkInsertFlowsAsync(flowEntities);
        _logger.LogInformation("BulkInsertFlowsAsync returned: {Count} flows (expected: {Expected})", flowCount, flowEntities.Count);

        var callEntities = CreateCallEntities(filteredFlows);
        var callCount = await _dspRepository.BulkInsertCallsAsync(callEntities);
        _logger.LogInformation("BulkInsertCallsAsync returned: {Count} calls (expected: {Expected})", callCount, callEntities.Count);

        return (flowCount, callCount);
    }

    /// <summary>
    /// AASX 에서 flow 이름만 바뀐 경우(GUID 동일) 옛 이름의 DB 행(plc.db 정의·이력, oee.db)과 flow 이름 키 설정을 새 이름으로
    /// 승계한다(2026-09-11). 종전엔 ReloadAndResync 가 옛 이름을 "사라진 flow" 로 보고 이력을 삭제했다.
    /// 판정 근거 = dspFlow.flowId — 첫 배포 부팅에는 비어 있어 판정 불가, UPSERT 가 채운 뒤부터 유효.
    /// 어떤 예외도 부팅을 막지 않는다.
    /// </summary>
    private async Task ApplyFlowRenamesAsync()
    {
        try
        {
            var rows = await _dspRepository.GetFlowIdRowsAsync();
            if (rows.Count == 0) return;

            var model = _projectService.GetAllFlowsIncludingDisabled()
                .Where(f => !f.Name.EndsWith("_Flow", StringComparison.OrdinalIgnoreCase))
                .Select(f => (f.Id, f.Name));
            var result = FlowRenameDetector.Detect(
                model, rows.Select(r => new FlowRenameDetector.DbFlow(r.FlowName, r.FlowId)));
            foreach (var w in result.Warnings)
                _logger.LogWarning("[FlowRename] {Warning}", w);
            if (result.Renames.Count == 0) return;

            var applied = new List<FlowRenameRecord>();
            foreach (var r in result.Renames)
            {
                var (flows, calls, hist) = await _dspRepository.RenameFlowAsync(r.OldName, r.NewName);
                var oee = 0;
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    oee = await scope.ServiceProvider.GetRequiredService<IOeeRepository>().RenameFlowAsync(r.OldName, r.NewName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[FlowRename] oee.db 승계 실패 '{Old}' -> '{New}' (plc.db 승계는 유효)", r.OldName, r.NewName);
                }
                var settingsChanged = _settings.RenameFlowInSettings(r.OldName, r.NewName);
                _logger.LogInformation(
                    "[FlowRename] '{Old}' -> '{New}' (GUID {Id}) 승계: dspFlow={F} dspCall={C} history={H} oee={O} 설정 {S}개 섹션",
                    r.OldName, r.NewName, r.Id, flows, calls, hist, oee, settingsChanged);
                applied.Add(new FlowRenameRecord(DateTime.UtcNow, r.OldName, r.NewName, r.Id, flows, calls, hist, oee, settingsChanged));
            }
            _settings.RecordFlowRenames(applied);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FlowRename] flow 리네임 승계 실패(로드는 계속)");
        }
    }

    private static List<DspFlowEntity> CreateFlowEntities(IEnumerable<Flow> flows)
    {
        var now = DateTime.UtcNow;
        return flows
            .Where(f => !f.Name.EndsWith("_Flow", StringComparison.OrdinalIgnoreCase))
            .Select(f => new DspFlowEntity
            {
                FlowName = f.Name,
                FlowId = f.Id.ToString("D"),   // 리네임 판정 근거(FlowRenameDetector) — 첫 UPSERT 에서 구 행에도 채워진다
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
