// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Collections.Concurrent;
using Ds2.Core;
using DSPilot.Models;
using DSPilot.Models.Heatmap;
using DSPilot.Repositories;

namespace DSPilot.Services;

/// <summary>
/// Heatmap 데이터 처리 서비스 (F# Performance 및 Models 모듈 사용)
/// 매트릭스 히트맵: 3개 메트릭 모두에 대해 색상 클래스를 동시 할당
/// </summary>
public class HeatmapService
{
    private readonly IDspRepository _dspRepository;
    private readonly IPlcRepository _plcRepository;
    private readonly PlcToCallMapperService _mapperService;
    private readonly AppSettingsService _settingsService;
    private readonly ILogger<HeatmapService> _logger;

    public HeatmapService(
        IDspRepository dspRepository,
        IPlcRepository plcRepository,
        PlcToCallMapperService mapperService,
        AppSettingsService settingsService,
        ILogger<HeatmapService> logger)
    {
        _dspRepository = dspRepository;
        _plcRepository = plcRepository;
        _mapperService = mapperService;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    /// Flow별로 그룹화된 매트릭스 Heatmap 데이터 조회 (전체 기간, 사전 계산 통계)
    /// 3개 메트릭 모두에 대해 색상 클래스를 할당
    /// </summary>
    public async Task<List<FlowHeatmapGroup>> GetHeatmapDataAsync()
    {
        try
        {
            // 1. 통계 데이터 조회
            var statistics = await _dspRepository.GetCallStatisticsAsync();

            if (statistics.Count == 0)
            {
                _logger.LogWarning("No Call statistics available for heatmap");
                return new List<FlowHeatmapGroup>();
            }

            // 2. Heatmap 아이템 리스트 생성
            var items = statistics.Select(s => new CallHeatmapItem
            {
                CallId = s.CallId,
                CallName = s.CallName,
                FlowName = s.FlowName,
                WorkName = s.WorkName,
                AverageGoingTime = s.AverageGoingTime,
                StdDevGoingTime = s.StdDevGoingTime,
                GoingCount = s.GoingCount,
                ColorClassAvg = "",
                ColorClassStdDev = "",
                ColorClassCV = ""
            }).ToList();

            return AssignColorsAndGroup(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get heatmap data");
            return new List<FlowHeatmapGroup>();
        }
    }

    /// <summary>
    /// Call의 실행 이력 조회 (PLCTagLog에서 InTag↔OutTag 매칭) - 전체 기간
    /// </summary>
    public async Task<List<CallExecutionRecord>> GetCallExecutionHistoryAsync(Guid callId)
    {
        return await GetCallExecutionHistoryAsync(callId, null, null, null);
    }

    /// <summary>
    /// Call의 실행 이력 조회 (PLCTagLog에서 InTag↔OutTag 매칭) - 필터 적용
    /// </summary>
    public async Task<List<CallExecutionRecord>> GetCallExecutionHistoryAsync(
        Guid callId, DateTime? startTime, DateTime? endTime, int? maxCycles)
    {
        var records = new List<CallExecutionRecord>();

        try
        {
            // 1. Call의 InTag/OutTag 주소 조회
            var tags = _mapperService.GetCallTagsByCallId(callId);
            if (!tags.HasValue)
            {
                _logger.LogWarning("Call {CallId}: No tag mapping found", callId);
                return records;
            }

            var (inTag, outTag) = tags.Value;
            if (string.IsNullOrEmpty(inTag) || string.IsNullOrEmpty(outTag))
            {
                _logger.LogWarning("Call {CallId}: InTag or OutTag is missing (InTag={InTag}, OutTag={OutTag})",
                    callId, inTag, outTag);
                return records;
            }

            // 2. 시간 범위 결정 (Unspecified Kind는 Local로 보정 → UTC 변환 보장)
            var queryStart = startTime.HasValue ? AsLocal(startTime.Value) : (DateTime?)null;
            var queryEnd = endTime.HasValue ? AsLocal(endTime.Value) : (DateTime?)null;

            if (!queryStart.HasValue || !queryEnd.HasValue)
            {
                var oldest = await _plcRepository.GetOldestLogDateTimeAsync();
                var latest = await _plcRepository.GetLatestLogDateTimeAsync();
                if (!oldest.HasValue || !latest.HasValue)
                {
                    _logger.LogWarning("Call {CallId}: No PLC log data available", callId);
                    return records;
                }
                queryStart ??= oldest.Value;
                queryEnd ??= latest.Value;
            }

            records = await ComputeExecutionRecordsAsync(inTag, outTag, queryStart.Value, queryEnd.Value, maxCycles);

            _logger.LogInformation(
                "Call {CallId}: Matched {Count} executions", callId, records.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get execution history for Call {CallId}", callId);
        }

        return records;
    }

    /// <summary>
    /// 전체 Call 의 동작편차 누적 통계(실행수/평균/표준편차)를 원시 plcTagLog 엣지에서 다시 도출해
    /// dspCall 에 절대값으로 덮어쓴다(self-heal). 라이브 누산기(Welford)가 캡 적용 전 누적한 이상치
    /// (라인 정지·엣지 유실로 분 단위로 늘어진 Going) 때문에 매트릭스 편차가 수천 %로 부풀던 오염을 청소한다.
    /// 상세 패널과 <b>동일한</b> <see cref="ComputeExecutionRecordsAsync"/>(= MaxCallGoingTimeMs/MinCallGoingTimeMs 캡)를
    /// 재사용하므로, 정리 후 매트릭스(저장 통계)와 상세 패널(엣지 재계산)이 같은 필터 기준을 본다.
    /// </summary>
    /// <returns>dspCall 에 통계를 덮어쓴 Call 수(0 = 매핑/로그 미준비 → 호출부가 재시도 판단).</returns>
    public async Task<int> RecomputeAllCallGoingStatisticsAsync(CancellationToken ct = default)
    {
        var pairs = _mapperService.GetAllCallTagPairs();
        if (pairs.Count == 0) return 0;

        var oldest = await _plcRepository.GetOldestLogDateTimeAsync();
        var latest = await _plcRepository.GetLatestLogDateTimeAsync();
        if (!oldest.HasValue || !latest.HasValue) return 0;

        var stats = new List<(Guid CallId, int Count, double Avg, double StdDev)>(pairs.Count);
        foreach (var p in pairs)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(p.InTag) || string.IsNullOrEmpty(p.OutTag))
                continue;

            var records = await ComputeExecutionRecordsAsync(p.InTag!, p.OutTag!, oldest.Value, latest.Value, null);
            if (records.Count == 0)
            {
                // 유효 표본 0(전부 캡 밖이거나 매칭 엣지 없음) → 0 으로 리셋해 과거 오염 제거(GoingCount=0 은 히트맵서 제외됨).
                stats.Add((p.CallId, 0, 0.0, 0.0));
                continue;
            }

            // 모집단 표준편차 σ = sqrt(Σ(x-μ)²/n) — Welford 의 sqrt(M2/n) 및 상세 차트 정의와 일치.
            double mean = records.Average(r => r.GoingTimeMs);
            double sumSq = records.Sum(r => (r.GoingTimeMs - mean) * (r.GoingTimeMs - mean));
            stats.Add((p.CallId, records.Count, mean, Math.Sqrt(sumSq / records.Count)));
        }

        var written = await _dspRepository.SetCallGoingStatisticsAsync(stats);
        _logger.LogInformation(
            "[Heatmap] 동작편차 통계 self-heal(캡 재도출): {Written}/{Total} Call 갱신", written, stats.Count);
        return written;
    }

    /// <summary>
    /// 메트릭 표시 이름 반환
    /// </summary>
    public static string GetMetricDisplayName(HeatmapMetric metric) =>
        HeatmapPerformance.GetMetricDisplayName(metric);

    /// <summary>
    /// 메트릭 값 포맷
    /// </summary>
    public static string FormatMetricValue(HeatmapMetric metric, double value) =>
        HeatmapPerformance.FormatMetricValue(metric, value);

    /// <summary>
    /// 컴팩트 셀용 짧은 포맷
    /// </summary>
    public static string FormatMetricValueShort(HeatmapMetric metric, double value)
    {
        if (metric.IsAverageTime)
            return value < 1000 ? $"{value:F0}" : $"{value / 1000.0:F1}s";
        if (metric.IsStdDeviation)
            return value < 1000 ? $"{value:F0}" : $"{value / 1000.0:F1}s";
        if (metric.IsCoefficientOfVariation)
            return $"{value:F2}";
        return $"{value:F1}";
    }

    // ===== Private Methods =====

    /// <summary>
    /// CallHeatmapItem 리스트에 색상 클래스를 할당하고 Flow별로 그룹화
    /// </summary>
    private List<FlowHeatmapGroup> AssignColorsAndGroup(List<CallHeatmapItem> items)
    {
        if (items.Count == 0)
            return new List<FlowHeatmapGroup>();

        // 3개 메트릭 각각에 대해 min/max 계산
        var allAvg = items.Select(i => i.AverageGoingTime).ToList();
        var allStdDev = items.Select(i => i.StdDevGoingTime).ToList();
        var allCV = items.Select(i => i.CoefficientOfVariation).ToList();

        var minAvg = allAvg.Min(); var maxAvg = allAvg.Max();
        var minStdDev = allStdDev.Min(); var maxStdDev = allStdDev.Max();
        var minCV = allCV.Min(); var maxCV = allCV.Max();

        // 3개 메트릭 색상 클래스 동시 할당
        foreach (var item in items)
        {
            item.ColorClassAvg = HeatmapPerformance.AssignColorClass(HeatmapMetric.AverageTime, item.AverageGoingTime, minAvg, maxAvg);
            item.ColorClassStdDev = HeatmapPerformance.AssignColorClass(HeatmapMetric.StdDeviation, item.StdDevGoingTime, minStdDev, maxStdDev);
            item.ColorClassCV = HeatmapPerformance.AssignColorClass(HeatmapMetric.CoefficientOfVariation, item.CoefficientOfVariation, minCV, maxCV);
        }

        // Flow별로 그룹화 + Flow 수준 집계 색상
        var groups = items
            .GroupBy(item => item.FlowName)
            .Select(g =>
            {
                var calls = g.OrderBy(c => c.CallName).ToList();
                var flowAvg = calls.Average(c => c.AverageGoingTime);
                var flowStdDev = calls.Average(c => c.StdDevGoingTime);
                var flowCV = calls.Average(c => c.CoefficientOfVariation);

                return new FlowHeatmapGroup
                {
                    FlowName = g.Key,
                    Calls = calls,
                    IsExpanded = true,
                    FlowColorClassAvg = HeatmapPerformance.AssignColorClass(HeatmapMetric.AverageTime, flowAvg, minAvg, maxAvg),
                    FlowColorClassStdDev = HeatmapPerformance.AssignColorClass(HeatmapMetric.StdDeviation, flowStdDev, minStdDev, maxStdDev),
                    FlowColorClassCV = HeatmapPerformance.AssignColorClass(HeatmapMetric.CoefficientOfVariation, flowCV, minCV, maxCV)
                };
            })
            .OrderBy(g => g.FlowName)
            .ToList();

        _logger.LogInformation(
            "Heatmap matrix data loaded: {FlowCount} flows, {CallCount} calls",
            groups.Count, items.Count);

        return groups;
    }

    /// <summary>
    /// InTag/OutTag Rising Edge를 매칭하여 실행 기록 리스트를 생성
    /// </summary>
    private async Task<List<CallExecutionRecord>> ComputeExecutionRecordsAsync(
        string inTag, string outTag, DateTime startTime, DateTime endTime, int? maxCycles)
    {
        var records = new List<CallExecutionRecord>();

        var inTagEdges = await _plcRepository.FindRisingEdgesAsync(inTag, startTime, endTime);
        var outTagEdges = await _plcRepository.FindRisingEdgesAsync(outTag, startTime, endTime);

        // OutTag Rising(동작 시작) → InTag Rising(동작 종료) 순서로 매칭하여 GoingTime 계산
        int inIndex = 0;
        int executionNumber = 0;
        var historyView = _settingsService.LoadSettings().HistoryView;
        var maxGoingTime = historyView.MaxCallGoingTimeMs;
        var minGoingTime = historyView.MinCallGoingTimeMs;

        foreach (var outTime in outTagEdges)
        {
            while (inIndex < inTagEdges.Count && inTagEdges[inIndex] <= outTime)
            {
                inIndex++;
            }

            if (inIndex >= inTagEdges.Count)
                break;

            var inTime = inTagEdges[inIndex];
            var goingTimeMs = (int)(inTime - outTime).TotalMilliseconds;

            // MaxCallGoingTimeMs 초과 시 InTag 누락으로 판단 → OutTag를 스킵하고 InTag는 유지하여 재매칭
            if (goingTimeMs > maxGoingTime)
            {
                continue;
            }

            // 최소 실행시간 미달(노이즈·오감지로 인한 짧은 동작) 시 제외. inIndex 는 정상 진행하여 페어 소비.
            if (goingTimeMs > 0 && (minGoingTime <= 0 || goingTimeMs >= minGoingTime))
            {
                executionNumber++;
                records.Add(new CallExecutionRecord
                {
                    ExecutionNumber = executionNumber,
                    Timestamp = outTime,
                    GoingTimeMs = goingTimeMs
                });
            }

            inIndex++;
        }

        // Cycles 모드: 최근 N개만 유지
        if (maxCycles.HasValue && records.Count > maxCycles.Value)
        {
            records = records.Skip(records.Count - maxCycles.Value).ToList();
            // ExecutionNumber 재할당
            for (int i = 0; i < records.Count; i++)
                records[i].ExecutionNumber = i + 1;
        }

        return records;
    }

    /// <summary>
    /// Unspecified Kind의 DateTime을 Local로 지정 (toSqliteUtcString에서 UTC 변환이 정상 동작하도록)
    /// </summary>
    private static DateTime AsLocal(DateTime dt) =>
        dt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dt, DateTimeKind.Local)
            : dt;
}

/// <summary>
/// Call 개별 실행 기록
/// </summary>
public class CallExecutionRecord
{
    public int ExecutionNumber { get; set; }
    public DateTime Timestamp { get; set; }
    public int GoingTimeMs { get; set; }
}
