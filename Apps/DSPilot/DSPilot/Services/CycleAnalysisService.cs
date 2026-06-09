// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Models.Analysis;
using DSPilot.Models.Plc;
using DSPilot.Repositories;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;

namespace DSPilot.Services;

/// <summary>
/// 사이클 분석 서비스 - 하이브리드 접근 방식
/// 1. 자동 사이클 경계 탐지 (Head Call InTag 기반)
/// 2. 수동 시간 범위 분석
/// 3. 통합 상세 분석
/// </summary>
public class CycleAnalysisService
{
    private const int MaxRenderedGanttItems = 2000;
    private readonly IDspRepository _dspRepository;
    private readonly IPlcRepository _plcRepository;
    private readonly PlcToCallMapperService _mapperService;
    private readonly DsProjectService _projectService;
    private readonly ILogger<CycleAnalysisService> _logger;

    public CycleAnalysisService(
        IDspRepository dspRepository,
        IPlcRepository plcRepository,
        PlcToCallMapperService mapperService,
        DsProjectService projectService,
        ILogger<CycleAnalysisService> logger)
    {
        _dspRepository = dspRepository;
        _plcRepository = plcRepository;
        _mapperService = mapperService;
        _projectService = projectService;
        _logger = logger;
    }

    #region 3. Helper Methods

    /// <summary>
    /// Flow 이름으로 Flow 찾기
    /// </summary>
    private Flow? GetFlowByName(string flowName)
    {
        if (!_projectService.IsLoaded)
            return null;

        var systems = _projectService.GetActiveSystems();
        foreach (var system in systems)
        {
            var flows = _projectService.GetFlows(system.Id);
            var flow = flows.FirstOrDefault(f => f.Name == flowName);
            if (flow != null)
                return flow;
        }

        return null;
    }

    /// <summary>
    /// Flow의 Head Call 찾기 (첫 번째 Work의 첫 번째 Call)
    /// </summary>
    private Call? GetHeadCall(Flow flow)
    {
        var works = _projectService.GetWorks(flow.Id);
        if (works.Count == 0)
            return null;

        // 첫 번째 Work의 첫 번째 Call
        var firstWork = works.First();
        var calls = _projectService.GetCalls(firstWork.Id);

        return calls.Count > 0 ? calls.First() : null;
    }

