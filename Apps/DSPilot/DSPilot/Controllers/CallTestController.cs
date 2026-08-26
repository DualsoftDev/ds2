// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models.Analysis;
using DSPilot.Repositories;
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

/// <summary>
/// 격리형 호스팅용 Call 간트(Call cycle) API.
/// Blazor /call-test 가 쓰던 PlcToCallMapperService + IPlcRepository + CycleAnalysisService + IFlowMetricsService 를 얇게 래핑.
/// 데이터 로직 추가 없음 — 순수 직렬화 경계. Gantt SVG 는 서버 BuildSvg() 대신 클라이언트(Alpine)에서
/// 동일 레이아웃 수학으로 재구성하므로(상호작용 보존), 여기서는 lane/segment/boundary/tail 의 raw JSON 만 내려보낸다.
/// 시간/시각은 DB-local tz(DateTime.Now) 기준으로 서버에서 해석하고, 이미 로컬화된 ISO("o") 문자열로 emit.
/// POST 들은 antiforgery 미적용(AutoValidate/global filter 없음) — 평범한 tokenless JSON fetch.
/// </summary>
[ApiController]
[Route("api/call-test")]
public class CallTestController : ControllerBase
{
    private readonly PlcToCallMapperService _callMapper;
    private readonly IPlcRepository _plcRepository;
    private readonly CycleAnalysisService _cycleAnalysis;
    private readonly IFlowMetricsService _flowMetrics;
    private readonly AppSettingsService _settings;
    private readonly DsProjectService _project;
    private readonly CallLaneBuilderService _laneBuilder;
    private readonly ILogger<CallTestController> _logger;

    public CallTestController(
        PlcToCallMapperService callMapper,
        IPlcRepository plcRepository,
        CycleAnalysisService cycleAnalysis,
        IFlowMetricsService flowMetrics,
        AppSettingsService settings,
        DsProjectService project,
        CallLaneBuilderService laneBuilder,
        ILogger<CallTestController> logger)
    {
        _callMapper = callMapper;
        _plcRepository = plcRepository;
        _cycleAnalysis = cycleAnalysis;
        _flowMetrics = flowMetrics;
        _settings = settings;
        _project = project;
        _laneBuilder = laneBuilder;
        _logger = logger;
    }

    /// <summary>Flow 목록 (CallTagPairs 에서 distinct 추출, 정렬). Blazor OnInitializedAsync 동일.</summary>
    [HttpGet("flows")]
    public ActionResult<List<string>> GetFlows()
    {
        var flows = _callMapper.GetAllCallTagPairs()
            .Select(p => p.FlowName)
            .Where(f => !string.IsNullOrEmpty(f))
            .Distinct()
            .OrderBy(f => f)
            .ToList();
        return flows;
    }

    /// <summary>
    /// 최신 로그 시각 + 기본 시간범위(최신-5분 ~ 최신). 프리셋 계산용 기준점.
    /// Blazor OnInitializedAsync / GetEffectiveLatestAsync 와 동일하게 DateTime.Now fallback.
    /// </summary>
    [HttpGet("latest-time")]
    public async Task<ActionResult<CtLatestTimeDto>> GetLatestTime()
    {
        DateTime end;
        try
        {
            var latest = await _plcRepository.GetLatestLogDateTimeAsync();
            end = latest ?? DateTime.Now;
        }
        catch
        {
            end = DateTime.Now;
        }
        var start = end.AddMinutes(-5);
        return new CtLatestTimeDto(IsoLocal(start), IsoLocal(end));
    }

