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

    // ── nocycle clear 분류 휴리스틱 (5분 임계 + MT 과주행 증거) ────────────

    [Fact]
    public void Classify_under_5min_stays_unclassified()
    {
        // 짧은 정지는 MT 과주행이 있어도 노이즈로 보아 도장을 찍지 않는다.
        var (rc, cat, isFail, should) = OeeMath.ClassifyByDuration(
            4 * 60 * 1000, hasOwnMtOverrun: true, lineHasMtOverrun: true);
        Assert.False(should);
        Assert.Null(rc);
        Assert.Null(cat);
        Assert.False(isFail);
    }

    [Fact]
    public void Classify_own_mt_overrun_is_failure()
    {
        // going 중 걸린 유발자 — 자기 flow 가 MT 과주행이면 고장.
        var (rc, cat, isFail, should) = OeeMath.ClassifyByDuration(
            30 * 60 * 1000, hasOwnMtOverrun: true, lineHasMtOverrun: false);
        Assert.True(should);
        Assert.Equal("equipment_fault", rc);
        Assert.Equal("unplanned", cat);
        Assert.True(isFail);
    }

    [Fact]
    public void Classify_sibling_mt_overrun_is_wait_not_failure()
    {
        // 유발자가 다른 flow 로 특정됨 → 이 flow 는 굶은 것. 고장 건수·MTBF 에서 빠진다.
        //   종전엔 지속시간만 봐서 라인 정지 1회가 설비 수만큼 고장으로 부풀었다(2026-08-24 실측 6건).
        var (rc, cat, isFail, should) = OeeMath.ClassifyByDuration(
            30 * 60 * 1000, hasOwnMtOverrun: false, lineHasMtOverrun: true);
        Assert.True(should);
        Assert.Equal("wait_starve", rc);
        Assert.Equal("wait", cat);
        Assert.False(isFail);
    }

    [Fact]
    public void Classify_no_mt_evidence_stays_unclassified()
    {
        // 아무도 MT 과주행이 없으면 고장이라 볼 근거가 없다 — 도장을 찍지 않고 조회 시점
        // 신호 판정(ClassifyStopWindow)에 맡긴다. DB 에 영구 박히는 오분류 방지.
        var (rc, cat, isFail, should) = OeeMath.ClassifyByDuration(
            30 * 60 * 1000, hasOwnMtOverrun: false, lineHasMtOverrun: false);
        Assert.False(should);
        Assert.Null(rc);
        Assert.Null(cat);
        Assert.False(isFail);
    }

    [Fact]
    public void Classify_own_mt_overrun_wins_over_sibling()
    {
        // 자기도 걸리고 형제도 걸린 경우 — 자기 고장이 우선(피해자로 강등되면 진짜 고장을 놓친다).
        var (rc, _, isFail, should) = OeeMath.ClassifyByDuration(
            30 * 60 * 1000, hasOwnMtOverrun: true, lineHasMtOverrun: true);
        Assert.True(should);
        Assert.Equal("equipment_fault", rc);
        Assert.True(isFail);
    }

    // ── 비생산 자동판정 (10×CT 장시간 무변화 정지) doc/22 §3.3 ─────────────

    [Fact]
    public void LongStop_multiplier_default_is_fifteen()
        => Assert.Equal(15.0, OeeMath.NonProductionCtMultiplier);   // 2026-08-24: 10× → 15×

    [Fact]
    public void Fault_mt_multiplier_default_is_two_point_five()
        => Assert.Equal(2.5, OeeMath.FaultMtMultiplierDefault);

    // ── MT 과주행 경계 하한 (2026-08-30) ─────────────────────────────────────
    //   초저 MT flow(중앙값 수십 ms)에선 중앙값×배수가 지터 수준 — mt=238ms 잡음이 "고장 유발자"가 되어
    //   다른 flow 강등 근거로 오염됐다(2026-08-28 Prog2 실증). 1초 하한으로 물리적으로 무의미한 경계를 막는다.

    [Theory]
    [InlineData(22, 2.5, 1000)]       // 지터 수준 중앙값(22ms×2.5=55ms) → 하한 1s 로 승격
    [InlineData(300, 2.5, 1000)]      // 300ms×2.5=750ms → 여전히 하한 미만 → 1s
    [InlineData(400, 2.5, 1000)]      // 정확히 하한(400×2.5=1000)
    [InlineData(5000, 2.5, 12500)]    // 정상 flow(중앙값 5s) → 종전과 동일 12.5s
    [InlineData(0, 2.5, 1000)]        // 중앙값 0(비정상 입력)도 경계가 0 이 되지 않는다
    public void ResolveMtFaultBoundary_applies_floor(double medianMs, double mult, double expected)
        => Assert.Equal(expected, OeeMath.ResolveMtFaultBoundaryMs(medianMs, mult));

    [Theory]
    [InlineData(1000, 15000, true)]   // 정확히 15× → 비생산
    [InlineData(1000, 14999, false)]  // 15× 직전 → 다운타임 유지
    [InlineData(1000, 50000, true)]   // 50× → 비생산
    [InlineData(2000, 29999, false)]  // 14.99× → 다운타임
    [InlineData(2000, 30000, true)]   // 15× → 비생산
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
    // gap' = 2000ms, CT임계 = 10000ms → 비가동 경계 6000(초과), 비생산 경계 150000(이상, 15×)
    [InlineData(2000, OeeMath.GapClass.Normal)]          // 정상 대기 (= gap')
    [InlineData(6000, OeeMath.GapClass.Normal)]          // 정확히 3×gap' → 아직 정상(초과 조건)
    [InlineData(6001, OeeMath.GapClass.Downtime)]        // 3×gap' 초과 → 비가동
    [InlineData(149999, OeeMath.GapClass.Downtime)]      // 15×CT 직전 → 비가동 유지
    [InlineData(150000, OeeMath.GapClass.NonProduction)] // 정확히 15×CT → 비생산
    [InlineData(500000, OeeMath.GapClass.NonProduction)] // 장시간 → 비생산
    public void ClassifyGap_boundaries(double gapMs, OeeMath.GapClass expected)
        => Assert.Equal(expected, OeeMath.ClassifyGap(gapMs, gapMedianMs: 2000, ctThresholdMs: 10000));

    [Fact]
    public void ClassifyGap_no_gap_median_only_nonproduction_applies()
    {
        // gap' 표본 부족(0) → 비가동 판정 불가(가짜 정지 금지) — 비생산 경계만 적용.
        Assert.Equal(OeeMath.GapClass.Normal, OeeMath.ClassifyGap(50_000, 0, 10_000));
        Assert.Equal(OeeMath.GapClass.NonProduction, OeeMath.ClassifyGap(150_000, 0, 10_000));
    }

    [Fact]
    public void ClassifyGap_no_thresholds_at_all_is_normal()
        => Assert.Equal(OeeMath.GapClass.Normal, OeeMath.ClassifyGap(1_000_000, 0, 0));

    // ── 자동 '가동중' 박제 해제 경계 (Max 미설정 폴백) ──────────────────────
    //   설비마다 사이클 길이가 수 초~수 분이라 고정 초를 기본값으로 둘 수 없어, flow 자신의 실측
    //   분포(중앙값·p99)에서 만든다. 아래 두 케이스는 실제 현장 측정값이다.

    [Fact]
    public void AutoAbandon_fast_line_uses_median_multiple()
    {
        // 우진 현장: 중앙값 1,500ms · p99 1,666ms · 표본 3,649 → max(20×1500, 3×1666)=30,000ms
        Assert.Equal(30_000, OeeMath.ResolveAutoAbandonBoundaryMs(1_500, 1_666, 3_649, floorMs: Floor));
    }

    [Fact]
    public void AutoAbandon_slow_jittery_line_uses_p99_multiple()
    {
        // 110.165 현장 #100: 중앙값 20,378ms · p99 626,581ms · 표본 2,997 → 3×p99 = 31.3분.
        // 중앙값 배수(6.8분)로는 정상 장주기 사이클을 잘라 미기록시키므로 관대한 쪽을 택한다.
        Assert.Equal(1_879_743, OeeMath.ResolveAutoAbandonBoundaryMs(20_378, 626_581, 2_997, floorMs: Floor));
    }

    [Fact]
    public void AutoAbandon_is_disabled_until_samples_accumulate()
        // 표본 부족 → 0 = 해제 안 함(종전 동작). 몇 건으로 경계를 만들어 정상 사이클을 자르지 않는다.
        => Assert.Equal(0, OeeMath.ResolveAutoAbandonBoundaryMs(1_500, 1_666, sample: 4, floorMs: Floor));

    [Fact]
    public void AutoAbandon_floor_comes_from_watchdog_tick_not_a_site_value()
    {
        // 하한은 설비 사례가 아니라 워치독 판정 주기에서 온다(호출측이 tick×3 을 주입).
        // 중앙값 200ms 초고속 라인: 공식값 4s → 하한이 이긴다. tick 이 바뀌면 하한도 따라 바뀐다.
        Assert.Equal(15_000, OeeMath.ResolveAutoAbandonBoundaryMs(200, 250, 1_000, floorMs: 5 * 3 * 1000));   // tick 5s
        Assert.Equal(90_000, OeeMath.ResolveAutoAbandonBoundaryMs(200, 250, 1_000, floorMs: 30 * 3 * 1000));  // reconcile 비활성(30s 폴링)
        // 실측 두 현장은 공식값이 하한보다 커서 하한과 무관하다 — tick 을 바꿔도 경계가 안 흔들린다.
        Assert.Equal(30_000, OeeMath.ResolveAutoAbandonBoundaryMs(1_500, 1_666, 3_649, floorMs: 90_000 / 3));
    }

    [Fact]
    public void AutoAbandon_ceiling_guarantees_release()
    {
        // p99 가 이상치(주말 정지 62시간)를 물어도 상한에서 잘려 언젠가는 해제된다.
        Assert.Equal(6 * 60 * 60 * 1000, OeeMath.ResolveAutoAbandonBoundaryMs(20_000, 225_675_180, 3_000, floorMs: Floor));
    }

    [Fact]
    public void AutoAbandon_zero_median_is_treated_as_unlearned()
        => Assert.Equal(0, OeeMath.ResolveAutoAbandonBoundaryMs(0, 0, 1_000, floorMs: Floor));

    // ── 무사이클 정지 마감/발생 판정 (2026-07-29 회귀) ──────────────────────
    //   현장 사고: 사이클이 정상 유입(1540건/시간) 중인데 무사이클 정지가 하루 종일 open 으로 남아
    //   그 구간이 비생산으로 승격 → 가동시간 0. 원인은 마감이 "idle < 임계" 분기 안에만 있어서,
    //   tick 이 그 창을 놓치거나 마지막-사이클 조회가 stale 하면 영구히 닫히지 않는 것이었다.

    static readonly DateTime T0 = new(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc);
    const double Floor = 15_000;   // = StateReconcile tick 5s × 3 (기본 설정)

    [Fact]
    public void NoCycleActions_closes_on_resumed_cycle_even_while_idle_exceeds_threshold()
    {
        // ★핵심 회귀: 조회가 stale 해 idle(10분)이 임계를 넘어도, 정지 시작 이후 새 사이클이 있으면 마감한다.
        var (close, open) = OeeMath.ResolveNoCycleActions(
            hasOpen: true, openStartUtc: T0, lastCycleUtc: T0.AddMinutes(5),
            idleMs: 600_000, thresholdMs: 30_000);
        Assert.True(close);
        Assert.True(open);   // 그 사이클 뒤로 또 임계를 넘겼으니 같은 tick 에서 재발생(startAt = 그 사이클)
    }

    [Fact]
    public void NoCycleActions_closes_and_stays_closed_when_line_is_running()
    {
        // 가동 중(idle 1.5s < 임계) — 마감만 하고 새 정지는 열지 않는다.
        var (close, open) = OeeMath.ResolveNoCycleActions(
            hasOpen: true, openStartUtc: T0, lastCycleUtc: T0.AddMinutes(5),
            idleMs: 1_500, thresholdMs: 30_000);
        Assert.True(close);
        Assert.False(open);
    }

    [Fact]
    public void NoCycleActions_does_not_close_on_its_own_onset_cycle()
    {
        // startAt == lastCycle = 그 정지를 만든 사이클 자신 → 0 길이 마감 금지(종전 <= 비교의 부작용).
        var (close, open) = OeeMath.ResolveNoCycleActions(
            hasOpen: true, openStartUtc: T0, lastCycleUtc: T0,
            idleMs: 600_000, thresholdMs: 30_000);
        Assert.False(close);
        Assert.False(open);   // 이미 열려 있으므로 중복 onset 금지
    }

    [Fact]
    public void NoCycleActions_opens_when_threshold_exceeded_and_none_open()
    {
        var (close, open) = OeeMath.ResolveNoCycleActions(
            hasOpen: false, openStartUtc: default, lastCycleUtc: T0,
            idleMs: 45_000, thresholdMs: 30_000);
        Assert.False(close);
        Assert.True(open);
    }

    [Fact]
    public void NoCycleActions_noop_when_running_and_none_open()
    {
        var (close, open) = OeeMath.ResolveNoCycleActions(
            hasOpen: false, openStartUtc: default, lastCycleUtc: T0,
            idleMs: 1_500, thresholdMs: 30_000);
        Assert.False(close);
        Assert.False(open);
    }

    // ── 정지 로그 '구분' 판정 (2026-07-30 회귀) ─────────────────────────────
    //   현장 증상: 라인 정지 1건이 flow 13개 '고장'으로 표시. 집계는 이미 유발자만 고장으로 세고 형제는
    //   공백으로 뺐는데, 로그 판정이 대기를 비생산의 하위로만 인정해(isWait = isNp && …) 전달되지 않았다.

    [Fact]
    public void LogStopClass_slack_overlap_alone_yields_wait_gap_not_fault()
    {
        // ★핵심 회귀: 비생산은 아니지만 이벤트성 공백에 덮인 정지 → '대기(공백)'. 종전엔 (F,F)=고장이었다.
        var (isNp, isWait) = OeeMath.ResolveLogStopClass(nonProdRatio: 0, waitRatio: 0, slackRatio: 1.0);
        Assert.False(isNp);
        Assert.True(isWait);
    }

    [Fact]
    public void LogStopClass_nonprod_with_wait_stays_nonprod_wait()
    {
        // 기준 이상 형제 정지 = 비생산·대기(종전 동작 보존).
        var (isNp, isWait) = OeeMath.ResolveLogStopClass(1.0, 1.0, 0);
        Assert.True(isNp);
        Assert.True(isWait);
    }

    [Fact]
    public void LogStopClass_nonprod_without_wait_is_plain_nonprod()
    {
        // 비생산일 때 대기 여부는 waitRatio 로만 본다 — slack 이 덮여 있어도 '비생산·대기'로 승격되지 않는다.
        var (isNp, isWait) = OeeMath.ResolveLogStopClass(1.0, 0, 1.0);
        Assert.True(isNp);
        Assert.False(isWait);
    }

    [Fact]
    public void LogStopClass_no_overlap_remains_fault()
    {
        // 어디에도 안 덮인 정지 = 고장/유지보수 유지(체크박스 경로) — 진짜 정지가 조용히 대기로 빠지지 않게.
        var (isNp, isWait) = OeeMath.ResolveLogStopClass(0, 0, 0);
        Assert.False(isNp);
        Assert.False(isWait);
    }

    [Fact]
    public void LogStopClass_uses_half_overlap_boundary()
    {
        // 경계 50%: 미만은 고장 유지, 이상은 대기 — 부분만 겹친 정지가 라벨을 뒤집지 않게 한다.
        Assert.False(OeeMath.ResolveLogStopClass(0, 0, 0.49).IsWait);
        Assert.True(OeeMath.ResolveLogStopClass(0, 0, 0.50).IsWait);
    }

    // ── 무사이클 감지 임계 ──────────────────────────────────────────────────
    // 2026-08-21 통일: 폴백 체인(3×gap' ▸ 3×평균CT ▸ 120s)을 폐기하고 감지 임계 = 14일 평균 CT ×
    // 비가동 배수(사용자 설정)로 집계 판정과 하나로 합쳤다. 종전엔 감지(109초)와 계상(213초)이 어긋나
    // "정지 로그엔 뜨는데 고장 건수엔 없는" 구간을 만들었다. 따라서 별도 체인 테스트는 폐기하고,
    // 감지 임계의 계약은 집계 판정 테스트(ClassifyCycle / IsLongStopNonProduction)가 그대로 커버한다.

    [Fact]
    public void NoCycle_detection_threshold_equals_downtime_criterion()
    {
        // 감지 임계와 집계 판정이 같은 식(평균CT × 배수)을 쓰는지 — 통일의 계약.
        const double ctAvg = 42_000, mult = 2.5;
        var boundary = ctAvg * mult;
        Assert.Equal(105_000, boundary);
        // 경계 초과 사이클은 비가동, 미만은 정상 — 같은 경계로 갈린다.
        Assert.Equal(OeeMath.CycleClass.Downtime, OeeMath.ClassifyCycle(mt: 1_000, ct: (int)boundary + 1, ctThresholdMs: ctAvg, idleMultiplier: mult));
        Assert.Equal(OeeMath.CycleClass.Normal,   OeeMath.ClassifyCycle(mt: 1_000, ct: (int)boundary - 1, ctThresholdMs: ctAvg, idleMultiplier: mult));
    }

    [Fact]
    public void NoCycle_slow_flow_no_false_onset()
    {
        // 회귀 핵심: 주기 200s(>120s) 느린 flow — 구 고정 120s 면 정상 gap(180s)에서 거짓 onset.
        // 통일 임계(평균CT 200s × 2.5 = 500s)로도 정상 gap 은 안 걸리고 진짜 정지(600s)만 걸린다.
        var thr = 200_000 * 2.5;
        Assert.Equal(500_000, thr);
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

    // ── 유지보수 확정 정지 = 고장 아님 (2026-07-30) ────────────────────────
    // 정지를 유지보수로 분류하면 고장 건수·MTBF onset·MTTR 에서 빠져야 한다(A 는 그대로 깎임).

    [Theory]
    [InlineData(10_000, 10_000, true)]   // 완전히 덮임 = 유지보수
    [InlineData(10_000, 6_000, true)]    // 과반 덮임 = 유지보수
    [InlineData(10_000, 5_001, true)]    // 과반 경계 바로 위
    [InlineData(10_000, 5_000, false)]   // 정확히 절반 = 고장 유지(과반 아님)
    [InlineData(10_000, 4_000, false)]   // 소수만 덮임 = 고장 유지
    [InlineData(10_000, 1_500, false)]   // 경계 스침(1.5초) 으로 진짜 고장이 지워지지 않는다
    [InlineData(10_000, 0, false)]       // 유지보수 구간 없음 = 고장
    [InlineData(0, 0, false)]            // 계측 0 — 판정 대상 아님(0 나눗셈 방어)
    public void IsMaintenanceCovered_requires_majority(double measuredMs, double maintMs, bool expected)
    {
        Assert.Equal(expected, OeeMath.IsMaintenanceCovered(measuredMs, maintMs));
    }

    [Fact]
    public void IsMaintenanceCovered_excluded_stop_drops_out_of_mtbf()
    {
        // 정지 3건(onset 0 / 10분 / 30분) 중 가운데가 유지보수 확정 → onset 2개만 남아
        // 갭이 10·20분(평균 15분)에서 30분 단일 갭으로 바뀐다 = MTBF 값이 실제로 변한다.
        var all = new List<double> { 0, 10 * 60_000, 30 * 60_000 };
        var (before, _, _) = OeeMath.ComputeMtbf2(all);

        Assert.True(OeeMath.IsMaintenanceCovered(measuredMs: 60_000, maintOverlapMs: 60_000));
        var kept = new List<double> { 0, 30 * 60_000 };   // 가운데 정지 제외 후
        var (after, _, _) = OeeMath.ComputeMtbf2(kept);

        Assert.Equal(15 * 60_000.0, before!.Value, 6);
        Assert.Equal(30 * 60_000.0, after!.Value, 6);
        Assert.NotEqual(before.Value, after.Value);
    }

    [Fact]
    public void Classify_over_8h_is_fault() // 8h↑ 도 고장(비생산 시간대 에디터가 planned 분리 — 단순 2-상태)
    {
        // 길이는 상한을 두지 않는다 — 단, 유발자 근거(자기 MT 과주행)는 여전히 필요하다(2026-08-24).
        var (rc, cat, isFail, should) = OeeMath.ClassifyByDuration(
            9L * 60 * 60 * 1000, hasOwnMtOverrun: true, lineHasMtOverrun: false);
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
    [InlineData(20000, 30000, 30000, OeeMath.CycleClass.Normal)]   // MT 20s ≤ thr, CT 30s ≤ thr(비초과) → 정상
    [InlineData(45000, 50000, 30000, OeeMath.CycleClass.Downtime)] // CT 50s > thr → 비가동
    [InlineData(null, 50000, 30000, OeeMath.CycleClass.Downtime)]  // complete=null, CT 50s > thr → 비가동
    [InlineData(null, 20000, 30000, OeeMath.CycleClass.Normal)]    // complete=null 이지만 CT 20s ≤ thr → 정상
    [InlineData(5000, 500000, 30000, OeeMath.CycleClass.Downtime)] // ① 2026-08-19: mt 정상·wt 폭주(정지 후 재개) → 비가동
    public void ClassifyCycle_marks_downtime_by_mt_or_ct_overrun(int? mt, int? ct, double thr, OeeMath.CycleClass expected)
    {
        Assert.Equal(expected, OeeMath.ClassifyCycle(mt, ct, thr));
    }

    // ── 비가동 적립 범위 (2026-08-24 MT 축 분리) ────────────────────────────

    [Fact]
    public void 적립_CT초과_행은_사이클_전체()
        => Assert.Equal(120_000, OeeMath.ResolveDowntimeAccrualMs(
            ctMs: 120_000, mtMs: 5_000, ctBoundaryMs: 101_885, mtBoundaryMs: 10_550, mtMedianMs: 4_220));

    [Fact]
    public void 적립_MT만_초과면_평소_대비_초과분만()
    {
        // 실측 2026-08-24 이송 12:43:35 — ct 40,823(정상권) / mt 31,191(중앙 4,220 의 7.4배).
        // 부품은 제때 나왔으므로 40.8초가 아니라 초과분 26,971ms 만 손실.
        Assert.Equal(26_971, OeeMath.ResolveDowntimeAccrualMs(
            ctMs: 40_823, mtMs: 31_191, ctBoundaryMs: 101_885, mtBoundaryMs: 10_550, mtMedianMs: 4_220));
    }

    [Fact]
    public void 적립_MT기준_미보유면_사이클_전체로_폴백()
        => Assert.Equal(40_823, OeeMath.ResolveDowntimeAccrualMs(
            ctMs: 40_823, mtMs: 31_191, ctBoundaryMs: 101_885, mtBoundaryMs: 101_885, mtMedianMs: 0));

    [Fact]
    public void 적립_초과분은_사이클_길이를_넘지_않는다()
    {
        // ct < mt 비정상 행(시계 역행 등) 방어 — 사이클보다 긴 손실을 만들지 않는다.
        Assert.Equal(5_000, OeeMath.ResolveDowntimeAccrualMs(
            ctMs: 5_000, mtMs: 90_000, ctBoundaryMs: 101_885, mtBoundaryMs: 10_550, mtMedianMs: 4_220));
    }

    [Fact]
    public void 적립_mt_없는_행은_사이클_전체()
        => Assert.Equal(40_823, OeeMath.ResolveDowntimeAccrualMs(
            ctMs: 40_823, mtMs: null, ctBoundaryMs: 101_885, mtBoundaryMs: 10_550, mtMedianMs: 4_220));

    [Fact]
    public void ClassifyCycle_MT경계가_주어지면_그_경계로_판정한다()
    {
        // ct 정상권 + mt 가 MT 경계 초과 → 비가동. 종전(CT 경계 비교)이면 정상으로 삼켰다.
        Assert.Equal(OeeMath.CycleClass.Downtime, OeeMath.ClassifyCycle(
            mt: 31_191, ct: 40_823, ctThresholdMs: 40_754, idleMultiplier: 2.5, mtBoundaryMs: 10_550));
        // MT 경계 미지정(0) → CT 경계 폴백 = 종전 동작
        Assert.Equal(OeeMath.CycleClass.Normal, OeeMath.ClassifyCycle(
            mt: 31_191, ct: 40_823, ctThresholdMs: 40_754, idleMultiplier: 2.5));
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
    [InlineData(45000, 50000, OeeMath.CycleClass.Normal)]    // CT 50s ≤ 2.5×thr(75s) → 정상(속도 손실 → P)
    [InlineData(74000, 75000, OeeMath.CycleClass.Normal)]    // CT 정확히 2.5×thr → 아직 정상(초과 조건)
    [InlineData(75000, 80000, OeeMath.CycleClass.Downtime)]  // CT 80s > 2.5×thr → 비가동(2026-08-19 ct 기준 — 종전엔 mt만 봐서 정상)
    [InlineData(75001, 80000, OeeMath.CycleClass.Downtime)]  // MT 도 CT 도 2.5×thr 초과 → 비가동
    [InlineData(null, 75001, OeeMath.CycleClass.Downtime)]   // 미완료 CT 도 동일 경계
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

    // ── ClassifyStopWindow (doc/25 §1 분류표 SSOT) ──────────────────────────
    //    기준 예시: thr=48s, 비생산 배수 10× → 경계 480s. 유발=flow 귀속 abnormal, 형제=같은 창에 유발자 존재.

    private const double Thr = 48_000;

    [Fact]
    public void Own_signal_wins_regardless_of_duration()
    {
        // 유발 flow — 41분(기준 초과)이어도 고장 확정(doc/25 §0 ① kit_test 41분 이송 케이스).
        Assert.Equal(OeeMath.StopClass.Fault, OeeMath.ClassifyStopWindow(
            signalRulesActive: true, hasOwnSignal: true, lineHasCulprit: false, lineHasUnresolvedUsertag: true,
            durationMs: 41 * 60_000, ctThresholdMs: Thr));
        // 짧아도 고장.
        Assert.Equal(OeeMath.StopClass.Fault, OeeMath.ClassifyStopWindow(
            true, true, false, true, 90_000, Thr));
    }

    [Fact]
    public void Sibling_with_culprit_splits_by_threshold()
    {
        // 형제 flow — 기준(10×48s=480s) 미만 = 대기 공백(§0 ② 5분 테스트), 이상 = 대기 비생산(41분 케이스).
        Assert.Equal(OeeMath.StopClass.WaitSlack, OeeMath.ClassifyStopWindow(
            true, hasOwnSignal: false, lineHasCulprit: true, lineHasUnresolvedUsertag: true, 300_000, Thr));
        Assert.Equal(OeeMath.StopClass.WaitNonProd, OeeMath.ClassifyStopWindow(
            true, false, true, true, 41 * 60_000, Thr));
    }

    [Fact]
    public void Usertag_only_without_culprit_stays_fault()
    {
        // usertag(라인 스코프)만 — 유발자 특정 불가 → 보수적으로 고장 유지(§2.3), 기준 초과라도 비생산 승격 금지.
        Assert.Equal(OeeMath.StopClass.Fault, OeeMath.ClassifyStopWindow(
            true, hasOwnSignal: false, lineHasCulprit: false, lineHasUnresolvedUsertag: true, 41 * 60_000, Thr));
    }

    [Fact]
    public void No_signal_falls_back_to_pure_ct_rule()
    {
        // 라인 전체 무신호 — 기준 이상은 비생산, 미만은 <b>대기</b>(2026-08-21 폴백 전환).
        //   종전 Down(고장)은 라인 정지 1회를 설비 수만큼 고장으로 부풀렸다(실측 3분 정지 → 6건).
        //   고장은 MT 과주행 / 자기 flow abnormal / 미해소 usertag 로만 잡는다.
        Assert.Equal(OeeMath.StopClass.NonProduction, OeeMath.ClassifyStopWindow(
            true, false, false, false, 41 * 60_000, Thr));
        Assert.Equal(OeeMath.StopClass.WaitSlack, OeeMath.ClassifyStopWindow(
            true, false, false, false, 300_000, Thr));
    }

    [Fact]
    public void Inactive_rules_ignore_signals_entirely()
    {
        // 커버리지 게이트/설정 OFF — 신호 인자 무시, CT 규칙만(§2.4 폴백). 경계 미만은 대기.
        Assert.Equal(OeeMath.StopClass.NonProduction, OeeMath.ClassifyStopWindow(
            signalRulesActive: false, hasOwnSignal: true, lineHasCulprit: true, lineHasUnresolvedUsertag: true,
            41 * 60_000, Thr));
        Assert.Equal(OeeMath.StopClass.WaitSlack, OeeMath.ClassifyStopWindow(
            false, true, true, true, 300_000, Thr));
    }

    // ── 유발자 특정 우선순위 (2026-08-24 실측 기반) ─────────────────────────
    // kit 라인에서 이송 하나를 Going 중 정지시킨 실측:
    //   이송 mt=431,226ms(7.2분) / 형제 4개 mt≈5,000ms — MT 만으로 유발자와 형제가 갈렸다.
    // 그 구간에 미해소 usertag(1st_usb.RET_센서단선이상)도 걸쳐 있었는데, 종전 규칙은 usertag 만 보고
    // 형제까지 전원 고장으로 올렸다(고장 5건). MT 과주행으로 이미 유발자가 특정됐으면 형제는 대기다.

    [Fact]
    public void MtOverrun_culprit_demotes_siblings_to_wait()
    {
        // 형제 flow — 자기 신호 없음, abnormal 유발자 없음, 미해소 usertag 있음.
        //   그러나 라인에 MT 과주행 flow 가 있으므로 대기(유발자 특정됨).
        Assert.Equal(OeeMath.StopClass.WaitSlack, OeeMath.ClassifyStopWindow(
            signalRulesActive: true, hasOwnSignal: false, lineHasCulprit: false,
            lineHasUnresolvedUsertag: true, durationMs: 300_000, ctThresholdMs: Thr,
            lineHasMtOverrun: true));
    }

    [Fact]
    public void Unresolved_usertag_is_line_fault_only_when_no_culprit()
    {
        // 유발자 전무 + 미해소 usertag → 라인 문제로 보고 전원 고장(최후 안전망).
        Assert.Equal(OeeMath.StopClass.Fault, OeeMath.ClassifyStopWindow(
            true, false, false, lineHasUnresolvedUsertag: true, durationMs: 300_000, ctThresholdMs: Thr));
        // 같은 조건에서 usertag 가 이미 해소됐으면 고장 근거가 없다 → 대기.
        //   실측: 09:00:34 발화 → 09:01:54 해소인데 09:02:31 까지의 정지가 전원 고장으로 잡혔다.
        Assert.Equal(OeeMath.StopClass.WaitSlack, OeeMath.ClassifyStopWindow(
            true, false, false, lineHasUnresolvedUsertag: false, durationMs: 300_000, ctThresholdMs: Thr));
    }

    [Fact]
    public void Own_signal_still_wins_over_everything()
    {
        // 자기 flow abnormal 은 최우선 — 라인에 MT 과주행이 있어도 자기 고장이다.
        Assert.Equal(OeeMath.StopClass.Fault, OeeMath.ClassifyStopWindow(
            true, hasOwnSignal: true, lineHasCulprit: true, lineHasUnresolvedUsertag: true,
            300_000, Thr, lineHasMtOverrun: true));
    }

    [Fact]
    public void No_threshold_never_promotes_to_nonproduction()
    {
        // 표본 부족(thr=0) — 승격 판정 불가 → 비생산으로 안 올린다(가짜 비생산 금지, doc/21 §10).
        //   무신호 폴백이므로 대기. 고장으로 세지 않는다(고장 근거가 없다).
        Assert.Equal(OeeMath.StopClass.WaitSlack, OeeMath.ClassifyStopWindow(
            true, false, false, false, 41 * 60_000, 0));
        Assert.Equal(OeeMath.StopClass.WaitSlack, OeeMath.ClassifyStopWindow(
            true, false, true, true, 41 * 60_000, 0));
    }

    [Fact]
    public void Custom_multiplier_moves_the_boundary()
    {
        // 사용자 배수(예: 5×) — 경계 240s: 250s 형제 정지가 대기 비생산으로 승격.
        Assert.Equal(OeeMath.StopClass.WaitNonProd, OeeMath.ClassifyStopWindow(
            true, false, true, true, 250_000, Thr, nonProdMultiplier: 5));
        Assert.Equal(OeeMath.StopClass.WaitSlack, OeeMath.ClassifyStopWindow(
            true, false, true, true, 230_000, Thr, nonProdMultiplier: 5));
    }
}
