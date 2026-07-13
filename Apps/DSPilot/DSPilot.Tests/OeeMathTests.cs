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

    // ── MTBF / 고장없음 배지 (가짜 max(n,1) 금지) ────────────────────────────

    [Fact]
    public void Mtbf_zero_failures_is_null_and_nofault()
    {
        var (mtbf, note, noFault) = OeeMath.ComputeMtbf(3_600_000, 0);
        Assert.Null(mtbf);          // 가짜 수치 금지
        Assert.True(noFault);       // UI 고장없음 배지
        Assert.Contains("고장없음", note);
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

    // ── 비생산 자동판정 (10×CT 장시간 무변화 정지) doc/22 §3.3 ─────────────

    [Fact]
    public void LongStop_multiplier_is_ten()
        => Assert.Equal(10.0, OeeMath.NonProductionCtMultiplier);

    [Theory]
    [InlineData(1000, 10000, true)]   // 정확히 10× → 비생산
    [InlineData(1000, 9999, false)]   // 10× 직전 → 다운타임 유지
    [InlineData(1000, 50000, true)]   // 50× → 비생산
    [InlineData(2000, 19999, false)]  // 9.99× → 다운타임
    [InlineData(2000, 20000, true)]   // 10× → 비생산
    public void IsLongStopNonProduction_threshold(double thrMs, double durMs, bool expected)
        => Assert.Equal(expected, OeeMath.IsLongStopNonProduction(durMs, thrMs));

    [Theory]
    [InlineData(0)]      // 표본 부족(임계 0) → 판정 불가
    [InlineData(-5)]     // 음수 방어
    public void IsLongStopNonProduction_no_threshold_is_false(double thrMs)
        => Assert.False(OeeMath.IsLongStopNonProduction(1_000_000, thrMs));

    // ── gap 기반 비가동 분류 (doc/23 §5) ────────────────────────────────────

    [Fact]
    public void DowntimeGap_multiplier_is_three()
        => Assert.Equal(3.0, OeeMath.DowntimeGapMultiplier);

    [Theory]
    // gap' = 2000ms, CT임계 = 10000ms → 비가동 경계 6000(초과), 비생산 경계 100000(이상)
    [InlineData(2000, OeeMath.GapClass.Normal)]         // 정상 대기 (= gap')
    [InlineData(6000, OeeMath.GapClass.Normal)]         // 정확히 3×gap' → 아직 정상(초과 조건)
    [InlineData(6001, OeeMath.GapClass.Downtime)]       // 3×gap' 초과 → 비가동
    [InlineData(99999, OeeMath.GapClass.Downtime)]      // 10×CT 직전 → 비가동 유지
    [InlineData(100000, OeeMath.GapClass.NonProduction)] // 정확히 10×CT → 비생산
    [InlineData(500000, OeeMath.GapClass.NonProduction)] // 장시간 → 비생산
    public void ClassifyGap_boundaries(double gapMs, OeeMath.GapClass expected)
        => Assert.Equal(expected, OeeMath.ClassifyGap(gapMs, gapMedianMs: 2000, ctThresholdMs: 10000));

    [Fact]
    public void ClassifyGap_no_gap_median_only_nonproduction_applies()
    {
        // gap' 표본 부족(0) → 비가동 판정 불가(가짜 정지 금지) — 비생산 경계만 적용.
        Assert.Equal(OeeMath.GapClass.Normal, OeeMath.ClassifyGap(50_000, 0, 10_000));
        Assert.Equal(OeeMath.GapClass.NonProduction, OeeMath.ClassifyGap(100_000, 0, 10_000));
    }

    [Fact]
    public void ClassifyGap_no_thresholds_at_all_is_normal()
        => Assert.Equal(OeeMath.GapClass.Normal, OeeMath.ClassifyGap(1_000_000, 0, 0));

    // ── 무사이클 임계 폴백 체인 (doc/23 §6 Phase 1) ─────────────────────────

    [Fact]
    public void NoCycleThreshold_chain_gap_median_first()
        // ① gap' 학습됨 → 3×gap' (floor 이상이면 그대로)
        => Assert.Equal(60_000, OeeMath.ResolveNoCycleThresholdMs(
            gapMedianMs: 20_000, ctAvgMs: 5_000, floorMs: 30_000, bootstrapMs: 120_000));

    [Fact]
    public void NoCycleThreshold_chain_floor_clamps_fast_flows()
        // ① 초고속 flow(gap' 500ms) → 3×gap'=1.5s 지만 floor 30s 로 클램프(잡음성 미세정지 방지)
        => Assert.Equal(30_000, OeeMath.ResolveNoCycleThresholdMs(
            gapMedianMs: 500, ctAvgMs: 5_000, floorMs: 30_000, bootstrapMs: 120_000));

    [Fact]
    public void NoCycleThreshold_chain_ct_avg_fallback()
        // ② gap' 없음 → 3×14일평균CT (여전히 per-flow)
        => Assert.Equal(150_000, OeeMath.ResolveNoCycleThresholdMs(
            gapMedianMs: 0, ctAvgMs: 50_000, floorMs: 30_000, bootstrapMs: 120_000));

    [Fact]
    public void NoCycleThreshold_chain_bootstrap_when_unlearned()
        // ③ 학습 전무(콜드스타트) → 부트스트랩(기존 NoCycleSeconds)
        => Assert.Equal(120_000, OeeMath.ResolveNoCycleThresholdMs(
            gapMedianMs: 0, ctAvgMs: 0, floorMs: 30_000, bootstrapMs: 120_000));

    [Fact]
    public void NoCycleThreshold_slow_flow_no_false_onset()
    {
        // 회귀 핵심: 주기 200s(>120s) 느린 flow — 구 고정 120s 면 정상 gap(180s)에서 거짓 onset.
        // gap'=180s 학습 시 임계 540s → 정상 gap 은 안 걸리고, 진짜 정지(600s)만 걸린다.
        var thr = OeeMath.ResolveNoCycleThresholdMs(180_000, 200_000, 30_000, 120_000);
        Assert.Equal(540_000, thr);
        Assert.True(180_000 < thr);   // 정상 gap → onset 아님
        Assert.True(600_000 >= thr);  // 진짜 정지 → onset
    }

    // ── MTBF 고장 판정 = 설비고장(equipment_fault)만 ───────────────────────

    [Theory]
    [InlineData("equipment_fault", true)]   // 설비고장 = 고장
    [InlineData("EQUIPMENT_FAULT", true)]   // 대소문자 무시
    [InlineData("material_wait", false)]    // 자재대기 = 계획외지만 고장 아님
    [InlineData("operator_wait", false)]    // 작업자대기 = 고장 아님
    [InlineData("tooling", false)]          // 금형·공구 = 고장 아님(설비고장만 정책)
    [InlineData("planned_maint", false)]    // 계획정비 = 고장 아님
    [InlineData("etc", false)]
    [InlineData(null, false)]               // 미분류 = 고장 아님
    public void IsFailureReason_only_equipment_fault(string? reasonCode, bool expected)
    {
        Assert.Equal(expected, OeeMath.IsFailureReason(reasonCode));
    }

    [Fact]
    public void Classify_over_8h_is_fault() // 8h↑ 도 고장(비생산 시간대 에디터가 planned 분리 — 단순 2-상태)
    {
        var (rc, cat, isFail, should) = OeeMath.ClassifyByDuration(9L * 60 * 60 * 1000);
        Assert.True(should);
        Assert.Equal("equipment_fault", rc);
        Assert.Equal("unplanned", cat);
        Assert.True(isFail);
    }

    // ── 사용자 직접 설정 전반 품질 (manual override) ───────────────────────

    [Fact]
    public void ResolveQuality_manual_override_wins_over_reject_data()
    {
        // 불량이 입력돼 있어도(measured) 사용자가 99% 로 직접 설정하면 그 값을 쓴다.
        var (q, _, source, reject, good) = OeeMath.ResolveQuality(99.0, totalCount: 1000, prodReject: 50, hasReject: true);
        Assert.Equal(0.99, q!.Value, 10);
        Assert.Equal("manual", source);
        Assert.Equal(990, good);   // 1000 × 0.99
        Assert.Equal(10, reject);  // 1000 − 990
    }

    [Fact]
    public void ResolveQuality_null_manual_falls_back_to_compute()
    {
        // 미설정(null)이면 불량 입력 기반(measured), 불량 데이터 없으면 가정(assumed).
        var (q1, _, s1, _, _) = OeeMath.ResolveQuality(null, 700, 7, hasReject: true);
        Assert.Equal(693.0 / 700.0, q1!.Value, 10);
        Assert.Equal("measured", s1);

        var (q2, _, s2, _, _) = OeeMath.ResolveQuality(null, 700, 0, hasReject: false);
        Assert.Equal(1.0, q2);
        Assert.Equal("assumed", s2);
    }

    [Fact]
    public void ResolveQuality_manual_clamps_and_handles_zero_cycles()
    {
        var (qHigh, _, _, _, _) = OeeMath.ResolveQuality(150.0, 100, 0, hasReject: false);
        Assert.Equal(1.0, qHigh); // 150% → clamp 100%
        var (qZero, _, src, reject, good) = OeeMath.ResolveQuality(95.0, totalCount: 0, prodReject: 0, hasReject: false);
        Assert.Equal(0.95, qZero!.Value, 10); // 사이클 0 이어도 사용자 설정값은 유효(환산 good/reject 만 null)
        Assert.Equal("manual", src);
        Assert.Null(reject);
        Assert.Null(good);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  사이클기반 OEE (doc/22 — P5 v4 CT/MT/WT 모델)
    // ════════════════════════════════════════════════════════════════════════

    // ── 비가동 판정 (doc/22 §3 ①②) ────────────────────────────────────────

    [Theory]
    [InlineData(20000, 30000, 30000, OeeMath.CycleClass.Normal)]   // MT 20s ≤ thr 30s → 정상
    [InlineData(45000, 50000, 30000, OeeMath.CycleClass.Downtime)] // ① MT 45s > thr → 비가동
    [InlineData(null, 50000, 30000, OeeMath.CycleClass.Downtime)]  // ② complete=null, CT 50s > thr → 비가동
    [InlineData(null, 20000, 30000, OeeMath.CycleClass.Normal)]    // complete=null 이지만 CT 20s ≤ thr → 정상
    public void ClassifyCycle_marks_downtime_by_mt_or_ct_overrun(int? mt, int? ct, double thr, OeeMath.CycleClass expected)
    {
        Assert.Equal(expected, OeeMath.ClassifyCycle(mt, ct, thr));
    }

    [Fact]
    public void ClassifyCycle_open_cycle_without_ct_is_ignored()
    {
        Assert.Equal(OeeMath.CycleClass.Ignore, OeeMath.ClassifyCycle(mt: 5000, ct: null, ctThresholdMs: 30000));
        Assert.Equal(OeeMath.CycleClass.Ignore, OeeMath.ClassifyCycle(mt: 5000, ct: 0, ctThresholdMs: 30000));
    }

    [Fact]
    public void ClassifyCycle_no_threshold_treats_as_normal()
    {
        // 표본 부족(thr=0) → 판정 불가 → Normal(상위에서 산출 게이트). 가짜 비가동 분류 금지.
        Assert.Equal(OeeMath.CycleClass.Normal, OeeMath.ClassifyCycle(mt: 999999, ct: 999999, ctThresholdMs: 0));
    }

    // ── 비가동 판정 배수 (2026-07-13 사용자 설정화) — 경계 = thr × idleMultiplier ──

    [Theory]
    [InlineData(45000, 50000, OeeMath.CycleClass.Normal)]    // MT 45s > 1×thr 지만 ≤ 2.5×thr(75s) → 정상(속도 손실 → P)
    [InlineData(75000, 80000, OeeMath.CycleClass.Normal)]    // 정확히 2.5×thr → 아직 정상(초과 조건)
    [InlineData(75001, 80000, OeeMath.CycleClass.Downtime)]  // 2.5×thr 초과 → 비가동
    [InlineData(null, 75001, OeeMath.CycleClass.Downtime)]   // ② 미완료 CT 도 동일 경계
    public void ClassifyCycle_idle_multiplier_moves_boundary(int? mt, int? ct, OeeMath.CycleClass expected)
        => Assert.Equal(expected, OeeMath.ClassifyCycle(mt, ct, ctThresholdMs: 30000, idleMultiplier: 2.5));

    [Fact]
    public void ClassifyCycle_idle_multiplier_below_one_clamps_to_one()
        // 배수 < 1 은 1로 클램프(정상 사이클을 비가동으로 삼키는 역방향 금지).
        => Assert.Equal(OeeMath.CycleClass.Normal, OeeMath.ClassifyCycle(mt: 29000, ct: 30000, ctThresholdMs: 30000, idleMultiplier: 0.5));

    // ── 비생산 승격 배수 (2026-07-13 사용자 설정화) — IsLongStopNonProduction/ClassifyGap 파라미터 ──

    [Theory]
    [InlineData(149_999, 15.0, false)]  // 15×thr 미만 → 다운타임 유지
    [InlineData(150_000, 15.0, true)]   // 정확히 15×thr → 비생산
    [InlineData(100_000, 15.0, false)]  // 기본 10× 였다면 비생산이었을 길이 — 배수 상향으로 다운타임 유지
    public void IsLongStopNonProduction_honors_custom_multiplier(double durMs, double mult, bool expected)
        => Assert.Equal(expected, OeeMath.IsLongStopNonProduction(durMs, ctThresholdMs: 10_000, multiplier: mult));

    [Fact]
    public void ClassifyGap_honors_custom_nonprod_multiplier()
    {
        // gap'=2000, thr=10000, 비생산 배수 5× → 경계 50s (기본 10×의 절반)
        Assert.Equal(OeeMath.GapClass.Downtime, OeeMath.ClassifyGap(49_999, 2000, 10_000, nonProdMultiplier: 5));
        Assert.Equal(OeeMath.GapClass.NonProduction, OeeMath.ClassifyGap(50_000, 2000, 10_000, nonProdMultiplier: 5));
    }

    [Fact]
    public void ResolveCtMultipliers_defaults_clamps_and_inversion_defense()
    {
        var s = new DSPilot.Models.OeeManualSettings();
        Assert.Equal((OeeMath.IdleCtMultiplierDefault, OeeMath.NonProductionCtMultiplier), s.ResolveCtMultipliers());

        // 역전(손편집/구버전 JSON) — 비가동 ≥ 비생산이면 비가동을 비생산/2 로 방어(승격 경로 사망 방지)
        s.IdleCtMultiplier = 12; s.NonProdCtMultiplier = 10;
        Assert.Equal((5.0, 10.0), s.ResolveCtMultipliers());

        // NaN → 기본값 폴백, 범위 밖 → 클램프
        s.IdleCtMultiplier = double.NaN; s.NonProdCtMultiplier = 1000;
        Assert.Equal((OeeMath.IdleCtMultiplierDefault, DSPilot.Models.OeeManualSettings.NonProdMultMax), s.ResolveCtMultipliers());
    }

    // ── P5 §⑥ 검산 (STN3): CT이상치=30s, N=90, Σ실측CT=2970s, Σ비가동CT=1200s ──

    [Fact]
    public void P5_worked_example_availability_71_2_percent()
    {
        var (a, _) = OeeMath.ComputeCycleAvailability(normalCtMs: 2_970_000, idleCtMs: 1_200_000);
        Assert.NotNull(a);
        Assert.Equal(2970.0 / 4170.0, a!.Value, 10); // = 0.7122…
        Assert.Equal(0.712, Math.Round(a.Value, 3));
    }

    [Fact]
    public void P5_worked_example_performance_90_9_percent()
    {
        var (p, _) = OeeMath.ComputeCyclePerformance(normalCycleCount: 90, ctThresholdMs: 30_000, normalCtMs: 2_970_000);
        Assert.NotNull(p);
        Assert.Equal(2700.0 / 2970.0, p!.Value, 10); // = 0.9090…
        Assert.Equal(0.909, Math.Round(p.Value, 3));
    }

    [Fact]
    public void P5_worked_example_oee_64_7_percent()
    {
        var (a, _) = OeeMath.ComputeCycleAvailability(2_970_000, 1_200_000);
        var (p, _) = OeeMath.ComputeCyclePerformance(90, 30_000, 2_970_000);
        var (q, _) = (1.0, ""); // 품질 100% 가정
        var (oee, _) = OeeMath.ComputeOee(a, p, q, "assumed");
        Assert.NotNull(oee);
        Assert.Equal(0.647, Math.Round(oee!.Value, 3)); // 0.712 × 0.909 × 1.0
    }

    // ── 사이클 가용성/성능 산출 불가 정직 표기 ──────────────────────────────

    [Fact]
    public void ComputeCycleAvailability_zero_cycles_is_null()
    {
        var (a, note) = OeeMath.ComputeCycleAvailability(0, 0);
        Assert.Null(a);
        Assert.Contains("산출 불가", note);
    }

    [Fact]
    public void ComputeCyclePerformance_capped_at_one()
    {
        // N×thr > Σ실측CT (당기가 14일 평균보다 빠름) → 1.0 캡.
        var (p, _) = OeeMath.ComputeCyclePerformance(100, 30_000, 2_500_000);
        Assert.Equal(1.0, p!.Value);
    }

    [Theory]
    [InlineData(0, 30000.0, 2970000.0)]   // 정상 사이클 0
    [InlineData(90, null, 2970000.0)]     // CT이상치 없음(표본 부족)
    [InlineData(90, 30000.0, 0.0)]        // Σ실측CT 0
    public void ComputeCyclePerformance_null_when_inputs_insufficient(int n, double? thr, double normalCt)
    {
        var (p, note) = OeeMath.ComputeCyclePerformance(n, thr, normalCt);
        Assert.Null(p);
        Assert.Contains("산출 불가", note);
    }

    // ── 생산효율 TEEP / 가동률 (P6) ────────────────────────────────────────

    [Fact]
    public void ComputeTeep_running_over_full_calendar()
    {
        // 하루(24h) 중 가동 12h → TEEP 50% (표준: 비생산도 분모 포함).
        var teep = OeeMath.ComputeTeep(runningMs: 12 * 3600_000.0, calendarMs: 24 * 3600_000.0);
        Assert.NotNull(teep);
        Assert.Equal(0.5, teep!.Value, 10);
    }

    [Fact]
    public void ComputeTeep_null_when_calendar_not_positive()
    {
        Assert.Null(OeeMath.ComputeTeep(1000, 0));
        Assert.Null(OeeMath.ComputeTeep(1000, -5));
    }

    [Fact]
    public void ComputeTeep_clamped_to_one()
    {
        // 가동 > 캘린더(방어)여도 1.0 캡.
        Assert.Equal(1.0, OeeMath.ComputeTeep(30 * 3600_000.0, 24 * 3600_000.0)!.Value);
    }

    [Fact]
    public void ComputeUtilization_excludes_nonprod_from_denominator()
    {
        // 캘린더 24h, 비생산 9h → 가동률 = (24−9)/24 = 62.5% (TEEP 와 달리 비생산을 분모서 뺀 관점).
        var util = OeeMath.ComputeUtilization(calendarMs: 24 * 3600_000.0, nonProdMs: 9 * 3600_000.0);
        Assert.NotNull(util);
        Assert.Equal(15.0 / 24.0, util!.Value, 10);
    }

    [Fact]
    public void ComputeUtilization_null_when_calendar_not_positive()
    {
        Assert.Null(OeeMath.ComputeUtilization(0, 0));
    }

    // ── MTBF (연속 onset 간격 평균) / MTTR ─────────────────────────────────

    [Fact]
    public void ComputeMtbf2_zero_failures_is_nofault()
    {
        var (mtbf, note, noFault) = OeeMath.ComputeMtbf2(new List<double>());
        Assert.Null(mtbf);
        Assert.True(noFault);
        Assert.Contains("고장없음", note);
    }

    [Fact]
    public void ComputeMtbf2_single_onset_has_no_gap()
    {
        var (mtbf, _, noFault) = OeeMath.ComputeMtbf2(new List<double> { 1000 });
        Assert.Null(mtbf);
        Assert.False(noFault); // 고장은 있으나 간격 없음 (고장없음 아님)
    }

    [Fact]
    public void ComputeMtbf2_averages_consecutive_onset_gaps()
    {
        // onset @ 0, 10min, 30min → 갭 10min, 20min → 평균 15min.
        var onsets = new List<double> { 0, 10 * 60_000, 30 * 60_000 };
        var (mtbf, _, _) = OeeMath.ComputeMtbf2(onsets);
        Assert.Equal(15 * 60_000.0, mtbf!.Value, 6);
    }

    [Fact]
    public void ComputeMttr_averages_repair_durations()
    {
        var (mttr, _) = OeeMath.ComputeMttr(new List<double> { 3 * 60_000, 5 * 60_000, 4 * 60_000 });
        Assert.Equal(4 * 60_000.0, mttr!.Value, 6); // 평균 4분
    }

    [Fact]
    public void ComputeMttr_empty_is_null()
    {
        var (mttr, note) = OeeMath.ComputeMttr(new List<double>());
        Assert.Null(mttr);
        Assert.Contains("산출 불가", note);
    }

    // ── BuildTeepMatrixCells (P6 L0 매트릭스 — /uptime-teep 3D/2D 차트 셀) ──────
    //  귀속 규칙 고정: 가동·사이클수=시작버킷 통째 귀속 / 정지·비생산=overlap 분배(다일 정지 몰림 방지).

    private static readonly List<(double S, double E)> TwoHourBuckets =
        new() { (0, 3_600_000), (3_600_000, 7_200_000) }; // [0,1h), [1h,2h)

    [Fact]
    public void TeepMatrix_assigns_cycle_to_start_bucket_and_computes_teep()
    {
        // 버킷1에 30초 사이클 60개(가동 30분) — TEEP = 30m/60m = 0.5. 버킷2는 무활동(산출 불가 null).
        var cycles = Enumerable.Range(0, 60).Select(i => ((double)i * 60_000, 30_000.0)).ToList();
        var cells = OeeMath.BuildTeepMatrixCells(TwoHourBuckets, cycles,
            idleIntervals: new List<(double, double)>(), nonProdIntervals: new List<(double, double)>(),
            ctThresholdMs: 30_000, quality: 1.0);

        Assert.Equal(2, cells.Count);
        Assert.Equal(60, cells[0].CycleCount);
        Assert.Equal(0.5, cells[0].Teep!.Value, 10);
        Assert.Equal(1.0, cells[0].Availability!.Value, 10);   // 정지 0
        Assert.Equal(1.0, cells[0].Performance!.Value, 10);    // 60×30s ÷ 30m
        Assert.Equal(1.0, cells[0].Oee!.Value, 10);
        Assert.Equal(0, cells[1].CycleCount);
        Assert.Equal(0.0, cells[1].Teep!.Value, 10);            // 가동 0 → TEEP 0 (캘린더는 있으므로 null 아님)
        Assert.Null(cells[1].Availability);                     // 가동+정지 0 → 산출 불가(null 정직 표기)
        Assert.Null(cells[1].Oee);
    }

    [Fact]
    public void TeepMatrix_boundary_cycle_belongs_wholly_to_start_bucket()
    {
        // 버킷 경계에 걸친 사이클(시작 59.5분, CT 1분)은 시작 버킷1에 통째 귀속 — 버킷2 가동 0.
        var cycles = new List<(double, double)> { (59.5 * 60_000, 60_000.0) };
        var cells = OeeMath.BuildTeepMatrixCells(TwoHourBuckets, cycles,
            new List<(double, double)>(), new List<(double, double)>(), 60_000, 1.0);

        Assert.Equal(60_000.0, cells[0].RunningMs, 6);
        Assert.Equal(0.0, cells[1].RunningMs, 6);
    }

    [Fact]
    public void TeepMatrix_distributes_idle_and_nonprod_by_overlap()
    {
        // 정지 30분(0.5h~1.5h)이 두 버킷에 걸침 → 각 15분씩 분배(시작일 몰빵 금지 — 주말 다일정지 함정).
        var idle = new List<(double, double)> { (1_800_000, 5_400_000) };
        // 비생산 1시간(1h~2h) → 버킷2에만.
        var nonProd = new List<(double, double)> { (3_600_000, 7_200_000) };
        var cells = OeeMath.BuildTeepMatrixCells(TwoHourBuckets, new List<(double, double)>(),
            idle, nonProd, 30_000, 1.0);

        Assert.Equal(1_800_000.0, cells[0].DownMs, 6);
        Assert.Equal(1_800_000.0, cells[1].DownMs, 6);
        Assert.Equal(0.0, cells[0].NonProdMs, 6);
        Assert.Equal(3_600_000.0, cells[1].NonProdMs, 6);
        // 가동 0 + 정지 >0 → A=0, TEEP=0, 성능 null → OEE null(정직 표기).
        Assert.Equal(0.0, cells[0].Availability!.Value, 10);
        Assert.Null(cells[0].Performance);
        Assert.Null(cells[0].Oee);
    }

    [Fact]
    public void TeepMatrix_applies_manual_quality_to_oee()
    {
        // A=0.5(가동 30분·정지 30분), P=1.0, Q=0.9 → OEE = 0.45.
        var cycles = Enumerable.Range(0, 60).Select(i => ((double)i * 30_000, 30_000.0)).ToList();
        var idle = new List<(double, double)> { (1_800_000, 3_600_000) };
        var cells = OeeMath.BuildTeepMatrixCells(TwoHourBuckets, cycles, idle,
            new List<(double, double)>(), 30_000, quality: 0.9);

        Assert.Equal(0.5, cells[0].Availability!.Value, 10);
        Assert.Equal(0.45, cells[0].Oee!.Value, 10);
    }

    [Fact]
    public void TeepMatrix_unsorted_cycles_are_bucketed_correctly()
    {
        // 두 포인터 귀속은 내부 정렬에 의존 — 역순 입력도 동일 결과.
        var cycles = new List<(double, double)> { (4_000_000, 30_000.0), (100_000, 30_000.0) };
        var cells = OeeMath.BuildTeepMatrixCells(TwoHourBuckets, cycles,
            new List<(double, double)>(), new List<(double, double)>(), 30_000, 1.0);

        Assert.Equal(1, cells[0].CycleCount);
        Assert.Equal(1, cells[1].CycleCount);
    }

    // ── FoldIntervalsToMinuteOfDay — planned-stops/actual 하루/날짜별 접기 (TEEP 날짜별 비생산 패턴) ──
    // epoch 0 = 그 날 00:00 로 두고 minute-of-day 변환기를 주입해 서버 타임존과 무관하게 검증한다.

    private const double Min = 60_000.0;
    private static int FakeMinuteOfDay(double ms) => (int)(ms / Min) % 1440;

    [Fact]
    public void Fold_merges_and_clips_intervals_to_windows()
    {
        // 12:00~13:00 + 12:30~14:00 (겹침) → 병합 720~840, 클립 밖(음수) 구간은 제거.
        var ivs = new List<(double, double)> { (720 * Min, 780 * Min), (750 * Min, 840 * Min), (-500 * Min, -100 * Min) };
        var w = OeeMath.FoldIntervalsToMinuteOfDay(ivs, 0, 1440 * Min, FakeMinuteOfDay);

        var win = Assert.Single(w);
        Assert.Equal(720, win.StartMinutes);
        Assert.Equal(840, win.EndMinutes);
    }

    [Fact]
    public void Fold_full_day_interval_fills_1440()
    {
        // 클립 폭 만큼(하루 전체) 덮는 구간 → 0~1440 전체 채움.
        var w = OeeMath.FoldIntervalsToMinuteOfDay(
            new List<(double, double)> { (-2880 * Min, 4320 * Min) }, 0, 1440 * Min, FakeMinuteOfDay);

        var win = Assert.Single(w);
        Assert.Equal(0, win.StartMinutes);
        Assert.Equal(1440, win.EndMinutes);
    }

    [Fact]
    public void Fold_per_day_clip_avoids_multiday_union_degeneration()
    {
        // 주말 정지(1일차 18:00 ~ 3일차 06:00, 36h)를 날짜별로 클립해 접으면:
        //   1일차 = 18:00~24:00 부분 채움, 2일차 = 전체 채움, 3일차 = 00:00~06:00 부분 채움.
        // (union 으로 한 번에 접으면 1440분 전체 채움으로 퇴화 — 날짜별 접기가 이 퇴화를 없앤다.)
        var stop = new List<(double, double)> { (1080 * Min, (2880 + 360) * Min) };

        var day1 = OeeMath.FoldIntervalsToMinuteOfDay(stop, 0, 1440 * Min, FakeMinuteOfDay);
        var day2 = OeeMath.FoldIntervalsToMinuteOfDay(stop, 1440 * Min, 2880 * Min, FakeMinuteOfDay);
        var day3 = OeeMath.FoldIntervalsToMinuteOfDay(stop, 2880 * Min, 4320 * Min, FakeMinuteOfDay);

        Assert.Equal((1080, 1440), (Assert.Single(day1).StartMinutes, Assert.Single(day1).EndMinutes));
        Assert.Equal((0, 1440), (Assert.Single(day2).StartMinutes, Assert.Single(day2).EndMinutes));
        Assert.Equal((0, 360), (Assert.Single(day3).StartMinutes, Assert.Single(day3).EndMinutes));
    }

    [Fact]
    public void Fold_empty_or_out_of_clip_returns_no_windows()
    {
        Assert.Empty(OeeMath.FoldIntervalsToMinuteOfDay(
            new List<(double, double)>(), 0, 1440 * Min, FakeMinuteOfDay));
        // 클립 범위(2일차) 밖 구간만 존재 → 빈 결과.
        Assert.Empty(OeeMath.FoldIntervalsToMinuteOfDay(
            new List<(double, double)> { (100 * Min, 200 * Min) }, 1440 * Min, 2880 * Min, FakeMinuteOfDay));
    }

    [Fact]
    public void Fold_disjoint_intervals_produce_separate_windows()
    {
        // 점심 12:00~13:00 + 야간 22:00~24:00 → 두 개의 분리 창(병합 금지).
        var ivs = new List<(double, double)> { (720 * Min, 780 * Min), (1320 * Min, 1440 * Min) };
        var w = OeeMath.FoldIntervalsToMinuteOfDay(ivs, 0, 1440 * Min, FakeMinuteOfDay);

        Assert.Equal(2, w.Count);
        Assert.Equal((720, 780), (w[0].StartMinutes, w[0].EndMinutes));
        Assert.Equal((1320, 1440), (w[1].StartMinutes, w[1].EndMinutes));
    }
}