    /// <summary>
    /// 메인 로드 — segment(lane), Call별 (InTag/OutTag) lookup, head/tail 경계, cycle 통계 를 한 번에.
    /// Blazor LoadAsync 의 segment 빌드 + MergeIntervals + ApplyProjectHeadTail + Resolve(Head|Tail) + ComputeCycleStats 를 재현.
    /// headCallId/tailCallId 가 명시되면 그 값으로, 아니면 유효(override 적용) Head/Tail 을 적용.
    /// 유효값 = 저장된 사용자 지정(FlowCycleOverride) > AASX 기본값. 저장/복원은 POST /api/flow/{name}/cycle-override 재사용.
    /// </summary>
    [HttpPost("load")]
    public async Task<ActionResult<CtLoadDto>> Load([FromBody] CtLoadRequest req)
    {
        if (string.IsNullOrEmpty(req.FlowName))
            return BadRequest("flowName is required");

        // datetime-local(JSON 미한정 ISO) → Kind=Unspecified 로 역직렬화됨.
        // Blazor 주 경로(프리셋 = DateTime.Now, Kind=Local)와 동일하게 Local 로 마킹해야
        // 리포지토리의 ToSqliteUtcString(Local→UTC) 변환이 올바르게 작동한다.
        var start = AsLocal(req.Start);
        var end = AsLocal(req.End);
        if (end <= start)
            return BadRequest("종료 시각은 시작 시각보다 커야 합니다.");

        // 간트 시간축 = 사용자가 요청한 날짜 범위 그대로(고정). 과거엔 실제 신호 발생 구간(min/max)에
        // 맞춰 자동 축소(fit-to-data)했으나, 그러면 윈도우를 넓혀도 축이 데이터 범위로 되돌아가
        // "날짜를 바꿔도 간트가 안 변한다"로 보였다(특히 신호가 드문/없는 구간). 이제 요청 [start,end] 를
        // 그대로 축으로 쓴다 — 세그먼트/경계/Tail 은 모두 [start,end] 로 클램프되므로 항상 축 안에 들어온다.
        var chartStart = start;
        var chartEnd = end > start ? end : start.AddSeconds(1);

        // lane 단위 grouping + interval merge. 자동 실측 보정(AutoCalibrationService)과 동일 코드를 공유.
        var lanes = await _laneBuilder.BuildLanesAsync(req.FlowName, start, end);

        // 유효(override 적용) Head/Tail — override 안 했을 때 적용할 기본값. 저장된 사용자 지정이 있으면
        // 그 값을, 없으면 AASX 기본값을 쓴다(GetCycleBoundaryCallNames = override 적용 후 런타임 경계).
        var (effHeadId, effTailId) = ResolveEffectiveHeadTail(req.FlowName, lanes);
        // Head/Tail 은 Flow 별로 무조건 존재 — 유효값이 없으면 첫 lane(Head)/마지막 lane(Tail) 으로 기본 지정.
        (effHeadId, effTailId) = EnsureHeadTailDefaults(effHeadId, effTailId, lanes);
        // AASX 원본 Head/Tail — "AASX 기본값 복원" 시 되돌릴 대상(override 와 무관한 프로젝트 정의).
        var (aasxHeadId, aasxTailId) = ResolveProjectHeadTail(req.FlowName, lanes);

        // 요청에 H/T 명시 여부. headCallId/tailCallId 가 빈 문자열이면 명시적 "해제"(null),
        // null(미전송)이면 유효(override 적용) 기본값을 적용.
        Guid? headId = ResolveRequestedId(req.HeadCallId, effHeadId, req.HeadSpecified);
        Guid? tailId = ResolveRequestedId(req.TailCallId, effTailId, req.TailSpecified);
        // head==tail 허용 — 단일 신호 Call 1개를 자기 OutTag↑→완료(InTag↑/OutTag↓)로 분해(MT). null 강제 안 함.

        var (cycleBoundaries, tailEdges, tailCompletionSource) =
            await ResolveBoundariesAsync(req.FlowName, start, end, headId, tailId, lanes);

        var stats = ComputeCycleStats(req.FlowName, cycleBoundaries, tailEdges, chartEnd);

        // 이 Flow 에 저장된 사용자 지정(override) 이 존재하는지 — UI 의 'CT 기준: 사용자 지정/AASX 기본' 표시용.
        var isOverride = _settings.GetFlowCycleOverride(req.FlowName) is not null;

        return new CtLoadDto(
            req.FlowName,
            IsoLocal(chartStart),
            IsoLocal(chartEnd),
            lanes,
            headId?.ToString(),
            tailId?.ToString(),
            aasxHeadId?.ToString(),
            aasxTailId?.ToString(),
            cycleBoundaries.Select(IsoLocal).ToList(),
            tailEdges.Select(IsoLocal).ToList(),
            stats.AvgCycleMs,
            stats.AvgActiveMs,
            isOverride,
            tailCompletionSource);
    }

    /// <summary>
    /// 프로젝트 정의 Head 기준 사이클 경계 시각 목록(rising edge). Blazor CycleAnalysis.GetCycleBoundaryTimesAsync 래핑.
    /// H/T 미지정(기본) 경계 source. 결과는 로컬 ISO 문자열.
    /// </summary>
    [HttpPost("cycle-boundaries")]
    public async Task<ActionResult<List<string>>> CycleBoundaries([FromBody] CtCycleBoundaryRequest req)
    {
        if (string.IsNullOrEmpty(req.FlowName))
            return BadRequest("flowName is required");
        var edges = await _cycleAnalysis.GetCycleBoundaryTimesAsync(req.FlowName, AsLocal(req.Start), AsLocal(req.End));
        return edges.OrderBy(t => t).Select(IsoLocal).ToList();
    }

