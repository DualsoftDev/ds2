// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Adapters;
using DSPilot.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DSPilot.Services;

/// <summary>
/// 실측 duration 수동 보정. 설정 화면에서 "지금 실측값 채우기"를 명시적으로 실행하면 각 Flow 의
/// 디바이스(Device Work) Duration/Min/MaxDuration 을 실측값으로 채운다. 공식: Duration=round(mean),
/// Max=round(max(중앙값×(1+MedianMarginMaxPct), 클린 실측최대))+MarginMaxAbsMs (<see cref="ComputeMaxThresholdMs"/>),
/// Min(FillMin=true 일 때만)=round(pPercentileMin×(1−MarginMinPct)). 측정 span 있는 디바이스만 기록.
///
/// <para>측정→조인→기록은 검증된 기존 자산을 재사용한다: <see cref="CallLaneBuilderService"/>(lane/interval 빌드,
/// CallTest 와 공유) + <see cref="ApiSpanMath"/>(apiSpans/apiMeasured 포팅) + <see cref="DsProjectService.WriteWorkDurationCalibrationAndExport"/>
/// (min≤duration≤max 정규화·distinct·exportFromStore·LastLoadedSha256 갱신 — 단일 writer 경로).</para>
///
/// <para>Head/Tail 저장이나 과거 이력 재계산은 이 서비스를 자동 실행하지 않는다. 재진입 가드(<see cref="_gate"/>)로
/// 수동 "지금 실측값 채우기" 요청끼리 겹치지 않게 직렬화한다.</para>
/// </summary>
public sealed class AutoCalibrationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AppSettingsService _settings;
    private readonly DsProjectService _project;
    private readonly DspRepositoryAdapter _dspRepo;
    private readonly IHubContext<MonitoringHub> _hub;
    private readonly ILogger<AutoCalibrationService> _logger;

    // 수동 실행 직렬화 — 같은 입력이면 같은 결과(멱등) + 동시 export 충돌 방지.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // 클린사이클 후보를 가져올 최근 RecordedAt 구간(일). 구버전 IsIdle NULL 행 과대카운트 방지(최근으로 제한).
    private const int RecentDays = 30;
    // 윈도우 앞뒤 여유(ms). 경계 사이클의 첫 명령/마지막 응답 엣지를 빠짐없이 포함하고, RecordedAt 과 실제 IO 완료
    // 시각의 어긋남을 흡수한다 — 라이브 기록 경로(FlowMetricsService)는 RecordedAt 을 완료시각이 아닌 기록시각(UtcNow,
    // 약간의 지연 가능)으로 찍으므로 1초로는 가장 오래된 사이클의 시작 엣지를 놓칠 수 있어 넉넉히 둔다. 윈도우를
    // 넓혀도 추가로 잡히는 것은 실제 디바이스 latency span 뿐이라 통계 정확도에 해롭지 않다.
    private const int WindowBufferMs = 5000;

    public AutoCalibrationService(
        IServiceScopeFactory scopeFactory,
        AppSettingsService settings,
        DsProjectService project,
        DspRepositoryAdapter dspRepo,
        IHubContext<MonitoringHub> hub,
        ILogger<AutoCalibrationService> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _project = project;
        _dspRepo = dspRepo;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>
    /// 적합 Flow 의 디바이스 duration 을 실측값으로 보정한다. 설정 화면은 manual=true로 호출해
    /// Enabled/CompletedAt 레거시 게이트를 무시하고 즉시 실행한다. 재진입 가드로 직렬화되며,
    /// 성공 export 후 최초/최근 적용 시각을 기록하고 DatabaseRebuilt 를 브로드캐스트한다.
    /// <paramref name="onlyWorkIds"/> 는 구버전 선택 재측정 호출과의 API 호환을 위해 보존한다.
    /// </summary>
    public async Task<AutoCalibrationRunResult> RunAsync(bool manual, CancellationToken ct, IReadOnlySet<Guid>? onlyWorkIds = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var settings = _settings.LoadSettings();
            var ac = settings.AutoCalibration;

            if (!manual)
            {
                // 구버전 비수동 호출과의 호환 게이트. 현재 UI/호스트는 이 경로를 호출하지 않는다.
                if (!ac.Enabled) return Skip("자동 보정 비활성", success: true);
                // CompletedAt(1회성)은 구버전 "전체 초기 보정"에만 적용. 선택 재측정(onlyWorkIds 지정)은 완료 후에도 허용.
                if (onlyWorkIds is null && ac.CompletedAt is not null) return Skip($"이미 완료됨 ({ac.CompletedAt:u})", success: true);
            }
            if (!_project.IsLoaded) return Skip("프로젝트(AASX) 미로드 — 보정할 수 없습니다", success: false);

            int minClean = Math.Max(1, ac.MinCleanCycles);
            var flowNames = await _dspRepo.GetAllFlowNamesAsync();
            var allChanges = new List<(Guid WorkId, int? DurationMs, int? MinMs, int? MaxMs)>();
            int eligible = 0, calibrated = 0;
            var perFlowLog = new List<string>();

            foreach (var flow in flowNames)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(flow)) continue;

                // desc(RecordedAt) 정렬된 최근 RecentDays 이력 → 이상치 제외 클린사이클만.
                var rows = await _dspRepo.GetFlowHistoryByDaysAsync(flow, RecentDays);
                var clean = rows.Where(r => !r.IsIdle && r.CT.HasValue).ToList();
                if (clean.Count < minClean) continue;
                eligible++;

                var take = clean.Take(minClean).ToList(); // 최근 N 클린사이클(desc)
                // RecordedAt 은 UTC 저장이나 SQLite 왕복 시 Kind=Unspecified → Utc 로 정규화 후 Local 로 변환.
                var recUtc = take.Select(r => DateTime.SpecifyKind(r.RecordedAt, DateTimeKind.Utc)).ToList();
                var newestUtc = recUtc.Max();
                var oldestUtc = recUtc.Min();
                int oldestCt = take.OrderBy(r => r.RecordedAt).First().CT ?? 0;
                // 윈도우: 가장 오래된 클린사이클의 시작(≈RecordedAt−CT)부터 가장 최근 완료까지 + 버퍼. lane 빌더는 Local 입력.
                var startLocal = oldestUtc.AddMilliseconds(-(oldestCt + WindowBufferMs)).ToLocalTime();
                var endLocal = newestUtc.AddMilliseconds(WindowBufferMs).ToLocalTime();

                var flowChanges = await BuildFlowChangesAsync(flow, startLocal, endLocal, ac, onlyWorkIds);
                if (flowChanges.Count > 0)
                {
                    allChanges.AddRange(flowChanges);
                    calibrated++;
                    perFlowLog.Add($"{flow}:{flowChanges.Count}dev");
                }
            }

            if (allChanges.Count == 0)
            {
                // 적용할 게 없음 = 정상 no-op(성공이되 미적용).
                var msg = eligible == 0
                    ? $"클린사이클 {minClean}개 도달한 Flow 없음 — 보정 대기"
                    : $"적합 Flow {eligible}개이나 측정 가능한 디바이스 span 없음";
                _logger.LogInformation("[AutoCal] {Msg} (manual={Manual})", msg, manual);
                return new AutoCalibrationRunResult(true, false, eligible, 0, 0, msg);
            }

            // Min: FillMin=true 일 때만 실측 확정(사용자가 '최소값도 실측으로 기록' 의사 확정) → ActionUnder 게이트.
            // Max: 자동 보정은 Max 를 항상 실측(σ 여유 포함)으로 채우므로 항상 확정 → ActionOver 게이트.
            var (applied, exported) = _project.WriteWorkDurationCalibrationAndExport(
                allChanges, markMinMeasured: ac.FillMin, markMaxMeasured: true);

            var summary = $"Flow {calibrated}/{eligible}개 보정, 디바이스 {applied}건 기록 [{string.Join(", ", perFlowLog)}]";

            if (exported)
            {
                // LastAppliedAt(=AASX 수정 시각)는 매 적용마다 갱신, CompletedAt(1회성)은 최초에만 박제(멱등).
                _settings.RecordAutoCalibrationApplied(summary);

                if (applied > 0)
                {
                    try
                    {
                        // 수동 호출자는 HTTP 응답으로 결과를 표시하고, 대시보드 미러만 새로고침한다.
                        await _hub.Clients.All.SendAsync("DatabaseRebuilt", ct);
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "[AutoCal] SignalR broadcast 실패(비치명)"); }
                }
            }

            _logger.LogInformation("[AutoCal] {Summary} (manual={Manual}, exported={Exported})", summary, manual, exported);
            // export 실패(변경은 있었으나 미저장)만 실제 오류. 성공이면 Applied=(실제 기록 건수>0).
            return new AutoCalibrationRunResult(exported, exported && applied > 0, eligible, calibrated, applied,
                exported ? summary : "AASX export 실패 (프로젝트 미로드 또는 export 오류)");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 모든 디바이스 Work 의 이상감지 MinDuration/MaxDuration 을 전부 비운다(Duration 은 보존). 실측 보정의 역연산.
    /// 같은 AASX writer 경로를 쓰므로 재진입 가드(<see cref="_gate"/>)로 "지금 실측값 채우기"와 직렬화한다.
    /// 성공 시 DatabaseRebuilt 를 브로드캐스트해 대시보드/AASX 상태 미러를 새로고침한다.
    /// </summary>
    public async Task<AutoCalibrationRunResult> ClearRangesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_project.IsLoaded) return Skip("프로젝트(AASX) 미로드 — 초기화할 수 없습니다", success: false);

            var (cleared, exported) = _project.ClearAllWorkDurationRangesAndExport();
            if (!exported)
                return new AutoCalibrationRunResult(false, false, 0, 0, 0, "AASX export 실패 (프로젝트 미로드 또는 export 오류)");

            if (cleared > 0)
            {
                try { await _hub.Clients.All.SendAsync("DatabaseRebuilt", ct); }
                catch (Exception ex) { _logger.LogDebug(ex, "[AutoCal] Min/Max 초기화 broadcast 실패(비치명)"); }
            }

            var msg = cleared > 0
                ? $"디바이스 Min/Max {cleared}건 초기화 완료 (Duration 은 보존)"
                : "초기화할 Min/Max 값이 없습니다";
            _logger.LogInformation("[AutoCal] {Msg}", msg);
            return new AutoCalibrationRunResult(true, cleared > 0, 0, 0, cleared, msg);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 한 Flow 의 [startLocal,endLocal] 윈도우에서 디바이스별 command→response span 을 집계해 보정 변경 목록을 만든다.
    /// CycleAnalysisService 가 Scoped 라 스코프를 열어 <see cref="CallLaneBuilderService"/> 를 해석한다.
    /// 디바이스(=TargetWorkId)별로 span 을 모아 한 건씩 산출 — 같은 Work 를 여러 Call/ApiCall 이 구동해도 합쳐 집계.
    /// </summary>
    private async Task<List<(Guid WorkId, int? DurationMs, int? MinMs, int? MaxMs)>> BuildFlowChangesAsync(
        string flow, DateTime startLocal, DateTime endLocal, Models.AutoCalibrationSettings ac,
        IReadOnlySet<Guid>? onlyWorkIds = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var laneBuilder = scope.ServiceProvider.GetRequiredService<CallLaneBuilderService>();
        var lanes = await laneBuilder.BuildLanesAsync(flow, startLocal, endLocal);

        // 디바이스(Work)별 span 누적 + 현재값 캡처(FillMin=false 시 기존 MinDuration 보존용).
        var spansByWork = new Dictionary<Guid, List<double>>();
        var currentMinByWork = new Dictionary<Guid, int?>();

        foreach (var lane in lanes)
        {
            var spans = ApiSpanMath.Spans(lane.OutIntervals, lane.InIntervals);
            if (spans.Count == 0) continue; // 측정 span 없는 lane(디바이스) 은 건드리지 않음.
            foreach (var apiCall in lane.ApiCalls)
            {
                if (!Guid.TryParse(apiCall.TargetWorkId, out var wid)) continue; // RxGuid 없는 ApiCall 제외.
                if (onlyWorkIds is not null && !onlyWorkIds.Contains(wid)) continue; // 구버전 선택 재측정: 지정 Work 만.
                if (!spansByWork.TryGetValue(wid, out var list))
                {
                    list = new List<double>();
                    spansByWork[wid] = list;
                    // CurrentMinMs 는 대상 Work(=wid)의 MinDuration 을 그대로 읽은 값이라 같은 wid 를 가리키는
                    // 모든 ApiCall/lane 에서 동일하다(store 불변) → 최초 1회 캡처가 canonical. (FillMin=false 보존용.)
                    currentMinByWork[wid] = apiCall.CurrentMinMs;
                }
                list.AddRange(spans);
            }
        }

        var changes = new List<(Guid, int?, int?, int?)>();
        foreach (var (wid, spans) in spansByWork)
        {
            var (count, pMin, _, mean, _, _) = ApiSpanMath.Measured(spans, percentileMin: ac.PercentileMin);
            var (median, cleanMax) = ApiSpanMath.MedianAndCleanMax(spans);
            if (count == 0 || mean is null || pMin is null || median is null) continue;

            int duration = (int)Math.Round(mean.Value);
            int maxMs = ComputeMaxThresholdMs(median.Value, cleanMax ?? median.Value, ac.MedianMarginMaxPct, ac.MarginMaxAbsMs);
            int? minMs = ac.FillMin
                ? (int)Math.Round(pMin.Value * (1 - ac.MarginMinPct))
                : currentMinByWork[wid]; // FillMin=false → 기존 MinDuration 보존(null 은 store 에서 clear 되므로 현재값 재기록).

            changes.Add((wid, duration, minMs, maxMs));
        }
        return changes;
    }

    /// <summary>
    /// ActionOver Max 임계(ms) = round(max(중앙값 × (1 + 여유율), 클린 실측최대)) + 절대 여유.
    /// 클린 실측최대 클램프 = "이미 관측된 정상(통신 이상치 제외) 가동은 절대 이상 판정하지 않는다" 불변식 —
    /// 정상 duration 이 두 갈래(부하 조건 등)인 디바이스에서 중앙값×여유율이 정상 느린 경로 안쪽에 떨어지는 오탐을 막는다.
    /// 분포가 좁으면 클램프는 발동하지 않아 중앙값 공식 그대로다.
    /// </summary>
    public static int ComputeMaxThresholdMs(double medianMs, double cleanMaxMs, double marginPct, int marginAbsMs)
        => (int)Math.Round(Math.Max(medianMs * (1 + Math.Max(0, marginPct)), cleanMaxMs)) + Math.Max(0, marginAbsMs);

    private static AutoCalibrationRunResult Skip(string message, bool success)
        => new(success, false, 0, 0, 0, message);

}

/// <summary>
/// 자동 보정 1회 실행 결과.
/// <list type="bullet">
/// <item><see cref="Success"/> = 오류 없이 끝났는지(적합 Flow 없는 정상 no-op 도 true; export 실패/미로드는 false).</item>
/// <item><see cref="Applied"/> = 실제로 보정값이 project.aasx 에 기록됐는지.</item>
/// </list>
/// </summary>
public sealed record AutoCalibrationRunResult(
    bool Success,
    bool Applied,
    int FlowsEligible,
    int FlowsCalibrated,
    int DevicesApplied,
    string Message);
