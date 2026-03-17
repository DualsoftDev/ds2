using Ds2.Core;
using DSPilot.Models.Dsp;
using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// DSP 데이터베이스 초기화 및 관리 서비스
/// </summary>
public class DspDatabaseService : BackgroundService
{
    private readonly ILogger<DspDatabaseService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DsProjectService _projectService;
    private readonly PlcToCallMapperService _mapper;

    public DspDatabaseService(
        ILogger<DspDatabaseService> logger,
        IServiceScopeFactory scopeFactory,
        DsProjectService projectService,
        PlcToCallMapperService mapper)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _projectService = projectService;
        _mapper = mapper;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DSP Database Service starting...");

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dspRepo = scope.ServiceProvider.GetRequiredService<IDspRepository>();

            // 1. 스키마 생성
            if (!await dspRepo.CreateSchemaAsync())
            {
                _logger.LogError("Failed to create DSP database schema");
                return;
            }

            // 2. AASX 로드 확인
            if (!_projectService.IsLoaded)
            {
                _logger.LogWarning("AASX project not loaded. DSP database will be empty.");
                return;
            }

            // 3. AASX에서 초기 데이터 로드
            await InitializeFromAasxAsync(dspRepo);

            // 4. PlcToCallMapper 초기화
            _mapper.Initialize();

            _logger.LogInformation("DSP Database Service initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize DSP Database Service");
        }

        // 백그라운드 서비스는 여기서 종료하지 않고 유지
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    /// <summary>
    /// AASX에서 초기 Flow/Call 데이터 로드
    /// </summary>
    private async Task InitializeFromAasxAsync(IDspRepository dspRepo)
    {
        try
        {
            var flows = _projectService.GetAllFlows();
            _logger.LogInformation("Loading {Count} flows from AASX...", flows.Count);

            // Flow 데이터 변환 및 삽입
            var flowEntities = flows.Select(f => new DspFlowEntity
            {
                FlowName = f.Name,
                State = "Ready",
                MT = null,
                WT = null,
                MovingStartName = null,
                MovingEndName = null
            }).ToList();

            var flowCount = await dspRepo.BulkInsertFlowsAsync(flowEntities);
            _logger.LogInformation("Inserted {Count} flows", flowCount);

            // Call 데이터 변환 및 삽입
            var callEntities = new List<DspCallEntity>();

            foreach (var flow in flows)
            {
                var works = _projectService.GetWorks(flow.Id);
                foreach (var work in works)
                {
                    var calls = _projectService.GetCalls(work.Id);
                    foreach (var call in calls)
                    {
                        var callEntity = new DspCallEntity
                        {
                            CallName = call.Name,
                            ApiCall = call.ApiCalls.Count > 0 ? call.ApiCalls[0].Name : call.ApiName,
                            WorkName = work.Name,
                            FlowName = flow.Name,
                            Next = null,  // TODO: Arrow 정보에서 추출 가능
                            Prev = null,
                            AutoPre = null,
                            CommonPre = null,
                            State = "Ready",
                            ProgressRate = 0.0,
                            Device = call.DevicesAlias,
                            PreviousGoingTime = null,
                            AverageGoingTime = null,
                            StdDevGoingTime = null,
                            GoingCount = 0
                        };

                        callEntities.Add(callEntity);

                        // ApiCall의 InTag/OutTag 정보 로그
                        foreach (var apiCall in call.ApiCalls)
                        {
                            var inTagInfo = Microsoft.FSharp.Core.FSharpOption<IOTag>.get_IsSome(apiCall.InTag)
                                ? $"Name={apiCall.InTag.Value.Name}, Address={apiCall.InTag.Value.Address}"
                                : "(none)";
                            var outTagInfo = Microsoft.FSharp.Core.FSharpOption<IOTag>.get_IsSome(apiCall.OutTag)
                                ? $"Name={apiCall.OutTag.Value.Name}, Address={apiCall.OutTag.Value.Address}"
                                : "(none)";

                            _logger.LogDebug(
                                "Call '{CallName}' (Flow: {FlowName}) - ApiCall: {ApiCallName}, InTag: [{InTag}], OutTag: [{OutTag}]",
                                call.Name, flow.Name, apiCall.Name, inTagInfo, outTagInfo);
                        }
                    }
                }
            }

            var callCount = await dspRepo.BulkInsertCallsAsync(callEntities);
            _logger.LogInformation("Inserted {Count} calls", callCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize from AASX");
            throw;
        }
    }
}
