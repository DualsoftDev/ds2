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

    // ── 두 규칙 모델 기본값·경계 (doc/28, 2026-09-11) ─────────────────────────────────

    [Fact]
    public void NonProd_multiplier_default_is_thirty()
        => Assert.Equal(30.0, OeeMath.NonProductionWtMultiplier);

    [Fact]
    public void Fault_multiplier_default_is_five()
        => Assert.Equal(5.0, OeeMath.FaultMtMultiplierDefault);   // doc/28 §3: 2.5 → 5.0(신규 설치만, 저장값 불변)

    [Fact]
    public void Baseline_sample_gate_is_ten_and_majority_is_half()
    {
        Assert.Equal(10, OeeMath.MinBaselineSamples);
        Assert.Equal(0.5, OeeMath.MajorityCoverRatio);
    }

    // ── 고장 경계 하한 (2026-08-30) — 초저 MT flow 지터가 고장이 되지 않게 1초 하한. 기준선 0 = 비활성(0).

    [Theory]
    [InlineData(22, 2.5, 1000)]       // 지터 수준 중앙값(22ms×2.5=55ms) → 하한 1s 로 승격
    [InlineData(300, 2.5, 1000)]      // 300ms×2.5=750ms → 여전히 하한 미만 → 1s
    [InlineData(400, 2.5, 1000)]      // 정확히 하한(400×2.5=1000)
    [InlineData(5000, 2.5, 12500)]    // 정상 flow(중앙값 5s) → 12.5s
    [InlineData(146_000, 10.0, 1_460_000)] // 현장 3000 #131: 중앙 MT 146s × 10 = 24.3분
    [InlineData(0, 2.5, 0)]           // 중앙값 0 = 기준선 미보유 → 0(비활성). 종전 1000 은 모든 완료 행을 고장으로 만들었다
    public void ResolveMtFaultBoundary_applies_floor_or_disables(double medianMs, double mult, double expected)
        => Assert.Equal(expected, OeeMath.ResolveMtFaultBoundaryMs(medianMs, mult));

    [Theory]
    [InlineData(180_000, 5.0, 900_000)]   // 셔틀형(WT 87%) 중앙 CT 180s × 5 = 15분 — 불인정 행은 CT 축에 댄다
    [InlineData(200, 2.5, 1000)]          // 하한 1s
    [InlineData(0, 5.0, 0)]               // 기준선 없음 → 비활성
    public void ResolveCtFaultBoundary_uses_median_ct(double medianCt, double mult, double expected)
        => Assert.Equal(expected, OeeMath.ResolveCtFaultBoundaryMs(medianCt, mult));

    // ── 비생산 경계 — 완료 행은 중앙 WT × 배수(하한 중앙 CT × 10), 불인정 행은 중앙 CT × 배수(같은 하한) ──

    [Theory]
    [InlineData(103_000, 238_000, 30.0, 3_090_000)]   // #121: 103s × 30 = 51.5분 (하한 10사이클 39.7분 미발동)
    [InlineData(600, 6_500, 30.0, 65_000)]            // Turn Zone: 18s → 하한 10사이클(65s)
    [InlineData(0, 6_500, 30.0, 65_000)]              // 중앙 WT 0(항상 즉시 재시작) — 정상 기준선, 경계는 하한이 맡는다
    [InlineData(14_000, 180_000, 30.0, 1_800_000)]    // 현장 3000 #13x: 14s × 30 = 7분 < 하한 10사이클 30분 → 30분
    [InlineData(1_000, 0, 30.0, 0)]                   // 기준선 미보유 → 0
    public void ResolveWtNonProdBoundary_applies_multiplier_and_ten_cycle_floor(double medWt, double medCt, double mult, double expected)
        => Assert.Equal(expected, OeeMath.ResolveWtNonProdBoundaryMs(medWt, medCt, mult));

    [Theory]
    [InlineData(180_000, 30.0, 5_400_000)]   // 중앙 CT 180s × 30 = 90분 — '확인 필요' 임계이기도 하다
    [InlineData(180_000, 5.0, 1_800_000)]    // 배수 5 < 하한 10사이클 → 30분
    [InlineData(0, 30.0, 0)]
    public void ResolveCtNonProdBoundary_uses_median_ct_with_floor(double medCt, double mult, double expected)
        => Assert.Equal(expected, OeeMath.ResolveCtNonProdBoundaryMs(medCt, mult));

    [Theory]
    [InlineData(5_400_000, 5_400_000, true)]    // 정확히 경계 = 확인 필요
    [InlineData(5_399_999, 5_400_000, false)]
    [InlineData(13_680_000, 5_400_000, true)]   // 9/9 야간 방치 3.8h(현장 3000) → 표시
    [InlineData(2_040_000, 5_400_000, false)]   // 9/10 34분 라인 정지 → 미표시(고장 유지)
    [InlineData(999_999_999, 0, false)]         // 경계 없음 → 표시 안 함
    public void IsReviewPending_flags_long_faults(double ctMs, double ctNp, bool expected)
        => Assert.Equal(expected, OeeMath.IsReviewPending(ctMs, ctNp));

    [Theory]
    [InlineData(514_999, 515_000, false)]
    [InlineData(515_000, 515_000, true)]
    [InlineData(1_000_000, 0, false)]   // 경계 없음 → 판정 불가(가짜 비생산 금지)
    public void IsNonProductionLength_compares_against_boundary(double durMs, double boundary, bool expected)
        => Assert.Equal(expected, OeeMath.IsNonProductionLength(durMs, boundary));

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

    const double Floor = 15_000;   // = StateReconcile tick 5s × 3 (기본 설정)

    // ── 행↔구간 조인 허용치 (doc/28 §2.6) — 저장된 수동 라벨을 재도출된 행에 다시 붙일 때의 과반 규칙 ──
    //   사용자 규칙이 아니다(전환 객체는 행). 경계 스침으로 이웃 행을 뒤집지 않고, 몇 초 어긋나도 라벨이 떨어지지 않는다.

    [Theory]
    [InlineData(63 * 3600_000.0, 54 * 3600_000.0, true)]    // 주말 행(금 17:00~월 08:00) ∩ 토 00:00~월 06:00 라벨 = 86% → 붙음
    [InlineData(30 * 60_000.0, 10 * 60_000.0, false)]       // 월 05:50~06:20 고장 행이 라벨 끝에 10분 걸침(33%) → 안 붙음
    [InlineData(10_000, 5_000, false)]                      // 정확히 절반 = 과반 아님
    [InlineData(10_000, 5_001, true)]
    [InlineData(0, 0, false)]                               // 0 길이 방어
    public void IsMajorityCovered_requires_more_than_half(double rowMs, double overlapMs, bool expected)
        => Assert.Equal(expected, OeeMath.IsMajorityCovered(rowMs, overlapMs));

    [Fact]
    public void Row_cannot_be_majority_covered_by_two_disjoint_labels()
    {
        // 서로 겹치지 않는 두 라벨이 한 행을 나눠 덮으면 둘 다 과반이 될 수 없다 — 이중 분류 불가.
        const double row = 10_000;
        double a = 5_000, b = 5_000;
        Assert.False(OeeMath.IsMajorityCovered(row, a) && OeeMath.IsMajorityCovered(row, b));
        a = 6_000; b = 4_000;
        Assert.True(OeeMath.IsMajorityCovered(row, a));
        Assert.False(OeeMath.IsMajorityCovered(row, b));
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
    //  사이클기반 OEE (doc/22 → doc/26 행 집합 → doc/28 두 규칙 · 사이클 단위)
    // ════════════════════════════════════════════════════════════════════════

    // 현장 3000 #13x 계열 기준선: 중앙 MT 150s, 중앙 WT 20s, 중앙 CT 170s. 고장 5× / 비생산 30×.
    //   완료 행: mtFault 750s, wtNonProd = max(20s×30=600s, 170s×10=1700s) = 1700s
    //   불인정 행: ctFault = 170s×5 = 850s, ctNonProd = max(170s×30=5100s, 1700s) = 5100s
    private const double MtF = 750_000, CtF = 850_000, WtNp = 1_700_000, CtNp = 5_100_000;
    private const int Sample = 100;

    private static OeeMath.CycleClass Cls(int? mt, int? ct, int? wt, int sample = Sample,
        double mtF = MtF, double ctF = CtF, double wtNp = WtNp, double ctNp = CtNp)
        => OeeMath.ClassifyCycle(mt, ct, wt, mtF, ctF, wtNp, ctNp, sample);

    // ── 완료 행: ①-a 동작 초과 = 고장 / ②-a 대기 초과 = 비생산 / 나머지 정상 ──

    [Theory]
    [InlineData(150_000, 170_000, 20_000, OeeMath.CycleClass.Normal)]          // 평소 그대로
    [InlineData(700_000, 720_000, 20_000, OeeMath.CycleClass.Normal)]          // 동작 4.7× — 경계(5×) 아래 = 정상(속도 손실 → P)
    [InlineData(750_001, 770_001, 20_000, OeeMath.CycleClass.Fault)]           // 동작 경계 초과(> 조건) = 고장
    [InlineData(150_000, 1_849_999, 1_699_999, OeeMath.CycleClass.Normal)]     // 대기 1699.999s — 비생산 경계 직전 = 정상(가동, P 손실)
    [InlineData(150_000, 1_850_000, 1_700_000, OeeMath.CycleClass.NonProduction)] // 대기 정확히 경계(≥) = 비생산
    [InlineData(150_000, 60 * 3600_000, 60 * 3600_000 - 150_000, OeeMath.CycleClass.NonProduction)] // 완료 후 주말 방치(wt 60h) = 비생산
    public void ClassifyCycle_completed_row_two_rules(int mt, int ct, int wt, OeeMath.CycleClass expected)
        => Assert.Equal(expected, Cls(mt, ct, wt));

    [Fact]
    public void ClassifyCycle_fault_wins_over_nonprod_when_both_exceed()
        // 동작도 늘어지고 대기도 길면 ①이 먼저 — 고장(고장은 길이 무관 비생산으로 승격하지 않는다).
        => Assert.Equal(OeeMath.CycleClass.Fault, Cls(900_000, 3_000_000, 2_100_000));

    [Fact]
    public void ClassifyCycle_weekend_going_row_stays_fault()
    {
        // 금요일 Going 상태로 세워 두고 월요일 이어서 완료 — mt 60h. doc/28 §1: 고장(승격 없음). 교정은 사용자 전환.
        var mt = 60 * 3600_000;
        Assert.Equal(OeeMath.CycleClass.Fault, Cls(mt, mt + 20_000, 20_000));
        // 사용자가 봐야 할 행 — 길이가 비생산 경계(CT) 이상이면 '확인 필요'.
        Assert.True(OeeMath.IsReviewPending(mt + 20_000, CtNp));
    }

    [Fact]
    public void ClassifyCycle_missing_wt_uses_ct_minus_mt()
        // wt 컬럼이 비어도 ct − mt(행 단위 항등)로 대기를 만든다.
        => Assert.Equal(OeeMath.CycleClass.NonProduction, Cls(150_000, 1_850_000, null));

    // ── 불인정 행(mt NULL, ct = 시작~다음 시작): CT 축 하나 — ①-b ct > 중앙 CT × 고장배수 / ②-b ct ≥ 중앙 CT × 비생산배수 ──

    [Theory]
    [InlineData(170_000, OeeMath.CycleClass.Normal)]         // 정상 길이인데 tail 누락 = 정상
    [InlineData(850_000, OeeMath.CycleClass.Normal)]         // 정확히 경계(> 조건) = 정상
    [InlineData(850_001, OeeMath.CycleClass.Fault)]          // 5× 초과 = 고장
    [InlineData(60 * 3600_000, OeeMath.CycleClass.Fault)]    // 소재 빼고 월요일 새로 시작(주말 60h) = 고장(비생산 경계도 넘지만 ①이 먼저)
    public void ClassifyCycle_incomplete_row_uses_ct_axis(int ct, OeeMath.CycleClass expected)
        => Assert.Equal(expected, Cls(null, ct, null));

    [Fact]
    public void ClassifyCycle_incomplete_row_nonprod_only_when_fault_clause_is_above_it()
    {
        // 고장배수 20 > 비생산배수 5 인 설정: ctFault 3400s, ctNp = max(170×5, 170×10) = 1700s → 1700~3400s 구간은 비생산.
        Assert.Equal(OeeMath.CycleClass.NonProduction, Cls(null, 2_000_000, null, ctF: 3_400_000, ctNp: 1_700_000));
        Assert.Equal(OeeMath.CycleClass.Fault, Cls(null, 3_400_001, null, ctF: 3_400_000, ctNp: 1_700_000));
    }

    [Fact]
    public void Shuttle_type_flow_incomplete_normal_length_row_is_not_fault()
    {
        // 현장 3000 셔틀: WT 가 CT 의 87% (중앙 MT 24s, 중앙 CT 180s). 초판(doc/28 9/10)은 불인정 행을 중앙 MT 경계(24s×5=120s)에
        // 대어 정상 길이 180s 행이 tail 누락 한 번에 고장이 됐다. CT 축(180s×5=900s)이면 정상.
        var mtFault = OeeMath.ResolveMtFaultBoundaryMs(24_000, 5.0);      // 120s — 완료 행 전용
        var ctFault = OeeMath.ResolveCtFaultBoundaryMs(180_000, 5.0);     // 900s — 불인정 행 전용
        var wtNp = OeeMath.ResolveWtNonProdBoundaryMs(156_000, 180_000, 30.0);
        var ctNp = OeeMath.ResolveCtNonProdBoundaryMs(180_000, 30.0);
        Assert.Equal(OeeMath.CycleClass.Normal, OeeMath.ClassifyCycle(null, 180_000, null, mtFault, ctFault, wtNp, ctNp, Sample));
        Assert.Equal(OeeMath.CycleClass.Fault, OeeMath.ClassifyCycle(null, 900_001, null, mtFault, ctFault, wtNp, ctNp, Sample));
        // 완료 행은 여전히 MT 축 — 동작 121s 는 고장(셔틀 동작이 5배 늘어진 것).
        Assert.Equal(OeeMath.CycleClass.Fault, OeeMath.ClassifyCycle(120_001, 300_000, 179_999, mtFault, ctFault, wtNp, ctNp, Sample));
    }

    // ── 게이트·비활성 절 ──

    [Fact]
    public void ClassifyCycle_sample_gate_marks_everything_normal()
    {
        // 14일 완료 사이클 10건 미만 → 기준선은 있어도 판정 보류(전부 정상). 10건부터 판정.
        Assert.Equal(OeeMath.CycleClass.Normal, Cls(900_000, 950_000, 50_000, sample: 9));
        Assert.Equal(OeeMath.CycleClass.Normal, Cls(null, 60 * 3600_000, null, sample: 9));
        Assert.Equal(OeeMath.CycleClass.Fault, Cls(900_000, 950_000, 50_000, sample: 10));
    }

    [Fact]
    public void ClassifyCycle_flow_without_mt_baseline_cannot_judge_fault()
    {
        // tail 미정의 flow: mt·wt 항상 NULL, MT 기준선 없음 → 고장 절 비활성(0). 비생산(CT 축)만 가능.
        Assert.Equal(OeeMath.CycleClass.Normal, Cls(null, 4_000_000, null, mtF: 0, ctF: 0));
        Assert.Equal(OeeMath.CycleClass.NonProduction, Cls(null, 5_100_000, null, mtF: 0, ctF: 0));
        // 완료 행이 있어도 MT 경계 0 = 판정 비활성(0 을 경계로 쓰면 전 행이 고장이 되는 함정 방지).
        Assert.Equal(OeeMath.CycleClass.Normal, Cls(900_000, 950_000, 50_000, mtF: 0, ctF: 0));
    }

    [Fact]
    public void ClassifyCycle_open_cycle_without_ct_is_ignored()
    {
        Assert.Equal(OeeMath.CycleClass.Ignore, Cls(5000, null, null));
        Assert.Equal(OeeMath.CycleClass.Ignore, Cls(5000, 0, 0));
    }

    [Fact]
    public void ClassifyCycle_all_boundaries_zero_is_normal()
        // 기준선 전무 → 판정 불가 → Normal(상위에서 산출 게이트). 가짜 고장/비생산 분류 금지.
        => Assert.Equal(OeeMath.CycleClass.Normal, Cls(999_999, 999_999, 1, mtF: 0, ctF: 0, wtNp: 0, ctNp: 0));

    // ── 설정 정규화 — 슬라이더 2개(고장·비생산). 정지 배수·신호 판별 설정은 폐기 ──

    [Fact]
    public void Multiplier_settings_defaults_and_clamps()
    {
        var s = new DSPilot.Models.OeeManualSettings();
        Assert.Equal(OeeMath.NonProductionWtMultiplier, s.ResolveNonProdWtMultiplier());
        Assert.Equal(OeeMath.FaultMtMultiplierDefault, s.ResolveFaultMtMultiplier());

        s.NonProdWtMultiplier = double.NaN; s.FaultMtMultiplier = 99;
        Assert.Equal(OeeMath.NonProductionWtMultiplier, s.ResolveNonProdWtMultiplier());
        Assert.Equal(DSPilot.Models.OeeManualSettings.FaultMultMax, s.ResolveFaultMtMultiplier());   // 상한 20(현장 3000 의 10 보존)
        Assert.Equal(20.0, DSPilot.Models.OeeManualSettings.FaultMultMax);

        s.NonProdWtMultiplier = 1000;
        Assert.Equal(DSPilot.Models.OeeManualSettings.NonProdMultMax, s.ResolveNonProdWtMultiplier());
    }

    [Fact]
    public void Old_multiplier_keys_are_not_migrated_and_fault_is_preserved()
    {
        // 구 Production.json 의 IdleWtMultiplier(정지 배수)·IdleCtMultiplier/NonProdCtMultiplier(CT 축)·SignalClassifyEnabled 는
        // 새 모델에 뜻이 없어 이관하지 않는다(ExtensionData 로 무해 보존). 고장 배수 10(현장 3000)은 그대로 읽힌다.
        var json = "{\"IdleWtMultiplier\":5,\"IdleCtMultiplier\":5,\"NonProdCtMultiplier\":24.5,\"SignalClassifyEnabled\":true,\"FaultMtMultiplier\":10}";
        var s = System.Text.Json.JsonSerializer.Deserialize<DSPilot.Models.OeeManualSettings>(json)!;
        Assert.Equal(OeeMath.NonProductionWtMultiplier, s.ResolveNonProdWtMultiplier());
        Assert.Equal(10.0, s.ResolveFaultMtMultiplier());
        Assert.NotNull(s.ExtensionData);
        Assert.True(s.ExtensionData!.ContainsKey("IdleWtMultiplier"));
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

    // ── doc/28 §2.1 — onset = 고장 행 시작, 고장 사이클 평균 시간 = 고장 행 ct 평균(행 전체) ──────────

    [Fact]
    public void Fault_cycle_mean_uses_whole_row_length_not_excess()
    {
        // 현장 3000 #131 9/9 10:45 행: ct 27.8분(mt 27.3 + wt 0.5). 초과분(24.9분)이 아니라 행 전체를 평균한다 — 행 안에서
        // 고장이 언제 시작됐는지 모르므로 행이 단위(doc/28 §7-C). ⓘ 라벨은 '고장 사이클 평균 시간'.
        var (mean, note) = OeeMath.ComputeMttr(new List<double> { 27.8 * 60_000, 22.7 * 60_000 });
        Assert.Equal(25.25 * 60_000, mean!.Value, 6);
        Assert.Contains("사이클", note);
    }

    [Fact]
    public void Mtbf_onsets_are_row_starts()
    {
        // 두 고장 행이 09:00 시작(30분)·10:00 시작(20분)이면 onset 간격은 시작 차이 60분 — 초과분 위치와 무관.
        var onsets = new List<double> { 9 * 3600_000.0, 10 * 3600_000.0 };
        var (mtbf, _, _) = OeeMath.ComputeMtbf2(onsets);
        Assert.Equal(3600_000.0, mtbf!.Value, 6);
    }

    [Fact]
    public void Performance_note_names_median_ct_standard()
    {
        var (p, note) = OeeMath.ComputeCyclePerformance(10, 170_000, 1_800_000);
        Assert.NotNull(p);
        Assert.Contains("중앙", note);
        var (_, note0) = OeeMath.ComputeCyclePerformance(10, null, 1_800_000);
        Assert.Contains("중앙", note0);
    }

    [Fact]
    public void Wall_clock_availability_is_run_over_available()
    {
        // A = 가동 ÷ 생산가능. 비가동 = 생산가능 − 가동 = 고장 + 유지보수 (+ 미귀속 0). 고장 행 전체가 손실이라 라벨과 어긋나지 않는다.
        var (a, note) = OeeMath.ComputeWallClockAvailability(runWallMs: 90 * 60_000, availableWallMs: 120 * 60_000);
        Assert.Equal(0.75, a!.Value, 10);
        Assert.Contains("고장", note);
        var (a0, note0) = OeeMath.ComputeWallClockAvailability(0, 0);
        Assert.Null(a0);
        Assert.Contains("산출 불가", note0);
    }
}
