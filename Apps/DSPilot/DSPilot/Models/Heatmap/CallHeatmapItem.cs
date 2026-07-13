// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using Ds2.Core;

namespace DSPilot.Models.Heatmap;

/// <summary>
/// Call Heatmap 셀 데이터.
/// </summary>
public class CallHeatmapItem
{
    public Guid CallId { get; set; }
    public string CallName { get; set; } = string.Empty;
    public string FlowName { get; set; } = string.Empty;
    public string WorkName { get; set; } = string.Empty;
    public double AverageGoingTime { get; set; }
    public double StdDevGoingTime { get; set; }
    public int GoingCount { get; set; }

    // ── 로버스트(중앙값 기반) 통계 — HeatmapService 메모리 캐시(CallRobustStats)에서 병합. ──
    // null = 아직 미산출(부팅 직후 첫 refresh 전) → 클라이언트가 기존 평균/CV 표시로 폴백한다.
    // 기존 AverageGoingTime/StdDevGoingTime 은 의미를 바꾸지 않는다(CCTV 오버레이·Flow KPI 가 소비).
    public double? MedianGoingTime { get; set; }
    public double? P10GoingTime { get; set; }
    public double? P90GoingTime { get; set; }
    public double? RobustCv { get; set; }
    public double? RecentRobustCv { get; set; }
    public int? DelayCount { get; set; }
    public int? RobustSampleCount { get; set; }

    public string ColorClassAvg { get; set; } = string.Empty;
    public string ColorClassStdDev { get; set; } = string.Empty;
    public string ColorClassCV { get; set; } = string.Empty;

    public double CoefficientOfVariation =>
        AverageGoingTime > 0.0 ? StdDevGoingTime / AverageGoingTime : 0.0;

    public double GetMetricValue(HeatmapMetric metric)
    {
        if (metric.IsAverageTime) return AverageGoingTime;
        if (metric.IsStdDeviation) return StdDevGoingTime;
        if (metric.IsCoefficientOfVariation) return CoefficientOfVariation;
        return 0.0;
    }

    public string GetColorClass(HeatmapMetric metric)
    {
        if (metric.IsAverageTime) return ColorClassAvg;
        if (metric.IsStdDeviation) return ColorClassStdDev;
        if (metric.IsCoefficientOfVariation) return ColorClassCV;
        return string.Empty;
    }

    public string GetTooltipText()
    {
        var cv = CoefficientOfVariation;
        var cvStatus = cv switch
        {
            < 0.1 => "매우 안정적",
            < 0.2 => "안정적",
            < 0.3 => "보통",
            < 0.5 => "불안정",
            _ => "매우 불안정",
        };
        return
            $"[{CallName}]\n━━━━━━━━━━━━━━━━━━━━\n" +
            $"평균 실행시간: {AverageGoingTime:F0} ms\n" +
            $"표준편차: {StdDevGoingTime:F0} ms\n" +
            $"변동계수: {cv:F2} ({cvStatus})\n" +
            $"실행횟수: {GoingCount}회\n" +
            $"━━━━━━━━━━━━━━━━━━━━\n💡 변동계수가 낮을수록 안정적입니다";
    }
}