    /// <summary>
    /// Flow 내 Work 이름 목록 (정의 순서, 중복 제거). 시프트 생산목표의 Work 드롭다운(Flow→Work) 용.
    /// </summary>
    public List<string> GetWorkNamesForFlow(string flowName)
    {
        var flow = GetFlowByName(flowName);
        if (flow == null) return new();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var work in _projectService.GetWorks(flow.Id))
        {
            if (!string.IsNullOrEmpty(work.Name) && seen.Add(work.Name))
                result.Add(work.Name);
        }
        return result;
    }

    /// <summary>
    /// 한 Work 의 "완료" 신호 rising edge 수를 [<paramref name="startUtc"/>, <paramref name="endUtc"/>] 구간에서 센다.
    /// 시프트 생산목표(만든 수)의 Work 단위 집계용. 완료 = InTag↑ 정본(엔진 규약, <see cref="PlcToCallMapperService"/> 참조).
    /// 대표 신호 = Work 의 마지막(작업을 끝내는) Call 의 InTag, InTag 없으면 그 Call 의 OutTag.
    /// 시각은 UTC(<paramref name="startUtc"/>/<paramref name="endUtc"/>) — plcTagLog 비교 포맷(ToSqliteUtcString)과 일치.
    /// </summary>
    public async Task<int> CountWorkCompletionsAsync(
        string flowName, string workName, DateTime startUtc, DateTime endUtc)
    {
        var tag = ResolveWorkCompletionTag(flowName, workName);
        if (string.IsNullOrEmpty(tag)) return 0;

        var edges = await _plcRepository.FindRisingEdgesAsync(tag, startUtc, endUtc);
        return edges.Count;
    }

    /// <summary>Work 의 완료 신호 주소(마지막 Call 의 InTag, 없으면 OutTag). 매핑 부재 시 null.</summary>
    private string? ResolveWorkCompletionTag(string flowName, string workName)
    {
        var flow = GetFlowByName(flowName);
        if (flow == null) return null;

        var work = _projectService.GetWorks(flow.Id).FirstOrDefault(w => w.Name == workName);
        if (work == null) return null;

        var calls = _projectService.GetCalls(work.Id);
        if (calls.Count == 0) return null;

        // 완료 = InTag↑. 마지막 Call 부터 거슬러 올라가며 InTag 보유 Call 을 우선 채택.
        for (int i = calls.Count - 1; i >= 0; i--)
        {
            var tags = _mapperService.GetCallTagsByCallId(calls[i].Id);
            if (tags.HasValue && !string.IsNullOrEmpty(tags.Value.InTag))
                return tags.Value.InTag;
        }

        // InTag 가 전혀 없는(OutOnly) Work 면 마지막 Call 의 OutTag 로 폴백.
        var last = _mapperService.GetCallTagsByCallId(calls[^1].Id);
        return last?.OutTag;
    }

    /// <summary>
    /// 시간 범위 내 사이클 경계(rising edge) 시각 목록 조회 (경량).
    /// Gantt 차트에서 비가동 사이클 구간 표시용.
    /// </summary>
    public async Task<List<DateTime>> GetCycleBoundaryTimesAsync(string flowName, DateTime startTime, DateTime endTime)
    {
        var flow = GetFlowByName(flowName);
        if (flow == null) return new();

        var headCall = GetHeadCall(flow);
        if (headCall == null) return new();

        var tags = _mapperService.GetCallTagsByCallId(headCall.Id);
        if (!tags.HasValue || string.IsNullOrEmpty(tags.Value.OutTag)) return new();

        // 진영 B: Head OutTag↑(PLC 명령) = 사이클 시작 경계.
        var edges = await _plcRepository.FindRisingEdgesAsync(tags.Value.OutTag, startTime, endTime);
        return edges;
    }

    /// <summary>
    /// 시간 범위 기준 실제 PLC 태그 상태를 읽어 In/Out의 ON~OFF 구간을 Gantt segment로 변환한다.
    /// cycle-time-analysis 페이지 전용 실제 IO 타임라인.
    /// </summary>
    public async Task<GanttChartData> GetActualIoSignalSegmentsInTimeRangeAsync(
        string flowName,
        DateTime startTime,
        DateTime endTime)
    {
        var flow = GetFlowByName(flowName);
        if (flow == null)
        {
            _logger.LogWarning("Flow '{FlowName}' not found", flowName);
            return new GanttChartData { FlowName = flowName };
        }

        var laneDefinitions = BuildSignalLaneDefinitions(flow);
        if (laneDefinitions.Count == 0)
        {
            _logger.LogWarning("No IO signal mapping found for Flow '{FlowName}'", flowName);
            return new GanttChartData { FlowName = flowName, StartTime = startTime, EndTime = endTime };
        }

        var addresses = laneDefinitions
            .Select(def => def.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var addressSet = new HashSet<string>(addresses, StringComparer.OrdinalIgnoreCase);

        // 3개 쿼리를 병렬로 실행 — 각자 고유 SqliteConnection 사용해 contention 없음.
        // 직렬 합계 vs max 1개 → 시간 범위 클수록 효과 큼.
        var allTagsTask = _plcRepository.GetAllTagsAsync();
        var latestBeforeTask = _plcRepository.GetLatestLogsByAddressesBeforeAsync(addresses, startTime);
        var rangeLogsTask = _plcRepository.GetMultipleTagLogsInRangeAsync(addresses, startTime, endTime);

        await Task.WhenAll(allTagsTask, latestBeforeTask, rangeLogsTask);

        var allTags = allTagsTask.Result;
        var latestBeforeLogs = latestBeforeTask.Result;
        var logs = rangeLogsTask.Result;

        var tagById = allTags
            .Where(tag => addressSet.Contains(tag.Address))
            .ToDictionary(tag => tag.Id, tag => tag);
        var tagNameByAddress = allTags
            .Where(tag => addressSet.Contains(tag.Address))
            .GroupBy(tag => tag.Address, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Name,
                StringComparer.OrdinalIgnoreCase);

        var initialStateByAddress = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var log in latestBeforeLogs.OrderBy(log => log.DateTime).ThenBy(log => log.Id))
        {
            if (tagById.TryGetValue(log.PlcTagId, out var tag))
            {
                initialStateByAddress[tag.Address] = NormalizePlcBoolValue(log.Value);
            }
        }

        var logsByAddress = logs
            .Where(log => !string.IsNullOrWhiteSpace(log.PlcTag?.Address) || !string.IsNullOrWhiteSpace(log.Address))
            .GroupBy(
                log => log.PlcTag?.Address ?? log.Address,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(log => log.DateTime).ThenBy(log => log.Id).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var items = new List<GanttChartItem>();

        foreach (var lane in laneDefinitions)
        {
            logsByAddress.TryGetValue(lane.Address, out var signalLogs);
            signalLogs ??= new List<PlcTagLogEntity>();

            var currentState = initialStateByAddress.TryGetValue(lane.Address, out var initialState) && initialState;
            DateTime? segmentStart = currentState ? startTime : null;
            var resolvedTagName = tagNameByAddress.GetValueOrDefault(lane.Address, lane.Address);

            foreach (var log in signalLogs)
            {
                if (!string.IsNullOrWhiteSpace(log.PlcTag?.Name))
                {
                    resolvedTagName = log.PlcTag.Name;
                }
                else if (!string.IsNullOrWhiteSpace(log.TagName))
                {
                    resolvedTagName = log.TagName;
                }

                var newState = NormalizePlcBoolValue(log.Value);
                if (newState == currentState)
                {
                    continue;
                }

                if (newState)
                {
                    currentState = true;
                    segmentStart = log.DateTime < startTime ? startTime : log.DateTime;
                    continue;
                }

                if (currentState && segmentStart.HasValue)
                {
                    var segmentEnd = log.DateTime > endTime ? endTime : log.DateTime;
                    if (segmentEnd > segmentStart.Value)
                    {
                        items.Add(BuildSignalSegmentItem(
                            lane,
                            flowName,
                            resolvedTagName,
                            segmentStart.Value,
                            segmentEnd,
                            startTime));
                    }
                }

                currentState = false;
                segmentStart = null;
            }

            if (currentState && segmentStart.HasValue && endTime > segmentStart.Value)
            {
                items.Add(BuildSignalSegmentItem(
                    lane,
                    flowName,
                    resolvedTagName,
                    segmentStart.Value,
                    endTime,
                    startTime));
            }
        }

        var totalEventCount = items.Count;
        var renderedItems = totalEventCount > MaxRenderedGanttItems
            ? items
                .OrderByDescending(item => item.GoingStartTime)
                .Take(MaxRenderedGanttItems)
                .OrderBy(item => item.GoingStartTime)
                .ToList()
            : items
                .OrderBy(item => item.GoingStartTime)
                .ToList();

        var actualEventStartTime = renderedItems.Count > 0
            ? renderedItems.Min(item => item.GoingStartTime)
            : (DateTime?)null;
        var actualEventEndTime = renderedItems.Count > 0
            ? renderedItems.Max(item => item.FinishTime ?? item.GoingStartTime)
            : (DateTime?)null;

        return new GanttChartData
        {
            CycleId = "ActualIoSignals",
            FlowName = flowName,
            CycleNumber = 0,
            StartTime = startTime,
            EndTime = endTime,
            ActualEventStartTime = actualEventStartTime,
            ActualEventEndTime = actualEventEndTime,
            CT = (int)(endTime - startTime).TotalMilliseconds,
            TotalLanes = laneDefinitions
                .Select(def => def.Lane)
                .Distinct()
                .Count(),
            LaneLabels = laneDefinitions
                .OrderBy(def => def.Lane)
                .GroupBy(def => def.Lane)
                .Select(group => group.First().Label)
                .ToList(),
            Items = renderedItems,
            TotalEventCount = totalEventCount,
            RenderedEventCount = renderedItems.Count,
            IsTruncated = totalEventCount > renderedItems.Count
        };
    }
    private List<SignalLaneDefinition> BuildSignalLaneDefinitions(Flow flow)
    {
        var laneDefinitions = new List<SignalLaneDefinition>();
        var works = _projectService.GetWorks(flow.Id);
        var laneIndex = 0;

        foreach (var work in works)
        {
            var calls = _projectService.GetCalls(work.Id);
            foreach (var call in calls)
            {
                var tags = _mapperService.GetCallTagsByCallId(call.Id);
                if (!tags.HasValue)
                    continue;

                if (string.IsNullOrWhiteSpace(tags.Value.InTag) &&
                    string.IsNullOrWhiteSpace(tags.Value.OutTag))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(tags.Value.InTag))
                {
                    laneDefinitions.Add(new SignalLaneDefinition(
                        tags.Value.InTag,
                        call.Id,
                        call.Name,
                        work.Name,
                        IOEventType.InTag,
                        call.Name,
                        laneIndex));
                }

                if (!string.IsNullOrWhiteSpace(tags.Value.OutTag))
                {
                    laneDefinitions.Add(new SignalLaneDefinition(
                        tags.Value.OutTag,
                        call.Id,
                        call.Name,
                        work.Name,
                        IOEventType.OutTag,
                        call.Name,
                        laneIndex));
                }

                laneIndex++;
            }
        }

        return laneDefinitions;
    }
    private static GanttChartItem BuildSignalSegmentItem(
        SignalLaneDefinition lane,
        string flowName,
        string tagName,
        DateTime segmentStart,
        DateTime segmentEnd,
        DateTime chartStartTime)
    {
        var relativeStart = Math.Max(0, (int)(segmentStart - chartStartTime).TotalMilliseconds);
        var relativeEnd = Math.Max(relativeStart, (int)(segmentEnd - chartStartTime).TotalMilliseconds);
        var duration = Math.Max(0, relativeEnd - relativeStart);

        return new GanttChartItem
        {
            CallId = lane.CallId,
            CallName = lane.CallName,
            WorkName = lane.WorkName,
            FlowName = flowName,
            TagName = string.IsNullOrWhiteSpace(tagName) ? lane.Address : tagName,
            TagAddress = lane.Address,
            RelativeStart = relativeStart,
            RelativeEnd = relativeEnd,
            Duration = duration,
            Lane = lane.Lane,
            GoingStartTime = segmentStart,
            FinishTime = segmentEnd,
            EventType = lane.EventType
        };
    }

    private static bool NormalizePlcBoolValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" => true,
            "true" => true,
            "on" => true,
            _ => false
        };
    }

    #endregion
}

