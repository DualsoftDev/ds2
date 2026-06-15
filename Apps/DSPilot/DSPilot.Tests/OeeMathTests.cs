// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// <see cref="OeeMath.ComputeQuality"/> 단위 테스트 — doc/21 §12 품질 정책을 코드로 고정한다:
/// 분모 = 기간 사이클수(자동), 불량 미입력 = 100% 가정(Source="assumed"), 입력 시 실측(Source="measured"),
/// 사이클 0 = null(무의미한 100% 금지), 과입력/음수는 clamp.
/// </summary>
public class OeeMathTests
{
    // ── 가정(assumed): 불량 데이터 전무 ────────────────────────────────────

    [Fact]
    public void No_reject_data_assumes_100_percent_with_assumed_source()
    {
        var (quality, note, source, reject, good) = OeeMath.ComputeQuality(700, 0, hasReject: false);

        Assert.Equal(1.0, quality);
        Assert.Equal("assumed", source);
        Assert.Equal(0, reject);
        Assert.Equal(700, good);
        Assert.Contains("가정", note);
    }

    [Fact]
    public void No_reject_data_ignores_stale_reject_argument()
    {
        // hasReject=false 면 prodReject 값이 무엇이든(방어) 불량 0 으로 본다.
        var (quality, _, source, reject, _) = OeeMath.ComputeQuality(100, 5, hasReject: false);

        Assert.Equal(1.0, quality);
        Assert.Equal("assumed", source);
        Assert.Equal(0, reject);
    }

    // ── 실측(measured): 불량 입력 존재 ─────────────────────────────────────

    [Fact]
    public void Reject_entered_computes_measured_ratio_over_cycle_count()
    {
        // §12 핵심 시나리오: 주간 700 사이클, 1일만 불량 5 입력 → 99.3% (구식 일부일 분모 방식의 95% 급락 방지).
        var (quality, _, source, reject, good) = OeeMath.ComputeQuality(700, 5, hasReject: true);

        Assert.NotNull(quality);
        Assert.Equal(695.0 / 700.0, quality!.Value, 10);
        Assert.Equal("measured", source);
        Assert.Equal(5, reject);
        Assert.Equal(695, good);
    }

    [Fact]
    public void Reject_zero_entered_is_measured_100_percent()
    {
        // 불량 0 을 "입력"한 것(행 존재)은 가정이 아니라 실측 100%.
        var (quality, _, source, _, _) = OeeMath.ComputeQuality(50, 0, hasReject: true);

        Assert.Equal(1.0, quality);
        Assert.Equal("measured", source);
    }

    [Fact]
    public void Reject_exceeding_total_clamps_to_zero_quality()
    {
        // PLC 불량카운터가 사이클수보다 큰 경우(다개취출 등) — 음수 양품 금지, 0% 로 클램프.
        var (quality, _, source, reject, good) = OeeMath.ComputeQuality(10, 25, hasReject: true);

        Assert.Equal(0.0, quality);
        Assert.Equal("measured", source);
        Assert.Equal(25, reject);
        Assert.Equal(0, good);
    }

    [Fact]
    public void Negative_reject_input_treated_as_zero()
    {
        var (quality, _, _, reject, good) = OeeMath.ComputeQuality(10, -3, hasReject: true);

        Assert.Equal(1.0, quality);
        Assert.Equal(0, reject);
        Assert.Equal(10, good);
    }

    // ── 산출 불가: 기간 사이클 0 ───────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void No_cycles_returns_null_not_fake_100(int? totalCount)
    {
        var (quality, note, source, reject, good) = OeeMath.ComputeQuality(totalCount, 0, hasReject: false);

        Assert.Null(quality);
        Assert.Null(source);
        Assert.Null(reject);
        Assert.Null(good);
        Assert.Contains("산출 불가", note);
    }

    // ── OEE = A × P × Q 합성 ───────────────────────────────────────────────

    [Fact]
    public void Oee_is_product_of_a_p_q()
    {
        var (oee, note) = OeeMath.ComputeOee(0.9, 0.8, 0.95, "measured");
        Assert.NotNull(oee);
        Assert.Equal(0.9 * 0.8 * 0.95, oee!.Value, 10);
        Assert.Null(note); // measured → 가정 주석 없음
    }

