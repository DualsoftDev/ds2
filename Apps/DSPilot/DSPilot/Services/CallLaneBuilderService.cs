// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Controllers;
using DSPilot.Models.Analysis;

namespace DSPilot.Services;

/// <summary>
/// Flow 의 IO 세그먼트를 lane(Call) 단위로 묶고 ON 구간을 병합해 <see cref="CtLaneDto"/> 리스트로 빌드한다.
/// 원래 <see cref="Controllers.CallTestController"/>.Load 안에 인라인이던 lane/interval 빌드를 추출한 것으로,
/// CallTest(간트 화면)와 <see cref="AutoCalibrationService"/>(자동 실측 보정)가 동일 코드를 공유한다 → 드리프트 방지.
/// 결과 lane 은 Out/In 인터벌(명령/응답 ON 구간)과 소속 ApiCall(보정 대상 Device Work 포함)을 담아,
/// 화면 렌더링과 command→response span 집계(<see cref="ApiSpanMath"/>) 양쪽에 쓰인다.
/// CycleAnalysisService 가 Scoped 이므로 본 서비스도 Scoped — 싱글톤 소비자(백그라운드)는 스코프를 열어 해석한다.
/// </summary>
public sealed class CallLaneBuilderService
{
    private readonly CycleAnalysisService _cycleAnalysis;
    private readonly PlcToCallMapperService _callMapper;
    private readonly DsProjectService _project;

    public CallLaneBuilderService(
        CycleAnalysisService cycleAnalysis,
        PlcToCallMapperService callMapper,
        DsProjectService project)
    {
        _cycleAnalysis = cycleAnalysis;
        _callMapper = callMapper;
        _project = project;
    }

    /// <summary>
    /// [start,end] 범위의 IO 세그먼트를 lane 단위로 그룹핑 + interval merge 하여 lane 리스트를 만든다.
    /// 시각은 호출자가 이미 Local(DB-local tz) 로 마킹한 값이어야 한다(CallTestController.Load 의 AsLocal 경로와 동일).
    /// 데이터 없는 Call 도 빈 인터벌 lane 으로 채워 정렬 후 반환(화면에서 누락 없이 표시).
    /// </summary>
    public async Task<List<CtLaneDto>> BuildLanesAsync(string flowName, DateTime start, DateTime end)
    {
        var data = await _cycleAnalysis.GetActualIoSignalSegmentsInTimeRangeAsync(flowName, start, end);

        // lane 단위 grouping + interval merge (CallTestController.Load 와 동일).
        var lanes = data.Items
            .GroupBy(i => i.Lane)
            .Select(g =>
            {
                var first = g.First();
                var intervals = MergeIntervals(
                    g.Select(i => (i.GoingStartTime, i.FinishTime ?? i.GoingStartTime)).ToList());
                // 태그별 ON 구간 분리 — OutTag(명령)/InTag(응답). command→response span 집계의 입력.
                var outIntervals = MergeIntervals(
                    g.Where(i => i.EventType == IOEventType.OutTag)
                     .Select(i => (i.GoingStartTime, i.FinishTime ?? i.GoingStartTime)).ToList());
                var inIntervals = MergeIntervals(
                    g.Where(i => i.EventType == IOEventType.InTag)
                     .Select(i => (i.GoingStartTime, i.FinishTime ?? i.GoingStartTime)).ToList());
                var tags = _callMapper.GetCallTagsByCallId(first.CallId);
                return new CtLaneDto(
                    first.CallId.ToString(),
                    first.CallName,
                    first.WorkName,
                    first.Lane,
                    intervals.Select(iv => new CtIntervalDto(IsoLocal(iv.Start), IsoLocal(iv.End))).ToList(),
                    tags?.InTag,
                    tags?.OutTag,
                    outIntervals.Select(iv => new CtIntervalDto(IsoLocal(iv.Start), IsoLocal(iv.End))).ToList(),
                    inIntervals.Select(iv => new CtIntervalDto(IsoLocal(iv.Start), IsoLocal(iv.End))).ToList(),
                    ResolveApiCalls(first.CallId));
            })
            .OrderBy(l => l.LaneIndex)
            .ToList();

        // 시간 범위에 데이터가 없는 Call 도 표시 — 빈 인터벌로 추가.
        var laneIds = lanes.Select(l => l.CallId).ToHashSet();
        int nextLane = lanes.Count > 0 ? lanes.Max(l => l.LaneIndex) + 1 : 0;
        foreach (var c in _callMapper.GetAllCallTagPairs()
                     .Where(p => string.Equals(p.FlowName, flowName, StringComparison.OrdinalIgnoreCase))
                     .Where(c => !laneIds.Contains(c.CallId.ToString())))
        {
            lanes.Add(new CtLaneDto(c.CallId.ToString(), c.CallName, c.WorkName, nextLane++,
                new List<CtIntervalDto>(), c.InTag, c.OutTag,
                new List<CtIntervalDto>(), new List<CtIntervalDto>(),
                ResolveApiCalls(c.CallId)));
        }

        return lanes;
    }

    /// <summary>
    /// Call lane 확장용 — 이 Call 에 소속된 ApiCall(들) + 보정 대상 Device Work 의 현재 AASX duration(ms).
    /// 읽기 전용(store 불변). 현재 PoC 는 1:1 이라 보통 1개. 미로드/미해석 시 빈 리스트.
    /// </summary>
    private List<CtApiCallDto> ResolveApiCalls(Guid callId)
        => _project.GetCallApiCallDetails(callId)
            .Select(d => new CtApiCallDto(
                d.ApiCallId.ToString(),
                d.Name,
                d.InTag,
                d.OutTag,
                d.TargetWorkId?.ToString(),
                d.CurrentDurationMs,
                d.CurrentMinMs,
                d.CurrentMaxMs))
            .ToList();

    private static List<(DateTime Start, DateTime End)> MergeIntervals(List<(DateTime Start, DateTime End)> intervals)
    {
        var merged = new List<(DateTime Start, DateTime End)>();
        if (intervals.Count == 0) return merged;

        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
        var curS = intervals[0].Start;
        var curE = intervals[0].End;
        for (int i = 1; i < intervals.Count; i++)
        {
            var (s, e) = intervals[i];
            if (s <= curE)
            {
                if (e > curE) curE = e;
            }
            else
            {
                if (curE > curS) merged.Add((curS, curE));
                curS = s;
                curE = e;
            }
        }
        if (curE > curS) merged.Add((curS, curE));
        return merged;
    }

    /// <summary>로컬 tz ISO("o"). 클라이언트는 new Date() 로, 서버 집계는 DateTimeOffset 으로 파싱.</summary>
    private static string IsoLocal(DateTime dt) => dt.ToString("o");
}