public class GanttChartData
{
    public string CycleId { get; set; } = string.Empty;
    public string FlowName { get; set; } = string.Empty;
    public int CycleNumber { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public DateTime? ActualEventStartTime { get; set; }
    public DateTime? ActualEventEndTime { get; set; }
    public int? CT { get; set; }
    public int? MT { get; set; }
    public int? WT { get; set; }
    public int TotalLanes { get; set; }
    public List<string> LaneLabels { get; set; } = new();
    public int TotalEventCount { get; set; }
    public int RenderedEventCount { get; set; }
    public bool IsTruncated { get; set; }
    public List<GanttChartItem> Items { get; set; } = new();
    public List<string> CriticalPath { get; set; } = new();
}

public class GanttChartItem
{
    public Guid CallId { get; set; }
    public string CallName { get; set; } = string.Empty;
    public string WorkName { get; set; } = string.Empty;
    public string FlowName { get; set; } = string.Empty;
    public string TagName { get; set; } = string.Empty;
    public string TagAddress { get; set; } = string.Empty;
    public int RelativeStart { get; set; }
    public int? RelativeEnd { get; set; }
    public int? Duration { get; set; }
    public int Lane { get; set; }
    public DateTime GoingStartTime { get; set; }
    public DateTime? FinishTime { get; set; }
    public IOEventType EventType { get; set; }
}
internal sealed record SignalLaneDefinition(
    string Address,
    Guid CallId,
    string CallName,
    string WorkName,
    IOEventType EventType,
    string Label,
    int Lane);