    [Fact]
    public void Oee_with_assumed_quality_notes_assumption()
    {
        var (oee, note) = OeeMath.ComputeOee(0.9, 0.8, 1.0, "assumed");
        Assert.Equal(0.9 * 0.8 * 1.0, oee!.Value, 10);
        Assert.Contains("가정", note);
    }

    [Theory]
    [InlineData(null, 0.8, 0.9, "가용성")]
    [InlineData(0.9, null, 0.9, "성능")]
    [InlineData(0.9, 0.8, null, "품질")]
    public void Oee_null_when_any_component_missing(double? a, double? p, double? q, string missingLabel)
    {
        var (oee, note) = OeeMath.ComputeOee(a, p, q, "measured");
        Assert.Null(oee);
        Assert.Contains("산출 불가", note);
        Assert.Contains(missingLabel, note);
    }

    // ── MTBF / 무고장 배지 (가짜 max(n,1) 금지) ────────────────────────────

    [Fact]
    public void Mtbf_zero_failures_is_null_and_nofault()
    {
        var (mtbf, note, noFault) = OeeMath.ComputeMtbf(3_600_000, 0);
        Assert.Null(mtbf);          // 가짜 수치 금지
        Assert.True(noFault);       // UI 무고장 배지
        Assert.Contains("무고장", note);
    }

    [Fact]
    public void Mtbf_divides_runtime_by_failures()
    {
        var (mtbf, _, noFault) = OeeMath.ComputeMtbf(6_000_000, 3);
        Assert.Equal(2_000_000.0, mtbf!.Value, 6);
        Assert.False(noFault);
    }

    // ── 표준CT 자동기입 후보 (p10 확정 / 중앙값 임시 / 없음) ─────────────────

    [Fact]
    public void Pick_p10_when_samples_reach_min_clean()
    {
        var (ms, src) = OeeMath.PickAutoIdealCycle(sampleCount: 30, recommendedMs: 500, medianMs: 800, minClean: 30, minMedian: 5);
        Assert.Equal(500, ms);
        Assert.Equal("auto", src);
    }

    [Fact]
    public void Pick_median_temporary_when_below_min_clean()
    {
        var (ms, src) = OeeMath.PickAutoIdealCycle(sampleCount: 18, recommendedMs: 500, medianMs: 800, minClean: 30, minMedian: 5);
        Assert.Equal(800, ms);
        Assert.Equal("auto-median", src);
    }

    [Fact]
    public void Pick_none_when_too_few_samples()
    {
        var (ms, src) = OeeMath.PickAutoIdealCycle(sampleCount: 3, recommendedMs: 500, medianMs: 800, minClean: 30, minMedian: 5);
        Assert.Null(ms);
        Assert.Null(src);
    }

    // ── nocycle clear 분류 휴리스틱 (5분/8h) ───────────────────────────────

    [Fact]
    public void Classify_under_5min_stays_unclassified()
    {
        var (rc, cat, isFail, should) = OeeMath.ClassifyByDuration(4 * 60 * 1000);
        Assert.False(should);
        Assert.Null(rc);
        Assert.Null(cat);
        Assert.False(isFail);
    }

    [Fact]
    public void Classify_5min_to_8h_is_failure()
    {
        var (rc, cat, isFail, should) = OeeMath.ClassifyByDuration(30 * 60 * 1000);
        Assert.True(should);
        Assert.Equal("equipment_fault", rc);
        Assert.Equal("unplanned", cat);
        Assert.True(isFail);
    }

    [Fact]
    public void Classify_over_8h_is_planned_maint_not_failure()
    {
        var (rc, cat, isFail, should) = OeeMath.ClassifyByDuration(9L * 60 * 60 * 1000);
        Assert.True(should);
        Assert.Equal("planned_maint", rc);
        Assert.Equal("planned", cat);
        Assert.False(isFail); // 8h↑ = 계획정비 → MTBF 분모 제외
    }
}
