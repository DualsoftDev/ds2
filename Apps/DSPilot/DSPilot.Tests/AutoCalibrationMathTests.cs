// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// ActionOver Max 임계 자동 산출 정책을 코드로 고정한다:
///   Max = round(max(중앙값 × (1 + 여유율), 클린 실측최대)) + 절대 여유.
/// 클린 실측최대 = 중앙값×3 초과 span(통신 오염 단발)을 제외한 최댓값 — "이미 관측된 정상 가동은
/// 절대 이상 판정하지 않는다" 불변식(이중모드 보호)과 "단발 이상치가 임계를 부풀리지 않는다"를 동시에 보장.
/// (<see cref="ApiSpanMath.MedianAndCleanMax"/> + <see cref="AutoCalibrationService.ComputeMaxThresholdMs"/>)
/// </summary>
public class AutoCalibrationMathTests
{
    [Fact]
    public void Work_duration_calibration_is_manual_only()
    {
        Assert.False(typeof(Microsoft.Extensions.Hosting.IHostedService)
            .IsAssignableFrom(typeof(AutoCalibrationService)));
    }

    // ── MedianAndCleanMax ──────────────────────────────────────────────────

    [Fact]
    public void MedianAndCleanMax_empty_returns_nulls()
    {
        var (median, cleanMax) = ApiSpanMath.MedianAndCleanMax(new double[0]);
        Assert.Null(median);
        Assert.Null(cleanMax);
    }

    [Fact]
    public void MedianAndCleanMax_single_span_returns_itself()
    {
        var (median, cleanMax) = ApiSpanMath.MedianAndCleanMax(new[] { 10_000.0 });
        Assert.Equal(10_000.0, median);
        Assert.Equal(10_000.0, cleanMax);
    }

    [Fact]
    public void MedianAndCleanMax_excludes_comm_spike_beyond_3x_median()
    {
        // 정상 1초 × 5 + 재연결 burst 로 부풀려진 14초 1건 — 14초는 클린최대에 반영되면 안 된다.
        var spans = new[] { 1_000.0, 1_000.0, 1_000.0, 1_000.0, 1_000.0, 14_000.0 };
        var (median, cleanMax) = ApiSpanMath.MedianAndCleanMax(spans);
        Assert.Equal(1_000.0, median);
        Assert.Equal(1_000.0, cleanMax);
    }

    [Fact]
    public void MedianAndCleanMax_keeps_bimodal_long_mode_within_3x()
    {
        // 정상이 두 갈래(60초/100초)인 디바이스 — 느린 정상 경로(≤3×중앙값)는 클린최대로 보호.
        var spans = new[] { 60_000.0, 60_000.0, 60_000.0, 100_000.0, 100_000.0 };
        var (median, cleanMax) = ApiSpanMath.MedianAndCleanMax(spans);
        Assert.Equal(60_000.0, median);
        Assert.Equal(100_000.0, cleanMax);
    }

    // ── ComputeMaxThresholdMs ─────────────────────────────────────────────

    [Fact]
    public void Narrow_distribution_uses_median_margin_formula()
    {
        // 중앙값 10초, 클린최대 10.8초(클램프 비발동) → 10×1.6 + 5초 = 21초.
        int max = AutoCalibrationService.ComputeMaxThresholdMs(10_000, 10_800, 0.60, 5_000);
        Assert.Equal(21_000, max);
    }

    [Fact]
    public void Bimodal_distribution_clamps_to_clean_max()
    {
        // 중앙값×1.6(96초)이 정상 느린 경로(100초) 안쪽 → 클린최대로 클램프 + 5초 = 105초.
        int max = AutoCalibrationService.ComputeMaxThresholdMs(60_000, 100_000, 0.60, 5_000);
        Assert.Equal(105_000, max);
    }

    [Fact]
    public void Negative_margin_and_abs_are_clamped_to_zero()
    {
        // 방어: 음수 여유율/여유값은 0 취급 — 임계가 중앙값 밑으로 내려가지 않는다.
        int max = AutoCalibrationService.ComputeMaxThresholdMs(1_000, 1_000, -0.5, -100);
        Assert.Equal(1_000, max);
    }

    [Fact]
    public void Comm_spike_does_not_inflate_threshold_end_to_end()
    {
        // 오염 포함 span → 클린최대 산출 → 임계. 14초 단발이 있어도 임계는 1×1.6+5 = 6.6초.
        var spans = new[] { 1_000.0, 1_000.0, 1_000.0, 1_000.0, 1_000.0, 14_000.0 };
        var (median, cleanMax) = ApiSpanMath.MedianAndCleanMax(spans);
        int max = AutoCalibrationService.ComputeMaxThresholdMs(median!.Value, cleanMax!.Value, 0.60, 5_000);
        Assert.Equal(6_600, max);
    }
}