    /// <summary>
    /// 임의 태그의 rising edge 시각 목록. Blazor PlcRepository.FindRisingEdgesAsync 래핑.
    /// 사용자가 Head(InTag) 또는 Tail(OutTag) 을 직접 지정했을 때의 경계/마커 source.
    /// </summary>
    [HttpPost("boundaries")]
    public async Task<ActionResult<List<string>>> Boundaries([FromBody] CtBoundaryRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Tag))
            return BadRequest("tag is required");
        var edges = await _plcRepository.FindRisingEdgesAsync(req.Tag, AsLocal(req.Start), AsLocal(req.End));
        return edges.OrderBy(t => t).Select(IsoLocal).ToList();
    }

    /// <summary>
    /// H/T 토글에 반응해 오버레이(boundary + tail edge)만 재조회. Blazor ResolveOverlaysAsync 와 동일.
    /// segment(lane) 는 재사용하므로 다시 보내지 않는다. lane 정보는 InTag/OutTag lookup 용으로만 사용.
    /// </summary>
    [HttpPost("resolve-overlays")]
    public async Task<ActionResult<CtOverlayDto>> ResolveOverlays([FromBody] CtOverlayRequest req)
    {
        if (string.IsNullOrEmpty(req.FlowName))
            return BadRequest("flowName is required");

        var start = AsLocal(req.Start);
        var end = AsLocal(req.End);

        Guid? headId = ParseGuid(req.HeadCallId);
        Guid? tailId = ParseGuid(req.TailCallId);

        // Head 경계(시작) = Head OutTag↑ (진영 B: OutTag=PLC 출력=명령=동작 시작)
        List<DateTime> cycleBoundaries;
        if (headId.HasValue && !string.IsNullOrWhiteSpace(req.HeadStartTag))
        {
            cycleBoundaries = await _plcRepository.FindRisingEdgesAsync(
                req.HeadStartTag!, start, end, _project.TryGetSystemIdByFlowName(req.FlowName));
        }
        else
        {
            cycleBoundaries = await _cycleAnalysis.GetCycleBoundaryTimesAsync(req.FlowName, start, end);
        }

        // Tail 완료 마커 = InTag↑(있으면) else OutTag↓(OutOnly 추정). 단일 규칙 = CycleCompletionResolver.
        var tc = tailId.HasValue
            ? CycleCompletionResolver.Resolve(req.TailFinishTag, req.TailOutTag)
            : default;
        List<DateTime> tailEdges = !string.IsNullOrWhiteSpace(tc.Tag)
            ? (tc.Falling
                ? await _plcRepository.FindFallingEdgesAsync(
                    tc.Tag!, start, end, _project.TryGetSystemIdByFlowName(req.FlowName))
                : await _plcRepository.FindRisingEdgesAsync(
                    tc.Tag!, start, end, _project.TryGetSystemIdByFlowName(req.FlowName)))
            : new List<DateTime>();

        cycleBoundaries = cycleBoundaries.OrderBy(t => t).ToList();
        tailEdges = tailEdges.OrderBy(t => t).ToList();

        var chartEnd = end;
        var stats = ComputeCycleStats(req.FlowName, cycleBoundaries, tailEdges, chartEnd);

        return new CtOverlayDto(
            cycleBoundaries.Select(IsoLocal).ToList(),
            tailEdges.Select(IsoLocal).ToList(),
            stats.AvgCycleMs,
            stats.AvgActiveMs,
            CycleCompletionResolver.SourceLabel(tc.Source));
    }

    /// <summary>
    /// 실측 duration(평균/min/max)을 ApiCall 의 대상 Device Work 에 기록하고 공유 project.aasx 재export.
    /// flow.html Call lane 확장 행의 '적용'(행별) / 액션바의 '실측 적용'(전체). 매핑은 평균→Duration,
    /// min→MinDuration, max→MaxDuration (DsProjectService 에서 min ≤ Duration ≤ max 로 정규화). antiforgery 미적용 POST.
    /// </summary>
    [HttpPost("apply-durations")]
    public ActionResult<CtApplyDurationsResult> ApplyDurations([FromBody] CtApplyDurationsRequest req)
    {
        if (req?.Changes is null || req.Changes.Count == 0)
            return BadRequest("changes is required");

        var parsed = new List<(Guid, int?, int?, int?)>();
        foreach (var ch in req.Changes)
        {
            if (Guid.TryParse(ch.WorkId, out var wid))
                parsed.Add((wid, ch.DurationMs, ch.MinMs, ch.MaxMs));
        }
        if (parsed.Count == 0)
            return BadRequest("유효한 workId 가 없습니다.");

        // 사용자가 실측 span 을 보고 직접 '실측 적용' 한 명시적 확정 — Min/Max 를 넘긴 Work 는 ActionUnder/Over 게이트를 연다.
        var (applied, exported) = _project.WriteWorkDurationCalibrationAndExport(
            parsed, markMinMeasured: true, markMaxMeasured: true);
        if (!exported)
            return StatusCode(500, new { message = "AASX 저장 실패 (프로젝트 미로드 또는 export 오류)." });

        return new CtApplyDurationsResult(applied, true);
    }

    // ── helpers (Blazor @code 1:1 이식) ───────────────────────────────────────

    /// <summary>프로젝트(AASX) 정의 Head/Tail 을 lane 매칭해서 CallId 로 변환. Blazor ApplyProjectHeadTail 동일.</summary>
    private (Guid? headId, Guid? tailId) ResolveProjectHeadTail(string flowName, List<CtLaneDto> lanes)
    {
        try
        {
            var (headName, tailName) = _flowMetrics.GetAasxCycleBoundaries(flowName);
            Guid? headId = !string.IsNullOrEmpty(headName)
                ? MatchLaneId(lanes, headName)
                : null;
            Guid? tailId = !string.IsNullOrEmpty(tailName)
                ? MatchLaneId(lanes, tailName)
                : null;
            if (headId == tailId) tailId = null;
            return (headId, tailId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CallTest] failed to apply project head/tail for flow '{Flow}'", flowName);
            return (null, null);
        }
    }

    /// <summary>
    /// 유효(override 적용) Head/Tail Call 이름 → lane CallId. 저장된 사용자 지정(FlowCycleOverride)이 있으면
    /// 그 값이, 없으면 AASX 기본값이 GetCycleBoundaryCallNames 로 반환된다(런타임 경계 = override 적용 결과).
    /// 런타임 상태가 아직 없으면 AASX 기본값으로 폴백.
    /// </summary>
    private (Guid? headId, Guid? tailId) ResolveEffectiveHeadTail(string flowName, List<CtLaneDto> lanes)
    {
        try
        {
            var (effHead, effTail) = _flowMetrics.GetCycleBoundaryCallNames(flowName);
            if (string.IsNullOrEmpty(effHead) && string.IsNullOrEmpty(effTail))
                (effHead, effTail) = _flowMetrics.GetAasxCycleBoundaries(flowName);

            Guid? headId = !string.IsNullOrEmpty(effHead) ? MatchLaneId(lanes, effHead) : null;
            Guid? tailId = !string.IsNullOrEmpty(effTail) ? MatchLaneId(lanes, effTail) : null;
            // head==tail 허용(단일 신호 Call 자기분해). null 강제 안 함.
            return (headId, tailId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CallTest] failed to resolve effective head/tail for flow '{Flow}'", flowName);
            return (null, null);
        }
    }

    private static Guid? MatchLaneId(List<CtLaneDto> lanes, string callName)
    {
        var lane = lanes.FirstOrDefault(l => string.Equals(l.CallName, callName, StringComparison.OrdinalIgnoreCase));
        return lane is null ? null : ParseGuid(lane.CallId);
    }

    /// <summary>
    /// Head/Tail 이 모두 존재하도록 보장 — 유효값이 없으면 첫 lane(Head)/마지막 lane(Tail) 으로 채운다.
    /// (Flow 별 사이클 경계는 무조건 존재해야 한다 — UI 에서 해제 불가.)
    /// Call 이 하나뿐(단일 신호)이면 Tail=Head 로 채워 자기 OutTag↑→완료로 MT 분해한다(head==tail 허용).
    /// </summary>
    private static (Guid? headId, Guid? tailId) EnsureHeadTailDefaults(Guid? headId, Guid? tailId, List<CtLaneDto> lanes)
    {
        if (lanes.Count == 0) return (headId, tailId);
        headId ??= ParseGuid(lanes[0].CallId);
        if (tailId is null)
        {
            var lastId = ParseGuid(lanes[^1].CallId);
            tailId = lastId != headId
                ? lastId
                : lanes.Select(l => ParseGuid(l.CallId)).FirstOrDefault(id => id != headId) ?? headId;
        }
        return (headId, tailId);
    }

    /// <summary>
    /// 요청 H/T id 해석: specified=false → 프로젝트 기본값; specified=true + 빈/null → 명시적 해제(null);
    /// specified=true + 값 → 그 값.
    /// </summary>
    private static Guid? ResolveRequestedId(string? requested, Guid? projectDefault, bool specified)
    {
        if (!specified) return projectDefault;
        return ParseGuid(requested);
    }

    private async Task<(List<DateTime> cycleBoundaries, List<DateTime> tailEdges, string? tailCompletionSource)> ResolveBoundariesAsync(
        string flowName, DateTime start, DateTime end, Guid? headId, Guid? tailId, List<CtLaneDto> lanes)
    {
        // 진영 B (PLC 기준): OutTag=출력(명령)=동작 시작, InTag=입력(응답)=동작 완료.
        //   Head 사이클 경계(시작) = Head OutTag↑. Tail 완료 = InTag↑(있으면) else OutTag↓(OutOnly 추정).
        var headStartTag = headId.HasValue ? lanes.FirstOrDefault(l => l.CallId == headId.Value.ToString())?.OutTag : null;
        var tailLane = tailId.HasValue ? lanes.FirstOrDefault(l => l.CallId == tailId.Value.ToString()) : null;
        var tc = tailLane is not null
            ? CycleCompletionResolver.Resolve(tailLane.InTag, tailLane.OutTag)
            : default;

        Task<List<DateTime>> headTask = headId.HasValue && !string.IsNullOrWhiteSpace(headStartTag)
            ? _plcRepository.FindRisingEdgesAsync(headStartTag!, start, end, _project.TryGetSystemIdByFlowName(flowName))
            : _cycleAnalysis.GetCycleBoundaryTimesAsync(flowName, start, end);

        Task<List<DateTime>> tailTask = !string.IsNullOrWhiteSpace(tc.Tag)
            ? (tc.Falling
                ? _plcRepository.FindFallingEdgesAsync(tc.Tag!, start, end, _project.TryGetSystemIdByFlowName(flowName))
                : _plcRepository.FindRisingEdgesAsync(tc.Tag!, start, end, _project.TryGetSystemIdByFlowName(flowName)))
            : Task.FromResult(new List<DateTime>());

        await Task.WhenAll(headTask, tailTask);

        return (
            headTask.Result.OrderBy(t => t).ToList(),
            tailTask.Result.OrderBy(t => t).ToList(),
            CycleCompletionResolver.SourceLabel(tc.Source));
    }

    /// <summary>
    /// 사이클 경계 간 CT 평균 + (Head OutTag↑ 시작 → 사이클 내 첫 Tail InTag↑ 완료) 활성구간 평균.
    /// 도출 로직은 <see cref="CycleDerivation"/> 로 추출되어 과거 history 재계산(CycleRecomputeService)과
    /// 동일 코드를 공유하고, 대시보드와 동일한 유효 비가동 범위(글로벌+per-flow override)를 적용한다 → 화면 ↔ 대시보드 1:1.
    /// </summary>
    private (double? AvgCycleMs, double? AvgActiveMs) ComputeCycleStats(
        string flowName, List<DateTime> cycleBoundaries, List<DateTime> tailEdges, DateTime chartEnd)
    {
        var (maxMs, minMs) = _settings.GetEffectiveCycleRangeMs(flowName);
        var cycles = CycleDerivation.BuildCycles(cycleBoundaries, tailEdges, chartEnd);
        return CycleDerivation.Averages(cycles, maxMs, minMs);
    }

    private static Guid? ParseGuid(string? s)
        => Guid.TryParse(s, out var g) ? g : null;

    /// <summary>
    /// 미한정(Unspecified) DateTime 을 Local 로 마킹. JSON 으로 들어온 datetime-local 값은
    /// Kind=Unspecified 인데, 리포지토리(ToSqliteUtcString)는 Local 만 UTC 변환하므로
    /// Blazor 프리셋 경로(DateTime.Now=Local)와 일치시키기 위해 Local 로 지정한다.
    /// </summary>
    private static DateTime AsLocal(DateTime dt)
        => dt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dt, DateTimeKind.Local)
            : dt;

    /// <summary>로컬 tz ISO("o"). 클라이언트는 new Date() 로 파싱 후 표시.</summary>
    private static string IsoLocal(DateTime dt) => dt.ToString("o");
}

// ── DTOs (positional records → camelCase 자동) ─────────────────────────────────
// 전역 네임스페이스(DSPilot.Controllers) 충돌 방지를 위해 Ct* 접두. JSON 출력은 타입명과 무관.

public record CtLatestTimeDto(string Start, string End);

public record CtLoadRequest(
    string FlowName,
    DateTime Start,
    DateTime End,
    string? HeadCallId,
    string? TailCallId,
    bool HeadSpecified,
    bool TailSpecified);

public record CtOverlayRequest(
    string FlowName,
    DateTime Start,
    DateTime End,
    string? HeadCallId,
    string? TailCallId,
    // 진영 B: Head 시작 = OutTag↑, Tail 완료 = InTag↑(있으면) else OutTag↓(OutOnly 추정).
    string? HeadStartTag,
    string? TailFinishTag,
    // Tail 에 InTag 가 없을 때 완료(OutTag↓) 도출용. 클라가 tailLane.outTag 를 함께 보낸다.
    string? TailOutTag);

public record CtCycleBoundaryRequest(string FlowName, DateTime Start, DateTime End);

public record CtBoundaryRequest(string Tag, DateTime Start, DateTime End);

public record CtIntervalDto(string Start, string End);

public record CtLaneDto(
    string CallId,
    string CallName,
    string WorkName,
    int LaneIndex,
    List<CtIntervalDto> Intervals,
    string? InTag,
    string? OutTag,
    // 태그별 ON 구간 (Call 막대 안 라인 그래프용). OutTag=명령(시작), InTag=응답(완료).
    List<CtIntervalDto> OutIntervals,
    List<CtIntervalDto> InIntervals,
    // 이 Call 에 소속된 ApiCall 들 — lane 행 확장 시 표시(소속 ApiCall + 보정 대상 Work 현재값).
    List<CtApiCallDto> ApiCalls);

/// <summary>
/// Call lane 확장 행 1개 = ApiCall 하나. inTag/outTag 는 이 ApiCall 자신의 태그(1:1 이면 lane 과 동일).
/// current* = 보정 대상 Device Work(RxGuid)의 현재 AASX Duration/Min/MaxDuration(ms, 없으면 null) — 실측치와 대비용.
/// </summary>
public record CtApiCallDto(
    string ApiCallId,
    string Name,
    string? InTag,
    string? OutTag,
    string? TargetWorkId,
    int? CurrentDurationMs,
    int? CurrentMinMs,
    int? CurrentMaxMs);

// ── 실측 duration → AASX 적용 ───────────────────────────────────────────────
public record CtApplyDurationsRequest(List<CtDurationChange> Changes);

/// <summary>대상 Device Work 에 기록할 한 건 — 평균→Duration, min→MinDuration, max→MaxDuration (ms, null 은 제거).</summary>
public record CtDurationChange(string WorkId, int? DurationMs, int? MinMs, int? MaxMs);

public record CtApplyDurationsResult(int Applied, bool Ok);

public record CtLoadDto(
    string FlowName,
    string ChartStart,
    string ChartEnd,
    List<CtLaneDto> Lanes,
    string? HeadCallId,
    string? TailCallId,
    // ProjectHead/TailCallId = AASX 원본 기본값(override 와 무관) — 'AASX 기본값 복원'용.
    string? ProjectHeadCallId,
    string? ProjectTailCallId,
    List<string> CycleBoundaries,
    List<string> TailEdges,
    double? AvgCycleMs,
    double? AvgActiveMs,
    // 이 Flow 에 저장된 사용자 지정(FlowCycleOverride) 존재 여부.
    bool IsOverride,
    // 완료 마커 소스: "InTag" | "OutTag"(명령 ON 추정) | null. UI 배지용.
    string? TailCompletionSource = null);

public record CtOverlayDto(
    List<string> CycleBoundaries,
    List<string> TailEdges,
    double? AvgCycleMs,
    double? AvgActiveMs,
    // 완료 마커 소스: "InTag"(응답=정통) | "OutTag"(명령 종료=추정) | null(완료 없음). UI 의 '명령 ON 추정' 배지용.
    string? TailCompletionSource);
